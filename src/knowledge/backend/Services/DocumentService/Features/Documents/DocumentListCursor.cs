using System.Buffers.Text;
using System.Globalization;
using System.Text;
using DocumentService.Domain;

namespace DocumentService.Features.Documents;

// FR-06, SC-05, [[IADR-0545]] (#1923): gRPC `ListDocuments` の**並べ替え**。キーが同値なら ID で並べ、ID の向きはキーと揃える
// （降順のキーは ID も降順、昇順は ID も昇順 —— 複合索引 1 本を前からも後ろからも辿れる向き）。
// 線上の enum（`knowledge.document.v1.DocumentListSort`）との写像は `DocumentReadGrpcService.SortOf` の 1 か所に置く。
internal enum DocumentListSortKey
{
    UpdatedDesc,
    UpdatedAsc,
    CreatedDesc,
    CreatedAsc,
    TitleAsc,
    TitleDesc,
}

// FR-06, NFR-16, [[IADR-0537]] (#1923・#1897), [[IADR-0545]]: gRPC `ListDocuments` の**キーセットの続きの印**
// （前ページ末尾の並べ替えキーの値と `Id`）。
//
// 🔴 **`GET /documents/page` の `DocumentPageCursor`（作成日時の昇順）と混ぜない。** 並びのキーも向きも違うので、
//   片方の印をもう片方へ渡すと黙って別の位置から読む。版の札を変えて（`u1` / `s1`）取り違えを `INVALID_ARGUMENT` にする。
// 🔴 **印は並べ替えを持つ**（IADR-0545）。要求の並べ替えと印の並べ替えが違えば呼び出し側（`GrpcService`）が `INVALID_ARGUMENT` にする。
//
// 線上は不透明な文字列（base64url）。中身は、更新日時の降順（既定）は従前どおり `u1:<UtcTicks>:<Id の N 形式>`
// （配備の途中で新旧の document-service が混ざっても既定の並びの印は読める）、ほかは `s1:<キー>:<値>:<Id の N 形式>`
// （値は時刻なら UtcTicks、表題なら UTF-8 の base64url。表題は `:` を含み得るので包む）。呼び出し側は解釈しない。
// 🔴 **返した（読める）文書からしか作らない**（`DocumentReadUseCase.ListPageAsync`）。読めない文書の ID・時刻・表題を載せない。
//
// ADR-0065 決定 2: 一覧の rpc だけが使うが、本体（`DocumentReadUseCase`）が合成点の階層に在るので同じ階層に置く。
internal readonly record struct DocumentListCursor(DocumentListSortKey Sort, long Ticks, string? Title, Guid Id)
{
    private const string LegacyVersion = "u1";
    private const string Version = "s1";

    // 既定の並び（更新日時の降順）の印。
    internal DocumentListCursor(long updatedAtUtcTicks, Guid id) : this(DocumentListSortKey.UpdatedDesc, updatedAtUtcTicks, null, id)
    {
    }

    internal static DocumentListCursor After(Document d) => After(DocumentListSortKey.UpdatedDesc, d);

    internal static DocumentListCursor After(DocumentListSortKey sort, Document d) => sort switch
    {
        DocumentListSortKey.UpdatedDesc or DocumentListSortKey.UpdatedAsc => new(sort, d.UpdatedAt.UtcTicks, null, d.Id),
        DocumentListSortKey.CreatedDesc or DocumentListSortKey.CreatedAsc => new(sort, d.CreatedAt.UtcTicks, null, d.Id),
        _ => new(sort, 0, d.Title, d.Id),
    };

    // カーソルの時刻（UTC・offset 0）。SQL の比較へ渡す値（Npgsql は offset 0 の値しか timestamptz へ書けない）。
    // 🔴 DB から読んだ値（マイクロ秒に丸まった値）から作るので、境界の行とちょうど等しく比べられる。
    internal DateTimeOffset At => new(Ticks, TimeSpan.Zero);

    internal string Encode()
    {
        var id = Id.ToString("N");
        var text = Sort switch
        {
            DocumentListSortKey.UpdatedDesc => $"{LegacyVersion}:{Ticks.ToString(CultureInfo.InvariantCulture)}:{id}",
            DocumentListSortKey.TitleAsc or DocumentListSortKey.TitleDesc =>
                $"{Version}:{CodeOf(Sort)}:{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Title ?? string.Empty))}:{id}",
            _ => $"{Version}:{CodeOf(Sort)}:{Ticks.ToString(CultureInfo.InvariantCulture)}:{id}",
        };
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));
    }

    internal static bool TryDecode(string? text, out DocumentListCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(text))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(text));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = decoded.Split(':');
        if (parts.Length == 3 && parts[0] == LegacyVersion)
        {
            if (!TryTicks(parts[1], out var ticks) || !Guid.TryParseExact(parts[2], "N", out var id))
                return false;
            cursor = new DocumentListCursor(ticks, id);
            return true;
        }

        if (parts.Length != 4 || parts[0] != Version || SortOf(parts[1]) is not { } sort
            || !Guid.TryParseExact(parts[3], "N", out var sid))
            return false;

        if (sort is DocumentListSortKey.TitleAsc or DocumentListSortKey.TitleDesc)
        {
            string title;
            try
            {
                title = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(parts[2]));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return false;
            }
            cursor = new DocumentListCursor(sort, 0, title, sid);
            return true;
        }

        if (!TryTicks(parts[2], out var sticks))
            return false;
        cursor = new DocumentListCursor(sort, sticks, null, sid);
        return true;
    }

    // 表せない時刻は壊れた印として扱う（通すと `At` の組み立てで例外になり UNKNOWN で落ちる）。
    private static bool TryTicks(string text, out long ticks)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks;

    // `u1` が既定の並び（更新日時の降順）を担うので、`s1` に `ud` は無い。
    private static string CodeOf(DocumentListSortKey sort) => sort switch
    {
        DocumentListSortKey.UpdatedAsc => "ua",
        DocumentListSortKey.CreatedDesc => "cd",
        DocumentListSortKey.CreatedAsc => "ca",
        DocumentListSortKey.TitleAsc => "ta",
        DocumentListSortKey.TitleDesc => "td",
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "既定の並びは u1 で書く"),
    };

    private static DocumentListSortKey? SortOf(string code) => code switch
    {
        "ua" => DocumentListSortKey.UpdatedAsc,
        "cd" => DocumentListSortKey.CreatedDesc,
        "ca" => DocumentListSortKey.CreatedAsc,
        "ta" => DocumentListSortKey.TitleAsc,
        "td" => DocumentListSortKey.TitleDesc,
        _ => null,
    };
}
