namespace DataSourceService.Domain;

// FR-01, FR-05, UC-04, SC-06（Q14 / #537）, IADR-0053 / IADR-0295: 同期エラーを外へ出す前のマスク。
//
// **例外メッセージは秘密を運ぶ。** 接続文字列（`Password=...`）・資格情報つき URL
// （`https://user:pass@host`）・トークンを含むクエリは、コネクタが投げる例外の Message にそのまま
// 現れる。応答の Config は既に RedactSecrets が守っている（IADR-0053）が、**同期エラーは別経路**
// であり、同じ守りを通さないと admin/operator の画面に平文の資格情報が出る。
//
// **マスクは保存の時点で行う**（表示の時点ではない）。DB に平文が残ると、バックアップ・ログ・
// 将来の別の読み口すべてが漏洩面になる。
//
// ［2026-08-28 追記 / #458］**保存だけでは足りなかった。** 同じ例外が
// (a) 手動同期 API の応答 と (d) 例外ログ の 2 経路で本クラスを通らずに出ていた。
// 両方を本クラス経由へ寄せた（IADR-0295 決定 2・決定 4）。
// **マスクの規則そのものは `SecretMask` へ移した**（`SecretConfigMask` と同じマーカー集合を
// 使うため。IADR-0295 決定 1）。本クラスが持つのは「同期エラーとしての上限と畳み方」だけである。
public static class SyncErrorRedactor
{
    // 画面の 1 行に収める。長い stack 由来の文字列をそのまま持たない。
    public const int MaxLength = 500;

    // 例外メッセージから秘密らしき部分を伏せ、長さを丸める。null / 空白は null を返す
    // （空文字を保存すると「エラーは在るがメッセージが無い」と読めてしまう）。
    //
    // **切り詰めはマスクの後**である。順序が逆だと、上限より後ろにある秘密が
    // 「切り詰めたから消えた」ように見えて、上限を伸ばした瞬間に漏れる。
    public static string? Redact(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var redacted = SecretMask.RedactText(message)!;

        return redacted.Length <= MaxLength ? redacted : redacted[..MaxLength];
    }
}
