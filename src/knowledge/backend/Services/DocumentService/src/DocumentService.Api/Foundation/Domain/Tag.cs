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
// **［#635］改名（`Rename`）と `UpdatedAt` を足した。** #634 では呼ぶ側が無かったため置かなかった。
public class Tag
{
    // **識別子は改名で変わらない。** SC-09 が定めた「改名は許して既存の文書が新しい名前へ追随する」は、
    // 文書がこの Id を参照することで成り立つ（IADR-0152 決定 6。**文書側の移行は #635**）。
    // **本 issue の時点では、文書はまだ表示名を複写している。**
    public Guid Id { get; private set; } = Guid.NewGuid();

    // 表示名。**改名（#635）で変わるのはこちらだけである。**
    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    // FR-09, SC-09, #635: 改名した時刻。**改名は表示名だけの変更**なので、ここだけが動く。
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private Tag() { }

    public static Tag Create(string name) => new() { Name = Normalize(name) };

    // SC-09: 「新しい名前は既存値と重複しない」。**比較は正規化後の名前で行う**——
    // 前後の空白だけが違う 2 つを別物として登録できると、辞書が実質的に重複を許すことになる。
    public static string Normalize(string name) => name.Trim();

    // FR-09, SC-09, #635: 改名する（「改名は許して既存の文書が新しい名前へ追随する」）。
    //
    // **`Id` は変えない。** 文書はこの `Id` を参照しているので、変えると既存の参照が切れる。
    // **追随は自動的に起こる**——文書は表示名を複写していないためである（[[IADR-0153]] 決定 1）。
    // **索引と外部システム（Qdrant / Wiki.js）は射影**であり、`DocumentUpdated` の再発行で作り直す
    // （同 決定 3）。
    public void Rename(string newName)
    {
        Name = Normalize(newName);
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
