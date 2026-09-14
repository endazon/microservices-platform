import { useMemo, useState } from 'react';
import type { FormEvent } from 'react';
import type { MessageDescriptor } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  Input,
  Label,
  Note,
  Panel,
  Select,
  StatusBadge,
  Textarea,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import type { SecretItemStatusDto } from '@foundation/api/generated/bff.schemas';
import { QueryState } from '@foundation/ui/QueryState';
import { toMessages } from '@foundation/utils/apiErrors';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import { DataTable } from '../../../components/DataTable';
import type { DataTableColumns } from '../../../components/DataTable';
import { useSecretItemUpdate, useSecretItems } from '../api/useSecretItems';
import {
  MAX_REASON_LENGTH,
  MAX_VALUE_LENGTH,
  secretItemLabel,
  secretUpdateIssues,
} from '../types/secretItemVocabulary';

// SC-22, FR-05, NFR-18, ADR-0095 決定 1・3・4, IADR-0433, IADR-0453: 秘密情報・接続設定の管理
// （05_screens: ルート /admin/secrets）。
//
// ■ 🔴 **値の列を置かない。** 値は書き込み専用で、保存後は画面から読み出せない（契約にも読み出し口が無い）。
// ■ 🔴 **一括再投入のボタンを置かない**（ADR-0095 決定 4）。更新は行ごとに開き、**1 回に 1 プロパティだけ**送る
//   （IADR-0453 決定 2）。
// ■ **未設定と「取得できない」を描き分ける**（SC-22 主要素 3）。状態は色だけで示さない（StatusBadge が
//   色 ＋ アイコン ＋ テキストを強制する）。「設定済み」は KV に版があることであり、各プロパティに
//   値が入っていることは意味しない —— 注記でそう書く（IADR-0453 決定 4）。
// ■ 値と確認入力はマスクし、**一致しなければ送信できない**（SC-22 入力規則）。確認ダイアログは置かない（IADR-0453 決定 6）。
// ■ 到達できるのは運用者・システム管理者だけ。ガードはルート側（RequireRole → NotFound）にある。

export function SecretItemManagementPage() {
  const { t, i18n } = useLingui();
  const items = useSecretItems();
  const [editingItem, setEditingItem] = useState<string | null>(null);

  const rows = useMemo(() => items.data ?? [], [items.data]);
  const editing = rows.find((row) => row.item === editingItem) ?? null;

  const columns: DataTableColumns<SecretItemStatusDto> = useMemo(
    () => [
      {
        id: 'name',
        header: t`項目名`,
        enableSorting: false,
        cell: ({ row }) => {
          const label = secretItemLabel(row.original.item);
          return (
            <div>
              <span>{label ? i18n._(label.name) : row.original.item}</span>
              <p className="text-xs text-fg-muted">
                <code>{row.original.vaultPath}</code>
              </p>
            </div>
          );
        },
      },
      {
        id: 'purpose',
        header: t`用途`,
        enableSorting: false,
        cell: ({ row }) => {
          const label = secretItemLabel(row.original.item);
          return (
            <span className="text-xs">
              {label ? i18n._(label.purpose) : t`用途の説明が登録されていません。`}
            </span>
          );
        },
      },
      {
        id: 'updatedAt',
        header: t`最終更新日時`,
        enableSorting: false,
        cell: ({ row }) => {
          const { status, lastUpdatedAt } = row.original;
          if (status === 'notSet') return <StatusBadge tone="warning">{t`未設定`}</StatusBadge>;
          if (status === 'unavailable')
            return <StatusBadge tone="danger">{t`取得できない`}</StatusBadge>;
          return (
            <div>
              <StatusBadge tone="success">{t`設定済み`}</StatusBadge>
              <p className="text-xs">{formatDateTime(lastUpdatedAt)}</p>
            </div>
          );
        },
      },
      {
        id: 'updatedBy',
        header: t`最終更新者`,
        enableSorting: false,
        cell: ({ row }) =>
          row.original.status === 'set' ? (row.original.lastUpdatedBy ?? t`記録なし`) : '—',
      },
      {
        id: 'operation',
        header: t`操作`,
        enableSorting: false,
        cell: ({ row }) => (
          <Button size="sm" onClick={() => setEditingItem(row.original.item)}>
            <Trans>更新</Trans>
          </Button>
        ),
      },
    ],
    [t, i18n],
  );

  const listErrorDescription =
    items.error instanceof ApiError && items.error.status === 503
      ? t`秘密情報の保管先（Vault）が構成されていないか、接続できません。保管先の稼働を確認してください。`
      : toMessages(items.error, '').join(' / ') || undefined;

  return (
    <section className="space-y-6">
      <div>
        <h1 className="text-[17px] font-medium text-fg">
          <Trans>秘密情報・接続設定の管理</Trans>
        </h1>
        <p className="text-xs text-fg-muted" data-testid="secrets-help">
          <Trans>
            Git に置けない秘密情報（API キー・接続秘密・通知の webhook）を 1
            項目ずつ投入・更新します。値は書き込み専用で、保存後はこの画面からも読み出せません。操作は監査ログに記録されます（値は記録されません）。
          </Trans>
        </p>
      </div>

      <Panel heading={t`項目`}>
        {/* 🔴 待ち・失敗・空・本体は `QueryState` が描き分ける。**失敗を空の一覧へ縮退しない** ——
            「扱う項目が無い」と「保管先に届かない」は別の意味である（IADR-0453 決定 5）。 */}
        <QueryState
          query={items}
          isEmpty={(list) => list.length === 0}
          errorTitle={t`秘密情報の項目を取得できませんでした。`}
          errorDescription={listErrorDescription}
        >
          {() => (
            <DataTable
              caption={t`秘密情報の項目の一覧`}
              sortHint={t`並べ替え`}
              columns={columns}
              data={rows}
            />
          )}
        </QueryState>
        <Note data-testid="secrets-status-note">
          <Trans>
            「設定済み」は保管先に値の版があることを示します。各プロパティに空でない値が入っているかは、値を読み出さないためこの画面では判定できません。最終更新者は、この画面から更新した版にだけ表示されます（コンソールから更新した版は「記録なし」）。
          </Trans>
        </Note>
      </Panel>

      {editing && (
        <SecretUpdateForm key={editing.item} row={editing} onClose={() => setEditingItem(null)} />
      )}
    </section>
  );
}

function SecretUpdateForm({ row, onClose }: { row: SecretItemStatusDto; onClose: () => void }) {
  const { t, i18n } = useLingui();
  const update = useSecretItemUpdate();
  const [property, setProperty] = useState(row.properties[0] ?? '');
  const [value, setValue] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [reason, setReason] = useState('');
  const [written, setWritten] = useState<{ property: string; version: number } | null>(null);

  const label = secretItemLabel(row.item);
  const itemName = label ? i18n._(label.name) : row.item;
  const heading = t`更新 — ${itemName}`;
  const issues = secretUpdateIssues({ property, value, confirmation, reason }, row.properties);
  const mismatch = confirmation.length > 0 && issues.includes('confirmation-mismatch');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (issues.length > 0 || update.isPending) return;
    setWritten(null);
    const trimmedReason = reason.trim();
    update.mutate(
      {
        item: row.item,
        data: { property, value, reason: trimmedReason.length > 0 ? trimmedReason : null },
      },
      {
        onSuccess: (result) => {
          // 🔴 送った値は画面に残さない（成功したら入力を空に戻す）。
          setValue('');
          setConfirmation('');
          setReason('');
          if (result.status === 200)
            setWritten({ property: result.data.property, version: result.data.version });
        },
      },
    );
  };

  const writtenProperty = written?.property ?? '';
  const writtenVersion = written?.version ?? 0;

  return (
    <Panel heading={heading} data-testid="secret-update-form">
      <form onSubmit={submit} className="space-y-3" noValidate>
        <div>
          <Label htmlFor="secret-property">
            <Trans>更新するプロパティ</Trans>
          </Label>
          <Select
            id="secret-property"
            selectSize="sm"
            value={property}
            onChange={(e) => setProperty(e.target.value)}
          >
            {row.properties.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </Select>
        </div>
        <div>
          <Label htmlFor="secret-value">
            <Trans>新しい値</Trans>
          </Label>
          <Input
            id="secret-value"
            type="password"
            autoComplete="new-password"
            spellCheck={false}
            value={value}
            invalid={value.length > MAX_VALUE_LENGTH}
            onChange={(e) => setValue(e.target.value)}
          />
        </div>
        <div>
          <Label htmlFor="secret-confirmation">
            <Trans>新しい値（確認のためもう一度）</Trans>
          </Label>
          <Input
            id="secret-confirmation"
            type="password"
            autoComplete="new-password"
            spellCheck={false}
            value={confirmation}
            invalid={mismatch}
            onChange={(e) => setConfirmation(e.target.value)}
          />
        </div>
        <div>
          <Label htmlFor="secret-reason">
            <Trans>更新の理由（任意）</Trans>
          </Label>
          <Textarea
            id="secret-reason"
            rows={2}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
          />
          <p className="text-xs text-fg-muted">
            <Trans>理由は監査ログに残ります。値そのものは書かないでください。</Trans>
          </p>
        </div>

        {mismatch && (
          <Alert
            tone="warning"
            role="alert"
            label={t`入力を確認してください`}
            data-testid="secret-confirmation-mismatch"
          >
            <Trans>確認のための入力が一致しません。</Trans>
          </Alert>
        )}
        {issues.includes('value-too-long') && (
          <Alert tone="warning" role="alert" label={t`入力を確認してください`}>
            <Trans>値は {MAX_VALUE_LENGTH} 文字以内で入力してください。</Trans>
          </Alert>
        )}
        {issues.includes('reason-too-long') && (
          <Alert tone="warning" role="alert" label={t`入力を確認してください`}>
            <Trans>更新の理由は {MAX_REASON_LENGTH} 文字以内で入力してください。</Trans>
          </Alert>
        )}

        {update.isError && (
          <Alert tone="danger" role="alert" label={t`エラー`} data-testid="secret-update-error">
            {updateErrorMessage(update.error, (message) => i18n._(message))}
          </Alert>
        )}
        {written && (
          <Alert
            tone="success"
            role="status"
            label={t`更新しました`}
            data-testid="secret-update-done"
          >
            <Trans>
              {writtenProperty} を更新しました（版 {writtenVersion}）。
            </Trans>
          </Alert>
        )}

        <div className="flex flex-wrap gap-2">
          <Button type="submit" variant="primary" disabled={issues.length > 0 || update.isPending}>
            <Trans>このプロパティを更新する</Trans>
          </Button>
          <Button type="button" onClick={onClose}>
            <Trans>閉じる</Trans>
          </Button>
        </div>
      </form>
    </Panel>
  );
}

// 書き込みの失敗の文言。**「値は保存されていない」を必ず添える**（利用者は書けたかどうかを次の一手に使う）。
// 関数へ `t` を渡すとマクロが展開されないので、文言は `msg` で持ち、呼び出し側の `i18n._` で解決する。
const UPDATE_UNAVAILABLE = msg`秘密情報の保管先（Vault）が構成されていないか、接続できません。値は保存されていません。`;
const UPDATE_REJECTED = msg`秘密情報の保管先（Vault）が書き込みを受け付けませんでした。値は保存されていません。`;
const UPDATE_FAILED = msg`更新できませんでした。値は保存されていません。`;
// IADR-0454 決定 1 (#1467): 現在の版が保管先で削除・破棄されている（409）。一覧では「未設定」と出る状態であり、
// 🔴 **画面は権限を広げて削除済みの版の上へ書かない**。次の一手（コンソールで版を復元する）を示す。
// 境界層の日本語の title をそのまま出さない（en ロケールで日本語が混ざる）。
const UPDATE_CURRENT_VERSION_DELETED = msg`この項目の現在の版は保管先（Vault）で削除されています。画面からは削除された版へ書き込めないため、運用 Runbook の手順でコンソールから版を復元してから、もう一度更新してください。値は保存されていません。`;

function updateErrorMessage(
  error: unknown,
  resolve: (message: MessageDescriptor) => string,
): string {
  if (error instanceof ApiError && error.status === 409)
    return resolve(UPDATE_CURRENT_VERSION_DELETED);
  if (error instanceof ApiError && error.status === 503) return resolve(UPDATE_UNAVAILABLE);
  if (error instanceof ApiError && error.status === 502) return resolve(UPDATE_REJECTED);
  return toMessages(error, resolve(UPDATE_FAILED)).join(' / ');
}
