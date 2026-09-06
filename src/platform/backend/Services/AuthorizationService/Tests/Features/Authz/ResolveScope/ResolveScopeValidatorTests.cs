using AuthorizationService.Domain;
using AuthorizationService.Features.Authz.ResolveScope;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, FR-21, UC-05, 計画 ADR-0004 / ADR-0036 D-07 / ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1 (b)・9: `ResolveScopeValidator` の単体試験。
//
// 応答としての鍵（`errors`）とメッセージは `AuthzValidationProblemContractTests` が端点越しに
// 固定しており、ここは**その上流**（検証器そのもの）を見る。
public class ResolveScopeValidatorTests
{
    private static readonly ResolveScopeValidator Validator = new();

    private static AccessScopeRequest Request(string action)
        => new("u1", new Dictionary<string, string>(), action);

    // 陽性対照: 値域内の action はすべて通る。**値域は `PolicyAction.All` から引く** ——
    // ここへ書き写すと、値域が増えたときに試験だけが古くなる。
    [Fact]
    public void EveryKnownAction_Passes()
    {
        foreach (var action in PolicyAction.All)
            Validator.Validate(Request(action)).IsValid.Should().BeTrue($"'{action}' は値域内である");
    }

    // A4: メッセージは**`static readonly` の定数とリテラルの両方**へ当てる。
    [Fact]
    public void UnknownAction_FailsWithOriginalMessage()
    {
        var result = Validator.Validate(Request("delete"));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(ResolveScopeValidator.ActionInvalidMessage);
        ResolveScopeValidator.ActionInvalidMessage.Should()
            .Be("action は read / analyze / manage / write のいずれかである必要があります。");
    }

    // 🔴 G 軸: `PolicyAction.IsValid` は `All.Contains` の**完全一致**である
    // （McpServer の `TryParseKind` と違い `Trim` も小文字化もしない）。
    // 検証器が寛容な述語へ置き換わるとここで落ちる。
    [Theory]
    [InlineData("")]
    [InlineData("Read")]
    [InlineData(" read ")]
    [InlineData("delete")]
    public void ActionOutsideTheDomain_Fails(string action)
        => Validator.Validate(Request(action)).IsValid.Should().BeFalse();

    // 🔴 K 軸: **鍵は sink（`AuthzEndpoints.ValidationProblem` の `errors`）が持ち、検証器は持たない**
    // （[[IADR-0398]] 決定 1 (b)）。`OverridePropertyName` を足すと鍵の正が 2 つになるので落とす。
    [Fact]
    public void Validator_DoesNotOwnTheResponseKey()
    {
        var result = Validator.Validate(Request("delete"));

        result.Errors[0].PropertyName.Should().Be("Action");
        result.Errors[0].PropertyName.Should().NotBe("errors");
    }
}
