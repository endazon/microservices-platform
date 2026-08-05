import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import { Alert, Button, Card, CardContent, CardHeader, CardTitle, Input, Label, Tag } from '@platform/ui';
import { appConfig } from '@foundation/config/runtimeConfig';
import { citationKind } from './citations';
import type { AskCitation } from './citations';
import { useAskStream, useFeedback } from './useAskStream';

// SC-01, UC-01, FR-03/FR-04/FR-05/FR-08: 検索／チャット質問画面（本システムの主入口。ルート /ask）。
// 1 つの入力から根拠付き AI 回答（真の SSE ストリーミング・出典併記）を得る。
// キーワード検索のみが欲しい場合は同じ語を SC-02 へ渡す（UC-01 代替フロー）。
//
// 実装しない要素（画面仕様書 docs/screens/SC-01_search-chat.md §hi-fi モックアップとの対応）:
//   - 対象範囲フィルタ（タグ／フォルダ）: BFF に載せる先が無く、権限内候補を得る API も無い
//     （feedback/20260804_sc01-03-bff-contract-gaps.md に環流の記録あり。計画リポへの起票は未了）。
//   - 個人資料まわり（「個人資料を含める」トグル・出典行の 👤）: FR-19 / FR-21 であり
//     IADR-0119 決定 1 が着手を保留している（**繰り延べであって放棄ではない**）。

/** 質問の最大長。計画（05_screens §SC-01）は「最大長制限」とだけ書き値を定めていない（画面仕様書 §未決事項 3）。 */
const MAX_QUESTION_LENGTH = 1000;

export function SearchChatPage() {
  const { t } = useLingui();
  const [question, setQuestion] = useState('');
  const answer = useAskStream();
  const feedback = useFeedback();

  const trimmed = question.trim();
  const canSubmit = trimmed.length > 0 && answer.status !== 'streaming';

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSubmit) return;
    // 新しい回答が始まるため、前の回答に対する押下状態を捨てる（FR-08）。
    feedback.reset();
    answer.submit(trimmed);
  }

  return (
    <section>
      <h1 className="text-lg font-semibold text-[--color-fg]">
        <Trans>ナレッジ検索・AI質問</Trans>
      </h1>
      <p className="mb-4 text-sm text-[--color-fg-muted]">
        <Trans>横断検索と根拠付きAI回答（ストリーミング・出典表示）</Trans>
      </p>

      <form onSubmit={onSubmit} className="mb-2 flex items-end gap-2">
        <div className="grow">
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

      {/* UC-01 代替フロー: キーワード検索のみで結果一覧を返し、AI 回答を省略する。 */}
      <p className="mb-4 text-sm">
        <Link to="/search" search={{ q: trimmed }} className="text-[--color-brand] hover:underline">
          <Trans>キーワード検索のみ →</Trans>
        </Link>
      </p>

      {answer.status !== 'idle' && (
        <Card className="mb-3">
          <CardHeader>
            <CardTitle>
              <Trans>AI回答（ストリーミング）</Trans>
            </CardTitle>
            {answer.status === 'streaming' && (
              <span role="status" className="text-xs text-[--color-fg-muted]">
                <Trans>回答を生成中…</Trans>
              </span>
            )}
          </CardHeader>
          <CardContent>
            <p className="whitespace-pre-wrap">{answer.answer}</p>

            {/* UC-01 例外フロー: LLM が不調な場合は検索結果のみを返す（縮退運転）。
                本画面は AI 回答だけを担うため、縮退は「検索結果一覧へ 1 クリックで到達させる」形で満たす。 */}
            {answer.status === 'error' && (
              <Alert tone="warning" role="alert" className="mt-3" label={t`注意`}>
                <Trans>AI 回答を生成できませんでした。キーワード検索の結果をご確認ください。</Trans>{' '}
                <Link to="/search" search={{ q: trimmed }} className="text-[--color-brand] underline">
                  <Trans>検索結果一覧を開く →</Trans>
                </Link>
              </Alert>
            )}

            {answer.status === 'done' && answer.answerId && (
              <div className="mt-3 flex items-center gap-2">
                <Button
                  type="button"
                  size="sm"
                  aria-pressed={feedback.rating === 'up'}
                  aria-label={t`役に立った`}
                  onClick={() => feedback.send(answer.answerId!, 'up', trimmed)}
                >
                  👍
                </Button>
                <Button
                  type="button"
                  size="sm"
                  aria-pressed={feedback.rating === 'down'}
                  aria-label={t`役に立たなかった`}
                  onClick={() => feedback.send(answer.answerId!, 'down', trimmed)}
                >
                  👎
                </Button>
                <span className="text-xs text-[--color-fg-muted]">
                  {feedback.rating ? (
                    <Trans>フィードバックを送信しました。</Trans>
                  ) : (
                    <Trans>フィードバック</Trans>
                  )}
                </span>
              </div>
            )}
            {feedback.failed && (
              <Alert tone="danger" role="alert" className="mt-2" label={t`エラー`}>
                <Trans>フィードバックを送信できませんでした。</Trans>
              </Alert>
            )}
          </CardContent>
        </Card>
      )}

      {answer.citations.length > 0 && (
        <Card className="mb-3">
          <CardHeader>
            <CardTitle>
              <Trans>出典（クリックで文書詳細／Wikiへ）</Trans>
            </CardTitle>
          </CardHeader>
          <CardContent>
            <ul className="flex flex-col gap-1.5">
              {answer.citations.map((c) => (
                <CitationRow key={c.chunkId} citation={c} />
              ))}
            </ul>
          </CardContent>
        </Card>
      )}

      <p className="text-sm">
        <Link to="/analyze" className="text-[--color-brand] hover:underline">
          <Trans>範囲を指定してAI分析を依頼 →</Trans>
        </Link>
      </p>
    </section>
  );
}

/**
 * 出典 1 行。
 *
 * 05_screens §SC-01「区別の表示方法」: **アイコンとラベルを併用し、色だけで意味を持たせない**
 * （INDEX 決定 21）。記号は装飾（`aria-hidden`）とし、意味はタグの文字が担う。
 * 計画が glyph（📄 / 📖 / 👤）を字義どおり指定しているため、ここは lucide-react ではなく計画の記号を使う。
 * **👤（個人資料）の行は本 issue では作らない**——FR-19 / FR-21 は IADR-0119 決定 1 の着手保留対象である。
 */
function CitationRow({ citation }: { citation: AskCitation }) {
  const { t } = useLingui();
  const kind = citationKind(citation.sourceUri, appConfig().wikiBaseUrl);
  return (
    <li className="flex flex-wrap items-center gap-2 text-sm">
      <span aria-hidden>{kind === 'wiki' ? '📖' : '📄'}</span>
      {kind === 'wiki' ? (
        <Link to="/wiki" className="text-[--color-brand] hover:underline">
          {citation.documentTitle}
        </Link>
      ) : (
        <Link
          to="/docs/$id"
          params={{ id: citation.documentId }}
          className="text-[--color-brand] hover:underline"
        >
          {citation.documentTitle}
        </Link>
      )}
      <Tag tone="outline">{t`組織文書`}</Tag>
      <span className="text-xs text-[--color-fg-muted]">{citation.snippet}</span>
    </li>
  );
}
