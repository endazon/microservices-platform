import { useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Alert, Button, EmptyState, Label, Note, Panel, Select, StatusBadge } from '@platform/ui';
import { appConfig } from '@foundation/config/runtimeConfig';
import { QueryState } from '@foundation/ui/QueryState';
import { toMessages } from '@foundation/utils/apiErrors';
import type { PlatformUserDto } from '@foundation/api/generated/bff.schemas';
import { ConfirmDialog } from '../../../components/ConfirmDialog';
import { DataTable } from '../../../components/DataTable';
import type { DataTableColumns } from '../../../components/DataTable';
import {
  useAbacAttributeDictionary,
  useAssignableRoles,
  useUserAccountActions,
  useUserAccounts,
} from '../api/useUserAccounts';
import {
  DEPARTMENT_KEY,
  assignableAttributes,
  departmentsInUse,
  filterUsers,
  optionalAttributes,
  requiredAttributes,
} from '../types/userAccountVocabulary';
import type { AssignmentIssue } from '../types/userAccountVocabulary';
import { useUserPermissionEditor } from '../hooks/useUserPermissionEditor';

// SC-17, UC-05, FR-05, FR-09, ADR-0026: ユーザーアカウント管理（05_screens: ルート /admin/users）。
//
// ■ 到達できるのは **platform-admin のみ**（05_screens §共通シェル「SC-09・SC-12・SC-17 =
//   システム管理者」）。ガードはルート側（RequireRole → NotFound。存在秘匿）にあり、
//   サーバ側も BFF・後段の二重ゲートで AdminOnly を強制する。
//
// ■ 🔴 **新規作成のフォームを置かない。**
//   05_screens §SC-17 アクション:「アカウントは人事システム連携で自動プロビジョニングし、
//   退職者は連携により自動で無効化され全セッションが即時失効する（**本画面から新規作成はしない**）」。
//   不在は `UserAccountManagementPage.test.tsx` が陽性対照つきで固定する。
//
// ■ ロール・属性の値域は**引く**（画面に焼き込まない）。ロールは認可基盤から、属性値は
//   SC-09 の属性辞書から。焼き込むと、増やしても選べず、消えた値を選べてしまう。
//
// ■ 状態は色だけで意味を持たせない（StatusBadge が色 ＋ アイコン ＋ テキストを強制する）。
//   無効の表示は計画の文言そのまま **「無効（全セッション失効）」** とする ——
//   「無効」だけだと、既存セッションが生き残るのか失効するのかが読めない。
//
// ■ 実装していない要素は画面仕様書の §計画との対応 に「一部する／しない」で理由つきで記録した。

/** 入力規則の識別子 → 表示文言。**語彙側は文言を持たない**ので画面が写す。 */
function useIssueLabels(): Record<AssignmentIssue, string> {
  const { t } = useLingui();
  return {
    'roles-required': t`ロールは 1 件以上を割り当ててください（権限を外すときは無効化を使います）。`,
    'required-attribute-missing': t`部門と機密区分上限は必須です。`,
  };
}

export function UserAccountManagementPage() {
  const { t } = useLingui();
  const users = useUserAccounts();
  const roles = useAssignableRoles();
  const dictionary = useAbacAttributeDictionary();
  const actions = useUserAccountActions();
  const issueLabels = useIssueLabels();

  // 絞り込みは画面に残す（SC-17 / IADR-0341 決定 3）。**規則を持たない** —— `useState` 2 本と、
  // 既に純関数として在る `filterUsers()` の呼び出しだけであり、フックへ出しても
  // 呼び出し元が 1 つしかない間接層が増えるだけになる。
  const [departmentFilter, setDepartmentFilter] = useState('');
  const [roleFilter, setRoleFilter] = useState('');
  // 🔴 **無効化は取り返しがつかない**（全セッションが即座に失効し、作業中の利用者が落ちる）。
  // 押した瞬間に走らせず、確認ダイアログを 1 枚挟む（SC-19 / SC-20 と同じ `ConfirmDialog`。
  // 初期フォーカスは取消側で、Escape で降りられる）。**再有効化には挟まない** ——
  // 失効しているものを戻す操作であり、取り返しがつく。
  const [confirmingDisable, setConfirmingDisable] = useState(false);

  const rows = useMemo(() => users.data ?? [], [users.data]);
  // SC-17 / IADR-0341: 権限編集の下書き（クライアント状態）は `hooks/` に在る。
  // 「対象が変わったときだけ引き直す」「任意属性を空へ戻すとキーごと落ちる」といった遷移の規則は
  // フック側に閉じており、画面を描かずに固定してある（`hooks/useUserPermissionEditor.test.ts`）。
  const editor = useUserPermissionEditor(rows);
  const editing = editor.editing;
  // 列定義（`useMemo`）から呼ぶので、**参照の固定してある関数だけ**を取り出して依存に置く
  // （`editor` ごと依存に入れるとフックの戻り値は毎描画で新しく、列定義が作り直される）。
  const { open: openEditor } = editor;
  const assignableRoles = useMemo(() => roles.data ?? [], [roles.data]);
  const definitions = useMemo(() => assignableAttributes(dictionary.data ?? []), [dictionary.data]);
  const required = useMemo(() => requiredAttributes(definitions), [definitions]);
  const optional = useMemo(() => optionalAttributes(definitions), [definitions]);

  const visible = useMemo(
    () => filterUsers(rows, { department: departmentFilter, role: roleFilter }),
    [rows, departmentFilter, roleFilter],
  );
  const departments = useMemo(() => departmentsInUse(rows), [rows]);
  // Lingui のマクロは `${変数}` しか受け取らない（`${obj.prop}` は抽出時に壊れる）ので先に畳む。
  const editingName = editing?.displayName ?? '';
  const editorHeading = t`権限編集 — ${editingName}`;

  // 監査ログの参照先はログ基盤（可観測性基盤）に在る。**SPA 側に監査ログ画面は無い。**
  // 接続先はビルドへ焼き込まず実行時 config から取り、未設定なら導線を出さず所在を文言で示す
  // （SC-12 の外部ツール導線と同じ作法。存在しないリンクを描かない）。
  const auditLogUrl = appConfig().opsLinks.grafanaUrl;

  const columns: DataTableColumns<PlatformUserDto> = useMemo(
    () => [
      {
        id: 'user',
        accessorKey: 'displayName',
        header: t`ユーザー`,
        cell: ({ row }) => (
          <div>
            <span>{row.original.displayName}</span>
            <p className="text-xs text-fg-muted">{row.original.username}</p>
          </div>
        ),
      },
      {
        id: 'department',
        header: t`部門`,
        enableSorting: false,
        // 部門は ABAC 属性そのものである（列として複写しない）。
        cell: ({ row }) => row.original.attributes[DEPARTMENT_KEY] ?? '—',
      },
      {
        id: 'roles',
        header: t`ロール`,
        enableSorting: false,
        // 併任は「・」で連ねる（計画の例「管理者・運用者」に合わせる）。
        cell: ({ row }) => (row.original.roles.length > 0 ? row.original.roles.join('・') : '—'),
      },
      {
        id: 'attributes',
        header: t`ABAC属性`,
        enableSorting: false,
        cell: ({ row }) => (
          <ul className="text-xs">
            {Object.entries(row.original.attributes).map(([key, value]) => (
              <li key={key}>{`${key}: ${value}`}</li>
            ))}
          </ul>
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
            // 05_screens §SC-17 の表示文言そのまま。「無効」だけでは既存セッションの扱いが読めない。
            <StatusBadge tone="danger">{t`無効（全セッション失効）`}</StatusBadge>
          ),
      },
      {
        id: 'operation',
        header: t`操作`,
        enableSorting: false,
        cell: ({ row }) => (
          <Button size="sm" onClick={() => openEditor(row.original.id)}>
            <Trans>編集</Trans>
          </Button>
        ),
      },
    ],
    // `openEditor` は `useCallback` で参照が固定してあるので、依存に入れても列定義は
    // 毎描画で作り直されない（IADR-0341）。
    [t, openEditor],
  );

  const save = () => {
    if (!editing) return;
    if (!editor.validate(definitions)) return;

    // 🔴 **2 本の要求に分かれる**（ロールと属性は別の反映先を持つ）。片方だけ通る余地があるため、
    // 画面側で先に検証してから送る。**中間状態は隠さない** —— どちらが失敗したかは
    // 各 mutation のエラーとして下に出る。
    actions.replaceRoles.mutate({ userId: editing.id, data: { roles: editor.draftRoles } });
    actions.replaceAttributes.mutate({
      userId: editing.id,
      data: { attributes: editor.draftAttributes },
    });
  };

  return (
    <section className="space-y-6">
      <div>
        {/* モックの `.ttl` / `.sub`。副題がモックの「プロビジョニング・監査」区画の内容
            （自動作成・定義済み値のみ・即時反映・退職者の自動無効化）を兼ねる ——
            同じ断りを 1 画面に 2 度置かない。 */}
        <h1 className="text-[17px] font-medium text-fg">
          <Trans>ユーザーアカウント管理</Trans>
        </h1>
        <p className="text-xs text-fg-muted" data-testid="users-help">
          <Trans>
            利用者のロール割当・ABAC
            属性割当・アカウントの無効化を行います。アカウントは人事システム
            連携で自動的に作成・更新され、この画面からは作成できません。退職者は連携により自動で
            無効化され、全セッションが失効します。
          </Trans>
        </p>
      </div>

      <Panel heading={t`監査ログ`}>
        {auditLogUrl ? (
          <a
            href={auditLogUrl}
            target="_blank"
            rel="noreferrer"
            className="text-sm text-brand hover:underline"
            data-testid="audit-log-link"
          >
            <Trans>ログ基盤で権限変更の監査ログを見る ↗</Trans>
          </a>
        ) : (
          // 🔴 **無いリンクを描かない。** 導線が未設定であることと、記録の所在は書く。
          <p className="text-sm text-fg-muted" data-testid="audit-log-unavailable">
            <Trans>
              監査ログの参照先が未設定です。権限変更とアカウント無効化は認可基盤の管理イベントとして
              記録されています。
            </Trans>
          </p>
        )}
      </Panel>

      <Panel heading={t`ユーザーアカウント`}>
        <div className="mb-3 flex flex-wrap items-end gap-4" data-testid="user-filters">
          <div>
            <Label htmlFor="user-filter-department">
              <Trans>部門</Trans>
            </Label>
            <Select
              id="user-filter-department"
              selectSize="sm"
              value={departmentFilter}
              onChange={(e) => setDepartmentFilter(e.target.value)}
            >
              <option value="">{t`すべて`}</option>
              {departments.map((department) => (
                <option key={department} value={department}>
                  {department}
                </option>
              ))}
            </Select>
          </div>
          <div>
            <Label htmlFor="user-filter-role">
              <Trans>ロール</Trans>
            </Label>
            <Select
              id="user-filter-role"
              selectSize="sm"
              value={roleFilter}
              onChange={(e) => setRoleFilter(e.target.value)}
            >
              <option value="">{t`すべて`}</option>
              {assignableRoles.map((role) => (
                <option key={role} value={role}>
                  {role}
                </option>
              ))}
            </Select>
          </div>
        </div>

        {/* 🔴 待ち・失敗・空・本体は `QueryState` が 1 か所で描き分ける（判定順 isError → isPending →
            isEmpty → 本体）。**空の一覧へ縮退しない** ——「1 人も居ない」と「一覧が引けない」は
            別の意味である。
            🔴 **空の判定は絞り込み後で行う**（絞り込みが全部を落としても 0 件として正しく描く）。
            **ただし文言は分ける** —— 「1 人も居ない」と「絞り込みに一致しない」では次の一手が違う。 */}
        <QueryState
          query={users}
          isEmpty={(list) =>
            filterUsers(list, { department: departmentFilter, role: roleFilter }).length === 0
          }
          empty={
            rows.length === 0 ? (
              <EmptyState
                title={t`利用者はまだ登録されていません。`}
                description={t`アカウントは人事システム連携で自動的に作成されます。連携の稼働を確認してください。`}
              />
            ) : (
              <EmptyState
                title={t`該当する利用者はいません。`}
                description={t`部門・ロールの絞り込みを「すべて」に戻すと全件が出ます。`}
              />
            )
          }
          errorTitle={t`利用者一覧を取得できませんでした。`}
          errorDescription={toMessages(users.error, '').join(' / ') || undefined}
        >
          {() => (
            <DataTable
              caption={t`利用者アカウントの一覧`}
              sortHint={t`並べ替え`}
              columns={columns}
              data={visible}
            />
          )}
        </QueryState>
      </Panel>

      {editing && (
        <Panel heading={editorHeading} data-testid="permission-editor">
          <fieldset className="mb-n3">
            <legend className="text-xs text-fg-muted">
              <Trans>ロール割当（必須・複数選択可）</Trans>
            </legend>
            {assignableRoles.map((role) => (
              <label key={role} className="mr-4 inline-flex items-center gap-1 text-sm">
                <input
                  type="checkbox"
                  checked={editor.draftRoles.includes(role)}
                  onChange={() => editor.toggleRole(role)}
                />
                {role}
              </label>
            ))}
          </fieldset>

          <div className="flex flex-wrap items-end gap-4">
            {required.map((definition) => (
              <div key={definition.key}>
                <Label htmlFor={`user-attr-${definition.key}`}>{`${definition.label} *`}</Label>
                <Select
                  id={`user-attr-${definition.key}`}
                  selectSize="sm"
                  value={editor.draftAttributes[definition.key] ?? ''}
                  onChange={(e) => editor.setAttribute(definition.key, e.target.value)}
                >
                  <option value="">{t`選択してください`}</option>
                  {definition.allowedValues.map((value) => (
                    <option key={value} value={value}>
                      {value}
                    </option>
                  ))}
                </Select>
              </div>
            ))}
            {/* 任意属性（計画の「タグ」）。**必須にしない。** */}
            {optional.map((definition) => (
              <div key={definition.key}>
                <Label htmlFor={`user-attr-${definition.key}`}>{definition.label}</Label>
                <Select
                  id={`user-attr-${definition.key}`}
                  selectSize="sm"
                  value={editor.draftAttributes[definition.key] ?? ''}
                  onChange={(e) => editor.setAttribute(definition.key, e.target.value)}
                >
                  <option value="">{t`指定しない`}</option>
                  {definition.allowedValues.map((value) => (
                    <option key={value} value={value}>
                      {value}
                    </option>
                  ))}
                </Select>
              </div>
            ))}
          </div>

          {editor.issues.length > 0 && (
            <Alert
              tone="warning"
              role="alert"
              label={t`入力を確認してください`}
              className="mt-3"
              data-testid="assignment-issues"
            >
              {editor.issues.map((issue) => issueLabels[issue]).join(' / ')}
            </Alert>
          )}

          {(actions.replaceRoles.isError || actions.replaceAttributes.isError) && (
            // 後段の拒否理由（RFC7807）をそのまま出す。**中立化しない** ——
            // 「辞書外の値」「定義済みでないロール」等、管理者が直せる情報である。
            <Alert
              tone="danger"
              role="alert"
              label={t`エラー`}
              className="mt-3"
              data-testid="assignment-error"
            >
              {[
                ...toMessages(actions.replaceRoles.error, ''),
                ...toMessages(actions.replaceAttributes.error, ''),
              ]
                .filter((message) => message.length > 0)
                .join(' / ')}
            </Alert>
          )}

          <div className="mt-3 flex flex-wrap gap-2">
            <Button variant="primary" onClick={save}>
              <Trans>保存</Trans>
            </Button>
            {editing.enabled ? (
              // 押した瞬間には走らせない。下の ConfirmDialog で 1 度受ける。
              <Button variant="danger" onClick={() => setConfirmingDisable(true)}>
                <Trans>無効化（全セッション失効）</Trans>
              </Button>
            ) : (
              <Button onClick={() => actions.enable.mutate({ userId: editing.id })}>
                <Trans>再有効化</Trans>
              </Button>
            )}
            <Button onClick={editor.close}>
              <Trans>閉じる</Trans>
            </Button>
          </div>

          <Note data-testid="editor-notes">
            <Trans>
              保存すると認可基盤へ反映され、認可判定に即座に効きます。属性に選べるのは定義済みの値
              だけです。無効化すると、その利用者の全セッションが即座に失効します。
            </Trans>
          </Note>
        </Panel>
      )}

      {/* 05_screens §SC-17: 無効化は**全セッションの即時失効**を伴う。取り返しがつかないので、
          実行の手前に確認を 1 枚挟む（`destructive` で実行ボタンを danger にし、
          文言でも何が起きるかを書く。色だけに意味を載せない）。 */}
      {editing && confirmingDisable && (
        <ConfirmDialog
          title={t`このアカウントを無効化しますか？`}
          confirmLabel={t`無効化する`}
          cancelLabel={t`やめる`}
          destructive
          pending={actions.disable.isPending}
          onConfirm={() => {
            actions.disable.mutate({ userId: editing.id });
            setConfirmingDisable(false);
          }}
          onCancel={() => setConfirmingDisable(false)}
        >
          <p>
            <Trans>
              「{editingName}
              」のアカウントを無効化します。その利用者の全セッションが即座に失効し、作業中の画面もその場で使えなくなります。
            </Trans>
          </p>
          <p>
            <Trans>再有効化はこの画面から行えます（ロールと属性の割当は保持されます）。</Trans>
          </p>
        </ConfirmDialog>
      )}
    </section>
  );
}
