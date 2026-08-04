#!/usr/bin/env node
'use strict';
/*
 * check-coverage-floor.js
 * バックエンドのカバレッジ床（floor）を強制する（NFR, Issue #453 / #468）。外部依存ゼロ。
 *
 * 背景:
 *   フロントは src/vitest.config.ts の thresholds を frontend-tests.yml が強制している（IADR-0034）。
 *   一方バックエンドは ci.yml が `--collect:"XPlat Code Coverage"` で**収集はするが閾値を強制して
 *   いなかった**（閾値強制はコメントアウトされた例として置かれたままだった）。全面再実装（#454）で
 *   11 サービスを作り直す間、テストが薄いまま置き換わっても CI が緑のままになる穴であり、
 *   #453 の受け入れ観点「カバレッジ floor が再実装前の水準を下回ったままマージできない」が
 *   塞ごうとしているのはここである。
 *
 * 方式（IADR-0118 決定 1 / IADR-0123）:
 *   reportgenerator 等のツール導入を要さず、`dotnet test --collect:"XPlat Code Coverage"` が出力する
 *   Cobertura XML（src 配下の coverage.cobertura.xml すべて）を直接読み、行/分岐の被覆率を集計する。
 *   ツール不要のため CI が速く、オフラインでも動く。
 *
 *   集計は各ファイルの line-rate を平均するのではなく、**全ファイルの行数で加重**する
 *   （小さいファイルが多いと単純平均は実態より高く出るため）。
 *
 *   **行は <class filename> でユニットへ帰属させ、集計対象外ユニット（submodule）の行を落とす**
 *   （#468 / IADR-0123 決定 1）。レポートファイルのパスによる除外だけでは、BFF の合成点
 *   （Platform.Bff → AiStockTrading.Bff.Endpoints）経由で src/platform/ 配下のレポートの**中身**に
 *   入り込む他ユニットの行に届かない。
 *
 *   **二重記載の扱い**（IADR-0123 決定 3）: coverlet の Cobertura は同じ行を <methods> 配下と
 *   class 直下の <lines> の両方に書く。集計は **class 直下の <lines> を正**とし、<methods> 配下は
 *   内訳として数えない。両方数えるとメソッドを持つ行だけが 2 票を持ち、メソッド外の行との重みが崩れる
 *   （素朴な <line> カウントが計測条件で振れる原因でもある。PR #464 のレビューで 266 行 / 230 行と
 *   実測が割れた）。前提が実レポートで正しいかは、<coverage> の lines-valid / lines-covered
 *   （coverlet 自身の集計値）との照合として毎回診断へ出す（IADR-0123 決定 4）。
 *
 * 使い方:
 *   node scripts/check-coverage-floor.js                 # 既定の探索パスから集計し床と比較
 *   node scripts/check-coverage-floor.js --report-only   # 集計だけ行い、床未達でも exit 0
 *   node scripts/check-coverage-floor.js --self-test
 *   COVERAGE_FLOOR_DEBUG=1 node scripts/check-coverage-floor.js   # レポート単位の診断も出す
 */
const fs = require('fs');
const path = require('path');
const { notice, warn } = require('./lib/ci-annotate');
const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const FLOOR_FILE = path.join(REPO_ROOT, 'src', 'coverage-floor.json');
const SEARCH_ROOT = 'src';
const SKIP_DIRS = new Set(['node_modules', '.git', 'dist']);

/**
 * 集計対象外のユニット。ci.yml の build-and-test は全ユニットの backend.slnx を自動発見して
 * test するため（AST を含む）、除外しないと AST のカバレッジが合算される。
 *
 * AST は独自の計画・ADR を持つ別プロジェクト（submodule）であり、本床の目的は
 * 「#454 で platform / knowledge を作り直す間の退行を止める」ことである。合算すると双方向に濁る:
 *   - AST 側のテストが厚ければ platform / knowledge の実際の退行を薄めて隠す
 *   - AST の pin 更新だけで、無関係な PR の床判定が動く
 * PR 本文が「単純平均は実態より高く出る」として単一プロジェクト内で加重平均を採ったのと同じ問題が、
 * プロジェクト間でも起きる（PR #464 のレビュー指摘）。check-test-traceability.js /
 * check-backend-libraries.js の EXCLUDED_UNITS と同じ切り分けに揃える。
 *
 * 値は .gitmodules（src/<unit> の submodule）から導出する。3 検査器が同じ集合を独立に
 * ハードコードしていた形は、submodule ユニットの追加（IADR-0056 決定 6）で 3 箇所同時に
 * 狭すぎになるため単一情報源へ寄せた（issue #473。規則は scripts/lib/excluded-units.js）。
 *
 * 除外は 2 面で効かせる（IADR-0123 決定 1）。
 *   1. レポートファイルのパス（isExcludedPath）— 除外ユニット配下のレポートは読まない
 *   2. 行の帰属（<class filename> → unitOfFilename）— 合成点経由でレポートの中身に混入する行
 */
const EXCLUDED_UNITS = excludedUnits({ root: REPO_ROOT });

/** リポジトリ相対パスが集計対象外ユニット配下か。 */
const isExcludedPath = makeIsExcludedPath(EXCLUDED_UNITS);

// --- 純粋ロジック ---------------------------------------------------------------

function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/** XML の実体参照を戻す（パスに現れうる最小限のみ）。 */
function decodeXmlEntities(s) {
  return String(s)
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');
}

/** 属性文字列から name の値を取り出す（" と ' の双方に対応。無ければ null）。 */
function attrOf(attrs, name) {
  const m = new RegExp(`\\b${name}\\s*=\\s*("([^"]*)"|'([^']*)')`).exec(String(attrs));
  if (!m) return null;
  return decodeXmlEntities(m[2] !== undefined ? m[2] : m[3]);
}

/**
 * <sources><source>…</source></sources> の値を返す。
 * coverlet は「全ソースファイルのうち最も浅いディレクトリ」を base path として出し、base path で
 * 始まらないファイルは filename に**絶対パスのまま**書く（GetBasePaths / GetRelativePathFromBase）。
 * deterministic build 指定時は空の <source> になる（filename が /_/src/… の形になる）。
 * 空文字は結合に使えないため落とす。
 */
function parseSources(xml) {
  const block = /<sources>([\s\S]*?)<\/sources>/i.exec(String(xml));
  if (!block) return [];
  const out = [];
  const re = /<source>([\s\S]*?)<\/source>/gi;
  let m;
  while ((m = re.exec(block[1])) !== null) {
    const v = decodeXmlEntities(m[1]).trim();
    if (v) out.push(toPosix(v));
  }
  return out;
}

/** <coverage …> の集計値（coverlet 自身が書いた値）。無ければ null。IADR-0123 決定 4 の照合に使う。 */
function parseReportedTotals(xml) {
  const m = /<coverage\b([^>]*)>/i.exec(String(xml));
  if (!m) return null;
  const num = (name) => {
    const v = attrOf(m[1], name);
    return v === null || v === '' || Number.isNaN(Number(v)) ? null : Number(v);
  };
  const t = {
    lines: num('lines-valid'),
    covered: num('lines-covered'),
    branches: num('branches-valid'),
    coveredBranches: num('branches-covered'),
  };
  return t.lines === null && t.covered === null ? null : t;
}

/**
 * <class …> … </class> を切り出す。あわせて class の外側のテキストも返す
 * （どの class にも属さない <line> は帰属できない＝除外できないため、件数を可視化する）。
 * Cobertura の <class> は入れ子にならないため、閉じタグの単純探索で足りる。
 *
 * 開始タグの走査は**引用符を跨がない**形にする。属性値に `>` が現れると（非同期ステートマシンの
 * `Foo/<Map>d__2` のような名前。書き手が `>` を実体参照へ落とさない場合）単純な [^>]* ではタグを
 * 途中で切ってしまい、後続の filename 属性を読めず**そのクラスだけ静かに未帰属**になる。
 * 未帰属は集計に残る＝除外が効かないため、除外対象が抜ける形の壊れ方をする。
 */
const CLASS_OPEN_RE = /<class\b((?:"[^"]*"|'[^']*'|[^>"'])*?)(\/?)>/g;

function classBlocks(xml) {
  const text = String(xml);
  const classes = [];
  let outside = '';
  let cursor = 0;
  const re = new RegExp(CLASS_OPEN_RE.source, 'g');
  let m;
  while ((m = re.exec(text)) !== null) {
    outside += text.slice(cursor, m.index);
    const attrs = m[1];
    const entry = { name: attrOf(attrs, 'name'), filename: attrOf(attrs, 'filename'), body: '' };
    if (m[2] === '/') {
      cursor = re.lastIndex;
      classes.push(entry);
      continue;
    }
    const start = re.lastIndex;
    const end = text.indexOf('</class>', start);
    if (end === -1) {
      entry.body = text.slice(start);
      classes.push(entry);
      cursor = text.length;
      break;
    }
    entry.body = text.slice(start, end);
    classes.push(entry);
    cursor = end + '</class>'.length;
    re.lastIndex = cursor;
  }
  outside += text.slice(cursor);
  return { classes, outside };
}

/** <methods>…</methods> を取り除く（残りが class 直下の <lines>）。 */
function stripMethods(body) {
  return String(body)
    .replace(/<methods\b[^>]*>[\s\S]*?<\/methods>/gi, '')
    .replace(/<methods\b[^>]*\/>/gi, '');
}

/** <methods>…</methods> の中身だけを返す（class 直下に <lines> が無いときのフォールバック用）。 */
function methodsOf(body) {
  const out = [];
  const re = /<methods\b[^>]*>([\s\S]*?)<\/methods>/gi;
  let m;
  while ((m = re.exec(String(body))) !== null) out.push(m[1]);
  return out.join('\n');
}

/** <line …> 1 件から { number, hits, branches, coveredBranches } を取り出す（hits が無ければ null）。 */
function parseLineElement(attrs) {
  const hits = /\bhits\s*=\s*"(\d+)"/.exec(attrs) || /\bhits\s*=\s*'(\d+)'/.exec(attrs);
  if (!hits) return null;
  const number = attrOf(attrs, 'number');
  // 分岐: condition-coverage="75% (3/4)" の分母・分子を採る。
  const cc = /\bcondition-coverage\s*=\s*["'][^"'(]*\((\d+)\/(\d+)\)["']/.exec(attrs);
  return {
    number: number === null ? null : Number(number),
    hits: Number(hits[1]),
    coveredBranches: cc ? Number(cc[1]) : 0,
    branches: cc ? Number(cc[2]) : 0,
  };
}

function zeroTotals() {
  return { lines: 0, covered: 0, branches: 0, coveredBranches: 0 };
}

/** テキスト中の <line> を数える（重複排除しない）。 */
function countLines(text) {
  const totals = zeroTotals();
  const re = /<line\b([^>]*?)\/?>/g;
  let m;
  while ((m = re.exec(String(text))) !== null) {
    const line = parseLineElement(m[1]);
    if (!line) continue;
    totals.lines++;
    if (line.hits > 0) totals.covered++;
    totals.branches += line.branches;
    totals.coveredBranches += line.coveredBranches;
  }
  return totals;
}

/**
 * テキスト中の <line> を**行番号で重複排除**して数える。
 * class 直下に <lines> が無く <methods> 配下にしか行が無いクラスのフォールバック専用。
 * 同じ行番号が複数のメソッドに現れた場合は hits の大きい方（＝実行された記録）を採る。
 */
function countLinesUnique(text) {
  const byNumber = new Map();
  const noNumber = [];
  const re = /<line\b([^>]*?)\/?>/g;
  let m;
  while ((m = re.exec(String(text))) !== null) {
    const line = parseLineElement(m[1]);
    if (!line) continue;
    if (line.number === null) {
      noNumber.push(line);
      continue;
    }
    const prev = byNumber.get(line.number);
    if (!prev || line.hits > prev.hits || line.branches > prev.branches) byNumber.set(line.number, line);
  }
  const totals = zeroTotals();
  for (const line of [...byNumber.values(), ...noNumber]) {
    totals.lines++;
    if (line.hits > 0) totals.covered++;
    totals.branches += line.branches;
    totals.coveredBranches += line.coveredBranches;
  }
  return totals;
}

/**
 * 1 クラスぶんの行統計。IADR-0123 決定 3。
 *   - class 直下の <lines> を正とする（<methods> 配下は同じ行の内訳であり数えない）
 *   - class 直下に行が無いクラスは <methods> 配下を行番号で重複排除して採る（source: 'methods-fallback'）
 */
function classLineStats(body) {
  const direct = countLines(stripMethods(body));
  if (direct.lines > 0) return { ...direct, source: 'class-lines' };
  const fallback = countLinesUnique(methodsOf(body));
  if (fallback.lines > 0) return { ...fallback, source: 'methods-fallback' };
  return { ...zeroTotals(), source: 'empty' };
}

/** パスの途中に src/<unit>/ を含むならその <unit> を返す。 */
const SRC_UNIT_RE = /(?:^|\/)src\/([^/]+)\//;

/** posix 結合（base の末尾 / と path の先頭 / を潰すだけ。解決はしない）。 */
function joinPosix(base, rel) {
  return `${toPosix(base).replace(/\/+$/, '')}/${toPosix(rel).replace(/^\/+/, '')}`;
}

/**
 * <class filename> をユニットへ帰属させる（IADR-0123 決定 2）。
 * filename が相対か絶対かは coverlet の base path 計算に依存し決め打ちできないため多段で解釈し、
 * **どの解釈で当たったか**（how）も返す。当たらなければ unit=null（未帰属＝集計に残す）。
 */
function unitOfFilename(filename, sources = []) {
  if (filename === null || filename === undefined || filename === '') {
    return { unit: null, how: 'unattributed', resolved: null };
  }
  const raw = toPosix(filename);
  const direct = SRC_UNIT_RE.exec(raw);
  if (direct) {
    const absolute = /^([a-zA-Z]:)?\//.test(raw);
    return { unit: direct[1], how: absolute ? 'absolute' : 'relative', resolved: raw };
  }
  for (const s of sources) {
    const joined = joinPosix(s, raw);
    const m = SRC_UNIT_RE.exec(joined);
    if (m) return { unit: m[1], how: 'source-joined', resolved: joined };
  }
  return { unit: null, how: 'unattributed', resolved: raw };
}

function addTotals(a, b) {
  a.lines += b.lines;
  a.covered += b.covered;
  a.branches += b.branches;
  a.coveredBranches += b.coveredBranches;
  return a;
}

const MAX_SAMPLES = 3;

/**
 * Cobertura XML 1 件を集計する。
 * 返すのは「集計対象の合算（除外ユニットの行を落とした値）」＋ 除外分 ＋ 診断である。
 * 既定の除外集合は EXCLUDED_UNITS（.gitmodules 由来・IADR-0120）。
 */
function parseCobertura(xml, { units = EXCLUDED_UNITS } = {}) {
  const text = String(xml);
  const sources = parseSources(text);
  const { classes, outside } = classBlocks(text);

  const totals = zeroTotals();
  const excluded = zeroTotals();
  const excludedClasses = [];
  const how = { relative: 0, absolute: 0, 'source-joined': 0, unattributed: 0 };
  const unitTotals = new Map();
  const filenameSamples = [];
  const unattributedSamples = [];
  let fallbackClasses = 0;
  let emptyClasses = 0;

  for (const c of classes) {
    const attribution = unitOfFilename(c.filename, sources);
    how[attribution.how] = (how[attribution.how] || 0) + 1;
    if (attribution.unit === null) {
      if (unattributedSamples.length < MAX_SAMPLES && c.filename) unattributedSamples.push(c.filename);
    } else if (filenameSamples.length < MAX_SAMPLES) {
      filenameSamples.push(`${c.filename} → ${attribution.unit}（${attribution.how}）`);
    }

    const stats = classLineStats(c.body);
    if (stats.source === 'methods-fallback') fallbackClasses++;
    if (stats.source === 'empty') emptyClasses++;

    const key = attribution.unit === null ? '(未帰属)' : attribution.unit;
    if (!unitTotals.has(key)) unitTotals.set(key, zeroTotals());
    addTotals(unitTotals.get(key), stats);

    if (attribution.unit !== null && units.has(attribution.unit)) {
      addTotals(excluded, stats);
      excludedClasses.push({
        name: c.name,
        filename: c.filename,
        unit: attribution.unit,
        how: attribution.how,
        lines: stats.lines,
        covered: stats.covered,
      });
      continue;
    }
    addTotals(totals, stats);
  }

  // どの <class> にも属さない <line>。帰属できない＝除外できないため集計には残し、診断で可視化する
  // （黙って落とすと実測値が理由不明に下がる）。正常な coverlet 出力では 0 件である。
  const orphan = countLines(outside);
  addTotals(totals, orphan);

  return {
    ...totals,
    excluded: { ...excluded, classes: excludedClasses },
    diagnostics: {
      sources,
      classCount: classes.length,
      attributed: classes.length - how.unattributed,
      how,
      unitTotals: Object.fromEntries([...unitTotals.entries()].map(([k, v]) => [k, v])),
      fallbackClasses,
      emptyClasses,
      orphan,
      reported: parseReportedTotals(text),
      filenameSamples,
      unattributedSamples,
    },
  };
}

/** 複数レポートの合算。 */
function mergeTotals(totalsList) {
  return totalsList.reduce(
    (a, b) => ({
      lines: a.lines + b.lines,
      covered: a.covered + b.covered,
      branches: a.branches + b.branches,
      coveredBranches: a.coveredBranches + b.coveredBranches,
    }),
    zeroTotals(),
  );
}

/**
 * parseCobertura の結果（レポート単位）を合算する。
 * 集計対象（totals）・除外分（excluded）・診断（diagnostics）をまとめて返す。
 */
function aggregateReports(parsedList) {
  const totals = mergeTotals(parsedList);
  const excluded = mergeTotals(parsedList.map((p) => p.excluded));
  const excludedClasses = [];
  const how = { relative: 0, absolute: 0, 'source-joined': 0, unattributed: 0 };
  const unitTotals = {};
  const orphan = zeroTotals();
  const reported = zeroTotals();
  const sources = new Set();
  const filenameSamples = [];
  const unattributedSamples = [];
  let classCount = 0;
  let attributed = 0;
  let fallbackClasses = 0;
  let emptyClasses = 0;
  let reportsWithReported = 0;

  for (const p of parsedList) {
    const d = p.diagnostics;
    excludedClasses.push(...p.excluded.classes);
    for (const k of Object.keys(how)) how[k] += d.how[k] || 0;
    for (const [unit, t] of Object.entries(d.unitTotals)) {
      if (!unitTotals[unit]) unitTotals[unit] = zeroTotals();
      addTotals(unitTotals[unit], t);
    }
    addTotals(orphan, d.orphan);
    if (d.reported) {
      reportsWithReported++;
      addTotals(reported, {
        lines: d.reported.lines || 0,
        covered: d.reported.covered || 0,
        branches: d.reported.branches || 0,
        coveredBranches: d.reported.coveredBranches || 0,
      });
    }
    for (const s of d.sources) sources.add(s);
    for (const s of d.filenameSamples) if (filenameSamples.length < MAX_SAMPLES) filenameSamples.push(s);
    for (const s of d.unattributedSamples) if (unattributedSamples.length < MAX_SAMPLES) unattributedSamples.push(s);
    classCount += d.classCount;
    attributed += d.attributed;
    fallbackClasses += d.fallbackClasses;
    emptyClasses += d.emptyClasses;
  }

  return {
    totals,
    excluded: { ...excluded, classes: excludedClasses },
    // 除外前（＝混入込み）の値。床を置き直す際の突き合わせに使う。
    beforeExclusion: mergeTotals([totals, excluded]),
    diagnostics: {
      sources: [...sources],
      classCount,
      attributed,
      how,
      unitTotals,
      orphan,
      reported: reportsWithReported ? reported : null,
      reportsWithReported,
      reportCount: parsedList.length,
      fallbackClasses,
      emptyClasses,
      filenameSamples,
      unattributedSamples,
    },
  };
}

/**
 * 診断から警告・通知を組み立てる（IADR-0123 決定 5）。終了コードは変えない。
 *
 * 最も危険なのは「フィルタが何にもマッチせず、除外したつもりで素通り」する状態である（#468）。
 * filename の形が想定と違えば帰属は 0 件になるため、そこを warn で名指しする。
 * 一方「帰属は成立していて除外が 0 行」は、合成点の参照が外れれば正常に起こる。恒常的な warn は
 * 「成果物は正しいのに黄」を常態化させ警告を読まない学習を生むため notice に留める（IADR-0118 決定 6）。
 */
function attributionMessages(agg) {
  const msgs = [];
  const d = agg.diagnostics;
  const units = [...EXCLUDED_UNITS].join(', ') || '（なし）';

  if (d.classCount > 0 && d.attributed === 0) {
    msgs.push({
      level: 'warn',
      text:
        `[check-coverage-floor] <class filename> を 1 件もユニットへ帰属できませんでした（クラス ${d.classCount} 件）。` +
        ' 除外ユニット由来の行を落とすフィルタが素通りしている状態です（#468 / IADR-0123 決定 2）。' +
        ` <sources>: ${JSON.stringify(d.sources)} / filename 例: ${JSON.stringify(d.unattributedSamples)}`,
    });
  } else if (d.how.unattributed > 0) {
    msgs.push({
      level: 'notice',
      text:
        `[check-coverage-floor] ユニットへ帰属できなかったクラスが ${d.how.unattributed} 件あります` +
        `（集計には残しています）。filename 例: ${JSON.stringify(d.unattributedSamples)}`,
    });
  }

  if (d.orphan.lines > 0) {
    msgs.push({
      level: 'warn',
      text:
        `[check-coverage-floor] どの <class> にも属さない <line> が ${d.orphan.lines} 行ありました。` +
        ' 帰属できないため除外の対象外です（集計には残しています）。レポートの構造が想定と異なります。',
    });
  }

  if (d.attributed > 0 && agg.excluded.lines === 0) {
    msgs.push({
      level: 'notice',
      text:
        `[check-coverage-floor] 集計対象外ユニット（${units}）由来の行は 0 行でした。` +
        ' 合成点（Platform.Bff → 可変ユニットの Bff エンドポイント）経由の混入が無いか、' +
        ' 除外ユニットのコードが実行されていない状態です（#468）。',
    });
  }

  if (d.fallbackClasses > 0) {
    msgs.push({
      level: 'notice',
      text:
        `[check-coverage-floor] class 直下に <lines> を持たないクラスが ${d.fallbackClasses} 件あり、` +
        ' <methods> 配下を行番号で重複排除して数えました（IADR-0123 決定 3 のフォールバック）。',
    });
  }

  return msgs;
}

/** 被覆率（%）を小数第 2 位までで返す。分母 0 のときは null（「測れていない」を 100% と誤解させない）。 */
function rate(covered, total) {
  if (!total) return null;
  return Math.round((covered / total) * 10000) / 100;
}

/** 床との比較。floor 未満なら違反を返す。rate が null（未計測）の項目は判定しない。 */
function compareToFloor(totals, floor) {
  const violations = [];
  const line = rate(totals.covered, totals.lines);
  const branch = rate(totals.coveredBranches, totals.branches);
  if (line !== null && floor.line != null && line < floor.line) {
    violations.push({ metric: 'line', actual: line, floor: floor.line });
  }
  if (branch !== null && floor.branch != null && branch < floor.branch) {
    violations.push({ metric: 'branch', actual: branch, floor: floor.branch });
  }
  return { line, branch, violations };
}

// --- 診断の整形 -----------------------------------------------------------------

const fmtRate = (v) => (v === null ? '未計測' : `${v}%`);

function formatTotals(t) {
  return `line ${fmtRate(rate(t.covered, t.lines))}（${t.covered}/${t.lines}） / ` +
    `branch ${fmtRate(rate(t.coveredBranches, t.branches))}（${t.coveredBranches}/${t.branches}）`;
}

const MAX_LISTED_CLASSES = 20;

/**
 * 既定で出す診断（数行）。CI ログから「混入行数」「除外前後の実測値」「filename の解釈」を
 * そのまま読み取れることを狙う（ci.yml にフラグを足さずに済ませるため。IADR-0123 決定 6）。
 */
function formatDiagnostics(agg) {
  const d = agg.diagnostics;
  const out = [];
  const units = [...EXCLUDED_UNITS].join(', ') || '（なし）';

  out.push(
    `除外（filename 帰属・#468）: 集計対象外ユニット（${units}）由来 ${agg.excluded.classes.length} クラス / ` +
      `${agg.excluded.lines} 行（被覆 ${agg.excluded.covered}） / 分岐 ${agg.excluded.branches}（被覆 ${agg.excluded.coveredBranches}）を落としました。` +
      ` 除外前: ${formatTotals(agg.beforeExclusion)}`,
  );

  out.push(
    `帰属: クラス ${d.classCount} 件（そのまま(相対) ${d.how.relative} / そのまま(絶対) ${d.how.absolute} / ` +
      `<sources> 結合 ${d.how['source-joined']} / 未帰属 ${d.how.unattributed}）。` +
      ` <sources>: ${JSON.stringify(d.sources)}。filename 例: ${JSON.stringify(d.filenameSamples)}` +
      (d.unattributedSamples.length ? `。未帰属の例: ${JSON.stringify(d.unattributedSamples)}` : ''),
  );

  const unitLine = Object.entries(d.unitTotals)
    .sort((a, b) => b[1].lines - a[1].lines)
    .map(([unit, t]) => `${EXCLUDED_UNITS.has(unit) ? '[除外] ' : ''}${unit} ${t.lines} 行（被覆 ${t.covered}）`)
    .join(' / ');
  out.push(`ユニット別の行数: ${unitLine || '（0 件）'}` +
    (d.orphan.lines ? ` / [class 外] ${d.orphan.lines} 行` : ''));

  if (d.reported) {
    const mine = agg.beforeExclusion;
    const agree = (a, b) => (a === b ? '一致' : `**乖離 ${b - a}**`);
    out.push(
      `coverlet 自身の集計値との照合（IADR-0123 決定 4。除外前で比較・${d.reportsWithReported}/${d.reportCount} レポート）: ` +
        `lines-valid ${d.reported.lines}（本実装 ${mine.lines}・${agree(d.reported.lines, mine.lines)}） / ` +
        `lines-covered ${d.reported.covered}（本実装 ${mine.covered}・${agree(d.reported.covered, mine.covered)}） / ` +
        `branches-valid ${d.reported.branches}（本実装 ${mine.branches}・${agree(d.reported.branches, mine.branches)}）`,
    );
  }

  if (agg.excluded.classes.length) {
    const listed = agg.excluded.classes.slice(0, MAX_LISTED_CLASSES);
    out.push(
      `除外したクラス（${listed.length}/${agg.excluded.classes.length} 件）:\n` +
        listed
          .map((c) => `    ${c.name || '(名前なし)'} [${c.unit} / ${c.how}] ${c.lines} 行（被覆 ${c.covered}）— ${c.filename}`)
          .join('\n'),
    );
  }

  return out;
}

/** COVERAGE_FLOOR_DEBUG=1 のときだけ出すレポート単位の詳細。混入源のテストプロジェクトを特定できる。 */
function formatReportDiagnostics(report, parsed) {
  const d = parsed.diagnostics;
  return (
    `  ${report}\n` +
    `    クラス ${d.classCount} 件（相対 ${d.how.relative} / 絶対 ${d.how.absolute} / 結合 ${d.how['source-joined']} / 未帰属 ${d.how.unattributed}）` +
    ` / 集計 ${parsed.lines} 行 / 除外 ${parsed.excluded.lines} 行（${parsed.excluded.classes.length} クラス）` +
    (d.fallbackClasses ? ` / フォールバック ${d.fallbackClasses} クラス` : '') +
    (d.orphan.lines ? ` / class 外 ${d.orphan.lines} 行` : '') +
    `\n    <sources>: ${JSON.stringify(d.sources)} / filename 例: ${JSON.stringify(d.filenameSamples.concat(d.unattributedSamples))}`
  );
}

// --- ファイル走査 ---------------------------------------------------------------

function walk(dir, predicate, acc = []) {
  const abs = path.join(REPO_ROOT, dir);
  let entries;
  try {
    entries = fs.readdirSync(abs, { withFileTypes: true });
  } catch {
    return acc;
  }
  for (const e of entries) {
    if (SKIP_DIRS.has(e.name)) continue;
    const rel = toPosix(path.join(dir, e.name));
    if (e.isDirectory()) walk(rel, predicate, acc);
    else if (predicate(rel)) acc.push(rel);
  }
  return acc;
}

/**
 * Cobertura レポートを探す。除外前後の内訳も返す——0 件のときに「探索そのものが空振りしたのか、
 * 除外で全部落ちたのか」を切り分けられないと、fail-open の warn が原因不明のまま素通りする。
 */
function findReportsDetailed() {
  const all = walk(SEARCH_ROOT, (p) => /coverage\.cobertura\.xml$/i.test(p));
  const included = all.filter((p) => !isExcludedPath(p));
  return { all, included, excluded: all.filter((p) => isExcludedPath(p)) };
}

function findReports() {
  return findReportsDetailed().included;
}

function readFloor() {
  try {
    return JSON.parse(fs.readFileSync(FLOOR_FILE, 'utf8')).backend || {};
  } catch {
    return {};
  }
}

// --- 自己試験 -------------------------------------------------------------------

const FIXTURE = `<?xml version="1.0"?>
<coverage>
  <packages><package><classes><class><lines>
    <line number="1" hits="1" />
    <line number="2" hits="0" />
    <line number="3" hits="5" branch="true" condition-coverage="50% (1/2)" />
    <line number="4" hits="2" branch="true" condition-coverage="100% (2/2)" />
  </lines></class></classes></package></packages>
</coverage>`;

/** 二重記載（<methods> 配下 と class 直下）と、除外ユニットへの帰属を含む実物に近いフィクスチャ。 */
const excludedUnitName = [...EXCLUDED_UNITS][0] || 'ai-stock-trading';
const FIXTURE_ATTRIBUTED = `<?xml version="1.0"?>
<coverage lines-valid="4" lines-covered="3" branches-valid="0" branches-covered="0">
  <sources><source>/home/runner/work/msp/msp/</source></sources>
  <packages><package name="Platform.Bff"><classes>
    <class name="Platform.Bff.HealthEndpoints" filename="src/platform/backend/Bff/Platform.Bff/HealthEndpoints.cs">
      <methods><method name="Map"><lines>
        <line number="10" hits="1" />
        <line number="11" hits="0" />
      </lines></method></methods>
      <lines>
        <line number="10" hits="1" />
        <line number="11" hits="0" />
      </lines>
    </class>
    <class name="AiStockTrading.Bff.Endpoints.MonitorBffEndpoints" filename="src/${excludedUnitName}/backend/Bff/AiStockTrading.Bff.Endpoints/MonitorBffEndpoints.cs">
      <methods><method name="Map"><lines>
        <line number="20" hits="3" />
        <line number="21" hits="7" />
      </lines></method></methods>
      <lines>
        <line number="20" hits="3" />
        <line number="21" hits="7" />
      </lines>
    </class>
  </classes></package></packages>
</coverage>`;

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  const totals = parseCobertura(FIXTURE);
  t('parseCobertura: 行数と被覆行を数える', totals.lines === 4 && totals.covered === 3, totals);
  t('parseCobertura: 分岐を condition-coverage から数える', totals.branches === 4 && totals.coveredBranches === 3, totals);
  t('parseCobertura: 属性順が違っても拾う',
    parseCobertura('<line hits="1" number="9" />').lines === 1);
  t('parseCobertura: hits の無い line は数えない',
    parseCobertura('<line number="1" />').lines === 0);
  t('parseCobertura: 空入力でも壊れない', parseCobertura('').lines === 0);

  t('rate: 3/4 は 75', rate(3, 4) === 75);
  t('rate: 分母 0 は null（未計測を 100% と誤らせない）', rate(0, 0) === null);

  t('mergeTotals: 合算する',
    mergeTotals([totals, totals]).lines === 8 && mergeTotals([totals, totals]).covered === 6);

  {
    const r = compareToFloor(totals, { line: 80, branch: 70 });
    t('compareToFloor: 行が床未満なら違反（75 < 80）',
      r.violations.length === 1 && r.violations[0].metric === 'line', r);
  }
  {
    const r = compareToFloor(totals, { line: 75, branch: 75 });
    t('compareToFloor: 床ちょうどは違反にしない（境界）',
      r.violations.length === 0, r);
  }
  {
    const r = compareToFloor(totals, { line: 90, branch: 90 });
    t('compareToFloor: 行・分岐とも未満なら 2 件', r.violations.length === 2, r);
  }
  {
    const r = compareToFloor({ lines: 0, covered: 0, branches: 0, coveredBranches: 0 }, { line: 80, branch: 70 });
    t('compareToFloor: 未計測（分母 0）は判定しない', r.violations.length === 0 && r.line === null, r);
  }

  // 集計対象ユニットの切り分け（別プロジェクトの submodule は合算しない。PR #464 レビュー指摘）。
  t('isExcludedPath: ai-stock-trading 配下は集計対象外',
    isExcludedPath('src/ai-stock-trading/backend/Services/X/tests/X.Tests/TestResults/g/coverage.cobertura.xml'));
  t('isExcludedPath: platform / knowledge は集計対象',
    !isExcludedPath('src/platform/backend/Bff/Platform.Bff.Tests/TestResults/g/coverage.cobertura.xml')
      && !isExcludedPath('src/knowledge/backend/Tests/X/TestResults/g/coverage.cobertura.xml'));

  // --- #468 / IADR-0123: filename 帰属による除外と二重記載の扱い ---

  t('unitOfFilename: 相対 filename（src/<unit>/…）',
    unitOfFilename('src/platform/backend/X.cs').unit === 'platform'
      && unitOfFilename('src/platform/backend/X.cs').how === 'relative');
  t('unitOfFilename: 絶対 filename（base path で始まらないファイルは絶対のまま書かれる）',
    unitOfFilename('/home/runner/work/msp/msp/src/ai-stock-trading/backend/X.cs').unit === 'ai-stock-trading'
      && unitOfFilename('/home/runner/work/msp/msp/src/ai-stock-trading/backend/X.cs').how === 'absolute');
  t('unitOfFilename: <sources> と結合して帰属（base path が src/ より深い場合）',
    unitOfFilename('ai-stock-trading/backend/X.cs', ['/home/runner/work/msp/msp/src/']).unit === 'ai-stock-trading'
      && unitOfFilename('ai-stock-trading/backend/X.cs', ['/home/runner/work/msp/msp/src/']).how === 'source-joined');
  t('unitOfFilename: deterministic build の /_/src/… も帰属する',
    unitOfFilename('/_/src/knowledge/backend/X.cs').unit === 'knowledge');
  t('unitOfFilename: Windows の区切りでも帰属する',
    unitOfFilename('C:\\work\\msp\\src\\platform\\backend\\X.cs').unit === 'platform');
  t('unitOfFilename: 帰属できなければ unit=null（黙って落とさない）',
    unitOfFilename('Foo/Bar.cs').unit === null && unitOfFilename('Foo/Bar.cs').how === 'unattributed');
  t('unitOfFilename: filename が無いクラスは未帰属',
    unitOfFilename(null).unit === null);

  {
    const p = parseCobertura(FIXTURE_ATTRIBUTED);
    t('parseCobertura: 二重記載は class 直下の <lines> のみ数える（<methods> 配下は内訳）',
      p.lines === 2 && p.excluded.lines === 2, { totals: p.lines, excluded: p.excluded.lines });
    t('parseCobertura: 除外ユニットへ帰属した行を集計から落とす',
      p.covered === 1 && p.excluded.covered === 2, p);
    t('parseCobertura: 除外したクラスを名前付きで報告する',
      p.excluded.classes.length === 1 && /MonitorBffEndpoints/.test(p.excluded.classes[0].name), p.excluded.classes);
    t('parseCobertura: coverlet 自身の集計値（lines-valid）を読む',
      p.diagnostics.reported && p.diagnostics.reported.lines === 4, p.diagnostics.reported);
    const agg = aggregateReports([p]);
    t('aggregateReports: 除外前の値は coverlet の lines-valid と一致する（IADR-0123 決定 4 の照合）',
      agg.beforeExclusion.lines === 4 && agg.beforeExclusion.covered === 3, agg.beforeExclusion);
    t('attributionMessages: 帰属も除外も成立していれば warn を出さない',
      attributionMessages(agg).every((m) => m.level !== 'warn'), attributionMessages(agg));
  }

  {
    // フィルタが何にもマッチしない状態（filename の形が想定外）は warn で気付けること（#468 受け入れ基準）。
    const noAttribution = '<coverage><packages><package><classes>' +
      '<class name="X" filename="Foo/Bar.cs"><lines><line number="1" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const agg = aggregateReports([parseCobertura(noAttribution)]);
    const msgs = attributionMessages(agg);
    t('attributionMessages: 帰属 0 件は warn（除外したつもりで素通りを検出）',
      msgs.some((m) => m.level === 'warn' && /帰属できませんでした/.test(m.text)), msgs);
  }
  {
    // class の外にある <line>（構造が想定外）も warn で可視化する。
    const agg = aggregateReports([parseCobertura('<coverage><line number="1" hits="1" /></coverage>')]);
    t('attributionMessages: class 外の <line> は warn',
      attributionMessages(agg).some((m) => m.level === 'warn' && /<class> にも属さない/.test(m.text)));
  }
  {
    // 除外 0 行は notice（合成点の参照が外れれば正常に起こるため warn にしない）。
    const onlyIncluded = '<coverage><packages><package><classes>' +
      '<class name="X" filename="src/platform/backend/X.cs"><lines><line number="1" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const msgs = attributionMessages(aggregateReports([parseCobertura(onlyIncluded)]));
    t('attributionMessages: 除外 0 行は notice（warn にしない）',
      msgs.some((m) => m.level === 'notice' && /0 行でした/.test(m.text))
        && msgs.every((m) => m.level !== 'warn'), msgs);
  }
  {
    // class 直下に <lines> が無いクラスは <methods> 配下を重複排除して数える（フォールバック）。
    const methodsOnly = '<coverage><packages><package><classes>' +
      '<class name="X" filename="src/platform/backend/X.cs"><methods>' +
      '<method name="a"><lines><line number="1" hits="1" /><line number="2" hits="0" /></lines></method>' +
      '<method name="b"><lines><line number="2" hits="4" /></lines></method>' +
      '</methods></class></classes></package></packages></coverage>';
    const p = parseCobertura(methodsOnly);
    t('parseCobertura: class 直下に <lines> が無ければ <methods> を行番号で重複排除して採る',
      p.lines === 2 && p.covered === 2 && p.diagnostics.fallbackClasses === 1, p);
  }

  t('parseSources: <source> を読み、空要素は落とす',
    parseSources('<sources><source>/a/b/</source><source></source></sources>').join(',') === '/a/b/');
  t('classBlocks: 自己終了形の <class /> も 1 クラスとして数える',
    classBlocks('<classes><class name="X" filename="src/platform/a.cs" /></classes>').classes.length === 1);
  t('classBlocks: <classes> を <class> と誤認しない',
    classBlocks('<classes></classes>').classes.length === 0);
  {
    // 属性値の中の > でタグを切らない（非同期ステートマシン Foo/<Map>d__2 の名前。切ると filename を
    // 読めず、そのクラスだけ静かに未帰属＝除外が抜ける）。
    const xml = `<classes><class name="X/<Map>d__2" filename="src/${excludedUnitName}/backend/X.cs">` +
      '<lines><line number="1" hits="1" /></lines></class></classes>';
    const p = parseCobertura(xml);
    t('classBlocks: 属性値に含まれる > でタグを切らない（未帰属で除外が抜けない）',
      p.excluded.lines === 1 && p.diagnostics.how.unattributed === 0, p.diagnostics);
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-coverage-floor] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-coverage-floor] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const reportOnly = process.argv.includes('--report-only');
  const debug = process.env.COVERAGE_FLOOR_DEBUG === '1';

  const { all, included: reports, excluded } = findReportsDetailed();
  if (reports.length === 0) {
    // fail-open: レポートが無い＝テストを走らせていない文脈（ローカル実行等）。
    // ここで fail にすると「カバレッジと無関係な PR が赤くなる」ため警告に留める。
    //
    // **ただし黙って素通りさせない。** 「探索が空振りした」のか「除外で全部落ちた」のかを
    // 切り分けられる情報を必ず出す（原因不明の warn は、この検査が無いのと同じである）。
    const sample = all.slice(0, 5).map((p) => `    ${p}`).join('\n');
    warn(`[check-coverage-floor] 集計対象の Cobertura レポートが 0 件でした（探索起点 ${SEARCH_ROOT}/）。`
      + ` 検出 ${all.length} 件 / 除外 ${excluded.length} 件（除外ユニット: ${[...EXCLUDED_UNITS].join(', ')}）。`
      + (all.length === 0
        ? ' 1 件も見つかっていないため、dotnet test --collect:"XPlat Code Coverage" が未実行か、出力先が探索起点の外である可能性が高い。'
        : ' 検出はしているため、すべて除外ユニット配下だったことになる。'));
    if (all.length) console.error(`検出したレポート（先頭 5 件）:\n${sample}`);
    process.exit(0);
  }

  const parsed = reports.map((r) => parseCobertura(fs.readFileSync(path.join(REPO_ROOT, r), 'utf8')));
  const agg = aggregateReports(parsed);
  const totals = agg.totals;
  const floor = readFloor();
  const { line, branch, violations } = compareToFloor(totals, floor);

  console.log(`[check-coverage-floor] レポート ${reports.length} 件を集計: line ${fmtRate(line)}（${totals.covered}/${totals.lines}） / ` +
    `branch ${fmtRate(branch)}（${totals.coveredBranches}/${totals.branches}）。床: line ${floor.line ?? '未設定'} / branch ${floor.branch ?? '未設定'}`);

  // NFR（#468 / IADR-0123 決定 6）: 診断は既定で出す。ci.yml にフラグを足さずに、CI ログから
  // 「混入行数」「除外前後の実測値」「filename の解釈」を読み取れるようにするためである。
  for (const d of formatDiagnostics(agg)) console.log(`[check-coverage-floor] ${d}`);
  if (debug) {
    console.log('[check-coverage-floor] レポート単位の内訳（COVERAGE_FLOOR_DEBUG=1）:');
    reports.forEach((r, i) => console.log(formatReportDiagnostics(r, parsed[i])));
  } else {
    console.log('[check-coverage-floor] レポート単位の内訳は COVERAGE_FLOOR_DEBUG=1 で出力します。');
  }
  for (const m of attributionMessages(agg)) {
    if (m.level === 'warn') warn(m.text);
    else notice(m.text);
  }

  const summary = process.env.GITHUB_STEP_SUMMARY;
  if (summary) {
    const before = agg.beforeExclusion;
    const lines = [
      '### バックエンドのカバレッジ（#453）',
      '',
      '| 指標 | 実測 | 床 |',
      '| --- | --- | --- |',
      `| line | ${fmtRate(line)} | ${floor.line ?? '未設定'} |`,
      `| branch | ${fmtRate(branch)} | ${floor.branch ?? '未設定'} |`,
      '',
      '床は `src/coverage-floor.json`。テストを増やしたら床を引き上げること（ratchet）。',
      '',
      `集計対象外ユニット（${[...EXCLUDED_UNITS].join(', ') || 'なし'}）由来の行は `
        + `**${agg.excluded.lines} 行**（${agg.excluded.classes.length} クラス・被覆 ${agg.excluded.covered}）を `
        + `\`<class filename>\` の帰属で除外した（#468 / IADR-0123）。除外前は ${formatTotals(before)}。`,
    ];
    try { fs.appendFileSync(summary, lines.join('\n') + '\n'); } catch { /* サマリ不可でも検査は続ける */ }
  }

  if (floor.line == null && floor.branch == null) {
    notice('[check-coverage-floor] 床が未設定です（src/coverage-floor.json）。実測値をもとに設定してください。');
    process.exit(0);
  }
  if (violations.length === 0) {
    console.log('[check-coverage-floor] OK: 床を下回っていません。');
    process.exit(0);
  }
  const detail = violations.map((v) => `${v.metric}: 実測 ${v.actual}% < 床 ${v.floor}%`).join(' / ');
  if (reportOnly) {
    warn(`[check-coverage-floor] 床を下回っています（--report-only のため exit 0）: ${detail}`);
    process.exit(0);
  }
  console.error(`[check-coverage-floor] カバレッジが床を下回っています: ${detail}`);
  console.error('テストを追加して床を満たすか、床を下げる正当な理由を作業仕様書に記してください（床の引き下げは退行です）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  EXCLUDED_UNITS,
  isExcludedPath,
  findReportsDetailed,
  findReports,
  readFloor,
  toPosix,
  attrOf,
  parseSources,
  parseReportedTotals,
  classBlocks,
  stripMethods,
  methodsOf,
  countLines,
  countLinesUnique,
  classLineStats,
  unitOfFilename,
  parseCobertura,
  mergeTotals,
  aggregateReports,
  attributionMessages,
  formatDiagnostics,
  rate,
  compareToFloor,
};
