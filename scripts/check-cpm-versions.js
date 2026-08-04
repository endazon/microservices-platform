#!/usr/bin/env node
'use strict';
/*
 * check-cpm-versions.js
 * CPM（Central Package Management）のバージョン直書き禁止の機械強制（NFR, Issue #467）。
 * 外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 検査するルール（CLAUDE.md「技術スタック別ルール / C# / .NET」）:
 *   「バージョンは src/Directory.Packages.props に集約し、.csproj の PackageReference には
 *     バージョンを書かない」
 *
 *   - `<PackageReference ... Version="..." />`（属性形）           → **違反 / fail**
 *   - `<PackageReference ...><Version>...</Version></...>`（子要素形）→ **違反 / fail**
 *   - `<PackageReference ... VersionOverride="..." />`             → **許可 / warn**
 *
 * check-backend-libraries.js（#455 / #471）とは**別の関心**である。あちらは「どのライブラリを
 * 使うか」、本検査は「バージョンを**どこに**書くか」を見る。したがって別スクリプトにする（#467）。
 *
 * ratchet / baseline を持たないのは、着手時点の違反が **0 件**であることを実測したためである
 * （src 30 + templates 7 = 37 プロジェクト / 195 参照。測定条件は作業仕様書に記載）。
 * 既知違反が無いのに baseline 機構を置くと、機構そのものが「違反を baseline へ足せば通る」という
 * 抜け道になる。よって最初から fail で強制する。
 *
 * VersionOverride を許可する理由:
 *   CPM が公式に用意した回避口であり、禁止すると逃げ場を失った実装者が
 *   ManagePackageVersionsCentrally を切るなど**より悪い抜け道**へ向かう。一方で無言で許すと
 *   「中央定義があるのに実際には別の版で解決される」プロジェクトが静かに増えるため、
 *   乱用の検出として実行サマリ・アノテーションへ可視化する（終了コードは変えない）。
 *
 * 使い方:
 *   node scripts/check-cpm-versions.js             # src/ と templates/ を走査。違反があれば終了コード 1。
 *   node scripts/check-cpm-versions.js --self-test # 検査ロジック自体の自己試験（正例・負例）。
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { warn } = require('./lib/ci-annotate');
const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const SRC_DIR = 'src';
/*
 * 雛形も走査する。templates/ は ci.yml のビルド対象外（ビルドするのは
 * src/<unit>/backend/backend.slnx のみ）であり、雛形へ直書きが入ると**全新規サービスへ伝播**する。
 * 同型の見逃しは PR #463 のレビューで実際に起きている（雛形の xUnit 版不整合）。
 */
const TEMPLATE_DIR = 'templates';
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

/**
 * 検査対象外のユニット。src/ai-stock-trading は独自の計画リポジトリと ADR を持つ**別プロジェクト**
 * （submodule）であり、本リポジトリの CLAUDE.md の規約を適用するのは誤りで、submodule のため
 * 是正もできない。値は .gitmodules から導出する（ハードコードしない。IADR-0120 / #473）。
 */
const EXCLUDED_UNITS = excludedUnits({ root: REPO_ROOT });

/** リポジトリ相対パスが検査対象外ユニット配下か。 */
const isExcludedPath = makeIsExcludedPath(EXCLUDED_UNITS);

// --- 純粋ロジック（scripts.repo.test.js から単体テストする） ---------------------

/** posix 区切りへ正規化する。 */
function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/**
 * 走査対象のプロジェクトファイルか。
 *
 * **.csproj（と雛形の .csproj.sample）のみ**を対象にする。.props / .targets を入れないのは、
 * そこには**正当なバージョン記述が存在する**ためである（src/Directory.Packages.props の
 * <PackageVersion Include="X" Version="Y" /> は CPM の中央定義そのもの、GlobalPackageReference も
 * 同ファイルで版を伴って書くのが正しい書き方）。#467 のスコープは .csproj であり、
 * Directory.Build.props 経由の一括注入（<PackageReference Update="X" Version="Y" />）は現状 0 件のため
 * 追わない（起こり得ないケースへの防御的実装をしない。CLAUDE.md 禁止事項）。
 * 「どのライブラリか」の側からは check-backend-libraries.js が props / targets を既に走査している（#471）。
 *
 * 末尾 .sample を許すのは、雛形が実ビルドを避けるため .sample 付きで配布されることがあるため
 * （templates/unit-template/backend/Directory.Packages.props.sample が実例）。
 */
function isScannedProjectFile(relPath) {
  return /\.csproj(\.sample)?$/i.test(toPosix(relPath));
}

/**
 * XML コメント（<!-- ... -->）を除去する。
 *
 * .csproj には「なぜこの参照があるか」を説明するコメントが実在し（実測 52 ブロック / 34 ファイル）、
 * 例示としてコメントアウトされた PackageReference を赤にすると検査が邪魔になる。
 * 位置合わせは不要（本検査は行番号を報告しない）ため、素朴に除去してよい。
 */
function stripXmlComments(content) {
  return String(content).replace(/<!--[\s\S]*?-->/g, '');
}

/**
 * 開始タグの属性部を { 名前: 値 } へトークン化する。
 *
 * 属性を**トークンとして順に消費する**のが要点である。値の中身を属性名と誤認しないため、
 * `Condition="'$(C)'=='Version=1'"` のような値があっても偽陽性を出さない。
 * XML として妥当な単一引用符形（Version='1.0'）にも対応する。
 */
function parseAttributes(attrText) {
  const out = {};
  const re = /([A-Za-z_][\w.:-]*)\s*=\s*(?:"([^"]*)"|'([^']*)')/g;
  let m;
  while ((m = re.exec(String(attrText))) !== null) {
    out[m[1]] = m[2] !== undefined ? m[2] : m[3];
  }
  return out;
}

/**
 * 本文から <PackageReference> 要素を切り出す（自己終了形と対応形の双方）。
 *
 * 要素名は `<PackageReference` の直後で境界を取るため、`<GlobalPackageReference` にも
 * `<PackageVersion` にも一致しない（CPM の中央定義を違反にしないための境界）。
 * PackageReference は入れ子にならないため、対応形は直近の </PackageReference> までを本文とする。
 *
 * 戻り値: [{ attrs: {...}, body: '子要素の生テキスト' }]
 */
function packageReferenceElements(content) {
  const text = stripXmlComments(content);
  const out = [];
  const re = /<PackageReference(?=[\s/>])([^>]*?)(\/?)>/g;
  let m;
  while ((m = re.exec(text)) !== null) {
    const attrs = parseAttributes(m[1]);
    let body = '';
    if (m[2] !== '/') {
      const close = text.indexOf('</PackageReference>', re.lastIndex);
      body = close === -1 ? text.slice(re.lastIndex) : text.slice(re.lastIndex, close);
    }
    out.push({ attrs, body });
  }
  return out;
}

/**
 * 子要素メタデータ（<Version>1.0</Version> 等）の値を返す。無ければ null。
 * MSBuild では子要素メタデータは属性形と**等価**であり、ここを見ないと検査は素通りする。
 */
function metadataOf(body, name) {
  const re = new RegExp(`<${name}\\s*>([\\s\\S]*?)</${name}\\s*>`, 'i');
  const m = re.exec(String(body || ''));
  return m ? m[1].trim() : null;
}

/**
 * PackageReference のパッケージ ID。Include → Update の順で解決する。
 * どちらも無ければ '(不明)'（報告用。判定そのものには使わない）。
 */
function packageIdOf(attrs) {
  return attrs.Include || attrs.Update || '(不明)';
}

/**
 * 1 ファイル分の検出結果を返す。
 *   violations: バージョン直書き（fail 対象）
 *   overrides : VersionOverride の使用（warn 対象・許可）
 *   references: PackageReference の総数（走査規模の実測に使う）
 *
 * Version の判定に `attrs.Version !== undefined` を使うのが要点である。空文字（Version=""）も
 * 「csproj に版の場がある」ことは同じで、意図が読めない記述を素通りさせない。
 * 条件付き ItemGroup は**解釈しない**（条件付きの直書きは「特定条件でだけ別の版になる」ため
 * むしろ危険度が高い）。プロパティ参照（Version="$(XVersion)"）も版が csproj 側で決まる点は同じ。
 */
function inlineVersionFindings(relPath, content) {
  const project = toPosix(relPath);
  const violations = [];
  const overrides = [];
  const elements = packageReferenceElements(content);
  for (const el of elements) {
    const id = packageIdOf(el.attrs);
    const attrVersion = el.attrs.Version;
    const metaVersion = metadataOf(el.body, 'Version');
    if (attrVersion !== undefined) {
      violations.push({ project, package: id, source: 'attribute', value: attrVersion });
    } else if (metaVersion !== null) {
      violations.push({ project, package: id, source: 'element', value: metaVersion });
    }
    const attrOverride = el.attrs.VersionOverride;
    const metaOverride = metadataOf(el.body, 'VersionOverride');
    if (attrOverride !== undefined) {
      overrides.push({ project, package: id, source: 'attribute', value: attrOverride });
    } else if (metaOverride !== null) {
      overrides.push({ project, package: id, source: 'element', value: metaOverride });
    }
  }
  return { violations, overrides, references: elements.length };
}

// --- ファイル走査 ---------------------------------------------------------------

/**
 * dir 配下を再帰的に走査し、条件に合うファイルの相対パスを返す。
 * root は走査の起点（既定はリポジトリルート）。自己試験が一時ツリーを走査するために外から与える。
 */
function walk(dir, predicate, acc = [], root = REPO_ROOT) {
  const abs = path.join(root, dir);
  let entries;
  try {
    entries = fs.readdirSync(abs, { withFileTypes: true });
  } catch {
    return acc;
  }
  for (const e of entries) {
    if (SKIP_DIRS.has(e.name)) continue;
    const rel = toPosix(path.join(dir, e.name));
    if (e.isDirectory()) walk(rel, predicate, acc, root);
    else if (predicate(rel)) acc.push(rel);
  }
  return acc;
}

/**
 * src/ と templates/ を走査し、違反・VersionOverride・走査規模を返す。
 * root は走査の起点（既定はリポジトリルート。自己試験が一時ツリーを与える）。
 */
function scanTree(root = REPO_ROOT) {
  const files = [
    ...walk(SRC_DIR, (p) => isScannedProjectFile(p) && !isExcludedPath(p), [], root),
    ...walk(TEMPLATE_DIR, isScannedProjectFile, [], root),
  ].sort();
  const violations = [];
  const overrides = [];
  let references = 0;
  for (const rel of files) {
    const content = fs.readFileSync(path.join(root, rel), 'utf8');
    const found = inlineVersionFindings(rel, content);
    violations.push(...found.violations);
    overrides.push(...found.overrides);
    references += found.references;
  }
  return { projects: files, violations, overrides, references };
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });
  const v = (xml) => inlineVersionFindings('src/x/X.csproj', xml).violations;
  const o = (xml) => inlineVersionFindings('src/x/X.csproj', xml).overrides;

  // --- 適合（違反にしてはならない形） ---
  t('CPM 準拠の PackageReference（版なし）は違反 0',
    v('<PackageReference Include="Microsoft.NET.Test.Sdk" />').length === 0);
  t('PackageVersion（CPM の中央定義）は対象外',
    v('<PackageVersion Include="xunit" Version="2.9.3" />').length === 0);
  t('GlobalPackageReference（CPM の全プロジェクト注入）は対象外',
    v('<GlobalPackageReference Include="Foo" Version="1.0.0" />').length === 0);
  t('PrivateAssets / IncludeAssets の子要素は版ではない',
    v('<PackageReference Include="coverlet.collector"><PrivateAssets>all</PrivateAssets>'
      + '<IncludeAssets>runtime; build</IncludeAssets></PackageReference>').length === 0);
  t('ProjectReference は対象外',
    v('<ProjectReference Include="../A/A.csproj" />').length === 0);
  t('コメントアウトされた直書きは違反にしない（説明コメントを赤にしない）',
    v('<!-- 例: <PackageReference Include="X" Version="1.0.0" /> -->').length === 0);
  t('属性値の中の "Version=" を属性名と誤認しない',
    v('<PackageReference Include="X" Condition="\'$(C)\'==\'Version=1\'" />').length === 0);
  t('前方一致の別要素（PackageReferenceFoo）に一致しない',
    v('<PackageReferenceFoo Include="X" Version="1.0.0" />').length === 0);

  // --- 違反（fail させる形） ---
  t('Version 属性は違反',
    v('<PackageReference Include="X" Version="1.0.0" />').length === 1);
  t('違反はパッケージ ID と値を報告する',
    JSON.stringify(v('<PackageReference Include="X" Version="1.0.0" />')[0])
      === JSON.stringify({ project: 'src/x/X.csproj', package: 'X', source: 'attribute', value: '1.0.0' }));
  t('属性の順序に依存しない（Version が先でも検出）',
    v('<PackageReference Version="1.0.0" Include="X" />').length === 1);
  t('子要素形（MSBuild メタデータ記法）も違反',
    v('<PackageReference Include="X"><Version>2.0.0</Version></PackageReference>').length === 1);
  t('子要素形は source=element として報告する',
    v('<PackageReference Include="X"><Version>2.0.0</Version></PackageReference>')[0].source === 'element');
  t('Update 形も違反（ID は Update から採る）',
    v('<PackageReference Update="X" Version="1.0.0" />')[0].package === 'X');
  t('Include も Update も無ければ ID は (不明)',
    v('<PackageReference Version="1.0.0" />')[0].package === '(不明)');
  t('プロパティ参照（$(XVersion)）でも違反',
    v('<PackageReference Include="X" Version="$(XVersion)" />').length === 1);
  t('空の Version も違反（意図が読めない記述を素通りさせない）',
    v('<PackageReference Include="X" Version="" />').length === 1);
  t('単一引用符も検出する（XML として妥当）',
    v("<PackageReference Include='X' Version='1.0.0' />").length === 1);
  t('条件付き ItemGroup 内でも違反（条件は解釈しない）',
    v('<ItemGroup Condition="\'$(TF)\'==\'net10.0\'"><PackageReference Include="X" Version="1.0.0" /></ItemGroup>').length === 1);
  t('複数要素をすべて拾う',
    v('<PackageReference Include="A" Version="1" /><PackageReference Include="B" />'
      + '<PackageReference Include="C" Version="3" />').length === 2);

  // --- VersionOverride（許可しつつ可視化する） ---
  t('VersionOverride 属性は違反にしない',
    v('<PackageReference Include="X" VersionOverride="1.0.0" />').length === 0);
  t('VersionOverride 属性は警告として拾う',
    o('<PackageReference Include="X" VersionOverride="1.0.0" />').length === 1);
  t('VersionOverride 子要素も警告として拾う',
    o('<PackageReference Include="X"><VersionOverride>1.0.0</VersionOverride></PackageReference>').length === 1);
  t('VersionOverride と Version の併記は違反かつ警告',
    inlineVersionFindings('src/x/X.csproj', '<PackageReference Include="X" Version="1" VersionOverride="2" />')
      .violations.length === 1
      && inlineVersionFindings('src/x/X.csproj', '<PackageReference Include="X" Version="1" VersionOverride="2" />')
        .overrides.length === 1);

  // --- 補助関数の境界 ---
  t('stripXmlComments: コメントのみ除去する',
    stripXmlComments('a<!-- b -->c') === 'ac');
  t('stripXmlComments: 複数行コメントも除去する',
    stripXmlComments('a<!--\n b \n-->c') === 'ac');
  t('parseAttributes: 二重・単一引用符の双方を読む',
    JSON.stringify(parseAttributes(' Include="X" Version=\'1.0\' '))
      === JSON.stringify({ Include: 'X', Version: '1.0' }));
  t('parseAttributes: 値の中の = を属性名として拾わない',
    parseAttributes(' Condition="a=b" ').a === undefined);
  t('packageReferenceElements: 自己終了形と対応形を数える',
    packageReferenceElements('<PackageReference Include="A" /><PackageReference Include="B"></PackageReference>').length === 2);
  t('packageReferenceElements: 対応形の本文を子要素まで含めて取る',
    packageReferenceElements('<PackageReference Include="A"><Version>1</Version></PackageReference>')[0].body
      === '<Version>1</Version>');
  t('metadataOf: 無ければ null', metadataOf('<PrivateAssets>all</PrivateAssets>', 'Version') === null);
  t('metadataOf: 前後の空白を落とす', metadataOf('<Version> 1.0 </Version>', 'Version') === '1.0');
  t('isScannedProjectFile: .csproj と .csproj.sample を対象にする',
    isScannedProjectFile('src/x/X.csproj') && isScannedProjectFile('templates/x/X.csproj.sample'));
  t('isScannedProjectFile: props / targets / cs / slnx は対象外',
    !isScannedProjectFile('src/Directory.Packages.props') && !isScannedProjectFile('src/Directory.Build.targets')
      && !isScannedProjectFile('src/x/X.cs') && !isScannedProjectFile('src/x/backend.slnx'));

  // --- 検査対象ユニットの切り分け（別プロジェクトの submodule は対象外） ---
  t('ai-stock-trading 配下は検査対象外',
    isExcludedPath('src/ai-stock-trading/backend/Services/X/src/X.Api/X.Api.csproj'));
  t('platform は検査対象', !isExcludedPath('src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj'));
  t('knowledge は検査対象', !isExcludedPath('src/knowledge/backend/Shared/Knowledge.Contracts/Knowledge.Contracts.csproj'));

  // --- 実地確認: 一時ツリーを scanTree で実走査する（負例の固定） ---
  // 関数単位の試験だけでは「走査対象に入っているか」を確かめられない（判定が正しくても
  // scanTree が当該ファイルを開かなければ検出されない）。よって scanTree ごと通す。
  {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'cpm-versions-selftest-'));
    const write = (rel, body) => {
      const abs = path.join(tmp, rel);
      fs.mkdirSync(path.dirname(abs), { recursive: true });
      fs.writeFileSync(abs, body);
    };
    // 除外判定（isExcludedPath）は**実リポジトリの .gitmodules** から導出済みのモジュール定数を使う
    // （check-backend-libraries.js と同じ作法）。一時ツリー側に .gitmodules は置かない。
    // (a) 属性形。
    write('src/platform/backend/A/A.csproj',
      '<Project><ItemGroup><PackageReference Include="A" Version="1.0.0" /></ItemGroup></Project>');
    // (b) 子要素形（属性しか見ない実装だと素通りする）。
    write('src/knowledge/backend/B/B.csproj',
      '<Project><ItemGroup><PackageReference Include="B"><Version>2.0.0</Version></PackageReference></ItemGroup></Project>');
    // (c) Update 形 ＋ 条件付き ItemGroup。
    write('src/knowledge/backend/C/C.csproj',
      '<Project><ItemGroup Condition="\'$(TargetFramework)\'==\'net10.0\'">'
      + '<PackageReference Update="C" Version="3.0.0" /></ItemGroup></Project>');
    // (d) 雛形。ci.yml のビルド対象外のため、走査しないと誰にも捕まらない。
    write('templates/unit-template/backend/T/T.csproj',
      '<Project><ItemGroup><PackageReference Include="T" Version="4.0.0" /></ItemGroup></Project>');
    // (e) 別プロジェクト（submodule）。検出してはならない。
    write('src/ai-stock-trading/backend/X/X.csproj',
      '<Project><ItemGroup><PackageReference Include="X" Version="9.9.9" /></ItemGroup></Project>');
    // (f) 適合プロジェクト ＋ VersionOverride（警告のみ）。
    write('src/platform/backend/D/D.csproj',
      '<Project><ItemGroup><PackageReference Include="D" />'
      + '<PackageReference Include="E" VersionOverride="5.0.0" /></ItemGroup></Project>');
    // (g) CPM の中央定義。走査対象外のファイル種であることを実地でも確かめる。
    write('src/Directory.Packages.props',
      '<Project><ItemGroup><PackageVersion Include="A" Version="1.0.0" /></ItemGroup></Project>');

    const r = scanTree(tmp);
    const hit = (p) => r.violations.filter((x) => x.project === p).length;
    t('実地(a): Version 属性の直書きを検出', hit('src/platform/backend/A/A.csproj') === 1);
    t('実地(b): 子要素形の直書きを検出', hit('src/knowledge/backend/B/B.csproj') === 1);
    t('実地(c): Update 形 ＋ 条件付き ItemGroup の直書きを検出', hit('src/knowledge/backend/C/C.csproj') === 1);
    t('実地(d): templates/ 配下の直書きを検出（雛形は全新規サービスへ伝播する）',
      hit('templates/unit-template/backend/T/T.csproj') === 1);
    t('実地(e): 別プロジェクト（ai-stock-trading）は検出しない',
      hit('src/ai-stock-trading/backend/X/X.csproj') === 0);
    t('実地(f): 適合プロジェクトは違反 0・VersionOverride は警告として 1 件',
      hit('src/platform/backend/D/D.csproj') === 0 && r.overrides.length === 1
        && r.overrides[0].package === 'E');
    t('実地(g): .props（CPM の中央定義）は走査対象に含めない',
      !r.projects.includes('src/Directory.Packages.props'));
    t('実地: 負例の合計は 4 件（属性・子要素・Update・雛形）', r.violations.length === 4, r.violations);
    t('実地: 走査したプロジェクトは 5 件（AST を除く）', r.projects.length === 5, r.projects);
    fs.rmSync(tmp, { recursive: true, force: true });
  }

  // --- 実リポジトリ: 現状は違反 0 件（#467 の受け入れ観点「現状で通る」の固定） ---
  {
    const r = scanTree();
    t('実リポジトリ: バージョン直書きは 0 件', r.violations.length === 0, r.violations);
    t('実リポジトリ: 走査対象の .csproj が 1 件以上ある（0 件検査への退行を止める）',
      r.projects.length > 0, r.projects.length);
    t('実リポジトリ: templates/ の .csproj も走査対象に入っている',
      r.projects.some((p) => p.startsWith('templates/')), r.projects.filter((p) => p.startsWith('templates/')));
  }

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-cpm-versions] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-cpm-versions] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

/** VersionOverride の使用を警告として可視化する（終了コードは変えない）。 */
function reportOverrides(overrides) {
  if (!overrides.length) return;
  warn(`CPM の VersionOverride を ${overrides.length} 件使用しています（許可されていますが、`
    + '中央定義（src/Directory.Packages.props）と実際の解決版が乖離します）: '
    + overrides.map((x) => `${x.project} の ${x.package}=${x.value}`).join(' / '));
  const summary = process.env.GITHUB_STEP_SUMMARY;
  if (!summary) return;
  const lines = ['### CPM: VersionOverride の使用箇所', '',
    '| プロジェクト | パッケージ | 版 | 記法 |', '| --- | --- | --- | --- |'];
  for (const x of overrides) lines.push(`| \`${x.project}\` | ${x.package} | ${x.value} | ${x.source} |`);
  try { fs.appendFileSync(summary, lines.join('\n') + '\n'); } catch { /* サマリを書けなくても検査は続ける */ }
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }

  const { projects, violations, overrides, references } = scanTree();
  reportOverrides(overrides);

  if (violations.length === 0) {
    console.log(`[check-cpm-versions] OK: ${projects.length} プロジェクト / ${references} 件の PackageReference に`
      + `バージョン直書き 0 件（VersionOverride ${overrides.length} 件）。`);
    process.exit(0);
  }

  console.error(`[check-cpm-versions] バージョン直書き ${violations.length} 件を検出しました:`);
  for (const x of violations) {
    const how = x.source === 'attribute' ? 'Version 属性' : '<Version> 子要素';
    console.error(`\n  ${x.project}\n    ${x.package} に ${how}（${x.value || '空'}）が書かれています。`);
  }
  console.error('\nバージョンは src/Directory.Packages.props の <PackageVersion> に集約します（CLAUDE.md /'
    + ' 計画 ADR-0030）。.csproj からは Version を削除し、中央に定義が無ければ中央へ追加してください。'
    + '\n特定プロジェクトだけ別の版が必要な場合に限り VersionOverride を使えます（許可されますが、'
    + '使用箇所は実行サマリに警告として出ます）。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  EXCLUDED_UNITS,
  isExcludedPath,
  toPosix,
  isScannedProjectFile,
  stripXmlComments,
  parseAttributes,
  packageReferenceElements,
  metadataOf,
  packageIdOf,
  inlineVersionFindings,
  scanTree,
};
