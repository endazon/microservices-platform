using AwesomeAssertions;
using DocumentService.Features.Documents.AstStaleCopies;

namespace DocumentService.Tests.Features.Documents.AstStaleCopies;

// FR-06, FR-05, AST/FR-08, 計画 ADR-0122 決定 1・2, [[IADR-0484]] (#1667): AST の古い写しを見分ける規則（純粋関数）。
//
// 🔴 **陰性は陽性の対照と対で置く。** 各陰性は、陽性の文書から**属性を 1 つだけ**変えたものである
// （「何も拾わない」規則でも陰性だけは緑になる）。
public class AstStaleCopyRulesTests
{
    private const string Created = "created";
    private const string CreatedWithBody = "created-with-body";

    private static Dictionary<string, string> Report(string? owner = null, string? project = null,
        string kind = "Daily", string periodKey = "2026-08-01")
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["kind"] = kind,
            ["periodKey"] = periodKey,
            ["assumptionsVersion"] = "3",
        };
        if (owner is not null) attributes["owner"] = owner;
        if (project is not null) attributes["project"] = project;
        return attributes;
    }

    private static Dictionary<string, string> Article(string? owner = null, string? project = null,
        string kind = "News", string publishedAt = "2026-08-01T09:00:00.0000000+00:00")
    {
        var attributes = new Dictionary<string, string>
        {
            ["confidentiality"] = "internal",
            ["kind"] = kind,
            ["source"] = "tdnet",
            ["publishedAt"] = publishedAt,
        };
        if (owner is not null) attributes["owner"] = owner;
        if (project is not null) attributes["project"] = project;
        return attributes;
    }

    private const string ReportTitle = "確定報告書 Daily 2026-08-01";

    // ── 陽性: owner の欠落・空白・system はどれも対象（ADR-0122 決定 1） ─────────────

    [Theory]
    [InlineData(null, AstCopyOwnerState.Missing)]
    [InlineData("  ", AstCopyOwnerState.Missing)]
    [InlineData("system", AstCopyOwnerState.System)]
    public void AST_の報告書の写しでownerが無いかsystemなら対象(string? owner, AstCopyOwnerState expected)
    {
        var verdict = AstStaleCopyRules.Classify(ReportTitle, Report(owner), Created);

        verdict.IsTarget.Should().BeTrue();
        verdict.Category.Should().Be(AstCopyCategory.Report);
        verdict.OwnerState.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, CreatedWithBody)]
    [InlineData("system", Created)]
    public void AST_の収集記事の写しも対象(string? owner, string firstNote)
    {
        // ADR-0122 決定 2: 記事は入れ直せないが、消す対象に含める。
        var verdict = AstStaleCopyRules.Classify("某社が決算を発表", Article(owner), firstNote);

        verdict.IsTarget.Should().BeTrue();
        verdict.Category.Should().Be(AstCopyCategory.Article);
    }

    [Fact]
    public void project_を持つ報告書は表題が違っても対象_projectが無ければ表題の一致が要る()
    {
        // AST の入れ直し（AST/IADR-0436 決定 2）と同じ写しの判定。
        AstStaleCopyRules.Classify("表題を変えた報告書", Report(project: "ai-stock-trading"), Created)
            .IsTarget.Should().BeTrue();

        AstStaleCopyRules.Classify("表題を変えた報告書", Report(), Created)
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.NotAstShape);
    }

    // ── 陰性: 陽性から 1 つだけ変える ─────────────────────────────────────────

    [Fact]
    public void 取り込みの経路のownerがsystemの文書は拾わない()
    {
        // DataSourceService の予約値（`DataSource.UnresolvedOwner`）。最初の版が `normalized`。
        AstStaleCopyRules.Classify(ReportTitle, Report("system"), "normalized")
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.NotCreatedViaPost);
    }

    [Fact]
    public void 版の無い文書は作成の経路が分からないので拾わない()
    {
        AstStaleCopyRules.Classify(ReportTitle, Report(), firstChangeNote: null)
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.NotCreatedViaPost);
    }

    [Theory]
    [InlineData("other-project")]
    [InlineData("AI-STOCK-TRADING")]
    public void 別のprojectの文書は拾わない(string project)
    {
        AstStaleCopyRules.Classify(ReportTitle, Report(project: project), Created)
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.OtherProject);
        AstStaleCopyRules.Classify("記事", Article(project: project), Created)
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.OtherProject);
    }

    [Fact]
    public void 個人資料は拾わない_取り込みの経路より先に数える()
    {
        var note = Report();
        note["doc_scope"] = "private-note";

        AstStaleCopyRules.Classify(ReportTitle, note, Created)
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.PrivateNote);
        AstStaleCopyRules.Classify(ReportTitle, note, "normalized")
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.PrivateNote);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("Quarterly")]
    public void 報告書の種別が違えば拾わない(string kind)
    {
        AstStaleCopyRules.Classify($"確定報告書 {kind} 2026-08-01", Report(kind: kind), Created)
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.NotAstShape);
    }

    [Theory]
    [InlineData("kind", "Blog")]
    [InlineData("source", " ")]
    [InlineData("publishedAt", "昨日")]
    public void 記事の属性が欠ければ拾わない(string key, string value)
    {
        var attributes = Article();
        attributes[key] = value;

        AstStaleCopyRules.Classify("記事", attributes, Created)
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.NotAstShape);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("system")]
    public void 人が作った形の違う文書は所有者が無くてもsystemでも拾わない(string? owner)
    {
        var attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" };
        if (owner is not null) attributes["owner"] = owner;

        AstStaleCopyRules.Classify("社内規程", attributes, Created)
            .ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.NotAstShape);
    }

    [Theory]
    [InlineData("service-account-ai-stock-trading-kb-writer", AstStaleCopyRules.Reasons.OwnedByCurrentAccount)]
    [InlineData("alice", AstStaleCopyRules.Reasons.OtherOwner)]
    [InlineData("System", AstStaleCopyRules.Reasons.OtherOwner)]
    public void 現在のサービスアカウントや他の主体が所有する写しは拾わない(string owner, string reason)
    {
        var verdict = AstStaleCopyRules.Classify(ReportTitle, Report(owner), Created);

        verdict.IsTarget.Should().BeFalse();
        verdict.ExcludedReason.Should().Be(reason);
        verdict.Category.Should().Be(AstCopyCategory.Report, "形まで通った写しは種別を持つ（重複の数えに使う）");
    }

    // ── FR-19, #1891: 承認待ちの報告書の写し（`reportState=draft`。AST#1301・[[IADR-0529]]）は確定報告書の写しに数えない ──
    //
    // ドラフトは確定版と同じ kind・periodKey・project を持つ。数えると確定版との組が重複に出て、runbook の
    // 「新しい方を消す」で確定版を消す。陽性（数えない）と陰性（draft 以外は従来どおり）を対で置く。

    private static Dictionary<string, string> Draft(string? owner)
    {
        var attributes = Report(owner, project: "ai-stock-trading");
        attributes["reportState"] = "draft";
        foreach (var key in new[] { "search_exposure", "graph_exposure", "ai_input" }) attributes[key] = "excluded";
        return attributes;
    }

    [Theory]
    [InlineData("service-account-ai-stock-trading-kb-writer")]
    [InlineData("system")]
    [InlineData(null)]
    public void 承認待ちの報告書の写しは所有者によらず報告書の写しに数えない(string? owner)
    {
        var verdict = AstStaleCopyRules.Classify("報告書ドラフト Daily 2026-08-01", Draft(owner), Created);

        verdict.IsTarget.Should().BeFalse();
        verdict.ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.NotAstShape);
        verdict.Category.Should().BeNull("ドラフトは現在のサービスアカウントの報告書の重複の数えに入らない");
    }

    [Fact]
    public void 承認待ちの写しは表題を確定版と同じにしても報告書の写しに数えない()
    {
        // 表題は見ない。属性だけで外す。
        AstStaleCopyRules.Classify(ReportTitle, Draft("service-account-ai-stock-trading-kb-writer"), Created)
            .Category.Should().BeNull();
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("Draft")]
    [InlineData(" ")]
    public void reportStateがdraftでない報告書は従来どおり報告書の写しに数える(string state)
    {
        var attributes = Report("service-account-ai-stock-trading-kb-writer", project: "ai-stock-trading");
        attributes["reportState"] = state;

        var verdict = AstStaleCopyRules.Classify(ReportTitle, attributes, Created);

        verdict.ExcludedReason.Should().Be(AstStaleCopyRules.Reasons.OwnedByCurrentAccount);
        verdict.Category.Should().Be(AstCopyCategory.Report);
    }

    // ── kind の値域を AST の実値で丸ごと固定する（過不足の両方を検出する。AST 側で KB のタグの語彙を固定している試験と同じ型） ──
    //
    // 出典（AST の隣接クローン 40d992e で読んだ。基盤は AST の型を参照できないので値を写して固定する）:
    //   - 報告書: `backend/Services/ReportService/Domain/TradingReport.cs` の `enum ReportKind { Daily, Weekly, Monthly }`
    //     （`ReportKnowledgeMapper` が `report.Kind.ToString()` を属性 `kind` に載せる）
    //   - 記事: `backend/Services/InformationCollectionService/Domain/CollectedInformation.cs` の
    //     `enum InformationKind { Quote, News, Disclosure, MacroIndicator, SupplyDemand, SourceStatus }`
    //     （`KnowledgeBaseWriterSink` が `item.Kind.ToString()` を属性 `kind` に載せる）
    // 🔴 AST が列挙値を足したら、ここと `AstStaleCopyRules` を同時に直す（足りないと古い写しを見落とし、余ると AST 以外を拾い得る）。
    private static readonly string[] AstReportKinds = ["Daily", "Weekly", "Monthly"];
    private static readonly string[] AstInformationKinds =
        ["Quote", "News", "Disclosure", "MacroIndicator", "SupplyDemand", "SourceStatus"];

    [Fact]
    public void 報告書のkindの値域はAST_のReportKindの全値と過不足なく一致する()
    {
        AstStaleCopyRules.ReportKinds.Should().BeEquivalentTo(AstReportKinds);
    }

    [Fact]
    public void 記事のkindの値域はAST_のInformationKindの全値と過不足なく一致する()
    {
        AstStaleCopyRules.ArticleKinds.Should().BeEquivalentTo(AstInformationKinds);
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    public void AST_の報告書のkindはどれも報告書として拾う(string kind)
    {
        AstStaleCopyRules.Classify($"確定報告書 {kind} 2026-08-01", Report(kind: kind), Created)
            .Category.Should().Be(AstCopyCategory.Report);
    }

    [Theory]
    [InlineData("Quote")]
    [InlineData("News")]
    [InlineData("Disclosure")]
    [InlineData("MacroIndicator")]
    [InlineData("SupplyDemand")]
    [InlineData("SourceStatus")]
    public void AST_の記事のkindはどれも記事として拾う(string kind)
    {
        AstStaleCopyRules.Classify("記事", Article(kind: kind), Created)
            .Category.Should().Be(AstCopyCategory.Article);
    }

    [Fact]
    public void 理由は判定の順に6つ()
    {
        AstStaleCopyRules.Reasons.All.Should().Equal(
            "private-note", "not-created-via-post", "other-project", "not-ast-shape",
            "owned-by-current-account", "other-owner");
    }
}
