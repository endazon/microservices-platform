using AwesomeAssertions;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Domain.Routing;

// FR-11, ADR-0010, 08_data-egress-policy: 機密区分×ティアの越境マトリクスと用途による呼び出し先切替を検証する。
//
// ［2026-08-18 追記 / #850］本ファイルの合成 config は **本番設定（appsettings.json）の写しではない**。
// 計画 ADR-0038 決定 1・2 により本番の analysis は claude-opus-5 で、claude-fable-5 は Models からも
// NonZdrModels からも外れた。それでも本ファイルは claude-fable-5 を合成 config に**意図的に残す** ——
// 外すと ZDR 除外機構（LlmRouter.EligibleModels / EgressMatrix.RequiresZeroDataRetention）を発火させる
// 唯一の単体カバレッジが失われ、除外系テストが空振りしたまま緑になるためである（#850 の明示指定）。
// 実効の割当は appsettings.json を正とし、ここの値を本番値として読まないこと。
//
// ［2026-08-21 追記 / #440・planning#426］**意図的な乖離は PurposeModels に限る。**
// PurposeFallbackModels は本番と**同じキー集合・同じ値**に揃える（下の該当箇所を参照）——
// 揃えない運用にした結果、diagram-coding の鎖を写し忘れたまま緑だった実例がある。
// **この 2 つの扱いの違いを混同しないこと。**
[Trait("TestKind", "Unit")]
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
        // ADR-0022 / IADR-0106: rag-answer は claude-sonnet-5。sonnet-4-6 は明示要求の呼び出し側を
        // 壊さないため許可集合に残す（Models は「割当」ではなく「利用を許可するモデル集合」）。
        Models = ["claude-fable-5", "claude-opus-5", "claude-opus-4-8", "claude-sonnet-5", "claude-sonnet-4-6", "claude-haiku-4-5"],
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
            // ADR-0010 / IADR-0022: 既定 opus / 定型 sonnet・haiku。
            // 最難関 analysis→fable-5 は **合成 config 固有の値**であり、本番は claude-opus-5 である
            // （ADR-0038 決定 1 / #850）。ZDR 除外を発火させるためにここでは旧値を保つ（ファイル冒頭の追記を参照）。
            // ADR-0022 / IADR-0106: 定型 RAG 回答は Sonnet 5（計画側 Accepted の確定値）。
            ["rag-answer"] = "claude-sonnet-5",
            ["analysis"] = "claude-fable-5",
            ["diagram-coding"] = "claude-haiku-4-5",
            // IADR-0112 決定1 / AST/04_workflows/03_reporting-cycle: 報告書は方針階層（月報→週報→日報→取引）を
            // なす方針書であり、上位ほど難度が高い。種別ごとに purpose を分けて割り当てる。
            // report-weekly は default と同値だが、明示エントリが無いと default 改定で無音に失効する。
            // IADR-0113 (#309): 月報は ZDR 対応の最上位 claude-opus-5（旧 claude-fable-5 は ZDR 非対応）。
            ["report-monthly"] = "claude-opus-5",
            ["report-weekly"] = "claude-opus-5",
            ["report-daily"] = "claude-sonnet-5",
            // AST/ADR-0011 / IADR-0102: 取引判断は基盤の既定モデル改定に自動追随させず版数を固定する。
            // IADR-0112 決定3: ピンの値を claude-sonnet-5 へ改定した（固定する仕組みは維持）。
            ["trade-decision"] = "claude-sonnet-5",
            ["default"] = "claude-opus-5"
        },
        // ADR-0038 決定 3 (#863): 用途別のフォールバック順序（第 2 候補以降）。**本番 appsettings.json と
        // 同じキー集合・同じ値である**（PurposeModels と違い、ここは意図的な乖離を置かない）。
        // **trade-decision は意図的に鎖を持たない**（AST/ADR-0011 / llm-model-pin-runbook）。
        // ［2026-08-21 / #440・planning#426 裁定 (a)］default / rag-answer の鎖が確定したため追加した。
        // 従前は「計画 ADR-0038 §未決事項で未確定のため置かない」としており、その典拠は根拠を失った。
        // なお diagram-coding は本番に鎖があるのにここへ写し忘れていた（同時に是正）。
        PurposeFallbackModels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["analysis"] = ["claude-sonnet-5"],
            ["diagram-coding"] = ["claude-haiku-4-5"],
            ["default"] = ["claude-sonnet-5"],
            ["rag-answer"] = ["claude-haiku-4-5"]
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
        // IADR-0106: Sonnet 5 も ZDR 対応（30 日保持要件は fable-5 / mythos-5 のみ）のため除外されない。
        decision.Model.Should().Be("claude-sonnet-5");
    }

    // T-19, ADR-0022 / IADR-0106: 定型 RAG 回答は Sonnet 5 を選択し、DefaultModel（claude-opus-5）へ
    // 落ちない。Models 未登録だと ResolveModel が黙って DefaultModel へフォールバックするため、
    // 「用途別モデルが選ばれたこと」と「既定へ落ちていないこと」を両方固定する（#376 / IADR-0102 の罠）。
    [Fact]
    public void Route_RagAnswer_PinsSonnet5AndDoesNotFallBackToDefault()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "rag-answer"));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-sonnet-5");
        decision.Model.Should().NotBe("claude-opus-5");
    }

    // T-19, IADR-0106: ZDR 要件区分（restricted）でも Sonnet 5 は除外されず維持される。
    [Fact]
    public void Route_Restricted_RagAnswer_KeepsSonnet5()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Restricted, "rag-answer"));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-sonnet-5");
    }

    // T-22, IADR-0112 決定1: 報告書は種別ごとに別モデルへ解決される（月報/週報=最上位 / 日報=定型）。
    // IADR-0113 (#309): 月報は ZDR 対応の最上位 claude-opus-5 へ改定した。週報と同値になるが、
    // 非 ZDR の claude-fable-5 を除いた集合の最上位が opus-5 である以上これが上位方針書に対する最善である。
    // 日報が別モデルへ解決されること（3 種別が 1 モデルへ潰れていないこと）は引き続き固定する。
    [Theory]
    [InlineData("report-monthly", "claude-opus-5")]
    [InlineData("report-weekly", "claude-opus-5")]
    [InlineData("report-daily", "claude-sonnet-5")]
    public void Route_ReportKindPurpose_ResolvesKindSpecificModel(string purpose, string expected)
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, purpose));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be(expected);
    }

    // T-23, IADR-0113 (#309): 報告書用途は機密区分によらず同一モデルへ解決する。
    // 旧割当 claude-fable-5 は ZDR 非対応（NonZdrModels）であり confidential 以上で EligibleModels から
    // 除外され DefaultModel へ黙って落ちていた（IADR-0112 決定2 が既知事実として固定していた挙動）。
    // ZDR 対応モデルへ改定したことで、呼び出し側の機密区分設定に割当が左右されないことを固定する。
    [Theory]
    [InlineData("report-monthly")]
    [InlineData("report-weekly")]
    [InlineData("report-daily")]
    public void Route_ReportKindPurpose_ResolvesSameModelAcrossSensitivities(string purpose)
    {
        var router = Build(Opts(Claude()));

        var baseline = router.Route(new RoutingRequest(SensitivityClass.Internal, purpose));

        baseline.Allowed.Should().BeTrue();
        baseline.Model.Should().NotBe("claude-fable-5");

        foreach (var sensitivity in new[] { SensitivityClass.Public, SensitivityClass.Confidential, SensitivityClass.Restricted })
        {
            var decision = router.Route(new RoutingRequest(sensitivity, purpose));

            decision.Allowed.Should().BeTrue();
            decision.Model.Should().Be(baseline.Model,
                $"用途 {purpose} の割当モデルが機密区分 {sensitivity} で無音に変わってはならない");
        }
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

    // AST/ADR-0011, IADR-0102 / IADR-0112 決定3: 取引判断は基盤の既定モデル改定に自動追随しない。用途
    // trade-decision はピン留めした claude-sonnet-5 を選択し、既定（claude-opus-5）へ落ちてはならない。
    // ピン留め対象が Models 許可一覧に無いと ResolveModel が黙って DefaultModel へフォールバックするため、
    // 「default と異なる値が返る」ことまで確認して無効化を検知する。
    // IADR-0112: ピンの値を claude-opus-4-8 から改定した。固定する仕組み（明示エントリ）は維持されている。
    [Fact]
    public void Route_TradeDecision_PinsSonnet5AndDoesNotFollowDefault()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "trade-decision"));

        decision.Allowed.Should().BeTrue();
        decision.Model.Should().Be("claude-sonnet-5");
        decision.Model.Should().NotBe("claude-opus-5");
        decision.Model.Should().NotBe("claude-opus-4-8"); // 旧ピン（IADR-0102）が残っていないこと
    }

    // AST/ADR-0011, IADR-0102 / IADR-0022: ZDR 要件区分（confidential）でもピン留めは維持される。
    // Sonnet 5 は NonZdrModels に含まれないため ZDR 除外の対象外（fable-5 とは異なる）。
    [Fact]
    public void Route_Confidential_TradeDecision_KeepsPinnedSonnet5()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "trade-decision"));

        decision.Allowed.Should().BeTrue();
        decision.Tier.Should().Be(ProtectionTier.B);
        decision.Model.Should().Be("claude-sonnet-5");
    }

    // IADR-0112 決定1: 旧来の単一用途 report-narrative はエントリを持たず default へ着地する（従来どおり）。
    // AST が種別ごとの purpose（report-daily/weekly/monthly）へ移行するまでの非破壊性、および
    // LlmGateway:Purpose を明示設定した既存デプロイのために、未知 purpose のフォールバックを維持する。
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

    // --- ADR-0038 決定 3・5 (#863) / IADR-0225: 用途別フォールバック順序の解決 ------------------

    // T-25, ADR-0038 決定 3: analysis の判定結果はフォールバック順序（第 2 候補 claude-sonnet-5）を伴う。
    // 順序はルーターが決めるものであり、呼び出し側（CompletionEndpoints）はそれを順に試すだけである。
    [Fact]
    public void Route_Analysis_CarriesConfiguredFallbackChain()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "analysis"));

        decision.Allowed.Should().BeTrue();
        decision.Fallbacks.Should().Equal("claude-sonnet-5");
        decision.Fallbacks.Should().NotContain(decision.Model!, "第 1 候補と同じモデルへ 2 回投げない");
    }

    // T-25, ADR-0038 決定 5: 鎖の要素が Models（利用許可集合）に無ければ鎖から落とす。
    // 落とさずに残すと「フォールバックしたつもりで、その場で失敗する」状態になる（決定 5 の警句）。
    [Fact]
    public void Route_DropsFallbackModelThatIsNotInEndpointModels()
    {
        var options = Opts(Claude());
        options.PurposeFallbackModels["analysis"] = ["claude-not-registered", "claude-sonnet-5"];
        var router = Build(options);

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "analysis"));

        decision.Fallbacks.Should().Equal("claude-sonnet-5");
        decision.Fallbacks.Should().NotContain("claude-not-registered");
    }

    // T-25, IADR-0022 / ADR-0038 決定 5: ZDR 要件区分では非 ZDR モデルは鎖からも落ちる。
    // 第 1 候補と同じ適格性判定（EligibleModels）を通す —— 鎖だけ ZDR 除外を素通りすると、
    // 400 系の失敗をきっかけに越境統制が破れる経路になる。
    [Fact]
    public void Route_Confidential_DropsNonZdrFallbackModel()
    {
        var options = Opts(Claude());
        options.PurposeFallbackModels["rag-answer"] = ["claude-fable-5", "claude-haiku-4-5"];
        var router = Build(options);

        var confidential = router.Route(new RoutingRequest(SensitivityClass.Confidential, "rag-answer"));
        var publicRoute = router.Route(new RoutingRequest(SensitivityClass.Public, "rag-answer"));

        confidential.Fallbacks.Should().Equal("claude-haiku-4-5");   // 非 ZDR の fable-5 は落ちる
        publicRoute.Fallbacks.Should().Equal("claude-fable-5", "claude-haiku-4-5"); // ZDR 非要件では残る
    }

    // T-25, AST/ADR-0011 / docs/operations/llm-model-pin-runbook.md: **取引判断はフォールバックしない。**
    // 別モデルで下した判断は再現性・監査可能性を失った別物であり、Runbook が明示的に禁じている。
    // 鎖を「書いていない」ことが挙動として担保されていることを固定する（設定を足せば破れるため）。
    [Fact]
    public void Route_TradeDecision_HasNoFallbackChain()
    {
        var router = Build(Opts(Claude()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Public, "trade-decision"));

        decision.Model.Should().Be("claude-sonnet-5");
        decision.Fallbacks.Should().BeEmpty("ピン留めしたモデルが使えないとき別モデルへ切り替えてはならない");
    }

    // 鎖を持たない用途は空である。null と空を呼び出し側に区別させない。
    // ［2026-08-21 / #440・planning#426 裁定 (a)］題材を default から report-weekly へ移した
    // —— default に鎖が確定したため、default のままでは「鎖が無い用途」の題材にならない。
    [Fact]
    public void Route_PurposeWithoutChain_HasEmptyFallbacks()
    {
        var router = Build(Opts(Claude()));

        router.Route(new RoutingRequest(SensitivityClass.Public, "report-weekly")).Fallbacks.Should().BeEmpty();
    }

    // 送信拒否（縮退）でも鎖は空である（呼び出していないのだから落ちる先も無い）。
    [Fact]
    public void Route_WhenDenied_HasEmptyFallbacks()
    {
        var router = Build(Opts(StandardExternal()));

        var decision = router.Route(new RoutingRequest(SensitivityClass.Confidential, "analysis"));

        decision.Allowed.Should().BeFalse();
        decision.Fallbacks.Should().BeEmpty();
    }

    // 複数文書の最高機密区分で判定する。
    [Fact]
    public void Highest_TakesMostSensitive()
        => SensitivityClasses.Highest(["public", "confidential", "internal"])
            .Should().Be(SensitivityClass.Confidential);
}
