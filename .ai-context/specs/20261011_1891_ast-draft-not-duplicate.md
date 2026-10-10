---
title: 作業仕様書 — AST の承認待ちの報告書の写し（reportState=draft）を、古い写しの棚卸しで報告書の重複に数えない（#1891）
type: spec
status: done
related_ids: [FR-19, FR-06, FR-05, SC-05, ADR-0122, ADR-0121, ADR-0061, IADR-0484, IADR-0529]
author: claude
created: 2026-10-11
updated: 2026-10-11
issue: "#1891"
---

# 作業仕様書 — 承認待ちの報告書の写しを、古い写しの棚卸しで報告書の重複に数えない（#1891）

> 本仕様書は実装着手前に作成した（着手 2026-10-11）。基点は MSP `origin/develop` `7a9f8cc9`。
> AST の写しの形は AST の隣接クローン（`c7458ab6`。読み取り専用）で読んだ。

## 起点（トレーサビリティ）

- **#1891**（本件）。関連: AST#1301（ドラフトの写しを知識ユニットへ置く）、planning#784（利用者裁定 2026-10-10）、#1886（IADR-0529）、#1784（本件が前提条件）。
- **FR-19**（露出 3 属性。ドラフトの写しは 3 つとも `excluded`）、**FR-06 / FR-05 / SC-05**（文書管理・管理者の削除）。
- **計画 ADR-0122** 決定 1・2（古い写しは作成の経路と属性で見分ける）・決定 3（入れ直しの後の確認）、**ADR-0121** 決定 3。
- 実装: **IADR-0484**（見分けの規則。凍結記録として書き換えない）・**IADR-0529**（ドラフトを組織文書として置く）。

## 問題（実測 `7a9f8cc9`）

AST のドラフトの写し（`ReportKnowledgeMapper.DraftAttributesOf` / `ToDraftDocument`）は次の形を持つ。

| 属性・表題 | 値 |
| --- | --- |
| 表題 | `報告書ドラフト {kind} {periodKey}` |
| `kind` / `periodKey` | 確定版と同じ |
| `reportState` | `draft`（確定版は持たない） |
| `project` | `ai-stock-trading`（AST の KB の書き手が必ず付ける） |
| 露出 3 キー | すべて `excluded` |

`AstStaleCopyRules.IsReport` は `kind`・`periodKey`・`project` があれば表題を見ずに報告書の写しと判定する。
そのため、所有者が現在のサービスアカウントのドラフトは `owned-by-current-account` かつ `Category=Report` となり、
`currentAccountReports` の数えに入る。確定後にドラフトの削除が失敗して残ると、確定版とドラフトが同じ
`(kind, periodKey)` の重複の組として出る。runbook の失敗の分岐は「重複した組の新しい方を消す」と案内しており、
ふつうはドラフトのほうが古いので、**手順どおりに操作すると確定版を消す**。

## 決めたこと

1. **`IsReport` は `reportState=draft`（値の完全一致。大小を区別する。AST は `draft` だけを書く）を持つ文書を偽にする。**
   判定の位置は「形」（規則の 4 番目）であり、ドラフトは `not-ast-shape` に数えられる。
   - 重複の数え（`currentAccountReports`）だけでなく**古い写しの対象（`targets`）からも外れる**。ドラフトの後始末は AST の責務
     （確定・入れ直しでドラフトを消す）であり、基盤の管理者が棚卸しの口から消す対象にしない（消す側へ倒さない）。
   - 新しい理由の鍵は足さない（応答の契約 `excluded` の鍵の集合と並びを変えない。IADR-0484 決定 1 の順序の契約を保つ）。
   - 表題（`報告書ドラフト `）は条件にしない。属性だけで外す（表題を変えられても確定版の重複に紛れない）。
2. **IADR は作らない。** 規則の形の判定に 1 条件を足すだけで、IADR-0484 の決定（順序・理由の鍵）を変えない。根拠はこの仕様書とコードの注記に置く。
3. runbook に「`reportState=draft` の文書は承認待ちの写しであり、古い写しの対象にも重複の解消の対象にもならない（`not-ast-shape` に数えられる）」と追記する。

## 受け入れ基準

- **AC-1（陽性: draft は報告書の写しに数えない）**: `kind`・`periodKey`・`project=ai-stock-trading`・`reportState=draft` の文書は、
  所有者が現在のサービスアカウントでも `system` でも無しでも `not-ast-shape` になり、`Category` を持たない。
- **AC-2（陽性: 列挙の口）**: 現在のサービスアカウントの確定版 1 件と、同じ `(kind, periodKey)` のドラフト 1 件（確定版より古い）を置くと、
  `currentAccountReports.count=1`・`duplicates` は空、`targets.total=0`、`excluded["not-ast-shape"]=1`。
- **AC-3（陰性: 確定版どうしの重複は従来どおり）**: 現在のサービスアカウントの確定版 2 件（同じ組）は従来どおり重複の組として返る。
  同じ台帳にドラフトを足しても組の件数（2）は変わらない。
- **AC-4（陰性の対照）**: `reportState` が `draft` 以外（`confirmed`・空白）の報告書は従来どおり報告書として判定される。
- **AC-5**: runbook の追記（`docs/operations/ast-stale-copies-deletion-runbook.md`）と trace ブロックの更新。テスト仕様書 T-75 の追記。
- **AC-6（変異）**: `IsReport` の draft の条件を外すと AC-1・AC-2 の試験が赤になる。

## 母集合（規則 9・10）

誤りの側の語（`duplicates` / `currentAccountReports` / `重複した組` / `not-ast-shape` / `NotAstShape`）で `git grep`（`.ai-context/specs` を除く）した結果:

| ヒット | 扱い |
| --- | --- |
| `Features/Documents/AstStaleCopies/AstStaleCopyRules.cs`・`Endpoint.cs` | 是正（規則と注記） |
| `Tests/.../AstStaleCopyRulesTests.cs`・`AstStaleCopiesEndpointTests.cs` | 試験を追加 |
| `docs/operations/ast-stale-copies-deletion-runbook.md`（67・68・100・111・123・137 行） | 67（`not-ast-shape` の説明）・123（失敗の分岐）・限界の節に追記。100・111・133 は記述がそのまま正しくなる（ドラフトが数えに入らない）ので変えない |
| `docs/tests/FR-06_document-crud-versioning.md`（T-75） | 期待へ 1 文追記 |
| `.ai-context/adr/IADR-0484_…md`（58・77 行） | **除外**: 凍結記録。理由の鍵の並びは変えていないので記述は誤りにならない |
| `.ai-context/adr/IADR-0168_…md`・`scripts/check-grafana-provisioning-parity.js`・`scripts/check-knip.js`・`AuthorizationService/…/UserAssignmentValidationTests.cs` | **除外**: 別の意味の `duplicates`（無関係） |

規則 10（自分の記述で新たに誤りになるもの）: runbook の「`not-ast-shape`（報告書・記事の属性の形でない）」は、ドラフトが形を満たすのに `not-ast-shape` へ入るため、説明を足して誤りにしない。

## 範囲外

- 一部だけ除外した露出・ドラフトの表題の扱い（IADR-0529 の残余）。
- AST 側の入れ直し・ドラフト削除の挙動（AST#1301）。
- #1895・#1871 のファイル（並行作業）には触れない。
