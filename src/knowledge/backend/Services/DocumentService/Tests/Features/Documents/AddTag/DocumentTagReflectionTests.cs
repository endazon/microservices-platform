using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Tests.Features.Documents.AddTag;

// FR-18, SC-03, SC-05, SC-09, ADR-0063 決定 1〜3, ADR-0036 D-07, IADR-0364 (#1187 / #1014):
// **AI タグ提案の承認の反映先**。
//
// ［2026-10-15 / #1902・[[IADR-0533]]］REST の `POST /documents/{id}/tags` は撤去した ——
// 呼び出し元（GraphService の D-2）は east-west gRPC（`DocumentTagWrite/AddTag`）だけになっていた。
// 本クラスは同じ本体（`AddDocumentTagUseCase`）を呼ぶ **gRPC 面**を TestServer 上で叩き、
// **反映の結果**（文書に付く・版が進む・`DocumentUpdated` が出る・拒んだら何も変わらない）を固定する。
// 認可の枝（管理者②・運用者・個人資料・信頼する中継者）と応答の写像は `GrpcDocumentTagWriteTests` /
// `DocumentTagWriteAdminRoleTests` / `DocumentTagWriteTrustedRelayTests` が持つ。
//
// 🔴 **陰性は陽性対照と対で置く。** 「常に 404」「常に 400」の実装でも陰性だけなら緑になる。
// 本クラスは同じ経路で (a) 所有者が成功する (b) 管理者が成功する を陽性対照として持つ。
//
// 🔴 **辞書照合の変異試験の対象である**（#1014 受け入れ基準 3・4）。`AddDocumentTagUseCase` の
// 辞書照合の分岐を外すと `Unknown_tag_is_rejected_and_nothing_is_attached` が落ちる
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

    // 承認の中継者（graph-service）として gRPC 面を叩く。承認者の身元は本文の `user_id` で運ぶ。
    private Task<AddTagResponse> AddAsync(string approver, Guid id, string name)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "service-account-graph-service");
        http.DefaultRequestHeaders.Add(TestAuthHandler.ClientIdHeader, "graph-service");
        http.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, PlatformAuthPolicies.ServiceRole);
        var channel = GrpcChannel.ForAddress(http.BaseAddress!, new GrpcChannelOptions { HttpClient = http });
        return new DocumentTagWrite.DocumentTagWriteClient(channel).AddTagAsync(
            new AddTagRequest { DocumentId = id.ToString(), TagName = name, UserId = approver, Action = "write" },
            cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;
    }

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

        var resp = await AddAsync("alice", doc.Id, tag);

        resp.Result.Should().Be(TagWriteResult.Applied, "所有者は①で通る");
        var after = await GetAsync(doc.Id);
        after.Tags.Should().Contain(tag);
        after.Version.Should().Be(doc.Version + 1, "利用者の意思による内容変更なので版が進む");
        Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == doc.Id)
            .Should().Be(before + 1, "射影（Qdrant / Wiki.js）が追随するために再発行する");
        Bus.PublishedOf<DocumentUpdated>().Last(e => e.DocumentId == doc.Id).Tags.Should().Contain(tag);
    }

    // 1187-2: **冪等**。二度目も成功だが版は進まず、イベントも増えない。
    [Fact]
    public async Task Attaching_the_same_tag_twice_is_idempotent()
    {
        var tag = await RegisterTagAsync();
        var doc = await CreateOwnedAsync("alice");
        (await AddAsync("alice", doc.Id, tag)).Result.Should().Be(TagWriteResult.Applied);
        var once = await GetAsync(doc.Id);
        var events = Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == doc.Id);

        var second = await AddAsync("alice", doc.Id, tag);

        second.Result.Should().Be(TagWriteResult.Applied);
        var twice = await GetAsync(doc.Id);
        twice.Tags.Should().ContainSingle(t => t == tag, "二重に付かない");
        twice.Version.Should().Be(once.Version, "何も変わっていないので版を進めない");
        Bus.PublishedOf<DocumentUpdated>().Count(e => e.DocumentId == doc.Id)
            .Should().Be(events, "何も変わっていないのでイベントも出さない");
    }

    // 🔴 1014-2 / 1014-3: 辞書に無い名前は却下（UNKNOWN_TAG。REST の 400）で、文書には何も付かない（承認段の強制）。
    [Fact]
    public async Task Unknown_tag_is_rejected_and_nothing_is_attached()
    {
        var doc = await CreateOwnedAsync("alice");
        var unknown = UniqueName("辞書に無い");

        var resp = await AddAsync("alice", doc.Id, unknown);

        resp.Result.Should().Be(TagWriteResult.UnknownTag,
            "SC-05「既定タグ辞書に整合」は経路を問わない不変条件である（ADR-0063 決定 2）");
        var after = await GetAsync(doc.Id);
        after.Tags.Should().BeEmpty("拒んだのに付いてはならない");
        after.Version.Should().Be(doc.Version, "拒んだのに版が進んではならない");
    }

    // 1187-4（所有者でない主体は書けず文書は変わらない）・1187-3（②管理者）・②の否定形（運用者）・認可が辞書照合より前・未知の文書 ID:
    // ［2026-10-15 / #1902］REST 面の試験から gRPC 面の試験へ移った ——
    // `GrpcDocumentTagWriteTests` の T-02（所有者でない身元では書けない）・T-03（認可サービスが管理者と答えれば所有者のいない文書へ反映）・
    // T-04（書けない主体には辞書の情報を返さない）・T-05（不存在と書けないは同じ応答）と
    // `DocumentTagWriteAdminRoleTests`（管理者かは認可サービスが答える。運用者は管理者ではない）が固定する。

    // ---- 生成段が引く内部口（IADR-0364 決定 2） ----
    // ［2026-10-15 / #1902・[[IADR-0533]]］無認証の REST `GET /internal/tags/names` は撤去した（呼び出し元の D-3 は
    // gRPC `TagDictionary/ListNames` だけ）。「名前だけを返す（使用件数を運ばない）」は gRPC の応答の形
    // （`GrpcTagDictionaryTests`）が固定する。
}
