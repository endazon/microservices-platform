import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import { ChevronDown, ChevronRight, RotateCcw, Square } from 'lucide-react';
import { Alert, Button, Input, Label, Note, Panel, Spinner, TooltipProvider } from '@platform/ui';
import { CitationList } from '@foundation/ai-chat/CitationList';
import { CopyButton } from '@foundation/ai-chat/CopyButton';
import { MarkdownAnswer } from '@foundation/ai-chat/MarkdownAnswer';
import { citationKind } from '@foundation/ai-chat/citations';
import type { AskCitation } from '@foundation/ai-chat/citations';
import { HISTORY_LIMIT, useAskStream, useFeedback } from '../api/useAskStream';
import type { AskTurn } from '../api/useAskStream';
import { EMPTY_SELECTION, ScopeFilter, toAttributeFilters } from '../../../lib/scope-filter';
import type { ScopeSelection } from '../../../lib/scope-filter';
// SC-01, UC-07, #1200 / IADR-0365 決定 1: 出典が Wiki 由来かは**権限内の Wiki 台帳**で判定する
// （実行時 config `wikiBaseUrl` の接頭辞判定は廃止。stg/prod では同値が供給されないため一度も真にならなかった）。
import { useWikiPageIndex } from '../../../lib/wiki-pages';

// SC-01, UC-01, FR-03/FR-04/FR-05/FR-08: 検索／チャット質問画面（本システムの主入口。ルート /ask）。
// 1 つの入力から根拠付き AI 回答（真の SSE ストリーミング・出典併記）を得る。
// キーワード検索のみが欲しい場合は同じ語を SC-02 へ渡す（UC-01 代替フロー）。
//
// ［2026-09-12 / UI/UX 改善 A-8］hi-fi モック `sc-01.html`（`.ttl` / `.sub` / 入力行 / 対象範囲行 /
// `.panel` AI回答 / `.panel` 出典 / `.note`）の構造へ合わせ、Markdown 描画・停止・再生成・コピー・
// 出典の対応印（脚注方式。裁定 6）・直近 N 件の履歴を足した（第 4 弾「体験の穴」(3)）。
//
// 実装しない要素（画面仕様書 docs/screens/SC-01_search-chat.md §hi-fi モックアップとの対応）:
//   - 個人資料まわり（「個人資料を含める」トグル・出典行の 👤）: FR-19 / FR-21 であり
//     IADR-0119 決定 1 が着手を保留している（**繰り延べであって放棄ではない**）。出典リストの
//     語彙（`CitationKind.personal`）は foundation 側に用意してあり、契約に印が入れば判定を足すだけでよい。
//
// **［2026-08-10 訂正 / #553］従前ここには「対象範囲フィルタ（タグ／フォルダ）は実装しない。
// planning#197 で裁定待ち」と書いてあった。この記述は実装と食い違っていた** ——
// 裁定（2026-08-05 Q1・Q2・Q3・Q9）が着地し、**#539（`attributeFilters`）と #540
// （`POST /bff/attribute-values`）で口が揃い、本画面は下で `<ScopeFilter>` を描いている。**
// 軸はタグ・部門・プロジェクトで、**「フォルダ」は保留ではなく不採用である**（Q9）。

/** 質問の最大長。計画（05_screens §SC-01）は「最大長制限」とだけ書き値を定めていない（画面仕様書 §未決事項 3）。 */
const MAX_QUESTION_LENGTH = 1000;

/** 進行中の回答の対応印の接頭辞（履歴の各往復は `turn.id` を使い、衝突しない）。 */
const CURRENT_CITE_PREFIX = 'ask-current';

export function SearchChatPage() {
  const { t } = useLingui();
  const [question, setQuestion] = useState('');
  // FR-04, SC-01, #539: 対象範囲の選択。**送信ごとに保つ**（連続で質問するとき選び直させない）。
  const [scope, setScope] = useState<ScopeSelection>(EMPTY_SELECTION);
  const answer = useAskStream();
  const feedback = useFeedback();

  const trimmed = question.trim();
  const streaming = answer.status === 'streaming';
  const canSubmit = trimmed.length > 0 && !streaming;

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    // 新しい回答が始まるため、前の回答に対する押下状態を捨てる（FR-08）。
    feedback.reset();
    // FR-04, #539: 対象範囲を添えて送る。**選択が無ければ `undefined` で載らない**
    // （旧来の要求と同じ形になる）。**絞り込みは narrowing-only** でサーバ側が ABAC と交差させる。
    answer.submit(trimmed, toAttributeFilters(scope));
  }

  return (
    <TooltipProvider>
      <section>
        <h1 className="text-[17px] font-medium text-fg">
          <Trans>ナレッジ検索・AI質問</Trans>
        </h1>
        <p className="mb-n3 text-xs text-fg-muted">
          <Trans>横断検索と根拠付きAI回答（ストリーミング・出典表示）</Trans>
        </p>

        <form onSubmit={onSubmit} className="mb-n3 flex items-end gap-n2">
          <div className="flex-1">
            {/* 入力の意味は見出しとプレースホルダで示す（モック準拠）。ラベルは支援技術のために必ず置く。 */}
            <Label htmlFor="ask-question" className="sr-only">
              <Trans>質問・キーワード</Trans>
            </Label>
            <Input
              id="ask-question"
              value={question}
              maxLength={MAX_QUESTION_LENGTH}
              onChange={(e) => setQuestion(e.target.value)}
              placeholder={t`質問またはキーワードを入力…（例: 経費精算の締め日は？）`}
            />
          </div>
          <Button type="submit" variant="primary" disabled={!canSubmit}>
            <Trans>送信</Trans>
          </Button>
        </form>

        {/* 対象範囲行（モック `.row`）: 左に対象範囲フィルタ、右端にキーワード検索のみの導線。
            SC-01: 対象範囲フィルタ（タグ／部門／プロジェクト）。**候補は権限内に限る**（#540 の口）。
            SC-08 と同じ部品を使う——同じ操作が画面ごとに違うと利用者は覚え直すことになる（裁定 Q3）。 */}
        <div className="mb-n4 flex flex-wrap items-start gap-n3">
          <div className="min-w-0 flex-1">
            <ScopeFilter selection={scope} onChange={setScope} disabled={streaming} />
          </div>
          {/* UC-01 代替フロー: キーワード検索のみで結果一覧を返し、AI 回答を省略する。 */}
          <Link
            to="/search"
            search={{ q: trimmed }}
            className="text-xs text-accent hover:underline"
          >
            <Trans>キーワード検索のみ →</Trans>
          </Link>
        </div>

        {answer.status !== 'idle' && (
          <Panel heading={<Trans>AI回答（ストリーミング）</Trans>}>
            <MarkdownAnswer
              markdown={answer.answer}
              citePrefix={CURRENT_CITE_PREFIX}
              citationCount={answer.citations.length}
            />
            {/* 停止した回答は完成品ではない。注記を文字で付ける。 */}
            {answer.stopped && (
              <p className="mt-n1 text-xs text-fg-muted">
                <Trans>（停止）</Trans>
              </p>
            )}

            {/* INDEX 決定 21: 進行中はアイコン ＋ テキスト。停止は `AbortController` で中断し部分回答を残す。 */}
            {streaming && (
              <div className="mt-n2 flex items-center gap-n2 text-xs text-fg-muted">
                <Spinner size="sm" label={t`回答を生成中…`} />
                <span aria-hidden>
                  <Trans>回答を生成中…</Trans>
                </span>
                <Button type="button" size="sm" onClick={answer.cancel}>
                  <Square className="size-3" aria-hidden />
                  <Trans>停止</Trans>
                </Button>
              </div>
            )}

            {/* UC-01 例外フロー: LLM が不調な場合は検索結果のみを返す（縮退運転）。
                本画面は AI 回答だけを担うため、縮退は「検索結果一覧へ 1 クリックで到達させる」形で満たす
                （**唯一「次の手」を出すエラー**。再生成とは別に維持する）。 */}
            {answer.status === 'error' && (
              <Alert tone="warning" role="alert" className="mt-n3" label={t`注意`}>
                <Trans>AI 回答を生成できませんでした。キーワード検索の結果をご確認ください。</Trans>{' '}
                <Link to="/search" search={{ q: trimmed }} className="text-accent underline">
                  <Trans>検索結果一覧を開く →</Trans>
                </Link>
              </Alert>
            )}

            {!streaming && (
              <div className="mt-n3 flex flex-wrap items-center gap-n2">
                <Button type="button" variant="ghost" size="sm" onClick={answer.regenerate}>
                  <RotateCcw className="size-3" aria-hidden />
                  <Trans>再生成</Trans>
                </Button>
                {answer.answer && <CopyButton text={answer.answer} />}
                {answer.status === 'done' && answer.answerId && (
                  <>
                    <Button
                      type="button"
                      size="sm"
                      aria-pressed={feedback.rating === 'up'}
                      aria-label={t`役に立った`}
                      onClick={() => feedback.send(answer.answerId!, 'up', answer.question)}
                    >
                      👍
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      aria-pressed={feedback.rating === 'down'}
                      aria-label={t`役に立たなかった`}
                      onClick={() => feedback.send(answer.answerId!, 'down', answer.question)}
                    >
                      👎
                    </Button>
                    <span className="text-xs text-fg-muted">
                      {feedback.rating ? (
                        <Trans>フィードバックを送信しました。</Trans>
                      ) : (
                        <Trans>フィードバック</Trans>
                      )}
                    </span>
                  </>
                )}
              </div>
            )}
            {feedback.failed && (
              <Alert tone="danger" role="alert" className="mt-n2" label={t`エラー`}>
                <Trans>フィードバックを送信できませんでした。</Trans>
              </Alert>
            )}
          </Panel>
        )}

        {answer.citations.length > 0 && (
          <Panel heading={<Trans>出典（クリックで文書詳細／Wikiへ）</Trans>}>
            <Sc01Citations citations={answer.citations} citePrefix={CURRENT_CITE_PREFIX} />
            <Note>
              <Trans>
                組織文書と個人資料はアイコン＋ラベルで区別する（色だけで意味を持たせない）。
              </Trans>
            </Note>
          </Panel>
        )}

        {answer.history.length > 0 && <History turns={answer.history} />}

        <p className="text-xs">
          <Link to="/analyze" className="text-accent hover:underline">
            <Trans>範囲を指定してAI分析を依頼 →</Trans>
          </Link>
        </p>
        <Note>
          <Trans>
            LLM不調時は検索結果のみ返す縮退運転（UC-01
            例外フロー）。フィルタ候補は権限内のタグ／部門／プロジェクトのみ提示。
          </Trans>
        </Note>
      </section>
    </TooltipProvider>
  );
}

/**
 * 出典の一覧（SC-01 の行き先つき）。
 *
 * 台帳（`GET /bff/wiki/pages`）は**出典が現れてから 1 回だけ**引く（この部品は出典が 0 件なら
 * 描画されない）。問う前に Wiki の口を叩かない —— SC-01 のスモークが「初期表示は身元と通知以外を
 * 叩かない」を固定している。
 *
 * Wiki 由来の出典は SC-04 の**文書別ディープリンク**（`/wiki?doc=<documentId>`）へ、それ以外は
 * SC-03（`/docs/:id`）へ送る（#1200）。種別のアイコン ＋ ラベルは foundation の `CitationList` が描く。
 */
function Sc01Citations({
  citations,
  citePrefix,
}: {
  citations: readonly AskCitation[];
  citePrefix: string;
}) {
  const wiki = useWikiPageIndex();
  return (
    <CitationList
      citations={citations}
      citePrefix={citePrefix}
      kindOf={(c) => citationKind(c.documentId, wiki.documentIds)}
      renderTitle={(c) =>
        citationKind(c.documentId, wiki.documentIds) === 'wiki' ? (
          <Link to="/wiki" search={{ doc: c.documentId }} className="text-accent hover:underline">
            {c.documentTitle}
          </Link>
        ) : (
          <Link
            to="/docs/$id"
            params={{ id: c.documentId }}
            className="text-accent hover:underline"
          >
            {c.documentTitle}
          </Link>
        )
      }
    />
  );
}

/**
 * 直近 N 件の履歴。**畳んでいる間は回答本文を DOM に置かない**——古い回答が古い出典つきで画面に
 * 残り続けるのを避ける（IADR-0126 決定 1 の趣旨。開いたときだけ描く）。
 */
function History({ turns }: { turns: readonly AskTurn[] }) {
  const limit = HISTORY_LIMIT;
  return (
    <Panel heading={<Trans>履歴（直近{limit}件）</Trans>}>
      <ol className="flex flex-col gap-n2">
        {turns.map((turn) => (
          <HistoryItem key={turn.id} turn={turn} />
        ))}
      </ol>
    </Panel>
  );
}

function HistoryItem({ turn }: { turn: AskTurn }) {
  const [open, setOpen] = useState(false);
  const Chevron = open ? ChevronDown : ChevronRight;
  return (
    <li className="rounded-md bg-surface-muted px-n3 py-n2">
      <Button
        type="button"
        variant="ghost"
        size="sm"
        aria-expanded={open}
        className="h-auto w-full justify-start px-0 py-1 text-left font-normal text-fg"
        onClick={() => setOpen((v) => !v)}
      >
        <Chevron className="size-4 shrink-0" aria-hidden />
        <span className="truncate">{turn.question}</span>
      </Button>
      {open && (
        <div className="mt-n2 flex flex-col gap-n2">
          <MarkdownAnswer
            markdown={turn.answer}
            citePrefix={turn.id}
            citationCount={turn.citations.length}
          />
          {turn.stopped && (
            <p className="text-xs text-fg-muted">
              <Trans>（停止）</Trans>
            </p>
          )}
          {turn.citations.length > 0 && (
            <Sc01Citations citations={turn.citations} citePrefix={turn.id} />
          )}
          <div className="flex justify-end">
            <CopyButton text={turn.answer} />
          </div>
        </div>
      )}
    </li>
  );
}
