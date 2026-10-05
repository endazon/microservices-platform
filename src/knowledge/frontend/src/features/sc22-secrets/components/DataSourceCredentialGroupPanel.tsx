import { useMemo, useState } from 'react';
import type { FormEvent } from 'react';
import type { MessageDescriptor } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  EmptyState,
  Input,
  Label,
  Note,
  Panel,
  Select,
  StatusBadge,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tag,
  Textarea,
} from '@platform/ui';
import { ApiError } from '@foundation/api/ApiError';
import type { SecretItemGroupMemberStatusDto } from '@foundation/api/generated/bff.schemas';
import { QueryState } from '@foundation/ui/QueryState';
import { toMessages } from '@foundation/utils/apiErrors';
import { formatDateTime } from '@foundation/utils/formatDateTime';
import {
  DATASOURCE_CREDENTIALS_GROUP,
  useDataSourceCredentialGroup,
  useDataSourceCredentialUpdate,
} from '../api/useDataSourceCredentialGroup';
import {
  MAX_REASON_LENGTH,
  MAX_VALUE_LENGTH,
  dataSourceKindLabel,
  secretPropertyShapes,
  secretSupplySource,
  secretUpdateIssues,
} from '../types/secretItemVocabulary';

// SC-22, SC-06, NFR-18, 計画 ADR-0126 決定 1〜4, IADR-0501 (#458 段 S3): SC-22 の群「データソースの資格情報」。
//
// ■ 項目はデータソースの登録で 1 件増え、無効化で消える（決定 1）。行は BFF が DataSourceService から引いた成員である。
// ■ 🔴 **値の列を置かない・一括の口を置かない**（静的な項目と同じ。値は書き込み専用）。
// ■ 🔴 **書き込みは管理者だけ**（決定 3）。BFF が返す `writable` が true のときだけ「更新」を出す。運用者は閲覧だけで、
//   その旨を注記する。**実効境界は BFF の PUT 側**であり、この出し分けは表示制御である。
// ■ 供給元（決定 4）: 「画面」は「実行時に取得・次の同期から効く」と添える。「画面以外」は平文などから供給されていて、
//   書いても使われない（書き込みは拒否しない。移送は別段）。「確認できない」は判定できなかった。**再起動の確認は出さない。**
// ■ SC-06 の導線（`?datasource=<ID>`）: 当該行を強調し、書ける利用者には更新フォームを開く。群に無い ID は注記で伝える。

export function DataSourceCredentialGroupPanel({ focusId }: { focusId?: string }) {
  const { t, i18n } = useLingui();
  const group = useDataSourceCredentialGroup();
  const [editingId, setEditingId] = useState<string | null>(null);
  // 導線で開いたフォームを閉じたら、同じ URL のまま再び開かない。
  const [focusDismissed, setFocusDismissed] = useState(false);

  const members = useMemo(() => group.data?.members ?? [], [group.data]);
  const writable = group.data?.writable === true;
  const focused = focusId ? (members.find((m) => m.memberId === focusId) ?? null) : null;
  const effectiveEditingId =
    editingId ?? (writable && focused && !focusDismissed ? focused.memberId : null);
  const editing = writable
    ? (members.find((m) => m.memberId === effectiveEditingId) ?? null)
    : null;

  const errorDescription =
    group.error instanceof ApiError && group.error.status === 502
      ? t`登録済みのデータソースの一覧を取得できません。データソースの管理サービスの稼働を確認してください。`
      : group.error instanceof ApiError && group.error.status === 503
        ? t`保管先（Vault）に届かないため、データソースの資格情報の状態を確認できません。`
        : toMessages(group.error, '').join(' / ') || undefined;

  return (
    <>
      <Panel heading={t`データソースの資格情報`} data-testid="datasource-credentials">
        <p className="text-xs text-fg-muted">
          <Trans>
            データソースのコネクタが接続に使う資格情報（Wiki・SaaS の API
            トークン、業務DBのパスワード）です。データソースを登録すると項目が増え、無効化すると一覧から消えます。値はコネクタが同期のたびに保管先から読みます。
          </Trans>
        </p>
        {focusId && group.isSuccess && !focused && (
          <Note data-testid="datasource-credentials-focus-missing">
            <Trans>
              指定されたデータソースは、この画面で資格情報を設定する対象ではありません（無効化されているか、資格情報を使わない種別です）。
            </Trans>
          </Note>
        )}
        <QueryState
          query={group}
          isEmpty={(data) => data.members.length === 0}
          errorTitle={t`データソースの資格情報を取得できませんでした。`}
          errorDescription={errorDescription}
          empty={
            <EmptyState
              title={t`資格情報を使うデータソースは登録されていません。`}
              description={t`データソースの管理画面でデータソースを登録すると、ここに項目が増えます。`}
            />
          }
        >
          {(data) => (
            <Table>
              <TableCaption>
                <Trans>データソースの資格情報の一覧</Trans>
              </TableCaption>
              <TableHead>
                <TableRow>
                  <TableHeaderCell>
                    <Trans>項目名</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>最終更新日時</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>最終更新者</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>供給元</Trans>
                  </TableHeaderCell>
                  {data.writable && (
                    <TableHeaderCell>
                      <Trans>操作</Trans>
                    </TableHeaderCell>
                  )}
                </TableRow>
              </TableHead>
              <TableBody>
                {data.members.map((member) => {
                  const kind = dataSourceKindLabel(member.kind);
                  const isFocused = member.memberId === focusId;
                  return (
                    <TableRow
                      key={member.memberId}
                      data-testid={`datasource-credential-row-${member.memberId}`}
                      data-focused={isFocused ? 'true' : undefined}
                      aria-current={isFocused ? 'true' : undefined}
                      className={isFocused ? 'bg-brand/10' : undefined}
                    >
                      <TableCell>
                        <span className="font-medium">{member.displayName}</span>{' '}
                        <Tag tone="neutral">{kind ? i18n._(kind) : member.kind}</Tag>
                        <p className="text-xs text-fg-muted">
                          <code>{member.vaultPath}</code>
                        </p>
                      </TableCell>
                      <TableCell>
                        <MemberStatus member={member} />
                      </TableCell>
                      <TableCell>
                        {member.status === 'set' ? (member.lastUpdatedBy ?? t`記録なし`) : '—'}
                      </TableCell>
                      <TableCell>
                        <MemberSupply member={member} />
                      </TableCell>
                      {data.writable && (
                        <TableCell>
                          <Button size="sm" onClick={() => setEditingId(member.memberId)}>
                            <Trans>更新</Trans>
                          </Button>
                        </TableCell>
                      )}
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          )}
        </QueryState>
        {group.isSuccess && !writable && (
          <Note data-testid="datasource-credentials-read-only">
            <Trans>
              データソースの資格情報を更新できるのは管理者だけです（データソースの登録・更新と同じ）。この一覧では状態だけを確認できます。
            </Trans>
          </Note>
        )}
        <Note data-testid="datasource-credentials-supply-note">
          <Trans>
            供給元が「画面」の項目は、この画面で書いた値をコネクタが実行時に保管先から読みます。書いた値は次の同期から使われ、アプリケーションの再起動は要りません。「画面以外」の項目は、データソースの設定に画面以外から入れた値が残っているため、この画面で書いた値は使われません。
          </Trans>{' '}
          {/* 計画 ADR-0126 決定 4「不明を 2 値へ寄せない」（#458 段 S2 の独立監査）: 「確認できない」の意味と次の手を添える。 */}
          <Trans>
            「確認できない」の項目は、データソースの設定が保管先の値を参照しているかを確かめられませんでした（保管先に値があるのに参照を置けなかった場合など）。次の同期が失敗する場合は、もう一度更新してください。
          </Trans>
        </Note>
      </Panel>

      {editing && (
        <DataSourceCredentialUpdateForm
          key={editing.memberId}
          member={editing}
          onClose={() => {
            setEditingId(null);
            setFocusDismissed(true);
          }}
        />
      )}
    </>
  );
}

// SC-22 主要素 3: 未設定・取得できない・設定済み（最終更新日時）を色だけでなく語で描き分ける。
function MemberStatus({ member }: { member: SecretItemGroupMemberStatusDto }) {
  const { t } = useLingui();
  if (member.status === 'notSet') return <StatusBadge tone="warning">{t`未設定`}</StatusBadge>;
  if (member.status === 'unavailable')
    return <StatusBadge tone="danger">{t`取得できない`}</StatusBadge>;
  return (
    <div>
      <StatusBadge tone="success">{t`設定済み`}</StatusBadge>
      <p className="text-xs">{formatDateTime(member.lastUpdatedAt)}</p>
    </div>
  );
}

// 計画 ADR-0126 決定 4: 供給元の 3 値。「画面」には「実行時に取得・次の同期から効く」を添える。
// 判定できない（BFF が `unknown`・未知の値）は「確認できない」—— 色・語・（StatusBadge の）印で描き、2 値へ寄せない。
function MemberSupply({ member }: { member: SecretItemGroupMemberStatusDto }) {
  const { t } = useLingui();
  const source = secretSupplySource(member);
  if (source === 'screen')
    return (
      <div>
        <StatusBadge tone="success">{t`画面`}</StatusBadge>
        <p className="text-xs text-fg-muted">{t`実行時に取得・次の同期から効く`}</p>
      </div>
    );
  if (source === 'git') return <StatusBadge tone="warning">{t`画面以外`}</StatusBadge>;
  return <StatusBadge tone="neutral">{t`確認できない`}</StatusBadge>;
}

function DataSourceCredentialUpdateForm({
  member,
  onClose,
}: {
  member: SecretItemGroupMemberStatusDto;
  onClose: () => void;
}) {
  const { t, i18n } = useLingui();
  const update = useDataSourceCredentialUpdate();
  // 群の項目は種別「入力」（マスク入力・確認入力 2 度）。BFF の宣言が無くても「値・秘密」に倒す。
  const shapes = useMemo(
    () =>
      secretPropertyShapes({
        properties: member.propertyDetails.map((p) => p.name),
        propertyDetails: member.propertyDetails,
      }).map((shape) => ({ ...shape, kind: 'value' as const, sensitive: true })),
    [member],
  );
  const [property, setProperty] = useState(shapes[0]?.name ?? '');
  const [value, setValue] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [reason, setReason] = useState('');
  const [written, setWritten] = useState<{
    property: string;
    version: number;
    supplySource: string;
  } | null>(null);

  const issues = secretUpdateIssues({ property, value, confirmation, reason }, shapes);
  const mismatch = confirmation.length > 0 && issues.includes('confirmation-mismatch');
  const source = secretSupplySource(member);
  const name = member.displayName;
  const heading = t`資格情報の更新 — ${name}`;

  const selectProperty = (next: string) => {
    setProperty(next);
    setValue('');
    setConfirmation('');
    setWritten(null);
  };

  // 計画 ADR-0126 決定 4: 🔴 **再起動の確認の段を置かない**（書いても消費側は再起動しない。次の同期から効く）。
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (issues.length > 0 || update.isPending) return;
    setWritten(null);
    const trimmedReason = reason.trim();
    update.mutate(
      {
        group: DATASOURCE_CREDENTIALS_GROUP,
        memberId: member.memberId,
        data: { property, value, reason: trimmedReason.length > 0 ? trimmedReason : null },
      },
      {
        onSuccess: (result) => {
          // 🔴 送った値は画面に残さない。
          setValue('');
          setConfirmation('');
          setReason('');
          if (result.status === 200)
            setWritten({
              property: result.data.property,
              version: result.data.version,
              supplySource: result.data.supplySource,
            });
        },
      },
    );
  };

  const writtenProperty = written?.property ?? '';
  const writtenVersion = written?.version ?? 0;

  return (
    <Panel heading={heading} data-testid="datasource-credential-update-form">
      <form onSubmit={submit} className="space-y-3" noValidate>
        <div>
          <Label htmlFor="datasource-credential-property">
            <Trans>更新するプロパティ</Trans>
          </Label>
          <Select
            id="datasource-credential-property"
            selectSize="sm"
            value={property}
            onChange={(e) => selectProperty(e.target.value)}
          >
            {shapes.map(({ name: option }) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </Select>
        </div>

        {/* 計画 ADR-0126 決定 4: 🔴 書き込みは拒否しない。平文などが残っていれば、書いても使われないことを先に伝える。 */}
        {source === 'git' && (
          <Alert
            tone="warning"
            role="status"
            label={t`この画面で書いた値は使われません`}
            data-testid="datasource-credential-not-screen"
          >
            <Trans>
              このデータソースの設定には、画面以外から入れた資格情報が残っています。書き込みはできますが、設定が保管先の値を参照するように移されるまで、書いた値は同期に使われません。
            </Trans>
          </Alert>
        )}

        <div>
          <Label htmlFor="datasource-credential-value">
            <Trans>新しい値</Trans>
          </Label>
          <Input
            id="datasource-credential-value"
            type="password"
            autoComplete="new-password"
            spellCheck={false}
            value={value}
            invalid={value.length > MAX_VALUE_LENGTH}
            onChange={(e) => setValue(e.target.value)}
          />
        </div>
        <div>
          <Label htmlFor="datasource-credential-confirmation">
            <Trans>新しい値（確認のためもう一度）</Trans>
          </Label>
          <Input
            id="datasource-credential-confirmation"
            type="password"
            autoComplete="new-password"
            spellCheck={false}
            value={confirmation}
            invalid={mismatch}
            onChange={(e) => setConfirmation(e.target.value)}
          />
        </div>
        <div>
          <Label htmlFor="datasource-credential-reason">
            <Trans>更新の理由（任意）</Trans>
          </Label>
          <Textarea
            id="datasource-credential-reason"
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
            data-testid="datasource-credential-confirmation-mismatch"
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
          <Alert
            tone="danger"
            role="alert"
            label={t`エラー`}
            data-testid="datasource-credential-update-error"
          >
            {groupUpdateErrorMessage(update.error, (message) => i18n._(message))}
          </Alert>
        )}
        {written && (
          <Alert
            tone={written.supplySource === 'screen' ? 'success' : 'warning'}
            role="status"
            label={t`更新しました`}
            data-testid="datasource-credential-update-done"
          >
            <Trans>
              {writtenProperty} を更新しました（版 {writtenVersion}）。
            </Trans>{' '}
            {written.supplySource === 'screen' ? (
              <Trans>書いた値は次の同期から使われます（再起動は要りません）。</Trans>
            ) : written.supplySource === 'git' ? (
              <Trans>
                保管先への書き込みは完了しましたが、このデータソースの設定には画面以外から入れた資格情報が残っているため、書いた値は使われません。
              </Trans>
            ) : (
              <Trans>
                保管先への書き込みは完了しましたが、データソースの設定が保管先の値を参照しているかを確認できませんでした。次の同期が失敗する場合は、もう一度更新してください。
              </Trans>
            )}
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

// 書き込みの失敗の文言。**「値は保存されていない」を必ず添える。** 境界層の日本語の title をそのまま出さない。
const GROUP_UPDATE_FORBIDDEN = msg`データソースの資格情報を更新できるのは管理者だけです。値は保存されていません。`;
const GROUP_UPDATE_NOT_REGISTERED = msg`このデータソースは登録されていないか、無効化されています。値は保存されていません。`;
const GROUP_UPDATE_BAD_GATEWAY = msg`登録済みのデータソースを確認できないか、保管先（Vault）が書き込みを受け付けませんでした。値は保存されていません。`;
const GROUP_UPDATE_UNAVAILABLE = msg`保管先（Vault）に届かないため、更新できませんでした。値は保存されていません。`;
const GROUP_UPDATE_CURRENT_VERSION_DELETED = msg`この項目の現在の版は保管先（Vault）で削除されています。運用 Runbook の手順でコンソールから版を復元してから、もう一度更新してください。値は保存されていません。`;
const GROUP_UPDATE_FAILED = msg`更新できませんでした。値は保存されていません。`;

function groupUpdateErrorMessage(
  error: unknown,
  resolve: (message: MessageDescriptor) => string,
): string {
  if (error instanceof ApiError) {
    if (error.status === 403) return resolve(GROUP_UPDATE_FORBIDDEN);
    if (error.status === 404) return resolve(GROUP_UPDATE_NOT_REGISTERED);
    if (error.status === 409) return resolve(GROUP_UPDATE_CURRENT_VERSION_DELETED);
    if (error.status === 502) return resolve(GROUP_UPDATE_BAD_GATEWAY);
    if (error.status === 503) return resolve(GROUP_UPDATE_UNAVAILABLE);
  }
  return toMessages(error, resolve(GROUP_UPDATE_FAILED)).join(' / ');
}
