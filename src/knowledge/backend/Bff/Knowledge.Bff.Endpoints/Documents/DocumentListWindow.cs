using Knowledge.Contracts.Dtos;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Knowledge.Bff.Endpoints.Documents;

// FR-06, SC-05, NFR-16, [[IADR-0009]], [[IADR-0537]] (#1923・#1897): 文書管理の一覧（`GET /bff/documents`）の**窓**。
//
// 送り方は**オフセット**である（`offset` / `limit`）。総件数を権限で絞った後に数えるには、どのみち 1 要求で台帳を 1 周する。
// 1 周する以上、続きの印（キーセット）を持っても読む量は減らず、ページ番号へ跳べる分だけオフセットが画面に合う（理由の全文は IADR-0537）。
//
// 🔴 **窓も総件数も「絞った後の並び」で数える。** `visible` が偽の文書は窓の位置を 1 つも進めず、総件数にも入らない ——
//   権限外の文書の有無で、ページの中身・境界・総件数が 1 ビットも変わらない（IADR-0009 の存在秘匿）。
// 🔴 **窓の外の文書の中身は保持しない**（数えるだけ）。中身を保持するのは窓の中の `limit` 件だけである。
//   ［2026-10-15 / [[IADR-0545]]］ただし**見た文書の ID の集合**は 1 周の間だけ持つ（重複を除くため。下の Collector の注記。
//   約 4.5 万件で数 MB）。
public readonly record struct DocumentListWindow(int Offset, int Limit)
{
    /// <summary>画面のページの大きさの既定（画面の既定の選択肢 50 と同じ）。</summary>
    public const int DefaultLimit = 50;

    /// <summary>画面のページの大きさの上限（画面の選択肢の最大 100 と同じ）。超えた要求はここへ丸める。</summary>
    public const int MaxLimit = 100;

    /// <summary>未指定は既定、範囲外は 1〜上限へ丸める（`GET /documents/page` の `ClampLimit` と同じ作法）。</summary>
    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    /// <summary>
    /// 空のページ（許可ポリシーが無い＝見える文書が無い）。総件数 0。
    /// 🔴 後段から引けなかったときには使わない（502。本当の 0 件と区別する。#1897）。
    /// </summary>
    public DocumentListPageDto Empty() => new() { Offset = Offset, Limit = Limit };

    public Collector Collect(Func<DocumentDto, bool> visible, Func<DocumentDto, DocumentDto> forViewer)
        => new(this, visible, forViewer, DocumentListFilter.None);

    // FR-06, SC-05, [[IADR-0545]] (#1923): 利用者の絞り込み（表題・状態）を ABAC の**後**に当てる。
    public Collector Collect(Func<DocumentDto, bool> visible, Func<DocumentDto, DocumentDto> forViewer, DocumentListFilter filter)
        => new(this, visible, forViewer, filter);

    public sealed class Collector(DocumentListWindow window, Func<DocumentDto, bool> visible,
        Func<DocumentDto, DocumentDto> forViewer, DocumentListFilter filter)
    {
        private readonly List<DocumentDto> _items = new(window.Limit);
        private readonly Dictionary<string, int> _statusCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _sourceCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _projectCounts = new(StringComparer.Ordinal);
        private readonly HashSet<Guid> _seen = [];
        private int _total;
        private int _unfilteredTotal;

        // 後段のページを並びどおりに渡す。絞った後の順番が窓に入る文書だけを残す。
        // 🔴 順序は **ABAC → 全体の数・状態別の数 → 利用者の絞り込み → 窓** である。ABAC で落ちた文書はどの数にも入らない。
        public void AddRange(IEnumerable<DocumentDto> docs)
        {
            foreach (var doc in docs)
            {
                // FR-06, SC-05, [[IADR-0545]] (#1923): 🔴 **1 周の中で同じ文書を 2 度数えない。** 後段のキーセットは要求ごとに位置から読むので、
                //   走査の途中で更新された文書は、並びのキーが動いた先でもう一度返り得る（昇順の更新日時なら末尾。監査の再現）。
                //   数えると件数が +1 になり、窓に同じ行が 2 度並ぶ（画面の key も重複する）。最初に見た位置だけを採る。
                if (!_seen.Add(doc.Id))
                    continue;
                if (!visible(doc))
                    continue;
                _unfilteredTotal++;
                _statusCounts[doc.Status] = _statusCounts.GetValueOrDefault(doc.Status) + 1;
                // ［2026-10-17 / [[IADR-0561]]］データソース別・プロジェクト別も同じ集合（ABAC 後・絞り込み前）で数える。
                var source = DocumentListSources.KeyOf(doc);
                _sourceCounts[source] = _sourceCounts.GetValueOrDefault(source) + 1;
                var project = DocumentListSources.ProjectOf(doc);
                _projectCounts[project] = _projectCounts.GetValueOrDefault(project) + 1;
                if (!filter.Matches(doc))
                    continue;
                var position = _total++;
                if (position >= window.Offset && position - window.Offset < window.Limit)
                    _items.Add(forViewer(doc));
            }
        }

        public DocumentListPageDto ToPage() => new()
        {
            Items = _items,
            Total = _total,
            Offset = window.Offset,
            Limit = window.Limit,
            UnfilteredTotal = _unfilteredTotal,
            StatusCounts = _statusCounts,
            SourceCounts = _sourceCounts,
            ProjectCounts = _projectCounts,
        };
    }
}

// FR-06, SC-05, [[IADR-0545]] (#1923): 文書管理の一覧の**利用者の絞り込み**（表題の部分一致・状態）。
// ［2026-10-17 / [[IADR-0561]]］データソース・機密区分・タグ・プロジェクト・保存日（作成日時）の範囲・タブ（機械の収集か否か）を足した。
//
// 🔴 **BFF の窓の述語として ABAC の後に当てる**（後段の rpc へは渡さない）。全体の件数（`UnfilteredTotal`）を出すには、どのみち
//   1 要求で台帳を 1 周する（IADR-0537 決定 2）。同じ 1 周で全体・絞り込み後・状態別を数えられる。rpc へ渡すと全体の数のために
//   もう 1 周が要る（理由の全文は IADR-0545）。
// 表題は**大文字小文字を区別しない序数比較の部分一致**（検索の索引を使わない。索引が空・縮退していても使える）。
// ほかの値は**序数の完全一致**（画面は件数の鍵・辞書・固定の語彙から選ぶので、表記揺れを吸収しない）。
public sealed record DocumentListFilter(string? TitleContains, string? Status)
{
    public static readonly DocumentListFilter None = new(null, null);

    /// <summary>表題の検索語の上限（文字数）。超えた要求は 400（黙って切り詰めない）。</summary>
    public const int MaxTitleQueryLength = 200;

    /// <summary>
    /// データソースの鍵の上限。超えた要求は 400。🔴 件数の側の鍵も同じ上限で切る（`DocumentListSources.KeyOf`）——
    /// 件数に出した鍵を選ぶと 400 になる経路を作らない。
    /// </summary>
    public const int MaxSourceLength = DocumentListSources.MaxKeyLength;

    /// <summary>タグ・プロジェクトの値の上限。超えた要求は 400。</summary>
    public const int MaxValueLength = 100;

    /// <summary>状態の絞り込みが受ける値（公開ライフサイクル。`DocumentDto.Status` の値域）。</summary>
    public static readonly IReadOnlyList<string> Statuses = ["draft", "normalized", "published", "archived"];

    /// <summary>機密区分の絞り込みが受ける値（`DocumentAttributes.AllowedConfidentiality` と同じ 4 値）。</summary>
    public static readonly IReadOnlyList<string> Confidentialities = ["public", "internal", "confidential", "restricted"];

    // FR-06, SC-05, [[IADR-0561]] (#1923): 追加の絞り込み。どれも null は無指定（従前の応答と同じ）。
    public string? Origin { get; init; }
    public string? Source { get; init; }
    public string? Confidentiality { get; init; }
    public string? Tag { get; init; }
    public string? Project { get; init; }
    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedBefore { get; init; }

    public bool IsEmpty => this == None;

    public bool Matches(DocumentDto doc)
        => (TitleContains is null || doc.Title.Contains(TitleContains, StringComparison.OrdinalIgnoreCase))
            && (Status is null || string.Equals(doc.Status, Status, StringComparison.Ordinal))
            && (Origin is null || string.Equals(DocumentListSources.OriginOf(DocumentListSources.KeyOf(doc)), Origin, StringComparison.Ordinal))
            && (Source is null || string.Equals(DocumentListSources.KeyOf(doc), Source, StringComparison.Ordinal))
            && (Confidentiality is null
                || (doc.Attributes.TryGetValue("confidentiality", out var c) && string.Equals(c, Confidentiality, StringComparison.Ordinal)))
            && (Tag is null || doc.Tags.Contains(Tag, StringComparer.Ordinal))
            && (Project is null || string.Equals(DocumentListSources.ProjectOf(doc), Project, StringComparison.Ordinal))
            && (CreatedFrom is null || doc.CreatedAt >= CreatedFrom)
            && (CreatedBefore is null || doc.CreatedAt < CreatedBefore);

    /// <summary>
    /// クエリを検証して絞り込みを作る。表題は前後の空白を除き、空なら無指定。誤りは <paramref name="errors"/> へ積む。
    /// </summary>
    public static DocumentListFilter Parse(string? q, string? status, IDictionary<string, string[]> errors)
        => Parse(new DocumentListQuery { Q = q, Status = status }, errors);

    /// <summary>
    /// FR-06, SC-05, [[IADR-0561]] (#1923): 全部のクエリを検証して絞り込みを作る。空の値は無指定。誤りは <paramref name="errors"/> へ積む
    /// （1 つでも誤りがあれば呼び出し元が 400 にし、後段を呼ばない）。
    /// </summary>
    public static DocumentListFilter Parse(DocumentListQuery query, IDictionary<string, string[]> errors)
    {
        var title = string.IsNullOrWhiteSpace(query.Q) ? null : query.Q.Trim();
        if (title is { Length: > MaxTitleQueryLength })
            errors["q"] = [$"q は {MaxTitleQueryLength} 文字以内で指定してください。"];

        var status = OneOf("status", query.Status, Statuses, errors);
        var origin = OneOf("origin", query.Origin, DocumentListSources.Origins, errors);
        var confidentiality = OneOf("confidentiality", query.Confidentiality, Confidentialities, errors);
        var source = Bounded("source", query.Source, MaxSourceLength, errors);
        var tag = Bounded("tag", query.Tag, MaxValueLength, errors);
        var project = Bounded("project", query.Project, MaxValueLength, errors);
        var from = Instant("createdFrom", query.CreatedFrom, errors);
        var before = Instant("createdBefore", query.CreatedBefore, errors);
        if (from is not null && before is not null && from >= before)
            errors["createdBefore"] = ["createdBefore は createdFrom より後の日時で指定してください。"];

        return new DocumentListFilter(title, status)
        {
            Origin = origin,
            Source = source,
            Confidentiality = confidentiality,
            Tag = tag,
            Project = project,
            CreatedFrom = from,
            CreatedBefore = before,
        };
    }

    private static string? OneOf(string name, string? value, IReadOnlyCollection<string> allowed, IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (!allowed.Contains(value, StringComparer.Ordinal))
            errors[name] = [$"{name} は {string.Join(" / ", allowed)} のいずれかで指定してください。"];
        return value;
    }

    private static string? Bounded(string name, string? value, int max, IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.Length > max)
            errors[name] = [$"{name} は {max} 文字以内で指定してください。"];
        return value;
    }

    // ISO 8601 の日時（オフセットつき、または UTC の `Z`）。オフセットの無い値は UTC として読む（画面は常に `toISOString()` の `Z` で送る）。
    private static DateTimeOffset? Instant(string name, string? value, IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var instant))
            return instant;
        errors[name] = [$"{name} は ISO 8601 の日時で指定してください。"];
        return null;
    }
}

// FR-06, SC-05, [[IADR-0561]] (#1923): `GET /bff/documents` のクエリ（`[AsParameters]`）。どれも省略可で、省略は従前の応答と同じ（後方互換）。
public sealed record DocumentListQuery
{
    public int? Offset { get; init; }
    public int? Limit { get; init; }
    public string? Sort { get; init; }
    public string? Q { get; init; }
    public string? Status { get; init; }
    public string? Origin { get; init; }
    public string? Source { get; init; }
    public string? Confidentiality { get; init; }
    public string? Tag { get; init; }
    public string? Project { get; init; }
    public string? CreatedFrom { get; init; }
    public string? CreatedBefore { get; init; }
}

// FR-06, SC-05, [[IADR-0561]] (#1923): 文書の**データソースの鍵**とタブ（機械の収集か否か）・プロジェクトの導出。**導出はここ 1 か所**
// （絞り込みの述語と件数の鍵が同じ関数を通る。画面の列の表示は `sc05-documents/types/documentSource.ts` が同じ規則で写す）。
//
// 鍵は次の順で最初に当てはまったもの:
//   1. `ds:<DataSourceName>` —— データソース（コネクタ）からの取り込み。台帳がデータソースの表示名を持つ。
//   2. `col:<属性 source>` —— 機械の収集（API の書き手が出典名を `source` 属性に載せる。AST の情報収集のニュース・開示など）。
//   3. `report` —— 確定報告書の写し（`kind` と `periodKey` を持ち、`source` を持たない。AST の日報・週報・月報）。
//   4. `manual` —— それ以外（画面・API からの作成）。
// タブ（`origin`）は 2 だけが `collected`（機械の収集）、ほかは `curated`（人の文書・報告書・データソースの取り込み）。
public static class DocumentListSources
{
    public const string Collected = "collected";
    public const string Curated = "curated";
    public static readonly IReadOnlyList<string> Origins = [Curated, Collected];

    public const string DataSourcePrefix = "ds:";
    public const string CollectedPrefix = "col:";
    public const string Report = "report";
    public const string Manual = "manual";

    /// <summary>属性 `project` を持たない文書の鍵（件数と絞り込みの両方で同じ値）。</summary>
    public const string NoProject = "(none)";

    /// <summary>
    /// 鍵の上限（文字数＝UTF-16 の符号単位）。🔴 属性 `source` は書き手の申告で長さの上限が無いので、鍵は**この長さで切る**
    /// （サロゲートペアは割らない）。件数の鍵も絞り込みの照合も同じ `KeyOf` を通るので、切った鍵を選べばその文書に当たる。
    /// 受理側の上限（`DocumentListFilter.MaxSourceLength`）はこの値そのもの。
    /// </summary>
    public const int MaxKeyLength = 300;

    public static string KeyOf(DocumentDto doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.DataSourceName))
            return Bounded(DataSourcePrefix + doc.DataSourceName);
        if (doc.Attributes.TryGetValue("source", out var source) && !string.IsNullOrWhiteSpace(source))
            return Bounded(CollectedPrefix + source);
        if (doc.Attributes.ContainsKey("kind") && doc.Attributes.ContainsKey("periodKey"))
            return Report;
        return Manual;
    }

    private static string Bounded(string key)
    {
        if (key.Length <= MaxKeyLength)
            return key;
        var cut = char.IsHighSurrogate(key[MaxKeyLength - 1]) ? MaxKeyLength - 1 : MaxKeyLength;
        return key[..cut];
    }

    public static string OriginOf(string key)
        => key.StartsWith(CollectedPrefix, StringComparison.Ordinal) ? Collected : Curated;

    public static string ProjectOf(DocumentDto doc)
        => doc.Attributes.TryGetValue("project", out var project) && !string.IsNullOrWhiteSpace(project) ? project : NoProject;
}

// FR-06, SC-05, [[IADR-0545]] (#1923): 文書管理の一覧の**並べ替え**（BFF の口の語彙 → 後段の rpc の enum）。
// 後段のキーセットがキーごとの続きの印で辿る（並べ替えは後段で行う。BFF は並べ直さない）。
public static class DocumentListSorts
{
    public const string Default = "updated-desc";

    private static readonly Dictionary<string, Pb.DocumentListSort> Map = new(StringComparer.Ordinal)
    {
        ["updated-desc"] = Pb.DocumentListSort.UpdatedDesc,
        ["updated-asc"] = Pb.DocumentListSort.UpdatedAsc,
        ["created-desc"] = Pb.DocumentListSort.CreatedDesc,
        ["created-asc"] = Pb.DocumentListSort.CreatedAsc,
        ["title-asc"] = Pb.DocumentListSort.TitleAsc,
        ["title-desc"] = Pb.DocumentListSort.TitleDesc,
    };

    public static IReadOnlyCollection<string> Values => Map.Keys;

    /// <summary>未指定は既定（更新日時の降順）。未知の値は誤りを積む（黙って既定へ畳まない）。</summary>
    public static Pb.DocumentListSort Parse(string? sort, IDictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(sort))
            return Map[Default];
        if (Map.TryGetValue(sort, out var value))
            return value;
        errors["sort"] = [$"sort は {string.Join(" / ", Map.Keys)} のいずれかで指定してください。"];
        return Map[Default];
    }
}
