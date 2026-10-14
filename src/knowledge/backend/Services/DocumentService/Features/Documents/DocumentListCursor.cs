using System.Buffers.Text;
using System.Globalization;
using System.Text;
using DocumentService.Domain;

namespace DocumentService.Features.Documents;

// FR-06, NFR-16, [[IADR-0537]] (#1923・#1897): gRPC `ListDocuments` の**キーセットの続きの印**
// （前ページ末尾の `UpdatedAt` と `Id`）。並びは更新日時の降順・同時刻は ID の降順である。
//
// 🔴 **`GET /documents/page` の `DocumentPageCursor`（作成日時の昇順）と混ぜない。** 並びのキーも向きも違うので、
//   片方の印をもう片方へ渡すと黙って別の位置から読む。版の札を変えて（`u1`）取り違えを `INVALID_ARGUMENT` にする。
//
// 線上は不透明な文字列（base64url）。中身は `u1:<UtcTicks>:<Id の N 形式>` で、呼び出し側は解釈しない。
// 🔴 **返した（読める）文書からしか作らない**（`DocumentReadUseCase.ListPageAsync`）。読めない文書の ID・時刻を載せない。
//
// ADR-0065 決定 2: 一覧の rpc だけが使うが、本体（`DocumentReadUseCase`）が合成点の階層に在るので同じ階層に置く。
internal readonly record struct DocumentListCursor(long UpdatedAtUtcTicks, Guid Id)
{
    private const string Version = "u1";

    internal static DocumentListCursor After(Document d) => new(d.UpdatedAt.UtcTicks, d.Id);

    // カーソルの更新時刻（UTC・offset 0）。SQL の比較へ渡す値（Npgsql は offset 0 の値しか timestamptz へ書けない）。
    // 🔴 DB から読んだ値（マイクロ秒に丸まった値）から作るので、境界の行とちょうど等しく比べられる。
    internal DateTimeOffset UpdatedAt => new(UpdatedAtUtcTicks, TimeSpan.Zero);

    internal string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
        $"{Version}:{UpdatedAtUtcTicks.ToString(CultureInfo.InvariantCulture)}:{Id:N}"));

    internal static bool TryDecode(string? text, out DocumentListCursor cursor)
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
        // 表せない時刻は壊れた印として扱う（通すと `UpdatedAt` の組み立てで例外になり UNKNOWN で落ちる）。
        if (ticks > DateTimeOffset.MaxValue.UtcTicks)
            return false;

        cursor = new DocumentListCursor(ticks, id);
        return true;
    }
}
