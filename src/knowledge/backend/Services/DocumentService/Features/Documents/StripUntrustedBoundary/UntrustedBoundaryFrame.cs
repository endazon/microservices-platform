namespace DocumentService.Features.Documents.StripUntrustedBoundary;

// FR-06, [[IADR-0556]] 決定 2 (#1973): AST が 2026-10-16 まで KB へ保存した表題・本文の**枠**（境界語）を剥がす純関数。
//
// AST の旧い保存（AST#1359 の前の `PromptSafetySanitizer.Sanitize`）は、値を `<<<UNTRUSTED_DATA\n{中身}\nUNTRUSTED_DATA>>>` で囲んでいた。
// 🔴 **剥がすのは先頭と末尾の枠だけ**である。中身の途中に現れる境界語は消さない（AST の判断側は読むときに不変点まで剥がす。
//   ここで中身を書き換えると、利用者が書いた文字列まで変わり得る）。
// 🔴 **枠が無くなるまで繰り返す。** 剥がした結果がまた枠の形（入れ子）なら、もう一度剥がす。こうしておくと、出力は決して枠の形を
//   していない（`IsWrapped` が偽）ので、**2 回流しても 2 回目は何も変えない**（冪等）。
// 境界語の直後・直前の改行は 1 つだけ剥がす（`\n` か `\r\n`）。中身の前後の空白は触らない（AST は囲む前に Trim していた）。
internal static class UntrustedBoundaryFrame
{
    // AST の境界語（AST の `UntrustedDataBoundary.OpenMarker` / `CloseMarker` と同じ文字列。AST へは依存しない）。
    public const string OpenMarker = "<<<UNTRUSTED_DATA";
    public const string CloseMarker = "UNTRUSTED_DATA>>>";

    // 先頭が開始の境界語で、末尾が終了の境界語であり、2 つが重ならない。
    public static bool IsWrapped(string? text)
        => text is not null
            && text.Length >= OpenMarker.Length + CloseMarker.Length
            && text.StartsWith(OpenMarker, StringComparison.Ordinal)
            && text.EndsWith(CloseMarker, StringComparison.Ordinal);

    // 枠を剥がした値。枠の形でなければ入力をそのまま返す（null は null）。
    public static string? Strip(string? text)
    {
        var current = text;
        while (IsWrapped(current))
        {
            var inner = current![OpenMarker.Length..^CloseMarker.Length];
            inner = TrimOneLeadingNewline(inner);
            inner = TrimOneTrailingNewline(inner);
            current = inner;
        }
        return current;
    }

    private static string TrimOneLeadingNewline(string s)
        => s.StartsWith("\r\n", StringComparison.Ordinal) ? s[2..]
            : s.StartsWith('\n') ? s[1..]
            : s;

    private static string TrimOneTrailingNewline(string s)
        => s.EndsWith("\r\n", StringComparison.Ordinal) ? s[..^2]
            : s.EndsWith('\n') ? s[..^1]
            : s;
}
