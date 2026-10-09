---
title: IADR-0526 Loki・Tempo の管理用の口は、経路 B で製品を Pod の loopback 待ちにし、同じ Pod の認証付きのリバースプロキシ（書き込み・読み取りの 2 つの身元と道の列挙）と NetworkPolicy の 2 段で塞ぐ。compose は 0 段として記録する
type: impl-adr
status: Accepted
related_ids: [ADR-0133, ADR-0112, ADR-0107, ADR-0006, ADR-0124, NFR-18, IADR-0520, IADR-0514, IADR-0461, IADR-0486, IADR-0168, IADR-0210, IADR-0077]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0133_admin-endpoint-front-authentication.md 決定 1（4 条件）・決定 2・決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md 決定 1（基準 D）
related_specs:
  - ../specs/20261009_1842_loki-tempo-front-auth.md
---

# IADR-0526: Loki・Tempo の管理用の口を、前段の認証と NetworkPolicy の 2 段で塞ぐ（#1842）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: claude（前段の形・身元の分け方・トークンの作り方）。dev の配備で今入れることは利用者の裁定（2026-10-09）

## 起点・関連

- 起点 issue: **#1842**（planning#750 の裁定の 4）。issue は「本番像へ展開するとき」としていたが、**利用者が dev の配備で今入れると裁定した**（2026-10-09）。
- 計画: **ADR-0133 決定 1**（前段の認証を基準 D の「認証を必須にできる」に数える 4 条件）・決定 2（Loki・Tempo は条件を満たす配備を作れば基準 D を満たす）・決定 3（配備状況の 3 点セット）。ADR-0112 決定 1（基準 D）。
- 前提: [IADR-0520](./IADR-0520_infra-product-default-egress-disabled.md)（Loki・Tempo の既定の外部通信。同じ ConfigMap を触る）・[IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（初回点検・digest 固定）・[IADR-0461](./IADR-0461_object-storage-seaweedfs-deployment.md) 決定 10（製品内の認証と NetworkPolicy の 2 段の先例）・[IADR-0486](./IADR-0486_vault-audit-two-devices-stdout-and-collector-socket.md)（Vault の audit を Loki の `{job="vault-audit"}` へ）・[IADR-0168](./IADR-0168_grafana-provisioning-parity.md)（Grafana の provisioning の経路間パリティ）・[IADR-0210](./IADR-0210_local-k8s-observability-persistence.md)（永続化の patch が `containers/0` を指す）・[IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md)（経路 B の可観測性 overlay）。
- 基点コミット: MSP `origin/develop` `842b970f`。

## コンテキストと課題

Loki 3.0.0・Tempo 2.5.0 は製品単体で認証を掛けられない（`auth_enabled: false`）。読み書きの口と、運用の口（`/flush`・`/config`・`/ingester/shutdown`・`/status/*`・ring 系）・削除 API（Loki `/loki/api/v1/delete`）・ルーラー・`/api/overrides` が**同じ 1 つの HTTP の口**に並ぶ。経路 B の `platform-infra` には NetworkPolicy も前段も無く、**同じクラスタのどの Pod からも、Vault の audit が入る Loki の削除 API や `/flush` へ届いた**（0 段）。ADR-0133 は、前段が身元を検証し（条件 1）、前段を経由しない直接の到達を別の段で塞げば（条件 2）、基準 D を製品の外で満たせると定めた。

## 検討した選択肢

### 前段の形（条件 1）

- **A. 認証付きのリバースプロキシを Pod のサイドカーに置く（採用）** —— 経路 B の既定（`ISTIO` 未指定・0）で効く。前段は Pod と一緒に動き、経路ごとの構成に依らない。
- B. Istio の AuthorizationPolicy（principal を条件） —— `platform-infra` はメッシュの外（サイドカーの注入なし）で、メッシュは opt-in（`ISTIO=1`）。principal を条件にするには Loki・Tempo・collector・Grafana の 4 つをメッシュへ入れる必要があり、`ISTIO=0` の経路 B では前段そのものが消える。採らない。
- C. Loki のマルチテナンシー（`auth_enabled: true` と `X-Scope-OrgID`） —— テナントの名前は**身元の検証ではない**（ヘッダを付ければ誰でもそのテナントになる）。条件 1 を満たさず、運用の口も塞がない。採らない。
- D. 前段の Pod を別に立てる（Deployment の proxy） —— 製品の口が Pod の外へ出たままになり、「前段を経由しない口」を NetworkPolicy 1 段だけで塞ぐことになる。A は製品を loopback 待ちにするので、前段を経由しない口がネットワークにそもそも出ない。採らない。

### 前段の製品

- **Caddy（`caddy:2.11-alpine`。frontend の基底と同じ版・同じ index の digest）（採用）** —— 既に本リポジトリで採用・点検済み（基準 A〜D。frontend の配信）。header・method・path の matcher で身元と道を同時に条件にでき、管理 API（既定 `localhost:2019`）は `admin off` で閉じられる。新しい製品を足さない。
- nginx（`auth_request` / `map`）・oauth2-proxy —— 新しい製品の採用（点検の対象が増える）。採らない。

### 身元の検証の方式

- **Bearer トークン（書き込み・読み取りの 2 つ。乱数の 16 進 64 文字）（採用）** —— collector の exporter（`headers`）と Grafana の datasource（`httpHeaderName1` / `secureJsonData`）がそのまま送れる。2 つに分けるのは、身元ごとに通す道を限るためである（書き込みは push だけ、読み取りは GET の読み取り API だけ）。
- Basic 認証（bcrypt） —— Caddy は平文ではなくハッシュを設定に要求し、起動時にハッシュを作るには平文をコマンドの引数に載せる形になる（#1793 の方針に反する）。採らない。
- mTLS（クライアント証明書） —— 証明書の発行・配布の仕組みが経路 B の `platform-infra` に無い（cert-manager は `LOCALEDGE=1` の opt-in）。採らない。

## 決定

### 決定 1: 製品は Pod の loopback だけで待つ

Loki は HTTP `127.0.0.1:3101`・gRPC `127.0.0.1:9095`、Tempo は HTTP `127.0.0.1:3201`・gRPC `127.0.0.1:9095`。Service の口（Loki 3100・Tempo 3200）は前段のコンテナ（`gate`）が持つ（`targetPort: http` は前段の口）。
Loki は `frontend.address: 127.0.0.1` も与える（query-frontend が querier へ広告する住所の既定はインターフェイスの IP で、gRPC を loopback へ移すとクエリが返らなくなる。実測）。
Tempo の OTLP の受け口（4317・4318）は**書き込みの口で管理用の口ではない**ので、従前どおり全インターフェイスで待つ（決定 4 で collector だけに絞る）。
`containers` の 0 番は製品のまま（永続化の overlay の patch が `containers/0` へ volumeMount を足すため）。

### 決定 2: 前段（条件 1）—— 2 つの身元と道の列挙

| 身元（トークン） | 誰 | Loki（3100） | Tempo（3200） |
| --- | --- | --- | --- |
| 書き込み（`writer`） | otel-collector の `loki` exporter | `POST /loki/api/v1/push` だけ | 通す道なし（403） |
| 読み取り（`reader`） | Grafana の Loki・Tempo の datasource | `GET` の `/loki/api/v1/{query,query_range,labels,label/*/values,series,index/stats,index/volume,index/volume_range,detected_fields,detected_labels,patterns,tail,format_query,status/buildinfo}` | `GET` の `/api/{echo,status/buildinfo,traces/*,v2/traces/*,search,search/tags,search/tag/*/values,v2/search/tags,v2/search/tag/*/values,metrics/query,metrics/query_range}` |
| どちらか | — | 上以外（運用の口・削除 API・ルーラー・旧 push・`/metrics`）は 403 | 上以外（運用の口・`/status/*`・`/api/overrides`・`/metrics`）は 403 |
| 無し・不一致 | — | 401 | 401 |

- 前段そのものの管理 API は閉じる（`admin off`）。
- **運用者が運用の口を使うときは `kubectl port-forward` で Pod の loopback（3101 / 3201）へ届く。** k8s の認証・認可（`pods/portforward`）を通る経路であり、break-glass として残す。
- 判定の実測（道の抜け `..`・`%2F`・二重スラッシュ・大文字を含む）は作業仕様書。前段は道を正規化してから照合し、許可の道から運用の口へ抜ける向きはすべて 403 だった。
- **［2026-10-09 追記 / #1865 の監査］道の抜けは身元を見る前に 400 で断つ。** Caddy の `path` matcher は正規化した道で照合するが、`reverse_proxy` は生の道を上流へ送る。運用の口から許可の道へ正規化される生の道（`/config/../loki/api/v1/labels`・Tempo の `/flush/../api/echo`）が読み取りのトークンで通り、上流へ生のまま届いた（同じ版の caddy で実測）。安全を上流の正規化に懸けないため、両方の Caddyfile の `route` の先頭で、復号した道に `..`・`//`・`.` だけのセグメントを含むもの、生の道（クエリ文字列を除く）に `%2e` / `%2f`（大小とも）を含むものを 400 で拒む（`@unsafe`）。クエリ文字列の `..` とドットを含む名前（`service.name`）は通る。正規化した道へ書き換えて送る形は採らなかった（拒む形のほうが、上流が受け取る道と前段が照合した道が常に同じになり、検査で固定しやすい）。

### 決定 3: トークンの作り方と fail-closed

- Secret `observability-gate`（`platform-infra`。鍵 `writer` / `reader`）は `scripts/k8s-local-up.sh` が `OBSERVABILITY=1` で作る。**既存の値（16 進 64 文字）を引き継ぎ、無ければ `/dev/urandom` から作る**（再実行で値を回すと、起動済みの Pod の env と食い違う）。値は `apply_secret` のファイル経由で渡す（#1793）。**dev の既定値（固定の文字列）は置かない。** ESO へは委譲しない（クラスタの中で閉じる値で、外の保管先に正本を持つ意味が無い）。
- **前段は fail-closed**: Secret を必須で参照し、起動の前に `gate.sh` がトークンを検査する（無い・16 進以外・32 文字未満・2 つが同じ → 起動しない）。16 進に限るのは、Caddyfile の `{$VAR}` が読み込み時の文字列の置換で、引用符や波括弧が設定の構文を壊すためである。
- collector・Grafana は Secret を**任意で**参照する。無ければ空の Bearer を送り、前段が 401 で拒む（collector の既定構成は debug のみで使わない。Grafana は Prometheus を使い続けられる）。

### 決定 4: 到達の制限（条件 2）—— NetworkPolicy を別のファイルに置く

`loki-ingress`: Loki の Pod へは collector と Grafana から 3100 だけ。`tempo-ingress`: Tempo の Pod へは collector から 4317、Grafana から 3200 だけ。前段（`observability-gate.yaml`）と同じ仕組みで兼ねない（`observability-networkpolicy.yaml`）。k3s は NetworkPolicy を既定で強制する。

### 決定 5: 経路 A（compose）は 0 段として記録する（条件 3）

compose に NetworkPolicy は無く、Loki 3100・Tempo 3200 / 4327 をホストへ公開している。前段を置かない（compose の Grafana は匿名に Admin を与えている dev の利便の経路で、前段だけを足しても 1 段にしかならず、ホストの公開も残る）。Grafana の datasource は経路間で同一でなければならない（IADR-0168 の検査）ので、**ヘッダの宣言は compose にも同じ形で置き、値は env**（compose では未設定＝空。`auth_enabled: false` の Loki・Tempo はヘッダを読まない）。collector の compose の設定にはトークンを置かない。

### 決定 6: Alertmanager は製品内（条件 4）—— 本件では配備を変えない

Alertmanager は web config で製品内の認証を掛けられるので、前段ではなく製品内で掛ける（ADR-0133 決定 2）。配備は認証なし（2026-10-08 の点検の記録）のまま残る。本件の射程外として運用仕様書の点検の記録に残す。

### 決定 7: 検査

`scripts/scripts.repo.test.js`（#1842）が、7 つのファイル（製品の config・前段の ConfigMap・Deployment・Service・NetworkPolicy・collector・Grafana・kustomization）にまたがる一致を判定し、陽性対照と、1 か所ずつ壊した 26 の変異（loopback を外す・管理用の口を道に足す・既定を通す・道の抜けの断ちを消す／`route` の先頭から外す・前段の env を任意にする・`gate.sh` を通らずに起動する・NetworkPolicy の from を全 Pod にする 等）で落ちることを固定する。`scripts/k8s-local-up.test.js` がトークンの生成（16 進 64 文字・2 つが別・起動のたびに別・既存値を先に読む・引数に載らない）を固定する。

## 結果

- 良い影響: 経路 B の Loki・Tempo の管理用の口が 2 段（前段の身元の検証・NetworkPolicy）で塞がり、ADR-0133 決定 1 の 4 条件を満たす。Vault の audit が入る Loki の削除 API へ、同じクラスタの他の Pod から届かなくなった（監査の記録の改ざんの経路が 1 つ減る）。
- 悪い影響: 前段のコンテナが 2 つ増える（要求 10m / 32Mi ずつ）。Loki・Tempo の `server` の節が compose と食い違う（意図の乖離。注記と検査で固定）。Grafana の新しい版や新しい機能が使う読み取りの API が列挙に無いと 403 になる（列挙を足す。運用の口でないことを確かめてから）。

## 残余

1. **NetworkPolicy の強制は稼働クラスタで実測していない**（この環境で k3d のクラスタを起こせなかった）。宣言は `kubeconform` と描画で確かめた。前段の判定と通しの経路は、配備と同じ版の公式イメージで実測した（作業仕様書）。
2. **経路 A（compose）は 0 段**（決定 5）。ホストへ口を公開している。
3. **本番像（helm chart）に Loki・Tempo は無い**。展開するときは本 IADR の 2 段を chart の定義へ移す（NetworkPolicy は chart の `networkPolicy.enabled` の仕組みへ、前段は同じサイドカーの形で）。ADR-0133 決定 3 の「本番像へ展開するときは条件 1・2 を配備の定義に含める」はそのまま生きている。**#1842 は本 PR で閉じるため、chart へ足すときに 2 段を検査で強制することは #1867 が追う。**
4. **Tempo の OTLP（4317）は 1 段**（NetworkPolicy だけ）。管理用の口ではない書き込みの口であり、基準 D の対象外として扱った。
5. Alertmanager の製品内の認証（決定 6）は未配備。
6. トークンの回し方（ローテーション）の手順は無い。Secret を消して起動器を再実行し、Loki・Tempo・Grafana・collector を再起動すれば新しい値になる。
7. **道の抜けの断ち（決定 2 の追記）は前段の文字列の判定である。** 上流が `..`・`%2e`・`//` 以外の形（例: 上流が独自に解釈する区切り）で道を読み替える場合は捕まらない。Loki・Tempo の版を上げるときは、作業仕様書の逆向きの表を同じ版で当て直す。

## フォローアップ

- **#1867**: 本番像（helm chart）へ Loki・Tempo を足すときの前段の認証・到達の制限を検査で強制する（残余 3。#1842 は本 PR で閉じるため、chart の時点の追跡をこちらへ移した）。経路 B の配備・検査・文書は本 IADR と同じ PR で入れた。計画の ADR-0133 決定 3 の「現在の実現手段」の更新は計画の側の作業（同 ADR フォローアップ 3）。
