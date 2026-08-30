using DocumentService.Features.Documents.GrantShare;
using DocumentService.Features.Documents.ListShares;
using DocumentService.Features.Documents.RevokeShare;

namespace DocumentService.Features.Documents;

// FR-19, FR-20, UC-11, ADR-0036 D-06, ADR-0046 D-06 部品 3, IADR-0253 決定 4（段 4）:
// 文書の共有先（DocumentShare）の付与・取り消し・一覧の合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/Documents/{ListShares,GrantShare,RevokeShare}/`
// に居る。**集約は `Documents` のままである** —— `/documents/{id}/shares` は文書の従属資源であり、
// 認可も本文書き込みと同じ動的束縛（`DocumentBodyIntake.CanWrite`）を共有する。
// 集約の切り直しは深さの規範（ADR-0065 決定 2）の射程外なので、ここでは行わない。
//
// **変更できるのは所有者だけである**（計画: `doc.shared_with` を変更できるのは `doc.owner` を
// 満たす主体に限る）。判定は本文書き込みと同じ動的束縛 `doc.owner ∈ { ${current_user} }` を
// `DocumentBodyIntake.CanWrite` の再利用で行い、規則を 1 か所に保つ。
// **再共有不可はこの所有者限定から従う** —— 被共有者は owner ではないため、共有の追加も
// 取り消しもできない。ロールによる判定は追加しない（ADR-0036 D-07）。
//
// 🔴 **拒否の応答は 404 である。403 にしない**（ADR-0056 決定 1・[[IADR-0277]]）。
// ADR-0056 は打ち分けの軸を「**主体がその文書を読めるか**」と定め、読めない相手には
// 常に「見つからない」と答えることを課した。**本サービスは ABAC の読み取り判定を持たない**
// （AuthorizationService を呼ぶ口が無い）ため、`CanWrite` が偽のとき「読めるが書けない」
// （＝403 が許される決定 2 の側）だと**言い切れない**。判定できないものを読めると仮定せず、
// fail-closed 側へ倒す。403 を返すと**文書 ID の総当たりで実在が判別できてしまう**。
//
// 🔴 本段は**貯蔵と管理 API まで**である。共有先ベースの分岐（選言の第 3 節）を認可スコープへ
// 載せる配線は、消費側が共有記録へ到達する方式（DB per Service の越境）が未決のため別段とする。
public static class DocumentShareEndpoints
{
    public static IEndpointRouteBuilder MapDocumentShareEndpoints(this IEndpointRouteBuilder app)
    {
        // 認証は必須（主体が決まらないと動的束縛を評価できない）。ロール要求は付けない
        // （FR-19 の共有は一般利用者の操作。管理者ロール限定にすると所有者が共有できない）。
        var g = app.MapGroup("/documents/{id:guid}/shares")
            .WithTags("Documents")
            .RequireAuthorization();

        ListDocumentSharesEndpoint.Map(g);
        GrantDocumentShareEndpoint.Map(g);
        RevokeDocumentShareEndpoint.Map(g);

        return app;
    }
}

// FR-20: 共有先 1 件の公開形。一覧と付与の両方が返すため集約直下に置く。
public record DocumentShareDto(string SubjectType, string SubjectId, string GrantedBy,
    DateTimeOffset CreatedAt);
