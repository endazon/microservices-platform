using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients;
using McpServer.Features.Tools;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;

namespace McpServer.Tests.Domain;

// FR-16: 公開構成のスキーマ検証（計画 ADR-0024 §5 / 2026-08-02 注記）。
[Trait("TestKind", "Unit")]
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

    // 🔴 FR-16 (#1190), AST/ADR-0032 決定 2 (2)(3): MCP のサービスアカウントへ
    // `ai-stock-trading` を含むプロジェクト属性を割り当てられない。**構成の検証で弾く**
    // （＝起動時 fail-fast。「①② と同じ場所・同じ形」）。
    [Fact]
    public void サービスアカウントへ制限プロジェクトの属性割当は禁止される()
    {
        var errors = ToolPublicationConfigValidator.Validate(Config(
            [new ToolPublicationEntry("retrieval.search", "retrieval")],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["batch-agent"] = new Dictionary<string, string>
                {
                    ["projects"] = "ai-stock-trading"
                }
            }));

        errors.Should().ContainSingle().Which.Should().Contain("ai-stock-trading");
    }

    // 🔴 FR-16 (#1190): **綴りの揺れで抜けない。** 計画は文書側を単数 `project`、主体側を複数
    // `projects` と定めるが、#1190 の本文は `attributes["project"]` と書く。片方だけ塞ぐと
    // 綴りを変えただけで通ってしまう。
    [Theory]
    [InlineData("projects")]
    [InlineData("project")]
    public void 制限プロジェクトの割当は単数複数どちらの綴りでも弾かれる(string key)
    {
        var errors = ToolPublicationConfigValidator.ValidateServiceAccountAttributes(
            "batch-agent", new Dictionary<string, string> { [key] = "ai-stock-trading" });

        errors.Should().ContainSingle().Which.Should().Contain("ai-stock-trading");
    }

    // FR-16 (#1190): 多値（`ServiceAccountAttributeSubset.Tokens` の分解）でも当たる。
    [Fact]
    public void 制限プロジェクトが多値の一部でも弾かれる()
    {
        var errors = ToolPublicationConfigValidator.ValidateServiceAccountAttributes(
            "batch-agent", new Dictionary<string, string> { ["projects"] = "kb, ai-stock-trading" });

        errors.Should().ContainSingle().Which.Should().Contain("ai-stock-trading");
    }

    // FR-16 (#1190・陽性対照): 制限外のプロジェクト属性は割り当てられる。
    // 🔴 これが無いと「`projects` を持つ割当を全部落とす実装」と区別できない。
    [Fact]
    public void 制限外のプロジェクト属性は割り当てられる()
    {
        var errors = ToolPublicationConfigValidator.ValidateServiceAccountAttributes(
            "batch-agent", new Dictionary<string, string> { ["projects"] = "knowledge-base, kb" });

        errors.Should().BeEmpty();
    }

    // 🔴 FR-16 (#1190): 2 つの違反があれば**両方返す**（最初の 1 件で打ち切らない）。
    // 打ち切ると、2 つ目は 1 つ目を直したあとの実行でしか現れない。
    [Fact]
    public void 個人資料と制限プロジェクトを同時に含む割当は両方の理由を返す()
    {
        var errors = ToolPublicationConfigValidator.ValidateServiceAccountAttributes(
            "batch-agent",
            new Dictionary<string, string>
            {
                ["doc_scope"] = "private-note",
                ["projects"] = "ai-stock-trading"
            });

        errors.Should().HaveCount(2);
        errors.Should().ContainSingle(e => e.Contains("private-note"));
        errors.Should().ContainSingle(e => e.Contains("ai-stock-trading"));
    }
}
