using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GraphService.Infrastructure.ExternalServices;
using GraphService.Domain;
using GraphService.Features.AiSuggestions.Generate;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.AiSuggestions.Generate;

// FR-18, ADR-0034 決定 5, ADR-0051 決定 1〜4, ADR-0033 決定 7 (#915):
// **AI 提案の生成 —— スコープ内候補限定の LLM 境界。**
//
// 🔴 **本クラスの主眼は「送信ペイロードの否定形テスト」である**（issue #915 受け入れ基準）。
// **結果のフィルタではなく、送信そのものを検査する** —— ADR-0034 決定 5 が
// 「送信そのものが違反であり、後段のフィルタでは償えない」と定めているためである。
// したがって LLM クライアントは差し替えず、**実際に出ていく HTTP 要求の本文を捕まえる。**
public class AiSuggestionGenerationTests
{
    // 既定 scope は全許可なので、可視性を測るテストは必ず絞る。
    private static AccessScopeResponse InternalOnly()
        => new("test-user", [new AttributeFilter("confidentiality", ["internal"])], true);

    private static GraphDbContext NewDb()
        => new(new DbContextOptionsBuilder<GraphDbContext>()
            .UseInMemoryDatabase($"gen_{Guid.NewGuid():N}").Options);

    private static GraphDocument Doc(Guid id, string title, string conf)
        => GraphDocument.Create(id, title,
            new Dictionary<string, string> { ["confidentiality"] = conf },
            null, DateTimeOffset.UnixEpoch);

    // 送信本文を捕まえるハンドラ。応答は LLM ゲートウェイの契約どおりに組む。
    //
    // 🔴 **生の本文と、復号したプロンプト本文の両方を残す。**
    // System.Text.Json は既定で非 ASCII を \uXXXX へ逃がすため、**生の本文に対する
    // 「日本語の表題を含まない」という assert は、逃がされているというだけで無条件に通る**
    // ——「送っていないから通った」と区別が付かない。**否定形テストが空振りする典型**なので、
    // 表題の検査は必ず復号後（Prompts）に対して行う。ID（ASCII）は生の本文でも見られる。
    private sealed class CapturingHandler(Func<string, string> respondWithText) : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];

        // 送信 JSON の prompt 欄を復号したもの（＝モデルが実際に読む文字列）。
        public List<string> Prompts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add(body);
            Prompts.Add(JsonDocument.Parse(body).RootElement.GetProperty("prompt").GetString() ?? string.Empty);

            var payload = JsonSerializer.Serialize(new CompletionApiResponse(
                respondWithText(body), "test-model", 0, 0));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubSimilarity(params SimilarityCandidate[] candidates) : ISimilarityCandidateSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<SimilarityCandidate>> FindSimilarAsync(
            Guid originDocumentId, int limit, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SimilarityCandidate>>(candidates);
        }
    }

    private sealed class RecordingLlmClient(IReadOnlyList<LlmSuggestionProposal> proposals) : ISuggestionLlmClient
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<LlmSuggestionProposal>> ProposeAsync(
            SuggestionPrompt prompt, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(proposals);
        }
    }

    private static AiSuggestionGenerator Generator(
        GraphDbContext db, ISimilarityCandidateSource similarity, ISuggestionLlmClient llm)
        => new(new EfGraphStore(db), similarity, llm, db, TimeProvider.System);

    private static (AiSuggestionGenerator Generator, CapturingHandler Handler) GatewayGenerator(
        GraphDbContext db, ISimilarityCandidateSource similarity, Func<string, string> respond)
    {
        var handler = new CapturingHandler(respond);
        var client = new LlmGatewaySuggestionClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway/") },
            NullLogger<LlmGatewaySuggestionClient>.Instance);
        return (Generator(db, similarity, client), handler);
    }

    // 起点 1 件・スコープ内候補 1 件・スコープ外候補 1 件を置く。
    private static async Task<(Guid Origin, Guid Visible, Guid Hidden)> SeedAsync(GraphDbContext db)
    {
        var origin = Guid.NewGuid();
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();

        db.Documents.Add(Doc(origin, "起点-社内議事録", "internal"));
        db.Documents.Add(Doc(visible, "候補-社内設計メモ", "internal"));
        // 🔴 スコープ外。**この文書の表題も ID も送信本文に現れてはならない。**
        db.Documents.Add(Doc(hidden, "候補-極秘人事評価", "restricted"));
        db.EdgeTypes.Add(EdgeType.Create(EdgeTypeSeed.DefaultTypeName, EdgeTypeLayer.Core, true, isSeed: true));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (origin, visible, hidden);
    }

    // G-01 🔴 FR-18, ADR-0034 決定 5, ADR-0051 決定 3: **送信ペイロードの否定形。**
    // スコープ外文書の ID・表題が、LLM への送信本文に 1 文字も現れない。
    //
    // **類似度側はスコープを跨いで両方を返している**（ADR-0051 決定 1 が認めた形）。
    // それでも送信物に入らないのは、候補列挙の段で述語が効いているからである。
    [Fact]
    public async Task Out_of_scope_document_never_appears_in_the_llm_request_payload()
    {
        using var db = NewDb();
        var (origin, visible, hidden) = await SeedAsync(db);

        var (generator, handler) = GatewayGenerator(
            db,
            new StubSimilarity(new SimilarityCandidate(visible, 0.8), new SimilarityCandidate(hidden, 0.9)),
            _ => "[]");

        await generator.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        handler.Sent.Should().HaveCount(1, "生成は 1 回の LLM 呼び出しで完結する");
        // ID は ASCII なので生の送信本文で直に見られる。
        handler.Sent[0].Should().NotContain(hidden.ToString(),
            "スコープ外文書の ID が送信本文に現れてはならない（ADR-0034 決定 5）");

        // 表題は復号後で見る（生の本文では \uXXXX へ逃げており、否定形が空振りする）。
        var prompt = handler.Prompts[0];
        prompt.Should().Contain("社内設計メモ",
            "空振り防止の番人 —— 復号が効いていなければ、この行が先に落ちる");
        prompt.Should().NotContain("極秘人事評価",
            "スコープ外文書の表題が送信本文に現れてはならない（送信そのものが違反であり、後段のフィルタでは償えない）");
    }

    // G-01b 🔴 FR-18, ADR-0051 決定 3, IADR-0266 決定 2: **候補列挙の段そのものを固定する。**
    //
    // **G-01（送信ペイロードの否定形）だけでは、この段の述語を外しても落ちない** ——
    // 封（SuggestionPrompt.Seal）が述語を再適用する多層防御になっているためである。
    // **2 つのゲートは別々の性質を担保している**ので、それぞれに対の否定形を置く。
    //   - 本テスト … 絞りが **LLM 呼び出しより前（列挙の段）**にあること（ADR-0051 決定 3）
    //   - G-01     … 送信物にスコープ外が入らないこと（ADR-0034 決定 5）
    [Fact]
    public async Task Candidate_enumeration_returns_no_out_of_scope_node()
    {
        using var db = NewDb();
        var (origin, visible, hidden) = await SeedAsync(db);

        var candidates = await new EfGraphStore(db).EnumerateAuthorizedCandidatesAsync(
            origin, [visible, hidden], InternalOnly(), TestContext.Current.CancellationToken);

        candidates.Select(c => c.DocumentId).Should().BeEquivalentTo([visible],
            "スコープ外の文書は候補列挙の段で落ちる。LLM へ渡してから捨てる形にしない");
    }

    // G-02 FR-18: **陽性対照。** スコープ内候補は送信本文に現れる。
    // これが無いと「常に空を送る」実装が G-01 を通してしまう。
    [Fact]
    public async Task In_scope_candidate_does_appear_in_the_llm_request_payload()
    {
        using var db = NewDb();
        var (origin, visible, hidden) = await SeedAsync(db);

        var (generator, handler) = GatewayGenerator(
            db,
            new StubSimilarity(new SimilarityCandidate(visible, 0.8), new SimilarityCandidate(hidden, 0.9)),
            _ => "[]");

        await generator.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        handler.Sent[0].Should().Contain(visible.ToString());
        handler.Prompts[0].Should().Contain("社内設計メモ");
    }

    // G-04 ADR-0051 決定 2: **件数も存在も漏らさない。**
    // 送信本文にも、落とした候補の件数を表す語が現れない。
    [Fact]
    public async Task Payload_does_not_disclose_how_many_candidates_were_filtered_out()
    {
        using var db = NewDb();
        var (origin, visible, hidden) = await SeedAsync(db);

        var (generator, handler) = GatewayGenerator(
            db,
            new StubSimilarity(new SimilarityCandidate(visible, 0.8), new SimilarityCandidate(hidden, 0.9)),
            _ => "[]");

        await generator.GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        // 候補は 2 件引かれ 1 件が落ちた。**「2」も「1 件除外」も本文に現れない。**
        var prompt = handler.Prompts[0];
        prompt.Should().NotContain("除外");
        prompt.Should().NotContain("filtered");
        // 候補一覧の行数がそのまま可視件数であること（落ちた件数を別に持っていない）。
        prompt.Split("- id: ").Length.Should().Be(3, "起点 1 行 ＋ 可視候補 1 行だけが並ぶ");
    }

    // G-03 FR-18, ADR-0033 決定 7: **生成された提案はすべて pending で入る。**
    [Fact]
    public async Task Generated_suggestions_are_all_pending()
    {
        using var db = NewDb();
        var (origin, visible, _) = await SeedAsync(db);

        var generator = Generator(db, new StubSimilarity(new SimilarityCandidate(visible, 0.8)),
            new RecordingLlmClient([
                new LlmSuggestionProposal(SuggestionKind.Link, visible, "related", null, "似ている"),
                new LlmSuggestionProposal(SuggestionKind.Tag, null, null, "設計", "設計文書である"),
            ]));

        var created = await generator.GenerateAsync(
            origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().NotBeNull().And.HaveCount(2);
        created!.Should().OnlyContain(s => s.State == SuggestionState.Pending);
        db.AiSuggestions.Should().OnlyContain(s => s.State == SuggestionState.Pending);
    }

    // G-07 🔴 FR-18, ADR-0034 決定 5: LLM が**許可集合に無い文書 ID を返しても**提案にならない。
    // 渡していない ID を復唱・幻覚された場合に、越境が提案として実体化する経路を塞ぐ。
    [Fact]
    public async Task Proposal_referring_to_a_document_outside_the_authorized_candidate_set_is_discarded()
    {
        using var db = NewDb();
        var (origin, visible, hidden) = await SeedAsync(db);

        var generator = Generator(db,
            new StubSimilarity(new SimilarityCandidate(visible, 0.8), new SimilarityCandidate(hidden, 0.9)),
            new RecordingLlmClient([
                new LlmSuggestionProposal(SuggestionKind.Link, hidden, "related", null, "似ている"),
            ]));

        var created = await generator.GenerateAsync(
            origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().BeEmpty();
        db.AiSuggestions.Should().BeEmpty();
    }

    // G-05 FR-18, ADR-0033 決定 7: **却下済みの組み合わせは候補に入らない。**
    [Fact]
    public async Task Rejected_pair_is_excluded_from_candidate_enumeration()
    {
        using var db = NewDb();
        var (origin, visible, _) = await SeedAsync(db);
        var type = db.EdgeTypes.Single();
        var rejected = AiSuggestion.CreateLink(origin, visible, type.Id, "根拠", DateTimeOffset.UnixEpoch);
        rejected.TryReject("s", "t", DateTimeOffset.UnixEpoch);
        db.AiSuggestions.Add(rejected);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (generator, handler) = GatewayGenerator(
            db, new StubSimilarity(new SimilarityCandidate(visible, 0.9)), _ => "[]");

        var created = await generator.GenerateAsync(
            origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().BeEmpty();
        handler.Sent.Should().BeEmpty("候補が 1 件も残らなければ LLM を呼ばない");
    }

    // G-06 FR-18: 既に辺がある組み合わせを二重に提案しない。
    [Fact]
    public async Task Already_linked_pair_is_excluded_from_candidate_enumeration()
    {
        using var db = NewDb();
        var (origin, visible, _) = await SeedAsync(db);
        var type = db.EdgeTypes.Single();
        db.Edges.Add(Edge.Create(origin, visible, type.Id, type.IsSymmetric, EdgeProvenance.User));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (generator, handler) = GatewayGenerator(
            db, new StubSimilarity(new SimilarityCandidate(visible, 0.9)), _ => "[]");

        var created = await generator.GenerateAsync(
            origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().BeEmpty();
        handler.Sent.Should().BeEmpty();
    }

    // G-08 🔴 ADR-0034 決定 2: 起点文書がスコープ外なら「見つからない」。**LLM を 1 度も呼ばない。**
    [Fact]
    public async Task Origin_outside_the_scope_yields_not_found_without_calling_the_llm()
    {
        using var db = NewDb();
        var (_, visible, hidden) = await SeedAsync(db);

        var similarity = new StubSimilarity(new SimilarityCandidate(visible, 0.9));
        var (generator, handler) = GatewayGenerator(db, similarity, _ => "[]");

        // hidden（restricted）を起点にする。
        var created = await generator.GenerateAsync(
            hidden, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().BeNull("見えない起点は「存在しない」と区別させない");
        similarity.Calls.Should().Be(0);
        handler.Sent.Should().BeEmpty();
    }

    // G-09 FR-05, ADR-0034: スコープが解決できていなければ（deny-by-default）何もしない。
    [Fact]
    public async Task Ungranted_scope_yields_not_found_without_calling_the_llm()
    {
        using var db = NewDb();
        var (origin, visible, _) = await SeedAsync(db);

        var similarity = new StubSimilarity(new SimilarityCandidate(visible, 0.9));
        var (generator, handler) = GatewayGenerator(db, similarity, _ => "[]");

        var created = await generator.GenerateAsync(
            origin, new AccessScopeResponse("test-user", [], false), TestContext.Current.CancellationToken);

        created.Should().BeNull();
        similarity.Calls.Should().Be(0);
        handler.Sent.Should().BeEmpty();
    }

    // G-11 FR-11, ADR-0025: 縮退した LLM 応答を根拠に使わない（Sent=false / refusal）。
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "refusal")]
    public async Task Degraded_gateway_responses_produce_no_suggestions(bool sent, string? stopReason)
    {
        using var db = NewDb();
        var (origin, visible, _) = await SeedAsync(db);

        var handler = new DegradedHandler(sent, stopReason, visible);
        var client = new LlmGatewaySuggestionClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway/") },
            NullLogger<LlmGatewaySuggestionClient>.Instance);

        var created = await Generator(db, new StubSimilarity(new SimilarityCandidate(visible, 0.9)), client)
            .GenerateAsync(origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().BeEmpty("縮退した応答は提案の根拠にならない");
        db.AiSuggestions.Should().BeEmpty();
    }

    private sealed class DegradedHandler(bool sent, string? stopReason, Guid target) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = $$"""[{"kind":"link","targetDocumentId":"{{target}}","edgeTypeName":"related","rationale":"x"}]""";
            var payload = JsonSerializer.Serialize(new CompletionApiResponse(
                text, "test-model", 0, 0, Sent: sent, StopReason: stopReason));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    // FR-18, ADR-0033 決定 3: ゲートウェイの応答を解釈し、未定義の辺の型は既定型へ倒す。
    [Fact]
    public async Task Unknown_edge_type_from_the_llm_falls_back_to_the_default_type()
    {
        using var db = NewDb();
        var (origin, visible, _) = await SeedAsync(db);
        var defaultTypeId = db.EdgeTypes.Single().Id;

        var (generator, _) = GatewayGenerator(
            db, new StubSimilarity(new SimilarityCandidate(visible, 0.9)),
            _ => $$"""[{"kind":"link","targetDocumentId":"{{visible}}","edgeTypeName":"存在しない型","rationale":"x"}]""");

        var created = await generator.GenerateAsync(
            origin, InternalOnly(), TestContext.Current.CancellationToken);

        created.Should().ContainSingle();
        created![0].EdgeTypeId.Should().Be(defaultTypeId);
    }
}
