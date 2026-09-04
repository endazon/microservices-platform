using FeedbackService.Domain;
using Knowledge.Contracts.Dtos;
using Riok.Mapperly.Abstractions;

namespace FeedbackService.Features.Feedback;

// FR-08, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly。選定基準 4「実行時リフレクションより
// コンパイル時生成を優先する」）/ IADR-0371 決定 3: ドメイン → DTO の写像。
//
// 従前は `FeedbackEndpoints.ToDto` の手書き詰め替え 1 本であった。`AnswerFeedback` と
// `FeedbackDto` は 8 プロパティすべて同名の 1:1 であり、Mapperly の既定規約でそのまま写る。
//
// **置き場は 2 段目（`Features/Feedback/`）である。** 投稿（Submit）と一覧（List）の
// **2 操作が使う**ためであり、ADR-0068 決定 2 の基準（1 操作にしか使われないものだけ 3 段目へ）
// の適用結果である。
//
// 生成コードは `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（IADR-0195 決定 1）。
// **床は動かない。**
[Mapper]
internal static partial class FeedbackMapper
{
    // FR-08: 保存済みフィードバック → 応答 DTO。実体は source generator が生成する。
    internal static partial FeedbackDto ToDto(AnswerFeedback feedback);
}
