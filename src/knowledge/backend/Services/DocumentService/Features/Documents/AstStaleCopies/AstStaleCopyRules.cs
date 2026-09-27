using System.Globalization;
using DocumentService.Domain;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace DocumentService.Features.Documents.AstStaleCopies;

// FR-06, FR-05, AST/FR-08, SC-05, 計画 ADR-0122 決定 1・2, ADR-0121 決定 3, [[IADR-0484]] (#1667):
// **AST の古い写し（内容の ABAC の前に基盤の管理者が消す文書）を見分ける規則。** 純粋関数であり、台帳にも主体にも触れない。
//
// 対象は「AST の KB の書き手が作った写しのうち、AST の現在のサービスアカウントが所有していないもの」
// （`owner=system` と `owner` の欠落の両方）である。
//
// 🔴 **`owner` では見分けない。`owner` は最後にだけ見る**（ADR-0122 決定 1）。`owner=system` は DataSourceService の
//   予約値（`DataSource.UnresolvedOwner`。取り込みの経路）でもあり、`owner=system` だけで拾うと AST 以外の文書を消す。
//   AST の写しであることは、作成の経路（最初の版の `ChangeNote`）・`project`・報告書／記事の属性の形で決める。
//
// 🔴 **順序が応答の契約である**（1 件は最初に外れた理由にだけ数える）。個人資料 → 作成の経路 → project → 形 → owner。
internal static class AstStaleCopyRules
{
    // AST の文書の project 属性の値（AST の KB の書き手が #665 以降必ず付ける。別の値は AST 側が例外で拒む）。
    public const string AstProject = "ai-stock-trading";

    // DataSourceService の予約値と同じ綴り。AST も #520 から #1057 の間はこの値を送り、MSP が保存した。
    public const string ReservedOwner = "system";

    // AST の現在のサービスアカウント（計画 ADR-0119 決定 2 の作成時の `owner`。`DocumentManageScope.MachineSubject` と同じ規約）。
    public const string CurrentAccount = MachinePrincipal.ServiceAccountUsernamePrefix + "ai-stock-trading-kb-writer";

    // `POST /documents` の経路の最初の版（`Document.Create` / `Document.CreateWithBody` の `Snapshot`）。
    // 取り込み（`CreateNormalized`）は `normalized` で、ここに入らない。
    private static readonly HashSet<string> CreatedViaPostNotes = new(StringComparer.Ordinal)
    {
        "created",
        "created-with-body",
    };

    // AST の確定報告書（`ReportKnowledgeMapper`）。`kind` は AST の `ReportKind` の名前。
    // 🔴 値域は `AstStaleCopyRulesTests` が AST の実値で丸ごと固定する（過不足の両方が赤になる）。
    internal static readonly IReadOnlySet<string> ReportKinds =
        new HashSet<string>(StringComparer.Ordinal) { "Daily", "Weekly", "Monthly" };

    // AST の収集記事（`KnowledgeBaseWriterSink`）。`kind` は AST の `InformationKind` の名前（同上の試験が固定する）。
    internal static readonly IReadOnlySet<string> ArticleKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "Quote", "News", "Disclosure", "MacroIndicator", "SupplyDemand", "SourceStatus",
    };

    public const string KindKey = "kind";
    public const string PeriodKeyKey = "periodKey";
    public const string SourceKey = "source";
    public const string PublishedAtKey = "publishedAt";

    // 除いた理由（応答の鍵）。**並びは判定の順**。
    public static class Reasons
    {
        public const string PrivateNote = "private-note";
        public const string NotCreatedViaPost = "not-created-via-post";
        public const string OtherProject = "other-project";
        public const string NotAstShape = "not-ast-shape";
        public const string OwnedByCurrentAccount = "owned-by-current-account";
        public const string OtherOwner = "other-owner";

        public static readonly IReadOnlyList<string> All =
            [PrivateNote, NotCreatedViaPost, OtherProject, NotAstShape, OwnedByCurrentAccount, OtherOwner];
    }

    public static AstCopyVerdict Classify(
        string title, IReadOnlyDictionary<string, string> attributes, string? firstChangeNote)
    {
        // 1. 個人資料は対象に含めない（ADR-0122 決定 1）。集合帰属で判定する（キー欠落は組織文書）。
        if (DocumentScopes.IsPrivateNote(attributes))
            return AstCopyVerdict.Excluded(Reasons.PrivateNote);

        // 2. 作成の経路。取り込みの経路の `owner=system` はここで落ちる。版が無い文書も「分からない」なので落とす（消す側へ倒さない）。
        if (firstChangeNote is null || !CreatedViaPostNotes.Contains(firstChangeNote))
            return AstCopyVerdict.Excluded(Reasons.NotCreatedViaPost);

        // 3. project は無い（#665 より前の AST の保存）か ai-stock-trading。
        var project = ValueOrNull(attributes, RestrictedProject.DocumentKey);
        if (project is not null && !string.Equals(project, AstProject, StringComparison.Ordinal))
            return AstCopyVerdict.Excluded(Reasons.OtherProject);

        // 4. AST の 2 つの書き手の形。
        var category = IsReport(title, attributes, project is not null) ? AstCopyCategory.Report
            : IsArticle(attributes) ? AstCopyCategory.Article
            : (AstCopyCategory?)null;
        if (category is null)
            return AstCopyVerdict.Excluded(Reasons.NotAstShape);

        // 5. owner: 無い・空白・`system` だけが対象（ADR-0122 決定 1）。遡及して付けることはしない。
        var owner = ValueOrNull(attributes, DocumentBodyIntake.OwnerKey);
        if (owner is null) return AstCopyVerdict.Target(category.Value, AstCopyOwnerState.Missing);
        if (string.Equals(owner, ReservedOwner, StringComparison.Ordinal))
            return AstCopyVerdict.Target(category.Value, AstCopyOwnerState.System);

        return AstCopyVerdict.Excluded(string.Equals(owner, CurrentAccount, StringComparison.Ordinal)
            ? Reasons.OwnedByCurrentAccount
            : Reasons.OtherOwner, category);
    }

    // AST の入れ直し（AST/IADR-0436 決定 2）と同じ写しの判定: `kind`・`periodKey` があり、
    // project を持つか、project が無く表題が確定時の写像の表題と完全に一致する。
    internal static bool IsReport(string title, IReadOnlyDictionary<string, string> attributes, bool hasAstProject)
    {
        var kind = ValueOrNull(attributes, KindKey);
        var periodKey = ValueOrNull(attributes, PeriodKeyKey);
        if (kind is null || periodKey is null || !ReportKinds.Contains(kind)) return false;

        return hasAstProject || string.Equals(title, ReportTitle(kind, periodKey), StringComparison.Ordinal);
    }

    internal static bool IsArticle(IReadOnlyDictionary<string, string> attributes)
    {
        var kind = ValueOrNull(attributes, KindKey);
        return kind is not null
            && ArticleKinds.Contains(kind)
            && ValueOrNull(attributes, SourceKey) is not null
            && PublishedAt(attributes) is not null;
    }

    // AST の `ReportKnowledgeMapper.TitleOf`（#169 から不変）。
    public static string ReportTitle(string kind, string periodKey) => $"確定報告書 {kind} {periodKey}";

    public static DateTimeOffset? PublishedAt(IReadOnlyDictionary<string, string> attributes)
        => ValueOrNull(attributes, PublishedAtKey) is { } raw
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    public static string? ValueOrNull(IReadOnlyDictionary<string, string> attributes, string key)
        => attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

public enum AstCopyCategory
{
    Report,
    Article,
}

public enum AstCopyOwnerState
{
    Missing,
    System,
}

// 判定の結果。対象なら `ExcludedReason` は null。除いたときも、形まで通ったものは `Category` を持つ
// （現在のサービスアカウントの報告書の写しの重複を数えるため）。
internal sealed record AstCopyVerdict(
    AstCopyCategory? Category, AstCopyOwnerState? OwnerState, string? ExcludedReason)
{
    public bool IsTarget => ExcludedReason is null;

    public static AstCopyVerdict Target(AstCopyCategory category, AstCopyOwnerState owner)
        => new(category, owner, null);

    public static AstCopyVerdict Excluded(string reason, AstCopyCategory? category = null)
        => new(category, null, reason);
}
