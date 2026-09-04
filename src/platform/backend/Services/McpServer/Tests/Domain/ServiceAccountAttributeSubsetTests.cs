using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;

namespace McpServer.Tests.Domain;

// FR-16, FR-05, UC-09, SC-12, ADR-0062 決定 2: 無人アカウントへ割り当てられる `clearance` と
// タグの集合は、**登録者が持つ集合の部分集合**でなければならない。
//
// **判定そのものを器なしで固定する。** 端点側のテストは経路（登録・差し替え・応答形式）を見る。
public class ServiceAccountAttributeSubsetTests
{
    private static RegistrarAssignableAttributes Registrar(string clearance, string tags = "")
        => RegistrarAssignableAttributes.Of(
            ServiceAccountAttributeSubset.Tokens(clearance),
            ServiceAccountAttributeSubset.Tokens(tags));

    private static IReadOnlyList<string> Validate(
        RegistrarAssignableAttributes registrar, params (string Key, string Value)[] attributes)
        => ServiceAccountAttributeSubset.Validate(
            "batch-x",
            attributes.ToDictionary(a => a.Key, a => a.Value),
            registrar);

    // 受け入れ基準 1: 登録者が持たない機密区分は割り当てられず、**外れた値が名指しで**含まれる。
    [Fact]
    public void 登録者の集合外の機密区分は外れた値を名指しして拒否される()
    {
        var errors = Validate(Registrar("public,internal"), ("clearance", "confidential"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("confidential");
    }

    // 受け入れ基準 2（陽性対照）: **登録者より狭い無人アカウントは作れる。** 同一値限定にしない。
    [Fact]
    public void 登録者より狭い機密区分は割り当てられる()
    {
        Validate(Registrar("public,internal,confidential"), ("clearance", "internal"))
            .Should().BeEmpty();
    }

    // 受け入れ基準 3: 🔴 **外れていない値を混ぜない**（差集合だけを名指しする）。
    [Fact]
    public void タグは外れた値だけが列挙される()
    {
        var errors = Validate(Registrar("public", "sales,hr"), ("tags", "sales,finance"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("finance");
        errors[0].Should().NotContain("'sales' は");
    }

    // 受け入れ基準 10: **集合であって列ではない。** 順序の違いは同値である。
    [Theory]
    [InlineData("sales,hr")]
    [InlineData("hr,sales")]
    [InlineData(" hr , sales ")]
    public void タグの順序と余白は判定に影響しない(string requested)
    {
        Validate(Registrar("public", "hr,sales"), ("tags", requested)).Should().BeEmpty();
    }

    // 受け入れ基準 5: **ロールと機密区分は別の軸である。** 判定はロールを一切見ない
    // （見る材料が引数に無いことが、それを構造で示している）。
    [Fact]
    public void 登録者の機密区分がinternalならrestrictedは配れない()
    {
        var errors = Validate(Registrar("public,internal"), ("clearance", "restricted"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("restricted");
    }

    // 受け入れ基準 6: 引けなかったときは配らない。**「持っていない」と混ぜない**（文言で分ける）。
    [Fact]
    public void 登録者の属性を解決できなければ拒否し理由を取り違えない()
    {
        var errors = ServiceAccountAttributeSubset.Validate(
            "batch-x",
            new Dictionary<string, string> { ["clearance"] = "public" },
            RegistrarAssignableAttributes.Unavailable);

        errors.Should().ContainSingle();
        errors[0].Should().Contain("解決できませんでした");
        // 🔴 最も広い区分ですら配れないので、「あなたの持ち物には無い」とは書かない。
        errors[0].Should().NotContain("登録者が持つ機密区分は");
    }

    // 陽性対照: 対象キーを 1 つも含まない属性は本規則の対象外である
    // （`doc_scope` は ADR-0034 決定 9 の別の規則が見る）。**解決できていなくても通る。**
    [Fact]
    public void 対象キーを含まない属性は解決できなくても通る()
    {
        ServiceAccountAttributeSubset.Governs(
            new Dictionary<string, string> { ["doc_scope"] = "organization" }).Should().BeFalse();

        ServiceAccountAttributeSubset.Validate(
            "batch-x",
            new Dictionary<string, string> { ["doc_scope"] = "organization" },
            RegistrarAssignableAttributes.Unavailable).Should().BeEmpty();
    }

    // 契約「Granted かつフィルタ無し＝条件無しで許可（全件可）」のとき、機密区分で絞る根拠は無い。
    // **タグは絞られ続ける**（無制限なのは機密区分だけである）。
    [Fact]
    public void 機密区分で絞られていない登録者は任意の区分を配れるがタグは絞られる()
    {
        var registrar = RegistrarAssignableAttributes.Of(
            [], ServiceAccountAttributeSubset.Tokens("sales"), clearanceUnrestricted: true);

        Validate(registrar, ("clearance", "restricted")).Should().BeEmpty();
        Validate(registrar, ("tags", "finance")).Should().ContainSingle();
    }

    // 大文字小文字は判定を割らない（属性辞書の許可値と同じ扱い＝OrdinalIgnoreCase）。
    [Fact]
    public void 機密区分の大文字小文字は同値として扱う()
    {
        Validate(Registrar("public,internal"), ("clearance", "INTERNAL")).Should().BeEmpty();
    }

    // 🔴 登録者が何も持たないとき、報告は「ありません」であって空文字ではない。
    [Fact]
    public void 登録者が何も持たないときの文言は空文字を騙らない()
    {
        var errors = Validate(Registrar(""), ("clearance", "public"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("ありません");
    }
}
