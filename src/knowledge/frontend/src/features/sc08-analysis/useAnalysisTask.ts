import { useBffAnalysisAnalyze } from '@foundation/api/generated/analysis/analysis';
import { ApiError } from '@foundation/api/ApiError';
import type { AiAnswerDto, AnalysisTaskRequest } from '@foundation/api/generated/bff.schemas';

// SC-08, UC-02, FR-07/FR-05: AI 分析の実行（orval 生成フックのラッパ）。
//
// IADR-0127 決定 3: `/bff/analysis/analyze` は docs/api/openapi.yaml にあるため、
// **#503 の 4 画面で唯一 orval 生成フックに載っていた**（生成物があるのに手書きするのは契約の二重管理）。
// #519 で残り 9 ファイルも生成物へ載せ替えたため、いまは本ファイルだけが特別ではない。
// フック名は `operationId` の規約統一（IADR-0135 決定 5）で `useAnalysisAnalyze` から改名された。
// IADR-0127 決定 4: 生成フックは `useMutation` であり、そもそも Query のキャッシュに載らない。
// IADR-0126 決定 1 が避けた事象（戻る操作で古い回答が古い出典つきで復活する）は構造的に起こらないため、
// 同 IADR の適用範囲（SC-01 本文）を広げる必要は無い。

/** 画面が扱う結果の状態。 */
export type AnalysisOutcome =
  | { kind: 'idle' }
  | { kind: 'running' }
  /** 回答あり。 */
  | { kind: 'answered'; answer: AiAnswerDto }
  /** UC-02 例外フロー: 権限外・該当なしを区別しない中立表示（存在秘匿。IADR-0009）。 */
  | { kind: 'empty' }
  /** 400 / 5xx / ネットワーク断。**権限の問題ではない**ので中立表示へ寄せない。 */
  | { kind: 'failed'; error: unknown };

/**
 * 権限による拒否・不在か。
 *
 * UC-02 例外フロー「対象が権限外の場合は対象から除外する。**権限の有無は利用者に開示しない**」に従い、
 * 403 と 404 を「該当なし」と同じ中立表示へ寄せる。
 * **400 / 5xx / ネットワーク断はここに含めない**——中立表示へ寄せると、利用者が「該当なし」と誤解して
 * 再試行しなくなる（本当は送り直せば成功し得る）。
 */
function isHiddenByPolicy(error: unknown): boolean {
  return error instanceof ApiError && (error.kind === 'forbidden' || error.kind === 'notFound');
}

/** 回答が空か（サーバ側の空縮退。ABAC 不許可・該当なしのいずれでも起こる）。 */
function isEmptyAnswer(answer: AiAnswerDto | undefined): boolean {
  return !answer || !(answer.answer ?? '').trim();
}

export function useAnalysisTask() {
  const mutation = useBffAnalysisAnalyze();

  // 生成される応答型は成功（200）と検証エラー（400）の union だが、非 2xx は `apiRequest` が
  // ApiError を投げるため 400 の枝は解決しない。型の上でだけ残る枝を status で落とす。
  const answer = mutation.data?.status === 200 ? mutation.data.data : undefined;

  const outcome: AnalysisOutcome = mutation.isPending
    ? { kind: 'running' }
    : mutation.isError
      ? isHiddenByPolicy(mutation.error)
        ? { kind: 'empty' }
        : { kind: 'failed', error: mutation.error }
      : mutation.isSuccess
        ? isEmptyAnswer(answer)
          ? { kind: 'empty' }
          : { kind: 'answered', answer: answer as AiAnswerDto }
        : { kind: 'idle' };

  return {
    outcome,
    run: (request: AnalysisTaskRequest) => mutation.mutate({ data: request }),
  };
}
