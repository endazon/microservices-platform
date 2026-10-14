using Knowledge.Contracts.Dtos;

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
        => new(this, visible, forViewer);

    public sealed class Collector(DocumentListWindow window, Func<DocumentDto, bool> visible,
        Func<DocumentDto, DocumentDto> forViewer)
    {
        private readonly List<DocumentDto> _items = new(window.Limit);
        private int _total;

        // 後段のページを並びどおりに渡す。絞った後の順番が窓に入る文書だけを残す。
        public void AddRange(IEnumerable<DocumentDto> docs)
        {
            foreach (var doc in docs)
            {
                if (!visible(doc))
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
        };
    }
}
