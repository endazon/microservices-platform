---
title: 作業仕様書 — 一括アーカイブ（#1931）と境界語の除去（#1973）の「発行に失敗したときの回収」を、ログの ID ではなく母集合の再発行へ改める（#1983）
type: spec
status: done
related_ids: [FR-06, IADR-0503, IADR-0549, IADR-0556]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
---

# 作業仕様書: 一括の口の発行失敗の回収をログの ID に頼らない形へ改める（#1983）

> 本仕様書は着手前に作成した（着手 2026-10-17）。基点は MSP `origin/develop` `9a9f14b8`（#1982 のマージ後）。

## 起点

- #1983（MSP#1982 の AI レビューの指摘）。`archive-bodyless`（IADR-0549）と `strip-untrusted-boundary`（IADR-0556）の口は、
  ページ全体を書き換えて `SaveChanges` した後に、1 件ずつ監査ログの行を出して `PublishUpdatedIfIndexableAsync` で発行する。
  発行がページの途中で例外になると、それより後の文書は台帳では書き換え済みなのに、監査の行も発行も無い。
  運用仕様書の回収手順（ログの行から ID を引いて `--ids`）ではこれらを拾えない。
- 方針（指示）: issue の案 (a)。**文書の変更だけで直し、コードは変えない**。新しい IADR は起こさず、IADR-0549・IADR-0556 へ日付つきで追記する。
  先例は #1974（MSP#1982）の Quote のアーカイブの回収（母集合指定の再発行）。

## 着手前の調査

- 口の並び（コードを読んだ）: `Features/Documents/ArchiveBodyless/Endpoint.cs` 126〜140 行・`Features/Documents/StripUntrustedBoundary/Endpoint.cs` 174〜188 行とも
  「全件を書き換え → `SaveChangesAsync` → foreach { `LogInformation`（監査）→ `PublishUpdatedIfIndexableAsync` }」。issue の記述どおり。
- 版の注記: 両方の口とも `Document` の遷移が `Snapshot(changeNote)` で `DocumentVersions` に 1 行足し、同じ `SaveChanges` で保存される。
  注記は固定（`ArchiveBodylessDocumentsEndpoint.ChangeNote` = `archived: body object missing (NoSuchKey) — bulk archive of pre-cutover documents (#1931)`、
  `StripUntrustedBoundaryEndpoint.ChangeNote` = `untrusted-boundary stripped from title/body — one-off migration of AST documents (#1973)`）。
  → **発行が途中で失敗しても、版の注記は全件に残る**。ログの行に代わる母集合の一次情報になる。
- 列名: `DocumentVersions` の `DocumentId` / `Version` / `ChangeNote`（モデルのスナップショットで確かめた）。
- 再発行の駆動器（`scripts/republish-document-updated.js`）: `--ids` は 500 件まで・`--status` は `draft/normalized/published/archived`・
  `--resume` は `--ids` 等の絞り込みと併用不可・dry-run は状態ファイルを書かない・新規の走査で状態ファイルが既にあると止まる・完了済みの状態ファイルを `--resume` すると何もせず 0。
  → 500 件ごとに塊へ分け、塊ごとに状態ファイルを分ける。流し直しは「状態ファイルが在れば `--resume`、無ければ `--ids`」のループで冪等にできる。
- 一括アーカイブの dry-run の「既にアーカイブ済み」は `CreatedAt < createdBefore` かつ archived の全件（同 Endpoint.cs 66 行）。
  → `--created-before 2026-09-27T00:00:00Z --status archived` の再発行の dry-run の「対象」と同じ件数になる（照合に使える）。

## 母集合（規則 9・10。誤りの側の文字列で走査）

`git grep -n "Archived body-less document\|Stripped untrusted boundary from document\|ログの行で ID\|ログの ID"`（`.cs` を除く）と、
`.cs`・`scripts/` の「回収」「--ids」の走査で引いた。

| 箇所 | 扱い |
| --- | --- |
| `docs/operations/operations.md` 一括アーカイブの「口が 500 で止まったとき」 | **直す**（ログの行を一次に挙げている） |
| `docs/operations/operations.md` 境界語の「口が 500 で止まったとき」 | **直す**（ログの行だけ） |
| `docs/operations/operations.md` Quote の節 | 対象外（#1974 で既に母集合の再発行） |
| IADR-0549 §結果 の回収（決定 6 の SQL ＋ `--ids`） | 本文は凍結。**日付つき追記**で 500 件の分割と「最後の版」の形・状態での再発行を足す |
| IADR-0549 §PoC 手順 2・3 のログの行数の突き合わせ | 確認の手段で回収の手段ではない。追記で「回収の母集合には使わない」と明記 |
| IADR-0556 §結果 の回収（ログの行） | 本文は凍結。**日付つき追記**で置き換えを宣言する |
| IADR-0556 §PoC 手順 2 のログの行 | 確認の手段。追記で同上 |
| `.ai-context/specs/20261017_1973_…` 115 行 | 凍結記録。書き換えない（追記も不要。IADR-0556 の追記が後継を示す） |
| 駆動器の失敗時の文言（`admin-page-driver.js` 411 行「口はページの途中まで保存しない」） | 対象外（コードは変えない方針。保存前の失敗についての文言として正しい） |
| `.cs` の「回収できる」コメント（ArchiveBodyless の 502） | 対象外（保存前に失敗する 502 の話） |

## 方式

1. 運用仕様書の 2 つの「口が 500 で止まったとき」を、Quote の節と同じ形（「🔴 ログの ID では回収しない」＋理由＋**母集合で再発行する**）に改める。
   - archive-bodyless: (a) 既定 `--created-before 2026-09-27T00:00:00Z --status archived`（件数の上限なし・費用なし。単票でアーカイブした切り替え前の文書も含むが害は無い）。
     (b) 版の注記から ID を引く SQL → `split -l 500` → 塊ごとの dry-run → 塊ごとに状態ファイルを分けたループ。
   - strip-untrusted-boundary: (a) 既定 版の注記から ID を引く SQL → 同じループ。(b) `--attr project=ai-stock-trading`（SQL を使えないとき。剥がしていない文書も埋め込み直すので費用が大きい旨を明記）。
   - SQL は「文書ごとの**最後の**版の注記が当の文字列」で引く（その後に別のイベントが出た文書を外す）。注記の em dash を端末で打たずに済むよう `LIKE '<前半>%(#NNNN)'` にする。
2. IADR-0549・IADR-0556 に `［2026-10-17 追記 / #1983］` の節を足す（本文は凍結のまま）。`related_specs` に本仕様書を足す。
3. 運用仕様書の trace ブロック（`specs` に本仕様書・`issues` に #1983）を追随させる。`updated:` は 3 文書とも既に 2026-10-17（当日）。

## 受け入れ基準と検証

- AC1: 運用仕様書の 2 つの口の回収手順が、どれもログの行を前提にしていない
  → `sed -n` で 2 節を読み、回収の手段がログの行でないことを確かめる。`Archived body-less document` / `Stripped untrusted boundary from document` の出現は「出ていない」理由の説明だけ。
- AC2: SQL が正しい母集合を引く → 手元の PostgreSQL 16（使い捨て）に `Documents` / `DocumentVersions` の最小の表を作り、
  A（一括アーカイブが最後）・B（一括アーカイブの後に再正規化）・C（単票のアーカイブ）・D（剥がしが最後）・E（剥がしの後に Quote のアーカイブ）・F（無関係）＋剥がし 1,202 件で、
  運用仕様書のコードブロックを抜き出し `kubectl … psql` を手元の psql に、駆動器を引数を記録する stub に置き換えて流す。
  期待: archive-bodyless → A だけ（1 件）、strip → D ＋ 1,202 件（1,203 件。E を含まない）。塊は 500 / 500 / 203。ループの流し直しは全塊が `--resume` になる。
  **変異**: 「最後の版」をやめて「どれかの版の注記」で引くと archive-bodyless が B を、strip が E を拾う（形が判別していることの確認）。
- AC3: 文書系検査（`check-trace-blocks.js`・`gen-knowledge-graph.js --check`・`check-cross-repo-refs.js`・`check-plan-id-qualification.js`・`check-reading-budget.js`・
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）が通る。

## 残余

- 口の並び（監査の行と発行を保存の直後にまとめて出す。案 (b)）は本件では変えない。ログの行は今後も「出た分の記録」であり、回収の母集合には使わない。
- (a) の再発行は、発行できていた文書にも同じイベントを出し直す（冪等。境界語は剥がした件数分の埋め込みの費用がもう一度かかる）。
