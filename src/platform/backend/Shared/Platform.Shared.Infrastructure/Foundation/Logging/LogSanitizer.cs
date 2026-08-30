namespace Platform.Shared.Infrastructure.Foundation.Logging;

// NFR, CodeQL(cs/log-forging) (#1019): ログ行へ出す**値域の閉じていない文字列**から
// 改行・制御文字を除去し、長さを切る。
//
// 🔴 **なぜ要るのか。** 本番の各 `Program.cs` は `ClearProviders` を呼んでおらず、既定の
// Console プロバイダ（行指向）が有効である。利用者由来の文字列を未加工で落とすと、改行を
// 仕込むだけで**偽の監査行を注入できる**（ログ偽造・CWE-117）。
//
// **先例**: `LlmRouter.Sanitize`（FR-11）と `ToolInvocationService.SanitizeForLog`（ADR-0024）が
// 同型の私有実装を持つ。3 つ目の複製を作らないため、ユニット外から参照できる
// `Platform.Shared.Infrastructure` へ引き上げた（判断の記録は IADR-0304）。
//
// **`Platform.Shared.Kernel` へは置かない** —— Kernel は Result / Error と DDD 基底型の共有
// カーネルであり、`src/README.md` 依存規則により Domain からのみ参照される。ログ整形は
// Infrastructure の関心である。
public static class LogSanitizer
{
    /// <summary>ログ 1 項目あたりの既定の長さ上限。要求由来の値でログを溢れさせない。</summary>
    public const int DefaultMaxLength = 512;

    /// <summary>
    /// 制御文字（改行・復帰・タブ・NUL 等）を <c>_</c> へ置き換え、<paramref name="maxLength"/> で切る。
    /// 切ったときは末尾へ <c>…</c> を付け、切り詰めが起きたことを読み手に残す。
    /// </summary>
    public static string Sanitize(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // char.IsControl は \r \n \t \0 と C0/C1 制御域を捕まえる。**置換であって除去ではない** ——
        // 消すと "a\nb" と "ab" が同じ行になり、注入の痕跡が読めなくなる。
        var cleaned = new string(Array.ConvertAll(
            value.ToCharArray(), c => char.IsControl(c) ? '_' : c));

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }
}
