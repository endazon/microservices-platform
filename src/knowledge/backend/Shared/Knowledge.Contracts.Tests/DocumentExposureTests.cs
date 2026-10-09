using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Xunit;

namespace Knowledge.Contracts.Tests;

// FR-19, ADR-0061 決定 1・2・3・6, [[IADR-0396]] 決定 1・2 (#1184):
// **露出 3 トグルの判定は 1 つの純関数である。**
//
// 🔴 本ファイルが守るのは「判定軸を生産側と消費側で割らない」ことである ——
// `IsIndexable` が 3 軸の選言そのものであること、`AiInputExposure` が別の答えを返さないこと。
public class DocumentExposureTests
{
    private static Dictionary<string, string> PrivateNote(params (string K, string V)[] extra)
    {
        var d = new Dictionary<string, string> { [DocumentScopes.Key] = DocumentScopes.PrivateNote };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    private static Dictionary<string, string> Organization(params (string K, string V)[] extra)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    public static TheoryData<string> Axes() => [.. DocumentExposure.AllKeys];

    // 明示された値は 3 軸とも同じ規則で読む（軸ごとに違う解釈をしない）。
    [Theory]
    [MemberData(nameof(Axes))]
    public void 明示値は軸によらず同じ規則で読まれる(string key)
    {
        DocumentExposure.IsAllowed(PrivateNote((key, DocumentExposure.Included), (key, DocumentExposure.Included)), key)
            .Should().BeTrue();
        DocumentExposure.IsAllowed(PrivateNote((key, DocumentExposure.Excluded)), key)
            .Should().BeFalse();
    }

    // 🔴 個人資料は fail-closed（欠落・未知値は OFF）。
    [Theory]
    [MemberData(nameof(Axes))]
    public void 個人資料の欠落と未知値はOFF扱い(string key)
    {
        DocumentExposure.IsAllowed(PrivateNote(), key).Should().BeFalse();
        DocumentExposure.IsAllowed(PrivateNote((key, "")), key).Should().BeFalse();
        DocumentExposure.IsAllowed(PrivateNote((key, "yes")), key).Should().BeFalse();
    }

    // 🔴 陽性対照: **組織文書は欠落・未知値でも従来どおり許可**である。
    // これが無いと「常に false を返す」実装でも上のテストが緑になる。
    [Theory]
    [MemberData(nameof(Axes))]
    public void 組織文書は欠落でも未知値でも許可される(string key)
    {
        DocumentExposure.IsAllowed(Organization(), key).Should().BeTrue();
        DocumentExposure.IsAllowed(Organization((key, "yes")), key).Should().BeTrue();
        DocumentExposure.IsAllowed(Organization((key, DocumentExposure.Excluded)), key)
            .Should().BeFalse("明示された opt-out は組織文書でも尊重する");
    }

    // ADR-0061 決定 1・2: **1 つでも ON なら索引へ載せる／3 つとも OFF なら載せない。**
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    public void 索引可否は3軸の選言である(bool search, bool graph, bool ai, bool expected)
    {
        var attributes = PrivateNote();
        foreach (var (k, v) in DocumentExposure.Project(search, graph, ai)) attributes[k] = v;

        DocumentExposure.IsIndexable(attributes).Should().Be(expected);
    }

    // 露出キーを持たない組織文書は常に索引可能（既存の取り込み経路の挙動を変えていない）。
    [Fact]
    public void 露出キーを持たない組織文書は常に索引可能である()
    {
        DocumentExposure.IsIndexable(Organization()).Should().BeTrue();
        DocumentExposure.IsIndexable(
            Organization((ConfidentialityLevels.AttributeKey, ConfidentialityLevels.Restricted)))
            .Should().BeTrue();
    }

    // ── FR-19, FR-13, FR-16, ADR-0061 決定 1・2, [[IADR-0529]] (#1879): 露出キーを明示した組織文書 ──────────
    //
    // planning#784 の裁定: AST の承認待ちの報告書（ドラフト）は、3 つとも `excluded` の組織文書として保存する。
    // 索引・検索・RAG・グラフ・MCP の一覧・Wiki のどれにも載せない。

    private static Dictionary<string, string> OrganizationWith(bool search, bool graph, bool ai)
    {
        var d = Organization((ConfidentialityLevels.AttributeKey, ConfidentialityLevels.Internal),
            (DocumentScopes.Key, DocumentScopes.Organization));
        foreach (var (k, v) in DocumentExposure.Project(search, graph, ai)) d[k] = v;
        return d;
    }

    // 🔴 3 つとも `excluded` の組織文書は索引できない。陽性対照: 1 つでも `included` なら索引できる。
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    public void 露出キーを明示した組織文書の索引可否は3軸の選言である(bool search, bool graph, bool ai, bool expected)
    {
        DocumentExposure.IsIndexable(OrganizationWith(search, graph, ai)).Should().Be(expected);
    }

    // Wiki 同期: 3 つとも除外の組織文書は載せない／露出キーの無い組織文書と 1 つでも含める組織文書は載せる／
    // 個人資料は露出によらず載せない（ADR-0046 D-01）。
    [Fact]
    public void Wikiへ載せてよいかは組織文書の索引可否に従い個人資料は常に偽である()
    {
        DocumentExposure.IsWikiPublishable(OrganizationWith(false, false, false)).Should()
            .BeFalse("3 つとも除外の組織文書は Wiki.js とその検索に出さない（planning#784）");
        DocumentExposure.IsWikiPublishable(Organization()).Should()
            .BeTrue("陽性対照: 露出キーの無い既存の組織文書は従来どおり載る");
        DocumentExposure.IsWikiPublishable(OrganizationWith(true, true, true)).Should()
            .BeTrue("陽性対照: 含めるへ戻した組織文書は載る");
        DocumentExposure.IsWikiPublishable(PrivateNote(
            (DocumentExposure.SearchKey, DocumentExposure.Included),
            (DocumentExposure.GraphKey, DocumentExposure.Included),
            (DocumentExposure.AiKey, DocumentExposure.Included))).Should()
            .BeFalse("個人資料は露出によらず Wiki.js へ載せない（ADR-0046 D-01）");
    }

    // MCP の一覧: 3 つとも除外の組織文書は載せない。個人資料は本述語では落とさない（ADR-0034 決定 9 の側）。
    [Fact]
    public void MCPの一覧に載せてよいかは組織文書の索引可否に従い個人資料は落とさない()
    {
        DocumentExposure.IsMcpListable(OrganizationWith(false, false, false)).Should().BeFalse();
        DocumentExposure.IsMcpListable(Organization()).Should().BeTrue("陽性対照: 露出キーの無い組織文書");
        DocumentExposure.IsMcpListable(OrganizationWith(false, false, true)).Should().BeTrue("陽性対照: 1 つでも含める");
        DocumentExposure.IsMcpListable(PrivateNote()).Should()
            .BeTrue("個人資料の MCP での扱いは露出トグルを見ない（裁定の無い挙動を変えない）");
    }

    // `Project` は 3 軸すべてを書く（1 つでも欠けると fail-closed で静かに見えなくなる）。
    [Fact]
    public void 投影は3軸すべてを明示する()
    {
        var projected = DocumentExposure.Project(true, false, true);

        projected.Keys.Should().BeEquivalentTo(DocumentExposure.AllKeys);
        projected[DocumentExposure.SearchKey].Should().Be(DocumentExposure.Included);
        projected[DocumentExposure.GraphKey].Should().Be(DocumentExposure.Excluded);
        projected[DocumentExposure.AiKey].Should().Be(DocumentExposure.Included);
    }

    // 🔴 判定軸を割らない: `AiInputExposure` は別名であって別実装ではない。
    // 片方だけ改名・改修されると、供給側と消費側が違う答えを見る。
    [Theory]
    [InlineData(DocumentExposure.Included)]
    [InlineData(DocumentExposure.Excluded)]
    [InlineData("unknown")]
    public void AiInputExposureはDocumentExposureと同じ答えを返す(string value)
    {
        var note = PrivateNote((DocumentExposure.AiKey, value));
        var org = Organization((DocumentExposure.AiKey, value));

        AiInputExposure.IsAllowed(note).Should().Be(DocumentExposure.IsAiAllowed(note));
        AiInputExposure.IsAllowed(org).Should().Be(DocumentExposure.IsAiAllowed(org));
    }

    // 🔴 値の一致を機械で固定する。**`AiInputExposure` の定数は契約 baseline の都合で
    // リテラルのまま複製してある**（参照へ置き換えると値が変わらないのに breaking 扱いになる）。
    // 複製を許すなら、ずれを検出する口を必ず対で置く（[[IADR-0270]] 決定 6 と同じ形）。
    [Fact]
    public void AiInputExposureの定数はDocumentExposureと同じ値である()
    {
        AiInputExposure.AttributeKey.Should().Be(DocumentExposure.AiKey);
        AiInputExposure.Included.Should().Be(DocumentExposure.Included);
        AiInputExposure.Excluded.Should().Be(DocumentExposure.Excluded);
        AiInputExposure.All.Should().BeEquivalentTo(DocumentExposure.AllValues);
    }
}
