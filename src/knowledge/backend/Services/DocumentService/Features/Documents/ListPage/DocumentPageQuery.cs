using System.Buffers.Text;
using System.Globalization;
using System.Text;
using DocumentService.Domain;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.ListPage;

// FR-06, NFR-08, ADR-0036 D-08, ADR-0034 決定 9, ADR-0054 (#1575):
// `GET /documents/page` の本体 —— **組織文書**を属性の完全一致で絞り、キーセットのカーソルで切り出す。
//
// 🔴 **見える集合を広げない。** DocumentService の読み取りは、内容の ABAC の門が閉じている間は組織文書の内容の ABAC を持たず
// （実施点は BFF の `BffScopeResolver` ＋ `IsManageable`。IADR-0041 / IADR-0045、`IADR-0012`。［2026-09-28 / #1615］門が開けば
// 端点が切り出しの前に `DocumentReadAccess` で絞る）、
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

    // FR-06, NFR-08, [[IADR-0509]] (#1765): 台帳を SQL のキーセットで塊ごとに読む大きさ。
    // 初回は `limit + 1` 行（絞り込みが緩ければ 1 回で足りる）、続きは 2 倍ずつ広げ、この上限で止める
    // （厳しい絞り込みで往復の回数が台帳の件数に比例しないようにする）。
    internal const int MaxScanChunk = 2000;

    // 対象集合（組織文書 ∩ 全絞り込みに一致 ∩ 読める）を全順序（`CreatedAt` 昇順・同時刻は `Id` 昇順）で辿り、
    // カーソルの後ろから `limit` 件を切り出す。
    //
    // 🔴 **並びのキーは作成時刻（不変）である。更新時刻にしない。** 更新時刻で並べると、走査の途中で
    // 更新された文書が先頭へ移り、未読のまま飛ばされる（キーセットでもオフセットでも起きる）。
    // 作成時刻は台帳の初期化子でしか決まらないので、**走査の間ずっと在った文書はちょうど 1 回ずつ返る**。
    // 走査の途中で作られた文書は末尾に現れ、削除はカーソルの位置を動かさない。
    //
    // ［2026-10-08 / #1765 / [[IADR-0509]]］**並べる・カーソルと比べるは SQL で行う**（`AfterCursor` / `InPageOrder`）。
    // 従前は台帳の全件を読んでメモリで並べていた（1 ページ O(N)、1 回の走査 O(N²)）。
    // 🔴 **絞り込みの述語はメモリのまま**である —— 属性は jsonb へ値変換で写しており LINQ から SQL へ訳せず、
    //   個人資料の判定（`DocumentScopes.IsPrivateNote`）は大文字小文字を区別しないので jsonb の包含でも同じ意味にならない。
    //   そこで台帳を SQL の並びで**塊ごとに**読み、塊の中で述語を当て、一致が `limit + 1` 件に達するか台帳が尽きるまで進む。
    //   塊の続きは塊の末尾の行（DB から読んだ値）から作るので、塊の境目でも抜け・重複は起きない。
    // 🔴 **.NET 側で並べ直さない。** Postgres の uuid の順と `Guid.CompareTo` の順を、並べる側と比べる側で混ぜない。
    //
    // `readable` は内容の ABAC の門が開いたときだけ渡す（切り出しの**前**に絞る。#1615）。塊の中の、述語に一致した文書だけを渡す。
    internal static async Task<(List<Document> Page, string? NextCursor)> ReadPageAsync(
        IQueryable<Document> ledger, IReadOnlyDictionary<string, string> filters, int limit, DocumentPageCursor? after,
        Func<List<Document>, CancellationToken, Task<List<Document>>>? readable, CancellationToken ct)
    {
        var matches = new List<Document>(limit + 1);
        var position = after;
        var chunk = limit + 1;
        while (true)
        {
            var batch = await ledger.AfterCursor(position).InPageOrder().Take(chunk).ToListAsync(ct);
            var admitted = batch.Where(d => Matches(d, filters)).ToList();
            if (readable is not null && admitted.Count > 0)
                admitted = await readable(admitted, ct);

            foreach (var d in admitted)
            {
                matches.Add(d);
                if (matches.Count > limit) break;
            }

            if (matches.Count > limit || batch.Count < chunk)
                break;
            position = DocumentPageCursor.After(batch[^1]);
            chunk = Math.Min(chunk * 2, MaxScanChunk);
        }

        if (matches.Count <= limit)
            return (matches, null);

        var page = matches.Take(limit).ToList();
        return (page, DocumentPageCursor.After(page[^1]).Encode());
    }

    // 組織文書であり、全絞り込みに一致する（AND・キーも値も大文字小文字を区別する完全一致）。
    private static bool Matches(Document d, IReadOnlyDictionary<string, string> filters)
        => !DocumentScopes.IsPrivateNote(d.Attributes)
            && filters.All(f =>
                d.Attributes.TryGetValue(f.Key, out var v) && string.Equals(v, f.Value, StringComparison.Ordinal));

    // FR-02, FR-06, [[IADR-0509]] (#1765): 並び（`CreatedAt` 昇順・同時刻は `Id` 昇順）でカーソルより**厳密に後ろ**の行。
    // SQL では `"CreatedAt" >= @c AND ("CreatedAt" > @c OR "Id" > @id)` に訳される。
    // 🔴 **先頭の `CreatedAt >= c` を外さない**（意味は `CreatedAt > c OR (CreatedAt = c AND Id > id)` と同じで、冗長に見える）。
    //   Postgres はこの項だけを `(CreatedAt, Id)` の索引の**開始位置**（Index Cond）に使える。素の OR の形だと索引を先頭から
    //   なめて述語で捨てるので、カーソルの位置に比例して読む（22,564 件の台帳の 15,000 件目で 15,001 行を捨てた。実測は IADR-0509）。
    //   行値の比較（`(CreatedAt, Id) > (c, id)`）は EF の InMemory で評価できないので採らない。
    // 🔴 時刻は offset 0 で渡す（Npgsql は offset 0 の `DateTimeOffset` しか timestamptz へ書けない）。
    // 🔴 「以上」にしない —— 前ページの末尾をもう一度返す。
    internal static IQueryable<Document> AfterCursor(this IQueryable<Document> ledger, DocumentPageCursor? after)
    {
        if (after is not { } cursor)
            return ledger;
        var createdAt = cursor.CreatedAt;
        var id = cursor.Id;
        return ledger.Where(d => d.CreatedAt >= createdAt && (d.CreatedAt > createdAt || d.Id > id));
    }

    // FR-02, FR-06, [[IADR-0509]] (#1765): ページの並び。**キーセットの述語（`AfterCursor`）と同じ 2 列・同じ向き**で並べる。
    internal static IOrderedQueryable<Document> InPageOrder(this IQueryable<Document> ledger)
        => ledger.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id);

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

    // カーソルの作成時刻（UTC・offset 0）。SQL の比較へ渡す値（#1765）。
    // 🔴 カーソルは DB から読んだ値（timestamptz＝マイクロ秒に丸まった値）から作るので、境界の行とちょうど等しく比べられる。
    internal DateTimeOffset CreatedAt => new(CreatedAtUtcTicks, TimeSpan.Zero);

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
        // #1765（監査 Y1）: SQL 側の比較は DateTimeOffset で行うので、表せない時刻は壊れたカーソルとして 400 にする
        //   （通さないと CreatedAt の組み立てで ArgumentOutOfRangeException になり 500 で落ちる）。
        if (ticks > DateTimeOffset.MaxValue.UtcTicks)
            return false;

        cursor = new DocumentPageCursor(ticks, id);
        return true;
    }
}
