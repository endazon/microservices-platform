#!/usr/bin/env node
'use strict';
/*
 * rabbitmq-permissions.test.js
 * NFR-18, IADR-0681: ブローカのサービスごとのユーザーの権限を、使うキュー・exchange の名前へ絞る（段 1 は報告だけ・段 2 で当てる）。
 *
 * 固定するもの:
 *   1. 生成物 deploy/local/rabbitmq-setup/permissions.txt は、コード（Wolverine の配線）から導いた値と一致する（スナップショット）。
 *      AST の写し（src/ai-stock-trading か AST_ROOT）が無い checkout では AST の行を照合しない（notice）。
 *      RABBITMQ_PERMISSIONS_REQUIRE_AST=1 なら写しが無いことを失敗にする（CI の submodule を取るジョブ）。
 *   2. 行の集合は users.txt とちょうど一致する。正規表現は ^( … | … )$・\.・.* だけで書く（awk・RabbitMQ・JS で同じに読める）。
 *   3. 欠け: 各ユーザーが要る操作（宣言・束ね・発行・購読・デッドレター・応答キュー・戻し）が、その権限の正規表現に当たる。
 *      変異（名前を 1 つ抜く）で必ず赤になる。
 *   4. 隔て: 他のサービスのキューには configure / read / write のいずれも当たらない。発行しない exchange には write が当たらない。
 *   4-a. 最小権限の陰性対照（NFR-18, IADR-0691）: 利用者ごとに「拒まれるべき操作」を列挙し、どれも当たらない。
 *        既定の exchange への write は受信キューを持つ利用者だけ。使わない exchange の configure（宣言・削除）・read・write、
 *        応答キューの形でない名前、錨の外（前後に字を足した名前）。規則を元に戻す変異（全員に既定の exchange・`.*` の応答キュー・
 *        全 exchange の configure・錨なし）で必ず赤になる。
 *   5. 導出の検出力: 規則の字面が変わった・ServiceName が見つからない・接頭辞が食い違うと、生成器は止まる（黙って緩めない）。
 *   6. 合わせ（reconcile-users.sh）: 既定（段 1）は vhost 全体のまま、narrow（段 2）は生成物の行を当てる。行の欠け・届かないものが
 *      あれば何も変えずに止まる。--permissions-report は何も書かない。当てた段を ConfigMap に写す。
 *   7. 起動の直後の復元（rabbitmq.yaml の restore-users.sh）は ConfigMap の段に従い、行の無いユーザーへ vhost 全体を付けない。
 *   4-b. デッドレターはサービスごと（NFR-18, IADR-0699）: 取引ユニットの利用者は、共有のデッドレター（wolverine-dead-letter-queue）にも
 *        他のサービスのデッドレター（<ServiceName>.dead-letter・<キュー>_error）にも、configure / write / read のいずれも持たない（ローカル・クラウドとも）。
 *        自分のデッドレターは要る操作として当たる。共有のデッドレターを戻す変異で赤になり、shim の配線（CustomizeDeadLetterQueueing）を外すと生成器が止まる。
 *   8. クラウドの写し（NFR-18, IADR-0694）: 購読する（発行しない）exchange の configure を与えない。それ以外はローカルの行と同じ。
 *      購読に要る操作（受動の宣言・束ね）は当たり、変異（購読の exchange の configure を戻す）で陰性対照が赤になる。
 *      exchanges.json は購読される exchange の和で、Wolverine の宣言と同じ形。クラウドの復元は利用者の権限を当てる前にそれを宣言する。
 *
 * 外部依存ゼロ（Node 標準モジュール ＋ bash / sh / awk）。実行: node scripts/rabbitmq-permissions.test.js
 */
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const gen = require('./gen-rabbitmq-permissions');

const REPO_ROOT = path.resolve(__dirname, '..');
const RECONCILE = path.join(REPO_ROOT, 'deploy/local/rabbitmq-setup/reconcile-users.sh');
const BROKER = fs.readFileSync(path.join(REPO_ROOT, 'deploy/local/infra/rabbitmq.yaml'), 'utf8');

let passed = 0;
function ok(name, fn) {
  fn();
  passed++;
  process.stdout.write(`  ok  ${name}\n`);
}

const committed = gen.readPermissions(REPO_ROOT);
const users = gen.readUsers(REPO_ROOT);
const G = gen.generate(REPO_ROOT);
const hasAst = Boolean(G.astRoot);
if (!hasAst) {
  if (process.env.RABBITMQ_PERMISSIONS_REQUIRE_AST === '1') throw new Error('AST の写しが無い（RABBITMQ_PERMISSIONS_REQUIRE_AST=1）');
  process.stdout.write('::notice::AST の写しが無いので、AST の行の導出（3・4 の AST 側）は照合しない\n');
}
const derivedBy = new Map(G.derived.map((d) => [d.user, d]));
const rx = (p) => new RegExp(p);

// --- 1・2 -------------------------------------------------------------------------------------------------------
ok('生成物はコードから導いた権限と一致する（スナップショット）', () => {
  const have = fs.readFileSync(path.join(REPO_ROOT, gen.PERMS_REL), 'utf8');
  assert.strictEqual(have, G.text, 'permissions.txt がコードと食い違う。node scripts/gen-rabbitmq-permissions.js --write');
});

ok('基盤の 1 行の値を字面で固定する（生成器の規則そのものの退行を止める）', () => {
  assert.deepStrictEqual(committed.get('msp-ingestion'), {
    user: 'msp-ingestion',
    configure: `^(ingestion-service\\..*|${gen.RESPONSE_PATTERN}|wolverine-dead-letter-queue|DocumentDeleted|DocumentUpdated|IngestionCompleted)$`,
    write: '^(ingestion-service\\..*|wolverine-dead-letter-queue|amq\\.default|IngestionCompleted)$',
    read: `^(ingestion-service\\..*|${gen.RESPONSE_PATTERN}|wolverine-dead-letter-queue|DocumentDeleted|DocumentUpdated)$`,
  });
  // NFR-18, IADR-0691: 発行だけの利用者（受信キューを持たない）は既定の exchange に書けない。
  assert.deepStrictEqual(committed.get('msp-document'), {
    user: 'msp-document',
    configure: `^(document-service\\..*|${gen.RESPONSE_PATTERN}|wolverine-dead-letter-queue|DocumentDeleted|DocumentUpdated)$`,
    write: '^(document-service\\..*|wolverine-dead-letter-queue|DocumentDeleted|DocumentUpdated)$',
    read: `^(document-service\\..*|${gen.RESPONSE_PATTERN}|wolverine-dead-letter-queue)$`,
  });
  assert.strictEqual(gen.RESPONSE_PATTERN,
    'wolverine\\.response\\.' + [8, 4, 4, 4, 12].map((n) => '[0-9a-f]'.repeat(n)).join('-'));
});

ok('行の集合は users.txt とちょうど一致し、正規表現は awk / RabbitMQ / JS で同じに読める字句だけを使う', () => {
  assert.deepStrictEqual([...committed.keys()].sort(), users.map((u) => u.user).sort());
  // 文字クラスは応答キューの Guid の 16 進（`[0-9a-f]`）だけを許す（区間 `{n}` は awk の実装によって読めないので使わない）。
  const safe = /^\^\((?:[A-Za-z0-9_-]|\\\.|\.\*|\[0-9a-f\]|\|)+\)\$$/;
  for (const r of committed.values()) {
    for (const k of ['configure', 'write', 'read']) {
      assert.ok(safe.test(r[k]), `${r.user} の ${k} に許していない字句がある: ${r[k].slice(0, 80)}`);
      assert.notStrictEqual(r[k], '.*', `${r.user} の ${k} が vhost 全体のまま`);
    }
  }
});

// --- 3・4 -------------------------------------------------------------------------------------------------------
function missingAccess(d, row) {
  return gen.requiredAccess(d).filter((a) => !rx(row[a.perm]).test(a.name)).map((a) => `${a.perm} ${a.name}（${a.why}）`);
}

ok('欠け: 各ユーザーが要る操作は、すべてその権限に当たる', () => {
  for (const d of G.derived) {
    const miss = missingAccess(d, committed.get(d.user));
    assert.deepStrictEqual(miss, [], `${d.user} の権限が欠ける:\n  ${miss.join('\n  ')}`);
  }
  if (!hasAst) assert.ok(G.derived.every((d) => d.unit === 'microservices-platform'));
});

ok('欠け（変異）: 購読・発行の名前を 1 つ抜くと、必ず欠けとして赤になる', () => {
  let bit = 0;
  for (const d of G.derived) {
    for (const [side, list] of [['subscribe', d.subscribe], ['publish', d.publish]]) {
      for (const name of list) {
        const mutated = gen.patternsFor({ ...d, [side]: list.filter((x) => x !== name) });
        // 同じ名前を他方の側でも使うなら configure は残る。read / write のどちらかが必ず欠ける。
        assert.ok(missingAccess(d, mutated).length > 0, `${d.user} から ${side} ${name} を抜いても欠けを検出できない`);
        bit++;
      }
    }
  }
  assert.ok(bit >= 9, `変異が少なすぎる（${bit}）`);
});

ok('隔て: 他のサービスのキューには当たらず、発行しない exchange には write が当たらない', () => {
  for (const a of G.derived) {
    const row = committed.get(a.user);
    for (const b of G.derived) {
      if (a === b) continue;
      // 他のサービスの受信キュー（購読の全件。AST はデッドレターの `_error` とサービスごとのデッドレターも）と、購読の無いサービスの前置の探り。
      const foreign = [`${b.queuePrefix}Probe`, ...(b.serviceDeadLetter ? [b.serviceDeadLetter] : []), ...b.subscribe.flatMap((t) => {
        const q = `${b.queuePrefix}${b.unit === 'ai-stock-trading' ? t.split('.').pop() : t}`;
        return b.deadLetterQueues ? [q, `${q}_error`] : [q];
      })];
      for (const q of foreign) {
        for (const k of ['configure', 'write', 'read']) assert.ok(!rx(row[k]).test(q), `${a.user} の ${k} が ${b.user} のキュー ${q} に当たる`);
      }
      for (const ex of b.publish) {
        if (!a.publish.includes(ex)) assert.ok(!rx(row.write).test(ex), `${a.user} が発行しない ${ex} へ書ける`);
      }
    }
    for (const k of ['configure', 'write', 'read']) {
      assert.ok(!rx(row[k]).test('ai-stock-trading.someone-else.OrderApproved'), `${a.user} の ${k} が前置の外に当たる`);
      assert.ok(!rx(row[k]).test(`x${a.queuePrefix}q`), `${a.user} の ${k} が行頭に錨を持たない`);
    }
  }
});

// --- 4-a --------------------------------------------------------------------------------------------------------
// NFR-18, IADR-0691: 利用者ごとの陰性対照。RabbitMQ の権限の表（requiredAccess の注記）で「その名前に要る権限」を読む:
//   configure = 宣言と削除（exchange.delete・queue.delete も configure）、write = 発行・束ねの宛先、read = 購読・束ねの元。
const ALL_EXCHANGES = [...new Set(G.derived.flatMap((d) => [...d.subscribe, ...d.publish]))].sort();
const SAMPLE_UUID = '3f2b8c1e-9a4d-4e6f-8b2a-1c3d5e7f9a0b';

/** 拒まれるべき操作（{ perm, name, why }）。 */
function forbiddenAccess(d) {
  const out = [];
  const no = (perm, name, why) => out.push({ perm, name, why });
  if (!d.hasListener) no('write', gen.DEFAULT_EXCHANGE, '受信キューを持たない利用者の既定の exchange への発行（宛先のキューを権限で分けない）');
  for (const ex of ALL_EXCHANGES) {
    const sub = d.subscribe.includes(ex);
    const pub = d.publish.includes(ex);
    if (!sub && !pub) no('configure', ex, '使わない exchange の宣言・削除');
    if (!sub) no('read', ex, '購読しない exchange への束ね');
    if (!pub) no('write', ex, '発行しない exchange への発行');
  }
  for (const perm of ['configure', 'read']) {
    no(perm, `${gen.RESPONSE_PREFIX}not-a-guid`, '応答キューの形でない名前');
    no(perm, `${gen.RESPONSE_PREFIX}${SAMPLE_UUID}.OrderApproved`, '応答キューの名前の後ろに字を足した名前');
    no(perm, `${gen.RESPONSE_PREFIX}${SAMPLE_UUID.toUpperCase()}`, '応答キューの Guid の書式でない名前（大文字）');
  }
  for (const perm of ['configure', 'write', 'read']) {
    no(perm, `x${gen.DEAD_LETTER}`, '錨の外（前）');
    no(perm, `${gen.DEAD_LETTER}x`, '錨の外（後）');
  }
  for (const ex of [...d.subscribe, ...d.publish]) {
    no('configure', `${ex}x`, '錨の外（exchange の後）');
    no('configure', `x${ex}`, '錨の外（exchange の前）');
  }
  if (d.hasListener) no('write', `${gen.DEFAULT_EXCHANGE}x`, '錨の外（既定の exchange の後）');
  return out;
}
function violations(d, row) {
  return forbiddenAccess(d).filter((a) => rx(row[a.perm]).test(a.name)).map((a) => `${a.perm} ${a.name}（${a.why}）`);
}

ok('最小権限: 既定の exchange へ書けるのは受信キューを持つ利用者だけ（発行だけの利用者は書けない）', () => {
  for (const d of G.derived) {
    assert.strictEqual(rx(committed.get(d.user).write).test(gen.DEFAULT_EXCHANGE), d.hasListener,
      `${d.user}: 既定の exchange への write が受信キューの有無（${d.hasListener}）と食い違う`);
  }
  assert.ok(G.derived.some((d) => !d.hasListener), '発行だけの利用者が 1 人も居ない（陰性対照が空になる）');
  assert.ok(G.derived.some((d) => d.hasListener), '受信キューを持つ利用者が 1 人も居ない');
  if (hasAst) {
    const astOff = G.derived.filter((d) => d.unit === 'ai-stock-trading' && !d.hasListener).map((d) => d.user).sort();
    assert.deepStrictEqual(astOff, ['ast-backtest', 'ast-configuration', 'ast-information-collection', 'ast-report']);
  }
});

ok('最小権限の陰性対照: 利用者ごとの拒まれるべき操作は、どれもその権限に当たらない', () => {
  let n = 0;
  for (const d of G.derived) {
    const v = violations(d, committed.get(d.user));
    assert.deepStrictEqual(v, [], `${d.user} が拒まれるべき操作を許す:\n  ${v.join('\n  ')}`);
    n += forbiddenAccess(d).length;
  }
  assert.ok(n >= 100, `陰性対照が少なすぎる（${n}）`);
});

ok('最小権限: exchange の configure（宣言・削除）は購読と発行の exchange ちょうどに限る', () => {
  for (const d of G.derived) {
    const got = ALL_EXCHANGES.filter((ex) => rx(committed.get(d.user).configure).test(ex));
    assert.deepStrictEqual(got, [...new Set([...d.subscribe, ...d.publish])].sort(), `${d.user} の configure の exchange`);
  }
});

ok('最小権限（変異）: 規則を元に戻すと、陰性対照が必ず赤になる', () => {
  const allExchanges = ALL_EXCHANGES.map((x) => x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  const mutants = [
    ['全員に既定の exchange の write', (d, r) => ({ ...r, write: gen.patternsFor({ ...d, hasListener: true }).write })],
    ['応答キューを `.*` へ戻す', (d, r) => ({ ...r, configure: r.configure.replace(gen.RESPONSE_PATTERN, 'wolverine\\.response\\..*'), read: r.read.replace(gen.RESPONSE_PATTERN, 'wolverine\\.response\\..*') })],
    ['全 exchange の configure', (d, r) => ({ ...r, configure: r.configure.replace(/\)\$$/, `|${allExchanges.join('|')})$`) })],
    ['錨を外す', (d, r) => ({ configure: r.configure.replace(/^\^/, '').replace(/\$$/, ''), write: r.write.replace(/^\^/, '').replace(/\$$/, ''), read: r.read.replace(/^\^/, '').replace(/\$$/, '') })],
  ];
  for (const [name, mutate] of mutants) {
    const caught = G.derived.filter((d) => violations(d, mutate(d, committed.get(d.user))).length > 0).map((d) => d.user);
    assert.ok(caught.length > 0, `変異「${name}」を陰性対照が検出しない`);
  }
  // 全員に既定の exchange を戻す変異は、発行だけの利用者の全員で赤になる。
  const off = G.derived.filter((d) => !d.hasListener);
  for (const d of off) {
    const r = committed.get(d.user);
    assert.ok(violations(d, { ...r, write: gen.patternsFor({ ...d, hasListener: true }).write }).length > 0, `${d.user}: 既定の exchange を戻しても赤にならない`);
  }
});

// --- 4-b ------------------------------------------------------------------------------------------------------
// NFR-18, IADR-0699: 取引ユニットのデッドレターはサービスごと。共有のデッドレターと他のサービスのデッドレターに、どの権限も当たらない。
const PERMS = ['configure', 'write', 'read'];
/** クラウドの写しの行（下の 8 の cloudRows と同じ読み方。8 より先に使うので関数にする）。 */
function cloudRowsEarly() {
  const m = fs.readFileSync(path.join(REPO_ROOT, gen.CLOUD_PERMS_REL), 'utf8').match(/\n  permissions\.txt: \|\n((?:    .*\n)+)/);
  assert.ok(m, 'rabbitmq-permissions.yaml に permissions.txt が無い');
  return new Map(m[1].trim().split('\n').map((l) => l.trim().split(/\s+/)).map(([user, configure, write, read]) => [user, { user, configure, write, read }]));
}
const astOf = (rows) => [...rows.entries()].filter(([u]) => users.some((x) => x.user === u && x.namespace === 'ai-stock-trading'));

/** 取引ユニットの利用者 u の行で、デッドレターについて拒まれるべきなのに当たる操作。 */
function deadLetterViolations(u, row) {
  const out = [];
  for (const k of PERMS) if (rx(row[k]).test(gen.DEAD_LETTER)) out.push(`${k} ${gen.DEAD_LETTER}（共有のデッドレター）`);
  for (const b of G.derived) {
    if (b.user === u || !b.serviceDeadLetter) continue;
    const others = [b.serviceDeadLetter, ...b.subscribe.map((t) => `${b.queuePrefix}${t.split('.').pop()}_error`)];
    for (const name of others) for (const k of PERMS) if (rx(row[k]).test(name)) out.push(`${k} ${name}（${b.user} のデッドレター）`);
  }
  return out;
}

ok('デッドレター: 取引ユニットの利用者は共有のデッドレターの権限を持たない（ローカルの行・クラウドの写しとも）', () => {
  const ast = [...astOf(committed), ...astOf(cloudRowsEarly())];
  assert.ok(ast.length >= 22, `取引ユニットの行が少なすぎる（${ast.length}）`);
  for (const [u, row] of ast) {
    for (const k of PERMS) {
      assert.ok(!rx(row[k]).test(gen.DEAD_LETTER), `${u} の ${k} が共有のデッドレター ${gen.DEAD_LETTER} に当たる`);
      assert.ok(!row[k].includes('wolverine-dead-letter'), `${u} の ${k} に共有のデッドレターの名前が残る`);
    }
  }
});

ok('デッドレター: どの取引ユニットの利用者も、他のサービスのデッドレターに権限を持たず、自分のデッドレターは要る操作として当たる', () => {
  if (!hasAst) return;
  const astD = G.derived.filter((d) => d.unit === 'ai-stock-trading');
  assert.strictEqual(astD.length, 11, '取引ユニットの利用者は 11');
  let probes = 0;
  for (const d of astD) {
    assert.strictEqual(d.serviceDeadLetter, `${d.queuePrefix}dead-letter`, `${d.user} のサービスごとのデッドレターの名前`);
    for (const rows of [committed, cloudRowsEarly()]) {
      const row = rows.get(d.user);
      assert.deepStrictEqual(deadLetterViolations(d.user, row), [], `${d.user} が他のデッドレターに当たる`);
      for (const k of PERMS) assert.ok(rx(row[k]).test(d.serviceDeadLetter), `${d.user} の ${k} が自分のデッドレター ${d.serviceDeadLetter} に当たらない`);
      probes++;
    }
    const need = gen.requiredAccess(d).filter((a) => a.name === d.serviceDeadLetter).map((a) => a.perm).sort();
    assert.deepStrictEqual(need, ['configure', 'read', 'write'], `${d.user}: 自分のデッドレターの要る操作`);
    assert.ok(!gen.requiredAccess(d).some((a) => a.name === gen.DEAD_LETTER), `${d.user}: 共有のデッドレターを要る操作に数えている`);
  }
  assert.strictEqual(probes, 22);
});

ok('デッドレター（変異）: 共有のデッドレターを戻す・他のサービスの接頭辞を足すと赤になり、クラウドの写しは作れない', () => {
  if (!hasAst) return;
  const astD = G.derived.filter((d) => d.unit === 'ai-stock-trading');
  for (const d of astD) {
    // 共有のデッドレターを戻す（serviceDeadLetter を外す＝ Wolverine の既定の名前の配線）。
    const shared = { user: d.user, ...gen.patternsFor({ ...d, serviceDeadLetter: null }) };
    assert.ok(deadLetterViolations(d.user, shared).length > 0, `${d.user}: 共有のデッドレターを戻しても赤にならない`);
    assert.throws(() => gen.cloudRow(shared), /共有のデッドレター/, `${d.user}: 共有のデッドレターを持つ行からクラウドの写しを作れてしまう`);
    // 他のサービスのデッドレターを足す。
    const other = astD.find((x) => x !== d);
    const leaked = { ...committed.get(d.user), read: committed.get(d.user).read.replace(/\)\$$/, `|${other.serviceDeadLetter.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')})$`) };
    assert.ok(deadLetterViolations(d.user, leaked).length > 0, `${d.user}: 他のサービスのデッドレターを足しても赤にならない`);
  }
  // 接頭辞の外のサービスごとのデッドレター（規則の取り違え）は生成器が止める。
  assert.throws(() => gen.patternsFor({ ...astD[0], serviceDeadLetter: 'ai-stock-trading.dead-letter' }), /接頭辞/);
});

ok('デッドレターの導出の検出力: shim の規則の字面・配線（CustomizeDeadLetterQueueing）が無ければ生成器が止まる', () => {
  if (!hasAst) return;
  const src = fs.readFileSync(path.join(G.astRoot, gen.AST_SHIM_REL), 'utf8');
  const withShim = (text, fn) => {
    const H = fs.mkdtempSync(path.join(os.tmpdir(), 'rmqdlq-'));
    try {
      fs.mkdirSync(path.dirname(path.join(H, gen.AST_SHIM_REL)), { recursive: true });
      fs.writeFileSync(path.join(H, gen.AST_SHIM_REL), text);
      return fn(H);
    } finally {
      fs.rmSync(H, { recursive: true, force: true });
    }
  };
  withShim(src, (H) => gen.assertAstNamingRules(H));
  const cases = [
    [src.replace('return $"{serviceName}.dead-letter";', 'return "wolverine-dead-letter-queue";'), /サービスごとの既定のデッドレターの規則/],
    [src.replace(/\.CustomizeDeadLetterQueueing\([^\n]*\n/, '\n'), /CustomizeDeadLetterQueueing が無い/],
    [src.replace(/(\s*)(\.CustomizeDeadLetterQueueing\()/, '$1// $2'), /CustomizeDeadLetterQueueing が無い/],
  ];
  for (const [text, want] of cases) {
    assert.notStrictEqual(text, src, '変異が当たっていない');
    withShim(text, (H) => assert.throws(() => gen.assertAstNamingRules(H), want));
  }
});

// --- 5 ----------------------------------------------------------------------------------------------------------
function fixture(mutate) {
  const H = fs.mkdtempSync(path.join(os.tmpdir(), 'rmqperm-'));
  const copy = (rel) => { fs.mkdirSync(path.dirname(path.join(H, rel)), { recursive: true }); fs.copyFileSync(path.join(REPO_ROOT, rel), path.join(H, rel)); };
  copy('src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/WolverineExtensions.cs');
  fs.mkdirSync(path.join(H, 'deploy/local/rabbitmq-setup'), { recursive: true });
  fs.writeFileSync(path.join(H, gen.USERS_REL), 'msp-demo microservices-platform demo-service msp/rabbitmq-users\n');
  const prog = path.join(H, 'src/knowledge/backend/Services/DemoService/Program.cs');
  fs.mkdirSync(path.dirname(prog), { recursive: true });
  fs.writeFileSync(prog, [
    'builder.Host.UseWolverine(opts => {',
    '  opts.ServiceName = "demo-service";',
    '  opts.UsePlatformRabbitMq(c).AutoProvision().BindPlatformQueue<DocumentUpdated>("demo-service", step);',
    '  // opts.RoutePlatformEvent<Commented>();',
    '  opts.RoutePlatformEvent<DemoDone>();',
    '});',
  ].join('\n'));
  if (mutate) mutate(H, prog);
  try {
    return { result: gen.derive(H, { requireAst: false, withAst: false }) };
  } catch (e) {
    if (!(e instanceof gen.GenError)) throw e;
    return { error: e.message };
  } finally {
    fs.rmSync(H, { recursive: true, force: true });
  }
}

ok('導出: 配線の呼び出しから読み、コメントアウトした配線は読まない', () => {
  const { result, error } = fixture();
  assert.ok(!error, error);
  assert.deepStrictEqual(result.derived.map((d) => [d.user, d.queuePrefix, d.subscribe, d.publish]),
    [['msp-demo', 'demo-service.', ['DocumentUpdated'], ['DemoDone']]]);
});

ok('導出の検出力: 規則の字面の変化・ServiceName の欠け・接頭辞の食い違い・配線なしで止まる', () => {
  const cases = [
    [(H) => { const f = path.join(H, 'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/WolverineExtensions.cs'); fs.writeFileSync(f, fs.readFileSync(f, 'utf8').replace('return $"{serviceName}.{queueName}";', 'return $"{serviceName}-{queueName}";')); }, /キュー名の規則/],
    [(H) => { const f = path.join(H, 'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/WolverineExtensions.cs'); fs.writeFileSync(f, fs.readFileSync(f, 'utf8').replace('=> typeof(TEvent).Name;', '=> typeof(TEvent).FullName!;')); }, /exchange 名の規則/],
    [(H, p) => fs.writeFileSync(p, fs.readFileSync(p, 'utf8').replace('"demo-service";', '"other-service";')), /Program\.cs が 0 件/],
    [(H, p) => fs.writeFileSync(p, fs.readFileSync(p, 'utf8').replace('<DocumentUpdated>("demo-service"', '<DocumentUpdated>("demo"')), /サービス名が "demo-service" でない/],
    [(H, p) => fs.writeFileSync(p, 'opts.ServiceName = "demo-service";\n'), /ブローカの配線/],
  ];
  for (const [mutate, want] of cases) {
    const { error } = fixture(mutate);
    assert.ok(error && want.test(error), `止まらない／理由が違う: ${error}`);
  }
});

// --- 6 ----------------------------------------------------------------------------------------------------------
// 模型の kubectl: ブローカの状態（$H/queues・exchanges・bindings・perms）を持ち、呼び出しを $H/log へ書く。
const KUBECTL = [
  '#!/usr/bin/env bash',
  'H="$STUB_H"',
  'echo "kubectl $*" >> "$H/log"',
  'case "$*" in',
  '  *"get secret rabbitmq-users"*) printf "dg=="; exit 0;;',
  '  *"rabbitmqctl -q list_users"*) printf "[]"; exit 0;;',
  '  *"rabbitmqctl -q list_queues --no-table-headers name"*) cat "$H/queues"; exit 0;;',
  '  *"rabbitmqctl -q list_exchanges --no-table-headers name"*) cat "$H/exchanges"; exit 0;;',
  '  *"rabbitmqctl -q list_bindings --no-table-headers "*) cat "$H/bindings"; exit 0;;',
  '  *"rabbitmqctl -q list_user_permissions --no-table-headers "*) u="${*: -1}"; awk -v u="$u" \'$1 == u { print "/\\t" $2 "\\t" $3 "\\t" $4 }\' "$H/perms"; exit 0;;',
  '  *"rabbitmqctl -q set_permissions "*) shift 9; [ "$3" = "${STUB_FAIL_USER:-}" ] && exit 1; echo "SET $3 $4 $5 $6" >> "$H/log"; exit 0;;',
  '  *"rabbitmqctl add_user "*|*"rabbitmqctl change_password "*|*"rabbitmqctl authenticate_user "*) cat >/dev/null; exit 0;;',
  // 段の値はファイル（--from-file=scope=<パス>）で届く。模型はその中身を読んで記録する（引数に値を載せないことも見る）。
  '  *"create configmap"*) f="$(printf "%s\\n" "$@" | sed -n "s/^--from-file=scope=//p")"; echo "CM scope=$(cat "$f" 2>/dev/null) $*" >> "$H/log"; exit 0;;',
  '  *"apply -f -"*) cat >/dev/null; exit 0;;',
  'esac',
  'exit 0',
  '',
].join('\n');

const PERMS_FIXTURE = [
  '# 生成物の模型',
  'msp-a ^(a-service\\..*|amq\\.default)$ ^(a-service\\..*|amq\\.default|EvA)$ ^(a-service\\..*)$',
  'msp-b ^(b-service\\..*|EvA)$ ^(b-service\\..*)$ ^(b-service\\..*|EvA)$',
  '',
].join('\n');

function reconcile({ args = [], env = {}, perms = PERMS_FIXTURE, queues = [], exchanges = [], bindings = [], current = '' } = {}) {
  const H = fs.mkdtempSync(path.join(os.tmpdir(), 'rmqperm-rec-'));
  try {
    const bin = path.join(H, 'bin');
    fs.mkdirSync(bin);
    fs.writeFileSync(path.join(bin, 'kubectl'), KUBECTL, { mode: 0o755 });
    fs.writeFileSync(path.join(H, 'users.txt'), 'msp-a ns a-service kv\nmsp-b ns b-service kv\n');
    if (perms !== null) fs.writeFileSync(path.join(H, 'permissions.txt'), perms);
    fs.writeFileSync(path.join(H, 'queues'), queues.map((q) => `${q}\n`).join(''));
    fs.writeFileSync(path.join(H, 'exchanges'), exchanges.map((q) => `${q}\n`).join(''));
    fs.writeFileSync(path.join(H, 'bindings'), bindings.map(([s, d]) => `${s}\t${d}\tqueue\n`).join(''));
    fs.writeFileSync(path.join(H, 'perms'), current);
    fs.writeFileSync(path.join(H, 'log'), '');
    const r = spawnSync('bash', [RECONCILE, ...args], { encoding: 'utf8', env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, STUB_H: H,
      RABBITMQ_USERS_FILE: path.join(H, 'users.txt'), RABBITMQ_PERMISSIONS_FILE: path.join(H, 'permissions.txt'), RABBITMQ_USERS_TIMEOUT: '1', ...env } });
    const log = fs.readFileSync(path.join(H, 'log'), 'utf8').split('\n').filter(Boolean);
    return { status: r.status, out: `${r.stdout}${r.stderr}`, sets: log.filter((l) => l.startsWith('SET ')), cms: log.filter((l) => l.startsWith('CM ')),
      writes: log.filter((l) => /set_permissions|add_user|change_password|set_user_tags|create configmap/.test(l)) };
  } finally {
    fs.rmSync(H, { recursive: true, force: true });
  }
}
const ON_BROKER = { queues: ['a-service.EvX', 'b-service.EvA', 'amq.gen-1'], exchanges: ['', 'amq.direct', 'EvA'], bindings: [['EvA', 'b-service.EvA'], ['', 'a-service.EvX']] };

ok('合わせ（段 1・既定）: 権限は vhost 全体のまま。段を ConfigMap に写し、点検の手順を示す', () => {
  const r = reconcile(ON_BROKER);
  assert.strictEqual(r.status, 0, r.out);
  assert.deepStrictEqual(r.sets, ['SET msp-a .* .* .*', 'SET msp-b .* .* .*']);
  // 段の記録は 2 回: 当てる前に applying、当て終えて確定の段（途中で止まれば applying が残る）。
  assert.strictEqual(r.cms.length, 2, r.cms.join('\n'));
  assert.ok(/^CM scope=applying /.test(r.cms[0]) && /^CM scope=vhost /.test(r.cms[1]) && /--from-file=permissions\.txt=/.test(r.cms[1]), r.cms.join('\n'));
  assert.ok(!r.cms.some((c) => /--from-literal/.test(c)), `値を引数（--from-literal）に載せた:\n${r.cms.join('\n')}`);
  assert.ok(/--permissions-report/.test(r.out) && /権限 vhost/.test(r.out), r.out);
});

ok('合わせ（段 2・narrow）: 生成物の行を当て、段を ConfigMap に写す', () => {
  const r = reconcile({ ...ON_BROKER, env: { RABBITMQ_PERMISSIONS: 'narrow' } });
  assert.strictEqual(r.status, 0, r.out);
  assert.deepStrictEqual(r.sets, [
    'SET msp-a ^(a-service\\..*|amq\\.default)$ ^(a-service\\..*|amq\\.default|EvA)$ ^(a-service\\..*)$',
    'SET msp-b ^(b-service\\..*|EvA)$ ^(b-service\\..*)$ ^(b-service\\..*|EvA)$',
  ]);
  assert.deepStrictEqual(r.cms.map((c) => /scope=(\w+)/.exec(c)[1]), ['applying', 'narrow'], r.cms.join('\n'));
});

ok('合わせ: 途中で失敗したら段を確定させない（applying が残り、起動の直後の復元は vhost 全体で作り直す）', () => {
  for (const scope of ['narrow', 'vhost']) {
    const r = reconcile({ ...ON_BROKER, env: { RABBITMQ_PERMISSIONS: scope, STUB_FAIL_USER: 'msp-b' } });
    assert.notStrictEqual(r.status, 0, r.out);
    assert.deepStrictEqual(r.cms.map((c) => /scope=(\w+)/.exec(c)[1]), ['applying'], `${scope}: 確定の段を書いた:\n${r.cms.join('\n')}`);
  }
});

ok('合わせ（段 2）: 行の欠けたユーザーが 1 人でも居れば、何も変えずに止まる', () => {
  const r = reconcile({ ...ON_BROKER, env: { RABBITMQ_PERMISSIONS: 'narrow' }, perms: PERMS_FIXTURE.split('\n').filter((l) => !l.startsWith('msp-b')).join('\n') });
  assert.strictEqual(r.status, 1, r.out);
  assert.deepStrictEqual(r.writes, [], `欠けがあるのに書いた:\n${r.writes.join('\n')}`);
  assert.ok(/msp-b/.test(r.out), r.out);
  const none = reconcile({ env: { RABBITMQ_PERMISSIONS: 'narrow' }, perms: null });
  assert.strictEqual(none.status, 1);
  assert.deepStrictEqual(none.writes, []);
});

ok('合わせ（段 2）: ブローカに在るものが絞った権限で届かなければ、何も変えずに止まる（押し切りは明示だけ）', () => {
  const gap = { ...ON_BROKER, queues: [...ON_BROKER.queues, 'c-service.Orphan'], exchanges: [...ON_BROKER.exchanges, 'EvZ'], bindings: [...ON_BROKER.bindings, ['EvZ', 'a-service.EvX']] };
  const r = reconcile({ ...gap, env: { RABBITMQ_PERMISSIONS: 'narrow' } });
  assert.strictEqual(r.status, 1, r.out);
  assert.deepStrictEqual(r.sets, [], '届かないものがあるのに当てた');
  for (const w of ['キュー c-service.Orphan', 'exchange EvZ', '束ね EvZ → a-service.EvX']) assert.ok(r.out.includes(w), `${w} を名指さない:\n${r.out}`);
  const forced = reconcile({ ...gap, env: { RABBITMQ_PERMISSIONS: 'narrow', RABBITMQ_PERMISSIONS_ALLOW_GAPS: '1' } });
  assert.strictEqual(forced.status, 0, forced.out);
  assert.strictEqual(forced.sets.length, 2);
});

ok('点検（--permissions-report）: 何も書かない。変わるユーザーと届かないものを出し、届かなければ exit 1', () => {
  const clean = reconcile({ ...ON_BROKER, args: ['--permissions-report'], current: 'msp-a .* .* .*\n' + PERMS_FIXTURE.split('\n').find((l) => l.startsWith('msp-b')) + '\n' });
  assert.strictEqual(clean.status, 0, clean.out);
  assert.deepStrictEqual(clean.writes, [], `点検が書いた:\n${clean.writes.join('\n')}`);
  assert.ok(/同じユーザー: 1 件/.test(clean.out) && /変わるユーザー: msp-a$/m.test(clean.out), clean.out);
  const gap = reconcile({ ...ON_BROKER, queues: ['z-service.Q'], args: ['--permissions-report'] });
  assert.strictEqual(gap.status, 1, gap.out);
  assert.deepStrictEqual(gap.writes, []);
  assert.ok(/キュー z-service\.Q/.test(gap.out), gap.out);
});

ok('合わせ: 段の値を間違えたら何もしない（exit 2）', () => {
  const r = reconcile({ env: { RABBITMQ_PERMISSIONS: 'narrowed' } });
  assert.strictEqual(r.status, 2, r.out);
  assert.deepStrictEqual(r.writes, []);
});

ok('合わせ（実物の生成物）: 宣言の全ユーザーに行があり、段 2 で当てる値は生成物の値そのもの', () => {
  const H = fs.mkdtempSync(path.join(os.tmpdir(), 'rmqperm-real-'));
  try {
    const bin = path.join(H, 'bin');
    fs.mkdirSync(bin);
    fs.writeFileSync(path.join(bin, 'kubectl'), KUBECTL, { mode: 0o755 });
    for (const f of ['queues', 'exchanges', 'bindings', 'perms', 'log']) fs.writeFileSync(path.join(H, f), '');
    const r = spawnSync('bash', [RECONCILE], { encoding: 'utf8', env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, STUB_H: H,
      RABBITMQ_PERMISSIONS: 'narrow', RABBITMQ_USERS_TIMEOUT: '1' } });
    assert.strictEqual(r.status, 0, `${r.stdout}${r.stderr}`);
    const sets = fs.readFileSync(path.join(H, 'log'), 'utf8').split('\n').filter((l) => l.startsWith('SET '));
    assert.deepStrictEqual(sets, users.map((u) => { const p = committed.get(u.user); return `SET ${u.user} ${p.configure} ${p.write} ${p.read}`; }));
  } finally {
    fs.rmSync(H, { recursive: true, force: true });
  }
});

// --- 7 ----------------------------------------------------------------------------------------------------------
const BOOT = (() => {
  const m = BROKER.match(/\n  restore-users\.sh: \|\n((?:    .*\n|\n)+)/);
  assert.ok(m, 'rabbitmq.yaml に restore-users.sh が無い');
  return m[1].split('\n').map((l) => l.replace(/^    /, '')).join('\n');
})();
function boot({ scope = null, perms = null }) {
  const H = fs.mkdtempSync(path.join(os.tmpdir(), 'rmqperm-boot-'));
  try {
    const bin = path.join(H, 'bin');
    const dir = path.join(H, 'users');
    const pdir = path.join(H, 'perms');
    fs.mkdirSync(bin); fs.mkdirSync(dir); fs.mkdirSync(pdir);
    fs.writeFileSync(path.join(dir, 'msp-a'), 'pa');
    fs.writeFileSync(path.join(dir, 'msp-b'), 'pb');
    if (scope !== null) fs.writeFileSync(path.join(pdir, 'scope'), scope);
    if (perms !== null) fs.writeFileSync(path.join(pdir, 'permissions.txt'), perms);
    fs.writeFileSync(path.join(H, 'cookie'), 'c');
    fs.writeFileSync(path.join(H, 'boot.sh'), BOOT);
    fs.writeFileSync(path.join(bin, 'rabbitmqctl'), '#!/bin/sh\ncase "$1 $2" in "add_user "*|"change_password "*) cat >/dev/null;; esac\nprintf "%s\\n" "$*" >> "$STUB_H/calls"\nexit 0\n', { mode: 0o755 });
    fs.writeFileSync(path.join(H, 'calls'), '');
    const r = spawnSync('sh', [path.join(H, 'boot.sh')], { encoding: 'utf8', env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, STUB_H: H,
      RABBITMQ_BOOT_USERS_DIR: dir, RABBITMQ_BOOT_PERMISSIONS_DIR: pdir, RABBITMQ_BOOT_FLAG: path.join(H, 'flag'), RABBITMQ_BOOT_TRIES: '2',
      RABBITMQ_BOOT_COOKIE: path.join(H, 'cookie'), RABBITMQ_BOOT_LOG: path.join(H, 'boot.log') } });
    return { status: r.status, sets: fs.readFileSync(path.join(H, 'calls'), 'utf8').split('\n').filter((l) => / set_permissions /.test(l)),
      log: fs.existsSync(path.join(H, 'boot.log')) ? fs.readFileSync(path.join(H, 'boot.log'), 'utf8') : '' };
  } finally {
    fs.rmSync(H, { recursive: true, force: true });
  }
}

ok('起動の直後の復元: ConfigMap が無い・vhost なら vhost 全体、narrow なら生成物の行。行の無いユーザーへ vhost 全体を付けない', () => {
  for (const scope of [null, 'vhost', 'applying']) {
    const r = boot({ scope, perms: PERMS_FIXTURE });
    assert.deepStrictEqual(r.sets, ['-q set_permissions -p / msp-a .* .* .*', '-q set_permissions -p / msp-b .* .* .*'], `scope=${scope}`);
  }
  const n = boot({ scope: 'narrow\n', perms: PERMS_FIXTURE });
  assert.strictEqual(n.status, 0);
  assert.deepStrictEqual(n.sets, [
    '-q set_permissions -p / msp-a ^(a-service\\..*|amq\\.default)$ ^(a-service\\..*|amq\\.default|EvA)$ ^(a-service\\..*)$',
    '-q set_permissions -p / msp-b ^(b-service\\..*|EvA)$ ^(b-service\\..*)$ ^(b-service\\..*|EvA)$',
  ]);
  assert.ok(/権限 narrow/.test(n.log), n.log);
  const partial = boot({ scope: 'narrow', perms: PERMS_FIXTURE.split('\n').filter((l) => !l.startsWith('msp-b')).join('\n') });
  assert.strictEqual(partial.status, 0, '復元の失敗でコンテナを止めた');
  assert.deepStrictEqual(partial.sets.map((l) => l.split(' ')[4]), ['msp-a'], '行の無いユーザーにも権限を付けた');
  // 記録は PID 1 の標準出力へ 1 行ずつ上書きで出る（模型ではファイル）。最後の行が失敗の数を持つ。
  assert.ok(/失敗 1 件/.test(partial.log), partial.log);
  assert.ok(/絞った権限の行が無い（権限を付けない）: \$u/.test(BOOT), '行の無いユーザーを記録しない');
});

ok('ブローカの manifest は ConfigMap rabbitmq-permissions を optional でマウントし、合わせと同じ名前を使う', () => {
  assert.ok(/name: permissions\n\s*configMap: \{ name: rabbitmq-permissions, optional: true \}/.test(BROKER), 'optional でマウントしていない');
  assert.ok(/mountPath: \/etc\/rabbitmq-permissions/.test(BROKER) && /\/etc\/rabbitmq-permissions/.test(BOOT), 'マウントのパスが食い違う');
  assert.ok(/PERMS_CM="\$\{RABBITMQ_PERMISSIONS_CONFIGMAP:-rabbitmq-permissions\}"/.test(fs.readFileSync(RECONCILE, 'utf8')), '合わせの ConfigMap の名前が食い違う');
});

// --- 8 ----------------------------------------------------------------------------------------------------------
// NFR-18, IADR-0694: クラウドの写し。取引ユニットはクラウドで購読の exchange を受動で宣言する（AST の IADR-0628）。
const CLOUD_TEXT = fs.readFileSync(path.join(REPO_ROOT, gen.CLOUD_PERMS_REL), 'utf8');
const cloudRows = (() => {
  const m = CLOUD_TEXT.match(/\n  permissions\.txt: \|\n((?:    .*\n)+)/);
  assert.ok(m, 'rabbitmq-permissions.yaml に permissions.txt が無い');
  return new Map(m[1].trim().split('\n').map((l) => l.trim().split(/\s+/)).map(([user, configure, write, read]) => [user, { user, configure, write, read }]));
})();
const astUsers = users.filter((u) => u.namespace === 'ai-stock-trading').map((u) => u.user);
const astDerived = G.derived.filter((d) => d.unit === 'ai-stock-trading');

ok('クラウドの写し: 取引ユニットの行だけを持ち、configure の exchange は発行するものだけ（write・read はローカルの行と同じ）', () => {
  assert.deepStrictEqual([...cloudRows.keys()], astUsers);
  for (const u of astUsers) {
    const local = committed.get(u);
    const cloud = cloudRows.get(u);
    assert.strictEqual(cloud.write, local.write, `${u} の write`);
    assert.strictEqual(cloud.read, local.read, `${u} の read`);
    assert.deepStrictEqual(cloud, gen.cloudRow(local), `${u} の行がローカルの行から作った値と違う`);
  }
  for (const d of astDerived) {
    const p = gen.patternsFor(d, { cloud: true });
    assert.deepStrictEqual(cloudRows.get(d.user), { user: d.user, ...p }, `${d.user}: 行から作った値が導出から作った値と違う`);
  }
});

ok('クラウドの写し（欠け）: 購読に要る操作（受動の宣言・束ね）と発行に要る操作は、すべてその権限に当たる', () => {
  for (const d of astDerived) {
    const miss = gen.requiredAccess(d, { cloud: true }).filter((a) => !rx(cloudRows.get(d.user)[a.perm]).test(a.name));
    assert.deepStrictEqual(miss, [], `${d.user} のクラウドの権限が欠ける:\n  ${miss.map((a) => `${a.perm} ${a.name}（${a.why}）`).join('\n  ')}`);
  }
});

/** クラウドで拒まれるべき操作: ローカルの陰性対照に、購読する（発行しない）exchange の configure（宣言・削除）を足す。 */
function forbiddenInCloud(d) {
  return [...forbiddenAccess(d), ...d.subscribe.filter((ex) => !d.publish.includes(ex))
    .map((ex) => ({ perm: 'configure', name: ex, why: '購読する（発行しない）exchange の宣言・削除（クラウドは受動の宣言）' }))];
}
const cloudViolations = (d, row) => forbiddenInCloud(d).filter((a) => rx(row[a.perm]).test(a.name)).map((a) => `${a.perm} ${a.name}（${a.why}）`);

ok('クラウドの写し（陰性対照）: 購読する（発行しない）exchange の configure は誰も持たない。発行する exchange の configure は残る', () => {
  let subOnly = 0;
  for (const d of astDerived) {
    const v = cloudViolations(d, cloudRows.get(d.user));
    assert.deepStrictEqual(v, [], `${d.user} がクラウドで拒まれるべき操作を許す:\n  ${v.join('\n  ')}`);
    for (const ex of d.publish) assert.ok(rx(cloudRows.get(d.user).configure).test(ex), `${d.user}: 発行する ${ex} の configure が無い（発行側の宣言が拒まれる）`);
    subOnly += d.subscribe.filter((ex) => !d.publish.includes(ex)).length;
  }
  if (hasAst) assert.ok(subOnly >= 20, `購読だけの exchange が少なすぎる（${subOnly}。陰性対照が痩せた）`);
  // 行だけ（AST の写しが無くても）で読める形: configure の exchange ⊆ write の exchange。
  for (const [u, r] of cloudRows) {
    const write = new Set(r.write.slice(2, -2).split('|'));
    const extra = r.configure.slice(2, -2).split('|').slice(2).filter((x) => !write.has(x));
    assert.deepStrictEqual(extra, [], `${u}: 発行しない exchange の configure がある`);
  }
});

ok('クラウドの写し（変異）: 購読の exchange の configure を戻す（ローカルの行のまま写す）と、陰性対照が必ず赤になる', () => {
  if (!hasAst) return;
  const caught = astDerived.filter((d) => d.subscribe.some((ex) => !d.publish.includes(ex)))
    .map((d) => [d.user, cloudViolations(d, committed.get(d.user)).length > 0]);
  assert.ok(caught.length >= 5, `購読だけの exchange を持つ利用者が少なすぎる（${caught.length}）`);
  for (const [u, hit] of caught) assert.ok(hit, `${u}: ローカルの行（購読の exchange の configure あり）を陰性対照が検出しない`);
});

ok('クラウドの写し: exchanges.json は購読される exchange の和で、Wolverine の宣言と同じ形（fanout・durable・自動削除しない・引数なし）', () => {
  const m = CLOUD_TEXT.match(/\n  exchanges\.json: '(.*)'\n/);
  assert.ok(m, 'rabbitmq-permissions.yaml に exchanges.json が無い');
  const defs = JSON.parse(m[1]);
  assert.deepStrictEqual(Object.keys(defs), ['exchanges'], 'exchange 以外（利用者・権限・キュー）を持ち込まない');
  const names = defs.exchanges.map((e) => e.name);
  assert.deepStrictEqual(names, [...names].sort(), '並びが決まっていない');
  for (const e of defs.exchanges) {
    assert.deepStrictEqual(e, { name: e.name, vhost: '/', type: 'fanout', durable: true, auto_delete: false, internal: false, arguments: {} });
  }
  const want = [...new Set([...cloudRows.values()].flatMap(gen.subscribedExchanges))].sort();
  assert.deepStrictEqual(names, want);
  if (hasAst) assert.deepStrictEqual(names, [...new Set(astDerived.flatMap((d) => d.subscribe))].sort(), '導出の購読の和と違う');
  assert.ok(names.length >= 20, `購読される exchange が少なすぎる（${names.length}）`);
});

const CLOUD_BROKER = fs.readFileSync(path.join(REPO_ROOT, 'deploy/cloud/infra/rabbitmq.yaml'), 'utf8');
const CLOUD_BOOT = (() => {
  const m = CLOUD_BROKER.match(/\n  restore-users\.sh: \|\n((?:    .*\n|\n)+)/);
  assert.ok(m, 'クラウドの rabbitmq.yaml に restore-users.sh が無い');
  return m[1].split('\n').map((l) => l.replace(/^    /, '')).join('\n');
})();
function cloudBoot({ exchanges, importFails = false, flag = null, onIncomplete = null, failPermissions = false }) {
  const H = fs.mkdtempSync(path.join(os.tmpdir(), 'rmqperm-cloudboot-'));
  try {
    const bin = path.join(H, 'bin');
    const dir = path.join(H, 'users');
    const pdir = path.join(H, 'perms');
    fs.mkdirSync(bin); fs.mkdirSync(dir); fs.mkdirSync(pdir);
    fs.writeFileSync(path.join(dir, 'ast-a'), 'pa');
    fs.writeFileSync(path.join(pdir, 'permissions.txt'), 'ast-a ^(a\\..*)$ ^(a\\..*)$ ^(a\\..*|EvA)$\n');
    if (exchanges) fs.writeFileSync(path.join(pdir, 'exchanges.json'), exchanges);
    if (flag !== null) fs.writeFileSync(path.join(H, 'flag'), flag);
    if (onIncomplete !== null) fs.writeFileSync(path.join(H, 'on-incomplete'), onIncomplete);
    fs.writeFileSync(path.join(H, 'cookie'), 'c');
    fs.writeFileSync(path.join(H, 'boot.sh'), CLOUD_BOOT);
    // 読み込みの失敗の模型（rabbitmqctl の実際の形: 「Error:」の次の行に詳細）。標準出力に出すものは捨てられるべき。
    const stderr = { short: '\\nError:\\n\\tvhost /nope does not exist\\n', long: '\\n  Error: %0300d\\nCUT-OFF-SHOULD-NOT-APPEAR\\n' }[importFails];
    const failImport = stderr
      ? `case "$*" in *import_definitions*) echo "STDOUT-SHOULD-NOT-APPEAR"; printf "${stderr}" 0 >&2; exit 2;; esac\n`
      : '';
    // NFR-18, IADR-0702: 利用者の権限を当てられない（揃わない）形の模型。
    const failPerm = failPermissions ? 'case "$*" in *set_permissions*) exit 2;; esac\n' : '';
    fs.writeFileSync(path.join(bin, 'rabbitmqctl'), `#!/bin/sh\ncase "$1 $2" in "add_user "*|"change_password "*) cat >/dev/null;; esac\nprintf "%s\\n" "$*" >> "$STUB_H/calls"\n${failImport}${failPerm}exit 0\n`, { mode: 0o755 });
    fs.writeFileSync(path.join(H, 'calls'), '');
    // 記録は 1 回ずつ開き直して上書きで出る（PID 1 の標準出力の模型）。全行を受けるため FIFO へ書かせ、読み手が集める
    // （書き手を 1 つ開いたままにして、読み手が途中で EOF にならないようにする）。
    const fifo = path.join(H, 'log.fifo');
    const wrapper = `mkfifo "${fifo}" && { cat "${fifo}" > "${path.join(H, 'boot.log')}" & } && exec 3>"${fifo}" && sh "${path.join(H, 'boot.sh')}"; rc=$?; exec 3>&-; wait; exit $rc`;
    const r = spawnSync('sh', ['-c', wrapper], { encoding: 'utf8', env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, STUB_H: H,
      RABBITMQ_BOOT_USERS_DIR: dir, RABBITMQ_BOOT_PERMISSIONS_DIR: pdir, RABBITMQ_BOOT_FLAG: path.join(H, 'flag'), RABBITMQ_BOOT_TRIES: '2',
      RABBITMQ_BOOT_COOKIE: path.join(H, 'cookie'), RABBITMQ_BOOT_LOG: fifo, RABBITMQ_BOOT_ON_INCOMPLETE: path.join(H, 'on-incomplete'), RABBITMQ_DEFAULT_USER: 'mqadmin_x' } });
    return { status: r.status, calls: fs.readFileSync(path.join(H, 'calls'), 'utf8').split('\n').filter(Boolean), pdir,
      log: fs.existsSync(path.join(H, 'boot.log')) ? fs.readFileSync(path.join(H, 'boot.log'), 'utf8') : '' };
  } finally {
    fs.rmSync(H, { recursive: true, force: true });
  }
}

ok('クラウドの復元: 利用者の権限を当てる前に exchanges.json を宣言する（表が無ければ宣言せず、利用者の復元は続ける）', () => {
  const r = cloudBoot({ exchanges: '{"exchanges":[]}' });
  assert.strictEqual(r.status, 0);
  const imp = r.calls.findIndex((c) => /^-q import_definitions .*\/perms\/exchanges\.json$/.test(c));
  const set = r.calls.findIndex((c) => / set_permissions /.test(c));
  assert.ok(imp >= 0, `import_definitions を呼ばない:\n${r.calls.join('\n')}`);
  assert.ok(set > imp, `権限を当てた後に宣言した:\n${r.calls.join('\n')}`);
  const none = cloudBoot({ exchanges: null });
  assert.strictEqual(none.status, 0);
  assert.ok(!none.calls.some((c) => /import_definitions/.test(c)), '表が無いのに宣言した');
  assert.ok(none.calls.some((c) => / set_permissions /.test(c)), '表が無いと利用者の権限を当てない');
  assert.ok(/mountPath: \/etc\/rabbitmq-permissions/.test(CLOUD_BROKER), '表のマウント先が食い違う');
});

ok('クラウドの復元: exchanges.json を読み込めないときは、stderr を 1 行に詰めて 200 字までに切って記録し、利用者の復元は続ける', () => {
  const reasonOf = (r) => {
    assert.strictEqual(r.status, 0, '読み込みの失敗でコンテナを止めた');
    const lines = r.log.split('\n').filter((l) => /購読される exchange を宣言できない/.test(l));
    assert.strictEqual(lines.length, 1, `失敗を 1 行で記録しない:\n${r.log}`);
    assert.ok(!/STDOUT-SHOULD-NOT-APPEAR/.test(r.log), '標準出力を記録した');
    assert.ok(r.calls.some((c) => / set_permissions /.test(c)), '読み込みの失敗で利用者の権限を当てない');
    assert.ok(/サービスごとのユーザーを復元した: 1 件/.test(r.log), r.log);
    return lines[0].split('起動を待つ）: ')[1] || '';
  };
  assert.strictEqual(reasonOf(cloudBoot({ exchanges: '{"exchanges":[]}', importFails: 'short' })), 'Error: vhost /nope does not exist',
    '「Error:」の次の行の詳細を落とした');
  const long = reasonOf(cloudBoot({ exchanges: '{"exchanges":[]}', importFails: 'long' }));
  assert.strictEqual(long.length, 200, `理由を 200 字に切っていない（${long.length}）`);
  assert.ok(/^Error: 0+$/.test(long) && !/CUT-OFF/.test(long), `切り方が違う: ${long.slice(0, 80)}`);
});

ok('IADR-0702: クラウドの復元の既定のユーザー — 揃えば消す。揃わなければ retain（既定・キーが無い・知らない値）は残して WARN を出し、delete は消す。印が無ければ触らない', () => {
  const deleted = (r) => r.calls.some((c) => /^-q delete_user mqadmin_x$/.test(c));
  const full = cloudBoot({ exchanges: null, flag: 'true' });
  assert.strictEqual(full.status, 0);
  assert.ok(deleted(full), `揃ったのに既定のユーザーを消さない:\n${full.calls.join('\n')}`);
  for (const mode of [null, 'retain', 'bogus']) {
    const r = cloudBoot({ exchanges: null, flag: 'true', onIncomplete: mode, failPermissions: true });
    assert.strictEqual(r.status, 0, '揃わないときにコンテナを止めた');
    assert.ok(!deleted(r), `揃わない・${mode} で既定のユーザーを消した`);
    assert.ok(/WARN: 既定のユーザーを残した/.test(r.log), `残したことを WARN で出さない（${mode}）:\n${r.log}`);
  }
  const del = cloudBoot({ exchanges: null, flag: 'true', onIncomplete: 'delete', failPermissions: true });
  assert.strictEqual(del.status, 0);
  assert.ok(deleted(del), `delete なのに揃わないときに消さない:\n${del.calls.join('\n')}`);
  assert.ok(/既定のユーザーを消した（無効化の印に従う。揃わなかったときの扱い: delete）/.test(del.log), del.log);
  const noFlag = cloudBoot({ exchanges: null, onIncomplete: 'delete', failPermissions: true });
  assert.ok(!deleted(noFlag), '無効化の印が無いのに消した');
  assert.ok(/mountPath: \/etc\/rabbitmq-default-user/.test(CLOUD_BROKER) && /\n  on-incomplete: "retain"\n/.test(CLOUD_BROKER), '段のキーの既定（retain）とマウント先が食い違う');
});

process.stdout.write(`rabbitmq-permissions.test.js: ${passed} passed\n`);
