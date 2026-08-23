import { useCallback, useRef, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { apiStream } from '@foundation/api/apiClient';
import type { AskRequestAttributeFilters } from '@foundation/api/generated/bff.schemas';
import { useBffSubmitFeedback } from '@foundation/api/generated/feedback/feedback';
import type { AskCitation } from '../types/citations';

// SC-01, UC-01, FR-04/FR-08: AI 回答（SSE）とフィードバック送信の状態管理。
//
// IADR-0126 決定 1: **回答は TanStack Query のキャッシュに載せない。** 同じ質問でも回答は毎回生成され
// `answerId` が変わるため、キャッシュに載せると戻る操作や再マウントで**古い回答が古い出典つきで復活する**。
// 出典は「いま表示している回答の根拠」であり、これは単なる古さではなく誤った根拠の提示になる。
// 発火（送信）は `useMutation` が持ち、増えていく途中経過（token 列）だけをローカル state に蓄積する。

/** 回答の進行状態。`idle` は未送信、`error` は縮退（UC-01 例外フロー）の入口である。 */
export type AnswerStatus = 'idle' | 'streaming' | 'done' | 'error';

export interface AnswerState {
  status: AnswerStatus;
  /** SSE の `token` を到着順に連結した本文。 */
  answer: string;
  /** SSE の `citations`（本文より先に届く）。 */
  citations: AskCitation[];
  /** SSE の `done` で確定する回答 ID。フィードバック（FR-08）の紐付け先。 */
  answerId: string | null;
}

const INITIAL: AnswerState = { status: 'idle', answer: '', citations: [], answerId: null };

interface DonePayload {
  answerId: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
}

/** 呼び出し側の意図的な中断（連投・離脱）は失敗ではない。 */
function isAbort(err: unknown): boolean {
  return err instanceof DOMException && err.name === 'AbortError';
}

/** SC-01, #539: 質問と、任意の対象範囲（絞り込み）。 */
interface AskInput {
  question: string;
  attributeFilters?: AskRequestAttributeFilters;
}

export function useAskStream() {
  const [state, setState] = useState<AnswerState>(INITIAL);
  const abortRef = useRef<AbortController | null>(null);

  const ask = useMutation({
    mutationFn: async ({ question, attributeFilters }: AskInput) => {
      // 直前のストリームを中断する（連投で 2 本のストリームが同じ state へ書き込むのを防ぐ）。
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;
      setState({ ...INITIAL, status: 'streaming' });

      await apiStream(
        '/analysis/ask/stream',
        // FR-05: クライアントは ABAC スコープを送らない（送っても BFF は使わない＝権限昇格の防止）。
        //
        // **［#539］`attributeFilters` は権限昇格ではない。** これは「自分の権限の内側を
        // さらに絞る」指定であり、サーバ側で ABAC と交差して **narrowing-only** に扱われる
        // （`DataRangeScopeResolver`）。**何も選んでいなければ載せない**（`undefined` は
        // JSON から落ちる）ので、旧来の要求と同じ形になる。
        { json: { question, attributeFilters } },
        (ev) => {
          if (ev.event === 'citations') {
            const parsed = JSON.parse(ev.data) as { citations?: AskCitation[] };
            setState((s) => ({ ...s, citations: parsed.citations ?? [] }));
          } else if (ev.event === 'token') {
            const parsed = JSON.parse(ev.data) as { text?: string };
            setState((s) => ({ ...s, answer: s.answer + (parsed.text ?? '') }));
          } else if (ev.event === 'done') {
            const parsed = JSON.parse(ev.data) as DonePayload;
            setState((s) => ({ ...s, status: 'done', answerId: parsed.answerId }));
          } else if (ev.event === 'error') {
            // UC-01 例外フロー: LLM が不調な場合。画面は検索結果一覧への導線へ縮退する。
            setState((s) => ({ ...s, status: 'error' }));
          }
        },
        controller.signal,
      );
    },
    onSuccess: () => {
      // `done` が来ないまま上流が閉じた場合も、進行中のまま固まらせない。
      setState((s) => (s.status === 'streaming' ? { ...s, status: 'done' } : s));
    },
    onError: (err) => {
      // 中断は失敗ではない。中断した側（新しい送信）が既に state を初期化している。
      if (isAbort(err)) return;
      setState((s) => ({ ...s, status: 'error' }));
    },
  });

  const submit = useCallback(
    (question: string, attributeFilters?: AskRequestAttributeFilters) =>
      ask.mutate({ question, attributeFilters }),
    [ask],
  );

  return { ...state, submit };
}

/** FR-08: 👍 / 👎 の評価値。 */
export type FeedbackRating = 'up' | 'down';

/**
 * FR-08: 回答へのフィードバック送信。
 *
 * 押下は**楽観的に**反映し、送信に失敗したら取り消す（押したのに何も起きない状態を残さない）。
 *
 * IADR-0135 決定 1（#519）: 送信は **orval 生成フック**（`useBffSubmitFeedback`）で行う。
 * **本文の SSE（`apiStream`）は載せ替えない**——orval は SSE を扱えず、生成物に該当の関数が
 * 存在しない（IADR-0131 決定 4。恒久的に対象外）。
 */
export function useFeedback() {
  const [rating, setRating] = useState<FeedbackRating | null>(null);
  const [failed, setFailed] = useState(false);

  const mutation = useBffSubmitFeedback<unknown>({
    mutation: {
      onError: () => {
        setRating(null);
        setFailed(true);
      },
    },
  });

  const send = useCallback(
    (answerId: string, next: FeedbackRating, question: string) => {
      setRating(next);
      setFailed(false);
      mutation.mutate({ data: { answerId, rating: next, question } });
    },
    [mutation],
  );

  /** 新しい回答が始まったら、前の回答に対する押下状態を捨てる。 */
  const reset = useCallback(() => {
    setRating(null);
    setFailed(false);
  }, []);

  return { rating, failed, send, reset };
}
