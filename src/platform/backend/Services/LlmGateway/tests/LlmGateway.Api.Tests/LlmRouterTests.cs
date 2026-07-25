using FluentAssertions;
using LlmGateway.Api.Foundation.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Api.Tests;

// FR-11, ADR-0010, 08_data-egress-policy: 機密区分×ティアの越境マトリクスと用途による呼び出し先切替を検証する。
public class LlmRouterTests
{
    private static LlmEndpointOptions Claude(bool enabled = true, int priority = 10) => new()
    {
        Name = "claude-managed",
        Tier = ProtectionTier.B,
        Provider = "claude",
        Enabled = enabled,
        Priority = priority,
        DefaultModel = "claude-opus-5",
        Models = ["claude-fable-5", "claude-opus-5", "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5"],
        // IADR-0022 / 08_data-egress-policy: fable-5 は ZDR 非対応。confidential/restricted では除外される。
        NonZdrModels = ["claude-fable-5"]
    };

    private static LlmEndpointOptions SelfHosted(bool enabled = true, int priority = 20) => new()
    {
        Name = "selfhosted-oss",
        Tier = ProtectionTier.A,
        Provider = "selfhosted",
        Enabled = enabled,
        Priority = priority,
        DefaultModel = "oss-llm",
        Models = ["oss-llm"]
    };

    private static LlmEndpointOptions StandardExternal(bool enabled = true, int priority = 5) => new()
    {
        Name = "standard-external",
        Tier = ProtectionTier.C,
        Provider = "claude",
        Enabled = enabled,
        Priority = priority,
        DefaultModel = "std-model",
        Models = ["std-model"]
    };

    // ADR-0010 / IADR-0022: GitHub Copilot エンドポイント（最難関用途の別経路）。
    // 送信先ティア確定までは安全側でティアC・既定無効として登録する。
    private static LlmEndpointOptions Copilot(bool enabled = false, int priority = 30) => new()
    {
        Name = "copilot-managed",
        Tier = ProtectionTier.C,
        Provider = "copilot",
        Enabled = enabled,
        Priority = priority,
        DefaultModel = "gpt-5",
        Models = ["gpt-5"]
    };

    private static LlmRouter Build(LlmRoutingOptions options)
        => new(Options.Create(options), NullLogger<LlmRouter>.Instance);

    private static LlmRoutingOptions Opts(params LlmEndpointOptions[] endpoints) => new()
    {
        Endpoints = [.. endpoints],
        PurposeModels = new(StringComparer.OrdinalIgnoreCase)
        {
            // ADR-0010 / IADR-0022: 既定 opus / 定型 sonnet・haiku / 最難関 analysis→fable-5。
            ["rag-answer"] = "claude-sonnet-4-6",
            ["analysis"] = "claude-fable-5",
            ["diagram-coding"] = "claude-haiku-4-5",
            // AST/ADR-0011 / IADR-0102: 取引判断は基盤の既定モデル改定に自動追随させず版数を固定する。
            ["trade-decision"] = "claude-opus-4-8",
            ["default"] = "claude-opus-5"
        }
    };

    // 越境マトリクス: public は全ティア許容。
    [Theory]
    [InlineData("public", ProtectionTier.A, true)]
    [InlineData("public", ProtectionTier.C, true)]
    [InlineData("confidential", ProtectionTier.C, false)]
    [InlineData("restricted", ProtectionTier.C, false)]
    [InlineData("confidential", ProtectionTier.B, true)]
    [InlineData("restricted", ProtectionTier.A, true)]
    public void AllowedTiers_FollowsEgressMatrix(string cls, ProtectionTier tier, bool allowed)
    {
        var sensitivity = SensitivityClasses.Parse(cls);
        EgressMatrix.AllowedTiers(sensitivity).Contains(tier).Should().Be(allowed);
    }

    // FR-11: confidential は ティアB（保護契約済み外部API）へ切り替えて送信できる。
    [Fact]
    public void Route_Confidential_SelectsProtectedExternalTierB()
    {
        var router = Build(Opts(Claude(), SelfHosted(enabled: false)));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.B);
        decision.EndpointName.Should().Be("claude-managed");
        decision.Provider.Should().Be("claude");
        // IADR-0022 / 08_data-egress-policy: confidential は ZDR 要件のため ZDR 非対応の fable-5 は除外され、
        // ZDR 対応の既定モデル（opus）へフォールバックする。
        decision.Model.Should().Be("claude-opus-5");
    }

    // IADR-0022 / 08_data-egress-policy: public は ZDR 非要件のため analysis→fable-5（最難関）を選択できる。
    [Fact]
    public void Route_Public_Analysis_AllowsNonZdrFable5()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-fable-5");
    }

    // IADR-0022 / 08_data-egress-policy: restricted も ZDR 要件のため fable-5 を除外し opus へフォールバックする。
    [Fact]
    public void Route_Restricted_Analysis_ExcludesNonZdrFable5()
    {
        var router = Build(Opts(Claude(), SelfHosted(enabled: false)));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Restricted, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.B);
        decision.Model.Should().Be("claude-opus-5");
    }

    // IADR-0022: confidential でも明示要求モデルが ZDR 非対応（fable-5）なら採用せず ZDR 対応へフォールバックする。
    [Fact]
    public void Route_Confidential_IgnoresRequestedNonZdrModel()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "rag-answer", "claude-fable-5"));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().NotBe("claude-fable-5");
        // 用途 rag-answer（ZDR 対応の sonnet）が適格モデルとして選択される。
        decision.Model.Should().Be("claude-sonnet-4-6");
    }

    // IADR-0022: ZDR 要件区分で当該エンドポイントに ZDR 対応モデルが 1 つも無ければ送信を拒否する（安全側）。
    [Fact]
    public void Route_Confidential_WhenAllModelsNonZdr_IsDenied()
    {
        var fableOnly = new LlmEndpointOptions
        {
            Name = "claude-managed",
            Tier = ProtectionTier.B,
            Provider = "claude",
            Enabled = true,
            Priority = 10,
            DefaultModel = "claude-fable-5",
            Models = ["claude-fable-5"],
            NonZdrModels = ["claude-fable-5"]
        };
        var router = Build(Opts(fableOnly));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "analysis"));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("拒否");
    }

    // IADR-0022: 先頭候補（優先度最上位）が ZDR 非対応モデルしか持たなくても、後続候補に
    // 適格モデルがあれば拒否せずそちらを採用する（候補をループして最初の適格候補を使う）。
    [Fact]
    public void Route_Confidential_FallsBackToNextCandidateWhenLeadHasNoZdrModel()
    {
        // 先頭候補（priority 5, ティアB）は fable-5 のみ＝ZDR 要件区分では適格モデル 0 件。
        var fableOnlyLead = new LlmEndpointOptions
        {
            Name = "claude-fable-only",
            Tier = ProtectionTier.B,
            Provider = "claude",
            Enabled = true,
            Priority = 5,
            DefaultModel = "claude-fable-5",
            Models = ["claude-fable-5"],
            NonZdrModels = ["claude-fable-5"]
        };
        // 後続候補（priority 10, ティアA）は ZDR 対応の oss-llm を持つ。
        var router = Build(Opts(fableOnlyLead, SelfHosted(priority: 10)));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.EndpointName.Should().Be("selfhosted-oss");
        decision.Tier.Should().Be(ProtectionTier.A);
        decision.Model.Should().Be("oss-llm");
    }

    // CodeQL(log-forging): purpose に改行・制御文字が含まれても例外なく通常どおりルーティングできる。
    [Fact]
    public void Route_PurposeWithControlChars_StillRoutes()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "analysis\r\nINJECTED log line"));

        // 未知 purpose のため既定モデル（opus）へフォールバックし、送信は許可される。
        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-opus-5");
        // IADR-0022: Reason へ埋め込む purpose も sanitize 済みで、改行・制御文字を含まない
        //（将来 Reason を監査ログへ出力しても偽造経路が再発しない）。
        decision.Reason.Should().NotContain("\n").And.NotContain("\r");
    }

    // CodeQL(cs/log-forging): 明示要求モデルが対応可能なとき、返却 Model は利用者由来の文字列ではなく
    // 設定側（endpoint.Models）の正規文字列を採用する。値そのものは一致するが、テイント源を選択結果へ
    // 持ち込まない（監査ログ偽造の経路を断つ）。挙動として要求モデルが尊重されることを確認する。
    [Fact]
    public void Route_HonorsRequestedModel_ReturnsConfiguredModelValue()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "rag-answer", "claude-haiku-4-5"));

        decision.Model.Should().Be("claude-haiku-4-5");
    }

    // FR-11: 許容ティアに送信可能なエンドポイントが無ければ送信を拒否する（縮退）。
    [Fact]
    public void Route_Confidential_WhenOnlyStandardExternal_IsDenied()
    {
        var router = Build(Opts(StandardExternal()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "analysis"));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("拒否");
    }

    // FR-11: restricted はティアA（セルフホスト）があればそちらへ切り替えられる。
    [Fact]
    public void Route_Restricted_PrefersEnabledSelfHostedWhenHigherPriority()
    {
        // セルフホストを最優先（priority 1）に設定
        var router = Build(Opts(Claude(priority: 10), SelfHosted(priority: 1)));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Restricted, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.A);
        decision.Provider.Should().Be("selfhosted");
        decision.Model.Should().Be("oss-llm");
    }

    // FR-11: internal × ティアC は既定（要承認・未許可）では候補から除外される。
    [Fact]
    public void Route_Internal_TierCRequiresApproval_DeniedByDefault()
    {
        var router = Build(Opts(StandardExternal())); // 唯一の候補がティアC

        var decision = router.Route(new RoutingRequest(SensitivityClass.Internal, "rag-answer"));

        decision.Allowed.Should().BeFalse();
    }

    // FR-11: 明示許可（AllowUnapprovedTierC=true）があれば internal はティアCへ送信できる（要承認フラグ付き）。
    [Fact]
    public void Route_Internal_TierCAllowedWhenApprovalGranted()
    {
        var options = Opts(StandardExternal());
        options.AllowUnapprovedTierC = true;
        var router = Build(options);

        var decision = router.Route(new RoutingRequest(SensitivityClass.Internal, "rag-answer"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.C);
        decision.RequiresApproval.Should().BeTrue();
    }

    // FR-11: 明示要求モデルがエンドポイントで対応可能ならそれを優先する。
    [Fact]
    public void Route_HonorsRequestedModelWhenSupported()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "rag-answer", "claude-haiku-4-5"));

        decision.Model.Should().Be("claude-haiku-4-5");
    }

    // ADR-0010 / IADR-0022: 用途未指定（default）は既定モデル opus を選択する。
    [Fact]
    public void Route_DefaultPurpose_SelectsOpusDefaultModel()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "default"));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-opus-5");
    }

    // AST/ADR-0011, IADR-0102: 取引判断は基盤の既定モデル改定に自動追随しない。用途 trade-decision は
    // ピン留めした claude-opus-4-8 を選択し、既定（claude-opus-5）へ落ちてはならない。
    // ピン留め対象が Models 許可一覧に無いと ResolveModel が黙って DefaultModel へフォールバックするため、
    // 「default と異なる値が返る」ことまで確認して無効化を検知する。
    [Fact]
    public void Route_TradeDecision_PinsOpus48AndDoesNotFollowDefault()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "trade-decision"));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-opus-4-8");
        decision.Model.Should().NotBe("claude-opus-5");
    }

    // AST/ADR-0011, IADR-0102 / IADR-0022: ZDR 要件区分（confidential）でもピン留めは維持される。
    // Opus 4.8 は NonZdrModels に含まれないため ZDR 除外の対象外（fable-5 とは異なる）。
    [Fact]
    public void Route_Confidential_TradeDecision_KeepsPinnedOpus48()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "trade-decision"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.B);
        decision.Model.Should().Be("claude-opus-4-8");
    }

    // AST/ADR-0011 §決定: 報告書生成（report-narrative）は「別扱い。基盤の既定モデルを用いてよい」ため
    // ピン留めせず default へ着地する。取引判断と扱いが異なることを固定する。
    [Fact]
    public void Route_ReportNarrative_FollowsDefaultModel()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Internal, "report-narrative"));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-opus-5");
    }

    // ADR-0010 / IADR-0022: Copilot（ティアC）は confidential では候補にならない（越境マトリクスで C 不可）。
    // 唯一の候補が Copilot のみなら送信を拒否する。
    [Fact]
    public void Route_Confidential_ExcludesCopilotTierC()
    {
        var router = Build(Opts(Copilot(enabled: true)));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "analysis"));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("拒否");
    }

    // ADR-0010 / IADR-0022: Copilot は既定無効。有効な Claude（ティアB）が優先され、無効な Copilot は候補外。
    [Fact]
    public void Route_Public_PrefersClaudeOverDisabledCopilot()
    {
        var router = Build(Opts(Claude(priority: 10), Copilot(enabled: false, priority: 5)));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.Provider.Should().Be("claude");
        decision.EndpointName.Should().Be("claude-managed");
    }

    // 08_data-egress-policy「既定は安全側」: 未指定（null/空）・未知の機密区分は Restricted に倒す。
    [Theory]
    [InlineData(null, SensitivityClass.Restricted)]
    [InlineData("", SensitivityClass.Restricted)]
    [InlineData("  ", SensitivityClass.Restricted)]
    [InlineData("unknown-value", SensitivityClass.Restricted)]
    [InlineData("internal", SensitivityClass.Internal)]
    [InlineData("PUBLIC", SensitivityClass.Public)]
    public void Parse_MapsToSafeSide(string? value, SensitivityClass expected)
        => SensitivityClasses.Parse(value).Should().Be(expected);

    // 複数文書の最高機密区分で判定する。
    [Fact]
    public void Highest_TakesMostSensitive()
        => SensitivityClasses.Highest(["public", "confidential", "internal"])
            .Should().Be(SensitivityClass.Confidential);
}
