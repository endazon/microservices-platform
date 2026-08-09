---
title: SC-02 検索結果へ更新日時を追加する（索引への取り込みと再索引を伴う）
type: spec
status: done
related_ids: [FR-02, FR-03, UC-01, SC-02, IADR-0014, IADR-0122, IADR-0125, IADR-0131, IADR-0148, IADR-0149]
author: Claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "../adr/IADR-0149_search-result-updated-at-indexing.md"
  - "../adr/IADR-0014_qdrant-attribute-payload-key.md"
  - "../adr/IADR-0122_contract-schema-source-and-compat-gate.md"
  - "../adr/IADR-0131_openapi-as-bff-contract-source.md"
  - "../screens/SC-02_search-results.md"
  - "../functional/FR-03_hybrid-search.md"
  - "../tests/FR-03_hybrid-search.md"
  - "../api/BFF_bff-surface.md"
---

# 仕様書: 検索結果へ更新日時を追加する（#536）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-03**（ハイブリッド検索）・**FR-02**（取り込み。**索引ペイロードを触るため**）
- ユースケース（UC）: **UC-01**（横断検索）
- 画面（SC）: **SC-02 検索結果一覧**
  （[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md)
  §SC-02 §主要素「結果テーブル（文書／タグ／**更新日時**、スニペット抜粋付き）」・
  §SC-02「検索モード・並び順・更新日時の確定」**Q6**）
- 関連 ADR:
  [IADR-0014](../adr/IADR-0014_qdrant-attribute-payload-key.md)（Qdrant ペイロードの表現。**実機検証済み**）／
  [IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md) 決定 2（既定値の無いメンバー追加は破壊的変更）／
  [IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 5（値集合を `enum` にしない）／
  **[IADR-0149](../adr/IADR-0149_search-result-updated-at-indexing.md)（本作業の判断記録）**
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
- 本リポジトリの起点: **#536**（親 #454）。**後続の #532（並び順 2 値）が本作業の索引項目をソートキーに使う**

## 目的・背景

計画は裁定 **Q6**（2026-08-05）で「**結果に更新日時を含める**」と定め、あわせて次を明記した。

> 本項目のみ索引（Qdrant のペイロード）へ日時を取り込むところから必要であり、
> **取り込み側（IngestionService）の変更と既存文書の再索引を伴う**。したがって検索モード・並び順より
> 遅れて入ることを許容する（**同じフェーズに束ねない**）。

**この一文が本作業の性格を決めている。** 契約に 1 メンバー足すだけの作業ではなく、
**索引の作り直しを要求する作業**である。[[IADR-0139]] 条件 F（契約の追加に閉じる）を満たさないため、
**単独の PR とする**（#534 + #537 のような束にはしない）。

**#532（並び順の 2 値化）は本作業に依存する。** 「更新日時の新しい順」は本作業が索引へ載せる項目を
ソートキーに使うため、先に本作業を入れる（#532 のコメントに実測つきで記録済み）。

## 母集合（[[IADR-0141]] 決定 1）

「**更新日時が通る経路**」を、**両端（契約と索引ペイロード）から**引いた。**拡張子で絞らず、パスから引いた**
（追跡下の全ファイル。`planning/` と `src/ai-stock-trading` は除く）。

```console
$ git grep -ln "SearchResultDto" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
43                     # 軸 1: 契約の構築・読み出し側
$ git grep -ln "document_title\|markdown_uri\|chunk_index" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
9                      # 軸 2: Qdrant ペイロードのキーを書く／読む側
```

### 軸 1: 契約（`SearchResultDto`）を構築・読む場所

| 層 | ファイル | 本作業での扱い |
| --- | --- | --- |
| 契約 | `Knowledge.Contracts/Dtos/SearchResultDto.cs` | **`UpdatedAt` を既定値つきで追加** |
| 検索（実装） | `RetrievalService.Api/Composable/Adapters/QdrantVectorStore.cs`（`MapPayload` / `UpsertAsync`） | **ペイロードから復元・書き込み** |
| 検索（テスト用） | `.../Composable/Adapters/InMemoryVectorStore.cs`・`Foundation/Ports/IVectorStore.cs`（`ChunkPayload`） | **`UpdatedAt` を運ぶ**（テストの seed 経路。ここを落とすと検索側のテストが書けない） |
| 検索（合成） | `.../Foundation/Services/HybridSearchService.cs` | **RRF の融合で `UpdatedAt` を落とさないこと**を確認する |
| AI 回答 | `AiAnalysisService.../CitationMapper.cs`・`RagOrchestrator.cs` | **触らない**（出典 `CitationDto` は Q10 で機密区分を足したが、日時は計画に無い） |
| BFF | `Platform.Bff`（`/bff/search`） | 型付き中継。**実装変更なし**・**透過をテストで固定する** |
| 契約書 | `docs/api/openapi.yaml` ＋ orval 生成物 | **追随**（`pnpm run codegen` を必ず再実行する） |
| 画面 | `sc02-results/SearchResultsPage.tsx` | **「更新日時」列を追加** |

### 軸 2: Qdrant ペイロードを書く／読む場所（**2 経路ある**）

| # | 経路 | 実体 | 本作業での扱い |
| --- | --- | --- | --- |
| 1 | **本番の書き込み** | `IngestionService.../QdrantIngestionVectorStore.BuildChunkPayload` ←`DocumentUpdatedConsumer` | **`updated_at` を書く。**`DocumentUpdated` は既に `UpdatedAt` を持つ（実測。イベント契約の変更は不要） |
| 2 | 検索側の書き込み | `RetrievalService.../QdrantVectorStore.UpsertAsync`（`ChunkPayload`） | **同じキーで書く。**呼び出しはテストの seed だけだが、**同じコレクションを読む復元側と表現がずれると静かに壊れる** |
| — | 読み出し | `RetrievalService.../QdrantVectorStore.MapPayload` | **`updated_at` を復元する** |

**2 経路あることを見落とすと「テストは緑・本番は空」または逆になる。** [[IADR-0014]] が
ABAC 属性のネスト表現で踏んだのと同じ型の事故である（書き込み・フィルタ・復元の 3 つを揃えた）。

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| **#532（並び順の 2 値化）** | **本作業の後続**である。索引項目が入って初めて実装できる。同じ PR にしない（[[IADR-0116]] 規約 1・#536 issue 本文が「同じフェーズに束ねるな」と明記） |
| `CitationDto`（AI 回答の出典） | 計画は**検索結果**（SC-02）に日時を求めており、出典（SC-01）には求めていない。**計画に無い項目を足さない** |
| `DocumentUpdated` イベント契約 | **既に `UpdatedAt` を持っている**（実測）。変更不要 |
| 既存索引のバックフィル機構 | **新規に作らない。**再索引の手順は `docs/operations/operations.md` に既存であり（`DocumentUpdated` の再発行）、本作業はそこへ**本項目のための追記**をする。§再索引 |
| `src/ai-stock-trading` | 別プロジェクトの submodule |
| `planning/` | 本作業では pin を動かさない（#628 で `31a69c9` へ前進済み・レンジ引き直し済み） |

## 実装方針

### 1. 契約（`SearchResultDto`）

```csharp
public record SearchResultDto(
    …,
    List<string> Tags,
    // FR-03, SC-02（Q6 / #536）: 文書の更新日時。
    DateTimeOffset? UpdatedAt = null);
```

**既定値つきで追加する**（[[IADR-0122]] 決定 2。既定値の無いメンバー追加は破壊的変更）。
**null 許容にする理由は再索引にある** —— 未再索引のチャンクはペイロードに日時を持たず、
`null` が「まだ索引に無い」を正しく表す。**`DateTimeOffset.MinValue` で埋めない**
（1 年 1 月 1 日の文書が並び順の先頭に来る、という嘘をつく）。

### 2. 索引ペイロード（`updated_at`）

**値は Unix epoch ミリ秒の整数で持つ。** 判断の根拠は [[IADR-0149]] に置く（要旨: 文字列 ISO-8601 だと
**後続 #532 の並び替えが辞書順に依存**し、オフセット表記の揺れ（`+09:00` / `Z`）で順序が壊れる。
整数なら表記に依らず一意で、Qdrant のペイロードインデックスにもそのまま載る）。

### 3. 画面（SC-02）

結果テーブルへ「更新日時」列を追加する。**`null` は `—` で描く**（[[IADR-0127]] が SC-07 で採った形）。
「日時が無い」と「まだ再索引していない」を画面で区別しない —— **利用者に索引の内部事情を見せない。**

### 4. 再索引（計画が「伴う」と書いたもの）

**本 PR は再索引を実行しない。実行できるようにして、手順を書く。**

- 仕組みは既にある: `DocumentUpdated` を再発行すると、`DocumentUpdatedConsumer` が
  **全コレクションから当該文書を削除してから索引し直す**（決定的チャンク ID により冪等）。
- したがって本作業で要るのは**運用手順への追記**であり、新しいバックフィル機構ではない。
- **再索引前は `updatedAt` が `null` で返る。** これは縮退であって障害ではない（画面は `—`）。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 取り込みがペイロードへ `updated_at` を書く | `QdrantIngestionVectorStoreTests` |
| 2 | 取り込みが `DocumentUpdated.UpdatedAt` をそのまま運ぶ（時刻を作らない） | `DocumentUpdatedConsumerTests` |
| 3 | 検索がペイロードから `UpdatedAt` を復元する | `QdrantVectorStoreTests` |
| 4 | **`updated_at` が無いチャンクは `null` で返る**（未再索引の縮退） | 同上 |
| 5 | ハイブリッド融合（RRF）で `UpdatedAt` が落ちない | `HybridSearchServiceTests` |
| 6 | BFF が `updatedAt` を透過する | `BffSearchEndpointTests` 相当 |
| 7 | 画面が「更新日時」列を出す | `SearchResultsPage.test.tsx` |
| 8 | 画面が `null` を `—` で描く | 同上 |

## 追随させる文書

- `docs/api/openapi.yaml`（`SearchResultDto` スキーマ）＋ **orval 再生成**
- `docs/api/BFF_bff-surface.md`（`/bff/search` の応答）
- `docs/screens/SC-02_search-results.md`（§hi-fi 対応 #9・§実装しない要素 (c)・§未決事項 1 を解消済みへ）
- `docs/functional/FR-03_hybrid-search.md`・`docs/tests/FR-03_hybrid-search.md`
- `docs/functional/FR-02_ingestion.md`・`docs/tests/FR-02_ingestion.md`（ペイロードのキー一覧）
- `docs/operations/operations.md`（**再索引手順へ本項目の追記**）
- `docs/adr/IADR-0149_*.md`（**新設**）＋ `docs/adr/README.md`

## 実装中に決めたこと（仕様書からの差分）

### `formatDateTime` を foundation へ移した

SC-02 の「更新日時」列は**値なしを `—` で描く**。同じ整形規則は SC-06 が既に持っていた
（`features/sc06-datasources/syncState.ts`）。**2 つ目の利用者が現れた時点で**
`platform/frontend/src/foundation/ui/formatDateTime.ts` へ移し、SC-06 は再エクスポートで受ける。

- **複写しない理由**: `—` の書き方が画面ごとに割れる。SC-06 の同期健全性で
  「同じ数が 2 箇所に立つと黙って割れる」（[[IADR-0148]] 決定 4）を踏んだのと同じ型である。
- **射程を広げたのではない**: 移したのは**本作業が必要とする 4 行**であり、`@platform/ui` へは入れない
  （**表示文言を持つ**ため。[[IADR-0125]] 決定 1 が禁じている）。SC-06 の振る舞いは変えていない
  （`syncState.test.ts` 12 件がそのまま緑）。

## 検証記録（実測・すべて本作業の head で走らせた）

`node scripts/…` は**リポジトリのルートから実行する**（`src/` から走らせると相対パスが割れる）。

| 対象 | 結果 |
| --- | --- |
| `dotnet test knowledge/backend/backend.slnx` | **440 passed / 0 failed**（18 skipped は統合テストの環境依存。**本作業で 9 件追加**） |
| `dotnet test platform/backend/backend.slnx` | **362 passed / 0 failed**（1 skipped。**本作業で 1 件追加**） |
| `dotnet format --verify-no-changes`（両ユニット） | OK |
| `pnpm typecheck` / `lint` / `format:check` | OK（lint は warning 9・error 0。既存の `react-refresh` 警告） |
| `pnpm test:coverage` | statements **96.39%** / branches **90.53%** / functions **91.68%** / lines **96.39%**（床 90 / 85 / 88 / 90。**割っていない**） |
| `pnpm build` ＋ `check-static-egress` | OK（24 ファイル・外部オリジン 0） |
| `check-chunk-budget` | **床を 578.06 → 578.15 kB へ更新**（+0.09 kB ＝ **新しい表示文言 1 件**〔「更新日時」〕が Lingui カタログへ載った分） |
| `check-contract-schema` | **baseline を更新**（`SearchResultDto.UpdatedAt` の**追加**。破壊的変更 0 件） |
| `check-test-spec-coverage --update` | **床を 75 → 78 対へ**（`FR-02 × QdrantIngestionVectorStoreTests` / `FR-03 × QdrantVectorStoreTests` / `FR-03 × BffSearchEndpointTests`） |
| `check-i18n-catalogs` | OK（**ja / en 両方に訳を入れた**。「更新日時」→ `Updated`） |
| その他 | `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-landed-subjects` / `check-adr-numbering` / `check-bff-downstreams` / `check-test-traceability` / `check-unit-dependencies` / `check-unit-service-ownership` / `check-backend-libraries` / `check-commit-messages` すべて OK |

**カバレッジ床は上げない**（#628 と同じ判断。床は #619 以降どの PR も動かしておらず、全ユニット横断の
床をここで上げると並走中の PR を巻き添えにする）。

### AI レビュー（仕様書のみの時点・PR #631）への対応

| 指摘 | 対応 |
| --- | --- |
| 🟢 [[IADR-0149]] に `related_specs` が無い | **修正した**（作業仕様書・[[IADR-0014]]・[[IADR-0122]]・SC-02 画面仕様書への逆リンクを追加） |
| 🟢 ブランチ名に起点 ID が無い | **直せない。** ブランチ名 `claude/handover-work-start-7g1vu3` はセッションに与えられた指定であり、**別ブランチへ push してはならない**制約がある。**指摘は正しい** —— PR 本文のチェックを外し、代わりにコミット件名・PR タイトル・コード内コメントで追跡を成立させている旨を明記した |
