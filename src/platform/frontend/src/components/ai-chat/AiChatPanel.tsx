import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { useRouterState } from '@tanstack/react-router';
import { Bot, Loader2, X } from 'lucide-react';
import { Alert, Button, Textarea } from '@platform/ui';
import { useAiChatStore } from './aiChatStore';
import { useAiChatStream } from './useAiChatStream';

// 05_screens §共通シェル: 「**AIチャットパネル（右レール）**」。IADR-0121 決定 1 の第 4 段（#788）。
//
// ■ 本段で実装する範囲
//   計画が挙げる要素のうち、**画面別履歴（画面ごとの保持／全消去）**と SSE のストリーミングを実装する。
//
// ■ 実装しない要素と、その理由（**動かない設定 UI を置かない**）
//   計画は「モデル選択・フォールバックモデル（セルフホスト LLM）・データ越境設定・
//   画面コンテキスト添付の ON/OFF・回答の詳しさ」も挙げるが、`/bff/analysis/ask/stream` の
//   要求本文は `question` と `attributeFilters` だけであり（`docs/api/openapi.yaml`。実測 2026-08-23）、
//   **これらを送る先が契約に無い**。置くと「操作できるのに何も変わらない」設定になる。
//   契約が追いついた時点で足す。差異は #788 の作業仕様書 §計画書との差異 に記録した。
//
// ■ 画面キーはルートの pathname である。計画の「画面ごとの保持」は画面の単位で分けることを指し、
//   SPA でその単位を表すのはルートだからである。

export function AiChatPanel() {
  const { t } = useLingui();
  const open = useAiChatStore((s) => s.open);
  const togglePanel = useAiChatStore((s) => s.togglePanel);

  return (
    <>
      <Button
        type="button"
        size="sm"
        aria-expanded={open}
        onClick={togglePanel}
        className="fixed bottom-4 right-4 z-40 shadow"
      >
        <Bot className="mr-1 size-4" aria-hidden />
        {open ? <Trans>AI チャットを閉じる</Trans> : <Trans>AI チャットを開く</Trans>}
      </Button>
      {open && <AiChatRail closeLabel={t`閉じる`} />}
    </>
  );
}

/**
 * 右レール本体。**開いているときだけマウントする**——閉じているときにストリームやストアの購読を
 * 残すと、使っていないパネルが画面遷移のたびに再描画される。
 */
function AiChatRail({ closeLabel }: { closeLabel: string }) {
  const { t } = useLingui();
  const screenKey = useRouterState({ select: (s) => s.location.pathname });
  const history = useAiChatStore((s) => s.historyByScreen[screenKey]);
  const clearScreen = useAiChatStore((s) => s.clearScreen);
  const clearAll = useAiChatStore((s) => s.clearAll);
  const closePanel = useAiChatStore((s) => s.closePanel);
  const { status, draft, pendingQuestion, submit, cancel } = useAiChatStream(screenKey);
  const [question, setQuestion] = useState('');

  const turns = history ?? [];
  const busy = status === 'streaming';
  const canSend = question.trim().length > 0 && !busy;

  return (
    <aside
      aria-label={t`AI チャットパネル`}
      className="fixed bottom-0 right-0 top-0 z-30 flex w-80 flex-col border-l border-[--color-border] bg-[--color-surface] p-3"
    >
      <div className="mb-2 flex items-center justify-between">
        <h2 className="text-sm font-semibold text-[--color-fg]">
          <Trans>AI チャット</Trans>
        </h2>
        <Button
          type="button"
          size="sm"
          aria-label={closeLabel}
          onClick={() => {
            cancel();
            closePanel();
          }}
        >
          <X className="size-4" aria-hidden />
        </Button>
      </div>

      {/* 05_screens §共通シェル:「画面ごとの保持／全消去」。**この画面ぶん**と**全体**を別の操作にする
          ——全消去しか無いと、1 画面の会話を捨てるために他の画面の履歴まで巻き添えになる。 */}
      <div className="mb-2 flex gap-2">
        <Button
          type="button"
          size="sm"
          disabled={turns.length === 0}
          onClick={() => clearScreen(screenKey)}
        >
          <Trans>この画面の履歴を消去</Trans>
        </Button>
        <Button type="button" size="sm" onClick={() => clearAll()}>
          <Trans>全消去</Trans>
        </Button>
      </div>

      <ol className="mb-2 grow overflow-y-auto text-sm" aria-label={t`会話履歴`}>
        {turns.length === 0 && !busy && (
          <li className="text-[--color-fg-muted]">
            <Trans>この画面の会話履歴はまだありません。</Trans>
          </li>
        )}
        {turns.map((turn) => (
          <li key={turn.id} className="mb-3">
            <p className="font-medium text-[--color-fg]">{turn.question}</p>
            <p className="whitespace-pre-wrap text-[--color-fg-muted]">{turn.answer}</p>
          </li>
        ))}
        {busy && (
          <li className="mb-3">
            <p className="font-medium text-[--color-fg]">{pendingQuestion}</p>
            <p className="whitespace-pre-wrap text-[--color-fg-muted]">{draft}</p>
          </li>
        )}
      </ol>

      {/* INDEX 決定 21「色だけで意味を持たせない」: 進行中はアイコン ＋ テキストで示す
          （回転するだけの印や色の変化に意味を持たせない）。 */}
      {busy && (
        <p role="status" className="mb-2 flex items-center gap-1 text-sm text-[--color-fg-muted]">
          <Loader2 className="size-4 animate-spin" aria-hidden />
          <Trans>回答を生成中…</Trans>
        </p>
      )}

      {/* UC-01 例外フロー: LLM が不調なときは縮退する。Alert は tone ＋ アイコン ＋ ラベル必須の API
          であり、色だけで意味を持たない（IADR-0125 決定 1）。 */}
      {status === 'error' && (
        <Alert tone="danger" role="alert" className="mb-2" label={t`エラー`}>
          <Trans>回答を生成できませんでした。時間をおいて再度お試しください。</Trans>
        </Alert>
      )}

      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (!canSend) return;
          submit(question.trim());
          setQuestion('');
        }}
      >
        <Textarea
          rows={2}
          value={question}
          aria-label={t`質問`}
          placeholder={t`この画面について質問する`}
          onChange={(e) => setQuestion(e.target.value)}
        />
        <Button type="submit" variant="primary" size="sm" className="mt-2" disabled={!canSend}>
          <Trans>送信</Trans>
        </Button>
      </form>
    </aside>
  );
}
