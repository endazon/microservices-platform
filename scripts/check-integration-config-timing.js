#!/usr/bin/env node
/**
 * NFR, #1040: 「ビルダ構築時に読まれる構成キーを、統合テストの器が `UseSetting` で与えているか」
 * の静的検査。**同型の事故を 3 回踏んだ**（規約「同型の事故が 2 回起きたら検査器を置く」を超過）。
 *
 * ■ 機序（3 回とも同一）
 *
 * 本番の `Program.cs` はトップレベル文で構成を**即座に**読む。一方 `ConfigureAppConfiguration` で
 * 足した値が見えるのは**その後**であり、**読み取りに間に合わない**。`UseSetting` はホスト構成へ
 * 書くので `CreateBuilder` が構成を組む時点から見える。**どちらで与えるかは好みではなく、
 * 読み取り時点で決まる。**
 *
 *   1. `Pipeline:ConfigPath`（#455 Phase 0 U0d）—— 段宣言が 1 行も読まれないまま全テストが緑
 *   2. `RabbitMq:ConnectionString`（#998 の Wolverine 切替後・PR #1006）—— BrokerInitializationException
 *   3. `ConnectionStrings:DefaultConnection`（#1012 の fail-fast 化後・#1032）—— 28 件が起動に到達せず失敗
 *
 * 🔴 **散文の警告は機能しなかった。** 器（`IntegrationTestFactory.cs`）はこの罠を本文中に 2 度
 * 書いており、2 回目を直した本人が 3 回目を踏んだ。だから機械で止める。
 *
 * ■ 何を違反とするか（**「Build 前に読む」だけでは落とさない**）
 *
 * `Build()` 前に読まれるキーには `Services:LlmGateway` や `WikiJs:ApiKey` のように
 * **既定値つきで読まれるもの**が多数ある（実測 8 件）。それらは未注入でも壊れないので、
 * 全部を要求すると**偽陽性の山になり検査器が無視される**。落とすのは
 * **「未注入だと壊れる読み方」**に限る。3 件の事故はすべてこの形だった。
 *
 *   A. **fail-fast** —— `?? throw` を伴う読み（1・2・3 のうち 2 と 3）
 *   B. **黙って縮退する読み** —— 下の SILENT_DEGRADERS（1）。関数が未設定時に**無言で return**
 *      するため、壊れたことがテスト結果に現れない。**A より危険であり、機械でしか気付けない。**
 *
 * ■ 器ごとに数える（**全器の UseSetting を 1 つの集合にしない**）
 *
 * 🔴 **最初の実装はここで間違えた。** テスト木全体の `UseSetting` を 1 つの集合に集めたところ、
 * 基底フィクスチャから 3 件のキーを 1 つずつ外す変異試験が**すべて生存した** —— 同じキーを
 * `QueueOverrideFanOutTests` と `McpToolDeclarationHosts` も与えているため、集合から消えなかった。
 * **緑は「検出力がある」を意味しない。** 器（host group）ごとに数える。
 *
 *   - **基底フィクスチャ群** …… `Fixtures/IntegrationTestFactory.cs` の `UseSetting`。
 *     `IntegrationTestFactoryBase` を継承する器と、それを `new` するテストが受け取る。
 *   - **単独の器** …… `WebApplicationFactory<TMarker>` を直接継承する器（`McpToolDeclarationHosts` /
 *     `RagIntegrationFactory`）。**基底の値は届かないので自分で与える。**
 *
 * ■ B（黙って縮退する読み）は基底フィクスチャ群にだけ要求する
 *
 * A（fail-fast）は**どの器でもホストが起動しない**ので普遍に要求できる。B はそうではない ——
 * 縮退して困るかどうかは**そのテストが何を主張するか**による。実例: `McpToolDeclarationHosts` は
 * DocumentService / GraphService を起こすが、見るのは `/internal/mcp-tools` だけであり、
 * 段宣言が読まれなくても主張は壊れない。ここに B を要求すると**無意味な注入を強いる偽陽性**になる。
 * 事故 1（`Pipeline:ConfigPath`）が起きたのは段を実際に流す試験群＝基底フィクスチャ群であり、
 * **そこに限って要求する。**
 *
 * ■ 検査しないこと（既知の限界。書いておく）
 *
 * - **器が条件つきで与えるキーの対応づけ**は見ていない。`RabbitMq:ConnectionString` の
 *   `UseSetting` は `if (_rabbit is not null)` の中に在り、`AuthorizationServiceFactory` は
 *   `base(pg, null)` を渡す。実測では AuthorizationService は RabbitMq を 1 度も読まないため
 *   齟齬は無い（`grep -c RabbitMq` = 0）。**RabbitMq を読むサービスの器が null を渡す**構成が
 *   将来生まれたら、本検査は素通りする。そのときは器の側が起動時に落ちる。
 * - 器を持たないサービス（RetrievalService / FeedbackService / DashboardService /
 *   NotificationService / McpServer / LlmGateway / Platform.Bff）は対象外である。
 *   **要求する器が無いキーを要求しても意味が無い。**
 *
 * 使い方: `node scripts/check-integration-config-timing.js [--self-test]`
 * 外部依存ゼロ（Node 標準モジュールのみ）。違反があれば終了コード 1。
 */
'use strict';

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.resolve(__dirname, '..');

/** サービス実装の走査根（両ユニット）。 */
const SERVICE_ROOTS = ['src/knowledge/backend/Services', 'src/platform/backend/Services'];

/** 統合テストの器が住む場所。 */
const TESTS_ROOT = 'src/knowledge/backend/Tests/Knowledge.IntegrationTests';

/**
 * B: 未設定でも例外を投げず、**黙って何もしない**読み手。
 * 呼び出しが Build 前に在れば、対応するキーを器が与えていなければならない。
 * **増やすときは「未設定時に無言で return するか」を実際に読んで確かめること。**
 */
const SILENT_DEGRADERS = [
  {
    call: 'AddPlatformPipelineConfig',
    key: 'Pipeline:ConfigPath',
    why: '未設定・不在パスだと段宣言を読まないまま黙って return する（#455 Phase 0 U0d）',
  },
];

/** コメント行だけを落とす。**文字列中の `//`（URL 等）を壊さないため、行頭のみを見る。** */
function stripCommentLines(src) {
  return src
    .split('\n')
    .filter((l) => !/^\s*\/\//.test(l))
    .join('\n');
}

/** `builder.Build()` より前の領域を返す。見つからなければ null（呼び出し側が fail-closed する）。 */
function preBuildRegion(src) {
  const i = src.indexOf('builder.Build()');
  return i < 0 ? null : src.slice(0, i);
}

/** A ＋ B のキーを抽出する。 */
function loadBearingKeys(preBuild) {
  const keys = new Set();
  // A-1: GetConnectionString("X") ?? throw  →  ConnectionStrings:X
  for (const m of preBuild.matchAll(
    /Configuration\s*\.\s*GetConnectionString\(\s*"([^"]+)"\s*\)\s*\?\?\s*throw/g,
  ))
    keys.add(`ConnectionStrings:${m[1]}`);
  // A-2: Configuration["X"] ?? throw
  for (const m of preBuild.matchAll(/Configuration\s*\[\s*"([^"]+)"\s*\]\s*\?\?\s*throw/g))
    keys.add(m[1]);
  // B: 無言で縮退する読み手
  for (const d of SILENT_DEGRADERS) if (preBuild.includes(d.call)) keys.add(d.key);
  return keys;
}

function walk(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === 'bin' || e.name === 'obj') continue;
      walk(full, out);
    } else out.push(full);
  }
  return out;
}

/** 1 ファイルの `UseSetting("KEY"` を集める。 */
function useSettingKeysIn(file) {
  const src = stripCommentLines(fs.readFileSync(file, 'utf8'));
  return new Set([...src.matchAll(/UseSetting\(\s*"([^"]+)"/g)].map((m) => m[1]));
}

/** マーカー型名 → その `Program.cs`。`TestMarker.cs` は `Program.cs` と同じディレクトリに在る。 */
function markerToProgram() {
  const map = new Map();
  for (const root of SERVICE_ROOTS) {
    for (const f of walk(path.join(REPO_ROOT, root))) {
      if (path.basename(f) !== 'TestMarker.cs') continue;
      const decl = /class\s+(\w*TestMarker)\b/.exec(fs.readFileSync(f, 'utf8'));
      if (!decl) continue;
      const program = path.join(path.dirname(f), 'Program.cs');
      if (fs.existsSync(program)) map.set(decl[1], program);
    }
  }
  return map;
}

function run() {
  const testFiles = walk(path.join(REPO_ROOT, TESTS_ROOT)).filter((f) => f.endsWith('.cs'));
  if (testFiles.length === 0) {
    console.error(`[check-integration-config-timing] 走査対象が 0 件である（${TESTS_ROOT} の想定が壊れている）。`);
    process.exit(1);
  }
  const baseFile = path.join(REPO_ROOT, TESTS_ROOT, 'Fixtures', 'IntegrationTestFactory.cs');
  if (!fs.existsSync(baseFile)) {
    console.error('[check-integration-config-timing] 基底フィクスチャが見つからない（想定が壊れている）。');
    process.exit(1);
  }
  const baseKeys = useSettingKeysIn(baseFile);
  const markers = markerToProgram();
  if (markers.size === 0) {
    console.error('[check-integration-config-timing] TestMarker が 0 件である（0 件を緑にしない）。');
    process.exit(1);
  }

  // 基底を継承する器のクラス名。これを `new` するテストにも基底の UseSetting が届く。
  const baseFactoryClasses = new Set();
  for (const f of testFiles) {
    const src = stripCommentLines(fs.readFileSync(f, 'utf8'));
    for (const m of src.matchAll(/class\s+(\w+)\s*:\s*IntegrationTestFactoryBase</g))
      baseFactoryClasses.add(m[1]);
  }

  const violations = [];
  let checked = 0;
  for (const f of testFiles) {
    const src = stripCommentLines(fs.readFileSync(f, 'utf8'));
    const used = [...markers.keys()].filter((m) => src.includes(m));
    if (used.length === 0) continue;

    const inheritsBase =
      f === baseFile ||
      src.includes('IntegrationTestFactoryBase<') ||
      [...baseFactoryClasses].some((c) => src.includes(c));
    const available = new Set([...useSettingKeysIn(f), ...(inheritsBase ? baseKeys : [])]);

    for (const marker of used) {
      const program = markers.get(marker);
      const pre = preBuildRegion(stripCommentLines(fs.readFileSync(program, 'utf8')));
      if (pre === null) {
        violations.push({
          host: path.relative(REPO_ROOT, f),
          svc: path.relative(REPO_ROOT, program),
          key: '(builder.Build() が見つからない)',
          hint: '合成ルートの想定が壊れている。検査が空回りするので落とす',
        });
        continue;
      }
      checked++;
      for (const key of loadBearingKeys(pre)) {
        const deg = SILENT_DEGRADERS.find((d) => d.key === key);
        // B は基底フィクスチャ群にだけ要求する（上の注記）。
        if (deg && !inheritsBase) continue;
        if (available.has(key)) continue;
        violations.push({
          host: path.relative(REPO_ROOT, f),
          svc: path.relative(REPO_ROOT, program),
          key,
          hint: deg ? deg.why : '`?? throw` で読まれる（未注入だとホストが起動しない）',
        });
      }
    }
  }

  if (checked === 0) {
    console.error('[check-integration-config-timing] 器に覆われたサービスが 0 件である（0 件を緑にしない）。');
    process.exit(1);
  }

  if (violations.length > 0) {
    console.error('[check-integration-config-timing] ビルダ構築時に読まれるキーを器が与えていません:\n');
    for (const v of violations)
      console.error(`  器 ${v.host}\n    → ${v.svc}\n      ${v.key} —— ${v.hint}`);
    console.error(
      '\nその器の `ConfigureWebHost` で `builder.UseSetting("<キー>", …)` として与えること。\n' +
        '🔴 **`ConfigureAppConfiguration` では間に合わない** —— Program.cs のトップレベル文が先に読む。\n' +
        'これは #455 / #998 / #1012 で 3 回踏んだ罠である（#1040）。',
    );
    process.exit(1);
  }

  console.log(
    `[check-integration-config-timing] OK: 器 × サービスの組 ${checked} 件を走査し、` +
      'ビルダ構築時に読まれる要注入キーはすべてその器が与えています。',
  );
}

function selfTest() {
  const cases = [
    [
      'A-1: GetConnectionString + ?? throw を拾う',
      'var c = builder.Configuration.GetConnectionString("DefaultConnection")\n ?? throw new X();',
      ['ConnectionStrings:DefaultConnection'],
    ],
    [
      'A-2: Configuration[..] + ?? throw を拾う',
      'var r = builder.Configuration["RabbitMq:ConnectionString"]\n ?? throw new X();',
      ['RabbitMq:ConnectionString'],
    ],
    [
      'B: 無言で縮退する読み手を拾う',
      'builder.AddPlatformPipelineConfig();',
      ['Pipeline:ConfigPath'],
    ],
    [
      '🔴 既定値つきの読みは拾わない（偽陽性を作らない）',
      'var u = builder.Configuration["Services:LlmGateway"] ?? "http://llm-gateway:8080";',
      [],
    ],
    [
      '🔴 既定値つきの GetConnectionString も拾わない',
      'var c = builder.Configuration.GetConnectionString("Other") ?? "Host=localhost";',
      [],
    ],
  ];
  let failed = 0;
  for (const [name, src, expected] of cases) {
    const got = [...loadBearingKeys(src)].sort();
    const want = [...expected].sort();
    if (JSON.stringify(got) !== JSON.stringify(want)) {
      console.error(`  ✗ ${name}（期待 ${JSON.stringify(want)} / 実際 ${JSON.stringify(got)}）`);
      failed++;
    } else console.log(`  ok  ${name}`);
  }

  // 🔴 最重要: ConfigureAppConfiguration にだけ在る状態を「与えている」と数えない。
  // ここを外すと、検査が過去 3 件をすべて素通りする（＝検査したつもりで何も検査していない）。
  const onlyOverrides = `builder.ConfigureAppConfiguration((_, cfg) => {
      cfg.AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:DefaultConnection"] = cs,
      });
    });`;
  const tmp = path.join(REPO_ROOT, 'scripts', '.selftest-config-timing.cs');
  fs.writeFileSync(tmp, onlyOverrides);
  try {
    const got = useSettingKeysIn(tmp);
    if (got.has('ConnectionStrings:DefaultConnection')) {
      console.error('  ✗ 🔴 ConfigureAppConfiguration のキーを UseSetting と数えている（検査が無意味になる）');
      failed++;
    } else console.log('  ok  🔴 ConfigureAppConfiguration のキーは「与えている」と数えない');
  } finally {
    fs.unlinkSync(tmp);
  }

  // コメント行に書かれた読み方を拾わない（器も Program.cs も、この罠を散文で説明している）。
  const commented = '// var r = builder.Configuration["RabbitMq:ConnectionString"] ?? throw new X();';
  if (loadBearingKeys(stripCommentLines(commented)).size !== 0) {
    console.error('  ✗ コメント行の記述を違反として拾っている');
    failed++;
  } else console.log('  ok  コメント行の記述は拾わない');

  // 文字列中の `//`（URL）を壊さない。
  if (!stripCommentLines('var u = "http://llm-gateway:8080";').includes('http://llm-gateway:8080')) {
    console.error('  ✗ 文字列中の // を壊している');
    failed++;
  } else console.log('  ok  文字列中の // を壊さない');

  if (failed > 0) process.exit(1);
  console.log(`✓ self-test: ${cases.length + 3} 件すべて通過`);
}

if (process.argv.includes('--self-test')) selfTest();
else run();
