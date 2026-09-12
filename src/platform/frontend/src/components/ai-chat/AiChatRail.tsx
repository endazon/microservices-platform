import { useRef, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { useRouterState } from '@tanstack/react-router';
import { ArrowDown, Bot, Eraser, RotateCcw, Send, Square, Trash2, X } from 'lucide-react';
import { Alert, Button, Spinner, Textarea, TooltipProvider } from '@platform/ui';
import { useAiChatStore } from './aiChatStore';
import type { AiChatTurn } from './aiChatStore';
import { CitationList } from './CitationList';
import { CopyButton } from './CopyButton';
import { IconButton } from './IconButton';
import { MarkdownAnswer } from './MarkdownAnswer';
import { useAiChatStream } from './useAiChatStream';
import { useStickToBottom } from './useStickToBottom';
import type { AskCitation } from './citations';

// 05_screens §共通シェル（右レール AI チャットパネル）/ SC-01 / UC-01 / IADR-0121 決定 5 / IADR-0439:
// 右レール**本体**。`AiChatPanel`（ランチャー）から `React.lazy` で読み込む**遅延チャンクの入口**である。
//
// 🔴 **`@platform/ui` の Tooltip 系を静的 import してよいのは、この遅延側のファイルだけである**
//    （IADR-0443）。`AiChatPanel.tsx`（＝初期チャンク）へ書き戻すと、`ui` チャンク経由ではなく
//    **エントリが直接 `vendor-baseui` を引く**辺が復活し、Base UI 114 kB が初期ロードへ戻る。
//    `AiChatPanel.test.tsx` の「閉じているときは本体を読み込まない」検査がこれを固定している。
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
//   モックの `.rr-set`（モデル・越境のチップ）と `.rr-cfg`（AI 設定）も同じ理由で描かない。
//   モックの `.rr-note`「画面コンテキストを自動添付。履歴は画面単位で保存・復元」も、**添付も復元も
//   契約に無い**ので書かない（履歴はメモリ上の Zustand であり、リロードで消える——そのとおりに書く）。
//
// ■ 画面キーはルートの pathname である。計画の「画面ごとの保持」は画面の単位で分けることを指し、
//   SPA でその単位を表すのはルートだからである。

/**
 * 遅延チャンクの公開面。**`TooltipProvider`（Tooltip の根）はここに置く** ——
 * 複数のツールチップで遅延を共有するため根は 1 つでよく、レールの外（初期側）へ出す理由が無い。
 */
export function AiChatRail() {
  return (
    <TooltipProvider>
      <AiChatRailContent />
    </TooltipProvider>
  );
}

/**
 * 右レールの中身。**開いているときだけマウントする**——閉じているときにストリームやストアの購読を
 * 残すと、使っていないパネルが画面遷移のたびに再描画される。
 */
function AiChatRailContent() {
  const { t } = useLingui();
  const screenKey = useRouterState({ select: (s) => s.location.pathname });
  const history = useAiChatStore((s) => s.historyByScreen[screenKey]);
  const clearScreen = useAiChatStore((s) => s.clearScreen);
  const clearAll = useAiChatStore((s) => s.clearAll);
  const closePanel = useAiChatStore((s) => s.closePanel);
  const { status, draft, pendingQuestion, citations, lastQuestion, submit, cancel, regenerate } =
    useAiChatStream(screenKey);
  const [question, setQuestion] = useState('');
  const listRef = useRef<HTMLOListElement>(null);
  const { stuck, scrollToBottom } = useStickToBottom(listRef);

  const turns = history ?? [];
  const turnCount = turns.length;
  const busy = status === 'streaming';
  const canSend = question.trim().length > 0 && !busy;

  return (
    <aside
      aria-label={t`AI チャットパネル`}
      className="flex h-full min-h-0 flex-col border-l border-divider bg-surface-muted text-xs"
    >
      {/* .rr-h */}
      <div className="flex items-center gap-n2 border-b border-divider px-n3 py-n2">
        <Bot className="size-4 shrink-0 text-accent" aria-hidden />
        <h2 className="text-xs font-medium text-fg">
          <Trans>AIチャット</Trans>
        </h2>
        <span className="flex-1" />
        {/* 05_screens §共通シェル:「画面ごとの保持／全消去」。**この画面ぶん**と**全体**を別の操作にする
            ——全消去しか無いと、1 画面の会話を捨てるために他の画面の履歴まで巻き添えになる。 */}
        <IconButton
          label={t`この画面の履歴を消去`}
          icon={Eraser}
          disabled={turns.length === 0}
          onClick={() => clearScreen(screenKey)}
        />
        <IconButton label={t`全消去`} icon={Trash2} onClick={() => clearAll()} />
        <IconButton
          label={t`AI チャットを閉じる`}
          icon={X}
          onClick={() => {
            cancel();
            closePanel();
          }}
        />
      </div>

      {/* .rr-hist */}
      <p className="border-b border-divider px-n3 py-1 text-[11px] text-fg-muted">
        <Trans>
          履歴（{screenKey}） {turnCount}件
        </Trans>
      </p>

      {/* .rr-msgs ＋ 追従スクロール。遡っている間は「最新へ」で復帰できる。 */}
      <div className="relative min-h-0 flex-1">
        <ol
          ref={listRef}
          className="flex h-full flex-col gap-n2 overflow-y-auto p-n3"
          aria-label={t`会話履歴`}
        >
          {turns.length === 0 && !busy && (
            <li className="text-fg-muted">
              <Trans>この画面の会話履歴はまだありません。</Trans>
            </li>
          )}
          {turns.map((turn) => (
            <TurnItem key={turn.id} turn={turn} />
          ))}
          {busy && (
            <li className="flex flex-col gap-n1">
              <UserBubble>{pendingQuestion}</UserBubble>
              <AnswerBubble answer={draft} citePrefix="pending" citations={citations} />
            </li>
          )}
        </ol>
        {!stuck && (
          <Button
            type="button"
            size="sm"
            className="absolute bottom-n2 left-1/2 -translate-x-1/2 bg-surface shadow-md"
            onClick={scrollToBottom}
          >
            <ArrowDown className="size-4" aria-hidden />
            <Trans>最新へ</Trans>
          </Button>
        )}
      </div>

      {/* INDEX 決定 21「色だけで意味を持たせない」: 進行中はアイコン ＋ テキストで示す
          （回転するだけの印や色の変化に意味を持たせない）。停止は `AbortController` で中断し、
          そこまでの部分回答を履歴へ残す（`useAiChatStream.cancel`）。 */}
      {busy && (
        <div className="flex items-center gap-n2 px-n3 py-n1 text-fg-muted">
          <Spinner size="sm" label={t`回答を生成中…`} />
          <span aria-hidden>
            <Trans>回答を生成中…</Trans>
          </span>
          <span className="flex-1" />
          <Button type="button" size="sm" onClick={cancel}>
            <Square className="size-3" aria-hidden />
            <Trans>停止</Trans>
          </Button>
        </div>
      )}

      {/* UC-01 例外フロー: LLM が不調なときは縮退する。Alert は tone ＋ アイコン ＋ ラベル必須の API
          であり、色だけで意味を持たない（IADR-0125 決定 1）。 */}
      {status === 'error' && (
        <Alert tone="danger" role="alert" className="mx-n3 my-n2" label={t`エラー`}>
          <Trans>回答を生成できませんでした。時間をおいて再度お試しください。</Trans>
        </Alert>
      )}

      {/* 再生成: 直前の質問を同じ内容で再送する（失敗からの復帰・停止した回答の取り直し）。 */}
      {!busy && lastQuestion !== null && (
        <div className="flex justify-end px-n3 pb-n1">
          <Button type="button" variant="ghost" size="sm" onClick={regenerate}>
            <RotateCcw className="size-3" aria-hidden />
            <Trans>再生成</Trans>
          </Button>
        </div>
      )}

      {/* .rr-in */}
      <form
        className="flex items-end gap-n2 border-t border-divider px-n3 py-n2"
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
          className="text-xs"
          onChange={(e) => setQuestion(e.target.value)}
        />
        <Button type="submit" variant="primary" size="sm" disabled={!canSend}>
          <Send className="size-3" aria-hidden />
          <Trans>送信</Trans>
        </Button>
      </form>

      {/* .rr-note: 契約に在ることだけを書く（画面コンテキストの添付も履歴の復元も契約に無い）。 */}
      <p className="px-n3 pb-n2 text-[10px] text-fg-muted">
        <Trans>履歴は画面単位。リロードで消えます</Trans>
      </p>
    </aside>
  );
}

/** 確定した 1 往復（モック `.rr-m.u` ＋ `.rr-m.a`）。 */
function TurnItem({ turn }: { turn: AiChatTurn }) {
  return (
    <li className="flex flex-col gap-n1">
      <UserBubble>{turn.question}</UserBubble>
      <AnswerBubble
        answer={turn.answer}
        citePrefix={turn.id}
        citations={turn.citations}
        stopped={turn.stopped}
      />
    </li>
  );
}

function UserBubble({ children }: { children: string }) {
  return (
    <p className="max-w-[94%] self-end whitespace-pre-wrap rounded-md bg-accent-soft px-2 py-1.5 leading-relaxed text-fg">
      {children}
    </p>
  );
}

function AnswerBubble({
  answer,
  citePrefix,
  citations,
  stopped = false,
}: {
  answer: string;
  citePrefix: string;
  citations: readonly AskCitation[];
  stopped?: boolean;
}) {
  return (
    <div className="flex max-w-[94%] flex-col gap-n1 rounded-md bg-surface px-2 py-1.5">
      <MarkdownAnswer
        markdown={answer}
        citePrefix={citePrefix}
        citationCount={citations.length}
        className="text-xs"
      />
      {/* 停止した回答は完成品ではない。注記を文字で付ける（色や省略記号だけにしない）。 */}
      {stopped && (
        <p className="text-fg-muted">
          <Trans>（停止）</Trans>
        </p>
      )}
      {citations.length > 0 && (
        <CitationList citations={citations} citePrefix={citePrefix} className="text-[11px]" />
      )}
      {answer && (
        <div className="flex justify-end">
          <CopyButton text={answer} />
        </div>
      )}
    </div>
  );
}
