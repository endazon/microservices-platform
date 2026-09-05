---
title: IADR-0389 未解決リンクは名前を保存して集計時に解決し直し、観測値モデルへ内訳の軸を足す
type: impl-adr
status: Accepted
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
  - IADR-0265
  - IADR-0281
  - IADR-0299
  - IADR-0353
  - IADR-0370
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-10 運用ダッシュボード)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md (ナレッジ健全性の指標・集計範囲)
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph.md (決定 3・9)
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md (決定 3)
---

# IADR-0389: ナレッジ健全性の生産者 2 件と観測値の内訳の軸（#1246）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 実装 issue **#1246**（#454 の子トラッカー棚卸しで摘出）
- 先行: **IADR-0299**（受け口と生産者の分離・観測値を渡す設計）／**IADR-0265**（「指標 1 つ＝件数 1 つ」の
  観測値モデル。型別内訳を**先送り**した）／**IADR-0353**（`stale-documents` の生産者としきい値の併記）／
  **IADR-0281**（リンク先の名前解決）／**IADR-0370**（`absent` 系アラートの置き方）
- 裁定 **planning#494**（生産者の無い指標を 0 件として並べてはならない）

## コンテキストと課題

#443 が受け口（`/internal/knowledge-health/observations` と閲覧 GET）を完成させたが、
**計画の 7 指標のうち 3 指標に生産側が無い。**

### 自分で引いた母集合（実測 2026-09-05・基点 `3d5f8c99`）

`git rev-parse --is-shallow-repository` → `false`。受け口の語彙
（`DashboardService.Domain.KnowledgeHealthIndicators.All`）から 7 指標を列挙し、
指標名で全 C# を走査した（**issue 本文の表を転記していない**）。

| # | 指標 | 生産者の実体 | 宛先 | 判定 |
| --- | --- | --- | --- | --- |
| 1 | `orphan-documents` | `KnowledgeHealthCollector.CollectOrphanDocumentsAsync` | 観測値 | 生産あり |
| 2 | `unresolved-links` | コメントのみ | — | 🔴 **無し** |
| 3 | `unsummarized-clusters` | コメントのみ | — | 🔴 **無し**（本 ADR の射程外） |
| 4 | `stale-documents` | `KnowledgeHealthCollector.CollectStaleDocumentsAsync` | 観測値 | 生産あり |
| 5 | `edge-type-usage` | コメントのみ | — | 🔴 **無し** |
| 6 | `undefined-type-fallbacks` | `EdgeTypeFallbackMetrics`（`graph.edge_type_fallback.total`） | OTel → Grafana | 生産あり |
| 7 | `ingest-unknown-tags` | `IngestTagMetrics`（`ingest.unknown_tag.total`） | OTel → Grafana | 生産あり |

**陽性対照**: 同じ走査が 1・4・6・7 について生産者側のファイルを返している。
よって 2・3・5 の「コメントだけ」は走査の取りこぼしではなく「無い」である。

**3 は新機能である。** クラスタリング・要約の実装がリポジトリ全体で 0 件であり
（走査 `cluster|community|louvain` → 実装 0 件。あるのは `get_cluster_summary` を**公開しない**という
否定形テストだけ）、計画が「クラスタ」の定義も要約の要否も定めていない。**実装側で先取りしない。**

### 塞がっていた理由は指標ごとに違った

- `unresolved-links` … 解決失敗は `LinkEdgeSynchronizer` がログへ出して**捨てていた**。永続化が未設計で、
  **曖昧一致（同名文書が複数）を未解決に含めるかも未定義**だった。
- `edge-type-usage` … 件数は引けるが、観測値モデルが「指標 1 つ＝件数 1 つ」であり
  **型別の内訳を表現できない**（IADR-0265 が先送りした）。

## 決定

### 決定 1: 観測値へ**内訳の軸**（`Dimension`）を足し、IADR-0265 の先送りを解く

`KnowledgeHealthObservation` に nullable の `Dimension` を持たせ、閲覧が
`KnowledgeHealthIndicatorDto.Breakdown`（軸名と件数の並び）として返す。

- 🔴 **基数が有界な語だけを載せる。** 軸は集計の GROUP BY 相当であり、自由語（文書名・リンク先の名前・
  未定義の型名）を入れると内訳が無界に増えて読めなくなる ——
  `EdgeTypeFallbackMetrics` が型名をタグにしないのと同じ判断基準である。
  載せるのは実行時辞書の語彙（辺の型名。SC-09 の管理下）と、実装が閉じた 2 語（`not-found` / `ambiguous`）。
- 🔴 **`Breakdown` の `null` と `[]` は意味が違う。** null＝その指標は軸を持たない（または観測値が 1 件も無い）、
  []＝軸を持つが除外後に 0 件。「0 件と欠落を混同させない」という本 DTO 全体の姿勢と同じである。
- 🔴 **内訳は除外後の行から畳む。** 除外前から畳むと**内訳の合計が件数を超え、個人資料の件数が差分として漏れる。**
  変異試験で実測した（§検証）。
- 列は **NULL 可のまま入れ、backfill しない**。既定値を書き込むと軸を持たない指標に内訳が生える。
  観測値は 1 時間ごとに全量置換されるため、生産側が軸を送り始めれば次周期で埋まる。

### 決定 2: 曖昧一致も**未解決に数え**、軸で理由を分ける

`unresolved-links` は**不在（相手が無い）と曖昧（同名が複数）の両方**を含む。
どちらも辺が作られず、利用者から見れば同じ「繋がっていないリンク」である。
ただし運用の直し方は違う（作る／改名して一意にする）ので、軸で `not-found` と `ambiguous` に分ける。

**却下した案**: 曖昧を除く。—— 除くと「同名の文書を量産する」ことで指標が下がる。
指標が自分の悪化要因で改善するのは測定ではない（IADR-0353 が `UpdatedAt` を却下したのと同じ機序）。

### 決定 3: 🔴 **解決の「失敗」を保存しない。リンク先の「名前」を保存し、集計のたびに解決し直す**

新表 `document_link_targets`（文書 ID × リンク先の名前）を置き、`LinkEdgeSynchronizer` が
取り込みのたびに**その文書ぶんを全量置換**する。`KnowledgeHealthCollector` は収集のたびに
**いま解決できるか**を判定して数える。

**却下した案（素直な実装）**: 解決に失敗したリンクを表へ落とす。

> リンクが解決できるかは**相手の側の事情で変わる**（相手が改名された・削除された）。
> 失敗を保存すると、相手が消えて A の `[[B]]` が壊れても、**A が再取り込みされるまで未解決に
> 数えられない。** リンク切れを数える指標が、**リンク切れの主因を取りこぼす。**

この差は測れる。`KnowledgeHealthNewIndicatorTests` の 2 本
（`相手の改名で壊れたリンクは書いた側を触らなくても未解決になる` /
`相手の削除で壊れたリンクも書いた側を触らずに未解決になる`）は、
**失敗を保存する実装では必ず落ちる。** どちらも改名・削除の前後で対照を取っている。

**規則は 1 か所にしか置かない。** 解決の判定を純粋関数 `LinkTargetMatcher` へ切り出し、
辺を張る側（`LinkEdgeSynchronizer`）と数える側（`KnowledgeHealthCollector`）が**同じものを呼ぶ**。
別々に書くと、片方だけを直したときに**辺は張られないのに未解決にも数えられない**リンクが生まれる ——
指標が測っている対象と実際に辺が作られなかった対象がずれる、最悪の壊れ方である。

**文書の削除では向きで扱いが違う**（対でテストしている）:
消えた文書が**書いた**行は消す（残ると永久に積み上がる）。消えた文書を**指す**行は残す
（あちらの本文はいま壊れたのであり、未解決として数えられるのが正しい）。

**観測値の鍵にリンク先の名前をそのまま入れない。** 名前は文書の題名であり、個人資料の題名でもあり得る。
受け口は鍵を応答に出さないが、**出さないことと持たないことは別である**。
`{文書 ID}:{SHA-256 の先頭 16 バイト}` を鍵にする（同じ組で同じ鍵になれば重複排除は成り立つ）。

### 決定 4: `edge-type-usage` は**両端点のどちらかが個人資料なら** `private-note` を添える

観測値 1 件 ＝ 辺 1 本、軸 ＝ 型名。片側だけを見ると、個人資料から組織文書へ張った辺が
組織の指標へ混ざる。孤立文書数が「辺の相手のスコープを問わない」のは、計画が定義した**文書**の
性質だからであり（IADR-0299 §結果 フォローアップで planning へ照会済み）、**辺そのものの帰属とは別の話**である。

`unresolved-links` 側は**書いている文書のスコープ**で判定する ——
相手は解決できていない（＝どの文書か分からない）ため、相手のスコープは原理的に引けない。

### 決定 5: 生産者の不在は `absent_over_time(...[2h])` で拾う。**`absent()` は使えない**

新しい計器 `knowledge.health.report.total`（タグ `knowledge.indicator`）を置き、
**受け口が受理したときだけ**数える。試みた回数を数えると、受け口が死んでいる間も系列が生き続け、
不在が沈黙する（送出は fail-open である）。

🔴 **なぜ件数ではなく「届けた回数」なのか。** 収集が止まっても受け口の件数は 0 にならない ——
全量スナップショット置換なので**最後に届いた値のまま凍る**。画面上は「安定している」に見え、
**沈黙が正常と読める。** #1246 が名指しした「生産者が居ない指標」の見え方そのものである。

🔴 **IADR-0370 決定 1 の「稼働クラスタの無風時間で決める」は本件に適用しない。**
あちらが対象にしたのは**トラフィック駆動**の系列であり、無風時間はリポジトリの中では決まらない。
本件は**周期駆動**であり、周期は構成から確定する（`KnowledgeHealthHostedService.Interval` = 1 時間）。
`absent()` の既定 5 分 lookback では**平常時のほとんどの時間で真になり、鳴り続ける** ——
IADR-0370 決定 1 が恒常発火を避けるために置いた判定基準を、同じ目的のまま別の手段で満たす。
2 周期ぶんの窓（`[2h]`）＋ `for: 10m` とし、**検知は最大およそ 2 時間 10 分**である。

ルールは本 issue が生産者を置いた **2 指標**に置く。`orphan-documents` / `stale-documents` にも
同じ形を置けるが、それらは別 issue の成果であり、本 PR の射程を広げない。

### 決定 6: SC-10 の健全性節は**開かない**

`unsummarized-clusters` の裁定が出るまで節は閉じたまま、フロントの否定形テストも残す。
**「2/3 揃ったから開ける」は planning#494 の「生産者の無い指標を 0 件として並べてはならない」に反する。**

## 検証（実測）

`dotnet test src/knowledge/backend/backend.slnx --filter "Category!=Integration"` は失敗 0。
新規の**実走**テストは GraphService.Tests **+30 件**（332 → 362）、DashboardService.Tests **+7 件**（57 → 64）。
Skipped は 1 件も増えていない。

### 変異試験

| # | 変異 | 結果 |
| --- | --- | --- |
| 1 | `RunAsync` から新 2 指標の `ReportAsync` を外す | **1 件が落ちる**（生産者の配線を測っている） |
| 2 | `edge-type-usage` のスコープ判定から相手の端点を外す | **1 件が落ちる**（決定 4） |
| 3 | `LinkTargetMatcher` の短絡（exact が複数なら即 ambiguous）を消す | 🔴 **1 件も落ちない** |
| 4 | 内訳を除外前の行から畳む | **1 件が落ちる**（決定 1） |

🔴 **変異 3 は生き残った。そして生き残るのが正しい。**
大文字小文字を無視した一致は ordinal 一致の**上位集合**なので、`exact >= 2` なら `loose >= 2` が必ず成り立ち、
短絡を消しても結論は変わらない。着手時に書いたテスト名（「大文字小文字を無視した段へ降りない」）は
**存在しない境界を主張していた** —— 通っているのに何も守っていない状態だった。
テストの名前と実装のコメントを実測に合わせて直した。

## 測っていないこと（満たしていると読ませない）

- 🔴 **`document_link_targets` は backfill していない。** 既存文書のリンク先は本文からしか復元できず、
  本文の正本は DocumentService にある（ADR-0002）。各文書の次の `DocumentUpdated` で埋まるため、
  **移行直後の `unresolved-links` は過少である。** 0 を「リンク切れ無し」と読まないこと。
- 🔴 **稼働 k3s では測っていない。** この環境から新しい系列を作れない（Docker デーモンも無い）。
  `absent_over_time` の窓は**周期の構成値から導いた**ものであり、稼働環境での実測ではない。
  IADR-0370 §実測 A に相当する実測は本 ADR には無い。
- **Grafana が provisioning を受理するかは測れない**（`check-grafana-alerting.js` 冒頭の既知の穴）。
  配備時に `/api/v1/provisioning/alert-rules` が **11 件**返すことを確かめること（9 → 11）。
- **収集の実行時間・DB 負荷を測っていない。** `edge-type-usage` は辺を全件引く。実データ規模
  （文書 2,368 件）では孤立・陳腐化と同程度と見込むが、実測はしていない。

## 結果

- 生産者のある指標は **2 → 4**（7 指標中）。残る 1 件（`unsummarized-clusters`）は計画の裁定待ちである。
- 観測値モデルは「指標 1 つ＝件数 1 つ」ではなくなった（IADR-0265 の先送りを解いた）。
- リンク解決の規則が 1 か所（`LinkTargetMatcher`）に集まった。

### フォローアップ

- `orphan-documents` / `stale-documents` にも `absent_over_time` を対で置くか（本 ADR の射程外）。
- `unsummarized-clusters` の裁定（クラスタの定義と要約の要否）を planning へ依頼する。
- SC-10 の健全性節を開く条件は、その裁定が出てから決める。
