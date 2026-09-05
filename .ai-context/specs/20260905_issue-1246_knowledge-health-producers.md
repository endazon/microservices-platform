---
title: 作業仕様書 — ナレッジ健全性の unresolved-links / edge-type-usage に生産者を置き、観測値モデルへ内訳の軸を足す
type: spec
status: done
related_ids:
  - FR-10
  - FR-17
  - FR-19
  - UC-05
  - SC-10
  - NFR-21
  - ADR-0002
  - ADR-0006
  - ADR-0033
  - ADR-0054
  - ADR-0076
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-10 運用ダッシュボード)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md (ナレッジ健全性の指標・集計範囲)
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph.md (決定 3・9 辺の型)
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md (決定 3 系列の不在)
related_adrs:
  - IADR-0389
  - IADR-0299
  - IADR-0265
  - IADR-0353
  - IADR-0370
  - IADR-0281
issue: "#1246"
---

# 作業仕様書: ナレッジ健全性の生産者 2 件（#1246）

## 起点

#443 が受け口（`DashboardService` の `/internal/knowledge-health/observations` と閲覧 GET）を
完成させたが、**計画の 7 指標のうち 3 指標に生産側が無い**。うち実装で閉じられる 2 件を本作業で置く。

## 自分で引いた母集合（実測 2026-09-05・基点 `3d5f8c99`）

`git rev-parse --is-shallow-repository` → `false`。

**issue 本文の表を転記していない。** 7 指標を受け口の語彙（`DashboardService.Domain.KnowledgeHealthIndicators.All`）
から列挙し、指標名で全 C# を走査して生産者の有無を自分で確かめた。

```console
$ grep -rn "orphan-documents\|stale-documents\|unresolved-links\|unsummarized-clusters\|edge-type-usage\|undefined-type-fallbacks\|ingest-unknown-tags" --include=*.cs src
```

| # | 指標 | 生産者の実体 | 宛先 | 判定 |
| --- | --- | --- | --- | --- |
| 1 | `orphan-documents` | `KnowledgeHealthCollector.CollectOrphanDocumentsAsync` | 観測値 | 生産あり |
| 2 | `unresolved-links` | **コメントのみ**（`KnowledgeHealthCollector` :27） | — | 🔴 **無し** |
| 3 | `unsummarized-clusters` | **コメントのみ**（同 :29） | — | 🔴 **無し**（射程外） |
| 4 | `stale-documents` | `KnowledgeHealthCollector.CollectStaleDocumentsAsync` | 観測値 | 生産あり |
| 5 | `edge-type-usage` | **コメントのみ**（同 :30） | — | 🔴 **無し** |
| 6 | `undefined-type-fallbacks` | `EdgeTypeFallbackMetrics`（`graph.edge_type_fallback.total`） | OTel → Grafana | 生産あり |
| 7 | `ingest-unknown-tags` | `IngestTagMetrics`（`ingest.unknown_tag.total`） | OTel → Grafana | 生産あり |

**陽性対照**: 同じ走査が 1・4・6・7 について生産者側のファイルを返している。よって 2・3・5 の
「コメントだけ」は走査の取りこぼしではなく「無い」である。

**3（`unsummarized-clusters`）は射程外**。クラスタリング・要約の実装がリポジトリ全体で 0 件であり
（走査: `cluster|community|louvain` → 実装は 0 件、`get_cluster_summary` を**公開しない**とする
否定形テストのみ）、計画が「クラスタ」の定義も要約の要否も定めていない。**実装側で先取りしない。**

## 決めること（実装 ADR `IADR-0389` に残す）

1. 観測値モデルに**内訳の軸**（`Dimension`）を足す。「指標 1 つ＝件数 1 つ」（IADR-0265 の先送り）を解く。
2. **曖昧一致（同名文書が複数）を未解決に含める。** 軸で `not-found` と `ambiguous` を分ける。
3. `unresolved-links` は**解決失敗を保存しない。リンク先の名前を保存し、集計時に解決する。**
   失敗を保存すると、**参照先の改名・削除で他文書のリンクが壊れても、その文書が再取り込みされる
   まで件数に現れない**（リンク切れを数える指標として致命的）。
4. `edge-type-usage` の文書スコープは**両端点のどちらかが個人資料なら `private-note`**。
5. 生産者の不在は `absent_over_time(...[2h])` で拾う。**`absent()` は使えない** ——
   生産周期が 1 時間（`KnowledgeHealthHostedService.Interval`）であり、既定 5 分の lookback では
   平常時に鳴り続ける。IADR-0370 決定 1 の「稼働クラスタの無風時間で決める」は
   **トラフィック駆動の系列**の規則であり、周期駆動の生産者は周期が構成から確定する。
6. **SC-10 の健全性節は開かない**（受け入れ基準 5）。フロントの否定形テストはそのまま残す。

## 変更する宣言ファイル領域

- `src/knowledge/backend/Services/DashboardService/Domain/KnowledgeHealth.cs`（`Dimension`）
- `src/knowledge/backend/Services/DashboardService/Infrastructure/Persistence/`（DbContext ＋ マイグレーション）
- `src/knowledge/backend/Services/DashboardService/Features/KnowledgeHealth/`（Report 契約・View 契約と集計）
- `src/knowledge/backend/Services/GraphService/Domain/`（`DocumentLinkTarget` / `LinkTargetMatcher` / 指標名）
- `src/knowledge/backend/Services/GraphService/Features/GraphDocuments/Sync/`（`LinkEdgeSynchronizer`）
- `src/knowledge/backend/Services/GraphService/Features/GraphDocuments/Delete/`（削除時の掃除）
- `src/knowledge/backend/Services/GraphService/Features/KnowledgeHealth/Report/`（収集の 2 本）
- `src/knowledge/backend/Services/GraphService/Common/Observability/`（報告カウンタ）
- `src/knowledge/backend/Services/GraphService/Infrastructure/Persistence/`（DbContext ＋ マイグレーション）
- `deploy/prometheus/alerts.yml` / `deploy/grafana/provisioning/alerting/slo-alerts.yaml` /
  `deploy/local/observability/grafana.yaml`（`absent_over_time` 2 件）
- `.ai-context/adr/IADR-0389_*.md` ＋ `.ai-context/adr/README.md`

**交差**: #1203（合成監視の除外標識）は `KnowledgeHealth*` を触り得る。着手時点で open な PR は無い。
#1215 の門（`scripts/check-stack-ready.js`）には触らない。

## 受け入れ基準（issue の 6 項目の写像）

1. `unresolved-links` の観測値が生産され、受け口が 0 以外を返し得る。曖昧一致の扱いを IADR に残す。
2. `edge-type-usage` が**型ごとの内訳**として表現できる。
3. 生産側のテストに**境界と陽性対照を対で**置く。
4. 受け口側の既存の否定形テスト（`private-note` 除外・403・文書名を含まない）が引き続き緑。
5. SC-10 の健全性節は開かない。
6. `dotnet test src/knowledge/backend/backend.slnx` が緑。

## 測らないこと

- **稼働 k3s では測っていない**（この環境から新しい系列を作れない。Docker デーモンも無い）。
  `absent_over_time` のしきい値は**周期の構成値から導いた**ものであり、稼働環境での実測ではない。
- **Grafana が provisioning を受理するか**は測れない（`check-grafana-alerting.js` の冒頭が明記する既知の穴）。
