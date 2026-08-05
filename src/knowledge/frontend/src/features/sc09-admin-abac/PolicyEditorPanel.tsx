import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import type { MessageDescriptor } from '@lingui/core';
import {
  Alert,
  Button,
  Input,
  Label,
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
} from '@platform/ui';
import { i18n } from '@foundation/i18n';
import { toMessages } from '@foundation/ui/apiErrors';
import {
  attributeScopeLabel,
  buildConditions,
  policyActionLabel,
  POLICY_ACTIONS,
  summarizeConditions,
} from './abacVocabulary';
import type { ConditionEntry, PolicyAction } from './abacVocabulary';
import { usePolicyActions } from './useAbacAdmin';
// SC-09, IADR-0135 決定 2: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type {
  AbacPolicyDto,
  AttributeDefinitionDto,
} from '@foundation/api/generated/bff.schemas';

// SC-09, UC-05, FR-09: ポリシー定義 ＋ 検証結果（計画 §SC-09 §主要素）。
//
// IADR-0129 決定 2: 条件は**構造化エディタ**（属性 Select × 許可値 Select）で積む。
// 契約（AbacPolicyDto.UserConditions / DocumentConditions）が表現できるのは
// **属性キー → 許可値の集合所属**だけであり、hi-fi が描く自由記述の条件式（`<=`・「含む」）は
// 表現できないため実装しない（**部分未実装**として画面仕様書に明記した）。
// 計画の入力表「対象属性｜必須｜選択｜**定義済み属性のみ**」はこの形で満たしている。
//
// 「検証」ボタン（hi-fi 430 左）は**実装しない**——保存せずに矛盾検証だけを行う API が無い。
// 検証は `POST /policies` の 400（ValidationProblem）としてのみ得られるため、
// 検証結果パネルは**保存時のサーバ検証を唯一の情報源**とする（IADR-0040）。

function labelOf(label: MessageDescriptor | string): string {
  return typeof label === 'string' ? label : i18n._(label);
}

/**
 * ポリシー一覧の 1 行。
 *
 * 行を独立の部品にしてあるのは `lingui/no-expression-in-message` のためである——
 * 補間には**素の変数だけ**を置く（`${p.name}` のような式を入れると、抽出されたメッセージから
 * 元の値が読めなくなり、翻訳者が文脈を判断できない）。
 */
function PolicyRow({
  policy,
  onToggleActive,
  onDelete,
}: {
  policy: AbacPolicyDto;
  onToggleActive: () => void;
  onDelete: () => void;
}) {
  const { t } = useLingui();
  const policyName = policy.name;
  return (
    <TableRow>
      <TableCell>
        <div className="text-[--color-fg]">{policyName}</div>
        {/* アクションは**分類の名前**であり状態ではない（Tag / StatusBadge の使い分け）。 */}
        <Tag>{labelOf(policyActionLabel(policy.action))}</Tag>
      </TableCell>
      <TableCell className="text-xs text-[--color-fg-muted]">
        <ConditionSummary policy={policy} />
      </TableCell>
      <TableCell>
        {/* INDEX 決定 21: 状態は色 ＋ アイコン ＋ テキストで示す。
            琥珀（warning）はどの状態にも充てない——琥珀が指すのは**異常**であって、
            管理者が意図して無効化した正常な設定状態ではない（#503 / SC-06 と同じ判断）。 */}
        <StatusBadge tone={policy.isActive ? 'success' : 'neutral'}>
          {policy.isActive ? t`有効` : t`無効`}
        </StatusBadge>
      </TableCell>
      <TableCell className="whitespace-nowrap">
        <Button
          type="button"
          size="sm"
          variant="ghost"
          aria-label={
            policy.isActive
              ? t`ポリシーを無効化: ${policyName}`
              : t`ポリシーを有効化: ${policyName}`
          }
          onClick={onToggleActive}
        >
          {policy.isActive ? t`無効化` : t`有効化`}
        </Button>
        <Button
          type="button"
          size="sm"
          variant="ghost"
          aria-label={t`ポリシーを削除: ${policyName}`}
          onClick={onDelete}
        >
          <Trans>削除</Trans>
        </Button>
      </TableCell>
    </TableRow>
  );
}

export function PolicyEditorPanel({
  policies,
  attributes,
  isPending,
  isError,
  error,
}: {
  policies: AbacPolicyDto[];
  attributes: AttributeDefinitionDto[];
  isPending: boolean;
  isError: boolean;
  error: unknown;
}) {
  const { t } = useLingui();
  const { create, setActive, remove } = usePolicyActions();

  const [name, setName] = useState('');
  const [action, setAction] = useState<PolicyAction>('read');
  const [conditions, setConditions] = useState<ConditionEntry[]>([]);
  const [attributeKey, setAttributeKey] = useState('');
  const [conditionValue, setConditionValue] = useState('');

  const mutations = [create, setActive, remove];
  const failed = mutations.find((m) => m.isError);
  const saved = create.isSuccess;

  function beginOperation() {
    for (const mutation of mutations) mutation.reset();
  }

  const selected = attributes.find((a) => a.key === attributeKey);
  const values = selected?.allowedValues ?? [];

  return (
    <div className="grid gap-3 lg:grid-cols-2">
      <section aria-label={t`アクセスポリシー`}>
        <h2 className="mb-2 text-base font-semibold text-[--color-fg]">
          <Trans>ポリシー（利用者属性 × 文書属性）</Trans>
        </h2>

        {isPending && (
          <p role="status" className="text-sm text-[--color-fg-muted]">
            <Trans>読み込み中…</Trans>
          </p>
        )}
        {isError && (
          <Alert tone="danger" role="alert" label={t`エラー`}>
            {toMessages(error, t`ポリシーを取得できませんでした。`).join(' / ')}
          </Alert>
        )}

        {!isPending &&
          !isError &&
          (policies.length === 0 ? (
            <p className="text-sm">
              <Trans>ポリシーは登録されていません。</Trans>
            </p>
          ) : (
            <Table>
              <TableCaption>{t`アクセスポリシーの一覧`}</TableCaption>
              <TableHead>
                <TableRow>
                  <TableHeaderCell>
                    <Trans>ポリシー</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>条件</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>状態</Trans>
                  </TableHeaderCell>
                  <TableHeaderCell>
                    <Trans>操作</Trans>
                  </TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {policies.map((p) => (
                  <PolicyRow
                    key={p.id}
                    policy={p}
                    onToggleActive={() => {
                      beginOperation();
                      setActive.mutate({ id: p.id, data: { isActive: !p.isActive } });
                    }}
                    onDelete={() => {
                      beginOperation();
                      remove.mutate({ id: p.id });
                    }}
                  />
                ))}
              </TableBody>
            </Table>
          ))}

        <form
          aria-label={t`ポリシー登録`}
          className="mt-3 flex flex-col gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            beginOperation();
            create.mutate(
              { data: { name: name.trim(), action, ...buildConditions(conditions) } },
              {
                onSuccess: () => {
                  setName('');
                  setConditions([]);
                },
              },
            );
          }}
        >
          <h3 className="text-sm font-medium text-[--color-fg-muted]">
            <Trans>ポリシーを追加</Trans>
          </h3>
          <div className="grid gap-2 sm:grid-cols-2">
            <div>
              <Label htmlFor="policy-name">
                <Trans>名前（必須）</Trans>
              </Label>
              <Input id="policy-name" value={name} onChange={(e) => setName(e.target.value)} />
            </div>
            <div>
              <Label htmlFor="policy-action">
                <Trans>対象アクション</Trans>
              </Label>
              <Select
                id="policy-action"
                value={action}
                onChange={(e) => setAction(e.target.value as PolicyAction)}
              >
                {POLICY_ACTIONS.map((a) => (
                  <option key={a} value={a}>
                    {labelOf(policyActionLabel(a))}
                  </option>
                ))}
              </Select>
            </div>
          </div>

          {/* 計画の入力表: 「対象属性｜必須｜選択｜**定義済み属性のみ**」。
              属性辞書から引くため、辞書に無い属性で条件を作ることはできない。 */}
          <div className="grid gap-2 sm:grid-cols-3">
            <div>
              <Label htmlFor="policy-attr">
                <Trans>対象属性</Trans>
              </Label>
              <Select
                id="policy-attr"
                value={attributeKey}
                onChange={(e) => {
                  setAttributeKey(e.target.value);
                  setConditionValue('');
                }}
              >
                <option value="">{t`選択してください`}</option>
                {attributes.map((a) => (
                  <option key={a.id} value={a.key}>
                    {a.label || a.key}（{labelOf(attributeScopeLabel(a.scope))}）
                  </option>
                ))}
              </Select>
            </div>
            <div>
              <Label htmlFor="policy-value">
                <Trans>条件の値</Trans>
              </Label>
              <Select
                id="policy-value"
                value={conditionValue}
                onChange={(e) => setConditionValue(e.target.value)}
                disabled={values.length === 0}
              >
                <option value="">{t`選択してください`}</option>
                {values.map((v) => (
                  <option key={v} value={v}>
                    {v}
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex items-end">
              <Button
                type="button"
                disabled={!selected || conditionValue === ''}
                onClick={() => {
                  if (!selected) return;
                  setConditions((prev) => [
                    ...prev,
                    { scope: selected.scope, key: selected.key, value: conditionValue },
                  ]);
                  setConditionValue('');
                }}
              >
                <Trans>条件を追加</Trans>
              </Button>
            </div>
          </div>

          {conditions.length > 0 && (
            <ul aria-label={t`設定した条件`} className="flex flex-wrap gap-2">
              {conditions.map((c, i) => (
                <ConditionChip
                  key={`${c.scope}-${c.key}-${c.value}`}
                  condition={c}
                  onRemove={() => setConditions((prev) => prev.filter((_, j) => j !== i))}
                />
              ))}
            </ul>
          )}

          <div>
            <Button type="submit" variant="primary" disabled={name.trim().length === 0}>
              <Trans>保存</Trans>
            </Button>
          </div>
        </form>
      </section>

      <section aria-label={t`検証結果`}>
        <h2 className="mb-2 text-base font-semibold text-[--color-fg]">
          <Trans>検証結果</Trans>
        </h2>
        {saved && !failed && (
          <Alert tone="success" role="status" label={t`完了`}>
            <Trans>ポリシーを保存しました。認可判定へ即時反映されます。</Trans>
          </Alert>
        )}
        {failed && (
          <Alert tone="danger" role="alert" label={t`エラー`}>
            <ul>
              {toMessages(failed.error, t`ポリシーを保存できませんでした。`).map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          </Alert>
        )}
        {!saved && !failed && (
          <p className="text-sm text-[--color-fg-muted]">
            <Trans>
              保存前に構文・矛盾を検証します。矛盾があれば保存できません。保存すると認可判定へ即時反映されます。
            </Trans>
          </p>
        )}
      </section>
    </div>
  );
}

/** 積んだ条件のチップ（削除可能）。行と同じ理由で独立の部品にしている。 */
function ConditionChip({
  condition,
  onRemove,
}: {
  condition: ConditionEntry;
  onRemove: () => void;
}) {
  const { t } = useLingui();
  const conditionKey = condition.key;
  const conditionValue = condition.value;
  return (
    <li className="flex items-center gap-1">
      <Tag>{`${labelOf(attributeScopeLabel(condition.scope))} ${conditionKey} = ${conditionValue}`}</Tag>
      <Button
        type="button"
        size="sm"
        variant="ghost"
        aria-label={t`条件を外す: ${conditionKey} = ${conditionValue}`}
        onClick={onRemove}
      >
        ✕
      </Button>
    </li>
  );
}

/** 条件の要約。契約が表現するのは**属性キー → 許可値の集合所属**だけである。 */
function ConditionSummary({ policy }: { policy: AbacPolicyDto }) {
  const rows = summarizeConditions(policy);
  if (rows.length === 0) {
    return (
      <span>
        <Trans>条件なし（すべてに一致）</Trans>
      </span>
    );
  }
  return (
    <ul>
      {rows.map((row) => (
        <li key={`${row.scope}-${row.key}`}>
          {`${labelOf(attributeScopeLabel(row.scope))} ${row.key} = ${row.values.join(' / ')}`}
        </li>
      ))}
    </ul>
  );
}
