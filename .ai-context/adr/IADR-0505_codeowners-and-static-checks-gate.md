---
title: IADR-0505 `.github/CODEOWNERS` を AST と同じ形（`* @endazon`）で置き、文書はルールセットの実態（承認とコードオーナー承認を要求・管理者ロールは exempt）をそのまま書く。`static-checks` は単独の必須 check にせず、必須 check の集約 `build-and-test` の `needs` で拾う
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0007, IADR-0182, IADR-0232]
author: claude
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0007（CI/CD）
related_specs:
  - ../specs/20261007_1768_codeowners-static-checks-gate.md
---

# IADR-0505: CODEOWNERS を置き、`static-checks` を `build-and-test` の `needs` で門に入れる（#1768）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-07
- 決定者: claude（#1768。第 4 回全体監査 A-2。作業仕様書 20261007_1768）

## 起点・関連

- 起点 issue: #1768（第 4 回全体監査 A-2。記録は planning `draft/cross-project/20261007_poc-operational-overall-audit.md`）
- 関連する計画 ADR: ADR-0007（CI/CD）
- 関連する実装 ADR: [[IADR-0182]]（必須 check はジョブの check 名で指定する）、[[IADR-0232]]（PR の待ち時間を縮める。必須 check 名は集約ジョブで維持する。決定 6 で node 検査を `static-checks` へ束ねた）
- 関連する AST の判断: AST#501 / AST#1017（AST は `.github/CODEOWNERS` を `* @endazon` で置いた）
- 基点コミット: `origin/develop` `c1f1bb34`

## コンテキストと課題

穴は 2 つある。

1. **CODEOWNERS が無い。** ルールセット `develop-rule`（id 18168237）の `pull_request` 規則は `require_code_owner_review: true` だが、`.github/CODEOWNERS` が無いので対象者 0 人で空振りしていた。`docs/DEFINITION_OF_DONE.md` は「必要なレビュー（CODEOWNERS）の承認を得た」を完了条件に置き、`docs/ai-workflow.md` は classic 保護の `required_pull_request_reviews: null` を記録していた。文書同士も、文書と実物も食い違っていた。
2. **`static-checks` が門の外にある。** `ci.yml` の `static-checks`（依存ゼロの node 検査器の集約。trace ブロック・文書リンク・必読予算・ワークフロー名参照・契約スキーマなど）は、必須 check の表にも `build-and-test` の `needs` にも無かった。理由の記録も無い。赤くてもマージできる。

### 実測（2026-10-07）

ルールセット API（`gh api repos/endazon/microservices-platform/rulesets/18168237`）:

| 項目 | 値 |
| --- | --- |
| `pull_request` 規則 | `required_approving_review_count: 1`・`require_code_owner_review: true`・`require_last_push_approval: true`・`required_review_thread_resolution: true`・`dismiss_stale_reviews_on_push: true` |
| `required_status_checks` | `image-build` の 1 件だけ |
| `bypass_actors` | `RepositoryRole` id 5（管理者）が `exempt`、`Integration` 4 件が `always` |
| classic 保護（`branches/develop/protection`） | HTTP 403。文書が記録する 8 件・`enforce_admins: true` は本作業では再測定できない |

`ci.yml` の直近 16 本の run（develop への push 10 本・PR 6 本。いずれも成功）の時刻（run の最初のジョブ作成からの秒）:

| 指標 | 値 |
| --- | --- |
| `static-checks` の所要 | 65〜80 秒 |
| `static-checks` の終了 | 67〜157 秒（157 秒の 1 本は開始が 80 秒遅れた） |
| `build-and-test` の開始 | 109〜376 秒 |
| 余裕（`build-and-test` 開始 − `static-checks` 終了） | 最小 28 秒・16 本すべて正 |
| 余裕（`lint` 開始 − `static-checks` 終了） | 最小 16 秒・16 本すべて正 |

run ごとの値は作業仕様書の表にある。

## 検討した選択肢

### CODEOWNERS

| | A. AST と同じ `* @endazon` を置く（**採用**） | B. 置かず、DoD とルールセットを「配備後に」の条件付きへ書き直す |
| --- | --- | --- |
| ルールセットの `require_code_owner_review` | 対象者が決まり空振りしなくなる | 空振りのまま。ルールセットの変更は所有者の操作が要る |
| 手順書（AI_SETUP.md 共通セットアップ 4） | 手順どおり（`.example` をリネーム） | 未実施のまま残る |
| 管理者がマージする PR | `exempt` で素通り（変わらない） | 同左 |

B は DoD を条件付きにしても、ルールセット側を実態に合わせる操作が別に要る。A はファイル 1 つで、ルールセットの前提を実物と一致させる。AST が同じ形を既に運用している。

### static-checks

| | (a) 単独の必須 check 名にする | (b) `build-and-test` の `needs` に足す（**採用**） | (c) 門の外に置き、理由を表に書く |
| --- | --- | --- | --- |
| 所有者の設定変更 | 要る（classic 保護の必須 check へ追加） | 要らない（`build-and-test` は既に必須） | 要らない |
| 待ち時間 | 増えない（並列） | 実測 16 本すべてで `build-and-test` の開始前に終わっており、増えない | 増えない |
| 赤のときの見え方 | `static-checks` が赤 | `build-and-test` も赤。原因はステップ名で探す | マージできる |
| 退行の検知 | 表の生きた行（`check-workflow-job-refs.js` 面 A） | 表の主張と `needs` の突合（同 面 D。本 IADR で追加） | 無い |

(c) は穴を残す。偽陽性が多いという記録も無い（直近 16 本はすべて成功）。(a) は名前の分かりやすさで勝るが、所有者の設定変更を待つ間は穴が残る。(b) は今すぐ効き、実測では待ち時間を増やさない。集約先は `lint` でもよいが、`lint` は余裕が小さい（最小 16 秒）。`build-and-test` は `check-ci-latency.js` が中央値を監視しており、遅くなれば検知できる。

## 決定

1. `.github/CODEOWNERS.example` を `git mv` で `.github/CODEOWNERS` にし、`*       @endazon` を置く。ヘッダのコメントで「置いただけでは関門にならない（管理者ロールは exempt）」と書く。
2. 文書（`docs/ai-workflow.md` の「必須チェックの有効化」・`docs/DEFINITION_OF_DONE.md` のレビュー項目・`AI_SETUP.md` 共通セットアップ 4）は、ルールセットの実態をそのまま書く。ルールセットは承認 1 件とコードオーナーの承認を要求する。管理者ロールは `exempt` で、管理者がマージする PR には機械的に課されない。classic 保護の `null` と結果は同じである。**`exempt` の是非は決めない**（受け入れ基準 3。AST#501 と同型の利用者判断）。
3. `static-checks` を `build-and-test` の `needs` に足す。判定は `build-and-test` の最後のステップ（`if: ${{ !cancelled() }}`）で行い、`success` 以外を落とす。ビルド結果の判定が赤でもこの判定は走る。カバレッジの報告も止めない。
4. 必須 check 表に取り消し線の行 `~~static-checks~~` を置き、「`build-and-test` の `needs` で拾う」と書く。件数（8 件）は変えない。
5. `scripts/check-workflow-job-refs.js` に面 D を足す。表の行が「`<集約>` の `needs` で拾う」と主張するとき、`<集約>` が表の生きた行で、ワークフローの `<集約>` の `needs:` にそのジョブが在ることを確かめる。`needs:` を読めなければ落とす（fail-closed）。`scripts/scripts.repo.test.js` は `build-and-test` の配線（`needs`・結果の変数・`!= success` の判定・`!cancelled()`）と表の行を固定する。

## 結果・影響

- `static-checks` の赤が `build-and-test` の赤としてマージを止める。classic 保護が文書どおり `build-and-test` を必須にしている限り、所有者の操作は要らない（classic 保護は 403 で再測定できていない。ルールセット側の必須 check は `image-build` だけである）。
- 実測の余裕（最小 28 秒）を `static-checks` の伸びが食い潰すと、`build-and-test` の終了が遅れる。そのときは `check-ci-latency.js` の監視（`build-and-test` の中央値）に現れる。直し方は `static-checks` を割る（node 検査器を 2 ジョブへ）か、(a) へ切り替える。
- CODEOWNERS は、管理者ロールでない主体（`bypass_actors` に無い協力者など）がマージするときに効く。管理者の操作では効かない現況は変わらない。

## 残余リスク

- classic 保護の現況（8 件・`enforce_admins`）は本作業の権限で再測定できない。文書の記録（2026-08-30 配備）を正としている。
- 管理者ロールの `exempt` を外すか、の判断は利用者に残っている。外せば全 PR に人間の承認が要り、流れが止まる（`docs/ai-workflow.md` の `null` の理由と同じ）。
