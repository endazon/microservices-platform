using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using WikiService.Api.Foundation.Domain;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;

namespace WikiService.Api.Tests;

// FR-13, FR-05, UC-07: Wiki ページ ABAC フィルタの評価意味論（AND / OR / deny-by-default）。
public class AbacPageFilterTests
{
    private static WikiPage Page(Dictionary<string, string> attrs)
        => WikiPage.CreateFromDocument(Guid.NewGuid(), "T", null, attrs, []);

    // deny-by-default: マッチするポリシーが無い（Granted=false）なら不可視。
    [Fact]
    public void Matches_ReturnsFalse_WhenNotGranted()
    {
        var page = Page(new() { ["confidentiality"] = "public" });
        var scope = new AccessScopeResponse("u", [], Granted: false);

        AbacPageFilter.Matches(page, scope).Should().BeFalse();
    }

    // Granted かつ条件無し（全件可）は可視。
    [Fact]
    public void Matches_ReturnsTrue_WhenGrantedAndNoFilters()
    {
        var page = Page(new() { ["confidentiality"] = "confidential" });
        var scope = new AccessScopeResponse("u", [], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeTrue();
    }

    // 値集合内は OR: 許可値のいずれかに一致すれば可視。
    [Fact]
    public void Matches_ReturnsTrue_WhenValueInAllowedSet()
    {
        var page = Page(new() { ["department"] = "sales" });
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("department", ["hr", "sales"])], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeTrue();
    }

    // 値が許可集合外なら不可視。
    [Fact]
    public void Matches_ReturnsFalse_WhenValueNotAllowed()
    {
        var page = Page(new() { ["department"] = "legal" });
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("department", ["hr", "sales"])], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeFalse();
    }

    // フィルタ間は AND: 全キーを満たさなければ不可視。
    [Fact]
    public void Matches_ReturnsFalse_WhenAnyFilterFails()
    {
        var page = Page(new() { ["department"] = "sales", ["confidentiality"] = "restricted" });
        var scope = new AccessScopeResponse("u",
        [
            new AttributeFilter("department", ["sales"]),
            new AttributeFilter("confidentiality", ["public", "internal"])
        ], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeFalse();
    }

    // 属性キーを持たない文書は不一致（欠落は安全側）。
    [Fact]
    public void Matches_ReturnsFalse_WhenAttributeKeyMissing()
    {
        var page = Page(new() { ["confidentiality"] = "public" });
        var scope = new AccessScopeResponse("u",
            [new AttributeFilter("department", ["sales"])], Granted: true);

        AbacPageFilter.Matches(page, scope).Should().BeFalse();
    }

    // Filter は Granted=false で空を返す（deny-by-default）。
    [Fact]
    public void Filter_ReturnsEmpty_WhenNotGranted()
    {
        var pages = new[] { Page(new() { ["x"] = "1" }), Page(new() { ["x"] = "2" }) };
        var scope = new AccessScopeResponse("u", [], Granted: false);

        AbacPageFilter.Filter(pages, scope).Should().BeEmpty();
    }
}
