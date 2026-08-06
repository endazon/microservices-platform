import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import type { MessageDescriptor } from '@lingui/core';
import {
  Alert,
  Button,
  Input,
  Label,
  Select,
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
import { i18n } from '@foundation/i18n';
import { toMessages } from '@foundation/ui/apiErrors';
import {
  ATTRIBUTE_SCOPES,
  attributeScopeLabel,
  parseAllowedValues,
} from './abacVocabulary';
import type { AttributeScope } from './abacVocabulary';
import { useAttributeActions } from './useAbacAdmin';
// SC-09, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type { AttributeDefinitionDto } from '@foundation/api/generated/bff.schemas';

// SC-09, UC-05, FR-09: 属性体系エディタ（計画 §SC-09 §主要素）。
// 利用者属性・文書属性の辞書（キー・ラベル・許可値・必須・スコープ）を一覧・追加・削除する。
//
// hi-fi は「属性体系」タブの見出しだけを描き中身を描いていない（画面仕様書 §モックに無いが実装する要素）。

function labelOf(label: MessageDescriptor | string): string {
  return typeof label === 'string' ? label : i18n._(label);
}

/**
 * 属性辞書の 1 行。
 *
 * 行を独立の部品にしてあるのは `lingui/no-expression-in-message` のためである——
 * 補間には**素の変数だけ**を置く（`${a.key}` のような式を入れると、抽出されたメッセージから
 * 元の値が読めなくなり、翻訳者が文脈を判断できない）。
 */
function AttributeRow({
  attribute,
  onDelete,
}: {
  attribute: AttributeDefinitionDto;
  onDelete: () => void;
}) {
  const { t } = useLingui();
  const attributeKey = attribute.key;
  return (
    <TableRow>
      <TableCell className="font-mono text-xs">{attributeKey}</TableCell>
      <TableCell>{attribute.label}</TableCell>
      <TableCell>
        {/* スコープは**分類の名前**であり状態ではない（Tag / StatusBadge の使い分け）。 */}
        <Tag>{labelOf(attributeScopeLabel(attribute.scope))}</Tag>
      </TableCell>
      <TableCell>{attribute.allowedValues.join(' / ') || '—'}</TableCell>
      <TableCell>{attribute.required ? t`必須` : t`任意`}</TableCell>
      <TableCell>
        <Button
          type="button"
          size="sm"
          variant="ghost"
          aria-label={t`属性を削除: ${attributeKey}`}
          onClick={onDelete}
        >
          <Trans>削除</Trans>
        </Button>
      </TableCell>
    </TableRow>
  );
}

export function AttributeDictionaryPanel({
  attributes,
  isPending,
  isError,
  error,
}: {
  attributes: AttributeDefinitionDto[];
  isPending: boolean;
  isError: boolean;
  error: unknown;
}) {
  const { t } = useLingui();
  const { create, remove } = useAttributeActions();
  const [key, setKey] = useState('');
  const [label, setLabel] = useState('');
  const [allowedValues, setAllowedValues] = useState('');
  const [required, setRequired] = useState(false);
  const [scope, setScope] = useState<AttributeScope>('document');

  // IADR-0127 決定 7 と同じ形: 画面が出す操作結果は**直近の 1 件だけ**。新しい操作の開始時に
  // 全ミューテーションの状態を捨てる。TanStack Query は「別のミューテーションが成功した」ことでは
  // 他方の isError を戻さないため、放置すると成功バナーと古い失敗バナーが並ぶ。
  const mutations = [create, remove];
  const failed = mutations.find((m) => m.isError);
  const conflicted = failed?.error instanceof ApiError && failed.error.status === 409;
  const succeeded = mutations.find((m) => m.isSuccess);

  function beginOperation() {
    for (const mutation of mutations) mutation.reset();
  }

  return (
    <section aria-label={t`属性体系`}>
      <h2 className="mb-2 text-base font-semibold text-[--color-fg]">
        <Trans>属性辞書（利用者属性・文書属性）</Trans>
      </h2>

      {succeeded && !failed && (
        <Alert tone="success" role="status" className="mb-2" label={t`完了`}>
          <Trans>属性辞書を更新しました。</Trans>
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
          {conflicted ? (
            // IADR-0006: 参照中の属性は削除できない。理由を書かないと「なぜ消えないか」が分からない。
            // IADR-0040: サーバは**どのポリシーが参照しているか**を Problem 本文に載せ、apiClient が
            // それを ApiError.details へ抽出している。固定文言だけへ置き換えるとその参照元一覧が
            // 画面から消える——利用者は「先に見直す」対象を自力で探すことになる。両方を出す。
            <>
              <Trans>この属性は使用中のため削除できません。参照しているポリシーを先に見直してください。</Trans>{' '}
              {toMessages(failed.error, t`参照元は特定できませんでした。`).join(' / ')}
            </>
          ) : (
            toMessages(failed.error, t`属性辞書を更新できませんでした。`).join(' / ')
          )}
        </Alert>
      )}

      {isPending && (
        <p role="status" className="text-sm text-[--color-fg-muted]">
          <Trans>読み込み中…</Trans>
        </p>
      )}
      {isError && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          {toMessages(error, t`属性辞書を取得できませんでした。`).join(' / ')}
        </Alert>
      )}

      {!isPending &&
        !isError &&
        (attributes.length === 0 ? (
          <p className="text-sm">
            <Trans>属性は登録されていません。</Trans>
          </p>
        ) : (
          <Table>
            <TableCaption>{t`属性辞書の一覧`}</TableCaption>
            <TableHead>
              <TableRow>
                <TableHeaderCell>
                  <Trans>キー</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>ラベル</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>スコープ</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>許可値</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>必須</Trans>
                </TableHeaderCell>
                <TableHeaderCell>
                  <Trans>操作</Trans>
                </TableHeaderCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {attributes.map((a) => (
                <AttributeRow
                  key={a.id}
                  attribute={a}
                  onDelete={() => {
                    beginOperation();
                    remove.mutate({ id: a.id });
                  }}
                />
              ))}
            </TableBody>
          </Table>
        ))}

      <form
        aria-label={t`属性辞書登録`}
        className="mt-3 flex flex-col gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          beginOperation();
          create.mutate(
            {
              data: {
                key: key.trim(),
                label: label.trim(),
                allowedValues: parseAllowedValues(allowedValues),
                required,
                scope,
              },
            },
            {
              onSuccess: () => {
                setKey('');
                setLabel('');
                setAllowedValues('');
              },
            },
          );
        }}
      >
        <h3 className="text-sm font-medium text-[--color-fg-muted]">
          <Trans>属性を追加</Trans>
        </h3>
        <div className="grid gap-2 sm:grid-cols-2">
          <div>
            <Label htmlFor="attr-key">
              <Trans>キー（必須）</Trans>
            </Label>
            <Input id="attr-key" value={key} onChange={(e) => setKey(e.target.value)} />
          </div>
          <div>
            <Label htmlFor="attr-label">
              <Trans>ラベル</Trans>
            </Label>
            <Input id="attr-label" value={label} onChange={(e) => setLabel(e.target.value)} />
          </div>
          <div>
            <Label htmlFor="attr-values">
              <Trans>許可値（カンマ区切り）</Trans>
            </Label>
            <Input
              id="attr-values"
              value={allowedValues}
              onChange={(e) => setAllowedValues(e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="attr-scope">
              <Trans>スコープ</Trans>
            </Label>
            <Select
              id="attr-scope"
              value={scope}
              onChange={(e) => setScope(e.target.value as AttributeScope)}
            >
              {ATTRIBUTE_SCOPES.map((s) => (
                <option key={s} value={s}>
                  {labelOf(attributeScopeLabel(s))}
                </option>
              ))}
            </Select>
          </div>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={required}
            onChange={(e) => setRequired(e.target.checked)}
          />
          <Trans>必須属性にする</Trans>
        </label>
        <div>
          <Button type="submit" variant="primary" disabled={key.trim().length === 0}>
            <Trans>追加する</Trans>
          </Button>
        </div>
      </form>
    </section>
  );
}
