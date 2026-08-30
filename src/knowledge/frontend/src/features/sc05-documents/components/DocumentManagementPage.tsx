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
import { PlatformRole, useHasAnyRole } from '@foundation/auth/roles';
import { toMessages } from '@foundation/ui/apiErrors';
import { CONFIDENTIALITY_KEY } from '../../../lib/abac';
import { DocumentForm } from './DocumentForm';
import type { DocumentFormValues } from './DocumentForm';
import { useAdminDocuments, useDocumentActions } from '../api/useDocumentAdmin';
import type { DocumentCommand } from '../api/useDocumentAdmin';
import { useTagOptions } from '../api/useTagOptions';
// SC-05, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type { DocumentDto } from '@foundation/api/generated/bff.schemas';

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
//   いずれも feedback/20260805_sc05-07-admin-contract-gaps.md に記録し、planning#198 として起票した。
//   **［2026-08-10 追記 / #553］2 件とも裁定で決着している。**
//     - **「変換」列 → 裁定 Q17 で計画側が「変換状況」を削除した**（01_screens.md:276）。
//       契約を足すのではなく**要素そのものが落ちた**ので、**出していないのが正しい**。
//     - **タグ辞書 → 裁定 Q18 で照会口を管理系ロールへ開くと確定し、#634 / #640 で実装済み**。
//       **本画面が補完へ載せる作業は未了**であり、そこが残りである。
//   機密区分の**値**を訳さない理由は abac/confidentiality.ts を参照。
//   **［2026-08-10 追記 / #553］裁定は着地している** —— 4 値の表示名は 2026-08-05 の裁定
//   （Q7・Q8・派生 Q30）で確定し、正は planning/docs/glossary.md（restricted＝**取扱制限**）。
//   **写像の実装先は #541 であり、それまでは生値を出す。**

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
  const [editing, setEditing] = useState<DocumentDto | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const documents = useAdminDocuments();
  // SC-05（#449）: タグの選択肢は辞書（`/bff/tags`）から引く。自由入力は許さない。
  const tagOptions = useTagOptions();
  const actions = useDocumentActions();
  const { create, update, publish, archive, remove } = actions;
  // IADR-0135 決定 6: 状態遷移は生成フックが 3 本に分かれる。画面の語彙（DocumentCommand）から
  // どのミューテーションを撃つかをここで選ぶ（パスの組み立ては生成コードが持つ）。
  const commands: Record<DocumentCommand, typeof publish | typeof archive | typeof remove> = {
    publish,
    archive,
    delete: remove,
  };
  const commandPending = publish.isPending || archive.isPending || remove.isPending;

  // FR-06, UC-03, SC-05（#629）: 計画 §SC-05「破壊的操作は管理者限定」（裁定 Q19）。
  // **本画面のボタンは 5 つとも書き込みなので、運用者には 1 つも出さない**
  // （＋新規登録・編集・公開・アーカイブ・削除。公開／アーカイブを含める根拠は
  // 作業仕様書 §判断 1）。押しても 403 になるボタンを置かない（[[IADR-0127]] 決定 1）。
  // **実効境界はサーバ側**（`/bff/documents` と後段の `AdminOnly`）であり、ここは表示制御にすぎない
  // （[[IADR-0039]] 決定 2）。**閲覧（一覧・SC-03 への詳細リンク）は運用者にも残す。**
  const canWrite = useHasAnyRole(PlatformRole.Admin);

  const items = documents.data ?? [];
  // IADR-0127 決定 7: 画面は**直近の操作の結果だけ**を出す。列挙は `useDocumentActions()` の
  // 戻り値から導く——手書きの配列にすると、次のミューテーションを足したときに
  // 「一覧には出るが読まれない失敗」「消し忘れる古い失敗」が静かに生まれる。
  // **#519 の載せ替えで束は 3 本から 5 本へ増えたが、ここは 1 文字も変えずに追随した**
  // （手書きの配列だったら 2 本を書き足す必要があった。IADR-0135 決定 6）。
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
        { id: editing.id, data: { ...values, expectedVersion: editing.version } },
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
      { data: { title: values.title, attributes: values.attributes, tags: values.tags } },
      { onSuccess: () => setNotice(t`文書を登録しました。`) },
    );
  }

  function run(id: string, kind: DocumentCommand, message: string) {
    beginOperation();
    commands[kind].mutate({ id }, { onSuccess: () => setNotice(message) });
  }

  return (
    <section className="flex flex-col gap-4 lg:flex-row">
      <div className="min-w-0 grow">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
          <h1 className="text-lg font-semibold text-[--color-fg]">
            <Trans>文書一覧</Trans>
          </h1>
          {/* 押しても 403 になるボタンを置かない（#502 が確立した規則・[[IADR-0127]] 決定 1）。
              無言で消すと「何もできない壊れた画面」に見えるので、理由の文言を残す。 */}
          {canWrite ? (
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
          ) : (
            <span className="text-xs text-[--color-fg-muted]">
              <Trans>文書の登録・編集・公開・アーカイブ・削除は管理者のみ実行できます</Trans>
            </span>
          )}
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
              ? (conflictDetails(failed.error) ??
                t`他の更新と競合しました（版が変わっています）。最新を再読み込みしてください。`)
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
                  {/* #629: 運用者には操作が 1 つも無いので、列ごと出さない
                      （空の「操作」列が並ぶと、押せる何かがあるように読める）。 */}
                  {canWrite && (
                    <TableHeaderCell>
                      <Trans>操作</Trans>
                    </TableHeaderCell>
                  )}
                </TableRow>
              </TableHead>
              <TableBody>
                {items.map((doc) => (
                  <DocumentRow
                    key={doc.id}
                    doc={doc}
                    busy={commandPending}
                    canWrite={canWrite}
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

      {/* #629: 入力フォームそのものが登録・編集の口なので、運用者には出さない。 */}
      {canWrite && (
        <div className="w-full lg:max-w-md">
          {/* 編集対象が変わったらフォームを作り直す（前の文書の入力値を持ち越さない）。 */}
          <DocumentForm
            key={editing?.id ?? 'new'}
            editing={editing}
            submitting={create.isPending || update.isPending}
            tagOptions={tagOptions.names}
            onSubmit={save}
            onCancel={() => setEditing(null)}
          />
        </div>
      )}
    </section>
  );
}

function DocumentRow({
  doc,
  busy,
  canWrite,
  onEdit,
  onCommand,
}: {
  doc: DocumentDto;
  busy: boolean;
  canWrite: boolean;
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
      {/* #629: 見出しと同じ条件で列ごと落とす（列数がずれると表が壊れる）。 */}
      {!canWrite ? null : (
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
      )}
    </TableRow>
  );
}
