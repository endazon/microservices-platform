'use strict';
/*
 * eslint-import-resolver-unit-alias.cjs
 * ADR-0066 決定 3 / ADR-0067 決定 5 / IADR-0308: `import/no-restricted-paths` に
 * **tsconfig の `paths` エイリアスを解決させる**ための最小のリゾルバ（interfaceVersion 2）。
 *
 * ■ なぜ要るか（無いと規則が静かに 0 件で通る）
 *   `import/no-restricted-paths` は**解決できた import しか見ない**。既定の node リゾルバは
 *   `@foundation/routing/router` のような TS の path エイリアスを解決できないため、
 *   ゾーンを正しく書いても**エイリアスで書かれた越境は 1 件も報告されない**。
 *   実測（2026-08-30）: `platform/frontend/src/components/` から
 *     - `../app/routing/router`      → error（相対なので node リゾルバが解決する）
 *     - `@foundation/routing/router` → **報告なし**（解決できず素通り）
 *   platform ユニットの内部参照は 26 ファイル・59 文が `@foundation/*` で書かれており、
 *   **エイリアスを解決できないと規則は platform でほぼ何も守らない。**
 *   IADR-0308 が拡張子の設定漏れで踏んだのと同じ形（「静かに 0 件で通る」）である。
 *
 * ■ エイリアス表を持たない
 *   本ファイルは表を**持たず**、呼び出し側（`eslint.config.js`）が tsconfig の `paths` から
 *   組んで `settings` 経由で渡す。エイリアスの正本を増やさないためである
 *   （向き先は tsconfig / vite.config.ts / vitest.config.ts の 3 箇所にあり、**4 つ目を作らない**）。
 *
 * ■ 使い方（`eslint.config.js` 側）
 *   settings: { 'import/resolver': {
 *     node: { extensions: [...] },
 *     [<本ファイルの絶対パス>]: { aliases: { '@foundation/routing': '<絶対パス>', ... } },
 *   } }
 *   eslint-plugin-import はリゾルバを**名前で require する**（`eslint-import-resolver-<name>` →
 *   `<name>` → ソースの package からの相対）。**絶対パスを名前として渡すと 2 番目で解決される**
 *   （実測）。相対名にすると基準がソースファイルの package ディレクトリになり、
 *   ユニットごとにずれるので使わない。
 */

const fs = require('node:fs');
const path = require('node:path');

/** TS/TSX を先に見る。`import/no-restricted-paths` は解決結果のパスしか使わないので順序が意味を持つ。 */
const EXTENSIONS = ['.ts', '.tsx', '.js', '.jsx'];

/** `base` そのもの → `base + 拡張子` → `base/index + 拡張子` の順に実在するファイルを探す。 */
function resolveToFile(base) {
  try {
    if (fs.statSync(base).isFile()) return base;
  } catch {
    // 存在しない/ディレクトリ。下で拡張子と index を試す。
  }
  for (const ext of EXTENSIONS) {
    const candidate = base + ext;
    if (fs.existsSync(candidate)) return candidate;
  }
  for (const ext of EXTENSIONS) {
    const candidate = path.join(base, `index${ext}`);
    if (fs.existsSync(candidate)) return candidate;
  }
  return undefined;
}

exports.interfaceVersion = 2;

/**
 * @param {string} source import 文に書かれた文字列
 * @param {string} _file  import している側のファイル（本リゾルバは使わない。エイリアスは絶対パスで渡る）
 * @param {{ aliases?: Record<string, string> }} config settings から渡る設定
 */
exports.resolve = function resolve(source, _file, config) {
  const aliases = (config && config.aliases) || {};
  // **長いエイリアスから照合する。** `@foundation/api` と `@foundation/api-x` のような
  // 前方一致の取り違えを避ける（現状は起きないが、表は tsconfig 由来で増える）。
  const keys = Object.keys(aliases).sort((a, b) => b.length - a.length);
  for (const key of keys) {
    if (source !== key && !source.startsWith(`${key}/`)) continue;
    const rest = source.slice(key.length).replace(/^\//, '');
    const resolved = resolveToFile(rest ? path.join(aliases[key], rest) : aliases[key]);
    // 当たったエイリアスで実ファイルへ届かなければ「見つからない」を返す
    // （後続のリゾルバへ回す。node リゾルバが node_modules を試す）。
    return resolved ? { found: true, path: resolved } : { found: false };
  }
  return { found: false };
};
