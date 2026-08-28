using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients;
using McpServer.Features.Tools;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Tests;

// FR-16: サービスアカウント実行時の個人資料一律除外（計画 ADR-0034 決定 9 / ADR-0024 2026-08-02 注記）。
//
// 🔴 **否定形テストと陽性対照を対で置く。** 否定形（個人資料が返らない）だけでは
// 「全部落としている実装」と区別できず、除外の向き（集合帰属か否定か）も分けられない。
public class ServiceAccountDocumentFilterTests
{
    private static readonly ServiceAccountDocumentFilter Filter =
        new(NullLogger<ServiceAccountDocumentFilter>.Instance);

    private static McpSubject ServiceAccount(string owner = "batch-agent") =>
        new(owner, owner, McpClientKind.ServiceAccount, new Dictionary<string, string>());

    private static McpSubject Human(string user = "alice") =>
        new(user, "claude-desktop", McpClientKind.Interactive, new Dictionary<string, string>());

    private static McpToolDocument PrivateNote(string id = "doc-private", string owner = "alice") =>
        new(id, "個人メモ", new Dictionary<string, string>
        {
            ["doc_scope"] = "private-note",
            ["owner"] = owner,
            ["confidentiality"] = "restricted"
        }, Body: "個人資料の本文");

    private static McpToolDocument OrganizationDoc(string id = "doc-org") =>
        new(id, "組織文書", new Dictionary<string, string>
        {
            ["doc_scope"] = "organization",
            ["confidentiality"] = "internal"
        }, Body: "組織文書の本文");

    // doc_scope を持たない既存文書（ADR-0054 は 2026-08-22 新設で遡及付与しない）。
    private static McpToolDocument LegacyDoc(string id = "doc-legacy") =>
        new(id, "属性未付与の既存文書", new Dictionary<string, string>
        {
            ["confidentiality"] = "internal"
        }, Body: "既存文書の本文");

    // FR-16（否定形）: サービスアカウント実行では個人資料が応答に一切現れない。
    [Fact]
    public void サービスアカウント実行では個人資料が返らない()
    {
        var result = new McpToolResult([PrivateNote(), OrganizationDoc()], TotalCount: 2);

        var filtered = Filter.Apply(ServiceAccount(), result);

        filtered.Documents.Should().ContainSingle().Which.DocumentId.Should().Be("doc-org");
        filtered.Documents.Should().NotContain(d => d.DocumentId == "doc-private");
    }

    // FR-16（否定形）: **所有者本人のサービスアカウントでも**個人資料は返らない。
    // 計画は「所有者の個人資料であっても含めない」と明示している（ADR-0034 決定 9）。
    [Fact]
    public void 所有者本人のサービスアカウントでも個人資料は返らない()
    {
        var result = new McpToolResult([PrivateNote(owner: "batch-agent")], TotalCount: 1);

        var filtered = Filter.Apply(ServiceAccount("batch-agent"), result);

        filtered.Documents.Should().BeEmpty();
    }

    // FR-16（否定形）: 除外した文書は**件数にも含まれない**（ADR-0034 決定 2・4 の存在秘匿）。
    [Fact]
    public void 除外した個人資料は件数にも含まれない()
    {
        var result = new McpToolResult(
            [PrivateNote("p1"), PrivateNote("p2"), OrganizationDoc()], TotalCount: 3);

        var filtered = Filter.Apply(ServiceAccount(), result);

        filtered.Documents.Should().HaveCount(1);
        filtered.TotalCount.Should().Be(1);
    }

    // FR-16（陽性対照 1）: 同じ文書が**有人実行では返る**。
    // これが無いと「常に全部落としている実装」と区別できない。
    [Fact]
    public void 有人実行では同じ個人資料が返る()
    {
        var result = new McpToolResult([PrivateNote(), OrganizationDoc()], TotalCount: 2);

        var filtered = Filter.Apply(Human(), result);

        filtered.Documents.Should().HaveCount(2);
        filtered.Documents.Should().Contain(d => d.DocumentId == "doc-private");
        filtered.TotalCount.Should().Be(2);
    }

    // FR-16（陽性対照 2）: **doc_scope を持たない文書は除外されない**。
    // 判定を否定（`!= "organization"`）で書いていたらここが落ちる —— 集合帰属で書いたことの証明。
    [Fact]
    public void doc_scope_を持たない文書はサービスアカウントでも返る()
    {
        var result = new McpToolResult([LegacyDoc(), OrganizationDoc()], TotalCount: 2);

        var filtered = Filter.Apply(ServiceAccount(), result);

        filtered.Documents.Should().HaveCount(2);
        filtered.TotalCount.Should().Be(2);
    }

    // FR-16（陽性対照 3）: 組織文書はサービスアカウントでも返る。
    [Fact]
    public void 組織文書はサービスアカウントでも返る()
    {
        var result = new McpToolResult([OrganizationDoc()], TotalCount: 1);

        Filter.Apply(ServiceAccount(), result).Documents.Should().ContainSingle();
    }

    // FR-16: 綴りの揺れ（大文字小文字）でも個人資料と判定する。
    [Theory]
    [InlineData("private-note")]
    [InlineData("Private-Note")]
    [InlineData("PRIVATE-NOTE")]
    public void 個人資料の判定は大文字小文字を問わない(string scope)
    {
        var doc = new McpToolDocument("d", "t",
            new Dictionary<string, string> { ["doc_scope"] = scope });

        Filter.Apply(ServiceAccount(), new McpToolResult([doc], 1)).Documents.Should().BeEmpty();
    }
}
