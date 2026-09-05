using AwesomeAssertions;
using LlmGateway.Domain.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace LlmGateway.Tests.Features.Completions.Complete;

// FR-11, ADR-0010: /complete が機密区分・用途に応じて呼び出し先を切り替え、
// 許容ティアが無い場合は送信を拒否（縮退）することを検証する。
// IADR-0110 (#395): メトリクス購読テスト（CompletionMetricsTests）と直列化する。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Integration")]
public class CompletionRoutingEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private record CompletionResponse(
        string Text, string Model, int InputTokens, int OutputTokens,
        bool Sent, string? Endpoint, string? RoutingReason);

    // IADR-0101, FR-11: maxTokens を省略した JSON では、共有契約 CompletionApiRequest の既定値
    // （4096）がプロバイダまで渡ることを固定する。HTTP 経路で実際に効く既定は ILlmProvider 側の
    // CompletionRequest ではなく DTO 側であり（エンドポイントは req.MaxTokens を常に明示的に渡す）、
    // ここが 1024 に戻ると thinking 既定有効モデルで本文が空になる回帰が起きる。
    // レコードのコンストラクタ既定値が System.Text.Json のバインドで尊重されることの担保も兼ねる。
    [Fact]
    public async Task PostComplete_WithoutMaxTokens_PassesContractDefaultToProvider()
    {
        var req = new { Prompt = "要約", Confidentiality = "public", Purpose = "default" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Text.Should().Contain("maxTokens=4096");
    }

    // FR-11: 既定構成（ティアB=claude 有効）では confidential でも保護契約済み外部APIへ送信できる。
    [Fact]
    public async Task PostComplete_Confidential_RoutesToProtectedExternalAndSends()
    {
        var req = new { Prompt = "機密文書の要約", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Endpoint.Should().Be("claude-managed");
        body.Text.Should().NotBeNullOrWhiteSpace();
    }

    // FR-11: Model 未指定なら用途（purpose）に応じてモデルを切り替える（analysis→opus / rag-answer→sonnet / diagram-coding→sonnet）。
    // ADR-0010 / IADR-0022: 既定 opus / 定型 sonnet・haiku。
    // ［2026-08-18 追記 / #850］計画 ADR-0038 決定 1 により最難関 analysis は claude-fable-5 → claude-opus-5 へ改定した。
    // これにより analysis は DefaultModel と同値になり、本ケースだけでは「用途別割当が発火した」ことと
    // 「DefaultModel へ黙って落ちた」ことを区別できない（ADR-0038 §結果 が受け入れたトレードオフ）。区別は
    // PurposeModelsAndFallbacks_AreAllRegisteredInClaudeEndpointModels（T-19。#863 で改名）と
    // LlmRouterTests の合成 config 側が担う。
    // 実運用経路（RagOrchestrator / LlmGatewayDiagramCoder）は Model=null で /complete を呼ぶため、この経路で用途別モデルが発火することを検証する。
    // purpose 値は呼び出し側が送る文字列（ConversionService は "diagram-coding"）と一致させる（設定キー統一のガード）。
    // 検証区分は public のまま据え置く（旧: ZDR 非対応の fable-5 が confidential/restricted で除外されるため。
    // 現在その制約は無いが、区分を変えると本ケースの意味も変わるので #850 では動かさない）。
    [Theory]
    [InlineData("analysis", "claude-opus-5")]
    [InlineData("rag-answer", "claude-sonnet-5")]
    // ［2026-08-21 追記 / #440］計画 06_technical/04_ai-rag-stack（fixed）§変更履歴 2026-08-02 と INDEX 決定 6 により
    // diagram-coding のピンを claude-haiku-4-5 → claude-sonnet-5 へ改定した（質問票 第4回 Q12 =(あ)・planning#83。
    // **単価 3 倍**を受け入れた裁定である）。claude-haiku-4-5 はフォールバック先として利用許可集合に残る。
    [InlineData("diagram-coding", "claude-sonnet-5")]
    public async Task PostComplete_WithoutExplicitModel_SelectsPurposeModel(string purpose, string expectedModel)
    {
        var req = new { Prompt = "要約", MaxTokens = 100, Confidentiality = "public", Purpose = purpose };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        // 用途別モデルが選択される（呼び出し元が既定モデルを固定送信しないため）。
        body.Model.Should().Be(expectedModel);
    }

    // IADR-0022 / 08_data-egress-policy: confidential の analysis は ZDR 対応モデルへ解決したうえで送信する。
    // ［2026-08-18 追記 / #850］計画 ADR-0038 決定 1・2 の反映に伴い改名した（旧名
    // PostComplete_ConfidentialAnalysis_FallsBackToZdrModel）。**この経路で ZDR 除外はもう発火しない** ——
    // analysis の割当自体が ZDR 対応（claude-opus-5）になり、NonZdrModels も空になったためである。
    // 「フォールバックする」という名前のまま残すと、通っている経路と名前が食い違い、空振りしているのに緑という
    // 最も悪い状態になる。よって本テストは「機密区分を上げても analysis の割当が無音で変わらない」ことの回帰と
    // して置き直す。ZDR 除外機構そのものの単体カバレッジは LlmRouterTests の合成 config が持つ。
    [Fact]
    public async Task PostComplete_ConfidentialAnalysis_ResolvesZdrModel()
    {
        var req = new { Prompt = "機密文書の分析", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Endpoint.Should().Be("claude-managed");
        body.Model.Should().Be("claude-opus-5");
        body.Model.Should().NotBe("claude-fable-5");
    }

    // FR-11 / deny-by-default: confidentiality 未指定は安全側（restricted 相当）に倒し、ティアC のみの構成では送信を拒否する。
    [Fact]
    public async Task PostComplete_WithoutConfidentiality_FallsBackToRestrictedAndRefusesTierCOnly()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
                s.Configure<LlmRoutingOptions>(o =>
                {
                    o.AllowUnapprovedTierC = false;
                    o.Endpoints =
                    [
                        new LlmEndpointOptions
                        {
                            Name = "standard-external",
                            Tier = ProtectionTier.C,
                            Provider = "claude",
                            Enabled = true,
                            Priority = 1,
                            DefaultModel = "std",
                            Models = ["std"]
                        }
                    ];
                }))).CreateClient();

        // Confidentiality を指定しない（null）。安全側では restricted 相当 → ティアC 不可で拒否。
        var req = new { Prompt = "分類不明の文書", MaxTokens = 100, Purpose = "analysis" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeFalse();
        body.RoutingReason.Should().Contain("拒否");
    }

    // FR-11: 送信先ティアが許容されない構成（confidential に対しティアCのみ）では送信を拒否する。
    [Fact]
    public async Task PostComplete_Confidential_WhenOnlyStandardExternal_IsRefused()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
                s.Configure<LlmRoutingOptions>(o =>
                {
                    // 越境の許容されないティアC（標準外部API）のみへ差し替える。
                    o.AllowUnapprovedTierC = false;
                    o.Endpoints =
                    [
                        new LlmEndpointOptions
                        {
                            Name = "standard-external", Tier = ProtectionTier.C, Provider = "claude",
                            Enabled = true, Priority = 1, DefaultModel = "std", Models = ["std"]
                        }
                    ];
                }))).CreateClient();

        var req = new { Prompt = "機密文書の要約", MaxTokens = 100, Confidentiality = "confidential", Purpose = "analysis" };
        var response = await client.PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeFalse();          // 外部送信していない（縮退）
        body.RoutingReason.Should().Contain("拒否");
    }

    // T-19, FR-11, ADR-0022 / IADR-0106: 用途別モデルは claude エンドポイントの Models（利用許可集合）に
    // 登録されていなければならない。ResolveModel は eligible.Contains(purposeModel) を条件とするため、
    // Models 未登録の用途別モデルは例外もログも出さずに DefaultModel へフォールバックし、
    // 「追随したつもりで追随していない」状態が無音で成立する（#376 / IADR-0102 で実際に踏んだ罠）。
    // rag-answer 単体ではなく PurposeModels 全体を集合として守り、用途追加のたびの再発を防ぐ。
    //
    // ［2026-08-18 追記 / #863］**射程を PurposeFallbackModels（フォールバック順序）へも広げ、名前も改めた**
    // （旧名 PurposeModels_AreAllRegisteredInClaudeEndpointModels）。計画 ADR-0038 決定 5 は
    // 「フォールバック先は利用許可集合に残す。登録されていなければフォールバックはその場で失敗する」と
    // 述べており、**本ガードが守るべき不変条件は第 1 候補と鎖の要素で同じ**である。
    // **並行するガードを新設せず、既存の 1 本を広げた** —— 同じ不変条件を 2 本で守ると片方が古くなる
    // （#850 が PurposeModels_AreNotListedAsNonZdr で採ったのと同じ形）。
    [Fact]
    public void PurposeModelsAndFallbacks_AreAllRegisteredInClaudeEndpointModels()
    {
        var options = factory.Services.GetRequiredService<IOptions<LlmRoutingOptions>>().Value;
        var claude = options.Endpoints.Single(e => e.Name == "claude-managed");

        foreach (var (purpose, model) in options.PurposeModels)
        {
            claude.Models.Should().Contain(model,
                $"用途 {purpose} の割当モデル {model} が Models 未登録だと DefaultModel へ黙って落ちる");
        }

        // ADR-0038 決定 3・5 (#863): 鎖の要素も同じ許可集合に無ければならない。
        options.PurposeFallbackModels.Should().ContainKey("analysis",
            "計画 ADR-0038 決定 3 が analysis のフォールバック順序を定めている（鎖が消えると決定が無音で失効する）");
        foreach (var (purpose, fallbacks) in options.PurposeFallbackModels)
        {
            foreach (var model in fallbacks)
            {
                claude.Models.Should().Contain(model,
                    $"用途 {purpose} のフォールバック先 {model} が Models 未登録だと、"
                    + "フォールバックはその場で失敗する（ADR-0038 決定 5）");
            }
        }
    }

    // T-25, AST/ADR-0011 / docs/operations/llm-model-pin-runbook.md:
    // **取引判断はフォールバックの対象にしない。** 別モデルで下した判断は再現性・監査可能性を失った
    // 別物であり、Runbook が「別のモデルへ切り替えて取引判断を続けてはならない」と明示している。
    // 実設定に鎖が足されたら落ちる（禁止の記述だけでは破られる、という #382 の懸念への手当て）。
    [Fact]
    public void TradeDecision_HasNoFallbackChainInProductionConfig()
    {
        var options = factory.Services.GetRequiredService<IOptions<LlmRoutingOptions>>().Value;

        options.PurposeFallbackModels.Should().NotContainKey("trade-decision",
            "ピン留めしたモデルが使えないとき、取引判断は実行しない（別モデルへ逃がさない）");
    }

    // AST#571, AST/ADR-0017 決定2: 取引判断の一次スクリーニングも本判断と同様にフォールバック禁止である。
    // 別モデルで下したスクリーニングは、本判断と同じ理由で再現性・監査可能性を失う。
    [Fact]
    public void TradeDecisionScreening_HasNoFallbackChainInProductionConfig()
    {
        var options = factory.Services.GetRequiredService<IOptions<LlmRoutingOptions>>().Value;

        options.PurposeFallbackModels.Should().NotContainKey("trade-decision-screening",
            "一次スクリーニングも本判断と同じ理由でフォールバックしない（AST/ADR-0017 決定2）");
    }

    // T-19, ADR-0022 / IADR-0106: 定型 RAG 回答は Sonnet 5 を選択し、既定（DefaultModel=claude-opus-5）へ
    // 落ちない。ADR-0022（Accepted）の確定値であり、ADR-0025 §決定も他層は Sonnet 5 と明記している。
    [Fact]
    public async Task PostComplete_RagAnswer_SelectsSonnet5AndDoesNotFallBackToDefault()
    {
        var req = new { Prompt = "文書を要約して", MaxTokens = 100, Confidentiality = "public", Purpose = "rag-answer" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Model.Should().Be("claude-sonnet-5");
        body.Model.Should().NotBe("claude-opus-5");  // DefaultModel への無音フォールバックでないこと
    }

    // T-22, FR-11, IADR-0112, AST/04_workflows/03_reporting-cycle: 報告書は方針階層（月報→週報→日報→取引）を
    // なす方針書であり、上位ほど難度が高い。種別ごとに purpose を分けてモデルを割り当てる。
    //
    // 週報は現行の DefaultModel と同値だが、それは default 追随による偶然であり割当として固定されていない。
    // 明示エントリを置かないと default の改定（IADR-0101 が実際に行った操作）で無音に失効する。
    // よって「同値だからテスト不要」ではなく、同値であることを固定する意味がある。
    //
    // IADR-0113 (#309): 月報は claude-fable-5（ZDR 非対応）から claude-opus-5（ZDR 対応の最上位）へ改定した。
    // 機密区分は report-service の既定値（internal）で検証する（従来は fable-5 が ZDR 非対応であることを避けて
    // public で検証していたが、その制約は無くなった）。
    [Theory]
    [InlineData("report-monthly", "claude-opus-5")]
    [InlineData("report-weekly", "claude-opus-5")]
    [InlineData("report-daily", "claude-sonnet-5")]
    public async Task PostComplete_ReportKindPurpose_SelectsKindSpecificModel(string purpose, string expectedModel)
    {
        var req = new { Prompt = "報告書の散文", MaxTokens = 100, Confidentiality = "internal", Purpose = purpose };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Model.Should().Be(expectedModel);
    }

    // T-22, FR-11, IADR-0112, AST/ADR-0011: 取引判断は claude-sonnet-5（利用者の仕様指定）。
    // ADR-0011 の「バージョン固定」原則は維持されており、改定したのはピンの値であってピンする仕組みではない。
    // DefaultModel（claude-opus-5）と異なる値を返すことが、固定が生きている（default に追随していない）証拠になる。
    [Fact]
    public async Task PostComplete_TradeDecision_SelectsSonnet5AndDoesNotFallBackToDefault()
    {
        var req = new { Prompt = "銘柄の売買判断", MaxTokens = 100, Confidentiality = "internal", Purpose = "trade-decision" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Model.Should().Be("claude-sonnet-5");
        body.Model.Should().NotBe("claude-opus-5");   // DefaultModel への無音フォールバックでないこと
        body.Model.Should().NotBe("claude-opus-4-8"); // 旧ピン（IADR-0102）が残っていないこと
    }

    // AST#571, AST/ADR-0014 §決定1・AST/ADR-0017 決定1: 取引判断の一次スクリーニングは本判断とは別の
    // 軽量モデル（claude-haiku-4-5）を用いる。基盤未登録のあいだは本判断の割当（sonnet-5）と照合され
    // 「割当外」と判定され続けていた（二段判断の層別用途登録の実装 ADR）。DefaultModel への無音フォールバック
    // でないことも併せて固定する。
    [Fact]
    public async Task PostComplete_TradeDecisionScreening_SelectsHaiku45AndDoesNotFallBackToDefault()
    {
        var req = new { Prompt = "銘柄の一次絞り込み", MaxTokens = 100, Confidentiality = "internal", Purpose = "trade-decision-screening" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Model.Should().Be("claude-haiku-4-5");
        body.Model.Should().NotBe("claude-opus-5");     // DefaultModel への無音フォールバックでないこと
        body.Model.Should().NotBe("claude-sonnet-5");   // 本判断の割当（trade-decision）と混同していないこと
    }

    // T-23, IADR-0113 (#309), IADR-0022 / 08_data-egress-policy: 報告書の割当モデルは機密区分によって
    // 変わらない（どの区分でも同一モデルへ解決し、送信も成立する）。旧割当 claude-fable-5 は ZDR 非対応で
    // あり confidential 以上で DefaultModel へ**黙って**落ちていた。ZDR 対応モデルへ改定したことで、
    // 呼び出し側が機密区分を上げても割当が無音で変わらないことを固定する。
    [Theory]
    [InlineData("internal")]
    [InlineData("confidential")]
    [InlineData("restricted")]
    public async Task PostComplete_ReportMonthly_KeepsAssignedModelAcrossSensitivities(string confidentiality)
    {
        var req = new { Prompt = "月報の散文", MaxTokens = 100, Confidentiality = confidentiality, Purpose = "report-monthly" };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>(TestContext.Current.CancellationToken);
        body!.Sent.Should().BeTrue();
        body.Endpoint.Should().Be("claude-managed");
        body.Model.Should().Be("claude-opus-5");
        body.Model.Should().NotBe("claude-fable-5");
    }

    // T-23, IADR-0113 (#309): 割当モデルが NonZdrModels に載っていないことを**集合として**固定する。
    // T-19（全 PurposeModels 値 ⊆ Models）と同じ発想の設定ガードで、用途が増えたときの再発を防ぐ。
    // ［2026-08-18 追記 / #850］**射程を report-* から全 PurposeModels へ広げ、名前も改めた**
    // （旧名 ReportPurposeModels_AreNotListedAsNonZdr）。IADR-0113 決定 4 は「analysis は ZDR 非要件区分に
    // 限って fable-5 を使う意図的な例外なので対象に含めない」として射程を report-* に絞っていたが、計画
    // ADR-0038 決定 2（基盤のいかなる用途でも claude-fable-5 を用いない）により **その例外は消滅した**。
    // 例外が無い以上、絞る理由も無い —— 絞ったままだと report-* 以外の用途に非 ZDR モデルを割り当てる
    // 再発を捕まえられない。射程が覆った旨は IADR-0113 §決定 の同日追記に記録した。
    [Fact]
    public void PurposeModels_AreNotListedAsNonZdr()
    {
        var options = factory.Services.GetRequiredService<IOptions<LlmRoutingOptions>>().Value;
        var claude = options.Endpoints.Single(e => e.Name == "claude-managed");

        options.PurposeModels.Should().NotBeEmpty("用途エントリが消えるとガード自体が空振りする");
        foreach (var (purpose, model) in options.PurposeModels)
        {
            claude.NonZdrModels.Should().NotContain(model,
                $"用途 {purpose} の割当モデル {model} が ZDR 非対応だと、機密区分を上げた時点で DefaultModel へ黙って落ちる");
        }
    }
}
