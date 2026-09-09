---
title: ユニットの主体が project 無しで保存した文書を数える指標を DocumentService に置く
type: spec
status: done
related_ids: [FR-05, FR-09, FR-10, FR-16, SC-05, SC-10, SC-12, ADR-0034, ADR-0036, ADR-0044, ADR-0062, ADR-0085, IADR-0119, IADR-0153, IADR-0293, IADR-0299, IADR-0373, IADR-0378, IADR-0389, IADR-0398, IADR-0405, IADR-0420]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 8 指標目の生産者を置く（#1233 残作業 1・3）

## 起点

- FR-05（ABAC）／ FR-09（認可）／ FR-10（ダッシュボード）／ FR-16（MCP）／ SC-10 ／ SC-12
- 計画 ADR: `ADR-0085` **決定 4**（2026-09-06 確定。指標「ユニットの主体が保存した文書のうち
  `project` を持たない件数」。**0 が正常**）／ 決定 1（基盤では `project` は**任意**）／
  決定 2（ABAC の判定軸へ載せない）／ `AST/ADR-0032` 決定 2・3 ／ `ADR-0044` 決定 1（属性の基数）
- 実装 ADR: [[IADR-0420]]（本 PR で新設）／ [[IADR-0405]]（制限 `project` は保存で落とせない）／
  [[IADR-0373]] 決定 4（**制限値の集合を構成から読まない**）／ [[IADR-0119]]（SC-10 の節ごと保留）／
  [[IADR-0153]] 決定 5・[[IADR-0389]]（同型の「0 が正常」計器と生産者の置き方）
- issue: #1233（2 番目のコメントが射程を組み替えた。残作業 **1**＝生産者の実装、**3**＝主体の識別方式）

## 🔴 現状（実測。基点 `origin/develop` `7c9d184`。`git rev-parse --is-shallow-repository` = **`true`** なので `git log` は出典に使わず作業ツリーを読んだ）

| 走査 | コマンド | 結果 |
| --- | --- | --- |
| 生産者の有無 | `grep -rn "unit_project\|project_missing" .`（拡張子で絞らない） | **0 件。定義だけで 1 件も観測されない** |
| 陽性対照（走査器が生きているか） | `grep -rln "ingest_unknown_tag_total" docs/ deploy/ scripts/ src/` | 3 ファイル（可観測性仕様書・compose ダッシュボード・k8s inline） |
| 計画側の指標数 | 計画 `06_technical/05_observability-ops.md:67` | 「対象は次の **8 指標**である（… **2026-09-06 にさらに 1 指標**を追加した）」 |
| 本リポの「7 指標」 | `grep -rn "7 指標" docs/ scripts/ src/ deploy/` | **16 行 / 8 ファイル**（下の母集合表） |
| アラート数（導出値） | `grep -c '^\s*-\s*alert:'` を 4 経路で | compose 12 / k8s 12 / Grafana compose 12 / Grafana k8s 12 |

## 母集合（規則 9・10。**誤りの側の語で全走査**してから挙げた）

`7 指標` で全走査した 16 行を、**直す／直さない**へ 1 行ずつ割り振る。

| 走査で出た箇所 | 扱い | 理由 |
| --- | --- | --- |
| `docs/observability/knowledge-health-indicators.md:21, 26` | **直す** | 本書の主語は**計画が定める指標**であり、計画は 8 になった |
| `docs/functional/FR-10_dashboard.md:92, 122` | **直す（言い換える）** | 主語は**集計 API の語彙**（7 のまま）。「計画が定める 7 指標」という書き方が計画と食い違うので、主語を明示して 7 の理由を書く |
| `src/…/DashboardService/Domain/KnowledgeHealth.cs:5` | **直す（言い換える）** | 同上。**語彙へ 8 つ目を足さない理由**をここへ書いた（[[IADR-0420]] 決定 4） |
| `docs/tests/FR-10_dashboard.md:66`（T-26「7 指標すべてが 0 件で返る」） | **直さない** | 集計 API の応答の性質であり、7 のままで正しい |
| `src/…/DashboardService/Features/KnowledgeHealth/View/{Endpoint,Query}.cs`・同 Tests 2 件 | **直さない** | 同上（API の語彙数） |
| `src/…/GraphService/Domain/KnowledgeHealthIndicators.cs:6` | **直さない** | 「受け口の 7 指標の一覧を複写しない」という文であり、受け口は 7 のまま |
| `src/knowledge/frontend/…/OperationsDashboardPage.test.tsx:298` | **直さない** | 2026-08-29 時点の実測を引いた**日付つきの記述**（SC-10 の保留の根拠） |
| `docs/screens/SC-10_operations-dashboard.md:95, 272` | **直さない** | 同上。いずれも `［2026-08-29 / #443］` の**時点つき**の記録 |
| `.ai-context/specs/*`・`.ai-context/adr/*`（6 ファイル） | **直さない** | **凍結記録**（本文プロズを後から書き換えない） |

**除外の軸をもう 1 本引いた**（規則 5）—— アラート件数の導出値 `12` を
`grep -rn "12 ルール\|12 件"` で引き、**live な 4 箇所**（`docs/operations/operations.md` 3 箇所 ＋
`scripts/check-grafana-alerting.js` ＋ `deploy` の provisioning 頭注 2 箇所）を **13 へ数え直した**。
`.ai-context/` 側の同じ数字（`IADR-0399` / 仕様書 2 件）は凍結記録なので**直さない**。

🔴 **是正が新たな誤りを作った箇所を引き直した**（規則 10）。`docs/operations/operations.md` の
`［2026-09-05 追記］`「経路B の inline は compose と同数ではない（2 件が写されていない）」は
**実測で既に解消していた**（両経路 12 件。`check-prometheus-alerts-parity` も OK）。
本 PR で 13 件へ増やすと**この誤りが目立つ形で残る**ため、日付つきで訂正した。

## 決定（詳細は [[IADR-0420]]）

1. **主体の識別は `MachinePrincipal`（`Platform.Shared.Infrastructure`）に 1 つ置く。**
   利用者名が `service-account-` で始まる、**または**利用者名を持たずクライアント識別クレームを持つ。
   🔴 **構成に許可リストを持たない**（[[IADR-0373]] 決定 4 と同じ理由 —— 一覧から外すだけで免れる）。
   🔴 **`SyntheticTraffic` は触らない**（あちらは許可集合との照合であり、目的が違う）。
2. **計器は `UnitProjectMetrics`（`documents.unit_project_missing.total`）。** 既存 Meter に相乗り。
   属性は `documents.client_id` ＋ `documents.operation` の 2 つだけ。文書 ID・題名は載せない。
3. **計上は 3 経路の `SaveChangesAsync` の後。** 保存が成立しない要求（400 / 404 / 409 / 413）は数えない。
4. **`DashboardService` の集計語彙へは足さない**（生産者の無い 0 件を並べない）。

## 触ったファイル

| 面 | ファイル | 内容 |
| --- | --- | --- |
| 判定 | `Platform.Shared.Infrastructure/Foundation/Observability/MachinePrincipal.cs`（新規） | 無人主体か／クライアント識別子 |
| 計器 | `DocumentService/Common/Observability/UnitProjectMetrics.cs`（新規） | カウンタと計上条件 |
| 合成 | `DocumentService/Program.cs` | 登録 ＋ `AddMeter` |
| 受け口 | `Features/Documents/{Create,Update,UpdateMetadata}/Endpoint.cs` | 保存成功後に 1 行 |
| 語彙 | `DashboardService/Domain/KnowledgeHealth.cs` | **足さない理由**をコメントで固定 |
| 配備 | `deploy/prometheus/alerts.yml` ／ `deploy/local/observability/prometheus.yaml` ／ `deploy/grafana/provisioning/alerting/slo-alerts.yaml` ／ `deploy/local/observability/grafana.yaml` | アラート 1 件を 4 経路へ同内容で ＋ パネル 1 枚を 2 経路へ |
| 文書 | `docs/observability/knowledge-health-indicators.md` ／ `docs/functional/FR-10_dashboard.md` ／ `docs/operations/operations.md` | 8 指標目・計器表・件数の数え直し |
| 試験 | `Platform.Shared.Infrastructure.Tests/…/MachinePrincipalTests.cs`／`DocumentService.Tests/…/UnitProjectMetricsTests.cs`／同 `UnitProjectEndpointMetricsTests.cs`／`TestAuthHandler.cs` | 下記 |

## テスト（受け入れ基準）

- [x] (a) 無人主体 ＋ `project` 欠落 → 加算する
- [x] (b) 無人主体 ＋ `project` 有り → 加算しない
- [x] (c) 🔴 **陽性対照**: 対話ログインの人間 ＋ `project` 欠落 → 加算しない
- [x] (d) `Update` / `UpdateMetadata` でも同じ母集合を数える
- [x] (e) 保存が成立しない要求（400 / 404 / 409）では加算しない
- [x] 属性は `client_id` ＋ `operation` の 2 つだけで、値は `azp` の生値（取れなければ `unknown`）
- [x] 空白だけの `project` は「持たない」と読む
- [x] 未認証・`null` は無人主体と読まない
- [x] 変異試験で赤を実測する

**観測は `MeterListener`。購読は `Meter.Scope` の参照一致で絞る**（名前で絞ると、プロセス全体を
購読する性質上、並行する他テストクラスの測定が混ざる）。端点側は `IMeterFactory` を
テスト用へ差し替えたホスト（`MeteredFactory`）で同じ probe を使う。

## 変異試験（実出力。すべてビルドし直して実走し、戻したことは緑で確認した）

| # | 変異 | 赤 |
| --- | --- | --- |
| M-1 | `IsMachine` を「クライアント識別クレームを持つか」だけにする（素朴な実装） | **shared 1 件**（`対話ログインの利用者はazpを持っていても無人主体と判定しない`） ＋ **DocumentService 3 件** |
| M-2 | `UpdateMetadata` の配線を外す | **1 件**（`メタデータ更新で無人主体がprojectを落としたら計上する`） |
| M-3 | `HasProject` を鍵の有無だけにする（空値を「持っている」と読む） | **1 件**（`空のproject値は持たないものとして計上する`） |
| M-4 | 計上を `SaveChangesAsync` の**前**（辞書引きの前）へ動かす | **1 件**（`登録が400で落ちたら計上しない`） |

🔴 **M-1 が本作業で最も重要な変異である。** 陰性側（無人主体を数える）はどちらの実装でも緑になり、
**分けられるのは陽性対照だけ**である。素朴な実装は `azp` が人間のトークンにも付くために
**全利用者を無人主体にし**、指標を母集合ごと壊す（計画が退けた「張り付いて動かない数字」になる）。

## 実測（数字）

- `DocumentService.Tests` 441 → **455**（新規 14）。`Platform.Shared.Infrastructure.Tests` 330 → **340**（新規 10）。
- knowledge 全ユニット・platform 全ユニットとも **Failed 0**。`dotnet format --verify-no-changes` 両ユニット緑。
- 配備の 3 検査器（Prometheus パリティ / Grafana アラート / Grafana provisioning パリティ）とも **OK**。
  アラートは 12 → **13**、ダッシュボードのパネルは 7 → **8**（いずれも数え直した導出値）。

## やらないこと（と理由）

- 🔴 **#1233 残作業 2（`project` を持つ文書の実件数の報告）は測れない。** **稼働 DB が無い**
  （本作業機に配備済みのスタックが無く、Docker も使えない）。**利用者の手が要る** ——
  統合スタックを立てて数えるか、運用者が本番相当のデータで数える必要がある。
  数え方だけ書いておく: `SELECT count(*) FROM "Documents" WHERE "Attributes" ? 'project'`
  （PostgreSQL の jsonb。母数は `count(*)`）。**推論で数字を書かない。**
- **`project` の必須化・ABAC 判定軸への追加**（`ADR-0085` 決定 1・2 が「しない」と確定済み）。
- **SC-10 の画面へ出すこと**（同節は [[IADR-0119]] により節ごと保留。1 指標だけ差し込むと線引きが壊れる）。
- **同型の `ingest_unknown_tag_total` にアラートを対で置くこと。** 本 PR の射程外に残した
  **非対称**であり、気付いていないのではない（別 issue の候補として可観測性仕様書へ明記した）。
- **`DashboardService` の集計語彙への追加**（決定 4。生産者の無い 0 件を並べない）。
