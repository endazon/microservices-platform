using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients;
using McpServer.Features.Tools;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;

namespace McpServer.Tests;

// FR-16: データ越境ポリシー（機密区分 × 送信先ティア）の文書単位判定
// （計画 ADR-0024 §4 / 06_technical/08_data-egress-policy §越境マトリクス）。
public class EgressPolicyTests
{
    // FR-16: 越境マトリクスの各行（計画 08_data-egress-policy）。
    [Theory]
    [InlineData("public", EgressTier.SelfHosted, true)]
    [InlineData("public", EgressTier.ProtectedExternal, true)]
    [InlineData("public", EgressTier.StandardExternal, true)]
    [InlineData("internal", EgressTier.SelfHosted, true)]
    [InlineData("internal", EgressTier.ProtectedExternal, true)]
    [InlineData("confidential", EgressTier.SelfHosted, true)]
    [InlineData("confidential", EgressTier.ProtectedExternal, true)]
    [InlineData("confidential", EgressTier.StandardExternal, false)]
    [InlineData("restricted", EgressTier.SelfHosted, true)]
    [InlineData("restricted", EgressTier.ProtectedExternal, true)]
    [InlineData("restricted", EgressTier.StandardExternal, false)]
    public void 越境マトリクスどおりに判定する(string confidentiality, EgressTier tier, bool expected)
        => EgressPolicy.CanSendBody(confidentiality, tier).Should().Be(expected);

    // FR-16: 「条件付き可（要承認）」は承認経路が無い間は**不可へ倒す**（計画 §基本原則「既定は安全側」）。
    [Fact]
    public void 条件付き可は承認経路が無い間は不可に倒す()
        => EgressPolicy.CanSendBody("internal", EgressTier.StandardExternal).Should().BeFalse();

    // FR-16: 機密区分の欠落・未知の値はセルフホスト以外へ送らない（安全側）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-class")]
    public void 機密区分が不明なら外部へは送らない(string? confidentiality)
    {
        EgressPolicy.CanSendBody(confidentiality, EgressTier.SelfHosted).Should().BeTrue();
        EgressPolicy.CanSendBody(confidentiality, EgressTier.ProtectedExternal).Should().BeFalse();
        EgressPolicy.CanSendBody(confidentiality, EgressTier.StandardExternal).Should().BeFalse();
    }
}
