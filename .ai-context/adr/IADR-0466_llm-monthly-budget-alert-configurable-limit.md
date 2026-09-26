---
title: IADR-0466 LLM 月次予算の上限は用途別の設定値（既定なし）とし、ゲートウェイがゲージで出して直近 30 日の費用と比べる
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, NFR-21, SC-10, ADR-0006, ADR-0044, IADR-0110, IADR-0164, IADR-0304, IADR-0322, IADR-0378]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md
related_specs:
  - ../specs/20260926_issue-1111_llm-budget-alert-configurable.md
---

# IADR-0466: LLM 月次予算の上限アラートを「所有者が設定する金額」で配線する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（#1111。設計はコーディネータの決定、実装の可否は所有者の裁定 2026-09-26）

## 起点・関連

- 関連する計画書 ID: FR-11（LLM 送信先の切替・用途別）／FR-10（利用実績）／NFR-21（障害検出）／SC-10（費用表示を戻さない制約）
- 関連する計画 ADR: **ADR-0044**（用途別・モデル別の計測・金額換算はゲートウェイ側）、ADR-0006（アラートは Alertmanager）
- 関連する計画書: `06_technical/05_observability-ops.md` §LLM 費用の上限アラートと暫定の統制（利用者裁定 2026-08-08）／§リスク・未決事項
- 関連する実装 ADR: **IADR-0322** 決定 3（予算アラートを置かず #1111 へ分けた）・**IADR-0304** 決定 5（絶対額のしきい値は置かない）・**IADR-0164**（月次の手動確認）・IADR-0110（属性の値域）・IADR-0378（合成監視を費用から外す）
- 関連する実装仕様書: `.ai-context/specs/20260926_issue-1111_llm-budget-alert-configurable.md`

## コンテキストと課題

#1111 は「LLM 月次予算の上限アラートを配線する」issue であり、前提 3 つのうち 2（計器）と 3（35 日の保持。PR #1355）は満たされたが、
**前提 1（金額）は計画が「定めない。実測を待って確定する」と明示したまま**である。IADR-0322 決定 3 はこれを根拠に
アラートを置かず、IADR-0304 決定 5 も「単価の絶対額しきい値は置かない」とした。

2026-09-26、所有者が次のとおり裁定した: **「予算の金額を所有者が設定する値にできるなら、アラートの配線を実装してよい」**。
つまり**配線と金額を切り離し、配線だけを先に置く**ことが許された。金額は引き続き実装側では選ばない。

判断が要ったのは次の 5 点である。

1. 金額の**単位**（総額 1 本か、用途別か）
2. 金額の**置き場**と**検証**
3. 金額をルールへ**どう渡すか**（式に書くか、系列で渡すか）
4. **窓**（暦月か、直近 30 日か）
5. 計画の「**手動確認と併存させない**」を、金額の無い今と金額を置いた後の両方でどう守るか

## 検討した選択肢

### 1. 単位

| 案 | 評価 |
| --- | --- |
| 総額 1 本の上限 | 🔴 **計画が禁じている。** 用途には利用側プロジェクト（AST）の用途（`trade-decision`・`trade-decision-screening`）が含まれ、計画は「基盤の費用と利用側プロジェクトの費用を合算して 1 つの数値にしない」と定める。総額はまさにその 1 つの数値である |
| 基盤／利用側の 2 本 | 用途がどちらに属するかの対応表を新たに持つことになる（設定に無い分類を実装が作る） |
| **用途別（採用）** | 用途は既に `llm.cost.total` の属性軸であり、分類を新設しない。基盤と AST の用途は別の系列のまま比べられる |

### 2. 置き場と検証

| 案 | 評価 |
| --- | --- |
| deploy 側（helm values・経路 B の values・compose）の環境変数 | 経路ごとに 3 つの置き場ができ、値が食い違っても気付けない |
| **ゲートウェイの `appsettings.json` の `Llm:Budget:MonthlyLimits`（採用）** | 単価表（`Llm:Pricing`・通貨）と同じファイル。3 経路に同じ値が届く。検証を起動時に掛けられる（`ModelPricingOptionsValidator` と同型） |

### 3. ルールへの渡し方

| 案 | 評価 |
| --- | --- |
| 式に数字を書く（`> 123`） | 金額の置き場がルール 4 か所（compose・経路 B の Prometheus と Grafana）に割れる。Grafana 版は `params` に持つので置き方まで 2 通りになる。**未設定を表現できず、プレースホルダの数字を置くしかない**（それが既成事実になる） |
| レコーディングルールで定数系列を作る | 同上（数字がルールファイルに入る） |
| **ゲートウェイが観測ゲージで出す（採用）** | 置き場は設定 1 か所。**未設定なら系列が無く、式が空ベクタになって発火しない** —— 「金額を決めていない」がそのまま「評価対象が無い」になる |

### 4. 窓

| 案 | 評価 |
| --- | --- |
| 暦月 | PromQL に暦月の範囲選択子は無い。`day_of_month()` 等で近似するとルールが複雑になり、月初に累計が 0 へ戻るため「月初に予算を使い切った」を月内のどこでも同じ強さで拾えない |
| **直近 30 日の移動窓（採用）** | `increase(llm_cost_total[30d])`。保持 35 日（PR #1355）で評価できる。ダッシュボードの前月比パネルも固定 30 日で読んでおり、読み方が揃う |

## 決定

### 決定 1 — 上限は**用途別**とし、総額 1 本は持たない

キーは `llm.purpose` と同じ値域（`Llm:Routing:PurposeModels` のキー ＋ `default`）。未知の用途の集約先 `other` は
受け付けない（どの呼び出し元の予算かが決まらない）。

### 決定 2 — 金額は `Llm:Budget:MonthlyLimits`（`appsettings.json`）の**設定値で、既定は無い**

- 型は `Dictionary<string, decimal>`（用途 → 金額）。**通貨は単価表の通貨**（`Llm:Pricing:Currency`）であり、別欄を持たない。
- **起動時検証**（`LlmBudgetOptionsValidator`・`ValidateOnStart`）: キーが既知の用途でない・金額が 0 以下なら起動を落とす。
  **未設定（空）は成功**とする —— 金額を決めていないことは計画の現在の状態であり誤りではない。
  ここを失敗にすると「起動させるために何か数字を入れる」圧力が生まれる。
- 🔴 **本 PR はリポジトリのどこにも金額を置かない**（`appsettings.json` は空の `MonthlyLimits` を持つだけ）。

### 決定 3 — ゲートウェイは**設定された用途だけ**観測ゲージ `llm.budget.monthly_limit` を出す

- 属性は `llm.purpose`（小文字化）と `llm.currency` の 2 つだけ（`llm.cost.total` と同じ軸で突き合わせるため）。
- **起動時に登録する**（`Program.cs` で解決する）。シングルトンの遅延生成に任せると最初の補完まで系列が出ず、
  所有者の確認手順（系列が出ることを見る）が成り立たない。
- **未設定なら 1 件も出さない。** `absent(llm_budget_monthly_limit)` のような補助ルールは**置かない** ——
  金額が未設定のあいだ恒常発火し、既知の誤報を作る（IADR-0322 決定 3 が却下した形）。

### 決定 4 — ルール `LlmMonthlyBudgetExceeded`（群 `llm-cost-budget`）を 4 か所に同じ式で置く

```promql
sum by (llm_purpose, llm_currency) (increase(llm_cost_total[30d]))
  > on (llm_purpose, llm_currency)
max by (llm_purpose, llm_currency) (llm_budget_monthly_limit)
```

- `for: 5m`・`severity: warning`。`max by` はゲートウェイのレプリカ・再起動（`instance` の違い）で重複した上限の系列を畳む。
- **金額も単価も式に無い**（計画の「金額換算はゲートウェイ側」「Prometheus・Grafana に単価を書かない」を満たす）。
- Grafana 版は同じ式を `expr` に持ち、閾値は `params: [0]`（式が超過した用途だけを残すので残った費用 > 0 が超過）。
  **`noDataState: OK`** —— 金額が未設定でも、予算内でも、結果は空（正常時が「データ無し」）であり、`NoData` だと恒常発火する。
- 置き場: `deploy/prometheus/alerts.yml`・`deploy/local/observability/prometheus.yaml`（inline）・
  `deploy/grafana/provisioning/alerting/slo-alerts.yaml`・`deploy/local/observability/grafana.yaml`（inline）。
  `check-grafana-alerting.js` / `check-grafana-provisioning-parity.js` / `check-prometheus-alerts-parity.js` が 1 対 1 を見る（16 → 17 件）。

### 決定 5 — 窓は**直近 30 日の移動窓**であり、暦月ではない

- 発火は「直近 30 日の費用 > 予算」。**解消は月初ではなく、超過分が窓から抜けたとき**に来る。
- 暦月との差は**両向きにある**。一方向に「早い」「保守的」とは言えない:
  - **鳴らない向き**: 31 日の月では、暦月の合計が予算を超えても、その月に収まるどの 30 日窓も予算を超えないことがあり得る
    （差は高々 1 日分の費用）。2 月（28・29 日）は逆に窓が暦月より長く、この向きの取りこぼしは起きない。
  - **鳴る向き**: 月をまたぐ 30 日窓で超えれば、暦月ではどちらの月も予算内であっても発火する。
- それでも採るのは、**PromQL に暦月の範囲指定が無い**（`[30d]` は固定長）からであり、暦月に合わせるには
  月初に巻き戻る記録ルールか外部の集計が要る。差は窓の端の高々数日分の費用に限られ、暦月の数字は
  月次確認の Runbook が暦月で記録しているのでそちらで突き合わせられる（数字がずれることを Runbook に書いた）。

### 決定 6 — **併存させない**を機械で強制する（金額の設定 XOR Runbook の superseded で fail）

- 金額が未設定のあいだ、`docs/operations/llm-cost-monthly-review-runbook.md` が**唯一の統制**のまま残り、
  「配線はあるが金額未設定のため働いていない」と明記する。
- **金額を初めて設定する変更が、同じ変更で Runbook を `superseded` にする。**
- `scripts/scripts.repo.test.js` が次を検査する:
  - 「設定済み」＝ `src/platform/backend/Services/LlmGateway/appsettings*.json` の `Llm.Budget.MonthlyLimits` が 1 件以上、
    **または** `deploy/` 配下の全ファイルに（コメント行以外で）`Llm__Budget__MonthlyLimits__<用途>`（またはコロン区切りの同じ鍵）が 1 件以上。
  - 「superseded」＝ Runbook の frontmatter `status: superseded`。
  - **両者が食い違えば fail**（両向き。金額あり×Runbook 継続＝併存、金額なし×Runbook 終了＝統制ゼロ）。
  - あわせて、C# のゲージ名（`LlmBudgetMetrics.LimitGaugeName`）から導いた Prometheus 名が 4 か所のルールの式に現れることを検査する
    （#1110 と同型の「名前のずれで永久に鳴らない」を止める）。
- 根拠は「同型 2 回目」ではなく**計画の明示的な禁止**（決定 39）である。本件は 1 回目の事故を待つと
  **費用の統制がゼロの期間を作る**向きに壊れるため、検査器の追加条件（同型 2 回）の例外として置く。
  新しい検査器ファイルは作らず、既存の companion テストへ 1 群を足すに留める。

## Prometheus 側の名前

- `llm.cost.total`（Counter・`{currency}`）→ `llm_cost_total`（既存。ダッシュボードのクエリで使用中）。
- `llm.budget.monthly_limit`（観測ゲージ・`{currency}`）→ **`llm_budget_monthly_limit`**。
  collector の `prometheusremotewrite` は既定設定（接尾辞の上書きなし）。`{…}` の単位は注記として接尾辞にならず、
  `_total` はカウンタだけに付く。同じ変換で出ている UpDownCounter `http.server.active_requests`（`{request}`）→
  `http_server_active_requests` が PR #1165 で実測されている。
- 🔴 **ゲージの稼働 TSDB での実在は本 PR では測っていない**（金額を設定しない限り系列が無い）。所有者手順（Runbook §金額を設定する手順 3）で確かめる。

## 結果

### 良くなること

- 金額を決めたあとに要る作業が「設定 1 か所 ＋ Runbook を閉じる」に縮む。配線の設計・名前の突合・4 か所の同期はもう要らない。
- 金額が無いあいだは何も評価しない —— 数字を実装が選ばないという計画の制約と、配線の着地を両立できる。

### 残るもの・受け入れる帰結

- **所有者の作業が残る**（#1111 は本 PR で閉じない）:
  1. 金額を決めて `Llm:Budget:MonthlyLimits` に置く（計画側の確定を待つかは所有者の判断）。
  2. 同じ変更で Runbook を `superseded` にし、運用仕様書・ダッシュボード説明の「自動検知はまだ働いていない」を書き換える。
  3. 配備後に系列の実在を確かめ、本番ではない環境で小さい金額を一時的に与えて `/api/v2/alerts` に発火が現れることを実測する（issue 受け入れ基準 3）。
- **`retention.size=4GB` が先に効くと 30 日窓の先頭が欠け、費用を過小に評価する**（鳴るべきときに鳴らない向き）。
  流入が増えたら PVC と size を対で上げる（IADR-0210 決定 3 の形）。
- **単価の解決漏れがあると費用が過小**になり、アラートも過小に評価する（`llm_pricing_unpriced_total` を先に見る。ADR-0044 決定 3 の帰結）。
- **ゲートウェイの再起動ごとに、新しい系列の最初の分だけ費用を過小に数える。** 再起動（Pod の作り直し）で
  `llm_cost_total` は `instance` 等のラベルが違う**新しい系列**として始まる。`increase(...[30d])` は系列の最初の標本より
  前の増分を知らないので、**新しい系列が最初に送出されるまでに積まれた分（起動直後から最初の送出間隔まで）**は数えない。
  1 回あたりは送出間隔ぶんの費用に限られるが、再起動が多いほど積み重なり、**鳴るべきときに鳴らない向き**に倒れる。
  同じ系列内の計数の巻き戻り（リセット）は `increase` が補正するので、この過小は系列が新しくなる場合に限る。
- **併存禁止の検査（決定 6）が見るのはリポジトリ内の文字列だけである。** 走査範囲は
  `src/platform/backend/Services/LlmGateway/appsettings*.json` と `deploy/` 配下のテキストに限られ、
  **`helm --set`・稼働クラスタで直接足した環境変数・Vault / ESO から入る値・`deploy/` 外の上書きファイルは見えない。**
  そうした経路で金額を入れると、検査は緑のまま Runbook と上限アラートが併存する。**金額は単一の置き場
  （`appsettings.json`）にだけ書く**ことを Runbook に明記し、この限界は機械では塞がない。
- **合成監視の費用は含まれない**（IADR-0378。費用に積まないため）。予算は人の利用に対するものとして読む。
- 通知の宛先は依然 `default-null`（IADR-0304 決定 2）。発火は Alertmanager の画面で見るまで誰にも届かない。
- IADR-0304 決定 5・IADR-0322 決定 3 の「置かない」は、**金額（しきい値）を置かない**部分として存続し、
  **配線を置かない**部分は本 ADR が改める（両記録に日付つきの追記を置いた）。
