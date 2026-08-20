---
title: 作業仕様書 — #546 の月次手動確認は実装済みと実測した（新設・追記とも行わない）
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
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 作業仕様書: #546 の月次手動確認（案 A）は既に実装済みである

## 結論（先に書く）

**本 issue で新たに作る文書もコードも無い。** 案 A（LLM 費用の月次手動確認）は
**2026-08-10 に PR #666 で完了している**。**新設も追記も行わなかった。**

**#546 に残っているのは Alertmanager の実配備ただ 1 点であり、これは実環境の判断（blocked）である。**

> **★ 本書は「実装しなかった」ことの記録である。** 着手指示は「月次の手動確認手順を文書化せよ」であったが、
> **母集合を自分で引いた結果、その手順書は既に存在していた**。**重複した手順書を作ることが最悪の結果**であり
> （同じ言明を 2 か所に持つ事故型そのもの。裁定 Q26 と計画 決定 39 が一貫して避けている形）、
> **作らないことを成果物とした。**

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-11**（LLM ゲートウェイ）
- ユースケース（UC）: 該当なし（運用手順であり、利用者操作のフローではない）
- 画面（SC）: **SC-10**（運用ダッシュボード。**費用の KPI カードは持たない**。Grafana 導線カードのみ）
- 非機能要件（NFR）: **運用・保守**（無採番。理由は末尾「NFR の採番について」）
- 関連 ADR: **ADR-0006**（可観測性。アラートは Alertmanager を用いる）／
  **ADR-0044**（LLM 利用実績の計測粒度と単価表。ADR-0006 の部分改定）
- 関連 実装 ADR: [IADR-0110](../adr/IADR-0110_llm-completion-stop-reason-metrics.md)（メトリクスの設計と限界）／[IADR-0164](../adr/IADR-0164_llm-cost-monthly-review-interim-control.md)（本件の暫定統制の決定）／
  [IADR-0168](../adr/IADR-0168_grafana-provisioning-parity.md)（Grafana provisioning のパリティ）
- 計画書リンク: `06_technical/05_observability-ops.md`（計画リポ）
  **§LLM 費用の上限アラートと暫定の統制**（2026-08-08 確定。決定 39〜41）／
  `05_screens/01_screens.md`（計画リポ） §SC-10
- 起点 issue: **#546**（planning#286 で裁定依頼 → planning `b8002cc` の**決定 39〜42**。**採られたのは案 A**）

## 目的・背景

2026-08-05 の裁定 Q26 で SC-10 から LLM コストの KPI カードを外した結果、**予算超過に気づく手段が
無くなった**。Alertmanager が未配備で上限アラートも飛ばないためである。2026-08-08 の裁定で
**案 A（月次の手動確認を運用手順へ置く）**が採られた。本作業はその文書化を行うために着手した。

## 既存実装の実測（判断の根拠）

### 1. PR #666 の変更内容

GitHub REST API で PR を実読した（`gh` と MCP が本セッションで使えないため
`https://api.github.com/repos/endazon/microservices-platform/pulls/666` を直接取得）。

```text
PR #666  feat(NFR,SC-10,IADR-0164): LLM 費用の月次手動確認を運用仕様へ置き、行き先のダッシュボードを作る
state=closed  merged=true  merge_commit_sha=0296f1f5cc3137a4b23a62fc03f163f8ca65de5d
```

| 変更 | ファイル |
| --- | --- |
| added (110 行) | **`docs/operations/llm-cost-monthly-review-runbook.md`** ← **案 A の手順書そのもの** |
| added (105 行) | `deploy/grafana/provisioning/dashboards/llm-usage.json` ← 手順の行き先 |
| added (149 行) | `docs/adr/IADR-0164_llm-cost-monthly-review-interim-control.md` |
| added (160 行) | `docs/specs/20260810_issue-546_llm-cost-monthly-review.md` |
| modified (21 行) | `docs/operations/operations.md` |
| modified (59 行) | `scripts/scripts.repo.test.js` ← 金額不記載とダッシュボード実在をテストで固定 |

### 2. `docs/operations/` 配下の実物

```console
$ ls docs/operations/
llm-cost-monthly-review-runbook.md   llm-model-pin-runbook.md
local-sso-recovery-runbook.md        operations.md
$ git log --oneline --all -- docs/operations/
1565021 / 36bc4ea / 39d6973 / 3a79911
```

**`llm-cost-monthly-review-runbook.md`（`status: fixed`・110 行）が存在する。**
内容は裁定どおりであり、**指示にあった要件をすべて満たしている**ことを 1 項目ずつ突合した。

| 指示された要件 | 既存 Runbook の該当箇所 | 判定 |
| --- | --- | --- |
| 何を・どの画面/コマンドで | §手順 1〜2（SC-10 の Grafana 導線カード → uid `llm-usage`） | **満たす** |
| どの閾値と突き合わせ | §手順 4（**絶対額を持たず**前月比 1.5 倍を目安・月内累計の傾き） | **満たす** |
| 超過時に何をするか | §増加を認めたとき（4 つの引き渡し先） | **満たす** |
| 担当・頻度・記録 | §担当と頻度（`platform-operator`・翌月第 1 営業日）／§記録 | **満たす** |
| **自動検知が無いことの明記** | §前提の表（`alertmanagers.targets` が空・**超過してもアラートは飛ばない**） | **満たす** |
| **「暫定手段」側である旨を冒頭に** | `:26`「**これは暫定の統制である。**」 | **満たす** |
| 配備時期を書かない | §終了条件（時期は書かず「**実環境の判断であり #546 で追跡**」） | **満たす** |

### 3. #546 のコメント（2026-08-08 の裁定と、その後の完了報告）

issue 本文と全 6 コメントを REST API で実読した。

- **2026-08-08**: 計画側で**決定 39〜42 が確定**（planning `b8002cc`）。案 A が採られ、
  Alertmanager の配備時期は**依然として未定＝実環境の判断**。
- **2026-08-10**: **決定 39〜41 の着地報告**（PR #666 / `0296f1f`）。
- **2026-08-15**: 棚卸し。「**暫定措置は 2026-08-10 に完了していた。新たに実装するものは無い**」。
  issue を閉じないのは、**3 つの ADR（IADR-0164 `:143` / IADR-0165 `:170` / IADR-0168 `:89`,`:171`）から
  名指しされた追跡アンカー**になっているため。

## 母集合（自分で引き直した）

**issue 本文・コメントの一覧は母集合として転記していない。** 着手時に自分で引いた。
走査は追跡下の全ファイルに対し、**拡張子で絞らず、行フィルタで絞らず、パスの除外だけ**で行った
（除外: `planning` / `src/ai-stock-trading` の未 populate submodule）。

> **［2026-08-15 追記 / #546］各軸の数を、走査がそのまま返す生の数から引き算を見せる形へ直した。**
> 従前は **69 / 22 / 74 / 22** と、**本書を書く前に引いた数**をそのまま載せていた。
> **本書自身が走査対象に入っており**（`docs/specs/` はどの軸のパス除外にも入っていない）、
> **`alertmanager` を 15 行含む**ため、公開時点で同じコマンドを走らせても再現しない数になっていた。
> 母集合の規則 8 は「**走査がそのまま返す数を先に出し、除外と時点を明示する**」と定める。
> **以下はすべて 2026-08-15 に、コミット `0609de8` のツリー（本追記を書く前の状態）で再実行した生の数**である。
> **自己参照の行数・ファイル数も同じコマンドを本書 1 ファイルへ当てて数えた。**
> **本追記そのものが `alertmanager` を含むため、この追記の後に走らせると軸 1 の生の数はさらに増える** ——
> だから**値は時点（`0609de8`）で固定し、引き算の形で残す**（規則 8 の「値はコミットで固定する」）。

### 軸 1: 誤りの側＝「Alertmanager」で引く

```console
$ git grep -In -i "alertmanager" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
84
$ git grep -In -i "alertmanager" -- docs/specs/20260815_issue-546_llm-cost-monthly-manual-check.md | wc -l
15
$ git grep -Il -i "alertmanager" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
23
$ git grep -Il -i "alertmanager" -- docs/specs/20260815_issue-546_llm-cost-monthly-manual-check.md | wc -l
1
```

**行数: 84 行 → 自己参照（本書）15 行を引く → 69 行。**
**ファイル数: 23 件 → 自己参照（本書）1 件を引く → 22 件。**
下の内訳表（合計 22 件）はこの 22 件に対応する。

| 群 | 件数 | 扱い |
| --- | --- | --- |
| `deploy/` 配下（prometheus.yml・alerts.yml・grafana provisioning 等） | 7 | **触らない**（配備は行わない） |
| `docs/adr/`（IADR-0110 / 0139 / 0148 / 0164 / 0165） | 5 | 決定の記録。決定を改めないため改変しない |
| `docs/specs/`（6 件） | 6 | **確定済みの point-in-time 記録。書き換えない**（規約） |
| `docs/operations/operations.md` | 1 | **既に決定 40 の形に是正済み**（`:544-547` / `:552-555` / `:557-559`） |
| `docs/operations/llm-cost-monthly-review-runbook.md` | 1 | **既存の案 A 手順書**（＝本作業が作ろうとしていたもの） |
| `docs/functional/FR-01_…` / `docs/observability/llm-completion-metrics.md` | 2 | 別資源。矛盾なし |

### 軸 2: 「予算 / 上限アラート / LLM コスト・費用」で引く

```console
$ git grep -Il -E "予算|上限アラート|LLM ?(コスト|費用)|llm.?cost" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
75
$ git grep -Il -E "予算|上限アラート|LLM ?(コスト|費用)|llm.?cost" -- docs/specs/20260815_issue-546_llm-cost-monthly-manual-check.md | wc -l
1
```

**75 件 → 自己参照（本書）1 件を引く → 74 件。**（2026-08-15・コミット済みツリー `0609de8`）

**74 ファイル。** 大半は**必読規約の「50KB 予算」**という同音異義（`CLAUDE.md` / [IADR-0190](../adr/IADR-0190_permanent-headroom-by-annexing-examples.md) /
[IADR-0200](../adr/IADR-0200_reading-budget-population-per-agent.md) / `check-reading-budget.js` 等）であり、**LLM 費用とは無関係**として除外した。
**除外は語の意味で行い、件数を黙って落としていない。** LLM 費用に実体で関わるのは
`docs/operations/` の 2 件・`docs/screens/SC-10_…`・`docs/tests/SC-10_…`・
`src/knowledge/frontend/src/features/sc10-operations/` の 2 件で、
**SC-10 側は「費用カードを持たない」ことをテストで固定**しており矛盾しない。

### 軸 3: 「単価 / pricing / 月次確認」で引く

```console
$ git grep -Il -i -E "単価|pricing|price_table|月次確認|monthly.?review" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
23
$ git grep -Il -i -E "単価|pricing|price_table|月次確認|monthly.?review" -- docs/specs/20260815_issue-546_llm-cost-monthly-manual-check.md | wc -l
1
```

**23 件 → 自己参照（本書）1 件を引く → 22 件。**（2026-08-15・コミット済みツリー `0609de8`）

**単価表の実体はリポジトリ内に存在しない。** 出てくるのは [IADR-0101](../adr/IADR-0101_default-model-opus-5.md) / [IADR-0106](../adr/IADR-0106_rag-answer-sonnet-5.md) / [IADR-0112](../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md) が
**モデル選定の根拠として引いた外部の公開単価**であり、費用計算に使える単価表ではない。
**よって手順に「単価表を参照する」とは書けない** —— 既存 Runbook もそう書いていない（`:38`）。

### 軸 4: 「月次 / 毎月」で引く（**同等の手順書の有無**）

```console
$ git grep -In -E "月次|毎月" -- . ':!planning' ':!src/ai-stock-trading' ':!CHANGELOG.md' ':!docs/specs' ':!feedback'
```

**この軸だけは自己参照の引き算が要らない** —— パス除外に `':!docs/specs'` が入っており、
**本書が最初から母集合の外**だからである（軸 1〜3 との違いはここ 1 点。2026-08-15 に確認）。

**この軸で新設を取り止めた。**

- **`docs/operations/llm-cost-monthly-review-runbook.md` が既に存在する。**
- `docs/operations/llm-model-pin-runbook.md` は別資源（モデル版数のピン留め）だが、
  **棚卸しの契機として既存 Runbook に相乗りしている**（`:136` / `:145` / `:152`）。
  **新設すると相乗り先が 2 つに割れる。**
- `docs/operations/operations.md`（`:544-546` / `:683`）と
  `scripts/scripts.repo.test.js`（`:2679`）が**既存 Runbook のパスを名指しで固定**している。

## 既存 Runbook の記載が実在するかを実測した（全件一致）

**「今すぐ人が実行できる形」であることを、記述ではなく実体で確かめた。**
**誤りは 1 件も見つからなかった。**

| Runbook の記述 | 実測 | 結果 |
| --- | --- | --- |
| ダッシュボード `LLM Usage (proxy for cost — NOT cost)` / uid `llm-usage` | `deploy/grafana/provisioning/dashboards/llm-usage.json`（uid・title とも完全一致） | **実在** |
| 既定の時間範囲が直近 30 日で前月と一致しない | `"time": {"from":"now-30d","to":"now"}` | **一致** |
| 「前月比のパネルだけは範囲に依らない」 | 前月比パネルの式のみ `increase(...[30d]) / ...[30d] offset 30d` で `$__range` を使わない。他は `$__range` | **一致** |
| パネル 4 種（前月比 / 累計・用途別 / 累計・モデル別 / 拒否率・打ち切り率） | 7 パネル中に全て実在 | **実在** |
| メトリクス `llm_completion_total` | `LlmCompletionMetrics.cs:21` に `llm.completion.total`（OTel の `.` → Prometheus `_`） | **一致** |
| 「トークン消費量・金額換算・単価表は未実装」 | 軸 3 のとおり実体なし | **正しい** |
| 「`alertmanagers.targets` は空」 | `deploy/prometheus.yml:16` と `deploy/local/observability/prometheus.yaml:19` の **2 か所とも `targets: []`** | **正しい** |

## 実装した ID と成果物

**コード・文書の変更は無い（本仕様書 1 枚のみ）。**

| 対象 | 判断 |
| --- | --- |
| `docs/operations/llm-cost-monthly-review-runbook.md` | **新設せず、追記もしない。** `origin/develop` と**バイト一致**（`git diff origin/develop -- <path>` が 0 行） |
| `deploy/` 配下 | **触らない**（配備は実環境の判断） |
| `CLAUDE.md` / `.claude/rules/` | **1 バイトも足さない**（必読規約 50KB 予算が warn 域。実測 50,193 バイトで不変） |

## 走査中に見つけた観察（**本 issue では対応しない**）

**既存 Runbook の手順 1 は「Grafana を開く」と無条件に書いているが、Grafana が provisioning されている
環境は限られる。** 自分で走査して確かめた。

```console
$ find deploy -iname "*grafana*"
deploy/grafana   deploy/local/observability/grafana.yaml   deploy/local/vault/eso/externalsecret-grafana-oidc.yaml
$ git grep -rn -i "grafana" -- deploy/helm/
deploy/helm/microservices-platform/values.yaml:571,573   # SPA の config.js へ渡す URL のみ。リソースは無い
```

**stg / prod（Helm）には Grafana リソースが無く、そこでは手順 1〜2 を実行できない。**

**それでも Runbook へ追記しなかった。** 理由:

1. **既に 2 か所が同じ事実を述べている** —— `operations.md:552-555`（「`deploy/helm/` 配下に
   Prometheus/Alertmanager リソースは無い」）と [IADR-0168](../adr/IADR-0168_grafana-provisioning-parity.md)（`:89` / `:171`）。
   **3 か所目を作れば、片方が古くなる**（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)／`docs/README.md` 運用ルール 4 と同じ型）。
2. **#546 に残る範囲の外である。** 本 issue の残りは Alertmanager の配備 1 点であり、
   **Grafana リソースの stg / prod 展開は [IADR-0168](../adr/IADR-0168_grafana-provisioning-parity.md) が既に追跡している。**
3. 一度この追記を入れたが、**上記 1 の理由で取り消した**（本書の判断として記録しておく）。

## 受け入れ基準

- [x] 案 A の手順が `docs/operations/` にあり、**何を・どの画面で・どの観点で見て・超過時に何をするか**が書かれている（**既存 Runbook が満たす**）
- [x] 手順が指す Grafana ダッシュボード・Prometheus 式・メトリクス名が**リポジトリ内に実在する**（実測表のとおり全件一致）
- [x] **「現時点では自動検知が無い」ことが明記されている**（Runbook §前提）
- [x] **絶対額のしきい値を持たない**（前月比と月内累計で見る）
- [x] **Runbook が「暫定手段」側である旨が冒頭にある**（`:26`）
- [x] **Alertmanager の配備時期を書いていない**／`deploy/` 配下を変更していない
- [x] **SC-10 へ費用表示を戻していない**
- [x] **重複した手順書を作っていない**（`docs/operations/` は `origin/develop` と差分ゼロ）

## テスト方針

**新規テストは追加しない。** 既存の `scripts/scripts.repo.test.js`（`:2630-2688`）が
ダッシュボードと Runbook の対応・メトリクス名の一致・金額不記載を既に固定しており、
本作業はその不変条件に一切触れていない。

## 検証

```console
$ node scripts/check-doc-links.js                 # OK: 624 件、破損リンクなし
$ node scripts/check-doc-status-vocabulary.js     # OK: 584 件、値域内
$ node scripts/check-doc-type-vocabulary.js       # OK: 598 件、値域内
$ node scripts/check-grafana-alerting.js          # OK: Prometheus 5 / Grafana 5 が 1 対 1
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js   # ✓ 528 tests passed
$ git diff origin/develop -- docs/operations/     # （空。既存文書は 1 バイトも変えていない）
```

## 計画書との差異

**無い。** 決定 39〜41 は PR #666 で既に着地しており、本作業はそれを追認しただけである。
新 ADR も起こさない（計画が「ADR-0006 / ADR-0044 の決定の範囲内の運用設計」と明示している）。

## 残っている範囲（blocked）

**Alertmanager の実配備＝実環境の判断。** 実装側では決められない。
配備が決まったときに実装側で行うこと（#546 が持つ宿題）:

1. `llm-cost-monthly-review-runbook.md` の `status` を `superseded` にし、後継（上限アラート）を明記する
   —— **手動確認と上限アラートを併存させない**（計画 決定 39）
2. Grafana の暫定通知経路を削除する（[IADR-0165](../adr/IADR-0165_grafana-interim-alerting.md) 決定 5 の 3 条件）
3. `alertmanagers.targets` を埋める（**compose と k8s の 2 か所**。本作業でも実測して確認した）
4. stg / prod（Helm）の Grafana リソースを整える（[IADR-0168](../adr/IADR-0168_grafana-provisioning-parity.md)）

## NFR の採番について

**無採番の `NFR` とした。根拠は規約の例外 2（ID 列はあるが、その作業に当たる番号が無い）である。**

［2026-08-15 追記 / #546］**従前ここには「submodule が populate されていないので確かめられなかった」と
書いていたが、それは無採番の根拠にならない。** 規約は「**どちらの場合かは、作業を始める前に計画の
ID 列を見て判断する**」と定めており、「読めない」は例外 1 にも 2 にも当たらない。**submodule を
populate して ID 列を実読し、判断をやり直した。**

```console
$ git submodule update --init planning && grep -n "NFR-2[0-3]" \
    planning/projects/microservices-platform/02_requirements/01_requirements.md
139:| NFR-21 | 運用・保守 | 障害検出〜復旧 | MTTR 30分以内、検出 5分以内 | アラート・Runbook整備。… |
```

- **`NFR-21`（障害検出〜復旧・MTTR 30 分／検出 5 分）は、#546 のうち「Alertmanager を配備して
  通知を配線する」部分に当たる。** その部分は本 PR の範囲外（実環境の判断・blocked）である。
- **本 PR の成果物は「既存 Runbook が案 A を満たしていることを実測して記録する」検証記録**であり、
  製品の稼働要件そのものではない。**工程の管理は製品の非機能要件表とは別の軸**であるため、
  例外 2 に当たる。**`NFR-21` を付けると、監査が「NFR-21 の実装」として数えてしまい無採番より劣化する。**
- したがって**この場合は計画側へ環流しない**（例外 1 と違い、計画側に不足があるわけではない）。

## 関連

- 既存 Runbook: [`llm-cost-monthly-review-runbook.md`](../../docs/operations/llm-cost-monthly-review-runbook.md)（**本作業では変更していない**）
- 先行する作業仕様書: [`20260810_issue-546_llm-cost-monthly-review.md`](./20260810_issue-546_llm-cost-monthly-review.md)
- 実装 ADR: [`IADR-0164`](../adr/IADR-0164_llm-cost-monthly-review-interim-control.md) ／
  [`IADR-0165`](../adr/IADR-0165_grafana-interim-alerting.md) ／
  [`IADR-0168`](../adr/IADR-0168_grafana-provisioning-parity.md)
- 運用仕様書: [`operations.md`](../../docs/operations/operations.md) §監視・アラート
- issue: **#546**（本件）／ #443（可観測性・LLM 計測の再実装）／ #665（SLO の暫定通知経路）
