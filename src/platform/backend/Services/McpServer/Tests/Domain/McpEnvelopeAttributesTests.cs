using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Features.McpClients;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;

namespace McpServer.Tests.Domain;

// 🔴 X-55（FR-16, UC-08, ADR-0024 §4, ADR-0034 決定 9, [[IADR-0479]] 2026-09-28 追記 / #1671）:
// **エンベロープの属性の許可リスト（`McpEnvelopeAttributes`）と、MCP サーバーの読み手のキーが同じ 1 か所を指す**ことを固定する。
//
// 受け口（検索・グラフの実行口）は許可リストのキーだけを運ぶ（各サービスの試験 X-50・X-52 が固定）。ここはその裏側 ——
// **読み手が読むキーが許可リストから外れると、受け口がそのキーを運ばなくなり、越境判定・2 層目の除外が黙って効かなくなる。**
// 共有の定数から 1 つ外すと、こちら（読み手）と受け口の両方が赤になる。
[Trait("TestKind", "Unit")]
public class McpEnvelopeAttributesTests
{
    // 読み手の 3 か所（越境判定・個人資料の除外・制限プロジェクトの除外）のキーが許可リストに在り、許可リストはそれだけである
    // （読まないキーを運ぶ理由は無い）。
    [Fact]
    public void 読み手のキーが許可リストに在り許可リストはそれだけ()
    {
        string[] readers = [EgressPolicy.ConfidentialityKey, DocumentScope.Key, RestrictedProject.DocumentKey];

        McpEnvelopeAttributes.Keys.Should().BeEquivalentTo(readers);
        readers.Should().OnlyContain(k => McpEnvelopeAttributes.IsCarried(k));
        McpEnvelopeAttributes.IsCarried("owner").Should().BeFalse();
        McpEnvelopeAttributes.IsCarried("dept").Should().BeFalse();
        McpEnvelopeAttributes.IsCarried("shared_with").Should().BeFalse();
    }

    // 振る舞いで固定する: 受け口が許可リストで濾した属性だけでも、読み手の統制は 3 つとも効く。
    // （許可リストから 1 つ外すと、その統制がここで効かなくなる。）
    [Fact]
    public void 許可リストで濾した属性だけで越境判定と2層目の除外が効く()
    {
        var privateNote = Carried("p", new() { ["doc_scope"] = "private-note", ["confidentiality"] = "internal", ["owner"] = "alice" });
        var restricted = Carried("r", new() { ["project"] = "ai-stock-trading", ["confidentiality"] = "internal", ["dept"] = "x" });
        var confidential = Carried("c", new() { ["confidentiality"] = "confidential", ["doc_scope"] = "organization" });
        var result = new McpToolResult([privateNote, restricted, confidential], 3);
        var agent = new McpSubject("batch-agent", "batch-agent", McpClientKind.ServiceAccount, new Dictionary<string, string>());

        var filtered = new ServiceAccountDocumentFilter(NullLogger<ServiceAccountDocumentFilter>.Instance).Apply(agent, result);
        var egressed = new EgressPolicy().Apply(EgressTier.StandardExternal, filtered);

        egressed.Documents.Select(d => d.DocumentId).Should().Equal(["c"], "個人資料と制限プロジェクトは 2 層目で落ちる");
        egressed.TotalCount.Should().Be(1);
        egressed.Documents.Single().Body.Should().BeNull("機密区分が運ばれていれば標準外部 API へ本文を出さない");
        privateNote.Attributes.Should().NotContainKey("owner", "許可リストの外は運ばれない");
    }

    // 受け口の写像と同じ濾し方（`McpEnvelopeAttributes.IsCarried`）。
    private static McpToolDocument Carried(string id, Dictionary<string, string> source) =>
        new(id, id, source.Where(kv => McpEnvelopeAttributes.IsCarried(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            Body: "本文");
}
