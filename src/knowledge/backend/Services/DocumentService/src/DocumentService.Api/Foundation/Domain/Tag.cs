namespace DocumentService.Api.Foundation.Domain;

// FR-06, FR-09, SC-05, SC-09, #634: タグ辞書のエントリ（IADR-0152 決定 1）。
//
// SC-05 の「既定タグ辞書に整合」と SC-09 の「参照が 1 件でもあるタグは削除拒否・改名は既存文書へ追随・
// 削除前に使用件数を示す」は**すべて契約側の機能**であり、辞書が無いと 1 つも満たせない。
//
// **DocumentService が所有する**——使用件数が文書の局所クエリになるためである（IADR-0152 決定 1）。
// サービスを跨ぐと、削除拒否の判定のたびに同期呼び出しが要り、
// 数え落としが「消してはいけないタグを消せる」事故になる。
//
// **改名（`Rename`）と削除は #635 で足す。** ここには置かない——
// 本 issue では呼ぶ側が無く、書き込まれない `UpdatedAt` 列と使われないメソッドが残るだけになる。
public class Tag
{
    // **識別子は改名で変わらない。** SC-09 が定めた「改名は許して既存の文書が新しい名前へ追随する」は、
    // 文書がこの Id を参照することで成り立つ（IADR-0152 決定 6。**文書側の移行は #635**）。
    // **本 issue の時点では、文書はまだ表示名を複写している。**
    public Guid Id { get; private set; } = Guid.NewGuid();

    // 表示名。**改名（#635）で変わるのはこちらだけである。**
    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private Tag() { }

    public static Tag Create(string name) => new() { Name = Normalize(name) };

    // SC-09: 「新しい名前は既存値と重複しない」。**比較は正規化後の名前で行う**——
    // 前後の空白だけが違う 2 つを別物として登録できると、辞書が実質的に重複を許すことになる。
    public static string Normalize(string name) => name.Trim();
}
