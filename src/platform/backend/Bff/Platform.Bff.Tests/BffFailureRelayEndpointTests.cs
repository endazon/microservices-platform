using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-03, FR-04, FR-07, FR-08, FR-17, NFR-06 (#2280): 利用者の入力を受ける BFF の口は、後段の失敗の本文を作り替えずに返す。
//
// 後段は答えない・保存しない理由を本文で返す（分析・評価の入力検証は `{ "error": "<文>" }`、グラフの近傍は `{ error, message }`）。
// 従前の BFF は状態コードだけを返して本文を捨て、画面に理由が届かなかった（#2109 で直したデータソースの書き込みの残り）。
// 読み取りの口（一覧・個別・集計・辞書）は対象外である（後段が理由の本文を持たない。作業仕様書の母集合の表）。
public class BffFailureRelayEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ErrorBody = """{"error":"分析の指示は 2000 文字以内で入力してください。"}""";
    private const string GraphErrorBody = """{"error":"hops_out_of_range","message":"近傍の深さは 1〜3 で指定してください。"}""";

    public BffFailureRelayEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.DownstreamFailure = null;
        _factory.SearchScopeGranted = true;
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubGraphView = new GraphViewDto([], [], false);
    }

    public static TheoryData<string, string> InputEndpoints => new()
    {
        { "AiAnalysisService", "ask" },
        { "AiAnalysisService", "analyze" },
        { "FeedbackService", "feedback" },
        { "RetrievalService", "search" },
        { "GraphService", "neighbors" },
    };

    private async Task<HttpResponseMessage> SendAsync(string endpoint)
    {
        var client = _factory.CreateClient();
        // GraphService のスタブは資格情報が無いと 401 を返す。他の後段にも同じものを転送する。
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token");
        return endpoint switch
        {
            "ask" => await client.PostAsJsonAsync("/bff/analysis/ask", new { question = "経費精算の締め日は？" }, Ct),
            "analyze" => await client.PostAsJsonAsync("/bff/analysis/analyze",
                new { instruction = "規程を比較して", taskType = "Compare" }, Ct),
            "feedback" => await client.PostAsJsonAsync("/bff/feedback", new FeedbackRequest(Guid.NewGuid(), "up"), Ct),
            "search" => await client.PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 }, Ct),
            "neighbors" => await client.GetAsync($"/bff/graph/{Guid.NewGuid()}/neighbors?hops=2", Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
        };
    }

    // AC (b): 後段の 400 の本文（理由）を、状態コード・content-type とも作り替えずに返す。
    [Theory]
    [MemberData(nameof(InputEndpoints))]
    public async Task 後段の400の本文を透過する(string service, string endpoint)
    {
        var body = service == "GraphService" ? GraphErrorBody : ErrorBody;
        _factory.DownstreamFailure = (service, HttpStatusCode.BadRequest, "application/json", body);
        try
        {
            var resp = await SendAsync(endpoint);

            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            resp.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            (await resp.Content.ReadAsStringAsync(Ct)).Should().Be(body, "答えない・保存しない理由を画面へ届ける");
        }
        finally
        {
            _factory.DownstreamFailure = null;
        }
    }

    // 陽性の対照: 後段が成功すれば従来どおり（失敗の注入が当たっていることの裏付け）。
    [Theory]
    [MemberData(nameof(InputEndpoints))]
    public async Task 後段が成功すれば従来どおり(string service, string endpoint)
    {
        _ = service;
        var resp = await SendAsync(endpoint);

        ((int)resp.StatusCode).Should().BeInRange(200, 299);
    }
}
