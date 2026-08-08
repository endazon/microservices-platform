#!/usr/bin/env node
'use strict';
/*
 * check-adr-numbering.js
 * NFR / issue #581: 実装 ADR（IADR-xxxx）の採番の一意性・連続性・索引との一致を機械検査する。
 *
 * 規約は .claude/rules/traceability.md「採番衝突時の改番手順」——
 * **番号は先にマージした側が確保する（先着尊重）。後発は次の空き番号へ改番し、欠番を作らない。**
 *
 * 検査する判定（**件数はここに書かない**。判定は「関係」であって「N 件であること」ではない）:
 *   1. 番号の重複が無い
 *   2. 欠番が無い（`0000` から始まり最大番号まで連続）
 *   3. ファイル名の番号 = 本文の自称番号
 *   4. 索引（docs/adr/README.md）⇔ 実ファイルが**双方向**で過不足なし
 *   5. 索引の並びが昇順
 *   6. 索引行の「形」（ID セルがリンク形式か／リンク先ファイル名が当該 ID で始まるか／行末の閉じ `|`）
 *
 * **判定 6 は #580 から統合したものである**（#580 作業仕様書
 * `docs/specs/20260807_issue-580_adr-records-drift.md` の「#581 への申し送り」）。#580 は同じ検査を
 * `scripts/scripts.repo.test.js` の `inspectAdrIndex` ブロックに置き、「#581 が索引行をパースする
 * 時点で統合し、そちらからは削除する。**同じ不変条件の検査を 2 本残さない**」と申し送っていた。
 * 本検査が判定 4・5 のために索引行をパースするので、ここが統合先である。あわせて #580 が置いていた
 * fail-open 下限（索引行数 >= ADR 本体数）は、判定 4 の `index-missing` と `no-adr-files` が
 * **同じことをより直接に**見るため引き継がない（下限値という手作業の更新点も消える）。
 *
 * **この検査が防げないこと**（#581 が明記を求めた。IADR-0144 決定 3）:
 *   衝突の本体は「**並行 PR が互いの採番を見ない**」ことであり、本検査は develop に着地した後しか
 *   見られない。2 本の PR が同じ番号を持ったまま並行しても、**先にマージされた側は通り、後発の CI が
 *   初めて落ちる**。つまり**未然に防げない**。それでも価値はある —— 現状は後発も素通りし、
 *   人が気づくまで develop が壊れたままである（実際に衝突は 2 回起き、2 回とも人手で事後修復した）。
 *   規約の後半（欠番を作らない・重複を残さない）だけを機械化するものであり、**先着の調停はできない**。
 *
 * **検出しないもの**（IADR-0144「検出しないこと」と同じ列挙。ここを正とする）:
 *   - **索引の状態列と本体 `status:` の突合**。#580 はこれも #581 へ委ねたが、本 PR では実装しない
 *     ——索引の状態セルは `Superseded by IADR-XXXX` のように**後継 ID を含む自由文**であり、
 *     本体 `status:` の語（`Accepted` 等）と 1 対 1 に対応しない。突合するには先に語彙を規約化する
 *     必要があり、それは索引タイトル列の要約化（`adr-index-title-baseline.json` のラチェット）と
 *     同じ「先に是正、後から検査」の順序になる。**未実装であることをここに開示する**（#606 レビュー）。
 *   - **リンク先ファイルの実在**（`check-doc-links.js` の担当。二重に持たない）。
 *   - **索引タイトル列**（`scripts.repo.test.js` のラチェット。字義一致は方向が合わない）。
 *
 * **計画 ADR（`ADR-xxxx`）は対象外**——計画リポの所有物であり、本リポは pin を進めるだけで採番しない。
 * **`docs/superpowers/` も対象外**（保管された旧計画であり live な採番空間ではない）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-adr-numbering.js              # docs/adr/ を検査
 *   node scripts/check-adr-numbering.js --self-test  # 検査ロジック自体の自己試験
 *   node scripts/check-adr-numbering.js --dir <path> # 別ディレクトリを検査（自己試験が使う）
 *
 * CI への載せ方（.github/workflows/ は編集不可のため既存の呼び出し口へ相乗りする。IADR-0140 決定 2）:
 *   - scripts/scripts.repo.test.js から --self-test ＋ 実データ走査（ci.yml の scripts-tests ジョブ）
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const DEFAULT_DIR = path.join(REPO_ROOT, 'docs', 'adr');

/** ファイル名から番号を取る。`IADR-0143_xxx.md` → 143。 */
const FILE_RE = /^IADR-(\d{4})_.*\.md$/;
/** 索引行の判定と ID 抽出。**同じ式を `scripts.repo.test.js` のタイトル列ラチェットも使う**
 *  ——2 箇所に別々の式を書くと、片方だけ直したとき挙動が黙って割れる。 */
const INDEX_LINE_RE = /^\|\s*\[?IADR-\d{4}/;
const ID_RE = /IADR-(\d{4})/;
/** 索引行の ID セルがリンク形式か（判定 6。#580 から統合）。`[IADR-0001](./IADR-0001_x.md) |` を取る。 */
const INDEX_LINK_RE = /^\|\s*\[(IADR-\d{4})\]\(\.\/([^)]+)\)\s*\|/;

/** 本文の自称番号を探す範囲（冒頭）。frontmatter の title と H1 がここに入る。 */
const SELF_ID_HEAD_CHARS = 400;

/**
 * ディレクトリを検査し、違反の配列を返す。
 * @returns {{kind: string, detail: string}[]}
 */
function checkAdrNumbering(dir = DEFAULT_DIR) {
  const violations = [];
  const push = (kind, detail) => violations.push({ kind, detail });

  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch (e) {
    // ディレクトリが読めないのは「違反 0」ではなく検査不能。**fail-closed** にする
    // ——黙って緑になると、検査が静かに失効したことに誰も気づけない。
    push('dir-unreadable', `${dir} を読めない: ${e.message}`);
    return violations;
  }

  const files = entries.filter((f) => FILE_RE.test(f)).sort();
  if (files.length === 0) {
    push('no-adr-files', `${dir} に IADR-xxxx_*.md が 1 件も無い（検査対象ゼロは fail-closed）`);
    return violations;
  }

  // --- 判定 1: 重複 ---
  const byNum = new Map();
  for (const f of files) {
    const n = Number(f.match(FILE_RE)[1]);
    if (!byNum.has(n)) byNum.set(n, []);
    byNum.get(n).push(f);
  }
  for (const [n, fs_] of [...byNum].sort((a, b) => a[0] - b[0])) {
    if (fs_.length > 1) {
      push('duplicate-number', `IADR-${String(n).padStart(4, '0')} が ${fs_.length} 本: ${fs_.join(' / ')}`);
    }
  }

  // --- 判定 2: 欠番 ---
  // **起点を `nums[0]` にすると「先頭がまるごと欠けている」型を取りこぼす**（#606 レビュー）。
  // 採番は `IADR-0000` から始まる規約なので、下端は 0 に固定して数える。
  const nums = [...byNum.keys()].sort((a, b) => a - b);
  if (nums[0] !== 0) {
    push(
      'numbering-not-from-zero',
      `採番が IADR-0000 から始まっていない（最小は IADR-${String(nums[0]).padStart(4, '0')}）`
    );
  }
  for (let i = 0; i <= nums[nums.length - 1]; i++) {
    if (!byNum.has(i)) push('missing-number', `IADR-${String(i).padStart(4, '0')} が欠番`);
  }

  // --- 判定 3: ファイル名の番号 = 本文の自称番号 ---
  for (const f of files) {
    const id = `IADR-${f.match(FILE_RE)[1]}`;
    let head;
    try {
      head = fs.readFileSync(path.join(dir, f), 'utf8').slice(0, SELF_ID_HEAD_CHARS);
    } catch (e) {
      push('file-unreadable', `${f} を読めない: ${e.message}`);
      continue;
    }
    if (!head.includes(id)) {
      push('self-id-mismatch', `${f} の冒頭に自称番号 ${id} が無い（改番の追随漏れ）`);
    }
  }

  // --- 判定 4・5: 索引 ⇔ 実ファイル ---
  const readmePath = path.join(dir, 'README.md');
  let readme;
  try {
    readme = fs.readFileSync(readmePath, 'utf8');
  } catch (e) {
    push('index-unreadable', `${readmePath} を読めない: ${e.message}`);
    return violations;
  }

  const indexIds = [];
  readme.split('\n').forEach((line, i) => {
    if (!INDEX_LINE_RE.test(line)) return;
    indexIds.push(line.match(ID_RE)[1]);

    // --- 判定 6: 索引行の「形」（#580 から統合。同じ不変条件の検査を 2 本残さない） ---
    // `check-doc-links.js` は「あるリンクが壊れているか」しか見ず「**リンクが無いこと**」を
    // 検出しない。実際 #582 は新規行をリンク無しのプレーンテキストで足し、無検査で通過した。
    const n = i + 1;
    const linked = line.match(INDEX_LINK_RE);
    if (!linked) {
      push('index-not-linked', `README.md:${n} の ID セルがリンク形式でない`);
    } else if (!linked[2].startsWith(linked[1] + '_')) {
      push(
        'index-id-file-mismatch',
        `README.md:${n} のリンク先 ${linked[2]} が ${linked[1]}_ で始まらない（実在検査では捕まらない型）`
      );
    }
    // GFM は閉じ `|` が無くても描画するが、厳密なパーサ（本検査を含む）は落ちる。
    if (!/\|\s*$/.test(line)) push('index-no-trailing-pipe', `README.md:${n} に行末の閉じ | が無い`);
  });

  const fileIds = files.map((f) => f.match(FILE_RE)[1]);
  const indexSet = new Set(indexIds);
  const fileSet = new Set(fileIds);

  for (const id of fileIds) {
    if (!indexSet.has(id)) push('index-missing', `IADR-${id} が索引 README.md に無い`);
  }
  for (const id of indexIds) {
    if (!fileSet.has(id)) push('index-orphan', `索引 README.md の IADR-${id} に実ファイルが無い`);
  }
  // 索引に同じ ID が 2 行ある場合（過不足では出ないので別に見る）
  const seen = new Set();
  for (const id of indexIds) {
    if (seen.has(id)) push('index-duplicate', `索引 README.md に IADR-${id} が 2 行以上ある`);
    seen.add(id);
  }

  // --- 判定 5: 昇順 ---
  const sorted = [...indexIds].sort();
  if (indexIds.join(',') !== sorted.join(',')) {
    const firstBad = indexIds.findIndex((v, i) => v !== sorted[i]);
    push(
      'index-not-sorted',
      `索引 README.md の並びが昇順でない（最初のずれ: ${indexIds[firstBad]} の位置に ${sorted[firstBad]} が来るべき）`
    );
  }

  return violations;
}

function formatReport(violations) {
  return violations.map((v) => `    [${v.kind}] ${v.detail}`).join('\n');
}

// --- 自己試験 -------------------------------------------------------------------
//
// **実データが全判定 clean なので、検出力は変異でしか示せない。**
// 正例（壊すと落ちる）と負例（正常なら落ちない）を**対で**固定する。

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const os = require('os');

  /** 一時ディレクトリに ADR 群と索引を作る。**実データは汚さない。** */
  const makeTree = (spec) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'adrnum-'));
    for (const [file, body] of Object.entries(spec.files)) {
      fs.writeFileSync(path.join(dir, file), body);
    }
    fs.writeFileSync(path.join(dir, 'README.md'), spec.readme);
    return dir;
  };
  const kinds = (dir) => checkAdrNumbering(dir).map((v) => v.kind).sort();

  const okSpec = {
    files: {
      'IADR-0000_a.md': '# IADR-0000: a\n',
      'IADR-0001_b.md': '# IADR-0001: b\n',
      'IADR-0002_c.md': '# IADR-0002: c\n',
    },
    readme:
      '| ID | 決定 | 状態 |\n| --- | --- | --- |\n' +
      '| [IADR-0000](./IADR-0000_a.md) | a | Accepted |\n' +
      '| [IADR-0001](./IADR-0001_b.md) | b | Accepted |\n' +
      '| [IADR-0002](./IADR-0002_c.md) | c | Accepted |\n',
  };

  // --- 負例（M6）: 正常なツリーは落ちない ---
  t('M6 負例: 正常なツリーで違反 0', kinds(makeTree(okSpec)).length === 0, kinds(makeTree(okSpec)));

  // --- M1: 番号の重複 ---
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    s.files['IADR-0002_dup.md'] = '# IADR-0002: dup\n';
    s.readme += '| [IADR-0002](./IADR-0002_dup.md) | dup | Accepted |\n';
    const k = kinds(makeTree(s));
    t('M1: 同じ番号の ADR が 2 本 → duplicate-number', k.includes('duplicate-number'), k);
    t('M1: 索引の重複行も別途検出する', k.includes('index-duplicate'), k);
  }

  // --- M2: 欠番 ---
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    delete s.files['IADR-0001_b.md'];
    s.readme = s.readme.replace('| [IADR-0001](./IADR-0001_b.md) | b | Accepted |\n', '');
    const k = kinds(makeTree(s));
    t('M2: 番号を 1 つ飛ばす → missing-number', k.includes('missing-number'), k);
  }

  // --- M3: ファイル名と本文の自称番号のずれ ---
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    s.files['IADR-0002_c.md'] = '# IADR-0009: 改番の追随漏れ\n';
    const k = kinds(makeTree(s));
    t('M3: 自称番号がファイル名と違う → self-id-mismatch', k.includes('self-id-mismatch'), k);
  }

  // --- M4: 索引の過不足（双方向） ---
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    s.readme = s.readme.replace('| [IADR-0001](./IADR-0001_b.md) | b | Accepted |\n', '');
    const k = kinds(makeTree(s));
    t('M4a: 索引から 1 行消す → index-missing', k.includes('index-missing'), k);
  }
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    s.readme += '| [IADR-0007](./IADR-0007_ghost.md) | 実体が無い | Accepted |\n';
    const k = kinds(makeTree(s));
    t('M4b: 索引にだけ 1 行足す → index-orphan', k.includes('index-orphan'), k);
  }

  // --- M5: 索引の並び ---
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    s.readme =
      '| ID | 決定 | 状態 |\n| --- | --- | --- |\n' +
      '| [IADR-0002](./IADR-0002_c.md) | c | Accepted |\n' +
      '| [IADR-0000](./IADR-0000_a.md) | a | Accepted |\n' +
      '| [IADR-0001](./IADR-0001_b.md) | b | Accepted |\n';
    const k = kinds(makeTree(s));
    t('M5: 索引の並びを入れ替える → index-not-sorted', k.includes('index-not-sorted'), k);
  }

  // --- M7: 採番の下端（先頭がまるごと欠けている型。#606 レビュー） ---
  {
    const s = { files: {}, readme: '| ID | 決定 | 状態 |\n| --- | --- | --- |\n' };
    for (const id of ['0001', '0002']) {
      s.files[`IADR-${id}_x.md`] = `# IADR-${id}: x\n`;
      s.readme += `| [IADR-${id}](./IADR-${id}_x.md) | x | Accepted |\n`;
    }
    const k = kinds(makeTree(s));
    // 起点を最小番号にすると連続なので緑になってしまう型。下端を 0 に固定して初めて落ちる。
    t('M7: 採番が IADR-0001 から始まる → numbering-not-from-zero', k.includes('numbering-not-from-zero'), k);
    t('M7: 併せて IADR-0000 を欠番として数える', k.includes('missing-number'), k);
  }

  // --- M8〜M10: 索引行の「形」（#580 から統合した判定 6） ---
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    s.readme = s.readme.replace('| [IADR-0001](./IADR-0001_b.md) |', '| IADR-0001 |');
    const k = kinds(makeTree(s));
    t('M8: 索引の ID セルからリンクを外す → index-not-linked', k.includes('index-not-linked'), k);
  }
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    // #580 着手時の実測: IADR-0082 の行が実際にこの状態だった（GFM は描画するが厳密解析は落ちる）。
    s.readme = s.readme.replace('| [IADR-0000](./IADR-0000_a.md) | a | Accepted |', '| [IADR-0000](./IADR-0000_a.md) | a | Accepted');
    const k = kinds(makeTree(s));
    t('M9: 行末の閉じパイプを外す → index-no-trailing-pipe', k.includes('index-no-trailing-pipe'), k);
  }
  {
    const s = JSON.parse(JSON.stringify(okSpec));
    // 実在するファイルを指しているのでリンク切れ検査（check-doc-links.js）では捕まらない型。
    s.readme = s.readme.replace('[IADR-0001](./IADR-0001_b.md)', '[IADR-0001](./IADR-0000_a.md)');
    const k = kinds(makeTree(s));
    t('M10: ID とリンク先ファイル名の食い違い → index-id-file-mismatch', k.includes('index-id-file-mismatch'), k);
  }

  // --- fail-closed: 検査不能を「違反 0」にしない ---
  {
    const empty = fs.mkdtempSync(path.join(os.tmpdir(), 'adrnum-empty-'));
    fs.writeFileSync(path.join(empty, 'README.md'), '');
    const k = kinds(empty);
    t('fail-closed: ADR が 1 件も無いディレクトリは緑にしない', k.includes('no-adr-files'), k);
  }
  {
    const k = checkAdrNumbering(path.join(os.tmpdir(), 'adrnum-does-not-exist-xyz')).map((v) => v.kind);
    t('fail-closed: 読めないディレクトリは緑にしない', k.includes('dir-unreadable'), k);
  }

  // --- 索引行の抽出が scripts.repo.test.js と同じ形であること ---
  t('索引行の抽出: 見出し行や本文の IADR 言及を拾わない', (() => {
    const s = JSON.parse(JSON.stringify(okSpec));
    s.readme = '本文で IADR-0009 に言及する。\n\n' + s.readme;
    return checkAdrNumbering(makeTree(s)).length === 0;
  })());

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-adr-numbering] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-adr-numbering] 自己試験 ${cases.length} 件 all passed。`);
}

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }
  const dirIdx = argv.indexOf('--dir');
  const dir = dirIdx >= 0 ? path.resolve(argv[dirIdx + 1]) : DEFAULT_DIR;

  const violations = checkAdrNumbering(dir);
  if (violations.length === 0) {
    console.log('[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。');
    process.exit(0);
  }
  console.error(`[check-adr-numbering] 採番の違反 ${violations.length} 件を検出しました:`);
  console.error(formatReport(violations));
  console.error(
    '\n規約（.claude/rules/traceability.md「採番衝突時の改番手順」）: ' +
      '番号は先にマージした側が確保する（先着尊重）。後発は次の空き番号へ改番し、欠番を作らない。\n' +
      '改番時はファイル名・本文の自称番号・索引・関連仕様書・コード内コメント・**PR タイトル**を追随させる。\n'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  checkAdrNumbering,
  formatReport,
  selfTest,
  FILE_RE,
  INDEX_LINE_RE,
  INDEX_LINK_RE,
  DEFAULT_DIR,
};
