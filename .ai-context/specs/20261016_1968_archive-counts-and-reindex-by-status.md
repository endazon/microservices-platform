---
title: 運用仕様書の切り替え前の文書のアーカイブ件数を 10-16 の実測へ直し、再索引の --created-after が本文の在る文書も外す注意を足す（#1968）
type: spec
status: done
related_ids: [FR-06, FR-02, ADR-0141, IADR-0549, IADR-0551]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0141_archive-effect-and-no-unarchive.md
issue: "#1968"
---

# 仕様書: 切り替え前の文書のアーカイブ件数と、状態で絞った再索引（#1968）

## 起点となる計画書（トレーサビリティ）

- FR-06（文書管理。アーカイブ）・FR-02（取り込み。再索引）。ADR-0141（アーカイブ済みを見つかる面から外す）は変えない前提として引く。
- 起点: #1968（オーナーの依頼 2026-10-16。10-16 の dry-run の実測と運用文書の件数の食い違い）。関連: #1931・#1961・#1951。
- 実装は変えない（文書だけ）。IADR は要らない（判断を足さない。数の訂正と、既存の口〔`--created-before` と `--status`〕の組み合わせ方の記録である）。

## 受け入れ基準と写像

| 受け入れ基準 | 写像 |
| --- | --- |
| 「約 5,803 件」を実測（対象 4,046・本文あり 1,755・参照なし 2）へ直す | `operations.md`「本文の無い切り替え前の文書のアーカイブ」の手順 1 のコメント |
| 対象 4,046 件はオーナーの承認でアーカイブ済み・DLQ は増えていない | 同節のコンソールの直後の日付つきの記録 |
| 再索引の手順に「`--created-after` は本文の在る文書も外す。アーカイブの後に状態で絞って再発行する」を足す | `operations.md`「`DocumentUpdated` の再発行（再索引の手段）」の手順 2 の切り替えの項 |
| 10-16 に 1,755 件を voyage_3_5 へ再発行した経緯 | 同項 |
| #1961 の手順コメントの件数に訂正コメント | #1961 へコメント |
| docs/ の規約（trace ブロック・`updated:`）・凍結記録は書き換えない | trace ブロックに本仕様書と #1968 を足した。`updated:` は既に 2026-10-16 |

## 母集合（規則 9・10）

- 誤りの側の文字列で走査した: `git grep -n -E "5,803|5803|約 5,8|4,046|1,755"`・`git grep -n "created-after 2026-09-27" -- docs scripts/README.md`。
  - 生きた文書の当たりは `docs/operations/operations.md` の 1 箇所（「約 5,803 件」）だけ → 直した。`--created-after` の当たりは同ファイルの再索引の項 → 注意を足した。
  - `.ai-context/adr/IADR-0540`・`IADR-0544`・`IADR-0549`・`IADR-0551` と `.ai-context/specs/` の 4 件の「5,803 件」は**凍結記録**であり書き換えない（当時の dry-run の総数として書かれている）。
    IADR-0549 の手順の期待「アーカイブの対象が約 5,803 件」も当時の見込みとして残す。現行の手順の正は運用仕様書である。
  - `scripts/scripts.repo.test.js` の `5803` は試験の入力の数値であり、件数の主張ではない（変えない）。
- 新たに誤りになる自分の記述（規則 10）: 再索引の項の「切り替え前の文書の扱いは別途決める」は、アーカイブ（本文なし）と状態で絞った再発行（本文あり）で扱いが決まった。直後に足した注意がその答えを書くので、元の文は 2026-10-15 時点の記述として残す。
- 状態の値: `--status` は `draft / normalized / published / archived` の完全一致 1 つ（`scripts/republish-document-updated.js` の `STATUSES`）。例の `normalized` は 10-16 の実行に合わせた。他の状態が残る場合の扱いを文書に書いた。

## 検証

- `node scripts/check-trace-blocks.js` / `node scripts/check-doc-links.js` / `node scripts/gen-knowledge-graph.js --check` / `node scripts/check-cross-repo-refs.js` / `node scripts/check-plan-id-qualification.js`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（docs を触ったため）

## 射程外

- Wiki 同期の律速（一括操作時に `wiki-service.DocumentUpdated` が溜まる件）は別 issue として起票だけする。
