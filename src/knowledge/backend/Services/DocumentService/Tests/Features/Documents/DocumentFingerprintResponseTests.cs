using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.Documents;

// FR-06, ADR-0050 決定 1 (#1575): **応答が本文指紋を運ぶ。**
//
// 呼び出し側（AST の KB 入れ直し）が「保存済みの本文が最新か」を判定できるようにする。
// 判定に要る性質は 2 つ —— ① 送った本文から呼び出し側が**同じ値を計算できる**（UTF-8 の SHA-256
// 小文字 hex）、② **本文が変われば変わり、メタデータだけの更新では変わらない**（ADR-0050 決定 1）。
// 期待値は本番の `DocumentBodyIntake.Fingerprint` を呼ばずに試験側で独立に計算する —— 本番の関数で
// 期待値を作ると、関数が変わったときに期待値も一緒に動き、呼び出し側との約束が破れても緑のままになる。
[Trait("TestKind", "Integration")]
public class DocumentFingerprintResponseTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "fp-reader");
        return client;
    }

    private static string Sha256Hex(string body)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private async Task<DocumentDto> CreateAsync(string title, string? body)
    {
        var resp = await Client().PostAsJsonAsync("/documents", new
        {
            title,
            body,
            attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken))!;
    }

    // FR-06, ADR-0050 決定 1 (#1575): 作成・取得・一覧の応答が、送った本文の指紋と一致する。
    [Fact]
    public async Task 本文つきで作った文書は_作成取得一覧の応答が送った本文の指紋を運ぶ()
    {
        const string body = "# 確定報告書\n\n本文（日本語を含む）";
        var created = await CreateAsync("指紋の応答", body);

        created.ContentFingerprint.Should().Be(Sha256Hex(body), "作成の応答");

        var got = await Client().GetFromJsonAsync<DocumentDto>($"/documents/{created.Id}", TestContext.Current.CancellationToken);
        got!.ContentFingerprint.Should().Be(Sha256Hex(body), "個別取得の応答");

        var list = await Client().GetFromJsonAsync<List<DocumentDto>>("/documents", TestContext.Current.CancellationToken);
        list!.Single(d => d.Id == created.Id).ContentFingerprint.Should().Be(Sha256Hex(body), "一覧の応答");
    }

    // FR-06, ADR-0050 決定 1 (#1575): 本文の無い文書は null（「指紋が無い」を空文字や既定値で偽らない）。
    [Fact]
    public async Task 本文なしで作った文書の指紋はnull()
    {
        var created = await CreateAsync("本文なし", body: null);

        created.ContentFingerprint.Should().BeNull();
        var got = await Client().GetFromJsonAsync<DocumentDto>($"/documents/{created.Id}", TestContext.Current.CancellationToken);
        got!.ContentFingerprint.Should().BeNull();
    }

    // FR-06, FR-21, ADR-0050 決定 1 (#1575): 本文を入れると指紋が現れ、差し替えると変わり、
    // メタデータだけの更新では変わらない。
    [Fact]
    public async Task 本文の投入と差し替えで指紋が進み_メタデータ更新では変わらない()
    {
        var created = await CreateAsync("指紋の遷移（応答）", body: null);

        const string body1 = "# v1\n\n一つ目";
        var put1 = await Client().PutAsJsonAsync($"/documents/{created.Id}/body", new { body = body1 }, TestContext.Current.CancellationToken);
        put1.StatusCode.Should().Be(HttpStatusCode.OK);
        (await put1.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken))!
            .ContentFingerprint.Should().Be(Sha256Hex(body1), "本文なし → 本文ありで指紋が現れる");

        const string body2 = "# v2\n\n二つ目";
        var put2 = await Client().PutAsJsonAsync($"/documents/{created.Id}/body", new { body = body2 }, TestContext.Current.CancellationToken);
        (await put2.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken))!
            .ContentFingerprint.Should().Be(Sha256Hex(body2), "本文の差し替えで指紋が変わる");

        var patch = await Client().PatchAsJsonAsync($"/documents/{created.Id}/metadata", new
        {
            attributes = new Dictionary<string, string> { ["confidentiality"] = "restricted" },
            tags = new List<string>(),
        }, TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        (await patch.Content.ReadFromJsonAsync<DocumentDto>(TestContext.Current.CancellationToken))!
            .ContentFingerprint.Should().Be(Sha256Hex(body2), "メタデータだけの更新では変わらない");
    }
}
