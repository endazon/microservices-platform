using System.Net;
using System.Text;
using System.Text.Json;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Tests;

// FR-04, FR-05, FR-07, UC-01, UC-02, ADR-0038, #448: RAG 回答の**契約**を後段まで通して固定する。
//
// **なぜ既存テストでは足りなかったか**（#448 の突き合わせで実測した死角）。
//   ① 既存の縮退テスト（`RagOrchestratorDegradedModelTests`）は**検索が 0 件のスタブ**で走るため、
//      「LLM が落ちても**出典は残る**」（UC-01 例外フロー＝縮退運転）を**一度も確かめていない**。
//      出典を捨てる実装へ変えても、既存テストはすべて緑のままである。
//   ② ABAC の否定形は RetrievalService 側（`HybridSearchEndpointTests`）にはあるが、
//      **RAG 出典の側には無かった**。issue #448 の退行防止は「検索結果・**RAG 出典のどちらにも**
//      現れないこと」を求めている。
//   ③ ゲートウェイへ渡す **`purpose`** を誰も見ていなかった。用途別のモデル割当（ADR-0038:
//      `analysis` → `claude-opus-5`）は**この文字列がキーである**。取り違えても応答は 200 で返り、
//      **静かに別のモデルへ流れる**。
public class RagAnswerContractTests
{
    private const string Sales = "営業部の議事録";
    private const string Hr = "人事部の評価シート";

    // ── FR-05, #448: ABAC の否定形（RAG 出典の側）──────────────────────────

    // 権限外文書が**出典に現れない**。検索スタブは実際に ABAC フィルタを適用するため、
    // 「スコープを渡し忘れた」「スコープを広げた」のどちらでも `人事部の評価シート` が出典に現れる。
    // FR-04, FR-05, UC-01, #448
    [Fact]
    public async Task 権限外文書はRAGの出典に現れない()
    {
        var (orchestrator, gateway) = Create(Abac("department", "sales"));

        var answer = await orchestrator.AskAsync("議事録を要約して", "user-1",
            new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken);

        answer.Citations.Select(c => c.DocumentTitle).Should().Equal([Sales]);
        answer.Citations.Should().NotContain(c => c.DocumentTitle == Hr);
        // 権限外文書の本文が LLM へ渡っていないことも併せて固定する（出典から消えても
        // プロンプトへ残っていれば回答本文として漏れる）。
        gateway.LastPrompt.Should().NotContain(Hr);
    }

    // ストリーミング経路も同じ境界を守る（SC-01 の本文は SSE 経路である）。
    // FR-04, FR-05, UC-01, #448
    [Fact]
    public async Task 権限外文書はストリーミングの出典にも現れない()
    {
        var (orchestrator, _) = Create(Abac("department", "sales"), llmIsStream: true);

        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync("議事録を要約して", "user-1",
            new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken))
            events.Add(ev);

        var citations = events.OfType<AskCitationsEvent>().Should().ContainSingle().Subject.Citations;
        citations.Select(c => c.DocumentTitle).Should().Equal([Sales]);
    }

    // FR-04, FR-05, SC-01, #448: 利用者が指定した対象範囲は**権限を広げない**。
    // 権限の外だけを指す範囲は全体 deny へ倒れ、**検索そのものを呼ばない**（漏えいの試行すらしない）。
    [Fact]
    public async Task 権限外を指す対象範囲では検索を呼ばず出典も返さない()
    {
        var (orchestrator, gateway) = Create(Abac("department", "sales"));

        var answer = await orchestrator.AskAsync("議事録を要約して", "user-1",
            new Dictionary<string, string>(),
            new Dictionary<string, List<string>> { ["department"] = ["hr"] },
            TestContext.Current.CancellationToken);

        answer.Citations.Should().BeEmpty();
        answer.Answer.Should().Be("閲覧権限のある文書が見つかりませんでした。");
        gateway.SearchCalls.Should().Be(0, "権限の外だけを指す範囲は検索へ到達しない");
        gateway.CompletionCalls.Should().Be(0, "LLM へも送らない");
    }

    // ── UC-01 例外フロー, #448: 縮退運転（LLM 不調でも検索結果＝出典は返す）────────

    // 🔴 計画 UC-01 例外フロー「**LLM が不調な場合は検索結果のみを返す（縮退運転）**」。
    // 既存テストはモデル名しか見ておらず、**出典が残ることを誰も固定していなかった**。
    // FR-04, UC-01, #448
    [Fact]
    public async Task LLM不調でも出典は残り縮退文言を返す()
    {
        var (orchestrator, _) = Create(Abac("department", "sales"),
            llmStatus: HttpStatusCode.ServiceUnavailable, llmBody: "unavailable");

        var answer = await orchestrator.AskAsync("議事録を要約して", "user-1",
            new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken);

        answer.Answer.Should().Be("LLM が現在利用できないため、関連文書の一覧を返します。");
        answer.Citations.Select(c => c.DocumentTitle).Should().Equal([Sales]);
        answer.Citations.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.SourceUri),
            "FR-04: 出典は元文書へのリンクを必ず持つ");
    }

    // FR-11 縮退: 機密区分により送信しなかった場合も、出典（＝検索結果）は返す。
    // FR-04, FR-11, UC-01, #448
    [Fact]
    public async Task 越境拒否でも出典は残り縮退文言を返す()
    {
        var (orchestrator, _) = Create(Abac("department", "sales"),
            llmBody: """{"text":"","model":"","inputTokens":0,"outputTokens":0,"sent":false}""");

        var answer = await orchestrator.AskAsync("議事録を要約して", "user-1",
            new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken);

        answer.Answer.Should().Be("機密区分により AI 送信を行わなかったため、関連文書の一覧を返します。");
        answer.Citations.Select(c => c.DocumentTitle).Should().Equal([Sales]);
    }

    // ストリーミングでも、LLM が不調なら出典を先に送ってから理由を本文として流し、done で終える
    // （本文が空のまま done になって理由不明の空白回答が表示されるのを防ぐ）。
    // FR-04, UC-01, IADR-0037, #448
    [Fact]
    public async Task ストリーミングはLLM不調でも出典を送り理由を本文にして完了する()
    {
        var (orchestrator, _) = Create(Abac("department", "sales"),
            llmStatus: HttpStatusCode.ServiceUnavailable, llmBody: "unavailable", llmIsStream: true);

        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync("議事録を要約して", "user-1",
            new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken))
            events.Add(ev);

        events.OfType<AskCitationsEvent>().Should().ContainSingle()
            .Which.Citations.Should().ContainSingle().Which.DocumentTitle.Should().Be(Sales);
        events.OfType<AskTokenEvent>().Select(t => t.Text)
            .Should().Contain("LLM が現在利用できません。");
        events.Last().Should().BeOfType<AskDoneEvent>("ストリームは必ず done で終端する");
    }

    // ── ADR-0038, #448: 用途（purpose）の写像 ──────────────────────────────

    // 🔴 用途別のモデル割当（`Llm:Routing:PurposeModels`）は**この文字列がキーである**。
    // ADR-0038 は `analysis` → `claude-opus-5` を確定しており、AI 分析の経路が別の用途名を
    // 送ると**割当が丸ごと効かなくなる**（応答は 200 のままなので気付けない）。
    // FR-07, UC-02, ADR-0038, #448
    [Fact]
    public async Task AI分析は用途analysisでゲートウェイを呼ぶ()
    {
        var (orchestrator, gateway) = Create(Abac("department", "sales"));

        await orchestrator.AnalyzeAsync(
            new AnalysisTaskRequest("2 つの議事録を比較して", AnalysisTaskType.Compare),
            "user-1", new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        gateway.LastPurpose.Should().Be("analysis");
        gateway.LastModel.Should().BeNull("モデルはゲートウェイが用途から決める（呼び出し側で固定しない）");
    }

    // 質問回答は `rag-answer`。**分析と同じ用途名にしない**（同じにすると用途別の割当・
    // フォールバック順序・可観測性がすべて 1 本に潰れる）。
    // FR-04, UC-01, ADR-0038, #448
    [Fact]
    public async Task 質問回答は用途ragAnswerでゲートウェイを呼ぶ()
    {
        var (orchestrator, gateway) = Create(Abac("department", "sales"));

        await orchestrator.AskAsync("議事録を要約して", "user-1",
            new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken);

        gateway.LastPurpose.Should().Be("rag-answer");
    }

    // ── FR-07, #448: 分析・比較・抽出の 3 種別が後段まで届く ────────────────

    // FR-07 は「分析・比較・抽出」の 3 種別を要求する。プロンプト組み立ての単体テストはあるが、
    // **オーケストレータ経由で実際にゲートウェイへ届く**ことは固定されていなかった。
    // FR-07, UC-02, SC-08, #448
    [Theory]
    [InlineData(AnalysisTaskType.Analyze, "分析")]
    [InlineData(AnalysisTaskType.Compare, "比較")]
    [InlineData(AnalysisTaskType.Extract, "抽出")]
    public async Task 分析比較抽出の種別がプロンプトへ反映される(AnalysisTaskType type, string keyword)
    {
        var (orchestrator, gateway) = Create(Abac("department", "sales"));

        await orchestrator.AnalyzeAsync(
            new AnalysisTaskRequest("指示文", type),
            "user-1", new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        gateway.LastPrompt.Should().Contain(keyword);
        gateway.LastPrompt.Should().Contain(Sales, "分析も検索結果を根拠にする");
    }

    // ── 足場 ────────────────────────────────────────────────────────────

    private static AccessScopeResponse Abac(string key, params string[] values)
        => new("user-1", [new AttributeFilter(key, values.ToList())], true);

    private static (RagOrchestrator, RecordingGatewayFactory) Create(
        AccessScopeResponse abac,
        string llmBody = """{"text":"回答本文 [1]","model":"claude-sonnet-5","inputTokens":1,"outputTokens":2,"sent":true}""",
        HttpStatusCode llmStatus = HttpStatusCode.OK,
        bool llmIsStream = false)
    {
        var factory = new RecordingGatewayFactory(abac, llmBody, llmStatus, llmIsStream);
        return (new RagOrchestrator(factory), factory);
    }

    // 認可・検索・LLM の 3 依存を 1 つのハンドラで担う。
    // **検索は本物のように ABAC フィルタを適用する** —— 「スコープを渡したか」ではなく
    // 「権限外文書が出典に出ないか」を見るためである。
    private sealed class RecordingGatewayFactory(
        AccessScopeResponse abac, string llmBody, HttpStatusCode llmStatus, bool llmIsStream)
        : IHttpClientFactory
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        // 固定コーパス（属性 department で権限が分かれる 2 文書）。
        private static readonly (string Title, string Department, string Text)[] Corpus =
        [
            (Sales, "sales", "営業部の週次議事録である。"),
            (Hr, "hr", "人事評価の個票である。"),
        ];

        public int SearchCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public string? LastPurpose { get; private set; }
        public string? LastModel { get; private set; }
        public string LastPrompt { get; private set; } = string.Empty;

        public HttpClient CreateClient(string name)
            => new(new Handler(this, name)) { BaseAddress = new Uri("http://localhost") };

        private async Task<HttpResponseMessage> HandleAsync(string name, HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);

            switch (name)
            {
                case "AuthorizationService":
                    return Ok(JsonSerializer.Serialize(abac, Json));

                case "RetrievalService":
                    {
                        SearchCalls++;
                        var search = JsonSerializer.Deserialize<SearchRequest>(body, Json)!;
                        var hits = Corpus
                            .Where(d => Allows(search.Scope, d.Department))
                            .Select((d, i) => new SearchResultDto(
                                Guid.NewGuid(), Guid.NewGuid(), d.Title, d.Text, 1f - (i * 0.1f),
                                $"s3://docs/{d.Department}.md",
                                new Dictionary<string, string> { ["department"] = d.Department }, []))
                            .ToList();
                        return Ok(JsonSerializer.Serialize(new SearchResponse(hits, hits.Count, 1), Json));
                    }

                default:
                    {
                        CompletionCalls++;
                        var completion = JsonSerializer.Deserialize<CompletionApiRequest>(body, Json)!;
                        LastPurpose = completion.Purpose;
                        LastModel = completion.Model;
                        LastPrompt = completion.Prompt;
                        return new HttpResponseMessage(llmStatus)
                        {
                            Content = new StringContent(llmBody, Encoding.UTF8,
                                llmIsStream ? "text/event-stream" : "application/json"),
                        };
                    }
            }
        }

        // 検索側（RetrievalService / Qdrant）と同じ意味論: フィルタ間は AND、値集合内は OR、
        // スコープ未指定・不許可は何も返さない（deny-by-default）。
        private static bool Allows(AccessScope? scope, string department)
        {
            if (scope is not { GrantsAccess: true })
                return false;
            return scope.Filters.All(f =>
                !string.Equals(f.Key, "department", StringComparison.OrdinalIgnoreCase)
                || f.AllowedValues.Contains(department, StringComparer.OrdinalIgnoreCase));
        }

        private static HttpResponseMessage Ok(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private sealed class Handler(RecordingGatewayFactory owner, string name) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => owner.HandleAsync(name, request, ct);
        }
    }
}
