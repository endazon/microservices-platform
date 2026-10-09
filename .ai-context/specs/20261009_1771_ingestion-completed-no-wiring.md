---
title: 作業仕様書 — IngestionCompleted は結線しないとの裁定（planning#741 項目 6）に合わせ、機能仕様書から結線の約束を外す（#1771 受け入れ基準 1）
type: spec
status: done
related_ids: [FR-01, FR-02, ADR-0027, IADR-0234, IADR-0314]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/10_feedback/20261009_audit-b12-b13-dept-sync-ingestion.md (§裁定 項目 6・§残るもの)
  - planning:projects/microservices-platform/04_workflows/01_ingestion-pipeline.md (`Ing-->>MQ: IngestionCompleted`＝発行だけ)
issue: "#1771"
---

# 作業仕様書 — IngestionCompleted は結線しない（#1771 受け入れ基準 1）

> 本仕様書は編集着手前に作成した（着手 2026-10-09）。基点は MSP `origin/develop` `c66c5641`。
> 計画は project-planning `origin/main`（隣接クローン・読み取り専用）。
> 文書の是正とコードのコメントの是正であり、挙動（コード・テスト・配備・baseline）は変えない。

## 起点（トレーサビリティ）

- #1771（第 4 回全体監査 B-13）。受け入れ基準 2〜5 は PR #1779（作業仕様書 `20261007_1771_ingestion-event-wiring-docs.md`。`Refs #1771`）で処理済み。
  **本 PR は残る受け入れ基準 1 を処理し、#1771 を閉じる。**
- 利用者裁定（planning#741 項目 6・2026-10-09。完了記録 planning `projects/microservices-platform/10_feedback/20261009_audit-b12-b13-dept-sync-ingestion.md`）:
  **`IngestionCompleted` は結線しない。計画の改訂は要らない。** 結線の約束は計画ではなく実装の `docs/functional/`（FR-01・FR-02）にあった。
  計画のワークフロー図は発行だけを書き（`Ing-->>MQ: IngestionCompleted`）、要求書の FR-01・FR-02 は事象名にも連鎖にも触れない。
- 計画 ADR-0027（Wolverine 採用）。IADR-0234（移行単位 E4）・IADR-0314（購読 0 件の記録）。

## 受け入れ基準

- AC1: `docs/functional/FR-01_data-source-catalog.md` の図と処理フローから、`IngestionCompleted` の購読者（DocumentService）への矢印を外し、
  「発行するが購読者は無い（結線しない）」と書く。
- AC2: `docs/functional/FR-02_ingestion.md` の「後続の検索反映へ連鎖する」を外し、購読者が無いこと・検索への反映は Qdrant への登録の時点で成立することを書く。
- AC3: `docs/tech/composability-classification.md` §6 の「計画側の裁定待ち」を閉じる（結線しないと裁定済み）。
- AC4: 母集合（下記）で見つけた「結線を約束・期待する」記述を、live な文書・コードのコメントは是正し、凍結記録は日付つき追記
  `［2026-10-09 追記 / #1771］` で扱う。本文は書き換えない。
- AC5: 発行そのもの（`MassTransitIngestionCompletedPublisher`）・契約型・baseline・テスト（T-06）は変えない。
  E4 で発行を Wolverine へ移すか撤去するかは本 PR では決めない（完了記録 §残るもの が実装に委ねている）。

## 母集合（規則 9・10）

`git grep -n "IngestionCompleted" -- ':!src/ai-stock-trading'`（誤りの側の文字列）と、約束の言い回し
`git grep -n "検索反映\|後続.*連鎖\|IngestionCompleted.*購読\|購読.*IngestionCompleted"` で全文書・コードを走査した（基点 `c66c5641`）。

### 是正する（live）

| 箇所 | 現状 | 処置 |
| --- | --- | --- |
| `docs/functional/FR-01_data-source-catalog.md:56` | 図 `ING -->\|IngestionCompleted\| DOC` | 矢印を外し、発行先を購読者の無いブローカへの点線で示す |
| `docs/functional/FR-01_data-source-catalog.md:67` | 処理フロー 5「… → `IngestionCompleted`」 | 「発行する（購読者は無い。結線しない）」と書き足す |
| `docs/functional/FR-02_ingestion.md:44` | 「後続の検索反映へ連鎖する」 | 外す。購読者は無く、検索への反映は Qdrant 登録で成立すると書く |
| `docs/tech/composability-classification.md:126` | §6「計画側の裁定待ち（2026-10-07 時点）」 | 「結線しないと裁定済み（2026-10-09）」へ閉じる |
| `src/knowledge/.../IngestionService/Features/Ingestion/Ingest/DocumentUpdatedConsumer.cs:197` | コメント「検索反映へ連鎖」 | 誤解を招くので直す（購読者なし・結線しない） |
| `src/knowledge/.../IngestionService/Features/Ingestion/Ingest/DocumentUpdatedConsumer.cs:24` | コメント「購読者の要否は planning#741 項目 6 の裁定待ち」 | 裁定済み（結線しない）へ直す。**初回の走査表から漏れ、検証の再走査（規則 10。「裁定待ち」で引き直し）で見つけた** |
| `src/knowledge/.../IngestionService/Domain/Ports/IIngestionCompletedPublisher.cs:16` | コメント「購読者の要否は planning#741 項目 6 の裁定待ち」 | 裁定済み（結線しない）へ直す |
| `src/knowledge/.../IngestionService/Infrastructure/Messaging/MassTransitIngestionCompletedPublisher.cs:18` | 同上 | 同上 |

### 日付つき追記（凍結記録）

| 箇所 | 処置 |
| --- | --- |
| `.ai-context/adr/IADR-0234_…md:207`（2026-10-07 追記の「E4 の中身は本追記では決めない … 計画側の裁定待ち」） | `［2026-10-09 追記 / #1771］` を足す: 結線しないと裁定。E4 は発行側だけの単位であり、「購読 0 の明記」の側で示す。移すか撤去するかは実装が決める |
| `.ai-context/specs/20260627_FR-02_ingestion-pipeline.md:86`（受け入れ基準「後続（検索反映）へ連鎖できる」） | `［2026-10-09 追記 / #1771］` を足す（specs は経過追記が可）。反映時間の前提は Qdrant 登録で満たす |

### 除外（理由つき）

| 箇所 | 理由 |
| --- | --- |
| `.ai-context/adr/IADR-0314_…md:100`「購読 0 件であり、本決定の対象外」 | 裁定と一致する事実の記録。約束を含まない。追記不要 |
| `.ai-context/adr/IADR-0059`・`IADR-0497:111` | 契約の列挙・ChunkCount の意味。結線の約束ではない |
| `.ai-context/specs/` の他の出現（`20260708_issue-111`・`20260804_issue-465`・`20260821_*`・`20260822_*`・`20260828_*`・`20260903_*`・`20261007_1771_*`・`20261007_1772_*`・`20261008_1799_*`） | point-in-time の記録。いずれも発行・購読 0・裁定待ちを当時の事実として書いたもので、結線を約束しない |
| `.ai-context/superpowers/*` | 凍結（追記不可）。契約型の当初設計のみ |
| `docs/functional/FR-02_ingestion.md:62`（処理フロー 8「発行する」）・`:67`（E2） | 発行の事実。正しい |
| `docs/tech/composability-classification.md:54`「（現在購読者なし）」 | 正しい。「現在」は裁定後も成立する（変えない） |
| `docs/tests/FR-02_ingestion.md:32` T-06「取り込み後に発行される」 | 発行の試験。結線を期待しない。区分欄の「取り込み: 連鎖」は試験区分の名であり変えない |
| `docs/tests/NFR-01_performance-load-test.md:45` P-03・`perf/k6/README.md:51` | 計測点（`IngestionCompleted` または Qdrant points 数）。購読者を要しない。E4 で発行を撤去する場合は計測点を Qdrant に寄せる（完了記録 §残るもの）—— その時の PR が追随する |
| `docs/operations/operations.md:945` | 発行の件数の意味。約束を含まない |
| `deploy/helm/.../pipeline.json`・`scripts/*baseline.json`・`scripts/check-event-topology.js`・`scripts/scripts.repo.test.js`・`scripts/README.md` | 発行の宣言・検査の素材。購読者を宣言していない |
| `src/.../Program.cs:23`・`:125`・`Knowledge.Contracts/*`・テスト各所 | 発行の実装・観測。結線を主張しない |

### 規則 10（この変更で新たに誤りになる自分の記述）

- 「購読者は無い（結線しない）」は E4 で発行を撤去すると「発行もしない」へ変わる。その PR が FR-01 / FR-02 / 区分表を追随する
  （今回の 3 文書は `IngestionCompleted` の文字列で引ける）。
- 「裁定待ち」の文字列で再走査し、live な文書・コードに残っていないことを確かめる（検証）。
  実測: 初回は `DocumentUpdatedConsumer.cs:24` が残っていた（上の表へ追加して是正）。是正後は `.ai-context/` の凍結記録（IADR-0234 の 2026-10-07 追記本文・作業仕様書 2 件）と本仕様書だけになった。

## IADR

新たな実装判断は無い（裁定は計画側の記録が持ち、E4 の中身は先送り）。IADR は起こさず、IADR-0234 へ日付つき追記を足す。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `check-trace-blocks` / `check-commit-messages --base origin/develop` / `check-doc-updated --base origin/develop` / `gen-knowledge-graph --check` /
  `check-reading-budget` / `check-plan-id-qualification` / `check-cross-repo-refs` / `check-doc-links` / `check-test-spec-coverage` / `check-test-traceability`
- コメントだけの C# 変更のため `dotnet format --verify-no-changes`（IngestionService）で整形差分が無いことを確かめる。
