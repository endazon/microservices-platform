#!/usr/bin/env node
/**
 * NFR, 計画 ADR-0107 決定 3・決定 5 / ADR-0112 決定 3, IADR-0514 (#1787, #1814):
 * `deploy/` のインフラのイメージの参照と、`src/` の自製イメージの基底イメージ・統合試験の Testcontainers の
 * イメージが **digest で固定されている**ことを検査する。
 *
 * **タグだけの参照では、同じタグの中身が差し替わっても検知できない**（ADR-0107 決定 3）。
 * 固定の表記は `<repo>:<tag>@sha256:<64 桁>` —— digest は multi-arch の image index のもので、
 * tag は人が読むために残す（IADR-0514 決定 1）。
 *
 * 拾う形:
 *   `deploy/` 配下の .yaml / .yml / Containerfile（`Dockerfile`・`*.Dockerfile`・`Containerfile`）:
 *   1. `image: <ref>`（compose・k8s マニフェスト・helm values の単一文字列。`- image:` も同じ）
 *   2. helm values の 3 キー形式（同じマッピングに `image:` と `tag:` があり、`image:` の値に `:` が無い）
 *      —— 同じマッピングに空でない `digest: "sha256:…"` があること。
 *   3. Containerfile の `FROM <ref>`（`scratch` と多段ビルドの段名は除く。`${VAR}/` の接頭辞はそのまま読む）、
 *      `COPY --from=<ref>`・`RUN --mount=…,from=<ref>`（段名・段番号は除く。#1814）
 *   4. テンプレートの `image:` 行に直書きした `default "<ref>"`（値が `/` か `:` を含むもの。`default "latest"` は拾わない。#1814）
 *   `src/` 配下（#1814。submodule `src/ai-stock-trading` は別リポジトリなので除く）:
 *   5. Containerfile（3 と同じ）—— 自製イメージの基底イメージ
 *   6. C# の Testcontainers の参照: `new [名前空間.]<モジュール|Container>Builder([image:] "<ref>")`（ビルダ名の許可リスト）・
 *      `.WithImage("<ref>")`・`const` / `static readonly string <…Image…|Reference> = "<ref>"`（Testcontainers に言及するファイル）。
 *      引数なしのモジュールのビルダ（`new PostgreSqlBuilder()` 等）は、同じ式に `.WithImage(` が無ければモジュール既定の
 *      tag だけのイメージを暗黙に使うので、参照として拾って落とす。コメントは読まない。
 *      既知の限界（拾わない）: target-typed の `new("<ref>")`、変数・補間文字列・逐語的文字列で組み立てた参照、
 *      Testcontainers に言及しないファイルの定数、許可リストに無いモジュールのビルダ（IADR-0514 の 2026-10-09 追記）。
 *
 * 対象外（理由つき）:
 *   - **自製イメージ**（`microservices-platform/*`・`k3d-local/*`）: CD が一意タグ/digest を渡す
 *     （運用仕様書 §自製イメージ）。chart 既定の `tag: latest` は CD 上書き用のプレースホルダである。
 *   - テンプレートの `{{ … }}` を含む行: 値は values 側で検査する（直書きの `default "<ref>"` は 4 で拾う）。
 *   - コメント行。`src/` の YAML（配備物の `image:` は `deploy/` にしか無い。lock ファイル等を誤読しない）。
 *
 * 落とすもの:
 *   - tag だけの参照（`scripts/image-digest-exceptions.json` に理由つきで載っているものを除く）。
 *   - **例外に載っているのに実在しない**参照（腐り止め。直ったなら例外から外す）。
 *   - **同じ `repo:tag` が別の digest で書かれている**（片側だけ更新した）。
 *   - 走査 0 件（fail-closed。「何も無い」と「問題が無い」を同じ出力にしない）。
 *
 * 使い方:
 *   node scripts/check-image-digests.js              # 検査。違反があれば終了コード 1
 *   node scripts/check-image-digests.js --list       # 点検の母集合（製品ごとの参照箇所）を出す。判定はしない
 *   node scripts/check-image-digests.js --self-test  # 検査ロジックの自己試験
 * 外部依存ゼロ（Node 標準モジュールのみ）。git もネットワークも使わない。
 */
'use strict';

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const DEPLOY_DIR = 'deploy';
/** #1814: 自製イメージの基底イメージと統合試験の Testcontainers の置き場。YAML は読まない（`listFiles` の `kinds`）。 */
const SRC_DIR = 'src';
/** 別リポジトリ（AST）の submodule。CI で取得されても本リポの統制の対象外（変えるのは向こうのリポジトリ）。 */
const SKIP_PATHS = new Set(['src/ai-stock-trading']);
const EXCEPTIONS_PATH = path.join(__dirname, 'image-digest-exceptions.json');
const SKIP_DIRS = new Set(['node_modules', '.git', 'charts', 'bin', 'obj']);

/**
 * 自製イメージの判定（IADR-0514 決定 2）。**実際に使っている接頭辞だけに錨を下ろす**（#1787 監査 (f)）:
 *   - `microservices-platform/<名>`（chart の services.*・frontend。レジストリは global.image.registry が付ける）
 *   - 同じ名前を本番・経路 B のレジストリ（`harbor.internal` / `k3d-local`）付きで直書きした形
 *   - `k3d-local/<名>`（経路 B の擬似レジストリ。k3d へ import する自製イメージだけが置かれる）
 * 任意のレジストリ配下の `microservices-platform/`（例 `evil.io/microservices-platform/x`）は免除しない。
 */
const SELF_BUILT = [/^(?:harbor\.internal\/|k3d-local\/)?microservices-platform\//, /^k3d-local\//];

const DIGEST_RE = /@sha256:[0-9a-f]{64}$/;

const isSelfBuilt = (ref) => SELF_BUILT.some((re) => re.test(ref));

/** 参照を { name, tag, digest } へ分ける（`name` はレジストリ込み・tag 抜き）。 */
function splitRef(ref) {
  let rest = ref;
  let digest = null;
  const at = rest.indexOf('@');
  if (at >= 0) {
    digest = rest.slice(at + 1);
    rest = rest.slice(0, at);
  }
  let tag = null;
  const lastSlash = rest.lastIndexOf('/');
  const colon = rest.indexOf(':', lastSlash + 1);
  if (colon >= 0) {
    tag = rest.slice(colon + 1);
    rest = rest.slice(0, colon);
  }
  return { name: rest, tag, digest };
}

/** 製品の名前（点検の母集合の鍵）。レジストリと `library/` を剥がす。 */
function productOf(name) {
  const parts = name.split('/');
  if (parts.length > 1 && (/[.:]/.test(parts[0]) || parts[0] === 'localhost' || parts[0].startsWith('${'))) parts.shift();
  if (parts[0] === 'library') parts.shift();
  return parts.join('/');
}

const unquote = (v) => v.trim().replace(/^(["'])(.*)\1$/, '$2');
const stripComment = (line) => line.replace(/\s+#.*$/, '');

/**
 * 1 ファイルから参照を拾う。返り値: [{ file, line, ref, kind }]
 * kind: 'image'（単一文字列）/ 'values-block'（3 キー形式）/ 'from'（Dockerfile）
 */
function extractRefs(relPath, content) {
  const out = [];
  const lines = String(content).replace(/\r\n/g, '\n').split('\n');
  const base = path.basename(relPath);

  if (isContainerfile(base)) {
    const stages = new Set();
    const isStage = (ref) => /^\d+$/.test(ref) || (stages.has(ref.toLowerCase()) && !ref.includes(':') && !ref.includes('/'));
    // #1814 監査: 行継続（末尾の `\`）をつないだ論理行で読む（`RUN \` の次の行の `--mount=…,from=` を取りこぼさない）。
    // 行番号は論理行の先頭の行。継続の途中のコメント行は Docker と同じく読み飛ばす。
    const logical = [];
    for (let i = 0; i < lines.length; i++) {
      if (/^\s*#/.test(lines[i])) continue;
      let text = lines[i];
      const start = i;
      while (/\\\s*$/.test(text) && i + 1 < lines.length) {
        i++;
        if (/^\s*#/.test(lines[i])) continue;
        text = text.replace(/\\\s*$/, ' ') + lines[i];
      }
      logical.push([text, start]);
    }
    logical.forEach(([raw, i]) => {
      const m = /^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?/i.exec(raw);
      if (m) {
        const ref = m[1];
        if (m[2]) stages.add(m[2].toLowerCase());
        if (ref === 'scratch' || isStage(ref)) return;
        out.push({ file: relPath, line: i + 1, ref, kind: 'from' });
        return;
      }
      // #1814: `COPY --from=<外部イメージ>` と `RUN --mount=…,from=<外部イメージ>` も外部イメージを引く。段名・段番号は除く。
      const c = /^\s*COPY\s+(?:--\S+\s+)*?--from=(\S+)/i.exec(raw);
      if (c) {
        if (!isStage(c[1])) out.push({ file: relPath, line: i + 1, ref: c[1], kind: 'copy-from' });
        return;
      }
      // #1814 AI レビュー: 1 行（論理行）に `--mount=…,from=` が複数あっても全件を拾う（非 global の exec は最後の 1 件しか返さなかった）。
      if (!/^\s*RUN\s/i.test(raw)) return;
      for (const m of raw.matchAll(/--mount=\S*?\bfrom=([^,\s]+)/gi)) {
        if (!isStage(m[1])) out.push({ file: relPath, line: i + 1, ref: m[1], kind: 'mount-from' });
      }
    });
    return out;
  }

  if (/\.cs$/.test(base)) return extractCsharpRefs(relPath, lines);

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    if (/^\s*#/.test(raw)) continue;
    const m = /^(\s*)(-\s+)?image:\s*(.*)$/.exec(stripComment(raw));
    if (!m) continue;
    const value = unquote(m[3]);
    const indent = m[1].length + (m[2] ? m[2].length : 0);

    // #1787 監査 (a): helm の `image: {repository: x, tag: y}` の形（ブロックでも flow でも）。
    // `repository` を持つマッピングは、同じマッピングに digest が要る。持たない見出し（global.image 等）は拾わない。
    if (value === '' || (value.startsWith('{') && !value.startsWith('{{'))) {
      const keys = value === '' ? childKeys(lines, i, indent) : flowKeys(value);
      if (keys && 'repository' in keys && !String(keys.repository).includes('{{')) {
        out.push({ file: relPath, line: i + 1, ref: composeRef(keys.registry, keys.repository, keys.tag, keys.digest), kind: 'values-block' });
      }
      continue;
    }
    if (value.includes('{{')) {
      // テンプレートの行。値は values 側で検査する。ただし直書きの既定値（`default "x/y:1"`）は values に現れない（#1814）。
      for (const d of value.matchAll(/\bdefault\s+"([^"]+)"/g)) {
        if (/[/:]/.test(d[1])) out.push({ file: relPath, line: i + 1, ref: d[1], kind: 'template-default' });
      }
      continue;
    }

    if (value.includes(':') || value.includes('@')) {
      out.push({ file: relPath, line: i + 1, ref: value, kind: 'image' });
      continue;
    }

    // 値に tag が無い: 同じマッピングの兄弟キー（registry / tag / digest）を探す。
    const siblings = {};
    const scan = (from, step) => {
      for (let j = from; j >= 0 && j < lines.length; j += step) {
        const l = lines[j];
        if (l.trim() === '' || /^\s*#/.test(l)) continue;
        const ind = l.length - l.trimStart().length;
        if (ind < indent) break;
        if (ind > indent) continue;
        if (/^\s*-\s/.test(l)) break; // 別の列要素
        const k = /^\s*([A-Za-z0-9_.-]+):\s*(.*)$/.exec(stripComment(l));
        if (k) siblings[k[1]] = unquote(k[2]);
      }
    };
    scan(i - 1, -1);
    scan(i + 1, 1);
    if (!('tag' in siblings)) {
      // tag も無い単一文字列（`image: busybox` 等）は latest の暗黙参照である。
      out.push({ file: relPath, line: i + 1, ref: value, kind: 'image' });
      continue;
    }
    out.push({ file: relPath, line: i + 1, ref: composeRef(siblings.registry, value, siblings.tag, siblings.digest), kind: 'values-block' });
  }
  return out;
}

/** イメージ参照の形（空白なし・小文字の名前。`${VAR}/` の接頭辞・tag・digest は任意）。 */
const IMAGE_REF_RE =
  /^(?:\$\{\w+\}\/)?(?:[a-z0-9.-]+(?::\d+)?\/)?[a-z0-9]+(?:[._-][a-z0-9]+)*(?:\/[a-z0-9]+(?:[._-][a-z0-9]+)*)*(?::[\w][\w.-]{0,127})?(?:@sha256:[0-9a-f]{64})?$/;
/**
 * Testcontainers のモジュールのビルダの名前（`<名前>Builder`）。**許可リスト**である（#1814 監査: 拒否リストだと
 * `AuthorizationPolicyBuilder("…")` 等の無関係なビルダを読んでしまう）。引数なしだとモジュール既定の tag だけのイメージを使う。
 * 名前で判定するので、`using` の形（`global using`・csproj の `<Using Include>`・完全修飾名）に依らず拾える。
 */
const TC_MODULES = 'PostgreSql|RabbitMq|Redis|Qdrant|Minio|Keycloak|Kafka|MongoDb|MsSql|MySql|MariaDb|Elasticsearch|Nats|LocalStack|Azurite|Valkey';
/** 汎用の `ContainerBuilder` も含めた、イメージを引数に取るビルダ。`new [名前空間.]<X>Builder([image:] "<ref>")`。 */
const TC_BUILDER_LITERAL = new RegExp(`\\bnew\\s+(?:[\\w.]+\\.)?(${TC_MODULES}|Container)Builder\\s*\\(\\s*(?:image\\s*:\\s*)?"([^"]*)"`, 'g');
const TC_BUILDER_NO_ARG = new RegExp(`\\bnew\\s+(?:[\\w.]+\\.)?(${TC_MODULES})Builder\\s*\\(\\s*\\)`, 'g');

/**
 * C# のコメント（`//`・`/* … *\/`）を空白に置き換える。文字列リテラルの中の `//`（`http://…`）は残す。
 * 長さと改行を保つので、正規表現の位置から行番号を引ける。逐語的文字列（`@"…"`）の `""` は近似で扱う。
 */
function blankCsharpComments(text) {
  let out = '';
  let i = 0;
  while (i < text.length) {
    const c = text[i];
    const n = text[i + 1];
    if (c === '/' && n === '/') {
      while (i < text.length && text[i] !== '\n') { out += ' '; i++; }
    } else if (c === '/' && n === '*') {
      while (i < text.length && !(text[i] === '*' && text[i + 1] === '/')) { out += text[i] === '\n' ? '\n' : ' '; i++; }
      if (i < text.length) { out += '  '; i += 2; }
    } else if (c === "'" && /^'(?:\\.|[^\\'\n])'/.test(text.slice(i, i + 4))) {
      // 文字リテラル（`'"'` を文字列の開始と取り違えない）。
      const len = text[i + 1] === '\\' ? 4 : 3;
      out += text.slice(i, i + len);
      i += len;
    } else if (c === '"') {
      out += c;
      i++;
      while (i < text.length && text[i] !== '"' && text[i] !== '\n') {
        if (text[i] === '\\') { out += text[i]; i++; }
        if (i < text.length) { out += text[i]; i++; }
      }
      if (i < text.length) { out += text[i]; i++; }
    } else {
      out += c;
      i++;
    }
  }
  return out;
}

/**
 * #1814: C# の Testcontainers の参照を拾う。コメント行は空にしてから全文へ正規表現を当てる（定数の値が次の行に来る形も読む）。
 * kind: 'testcontainers'（リテラル）/ 'testcontainers-default'（引数なしのモジュールのビルダ）。
 */
function extractCsharpRefs(relPath, lines) {
  const out = [];
  const text = blankCsharpComments(lines.join('\n'));
  const mentionsTc = /Testcontainers/.test(lines.join('\n'));
  const lineOf = (idx) => text.slice(0, idx).split('\n').length;
  const push = (idx, ref, kind) => {
    if (kind === 'testcontainers' && !IMAGE_REF_RE.test(ref)) return;
    out.push({ file: relPath, line: lineOf(idx), ref, kind });
  };
  for (const m of text.matchAll(TC_BUILDER_LITERAL)) push(m.index, m[2], 'testcontainers');
  for (const m of text.matchAll(TC_BUILDER_NO_ARG)) {
    // 同じ式（次の `;` まで）に `.WithImage(` があれば既定のイメージは使わない（その引数は下の規則で拾う。#1814 監査）。
    const end = text.indexOf(';', m.index);
    if (/\.WithImage\s*\(/.test(text.slice(m.index, end < 0 ? undefined : end))) continue;
    push(m.index, `${m[1]}Builder()（モジュール既定のイメージ）`, 'testcontainers-default');
  }
  for (const m of text.matchAll(/\.WithImage\s*\(\s*(?:image\s*:\s*)?"([^"]*)"/g)) push(m.index, m[1], 'testcontainers');
  if (mentionsTc) {
    // `const string` と `static readonly string`（#1814 監査）。名前が …Image… か Reference のもの。
    const decl = /\b(?:const|static\s+readonly|readonly\s+static)\s+string\s+(\w*(?:Image\w*|Reference))\s*=\s*"([^"]*)"/g;
    for (const m of text.matchAll(decl)) push(m.index, m[2], 'testcontainers');
  }
  return out;
}

/** registry / name / tag / digest から参照を組み立てる（テンプレートの registry は付けない）。 */
function composeRef(registry, name, tag, digest) {
  const reg = registry && !String(registry).includes('{{') ? `${registry}/` : '';
  return `${reg}${name}${tag ? `:${tag}` : ''}${digest ? `@${digest}` : ''}`;
}

/** `key:` 見出しの直下の子キー（最初の子の深さだけ）を読む。子が無ければ null。 */
function childKeys(lines, at, indent) {
  const keys = {};
  let childIndent = null;
  for (let j = at + 1; j < lines.length; j++) {
    const l = lines[j];
    if (l.trim() === '' || /^\s*#/.test(l)) continue;
    const ind = l.length - l.trimStart().length;
    if (ind <= indent) break;
    if (childIndent === null) childIndent = ind;
    if (ind !== childIndent) continue;
    const k = /^\s*([A-Za-z0-9_.-]+):\s*(.*)$/.exec(stripComment(l));
    if (k) keys[k[1]] = unquote(k[2]);
  }
  return childIndent === null ? null : keys;
}

/** flow 形式 `{repository: x, tag: "y"}` を読む（入れ子は扱わない）。 */
function flowKeys(value) {
  const body = value.replace(/^\{/, '').replace(/\}\s*$/, '');
  const keys = {};
  for (const part of body.split(',')) {
    const k = /^\s*([A-Za-z0-9_.-]+)\s*:\s*(.*?)\s*$/.exec(part);
    if (k) keys[k[1]] = unquote(k[2]);
  }
  return keys;
}

/** 走査するファイル名（#1787 監査 (d): `*.Dockerfile` と `Containerfile` も読む）。 */
const isContainerfile = (name) => /^(Dockerfile|Containerfile)(\..*)?$/.test(name) || /\.(Dockerfile|Containerfile)$/.test(name);

/**
 * 走査するファイルを集める。`deploy/` は YAML と Containerfile、`src/` は Containerfile と C#（#1814）。
 */
function listFiles(dir, root, { yaml, csharp }) {
  const found = [];
  const walk = (d) => {
    if (!fs.existsSync(d)) return;
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      if (SKIP_DIRS.has(e.name)) continue;
      const full = path.join(d, e.name);
      if (SKIP_PATHS.has(path.relative(root, full).split(path.sep).join('/'))) continue;
      if (e.isDirectory()) walk(full);
      else if ((yaml && /\.ya?ml$/.test(e.name)) || (csharp && /\.cs$/.test(e.name)) || isContainerfile(e.name)) found.push(full);
    }
  };
  walk(dir);
  return found.sort();
}

function collect(root = REPO_ROOT) {
  const files = [
    ...listFiles(path.join(root, DEPLOY_DIR), root, { yaml: true, csharp: false }),
    ...listFiles(path.join(root, SRC_DIR), root, { yaml: false, csharp: true }),
  ];
  const refs = files.flatMap((f) =>
    extractRefs(path.relative(root, f).split(path.sep).join('/'), fs.readFileSync(f, 'utf8')),
  );
  return { files, refs };
}

/** 判定の本体（純粋関数）。 */
function evaluate(refs, exceptions) {
  const infra = refs.filter((r) => !isSelfBuilt(r.ref));
  const errors = [];
  const exKey = (e) => `${e.file}::${e.ref}`;
  const exSet = new Set(exceptions.map(exKey));
  const usedEx = new Set();

  for (const r of infra) {
    if (DIGEST_RE.test(r.ref)) continue;
    if (exSet.has(exKey(r))) {
      usedEx.add(exKey(r));
      continue;
    }
    errors.push(`[tag-only] ${r.file}:${r.line} ${r.ref} — digest（@sha256:…）で固定する（ADR-0107 決定 3）`);
  }
  for (const e of exceptions) {
    if (!e.reason || String(e.reason).trim() === '') errors.push(`[exception-no-reason] ${exKey(e)} — 例外には理由を書く`);
    if (!usedEx.has(exKey(e))) errors.push(`[exception-stale] ${exKey(e)} — 例外に載っているが tag だけの参照として実在しない（直ったなら例外から外す）`);
  }

  // 同じ repo:tag は同じ digest であること。
  const byTag = new Map();
  for (const r of infra) {
    const { name, tag, digest } = splitRef(r.ref);
    if (!digest) continue;
    const key = `${productOf(name)}:${tag}`;
    if (!byTag.has(key)) byTag.set(key, new Map());
    const m = byTag.get(key);
    if (!m.has(digest)) m.set(digest, []);
    m.get(digest).push(`${r.file}:${r.line}`);
  }
  for (const [key, m] of byTag) {
    if (m.size > 1) {
      const detail = [...m].map(([d, at]) => `${d.slice(0, 19)}… (${at.join(', ')})`).join(' / ');
      errors.push(`[digest-mismatch] ${key} が別の digest で書かれている: ${detail}`);
    }
  }
  return { infra, errors };
}

function readExceptions() {
  if (!fs.existsSync(EXCEPTIONS_PATH)) return [];
  const json = JSON.parse(fs.readFileSync(EXCEPTIONS_PATH, 'utf8'));
  if (!Array.isArray(json.exceptions)) throw new Error(`${EXCEPTIONS_PATH} に exceptions 配列が無い`);
  return json.exceptions;
}

function run() {
  const { files, refs } = collect();
  if (files.length === 0 || refs.length === 0) {
    console.error(`[check-image-digests] 走査対象が 0 件である（ファイル ${files.length} / 参照 ${refs.length}。パスの想定が壊れている）。`);
    process.exit(1);
  }
  const { infra, errors } = evaluate(refs, readExceptions());
  if (infra.length === 0) {
    console.error('[check-image-digests] 外部イメージの参照が 0 件である（自製判定か抽出が壊れている）。');
    process.exit(1);
  }
  if (errors.length > 0) {
    console.error('[check-image-digests] 違反を検出しました:\n');
    for (const e of errors) console.error(`  ${e}`);
    console.error('\n固定の更新手順は運用仕様書 §インフラ製品の点検（digest の解決と更新）を参照（IADR-0514）。');
    process.exit(1);
  }
  const products = new Set(infra.map((r) => productOf(splitRef(r.ref).name)));
  console.log(
    `[check-image-digests] OK: ${files.length} ファイル・外部イメージの参照 ${infra.length} 件（製品 ${products.size} 種）はすべて digest で固定されている（自製 ${refs.length - infra.length} 件は対象外）。`,
  );
}

function list() {
  const { refs } = collect();
  const infra = refs.filter((r) => !isSelfBuilt(r.ref));
  const byProduct = new Map();
  for (const r of infra) {
    const { name, tag, digest } = splitRef(r.ref);
    const p = productOf(name);
    if (!byProduct.has(p)) byProduct.set(p, []);
    byProduct.get(p).push({ at: `${r.file}:${r.line}`, tag, pinned: Boolean(digest) });
  }
  console.log('| 製品 | tag | digest | 参照箇所 |');
  console.log('| --- | --- | --- | --- |');
  for (const p of [...byProduct.keys()].sort()) {
    const rows = byProduct.get(p);
    const tags = [...new Set(rows.map((x) => x.tag || '(なし)'))].join(', ');
    const pinned = rows.every((x) => x.pinned) ? '固定' : rows.some((x) => x.pinned) ? '一部' : '未固定';
    console.log(`| ${p} | ${tags} | ${pinned} | ${rows.map((x) => x.at).join('<br>')} |`);
  }
  console.log(`\n製品 ${byProduct.size} 種・参照 ${infra.length} 件（自製 ${refs.length - infra.length} 件は対象外）。`);
}

function selfTest() {
  const D = 'sha256:' + 'a'.repeat(64);
  const D2 = 'sha256:' + 'b'.repeat(64);
  const cases = [];
  const t = (name, fn) => cases.push([name, fn]);
  const refsOf = (file, text) => extractRefs(file, text).map((r) => r.ref);
  const errs = (file, text, ex = []) => evaluate(extractRefs(file, text), ex).errors;

  t('compose の tag だけの参照を拾って落とす', () =>
    errs('deploy/docker-compose.yml', 'services:\n  redis:\n    image: redis:7-alpine\n').length === 1);
  t('digest 付きは通す', () => errs('deploy/x.yaml', `      - image: redis:7-alpine@${D}\n`).length === 0);
  t('引用符つきの値も読む', () => refsOf('deploy/x.yaml', `  image: "node:22-alpine@${D}"\n`)[0] === `node:22-alpine@${D}`);
  t('行末コメントを剥がす', () => refsOf('deploy/x.yaml', `  image: busybox:1.37@${D}  # probe\n`)[0] === `busybox:1.37@${D}`);
  t('コメント行は拾わない', () => refsOf('deploy/x.yaml', '  # image: redis:7-alpine\n').length === 0);
  t('テンプレートの行は拾わない', () => refsOf('deploy/x.yaml', '  image: "{{ $s.registry }}/{{ $s.image }}:{{ $s.tag }}"\n').length === 0);
  t('マッピングの見出し（値なし）は拾わない', () => refsOf('deploy/x.yaml', 'global:\n  image:\n    registry: k3d-local\n').length === 0);
  t('自製（k3d-local）は対象外', () => errs('deploy/x.yaml', '  image: k3d-local/platform-backup:r6\n').length === 0);
  t('自製（microservices-platform/ の 3 キー形式）は対象外', () =>
    errs('deploy/v.yaml', 'svc:\n  image: microservices-platform/bff\n  tag: latest\n').length === 0);
  t('3 キー形式で digest が無ければ落とす', () =>
    errs('deploy/v.yaml', 'wikijs:\n  registry: ghcr.io\n  image: requarks/wiki\n  tag: "2.5"\n  port: 3000\n').length === 1);
  t('3 キー形式で digest があれば通し、registry 込みで組み立てる', () => {
    const r = refsOf('deploy/v.yaml', `wikijs:\n  registry: ghcr.io\n  image: requarks/wiki\n  # 注記\n  tag: "2.5"\n  digest: "${D}"\n`);
    return r.length === 1 && r[0] === `ghcr.io/requarks/wiki:2.5@${D}`;
  });
  t('3 キー形式の兄弟は同じ深さだけを見る（入れ子の tag を拾わない）', () =>
    refsOf('deploy/v.yaml', 'a:\n  image: foo/bar\n  sub:\n    tag: "1"\n').join() === 'foo/bar');
  t('tag の無い単一文字列は暗黙の latest として落とす', () => errs('deploy/x.yaml', '  image: busybox\n').length === 1);
  t('Dockerfile の FROM を拾い、段名と scratch は拾わない', () =>
    refsOf('deploy/a/Dockerfile', `FROM \${BASE}/postgres:16@${D} AS base\nFROM base\nFROM scratch\n`).join() === `\${BASE}/postgres:16@${D}`);
  t('Dockerfile の tag だけの FROM を落とす', () => errs('deploy/a/Dockerfile', 'FROM alpine:3.20\n').length === 1);
  t('例外（理由つき）に載った参照は通す', () =>
    errs('deploy/x.yaml', '  image: foo:1\n', [{ file: 'deploy/x.yaml', ref: 'foo:1', reason: '理由' }]).length === 0);
  t('理由の無い例外は落とす', () =>
    errs('deploy/x.yaml', '  image: foo:1\n', [{ file: 'deploy/x.yaml', ref: 'foo:1', reason: '' }]).length === 1);
  t('実在しない例外は落とす（腐り止め）', () =>
    errs('deploy/x.yaml', `  image: foo:1@${D}\n`, [{ file: 'deploy/x.yaml', ref: 'foo:1', reason: '理由' }]).length === 1);
  t('同じ repo:tag の digest が食い違えば落とす（レジストリ接頭辞の有無は同一視）', () =>
    evaluate(
      [
        { file: 'a', line: 1, ref: `docker.io/library/redis:7@${D}`, kind: 'image' },
        { file: 'b', line: 1, ref: `redis:7@${D2}`, kind: 'image' },
      ],
      [],
    ).errors.some((e) => e.startsWith('[digest-mismatch]')));
  t('同じ repo:tag で同じ digest なら通す', () =>
    evaluate(
      [
        { file: 'a', line: 1, ref: `docker.io/chrislusf/seaweedfs:4.47@${D}`, kind: 'image' },
        { file: 'b', line: 1, ref: `chrislusf/seaweedfs:4.47@${D}`, kind: 'values-block' },
      ],
      [],
    ).errors.length === 0);
  t('(a) helm の image: { repository, tag } ブロックで digest が無ければ落とす', () => {
    const text = 'redis:\n  image:\n    registry: docker.io\n    repository: bitnami/redis\n    tag: "7.2"\n  port: 6379\n';
    const r = refsOf('deploy/v.yaml', text);
    return r.join() === 'docker.io/bitnami/redis:7.2' && errs('deploy/v.yaml', text).length === 1;
  });
  t('(a) image: { repository, tag, digest } ブロックは通す', () =>
    errs('deploy/v.yaml', `x:\n  image:\n    repository: foo/bar\n    tag: "1"\n    digest: "${D}"\n`).length === 0);
  t('(a) flow 形式の image: {repository: x, tag: y} も拾って落とす', () => {
    const text = 'x:\n  image: {repository: foo/bar, tag: "1"}\n';
    return refsOf('deploy/v.yaml', text).join() === 'foo/bar:1' && errs('deploy/v.yaml', text).length === 1;
  });
  t('(a) repository を持たない見出し（global.image）は拾わない', () =>
    refsOf('deploy/v.yaml', 'global:\n  image:\n    registry: harbor.internal\n    pullPolicy: IfNotPresent\n').length === 0);
  t('(d) *.Dockerfile と Containerfile を走査対象にし、FROM を読む', () =>
    isContainerfile('backup.Dockerfile') && isContainerfile('Containerfile') && isContainerfile('Dockerfile.dev') &&
    !isContainerfile('notes.md') && !isContainerfile('values.yaml') &&
    errs('deploy/a/backup.Dockerfile', 'FROM alpine:3.20\n').length === 1 &&
    errs('deploy/a/Containerfile', 'FROM alpine:3.20\n').length === 1);
  t('(f) 任意レジストリ配下の microservices-platform/ は自製として免除しない', () =>
    !isSelfBuilt('evil.io/microservices-platform/x:1') && isSelfBuilt('microservices-platform/bff:latest') &&
    isSelfBuilt('harbor.internal/microservices-platform/bff:abc') && isSelfBuilt('k3d-local/platform-backup:r6') &&
    errs('deploy/x.yaml', '  image: evil.io/microservices-platform/x:1\n').length === 1);
  // --- #1814: 走査の範囲を src/ へ広げ、監査の取りこぼし（COPY --from・テンプレートの直書き default）を塞ぐ ---
  t('#1814 COPY --from=<外部イメージ> の tag だけを落とし、段名・段番号は拾わない', () => {
    const df = 'FROM golang:1.22@' + D + ' AS build\nCOPY --from=build /a /a\nCOPY --from=0 /b /b\nCOPY --chown=1:1 --from=busybox:1.37 /bin/sh /sh\n';
    const r = extractRefs('src/x/Dockerfile', df);
    return r.map((x) => x.ref).join() === `golang:1.22@${D},busybox:1.37` && r[1].kind === 'copy-from' &&
      errs('src/x/Dockerfile', df).length === 1;
  });
  t('#1814 RUN --mount=…,from=<外部イメージ> を拾う（段名は拾わない）', () =>
    refsOf('src/x/Dockerfile', 'FROM a:1@' + D + ' AS deps\nRUN --mount=type=bind,from=deps,target=/d true\nRUN --mount=type=bind,from=alpine:3.20,target=/x true\n')
      .join() === `a:1@${D},alpine:3.20`);
  t('#1814 テンプレートの image 行に直書きした default "<参照>" を落とし、default "latest" は拾わない', () => {
    const tpl = '  image: "{{ .Values.x.image | default "busybox:1.36" }}"\n  image: "{{ $r }}/{{ $s.image }}:{{ $s.tag | default "latest" }}"\n';
    return refsOf('deploy/helm/t/templates/a.yaml', tpl).join() === 'busybox:1.36' && errs('deploy/helm/t/templates/a.yaml', tpl).length === 1;
  });
  t('#1814 C#: Testcontainers のビルダの引数・WithImage のリテラルを拾い、StringBuilder は拾わない', () => {
    const cs = 'using Testcontainers.PostgreSql;\nvar a = new PostgreSqlBuilder("postgres:16-alpine").Build();\n' +
      'var sb = new StringBuilder("BT /F1");\nvar u = new UriBuilder("http://x");\nvar c = new ContainerBuilder().WithImage("redis:7@' + D + '");\n';
    const r = extractRefs('src/T/A.cs', cs);
    return r.map((x) => x.ref).join() === `postgres:16-alpine,redis:7@${D}` && r[0].line === 2 && r[0].kind === 'testcontainers' &&
      errs('src/T/A.cs', cs).length === 1;
  });
  t('#1814 C#: Testcontainers に言及するファイルの const …Image / Reference を拾う（値が次の行でも読む）', () =>
    refsOf('src/T/I.cs', '// Testcontainers の参照\npublic const string Reference =\n    "qdrant/qdrant:v1.18.1@' + D + '";\nconst string Image = "chrislusf/seaweedfs:4.47";\nconst string Name = "redis:7";\n')
      .join() === `qdrant/qdrant:v1.18.1@${D},chrislusf/seaweedfs:4.47`);
  t('#1814 C#: Testcontainers に言及しないファイルの const は拾わない（接続文字列の "redis:6379" 等の誤読を防ぐ）', () =>
    refsOf('src/A/O.cs', 'public const string Reference = "redis:6379";\n').length === 0);
  t('#1814 C#: 引数なしのモジュールのビルダ（既定のイメージ）を落とす。コメント行は読まない', () => {
    const cs = 'using Testcontainers.RabbitMq;\n// new RabbitMqBuilder() は廃止予定\nvar r = new RabbitMqBuilder().Build();\nvar h = new HostBuilder();\n';
    const r = extractRefs('src/T/B.cs', cs);
    return r.length === 1 && r[0].kind === 'testcontainers-default' && r[0].line === 3 && errs('src/T/B.cs', cs).length === 1;
  });
  t('#1814 src/ は Containerfile と C# だけを読み、submodule（src/ai-stock-trading）と bin/obj を読まない', () => {
    const os = require('os');
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'cid-'));
    try {
      const w = (rel, text) => { fs.mkdirSync(path.dirname(path.join(tmp, rel)), { recursive: true }); fs.writeFileSync(path.join(tmp, rel), text); };
      w('deploy/c.yml', 'services:\n  r:\n    image: redis:7\n');
      w('src/a/Dockerfile', 'FROM alpine:3.20\n');
      w('src/a/k.yaml', 'image: busybox:1\n');
      w('src/a/T.cs', 'using Testcontainers.Redis;\nvar x = new RedisBuilder("redis:7").Build();\n');
      w('src/a/obj/Dockerfile', 'FROM obj:1\n');
      w('src/ai-stock-trading/Dockerfile', 'FROM ast:1\n');
      const { refs } = collect(tmp);
      return refs.map((r) => `${r.file}=${r.ref}`).sort().join() === 'deploy/c.yml=redis:7,src/a/Dockerfile=alpine:3.20,src/a/T.cs=redis:7';
    } finally {
      fs.rmSync(tmp, { recursive: true, force: true });
    }
  });
  // --- #1814 独立監査の指摘（誤検知・取りこぼし・精度） ---
  t('#1814 監査: 引数なしのビルダでも同じ式に .WithImage(…) があれば既定のイメージとして落とさない', () => {
    const cs = 'using Testcontainers.PostgreSql;\nvar a = new PostgreSqlBuilder()\n    .WithImage("postgres:16@' + D + '")\n    .Build();\n';
    const r = extractRefs('src/T/W.cs', cs);
    return r.length === 1 && r[0].kind === 'testcontainers' && r[0].ref === `postgres:16@${D}` && errs('src/T/W.cs', cs).length === 0;
  });
  t('#1814 監査: global using・完全修飾名・名前付き引数・static readonly を拾う', () => {
    const viaGlobal = refsOf('src/T/G.cs', 'var a = new PostgreSqlBuilder("postgres:16").Build();\n');
    const qualified = refsOf('src/T/Q.cs', 'var a = new Testcontainers.RabbitMq.RabbitMqBuilder("rabbitmq:3.13").Build();\n');
    const named = refsOf('src/T/N.cs', 'using Testcontainers.Redis;\nvar a = new RedisBuilder(image: "redis:7").Build();\n');
    const ro = refsOf('src/T/R.cs', '// Testcontainers の参照\nprivate static readonly string PgImage = "postgres:16-alpine";\n');
    return viaGlobal.join() === 'postgres:16' && qualified.join() === 'rabbitmq:3.13' && named.join() === 'redis:7' &&
      ro.join() === 'postgres:16-alpine';
  });
  t('#1814 AI レビュー: 1 行に複数の --mount=…,from= があれば全件を拾う（段名は除く）', () => {
    const r = extractRefs('src/x/Dockerfile', 'FROM a:1@' + D + ' AS deps\nRUN --mount=type=bind,from=alpine:3.20,target=/a --mount=type=cache,target=/c --mount=type=bind,from=deps,target=/d --mount=type=bind,from=busybox:1.37,target=/b true\n');
    const m = r.filter((x) => x.kind === 'mount-from').map((x) => x.ref);
    return m.length === 2 && m[0] === 'alpine:3.20' && m[1] === 'busybox:1.37';
  });
  t('#1814 監査: 行継続した RUN の次の行の --mount=…,from= を拾う（行番号は論理行の先頭）', () => {
    const r = extractRefs('src/x/Dockerfile', 'FROM a:1@' + D + '\nRUN \\\n    --mount=type=bind,from=alpine:3.20,target=/x \\\n    true\n');
    return r.length === 2 && r[1].ref === 'alpine:3.20' && r[1].kind === 'mount-from' && r[1].line === 2;
  });
  t('#1814 監査: 行末コメント・ブロックコメントの中のビルダは読まず、文字列の中の // は残す', () => {
    const cs = 'using Testcontainers.Redis;\nvar u = "http://x"; // new RedisBuilder() は使わない\n/* new PostgreSqlBuilder() */\nvar q = \'"\'; var a = new RedisBuilder("redis:7@' + D + '");\n';
    const r = extractRefs('src/T/C.cs', cs);
    return r.length === 1 && r[0].ref === `redis:7@${D}` && r[0].line === 4;
  });
  t('#1814 監査: 許可リストに無いビルダ（AuthorizationPolicyBuilder 等）と target-typed の new(…) は拾わない（後者は既知の限界）', () =>
    refsOf('src/T/P.cs', 'using Testcontainers.PostgreSql;\nvar p = new AuthorizationPolicyBuilder("admin").Build();\nvar s = new StringBuilder("x/y:1");\nPostgreSqlBuilder b = new("postgres:16");\n').length === 0);
  t('splitRef はレジストリのポートを tag と取り違えない', () => {
    const s = splitRef(`localhost:5000/foo/bar:1.2@${D}`);
    return s.name === 'localhost:5000/foo/bar' && s.tag === '1.2' && s.digest === D;
  });

  let failed = 0;
  for (const [name, fn] of cases) {
    let ok = false;
    try {
      ok = fn() === true;
    } catch (e) {
      ok = false;
    }
    if (ok) console.log(`  ok  ${name}`);
    else {
      console.error(`  ✗ ${name}`);
      failed++;
    }
  }
  if (failed > 0) process.exit(1);
  console.log(`✓ self-test: ${cases.length} 件すべて通過`);
}

module.exports = { extractRefs, evaluate, splitRef, productOf, isSelfBuilt, isContainerfile, collect };

if (require.main === module) {
  if (process.argv.includes('--self-test')) selfTest();
  else if (process.argv.includes('--list')) list();
  else run();
}
