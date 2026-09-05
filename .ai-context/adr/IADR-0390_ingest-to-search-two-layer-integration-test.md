---
title: IADR-0390 取り込み → 検索の段間結合は 2 層で測る（Docker 不要の共有索引を床に、実 Qdrant を天井に）
type: impl-adr
status: Accepted
related_ids:
  - FR-02
  - FR-03
  - FR-21
  - NFR-21
  - ADR-0009
  - ADR-0016
  - ADR-0035
  - IADR-0368
  - IADR-0232
  - IADR-0358
  - IADR-0014
  - IADR-0231
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-02 取り込み: parse → chunk → embed → index)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-03 ベクトル＋全文のハイブリッド検索)
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-database.md (ベクトル DB はポートで抽象化する)
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-model-routing.md (機密区分でモデル別コレクションへルーティングする)
---

# IADR-0390: 取り込み → 検索の段間結合テストの置き方（#1247）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `FR-02` / `FR-03` / `FR-21` / `NFR-21` / `ADR-0009` / `ADR-0016` / `ADR-0035`
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1247_ingest-to-search-integration.md`
- issue: #1247（トラッカー #447 の「退行防止（テスト必須）」2 番目）
- 先行: `IADR-0368`（`TestKind` トレイト。`Category` は CI の振り分けに load-bearing）／
  `IADR-0232`（Integration の回収先は `integration.yml`）／
  `IADR-0231` 決定 3（動的スキップは `Assert.Skip*` に統一する）／
  `IADR-0014`（サービスを跨ぐ文字列一致は「テストは緑・本番は空」を生む）

## コンテキストと課題

**取り込みが索引へ書いた点が検索で当たるかを、in-repo で 1 度も測っていない。**

実測（2026-09-05・基点 `3d5f8c99`。`git rev-parse --is-shallow-repository` → `false`）:

```console
$ grep -rnE '"/search|/search/hybrid|IHybridSearchService|SearchAsync' \
    src/knowledge/backend/Tests/Knowledge.IntegrationTests
（0 件）

$ grep -rnE '"/search|/search/hybrid|IHybridSearchService|SearchAsync' \
    src/knowledge/backend/Services/RetrievalService/Tests | wc -l
98        ← 陽性対照（走査は生きている）
```

98 件はすべて**検索の中**だけを測っており、取り込み側の書き込みを経由したものは無い。

段の全体は 7 段あり、**段 5（`IIngestionVectorStore` → Qdrant への書き込み）と
段 6（Qdrant → `IVectorStore` の読み出し）の間に測定点が無い。** 両者は
**別サービスの別クラス**で、コレクション名とペイロード鍵（`text` / `document_id` /
`attributes.*`）を**文字列で**合わせている。`QdrantIngestionVectorStore.FullTextKey` の
コメント自身が「サービスを跨ぐため型では束ねられない」と書いており、
これは `IADR-0014` が実際に踏んだ型そのものである。稼働 dev クラスタで検索が全件 0 件に
なった事故（#1215）の一因も、まさに読み書き先の不一致だった。

#1247 は選択肢を 2 つ挙げ、**どちらかを記録に残して決めよ**と条件を付けている。

- **(A)** `Knowledge.IntegrationTests` に Qdrant コンテナを足し、記録フェイクを実体へ置き換える。
- **(B)** スタック水準（`integration-stack`）を正式な回収先と宣言し、xUnit 版は作らない。

## 検討した選択肢

| 案 | Docker 無しの開発機で走るか | アダプタ間の表現ずれを検知できるか | 評価 |
| --- | --- | --- | --- |
| **(A) 実 Qdrant のみ** | ❌ 走らない（本環境は containerd / nerdctl で Docker API が無い） | ✅ できる | **却下**。既存の Integration は 42 件中 **41 件が Skipped** で緑になる。ここへ 1 本足しても「書いたが走らない」が 1 本増えるだけで、**#1247 が名指しした再生産**になる |
| **(B) スタック水準のみ** | ❌（`integration-stack` は CI 専用。しかも #1219 で現在赤い） | ✅ できる | **却下**。前提（#1219 の解消）が他 issue に依存し、**本 issue の完了が他人の作業に従属する**。加えて in-repo の単体・結合の層に測定点が 1 つも増えない |
| **(A′) 共有索引の in-process のみ** | ✅ 走る | ❌ **できない**（自分で書いた橋は自分と必ず一致する） | **却下**。段は繋がるが、事故（#1215）の主因の型を検知できない |
| **(A+A′) 2 層（採用）** | ✅ 層 1 が必ず走る | ✅ 層 2 が検知する | **採用** |

## 決定

### 決定 1: **(A) を採る。ただし実体は 2 層である**

- **層 1 `IngestToSearchInProcessTests`** — Docker 不要。段 2〜7 を 1 プロセスで通す。
  索引の実体は `RetrievalService.Infrastructure.ExternalServices.InMemoryVectorStore`
  （**本番コードに実在するポート実装**）で、書き手と読み手が**同一インスタンス**を共有する。
  書き込み側ポートを読み出し側ポートの語彙へ写す橋は
  `SharedIndexIngestionVectorStore`（テストプロジェクト内）1 箇所に閉じる。
- **層 2 `IngestToSearchQdrantTests`** — `Testcontainers.Qdrant` で実 Qdrant を起こし、
  **本番の `QdrantIngestionVectorStore` で書き、本番の `QdrantVectorStore` で読む。**

🔴 **どちらか一方では要件を満たさない。** 層 1 は「段が繋がっているか」を**必ず**測るが
アダプタ間の表現ずれを測れない。層 2 はそれを測れるが**この環境では 1 度も走らない**。
**2 層であることが決定の内容であり、片方だけを残すのは決定の否定である。**

### 決定 2: トレイトは軸ごとに使い分ける。**層 1 に `Category=Integration` を付けない**

| テスト | `Category` | `TestKind` | PR の CI |
| --- | --- | --- | --- |
| 層 1 | **付けない** | `Integration` | **走る** |
| 層 2 | `Integration` | `Integration` | 走らない（`integration.yml` が回収） |

🔴 `ci.yml` の `backend-build` は `--filter "Category!=Integration"` である
（`IADR-0232` 決定 3）。層 1 に `Category` を付けた瞬間、**本 ADR が置いた床が PR から消える。**
`IADR-0368` 決定 1 が `TestKind` を別軸として新設したのはこのためであり、
本件はその軸の最初の実利用である。

### 決定 3: 層 2 の Docker 不在は `Assert.SkipUnless`（真の Skipped）で表す

`IADR-0231` 決定 3 の適用であり、新しい作法ではない。既存の `DockerRequired` をそのまま使う。
**ソフトスキップ（`if (!available) return;`）にしない** —— 走っていないものを Passed と数えない。

🔴 **本 PR の時点で層 2 は 1 度も実行されていない。** 開発環境に Docker デーモンが無く、
コンパイルが通ったことしか確かめていない。**初回の実走は develop マージ後の
`integration.yml`** である。この事実を PR 本文にも書く —— 書かないと「結合テストがある」が
「結合テストが通っている」と読まれる。

### 決定 4: **NFR「登録から 15 分以内に検索へ反映」は、本テストでは測っていない**

層 1 は同期呼び出し、層 2 はコンテナ 1 台の待ち時間であり、**どちらも配送遅延・再試行・
バックログを含まない。** ここが緑でも反映時間の担保にはならない。

測るべき場所は稼働スタック水準（`integration-stack` / #1215 の門）であり、
**現時点ではどこでも測っていない。** 本 ADR は「測っていない」と記録するに留める ——
🔴 **測っていないことを「満たしている」と読ませないことが、この決定の全部である。**
（時間軸を測る受け皿を作ることは本 issue の射程外。必要になった時点で別 issue を立てる。）

### 決定 5: 0 件で緑にならない形を、陽性・陰性の対と変異試験で固定する

- 陽性: 取り込んだ語で**ヒットが 1 件以上**あること（`NotBeEmpty` ＋ 当の文書 ID を含むこと）。
- 陰性: 本文にも題名にもタグにも無い語で**0 件**であること。**陰性の直前に陽性対照を置く**
  —— 索引が空でも陰性は緑になるので、単独では何の証拠にもならない。
- 変異: 取り込み側の索引書き込み（`store.UpsertChunkAsync`）を外すと**層 1 の 4 本中 3 本が落ちる**
  （実測。残る 1 本は本文なし＝メタデータ点の経路であり、変異させた行を通らない）。

**陰性を全文（keyword）モードで測る**理由: 索引の実体 `InMemoryVectorStore` の意味検索は
**問い合わせベクトルを見ずに全件へスコア 0.9 を返す**（当該クラスのコメントが明示している）。
既定のハイブリッドで陰性を主張すると、検索の欠陥ではなく **test double の性質**を測ってしまう。

### 決定 6: `RetrievalService` にテスト用マーカー型を足す

`Knowledge.IntegrationTests` は 12 サービスを参照するので `WebApplicationFactory<Program>` は
CS0433 で衝突する。他 8 サービスと同じ `TestMarker.cs` を置く。
**RetrievalService 自身の `Tests/` は `WebApplicationFactory<Program>` のままでよい**
（あちらは 1 サービスしか参照しない）。既存 98 件の宣言は 1 バイトも動かない。

## 結果

- **良い影響**
  - 段 5 と段 6 の間に**必ず走る**測定点ができた（層 1 は 4 本すべて実走。Skipped 0）。
  - 稼働環境の事故（#1215）と同型の欠陥 —— 読み書き先コレクションの不一致 —— を、
    層 1 が書き込み先の主張として、層 2 が実機の往復として、それぞれ捕まえる。
  - `TestKind` 軸（`IADR-0368`）に最初の実利用ができた。
- **悪い影響 / トレードオフ**
  - **層 1 の橋は自分と一致する。** アダプタ間のずれは層 2 に依存し、層 2 は開発機で走らない。
    **未実走のテストが 3 本増える**（`integration.yml` が初回を回す）。
  - 埋め込みは両層ともスタブである。**意味検索の「近さ」は測っていない**（測る対象ではない）。
  - `Knowledge.IntegrationTests` の参照パッケージが 1 つ増える（`Testcontainers.Qdrant`。
    版は `Directory.Packages.props` に既出で、他の Testcontainers 一族と揃っている）。
- **フォローアップ**
  1. **反映時間（NFR）を測る場所**が無い（決定 4）。稼働スタック水準の受け皿は #1215 / #1219 側。
  2. 層 2 の初回実走の結果を develop マージ後に確認する。落ちたら、それは**この ADR が
     検知したかった型の欠陥が実在した**ということである。

## 関連

- Supersedes: なし
- Superseded by: なし
