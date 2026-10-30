using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.Graph.LinkCandidates;

// FR-17, SC-19, ADR-0034 決定 2・8・9, [[IADR-0655]] (#2343): リンク作成の補完候補。
//
// 🔴 **候補は作成者の read スコープ内に限る**（権限外の題名を補完に流さない。作成側からの逆方向の露出）。
// 🔴 **個人資料は自分のものだけ**（共有されて読める他人の個人資料も出さない）。機械の主体には個人資料を出さない。
// 🔴 **件数を返さない**（応答は候補の配列だけ）。
[Trait("TestKind", "Integration")]
public class LinkCandidateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LinkCandidateTests(TestWebApplicationFactory factory) => _factory = factory;

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    // internal の組織文書と、自分と他人の個人資料を読める裁量の分岐（共有されて読める他人の資料を模す）。
    private static AccessScopeResponse InternalAndPrivateNotes()
        => new("test-user", [], true,
        [
            new AccessScopeBranch("org", [new AttributeFilter("confidentiality", ["internal"])]),
            new AccessScopeBranch("notes", [new AttributeFilter("owner", ["test-user", "someone-else"])]),
        ]);

    private static GraphDocument Org(Guid id, string title, string confidentiality)
        => GraphDocument.Create(id, title,
            new Dictionary<string, string> { ["confidentiality"] = confidentiality },
            null, DateTimeOffset.UtcNow);

    private static GraphDocument Note(Guid id, string title, string owner)
        => GraphDocument.Create(id, title,
            new Dictionary<string, string> { ["doc_scope"] = "private-note", ["owner"] = owner },
            null, DateTimeOffset.UtcNow);

    private sealed record Seeded(string Token, Guid Visible, Guid Forbidden, Guid MyNote, Guid OthersNote);

    private async Task<Seeded> SeedAsync()
    {
        var token = $"tk{Guid.NewGuid():N}"[..14];
        var s = new Seeded(token, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(Org(s.Visible, $"設計 {token} 公開", "internal"));
            db.Documents.Add(Org(s.Forbidden, $"設計 {token} 秘密", "restricted"));
            db.Documents.Add(Note(s.MyNote, $"メモ {token} 自分", "test-user"));
            db.Documents.Add(Note(s.OthersNote, $"メモ {token} 他人", "someone-else"));
            return Task.CompletedTask;
        });
        _factory.ScopeProvider = _ => InternalAndPrivateNotes();
        return s;
    }

    private async Task<List<GraphLinkCandidateDto>> GetOkAsync(HttpClient client, string q)
    {
        var res = await client.GetAsync($"/graph/link-candidates?q={Uri.EscapeDataString(q)}", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<List<GraphLinkCandidateDto>>(Ct))!;
    }

    // 陽性対照 ＋ 🔴 ADR-0034 決定 8: 権限内は出て、権限外は出ない。
    [Fact]
    public async Task 権限内の文書は候補に出て権限外の文書は出ない()
    {
        var s = await SeedAsync();

        var items = await GetOkAsync(_factory.CreateClient(), s.Token);

        items.Select(i => i.DocumentId).Should().Contain(s.Visible, "陽性対照: 読める組織文書は出る");
        items.Select(i => i.DocumentId).Should().NotContain(s.Forbidden);
        items.Should().NotContain(i => i.Title.Contains("秘密"), "権限外の題名は補完に流さない");
    }

    // 🔴 拘束 2: 自分の個人資料は出て、共有されて読める他人の個人資料は出ない。
    [Fact]
    public async Task 自分の個人資料は候補に出て他人の個人資料は読めても出ない()
    {
        var s = await SeedAsync();

        var items = await GetOkAsync(_factory.CreateClient(), s.Token);

        items.Should().ContainSingle(i => i.DocumentId == s.MyNote).Which.IsPrivateNote.Should().BeTrue();
        items.Select(i => i.DocumentId).Should().NotContain(s.OthersNote,
            "read の分岐が他人の個人資料を許していても、候補には出さない");
        items.Single(i => i.DocumentId == s.Visible).IsPrivateNote.Should().BeFalse();
    }

    // 所有者の比較は本文の書き込みの所有者判定と同じ（大小も区別する）。大小だけ違う所有者の資料は自分のものとしない。
    [Fact]
    public async Task 所有者の名前が大小だけ違う個人資料は自分のものとして扱わない()
    {
        var s = await SeedAsync();
        var lookalike = Guid.NewGuid();
        await _factory.SeedAsync(db =>
        {
            db.Documents.Add(Note(lookalike, $"メモ {s.Token} 大小", "Test-User"));
            return Task.CompletedTask;
        });
        _factory.ScopeProvider = _ => new AccessScopeResponse("test-user", [], true,
        [
            new AccessScopeBranch("org", [new AttributeFilter("confidentiality", ["internal"])]),
            new AccessScopeBranch("notes", [new AttributeFilter("owner", ["test-user", "Test-User"])]),
        ]);

        var items = await GetOkAsync(_factory.CreateClient(), s.Token);

        items.Select(i => i.DocumentId).Should().Contain(s.MyNote, "陽性対照");
        items.Select(i => i.DocumentId).Should().NotContain(lookalike);
    }

    // 🔴 ADR-0034 決定 9: 機械の主体には個人資料を出さない（自分の名前の資料であっても）。
    [Fact]
    public async Task 機械の主体には個人資料を出さない()
    {
        var s = await SeedAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.MachineClientHeader, "bot");

        var items = await GetOkAsync(client, s.Token);

        items.Should().NotContain(i => i.IsPrivateNote);
        items.Select(i => i.DocumentId).Should().Contain(s.Visible, "陽性対照: 組織文書は出る");
    }

    // 🔴 ADR-0034 決定 2: 応答は候補の配列だけで、件数・総数を持たない。
    [Fact]
    public async Task 応答は候補の配列だけで件数を持たない()
    {
        var s = await SeedAsync();

        var res = await _factory.CreateClient().GetAsync($"/graph/link-candidates?q={s.Token}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        body.TrimStart().Should().StartWith("[", "配列そのもの（件数の項目を載せる器が無い）");
        body.Should().NotContain("total").And.NotContain("count").And.NotContain("truncated");
    }

    // 許可が無ければ空（deny-by-default）。404 にしない —— 補完は「無い」と「見えない」を区別しない。
    [Fact]
    public async Task 許可が無ければ空を返す()
    {
        var s = await SeedAsync();
        _factory.ScopeProvider = _ => new AccessScopeResponse("test-user", [], false);

        var items = await GetOkAsync(_factory.CreateClient(), s.Token);

        items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public async Task 検索語が2文字未満なら400(string q)
    {
        await SeedAsync();

        var res = await _factory.CreateClient().GetAsync($"/graph/link-candidates?q={q}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 件数の上限を超える指定は400()
    {
        var s = await SeedAsync();

        var res = await _factory.CreateClient().GetAsync($"/graph/link-candidates?q={s.Token}&limit=51", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 表題の一致は大小を区別せず件数は指定で絞れる()
    {
        var s = await SeedAsync();

        var items = await GetOkAsync(_factory.CreateClient(), s.Token.ToUpperInvariant());
        items.Select(i => i.DocumentId).Should().Contain(s.Visible);

        var one = await _factory.CreateClient().GetAsync($"/graph/link-candidates?q={s.Token}&limit=1", Ct);
        (await one.Content.ReadFromJsonAsync<List<GraphLinkCandidateDto>>(Ct)).Should().HaveCount(1);
    }

    [Fact]
    public async Task 未認証は401()
    {
        var s = await SeedAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.GetAsync($"/graph/link-candidates?q={s.Token}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
