using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Domain;

// FR-17, SC-18, ADR-0054 (#917 / IADR-0274 決定 2・3): ノードの個人資料フラグ。
//
// 描き分け（組織文書＝円＋📄 / 個人資料＝角丸四角＋👤）のための 1 bit を応答へ載せる。
// 🔴 **値が無い文書は組織文書（false）である** —— `doc_scope` は実データ 0 件・遡及付与しない方針
// （ADR-0054 §結果）であり、否定形（「organization でない」）で書くと属性を持たない既存文書が
// すべて個人資料に化ける。2 つの書き方は private-note の文書だけでは見分けがつかないため、
// **属性を持たない文書が false になる陽性対照**を必ず対で置く（McpServer の
// ServiceAccountDocumentFilterTests と同じ構図）。
public class NodeScopeFlagTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public NodeScopeFlagTests(TestWebApplicationFactory factory) => _factory = factory;

    private static GraphDocument Node(Guid id, string name, string? docScope = null)
    {
        var attrs = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        if (docScope is not null)
            attrs["doc_scope"] = docScope;
        return GraphDocument.Create(id, name, attrs, null, DateTimeOffset.UtcNow);
    }

    private sealed record NodeDto(Guid DocumentId, string Title, bool IsPrivateNote);
    private sealed record View(List<NodeDto> Nodes);

    private async Task<View> GetNeighborsAsync(Guid origin)
    {
        var res = await _factory.CreateClient()
            .GetAsync($"/graph/{origin}/neighbors", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var v = await res.Content.ReadFromJsonAsync<View>(TestContext.Current.CancellationToken);
        v.Should().NotBeNull();
        return v!;
    }

    private async Task<Guid> SeedAsync(params GraphDocument[] leaves)
    {
        var origin = Guid.NewGuid();
        var type = EdgeType.Create($"t-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            db.Documents.Add(Node(origin, "origin"));
            foreach (var leaf in leaves)
            {
                db.Documents.Add(leaf);
                db.Edges.Add(Edge.Create(origin, leaf.DocumentId, type.Id, false, EdgeProvenance.Auto));
            }
            return Task.CompletedTask;
        });
        return origin;
    }

    // FR-17, SC-18, ADR-0054: doc_scope=private-note のノードだけが IsPrivateNote=true で返る。
    // 同一応答の中の組織文書（明示 organization）は false —— 1 つの応答で対にして固定する。
    [Fact]
    public async Task Private_note_nodes_carry_the_flag_and_organization_nodes_do_not()
    {
        var privateNote = Node(Guid.NewGuid(), "個人メモ", "private-note");
        var organization = Node(Guid.NewGuid(), "組織文書", "organization");
        var origin = await SeedAsync(privateNote, organization);

        var view = await GetNeighborsAsync(origin);

        view.Nodes.Single(n => n.DocumentId == privateNote.DocumentId).IsPrivateNote.Should().BeTrue();
        view.Nodes.Single(n => n.DocumentId == organization.DocumentId).IsPrivateNote.Should().BeFalse();
    }

    // FR-17, SC-18, ADR-0054 決定 5 陽性対照（🔴 これが本テストクラスの要点である）:
    // **doc_scope を持たない文書は組織文書（false）として返る。**
    //
    // 判定を否定形（「organization でない」）で書くと本テストだけが落ちる —— 実データは 0 件・
    // 遡及付与しない方針のため、運用開始直後の画面はほぼ全ノードがこの経路を通る。
    [Fact]
    public async Task Nodes_without_doc_scope_are_organization_documents()
    {
        var legacy = Node(Guid.NewGuid(), "doc_scope 新設前の文書");
        var origin = await SeedAsync(legacy);

        var view = await GetNeighborsAsync(origin);

        view.Nodes.Single(n => n.DocumentId == legacy.DocumentId).IsPrivateNote.Should().BeFalse(
            "値が無い ⇒ 組織文書（ADR-0054 決定 5: 取り込み経路が個人資料を作ることはない）");
    }

    // FR-17, SC-18: 値の照合は大文字小文字を区別しない（属性複製の揺れで描き分けが割れない）。
    [Fact]
    public async Task Doc_scope_comparison_is_case_insensitive()
    {
        var shouting = Node(Guid.NewGuid(), "大文字", "PRIVATE-NOTE");
        var origin = await SeedAsync(shouting);

        var view = await GetNeighborsAsync(origin);

        view.Nodes.Single(n => n.DocumentId == shouting.DocumentId).IsPrivateNote.Should().BeTrue();
    }
}
