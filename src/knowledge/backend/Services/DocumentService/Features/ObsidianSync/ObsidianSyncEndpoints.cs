using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.ObsidianSync.Delete;
using DocumentService.Features.ObsidianSync.Manifest;
using DocumentService.Features.ObsidianSync.Move;
using DocumentService.Features.ObsidianSync.Pull;
using DocumentService.Features.ObsidianSync.Push;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.ObsidianSync;

// FR-20, UC-11, ADR-0037 決定 2〜5・7〜9・14, ADR-0046 D-03, 08_data-egress-policy 例外規定,
// [[IADR-0270]] 決定 3・5・7: Obsidian プラグイン向けの双方向同期プロトコルの合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/ObsidianSync/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— route group（同期トークンの
// 経路であることの宣言）、端末の解決、所有者スコープの照会。
//
// **認証はブラウザセッション（JWT）ではなく同期トークン（Bearer）である**（ADR-0037 課題 2:
// 別系統の資格情報）。検証失敗は欠落・不正・期限切れ・失効のいずれも**同じ 401**で返し、
// 理由と存在を漏らさない（deny-by-default）。
//
// **スコープはトークンの所有者の個人資料のみ**（FR-20 / egress 例外の許容条件 1）。
// 他者の資料（共有されたものを含む）・組織文書は、どの端点からも到達できない ——
// すべての照会が台帳（PrivateNote.OwnerId == 所有者）を通るため、構造的に閉じている。
//
// **KB が唯一の正である**（決定 14）。競合（baseVersion 不一致）は 409 で返し、サーバは
// 自動解決しない（決定 7。「ローカル採用／サーバ採用／両方残す」の選択はプラグインが利用者へ提示する）。
public static class ObsidianSyncEndpoints
{
    public static IEndpointRouteBuilder MapObsidianSyncEndpoints(this IEndpointRouteBuilder app)
    {
        // JWT の RequireAuthorization は付けない（同期トークンが本経路の資格情報である）。
        var g = app.MapGroup("/private-notes/sync").WithTags("PrivateNotesSync");

        SyncManifestEndpoint.Map(g);
        PushNoteEndpoint.Map(g);
        PullNoteEndpoint.Map(g);
        DeleteNoteEndpoint.Map(g);
        MoveNoteEndpoint.Map(g);

        return app;
    }

    // [[IADR-0270]] 決定 3: Bearer 同期トークン → ハッシュ照合 → 有効（未失効・期限内）な端末。
    // 欠落・不正・期限切れ・失効はいずれも null（呼び出し側で同じ 401 になる）。
    //
    // FR-20, SC-17, NFR-14, 計画 ADR-0114 決定 1・2, [[IADR-0474]] (#1532):
    // **所有者のアカウントが有効であることも確かめる。** 無効化・名簿に居ない・判定できない
    // （名簿を読めない・時間切れ・口が未構成）はいずれも null ＝ 同じ 401（fail-closed）。
    // 🔴 **名簿を引くのは端末が有効と確定した後だけ** —— トークンの無い・不正な要求で
    //   認可サービスへの往復を生ませない（無資格の呼び出しが名簿の負荷を操れない）。
    // 🔴 **口は要求のサービスから引く**（端点の引数にしない）—— 同期トークンの検証は
    //   ここ 1 か所であり、新しい端点が引数を書き忘れても門を素通りできない形にする。
    internal static async Task<SyncDevice?> ResolveDeviceAsync(HttpContext http,
        DocumentDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = auth["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = SyncTokens.HashOf(token);
        var device = await db.SyncDevices.FirstOrDefaultAsync(d => d.TokenHash == hash, ct);
        if (device is null || !device.IsActive(now)) return null;

        var accounts = http.RequestServices.GetRequiredService<IOwnerAccountDirectory>();
        var state = await accounts.GetStateAsync(device.OwnerId, ct);
        return state == OwnerAccountState.Enabled ? device : null;
    }

    internal static async Task<PrivateNote?> FindOwnedAsync(DocumentDbContext db, string owner,
        Guid id, CancellationToken ct)
    {
        var note = await db.PrivateNotes.FindAsync([id], ct);
        return note is not null && note.OwnerId == owner ? note : null;
    }
}
