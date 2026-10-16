---
title: IADR-0552 計画 ADR-0140 への追随 —— 実装の「ZDR」はモデル単位の除外と読み分け、組織の ZDR が働いていると読める記述を直す。未認定の Voyage の鍵の環境は「鍵を入れない」か「ティア A を有効にして Voyage を止める」で扱い、compose に Voyage の経路の口を足す
type: impl-adr
status: Accepted
related_ids: [NFR-17, FR-02, FR-03, FR-11, ADR-0140, ADR-0016, ADR-0127, IADR-0007, IADR-0022, IADR-0025, IADR-0085, IADR-0498, IADR-0504]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0140_voyage-tier-b-residency-accepted-risk.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
related_specs:
  - ../specs/20261016_1960_adr-0140-tier-b-residency-zdr.md
---

# IADR-0552: 計画 ADR-0140 への追随（ZDR の読み分けと、未認定の Voyage の鍵の手順）（#1960）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-16
- 決定者: claude（計画 ADR-0140 の追随。起点 #1960）

## 起点・関連

- 起点 issue: #1960。
- 計画 ADR: **ADR-0140**（planning#795 の裁定。Voyage と Claude の所在を受け入れたリスクとし、Claude の ZDR を未締結と記録して期限付きで受け入れた）。ADR-0016・ADR-0127。
- 実装 ADR: IADR-0007（エンドポイント定義。判定根拠のメタデータ化のフォローアップ）・IADR-0022（`NonZdrModels`）・IADR-0498（用途 `rerank` の ZDR の要件）・
  IADR-0025・IADR-0504・IADR-0085（Voyage の経路・鍵・セルフホスト）。いずれも ADR-0140 が前提として扱ったもので、本 IADR は覆さない。

## コンテキストと課題

ADR-0140 は、計画が「Claude（ティア B・ZDR）」と書いてきた箇所について、組織の ZDR 契約の記録が無いため**未締結**と記録した。
実装の側にも同じ形の記述が残っていた（Helm values のコメント「ティア B（ZDR）へ出る」・運用仕様書「守りは ZDR」・ルーターのコメント
「ティア C（ZDR の契約が無い）」）。実装の ZDR の要件は `NonZdrModels`（現在は空）とティア C を候補から外すだけで、組織の契約を見ない。
このままでは、ゼロ保持が働いていると読める（裁定 planning#286 の読み分けに反する）。

また ADR-0140 決定 3 は、opt-out を確かめていない Voyage の鍵の環境に、運用仕様書の「未認定の環境へデプロイする場合」の手順
（Voyage の経路を明示的に無効にする）を当てると定めた。実測すると、この手順は書かれたとおりには成り立たない。

1. `Embedding__Routing__Endpoints__0__Enabled=false` だけを与えると、`EmbeddingRoutingOptionsValidator`（`ValidateOnStart`）が
   「public を送れる有効なエンドポイントが無い」として起動を落とす。既定ではティア A がすべて無効なので、**LLM ゲートウェイ全体（Claude の呼び出しを含む）が起動しない。**
2. compose の `llm-gateway` はこの env を持たず、運用仕様書の「compose は `.env`」は届かない。

## 検討した選択肢

### ZDR の読み分け

1. **語を残し、モデル単位であることと組織の ZDR が未締結であることを注記する（採用）。** 型名・設定名（`NonZdrModels`・`ZeroDataRetentionPurposes`・
   `RequiresZeroDataRetention`）と試験名は、モデル単位の除外の名前として正しい。誤って読めるのは「ティア B なら ZDR」と結ぶ記述だけである。
2. 型名・設定名を改名する（例 `NonZdrModels` → `ModelLevelNonZdrModels`）。設定の互換を壊し、ADR-0140 が求めていない挙動の変更を伴う。採らない。

### 未認定の鍵の環境の手順

1. **手順を 2 つの形に直し、compose に口を足す（採用）。** (a) 鍵を入れない（鍵が空なら `VoyageEmbeddingProvider` は HTTP を行わない）。
   (b) ティア A（セルフホスト）を有効にしたうえで Voyage の経路を止める。compose に `VOYAGE_EMBEDDING_ENABLED`（既定 `true`）を足し、Helm は既存の `extraEnvAppend` を使う。
2. 起動時の検証を緩め、Voyage だけを止めても起動できるようにする（埋め込みは全件 fail-closed）。検証は「インデックス依存の上書きで Voyage を誤って止める事故」を
   止めるために置いたもの（Issue #98）であり、緩めると意図した無効化と事故を区別できなくなる。ADR-0140 はコードの挙動の変更を求めていない。採らない。
3. 決定的ローカル（検証スタック専用）を有効にして起動を通す手順にする。意味の近さを持たない埋め込みで本番の検索を動かすことになる。採らない。
4. Helm に専用の values（例 `embedding.voyageEnabled`）を足す。既存の `extraEnvAppend` で足りる。手順で済む口をチャートへ増やさない。採らない。

## 決定

1. **実装の「ZDR」はモデル単位の除外（`NonZdrModels`）とティア C の除外を指し、組織の ZDR 契約を見ない、と読み分ける。**
   live な文書（運用・セキュリティ・機能仕様書）、Helm values のコメント、ゲートウェイのコードのコメントのうち、組織の ZDR が働いていると読める箇所を直し、
   Claude の組織の ZDR は未締結（期限付きで受け入れたリスク。受容者は利用者、期限は本番運用への移行判断の前）であると書く。
   型名・設定名・試験名は改めない。
2. **opt-out を確かめていない Voyage の鍵の環境は、次のどちらかで扱う。** (a) 鍵を入れない。(b) ティア A を有効にしたうえで Voyage の経路を止める。
   Voyage の経路だけを止める形は、ゲートウェイを起動させないので手順に書かない。決定的ローカルで起動を通す形も採らない。
3. **compose の `llm-gateway` に `Embedding__Routing__Endpoints__0__Enabled: ${VOYAGE_EMBEDDING_ENABLED:-true}` を足す。** 既定は `true` で従前と同じ挙動である。
   Helm は `services.llmgateway.extraEnvAppend` に同じ env を足す（`extraEnv` は本番既定の env を置き換えるので使わない）。
4. **運用仕様書に Claude の送信先ティアの認定状況の節を置き**（ADR-0140 決定 5 の表・受容者・期限・`inference_geo` 未指定・workspace の設定値は未確認・「監査可能」未照合）、
   Voyage の記録へ、定義への例外であること・根拠は規約と opt-out だけであること・「監査可能」未照合・判定根拠がエンドポイント定義に無いことを足す。
   **判定根拠のエンドポイント定義への記録（ADR-0140 フォローアップ 1・IADR-0007 のフォローアップ）は本 IADR では実装しない。** それまでは運用仕様書の記録が暫定の手段である。

## 理由

- 決定 1: ADR-0140 決定 5 の理由（「ZDR とだけ書き続けると、ゼロ保持が働いていると読める」）は、実装の記述にもそのまま当たる。語そのものはモデル単位の意味で正しく、
  改名は設定の互換を壊すだけで読み違いを減らさない。
- 決定 2: ADR-0140 決定 3・6 は「運用の手順だけがある」を統制の現在の実現手段としている。その手順が実行するとゲートウェイを止めるなら、統制は働いていない。
  鍵を入れないのは、外へ何も出ないことがコード（鍵が空なら HTTP を行わない）で保証される最も単純な形である。
- 決定 3: 運用仕様書が「compose は `.env`」と書いていた手順を、字面どおりに成り立たせる最小の変更である。既定を変えないので、既存の配備は影響を受けない。
- 決定 4: ADR-0140 決定 6 は、判定根拠のメタデータ化を未実装とし、運用仕様書の記録を暫定手段とした。設定の型の追加はコードの変更を伴い、ADR-0140 はフォローアップとして挙げている。

## 結果

- **良い影響**: 実装の文書から、Claude と Voyage について何を受け入れているか（所在 2 件・ZDR 1 件〔期限付き〕・opt-out 前の送信・「監査可能」未照合）が辿れる。
  未認定の鍵の環境の手順が、実行してゲートウェイを止めない形になる。
- **悪い影響 / トレードオフ**:
  - 手順 (b) で `public` / `internal` と検索クエリの埋め込みはティア A（Ruri）へ回る。主コレクション（voyage）を読む検索の振る舞いは確かめていない（確かめたのは起動の可否だけ）。
  - 鍵がどのアカウントのものかを機械で確かめる手段は引き続き無い（ADR-0140 決定 6）。
  - 「ZDR 対応」「ZDR 必須」の語は残るので、注記を読まないと組織の ZDR と読み得る。注記は語の出る主な箇所（運用・機能・セキュリティ仕様書・ルーターと設定のコメント）に置いた。
- **フォローアップ**:
  1. 判定根拠をエンドポイント定義へ記録する（ADR-0140 フォローアップ 1・IADR-0007 のフォローアップ）。
  2. Claude の ZDR を締結したら（ADR-0140 フォローアップ 2。担当はオーナー）、運用仕様書の Claude の節と本 IADR の決定 1 の注記を改める。
