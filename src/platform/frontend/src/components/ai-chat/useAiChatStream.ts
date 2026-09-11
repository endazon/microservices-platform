import { useCallback, useRef, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { apiStream } from '@foundation/api/apiClient';
import { useAiChatStore } from './aiChatStore';
import type { AskCitation } from './citations';

// 05_screens §共通シェル（右レール AI チャットパネル）/ IADR-0121 決定 5（#788 で実装）:
// **「自前フック ＋ TanStack Query は確定済み履歴のみ」**。
//
// ■ 決定 5 の再評価（同決定が「第 4 段の着手時に確認する」と申し送った事項。#788 で実施）
//   再評価条件は「`streamedQuery` が experimental を外し、**かつ**任意の非同期イテレータを
//   素直に受けられるようになったとき」。実測（2026-08-23 / @tanstack/react-query 5.101.4・
//   最新 5.102.0）では `index.d.ts` の export が `experimental_streamedQuery` のままである。
//   **条件 (1) が満たされていないため、決定 5 を維持する。**
//
// ■ 決定 5 と IADR-0126 決定 1 の食い違いの扱い（同決定が「第 4 段で判断する」と申し送った事項）
//   決定 5 は「完了時に `setQueryData` / `invalidateQueries` で Query へ引き渡す」、
//   IADR-0126 決定 1 は「回答を Query のキャッシュに載せない」である。
//   **右レールでは引き渡し先が存在しない** —— `docs/api/openapi.yaml` の `/bff/` 配下に
//   会話履歴の取得口が無い（実測 2026-08-23）。よって確定した往復は**クライアント状態**
//   （`aiChatStore`）へ積む。これは決定 5 の否定ではなく**適用条件の不成立**であり、
//   履歴の口が契約に入った時点で決定 5 の引き渡しが有効になる。
//
// ■ SSE の口は `apiStream` である（IADR-0131 決定 4）。`EventSource` は Authorization を付けられず、
//   orval は SSE を生成できない。したがって画面からも `apiStream` が恒久的な正規の口である。
//
// ■ ［2026-09-12 / UI/UX 改善 A-8］停止・再生成・出典
//   - `cancel()` は **停止**である。`AbortController.abort()` で中断し、その時点の部分回答を
//     `stopped: true` で履歴へ残す（表示側が「（停止）」を付ける）。中断は失敗ではない（`isAbort`）。
//   - `regenerate()` は直前の質問（ref に保持）を同じ内容で再送する。失敗（縮退）からの復帰と、
//     停止した回答の取り直しに使う。
//   - `citations` イベントを購読し、往復に紐付けて履歴へ積む（SC-01 の `useAskStream` と同型）。

/** 回答の進行状態。`idle` は未送信、`error` は縮退の入口。 */
export type AiChatStatus = 'idle' | 'streaming' | 'done' | 'error';

interface DonePayload {
  answerId?: string;
}

/** 呼び出し側の意図的な中断（停止・連投・パネルを閉じる）は失敗ではない。 */
function isAbort(err: unknown): boolean {
  return err instanceof DOMException && err.name === 'AbortError';
}

/** 確定した往復の一意キー。`answerId` は返らないことがあるので独立に採番する。 */
let turnSeq = 0;
function nextTurnId(): string {
  turnSeq += 1;
  return `turn-${turnSeq}`;
}

/** 1 本のストリームの途中経過。state（描画用）と ref（停止時に読む用）の両方へ同じ値を書く。 */
interface Progress {
  question: string;
  answer: string;
  citations: AskCitation[];
}

const NO_PROGRESS: Progress = { question: '', answer: '', citations: [] };

/**
 * 右レールの 1 画面ぶんのストリーミング。
 *
 * @param screenKey 履歴を分ける単位（ルートの pathname）。
 */
export function useAiChatStream(screenKey: string) {
  const [status, setStatus] = useState<AiChatStatus>('idle');
  /** ストリーム中の途中経過。**確定するまでストアへは入れない**（履歴に半端な回答を残さない）。 */
  const [progress, setProgress] = useState<Progress>(NO_PROGRESS);
  const progressRef = useRef<Progress>(NO_PROGRESS);
  const abortRef = useRef<AbortController | null>(null);
  /** 直前に送った質問（再生成用）。 */
  const lastQuestionRef = useRef<string | null>(null);
  const [lastQuestion, setLastQuestion] = useState<string | null>(null);
  const appendTurn = useAiChatStore((s) => s.appendTurn);

  const update = useCallback((next: Progress) => {
    progressRef.current = next;
    setProgress(next);
  }, []);

  const { mutate } = useMutation({
    mutationFn: async (question: string) => {
      // 連投で 2 本のストリームが同じ state を奪い合わないよう、直前を中断する。
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;
      lastQuestionRef.current = question;
      setLastQuestion(question);
      setStatus('streaming');
      update({ question, answer: '', citations: [] });

      let answerId: string | null = null;
      let failed = false;

      await apiStream(
        '/analysis/ask/stream',
        // FR-05: クライアントは ABAC スコープを送らない（送っても BFF は使わない＝権限昇格の防止）。
        { json: { question } },
        (ev) => {
          if (ev.event === 'citations') {
            const parsed = JSON.parse(ev.data) as { citations?: AskCitation[] };
            update({ ...progressRef.current, citations: parsed.citations ?? [] });
          } else if (ev.event === 'token') {
            const parsed = JSON.parse(ev.data) as { text?: string };
            update({
              ...progressRef.current,
              answer: progressRef.current.answer + (parsed.text ?? ''),
            });
          } else if (ev.event === 'done') {
            const parsed = JSON.parse(ev.data) as DonePayload;
            answerId = parsed.answerId ?? null;
          } else if (ev.event === 'error') {
            // UC-01 例外フロー: LLM が不調な場合。パネルは縮退表示へ落ちる。
            failed = true;
          }
        },
        controller.signal,
      );

      return { controller, answerId, failed };
    },
    onSuccess: ({ controller, answerId, failed }) => {
      // 停止と同時に上流が閉じた場合。停止側（`cancel`）が既に履歴へ積んでいるので二重に積まない。
      if (controller.signal.aborted) return;
      // 終わったストリームを `cancel()` が「走っている」と誤認しないよう手放す。
      if (abortRef.current === controller) abortRef.current = null;
      if (failed) {
        setStatus('error');
        return;
      }
      const { question, answer, citations } = progressRef.current;
      setStatus('done');
      update(NO_PROGRESS);
      // **空の回答は履歴へ積まない**（上流が何も返さないまま閉じた場合。空の吹き出しを残さない）。
      if (answer) {
        appendTurn(screenKey, {
          id: nextTurnId(),
          question,
          answer,
          answerId,
          citations,
          stopped: false,
        });
      }
    },
    onError: (err) => {
      // 中断は失敗ではない。中断した側（停止・新しい送信）が既に state を片付けている。
      if (isAbort(err)) return;
      abortRef.current = null;
      setStatus('error');
    },
  });

  const submit = useCallback((question: string) => mutate(question), [mutate]);

  /**
   * 停止。走っているストリームを止め、**その時点の部分回答を履歴に残す**（`stopped: true`）。
   * パネルを閉じる・離脱するときも同じ経路を通る（途中まで読んだ回答を捨てない）。
   */
  const cancel = useCallback(() => {
    const controller = abortRef.current;
    abortRef.current = null;
    controller?.abort();
    const { question, answer, citations } = progressRef.current;
    if (controller && answer) {
      appendTurn(screenKey, {
        id: nextTurnId(),
        question,
        answer,
        answerId: null,
        citations,
        stopped: true,
      });
    }
    setStatus('idle');
    update(NO_PROGRESS);
  }, [appendTurn, screenKey, update]);

  /** 直前の質問を同じ内容で再送する。直前が無ければ何もしない。 */
  const regenerate = useCallback(() => {
    const question = lastQuestionRef.current;
    if (question) mutate(question);
  }, [mutate]);

  return {
    status,
    draft: progress.answer,
    pendingQuestion: progress.question,
    citations: progress.citations,
    /** 直前に送った質問。`regenerate()` が使える目安（null なら不可）。 */
    lastQuestion,
    submit,
    cancel,
    regenerate,
  };
}
