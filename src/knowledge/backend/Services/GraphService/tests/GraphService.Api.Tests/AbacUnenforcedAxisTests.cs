using AwesomeAssertions;
using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Services;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Tests;

// FR-17, ADR-0034 決定 6・8・9, ADR-0036, IADR-0242 フォローアップ:
// 🔴 **現時点で強制できていない認可軸を固定する。**
//
// ADR-0034 決定 6・8・9（個人資料の境界）と ADR-0036（所有者ベースの裁量制御）は、
// 実装の現状では**機能しない**。理由は 3 つあり、いずれも GraphService の外にある:
//
//   1. 実データ上、必須とされる文書属性のうち `owner` / `department` / `lifecycle` /
//      **`doc_scope`** が 0% 充足である（実効的な認可軸は `confidentiality` のみ。#516）
//
//      **［2026-08-22 追記］`doc_scope` が 4 件目として加わった**（計画 `ADR-0054`。
//      個人資料 `private-note` と組織文書 `organization` を区別する**必須**属性）。
//      必須は 4 → 5 属性、欠落は 3 → 4 属性になった。
//
//      🔴 **`doc_scope` は他の 3 件と性質が違う。** `owner` / `department` は「解決できない
//      ときの予約値」を持つが、**`doc_scope` は持たない** —— 取り込み経路が個人資料を作る
//      ことはないため、システム投入経路の既定（`organization`）が終端である。
//      既存 2,368 件へは**遡及付与しない**方針で、**破棄が完了するまで 0 件のままである**。
//
//      **本サービスにコード変更は要らない。** `AbacNodeFilter` はスコープが返したフィルタを
//      適用するだけで必須属性を列挙しない。**属性キーの欠落は不一致（安全側）** なので、
//      組織ポリシーが `doc_scope` を名指した瞬間に**すべての文書が不可視へ倒れる** ——
//      これは情報が漏れる向きではない。
//   2. ~~ADR-0036 が定めた `read` の 3 分岐 OR（属性ベース / 所有者ベース / 共有ベース）を
//      表現する構造が `AccessScopeResponse` に無い（複数ポリシーはキー単位の和で AND に畳まれる）~~
//
//      **［2026-08-28 追記 / #989 段 3］理由 2 も解消した。** `AccessScopeResponse` は `Branches`
//      （名前つき分岐）を持ち（IADR-0253 決定 1 / 段 1）、評価器が分岐を組み立て（段 2）、
//      **`AbacNodeFilter` が分岐間 OR・分岐内 AND で評価するようになった**（段 3・本 issue）。
//      その証拠は `Owner_attribute_IS_enforced_when_scope_carries_an_owner_branch`（下の陽性対照）
//      であり、**分岐を運ぶ応答なら owner で見え方が変わる**。
//
//      🔴 **それでも下の 2 件は緑のままで正しい。** あれらが与えているのは**分岐を持たない
//      応答**（未移行の発行者・段 1 以前の形）であり、その場合は後方互換で従来の連言評価に
//      落ちるからである。**残る未強制の理由は 1 だけになった** —— 実データに `owner` が無く
//      （0% 充足）、owner ベースのポリシーも未配備なので、**現実の応答には owner 分岐が現れない**。
//   3. ~~`/authz/scope` は `PolicyAction.Read` をサーバ側でハードコードしており、
//      `AccessScopeRequest` に Action フィールドが無い~~
//
//      **［2026-08-23 追記 / #993］理由 3 は解消した。** `AccessScopeRequest` は `Action`（既定
//      `read`）を持ち（IADR-0253 決定 5 / #989）、`AuthzEndpoints` のハードコードは消え、
//      `PolicyAction` は `write` を含む 4 値になった。**GraphService も書き込み経路で `write` を
//      渡すようになった**（IADR-0272 / #993。`WriteActionAuthorizationTests` が固定する）。
//
//      🔴 **それでも本テストは赤くならない。** 解消したのは「**行為**を区別できない」ことであり、
//      本テストが測っているのは「**所有者（owner）で見え方が変わらない**」ことだからである
//      —— 軸が違う。**理由 1・2 は今も成立している**ため、下の 2 件は緑のままで正しい。
//
// **本テストは「まだ強制していない」ことを明示的に固定するものである。**
// 上記が是正されると本テストは赤くなる —— それが狙いであり、そのとき初めて
// 「所有者で見え方が変わる」実装を足してよい。**赤くなったら消すのではなく、
// 強制されるようになったことを確かめる形へ書き換えること。**
//
// ［2026-08-28 追記 / #989 段 3］**その書き換えを行った。** 理由 2 が解消したので、
// **検出対象を「述語が分岐を評価できるか」から「実データ・ポリシーが owner を運ぶか」へ移した**。
// 下の 2 件（分岐なし応答＝従来評価）は**残す** —— 後方互換の固定として意味を持ち続ける。
// **陽性対照を 1 件足した** —— 分岐を運ぶ応答なら owner で見え方が変わることを示し、
// 「分岐評価を消す・キー単位 union へ潰す」変異をここで捕まえる。
public class AbacUnenforcedAxisTests
{
    // #516 / ADR-0036: owner だけが異なる 2 文書は、現状のスコープでは区別されない。
    [Fact]
    public void Owner_attribute_is_NOT_yet_enforced_documents_differing_only_by_owner_are_indistinguishable()
    {
        // 「自分が所有者」と「他人が所有者」。ADR-0036 D-02 が意図した判定なら差が出るはずである。
        var mine = GraphDocument.Create(Guid.NewGuid(), "mine",
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["owner"] = "test-user" },
            null, DateTimeOffset.UtcNow);
        var theirs = GraphDocument.Create(Guid.NewGuid(), "theirs",
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["owner"] = "someone-else" },
            null, DateTimeOffset.UtcNow);

        // 認可サービスが現実に返す形。owner の分岐はここに現れない。
        var scope = new AccessScopeResponse("test-user",
            [new AttributeFilter("confidentiality", ["internal"])], true);

        AbacNodeFilter.Matches(mine, scope).Should().BeTrue();
        AbacNodeFilter.Matches(theirs, scope).Should()
            .BeTrue("**分岐を持たない応答**では owner に基づく判定は働かない（後方互換の従来評価）。"
                  + "現実の応答に owner 分岐が現れないのは実データの owner が 0% 充足であり"
                  + "owner ベースのポリシーも未配備だからである（#516）。"
                  + "ここが false になったら、分岐なし応答の後方互換が壊れた合図である");
    }

    // ── 🔴 陽性対照（#989 段 3 で新設）───────────────────────────────────────────
    // **分岐を運ぶ応答なら owner で見え方が変わる。** 理由 2（表現構造が無い）が解消したことの証拠。
    //
    // これが赤くなったら、分岐評価が消えた・キー単位 union へ潰れたということである。
    [Fact]
    public void Owner_attribute_IS_enforced_when_scope_carries_an_owner_branch()
    {
        var mine = GraphDocument.Create(Guid.NewGuid(), "mine",
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["owner"] = "test-user" },
            null, DateTimeOffset.UtcNow);
        var theirs = GraphDocument.Create(Guid.NewGuid(), "theirs",
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["owner"] = "someone-else" },
            null, DateTimeOffset.UtcNow);

        // ADR-0036 D-02 が意図した形。所有者ベースの分岐は ${current_user} を束縛済みで届く
        // （束縛は認可サービスの責務。IADR-0253 決定 3）。
        var scope = new AccessScopeResponse("test-user",
            [new AttributeFilter("confidentiality", ["internal"])], true,
            Branches: [new AccessScopeBranch("個人資料", [new AttributeFilter("owner", ["test-user"])])]);

        AbacNodeFilter.Matches(mine, scope).Should()
            .BeTrue("自分が所有者の文書は所有者ベースの分岐で可視になる");
        AbacNodeFilter.Matches(theirs, scope).Should()
            .BeFalse("他人の個人資料は見えない —— ここが true なら分岐評価が効いていない");
    }

    // 🔴 陰性対照: 分岐が混成を許さない（キー単位 union への退行を捕まえる）。
    [Fact]
    public void Branches_are_not_folded_into_a_keywise_union()
    {
        var mixture = GraphDocument.Create(Guid.NewGuid(), "混成",
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["department"] = "sales" },
            null, DateTimeOffset.UtcNow);

        var scope = new AccessScopeResponse("test-user", [], true, Branches:
        [
            new AccessScopeBranch("A: 人事の内部資料",
                [new AttributeFilter("confidentiality", ["internal"]),
                 new AttributeFilter("department", ["hr"])]),
            new AccessScopeBranch("B: 営業の公開資料",
                [new AttributeFilter("confidentiality", ["public"]),
                 new AttributeFilter("department", ["sales"])])
        ]);

        AbacNodeFilter.Matches(mixture, scope).Should().BeFalse(
            "(internal, sales) はどちらのポリシー単独でも許可されない（IADR-0253 決定 2 の反例）");
    }

    // ADR-0036 D-03: 動的束縛（${current_user}）はスコープの語彙に存在しない。
    [Fact]
    public void Dynamic_binding_placeholders_are_NOT_interpreted()
    {
        var doc = GraphDocument.Create(Guid.NewGuid(), "d",
            new Dictionary<string, string> { ["owner"] = "test-user" },
            null, DateTimeOffset.UtcNow);

        // 仮に認可サービスが束縛前の文字列を返しても、述語は素の文字列比較しかしない。
        var scope = new AccessScopeResponse("test-user",
            [new AttributeFilter("owner", ["${current_user}"])], true);

        AbacNodeFilter.Matches(doc, scope).Should()
            .BeFalse("動的束縛は認可サービス側で解決されるべきものであり、"
                   + "述語がプレースホルダを解釈すると認可の判断が 2 箇所へ散る");
    }
}
