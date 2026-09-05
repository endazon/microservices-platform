using FluentValidation.Results;

namespace DocumentService.Features;

// FR-05, FR-06, FR-09, FR-19, FR-20, FR-21, UC-03, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// ADR-0068 決定 1 / IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1・8:
// **検証結果 → RFC7807（400）の写像**。手書きのガード節から移した端点が共有する唯一の sink である。
//
// 🔴 **形 α（先頭 1 件・鍵つき）の写像だけを持つ。** 移送前の DocumentService のガード節は
// **どれも 1 件返したら即 `return`** しており、複数の項目が同時に不正でも本文には**最初の 1 鍵 1 件**
// しか出ない（例: `Documents/Create` は題名で返り、機密区分の検査へ到達しない）。したがって
// `result.ToDictionary()`（全違反）で写すと**複数違反の要求で鍵が増え、応答本文が変わる**。
// #1278 は群 3 を一律に「全違反を返す」と書いているが、37 呼び出しのうちそれに当たるのは 11 だけで、
// DocumentService は 1 つも該当しない（[[IADR-0398]] コンテキスト）。
//
// 🔴 **鍵は `PropertyName` である。** 検証器は `OverridePropertyName` か `Custom` の `AddFailure` で
// **必ず明示する** —— 推論名は `Title`（PascalCase）になり、移送前の `title` と一致しない。
// 型では止まらないので、各検証器の試験が「鍵の定数」と「リテラル」の両方を見る。
//
// **置き場は 1 段目である**（ADR-0068 決定 1）。Documents / Tags / PrivateNotes / SyncDevices /
// ObsidianSync の 5 集約が使うため 2 段目には置けない。`DocumentEndpoints` に置くと、
// Tags の検証器が Documents 集約の合成点へ依存する形になる。
internal static class ValidationProblems
{
    // 先頭 1 件を、その鍵で RFC7807 の `errors` へ載せる。**器（`Results.ValidationProblem`）は
    // 移送前と同じもの**であり、変わったのは辞書を誰が作るかだけである。
    //
    // 宣言順が応答の契約である（IADR-0371 決定 2）—— 規則の並びを入れ替えると、複数違反したときに
    // 出る 1 件が変わる。各検証器の `MultipleViolations_*` 試験がそれを固定する。
    internal static IResult FirstViolation(ValidationResult result)
    {
        var failure = result.Errors[0];
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [failure.PropertyName] = [failure.ErrorMessage],
        });
    }
}
