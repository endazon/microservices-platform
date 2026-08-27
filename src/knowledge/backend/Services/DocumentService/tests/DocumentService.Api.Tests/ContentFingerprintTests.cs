using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Api.Foundation.Domain;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Api.Tests;

// FR-18, ADR-0050 決定 1 (#911): 本文指紋（ContentFingerprint）。
// 契約が要求する性質は 1 つ —— **本文が変われば変わり、変わらなければ変わらない**。
// #911 受け入れ基準「本文ハッシュが本文変化で変わり、無変化で変わらない」の写像である。
public class ContentFingerprintTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "fp-owner");
        return client;
    }

    // ── 純粋関数としての性質 ─────────────────────────────────────────────

    [Fact]
    public void 指紋は本文が変われば変わり_変わらなければ変わらない()
    {
        var a1 = DocumentBodyIntake.Fingerprint("# 規程\n\n本文A");
        var a2 = DocumentBodyIntake.Fingerprint("# 規程\n\n本文A");
        var b = DocumentBodyIntake.Fingerprint("# 規程\n\n本文B");

        a1.Should().Be(a2, "同じ本文からは常に同じ指紋（決定的）");
        a1.Should().NotBe(b, "本文が変われば指紋が変わる");
        a1.Should().MatchRegex("^[0-9a-f]{64}$", "SHA-256 小文字 hex（64 文字）の不透明な値");
    }

    // ── イベントが運ぶ（ADR-0050 決定 1） ─────────────────────────────────

    [Fact]
    public async Task 本文つき登録のDocumentUpdatedは本文の指紋を運ぶ()
    {
        const string body = "# 指紋対象\n\n本文いろは";
        var resp = await Client().PostAsJsonAsync("/documents", new
        {
            title = "指紋つき登録",
            body,
            attributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["owner"] = "fp-owner",
            },
            tags = new List<string>(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = (await resp.Content.ReadFromJsonAsync<DocumentDto>())!;

        var published = factory.Services.GetRequiredService<RecordingMessageBus>()
            .PublishedOf<DocumentUpdated>();
        published.Should().Contain(e =>
            e.DocumentId == doc.Id
            && e.ContentFingerprint == DocumentBodyIntake.Fingerprint(body));
    }

    [Fact]
    public async Task 本文の再投入で指紋が変わり_メタデータ更新では変わらない()
    {
        const string body1 = "# v1\n\n本文その一";
        const string body2 = "# v2\n\n本文その二";
        var create = await Client().PostAsJsonAsync("/documents", new
        {
            title = "指紋の遷移",
            body = body1,
            attributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "internal",
                ["owner"] = "fp-owner",
            },
            tags = new List<string>(),
        });
        var doc = (await create.Content.ReadFromJsonAsync<DocumentDto>())!;

        // 本文の再投入（所有者）→ 指紋が body2 のものへ進む。
        var put = await Client().PutAsJsonAsync($"/documents/{doc.Id}/body", new { body = body2 });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // メタデータのみ更新 → 直近の本文の指紋を**そのまま**運ぶ（本文は変わっていない）。
        var patch = await Client().PatchAsJsonAsync($"/documents/{doc.Id}/metadata", new
        {
            attributes = new Dictionary<string, string>
            {
                ["confidentiality"] = "restricted",
                ["owner"] = "fp-owner",
            },
            tags = new List<string>(),
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var mine = factory.Services.GetRequiredService<RecordingMessageBus>()
            .PublishedOf<DocumentUpdated>().Where(e => e.DocumentId == doc.Id).ToList();
        mine.Should().HaveCountGreaterThanOrEqualTo(3);
        mine[^2].ContentFingerprint.Should().Be(DocumentBodyIntake.Fingerprint(body2),
            "本文の再投入は指紋を進める（本文変化 → 指紋変化）");
        mine[^1].ContentFingerprint.Should().Be(DocumentBodyIntake.Fingerprint(body2),
            "メタデータ更新は本文を変えないので指紋は変わらない（UpdatedAt だけが進む）");
        mine[0].ContentFingerprint.Should().NotBe(mine[^1].ContentFingerprint);
    }
}
