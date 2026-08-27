using AiAnalysisService.Api.Foundation.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace AiAnalysisService.Api.Tests;

// FR-05, FR-17, ADR-0034, ADR-0035 (#970): RAG 経路の `Authorization` 伝播（方式 A）。
//
// 二段検索の段（グラフ近傍展開）は RetrievalService → GraphService とヘッダを運んで
// ホップごと ABAC を効かせる。**RAG 経路（AiAnalysisService → RetrievalService）が伝播しないと、
// 段を有効化しても展開は常に 0 件だった**（IADR-0263 残件 2）。ここで固定するのは
// 「そのまま転送する」「無ければ付けない」の対である —— 否定形だけでは
// 「常に固定トークンを付ける実装」も「常に付けない実装」も通してしまう。
public class RagOrchestratorAuthPropagationTests
{
    private const string UserToken = "Bearer user-jwt";

    // 陽性対照: 受信リクエストの `Authorization` が RetrievalService への要求へ**そのまま**載る。
    [Fact]
    public async Task AskAsync_ForwardsTheIncomingAuthorizationToRetrieval()
    {
        var routes = new RoutingHandler();
        var orchestrator = new RagOrchestrator(
            new SingleHandlerFactory(routes), AccessorWith(UserToken));

        await orchestrator.AskAsync("質問", "user-1", [],
            ct: TestContext.Current.CancellationToken);

        routes.SearchCalls.Should().Be(1, "検索は 1 回だけ走る（装置の健全性）");
        routes.LastSearchAuthorization.Should().Be(UserToken,
            "本文で scope を渡す方式 B ではなく、ヘッダをそのまま伝播する（方式 A）");
    }

    // 否定形: 受信リクエストにヘッダが無ければ、下流要求にも付けない
    // （縮退の判断とその警告は RetrievalService 側が一元で持つ。ここで捏造しない）。
    [Fact]
    public async Task AskAsync_DoesNotInventAnAuthorizationHeader()
    {
        var routes = new RoutingHandler();
        var orchestrator = new RagOrchestrator(
            new SingleHandlerFactory(routes), AccessorWith(authorization: null));

        await orchestrator.AskAsync("質問", "user-1", [],
            ct: TestContext.Current.CancellationToken);

        routes.SearchCalls.Should().Be(1);
        routes.LastSearchAuthorization.Should().BeNull("無いヘッダは無いまま運ぶ");
    }

    // 要求文脈そのものが無い（accessor 未指定 = 既存テストと同じ直接構築）でも従来どおり動く。
    [Fact]
    public async Task AskAsync_WorksWithoutAnHttpContext()
    {
        var routes = new RoutingHandler();
        var orchestrator = new RagOrchestrator(new SingleHandlerFactory(routes));

        var act = async () => await orchestrator.AskAsync("質問", "user-1", [],
            ct: TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        routes.LastSearchAuthorization.Should().BeNull();
    }

    private static IHttpContextAccessor AccessorWith(string? authorization)
    {
        var ctx = new DefaultHttpContext();
        if (authorization is not null)
            ctx.Request.Headers.Authorization = authorization;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    // パスで応答を出し分ける GraphService/LlmGateway/AuthorizationService の代役。
    //   /authz/scope → 許可スコープ（検索まで到達させる）
    //   /search      → Authorization を記録して空の検索結果
    //   /complete    → 500（LLM 縮退経路。本テストの関心は /search のヘッダだけである）
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public int SearchCalls { get; private set; }
        public string? LastSearchAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/authz/scope")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new AccessScopeResponse("user-1", [], Granted: true)),
                });

            if (path == "/search")
            {
                SearchCalls++;
                LastSearchAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                    ? string.Join(' ', values)
                    : null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new Knowledge.Contracts.Dtos.SearchResponse([], 0, 0)),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    // どの名前にも同じハンドラを返すファクトリ（オーケストレータ単体の試験用）。
    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
