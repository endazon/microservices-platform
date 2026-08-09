using System.Text.RegularExpressions;

namespace DataSourceService.Api.Foundation.Services;

// FR-01, FR-05, UC-04, SC-06（Q14 / #537）, IADR-0053: 直近同期エラーを画面へ返す前のマスク。
//
// **例外メッセージは秘密を運ぶ。** 接続文字列（`Password=...`）・資格情報つき URL
// （`https://user:pass@host`）・トークンを含むクエリは、コネクタが投げる例外の Message にそのまま
// 現れる。応答の Config は既に RedactSecrets が守っている（IADR-0053）が、**直近エラーは新しい経路**
// であり、同じ守りを通さないと admin/operator の画面に平文の資格情報が出る。
//
// **マスクは保存の時点で行う**（表示の時点ではない）。DB に平文が残ると、バックアップ・ログ・
// 将来の別の読み口すべてが漏洩面になる。
public static partial class SyncErrorRedactor
{
    // 画面の 1 行に収める。長い stack 由来の文字列をそのまま持たない。
    public const int MaxLength = 500;

    // キー=値 / キー:値 の形。**キー名は残し値だけを伏せる** —— 原因の切り分けには「どの項目が悪いか」が要る。
    // 既存の RedactSecrets（IADR-0053）が使うマーカーに authorization を加える。あちらは Config の
    // キー名を見るので Authorization ヘッダは現れないが、こちらは例外メッセージなので現れる。
    //
    // **スキーム語（Bearer / Basic）は値側へ畳んで一緒に伏せる。** 分けて 2 度当てると
    // `Authorization: Bearer xyz` が `Authorization: *** ***` のように二重置換される。
    // 値の終端は区切り記号と**閉じ括弧**でも切る。括弧を値に含めると `(apiToken=abc)` の `)` まで
    // 飲み込み、伏せた跡が `(apiToken=***` となって括弧が閉じない。
    [GeneratedRegex(@"(?<key>password|pwd|token|secret|api[-_]?key|credential|authorization)(?<sep>\s*[=:]\s*)(?:(?:bearer|basic)\s+)?(?<value>[^;,\s""'()\]}>]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    // キーを伴わない裸の `Bearer <token>` / `Basic <base64>`（ログ本文に直接現れる形）。
    // 上の規則を先に当てているので、キー付きの形はここへ届かない。
    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)\s+(?<value>[^\s;,""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex BareHttpAuthScheme();

    // 資格情報つき URI（scheme://user:pass@host）。ユーザー名も残さない（利用者アカウント名は
    // それ自体が攻撃の手掛かりになる）。
    //
    // **パスワード部は貪欲に取る。** `@` を含むパスワード（`p@ssw0rd`）は実在するため、最初の `@` で
    // 切ると `https://***@ssw0rd@host` のように後半が残る。貪欲に取って**最後の `@`** を境にすれば、
    // ホスト名の直前まで正しく伏せられる（`[^/\s]` なのでパス・空白は越えない）。
    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.-]*://)(?<cred>[^/\s:@]+:[^/\s]*)@", RegexOptions.IgnoreCase)]
    private static partial Regex UriCredentials();

    // 例外メッセージから秘密らしき部分を伏せ、長さを丸める。null / 空白は null を返す
    // （空文字を保存すると「エラーは在るがメッセージが無い」と読めてしまう）。
    public static string? Redact(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var redacted = KeyValueSecret().Replace(message, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}***");
        redacted = BareHttpAuthScheme().Replace(redacted, m => $"{m.Groups["scheme"].Value} ***");
        redacted = UriCredentials().Replace(redacted, m => $"{m.Groups["scheme"].Value}***@");

        return redacted.Length <= MaxLength ? redacted : redacted[..MaxLength];
    }
}
