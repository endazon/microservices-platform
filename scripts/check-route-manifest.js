#!/usr/bin/env node
'use strict';
/*
 * check-route-manifest.js — #1013 / NFR / IADR-0124 / IADR-0130
 *
 * ルートマニフェスト（`router.test.ts` の `PLANNED_ROUTES`）と、画面 feature のディレクトリを
 * 双方向に突き合わせる。あわせて「E2E がルートの実在を固定できる」という**誤った主張**の
 * 再混入を止める。
 *
 * ★ 起点（#1013）: 2 つの欠陥が同時に見つかった。
 *   1. **E2E スモーク 5 本が「この 1 本でルートの実在も同時に固定できる」と書いていた。**
 *      未知パスの受け皿（catchAllRoute）は `RequireAuth` 配下に居るため、**ルートを消しても
 *      未認証なら同じく `/login` へ行く**。#918 が改名の変異を当てて**落ちたテスト 0 件**を実測した。
 *   2. **`PLANNED_ROUTES` に SC-18 / SC-19 / SC-20 が入っていなかった。**
 *      🔴 **列挙は「載っている行」しか検査できない。載せ忘れた画面は誰にも見えない。**
 *
 * ── 判定 1: マニフェストの網羅（順方向・逆方向）
 *   順方向: 画面 feature（`sc<NN>-*` かつ `createRoute({ … path: … })` を持つ）が
 *           `PLANNED_ROUTES` にも `SCREENS_NOT_IN_THE_ROUTE_TABLE` にも無ければ error。
 *   逆方向: マニフェスト側の `SC-NN` に対応する画面 feature が無ければ error。
 *           🔴 **SC 番号はテスト名のラベルでしかなく、取り違えても Vitest は緑のまま**である
 *           （パスさえ木にあれば通る）。番号の誤りはここでしか捕まらない。
 *   除外は**理由の文字列とともにしか宣言できない**（黙って外す道を用意しない）。
 *
 *   🔴 **パス値は突き合わせない（意図的）。** 検査器が feature 側の `path:` を正としてマニフェストへ
 *   写すと、マニフェストは「計画から書き写した値」ではなく「実装から導出した値」に変わり、
 *   実装がパスを改名したときに検査器が「マニフェストを直せ」と促す形になる。
 *   **パス値を計画に対して固定するのは `router.test.ts`（木に載っているかを実行時に見る）の役目**で、
 *   本検査器の役目は**行の有無**である。2 つの層で別のものを見る。
 *
 * ── 判定 2: 誤った主張の再混入（grep-zero）
 *   走査対象は `src/<unit>/frontend/e2e/` と `docs/tests/`。
 *   🔴 **これは文面のリテラル検査であり、新しい言い回しの誤記は捕まえられない。**
 *   捕まえるのは**コピー由来の再混入**である —— 5 本の e2e は同一文面の複製であり、
 *   次の画面も既存ファイルを複製して作られる。それが機序である。
 *   否定形（「ルートの実在は**固定できない**」）・委譲（「ルートの実在は T-30 が固定する」）は
 *   **落としてはならない**。落とすと正しく書いた文書が赤くなり、検査器ごと外される。
 *
 * ── 走査しないもの（黙って飛ばさず、ここに開示する）
 *   - **`.ai-context/` の凍結記録**。同じ文面が 4 件あるが、確定済み記録の本文は書き換えない
 *     （IADR-0166 決定 2 の 2026-08-17 追記）。対象に入れると、検査器が
 *     **「書き換えてはならない記録の書き換え」を要求する**ことになる。是正は経過追記で行う。
 *   - **submodule のユニット**（`.gitmodules` から導出。`src/ai-stock-trading`）。別プロジェクトであり、
 *     旧契約（宣言的ルート）で木に載るため本マニフェストの射程外である（IADR-0124 決定 2）。
 *   - **画面でない feature ディレクトリ**（`scope-filter` 等。`sc<2 桁>-` に一致しないもの）。
 *   - **SC 番号のレンジ**（`.claude/rules/traceability.repo.md` の `SC-01..21`）。実在性は
 *     コミット件名・trace ブロックの検査器が持つ。ここで二重に持たない。
 *
 * 実行: node scripts/check-route-manifest.js [--self-test]
 */
const fs = require('fs');
const path = require('path');
const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');

const REPO = path.join(__dirname, '..');
/** マニフェストの在り処（単一情報源）。 */
const MANIFEST_REL = 'src/platform/frontend/src/app/routing/router.test.ts';
/** 画面 feature のディレクトリ名。`sc<2 桁>-<概要>`。 */
const SCREEN_DIR_RE = /^sc(\d{2})-/;
/** 除外の理由に求める最低の長さ（「—」「未定」で済ませられないようにする）。 */
const MIN_REASON_LENGTH = 10;

// --- 共通: コードの伏せ字化 --------------------------------------------------
//
// 意図的に誤例を書く場合はインラインコード／コードフェンスに入れる、という既存の逃げ道
// （`.claude/rules/traceability.md`）を本検査器でも用意する。長さを保って潰すのは、
// 行番号を元テキストと一致させたまま走査するためである。
const FENCE_LINE_RE = /^\s*(```|~~~)/;

function maskCode(text, { fences = false } = {}) {
  const out = [];
  let fenced = false;
  for (const line of String(text).split('\n')) {
    if (fences && FENCE_LINE_RE.test(line)) {
      fenced = !fenced;
      out.push(' '.repeat(line.length));
      continue;
    }
    if (fenced) {
      out.push(' '.repeat(line.length));
      continue;
    }
    // バッククォートの本数を合わせて対応付ける（CommonMark のコードスパン）。
    out.push(line.replace(/(`+)(?:(?!\1)[\s\S])*?\1/g, (m) => ' '.repeat(m.length)));
  }
  return out.join('\n');
}

/**
 * 閉じていないコードフェンスの行番号（閉じていれば null）。
 * 行ベースのトグルなので、閉じないフェンスが 1 本あると**以降のファイル全体が対象外**になる。
 * 黙って見逃さず違反として上げる。
 */
function unbalancedFenceLine(text) {
  let count = 0;
  let last = 0;
  String(text)
    .split('\n')
    .forEach((line, i) => {
      if (FENCE_LINE_RE.test(line)) {
        count++;
        last = i + 1;
      }
    });
  return count % 2 === 1 ? last : null;
}

function lineNumberAt(text, index) {
  let n = 1;
  for (let i = 0; i < index && i < text.length; i++) if (text[i] === '\n') n++;
  return n;
}

// --- 判定 1: マニフェストの解析 ----------------------------------------------

/** 行コメント（`//` 以降）を落とす。対象の文字列リテラルに `//` は現れない。 */
function stripLineComments(block) {
  return String(block)
    .split('\n')
    .map((line) => {
      const i = line.indexOf('//');
      return i === -1 ? line : line.slice(0, i);
    })
    .join('\n');
}

/**
 * `const <NAME> … = [ … ];` の中身を取り出す。見つからなければ null。
 * 閉じは**行頭の `];`**（prettier の整形結果。ネストした配列を持たない表であるため十分）。
 */
function arrayBlock(text, name) {
  const start = new RegExp(`const\\s+${name}\\b[^=]*=\\s*\\[`).exec(String(text));
  if (!start) return null;
  const from = start.index + start[0].length;
  const end = String(text).indexOf('\n];', from);
  return end === -1 ? null : String(text).slice(from, end);
}

/**
 * `['SC-NN', '…']` のタプルを拾う（prettier が改行しても拾えるよう空白を跨ぐ）。
 * @returns {{sc: string, value: string}[]}
 */
function tuples(block) {
  const out = [];
  const re = /'(SC-\d{2})'\s*,\s*'((?:[^'\\]|\\.)*)'/g;
  let m;
  while ((m = re.exec(stripLineComments(block)))) out.push({ sc: m[1], value: m[2] });
  return out;
}

/**
 * マニフェストのソースから 2 つの表を読む（純関数）。
 * @returns {{planned: {sc,value}[]|null, exempt: {sc,value}[]|null}}
 */
function parseManifest(text) {
  const plannedBlock = arrayBlock(text, 'PLANNED_ROUTES');
  const exemptBlock = arrayBlock(text, 'SCREENS_NOT_IN_THE_ROUTE_TABLE');
  return {
    planned: plannedBlock === null ? null : tuples(plannedBlock),
    exempt: exemptBlock === null ? null : tuples(exemptBlock),
  };
}

// --- 判定 1: 画面 feature の走査 ----------------------------------------------

/** `createRoute({ … path: '…' })` を持つか（＝ SPA にルートを持つ画面か）。 */
const CREATE_ROUTE_PATH_RE = /createRoute\(\{[\s\S]*?path:\s*'([^']*)'/g;

/**
 * feature ファイル群から画面を集める（純関数）。
 * @param {{relPath: string, text: string}[]} files
 * @returns {{sc: string, dir: string, paths: string[]}[]}
 */
function collectScreens(files) {
  /** @type {Map<string, {sc: string, dir: string, paths: string[]}>} */
  const byDir = new Map();
  for (const { relPath, text } of files) {
    const m = /\/features\/([^/]+)\//.exec(relPath);
    if (!m) continue;
    const dirName = m[1];
    const sc = SCREEN_DIR_RE.exec(dirName);
    if (!sc) continue; // 画面でない feature（scope-filter 等）
    CREATE_ROUTE_PATH_RE.lastIndex = 0;
    const paths = [];
    let hit;
    while ((hit = CREATE_ROUTE_PATH_RE.exec(text))) paths.push(hit[1]);
    if (paths.length === 0) continue;
    const id = `SC-${sc[1]}`;
    const key = relPath.slice(0, m.index + m[0].length);
    const prev = byDir.get(key);
    if (prev) prev.paths.push(...paths);
    else byDir.set(key, { sc: id, dir: key, paths });
  }
  return [...byDir.values()].sort((a, b) => a.sc.localeCompare(b.sc));
}

/**
 * 判定 1 の違反（純関数）。
 * @returns {{kind: string, sc?: string, detail: string}[]}
 */
function findManifestViolations(screens, { planned, exempt }) {
  const out = [];
  const plannedIds = new Set(planned.map((t) => t.sc));
  const exemptIds = new Set(exempt.map((t) => t.sc));

  // 順方向: 画面がどちらの表にも無い（＝ #1013 が突いた足し忘れ）。
  for (const s of screens) {
    if (plannedIds.has(s.sc) || exemptIds.has(s.sc)) continue;
    out.push({
      kind: 'missing-from-manifest',
      sc: s.sc,
      detail:
        `${s.dir} が ${s.paths.join(' / ')} を宣言しているのに、PLANNED_ROUTES にも ` +
        'SCREENS_NOT_IN_THE_ROUTE_TABLE にも無い（計画のルートパス表に載るなら前者へ、載らないなら理由つきで後者へ）',
    });
  }

  // 逆方向: 表にあるのに画面が無い（番号の取り違え・画面の削除）。
  const screenIds = new Set(screens.map((s) => s.sc));
  for (const t of [...planned, ...exempt]) {
    if (screenIds.has(t.sc)) continue;
    out.push({
      kind: 'unknown-in-manifest',
      sc: t.sc,
      detail: `マニフェストが ${t.sc} を挙げているが、ルートを宣言する画面 feature（sc<NN>-*）が無い`,
    });
  }

  // 両方に載っている（除外の意味が失われる）。
  for (const t of exempt) {
    if (plannedIds.has(t.sc)) {
      out.push({
        kind: 'in-both-lists',
        sc: t.sc,
        detail: `${t.sc} が PLANNED_ROUTES と除外の両方にある。除外は「計画の表に載らない」ことの表明である`,
      });
    }
  }

  // 除外の理由が空／短すぎる。
  for (const t of exempt) {
    if (t.value.trim().length < MIN_REASON_LENGTH) {
      out.push({
        kind: 'empty-reason',
        sc: t.sc,
        detail: `${t.sc} の除外理由が ${MIN_REASON_LENGTH} 文字未満である（黙って外す道を用意しない）`,
      });
    }
  }
  return out;
}

// --- 判定 2: 誤った主張の再混入 -----------------------------------------------

/**
 * 🔴 **文面のリテラル検査である。** 実測した 4 通りの言い回し（#1013 の母集合）を拾う。
 * 助詞を `も` / `が` に限るのが陰性対照の要 —— `は` は「ルートの実在**は** …が固定する」という
 * **正しい委譲**の形であり、落としてはならない。
 */
const CLAIM_PATTERNS = [
  {
    id: 'route-existence-also-fixed',
    re: /ルート(?:が実在すること|の実在)[」』]*\s*(?:も|が)\s*(?:同時に)?\s*固定(?:できる|される|する)/g,
    hint: 'E2E は未認証の導線しか測っていない（catch-all が認証ガード配下に居るため区別できない）。ルートの実在はルート木の走査（Vitest）が固定する',
  },
  {
    id: 'route-existence-label',
    re: /ルートの実在\s*[＋+]\s*認証ガード/g,
    hint: '観点の見出しが「ルートの実在」を含んでいる。E2E が測るのは認証ガードだけである',
  },
];

/**
 * 1 ファイル分の違反（純関数）。
 * @param {string} text
 * @param {{markdown?: boolean}} opts
 */
function findClaimViolations(text, opts = {}) {
  const src = String(text == null ? '' : text);
  const out = [];
  if (opts.markdown) {
    const fenceLine = unbalancedFenceLine(src);
    if (fenceLine !== null) {
      out.push({
        id: 'unbalanced-fence',
        line: fenceLine,
        matched: '```',
        hint: '閉じないコードフェンスがあり、以降のファイル全体が検査対象外になる',
      });
      return out;
    }
  }
  const scan = maskCode(src, { fences: Boolean(opts.markdown) });
  for (const p of CLAIM_PATTERNS) {
    p.re.lastIndex = 0;
    let m;
    while ((m = p.re.exec(scan))) {
      out.push({ id: p.id, line: lineNumberAt(scan, m.index), matched: m[0], hint: p.hint });
    }
  }
  return out;
}

// --- 走査（I/O） --------------------------------------------------------------

function walk(dir, accept, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === 'node_modules') continue;
      walk(p, accept, out);
    } else if (accept(e.name)) out.push(p);
  }
  return out;
}

const rel = (abs) => path.relative(REPO, abs).split(path.sep).join('/');
const read = (abs) => ({ relPath: rel(abs), text: fs.readFileSync(abs, 'utf8') });

/** `src/` 直下のユニットのうち、submodule でないもの。 */
function localUnits(repo = REPO) {
  const isExcluded = makeIsExcludedPath(excludedUnits(repo));
  let entries;
  try {
    entries = fs.readdirSync(path.join(repo, 'src'), { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((e) => e.isDirectory() && !isExcluded(`src/${e.name}/x`))
    .map((e) => e.name)
    .sort();
}

function collectFeatureFiles(repo = REPO) {
  const files = [];
  for (const unit of localUnits(repo)) {
    const dir = path.join(repo, 'src', unit, 'frontend', 'src', 'features');
    files.push(...walk(dir, (n) => n.endsWith('.ts') || n.endsWith('.tsx')).map(read));
  }
  return files;
}

function collectClaimFiles(repo = REPO) {
  const files = [];
  for (const unit of localUnits(repo)) {
    const dir = path.join(repo, 'src', unit, 'frontend', 'e2e');
    files.push(
      ...walk(dir, (n) => n.endsWith('.ts') || n.endsWith('.tsx'))
        .map(read)
        .map((f) => ({ ...f, markdown: false })),
    );
  }
  files.push(
    ...walk(path.join(repo, 'docs', 'tests'), (n) => n.endsWith('.md'))
      .map(read)
      .map((f) => ({ ...f, markdown: true })),
  );
  return files;
}

// --- 自己試験 -----------------------------------------------------------------

function selfTest() {
  const assert = require('assert');
  let passed = 0;
  const t = (name, fn) => {
    fn();
    passed++;
    process.stdout.write(`  ok  ${name}\n`);
  };

  const MANIFEST = [
    "const PLANNED_ROUTES: ReadonlyArray<readonly [string, string]> = [",
    "  ['SC-01', '/ask'],",
    "  // #917: コメントは無視する",
    "  ['SC-18', '/graph'],",
    "];",
    '',
    "const SCREENS_NOT_IN_THE_ROUTE_TABLE: ReadonlyArray<readonly [string, string]> = [",
    '  [',
    "    'SC-04',",
    "    '実体は別ホストの Wiki.js であり計画の表に SPA ルートが無い',",
    '  ],',
    '];',
  ].join('\n');

  const feature = (dir, p) => ({
    relPath: `src/knowledge/frontend/src/features/${dir}/routes/r.ts`,
    text: `export const c = (shell: S) =>\n  createRoute({\n    getParentRoute: () => shell,\n    path: '${p}',\n  });\n`,
  });

  t('マニフェストの 2 表を読める（複数行のタプルも拾う）', () => {
    const m = parseManifest(MANIFEST);
    assert.deepStrictEqual(
      m.planned.map((x) => [x.sc, x.value]),
      [
        ['SC-01', '/ask'],
        ['SC-18', '/graph'],
      ],
    );
    assert.strictEqual(m.exempt.length, 1);
    assert.strictEqual(m.exempt[0].sc, 'SC-04');
  });

  t('表が無ければ null を返す（0 件を「解析できた」と読ませない）', () => {
    const m = parseManifest('const OTHER = [];\n');
    assert.strictEqual(m.planned, null);
    assert.strictEqual(m.exempt, null);
  });

  t('画面 feature を集める（画面でないディレクトリは拾わない）', () => {
    const screens = collectScreens([
      feature('sc01-search', '/ask'),
      feature('sc18-graph', '/graph'),
      {
        relPath: 'src/knowledge/frontend/src/features/scope-filter/index.ts',
        text: "export const x = 1;\n",
      },
    ]);
    assert.deepStrictEqual(
      screens.map((s) => s.sc),
      ['SC-01', 'SC-18'],
    );
  });

  t('ルートを宣言しない feature は画面として数えない', () => {
    const screens = collectScreens([
      { relPath: 'src/knowledge/frontend/src/features/sc99-x/index.ts', text: 'export const x = 1;\n' },
    ]);
    assert.deepStrictEqual(screens, []);
  });

  t('★ 表と画面が揃っていれば違反 0 件', () => {
    const v = findManifestViolations(
      [...collectScreens([feature('sc01-search', '/ask'), feature('sc18-graph', '/graph')]), { sc: 'SC-04', dir: 'd', paths: ['/wiki'] }],
      parseManifest(MANIFEST),
    );
    assert.deepStrictEqual(v, []);
  });

  t('★ 画面を足して表へ書き忘れると落ちる（順方向・#1013 の欠陥そのもの）', () => {
    const screens = [
      ...collectScreens([feature('sc01-search', '/ask'), feature('sc18-graph', '/graph'), feature('sc19-private-notes', '/my/notes')]),
      { sc: 'SC-04', dir: 'd', paths: ['/wiki'] },
    ];
    const v = findManifestViolations(screens, parseManifest(MANIFEST));
    assert.strictEqual(v.length, 1, JSON.stringify(v));
    assert.strictEqual(v[0].kind, 'missing-from-manifest');
    assert.strictEqual(v[0].sc, 'SC-19');
  });

  t('★ 表の SC 番号を取り違えると落ちる（逆方向。Vitest は緑のままになる型）', () => {
    const mutated = MANIFEST.replace("['SC-18', '/graph']", "['SC-17', '/graph']");
    const screens = [
      ...collectScreens([feature('sc01-search', '/ask'), feature('sc18-graph', '/graph')]),
      { sc: 'SC-04', dir: 'd', paths: ['/wiki'] },
    ];
    const v = findManifestViolations(screens, parseManifest(mutated));
    assert.deepStrictEqual(
      v.map((x) => `${x.kind}:${x.sc}`).sort(),
      ['missing-from-manifest:SC-18', 'unknown-in-manifest:SC-17'],
    );
  });

  t('★ 除外を理由なしで宣言すると落ちる', () => {
    const mutated = MANIFEST.replace("'実体は別ホストの Wiki.js であり計画の表に SPA ルートが無い'", "'—'");
    const screens = [
      ...collectScreens([feature('sc01-search', '/ask'), feature('sc18-graph', '/graph')]),
      { sc: 'SC-04', dir: 'd', paths: ['/wiki'] },
    ];
    const v = findManifestViolations(screens, parseManifest(mutated));
    assert.strictEqual(v.length, 1, JSON.stringify(v));
    assert.strictEqual(v[0].kind, 'empty-reason');
  });

  t('★ 表と除外の両方に載せると落ちる', () => {
    const mutated = MANIFEST.replace("['SC-01', '/ask'],", "['SC-01', '/ask'],\n  ['SC-04', '/wiki'],");
    const screens = [
      ...collectScreens([feature('sc01-search', '/ask'), feature('sc18-graph', '/graph')]),
      { sc: 'SC-04', dir: 'd', paths: ['/wiki'] },
    ];
    const v = findManifestViolations(screens, parseManifest(mutated));
    assert.strictEqual(v.length, 1, JSON.stringify(v));
    assert.strictEqual(v[0].kind, 'in-both-lists');
  });

  // --- 判定 2 -----------------------------------------------------------------

  const claims = (s, markdown = false) => findClaimViolations(s, { markdown }).map((v) => v.id);

  t('★ 誤った主張を検出する（e2e の実文面）', () => {
    assert.deepStrictEqual(
      claims('// （ルートが登録されていないと NotFound が出て /login へ行かないため、この 1 本で\n//   「ルートが実在すること」も同時に固定できる）。'),
      ['route-existence-also-fixed'],
    );
  });

  t('★ 誤った主張を検出する（「この 1 本でルートの実在も固定できる」）', () =>
    assert.deepStrictEqual(claims('この 1 本でルートの実在も固定できる'), ['route-existence-also-fixed']));

  t('★ 誤った主張を検出する（受身「ルートの実在も同時に固定される」）', () =>
    assert.deepStrictEqual(claims('未認証で各ルートが /login へ誘導されること（ルートの実在も同時に固定される）'), [
      'route-existence-also-fixed',
    ]));

  t('★ 観点の見出しを検出する（「ルートの実在 ＋ 認証ガード」）', () =>
    assert.deepStrictEqual(claims('| E1 | ルートの実在 ＋ 認証ガード | 未認証で開くと /login へ |', true), [
      'route-existence-label',
    ]));

  // 陰性対照。**ここが落ちると、正しく書いた文書が赤くなり検査器ごと外される。**
  t('陰性対照: 否定形（固定できない）を落とさない', () =>
    assert.deepStrictEqual(claims('🔴 さらに、E2E は「ルートが実在すること」も固定できない（#918 で実測した）。'), []));

  t('陰性対照: 「固定しない」を落とさない', () =>
    assert.deepStrictEqual(claims('ログイン画面へ誘導される（認証ガードが先に効く。ルートの実在は固定しない）'), []));

  t('陰性対照: 別担当への委譲（は …が固定する／固定している）を落とさない', () => {
    assert.deepStrictEqual(claims('ルートの実在は router.test.ts が Vitest 側で固定している。'), []);
    assert.deepStrictEqual(claims('ルートの実在は T-30（ルート木の組み立て）が固定する。'), []);
    assert.deepStrictEqual(claims('（ルートの実在はここで固定する）'), []);
  });

  t('陰性対照: インラインコードに入れた誤例は対象外（既存の逃げ道と同じ）', () =>
    assert.deepStrictEqual(claims('誤例: `ルートの実在も固定できる` と書かない。', true), []));

  t('陰性対照: コードフェンスの中は対象外', () =>
    assert.deepStrictEqual(claims('```\nルートの実在も固定できる\n```\n', true), []));

  t('★ 閉じないフェンスは違反として上げる（盲目化を黙認しない）', () => {
    const v = findClaimViolations('```\nルートの実在も固定できる\n', { markdown: true });
    assert.strictEqual(v.length, 1, JSON.stringify(v));
    assert.strictEqual(v[0].id, 'unbalanced-fence');
  });

  t('非 markdown ではフェンス判定をしない（TS にフェンスの概念は無い）', () =>
    assert.deepStrictEqual(claims('```\n// 何も無い\n'), []));

  process.stdout.write(`\n✓ self-test: ${passed} 件すべて通過\n`);
}

// --- 入口 ---------------------------------------------------------------------

function main(argv) {
  if (argv.includes('--self-test')) {
    selfTest();
    return 0;
  }

  const manifestAbs = path.join(REPO, MANIFEST_REL);
  let manifestText;
  try {
    manifestText = fs.readFileSync(manifestAbs, 'utf8');
  } catch {
    console.error(`[check-route-manifest] マニフェストを読めません: ${MANIFEST_REL}`);
    console.error('  移設・改名したなら MANIFEST_REL を追随させてください（読めないまま緑は返しません）。');
    return 1;
  }

  const manifest = parseManifest(manifestText);
  // IADR-0130: 0 件走査で緑を返さない。**解析できなかったことと「違反 0 件」は区別する。**
  if (manifest.planned === null || manifest.exempt === null) {
    console.error(`[check-route-manifest] ${MANIFEST_REL} から表を読めません:`);
    if (manifest.planned === null) console.error('  - PLANNED_ROUTES が見つからない');
    if (manifest.exempt === null) console.error('  - SCREENS_NOT_IN_THE_ROUTE_TABLE が見つからない');
    console.error('  0 件解析は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }
  if (manifest.planned.length === 0) {
    console.error('[check-route-manifest] PLANNED_ROUTES が空です（0 件検査で緑は返しません）。');
    return 1;
  }

  const screens = collectScreens(collectFeatureFiles());
  if (screens.length === 0) {
    console.error('[check-route-manifest] ルートを宣言する画面 feature を 1 件も見つけられませんでした。');
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    return 1;
  }

  const claimFiles = collectClaimFiles();
  if (claimFiles.length === 0) {
    console.error('[check-route-manifest] 判定 2 の走査対象（e2e / docs/tests）が 0 件でした。');
    return 1;
  }

  const manifestViolations = findManifestViolations(screens, manifest);
  const claimViolations = [];
  for (const f of claimFiles) {
    for (const v of findClaimViolations(f.text, { markdown: f.markdown })) {
      claimViolations.push({ ...v, file: f.relPath });
    }
  }

  if (manifestViolations.length === 0 && claimViolations.length === 0) {
    console.log(
      `[check-route-manifest] OK: 画面 ${screens.length} 件とマニフェスト ` +
        `${manifest.planned.length} 行（除外 ${manifest.exempt.length} 件）が対応し、` +
        `${claimFiles.length} 件の e2e / 試験仕様に誤った主張はありません。`,
    );
    return 0;
  }

  if (manifestViolations.length > 0) {
    console.error(`[check-route-manifest] 判定 1（マニフェストの網羅）の違反 ${manifestViolations.length} 件:`);
    for (const v of manifestViolations) console.error(`  - [${v.kind}] ${v.detail}`);
    console.error(`  表は ${MANIFEST_REL} にあります。計画の正本は 05_screens §共通シェル「ルートパス」です。`);
  }
  if (claimViolations.length > 0) {
    console.error(`[check-route-manifest] 判定 2（誤った主張）の違反 ${claimViolations.length} 件:`);
    for (const v of claimViolations) {
      console.error(`  - ${v.file}:${v.line} [${v.id}] ${v.matched}`);
      console.error(`      ${v.hint}`);
    }
    console.error('  意図的に誤例を書く場合はインラインコード（`…`）かコードフェンスへ入れてください。');
  }
  return 1;
}

module.exports = {
  MANIFEST_REL,
  MIN_REASON_LENGTH,
  parseManifest,
  collectScreens,
  collectFeatureFiles,
  collectClaimFiles,
  findManifestViolations,
  findClaimViolations,
  maskCode,
};

if (require.main === module) process.exit(main(process.argv.slice(2)));
