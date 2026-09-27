---
title: 作業仕様書 — ADR-0121 系の監査で残った非ブロッキング指摘を片付ける（試験の穴・文書の追随・計画 ADR レンジの引き直し。#1676）
type: spec
status: in-progress
related_ids: [NFR, FR-05, FR-09, FR-17, FR-10, FR-19, SC-09, UC-05, ADR-0121, ADR-0122, ADR-0120, ADR-0119, ADR-0036, ADR-0034, IADR-0425, IADR-0480, IADR-0481, IADR-0482, IADR-0483]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 1・2・4・5
  - planning:projects/microservices-platform/07_adr/ADR-0122_ast-stale-copies-deletion-scope-and-switchover-order.md（レンジの引き直しのみ。引く実装は本件に無い）
  - planning:projects/microservices-platform/07_adr/ADR-0120_singleton-clusters-excluded-from-summary.md 決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-03・D-07
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md 決定 9（機械の主体は個人資料を読まない）
related_specs:
  - 20260927_issue-1665_owner-read-policy-guard-and-content-abac-gate
  - 20260927_issue-1666_sc09-dynamic-binding-conditions
  - 20260928_issue-1615_content-abac-document-reads
  - 20260927_issue-1663_singleton-cluster-summary-exclusion
  - 20260927_1612_plan-adr-range-0119
issue: "#1676"
---

# 作業仕様書 — ADR-0121 系の監査の後始末（#1676）

> 本仕様書は実装着手前に作成する。受け入れ基準は issue #1676 のチェックリスト全項目である。
> 背景の PR は #1669（#1663）・#1673（#1665）・#1674（#1666）・#1675（#1615）。いずれも監査は GO で、ここに挙げるのは非ブロッキングの残りである。

## 射程と、触らないもの

- **新しい IADR は起こさない。** 判断は既存 IADR（0425・0480・0482）への日付つき追記で足りる。
- **並行作業（#1611 段 2。DocumentService の MCP 実行口）と交差させない。** `Features/McpTools/`・FR-16 のテスト仕様・IADR-0479 は触らない。
  `DocumentReadAccess` の本体は変えない（試験を足すだけ）。
- 計画リポジトリは読み取り専用（`git archive origin/main` を scratch へ展開して実測する）。

## 項目ごとの設計

### A. 試験の穴（生き残った変異を赤にする）

| # | 起点 | 足す試験 | 赤にする変異 |
| --- | --- | --- | --- |
| A1 | #1674 | `AbacValidationTests.ValidatePolicy_BindingMixedWithLiteral_Error` にリテラルが先の混在 `("shared_with", "bob", "${current_groups}")` を足す（引数を「先・後・リテラル」に分ける） | 混在の検査を先頭の値だけで判定する（`bindings.Count > 0` → `LooksLikeBinding(list[0])`） |
| A2 | #1675 | T-54 に「他人の・共有なしの個人資料は、閉じた枝で認可サービスへの問い合わせが 0 回」を足す | 閉じた枝の「共有が 0 件なら問わずに偽」を外す |
| A3 | #1675 | 利用者文脈の無い gRPC 呼び出し（`GrpcService.PrincipalOf(null, 呼び出し元)`。主体は `service-account-bff`）が、その名前を所有者・共有先に持つ個人資料を**門が開いた枝**で読めない。陽性対照は同じ主体が所有する組織文書を読めること | 開いた枝の「機械は個人資料を読まない」を外す ／ `CallingService` を人として作る |
| A4 | #1673 | seed の `deploy/local/abac-seed/policies.json` と運用仕様書 §所有者の読み取りのポリシーの投入 の JSON 本文を**ファイルから読んで** `OwnerReadPolicyShape` に通し、「在る」（seed は 1 件ちょうど）と判定されることを固定する | seed・運用仕様書の本文の形を崩す（値にリテラルを足す等） |
| A5 | #1673 | 門のラッチを並行評価で試験する（2 つの評価を閉じた状態で走らせ、先に「在る」が返って開いた後に「無い」が返る） | ロック内の再確認（`if (_state == Open) return Open;`）を外す |

- A2 は `DocumentReadContentAbacTests` の既存の T-54 に追記する（新しい番号は取らない）。
- A3 は `DocumentReadAccess` の本体を変えない。`GrpcService.PrincipalOf` は internal で試験から呼べる。

### B. 挙動と文書

- **B1（#1674）利用者スコープに `owner`・`shared_with` が登録済みの環境の更新が 400 になる**: **第一案を採る —— 更新の口では既存のキーを予約語の検査から外す。登録の拒否は維持する。**
  - 実装: `AbacValidation.ValidateAttributeDefinition` に `keyAlreadyStored`（既定 false）を足し、真なら予約語の検査だけを飛ばす。
    `UpdateAttributeEndpoint` は Key / Scope が不変なので真を渡す。登録（`CreateAttributeEndpoint`）は渡さない。
  - 理由: 予約語の検査は「新たに作らせない」ための検査であり、既存の属性のラベル・許可値の変更を止めても束縛の取り違えは減らない
    （キーは変えられないので、更新で束縛と同名の利用者属性が新たに生まれることは無い）。運用仕様書に「更新できない」と書く案は、
    管理者に削除と作り直しを強いる（参照中の属性は削除できない＝409）ので採らない。
  - 試験: 純関数の否定（登録は従来どおりエラー）・陽性（`keyAlreadyStored: true` はエラー無し）、結合（DB に直接置いた利用者スコープの `owner` を
    PUT でラベル変更 → 200、同じキーの POST → 400）。変異「更新でも検査する」（引数を無視）は結合・単体とも赤になる。
  - 記録: IADR-0482 決定 7 への日付つき追記。テスト仕様 SC-09 T-74・FR-09 の単体表 29。
- **B2（#1674）IADR-0482 の表題と索引の行**: `.claude/rules/` に「確定済み IADR の `title:` と H1 を書き換えてよい」とする規定は無い
  （`traceability.repo.md` が本文の書き換え禁止の対象外と明記するのは frontmatter の**状態欄**だけ。`.ai-context/README.md` は本文プロズ不変を定め、
  許す変換は移設・パス修正・frontmatter の整形に限る）。したがって **`title:` と H1 は変えず、本文冒頭へ日付つき追記で表題の読み替えを置く**
  （決定 9 の action を含む組 —— `write` × `owner` —— と、決定 10 の束縛の位置の値は束縛だけ、の 2 点）。**索引の行は更新する**
  （索引は生きた一覧。タイトルセルのラチェット〔200 字以内・本体 `title:` と 12 字以上共有・追記ブロックを含めない〕を守る）。
- **B3（#1673）警報に至るまでの遅れの目安**: 運用仕様書 §消えたときの検知と通知 の `OwnerReadPolicyCheckSeriesAbsent` に、
  「数えられない」（検査の失敗が続く）が警報に至るまでの目安を書く —— 検査の周期（最大 1 分）＋ remote write の系列が lookback（約 5 分）で消えるまで ＋ `for: 5m` ＝ **最大およそ 11 分**。
- **B4（#1673）IADR-0480 の追記の見出しの日付と `updated:` の食い違い**: 追記の見出しは `［2026-09-27 追記 / #1665］`、`updated:` は 2026-09-28。
  #1673 が IADR-0480 に加えた変更はこの追記と `updated:` だけである（`git show 653878c0 -- IADR-0480` で実測）。追記の本文（見出しを含む）は凍結記録なので変えず、
  **`updated:` を追記の日付 2026-09-27 に揃える**（frontmatter の整形。本件は IADR-0480 の本文に何も足さない）。
- **B5（#1663）`GraphService/Program.cs` の注記**: 「これが `unsummarized-clusters` の分母」を「所属 2 件以上のクラスタが分母」に追随させる。
- **B6（#1663）旧 3 引数の `UnsummarizedClusterRule.Evaluate`**: `git grep -n "UnsummarizedClusterRule.Evaluate(" -- src` の実測で、本番の呼び出しは
  `ClusterSummaryJob`・`KnowledgeHealthCollector` の 2 か所とも 4 引数版、3 引数版は試験だけ（`UnsummarizedClusterRuleTests` の 11 か所）。
  **private 化を採る（名前は `EvaluateConditions` へ改める）。** 削除しない理由: 3 条件の判定は本体として残る（4 引数版がそれを呼ぶ）。
  public のままにしない理由: 呼び出し側が 3 引数版を選べると単独クラスタの除外を落とせる（#1663 が塞いだ割れ方の再発経路）。
  試験は 4 引数版に `MinMembersToSummarize` を渡す形へ書き換える（3 条件の固定はそのまま）。記録は IADR-0425 への日付つき追記。

### C. 計画 ADR レンジ

- 実測（`/home/user/project-planning`・shallow のため `git archive origin/main` を scratch へ展開。出典 `17518cc`）:
  `node tools/doc-checks/gen-plan-ranges.js --check` → MSP は FR [1, 22]・UC [1, 11]・SC [1, 22]・**ADR [1, 122]**（欠番なし）・NFR 実物 [1, 28]。
- 前回の出典 `3c7949f` からの `07_adr/` の差分で `status:` 行の変化は追加ファイルの `+status: Accepted` 1 件（ADR-0122）だけ。
- `.claude/rules/traceability.repo.md` の ADR レンジを `ADR-0001..0121` → `ADR-0001..0122` にする（FR/UC/SC/NFR は動かさない）。
  別紙 `docs/how-to/plan-id-range-history-annex.md` に 1 回分を追記し、trace ブロックへ ADR-0122 と本仕様書・#1676 を足す。

## 母集合（規則 9。誤りの側の文字列で全文書を走査してから挙げる）

| 走査した文字列 | 当たり | 扱い |
| --- | --- | --- |
| `ADR-0001..0121` | `.claude/rules/traceability.repo.md` の 1 か所 | 直す。別紙は過去の記録の見出し（`0001..0119` → `0001..0121`）であり直さない |
| `unsummarized-clusters` ＋ `分母` | `GraphService/Program.cs:274` の 1 か所 | 直す。`LeidenCommunityDetectorTests`・`ClusterDetectionTests` の「分母」は未要約クラスタ数の分母ではなく検出の母集合の話であり対象外 |
| `UnsummarizedClusterRule.Evaluate(` | 本番 2（4 引数）・試験 11（3 引数）＋ 4（4 引数） | 3 引数の 11 か所を書き換える。IADR-0425・作業仕様書 #1663 の本文の `Evaluate(memberCount, …)` は凍結記録で、4 引数版を指すので正しいまま |
| `利用者属性に使えません`・利用者スコープの `owner` の拒否 | `AbacValidation.cs`・試験・SC-09 T-71・FR-09 表 24・`abacVocabulary.ts` の注記 | 登録の拒否は維持するので、T-71・表 24 は正しいまま。更新の扱いを T-74・表 29 に足す |
| `2 つの位置` / IADR-0482 の表題 | IADR-0482 の `title:`・H1、索引の行、`docs/screens/SC-09_admin-abac-settings.md:195` | 索引の行を直し、IADR-0482 に追記。画面仕様の本文は同節で action の次元（閲覧だけ・書き込みの所有者）と束縛だけの値を既に書いており、誤りではない |
| `鳴るまで`・`lookback`（運用仕様書） | 運用仕様書の `OwnerReadPolicyMissing` の「最大およそ 6 分」の 1 か所。`OwnerReadPolicyCheckSeriesAbsent` には目安が無い | 後者に目安を足す。前者（ポリシーが消えた場合。系列は 0 を出し続けるので lookback は掛からない）は正しいまま |
| `［2026-09-27 追記 / #1665］`（IADR-0480） | 1 か所 | B4 のとおり `updated:` を揃える |

**規則 10（この変更で新たに誤りになる自分の記述）**: 3 引数版を private にすると、IADR-0425 追記（2026-09-27）の「判定の入口を `Evaluate(memberCount, …)` にし」は
正しいままだが、`UnsummarizedClusterRule` の注記「呼び出し側は上の `Evaluate(memberCount, …)` を使う」は private 化で意味が変わるので同時に直す。
`ValidateAttributeDefinition` の引数を足すと、`UpdateAttributeEndpoint` の注記「Key / Scope は不変。既存値を用いて一意・整合を再検証する」に予約語の検査を外す理由を足す。

## 受け入れ基準

- A1〜A5 と B1 の変異を、それぞれ試験のコミット後に当てて赤を確かめ、`git show HEAD:<path> > <path>` で戻す（変異表を PR 本文に載せる）。
- 文書が実装と一致する（B2〜B6）。
- AuthorizationService.Tests・DocumentService.Tests・GraphService.Tests が全件緑。両ユニットの build、`dotnet format --verify-no-changes`、
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、check-trace-blocks / check-test-spec-coverage / check-test-traceability / check-cross-repo-refs /
  check-plan-id-qualification / gen-knowledge-graph --check / check-reading-budget / check-commit-messages が緑。
- テスト ID は各テスト仕様の develop の最大の次: FR-05 は T-67〜T-69、SC-09 は T-74。
