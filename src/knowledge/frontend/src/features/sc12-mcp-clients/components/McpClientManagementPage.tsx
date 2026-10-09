import { useCallback, useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import type { MessageDescriptor } from '@lingui/core';
import { i18n } from '@foundation/i18n';
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
  Textarea,
} from '@platform/ui';
import { appConfig } from '@foundation/config/runtimeConfig';
import { notify } from '@foundation/ui/notifications';
import { QueryState } from '@foundation/ui/QueryState';
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
  clientAuthLabel,
  clientKindLabel,
  requiresAttributes,
} from '../types/mcpClientVocabulary';
import type { ClientKind, RegistrationIssue } from '../types/mcpClientVocabulary';
import { useMcpClientRegistrationForm } from '../hooks/useMcpClientRegistrationForm';
import { useMcpClientAttributeEditor } from '../hooks/useMcpClientAttributeEditor';
import { useIssuedClientSecret } from '../hooks/useIssuedClientSecret';

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
// ■ 🔴 **部分集合の判定は後段だけが持つ**（ADR-0062 決定 2・3。従前の「上限を超えない」という
//   順序の語は撤回された —— 07_abac-attribute-model は序数比較を排除しており「上限」を定義できない）。
//   本画面は値域の絞り込み（定義済みの属性・許可値のみ）までを担い、**割り当てられるかを事前に
//   示さない。** 画面が判定しても API を直接叩けば素通しになり、「検証がある」と読める UI は
//   検証が無いことより悪い。代わりに**後段が名指しした「外れた値」をそのまま描く**（同 §結果）。
//   身元の口（`/bff/auth/me`）は `clearance` / `department` を返さない（同 決定 4）。
//
// ■ 実装していない要素は画面仕様書の §計画との対応 に「一部する／しない」で理由つきで記録した。

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
    'redirect-uris-required': t`有人にはリダイレクト URI が 1 件以上必要です。`,
    'redirect-uris-too-many': t`リダイレクト URI は 10 件以下にしてください。`,
    'redirect-uri-invalid': t`リダイレクト URI は https か、ループバックの http://127.0.0.1 / http://[::1] に限ります（ワイルドカード・フラグメント・localhost は使えません）。`,
    'redirect-uri-loopback-port-required': t`ループバックのリダイレクト URI には port を明示してください（例: http://127.0.0.1:53123/callback）。`,
    'redirect-uri-duplicate': t`リダイレクト URI が重複しています。`,
  };
}

export function McpClientManagementPage() {
  const { t } = useLingui();
  const clients = useMcpClients();
  const tools = useEffectiveTools();
  const dictionary = useAbacAttributeDictionary();
  const actions = useMcpClientActions();
  const issueLabels = useIssueLabels();

  // SC-12 / IADR-0341: 下書き（クライアント状態）は `hooks/` に在る。画面はそれを描くだけで、
  // 「キーを選び直すと値が消える」「同じキーは後勝ち」「登録後も種別は残す」といった遷移の規則は
  // フック側に閉じており、画面を描かずに固定してある（`hooks/*.test.ts`）。
  const form = useMcpClientRegistrationForm(dictionary.data ?? []);
  // FR-16, UC-09, SC-12「無人アカウントの ABAC 属性割当」: **登録後の差し替え**の対象。
  // 🔴 後段には差し替えの端点が在るのに、画面から呼ぶ経路が無かった —— 登録時にしか
  // 属性を置けず、機密区分を打ち間違えたら**クライアントを消して作り直す**しかなかった。
  // 「後段 API はあるが画面から呼ばれない＝使えない」は本画面が閉じにきた欠陥そのものであり、
  // それを 1 経路で再演していた（AI レビューが検出）。
  const editor = useMcpClientAttributeEditor();
  // 列定義（`useMemo`）から呼ぶので、**参照の固定してある関数だけ**を取り出して依存に置く
  // （`editor` ごと依存に入れるとフックの戻り値は毎描画で新しく、列定義が作り直される）。
  const { start: startEditingAttributes } = editor;

  // SC-12（2026-10-09 補完）・#1845: 無人の client secret は登録・再発行の応答で一度だけ受け取り、**この画面のローカル状態にだけ**持つ。
  // 閉じたとき・次の操作を始めたときに捨てる（再表示の手段は無い）。
  // 🔴 捨てるのは表示だけではない —— 応答を受け取った変更（mutation）の結果にも同じ secret が載っている。
  // 表示を捨てるたびに変更も `reset()` して、クエリのメモリに写しを残さない（#1845 の独立監査）。
  // **送信中の変更は捨てない** —— 捨てると応答の後始末（表示）が呼ばれず、再発行では旧 secret だけが失効して
  // 新しい値を誰も見られない状態になる。
  const registerPending = actions.register.isPending;
  const reissuePending = actions.reissueSecret.isPending;
  const { reset: resetRegister } = actions.register;
  const { reset: resetReissue } = actions.reissueSecret;
  const discardSecretResponses = useCallback(() => {
    if (!registerPending) resetRegister();
    if (!reissuePending) resetReissue();
  }, [registerPending, reissuePending, resetRegister, resetReissue]);
  const {
    issued: issuedSecret,
    show: showIssuedSecret,
    clear: clearIssuedSecret,
  } = useIssuedClientSecret(discardSecretResponses);
  // 再発行は旧 secret を即時に失効させるので、確認を挟む（対象の clientId。確認していないときは null）。
  const [confirmingReissue, setConfirmingReissue] = useState<string | null>(null);
  // 翻訳文へ差し込む値は単純な変数で渡す（`lingui/no-expression-in-message`）。
  const issuedClientId = issuedSecret?.clientId ?? '';
  const reissueTargetId = confirmingReissue ?? '';

  const definitions = form.definitions;

  const rows = useMemo(() => clients.data ?? [], [clients.data]);

  // 呼び出し監査ログは可観測性基盤（ログ集約）に在る。**SPA 側に監査ログ画面は無い。**
  // ［#1845］本画面の管理操作（登録・差し替え・無効化・再有効化・secret の発行と再発行）の監査記録も同じログ基盤に在る。
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
            <p className="text-xs text-fg-muted">{row.original.clientId}</p>
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
          <span className="text-xs text-fg-muted">
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
            <span className="text-xs text-fg-muted">
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
                onClick={() => {
                  clearIssuedSecret();
                  actions.disable.mutate({ clientId: row.original.clientId });
                }}
              >
                <Trans>無効化</Trans>
              </Button>
            ) : (
              <Button
                size="sm"
                onClick={() => {
                  clearIssuedSecret();
                  actions.enable.mutate({ clientId: row.original.clientId });
                }}
              >
                <Trans>再有効化</Trans>
              </Button>
            )}
            {/* #1845: client secret の再発行は無人だけ（有人は公開クライアントで secret を持たない）。確認を挟む。 */}
            {requiresAttributes(row.original.kind) && (
              <Button
                variant="secondary"
                size="sm"
                // 🔴 送信中は押させない。応答を待つ間に再び確認まで進めると要求が 2 本飛び、先に表示された secret が
                // 後の再発行で黙って失効する（#1845 の独立監査）。
                disabled={reissuePending}
                onClick={() => {
                  clearIssuedSecret();
                  setConfirmingReissue(row.original.clientId);
                }}
              >
                <Trans>secret を再発行</Trans>
              </Button>
            )}
            {/* 有人には出さない。属性は利用者本人のもので解決され、割り当てる対象が無い。 */}
            {requiresAttributes(row.original.kind) && (
              <Button
                variant="secondary"
                size="sm"
                onClick={() => {
                  clearIssuedSecret();
                  startEditingAttributes(row.original.clientId, row.original.attributes);
                }}
              >
                <Trans>属性を変更</Trans>
              </Button>
            )}
          </div>
        ),
      },
    ],
    // `startEditingAttributes` は `useCallback` で参照が固定してあるので、依存に入れても列定義は
    // 毎描画で作り直されない（従前ここは eslint-disable で規則を止めていた。IADR-0341）。
    [t, actions.disable, actions.enable, startEditingAttributes, clearIssuedSecret, reissuePending],
  );

  const submit = () => {
    // 送信中は 2 本目を送らない（同じ clientId の二重登録。無人なら 2 本目の応答の secret が先の表示を上書きする）。
    if (registerPending) return;
    // 有人には属性を送らない（送る値が無いのが正しい）—— 本文の組み立ては form.body() が持つ。
    if (!form.validate()) return;
    clearIssuedSecret();
    actions.register.mutate(
      { data: form.body() },
      {
        onSuccess: (response) => {
          form.resetAfterRegister();
          // 無人の 201 だけが client secret を一度だけ含む（有人は null）。
          if (response.status === 201 && response.data.clientSecret) {
            showIssuedSecret({
              clientId: response.data.clientId,
              secret: response.data.clientSecret,
              origin: 'issued',
            });
          }
        },
      },
    );
  };

  const confirmReissue = () => {
    // 送信中は 2 本目を送らない（ボタンの無効化と二重に塞ぐ）。
    if (confirmingReissue === null || reissuePending) return;
    const clientId = confirmingReissue;
    setConfirmingReissue(null);
    actions.reissueSecret.mutate(
      { clientId },
      {
        onSuccess: (response) => {
          if (response.status === 200) {
            showIssuedSecret({
              clientId: response.data.clientId,
              secret: response.data.clientSecret,
              origin: 'reissued',
            });
          }
        },
      },
    );
  };

  const copySecret = (secret: string) => {
    // 結果は notify で伝える。**失敗も伝える**（非 HTTPS・権限拒否で reject する。黙って何も起きないのが最も分かりにくい）。
    navigator.clipboard.writeText(secret).then(
      () => notify.success(t`コピーしました`),
      () => notify.error(t`コピーできませんでした`),
    );
  };

  return (
    <section className="space-y-6">
      <div>
        {/* モックの `.ttl` / `.sub`。 */}
        <h1 className="text-[17px] font-medium text-fg">
          <Trans>MCP クライアント登録管理</Trans>
        </h1>
        <p className="text-xs text-fg-muted" data-testid="mcp-help">
          <Trans>
            MCP サーバーへ接続できるクライアント（外部 AI エージェント）を登録・無効化し、
            無人アカウントへ ABAC
            属性を割り当てます。公開ツールの構成はこの画面からは変更できません。
          </Trans>
        </p>
      </div>

      <div>
        <h2 className="mb-2 text-sm font-medium text-fg-muted">
          <Trans>監査ログ（呼び出し・管理操作）</Trans>
        </h2>
        {auditLogUrl ? (
          <a
            href={auditLogUrl}
            target="_blank"
            rel="noreferrer"
            className="text-sm text-brand hover:underline"
            data-testid="audit-log-link"
          >
            <Trans>ログ基盤で監査ログを見る ↗</Trans>
          </a>
        ) : (
          // 🔴 **無いリンクを描かない。** 導線が未設定であることと、記録が残っている場所は書く。
          <p className="text-sm text-fg-muted" data-testid="audit-log-unavailable">
            <Trans>
              監査ログの参照先が未設定です。ツールの呼び出しと本画面の管理操作はすべてログ基盤へ記録されています（client
              secret の値は記録しません）。
            </Trans>
          </p>
        )}
      </div>

      <Panel heading={t`登録クライアント`}>
        {/* 🔴 待ち・失敗・空・本体は `QueryState` が 1 か所で描き分ける（判定順 isError → isPending →
            isEmpty → 本体）。**空の一覧へ縮退しない** ——「1 件も登録が無い」と「一覧が引けない」は
            別の意味であり、後者を前者に見せると管理者は重複登録へ進む。 */}
        <QueryState
          query={clients}
          isEmpty={(list) => list.length === 0}
          empty={
            <EmptyState
              title={t`登録されたクライアントはありません。`}
              description={t`下の「クライアント登録」から、接続する外部 AI エージェントを登録してください。`}
            />
          }
          errorTitle={t`登録クライアントを取得できませんでした。`}
          errorDescription={toMessages(clients.error, '').join(' / ') || undefined}
        >
          {() => (
            <DataTable
              caption={t`登録された MCP クライアントの一覧`}
              sortHint={t`並べ替え`}
              columns={columns}
              data={rows}
            />
          )}
        </QueryState>
      </Panel>

      {/* #1845: 再発行の確認。旧 secret はただちに使えなくなる（猶予は無い）。 */}
      {confirmingReissue !== null && (
        <Alert
          tone="warning"
          role="alertdialog"
          label={t`client secret の再発行`}
          data-testid="reissue-confirmation"
        >
          <span className="flex flex-col gap-2">
            <span>
              <Trans>
                {reissueTargetId} の client secret を再発行します。いまの secret
                はただちに使えなくなります。エージェントの設定を新しい secret に差し替えてください。
              </Trans>
            </span>
            <span className="flex gap-2">
              <Button variant="danger" size="sm" disabled={reissuePending} onClick={confirmReissue}>
                <Trans>再発行する</Trans>
              </Button>
              <Button variant="secondary" size="sm" onClick={() => setConfirmingReissue(null)}>
                <Trans>取消</Trans>
              </Button>
            </span>
          </span>
        </Alert>
      )}

      {actions.reissueSecret.isError && (
        <Alert tone="danger" role="alert" label={t`エラー`} data-testid="reissue-error">
          {toMessages(
            actions.reissueSecret.error,
            t`client secret を再発行できませんでした。`,
          ).join(' / ')}
        </Alert>
      )}

      {/* 🔴 #1845: 平文の client secret が現れるのはここだけである。**再表示できない**旨を同じ枠の中に置く。 */}
      {issuedSecret && (
        <Alert
          tone="success"
          role="status"
          label={issuedSecret.origin === 'issued' ? t`発行しました` : t`再発行しました`}
          data-testid="issued-secret"
        >
          <span className="flex flex-col gap-2">
            <span>
              <Trans>
                {issuedClientId} の client secret
                です。表示できるのは今回だけです。閉じると再表示できません（再発行のみ可能です）。
              </Trans>
            </span>
            <code
              className="break-all rounded bg-surface-muted p-2 text-xs"
              data-testid="issued-secret-value"
            >
              {issuedSecret.secret}
            </code>
            <span className="flex gap-2">
              <Button size="sm" onClick={() => copySecret(issuedSecret.secret)}>
                <Trans>コピー</Trans>
              </Button>
              <Button variant="secondary" size="sm" onClick={clearIssuedSecret}>
                <Trans>閉じる</Trans>
              </Button>
            </span>
          </span>
        </Alert>
      )}

      {/* FR-16, UC-09, SC-12: 登録後の ABAC 属性の差し替え。**置換であって追加ではない** ——
          後段の端点が属性の集合ごと入れ替えるので、画面も現在値を読み込んでから編集させる。 */}
      {editor.editingClientId !== null && (
        <Panel heading={t`ABAC 属性の変更`} data-testid="attribute-edit">
          <p className="text-xs text-fg-muted" data-testid="attribute-edit-target">
            {editor.editingClientId}
          </p>
          <p className="mt-1 text-xs text-fg-muted">
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
                value={editor.key}
                onChange={(e) => editor.selectKey(e.target.value)}
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
                value={editor.value}
                onChange={(e) => editor.setValue(e.target.value)}
              >
                <option value="">{t`選択してください`}</option>
                {(definitions.find((d) => d.key === editor.key)?.allowedValues ?? []).map(
                  (value) => (
                    <option key={value} value={value}>
                      {value}
                    </option>
                  ),
                )}
              </Select>
            </div>
            <Button size="sm" onClick={editor.addEntry}>
              <Trans>属性を追加</Trans>
            </Button>
          </div>
          <ul className="mt-2 text-xs" data-testid="attribute-edit-entries">
            {editor.entries.map((entry) => (
              <li key={entry.key} className="flex items-center gap-2">
                <span>{`${entry.key}: ${entry.value}`}</span>
                <Button variant="ghost" size="sm" onClick={() => editor.removeEntry(entry.key)}>
                  <Trans>削除</Trans>
                </Button>
              </li>
            ))}
          </ul>
          {/* 🔴 空で保存させない。無人アカウントに属性が 1 つも無い状態は、登録時に
              禁じているのと同じ理由（判定軸が消える）で作らせてはならない。 */}
          {!editor.canSave && (
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
              disabled={!editor.canSave}
              onClick={() =>
                actions.replaceAttributes.mutate(
                  {
                    clientId: editor.editingClientId as string,
                    data: { attributes: editor.attributes() },
                  },
                  { onSuccess: editor.close },
                )
              }
            >
              <Trans>保存</Trans>
            </Button>
            <Button variant="secondary" size="sm" onClick={editor.close}>
              <Trans>取消</Trans>
            </Button>
          </div>
        </Panel>
      )}

      {/* モックの `.g2`: 左が登録フォーム、右が公開ツール一覧。 */}
      <div className="grid gap-n3 lg:grid-cols-2">
        <Panel heading={t`クライアント登録`} className="mb-0">
          <div className="flex flex-wrap items-end gap-4">
            <div>
              <Label htmlFor="mcp-client-id">
                <Trans>クライアント ID</Trans>
              </Label>
              <Input
                id="mcp-client-id"
                value={form.clientId}
                onChange={(e) => form.setClientId(e.target.value)}
              />
            </div>
            <div>
              <Label htmlFor="mcp-display-name">
                <Trans>表示名</Trans>
              </Label>
              <Input
                id="mcp-display-name"
                value={form.displayName}
                onChange={(e) => form.setDisplayName(e.target.value)}
              />
            </div>
            <div>
              <Label htmlFor="mcp-kind">
                <Trans>クライアント種別</Trans>
              </Label>
              <Select
                id="mcp-kind"
                selectSize="sm"
                value={form.kind}
                onChange={(e) => form.setKind(e.target.value as ClientKind)}
              >
                {CLIENT_KINDS.map((option) => (
                  <option key={option} value={option}>
                    {`${labelOf(clientKindLabel(option))}（${labelOf(clientAuthLabel(option))}）`}
                  </option>
                ))}
              </Select>
            </div>
          </div>

          {/*
            有人のときだけリダイレクト URI の入力を出す（05_screens §SC-12 の入力表・ADR-0134 決定 1）。
            後段は認証基盤に公開クライアント（PKCE S256・完全一致）を作る。無人には送らない。
          */}
          {form.needsRedirectUris && (
            <div className="mt-3" data-testid="redirect-uris">
              <Label htmlFor="mcp-redirect-uris">
                <Trans>リダイレクト URI（1 行に 1 件）</Trans>
              </Label>
              <Textarea
                id="mcp-redirect-uris"
                rows={3}
                value={form.redirectUrisText}
                onChange={(e) => form.setRedirectUrisText(e.target.value)}
                placeholder="http://127.0.0.1:53123/callback"
              />
              <p className="mt-1 text-xs text-fg-muted">
                <Trans>
                  完全一致で照合します。https か、ループバックの http://127.0.0.1 / http://[::1]
                  に限ります（ワイルドカードは使えません）。ループバックは port
                  を明示してください（例: http://127.0.0.1:53123/callback）。
                </Trans>
              </p>
            </div>
          )}

          {/* 無人のときだけ属性の入力を出す。**有人では要求しない**（05_screens §SC-12）。 */}
          {form.needsAttributes && (
            <div className="mt-3" data-testid="attribute-assignment">
              <p className="text-xs text-fg-muted">
                <Trans>
                  無人（サービスアカウント）には ABAC
                  属性の割当が必須です。選べるのは定義済みの属性と その許可値だけです。
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
                    value={form.attributeKey}
                    onChange={(e) => form.selectAttributeKey(e.target.value)}
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
                    value={form.attributeValue}
                    onChange={(e) => form.setAttributeValue(e.target.value)}
                  >
                    <option value="">{t`選択してください`}</option>
                    {(form.selectedDefinition?.allowedValues ?? []).map((value) => (
                      <option key={value} value={value}>
                        {value}
                      </option>
                    ))}
                  </Select>
                </div>
                <Button size="sm" onClick={form.addEntry}>
                  <Trans>属性を追加</Trans>
                </Button>
              </div>
              <ul className="mt-2 text-xs" data-testid="attribute-entries">
                {form.entries.map((entry) => (
                  <li key={entry.key}>{`${entry.key}: ${entry.value}`}</li>
                ))}
              </ul>
            </div>
          )}

          {form.issues.length > 0 && (
            <Alert
              tone="warning"
              role="alert"
              label={t`入力を確認してください`}
              className="mt-3"
              data-testid="registration-issues"
            >
              {form.issues.map((issue) => issueLabels[issue]).join(' / ')}
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

          <Button variant="primary" className="mt-3" disabled={registerPending} onClick={submit}>
            <Trans>登録</Trans>
          </Button>
        </Panel>

        <Panel heading={t`公開ツール一覧（実効構成の参照）`} className="mb-0">
          {/* 🔴 **変更の入口を置かない。** 常に出す固定文言で、変更経路が Git であることを示す。
            モックの `.note`（区画内の注記）へ寄せる。 */}
          <Note data-testid="tools-readonly-notice">
            <Trans>
              公開ツールはこの画面からは変更できません。公開範囲は許可リスト方式で管理しており、
              変更は Git 上の公開構成を更新して反映します。
            </Trans>
          </Note>
          <QueryState
            query={tools}
            isEmpty={(view) => view.tools.length === 0}
            empty={
              <EmptyState
                title={t`公開されているツールはありません。`}
                description={t`公開範囲は Git 上の公開構成で定義します。許可リストへ追加してください。`}
              />
            }
            errorTitle={t`公開ツール一覧を取得できませんでした。`}
            errorDescription={toMessages(tools.error, '').join(' / ') || undefined}
          >
            {(view) => (
              <>
                {/* 一覧は**表**にする（モックは縦並びだが、名前とサービスの 2 項目を持つため
                  列見出しのある表のほうが読み手に速い）。**参照専用**で操作の列は置かない。 */}
                <Table data-testid="published-tools">
                  <TableCaption>{t`公開ツールの一覧`}</TableCaption>
                  <TableHead>
                    <TableRow>
                      <TableHeaderCell>
                        <Trans>ツール</Trans>
                      </TableHeaderCell>
                      <TableHeaderCell>
                        <Trans>提供サービス</Trans>
                      </TableHeaderCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {view.tools.map((tool) => (
                      <TableRow key={tool.name}>
                        <TableCell className="font-medium">{tool.name}</TableCell>
                        <TableCell className="text-xs text-fg-muted">{tool.service}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                {view.drifts.length > 0 && (
                  // ADR-0024 §5: 申告と許可リストの食い違い。**握り潰さない** ——
                  // 「公開されているつもりの公開されていない」を人が気付ける唯一の出口である。
                  <Alert
                    tone="warning"
                    label={t`構成のずれ`}
                    className="mt-n2"
                    data-testid="tool-drifts"
                  >
                    {view.drifts
                      .map((drift) => `${drift.kind} / ${drift.target}: ${drift.detail}`)
                      .join(' / ')}
                  </Alert>
                )}
              </>
            )}
          </QueryState>
        </Panel>
      </div>
    </section>
  );
}
