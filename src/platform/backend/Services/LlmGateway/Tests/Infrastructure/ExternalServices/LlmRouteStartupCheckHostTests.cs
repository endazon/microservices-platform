using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// NFR-18, FR-11, ADR-0010, ADR-0044, IADR-0677 (#2411): 経路の設定（用途のモデルの割当・上流の宛先・単価表）の起動時検査を、
// 本物のホスト（Program.cs の配線）で固定する。
//
// 🔴 **段 1（既定 Warn）は起動を止めない。段 2（Fail）は誤りがあれば起動を止める。**
// 設定キーは配備が書く外部の契約なので、定数を引かずに綴りで書く。
[Trait("TestKind", "Integration")]
public class LlmRouteStartupCheckHostTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    private static Exception? StartupFailure(WebApplicationFactory<Program> app)
    {
        try
        {
            using var _ = app.CreateClient();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // 誤り 3 種のそれぞれを 1 つだけ持つ設定。
    public static TheoryData<string, string, string, string> Faults => new()
    {
        // 未知のモデル: 用途の割当がどのエンドポイントの Models にも無い。
        { "unknown-model", "Llm:Routing:PurposeModels:rag-answer", "claude-sonnet-9-9", "claude-sonnet-9-9" },
        // 空の宛先: 有効にしたセルフホストの宛先が空。
        { "empty-destination", "Llm:Routing:Endpoints:2:Enabled", "true", "Llm:SelfHosted:BaseUrl" },
        // 単価の欠落: 有効な外部エンドポイントのモデルに単価が無い（区間が起動時点を含まない）。
        { "missing-price", "Llm:Pricing:Models:claude-haiku-5-5:0:EffectiveTo", "2000-01-01T00:00:00Z", "claude-haiku-5-5" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void 段2では誤りがあると起動を止め_理由に誤りの対象を名指しする(string fault, string key, string value, string named)
    {
        using var app = With(("Llm:RouteValidation:Mode", "Fail"), (key, value));

        var failure = StartupFailure(app);

        failure.Should().NotBeNull($"{fault} は段 2 で起動を止める");
        failure!.ToString().Should().Contain(named);
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public void 既定の段では誤りがあっても起動する(string fault, string key, string value, string named)
    {
        _ = named;
        using var app = With((key, value));

        StartupFailure(app).Should().BeNull($"{fault} は段 1 では警告だけ");
    }

    [Fact]
    public void 段2でも既定の設定は誤りが無く起動する()
    {
        using var app = With(("Llm:RouteValidation:Mode", "Fail"));

        StartupFailure(app).Should().BeNull();
    }
}
