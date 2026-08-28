using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests;

// FR-20, FR-21, UC-11, ADR-0036 D-04, ADR-0056 決定 1, [[IADR-0277]]:
// **所有者ベースの書き込み経路は、拒否と不在を区別させない。**
//
// 🔴 **何を固定しているか。** ADR-0056 は打ち分けの軸を「**主体がその文書を読めるか**」と定め、
// 読めない相手には常に「見つからない」と答えることを課した（決定 1）。決定 2 は「読めるが
// 書けない」なら 403 を**返してよい**とするが、**本サービスは ABAC の読み取り判定を持たない**
// ため、`CanWrite` が偽のときにどちらの側かを言い切れない。したがって fail-closed 側へ倒す。
//
// **これを測らないと何が起きるか** —— 403 と 404 を打ち分ける実装では、任意の認証利用者が
// 文書 ID を総当たりし、**応答の差だけで実在を判別できる**。ADR-0036 D-04 の存在秘匿は
// 「読めない文書の存在を知らせない」ことであり、応答コードの差はその抜け道になる。
//
// 🔴 **陰性だけでは緑にならないようにする。** 「全経路が常に 404」でも陰性は通るため、
// **所有者が同じ経路で成功することを対で固定する**。
public class DocumentExistenceConcealmentTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient ClientAs(string? user = null)
    {
        var client = factory.CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    private async Task<Guid> CreateOwnedAsync(string owner)
    {
        var resp = await ClientAs().PostAsJsonAsync("/documents", new
        {
            title = $"存在秘匿の対象 {Guid.NewGuid():N}",
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

    private static HttpRequestMessage Request(string method, string path) => method switch
    {
        "GET" => new HttpRequestMessage(HttpMethod.Get, path),
        "DELETE" => new HttpRequestMessage(HttpMethod.Delete, path),
        "POST" => new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { subjectType = "user", subjectId = "carol" })
        },
        "PUT" => new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(new { body = "他人が上書きした本文" })
        },
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    // 対象の 4 経路（母集合。`Status403Forbidden` を返していた全箇所）。
    public static TheoryData<string, string> WriteRoutes() => new()
    {
        { "GET", "/documents/{id}/shares" },
        { "POST", "/documents/{id}/shares" },
        { "DELETE", "/documents/{id}/shares/user/bob" },
        { "PUT", "/documents/{id}/body" },
    };

    // 陰性: 所有者でない主体には、**実在する文書も存在しない文書と同じ 404** に見える。
    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task 所有者でない主体には実在する文書と不在の文書が区別できない(
        string method, string template)
    {
        var existing = await CreateOwnedAsync("alice");
        var absent = Guid.NewGuid();
        var mallory = ClientAs("mallory");

        using var toExisting = Request(method, template.Replace("{id}", existing.ToString()));
        using var toAbsent = Request(method, template.Replace("{id}", absent.ToString()));

        var onExisting = await mallory.SendAsync(toExisting, TestContext.Current.CancellationToken);
        var onAbsent = await mallory.SendAsync(toAbsent, TestContext.Current.CancellationToken);

        onExisting.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // 🔴 **これが主張の本体である。** 片方が 403 なら、応答の差で実在が判る。
        onExisting.StatusCode.Should().Be(onAbsent.StatusCode,
            "実在する文書と不在の文書で応答が違うと、ID の総当たりで実在を判別できる");
    }

    // 🔴 陽性対照: 所有者は同じ 4 経路で成功する。
    // これが無いと「全経路が常に 404」の実装でも上の陰性が緑になる。
    [Fact]
    public async Task 所有者は同じ経路で操作できる()
    {
        var docId = await CreateOwnedAsync("alice");
        var alice = ClientAs("alice");

        (await alice.GetAsync($"/documents/{docId}/shares", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await alice.PostAsJsonAsync($"/documents/{docId}/shares",
            new { subjectType = "user", subjectId = "bob" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await alice.DeleteAsync($"/documents/{docId}/shares/user/bob", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await alice.PutAsJsonAsync($"/documents/{docId}/body", new { body = "alice の本文" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 陽性対照 2: 拒否は「読み取りごと塞いだ」ではない。
    // 所有者でない主体でも、**読み取り経路（GET /documents/{id}）は従来どおり**である。
    // ADR-0056 が改めたのは書き込み拒否の応答コードだけであり、可視性そのものは動かしていない。
    [Fact]
    public async Task 所有者でない主体でも文書の読み取り経路は塞がっていない()
    {
        var docId = await CreateOwnedAsync("alice");

        var resp = await ClientAs("mallory").GetAsync($"/documents/{docId}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "本 PR が変えたのは書き込み拒否の応答コードだけである");
    }
}
