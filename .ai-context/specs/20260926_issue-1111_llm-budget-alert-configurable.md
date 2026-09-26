---
title: 作業仕様書 — LLM 月次予算の上限アラートを「所有者が設定する金額」で配線する（金額は置かない）
type: spec
status: done
related_ids: [FR-10, FR-11, NFR-21, SC-10, ADR-0006, ADR-0044, IADR-0164, IADR-0304, IADR-0322, IADR-0466]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md (§LLM 費用の上限アラートと暫定の統制 / §リスク・未決事項)
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md
issue: "#1111"
---

# 作業仕様書 — LLM 月次予算の上限アラート（金額は所有者の設定値）

## 起点

- issue #1111（LLM 月次予算の上限アラートを配線する）。FR-11 / NFR-21 / 計画 ADR-0044・ADR-0006。
- 🔴 **所有者の裁定（2026-09-26・チャット経由でコーディネータから伝達）**:
  「**予算の金額を所有者が設定する値にできるなら、アラートの配線を実装してよい**」。
  **本作業は金額を 1 つも選ばない。** 設定値は既定を持たず、未設定のあいだアラートは評価対象を持たない（不活性）。
- 判断の記録: `IADR-0466`（本 PR で新設）。

## 計画の制約（読んだ。`05_observability-ops.md` 254〜302 行付近）

| # | 制約 | 本作業での扱い |
| --- | --- | --- |
| C1 | **月次予算の金額（しきい値）は定めない。実測を待って確定する** | 金額は設定値。**既定なし**。リポジトリのどこにも金額を書かない |
| C2 | **手動確認と上限アラートを併存させない** | 金額未設定のあいだ Runbook が唯一の統制。**金額を初めて設定する変更が Runbook を `superseded` にする**ことを scripts のテストで機械的に強制する |
| C3 | **金額換算はゲートウェイ側。Prometheus / Grafana に単価を書かない** | ルールは `llm_cost_total`（換算済み）とゲートウェイが出す上限のゲージを比べるだけ。単価は式に現れない |
| C4 | **SC-10 へ費用表示を戻さない**（BFF 契約に費用の項目を持たない） | SC-10 / BFF 契約には触れない |
| C5 | **基盤の費用と利用側プロジェクト（AST）の費用を合算して 1 つの数値にしない** | **上限は用途（purpose）ごと**。総額 1 本の上限は持たない（AST の用途 `trade-decision*` と基盤の用途が同じ数値へ潰れるため） |

## 前提 1〜3 の現況（着手時に測り直した。リポジトリ走査のみ・稼働クラスタは叩いていない）

```console
$ git rev-parse --is-shallow-repository
false
$ git rev-parse --short HEAD          # origin/develop から分岐
33a21412
```

| # | 前提 | 現況 | 根拠 |
| --- | --- | --- | --- |
| 1 | 金額が確定している | 🔴 **未確定のまま**（計画 §リスク・未決事項）。**本作業はこれを満たさない —— 満たさずに済む形（設定値・既定なし・不活性）にすることが所有者の裁定である** | 計画 `05_observability-ops.md`「月次予算の金額（しきい値）が未確定である」の行は残っている（`gh api .../05_observability-ops.md` 2026-09-26 取得） |
| 2 | 費用の計器がソースにある | 🟢 `LlmUsageMetrics.cs` の `llm.cost.total`（`llm.purpose` / `llm.model` / `llm.provider` / `llm.confidentiality` / `llm.currency`） | ソース走査。**稼働イメージは本セッションでは測っていない**（HARD LIMIT） |
| 3 | `[30d]` 窓を評価できる保持 | 🟢 compose・経路 B とも `--storage.tsdb.retention.time=35d`（PR #1355） | `deploy/docker-compose.yml:220` / `deploy/local/observability/prometheus.yaml:232`。ただし `retention.size=4GB` が先に効くと窓の先頭が欠け得る（IADR-0466 §結果に記録） |

## Prometheus 側の名前（推測しない）

- 既存の実在名: `llm_cost_total`（`docs/observability/llm-usage-and-cost-metrics.md` の表・`llm-usage.json` のクエリ）。
  ラベルは `llm_purpose` / `llm_currency`（同ダッシュボードの `sum by (llm_purpose)`）。
- 新設ゲージ `llm.budget.monthly_limit`（単位 `{currency}`）→ **`llm_budget_monthly_limit`**。
  根拠: collector の `prometheusremotewrite` は既定設定（`deploy/otel-collector-config.yaml:25`。
  `add_metric_suffixes` 等の上書きなし）。`{…}` の単位は注記扱いで接尾辞にならず、`_total` はカウンタだけに付く。
  **同じ変換で出ている実在例**: UpDownCounter `http.server.active_requests`（単位 `{request}`）→
  `http_server_active_requests`（PR #1165 の実測列挙）。
- 🔴 **稼働 TSDB での実在確認は本 PR ではしていない**（ゲージは金額を設定しない限り系列を持たない）。
  所有者手順（Runbook）に「系列が出ることを確かめる」を置く。

## 設計（コーディネータの決定。IADR-0466 に記録）

1. `Llm:Budget:MonthlyLimits` ＝ 用途 → 金額（単価表の通貨 `Llm:Pricing:Currency`）。**既定なし（空）**。
2. 起動時検証（`ValidateOnStart`。`ModelPricingOptionsValidator` と同じ型）: キーは `Llm:Routing:PurposeModels` のキーか `default`、金額は `> 0`。
3. ゲートウェイは観測ゲージ `llm.budget.monthly_limit`（`llm.purpose` ＋ `llm.currency`）を**設定された用途だけ**出す。未設定 → 系列なし → ルールは空ベクタで**発火しない**。`absent()` 系は置かない（#1111 で却下済み）。
4. ルール `LlmMonthlyBudgetExceeded`:
   `sum by (llm_purpose, llm_currency) (increase(llm_cost_total[30d])) > on (llm_purpose, llm_currency) max by (llm_purpose, llm_currency) (llm_budget_monthly_limit)`。
   **窓は暦月ではなく直近 30 日の移動窓**（理由は IADR-0466）。
5. 同じルールを 4 か所（compose / 経路 B の Prometheus・Grafana）に置き、3 つの突合検査を通す。
6. 併存の禁止を機械化: 「金額が設定されている」XOR「Runbook が `superseded`」なら fail（`scripts/scripts.repo.test.js`）。

### 金額の置き場（単一の情報源）

**`src/platform/backend/Services/LlmGateway/appsettings.json` の `Llm:Budget:MonthlyLimits`** とする。
単価表（`Llm:Pricing`・通貨）と同じファイルであり、compose・経路 B・helm の 3 経路に同じ値が届く
（deploy 側に書くと経路ごとに 3 つの置き場ができる）。
**検査は deploy 側の環境変数上書き（`Llm__Budget__MonthlyLimits__*`）も「設定済み」と数える** ——
どこかにコミットされた金額はすべて統制の切替を意味するため。発火の実演で一時的に入れる上書きはコミットしない。

## 母集合（`traceability.repo.md` 規則 9・10。自分で引いた）

**軸 1（誤りになる側の文言）**:

```console
$ git grep -n -E "自動検知(は|が)無|上限アラートを置いていない|上限アラートは置かない|アラートは置けない|上限アラート.*未配線|#1111|月次予算" \
    -- ':!.ai-context/specs' ':!.ai-context/superpowers' ':!CHANGELOG.md'
```

| 出たファイル | 扱い | 理由 |
| --- | --- | --- |
| `docs/operations/llm-cost-monthly-review-runbook.md` | **直す** | 「自動検知は無い」「上限アラートは配線されていない」→「配線はあるが金額未設定で働いていない」。所有者手順を足す |
| `docs/operations/operations.md`（§監視・アラート 647〜658 / §未決事項 1072〜1078） | **直す** | 同上（issue 受け入れ基準 5） |
| `deploy/grafana/provisioning/dashboards/llm-usage.json` の説明テキスト | **直す** | 「上限アラートを置いていない」が偽になる |
| `deploy/local/observability/grafana.yaml` の同テキスト | **直す** | 上と同内容（`check-grafana-provisioning-parity.js`） |
| `docs/observability/llm-usage-and-cost-metrics.md`（§本仕様書が扱わないこと） | **直す** | 同上。ゲージを計器表へ足す |
| `docs/operations/operations.md:662`（提供終了の監視は月次の費用確認に相乗り。自動検知は無い） | 除外 | モデルの提供終了の監視の話であり費用の自動検知ではない。月次確認は存続するので偽にならない |
| `deploy/docker-compose.yml:213` / `deploy/local/observability/prometheus.yaml:224`（#1111 前提 3 の保持） | 除外 | 保持 35d の理由であり、本作業で偽にならない |
| `docs/observability/synthetic-traffic-exclusion.md`（trace ブロックの `#1111`） | 除外 | 参照だけ |
| `.ai-context/adr/IADR-0164` / `IADR-0265` / `IADR-0304` / `IADR-0322` | 本文は書き換えない（凍結記録）。**IADR-0304 決定 5・IADR-0322 決定 3 には日付つき追記を 1 行ずつ置く**（決定の射程が本作業で変わるため） |

**軸 2（導出値＝ルール件数）**: 1 件足すので 16 → 17。

```console
$ grep -cE "^\s*-\s*alert:" deploy/prometheus/alerts.yml deploy/local/observability/prometheus.yaml
deploy/prometheus/alerts.yml:16
deploy/local/observability/prometheus.yaml:16
$ grep -cE "^\s*title:" deploy/grafana/provisioning/alerting/slo-alerts.yaml
16
$ git grep -n -E "(16|13) ?(件|ルール)" -- deploy scripts docs/operations docs/observability   # 件数を書いた箇所
```

| 箇所 | 現在の記述 | 扱い |
| --- | --- | --- |
| `slo-alerts.yaml` 冒頭（＋経路 B inline） | 16 件・内訳 | 17 件へ計算し直す（内訳に群を足す） |
| `scripts/check-grafana-alerting.js` 冒頭 | 16 件 | 17 件へ |
| `docs/operations/operations.md` 638 / 667 / 686 / 695 | **13**（既に腐っていた。実体は 16） | 17 へ計算し直す |

**軸 3（新設名の衝突）**: `git grep -n "llm_budget\|llm.budget\|Llm:Budget\|Llm__Budget"` → 0 件（衝突なし）。

## 受け入れ基準（本 PR が満たすもの）

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| A1 | 金額を 1 つも選ばない（既定なし） | `appsettings.json` に `MonthlyLimits` が空。T-3 の実データ検査が「未設定」を返す |
| A2 | 起動時検証: 未知の用途・0 以下の金額で落ちる。未設定は成功 | T-1（xUnit） |
| A3 | ゲージは設定された用途だけを出し、未設定なら 1 件も出さない（陰性対照） | T-2（xUnit・MeterListener） |
| A4 | 同じルールが 4 か所にあり、3 検査器が通る | `check-grafana-alerting.js` / `check-grafana-provisioning-parity.js` / `check-prometheus-alerts-parity.js` |
| A5 | 併存の禁止が機械的に強制される（XOR で fail・変異試験つき） | T-3（`scripts.repo.test.js`） |
| A6 | ゲージ名と式の名前が一致している（#1110 と同型の「名前のずれで永久に鳴らない」を止める） | T-3 |
| A7 | Runbook・運用仕様書・ダッシュボード説明が「配線はあるが金額未設定で働いていない」と正確に書く | 目視 ＋ `check-trace-blocks.js` |
| A8 | 所有者の手順（金額の置き場・系列の確かめ方・意図的な発火・Runbook の superseded 化）が Runbook にある | 目視 |

**本 PR が満たさないもの（所有者の手順として PR 本文と Runbook に残す）**: 金額の設定・
`/api/v2/alerts` での発火の実測（issue 受け入れ基準 3）・Runbook の superseded 化（金額設定と同じ変更）。
**よって PR は `Refs #1111`（Closes ではない）。**

## テスト ID

| ID | 内容 | 置き場 |
| --- | --- | --- |
| T-1 | 予算設定の起動時検証（未設定は成功／未知の用途・0・負値は失敗／`default` と PurposeModels のキーは既知） | `Tests/Domain/Pricing/LlmBudgetOptionsValidatorTests.cs` |
| T-2 | ゲージの発行（設定した用途だけ・タグ・通貨／未設定は 0 件＝陰性対照）と、ホストが起動時にゲージを登録すること | `Tests/Common/Observability/LlmBudgetMetricsTests.cs` |
| T-3 | 併存の禁止（XOR）・ゲージ名と式の一致・実データ | `scripts/scripts.repo.test.js` |

## 宣言ファイル領域

issue 本文の 6 ファイルに加え:
`src/platform/backend/Services/LlmGateway/{Program.cs,appsettings.json,Common/Observability/LlmBudgetMetrics.cs,Domain/Pricing/LlmBudgetOptions*.cs,Tests/**}` /
`deploy/grafana/provisioning/dashboards/llm-usage.json` / `docs/observability/llm-usage-and-cost-metrics.md` /
`scripts/scripts.repo.test.js` / `scripts/check-grafana-alerting.js`（件数コメントのみ） /
`.ai-context/adr/{IADR-0466_*.md,README.md,IADR-0304_*.md,IADR-0322_*.md}`

## 検証（PR 前）

`dotnet build` / `dotnet test`（LlmGateway.Tests） / `dotnet format --verify-no-changes` / 3 検査器 /
`check-trace-blocks.js` / `gen-knowledge-graph.js --check` / `check-adr-numbering.js` /
`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。出力は PR 本文へ貼る。

### 結果（2026-09-26・`origin/develop` b54b719c へ rebase 後）

| 検査 | 結果 |
| --- | --- |
| `dotnet build src/platform/backend/backend.slnx` | 成功（警告 0・エラー 0）。AST submodule を init してから実行 |
| `dotnet test` LlmGateway.Tests | 314 件合格・失敗 0 |
| `dotnet format ... --verify-no-changes` | exit 0 |
| 3 検査器（alerting / provisioning parity / prometheus parity） | いずれも OK（17 / 17 件） |
| `check-trace-blocks.js` / `gen-knowledge-graph.js --check` | OK |
| `check-adr-numbering.js` | 🔴 **`IADR-0465` が欠番**（事前割当の番号。0465 は open の PR #1529 が持つ。**#1529 のマージ後に rebase すれば解消**。本 PR の側で直すものではない） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 採番検査で止まる（上と同じ理由）。**0465 の仮置き（ファイル ＋ 索引行）を一時的に置いて**全件を流し **808 件合格**（T-3 の 8 件を含む）。仮置きは消し、コミットに含めていない |
| 変異試験（T-2） | `Program.cs` の起動時登録を外すと T-2d・T-2e が落ちることを確かめ、戻した |
