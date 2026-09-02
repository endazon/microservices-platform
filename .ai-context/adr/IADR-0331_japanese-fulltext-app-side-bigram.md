---
title: IADR-0331 日本語の全文検索はアプリ側 2-gram を Qdrant の全文索引に載せて成立させる（形態素解析・別エンジン・要求の読み替えは採らず、計画の裁定も要しない）
type: impl-adr
status: Accepted
related_ids: [FR-03, UC-01, SC-01, SC-02, NFR, NFR-01, NFR-06, NFR-08, ADR-0009, ADR-0016, IADR-0014, IADR-0252, IADR-0313, IADR-0315, IADR-0318]
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-store-qdrant.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
related_specs:
  - ../specs/20260902_issue-1118_japanese-bigram-fulltext.md
---

# IADR-0331: 日本語の全文検索はアプリ側 2-gram で成立させる（#1118）

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: claude（実装判断。#1118 の実測で確定）

## 起点・関連

- 起点: #1118「全文検索が日本語でほぼ引けない（`multilingual` トークナイザの再現率が実配備のチャンクで 0 件）」
- 前提: [[IADR-0318]]（`text` に `multilingual` の全文索引を張った。日本語の再現率が部分的であることを記録し、
  フォローアップに「形態素解析器つきビルドか `Match::Phrase` の採否を裁定に掛ける」と残した）／
  [[IADR-0315]]（Qdrant サーバ 1.18.1）／[[IADR-0014]]（ペイロードキーは両サービスへ複写して揃える）
- 関連する実装仕様書: [`20260902_issue-1118_japanese-bigram-fulltext.md`](../specs/20260902_issue-1118_japanese-bigram-fulltext.md)

> 🔴 **issue は「ADR-0016（ベクトルストアの選定）」と書くが、ベクトルストアの選定は ADR-0009 である。**
> ADR-0016 は埋め込みプロバイダの ADR で、本件の射程は ADR-0009 にある。コミットのスコープは issue の件名に
> 揃えて `ADR-0016` を残すが、根拠は ADR-0009 で引く。

## コンテキストと課題

FR-03 は「キーワードと自然文の双方で横断検索できる（ベクトル検索＋全文検索のハイブリッド）」と定め、
本製品は日本語のナレッジ基盤である。#1117 で識別子・型番・略語は引けるようになったが、日本語の語は
実配備のチャンクでほぼ引けない。

### 実測 1（陰性）: 公式イメージの `multilingual` は CJK の連なりを語で割らない

稼働 k3s の `qdrant/qdrant:v1.18.1` へ port-forward し、**稼働コレクション `knowledge_chunks_deterministic_v1`
の 3 点（実配備チャンク。185 / 233 / 129 文字）を scroll で読み、本文をそのまま使い捨てコレクションへ入れて**
索引を張り替えながら同じクエリで引いた（稼働コレクションには触れていない）。

```
=== text: multilingual min1 max40 lower (現行)
   日本語(在る): 文書=0 検索=0 導線=0 検証=0 統合=0 横断検索=0 観測=0 投入=0 合言葉=0 取り込み=1 索引=0 経路=0
                本文=0 格納=0 発行=0 埋め込み=0 登録=0 解決=0 早期=0 捨てる=0 チャンク=0 オブジェクトストレージ=0
                ハイブリッド検索=0 検索導線の検証用文書=0 壊れている=0
   1文字: 本=0 文=0 索=0
   全 2-gram 再現率: 1/176
   識別子(在る): IngestionService=2 MarkdownUri=2 DocumentUpdatedConsumer=1 abac=1 tanpopo searchseed msp=1
   識別子(在らない/断片): zzzznotexistword=0 anpop=0 estionServ=0
```

**チャンクに実在する日本語 25 語のうち当たるのは 1 語、実在する 2-gram 176 種のうち当たるのは 1 つ。**
当たった `取り込み` は改行と `（` に挟まれて**単独の連なり**になっている語である。
`max_token_len` を 400 にしても同じ（1/176）。**＝ `multilingual` は日本語を分かち書きしておらず、語で当たるかは
連なりの切れ目次第である**（[[IADR-0318]] が短い文で `索引` は当たり `合言葉` は当たらないと記録したのも同じ理由。
Qdrant のビルドオプション `multiling-japanese` が公式イメージに入っていないときの挙動）。
**識別子は当たる（陽性対照）。**

`word` は 1/176、`whitespace` は 0/176 でしかも `MarkdownUri`（バッククォート付き）を落とす、`prefix` は 25/176（語頭のみ）。

### 実測 2（陽性）: アプリ側 2-gram を別ペイロードに載せると、同じチャンク・同じ語で全て当たる

`text_ngram` ＝ CJK の連なりごとに 2-gram（1 文字の連なりは 1-gram）を空白区切りで並べた文字列を同じ 3 点に併記し、
クエリも同じ変換をして `Match { Text }` した。

```
=== text_ngram(app bigram): prefix min1 max2
   日本語(在る): 文書=2 検索=2 導線=1 検証=1 統合=1 横断検索=1 観測=1 投入=1 合言葉=1 取り込み=1 索引=2 経路=1
                本文=2 格納=1 発行=1 埋め込み=1 登録=1 解決=1 早期=1 捨てる=1 チャンク=1 オブジェクトストレージ=1
                ハイブリッド検索=1 検索導線の検証用文書=1 壊れている=1
   日本語(在らない): 零細企業=0 月餅=0 東京都=0 形態素解析=0 株価=0
   1文字: 本=2 文=3 索=3
   全 2-gram 再現率: 176/176
```

**25/25 語・176/176。在らない 5 語は 0 件。** `whitespace` / `word` でも 25/25・176/176 だが **1 文字の語が 0 件**。
`prefix`（`max_token_len=2`）は 2-gram の 1 文字接頭辞も索引に持つので 1 文字の語も当たる。

## 検討した選択肢（計画の前提に触れるかで切り分けた）

| 道 | 触れる計画の前提 | 評価 |
| --- | --- | --- |
| A 形態素解析（Kuromoji / Sudachi / Lindera） | Qdrant の**自前ビルド**（公式イメージに `multiling-japanese` は無い）か取り込み側への辞書依存。辞書の同梱・更新運用。`08_data-egress-policy`（実行時の外部取得禁止）に沿わせる設計 | **計画の裁定が要る。採らない** |
| **B アプリ側 2-gram を別ペイロードに載せ、Qdrant 自身の全文索引で引く** | **無い。** ADR-0009 の決定（Qdrant が唯一の基盤・ポートで抽象化）を変えず、同 ADR が**フォローアップとして実装に残した「日本語性能に応じた索引設定」**そのものである。外部依存・辞書・外部取得・契約の変更を増やさない | **採用** |
| C 別エンジン（OpenSearch / Meilisearch） | ADR-0009「専用ベクトルDB として Qdrant を採用」の射程。運用対象が増える | **計画の裁定が要る。採らない** |
| D キーワード側は日本語を諦める | FR-03「キーワードと自然文の双方で」の読み替え | **計画の裁定が要る。採らない** |
| B' `text` のトークナイザを替える（`prefix` 等） | 無い | 実測 1 のとおり 25/176 が上限。`text` を替えると識別子の系統も動く。**採らない** |

**既裁定の有無**: `endazon/project-planning` の issue を closed 込みで `全文検索` / `日本語 検索` / `FR-03` /
`tokenizer OR multilingual OR 形態素 OR bigram OR N-gram` / `Qdrant` で検索した。**本件を扱う issue は 0 件**
（陽性対照: 同じ検索で `Qdrant` は planning#66 / planning#69 ほか、`FR-03` は planning#197 ほかが当たる）。
**B は裁定を要しないので、planning へは起票しない。** A / C / D へ進む必要が出たとき（B の精度が実運用で問題に
なったとき）に初めて `decision-needed` を起票する。

## 決定

### 決定 1: `text` は `multilingual` のまま、CJK は別ペイロード `text_ngram`（2-gram）で引く

- 符号化と分割は **`Knowledge.Contracts.Indexing.CjkBigramPayload`** に 1 つだけ置く（`PayloadKey = "text_ngram"` /
  `Encode` / `SplitQuery`）。**ペイロードキーの文字列は両サービスへ複写で揃えてきた**（[[IADR-0014]]）が、
  **変換の関数を複写すると必ず割れる**（片方だけ直すと静かに 0 件へ落ちる）ので、ここだけは契約プロジェクトで共有する。
- 索引: `tokenizer=prefix / min_token_len=1 / max_token_len=2 / lowercase=true`（`QdrantIngestionVectorStore.BuildCjkNgramIndexParams`）。
- 検索: `QdrantVectorStore.BuildFullTextConditions` がクエリを CJK 以外（→ `text`）と CJK の 2-gram（→ `text_ngram`）に割り、
  非空な側の条件だけを出す（両方在れば両方 `must`）。**識別子だけのクエリは #1117 と同じ 1 条件**であり、獲得物を落とさない。

### 決定 2: 索引は起動時に無条件・冪等に張り、既存の点は起動後に後付けする（再取り込みを要求しない）

- `EnsureCjkNgramIndexAsync`: `EnsureCollectionsAsync` の直後に、存在の有無によらず全コレクションへ張る（[[IADR-0318]] 決定 2 と同じ作法）。
- `BackfillCjkNgramAsync`: `is_empty text_ngram` の点を 256 点ずつ scroll し、`text` から `Encode` して `UpdateBatch(SetPayload)` で埋める。
  `QdrantCjkNgramBackfillHostedService`（`BackgroundService`）が起動後に走らせ、**起動を塞がない**。
  **2 回目以降の起動は 0 件走査で終わる**（埋めた点は `is_empty` に当たらない。CJK を含まない点にも空文字列を書く ——
  Qdrant の `is_empty` は空文字列を「空」と見ない）。同じ先頭の点が続けて返ったら止める（無限ループの防止）。
- 🔴 **`EnsureCollectionsAsync` には足さず別メソッドにし、ポートには既定実装（no-op）を置く。** #1063 が全サービスの
  `Tests/` を移送中で既存テストファイルを触れないため、`text` の索引を `OnlyContain` で固定している既存試験と、
  記録するだけの偽物ストアを壊さない形を選んだ。**既定 no-op の上書き漏れは検索側の readiness に現れる**（決定 3）。
- 移行スクリプトは作らない（[[IADR-0318]] が索引の後付けで退けた案 2 と同じ理由）。

### 決定 3: 縮退の可観測化は既存と同型（新 check ＋ 理由値 1 つ）

- `QdrantCjkNgramIndexHealthCheck`（`qdrant-cjk-ngram-index`）: `text_ngram: Text` が無ければ **Degraded**。
  `text` の check とは別に置く —— **`text` だけで Healthy を固定している既存試験を動かさない**ためと、
  Degraded の本文で「識別子は当たるが日本語だけが死んでいる」を区別して報告するため。**Unhealthy にしない**（NFR-06）。
- `KeywordSearchMetrics.MissingNgramIndexReason = "missing_ngram_index"`（理由の値域は 2 → 3）。

### 決定 4: 門は日本語の語を seed のタイトルから導く（合言葉を増やさない）

- `seed-search-documents.js --print-japanese-keyword-query` ＝ seed タイトルの最初の CJK の連なり（`検索導線の検証用文書`）。
  タイトルは本文の H1 としてチャンクに入る。**語をスクリプトへ書かない**（seed が単一情報源）。
- `verify-oidc-edge-flow.sh` 段 S6: `mode=keyword` でこの語を引き seed が当たること／**在らない日本語の語**で 0 件。
  S4（識別子）とは系統が違うので別段に置く —— **S4 が緑でも日本語は 0 件であり得る**のが #1118 の形である。
- `verify-qdrant-fulltext-index.sh` 段 7: 「数字を出すだけ」から**判定**へ格上げ（在る 4 語が全て ≥1・在らない語 0・1 文字の語 ≥1）。

## 理由

- **計画の前提を 1 つも動かさずに FR-03 の日本語側を成立させられる**のが B だけである（上表）。ADR-0009 が
  索引設定を実装に委ねているので、実装 ADR で閉じてよい。
- 2-gram は形態素解析より精度が低い（`京都` は `東京都` に当たる）が、**再現率 0 と精度の低下では前者が要求の不成立**である。
  ハイブリッド既定では RRF がベクトル側と融合するので単独の誤ヒットが上位を占めにくい。
- `text` を据え置くのは、識別子・型番・略語の再現率（#1117 の獲得物）を変異させないためである（受け入れ基準 3）。

## 結果

- **良い影響**: 実配備と同型のチャンクで日本語の語が全文検索に当たる（25/25・176/176）。既存の点も再取り込みなしで追随する。
  契約（`SearchResponse` / openapi）・配備の値・外部依存は変えない。
- **悪い影響 / トレードオフ**:
  - **精度は部分文字列一致相当**（誤ヒット）。助詞を含むクエリはその並びのまま要求する（`検証の文書` は `検証用文書` に当たらない）。
    言い換えは意味検索の側が受け持つ。**nDCG は測っていない。**
  - **索引が 2-gram の分だけ増える**（1 チャンクあたり CJK 文字数に比例。実配備 3 点で 84 / 84 / 45 トークン。
    `prefix` は 1 文字接頭辞も持つのでその分さらに増える）。NFR-08 の規模での実測は無い。
  - 起動後の後付けは**一度きり**だが、NFR-08 の規模では分単位になり得る（その間も取り込み・検索は動く）。
  - 検証スクリプトに符号化の**写し**（JS）が 1 つ在る。固定はアプリ側の単体試験が持ち、写しは実機検証にしか使わない。
- **フォローアップ**:
  - 誤ヒットが実運用で問題になったら A（形態素解析つきビルド）／C を `decision-needed` で planning へ掛ける。
  - 🔴 稼働 Pod（ingestion / retrieval）を新イメージで再起動しての通し（後付けの実走・S6 の実走）は、
    同じクラスタで並行実測中の作業（#1110 / #1102 / #1115）を理由に本作業では行っていない。マージ後の再測に残す。

## 検証

- **実機（稼働 k3s / `qdrant/qdrant:v1.18.1`・使い捨てコレクション）**: 上の実測 1・2（`probe-1118.mjs`。生出力は PR 本文）。
  `scripts/verify-qdrant-fulltext-index.sh` は **PASS 9 / FAIL 0**（段 2 で `text_ngram` の索引を張り、段 7 で
  在る 4 語 ≥1・在らない語 0・1 文字の語 ≥1 を対で判定。同じ語の `text` 側の件数を併記して対比を残す）。
- **単体**: `CjkBigramPayloadTests`（実配備チャンクの実文字列を fixture に符号化・分割を固定）／
  `QdrantCjkNgramIndexTests`（全コレクションへの索引・宣言値・後付けの選別と書き込み・0 件で書かない・進まなければ止まる）／
  `QdrantCjkNgramSearchTests`（クエリの分割が Scroll のフィルタへ写る・識別子だけのクエリは従来と同じ・
  `text` だけ在るとき Degraded・Unhealthy にしない）。既存の `QdrantFullTextIndexBootstrapTests` /
  `QdrantFullTextIndexObservabilityTests` / `DocumentUpdatedConsumerTests` は**無改変で緑**。
- **scripts**: `scripts.repo.test.js` に決定 4 の導出・S6 の形・値を写していないことの試験を足した。
- **契約スナップショット**: `check-contract-schema.js --update`（`Knowledge.Contracts.Indexing.CjkBigramPayload` の型追加。非破壊）。

## 関連

- Supersedes: なし（[[IADR-0318]] 決定 1 の `text` / `multilingual` はそのまま。本 ADR は日本語の系統を**足す**）
- Superseded by: なし
