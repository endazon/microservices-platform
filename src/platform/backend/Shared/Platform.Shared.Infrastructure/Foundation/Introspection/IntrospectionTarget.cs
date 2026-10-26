namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, ADR-0018, IADR-0029 (#2118): ポートの接続先識別子（自己申告の `target`）を URL から作る。
//
// 🔴 **ホスト:ポートだけを返す。** 自己申告は構成情報 API（管理者向け）を通って画面と監査へ出る。URL をそのまま載せると、
//    userinfo（`user:pass@`）・クエリ（API キーを載せる構成）・パスが出る。接続先の識別にはホストとポートで足りる。
//    読めない値は null（「申告しない」）へ倒す —— 推測した接続先を載せない。
public static class IntrospectionTarget
{
    public static string? HostAndPort(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host))
            return null;
        return $"{uri.Host}:{uri.Port}";
    }
}
