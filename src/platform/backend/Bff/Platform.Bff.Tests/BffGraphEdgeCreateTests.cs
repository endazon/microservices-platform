using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace Platform.Bff.Tests;

// FR-17, UC-10, ADR-0034 決定 2・8 (#2107 の分割 A): 利用者による辺の明示的な付与を BFF に開けた
// （`POST /bff/graph/edges` → 後段 `POST /graph/edges` の透過中継）。
//
// 🔴 **後段の状態コードと本文を詰め替えない。** 後段は「権限外・不存在・起点への write 権限なし」を
// すべて 404 に倒している（存在秘匿）。BFF が 403 や 200 へ変えると秘匿が BFF 層で破れる。
// 400（`unknown_edge_type`）・409（`edge_exists`）の本文は画面の文言の根拠なので、本文ごと透過する。
// 🔴 **陽性対照（資格情報と本文が後段へ届く）が無いと、伝播を外す変異が検出できない** ——
// 伝播が切れると後段は anonymous として解決して全件 404 になり、否定形の試験は緑のままである。
public class BffGraphEdgeCreateTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffGraphEdgeCreateTests(BffTestFactory factory) => _factory = factory;

    private static readonly Guid SourceId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid TargetId = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid TypeId = new("bbbbbbbb-0000-0000-0000-0000000000e1");

    private static CreateGraphEdgeRequest Request() => new(SourceId, TargetId, TypeId);

    private HttpClient WriteClient(HttpStatusCode status, string? body)
    {
        _factory.GraphWriteStubStatusCode = status;
        _factory.GraphWriteStubBody = body;
        _factory.GraphWriteStubThrows = false;
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return c;
    }

    // 陽性対照: 201 と本文が透過し、資格情報と要求の本文が後段へ届く。
    [Fact]
    public async Task Create_forwards_credentials_and_body_and_relays_201()
    {
        var edgeId = Guid.NewGuid();
        var client = WriteClient(HttpStatusCode.Created, JsonSerializer.Serialize(
            new GraphEdgeCreatedDto(edgeId, SourceId, TargetId, TypeId, "user"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var res = await client.PostAsJsonAsync("/bff/graph/edges", Request(),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.LastGraphPath.Should().Be("/graph/edges");
        _factory.LastGraphMethod.Should().Be("POST");
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty(
            "伝播しないと GraphService は利用者を anonymous として解決し、作成が静かに全件 404 になる");
        var sent = JsonSerializer.Deserialize<CreateGraphEdgeRequest>(_factory.LastGraphBody!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        sent.Should().Be(Request(), "要求の本文（両端と型）はそのまま後段へ届く");
        var dto = await res.Content.ReadFromJsonAsync<GraphEdgeCreatedDto>(
            TestContext.Current.CancellationToken);
        dto!.Id.Should().Be(edgeId);
        dto.Provenance.Should().Be("user");
    }

    // 後段の 400（`unknown_edge_type`）は本文ごと透過する（型が辞書に無いことを画面へ伝える）。
    [Fact]
    public async Task Backend_400_passes_through_with_its_body()
    {
        var client = WriteClient(HttpStatusCode.BadRequest, """{"error":"unknown_edge_type"}""");

        var res = await client.PostAsJsonAsync("/bff/graph/edges", Request(),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("unknown_edge_type");
    }

    // 🔴 権限外の端点（後段の 404）は 404 のまま返る。403 にしない（存在秘匿。ADR-0034 決定 2・8）。
    [Fact]
    public async Task Backend_404_is_relayed_verbatim()
    {
        var client = WriteClient(HttpStatusCode.NotFound, null);

        var res = await client.PostAsJsonAsync("/bff/graph/edges", Request(),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.LastGraphPath.Should().Be("/graph/edges", "陽性対照: 404 は後段から来ている（BFF の経路が無いのではない）");
    }

    // 409（`edge_exists`）は本文ごと透過する。
    [Fact]
    public async Task Backend_409_passes_through_with_its_body()
    {
        var client = WriteClient(HttpStatusCode.Conflict, """{"error":"edge_exists"}""");

        var res = await client.PostAsJsonAsync("/bff/graph/edges", Request(),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("edge_exists");
    }

    // 未認証は BFF の入口で 401。後段へ行かない。
    [Fact]
    public async Task Unauthenticated_is_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.PostAsJsonAsync("/bff/graph/edges", Request(),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 後段へ到達できないときに成功へ縮退しない（辺が生まれたと誤認させない）。
    [Fact]
    public async Task Backend_unreachable_is_502()
    {
        var client = WriteClient(HttpStatusCode.Created, null);
        _factory.GraphWriteStubThrows = true;

        var res = await client.PostAsJsonAsync("/bff/graph/edges", Request(),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }
}
