#!/usr/bin/env node
'use strict';
/*
 * SC-15, FR-05, NFR-09, ADR-0026 / ADR-0078 決定 1, ADR-0094 決定 2, IADR-0432 (#1410):
 * **パスワードリセットの申請を、床（最小応答時間）に達するまで返さない前段**。
 *
 *   Keycloak の手前に置く逆プロキシとして常駐する（deploy/mail-relay/reset-floor.yaml）。
 *   node:22-alpine・**外部依存ゼロ**（Node 標準の http / url のみ）。門（reset-gate.js）・
 *   キュー exporter（mail-queue-exporter.js）・合成監視の probe.js と同じ作法である。
 *
 * ## なぜ要るか —— 差は偶然ではなく構造である
 *
 * 計画 ADR-0094 §コンテキストと課題 が稼働 k3s で測ったとおり、リセット申請の所要時間は
 * **実在する利用者名のほうが一貫して 1.9〜3.1 倍遅い**。機序は IADR-0404 が既に名指ししている ——
 * **Keycloak の送出は同期**であり（`DefaultEmailSenderProvider`）、**非実在の利用者名は
 * `forkWithSuccessMessage(EMAIL_SENT)` で即 200 を返す**（`ResetCredentialEmail`）。
 * 🔴 **非実在側はメールを作らず、送らない。** 近接 MTA を挟めば往復は localhost へ縮むが、
 * **往復そのものが消えない限り差は消えない。**
 *
 * そして **リセット申請には回数制限が 1 件も無い**（ADR-0094 実測 3。`NFR-13` のロックは
 * ログインの失敗回数にしか効かず、申請は資格情報を出さないので計上されない）。
 * **攻撃者は同じ名前を何度でも投げて中央値を取れる** —— 単発の分布が重なっても防御にならない。
 *
 * ## 🔴 「遅延を足す」部品では足りない（要るのは床である）
 *
 * Istio / Envoy の fault injection の `delay` は**固定値を両側に同じだけ足す**ため、**比を変えない**
 * （154:49 に 100 ms を足せば 254:149 で比は縮むが 0 にはならない。ADR-0094 実測 5）。
 * 要るのは「足す遅延」ではなく **床** である —— **応答が床より速く返りそうなら床まで待たせる。**
 * 床に達したあとは待たせないので、**両側とも床の時間で返る。**
 *
 * ## なぜ Envoy の Lua ではないのか（IADR-0432 決定 1）
 *
 * 🔴 **Envoy の Lua フィルタが公開するストリームハンドル API に待機（sleep / timer）が無い。**
 * 公開されているのは headers / body / bodyChunks / trailers / httpCall / respond / ログ /
 * metadata / streamInfo / connection などであり、**待てるのは `httpCall` の応答待ちだけ**である。
 * つまり Lua で床を作るには**待たせる相手を別に用意しなければならない** —— その相手がこれである。
 * （これは上流ドキュメントの API 面からの**導出**であり、稼働クラスタで打った実測ではない。）
 *
 * ## なぜ reset-gate ではないのか（同 決定 1）
 *
 * 🔴 **門（reset-gate.js）は要求経路に居ない。** 近接 MTA へ能動プローブを打ち、realm を PUT する
 * **制御ループ**であり、利用者の要求を握って待たせられない。**同じ配備単位に置くが、別の器である。**
 *
 * ## 🔴 本文・ステータス・ヘッダを 1 バイトも変えない
 *
 * 床が変えてよいのは**返す時刻だけ**である。存在秘匿の他の面（ステータス・本文のバイト一致）は
 * 近接 MTA と門が既に成立させており（ADR-0094 §統制と現在の実現手段）、**ここで触れば壊す。**
 * したがって上流の応答を**全部受け切ってから**、床に達するまで待ち、**そのまま**書き出す。
 * 本文は復号も再圧縮もしない（`content-encoding` はそのまま素通しする）。
 *
 * ## 🔴 数値の既定をコードが持たない
 *
 * `RESET_FLOOR_MS` / `UPSTREAM_URL` / `LISTEN_PORT` は**マニフェストが与える**。未設定なら**起動しない**。
 * 床の値そのものの導出は IADR-0432 が持つ（実在側の分布の上側を覆う値。ADR-0094 決定 2 は
 * 「計画は値を発明しない」と定めている）。**実装もここで発明しない** —— 起動しないほうが気付ける。
 *
 * 🔴 **本器は稼働クラスタで打っていない**（#1410 の作業機にクラスタが無い）。
 *    「動くはず」を実測として書かない。床を入れた構成での再実測は integration-stack が行う。
 *
 * 実行: node deploy/mail-relay/reset-floor.js
 *       node deploy/mail-relay/reset-floor.js --self-test   # 純関数の自己試験
 */
const http = require('http');

/** 逆プロキシが引き継いではならないヘッダ（RFC 7230 §6.1 の hop-by-hop）。 */
const HOP_BY_HOP = new Set([
  'connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
  'te', 'trailer', 'transfer-encoding', 'upgrade',
]);

/**
 * 床に達するまでの残り待ち時間。**純関数**。
 *
 * 🔴 **床を超えて返る応答は、そのまま出す**（負の待ちを作らない）。ADR-0094 §残るもの が
 * 「床が実在側の分布を覆えなかった場合、上側の裾は残る」と明記しており、**裾を隠す仕掛けを
 * ここへ足さない** —— 裾の扱いは計画が未裁定である。
 *
 * @param {number} startedAt 要求を受けた時刻（ms）
 * @param {number} now 上流の応答を受け切った時刻（ms）
 * @param {number} floorMs 床（ms）
 * @returns {number} 待つべき ms（0 以上）
 */
function holdDelayMs(startedAt, now, floorMs) {
  const elapsed = now - startedAt;
  const remaining = floorMs - elapsed;
  return remaining > 0 ? remaining : 0;
}

/**
 * マニフェストが与える構成を読む。**既定を持たない**（欠けていれば起動しない）。**純関数**。
 * @param {Record<string,string|undefined>} env
 * @returns {{ok:true, value:{listenPort:number, upstream:URL, floorMs:number}}|{ok:false, error:string}}
 */
function readConfig(env) {
  const missing = ['RESET_FLOOR_MS', 'UPSTREAM_URL', 'LISTEN_PORT']
    .filter((k) => !env[k] || String(env[k]).trim() === '');
  if (missing.length > 0) {
    return {
      ok: false,
      error: `[reset-floor] 構成が足りない: ${missing.join(' / ')}。`
        + ' 🔴 **既定値を持たない**（床の値は実測から定めるものであり、実装が発明しない）。'
        + ' マニフェスト（deploy/mail-relay/reset-floor/reset-floor.yaml）が与える。',
    };
  }
  const floorMs = Number(env.RESET_FLOOR_MS);
  if (!Number.isInteger(floorMs) || floorMs <= 0) {
    return { ok: false, error: `[reset-floor] RESET_FLOOR_MS が正の整数でない: ${env.RESET_FLOOR_MS}` };
  }
  const listenPort = Number(env.LISTEN_PORT);
  if (!Number.isInteger(listenPort) || listenPort <= 0 || listenPort > 65535) {
    return { ok: false, error: `[reset-floor] LISTEN_PORT が不正: ${env.LISTEN_PORT}` };
  }
  let upstream;
  try {
    upstream = new URL(String(env.UPSTREAM_URL));
  } catch (e) {
    return { ok: false, error: `[reset-floor] UPSTREAM_URL を URL として読めない: ${env.UPSTREAM_URL}` };
  }
  if (upstream.protocol !== 'http:') {
    // 🔴 上流は同じ namespace の Keycloak（メッシュ外・平文）である。TLS を張るなら
    //    ここではなくエッジの仕事であり、黙って `https:` を受けると「検証を切った TLS」になりかねない。
    return { ok: false, error: `[reset-floor] UPSTREAM_URL は http: のみ受ける（受け取った: ${upstream.protocol}）` };
  }
  return { ok: true, value: { listenPort, upstream, floorMs } };
}

/** 上流へ渡すヘッダ（hop-by-hop を落とすだけ。**それ以外は 1 つも足さない・変えない**）。 */
function forwardableHeaders(headers) {
  const out = {};
  for (const [k, v] of Object.entries(headers || {})) {
    if (!HOP_BY_HOP.has(String(k).toLowerCase())) out[k] = v;
  }
  return out;
}

/**
 * 床つきの逆プロキシ。上流の応答を**全部受け切ってから**床まで待ち、そのまま書き出す。
 * @param {{upstream:URL, floorMs:number}} cfg
 * @param {typeof http} [httpMod] 試験が差し替える
 */
function createServer(cfg, httpMod = http) {
  return httpMod.createServer((req, res) => {
    const startedAt = Date.now();
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('error', () => { res.statusCode = 502; res.end(); });
    req.on('end', () => {
      const body = Buffer.concat(chunks);
      const upstreamReq = httpMod.request({
        protocol: cfg.upstream.protocol,
        hostname: cfg.upstream.hostname,
        port: cfg.upstream.port,
        method: req.method,
        // 🔴 パスは**そのまま**渡す（書き換えると Keycloak のフロー識別子が壊れる）。
        path: req.url,
        headers: forwardableHeaders(req.headers),
      }, (upstreamRes) => {
        const outChunks = [];
        upstreamRes.on('data', (c) => outChunks.push(c));
        upstreamRes.on('end', () => {
          const out = Buffer.concat(outChunks);
          const headers = forwardableHeaders(upstreamRes.headers);
          // 本文を握ってから返すので長さは自分で宣言する（バイト列そのものは変えない）。
          delete headers['content-length'];
          const wait = holdDelayMs(startedAt, Date.now(), cfg.floorMs);
          setTimeout(() => {
            res.writeHead(upstreamRes.statusCode, headers);
            res.end(out);
          }, wait);
        });
      });
      upstreamReq.on('error', (e) => {
        /*
         * 🔴 **上流が落ちたときも床を守る。** ここで即座に 502 を返すと、
         * 「速い 502」と「床の時間で返る 200」が**所要時間で区別できる**状態を自分で作ることになる。
         */
        const wait = holdDelayMs(startedAt, Date.now(), cfg.floorMs);
        setTimeout(() => {
          if (!res.headersSent) res.writeHead(502, { 'content-type': 'text/plain; charset=utf-8' });
          res.end(`[reset-floor] 上流へ到達できない: ${e && e.message ? e.message : e}\n`);
        }, wait);
      });
      if (body.length > 0) upstreamReq.write(body);
      upstreamReq.end();
    });
  });
}

function selfTest() {
  const assert = require('assert');
  let n = 0;
  const ok = (name, fn) => { fn(); n += 1; console.log(`  ok  ${name}`); };

  ok('床より速い応答は床まで待つ', () => {
    assert.strictEqual(holdDelayMs(1000, 1040, 150), 110);
  });
  ok('🔴 床より遅い応答はそのまま出す（負の待ちを作らない・裾を隠さない）', () => {
    assert.strictEqual(holdDelayMs(1000, 1400, 150), 0);
    assert.strictEqual(holdDelayMs(1000, 1150, 150), 0);
  });
  ok('🔴 床は両側を同じ時刻へ揃える（足す遅延では比が残る）', () => {
    // 計画が実測した「床の無い」形: 実在 154 ms / 非実在 49 ms。
    const floor = 150;
    const existing = 154 + holdDelayMs(0, 154, floor); // 154（床を超えている）
    const absent = 49 + holdDelayMs(0, 49, floor); // 49 + 101 = 150
    assert.strictEqual(absent, floor);
    // 固定 delay（両側に +100 ms）だと比は 254 / 149 = 1.70 倍で残る。床なら 154 / 150 = 1.03 倍。
    assert.ok((existing / absent) < ((154 + 100) / (49 + 100)), '床が固定 delay より比を縮めていない');
  });
  ok('🔴 構成に既定を持たない（欠ければ起動しない）', () => {
    assert.strictEqual(readConfig({}).ok, false);
    assert.strictEqual(readConfig({ RESET_FLOOR_MS: '150', LISTEN_PORT: '8080' }).ok, false);
    assert.ok(readConfig({}).error.includes('RESET_FLOOR_MS'));
  });
  ok('構成の型を検査する（床は正の整数・上流は http の URL）', () => {
    const base = { UPSTREAM_URL: 'http://keycloak:8080', LISTEN_PORT: '8080' };
    assert.strictEqual(readConfig({ ...base, RESET_FLOOR_MS: '0' }).ok, false);
    assert.strictEqual(readConfig({ ...base, RESET_FLOOR_MS: '15.5' }).ok, false);
    assert.strictEqual(readConfig({ ...base, RESET_FLOOR_MS: 'いくつか' }).ok, false);
    assert.strictEqual(readConfig({ ...base, RESET_FLOOR_MS: '150', LISTEN_PORT: '0' }).ok, false);
    assert.strictEqual(readConfig({ ...base, UPSTREAM_URL: 'https://keycloak:8443', RESET_FLOOR_MS: '150' }).ok, false);
    const good = readConfig({ ...base, RESET_FLOOR_MS: '150' });
    assert.strictEqual(good.ok, true);
    assert.strictEqual(good.value.floorMs, 150);
    assert.strictEqual(good.value.upstream.hostname, 'keycloak');
  });
  ok('hop-by-hop ヘッダだけを落とし、それ以外は足さない・変えない', () => {
    const out = forwardableHeaders({
      host: 'keycloak.localhost', 'set-cookie': ['a=1', 'b=2'], connection: 'keep-alive',
      'transfer-encoding': 'chunked', 'content-encoding': 'gzip',
    });
    assert.deepStrictEqual(out, {
      host: 'keycloak.localhost', 'set-cookie': ['a=1', 'b=2'], 'content-encoding': 'gzip',
    });
  });

  console.log(`[reset-floor] self-test OK: ${n} 件`);
}

function main() {
  if (process.argv.slice(2).includes('--self-test')) { selfTest(); return; }
  const cfg = readConfig(process.env);
  if (!cfg.ok) {
    console.error(cfg.error);
    process.exit(2);
  }
  createServer(cfg.value).listen(cfg.value.listenPort, () => {
    console.log(`[reset-floor] listen=${cfg.value.listenPort} upstream=${cfg.value.upstream.origin}`
      + ` floor=${cfg.value.floorMs}ms`);
  });
}

if (require.main === module) main();

module.exports = { holdDelayMs, readConfig, forwardableHeaders, createServer, HOP_BY_HOP };
