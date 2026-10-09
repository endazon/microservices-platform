namespace McpServer.Domain;

// FR-16, UC-09, SC-12, 計画 ADR-0134 決定 1, [[IADR-0516]]（2026-10-09 追記 / #1844）:
// 有人（対話型）の MCP クライアントのリダイレクト URI の規則。**登録の検証器と、IdP へ書いた表現の読み戻しが同じ 1 つを使う。**
//
// ■ 許すのは `https` の URI か、ループバックの `http://127.0.0.1` / `http://[::1]`（RFC 8252 §7.3）だけである。
//   🔴 `http://localhost` は認めない（RFC 8252 §8.3: 名前解決が書き換えられ得る。計画 ADR-0134 決定 1 も IP リテラルだけを挙げる）。
// ■ 🔴 **ワイルドカード（`*`）は認めない。** Keycloak は登録値が `*` で終わると前方一致で照合する（`RedirectUtils.matchesRedirects`）。
//   `*` を 1 文字でも通すと「完全一致」が崩れる。
// ■ フラグメントは認めない（RFC 6749 §3.1.2）。利用者情報（`user@`）も認めない（Keycloak はその URI でワイルドカードを無効にするが、
//   見た目の host を偽る形をそもそも入れない）。
// ■ 🔴 **ループバックの port は登録値に明示させる（必須）。** RFC 8252 §7.3 は「port なしで登録し任意の port を受ける」を認めるが、
//   その照合を担う Keycloak 24 の `RedirectUtils` は、要求の URI が `http://127.0.0.1` で始まるとき最初の `:` から次の `/` までを
//   落として照合し直し、利用者情報（`user@`）を見ない。port なしの `http://127.0.0.1/cb` を登録すると
//   `redirect_uri=http://127.0.0.1:49152@evil.example/cb` が一致扱いになり、ブラウザは認可コードを持って evil.example へ送られる
//   （CVE-2024-8883。Keycloak 25.0.6 で修正）。攻撃者は自分の PKCE 検証子でコードを引き換え、audience=mcp-server のトークンを得る。
//   port を明示した登録（`http://127.0.0.1:50000/cb`）は完全一致で照合され、`:50000@evil.example/cb` も別の port も 400 になる
//   （Keycloak 24.0.5 の稼働で実測。integration-stack の門 M9）。`http://[::1]` も同じ規則に揃える（port まで完全一致）。
//   **任意の port を受ける利便は失う**（クライアントは固定の port で待ち受ける）。緩めるには Keycloak を 25.0.6 以上へ上げることが前提
//   （IADR-0516 の #1844 追記）。
public static class RedirectUriRules
{
    /// <summary>1 クライアントに登録できるリダイレクト URI の上限。</summary>
    public const int MaxCount = 10;

    /// <summary>1 件の長さの上限（文字数）。</summary>
    public const int MaxLength = 2048;

    /// <summary>
    /// 1 件の URI が規則を満たすか。満たさなければ理由（利用者に見せる文言）を返す。満たせば null。
    /// </summary>
    public static string? Violation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "リダイレクト URI に空の値があります。";
        if (value.Length > MaxLength) return $"リダイレクト URI は {MaxLength} 文字以下にしてください。";
        if (value != value.Trim()) return $"リダイレクト URI '{value}' の前後に空白があります。";
        if (value.Contains('*'))
            return $"リダイレクト URI '{value}' にワイルドカード（*）は使えません（完全一致で照合します）。";
        // 🔴 Unix では `/callback` が `file:///callback` として絶対 URI に読める（.NET の `Uri`）。`scheme://` の形も要る。
        if (!value.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.IsFile)
            return $"リダイレクト URI '{value}' は絶対 URI ではありません。";
        if (value.Contains('#')) return $"リダイレクト URI '{value}' にフラグメント（#）は使えません。";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return $"リダイレクト URI '{value}' に利用者情報（user@）は使えません。";

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return null;
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && IsLoopbackLiteral(value, uri, out var hasExplicitPort))
        {
            // 🔴 CVE-2024-8883: port なしのループバックは Keycloak 24 で認可コードの横取りを許す（上の注記）。
            return hasExplicitPort
                ? null
                : $"ループバックのリダイレクト URI '{value}' には port を明示してください（例: http://127.0.0.1:53123/callback）。";
        }
        return $"リダイレクト URI '{value}' は https か、ループバックの http://127.0.0.1 / http://[::1] に限ります。";
    }

    // 🔴 **綴りで判定する。** `Uri.IsLoopback` は `localhost` と `127.0.0.0/8` の全体を真にするので使わない
    // （計画が挙げるのは `127.0.0.1` と `[::1]` の 2 つの IP リテラルだけ）。`Uri.Host` は大文字小文字・省略形を正規化するので、
    // 入力の綴りの側も見る（`http://127.1/` は `127.0.0.1` に正規化されるが、Keycloak は綴りで照合する）。
    // port の明示は**綴りで**判定する（`Uri.IsDefaultPort` は `:80` を書いた形と書かない形を区別できない）。
    // authority の直後が `:` で、1〜5 桁の数字が `/`・`?`・末尾のいずれかで終わり、値が 1〜65535 のときだけ明示とみなす。
    private static bool IsLoopbackLiteral(string value, Uri uri, out bool hasExplicitPort)
    {
        hasExplicitPort = false;
        foreach (var (prefix, host) in new[] { ("http://127.0.0.1", "127.0.0.1"), ("http://[::1]", "[::1]") })
        {
            var authorityEnd = value.Length > prefix.Length ? value[prefix.Length] : '/';
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || authorityEnd is not (':' or '/' or '?')) continue;
            if (uri.Host != host) return false;
            hasExplicitPort = authorityEnd == ':' && HasPortDigits(value, prefix.Length + 1) && uri.Port is > 0 and <= 65535;
            return true;
        }
        return false;
    }

    private static bool HasPortDigits(string value, int start)
    {
        var end = start;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        var digits = end - start;
        return digits is >= 1 and <= 5 && (end == value.Length || value[end] is '/' or '?');
    }

    /// <summary>
    /// 一覧が規則を満たすか（有人の登録）。最初の違反の文言を返す。満たせば null。
    /// </summary>
    public static string? ListViolation(IReadOnlyList<string?>? values)
    {
        if (values is null || values.Count == 0)
            return "有人（interactive）にはリダイレクト URI が 1 件以上必要です。";
        if (values.Count > MaxCount) return $"リダイレクト URI は {MaxCount} 件以下にしてください。";
        foreach (var value in values)
        {
            if (Violation(value) is { } violation) return violation;
        }
        var duplicate = values.GroupBy(v => v, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? null : $"リダイレクト URI '{duplicate.Key}' が重複しています。";
    }
}
