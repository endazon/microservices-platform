using System.Security.Cryptography;
using System.Text;

namespace IngestionService.Domain;

// FR-02, FR-05, [[IADR-0595]] 決定 1・3 (#2101): **文書ごとに、索引へ最後に反映した版**（版の印）。
//
// `UpdatedAt` は反映した `DocumentUpdated.UpdatedAt`（Unix epoch ミリ秒で持つ。点の `updated_at` と同じ精度）。
// `AccessFingerprint` は反映した版の**判定軸（属性辞書 ＋ 共有先）の一方向の指紋**であり、次の版で判定軸が
// 変わったかだけを見るために持つ（値そのものは持たない。[[IADR-0595]] 決定 3）。撤収した版は空文字列。
public sealed record DocumentIndexVersion(DateTimeOffset UpdatedAt, string AccessFingerprint)
{
    // 比較はミリ秒で行う（点の `updated_at` と版の印は epoch ミリ秒で保存される。ティックで比べると、
    // 同じイベントの再配送が保存値より「新しい」と読めてしまい、等しいことを判定できない）。
    public long UpdatedAtMs => UpdatedAt.ToUnixTimeMilliseconds();
}

// FR-02, FR-05, [[IADR-0595]] 決定 1 (#2101): 到着した更新を索引へ反映するかの判定（純関数）。
public static class DocumentIndexOrdering
{
    // 🔴 **索引に反映済みの版より厳密に古い更新は反映しない**（skip して ack する）。等しい版は反映する
    // （再配送・再発行は同じ版を運ぶ。冪等な上書きで収束する）。版の印が無い（初回・移行前）なら反映する。
    public static bool IsStale(DocumentIndexVersion? indexed, DateTimeOffset incoming)
        => indexed is not null && incoming.ToUnixTimeMilliseconds() < indexed.UpdatedAtMs;
}

// FR-05, FR-19, [[IADR-0595]] 決定 3 (#2101): 判定軸（属性辞書 ＋ 共有先）の指紋。
//
// 🔴 **判定軸が変わった更新は、旧版の点を先に消してから書く**（fail-closed。失敗しても旧版の判定軸で点が残らない）。
// 変わっていない更新は新版を書き終えてから旧版を消す（失敗しても文書が索引から消えない）。その分岐の材料である。
// 鍵の順・共有先の順・重複に依らない正準形を SHA-256 にかける（同じ判定軸は必ず同じ値になる）。
public static class IndexAccessFingerprint
{
    public static string Of(IReadOnlyDictionary<string, string> attributes, IReadOnlyCollection<string>? sharedWith)
    {
        var canonical = new StringBuilder();
        foreach (var (key, value) in attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
            canonical.Append("a\u001f").Append(key).Append('\u001f').Append(value).Append('\u001e');
        foreach (var subject in (sharedWith ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            canonical.Append("s\u001f").Append(subject).Append('\u001e');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

// FR-02, [[IADR-0595]] 決定 2 (#2101): この取り込みで書いた点。旧版の点の削除で**残す**点を指す。
// `Collection` が null の点は語彙索引（名前は実装が持つ）。点 ID はコレクションごとに別物として扱う ——
// 同じチャンク ID の点が別のコレクション（機密区分が変わる前の置き場所・埋め込み待ちの点）に残っていれば消す。
public sealed record IndexedPointRef(string? Collection, Guid PointId)
{
    public static IndexedPointRef Lexical(Guid pointId) => new(null, pointId);
}
