using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests;

// FR-19, FR-20, ADR-0036 D-06/D-07, IADR-0253 決定 4（#989 段 4）:
// 文書の共有先（DocumentShare）の付与・取り消し・一覧。
//
// 🔴 認可の変更なので、否定形（許してはならない操作が通らない）と陽性対照を対で置く。
// 「常に拒否する実装」は否定形だけを通す——所有者ができることの固定が証拠の残り半分である。
//
// 拒否の応答は 404 である（ADR-0056 決定 1・[[IADR-0277]]）。不在との区別ができないことが
// 存在秘匿の狙いであり、区別できないぶんの検出力は陽性対照が担う。
public class DocumentShareTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private record ShareDto(string SubjectType, string SubjectId, string GrantedBy,
        DateTimeOffset CreatedAt);

    private HttpClient ClientAs(string? user = null)
    {
        var client = factory.CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    // 所有者つきの文書を作る（作成は管理者経路。owner は属性である）。
    private async Task<Guid> CreateOwnedAsync(string owner)
    {
        var resp = await ClientAs().PostAsJsonAsync("/documents", new
        {
            title = $"共有対象 {Guid.NewGuid():N}",
            attributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "restricted",
                ["owner"] = owner,
            },
            tags = new List<string>(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>())!.Id;
    }

    // FR-20, ADR-0036 D-06（陽性対照）: 所有者は個人へ共有を付与でき、一覧で確認できる。
    [Fact]
    public async Task 所有者は共有を付与し一覧で確認できる()
    {
        var docId = await CreateOwnedAsync("alice");
        var owner = ClientAs("alice");

        var grant = await owner.PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken);
        grant.StatusCode.Should().Be(HttpStatusCode.Created);

        var shares = await owner.GetFromJsonAsync<List<ShareDto>>(
            $"/documents/{docId}/shares", TestContext.Current.CancellationToken);
        shares.Should().ContainSingle();
        shares![0].SubjectType.Should().Be("user");
        shares[0].SubjectId.Should().Be("bob");
        shares[0].GrantedBy.Should().Be("alice", "誰が共有したかは監査の記録である");
    }

    // FR-20, ADR-0036 D-06（陽性対照）: グループへの共有もできる（共有の単位は個人とグループの両方）。
    [Fact]
    public async Task 所有者はグループへも共有できる()
    {
        var docId = await CreateOwnedAsync("alice");

        var grant = await ClientAs("alice").PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "group", subjectId = "sales-dept" }, TestContext.Current.CancellationToken);

        grant.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // FR-19, ADR-0036 D-06/D-08（否定形）: 所有者でない主体は共有を付与できない。
    // 既定の役割は platform-admin である——**管理者ロールでも所有者でなければ拒否される**
    // （共有の管理はロールでなく所有の裁量。平時は管理者も裁量外）。
    //
    // 🔴 **拒否は 404 である**（ADR-0056 決定 1・[[IADR-0277]]）。403 を返すと
    // 「権限が無いだけで存在はする」が漏れ、文書 ID の総当たりで実在を判別できてしまう。
    [Fact]
    public async Task 所有者でない主体は管理者ロールでも共有を付与できない()
    {
        var docId = await CreateOwnedAsync("alice");

        var grant = await ClientAs("mallory").PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "mallory-friend" }, TestContext.Current.CancellationToken);

        grant.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "他人の文書を共有できるなら、共有経路が所有者裁量制御の抜け道になる");
    }

    // FR-19, ADR-0036 D-06（否定形・再共有不可）: 共有された相手は第三者へ共有できない。
    // 所有者の制御が及ぶ範囲を保つ——被共有者は owner ではないため拒否される（404。ADR-0056 決定 1）。
    [Fact]
    public async Task 被共有者は第三者へ再共有できない()
    {
        var docId = await CreateOwnedAsync("alice");
        (await ClientAs("alice").PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var reshare = await ClientAs("bob").PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "carol" }, TestContext.Current.CancellationToken);

        reshare.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "再共有を許すと所有者の制御が及ばない範囲へ文書が広がる（計画は再共有不可と定めている）");
    }

    // FR-20, ADR-0036 D-06（陽性対照）: 所有者は共有を取り消せる。
    [Fact]
    public async Task 所有者は共有を取り消せる()
    {
        var docId = await CreateOwnedAsync("alice");
        var owner = ClientAs("alice");
        (await owner.PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var revoke = await owner.DeleteAsync($"/documents/{docId}/shares/user/bob", TestContext.Current.CancellationToken);

        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<ShareDto>>(
            $"/documents/{docId}/shares", TestContext.Current.CancellationToken))
            .Should().BeEmpty("取り消し後に共有が残ると、剥奪したはずの到達権が記録として残る");
    }

    // FR-19（否定形・上と対）: 所有者でない主体（被共有者を含む）は共有を取り消せない。
    [Fact]
    public async Task 被共有者は自分以外の共有を取り消せない()
    {
        var docId = await CreateOwnedAsync("alice");
        var owner = ClientAs("alice");
        foreach (var subject in new[] { "bob", "carol" })
            (await owner.PostAsJsonAsync($"/documents/{docId}/shares",
                new { subjectType = "user", subjectId = subject }, TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.Created);

        var revoke = await ClientAs("bob").DeleteAsync($"/documents/{docId}/shares/user/carol", TestContext.Current.CancellationToken);

        revoke.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-20: 同一主体への重複付与は 409（同一文書 × 同一主体の共有は 1 行）。
    [Fact]
    public async Task 重複した共有の付与は409になる()
    {
        var docId = await CreateOwnedAsync("alice");
        var owner = ClientAs("alice");
        (await owner.PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var duplicate = await owner.PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken);

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // FR-20（否定形）: 種別の値域は user / group のみ。値域外は 400。
    [Fact]
    public async Task 値域外の共有種別は400になる()
    {
        var docId = await CreateOwnedAsync("alice");

        var grant = await ClientAs("alice").PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "everyone", subjectId = "all" }, TestContext.Current.CancellationToken);

        grant.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "種別の語彙が緩むと『全員』のような主体が紛れ、共有が公開に化ける");
    }

    // FR-19, FR-05（否定形・deny-by-default）: owner 属性を持たない文書は誰も共有できない
    // （取り込み経路の既定は owner=system。所有者ベースの裁量が働かない文書に共有の入口は無い）。
    [Fact]
    public async Task owner属性の無い文書は誰も共有できない()
    {
        var resp = await ClientAs().PostAsJsonAsync("/documents", new
        {
            title = "owner なし",
            attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        var docId = (await resp.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken))!.Id;

        var grant = await ClientAs("alice").PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken);

        grant.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 存在しない文書への共有操作は 404（存在の作り込みをしない）。
    //
    // 🔴 **ADR-0056 決定 1 の適用後、本テストは「不在」と「拒否」を区別しない。**
    // 両方 404 になるのが**存在秘匿の狙いそのもの**であり、区別できることが値ではない。
    // 検出力は上の陽性対照（所有者は 201 / 200 になる）が担う。
    [Fact]
    public async Task 存在しない文書への共有は404になる()
    {
        var grant = await ClientAs("alice").PostAsJsonAsync(
            $"/documents/{Guid.NewGuid()}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken);

        grant.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
