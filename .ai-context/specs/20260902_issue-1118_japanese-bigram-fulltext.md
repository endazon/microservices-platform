---
title: 作業仕様書 — 全文検索を日本語で引けるようにする（アプリ側 2-gram ペイロードを Qdrant の全文索引に載せる）（#1118）
type: spec
status: done
related_ids:
  - FR-03
  - UC-01
  - SC-01
  - SC-02
  - NFR
  - NFR-01
  - NFR-06
  - NFR-08
  - ADR-0009
  - ADR-0016
  - IADR-0014
  - IADR-0252
  - IADR-0313
  - IADR-0315
  - IADR-0318
  - IADR-0331
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-store-qdrant.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
---

# 作業仕様書: 全文検索の日本語化 — アプリ側 2-gram（#1118）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-03**「キーワードと自然文の双方で横断検索できる（ベクトル検索＋全文検索のハイブリッド）」
- ユースケース（UC）: UC-01（横断検索）／画面: SC-01（検索窓）・SC-02（結果一覧）
- 非機能: NFR-01（検索 p95）・NFR-06（縮退運転）・NFR-08（文書 数万〜数十万件）
- 関連 ADR: **ADR-0009**（ベクトル検索基盤 = Qdrant。**「ハイブリッド検索（ベクトル＋全文）…を扱える基盤」**として選定し、
  フォローアップに「**日本語性能に応じた索引設定**」を残している）／ADR-0016（埋め込みプロバイダ・モデル別コレクション）／
  `06_technical/08_data-egress-policy`（実行時の外部取得の禁止）
- 実装 ADR: [[IADR-0318]]（`multilingual` 採用と索引の後付け）／[[IADR-0315]]（Qdrant 1.18.1）／[[IADR-0014]]（ペイロードキーの表現）／
  [[IADR-0252]]（正負の対照を対で置く）／[[IADR-0313]]（決定的ローカル埋め込みと門）
- 本作業の実装 ADR: **[[IADR-0331]]**（`develop` の最大値 0330 + 1。並行 PR と衝突した場合はマージ直前に改番する）

> 🔴 **issue #1118 は「ADR-0016（ベクトルストアの選定）」と書くが、ベクトルストアの選定は ADR-0009 である。**
> ADR-0016 は埋め込みプロバイダ（Voyage / Ruri）の ADR で、本件が触れるのは ADR-0009 の射程である。
> コミットのスコープは issue の件名に揃えて `ADR-0016` も残すが、判断の根拠は ADR-0009 で引く。

## 目的・背景

#1117（[[IADR-0318]]）で全文ペイロードインデックス（`multilingual`）が張られ、識別子・型番・略語は引けるようになった。
**しかし日本語の語は実配備のチャンクでほぼ引けない**（起票の実測。本書 §実測 1 で測り直した）。
本製品は日本語のナレッジ基盤であり、FR-03 のキーワード側が日本語で成立しないのは要求の半分が欠けている状態である。

## 計画の前提の切り分け（着手前に読んだ結果）

起票は A（形態素解析）／B（N-gram）／C（別エンジン）／D（ベクトルへ寄せる）を挙げ「どれも計画の前提に触れる」と書く。
**計画書を逐語で読んだ結果、B だけは触れない。**

| 道 | 触れる計画の前提 | 判定 |
| --- | --- | --- |
| A 形態素解析（Kuromoji / Sudachi / Lindera） | 辞書の同梱・更新運用が増える。Qdrant の**自前ビルド**（公式イメージは `multiling-japanese` を含まない。§実測 1 が示す挙動どおり）か、取り込み側への辞書依存の追加。`08_data-egress-policy`（実行時の外部取得禁止）に沿わせる設計が要る | **計画の裁定が要る** |
| **B アプリ側で 2-gram を作り、別ペイロードとして Qdrant 自身の全文索引に載せる** | **無い。** ADR-0009 の決定（Qdrant が唯一のベクトル／全文の基盤・ポートで抽象化）を変えず、ADR-0009 が**フォローアップとして実装に残した「日本語性能に応じた索引設定」**そのものである。外部依存・辞書・外部取得を増やさない。契約（`SearchResponse` / openapi）を変えない | **実装裁量** |
| C 別エンジン（OpenSearch / Meilisearch） | ADR-0009「専用ベクトルDB として Qdrant を採用」の射程。運用対象が増える | **計画の裁定が要る** |
| D キーワード側は日本語を諦める | FR-03「キーワードと自然文の双方で」の読み替え | **計画の裁定が要る** |

**既裁定の有無**: `endazon/project-planning` の issue を closed 込みで `全文検索` / `日本語 検索` / `FR-03` / `tokenizer OR multilingual OR 形態素 OR bigram OR N-gram` /
`Qdrant` で検索した。**本件（全文検索の日本語再現率）を扱う issue は open / closed とも 0 件**（陽性対照: 同じ検索で `Qdrant` は #66 / #69 ほか 13 件、
`FR-03` は #197 ほかが当たる＝検索そのものは機能している）。**未裁定だが、B は裁定を要しないので起票しない。**
A / C / D へ進む必要が出たとき（B の精度が実運用で問題になったとき）に初めて `decision-needed` を起票する。

## 母集合の引き直し（着手時に自分で引いた）

`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方 に従い、誤りの側の文字列で引いた
（`git grep -nI`、除外は `src/ai-stock-trading`・`node_modules`・`bin`・`obj`）。

| 軸 | 検索語 | 目的 |
| --- | --- | --- |
| 1 | `FullTextKey\|TextIndexParams\|BuildFullTextIndexParams\|CreatePayloadIndex` | 索引の宣言・生成点と、それを固定しているテスト |
| 2 | `Match \{ Text\|KeywordSearchAsync` | 全文 Match の実装点 |
| 3 | `日本語の再現率\|日本語で引ける\|multilingual` | **「日本語は部分的」と書いている生きた文書**（本作業で古くなる） |
| 4 | `qdrant-fulltext-index\|keyword_degraded` | readiness / メトリクスを説明する文書（新しい check と理由値の追随先） |
| 5 | `verify-qdrant-fulltext-index\|print-keyword-only-query\|KEYWORD_ABSENT_TERM` | 門と検証スクリプトの追随先 |

### 引いた結果と、変更する / しない の別

| 反映先 | 扱い | 理由 |
| --- | --- | --- |
| `src/knowledge/backend/Shared/Knowledge.Contracts/Indexing/CjkBigramPayload.cs` | **新設** | 取り込み側（書く）と検索側（読む）が**同じ 1 つの変換**を使う。ペイロードのキーと符号化はサービス間の契約である（`document_id` / `text` は文字列の複写で済んだが、**関数は複写すると必ず割れる**） |
| `src/knowledge/backend/Shared/Knowledge.Contracts.Tests/CjkBigramPayloadTests.cs` | **新設** | 符号化の純関数を固定（実配備チャンクの実文字列を fixture にする） |
| `src/.../IngestionService/Infrastructure/ExternalServices/QdrantIngestionVectorStore.cs` | **変更** | `BuildChunkPayload` に `text_ngram` を足す。`text_ngram` の索引生成 `EnsureCjkNgramIndexAsync` と**既存点への後付け** `BackfillCjkNgramAsync` を置く |
| `src/.../IngestionService/Domain/Ports/IIngestionVectorStore.cs` | **変更** | 上の 2 メソッドをポートへ（既定実装 no-op。既存の偽物ストアを触らないため） |
| `src/.../IngestionService/Infrastructure/ExternalServices/QdrantBootstrapHostedService.cs` / `QdrantCjkNgramBackfillHostedService.cs`（新設） | **変更 / 新設** | 起動時に索引を張り、後付けは `BackgroundService` が起動後に走らせる（起動を塞がない） |
| `src/.../IngestionService/Tests/Infrastructure/ExternalServices/QdrantCjkNgramIndexTests.cs` | **新設**（#1063 移送後の経路） | 索引が新規・既存とも張られること／backfill が `text_ngram` の無い点だけを埋めること／宣言値 |
| `src/.../RetrievalService/Infrastructure/ExternalServices/QdrantVectorStore.cs` | **変更** | `KeywordSearchAsync` がクエリを CJK / 非 CJK に割り、`text_ngram` / `text` へそれぞれ Match する |
| `src/.../RetrievalService/Infrastructure/ExternalServices/QdrantCjkNgramIndexHealthCheck.cs` | **新設** | `text_ngram` 索引の有無を readiness（Degraded）に載せる。**既存の `QdrantFullTextIndexHealthCheck` は変えない**（既存テストが `text` だけで Healthy を固定している） |
| `src/.../RetrievalService/Common/Observability/KeywordSearchMetrics.cs` | **変更** | 理由値 `missing_ngram_index` を足す（値域 2 → 3） |
| `src/.../RetrievalService/Program.cs` | **変更** | 新 check の登録 |
| `src/.../RetrievalService/Tests/Infrastructure/ExternalServices/QdrantCjkNgramSearchTests.cs` | **新設**（#1063 移送後の経路） | クエリの分割が Scroll のフィルタへ写ること／新 check の Healthy / Degraded |
| `scripts/verify-qdrant-fulltext-index.sh` | **変更** | 段 7 を「数字を出すだけ」から**日本語の陽性・陰性対照の判定**へ格上げ（`text_ngram` を張って測る） |
| `scripts/seed-search-documents.js` / `scripts/verify-oidc-edge-flow.sh` / `scripts/scripts.repo.test.js` | **変更** | 門 S4 の隣に **日本語の語（seed のタイトルから導く）** の段 S6 を足す |
| `docs/functional/FR-03_hybrid-search.md` / `docs/tests/FR-03_hybrid-search.md` / `docs/observability/search-keyword-degradation.md` | **変更** | 「日本語は部分的」の記述を現況へ。新 check・理由値・テスト行の追加 |
| `.ai-context/adr/IADR-0331_*.md` ＋ `README.md` | **新設** | 実装判断の記録 |
| `.ai-context/adr/IADR-0318` / `.ai-context/specs/20260831_issue-1116_*` | **変更しない** | 凍結記録 |
| `src/.../Tests/QdrantFullTextIndexBootstrapTests.cs` / `QdrantFullTextIndexObservabilityTests.cs` | **変更しない** | 🔴 #1063 が全サービスの `Tests/` を移送中。既存テストファイルを触らない。**この制約が設計を決めた**（`EnsureCollectionsAsync` へ足すと既存の `OnlyContain(FieldName == text)` が割れる → 別メソッド／既存 check へ足すと `text` だけで Healthy の固定が割れる → 別 check） |
| `docs/api/openapi.yaml` / `SearchResponse` | **変更しない** | 契約は 1 バイトも変えない |
| `deploy/**` / helm values | **変更しない** | 索引もペイロードも取り込みサービスが起動時に収束させる。配備の値に依存しない |

## 対象範囲

- 対象: 日本語（CJK）の語による全文検索の再現率／既存点への後付け／縮退の可観測化／門と実機検証／記録。
- 対象外: 形態素解析（A）・別エンジン（C）・FR-03 の読み替え（D）・`tags` / `attributes.*` の索引・nDCG の評価・`Match::Phrase`。

## 実測

環境: 稼働 k3s（Rancher Desktop）`platform-infra/qdrant`（`qdrant/qdrant:v1.18.1`）へ `kubectl port-forward svc/qdrant 6333:6333`。
**稼働コレクションは読むだけ**（`knowledge_chunks_deterministic_v1` の 3 点を `scroll` で取り、その本文をそのまま使い捨てコレクション
`issue1118_probe` へ入れて張り替えた）。稼働コレクションの索引・データには触れていない。
実行したのは `probe-1118.mjs`（本 PR の本文に生出力を載せる）。

### 実測 1: 現行（`text` / `multilingual`）は実配備チャンクの日本語をほぼ引けない（陰性）

実配備の 3 チャンク（185 / 233 / 129 文字。日本語＋識別子＋記号の長文）に対し、**チャンクに実在する日本語 25 語**を引いた。

```
=== text: multilingual min1 max40 lower (現行)
   日本語(在る): 文書=0 検索=0 導線=0 検証=0 統合=0 横断検索=0 観測=0 投入=0 合言葉=0 取り込み=1 索引=0 経路=0
                本文=0 格納=0 発行=0 埋め込み=0 登録=0 解決=0 早期=0 捨てる=0 チャンク=0 オブジェクトストレージ=0
                ハイブリッド検索=0 検索導線の検証用文書=0 壊れている=0
   1文字: 本=0 文=0 索=0
   全 2-gram 再現率: 1/176        ← チャンクに実在する 2-gram 176 種のうち当たるのは 1 つ
   識別子(在る): IngestionService=2 MarkdownUri=2 DocumentUpdatedConsumer=1 abac=1 tanpopo searchseed msp=1
```

**25 語中 1 語**（`取り込み`。改行と `（` に挟まれて単独の run になっている語だけ）。`max_token_len=400` にしても同じ（1/176）。
**つまり公式イメージの `multilingual` は日本語を分かち書きしておらず、語で当たるかは連なりの切れ目次第である**
（Qdrant のビルドオプション `multiling-japanese` が入っていないときの挙動。短い文で `索引` `本文` は当たり
`合言葉` は当たらない）。**識別子は当たる（陽性対照）。**

`word` は 1/176、`whitespace` は 0/176 でしかも `MarkdownUri`（バッククォート付き）を落とす。`prefix` は 25/176（語頭のみ）。

### 実測 2: アプリ側 2-gram を別ペイロードに載せると、同じチャンク・同じ語で全て当たる（陽性）

`text_ngram` = CJK の連なりごとに 2-gram を空白区切りで並べた文字列（1 文字の run は 1-gram のまま）。
クエリ側も同じ変換をして `Match { Text }` する（Qdrant の全文 Match は**全トークンの存在**を要求するので、部分文字列に近い意味論になる）。

```
=== text_ngram(app bigram): prefix min1 max2
   日本語(在る): 文書=2 検索=2 導線=1 検証=1 統合=1 横断検索=1 観測=1 投入=1 合言葉=1 取り込み=1 索引=2 経路=1
                本文=2 格納=1 発行=1 埋め込み=1 登録=1 解決=1 早期=1 捨てる=1 チャンク=1 オブジェクトストレージ=1
                ハイブリッド検索=1 検索導線の検証用文書=1 壊れている=1
   日本語(在らない): 零細企業=0 月餅=0 東京都=0 形態素解析=0 株価=0     ← 陰性対照
   1文字: 本=2 文=3 索=3
   全 2-gram 再現率: 176/176
```

**25/25 語・176/176 の 2-gram。陰性対照 5 語は 0 件。** `whitespace` / `word` でも 25/25・176/176 だが **1 文字の語が 0 件**になる。
`prefix`（`max_token_len=2`）は 2-gram の 1 文字接頭辞も索引に持つので **1 文字の語も当たる**。これを採る。

### 実測 3: 識別子の系統は変えないので落ちない（#1117 の獲得物）

`text` の索引は `multilingual` のまま据え置き、非 CJK のクエリ断片はこれまでどおり `text` へ Match する。
実測 1 の識別子行がそのまま成立する（`IngestionService=2` / `tanpopo searchseed msp=1` / `anpop=0` / `estionServ=0`）。

## 設計（詳細は [[IADR-0331]]）

### 決定 1: `text` は `multilingual` のまま、CJK は別ペイロード `text_ngram` の 2-gram で引く

- `CjkBigramPayload.PayloadKey = "text_ngram"`。`Encode(text)`: CJK（Han / Hiragana / Katakana / `ー々〆〤`）の連なりごとに 2-gram（1 文字の run は 1-gram）を空白区切りで並べる。
- 索引: `tokenizer=prefix, min_token_len=1, max_token_len=2, lowercase=true`。
- クエリ: `SplitQuery(query)` → `(nonCjk, ngram)`。`nonCjk` が非空なら `text` へ、`ngram` が非空なら `text_ngram` へ、**両方 `must`**。

### 決定 2: 索引は起動時に無条件・冪等に張り、既存点は起動後に backfill する

- `EnsureCjkNgramIndexAsync`: 各コレクションに `text_ngram` の索引を張る（[[IADR-0318]] 決定 2 と同じ「存在の有無によらず毎回」）。
- `BackfillCjkNgramAsync`: `is_empty text_ngram` の点を scroll し、`text` から `Encode` して `UpdateBatch(SetPayload)` で埋める。
  **2 回目以降の起動は 0 件走査で終わる**（埋まった点は条件に当たらない）。起動を塞がない（`BackgroundService`）。
- 🔴 **`EnsureCollectionsAsync` には足さない。** 既存テスト `OnlyContain(FieldName == text)` を触れないため（#1063）。ホストは 2 つを順に呼ぶ。

### 決定 3: 縮退の可観測化は既存と同型（新 check ＋ 理由値 1 つ）

- `QdrantCjkNgramIndexHealthCheck`（`qdrant-cjk-ngram-index`）: `text_ngram: Text` が無ければ **Degraded**（Unhealthy にしない。NFR-06）。
- `KeywordSearchMetrics.MissingNgramIndexReason = "missing_ngram_index"`。

### 決定 4: 門は日本語の語を seed のタイトルから導く（合言葉を増やさない）

`seedJapaneseKeywordQuery(seed)` = seed タイトルの**最初の CJK の連なり**（`検索導線の検証用文書`）。
タイトルは本文の H1 としてチャンクに入る（`documentsMissingProbeTerm` と同じ理由で seed 側が保証する）。
段 S6: `mode=keyword` でこの語を引き seed が当たること。陰性対照は S5（`msp-absent-zzzznotexistword`）に加え、**日本語の在らない語**を同じモードで 0 件。

## 受け入れ基準（issue 本文が正）

- [x] 1. **実配備と同型のチャンク**（実配備そのもの）で日本語の語が当たる（§実測 2: 25/25 語・176/176 の 2-gram）
- [x] 2. 陽性対照と陰性対照を対で置く（§実測 2: 在る 25 語 ≥ 1 件／在らない 5 語 = 0 件。`verify-qdrant-fulltext-index.sh` 段 7 が
      毎回判定する —— 実機で **PASS 9 / FAIL 0**。同じ語の `text` 側の件数を併記し対比を残す）
- [x] 3. 識別子・型番・略語の再現率を落とさない（`text` の系統は条件が不変 —— 単体 `BuildFullTextConditions_IdentifierOnly_IsUnchangedFromTheMultilingualPath`
      が固定。実機の変異試験（§実測 4）: `text_ngram` ペイロードを持たない点＝本 PR 以前の姿では**日本語だけ 0 件で識別子は当たり続ける**）
- [x] 4. 採った道（B）の根拠を [[IADR-0331]] に残す（計画の裁定は不要と判定した根拠を含む）

### 実測 4: 変異試験（実配備チャンク 3 点・使い捨てコレクション）

`mutate-1118.mjs`（生出力は PR 本文）。(a) 両索引＋両ペイロード／(b) 変異: `text_ngram` ペイロード無し（本 PR 以前の点の姿）／
(c) 変異: `text_ngram` の索引だけ削除。**(b) で日本語だけが 0 件になり識別子は当たり続ける**ことが受け入れ基準 3 の証跡。
**(c) は索引なしの部分文字列走査へ落ちて「当たっているように見える」**（[[IADR-0318]] と同型）ので、件数ではなく
readiness（`qdrant-cjk-ngram-index` Degraded）で検出する（決定 3）。

## テスト方針

| # | 何を | どこで |
| --- | --- | --- |
| U-1 | `Encode` / `SplitQuery` の純関数（実配備チャンクの実文字列を fixture に、期待する 2-gram 列を固定） | `Knowledge.Contracts.Tests` |
| U-2 | `EnsureCjkNgramAsync` が既存・新規の両コレクションへ `text_ngram` の索引（`prefix`・1..2・lowercase）を張る | `IngestionService.Tests/Infrastructure/ExternalServices/` |
| U-3 | `BackfillCjkNgramAsync` が `is_empty` フィルタで scroll し、返った点にだけ `SetPayload` を出す／0 件なら書かない | 同上 |
| U-4 | `BuildChunkPayload` が `text_ngram` を書く（`text` と同じ本文から） | 同上 |
| U-5 | `KeywordSearchAsync` が CJK / 非 CJK を割って 2 条件（`text` / `text_ngram`）を出す。CJK だけのクエリは `text` 条件を出さない | `RetrievalService.Tests/Infrastructure/ExternalServices/` |
| U-6 | 新 check の Healthy / Degraded / 到達不能 | 同上 |
| U-7 | `seedJapaneseKeywordQuery` の導出と門 S6 の存在 | `scripts/scripts.repo.test.js` |
| E-1 | 実機 Qdrant で日本語の陽性・陰性対照 | `scripts/verify-qdrant-fulltext-index.sh` 段 7（opt-in） |
| E-2 | 統合スタックの門 | `scripts/verify-oidc-edge-flow.sh` 段 S6 |

## 既知の限界（隠さない）

- **2-gram は部分文字列一致に近い**（`京都` は `東京都` に当たる）。精度は形態素解析に劣るが、再現率を優先する。ハイブリッド既定では RRF がベクトル側と融合するので単独の誤ヒットが上位を占めにくい。
- **助詞を含むクエリはその並びのまま要求する**（`検証の文書` は `検証用文書` に当たらない）。言い換えは意味検索の側が受け持つ。
- 索引は 2-gram の分だけ増える（1 チャンクあたり CJK 文字数に比例。実配備 3 点で 84 / 84 / 45 トークン）。

## 測れなかったもの（隠さない）

| 測れなかったもの | 理由 |
| --- | --- |
| 稼働 Pod（ingestion / retrieval）を新イメージで再起動しての通し | 🔴 同じクラスタで #1110 / #1102 / #1115 が並行実測中で、**Qdrant 以外の Pod を再起動しない**制約がある。代わりに、実配備チャンクの実文字列＋アプリと同じ索引パラメータ・同じ符号化で使い捨てコレクションに対して測った（§実測 2）。Pod 再起動を伴う通しはマージ後の再測に残す |
| `verify-oidc-edge-flow.sh` の通し実行 | 稼働クラスタの `developer` は TOTP 登録済みで段 4 が止まる（#1117 と同じ環境状態） |

## 計画書との差異

- 差異: なし（ADR-0009 のフォローアップ「日本語性能に応じた索引設定」を実装で埋めた。FR-03 の文言は変えない）。

## 未決事項

- 2-gram の精度が実運用で問題になったら、A（形態素解析つきビルド）／C を `decision-needed` で planning へ掛ける。本作業では掛けない。
