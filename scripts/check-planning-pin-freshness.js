#!/usr/bin/env node
'use strict';
/*
 * check-planning-pin-freshness.js — 計画 pin と planning 既定ブランチの乖離のうち、
 * **実装の着手可否に効くもの**を検知する（issue #589 / IADR-0170）。
 *
 * 背景:
 *   IADR-0119 決定 2 の着手条件には「前提 ADR が Accepted になること」が含まれる。計画側で
 *   裁定が反映されても、本リポの pin を進めるまで実装側には何も伝わらない。**待ち時間の実体は
 *   「回答待ち」ではなく「回答に気づいていない時間」だった**（#572 施策 7 の訂正）。
 *   同型は #548 / #560 / #589 の 3 回とも「人が気づいて起票」で処理されている。
 *   **自動化するのは検知ではなく、その手作業（起票）そのものである。**
 *
 * 方針:
 *   - **落とさない（fail-open）。** pin を進める判断は人・AI が行う。赤にすると pin が
 *     古い間ずっと CI が止まる（#589 の指定）。通知手段は「赤」ではなく issue にする。
 *   - **「検査していない」と「乖離なし」を読み分けられるようにする。** planning が未 populate
 *     なら、その旨を出して exit 0 する。**黙って緑を返さない**（#546 / #664 / #674 の型）。
 *   - **鳴りすぎると読まれなくなる。** 着手可否に効く差分だけを鳴らす（判断 3）。
 *   - **比較の向きを検査する（#749 / IADR-0202 案 B）。** submodule の `origin` が pin より
 *     後ろにあると `git diff <新しい pin> <古い ref>` という逆方向の比較になり、
 *     **分類器が正しく動いたまま「効く変更はありません」と報告する**。祖先判定で止める。
 *   - **比較元をどこから取ったかを必ず出力する（#749 受け入れ基準 3）。** 出力を読んでも
 *     比較相手が分からなければ、誤りに気づけない（実際に気づけなかった）。
 *
 * 外部依存ゼロ（Node 標準モジュールのみ。scripts/ に依存解決の経路が無い）。
 */

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { warn } = require('./lib/ci-annotate.js');

const REPO_ROOT = path.join(__dirname, '..');
const PLANNING = 'planning';

// --- 判断 3: 「着手可否に効く」の定義 ---------------------------------------
//
// 鳴らす対象を絞る。全差分で鳴らすと、pin が古い間ずっと同じ内容が鳴り続け、読まれなくなる。
// 実測（2026-08-11・pin 2cf0795 → planning HEAD 14aed71）では、3 コミット 22 ファイルの
// 差分のうち、この規則で拾うのは ADR の status 変化 1 件と要求の変更 1 件である。

/**
 * ★★ **本リポジトリが実装するプロジェクトに限る。**
 *
 * 計画リポには**3 つのプロジェクト**が同居する（実測: `ai-stock-trading` 28 件 /
 * `microservices-platform` 45 件 / `mondriq` 6 件の ADR）。**`projects/<any>/` で拾うと
 * AST や mondriq の ADR まで「本リポの着手ゲート」として鳴る。**
 *
 * IADR-0119 / IADR-0142 が定めるのは **microservices-platform の FR-17〜21** の話であり、
 * 他プロジェクトの ADR が `Accepted` になっても本リポの着手可否とは無関係である。
 *
 * ★ **これは `.claude/rules/traceability.md` が繰り返し記録している「AST / MSP の ID 名前空間衝突」
 *   と同型である。** 初版はプロジェクトを絞っておらず、**実データで AST の ADR を 1 件拾っていた**
 *   （`ADR-0003_ai-decision-guardrails.md`。`status` が変わっていなかったため鳴らなかっただけ）。
 */
const PLANNING_PROJECT = 'microservices-platform';

/** ADR ファイル（status の変化を見る）。 */
const ADR_RE = new RegExp(`^projects/${PLANNING_PROJECT}/07_adr/ADR-\\d+[^/]*\\.md$`);

/**
 * 受け入れ基準が載る計画書（変更そのものを見る）。
 *
 * ★ **限界**: 直下の 1 階層だけを見る。実データでは両ディレクトリに `.md` は 1 件ずつしか無い
 *   （`mockups/` 配下は `.html`）が、**将来ここに `.md` が増設されると無警告で `ignored` へ落ちる。**
 */
const GATE_DOC_RE = new RegExp(`^projects/${PLANNING_PROJECT}/(02_requirements|05_screens)/[^/]+\\.md$`);

/**
 * 変更ファイル一覧を「着手可否に効くもの」と「効かないもの」へ仕分ける。純関数。
 *
 * @param {string[]} files planning リポジトリ内の相対パス
 * @returns {{adr: string[], gateDocs: string[], ignored: string[]}}
 */
function classifyChanges(files) {
  const adr = [];
  const gateDocs = [];
  const ignored = [];
  for (const f of files) {
    if (ADR_RE.test(f)) adr.push(f);
    else if (GATE_DOC_RE.test(f)) gateDocs.push(f);
    else ignored.push(f);
  }
  return { adr, gateDocs, ignored };
}

/** frontmatter の `status:` を 1 件読む。読めなければ null。 */
function statusOf(text) {
  if (typeof text !== 'string') return null;
  const m = text.match(/^status:\s*(.+?)\s*$/m);
  return m ? m[1] : null;
}

/**
 * ADR の status 変化を求める。純関数。
 *
 * ★ **「変化した」だけでなく「Proposed → Accepted」かどうかを別に持つ。**
 *   着手条件に関わるのはこの遷移であり、他の変化（Accepted → Superseded 等）とは
 *   意味が違う。まとめると、鳴らす理由が読めなくなる。
 *   **ただし「Accepted になった」は「着手できる」ではない**（下の becameAccepted 参照）。
 *
 * @param {{file: string, before: string|null, after: string|null}[]} pairs
 */
function adrStatusChanges(pairs) {
  const out = [];
  for (const { file, before, after } of pairs) {
    const b = statusOf(before);
    const a = statusOf(after);
    if (b === a) continue;
    out.push({
      file,
      before: b,
      after: a,
      // ★ **「Accepted になった」だけを言う。「着手できる」とは言わない。**
      //   IADR-0119 は「**前提 ADR が全部 Accepted になった**」ことと「**保留が全部外れた**」ことは
      //   別だと明記している —— FR-19/20 は **着手する範囲が「覆り得る範囲」の外である**ことも要り
      //   （IADR-0142 が IADR-0119 決定 2 を部分改定した。範囲の正は計画 ADR-0037 の着手可否の注記）、
      //   FR-21 は計画側の確定（fixed）が要る。
      //   同書はこの取り違えが**実際に起きた**とも記録している（一括りにした追記の是正）。
      //   検知器が「ゲートが外れた」と名乗ると、同じ取り違えを機械が量産する。
      becameAccepted: b === 'Proposed' && a === 'Accepted',
    });
  }
  return out;
}

/**
 * 検知結果を組み立てる。純関数（git を触らない）。
 *
 * @param {{pin: string, head: string, files: string[], adrPairs: object[]}} input
 */
function findIssues(input) {
  const { pin, head, files, adrPairs } = input;
  if (pin === head) return { drifted: false, reasons: [] };
  const { adr, gateDocs, ignored } = classifyChanges(files);
  const statusChanges = adrStatusChanges(adrPairs);
  const reasons = [];
  for (const c of statusChanges) {
    reasons.push({
      kind: c.becameAccepted ? 'adr-accepted' : 'adr-status-changed',
      file: c.file,
      detail: `${c.before} → ${c.after}`,
    });
  }
  for (const f of gateDocs) {
    reasons.push({ kind: 'gate-doc-changed', file: f, detail: '受け入れ基準が変わった可能性' });
  }
  return {
    drifted: true,
    reasons,
    counts: { adr: adr.length, gateDocs: gateDocs.length, ignored: ignored.length },
  };
}

// --- git 側（副作用あり） ---------------------------------------------------

/**
 * planning サブモジュールが populate 済みか（`check-doc-links.js` の作法を踏襲）。
 * CI が submodule なしで checkout すると空プレースホルダになるため、存在チェックだけでは足りない。
 */
function planningPopulated(root = REPO_ROOT) {
  try {
    return fs.existsSync(path.join(root, PLANNING, 'projects'));
  } catch (e) {
    return false;
  }
}

function git(args, opts = {}) {
  return execFileSync('git', args, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, ...opts }).trim();
}

/** 本リポが pin している planning の commit。 */
function pinnedCommit(root = REPO_ROOT) {
  try {
    const line = git(['-C', root, 'ls-tree', 'HEAD', PLANNING]);
    const m = line.match(/^\d+ commit ([0-9a-f]{40})\t/);
    return m ? m[1] : null;
  } catch (e) {
    return null;
  }
}

/**
 * 比較元（planning の既定ブランチ）を解決する。**「どこから取ったか」を必ず一緒に返す**（#749）。
 *
 * ★ 旧実装は commit の文字列だけを返しており、**出力を読んでも比較相手が分からなかった。**
 *   #749 では submodule の `origin` が GitHub ではなく隣接クローンを指していたが、
 *   その事実が出力のどこにも現れず、誤りに気づけなかった。
 *
 * @returns {{commit: string, ref: string, remoteUrl: string|null, fetch: 'ok'|'failed'|'skipped'}|null}
 */
function resolveComparisonSource(root = REPO_ROOT, { fetch = true } = {}) {
  const dir = path.join(root, PLANNING);
  let fetchState = 'skipped';
  if (fetch) {
    // 失敗しても続行する（オフライン・認証なしの環境がある）。
    try {
      git(['-C', dir, 'fetch', '--quiet', 'origin'], { timeout: 60_000 });
      fetchState = 'ok';
    } catch (e) {
      fetchState = 'failed';
    }
  }
  let remoteUrl = null;
  try {
    remoteUrl = git(['-C', dir, 'remote', 'get-url', 'origin']);
  } catch (e) {
    /* remote 未設定。null のまま出力へ出す（黙って隠さない） */
  }
  for (const ref of ['origin/HEAD', 'origin/main', 'origin/master']) {
    try {
      return { commit: git(['-C', dir, 'rev-parse', ref]), ref, remoteUrl, fetch: fetchState };
    } catch (e) {
      /* 次の候補へ */
    }
  }
  return null;
}

/** remote URL がネットワーク越しの upstream ではなくローカルパスを指しているか。純関数。 */
function isLocalPathRemote(remoteUrl) {
  if (!remoteUrl) return false;
  return !/^(https?:|git:|ssh:|file:|[^/\\]+@)/.test(remoteUrl);
}

/**
 * 比較元の説明を 1 行で組み立てる。純関数（受け入れ基準 3: どこから取ったかを出力に含める）。
 */
function describeSource(src) {
  if (!src) {
    return '比較元: 解決できません（planning に origin/HEAD・origin/main・origin/master のいずれもありません）';
  }
  const state =
    { ok: 'fetch 成功', failed: 'fetch 失敗（ローカルの参照のまま）', skipped: 'fetch 省略' }[src.fetch] ?? src.fetch;
  const url = src.remoteUrl ?? '不明（origin が未設定）';
  const note = isLocalPathRemote(src.remoteUrl)
    ? ' ★ origin が upstream ではなくローカルパスを指しています（更新されていない可能性）'
    : '';
  return `比較元: planning の ${src.ref} = ${src.commit.slice(0, 7)}（remote origin = ${url} / ${state}）${note}`;
}

// --- #749: 比較の向き（案 B: 祖先判定） --------------------------------------
//
// **逆方向の比較を「乖離なし」と報告しない。** submodule の `origin` が pin より後ろにあると、
// `git diff <新しい pin> <古い ref>` は「pin にあって比較元に無いもの」しか返さない。
// #749 ではそれが draft / tools / 索引 66 件となり、分類器が正しく「効かない」と判定して緑になった。
// **分類器は正しく、入力が壊れていた。** 入力の妥当性はここで見る。

const RELATION = {
  /** pin == 比較元。 */
  SAME: 'same',
  /** pin が比較元の祖先＝比較元のほうが新しい。**正しい向き。** */
  FORWARD: 'forward',
  /** 比較元が pin の祖先＝比較元のほうが古い。**比較が成立しない**（#749）。 */
  REVERSE: 'reverse',
  /** どちらも祖先でない。比較元が別系統を指している。 */
  DIVERGED: 'diverged',
  /** 判定できない（浅いクローン等）。**従来どおり続行するが、その旨を出力へ添える。** */
  UNKNOWN: 'unknown',
};

/**
 * 祖先関係から pin と比較元の位置関係を決める。純関数。
 *
 * @param {{pin: string, head: string, pinIsAncestorOfHead: boolean|null, headIsAncestorOfPin: boolean|null}} input
 */
function classifyRelation({ pin, head, pinIsAncestorOfHead, headIsAncestorOfPin }) {
  if (pin === head) return RELATION.SAME;
  if (pinIsAncestorOfHead === null || headIsAncestorOfPin === null) return RELATION.UNKNOWN;
  if (pinIsAncestorOfHead) return RELATION.FORWARD;
  if (headIsAncestorOfPin) return RELATION.REVERSE;
  return RELATION.DIVERGED;
}

/**
 * `git merge-base --is-ancestor a b`。true / false / null（判定不能）。
 * exit 1 は「祖先でない」、それ以外の失敗（オブジェクトが無い等）は判定不能として null を返す。
 */
function isAncestor(dir, a, b) {
  try {
    execFileSync('git', ['-C', dir, 'merge-base', '--is-ancestor', a, b], { stdio: 'ignore' });
    return true;
  } catch (e) {
    return e && e.status === 1 ? false : null;
  }
}

/** pin と比較元の位置関係を実際の git で求める。 */
function relationOf(root, pin, head) {
  const dir = path.join(root, PLANNING);
  return classifyRelation({
    pin,
    head,
    pinIsAncestorOfHead: isAncestor(dir, pin, head),
    headIsAncestorOfPin: isAncestor(dir, head, pin),
  });
}

/** pin..head の変更ファイル一覧と、ADR の前後本文を集める。 */
function collect(root, pin, head) {
  const dir = path.join(root, PLANNING);
  const files = git(['-C', dir, 'diff', '--name-only', pin, head])
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean);
  const { adr } = classifyChanges(files);
  const adrPairs = adr.map((f) => {
    const show = (rev) => {
      try {
        return git(['-C', dir, 'show', `${rev}:${f}`]);
      } catch (e) {
        return null; // 追加・削除された ADR
      }
    };
    return { file: f, before: show(pin), after: show(head) };
  });
  return { files, adrPairs };
}

// --- 自己試験 ---------------------------------------------------------------

function selfTest() {
  let failed = 0;
  const t = (name, cond) => {
    if (!cond) {
      failed++;
      console.error(`  NG  ${name}`);
    } else {
      console.log(`  ok  ${name}`);
    }
  };

  // 判断 3: 仕分け
  const c = classifyChanges([
    'projects/microservices-platform/07_adr/ADR-0023_edge-cert.md',
    'projects/microservices-platform/02_requirements/01_requirements.md',
    'projects/microservices-platform/05_screens/01_screens.md',
    'draft/feedback/20260807_x.md',
    'tools/impl-sync/sync-impl-adr.js',
    'projects/microservices-platform/INDEX.md',
    'projects/microservices-platform/07_adr/README.md',
  ]);
  t('仕分け: ADR を拾う', c.adr.length === 1);
  t('仕分け: 要求・画面を拾う', c.gateDocs.length === 2);
  t('仕分け: draft / tools / INDEX は拾わない', c.ignored.length === 4);
  t('仕分け: 07_adr/README.md は ADR ではない', !c.adr.includes('projects/microservices-platform/07_adr/README.md'));

  // status の読み取り
  t('status を読む', statusOf('---\ntitle: x\nstatus: Accepted\n---\n') === 'Accepted');
  t('status が無ければ null', statusOf('---\ntitle: x\n---\n') === null);
  t('本文が無ければ null', statusOf(null) === null);

  // status 変化
  const ch = adrStatusChanges([
    { file: 'a.md', before: 'status: Proposed\n', after: 'status: Accepted\n' },
    { file: 'b.md', before: 'status: Accepted\n', after: 'status: Superseded\n' },
    { file: 'c.md', before: 'status: Accepted\n', after: 'status: Accepted\n' },
  ]);
  t('status 変化を 2 件拾う（変化なしは拾わない）', ch.length === 2);
  t('Proposed → Accepted だけを becameAccepted とする', ch.filter((x) => x.becameAccepted).length === 1);
  t('Accepted → Superseded は becameAccepted ではない', ch.find((x) => x.file === 'b.md').becameAccepted === false);

  // ★ 新規追加された ADR（before が null）でも落ちない
  const added = adrStatusChanges([{ file: 'n.md', before: null, after: 'status: Accepted\n' }]);
  t('新規 ADR: before が null でも拾う', added.length === 1 && added[0].before === null);
  t('新規 ADR は becameAccepted ではない（Proposed からの遷移ではない）', added[0].becameAccepted === false);

  // #749: 比較の向き（案 B）
  const rel = (a, b) => classifyRelation({ pin: 'p', head: 'h', pinIsAncestorOfHead: a, headIsAncestorOfPin: b });
  t('向き: pin == head なら same', classifyRelation({ pin: 'x', head: 'x' }) === RELATION.SAME);
  t('向き: pin が比較元の祖先なら forward（正しい向き）', rel(true, false) === RELATION.FORWARD);
  t('向き: 比較元が pin の祖先なら reverse（#749 の型）', rel(false, true) === RELATION.REVERSE);
  t('向き: どちらも祖先でなければ diverged', rel(false, false) === RELATION.DIVERGED);
  t('向き: 判定不能なら unknown（黙って forward にしない）', rel(null, false) === RELATION.UNKNOWN);
  t('向き: 逆側が判定不能でも unknown', rel(true, null) === RELATION.UNKNOWN);

  // #749: 比較元の説明（受け入れ基準 3）
  const src = { commit: 'a'.repeat(40), ref: 'origin/main', remoteUrl: 'https://github.com/e/p.git', fetch: 'ok' };
  t('比較元: ref と commit と remote を出す', /origin\/main = aaaaaaa/.test(describeSource(src)) && describeSource(src).includes('https://github.com/e/p.git'));
  t('比較元: fetch の状態を出す', describeSource({ ...src, fetch: 'failed' }).includes('fetch 失敗'));
  t('比較元: 解決できないことを明示する', describeSource(null).includes('解決できません'));
  t(
    '比較元: origin がローカルパスなら注意を添える（#749 の根本原因）',
    describeSource({ ...src, remoteUrl: '/home/user/project-planning' }).includes('ローカルパス'),
  );
  t('比較元: http(s) はローカルパス扱いしない', isLocalPathRemote('https://github.com/e/p.git') === false);
  t('比較元: ssh 短縮形（git@…）もローカルパス扱いしない', isLocalPathRemote('git@github.com:e/p.git') === false);
  t('比較元: 相対パスもローカルパス', isLocalPathRemote('../project-planning') === true);

  // findIssues
  t(
    'pin == head なら乖離なし',
    findIssues({ pin: 'x', head: 'x', files: [`projects/${PLANNING_PROJECT}/07_adr/ADR-1_a.md`], adrPairs: [] }).drifted === false,
  );
  const r = findIssues({
    pin: 'a',
    head: 'b',
    files: [`projects/${PLANNING_PROJECT}/02_requirements/01_requirements.md`, 'draft/x.md'],
    adrPairs: [],
  });
  t('要求の変更で鳴る', r.drifted && r.reasons.some((x) => x.kind === 'gate-doc-changed'));
  const r2 = findIssues({
    pin: 'a',
    head: 'b',
    files: ['draft/x.md', 'tools/y.js'],
    adrPairs: [],
  });
  // ★ 乖離はあるが、着手可否に効く変更は無い ＝ 理由 0 件。
  t('draft / tools だけの差分では理由が 0 件', r2.drifted && r2.reasons.length === 0);

  console.log(failed === 0 ? '[check-planning-pin-freshness] self-test OK' : `NG ${failed} 件`);
  return failed === 0 ? 0 : 1;
}

// --- main -------------------------------------------------------------------

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) process.exit(selfTest());
  const noFetch = argv.includes('--no-fetch');
  // `--root <path>`: 検査対象のリポジトリルートを差し替える。**回帰テスト（fixture）が
  // プロセスとして本体を走らせるために要る** —— gitlink の読み取りと祖先判定は実物の
  // git リポジトリを要し、純関数だけでは #749 の型（入力が壊れている）を再現できない。
  const rootArg = argv.indexOf('--root');
  const root = rootArg >= 0 ? path.resolve(argv[rootArg + 1] || '.') : REPO_ROOT;

  // 判断 4: 「検査していない」と「乖離なし」を読み分ける。
  if (!planningPopulated(root)) {
    console.log(
      '[check-planning-pin-freshness] planning サブモジュールが未 populate のため検査していません' +
        '（PR CI は submodule を取得しない）。乖離が無いことを意味しません。',
    );
    process.exit(0);
  }
  // ★ 門: 対象プロジェクトが存在しないのに緑を返さない。**改名・移動が起きると
  //   classifyChanges が全件を ignored へ落とし、「着手可否に効く変更はありません」と
  //   静かに報告し続ける**（#664 / IADR-0130 の「0 件走査で緑を返さない」と同型）。
  if (!fs.existsSync(path.join(root, PLANNING, 'projects', PLANNING_PROJECT))) {
    console.error(
      `[check-planning-pin-freshness] planning に projects/${PLANNING_PROJECT} がありません。` +
        ' 対象プロジェクトの改名・移動を疑ってください（黙って 0 件検査へ落ちないよう fail させています）。',
    );
    process.exit(1);
  }
  const pin = pinnedCommit(root);
  const source = resolveComparisonSource(root, { fetch: !noFetch });
  // 受け入れ基準 3（#749）: **比較元をどこから取ったかを、どの経路でも必ず出す。**
  const sourceLine = describeSource(source);
  const head = source ? source.commit : null;
  if (!pin || !head) {
    console.log(
      `[check-planning-pin-freshness] 比較対象を取得できないため検査していません（pin=${pin ?? '不明'} / HEAD=${head ?? '不明'}）。\n` +
        `  ${sourceLine}`,
    );
    process.exit(0);
  }
  if (pin === head) {
    console.log(
      `[check-planning-pin-freshness] OK: pin は比較元と一致しています（${pin.slice(0, 7)}）。\n  ${sourceLine}`,
    );
    process.exit(0);
  }

  // ★★ #749: 向きを見る。**逆方向の比較で「効く変更はありません」と報告しない。**
  const relation = relationOf(root, pin, head); // ← 変異点（この判定を外すと #749 が再発する）
  if (relation === RELATION.REVERSE || relation === RELATION.DIVERGED) {
    const detail =
      relation === RELATION.REVERSE
        ? '比較元が pin より**後ろ**にあります（逆方向の比較）'
        : '比較元と pin が**分岐**しています（別系統を指しています）';
    const broken = [
      `比較できていません: ${detail}。`,
      `  ${sourceLine}`,
      `  pin: ${pin.slice(0, 7)}`,
      '  差分を取っても「pin にあって比較元に無いもの」しか出ず、計画側の新しい変更は 1 件も見えません。',
      '  **よって乖離の有無は判定できていません。差分が draft だけに見えても、それは向きのせいです**（#749）。',
      '  submodule の origin が古いローカルクローンを指していないか確認してください。',
    ].join('\n');
    warn(broken, { prefix: '[check-planning-pin-freshness] ' });
    if (process.env.GITHUB_OUTPUT) {
      fs.appendFileSync(process.env.GITHUB_OUTPUT, `comparison=${relation}\n`);
    }
    if (process.env.PIN_REPORT_PATH) {
      fs.writeFileSync(process.env.PIN_REPORT_PATH, `${broken}\n`);
    }
    // fail-open は維持する（受け入れ基準 2。セッション開始・CI を止めない）。
    process.exit(0);
  }
  // 向きを判定できない場合（浅いクローン等）は続行するが、断定しない。
  const unknownNote =
    relation === RELATION.UNKNOWN ? '\n  ★ 比較の向きを判定できませんでした（履歴が浅い可能性）。' : '';

  const { files, adrPairs } = collect(root, pin, head);
  // 0 件走査の門: pin != head なのに差分が 1 件も無いのは、配管が壊れている合図である
  // （#664 / IADR-0130 の作法。ここは fail-open の例外とし、黙って緑を返さない）。
  if (files.length === 0) {
    console.error(
      `[check-planning-pin-freshness] pin (${pin.slice(0, 7)}) と HEAD (${head.slice(0, 7)}) が異なるのに差分が 0 件でした。` +
        ' 比較の配管が壊れている可能性があります。',
    );
    process.exit(1);
  }

  const result = findIssues({ pin, head, files, adrPairs });
  const lines = [
    `計画 pin が ${pin.slice(0, 7)} のままで、比較元は ${head.slice(0, 7)} です。`,
    `  ${sourceLine}`,
  ];
  if (result.reasons.length === 0) {
    console.log(
      `[check-planning-pin-freshness] pin は古いですが、着手可否に効く変更はありません` +
        `（${files.length} 件の差分はすべて draft / tools / 索引）。\n  ${sourceLine}${unknownNote}`,
    );
    process.exit(0);
  }
  for (const r of result.reasons) {
    lines.push(`  [${r.kind}] ${r.file} — ${r.detail}`);
  }
  const accepted = result.reasons.filter((r) => r.kind === 'adr-accepted');
  if (accepted.length) {
    // ★ 「着手できる」とは書かない（上の becameAccepted のコメント参照）。
    lines.push(
      `  ★ ${accepted.length} 件の ADR が Proposed → Accepted になりました。` +
        'IADR-0119 決定 2 の着手条件の**一部**です —— **範囲基準（IADR-0142）や計画側の確定が' +
        '別に要る要求があります**。保留が外れたかは IADR-0119 と IADR-0142 を読んで判断してください。',
    );
  }
  lines.push('  pin を進めるか判断してください（本検査は落としません）。');
  const message = lines.join('\n') + unknownNote;
  // 落とさない。ただし GitHub Actions では注釈として出す（緑のログに埋もれさせない）。
  // `warn` が Actions / ローカルの出し分けを持つので、ここで二重に出さない。
  warn(message, { prefix: '[check-planning-pin-freshness] ' });
  // ★ 呼び出し側（ワークフロー）が issue を起票できるよう、機械可読な出力も残す。
  if (process.env.GITHUB_OUTPUT) {
    fs.appendFileSync(process.env.GITHUB_OUTPUT, `drifted=true\nreasons=${result.reasons.length}\n`);
  }
  // ★ issue の本文に使う素のテキスト。**stdout を tee で拾わない** ——
  //   Actions 上の stdout は `::warning::…`（注釈の書式）であり、そのまま issue へ貼ると読めない。
  //   1 回の実行で「注釈」と「素のテキスト」を別々の出口へ出す（2 回走らせて結果がずれるのを避ける）。
  if (process.env.PIN_REPORT_PATH) {
    fs.writeFileSync(process.env.PIN_REPORT_PATH, `${message}\n`);
  }
  process.exit(0);
}

if (require.main === module) main();

module.exports = {
  PLANNING_PROJECT,
  classifyChanges,
  statusOf,
  adrStatusChanges,
  findIssues,
  planningPopulated,
  pinnedCommit,
  resolveComparisonSource,
  describeSource,
  isLocalPathRemote,
  classifyRelation,
  relationOf,
  RELATION,
  collect,
  ADR_RE,
  GATE_DOC_RE,
};
