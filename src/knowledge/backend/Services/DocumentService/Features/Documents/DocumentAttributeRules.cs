using DocumentService.Domain;
using FluentValidation;

namespace DocumentService.Features.Documents;

// FR-05, FR-19, UC-03, SC-05, ADR-0054, ADR-0068 決定 1, IADR-0047, [[IADR-0270]] 決定 2,
// 計画 ADR-0030 §決定（検証 = FluentValidation）/ [[IADR-0398]] 決定 4:
// **登録・更新・メタデータ更新の 3 操作が共有する属性の入力規則。**
//
// 移送前は `DocumentEndpoints.ConfidentialityProblemOrNull` / `DocScopeProblemOrNull` という
// **1 つの判定**を 3 つの端点が呼んでいた。移送後もその性質を保つため、規則を 1 組だけここに置く。
// **書き分けると「登録では弾くのに更新では通る」という穴が空く。**
//
// **置き場は 2 段目である**（ADR-0068 決定 1: 3 段目へ下ろすのは「その操作の処理」。集約の複数操作が
// 使うものは 2 段目に残す）——**旧ヘルパと同じ場所**であり、移送で置き場を動かしていない。
//
// 🔴 **述語の実体は `Domain/DocumentAttributes` の関数のままである。** Domain は FluentValidation に
// 依存しない（`check-backend-libraries.js` の Domain 外部依存ゼロ規則）ので、橋渡しの拡張メソッドを
// `Features/` 側に置く。ここに述語を書き写すと不変条件が 2 箇所になり、どちらかだけが直る。
//
// 🔴 **`Custom` で書く。`Must` ＋ `WithMessage(func)` にしない** —— 後者は同じ検証関数を 2 度呼び、
// 失敗時のメッセージが**2 度目の呼び出し**由来になる。`Custom` なら 1 度で済み、
// **鍵（`AddFailure` の第 1 引数）も同じ行に書ける**（推論名に落ちない）。
internal static class DocumentAttributeRules
{
    // FR-05, UC-03, SC-05, IADR-0047: 機密区分（必須属性）のサーバー側検証（最終防衛線）。
    // 欠落・未知値は 400。鍵は `DocumentAttributes.ConfidentialityKey`（= "confidentiality"）。
    internal static IRuleBuilderOptionsConditions<T, Dictionary<string, string>?> Confidentiality<T>(
        this IRuleBuilder<T, Dictionary<string, string>?> rule)
        => rule.Custom((attributes, ctx) =>
        {
            var (ok, error) = DocumentAttributes.ValidateConfidentiality(attributes);
            if (!ok) ctx.AddFailure(DocumentAttributes.ConfidentialityKey, error!);
        });

    // FR-19, ADR-0054, [[IADR-0270]] 決定 2: doc_scope の値域検証。
    // 🔴 欠落は拒否しない（既存文書へ遡及付与しない方針。ADR-0054 §結果）。未知値のみ 400。
    // **不変性の検査（`DocScopeChangedProblemOrNull`）とは別物**であり、あちらは既存値が要るので
    // 端点に残る（`FindAsync` の後ろ。[[IADR-0398]] 決定 8）。
    internal static IRuleBuilderOptionsConditions<T, Dictionary<string, string>?> DocScope<T>(
        this IRuleBuilder<T, Dictionary<string, string>?> rule)
        => rule.Custom((attributes, ctx) =>
        {
            var (ok, error) = DocumentAttributes.ValidateDocScope(attributes);
            if (!ok) ctx.AddFailure(DocumentAttributes.DocScopeKey, error!);
        });
}
