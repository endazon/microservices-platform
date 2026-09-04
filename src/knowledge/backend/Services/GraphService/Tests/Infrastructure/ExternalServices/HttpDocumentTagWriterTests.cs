using System.Net;
using System.Text;
using AwesomeAssertions;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-18, ADR-0063 決定 1〜3, IADR-0364 決定 1・2 (#1187 / #1014):
// DocumentService へ向く 2 つのアダプタの**写像**を `HttpMessageHandler` 層で固定する。
//
// 🔴 **資格情報の転送は陽性対照つきで固定する。** 転送を外しても本サービスのテストは緑のまま
// （記録スタブは資格を見ない）で、実配備で後段が匿名として拒み承認が静かに全件 404 になる。
[Trait("TestKind", "Unit")]
public class HttpDocumentTagWriterTests
{
    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://document-service/") };
    }

    private sealed class StubHttpContextAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static HttpContext ContextWithAuthorization(string? value)
    {
        var ctx = new DefaultHttpContext();
        if (value is not null) ctx.Request.Headers.Authorization = value;
        return ctx;
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpDocumentTagWriter Writer(CapturingHandler handler, HttpContext? ctx)
        => new(new StubHttpClientFactory(handler), new StubHttpContextAccessor(ctx),
            NullLogger<HttpDocumentTagWriter>.Instance);

    // 🔴 承認者本人の `Authorization` がそのまま後段へ載る（方式 A）。パスと本文も固定する。
    [Fact]
    public async Task Forwards_the_approver_authorization_header_to_the_document_service()
    {
        var handler = new CapturingHandler(_ => Status(HttpStatusCode.OK));
        var documentId = Guid.NewGuid();

        var outcome = await Writer(handler, ContextWithAuthorization("Bearer approver-token"))
            .AddTagAsync(documentId, "経理", TestContext.Current.CancellationToken);

        outcome.Should().Be(TagWriteOutcome.Applied);
        handler.Last!.Method.Should().Be(HttpMethod.Post);
        handler.Last.RequestUri!.AbsolutePath.Should().Be($"/documents/{documentId}/tags");
        handler.Last.Headers.TryGetValues("Authorization", out var values).Should().BeTrue(
            "転送しないと後段は匿名として拒み、承認が静かに全件 404 になる");
        values!.Single().Should().Be("Bearer approver-token");
        // System.Text.Json は非 ASCII を \uXXXX へ逃がすので、生の本文ではなく復号した値を見る。
        System.Text.Json.JsonDocument.Parse(handler.LastBody!).RootElement
            .GetProperty("name").GetString().Should().Be("経理");
    }

    // 陰性対照: 要求に資格情報が無ければ何も載せない（でっち上げない）。
    [Fact]
    public async Task Does_not_invent_credentials_when_the_request_has_none()
    {
        var handler = new CapturingHandler(_ => Status(HttpStatusCode.OK));

        await Writer(handler, ContextWithAuthorization(null))
            .AddTagAsync(Guid.NewGuid(), "経理", TestContext.Current.CancellationToken);

        handler.Last!.Headers.Contains("Authorization").Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, TagWriteOutcome.UnknownTag)]
    [InlineData(HttpStatusCode.NotFound, TagWriteOutcome.NotWritable)]
    [InlineData(HttpStatusCode.InternalServerError, TagWriteOutcome.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TagWriteOutcome.Unavailable)]
    public async Task Maps_the_document_service_status_to_an_outcome(HttpStatusCode status, TagWriteOutcome expected)
    {
        var handler = new CapturingHandler(_ => Status(status));

        var outcome = await Writer(handler, ContextWithAuthorization("Bearer t"))
            .AddTagAsync(Guid.NewGuid(), "経理", TestContext.Current.CancellationToken);

        outcome.Should().Be(expected);
    }

    // 到達できない・タイムアウトは Unavailable（呼び出し側が 502 にする。成功へ縮退しない）。
    [Fact]
    public async Task Connection_failure_is_unavailable_not_applied()
    {
        var handler = new CapturingHandler(_ => throw new HttpRequestException("refused"));

        var outcome = await Writer(handler, ContextWithAuthorization("Bearer t"))
            .AddTagAsync(Guid.NewGuid(), "経理", TestContext.Current.CancellationToken);

        outcome.Should().Be(TagWriteOutcome.Unavailable);
    }

    // ── 辞書の読み取り（IADR-0364 決定 2） ──────────────────────────────────

    private static HttpTagDictionaryReader Reader(CapturingHandler handler)
        => new(new StubHttpClientFactory(handler), NullLogger<HttpTagDictionaryReader>.Instance);

    // 内部口のパスは受け口（DocumentService `TagNamesEndpoint.NamesPath`）と文字列一致で固定する。
    // 🔴 **利用者の資格情報を載せない**（読み取り主体は本サービス自身）。
    [Fact]
    public async Task Reads_names_from_the_internal_path_without_user_credentials()
    {
        var handler = new CapturingHandler(_ => Status(HttpStatusCode.OK, """{"names":["経理","規程"]}"""));

        var names = await Reader(handler).ReadNamesAsync(TestContext.Current.CancellationToken);

        names.Should().BeEquivalentTo(["経理", "規程"]);
        handler.Last!.RequestUri!.AbsolutePath.Should().Be("/internal/tags/names");
        handler.Last.Headers.Contains("Authorization").Should().BeFalse();
    }

    // 🔴 fail-closed: 引けなければ **null**（空集合へ縮退しない）。
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Non_success_yields_null_not_an_empty_dictionary(HttpStatusCode status)
    {
        var handler = new CapturingHandler(_ => Status(status));

        var names = await Reader(handler).ReadNamesAsync(TestContext.Current.CancellationToken);

        names.Should().BeNull("空集合は『辞書が空』であり『分からない』とは別の事実である");
    }

    [Fact]
    public async Task Connection_failure_yields_null()
    {
        var handler = new CapturingHandler(_ => throw new HttpRequestException("refused"));

        var names = await Reader(handler).ReadNamesAsync(TestContext.Current.CancellationToken);

        names.Should().BeNull();
    }

    // 陽性対照の対: 辞書が本当に空なら**空集合**が返る（null と区別できる）。
    [Fact]
    public async Task An_empty_dictionary_is_an_empty_set_not_null()
    {
        var handler = new CapturingHandler(_ => Status(HttpStatusCode.OK, """{"names":[]}"""));

        var names = await Reader(handler).ReadNamesAsync(TestContext.Current.CancellationToken);

        names.Should().NotBeNull().And.BeEmpty();
    }
}
