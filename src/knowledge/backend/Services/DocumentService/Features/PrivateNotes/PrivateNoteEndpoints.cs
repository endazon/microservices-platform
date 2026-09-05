using DocumentService.Domain;
using DocumentService.Features.PrivateNotes.Create;
using DocumentService.Features.PrivateNotes.GetQuota;
using DocumentService.Features.PrivateNotes.List;
using DocumentService.Features.PrivateNotes.Purge;
using DocumentService.Features.PrivateNotes.Restore;
using DocumentService.Features.PrivateNotes.SetExposure;
using DocumentService.Features.PrivateNotes.SetQuota;
using DocumentService.Features.PrivateNotes.SoftDelete;
using DocumentService.Infrastructure.Persistence;
// FR-19, #451-a: 応答・要求の形は `Knowledge.Contracts/Dtos/PrivateNoteDto.cs` が持つ。
// **BFF（別ユニット）が同じ形を配るため、定義を 2 つ持たない**（タグ辞書と同じ切り分け。
// サービス内に写しを置くと契約検査が片方しか見ず、静かに割れる）。
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.PrivateNotes;

// FR-19, UC-11, SC-19, ADR-0037 決定 5・16・17・19・20, ADR-0054 決定 4, [[IADR-0270]]:
// 個人資料（private-note）のライフサイクル端点の合成点。一覧＋容量表示・作成・論理削除・復元・
// 完全削除（単票／一括）・露出 3 トグル・管理者による上限変更。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/PrivateNotes/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— 2 つの route group、
// 主体の取り出し、所有者スコープの照会、既定属性、DTO 変換、容量・経路衝突の応答。
// **`ObsidianSync` 集約も一部を呼ぶ**（作成の既定・容量超過・経路衝突は同期経路と同じ規則である）。
//
// **主体はトークンからしか採らない**（クエリ・本文に主体の口を作らない）。
// 所有者スコープは台帳（PrivateNote.OwnerId）で判定し、他者の資料は**存在ごと秘匿**する
// （404。403 を返すと他人の資料 ID の実在が漏れる。ADR-0036 D-04 の存在秘匿と同じ向き）。
//
// **［#1184］本経路は「露出 3 トグルのうち 1 つでも ON」のときだけ DocumentUpdated を発行する**
// （ADR-0061 決定 1・2 / [[IADR-0395]] 決定 4。[[IADR-0270]] 決定 5「発行しない」の後継）。
// **作成は必ず 3 つとも OFF である**（下の `PrivateNoteDefaults`）ため作成では発行せず、
// 露出 OFF の資料は索引に存在しないまま保たれる —— 既定を構造で守る性質は門の形で残る。
// 完全削除は従来どおり DocumentDeleted を発行する（下流掃除の向き）。
public static class PrivateNoteEndpoints
{
    public static IEndpointRouteBuilder MapPrivateNoteEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-19: 一般利用者の操作。認証は必須・ロールは要求しない（管理者限定にすると所有者が使えない）。
        var g = app.MapGroup("/private-notes").WithTags("PrivateNotes").RequireAuthorization();

        // SC-19: 管理者の上限変更（FR-19「管理者が最大 1 TB まで引き上げられる」）。
        var admin = app.MapGroup("/private-notes/quotas").WithTags("PrivateNotes")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        ListPrivateNotesEndpoint.Map(g);
        CreatePrivateNoteEndpoint.Map(g);
        SoftDeletePrivateNoteEndpoint.Map(g);
        RestorePrivateNoteEndpoint.Map(g);
        PurgePrivateNotesEndpoint.Map(g);
        SetPrivateNoteExposureEndpoint.Map(g);

        GetPrivateNoteQuotaEndpoint.Map(admin);
        SetPrivateNoteQuotaEndpoint.Map(admin);

        return app;
    }

    // FR-19 受け入れ基準 / FR-21 受け入れ基準 ⑩: 新規作成時の既定値。
    // doc_scope=private-note（ADR-0054）・owner=本人（ADR-0036 D-05）・
    // 機密区分 restricted（07_abac-attribute-model のフェイルセーフ既定）。
    //
    // **［#447 → #1184］露出 3 トグルの投影をすべて `excluded` で明示する**
    // （[[IADR-0283]] 決定 4 / ADR-0061 決定 3 / [[IADR-0395]] 決定 1）——
    // ⑩「新規に登録した個人資料は 3 トグルがすべて OFF」を、**値の不在ではなく明示された OFF**
    // として持つ。不在に頼ると、`DocumentExposure` の fail-closed 分岐が失われたときに
    // 静かに全件許可へ倒れる（多層防御。IADR-0044 と同じ向き）。
    //
    // **［#1184］「横断検索に含める」「ナレッジグラフに表示」も写すようになった。**
    // 従前は「索引に載せない」ことで構造的に守られていたため置いていなかったが、
    // ADR-0061 決定 1 が ON の資料を索引へ載せると裁定した以上、**索引の側から
    // 3 軸すべてを読めなければならない**（決定 5・6：`confidentiality` だけで判定しない）。
    internal static Dictionary<string, string> PrivateNoteDefaults(string owner)
    {
        var attributes = new Dictionary<string, string>
        {
            [DocumentAttributes.DocScopeKey] = DocumentAttributes.DocScopePrivateNote,
            [DocumentBodyIntake.OwnerKey] = owner,
            [DocumentAttributes.ConfidentialityKey] = "restricted",
        };

        foreach (var (key, value) in DocumentExposure.Project(false, false, false))
            attributes[key] = value;

        return attributes;
    }

    internal static string? SubjectOf(HttpContext http)
        => string.IsNullOrWhiteSpace(http.User.Identity?.Name) ? null : http.User.Identity!.Name;

    internal static async Task<PrivateNote?> FindOwnedAsync(DocumentDbContext db, string owner,
        Guid id, CancellationToken ct)
    {
        var note = await db.PrivateNotes.FindAsync([id], ct);
        return note is not null && note.OwnerId == owner ? note : null;
    }

    internal static async Task<bool> ActivePathExistsAsync(DocumentDbContext db, string owner,
        string vaultPath, CancellationToken ct)
        => await db.PrivateNotes.AnyAsync(
            n => n.OwnerId == owner && n.VaultPath == vaultPath && n.DeletedAt == null, ct);

    internal static PrivateNoteDto ToDto(PrivateNote n, Document? doc) => new(
        n.DocumentId,
        doc?.Title ?? string.Empty,
        n.VaultPath,
        doc?.Version ?? 0,
        n.LatestBytes,
        n.ContentHash,
        n.IncludeInSearch,
        n.IncludeInGraph,
        n.IncludeInAi,
        n.IsDeleted,
        n.DeletedAt,
        n.PurgeAt,
        n.CreatedAt,
        n.UpdatedAt);

    // ADR-0037 決定 17: 100% 到達時の新規作成拒否。507 Insufficient Storage（WebDAV 由来の
    // 容量超過の標準コード）。**更新はこの拒否を通らない**ことが決定の要である。
    internal static IResult QuotaExceededProblem(long usedBytes, long limitBytes) => Results.Problem(
        title: "保存容量の上限に達しています。",
        detail: $"使用量 {usedBytes} バイト / 上限 {limitBytes} バイト。新規作成はできません。"
              + "削除済み資料の完全削除で容量を空けるか、管理者に上限の引き上げを依頼してください"
              + "（論理削除では容量は空きません）。",
        statusCode: StatusCodes.Status507InsufficientStorage);

    internal static IResult PathConflictProblem(string vaultPath) => Results.Conflict(new
    {
        error = "vault_path_conflict",
        vaultPath,
    });
}

// FR-19, #451-a: 一覧・作成・完全削除・露出トグルの形は `Knowledge.Contracts.Dtos` にある
// （`PrivateNoteDto` / `PrivateNoteUsageDto` / `PrivateNoteListResponse` /
// `CreatePrivateNoteRequest` / `PurgePrivateNotes{Request,Response}` / `UpdateExposureRequest` /
// `PrivateNoteDeletedResponse`）。**BFF が同じ形を画面へ配るため、定義はそちら 1 つだけである。**
//
// 上限変更（管理者）だけは本サービスに残る —— **BFF に口を持たない**（計画に載せる画面が無い）ため、
// 契約として共有する相手が居ない。形は `SetQuota/Command.cs` にある（1 操作専用の入力）。
