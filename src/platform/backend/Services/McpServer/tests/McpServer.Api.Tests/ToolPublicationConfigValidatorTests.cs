using AwesomeAssertions;
using McpServer.Api.Foundation.Contracts;
using McpServer.Api.Foundation.Services;

namespace McpServer.Api.Tests;

// FR-16: 公開構成のスキーマ検証（計画 ADR-0024 §5 / 2026-08-02 注記）。
public class ToolPublicationConfigValidatorTests
{
    private static ToolPublicationConfig Config(
        IReadOnlyList<ToolPublicationEntry> tools,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? attributes = null)
        => new("v1", tools, attributes);

    // FR-16: 正常な構成は検証を通る。
    [Fact]
    public void 正常な公開構成は検証を通る()
    {
        var errors = ToolPublicationConfigValidator.Validate(
            Config([new ToolPublicationEntry("retrieval.search", "retrieval")]));

        errors.Should().BeEmpty();
    }

    // FR-16: 公開名の一意性（計画 ADR-0024 §5「ツール名の一意性」）。
    [Fact]
    public void 公開名の重複は検証で弾かれる()
    {
        var errors = ToolPublicationConfigValidator.Validate(Config(
        [
            new ToolPublicationEntry("retrieval.search", "retrieval", "search"),
            new ToolPublicationEntry("document.search", "document", "search")
        ]));

        errors.Should().ContainSingle().Which.Should().Contain("重複");
    }

    // FR-16: **AI 分析系（ai.*）は初期公開範囲外**（計画 ADR-0024 §決定「初期公開範囲」）。
    // 構成に書けてしまうとレビューを通り抜けた瞬間に公開されるため、検証で弾く。
    [Fact]
    public void AI分析系ツールは公開構成に書けない()
    {
        var errors = ToolPublicationConfigValidator.Validate(
            Config([new ToolPublicationEntry("ai.summarize", "ai-analysis")]));

        errors.Should().ContainSingle().Which.Should().Contain("初期公開範囲外");
    }

    // FR-16: 要約系（LLM 呼び出しを伴う get_cluster_summary）も公開しない（計画 2026-07-30 裁定）。
    [Fact]
    public void クラスタ要約ツールは公開構成に書けない()
    {
        var errors = ToolPublicationConfigValidator.Validate(
            Config([new ToolPublicationEntry("graph.get_cluster_summary", "graph")]));

        errors.Should().ContainSingle().Which.Should().Contain("初期公開範囲外");
    }

    // 🔴 FR-16: サービスアカウントへ個人資料を読ませる属性割当は**構成上禁止**であり、
    // スキーマ検証で弾く（計画 ADR-0024 2026-08-02 注記 / ADR-0034 決定 9）。
    [Fact]
    public void サービスアカウントへ個人資料の属性割当は禁止される()
    {
        var errors = ToolPublicationConfigValidator.Validate(Config(
            [new ToolPublicationEntry("retrieval.search", "retrieval")],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["batch-agent"] = new Dictionary<string, string>
                {
                    ["doc_scope"] = "private-note"
                }
            }));

        errors.Should().ContainSingle().Which.Should().Contain("private-note");
    }

    // FR-16（陽性対照）: 組織文書の属性割当は許される。
    // 「属性割当を一切受け付けない」実装と区別するために置く。
    [Fact]
    public void サービスアカウントへ組織文書の属性割当は許される()
    {
        var errors = ToolPublicationConfigValidator.Validate(Config(
            [new ToolPublicationEntry("retrieval.search", "retrieval")],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["batch-agent"] = new Dictionary<string, string>
                {
                    ["doc_scope"] = "organization",
                    ["confidentiality"] = "internal"
                }
            }));

        errors.Should().BeEmpty();
    }
}
