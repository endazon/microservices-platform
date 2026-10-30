import { Trans, useLingui } from '@lingui/react/macro';
import { Alert, Button } from '@platform/ui';
import { useSuggestionGenerate } from '../api/useDocumentSuggestions';

// SC-03, FR-18, ADR-0081 決定 1・2, [[IADR-0653]] 決定 4 (#2106): **AI 提案の生成を起動する要素。**
//
// ■ 押せるのは**その文書を読める利用者**（決定 2）。判定は後段が持ち、画面は権限を判定しない。
// ■ 🔴 **失敗は 404 を含めて中立の文言にする** —— 「見えない」と「無い」を区別しない（存在秘匿。ADR-0034 決定 2）。
// ■ 🔴 **0 件のときの置き場所は暫定である**（planning#857 の裁定待ち。推奨の案 (a)）。0 件のときは承認欄の見出し・枠を出さず
//   本部品だけを置き、1 件以上のときは欄の中に置く（`AiSuggestionPanel`）。**配置の判断は本部品と呼び出し側の 1 か所に閉じる** ——
//   裁定が異なれば配置だけを差し替える。
export function SuggestionGenerateControl({ documentId }: { documentId: string }) {
  const { t } = useLingui();
  const generate = useSuggestionGenerate(documentId);
  // 応答の型は状態コードの和である。配列（200）のときだけ「作れた件数」として読む。
  const created =
    generate.isSuccess && Array.isArray(generate.data.data) ? generate.data.data : null;

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="secondary"
          size="sm"
          disabled={generate.isPending}
          onClick={() => {
            generate.reset();
            generate.mutate({ documentId });
          }}
        >
          <Trans>AI に提案を作らせる</Trans>
        </Button>
        <span className="text-xs text-fg-muted">
          <Trans>
            AI
            がこの文書のリンク候補・タグ候補を作ります（あなたが閲覧できる文書の範囲で作ります。AI
            を呼び出します）。
          </Trans>
        </span>
      </div>
      {generate.isPending && (
        <p role="status" className="text-xs text-fg-muted">
          <Trans>提案を作っています…</Trans>
        </p>
      )}
      {created !== null && created.length === 0 && (
        <p role="status" className="text-xs text-fg-muted">
          <Trans>新しい提案はありませんでした。</Trans>
        </p>
      )}
      {generate.isError && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          <Trans>提案を作れませんでした。時間をおいて再度お試しください。</Trans>
        </Alert>
      )}
    </div>
  );
}
