using AwesomeAssertions;
using LlmGateway.Api.Foundation.Routing;

namespace LlmGateway.Api.Tests;

// FR-02, ADR-0016（Issue #98 レビュー対応）: 埋め込みルーティング設定の起動時バリデーション。
// インデックス依存の環境変数上書きによる取り違え（例 Voyage を誤って無効化）を fail-fast することを検証する。
public class EmbeddingRoutingOptionsValidatorTests
{
    private static EmbeddingEndpointOptions Voyage(bool enabled = true) => new()
    {
        Name = "voyage-managed",
        Tier = ProtectionTier.B,
        Provider = "voyage",
        Model = "voyage-3.5",
        Dimensions = 1024,
        Collection = "knowledge_chunks_voyage_3_5",
        Enabled = enabled,
        Priority = 10
    };

    private static EmbeddingEndpointOptions SelfHosted(bool enabled = false) => new()
    {
        Name = "selfhosted-ruri",
        Tier = ProtectionTier.A,
        Provider = "selfhosted-embedding",
        Model = "ruri-v3",
        Dimensions = 768,
        Collection = "knowledge_chunks_ruri_v3",
        Enabled = enabled,
        Priority = 20
    };

    private static ValidateOptionsResultAssertion Validate(params EmbeddingEndpointOptions[] endpoints)
    {
        var result = new EmbeddingRoutingOptionsValidator()
            .Validate(null, new EmbeddingRoutingOptions { Endpoints = [.. endpoints] });
        return new ValidateOptionsResultAssertion(result.Succeeded, result.FailureMessage);
    }

    private record ValidateOptionsResultAssertion(bool Succeeded, string? FailureMessage);

    // 既定構成（Voyage 有効・セルフホスト無効）は妥当。
    [Fact]
    public void Validate_DefaultConfiguration_Succeeds()
        => Validate(Voyage(), SelfHosted()).Succeeded.Should().BeTrue();

    // Voyage 無効でもセルフホストが有効なら既定経路は担保される（未認定時の運用シナリオ）。
    [Fact]
    public void Validate_VoyageDisabledButSelfHostedEnabled_Succeeds()
        => Validate(Voyage(enabled: false), SelfHosted(enabled: true)).Succeeded.Should().BeTrue();

    // Voyage を誤って無効化し、セルフホストも既定無効 → 既定/クエリ経路が皆無 → 起動を失敗させる。
    [Fact]
    public void Validate_AllDefaultPathEndpointsDisabled_Fails()
    {
        var result = Validate(Voyage(enabled: false), SelfHosted(enabled: false));
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("有効なエンドポイントがありません");
    }

    // ティア↔プロバイダの取り違え（並び替え・誤設定）を検知する。
    [Fact]
    public void Validate_TierProviderMismatch_Fails()
    {
        var mismatched = Voyage();
        mismatched.Provider = "selfhosted-embedding"; // ティアB なのにセルフホストプロバイダ
        Validate(mismatched, SelfHosted()).Succeeded.Should().BeFalse();
    }

    // 必須項目欠落（次元 0）を検知する。
    [Fact]
    public void Validate_InvalidDimensions_Fails()
    {
        var bad = Voyage();
        bad.Dimensions = 0;
        Validate(bad, SelfHosted()).Succeeded.Should().BeFalse();
    }
}
