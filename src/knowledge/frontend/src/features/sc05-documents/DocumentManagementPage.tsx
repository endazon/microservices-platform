import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import {
  Alert,
  Button,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import { toMessages } from '@foundation/ui/apiErrors';
import { CONFIDENTIALITY_KEY } from '../abac/confidentiality';
import { DocumentForm } from './DocumentForm';
import type { DocumentFormValues } from './DocumentForm';
import { useAdminDocuments, useDocumentActions } from './useDocumentAdmin';
import type { AdminDocument, DocumentCommand } from './useDocumentAdmin';

// SC-05, UC-03, FR-06/FR-09: 文書管理画面（05_screens: ルート /admin/documents）。
// 正規化文書の一覧・登録・編集（属性／タグ設定）を行う。詳細と版履歴は SC-03（/docs/$id）が持つ
// （05_screens §SC-05「版ごとの履歴パネルは SC-03 側に置く。本画面は一覧の版列で現行版を示す」）。
//
// 実装しない要素（画面仕様書 docs/screens/SC-05_document-management.md §hi-fi モックアップとの対応）:
//   - **「変換」列**: `DocumentDto` に変換の情報が無く（`Status` は公開ライフサイクル）、
//     `ConversionJobDto` からの結合も**失敗ジョブが `DocumentId` を持たない**ため原理的にできない
//     ——「✕ 失敗」を決して表示できない列になる。変換状況は SC-07（/admin/conversions）が担う。
//   - **タグ辞書からの補完**: 辞書は /bff/admin/authz（システム管理者限定）にあり、本画面の
//     利用者（admin / operator）が引ける保証が無い。
//   いずれも feedback/20260805_sc05-07-admin-contract-gaps.md に環流の記録を作成した。
//   機密区分の**値**を訳さない理由は abac/confidentiality.ts を参照（planning#197 で裁定待ち）。

/** 未公開状態のみ公開できる（アーカイブ済みの誤再公開を防ぐ。サーバも 409 で拒否する）。 */
function canPublish(status: string): boolean {
  return status === 'draft' || status === 'normalized';
}

/**
 * 409 の詳細（Problem 本文の errors / detail / title 由来）。
 *
 * `toMessages` を使わないのは、同関数が詳細の無い `ApiError` に対して**汎用の `message`**
 * （「競合が発生しました。」）を返してしまい、版競合であることが読み取れないためである。
 * 詳細があればそれを、無ければ呼び出し側の平易な文言を出す。
 */
function conflictDetails(error: unknown): string | null {
  return error instanceof ApiError && error.details.length > 0 ? error.details.join(' ') : null;
}

export function DocumentManagementPage() {
  const { t } = useLingui();
  const [editing, setEditing] = useState<AdminDocument | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const documents = useAdminDocuments();
  const actions = useDocumentActions();
  const { create, update, command } = actions;

  const items = documents.data ?? [];
  // IADR-0127 決定 7: 画面は**直近の操作の結果だけ**を出す。列挙は `useDocumentActions()` の
  // 戻り値から導く——手書きの配列にすると、4 本目のミューテーションを足したときに
  // 「一覧には出るが読まれない失敗」「消し忘れる古い失敗」が静かに生まれる。
  const mutations = Object.values(actions);
  const failed = mutations.find((m) => m.isError);
  const conflicted = failed?.error instanceof ApiError && failed.error.status === 409;

  /**
   * 新しい操作を始める前に、前回の結果（成功メッセージと各ミューテーションの失敗状態）を捨てる。
   *
   * TanStack Query は「**別の**ミューテーションが成功した」ことでは他方の `isError` を戻さない。
   * これが無いと「削除が 409 で失敗 → 別文書の保存が成功」で成功バナーと古い失敗バナーが並び、
   * どの操作の結果なのかが読めなくなる（IADR-0127 決定 7）。
   */
  function beginOperation() {
    setNotice(null);
    for (const mutation of mutations) mutation.reset();
  }

  function save(values: DocumentFormValues) {
    beginOperation();
    if (editing) {
      update.mutate(
        { id: editing.id, expectedVersion: editing.version, ...values },
        {
          onSuccess: () => {
            setEditing(null);
            setNotice(t`文書を更新しました。`);
          },
        },
      );
      return;
    }
    // 新規登録に変更メモは無い（版スナップショットの説明であり、初版には対象の変更が無い）。
    create.mutate(
      { title: values.title, attributes: values.attributes, tags: values.tags },
      { onSuccess: () => setNotice(t`文書を登録しました。`) },
    );
  }

  function run(id: string, kind: DocumentCommand, message: string) {
    beginOperation();
    command.mutate({ id, kind }, { onSuccess: () => setNotice(message) });
  }

  return (
    <section className="flex flex-col gap-4 lg:flex-row">
      <div className="min-w-0 grow">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
          <h1 className="text-lg font-semibold text-[--color-fg]">
            <Trans>文書一覧</Trans>
          </h1>
          <Button
            type="button"
            variant="primary"
            onClick={() => {
              beginOperation();
              setEditing(null);
            }}
          >
            <Trans>＋ 新規登録</Trans>
          </Button>
        </div>

        {notice && (
          <Alert tone="success" role="status" className="mb-2" label={t`完了`}>
            {notice}
          </Alert>
        )}
        {failed && (
          // INDEX 決定 21: 色（tone）だけで深刻度を伝えない。**ラベルの文言も tone に揃える**
          // ——琥珀のアイコンに「エラー」と書かれていると、色を除いたときに区別が消える。
          <Alert
            tone={conflicted ? 'warning' : 'danger'}
            role="alert"
            className="mb-2"
            label={conflicted ? t`注意` : t`エラー`}
          >
            {conflicted
              ? conflictDetails(failed.error) ??
                t`他の更新と競合しました（版が変わっています）。最新を再読み込みしてください。`
              : toMessages(failed.error, t`操作を実行できませんでした。`).join(' / ')}
          </Alert>
        )}

        {documents.isPending && (
          <p role="status" className="text-sm text-[--color-fg-muted]">
            <Trans>読み込み中…</Trans>
          </p>
        )}
        {documents.isError && (
          <Alert tone="danger" role="alert" label={t`エラー`}>
            {toMessages(documents.error, t`文書を取得できませんでした。`).join(' / ')}
          </Alert>
        )}

        {documents.isSuccess &&
          (items.length === 0 ? (
            <p className="text-sm">
              <Trans>文書はありません。</Trans>
            </p>
          ) : (
            <Table>
              <TableCaption>
                <Trans>文書の一覧</Trans>
              </TableCaption>
              <TableHead>
                <TableRow>
                  <TableHeaderCell>
                    <Trans>タイトル</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>機密区分</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>版</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>操作</Trans>
                  </TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {items.map((doc) => (
                  <DocumentRow
                    key={doc.id}
                    doc={doc}
                    busy={command.isPending}
                    onEdit={() => {
                      beginOperation();
                      setEditing(doc);
                    }}
                    onCommand={run}
                  />
                ))}
              </TableBody>
            </Table>
          ))}
      </div>

      <div className="w-full lg:max-w-md">
        {/* 編集対象が変わったらフォームを作り直す（前の文書の入力値を持ち越さない）。 */}
        <DocumentForm
          key={editing?.id ?? 'new'}
          editing={editing}
          submitting={create.isPending || update.isPending}
          onSubmit={save}
          onCancel={() => setEditing(null)}
        />
      </div>
    </section>
  );
}

function DocumentRow({
  doc,
  busy,
  onEdit,
  onCommand,
}: {
  doc: AdminDocument;
  busy: boolean;
  onEdit: () => void;
  onCommand: (id: string, kind: DocumentCommand, message: string) => void;
}) {
  const { t } = useLingui();
  const confidentiality = doc.attributes?.[CONFIDENTIALITY_KEY];

  return (
    <TableRow>
      <TableCell>
        {/* 計画の遷移図 SC05 → SC03（詳細・版履歴）。 */}
        <Link
          to="/docs/$id"
          params={{ id: doc.id }}
          className="font-medium text-[--color-brand] hover:underline"
        >
          {doc.title}
        </Link>
      </TableCell>
      <TableCell>{confidentiality ? <Tag tone="neutral">{confidentiality}</Tag> : '—'}</TableCell>
      <TableCell>v{doc.version}</TableCell>
      <TableCell>
        <span className="flex flex-wrap gap-2">
          <Button type="button" size="sm" onClick={onEdit}>
            <Trans>編集</Trans>
          </Button>
          {canPublish(doc.status) && (
            <Button
              type="button"
              size="sm"
              disabled={busy}
              onClick={() => onCommand(doc.id, 'publish', t`文書を公開しました。`)}
            >
              <Trans>公開</Trans>
            </Button>
          )}
          {doc.status !== 'archived' && (
            <Button
              type="button"
              size="sm"
              disabled={busy}
              onClick={() => onCommand(doc.id, 'archive', t`文書をアーカイブしました。`)}
            >
              <Trans>アーカイブ</Trans>
            </Button>
          )}
          <Button
            type="button"
            size="sm"
            variant="danger"
            disabled={busy}
            onClick={() => onCommand(doc.id, 'delete', t`文書を削除しました。`)}
          >
            <Trans>削除</Trans>
          </Button>
        </span>
      </TableCell>
    </TableRow>
  );
}
