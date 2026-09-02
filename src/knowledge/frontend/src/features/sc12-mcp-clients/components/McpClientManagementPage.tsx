import { useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import type { MessageDescriptor } from '@lingui/core';
import { i18n } from '@foundation/i18n';
import { Alert, Button, Input, Label, Select, StatusBadge } from '@platform/ui';
import { appConfig } from '@foundation/config/runtimeConfig';
import { toMessages } from '@foundation/utils/apiErrors';
import type { McpClientView } from '@foundation/api/generated/bff.schemas';
import { DataTable } from '../../../components/DataTable';
import type { DataTableColumns } from '../../../components/DataTable';
import {
  useAbacAttributeDictionary,
  useEffectiveTools,
  useMcpClientActions,
  useMcpClients,
} from '../api/useMcpClients';
import {
  CLIENT_KINDS,
  assignableAttributes,
  buildAttributes,
  clientAuthLabel,
  clientKindLabel,
  requiresAttributes,
  validateRegistration,
} from '../types/mcpClientVocabulary';
import type { AttributeEntry, ClientKind, RegistrationIssue } from '../types/mcpClientVocabulary';

// SC-12, UC-09, FR-16, ADR-0024: MCP クライアント登録管理（05_screens: ルート /admin/mcp-clients）。
//
// ■ 到達できるのは **platform-admin のみ**（05_screens §共通シェル「SC-09・SC-12・SC-17 =
//   システム管理者」）。ガードはルート側（RequireRole → NotFound。存在秘匿）にあり、
//   サーバ側も BFF・後段の二重ゲートで AdminOnly を強制する。
//
// ■ 🔴 **公開ツールを編集する UI を置かない。**
//   05_screens §SC-12 アクション:「公開ツールの変更は本画面から直接行わず、Git 経由の公開構成変更へ
//   誘導する（許可リスト方式・GitOps）」。一覧は**参照だけ**で、変更の入口は文言で示す。
//   不在は `McpClientManagementPage.test.tsx` が陽性対照つきで固定する。
//
// ■ 属性の値域は**辞書から引く**（画面に焼き込まない）。焼き込むと辞書を増やしても選べず、
//   消えた値を選べてしまう。
//
// ■ 状態は色だけで意味を持たせない（StatusBadge が色 ＋ アイコン ＋ テキストを強制する）。
//
// ■ 実装していない要素は画面仕様書の §計画との対応 に「一部する／しない」で理由つきで記録した。
//   とくに「有人アカウントの上限を超えない」は**契約が登録者の上限を返さないため実装できない**
//   （画面側だけの見せかけの検証を置かない。信頼できない検証は無いより悪い）。

/**
 * 語彙側の表示名を文字列へ解決する。
 *
 * 語彙関数は**未知の値を生値のまま返す**（`—`・「不明」へ丸めない）ため、戻り値は
 * `MessageDescriptor | string` の union である。`i18n._` はこの union を受け取れないので畳む。
 */
function labelOf(label: MessageDescriptor | string): string {
  return typeof label === 'string' ? label : i18n._(label);
}

/** 入力規則の識別子 → 表示文言。**語彙側は文言を持たない**ので画面が写す。 */
function useIssueLabels(): Record<RegistrationIssue, string> {
  const { t } = useLingui();
  return {
    'client-id-required': t`クライアント ID は必須です。`,
    'display-name-required': t`表示名は必須です。`,
    'attributes-required': t`無人（サービスアカウント）には ABAC 属性の割当が必須です。`,
  };
}

export function McpClientManagementPage() {
  const { t } = useLingui();
  const clients = useMcpClients();
  const tools = useEffectiveTools();
  const dictionary = useAbacAttributeDictionary();
  const actions = useMcpClientActions();
  const issueLabels = useIssueLabels();

  const [clientId, setClientId] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [kind, setKind] = useState<ClientKind>('interactive');
  const [attributeKey, setAttributeKey] = useState('');
  const [attributeValue, setAttributeValue] = useState('');
  const [entries, setEntries] = useState<AttributeEntry[]>([]);
  const [issues, setIssues] = useState<RegistrationIssue[]>([]);
  // FR-16, UC-09, SC-12「無人アカウントの ABAC 属性割当」: **登録後の差し替え**の対象。
  // 🔴 後段には差し替えの端点が在るのに、画面から呼ぶ経路が無かった —— 登録時にしか
  // 属性を置けず、機密区分を打ち間違えたら**クライアントを消して作り直す**しかなかった。
  // 「後段 API はあるが画面から呼ばれない＝使えない」は本画面が閉じにきた欠陥そのものであり、
  // それを 1 経路で再演していた（AI レビューが検出）。
  const [editingClientId, setEditingClientId] = useState<string | null>(null);
  const [editEntries, setEditEntries] = useState<AttributeEntry[]>([]);
  const [editKey, setEditKey] = useState('');
  const [editValue, setEditValue] = useState('');

  const definitions = useMemo(() => assignableAttributes(dictionary.data ?? []), [dictionary.data]);
  const selectedDefinition = definitions.find((d) => d.key === attributeKey);

  const rows = useMemo(() => clients.data ?? [], [clients.data]);

  // 呼び出し監査ログは可観測性基盤（ログ集約）に在る。**SPA 側に監査ログ画面は無い。**
  // 接続先はビルドへ焼き込まず実行時 config から取り、未設定なら導線を出さず所在を文言で示す
  // （SC-10 の外部ツール導線と同じ作法。存在しないリンクを描かない）。
  const auditLogUrl = appConfig().opsLinks.grafanaUrl;

  const columns: DataTableColumns<McpClientView> = useMemo(
    () => [
      {
        id: 'client',
        accessorKey: 'displayName',
        header: t`クライアント`,
        cell: ({ row }) => (
          <div>
            <span>{row.original.displayName}</span>
            <p className="text-xs text-[--color-fg-muted]">{row.original.clientId}</p>
          </div>
        ),
      },
      {
        id: 'kind',
        accessorKey: 'kind',
        header: t`種別`,
        cell: ({ row }) => labelOf(clientKindLabel(row.original.kind)),
      },
      {
        id: 'auth',
        header: t`認証`,
        enableSorting: false,
        cell: ({ row }) => (
          <span className="text-xs text-[--color-fg-muted]">
            {labelOf(clientAuthLabel(row.original.kind))}
          </span>
        ),
      },
      {
        id: 'attributes',
        header: t`ABAC属性（無人のみ）`,
        enableSorting: false,
        // 有人は利用者本人の属性で解決される。**空欄にせず、そう書く** ——
        // 空欄だと「割り当て忘れ」と読める。
        cell: ({ row }) =>
          requiresAttributes(row.original.kind) ? (
            <ul className="text-xs">
              {Object.entries(row.original.attributes).map(([key, value]) => (
                <li key={key}>{`${key}: ${value}`}</li>
              ))}
            </ul>
          ) : (
            <span className="text-xs text-[--color-fg-muted]">
              <Trans>利用者の属性で解決</Trans>
            </span>
          ),
      },
      {
        id: 'state',
        accessorKey: 'enabled',
        header: t`状態`,
        cell: ({ row }) =>
          row.original.enabled ? (
            <StatusBadge tone="success">{t`有効`}</StatusBadge>
          ) : (
            // 無効化は**次の呼び出しから即座に**効く。状態名だけだと「いつから効くのか」が読めない。
            <StatusBadge tone="danger">{t`無効（即時接続拒否）`}</StatusBadge>
          ),
      },
      {
        id: 'operation',
        header: t`操作`,
        enableSorting: false,
        cell: ({ row }) => (
          <div className="flex flex-wrap gap-2">
            {row.original.enabled ? (
              <Button
                variant="danger"
                size="sm"
                onClick={() => actions.disable.mutate({ clientId: row.original.clientId })}
              >
                <Trans>無効化</Trans>
              </Button>
            ) : (
              <Button
                size="sm"
                onClick={() => actions.enable.mutate({ clientId: row.original.clientId })}
              >
                <Trans>再有効化</Trans>
              </Button>
            )}
            {/* 有人には出さない。属性は利用者本人のもので解決され、割り当てる対象が無い。 */}
            {requiresAttributes(row.original.kind) && (
              <Button
                variant="secondary"
                size="sm"
                onClick={() =>
                  startEditingAttributes(row.original.clientId, row.original.attributes)
                }
              >
                <Trans>属性を変更</Trans>
              </Button>
            )}
          </div>
        ),
      },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- startEditingAttributes は
    // setState だけを呼ぶ安定した関数である（依存に入れると列定義が毎描画で作り直される）。
    [t, actions.disable, actions.enable],
  );

  // 一覧の行から差し替えを始める。**現在の値を初期値として読み込む** ——
  // 空から始めると「変更しなかった属性が消える」（差し替えは置換であって追加ではない）。
  const startEditingAttributes = (target: string, current: Record<string, string>) => {
    setEditingClientId(target);
    setEditEntries(Object.entries(current).map(([key, value]) => ({ key, value })));
    setEditKey('');
    setEditValue('');
  };

  const addEntry = () => {
    if (!attributeKey || !attributeValue) return;
    setEntries((prev) => [
      ...prev.filter((e) => e.key !== attributeKey),
      { key: attributeKey, value: attributeValue },
    ]);
    setAttributeValue('');
  };

  const submit = () => {
    const found = validateRegistration({ clientId, displayName, kind, attributes: entries });
    setIssues(found);
    if (found.length > 0) return;

    actions.register.mutate(
      {
        data: {
          clientId: clientId.trim(),
          displayName: displayName.trim(),
          kind,
          // 有人には属性を送らない（送る値が無いのが正しい）。
          ...(requiresAttributes(kind) ? { attributes: buildAttributes(entries) } : {}),
        },
      },
      {
        onSuccess: () => {
          setClientId('');
          setDisplayName('');
          setEntries([]);
        },
      },
    );
  };

  return (
    <section className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold text-[--color-fg]">
          <Trans>MCP クライアント登録管理</Trans>
        </h1>
        <p className="text-xs text-[--color-fg-muted]" data-testid="mcp-help">
          <Trans>
            MCP サーバーへ接続できるクライアント（外部 AI エージェント）を登録・無効化し、
            無人アカウントへ ABAC
            属性を割り当てます。公開ツールの構成はこの画面からは変更できません。
          </Trans>
        </p>
      </div>

      <div>
        <h2 className="mb-2 text-sm font-medium text-[--color-fg-muted]">
          <Trans>呼び出し監査ログ</Trans>
        </h2>
        {auditLogUrl ? (
          <a
            href={auditLogUrl}
            target="_blank"
            rel="noreferrer"
            className="text-sm text-[--color-brand] hover:underline"
            data-testid="audit-log-link"
          >
            <Trans>ログ基盤で呼び出し監査ログを見る ↗</Trans>
          </a>
        ) : (
          // 🔴 **無いリンクを描かない。** 導線が未設定であることと、記録が残っている場所は書く。
          <p className="text-sm text-[--color-fg-muted]" data-testid="audit-log-unavailable">
            <Trans>
              監査ログの参照先が未設定です。ツールの呼び出しはすべてログ基盤へ記録されています。
            </Trans>
          </p>
        )}
      </div>

      <div>
        <h2 className="mb-2 text-sm font-medium text-[--color-fg-muted]">
          <Trans>登録クライアント</Trans>
        </h2>
        {clients.isError ? (
          // 🔴 **空の一覧へ縮退しない。**「1 件も登録が無い」と「一覧が引けない」は別の意味である。
          <Alert tone="danger" role="alert" label={t`エラー`} data-testid="clients-error">
            {toMessages(clients.error, t`登録クライアントを取得できませんでした。`).join(' / ')}
          </Alert>
        ) : clients.isPending ? (
          <p className="text-sm text-[--color-fg-muted]" data-testid="clients-loading">
            <Trans>読み込み中です。</Trans>
          </p>
        ) : rows.length === 0 ? (
          <p className="text-sm text-[--color-fg-muted]" data-testid="clients-empty">
            <Trans>登録されたクライアントはありません。</Trans>
          </p>
        ) : (
          <DataTable
            caption={t`登録された MCP クライアントの一覧`}
            sortHint={t`並べ替え`}
            columns={columns}
            data={rows}
          />
        )}
      </div>

      {/* FR-16, UC-09, SC-12: 登録後の ABAC 属性の差し替え。**置換であって追加ではない** ——
          後段の端点が属性の集合ごと入れ替えるので、画面も現在値を読み込んでから編集させる。 */}
      {editingClientId !== null && (
        <div data-testid="attribute-edit">
          <h2 className="mb-2 text-sm font-medium text-[--color-fg-muted]">
            <Trans>ABAC 属性の変更</Trans>
          </h2>
          <p className="text-xs text-[--color-fg-muted]" data-testid="attribute-edit-target">
            {editingClientId}
          </p>
          <p className="mt-1 text-xs text-[--color-fg-muted]">
            <Trans>
              保存すると属性はここに並んでいる内容で置き換わります。残したい属性は消さないでください。
            </Trans>
          </p>
          <div className="mt-2 flex flex-wrap items-end gap-4">
            <div>
              <Label htmlFor="mcp-edit-attribute-key">
                <Trans>属性</Trans>
              </Label>
              <Select
                id="mcp-edit-attribute-key"
                selectSize="sm"
                value={editKey}
                onChange={(e) => {
                  setEditKey(e.target.value);
                  setEditValue('');
                }}
              >
                <option value="">{t`選択してください`}</option>
                {definitions.map((definition) => (
                  <option key={definition.id} value={definition.key}>
                    {definition.label}
                  </option>
                ))}
              </Select>
            </div>
            <div>
              <Label htmlFor="mcp-edit-attribute-value">
                <Trans>値</Trans>
              </Label>
              <Select
                id="mcp-edit-attribute-value"
                selectSize="sm"
                value={editValue}
                onChange={(e) => setEditValue(e.target.value)}
              >
                <option value="">{t`選択してください`}</option>
                {(definitions.find((d) => d.key === editKey)?.allowedValues ?? []).map((value) => (
                  <option key={value} value={value}>
                    {value}
                  </option>
                ))}
              </Select>
            </div>
            <Button
              size="sm"
              onClick={() => {
                if (!editKey || !editValue) return;
                setEditEntries((prev) => [
                  ...prev.filter((e) => e.key !== editKey),
                  { key: editKey, value: editValue },
                ]);
                setEditValue('');
              }}
            >
              <Trans>属性を追加</Trans>
            </Button>
          </div>
          <ul className="mt-2 text-xs" data-testid="attribute-edit-entries">
            {editEntries.map((entry) => (
              <li key={entry.key} className="flex items-center gap-2">
                <span>{`${entry.key}: ${entry.value}`}</span>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => setEditEntries((prev) => prev.filter((e) => e.key !== entry.key))}
                >
                  <Trans>削除</Trans>
                </Button>
              </li>
            ))}
          </ul>
          {/* 🔴 空で保存させない。無人アカウントに属性が 1 つも無い状態は、登録時に
              禁じているのと同じ理由（判定軸が消える）で作らせてはならない。 */}
          {editEntries.length === 0 && (
            <Alert
              tone="warning"
              role="alert"
              label={t`入力を確認してください`}
              className="mt-3"
              data-testid="attribute-edit-empty"
            >
              {issueLabels['attributes-required']}
            </Alert>
          )}
          {actions.replaceAttributes.isError && (
            <Alert
              tone="danger"
              role="alert"
              label={t`エラー`}
              className="mt-3"
              data-testid="attribute-edit-error"
            >
              {toMessages(
                actions.replaceAttributes.error,
                t`ABAC 属性を変更できませんでした。`,
              ).join(' / ')}
            </Alert>
          )}
          <div className="mt-3 flex gap-2">
            <Button
              size="sm"
              disabled={editEntries.length === 0}
              onClick={() =>
                actions.replaceAttributes.mutate(
                  {
                    clientId: editingClientId,
                    data: { attributes: buildAttributes(editEntries) },
                  },
                  { onSuccess: () => setEditingClientId(null) },
                )
              }
            >
              <Trans>保存</Trans>
            </Button>
            <Button variant="secondary" size="sm" onClick={() => setEditingClientId(null)}>
              <Trans>取消</Trans>
            </Button>
          </div>
        </div>
      )}

      <div>
        <h2 className="mb-2 text-sm font-medium text-[--color-fg-muted]">
          <Trans>クライアント登録</Trans>
        </h2>
        <div className="flex flex-wrap items-end gap-4">
          <div>
            <Label htmlFor="mcp-client-id">
              <Trans>クライアント ID</Trans>
            </Label>
            <Input
              id="mcp-client-id"
              value={clientId}
              onChange={(e) => setClientId(e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="mcp-display-name">
              <Trans>表示名</Trans>
            </Label>
            <Input
              id="mcp-display-name"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="mcp-kind">
              <Trans>クライアント種別</Trans>
            </Label>
            <Select
              id="mcp-kind"
              selectSize="sm"
              value={kind}
              onChange={(e) => setKind(e.target.value as ClientKind)}
            >
              {CLIENT_KINDS.map((option) => (
                <option key={option} value={option}>
                  {`${labelOf(clientKindLabel(option))}（${labelOf(clientAuthLabel(option))}）`}
                </option>
              ))}
            </Select>
          </div>
        </div>

        {/* 無人のときだけ属性の入力を出す。**有人では要求しない**（05_screens §SC-12）。 */}
        {requiresAttributes(kind) && (
          <div className="mt-3" data-testid="attribute-assignment">
            <p className="text-xs text-[--color-fg-muted]">
              <Trans>
                無人（サービスアカウント）には ABAC 属性の割当が必須です。選べるのは定義済みの属性と
                その許可値だけです。
              </Trans>
            </p>
            <div className="mt-2 flex flex-wrap items-end gap-4">
              <div>
                <Label htmlFor="mcp-attribute-key">
                  <Trans>属性</Trans>
                </Label>
                <Select
                  id="mcp-attribute-key"
                  selectSize="sm"
                  value={attributeKey}
                  onChange={(e) => {
                    setAttributeKey(e.target.value);
                    setAttributeValue('');
                  }}
                >
                  <option value="">{t`選択してください`}</option>
                  {definitions.map((definition) => (
                    <option key={definition.id} value={definition.key}>
                      {definition.label}
                    </option>
                  ))}
                </Select>
              </div>
              <div>
                <Label htmlFor="mcp-attribute-value">
                  <Trans>値</Trans>
                </Label>
                <Select
                  id="mcp-attribute-value"
                  selectSize="sm"
                  value={attributeValue}
                  onChange={(e) => setAttributeValue(e.target.value)}
                >
                  <option value="">{t`選択してください`}</option>
                  {(selectedDefinition?.allowedValues ?? []).map((value) => (
                    <option key={value} value={value}>
                      {value}
                    </option>
                  ))}
                </Select>
              </div>
              <Button size="sm" onClick={addEntry}>
                <Trans>属性を追加</Trans>
              </Button>
            </div>
            <ul className="mt-2 text-xs" data-testid="attribute-entries">
              {entries.map((entry) => (
                <li key={entry.key}>{`${entry.key}: ${entry.value}`}</li>
              ))}
            </ul>
          </div>
        )}

        {issues.length > 0 && (
          <Alert
            tone="warning"
            role="alert"
            label={t`入力を確認してください`}
            className="mt-3"
            data-testid="registration-issues"
          >
            {issues.map((issue) => issueLabels[issue]).join(' / ')}
          </Alert>
        )}

        {actions.register.isError && (
          // 後段の拒否理由（RFC7807）をそのまま出す。**中立化しない** ——
          // 「無人アカウントへ個人資料を読ませる属性割当は禁止」等、管理者が直せる情報である。
          <Alert
            tone="danger"
            role="alert"
            label={t`エラー`}
            className="mt-3"
            data-testid="registration-error"
          >
            {toMessages(actions.register.error, t`クライアントを登録できませんでした。`).join(
              ' / ',
            )}
          </Alert>
        )}

        <Button variant="primary" className="mt-3" onClick={submit}>
          <Trans>登録</Trans>
        </Button>
      </div>

      <div>
        <h2 className="mb-2 text-sm font-medium text-[--color-fg-muted]">
          <Trans>公開ツール一覧（実効構成の参照）</Trans>
        </h2>
        {/* 🔴 **変更の入口を置かない。** 常に出す固定文言で、変更経路が Git であることを示す。 */}
        <p className="text-xs text-[--color-fg-muted]" data-testid="tools-readonly-notice">
          <Trans>
            公開ツールはこの画面からは変更できません。公開範囲は許可リスト方式で管理しており、
            変更は Git 上の公開構成を更新して反映します。
          </Trans>
        </p>
        {tools.isError ? (
          <Alert tone="danger" role="alert" label={t`エラー`} data-testid="tools-error">
            {toMessages(tools.error, t`公開ツール一覧を取得できませんでした。`).join(' / ')}
          </Alert>
        ) : tools.isPending ? (
          <p className="text-sm text-[--color-fg-muted]" data-testid="tools-loading">
            <Trans>読み込み中です。</Trans>
          </p>
        ) : (
          <>
            <ul className="mt-2 text-sm" data-testid="published-tools">
              {tools.data?.tools.map((tool) => (
                <li key={tool.name}>
                  <span className="font-medium">{tool.name}</span>
                  <span className="text-xs text-[--color-fg-muted]">{` — ${tool.service}`}</span>
                </li>
              ))}
            </ul>
            {(tools.data?.drifts.length ?? 0) > 0 && (
              // ADR-0024 §5: 申告と許可リストの食い違い。**握り潰さない** ——
              // 「公開されているつもりの公開されていない」を人が気付ける唯一の出口である。
              <Alert
                tone="warning"
                label={t`構成のずれ`}
                className="mt-2"
                data-testid="tool-drifts"
              >
                {(tools.data?.drifts ?? [])
                  .map((drift) => `${drift.kind} / ${drift.target}: ${drift.detail}`)
                  .join(' / ')}
              </Alert>
            )}
          </>
        )}
      </div>
    </section>
  );
}
