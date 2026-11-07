---
title: 作業仕様書 — クラウドの LLM ゲートウェイの当日の費用の上限・月次予算の警報と、k3s の導入の版の固定と照合
type: spec
status: done
related_ids: [NFR-18, NFR-21, FR-11, ADR-0044, IADR-0466, IADR-0674, IADR-0678, IADR-0679, IADR-0689, IADR-0692]
author: claude
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 作業仕様書 — クラウドの LLM ゲートウェイの当日の費用の上限・月次予算の警報と、k3s の導入の版の固定と照合

> 本仕様書は、設定と手順を変える前に作成した（着手 2026-11-07。develop `7d1bb15dc`）。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR-18（通信と配備の保護）・NFR-21（費用と運用の観測）。機能要求: FR-11（LLM 費用の計測）。計画 ADR-0044。
- 画面（SC）・ユースケース（UC）: なし。
- 関連 IADR: IADR-0678（送る前の上限）、IADR-0466（月次予算のゲージと上限アラート）、IADR-0689 決定 12（クラウドの段 2）、
  IADR-0674（クラウドの最小構成）、IADR-0679（外部の成果物の sha256 の固定）。本作業の決定は IADR-0692。
- 所有者の裁定（2026-11-07）: 稼働の前に中程度の指摘もすべて直す。LLM の当日の費用の上限は 1 日 20 USD。

## 目的・背景

1. クラウドの LLM ゲートウェイは段 2（Enforce）だが、当日の費用の上限の金額が 1 つも無く、費用の判定が働いていなかった
   （呼び手に金額が無いと `HasCostLimit` が偽で、判定そのものを飛ばす）。月次予算の金額も未設定で、上限アラートも評価対象を持たなかった。
2. クラウドのノードへの k3s の導入が、版を固定せず、取得したスクリプトを照合せずに root で実行する形だった。

## 現状の確認（変更の前に実測）

- Enforce で金額を置かずに起動すると: 起動は成功し、要求数と入力の上限だけが働く（費用は無制限）。`LlmCallLimitOptionsValidator` は
  0 以下の金額だけを落とし、未設定は通していた。→ 黙って無制限になるので直す（決定 3）。
- 設定の形（`LlmCallLimitOptions`）: 全体（ゲートウェイ全体）の上限は無い。呼び手ごと（`Callers:<呼び手>:DailyCostLimit`）と
  呼び手 × 用途（`PurposeDailyCostLimits`）だけ。呼び手の項目に無い値は `Default` を使う。通貨は単価表の通貨（USD）。
  日の区切りは UTC の 0 時で、判定器に固定（設定に無い）。状態はプロセスごと。
- クラウドで LLM ゲートウェイを呼べるのは取引ユニットの呼び手だけ（ナレッジのサービスは描かない）。許可表（CallerPolicy、Enforce）は
  表に無い呼び手を上限の判定より前に拒む。
- 月次予算（`Llm:Budget:MonthlyLimits`）は用途ごとだけ（総額 1 本は計画が禁じる）。取引ユニットの用途のうち
  `stage0-recording`・`policy-revision`・`report-regeneration` は単価表の用途（`Routing:PurposeModels`）に無く `other` に丸まり、
  月次予算に置けない（検証器が拒む）。
- クラウドの Prometheus は保持 7 日・emptyDir で、直近 30 日の窓は常に過小になる。メトリクスは collector の remote write で届く。
- k3s: 公式の安定の版は v1.37.2+k3s1、試験の k3d の k3s は v1.35.4（`scripts/k8s-local-up.sh` の `K3S_IMAGE`）。v1.35 の最新の patch は v1.35.10+k3s1。

## 決定（詳細は IADR-0692）

- 当日の費用の上限（クラウドだけ）: 取引ユニットの呼び手 18 USD、既定 0.5 USD（ナレッジの 4 つの呼び手のそれぞれ）。合計 20 USD。
- 月次予算: 呼び手の日の上限を用途へ割り当て、割り当て × 30 日 × 0.5 を `appsettings.json` に置く（全経路）。
- クラウドの警報: `LlmMonthlyBudgetPaceExceeded`（直近 7 日の費用 ＞ 月次予算 × 7/30）。
- Enforce で `Default:DailyCostLimit` が無ければ起動で止める。
- k3s: v1.35.10+k3s1 のリリースのバイナリと同じ版のタグの `install.sh` を、`pinned_tool_entry` の表の sha256 で照合して入れる。

## sha256 の出どころ（自分で確かめた）

| 取得物 | URL | sha256 | 確かめ方 |
| --- | --- | --- | --- |
| k3s（amd64） | `https://github.com/k3s-io/k3s/releases/download/v1.35.10%2Bk3s1/k3s` | `5cd49f0faca5b2ea4355d4761e7b1445b8aa05cd50e612fd9c370551db49e5e2` | 2 回取って一致。同じリリースの `sha256sum-amd64.txt`（2 回取って一致）の `k3s` の行と一致。実行して `k3s version v1.35.10+k3s1` |
| install.sh | `https://raw.githubusercontent.com/k3s-io/k3s/v1.35.10%2Bk3s1/install.sh` | `8598e002e61d658fed7b7542fc6d2c66d8da6eae69e088830105d2ee1ffb6d91` | 公式の checksum は無い。2 回取って一致。`sh -n` で構文を確かめ、`INSTALL_K3S_SKIP_DOWNLOAD` を持つことを読んだ |

## 是正の母集合（規則 9・10）

### k3s の導入（取得物をシェルへ流す形）

- 引き方: 追跡下の全ファイル（submodule・`CHANGELOG.md` を除く）を `(curl|wget)[^|]*\| *(sudo )?(sh|bash)` と、
  `get.k3s.io`・`INSTALL_K3S` で走査した。
- 結果: 稼働の手順は `deploy/cloud/README.md` §1-4 の 1 か所だけ。ほかの当たりは `.ai-context/` の凍結記録（実測の記述・k3d の過去の作業仕様書）で、
  手順ではないので除外した。
- 再発の検査: `scripts/deploy-cloud.test.js` が `deploy/cloud/` の全ファイルと `docs/operations/cloud-*` を走査する。

### 月次予算の金額の設定で誤りになる記述（「未設定」「不活性」「自動検知は働いていない」）

- 引き方: `金額が未設定|金額は未設定|未設定のあいだ|不活性|自動検知はまだ働いていない|llm-cost-monthly-review-runbook|金額の既定は無い|金額は定めない`
  を追跡下の全ファイル（submodule・`CHANGELOG.md`・`.ai-context/specs/`・`.ai-context/superpowers/` を除く）で走査した。
- 直したもの: `docs/operations/llm-cost-monthly-review-runbook.md`（superseded）、`docs/operations/operations.md`（4 か所）、
  `docs/observability/llm-usage-and-cost-metrics.md`、`docs/operations/llm-output-token-measurement-runbook.md`、
  `deploy/prometheus/alerts.yml`、`deploy/local/observability/prometheus.yaml`、`deploy/grafana/provisioning/alerting/slo-alerts.yaml`、
  `deploy/local/observability/grafana.yaml`（規則の前書きとダッシュボードの説明）、`deploy/grafana/provisioning/dashboards/llm-usage.json`、
  `deploy/helm/microservices-platform/values.yaml`、`LlmBudgetOptions.cs`・`Program.cs` のコメント。いずれも日付つきの追記で、過去の記述は消していない。
- 除外したもの（理由）:
  - `.ai-context/adr/` の IADR-0304・IADR-0322・IADR-0466 本文: 凍結記録。IADR-0466 には日付つきの追記を足した。
  - 「不活性」の当たりのうち、LLM の費用と無関係なもの（IADR-0348・IADR-0428・`values-local.yaml` の edge・moomoo の seed）。
  - `LlmBudgetMetrics.cs`・`LlmBudgetMetricsTests.cs` のコメント: 「未設定なら系列を出さない」はゲージの振る舞いの説明で、今も正しい。
  - `scripts/scripts.repo.test.js` の #1111 の節のコメント: 検査の向き（併存・統制ゼロ）の説明で、今も正しい。
  - `LlmCallLimitOptionsValidatorTests` の「金額の既定は置かない」: クラスの既定は今も空。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | クラウドの描画で、当日の費用の上限が合計 20 USD（取引ユニット 18・既定 0.5）、1 レプリカ・HPA なし | `deploy-cloud.test.js`（陰性対照つき） |
| AC2 | 既定とローカルの描画に `Llm__CallLimits__*` が無い | 同上 |
| AC3 | Enforce で `Default:DailyCostLimit` が無い・0 なら起動で止まる | LlmGateway の単体試験・起動の試験 |
| AC4 | 月次予算は割り当てから導いた値で、割り当ての合計は呼び手の日の上限と等しい | `deploy-cloud.test.js` |
| AC5 | クラウドの Prometheus に進みの警報があり、promtool の単体試験で按分の超過だけが鳴る | `cloud-ops.test.js` |
| AC6 | 月次確認の Runbook が superseded で、金額の設定と食い違わない | `scripts.test.js`（#1111 の節） |
| AC7 | k3s の導入が版の固定と照合の後で、起動の旗は変わらず、取得物をシェルへ流す形が無い | `deploy-cloud.test.js`（陰性対照つき） |

## 残余リスク

- IADR-0692 §残余リスク を正とする（再起動で当日の枠が 0 に戻る・プロセスごとの状態・`other` の用途は月次予算の外・提供元の側の支出上限は リポジトリの外）。
