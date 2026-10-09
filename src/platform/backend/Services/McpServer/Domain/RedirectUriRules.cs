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
// ■ ループバックの port は**登録値に書いても書かなくてもよい**。Keycloak 24 は要求の URI が `http://127.0.0.1` で始まるとき port を落として
//   もう一度照合する（port なしで登録すれば任意の port を受ける）。`http://[::1]` にはこの扱いが無く、port まで完全一致になる
//   （Keycloak 24.0.5 のソースの読み。稼働での確かめは integration-stack の門 M9。IADR-0516 の #1844 追記）。
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
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && IsLoopbackLiteral(value, uri))
            return null;
        return $"リダイレクト URI '{value}' は https か、ループバックの http://127.0.0.1 / http://[::1] に限ります。";
    }

    // 🔴 **綴りで判定する。** `Uri.IsLoopback` は `localhost` と `127.0.0.0/8` の全体を真にするので使わない
    // （計画が挙げるのは `127.0.0.1` と `[::1]` の 2 つの IP リテラルだけ）。`Uri.Host` は大文字小文字・省略形を正規化するので、
    // 入力の綴りの側も見る（`http://127.1/` は `127.0.0.1` に正規化されるが、Keycloak は綴りで照合する）。
    private static bool IsLoopbackLiteral(string value, Uri uri)
    {
        const string v4 = "http://127.0.0.1";
        const string v6 = "http://[::1]";
        var authorityEnd = value.Length > v4.Length ? value[v4.Length] : '/';
        if (value.StartsWith(v4, StringComparison.Ordinal) && authorityEnd is ':' or '/' or '?')
            return uri.Host == "127.0.0.1";
        var v6End = value.Length > v6.Length ? value[v6.Length] : '/';
        if (value.StartsWith(v6, StringComparison.Ordinal) && v6End is ':' or '/' or '?')
            return uri.Host == "[::1]";
        return false;
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
