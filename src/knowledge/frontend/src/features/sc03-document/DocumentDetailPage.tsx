import { useEffect, useState } from 'react';
import { Link, useParams } from '@tanstack/react-router';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';
import { appConfig } from '@foundation/config/runtimeConfig';

// SC-03, UC-01/UC-07, FR-06/FR-12: 文書詳細／プレビュー画面。正規化文書（Markdown）本文と
// メタデータ（属性・タグ・版）を表示し、出典元リンク・SC-04（Wiki）への遷移導線を提供する。
// データソースは BFF 集約（/bff/documents/{id}・/content・/versions）。ABAC はサーバ側で適用され、
// 権限外・不在はいずれも 404（存在秘匿・IADR-0009）→ UI は中立に「見つかりません」と表示する。

interface DocumentDetail {
  id: string;
  title: string;
  status: string;
  markdownUri?: string | null;
  version: number;
  attributes: Record<string, string>;
  tags: string[];
  createdAt: string;
  updatedAt: string;
}
interface DocumentContent {
  id: string;
  title: string;
  markdown: string;
  sourceUri?: string | null;
}
interface DocumentVersion {
  documentId: string;
  version: number;
  title: string;
  status: string;
  changeNote?: string | null;
  createdAt: string;
}

type Status = 'loading' | 'ok' | 'notFound' | 'error';
type ContentStatus = 'loading' | 'ok' | 'unavailable';

export function DocumentDetailPage() {
  // SC-03, IADR-0124 決定 3: パスパラメータはルート ID のリテラルを渡す形だけが厳密に型付く。
  const { id } = useParams({ from: '/_shell/docs/$id' });
  const [status, setStatus] = useState<Status>('loading');
  const [doc, setDoc] = useState<DocumentDetail | null>(null);
  const [contentStatus, setContentStatus] = useState<ContentStatus>('loading');
  const [content, setContent] = useState<DocumentContent | null>(null);
  const [versions, setVersions] = useState<DocumentVersion[]>([]);

  useEffect(() => {
    if (!id) {
      setStatus('notFound');
      return;
    }
    let active = true;

    // 詳細（メタデータ）: notFound を画面の存在秘匿ゲートとする。
    apiFetch<DocumentDetail>(`/documents/${id}`)
      .then((data) => {
        if (!active) return;
        setDoc(data);
        setStatus('ok');
      })
      .catch((e: unknown) => {
        if (!active) return;
        // 404 は不在/秘匿を区別しない（IADR-0009）。
        setStatus(e instanceof ApiError && e.kind === 'notFound' ? 'notFound' : 'error');
      });

    // 本文（Markdown）: 詳細と疎結合に取得。取得不能時はその領域のみ縮退する。
    apiFetch<DocumentContent>(`/documents/${id}/content`)
      .then((data) => {
        if (!active) return;
        setContent(data);
        setContentStatus('ok');
      })
      .catch(() => {
        if (!active) return;
        setContentStatus('unavailable');
      });

    // 版履歴: 取得不能時は空表示へ縮退する。
    apiFetch<DocumentVersion[]>(`/documents/${id}/versions`)
      .then((data) => {
        if (!active) return;
        setVersions(data ?? []);
      })
      .catch(() => {
        /* 版履歴は補助情報のため失敗しても本体表示は継続する。 */
      });

    return () => {
      active = false;
    };
  }, [id]);

  return (
    <section>
      <h1>文書詳細</h1>

      {status === 'loading' && <p role="status">読み込み中…</p>}
      {status === 'notFound' && <p>文書が見つかりませんでした。</p>}
      {status === 'error' && <p role="alert">文書の取得に失敗しました。</p>}

      {status === 'ok' && doc && (
        <>
          <MetaHeader doc={doc} />
          <SourceLinks doc={doc} content={content} />
          <ContentView status={contentStatus} content={content} />
          <VersionsView versions={versions} />
        </>
      )}
    </section>
  );
}

// ISO 8601（DateTimeOffset 由来）をロケール表記に整形する。解釈できない値はそのまま表示する。
function formatDate(value: string | null | undefined): string {
  if (!value) return '—';
  const t = Date.parse(value);
  return Number.isNaN(t) ? value : new Date(t).toLocaleString();
}

function MetaHeader({ doc }: { doc: DocumentDetail }) {
  const attrs = Object.entries(doc.attributes ?? {});
  return (
    <header
      aria-label="文書メタデータ"
      style={{ border: '1px solid #ddd', borderRadius: 8, padding: '0.5rem 0.75rem', margin: '0.75rem 0' }}
    >
      <h2 style={{ margin: '0 0 0.25rem' }}>{doc.title}</h2>
      <div>
        <small>
          状態: {doc.status}｜版: v{doc.version}｜更新: {formatDate(doc.updatedAt)}
        </small>
      </div>
      {attrs.length > 0 && (
        <div aria-label="属性">
          {attrs.map(([k, v]) => (
            <span
              key={k}
              style={{ display: 'inline-block', border: '1px solid #ccc', borderRadius: 4, padding: '0 0.4rem', margin: '0.2rem 0.2rem 0 0' }}
            >
              {k}: {v}
            </span>
          ))}
        </div>
      )}
      {doc.tags?.length > 0 && (
        <div aria-label="タグ">
          {doc.tags.map((t) => (
            <span key={t} style={{ marginRight: '0.4rem' }}>
              #{t}
            </span>
          ))}
        </div>
      )}
    </header>
  );
}

// 出典元リンク（正規化本文の格納先）と SC-04（Wiki）への遷移導線。
// markdownUri が http(s) の場合のみ外部リンク化し、storage:// 等はコード表記で参照だけ示す。
function SourceLinks({ doc, content }: { doc: DocumentDetail; content: DocumentContent | null }) {
  const { wikiBaseUrl } = appConfig();
  const sourceUri = content?.sourceUri ?? doc.markdownUri ?? null;
  const isHttp = !!sourceUri && /^https?:\/\//i.test(sourceUri);
  return (
    <p aria-label="出典元">
      出典元:{' '}
      {sourceUri ? (
        isHttp ? (
          <a href={sourceUri} target="_blank" rel="noopener noreferrer">
            {sourceUri}
          </a>
        ) : (
          <code>{sourceUri}</code>
        )
      ) : (
        <span>—</span>
      )}
      {wikiBaseUrl && (
        <>
          {' ｜ '}
          {/* SC-04: Wiki 閲覧導線（内部ルート）。閲覧範囲はゲートウェイ（ABAC）で制御される。 */}
          <Link to="/wiki">Wiki で開く</Link>
        </>
      )}
    </p>
  );
}

// 正規化 Markdown 本文。Markdown ライブラリは導入せず、原文を等幅・改行保持で安全に表示する
// （XSS を避けるため HTML 描画しない）。
function ContentView({ status, content }: { status: ContentStatus; content: DocumentContent | null }) {
  return (
    <article aria-label="本文" style={{ margin: '0.75rem 0' }}>
      <h3>本文</h3>
      {status === 'loading' && <p role="status">本文を読み込み中…</p>}
      {status === 'unavailable' && <p>本文は利用できません。</p>}
      {status === 'ok' && content && (
        <pre
          style={{
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            background: '#f7f7f7',
            border: '1px solid #eee',
            borderRadius: 6,
            padding: '0.75rem',
          }}
        >
          {content.markdown}
        </pre>
      )}
    </article>
  );
}

function VersionsView({ versions }: { versions: DocumentVersion[] }) {
  if (versions.length === 0) return null;
  return (
    <section aria-label="版履歴" style={{ margin: '0.75rem 0' }}>
      <h3>版履歴（{versions.length}）</h3>
      <table>
        <thead>
          <tr>
            <th>版</th>
            <th>状態</th>
            <th>変更メモ</th>
            <th>作成</th>
          </tr>
        </thead>
        <tbody>
          {versions.map((v) => (
            <tr key={v.version}>
              <td>v{v.version}</td>
              <td>{v.status}</td>
              <td>{v.changeNote ?? '—'}</td>
              <td>{formatDate(v.createdAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
