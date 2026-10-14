using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace Platform.Bff.Tests;

// FR-06, UC-03, SC-05, NFR-16, [[IADR-0009]], [[IADR-0041]], [[IADR-0537]] (#1923・#1897):
// 文書管理の一覧（`GET /bff/documents`）のページング —— オフセットの窓と、**ABAC で絞った後の総件数**。
//
// 🔴 **権限外の文書の存在が、総件数・ページの中身・ページの境界のどれからも漏れないこと**を、
//   「権限外の文書を混ぜた台帳」と「混ぜない台帳」で応答が 1 ビットも変わらないことで示す（陰性）。
//   陽性対照として、混ぜた権限外の文書が後段の一覧には実際に居ること（＝絞っているのは BFF である）を先に確かめる。
public class BffDocumentListPagingTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentListPagingTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        // 読めるのは internal だけ（confidential は権限外）。
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal"])];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
        _factory.DocumentReadServerPageSize = null;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentDto Doc(int n, string confidentiality, Dictionary<string, string>? extra = null)
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = confidentiality };
        foreach (var (k, v) in extra ?? []) attributes[k] = v;
        return new DocumentDto
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{n:D12}"),
            Title = $"{confidentiality}-{n}",
            Attributes = attributes,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(100 - n),
        };
    }

    // 後段の並び（更新の新しい順）どおりに、読める 3 件（1・3・5）と権限外の 2 件（2・4）を交互に置く。
    private static List<DocumentDto> Mixed() =>
        [Doc(1, "internal"), Doc(2, "confidential"), Doc(3, "internal"), Doc(4, "confidential"), Doc(5, "internal")];

    private static List<DocumentDto> VisibleOnly() => [Doc(1, "internal"), Doc(3, "internal"), Doc(5, "internal")];

    private async Task<DocumentListPageDto> PageAsync(string query)
    {
        var resp = await _factory.CreateClient().GetAsync($"/bff/documents{query}", Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<DocumentListPageDto>(Ct))!;
    }

    private static IEnumerable<int> Numbers(DocumentListPageDto page) => page.Items.Select(d => int.Parse(d.Title.Split('-')[1]));

    // ── 窓と総件数 ──────────────────────────────────────────────

    // 総件数は絞った後の 3 件。窓は絞った後の順番で切り出す（1 ページ目 1・3、2 ページ目 5）。
    [Fact]
    public async Task 窓と総件数は権限で絞った後の並びで数える()
    {
        _factory.StubDocumentList = Mixed();

        var first = await PageAsync("?offset=0&limit=2");
        var second = await PageAsync("?offset=2&limit=2");

        first.Total.Should().Be(3);
        Numbers(first).Should().Equal(1, 3);
        second.Total.Should().Be(3);
        Numbers(second).Should().Equal(5);
        (first.Offset, first.Limit, second.Offset).Should().Be((0, 2, 2));
    }

    // 🔴 存在秘匿（IADR-0009）: 権限外の文書を混ぜても、総件数・ページの中身・境界がどのページでも変わらない。
    [Theory]
    [InlineData("?offset=0&limit=1")]
    [InlineData("?offset=1&limit=1")]
    [InlineData("?offset=0&limit=2")]
    [InlineData("?offset=2&limit=2")]
    [InlineData("?offset=3&limit=2")]
    [InlineData("")]
    public async Task 権限外の文書は総件数にもページの中身にも境界にも現れない(string query)
    {
        // 陽性対照: 権限外の文書は後段から実際に届いている —— 区分を広げたスコープでは 5 件と数える（絞っているのは BFF である）。
        _factory.StubDocumentList = Mixed();
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal", "confidential"])];
        (await PageAsync("?limit=10")).Total.Should().Be(5);
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal"])];

        var withHidden = await PageAsync(query);
        _factory.StubDocumentList = VisibleOnly();
        var withoutHidden = await PageAsync(query);

        withHidden.Should().BeEquivalentTo(withoutHidden, o => o.WithStrictOrdering());
        withHidden.Items.Should().NotContain(d => d.Attributes["confidentiality"] == "confidential");
    }

    // FR-19, ADR-0036 D-08: 区分では読める個人資料も、管理面の一覧の総件数には入らない（`IsManageable` の一律除外）。
    [Fact]
    public async Task 個人資料は総件数に入らない()
    {
        _factory.StubDocumentList =
        [
            Doc(1, "internal"),
            Doc(2, "internal", new() { ["doc_scope"] = "private-note", ["owner"] = "someone-else" }),
            Doc(3, "internal"),
        ];

        var page = await PageAsync("?limit=10");

        page.Total.Should().Be(2);
        Numbers(page).Should().Equal(1, 3);
    }

    // ── 後段のページを辿る（#1897: 1 応答は台帳の件数に比例しない） ───────────────

    // 後段が 1 件ずつしか返さなくても、窓と総件数は 1 回で引いた場合と同じ。BFF は後段の上限（1000）で求める。
    [Fact]
    public async Task 後段のページを辿っても窓と総件数は変わらない()
    {
        _factory.StubDocumentList = Mixed();
        var whole = await PageAsync("?offset=1&limit=2");

        _factory.DocumentReadServerPageSize = 1;
        while (_factory.ListDocumentsRequests.TryDequeue(out _)) { }
        var paged = await PageAsync("?offset=1&limit=2");

        paged.Should().BeEquivalentTo(whole, o => o.WithStrictOrdering());
        Numbers(paged).Should().Equal(3, 5);
        var requests = _factory.ListDocumentsRequests.ToList();
        requests.Should().HaveCount(Mixed().Count, "後段のページを終端まで辿る（1 件ずつ × 5 件）");
        requests.Should().OnlyContain(r => r.PageSize == DocumentReadGrpcClient.ListPageSize);
        requests[0].PageToken.Should().BeEmpty("先頭は印なし");
        requests.Skip(1).Should().OnlyContain(r => r.PageToken.Length > 0, "続きは前の応答の印を渡す");
    }

    // ── 丸めと検証 ──────────────────────────────────────────────

    [Theory]
    [InlineData("", 0, DocumentListWindow.DefaultLimit)]
    [InlineData("?limit=1000", 0, DocumentListWindow.MaxLimit)]
    [InlineData("?limit=0", 0, 1)]
    [InlineData("?limit=-5", 0, 1)]
    [InlineData("?limit=25&offset=7", 7, 25)]
    public async Task ページの大きさはサーバ側で丸める(string query, int offset, int limit)
    {
        _factory.StubDocumentList = Mixed();

        var page = await PageAsync(query);

        (page.Offset, page.Limit).Should().Be((offset, limit));
        page.Total.Should().Be(3);
    }

    // 末尾を越えた offset は空の窓で、総件数は同じ（絞った後の数なので、越えた先に権限外の文書が在るかは読めない）。
    [Fact]
    public async Task 末尾を越えたoffsetは空の窓と同じ総件数を返す()
    {
        _factory.StubDocumentList = Mixed();

        var page = await PageAsync("?offset=3&limit=50");

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(3);
    }

    // 改ざん: 負の offset・数でない値は 400（黙って先頭へ畳まない）。
    [Theory]
    [InlineData("?offset=-1")]
    [InlineData("?offset=abc")]
    [InlineData("?limit=abc")]
    public async Task 負や数でないoffset_limitは400(string query)
    {
        var resp = await _factory.CreateClient().GetAsync($"/bff/documents{query}", Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // スコープが解決できない（許可ポリシーが無い）なら空のページ（総件数 0）。権限外の存在を示さない。
    [Fact]
    public async Task スコープが無ければ空のページを返す()
    {
        _factory.StubDocumentList = Mixed();
        _factory.SearchScopeGranted = false;

        var page = await PageAsync("?limit=10");

        page.Items.Should().BeEmpty();
        page.Total.Should().Be(0);
        (page.Offset, page.Limit).Should().Be((0, 10));
    }
}
