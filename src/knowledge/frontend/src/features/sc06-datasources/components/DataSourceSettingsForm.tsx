import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link } from '@tanstack/react-router';
import { Button, Input, Label, Panel, Tag } from '@platform/ui';
import { i18n } from '@foundation/i18n';
import type { DataSourceDto, PatchDataSourceRequest } from '@foundation/api/generated/bff.schemas';
import { sourceTypeLabel } from '../types/syncState';

// FR-01, UC-04, SC-06（計画 §SC-06 アクション「設定変更」・更新 API の裁定 Q16）(#2122): **データソースの設定変更。**
//
// 編集できるのは **名前・接続先 URI・`config` の非秘密キー**（値の変更・キーの追加と削除）だけである。
// - 🔴 **種別（`sourceType`）は読み取り専用。** 変えるとコネクタが入れ替わる。計画が挙げる日常の運用
//   （接続先の変更・認証情報のローテーション・同期対象パスの追加）に種別の変更は無い。
// - 🔴 **マスク済みのキー（値が `***`）は読み取り専用。** 認証情報は Vault で管理し、SC-22 で設定する（計画 ADR-0126 決定 1）。
//   送り返すと後段が既存値を保つ（`SecretConfigMask.PreserveMasked`）ので、この画面からは壊せない。
//   **秘密キーの判定（`KeyMarkers`）を SPA に複写しない** —— 後段の 1 か所にだけ置く（2 か所に書くと片方だけが緩む）。
//   判定に使うのは「応答で伏せられて返った値か」だけである。
// - 🔴 **PATCH は変わった項目だけを送る**（後段は null を現状維持と読む）。`config` は変えたときだけ全量を送る
//   （後段は全置換。伏せた値は `***` のまま送り、後段が既存値を保つ）。何も変わっていなければ送らない。
// - 後段の検証の理由（`ConnectionUriPolicy` の `{ error }` 形の 400・RFC 7807 の 400）は、画面の結果表示が出す（`toMessages`）。

/**
 * 応答で伏せられて返った値（後段の `SecretMask.Placeholder` と同じ文字列）。判定は値で行い、キー名では行わない。
 * 既知の制約（#2122）: 非秘密キーの値として利用者が `***` そのものを保存すると、次に開いたときに伏せた値と区別できず
 * 読み取り専用で描く（後段は秘密キーでなければ `***` を普通の値として保存する）。キー名で判定すれば区別できるが、
 * 判定の規則（`KeyMarkers`）を SPA に複写しないことを優先した。値を変えるには API を使う。
 */
const MASKED = '***';
const MAX_NAME = 200;
const MAX_URI = 500;

interface ConfigRow {
  /** 行の識別子（描画用）。 */
  id: number;
  key: string;
  value: string;
  /** 伏せて返ったキー（読み取り専用）。 */
  masked: boolean;
}

// 並びは文字コードの順（実行環境のロケールに依らない。localeCompare は環境で順が変わる）。
// 前提: 行の `id` は 0 からの通し番号で、`nextId` の初期値（行数）はどの既存の行とも重ならない。
//   `id` は描画の key と行の更新・削除の対象を決めるだけで、送る本文には入らない。行を削除しても番号は詰めず、
//   追加は常に `nextId` から振る（削除した番号を再利用すると、React が別の行の入力状態を引き継ぐ）。
function rowsOf(config: Record<string, string> | null | undefined): ConfigRow[] {
  return Object.entries(config ?? {})
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
    .map(([key, value], i) => ({ id: i, key, value, masked: value === MASKED }));
}

function sameConfig(a: Record<string, string>, b: Record<string, string>): boolean {
  const ka = Object.keys(a);
  return (
    ka.length === Object.keys(b).length && ka.every((k) => Object.hasOwn(b, k) && a[k] === b[k])
  );
}

/** 変わった項目だけを持つ PATCH の本文を作る。何も変わっていなければ null。純関数（試験の対象）。 */
function settingsPatch(
  source: Pick<DataSourceDto, 'name' | 'connectionUri' | 'config'>,
  name: string,
  connectionUri: string,
  config: Record<string, string>,
): PatchDataSourceRequest | null {
  const patch: PatchDataSourceRequest = {};
  if (name.trim() !== source.name) patch.name = name.trim();
  if (connectionUri.trim() !== source.connectionUri) patch.connectionUri = connectionUri.trim();
  if (!sameConfig(config, source.config ?? {})) patch.config = config;
  return Object.keys(patch).length === 0 ? null : patch;
}

export function DataSourceSettingsForm({
  source,
  submitting,
  onCancel,
  onSubmit,
}: {
  source: DataSourceDto;
  submitting: boolean;
  onCancel: () => void;
  onSubmit: (input: PatchDataSourceRequest) => void;
}) {
  const { t } = useLingui();
  const sourceName = source.name;
  const [name, setName] = useState(source.name);
  const [connectionUri, setConnectionUri] = useState(source.connectionUri);
  const [rows, setRows] = useState<ConfigRow[]>(() => rowsOf(source.config));
  const [nextId, setNextId] = useState(rows.length);
  const typeLabel = sourceTypeLabel(source.sourceType);

  // 空のキーの行は送らない（追加しかけて値を入れなかった行）。同じキーが 2 行あれば送れない。
  const filled = rows.filter((r) => r.key.trim() !== '');
  const keys = filled.map((r) => r.key.trim());
  const duplicate = keys.find((k, i) => keys.indexOf(k) !== i) ?? null;
  const config = Object.fromEntries(filled.map((r) => [r.key.trim(), r.value]));
  const patch = settingsPatch(source, name, connectionUri, config);
  const canSubmit =
    name.trim().length > 0 &&
    connectionUri.trim().length > 0 &&
    !duplicate &&
    patch !== null &&
    !submitting;

  const update = (id: number, change: Partial<ConfigRow>) =>
    setRows((rs) => rs.map((r) => (r.id === id ? { ...r, ...change } : r)));

  return (
    <Panel heading={<Trans>設定を変更: {sourceName}</Trans>}>
      <form
        aria-label={t`データソースの設定変更`}
        className="flex flex-col gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          if (!canSubmit || patch === null) return;
          onSubmit(patch);
        }}
      >
        <div>
          <Label htmlFor="ds-edit-name" requiredHint={t`（必須）`}>
            <Trans>名前</Trans>
          </Label>
          <Input
            id="ds-edit-name"
            value={name}
            maxLength={MAX_NAME}
            onChange={(e) => setName(e.target.value)}
          />
        </div>

        <div>
          <span className="text-sm font-medium">
            <Trans>種別</Trans>
          </span>{' '}
          <Tag tone="neutral">{typeof typeLabel === 'string' ? typeLabel : i18n._(typeLabel)}</Tag>
          <p className="text-xs text-fg-muted">
            <Trans>
              種別は変更できません。別の種別にするときは、新しいデータソースとして登録してください。
            </Trans>
          </p>
        </div>

        <div>
          <Label htmlFor="ds-edit-uri" requiredHint={t`（必須）`}>
            <Trans>接続先 URI</Trans>
          </Label>
          <Input
            id="ds-edit-uri"
            value={connectionUri}
            maxLength={MAX_URI}
            aria-describedby="ds-edit-uri-hint"
            onChange={(e) => setConnectionUri(e.target.value)}
          />
          <p id="ds-edit-uri-hint" className="text-xs text-fg-muted">
            <Trans>接続先だけを書きます。パスワード・トークンを URI に含めないでください。</Trans>
          </p>
        </div>

        <fieldset className="flex flex-col gap-2">
          <legend className="text-sm font-medium">
            <Trans>コネクタの設定（config）</Trans>
          </legend>
          <p className="text-xs text-fg-muted">
            <Trans>
              パスワード・トークンなどの認証情報はここに書かず、「秘密情報・接続設定の管理」画面で設定します。伏せて表示されている項目（***）は認証情報であり、ここでは変更できません。
            </Trans>
          </p>
          {rows.map((row, index) => {
            const position = index + 1;
            return (
              <div key={row.id} className="flex flex-wrap items-center gap-2">
                <Input
                  aria-label={t`設定のキー ${position}`}
                  value={row.key}
                  disabled={row.masked}
                  className="max-w-[14rem]"
                  onChange={(e) => update(row.id, { key: e.target.value })}
                />
                {row.masked ? (
                  <>
                    <code aria-label={t`設定の値 ${position}`} className="text-xs text-fg-muted">
                      {MASKED}
                    </code>
                    <Link
                      to="/admin/secrets"
                      search={{ datasource: source.id }}
                      className="text-sm text-brand hover:underline"
                    >
                      <Trans>認証情報を設定</Trans>
                    </Link>
                  </>
                ) : (
                  <>
                    <Input
                      aria-label={t`設定の値 ${position}`}
                      value={row.value}
                      className="max-w-[20rem]"
                      onChange={(e) => update(row.id, { value: e.target.value })}
                    />
                    <Button
                      type="button"
                      size="sm"
                      onClick={() => setRows((rs) => rs.filter((r) => r.id !== row.id))}
                    >
                      <Trans>削除</Trans>
                    </Button>
                  </>
                )}
              </div>
            );
          })}
          <div>
            <Button
              type="button"
              size="sm"
              onClick={() => {
                setRows((rs) => [...rs, { id: nextId, key: '', value: '', masked: false }]);
                setNextId((n) => n + 1);
              }}
            >
              <Trans>＋ 設定を追加</Trans>
            </Button>
          </div>
          {duplicate && (
            <p role="alert" className="text-xs text-danger">
              <Trans>同じキーが 2 つあります: {duplicate}</Trans>
            </p>
          )}
        </fieldset>

        <div className="flex gap-2">
          <Button type="submit" variant="primary" disabled={!canSubmit}>
            <Trans>変更を保存</Trans>
          </Button>
          <Button type="button" onClick={onCancel}>
            <Trans>キャンセル</Trans>
          </Button>
        </div>
      </form>
    </Panel>
  );
}
