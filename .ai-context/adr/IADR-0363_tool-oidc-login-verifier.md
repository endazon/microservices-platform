---
title: IADR-0363 ツール側 OIDC のログイン開始は別スクリプトで測り、期待値は稼働している側から引く。到達できないツールは段を消費して SKIP と記録する
type: impl-adr
status: Accepted
related_ids:
  - NFR-09
  - NFR-11
  - ADR-0023
  - ADR-0032
  - ADR-0047
  - IADR-0084
  - IADR-0090
  - IADR-0091
  - IADR-0092
  - IADR-0093
  - IADR-0094
  - IADR-0095
  - IADR-0103
  - IADR-0206
  - IADR-0220
  - IADR-0227
  - IADR-0243
  - IADR-0255
  - IADR-0310
  - IADR-0316
  - IADR-0328
  - IADR-0342
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md
---

# IADR-0363: ツール側 OIDC ログイン開始の検証器（#1163）

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（実装）
- Issue: **#1163**（#1127 の子）。作業仕様書:
  [`20260903_issue-1163_tool-oidc-login-verifier`](../specs/20260903_issue-1163_tool-oidc-login-verifier.md)

## コンテキスト —— 「7/7 で通した」は 4 日で偽になっていた

[IADR-0328](./IADR-0328_tool-oidc-edge-issuer-followthrough.md) §実測 は、ブラウザ OIDC を持つ
**7 クライアントすべて**でログインが端から端まで通ったと表に残している（2026-08-31）。
これは**人が手で 1 つずつ curl した結果**であり、再現も回帰検知もできない。

🔴 **着手前の実測（2026-09-03）で、そのうち 1 件は既に落ちていた。**

```console
$ curl -sS --cacert ca.pem --ssl-revoke-best-effort \
    https://vault.localhost:50000/v1/sys/internal/ui/mounts
{"data":{"auth":{},"secret":{}}, …}          ← 未認証で見える auth mount は 0 件
$ curl -sS ... -X POST https://vault.localhost:50000/v1/auth/oidc/oidc/auth_url -d '{"role":"default", …}'
{"errors":["permission denied"]}                 ← HTTP 403。ブラウザは開始 URL を得られない
$ curl -sS ... -X POST https://vault.localhost:50000/v1/auth/msp-no-such-mount/oidc/auth_url -d '{…}'
{"errors":["permission denied"]}                 ← 対照。**存在しない mount も同じ 403**
$ curl -sS ... https://vault.localhost:50000/v1/sys/seal-status
{"type":"shamir","initialized":true,"sealed":false, …}   ← 陽性対照。未認証の口は生きている
```

**測ったのは「ブラウザが OIDC ログインを開始できない」ことである。** 原因の断定には対照が要る ——
存在しない mount（`auth/msp-no-such-mount/...`）も **同じ 403 `permission denied`** を返すので、
403 だけでは「mount が消えた」と「在るが未認証で拒まれた」を分けられない（実測）。
分けているのは時間の対照である: 同じ口は 2026-08-31 に `auth_url` を返しており（IADR-0328 §実測）、
当該 Pod はその後の **2026-09-02T09:40:57Z に 19 回目の再起動**をしている。dev Vault はインメモリで、
Pod 再起動で `auth/oidc` ごと消える（runbook の揮発マトリクスが宣言しているとおり）。
**Pod は Running・`sys/health` も `sys/seal-status` も 200 を返す**（未認証で引ける口は生きている＝陽性対照）。
つまり **#1163 が想定した縮退は、#1163 の着手時点で既に起きていた。**

## 決定 1 — **既存へ段を足さず、別スクリプトを置く**

`scripts/verify-tool-oidc-logins.sh` を新設した。`scripts/verify-oidc-edge-flow.sh` へ
opt-in の段として足す案は採らない。

| 観点 | 既存へ足す | 別スクリプト（採用） |
| --- | --- | --- |
| 前提 | エッジ 1 本＋`platform-spa` 1 client | ツールごとに配備の有無が違う（opt-in ゲート 5 種） |
| 資格情報 | 必要（TOTP を含む） | **不要**（ログインを完了させない） |
| 実行時間 | 既に 11〜23 段 | 15 段。片方だけ回したい |
| 大きさ | 既に 987 行 | 混ぜると読めなくなる |

**同じスクリプトに入れると「前提未整備」の意味が 2 つになる** —— 既存は「エッジが無い」、
本件は「そのツールを配備していない」。exit 2 が両方を指すようになると、切り分けの手がかりが減る。

## 決定 2 — 段は 3 種。**陰性対照を段として持つ**

1. **(a) ログイン開始がエッジ Keycloak の認可端点へ向く**（ツール 1 件につき 1 段）
2. **(b) その認可 URL で Keycloak のログインフォームが返る**（同 1 段）
3. **(c) 陰性対照: 未登録の `redirect_uri` は 400 で拒まれる**（末尾に 1 段）

🔴 **(c) が無いと (b) の PASS は何も言っていない。** 「Keycloak が `redirect_uri` を何でも通す」
状態でもログインフォームは返るからである。**「在ることの確認」には、それが在らないときに
落ちることの確認を対で置く**（#972 / [IADR-0252](./IADR-0252_abac-positive-path-observation.md) と同じ型）。
実測では未登録の `https://msp-verify-unregistered.invalid/callback` に対し Keycloak は
**HTTP 400 ＋ `kc-error` 画面**を返した。

**ログインの完了（資格情報 POST → callback → セッション確立）は測らない**（#1163 §射程外）。
完了まで測ると、検証器が「どの利用者なら成立するか」を抱え込むことになる ——
IADR-0328 が記録したとおり **Grafana は `admin` で落ち、MinIO は `admin` でしか成立しない**。
これは issuer とは無関係な事情であり、ログイン開始の健全性とは別に測るべきものである。

## 決定 3 — 🔴 **期待値をスクリプトへ列挙しない。稼働している側から引く**

| 期待値 | 従来（人手の手順書） | 本検証器 |
| --- | --- | --- |
| 認可端点 | 手順書に URL を書く | **discovery の `authorization_endpoint`** |
| `redirect_uri` が登録済みか | realm JSON と目で突合 | **Keycloak 自身に判定させる**（段 b） |
| Wiki.js のストラテジキー | `7c1f6f2e-…` を写す | **GraphQL `activeStrategies`** |
| ツールの origin | ツールごとに URL を並べる | `https://%s.localhost:50000` の**書式 1 本**から組む |

列挙を持つと、**realm 側とツール側の片方だけが変わったときに静かに割れる**（#780 で
「ADR に書いた」と「配線した」が乖離したのと同じ構図）。とくに Wiki.js のキーは
seed の SQL が `COALESCE` で**既存値を再利用する**ため、DB を作り直した環境では値が違う。

段 (a) が `redirect_uri` について見るのは**帰属だけ**である ——「ツール自身の origin を
指しているか」。登録の可否は Keycloak が持っており、そこを二重に持たない。

## 決定 4 — **`-k` を持たない。CA を解決できなければ測らずに exit 2**

[IADR-0328](./IADR-0328_tool-oidc-edge-issuer-followthrough.md) 決定 3 は
`verify-oidc-edge-flow.sh` に「CA が取れなければ `-k` へ落ちて警告」という fail-safe を入れた。
**本検証器はそれを引き継がない。**

理由: 既存スクリプトは OIDC の**導線**を測るのが目的で、TLS はその前提にすぎない。
本検証器は**エッジ host（`*.localhost:50000` の 7 本）へブラウザが実際に到達できるか**を
測るものであり、**証明書の検証は測定対象の一部**である。切って測ったら測っていないのと同じ
（#1074 はまさにそれで SAN の欠落を見逃した）。

したがって CA は ①`OIDC_CA_BUNDLE` ②`cert-manager/local-edge-root-ca` の順に解決し、
**どちらも無ければ exit 2 で終える**。`OIDC_TLS_INSECURE` に相当する逃げ道も置かない。
Windows の schannel 対策の `--ssl-revoke-best-effort` は**失効確認だけ**を緩めるもので、
チェーン検証とホスト名照合は有効なまま残る（`-k` とは別物）。

## 決定 5 — **未配備は SKIP。ただし段は消費する。catch-all は「宣言」に数えない**

opt-in ゲート（`OBSERVABILITY` / `HEADLAMP` / `ARGOCD` / `WIKIJS_OIDC` / `VAULT`）が
無効なツールを FAIL にしてはならない（#1163 受け入れ基準 6）。一方で
**「到達できない」を一律 SKIP にすると、エッジのルートが消えた事故まで緑になる。**

分け方は**クラスタから引く**（スクリプトへ host を列挙しない）:

| 観測 | 判定 |
| --- | --- |
| エッジが host を宣言していない／宣言を読めない、かつ到達できない | **SKIP**（未配備） |
| エッジが host を**宣言しているのに**到達できない | **FAIL**（配備事故） |
| 到達できるがログイン開始が認可端点へ向かない | **FAIL** |

🔴 **catch-all（`*`）を宣言に数えない。** エッジには platform frontend の `*` ルートが常に 1 本
あるため、これを数えると**どんなホスト名でも「宣言あり」になる**。実測（本 PR の実走）では
存在しない `*.invalid` を 7 件とも FAIL と報告した。**完全一致だけを宣言と数える。**

**SKIP でも段は消費する**（`STEPS` を進める）。飛ばすと、段が静かに消えても
「PASS が減るだけで EXIT=0」になる（#466 / [IADR-0255](./IADR-0255_edge-smoke-step-loss-gate-and-unobservable-search.md)）。
段数の単一情報源は母集合そのもの（`TOTAL = 件数 × 2 + 1`）で、固定値は書かない（#1124 と同型の事故を避ける）。

**全件が SKIP なら exit 2。** 何も測っていない実行を「緑」と呼ばせない。

## 決定 6 — **判定は JS の純粋関数へ出し、シェルは I/O だけを持つ**

判定ロジックを `scripts/lib/tool-oidc-login.js` に置き、`scripts.repo.test.js` が
**変異させて落ちること**を確かめる（17 件）。

**シェル本文を grep するだけの検査にしない。** grep は「文字列が在ること」しか見ないので、
**判定が逆になっても緑のまま通る**（#992 / #1124 で学んだ形）。実際に固定したのは、
①別 realm の認可端点 ②in-cluster issuer（`http://keycloak:8080/...`＝IADR-0328 が直した縮退）
③`redirect_uri` の帰属ずれ ④`client_id` の欠落 ⑤ログイン開始が空（Vault の実測がこの形）
の 5 変異が**すべて FAIL になる**ことである。

🔴 **段数の門も「書いてあること」では確かめない。** 走査だけでは門が実際に落ちるか分からないので、
**7 ツールと Keycloak を演じる HTTP スタブ**（接続先は env で差し替えられる）へ検証器を向けて
実際に走らせ、3 つの終了経路を固定した ——
①そのまま走らせると **15 段すべてが刻まれ EXIT=0**（各クライアントが段 (a)(b) を 1 本ずつ持つ・
PAR の `bff` も PASS になる）、②**段の `step` を 1 本消すと** 実行 8 対宣言 15 で門が落ち **EXIT=1**、
③到達できない先へ向けると 15 段とも SKIP で **EXIT=2**（緑にならない）。
段数の宣言（`TOTAL` の式の係数）は**テスト側へ書き写さず、本文を走査して数えた実段数と突き合わせる**
（写しは書き手が両方を同じ誤った値で揃えると検出力がゼロになる。#1124）。
**スタブが測っているのは検証器の結線であって、クラスタの健全性ではない**（そちらは上の実測が持つ）。

## 実測（稼働 k3s `v1.35.4+k3s1` / Istio エッジ・2026-09-03）

```console
$ bash scripts/verify-tool-oidc-logins.sh
  issuer : https://keycloak.localhost（realm platform）
  TLS    : 検証する（CA=/tmp/msp-verify-tool-oidc-ca.pem）。-k は持たない
  母集合 : 7 クライアント（scripts/lib/tool-oidc-login.js の TOOLS が単一情報源）
[前提] authorization_endpoint = https://keycloak.localhost/realms/platform/protocol/openid-connect/auth
[前提] エッジが宣言している host: 17 件
[1/15]  PASS  bff:      client_id=bff → 認可端点（PAR。redirect_uri は request_uri へ押し込まれている）
[2/15]  PASS  grafana:  redirect_uri=https://grafana.localhost:50000/login/generic_oauth
[3/15]  PASS  argocd:   redirect_uri=https://argocd.localhost:50000/auth/callback
[4/15]  PASS  headlamp: redirect_uri=https://headlamp.localhost:50000/oidc-callback
[5/15]  PASS  minio:    redirect_uri=https://minio.localhost:50000/oauth_callback
[6/15]  FAIL  vault:    ログイン開始 URL を取り出せない（ツールは応答するが認可 URL を返していない）
[7/15]  PASS  wiki-js:  redirect_uri=https://wiki.localhost:50000/login/<key>/callback
[8..14] 段 (b): bff / grafana / argocd / headlamp / minio / wiki-js は HTTP 200 ＋ kc-form-login。vault は SKIP
[15/15] PASS  未登録の redirect_uri は HTTP 400 で拒まれた（陰性対照）
結果: PASS 13 / FAIL 1 / SKIP 1（段 15/15）  落ちたクライアント: vault   EXIT=1
```

**終了経路をすべて対照つきで確かめた**（生出力は PR 本文。段は**どの条件でも 15/15 刻まれる**）:

| 対照 | 与えた条件 | 観測 |
| --- | --- | --- |
| 正常 | そのまま | PASS 13 / FAIL 1 / SKIP 1（vault）・**EXIT=1** |
| 陽性対照（issuer のずれ） | `OIDC_REALM=master` | 7 件とも「認可端点が discovery と一致しない」＋陰性対照も組めず FAIL 8 / SKIP 7・**EXIT=1** |
| 陽性対照（配備事故） | `TOOLS_ADMIN_ORIGIN_FMT` のポートを 50001 へずらす | **宣言のある 6 host が SKIP ではなく FAIL**（HTTP 000）。ずらしていない `bff` は PASS のまま・PASS 3 / FAIL 6 / SKIP 6・**EXIT=1** |
| 前提未整備（全件未配備） | `*.invalid` を向ける | 宣言が無いので 15 段すべて SKIP・**EXIT=2**（緑にしない） |
| 前提未整備（CA 不在） | `OIDC_CA_BUNDLE=/nonexistent-ca.pem` | 「検証を切って（-k で）測るくらいなら測りません」・**EXIT=2**（1 段も走らせない） |

🔴 **配備事故の対照が効いていることが重要である。** ポートをずらした 6 件は「到達できない」まま
**SKIP ではなく FAIL** になっている —— 一律 SKIP にする設計だと、エッジのルートが消えた事故が
そのまま緑になる（決定 5）。

## 側所見 — glibc の `*.localhost` は ArgoCD の OIDC を壊していない

PR #1152 §射程外 は「argocd-server（Ubuntu / glibc 2.43）も `keycloak.localhost` を引けない」と
記録している。**再現したが、結論は違う。**

```console
$ kubectl -n argocd exec deploy/argocd-server -- sh -c \
    "getent hosts keycloak.localhost; echo getent_rc=$?; \
     getent hosts keycloak.platform-infra.svc.cluster.local >/dev/null; echo control_rc=$?"
getent_rc=2      ← glibc の NSS は *.localhost を引かない
control_rc=0     ← 陽性対照。同じ resolv.conf で in-cluster 名は引ける
$ kubectl -n argocd logs deploy/argocd-server --tail=200 | grep -i oidc
{"level":"info","msg":"Initializing OIDC provider (issuer: https://keycloak.localhost/realms/platform)",…}
```

**argocd-server は Go バイナリで、名前解決に glibc NSS を使っていない**（pure-Go resolver）。
discovery は成功し、`/auth/login` は 303 で正しい認可 URL を返す（上の段 3）。

🔴 **`getent` の失敗を「そのツールの OIDC が壊れている」と読むのは誤りである。**
どのランタイムが解決するかで結論が変わる。**本検証器はランタイムの実挙動（ログイン開始が
実際にどこへ向くか）を測るので、この取り違えを構造的に避けている。**

## 却下した代替案

| 案 | 却下理由 |
| --- | --- |
| `verify-oidc-edge-flow.sh` へ opt-in の段として足す | 前提・資格情報・exit 2 の意味が二重になる（決定 1） |
| 期待する `redirect_uri` をスクリプトへ列挙して突合する | realm とツールの片方だけ変わったとき静かに割れる（決定 3）。**受け入れ基準 2 が明示的に禁じている** |
| Keycloak の Admin API で realm を読んで突合する | 管理資格が要る（読み取り専用の原則を破る）。**`kcadm.sh` の pod 内実行は Keycloak 本体を OOMKill する**（既知） |
| CA が無ければ `-k` へ落ちる（既存の fail-safe を踏襲） | TLS 検証が測定対象の一部である（決定 4）。#1074 と同型 |
| 到達できないツールを一律 SKIP にする | エッジのルート欠落まで緑になる（決定 5） |
| 資格情報 POST → callback まで通す | 利用者ごとの成立条件を検証器が抱え込む（決定 2）。#1163 §射程外 |
| 判定をシェルの `grep`／`case` に置き、テストは本文の grep | 判定が逆になっても緑で通る（決定 6） |
| ここで Vault の OIDC を復旧する | 検証器は読み取り専用。復旧は runbook STEP 2 の再走であり別作業 |

## 影響

- **`docs/operations/local-sso-recovery-runbook.md` STEP 4** の手作業 curl 4 本を本検証器の
  1 コマンドへ置き換えた。併せて、同 runbook が残していた
  **「各ツールは `http://keycloak:8080/...` へリダイレクトするため hosts 追記と port-forward が必要」**
  という記述を落とした —— [IADR-0328](./IADR-0328_tool-oidc-edge-issuer-followthrough.md) 決定 1 で
  ブラウザの飛び先はエッジ host へ移っており、**本検証器の実測がそれを 7 件とも確認している**。
- **稼働クラスタの Vault OIDC が落ちていることが分かった。** 復旧は runbook STEP 2 の再走で、
  本 PR の射程外（読み取り専用）。
- CI 実行は #466（`blocked`）に依存する。本検証器は稼働 dev クラスタ向けである。
