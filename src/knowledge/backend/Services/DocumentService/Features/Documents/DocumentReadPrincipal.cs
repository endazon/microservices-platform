using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace DocumentService.Features.Documents;

// FR-19, NFR-09, 計画 ADR-0119 決定 3, ADR-0109 決定 3, ADR-0086 決定 1, ADR-0034 決定 9 (#1614):
// **読み取りの判定の主体。** ADR-0119 決定 3 が挙げる 3 種のいずれかである。
//
//   | 経路                               | 主体                     |
//   | REST（エッジが中継した利用者の資格情報） | その利用者               |
//   | gRPC（本文で運ばれた利用者文脈）        | その利用者               |
//   | REST / gRPC（機械クライアント自身）     | そのサービスアカウント   |
//
// 🔴 **人か機械かの判定は `MachinePrincipal.IsMachine` ただ 1 つ**（トークンが名乗る形だけで決める）。
// 述語を新設しない —— 2 本目を書くと片方だけが直る。
//
// 🔴 **個人資料を読み得るのは「名前の分かる人」だけである**（`PrivateNoteSubject`）。
// 機械は所有者・共有先を名乗っても読まない（ADR-0034 決定 9）。名前の無い人（`preferred_username` も
// クライアント識別も無い）も読まない —— 主体が決まらないものを誰かと読み替えない。
public sealed record DocumentReadPrincipal
{
    private DocumentReadPrincipal(string? userId, bool isMachine)
    {
        UserId = userId;
        IsMachine = isMachine;
    }

    /// <summary>利用者識別子（`preferred_username`）。機械・名前の無い主体は null。</summary>
    public string? UserId { get; }

    /// <summary>機械の主体（サービスアカウント・利用者文脈を運ばない呼び出し元サービス）か。</summary>
    public bool IsMachine { get; }

    /// <summary>個人資料の所有者・共有先として照合してよい利用者名。機械・名前の無い主体は null。</summary>
    public string? PrivateNoteSubject => IsMachine ? null : UserId;

    // REST: 呼び出し元の資格情報そのもの（利用者の中継、または機械クライアント自身）。
    public static DocumentReadPrincipal FromUser(ClaimsPrincipal user)
    {
        if (MachinePrincipal.IsMachine(user)) return new(null, isMachine: true);
        var name = user.Identity?.Name;
        return new(string.IsNullOrWhiteSpace(name) ? null : name, isMachine: false);
    }

    // gRPC: 利用者文脈を運ばない呼び出し ＝ 呼び出し元サービス自身。
    public static DocumentReadPrincipal CallingService() => new(null, isMachine: true);

    // gRPC: 本文で運ばれた利用者文脈（ADR-0086 決定 1）。呼び出し元は `ServiceCaller` を通り、
    // ［2026-09-27 追記 / #1628］かつ許可集合の中継者（`DocumentReadRelayOptions`。既定 `bff`）であることを確かめ済み。
    // 🔴 `service-account-` の利用者名は機械として扱う（BFF の呼び出し元が機械だった等。REST と同じ規約）。
    public static DocumentReadPrincipal RelayedUser(string userId)
        => userId.StartsWith(MachinePrincipal.ServiceAccountUsernamePrefix, StringComparison.OrdinalIgnoreCase)
            ? new(null, isMachine: true)
            : new(userId, isMachine: false);
}
