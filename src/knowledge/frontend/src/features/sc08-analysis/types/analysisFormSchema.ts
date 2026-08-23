import { z } from 'zod';
import { MAX_INSTRUCTION_LENGTH, MAX_RANGE_QUERY_LENGTH, TASK_TYPES } from './analysisRange';

// SC-08, UC-02, FR-07 / ADR-0031 §採用技術一覧「フォーム = React Hook Form / Zod /
// @hookform/resolvers」/ IADR-0121 決定 1 の第 4 段（#788）。
//
// ■ 文言をスキーマに書かない
//   `message` に置くのは**安定した符号**（`required` / `tooLong`）だけである。日本語を書くと
//   Lingui の抽出対象から外れ、カタログの網羅検査（IADR-0125 決定 4 / `check-i18n-catalogs.js`）を
//   素通りする。画面側が符号を Lingui の文言へ写す。
//
// ■ 上限値の正本は `analysisRange.ts` である
//   バックエンド（`AnalysisPromptBuilder.MaxInstructionLength`）と揃える値であり、
//   ここへ数値を書き写すと片方が古くなる。参照して使う。

/** 検証エラーの符号。画面がこの値を Lingui の文言へ写す。 */
export const ANALYSIS_FORM_ERRORS = ['required', 'tooLong'] as const;
export type AnalysisFormError = (typeof ANALYSIS_FORM_ERRORS)[number];

export const analysisFormSchema = z.object({
  // 空白だけの指示は送らない（`buildAnalysisRequest` が trim するのと同じ判断を入口でも行う）。
  instruction: z
    .string()
    .trim()
    .min(1, { message: 'required' satisfies AnalysisFormError })
    .max(MAX_INSTRUCTION_LENGTH, { message: 'tooLong' satisfies AnalysisFormError }),
  taskType: z.enum(TASK_TYPES),
  // **`.default()` を付けない。** 付けるとスキーマの入力型（省略可）と出力型（必須）が割れ、
  // `zodResolver` の型が `useForm` と噛み合わなくなる（実測）。初期値は `useForm` の
  // `defaultValues` が与えるので、既定値の情報源はそこ 1 か所で足りる。
  rangeQuery: z
    .string()
    .max(MAX_RANGE_QUERY_LENGTH, { message: 'tooLong' satisfies AnalysisFormError }),
});

export type AnalysisFormValues = z.infer<typeof analysisFormSchema>;
