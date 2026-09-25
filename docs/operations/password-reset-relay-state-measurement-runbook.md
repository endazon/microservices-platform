---
title: 運用 Runbook — パスワードリセットの近接 MTA の状態を意図的に作り、存在秘匿の窓とキューを実測する
type: runbook
status: draft
author: claude
created: 2026-09-26
updated: 2026-09-26
---
<!-- trace:
ids: [SC-15, SC-13, SC-10, FR-05, NFR-13]
adrs: [ADR-0026, ADR-0045, ADR-0078, ADR-0094, ADR-0097, ADR-0103, ADR-0108]
iadrs: [IADR-0347, IADR-0369, IADR-0404, IADR-0421, IADR-0427, IADR-0432, IADR-0463]
specs: [20260926_issue-1245_pr-d-state-measurement-runbook, 20260907_issue-1245_reset-gate, 20260909_issue-1245_mail-relay-observation, 20260911_issue-1245_login-existence-disclosure, 20260926_issue-1558_runbook-nits]
issues: [#1245, #1143, #1169, #1319, #1355, #1378, #1388, #1526, #1558, planning#596, planning#602, planning#656, planning#659]
-->

# 運用 Runbook: パスワードリセットの近接 MTA の状態を作り、窓とキューを実測する

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 近接 MTA の設定・値の投入は [`keycloak-smtp-relay-setup-runbook.md`](keycloak-smtp-relay-setup-runbook.md) が持つ。
> 本書は **#1245 の最後の段（PR-D）**、すなわち **稼働クラスタで状態を意図的に作って測る**ことだけを扱う。
>
> 🔴 **本書を実行するのは利用者（クラスタの持ち主）だけである。** 本書を書いた AI は稼働クラスタに 1 度も触れていない。
> **本書のコマンドと期待値は、リポジトリの宣言とコードから導いたものであって、実測ではない。**
> 期待値と違う結果が出たら、**期待値に合わせて読み替えず、出た値をそのまま記録する**（§5）。

## この手順を実行する条件（いつ走らせるか）

- #1245 を閉じるための実測（受け入れ基準「状態 C を意図的に作った実測を PR に貼る」）を取るとき。**1 回でよい。**
- クラスタの再構築（#1378）の後に、所要時間の床が稼働クラスタでも効いているかを測り直すとき（§2.1 だけでよい）。
- キューのアラートのしきい値を見直すとき（§3 だけでよい）。

**実行しなくてよい場合**: 開発機の使い捨てスタックでの確認だけなら、統合スタックの CI が状態 A を毎回測っている。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 対象クラスタの `platform-infra` 名前空間で `scale` / `patch networkpolicy` / `exec` / `logs` ができること。認証基盤の管理コンソールへ `master` の管理者で入れること（§4 の試験利用者の作成・削除、管理イベントの閲覧） |
| 必要なツール | `kubectl`（対象クラスタの context）・`node` 22・GNU `date`（Git Bash で可）・`curl`。**リポジトリのチェックアウト**（下の版） |
| チェックアウトの版 | `origin/develop` の **#1526 のマージコミット `33a21412` 以降**。確かめ方: `git merge-base --is-ancestor 33a21412 HEAD && echo OK` |
| シェル | 本書のコマンドは bash の書き方である（PowerShell では動かない） |
| 所要時間の目安 | 全体で約 3 時間（§2.2 の上流停止の保持 35 分、§2.4 の保持 15 分、§4 の待ち 15 分を含む）。**節ごとに分けて実行してよい**（節の終わりで必ず §0.5 の復元まで行う） |

---

## 0. 安全の前置き

### 0.1 触るもの・触らないもの

利用者のクラスタには本製品以外のワークロードも載っている。**本書が変更するのは次の表のものだけである。**
すべて `platform-infra` 名前空間の、メール送出とパスワードリセットに閉じた部品である。

| 対象 | 何をするか | 使う節 | 戻し方 |
| --- | --- | --- | --- |
| `deploy/mailpit`（捕捉用 MTA ＝ dev の上流） | レプリカを 0 にする | §2.2 | 元のレプリカ数へ |
| `deploy/mail-relay`（近接 MTA） | レプリカを 0 にする／稼働コンテナで `postconf` の 1 項目を変える | §2.3 / §2.4 | 元のレプリカ数へ／スナップショットの値へ |
| `networkpolicy/mail-relay-ingress` と `networkpolicy/mail-relay-ingress-reset-gate` | `from` の `podSelector` を一致しない値へ差し替える | §2.5 | 元のラベル値へ差し替え直す |
| `deploy/reset-gate`（申請を閉じる門） | **任意**でレプリカを 0 にする | §2.7 | 元のレプリカ数へ |
| realm `platform` の `resetPasswordAllowed` と `reset-gate.*` 属性 | **人は書かない。** 門が閉じ、門が開け直す | §2 全体 | 門が戻す（§0.5） |
| realm `platform` の試験利用者 1 人 | 作る → 一時ロックさせる → 削除する | §4 | 削除する |

**触らないもの**: 認証基盤（Keycloak）の Deployment・realm の宣言・Secret（`keycloak-smtp` / `reset-gate-oidc` を含む）・
床（`deploy/reset-floor` と Istio の経路）・他の名前空間。**`scripts/k8s-local-up.sh` と `scripts/istio-edge-up.sh` は本書の実行中に走らせない**
（前者は `platform-infra` を描画し直し、測っている状態を上書きする）。**床を外す退路（`RESET_FLOOR=0`）は本書では使わない。**

### 0.2 中止条件（1 つでも当たったら、その場で §0.5 の復元へ進む）

| # | 条件 | 理由 |
| --- | --- | --- |
| S1 | 上流（Secret `keycloak-smtp` の `host`）が捕捉用 MTA ではない | 状態 A と §2 の申請が**実在の宛先へ実メールを出す**。本書は dev の上流を前提にしている（未決事項 4） |
| S2 | 門が居ない（`deploy/reset-gate` が available 1 でない）状態で §2.2〜§2.5 を始めようとしている | 門が居ないと窓が閉じない。§2.7 以外で門の不在を前提にしない |
| S3 | 障害を入れてから **60 秒経っても**門が閉じない（門のログに `close:` が出ない、または `watch` で 200 / 500 が続く） | **利用者名を列挙できる窓が開いたまま**である。予想の上限（周期 10 秒 ＋ タイムアウト 10 秒 ＋ PUT）を大きく超えている |
| S4 | 門のログに `tick failed: PUT realm returned 401` / `403` が出る | `manage-realm` での PUT が通っていない。閉じるべきときに閉じられない |
| S5 | 認証基盤の Pod が再起動した・`OOMKilled` になった | 測っている状態が別物になる |
| S6 | 復元の後 **120 秒**経っても `mail-relay` が Ready にならない・門が開け直さない | 手順の外の故障である。§失敗したときの分岐へ |
| S7 | 他の誰かが同じクラスタで `platform-infra` を操作している（起動器・ArgoCD の同期・手動の apply） | 測定の前提が崩れる |
| S8 | §2.7（門を止める）で上限時間を超えた | 門を止めている間は窓が開きっぱなしである |

### 0.3 事前チェック

記録用のディレクトリを**リポジトリの外**に作り、以後の出力をすべてそこへ残す（コミットしない）。

```bash
export PRD="$HOME/prd-$(date -u +%Y%m%dT%H%M%SZ)"; mkdir -p "$PRD"
ts() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }          # UTC・ミリ秒。以後の時刻はすべてこれで取る
cd <リポジトリのルート>
git rev-parse HEAD | tee "$PRD/00-head.txt"
git merge-base --is-ancestor 33a21412 HEAD && echo "#1526 以降: OK" | tee -a "$PRD/00-head.txt"
kubectl config current-context | tee "$PRD/00-context.txt"      # 対象クラスタであることを目で確かめる
```

| 確かめること | コマンド | 期待 | 外れたら |
| --- | --- | --- | --- |
| 上流が捕捉用 MTA | `kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.host}' \| base64 -d; echo` | `mailpit.platform-infra.svc.cluster.local`（`host` は秘匿値ではない） | **S1。中止** |
| 近接 MTA・門・床・捕捉用 MTA が居る | `kubectl -n platform-infra get deploy mail-relay reset-gate reset-floor mailpit keycloak` | すべて `READY 1/1`（床は 1 以上） | 居ないものに依存する節を飛ばす（§5 に「未測」と書く） |
| 門の構成 | `kubectl -n platform-infra get deploy reset-gate -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}={.value}{"\n"}{end}'` | `PROBE_INTERVAL_SECONDS=10` / `PROBE_TIMEOUT_MS=10000` / `REOPEN_AFTER_SUCCESSES=3`（秘匿値は `valueFrom` なので空で出る） | 実際の値を記録し、§2 の上限の見積もりをその値で置き換える |
| 門が健全に回っている | `kubectl -n platform-infra logs deploy/reset-gate --timestamps --since=5m` | `起動:` の行があり、`tick failed:` が無い | `tick failed:` があれば S4 に準じて原因を先に直す |
| 床の経路が入っている | `kubectl -n istio-system get virtualservice msp-keycloak-edge -o jsonpath='{.spec.http[0].name}'; echo` | `reset-credentials-floor` | 床が無い構成である。§2.1 の所要時間は「床なし」と明記して記録する |
| 観測スタックが在る | `kubectl -n platform-infra get svc prometheus` | Service が在る（観測スタックは opt-in） | §3 のうち Prometheus とアラートの行は「未測」。exporter の値だけ記録する |
| NetworkPolicy を強制するか | §2.5 の冒頭で確かめる | — | — |

### 0.4 事前スナップショット

**復元の正解はこのスナップショットである。** 各節の終わりの復元は、ここと同じ値へ戻ったことを確かめて終える。

まず、realm の状態を読む関数を定義する（§1.2。門の Pod の中から、門自身の資格情報で **読むだけ**の要求を打つ）。

```bash
realm_gate_state() {
  kubectl -n platform-infra exec deploy/reset-gate -c gate -- node -e '
const e = process.env;
(async () => {
  const tr = await fetch(`${e.KC_URL}/realms/${e.KC_REALM}/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "client_credentials", client_id: e.GATE_CLIENT_ID, client_secret: e.GATE_CLIENT_SECRET }),
  });
  if (!tr.ok) throw new Error(`token ${tr.status}`);
  const { access_token: token } = await tr.json();
  const rr = await fetch(`${e.KC_URL}/admin/realms/${e.KC_REALM}`, { headers: { authorization: `Bearer ${token}` } });
  if (!rr.ok) throw new Error(`GET realm ${rr.status}`);
  const r = await rr.json();
  const a = r.attributes || {};
  console.log(JSON.stringify({
    at: new Date().toISOString(),
    resetPasswordAllowed: r.resetPasswordAllowed,
    state: a["reset-gate.state"] ?? null,
    reason: a["reset-gate.reason"] ?? null,
    since: a["reset-gate.since"] ?? null,
  }));
})().catch((x) => { console.error(x.message); process.exit(1); });'
}
```

🔴 **秘匿値は出力しない**（`client_secret` は Pod の env のまま使い、表示しない）。GET だけなので管理イベントも増えない。

```bash
NS=platform-infra
realm_gate_state                                                              | tee "$PRD/01-realm.json"
kubectl -n $NS get deploy mail-relay reset-gate reset-floor mailpit keycloak \
  -o custom-columns=NAME:.metadata.name,REPLICAS:.spec.replicas,READY:.status.readyReplicas \
                                                                              | tee "$PRD/01-replicas.txt"
kubectl -n $NS get pods -l 'app in (mail-relay,reset-gate,reset-floor,mailpit,keycloak)' -o wide \
                                                                              | tee "$PRD/01-pods.txt"
kubectl -n $NS get networkpolicy mail-relay-ingress mail-relay-ingress-reset-gate mail-relay-ingress-otel-collector \
  -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.spec.ingress}{"\n"}{end}'  | tee "$PRD/01-netpol.txt"
for p in queue_minfree smtpd_client_restrictions; do
  echo "$p explicit=[$(kubectl -n $NS exec deploy/mail-relay -c postfix -- postconf -n "$p")]" \
       "effective=[$(kubectl -n $NS exec deploy/mail-relay -c postfix -- postconf -h "$p")]"
done                                                                          | tee "$PRD/01-postconf.txt"
kubectl -n $NS exec deploy/mail-relay -c queue-exporter -- wget -qO- http://127.0.0.1:9154/metrics \
                                                                              | tee "$PRD/01-queue-metrics.txt"
kubectl -n $NS logs deploy/reset-gate --timestamps --since=10m                | tee "$PRD/01-gate.log"
```

期待（健全な状態 A）: `resetPasswordAllowed=true`、`state` は `null`（門が一度も閉じていない）か `"open"`、
`postfix_up 1`、`postfix_queue_size{queue="deferred"} 0`。**違っていたら測る前に原因を確かめる**（既に門が閉じている・キューが溜まっている）。

観測スタックが在るなら、アラートの現況も残す（別の端末で `kubectl -n platform-infra port-forward svc/prometheus 9090:9090` を開いておく）。

```bash
curl -s 'http://localhost:9090/api/v1/alerts' | tee "$PRD/01-alerts.json" | head -c 2000; echo
curl -s 'http://localhost:9090/api/v1/rules?type=alert' | grep -o '"name":"MailRelay[A-Za-z]*"' | sort -u | tee "$PRD/01-rules.txt"
```

期待: 規則に `MailRelayDeferredBacklog` / `MailRelayDeferredMessageNearExpiry` / `MailRelayQueueSeriesAbsent` の 3 件があり、いずれも発火していない。

### 0.5 復元手順（スナップショットへ戻す）

**各節の終わりで、その節で変えたものだけを戻す。** 最後に全体を 1 度照合する。

| 変えたもの | 戻すコマンド | 戻ったことの確かめ方 |
| --- | --- | --- |
| `mailpit` のレプリカ | `kubectl -n platform-infra scale deploy/mailpit --replicas=<スナップショットの値>` → `kubectl -n platform-infra rollout status deploy/mailpit --timeout=120s` | READY が元の数 |
| `mail-relay` のレプリカ | `kubectl -n platform-infra scale deploy/mail-relay --replicas=<値>` → `rollout status deploy/mail-relay --timeout=120s` | READY が元の数。**新しい Pod は空の spool で起動する**（§2.4 で確かめる項目） |
| `postconf` の項目 | スナップショットの `explicit=[]` が空（明示の設定が無かった）なら `postconf -X <項目>`、`explicit=[<項目> = <値>]` だったなら `postconf -e '<項目>=<値>'`。続けて `postfix reload`（いずれも `kubectl -n platform-infra exec deploy/mail-relay -c postfix -- …`） | `postconf -n` / `postconf -h` がスナップショットと同じ。**Pod を作り直しても戻る**（起動時に初期化スクリプトが導き直す）が、作り直すと §2.4 の状態 C2 が混ざるので、まず上の方法で戻す |
| NetworkPolicy の `podSelector` | §2.5 の「戻す」 | `01-netpol.txt` と同じ出力 |
| `reset-gate` のレプリカ | `kubectl -n platform-infra scale deploy/reset-gate --replicas=<値>` → `rollout status` | READY が元の数。門のログに `起動:` |
| realm | **人は触らない。** 近接 MTA が健全に戻れば、門が連続 3 回の成功のあと `reopen:` で `resetPasswordAllowed=true` に戻す | `realm_gate_state` で `resetPasswordAllowed=true` / `state="open"` |
| 試験利用者 | §4.4 | 管理コンソールの利用者一覧に居ない |

最後の照合: §0.4 のコマンドを `02-` の接頭辞で撮り直し、`diff "$PRD/01-replicas.txt" "$PRD/02-replicas.txt"` のように 1 つずつ比べる。

**戻らないもの（差として記録する）**

- 🔴 **realm の `reset-gate.*` 属性。** 門が 1 度でも閉じて開け直すと、`state="open"`・`reason`・`since` が残る。
  実験前が `null` だった場合も、**門が平常運転で作る姿**であり、門と後追いの Job はこの状態を健全として扱う。
  消すには人手で `manage-realm` の PUT を打つことになり、**門を機械にした理由（人手で realm を書かない）に反する**ので本書は消さない（未決事項 5）。
- Pod 名（`mail-relay` を 0 → 1 にすると変わる）と、再作成前の spool の中身（寿命 30 分のメールだけ）。
- 認証基盤の管理イベント・利用者イベント・Postfix のログ（記録として残る）。捕捉用 MTA に溜まったメール。

---

## 1. 使い捨ての測定器（リポジトリへ入れない）

既存の検査器 2 本は、本書の状態のいくつかを測れない ——
`check-password-reset-mail.js` は**閉じた状態では申請を打たずに終わり**、上流が落ちていると**捕捉用 MTA の API を読めず前提で止まる**。
`check-login-existence-disclosure.js` は**対象が realm 宣言の最初の対話利用者（`admin`）に固定**で、ロックの手前で止まる。
そこで、両検査器の export を**そのまま借りる**小さな測定器を `$PRD` に置いて使う（URL・本文の正規化・時計は検査器と同じ）。

### 1.1 `reset-pair.js` —— リセット申請を実在／非実在の対で打つ

実在側は realm 宣言の最初の対話利用者、非実在側は**同じバイト長**で宣言に居ない名前である
（名前の長さが違うと本文長が必ず違う —— #1245 の 2026-09-10 の測り方の失敗を、前提で落とす）。
本文そのものは出さず、**正規化後の本文が一致するか**とバイト長だけを出す。

```bash
cat > "$PRD/reset-pair.js" <<'EOF'
'use strict';
// PR-D 用の使い捨て測定器（リポジトリへは入れない）。リポジトリのルートで実行する。
//   node "$PRD/reset-pair.js" once                      対を 1 回（新しいフローで）
//   node "$PRD/reset-pair.js" window <対の数> <間隔ms>   開いている間にフォームを先取りし、1 対ずつ POST し続ける
//   node "$PRD/reset-pair.js" watch <秒> <間隔ms>        新しいフローで対を打ち続ける（再開の観測）
//   node "$PRD/reset-pair.js" timing <反復> <片側標本>   所要時間の生の標本（実在と非実在を交互に打つ）
const path = require('path');
const m = require(path.resolve('scripts', 'check-password-reset-mail.js'));

const sleep = (ms) => new Promise((r) => { setTimeout(r, ms); });
const now = () => new Date().toISOString();
const msSince = (t0) => Number(process.hrtime.bigint() - t0) / 1e6;

function context() {
  const realmRes = m.loadRealm();
  if (!realmRes.ok) throw new Error(realmRes.error);
  const realm = realmRes.value;
  const user = m.pickTargetUser(realm);
  const client = m.pickBrowserFlowClient(realm);
  const base = m.keycloakBaseUrl();
  const ca = m.edgeCa();
  if (!user || !client || !base.ok || !ca.ok) {
    throw new Error('前提を満たさない（対話利用者・標準フローのクライアント・エッジ URL・ローカル CA のいずれか）');
  }
  const absent = m.makeAbsentUsernameOfLength(realm, Buffer.byteLength(user.username));
  const pairing = m.evaluateTimingPair({
    existingUsername: user.username,
    absentUsername: absent || '',
    realmUsernames: (realm.users || []).map((u) => String(u.username || '')),
  });
  if (pairing.length > 0) throw new Error(pairing.join(' / '));
  return { realmName: realm.realm, client, base: base.value, ca: ca.value, existing: user.username, absent };
}

// 申請の前半（認可要求 → ログイン画面 → 申請画面 → フォームの action）。検査器の submitResetRequest と同じ URL を組む。
async function prepare(ctx, username) {
  try {
    const jar = m.createJar();
    const realmBase = `${ctx.base}/realms/${encodeURIComponent(ctx.realmName)}`;
    const authUrl = `${realmBase}/protocol/openid-connect/auth`
      + `?client_id=${encodeURIComponent(ctx.client.clientId)}`
      + `&redirect_uri=${encodeURIComponent(ctx.client.redirectUri)}`
      + '&response_type=code&scope=openid&state=pr-d-reset-pair'
      + '&code_challenge_method=S256&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM';
    const login = await m.request(authUrl, { jar, ca: ctx.ca });
    if (login.status !== 200) return { error: `login-page-${login.status}` };
    const href = /login-actions\/reset-credentials[^"']*/.exec(login.body);
    if (!href) return { error: 'no-reset-link' }; // 申請が閉じるとログイン画面から導線が消える
    const page = await m.request(`${realmBase}/${m.decodeEntities(href[0])}`, { jar, ca: ctx.ca });
    const action = /action="([^"]*login-actions\/reset-credentials[^"]*)"/.exec(page.body);
    if (!action) return { error: `no-form-action-${page.status}` };
    return { jar, url: m.decodeEntities(action[1]), username };
  } catch (e) {
    return { error: `network-${e.code || e.message}` };
  }
}

// 申請の後半（POST）。計るのは POST だけ（検査器と同じ。時計は単調時計の整数 ns）。
async function post(ctx, p) {
  if (p.error) return { error: p.error };
  const t0 = process.hrtime.bigint();
  try {
    const res = await m.request(p.url, {
      method: 'POST', jar: p.jar, ca: ctx.ca, body: `username=${encodeURIComponent(p.username)}`,
    });
    return { status: res.status, ms: msSince(t0), norm: m.normalizeConcealmentBody(res.body, p.username) };
  } catch (e) {
    return { error: `network-${e.code || e.message}`, ms: msSince(t0) };
  }
}

function view(r) {
  if (r.error) return r.ms === undefined ? { error: r.error } : { error: r.error, ms: Number(r.ms.toFixed(3)) };
  return { status: r.status, ms: Number(r.ms.toFixed(3)), bodyBytes: Buffer.byteLength(r.norm) };
}

function emit(extra, e, a) {
  const bodyEqual = e.norm !== undefined && a.norm !== undefined ? e.norm === a.norm : null;
  console.log(JSON.stringify({ at: now(), ...extra, existing: view(e), absent: view(a), bodyEqual }));
}

async function freshPair(ctx) {
  const e = await post(ctx, await prepare(ctx, ctx.existing));
  const a = await post(ctx, await prepare(ctx, ctx.absent));
  return [e, a];
}

async function main() {
  const [mode = 'once', arg1, arg2] = process.argv.slice(2);
  const ctx = context();
  console.error(`[reset-pair] realm=${ctx.realmName} 実在=${ctx.existing} 非実在=${ctx.absent}`
    + `（同じ ${Buffer.byteLength(ctx.existing)} バイト・realm 宣言に不在）edge=${ctx.base}`);
  if (mode === 'once') {
    const [e, a] = await freshPair(ctx);
    emit({ mode }, e, a);
  } else if (mode === 'window') {
    const size = Number(arg1 || 90);
    const interval = Number(arg2 || 1000);
    const pool = [];
    for (let i = 0; i < size; i += 1) {
      pool.push([await prepare(ctx, ctx.existing), await prepare(ctx, ctx.absent)]);
    }
    const broken = pool.filter(([pe, pa]) => pe.error || pa.error);
    if (broken.length > 0) {
      throw new Error(`先取りに失敗した対が ${broken.length} 件ある（例: ${broken[0][0].error || broken[0][1].error}）。`
        + ' 申請が開いている状態で先取りすること。');
    }
    console.error(`[reset-pair] READY ${now()} —— ${size} 対を先取りした。ここで障害を入れる。`);
    for (let i = 0; i < pool.length; i += 1) {
      const e = await post(ctx, pool[i][0]);
      const a = await post(ctx, pool[i][1]);
      emit({ mode, seq: i + 1 }, e, a);
      await sleep(interval);
    }
  } else if (mode === 'watch') {
    const deadline = Date.now() + Number(arg1 || 120) * 1000;
    const interval = Number(arg2 || 2000);
    for (let seq = 1; Date.now() < deadline; seq += 1) {
      const [e, a] = await freshPair(ctx);
      emit({ mode, seq }, e, a);
      await sleep(interval);
    }
  } else if (mode === 'timing') {
    const reps = Number(arg1 || 3);
    const per = Number(arg2 || 12);
    const repetitions = [];
    for (let rep = 1; rep <= reps; rep += 1) {
      const existing = [];
      const absent = [];
      for (let i = 1; i <= per; i += 1) {
        const [e, a] = await freshPair(ctx);
        emit({ mode, rep, i }, e, a);
        if (e.error || a.error) throw new Error(`反復 ${rep} の ${i} 標本目で申請を通せなかった`);
        existing.push(e.ms);
        absent.push(a.ms);
      }
      repetitions.push({ existing, absent });
    }
    // 参考表示。🔴 この判定式は置き換えが決まっている（§2.1 の注意）。判定の正本にしない。
    const current = m.evaluateTimingConsistency({ repetitions });
    console.error(`[reset-pair] 参考（現行の判定式）: ${current.verdict}`);
    for (const line of current.lines) console.error(line);
  } else {
    throw new Error(`未知のモード: ${mode}`);
  }
}

main().catch((e) => { console.error(`[reset-pair] ${e.message}`); process.exit(1); });
EOF
node --check "$PRD/reset-pair.js" && echo "構文 OK"
```

**`window` がなぜ要るか**: 申請が閉じると**ログイン画面から申請の導線が消える**ので、新しいフローでは閉じた後の応答（400）に届かない
（`no-reset-link` になる）。**閉じる前に取っておいたフォームへ、閉じた後に POST する**ことで、窓の終わり（最初の 400）と
閉じた状態の同値（400 / 400・本文一致）を直接観測する。先取りは**申請が開いている状態で**行う。

### 1.2 realm の状態を読む

§0.4 で定義した `realm_gate_state`（門の Pod から門自身の資格情報で GET するだけ）を使う。
**門を止めている間（§2.7）は使えない。** そのときは開閉を `reset-pair.js once` の結果（`no-reset-link` なら閉じている）で見る。

### 1.3 `login-lockout.js` —— §4 で使う

§4.2 に置く。

### 1.4 時刻の取り方

- 障害を入れる・戻すコマンドの**直前と直後**に `ts` を打ち、両方を記録する（`echo "fault-begin $(ts)" | tee -a "$PRD/timeline.txt"`）。
- 門の判断の時刻は `kubectl -n platform-infra logs deploy/reset-gate --timestamps` の行頭（UTC）で取る。
- 測定器の各行の `at` も UTC である。**3 つとも UTC で揃っている**ので、そのまま引き算してよい。

---

## 2. 状態ごとの手順

### 2.0 状態と期待値の一覧（導出。実測ではない）

門（申請を閉じる常駐の制御ループ）は**周期 10 秒**で近接 MTA へ本物の SMTP 取引（`EHLO` → `MAIL FROM` → `RCPT TO` → `RSET` → `QUIT`。
`DATA` は送らない）を打ち、**1 回の失敗で閉じ、連続 3 回の成功で開け直す**。閉じるときは `resetPasswordAllowed` と
`reset-gate.{state,reason,since}` の 4 キーだけを同じ PUT で書く。

| 状態 | 作り方（節） | 門が居るときの期待 | 門の閉鎖の理由（ログ） | 門が居ないときの期待（§2.7） | 窓 |
| --- | --- | --- | --- | --- | --- |
| **A** 正常・開 | 何もしない（§2.1） | 200 / 200・本文一致 | 閉じない | 同じ | — |
| **C1** 上流停止・relay 稼働 | 捕捉用 MTA を 0（§2.2） | **200 / 200・本文一致**。メールは `deferred` へ | **閉じない**（relay は投函を受け付ける） | 同じ | — |
| **C3** relay が投函を拒む | `postconf` で拒ませる（§2.3） | 窓の間は 500 / 200 → 閉鎖後 400 / 400 | `mail-from rejected with 452: …` または `rcpt-to rejected with 554: …` | 500 / 200 が続く | **W2** |
| **C2** relay 停止 | relay を 0（§2.4） | 窓の間は 500（即時）/ 200 → 閉鎖後 400 / 400 | 接続の失敗（例: `ECONNREFUSED: …`） | 500 / 200 が続く | **W1** |
| **C2'** relay への SYN が落ちる | NetworkPolicy の差し替え（§2.5） | 窓の間は **500（約 10 秒後）** / 200 → 閉鎖後 400 / 400 | `timeout after 10000ms` | 500（10 秒）/ 200 が続く | **W1'** |
| **D** 閉 | 上の C3 / C2 / C2' で門が閉じた後（§2.6） | **400 / 400・本文一致**・ログイン画面から導線が消える | — | — | — |

**窓の上限の見積もり**（門の構成 §0.3 から）: 障害の直後の周期で失敗を検知し PUT する —— W1 と W2 は**周期 10 秒 ＋ プローブ往復 ＋ PUT 往復**、
W1' はそれに**プローブのタイムアウト 10 秒**が乗る。**これは見積もりであり、実測で置き換える値である。**

### 2.0.1 各状態に共通する測り方（窓・`close` の証跡・再開）

§2.3〜§2.5 は同じ型で測る。**端末を 3 つ**使う。

**端末 1（門のログ）**: `kubectl -n platform-infra logs deploy/reset-gate --timestamps -f | tee "$PRD/<状態>-gate.log"`

**端末 2（対の申請）**:
```bash
node "$PRD/reset-pair.js" window 90 1000 | tee "$PRD/<状態>-window.jsonl"
```
`READY` が出るまで待つ（先取り）。`READY` の後の数行が `200 / 200` であることを見てから、端末 3 で障害を入れる。

**端末 3（障害と復元）**: 各節の「入れる」「戻す」を、`ts` で前後を挟んで打つ。

**窓の秒数** ＝ 端末 2 で**実在・非実在とも 400 になった最初の行の `at`** − **障害を入れたコマンドの直前の `ts`**。
あわせて次も記録する（§5 の表）。

- 障害から**最初の 500（実在側）**までと、**最後の 500** まで（窓の中で実際に何件の区別できる応答が返ったか）
- 門の `close:` 行の時刻 − 障害の時刻（**検知から PUT 完了まで**）
- 門の `close:` 行の時刻と、最初の 400 の行の時刻の差（PUT から認証基盤の応答が変わるまで）

**`close` の PUT が `manage-realm` で通ったことの証跡**（3 つとも取る）:

1. **門のログ**に `[reset-gate] close: resetPasswordAllowed=false 理由=probe failed: <理由>` がある。
   🔴 この行は **PUT が 2xx を返した後にしか出ない**（PUT が失敗すると `tick failed: PUT realm returned <status>` になり、`close:` は出ない）。
2. `realm_gate_state` が `resetPasswordAllowed=false` / `state="closed"` / `reason="probe failed: …"` / `since=<閉じた時刻>` を返す。
3. 管理コンソール（`master` の管理者）→ realm `platform` → **Events → Admin events** に、リソース種別 `REALM`・操作 `UPDATE`・
   認証の client が `reset-gate`（利用者は `service-account-reset-gate`）の行がある（realm は管理イベントの記録が有効である）。
   **閉じた時刻の行と開け直した時刻の行の 2 つ**を確認し、時刻を記録する。

**閉じた状態の機械の確認（任意・T-20）**: 閉じている間に次を 1 回ずつ打つ。

```bash
node scripts/check-password-reset-mail.js;           echo "exit=$?"   # 期待: exit=1、[T-20] 門（reset-gate）が申請を閉じている …
EXPECT_GATE_CLOSED=1 node scripts/check-password-reset-mail.js; echo "exit=$?"   # 期待: exit=0、OK: 申請が閉じており …
```

🔴 この検査器は稼働 realm を**認証基盤の Pod の中で管理 CLI を起動して**読む（既知の残債。認証基盤のメモリを圧迫し得る）。
**1 回ずつに留め、ループで回さない。** S5（認証基盤の再起動）に当たったら中止する。

**再開の観測**: 「戻す」を打った直後から、端末 2 を次へ切り替える。

```bash
node "$PRD/reset-pair.js" watch 180 2000 | tee "$PRD/<状態>-reopen.jsonl"
```

- 期待: しばらく `no-reset-link`（閉じている）が続き、門のログに `[reset-gate] reopen: resetPasswordAllowed=true 理由=relay reachable for 3 consecutive probes`
  が出た後、`200 / 200`・本文一致に戻る。
- 記録: **戻した時刻 → 近接 MTA が健全になった時刻 → `reopen:` の時刻 → 最初の 200 / 200 の時刻**。
  門は周期 10 秒で連続 3 回の成功を要るので、見積もりは「健全になってから 20〜30 秒 ＋ 往復」である。
- 🔴 開け直しの前に**失敗が 1 回でも挟まると連続回数は 0 に戻る**。揺れで開閉を繰り返したら（`close:` と `reopen:` が交互に出たら）、その回数と間隔を記録する。
- `realm_gate_state` で `resetPasswordAllowed=true` / `state="open"` を確かめて節を終える。

### 2.1 状態 A —— 陽性対照と、稼働クラスタでの所要時間

1. 検査器を稼働モードで 1 回走らせる（送出・本文・ステータスと本文の同値・所要時間を通しで見る）。

   ```bash
   node scripts/check-password-reset-mail.js 2>&1 | tee "$PRD/A-check.txt"; echo "exit=${PIPESTATUS[0]}"
   ```

   期待: `T-10: 実在=200 / 非実在=200`、T-16 / T-17 が通る（捕捉用 MTA にちょうど 1 通）。`T-25 所要時間（判定: …）` の行と、
   反復ごとの中央値・段の内訳の行が出る。
2. 所要時間の**生の標本**を取る（判定式に依らず後から判定できる形で残す）。

   ```bash
   node "$PRD/reset-pair.js" timing 3 12 | tee "$PRD/A-timing.jsonl"
   ```

   反復 3（1 回目は暖機）× 片側 12 標本。実在側の 1 標本はメール 1 通であり、捕捉用 MTA に 36 通届く。
3. 対を 1 回だけ打ち、ステータスと本文の一致を見る: `node "$PRD/reset-pair.js" once | tee "$PRD/A-once.jsonl"`（期待 `200 / 200`・`bodyEqual: true`）。

🔴 **所要時間の判定について（#1526 との依存）**

- #1526 は**着地済み**である（`33a21412`）。検査器の所要時間の札が `T-25` になり、時計が単調時計の整数 ns になり、出力に段の内訳が出る。
  **これらは #1526 以降のチェックアウトでしか出ない**（§前提の版の確認はこのためである）。
- ただし #1526 の後、**計画が所要時間の判定式そのものを順位和検定（両側・有意水準 1%。片側 12・反復 3）へ改めると裁定した**（2026-09-26）。
  理由は、整数 ns の時計と現行の 2 段の判定式の組では、**系統差が無くても実行の約 6 割が「不合格」になる**ことが合成試験で示されたためである。
  **この判定式の変更は本リポジトリにまだ実装されていない**（時計だけが先に入っている）。
- したがって **§2.1 の手順 1 の `T-25` の判定（合格／不合格）は、本書の記録の正本にしない。** 正本は手順 2 の**生の標本**である。
  判定式が実装されたら、その実装で `A-timing.jsonl` を判定し直す。反復ごとの中央値と比は参考として記録してよい。
- **床を外して比べない**（本番での退路としても、比較のためにも、本書では `RESET_FLOOR=0` を使わない）。

### 2.2 状態 C1 —— 上流停止（relay は稼働）

**入れる**:
```bash
echo "C1-fault-begin $(ts)" | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra scale deploy/mailpit --replicas=0
echo "C1-fault-end $(ts)"   | tee -a "$PRD/timeline.txt"
```

**測る**:
1. 対を打つ: `for i in 1 2 3; do node "$PRD/reset-pair.js" once; sleep 5; done | tee "$PRD/C1-pairs.jsonl"`
   —— 期待: **3 回とも `200 / 200`・`bodyEqual: true`**（近接 MTA が受け付けて `deferred` へ入れる）。**これが近接 MTA を挟んだ目的そのものの確認である。**
2. 門が**閉じない**こと: 門のログに `close:` が出ず、`realm_gate_state` が `resetPasswordAllowed=true` のまま。
3. 🔴 ここで検査器（`check-password-reset-mail.js`）を走らせない —— 捕捉用 MTA が居ないので前提で止まるだけである。
4. キューの指標とアラートを §3 の表に沿って **35 分**観測する（キュー寿命 30 分と、最長 300 秒の再送間隔を覆う）。
5. 認証基盤の監査ログに投函失敗が**出ない**こと（出るのは近接 MTA への投函失敗だけ。上流の停止は現れない）:
   `kubectl -n platform-infra logs deploy/keycloak --since=40m | grep -c SEND_RESET_PASSWORD_ERROR`（期待 0。行そのものは利用者名と IP を含むので貼らない）。

**戻す**（排出も確かめる）:
```bash
echo "C1-restore-begin $(ts)" | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra scale deploy/mailpit --replicas=<スナップショットの値>
kubectl -n platform-infra rollout status deploy/mailpit --timeout=120s
echo "C1-restore-end $(ts)"   | tee -a "$PRD/timeline.txt"
```

寿命 30 分を過ぎたメールは既に破棄されているので、排出を見るには**短い 2 回目**を行う:
上流を再び 0 にして `once` を 1 回打ち、`deferred` が 1 以上になったのを exporter で確かめてから上流を戻し、
**`deferred` が 0 に戻るまでの秒数**と、捕捉用 MTA にそのメールが届いたことを記録する（再送間隔は 60〜300 秒の設定である）。

### 2.3 状態 C3 —— relay は生きていて投函を拒む（窓 W2）

**Pod を作り直さずに**、稼働コンテナの設定 1 項目で拒ませる（作り直すと C2 の窓が混ざる）。2 通りある。**どちらか 1 つでよい。**

| 型 | 変える項目 | 期待する拒否（導出・未実測） |
| --- | --- | --- |
| C3a（容量不足の 452） | `queue_minfree` を空き容量より大きな値にする | `MAIL FROM` に `452 4.3.1 Insufficient system storage` |
| C3b（接続元の拒否 554） | `smtpd_client_restrictions` を `reject` にする | `RCPT TO` に `554 5.7.1 …`（拒否は RCPT まで遅らせる既定のため） |

§2.0.1 の型で、端末 2 の `READY` を見てから端末 3 で**入れる**（C3a の例）:

```bash
echo "C3-fault-begin $(ts)" | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra exec deploy/mail-relay -c postfix -- postconf -e 'queue_minfree=999999999999999'
kubectl -n platform-infra exec deploy/mail-relay -c postfix -- postfix reload
echo "C3-fault-end $(ts)"   | tee -a "$PRD/timeline.txt"
```

（C3b は 1 行目の代わりに `postconf -e 'smtpd_client_restrictions=reject'`。）

**確かめる**: 門のログの `close:` の理由が期待の拒否コードを含むこと。**違うコード・違う段で拒まれたら、そのまま記録する**（未決事項 3）。
門が閉じた後、§2.6（状態 D）をここで測る。

**戻す**（§0.5 の `postconf` の行）:
```bash
echo "C3-restore-begin $(ts)" | tee -a "$PRD/timeline.txt"
# C3a（queue_minfree）—— スナップショットの explicit=[] が空だった場合:
kubectl -n platform-infra exec deploy/mail-relay -c postfix -- postconf -X queue_minfree
# explicit=[queue_minfree = <値>] だった場合は代わりに: postconf -e 'queue_minfree=<値>'
# C3b（smtpd_client_restrictions）—— 上の 2 行の代わりに、スナップショットの
# explicit=[smtpd_client_restrictions = <値>] の <値> をそのまま戻す:
#   kubectl -n platform-infra exec deploy/mail-relay -c postfix -- postconf -e 'smtpd_client_restrictions=<値>'
kubectl -n platform-infra exec deploy/mail-relay -c postfix -- postfix reload
echo "C3-restore-end $(ts)"   | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra exec deploy/mail-relay -c postfix -- postconf -h queue_minfree   # C3b では smtpd_client_restrictions。スナップショットの effective と同じか
```

🔴 **C3b に `postconf -X` を使わない。** このイメージは起動スクリプトが `smtpd_client_restrictions` を明示に書く
（イメージのスクリプトからの導出では `permit_mynetworks,permit_sasl_authenticated,reject`。正はスナップショット）ため、
スナップショットの `explicit=[...]` は空にならない。`-X` は Postfix の既定値へ戻すだけで、控えた値には戻らない。

続けて §2.0.1 の「再開の観測」。

### 2.4 状態 C2 —— relay 停止（窓 W1）＋ 観測点の消失 ＋ 空の spool での起動

§2.0.1 の型で、`READY` を見てから**入れる**:
```bash
echo "C2-fault-begin $(ts)" | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra scale deploy/mail-relay --replicas=0
echo "C2-fault-end $(ts)"   | tee -a "$PRD/timeline.txt"
```

**確かめる**:
1. 窓（§2.0.1）。実在側の 500 が**即時**（数十〜数百 ms。床 150 ms を下回らない）で返ることを `ms` で見る。
2. 門が閉じた後、§2.6（状態 D）を測る。
3. 🔴 **この状態を 15 分保つ**（門が閉じているので存在秘匿は保たれている）。exporter も同じ Pod なので系列が消える ——
   **`MailRelayQueueSeriesAbsent` が発火するまでの時間**を §3 の表に記録する。
4. 認証基盤の監査ログに投函失敗が出ること（窓の間の実在側の申請の数だけ）: `kubectl -n platform-infra logs deploy/keycloak --since=20m | grep -c SEND_RESET_PASSWORD_ERROR`。
   **件数だけ**を記録する（テスト仕様書の T-13「近接 MTA への投函失敗は監査ログへ残る」）。

**戻す**:
```bash
echo "C2-restore-begin $(ts)" | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra scale deploy/mail-relay --replicas=<スナップショットの値>
kubectl -n platform-infra rollout status deploy/mail-relay --timeout=120s
echo "C2-restore-end $(ts)"   | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra get pods -l app=mail-relay        # 2/2 Running・RESTARTS 0
kubectl -n platform-infra exec deploy/mail-relay -c queue-exporter -- wget -qO- http://127.0.0.1:9154/metrics | grep -E '^postfix_(up|queue_size\{queue="deferred"\})'
```

**空の spool で起動すること**（観測の仕様書が稼働で確かめていないと書いている項目）: 新しい Pod は空の `emptyDir` で起動する。
**2/2 Running・再起動 0・`postfix_up 1`** になれば確認できたとする。ならなければ S6。続けて §2.0.1 の「再開の観測」と、
`MailRelayQueueSeriesAbsent` が解消するまでの時間を記録する。

### 2.5 状態 C2' —— relay への SYN が落ちる（窓 W1'）

近接 MTA への投函を許しているのは NetworkPolicy 2 本（認証基盤から :587、門から :587）である。**両方の `from` を、どの Pod にも一致しないラベルへ差し替える。**
NetworkPolicy は加算的なので、残る `mail-relay-ingress-otel-collector`（:9154 だけを許す）により Pod は「隔離」され、:587 への接続は**強制するクラスタでは**落とされる。
**削除ではなく差し替え**にするのは、戻すときに同じ値へ確実に戻せるからである。

**まず、このクラスタで C2' が作れるかを確かめる**（未決事項 1）:

- リポジトリは「dev の k3d の既定は NetworkPolicy を強制しない」と書いている。強制しないクラスタでは差し替えても何も起きない（門は閉じず、申請は 200 / 200 のまま）。
- 強制していても、**捨てる（DROP）か拒む（REJECT）か**は実装による。拒むなら接続は即座に失敗し、それは C2 と同じ形である。
- **判定は結果で行う**: 門の `close:` の理由が `timeout after 10000ms` なら C2'（SYN が落ちている）、`ECONNREFUSED` などの即時の失敗なら「C2 相当」、
  60 秒たっても閉じず申請が 200 / 200 なら「強制しない」。**後の 2 つのときは C2' は作れなかったと記録する**（別の作り方は未決事項 1）。

§2.0.1 の型で、`READY` を見てから**入れる**:
```bash
echo "C2p-fault-begin $(ts)" | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra patch networkpolicy mail-relay-ingress --type=json \
  -p '[{"op":"replace","path":"/spec/ingress/0/from/0/podSelector/matchLabels/app","value":"pr-d-no-such-pod"}]'
kubectl -n platform-infra patch networkpolicy mail-relay-ingress-reset-gate --type=json \
  -p '[{"op":"replace","path":"/spec/ingress/0/from/0/podSelector/matchLabels/app","value":"pr-d-no-such-pod"}]'
echo "C2p-fault-end $(ts)"   | tee -a "$PRD/timeline.txt"
```

**確かめる**: 実在側の 500 が**約 10 秒後**に返ること（`ms` が 10 000 前後。これが「ステータスを見なくても所要時間で判別できる」W1' の形である）。
**10 秒より短い時間で 5xx になったら**、経路上のどこかが先に打ち切っている（未決事項 10）—— ステータスと `ms` をそのまま記録する。
門が閉じた後、§2.6（状態 D）を測る。

**戻す**:
```bash
echo "C2p-restore-begin $(ts)" | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra patch networkpolicy mail-relay-ingress --type=json \
  -p '[{"op":"replace","path":"/spec/ingress/0/from/0/podSelector/matchLabels/app","value":"keycloak"}]'
kubectl -n platform-infra patch networkpolicy mail-relay-ingress-reset-gate --type=json \
  -p '[{"op":"replace","path":"/spec/ingress/0/from/0/podSelector/matchLabels/app","value":"reset-gate"}]'
echo "C2p-restore-end $(ts)"   | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra get networkpolicy mail-relay-ingress mail-relay-ingress-reset-gate mail-relay-ingress-otel-collector \
  -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.spec.ingress}{"\n"}{end}' | diff - "$PRD/01-netpol.txt" && echo "NetworkPolicy: スナップショットと一致"
```

戻す値（`keycloak` / `reset-gate`）は `deploy/mail-relay/mail-relay.yaml` と `deploy/mail-relay/reset-gate.yaml` の宣言である。**スナップショット（`01-netpol.txt`）と違っていたら、スナップショットの値へ戻す。**
続けて §2.0.1 の「再開の観測」。

### 2.6 状態 D —— 閉じた状態の同値（テスト仕様書の T-21）

C3 / C2 / C2' のいずれかで門が閉じている間に測る（門が閉じた状態そのものが D である）。

1. `window` の出力で、門が閉じた後の行が**実在・非実在とも 400**・`bodyEqual: true`・`bodyBytes` が同じであること。**連続 5 行以上**を確かめる。
2. ログイン画面から導線が消えていること: `node "$PRD/reset-pair.js" once` が両側とも `error: "no-reset-link"` を返す。
3. 閉じている間に新しい申請を**先取りできない**ので、`window` の先取りは必ず開いている間（`READY` の前）に済ませる。

期待と違ったら（400 以外・本文不一致）**そのまま記録する**。過去の実測（400 / 400・本文バイト一致）がどの経路で閉じた状態の申請端点に届いたかは記録に残っていない（未決事項 2）。

### 2.7 任意: 門を止めた陰性対照（生の 500 / 200）

**門を止めている間は、利用者名を 1 件ずつ列挙できる窓が開きっぱなしになる。** 陰性対照は既に #1169 の実測にあるので、**次のすべてを満たすときだけ**行う。

- エッジ（認証基盤の公開 URL）に、利用者以外が到達できないこと
- **1 状態あたり 3 分以内**（S8）。対を 3 回打ったら直ちに戻す

手順（C2 の例）:
```bash
kubectl -n platform-infra scale deploy/reset-gate --replicas=0 && kubectl -n platform-infra rollout status deploy/reset-gate --timeout=60s
echo "C2raw-fault-begin $(ts)" | tee -a "$PRD/timeline.txt"
kubectl -n platform-infra scale deploy/mail-relay --replicas=0
for i in 1 2 3; do node "$PRD/reset-pair.js" once; sleep 3; done | tee "$PRD/C2raw-pairs.jsonl"   # 期待: 500 / 200
# 戻す: 先に門を戻す（門は起動直後の周期で閉じる）→ その後に近接 MTA を戻す（門が開け直す）
kubectl -n platform-infra scale deploy/reset-gate --replicas=<値> && kubectl -n platform-infra rollout status deploy/reset-gate --timeout=120s
kubectl -n platform-infra scale deploy/mail-relay --replicas=<値> && kubectl -n platform-infra rollout status deploy/mail-relay --timeout=120s
echo "C2raw-restore-end $(ts)" | tee -a "$PRD/timeline.txt"
```

門が 0 の間は `realm_gate_state` を使えない（§1.2）。統合スタックの検査（`check-stack-ready.js`）は門の不在で赤になる —— 期待どおりの挙動であり、戻せば消える。

---

## 3. キューのアラートのしきい値を確定するための記録

3 件のアラートのしきい値と `for` は、**実測が 1 つも無い暫定値**である（キュー寿命 30 分・再送の最小間隔 60 秒から導いた初期値）。
ここで記録するのは「どの時点で何が見えたか」であり、**しきい値そのものは本書では決めない**（記録を見て、別の変更で確定する）。

| アラート | 現在の式・`for`・深刻度 | 観測する状態 |
| --- | --- | --- |
| `MailRelayDeferredBacklog` | `postfix_queue_size{queue="deferred"} > 0`・5 分・warning | §2.2（C1） |
| `MailRelayDeferredMessageNearExpiry` | `postfix_queue_oldest_message_age_seconds{queue="deferred"} > 1200`・5 分・critical | §2.2（C1。35 分保つ） |
| `MailRelayQueueSeriesAbsent` | `absent(postfix_queue_size{queue="deferred"})`・5 分・warning | §2.4（C2。15 分保つ） |

**観測のコマンド**（1 分ごとに打ち、出力に `ts` を付けて残す）:

```bash
mailrelay_alerts() {   # 発火中・保留中の MailRelay* だけを「名前 状態 activeAt」で出す
  curl -s 'http://localhost:9090/api/v1/alerts' | node -e '
let s = ""; process.stdin.on("data", (d) => { s += d; }).on("end", () => {
  const alerts = JSON.parse(s).data.alerts.filter((a) => /^MailRelay/.test(a.labels.alertname));
  if (alerts.length === 0) console.log("MailRelay のアラートなし");
  for (const a of alerts) console.log(a.labels.alertname, a.state, a.activeAt);
});'
}
while true; do
  echo "== $(ts)"
  kubectl -n platform-infra exec deploy/mail-relay -c queue-exporter -- wget -qO- http://127.0.0.1:9154/metrics 2>/dev/null \
    | grep -E '^postfix_(up|queue_size|queue_oldest_message_age_seconds)' || echo "exporter に届かない"
  curl -sG 'http://localhost:9090/api/v1/query' --data-urlencode 'query=postfix_queue_size{queue="deferred"}' | head -c 400; echo
  mailrelay_alerts
  sleep 60
done | tee "$PRD/queue-watch.txt"
```

（Prometheus は `kubectl -n platform-infra port-forward svc/prometheus 9090:9090` を開いておく。観測スタックが無いなら exporter の行だけを残す。）

**記録する項目**:

| # | 項目 | 何を決めるための値か |
| --- | --- | --- |
| Q1 | 上流を止めてから、exporter の `deferred` が 1 以上になるまでの秒数 | 系列の立ち上がり（exporter は scrape のたびに spool を読む） |
| Q2 | 同じく、Prometheus の `postfix_queue_size{queue="deferred"}` が 1 以上になるまでの秒数 | 収集経路（collector の scrape 30 秒 → remote write）の遅れ。`for` の起点 |
| Q3 | `MailRelayDeferredBacklog` が pending になった時刻・firing になった時刻 | `for: 5m` の実効の長さ |
| Q4 | `deferred` の最大値と、申請した通数 | キュー長のしきい値（`> 0`）の妥当性 |
| Q5 | 最古の齢が 1200 秒を超えた時刻と、`MailRelayDeferredMessageNearExpiry` が firing になった時刻 | **破棄の何分前に鳴ったか**（寿命 1800 秒に対する余裕） |
| Q6 | `deferred` が 0 に戻った時刻（破棄）と、Postfix のログの期限切れの件数（`kubectl -n platform-infra logs deploy/mail-relay -c postfix --since=40m \| grep -c 'status=expired'`。行は宛先を含むので件数だけ） | 寿命 30 分の実効（再送間隔の最大 300 秒ぶん遅れ得る） |
| Q7 | 2 回目の短い C1 で、上流を戻してから `deferred` が 0 に戻るまでの秒数 | 排出の速さ（再送間隔 60〜300 秒） |
| Q8 | relay を 0 にしてから Prometheus で系列が消えるまでの時間と、`MailRelayQueueSeriesAbsent` の pending / firing の時刻 | 系列の不在の検知の遅れ（取り込みの鮮度の扱い） |
| Q9 | relay を戻してから系列が戻り、`MailRelayQueueSeriesAbsent` が解消するまでの時間 | 復旧側 |
| Q10 | §2.1・§2.3〜§2.5 の間に、C1 以外で `deferred` が 1 以上になった瞬間があったか | 平常時の誤発火の有無 |
| Q11 | `postfix_up` が 0 になった時刻があったか（読めなかったキューがあった） | 計器の健全性 |

🔴 **Grafana 側の同内容のアラートも同じ時刻に発火したか**を、Grafana の Alerting 画面で目視し、差があれば記録する（3 系統に同じ内容で置いてある）。

---

## 4. ログインのロックアウトを意図的に発火させる

realm は「**5 回連続で失敗すると 15 分の一時ロック**（永久ロックではない）」を宣言している。**ロックは実在する利用者にしか起きない。**
ロック中の応答（ステータス・本文・リダイレクト先・所要時間）が実在しない利用者名の応答と区別できるなら、それは**もう 1 つの存在の判定器**である。
統合スタックの検査器は、後段を壊さないために**意図的にロックの手前で止まる**ので、ここだけが未実測で残っている。

🔴 **`admin` をはじめ、realm 宣言に在る利用者では決して行わない。** 他の自動化（性能試験・検索評価など）が使っている。
**専用の試験利用者を作り、測り、削除する。** 下の測定器は宣言済みの利用者名を渡されると拒む。
🔴 **統合スタック用のログインの検査器（`check-login-existence-disclosure.js`）を利用者のクラスタで稼働モードにしない。** 対象が `admin` に固定で、失敗を 4 回積む（上書きの手段が無い。未決事項 6）。

### 4.1 試験利用者を作る

1. 管理コンソール（`master` の管理者）→ realm `platform` → **Users → Add user**。
2. 利用者名は **realm 宣言に無い名前**（例: `prd-lockout1`。**ASCII だけ**にする —— 非実在側を同じバイト長で作るため）。メールは空でよい。ロールは付けない。
3. **Credentials → Set password** で、パスワードポリシーを満たす任意の値を設定する（一時パスワードはオフ）。**値は記録しない**（使わない）。
4. 作った時刻を `timeline.txt` に書く。

### 4.2 `login-lockout.js` を置く

```bash
cat > "$PRD/login-lockout.js" <<'EOF'
'use strict';
// PR-D 用の使い捨て測定器（リポジトリへは入れない）。リポジトリのルートで実行する。
//   node "$PRD/login-lockout.js" <試験利用者名>
// ログインを実在（試験利用者）／非実在（同じバイト長）の対で failureFactor + 2 回失敗させ、各回の応答を並べる。
const path = require('path');
const m = require(path.resolve('scripts', 'check-password-reset-mail.js'));
const l = require(path.resolve('scripts', 'check-login-existence-disclosure.js'));

// 両側で同じ誤った資格情報（秘密ではない。どの利用者にも設定されていない固定文字列）。
const WRONG_CREDENTIAL = 'pr-d-lockout-wrong-credential';
const sleep = (ms) => new Promise((r) => { setTimeout(r, ms); });

function view(r) {
  if (r.error) return { error: r.error };
  return { status: r.status, location: r.location, bodyBytes: Buffer.byteLength(r.norm), ms: Number(r.ms.toFixed(3)) };
}

async function main() {
  const testUser = String(process.argv[2] || '');
  const realmRes = m.loadRealm();
  if (!realmRes.ok) throw new Error(realmRes.error);
  const realm = realmRes.value;
  const declared = (realm.users || []).map((u) => String((u && u.username) || ''));
  if (testUser === '') throw new Error('試験利用者名を引数で渡す');
  if (declared.includes(testUser)) {
    throw new Error(`${testUser} は realm 宣言に在る共有の利用者である。専用の試験利用者を作って渡す`);
  }
  if (!(realm.bruteForceProtected === true && Number.isInteger(realm.failureFactor) && realm.failureFactor >= 1)) {
    throw new Error('realm 宣言が一時ロックを持たない（bruteForceProtected / failureFactor）');
  }
  const absent = l.makeAbsentUsernameOfLength(realm, Buffer.byteLength(testUser));
  const pairing = l.evaluateProbePairing({ existingUsername: testUser, absentUsername: absent || '', realmUsernames: declared });
  if (pairing.length > 0) throw new Error(pairing.join(' / '));
  const client = m.pickBrowserFlowClient(realm);
  const base = m.keycloakBaseUrl();
  const ca = m.edgeCa();
  if (!client || !base.ok || !ca.ok) throw new Error('前提を満たさない（クライアント・エッジ URL・ローカル CA）');

  const rounds = realm.failureFactor + 2;
  const spacing = l.probeSpacingMs(realm);
  console.error(`[login-lockout] 実在（試験利用者）=${testUser} 非実在=${absent} 回数=${rounds}`
    + `（failureFactor=${realm.failureFactor} ＋ 2）間隔=${spacing}ms`);
  for (let round = 1; round <= rounds; round += 1) {
    const out = {};
    for (const [side, username] of [['existing', testUser], ['absent', absent]]) {
      const r = await l.attemptLogin({
        base: base.value, realmName: realm.realm, client, ca: ca.value, username, credential: WRONG_CREDENTIAL,
      });
      out[side] = r.error ? { error: r.error } : {
        status: r.status,
        location: l.normalizeLoginLocation(r.location, username),
        norm: m.normalizeConcealmentBody(r.body, username),
        ms: r.elapsedMs,
      };
    }
    const e = out.existing;
    const a = out.absent;
    const both = !e.error && !a.error;
    console.log(JSON.stringify({
      at: new Date().toISOString(),
      round,
      afterLockThreshold: round > realm.failureFactor,
      existing: view(e),
      absent: view(a),
      statusEqual: both ? e.status === a.status : null,
      locationEqual: both ? e.location === a.location : null,
      bodyEqual: both ? e.norm === a.norm : null,
    }));
    if (round < rounds) await sleep(spacing);
  }
}

main().catch((e) => { console.error(`[login-lockout] ${e.message}`); process.exit(1); });
EOF
node --check "$PRD/login-lockout.js" && echo "構文 OK"
```

### 4.3 測る

```bash
echo "lockout-begin $(ts)" | tee -a "$PRD/timeline.txt"
node "$PRD/login-lockout.js" prd-lockout1 | tee "$PRD/lockout.jsonl"
echo "lockout-end $(ts)"   | tee -a "$PRD/timeline.txt"
```

| 見るもの | どこで | 期待（導出）・記録 |
| --- | --- | --- |
| ロックの手前（`afterLockThreshold: false` の行） | `lockout.jsonl` | 各回 `statusEqual` / `locationEqual` / `bodyEqual` がすべて `true`（統合スタックの実測と同じ形） |
| **ロックの後**（`afterLockThreshold: true` の行） | 同上 | 🎯 **これが本節の測定対象である。** ステータス・リダイレクト先・本文が非実在側と一致するか。**期待値は置かない**（認証基盤がロック中の利用者に何を返すかは実測で決める。未決事項 8） |
| 所要時間 | 同上の `ms` | ロックの前後で実在側の `ms` が変わるか（例: ロック中は資格情報の照合を省いて速くなる／遅くなる）。**判定しない。数字だけ記録する** |
| ロックが本当に掛かったこと | 管理コンソール → Users → 試験利用者（一時ロックの表示）／ **Events → User events** で試験利用者の `LOGIN_ERROR` の error 欄 | ロックを示すエラー種別の行があること。時刻を記録する。**無ければロックは起きておらず、ロック後の行の比較は無効**である |
| 非実在側がロックされないこと | User events に非実在名の行があっても、利用者が存在しないので一時ロックの対象にならない | 記録のみ |

### 4.4 解除と後片付け

1. **解除**: 管理コンソール → Users → 試験利用者 → 一時ロックの表示を解除する（または 15 分待つ）。**realm 全体のロックの一括解除は使わない**（他の利用者のロックまで消える）。
2. **削除**: 同じ画面から試験利用者を削除する。削除の時刻を `timeline.txt` に書く。
3. 確かめる: 利用者一覧に試験利用者が居ないこと。

---

## 5. 記録

### 5.1 記録の雛形

**時刻はすべて UTC。** 各行の根拠のファイル（`$PRD/…`）を併記する。**本文そのもの・メールアドレス・秘匿値は貼らない。**

**表 1: 状態ごとの応答と窓**

| 状態 | 障害の時刻 | 実在（ステータス・`ms`） | 非実在（ステータス・`ms`） | 本文一致 | 門の `close:`（時刻・理由） | 最初の 400 / 400 | **窓の秒数** | 窓の中の 500 の件数 | 戻した時刻 | `reopen:` の時刻 | 最初の 200 / 200 | 根拠 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | — |  |  |  | —（閉じない） | — | — | — | — | — | — | `A-once.jsonl` |
| C1 |  |  |  |  | —（閉じないこと） | — | — | — |  | — | — | `C1-pairs.jsonl` |
| C3（a / b） |  |  |  |  |  |  |  |  |  |  |  | `C3-*.jsonl` / `C3-gate.log` |
| C2 |  |  |  |  |  |  |  |  |  |  |  | `C2-*` |
| C2'（作れたか: 作れた／C2 相当／強制しない） |  |  |  |  |  |  |  |  |  |  |  | `C2p-*` |
| D（どの状態の閉鎖中か） | — | 400 ・ | 400 ・ |  | — | — | — | — | — | — | — | `*-window.jsonl` の閉鎖後の行 |
| 任意: C2raw ほか |  |  |  |  | —（門を止めた） | — | — |  |  | — | — | `C2raw-pairs.jsonl` |

**表 2: 門の `close` PUT の証跡**

| 状態 | 門のログの `close:` 行（時刻） | `realm_gate_state`（state / reason / since） | 管理イベント（時刻・client） | `tick failed:` の有無 |
| --- | --- | --- | --- | --- |
|  |  |  |  |  |

**表 3: 所要時間（状態 A）**

| 項目 | 値 |
| --- | --- |
| 床の経路の有無（§0.3） |  |
| 生の標本 | `A-timing.jsonl`（反復 3 × 片側 12） |
| 反復 2・3 の実在／非実在の中央値（ms） |  |
| 検査器の `T-25` の出力（参考。判定の正本にしない） |  |

**表 4: キュー（§3 の Q1〜Q11）** —— 項目ごとに値と根拠の行（`queue-watch.txt` の時刻）。

**表 5: ロックアウト（§4）** —— 回ごとに実在／非実在のステータス・`location` の一致・本文の一致・`ms`、ロックの確認（User events の時刻）。

**表 6: 復元の照合** —— §0.5 の最後の照合の `diff` の結果と、「戻らないもの」の実際の値。

### 5.2 どこへ置くか

1. **#1245 へのコメント**（本書の §5.1 の表をそのまま）: `gh issue comment 1245 --body-file <記録の Markdown>`。
   生のファイル（`$PRD`）は添付しない —— 表の値と、必要ならファイルの該当行の抜粋だけにする。
2. **計画リポジトリへの環流**（`/plan-feedback`、feedback テンプレート。**起票の前に同件の既存 issue を検索する**）:
   - 存在秘匿の計画 ADR が求めた「近接 MTA 構成での応答の区別不能性」と「送出不能時に機械で閉じる門」の実測 —— 表 1・表 2（窓の秒数を含む）
   - 所要時間の床を既定にした計画 ADR のフォローアップ（クラスタ再構築後の稼働クラスタでの再測定）—— 表 3
   - ログインのロックアウトの実測（ログイン経路の所要時間の測り方を保留している計画の論点）—— 表 5
3. **キューのしきい値（表 4）は本リポジトリ側で確定する**（実装が暫定値を置いた項目）。記録を受けて、アラートの 3 系統・観測の仕様書・実装の記録を同じ変更で改める（AI が行ってよい作業である）。
4. 実測を受けた後続の変更（AI が行う）: 画面仕様書・テスト仕様書（T-13 / T-21 / T-24 の「手動」を実測済みへ）・実装の記録への日付つき追記。

### 5.3 各行が満たす #1245 の受け入れ基準

| #1245 の受け入れ基準・残作業 | 満たす記録 |
| --- | --- |
| 基準 1: 計画の裁定（近接 MTA ＋ 門）に基づく実装で、**状態 B / C の応答を同値にする** | 表 1 の C1（そもそも 200 / 200）と C3 / C2 / C2'（窓の後は 400 / 400）、表 1 の D、表 2（門が本当に閉じたこと）。**窓の秒数が「残る窓」の大きさ**として併記されること |
| 基準 2: **状態 C を意図的に作った実測**（送出先を到達不能にして実在／非実在を対で申請）を PR に貼る。#1169 の B / C が陰性対照、A / D が陽性対照 | 表 1 の C1 / C3 / C2 / C2' の各行（対の申請の生の行が根拠）。陽性対照は表 1 の A / D。陰性対照は #1169 の実測（§2.7 を行ったなら表 1 の任意の行も） |
| 基準 3: #1143 の受け入れ基準 1・2 を本 issue のクローズで初めて「合」とする | 基準 1・2 の記録がそろったこと（計画は「近接 MTA と門の両方が配備された時点で満たされる」としている） |
| 残作業: 近接 MTA 構成での実測（ステータス・本文・所要時間）を環流する | 表 1 の A / C1、表 3 |
| 残作業: 門の閉じる側（`close` の PUT が `manage-realm` で通ること）は一度も踏まれていない | 表 2 |
| 残作業: キューの暫定しきい値の確定・空の spool での起動 | 表 4、§2.4 の起動の確認 |
| 残作業: ログインのロックアウト（もう 1 つの存在判定器） | 表 5 |

## 確認（この手順が成功したと言える条件）

- 表 1 の C1 / C3 / C2 の行が埋まっている（C2' は「作れた／作れなかった」のどちらかが書かれている）
- C3 / C2（/ C2'）のそれぞれで、表 2 の 3 つの証跡がそろっている
- 表 1 の D の行が `400 / 400`・本文一致である。**そうでなければ成功ではなく、それ自体が報告すべき結果である**
- §0.5 の最後の照合で、「戻らないもの」以外の差が無い
- 記録が #1245 にコメントされている

**「窓の秒数が 0 だった」は成功条件ではない。** 窓は残るものとして設計されている —— 測れていればよい。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| `reset-pair.js` が `前提を満たさない` で止まる | context が違う・エッジの CA を読めない・Keycloak の Deployment が見つからない | `kubectl config current-context` と §0.3 を見直す。検査器の稼働モードが同じ前提で動くかを 1 回試す |
| `window` の先取りで `no-reset-link` | 既に申請が閉じている | `realm_gate_state` を見る。門が閉じているなら原因（近接 MTA）を先に直す |
| 障害を入れても門が閉じない（S3） | NetworkPolicy を強制しない（C2'）／拒否が効いていない（C3。`postfix reload` が効いていない）／門のプローブが別の経路を見ている | 直ちに戻す。C3 なら `postconf -h` で値が入ったか、Pod を作り直さずに `postfix reload` をもう 1 度。記録して未決事項へ |
| `tick failed: PUT realm returned 403`（S4） | 門のサービスアカウントに `manage-realm` が無い | 直ちに戻す。宣言の検査（`node scripts/check-realm-constraints.js`）が通るか、稼働 realm の service account のロールを管理コンソールで確認し、#1245 へ報告 |
| 戻しても門が開け直さない（S6） | 門の連続成功が揃わない（揺れ）／門の標識が `closed` でない（人が手で閉じた扱い）／宣言が閉じている | `realm_gate_state` の `state` を見る。`closed` 以外なら門は開けない設計である。**手で開けない** —— 開けると検査器の T-20 が「門の標識が closed のまま開いている」で赤になる。状態を記録して報告する |
| `mail-relay` が 2/2 にならない | 起動時の初期化スクリプトの fail-closed（上流の値が空・STARTTLS の値が不正）／exporter のスクリプトの ConfigMap が無い | `kubectl -n platform-infra logs deploy/mail-relay -c postfix` の `mail-relay:` で始まる行を見る。直らなければ、稼働中の構成を入れたときと同じチェックアウトから `kubectl apply -f deploy/mail-relay/mail-relay.yaml` |
| ロックアウトで User events にロックの行が無い | 試験利用者が無効になっている・稼働 realm の一時ロックの設定が宣言と違う | 利用者が有効か、管理コンソールの realm 設定（Security defenses → Brute force detection）が宣言どおりかを確かめ、もう 1 度だけ行う。**2 回目も無ければ行わない**（記録する） |

## 限界（この手順で担保できないこと）

- **本書の期待値はすべて導出である。** 本書が正しく状態を作れることも、稼働クラスタでは 1 度も確かめていない。
- **窓は閉じない。** 本書は窓の大きさを測るだけである。窓の間に返った 500 / 200 の件数が、そのまま「その間に列挙できた件数の上限」になる。
- **go-live の構成（上流が実テナント）では本書をそのまま使えない**（S1）。上流停止の作り方が別に要る。
- **門の不在を知らせる計器は無い**（観測の仕様書の未決事項）。本書の S2 は人の目で見ている。
- **所要時間の判定式は本リポジトリで未実装の改定を待っている**（§2.1）。生の標本は残るが、判定の結論はまだ出せない。
- 認証基盤の Pod の中で管理 CLI を起動する既存の検査器（§2.0.1 の T-20 の確認）は、認証基盤のメモリを圧迫し得る既知の残債を抱えている。

## 未決事項（リポジトリから導けなかったこと）

1. **利用者のクラスタが NetworkPolicy を強制するか、強制するなら捨てるか拒むか。** C2' を作れるかはこれで決まる。作れなかった場合の別の作り方（接続は成立するがバナーを返さない状態など）はリポジトリに無く、本書は手順にしていない。
2. **閉じた状態の 400 / 400 を過去の実測がどう観測したか。** 本書は「閉じる前に先取りしたフォームへ閉じた後に POST する」方法を採った。これが同じ応答を返すことは確かめていない。
3. **`postconf` による拒否の実際の応答コードと段**（`queue_minfree` → `MAIL FROM` の 452、`smtpd_client_restrictions=reject` → `RCPT TO` の 554）と、このイメージで `postfix reload` が効くか。いずれも Postfix の仕様からの導出である。
4. **上流が捕捉用 MTA でない場合の C1 の作り方。** 本書は中止条件にした。
5. **門の `reset-gate.*` 属性を実験前（無し）へ戻すか。** 戻すなら人手で `manage-realm` の PUT を打つことになる。本書は戻さない。
6. **ログインの検査器に対象利用者の上書きを足すか。** 足せば利用者のクラスタでも統合スタックと同じ検査器で測れる。
7. **所要時間の判定式の改定（順位和検定）の実装**と、先に入った時計の扱い。本リポジトリに追跡する issue がまだ無い。
8. **認証基盤が一時ロック中の利用者に返す応答**（文言・ステータス・所要時間）。本書は期待値を置かず、実測で決める。
9. **観測スタック・Istio エッジ（床の経路）が利用者のクラスタに在るか。** 無ければ該当の測定は「未測」と記録する。
10. **C2' の 10 秒待ちの経路上に、10 秒未満で打ち切るプロキシが無いか。** リポジトリのマニフェスト（エッジの経路・床の器）には明示のタイムアウトが無い。
