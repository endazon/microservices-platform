---
title: IADR-0534 検索の応答に縮退の印（degraded と理由の固定語彙）を足す —— 部品の健全性だけを表し権限の有無は表さないので IADR-0313 決定 1 の線は崩さず、構成で無効な段と全文側は数えず、計器を同じ符号で揃える
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-04, NFR-06, ADR-0016, ADR-0018, ADR-0035, ADR-0127, IADR-0009, IADR-0256, IADR-0313, IADR-0318, IADR-0379, IADR-0467, IADR-0497, IADR-0498]
author: claude
created: 2026-10-11
updated: 2026-10-11
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-03 / NFR-06「障害時の縮退運転: 検索は継続」）
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
related_specs:
  - ../specs/20261011_1871_search-degraded-signal.md
---

# IADR-0534: 検索の応答に縮退の印を足す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-10-11
- 決定者: Claude（実装）／起点は #1871（AST#1283 の監査・AST#1287）

## 起点・関連

- 関連する計画書 ID: FR-03（ハイブリッド検索）・NFR-06（障害時の縮退運転。検索は継続する）・ADR-0016（埋め込みの経路）・
  ADR-0018（着脱可能な段）・ADR-0035（二段検索）・ADR-0127（語彙索引と再順位付け）。
- 一部を改める実装判断: [IADR-0313](./IADR-0313_deterministic-local-embedding-for-search-gate.md) **決定 1**
  （「応答へ『なぜ空か』を載せない」。同 IADR が退けた案 4「縮退の別を応答へ載せる」を、理由を部品の健全性に限って採る）。
- 変えない実装判断: [IADR-0318](./IADR-0318_qdrant-fulltext-payload-index.md) 決定 3（全文側の縮退は計器で観る。応答へは載せない）・
  [IADR-0256](./IADR-0256_embedding-degradation-vs-backend-failure.md) 決定 3（埋め込みの輸送失敗は例外として上げる）。
- 作業仕様書: [20261011_1871_search-degraded-signal](../specs/20261011_1871_search-degraded-signal.md)（受け入れ基準の写像・母集合）。

## コンテキストと課題

検索は、クエリ埋め込みが空ベクトルで返ると意味検索の系統を落とし、語彙検索だけで 200 を返す（#995）。
PoC では Voyage の鍵が無いあいだ、AST の RAG はすべてこの形で返っていた。応答には縮退の有無が無く、
呼び出し元（AST）からは「両系統で正常に引いた結果」と見分けられなかった。手掛かりは検索サービスの警告ログだけで、
埋め込みの縮退には計器も無かった。

印を応答へ載せるには、3 つを決める必要があった。(1) 何を縮退と数えるか、(2) IADR-0313 決定 1（存在秘匿）との関係、
(3) 符号の形（REST・gRPC・計器）。

## 検討した選択肢

### 何を数えるか

1. **埋め込みだけ** —— issue の事象は塞げるが、再順位付けとグラフ展開も「失敗しても 200 で劣化した結果を返す」同じ形である。
   後から足すと語彙が 2 度変わる。
2. **部品が働かず結果が劣化したものすべて（埋め込み・追加コレクションの埋め込み・グラフ展開・再順位付け）**（採用） ——
   全部が検索サービスの出口（`FinishAsync`）へ集まるので、印を作る点を 1 つにできる。
3. **2 に加えて全文側（キーワード）** —— `IVectorStore` のポートは縮退を返さず、載せるには全実装（Qdrant・InMemory・試験の替え玉）の
   契約を変える必要がある。全文側は readiness と `search.keyword_degraded.total` で既に観測できている（IADR-0318）。

### 存在秘匿との関係

IADR-0313 決定 1 が守るのは「権限が無いのか該当が無いのかを区別させない」ことである。
deny の経路は 2 つあり、印の出方が違う。

- **スコープ無し・`GrantsAccess=false`**（許可ポリシーが無い）と空クエリは、埋め込みより前に空で返る。`degraded=false`。
- **絞り込み（`AttributeFilters`）との交差が空**のときは、フィルタが `DenyEverything` になるだけで埋め込みは呼ぶ。
  埋め込みが壊れていれば `embed-failed` が立つ。これは許可スコープの中身に依らず一様に立つので、秘匿の点では無害である。

許可がある検索は、該当の有無に依らず同じように埋め込みを呼ぶ。したがって `embed-failed` / `fused-embed-failed` /
`rerank-failed` は、**文書の存在**も**該当の有無**も表さない。読めるのは「呼び出した本人が許可ポリシーを 1 つでも持つか」
（埋め込みが壊れているときに限り、前者の deny は `false`・許可ありは `true`）だけであり、これは本人自身の権限状態である。
例外は `graph-expand-failed` で、§結果の残余リスクに書く。

### 符号の形

1. 例外メッセージやゲートウェイの `RoutingReason` をそのまま載せる —— 本文・宛先・設定値が混ざり得る。不採用。
2. **固定語彙の短い符号**（採用）。issue の例（`embed-failed`）に合わせて小文字のケバブケース。gRPC は同じ集合を enum で持つ。
   計器のタグも同じ値にする（呼び出し元が見た符号でダッシュボードを引ける）。

## 決定

### 決定 1: 応答に `degraded`（bool）と `degradedReasons`（固定語彙）を足す。追加のみ

- REST `SearchResponse`（`Knowledge.Contracts`）: init のメンバーとして足す（位置引数は変えない）。2 つは `WithDegradation` の 1 か所だけで揃えて設定する。
- gRPC `knowledge.retrieval.v1.SearchResponse`: `bool degraded = 2;`・`repeated SearchDegradedReason degraded_reasons = 3;`。既存の番号は変えない。
- BFF `POST /bff/search` は DTO を読み直して返すので素通しになる。`docs/api/openapi.yaml` と orval 生成物を同じ PR で更新する。
- MCP のツール結果には載せない（ツールの契約外。計器とログには残る）。

### 決定 2: 語彙は 4 値に閉じる

| 符号 | gRPC | 条件 |
| --- | --- | --- |
| `embed-failed` | `SEARCH_DEGRADED_REASON_EMBED_FAILED` | 主コレクションのクエリ埋め込みが空ベクトル |
| `fused-embed-failed` | `SEARCH_DEGRADED_REASON_FUSED_EMBED_FAILED` | ベクトルの系統を持つ追加コレクションのクエリ埋め込みが空ベクトル |
| `graph-expand-failed` | `SEARCH_DEGRADED_REASON_GRAPH_EXPAND_FAILED` | 二段検索の段が登録されていて、近傍・辺の型の辞書が引けない、または利用者文脈が無く呼べない |
| `rerank-failed` | `SEARCH_DEGRADED_REASON_RERANK_FAILED` | 再順位付けの段が登録されていて、元の順へ戻した |

並びはこの表の順、重複しない。本文・URL・資格情報・例外メッセージ・コレクション名は載せない。足すときは REST の定数と proto の enum を
同時に足す（写しの網羅は gRPC の試験が固定する）。

### 決定 3: 縮退に数えないもの

- 構成で無効な段（グラフ展開・再順位付けの既定オフ。ADR-0018）。数えると既定構成の全検索が `degraded=true` になり、印が意味を失う。
- 設計どおり掛けない段（再順位付けの skipped・起点 0 件・グラフに辺が無い・`found=false`）・語彙索引にベクトルが無いこと（ADR-0127 決定 2）・
  keyword モード（埋め込みを呼ばない）・辞書に無い辺の型（辞書は引けている）。
- 全文側の縮退（IADR-0318 決定 3 のまま）。
- 埋め込みの輸送例外（IADR-0256 決定 3。検索そのものが失敗する）。
- deny・空クエリ（部品を呼ばない。存在秘匿の線）。

### 決定 4: 計器 `search.degraded.total` を足し、タグの値域を応答の符号と同一にする

埋め込みの縮退には計器が無かった（ログのみ）。応答の印は呼び出し元にしか見えないので、運用が同じ事実を読めるよう
`search.degraded.total{search.degraded_reason=<符号>}` を足す（0 が正常。理由ごとに 1 つ数える）。記録は出口（`FinishAsync`）の 1 か所で行う。
既存の `search.rerank.total`（細かな理由つき）・`search.keyword_degraded.total` は残す。

### 決定 5: ポートの形

- `IHybridSearchService.SearchWithDegradationAsync` を足し、入口（`SearchEndpoint.ExecuteAsync`）はこれを呼ぶ。**既定実装を置かない**
  （新しい実装が書き忘れたとき「縮退なし」と黙って答えさせない）。従来の `SearchAsync` は結果だけを返す口として残す。
- `ISearchReranker.RerankAsync` は `RerankOutcome`（並び＋縮退したか）を返す。
- `GraphNeighborhood` に `Degraded` を足す（失敗を例外にしない実装が、空と失敗を区別して運ぶため）。

## 理由

- 検索は NFR-06 のとおり部品が落ちても続くので、「結果が劣化した」ことは応答に載せないと呼び出し元には分からない。
- 印の範囲を部品の健全性に限れば、IADR-0313 決定 1 が守る区別（権限が無い／該当が無い）を崩さずに済む。
- 符号を固定語彙にすると、呼び出し元は分岐でき、計器と同じ語で運用と会話できる。秘密や本文が混ざる余地も無い。

## 結果

- **良い影響**: PoC で `{"degraded": true, "degradedReasons": ["embed-failed"]}` が返り、AST が「語彙検索だけで返った」と記録できる
  （取り込みは AST 側の後続）。運用は `search.degraded.total` で同じ事実を観る。
- **悪い影響 / トレードオフ**:
  - 埋め込みが壊れているときに限り、本人が読み取りの許可を 1 つでも持つかが印から読める（本人の権限状態であり、他人の文書の存在ではない）。
  - 🟡 **`graph-expand-failed` は「露出 OFF の文書が範囲内にあるか」を示し得る**（#1871 監査）。近傍展開の起点は
    **露出（`DocumentExposure`）で落とす前**のベクトル側ヒットから取る。段が有効（既定はオフ）で、かつグラフが故障しているとき、
    露出 OFF の文書だけが当たると「結果は空なのに `graph-expand-failed` が立つ」。ここから読めるのは、本人が ABAC 上読める文書のうち
    露出 OFF のものが範囲内にあることだけである。権限の有無や他人の文書は漏れない（ABAC は起点の前に効いている）。
    `SearchDegradationTests` の「露出OFFの文書だけが当たりグラフが故障していると…」がこの挙動を固定する。
  - 起点を露出の後の集合から取る案は採らなかった。起点が変わると近傍の加点が変わり、段が健全なときの並び（ランキング）が変わるためである。
  - 全文側の縮退は応答に載らない。AST が語彙検索だけで返っている今、全文側も落ちると `degraded=false` のまま 0 件になり得る
    （readiness と計器では見える）。必要になれば `IVectorStore` の契約変更として別に扱う。
  - BFF も同じ項目を返す。SC-02 の画面は読まない（表示は別に扱う）。
  - 🔴 **`embed-failed` は「語彙検索なら当たる」を意味しない。** 取り込みも同じゲートウェイで埋め込み、`public` / `internal` の文書は
    埋め込めないと点ごと書かれない（鍵待ちは再試行の後 DLQ。IADR-0497 決定 2 で語彙索引へも回さない）。`embed-failed` が続く配備では、
    その間に入った文書は全文検索にも現れない（#1908 で裁定を求めた）。
  - 「索引が空」「対象が索引に無い」の符号は足さない。後者は存在秘匿（IADR-0009）を崩し、前者は検索ごとの点数の往復が増えるうえ
    全体の文書の有無を示す。運用側の観測（DLQ の件数・コレクションの点数）で補う（#1908 の問い 3）。
- **フォローアップ**: AST 側の取り込み（`ragContext` への反映）は AST の issue で扱う。全文だけの点を書くかは #1908。

## 関連

- [IADR-0313](./IADR-0313_deterministic-local-embedding-for-search-gate.md) 決定 1（本 IADR が理由の範囲を限って一部を改める）
- [IADR-0318](./IADR-0318_qdrant-fulltext-payload-index.md) 決定 3（全文側は変えない）
- [IADR-0498](./IADR-0498_claude-rerank-stage-at-search-exit.md)（再順位付けの段。出口 `FinishAsync`）
