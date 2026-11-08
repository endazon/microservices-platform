---
title: 作業仕様書 — クラウドの外向きの中継の許可表を送り元ごとにし、API サーバーへの出の番地を名指し、OTLP の送り手の名乗りを送り元の Pod で確かめる
type: spec
status: in-progress
related_ids: [NFR-18, NFR-21, IADR-0709, IADR-0705, IADR-0697, IADR-0689]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - 20261107_cloud-egress-audit-cert-expiry.md
  - 20261107_cloud-infra-pod-hardening.md
---

# 作業仕様書 — 外向きの中継の送り元ごとの表・API サーバーの番地・OTLP の送り手の名乗り

> 着手 2026-11-08（develop `3ba6611f1`）。判断の記録は IADR-0709。取引ユニット（AST）の外向きを中継へ通す作業は AST の側（AST#1607）が並行で行う。

## 起点

クラウドの最小構成（`deploy/cloud`）の硬化。3 つを扱う。

1. 外向きの中継の許可表が送り元を問わない 1 枚で、ある送り元が別の送り元のための宛先（Discord・LLM の提供元・オブジェクトストレージの共有の名前）へも出られる。クラウドで使わない宛先（埋め込みの提供元）も載っている。
2. ESO と OpenBao の API サーバー向けの出が、宛先を問わない 443・6443 で書かれている。
3. collector の受け口に届く送り手が、自分で名乗る `service.name`（＝`job`）で系列を書くので、警報の元の系列（LLM ゲートウェイの費用・再起動、送り手の数）を他の送り手の名で書ける。

## 方式

- 送り元の名乗り＝中継の受け口のポート。各受け口へ届く Pod は中継の NetworkPolicy が「名前空間 かつ `app`」で 1 つに絞る（決定 1）。
- 表は `<送り元> <名前>[:<ポート>]` の行。`*.<名前>` は 1 段下だけ（Discord の Bot の再接続先）。バックアップの保管先は、仮想ホスト形式なら `<バケット>.<名前>` だけ（決定 2）。
- 送り元ごとの段は既定 report（他の送り元の行の宛先も通して数える）→ enforce（決定 3）。従来の口 3128 は report の間だけ全体の表で通す。
- API サーバーの番地は k3s の起動の旗から決まる（`10.43.0.1:443`・`127.0.0.1:6443`）。宛先を問わない出は段 1 の別の宣言に分け、読むだけの確かめの手順の後に外す（決定 4）。
- collector は接続の送り元の Pod から名前空間と `app` を引き（名前空間ごとの読みだけの Role）、各点へ印を写す。LLM ゲートウェイの警報と送り手の数を印で見る。系列は落とさない（決定 5）。

## 母集合（規則 9・10）

- 中継のポート 3128 を引く箇所: `git grep -n -E '3128|egress-proxy'`（作業前）。`backup/cronjob-postgres.yaml`・`backup/networkpolicy.yaml`・`infra/networkpolicy.yaml`・
  `observability/alertmanager.yaml`・`observability/networkpolicy.yaml`・`values-cloud.yaml`・`scripts/cloud-ops.test.js`・`scripts/deploy-cloud-egress-audit.test.js`・
  `deploy/cloud-transition/egress-proxy-report`（3128 は従来の口として残すので重ねは変えない）。
- ポートだけの出: `deploy-cloud-egress-audit.test.js` の `portsOnlyEgress` が全描画を走査（ESO・OpenBao の 2 つだけ）。
- LLM ゲートウェイの系列を使う警報: `git grep -n -E 'llm_cost_total|llm_budget_monthly_limit|llm_gateway_start_time_seconds|llm_call_limit_exceeded_total' deploy/cloud`
  → `observability/prometheus.yaml` の 3 つの警報。ローカル・Grafana の規則はクラウドの collector を通らないので対象外。
- `OtlpSendersMissing`: `deploy-cloud.test.js`（閾値・注釈）・`cloud-ops.test.js`（promtool）。
- collector の設定の 2 か所（`infra/otel-collector.yaml`・`observability/patches/otel-collector.yaml`）は T-2402-51 が同じであることを突き合わせる。両方を直す。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | 受け口の送り元の行に在る宛先だけ allow。他の送り元の行の宛先は report で通して数え、enforce で 403。legacy は enforce で何も通さない。`*.` は 1 段下だけ | `proxy.test.js` E8・E9 |
| AC2 | 送り元と受け口（env）・コンテナと Service のポート・NetworkPolicy の入り（受け口ごとに名前空間 かつ Pod が 1 つ）が同じ組。3 つの MSP の Pod は自分の受け口を使う | egress audit E2・E3・E4 |
| AC3 | 表は LLM の提供元を llmgateway だけ、Discord を alertmanager（と AST の通知）だけに持ち、埋め込みの提供元を持たない。`*.` の行は Discord の Bot の再接続先だけ | E4 |
| AC4 | 仮想ホスト形式のとき、保管先の行は `<バケット>.<名前>` だけ。バケットが読めなければ何も足さない | E9 |
| AC5 | 送り元ごとの段が report のまま 14 日（か日時が無い）で `EgressProxyReportMode` が鳴る。`EgressProxySourceMismatch` がある | E6（promtool） |
| AC6 | ESO・OpenBao・collector の API サーバーへの出は 2 つの番地を名指し、宛先を問わない出は段 1 の 2 つの宣言だけ。番地は README の k3s の旗・確かめの手順の値と同じ。確かめの手順は一致で 0・違いで 1・宛先が違えば 4 | E1・A1・`cluster-target.test.sh` |
| AC7 | collector の metrics の pipeline が「消す→名前空間ごとに引く→写す→batch」の順、接続から引く、Role は Pod の読みだけ・ClusterRole なし。LLM の 3 つの警報は印で絞り、送り手の数は印で数え、`OtlpSenderUnverified` がある | O1・`cloud-ops.test.js`（promtool。他の送り手の系列で鳴らない） |
| AC8 | 陰性対照: 上の各直しを外すと赤 | 各試験の中の陰性対照 |

## 確かめられないこと

- collector の設定（OTTL・k8sattributes）を実物の collector で読ませていない（手元に collector の実行物が無く、取得も許されなかった）。初回は `OtelCollectorDown`・`OtlpSenderUnverified` で見る。
- API サーバーの番地が実際に k3s のクラスタで当たるか（NetworkPolicy が書き換えの前と後のどちらで当たるか）は、稼働のクラスタで確かめの手順を流すまで分からない（段 1 を残す理由）。
