---
title: 作業仕様書 — 取り込み経路がタグを生成・上書きしないようにする（#637）
type: work-spec
status: done
related_ids:
  - FR-01
  - FR-06
  - SC-05
  - SC-09
  - SC-10
  - UC-03
  - UC-04
  - IADR-0153
  - IADR-0119
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
related_specs:
  - "../adr/IADR-0153_tag-identity-storage-and-projection.md"
  - "../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md"
---

# 作業仕様書 — 取り込み経路がタグを生成・上書きしないようにする（#637）

## 起点となる計画書（トレーサビリティ）

**利用者裁定 2026-08-09**（裁定依頼 planning#304 → planning#305 で計画へ反映。走査基準: planning `2cf0795`）。

| 種別 | 何を求めているか |
| --- | --- |
| `06_technical/09_datasource-connectors.md` §取り込み経路はタグを生成しない（**正**） | **ソースのメタからタグを作らない。** 所在・部門・フォルダ・更新者等は **ABAC 基本属性へ写像する。タグの生成先ではない** |
| **SC-05** | 「既定タグ辞書に整合」は**経路を問わない不変条件**である。**再正規化はタグ欄を上書きしない** |
| **SC-09** | 取り込み経路から辞書は増えない |
| **SC-10** / `06_technical/05_observability-ops.md` | 「取り込み経路で辞書に無いタグが現れた件数」を**ナレッジ健全性の指標**へ（6 → 7 指標）。**0 が正常** |

**これは新たな制限ではない。** 同節は当初からソースのメタの写像先を「ABAC 基本属性」と定めており、
「フォルダ → タグ」とは書いていない。2026-08-05 に SC-01 / SC-08 / UC-02 から「フォルダ」を削除した確定も、
**フォルダが取り込み時に属性へ写像されて消える**ことを理由にしており、既に本項を前提にしていた。

## 母集合（[[IADR-0141]] 決定 1）

**着手時に実装側が自分で引いた。走査基準: develop `47006a1`。**

**［重要］裁定依頼に書いた前提を実測しなかったのが #635 での落ち度である**（[[IADR-0153]] §決定を覆した記録）。
**今回は先に測った。**

| # | 対象 | 実測 |
| --- | --- | --- |
| 1 | `DataSourceSyncService.BuildTags` | **親フォルダ名 1 個**を返す（`Path.GetFileName(Path.GetDirectoryName(item.Path))`）。**唯一のタグ生成点である** |
| 2 | `SourceItem` | `(string Path, DateTimeOffset ModifiedAt, long Size)`。**タグの器そのものが無い**——コネクタは構造上タグを運べない |
| 3 | `Document.ApplyNormalized` | `Tags = tags` で**無条件上書き**。`Update` / `UpdateMetadata`（画面経由）も上書きするが、**そちらは利用者の意図した更新なので正しい** |
| 4 | SC-10 の「ナレッジ健全性」節 | **意図的に未実装**（[[IADR-0119]]。FR-17 が着手保留。`OperationsDashboardPage.test.tsx` が**画面に出さないことを固定**している） |
| 5 | メトリクスの先例 | `LlmCompletionMetrics`（OpenTelemetry の `Meter` ＋ `Counter`。`Program.cs` の `AddMeter` で OTLP へ） |

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `Document.Update` / `UpdateMetadata` のタグ上書き | **画面（SC-05）からの意図した更新**である。止めるとタグを外せなくなる |
| `ConversionJob.Tags` ＋ イベントの `Tags` | **契約は変えない。** 取り込みが空を運ぶようになるだけで、器は残る（将来コネクタがタグを運ぶ場合の裁定は planning が保留した） |
| SC-10 の画面表示 | **[[IADR-0119]] で節ごと保留中**。下記「指標の置き場所」を参照 |
| タグの識別子化 | **#635 の射程** |

## 実装方針

1. **`BuildTags` を止める。** `RawDocumentFetched` へ空のタグを渡す。**フォルダは ABAC 基本属性側で扱う**（同節の既定）。
2. **`ApplyNormalized` がタグ欄を上書きしないようにする。** 取り込みはタグを作らないので、**既存のタグを保つ**。
3. **未知タグの件数を OpenTelemetry のカウンタで出す**（下記）。

### 指標の置き場所（実装判断）

**SC-10 の画面へは出さない。** 「ナレッジ健全性」節は [[IADR-0119]] により**節ごと着手保留**であり、
**画面に出さないことをテストが固定している**。ここに 1 指標だけ差し込むと、保留の線引きが壊れる。

**OpenTelemetry のカウンタとして出す**（`LlmCompletionMetrics` と同じ作法）。
**これで裁定の意図は満たせる**——裁定が求めたのは「**0 でない値が検出になる**」ことであり、
Grafana で観測できれば成立する。**画面の行は、FR-17 の保留が解けて「ナレッジ健全性」節を作るときに一緒に置く。**

## テスト（受け入れ基準の写像）

| # | 確かめること |
| --- | --- |
| 1 | 取り込みが**親フォルダ名をタグにしない**（`RawDocumentFetched.Tags` が空） |
| 2 | **再正規化で既存のタグが消えない**（`ApplyNormalized` がタグ欄を保つ） |
| 3 | 画面経由の更新（`Update` / `UpdateMetadata`）は**従来どおりタグを更新できる**（外せる） |
| 4 | 取り込み経路で辞書に無いタグが現れたら**カウンタが増える** |
| 5 | 規定どおり（タグ無し）なら**カウンタは増えない**（0 が正常） |

## 実装中に決めたこと（仕様書からの差分）

### `BuildTags` を「空を返す関数」にせず、呼び出し側へ畳んだ

`BuildTags(SourceItem item) => []` にすると、**引数を使わない関数と無意味な間接**が残る。
説明（なぜ作らないのか）は**呼び出し側のコメント**として残した——
将来ここへタグを足そうとする人が最初に見る場所である。

### 辞書に無いタグは**捨てる**（付けない）

計画が「**SC-05 の辞書整合は経路を問わない不変条件**」と明文化したので、
**取り込み経路から辞書外のタグが入る余地を残さない**。
**黙って捨てない**——カウンタが上がるので「計画に無い経路でタグが生まれている」ことが分かる。
**規定どおり（取り込みはタグを生成しない）なら `incoming` は空**で、この絞り込みは何もしない。

### 指標は OpenTelemetry のカウンタで出し、SC-10 の画面へは出さない

「ナレッジ健全性」節は [[IADR-0119]] により**節ごと着手保留**であり、
`OperationsDashboardPage` が**節を出さないことをテストで固定**している（実測）。
ここに 1 指標だけ差し込むと**保留の線引きが壊れる**。
**裁定が求めたのは「0 でない値が検出になる」ことであり、Grafana で観測できれば成立する。**

### `KnownTagsAsync` を `internal` にした

テストハーネスに本 consumer は**登録されていない**（実測）ため、`ConsumeContext` を組み立てないと
経路を通せない。`RetrievalService.Api` が `HybridSearchService.Finish` で採っているのと同じ作法
（`csproj` の `InternalsVisibleTo`）で、**絞り込みだけを直接検証できるようにした。**

### メトリクスの購読は Meter 名を一意にして混入を防いだ

`MeterListener` は**プロセス全体**を購読する。先例（`CompletionMetricsTests`）は
xUnit のコレクションで直列化してこれを避けているが、本テストは **Meter を毎回作り分けられる**ので
**混入自体を起こさない**形にした（固定名だと並行する他テストの測定が混ざる）。

## 検証記録（実測・すべて本作業の head で走らせた）

**［注目］従前の「フォルダ名 → タグ」にはテストが 1 件も無かった。**
`BuildTags` を削除しても**既存 473 件は 1 件も落ちなかった**（実測）。
**だから今回それを固定するテストを置いた**（`Sync_DoesNotTurnFolderNameIntoTag`）。

| 対象 | 結果 |
| --- | --- |
| `dotnet test knowledge/backend/backend.slnx` | **480 passed / 0 failed**（20 skipped は Docker 依存の統合テスト。**本作業で 7 件追加**。473 → 480） |
| `dotnet test platform/backend/backend.slnx` | **376 passed / 0 failed**（1 skipped。**変化なし**——platform は触っていない） |
| `dotnet format --verify-no-changes`（両ユニット） | OK |
| `check-contract-schema` | **変化なし**（契約は変えていない。`RawDocumentFetched` / `DocumentNormalized` の `Tags` は器のまま） |
| `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-adr-numbering` / `check-test-traceability` / `check-bff-downstreams` / `check-unit-dependencies` / `check-backend-libraries` / `check-landed-subjects` / `check-test-spec-coverage` / `check-i18n-catalogs` / `scripts.repo.test` | すべて OK |

**フロントは触っていない**ので `pnpm` 系の床（カバレッジ・chunk budget）は動かない。

## レビュー指摘への対応（PR #638・AI レビュー）

**🔴 は 0 件。** 🟡 1 件・🟢 1 件をいずれも受け入れた。

| 指摘 | 対応 |
| --- | --- |
| 🟡 **PR タイトルのスコープから `FR-09` と `NFR` が落ちている**（4 コミットの scope 合算は `FR-01,FR-06,FR-09,NFR,SC-05,SC-09,SC-10,IADR-0153`）。`.claude/rules/traceability.md`「★ スカッシュ件名を書き直すときは、スコープの ID を 1 つも落とさない」が名指しする事故型（`bc7bc8e` / #612）と同じ構造 | **指摘のとおり。PR タイトルへ `FR-09,NFR` を足した。** 両方とも本 PR に実体がある——**`NFR`** は `.claude/rules/traceability.md` の NFR 採番追記、**`FR-09`** は [[IADR-0153]] と #635 作業仕様書（タグ辞書）の変更である。**対になる規則（実体を伴わない ID を足さない）にも触れていない**ことを確認した |
| 🟢 `KnownTagsAsync` が**重複した未知タグを複数回カウント**する | **受け入れた。** `Distinct` を通した。**[[IADR-0152]] 決定 2 の使用件数と同じ理屈**である——数えるのは「現れたタグの種類」であって出現回数ではない。テスト `DuplicateUnknownTag_IsCountedOnce` で固定した（knowledge 480 → 481 件） |

**レビューが独立に追試した結果も記録する**（他人の数えを転記しないが、自分の測れなかった範囲は記録する）。
レビュー環境は Docker が使えたため統合テストが skip されず、**knowledge は 500 passed / 0 skipped** だった
（当方の 480 passed + 20 skipped と総数が一致する）。
