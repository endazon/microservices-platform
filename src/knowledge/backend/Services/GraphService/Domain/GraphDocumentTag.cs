namespace GraphService.Domain;

// FR-17, ADR-0033 決定 2・6, [[IADR-0522]] (#1396): 文書が持つタグの**複製**（共有タグの辺の材料）。
//
// 正本は DocumentService であり、`DocumentUpdated.Tags`（表示名）を受信のたびに全量で置き換える。
// **キーは正規化した名前**（前後の空白を落とし、不変カルチャで小文字化）。大小文字・空白だけが違う
// 2 つの綴りを別のタグとして数えると、同じタグの文書どうしが結ばれない。
public class GraphDocumentTag
{
    // 列の長さ。これより長いタグは切り詰める（捨てると、その文書だけが黙って結ばれなくなる）。
    public const int MaxTagLength = 200;

    public Guid DocumentId { get; private set; }

    public string Tag { get; private set; } = string.Empty;

    private GraphDocumentTag() { }

    public static GraphDocumentTag Create(Guid documentId, string normalizedTag)
        => new() { DocumentId = documentId, Tag = normalizedTag };

    // イベントのタグ列を正規化した集合へ。空・空白だけの値は捨てる。
    public static HashSet<string> Normalize(IEnumerable<string>? tags)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (tags is null) return keys;
        foreach (var raw in tags)
        {
            if (raw is null) continue;
            var key = raw.Trim().ToLowerInvariant();
            if (key.Length == 0) continue;
            keys.Add(key.Length <= MaxTagLength ? key : key[..MaxTagLength]);
        }
        return keys;
    }
}
