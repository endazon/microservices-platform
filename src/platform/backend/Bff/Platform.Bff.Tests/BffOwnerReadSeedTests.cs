using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Platform.Bff.Tests;

// FR-05, FR-19, NFR-09, UC-11, 計画 ADR-0121 決定 1・フォローアップ 6, ADR-0036 D-01・D-05・D-08,
// [[IADR-0253]], [[IADR-0450]] (#1664):
// **dev seed を入れた構成で、所有者が共有していない自分の個人資料を BFF の経路で開ける。**
//
// 🔴 **認可スコープは手で組まない。** 入力は、seed を入れた認可サービスの `/authz/scope` の応答の期待値
// （`AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json`）である。同じファイルを
// 認可サービスの試験（`OwnerReadPolicySeedTests`）が実物の応答と突き合わせているので、
// ここで見るのは「seed が作る応答を BFF がどう判定するか」そのものになる。
//
//   alice: 属性を持たない利用者（所有者・共有先の分岐だけ）
//   bob:   `clearance=internal` の利用者（階段 2 段 ＋ 所有者・共有先の分岐）
//
// 🔴 **陰性と陽性を対で置く。** 所有者が開けるだけでは「個人資料を誰にでも見せる」実装でも緑になる。
public class BffOwnerReadSeedTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    private static readonly Guid NoteId = Guid.Parse("16641664-1664-1664-1664-166416641664");
    private static readonly Guid OrgId = Guid.Parse("16640000-0000-0000-0000-000000001664");

    public BffOwnerReadSeedTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    private void As(string userId)
    {
        var scope = SeedReadScopes.Of(userId);
        _factory.SearchScopeGranted = scope.Granted;
        _factory.ScopeFilters = scope.AllowedFilters;
        _factory.ScopeBranches = scope.Branches;
    }

    // 個人資料の実物と同じ属性（DocumentService の `PrivateNoteDefaults`: 個人資料・所有者・restricted）。
    private static DocumentDto PrivateNote(string owner, List<string>? sharedWith = null) => new()
    {
        Id = NoteId,
        Title = "自分だけの個人メモ",
        Status = "published",
        MarkdownUri = "storage://bucket/note.md",
        Version = 1,
        Attributes = new Dictionary<string, string>
        {
            ["doc_scope"] = "private-note",
            ["owner"] = owner,
            ["confidentiality"] = "restricted",
        },
        Tags = [],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        SharedWith = sharedWith,
    };

    private static DocumentDto OrganizationDocument(string owner, string confidentiality) => new()
    {
        Id = OrgId,
        Title = "組織文書",
        Status = "published",
        MarkdownUri = "storage://bucket/org.md",
        Version = 1,
        Attributes = new Dictionary<string, string>
        {
            ["owner"] = owner,
            ["confidentiality"] = confidentiality,
        },
        Tags = [],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private Task<HttpResponseMessage> GetAsync(Guid id, string suffix = "")
        => _factory.CreateClient().GetAsync($"/bff/documents/{id}{suffix}", TestContext.Current.CancellationToken);

    // T-28（フォローアップ 6・陽性）: 所有者は、共有していない自分の個人資料を詳細・版・本文のいずれでも開ける。
    // 属性を持たない利用者（alice）でも、`clearance=internal` の利用者（bob。個人資料は restricted）でも同じ。
    [Theory]
    [InlineData("alice", "")]
    [InlineData("alice", "/versions")]
    [InlineData("alice", "/content")]
    [InlineData("bob", "")]
    public async Task 所有者は共有していない自分の個人資料を開ける(string userId, string suffix)
    {
        As(userId);
        _factory.StubDocument = PrivateNote(owner: userId);

        (await GetAsync(NoteId, suffix)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // T-29（陰性対照）: **他人は開けない**（404。存在を秘匿する）。
    // bob は階段で restricted 以外の区分を読めるが、個人資料は裁量の分岐でしか許可されない（ADR-0036 D-08）。
    [Theory]
    [InlineData("bob", "alice")]
    [InlineData("alice", "bob")]
    public async Task 他人の共有していない個人資料は開けない(string viewer, string owner)
    {
        As(viewer);
        _factory.StubDocument = PrivateNote(owner: owner);

        (await GetAsync(NoteId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // T-30（束縛の外）: 所有者の分岐は**本人の名前にしか一致しない**。予約値 `system`（取り込みで所有者を
    // 解決できなかった印・AST の古い写し）の文書は、属性を持たない利用者には見えない。
    // 陽性対照: 同じ文書が階段の分岐を持つ bob には従前どおり見える（「常に 404」の実装を落とす）。
    [Theory]
    [InlineData("alice", HttpStatusCode.NotFound)]
    [InlineData("bob", HttpStatusCode.OK)]
    public async Task 予約値の所有者の組織文書は所有者の分岐では見えない(string viewer, HttpStatusCode expected)
    {
        As(viewer);
        _factory.StubDocument = OrganizationDocument(owner: "system", confidentiality: "internal");

        (await GetAsync(OrgId)).StatusCode.Should().Be(expected);
    }

    // T-31（計画の意図どおりの広がり）: 所有者は、自分の取扱区分を超える区分の**自分の**組織文書も読める
    // （read 規則の所有者の分岐。ADR-0036 D-01）。他人の同じ文書は読めない（陰性対照）。
    [Theory]
    [InlineData("alice", "alice", HttpStatusCode.OK)]
    [InlineData("alice", "bob", HttpStatusCode.NotFound)]
    public async Task 所有者は自分の組織文書を区分に関わらず読める(string viewer, string owner, HttpStatusCode expected)
    {
        As(viewer);
        _factory.StubDocument = OrganizationDocument(owner: owner, confidentiality: "restricted");

        (await GetAsync(OrgId)).StatusCode.Should().Be(expected);
    }

    // T-32（IADR-0450 の前提が seed で働く）: 所有者の分岐で読んだ所有者には共有先の写しが返り、
    // 共有先の分岐だけで読んだ相手には返らない。
    [Fact]
    public async Task 共有先の写しはSeedの構成でも所有者にだけ返る()
    {
        _factory.StubDocument = PrivateNote(owner: "alice", sharedWith: ["alice", "bob"]);

        As("alice");
        var own = await _factory.CreateClient().GetFromJsonAsync<DocumentDto>(
            $"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);
        own!.SharedWith.Should().Equal("alice", "bob");

        As("bob");
        var shared = await _factory.CreateClient().GetFromJsonAsync<DocumentDto>(
            $"/bff/documents/{NoteId}", TestContext.Current.CancellationToken);
        shared!.Id.Should().Be(NoteId, "共有された相手は読める（陽性対照）");
        shared.SharedWith.Should().BeNull("所有者ではない閲覧者に他の共有先を見せない");
    }

    // T-33（管理面は不変）: SC-05 の一覧には、所有者自身の個人資料も現れない（一律除外）。
    // 陽性対照: 同じ呼び出しで自分の組織文書は現れる。
    [Fact]
    public async Task SC05の一覧には自分の個人資料も現れない()
    {
        As("alice");
        _factory.StubDocumentList = [PrivateNote(owner: "alice"), OrganizationDocument(owner: "alice", confidentiality: "internal")];

        var body = (await _factory.CreateClient().GetFromJsonAsync<List<DocumentDto>>(
            "/bff/documents", TestContext.Current.CancellationToken))!;

        body.Select(d => d.Id).Should().NotContain(NoteId).And.Contain(OrgId);
    }
}

// FR-05, ADR-0121 フォローアップ 6 (#1664): seed を入れた認可サービスの応答の期待値を読む。
// **正は `AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json`**（認可サービスの試験が実物と突き合わせる）。
internal static class SeedReadScopes
{
    public static AccessScopeResponse Of(string userId)
    {
        const string relative = "src/platform/backend/Services/AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            return JsonNode.Parse(File.ReadAllText(path))!["subjects"]!.AsArray()
                .Single(s => (string)s!["userId"]! == userId)!["scope"]!
                .Deserialize<AccessScopeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        throw new FileNotFoundException($"リポジトリの {relative} が見つからない（走査の起点: {AppContext.BaseDirectory}）");
    }
}
