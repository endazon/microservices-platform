import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';

// SC-09, UC-05, FR-09: 管理者設定（ABAC）画面。属性辞書・アクセスポリシーを管理する（platform-admin 限定）。
// データソースは BFF 集約（/bff/admin/authz/*）。保存前検証（矛盾・構文）は AuthorizationService が行い、
// 400 の詳細を画面へ表示する（IADR-0040）。属性が参照中の削除は 409 で拒否される（IADR-0006）。

interface AttributeDef {
  id: string;
  key: string;
  label: string;
  allowedValues: string[];
  required: boolean;
  scope: string;
}
interface Policy {
  id: string;
  name: string;
  action: string;
  userConditions: Record<string, string[]>;
  documentConditions: Record<string, string[]>;
  isActive: boolean;
}

const ATTR = '/admin/authz/attributes';
const POL = '/admin/authz/policies';
const ACTIONS = ['read', 'analyze', 'manage'] as const;
const SCOPES = ['document', 'user'] as const;

export function AdminAbacSettingsPage() {
  const [attributes, setAttributes] = useState<AttributeDef[]>([]);
  const [policies, setPolicies] = useState<Policy[]>([]);
  const [status, setStatus] = useState<'loading' | 'ok' | 'error'>('loading');

  const load = useCallback(() => {
    setStatus('loading');
    Promise.all([apiFetch<AttributeDef[]>(ATTR), apiFetch<Policy[]>(POL)])
      .then(([attrs, pols]) => {
        setAttributes(attrs ?? []);
        setPolicies(pols ?? []);
        setStatus('ok');
      })
      .catch(() => setStatus('error'));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  return (
    <section>
      <h1>管理者設定（ABAC）</h1>
      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'error' && <p role="alert">設定の取得に失敗しました。</p>}
      {status === 'ok' && (
        <>
          <AttributeSection attributes={attributes} onChanged={load} />
          <PolicySection policies={policies} onChanged={load} />
        </>
      )}
    </section>
  );
}

// FR-09: 検証エラー（400）・競合（409）の詳細を一覧表示する共通部品。
function Errors({ errors }: { errors: string[] }) {
  if (errors.length === 0) return null;
  return (
    <ul role="alert" style={{ color: '#b00' }}>
      {errors.map((e, i) => (
        <li key={i}>{e}</li>
      ))}
    </ul>
  );
}

// ---- 属性辞書 ----
function AttributeSection({ attributes, onChanged }: { attributes: AttributeDef[]; onChanged: () => void }) {
  const [key, setKey] = useState('');
  const [label, setLabel] = useState('');
  const [allowedValues, setAllowedValues] = useState('');
  const [required, setRequired] = useState(false);
  const [scope, setScope] = useState<(typeof SCOPES)[number]>('document');
  const [errors, setErrors] = useState<string[]>([]);

  async function onCreate(e: React.FormEvent) {
    e.preventDefault();
    setErrors([]);
    try {
      await apiFetch(ATTR, {
        method: 'POST',
        json: {
          key: key.trim(),
          label: label.trim(),
          allowedValues: allowedValues.split(',').map((v) => v.trim()).filter(Boolean),
          required,
          scope,
        },
      });
      setKey('');
      setLabel('');
      setAllowedValues('');
      onChanged();
    } catch (err) {
      setErrors(toMessages(err, '属性辞書の登録に失敗しました。'));
    }
  }

  async function onDelete(id: string) {
    setErrors([]);
    try {
      await apiFetch(`${ATTR}/${id}`, { method: 'DELETE' });
      onChanged();
    } catch (err) {
      // 参照中の属性は 409（IADR-0006）。理由を表示する。
      setErrors(toMessages(err, '属性辞書の削除に失敗しました。'));
    }
  }

  return (
    <section aria-label="属性辞書">
      <h2>属性辞書</h2>
      <table>
        <thead>
          <tr>
            <th>キー</th>
            <th>ラベル</th>
            <th>スコープ</th>
            <th>許可値</th>
            <th>必須</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          {attributes.map((a) => (
            <tr key={a.id}>
              <td>{a.key}</td>
              <td>{a.label}</td>
              <td>{a.scope}</td>
              <td>{a.allowedValues.join(' / ')}</td>
              <td>{a.required ? '必須' : '任意'}</td>
              <td>
                {/* 対象を含む aria-label で取り違え・アクセシビリティを堅牢にする。 */}
                <button type="button" aria-label={`属性を削除: ${a.key}`} onClick={() => void onDelete(a.id)}>
                  削除
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <form onSubmit={onCreate} aria-label="属性辞書登録">
        <h3>属性を追加</h3>
        <div>
          <label htmlFor="attr-key">キー（必須）</label>
          <br />
          <input id="attr-key" value={key} onChange={(e) => setKey(e.target.value)} />
        </div>
        <div>
          <label htmlFor="attr-label">ラベル</label>
          <br />
          <input id="attr-label" value={label} onChange={(e) => setLabel(e.target.value)} />
        </div>
        <div>
          <label htmlFor="attr-values">許可値（カンマ区切り）</label>
          <br />
          <input id="attr-values" value={allowedValues} onChange={(e) => setAllowedValues(e.target.value)} />
        </div>
        <div>
          <label htmlFor="attr-scope">スコープ</label>
          <br />
          <select id="attr-scope" value={scope} onChange={(e) => setScope(e.target.value as typeof scope)}>
            {SCOPES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </div>
        <label>
          <input type="checkbox" checked={required} onChange={(e) => setRequired(e.target.checked)} /> 必須
        </label>
        <Errors errors={errors} />
        <button type="submit" disabled={key.trim().length === 0}>
          追加する
        </button>
      </form>
    </section>
  );
}

// ---- ポリシー ----
function PolicySection({ policies, onChanged }: { policies: Policy[]; onChanged: () => void }) {
  const [name, setName] = useState('');
  const [action, setAction] = useState<(typeof ACTIONS)[number]>('read');
  const [userConditions, setUserConditions] = useState('{}');
  const [documentConditions, setDocumentConditions] = useState('{}');
  const [errors, setErrors] = useState<string[]>([]);

  async function onCreate(e: React.FormEvent) {
    e.preventDefault();
    setErrors([]);
    // 条件は {key: [値,…]} 形式の JSON。構文エラーはローカルで検出する（矛盾検証はサーバ側）。
    let userCond: unknown;
    let docCond: unknown;
    try {
      userCond = JSON.parse(userConditions || '{}');
      docCond = JSON.parse(documentConditions || '{}');
    } catch {
      setErrors(['条件は正しい JSON（{"key":["値"]} 形式）で入力してください。']);
      return;
    }
    try {
      await apiFetch(POL, {
        method: 'POST',
        json: { name: name.trim(), action, userConditions: userCond, documentConditions: docCond },
      });
      setName('');
      setUserConditions('{}');
      setDocumentConditions('{}');
      onChanged();
    } catch (err) {
      setErrors(toMessages(err, 'ポリシーの登録に失敗しました。'));
    }
  }

  async function onToggleActive(p: Policy) {
    setErrors([]);
    try {
      await apiFetch(`${POL}/${p.id}/active`, { method: 'PATCH', json: { isActive: !p.isActive } });
      onChanged();
    } catch (err) {
      setErrors(toMessages(err, 'ポリシーの状態変更に失敗しました。'));
    }
  }

  async function onDelete(id: string) {
    setErrors([]);
    try {
      await apiFetch(`${POL}/${id}`, { method: 'DELETE' });
      onChanged();
    } catch (err) {
      setErrors(toMessages(err, 'ポリシーの削除に失敗しました。'));
    }
  }

  return (
    <section aria-label="アクセスポリシー">
      <h2>アクセスポリシー</h2>
      <table>
        <thead>
          <tr>
            <th>名前</th>
            <th>アクション</th>
            <th>利用者条件</th>
            <th>文書条件</th>
            <th>状態</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          {policies.map((p) => (
            <tr key={p.id}>
              <td>{p.name}</td>
              <td>{p.action}</td>
              <td>
                <code>{JSON.stringify(p.userConditions)}</code>
              </td>
              <td>
                <code>{JSON.stringify(p.documentConditions)}</code>
              </td>
              <td>{p.isActive ? '有効' : '無効'}</td>
              <td>
                <button type="button" aria-label={`ポリシーを${p.isActive ? '無効化' : '有効化'}: ${p.name}`} onClick={() => void onToggleActive(p)}>
                  {p.isActive ? '無効化' : '有効化'}
                </button>{' '}
                <button type="button" aria-label={`ポリシーを削除: ${p.name}`} onClick={() => void onDelete(p.id)}>
                  削除
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <form onSubmit={onCreate} aria-label="ポリシー登録">
        <h3>ポリシーを追加</h3>
        <div>
          <label htmlFor="pol-name">名前（必須）</label>
          <br />
          <input id="pol-name" value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <div>
          <label htmlFor="pol-action">アクション</label>
          <br />
          <select id="pol-action" value={action} onChange={(e) => setAction(e.target.value as typeof action)}>
            {ACTIONS.map((a) => (
              <option key={a} value={a}>
                {a}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="pol-user">利用者条件（JSON）</label>
          <br />
          <textarea id="pol-user" value={userConditions} onChange={(e) => setUserConditions(e.target.value)} rows={2} />
        </div>
        <div>
          <label htmlFor="pol-doc">文書条件（JSON）</label>
          <br />
          <textarea id="pol-doc" value={documentConditions} onChange={(e) => setDocumentConditions(e.target.value)} rows={2} />
        </div>
        <Errors errors={errors} />
        <button type="submit" disabled={name.trim().length === 0}>
          追加する
        </button>
      </form>
    </section>
  );
}

// FR-09: ApiError の検証詳細（矛盾・構文）を優先して表示。詳細が無ければ既定文言へフォールバック。
function toMessages(err: unknown, fallback: string): string[] {
  if (err instanceof ApiError && err.details.length > 0) return err.details;
  if (err instanceof ApiError) return [err.message];
  return [fallback];
}
