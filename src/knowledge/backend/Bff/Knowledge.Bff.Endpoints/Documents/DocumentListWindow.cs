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
// 🔴 **窓の外の文書は保持しない**（数えるだけ）。保持するのは窓の中の `limit` 件だけである。
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
        private int _total;
        private int _unfilteredTotal;

        // 後段のページを並びどおりに渡す。絞った後の順番が窓に入る文書だけを残す。
        // 🔴 順序は **ABAC → 全体の数・状態別の数 → 利用者の絞り込み → 窓** である。ABAC で落ちた文書はどの数にも入らない。
        public void AddRange(IEnumerable<DocumentDto> docs)
        {
            foreach (var doc in docs)
            {
                if (!visible(doc))
                    continue;
                _unfilteredTotal++;
                _statusCounts[doc.Status] = _statusCounts.GetValueOrDefault(doc.Status) + 1;
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
        };
    }
}

// FR-06, SC-05, [[IADR-0545]] (#1923): 文書管理の一覧の**利用者の絞り込み**（表題の部分一致・状態）。
//
// 🔴 **BFF の窓の述語として ABAC の後に当てる**（後段の rpc へは渡さない）。全体の件数（`UnfilteredTotal`）を出すには、どのみち
//   1 要求で台帳を 1 周する（IADR-0537 決定 2）。同じ 1 周で全体・絞り込み後・状態別を数えられる。rpc へ渡すと全体の数のために
//   もう 1 周が要る（理由の全文は IADR-0545）。
// 表題は**大文字小文字を区別しない序数比較の部分一致**（検索の索引を使わない。索引が空・縮退していても使える）。
public sealed record DocumentListFilter(string? TitleContains, string? Status)
{
    public static readonly DocumentListFilter None = new(null, null);

    /// <summary>表題の検索語の上限（文字数）。超えた要求は 400（黙って切り詰めない）。</summary>
    public const int MaxTitleQueryLength = 200;

    /// <summary>状態の絞り込みが受ける値（公開ライフサイクル。`DocumentDto.Status` の値域）。</summary>
    public static readonly IReadOnlyList<string> Statuses = ["draft", "normalized", "published", "archived"];

    public bool IsEmpty => TitleContains is null && Status is null;

    public bool Matches(DocumentDto doc)
        => (TitleContains is null || doc.Title.Contains(TitleContains, StringComparison.OrdinalIgnoreCase))
            && (Status is null || string.Equals(doc.Status, Status, StringComparison.Ordinal));

    /// <summary>
    /// クエリを検証して絞り込みを作る。表題は前後の空白を除き、空なら無指定。誤りは <paramref name="errors"/> へ積む。
    /// </summary>
    public static DocumentListFilter Parse(string? q, string? status, IDictionary<string, string[]> errors)
    {
        var title = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (title is { Length: > MaxTitleQueryLength })
            errors["q"] = [$"q は {MaxTitleQueryLength} 文字以内で指定してください。"];

        var wanted = string.IsNullOrEmpty(status) ? null : status;
        if (wanted is not null && !Statuses.Contains(wanted, StringComparer.Ordinal))
            errors["status"] = [$"status は {string.Join(" / ", Statuses)} のいずれかで指定してください。"];

        return new DocumentListFilter(title, wanted);
    }
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
