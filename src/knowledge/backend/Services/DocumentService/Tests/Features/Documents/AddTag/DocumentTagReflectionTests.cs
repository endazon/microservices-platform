using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents.AddTag;

// FR-18, SC-03, SC-05, SC-09, ADR-0063 決定 1〜3, ADR-0036 D-07, IADR-0364 (#1187 / #1014):
// **AI タグ提案の承認の反映先** `POST /documents/{id}/tags`。
//
// 🔴 **陰性は陽性対照と対で置く。** 「常に 404」「常に 400」の実装でも陰性だけなら緑になる。
// 本クラスは同じ経路で (a) 所有者が成功する (b) 管理者が成功する を陽性対照として持つ。
//
// 🔴 **辞書照合の変異試験の対象である**（#1014 受け入れ基準 3・4）。`AddTag/Endpoint.cs` の
// `UnknownTagsProblem` 分岐を外すと `Unknown_tag_is_rejected_and_nothing_is_attached` が落ちる
// （辞書に無い名前は識別子へ解決できないので、そのまま進めると保存で落ちるか空の識別子を付ける）。
[Trait("TestKind", "Integration")]
public class DocumentTagReflectionTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient ClientAs(string? user = null, params string[] roles)
    {
        var client = factory.CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    // 辞書へ登録する（管理者限定の口）。各テストが自分の名前を使う（DB はクラス内で共有される）。
    private async Task<string> RegisterTagAsync()
    {
        var name = UniqueName("tag");
        var resp = await ClientAs(roles: "platform-admin")
            .PostAsJsonAsync("/tags", new CreateTagRequest(name), TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return name;
    }

    // **その利用者として作成する**（ADR-0060 決定 3）。所有者は「誰が作ったか」で決まる。
    private async Task<DocumentDto> CreateOwnedAsync(string owner)
    {
        var resp = await ClientAs(user: owner).PostAsJsonAsync("/documents", new
        {
            title = $"反映先 {Guid.NewGuid():N}",
            attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken))!;
    }

    // **`owner` を持たない文書**（取り込み文書の `owner=system` と同じく、所有者ベースでは誰も書けない）。
    private async Task<DocumentDto> CreateOwnerlessAsync()
    {
        var client = ClientAs();
        client.DefaultRequestHeaders.Add(TestAuthHandler.NoNameHeader, "1");
        var resp = await client.PostAsJsonAsync("/documents", new
        {
            title = $"取り込み文書 {Guid.NewGuid():N}",
            attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken))!;
        dto.Attributes.Should().NotContainKey("owner", "前提: 所有者ベースでは誰も書けない文書である");
        return dto;
    }

    private static Task<HttpResponseMessage> AddAsync(HttpClient client, Guid id, string name)
        => client.PostAsJsonAsync($"/documents/{id}/tags", new AddDocumentTagRequest(name),
            TestContext.Current.CancellationToken);

    private async Task<DocumentDto> GetAsync(Guid id)
    {
        var resp = await ClientAs().GetAsync($"/documents/{id}", TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken))!;
    }

    private RecordingMessageBus Bus => factory.Services.GetRequiredService<RecordingMessageBus>();

    // 1187-1 陽性対照（①）: 所有者が辞書内のタグを足すと、文書に付き、版が 1 つ進み、DocumentUpdated が出る。
    [Fact]
    public async Task Owner_can_attach_a_dictionary_tag_and_the_document_is_republished()
    {
        var tag = await RegisterTagAsync();
        var doc = await CreateOwnedAsync("alice");
        var before = Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == doc.Id);

        var resp = await AddAsync(ClientAs(user: "alice", roles: "viewer"), doc.Id, tag);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "ロールを持たない所有者でも①で通る");
        var after = await GetAsync(doc.Id);
        after.Tags.Should().Contain(tag);
        after.Version.Should().Be(doc.Version + 1, "利用者の意思による内容変更なので版が進む");
        Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == doc.Id)
            .Should().Be(before + 1, "射影（Qdrant / Wiki.js）が追随するために再発行する");
        Bus.PublishedOf<DocumentUpdated>().Last(e => e.DocumentId == doc.Id).Tags.Should().Contain(tag);
    }

    // 1187-2: **冪等**。二度目は 200 だが版は進まず、イベントも増えない。
    [Fact]
    public async Task Attaching_the_same_tag_twice_is_idempotent()
    {
        var tag = await RegisterTagAsync();
        var doc = await CreateOwnedAsync("alice");
        var alice = ClientAs(user: "alice", roles: "viewer");
        (await AddAsync(alice, doc.Id, tag)).StatusCode.Should().Be(HttpStatusCode.OK);
        var once = await GetAsync(doc.Id);
        var events = Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == doc.Id);

        var second = await AddAsync(alice, doc.Id, tag);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var twice = await GetAsync(doc.Id);
        twice.Tags.Should().ContainSingle(t => t == tag, "二重に付かない");
        twice.Version.Should().Be(once.Version, "何も変わっていないので版を進めない");
        Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == doc.Id)
            .Should().Be(events, "何も変わっていないのでイベントも出さない");
    }

    // 🔴 1014-2 / 1014-3: 辞書に無い名前は 400 で、文書には何も付かない（承認段の強制）。
    [Fact]
    public async Task Unknown_tag_is_rejected_and_nothing_is_attached()
    {
        var doc = await CreateOwnedAsync("alice");
        var unknown = UniqueName("辞書に無い");

        var resp = await AddAsync(ClientAs(user: "alice"), doc.Id, unknown);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "SC-05「既定タグ辞書に整合」は経路を問わない不変条件である（ADR-0063 決定 2）");
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("tags");
        var after = await GetAsync(doc.Id);
        after.Tags.Should().BeEmpty("拒んだのに付いてはならない");
        after.Version.Should().Be(doc.Version, "拒んだのに版が進んではならない");
    }

    // 🔴 1187-4: 所有者でもなく管理者でもない主体は 404（存在秘匿。403 にしない）。文書は変わらない。
    [Fact]
    public async Task Subject_who_is_neither_owner_nor_admin_gets_not_found()
    {
        var tag = await RegisterTagAsync();
        var doc = await CreateOwnedAsync("alice");

        var resp = await AddAsync(ClientAs(user: "mallory", roles: "viewer"), doc.Id, tag);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "403 にすると文書 ID の総当たりで実在が判別できる（PutBody と同じ理由）");
        (await GetAsync(doc.Id)).Tags.Should().BeEmpty();
    }

    // 🔴 1187-3 陽性対照（②）: `owner` を持たない文書（取り込み文書と同じ）は①では誰も書けないが、
    // **管理者ロールなら通る**。ADR-0063 決定 3 の中心 —— ②が無いとこの 1 件が通らない。
    [Fact]
    public async Task Admin_can_attach_a_tag_to_a_document_nobody_owns()
    {
        var tag = await RegisterTagAsync();
        var doc = await CreateOwnerlessAsync();

        var resp = await AddAsync(ClientAs(user: "root", roles: "platform-admin"), doc.Id, tag);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(doc.Id)).Tags.Should().Contain(tag);
    }

    // ②の否定形: 同じ文書に、運用者（管理者ではない）は書けない。**②は `platform-admin` だけ**である
    // （SC-05「作成・編集は管理者限定」。`UpdateMetadata` の `AdminOnly` と揃える）。
    [Fact]
    public async Task Operator_cannot_attach_a_tag_to_a_document_nobody_owns()
    {
        var tag = await RegisterTagAsync();
        var doc = await CreateOwnerlessAsync();

        var resp = await AddAsync(ClientAs(user: "ops", roles: "platform-operator"), doc.Id, tag);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync(doc.Id)).Tags.Should().BeEmpty();
    }

    // 認可は辞書照合より**前**にある: 書けない主体には「辞書に無い」すら返さない（404 のまま）。
    [Fact]
    public async Task Authorization_is_checked_before_the_dictionary()
    {
        var doc = await CreateOwnedAsync("alice");

        var resp = await AddAsync(ClientAs(user: "mallory", roles: "viewer"), doc.Id, UniqueName("辞書に無い"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "400 を返すと、書けない主体に辞書の中身（その名前が無いこと）が漏れる");
    }

    // 未知の文書 ID は 404（不在と権限外を区別しない）。
    [Fact]
    public async Task Unknown_document_is_not_found()
    {
        var tag = await RegisterTagAsync();

        var resp = await AddAsync(ClientAs(roles: "platform-admin"), Guid.NewGuid(), tag);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 生成段が引く内部口（IADR-0364 決定 2） ----

    // FR-18, ADR-0063 決定 2 (#1014): `/internal/tags/names` は**名前だけ**を返す（使用件数を運ばない）。
    // 認証を要求しない（メッシュ内部 API。`/internal/knowledge-health/observations` と同じ形）。
    [Fact]
    public async Task Internal_names_endpoint_returns_names_only_without_authentication()
    {
        var tag = await RegisterTagAsync();

        var resp = await ClientAs(roles: "nobody")
            .GetAsync("/internal/tags/names", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "管理者・運用者のロールを要求しない");
        var raw = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().NotContain("usageCount", "件数は管理面の集計値であり、生成には要らない");
        var body = await resp.Content.ReadFromJsonAsync<TagNamesResponse>(TestContext.Current.CancellationToken);
        body!.Names.Should().Contain(tag, "陽性対照: 登録した名前が返る");
    }
}
