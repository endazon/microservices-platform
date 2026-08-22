#!/usr/bin/env node
'use strict';
/*
 * check-xunit1051-ratchet.js
 * NFR / issue #882 / IADR-0238: xUnit1051（TestContext.Current.CancellationToken）の
 * 段階採用を ratchet で守る。
 *
 * 背景（この検査が無いと何が起きるか）:
 *   `TreatWarningsAsErrors` は **false** である。したがって `NoWarn` を外しただけでは、
 *   剥がしたプロジェクトで xUnit1051 が再発しても **CI は緑のまま**になる。
 *   「剥がしたら 0 件」を機械で担保しているのは `src/Directory.Build.props` の
 *   **`WarningsAsErrors`** のほうであり、本検査はその配線が**外れていないこと**を見る。
 *
 * 🔴 **抑止は WarningsAsErrors に勝つ**（#882 で実測）。`.editorconfig` の
 *   `dotnet_diagnostic.xUnit1051.severity = none` も `<NoWarn>xUnit1051</NoWarn>` も、
 *   WarningsAsErrors を**黙って無効化する**。つまり移行済みプロジェクトへ後から抑止を足せば、
 *   ビルドは緑に戻り ratchet は外れる。**判定 6 がこれを止める。**
 *
 * 🔴 **Contains / EndsWith は序数・大文字小文字を区別する**（#882 で実測）。許可リストの綴りを
 *   誤ると、そのプロジェクトは NoWarn も WarningsAsErrors も付かない状態になり、
 *   **警告は出るが CI は緑**になる。**判定 3 がこれを止める**（許可リストの項目は実在必須）。
 *
 * 検査する判定（**件数はここに書かない**。判定は「関係」であって「N 件であること」ではない）:
 *   1. `added`      … 追跡下に在るテストプロジェクトが baseline に無い
 *   2. `removed`    … baseline に在るテストプロジェクトが追跡下に無い
 *   3. `props-desync` … baseline の migrated 集合 ≠ props の XUnit1051Migrated 集合
 *   4. `migrated-nonzero` … migrated:true なのに remaining が 0 でない
 *   5. `zero-not-locked`  … remaining 0 なのに migrated でも deferReason 付きでもない
 *                           （0 件になったら**必ず**剥がすか、剥がさない理由を書かせる）
 *   6. `stray-suppression` … 追跡下の .editorconfig / .csproj / .props / .targets / .ruleset に
 *                           xUnit1051 が現れる（`src/Directory.Build.props` の正規の 1 箇所を除く）
 *
 *      🔴 判定 6 は**ファイル全文の単純な部分一致**であり、意図的に過剰検出へ倒してある。
 *      構文を解釈して「抑止の指定だけ」を拾う実装にすると、`.editorconfig` の書き方
 *      （`[*.cs]` セクションの継承・`severity` 以外の指定）や XML コメントの形を取りこぼし、
 *      **偽陰性＝ratchet が黙って外れる**方向に倒れる。この検査の目的からすると偽陰性のほうが
 *      危険なので、**言及しただけでも落とす**。
 *      **偽陽性を踏んだ場合**（設定ファイルへ「xUnit1051 は抑止していない」旨のコメントを
 *      書きたい等）は、①その説明を本 ADR / 作業仕様書側へ移して設定ファイルには書かない、
 *      ②どうしても必要なら `SANCTIONED_SUPPRESSION` を配列化して理由付きで足す、のいずれかを採る。
 *      **除外を足すときは理由を必ず書くこと**（黙って足すと判定 6 は空洞化する）。
 *   7. fail-closed   … テストプロジェクトが 1 件も見つからない／props や baseline が読めない
 *
 * **この検査が防げないこと**（開示する。IADR-0144 決定 3 と同じ作法）:
 *   - **未移行プロジェクトの remaining が増えたこと**を検出しない。実数を得るにはビルドが要り、
 *     本検査は依存ゼロ・静的である。未移行プロジェクトは NoWarn で抑止されているため、
 *     増えても CI は緑であり、それは**設計どおり**である（段階採用の途中だから抑止している）。
 *     移行済みプロジェクトの再発は**ビルドが**止める（WarningsAsErrors）。役割を二重に持たない。
 *   - **baseline の remaining が実測値かどうか**を検出しない。数え直しは人が
 *     `dotnet build <slnx> -t:Rebuild -p:NoWarn= -m:1` で行う（採り方は baseline の $comment）。
 *   - **src/ai-stock-trading（AST submodule）**は対象外。別プロジェクトであり、
 *     許可リスト方式のため WarningsAsErrors は AST へ届かない（`check-backend-libraries.js` と同じ扱い）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 *
 * 使い方:
 *   node scripts/check-xunit1051-ratchet.js              # 実データを検査
 *   node scripts/check-xunit1051-ratchet.js --self-test  # 検査ロジック自体の自己試験
 *
 * CI への載せ方（IADR-0140 決定 2。既存の呼び出し口へ相乗りする）:
 *   - scripts/scripts.repo.test.js から --self-test ＋ 実データ走査（ci.yml の scripts-tests ジョブ）
 */
const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');
const BASELINE_PATH = path.join(REPO_ROOT, 'scripts', 'xunit1051-baseline.json');
const PROPS_PATH = path.join(REPO_ROOT, 'src', 'Directory.Build.props');

/** テストプロジェクトを探す根。AST submodule は含めない（別プロジェクト）。 */
const SCAN_ROOTS = ['src', 'templates'];
/** 走査から外すディレクトリ。 */
const EXCLUDED_DIR_RE = /^(?:\.git|node_modules|bin|obj|ai-stock-trading|\.template-buildcheck-.*)$/;
/** テストプロジェクトの判定。props の `EndsWith('Tests')` と**同じ形**でなければならない。 */
const TEST_PROJECT_RE = /Tests\.csproj$/;
/** props の許可リスト。 */
const MIGRATED_PROP_RE = /<XUnit1051Migrated>([^<]*)<\/XUnit1051Migrated>/;
/** 判定 6 の走査対象。 */
const SUPPRESSION_FILE_RE = /(?:\.editorconfig|\.csproj|\.props|\.targets|\.ruleset)$/;
/** 判定 6 で唯一許す出現箇所（リポジトリ相対・POSIX 区切り）。 */
const SANCTIONED_SUPPRESSION = 'src/Directory.Build.props';

/** 常に POSIX 区切りのリポジトリ相対パスにする（Windows でも baseline のキーと一致させる）。 */
function toRepoRelative(absPath) {
  return path.relative(REPO_ROOT, absPath).split(path.sep).join('/');
}

/** SCAN_ROOTS 以下を歩いて、条件に合うファイルのリポジトリ相対パスを返す。 */
function walk(root, matchRe) {
  const found = [];
  const stack = [path.join(REPO_ROOT, root)];
  while (stack.length > 0) {
    const dir = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch (e) {
      continue; // 読めないディレクトリ（未 populate な submodule 等）は素通りする
    }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (!EXCLUDED_DIR_RE.test(entry.name)) stack.push(full);
      } else if (matchRe.test(entry.name)) {
        found.push(toRepoRelative(full));
      }
    }
  }
  return found.sort();
}

/** props から許可リストの集合を読む。読めなければ null（fail-closed は呼び出し側で判定）。 */
function readMigratedFromProps(propsText) {
  const m = propsText.match(MIGRATED_PROP_RE);
  if (!m) return null;
  return new Set(
    m[1]
      .split(';')
      .map((s) => s.trim())
      .filter((s) => s.length > 0)
  );
}

/**
 * 検査本体。
 * @param {{repoRoot?: string, baseline?: object, propsText?: string, projects?: string[], suppressionFiles?: {path: string, text: string}[]}} opts
 * @returns {{kind: string, detail: string}[]}
 */
function checkXUnit1051Ratchet(opts = {}) {
  const violations = [];
  const push = (kind, detail) => violations.push({ kind, detail });

  // --- 入力（既定は実データ。自己試験は差し替える） ---
  let baseline = opts.baseline;
  if (baseline === undefined) {
    try {
      baseline = JSON.parse(fs.readFileSync(BASELINE_PATH, 'utf8'));
    } catch (e) {
      push('baseline-unreadable', `${toRepoRelative(BASELINE_PATH)} を読めない: ${e.message}`);
      return violations;
    }
  }
  let propsText = opts.propsText;
  if (propsText === undefined) {
    try {
      propsText = fs.readFileSync(PROPS_PATH, 'utf8');
    } catch (e) {
      push('props-unreadable', `${toRepoRelative(PROPS_PATH)} を読めない: ${e.message}`);
      return violations;
    }
  }

  const entries = (baseline && baseline.projects) || null;
  if (!entries || typeof entries !== 'object') {
    push('baseline-malformed', 'baseline に projects オブジェクトが無い');
    return violations;
  }

  const discovered =
    opts.projects !== undefined
      ? [...opts.projects].sort()
      : SCAN_ROOTS.flatMap((r) => walk(r, TEST_PROJECT_RE));

  // --- 判定 7: fail-closed ---
  // 走査が空振りしたのは「違反 0」ではなく検査不能である。黙って緑になると、
  // 除外パターンの書き間違いで検査が静かに失効したことに誰も気づけない。
  if (discovered.length === 0) {
    push('no-test-projects', `${SCAN_ROOTS.join(' / ')} にテストプロジェクトが 1 件も無い（fail-closed）`);
    return violations;
  }

  const migratedInProps = readMigratedFromProps(propsText);
  if (migratedInProps === null) {
    push('props-no-allowlist', `${SANCTIONED_SUPPRESSION} に XUnit1051Migrated が無い（配線が外れている）`);
    return violations;
  }

  // --- 判定 1・2: baseline ⇔ 実在の双方向 ---
  const baselineKeys = new Set(Object.keys(entries));
  const discoveredSet = new Set(discovered);
  for (const p of discovered) {
    if (!baselineKeys.has(p)) {
      push('added', `${p} が baseline に無い（新規プロジェクトは remaining:0 ＋ migrated:true で登録すること）`);
    }
  }
  for (const p of baselineKeys) {
    if (!discoveredSet.has(p)) push('removed', `baseline の ${p} が実在しない（移動・改名の追随漏れ）`);
  }

  // --- 判定 4・5: baseline 内の整合 ---
  const migratedInBaseline = new Set();
  for (const [p, e] of Object.entries(entries)) {
    const name = e && e.project;
    const remaining = e && e.remaining;
    if (typeof name !== 'string' || typeof remaining !== 'number') {
      push('entry-malformed', `${p} の project / remaining が無い（または型が違う）`);
      continue;
    }
    if (e.migrated === true) {
      migratedInBaseline.add(name);
      if (remaining !== 0) {
        push('migrated-nonzero', `${name} は migrated:true なのに remaining が ${remaining} 件ある`);
      }
    } else if (remaining === 0 && !e.deferReason) {
      // 0 件になったら剥がす。剥がさないなら理由を書かせる（黙って放置させない）。
      push(
        'zero-not-locked',
        `${name} は remaining 0 だが migrated でも deferReason 付きでもない（剥がすか、剥がさない理由を書く）`
      );
    }
  }

  // --- 判定 3: baseline の migrated 集合 ⇔ props の許可リスト ---
  // 2 つのリストを別々に持つと片方だけ腐る。ここで必ず一致させる。
  // 綴り・大文字小文字の誤りもここで落ちる（props 側は序数比較なので静かに効かなくなる）。
  for (const name of migratedInProps) {
    if (!migratedInBaseline.has(name)) {
      push('props-desync', `props の XUnit1051Migrated に ${name} が在るが baseline が migrated:true にしていない`);
    }
  }
  for (const name of migratedInBaseline) {
    if (!migratedInProps.has(name)) {
      push('props-desync', `baseline が ${name} を migrated:true にしているが props の XUnit1051Migrated に無い`);
    }
  }

  // --- 判定 6: 抑止の混入（WarningsAsErrors を黙って無効化する） ---
  const suppressionFiles =
    opts.suppressionFiles !== undefined
      ? opts.suppressionFiles
      : SCAN_ROOTS.flatMap((r) => walk(r, SUPPRESSION_FILE_RE))
          .concat(fs.existsSync(path.join(REPO_ROOT, '.editorconfig')) ? ['.editorconfig'] : [])
          .map((p) => {
            try {
              return { path: p, text: fs.readFileSync(path.join(REPO_ROOT, p), 'utf8') };
            } catch (e) {
              return { path: p, text: '' };
            }
          });
  for (const f of suppressionFiles) {
    if (f.path === SANCTIONED_SUPPRESSION) continue;
    if (f.text.includes('xUnit1051')) {
      push(
        'stray-suppression',
        `${f.path} に xUnit1051 が現れる（抑止は WarningsAsErrors に勝つ。抑止は ${SANCTIONED_SUPPRESSION} の 1 箇所に限る）`
      );
    }
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

  const P_OK = 'src/a/Alpha.Tests/Alpha.Tests.csproj';
  const P_DEBT = 'src/b/Beta.Tests/Beta.Tests.csproj';
  const P_DEFER = 'src/c/Gamma.Tests/Gamma.Tests.csproj';

  const okWorld = () => ({
    projects: [P_OK, P_DEBT, P_DEFER],
    propsText: '<XUnit1051Migrated>;Alpha.Tests;</XUnit1051Migrated>',
    suppressionFiles: [{ path: 'src/Directory.Build.props', text: '<NoWarn>$(NoWarn);xUnit1051</NoWarn>' }],
    baseline: {
      projects: {
        [P_OK]: { project: 'Alpha.Tests', remaining: 0, migrated: true },
        [P_DEBT]: { project: 'Beta.Tests', remaining: 7, migrated: false },
        [P_DEFER]: { project: 'Gamma.Tests', remaining: 0, migrated: false, deferReason: '並行 PR と衝突するため' },
      },
    },
  });
  const kinds = (world) => checkXUnit1051Ratchet(world).map((v) => v.kind).sort();

  // --- 負例（M0）: 正常な世界は落ちない ---
  t('M0 負例: 正常な世界で違反 0', kinds(okWorld()).length === 0, kinds(okWorld()));

  // --- M1: baseline に無いプロジェクトが実在する ---
  {
    const w = okWorld();
    w.projects.push('src/d/Delta.Tests/Delta.Tests.csproj');
    const k = kinds(w);
    t('M1: baseline に無いプロジェクトを足す → added', k.includes('added'), k);
  }

  // --- M2: baseline から 1 本消す（＝実在するのに載っていない） ---
  {
    const w = okWorld();
    delete w.baseline.projects[P_DEBT];
    const k = kinds(w);
    t('M2: baseline から 1 本消す → added', k.includes('added'), k);
  }

  // --- M3: 実在しないプロジェクトが baseline に残る ---
  {
    const w = okWorld();
    w.projects = w.projects.filter((p) => p !== P_DEBT);
    const k = kinds(w);
    t('M3: 実在しないものが baseline に残る → removed', k.includes('removed'), k);
  }

  // --- M4: props と baseline の食い違い（両方向） ---
  {
    const w = okWorld();
    w.propsText = '<XUnit1051Migrated>;</XUnit1051Migrated>';
    const k = kinds(w);
    t('M4a: baseline は migrated だが props の許可リストに無い → props-desync', k.includes('props-desync'), k);
  }
  {
    const w = okWorld();
    w.propsText = '<XUnit1051Migrated>;Alpha.Tests;Beta.Tests;</XUnit1051Migrated>';
    const k = kinds(w);
    t('M4b: props にだけ在る → props-desync', k.includes('props-desync'), k);
  }
  {
    // 🔴 序数比較なので綴り違いは props 側では静かに効かなくなる。ここで落とす。
    const w = okWorld();
    w.propsText = '<XUnit1051Migrated>;alpha.tests;</XUnit1051Migrated>';
    const k = kinds(w);
    t('M4c: 許可リストの大文字小文字を崩す → props-desync', k.includes('props-desync'), k);
  }

  // --- M5: migrated なのに残件がある ---
  {
    const w = okWorld();
    w.baseline.projects[P_OK].remaining = 3;
    const k = kinds(w);
    t('M5: migrated:true で remaining>0 → migrated-nonzero', k.includes('migrated-nonzero'), k);
  }

  // --- M6: 0 件になったのに剥がしも理由付けもしない ---
  {
    const w = okWorld();
    w.baseline.projects[P_DEBT].remaining = 0;
    const k = kinds(w);
    t('M6: remaining 0 で migrated も deferReason も無い → zero-not-locked', k.includes('zero-not-locked'), k);
  }
  {
    // 負例: deferReason が在れば落ちない（M6 と対）
    const k = kinds(okWorld());
    t('M6 負例: deferReason 付きの 0 件は落ちない', !k.includes('zero-not-locked'), k);
  }

  // --- M7: 抑止の混入（WarningsAsErrors を黙って無効化する） ---
  {
    const w = okWorld();
    w.suppressionFiles.push({ path: '.editorconfig', text: 'dotnet_diagnostic.xUnit1051.severity = none' });
    const k = kinds(w);
    t('M7a: .editorconfig へ severity=none を足す → stray-suppression', k.includes('stray-suppression'), k);
  }
  {
    const w = okWorld();
    w.suppressionFiles.push({ path: P_OK, text: '<NoWarn>xUnit1051</NoWarn>' });
    const k = kinds(w);
    t('M7b: 移行済み csproj へ NoWarn を足す → stray-suppression', k.includes('stray-suppression'), k);
  }

  // --- fail-closed: 検査不能を「違反 0」にしない ---
  {
    const w = okWorld();
    w.projects = [];
    const k = kinds(w);
    t('fail-closed: プロジェクトが 1 件も見つからない → no-test-projects', k.includes('no-test-projects'), k);
  }
  {
    const w = okWorld();
    w.propsText = '<Project></Project>';
    const k = kinds(w);
    t('fail-closed: props から許可リストが消える → props-no-allowlist', k.includes('props-no-allowlist'), k);
  }
  {
    const k = checkXUnit1051Ratchet({ baseline: {}, propsText: '', projects: [P_OK] }).map((v) => v.kind);
    t('fail-closed: baseline に projects が無い → baseline-malformed', k.includes('baseline-malformed'), k);
  }

  // --- 走査の形が props と一致していること ---
  t(
    "テストプロジェクトの判定は props の EndsWith('Tests') と同じ形",
    TEST_PROJECT_RE.test('Foo.Tests.csproj') && !TEST_PROJECT_RE.test('Foo.TestSupport.csproj')
  );
  t('AST submodule は走査から外れる', EXCLUDED_DIR_RE.test('ai-stock-trading'));
  t('雛形の複製先は走査から外れる', EXCLUDED_DIR_RE.test('.template-buildcheck-unit-template'));

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) {
      failed++;
      if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual));
    }
  }
  if (failed) {
    console.error(`[check-xunit1051-ratchet] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-xunit1051-ratchet] 自己試験 ${cases.length} 件 all passed。`);
}

function main() {
  if (process.argv.slice(2).includes('--self-test')) {
    selfTest();
    return;
  }
  const violations = checkXUnit1051Ratchet();
  if (violations.length === 0) {
    console.log(
      '[check-xunit1051-ratchet] OK: baseline と実在プロジェクトが双方向で一致し、' +
        '許可リストは props と揃い、抑止の混入もありません。'
    );
    process.exit(0);
  }
  console.error(`[check-xunit1051-ratchet] 違反 ${violations.length} 件を検出しました:`);
  console.error(formatReport(violations));
  console.error(
    '\n段階採用の規約（IADR-0238）: 1 PR = 1 プロジェクト。移行したら baseline を migrated:true にし、\n' +
      'src/Directory.Build.props の XUnit1051Migrated へ同じ名前を足す。**剥がしたら戻れない。**\n' +
      '残件の数え直しは dotnet build <slnx> -t:Rebuild -p:NoWarn= -m:1（採り方は baseline の $comment）。\n'
  );
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  checkXUnit1051Ratchet,
  formatReport,
  selfTest,
  readMigratedFromProps,
  TEST_PROJECT_RE,
  EXCLUDED_DIR_RE,
  BASELINE_PATH,
  PROPS_PATH,
};
