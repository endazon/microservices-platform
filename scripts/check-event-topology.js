#!/usr/bin/env node
/**
 * NFR / #455 子 C（Wolverine 移行 手順 1）: イベント型 → 発行元 / 購読先の対応表を走査で作り、
 * ［2026-08-21 訂正 / #455］**従前ここは「手順 1・6」と書いていたが誤りである。**
 * 手順 6 は「手順 3〜5 を共通ヘルパへ封じ込め、**個別サービスでの逸脱を静的検査で禁止する**」であり、
 * 本検査器はトポロジの対応表（手順 1）しか見ていない。手順 6 の静的検査は
 * check-backend-libraries.js の規則 4（禁止 API シンボル）が担う。
 * baseline に固定する。計画 ADR-0027（Wolverine）/ ADR-0030。
 *
 * ## なぜ移行より先に要るのか
 *
 * 計画は Wolverine 移行の手順 1 を「移行**前**に対応表を機械的に作る（後の検査の基準になる）」と定め、
 * 同時に「**うち 2 つの退行はビルド・ユニットテスト・トポロジ検査をすべて通過したまま、例外もログも
 * 出さずに業務イベントを失う**」と警告している。**赤で分からない種類の退行**なので、移行前に正解表を
 * 凍結しておかないと、移行後に「これで合っているのか」を判定する基準が無い。
 *
 * 最も重要な固定対象は **`DocumentUpdated` の購読が 2 件である**こと（IngestionService ＋ WikiService）。
 * 手順 3（リスニングキュー名にサービス名を前置する）を誤ると 2 つの購読者が同一キューを競合し、
 * **片方だけがメッセージを受け取る**。本検査はその減少を exit 1 で止める。
 *
 * ## 設計の要点
 *
 * 1. **列挙を持たない。** イベント型は `*.Contracts/Events/*.cs` の走査で発見する。
 * 2. **両方向の ratchet。** 減っても増えても違反にする（増加を黙って許すと baseline が形骸化する）。
 * 3. **0 件走査で緑を返さない。** イベント 0 件・購読 0 件なら exit 1（#797 / IADR-0130）。
 * 4. **購読 0 件のイベントは notice で必ず出す。** 「検査して 0 件」と「見ていない」を区別する。
 * 5. **MassTransit と Wolverine の両方の記法を読む。** 移行中は同居し得る。
 *    **移行しても表が変わらないことが、移行が正しいことの証拠**になる。
 * 6. **トランスポートも記録し、発行側と購読側の食い違いを違反にする**（#455 Phase 0 / U2）。
 * 7. **トランスポートの変化そのものを baseline と突き合わせる ratchet**（#921 / IADR-0257）。
 *    向きは非対称である —— **前進（MassTransit → Wolverine）は baseline の更新を促し、
 *    逆行（Wolverine → MassTransit）は `--update` でも通さない**（`--allow-regression` が要る）。
 *
 * ## 🔴 なぜ 7 を足したか —— 要点 6 だけでは「辺の両側を同時に移した」ことが見えない
 *
 * 要点 6（`transportMismatches`）が見るのは**同一イベントの発行側と購読側の食い違い**だけである。
 * 辺の両側を同時に切り替えると交差は空にならないので、この判定は何も言わない。一方 baseline 突合
 * （`diffAgainstBaseline`）は `ownersOf()`＝`Object.keys()` で **transport 配列を捨てていた**ため、
 * owner 名が変わらない限り差分は 0 件だった。**すなわち辺 1 本の移行は、正解表を 1 行も更新しないまま
 * 緑で通った** —— 実際に `RawDocumentFetched`（#441）がその状態で develop に載っていた（#921 で実測）。
 *
 * 移行の正しさの証拠は「表が変わらないこと」（要点 5）だが、**表が transport を持つ以上、
 * transport が変わったなら表は変わっている**。変わったことを人が明示的に記録して初めて、
 * 「意図した移行」と「気付かぬ退行」が区別できる。
 *
 * **逆行だけ `--update` を拒むのは、ADR-0027 が MassTransit を不採用と決めているからである。**
 * 前進と同じ扱い（更新すれば消える指摘）にすると、その制約は baseline 更新 1 回で溶ける。
 *
 * ## 🔴 なぜ 6 を足したか —— 従前この検査は部分移行を原理的に検出できなかった
 *
 * 当初の実装は `IConsumer<T>`（MassTransit）と `Handle(T)`（Wolverine 規約）を**同じ集合へ入れ**、
 * 発行側も `Publish[A-Za-z]*` という語しか見ずレシーバの型を無視していた。baseline のレコードは
 * `{publishers:[owner], subscribers:[owner]}` だけで、**トランスポートという次元が表に存在しなかった**。
 * つまり `IConsumer<X>` を `Handle(X e)` へ書き換えても出力は 1 ビットも変わらず、
 * **「MT 発行 → Wolverine 購読」の組ができても素通りした**。
 *
 * これはバグではなく射程外であり、設計要点 5 の意図（表が変わらないこと＝正しさの証拠）そのものだった。
 * だが**必要条件であって十分条件ではない**。RabbitMQ の exchange / queue 名の導出規則は両者で異なるため、
 * binding の無い exchange へ publish した結果**メッセージが黙って捨てられる**（confirms は成功を返す）。
 *
 * **判定は「購読者ごとに、発行側と共有するトランスポートが 1 つ以上あるか」**である。
 * 共有が 0 ならその購読者は 1 通も受け取れない。**二重購読（MT と Wolverine の両方で待つ）は
 * 意図的な移行手順なので違反にしない** —— 交差が空でないためである。
 *
 * ## 既知の限界（承知のうえで残す）
 *
 * 発行の検出は `Publish(new X(...))` / `Publish<X>(...)` の形しか拾わない。
 * **`Publish(変数)` や `Publish(メソッド呼び出し())` の形は見えない**（型解析が要るため）。
 *
 * 実測（2026-08-21。本番のみ。テストは走査対象外）:
 *
 *     # bus.Publish の総数（doc.Publish() のようなドメインメソッドは除く）。
 *     # パス指定は src 配下の本番コード（グロブは実行時に補う。ここへ書くと
 *     # ブロックコメントの終端記号と衝突するため省いている）。
 *     git grep -nE 'bus\.Publish[A-Za-z]*\s*\(' | grep -vE '\.Tests|/tests/'   # → 13
 *     # うち検出できる形（Publish(new X( / Publish<X>）
 *     git grep -nE 'bus\.Publish[A-Za-z]*\s*(<|\(\s*new\s)'                 # → 6
 *
 * **総数 13 / 見える形 6 / 見えない形 7。** 見えない 7 件は
 * `ConversionJobEndpoints.cs:70`（`Publish(ev, ct)`）・`DocumentEndpoints.cs` の 5 箇所
 * （`Publish(ToEvent(...))`）・`TagDictionaryEndpoints.cs:136` である。
 * 🔴 **`DocumentUpdated` の業務上の主経路（`DocumentEndpoints.cs`）は 1 件も見えていない。**
 * 表に `knowledge/DocumentService` が発行元として出るのは、`DocumentNormalizedConsumer.cs:61` の
 * `Publish(new DocumentUpdated(` が拾えているからであって、網羅しているからではない。
 * **発行元が減ったときに検出できない可能性がある**ことを承知して使うこと。
 */

'use strict';

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.join(__dirname, '..');
const BASELINE = path.join(__dirname, 'event-topology-baseline.json');
const SKIP_DIRS = new Set(['node_modules', '.git', 'bin', 'obj', 'dist', 'coverage']);

/** 別プロジェクトの submodule。契約の名前空間が違うので走査に混ぜない。 */
const EXCLUDED_UNITS = new Set(['ai-stock-trading']);

const MT = 'masstransit';
const WOLVERINE = 'wolverine';

/**
 * ファイルがどのライブラリを取り込んでいるかを見る（発行側のトランスポート判定に使う）。
 *
 * 発行は両者とも `Publish...` という同じ語なので、**呼び出しの形では区別できない**。
 * 取り込み（`using` / `global using`）で判定する。両方あれば両方返す（移行中は同居し得る）。
 */
function transportsOfFile(content) {
  const t = new Set();
  if (/^\s*(?:global\s+)?using\s+MassTransit\b/m.test(content)) t.add(MT);
  if (/^\s*(?:global\s+)?using\s+Wolverine\b/m.test(content)) t.add(WOLVERINE);
  return t;
}

/** ファイルを再帰的に集める（拡張子で絞る）。 */
function walk(dir, ext, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (!SKIP_DIRS.has(e.name)) walk(full, ext, out);
    } else if (e.name.endsWith(ext)) {
      out.push(full);
    }
  }
  return out;
}

const toPosix = (p) => p.split(path.sep).join('/');

/** テストプロジェクト配下か。フィクスチャの発行を実配線と数えないための除外。 */
function isTestPath(rel) {
  return /(^|\/)tests?\//i.test(rel) || /\.Tests?\//.test(rel);
}

/** submodule ユニット配下か。 */
function isExcludedUnit(rel) {
  const m = /^src\/([^/]+)\//.exec(rel);
  return m ? EXCLUDED_UNITS.has(m[1]) : false;
}

/**
 * パスから「担い手」の名前を決める。
 * サービスなら `<unit>/<Service>`、共有なら `<unit>/Shared/<Project>`、BFF なら `<unit>/Bff/<Project>`。
 */
function ownerOf(rel) {
  const p = toPosix(rel);
  let m = /^src\/([^/]+)\/backend\/Services\/([^/]+)\//.exec(p);
  if (m) return `${m[1]}/${m[2]}`;
  m = /^src\/([^/]+)\/backend\/(Shared|Bff|Tests)\/([^/]+)\//.exec(p);
  if (m) return `${m[1]}/${m[2]}/${m[3]}`;
  m = /^src\/([^/]+)\//.exec(p);
  return m ? m[1] : 'unknown';
}

/** イベント契約の型名を発見する（`*.Contracts/Events/*.cs` のファイル名を型名とみなす）。 */
function discoverEvents(repoRoot = REPO_ROOT) {
  const files = walk(path.join(repoRoot, 'src'), '.cs')
    .map((f) => toPosix(path.relative(repoRoot, f)))
    .filter((f) => !isExcludedUnit(f))
    .filter((f) => /\/[^/]*\.Contracts\/Events\/[^/]+\.cs$/.test(f));
  return [...new Set(files.map((f) => path.basename(f, '.cs')))].sort();
}

/**
 * 発行元を見つける。MassTransit / Wolverine とも `Publish` 系の呼び出しで表す。
 * `Publish(new Foo(...))` / `Publish<Foo>(...)` / `PublishAsync(new Foo(...))` を拾う。
 */
function findPublishers(content, events) {
  const hit = new Map();
  const fileTransports = transportsOfFile(content);
  for (const ev of events) {
    const re = new RegExp(String.raw`Publish[A-Za-z]*\s*(?:<\s*${ev}\s*>|\(\s*new\s+${ev}\b)`);
    if (re.test(content)) hit.set(ev, new Set(fileTransports));
  }
  return hit;
}

/**
 * 購読先を見つける。**MassTransit と Wolverine の両方の記法**を読む。
 *   - MassTransit: `IConsumer<Foo>`
 *   - Wolverine 規約: `Handle(Foo ...)` / `Consume(Foo ...)`（メソッド引数の第 1 型）
 */
function findSubscribers(content, events) {
  const hit = new Map();
  for (const ev of events) {
    const massTransit = new RegExp(String.raw`IConsumer\s*<\s*${ev}\s*>`);
    const wolverine = new RegExp(String.raw`\b(?:Handle|Consume)\s*\(\s*(?:\[[^\]]*\]\s*)?${ev}\s+\w`);
    const t = new Set();
    // 購読は記法そのものがトランスポートを表すので、ファイルの取り込みではなく一致した記法で決める。
    if (massTransit.test(content)) t.add(MT);
    if (wolverine.test(content)) t.add(WOLVERINE);
    if (t.size > 0) hit.set(ev, t);
  }
  return hit;
}

/** 対応表を作る。{ [event]: { publishers: [...], subscribers: [...] } } */
function buildTopology(repoRoot = REPO_ROOT) {
  const events = discoverEvents(repoRoot);
  const table = Object.fromEntries(events.map((e) => [e, { publishers: new Map(), subscribers: new Map() }]));
  // #992 / [[IADR-0314]]: 経路の宣言（発行側 / 購読側の束ね）。Map<event, Set<owner>>。
  const routes = new Map();
  const binds = new Map();
  if (events.length === 0) return { events, topology: {}, routes, binds };

  const files = walk(path.join(repoRoot, 'src'), '.cs')
    .map((f) => toPosix(path.relative(repoRoot, f)))
    .filter((f) => !isExcludedUnit(f))
    .filter((f) => !isTestPath(f));

  for (const rel of files) {
    let content;
    try {
      content = fs.readFileSync(path.join(repoRoot, rel), 'utf8');
    } catch {
      continue;
    }
    const owner = ownerOf(rel);
    for (const [ev, ts] of findPublishers(content, events)) addOwner(table[ev].publishers, owner, ts);
    for (const [ev, ts] of findSubscribers(content, events)) addOwner(table[ev].subscribers, owner, ts);
    for (const ev of findRoutedEvents(content, events)) addDeclared(routes, ev, owner);
    for (const ev of findBoundEvents(content, events)) addDeclared(binds, ev, owner);
  }

  const topology = {};
  for (const ev of events) {
    topology[ev] = {
      publishers: freezeSide(table[ev].publishers),
      subscribers: freezeSide(table[ev].subscribers),
    };
  }
  return { events, topology, routes, binds };
}

/**
 * #992 / [[IADR-0314]]: Wolverine の **経路宣言**を拾う。
 *
 * 🔴 **これが無いと発行は 1 通もブローカへ出ない。** 発行側が経路を宣言せず、購読側が
 * exchange へ束ねていなければ、Wolverine は宛先を決められず「宛先を決められない」旨の
 * info ログを 1 行出して**捨てる**。例外もヘルスチェックの赤も出ない。
 * **稼働クラスタで実測した**（取り込みが一度も起動していなかった）。
 *
 * 従前この検査器は「発行側と購読側が同じトランスポートを名乗るか」しか見ておらず、
 * **両側とも wolverine と名乗りながら経路が 1 本も無い状態を緑にしていた。**
 */
function findRoutedEvents(content, events) {
  return events.filter((ev) => new RegExp(String.raw`RoutePlatformEvent\s*<\s*${ev}\s*>`).test(content));
}

function findBoundEvents(content, events) {
  return events.filter((ev) => new RegExp(String.raw`BindPlatformQueue\s*<\s*${ev}\s*>`).test(content));
}

/** Map<event, Set<owner>> へ 1 件足す。 */
function addDeclared(map, ev, owner) {
  if (!map.has(ev)) map.set(ev, new Set());
  map.get(ev).add(owner);
}

/**
 * #992 / [[IADR-0314]]: Wolverine の辺について、**経路が実際に宣言されているか**を見る。
 *
 * - 発行側（wolverine を名乗る owner が居る）→ どこかで `RoutePlatformEvent<Ev>` が要る
 * - 購読側（wolverine を名乗る owner）→ その owner に `BindPlatformQueue<Ev>` が要る
 *
 * **MassTransit の辺は対象外**（経路の作り方が違う）。購読 0 件のイベントは発行側だけ見る。
 */
function routingGaps(topology, routes, binds) {
  const out = [];
  for (const [ev, t] of Object.entries(topology)) {
    const wolverinePublishers = Object.entries(normalizeSide(t.publishers))
      .filter(([, ts]) => normalizeTransports(ts).includes(WOLVERINE))
      .map(([owner]) => owner);
    if (wolverinePublishers.length > 0 && !(routes.get(ev) && routes.get(ev).size > 0)) {
      out.push(
        [
          `「${ev}」は Wolverine で発行されるのに、**外向きの経路が 1 本も宣言されていない**`
            + `（発行元: ${wolverinePublishers.join(', ')}）。`,
          '      発行は「宛先を決められない」旨の info ログ 1 行で黙って捨てられる（例外は出ない）。',
          `      発行側の UseWolverine で WolverineExtensions.RoutePlatformEvent<${ev}>() を呼ぶこと。`,
        ].join('\n'),
      );
    }
    for (const [owner, ts] of Object.entries(normalizeSide(t.subscribers))) {
      if (!normalizeTransports(ts).includes(WOLVERINE)) continue;
      if (binds.get(ev) && binds.get(ev).has(owner)) continue;
      out.push(
        [
          `「${ev}」を Wolverine で購読する ${owner} が、**自分のキューを exchange へ束ねていない**。`,
          '      キュー名を分けるだけでは 1 通も届かない（束ねて初めて fan-out が成立する）。',
          `      UseRabbitMq(...).BindPlatformQueue<${ev}>("<service>", <queue>) を呼ぶこと。`,
        ].join('\n'),
      );
    }
  }
  return out;
}

/** owner の Map<string, Set<transport>> へ 1 件足す。 */
function addOwner(map, owner, transports) {
  if (!map.has(owner)) map.set(owner, new Set());
  for (const t of transports) map.get(owner).add(t);
}

/** Map<owner, Set<transport>> を baseline へ書ける形（{owner: [transport...]}）へ畳む。 */
function freezeSide(map) {
  const out = {};
  for (const owner of [...map.keys()].sort()) out[owner] = [...map.get(owner)].sort();
  return out;
}

/**
 * baseline 側の片側を正規化する。
 * **旧形式（owner の配列）も読む** —— トランスポート欄が無かった頃の baseline が残っていても
 * クラッシュさせない。その場合トランスポートは「不明」（空集合）として扱い、食い違い判定は
 * 保留する（不明を違反にすると、更新前の baseline で一斉に赤くなる）。
 */
function normalizeSide(side) {
  if (Array.isArray(side)) return Object.fromEntries(side.map((o) => [o, []]));
  return side && typeof side === 'object' ? side : {};
}

/**
 * 片側の owner 名を並べる。
 *
 * 🔴 **この関数は transport を捨てる。** owner の増減を見るためだけに使い、
 * **突合をこれだけで終わらせない**（transport の突合は `transportChanges()` が担う。#921）。
 */
function ownersOf(side) {
  return Object.keys(normalizeSide(side)).sort();
}

/** 片側の 1 owner ぶんの transport 配列を正規化する（重複を潰し、順序を固定する）。 */
function normalizeTransports(ts) {
  return [...new Set(Array.isArray(ts) ? ts : [])].sort();
}

/**
 * baseline と現状の transport を比べ、変化を分類する（#921）。
 *
 * 返り値 `null` は「判定しない」である。**どちらかが不明（空）なら保留する** ——
 * 旧形式の baseline（owner の配列）や、`using` を持たないファイル（取り込みが global usings 側に
 * あると起こる）で一斉に赤くすると、検査そのものが信用されなくなる。`transportMismatches()` と
 * 同じ作法である。**保留は「安全」ではなく「見えていない」であることを承知して使うこと。**
 *
 * 逆行の定義は **`wolverine` を失った、または `masstransit` が新たに混入した**である
 * （ADR-0027: Wolverine 採用・MassTransit 不採用）。残りは前進 —— 取り得る値が 2 つしか
 * 無いため、逆行でない変化は `wolverine` の追加か `masstransit` の脱落のいずれかになる。
 */
function classifyTransportChange(baseTs, curTs) {
  const from = normalizeTransports(baseTs);
  const to = normalizeTransports(curTs);
  if (from.length === 0 || to.length === 0) return null; // 不明 → 保留
  if (from.join('+') === to.join('+')) return null; // 変化なし
  const lost = from.filter((t) => !to.includes(t));
  const added = to.filter((t) => !from.includes(t));
  const regression = lost.includes(WOLVERINE) || added.includes(MT);
  return { kind: regression ? 'regression' : 'forward', from, to, lost, added };
}

/**
 * baseline と現状で **両方に居る owner** の transport 変化を列挙する（#921）。
 *
 * 増えた／減った owner は `diffAgainstBaseline()` の owner 差分が既に報告しているため、
 * ここでは扱わない（同じ事実を 2 通の違反として出さない）。
 */
function transportChanges(topology, baseline) {
  const out = [];
  const base = baseline && baseline.topology ? baseline.topology : {};
  for (const ev of Object.keys(topology).sort()) {
    const old = base[ev];
    if (!old) continue; // 新設イベントは owner 差分の側で報告済み
    for (const side of ['publishers', 'subscribers']) {
      const oldSide = normalizeSide(old[side]);
      const curSide = normalizeSide(topology[ev][side]);
      for (const owner of Object.keys(curSide).sort()) {
        if (!Object.prototype.hasOwnProperty.call(oldSide, owner)) continue; // 増えた owner
        const change = classifyTransportChange(oldSide[owner], curSide[owner]);
        if (change) out.push({ event: ev, side, owner, ...change });
      }
    }
  }
  return out;
}

/** transport 変化 1 件を違反メッセージへ整形する。**前進と逆行で文面を分ける。** */
function fmtTransportChange(c, label) {
  const from = `[${c.from.join(', ')}]`;
  const to = `[${c.to.join(', ')}]`;
  if (c.kind === 'regression') {
    return (
      `「${c.event}」の${label} ${c.owner} のトランスポートが**逆行**した: ${from} → ${to}\n` +
      `      **ADR-0027 は Wolverine を採用し MassTransit を不採用と決めている。**\n` +
      `      これは baseline の更新では通らない —— \`--update\` も逆行を含む表を書き込まない。\n` +
      `      撤退が本当に要るなら \`--update --allow-regression\` を使い、IADR で根拠を残すこと。`
    );
  }
  return (
    `「${c.event}」の${label} ${c.owner} のトランスポートが変わった: ${from} → ${to}\n` +
    `      **移行そのものは前進だが、baseline（正解表）の更新を忘れている。**\n` +
    `      owner 名が変わらないため、従前の突合（owner 集合だけ）では素通りしていた（#921）。\n` +
    `      意図した移行なら \`--update\` で表を更新し、**辺の全メンバが同時に移っているか**を確かめること。`
  );
}

function loadBaseline() {
  try {
    return JSON.parse(fs.readFileSync(BASELINE, 'utf8'));
  } catch {
    return null;
  }
}

/**
 * baseline と突き合わせる。増減の**両方向**を違反にする。
 * **owner の増減に加え、両方に居る owner の transport 変化も違反にする**（#921 / IADR-0257）。
 */
function diffAgainstBaseline(topology, baseline) {
  const violations = [];
  const base = baseline && baseline.topology ? baseline.topology : {};
  const changes = transportChanges(topology, baseline);
  const evs = [...new Set([...Object.keys(topology), ...Object.keys(base)])].sort();

  for (const ev of evs) {
    const cur = topology[ev];
    const old = base[ev];
    if (!old) {
      violations.push(`イベント「${ev}」が baseline に無い。新設したなら baseline を更新すること。`);
      continue;
    }
    if (!cur) {
      violations.push(`イベント「${ev}」が消えた（baseline には在る）。契約を削ったなら baseline を更新すること。`);
      continue;
    }
    for (const kind of ['publishers', 'subscribers']) {
      const a = new Set(ownersOf(old[kind]));
      const b = new Set(ownersOf(cur[kind]));
      const lost = [...a].filter((x) => !b.has(x));
      const added = [...b].filter((x) => !a.has(x));
      const label = kind === 'publishers' ? '発行元' : '購読先';
      if (lost.length > 0) {
        violations.push(
          `「${ev}」の${label}が減った: ${lost.join(', ')}\n` +
            `      **これは「例外もログも出さずにイベントを失う」退行そのものである。**` +
            `意図した削除なら baseline を更新すること。`,
        );
      }
      if (added.length > 0) {
        violations.push(
          `「${ev}」の${label}が増えた: ${added.join(', ')}\n` +
            `      baseline の更新を忘れている（増加を黙って許すと baseline が形骸化する）。`,
        );
      }
      for (const c of changes.filter((x) => x.event === ev && x.side === kind)) {
        violations.push(fmtTransportChange(c, label));
      }
    }
  }
  return violations;
}

/**
 * 🔴 発行側と購読側のトランスポートの食い違いを違反として返す（#455 Phase 0 / U2）。
 *
 * 判定は**購読者ごと**である。ある購読者が発行側と 1 つもトランスポートを共有していなければ、
 * その購読者は**1 通も受け取れない** —— 例外もログも出ず、ビルドもテストも通ったまま消える。
 *
 * **二重購読は違反にしない。** MT と Wolverine の両方で待つのは移行手順そのものであり、
 * 交差が空でないため下の条件に掛からない。これが「切替」を「追加」に分解して
 * 1 PR を小さく保つための前提になる。
 *
 * トランスポート不明（旧形式の baseline・取り込みを持たないファイル）は**判定を保留する**。
 * 不明を違反にすると、まだ移行していない箇所まで一斉に赤くなり検査が信用されなくなる。
 */
function transportMismatches(topology) {
  const violations = [];
  for (const ev of Object.keys(topology).sort()) {
    const pubSide = normalizeSide(topology[ev].publishers);
    const subSide = normalizeSide(topology[ev].subscribers);

    const pubTransports = new Set();
    for (const ts of Object.values(pubSide)) for (const t of ts) pubTransports.add(t);
    if (pubTransports.size === 0) continue; // 発行元なし or 不明 → 判定しない

    for (const owner of Object.keys(subSide).sort()) {
      const subTransports = subSide[owner] || [];
      if (subTransports.length === 0) continue; // 不明 → 判定しない
      if (subTransports.some((t) => pubTransports.has(t))) continue; // 1 つでも共有していれば届く

      violations.push(
        `「${ev}」の購読先 ${owner} は発行側とトランスポートを共有していない: ` +
          `発行 [${[...pubTransports].sort().join(', ')}] / 購読 [${subTransports.join(', ')}]\n` +
          `      **この購読者は 1 通も受け取れない。** 例外もログも出ず、ビルドもユニットテストも通る。\n` +
          `      片側だけを移す部分移行がこれである。**発行元と全購読先を同一 PR で同じトランスポートへ移すこと。**
` +
          `      🔴 **二重購読で凌ごうとしないこと。** 本検査は二重購読を違反にしないが、それは
` +
          `      「検査が許す」だけであって「安全」ではない —— MassTransit 発行 → Wolverine 購読は
` +
          `      **メッセージが黙って捨てられる**（キュー深さ 0・例外なし・ログなし。実ブローカで実測）。
` +
          `      切替は「停止 → 排出 → 切替」の 3 段で行う（IADR-0245 決定 4）。`,
      );
    }
  }
  return violations;
}

// ---------------------------------------------------------------- self-test

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => {
    fn();
    n += 1;
    console.log(`  ok  ${name}`);
  };
  const EV = ['DocumentUpdated', 'IngestionCompleted'];

  ok('MassTransit の IConsumer<T> を購読として拾い、トランスポートを masstransit と記録する', () => {
    const s = findSubscribers('class C : IConsumer<DocumentUpdated>, IPipelineStep {}', EV);
    assert.deepStrictEqual([...s.keys()], ['DocumentUpdated']);
    assert.deepStrictEqual([...s.get('DocumentUpdated')], ['masstransit']);
  });

  ok('Wolverine 規約の Handle(T) / Consume(T) も購読として拾い、wolverine と記録する', () => {
    const a = findSubscribers('public Task Handle(DocumentUpdated e) => Task.CompletedTask;', EV);
    assert.deepStrictEqual([...a.get('DocumentUpdated')], ['wolverine']);
    assert.ok(findSubscribers('public void Consume(DocumentUpdated msg) { }', EV).has('DocumentUpdated'));
    assert.ok(findSubscribers('public void Handle([NotNull] DocumentUpdated m) { }', EV).has('DocumentUpdated'));
  });

  ok('★ 二重購読は両方のトランスポートを記録する（移行手順そのもの）', () => {
    const src = 'class C : IConsumer<DocumentUpdated> { }\nclass D { public Task Handle(DocumentUpdated e) => null; }';
    assert.deepStrictEqual([...findSubscribers(src, EV).get('DocumentUpdated')].sort(), ['masstransit', 'wolverine']);
  });

  ok('Publish(new T) / Publish<T> を発行として拾う', () => {
    assert.ok(findPublishers('await bus.Publish(new DocumentUpdated(id));', EV).has('DocumentUpdated'));
    assert.ok(findPublishers('await bus.PublishAsync<DocumentUpdated>(msg);', EV).has('DocumentUpdated'));
  });

  ok('★ 発行のトランスポートは呼び出しの形ではなく取り込みで決まる', () => {
    // Publish という語は MT と Wolverine で同じなので、using で判定するほかない。
    const mt = findPublishers('using MassTransit;\nawait bus.Publish(new DocumentUpdated(id));', EV);
    assert.deepStrictEqual([...mt.get('DocumentUpdated')], ['masstransit']);
    const wo = findPublishers('using Wolverine;\nawait bus.PublishAsync(new DocumentUpdated(id));', EV);
    assert.deepStrictEqual([...wo.get('DocumentUpdated')], ['wolverine']);
    // 取り込みが無ければ「不明」（空集合）。違反にはしない。
    assert.deepStrictEqual([...findPublishers('Publish(new DocumentUpdated(id));', EV).get('DocumentUpdated')], []);
  });

  ok('transportsOfFile: global using も読む / 部分一致で誤検出しない', () => {
    assert.ok(transportsOfFile('global using MassTransit;').has('masstransit'));
    assert.ok(transportsOfFile('using MassTransit.Testing;').has('masstransit'));
    assert.strictEqual(transportsOfFile('using MassTransitExtras;').size, 0);
    assert.strictEqual(transportsOfFile('using System;').size, 0);
  });

  ok('型名の部分一致で誤検出しない', () => {
    assert.strictEqual(findSubscribers('class C : IConsumer<DocumentUpdatedV2> {}', EV).size, 0);
    assert.strictEqual(findPublishers('Publish(new DocumentUpdatedV2());', EV).size, 0);
  });

  ok('担い手はパスから決まる（サービス / 共有 / BFF）', () => {
    assert.strictEqual(ownerOf('src/knowledge/backend/Services/WikiService/src/W/X.cs'), 'knowledge/WikiService');
    assert.strictEqual(
      ownerOf('src/platform/backend/Shared/Platform.Shared.Infrastructure/X.cs'),
      'platform/Shared/Platform.Shared.Infrastructure',
    );
  });

  ok('テストプロジェクトは走査から外れる（フィクスチャの発行を実配線と数えない）', () => {
    assert.ok(isTestPath('src/knowledge/backend/Tests/Knowledge.IntegrationTests/X.cs'));
    assert.ok(isTestPath('src/platform/backend/Bff/Platform.Bff.Tests/X.cs'));
    assert.ok(!isTestPath('src/knowledge/backend/Services/WikiService/src/W/X.cs'));
  });

  ok('購読が減ると違反になる（イベントを失う退行）', () => {
    const base = { topology: { DocumentUpdated: { publishers: ['a'], subscribers: ['x', 'y'] } } };
    const v = diffAgainstBaseline({ DocumentUpdated: { publishers: ['a'], subscribers: ['x'] } }, base);
    assert.strictEqual(v.length, 1);
    assert.ok(v[0].includes('購読先が減った'));
  });

  ok('購読が増えても違反になる（baseline の更新を強制する）', () => {
    const base = { topology: { DocumentUpdated: { publishers: ['a'], subscribers: ['x'] } } };
    const v = diffAgainstBaseline({ DocumentUpdated: { publishers: ['a'], subscribers: ['x', 'z'] } }, base);
    assert.strictEqual(v.length, 1);
    assert.ok(v[0].includes('購読先が増えた'));
  });

  ok('旧形式（owner の配列）の baseline も読める（クラッシュさせない）', () => {
    const base = { topology: { DocumentUpdated: { publishers: ['a'], subscribers: ['x'] } } };
    const cur = { DocumentUpdated: { publishers: { a: ['masstransit'] }, subscribers: { x: ['masstransit'] } } };
    assert.deepStrictEqual(diffAgainstBaseline(cur, base), []);
    assert.deepStrictEqual(ownersOf(['a', 'b']), ['a', 'b']);
    assert.deepStrictEqual(ownersOf({ b: [], a: [] }), ['a', 'b']);
  });

  // ---- ★ 部分移行の検出（本検査を足した理由そのもの） ----

  ok('★ MT 発行 → Wolverine 購読は違反（1 通も届かない部分移行）', () => {
    const v = transportMismatches({
      DocumentUpdated: { publishers: { a: ['masstransit'] }, subscribers: { x: ['wolverine'] } },
    });
    assert.strictEqual(v.length, 1);
    assert.ok(v[0].includes('トランスポートを共有していない'));
  });

  ok('★ 逆向き（Wolverine 発行 → MT 購読）も違反', () => {
    const v = transportMismatches({
      DocumentUpdated: { publishers: { a: ['wolverine'] }, subscribers: { x: ['masstransit'] } },
    });
    assert.strictEqual(v.length, 1);
  });

  ok('★ 二重購読は違反にしない（交差が空でない。移行手順そのもの）', () => {
    const v = transportMismatches({
      DocumentUpdated: {
        publishers: { a: ['masstransit'] },
        subscribers: { x: ['masstransit', 'wolverine'] },
      },
    });
    assert.deepStrictEqual(v, []);
  });

  ok('★ 購読者ごとに判定する（片方だけ届かない状態を見逃さない）', () => {
    // 交差の有無を「発行側 vs 全購読者の和集合」で見ると x が届かないことを見逃す。
    const v = transportMismatches({
      DocumentUpdated: {
        publishers: { a: ['masstransit'] },
        subscribers: { x: ['wolverine'], y: ['masstransit'] },
      },
    });
    assert.strictEqual(v.length, 1);
    assert.ok(v[0].includes('x'));
    assert.ok(!v[0].includes('購読先 y'));
  });

  ok('トランスポート不明は判定を保留する（旧 baseline で一斉に赤くしない）', () => {
    assert.deepStrictEqual(
      transportMismatches({ DocumentUpdated: { publishers: { a: [] }, subscribers: { x: ['wolverine'] } } }),
      [],
    );
    assert.deepStrictEqual(
      transportMismatches({ DocumentUpdated: { publishers: { a: ['masstransit'] }, subscribers: { x: [] } } }),
      [],
    );
  });

  ok('★ countSubscribers は新旧どちらの形でも数えられる（NaN の fail-open 回帰防止）', () => {
    // 本作業中に実際に踏んだ事故: t.subscribers.length が undefined → 合計 NaN →
    // `totalSubs === 0` が偽 → 0 件走査でも緑になる（門が静かに開く）。
    const cur = { A: { publishers: {}, subscribers: { x: ['masstransit'], y: [] } } };
    assert.strictEqual(countSubscribers(cur), 2);
    const legacy = { A: { publishers: [], subscribers: ['x', 'y'] } };
    assert.strictEqual(countSubscribers(legacy), 2);
    assert.strictEqual(countSubscribers({ A: { publishers: {}, subscribers: {} } }), 0);
    assert.ok(Number.isInteger(countSubscribers(cur)), '合計が NaN になっていない');
  });

  // ---- ★ トランスポート ratchet（#921 / IADR-0257。owner 名は同じままの変化） ----

  const baseOf = (pub, sub) => ({ topology: { DocumentUpdated: { publishers: pub, subscribers: sub } } });
  const curOf = (pub, sub) => ({ DocumentUpdated: { publishers: pub, subscribers: sub } });

  ok('★ owner 名が同じでトランスポートだけ変わると違反になる（本 ratchet の中核）', () => {
    // 従前はここが 0 件だった —— ownersOf() が Object.keys() で transport を捨てていたため。
    const v = diffAgainstBaseline(
      curOf({ a: ['wolverine'] }, { x: ['wolverine'] }),
      baseOf({ a: ['masstransit'] }, { x: ['masstransit'] }),
    );
    assert.strictEqual(v.length, 2, '発行側と購読側で 1 件ずつ出る');
    assert.ok(v[0].includes('発行元 a のトランスポートが変わった: [masstransit] → [wolverine]'));
    assert.ok(v[1].includes('購読先 x のトランスポートが変わった'));
  });

  ok('★ 前進（masstransit → wolverine）は「baseline の更新を忘れている」として出す', () => {
    const v = diffAgainstBaseline(curOf({ a: ['wolverine'] }, {}), baseOf({ a: ['masstransit'] }, {}));
    assert.strictEqual(v.length, 1);
    assert.ok(v[0].includes('baseline（正解表）の更新を忘れている'));
    assert.ok(!v[0].includes('逆行'));
  });

  ok('★ 逆行（wolverine → masstransit）は「逆行」として出す（文面が違う）', () => {
    const v = diffAgainstBaseline(curOf({ a: ['masstransit'] }, {}), baseOf({ a: ['wolverine'] }, {}));
    assert.strictEqual(v.length, 1);
    assert.ok(v[0].includes('トランスポートが**逆行**した: [wolverine] → [masstransit]'));
    assert.ok(v[0].includes('ADR-0027'));
  });

  ok('★ 二重購読への拡張は前進 / MassTransit の再混入は逆行', () => {
    assert.strictEqual(
      classifyTransportChange(['masstransit'], ['masstransit', 'wolverine']).kind, 'forward',
    );
    assert.strictEqual(classifyTransportChange(['masstransit', 'wolverine'], ['wolverine']).kind, 'forward');
    assert.strictEqual(
      classifyTransportChange(['wolverine'], ['wolverine', 'masstransit']).kind, 'regression',
    );
    assert.strictEqual(classifyTransportChange(['masstransit', 'wolverine'], ['masstransit']).kind, 'regression');
  });

  ok('★ トランスポート不明・順序違いは変化としない（旧 baseline で一斉に赤くしない）', () => {
    assert.strictEqual(classifyTransportChange([], ['wolverine']), null);
    assert.strictEqual(classifyTransportChange(['masstransit'], []), null);
    assert.strictEqual(classifyTransportChange(['wolverine', 'masstransit'], ['masstransit', 'wolverine']), null);
    // 旧形式（owner の配列）の baseline は transport を持たないので保留になる。
    const legacy = { topology: { DocumentUpdated: { publishers: ['a'], subscribers: ['x'] } } };
    assert.deepStrictEqual(
      diffAgainstBaseline(curOf({ a: ['wolverine'] }, { x: ['wolverine'] }), legacy), [],
    );
  });

  ok('★ owner の増減とトランスポート変化を二重報告しない', () => {
    // a が消えて b が増えた。b は baseline に居ないので transport の突合対象にしない。
    const v = diffAgainstBaseline(curOf({ b: ['wolverine'] }, {}), baseOf({ a: ['masstransit'] }, {}));
    assert.strictEqual(v.length, 2);
    assert.ok(v[0].includes('発行元が減った'));
    assert.ok(v[1].includes('発行元が増えた'));
    assert.ok(!v.some((m) => m.includes('トランスポートが')));
  });

  ok('★ transportChanges は baseline に無いイベントを触らない（新設は owner 差分が報告する）', () => {
    assert.deepStrictEqual(transportChanges(curOf({ a: ['wolverine'] }, {}), { topology: {} }), []);
    assert.deepStrictEqual(transportChanges(curOf({ a: ['wolverine'] }, {}), null), []);
  });

  ok('発行元が居ないイベントは判定しない（購読 0 件の孤児と同じ扱い）', () => {
    assert.deepStrictEqual(
      transportMismatches({ IngestionCompleted: { publishers: {}, subscribers: { x: ['wolverine'] } } }),
      [],
    );
  });

  console.log(`[check-event-topology] self-test OK: ${n} 件`);
}

/**
 * 購読の総数を数える。
 *
 * 🔴 **必ずこの関数を通す。** owner 配列 → `{owner: [transport]}` へ形を変えたとき、
 * 呼び出し側に `t.subscribers.length` が残っていて **NaN** になり、
 * `totalSubs === 0` が偽になって **「0 件走査で緑を返さない」門（設計要点 3）が静かに開いた**。
 * 実際に本作業中に踏んだ。数え方を 1 箇所に畳み、自己試験で形の変化に追随させる。
 */
function countSubscribers(topology) {
  return Object.values(topology).reduce((a, t) => a + ownersOf(t.subscribers).length, 0);
}

/** 表示用に `owner(transport)` の並びへ整形する。トランスポート不明は owner だけ出す。 */
function fmtSide(side) {
  const m = normalizeSide(side);
  const parts = Object.keys(m)
    .sort()
    .map((o) => (m[o] && m[o].length > 0 ? `${o}(${m[o].join('+')})` : o));
  return parts.join(', ') || '-';
}

// ---------------------------------------------------------------- main

function main() {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-test')) {
    selfTest();
    return;
  }

  const { events, topology, routes, binds } = buildTopology();

  // 0 件走査で緑を返さない（#797 / IADR-0130）。
  if (events.length === 0) {
    console.error('[check-event-topology] イベント契約が 0 件だった。走査が壊れている可能性がある。');
    process.exit(1);
  }
  const totalSubs = countSubscribers(topology);
  if (totalSubs === 0) {
    console.error('[check-event-topology] 購読が 1 件も見つからなかった。走査が壊れている可能性がある。');
    process.exit(1);
  }

  if (argv.includes('--update')) {
    // 🔴 ratchet の向き（#921 / IADR-0257）: 逆行を含む表は書き込まない。
    // これが無いと「逆行の指摘は --update 1 回で消える」ことになり、ADR-0027 の制約が溶ける。
    const regressions = transportChanges(topology, loadBaseline()).filter((c) => c.kind === 'regression');
    if (regressions.length > 0 && !argv.includes('--allow-regression')) {
      console.error(
        `[check-event-topology] 逆行（Wolverine → MassTransit）を含む表は書き込まない: ${regressions.length} 件`,
      );
      for (const c of regressions) {
        console.error(
          `  - ${c.event} / ${c.side} / ${c.owner}: [${c.from.join(', ')}] → [${c.to.join(', ')}]`,
        );
      }
      console.error(
        '  ADR-0027 は Wolverine を採用し MassTransit を不採用と決めている。\n' +
          '  撤退が本当に必要なら --allow-regression を明示し、IADR で根拠を残すこと。',
      );
      process.exit(1);
    }
    fs.writeFileSync(
      BASELINE,
      `${JSON.stringify(
        {
          $comment:
            'NFR / #455 子 C: Wolverine 移行 手順 1 の対応表。移行前の正解表であり、' +
            '移行後もこの表が変わらないことが移行の正しさの証拠になる。増減の両方向を違反にする。' +
            ' 各 owner の値は**トランスポート**（masstransit / wolverine）である。' +
            '購読者が発行側と 1 つもトランスポートを共有していなければ、その購読者は 1 通も' +
            '受け取れないため違反にする（部分移行の検出。#455 Phase 0 / U2）。' +
            '二重購読（両方で待つ）は移行手順なので違反にしない。' +
            ' **トランスポートの変化そのものも突合する**（#921 / IADR-0257）。' +
            '前進（masstransit → wolverine）は本ファイルの更新を促し、' +
            '逆行（wolverine → masstransit）は --update でも書き込まない（--allow-regression が要る）。',
          topology,
        },
        null,
        2,
      )}\n`,
    );
    console.log(`[check-event-topology] baseline を更新した: ${path.relative(REPO_ROOT, BASELINE)}`);
    return;
  }

  const baseline = loadBaseline();
  if (!baseline) {
    console.error(
      `[check-event-topology] baseline が読めない: ${path.relative(REPO_ROOT, BASELINE)}\n` +
        '  初回は --update で作成すること。',
    );
    process.exit(1);
  }

  // 購読 0 件のイベントを notice で必ず出す（「検査して 0 件」と「見ていない」を区別する）。
  const orphans = Object.entries(topology)
    .filter(([, t]) => ownersOf(t.subscribers).length === 0)
    .map(([ev, t]) => {
      const pubs = ownersOf(t.publishers);
      return `${ev}（発行元: ${pubs.length > 0 ? pubs.join(', ') : 'なし'}）`;
    });
  if (orphans.length > 0) {
    console.log(
      `notice: 購読が 0 件のイベントが ${orphans.length} 件ある（違反ではない）: ${orphans.join(' / ')}`,
    );
  }

  const violations = [
    ...diffAgainstBaseline(topology, baseline),
    ...transportMismatches(topology),
    ...routingGaps(topology, routes, binds),
  ];
  if (violations.length > 0) {
    console.error(`[check-event-topology] ${violations.length} 件の違反:`);
    for (const v of violations) console.error(`\n  - ${v}`);
    process.exit(1);
  }

  console.log(`[check-event-topology] OK: イベント ${events.length} 件 / 購読 ${totalSubs} 件が baseline と一致。`);
  for (const [ev, t] of Object.entries(topology)) {
    console.log(`  ${ev}: 発行 [${fmtSide(t.publishers)}] → 購読 [${fmtSide(t.subscribers)}]`);
  }
}

if (require.main === module) main();

module.exports = {
  buildTopology, discoverEvents, findPublishers, findSubscribers, diffAgainstBaseline,
  transportMismatches, transportsOfFile, normalizeSide, ownersOf, countSubscribers, ownerOf, isTestPath,
  classifyTransportChange, transportChanges, normalizeTransports,
};
