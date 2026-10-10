---
title: 検索の応答に「縮退したか」と理由の符号を載せる
type: spec
status: done
related_ids: [FR-03, FR-04, NFR-06, ADR-0016, ADR-0018, ADR-0035, ADR-0127, IADR-0009, IADR-0313, IADR-0318, IADR-0379, IADR-0498, IADR-0534]
author: claude
created: 2026-10-11
updated: 2026-10-11
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-03 / NFR-06「障害時の縮退運転: 検索は継続」）
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md
issue: "#1871"
---

# 仕様書: 検索の応答に「縮退したか」と理由の符号を載せる（#1871）

## 起点となる計画書（トレーサビリティ）

- FR-03（ハイブリッド検索）・NFR-06（障害時の縮退運転。検索は継続する）。
- ADR-0016（埋め込みの経路）・ADR-0018（着脱可能な段）・ADR-0035（二段検索）・ADR-0127（再順位付けの段）。
- 起点: AST#1283 の監査（AST#1287）。PoC では Voyage の鍵が無く、クエリの埋め込みが空ベクトルで返り続けた。
  検索は 200 で語彙検索だけの結果を返し、AST からは「正常に両系統で引いた」と見分けられなかった。
- 実装判断の記録: [IADR-0534](../adr/IADR-0534_search-response-degraded-signal.md)。

## 受け入れ基準（issue より）と写像

| # | 基準 | 写像（テスト） |
| --- | --- | --- |
| A1 | 応答に縮退の有無（`degraded`）と、理由の固定語彙の符号を機械可読で載せる | `SearchDegradationTests`（サービス層）・`HybridSearchEndpointTests`（REST）・`GrpcDocumentSearchTests`（gRPC） |
| A2 | 理由に本文・URL・資格情報・例外メッセージを含めない | 符号は `SearchDegradedReasons.All` の 4 値に閉じる（`SearchDegradationTests` が値域を固定）。gRPC は enum |
| A3 | openapi と生成物を同時に更新。追加のみ（後方互換） | `docs/api/openapi.yaml` の `SearchResponse` へ 2 項目を追加、orval 生成物を再生成。proto は番号 2・3 の追加のみ（`check-proto-contracts.js`） |
| A4 | 縮退の計数があれば揃える。無ければ足すかを判断して記録 | 埋め込みの縮退に計数は無かった（ログのみ）。`search.degraded.total{search.degraded_reason}` を足し、値域を応答の符号と同一にする（`SearchDegradationTests`） |
| A5 | AST 側の取り込みは AST へ別起票 | 本 PR に含めない（報告に起票番号を記す） |

## 理由の符号（固定語彙）

issue の例（`embed-failed`）に合わせ、小文字のケバブケースとする。gRPC は同じ集合を enum で持つ。

| 符号（REST） | gRPC enum | 起きる条件 | 結果への影響 |
| --- | --- | --- | --- |
| `embed-failed` | `SEARCH_DEGRADED_REASON_EMBED_FAILED` | 主コレクションのクエリ埋め込みが空ベクトル（送信拒否・鍵なし・次元不整合・ゲートウェイ側の失敗） | hybrid は語彙検索のみ。semantic は 0 件 |
| `fused-embed-failed` | `SEARCH_DEGRADED_REASON_FUSED_EMBED_FAILED` | ベクトルの系統を持つ追加コレクションのどれかの埋め込みが空ベクトル | そのコレクションは語彙検索のみ |
| `graph-expand-failed` | `SEARCH_DEGRADED_REASON_GRAPH_EXPAND_FAILED` | 二段検索の段が**登録されていて**、近傍の取得・辺の型の辞書の取得が失敗した、または利用者文脈が無く呼べなかった | 一次の結果のまま（または辞書のフォールバック重み） |
| `rerank-failed` | `SEARCH_DEGRADED_REASON_RERANK_FAILED` | 再順位付けの段が**登録されていて**、掛けようとして元の順へ戻した（時間切れ・輸送・送信拒否・拒否・解釈不能） | 再順位付けなしの順 |

### 縮退に数えないもの（理由）

- **段が構成で無効**（グラフ展開・再順位付けの既定オフ）。ADR-0018 の「着脱可能な段」の既定であり、故障ではない。
  数えると既定構成の全検索が `degraded=true` になり、印が意味を失う。
- **段が設計どおり掛けない**（再順位付けの `skipped`: 日時順・semantic・合成監視・候補不足。起点 0 件・グラフ 0 件）。
- **語彙索引（`LexicalOnly`）にベクトルが無いこと**（ADR-0127 決定 2 の設計どおり）。
- **全文（キーワード）側の縮退**（`search.keyword_degraded.total`）。`IVectorStore` のポートが縮退を返さず、
  載せるには全実装（Qdrant・InMemory・試験の替え玉）の契約を変える必要がある。本 PR の射程外とし、残課題に記す。
- **埋め込みの輸送例外**（gRPC 不達）。従来どおり例外として上がり、検索そのものが失敗する（IADR-0256 決定 3）。縮退ではない。

## 「索引が空」「対象が索引に無い」の符号を足さない（PoC の実測を受けた検討）

PoC（2026-10-11）では AST の `internal` の報告書が keyword でも hybrid でも 0 件だった。コードで確かめると、取り込みは
`public` / `internal` の文書を**埋め込めたチャンクだけ**書く（全文索引は同じ点のペイロード）。鍵が無いと `Retryable=true` で
再試行の後 DLQ へ送られ、語彙索引へも回らない（IADR-0497 決定 2）。検索側の「全文のみで続行」は、埋め込みが働いていた時期に
索引された文書にしか効かない。これは #1908 に起票した（本 PR には含めない）。

そのうえで、応答へ次の符号を足すかを検討し、足さないと決めた。

- **対象の文書が索引に無い**: 文書単位の有無は、権限が無いのか該当が無いのかの区別そのものである（IADR-0009 / IADR-0313 決定 1）。足せない。
- **索引が空**（コレクションの点数 0）: 検索ごとに点数を数える往復が増え、全体に文書が 1 件も無いことを許可のある全利用者へ示す。
  原因（鍵待ちで DLQ に溜まっている）も応答からは分からない。運用側の観測（DLQ の件数・コレクションの点数）で補う方が効く（#1908 の問い 3）。
- 現実の PoC では `embed-failed` が立つ。取り込みも同じゲートウェイで埋め込むので、`embed-failed` が続いている配備では
  新規の `public` / `internal` 文書も索引に入っていない —— この読み方を FR-03 の機能仕様書と IADR-0534 に書く。

## 存在秘匿との関係（IADR-0313 決定 1 / IADR-0009）

IADR-0313 決定 1 は「応答へ『なぜ空か』を載せない」とした。守る対象は**権限が無いのか該当が無いのか**の区別である。
本 PR の符号は**系の部品の健全性**（埋め込み・グラフ・再順位付け）だけを表し、文書単位の権限や該当の有無を表さない。

- スコープ無し・`GrantsAccess=false` の deny と空クエリは**埋め込みより前に**空で返るので、`degraded=false` のまま返る。
  これで読めるのは「呼び出した本人が許可ポリシーを 1 つでも持つか」（本人の権限状態）だけであり、他人の文書の存在ではない。
- 絞り込み（`AttributeFilters`）との交差が空の deny は、フィルタが `DenyEverything` になるだけで埋め込みを呼ぶ。
  埋め込みが壊れていれば `embed-failed` が立つが、許可スコープの中身に依らず一様に立つので秘匿の点では無害である。
- 許可がある検索では、該当の有無に依らず埋め込みは同じように呼ばれる。`embed-failed` / `fused-embed-failed` / `rerank-failed` は該当の有無で変わらない。
- ［2026-10-11 追記 / #1871 監査 🟡-1］**`graph-expand-failed` だけは例外**である。近傍展開の起点は露出で落とす前のベクトル側ヒットから取るので、
  段が有効（既定オフ）でグラフが故障しているとき、露出 OFF の文書（本人は ABAC 上読める）だけが当たると「結果は空なのに印が立つ」。
  読めるのは本人が読める範囲に露出 OFF の文書があることだけで、権限の有無・他人の文書は漏れない。起点を露出の後へ移す案は並びが変わるので採らない
  （IADR-0534 §結果）。試験で挙動を固定した。

したがって IADR-0313 決定 1 の守る線は崩れない。同決定が退けた「案 4（縮退の別を載せる）」の一部を、
理由の範囲を部品の健全性に限って採ることを IADR-0534 に記録する。

## 変更

1. 契約
   - `Knowledge.Contracts/Dtos/SearchDto.cs`: `SearchResponse` に `DegradedReasons`（`List<string>`、既定は空）と
     `Degraded`（`DegradedReasons` が空でないこと。計算値）を足す。位置引数は変えない。
     符号の定数 `SearchDegradedReasons` を置く。
   - `document_search.proto`: `SearchResponse` に `bool degraded = 2;` と `repeated SearchDegradedReason degraded_reasons = 3;`、
     enum `SearchDegradedReason` を足す（既存番号は不変）。`scripts/proto-contract-baseline.json` を `--update` で更新する。
   - `docs/api/openapi.yaml`（手書きの正。生成器は無い）: `SearchResponse` に 2 項目を足し、`check-openapi-dto-drift.js` で突き合わせる。
     orval 生成物（`src/platform/frontend/src/lib/api/generated/`）を `pnpm run codegen` で再生成する。
2. 検索サービス
   - `HybridSearchService`: 中間値（`HybridSearchOutcome`）に縮退の符号を持たせ、唯一の出口 `FinishAsync` が
     再順位付けの縮退を足して `HybridSearchResult` を返す。計数もここで 1 度だけ記録する。
   - `GraphExpandingSearchService`: 近傍展開の縮退を足して出口へ渡す。
   - ポート: `ISearchReranker` は `RerankOutcome`（並び＋縮退したか）を返す。`GraphNeighborhood` に `Degraded` を足す。
   - `SearchEndpoint.ExecuteAsync`（REST・gRPC・MCP の唯一の検索）は `HybridSearchResult` を返す。
     REST・gRPC は応答へ写す。MCP のツール応答は変えない（契約外）。
3. 観測: `SearchDegradationMetrics`（`search.degraded.total`、タグ `search.degraded_reason`、値域は符号と同一）。
   既存の警告ログ（`WarnEmbeddingUnavailable` 等）と `search.rerank.total` はそのまま残す。

## 母集合（規則 9・10）

- `SearchResponse` を作る・読む箇所: `grep -rn "SearchResponse" src --include=*.cs`（RetrievalService の REST・BFF・試験）。
  BFF は Retrieval の応答を DTO で読み直して返すので、項目は素通りする（早期の空応答は `degraded=false`）。
- `IHybridSearchService` の実装: `HybridSearchService` / `GraphExpandingSearchService`、試験の替え玉 2 つ（`GraphExpansionTwoStageSearchTests`・`PrivateNoteAiInputPurposeTests`）。
- `ISearchReranker` の実装: `ClaudeSearchReranker`、試験の替え玉（`PrivateNoteAiInputPurposeTests`・`ClaudeRerankTests` 系）。
- 「応答へは載せない」と書いた既存の注記: `KeywordSearchMetrics.cs`・`RerankMetrics.cs`・`HybridSearchService.cs`（`WarnEmbeddingUnavailable`）・`Program.cs`。
  全文側（キーワード）の記述は**今も正しい**（本 PR は全文側を載せない）ので変えない。埋め込みの注記だけを改める。

## 配備で変わること

- AST が呼ぶ REST `POST /search` の応答 JSON に `degraded` / `degradedReasons` が増える。Voyage の鍵が無い PoC では
  `{"degraded": true, "degradedReasons": ["embed-failed"]}` になる。AST の現行の読み手は未知の項目を無視する。
- `POST /bff/search` も同じ項目を返す（BFF は素通し）。SC-02 の画面は読まない（表示は後続）。
- メトリクス `search.degraded.total` が増える（0 が正常）。

## 残課題

- AST 側の取り込み（`ragContext` へ反映）: AST へ起票。
- 全文側の縮退を応答へ載せるか（`IVectorStore` の契約変更）: 必要になれば MSP へ起票。
- 埋め込みが使えない間、`public` / `internal` の文書が全文検索にも現れない件: #1908（裁定待ち）。
