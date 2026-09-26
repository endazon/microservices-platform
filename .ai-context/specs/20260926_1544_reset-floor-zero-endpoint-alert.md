---
title: 作業仕様書 — リセット申請の床の器に ready な endpoint が 0 になったことを通知の配線で知らせる（#1544・計画 ADR-0111 フォローアップ 3）
type: spec
status: in-progress
related_ids: [SC-15, NFR-05, NFR-21, ADR-0006, ADR-0076, ADR-0078, ADR-0097, ADR-0111, IADR-0304, IADR-0370, IADR-0421, IADR-0432]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0111_reset-floor-replicas-and-no-bypass-on-failure.md (Accepted 2026-09-26・フォローアップ 3)
  - planning:projects/microservices-platform/07_adr/ADR-0097_timing-floor-release-default-on-and-periodic-review.md (決定 2・例外 3 の訂正)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-21・NFR-05)
related_specs: [20260926_1543_reset-floor-replicas-pdb.md, 20260906_issue-1245_nearby-mta-relay.md, 20260904_issue-1202_absent-series-slo-alerts.md]
issue: "#1544"
---

# 作業仕様書 — リセット申請の床の器に ready な endpoint が 0 になったことを通知の配線で知らせる

## 起点

- issue: #1544（計画 ADR-0111 フォローアップ 3「器が落ちたこと（ready な endpoint が 0）の検知は NFR-21 の通知の配線の射程で扱う。
  本 ADR では検知の手段を定めない」）。
- 計画 ADR-0111 決定 2（予備の経路なし）・決定 3（全滅時の 503 は「申請を閉じた状態」。本番で `RESET_FLOOR=0` を退路にしない）・
  決定 4（「器が落ちたことを検知して知らせる手段は無い」）。NFR-21（検出 5 分以内。充足は「評価対象があること」まで含めて判断する）。
- 実装判断の記録先: **新しい IADR は起こさず、IADR-0432 へ日付つき追記**（床の器の IADR。#1500 / #1543 の先例と同じ）。

## 現状（着手時に確かめた。2026-09-26・`origin/develop` = 9d1b6b3b）

| # | 事実 | 確かめた場所 |
| --- | --- | --- |
| 1 | Prometheus の scrape 対象は otel-collector:8888 だけ（「唯一の scrape 対象」は #546 / #1090 の不変条件） | `deploy/local/observability/prometheus.yaml`・`deploy/prometheus.yml` |
| 2 | kube-state-metrics・blackbox exporter は配備されていない（`kube_endpoint_*` / `kube_deployment_status_replicas_available` 相当の系列は無い） | `git grep -n -i "kube-state-metrics\|blackbox" -- deploy` の当たりは `ServiceRequestMetricsAbsent` の description の「要 blackbox 補完」3 件だけ（配備の宣言は 0 件） |
| 3 | Istio / Envoy の統計も収集していない | `istio_requests_total` / `envoy_cluster` が deploy/ に 0 件 |
| 4 | 床の器は `/metrics` を持たない（全要求を Keycloak へ逆プロキシする） | `deploy/mail-relay/reset-floor.js` |
| 5 | **先例**: 近接 MTA のキューは、サイドカー exporter（:9154）を **otel-collector の prometheus receiver** が取り、remote write で入る（job ラベル＝ receiver の `job_name`） | `deploy/local/infra/otel-collector.yaml`・`deploy/local/observability/otel-collector-forward.yaml`・IADR-0421 |
| 6 | platform-infra は Istio 注入の対象外（`istio-injection` を付けるのは MSP の namespace だけ）。集合側から器の Service へは kube-proxy がそのまま振り分ける | `scripts/k8s-local-up.sh` 425 行 |
| 7 | 器の Service（`reset-floor:8080`）に NetworkPolicy は無い | `deploy/mail-relay/reset-floor/reset-floor.yaml` |
| 8 | エッジの route は `POST ^/realms/[^/]+/login-actions/reset-credentials.*` だけを器へ向ける（器の他のパスはクラスタ外から届かない） | `deploy/local/edge-istio-reset-floor/kustomization.yaml` |

## 設計判断

### 信号: 「クラスタ内から器の Service に器自身の口が答えるか」を otel-collector の prometheus receiver で取り、`up` で判定する（issue の案 (b)）

器に**器自身が答える最小の `GET /metrics`**（`reset_floor_up 1`）を足し、otel-collector の prometheus receiver が
**Service 名 `reset-floor:8080`** を scrape する。receiver が出す `up{job="reset-floor"}` は:

- **ready な endpoint が 1 つ以上** → kube-proxy がその 1 つへ振り分け、器が 200 を返す → `up = 1`。
- **ready な endpoint が 0** → Service に振り分け先が無く接続が拒否される → `up = 0`。
  Envoy（エッジ）が 503 を返す条件（EndpointSlice の ready が 0）と**同じ情報源**を見ている。

採らなかった案（issue の (a)(c) と、依頼の挙げた blackbox）:

| 案 | 採らない理由 |
| --- | --- |
| (a) kube-state-metrics を足す | イメージと RBAC（クラスタ全体の list/watch）が増え、供給網の統制（08_data-egress-policy）の対象が 1 つ増える。Prometheus の直 scrape を足すと #546 / #1090 の不変条件が崩れ、collector 経由にしても得るのは本件 1 系列だけ。**最も侵襲的** |
| (c) エッジ（Envoy）の 503 を拾う | Istio の ingress gateway の統計を新たに scrape する配線が要る。**申請が来ないと 503 も出ない**（頻度の低い経路なので全滅していても無風なら鳴らない）。能動的に確かめる形にならない |
| blackbox exporter（probe） | 新しいイメージが要る（IADR-0421 決定 1 が kumina 系を退けたのと同じ理由）。やることは本案の receiver の scrape と同じ |
| OTel の `httpcheck` receiver | 版（0.102.0）での系列名・ラベル・失敗時の出し方を稼働 Prometheus で確かめられない（#1110 の「推測で名前を書かない」）。`up` は既存の `OtelCollectorDown` が同じ形で稼働実績を持つ |
| 器の Pod を個別に scrape（Pod IP の service discovery） | receiver に Kubernetes SD と RBAC が要る。しかも見たいのは Pod の生死ではなく **Service に振り分け先があるか**（申請が 503 になる条件そのもの） |

🔴 **器の変更は最小に留める**: `GET` かつ パスが `/metrics` ちょうどのときだけ、**上流へ渡さず・床を掛けずに**器自身が答える。
それ以外（リセット申請の POST を含む）は従来どおりバイト列を変えずに中継する。エッジの route は POST の申請パスだけを
器へ向ける（現状 8）ので、この口はクラスタ外から届かない。値は `reset_floor_up 1` の 1 系列だけで、機密も存在の有無も含まない。

🔴 **`Connection: close` を返す**: Prometheus（receiver）の scrape は既定で接続を使い回す。使い回された接続は、器が
NotReady になって Service から外れた後も**古い Pod へ繋がったまま**になり得る（kube-proxy は新しい接続にだけ効く）。
毎回閉じれば、**毎回の scrape が現在の ready な endpoint の集合を通る**。

### 規則: 主 1 件 ＋ 評価対象の不在 1 件（`OtelCollectorDown` ＋ `OtelCollectorUpSeriesAbsent` と同じ対）

| 規則 | 群 | 式 | for | severity | 理由 |
| --- | --- | --- | --- | --- | --- |
| `ResetFloorNoReadyEndpoint` | `reset-floor-availability`（新設） | `up{job="reset-floor"} == 0` | 2m | critical | 全滅はリセット申請が全件 503 になる**利用者に見える停止**である（`OtelCollectorDown` と同じ重さと待ち）。検出の見積りは下記 |
| `ResetFloorUpSeriesAbsent` | `platform-slo-evaluation-target`（既存） | `absent(up{job="reset-floor"})` | 5m | warning | receiver を落とすと `up` の系列ごと消え、主の規則は**空ベクタになって発火しない**（#1110 の形）。`up` は scrape ごとに必ず出る（失敗時も 0 として出る）ので、`MailRelayQueueSeriesAbsent` と同じく**常時トラフィックが要らない**対象であり、ADR-0076 決定 3 の判定基準（無風が 5 分より短い）を満たす |

- **検出の見積り（評価の側）**: 全滅 → 次の scrape（30 秒以内）で `up = 0` → remote write（batch 5 秒）→ 評価（15 秒間隔）→ `for: 2m`。
  **およそ 3 分**で firing（NFR-21 の 5 分以内）。Pod のプロセスが固まって readiness が落ちるまでの時間（tcpSocket・5 秒 × 既定 3 回）を足しても 5 分に収まる。
- **陰性対照**: 器が 1 つでも ready なら Service はその 1 つへ振り分けるので `up = 1`（2 レプリカのうち 1 つが落ちても鳴らない）。
  更新は既定の RollingUpdate（maxUnavailable 0）で ready が 0 にならない。単発の scrape 失敗は `for: 2m`（4 周期）が吸う。
- **宛先**: 既存の Alertmanager（`alertmanager:9093`・既定の受信先 `default-null`）と、暫定の Grafana 統合アラート（宛先なし）。
  **外部の通知先は足さない**（IADR-0304 決定 2）。
- **置く場所**: compose の `deploy/prometheus/alerts.yml` と経路 B の inline（`check-prometheus-alerts-parity.js` が 1 対 1 を見る）、
  Grafana の provisioning 2 か所（`check-grafana-alerting.js` / `check-grafana-provisioning-parity.js`）。
  compose スタックには床の器が居ないので、compose で両規則は `absent` 側が鳴り続ける —— **これは近接 MTA の
  `MailRelayQueueSeriesAbsent` と同じ既知の状態**であり（compose の collector 設定は mail-relay も持たない）、
  両経路で同じ規則集合を持つ門（パリティ）を崩さないために受容する。
- **Grafana 版の閾値**: `expr: 'up{job="reset-floor"}'`・`evaluator: { type: lt, params: [1] }`（値 0 で真）。
  🔴 **`up == 0` を expr に書いて `gt 0` で比べる形は採らない** —— フィルタ後の値は 0 なので `0 > 0` は偽になり**永久に発火しない**。
  （既存の Grafana 版 `OtelCollectorDown` がこの形である。本作業の射程外なので直さず、報告に残す。）
  `noDataState: OK`（系列の不在は `ResetFloorUpSeriesAbsent` が拾う。同じ不在で 2 通鳴らさない）。

### collector: 経路 B の 2 つの設定に同じ receiver を置き、compose には置かない

- `deploy/local/infra/otel-collector.yaml`（既定・debug のみ）と `deploy/local/observability/otel-collector-forward.yaml`（転送）の
  **両方**に `prometheus/reset-floor`（`job_name: reset-floor`・`scrape_interval: 30s`・`metrics_path: /metrics`・`targets: ['reset-floor:8080']`）を置き、
  metrics パイプラインの receivers へ足す（片方だけだと opt-in の apply で観測が消える。#1090 の形）。
- compose（`deploy/otel-collector-config.yaml`）には置かない。器が居ないので宛先の無い scrape が恒常的に失敗し続ける。
  同ファイル冒頭の「乖離は意図」の注記へ床の器を足す。

## 母集合（着手時に引き直した）

`git grep`（パス除外: `src/ai-stock-trading` / `CHANGELOG.md`。拡張子で絞らない）。

| 軸 | 検索 | 拾ったもの |
| --- | --- | --- |
| 1 誤りになる言明（「知らせる手段は無い」） | `全滅\|知らせる手段\|鳴らす計器はまだ無い\|ready な endpoint\|#1544\|1544` | `docs/operations/keycloak-smtp-relay-setup-runbook.md` 387 / `docs/screens/SC-15_password-reset.md` 328 / `docs/tests/SC-15_password-reset.md` 126〜127 / `deploy/mail-relay/reset-floor/reset-floor.yaml` 30 / IADR-0432 265・336 / 仕様書 #1500・#1543 |
| 2 規則の件数（導出値） | `17 ?(件\|ルール)\|= \*\*17\|16 → 17\|platform-slo-evaluation-target 5` | `deploy/grafana/provisioning/alerting/slo-alerts.yaml` 24・27〜29 / `deploy/local/observability/grafana.yaml` 471・474〜476 / `docs/operations/operations.md` 654・693・712・721 / `scripts/check-grafana-alerting.js` 12〜17 / IADR-0466 118 |
| 3 規則を列挙する表 | `MailRelayQueueSeriesAbsent\|OtelCollectorUpSeriesAbsent` in docs | `docs/operations/operations.md` §監視・アラートの表 / `docs/operations/password-reset-relay-state-measurement-runbook.md`（近接 MTA の 3 件だけを扱う） |
| 4 collector 設定の同型の宣言 | `prometheus/mail-relay` | `deploy/local/infra/otel-collector.yaml` / `deploy/local/observability/otel-collector-forward.yaml` / `deploy/otel-collector-config.yaml`（持たない旨の注記） |
| 5 「器は `/metrics` を持たない」 | `/metrics を持たない\|/metrics` ∩ 床 | issue 本文・仕様書 #1543 77 行（凍結） |
| 6 障害対応の表 | `## 障害対応（Runbook）` | `docs/operations/operations.md` の表（床の行が無い） |

**直すもの**: 軸 1 の live な文書 4 つ（runbook / SC-15 画面 / SC-15 テスト / マニフェストのコメント）、軸 2 の件数（Grafana 2 か所・
operations.md 4 か所・`check-grafana-alerting.js` の注記）、軸 3 の表へ 2 行、軸 4 の 3 ファイル、軸 6 の表へ 1 行、IADR-0432 は日付つき追記。

**除外したものと理由**

- IADR-0432 265・336 行（本文と #1543 追記）: 凍結記録。新しい日付つき追記で「実装した」を記録する。
- IADR-0466 118 行（「16 → 17 件」）: 当時の記録。現行値の主張ではない。
- `.ai-context/specs/` の確定済み仕様書（#1500・#1543）: 凍結記録。
- `docs/operations/password-reset-relay-state-measurement-runbook.md`: 近接 MTA の 3 規則の実測手順であり、床の器は射程外。
- `scripts/check-grafana-alerting.js` は件数を注記に持つだけで、検査は alerts.yml から数えるので値は動かない。**注記だけ直す**
  （#1550 / #1551 の射程〔稼働クラスタへ当たる scripts・AST の CI〕とは交差しない）。
- `.github/workflows/`: `reset-floor.test.js` は既に `ci.yml` が走らせており、起動条件も変えない。触らない。

## 受け入れ基準

1. 器が `GET /metrics` に**上流へ渡さず・床を待たずに** 200・`text/plain; version=0.0.4`・`reset_floor_up 1`・`Connection: close` で答える。
   `/metrics` 以外（申請の POST を含む）と `GET` 以外は従来どおり上流へ中継する。
2. 経路 B の 2 つの collector 設定が `prometheus/reset-floor` を持ち、scrape 先は器の Service の名前とポート（マニフェストが単一情報源）、
   `metrics_path` は器が答えるパス、metrics パイプラインの receivers に入っている。compose の設定は持たない（注記がある）。
3. `ResetFloorNoReadyEndpoint`（`up{job="reset-floor"} == 0`・2m・critical）と `ResetFloorUpSeriesAbsent`（`absent(up{job="reset-floor"})`・5m・warning）が
   compose・経路 B の Prometheus と Grafana 2 か所にあり、job ラベルは receiver の `job_name` と一致する。Grafana 版は `lt 1` で比べる。
4. `node scripts/reset-floor.test.js` が 1〜3 を固定し、変異（receiver を pipeline から外す・job 名を変える・ポートを変える・パスを変える・
   規則の job を変える・不在の規則を消す・器が `/metrics` を上流へ渡す）で落ちる。
5. `check-prometheus-alerts-parity.js` / `check-grafana-alerting.js` / `check-grafana-provisioning-parity.js` / `check-collector-self-telemetry.js` と
   `scripts.test.js` / `scripts.repo.test.js`・文書の検査（trace ブロック・更新日・知識グラフ）が通る。
6. 運用仕様書の監視の表に 2 行、障害対応の表に 1 行があり、runbook / SC-15 画面・テスト仕様書の「知らせる手段は無い」が改まっている
   （外部の通知先は増えていない ——「Alertmanager までは届く。その先は未配線」は変わらない）。
7. IADR-0432 に `［2026-09-26 追記 / #1544］` がある。

## 確かめられないこと（稼働クラスタに触れない）

- 🔴 **receiver が scrape 失敗時に `up = 0` を remote write で Prometheus へ届けることは、本作業では稼働環境で確かめていない。**
  同じ collector（`otel/opentelemetry-collector-contrib:0.102.0`）の同じ receiver が出す `up` を、既存の規則は `job="otel-collector"`（Prometheus の
  直 scrape）でしか使っていない。確かめる手順（陽性対照を対で置く。`alerts.yml` 冒頭の #1110 の手順）をテスト仕様書の手動項目に置く。
- 器を 0 へ絞って `ResetFloorNoReadyEndpoint` が firing になるまでの時間と、1 つ戻して解消する時間（テスト仕様書の手動項目）。
