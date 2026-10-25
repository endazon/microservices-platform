// NFR / FR-05 / SC-09 / IADR-0631（#2032 / E-52）: live-rw の管理リソースの試験の**前後で、既存のポリシーと辞書に差分が無い**ことを
// 確かめるための写し（読み取りだけ）。
//
// - 読むもの: ABAC ポリシー・属性辞書・タグ辞書・関係の型の辞書（いずれも e2e-admin が読める GET）。
// - 🔴 比べないもの: 名前が `e2e-` で始まる行（試験が作る・消す行）と、件数の項目（`usageCount` など。文書を登録すると
//   グラフや索引が数え直すことがあり、辞書の中身ではない）。並びは ID で揃える。
import { expect, type APIRequestContext } from '@playwright/test';

const SOURCES: { name: string; path: string; rows: (body: unknown) => unknown[] }[] = [
  { name: 'ABAC ポリシー', path: '/bff/admin/authz/policies', rows: (b) => b as unknown[] },
  { name: '属性辞書', path: '/bff/admin/authz/attributes', rows: (b) => b as unknown[] },
  { name: 'タグ辞書', path: '/bff/tags', rows: (b) => (b as { tags: unknown[] }).tags },
  { name: '関係の型の辞書', path: '/bff/edge-types', rows: (b) => b as unknown[] },
];

type Row = Record<string, unknown>;

/** 試験が作る行か（名前・キー・表示名のいずれかが `e2e-` で始まる）。 */
function isTestRow(row: Row): boolean {
  return ['name', 'key', 'label'].some(
    (k) => typeof row[k] === 'string' && (row[k] as string).startsWith('e2e-'),
  );
}

/**
 * 並べ替えのキー（`id`・`key`・`name` の順に最初に値のあるもの。どれも無ければ行全体の JSON）。辞書によっては `id` を持たないので、
 * `id` だけで並べると順序が応答の並びのまま残り、並びが変わっただけで差分に見える（#2238 のレビュー）。
 */
function sortKeyOf(row: Row): string {
  for (const k of ['id', 'key', 'name']) {
    const v = row[k];
    if (v !== undefined && v !== null && v !== '') return `${k}:${String(v)}`;
  }
  return `json:${JSON.stringify(row)}`;
}

/** 行を比べられる形に揃える（試験が作る行を除き、件数の項目を外し、安定したキーで並べる）。純関数。 */
export function normalizeRows(rows: readonly Row[]): Row[] {
  return rows
    .filter((r) => !isTestRow(r))
    .map(contentOf)
    .sort((a, b) => sortKeyOf(a).localeCompare(sortKeyOf(b)));
}

/** 件数の項目を除いた行（辞書の中身だけを比べる）。 */
function contentOf(row: Row): Row {
  return Object.fromEntries(Object.entries(row).filter(([k]) => !/count$/i.test(k)));
}

/** 既存のポリシーと辞書の写し（`e2e-` の行を除き、件数の項目を外し、安定したキーで並べる）。 */
export async function dictionarySnapshot(
  request: APIRequestContext,
): Promise<Record<string, Row[]>> {
  const out: Record<string, Row[]> = {};
  for (const s of SOURCES) {
    const res = await request.get(s.path, { failOnStatusCode: false });
    expect(res.status(), `GET ${s.path}（${s.name}）`).toBe(200);
    const rows = s.rows(await res.json());
    expect(Array.isArray(rows), `${s.name} の形が違う`).toBe(true);
    out[s.name] = normalizeRows(rows as Row[]);
  }
  return out;
}
