import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import { RotateCcw } from 'lucide-react';
import { Alert, Button, Card, CardContent, CardHeader, CardTitle, Tag } from '@platform/ui';
import type { AiSuggestion } from '@foundation/api/generated/bff.schemas';
import {
  useDocumentSuggestions,
  useEdgeTypeNames,
  useSuggestionActions,
} from '../api/useDocumentSuggestions';

// SC-03, UC-10, FR-18, ADR-0033 決定 7・9・10: **AI 提案の承認欄**（#450）。
//
// ■ 05_screens §SC-03 の逐語が本欄の要件である。
//   - 当該文書を**両端のいずれかとする提案**（リンク候補・タグ候補）を本文の下部に表示する
//   - 🔴 **提案が 0 件のときは欄自体を表示しない**（見出しだけが残ると、承認すべきものが
//     あるかのように読める）
//   - 各提案には**種類・相手の文書またはタグ・辺の型・提案の根拠**を示す
//   - 既定で表示するのは `pending` の提案である
//   - 本欄から SC-21（AI 提案一覧）への導線を置く
//
// ■ 🔴 **一括承認・一括却下を置かない**（FR-18・§SC-21「描いてはいけないもの」）。
//   一覧の 1 行に収まる情報では承認を判断できず、タイトルだけを見た機械的な承認に落ちるためである。
//   **本画面はまさに「文脈のある側」であり、判断はここで 1 件ずつ行う。**
//
// ■ 🔴 **タグ提案は承認できないものとして描く**（[[IADR-0300]] 決定 4）。
//   後段はタグ提案を承認しても状態を `approved` にするだけで、**文書のタグは 1 つも増えない**
//   （反映経路が未実装である）。押せる承認ボタンを置くと「承認したのにタグが付かない」という
//   偽の作用を約束することになる。**却下は完全に効く**ので押せるままにする ——
//   隠してしまうと、SC-21 の全行が持つ「文書詳細で確認」の導線がタグ提案について行き止まりになる。
//   計画（「その場で承認／却下できる」）との差異であり、計画へ環流する。
//
// ■ 状態は**色だけで意味を持たせない**（文言とアイコンを併用する）。

/**
 * 却下からの再提示に添える**固定文言**（05_screens §SC-21・ADR-0033 決定 10）。
 *
 * 🔴 **理由を示さない再提示を起こさない。** 理由がないと利用者は「却下したのにまた出てきた」と
 * 受け取り、承認作業そのものへの信頼を失う。SC-21 と同じ文言を用いる。
 */
function ReinstatedNotice() {
  return (
    <p className="mt-1 flex items-start gap-1 text-xs text-[--color-fg-muted]">
      <RotateCcw className="mt-0.5 size-3.5 shrink-0" aria-hidden />
      <Trans>この提案は一度却下されましたが、文書が更新されたため再度提示しています。</Trans>
    </p>
  );
}

export function AiSuggestionPanel({ documentId }: { documentId: string }) {
  const { t } = useLingui();
  const { items, isPending, isError } = useDocumentSuggestions(documentId);
  const edgeTypeNames = useEdgeTypeNames();
  const { approve, reject } = useSuggestionActions();

  // 読み込み中は欄ごと出さない（0 件のときと同じ見え方にする。見出しだけが先に出ない）。
  if (isPending) return null;

  // 🔴 **「提案が無い」へ縮退しない。** 引けないことと 0 件は利用者にとって別の意味である
  // （SC-21 の一覧と同じ判断）。ただし本欄は文書詳細の従属要素なので、本体表示は妨げない。
  if (isError) {
    return (
      <Alert tone="warning" role="status" label={t`AI 提案`} className="mb-3">
        <Trans>AI 提案を取得できませんでした。提案の有無は判断できません。</Trans>
      </Alert>
    );
  }

  // 05_screens §SC-03: **0 件なら欄自体を出さない。**
  if (items.length === 0) return null;

  const busy = approve.isPending || reject.isPending;

  return (
    <Card className="mb-3">
      <CardHeader>
        <CardTitle as="h2">
          <Trans>AI 提案</Trans>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <p className="mb-2 text-xs text-[--color-fg-muted]">
          <Trans>
            この文書に対するリンク候補・タグ候補です。両端の文書の内容を確かめたうえで、 1
            件ずつ承認または却下してください。
          </Trans>
        </p>

        <ul className="flex flex-col gap-3">
          {items.map((s) => (
            <SuggestionRow
              key={s.id}
              suggestion={s}
              edgeTypeName={(s.edgeTypeId && edgeTypeNames.get(s.edgeTypeId)) || t`型不明`}
              busy={busy}
              onApprove={() => approve.mutate({ id: s.id })}
              onReject={() => reject.mutate({ id: s.id })}
            />
          ))}
        </ul>

        {(approve.isError || reject.isError) && (
          <Alert tone="danger" role="alert" label={t`エラー`} className="mt-3">
            <Trans>
              操作できませんでした。すでに他の利用者が承認・却下した可能性があります。
              画面を再読み込みして確認してください。
            </Trans>
          </Alert>
        )}

        {/* 05_screens §SC-03: 本欄から SC-21（棚卸しの一覧）への導線を置く。 */}
        <p className="mt-3 text-sm">
          {/* SC-21 は URL が絞り込みの単一情報源であり、検索パラメータが必須である。
           **棚卸しの既定（承認待ち・種類はすべて）で開く** —— 本欄から渡す絞りは無い。 */}
          <Link
            to="/ai-suggestions"
            search={{ state: 'pending', kind: 'all' }}
            className="text-[--color-brand] hover:underline"
          >
            <Trans>AI 提案の一覧を見る</Trans>
          </Link>
        </p>
      </CardContent>
    </Card>
  );
}

function SuggestionRow({
  suggestion,
  edgeTypeName,
  busy,
  onApprove,
  onReject,
}: {
  suggestion: AiSuggestion;
  edgeTypeName: string;
  busy: boolean;
  onApprove: () => void;
  onReject: () => void;
}) {
  const { t } = useLingui();
  const isTag = suggestion.kind === 'tag';
  // `lingui/no-expression-in-message`: 翻訳単位へ渡せるのは単一の変数だけである
  // （プロパティ参照・`??` はカタログの ID を壊す）。SC-03 の本体が既に採っている作法に揃える。
  const tagValue = suggestion.tagValue ?? '';
  const sourceTitle = suggestion.sourceDocumentTitle;
  const targetTitle = suggestion.targetDocumentTitle ?? '';

  return (
    <li className="rounded-[--radius-control] border border-[--color-border] p-3">
      <div className="mb-1 flex flex-wrap items-center gap-2">
        {/* 種類は分類であって状態ではないので Tag を使う（StatusBadge は状態の部品である）。 */}
        <Tag tone="neutral">{isTag ? t`タグ` : t`リンク`}</Tag>
        <span className="text-sm">
          {isTag ? (
            <Trans>「{tagValue}」を付与</Trans>
          ) : (
            <Trans>
              {sourceTitle} → {targetTitle}（{edgeTypeName}）
            </Trans>
          )}
        </span>
      </div>

      {/* 05_screens §SC-03 / §SC-21 主要素 6: **提案の根拠**（なぜ関連と判断したか）。 */}
      <p className="text-xs text-[--color-fg-muted]">{suggestion.rationale}</p>
      {suggestion.reinstatedReason ? <ReinstatedNotice /> : null}

      <div className="mt-2 flex flex-wrap items-center gap-2">
        <Button
          variant="primary"
          size="sm"
          disabled={busy || isTag}
          onClick={onApprove}
          // 🔴 押せない理由を**必ず添える**。無効なボタンだけを置くと理由が読めない。
          title={isTag ? t`タグ提案の反映経路が未実装のため、承認は実行できません。` : undefined}
        >
          <Trans>承認</Trans>
        </Button>
        <Button variant="secondary" size="sm" disabled={busy} onClick={onReject}>
          <Trans>却下</Trans>
        </Button>
        {isTag && (
          <span className="text-xs text-[--color-fg-muted]">
            <Trans>
              タグ提案の反映経路が未実装のため、承認は実行できません。却下は実行できます。
            </Trans>
          </span>
        )}
      </div>
    </li>
  );
}
