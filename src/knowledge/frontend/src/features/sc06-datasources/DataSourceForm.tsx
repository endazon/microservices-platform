import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Button, Card, CardContent, CardHeader, CardTitle, Input, Label, Select } from '@platform/ui';
import {
  CONFIDENTIALITY_KEY,
  CONFIDENTIALITY_VALUES,
  DEFAULT_CONFIDENTIALITY,
} from '../abac/confidentiality';
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
            onSubmit({
              name: name.trim(),
              sourceType,
              connectionUri: connectionUri.trim(),
              defaultAttributes: { [CONFIDENTIALITY_KEY]: confidentiality },
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
