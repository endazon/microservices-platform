using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Platform.Bff.Tests;

// FR-06, UC-03, SC-03, ADR-0014, ADR-0029, [[IADR-0541]] (#1905): `GET /bff/documents/{id}/content` の応答の写しを固定する。
//
// 🔴 #1905 の本質: BFF が本文の代わりに案内文（`# 題名\n\nコンテンツは storage://… から取得します。`・381 バイト）を
//   **200 で返していた**。利用者も監視も壊れていると気付けない。本文が返せないときは理由ごとの状態と問題詳細を返す。
//
// 受け入れ基準（作業仕様書 20261015_1905_bff-document-content）:
//   AC-1 本文を返す / AC-2 本文欠落は 404＋問題詳細 / AC-3 権限外は従来の 404（秘匿・問題詳細なし・後段の本文を引かない）/
//   AC-4 上限超過は 413 / AC-5 引けなかったは 502（どれも案内文の 200 にしない）。
public class BffDocumentContentTests : IClassFixture<BffTestFactory>
{
    private const string Placeholder = "から取得します";
    private readonly BffTestFactory _factory;

    public BffDocumentContentTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
        _factory.StubContentState = Pb.DocumentContentState.Available;
        _factory.StubContentThrows = null;
        _factory.StubContentFound = true;
        _factory.GrpcUserContexts.Clear();
    }

    private static string ContentPath => $"/bff/documents/{BffTestFactory.StubDocumentId}/content";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> GetAsync() => _factory.CreateClient().GetAsync(ContentPath, Ct);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage resp)
    {
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    // AC-1（陽性）: 後段の本文をそのまま返す。
    [Fact]
    public async Task 本文を返す()
    {
        var resp = await GetAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DocumentContentDto>(Ct);
        body!.Markdown.Should().Be(_factory.StubContentMarkdown);
        body.Markdown.Should().NotContain(Placeholder);
        body.Id.Should().Be(BffTestFactory.StubDocumentId);
        body.Title.Should().Be("経費規程 2025");
    }

    // AC-2: 本文の実体が無い（NoSuchKey）・参照が無いは 404 ＋ 問題詳細（理由つき）。🔴 案内文の 200 にしない。
    [Theory]
    [InlineData(Pb.DocumentContentState.Missing, "missing")]
    [InlineData(Pb.DocumentContentState.NotStored, "not_stored")]
    public async Task 本文が無いときは404と問題詳細で案内文を返さない(Pb.DocumentContentState state, string reason)
    {
        _factory.StubContentState = state;

        var resp = await GetAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await ProblemAsync(resp);
        problem.GetProperty("type").GetString().Should().Be(DocumentBodyProblems.BodyUnavailableType);
        problem.GetProperty("reason").GetString().Should().Be(reason);
        problem.GetRawText().Should().NotContain(Placeholder);
    }

    // AC-3: 権限外は従来どおり 404（存在秘匿）。問題詳細を付けない（詳細の 404 と見分けが付かない）。
    // 🔴 本文の rpc を呼ばない —— 判定を通る前に後段の本文を引かない。
    [Fact]
    public async Task 権限外は従来の404で本文を引かない()
    {
        _factory.SearchScopeGranted = false;

        var resp = await GetAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resp.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json", "秘匿の 404 は理由を言わない");
        _factory.GrpcUserContexts.Keys.Should().NotContain("GetDocumentContent");
    }

    // AC-3: 後段の判定で読めない（found=false。AND 合成）も同じ秘匿の 404（問題詳細なし）。
    [Fact]
    public async Task 後段で読めないときも秘匿の404()
    {
        _factory.StubContentFound = false;

        var resp = await GetAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resp.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json");
    }

    // AC-4: 上限超過は 413 ＋ 大きさと上限（切り詰めた本文を返さない）。
    [Fact]
    public async Task 上限を超える本文は413で大きさと上限を返す()
    {
        _factory.StubContentState = Pb.DocumentContentState.TooLarge;

        var resp = await GetAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var problem = await ProblemAsync(resp);
        problem.GetProperty("type").GetString().Should().Be(DocumentBodyProblems.BodyTooLargeType);
        problem.GetProperty("sizeBytes").GetInt64().Should().Be(20L * 1024 * 1024);
        problem.GetProperty("limitBytes").GetInt64().Should().Be(16L * 1024 * 1024);
        problem.TryGetProperty("markdown", out _).Should().BeFalse();
    }

    // AC-5: 引けなかった（宛先未構成の UNAVAILABLE・ストレージ未構成の FAILED_PRECONDITION・一時障害の INTERNAL）は 502。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.FailedPrecondition)]
    [InlineData(StatusCode.Internal)]
    public async Task 引けなかったときは502で案内文を返さない(StatusCode status)
    {
        _factory.StubContentThrows = new RpcException(new Status(status, "stub"));

        var resp = await GetAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await ProblemAsync(resp);
        problem.GetProperty("type").GetString().Should().Be(DocumentBodyProblems.BodyUnreachableType);
        problem.GetRawText().Should().NotContain(Placeholder);
    }

    // 🔴 状態 0（UNSPECIFIED）・未知の値は「分からない応答」。本文として 200 へ畳まない。
    [Fact]
    public async Task 状態が分からない応答は502()
    {
        _factory.StubContentState = Pb.DocumentContentState.Unspecified;

        (await GetAsync()).StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // 利用者文脈を運ぶ（所有者が自分の個人資料の本文を読めるため。#1614 と同じ）。
    [Fact]
    public async Task 本文の読み取りは呼び出し元の利用者を運ぶ()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UsernameHeader, "reader");

        (await client.GetAsync(ContentPath, Ct)).EnsureSuccessStatusCode();

        _factory.GrpcUserContexts["GetDocumentContent"].Should().Be("reader");
    }

    // 上限（後段 16 MiB）は D-1 チャネルの受信上限（既定 64 MiB）に収まる。逆転すると上限ちょうどの本文が
    // RESOURCE_EXHAUSTED（→ 502）に化ける。後段の上限の値は文書サービスの試験が固定する。
    [Fact]
    public void 本文の上限はD1チャネルの受信上限に収まる()
    {
        const long backendLimit = 16L * 1024 * 1024;
        const int envelope = 64 * 1024; // proto の枠・他の項目のための余裕
        (backendLimit + envelope).Should().BeLessThan(DocumentReadGrpcClient.DefaultMaxReceiveMessageSize);
    }
}
