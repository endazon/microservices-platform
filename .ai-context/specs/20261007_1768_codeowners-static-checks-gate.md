---
title: 作業仕様書 — develop の門の穴 2 件（CODEOWNERS 不在・static-checks が門の外）を塞ぐ（#1768）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0007
  - IADR-0505
  - IADR-0182
  - IADR-0232
author: claude
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0007（CI/CD）
issue: "#1768"
---

# 作業仕様書 — develop の門の穴 2 件を塞ぐ（#1768）

## 目的と射程

第 4 回全体監査（2026-10-07）の指摘 A-2 の 2 件を塞ぐ。

1. `.github/CODEOWNERS` が無いのに、ルールセット `develop-rule` の `require_code_owner_review: true` と
   `docs/DEFINITION_OF_DONE.md:80`「必要なレビュー（CODEOWNERS）の承認を得た」が配置を前提にしている。
   `docs/ai-workflow.md:128` は classic 保護の `required_pull_request_reviews: null` を記録しており、文書同士も食い違う
   （issue の訂正コメントによる受け入れ基準 1 の読み替え）。
2. `ci.yml` の `static-checks`（依存ゼロの node 検査器の集約）が、必須 check 表にも `build-and-test` の `needs` にも無い。赤くてもマージできる。

**射程外**（受け入れ基準 3）: 管理者ロールが `bypass_actors` で `exempt` であることの是非。本作業は実態をそのまま書くだけで、是非を決めない（AST#501 と同型の利用者判断）。GitHub の設定（ルールセット・classic 保護）は本作業の権限で変えられない。

## 受け入れ基準

1. `.github/CODEOWNERS` を AST と同じ形（`* @endazon`）で置く。DoD:80・`ai-workflow.md:128` 周辺・`:138` が、実物（ファイル）と実測したルールセットに一致し、互いに食い違わない。
2. `static-checks` を門へ入れる。入れ方と所要時間の実測・律速への影響を `ai-workflow.md` の必須 check 表と IADR に書き、`check-workflow-job-refs.js` が表と `ci.yml` の一致を検査する。
3. 管理者 exempt の是非を決めない（書くのは実態だけ）。

## 実測（基点 `origin/develop` `c1f1bb34`。`git rev-parse --is-shallow-repository` = `false`）

### CODEOWNERS とルールセット

```console
$ gh api repos/endazon/microservices-platform/rulesets/18168237   # 抜粋（2026-10-07）
"name":"develop-rule","enforcement":"active","conditions":{"ref_name":{"include":["~DEFAULT_BRANCH"]}}
{"type":"pull_request","parameters":{"required_approving_review_count":1,"dismiss_stale_reviews_on_push":true,
 "require_code_owner_review":true,"require_last_push_approval":true,"required_review_thread_resolution":true, ...}}
{"type":"required_status_checks","parameters":{"strict_required_status_checks_policy":false,
 "required_status_checks":[{"context":"image-build"}]}}
"bypass_actors":[{"actor_id":5,"actor_type":"RepositoryRole","bypass_mode":"exempt"},
 {"actor_type":"Integration","bypass_mode":"always"} × 4]
"current_user_can_bypass":"exempt"
$ gh api repos/endazon/microservices-platform/branches/develop/protection
HTTP 403（Resource not accessible by integration）  … classic 保護の 8 件・enforce_admins は本作業では再測定できない
```

- `.github/CODEOWNERS` は無い。`.github/CODEOWNERS.example` は雛形のまま（全行コメント）。
- AST は `.github/CODEOWNERS`（`*       @endazon`）を置き、`.example` は持たない（AST#501 / AST#1017）。

### static-checks の所要時間と律速への影響

`ci.yml` の直近の run 16 本（develop への push 10 本・PR 6 本。いずれも成功）。時刻は run の最初のジョブ作成からの秒。

| run | static-checks 所要 | static-checks 終了 | `lint` 開始 | `build-and-test` 開始 | 余裕（b&t 開始 − static 終了） |
| --- | --- | --- | --- | --- | --- |
| 37471278779 | 79 | 82 | 142 | 171 | 89 |
| 37465929611 | 75 | 77 | 143 | 201 | 124 |
| 37392174425 | 79 | 81 | 101 | 109 | 28 |
| 37357298590 | 70 | 72 | 147 | 132 | 60 |
| 37350464737 | 76 | 78 | 116 | 114 | 36 |
| 37348592267 | 65 | 67 | 154 | 221 | 154 |
| 37345177230 | 79 | 81 | 172 | 196 | 115 |
| 37343457805 | 67 | 69 | 112 | 129 | 60 |
| 37328858627 | 72 | 75 | 148 | 202 | 127 |
| 37314923734 | 78 | 80 | 107 | 121 | 41 |
| 37469857359（PR） | 77 | 157 | 203 | 376 | 219 |
| 37464561898（PR） | 75 | 78 | 142 | 124 | 46 |
| 37462606181（PR） | 75 | 76 | 139 | 243 | 167 |
| 37457592661（PR） | 73 | 75 | 211 | 220 | 145 |
| 37391482390（PR） | 80 | 83 | 111 | 133 | 50 |
| 37389461694（PR） | 71 | 73 | 89 | 126 | 53 |

- 所要 65〜80 秒。**16 本すべてで `build-and-test` の開始より前に終わっている**（余裕の最小 28 秒・中央値 75 秒前後）。
  `lint` の開始に対する余裕は最小 16 秒（37389461694）で、`build-and-test` のほうが余裕が大きい。
- したがって `build-and-test` の `needs` へ足しても、16 本のいずれでも `build-and-test` の終了時刻は変わらなかった計算になる。

## 判断（IADR-0505）

- **CODEOWNERS**: `.example` を `git mv` で `.github/CODEOWNERS` にし、AST と同じ `* @endazon` を置く（AI_SETUP.md 共通セットアップ 4 の手順どおり）。文書は実態を書く —— ルールセットは承認 1 件・コードオーナー承認を要求する。管理者ロールは `exempt` で素通りする。classic 保護は承認を要求しない（`null`）。
- **static-checks**: 選択肢 (a) 単独の必須 check 名にする（所有者の設定変更が要る）、(b) `build-and-test` の `needs` に足す、(c) 門の外に置いて理由を書く —— のうち **(b)** を採る。所有者の設定変更が要らず、実測で待ち時間を増やさない。表には取り消し線の行で「単独では必須にしない。`build-and-test` の `needs` で拾う」と書き、`check-workflow-job-refs.js` に面 D（表の「`<集約>` の `needs` で拾う」の主張と `ci.yml` の `needs` の突合）を足す。

## 変更対象（母集合。規則 9: 誤りの側の文字列で走査した）

走査語: `CODEOWNERS` / `required_pull_request_reviews` / `Code Owners` / `static-checks`（`static-checks-units` を除く）を `docs/` `scripts/README.md` `AI_SETUP.md` `AGENTS.md` `CLAUDE.md` `.claude/` に対して。

| ヒット | 扱い |
| --- | --- |
| `AI_SETUP.md:68`（`.example` を `CODEOWNERS` にリネーム） | 追随（実施済みと書く。`.example` が無くなるため） |
| `docs/ai-workflow.md:30`（人間レビュー（CODEOWNERS…）） | 変更不要（フロー図の一般記述） |
| `docs/ai-workflow.md:128`（classic `null`） | 追随（ルールセット側の実測を併記） |
| `docs/ai-workflow.md:138`（推奨の列挙） | 追随（配置済みと書く） |
| `docs/DEFINITION_OF_DONE.md:80` | 追随（実態どおりに書き直す） |
| `docs/**` の `static-checks` ジョブへの言及 6 件 | 変更不要（ジョブの存在の記述で、必須か否かを主張していない） |
| `scripts/README.md` §検査（CI） の `static-checks` 行・`check-workflow-job-refs.js` 行 | 追随（面 D・自己試験件数） |
| `scripts/scripts.repo.test.js`（#1551 / #1686 の `build-and-test` 配線試験・#705 の表の行の試験） | 追随（`static-checks` の結果の判定と表の行を固定） |

除外: `.ai-context/specs/`・`.ai-context/superpowers/`（凍結記録）、`CLAUDE.md` / `.claude/rules/`（必読予算 90.5%。MSP#1770。本件の記述は無い）。

規則 10（本変更で新たに誤りになる自分の記述）: `check-workflow-job-refs.js` の自己試験件数（README の「17 件」）、`scripts-tests` 行の件数は変えない（`scripts.test.js` の件数は本表で引用している数を本変更で更新しない —— 引用値は #936 時点の歴史値として書かれている）。

## 検証

- `static-checks` ジョブの node 検査器を手元で同じ順に実走する。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
- actionlint（在れば）。

## ［2026-10-07 追記 / #1768］監査の指摘への追随

- **`docs/DEFINITION_OF_DONE.md` §レビュー**: 「管理者がマージする PR では機械的には強制されない…その場合この項は人の確認である」は時々起きる例外のように読めた。実測では現状のマージはすべて例外の管理者が承認 0 件で行っている（例: #1717 / #1774）。計画リポの統制記述の規則（統制を定める記述には現在の実現手段を併記し、未配備なら条件付きに書いて暫定手段を並べる）に従い、**現状すべての PR が該当し機械的には強制されていないこと**・**暫定手段（PR チェックリストでの人の確認）**・**効く条件（例外でない主体のマージ、または管理者の例外を外した場合。判断はオーナーに残す）**を書いた。
- **`AI_SETUP.md` 共通セットアップ 4**: 本 PR で削除した `.github/CODEOWNERS.example` のリネームを指示したままだった。「配置済み（`.github/CODEOWNERS`）」と、オーナー変更時は同ファイルの行を直接編集する旨へ書き直した。上の母集合表の `AI_SETUP.md:68` の扱い（「実施済みと書く」）は不足で、手順文自体を書き換えるのが正しかった。
- 再走査: `CODEOWNERS.example` は `.ai-context/` の凍結記録を除き追跡下に残っていない。
