namespace NotificationService.Common.Observability;

// FR-22: 受け口（POST /internal/notifications）経由で `kind` 等に利用者影響の文字列が届き得る。
// 改行・制御文字を未加工で行指向ログへ落とすと偽のログ行を注入できる（CWE-117。CodeQL 検出）。
// McpServer.ToolInvocationService / LlmGateway.LlmRouter の Sanitize と同型（制御文字置換＋切り詰め）。
internal static class LogSanitizer
{
    private const int MaxLength = 100;

    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var cleaned = new string(Array.ConvertAll(
            value.ToCharArray(), c => char.IsControl(c) ? '_' : c));
        return cleaned.Length <= MaxLength ? cleaned : cleaned[..MaxLength] + "…";
    }
}
