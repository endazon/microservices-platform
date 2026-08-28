using System.Text.RegularExpressions;

namespace DataSourceService.Domain;

// FR-01, FR-05, UC-04, SC-06, ADR-0005, IADR-0295 決定 1: **「何が秘密か」の唯一の情報源。**
//
// 従前、同じ知識が 2 箇所にあった —— `SecretConfigMask` は 4 語（token / password / secret /
// credential）を `Contains` で見ており、`SyncErrorRedactor` は 7 種（pwd / api key / authorization を
// 含む）を正規表現で見ていた。**片方に足したときにもう片方が黙って古くなる型**であり、
// `SecretConfigMask` 自身のコメントがその危険を警告しながら、まさにそれが起きていた。
//
// 実害: `Config` のキーが `apiKey` / `pwd` / `privateKey` だと**応答のマスクが掛からなかった**。
// 計画 `06_technical/09_datasource-connectors.md` は SaaS の認証を「OAuth／APIキー」と定めており、
// **計画が名指しする形式が現行マーカーで捕まらない**状態だった。
//
// **向きを 2 箇所に持たない。** キー名判定（`IsSecretKey`）も自由文のマスク（`RedactText`）も、
// ともに下の `KeyMarkers` ただ 1 本を読む。
public static partial class SecretMask
{
    // マスク後の値。応答に出る文字列であり、書き戻しの検出にも同じ値を使う（2 箇所に持たない）。
    public const string Placeholder = "***";

    // 秘密とみなすキー名のマーカー（正規表現の選択肢。大文字小文字は無視する）。
    //
    // **`key` 単独はマーカーにしない。** `spaceKey`（Confluence の空間キー）・`listPath` /
    // `rootPath` のような非秘密キーを誤マスクする。誤マスクは原因の切り分けを潰すので、
    // 「秘密でないものを伏せない」ことも守るべき性質である。
    //
    // `api[-_]?key` は計画が名指しする形式（APIキー）、`private[-_]?key` は秘密鍵であり、
    // **どちらも従前の 2 集合のいずれでも捕まらなかった**（IADR-0295 決定 1）。
    public const string KeyMarkers =
        "password|pwd|token|secret|api[-_]?key|private[-_]?key|credential|authorization";

    [GeneratedRegex(KeyMarkers, RegexOptions.IgnoreCase)]
    private static partial Regex KeyName();

    // 設定辞書のキー名が秘密を指すか。部分一致である（`apiToken` は `token` で捕まる）。
    public static bool IsSecretKey(string key) => KeyName().IsMatch(key);

    // キー=値 / キー:値 の形。**キー名は残し値だけを伏せる** —— 原因の切り分けには
    // 「どの項目が悪いか」が要る。
    //
    // **スキーム語（Bearer / Basic）は値側へ畳んで一緒に伏せる。** 分けて 2 度当てると
    // `Authorization: Bearer xyz` が `Authorization: *** ***` のように二重置換される。
    // 値の終端は区切り記号と**閉じ括弧**でも切る。括弧を値に含めると `(apiToken=abc)` の `)` まで
    // 飲み込み、伏せた跡が `(apiToken=***` となって括弧が閉じない。
    //
    // 定数補間で `KeyMarkers` を差し込む（`[GeneratedRegex]` は定数式を要求するが、
    // 定数補間文字列はコンパイル時定数である）。`}` は補間の閉じ括弧と区別するため二重にする。
    [GeneratedRegex($@"(?<key>{KeyMarkers})(?<sep>\s*[=:]\s*)(?:(?:bearer|basic)\s+)?(?<value>[^;,\s""'()\]}}>]+)",
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

    // 自由文から秘密らしき部分を伏せる。**切り詰めない** —— 上限が要る呼び出し側
    // （`SyncErrorRedactor`）が自分で行う。`ConnectionUri` は varchar(2048) であり、
    // 表示のために切り詰めると値が変わって書き戻しの往復が壊れる。
    //
    // null / 空は入力をそのまま返す（「秘密が無い」ことと「値が無い」ことを混ぜない）。
    public static string? RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var redacted = KeyValueSecret().Replace(text, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Placeholder}");
        redacted = BareHttpAuthScheme().Replace(redacted, m => $"{m.Groups["scheme"].Value} {Placeholder}");
        redacted = UriCredentials().Replace(redacted, m => $"{m.Groups["scheme"].Value}{Placeholder}@");

        return redacted;
    }

    // その文字列が秘密を運んでいるか。**判定規則を 2 本持たない** ——
    // 「マスクを掛けて値が変わるならそれは秘密を運んでいる」という 1 本で決める。
    // 第 2 のマーカー集合を作れば、それは本クラスが解消した defect の再発である。
    public static bool CarriesSecret(string? text) => RedactText(text) != text;
}
