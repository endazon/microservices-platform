using AwesomeAssertions;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// NFR-18, FR-11, ADR-0044, IADR-0677 (#2411): 経路の設定の起動時検査（純粋な関数と段の検証器）。
[Trait("TestKind", "Unit")]
public class LlmRouteConfigurationCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    private static LlmRoutingOptions Routing(bool selfHostedEnabled = false)
    {
        var r = new LlmRoutingOptions
        {
            Endpoints =
            [
                new() { Name = "claude-managed", Provider = "claude", Tier = ProtectionTier.B, Models = ["m-a", "m-b"], DefaultModel = "m-a" },
                new() { Name = "selfhosted-oss", Provider = "selfhosted", Tier = ProtectionTier.A, Models = ["oss"], DefaultModel = "oss", Enabled = selfHostedEnabled },
            ],
        };
        r.PurposeModels["rag-answer"] = "m-b";
        r.PurposeFallbackModels["rag-answer"] = ["m-a"];
        return r;
    }

    private static ModelPricingOptions Pricing(params string[] models)
    {
        var p = new ModelPricingOptions();
        foreach (var m in models)
            p.Models[m] = [new ModelPriceEntry { InputPerMillionTokens = 1m, OutputPerMillionTokens = 2m }];
        return p;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static IReadOnlyList<LlmRouteFinding> Find(
        LlmRoutingOptions? routing = null, EmbeddingRoutingOptions? embedding = null,
        ModelPricingOptions? pricing = null, IConfiguration? config = null)
        => LlmRouteConfigurationCheck.Find(
            routing ?? Routing(), embedding ?? new EmbeddingRoutingOptions(), pricing ?? Pricing("m-a", "m-b"),
            config ?? Config(), Now);

    [Fact]
    public void 正しい設定では誤りが無い() => Find().Should().BeEmpty();

    [Fact]
    public void 用途の割当とフォールバックと既定のモデルが宣言に無ければ未知のモデル()
    {
        var r = Routing();
        r.PurposeModels["analysis"] = "m-typo";
        r.PurposeFallbackModels["analysis"] = ["m-gone"];
        r.Endpoints[0].DefaultModel = "m-other";

        Find(routing: r).Should().OnlyContain(f => f.Kind == LlmRouteConfigurationCheck.KindUnknownModel)
            .And.HaveCount(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("selfhosted:8080")]
    [InlineData("ftp://host")]
    public void 有効なエンドポイントの宛先が空か絶対URLでなければ誤り(string? baseUrl)
    {
        var findings = Find(routing: Routing(selfHostedEnabled: true), config: Config(("Llm:SelfHosted:BaseUrl", baseUrl)));

        findings.Should().ContainSingle().Which.Kind.Should().Be(LlmRouteConfigurationCheck.KindDestination);
    }

    [Fact]
    public void 無効なエンドポイントの宛先は見ない_有効で宛先があれば誤りでない()
    {
        Find(routing: Routing(selfHostedEnabled: false)).Should().BeEmpty();
        Find(routing: Routing(selfHostedEnabled: true), config: Config(("Llm:SelfHosted:BaseUrl", "http://llm:8000"))).Should().BeEmpty();
    }

    [Fact]
    public void 未知のプロバイダは宛先の誤り()
    {
        var r = Routing();
        r.Endpoints[0].Provider = "claudee";

        Find(routing: r).Should().Contain(f => f.Kind == LlmRouteConfigurationCheck.KindDestination);
    }

    [Fact]
    public void 埋め込みの有効なエンドポイントも宛先を見る_決定的埋め込みは宛先を持たない()
    {
        var e = new EmbeddingRoutingOptions
        {
            Endpoints =
            [
                new() { Name = "voyage-managed", Provider = "voyage", Enabled = true },
                new() { Name = "deterministic-local", Provider = "deterministic-embedding", Enabled = true },
            ],
        };

        Find(embedding: e, config: Config(("Embedding:Voyage:BaseUrl", ""))).Should().ContainSingle()
            .Which.Message.Should().Contain("Embedding:Voyage:BaseUrl");
        Find(embedding: e, config: Config(("Embedding:Voyage:BaseUrl", "https://api.voyageai.com"))).Should().BeEmpty();
    }

    [Fact]
    public void 外部エンドポイントのモデルに起動時点で有効な単価が無ければ単価の欠落_セルフホストは見ない()
    {
        var expired = Pricing("m-a", "m-b");
        expired.Models["m-b"][0].EffectiveTo = Now.AddDays(-1);

        Find(pricing: Pricing("m-a")).Should().ContainSingle().Which.Kind.Should().Be(LlmRouteConfigurationCheck.KindMissingPrice);
        Find(pricing: expired).Should().ContainSingle().Which.Message.Should().Contain("m-b");
        Find(routing: Routing(selfHostedEnabled: true), config: Config(("Llm:SelfHosted:BaseUrl", "http://llm:8000")))
            .Should().BeEmpty("セルフホストのモデル oss に単価は要らない");
    }

    [Theory]
    [InlineData(null, true, LlmRouteValidationMode.Warn)]
    [InlineData("warn", true, LlmRouteValidationMode.Warn)]
    [InlineData("Fail", true, LlmRouteValidationMode.Fail)]
    [InlineData("fial", false, LlmRouteValidationMode.Warn)]
    [InlineData("2", false, LlmRouteValidationMode.Warn)]
    public void 段の値の読み取り(string? raw, bool ok, LlmRouteValidationMode expected)
    {
        LlmRouteConfigurationCheck.TryReadMode(Config(("Llm:RouteValidation:Mode", raw)), out var mode).Should().Be(ok);
        if (ok) mode.Should().Be(expected);
    }

    private static LlmRouteStartupValidator Validator(IConfiguration config, ModelPricingOptions pricing)
        => new(config,
            new TestClaudeProviders.StaticOptionsMonitor<EmbeddingRoutingOptions>(new EmbeddingRoutingOptions()),
            new TestClaudeProviders.StaticOptionsMonitor<ModelPricingOptions>(pricing),
            new FakeTimeProvider(Now));

    [Fact]
    public void 検証器は段1では誤りがあっても成功し_段2では失敗し_段の値が不正なら失敗する()
    {
        var broken = Pricing("m-a"); // m-b の単価が無い

        Validator(Config(), broken).Validate(null, Routing()).Succeeded.Should().BeTrue();
        Validator(Config(("Llm:RouteValidation:Mode", "Fail")), broken).Validate(null, Routing()).Failed.Should().BeTrue();
        Validator(Config(("Llm:RouteValidation:Mode", "Fail")), Pricing("m-a", "m-b")).Validate(null, Routing()).Succeeded.Should().BeTrue();
        Validator(Config(("Llm:RouteValidation:Mode", "fial")), Pricing("m-a", "m-b")).Validate(null, Routing()).Failed.Should().BeTrue();
    }
}
