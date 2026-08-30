using DocumentService.Domain;
using DocumentService.Features.ObsidianSync.Delete;
using DocumentService.Features.ObsidianSync.Manifest;
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

        return app;
    }

    // [[IADR-0270]] 決定 3: Bearer 同期トークン → ハッシュ照合 → 有効（未失効・期限内）な端末。
    // 欠落・不正・期限切れ・失効はいずれも null（呼び出し側で同じ 401 になる）。
    internal static async Task<SyncDevice?> ResolveDeviceAsync(HttpContext http,
        DocumentDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = auth["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = SyncTokens.HashOf(token);
        var device = await db.SyncDevices.FirstOrDefaultAsync(d => d.TokenHash == hash, ct);
        return device is not null && device.IsActive(now) ? device : null;
    }

    internal static async Task<PrivateNote?> FindOwnedAsync(DocumentDbContext db, string owner,
        Guid id, CancellationToken ct)
    {
        var note = await db.PrivateNotes.FindAsync([id], ct);
        return note is not null && note.OwnerId == owner ? note : null;
    }
}
