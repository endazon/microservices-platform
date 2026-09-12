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
// FR-19, ADR-0036 D-06, ADR-0098 決定 1, [[IADR-0447]] 決定 4 (#1447):
// **共有先ベースの分岐（選言の第 3 節）は配線済みである。**
// （従前ここには「本段は貯蔵と管理 API までであり、消費側が共有記録へ到達する方式
// 〔DB per Service の越境〕が未決のため別段とする」と書いてあった。その未決が解けた。）
//
// - **運ぶ**: 共有先は `DocumentDto.SharedWith`（応答）と `DocumentUpdated.SharedWith`（イベント）で
//   運ぶ。**所有者（本サービス）が写しを載せ、消費側は台帳へ到達しない**（越境しない）。
//   解決点は `DocumentEndpoints.ResolveSharedWithAsync` ただ 1 つで、応答とイベントで同じ値である。
// - **評価する**: 判定は `shared_with` を条件に持つ read ポリシーの分岐であり
//   （`documentConditions: { "shared_with": ["${current_user}", "${current_groups}"] }`）、
//   `AbacEvaluator` が `${current_groups}` を所属の集合へ展開する。集合値キーなので交差で判定する
//   （ADR-0080 決定 2 / [[IADR-0448]]）。**配備環境ではポリシーの投入が統制の実現手段である** ——
//   投入までは従前と同じ fail-closed（共有は誰にも何も許可しない）。dev は
//   `deploy/local/abac-seed/policies.json` に 1 本入っている。
// - **到達する面**: 索引（Qdrant のリスト項目）・BFF の単体判定（`DocumentAttributeEncoding.WithSharedWith`
//   の像）・グラフの複製（`GraphDocument.Attributes["shared_with"]`）。
//
// 🔴 **`PublishUpdatedAsync` が `SubjectType` を落とすのは意図である。** 判定規則
// `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅` は種別を区別せず
// **1 つの集合として突き合わせる** —— 利用者名（Keycloak の username）とグループ ID（UUID）は
// 名前空間が交わらないため、混ぜても「別種の同名」が起き得ない。**種別が要るのはこの管理 API
// だけである**（取り消しの鍵 `/{subjectType}/{subjectId}` と、SC-19 の公開範囲の導出）。
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

// FR-19, SC-19 主要素 3, ADR-0098 決定 1, #1445: 🔴 **応答・要求の形はここに無い。**
// `Knowledge.Contracts/Dtos/PrivateNoteShareDto.cs`（`DocumentShareDto` / `CreateShareRequest`）が持つ
// —— **BFF（別ユニット）が同じ形を画面へ配るため、定義を 2 つ持たない**（個人資料・タグ辞書と同じ
// 切り分け。写しを置くと契約検査が片方しか見ず、静かに割れる）。**名前も項目も変えずに移した。**
