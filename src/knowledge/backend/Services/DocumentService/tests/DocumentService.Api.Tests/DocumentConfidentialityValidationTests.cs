using System.Net;
using System.Net.Http.Json;
using DocumentService.Api.Foundation.Domain;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace DocumentService.Api.Tests;

// FR-05, FR-06, UC-03, SC-05, IADR-0047, Issue #199:
// 必須属性（機密区分 confidentiality）のサーバー側検証。BFF/フロントの既定値に依存せず、DocumentService が
// 最終防衛線として欠落・未知値を 400 で拒否する（admin/operator 直叩き・別クライアント経路を含む）。
// 認可（admin/operator）は既定の TestAuthHandler（platform-admin）で通過し、以降は検証の合否のみを見る。
public class DocumentConfidentialityValidationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client() => factory.CreateClient();

    // UC-03 例外フロー: attributes 未指定は保存拒否（400）。
    [Fact]
    public async Task Create_WithoutAttributes_Returns400()
    {
        var resp = await Client().PostAsJsonAsync("/documents", new { title = "no-attrs" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // UC-03 例外フロー: confidentiality を含まない属性は保存拒否（400）。
    [Fact]
    public async Task Create_MissingConfidentiality_Returns400()
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title = "dept-only", attributes = new Dictionary<string, string> { ["dept"] = "sales" } });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // SC-05: 未知の機密区分（正準集合外）は保存拒否（400）。"secret" は正準集合に含まれない。
    [Theory]
    [InlineData("secret")]
    [InlineData("")]
    [InlineData("Internal")] // 大文字小文字を区別する（正準は小文字）
    public async Task Create_UnknownConfidentiality_Returns400(string value)
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title = "bad-conf", attributes = new Dictionary<string, string> { ["confidentiality"] = value } });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // SC-05: 正準値（public/internal/confidential/restricted）は受理（201）。
    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    [InlineData("confidential")]
    [InlineData("restricted")]
    public async Task Create_ValidConfidentiality_Returns201(string value)
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title = "ok", attributes = new Dictionary<string, string> { ["confidentiality"] = value } });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await resp.Content.ReadFromJsonAsync<DocumentDto>();
        doc!.Attributes[DocumentAttributes.ConfidentialityKey].Should().Be(value);
    }

    // 更新（PUT・属性全置換）でも機密区分を必須検証する（欠落 400 / 正準値 200）。
    [Fact]
    public async Task Update_MissingConfidentiality_Returns400()
    {
        var id = await CreateValidAsync();
        var resp = await Client().PutAsJsonAsync($"/documents/{id}",
            new { title = "updated", attributes = new Dictionary<string, string> { ["dept"] = "sales" } });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ValidConfidentiality_Returns200()
    {
        var id = await CreateValidAsync();
        var resp = await Client().PutAsJsonAsync($"/documents/{id}",
            new { title = "updated", attributes = new Dictionary<string, string> { ["confidentiality"] = "confidential" } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // メタデータ更新（PATCH・属性全置換）でも機密区分を必須検証する（欠落 400 / 正準値 200）。
    [Fact]
    public async Task PatchMetadata_MissingConfidentiality_Returns400()
    {
        var id = await CreateValidAsync();
        var resp = await Client().PatchAsJsonAsync($"/documents/{id}/metadata",
            new { attributes = new Dictionary<string, string> { ["dept"] = "sales" }, tags = Array.Empty<string>() });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchMetadata_ValidConfidentiality_Returns200()
    {
        var id = await CreateValidAsync();
        var resp = await Client().PatchAsJsonAsync($"/documents/{id}/metadata",
            new { attributes = new Dictionary<string, string> { ["confidentiality"] = "restricted" }, tags = Array.Empty<string>() });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<Guid> CreateValidAsync()
    {
        var resp = await Client().PostAsJsonAsync("/documents",
            new { title = "seed", attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" } });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await resp.Content.ReadFromJsonAsync<DocumentDto>();
        return doc!.Id;
    }
}
