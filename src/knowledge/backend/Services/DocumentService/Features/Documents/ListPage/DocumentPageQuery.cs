using System.Buffers.Text;
using System.Globalization;
using System.Text;
using DocumentService.Domain;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.Documents.ListPage;

// FR-06, NFR-08, ADR-0036 D-08, ADR-0034 決定 9, ADR-0054 (#1575):
// `GET /documents/page` の本体 —— **組織文書**を属性の完全一致で絞り、キーセットのカーソルで切り出す。
//
// 🔴 **見える集合を広げない。** DocumentService の読み取りは組織文書の内容の ABAC をまだ持たず（実施点は BFF の
// `BffScopeResolver` ＋ `IsManageable`。IADR-0041 / IADR-0045、`IADR-0012`。#1615 で後段にも入る）、
// 直接の呼び出し元に見えている組織文書は `GET /documents` の全件である（［2026-09-27 / #1614］他人の
// 個人資料は `GET /documents` からも除かれるようになった）。ここが返すのは
// 「その全件 ∩ 組織文書 ∩ 全絞り込みに一致」であり、**構成上その部分集合にしかならない**。
// 絞り込みの項目を足すほど狭くなり、どの値を与えても広がらない。
//
// 🔴 **個人資料は値に依らず返さない。** `attr.doc_scope=private-note` を与えても、所有者本人が
// 呼んでも空である。平時は管理者すら個人資料を見ない（ADR-0036 D-08）、機械の主体は個人資料を
// 対象にしない（ADR-0034 決定 9）の線であり、BFF の管理一覧が個人資料を外す（`IsManageable`）のと同じ。
// 判定は `DocumentScopes.IsPrivateNote` ただ 1 つ（**集合帰属で書く**。キー欠落は組織文書）。
//
// ADR-0065 決定 2: 1 操作だけが使うので操作フォルダに置く（`DocumentReadUseCase` へ入れない）。
internal static class DocumentPageQuery
{
    internal const string AttributePrefix = "attr.";
    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 500;

    // 絞り込みの解析。**同じキーの重複・空のキー・空の値は 400**（黙って片方を採らない ——
    // どちらを採っても呼び出し側の意図と食い違い得る）。
    // クエリのキーは大文字小文字を区別しない集合（ASP.NET Core）なので、`attr.project` と
    // `attr.Project` は同じキーの重複として扱われる。属性との突き合わせは値・キーとも大文字小文字を区別する。
    internal static (Dictionary<string, string>? Filters, IResult? Problem) ParseFilters(IQueryCollection query)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, values) in query)
        {
            if (!rawKey.StartsWith(AttributePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = rawKey[AttributePrefix.Length..];
            if (string.IsNullOrWhiteSpace(key))
                return (null, Problem(rawKey, "属性キーが空です（`attr.<キー>=<値>` の形で指定してください）。"));
            if (values.Count != 1)
                return (null, Problem(rawKey, "同じ属性キーは 1 回だけ指定できます。"));
            var value = values[0];
            if (string.IsNullOrEmpty(value))
                return (null, Problem(rawKey, "属性の値が空です。"));

            filters[key] = value;
        }

        return (filters, null);
    }

    // FeedbackService の一覧と同じ作法で丸める（1〜500。未指定は 100）。
    internal static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    // 対象集合（組織文書 ∩ 全絞り込みに一致）を全順序（`CreatedAt` 昇順・同時刻は `Id` 昇順）で並べ、
    // カーソルの後ろから `limit` 件を切り出す。
    //
    // 🔴 **並びのキーは作成時刻（不変）である。更新時刻にしない。** 更新時刻で並べると、走査の途中で
    // 更新された文書が先頭へ移り、未読のまま飛ばされる（キーセットでもオフセットでも起きる）。
    // 作成時刻は台帳の初期化子でしか決まらないので、**走査の間ずっと在った文書はちょうど 1 回ずつ返る**。
    // 走査の途中で作られた文書は末尾に現れ、削除はカーソルの位置を動かさない。
    //
    // 🔴 **メモリ上で絞る。** 属性は jsonb へ値変換で写しており（`DocumentDbContext`）、LINQ から SQL へ
    // 訳せない。呼び出し側は既存の `GET /documents` と同じく台帳を読んでから渡す（DB の負荷は増えない）。
    internal static (List<Document> Page, string? NextCursor) Slice(
        IEnumerable<Document> ledger, IReadOnlyDictionary<string, string> filters,
        int limit, DocumentPageCursor? after)
    {
        var ordered = ledger
            .Where(d => !DocumentScopes.IsPrivateNote(d.Attributes))
            .Where(d => filters.All(f =>
                d.Attributes.TryGetValue(f.Key, out var v) && string.Equals(v, f.Value, StringComparison.Ordinal)))
            .OrderBy(d => d.CreatedAt.UtcTicks)
            .ThenBy(d => d.Id)
            .Where(d => after is null || after.Value.Precedes(d))
            .Take(limit + 1)
            .ToList();

        if (ordered.Count <= limit)
            return (ordered, null);

        var page = ordered.Take(limit).ToList();
        return (page, DocumentPageCursor.After(page[^1]).Encode());
    }

    private static IResult Problem(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });
}

// FR-06, NFR-08 (#1575): **キーセットのカーソル**（前ページ末尾の `CreatedAt` と `Id`）。
//
// 🔴 **オフセットにしない。** 走査の途中で前のページの文書が消えると、オフセットでは後続のページが
// 1 件ずつずれて**未読の文書を読み飛ばす**。キーセットは「どこまで読んだか」を値で持つので、
// 削除・追加でずれない（並びのキーが不変であることと対で効く。`Slice` の注記）。
//
// 線上は不透明な文字列（base64url）。中身は `v1:<UtcTicks>:<Id の N 形式>` で、呼び出し側は解釈しない。
internal readonly record struct DocumentPageCursor(long CreatedAtUtcTicks, Guid Id)
{
    private const string Version = "v1";

    internal static DocumentPageCursor After(Document d) => new(d.CreatedAt.UtcTicks, d.Id);

    // 並び（`CreatedAt` 昇順・`Id` 昇順）でカーソルより後ろにあるか。
    internal bool Precedes(Document d)
    {
        var ticks = d.CreatedAt.UtcTicks;
        return ticks > CreatedAtUtcTicks || (ticks == CreatedAtUtcTicks && d.Id.CompareTo(Id) > 0);
    }

    internal string Encode()
    {
        var raw = Encoding.UTF8.GetBytes(
            $"{Version}:{CreatedAtUtcTicks.ToString(CultureInfo.InvariantCulture)}:{Id:N}");
        return Base64Url.EncodeToString(raw);
    }

    internal static bool TryDecode(string? text, out DocumentPageCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(text))
            return false;

        byte[] raw;
        try
        {
            raw = Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(raw).Split(':');
        if (parts.Length != 3 || parts[0] != Version
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !Guid.TryParseExact(parts[2], "N", out var id))
            return false;

        cursor = new DocumentPageCursor(ticks, id);
        return true;
    }
}
