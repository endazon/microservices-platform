import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Label,
  Select,
} from '@platform/ui';
import {
  CONFIDENTIALITY_KEY,
  CONFIDENTIALITY_VALUES,
  DEFAULT_CONFIDENTIALITY,
} from '../abac/confidentiality';
import { DEPARTMENT_KEY, UNRESOLVED_DEPARTMENT } from '../abac/department';
import { i18n } from '@foundation/i18n';
import { SOURCE_TYPES, sourceTypeLabel } from './syncState';
import type { CreateDataSourceRequest } from '@foundation/api/generated/bff.schemas';

// SC-06, UC-04 基本 1, FR-01: データソース登録フォーム
// （05_screens §SC-06 主要素「ソース登録ボタン」「コネクタ設定」）。
// 認証情報はここに入力しない——Vault 管理である旨を画面の注記が伝える（計画 §SC-06 主要素）。

const MAX_NAME = 200;
const MAX_URI = 500;

export function DataSourceForm({
  onSubmit,
  onCancel,
  submitting,
}: {
  onSubmit: (input: CreateDataSourceRequest) => void;
  onCancel: () => void;
  submitting: boolean;
}) {
  const { t } = useLingui();
  const [name, setName] = useState('');
  const [sourceType, setSourceType] = useState<string>(SOURCE_TYPES[0]);
  const [connectionUri, setConnectionUri] = useState('');
  const [confidentiality, setConfidentiality] = useState<string>(DEFAULT_CONFIDENTIALITY);
  // FR-05, UC-04, SC-06: 既定の所管部門（#767）。計画 09_datasource-connectors §システム投入経路の
  // **2 段目**（データソースの既定属性）を開ける。1 段目（ソースから解決）は写像規則が未裁定である。
  const [department, setDepartment] = useState('');

  const canSubmit = name.trim().length > 0 && connectionUri.trim().length > 0 && !submitting;

  return (
    <Card className="mb-3">
      <CardHeader>
        <CardTitle>
          <Trans>データソースを登録</Trans>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <form
          aria-label={t`データソース登録`}
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (!canSubmit) return;
            // FR-05, UC-04, SC-06: 未入力の `department` は**キーごと送らない**（#767）。
            // 後段の `FillIfBlank` は空文字も予約値へ倒すので今日の結果は同じだが、空文字を送る形は
            // その空白判定に依存する。判定がキーの有無だけに変わった瞬間、画面から登録した全ソースの
            // 部門が空文字になり、予約値との区別（＝環流債務の測定値。IADR-0199）が静かに壊れる。
            const trimmedDepartment = department.trim();
            onSubmit({
              name: name.trim(),
              sourceType,
              connectionUri: connectionUri.trim(),
              defaultAttributes: {
                [CONFIDENTIALITY_KEY]: confidentiality,
                ...(trimmedDepartment ? { [DEPARTMENT_KEY]: trimmedDepartment } : {}),
              },
            });
          }}
        >
          <div>
            <Label htmlFor="ds-name" requiredHint={t`（必須）`}>
              <Trans>名前</Trans>
            </Label>
            <Input
              id="ds-name"
              value={name}
              maxLength={MAX_NAME}
              onChange={(e) => setName(e.target.value)}
            />
          </div>

          <div>
            <Label htmlFor="ds-type" requiredHint={t`（必須）`}>
              <Trans>種別</Trans>
            </Label>
            <Select id="ds-type" value={sourceType} onChange={(e) => setSourceType(e.target.value)}>
              {SOURCE_TYPES.map((type) => {
                const label = sourceTypeLabel(type);
                return (
                  <option key={type} value={type}>
                    {typeof label === 'string' ? label : i18n._(label)}
                  </option>
                );
              })}
            </Select>
          </div>

          <div>
            <Label htmlFor="ds-uri" requiredHint={t`（必須）`}>
              <Trans>接続先 URI</Trans>
            </Label>
            <Input
              id="ds-uri"
              value={connectionUri}
              maxLength={MAX_URI}
              onChange={(e) => setConnectionUri(e.target.value)}
              placeholder={t`例: smb://fs01/share/規程集`}
            />
          </div>

          <div>
            {/* FR-05 / IADR-0019: 既定の機密区分。未指定でもサーバが internal を補完する。
                値そのものは翻訳しない（表示名が計画に無い値がある。abac/confidentiality.ts 参照）。 */}
            <Label htmlFor="ds-conf">
              <Trans>既定の機密区分</Trans>
            </Label>
            <Select
              id="ds-conf"
              value={confidentiality}
              onChange={(e) => setConfidentiality(e.target.value)}
            >
              {CONFIDENTIALITY_VALUES.map((value) => (
                <option key={value} value={value}>
                  {value}
                </option>
              ))}
            </Select>
          </div>

          <div>
            {/* FR-05, UC-04, SC-06（#767）: 既定の所管部門。計画 07_abac-attribute-model は
                `department` を**必須**の文書属性と定めるが、値域を列挙していない（「部門コード
                （人事/経理/開発 等）」の例示のみ）ため自由入力にする。**実装が値集合を決めない。**
                予約値そのものは翻訳しない（機密区分の値と同じ扱い）。 */}
            <Label htmlFor="ds-dept">
              <Trans>既定の部門</Trans>
            </Label>
            <Input
              id="ds-dept"
              value={department}
              aria-describedby="ds-dept-hint"
              onChange={(e) => setDepartment(e.target.value)}
              placeholder={t`例: 開発`}
            />
            <p id="ds-dept-hint" className="text-xs text-[--color-fg-muted]">
              <Trans>未入力のときは予約値 {UNRESOLVED_DEPARTMENT} が入ります。</Trans>
            </p>
          </div>

          <div className="flex gap-2">
            <Button type="submit" variant="primary" disabled={!canSubmit}>
              <Trans>登録する</Trans>
            </Button>
            <Button type="button" onClick={onCancel}>
              <Trans>キャンセル</Trans>
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}
