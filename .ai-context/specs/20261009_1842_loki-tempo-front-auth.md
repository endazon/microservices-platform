---
title: 作業仕様書 — Loki・Tempo の管理用の口に、身元を検証する前段（認証付きのリバースプロキシ）と NetworkPolicy の 2 段を dev の経路 B で置く（#1842・planning#750 の裁定の 4）
type: spec
status: done
related_ids:
  - NFR-18
  - ADR-0133
  - ADR-0112
  - ADR-0107
  - ADR-0006
  - ADR-0124
  - IADR-0526
  - IADR-0520
  - IADR-0514
  - IADR-0461
  - IADR-0486
  - IADR-0168
  - IADR-0210
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0133_admin-endpoint-front-authentication.md 決定 1（4 条件）・決定 2・決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md 決定 1（基準 D）と 2026-10-09 の補完
related_specs:
  - 20261009_1841_disable-default-egress
  - 20261008_1787_infra-audit-digest-pin
issue: "#1842"
---

# 作業仕様書 — Loki・Tempo の管理用の口に前段の認証と到達の制限を置く（#1842）

## 目的と射程

計画 ADR-0133 は、基準 D（ADR-0112 決定 1）の「認証を必須にできる」に、**製品の前段で掛ける認証**を 4 条件つきで含めた。
Loki と Tempo は製品単体で認証を掛けられない（`auth_enabled: false`。運用の口 `/flush`・`/config`・削除 API 等）。
issue は「本番像（helm chart）へ展開するとき」に条件 1・2 を配備の定義へ含めることを求めていたが、
**利用者の裁定（2026-10-09）で、本番像を待たずに dev の配備で今入れる**ことになった。

- **射程**: 経路 B（`deploy/local/observability`。k3s/k3d・Rancher Desktop）の Loki・Tempo に、
  **条件 1（身元を検証する前段）と条件 2（前段を経由しない直接の到達を別の段で塞ぐ）**を入れる。
  経路 A（compose）は**条件 3 に従って段数を記録する**。Alertmanager は**条件 4 に従って現状を点検して記録する**（配備は変えない）。
- **射程外**: 本番像（helm chart）への可観測性の展開（chart に Loki・Tempo は無い。計画の未決事項「監視の stg/prod 展開」）、
  Alertmanager の web config の配備、compose への前段の追加、egress の既定拒否。

基点: MSP `origin/develop` `842b970f`。計画: `project-planning` `origin/main` `142c3e4`。

## 計画の条件（読み取り。隣接クローン・`git show origin/main:`）

ADR-0133 決定 1 の 4 条件:

1. 前段は身元を検証する（mTLS の principal / JWT を条件にした AuthorizationPolicy、または**認証付きのリバースプロキシ**）。L3/L4 の到達の制限は認証と数えない。
2. 到達の制限は別の段（NetworkPolicy 等）で、前段を経由しない Pod への直接の到達を塞ぐ。前段と同じ 1 つの仕組みで兼ねない。
3. 前段の無い経路は段数を明記する（ADR-0112 決定 2 の経路 B と同じ書式）。
4. 製品内で認証を掛けられる製品は製品内を優先する（Alertmanager の web config）。

ADR-0133 は前段の形を 2 つ並べて**どちらでもよい**としている（決定 1 の 1）。どちらを採るかは配備の事情で決まる実装の判断であり、
計画の裁定を要する選択は残っていない（下の「決定」）。

## 母集合の引き方（規則 9・10・6）

**誤りの側の文字列**（「前段なしで Loki・Tempo へ届く」こと）で引いた。宛先の文字列 `loki` / `tempo` と口の番号（3100・3200・4317・9095）で
追跡下の全ファイルを走査し（`git grep -l -i -E '\bloki\b|\btempo\b' -- ':!.ai-context' ':!CHANGELOG.md'`。42 ファイル）、
1 件ずつ「Loki・Tempo の口へ届く者か／口を定める者か／名前だけ現れる文書か」に分けた。

### 口（Loki 3.0.0・Tempo 2.5.0。版の実物で確かめた）

| 製品 | 口 | 中身 | 管理用の口か |
| --- | --- | --- | --- |
| Loki | HTTP 3100 | 書き込み `/loki/api/v1/push`（と旧 `/api/prom/push`）・読み取り `/loki/api/v1/{query,query_range,labels,label,series,index,tail,…}`・**運用 `/flush`・`/ingester/shutdown`・`/config`・`/services`・ring 系**・**削除 API `/loki/api/v1/delete`**・ルーラー `/loki/api/v1/rules`・`/metrics` | **兼ねる**（1 つの口に全部ある） |
| Loki | gRPC 9095 | 内部（query-frontend ↔ querier） | 内部 |
| Tempo | HTTP 3200 | 読み取り `/api/{traces,search,…}`・**運用 `/flush`・`/shutdown`・`/status/*`・ring 系**・`/api/overrides`・`/metrics` | **兼ねる** |
| Tempo | OTLP 4317（gRPC）・4318（HTTP） | トレースの書き込み（distributor の受け口） | 管理用の口ではない（書き込みの口） |
| Tempo | gRPC 9095 | 内部（query-frontend ↔ querier） | 内部 |

いずれも既定は全インターフェイスで待つ（実測: コンテナ内の `/proc/net/tcp`）。memberlist は単一バイナリの既定構成では開かない（同）。

### 届く者（書き込み・読み取り）

| 誰が | どの口へ | 経路 | 定義の場所 |
| --- | --- | --- | --- |
| otel-collector（`app: otel-collector`） | Loki `POST /loki/api/v1/push` | A・B | A: `deploy/otel-collector-config.yaml` ／ B: `deploy/local/observability/otel-collector-forward.yaml`（既定の `deploy/local/infra/otel-collector.yaml` は debug のみで届かない） |
| otel-collector | Tempo OTLP gRPC 4317 | A・B | 同上（`otlp/tempo`） |
| Vault の audit | collector の `tcplog/vault-audit`（9514）→ collector が Loki へ | B | `deploy/local/vault-persistence/vault-entrypoint.sh`。**Vault は Loki へ直接は届かない**（collector 経由） |
| Grafana（`app: grafana`） | Loki・Tempo の読み取り（datasource） | A・B | A: `deploy/grafana/provisioning/datasources/datasources.yaml` ／ B: `deploy/local/observability/grafana.yaml` の inline（**両者は `check-grafana-provisioning-parity.js` が 1 対 1 で突き合わせる**） |
| Grafana のアラート（`grafana-alerting`） | Prometheus のみ（Loki・Tempo を読むルールは無い） | A・B | `deploy/grafana/provisioning/alerting/` |
| 運用者（人） | Grafana の Explore 経由（`{job="vault-audit"}` の抽出。`docs/security/security.md`） | B | 直接 Loki を叩く手順は文書に無い |
| promtail / alloy | **配備に無い**（ログは OTLP → collector だけ） | — | — |
| `check-stack-ready.js`・`integration-stack.yml`・`cutover-rehearsal.yml` | **Loki・Tempo を読まない**（両ワークフローは `OBSERVABILITY` を立てない） | — | 走査で 0 件 |
| Vault の audit を Loki で確かめる検査 | **無い**（抽出は手作業の Explore） | — | 走査で 0 件 |
| エッジ（Traefik・Istio のエッジ） | **Loki・Tempo を出していない** | — | `deploy/local/edge*` の走査で 0 件 |
| SPA の運用ダッシュボード（SC-10） | `JAEGER_URL`（実行時 config）。既定は空で、Tempo を指す配備は無い | — | `src/platform/frontend/docker-entrypoint.sh` |
| Prometheus | Loki・Tempo を scrape しない（唯一の scrape 対象は collector:8888） | — | `deploy/local/observability/prometheus.yaml` |

### 到達の制限・前段の現状（変更前）

- `deploy/local/` に NetworkPolicy・AuthorizationPolicy は Loki・Tempo 向けに 1 つも無い（ADR-0133 実測 4 と同じ。走査で再確認）。
- `platform-infra` は**メッシュの外**（サイドカーの注入なし。`deploy/local/edge-istio/virtualservice-app.yaml` の注記）。メッシュは `ISTIO=1` の opt-in。
- compose は Loki 3100・Tempo 3200 / 4327 を**ホストへ公開**している（`deploy/docker-compose.yml`）。
- 本番像（`deploy/helm/`）に Loki・Tempo は無い（走査で 0 件）。

### 名前だけ現れる文書（変更しない）

`docs/data/usage-event.md`・`docs/how-to/local-development.md`・`docs/migration/cutover-discard-and-rebuild.md`・`docs/screens/SC-10_*`・
`docs/tests/*`・`docs/tech/system-architecture.md`・`perf/k6/README.md`・SC-10 の frontend・`ObservabilityExtensions.cs`・
`scripts/measure-cutover-inventory.js`（PVC 名）・`deploy/local/infra/inotify-sysctl.yaml`・`deploy/local/*-persistence`（PVC。マウント先は変えない）。
いずれも口や届き方を定めていない。

### 規則 10（この変更で新たに誤りになる自分の記述）

- `deploy/local/observability/README.md`「config は compose と同内容を inline」→ Loki・Tempo の `server` 節だけは経路 B が違う（待ち受けを loopback へ移す）。注記を足す。
- `docs/operations/operations.md` 点検の記録の loki・tempo 行「判定保留」→ 過去の回の表は書き換えず、新しい回の小見出しで付け直す。
- `docs/security/security.md` には Loki・Tempo の口の記述が無い → 節を足す。
- `deploy/local/observability/README.md`・`operations.md` の「Grafana は datasource を compose と同内容」→ 同内容のまま（ヘッダも同じ。compose では値が空）。

## 決定（詳細は IADR-0526）

1. **前段は「認証付きのリバースプロキシ」を採る**（条件 1 の 2 つ目の形）。Istio の AuthorizationPolicy（principal）は採らない ——
   `platform-infra` はメッシュの外で、メッシュ自体が opt-in（`ISTIO=1`）であり、collector と Grafana もメッシュの外にいる。
   principal を条件にするには 4 つのワークロードをメッシュへ入れる必要があり、`ISTIO=0` の経路 B では前段が消える。
2. **前段は Loki・Tempo の Pod の中のサイドカー**（Caddy。frontend の基底と同じ版・同じ digest）。
   **製品は Pod の loopback（127.0.0.1）だけで待つ**（Loki `3101`・Tempo `3201`・両方の gRPC `9095`）。
   Service の口（3100・3200）は前段が持つ。これで「前段を経由しない口」はネットワークに出ない。
3. **身元は 2 つ**: 書き込み（collector）と読み取り（Grafana）。それぞれ別のトークン（Bearer）で、**口ごとに通す道を限る**:
   - 書き込みのトークン: Loki の `POST /loki/api/v1/push` だけ。
   - 読み取りのトークン: Loki の `GET` の読み取り API（query・labels・series・index・tail 等の列挙）、Tempo の `GET` の読み取り API（echo・traces・search・tags・metrics の列挙）だけ。
   - **運用の口（`/flush`・`/config`・`/ingester/shutdown`・`/status/*`・ring 系・`/metrics`）、削除 API、ルーラー、`/api/overrides`、旧 push は、どちらのトークンでも 403**。トークンが無い・違うときは 401。
   - 運用者が運用の口を使うときは `kubectl port-forward`（k8s の認証・認可を通る）で Pod の loopback へ届く（break-glass）。
4. **到達の制限は NetworkPolicy**（条件 2。前段とは別の段）: Loki の Pod へは collector と Grafana から 3100 だけ、
   Tempo の Pod へは collector から 4317、Grafana から 3200 だけを許す。k3s は NetworkPolicy を既定で強制する。
5. **トークンは起動器が作る**（`scripts/k8s-local-up.sh`・`OBSERVABILITY=1`）: Secret `observability-gate`（`writer` / `reader`）。
   **既存の値を引き継ぎ、無ければ乱数で作る**（再実行で値を回さない。Pod の env は起動時にしか読まれない）。値は引数に載せない（#1793 の `apply_secret`）。
   dev の既定値（固定の文字列）は置かない。
6. **fail-closed**: 前段は、トークンが無い・16 進以外・32 文字未満・2 つが同じ、のいずれでも起動しない（Pod の env は Secret を必須で参照）。
   collector・Grafana は Secret を任意で参照する（無ければ空の Bearer を送り、前段が 401 で拒む。Grafana の Prometheus は使える）。
7. **Tempo の OTLP（4317）は前段を通さない**（管理用の口ではない書き込みの口）。NetworkPolicy で collector だけに絞る（1 段）。
8. **経路 A（compose）は 0 段のまま記録する**（条件 3）。compose に NetworkPolicy は無く、ホストへ口を公開している。
   Grafana の datasource は経路間で同一でなければならない（パリティの検査）ので、**ヘッダの宣言は compose にも同じ形で置き、値は env**
   （`$OBS_GATE_READER_TOKEN`。compose では未設定＝空。`auth_enabled: false` の Loki・Tempo は無視する）。
9. **Alertmanager は条件 4 の点検だけ**: web config で製品内の認証を掛けられる。配備は認証なし（初回の点検の記録のとおり）。本件では変えない。

## 実測（配備と同じ版の公式イメージ・docker の上で。2026-10-09）

**前段の判定**（Loki・Tempo を loopback 待ちにし、前段を同じネットワーク名前空間で起動。別のコンテナから `curl`）:

| 要求 | 結果 |
| --- | --- |
| Loki `GET /loki/api/v1/labels`（トークン無し／違うトークン） | 401／401 |
| 同（読み取り／書き込みのトークン） | 200／403 |
| Loki `POST /loki/api/v1/push`（書き込み／読み取り） | 204／403 |
| Loki `/config`・`/flush`・`/loki/api/v1/delete`（GET/POST）・`/loki/api/v1/rules`・`/api/prom/push`（どちらのトークンでも） | 403 |
| 道の抜け: `/loki/api/v1/query/../../../config`・`/loki/api/v1/label/..%2F..%2F..%2F..%2Fconfig/values`・`/loki/api/v1/label/x/../../../../config`・`//loki/api/v1/labels/../../../../config`（`--path-as-is`） | すべて 403 |
| 大文字の道 `/LOKI/API/V1/LABELS`（読み取り） | 404（前段は通し、Loki が知らない道として返す） |
| Loki の loopback の口（3101）・gRPC（9095）へ別のコンテナから | 接続できない（000） |
| Tempo `GET /api/echo`（読み取り／書き込み／無し） | 200／403／401 |
| Tempo `/status/config`・`/flush`・`/api/overrides`・`/api/traces/../../status/config` | 403 |
| Tempo の loopback の口（3201）へ別のコンテナから | 接続できない |

**通しの確かめ**（collector 0.102.0 → 前段 → Loki・Tempo、Grafana 11.0.0 → 前段）:

- OTLP で 1 行のログと 1 本のトレースを collector へ送り、collector の `loki` exporter（`Authorization: Bearer ${env:…}`）が前段を通って書けた（collector にエラー無し）。
- Grafana の datasource（`httpHeaderName1` ＋ `secureJsonData.httpHeaderValue1: Bearer $OBS_GATE_READER_TOKEN`）で:
  Loki の health が `Data source successfully connected.`、範囲クエリで 1 行・labels が返った。
  Tempo は traceId の検索（`/api/traces/<id>`）・`/api/search`・`/api/search/tags`・`/api/v2/search/tags`・`/api/v2/search/tag/…/values`・`/api/echo`・`/api/status/buildinfo` が 200。
  datasource の proxy 越しの `/status/config`・`/flush` は 403。
- 前段の起動の拒否: トークン無し・2 つが同じ・16 進以外（引用符と波括弧を含む値）で、いずれも exit 1（値は表示しない）。

**loopback へ移すときの落とし穴（実測で見つけた）**: Loki は query-frontend が querier へ広告する自分の住所を
インターフェイスの IP から取るため、gRPC を loopback へ移すと**クエリが返らなくなる**（querier が `172.x:9095` へ繋ぎに行って拒否される）。
`frontend.address: 127.0.0.1` を与えて解消した。Tempo は単一バイナリで worker の住所を `127.0.0.1:9095` に自動で決める（起動ログ）ので不要。

**NetworkPolicy の強制は稼働クラスタで実測していない**（k3d のクラスタをこの環境で起こせなかった。`cluster dns configmap` の待ちで打ち切り）。
宣言は `kubeconform` と描画（`kubectl kustomize`）で確かめた（下の「検証」）。k3s が NetworkPolicy を既定で強制することは点検の記録（Argo CD の行）と同じ前提である。

## 検証

- **描画した実物での通し**: `kubectl kustomize deploy/local/observability-persistence` の出力から ConfigMap（前段の `gate.sh`・2 つの Caddyfile・Loki / Tempo の config・
  collector の転送構成・Grafana の datasource）を取り出し、そのまま docker の上で起動して上の「実測」を再現した（401 / 403 の表は同じ結果。
  OTLP のログ・トレースと `tcplog` の Vault の audit の 1 行が前段を通って書け、Grafana から `{job="vault-audit"} | json | request_operation="update"` が 1 行返った）。
  描画で、永続化の patch の volumeMount が `containers/0`（製品）に付き、前段のコンテナには付かないことを確かめた。
- `node scripts/check-deploy-manifests.js`（helm v3.16.2・kubeconform v0.6.7・kubectl v1.33.4 あり）: chart 1 件 / overlay 17 件が描画でき、スキーマに適合。
- `node scripts/k8s-local-up.test.js`（302 件）・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（#1842 の陽性対照・22 の変異を含む）。
- `check-stack-ready --self-test`・`check-grafana-provisioning-parity`・`check-grafana-alerting`・`check-image-digests`・`check-trace-blocks`・
  `gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-updated`・`check-commit-messages`（PR 本文に出力）。
- k3d のクラスタ（k3s v1.35.4）はこの環境で起動できなかった（`cluster dns configmap` の待ちで打ち切り）。NetworkPolicy の強制は未実測（IADR-0526 残余 1）。

## 受け入れ基準

- [x] 経路 B の Loki・Tempo の管理用の口は、前段（認証付きのリバースプロキシ）を通らなければ届かない（製品は loopback 待ち）。
- [x] 前段は書き込み・読み取りの 2 つの身元を検証し、運用の口・削除 API は両方とも拒む（401 / 403）。
- [x] 前段とは別の段（NetworkPolicy）で、Loki・Tempo の Pod へ届く相手を collector と Grafana に限る。
- [x] collector の書き込みと Grafana の読み取り（Loki・Tempo）、Vault の audit の `{job="vault-audit"}` の取り込みの経路が壊れない（collector → Loki の push は同じ道）。
- [x] Grafana の provisioning のパリティ（compose ＝ k8s）を保つ。
- [x] 前段はトークンが欠けると起動しない（fail-closed）。トークンは乱数で作り、再実行で引き継ぐ。値を引数に載せない。
- [x] 経路 A（compose）の段数（0 段）と Alertmanager の現状（条件 4）を記録する。
- [x] 判断を IADR-0526 に、統制と現在の実現手段をセキュリティ仕様書・運用仕様書（点検の記録）に残す。
- [x] 外すと CI が落ちる静的検査を置く（`scripts.repo.test.js`）。

## 計画への環流の要否

- **要らない**（裁定待ちの選択は無い）。ADR-0133 決定 3 の「現在の実現手段: 無い」は、本件で経路 B について「ある（2 段）」になった。
  計画の側の更新は ADR-0133 フォローアップ 3（計画が `.claude/rules/adr.md` 例外 4 で行う）であり、実装からは本 PR と IADR-0526 が根拠になる。
  issue の起票はしない（指示どおり、他所へのコメントもしない）。

## 必須チェックへの影響

- 必須 check の名前・起動条件は変えない。`static-checks-units` の `scripts.repo.test.js` に検査が増える。
- IADR の連番の検査は、IADR-0522〜0525（未マージの他 PR）が入るまで欠番で赤になり得る（PR 本文に記す）。
