import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import { ErrorList } from '@foundation/ui/ErrorList';
import { toMessages } from '@foundation/ui/apiErrors';

// SC-05, UC-03, FR-06: 文書管理画面。文書の作成・更新・公開／アーカイブ・削除と属性／タグ設定を行う。
// 管理系のため platform-admin/operator 限定（ルートは RequireRole、サーバ側 /bff/documents 書き込みも同ロール）。
// 一覧・詳細は ABAC スコープ内のみ（SC-02/03 と同じ読み取り境界）。更新は楽観ロック（版不一致=409 を表示）。
// 保存→取り込み・Wiki 同期がトリガされる（公開）。詳細・版履歴は SC-03（/documents/:id）へ遷移する。

interface DocumentItem {
  id: string;
  title: string;
  status: string;
  version: number;
  attributes: Record<string, string>;
  tags: string[];
  updatedAt: string;
}

// FR-05, ADR-0004: 機密区分の許可値（AuthorizationService の属性辞書に準拠）。必須属性。
const CONFIDENTIALITY = ['public', 'internal', 'confidential', 'restricted'] as const;

function formatDate(value: string | null | undefined): string {
  if (!value) return '—';
  const t = Date.parse(value);
  return Number.isNaN(t) ? value : new Date(t).toLocaleString();
}

export function DocumentManagementPage() {
  const [items, setItems] = useState<DocumentItem[]>([]);
  const [status, setStatus] = useState<'loading' | 'ok' | 'error'>('loading');
  const [notice, setNotice] = useState<string | null>(null);
  const [editing, setEditing] = useState<DocumentItem | null>(null);

  const load = useCallback(() => {
    setStatus('loading');
    apiFetch<DocumentItem[]>('/documents')
      .then((data) => {
        setItems(data ?? []);
        setStatus('ok');
      })
      .catch(() => setStatus('error'));
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  function reportError(err: unknown, fallback: string) {
    if (err instanceof ApiError && err.status === 409) {
      // #177: 競合の詳細（Problem 本文の detail/title）があれば表示。版競合（version_conflict 本文で
      // details 空）の場合は従来の平易な文言へフォールバックし、いずれも最新を再読み込みする。
      setNotice(
        err.details.length > 0
          ? err.details.join(' ')
          : '他の更新と競合しました（版が変わっています）。最新を再読み込みしました。',
      );
      load();
      return;
    }
    // #177: 検証（400）等の詳細メッセージを SC-09 と統一して優先表示する。
    setNotice(toMessages(err, fallback).join(' '));
  }

  async function act(path: string, okMsg: string, errMsg: string) {
    setNotice(null);
    try {
      await apiFetch(path, { method: 'POST' });
      setNotice(okMsg);
      load();
    } catch (err) {
      reportError(err, errMsg);
    }
  }

  async function onDelete(id: string) {
    setNotice(null);
    try {
      await apiFetch(`/documents/${id}`, { method: 'DELETE' });
      setNotice('文書を削除しました。');
      load();
    } catch (err) {
      reportError(err, '削除に失敗しました。');
    }
  }

  return (
    <section>
      <h1>文書管理</h1>

      {editing ? (
        <EditForm
          doc={editing}
          onCancel={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            setNotice('文書を更新しました。');
            load();
          }}
          onError={(err) => {
            setEditing(null);
            reportError(err, '更新に失敗しました。');
          }}
        />
      ) : (
        <CreateForm
          onCreated={() => {
            setNotice('文書を作成しました。');
            load();
          }}
        />
      )}

      {notice && <p role="status">{notice}</p>}

      <h2>文書一覧</h2>
      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'error' && <p role="alert">文書の取得に失敗しました。</p>}
      {status === 'ok' &&
        (items.length === 0 ? (
          <p>文書はありません。</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>タイトル</th>
                <th>状態</th>
                <th>版</th>
                <th>機密区分</th>
                <th>更新</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {items.map((d) => (
                <tr key={d.id}>
                  <td>
                    {/* SC-03: 詳細・版履歴へ内部遷移 */}
                    <Link to={`/documents/${d.id}`}>{d.title}</Link>
                  </td>
                  <td>{d.status}</td>
                  <td>v{d.version}</td>
                  <td>{d.attributes?.confidentiality ?? '—'}</td>
                  <td>{formatDate(d.updatedAt)}</td>
                  <td>
                    <button type="button" onClick={() => setEditing(d)}>
                      編集
                    </button>{' '}
                    {/* SC-05 仕様: 公開は未公開状態（draft / normalized）のみ。published・archived では出さない
                        （アーカイブ済みの誤再公開を防止＝状態遷移の意図を守る。サーバも 409 で拒否）。 */}
                    {(d.status === 'draft' || d.status === 'normalized') && (
                      <button type="button" onClick={() => void act(`/documents/${d.id}/publish`, '公開しました。', '公開に失敗しました。')}>
                        公開
                      </button>
                    )}{' '}
                    {d.status !== 'archived' && (
                      <button type="button" onClick={() => void act(`/documents/${d.id}/archive`, 'アーカイブしました。', 'アーカイブに失敗しました。')}>
                        アーカイブ
                      </button>
                    )}{' '}
                    <button type="button" onClick={() => void onDelete(d.id)}>
                      削除
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ))}
    </section>
  );
}

// FR-06, UC-03: 新規作成。タイトル・機密区分（必須属性）を伴って作成する。必須未設定は保存不可。
function CreateForm({ onCreated }: { onCreated: () => void }) {
  const [title, setTitle] = useState('');
  const [confidentiality, setConfidentiality] = useState<(typeof CONFIDENTIALITY)[number]>('internal');
  const [tags, setTags] = useState('');
  // #177: 検証エラー（400）の詳細を SC-09 と統一して一覧表示する。
  const [errors, setErrors] = useState<string[]>([]);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (title.trim().length === 0) return;
    setErrors([]);
    try {
      await apiFetch('/documents', {
        method: 'POST',
        json: {
          title: title.trim(),
          attributes: { confidentiality },
          tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
        },
      });
      setTitle('');
      setTags('');
      onCreated();
    } catch (err) {
      setErrors(toMessages(err, '文書の作成に失敗しました。'));
    }
  }

  return (
    <form onSubmit={onSubmit} aria-label="文書作成">
      <h2>文書を作成</h2>
      <div>
        <label htmlFor="doc-title">タイトル（必須）</label>
        <br />
        <input id="doc-title" value={title} onChange={(e) => setTitle(e.target.value)} />
      </div>
      <div>
        <label htmlFor="doc-conf">機密区分（必須）</label>
        <br />
        <select id="doc-conf" value={confidentiality} onChange={(e) => setConfidentiality(e.target.value as typeof confidentiality)}>
          {CONFIDENTIALITY.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      </div>
      <div>
        <label htmlFor="doc-tags">タグ（カンマ区切り）</label>
        <br />
        <input id="doc-tags" value={tags} onChange={(e) => setTags(e.target.value)} />
      </div>
      <ErrorList errors={errors} />
      <button type="submit" disabled={title.trim().length === 0}>
        作成する
      </button>
    </form>
  );
}

// FR-06, UC-03: 編集（楽観ロック）。現在版を ExpectedVersion として送り、競合は 409 で検出する。
function EditForm({
  doc,
  onCancel,
  onSaved,
  onError,
}: {
  doc: DocumentItem;
  onCancel: () => void;
  onSaved: () => void;
  onError: (err: unknown) => void;
}) {
  const [title, setTitle] = useState(doc.title);
  const [confidentiality, setConfidentiality] = useState(doc.attributes?.confidentiality ?? 'internal');
  const [tags, setTags] = useState((doc.tags ?? []).join(', '));
  const [changeNote, setChangeNote] = useState('');

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (title.trim().length === 0) return;
    try {
      await apiFetch(`/documents/${doc.id}`, {
        method: 'PUT',
        json: {
          title: title.trim(),
          attributes: { ...doc.attributes, confidentiality },
          tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
          expectedVersion: doc.version,
          changeNote: changeNote.trim() || null,
        },
      });
      onSaved();
    } catch (err) {
      onError(err);
    }
  }

  return (
    <form onSubmit={onSubmit} aria-label="文書編集">
      <h2>文書を編集（v{doc.version}）</h2>
      <div>
        <label htmlFor="edit-title">タイトル（必須）</label>
        <br />
        <input id="edit-title" value={title} onChange={(e) => setTitle(e.target.value)} />
      </div>
      <div>
        <label htmlFor="edit-conf">機密区分（必須）</label>
        <br />
        <select id="edit-conf" value={confidentiality} onChange={(e) => setConfidentiality(e.target.value)}>
          {CONFIDENTIALITY.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      </div>
      <div>
        <label htmlFor="edit-tags">タグ（カンマ区切り）</label>
        <br />
        <input id="edit-tags" value={tags} onChange={(e) => setTags(e.target.value)} />
      </div>
      <div>
        <label htmlFor="edit-note">変更メモ</label>
        <br />
        <input id="edit-note" value={changeNote} onChange={(e) => setChangeNote(e.target.value)} />
      </div>
      <button type="submit" disabled={title.trim().length === 0}>
        保存する
      </button>{' '}
      <button type="button" onClick={onCancel}>
        キャンセル
      </button>
    </form>
  );
}
