using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;
using System.Security.Claims;

namespace Platform.Bff.Tests;

// FR-05, IADR-0009, Issue #229: Shared.Infrastructure へ切り出した ABAC スコープ解決ヘルパの純ロジック単体テスト。
// deny-by-default・AND/OR・大文字小文字非依存・claim 抽出を直接検証する（ResolveAsync の HTTP 経路は
// Document/Search の BFF エンドポイントテストが回帰保証する）。
public class BffScopeResolverTests
{
    // FR-05: 許可ポリシー無し（GrantsAccess=false）は deny-by-default で常に不一致。
    [Fact]
    public void Matches_DeniesWhenScopeGrantsNoAccess()
    {
        var scope = new BffAccessScope([], GrantsAccess: false);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["department"] = "sales" }, scope)
            .Should().BeFalse();
    }

    // FR-05: フィルタ空 かつ GrantsAccess=true は「条件なしで全件許可」。
    [Fact]
    public void Matches_AllowsWhenGrantedAndNoFilters()
    {
        var scope = new BffAccessScope([], GrantsAccess: true);

        BffScopeResolver.Matches(new Dictionary<string, string>(), scope).Should().BeTrue();
    }

    // FR-05: 値集合内は OR（大文字小文字非依存）で一致する。
    [Theory]
    [InlineData("sales", true)]
    [InlineData("SALES", true)]
    [InlineData("legal", true)]
    [InlineData("hr", false)]
    public void Matches_EvaluatesValueSetAsCaseInsensitiveOr(string value, bool expected)
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("department", ["sales", "legal"])],
            GrantsAccess: true);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["department"] = value }, scope)
            .Should().Be(expected);
    }

    // FR-05: 文書に当該属性キーが無ければ不一致（narrowing-only）。
    [Fact]
    public void Matches_DeniesWhenAttributeKeyMissing()
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("department", ["sales"])],
            GrantsAccess: true);

        BffScopeResolver.Matches(new Dictionary<string, string> { ["clearance"] = "secret" }, scope)
            .Should().BeFalse();
    }

    // FR-05: フィルタ間は AND（1 つでも外れれば不一致）。
    [Fact]
    public void Matches_RequiresAllFiltersToPass()
    {
        var scope = new BffAccessScope(
            [
                new AttributeFilter("department", ["sales"]),
                new AttributeFilter("clearance", ["secret"]),
            ],
            GrantsAccess: true);

        var docAttrs = new Dictionary<string, string> { ["department"] = "sales", ["clearance"] = "public" };

        BffScopeResolver.Matches(docAttrs, scope).Should().BeFalse();
    }

    // ── FR-19, 計画 ADR-0036 D-06, ADR-0080 決定 2, ADR-0098 決定 1, [[IADR-0448]] (#1448):
    //    **文書側の集合値属性は交差で一致する**（受け入れ基準 3 の BFF 面）────────────
    //
    // 🔴 従前ここ（`BffScopeResolver` の private `MatchesAll`）は属性値を**単一文字列**として
    // 比べており、集合値の `shared_with`（文書ごとに可変長の共有先）は**1 件も一致しなかった**。
    // 述語は契約側の 1 か所（`AttributeFilterMatch`）へ寄せた —— **ここへ自前の実装を戻すと
    // この節が赤くなる。**

    // 集合値キー: 交差が空でなければ一致する（部分集合ではない）。
    [Theory]
    [InlineData("u-alice,g-knowledge", true)]   // 交差 {g-knowledge}
    [InlineData("g-knowledge", true)]           // 1 要素でも足りる
    [InlineData("g-knowledge,g-finance", true)] // 許可側に無い要素が在っても失われない
    [InlineData("g-finance", false)]            // 交差なし（他人のグループ共有）
    [InlineData("", false)]                     // 🔴 空集合は何にも一致しない
    public void Matches_EvaluatesSetValuedDocumentAttributesAsIntersection(string sharedWith, bool expected)
    {
        var scope = new BffAccessScope(
            [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["g-knowledge"])],
            GrantsAccess: true);

        BffScopeResolver.Matches(
            new Dictionary<string, string>
            {
                [DocumentAttributeEncoding.SharedWithKey] = sharedWith,
            }, scope)
            .Should().Be(expected);
    }

    // 🔴 陽性対照（**単一値の判定は不変である**）: 集合の規則を単一値キーへ広げていない。
    // `"internal,public"` は「internal でも public でもない値」のままである（[[IADR-0385]] の禁則）。
    [Fact]
    public void Matches_DoesNotSplitSingleValuedAttributes()
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("confidentiality", ["internal"])],
            GrantsAccess: true);

        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope)
            .Should().BeTrue("陽性対照: 単一値の一致は従前どおり");
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal,public" }, scope)
            .Should().BeFalse("一律に分割すると単一値属性の値域へ区切り文字が侵食する");
    }

    // FR-19, ADR-0098 決定 1 / #1447 の消費側: 共有先を重ねた像（`WithSharedWith`）が、
    // `${current_groups}` を束縛した分岐で一致する（**個人共有とグループ共有が同じ分岐で効く**）。
    [Fact]
    public void Matches_AllowsADocumentSharedWithOneOfMyGroups()
    {
        // 認可サービスが `shared_with ∈ {自分, 所属…}` へ束縛した分岐（IADR-0447）。
        var scope = new BffAccessScope(
            [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["u-alice", "g-knowledge"])],
            GrantsAccess: true,
            Branches:
            [
                new AccessScopeBranch("共有された資料",
                    [new AttributeFilter(DocumentAttributeEncoding.SharedWithKey, ["u-alice", "g-knowledge"])]),
            ]);

        var document = new Dictionary<string, string> { ["confidentiality"] = "restricted" };

        // グループへの共有（自分は共有先に個人として入っていない）→ 可視。
        BffScopeResolver.Matches(
            DocumentAttributeEncoding.WithSharedWith(document, ["g-knowledge"]), scope)
            .Should().BeTrue();
        // 個人への共有 → 可視。
        BffScopeResolver.Matches(
            DocumentAttributeEncoding.WithSharedWith(document, ["u-alice"]), scope)
            .Should().BeTrue();
        // 陰性対照: 自分が入っていない共有 → 不可視（存在秘匿は呼び出し側が 404 へ写す）。
        BffScopeResolver.Matches(
            DocumentAttributeEncoding.WithSharedWith(document, ["u-bob", "g-finance"]), scope)
            .Should().BeFalse();
        // 陰性対照: 共有が 1 つも無い個人資料 → キーごと載らないので不可視。
        BffScopeResolver.Matches(
            DocumentAttributeEncoding.WithSharedWith(document, []), scope)
            .Should().BeFalse();
    }

    // ── #989 段 3（FR-19, ADR-0036, IADR-0253 決定 1）: 名前つき分岐の評価 ────────────
    //
    // 分岐内は AND・分岐間は OR。#989 退行防止の写像: 「個人資料（owner ベース）」と
    // 「組織文書（属性ベース）」の両ポリシーがマッチしたとき、**どちらか一方**を満たす文書が
    // 見える（従来のキー単位 union では積だけが見えた）。

    // 正例: 2 分岐のうち片方（属性ベース）だけを満たす文書が可視。
    [Fact]
    public void Matches_EvaluatesBranchesAsDisjunction()
    {
        var scope = new BffAccessScope(
            // Filters（従来の算出値）は union の連言 —— これでは owner を持たない文書は全滅する。
            [new AttributeFilter("owner", ["u1"]), new AttributeFilter("confidentiality", ["internal"])],
            GrantsAccess: true,
            Branches:
            [
                new AccessScopeBranch("個人資料", [new AttributeFilter("owner", ["u1"])]),
                new AccessScopeBranch("組織文書", [new AttributeFilter("confidentiality", ["internal"])]),
            ]);

        // owner 属性を持たない組織文書 → 分岐「組織文書」で可視（従来評価なら不可視だった）。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope)
            .Should().BeTrue();
        // 自分の個人資料（owner だけ合致）→ 分岐「個人資料」で可視。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["owner"] = "u1", ["confidentiality"] = "restricted" }, scope)
            .Should().BeTrue();
        // 陰性対照: どの分岐も満たさない（他人の個人資料）→ 不可視。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["owner"] = "u2", ["confidentiality"] = "restricted" }, scope)
            .Should().BeFalse();
    }

    // 🔴 負例（IADR-0253 決定 2 の反例シナリオ）: **キー単位 union はどのポリシー単独も許可しない
    // 値の混成（A=internal×hr・B=public×sales → internal×sales）を許す。** 分岐評価は
    // これを拒否しなければならない —— union へ潰す実装（再導入）はこのテストが落とす。
    [Fact]
    public void Matches_DeniesCrossPolicyMixture_BranchesAreNotKeywiseUnion()
    {
        var scope = new BffAccessScope(
            // 従来の算出値（キー単位 union）。これで評価すると internal×sales が通ってしまう。
            [
                new AttributeFilter("confidentiality", ["internal", "public"]),
                new AttributeFilter("department", ["hr", "sales"]),
            ],
            GrantsAccess: true,
            Branches:
            [
                new AccessScopeBranch("A", [
                    new AttributeFilter("confidentiality", ["internal"]),
                    new AttributeFilter("department", ["hr"]),
                ]),
                new AccessScopeBranch("B", [
                    new AttributeFilter("confidentiality", ["public"]),
                    new AttributeFilter("department", ["sales"]),
                ]),
            ]);

        // 混成（A にも B にも単独では許可されない組合せ）→ 不可視。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["department"] = "sales" },
            scope).Should().BeFalse();

        // 陽性対照: 各ポリシーが単独で許可する組合せは可視（分岐そのものが効いていることの対）。
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal", ["department"] = "hr" },
            scope).Should().BeTrue();
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "public", ["department"] = "sales" },
            scope).Should().BeTrue();
    }

    // 後方互換: Branches が null／空なら従来どおり Filters（連言）で評価する（段 1・2 と同じ扱い）。
    [Theory]
    [InlineData(true)]   // null
    [InlineData(false)]  // 空リスト
    public void Matches_FallsBackToFiltersWhenBranchesAbsent(bool useNull)
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("confidentiality", ["internal"])],
            GrantsAccess: true,
            Branches: useNull ? null : []);

        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope).Should().BeTrue();
        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "secret" }, scope).Should().BeFalse();
    }

    // 分岐のフィルタが空 = そのポリシーの範囲で全件許可（AbacPageFilter と同一意味論）。
    [Fact]
    public void Matches_BranchWithNoFilters_GrantsAll()
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("confidentiality", ["secret"])],
            GrantsAccess: true,
            Branches: [new AccessScopeBranch("無条件許可", [])]);

        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope).Should().BeTrue();
    }

    // deny-by-default は分岐があっても変わらない（Granted=false が最優先）。
    [Fact]
    public void Matches_DeniesWhenNotGranted_EvenWithBranches()
    {
        var scope = new BffAccessScope(
            [],
            GrantsAccess: false,
            Branches: [new AccessScopeBranch("無条件許可", [])]);

        BffScopeResolver.Matches(
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, scope).Should().BeFalse();
    }

    // ── FR-19, IADR-0253 決定 1（段 3 / #989）: ToContractScope は Branches を後段へ運ぶ ──
    //
    // 🔴 **波 1 ではここで Branches を落としていた**（後段 RetrievalService が未移行だったため）。
    // 段 3 で消費側の移行が完了したので反転した。**落とす実装へ戻すと後段だけが従来の連言で
    // 判定し、検索経路だけが混成（IADR-0253 決定 2 の反例）を許す。** その退行をここで止める。
    [Fact]
    public void ToContractScope_CarriesBranchesToDownstream()
    {
        var branches = new List<AccessScopeBranch>
        {
            new("A: 人事の内部資料",
                [new AttributeFilter("confidentiality", ["internal"]),
                 new AttributeFilter("department", ["hr"])]),
            new("B: 営業の公開資料",
                [new AttributeFilter("confidentiality", ["public"]),
                 new AttributeFilter("department", ["sales"])])
        };
        var scope = new BffAccessScope(
            [new AttributeFilter("confidentiality", ["internal", "public"])],
            GrantsAccess: true,
            Branches: branches);

        var contract = scope.ToContractScope();

        contract.Branches.Should().BeEquivalentTo(branches,
            "後段が分岐で判定できなければ段 3 は成立しない");
        contract.Filters.Should().BeEquivalentTo(scope.Filters,
            "従来の連言（AllowedFilters 由来）は据え置き（IADR-0253 決定 2）");
        contract.GrantsAccess.Should().BeTrue();
    }

    // 陰性対照: 分岐が無い応答では null のまま運ばれる（後方互換。「常に何か入れる」実装を落とす）。
    [Fact]
    public void ToContractScope_WithoutBranches_CarriesNull()
    {
        var scope = new BffAccessScope(
            [new AttributeFilter("confidentiality", ["internal"])], GrantsAccess: true);

        scope.ToContractScope().Branches.Should().BeNull();
    }

    // FR-05, FR-06, IADR-0272 決定 4 (#1010): **ResolveAsync の action に既定値を置かない**ことを
    // リフレクションで固定する。既定値つきの引数は「書かなければ read」を意味し、書き忘れが
    // 認可の緩みとして現れる（#993 / #1010 の欠陥そのもの）。GraphService の
    // GraphTypeGateArchitectureTests と同型の構造テスト。
    [Fact]
    public void ResolveAsync_ActionParameter_HasNoDefaultValue()
    {
        var method = typeof(BffScopeResolver).GetMethod(nameof(BffScopeResolver.ResolveAsync));

        method.Should().NotBeNull();
        var actionParam = method!.GetParameters().SingleOrDefault(p => p.Name == "action");
        actionParam.Should().NotBeNull("action 引数が無ければ #1010 の是正が外れている");
        actionParam!.HasDefaultValue.Should().BeFalse(
            "既定値が復活すると『書き忘れ＝read で解決』が再発する（IADR-0272 決定 4）");
    }

    // FR-05: JWT の clearance/department クレームを利用者属性へ写す。
    [Fact]
    public void ExtractUserAttributes_ReadsClearanceAndDepartmentClaims()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("clearance", "secret"),
                new Claim("department", "sales"),
            ], authenticationType: "test")),
        };

        var attrs = BffScopeResolver.ExtractUserAttributes(ctx);

        attrs.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["clearance"] = "secret",
            ["department"] = "sales",
        });
    }

    // FR-05: 対象クレームが無ければ該当キーを含めない（欠落は付与しない）。
    [Fact]
    public void ExtractUserAttributes_OmitsMissingClaims()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("department", "legal")], authenticationType: "test")),
        };

        var attrs = BffScopeResolver.ExtractUserAttributes(ctx);

        attrs.Should().ContainKey("department").And.NotContainKey("clearance");
    }

    // ---- #1323 / ADR-0080 決定 1・2, IADR-0411: 集合値の利用者属性を運ぶ ----------------------
    //
    // 🔴 **Keycloak の多値属性マッパーは同じ型のクレームを複数発行する。** `FindFirst` で読むと
    // **先頭 1 値へ畳まれる**（#1243 で実測した欠陥そのもの）。ここが畳むと、下流の交差判定
    // （`AbacEvaluator.MatchesUserConditions`）は正しくても**入力が痩せているせいで**
    // マッチしなくなる。**倒れる向きは deny だが、効かせたい統制が黙って効かない。**

    // FR-05, FR-09, ADR-0080 決定 1（陽性）: 多値クレームは線上表現へ連結する。
    [Fact]
    public void ExtractUserAttributes_MultiValuedTagClaims_AreJoinedNotCollapsed()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("clearance", "internal"),
                new Claim("tags", "sales"),
                new Claim("tags", "hr"),
            ], authenticationType: "test")),
        };

        var attrs = BffScopeResolver.ExtractUserAttributes(ctx);

        UserAttributeEncoding.Split(attrs["tags"]).Should().BeEquivalentTo(["sales", "hr"],
            "先頭 1 値へ畳むと 2 つ目のタグを条件に持つポリシーが黙ってマッチしなくなる");
        // 陽性対照: 単値キーの読み方は 1 文字も変わっていない。
        attrs["clearance"].Should().Be("internal");
    }

    // FR-05, ADR-0080（陰性対照・IADR-0385 の禁則）: 単値キーは連結・分割の対象にしない。
    [Fact]
    public void ExtractUserAttributes_SingleValuedKeys_AreNotTreatedAsSets()
    {
        // 区切り文字を含む単値。**分割してはならない**（辞書外の値が要素として通る）。
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("clearance", "internal,restricted")], authenticationType: "test")),
        };

        var attrs = BffScopeResolver.ExtractUserAttributes(ctx);

        attrs["clearance"].Should().Be("internal,restricted",
            "単値キーへ集合の規則を適用すると clearance の値域へ区切り文字が侵食する");
        UserAttributeEncoding.IsSetValued("clearance").Should().BeFalse("陽性対照: 集合値キーではない");
    }

    // FR-05（陰性対照）: 集合値クレームが無ければキー自体を載せない。
    // 空文字を載せると「属性は持つが空」となり、単値キーの欠落と扱いがずれる。
    [Fact]
    public void ExtractUserAttributes_NoSetValuedClaims_OmitsTheKeysEntirely()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("clearance", "public")], authenticationType: "test")),
        };

        var attrs = BffScopeResolver.ExtractUserAttributes(ctx);

        attrs.Should().NotContainKey("tags").And.NotContainKey("projects");
    }

    // FR-05, ADR-0080（回帰の担保）: 集合値キーの列挙は契約 1 か所（UserAttributeEncoding）に従う。
    // 🔴 抽出点が独自にキーを列挙し始めると #1323 が再発する。
    [Fact]
    public void ExtractUserAttributes_CarriesEverySetValuedKeyDeclaredByTheContract()
    {
        var claims = UserAttributeEncoding.SetValuedKeys
            .Select(k => new Claim(k, "v-" + k)).ToArray();
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test")),
        };

        var attrs = BffScopeResolver.ExtractUserAttributes(ctx);

        foreach (var key in UserAttributeEncoding.SetValuedKeys)
            attrs.Should().ContainKey(key, "契約が集合値と宣言したキーは 1 つ残らず運ぶ");
    }
}
