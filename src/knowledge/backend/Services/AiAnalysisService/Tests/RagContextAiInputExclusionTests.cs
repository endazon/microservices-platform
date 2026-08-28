using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace AiAnalysisService.Tests;

// FR-21 受け入れ基準 ⑨, FR-19, FR-11, [[IADR-0283]] 決定 3 (#447):
//
// > 「横断検索に含める」が ON、「AI の入力に含める」が OFF の個人資料は、
// > **検索結果に現れるが RAG 回答のコンテキストには含まれない**
//
// 本テストが測るのは**後半**（RAG 回答のコンテキストに含まれないこと）である。
// 前半（検索結果には現れること）は検索側 `RetrievalService.Api.Tests` の
// `PrivateNoteSearchExposureTests` が測る —— **⑨ は 2 つの経路にまたがる基準であり、
// 片側だけを測ると「どちらでも落とす」実装が通り抜ける。**
//
// 🔴 **否定形と陽性対照を対で置く。** AI 入力 OFF は落ち、ON は入る。
// 片方だけだと「全部落とす」実装も「全部通す」実装も緑になる。
public class RagContextAiInputExclusionTests
{
    private static readonly Guid AiOffChunk = Guid.NewGuid();
    private static readonly Guid AiOnChunk = Guid.NewGuid();
    private static readonly Guid OrgChunk = Guid.NewGuid();

    // 「横断検索に含める」ON・「AI の入力に含める」OFF の個人資料（⑨ の主語そのもの）。
    private static SearchResultDto AiOffPrivateNote() => Chunk(
        AiOffChunk, "AI 入力 OFF の個人資料", "秘密のメモ本文", new Dictionary<string, string>
        {
            [DocumentScopes.Key] = DocumentScopes.PrivateNote,
            ["owner"] = "alice",
            [ConfidentialityLevels.AttributeKey] = ConfidentialityLevels.Restricted,
            [AiInputExposure.AttributeKey] = AiInputExposure.Excluded,
        });

    // 陽性対照: 同じ個人資料で AI 入力だけ ON。
    private static SearchResultDto AiOnPrivateNote() => Chunk(
        AiOnChunk, "AI 入力 ON の個人資料", "共有してよいメモ本文", new Dictionary<string, string>
        {
            [DocumentScopes.Key] = DocumentScopes.PrivateNote,
            ["owner"] = "alice",
            [ConfidentialityLevels.AttributeKey] = ConfidentialityLevels.Restricted,
            [AiInputExposure.AttributeKey] = AiInputExposure.Included,
        });

    // 陽性対照 2: `doc_scope` を持たない既存の組織文書（従来どおり文脈に入る）。
    private static SearchResultDto OrganizationDocument() => Chunk(
        OrgChunk, "組織文書", "社内規程の本文", new Dictionary<string, string>
        {
            [ConfidentialityLevels.AttributeKey] = ConfidentialityLevels.Internal,
        });

    private static SearchResultDto Chunk(
        Guid chunkId, string title, string text, Dictionary<string, string> attributes) =>
        new(chunkId, Guid.NewGuid(), title, text, 0.9f, null, attributes, []);

    // FR-21 ⑨（否定形の核）: AI 入力 OFF の個人資料は**出典にも文脈にも現れない。**
    [Fact]
    public async Task AI入力OFFの個人資料は出典にも文脈にも現れない()
    {
        var routes = new RoutingHandler(
            [OrganizationDocument(), AiOffPrivateNote(), AiOnPrivateNote()]);
        var orchestrator = new RagOrchestrator(new SingleHandlerFactory(routes));

        var answer = await orchestrator.AskAsync("質問", "alice", [],
            ct: TestContext.Current.CancellationToken);

        routes.SearchCalls.Should().Be(1, "検索は 1 回だけ走る（装置の健全性）");
        answer.Citations.Should().NotContain(c => c.ChunkId == AiOffChunk,
            "AI 入力 OFF の個人資料は回答の根拠にならない");
        routes.LastPrompt.Should().NotContain("秘密のメモ本文",
            "本文が LLM へのプロンプトへ入ってはならない");
        routes.LastPrompt.Should().NotContain("AI 入力 OFF の個人資料",
            "タイトルも文脈へ入らない（出典欄はタイトルを載せる）");
    }

    // FR-21 ⑨（陽性対照）: AI 入力 ON の個人資料と組織文書は**入る**。
    // これが無いと「個人資料を全部落とす」実装が上のテストだけで緑になる。
    [Fact]
    public async Task AI入力ONの個人資料と組織文書は出典にも文脈にも入る()
    {
        var routes = new RoutingHandler(
            [OrganizationDocument(), AiOffPrivateNote(), AiOnPrivateNote()]);
        var orchestrator = new RagOrchestrator(new SingleHandlerFactory(routes));

        var answer = await orchestrator.AskAsync("質問", "alice", [],
            ct: TestContext.Current.CancellationToken);

        answer.Citations.Select(c => c.ChunkId).Should().BeEquivalentTo([OrgChunk, AiOnChunk]);
        routes.LastPrompt.Should().Contain("共有してよいメモ本文");
        routes.LastPrompt.Should().Contain("社内規程の本文");
    }

    // FR-21 ⑨, [[IADR-0283]] 決定 2: 🔴 **fail-closed** —— 個人資料でトグル属性が欠落していたら
    // OFF 扱いにする。供給側が既定を書き忘れても、見える側へ倒れない。
    [Fact]
    public async Task トグル属性を持たない個人資料は文脈から外れる()
    {
        var chunkId = Guid.NewGuid();
        var attributeless = Chunk(chunkId, "属性欠落の個人資料", "欠落した資料の本文",
            new Dictionary<string, string>
            {
                [DocumentScopes.Key] = DocumentScopes.PrivateNote,
                ["owner"] = "alice",
            });
        var routes = new RoutingHandler([OrganizationDocument(), attributeless]);
        var orchestrator = new RagOrchestrator(new SingleHandlerFactory(routes));

        var answer = await orchestrator.AskAsync("質問", "alice", [],
            ct: TestContext.Current.CancellationToken);

        answer.Citations.Should().NotContain(c => c.ChunkId == chunkId);
        routes.LastPrompt.Should().NotContain("欠落した資料の本文");
        // 陽性対照: 同じ応答で組織文書は残っている（「全部落ちた」のではない）。
        answer.Citations.Should().Contain(c => c.ChunkId == OrgChunk);
    }

    // FR-21 ⑨: **ストリーミング経路でも同じ**（配線漏れの検出）。
    // 出典イベントは本文より先に送出されるため、ここが漏れると本文生成前に露出する。
    [Fact]
    public async Task ストリーミング経路でもAI入力OFFの個人資料は現れない()
    {
        var routes = new RoutingHandler(
            [OrganizationDocument(), AiOffPrivateNote(), AiOnPrivateNote()]);
        var orchestrator = new RagOrchestrator(new SingleHandlerFactory(routes));

        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync("質問", "alice", [],
            ct: TestContext.Current.CancellationToken))
        {
            events.Add(ev);
        }

        var citations = events.OfType<AskCitationsEvent>().Single().Citations;
        citations.Should().NotContain(c => c.ChunkId == AiOffChunk);
        citations.Select(c => c.ChunkId).Should().BeEquivalentTo([OrgChunk, AiOnChunk],
            "陽性対照も同じイベントで確かめる");
        routes.LastPrompt.Should().NotContain("秘密のメモ本文");
    }

    // FR-11, FR-21 ⑨: 越境判定（文脈の最高機密区分）は**除外後の集合**で測る。
    // 除外前で測ると、**LLM へ渡していない個人資料（restricted）のせいで回答が縮退する。**
    [Fact]
    public async Task 越境判定は除外後の集合で測る()
    {
        // 文脈に残るのは組織文書（internal）だけ。除外されるのは restricted の個人資料。
        var routes = new RoutingHandler([OrganizationDocument(), AiOffPrivateNote()]);
        var orchestrator = new RagOrchestrator(new SingleHandlerFactory(routes));

        await orchestrator.AskAsync("質問", "alice", [],
            ct: TestContext.Current.CancellationToken);

        routes.LastConfidentiality.Should().Be(ConfidentialityLevels.Internal,
            "送っていない資料の機密区分で送信先を決めない");
    }

    // 装置の健全性: 除外がゼロ件のときは検索結果と文脈が一致する（余計に落としていない）。
    [Fact]
    public async Task 除外が無ければ検索結果と文脈は一致する()
    {
        var routes = new RoutingHandler([OrganizationDocument(), AiOnPrivateNote()]);
        var orchestrator = new RagOrchestrator(new SingleHandlerFactory(routes));

        var answer = await orchestrator.AskAsync("質問", "alice", [],
            ct: TestContext.Current.CancellationToken);

        answer.Citations.Should().HaveCount(2);
    }

    // パスで応答を出し分ける AuthorizationService / RetrievalService / LlmGateway の代役。
    //   /authz/scope → 許可スコープ（検索まで到達させる）
    //   /search      → 与えた検索結果をそのまま返す（**絞り込まない** = 検索側の役）
    //   /complete    → プロンプトと機密区分を記録して定型応答
    private sealed class RoutingHandler(IReadOnlyList<SearchResultDto> results) : HttpMessageHandler
    {
        public int SearchCalls { get; private set; }
        public string LastPrompt { get; private set; } = string.Empty;
        public string? LastConfidentiality { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/authz/scope")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new AccessScopeResponse("alice", [], Granted: true)),
                };

            if (path == "/search")
            {
                SearchCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new SearchResponse([.. results], results.Count, 0)),
                };
            }

            if (path is "/complete" or "/complete/stream")
            {
                var body = await request.Content!.ReadFromJsonAsync<CompletionApiRequest>(
                    cancellationToken);
                LastPrompt = body?.Prompt ?? string.Empty;
                LastConfidentiality = body?.Confidentiality;

                if (path == "/complete")
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new CompletionApiResponse(
                            "回答", "test-model", 1, 1, Sent: true)),
                    };

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"delta\":\"回答\",\"done\":false}\n\n"
                        + "data: {\"delta\":\"\",\"done\":true,\"sent\":true,\"model\":\"test-model\"}\n\n",
                        Encoding.UTF8, "text/event-stream"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
