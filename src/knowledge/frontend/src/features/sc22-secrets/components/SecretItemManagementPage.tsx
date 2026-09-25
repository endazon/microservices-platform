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
import { ConfirmDialog } from '../../../components/ConfirmDialog';
import { DataTable } from '../../../components/DataTable';
import type { DataTableColumns } from '../../../components/DataTable';
import { useSecretItemUpdate, useSecretItems } from '../api/useSecretItems';
import {
  MAX_REASON_LENGTH,
  MAX_VALUE_LENGTH,
  needsWriteConfirmation,
  secretConsumer,
  secretItemLabel,
  secretPropertyShapes,
  secretSupplySource,
  secretUpdateIssues,
} from '../types/secretItemVocabulary';

// SC-22, FR-05, NFR-18, ADR-0095 決定 1・3・4, IADR-0433, IADR-0453, IADR-0456 (#1477): 秘密情報・接続設定の管理
// （05_screens: ルート /admin/secrets）。
//
// ■ 🔴 **値の列を置かない。** 値は書き込み専用で、保存後は画面から読み出せない（契約にも読み出し口が無い）。
// ■ 🔴 **一括再投入のボタンを置かない**（ADR-0095 決定 4）。更新は行ごとに開き、**1 回に 1 プロパティだけ**送る
//   （IADR-0453 決定 2）。
// ■ **未設定と「取得できない」を描き分ける**（SC-22 主要素 3）。状態は色だけで示さない（StatusBadge が
//   色 ＋ アイコン ＋ テキストを強制する）。「設定済み」は KV に版があることであり、各プロパティに
//   値が入っていることは意味しない —— 注記でそう書く（IADR-0453 決定 4）。
// ■ 値と確認入力はマスクし、**一致しなければ送信できない**（SC-22 入力規則）。2 度目の入力が値の確認である（IADR-0453 決定 6）。
// ■ IADR-0456 決定 1〜3: 入力の形はプロパティの種別で変わる —— パスワード（MD5 で保存される旨を書く）／
//   生成（値の欄を持たず、鍵が置き換わることを確かめてから送る）／秘密でない ID（平文で入力させる）。
// ■ IADR-0456 決定 4: 保存後に即時同期を依頼できたかを示す（依頼できなくても書き込みは成立している）。
// ■ ADR-0104 決定 2, ADR-0110 決定 1, IADR-0460 決定 1 (#1502 / #1523): 一覧に「供給元」（画面／画面以外／確認できない）を出す。
//   値は BFF が配備の結果（同期先 ExternalSecret の有無）から判定したもので、🔴 **画面は推測しない**（無い・未知の値は「確認できない」）。
//   「画面以外」（契約の値は `git` のまま）の項目は**書き込みを拒否せず**、書いても反映されないことを送る前と後の両方で伝える。
// ■ ADR-0110 決定 3 (#1523): **書き込みの確認を、消費側の再起動の確認とする。** 「画面以外」でない項目は、送信の押下で
//   確認ダイアログを開き、再起動する消費側・断たれ得る処理・確認後に続くこと（即時同期 → 自動の作り直し／OpenD は手動）を出す。
//   確認して初めて送る。🔴 **書き込みとは別の再起動の操作は置かない**（BFF に Deployment を動かす権限を与えない）。
//   鍵の生成の確認（IADR-0456 決定 3）もこの確認の段へ統合した（2 度確認させない）。
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
        id: 'supplySource',
        header: t`供給元`,
        enableSorting: false,
        cell: ({ row }) => {
          // 色だけに意味を持たせない（StatusBadge が色 ＋ アイコン ＋ テキストを強制する）。
          const source = secretSupplySource(row.original);
          if (source === 'screen') return <StatusBadge tone="success">{t`画面`}</StatusBadge>;
          // ADR-0110 決定 1: 表示名は「画面以外」（契約の値 `git` は識別子であり、出どころが Git とは限らない）。
          if (source === 'git') return <StatusBadge tone="warning">{t`画面以外`}</StatusBadge>;
          return <StatusBadge tone="neutral">{t`確認できない`}</StatusBadge>;
        },
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
        <Note data-testid="secrets-supply-note">
          <Trans>
            供給元は、各項目の同期先（ExternalSecret）がクラスタにあるかで判定します。「画面」はこの画面で書いた値がアプリケーションへ届く状態です。「画面以外」は同期先が無く、値が画面以外（手で作った
            Secret・配備スクリプト・Git
            など）から供給されている状態で、この画面で書いた値は反映されません。「確認できない」は判定に必要なクラスタへの接続が無いか、判定に失敗したことを示します（同期を無効にした構成では全項目がこの表示になります）。
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
  const shapes = useMemo(() => secretPropertyShapes(row), [row]);
  const [property, setProperty] = useState(shapes[0]?.name ?? '');
  const [value, setValue] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [reason, setReason] = useState('');
  // ADR-0110 決定 3: 確認ダイアログを開いているか（開いている間は送っていない）。
  const [confirming, setConfirming] = useState(false);
  const [written, setWritten] = useState<{
    property: string;
    version: number;
    syncRequested: boolean;
  } | null>(null);

  const label = secretItemLabel(row.item);
  const itemName = label ? i18n._(label.name) : row.item;
  const heading = t`更新 — ${itemName}`;
  const shape = shapes.find((candidate) => candidate.name === property);
  const generate = shape?.kind === 'generate-rsa-pkcs1';
  const password = shape?.kind === 'md5-from-password';
  const plain = shape?.kind === 'value' && !shape.sensitive;
  const issues = secretUpdateIssues({ property, value, confirmation, reason }, shapes);
  const mismatch = confirmation.length > 0 && issues.includes('confirmation-mismatch');
  // ADR-0104 決定 2・4, ADR-0110 決定 3: 供給元と消費側（再起動の確認に使う）。
  const source = secretSupplySource(row);
  const consumer = secretConsumer(row.item);
  const restart = consumer?.restart ?? null;
  const consumerName = consumer ? i18n._(consumer.consumer) : '';
  const interrupts = consumer ? i18n._(consumer.interrupts) : '';

  // プロパティを替えたら入力を捨てる（別のプロパティの値を送らない）。
  const selectProperty = (next: string) => {
    setProperty(next);
    setValue('');
    setConfirmation('');
    setWritten(null);
  };

  const send = () => {
    setConfirming(false);
    setWritten(null);
    const trimmedReason = reason.trim();
    update.mutate(
      {
        item: row.item,
        // IADR-0456 決定 3: 生成は値を持たない（空文字で送り、BFF が鍵を作る）。
        data: {
          property,
          value: generate ? '' : value,
          reason: trimmedReason.length > 0 ? trimmedReason : null,
        },
      },
      {
        onSuccess: (result) => {
          // 🔴 送った値は画面に残さない（成功したら入力を空に戻す）。
          setValue('');
          setConfirmation('');
          setReason('');
          if (result.status === 200)
            setWritten({
              property: result.data.property,
              version: result.data.version,
              syncRequested: result.data.syncRequested === true,
            });
        },
      },
    );
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (issues.length > 0 || update.isPending) return;
    // ADR-0110 決定 3: 再起動を伴う（伴い得る）項目と鍵の生成は、押下では送らず確認ダイアログを開く。
    // 「画面以外」の値の書き込みは Secret が変わらず再起動も起きないので、そのまま送る。
    if (needsWriteConfirmation(source, generate)) {
      setConfirming(true);
      return;
    }
    send();
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
            onChange={(e) => selectProperty(e.target.value)}
          >
            {shapes.map(({ name }) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </Select>
        </div>

        {/* ADR-0104 決定 2, ADR-0110 決定 1: 🔴 書き込みは拒否しない。画面以外から供給されているなら、書いても効かないことを先に伝える。 */}
        {source === 'git' && (
          <Alert
            tone="warning"
            role="status"
            label={t`この画面で書いた値は反映されません`}
            data-testid="secret-supply-not-screen"
          >
            <Trans>
              この項目はいま画面以外（手で作った Secret・配備スクリプト・Git
              など）から供給されています。書き込みはできますが、配備時のスイッチを画面の経路へ切り替える（同期先を作る）まで、書いた値はアプリケーションに反映されません。
            </Trans>
          </Alert>
        )}
        {source === 'unknown' && (
          <Note data-testid="secret-supply-unknown">
            <Trans>
              この項目の供給元を確認できません。書いた値がアプリケーションに反映されるかは、この画面からは分かりません。
            </Trans>
          </Note>
        )}

        {generate ? (
          <Note data-testid="secret-generate-note">
            <Trans>
              このプロパティは値を入力しません。「生成」を押すと RSA 1024 bit
              の鍵を新しく作って保管先に書き込みます。鍵はこの画面にも表示されません。
            </Trans>
          </Note>
        ) : plain ? (
          <div>
            <Label htmlFor="secret-value">
              <Trans>新しい値</Trans>
            </Label>
            <Input
              id="secret-value"
              type="text"
              autoComplete="off"
              spellCheck={false}
              value={value}
              invalid={value.length > MAX_VALUE_LENGTH}
              onChange={(e) => setValue(e.target.value)}
            />
            <p className="text-xs text-fg-muted" data-testid="secret-non-secret-note">
              <Trans>
                このプロパティは秘密情報ではありません（環境ごとの ID
                など）。入力内容は表示されますが、保存後はこの画面から読み出せません。
              </Trans>
            </p>
          </div>
        ) : (
          <>
            <div>
              <Label htmlFor="secret-value">
                {password ? <Trans>パスワード</Trans> : <Trans>新しい値</Trans>}
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
                {password ? (
                  <Trans>パスワード（確認のためもう一度）</Trans>
                ) : (
                  <Trans>新しい値（確認のためもう一度）</Trans>
                )}
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
            {password && (
              <p className="text-xs text-fg-muted" data-testid="secret-md5-note">
                <Trans>
                  パスワードはそのまま保存されません。MD5 に変換した値だけが保管先に書き込まれます。
                </Trans>
              </p>
            )}
          </>
        )}

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
            {written.syncRequested && source !== 'git' && (
              <span className="block" data-testid="secret-sync-status">
                <Trans>
                  即時同期を依頼しました。アプリケーションへの反映まで少し時間がかかることがあります。
                </Trans>
              </span>
            )}
            {/* ADR-0110 決定 3: 書き込み後の表示でも「再起動するまで反映されない」を出す（配備の有無は検出しない）。 */}
            {source !== 'git' && (
              <span className="block" data-testid="secret-restart-after">
                {restart === 'manual-opend' ? (
                  <Trans>OpenD を手動で再起動するまで、書いた値は反映されません。</Trans>
                ) : (
                  <Trans>
                    自動再起動を配備していない環境では、この値を読むアプリケーションを再起動するまで反映されません。
                  </Trans>
                )}
              </span>
            )}
          </Alert>
        )}
        {/* ADR-0104 決定 2, ADR-0110 決定 1: 画面以外から供給されている項目は、同期の成否ではなく「反映されない」を伝える
            （同期先が無いので同期の依頼は通らず、「定期同期を待つ」と書くと反映されるかのように読める）。 */}
        {written && source === 'git' && (
          <Alert
            tone="warning"
            role="status"
            label={t`書いた値は反映されません`}
            data-testid="secret-not-applied"
          >
            <Trans>
              保管先への書き込みは完了しましたが、この項目は画面以外（手で作った
              Secret・配備スクリプト・Git
              など）から供給されているため、書いた値はアプリケーションに反映されません。
            </Trans>
          </Alert>
        )}
        {written && !written.syncRequested && source !== 'git' && (
          <Alert
            tone="warning"
            role="status"
            label={t`即時同期を依頼できませんでした`}
            data-testid="secret-sync-status"
          >
            <Trans>
              保管先への書き込みは完了しましたが、即時同期を依頼できませんでした。アプリケーションへの反映は定期同期（最大
              1 時間）を待ちます。
            </Trans>
          </Alert>
        )}

        <div className="flex flex-wrap gap-2">
          <Button type="submit" variant="primary" disabled={issues.length > 0 || update.isPending}>
            {generate ? <Trans>生成</Trans> : <Trans>このプロパティを更新する</Trans>}
          </Button>
          <Button type="button" onClick={onClose}>
            <Trans>閉じる</Trans>
          </Button>
        </div>
      </form>

      {/* ADR-0110 決定 3: 書き込みの確認＝消費側の再起動の確認。確認して初めて送る。初期フォーカスは取消（ConfirmDialog）。 */}
      {confirming && (
        <ConfirmDialog
          title={generate ? t`鍵を生成して書き込みますか？` : t`このプロパティを書き込みますか？`}
          confirmLabel={generate ? t`生成して書き込む` : t`書き込む`}
          cancelLabel={t`やめる`}
          // #1530 の監査: 生成は保管先の鍵を置き換える（旧版へ戻しても OpenD が読み込んだ鍵は戻らない）ので破壊的として示す。
          destructive={generate}
          pending={update.isPending}
          onConfirm={send}
          onCancel={() => setConfirming(false)}
        >
          <div data-testid="secret-write-confirm" className="flex flex-col gap-2">
            {/* #1530 の監査: 「画面以外」では同期先が無く、生成した鍵は OpenD にもクライアントにも届かない。
                「再起動するまで食い違う」と書くと再起動で届くかのように読めるので、供給元で書き分ける。 */}
            {generate &&
              (source === 'git' ? (
                <p data-testid="secret-generate-confirm">
                  <Trans>
                    鍵を生成し直すと、保管先の鍵が新しい鍵に置き換わります。この項目は画面以外から供給されているため、生成した鍵は
                    OpenD にもクライアントにも届きません（OpenD
                    を再起動しても届きません）。鍵はこの画面にも表示されません。
                  </Trans>
                </p>
              ) : (
                <p data-testid="secret-generate-confirm">
                  <Trans>
                    鍵を生成し直すと、保管先の鍵が新しい鍵に置き換わります。OpenD
                    が新しい鍵を読み込むまで（手動で再起動するまで）、OpenD
                    と同じ鍵で接続するクライアントとの間で鍵が食い違います。鍵はこの画面にも表示されません。
                  </Trans>
                </p>
              ))}
            {source !== 'git' && (
              <RestartConfirmation
                restart={restart}
                sourceUnknown={source === 'unknown'}
                consumerName={consumerName}
                interrupts={interrupts}
              />
            )}
          </div>
        </ConfirmDialog>
      )}
    </Panel>
  );
}

// ADR-0110 決定 3 (#1523): 確認の段の本文。再起動する消費側と断たれ得る処理の種類、確認後に続くことを項目ごとに書き分ける。
// 🔴 **断定しない**: 供給元を確認できない項目と表に無い項目は「再起動することがある」。自動の作り直しは「配備した環境の場合」に限る。
function RestartConfirmation({
  restart,
  sourceUnknown,
  consumerName,
  interrupts,
}: {
  restart: 'automatic' | 'manual-opend' | null;
  sourceUnknown: boolean;
  consumerName: string;
  interrupts: string;
}) {
  if (restart === null) {
    return (
      <p data-testid="secret-restart-confirm">
        <Trans>
          書き込むと、この値を読むアプリケーションが再起動されることがあります。再起動は稼働中の処理を中断します。止めてよい時機か確かめてから書き込んでください。
        </Trans>
      </p>
    );
  }
  return (
    <div data-testid="secret-restart-confirm" className="flex flex-col gap-2">
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
        <dt>
          <Trans>再起動する消費側</Trans>
        </dt>
        <dd>{consumerName}</dd>
        <dt>
          <Trans>断たれ得る処理</Trans>
        </dt>
        <dd>{interrupts}</dd>
      </dl>
      {restart === 'manual-opend' ? (
        <p>
          <Trans>
            この書き込みでは OpenD は再起動しません。書いた値を反映するには、書き込み後に kubectl -n
            ai-stock-trading rollout restart deploy/opend で OpenD
            を手動で再起動してください。手動の再起動は稼働中の処理を中断し、SMS
            または画像の認証を再び求められることがあります。
          </Trans>
        </p>
      ) : sourceUnknown ? (
        <p>
          <Trans>
            この項目の供給元を確認できないため、書き込みで {consumerName}{' '}
            が再起動するかは断定できません。同期先がある環境では、即時同期のあとで自動で再起動されることがあります（自動再起動を配備した環境の場合。配備していない環境では、再起動するまで反映されません）。
          </Trans>
        </p>
      ) : (
        <p>
          <Trans>
            確認して書き込むと、即時同期のあとで {consumerName}{' '}
            が自動で再起動されます（自動再起動を配備した環境の場合）。配備していない環境では、再起動するまで書いた値は反映されません。
          </Trans>
        </p>
      )}
      <p>
        <Trans>
          再起動は稼働中の処理を中断します。止めてよい時機か確かめてから書き込んでください。
        </Trans>
      </p>
    </div>
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
