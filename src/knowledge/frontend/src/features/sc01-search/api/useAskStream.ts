import { useCallback, useRef, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { apiStream } from '@foundation/api/apiClient';
import type { AskRequestAttributeFilters } from '@foundation/api/generated/bff.schemas';
import { useBffSubmitFeedback } from '@foundation/api/generated/feedback/feedback';
import type { AskCitation } from '@foundation/ai-chat/citations';

// SC-01, UC-01, FR-04/FR-08: AI 回答（SSE）とフィードバック送信の状態管理。
//
// IADR-0126 決定 1: **回答は TanStack Query のキャッシュに載せない。** 同じ質問でも回答は毎回生成され
// `answerId` が変わるため、キャッシュに載せると戻る操作や再マウントで**古い回答が古い出典つきで復活する**。
// 出典は「いま表示している回答の根拠」であり、これは単なる古さではなく誤った根拠の提示になる。
// 発火（送信）は `useMutation` が持ち、増えていく途中経過（token 列）だけをローカル state に蓄積する。
//
// ［2026-09-12 / UI/UX 改善 A-8］停止・再生成・履歴
//   - `cancel()` は**停止**である。`AbortController.abort()` で中断し、その時点の部分回答を `stopped: true`
//     で残す（画面が「（停止）」を付ける）。中断は失敗ではない（`isAbort`）。
//   - `regenerate()` は直前の入力（質問 ＋ 対象範囲）を ref に保持し、同じ内容で再送する。
//   - `history` は直近 N 件（`HISTORY_LIMIT`）の確定した Q&A である。**`aiChatStore`（右レール）は流用しない**
//     ——あれは画面キー（pathname）ごとの会話をシェルが持つもので、この画面の回答は IADR-0126 決定 1 の
//     とおり画面のローカル state に閉じる（離脱で消える。古い出典つきの回答を別の場所へ持ち出さない）。

/** 回答の進行状態。`idle` は未送信、`error` は縮退（UC-01 例外フロー）の入口である。 */
export type AnswerStatus = 'idle' | 'streaming' | 'done' | 'error';

export interface AnswerState {
  status: AnswerStatus;
  /** 送った質問。 */
  question: string;
  /** SSE の `token` を到着順に連結した本文。 */
  answer: string;
  /** SSE の `citations`（本文より先に届く）。 */
  citations: AskCitation[];
  /** SSE の `done` で確定する回答 ID。フィードバック（FR-08）の紐付け先。 */
  answerId: string | null;
  /** 利用者が停止した回答か（`done` を待たずに `status: 'done'` へ入る）。 */
  stopped: boolean;
}

/** 確定した 1 往復（履歴の要素）。 */
export interface AskTurn {
  id: string;
  question: string;
  answer: string;
  citations: AskCitation[];
  answerId: string | null;
  stopped: boolean;
}

/** 履歴に保つ件数。計画は件数を定めておらず、画面 1 枚に収まる目安で 10 とする。 */
export const HISTORY_LIMIT = 10;

const INITIAL: AnswerState = {
  status: 'idle',
  question: '',
  answer: '',
  citations: [],
  answerId: null,
  stopped: false,
};

interface DonePayload {
  answerId: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
}

/** 呼び出し側の意図的な中断（停止・連投・離脱）は失敗ではない。 */
function isAbort(err: unknown): boolean {
  return err instanceof DOMException && err.name === 'AbortError';
}

/** SC-01, #539: 質問と、任意の対象範囲（絞り込み）。 */
interface AskInput {
  question: string;
  attributeFilters?: AskRequestAttributeFilters;
}

let turnSeq = 0;
function nextTurnId(): string {
  turnSeq += 1;
  return `ask-${turnSeq}`;
}

export function useAskStream() {
  const [state, setState] = useState<AnswerState>(INITIAL);
  const [history, setHistory] = useState<AskTurn[]>([]);
  /** 停止時に読むための写し（`cancel` は描画の外から state を読めない）。 */
  const stateRef = useRef<AnswerState>(INITIAL);
  const abortRef = useRef<AbortController | null>(null);
  const lastInputRef = useRef<AskInput | null>(null);

  const update = useCallback((fn: (s: AnswerState) => AnswerState) => {
    stateRef.current = fn(stateRef.current);
    setState(stateRef.current);
  }, []);

  /** 確定した回答を履歴の先頭へ積む（直近が先。上限を超えた古いものは落とす）。 */
  const remember = useCallback((s: AnswerState, stopped: boolean) => {
    if (!s.answer) return;
    setHistory((h) =>
      [
        {
          id: nextTurnId(),
          question: s.question,
          answer: s.answer,
          citations: s.citations,
          answerId: s.answerId,
          stopped,
        },
        ...h,
      ].slice(0, HISTORY_LIMIT),
    );
  }, []);

  const { mutate } = useMutation({
    mutationFn: async ({ question, attributeFilters }: AskInput) => {
      // 直前のストリームを中断する（連投で 2 本のストリームが同じ state へ書き込むのを防ぐ）。
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;
      lastInputRef.current = { question, attributeFilters };
      update(() => ({ ...INITIAL, status: 'streaming', question }));

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
            update((s) => ({ ...s, citations: parsed.citations ?? [] }));
          } else if (ev.event === 'token') {
            const parsed = JSON.parse(ev.data) as { text?: string };
            update((s) => ({ ...s, answer: s.answer + (parsed.text ?? '') }));
          } else if (ev.event === 'done') {
            const parsed = JSON.parse(ev.data) as DonePayload;
            update((s) => ({ ...s, status: 'done', answerId: parsed.answerId }));
          } else if (ev.event === 'error') {
            // UC-01 例外フロー: LLM が不調な場合。画面は検索結果一覧への導線へ縮退する。
            update((s) => ({ ...s, status: 'error' }));
          }
        },
        controller.signal,
      );
      return controller;
    },
    onSuccess: (controller) => {
      // 停止と同時に上流が閉じた場合。停止側（`cancel`）が既に片付けている。
      if (controller.signal.aborted) return;
      if (abortRef.current === controller) abortRef.current = null;
      // `done` が来ないまま上流が閉じた場合も、進行中のまま固まらせない。
      update((s) => (s.status === 'streaming' ? { ...s, status: 'done' } : s));
      if (stateRef.current.status === 'done') remember(stateRef.current, false);
    },
    onError: (err) => {
      // 中断は失敗ではない。中断した側（停止・新しい送信）が既に state を片付けている。
      if (isAbort(err)) return;
      abortRef.current = null;
      update((s) => ({ ...s, status: 'error' }));
    },
  });

  const submit = useCallback(
    (question: string, attributeFilters?: AskRequestAttributeFilters) =>
      mutate({ question, attributeFilters }),
    [mutate],
  );

  /** 停止。走っているストリームを止め、その時点の部分回答を `stopped` として残す。 */
  const cancel = useCallback(() => {
    const controller = abortRef.current;
    abortRef.current = null;
    if (!controller) return;
    controller.abort();
    if (stateRef.current.status !== 'streaming') return;
    update((s) => ({ ...s, status: 'done', stopped: true }));
    remember(stateRef.current, true);
  }, [remember, update]);

  /** 直前の入力（質問 ＋ 対象範囲）を同じ内容で再送する。直前が無ければ何もしない。 */
  const regenerate = useCallback(() => {
    const last = lastInputRef.current;
    if (last) mutate(last);
  }, [mutate]);

  return { ...state, history, submit, cancel, regenerate };
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
