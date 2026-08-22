using AwesomeAssertions;
using McpServer.Api.Foundation.Contracts;
using McpServer.Api.Foundation.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpServer.Api.Tests;

// FR-16: 宣言的公開構成（許可リスト）と自己申告の突合（計画 ADR-0024 §2・§5）。
public class ToolCatalogTests
{
    private static ToolCatalog NewCatalog() => new(NullLogger<ToolCatalog>.Instance);

    private static McpToolDeclaration Decl(string name, string egressClass = "internal") =>
        new(name, "説明", """{"type":"object"}""", "http://svc/exec", "read", egressClass);

    // FR-16: **既定は非公開**。申告があっても公開構成に無いツールは公開しない。
    [Fact]
    public void 公開構成に無い申告は公開されない()
    {
        var catalog = NewCatalog();
        catalog.Refresh(
            new ToolPublicationConfig("v1", [new ToolPublicationEntry("retrieval.search", "retrieval")]),
            [new ServiceToolDeclarations("retrieval",
                [Decl("retrieval.search"), Decl("retrieval.secret_dump")])]);

        catalog.PublishedTools.Select(t => t.PublishedName).Should().Equal("retrieval.search");
        catalog.Find("retrieval.secret_dump").Should().BeNull();
    }

    // FR-16: 空の公開構成では 1 件も公開されない（許可リスト方式の既定）。
    [Fact]
    public void 公開構成が空なら何も公開されない()
    {
        var catalog = NewCatalog();
        catalog.Refresh(new ToolPublicationConfig("v1", []),
            [new ServiceToolDeclarations("retrieval", [Decl("retrieval.search")])]);

        catalog.PublishedTools.Should().BeEmpty();
    }

    // FR-16: 公開宣言に対して申告が無い場合は構成ドリフトとして報告し、**公開はしない**
    // （計画 ADR-0024 §5）。
    [Fact]
    public void 申告の無い公開宣言はドリフトとして報告し公開しない()
    {
        var catalog = NewCatalog();
        catalog.Refresh(
            new ToolPublicationConfig("v1", [new ToolPublicationEntry("graph.traverse", "graph")]),
            []);

        catalog.PublishedTools.Should().BeEmpty();
        catalog.Drifts.Should().ContainSingle().Which.Kind.Should().Be("missing-declaration");
    }

    // FR-16: egress_class を欠く申告は公開しない（計画 ADR-0024 §5「egress_class 必須」）。
    [Fact]
    public void egress_class_を欠く申告は公開しない()
    {
        var catalog = NewCatalog();
        catalog.Refresh(
            new ToolPublicationConfig("v1", [new ToolPublicationEntry("retrieval.search", "retrieval")]),
            [new ServiceToolDeclarations("retrieval", [Decl("retrieval.search", egressClass: "")])]);

        catalog.PublishedTools.Should().BeEmpty();
        catalog.Drifts.Should().ContainSingle().Which.Kind.Should().Be("missing-egress-class");
    }

    // FR-16: 公開名の差し替え（published_name）が効く。
    [Fact]
    public void 公開名を差し替えられる()
    {
        var catalog = NewCatalog();
        catalog.Refresh(
            new ToolPublicationConfig("v1",
                [new ToolPublicationEntry("retrieval.search", "retrieval", "search_documents")]),
            [new ServiceToolDeclarations("retrieval", [Decl("retrieval.search")])]);

        catalog.Find("search_documents").Should().NotBeNull();
        catalog.Find("retrieval.search").Should().BeNull();
    }

    // FR-16: 同じサービス名のツールでも、申告元サービスが違えば別物として扱う
    // （公開構成は service と name の対で申告を引く）。
    [Fact]
    public void 別サービスの同名ツールは公開構成のサービス名で区別される()
    {
        var catalog = NewCatalog();
        catalog.Refresh(
            new ToolPublicationConfig("v1", [new ToolPublicationEntry("search", "retrieval")]),
            [new ServiceToolDeclarations("document", [Decl("search")])]);

        catalog.PublishedTools.Should().BeEmpty();
        catalog.Drifts.Should().ContainSingle();
    }

    // FR-16: 実効一覧が変わったときだけ版が進む（ツール一覧変化の検知点）。
    [Fact]
    public void 実効一覧が変わったときだけ版が進む()
    {
        var catalog = NewCatalog();
        var config = new ToolPublicationConfig("v1",
            [new ToolPublicationEntry("retrieval.search", "retrieval")]);
        var declarations = new List<ServiceToolDeclarations>
        {
            new("retrieval", [Decl("retrieval.search")])
        };

        catalog.Refresh(config, declarations);
        var first = catalog.Version;

        catalog.Refresh(config, declarations);
        catalog.Version.Should().Be(first);

        catalog.Refresh(new ToolPublicationConfig("v2", []), declarations);
        catalog.Version.Should().Be(first + 1);
    }
}
