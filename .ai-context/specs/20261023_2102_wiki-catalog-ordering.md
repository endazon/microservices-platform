---
title: 作業仕様書 — Wiki とカタログ（DocumentService）の受け手の順序（古い更新で新しい状態を上書きしない・削除やアーカイブの後に復活させない）
type: spec
status: done
related_ids: [FR-06, FR-13, FR-01, FR-19, UC-07, UC-04, ADR-0011, ADR-0027, ADR-0057, ADR-0141, ADR-0061, IADR-0596, IADR-0595, IADR-0021, IADR-0541, IADR-0563, IADR-0529, IADR-0296]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-01・FR-06・FR-13・FR-19）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（少なくとも 1 回・再試行／デッドレター）
  - planning:projects/microservices-platform/07_adr/ADR-0057_deletion-propagation-scope.md（決定 1・2）
  - planning:projects/microservices-platform/07_adr/ADR-0141（決定 1 (a): 再正規化はアーカイブを normalized へ戻す）
issue: "#2102"
---

# 作業仕様書 — Wiki とカタログの受け手の順序

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0596**（新規）に置く。
> 親 issue #2047 の指摘 M-4（Wiki・Catalog 分）。Ingestion の受け手は #2101（IADR-0595・PR #2149）で済んでいる。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-13**（Wiki 閲覧。Wiki.js への同期と ABAC の同期メタデータ）、**FR-06**（文書の台帳・版）、FR-01（取り込み経路のカタログ登録）、FR-19（露出の撤収）。ユースケース: UC-07・UC-04。
- 計画 ADR: **ADR-0027**（Accepted。少なくとも 1 回・順序は保証しない）、**ADR-0057**（決定 1: 削除は外部の実体まで及ぶ／決定 2: 復元に使える残余を置かない。削除の事実の記録は許す）、
  **ADR-0141 決定 1 (a)**（データソースの再同期で同じ文書 ID を再び正規化すると状態は normalized へ戻る ＝ アーカイブを戻す方法の 1 つ。#2139 で確認済み）、ADR-0011（Wiki.js）、ADR-0061（露出の撤収）。
  **計画に到着順の逆転を扱う明文は無い。** 本変更は FR-06 / FR-13 の受け入れ基準（台帳・Wiki が最新の状態を示す・権限外に出ない）を、少なくとも 1 回・順序非保証の配送の下でも保つための実装判断であり、計画との矛盾は無い。
  ADR-0141 決定 1 (a) とは**両立させる**: 新しい再正規化は従来どおりアーカイブを戻し、アーカイブより前に作られた再正規化だけが戻さない（下の要件 5）。
- 実装 ADR: **IADR-0596**（新設）。引き継ぐ判断: IADR-0595（Ingestion の版の印。同じ考え方を写す）・IADR-0021（Wiki.js の正準パス・同期メタデータ）・IADR-0541（恒久的な失敗は ack）・IADR-0563（Wiki.js の書き込みの門）・IADR-0529（露出の門）・IADR-0296（削除は台帳の参照を辿る）。
- 対照: GraphService `GraphDocument.TryApply`（`if (updatedAt < UpdatedAt) return false;`）、IngestionService `DocumentIndexOrdering.IsStale`。

## 範囲

- 本 PR:
  - WikiService の `DocumentUpdated` の受け口（`DocumentSyncConsumer`）と `DocumentDeleted` の受け口（`DocumentDeletedConsumer`）。
  - DocumentService（カタログ）の `DocumentNormalized` の受け口（`DocumentNormalizedConsumer`）と、それが書く `Document`（`ApplyNormalized`・`Archive`）・削除の口（`Delete/Endpoint`）。
- 範囲外（境界）:
  - **#2150（`UpdatedAt` を進めない変更の順序）**: 露出トグル・共有の付与と取り消し・タグの改名・再発行は `UpdatedAt` を進めずに `DocumentUpdated` を出す。Wiki の受け手には**等しい版**として届き、従前どおり後着が勝つ。全順序の版番号を契約に足す変更が要り、発行側と 4 つの受け手を揃えて変えるので本 PR では扱わない。
  - **#2042（Transactional Outbox）**: 発行側の DB の確定と発行の原子性。本 PR は届いたイベントの順序だけを扱う（受け手側）。
  - **#2100（Wiki.js の書き込みの門がプロセス内だけ）**: 門をレプリカ間の排他にする変更。本 PR はレプリカ間の並行に対して「保存の楽観的な突き合わせ ＋ 負けたら保存済みの最新の状態へ Wiki.js を合わせ直す」までを入れ、残る窓（§限界）は #2100 の排他で閉じる前提とする。
  - **#2103（Document の楽観ロック）**: カタログの 2 つの再正規化が並行に台帳を書く窓（check-then-write）。`Version` の並行トークン化は #2103 が入れる。本 PR は順序の判定だけを足す。
  - #2139（再正規化がアーカイブを戻す）は not planned で閉じた（ADR-0141 決定 1 (a) どおり）。本 PR はその決定を変えない。

## 母集合（規則 9・10。着手前に引いた）

`git grep` で誤りの側の字面（`IPipelineStep<DocumentUpdated>`・`IPipelineStep<DocumentDeleted>`・`IConsumer<DocumentNormalized>`・`ApplyNormalized`・`page.Sync(`・`.Archive(`・`Documents.Remove`）を引いた結果:

| 区分 | 対象 | 扱い |
| --- | --- | --- |
| Wiki の受け口（更新） | `DocumentSyncConsumer.Handle`（購読は `pipeline.json` の `wiki-sync` 段だけ）。分岐はアーカイブ・対象外の状態・個人資料・露出の門（撤去）・通常の同期 | 順序の門を足す。アーカイブ・撤去・同期の 3 つの書き込みの分岐が版の印を進める |
| Wiki の受け口（削除） | `DocumentDeletedConsumer.Handle`（`wiki-delete` 段） | 版の印に削除を記録する（行を消しても印は残す）。削除より新しい版を反映済みなら古い削除は適用しない |
| Wiki の書き込み | Wiki.js（`UpsertPageAsync`・`ArchivePageAsync`・`DeletePageAsync`）と同期メタデータ（`Pages`） | 順序の門の後でだけ書く。保存は版の印の改訂番号で突き合わせ、負けたら保存済みの状態へ Wiki.js を合わせ直す |
| カタログの受け口 | `DocumentNormalizedConsumer.ConsumeAsync`（`DocumentNormalized` の唯一の受け口。発行は変換の 2 か所: `MassTransitDocumentNormalizedPublisher`・`FigureCorrectionService`。どちらも `NormalizedAt = UtcNow`） | 本文を読む前に台帳を引き、順序の門を掛ける |
| カタログの書き込み | `Document.CreateNormalized`・`Document.ApplyNormalized`（呼び出しは受け口だけ）・`Document.Archive`（単票・本文なしの一括・AST の Quote 一括の 3 か所） | 反映した `NormalizedAt` とアーカイブの時刻を台帳に持つ |
| カタログの削除 | 組織文書の削除は `Delete/Endpoint` だけ。個人資料の削除（`PrivateNotes/Purge`・`PrivateNoteMaintenanceService`）は `DocumentNormalized` の経路に乗らない（文書 ID は変換の決定的 ID ではない） | `Delete/Endpoint` で削除の事実（文書 ID と時刻）を残す。個人資料の削除は改めない |
| 順序の判定キー | Wiki: `DocumentUpdated.UpdatedAt`（Ingestion・Graph と同じ）／`DocumentDeleted.DeletedAt`。カタログ: `DocumentNormalized.NormalizedAt` | 判定キーに使う。`UpdatedAt` を進めない変更 → #2150 |
| 再配送 | Wolverine の再試行（Wiki）・MassTransit の再試行（カタログ）・デッドレターからの再投入・ブローカの再配送・wiki の 2 レプリカ（HPA）・同じプロセス内の更新と削除の 2 本のリスナー | 等しい版は反映（冪等）。古い版は ack して捨てる。並行は保存の突き合わせ |
| 偽物（試験） | Wiki の `IWikiJsClient` の記録スタブ（試験ファイルごと）・InMemory の DbContext。カタログの試験は InMemory | 新しい試験で使う。既存の試験は変えない（版の印が無い ＝ 従前の挙動） |

除外: GraphService・IngestionService（それぞれ既存・#2101 で済み）。`DocumentService` の他の `DocumentUpdated` の発行点（受け手ではない）。

## 要件

1. Wiki は反映済みの版より厳密に古い `DocumentUpdated` を反映しない（Wiki.js にもメタデータにも書かずに ack）。等しい版は反映する。
2. Wiki は削除（`DocumentDeleted`）・撤去（露出の門）で同期メタデータの行を消した後も、それより古い版で Wiki.js のページとメタデータを書き戻さない。決定的な文書 ID で作り直された文書（削除より新しい版）は従来どおり載る。
3. Wiki はアーカイブを反映した後に、それより古い版でページを公開へ戻さない。
4. Wiki は 2 つの版を並行に処理して保存で負けたら、保存済みの最新の状態へ Wiki.js を合わせ直す（古い版の push を残さない）。
5. カタログは反映済みの `NormalizedAt` より厳密に古い `DocumentNormalized` を反映しない（本文を読まず、台帳を書かず、`DocumentUpdated` も出さずに ack）。
6. カタログはアーカイブより前に作られた再正規化で状態を normalized へ戻さない（内容は反映し、状態はアーカイブのまま）。アーカイブより後の再正規化は従来どおり戻す（ADR-0141 決定 1 (a)）。
7. カタログは削除した文書を、削除より前に作られた `DocumentNormalized` で台帳へ作り直さない。削除より後の再正規化（データソースの再同期）は従来どおり作る。
8. 印・削除の事実は文書 ID と時刻だけを持ち、題名・本文・属性の値を持たない（ADR-0057 決定 2）。

## 設計（決定は IADR-0596）

- **Wiki の版の印**: 新しい表 `DocumentSyncMarks`（主キー `DocumentId`・`AppliedAt`〔反映した版。マイクロ秒〕・`Deleted`・`Revision`〔並行トークン〕）。同期メタデータ（`Pages`）と別に置く —— 撤去・削除で `Pages` の行を消しても印は残す。
  - 古い更新の判定: `ev.UpdatedAt < AppliedAt`、または削除済みで `ev.UpdatedAt <= AppliedAt`。古い削除の判定: `ev.DeletedAt < AppliedAt`。比較は保存の精度（マイクロ秒）に丸めて行う。
  - 受け口の順: 印を読む → 古ければ ack → 分岐ごとの Wiki.js への書き込み → メタデータと印を 1 回の保存で書く（`Revision` で突き合わせ）。保存が突き合わせで負けたら（他のレプリカ・他のリスナーが先に印を進めた）、印を読み直し、自分が古くなっていれば保存済みの状態（行が active なら push・archived ならアーカイブ・行が無ければ削除）へ Wiki.js を合わせ直して ack する。古くなっていなければ投げて再試行へ委ねる。
  - 印を書かない分岐: 対象外の状態・個人資料（何も書かない）・本文の欠落と値の長さの拒否（IADR-0541 / IADR-0554 の ack。状態を変えない）。
- **カタログ**: `Document` に `LastNormalizedAt`（反映した `NormalizedAt`）と `ArchivedAt`（アーカイブした時刻。戻したら消す）を足す。削除の事実は新しい表 `DocumentDeletionMarks`（`DocumentId`・`DeletedAt`）。
  - 受け口の順: タグ → 台帳を引く → 古い（`NormalizedAt < LastNormalizedAt`）・削除より前（行が無く `NormalizedAt <= DeletedAt`）なら ack → 本文を読む → 反映（アーカイブ中で `NormalizedAt <= ArchivedAt` なら状態はアーカイブのまま）→ 発行の門（従来どおり）。
  - 移行: 既存のアーカイブ済みの行の `ArchivedAt` は `UpdatedAt` で埋める（アーカイブ以後に動いた時刻であり、アーカイブの時刻以上。戻しにくい側に倒れる）。`LastNormalizedAt` は埋めない（最初の再正規化までは従前の挙動）。
- **共有層に置かない**: 比較は 1 行で、受け手ごとに判定キー（`UpdatedAt` / `NormalizedAt`）・保存の精度（Qdrant のミリ秒 / Postgres のマイクロ秒）・印の置き場所が違う。契約（`Knowledge.Contracts`）に振る舞いを持ち込まない（IADR-0596 決定 4）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | Wiki: 新しい版の後に古い版が届いても、Wiki.js とメタデータが新しい版のまま（古い版は本文を読まず push しない） | `WikiSyncOrderingTests.保持中より古い更新は反映されない_厳格化後に公開へ戻らない` |
| 2 | Wiki: 機密区分を厳格化（`public` → `confidential`）した後の古い版で、Wiki.js のページが公開に戻らず、メタデータの機密区分も緩まない | 同上 |
| 3 | Wiki: 削除の後の古い版で Wiki.js にもメタデータにも書き戻さない。削除より新しい版（作り直し）は載る（陽性対照） | `…削除の後に古い版が届いても書き戻されない`・`…削除より新しい版は作り直される` |
| 4 | Wiki: 撤去（露出の門）・アーカイブの後の古い版で復活しない | `…撤去の後に古い版が届いても書き戻されない`・`…アーカイブの後に古い版が届いても公開へ戻らない` |
| 5 | Wiki: 新しい版を反映した後の古い削除は適用しない | `…作り直した後に古い削除が届いても消さない` |
| 6 | Wiki: 同じ版の再配送は弾かず同じ内容に収束する（行は 1 つ） | `…同じ版の再配送は収束する` |
| 7 | Wiki: 2 つの版が並行に処理され古い版の push が後に着いても、保存で負けた側が保存済みの最新の状態へ Wiki.js を合わせ直す | `…並行して保存で負けた古い版は最新の状態へ合わせ直す`・`…並行して削除に負けた古い版はページを消し直す` |
| 8 | カタログ: 新しい再正規化（`confidential`）の後に古い再正規化（`internal`）が届いても台帳が新しいまま。本文を読まず `DocumentUpdated` も出さない | `CatalogNormalizationOrderingTests.保持中より古い再正規化は反映されない_厳格化後に緩和が復活しない` |
| 9 | カタログ: アーカイブより前に作られた再正規化はアーカイブを戻さない。アーカイブより後の再正規化は戻す（陽性対照・ADR-0141 決定 1 (a)） | `…アーカイブより前の再正規化はアーカイブを戻さない`・`…アーカイブより後の再正規化はアーカイブを戻す` |
| 10 | カタログ: 削除より前の再正規化で台帳へ作り直さない。削除より後の再正規化は作る（陽性対照） | `…削除より前の再正規化は台帳へ作り直さない`・`…削除より後の再正規化は作り直す` |
| 11 | カタログ: 同じ `NormalizedAt` の再配送は反映する（冪等） | `…同じ版の再配送は反映される` |
| 12 | 上記は防御を外すと赤になる | 下の §変異 |
| 13 | マイグレーションがモデルと一致する（Wiki・カタログの両方） | `dotnet ef migrations add` で生成（スナップショットと同時に更新）。表 `SyncMarks`（主キー `PK_SyncMarks`）・表 `DocumentDeletionMarks`・列 `LastNormalizedAt` / `ArchivedAt`（いずれも null 可） |
| 14 | 削除の口が削除の事実を残す | `DocumentDeletionMarkEndpointTests.削除の口は削除の事実を残す` |

### 変異（防御を外すと赤になること。2026-10-23 実測）

| 変異 | 赤になった試験 |
| --- | --- |
| Wiki: 更新の順序の門を外す（`IsStaleUpdate` → 常に false） | 1・2・3・4・7・判定の純関数（7 件） |
| Wiki: 削除の順序の門を外す（`IsStaleDelete` → false） | 5・判定の純関数（2 件） |
| Wiki: 合わせ直しをしない（負けたら常に投げる） | 7 の 2 件 |
| Wiki: 削除済みで同じ時刻の更新を古いとしない | 3・判定の純関数（2 件） |
| Wiki: 印の並行トークンを外す | 7 の古い版・対照（2 件。削除との並行は行の消失で突き合わせに負けるので残る） |
| Wiki: 撤去の分岐で印を進めない | 4 の撤去（1 件） |
| Wiki: アーカイブの分岐で印を進めない | 4 のアーカイブ（1 件） |
| Wiki: 削除で印を残さない | 3・7 の削除（2 件） |
| Wiki: 比較をティックのまま行う | 判定の純関数（1 件） |
| カタログ: 古い再正規化の門を外す（`IsNormalizationStale` → false／受け口の門を外す） | 8（各 1 件） |
| カタログ: 再正規化で `LastNormalizedAt` を進めない | 8（1 件） |
| カタログ: アーカイブを戻さない判定を外す（`KeepsArchiveAgainst` → false） | 9・移行の判定（2 件） |
| カタログ: アーカイブで時刻を残さない | 9 の 2 件 |
| カタログ: 削除の事実の判定を外す（`Supersedes` → false） | 10・削除の口（2 件） |
| カタログ: 削除の口で削除の事実を残さない | 削除の口（1 件） |

## 限界（残余）

- **`UpdatedAt` を進めない変更の順序**（#2150）。等しい版として届くので後着が勝つ。
- **発行側の時計**: Wiki の版は DocumentService の時計、カタログの版は ConversionService の時計である。カタログのアーカイブ・削除の時刻（DocumentService の時計）と `NormalizedAt`（ConversionService の時計）を比べる箇所は、時計の差が再同期の間隔より大きいと誤る（GraphService・IADR-0595 と同じ前提）。
- **Wiki の 3 者以上の並行**: 合わせ直しの push 自体は突き合わせない。合わせ直しの最中にさらに新しい版が push と保存を終えると、合わせ直しの push（1 つ前の版）が後に着く窓が残る。Wiki.js への書き込みをレプリカ間で直列にする #2100 で閉じる。
- **カタログの並行**: 2 つの再正規化が同時に台帳を書く窓は、#2103（`Version` の並行トークン）が閉じる。
- **カタログで利用者の編集と古い再正規化が交差する**: 再正規化は属性を取り込みの値で置き換える（取り込みが正本）。再正規化が作られた後・届く前に利用者が属性を編集すると、届いた再正規化がその編集を上書きする（従前と同じ）。順序の判定キーが別の時計の別の列（`UpdatedAt` は利用者の編集でも進む）であり、本 PR では扱わない（報告に未決として残す）。
- **移行**: 配備時点で Wiki の印を持つ文書は無い。最初の更新までは従前の挙動（印が無ければ反映する）。

## 影響範囲

- コード: `WikiService`（`Domain/DocumentSyncMark.cs` 新設・`Features/Wiki/SyncDocument/DocumentSyncConsumer.cs`・`Features/Wiki/RemoveDeleted/DocumentDeletedConsumer.cs`・`Infrastructure/Persistence/WikiDbContext.cs`・マイグレーション）、
  `DocumentService`（`Domain/Document.cs`・`Domain/DocumentDeletionMark.cs` 新設・`Features/Documents/Catalog/DocumentNormalizedConsumer.cs`・`Features/Documents/Delete/Endpoint.cs`・`Infrastructure/Persistence/DocumentDbContext.cs`・マイグレーション）。
- 試験: `WikiService.Tests`（新設 1）・`DocumentService.Tests`（新設 1）。
- 文書: `docs/functional/FR-13_wiki-browsing.md`・`docs/functional/FR-06_document-crud-versioning.md`・`docs/tests/FR-13_wiki-browsing-abac.md`・`docs/tests/FR-06_document-crud-versioning.md`。
- 配備: wiki_svc・document_svc に表と列が増える（起動時のマイグレーション）。helm・pipeline.json の変更なし。

## 検証

- `dotnet build` / `dotnet test`（WikiService.Tests・DocumentService.Tests）・`dotnet format --verify-no-changes`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書系の検査器。結果は PR 本文に記録する。

## ［2026-10-23 追記 / #2102］レビュー対応で直した点

- **表名の誤り**: §設計の「Wiki の版の印: 新しい表 `DocumentSyncMarks`」は誤りで、実装の表名は **`SyncMarks`**（主キー `PK_SyncMarks`）である（受け入れ基準 13 と同じ）。`DocumentSyncMarks` という表は無い（カタログの削除の事実の表は `DocumentDeletionMarks`）。
- **移行で `ArchivedAt` を埋めない**: §設計の「移行: 既存のアーカイブ済みの行の `ArchivedAt` は `UpdatedAt` で埋める」は採らなかった。マイグレーションは列を null 可で足すだけで値を埋めず、`ArchivedAt` が無いアーカイブは実行時に `UpdatedAt` で読む（IADR-0596 決定 6・試験「アーカイブの時刻が無ければ更新時刻で読む」）。戻しにくい側に倒れる性質は同じである。
- **初回の同期の並行（実 PostgreSQL で判明）**: 印がまだ無い文書を 2 者が同時に同期すると、後から保存する側は印の主キー `PK_SyncMarks` より先に同期メタデータの一意索引 **`IX_Pages_DocumentId`** で 23505 になる（挿入の順で決まる）。当初の実装はこれを突き合わせの負けと読まず、再試行で古い側が「古い」と判定して ack し、Wiki.js に古い版の push を残した。**両方の制約名を突き合わせの負けとして扱う**ように直した（IADR-0596 決定 3）。
  - 試験: `Knowledge.IntegrationTests` の `WikiSyncMarkPostgresTests`（実 PostgreSQL・本番と同じマイグレーション。3 件）。CI では `integration.yml`（develop への push と日次）で走り、PR の `ci.yml` は `Category!=Integration` で外す。手元では PostgreSQL 16 を立てて `PLATFORM_TEST_POSTGRES` で与えて実走した（3 件合格）。
  - 変異: `IX_Pages_DocumentId` を突き合わせの負けとしない → 「初回の同期で印の作成に負けた古い版は…」が赤。`PK_SyncMarks` を突き合わせの負けとしない → 「印の同時作成は…」「初回の同期で削除に…」の 2 件が赤。

