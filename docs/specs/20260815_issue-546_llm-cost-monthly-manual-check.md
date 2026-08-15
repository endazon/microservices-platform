---
title: 作業仕様書 — LLM 費用の月次手動確認手順（案 A）の文書化（#546・母集合の引き直し）
type: spec
status: done
related_ids:
  - NFR
  - SC-10
  - FR-11
  - ADR-0006
  - ADR-0044
  - IADR-0110
  - IADR-0164
  - IADR-0168
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
---

# 作業仕様書: LLM 費用の月次手動確認手順（#546 / 案 A）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-11**（LLM ゲートウェイ）
- ユースケース（UC）: 該当なし（運用手順であり、利用者操作のフローではない）
- 画面（SC）: **SC-10**（運用ダッシュボード。**費用の KPI カードは持たない**。Grafana 導線カードのみ）
- 非機能要件（NFR）: **運用・保守**（無採番。理由は後述「NFR の採番について」）
- 関連 ADR: **ADR-0006**（可観測性。アラートは Alertmanager を用いる）／
  **ADR-0044**（LLM 利用実績の計測粒度と単価表。ADR-0006 の部分改定）
- 関連 実装 ADR: [[IADR-0110]]（メトリクスの設計と限界）／[[IADR-0164]]（本件の暫定統制の決定）／
  [[IADR-0168]]（Grafana provisioning のパリティ。stg/prod に Grafana リソースが無いこと）
- 計画書リンク: [`06_technical/05_observability-ops.md`](../../planning/projects/microservices-platform/06_technical/05_observability-ops.md)
  **§LLM 費用の上限アラートと暫定の統制**（2026-08-08 確定。決定 39〜41）／
  [`05_screens/01_screens.md`](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-10
- 起点 issue: **#546**（planning#286 で裁定依頼 → planning `b8002cc` の**決定 39〜42**。**採られたのは案 A**）

## 目的・背景

2026-08-05 の裁定 Q26 で SC-10 から LLM コストの KPI カードを外した結果、**予算超過に気づく手段が
無くなった**。Alertmanager が未配備で上限アラートも飛ばないためである。2026-08-08 の裁定で
**案 A（月次の手動確認を運用手順へ置く）**が採られた。本作業はその**文書化のみ**を行う。

**Alertmanager の配備そのものは行わない。配備時期も書かない**（実環境の判断であり、#546 が追跡を続ける）。

## 対象範囲

- **対象**: `docs/operations/` 配下の月次手動確認手順（Runbook）と、本作業仕様書。
- **対象外**:
  - **Alertmanager の配備**および**配備時期の記述**（実環境の判断。`deploy/` 配下を一切触らない）
  - **SC-10 への費用表示の復活**（裁定 Q26 と衝突。同じ指標の定義が 2 か所に生まれる）
  - **絶対額のしきい値の設定**（決定 41。月次予算の金額は実測後に確定する）
  - **記録簿（`llm-cost-monthly-review-log.md`）の新設**（空のログは「運用している」ように見える。
    1 回目の確認時に作る、という既存の判断を変えない）
  - `CLAUDE.md` および `.claude/rules/` への追記（必読規約 50KB 予算が warn 域のため 1 バイトも足さない）

## 母集合（自分で引き直した）

**issue 本文・コメントの一覧は母集合として転記していない。** 着手時に自分で引いた。
走査対象は追跡下の全ファイルで、**拡張子で絞らず、行フィルタで絞らず、パスの除外だけ**で取った
（除外: `planning` / `src/ai-stock-trading` の submodule）。

### 軸 1: 誤りの側＝「Alertmanager」で引く

```console
$ git grep -In -i "alertmanager" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
69
$ git grep -Il -i "alertmanager" -- . ':!planning' ':!src/ai-stock-trading'
（22 ファイル）
```

**22 ファイル / 69 行。** 内訳と扱い:

| 群 | 件数 | 扱い |
| --- | --- | --- |
| `deploy/` 配下（prometheus.yml・alerts.yml・grafana provisioning 等） | 7 | **触らない**（本作業の対象外。配備は行わない） |
| `docs/adr/`（IADR-0110 / 0139 / 0148 / 0164 / 0165） | 5 | 決定の記録。**本作業で決定を改めないため改変しない** |
| `docs/specs/`（6 件） | 6 | **確定済みの point-in-time 記録。書き換えない**（規約） |
| `docs/operations/operations.md` | 1 | **既に決定 40 の形に是正済み**（`:544-547` / `:552-555` / `:557-559`）。矛盾なし |
| `docs/operations/llm-cost-monthly-review-runbook.md` | 1 | **本作業の対象** |
| `docs/functional/FR-01_…` / `docs/observability/llm-completion-metrics.md` | 2 | 別資源（同期健全性・メトリクス設計）。矛盾なし |

### 軸 2: 「予算 / 上限アラート / LLM コスト・費用」で引く

```console
$ git grep -Il -E "予算|上限アラート|LLM ?(コスト|費用)|llm.?cost" -- . ':!planning' ':!src/ai-stock-trading'
（74 ファイル）
```

**74 ファイル。** 大半は**必読規約の「50KB 予算」**という同音異義（`CLAUDE.md` / `IADR-0190` /
`IADR-0200` / `check-reading-budget.js` 等）であり、**LLM 費用とは無関係**として除外した。
**除外は語の意味で行い、件数を黙って落としていない。** LLM 費用に実体で関わるのは軸 1 の
`docs/operations/` 2 件・`docs/screens/SC-10_…` ・`docs/tests/SC-10_…` ・
`src/knowledge/frontend/src/features/sc10-operations/` の 2 件である。
**SC-10 側は「費用カードを持たない」ことをテストで固定しており、本作業と矛盾しない**（確認済み）。

### 軸 3: 「単価 / pricing / 月次確認」で引く

```console
$ git grep -Il -i -E "単価|pricing|price_table|月次確認|monthly.?review" -- . ':!planning' ':!src/ai-stock-trading'
（22 ファイル）
```

**単価表の実体はリポジトリ内に存在しない。** 出てくるのは IADR-0101 / 0106 / 0112 が
**モデル選定の根拠として引いた外部の公開単価**であり、**費用計算に使える単価表ではない**。
**よって手順に「単価表を参照する」とは書けない**（存在しないものを手順に書かない）。

### 軸 4: 「月次 / 毎月」で引く（同等の手順書が既にないかの確認）

```console
$ git grep -In -E "月次|毎月" -- . ':!planning' ':!src/ai-stock-trading' ':!CHANGELOG.md' ':!docs/specs' ':!feedback'
```

- **`docs/operations/llm-cost-monthly-review-runbook.md` が既に存在する**（`status: fixed`・2026-08-10・PR #666）。
- `docs/operations/llm-model-pin-runbook.md` は**別資源**（モデル版数のピン留め）だが、
  **棚卸しの契機として本 Runbook に相乗りしている**（`:136` / `:152`）。**新設すると相乗り先が 2 つに割れる。**

## ★ 判断: **新設せず、既存 Runbook へ追記する**

母集合の軸 4 で、**案 A の手順書は既に存在し、内容も裁定どおり**であることが判明した。
**新設は行わない。** 理由は 3 点である。

1. **同じ統制が 2 か所に生まれる。** これは裁定 Q26 と決定 39 が一貫して避けている型そのものである。
2. `llm-model-pin-runbook.md` と `operations.md` が**既存 Runbook を名指しで参照している**。新設すると
   参照先が割れる。
3. `scripts/scripts.repo.test.js`（`:2679`）が**既存 Runbook のパスを固定**している。

### 既存 Runbook の記載が実在するかを実測した（全件一致）

**「今すぐ人が実行できる形」であることを、記述ではなく実体で確かめた。**

| Runbook の記述 | 実測 | 結果 |
| --- | --- | --- |
| ダッシュボード `LLM Usage (proxy for cost — NOT cost)` / uid `llm-usage` | `deploy/grafana/provisioning/dashboards/llm-usage.json`（`uid: llm-usage`・title 完全一致） | **実在** |
| 既定の時間範囲が直近 30 日で前月と一致しない | `"time": {"from":"now-30d","to":"now"}` | **一致** |
| 「前月比のパネルだけは範囲に依らない」 | 前月比パネルの式のみ `increase(...[30d]) / ...[30d] offset 30d` で `$__range` を使わない。他 3 枚は `$__range` | **一致** |
| パネル 4 種（前月比 / 累計・用途別 / 累計・モデル別 / 拒否率・打ち切り率） | 7 パネル中に全て実在（送信可否の内訳も含む） | **実在** |
| メトリクス `llm_completion_total` | `LlmCompletionMetrics.cs:21` に `llm.completion.total`（OTel の `.` → Prometheus `_`） | **一致** |
| 「単価表は未実装」 | 軸 3 のとおり実体なし | **正しい** |
| 「`alertmanagers.targets` は空」 | `deploy/prometheus.yml:16` と `deploy/local/observability/prometheus.yaml:19` の **2 か所とも `targets: []`** | **正しい** |

**誤りは 1 件も見つからなかった。** よって本文の是正は行わない。

### 追記する 1 点（実測で見つかった実行可能性の欠落）

**Runbook 手順 1 は「Grafana を開く」と無条件に書いているが、Grafana のダッシュボードが
provisioning されている環境は限られる。** 自分で走査して確かめた。

```console
$ find deploy -iname "*grafana*"
deploy/grafana
deploy/local/observability/grafana.yaml
deploy/local/vault/eso/externalsecret-grafana-oidc.yaml
$ git grep -rn -i "grafana" -- deploy/helm/
deploy/helm/microservices-platform/values.yaml:571,573   # SPA の config.js へ渡す URL のみ。リソースは無い
```

**stg / prod（Helm）には Grafana リソースが無い。** そこでは**手順 1〜2 を実行できない**。
これは `operations.md:552-555` が運用仕様の側で既に述べている事実だが、
**運用者が実際に開くのは Runbook のほうである**。統制を定める記述に現在の実現手段を併記せよ
（計画 決定 40）という規則に照らし、**Runbook 側へ「確認できる環境」を 1 表だけ追記する。**

**配備時期は書かない。** 追記するのは「いまどこで実行できるか」だけである。

## 設計（追記内容）

`docs/operations/llm-cost-monthly-review-runbook.md` の **§担当と頻度 と §手順 の間**に、
`### 確認できる環境（実行できるのはどこか）` を追加する。

- 3 行の表（compose / ローカル k8s / stg・prod）と、実行できない場合の扱いを 2 行。
- frontmatter の `related_ids` へ [[IADR-0164]] / [[IADR-0168]] を、`related_specs` へ本仕様書を追加し、
  `updated:` を 2026-08-15 へ前進させる。
- **金額を 1 文字も書かない**（決定 41。`scripts.repo.test.js:2683` が正規表現で固定している）。

## 受け入れ基準

- [x] 案 A（月次の手動確認）の手順が `docs/operations/` にあり、**何を・どの画面で・どの観点で見て・
      超過時に何をするか**が書かれている
- [x] 手順が指す Grafana ダッシュボード・Prometheus 式・メトリクス名が**リポジトリ内に実在する**
      （実測表のとおり全件一致）
- [x] **「現時点では自動検知が無い」ことが明記されている**（Runbook §前提）
- [x] **絶対額のしきい値を持たない**（前月比と月内累計で見る）
- [x] **本 Runbook が「暫定手段」側である旨が冒頭に書かれている**（`:26`）
- [x] **Alertmanager の配備時期を書いていない**／`deploy/` 配下を変更していない
- [x] **SC-10 へ費用表示を戻していない**
- [x] 検査器 4 本が通る（下記）

## テスト方針

**新規テストは追加しない。** 既存の `scripts/scripts.repo.test.js`（`:2630-2688`）が
ダッシュボードと Runbook の対応・金額不記載を既に固定しており、本作業はその不変条件を変えない。
文書側の検査は下記 4 本で確認する。

## 検証

```console
$ node scripts/check-doc-links.js
$ node scripts/check-doc-status-vocabulary.js
$ node scripts/check-doc-type-vocabulary.js
$ node scripts/check-grafana-alerting.js
```

## 計画書との差異

**無い。** 決定 39〜41 の範囲内であり、新 ADR も起こさない（計画が明示的に「ADR-0006 / ADR-0044 の
決定の範囲内の運用設計」と述べている）。

## NFR の採番について

**無採番の `NFR` とした。** 本作業は稼働する製品の運用要件（運用・保守）に当たるが、
**planning submodule が populate されていない**（`git submodule status` が `-4d6a7d6…`）ため、
`NFR-01`〜`NFR-27` のどの番号かを**実際に読んで確かめられなかった**。
**無理に近い番号を付けない**（実在しない対応づけを作ると監査が誤って数える）という規約に従い、
先行する同 issue の仕様書・Runbook と同じく無採番のままとした。

## 関連

- 既存 Runbook: [`llm-cost-monthly-review-runbook.md`](../operations/llm-cost-monthly-review-runbook.md)
- 先行する作業仕様書: [`20260810_issue-546_llm-cost-monthly-review.md`](./20260810_issue-546_llm-cost-monthly-review.md)
- 実装 ADR: [`IADR-0164`](../adr/IADR-0164_llm-cost-monthly-review-interim-control.md) ／
  [`IADR-0168`](../adr/IADR-0168_grafana-provisioning-parity.md)
- 運用仕様書: [`operations.md`](../operations/operations.md) §監視・アラート
