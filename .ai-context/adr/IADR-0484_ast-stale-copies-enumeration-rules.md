---
title: IADR-0484 AST の古い写しは、owner ではなく作成の経路（最初の版の ChangeNote）・project・報告書／記事の属性の形で見分け、owner は最後にだけ見る。列挙は管理者だけの読み取り専用の口で行い、除いた件数を理由ごとに返す
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-05, FR-08, FR-19, UC-03, SC-05, NFR-09, ADR-0122, ADR-0121, ADR-0119, ADR-0057, ADR-0036, ADR-0060, IADR-0044, IADR-0075, IADR-0459, IADR-0480, IADR-0483]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0122_ast-stale-copies-deletion-scope-and-switchover-order.md 決定 1・2・3・4・フォローアップ 1・2
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 3・4（段 2）
related_specs:
  - ../specs/20260928_issue-1667_ast-stale-copies-enumeration.md
---

# IADR-0484: AST の古い写しの見分け方と列挙の口（#1667）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-28
- 決定者: claude（#1667。計画 ADR-0122 決定 1 の「見分け方は実装の IADR で決める」の実装側の形）

## 起点・関連

- 関連する計画書 ID: FR-06 / UC-03 / SC-05、FR-05、NFR-09、FR-19、AST/FR-08
- 関連する計画 ADR: **ADR-0122** 決定 1（対象は AST が書いた写しのうち AST の現在のサービスアカウントが所有していないもの。`owner=system` と欠落の両方。
  AST の写しかどうかは `owner` ではなく作成の経路と属性で見分ける。個人資料は含めない。遡及しない。消す前に列挙し、除いた件数を理由ごとに示す）・
  決定 2（収集記事も対象。件数と期間を示す）・決定 3（切替の後は 0 件の確認。列挙の口は両方の場合で作る）・決定 4（口ができるまで段 2 を済んだものとしない）、
  ADR-0121 決定 3・4（段 2。基盤の管理者が消す）、ADR-0057（削除の伝播範囲）、ADR-0119 決定 2（機械が作る文書の `owner`）
- 関連する実装 ADR: [[IADR-0483]]（門の `On` は段 2 の後）、[[IADR-0480]]、[[IADR-0459]]（切替）、[[IADR-0044]]・[[IADR-0075]]（管理の口のロール）
- 他リポジトリ: AST/IADR-0436 決定 2（入れ直しの写しの判定）、AST/IADR-0274（KB へ書く属性）
- 裁定: planning#696（利用者裁定 2026-09-28）

## コンテキストと課題

ADR-0122 は「AST の写しであることは作成の経路と属性で見分ける。見分け方は実装の IADR で決める」とした。
`owner=system` は DataSourceService の予約値（取り込みの経路）でもあり、AST の写しの大半は `owner` を持たない見込みである（同 実測 3・5）。
決めることは次の 4 点だった。

1. 作成の経路を何で見分けるか
2. AST の写しの属性の条件
3. `owner` の条件と、判定の順序（除いた理由の数え方）
4. 口の形（場所・認可・応答）

## 検討した選択肢

| 論点 | 採用 | 退けた選択肢と理由 |
| --- | --- | --- |
| 1 | **最初の版の `ChangeNote` が `created` / `created-with-body`**（`Document.Create` / `CreateWithBody`。取り込みは `normalized`）。版が無ければ「分からない」として除く | 作成時刻の窓（AST の着地の時刻で切る）—— 境界が稼働の時刻に依存し、shallow な履歴では確かめられない。人の SC-05 の文書と区別できない。`OriginalUri` の有無 —— AST の記事は `OriginalUri` を持つので経路を表さない |
| 2 | **`project` が無いか `ai-stock-trading`（大小を区別）** ∧ **報告書の形**（`kind` ∈ Daily/Weekly/Monthly ∧ `periodKey` ∧（project あり ∨ 表題 `確定報告書 {kind} {periodKey}`））**か記事の形**（`kind` ∈ 記事の 6 種 ∧ `source` ∧ `publishedAt` が日時） | `project=ai-stock-trading` だけ —— #665 より前の写し（project なし）を見失う（AST/IADR-0436 の初版と同じ誤り）。表題だけ —— 記事の表題は固定の形を持たない。`department=unassigned` —— #520 より前の写しは持たず、他の経路の既定値と区別できない |
| 3 | **順序は 個人資料 → 作成の経路 → project → 形 → owner**。`owner` が無い・空白・`system` だけを対象にし、`service-account-ai-stock-trading-kb-writer` は「現在のアカウント」、それ以外は「他の主体」として除く。1 件は最初に外れた理由にだけ数える | `owner=system` で先に絞る —— ADR-0122 決定 1 が禁じた形（AST 以外を拾い、欠落を落とす）。`system` を大小を問わずに見る —— 予約値は小文字で書かれ、大小違いは人の名前であり得る（消す側へ倒さない） |
| 4 | **DocumentService の `GET /documents/ast-stale-copies`**。`read` 群（認証）＋口の側で `AdminOnly`。台帳を 1 回引いて純粋関数で分類する。応答は見た件数・対象の件数と期間（報告書・記事）・除いた件数（理由 6 つを 0 件も並べる）・現在のアカウントの報告書の写しの重複・対象の各件。書き込まない | 削除まで行う口 —— ADR-0121 決定 3 は削除を管理者の手作業とし、伝播は既存の削除の口（ADR-0057）に委ねる。BFF の中継・画面 —— 使うのは 1 回（または 0 件の確認の 1 回）で、管理者はクラスタの内側から叩ける（認可サービスの管理 API と同じ運用）。`write` 群 —— 読み取りの口であり、運用者（閲覧の下限）へも開かない |

## 決定

1. **見分けの規則は `AstStaleCopyRules.Classify`（純粋関数）ただ 1 つ**に置き、上の表の順で判定する。除いた理由の鍵は
   `private-note` / `not-created-via-post` / `other-project` / `not-ast-shape` / `owned-by-current-account` / `other-owner`。
2. **`owner` は最後にだけ見る。** 対象は `owner` が無い・空白・`system`（大小を区別）。`owner` を遡及して付けない（口は書き込まない）。
3. **口は `GET /documents/ast-stale-copies`（`AdminOnly`・読み取り専用）。** 応答の `scanned` は対象と除いた件数の合計に等しい。
   報告書は作成の時刻の期間、記事は作成の時刻と `publishedAt` の期間を返す（ADR-0122 決定 2）。期間は件数 0 なら null。
4. **入れ直しの後の確認のため、現在のアカウントが所有する報告書の写しの件数と、同じ `kind`・`periodKey` が 2 件以上ある組を返す。**
5. 手順（列挙 → 件数の確認 → SC-05 の管理者の経路で削除 → AST の報告書の入れ直しを 1 回 → 重複が無いことの確認。切替の後は 0 件の確認）は
   `docs/operations/ast-stale-copies-deletion-runbook.md` に置く。

## 理由

- 作成の経路を最初の版で見ると、`owner=system` の取り込みの文書（DataSourceService）を属性に依らず落とせる。版 1 は作成時に必ず積まれ、後から書き換わらない。
- `created` / `created-with-body` を作る他の口（個人資料の作成・Obsidian の push・同期の衝突の解決）は、すべて `doc_scope=private-note` で作るので個人資料の除外で落ちる（作業仕様書 事実 2）。
- 報告書の条件を AST の入れ直しの写しの判定（AST/IADR-0436 決定 2）と同じにすると、「消した写しは入れ直しで作り直される」と「入れ直しが見つける写しは列挙も見つける」が同じ集合で成り立つ。
- 理由を 0 件も並べると、切替の後に「0 件」と「数えていない」を読み分けられる。

## 結果

- 良い影響: 段 2 の前提（件数・期間）が数えられる。切替の後の 0 件の確認も同じ口で行える（ADR-0122 決定 3・4）。
- 悪い影響 / トレードオフ:
  - 🔴 **表題を変えた `project` なしの報告書は見分けられず、`not-ast-shape` に数えられる**（AST の入れ直しと同じ限界。表題を変えるのは基盤の管理者の操作だけ）。
  - 🔴 **版を持たない文書は `not-created-via-post` に数えられ、対象にならない**（消す側へ倒さない）。版の履歴が作成時から積まれている限り該当は無い見込みで、件数は応答で見える。
  - 人が SC-05 で作った文書が偶然 AST の報告書・記事の形（属性の組）を持ち、`owner` が無いか `system` なら対象に入る。Runbook は消す前に対象の各件（表題・属性）を確かめさせる。
  - 口は台帳の全件を 1 回読む（数千件規模の想定。本文は読まない）。
- フォローアップ: なし（`PutBody` の所有者の判定は #1679、稼働中の `owner` の分布の実測は Runbook の列挙の結果が兼ねる）。

## 関連

- 作業仕様書: [20260928_issue-1667_ast-stale-copies-enumeration](../specs/20260928_issue-1667_ast-stale-copies-enumeration.md)
- 実装: `src/knowledge/backend/Services/DocumentService/Features/Documents/AstStaleCopies/`
- 試験: `.../Tests/Features/Documents/AstStaleCopies/AstStaleCopyRulesTests.cs`・`AstStaleCopiesEndpointTests.cs`
- Supersedes: なし
- Superseded by: なし
