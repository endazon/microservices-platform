using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents.ListPage;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents;

// FR-06, NFR-08, ADR-0036 D-08, ADR-0034 決定 9, ADR-0054 (#1575): `GET /documents/page`
// （組織文書の属性の絞り込み・キーセットのページング）。
//
// 🔴 **この口が守る約束は「見える集合を広げない」である。** DocumentService の読み取りは ABAC の判定を
// 持たず、直接の呼び出し元に見えているのは `GET /documents` の全件である。したがって ABAC の観点の
// 試験は次の 3 つになる —— ① 結果は常に `GET /documents` の部分集合、② 個人資料は絞り込みの値にも
// 呼び出し元にも依らず返らない、③ 絞り込みを足すと狭くなる一方である。
// **除外は陽性対照と対で置く**（個人資料が `GET /documents` には居ることを先に確かめる。居なければ
// 「返らない」は何も確かめていない）。
//
// テスト間は属性 `project` の値を 1 件ずつ変えて分離する（クラス内で DB を共有するため）。
[Trait("TestKind", "Integration")]
public class DocumentPageTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ClientAs(string user, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        return client;
    }

    // 台帳へ直接入れる（個人資料を含めて属性を自由に与えるため。作成の口は個人資料を拒む）。
    private async Task<Document> SeedAsync(string title, Dictionary<string, string> attributes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = Document.Create(title, originalUri: null, contentType: null, attributes: attributes);
        db.Documents.Add(doc);
        await db.SaveChangesAsync(Ct);
        return doc;
    }

    private static Dictionary<string, string> Org(string project, params (string Key, string Value)[] extra)
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "organization",
            ["project"] = project,
        };
        foreach (var (key, value) in extra) attributes[key] = value;
        return attributes;
    }

    private static Dictionary<string, string> PrivateNote(string project, string owner) => new()
    {
        ["confidentiality"] = "restricted",
        ["doc_scope"] = "private-note",
        ["owner"] = owner,
        ["project"] = project,
    };

    private async Task<DocumentPageDto> PageAsync(HttpClient client, string query)
    {
        var resp = await client.GetAsync($"/documents/page?{query}", Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<DocumentPageDto>(Ct))!;
    }

    private async Task<List<DocumentDto>> ListAllAsync(HttpClient client)
        => (await client.GetFromJsonAsync<List<DocumentDto>>("/documents", Ct))!;

    // ── ABAC: 見える集合を広げない ───────────────────────────────────────────

    // FR-06, ADR-0036 D-08, ADR-0054 (#1575): 属性で絞ると、その値を持つ**組織文書だけ**が返り、
    // 結果は `GET /documents` の部分集合である。同じ `project` の個人資料は返らない。
    [Fact]
    public async Task 属性で絞ると一致する組織文書だけが返り_既存一覧の部分集合で_個人資料は含まない()
    {
        var project = $"p-{Guid.NewGuid():N}";
        var a = await SeedAsync("組織A", Org(project));
        var b = await SeedAsync("組織B", Org(project));
        var other = await SeedAsync("別プロジェクト", Org($"q-{Guid.NewGuid():N}"));
        var note = await SeedAsync("個人資料", PrivateNote(project, owner: "alice"));

        var client = ClientAs("ast-kb-writer", "platform-operator");
        var all = await ListAllAsync(client);
        // 陽性対照: 個人資料も別プロジェクトの文書も、既存の一覧には居る（この口が外していることの前提）。
        all.Select(d => d.Id).Should().Contain([a.Id, b.Id, other.Id, note.Id]);

        var page = await PageAsync(client, $"attr.project={project}");

        page.Items.Select(d => d.Id).Should().BeEquivalentTo([a.Id, b.Id]);
        page.Items.Select(d => d.Id).Should().BeSubsetOf(all.Select(d => d.Id), "既存の一覧より広がらない");
        page.Items.Should().OnlyContain(d => d.Attributes["project"] == project);
        page.NextCursor.Should().BeNull();
    }

    // FR-06, ADR-0036 D-08, ADR-0034 決定 9 (#1575): 個人資料を名指しで絞っても、所有者本人が呼んでも返らない。
    [Fact]
    public async Task 個人資料は_doc_scopeで名指ししても所有者本人が呼んでも返らない()
    {
        var project = $"p-{Guid.NewGuid():N}";
        var note = await SeedAsync("本人の個人資料", PrivateNote(project, owner: "alice"));

        var owner = ClientAs("alice");
        (await ListAllAsync(owner)).Select(d => d.Id).Should().Contain(note.Id, "陽性対照: 既存の一覧には居る");

        (await PageAsync(owner, "attr.doc_scope=private-note")).Items
            .Should().NotContain(d => d.Id == note.Id);
        (await PageAsync(owner, $"attr.project={project}")).Items.Should().BeEmpty();
        (await PageAsync(owner, $"attr.owner=alice&attr.project={project}")).Items.Should().BeEmpty();
    }

    // FR-06, ADR-0036 D-08, ADR-0054 (#1575): **`doc_scope` の値の大小の揺れでも個人資料は返らない。**
    // 判定は `DocumentScopes.IsPrivateNote`（大文字小文字を区別しない）であり、絞り込みの完全一致
    // （区別する）とは別の規則である。`Private-Note` で保存された個人資料が「組織文書」扱いで漏れないこと。
    [Theory]
    [InlineData("Private-Note")]
    [InlineData("PRIVATE-NOTE")]
    public async Task 個人資料はdoc_scopeの値の大小が揺れていても返らない(string docScope)
    {
        var project = $"p-{Guid.NewGuid():N}";
        var attributes = PrivateNote(project, owner: "alice");
        attributes["doc_scope"] = docScope;
        var note = await SeedAsync("大小の揺れた個人資料", attributes);
        var org = await SeedAsync("陽性対照の組織文書", Org(project));

        var client = ClientAs("ast-kb-writer", "platform-operator");
        (await ListAllAsync(client)).Select(d => d.Id).Should().Contain(note.Id, "陽性対照: 既存の一覧には居る");

        var page = await PageAsync(client, $"attr.project={project}");
        page.Items.Select(d => d.Id).Should().BeEquivalentTo([org.Id], "組織文書は返り、個人資料は返らない");
        (await PageAsync(client, $"attr.doc_scope={docScope}")).Items.Should().NotContain(d => d.Id == note.Id);
    }

    // FR-06 (#1575): 絞り込みを足すと狭くなる一方で、どの値を与えても広がらない。
    [Fact]
    public async Task 絞り込みを足すと結果は前の結果の部分集合になり_一致しない値では空になる()
    {
        var project = $"p-{Guid.NewGuid():N}";
        var q1 = await SeedAsync("Q1", Org(project, ("periodKey", "2026Q1"), ("kind", "quarterly")));
        await SeedAsync("Q2", Org(project, ("periodKey", "2026Q2"), ("kind", "quarterly")));
        await SeedAsync("週報", Org(project, ("periodKey", "2026W10"), ("kind", "weekly")));

        var client = ClientAs("ast-kb-writer", "platform-operator");
        var one = await PageAsync(client, $"attr.project={project}");
        var two = await PageAsync(client, $"attr.project={project}&attr.kind=quarterly");
        var three = await PageAsync(client, $"attr.project={project}&attr.kind=quarterly&attr.periodKey=2026Q1");

        one.Items.Should().HaveCount(3);
        two.Items.Select(d => d.Id).Should().BeSubsetOf(one.Items.Select(d => d.Id)).And.HaveCount(2);
        three.Items.Select(d => d.Id).Should().BeEquivalentTo([q1.Id]);
        (await PageAsync(client, $"attr.project={project}&attr.kind=monthly")).Items.Should().BeEmpty();
        // 値は完全一致（大文字小文字を区別する）。部分一致・大小の揺れで広がらない。
        (await PageAsync(client, $"attr.project={project.ToUpperInvariant()}")).Items.Should().BeEmpty();
        (await PageAsync(client, $"attr.project={project[..6]}")).Items.Should().BeEmpty();
    }

    // ── ページング ───────────────────────────────────────────────────────

    // FR-06, NFR-08 (#1575): `limit` とカーソルで辿ると、絞り込み結果を重複なく作成順に得る。
    [Fact]
    public async Task limitとカーソルで全ページを辿ると絞り込み結果を重複なく作成順に得る()
    {
        var project = $"p-{Guid.NewGuid():N}";
        var seeded = new List<Document>();
        for (var i = 0; i < 5; i++)
            seeded.Add(await SeedAsync($"頁{i}", Org(project)));

        var client = ClientAs("ast-kb-writer", "platform-operator");
        var seen = new List<DocumentDto>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var query = $"attr.project={project}&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await PageAsync(client, query);
            page.Items.Should().HaveCountLessThanOrEqualTo(2);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        pages.Should().Be(3, "5 件を 2 件ずつ → 2・2・1");
        seen.Select(d => d.Id).Should().OnlyHaveUniqueItems();
        seen.Select(d => d.Id).Should().Equal(
            seeded.OrderBy(d => d.CreatedAt.UtcTicks).ThenBy(d => d.Id).Select(d => d.Id),
            "作成時刻の昇順（同時刻は Id 昇順）");
    }

    // FR-06, NFR-08 (#1575): 走査の途中で更新・削除・追加が起きても、走査の間ずっと在った文書を読み飛ばさない。
    // 🔴 更新時刻で並べるとこの試験は落ちる（更新された未読の文書が先頭へ移り、飛ばされる）。
    [Fact]
    public async Task 走査の途中で更新削除追加があっても_ずっと在った文書を読み飛ばさない()
    {
        var project = $"p-{Guid.NewGuid():N}";
        var seeded = new List<Document>();
        for (var i = 0; i < 6; i++)
            seeded.Add(await SeedAsync($"走査{i}", Org(project)));
        var ordered = seeded.OrderBy(d => d.CreatedAt.UtcTicks).ThenBy(d => d.Id).ToList();

        var reader = ClientAs("ast-kb-writer", "platform-operator");
        var admin = ClientAs("admin-user", "platform-admin");

        var first = await PageAsync(reader, $"attr.project={project}&limit=2");
        first.Items.Select(d => d.Id).Should().Equal(ordered.Take(2).Select(d => d.Id));

        // 未読の文書（4 件目）を更新し、既読の文書（1 件目）を消し、新しい文書を 1 件作る。
        var patch = await admin.PatchAsJsonAsync($"/documents/{ordered[3].Id}/metadata", new
        {
            attributes = Org(project, ("periodKey", "updated")),
            tags = new List<string>(),
        }, Ct);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/documents/{ordered[0].Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var late = await SeedAsync("途中で作った文書", Org(project));

        var rest = new List<DocumentDto>();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await PageAsync(reader,
                $"attr.project={project}&limit=2&cursor={Uri.EscapeDataString(cursor)}");
            rest.AddRange(page.Items);
            cursor = page.NextCursor;
        }

        rest.Select(d => d.Id).Should().Equal(
            ordered.Skip(2).Select(d => d.Id).Append(late.Id),
            "未読だった 4 件（途中で更新された 1 件を含む）がちょうど 1 回ずつ、途中で作った文書は末尾に現れる");
    }

    // FR-06, NFR-08 (#1575): **作成時刻が同じ文書は `Id` 昇順で切り分ける**（カーソルの同時刻の枝）。
    // 作成時刻を台帳で同じ tick に揃え、1 件ずつ辿る。同時刻の枝（`Precedes` の `Id` 比較）を
    // 落とす・向きを逆にする・`>=` にすると、読み飛ばし・重複・無限の繰り返しのいずれかになる。
    [Fact]
    public async Task 作成時刻が同じtickの文書はIdの昇順で1件ずつ重複なく辿れる()
    {
        var project = $"p-{Guid.NewGuid():N}";
        var ticks = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var seeded = new List<Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
            for (var i = 0; i < 4; i++)
            {
                var doc = Document.Create($"同時刻{i}", originalUri: null, contentType: null, attributes: Org(project));
                db.Documents.Add(doc);
                db.Entry(doc).Property(d => d.CreatedAt).CurrentValue = ticks;
                seeded.Add(doc.Id);
            }
            // 同じ作成時刻の前後に 1 件ずつ置き、同時刻の群が時刻の比較と正しく噛み合うことも見る。
            var before = Document.Create("前", originalUri: null, contentType: null, attributes: Org(project));
            var after = Document.Create("後", originalUri: null, contentType: null, attributes: Org(project));
            db.Documents.AddRange(before, after);
            db.Entry(before).Property(d => d.CreatedAt).CurrentValue = ticks.AddTicks(-1);
            db.Entry(after).Property(d => d.CreatedAt).CurrentValue = ticks.AddTicks(1);
            await db.SaveChangesAsync(Ct);
            seeded = [before.Id, .. seeded.OrderBy(id => id), after.Id];
        }

        var client = ClientAs("ast-kb-writer", "platform-operator");
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await PageAsync(client, $"attr.project={project}&limit=1"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"));
            seen.AddRange(page.Items.Select(d => d.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 20);

        seen.Should().Equal(seeded, "時刻の昇順、同時刻は Id の昇順で、ちょうど 1 回ずつ");
    }

    // FR-06 (#1575): `limit` は 1〜500 に丸める（FeedbackService の一覧と同じ作法）。
    [Fact]
    public async Task limitは1から500に丸める()
    {
        var project = $"p-{Guid.NewGuid():N}";
        await SeedAsync("丸め1", Org(project));
        await SeedAsync("丸め2", Org(project));

        var client = ClientAs("ast-kb-writer", "platform-operator");
        var zero = await PageAsync(client, $"attr.project={project}&limit=0");
        zero.Items.Should().HaveCount(1);
        zero.NextCursor.Should().NotBeNull();

        (await PageAsync(client, $"attr.project={project}&limit=100000")).Items.Should().HaveCount(2);
    }

    // FR-06, NFR-08 (#1575): **上限 500・下限 1・未指定は 100** を値で固定する。
    // 上の T-35 は 2 件しか置かないため上限を観測できない（上限を 100000 へ上げる変異が全試験を
    // 生き延びた）。上限は「1 回の応答で返す件数」の天井であり、端点経由で 501 件を置く代わりに
    // 丸めの関数を直接見る（端点がこの関数を通ることは T-35 の `limit=0` → 1 件が見ている）。
    [Theory]
    [InlineData(100000, 500)]
    [InlineData(501, 500)]
    [InlineData(500, 500)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(null, 100)]
    public void limitの丸めは上限500_下限1_未指定100(int? requested, int expected)
    {
        DocumentPageQuery.ClampLimit(requested).Should().Be(expected);
        DocumentPageQuery.MaxLimit.Should().Be(500, "上限は仕様の値（通信仕様書・機能仕様書が 500 と書いている）");
    }

    // ── 入力の検証 ───────────────────────────────────────────────────────

    // FR-06 (#1575): 同じキーの重複・空のキー・空の値・壊れたカーソルは 400（黙ってどれかを採らない）。
    [Theory]
    [InlineData("attr.project=a&attr.project=b")]
    [InlineData("attr.project=a&attr.Project=b")]
    [InlineData("attr.=x")]
    [InlineData("attr.project=")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=djE6YWJjOmRlZg")]
    public async Task 不正な絞り込みやカーソルは400(string query)
    {
        var client = ClientAs("ast-kb-writer", "platform-operator");
        var resp = await client.GetAsync($"/documents/page?{query}", Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 認可 ─────────────────────────────────────────────────────────────

    // FR-06, IADR-0044 (#1575): 新しい口は**認証を要する**（既存の `GET /documents` は認証を要らない）。
    // `TestAuthHandler` は常に認証するため 401 は観測できない —— 端点の認可メタデータで固定する。
    [Fact]
    public void 新しい口は認証を要求し_既存の一覧は従来どおり要求しない()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .ToList();

        var page = endpoints.Single(e => e.RoutePattern.RawText == "/documents/page");
        page.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().NotBeEmpty();

        // 陽性対照: 既存の一覧は認証を要らない（ここを塞ぐと SC-03 の読み取りの前提が変わる。本作業の対象外）。
        var list = endpoints.Single(e => e.RoutePattern.RawText == "/documents/");
        list.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().BeEmpty();
    }
}
