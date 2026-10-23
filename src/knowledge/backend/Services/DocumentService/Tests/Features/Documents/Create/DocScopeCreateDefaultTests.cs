using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents.Create;

// FR-19, SC-05, ADR-0054 決定 3・5, ADR-0058 決定 1, [[IADR-0598]] (#2112):
// **組織文書の作成口（`POST /documents`）で `doc_scope` が未指定なら `organization` を補う。**
//
// 🔴 **何を守っているか。** SC-05 の作成フォームは機密区分しか送らない。作成後の新規付与は
// 不変性検査（ADR-0058 決定 1）が拒否するため、作成時に付かなければ**恒久的に欠落する**。
// 補完を外すと受け入れ基準 1（陽性）が赤になる。
[Trait("TestKind", "Integration")]
public class DocScopeCreateDefaultTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> PostAsync(Dictionary<string, string> attributes, string? body = null) =>
        factory.CreateClient().PostAsJsonAsync("/documents", new
        {
            title = $"作成時の文書スコープ {Guid.NewGuid():N}",
            body,
            attributes,
            tags = new List<string>(),
        }, Ct);

    private async Task<Dictionary<string, string>> StoredAttributesAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        return doc.Attributes;
    }

    // 受け入れ基準 1（陽性）: SC-05 相当の要求（機密区分だけ・doc_scope 未指定）で作った文書は organization を持つ。
    // 応答だけでなく**保存された属性**を見る（応答の整形で付けただけの実装を落とす）。
    [Theory]
    [InlineData(null)]
    [InlineData("# 本文あり")]
    public async Task 文書スコープ未指定の作成はorganizationを持つ(string? body)
    {
        var resp = await PostAsync(new Dictionary<string, string> { ["confidentiality"] = "internal" }, body);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<DocumentDto>(Ct))!;
        dto.Attributes.Should().Contain(DocumentAttributes.DocScopeKey, DocumentAttributes.DocScopeOrganization);
        (await StoredAttributesAsync(dto.Id)).Should()
            .Contain(DocumentAttributes.DocScopeKey, DocumentAttributes.DocScopeOrganization,
                "作成後は付与できないので、作成時に保存されていなければ恒久的に欠落する（ADR-0058 決定 1）");
    }

    // 受け入れ基準 1 の続き: 補完された文書は SC-05 の通常の保存（既存属性のスプレッド＝同値の同送）で通り、
    // スコープを持ったまま残る（補完が不変性検査と衝突しないこと）。
    [Fact]
    public async Task 補完された文書はSC05の通常の保存で文書スコープを保つ()
    {
        var created = (await (await PostAsync(new Dictionary<string, string> { ["confidentiality"] = "internal" }))
            .Content.ReadFromJsonAsync<DocumentDto>(Ct))!;

        var resp = await factory.CreateClient().PutAsJsonAsync($"/documents/{created.Id}", new
        {
            title = created.Title,
            attributes = new Dictionary<string, string>(created.Attributes) { ["confidentiality"] = "confidential" },
            tags = new List<string>(),
        }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await resp.Content.ReadFromJsonAsync<DocumentDto>(Ct))!;
        updated.Attributes.Should().Contain(DocumentAttributes.DocScopeKey, DocumentAttributes.DocScopeOrganization);
    }

    // 受け入れ基準 3: 明示の organization はそのまま通る。
    [Fact]
    public async Task 明示のorganizationはそのまま通る()
    {
        var resp = await PostAsync(new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "organization",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<DocumentDto>(Ct))!;
        dto.Attributes.Should().Contain(DocumentAttributes.DocScopeKey, DocumentAttributes.DocScopeOrganization);
    }

    // 受け入れ基準 3: 不正値・空文字は既存の値域検証どおり 400（補完で上書きして通さない）。
    [Theory]
    [InlineData("team-shared")]
    [InlineData("")]
    [InlineData("Organization")]
    public async Task 不正な文書スコープは補完されず従来どおり400(string value)
    {
        var resp = await PostAsync(new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = value,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 受け入れ基準 2（陰性）: この口に private-note が来たら従来どおり 400（organization へ書き換えて通さない）。
    [Fact]
    public async Task 組織文書の作成口に個人資料を指定すると従来どおり400()
    {
        var resp = await PostAsync(new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["doc_scope"] = "private-note",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 受け入れ基準 2（陰性）: 個人資料の作成口（`/private-notes`）は organization を付けず private-note のまま。
    [Fact]
    public async Task 個人資料の作成経路ではorganizationが付かない()
    {
        var user = $"scope-{Guid.NewGuid():N}"[..20];
        var session = factory.CreateClient();
        session.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);

        var resp = await session.PostAsJsonAsync("/private-notes/", new { title = "個人メモ" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = (await resp.Content.ReadFromJsonAsync<PrivateNoteDto>(Ct))!;
        (await StoredAttributesAsync(note.Id)).Should()
            .Contain(DocumentAttributes.DocScopeKey, DocumentAttributes.DocScopePrivateNote);
    }
}

// FR-19, [[IADR-0598]]: 補完関数の単体。キーの有無だけで判定し、既存値を上書きしない。
public class WithOrganizationScopeIfAbsentTests
{
    [Fact]
    public void キーが無ければorganizationを補う()
    {
        var result = DocumentAttributes.WithOrganizationScopeIfAbsent(
            new Dictionary<string, string> { ["confidentiality"] = "internal" });

        result.Should().Contain(DocumentAttributes.DocScopeKey, DocumentAttributes.DocScopeOrganization);
    }

    [Theory]
    [InlineData("private-note")]
    [InlineData("organization")]
    [InlineData("")]
    public void キーが有れば値を変えない(string value)
    {
        var result = DocumentAttributes.WithOrganizationScopeIfAbsent(
            new Dictionary<string, string> { [DocumentAttributes.DocScopeKey] = value });

        result[DocumentAttributes.DocScopeKey].Should().Be(value);
    }
}
