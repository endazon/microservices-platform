namespace DataSourceService.Domain;

// FR-01, UC-04, SC-06, ADR-0005, IADR-0295 決定 3: `ConnectionUri` に資格情報を置かせない。
//
// **`ConnectionUri` は `Config` と並ぶ 2 本目の平文の器である。** `SecretConfigMask` は `Config`
// にしか掛からず、`ConnectionUri` は応答へ素で出ていた。一方 `SecretMask` の URI 規則は
// `scheme://user:pass@host` を**明示的に想定して伏せている** —— **コード自身が、資格情報つき URI が
// 入り得ることを認めていた。**
//
// `DatabaseConnector` は `ConnectionUri` を ADO.NET 接続文字列の土台として使う
// （`DbConnectionStringBuilder { ConnectionString = baseConn }`）ため、`Host=..;Password=..` 形式も
// 入り得る。`DatabaseConnector.cs` の契約は「`ConnectionUri`（パスワードを含めない）」と書いていたが、
// **それを強制する検証がどこにも無かった。** 本クラスがその強制である。
//
// **判定規則は 1 本**である —— 「**マスクを掛けて値が変わるなら、それは資格情報を運んでいる**」
// （`SecretMask.CarriesSecret`）。第 2 のマーカー集合を作れば、IADR-0295 決定 1 が解消した
// defect の再発になる。
public static class ConnectionUriPolicy
{
    // 400 の本文に出る案内。**どこへ移せばよいかまで書く** —— 「駄目である」だけでは
    // 運用者は資格情報を消すか、あきらめて古い値のまま放置する。
    public const string CredentialMessage =
        "connectionUri に資格情報を含めないでください（接続先だけを書きます）。"
        + "パスワード・トークンは config へ入れてください（応答ではマスクされます）。";

    public const string PlaceholderMessage =
        "connectionUri に \"" + SecretMask.Placeholder + "\" が含まれています。"
        + "応答のマスク済みの値を編集して送り返すと、保存されている資格情報が失われます。"
        + "接続先の実値を書くか、connectionUri を変更しないでください。";

    // 受理なら null、拒否ならその理由（400 の本文）。
    //
    // 順序に意味がある。
    //   1. 空は従前どおり受理する（未設定は各コネクタが空列挙へ縮退する既存の口である）。
    //   2. **既存値をマスクしたものと一致するなら受理する。** GET の結果をそのまま書き戻した形であり、
    //      保存側（`Preserve`）が実値を保つ。ここで弾くと、資格情報つきの既存行は
    //      **名前 1 つ直すことすらできなくなる。**
    //   3. 資格情報を運んでいれば拒否する。
    //   4. マスク値を**編集して**送り返した形（`***` を含むがマスク規則には掛からない）を拒否する。
    //      2 も 3 もすり抜けるため、ここで止めないと `scheme://***@new-host/db` がそのまま保存され、
    //      **資格情報が黙って消える。**
    public static string? Validate(string? incoming, string? existing)
    {
        if (string.IsNullOrEmpty(incoming)) return null;
        if (IsUnchangedMaskedEcho(incoming, existing)) return null;
        if (SecretMask.CarriesSecret(incoming)) return CredentialMessage;
        if (incoming.Contains(SecretMask.Placeholder, StringComparison.Ordinal)) return PlaceholderMessage;
        return null;
    }

    // 書き込み時の防御（IADR-0148 決定 6 が `Config` について確立した形を `ConnectionUri` へ広げる）:
    // **応答のマスク済みの値をそのまま書き戻しても、既存の実値を壊さない。**
    // **`incoming` は非 null である。** `Patch` は「null＝現状維持」を自分で判定してから呼ぶので、
    // ここへ null 分岐を置くと `Update` の意味論（全置換）だけを黙って変えてしまう。
    public static string Preserve(string incoming, string existing)
        => IsUnchangedMaskedEcho(incoming, existing) ? existing : incoming;

    // 「既存値をマスクしたもの」と一致するか。既存値に秘密が無ければマスクしても値は変わらないので、
    // 通常の無変更 PUT もここで一致する（実値をそのまま保つので結果は同じである）。
    private static bool IsUnchangedMaskedEcho(string incoming, string? existing)
        => !string.IsNullOrEmpty(existing) && incoming == SecretMask.RedactText(existing);
}
