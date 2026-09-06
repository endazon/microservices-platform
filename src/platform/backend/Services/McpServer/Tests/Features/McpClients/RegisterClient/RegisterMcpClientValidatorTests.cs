using AwesomeAssertions;
using McpServer.Features.McpClients;
using McpServer.Features.McpClients.RegisterClient;

namespace McpServer.Tests.Features.McpClients.RegisterClient;

// FR-16, UC-09 基本フロー 1, SC-12, 計画 ADR-0024 / ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1 (b)・5・9: `RegisterMcpClientValidator` の単体試験。
//
// 器（HTTP）を通さずに **宣言順・件数・メッセージ・鍵の所在**を固定する。
// 応答としての鍵とメッセージは `McpValidationProblemContractTests` が端点越しに固定しており、
// ここは**その上流**（検証器そのもの）を見る。
public class RegisterMcpClientValidatorTests
{
    private static readonly RegisterMcpClientValidator Validator = new();

    private static RegisterMcpClientRequest Request(
        string clientId = "agent-ok", string kind = "interactive", string? egressTier = null)
        => new(clientId, "表示名", kind, null, egressTier);

    // 陽性対照: 全規則を満たす要求は通る。これが無いと「常に落ちる検証器」と区別できない。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = Validator.Validate(Request(egressTier: "self-hosted"));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // M1: メッセージは**定数とリテラルの両方**へ当てる（定数だけを書き換える退行を止める）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankClientId_FailsWithOriginalMessage(string clientId)
    {
        var result = Validator.Validate(Request(clientId: clientId));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(RegisterMcpClientValidator.ClientIdRequiredMessage);
        RegisterMcpClientValidator.ClientIdRequiredMessage.Should().Be("clientId は必須です。");
    }

    // M2: 補間を含むので定数ではなく関数である。**関数とリテラルの両方**へ当てる。
    [Fact]
    public void InvalidKind_FailsWithOriginalMessage()
    {
        var result = Validator.Validate(Request(kind: "robot"));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(RegisterMcpClientValidator.KindInvalidMessage("robot"));
        RegisterMcpClientValidator.KindInvalidMessage("robot").Should()
            .Be("kind の値 'robot' は不正です（interactive / service-account）。");
    }

    // M3: 同上。
    [Fact]
    public void InvalidEgressTier_FailsWithOriginalMessage()
    {
        var result = Validator.Validate(Request(egressTier: "moon"));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(RegisterMcpClientValidator.EgressTierInvalidMessage("moon"));
        RegisterMcpClientValidator.EgressTierInvalidMessage("moon").Should()
            .Be("egressTier の値 'moon' は不正です。");
    }

    // 🔴 O 軸・C 軸: 3 つとも不正なら失敗は **3 件**あり、**先頭は移送前の最初のガード節**である。
    // 端点が載せるのは `Errors[0]` だけなので、宣言順を入れ替えると応答が変わる。
    [Fact]
    public void AllThreeInvalid_ReportsClientIdFirst()
    {
        var result = Validator.Validate(Request(clientId: " ", kind: "robot", egressTier: "moon"));

        result.Errors.Should().HaveCount(3);
        result.Errors[0].ErrorMessage.Should().Be(RegisterMcpClientValidator.ClientIdRequiredMessage);
        result.Errors[1].ErrorMessage.Should().Be(RegisterMcpClientValidator.KindInvalidMessage("robot"));
        result.Errors[2].ErrorMessage.Should().Be(RegisterMcpClientValidator.EgressTierInvalidMessage("moon"));
    }

    // 🔴 K 軸: **鍵は sink（`McpClientEndpoints.Problem` の `request`）が持ち、検証器は持たない**
    // （[[IADR-0398]] 決定 1 (b)）。したがって `PropertyName` は推論名のままであり、
    // **応答の鍵と一致しない**。ここに `OverridePropertyName` を足すと鍵の正が 2 つになるので落とす。
    [Fact]
    public void Validator_DoesNotOwnTheResponseKey()
    {
        var result = Validator.Validate(Request(clientId: " ", kind: "robot", egressTier: "moon"));

        result.Errors.Select(e => e.PropertyName).Should().Equal(["ClientId", "Kind", "EgressTier"]);
        result.Errors.Select(e => e.PropertyName).Should().NotContain("request");
    }

    // 🔴 G 軸: `TryParseKind` は `Trim().ToLowerInvariant()` してから比較する。
    // 検証器が自前の集合比較を持つと、ここで割れる（**同じ関数を共有している**ことの試験）。
    [Theory]
    [InlineData("interactive")]
    [InlineData("service-account")]
    [InlineData(" Service-Account ")]
    [InlineData("INTERACTIVE")]
    public void KindWithCaseAndSpaces_Passes(string kind)
        => Validator.Validate(Request(kind: kind)).IsValid.Should().BeTrue();

    // 🔴 G 軸: `egressTier` の**未指定・空白は有効**（既定は最も低い保護水準）。
    // `NotEmpty()` へ置き換えるとここで落ちる。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Self-Hosted ")]
    public void MissingOrLooselyWrittenEgressTier_Passes(string? egressTier)
        => Validator.Validate(Request(egressTier: egressTier)).IsValid.Should().BeTrue();
}
