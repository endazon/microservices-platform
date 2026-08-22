namespace GraphService.Api.Foundation.Domain;

// FR-17, SC-09, ADR-0033 決定 3・9, IADR-0241 決定 7: 辺の型辞書のエントリ。
//
// **コード定義（C# の enum）にも DB の列挙型にもしない。** ADR-0033 決定 3 は「値の追加手続きは
// SC-09（タグ辞書）で管理する」と定めており、列挙型にすると値の追加がそのままマイグレーションに
// なる。**行データにすれば型の追加は INSERT のみで済む。**
//
// **識別子は改名で変わらない。** ADR-0033 決定 9 の「改名は許し、既存の辺は新しい名前へ追随する」は、
// 辺がこの Id を参照することで成り立つ。表示名を辺側へ複写する設計だと改名が該当辺すべての UPDATE に
// なり、書きかけの失敗で混在状態が生まれる。
//
// **これは DocumentService のタグ辞書（Tag / TagDictionaryEndpoints。IADR-0152 / IADR-0153）と
// 同型である。** ADR-0033 決定 9 が「同じ規則を SC-09 のタグ辞書にも適用する」と言っている当のものが
// 既に実装されており、車輪を再発明しない。
public class EdgeType
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    // 表示名。**改名（#910）で変わるのはここだけである。**
    public string Name { get; private set; } = string.Empty;

    // ADR-0033 決定 3・8: 意味の層（3 層構成）。Obsidian のリンク構文の写像先でもある（#912）。
    public string Layer { get; private set; } = EdgeTypeLayer.Core;

    // ADR-0033: 対称関係か。true のとき辺は (min, max) に正規化して 1 行で持ち、
    // 読み取り時に無向として提示する（IADR-0241 決定 9）。
    public bool IsSymmetric { get; private set; }

    // 初期値集合（コア 5 種＋推奨 4 種）の目印。**削除ガードとは独立である** ——
    // 削除の可否は「参照が 1 件でもあるか」だけで決まる（ADR-0033 決定 9）。
    public bool IsSeed { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private EdgeType() { }

    public static EdgeType Create(string name, string layer, bool isSymmetric, bool isSeed = false)
        => new()
        {
            Name = Normalize(name),
            Layer = layer,
            IsSymmetric = isSymmetric,
            IsSeed = isSeed,
        };

    // SC-09: 「新しい名前は既存値と重複しない」。**比較は正規化後の名前で行う** ——
    // 前後の空白だけが違う 2 つを別物として登録できると、辞書が実質的に重複を許すことになる。
    public static string Normalize(string name) => name.Trim();

    // FR-17, SC-09, ADR-0033 決定 9: 改名する。
    //
    // **Id は変えない。** 辺はこの Id を参照しているため、変えると既存の参照が切れる。
    // **追随は自動的に起こる** —— 辺は表示名を複写していないためである。
    public void Rename(string newName)
    {
        Name = Normalize(newName);
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

// ADR-0033 決定 3・8: 辺の型の 3 層構成。
public static class EdgeTypeLayer
{
    // 中核 5 種。
    public const string Core = "core";
    // 追加推奨 4 種。
    public const string Recommended = "recommended";
    // 将来検討。
    public const string Future = "future";

    public static bool IsValid(string value)
        => value is Core or Recommended or Future;
}
