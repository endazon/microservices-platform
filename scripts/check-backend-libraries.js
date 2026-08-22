#!/usr/bin/env node
'use strict';
/*
 * check-backend-libraries.js
 * バックエンドアプリケーション層のライブラリ標準（計画 ADR-0030 / 12_backend-application-stack）の
 * 機械強制（NFR, Issue #455）。外部依存ゼロ（Node 標準モジュールのみ）。
 *
 * 検査するルール:
 *   1) 不採用ライブラリの参照禁止: .csproj / MSBuild の props・targets の PackageReference と
 *      .cs の using の双方を走査する（props・targets は ADR-0030 / #471 で追加。Directory.Build.props に
 *      <PackageReference> を書くと全プロジェクトへ一括注入されるため、csproj だけを見る検査は素通りになる）。
 *      **PackageVersion（CPM のバージョン定義）は違反にしない**。baseline を消化するまで不採用パッケージの
 *      版定義は src/Directory.Packages.props に正当に残る設計であり、違反にすると残件と同数の偽陽性が出る
 *      （#455 の Serilog 消化後は 29 件。IADR-0216）。
 *      現行実装は MassTransit を広範に使用中のため（FluentAssertions は #455 A-3 で消化済み・残件 0）、即時に全件 fail
 *      させると「成果物は正しいのに赤」が常態化する。同じ判断の先例は scripts/README.md の
 *      check-permission-denials.js の段階ポリシーである（赤の常態化は「赤を無視する学習」を生み、
 *      検査の目的そのものを壊す。planning#146・planning#160（前段の失敗モード）／
 *      planning#161・planning#162（段階ポリシーの導入））。よって **ratchet 方式**を採る。
 *        - baseline に無いプロジェクトでの違反 → fail（新規混入を止める）
 *        - baseline にあるプロジェクトの違反   → warn（残件として実行サマリに出す）
 *        - baseline にあるのに違反が消えた     → fail（baseline の減らし忘れを検出する）
 *      3 番目が要点である。カバレッジ ratchet と同じく **床は下げられるが上げっぱなしにできない**。
 *      🔴 **行が減る最小単位はイベント辺**（1 イベントの発行元＋全購読先を一括）であり、辺はサービスを
 *      またぐため各サービスの再実装 issue（#438〜#451）には入れられない。残件は Wolverine 移行（#441）が
 *      落とす。旧版の「各サービスの再実装 issue が自プロジェクトの行を削除する」は誤り（IADR-0234 決定 2）。
 *   2) Domain 層の外部依存ゼロ: *.Domain.csproj は PackageReference を持てない
 *      （ProjectReference は共有カーネル Platform.Shared.Kernel のみ許可）。ADR-0030 選定基準 3 の機械化。
 *   4) 禁止 API シンボル（計画 ADR-0027 移行チェックリスト 手順 3・手順 6。#455）:
 *      Wolverine の `PrefixIdentifiers` は **exchange 名まで前置する**ため、fan-out の宛先が
 *      誰にも束ねられず「**誰にも届かない**」形になる（キュー名の衝突＝competing consumer より悪い）。
 *      計画は手順 3 で「使わない」と明示し、手順 6 で「3〜5 を共通ヘルパへ封じ込め、
 *      **個別サービスでの逸脱を静的検査で禁止する**」と定めている。本規則はその静的検査である。
 *      🔴 **現在 0 件であり、これが正解の状態である**（未実装ではない）。ratchet は要らず、
 *      新規混入をそのまま fail にする（規則 3 と同じく既知違反ゼロで開始できる）。
 *      🔴 **移行を始める前に置くことに意味がある** —— 誤用が起きるのは Wolverine を配線する
 *      まさにその瞬間であり、そのとき既に仕掛けが在る必要がある。
 *   3) 共有カーネルの依存規律（計画 ADR-0041 が選定基準 3 を部分改定。#500）:
 *      Platform.Shared.Kernel が持てる外部パッケージは Result 型の実装 1 つ（SHARED_KERNEL_ALLOWED）に限る。
 *      その 1 つは共有カーネルの**内部実装としてのみ**許され、他プロジェクトでの直接参照は 2) と同じく違反。
 *      現時点で *.Domain プロジェクトは存在しないため、既知違反ゼロで開始でき ratchet を要さない。
 *
 * 使い方:
 *   node scripts/check-backend-libraries.js             # src/ を走査。違反があれば終了コード 1。
 *   node scripts/check-backend-libraries.js --self-test # 検査ロジック自体の自己試験。
 *   node scripts/check-backend-libraries.js --write-baseline # 現状を baseline へ書き出す（初回のみ）。
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const { warn, notice } = require('./lib/ci-annotate');
const { excludedUnits, makeIsExcludedPath } = require('./lib/excluded-units.js');

const REPO_ROOT = path.resolve(__dirname, '..');
const SRC_DIR = 'src';
const BASELINE_FILE = path.join(__dirname, 'backend-library-baseline.json');
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);

/**
 * 検査対象外のユニット。ADR-0030 は **MSP（microservices-platform）の計画 ADR** であり、
 * src/ai-stock-trading は独自の計画リポジトリと ADR を持つ**別プロジェクト**（submodule）である。
 * 他プロジェクトへ MSP の標準を適用するのは誤りであり、submodule のため本リポジトリからは
 * 是正もできない（.claude/rules/traceability.md「複数プロジェクトを跨ぐ場合」と同じ切り分け）。
 *
 * 値は .gitmodules（src/<unit> の submodule）から導出する。かつては本ファイル・
 * check-test-traceability.js・check-coverage-floor.js が同じ集合を独立にハードコードしており、
 * IADR-0056 決定 6（追加の可変機能ユニットは submodule でリンク）で次のユニットが増えた瞬間に
 * 3 箇所が同時に狭すぎになる形だった（issue #473）。導出規則と fail-closed の根拠は
 * scripts/lib/excluded-units.js を参照。
 */
const EXCLUDED_UNITS = excludedUnits({ root: REPO_ROOT });

/** リポジトリ相対パスが検査対象外ユニット配下か。 */
const isExcludedPath = makeIsExcludedPath(EXCLUDED_UNITS);

/**
 * 不採用ライブラリ（計画 12_backend-application-stack の棚卸し表で ★不採用 / 置換対象）。
 * 値はパッケージ ID かつ using の名前空間の先頭セグメント群として扱う。
 * 判定は「完全一致」または「<名前>. で始まる」（Serilog → Serilog.AspNetCore は該当、
 * SerilogFoo は非該当）。
 */
const BANNED = [
  'MediatR',
  'AutoMapper',
  'Mapster',
  'MassTransit',
  'FluentAssertions',
  'Serilog',
  'Hellang.Middleware.ProblemDetails',
  'OneOf',
  'CSharpFunctionalExtensions',
  'Z.EntityFramework.Extensions',
  'Hangfire',
  'OpenIddict',
  'BCrypt.Net-Next',
  'DotNetEnv',
  'BouncyCastle.Cryptography',
  // ADR-0030 / #471: 実在するパッケージ ID は Microsoft.Kiota.Abstractions 等であり、
  // 'Kiota' は完全一致にも 'Kiota.' 前方一致にも当たらない**死にエントリ**だった。
  'Microsoft.Kiota',
  'NSwag',
  // --- 以下は 12_backend-application-stack の棚卸し表にあるのに BANNED から漏れていた分（#471） ---
  // L77「WolverineFx.Kafka ★採用 … Confluent.Kafka 直接利用はしない」。
  // Wolverine トランスポート（WolverineFx.Kafka）経由が標準であり、素クライアントの直接参照を止める。
  'Confluent.Kafka',
  // L76「WolverineFx.RabbitMQ ★採用 … MassTransit / 素の RabbitMQ.Client を置換」。
  'RabbitMQ.Client',
  // L85「Azure Key Vault Provider（Secret 管理）★不採用 … HashiCorp Vault（暫定は k8s Secret）」。
  // ADR が不採用としたのは「Azure Key Vault から secret を取る経路」そのものであるため、
  // 構成プロバイダ（Azure.Extensions.AspNetCore.Configuration.Secrets）と
  // Key Vault クライアント SDK（Azure.Security.KeyVault.Secrets / .Keys / .Certificates）の双方を対象にする。
  // Azure.Identity や他の Azure SDK は不採用ではないため、前方一致は Azure.Security.KeyVault までに留める。
  'Azure.Extensions.AspNetCore.Configuration.Secrets',
  'Azure.Security.KeyVault',
  // L79「OpenIddict / BCrypt.Net-Next / Argon2（パスワードハッシュ）★不採用 … Keycloak が担う」。
  // Argon2 は .NET に単一の標準実装が無く複数パッケージが流通するため、実在 ID を列挙する。
  // Konscious は Argon2 と Blake2 を別パッケージで出しており、Blake2 は ADR の不採用対象ではない。
  // よって前方一致の起点は .Argon2 までとし、同一作者の別用途パッケージを巻き込まない。
  'Konscious.Security.Cryptography.Argon2',
  'Isopoh.Cryptography.Argon2',
];

/** 共有カーネル（Domain が唯一 ProjectReference を許される先）。 */
/**
 * 計画 ADR-0027 手順 3 が名指しで禁じた API。**「使ってはならない」ものだけを置く。**
 * `DisableConventionalLocalRouting`（手順 4）・`ServiceLocationPolicy`（手順 5）は**逆で、
 * 共通ヘルパに在るべきもの**なので、ここへ入れてはならない（極性が違う）。
 * それらは下の CONFINED_APIS（規則 5）が「1 箇所でだけ使ってよい」として扱う。
 * 2 つのリストにシンボルが重複しないことは自己試験が固定する。
 */
const FORBIDDEN_APIS = [
  {
    symbol: 'PrefixIdentifiers',
    why: 'exchange 名まで前置され、fan-out の宛先が「誰にも届かない」形になる（ADR-0027 手順 3）。'
      + ' キュー名はサービス名を前置して衝突を避け、exchange 名は既定（メッセージ型の fanout）のままにすること。',
  },
  {
    // #897 の監査（フレッシュ文脈）が「手順 3 を潰す最も現実的な経路」として実測で挙げた。
    // 封じ込め側（規則 5）ではなく禁止側（規則 4）に置く —— 共通ヘルパも含めて**誰も使わない**ためである。
    symbol: 'UseConventionalRouting',
    why: 'Wolverine の規約ルーティングはリスニングキュー名を**メッセージ型名だけ**から導くため、'
      + '同じイベントを購読する別サービスが**必ず**同一キューを共有し、pub/sub が competing consumer へ'
      + '退行する（ADR-0027 手順 3 が防ごうとしているもの）。'
      + ' WolverineExtensions.ListenToPlatformQueue で明示的に配線すること。',
  },
];

/**
 * .cs 本文から禁止 API シンボルの使用を拾う。
 *
 * 🔴 **識別子の境界で照合する。** 部分一致で拾うと `PrefixIdentifiersFoo` のような別の識別子や、
 * 将来 `MyPrefixIdentifiers` のような命名が生まれたときに偽陽性になる。偽陽性は「検査を無視する学習」
 * を生むため、禁止側の検査ほど境界を厳密にする。
 *
 * コメント内の記述も拾う。**この API は名前を書くこと自体が誤りの兆候**であり、
 * 「コメントに書いてから外す」経路を許すと、次に読む人が有効な選択肢だと受け取る。
 * 意図的に言及する必要がある文書は `.cs` ではなく `docs/` / `.ai-context/` に置く（走査対象外）。
 */
function forbiddenApiViolations(relPath, content, list = FORBIDDEN_APIS) {
  const out = [];
  for (const f of list) {
    const re = new RegExp(`\\b${f.symbol}\\b`);
    if (re.test(String(content))) {
      out.push({ kind: 'forbidden-api', project: toPosix(relPath), detail: f.symbol });
    }
  }
  return out;
}

/**
 * 計画 ADR-0027 手順 6 が「共通ヘルパへ封じ込め、**個別サービスでの逸脱を静的検査で禁止**する」と
 * 定めた 3 手順の API。規則 4（禁止 API）とは**極性が逆**である —— これらは使ってよい。
 * ただし **使ってよい場所が 1 箇所しかない**。
 */
const CONFINED_API_HOME =
  'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/WolverineExtensions.cs';

/**
 * 封じ込め API を書いてよいファイル。**本拠と、その本拠のふるまいを試験するファイルだけ**である。
 * 試験は「個別サービスでの逸脱」ではないので許可するが、**プロジェクト単位ではなくファイル単位**で
 * 許可する（`*.Tests` を丸ごと許すと、サービス側のテストが逸脱した配線を組めてしまう）。
 */
const CONFINED_API_ALLOWED = [
  CONFINED_API_HOME,
  'src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/Extensions/WolverineExtensionsTests.cs',
];

const CONFINED_APIS = [
  {
    symbol: 'ListenToRabbitQueue',
    // 規則 5(b) 用。**実際に呼ばれている形**でしか一致しない（下の confinedApiHomeGaps を参照）。
    usage: /\.\s*ListenToRabbitQueue\s*\(/,
    step: '手順 3',
    why: 'リスニングキュー名にサービス名を前置する適用点。前置を怠ると、同一イベントを購読する'
      + '2 サービスが同じキューを共有して competing consumer へ退行し、丁度 1 つだけが受信する。'
      + ' WolverineExtensions.ListenToPlatformQueue を使うこと。',
  },
  {
    symbol: 'DisableConventionalLocalRouting',
    usage: /\.\s*DisableConventionalLocalRouting\s*\(/,
    step: '手順 4',
    why: '発行元プロセスに同じ型のハンドラがあると発行がプロセス内へ閉じる。'
      + ' WolverineExtensions.UsePlatformMessagingDefaults を使うこと。',
  },
  {
    symbol: 'ServiceLocationPolicy',
    // 🔴 **代入**されていること。参照（右辺の enum 名）でも**比較**でも満たさない。
    // `(?!=)` が無いと `if (o.ServiceLocationPolicy == X)` のような読み取り専用の参照に一致し、
    // 手順 5 の適用が消えていても「在る」と誤判定する（#897 の 2 巡目レビューの指摘。実測で再現した）。
    // 呼び出し構文へ絞る是正それ自体に、同じ「広く拾いすぎる」穴が残っていた。
    usage: /\.\s*ServiceLocationPolicy\s*=(?!=)/,
    step: '手順 5',
    why: 'internal 実装型に依存するハンドラが**最初のメッセージ受信時に**落ちる（既定は NotAllowed）。'
      + ' WolverineExtensions.UsePlatformMessagingDefaults を使うこと。',
  },
];

/**
 * 規則 5(a): 封じ込め API が**許可ファイルの外**で使われていないか。
 *
 * 照合は規則 4 と同じく識別子の境界で行う。コメント中の言及も拾うのは規則 4 と同じ理由である
 * （「コメントに書いてから外す」経路を許すと、次に読む人が有効な選択肢だと受け取る）。
 */
function confinedApiViolations(
  relPath, content, list = CONFINED_APIS, allowed = CONFINED_API_ALLOWED,
) {
  const rel = toPosix(relPath);
  if (allowed.includes(rel)) return [];
  const out = [];
  for (const f of list) {
    if (new RegExp(`\\b${f.symbol}\\b`).test(String(content))) {
      out.push({ kind: 'confined-api', project: rel, detail: f.symbol });
    }
  }
  return out;
}

/**
 * .cs からコメントを取り除く。行コメント（行頭・行末とも）とブロックコメントを空白へ潰す。
 *
 * 文字列リテラル中の `//` を誤ってコメントと見なさないよう、素朴だが状態を持って走る。
 * **誤って消しすぎる方向は安全である**（規則 5(b) が「実装が無い」と判定して fail するため、
 * 静かに通ることはない）。逆に消し足りないと F1 の穴が残るので、迷ったら消す側へ倒す。
 */
function stripCsharpComments(text) {
  const src = String(text);
  let out = '';
  let i = 0;
  let state = 'code'; // code | line | block | string | verbatim | char
  while (i < src.length) {
    const c = src[i];
    const d = src[i + 1];
    if (state === 'code') {
      if (c === '/' && d === '/') { state = 'line'; i += 2; continue; }
      if (c === '/' && d === '*') { state = 'block'; i += 2; continue; }
      if (c === '@' && d === '"') { state = 'verbatim'; out += '  '; i += 2; continue; }
      if (c === '"') { state = 'string'; out += ' '; i += 1; continue; }
      if (c === '\'') { state = 'char'; out += ' '; i += 1; continue; }
      out += c; i += 1; continue;
    }
    if (state === 'line') {
      if (c === '\n') { state = 'code'; out += '\n'; }
      i += 1; continue;
    }
    if (state === 'block') {
      if (c === '*' && d === '/') { state = 'code'; i += 2; continue; }
      if (c === '\n') out += '\n';
      i += 1; continue;
    }
    if (state === 'string') {
      if (c === '\\') { i += 2; continue; }
      if (c === '"') { state = 'code'; }
      i += 1; continue;
    }
    if (state === 'verbatim') {
      if (c === '"' && d === '"') { i += 2; continue; }
      if (c === '"') { state = 'code'; }
      i += 1; continue;
    }
    // char
    if (c === '\\') { i += 2; continue; }
    if (c === '\'') { state = 'code'; }
    i += 1;
  }
  return out;
}

/**
 * 規則 5(b): 封じ込め API が**本拠から消えていない**か。
 *
 * 🔴 **これが無いと規則 5 は静かに no-op になる。** (a) だけなら「どこにも書かれていない」状態が
 * 満点になり、共通ヘルパから手順 4 の 1 行を削っても検査は緑を返す。封じ込めは
 * 「他所で書けない」だけでは半分で、「ここに在り続ける」が要る。
 *
 * 🔴 **(a) と (b) は照合の仕方が違う。極性が逆だからである。**
 *   - (a)「ここに書くな」→ **コメントも含めた全文**をバレ識別子で見る（規則 4 と同じ。
 *     「コメントに書いてから外す」経路を塞ぐ）。見落としは fail 側へ倒れるので安全である。
 *   - (b)「ここに在れ」→ **コメントを除いたコード**を**呼び出し構文**で見る。
 *     全文をバレ識別子で見ると、**本拠の説明コメントが実装の消失を覆い隠す**。
 *
 * 🔴 **当初の実装は (a) のロジックを (b) へ流用しており、まさにその穴を持っていた。**
 * `PR #897` の AI レビューとフレッシュ文脈監査が独立に同じ穴を指摘し、実測で確認した ——
 * 本拠の説明コメントが `ListenToRabbitQueue` と `ServiceLocationPolicy` に言及しているため、
 * **実コード 1 行だけを消すと EXIT=0 になっていた**（当初の変異試験はコメントごと消していたので
 * 当たっていなかった）。fail-open を塞ぐつもりで足した門が、それ自体 fail-open だった。
 */
function confinedApiHomeGaps(homeContent, list = CONFINED_APIS) {
  const code = stripCsharpComments(homeContent);
  return list
    .filter((f) => !(f.usage instanceof RegExp
      ? f.usage.test(code)
      : new RegExp(`\\b${f.symbol}\\b`).test(code)))
    .map((f) => f.symbol);
}

const SHARED_KERNEL = 'Platform.Shared.Kernel';

/**
 * 共有カーネルの「内部実装としてのみ」許可される外部パッケージ（計画 ADR-0041。#500）。
 *
 * ADR-0041 決定 2 は「SharedKernel に自前の型を定義し、その内部実装としてのみ外部ライブラリを
 * 使う。Domain / Application / Api / Infrastructure は外部ライブラリの型・名前空間を直接参照して
 * はならない」と定め、決定 3 は「SharedKernel が推移的に持ち込んでよい外部パッケージは Result 型の
 * 実装 **1 つに限る**」と定める。
 *
 * したがって本リストは **BANNED からの除外リストであると同時に、SharedKernel の許可リストでもある**。
 *   - SharedKernel では BANNED から本リスト分を差し引いて判定する（＝ここに挙げたものだけ使える）
 *   - SharedKernel に本リスト外の PackageReference が入ったら違反にする（決定 3）
 *
 * **Result 実装を差し替えるときは、要素を増やすのではなく入れ替える。** 増やすと決定 3 の
 * 「1 つに限る」が崩れ、ADR-0041 が塞ごうとした「SharedKernel は外部依存の抜け道である」という
 * 読みが復活する。増やす必要が生じた場合は ADR-0041 の改定が要る。
 *
 * OneOf は決定 1 が明示的に採らないとしたため、ここには入れない（SharedKernel 内でも違反）。
 */
const SHARED_KERNEL_ALLOWED = ['CSharpFunctionalExtensions'];

/**
 * xUnit v3 と runner の版整合を検査する対象。
 *
 * xunit.runner.visualstudio は v2 用（2.x）と v3 用（3.x）で別系列である。**CPM は 1 パッケージに
 * 1 バージョンしか持てない**ため、系列の食い違ったプロジェクトは非互換の runner と組み合わさる。
 * **両方向を見る**（#455 A-2 で対称化）—— runner 2.x に対する xunit.v3 参照も、runner 3.x に対する
 * xunit（v2 本体）参照も、同じく非互換である。
 *
 * この誤りは通常の CI では捕まらない。ci.yml のビルド対象は src/<unit>/backend/backend.slnx のみで
 * **templates/ は対象外**であり、雛形をコピーして最初のサービスを作った人が dotnet test を走らせて
 * 初めて表面化する（PR #463 のレビューで実際に指摘された）。よって templates/ も走査する。
 */
const XUNIT_V3 = 'xunit.v3';
const XUNIT_V2 = 'xunit';
const XUNIT_RUNNER = 'xunit.runner.visualstudio';
const TEMPLATE_DIR = 'templates';

// --- 純粋ロジック（scripts.test.js から単体テストする） -------------------------

/** posix 区切りへ正規化する。 */
function toPosix(p) {
  return String(p).replace(/\\/g, '/');
}

/**
 * name が禁止対象 banned に該当するか。完全一致、または `banned.` 前方一致。
 * 「Serilog」が「Serilog.AspNetCore」に一致し「SerilogExtras」には一致しないこと（境界）が要点。
 */
function matchesBanned(name, banned) {
  const n = String(name).trim();
  return n === banned || n.startsWith(banned + '.');
}

/** name に該当する禁止ライブラリ名を返す（無ければ null）。最長一致を優先する。 */
function bannedNameOf(name, bannedList = BANNED) {
  let best = null;
  for (const b of bannedList) {
    if (matchesBanned(name, b) && (best === null || b.length > best.length)) best = b;
  }
  return best;
}

/**
 * .csproj / props / targets 本文から PackageReference の Include 値を列挙する。
 *
 * ADR-0030 / #471: CPM の `GlobalPackageReference`（Directory.Packages.props に書くと全プロジェクトへ
 * 参照が注入される）も同じ経路であるため併せて拾う。一方 **`PackageVersion` は拾わない** —
 * こちらは「参照」ではなく版の中央定義であり、baseline 消化まで不採用パッケージの版を正当に持つ
 * （src/Directory.Packages.props / templates の .sample）。要素名を厳密に見分けるのが要点である。
 */
function packageReferencesOf(content) {
  const out = [];
  const re = /<(?:Global)?PackageReference\b[^>]*\bInclude\s*=\s*"([^"]+)"/g;
  let m;
  while ((m = re.exec(String(content))) !== null) out.push(m[1]);
  return out;
}

/** .csproj 本文から ProjectReference の Include 値を列挙する。 */
function projectReferencesOf(content) {
  const out = [];
  const re = /<ProjectReference\b[^>]*\bInclude\s*=\s*"([^"]+)"/g;
  let m;
  while ((m = re.exec(String(content))) !== null) out.push(m[1]);
  return out;
}

/**
 * .cs 本文から using が導入する名前空間を列挙する。
 * `using X;` / `global using X;` / `using static X.Y;` / `using Alias = X.Y;` を扱う。
 * エイリアス形は右辺（実体側）を採る。ブロック構文 `using (var x = ...)` は除外する。
 */
function usingNamespacesOf(content) {
  const out = [];
  const re = /^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?(?:([A-Za-z_][\w]*)[ \t]*=[ \t]*)?([A-Za-z_][\w.]*)[ \t]*;/gm;
  let m;
  while ((m = re.exec(String(content))) !== null) out.push(m[2]);
  return out;
}

/** .csproj 本文から検出した禁止ライブラリ名（重複なし・ソート済み）。 */
function bannedInCsproj(content, bannedList = BANNED) {
  const hits = new Set();
  for (const id of packageReferencesOf(content)) {
    const b = bannedNameOf(id, bannedList);
    if (b) hits.add(b);
  }
  return [...hits].sort();
}

/** .cs 本文から検出した禁止ライブラリ名（重複なし・ソート済み）。 */
function bannedInSource(content, bannedList = BANNED) {
  const hits = new Set();
  for (const ns of usingNamespacesOf(content)) {
    const b = bannedNameOf(ns, bannedList);
    if (b) hits.add(b);
  }
  return [...hits].sort();
}

/** Directory.Packages.props 本文から指定パッケージの中央バージョンを返す（無ければ null）。 */
function centralVersionOf(propsContent, packageId) {
  const re = new RegExp(`<PackageVersion\\b[^>]*\\bInclude\\s*=\\s*"${packageId.replace(/\./g, '\\.')}"[^>]*\\bVersion\\s*=\\s*"([^"]+)"`, 'i');
  const m = re.exec(String(propsContent));
  return m ? m[1] : null;
}

/** バージョン文字列のメジャー番号（数値）。取れなければ null。 */
function majorOf(version) {
  const m = /^(\d+)\./.exec(String(version || ''));
  return m ? Number(m[1]) : null;
}

/**
 * xUnit 本体と CPM の runner の系列が食い違う場合を違反として返す（**両方向**）。
 *
 * runnerVersion が null（CPM に定義が無い＝各 csproj が版を持つ）のときは判定しない。
 * runner を参照しないプロジェクトも判定しない。
 *
 * **両方向を見る理由**（#455 A-2）。当初は「xunit.v3 参照 ＋ runner 2.x」の一方向しか見ていなかった。
 * それは runner が 2.x に固定されていた時代には十分だったが、A-2 で runner を 3.x へ上げた結果、
 * **逆向きの取り残し**——`xunit`（v2 本体）を参照したままのプロジェクト——が同じく非互換になるのに
 * 検出されなくなった。CPM は 1 パッケージ 1 バージョンしか持てないため v2 と v3 は共存できず、
 * **一斉切替でしか成立しない**。その「一斉である」という性質自体をここで機械が担保する。
 * 新しい検査の追加ではなく、既存の検査に欠けていた対称な半分である。
 */
function xunitRunnerMismatch(relPath, csprojContent, runnerVersion) {
  const refs = packageReferencesOf(csprojContent);
  if (!refs.includes(XUNIT_RUNNER)) return [];
  const major = majorOf(runnerVersion);
  if (major === null) return [];

  if (refs.includes(XUNIT_V3) && major < 3) {
    return [{
      kind: 'xunit-runner-mismatch',
      project: toPosix(relPath),
      detail: `${XUNIT_V3} を参照していますが CPM の ${XUNIT_RUNNER} は ${runnerVersion}（v2 系）です`,
    }];
  }
  if (refs.includes(XUNIT_V2) && major >= 3) {
    return [{
      kind: 'xunit-runner-mismatch',
      project: toPosix(relPath),
      detail: `${XUNIT_V2}（v2 本体）を参照していますが CPM の ${XUNIT_RUNNER} は ${runnerVersion}（v3 系）です。`
        + ` v3 の本体は ${XUNIT_V3} です（v2 と v3 は CPM 上共存できないため一斉に切り替える）`,
    }];
  }
  return [];
}

/**
 * 不採用ライブラリ検査の対象となるビルドファイルか（ADR-0030 / #471）。
 *
 * .csproj に加えて MSBuild の props / targets を対象にする。`Directory.Build.props` の
 * `<ItemGroup><PackageReference>` は配下の全プロジェクトへ一括注入されるため、そこに書けば
 * csproj のみの検査は素通りする（`.cs` の using 検査は二次防衛にしかならない。DI 拡張だけを使い
 * `global using` を書かなければ抜ける）。`Directory.Build.targets` も同じ注入経路を持ち、
 * 任意名の `*.props` / `*.targets` は csproj から `<Import>` して同じことができる。
 * templates/ の雛形は実ビルドを避けるため `.sample` 付きで配布されるので末尾の `.sample` も許す。
 */
function isScannedBuildFile(relPath) {
  return /\.(csproj|props|targets)(\.sample)?$/i.test(toPosix(relPath));
}

/** リポジトリ相対パスが Domain プロジェクトの .csproj か。 */
function isDomainProject(relPath) {
  return /\.Domain\.csproj$/i.test(toPosix(relPath));
}

/**
 * リポジトリ相対パスが共有カーネルの .csproj か（ADR-0041。#500）。
 *
 * **判定はプロジェクト名で行い、ディレクトリ階層では行わない。** ADR-0041 決定 2 が名指しするのは
 * 「SharedKernel」という**プロジェクト**であり、同じ Shared/ 配下にある Platform.Shared.Contracts /
 * Platform.Shared.Infrastructure は対象外だからである。階層で判定すると、それらにも外部 Result
 * ライブラリが入れるようになり封じ込めが壊れる。
 */
function isSharedKernelProject(relPath) {
  return path.basename(toPosix(relPath)).replace(/\.csproj$/i, '') === SHARED_KERNEL;
}

/**
 * 当該プロジェクトに適用する BANNED を返す（ADR-0041 決定 2。#500）。
 *
 * 共有カーネルでは許可リスト分を差し引く。**BANNED から恒久的に外すのではなく、ここでだけ
 * 差し引く**のが要点である。恒久的に外すと、SharedKernel 以外での直接参照（決定 2 が禁じるもの）が
 * 素通りしてしまう。
 *
 * projPath は .csproj のリポジトリ相対パス。.cs は owningProject() が解決した所属 csproj を渡す。
 */
function bannedListFor(projPath) {
  if (!isSharedKernelProject(projPath)) return BANNED;
  return BANNED.filter((b) => !SHARED_KERNEL_ALLOWED.includes(b));
}

/**
 * 共有カーネルの依存規律違反を返す（ADR-0041 決定 3。#500）。
 *
 * 決定 3 は「SharedKernel が推移的に持ち込んでよい外部パッケージは Result 型の実装 1 つに限る。
 * **この 1 つ以外を SharedKernel へ追加してはならない**」と定める。Domain は SharedKernel だけを
 * ProjectReference できる（domainViolations）ため、**SharedKernel の PackageReference が
 * そのまま Domain の推移的な外部依存になる**。したがって許可リスト外は 1 件でも違反とする。
 *
 * これは BANNED の判定とは独立である —— BANNED に載っていない任意のパッケージ
 * （例: Npgsql）でも、SharedKernel へ入れば違反になる。
 */
function sharedKernelViolations(relPath, content, owner = relPath) {
  if (!isSharedKernelProject(owner)) return [];
  const out = [];
  for (const id of packageReferencesOf(content)) {
    if (SHARED_KERNEL_ALLOWED.includes(id)) continue;
    out.push({ kind: 'shared-kernel-package', project: toPosix(relPath), detail: id });
  }
  // 決定 3 は「**推移的に**持ち込んでよい外部パッケージは 1 つに限る」「静的解析は許可リストで
  // **Domain の推移閉包**を検査する」と書いている。PackageReference だけを見ると 1 段しか見ておらず、
  // Domain → Kernel → 任意のプロジェクト(Npgsql 等) という経路が素通りする（実測で再現。#500 監査）。
  // 共有カーネルは Result / Error・共通基底を置く最下層であり（IADR-0117 決定 1）、
  // 他プロジェクトを参照する理由が無い。よって ProjectReference は 0 件に固定する。
  for (const ref of projectReferencesOf(content)) {
    out.push({ kind: 'shared-kernel-project', project: toPosix(relPath), detail: ref });
  }
  return out;
}

/**
 * Domain 層の依存規律違反を返す。PackageReference は 1 件でも違反。
 * ProjectReference は共有カーネル以外を違反とする。
 */
function domainViolations(relPath, content) {
  if (!isDomainProject(relPath)) return [];
  const out = [];
  for (const id of packageReferencesOf(content)) {
    out.push({ kind: 'domain-package', project: toPosix(relPath), detail: id });
  }
  for (const ref of projectReferencesOf(content)) {
    const base = path.basename(toPosix(ref)).replace(/\.csproj$/i, '');
    if (base !== SHARED_KERNEL) {
      out.push({ kind: 'domain-project', project: toPosix(relPath), detail: ref });
    }
  }
  return out;
}

/**
 * 現状（プロジェクト → 禁止ライブラリ名の配列）と baseline を突き合わせ、3 分類して返す。
 *   added   : baseline に無い（プロジェクトごと新規 / 既知プロジェクトでの新ライブラリ）→ fail
 *   known   : baseline どおり残っている → warn
 *   stale   : baseline にあるが現状に無い（減らし忘れ）→ fail
 */
function classifyAgainstBaseline(current, baseline) {
  const added = [];
  const known = [];
  const stale = [];
  const projects = new Set([...Object.keys(current), ...Object.keys(baseline)]);
  for (const project of [...projects].sort()) {
    const cur = new Set(current[project] || []);
    const base = new Set(baseline[project] || []);
    for (const lib of [...cur].sort()) {
      if (base.has(lib)) known.push({ project, lib });
      else added.push({ project, lib });
    }
    for (const lib of [...base].sort()) {
      if (!cur.has(lib)) stale.push({ project, lib });
    }
  }
  return { added, known, stale };
}

// --- ファイル走査 ---------------------------------------------------------------

/**
 * dir 配下を再帰的に走査し、条件に合うファイルの相対パスを返す。
 * root は走査の起点（既定はリポジトリルート）。自己試験が一時ツリーを走査するために外から与える（#471）。
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
 * .cs ファイルが属するプロジェクト（最も近い祖先の .csproj のディレクトリ相対パス）を返す。
 * 見つからなければ null。
 */
function owningProject(relCsPath, csprojPaths) {
  const p = toPosix(relCsPath);
  let best = null;
  for (const proj of csprojPaths) {
    const dir = toPosix(path.dirname(proj)) + '/';
    if (p.startsWith(dir) && (best === null || dir.length > best.dir.length)) {
      best = { dir, proj };
    }
  }
  return best ? best.proj : null;
}

/**
 * src/ を走査し、プロジェクト → 禁止ライブラリ名配列 と Domain 違反を返す。
 * root は走査の起点（既定はリポジトリルート。自己試験が一時ツリーを与える。#471）。
 */
function scanTree(root = REPO_ROOT) {
  // #471: csproj だけでなく props / targets も走査する（Directory.Build.props 経由の一括注入対策）。
  const buildFiles = walk(SRC_DIR, (p) => isScannedBuildFile(p) && !isExcludedPath(p), [], root);
  // .cs の所属プロジェクト解決に使うのは .csproj のみ（props は「プロジェクト」ではない）。
  const csprojPaths = buildFiles.filter((p) => /\.csproj$/i.test(p));
  const current = {};
  const domain = [];
  const add = (project, libs) => {
    if (!libs.length) return;
    const set = new Set(current[project] || []);
    for (const l of libs) set.add(l);
    current[project] = [...set].sort();
  };

  // CPM の runner 版（xunit.v3 との整合判定に使う）。
  let runnerVersion = null;
  try {
    runnerVersion = centralVersionOf(fs.readFileSync(path.join(root, 'src', 'Directory.Packages.props'), 'utf8'), XUNIT_RUNNER);
  } catch { /* CPM を読めない構成では版整合の判定を行わない */ }

  for (const proj of buildFiles) {
    const content = fs.readFileSync(path.join(root, proj), 'utf8');
    // props / targets は「置かれた場所の所属プロジェクト」で判定する（.cs と同じ扱い。#500 / AI レビュー指摘）。
    // csproj だけで判定すると、共有カーネルの PackageReference を隣の .props へ移すだけで
    // (a) 許可パッケージが誤検出され、(b) 決定 3 の許可リスト検査が丸ごと素通りする（実測）。
    // #471 が Directory.Build.props を実在の混入経路として実測しており、props は現実的な抜け道である。
    // src/Directory.Build.props のようにどのプロジェクトにも属さないものは owner=自身となり、
    // 共有カーネル扱いにならない（＝従来どおり BANNED 全量で判定される）。
    const owner = /\.csproj$/i.test(proj) ? proj : (owningProject(proj, csprojPaths) || proj);
    add(proj, bannedInCsproj(content, bannedListFor(owner)));
    domain.push(...domainViolations(proj, content));
    domain.push(...sharedKernelViolations(proj, content, owner));
    domain.push(...xunitRunnerMismatch(proj, content, runnerVersion));
  }
  // templates/ は ci.yml のビルド対象外のため、ここで走査しないと雛形の版不整合が誰にも捕まらない。
  // 不採用ライブラリの baseline 対象にはしない（雛形は src/ の残件ではないため）。
  // #471: 雛形も props / targets（.sample 付きで配布される）まで走査する。
  for (const proj of walk(TEMPLATE_DIR, isScannedBuildFile, [], root)) {
    const content = fs.readFileSync(path.join(root, proj), 'utf8');
    domain.push(...domainViolations(proj, content));
    domain.push(...sharedKernelViolations(proj, content));
    domain.push(...xunitRunnerMismatch(proj, content, runnerVersion));
    const banned = bannedInCsproj(content, bannedListFor(proj));
    if (banned.length) {
      domain.push({ kind: 'template-banned', project: toPosix(proj), detail: banned.join(' / ') });
    }
  }
  // 規則 4・5(a) は**ファイル単位**の規律であり、プロジェクトの所属を問わない。
  // 規則 1（不採用ライブラリの using）だけが「その .cs がどのプロジェクトに属するか」を要する。
  //
  // 🔴 **走査範囲は src/ と templates/ の両方である。**（#897 の監査が実測で 2 つの穴を挙げた）
  //   - `templates/` は新サービスの出発点であり、**複製される**。規則 1 が既に
  //     「雛形へ不採用ライブラリを持ち込ませない」を強制しているのと同じ理由で、
  //     禁止 API・封じ込め API も雛形で止める。
  //   - `.csproj` にオーナーが無い `.cs`（孤児）も規則 4・5(a) の対象にする。
  //     従来は `if (!proj) continue;` で規則 1 と一緒に飛ばしており、
  //     「検査対象はプロジェクトグラフが偶然拾ったファイル」になっていた。
  const csRoots = [SRC_DIR, TEMPLATE_DIR];
  for (const dir of csRoots) {
    for (const cs of walk(dir, (p) => /\.cs$/i.test(p) && !isExcludedPath(p), [], root)) {
      const content = fs.readFileSync(path.join(root, cs), 'utf8');
      const proj = owningProject(cs, csprojPaths);
      if (proj) {
        // using の許否は「その .cs がどのプロジェクトに属するか」で決まる（ADR-0041 決定 2。#500）。
        add(proj, bannedInSource(content, bannedListFor(proj)));
      }
      // 規則 4: 禁止 API シンボル。**プロジェクトではなくファイルを名指しする**
      // （1 行の混入であり、どのファイルかが分からないと直せない）。
      domain.push(...forbiddenApiViolations(cs, content));
      // 規則 5(a): 封じ込め API が許可ファイルの外で使われていないか（ADR-0027 手順 6）。
      domain.push(...confinedApiViolations(cs, content));
    }
  }

  // 規則 5(b): 封じ込め API が本拠から消えていないか。**(a) だけでは静かに no-op になる。**
  // 本拠のファイルごと消えた場合も、全シンボルが欠けた扱いで報告される（fail-closed）。
  const homeAbs = path.join(root, CONFINED_API_HOME);
  const homeContent = fs.existsSync(homeAbs) ? fs.readFileSync(homeAbs, 'utf8') : '';
  for (const missing of confinedApiHomeGaps(homeContent)) {
    domain.push({ kind: 'confined-api-missing', project: CONFINED_API_HOME, detail: missing });
  }
  return { current, domain };
}

/** baseline を読む。無ければ空。 */
function readBaseline() {
  try {
    return JSON.parse(fs.readFileSync(BASELINE_FILE, 'utf8')).projects || {};
  } catch {
    return {};
  }
}

// --- 自己試験 -------------------------------------------------------------------

function selfTest() {
  const cases = [];
  const t = (name, pass, actual) => cases.push({ name, pass, actual });

  // 境界: 前方一致は「.」区切りのときだけ効く。
  t('Serilog は Serilog.AspNetCore に一致する', matchesBanned('Serilog.AspNetCore', 'Serilog'));
  t('Serilog は SerilogExtras に一致しない', !matchesBanned('SerilogExtras', 'Serilog'));
  t('MassTransit は MassTransit.RabbitMQ に一致する', matchesBanned('MassTransit.RabbitMQ', 'MassTransit'));
  t('bannedNameOf は非該当で null', bannedNameOf('Npgsql.EntityFrameworkCore.PostgreSQL') === null);

  // csproj の抽出。
  t('PackageReference から禁止パッケージを検出',
    JSON.stringify(bannedInCsproj('<PackageReference Include="MassTransit.RabbitMQ" />')) === '["MassTransit"]');
  t('Version 属性が先にあっても Include を拾う',
    JSON.stringify(bannedInCsproj('<PackageReference Version="1.0" Include="FluentAssertions" />')) === '["FluentAssertions"]');
  t('採用ライブラリのみなら空',
    bannedInCsproj('<PackageReference Include="FluentValidation" />').length === 0);

  // BANNED の網羅（ADR-0030 / #471）。棚卸し表の ★不採用・置換対象と 1 件ずつ突き合わせた分。
  t('Kiota: 実在 ID は Microsoft.Kiota.* — 旧 "Kiota" は 1 件も当たらない死にエントリだった',
    bannedNameOf('Microsoft.Kiota.Abstractions') === 'Microsoft.Kiota'
      && bannedNameOf('Microsoft.Kiota.Serialization.Json') === 'Microsoft.Kiota'
      && !BANNED.includes('Kiota'));
  t('Confluent.Kafka は不採用（Wolverine トランスポート経由が標準）だが WolverineFx.Kafka は採用',
    bannedNameOf('Confluent.Kafka') === 'Confluent.Kafka'
      && bannedNameOf('WolverineFx.Kafka') === null);
  t('素の RabbitMQ.Client は不採用（WolverineFx.RabbitMQ が置換）',
    bannedNameOf('RabbitMQ.Client') === 'RabbitMQ.Client'
      && bannedNameOf('WolverineFx.RabbitMQ') === null);
  t('Azure Key Vault の構成プロバイダ / クライアント SDK は不採用（HashiCorp Vault を使う）',
    bannedNameOf('Azure.Extensions.AspNetCore.Configuration.Secrets') === 'Azure.Extensions.AspNetCore.Configuration.Secrets'
      && bannedNameOf('Azure.Security.KeyVault.Secrets') === 'Azure.Security.KeyVault'
      && bannedNameOf('Azure.Security.KeyVault.Keys') === 'Azure.Security.KeyVault');
  t('Azure Key Vault 以外の Azure SDK（Azure.Identity 等）は不採用ではない',
    bannedNameOf('Azure.Identity') === null && bannedNameOf('Azure.Core') === null);
  t('Argon2 実装は不採用（認証は Keycloak が担う）',
    bannedNameOf('Konscious.Security.Cryptography.Argon2') === 'Konscious.Security.Cryptography.Argon2'
      && bannedNameOf('Isopoh.Cryptography.Argon2') === 'Isopoh.Cryptography.Argon2');
  t('Argon2 と同一作者の別用途パッケージ（Blake2）は巻き込まない',
    bannedNameOf('Konscious.Security.Cryptography.Blake2') === null
      && bannedNameOf('Isopoh.Cryptography.Blake2b') === null);

  // #471 の最重要の罠: CPM の PackageVersion を違反にしてはならない。
  // Directory.Packages.props は baseline 消化まで不採用パッケージの版定義を正当に持つ。
  t('PackageVersion（CPM の版定義）は違反にしない',
    bannedInCsproj('<PackageVersion Include="MassTransit" Version="8.4.1" />'
      + '<PackageVersion Include="Serilog.AspNetCore" Version="10.0.0" />'
      + '<PackageVersion Include="FluentAssertions" Version="7.2.0" />').length === 0);
  t('GlobalPackageReference（CPM の全プロジェクト注入）は違反にする',
    JSON.stringify(bannedInCsproj('<GlobalPackageReference Include="Serilog" Version="4.0.0" />')) === '["Serilog"]');
  t('実リポの src/Directory.Packages.props は不採用の PackageVersion を持つが違反 0',
    bannedInCsproj(fs.readFileSync(path.join(REPO_ROOT, 'src', 'Directory.Packages.props'), 'utf8')).length === 0);
  t('雛形の Directory.Packages.props.sample も同様に違反 0',
    bannedInCsproj(fs.readFileSync(
      path.join(REPO_ROOT, 'templates', 'unit-template', 'backend', 'Directory.Packages.props.sample'), 'utf8')).length === 0);

  // 走査対象のファイル種別（#471）。
  t('isScannedBuildFile: csproj / props / targets と雛形の .sample を対象にする',
    isScannedBuildFile('src/x/X.csproj') && isScannedBuildFile('src/Directory.Build.props')
      && isScannedBuildFile('src/Directory.Build.targets') && isScannedBuildFile('src/x/Custom.props')
      && isScannedBuildFile('templates/unit-template/backend/Directory.Packages.props.sample'));
  t('isScannedBuildFile: .cs や無関係なファイルは対象外',
    !isScannedBuildFile('src/x/X.cs') && !isScannedBuildFile('src/x/backend.slnx')
      && !isScannedBuildFile('src/x/README.md'));

  // using の抽出。
  t('using MassTransit; を検出',
    JSON.stringify(bannedInSource('using MassTransit;\n')) === '["MassTransit"]');
  t('global using / static / エイリアスも検出',
    JSON.stringify(bannedInSource('global using Serilog;\nusing static FluentAssertions.AssertionExtensions;\nusing M = MassTransit.IBus;\n'))
      === '["FluentAssertions","MassTransit","Serilog"]');
  t('using ブロック構文は名前空間として拾わない',
    bannedInSource('using (var x = new MassTransitThing()) { }\n').length === 0);
  t('コメント行内の語は using 形でなければ拾わない',
    bannedInSource('// MassTransit をやめる\n').length === 0);

  // Domain 層の依存規律。
  t('Domain の PackageReference は違反',
    domainViolations('src/platform/backend/X.Domain.csproj', '<PackageReference Include="FluentValidation" />').length === 1);
  t('Domain の共有カーネル参照は許可',
    domainViolations('src/platform/backend/X.Domain.csproj',
      '<ProjectReference Include="../../Shared/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj" />').length === 0);
  t('Domain の共有カーネル以外の ProjectReference は違反',
    domainViolations('src/platform/backend/X.Domain.csproj',
      '<ProjectReference Include="../X.Infrastructure/X.Infrastructure.csproj" />').length === 1);
  t('Domain 以外の csproj は本検査の対象外',
    domainViolations('src/platform/backend/X.Api.csproj', '<PackageReference Include="FluentValidation" />').length === 0);

  // ratchet の 3 判定。
  {
    const r = classifyAgainstBaseline({ 'a.csproj': ['MassTransit'] }, {});
    t('baseline に無い違反は added（fail 対象）', r.added.length === 1 && r.known.length === 0 && r.stale.length === 0);
  }
  {
    const r = classifyAgainstBaseline({ 'a.csproj': ['MassTransit'] }, { 'a.csproj': ['MassTransit'] });
    t('baseline どおりの違反は known（warn）', r.known.length === 1 && r.added.length === 0 && r.stale.length === 0);
  }
  {
    const r = classifyAgainstBaseline({}, { 'a.csproj': ['MassTransit'] });
    t('違反が消えたのに baseline に残るのは stale（fail 対象）', r.stale.length === 1 && r.added.length === 0);
  }
  {
    const r = classifyAgainstBaseline({ 'a.csproj': ['MassTransit', 'Serilog'] }, { 'a.csproj': ['MassTransit'] });
    t('既知プロジェクトでの新ライブラリ追加も added', r.added.length === 1 && r.added[0].lib === 'Serilog' && r.known.length === 1);
  }

  // 所属プロジェクトの解決（最も深い .csproj を採る）。
  t('owningProject は最も近い祖先の csproj を返す',
    owningProject('src/a/b/c/X.cs', ['src/a/A.csproj', 'src/a/b/B.csproj']) === 'src/a/b/B.csproj');
  t('owningProject は配下に無ければ null',
    owningProject('src/z/X.cs', ['src/a/A.csproj']) === null);

  // xUnit v3 と CPM の runner 版の整合（PR #463 レビュー指摘の回帰防止）。
  const V3_PROJ = '<PackageReference Include="xunit.v3" /><PackageReference Include="xunit.runner.visualstudio" />';
  const V2_PROJ = '<PackageReference Include="xunit" /><PackageReference Include="xunit.runner.visualstudio" />';
  t('centralVersionOf: CPM から版を取り出す',
    centralVersionOf('<PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />', 'xunit.runner.visualstudio') === '2.8.2');
  t('centralVersionOf: 未定義は null',
    centralVersionOf('<PackageVersion Include="xunit" Version="2.9.3" />', 'xunit.runner.visualstudio') === null);
  t('majorOf: メジャー番号', majorOf('2.8.2') === 2 && majorOf('3.1.5') === 3 && majorOf('') === null);
  t('xunit.v3 ＋ runner 2.x は違反（雛形で実際に作り込んだ不整合）',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V3_PROJ, '2.8.2').length === 1);
  t('xunit.v3 ＋ runner 3.x は適合',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V3_PROJ, '3.1.5').length === 0);
  t('xunit（v2）＋ runner 2.x は適合',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V2_PROJ, '2.8.2').length === 0);
  t('★ xunit（v2）＋ runner 3.x は違反（#455 A-2 で対称化。一斉切替の取り残しを捕まえる）',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V2_PROJ, '3.1.5').length === 1);
  t('xunit（v2）で runner を参照しなければ判定しない',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', '<PackageReference Include="xunit" />', '3.1.5').length === 0);
  t('xunit.v3 の前方一致で v2 を誤検出しない（v3 のみ参照 ＋ runner 3.x は適合）',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V3_PROJ, '3.1.5').length === 0);
  t('runner を参照しなければ判定しない（v3 単体は自己実行できる）',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', '<PackageReference Include="xunit.v3" />', '2.8.2').length === 0);
  t('CPM に runner 定義が無ければ判定しない',
    xunitRunnerMismatch('templates/x/X.Tests.csproj', V3_PROJ, null).length === 0);

  // 検査対象ユニットの切り分け（別プロジェクトの submodule は対象外）。
  t('ai-stock-trading 配下は検査対象外',
    isExcludedPath('src/ai-stock-trading/backend/Services/X/src/X.Api/X.Api.csproj'));
  t('platform は検査対象', !isExcludedPath('src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj'));
  t('knowledge は検査対象', !isExcludedPath('src/knowledge/backend/Shared/Knowledge.Contracts/Knowledge.Contracts.csproj'));
  t('src 直下のファイルは対象外扱いにしない', !isExcludedPath('src/Directory.Packages.props'));

  // --- 実地確認（#471）: 一時ツリーを実際に走査し、3 種の検出漏れ / 偽陽性を固定する ---
  // 関数単位の試験だけでは「走査対象に入っているか」を確かめられない（BANNED に足しても
  // scanTree が当該ファイルを開かなければ検出されない）。よって scanTree ごと通す。
  {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'backend-libs-selftest-'));
    const write = (rel, body) => {
      const abs = path.join(tmp, rel);
      fs.mkdirSync(path.dirname(abs), { recursive: true });
      fs.writeFileSync(abs, body);
    };
    // (a) 実在する Kiota のパッケージ ID。旧 BANNED の 'Kiota' では検出できなかった。
    write('src/platform/backend/Sample/Sample.Api.csproj',
      '<Project><ItemGroup><PackageReference Include="Microsoft.Kiota.Abstractions" Version="1.0.0" /></ItemGroup></Project>');
    // (b) Directory.Build.props 経由の一括注入。csproj には一切現れない混入経路。
    write('src/Directory.Build.props',
      '<Project><ItemGroup><PackageReference Include="MassTransit" /></ItemGroup></Project>');
    // (c) CPM の版定義。走査対象に加えても違反にしてはならない（baseline 消化まで正当に残る）。
    write('src/Directory.Packages.props',
      '<Project><ItemGroup>'
      + '<PackageVersion Include="MassTransit" Version="8.4.1" />'
      + '<PackageVersion Include="Serilog.AspNetCore" Version="10.0.0" />'
      + '<PackageVersion Include="FluentAssertions" Version="7.2.0" />'
      + '</ItemGroup></Project>');
    const { current } = scanTree(tmp);
    t('実地(a): csproj の Microsoft.Kiota.Abstractions を検出',
      JSON.stringify(current['src/platform/backend/Sample/Sample.Api.csproj']) === '["Microsoft.Kiota"]',
      current['src/platform/backend/Sample/Sample.Api.csproj']);
    t('実地(b): Directory.Build.props 経由の一括注入を検出',
      JSON.stringify(current['src/Directory.Build.props']) === '["MassTransit"]',
      current['src/Directory.Build.props']);
    t('実地(c): CPM の PackageVersion は違反にならない（走査追加で偽陽性を出さない）',
      current['src/Directory.Packages.props'] === undefined, current['src/Directory.Packages.props']);
    fs.rmSync(tmp, { recursive: true, force: true });
  }

  // --- 単位判定（ADR-0041。#500）------------------------------------------------
  t('SharedKernel の csproj を名前で判定する',
    isSharedKernelProject('src/platform/backend/Shared/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj'));
  t('同じ Shared/ 配下でも Contracts は SharedKernel ではない',
    !isSharedKernelProject('src/platform/backend/Shared/Platform.Shared.Contracts/Platform.Shared.Contracts.csproj'));
  t('SharedKernel では CSharpFunctionalExtensions が BANNED から外れる',
    !bannedListFor('x/Platform.Shared.Kernel.csproj').includes('CSharpFunctionalExtensions'));
  t('SharedKernel でも OneOf は BANNED のまま（ADR-0041 決定 1）',
    bannedListFor('x/Platform.Shared.Kernel.csproj').includes('OneOf'));
  t('SharedKernel 以外では CSharpFunctionalExtensions は BANNED のまま',
    bannedListFor('x/Foo.Domain.csproj').includes('CSharpFunctionalExtensions'));
  t('BANNED 本体からは外していない（他プロジェクトの直接参照を素通りさせない）',
    BANNED.includes('CSharpFunctionalExtensions'));
  t('許可リストは 1 件のみ（ADR-0041 決定 3「1 つに限る」）',
    SHARED_KERNEL_ALLOWED.length === 1 && SHARED_KERNEL_ALLOWED[0] === 'CSharpFunctionalExtensions',
    SHARED_KERNEL_ALLOWED);

  // --- 実地確認（ADR-0041。#500）------------------------------------------------
  // 一時ツリーを組んで正例・負例を実測する。**実体が在る今も一時ツリーで測る** ——
  // 実リポジトリの状態に依存させると、SharedKernel の中身が変わるたび自己試験の意味が変わるためである
  // （#500 の初版は「未作成だから」を理由にしていたが、#455 で実体ができた後もこの作法は変えない）。
  // 判定は「BANNED からの差し引き」と「許可リスト外の検出」の 2 系統に分かれるため、両方を通す。
  {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'backend-libs-kernel-'));
    const write = (rel, body) => {
      const abs = path.join(tmp, rel);
      fs.mkdirSync(path.dirname(abs), { recursive: true });
      fs.writeFileSync(abs, body);
    };
    const K = 'src/platform/backend/Shared/Platform.Shared.Kernel';
    // 正例: SharedKernel は Result 実装 1 つだけを持つ。
    write(`${K}/Platform.Shared.Kernel.csproj`,
      '<Project><ItemGroup><PackageReference Include="CSharpFunctionalExtensions" /></ItemGroup></Project>');
    // 正例: SharedKernel 配下の .cs は外部ライブラリを using してよい（内部実装）。
    // **変異**: 同じファイルに、許可リストに無い BANNED（MassTransit）も置く。
    // これが検出されなければ「.cs が走査されていない / 所属プロジェクトを解決できていない」ため、
    // 上の正例は**空振りで通っている**ことになる。#471 が記録した型の再発防止である。
    write(`${K}/Result.cs`,
      'using CSharpFunctionalExtensions;\nusing MassTransit;\nnamespace Platform.Shared.Kernel;\n');
    // 負例 1: Domain の csproj が直接参照する（決定 2 が禁じる）。
    write('src/platform/backend/Sample/Sample.Domain.csproj',
      '<Project><ItemGroup><PackageReference Include="CSharpFunctionalExtensions" /></ItemGroup></Project>');
    // 負例 2: Application の .cs が直接 using する（決定 2 が禁じる）。
    write('src/platform/backend/Sample/Sample.Application.csproj', '<Project></Project>');
    write('src/platform/backend/Sample/Handler.cs', 'using CSharpFunctionalExtensions;\n');
    const { current, domain } = scanTree(tmp);

    // 変異試験（上記 write の意図）: SharedKernel の .cs は「走査されたうえで」
    // CSharpFunctionalExtensions だけが免除され、MassTransit は検出される、が期待。
    // 免除が効いていなければ 2 件、走査されていなければ 0 件になり、どちらも本試験で落ちる。
    t('実地(ADR-0041 正例・変異): SharedKernel の .cs は走査され、許可分だけが免除される',
      JSON.stringify(current[`${K}/Platform.Shared.Kernel.csproj`]) === '["MassTransit"]',
      current[`${K}/Platform.Shared.Kernel.csproj`]);
    t('実地(ADR-0041 負例 1): Domain の直接参照は検出する',
      (current['src/platform/backend/Sample/Sample.Domain.csproj'] || []).includes('CSharpFunctionalExtensions'),
      current['src/platform/backend/Sample/Sample.Domain.csproj']);
    t('実地(ADR-0041 負例 2): Application の using は検出する',
      (current['src/platform/backend/Sample/Sample.Application.csproj'] || []).includes('CSharpFunctionalExtensions'),
      current['src/platform/backend/Sample/Sample.Application.csproj']);
    t('実地(ADR-0041): 正例の SharedKernel は決定 3 の違反を出さない',
      domain.filter((d) => d.kind === 'shared-kernel-package').length === 0,
      domain.filter((d) => d.kind === 'shared-kernel-package'));
    fs.rmSync(tmp, { recursive: true, force: true });
  }

  // 決定 3: 許可リスト外が SharedKernel へ入ったら失敗する。BANNED 掲載の有無に関わらず効くことを、
  // 「BANNED の OneOf」と「BANNED に無い Npgsql」の 2 系統で確かめる。
  {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'backend-libs-kernel-neg-'));
    const abs = path.join(tmp, 'src/platform/backend/Shared/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj');
    fs.mkdirSync(path.dirname(abs), { recursive: true });
    fs.writeFileSync(abs,
      '<Project><ItemGroup>'
      + '<PackageReference Include="CSharpFunctionalExtensions" />'
      + '<PackageReference Include="OneOf" />'
      + '<PackageReference Include="Npgsql" />'
      + '</ItemGroup></Project>');
    const { current, domain } = scanTree(tmp);
    const rel = 'src/platform/backend/Shared/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj';
    const kernelViolations = domain.filter((d) => d.kind === 'shared-kernel-package').map((d) => d.detail).sort();
    t('実地(ADR-0041 決定 3): 許可リスト外の 2 件を検出し、許可された 1 件は出さない',
      JSON.stringify(kernelViolations) === '["Npgsql","OneOf"]', kernelViolations);
    t('実地(ADR-0041 決定 3): BANNED に無い Npgsql も SharedKernel では違反になる',
      kernelViolations.includes('Npgsql') && !BANNED.includes('Npgsql'));
    t('実地(ADR-0041 決定 1): SharedKernel の OneOf は BANNED 側でも検出される',
      (current[rel] || []).includes('OneOf'), current[rel]);
    fs.rmSync(tmp, { recursive: true, force: true });
  }

  // props / targets 経由の抜け道（#500 / AI レビュー指摘を実測して確認したもの）。
  // csproj だけで共有カーネルを判定していたときは、隣に .props を置くだけで
  // (a) 許可パッケージが誤検出され、(b) 決定 3 の検査が素通りした。両方を固定する。
  {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'backend-libs-kernel-props-'));
    const w = (rel, body) => {
      const abs = path.join(tmp, rel);
      fs.mkdirSync(path.dirname(abs), { recursive: true });
      fs.writeFileSync(abs, body);
    };
    const K = 'src/platform/backend/Shared/Platform.Shared.Kernel';
    w(`${K}/Platform.Shared.Kernel.csproj`, '<Project></Project>');
    // (a) 許可パッケージを .props 側で宣言する。
    w(`${K}/Platform.Shared.Kernel.props`,
      '<Project><ItemGroup><PackageReference Include="CSharpFunctionalExtensions" /></ItemGroup></Project>');
    // (b) 許可リスト外を .props 側で持ち込む（BANNED 非掲載のもので試す）。
    w(`${K}/Extra.props`, '<Project><ItemGroup><PackageReference Include="Npgsql" /></ItemGroup></Project>');
    // (c) どのプロジェクトにも属さない props は共有カーネル扱いにしない（#471 の経路を殺さない）。
    w('src/Directory.Build.props',
      '<Project><ItemGroup><PackageReference Include="CSharpFunctionalExtensions" /></ItemGroup></Project>');
    const { current, domain } = scanTree(tmp);
    const kernelViolations = domain.filter((d) => d.kind === 'shared-kernel-package').map((d) => d.detail);

    t('props(a): 共有カーネル配下の .props の許可パッケージは誤検出しない',
      current[`${K}/Platform.Shared.Kernel.props`] === undefined,
      current[`${K}/Platform.Shared.Kernel.props`]);
    t('props(b): 共有カーネル配下の .props の許可リスト外は決定 3 違反になる',
      JSON.stringify(kernelViolations) === '["Npgsql"]', kernelViolations);
    t('props(c): どのプロジェクトにも属さない Directory.Build.props は免除しない（#471 の経路を残す）',
      JSON.stringify(current['src/Directory.Build.props']) === '["CSharpFunctionalExtensions"]',
      current['src/Directory.Build.props']);
    fs.rmSync(tmp, { recursive: true, force: true });
  }

  // 決定 3 の「推移閉包」（#500 監査 🔴-1）。Domain → Kernel → 他プロジェクト の経路を塞ぐ。
  // PackageReference だけを見ていたときは、この経路が違反 0 件で素通りした（監査が実測）。
  {
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'backend-libs-kernel-transitive-'));
    const w = (rel, body) => {
      const abs = path.join(tmp, rel);
      fs.mkdirSync(path.dirname(abs), { recursive: true });
      fs.writeFileSync(abs, body);
    };
    const S = 'src/platform/backend/Shared';
    w(`${S}/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj`,
      '<Project><ItemGroup>'
      + '<PackageReference Include="CSharpFunctionalExtensions" />'
      + '<ProjectReference Include="../Platform.Shared.Infrastructure/Platform.Shared.Infrastructure.csproj" />'
      + '</ItemGroup></Project>');
    w(`${S}/Platform.Shared.Infrastructure/Platform.Shared.Infrastructure.csproj`,
      '<Project><ItemGroup><PackageReference Include="Npgsql" /></ItemGroup></Project>');
    const { domain } = scanTree(tmp);
    const refs = domain.filter((d) => d.kind === 'shared-kernel-project').map((d) => d.detail);
    t('推移(ADR-0041 決定 3): SharedKernel の ProjectReference は違反になる（Domain の推移的依存を塞ぐ）',
      refs.length === 1 && /Platform\.Shared\.Infrastructure/.test(refs[0]), refs);
    t('推移(ADR-0041 決定 3): 許可された PackageReference は同時に違反にならない',
      domain.filter((d) => d.kind === 'shared-kernel-package').length === 0,
      domain.filter((d) => d.kind === 'shared-kernel-package'));
    fs.rmSync(tmp, { recursive: true, force: true });
  }

  // --- 規則 4: 禁止 API シンボル（ADR-0027 手順 3・手順 6。#455） ---
  t('PrefixIdentifiers の呼び出しを検出する',
    forbiddenApiViolations('a/B.cs', 'cfg.PrefixIdentifiers();').length === 1);
  t('検出はファイルを名指しする（プロジェクトではなく）',
    forbiddenApiViolations('a/B.cs', 'cfg.PrefixIdentifiers();')[0].project === 'a/B.cs');
  t('★ 部分一致では検出しない（識別子の境界で照合する。偽陽性は検査を無視する学習を生む）',
    forbiddenApiViolations('a/B.cs', 'var myPrefixIdentifiersFoo = 1;').length === 0
      && forbiddenApiViolations('a/B.cs', 'XPrefixIdentifiers();').length === 0);
  t('コメント中の言及も検出する（「書いてから外す」経路を許さない）',
    forbiddenApiViolations('a/B.cs', '// TODO: PrefixIdentifiers を検討').length === 1);
  t('禁止 API を含まなければ 0 件',
    forbiddenApiViolations('a/B.cs', 'cfg.UsePlatformRetry();').length === 0);
  t('★ 手順 4・5 の API は禁止側に入れない（極性が逆。共通ヘルパに在るべきもの）',
    !FORBIDDEN_APIS.some((f) => f.symbol === 'DisableConventionalLocalRouting')
      && !FORBIDDEN_APIS.some((f) => f.symbol === 'ServiceLocationPolicy'));
  t('禁止 API には理由が必ず付く（メッセージだけ見て直せるように）',
    FORBIDDEN_APIS.every((f) => typeof f.why === 'string' && f.why.length > 0));

  // --- 規則 5: 封じ込め API（ADR-0027 手順 3・4・5 を手順 6 に従って 1 箇所へ閉じる。#455 U4） ---
  const HOME = CONFINED_API_HOME;
  const ALLOWED_TEST_FILE = CONFINED_API_ALLOWED[1];
  t('許可ファイルの外での使用を検出する',
    confinedApiViolations('src/knowledge/x/Program.cs',
      'opts.Policies.DisableConventionalLocalRouting();').length === 1);
  t('検出はファイルを名指しする',
    confinedApiViolations('src/knowledge/x/Program.cs', 'cfg.ListenToRabbitQueue("q");')[0]
      .project === 'src/knowledge/x/Program.cs');
  t('★ 本拠（共通ヘルパ）では検出しない',
    confinedApiViolations(HOME,
      'options.Policies.DisableConventionalLocalRouting();').length === 0);
  t('★ 本拠の試験ファイルでは検出しない',
    confinedApiViolations(ALLOWED_TEST_FILE, 'ServiceLocationPolicy.AlwaysAllowed').length === 0);
  t('★ 許可はファイル単位である（同じ Tests プロジェクトの別ファイルは許可しない）',
    confinedApiViolations(
      'src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Other.cs',
      'ServiceLocationPolicy.AlwaysAllowed').length === 1);
  t('★ 部分一致では検出しない（識別子の境界で照合する）',
    confinedApiViolations('src/k/x.cs', 'var myServiceLocationPolicyX = 1;').length === 0
      && confinedApiViolations('src/k/x.cs', 'XListenToRabbitQueue();').length === 0);
  t('封じ込め API を含まなければ 0 件',
    confinedApiViolations('src/k/x.cs', 'cfg.UsePlatformRetry();').length === 0);
  t('★ 本拠から消えたら消失として報告する（(a) だけでは静かに no-op になる）',
    confinedApiHomeGaps('options.Policies.DisableConventionalLocalRouting();')
      .sort().join(',') === 'ListenToRabbitQueue,ServiceLocationPolicy');
  t('★ 本拠に手順 4 だけが在る場合、残り 2 つが消失として挙がる（順序非依存）',
    confinedApiHomeGaps('x.DisableConventionalLocalRouting();').length === 2);
  t('★ 本拠が空（ファイルごと消失）なら全シンボルを報告する',
    confinedApiHomeGaps('').length === CONFINED_APIS.length);
  t('本拠に全シンボルが**呼び出し構文で**在れば消失 0 件',
    confinedApiHomeGaps(
      'options.Policies.DisableConventionalLocalRouting();\n'
      + 'options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;\n'
      + 'return options.ListenToRabbitQueue(name);\n',
    ).length === 0);
  t('★ シンボル名を並べただけ（宣言・言及）では「在る」と見なさない',
    confinedApiHomeGaps(CONFINED_APIS.map((f) => f.symbol).join('\n')).length
      === CONFINED_APIS.length);
  t('★ 禁止側（規則 4）と封じ込め側（規則 5）でシンボルが重複しない（極性の混同防止）',
    !CONFINED_APIS.some((c) => FORBIDDEN_APIS.some((f) => f.symbol === c.symbol)));
  t('封じ込め API には手順と理由が必ず付く（メッセージだけ見て直せるように）',
    CONFINED_APIS.every((f) => typeof f.why === 'string' && f.why.length > 0
      && typeof f.step === 'string' && f.step.length > 0));
  t('許可リストの先頭は本拠である', CONFINED_API_ALLOWED[0] === CONFINED_API_HOME);

  // --- 規則 5 の F1 是正: (b) はコメントに覆い隠されない（#897 の監査・AI レビューが実測した穴） ---
  t('★ (b) 本拠の説明コメントだけが言及していても「在る」と見なさない（F1 の回帰試験）',
    confinedApiHomeGaps(
      '// 手順 5: 既定は ServiceLocationPolicy.NotAllowed である\n'
      + 'options.Policies.DisableConventionalLocalRouting();\n'
      + 'return options.ListenToRabbitQueue(name);\n',
    ).join(',') === 'ServiceLocationPolicy');
  t('★ (b) 実際に代入されていれば「在る」と見なす',
    confinedApiHomeGaps(
      'options.Policies.DisableConventionalLocalRouting();\n'
      + 'return options.ListenToRabbitQueue(name);\n'
      + 'options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;\n',
    ).length === 0);
  t('★ (b) 行末コメントでも覆い隠されない',
    confinedApiHomeGaps(
      'var x = 1; // options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;\n'
      + 'options.Policies.DisableConventionalLocalRouting();\n'
      + 'return options.ListenToRabbitQueue(name);\n',
    ).join(',') === 'ServiceLocationPolicy');
  t('★ (b) ブロックコメントでも覆い隠されない',
    confinedApiHomeGaps(
      '/* options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed; */\n'
      + 'options.Policies.DisableConventionalLocalRouting();\n'
      + 'return options.ListenToRabbitQueue(name);\n',
    ).join(',') === 'ServiceLocationPolicy');
  t('★ (b) 文字列リテラル中の言及でも覆い隠されない',
    confinedApiHomeGaps(
      'var s = "options.ServiceLocationPolicy = x";\n'
      + 'options.Policies.DisableConventionalLocalRouting();\n'
      + 'return options.ListenToRabbitQueue(name);\n',
    ).join(',') === 'ServiceLocationPolicy');
  t('★ (b) 全 API に呼び出し構文（usage）が定義されている（1 つでも欠けると全文一致へ落ちる）',
    CONFINED_APIS.every((f) => f.usage instanceof RegExp));
  t('★ (b) == 比較だけでは「在る」と見なさない（代入のみを認める）',
    confinedApiHomeGaps(
      'if (options.ServiceLocationPolicy == ServiceLocationPolicy.NotAllowed) { }\n'
      + 'options.Policies.DisableConventionalLocalRouting();\n'
      + 'return options.ListenToRabbitQueue(name);\n',
    ).join(',') === 'ServiceLocationPolicy');
  t('★ (b) 代入は == 除外を入れても引き続き一致する',
    confinedApiHomeGaps(
      'options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;\n'
      + 'options.Policies.DisableConventionalLocalRouting();\n'
      + 'return options.ListenToRabbitQueue(name);\n',
    ).length === 0);

  // コメント除去そのもの。
  t('stripCsharpComments: 行コメントを消す',
    !stripCsharpComments('a(); // ListenToRabbitQueue(').includes('ListenToRabbitQueue'));
  t('stripCsharpComments: ブロックコメントを消す',
    !stripCsharpComments('/* ListenToRabbitQueue( */ a();').includes('ListenToRabbitQueue'));
  t('stripCsharpComments: コードは残す',
    stripCsharpComments('x.ListenToRabbitQueue("q");').includes('ListenToRabbitQueue'));
  t('★ stripCsharpComments: 文字列中の // をコメント扱いしない（消しすぎでコードを失わない）',
    stripCsharpComments('var u = "http://x"; y.DisableConventionalLocalRouting();')
      .includes('DisableConventionalLocalRouting'));

  // --- F4: 許可リストは「ちょうど 2 件」で固定する ---
  // 「2 件以上」にすると、許可リストを増やす変更が自己試験を素通りする。
  // scripts.repo.test.js のテストプロジェクト数 ratchet と同じ作法である
  // （「N 件以上」にすると走査が壊れて空振りしたときに緑になる）。
  t('★ 許可リストはちょうど 2 件である（本拠とその試験ファイル。増やすときはここも直す）',
    CONFINED_API_ALLOWED.length === 2);

  // --- F3: 規約ルーティングは禁止側（規則 4）にある ---
  t('★ UseConventionalRouting は禁止 API である（手順 3 を潰す最も現実的な経路）',
    FORBIDDEN_APIS.some((f) => f.symbol === 'UseConventionalRouting'));
  t('UseConventionalRouting の呼び出しを検出する',
    forbiddenApiViolations('a/B.cs', 'opts.UseRabbitMq().UseConventionalRouting();').length === 1);

  let failed = 0;
  for (const c of cases) {
    process.stdout.write(`  ${c.pass ? 'ok  ' : 'FAIL'} ${c.name}\n`);
    if (!c.pass) { failed++; if (c.actual !== undefined) console.error('    actual:', JSON.stringify(c.actual)); }
  }
  if (failed) {
    console.error(`[check-backend-libraries] 自己試験 ${failed} 件 失敗。`);
    process.exit(1);
  }
  console.log(`[check-backend-libraries] 自己試験 ${cases.length} 件 OK。`);
}

// --- 実行 -----------------------------------------------------------------------

function writeBaseline() {
  const { current } = scanTree();
  const body = {
    $comment: [
      'ADR-0030 の不採用ライブラリに対する既知違反の baseline（Issue #455 で新設）。',
      'ratchet: 新規混入は fail、ここに載る残件は warn、消えたのに残っていれば fail。',
      '🔴 残件はすべて MassTransit であり、Wolverine 移行（#441）で落ちる。**行が減る最小単位は',
      'イベント辺**（1 イベントの発行元＋全購読先を一括）であり、辺はサービスをまたぐため、各サービスの',
      '再実装 issue（#438〜#451）には入れられない。旧版の「各サービスの再実装 issue が移行と同時に',
      '自プロジェクトの行を削除する」は誤りであった（IADR-0234 決定 2 が訂正）。',
      'baseline が空になったら Directory.Packages.props から不採用パッケージを削除する（#441 / C3）。',
    ],
    projects: current,
  };
  fs.writeFileSync(BASELINE_FILE, JSON.stringify(body, null, 2) + '\n');
  console.log(`[check-backend-libraries] baseline を書き出しました: ${Object.keys(current).length} プロジェクト`);
}

function main() {
  if (process.argv.includes('--self-test')) { selfTest(); return; }
  if (process.argv.includes('--write-baseline')) { writeBaseline(); return; }

  const { current, domain } = scanTree();
  const baseline = readBaseline();
  const { added, known, stale } = classifyAgainstBaseline(current, baseline);

  if (known.length) {
    const byProject = new Set(known.map((k) => k.project));
    notice(`ADR-0030 不採用ライブラリの残件: ${known.length} 件 / ${byProject.size} プロジェクト（baseline 済み）。` +
      'Wolverine 移行（#441）がイベント辺の単位で解消し baseline から削除する（IADR-0234 決定 2・3）。');
    const summary = process.env.GITHUB_STEP_SUMMARY;
    if (summary) {
      const lines = ['### ADR-0030 不採用ライブラリの残件（baseline）', '', '| プロジェクト | ライブラリ |', '| --- | --- |'];
      for (const k of known) lines.push(`| \`${k.project}\` | ${k.lib} |`);
      try { fs.appendFileSync(summary, lines.join('\n') + '\n'); } catch { /* サマリ書けなくても検査は続ける */ }
    }
  }

  const failures = [];
  for (const a of added) {
    failures.push(`[新規混入] ${a.project}\n    不採用ライブラリ「${a.lib}」への参照が baseline に無い状態で追加されています。`);
  }
  for (const s of stale) {
    failures.push(`[baseline 減らし忘れ] ${s.project}\n    「${s.lib}」の参照は既に解消済みです。baseline の該当行を削除してください。`);
  }
  for (const d of domain) {
    if (d.kind === 'xunit-runner-mismatch') {
      failures.push(`[xUnit 版不整合] ${d.project}\n    ${d.detail}。` +
        'CPM は 1 パッケージ 1 バージョンしか持てず v2 と v3 は共存できない。本リポジトリは ' +
        '#455 A-2 で 16 プロジェクトを v3 へ一斉に切り替え済みである。' +
        '本体は xunit.v3、CPM の xunit.runner.visualstudio は 3.x に揃えること。');
      continue;
    }
    if (d.kind === 'forbidden-api') {
      const f = FORBIDDEN_APIS.find((x) => x.symbol === d.detail);
      failures.push(`[禁止 API] ${d.project}\n    「${d.detail}」は計画 ADR-0027 が名指しで禁じています。`
        + ` ${f ? f.why : ''}`
        + ' この退行は例外もログも出さず、業務イベントが黙って消えます。');
      continue;
    }
    if (d.kind === 'confined-api') {
      const f = CONFINED_APIS.find((x) => x.symbol === d.detail);
      failures.push(`[封じ込め API] ${d.project}\n    「${d.detail}」（ADR-0027 ${f ? f.step : ''}）は`
        + `共通ヘルパ ${CONFINED_API_HOME} の中でしか呼べません。`
        + ` ${f ? f.why : ''}`
        + ' 手順 6 が「個別サービスでの逸脱を静的検査で禁止する」と定めています。');
      continue;
    }
    if (d.kind === 'confined-api-missing') {
      const f = CONFINED_APIS.find((x) => x.symbol === d.detail);
      failures.push(`[封じ込め API の消失] ${d.project}\n    「${d.detail}」（ADR-0027 ${f ? f.step : ''}）が`
        + '共通ヘルパから消えています。封じ込めは「他所で書けない」だけでは半分で、'
        + '「ここに在り続ける」が要ります —— 本拠から消えると、どこにも設定が無い状態が'
        + '検査を素通りします。削除ではなく、手順そのものを見直すなら ADR-0027 の改定が要ります。');
      continue;
    }
    if (d.kind === 'template-banned') {
      failures.push(`[雛形に不採用ライブラリ] ${d.project}\n    「${d.detail}」。` +
        '雛形は新サービスの出発点であり、不採用ライブラリを持ち込ませてはならない（ADR-0030）。');
      continue;
    }
    if (d.kind === 'shared-kernel-project') {
      failures.push(`[SharedKernel 依存規律] ${d.project}\n    ProjectReference「${d.detail}」。` +
        `${SHARED_KERNEL} は Result / Error・共通基底を置く最下層であり、他プロジェクトを参照しません。` +
        '参照先が持つ外部パッケージはそのまま Domain の推移的な外部依存になり、' +
        '計画 ADR-0041 決定 3（推移的に 1 つに限る）を破ります。');
      continue;
    }
    if (d.kind === 'shared-kernel-package') {
      failures.push(`[SharedKernel 依存規律] ${d.project}\n    PackageReference「${d.detail}」は許可リスト外です。` +
        `${SHARED_KERNEL} が持ち込んでよい外部パッケージは Result 型の実装 1 つ` +
        `（現行: ${SHARED_KERNEL_ALLOWED.join(' / ')}）に限ります（計画 ADR-0041 決定 3）。` +
        'Domain は SharedKernel だけを ProjectReference できるため、ここへ入れたものは' +
        'そのまま Domain の推移的な外部依存になります。' +
        '別のパッケージが必要な場合は、許可リストへ足すのではなく ADR-0041 の改定が要ります。');
      continue;
    }
    const what = d.kind === 'domain-package' ? 'PackageReference' : `ProjectReference（${SHARED_KERNEL} 以外）`;
    failures.push(`[Domain 依存規律] ${d.project}\n    ${what}「${d.detail}」。` +
      `Domain 層が依存してよい外部ライブラリは ${SHARED_KERNEL} 経由の Result 実装 1 つのみです` +
      '（ADR-0030 選定基準 3 を計画 ADR-0041 が部分改定。Domain 自身は PackageReference を持てません）。');
  }

  if (failures.length === 0) {
    console.log(`[check-backend-libraries] OK: 新規混入 0 件 / Domain 依存規律 OK（既知残件 ${known.length} 件は baseline 済み）。`);
    process.exit(0);
  }
  console.error(`[check-backend-libraries] 違反 ${failures.length} 件を検出しました:`);
  for (const f of failures) console.error(`\n  ${f}`);
  console.error('\n標準は計画 ADR-0030 / 12_backend-application-stack、実装側の要点は docs/tech/tech-requirements.md を参照してください。');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  BANNED,
  // ADR-0041 / #500。scripts.repo.test.js から固定できるよう、既存の BANNED / isDomainProject と
  // 同じく公開する（監査 🟢-13。自己試験だけで覆うと外から回帰を止められない）。
  SHARED_KERNEL,
  SHARED_KERNEL_ALLOWED,
  isSharedKernelProject,
  bannedListFor,
  sharedKernelViolations,
  EXCLUDED_UNITS,
  isExcludedPath,
  centralVersionOf,
  majorOf,
  xunitRunnerMismatch,
  matchesBanned,
  bannedNameOf,
  packageReferencesOf,
  projectReferencesOf,
  usingNamespacesOf,
  bannedInCsproj,
  bannedInSource,
  isScannedBuildFile,
  isDomainProject,
  domainViolations,
  classifyAgainstBaseline,
  owningProject,
  scanTree,
};
