import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import { RotateCcw } from 'lucide-react';
import { Alert, Button, Card, CardContent, CardHeader, CardTitle, Tag } from '@platform/ui';
import type { AiSuggestion } from '@foundation/api/generated/bff.schemas';
import { ApiError } from '@foundation/api/ApiError';
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
// ■ 🔴 **タグ提案の行は `canDecide` で 2 つに分けて描く**（ADR-0063 決定 3〜5 / [[IADR-0364]] 決定 5。
//   #1187。[[IADR-0300]] 決定 4「承認だけを実行不可にする」は反映経路の実装をもって失効した）。
//   後段はタグ提案の承認を**文書のタグへ反映してから** `approved` にする（承認者本人の資格で書く）。
//   - **資格を持つ**（起点文書への write **または** 管理者ロール）: 承認・却下とも押せる。
//     「準備中」「未実装」の文言は**存在しない**。
//   - **資格を持たない**: 承認・却下とも押せず、「この文書のタグを編集する権限がありません。」を
//     画面上のテキストとして出す（恒久。却下も塞ぐのは決定 4「承認と却下は同じ権限に従う」）。
//   資格の判定はサーバ側（一覧の各行が運ぶ）。**画面は辞書もポリシーも引かない。**
//   隠さないのは従来どおり —— SC-21 の全行が持つ「文書詳細で確認」の導線を行き止まりにしない。
//
// ■ 🔴 **辞書に無い値の提案は承認できず、却下だけができる**（決定 2 後段）。承認が 400 `unknown_tag`
//   で返ったときは、汎用の「操作できませんでした」ではなく**その事実を読める文言**で出す。
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
  // #1104: 無効化の対象は**この文書の**一覧である（絞りをサーバへ移してキーが分かれた）。
  const { approve, reject } = useSuggestionActions(documentId);

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
  // ADR-0063 決定 2 後段: 辞書に無い値は承認できず却下のみ。後段は 400 `unknown_tag` を本文ごと透過する。
  const unknownTag = isUnknownTagError(approve.error);

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

        {unknownTag ? (
          <Alert tone="danger" role="alert" label={t`エラー`} className="mt-3">
            <Trans>このタグは辞書に無いため反映できません。却下してください。</Trans>
          </Alert>
        ) : (
          (approve.isError || reject.isError) && (
            <Alert tone="danger" role="alert" label={t`エラー`} className="mt-3">
              <Trans>
                操作できませんでした。すでに他の利用者が承認・却下した可能性があります。
                画面を再読み込みして確認してください。
              </Trans>
            </Alert>
          )
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
  // ADR-0063 決定 5 / IADR-0364 決定 5: **タグ提案の行だけ**資格で表示を分ける（リンク提案の行は
  // 従来どおり押せる。拒否は 404 → 汎用エラー）。値はサーバが行ごとに判定して運ぶ。
  // **旧版の後段は載せない**ので、欠けていれば deny 側（権限が無い）に倒す。
  const forbidden = isTag && !(suggestion.canDecide ?? false);
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
        {/* 承認と却下は同じ権限に従う（ADR-0063 決定 4）。資格が無ければ両方を塞ぐ。 */}
        <Button variant="primary" size="sm" disabled={busy || forbidden} onClick={onApprove}>
          <Trans>承認</Trans>
        </Button>
        <Button variant="secondary" size="sm" disabled={busy || forbidden} onClick={onReject}>
          <Trans>却下</Trans>
        </Button>
        {forbidden && (
          // 🔴 押せない理由を**画面上のテキストとして**出す（無効なボタンだけを置くと理由が読めない）。
          <span className="text-xs text-[--color-fg-muted]">
            <Trans>この文書のタグを編集する権限がありません。</Trans>
          </span>
        )}
      </div>
    </li>
  );
}

/**
 * 承認が **400 `unknown_tag`**（提案の値が SC-09 のタグ辞書に無い）で拒まれたか。
 *
 * 後段は本文 `{ error: "unknown_tag" }` を透過する（`unknown_edge_type` と同じ形）。`ApiError.body` は
 * 解析済みの本文であり、**状態コードだけで判定しない** —— 400 は検証エラー一般の器である。
 */
function isUnknownTagError(error: unknown): boolean {
  if (!(error instanceof ApiError) || error.status !== 400) return false;
  const body = error.body as { error?: unknown } | undefined;
  return body?.error === 'unknown_tag';
}
