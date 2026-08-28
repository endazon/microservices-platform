#!/usr/bin/env node
'use strict';
/*
 * check-unit-dependencies.js
 * ユニット依存方向の機械検査（FR-14, IADR-0027 / IADR-0056 / IADR-0057 / IADR-0117, Issue #231）。
 * src/README.md「依存規則」を CI で機械強制する。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 検査するルール（backend）:
 *   1) ユニット外参照: 各 .csproj の ProjectReference を解決し、参照元/先ユニット（src/<unit>/ の
 *      第 1 セグメント）が異なる場合、
 *        - 参照先が platform/backend/Shared/ の 3 プロジェクト（Platform.Shared.Contracts /
 *          Platform.Shared.Infrastructure / Platform.Shared.Kernel）なら許可。2 → 3 の改定は
 *          IADR-0117（配置を確定）。**Platform.Shared.Kernel は 2026-08-21 に実体を持った**（#455 / IADR-0229）、
 *        - 参照元が Tests プロジェクトで参照先が platform サービス（統合テスト例外）なら許可、
 *        - 参照元が BFF 合成点（platform/backend/Bff/Platform.Bff/）で参照先が可変ユニットの BFF
 *          エンドポイント（<unit>/backend/Bff/）なら許可（例外3・IADR-0063）、
 *        - それ以外（特に platform → 可変ユニット）は違反。
 *   2) Foundation → Composable: Foundation/ 配下 .cs に `using <ns>.Composable(.|;)` が現れたら違反。
 *   3) サービス内レイヤ依存方向（NFR, IADR-0282 決定 2。旧規範は IADR-0280 決定 3
 *      〔Superseded by IADR-0282〕）。移送はサービス単位で進み、旧樹形（層プロジェクト）と
 *      新樹形（単一プロジェクト＋VSA フォルダ）がしばらく混在するため、**新旧の判定を併走**
 *      させる（対象が在るほうが判定する）:
 *      ③【新判定（IADR-0282 決定 1・2）】単一プロジェクト配下
 *        src/<unit>/backend/Services/<Svc>/{Domain,Features,Infrastructure,Common}/ 配下の .cs を
 *        **ファイルパスが属する層フォルダで分類**し、**自サービスの名前空間**（<Svc>.Domain /
 *        <Svc>.Features… 等）宛の using の宛先で違反を見る:
 *          - Domain は Features / Infrastructure / Common.Behaviors を using してはならない
 *          - Infrastructure は Features を using してはならない
 *          - Features は Domain / Infrastructure / Common を使ってよい（Common にも制約は課さない）
 *        他サービス・Shared・外部ライブラリ宛の using は対象外（ユニット外参照は規則 1 の、
 *        外部ライブラリは check-backend-libraries.js の領分）。Tests/ 配下は対象外。
 *      ①【旧判定・経過措置】同一サービス内の 8 要素プロジェクト（<Svc>.{Api|Worker|Application|
 *        Domain|Infrastructure|Contracts|SharedKernel}）間の ProjectReference は宣言方向
 *        （Domain ← Application ← Infrastructure ← Api/Worker。Contracts / SharedKernel は
 *        参照される側にしか立てない葉）に限る。
 *      ②【旧判定・経過措置】`*.Domain` プロジェクト配下の .cs に `using Microsoft.EntityFrameworkCore` /
 *        `using MassTransit` / `using Wolverine` / `using Refit`（下位名前空間・static・エイリアス
 *        束縛を含む）が現れたら違反（「Domain 層は SharedKernel を除き外部ライブラリへ依存しない」
 *        —— csproj の PackageReference ゼロは check-backend-libraries.js 規則 2 が見るが、
 *        推移参照で届く型の using はそちらでは止まらないため、ソース面をここで塞ぐ）。
 *      submodule ユニット（scripts/lib/excluded-units.js）は規則 3 の対象外（他プロジェクトの
 *      コードを自リポジトリの規約で検査しない）。
 *
 *      ★ 0 件走査の門（#664 / IADR-0130）の置き場（設計判断）: 層プロジェクトの csproj が
 *        1 つも無くなると旧判定①②は自然に 0 件走査になるが、それは移送完了の**正常な帰結**で
 *        ある。よって**旧判定側には門を置かない**（対象 0 件でも fail しない）。いっぽう
 *        新判定③の対象 .cs が 0 件になるのは、FeedbackService が新樹形へ移送済みの現在以降は
 *        「走査の壊れ（パスずれ）か移送の巻き戻り」でしかない。よって**門は新判定側に置き**、
 *        main() が fail-closed で止める（0 件走査で緑を返さない）。
 *
 * フロントの合成点制約（合成点以外の @knowledge / @features import 禁止）は ESLint
 * （src/eslint.config.js の no-restricted-imports）で検査する（lint ジョブ）。本スクリプト対象外。
 *
 * 使い方:
 *   node scripts/check-unit-dependencies.js            # src/ を走査。違反があれば終了コード 1。
 *   node scripts/check-unit-dependencies.js --self-test # 検査ロジック自体の自己試験。
 */
const fs = require('fs');
const path = require('path');
const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const SRC_DIR = 'src';
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

// 規則 3 の対象外（submodule ユニット。単一情報源は .gitmodules）。
// **遅延評価にする**: excluded-units.js は .gitmodules を読めないと fail-closed で throw する。
// 走査対象が 1 件も無いときは 0 件走査の門（main の fail-closed）が先に語るべきなので、
// 実際に規則 3 の判定が要るまで .gitmodules を読まない。
let _isExcludedPath = null;
function isExcludedPath(relPath) {
  if (_isExcludedPath === null) {
    _isExcludedPath = makeIsExcludedPath(excludedUnits({ root: REPO_ROOT }));
  }
  return _isExcludedPath(relPath);
}

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

// posix 区切りへ正規化する。
function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

// リポジトリ相対パス（posix）から所属ユニット（src/<unit>/ の <unit>）を返す。src 外は null。
function pathUnit(relPath) {
  const m = toPosix(relPath).match(/^src\/([^/]+)\//);
  return m ? m[1] : null;
}

// 参照先が platform/backend/Shared/ 配下（ユニット外から参照を許可する 3 プロジェクト）か。
// 判定はパス接頭辞で行うため、許可プロジェクトの増減でこの関数を変える必要はない（IADR-0117 の 2 → 3 改定も無変更で追随）。
function isSharedProject(relPath) {
  return /^src\/platform\/backend\/Shared\//.test(toPosix(relPath));
}

// csproj がテストプロジェクトか（*.Tests.csproj もしくは tests/ 配下）。統合テスト例外の判定に使う。
// 大文字小文字は問わない（Tests/ / tests/ / TESTS/ いずれも許容）。
function isTestsProject(relPath) {
  const p = toPosix(relPath);
  return /\.Tests\.csproj$/i.test(p) || /(^|\/)tests\//i.test(p);
}

// 参照元が BFF 合成点（platform の BFF アプリ）か。例外3（BFF 合成点例外）の判定に使う。
function isBffCompositionHost(relPath) {
  return /^src\/platform\/backend\/Bff\/Platform\.Bff\//.test(toPosix(relPath));
}

// 参照先が可変ユニットの BFF エンドポイントプロジェクト（<unit>/backend/Bff/ 配下・platform 以外）か。
function isUnitBffEndpoints(relPath) {
  const m = toPosix(relPath).match(/^src\/([^/]+)\/backend\/Bff\//);
  return !!m && m[1] !== 'platform';
}

// ProjectReference 1 件を分類する。{ ok: boolean, reason: string }。
// from / to はいずれもリポジトリ相対（posix）の csproj パス。
function classifyProjectReference(fromCsproj, toCsproj) {
  const fromUnit = pathUnit(fromCsproj);
  const toUnit = pathUnit(toCsproj);
  // src 外（想定外）の参照は本検査の対象外とする。
  if (!fromUnit || !toUnit) return { ok: true, reason: 'out-of-src' };
  // 同一ユニット内の参照は常に許可。
  if (fromUnit === toUnit) return { ok: true, reason: 'intra-unit' };
  // 以降はユニットをまたぐ参照。
  if (toUnit === 'platform' && isSharedProject(toCsproj)) {
    return { ok: true, reason: 'allowed-shared' };
  }
  // 統合テスト例外: Tests プロジェクトが platform のサービスを検証対象として参照する場合のみ許可。
  if (toUnit === 'platform' && isTestsProject(fromCsproj)) {
    return { ok: true, reason: 'integration-test-exception' };
  }
  // 例外3: BFF 合成点（platform の BFF アプリ）→ 可変ユニットの BFF エンドポイント（<unit>/backend/Bff/）は許可。
  // フロントの合成点（features/index.ts, 例外2）の backend 版（IADR-0063）。可変ユニットは自分の BFF
  // エンドポイントを合成点経由で BFF へ組み込む。合成点以外の platform → 可変ユニット参照は引き続き禁止。
  if (fromUnit === 'platform' && isBffCompositionHost(fromCsproj) && isUnitBffEndpoints(toCsproj)) {
    return { ok: true, reason: 'bff-composition-exception' };
  }
  if (fromUnit === 'platform') {
    return { ok: false, reason: `platform → 可変ユニット(${toUnit}) の参照は禁止（一方向依存）` };
  }
  return {
    ok: false,
    reason: `ユニット外参照は platform/backend/Shared/ の 3 プロジェクトのみ許可（${fromUnit} → ${toUnit}）`,
  };
}

// Foundation/ 配下 .cs の Composable への using を検出する。違反 using 行の配列を返す。
function scanFoundationComposable(relPath, content) {
  if (!/(^|\/)Foundation\//.test(toPosix(relPath))) return [];
  const violations = [];
  // `using X.Composable...;` / `using static X.Composable...;` / `using Alias = X.Composable...;`
  // （global 前置・static 修飾・エイリアス束縛のいずれの形でも Composable 名前空間参照を検出する）。
  const re =
    /^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?([A-Za-z_][\w.]*\.Composable(?:\.[\w.]+)?)\s*;/gm;
  let m;
  while ((m = re.exec(content))) violations.push(m[0].trim());
  return violations;
}

// --- 規則 3-①②（旧判定・経過措置）: 8 要素プロジェクトのレイヤ依存方向（NFR, IADR-0280 決定 3〔Superseded by IADR-0282〕） -------

// 8 要素の要素名。単一情報源は IADR-0280 決定 3（Superseded by IADR-0282。#1021）。
// 🔴 IADR-0282 決定 2 の名前空間走査版は下の「規則 3-③（新判定）」として実装済み。本旧判定①②は
//    層プロジェクトが実在する間の経過措置として残す（Tests は 1 プロジェクトで参照制約の対象外）。
//    層プロジェクトが尽きて 0 件走査になっても fail しない——冒頭「0 件走査の門の置き場」参照。
const EIGHT_ELEMENTS = 'Api|Worker|Application|Domain|Infrastructure|Contracts|SharedKernel';

// レイヤの序数。大きい側から小さい側への参照のみ許す。Contracts / SharedKernel は序列に
// 入らない葉（参照される側にしか立てない）。
const EIGHT_ELEMENT_RANK = { Domain: 0, Application: 1, Infrastructure: 2, Api: 3, Worker: 3 };

// リポジトリ相対パス（posix）の csproj が 8 要素プロジェクトなら { service, element } を返す。
// 形は src/<unit>/backend/Services/<Svc>/src/<Svc>.<要素>/<Svc>.<要素>.csproj に限る
// （Shared/ 配下の Platform.Shared.Contracts 等・tests/ 側・BFF は対象外）。
function parseEightElementProject(relPath) {
  const re = new RegExp(
    `^src/[^/]+/backend/Services/([^/]+)/src/\\1\\.(${EIGHT_ELEMENTS})/\\1\\.\\2\\.csproj$`,
  );
  const m = toPosix(relPath).match(re);
  return m ? { service: m[1], element: m[2] } : null;
}

// 同一サービス内の 8 要素プロジェクト間 ProjectReference を分類する。{ ok, reason }。
// どちらかが 8 要素でない・サービスが異なる参照は本規則の対象外（規則 1 の領分）。
function classifyLayerReference(fromCsproj, toCsproj) {
  const from = parseEightElementProject(fromCsproj);
  const to = parseEightElementProject(toCsproj);
  if (!from || !to) return { ok: true, reason: 'not-eight-element' };
  if (from.service !== to.service) return { ok: true, reason: 'cross-service' };
  if (from.element === 'Contracts' || from.element === 'SharedKernel') {
    return {
      ok: false,
      reason: `${from.element} は葉であり、同一サービスの他要素を参照できない（IADR-0280 決定 3〔Superseded by IADR-0282〕）`,
    };
  }
  if (to.element === 'Contracts' || to.element === 'SharedKernel') {
    return { ok: true, reason: 'leaf-reference' };
  }
  if (EIGHT_ELEMENT_RANK[from.element] > EIGHT_ELEMENT_RANK[to.element]) {
    return { ok: true, reason: 'downward' };
  }
  return {
    ok: false,
    reason:
      `レイヤ依存方向の違反: ${from.element} → ${to.element} は宣言方向` +
      '（Domain ← Application ← Infrastructure ← Api/Worker）に反する（IADR-0280 決定 3〔Superseded by IADR-0282〕）',
  };
}

// Domain プロジェクトの .cs に混入してはならない外部フレームワークの名前空間
// （「Domain 層は SharedKernel を除き外部ライブラリへ依存しない」）。
const DOMAIN_FORBIDDEN_USINGS = ['Microsoft.EntityFrameworkCore', 'MassTransit', 'Wolverine', 'Refit'];

// リポジトリ相対パス（posix）が *.Domain プロジェクト配下の .cs か。
function isDomainProjectPath(relPath) {
  return /(^|\/)[^/]+\.Domain\//.test(toPosix(relPath));
}

// *.Domain 配下 .cs の禁止 using を検出する。違反 using 行の配列を返す。
// scanFoundationComposable と同じく global 前置・static 修飾・エイリアス束縛のいずれの形も見る。
function scanDomainForbiddenUsings(relPath, content) {
  if (!isDomainProjectPath(relPath)) return [];
  const violations = [];
  const re =
    /^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?([A-Za-z_][\w.]*)\s*;/gm;
  let m;
  while ((m = re.exec(content))) {
    const ns = m[1];
    if (DOMAIN_FORBIDDEN_USINGS.some((b) => ns === b || ns.startsWith(`${b}.`))) {
      violations.push(m[0].trim());
    }
  }
  return violations;
}

// --- 規則 3-③（新判定）: 単一プロジェクト＋VSA フォルダの名前空間参照方向（NFR, IADR-0282 決定 2） ---

// 層フォルダ名（IADR-0282 決定 1 の標準樹形）。Tests/ は層でないため列挙しない（構造的に対象外）。
const VSA_LAYERS = 'Domain|Features|Infrastructure|Common';

// 層ごとの禁止宛先（自サービスのルート名前空間 <Svc>. を除いた接頭辞）。
// Features / Common には制約を課さない（Features は Domain / Infrastructure / Common を使ってよい）。
const VSA_FORBIDDEN_TARGETS = {
  Domain: ['Features', 'Infrastructure', 'Common.Behaviors'],
  Infrastructure: ['Features'],
};

// VSA 層フォルダ配下の .cs のパス形。旧樹形（Services/<Svc>/src|tests/ 配下）・Tests/・Worker/・
// <Svc> 直下の Program.cs 等は、<Svc> 直下の最初のセグメントが層フォルダ名でないため構造的に
// 対象外になる。
const VSA_LAYER_PATH_RE = new RegExp(
  `^src/[^/]+/backend/Services/([^/]+)/(${VSA_LAYERS})/.+\\.cs$`,
);

// リポジトリ相対パス（posix）の .cs が VSA 層フォルダ配下なら { service, layer } を返す。
// ルート名前空間は <Svc>（IADR-0282 決定 3）なので、パスから取ったサービス名をそのまま
// 名前空間の照合に使える。
function parseVsaLayerPath(relPath) {
  const m = toPosix(relPath).match(VSA_LAYER_PATH_RE);
  return m ? { service: m[1], layer: m[2] } : null;
}

// VSA 層フォルダ配下 .cs の、自サービス名前空間宛 using の方向違反を検出する。
// 違反 { line, layer, target } の配列を返す。scanDomainForbiddenUsings と同じく
// global 前置・static 修飾・エイリアス束縛のいずれの形も見る。判定対象は**自サービスの
// 名前空間だけ**であり、他サービス・Shared・外部ライブラリ宛の using は対象外
// （ユニット外参照は規則 1 の、外部ライブラリは check-backend-libraries.js の領分）。
function scanVsaLayerUsings(relPath, content) {
  const info = parseVsaLayerPath(relPath);
  if (!info) return [];
  const forbidden = VSA_FORBIDDEN_TARGETS[info.layer] || [];
  if (forbidden.length === 0) return [];
  const violations = [];
  const re =
    /^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?([A-Za-z_][\w.]*)\s*;/gm;
  let m;
  while ((m = re.exec(content))) {
    const ns = m[1];
    // 自サービスの名前空間（`<Svc>.` 始まり）以外は対象外。前方一致の取り違え（<Svc>Foo.* 等）を
    // 避けるため、区切りの `.` まで含めて照合する。
    if (!ns.startsWith(`${info.service}.`)) continue;
    const rest = ns.slice(info.service.length + 1);
    const hit = forbidden.find((t) => rest === t || rest.startsWith(`${t}.`));
    if (hit) violations.push({ line: m[0].trim(), layer: info.layer, target: hit });
  }
  return violations;
}

// --- ファイル走査 -------------------------------------------------------------

function walk(dir, pred, out) {
  let ents;
  try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch (e) { return out; }
  for (const ent of ents) {
    if (ent.isDirectory()) {
      if (SKIP_DIRS.has(ent.name)) continue;
      walk(path.join(dir, ent.name), pred, out);
    } else if (ent.isFile() && pred(ent.name)) {
      out.push(path.join(dir, ent.name));
    }
  }
  return out;
}

function repoRel(abs) {
  return toPosix(path.relative(REPO_ROOT, abs));
}

// csproj の ProjectReference Include を解決してリポジトリ相対 posix パスの配列で返す。
function projectReferencesOf(csprojAbs) {
  let content = '';
  try { content = fs.readFileSync(csprojAbs, 'utf8'); } catch (e) { return []; }
  const dir = path.dirname(csprojAbs);
  const out = [];
  const re = /ProjectReference\s+Include="([^"]+)"/g;
  let m;
  while ((m = re.exec(content))) {
    const resolved = path.resolve(dir, m[1].replace(/\\/g, path.sep));
    out.push(repoRel(resolved));
  }
  return out;
}

function checkTree() {
  const violations = [];
  // #664: 走査件数を持ち帰り、呼び出し側の fail-closed の門に使う（0 件走査で緑を返さない）。
  const scanned = { csprojs: 0, csFiles: 0, vsaLayerCs: 0 };
  // 1) ProjectReference の方向検査。
  const csprojs = walk(path.join(REPO_ROOT, SRC_DIR), (n) => n.endsWith('.csproj'), []);
  scanned.csprojs = csprojs.length;
  for (const abs of csprojs) {
    const from = repoRel(abs);
    for (const to of projectReferencesOf(abs)) {
      const r = classifyProjectReference(from, to);
      if (!r.ok) violations.push({ kind: 'project-reference', from, to, reason: r.reason });
      // 3-①) 8 要素プロジェクト間のレイヤ依存方向（submodule ユニットは対象外）。
      if (isExcludedPath(from)) continue;
      const layer = classifyLayerReference(from, to);
      if (!layer.ok) violations.push({ kind: 'layer-direction', from, to, reason: layer.reason });
    }
  }
  // 2) Foundation → Composable / 3-②) Domain の禁止 using / 3-③) VSA 層の名前空間方向の検査。
  const csFiles = walk(path.join(REPO_ROOT, SRC_DIR), (n) => n.endsWith('.cs'), []);
  scanned.csFiles = csFiles.length;
  for (const abs of csFiles) {
    const rel = repoRel(abs);
    const isFoundation = /(^|\/)Foundation\//.test(rel);
    const excluded = isExcludedPath(rel);
    const isDomain = !excluded && isDomainProjectPath(rel);
    // 3-③) 新判定の対象（VSA 層フォルダ配下。submodule ユニットは対象外）。走査件数は
    // main() の 0 件走査の門（新判定側）に使うため、違反の有無と独立に数える。
    const vsa = excluded ? null : parseVsaLayerPath(rel);
    if (vsa) scanned.vsaLayerCs += 1;
    if (!isFoundation && !isDomain && !vsa) continue;
    let content = '';
    try { content = fs.readFileSync(abs, 'utf8'); } catch (e) { continue; }
    if (isFoundation) {
      for (const line of scanFoundationComposable(rel, content)) {
        violations.push({ kind: 'foundation-composable', from: rel, to: '(Composable)', reason: line });
      }
    }
    if (isDomain) {
      for (const line of scanDomainForbiddenUsings(rel, content)) {
        violations.push({ kind: 'domain-forbidden-using', from: rel, to: '(外部フレームワーク)', reason: line });
      }
    }
    if (vsa) {
      for (const v of scanVsaLayerUsings(rel, content)) {
        violations.push({
          kind: 'vsa-layer-using',
          from: rel,
          to: `(${vsa.service}.${v.target})`,
          reason: `${v.layer} は自サービスの ${v.target} を using できない（IADR-0282 決定 2）: ${v.line}`,
        });
      }
    }
  }
  // #664: 走査件数を併せて返す。**配列へプロパティを生やさない**（戻り値の型が読みづらくなる）。
  return { violations, scanned };
}

// --- 自己試験（--self-test） --------------------------------------------------

function selfTest() {
  const cases = [];
  const expectOk = (name, actual) => cases.push({ name, pass: actual.ok === true, actual });
  const expectViolation = (name, actual) => cases.push({ name, pass: actual.ok === false, actual });

  // 許可: 可変ユニット → platform Shared。
  expectOk('knowledge → platform Shared.Contracts は許可', classifyProjectReference(
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj',
    'src/platform/backend/Shared/Platform.Shared.Contracts/Platform.Shared.Contracts.csproj'));
  // 許可: 同一ユニット内（テスト → 実装）。
  expectOk('同一ユニット内参照は許可', classifyProjectReference(
    'src/knowledge/backend/Services/DocumentService/tests/DocumentService.Api.Tests/DocumentService.Api.Tests.csproj',
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj'));
  // 許可: 統合テスト例外（可変ユニットの Tests → platform サービス）。
  expectOk('統合テスト → platform サービスは許可', classifyProjectReference(
    'src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj',
    'src/platform/backend/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj'));
  // 違反: platform → 可変ユニット。
  expectViolation('platform → knowledge は違反', classifyProjectReference(
    'src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj',
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj'));
  // 違反: 可変ユニット（非テスト） → platform の非 Shared サービス。
  expectViolation('knowledge サービス → platform サービス（非 Shared）は違反', classifyProjectReference(
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj',
    'src/platform/backend/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj'));
  // 違反: 可変ユニット同士のコード参照。
  expectViolation('可変ユニット間の参照は違反', classifyProjectReference(
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj',
    'src/analytics/backend/Services/ReportService/src/ReportService.Api/ReportService.Api.csproj'));
  // 許可（例外3）: BFF 合成点（Platform.Bff）→ 可変ユニットの BFF エンドポイント。
  expectOk('BFF 合成点 → knowledge の BFF エンドポイントは許可（例外3）', classifyProjectReference(
    'src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj',
    'src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj'));
  // 違反: 例外3 は BFF 合成点かつ相手が <unit>/backend/Bff/ のときのみ。platform サービス → 可変ユニット BFF は不可。
  expectViolation('platform サービス → knowledge の BFF エンドポイントは違反（例外3 対象外）', classifyProjectReference(
    'src/platform/backend/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj',
    'src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj'));
  // 違反: BFF 合成点でも相手が <unit>/backend/Bff/ 以外（サービス等）なら不可。
  expectViolation('BFF 合成点 → knowledge のサービスは違反（Bff 配下でない）', classifyProjectReference(
    'src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj',
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj'));
  // Foundation → Composable の using 検出。
  cases.push({
    name: 'Foundation 配下の using .Composable を検出',
    pass: scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/Endpoints/X.cs',
      'namespace X;\nusing DocumentService.Api.Composable.Steps;\n').length === 1,
  });
  cases.push({
    name: 'Foundation 配下でも Composable を含まない using は無視',
    pass: scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/Endpoints/X.cs',
      'using DocumentService.Api.Foundation.Domain;\n').length === 0,
  });
  cases.push({
    name: 'Foundation 外の using .Composable は無視（合成点等）',
    pass: scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Program.cs',
      'using DocumentService.Api.Composable.Steps;\n').length === 0,
  });
  cases.push({
    name: 'Foundation 配下のエイリアス using .Composable も検出',
    pass: scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/X.cs',
      'using Step = DocumentService.Api.Composable.Steps.SomeStep;\n').length === 1,
  });

  // 規則 3-①: 8 要素プロジェクト間のレイヤ依存方向（IADR-0280 決定 3〔Superseded by IADR-0282・経過措置〕）。
  const P = (svc, elem) =>
    `src/knowledge/backend/Services/${svc}/src/${svc}.${elem}/${svc}.${elem}.csproj`;
  cases.push({
    name: 'parseEightElementProject は 8 要素の csproj を解析する',
    pass:
      JSON.stringify(parseEightElementProject(P('FeedbackService', 'Domain'))) ===
        JSON.stringify({ service: 'FeedbackService', element: 'Domain' }) &&
      parseEightElementProject(
        'src/platform/backend/Shared/Platform.Shared.Contracts/Platform.Shared.Contracts.csproj') === null &&
      parseEightElementProject(
        'src/knowledge/backend/Services/FeedbackService/tests/FeedbackService.Api.Tests/FeedbackService.Api.Tests.csproj') === null,
  });
  expectOk('Application → Domain は許可（下向き）', classifyLayerReference(
    P('FeedbackService', 'Application'), P('FeedbackService', 'Domain')));
  expectOk('Infrastructure → Application は許可', classifyLayerReference(
    P('FeedbackService', 'Infrastructure'), P('FeedbackService', 'Application')));
  expectOk('Api → Infrastructure / Worker → Domain は許可', classifyLayerReference(
    P('ConversionService', 'Worker'), P('ConversionService', 'Domain')));
  expectOk('Api → Contracts は許可（葉への参照）', classifyLayerReference(
    P('FeedbackService', 'Api'), P('FeedbackService', 'Contracts')));
  expectViolation('Domain → Application は違反（上向き）', classifyLayerReference(
    P('FeedbackService', 'Domain'), P('FeedbackService', 'Application')));
  expectViolation('Application → Infrastructure は違反（上向き）', classifyLayerReference(
    P('FeedbackService', 'Application'), P('FeedbackService', 'Infrastructure')));
  expectViolation('Infrastructure → Api は違反（上向き）', classifyLayerReference(
    P('FeedbackService', 'Infrastructure'), P('FeedbackService', 'Api')));
  expectViolation('Contracts → Domain は違反（葉からの参照）', classifyLayerReference(
    P('FeedbackService', 'Contracts'), P('FeedbackService', 'Domain')));
  expectViolation('SharedKernel → Domain は違反（葉からの参照）', classifyLayerReference(
    P('FeedbackService', 'SharedKernel'), P('FeedbackService', 'Domain')));
  expectOk('サービスが異なる 8 要素間は本規則の対象外（規則 1 の領分）', classifyLayerReference(
    P('FeedbackService', 'Api'), P('DocumentService', 'Domain')));
  expectOk('Domain → Platform.Shared.Kernel は本規則の対象外（8 要素でない）', classifyLayerReference(
    P('FeedbackService', 'Domain'),
    'src/platform/backend/Shared/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj'));

  // 規則 3-②: Domain プロジェクト配下 .cs の禁止 using。
  const DOMAIN_CS = 'src/knowledge/backend/Services/FeedbackService/src/FeedbackService.Domain/AnswerFeedback.cs';
  cases.push({
    name: 'Domain 配下の using Microsoft.EntityFrameworkCore を検出',
    pass: scanDomainForbiddenUsings(DOMAIN_CS, 'using Microsoft.EntityFrameworkCore;\n').length === 1,
  });
  cases.push({
    name: 'Domain 配下の下位名前空間・static・エイリアス束縛も検出',
    pass:
      scanDomainForbiddenUsings(DOMAIN_CS, 'using Wolverine.Attributes;\n').length === 1 &&
      scanDomainForbiddenUsings(DOMAIN_CS, 'using static Microsoft.EntityFrameworkCore.EF;\n').length === 1 &&
      scanDomainForbiddenUsings(DOMAIN_CS, 'using Db = Microsoft.EntityFrameworkCore.DbContext;\n').length === 1 &&
      scanDomainForbiddenUsings(DOMAIN_CS, 'global using MassTransit;\n').length === 1 &&
      scanDomainForbiddenUsings(DOMAIN_CS, 'using Refit;\n').length === 1,
  });
  cases.push({
    name: 'Domain 配下でも許可された using は無視（前方一致の取り違えも無い）',
    pass:
      scanDomainForbiddenUsings(DOMAIN_CS, 'using Platform.Shared.Kernel;\n').length === 0 &&
      scanDomainForbiddenUsings(DOMAIN_CS, 'using Microsoft.Extensions.Logging;\n').length === 0 &&
      scanDomainForbiddenUsings(DOMAIN_CS, 'using WolverineFoo.Bar;\n').length === 0,
  });
  cases.push({
    name: 'Domain 外の .cs は対象外',
    pass: scanDomainForbiddenUsings(
      'src/knowledge/backend/Services/FeedbackService/src/FeedbackService.Api/Program.cs',
      'using Microsoft.EntityFrameworkCore;\n').length === 0,
  });

  // 規則 3-③（新判定）: VSA 層フォルダの名前空間参照方向（IADR-0282 決定 2）。
  const V = (sub) => `src/knowledge/backend/Services/FeedbackService/${sub}`;
  cases.push({
    name: 'parseVsaLayerPath は VSA 層フォルダ配下の .cs を層に分類する',
    pass:
      JSON.stringify(parseVsaLayerPath(V('Domain/AnswerFeedback.cs'))) ===
        JSON.stringify({ service: 'FeedbackService', layer: 'Domain' }) &&
      JSON.stringify(parseVsaLayerPath(V('Features/Feedback/FeedbackEndpoints.cs'))) ===
        JSON.stringify({ service: 'FeedbackService', layer: 'Features' }) &&
      JSON.stringify(parseVsaLayerPath(V('Infrastructure/Persistence/Migrations/X.cs'))) ===
        JSON.stringify({ service: 'FeedbackService', layer: 'Infrastructure' }) &&
      JSON.stringify(parseVsaLayerPath(V('Common/Behaviors/LoggingBehavior.cs'))) ===
        JSON.stringify({ service: 'FeedbackService', layer: 'Common' }),
  });
  cases.push({
    name: 'parseVsaLayerPath は Tests/・旧樹形・<Svc> 直下・.cs 以外・Services 外を対象にしない',
    pass:
      parseVsaLayerPath(V('Tests/FeedbackEndpointTests.cs')) === null &&
      parseVsaLayerPath(V('src/FeedbackService.Domain/AnswerFeedback.cs')) === null &&
      parseVsaLayerPath(V('Program.cs')) === null &&
      parseVsaLayerPath(V('Domain/README.md')) === null &&
      parseVsaLayerPath('src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Domain/X.cs') === null,
  });
  const VSA_DOMAIN_CS = V('Domain/AnswerFeedback.cs');
  const VSA_INFRA_CS = V('Infrastructure/Persistence/FeedbackDbContext.cs');
  const VSA_FEATURES_CS = V('Features/Feedback/FeedbackEndpoints.cs');
  cases.push({
    name: '規則 3-③: Domain → Features / Infrastructure / Common.Behaviors の using は違反',
    pass:
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'using FeedbackService.Features.Feedback;\n').length === 1 &&
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'using FeedbackService.Infrastructure.Persistence;\n').length === 1 &&
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'using FeedbackService.Common.Behaviors;\n').length === 1,
  });
  cases.push({
    name: '規則 3-③: global 前置・static 修飾・エイリアス束縛の形でも検出する',
    pass:
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'global using FeedbackService.Infrastructure;\n').length === 1 &&
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'using static FeedbackService.Features.Feedback.FeedbackEndpoints;\n')
        .length === 1 &&
      scanVsaLayerUsings(VSA_INFRA_CS, 'using Ep = FeedbackService.Features.Feedback.FeedbackEndpoints;\n')
        .length === 1,
  });
  cases.push({
    name: '規則 3-③: Infrastructure → Features は違反 / Infrastructure → Domain・自層は許可',
    pass:
      scanVsaLayerUsings(VSA_INFRA_CS, 'using FeedbackService.Features.Feedback;\n').length === 1 &&
      scanVsaLayerUsings(VSA_INFRA_CS, 'using FeedbackService.Domain;\n').length === 0 &&
      scanVsaLayerUsings(VSA_INFRA_CS, 'using FeedbackService.Infrastructure.Persistence;\n').length === 0,
  });
  cases.push({
    name: '規則 3-③: Features は Domain / Infrastructure / Common を使ってよい（Common も制約なし）',
    pass:
      scanVsaLayerUsings(
        VSA_FEATURES_CS,
        'using FeedbackService.Domain;\nusing FeedbackService.Infrastructure.Persistence;\nusing FeedbackService.Common.Behaviors;\n',
      ).length === 0 &&
      scanVsaLayerUsings(V('Common/Exceptions/X.cs'), 'using FeedbackService.Domain;\n').length === 0,
  });
  cases.push({
    name: '規則 3-③: Domain でも Common.Behaviors 以外の Common（Exceptions 等）は許可',
    pass: scanVsaLayerUsings(VSA_DOMAIN_CS, 'using FeedbackService.Common.Exceptions;\n').length === 0,
  });
  cases.push({
    name: '規則 3-③: 他サービス・Shared・外部ライブラリ宛の using は対象外',
    pass:
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'using DocumentService.Features.Search;\n').length === 0 &&
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'using Platform.Shared.Kernel;\n').length === 0 &&
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'using Microsoft.EntityFrameworkCore;\n').length === 0,
  });
  cases.push({
    name: '規則 3-③: 前方一致の取り違え（<Svc>.FeaturesX / 別サービス名の接頭辞）を検出しない',
    pass:
      scanVsaLayerUsings(VSA_DOMAIN_CS, 'using FeedbackService.FeaturesX.Y;\n').length === 0 &&
      scanVsaLayerUsings(VSA_INFRA_CS, 'using FeedbackServiceX.Features.Y;\n').length === 0,
  });

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-unit-dependencies] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-unit-dependencies] 自己試験 ${cases.length} 件 OK。`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  const { violations, scanned } = checkTree();
  // #664 / IADR-0130 の作法: **0 件走査で緑を返さない**（fail-closed）。
  // 走査対象を 1 件も拾えないのは「検査しているつもりで何も見ていない」状態であり、
  // 退行を止めているという記録だけが残る（#592 の初版がこれで、変異試験で辛うじて捕まえた）。
  if (scanned.csprojs === 0 || scanned.csFiles === 0) {
    console.error(
      `[check-unit-dependencies] ${SRC_DIR}/ から .csproj / .cs を 1 件も見つけられませんでした` +
        `（csproj ${scanned.csprojs} 件 / cs ${scanned.csFiles} 件）。`,
    );
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
    return;
  }
  // 規則 3-③ の 0 件走査の門（新判定側。冒頭「0 件走査の門の置き場」の設計判断）。
  // 旧判定①②は層プロジェクトの撤去で自然に 0 件になる（移送完了の正常）ため門を置かないが、
  // VSA 層フォルダの .cs を 1 件も分類できないのは「新判定が何も見ていない」状態である。
  if (scanned.vsaLayerCs === 0) {
    console.error(
      '[check-unit-dependencies] 規則 3-③: VSA 層フォルダ' +
        '（src/<unit>/backend/Services/<Svc>/{Domain,Features,Infrastructure,Common}/）配下の' +
        ' .cs を 1 件も分類できませんでした。',
    );
    console.error('  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。');
    process.exit(1);
    return;
  }
  if (violations.length === 0) {
    // #664: **件数を出す。** 従前は件数を出しておらず、0 件走査かどうかがログから読めなかった。
    console.log(
      `[check-unit-dependencies] OK: csproj ${scanned.csprojs} 件 / .cs ${scanned.csFiles} 件` +
        `（うち VSA 層分類 ${scanned.vsaLayerCs} 件）を走査し、ユニット依存方向の違反はありません。`,
    );
    process.exit(0);
  }
  console.error(`[check-unit-dependencies] 依存方向の違反 ${violations.length} 件を検出しました:`);
  for (const v of violations) {
    if (v.kind === 'project-reference') {
      console.error(`\n  [ProjectReference] ${v.from}\n    → ${v.to}\n    ${v.reason}`);
    } else if (v.kind === 'layer-direction') {
      console.error(`\n  [レイヤ依存方向] ${v.from}\n    → ${v.to}\n    ${v.reason}`);
    } else if (v.kind === 'domain-forbidden-using') {
      console.error(`\n  [Domain の禁止 using] ${v.from}\n    ${v.reason}`);
    } else if (v.kind === 'vsa-layer-using') {
      console.error(`\n  [VSA 層方向] ${v.from}\n    → ${v.to}\n    ${v.reason}`);
    } else {
      console.error(`\n  [Foundation→Composable] ${v.from}\n    ${v.reason}`);
    }
  }
  console.error('\n依存規則は src/README.md「依存規則」/ IADR-0027 / IADR-0056 / IADR-0282 / IADR-0280（Superseded by IADR-0282）を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  pathUnit,
  isSharedProject,
  isTestsProject,
  isBffCompositionHost,
  isUnitBffEndpoints,
  classifyProjectReference,
  scanFoundationComposable,
  parseEightElementProject,
  classifyLayerReference,
  isDomainProjectPath,
  scanDomainForbiddenUsings,
  parseVsaLayerPath,
  scanVsaLayerUsings,
  projectReferencesOf,
  checkTree,
};
