---
title: IADR-0362 SPA の静的配信を nginx から Caddy へ移す。3 つの契約（元 URI 無改変の /bff プロキシ・実行時 config の起動時描画・history fallback）を Caddyfile 1 枚で保ち、ポート・Service・probe は変えない
type: impl-adr
status: Proposed
related_ids:
  - FR-14
  - NFR
  - NFR-11
  - ADR-0021
  - ADR-0023
  - IADR-0076
  - IADR-0078
  - IADR-0081
  - IADR-0317
  - IADR-0348
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md
---

# IADR-0362: SPA 静的配信の Caddy 移送（#442 の残作業）

- 状態: Proposed
- 日付: 2026-09-03
- 決定者: claude（実装）／ endazon（マージ判断）。**道の選択は計画 `ADR-0021` が既に決めている**（実装側の裁量ではない）

## 起点・関連

- 計画 ADR: **`ADR-0021`**（エッジ ＝ Istio Ingress Gateway（入口・Envoy）＋ **Caddy（SPA 静的配信）**・`Accepted`。
  「静的配信の位置づけ」節が「Envoy はファイル配信に不向きなため、SPA の実体配信は Caddy（mesh 内サービス）が担う」と明記）／
  `ADR-0023`（エッジ証明書自動化。入口の TLS は本 PR では触らない）
- 実装 issue: **#1135**（親 **#442** の受け入れ観点の最後の 1 件）
- 先行: [IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)（入口を Istio Ingress Gateway へ。
  「**Caddy はまだ入っていない**……#442 の残作業として残る」と明記していた）／
  [IADR-0078](./IADR-0078_frontend-k8s-serving.md)（SPA の k8s chart 配信・probe の意味）／
  [IADR-0076](./IADR-0076_edge-bff-routing-and-oidc-hostname.md)（`/bff/*` に rewrite を張らない契約）／
  [IADR-0081](./IADR-0081_frontend-base-registry-mirror.md)（base イメージのミラー経由）／
  [IADR-0348](./IADR-0348_private-notes-sync-edge-route.md)（エッジ前置と catch-all の 200/401 の対）
- 作業仕様書: [`20260903_issue-1135_spa-serving-nginx-to-caddy`](../specs/20260903_issue-1135_spa-serving-nginx-to-caddy.md)

## コンテキストと課題

`ADR-0021` はエッジを 2 つの部品で定義している —— **入口＝Istio Ingress Gateway** と
**SPA 配信＝Caddy**。#782（`IADR-0317`）で入口の側は移ったが、**SPA を配る Web サーバは nginx のまま**で、
計画と実装が半分だけ食い違った状態が残っていた。リポジトリ側も 2 箇所（`deploy/local/edge-istio/README.md`
「既知の限界」・`IADR-0317`「結果」）でこれを #442 の残作業と明記していたが、**それを追う issue が無かった**。

🔴 **難しさは「Web サーバの差し替え」ではなく「3 つの契約が 1 つのファイルに同居していること」にある。**
`nginx.default.conf.template` は 28 行しかないが、次の 3 つを同時に担っていた。

1. **`/bff/*` を `${BFF_UPSTREAM}` へ元 URI 無改変でプロキシする**（`IADR-0076` 決定 1）
2. **実行時 config（`config.js`）を `no-store` で返す**（接続先をビルドへ焼き込まない）
3. **SPA の history fallback**（未知パス → `index.html`）

どれか 1 つを落としても**画面は一見動く**。壊れるのは経路だけであり、静かに壊れる。

## 決定

### 決定 1: runtime stage を `caddy:2.11-alpine`（`mirror.gcr.io/library` 経由）にする

`IADR-0081` の `BASE_REGISTRY` 機構をそのまま使う（`docker.io` を直参照しない）。タグは nginx 時代の
`1.27-alpine` と同じ**マイナー固定**に揃える —— `:2` だけだと major 内の任意のマイナーへ動く。

### 決定 2: 配信設定は `Caddyfile` 1 枚。3 つの契約を排他な `handle` で書く

`handle` ブロックは相互排他で、Caddy がパスの具体性順に評価する。`/healthz` → `/config.js` → `/bff/*` →
catch-all の 4 本にする。

**🔴 `handle_path` を使わない。** `handle_path` は一致した前置を**剥がす**。剥がすと `IADR-0076` 決定 1 の
「rewrite を張らない」契約が破れ、`/bff` を剥がさず受ける BFF・エッジ VirtualService・compose の 3 者と
食い違う。`handle` は剥がさない —— この 1 語が契約の担保である。

`{$BFF_UPSTREAM}` は Caddy が**設定読み込み時に**環境変数から展開するため、nginx が必要としていた
テンプレート描画（`/etc/nginx/templates/` ＋ `envsubst`）が配信設定側では不要になった。

転送ヘッダ: `Host` は `reverse_proxy` が既定で元のまま上流へ渡す（nginx の `proxy_set_header Host $host`
と同じ）。`X-Forwarded-For` / `X-Forwarded-Proto` も既定で付く。**`X-Real-IP` だけは付かない**が、
`git grep -ci "X-Real-IP\|X-Forwarded" -- src/platform/backend` は **0 件**（そもそも転送ヘッダを読む実装が無い）。
**陰性結論の陽性対照**として同じ走査で `X-Accel-Buffering` は 1 件当たる ＝ 走査自体は壊れていない。

### 決定 3: 実行時 config の描画は起動時のまま。エントリポイントを自前で持つ

Caddy 公式イメージは nginx の `/docker-entrypoint.d/*.sh` 規約を持たない（entrypoint が `caddy` そのもの）。
`docker-entrypoint.sh` を 1 枚置き、`config.js` を描画してから `exec "$@"` で Caddy を起動する。

- **`envsubst`（`apk add gettext`）を使い続ける。** 置換の意味論を現行とバイト等価に保つためで、
  `sed` へ書き換えると値に含まれる `&` / `\` のエスケープという**新しい壊れ方**を持ち込む。
- **描画に失敗したらコンテナが起動しない**（`set -eu` ＋ `exec`）。readiness が 404 で落ちる現行の
  fail-safe より早く落ちる方向であり、弱くはならない。
- **🔴 Caddy の `templates` ディレクティブでリクエスト時に描画する案は採らない。** それだと `config.js` が
  **常に 200 になり**、`IADR-0078` 決定 2 の readiness が「生成完了の確認」でなくなる。
  検査が静かに失効する形であり、設定は短くなるが意味が痩せる。

### 決定 4: ポート・Service・probe は変えない。`/healthz` は足すが readiness には使わない

`8080` / `frontend-service` / liveness `/` / readiness `/config.js` を据え置く（`IADR-0078` 決定 2）。
`/healthz` は手動スモークと切り分けのための口として足すが、**readiness の宛先にはしない** ——
`/healthz` は「プロセスが生きている」しか言わず、readiness の意味（config.js の描画完了）を薄める。

### 決定 5: 圧縮を有効にし、セキュリティヘッダは SPA を壊さない範囲に限る

`encode zstd gzip`（nginx 側は無圧縮だった）。ヘッダは `X-Content-Type-Options: nosniff` /
`Referrer-Policy: same-origin` / `X-Frame-Options: DENY` と `-Server`（版を名乗らない）に限る。
**CSP は入れない** —— 実測の裏付けなしに入れると画面が静かに欠ける。別 issue の仕事である。

## 実測（稼働 k3s ＋ ローカルコンテナ・2026-09-03）

frontend の Deployment **だけ**を新イメージへ差し替えた（`kubectl set image`。他 Pod は再起動していない）。
TLS の検証は切っていない —— chain の正は `openssl s_client -CAfile`（`Verify return code: 0 (ok)`）が持ち、
curl は `--cacert` ＋ `--ssl-no-revoke`（Windows/schannel の**失効照会のみ**無効）で叩いた。**`-k` は使っていない。**

### エッジ（`https://localhost/`・Istio Ingress Gateway 経由）

| 観測 | before（nginx） | after（Caddy） |
| --- | --- | --- |
| `/` | 200 `text/html` 926B | 200 `text/html; charset=utf-8` 926B |
| `/settings`（深いパス） | 200 `text/html` 926B | 200 926B |
| **陰性対照** `/no-such-path-xyzzy` | 200 index.html 926B | **200 index.html 926B**（fallback の証明） |
| **陽性対照** `/assets/index-*.js` | — | **200 `text/javascript` 86,583B・`content-encoding: gzip`**（実ファイルは fallback せず実体が出る） |
| `/config.js` | 200・`cache-control: no-store` 798B | 200・`no-store` 798B |
| `/healthz` | **200 `text/html` 926B**（口が無く catch-all で index へ落ちていた） | **200 `text/plain` 2B**（`ok`。新設の口） |
| `/bff/auth/me` | 401 | **401**（元 URI 無改変で BFF へ抜ける） |
| `/private-notes/sync/manifest` | 401 | **401**（`IADR-0348` の前置が生きている） |

**陰性対照と陽性対照は対で読む。** 「未知パスが 200 index.html」だけでは
「fallback が効いた」と「全部 index.html を返すだけの壊れた配信」を区別できない。実アセットが
86KB の JS として出ること（陽性側）と併せて初めて `try_files` が正しいと言える。

### ローカルコンテナ（クラスタ非経由・同一 dist で before/after を対比）

| | before（nginx 1.27.5） | after（Caddy 2.11） |
| --- | --- | --- |
| イメージサイズ | **54.4 MB**（blob 21.51 MB） | **72.55 MB**（blob 26.73 MB）＝ **+18.2 MB / +33%** |
| 起動〜`/config.js` 200 | **1,619 / 1,697 ms** | **1,058 / 1,131 ms** ＝ **-33〜35%** |
| `/index.html` の gzip | 無し | `content-encoding: gzip` |
| `Server` ヘッダ | `nginx/1.27.5`（版を漏らす） | 無し（`-Server`） |
| `/config.js` の Content-Type | `application/javascript` | `text/javascript; charset=utf-8` |
| 上流が名前解決できないとき | **起動できず終了**（`host not found in upstream`） | 起動する（遅延解決。要求時に 502） |

**イメージは 18MB 大きくなった。** `ADR-0021` は Caddy を「設定が小さく運用が軽い」と評したが、
それは**設定の話であって配布物の大きさの話ではない**（Caddy の単一バイナリは nginx の alpine 構成より大きい）。
起動は速く、設定は 28 行 → 実質 10 行程度に減った。**この差分は受容する**（記録して黙らせない）。

**起動時間は 2 回測って両方とも同じ向きだった**（1,619→1,058 ms と 1,697→1,131 ms。2 回目は
本 PR の最終ツリーから再ビルドしたイメージで測り直したもの）。1 回の計測だけでは
「たまたま速かった」と区別できないため、2 標本を残す。

### 認証導線（`scripts/verify-oidc-edge-flow.sh`・#1135 の受け入れ基準）

**移送の前後で PASS 数が減らないこと**が issue の基準である。稼働 k3s に対して同じスタックで 2 回走らせた。

| | before（nginx `:latest`） | after（Caddy） |
| --- | --- | --- |
| 結果 | **PASS 19 / FAIL 0**（段 11/11 完走） | **PASS 19 / FAIL 0**（段 11/11 完走） |

認可コード + PKCE の交換・`iss` / `clearance` / `department` クレーム・認証後の `/bff/documents` 200 まで
同じ数だけ通った。**TLS 検証は既定のまま**（同スクリプトは CA をクラスタから取り出して `--cacert` で検証する。
`-k` へは落ちていない）。

### 測った経路と、そこから言えないこと

- 測ったのは **Istio Ingress Gateway 経路**である（`server: istio-envoy` が全応答に載る。#1135 の
  「`ISTIO=1 LOCALEDGE=1` の経路でも同じ結果になる」はこの経路そのもので測ったということ）。
- 🔴 **エッジ越しでは `-Server` の効果は見えない。** Envoy が自分の `server: istio-envoy` で上書きするため、
  「配信サーバの版を名乗らない」ことが観測できるのは**コンテナ直叩き**（上表）だけである。
  エッジ越しの応答から「Caddy が版を隠している」とは言えない —— 隠しているのは Envoy かもしれない。
- `Caddyfile` は **`caddy fmt` に対して整形済み**である（`caddy fmt -` の出力が入力とバイト一致・exit 0）。
  途中まで pod の起動ログに `Caddyfile input is not formatted`（10 行目）が出ていたが、これは
  **1 つ前のビルドに空行が 1 行余計に入っていた**ためで、最終ツリーのイメージでは出ない（実測 0 件）。

## 影響・代替案

- **代替（不採用・nginx を維持）**: 計画 `ADR-0021` の決定に反する。実装側の裁量では覆せない。
- **代替（不採用・`handle_path`）**: 記述は短くなるが前置を剥がし `IADR-0076` 決定 1 を破る。
- **代替（不採用・`templates` で config.js をリクエスト時描画）**: 決定 3 参照。readiness の意味が痩せる。
- **代替（不採用・`sed` で描画）**: `envsubst` を落として `apk add` を省けるが、値に含まれる
  `&` / `\` のエスケープという新しい壊れ方を持ち込む。
- **挙動差（受容）**: `/assets/`（実体のないディレクトリ）が nginx の 403 から Caddy の 200 index.html へ
  変わった。ディレクトリ一覧は**どちらも出ない**（`browse` は無効）ので露出は増えていない。
- **後方互換**: ポート・Service 名・probe・env（`BFF_UPSTREAM` ほか）・`/bff` の契約はすべて据え置き。
  chart / compose / エッジ宣言の**実体は無改変**（コメントのみ追随）。`images.yml` は compose の build 定義から
  対象を導出するため**起動条件も必須チェック名も変わらない**。

## 計画への環流（候補）

`ADR-0021`「理由」が「Caddy は …… HTTP/2・HTTP/3・**brotli 圧縮を標準装備**し」と書くが、
**Caddy v2 の標準配布バイナリに brotli エンコーダは含まれない**（`encode` は gzip / zstd。
brotli はプラグイン再ビルドが要る）。また mesh 内は素の HTTP なので HTTP/2・HTTP/3 も効かない
（実測ログ: `HTTP/2 skipped because it requires TLS` / `HTTP/3 skipped ...`）。
**決定（SPA 配信 ＝ Caddy）には影響しない理由節の事実誤認**であり、本 PR は決定に従った。
