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
 *   **生成コードは集計から落とす**（#571 / IADR-0138、#574 / IADR-0195）。人が書くコードではなく、
 *   テストで被覆する対象でもない。含めると「設計上まったく無関係な操作」で床判定が動く。
 *   対象は 2 種類ある。
 *     - **EF Core**（Migrations/ 配下・*ModelSnapshot.cs。IADR-0138 決定 1）
 *       —— マイグレーションを 1 本足しただけで床が動く（実際に PR #568 がそれで止まった）。
 *     - **source generator の出力**（obj/ 配下。IADR-0195 決定 1）
 *       —— エンドポイントへ XML doc コメントを 1 つ足すだけで
 *          OpenApiXmlCommentSupport.generated.cs が再生成され、床が動く。
 *   判定は <class filename>（帰属で解決した経路）に対して行い、除外量は**種別ごとに**毎回診断へ出す。
 *
 *   **二重記載の扱い**（IADR-0123 決定 3）: coverlet の Cobertura は同じ行を <methods> 配下と
 *   class 直下の <lines> の両方に書く。集計は **行・分岐とも class 直下の <lines> を正**とし、
 *   <methods> 配下は内訳として数えない。両方数えるとメソッドを持つ行だけが 2 票を持ち、メソッド外の
 *   行との重みが崩れる（旧方式が混入量を一律 2 倍に見せていた原因でもある。PR #464 のレビューが
 *   記録した 266 行 / 230 行は、いずれも二重記載で 2 倍になった値であり、266 と 230 の差は
 *   スコープ差〔全プロジェクト実行 / Platform.Bff.Tests 単体実行〕である）。前提が実レポートで
 *   正しいかは、<coverage> の lines-valid / lines-covered（coverlet 自身の集計値）との照合として
 *   毎回診断へ出す（IADR-0123 決定 4）。分岐は定義が異なり照合が反証力を持たないため、
 *   「全 <line> と class 直下の比」を別の観測点として出す（同 決定 5）。
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
 * 始まらないファイルは filename に**絶対パスのまま**書く（GetBasePaths / GetRelativePathFromBase）
 * ——**実装時点の理解であり coverlet のソースで確認していない**（IADR-0123「filename の解釈」）。
 * この理解は前提として採らず「決め打ちしない理由」としてのみ使う。実レポートでの真偽は
 * 診断出力（帰属の内訳・lines-valid との照合）に現れる。
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

/**
 * テキスト中の <line> を数える（重複排除しない）。
 *
 * collect: true のときは行そのものも entries として返す（レポート跨ぎの重複排除に使う。#900 /
 * IADR-0236）。既定で集めないのは、診断用の raw（<methods> 配下の重複込みで実測 5 万行規模）まで
 * 行の配列を持つのが無駄だからである。
 */
function countLines(text, { collect = false } = {}) {
  const totals = zeroTotals();
  const entries = collect ? [] : null;
  const re = /<line\b([^>]*?)\/?>/g;
  let m;
  while ((m = re.exec(String(text))) !== null) {
    const line = parseLineElement(m[1]);
    if (!line) continue;
    totals.lines++;
    if (line.hits > 0) totals.covered++;
    totals.branches += line.branches;
    totals.coveredBranches += line.coveredBranches;
    if (entries) entries.push(line);
  }
  if (entries) totals.entries = entries;
  return totals;
}

/**
 * テキスト中の <line> を**行番号で重複排除**して数える。
 * class 直下に <lines> が無く <methods> 配下にしか行が無いクラスのフォールバック専用。
 * 同じ行番号が複数のメソッドに現れた場合は hits の大きい方（＝実行された記録）を採る。
 */
function countLinesUnique(text, { collect = false } = {}) {
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
  const kept = [...byNumber.values(), ...noNumber];
  for (const line of kept) {
    totals.lines++;
    if (line.hits > 0) totals.covered++;
    totals.branches += line.branches;
    totals.coveredBranches += line.coveredBranches;
  }
  if (collect) totals.entries = kept;
  return totals;
}

/**
 * 1 クラスぶんの行統計。IADR-0123 決定 3。
 *   - class 直下の <lines> を正とする（<methods> 配下は同じ行の内訳であり数えない）
 *   - class 直下に行が無いクラスは <methods> 配下を行番号で重複排除して採る（source: 'methods-fallback'）
 */
function classLineStats(body) {
  const direct = countLines(stripMethods(body), { collect: true });
  if (direct.lines > 0) return { ...direct, source: 'class-lines' };
  const fallback = countLinesUnique(methodsOf(body), { collect: true });
  if (fallback.lines > 0) return { ...fallback, source: 'methods-fallback' };
  return { ...zeroTotals(), entries: [], source: 'empty' };
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

/**
 * レポート間の重複排除に使うファイルキー（#900 / IADR-0236 決定 2）。
 *
 * 🔴 **生の `filename` をキーにしてはならない。** `unitOfFilename` は同じファイルをレポートごとに
 * 違う文字列で返す（`relative` / `absolute` / `source-joined`）。IADR-0123 決定 4 の CI 実測は
 * 「そのまま(相対) 645 / <sources> 結合 1391」で、**同一 CI 実行の中で両形が混在している**。
 * 生パスをキーにすると重複排除が一部にしか効かず、しかも診断には何も出ない（無音の部分適用）。
 *
 * そこで帰属で解決した経路（attribution.resolved）を **SRC_UNIT_RE の最初のマッチ位置＝
 * `src/<unit>/` の先頭以降**へ切り詰める。相対でも <sources> 結合でも同じ文字列へ落ちる。
 *
 * **大文字小文字は潰さない。** CI は Linux で大小が意味を持つ。潰すと「別ファイルを畳む＝分母過小
 * ＝床が甘くなる」方向に壊れる。畳み残し（分母過大）は保守的な壊れ方であり、その逆は退行を隠す。
 *
 * **既知の限界**: `Services/<X>/src/<X>.Api/…` のように**内側にも `src/` を持つ**経路では、
 * 最初のマッチが内側 `src/` に食い付く（`unitOfFilename` の帰属も同じ位置で当たっており、本関数は
 * その規則を変えない）。内側 `src/` に当たる場合、切り出される接尾辞は <sources> の深さに依らず
 * 同一なのでレポート間でキーは揃う。揃わないのは**絶対パス形**が混在したときだけで、
 * その場合は先頭側の `src/<unit>/` に当たってキーが割れる —— 方向は**畳み残し（分母過大＝保守的）**
 * であり、下の重複排除の診断（落とした行数・レポート数の内訳）に変動として現れる。
 * IADR-0123 決定 4 の CI 実測では絶対形は 0 件だった。
 */
function dedupFileKey(attribution) {
  const resolved = attribution && attribution.resolved;
  if (resolved === null || resolved === undefined || resolved === '') {
    return { key: '(filename なし)', normalized: false };
  }
  const p = toPosix(resolved);
  const m = SRC_UNIT_RE.exec(p);
  if (!m) return { key: p, normalized: false };
  return { key: p.slice(m.index + (p.charAt(m.index) === '/' ? 1 : 0)), normalized: true };
}

/**
 * 生成コード（EF Core のマイグレーション）と判定するパスの規則（#571 / IADR-0138 決定 1）。
 *
 * 実測で決めた（形を仮定して書くと、何にもマッチせず「除外したつもりで素通り」になる。
 * IADR-0123 が同じ失敗を名指ししている）。develop 実測の <class filename> は 2 形あった。
 *   - `knowledge/backend/Services/WikiService/src/WikiService.Api/Migrations/20260626150858_InitialCreate.cs`
 *     （<sources> が `…/src/` のレポート）
 *   - `Services/AuthorizationService/src/AuthorizationService.Api/Migrations/AuthorizationDbContextModelSnapshot.cs`
 *     （<sources> が `…/src/platform/backend/` のレポート）
 * いずれも `Migrations/` をパスの**区切り付きの一区画**として含む。`*.Designer.cs` も同ディレクトリに
 * 出るため、この 1 規則で 3 種（migration 本体 / Designer / ModelSnapshot）すべてに当たる。
 * ModelSnapshot を別規則で持つのは、出力ディレクトリを変えた場合の取りこぼしを避けるためである
 * （develop 実測では Migrations/ の外に ModelSnapshot は 0 件だった）。
 *
 * 区切り付きで見るのは誤爆を避けるためである（`MigrationsHelper.cs` や `MyMigrations/` は生成物ではない）。
 */
const GENERATED_DIR_RE = /(?:^|\/)Migrations\//;
const GENERATED_FILE_RE = /(?:^|\/)[^/]*ModelSnapshot\.cs$/i;

/**
 * source generator の出力と判定するパスの規則（#574 / IADR-0195 決定 1）。
 *
 * これも実測で決めた。develop `1d7edce` のレポート（14 件・Release）の <class filename> を
 * 重複除去して全数（1061 件）分類した結果は次のとおりで、**obj/ 配下は全て source generator の
 * 出力であり、中間生成物の巻き込みは 0 件**だった。
 *   obj/ を区切り付きで含む            : 14 件（OpenApiXmlCommentSupport.generated.cs 11 / RegexGenerator.g.cs 3）
 *   obj/ の外の *.g.cs / *.generated.cs :  0 件
 * `.dll` や `.cache` がレポートに現れないのは、コンパイラが <class filename> へ書くのが
 * **ソースとして食わせたファイル**だけだからである。
 *
 * **サフィックス（*.g.cs / *.generated.cs）ではなくディレクトリで見る。** 上の実測では両者は
 * 完全に一致するが、根拠の性質が違う——obj/ は MSBuild の中間出力ディレクトリで gitignore 済みであり
 * 「人が書いたコードは定義上そこに無い」という**構造的**な根拠を持つ。サフィックスは各 generator が
 * 選ぶ**規約**にすぎず、別の名前で出す generator が入れば黙って素通りする。壊れたときに無音になる
 * 規則を選ばない（IADR-0123 / IADR-0138 が繰り返し名指しした失敗モード）。
 */
const GENERATED_INTERMEDIATE_RE = /(?:^|\/)obj\//;

/**
 * filename（帰属で解決した経路）が生成コードなら種別を、そうでなければ null を返す。
 *
 * 種別を分けて持つのは診断のためだけではない。**IADR-0138 決定 3 の notice は「除外量 0 = フィルタの
 * 素通り」を可視化する仕組みであり、2 種を 1 つのカウンタに合算すると、片方が壊れてももう片方が
 * 数を埋めて notice が出なくなる**（＝守っていたはずの穴が黙って開く）。種別ごとに数え、種別ごとに
 * notice を出す（IADR-0195 決定 2）。
 */
function generatedKindOf(filename) {
  if (filename === null || filename === undefined || filename === '') return null;
  const p = toPosix(filename);
  if (GENERATED_INTERMEDIATE_RE.test(p)) return 'sourcegen';
  if (GENERATED_DIR_RE.test(p) || GENERATED_FILE_RE.test(p)) return 'ef';
  return null;
}

/** 生成コードの種別の表示名（診断文の単一情報源）。 */
const GENERATED_KIND_LABEL = {
  ef: 'EF（Migrations/ 配下・*ModelSnapshot.cs）',
  sourcegen: 'source generator（obj/ 配下）',
};

/** filename（帰属で解決した経路）が生成コードか。IADR-0138 決定 1 / IADR-0195 決定 1。 */
function isGeneratedFilename(filename) {
  return generatedKindOf(filename) !== null;
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
  const generated = zeroTotals();
  const generatedByUnit = new Map();
  const generatedSamples = [];
  let generatedClasses = 0;
  const generatedByKind = new Map();
  const how = { relative: 0, absolute: 0, 'source-joined': 0, unattributed: 0 };
  const unitTotals = new Map();
  const filenameSamples = [];
  const unattributedSamples = [];
  let fallbackClasses = 0;
  let emptyClasses = 0;
  // #900 / IADR-0236: 集計対象として残った行の同一性。aggregateReports がレポート間で畳む。
  const includedEntries = [];
  const includedUnkeyed = zeroTotals();
  let unnormalizedLines = 0;
  const occurrence = new Map();

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

    // 生成コード（EF の Migrations / ModelSnapshot と source generator の obj/ 配下）は集計から
    // 落とす（#571 / IADR-0138 決定 1、#574 / IADR-0195 決定 1）。
    // 集計対象外ユニットの除外を先に通すのは、AST 由来の行を「生成コード」として二重計上させない
    // ため（既存の診断値〔IADR-0123 の 133 行〕の意味を変えない）。
    const generatedKind = generatedKindOf(attribution.resolved);
    if (generatedKind !== null) {
      generatedClasses++;
      addTotals(generated, stats);
      if (!generatedByUnit.has(key)) generatedByUnit.set(key, zeroTotals());
      addTotals(generatedByUnit.get(key), stats);
      if (!generatedByKind.has(generatedKind)) {
        generatedByKind.set(generatedKind, { ...zeroTotals(), classCount: 0 });
      }
      const kindTotals = generatedByKind.get(generatedKind);
      addTotals(kindTotals, stats);
      kindTotals.classCount++;
      if (generatedSamples.length < MAX_SAMPLES) generatedSamples.push(c.filename);
      continue;
    }
    addTotals(totals, stats);

    // #900 / IADR-0236: 集計に残った行へ (class name, 正規化 filename, 行番号) のキーを与える。
    //
    // 🔴 **同一レポート内の同キーは畳まない。** 出現順の連番をキーへ足して衝突させないことで、
    // **レポート 1 件のときは重複排除が恒等になる**（IADR-0123 決定 3・決定 4 の非退行が
    // 構成上保証される）。畳むのはレポートを跨いだぶんだけである。
    const fileKey = dedupFileKey(attribution);
    if (!fileKey.normalized) unnormalizedLines += stats.lines;
    for (const e of stats.entries) {
      if (e.number === null) {
        // 行番号を持たない <line> は識別できない＝畳めない。単純和のまま集計へ残し、件数を診断へ出す。
        includedUnkeyed.lines++;
        if (e.hits > 0) includedUnkeyed.covered++;
        includedUnkeyed.branches += e.branches;
        includedUnkeyed.coveredBranches += e.coveredBranches;
        continue;
      }
      const base = `${c.name === null ? '' : c.name}\u0000${fileKey.key}\u0000${e.number}`;
      const seen = occurrence.get(base) || 0;
      occurrence.set(base, seen + 1);
      includedEntries.push({
        key: seen === 0 ? base : `${base}\u0000#${seen}`,
        hits: e.hits,
        branches: e.branches,
        coveredBranches: e.coveredBranches,
      });
    }
  }

  // どの <class> にも属さない <line>。帰属できない＝除外できないため集計には残し、診断で可視化する
  // （黙って落とすと実測値が理由不明に下がる）。正常な coverlet 出力では 0 件である。
  const orphan = countLines(outside);
  addTotals(totals, orphan);
  // orphan は class も filename も持たずキーを作れない。畳めないぶんとして単純和で残す。
  addTotals(includedUnkeyed, orphan);

  return {
    ...totals,
    // #900 / IADR-0236: レポート間の重複排除の材料。totals は「このレポート単体の集計値」のままで、
    // 意味を変えない（aggregateReports が畳んだ値を別に作る）。
    // 不変条件: totals.lines === included.entries.length + included.unkeyed.lines
    included: { entries: includedEntries, unkeyed: includedUnkeyed, unnormalizedLines },
    excluded: { ...excluded, classes: excludedClasses },
    generated: {
      ...generated,
      classCount: generatedClasses,
      byUnit: Object.fromEntries([...generatedByUnit.entries()]),
      byKind: Object.fromEntries([...generatedByKind.entries()]),
      samples: generatedSamples,
    },
    diagnostics: {
      sources,
      classCount: classes.length,
      attributed: classes.length - how.unattributed,
      how,
      unitTotals: Object.fromEntries([...unitTotals.entries()].map(([k, v]) => [k, v])),
      fallbackClasses,
      emptyClasses,
      orphan,
      // 全 <line>（<methods> 配下の重複込み）。二重記載の排除が効いているかの観測点（決定 5）。
      raw: countLines(text),
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
 * レポート間で行を重複排除して畳む（#900 / IADR-0236 決定 1）。
 *
 * 共有ライブラリ（Platform.Shared.Infrastructure 等）の行は、それを参照するテストプロジェクトの
 * 数だけ各レポートに載る。従来はレポート単位の集計値を単純加算していたため、**分母が参照数だけ
 * 水増しされ、テストプロジェクトを増やす行為が床判定では罰になっていた**（#899 で実際に割れた）。
 *
 * 畳み方は同一キーの行についてフィールドごとの max である。
 *   - hits            … max > 0 なら被覆＝**OR**（1 つのレポートで被覆されていれば被覆）
 *   - branches        … 分岐分母
 *   - coveredBranches … 分岐分子
 * 各レポートで coveredBranches <= branches なので max(coveredBranches) <= max(branches) が成り立ち、
 * 分子が分母を超えることはない。
 *
 * 🔴 **分岐の max は「測定定義の変更」である**（IADR-0236 決定 3）。Cobertura の <line> が分岐に
 * ついて持つのは condition-coverage="50% (1/2)" という**個数だけ**で、どの分岐が通ったかの識別子が
 * 無い。レポート A が分岐 1 を、B が分岐 2 を被覆していても（真の和集合は 2/2）合成できず 1/2 に
 * とどまる。max は真の和集合の**下界**であり、誤差は常に「実際より低く見える」方向にしか出ない
 * （床の検査としては fail-safe）。IADR-0123 決定 4 の 2026-08-04 追記により、分岐の定義の変更は
 * **床の置き直しとセットでしか行えない**。
 */
function foldLineEntries(parsedList) {
  const byKey = new Map();
  const unkeyed = zeroTotals();
  let unnormalizedLines = 0;
  for (const p of parsedList) {
    const inc = p.included || { entries: [], unkeyed: zeroTotals(), unnormalizedLines: 0 };
    for (const e of inc.entries) {
      const prev = byKey.get(e.key);
      if (!prev) {
        byKey.set(e.key, {
          hits: e.hits, branches: e.branches, coveredBranches: e.coveredBranches, reports: 1,
        });
        continue;
      }
      // 同一レポート内のキーは連番で衝突を避けてあるので、ここに来るのは必ず別レポート由来である。
      prev.reports++;
      if (e.hits > prev.hits) prev.hits = e.hits;
      if (e.branches > prev.branches) prev.branches = e.branches;
      if (e.coveredBranches > prev.coveredBranches) prev.coveredBranches = e.coveredBranches;
    }
    addTotals(unkeyed, inc.unkeyed);
    unnormalizedLines += inc.unnormalizedLines || 0;
  }
  const totals = { ...unkeyed };
  // 出現レポート数 → キー数。**プレーンオブジェクトで持つ**（Map は JSON.stringify で {} に消える）。
  const histogram = {};
  let duplicatedKeys = 0;
  for (const r of byKey.values()) {
    totals.lines++;
    if (r.hits > 0) totals.covered++;
    totals.branches += r.branches;
    totals.coveredBranches += r.coveredBranches;
    if (r.reports > 1) {
      duplicatedKeys++;
      histogram[r.reports] = (histogram[r.reports] || 0) + 1;
    }
  }
  return {
    totals, duplicatedKeys, histogram, unnormalizedLines,
    keyCount: byKey.size,
    unkeyedLines: unkeyed.lines,
  };
}

/**
 * parseCobertura の結果（レポート単位）を合算する。
 * 集計対象（totals）・除外分（excluded）・診断（diagnostics）をまとめて返す。
 *
 * 🔴 **重複排除するのは totals だけである**（#900 / IADR-0236 決定 4）。
 * excluded / generated / beforeExclusion / beforeGeneratedExclusion / unitTotals は**単純和のまま**。
 * とくに beforeExclusion は IADR-0123 決定 4 の「coverlet の lines-valid との照合」に使われ、
 * 照合相手（diagnostics.reported）は**レポートごとの lines-valid を単純加算した値**である
 * ——coverlet はレポート間の重複を知らない。ここを重複排除すると、行側で唯一の反証装置が
 * 恒常的に「乖離・要調査」を出すようになり、本当の前提破れと区別できなくなる。
 */
function aggregateReports(parsedList) {
  // 重複排除**前**の単純和。beforeExclusion 系の再構成と、重複排除の前後比較に使う。
  const summed = mergeTotals(parsedList);
  const fold = foldLineEntries(parsedList);
  const totals = fold.totals;
  const excluded = mergeTotals(parsedList.map((p) => p.excluded));
  const generated = mergeTotals(parsedList.map((p) => p.generated));
  const generatedByUnit = {};
  const generatedByKind = {};
  const generatedSamples = [];
  let generatedClasses = 0;
  const excludedClasses = [];
  const how = { relative: 0, absolute: 0, 'source-joined': 0, unattributed: 0 };
  const unitTotals = {};
  const orphan = zeroTotals();
  const raw = zeroTotals();
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
    generatedClasses += p.generated.classCount;
    for (const [unit, t] of Object.entries(p.generated.byUnit)) {
      if (!generatedByUnit[unit]) generatedByUnit[unit] = zeroTotals();
      addTotals(generatedByUnit[unit], t);
    }
    for (const [kind, t] of Object.entries(p.generated.byKind || {})) {
      if (!generatedByKind[kind]) generatedByKind[kind] = { ...zeroTotals(), classCount: 0 };
      addTotals(generatedByKind[kind], t);
      generatedByKind[kind].classCount += t.classCount || 0;
    }
    for (const s of p.generated.samples) if (generatedSamples.length < MAX_SAMPLES) generatedSamples.push(s);
    for (const k of Object.keys(how)) how[k] += d.how[k] || 0;
    for (const [unit, t] of Object.entries(d.unitTotals)) {
      if (!unitTotals[unit]) unitTotals[unit] = zeroTotals();
      addTotals(unitTotals[unit], t);
    }
    addTotals(orphan, d.orphan);
    addTotals(raw, d.raw);
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
    generated: {
      ...generated,
      classCount: generatedClasses,
      byUnit: generatedByUnit,
      byKind: generatedByKind,
      samples: generatedSamples,
    },
    // 生成コードだけを戻した値（#571 / IADR-0138 の前後比較用）。**重複込み・単純和**。
    beforeGeneratedExclusion: mergeTotals([summed, generated]),
    // すべての除外の前（＝混入込み・生成コード込み）の値。coverlet の lines-valid との照合
    // （IADR-0123 決定 4）はこの値で行う——除外を足し戻さないと突合が成立しない。
    // 🔴 **summed（重複排除前）から組む。** 畳んだ totals から組むと照合が恒常的に割れる。
    beforeExclusion: mergeTotals([summed, excluded, generated]),
    // レポート跨ぎの重複排除の**前**の集計値（#900 / IADR-0236。前後比較の観測点）。
    beforeCrossReportDedup: summed,
    diagnostics: {
      sources: [...sources],
      classCount,
      attributed,
      how,
      unitTotals,
      orphan,
      raw,
      reported: reportsWithReported ? reported : null,
      reportsWithReported,
      reportCount: parsedList.length,
      // #900 / IADR-0236: レポート跨ぎの重複排除の観測点。
      dedup: {
        droppedLines: summed.lines - totals.lines,
        droppedCovered: summed.covered - totals.covered,
        droppedBranches: summed.branches - totals.branches,
        droppedCoveredBranches: summed.coveredBranches - totals.coveredBranches,
        duplicatedKeys: fold.duplicatedKeys,
        keyCount: fold.keyCount,
        histogram: fold.histogram,
        unnormalizedLines: fold.unnormalizedLines,
        unkeyedLines: fold.unkeyedLines,
      },
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

  // NFR（#571 / IADR-0138 決定 3、#574 / IADR-0195 決定 2）: 生成コードのフィルタが何にもマッチ
  // しない状態は、床を静かに元の定義へ戻す（＝生成物を 1 本足しただけで床判定が動く状態へ逆戻り
  // する）。出力先を変えれば正常に 0 件になり得るため fail や warn にはせず notice で毎回可視化する
  // （IADR-0118 決定 6 の段階ポリシー）。
  //
  // **種別ごとに見る。** 合算 1 本で見ると、EF 側のフィルタが壊れても source generator 側の
  // 4740 行が数を埋めてしまい、notice が出ない（守っていたはずの穴が黙って開く）。
  if (d.classCount > 0) {
    for (const kind of Object.keys(GENERATED_KIND_LABEL)) {
      const t = agg.generated.byKind && agg.generated.byKind[kind];
      if (t && t.lines > 0) continue;
      msgs.push({
        level: 'notice',
        text:
          `[check-coverage-floor] 生成コードのうち ${GENERATED_KIND_LABEL[kind]} 由来の行は 0 行でした。` +
          ' 対象の生成物が 1 本も無いか、出力先が想定と異なりフィルタが素通りしています' +
          '（#571 / IADR-0138 決定 1 ／ #574 / IADR-0195 決定 1）。',
      });
    }
  }

  // NFR（#900 / IADR-0236 決定 5）: レポートが 2 件以上あるのに重複排除で 1 行も落ちない状態は、
  // 「共有ライブラリを参照するテストが重なっていない」か「<class name> や正規化キーがレポート間で
  // 揃わず畳めていない（＝分母が二重計上のまま）」かのどちらかである。**<class name> がレポート跨ぎで
  // 安定していることは未確認**（実装時に手元へ実レポートが 0 件だった）ため、素通りに毎回気付ける
  // ようにする。正常に 0 行になり得るので fail でも warn でもなく notice にする
  // （IADR-0138 決定 3 / IADR-0195 決定 2 と同じ「除外量 0 = フィルタ素通り」の作法）。
  //
  // reportCount > 1 でゲートするのは、1 レポートなら重複排除は定義上恒等であり、
  // ローカル実行や単一フィクスチャで恒常ノイズになるためである（IADR-0118 決定 6 の段階ポリシー）。
  if (d.reportCount > 1 && d.dedup && d.dedup.droppedLines === 0) {
    msgs.push({
      level: 'notice',
      text:
        `[check-coverage-floor] レポート跨ぎの重複排除で落ちた行は 0 行でした（${d.reportCount} レポート）。` +
        ' 共有プロジェクトを参照するテストが重なっていないか、<class name> や正規化キーが' +
        'レポート間で揃わず畳めていない（＝分母が二重計上のまま）状態です（#900 / IADR-0236 決定 2）。',
    });
  }

  if (d.dedup && d.dedup.unnormalizedLines > 0) {
    msgs.push({
      level: 'notice',
      text:
        `[check-coverage-floor] 重複排除キーを src/<unit>/ 経路へ正規化できなかった行が ` +
        `${d.dedup.unnormalizedLines} 行あります（未帰属クラス由来）。生の filename をキーにしているため、` +
        '同じファイルでもレポート間で表記が違うと畳まれず、分母が二重計上のまま残ります' +
        `（#900 / IADR-0236 決定 2）。filename 例: ${JSON.stringify(d.unattributedSamples)}`,
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
 * floor は表示にのみ使う（床の値の単一情報源は src/coverage-floor.json。ここへ数値を書かない）。
 */
function formatDiagnostics(agg, floor = {}) {
  const d = agg.diagnostics;
  const out = [];
  const units = [...EXCLUDED_UNITS].join(', ') || '（なし）';

  // NFR（#900 / IADR-0236 決定 5）: レポート跨ぎの重複排除を毎回出す。共有ライブラリの行が
  // 参照するテストプロジェクトの数だけ分母に載る状態を、CI ログからそのまま読めるようにする。
  {
    const dd = d.dedup || {};
    const hist = Object.keys(dd.histogram || {})
      .map(Number)
      .sort((a, b) => a - b)
      .map((n) => `${n} 部 ${dd.histogram[n]} 行`)
      .join(' / ');
    out.push(
      `レポート跨ぎの重複排除（#900）: ${d.reportCount} レポート。重複排除前 ${formatTotals(agg.beforeCrossReportDedup)}` +
        ` → 後 ${formatTotals(agg.totals)}。落とした重複 ${dd.droppedLines} 行（被覆 ${dd.droppedCovered}） / ` +
        `分岐分母 ${dd.droppedBranches}（被覆 ${dd.droppedCoveredBranches}）。` +
        ` 重複していたキー ${dd.duplicatedKeys} 件 / 全キー ${dd.keyCount} 件。` +
        ` 出現レポート数の内訳: ${hist || '（重複なし）'}。` +
        ` キーを src/<unit>/ へ正規化できなかった行 ${dd.unnormalizedLines} 行（未帰属クラス由来。生パスをキーにしている）／` +
        `行番号を持たず畳めなかった <line> ${dd.unkeyedLines} 行。`,
    );
  }

  out.push(
    `除外（filename 帰属・#468）: 集計対象外ユニット（${units}）由来 ${agg.excluded.classes.length} クラス / ` +
      `${agg.excluded.lines} 行（被覆 ${agg.excluded.covered}） / 分岐 ${agg.excluded.branches}（被覆 ${agg.excluded.coveredBranches}）を落としました。` +
      ` 除外前: ${formatTotals(agg.beforeExclusion)}（生成コードも戻した値）` +
      // #900 / IADR-0236 決定 4: 除外量と「除外前」はレポート跨ぎの重複を**含んだ単純和**である。
      // 床が判定に使う値（重複排除後）と桁が違うため、混同されないよう毎回書く。
      '。※ この行の値はレポート跨ぎの重複込み・単純和（coverlet の lines-valid と突き合わせるため）',
  );

  // NFR（#571 / IADR-0138 決定 2）: 生成コードの除外量を毎回出す。AST 除外と同じ作法で、
  // 「何行落としたか」「除外前後で実測値がどう動いたか」を CI ログからそのまま読めるようにする。
  {
    const g = agg.generated;
    const byUnit = Object.entries(g.byUnit)
      .sort((a, b) => b[1].lines - a[1].lines)
      .map(([unit, t]) => `${unit} ${t.lines} 行（被覆 ${t.covered}）`)
      .join(' / ');
    // 種別ごとの内訳（#574 / IADR-0195 決定 2）。合計だけだと、片方の種別が増減しても
    // もう片方の変化と打ち消し合って CI ログから読めない。
    const byKind = Object.keys(GENERATED_KIND_LABEL)
      .map((kind) => {
        const t = (g.byKind && g.byKind[kind]) || { ...zeroTotals(), classCount: 0 };
        return `${GENERATED_KIND_LABEL[kind]} ${t.classCount} クラス / ${t.lines} 行（被覆 ${t.covered}） / ` +
          `分岐 ${t.branches}（被覆 ${t.coveredBranches}）`;
      })
      .join(' ／ ');
    out.push(
      `除外（生成コード・#571 / #574）: 計 ${g.classCount} クラス / ` +
        `${g.lines} 行（被覆 ${g.covered}） / 分岐 ${g.branches}（被覆 ${g.coveredBranches}）を落としました。` +
        ` 種別内訳: ${byKind}。` +
        ` 生成コードを戻すと: ${formatTotals(agg.beforeGeneratedExclusion)}` +
        `。ユニット内訳: ${byUnit || '（0 件）'}。filename 例: ${JSON.stringify(g.samples)}`,
    );
  }

  out.push(
    `帰属: クラス ${d.classCount} 件（そのまま(相対) ${d.how.relative} / そのまま(絶対) ${d.how.absolute} / ` +
      `<sources> 結合 ${d.how['source-joined']} / 未帰属 ${d.how.unattributed}）。` +
      ` <sources>: ${JSON.stringify(d.sources)}。filename 例: ${JSON.stringify(d.filenameSamples)}` +
      (d.unattributedSamples.length ? `。未帰属の例: ${JSON.stringify(d.unattributedSamples)}` : ''),
  );

  const unitLine = Object.entries(d.unitTotals)
    .sort((a, b) => b[1].lines - a[1].lines)
    .map(([unit, t]) => {
      const g = agg.generated.byUnit[unit];
      return `${EXCLUDED_UNITS.has(unit) ? '[除外] ' : ''}${unit} ${t.lines} 行（被覆 ${t.covered}` +
        `${g ? `・うち生成 ${g.lines} 行（被覆 ${g.covered}）を除外` : ''}）`;
    })
    .join(' / ');
  out.push(`ユニット別の行数: ${unitLine || '（0 件）'}` +
    (d.orphan.lines ? ` / [class 外] ${d.orphan.lines} 行` : ''));

  if (d.reported) {
    const mine = agg.beforeExclusion;
    // NFR（#468 / IADR-0123 決定 4・2026-08-04 追記）: line と branch で照合の意味が違う。
    //   line   … 同じものを数えている。**一致を期待する**。乖離は決定 3（class 直下の <lines> を正とする）
    //            の前提が破れた信号であり、要調査として目立たせる。
    //   branch … 定義が異なる。本実装が数えるのは <line> の condition-coverage の分母/分子であり、
    //            coverlet の branches-valid は別経路で算出されているとみられる（一次出典未検証）。
    //            **一致を期待しない**。同列に「乖離」と出すと、期待される差が異常に見える。
    const agreeLine = (a, b) => (a === b ? '一致' : `**乖離 ${b - a}・要調査**`);
    const agreeBranch = (a, b) => (a === b ? '一致' : `差 ${b - a}（定義差・期待される乖離）`);
    const branchDiffers = d.reported.branches !== mine.branches || d.reported.coveredBranches !== mine.coveredBranches;
    // 床の値は src/coverage-floor.json が単一情報源（IADR-0118 決定 1）。ここに数値を書くと
    // ratchet で床を上げた瞬間に同じログの中で自己矛盾する。
    const branchFloor = floor && floor.branch != null ? `床 ${floor.branch}` : '床（src/coverage-floor.json の branch）';
    out.push(
      `coverlet 自身の集計値との照合（IADR-0123 決定 4。除外前で比較・${d.reportsWithReported}/${d.reportCount} レポート）: ` +
        `lines-valid ${d.reported.lines}（本実装 ${mine.lines}・${agreeLine(d.reported.lines, mine.lines)}） / ` +
        `lines-covered ${d.reported.covered}（本実装 ${mine.covered}・${agreeLine(d.reported.covered, mine.covered)}） / ` +
        `branches-valid ${d.reported.branches}（本実装 ${mine.branches}・${agreeBranch(d.reported.branches, mine.branches)}） / ` +
        `branches-covered ${d.reported.coveredBranches}（本実装 ${mine.coveredBranches}・${agreeBranch(d.reported.coveredBranches, mine.coveredBranches)}）` +
        (branchDiffers
          ? '。※ 分岐は定義が異なるため一致を期待しない（本実装は condition-coverage の合算。' +
            `${branchFloor} はこの方式での実測に基づくため、定義の変更は床の置き直しとセットでしか行えない）。` +
            '行の乖離のみ決定 3 の反証になる。'
          : ''),
    );
  }

  // NFR（#468 / IADR-0123 決定 5）: 分岐側の観測点。行は lines-valid との一致が決定 3 の裏づけになるが、
  // 分岐は定義差のため照合が反証力を持たない。二重記載の排除が分岐で壊れても値が増えるだけで
  // CI ログには何も現れない（無音の失敗）。そこで「全 <line>（<methods> 重複込み）」と
  // 「class 直下のみ（＝集計値）」の比を出し、実測の 2 倍関係が崩れたら目視で分かるようにする。
  // 注: raw は class 外の <line> も含む（正常な coverlet 出力では 0 行。上の「ユニット別の行数」で可視化）。
  {
    const mine = agg.beforeExclusion;
    const ratio = (raw, direct) => (direct ? (raw / direct).toFixed(2) : '—');
    out.push(
      '二重記載の観測（IADR-0123 決定 3・決定 5）: 全 <line>（<methods> 重複込み）= ' +
        `行 ${d.raw.lines} / 分岐分母 ${d.raw.branches}（被覆 ${d.raw.coveredBranches}）。` +
        `class 直下のみ（除外前の集計）= 行 ${mine.lines} / 分岐分母 ${mine.branches}（被覆 ${mine.coveredBranches}）。` +
        `比 行 ${ratio(d.raw.lines, mine.lines)} / 分岐 ${ratio(d.raw.branches, mine.branches)}` +
        '（実測は厳密に 2.0。崩れたら二重記載の扱いが壊れた可能性がある）',
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

  {
    // 照合の書き分け（IADR-0123 決定 4・［2026-08-04 追記］）。CI 実測で branches-valid だけが乖離した
    // （行は完全一致）。分岐は定義が異なり一致を期待しないため、行の乖離と同列に出さない。
    const cls = (attrs) => `<coverage ${attrs}><packages><package><classes>` +
      '<class name="X" filename="src/platform/backend/X.cs"><lines>' +
      '<line number="1" hits="1" branch="true" condition-coverage="50% (1/2)" />' +
      '</lines></class></classes></package></packages></coverage>';
    const same = formatDiagnostics(aggregateReports([parseCobertura(
      cls('lines-valid="1" lines-covered="1" branches-valid="4" branches-covered="1"'))])).join('\n');
    t('formatDiagnostics: 行は一致・分岐の差は「定義差・期待される乖離」と書き分ける',
      same.includes('lines-valid 1（本実装 1・一致）')
        && same.includes('branches-valid 4（本実装 2・差 -2（定義差・期待される乖離）')
        && !same.includes('**乖離'), same);
    t('formatDiagnostics: branches-covered も照合に出す（coverlet 側の値をログから読めること）',
      same.includes('branches-covered 1（本実装 1・一致）'), same);
    // 床の値は src/coverage-floor.json が単一情報源。診断は渡された床を表示し、数値を持たない。
    t('formatDiagnostics: 注記の床は引数の floor を反映する（ハードコードしない）',
      formatDiagnostics(aggregateReports([parseCobertura(
        cls('lines-valid="1" lines-covered="1" branches-valid="4" branches-covered="1"'))]), { branch: 18 })
        .join('\n').includes('床 18 はこの方式')
        && same.includes('床（src/coverage-floor.json の branch） はこの方式'), same);
    // 分岐が一致する（＝注記が不要な）ときはノイズを出さない。
    const branchSame = formatDiagnostics(aggregateReports([parseCobertura(
      cls('lines-valid="1" lines-covered="1" branches-valid="2" branches-covered="1"'))])).join('\n');
    t('formatDiagnostics: 分岐が一致していれば「※ 分岐は…」の注記を出さない',
      branchSame.includes('branches-valid 2（本実装 2・一致）') && !branchSame.includes('※ 分岐は'), branchSame);
    const drift = formatDiagnostics(aggregateReports([parseCobertura(
      cls('lines-valid="9" lines-covered="9" branches-valid="2" branches-covered="1"'))])).join('\n');
    t('formatDiagnostics: 行の乖離は要調査として目立たせる（決定 3 の前提の破れ）',
      drift.includes('**乖離 -8・要調査**'), drift);
  }

  {
    // 分岐側の観測点（決定 5）: 全 <line>（<methods> 重複込み）と class 直下のみの比。
    // 分岐の二重記載排除が壊れても照合（定義差）では気付けないため、比を出して目視できるようにする。
    const xml = '<coverage><packages><package><classes>' +
      '<class name="X" filename="src/platform/backend/X.cs">' +
      '<methods><method name="M"><lines>' +
      '<line number="1" hits="1" branch="true" condition-coverage="50% (1/2)" />' +
      '<line number="2" hits="1" /></lines></method></methods>' +
      '<lines><line number="1" hits="1" branch="true" condition-coverage="50% (1/2)" />' +
      '<line number="2" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const text = formatDiagnostics(aggregateReports([parseCobertura(xml)])).join('\n');
    t('formatDiagnostics: 二重記載の観測（全 <line> と class 直下の比）を出す',
      text.includes('全 <line>（<methods> 重複込み）= 行 4 / 分岐分母 4（被覆 2）')
        && text.includes('class 直下のみ（除外前の集計）= 行 2 / 分岐分母 2（被覆 1）')
        && text.includes('比 行 2.00 / 分岐 2.00'), text);
  }

  // --- #571 / IADR-0138: 生成コード（EF の Migrations / ModelSnapshot）を集計から落とす ---

  // 判定に使うパスの形は develop の実レポートから採った（形を仮定すると素通りする）。
  t('isGeneratedFilename: <sources> が …/src/ のレポートの形（Migrations 本体）',
    isGeneratedFilename('knowledge/backend/Services/WikiService/src/WikiService.Api/Migrations/20260626150858_InitialCreate.cs'));
  t('isGeneratedFilename: 同上の Designer.cs',
    isGeneratedFilename('knowledge/backend/Services/WikiService/src/WikiService.Api/Migrations/20260626150858_InitialCreate.Designer.cs'));
  t('isGeneratedFilename: <sources> が …/src/platform/backend/ のレポートの形（先頭が Services/…）',
    isGeneratedFilename('Services/AuthorizationService/src/AuthorizationService.Api/Migrations/AuthorizationDbContextModelSnapshot.cs'));
  t('isGeneratedFilename: 絶対パスでも当たる',
    isGeneratedFilename('/home/runner/work/msp/msp/src/knowledge/backend/X/Migrations/20260101_Init.cs'));
  t('isGeneratedFilename: Windows の区切りでも当たる',
    isGeneratedFilename('C:\\work\\msp\\src\\knowledge\\backend\\X\\Migrations\\Init.cs'));
  t('isGeneratedFilename: Migrations/ の外の ModelSnapshot も当たる（出力先を変えた場合の取りこぼし防止）',
    isGeneratedFilename('knowledge/backend/X/Data/FooDbContextModelSnapshot.cs'));
  t('isGeneratedFilename: 手書きコードは落とさない',
    !isGeneratedFilename('src/platform/backend/Bff/Platform.Bff/HealthEndpoints.cs'));
  t('isGeneratedFilename: MigrationsHelper.cs は落とさない（区切りで見る）',
    !isGeneratedFilename('src/platform/backend/X/MigrationsHelper.cs'));
  t('isGeneratedFilename: MyMigrations/ は落とさない（区切りで見る）',
    !isGeneratedFilename('src/platform/backend/X/MyMigrations/Foo.cs'));
  t('isGeneratedFilename: ModelSnapshotBuilder.cs は落とさない（末尾一致で見る）',
    !isGeneratedFilename('src/platform/backend/X/ModelSnapshotBuilder.cs'));
  t('isGeneratedFilename: filename が無ければ false（未帰属を巻き込まない）',
    !isGeneratedFilename(null) && !isGeneratedFilename(''));

  {
    // 実レポートに近い形: 手書き 1 クラス ＋ 生成 2 クラス。生成分は集計から落ち、診断へ出る。
    const xml = '<coverage lines-valid="6" lines-covered="4"><sources><source>/w/src/</source></sources>' +
      '<packages><package name="WikiService.Api"><classes>' +
      '<class name="WikiService.Api.Endpoints" filename="knowledge/backend/Services/WikiService/src/WikiService.Api/Endpoints.cs">' +
      '<lines><line number="1" hits="1" /><line number="2" hits="0" /></lines></class>' +
      '<class name="WikiService.Api.Migrations.InitialCreate" filename="knowledge/backend/Services/WikiService/src/WikiService.Api/Migrations/20260626150858_InitialCreate.cs">' +
      '<lines><line number="10" hits="3" /><line number="11" hits="0" /></lines></class>' +
      '<class name="WikiService.Api.Migrations.WikiDbContextModelSnapshot" filename="knowledge/backend/Services/WikiService/src/WikiService.Api/Migrations/WikiDbContextModelSnapshot.cs">' +
      '<lines><line number="20" hits="4" /><line number="21" hits="4" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const p = parseCobertura(xml);
    t('parseCobertura: 生成コードを集計から落とす', p.lines === 2 && p.covered === 1, p);
    t('parseCobertura: 落とした生成コードを別枠で数える',
      p.generated.lines === 4 && p.generated.covered === 3 && p.generated.classCount === 2, p.generated);
    // 帰属先のキーは unitOfFilename の結果に従う（本改修では帰属規則を変えない）。ここで見たいのは
    // 「生成コードがユニット別にも数えられている」ことなので、キー名ではなく内訳の合計で確かめる。
    t('parseCobertura: 生成コードをユニット別に数える',
      Object.keys(p.generated.byUnit).length === 1
        && Object.values(p.generated.byUnit).reduce((n, u) => n + u.lines, 0) === 4, p.generated.byUnit);
    const agg = aggregateReports([p]);
    t('aggregateReports: 生成コードだけ戻した値を持つ（前後比較用）',
      agg.beforeGeneratedExclusion.lines === 6 && agg.beforeGeneratedExclusion.covered === 4, agg.beforeGeneratedExclusion);
    t('aggregateReports: coverlet 照合は全除外を戻した値で行う（lines-valid と一致する）',
      agg.beforeExclusion.lines === 6 && agg.beforeExclusion.covered === 4, agg.beforeExclusion);
    const text = formatDiagnostics(agg).join('\n');
    t('formatDiagnostics: 生成コードの除外量を出す（AST 除外と同じ作法）',
      text.includes('除外（生成コード・#571 / #574）: 計 2 クラス / 4 行（被覆 3）'), text);
    t('formatDiagnostics: 生成コードを戻した値も出す（前後比較が CI ログで読める）',
      text.includes('生成コードを戻すと: line 66.67%（4/6）'), text);
    t('formatDiagnostics: ユニット別の行数に生成の内訳を添える',
      /ユニット別の行数: \S+ 6 行（被覆 4・うち生成 4 行（被覆 3）を除外）/.test(text), text);
    t('attributionMessages: EF 側が落ちていれば EF の notice は出さない',
      attributionMessages(agg).every((m) => !/EF（Migrations\//.test(m.text)), attributionMessages(agg));
  }
  {
    // 生成コードが 1 行も落ちない＝フィルタが素通り。fail にはしないが notice で毎回可視化する。
    const onlyHandWritten = '<coverage><packages><package><classes>' +
      '<class name="X" filename="src/platform/backend/X.cs"><lines><line number="1" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const msgs = attributionMessages(aggregateReports([parseCobertura(onlyHandWritten)]));
    t('attributionMessages: 生成コード 0 行は notice（素通りに気付ける）',
      msgs.some((m) => m.level === 'notice' && /生成コード/.test(m.text))
        && msgs.every((m) => m.level !== 'warn'), msgs);
    t('attributionMessages: 2 種とも 0 行なら notice も 2 本出る（種別ごとに見る）',
      msgs.filter((m) => /生成コードのうち/.test(m.text)).length === 2, msgs);
  }

  // --- #574 / IADR-0195: source generator の出力（obj/ 配下）も集計から落とす ---

  // 判定に使うパスの形は develop `1d7edce` の実レポートから採った（全数 1061 件を分類し、
  // obj/ 配下 14 件が全て *.generated.cs / *.g.cs、手書きの巻き込み 0 件であることを確認した）。
  t('generatedKindOf: OpenApiXmlCommentSupport.generated.cs（実レポートの形）',
    generatedKindOf('knowledge/backend/Services/AiAnalysisService/obj/Release/net10.0/Microsoft.AspNetCore.OpenApi.SourceGenerators/Microsoft.AspNetCore.OpenApi.SourceGenerators.XmlCommentGenerator/OpenApiXmlCommentSupport.generated.cs') === 'sourcegen');
  t('generatedKindOf: RegexGenerator.g.cs（実レポートの形）',
    generatedKindOf('platform/backend/Bff/Platform.Bff/obj/Release/net10.0/System.Text.RegularExpressions.Generator/System.Text.RegularExpressions.Generator.RegexGenerator/RegexGenerator.g.cs') === 'sourcegen');
  t('generatedKindOf: <sources> が …/src/platform/backend/ のレポートの形（先頭が Services/…）',
    generatedKindOf('Services/AuthorizationService/src/AuthorizationService.Api/obj/Release/net10.0/X.generated.cs') === 'sourcegen');
  t('generatedKindOf: 絶対パスでも当たる',
    generatedKindOf('/home/runner/work/msp/msp/src/knowledge/backend/X/obj/Debug/net10.0/Y.g.cs') === 'sourcegen');
  t('generatedKindOf: Windows の区切りでも当たる',
    generatedKindOf('C:\\work\\msp\\src\\knowledge\\backend\\X\\obj\\Release\\net10.0\\Y.g.cs') === 'sourcegen');
  t('generatedKindOf: EF は ef と判定する（種別が混ざらない）',
    generatedKindOf('knowledge/backend/X/Migrations/20260101_Init.cs') === 'ef');
  t('generatedKindOf: 手書きコードは null',
    generatedKindOf('src/platform/backend/Bff/Platform.Bff/HealthEndpoints.cs') === null);
  t('generatedKindOf: objects/ は落とさない（区切りで見る）',
    generatedKindOf('src/platform/backend/X/objects/Foo.cs') === null);
  t('generatedKindOf: MyObj/ は落とさない（区切りで見る）',
    generatedKindOf('src/platform/backend/X/MyObj/Foo.cs') === null);
  t('generatedKindOf: obj という語を含むだけのファイル名は落とさない',
    generatedKindOf('src/platform/backend/X/ObjectMapper.cs') === null);
  t('generatedKindOf: filename が無ければ null（未帰属を巻き込まない）',
    generatedKindOf(null) === null && generatedKindOf('') === null);

  {
    // 手書き 1 ・EF 1 ・source generator 1。種別ごとに数え、種別ごとに診断へ出す。
    const xml = '<coverage><sources><source>/w/src/</source></sources>' +
      '<packages><package name="WikiService.Api"><classes>' +
      '<class name="Endpoints" filename="knowledge/backend/Services/WikiService/src/WikiService.Api/Endpoints.cs">' +
      '<lines><line number="1" hits="1" /><line number="2" hits="0" /></lines></class>' +
      '<class name="InitialCreate" filename="knowledge/backend/Services/WikiService/src/WikiService.Api/Migrations/20260626150858_InitialCreate.cs">' +
      '<lines><line number="10" hits="3" /></lines></class>' +
      '<class name="XmlComments" filename="knowledge/backend/Services/WikiService/src/WikiService.Api/obj/Release/net10.0/G/OpenApiXmlCommentSupport.generated.cs">' +
      '<lines><line number="20" hits="0" branch="true" condition-coverage="0% (0/4)" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const p = parseCobertura(xml);
    t('parseCobertura: source generator の出力も集計から落とす',
      p.lines === 2 && p.covered === 1 && p.branches === 0, p);
    t('parseCobertura: 種別ごとに数える（合算で埋め合わない）',
      p.generated.byKind.ef.lines === 1 && p.generated.byKind.ef.classCount === 1
        && p.generated.byKind.sourcegen.lines === 1 && p.generated.byKind.sourcegen.branches === 4,
      p.generated.byKind);
    const agg = aggregateReports([p]);
    const text = formatDiagnostics(agg).join('\n');
    t('formatDiagnostics: 種別内訳を出す（片方の増減がもう片方に埋もれない）',
      text.includes('種別内訳: EF（Migrations/ 配下・*ModelSnapshot.cs） 1 クラス / 1 行（被覆 1）')
        && text.includes('source generator（obj/ 配下） 1 クラス / 1 行（被覆 0） / 分岐 4（被覆 0）'), text);
    t('attributionMessages: 2 種とも落ちていれば notice を出さない',
      attributionMessages(agg).every((m) => !/生成コードのうち/.test(m.text)), attributionMessages(agg));
  }
  {
    // ★ 種別を分けて持つ理由の固定。EF 側のフィルタが壊れても source generator 側が数を埋めるため、
    //    合算 1 本で見ていると notice が出ない（守っていたはずの穴が黙って開く）。
    const onlySourcegen = '<coverage><packages><package><classes>' +
      '<class name="X" filename="src/platform/backend/X.cs"><lines><line number="1" hits="1" /></lines></class>' +
      '<class name="G" filename="src/platform/backend/X/obj/Release/net10.0/G.generated.cs">' +
      '<lines><line number="1" hits="0" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const agg = aggregateReports([parseCobertura(onlySourcegen)]);
    t('attributionMessages: 片方だけ 0 行でも notice が出る（合算では気付けない状態を検出する）',
      agg.generated.lines > 0
        && attributionMessages(agg).some((m) => m.level === 'notice' && /EF（Migrations\//.test(m.text)),
      attributionMessages(agg));
  }
  {
    // 集計対象外ユニット配下の生成コードは「ユニット除外」として数える（二重計上しない）。
    // IADR-0123 が記録した混入行数（AST 由来 n 行）の意味を本改修で変えないための固定。
    const xml = '<coverage><packages><package><classes>' +
      `<class name="M" filename="src/${excludedUnitName}/backend/X/Migrations/20260101_Init.cs">` +
      '<lines><line number="1" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const p = parseCobertura(xml);
    t('parseCobertura: 除外ユニット配下の生成コードはユニット除外側で数える（二重計上しない）',
      p.excluded.lines === 1 && p.generated.lines === 0 && p.lines === 0, { excluded: p.excluded.lines, generated: p.generated.lines });
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

  // --- #900 / IADR-0236: レポート跨ぎの行重複排除 ---------------------------------
  //
  // 🔴 **受け入れ基準を rate で書いてはならない。** 「同じレポートを 2 部与えても集計値が変わらない」
  // は正しいが、**同じレポート 2 部では被覆率は動かない**（分子分母が等倍で増えるため 50% のまま）。
  // 率が動くのは「同じ行を違う被覆で載せた 2 部」のときだけである。変異試験を rate で書くと
  // **重複排除を外しても緑のまま通る**（検査が静かに no-op 化する）。
  //
  // 🔴 **ケース 1（不変）とケース 2（差が出る）の両方**を必ず置く。片方だけでは片方向の穴が開く ——
  //   ケース 1 だけ: 畳み込みを「常に全部潰す」実装にしても通る
  //   ケース 2 だけ: 「何もしない」実装では落ちるが、過剰に潰す実装は通る
  // check-backend-libraries.js 規則 5 が「(a) だけでは静かに no-op になる」で踏んだ穴と同型である。

  const SHARED_FILE = 'src/platform/backend/Shared/Platform.Shared.Infrastructure/X.cs';

  {
    // ケース 1: 同一レポート 2 部で totals 不変。lines / covered / branches を**個別に** assert する。
    const xml = '<coverage lines-valid="2" lines-covered="1"><packages><package><classes>' +
      `<class name="Shared.X" filename="${SHARED_FILE}"><lines>` +
      '<line number="1" hits="1" branch="true" condition-coverage="50% (1/2)" />' +
      '<line number="2" hits="0" />' +
      '</lines></class></classes></package></packages></coverage>';
    const agg = aggregateReports([parseCobertura(xml), parseCobertura(xml)]);
    t('aggregateReports: 同一レポート 2 部でも行は 1 部ぶん（単純合算なら 4）',
      agg.totals.lines === 2, agg.totals);
    t('aggregateReports: 同一レポート 2 部でも被覆行は 1 部ぶん（単純合算なら 2）',
      agg.totals.covered === 1, agg.totals);
    t('aggregateReports: 同一レポート 2 部でも分岐は 1 部ぶん（分母 2 / 分子 1）',
      agg.totals.branches === 2 && agg.totals.coveredBranches === 1, agg.totals);
    t('aggregateReports: 重複排除前の値を別に保つ（前後比較の観測点）',
      agg.beforeCrossReportDedup.lines === 4 && agg.beforeCrossReportDedup.covered === 2,
      agg.beforeCrossReportDedup);
    t('aggregateReports: 落とした重複を診断へ出す（2 行 / 重複キー 2 件 / 2 部が 2 行）',
      agg.diagnostics.dedup.droppedLines === 2 && agg.diagnostics.dedup.duplicatedKeys === 2
        && agg.diagnostics.dedup.histogram[2] === 2, agg.diagnostics.dedup);
    // 🔴 決定 4 の非退行: 照合は重複排除**前**の単純和で行う（lines-valid の和 4 と一致し続ける）。
    t('aggregateReports: beforeExclusion は単純和のまま（lines-valid の和と一致し続ける）',
      agg.beforeExclusion.lines === 4 && agg.diagnostics.reported.lines === 4, agg.beforeExclusion);
    t('formatDiagnostics: 重複排除の前後と内訳を出す',
      formatDiagnostics(agg).join('\n').includes('落とした重複 2 行'), formatDiagnostics(agg));
  }

  {
    // ケース 2: 被覆の違う 2 部を OR で畳む。A(hits=1,0) ＋ B(hits=0,0) → lines 2 / covered 1。
    // 現行（単純合算）は lines 4 / covered 1 で rate 50% → 25% になる。**唯一 rate でも差が出る**。
    const mk = (h1, h2) => '<coverage><packages><package><classes>' +
      `<class name="Shared.X" filename="${SHARED_FILE}"><lines>` +
      `<line number="1" hits="${h1}" /><line number="2" hits="${h2}" />` +
      '</lines></class></classes></package></packages></coverage>';
    const agg = aggregateReports([parseCobertura(mk(1, 0)), parseCobertura(mk(0, 0))]);
    t('aggregateReports: 片方のレポートでのみ被覆された行は OR で被覆扱い（lines 2 / covered 1）',
      agg.totals.lines === 2 && agg.totals.covered === 1, agg.totals);
    t('aggregateReports: OR の結果は rate でも差が出る（単純合算の 25% ではなく 50%）',
      rate(agg.totals.covered, agg.totals.lines) === 50, agg.totals);
    // 逆順でも同じ（畳み込みが「最初のレポート優先」に退化していないこと）。
    const rev = aggregateReports([parseCobertura(mk(0, 0)), parseCobertura(mk(1, 0))]);
    t('aggregateReports: OR はレポートの順序に依らない',
      rev.totals.covered === 1, rev.totals);
  }

  {
    // ケース 2b: 分岐も同じく max で畳む。**被覆の低い側が先に来る順序を必ず含める** ——
    // 「最初のレポートの値を採る」実装は、高い側が先の順序だけでは落ちない（M5 変異で実測した）。
    const mk = (cov2) => '<coverage><packages><package><classes>' +
      `<class name="Shared.X" filename="${SHARED_FILE}"><lines>` +
      `<line number="1" hits="1" branch="true" condition-coverage="x% (${cov2}/2)" />` +
      '</lines></class></classes></package></packages></coverage>';
    const lowFirst = aggregateReports([parseCobertura(mk(0)), parseCobertura(mk(2))]);
    t('aggregateReports: 分岐分子は max で畳む（被覆の低い側が先でも 2/2）',
      lowFirst.totals.branches === 2 && lowFirst.totals.coveredBranches === 2, lowFirst.totals);
    const highFirst = aggregateReports([parseCobertura(mk(2)), parseCobertura(mk(0))]);
    t('aggregateReports: 分岐の畳み込みはレポートの順序に依らない',
      highFirst.totals.coveredBranches === 2, highFirst.totals);
    // 分岐分母も同様（分母が違う形で現れたら大きい方を採る）。
    const denom = (b) => '<coverage><packages><package><classes>' +
      `<class name="Shared.X" filename="${SHARED_FILE}"><lines>` +
      `<line number="1" hits="1" branch="true" condition-coverage="x% (0/${b})" />` +
      '</lines></class></classes></package></packages></coverage>';
    const wide = aggregateReports([parseCobertura(denom(2)), parseCobertura(denom(4))]);
    t('aggregateReports: 分岐分母は max で畳む（小さい側が先でも 4）',
      wide.totals.branches === 4, wide.totals);
  }

  {
    // ケース 3: 1 レポートだけなら現行と完全同値（IADR-0123 決定 3・決定 4 の非退行）。
    const p = parseCobertura(FIXTURE_ATTRIBUTED);
    const agg = aggregateReports([p]);
    t('aggregateReports: 1 レポートなら重複排除は恒等（totals がレポート単体の値と全項一致）',
      agg.totals.lines === p.lines && agg.totals.covered === p.covered
        && agg.totals.branches === p.branches && agg.totals.coveredBranches === p.coveredBranches
        && agg.diagnostics.dedup.droppedLines === 0,
      { totals: agg.totals, parsed: { lines: p.lines, covered: p.covered } });
    t('aggregateReports: 1 レポートの beforeExclusion は lines-valid と一致し続ける（決定 4 の照合）',
      agg.beforeExclusion.lines === 4 && agg.diagnostics.reported.lines === 4, agg.beforeExclusion);
    // 不変条件: totals.lines === キー付きの行数 ＋ 畳めなかった行数
    t('parseCobertura: totals は「キー付きの行 ＋ 畳めない行」に分解できる',
      p.lines === p.included.entries.length + p.included.unkeyed.lines,
      { lines: p.lines, keyed: p.included.entries.length, unkeyed: p.included.unkeyed.lines });
  }

  {
    // ケース 4: Foo と <Foo>d__2（非同期ステートマシン）は同一 filename・同一行番号でも潰さない。
    // (filename, 行番号) をキーにすると潰れる —— IADR-0123 が選択肢 C を退けた理由をキー側で保つ。
    const xml = '<coverage><packages><package><classes>' +
      '<class name="Foo" filename="src/platform/backend/X.cs">' +
      '<lines><line number="10" hits="1" /></lines></class>' +
      '<class name="Foo/<Foo>d__2" filename="src/platform/backend/X.cs">' +
      '<lines><line number="10" hits="0" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const agg = aggregateReports([parseCobertura(xml), parseCobertura(xml)]);
    t('aggregateReports: 同一 filename・同一行番号でも class が違えば潰さない（lines 2）',
      agg.totals.lines === 2 && agg.totals.covered === 1, agg.totals);

    // 🔴 上のケースだけでは **キーから class name を落とす変異を検出できない**（実測した）。
    // 同一レポート内の同キーには出現連番が付くため、class name を落としても
    // `Foo` と `<Foo>d__2` は別キーのまま残ってしまい、行数が変わらないからである。
    // **2 つのクラスが別々のレポートに分かれて現れる形**で初めて差が出る ——
    // class name がキーに無いと、この 2 行が同じキーへ落ちて 1 行に潰れる。
    const only = (name, hits) => '<coverage><packages><package><classes>' +
      `<class name="${name}" filename="src/platform/backend/X.cs">` +
      `<lines><line number="10" hits="${hits}" /></lines></class>` +
      '</classes></package></packages></coverage>';
    const split = aggregateReports([
      parseCobertura(only('Foo', 1)),
      parseCobertura(only('Foo/<Foo>d__2', 0)),
    ]);
    t('aggregateReports: 別レポートに分かれた Foo と <Foo>d__2 も潰さない（キーに class name が要る）',
      split.totals.lines === 2 && split.totals.covered === 1, split.totals);
  }

  {
    // ケース 5: filename の形が違う 2 レポート（A: relative / B: <sources> ＋相対の source-joined）でも
    // 正規化キーが一致して畳まれる。生 filename をキーにすると畳めない（無音の部分適用になる）。
    const a = '<coverage><packages><package><classes>' +
      '<class name="Shared.X" filename="src/platform/backend/Shared/X.cs">' +
      '<lines><line number="1" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const b = '<coverage><sources><source>/home/runner/work/msp/msp/src/</source></sources>' +
      '<packages><package><classes>' +
      '<class name="Shared.X" filename="platform/backend/Shared/X.cs">' +
      '<lines><line number="1" hits="0" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const pa = parseCobertura(a);
    const pb = parseCobertura(b);
    t('parseCobertura: 前提 —— 2 レポートは filename の解釈が違う（relative と source-joined）',
      pa.diagnostics.how.relative === 1 && pb.diagnostics.how['source-joined'] === 1,
      { a: pa.diagnostics.how, b: pb.diagnostics.how });
    t('dedupFileKey: 表記の違う同一ファイルが同じキーへ落ちる',
      dedupFileKey(unitOfFilename('src/platform/backend/Shared/X.cs')).key
        === dedupFileKey(unitOfFilename('platform/backend/Shared/X.cs', ['/home/runner/work/msp/msp/src/'])).key,
      dedupFileKey(unitOfFilename('src/platform/backend/Shared/X.cs')).key);
    const agg = aggregateReports([pa, pb]);
    t('aggregateReports: 表記の違う同一ファイルでも畳む（lines 1 / covered 1）',
      agg.totals.lines === 1 && agg.totals.covered === 1, agg.totals);
    t('dedupFileKey: 帰属できない filename は正規化できない（生パスをキーにする）',
      dedupFileKey(unitOfFilename('Foo/Bar.cs')).normalized === false
        && dedupFileKey(unitOfFilename('Foo/Bar.cs')).key === 'Foo/Bar.cs');
  }

  {
    // ケース 6: レポートが 2 件以上あるのに重複排除量が 0 行なら notice（素通りに気付ける）。
    const a = '<coverage><packages><package><classes>' +
      '<class name="A" filename="src/platform/backend/A.cs"><lines><line number="1" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const b = '<coverage><packages><package><classes>' +
      '<class name="B" filename="src/knowledge/backend/B.cs"><lines><line number="1" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const msgs = attributionMessages(aggregateReports([parseCobertura(a), parseCobertura(b)]));
    t('attributionMessages: 複数レポートで重複排除 0 行は notice（畳めていない状態を可視化）',
      msgs.some((m) => m.level === 'notice' && /重複排除で落ちた行は 0 行/.test(m.text))
        && msgs.every((m) => m.level !== 'warn'), msgs);
    // 1 レポートでは定義上 0 行になるため出さない（恒常ノイズにしない。IADR-0118 決定 6）。
    const single = attributionMessages(aggregateReports([parseCobertura(a)]));
    t('attributionMessages: 1 レポートでは重複排除 0 行の notice を出さない',
      single.every((m) => !/重複排除で落ちた行は 0 行/.test(m.text)), single);
    // 実際に畳めていれば notice は出ない。
    const folded = attributionMessages(aggregateReports([parseCobertura(a), parseCobertura(a)]));
    t('attributionMessages: 畳めていれば重複排除の notice は出ない',
      folded.every((m) => !/重複排除で落ちた行は 0 行/.test(m.text)), folded);
  }

  {
    // 未帰属クラスは正規化できないので生パスをキーにする。件数を診断と notice へ出す。
    const xml = '<coverage><packages><package><classes>' +
      '<class name="X" filename="Foo/Bar.cs"><lines><line number="1" hits="1" /></lines></class>' +
      '<class name="Y" filename="src/platform/backend/Y.cs"><lines><line number="1" hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const agg = aggregateReports([parseCobertura(xml)]);
    t('aggregateReports: 正規化できなかった行数を診断へ出す',
      agg.diagnostics.dedup.unnormalizedLines === 1, agg.diagnostics.dedup);
    t('attributionMessages: 正規化できない行があれば notice で名指しする',
      attributionMessages(agg).some((m) => m.level === 'notice' && /正規化できなかった/.test(m.text)),
      attributionMessages(agg));
  }

  {
    // 行番号を持たない <line> は畳めないため単純和で残す（黙って落とさない）。
    const xml = '<coverage><packages><package><classes>' +
      '<class name="X" filename="src/platform/backend/X.cs"><lines><line hits="1" /></lines></class>' +
      '</classes></package></packages></coverage>';
    const agg = aggregateReports([parseCobertura(xml), parseCobertura(xml)]);
    t('aggregateReports: 行番号を持たない <line> は畳まず単純和で残す（2 部で 2 行）',
      agg.totals.lines === 2 && agg.diagnostics.dedup.unkeyedLines === 2, agg.diagnostics.dedup);
  }

  {
    // 同一レポート内に同じ (class name, filename, 行番号) が 2 度現れても畳まない。
    // これが 1 レポートのときの恒等性（＝決定 3・決定 4 の非退行）を**構成上**保証している。
    const xml = '<coverage><packages>' +
      '<package name="P1"><classes><class name="X" filename="src/platform/backend/X.cs">' +
      '<lines><line number="1" hits="1" /></lines></class></classes></package>' +
      '<package name="P2"><classes><class name="X" filename="src/platform/backend/X.cs">' +
      '<lines><line number="1" hits="0" /></lines></class></classes></package>' +
      '</packages></coverage>';
    const p = parseCobertura(xml);
    t('aggregateReports: 同一レポート内の同キー重複は畳まない（1 レポートの恒等性）',
      p.lines === 2 && aggregateReports([p]).totals.lines === 2,
      { parsed: p.lines, agg: aggregateReports([p]).totals.lines });
    t('aggregateReports: その形でもレポートを跨げば畳む（2 部でも 2 行のまま）',
      aggregateReports([p, parseCobertura(xml)]).totals.lines === 2,
      aggregateReports([p, parseCobertura(xml)]).totals);
  }

  {
    // 🔴 畳み込みが配線ごと効いていること（no-op 化の検出）。素朴な合算（mergeTotals）と
    //    畳み込み後（aggregateReports の入口）を**同じ入力**で比べ、**差が出ること自体**を固定する。
    const xml = '<coverage><packages><package><classes>' +
      `<class name="Shared.X" filename="${SHARED_FILE}"><lines>` +
      '<line number="1" hits="1" /><line number="2" hits="0" />' +
      '</lines></class></classes></package></packages></coverage>';
    const parsed = [parseCobertura(xml), parseCobertura(xml)];
    const naive = mergeTotals(parsed);
    const agg = aggregateReports(parsed);
    t('foldLineEntries: 素朴な合算と畳み込み後が同じ入力で異なる（no-op 化の検出）',
      naive.lines === 4 && agg.totals.lines === 2 && naive.lines !== agg.totals.lines,
      { naive: naive.lines, folded: agg.totals.lines });
    t('foldLineEntries: 診断の droppedLines は素朴な合算との差と一致する',
      agg.diagnostics.dedup.droppedLines === naive.lines - agg.totals.lines,
      agg.diagnostics.dedup);
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
  for (const d of formatDiagnostics(agg, floor)) console.log(`[check-coverage-floor] ${d}`);
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
        + `\`<class filename>\` の帰属で除外した（#468 / IADR-0123）。`,
      '',
      `生成コード（\`Migrations/\` 配下・\`*ModelSnapshot.cs\`）は `
        + `**${agg.generated.lines} 行**（${agg.generated.classCount} クラス・被覆 ${agg.generated.covered}）`
        + `を除外した（#571 / IADR-0138）。生成コードを戻すと ${formatTotals(agg.beforeGeneratedExclusion)}。`,
      '',
      `いずれの除外もかける前は ${formatTotals(before)}。`,
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
  dedupFileKey,
  isGeneratedFilename,
  generatedKindOf,
  parseCobertura,
  mergeTotals,
  foldLineEntries,
  aggregateReports,
  attributionMessages,
  formatDiagnostics,
  rate,
  compareToFloor,
};
