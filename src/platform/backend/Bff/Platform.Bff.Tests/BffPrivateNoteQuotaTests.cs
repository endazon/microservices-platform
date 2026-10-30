using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Bff.Tests;

// FR-19, NFR-27, SC-17 (#2330): **個人資料の保存容量（使用量・上限）の照会と変更の BFF の経路。**
//
// 🔴 **システム管理者だけ**（`AdminOnly`。後段も `AdminOnly` で、二重の守りになる）。運用者・一般利用者は 403 で、後段へ行かない。
// 🔴 **2 つの数（と割合）だけを透過する** —— 題名・件数・中身は応答に無い（後段の `PrivateNoteUsageDto`）。
// 資格情報を後段へ伝播し、後段の 400（範囲外）は本文ごと透過する。陽性対照（管理者の要求が資格情報と本文つきで後段へ届く）を置く。
//
// 共有の器（BffTestFactory）は他の作業と交差するので触らない。後段（DocumentService）の HttpClient だけを本クラスで差し替える。
public class BffPrivateNoteQuotaTests(BffTestFactory baseFactory) : IClassFixture<BffTestFactory>
{
    private sealed class Recorder : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = """{"usedBytes":1536,"limitBytes":1073741824,"percent":0}""";
        public bool Throws { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastMethod = request.Method.Method;
            LastAuthorization = request.Headers.Authorization?.ToString()
                ?? (request.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (Throws) throw new HttpRequestException("document unreachable");
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private (WebApplicationFactory<Program> App, Recorder Doc) App()
    {
        var doc = new Recorder();
        var app = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient("DocumentService")
                .ConfigurePrimaryHttpMessageHandler(() => doc)));
        return (app, doc);
    }

    private static HttpClient Client(WebApplicationFactory<Program> app, string? roles = null)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        if (roles is not null) c.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return c;
    }

    // 陽性対照: システム管理者は利用者の使用量と上限を照会でき、資格情報が後段へ届く。2 つの数（と割合）だけが返る。
    [Fact]
    public async Task システム管理者は使用量と上限を照会でき資格情報が後段へ届く()
    {
        var (app, doc) = App();

        var res = await Client(app).GetAsync("/bff/private-notes/quotas/alice", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        doc.LastPath.Should().Be("/private-notes/quotas/alice");
        doc.LastMethod.Should().Be("GET");
        doc.LastAuthorization.Should().NotBeNullOrEmpty("後段も AdminOnly であり、資格情報が無いと 401 になる");
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["usedBytes", "limitBytes", "percent"], "題名・件数・中身は返らない");
    }

    // システム管理者は上限を変更できる（0 を含む）。要求の本文がそのまま後段へ届く。
    [Theory]
    [InlineData(0L)]
    [InlineData(5L * 1024 * 1024 * 1024)]
    public async Task システム管理者は上限を変更でき本文が後段へ届く(long limitBytes)
    {
        var (app, doc) = App();

        var res = await Client(app).PutAsJsonAsync("/bff/private-notes/quotas/alice",
            new SetPrivateNoteQuotaRequest(limitBytes), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        doc.LastPath.Should().Be("/private-notes/quotas/alice");
        doc.LastMethod.Should().Be("PUT");
        JsonSerializer.Deserialize<SetPrivateNoteQuotaRequest>(doc.LastBody!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Be(new SetPrivateNoteQuotaRequest(limitBytes));
    }

    // 🔴 システム管理者でない（運用者・一般利用者）は 403 で、後段へ行かない（照会・変更とも）。
    [Theory]
    [InlineData("platform-operator", "GET")]
    [InlineData("platform-user", "GET")]
    [InlineData("platform-operator", "PUT")]
    [InlineData("platform-user", "PUT")]
    public async Task システム管理者でなければ403で後段へ行かない(string role, string method)
    {
        var (app, doc) = App();
        var client = Client(app, role);

        var res = method == "GET"
            ? await client.GetAsync("/bff/private-notes/quotas/alice", TestContext.Current.CancellationToken)
            : await client.PutAsJsonAsync("/bff/private-notes/quotas/alice",
                new SetPrivateNoteQuotaRequest(0), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        doc.LastPath.Should().BeNull("管理者でない要求は後段へ届かない");
    }

    // 未認証は BFF の入口で 401。
    [Fact]
    public async Task 未認証は401()
    {
        var (app, doc) = App();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.GetAsync("/bff/private-notes/quotas/alice", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        doc.LastPath.Should().BeNull();
    }

    // 後段の 400（範囲外）は本文ごと透過する。
    [Fact]
    public async Task 後段の400は本文ごと透過する()
    {
        var (app, doc) = App();
        doc.Status = HttpStatusCode.BadRequest;
        doc.Body = """{"errors":{"limitBytes":["保存容量の上限は 0 バイト以上 1099511627776 バイト（1 TB）以下です。"]}}""";

        var res = await Client(app).PutAsJsonAsync("/bff/private-notes/quotas/alice",
            new SetPrivateNoteQuotaRequest(-1), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("limitBytes");
    }

    // 後段へ到達できなければ 502（成功へ縮退しない）。
    [Fact]
    public async Task 後段へ到達できなければ502()
    {
        var (app, doc) = App();
        doc.Throws = true;

        var res = await Client(app).GetAsync("/bff/private-notes/quotas/alice", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // 利用者名の記号は後段の経路で 1 つの区切りとして運ぶ（`/` で別の経路へ化けない）。
    [Fact]
    public async Task 利用者名はエスケープして後段へ運ぶ()
    {
        var (app, doc) = App();

        var res = await Client(app).GetAsync("/bff/private-notes/quotas/a%20b%40example", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        doc.LastPath.Should().Be("/private-notes/quotas/a%20b%40example");
    }

    // ── #2348: 一括の照会（SC-17 の一覧の N+1 を解く）──────────────────────────

    // 陽性対照: システム管理者の一括の照会は、本文と資格情報ごと後段の `POST /private-notes/quotas/lookup` へ 1 本で届く。
    // 応答は利用者名と 2 つの数（と割合）だけを透過する。
    [Fact]
    public async Task システム管理者の一括の照会は本文と資格情報ごと後段へ1本で届く()
    {
        var (app, doc) = App();
        doc.Body = """{"items":[{"ownerId":"alice","usedBytes":1536,"limitBytes":1073741824,"percent":0}]}""";

        var res = await Client(app).PostAsJsonAsync("/bff/private-notes/quotas/lookup",
            new PrivateNoteQuotaLookupRequest(["alice", "bob"]), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        doc.LastPath.Should().Be("/private-notes/quotas/lookup");
        doc.LastMethod.Should().Be("POST");
        doc.LastAuthorization.Should().NotBeNullOrEmpty("後段も AdminOnly");
        JsonSerializer.Deserialize<PrivateNoteQuotaLookupRequest>(doc.LastBody!,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .OwnerIds.Should().Equal("alice", "bob");
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["ownerId", "usedBytes", "limitBytes", "percent"], "題名・件数・中身は返らない");
    }

    // 🔴 システム管理者でない（運用者・一般利用者）は 403 で、後段へ行かない。
    [Theory]
    [InlineData("platform-operator")]
    [InlineData("platform-user")]
    public async Task 一括の照会もシステム管理者でなければ403で後段へ行かない(string role)
    {
        var (app, doc) = App();

        var res = await Client(app, role).PostAsJsonAsync("/bff/private-notes/quotas/lookup",
            new PrivateNoteQuotaLookupRequest(["alice"]), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        doc.LastPath.Should().BeNull("管理者でない要求は後段へ届かない");
    }

    // 後段の 400（件数・名前の検証）は本文ごと透過する。
    [Fact]
    public async Task 一括の照会の後段の400は本文ごと透過する()
    {
        var (app, doc) = App();
        doc.Status = HttpStatusCode.BadRequest;
        doc.Body = """{"errors":{"ownerIds":["1〜1000 件の、空白でない 256 文字以下の利用者名を指定してください。"]}}""";

        var res = await Client(app).PostAsJsonAsync("/bff/private-notes/quotas/lookup",
            new PrivateNoteQuotaLookupRequest([]), TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("ownerIds");
    }
}
