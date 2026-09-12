using DocumentService.Domain;
using DocumentService.Features.PrivateNotes;
using DocumentService.Features.SyncSettings.Get;
using DocumentService.Features.SyncSettings.Update;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.SyncSettings;

// FR-20, UC-11, SC-20 主要素 3, ADR-0037 決定 3・4, #1442: 同期対象範囲（本人の同期設定）の合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/SyncSettings/<操作>/` に居る。
// **ここに残るのは 2 操作が共有するもの**だけである —— route group と、設定 → DTO の投影。
//
// **認証は必須・ロールは要求しない**（`/private-notes` 群と同じ。SC-20 は全利用者が使う）。
// **主体はトークンからしか採らない**（`PrivateNoteEndpoints.SubjectOf`）——
// 設定は利用者ごとに 1 行であり、他人の設定へ到達する口は存在しない（ID を受け取らない）。
//
// 🔴 **名前空間とドメイン型が同名である。** 本ファイル群では `SyncSettings` と書くと
// この名前空間を指すため、エンティティは `Domain.SyncSettings` と修飾する。
public static class SyncSettingsEndpoints
{
    public static IEndpointRouteBuilder MapSyncSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/private-notes/sync-settings").WithTags("PrivateNotes")
            .RequireAuthorization();

        GetSyncSettingsEndpoint.Map(g);
        UpdateSyncSettingsEndpoint.Map(g);

        return app;
    }

    // SC-20 主要素 3: 設定 → 応答。フォルダごとに**配下の資料数**と**最終同期日時**を添える
    // （画面が「このフォルダを外すと何件が同期対象から外れるか」を示せるようにする）。
    //
    // 🔴 **配下の判定は 1 つの述語（`PrivateNoteEnrichment.IsUnderTargetFolders`）を使う** ——
    // 一覧の `syncState` と別の判定を書くと、「対象と出ているのに数に入らない」が起きる。
    internal static SyncSettingsDto ToDto(Domain.SyncSettings? settings,
        IReadOnlyList<PrivateNote> activeNotes)
    {
        var folders = settings?.TargetFolders ?? [];
        return new SyncSettingsDto(
            [.. folders.Select(f =>
            {
                var under = activeNotes
                    .Where(n => PrivateNoteEnrichment.IsUnderTargetFolders(n.VaultPath, [f]))
                    .ToList();
                return new SyncTargetFolderDto(f, under.Count,
                    under.Count == 0 ? null : under.Max(n => n.UpdatedAt));
            })],
            settings?.UpdatedAt);
    }

    // SC-20 主要素 3: 配下の資料数の母集合は**削除済みを除く本人の資料**である
    // （削除済みは同期対象にならない＝`syncState` が常に `excluded`。数え方を 2 つ持たない）。
    internal static Task<List<PrivateNote>> ActiveNotesAsync(DocumentDbContext db, string owner,
        CancellationToken ct)
        => db.PrivateNotes.Where(n => n.OwnerId == owner && n.DeletedAt == null).ToListAsync(ct);
}
