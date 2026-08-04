---
title: 作業仕様書 — impl-handoff-kit 同期 第 14 ラウンド（planning#176 反映後の暫定デルタ撤去）
type: spec
status: done
related_ids: [NFR, IADR-0115]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ./20260801_impl-handoff-kit-sync.md
  - ./20260803_issue-460_ai-review-permission-denials.md
  - ./20260803_issue-469_ai-review-execution-permissions.md
  - ./20260803_issue-470_doc-links-code-extensions.md
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
---

# 作業仕様書: キット同期 第 14 ラウンド — 暫定デルタの撤去

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — 足場の単一情報源を保ち、乖離を機械検出可能に保つ）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: [`IADR-0115`](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  （impl-handoff-kit を足場の単一情報源とする同期規約。分類 A / B / C と固有デルタ 4 種の定義）
- 関連 issue: #460 / #469 / #470（本リポジトリ側）／ 環流先 planning#163 / planning#167 / planning#168

## 目的・背景

#460 / #469 / #470 の対応で本リポジトリへ入れた**暫定デルタ**（キット反映を待たずに先行適用した差分。
ソースコメントに環流先 issue を明記し、反映後の同期で撤去する運用）を、
[planning#176](https://github.com/endazon/project-planning/pull/176) のマージにより撤去する。

**注記を残したままにしてはならない。** 「キット反映後に撤去する」という注記が残ると、
次の同期担当者が「まだ未反映」と誤読する。

submodule pin を `df8bce5` → `abb6a75` へ進める。

## 対象範囲

| 分類 | ファイル | 扱い |
| --- | --- | --- |
| A | [`scripts/check-doc-links.js`](../../scripts/check-doc-links.js) | キットから機械コピーし `cmp` でバイト一致を確認 |
| A | [`scripts/check-ai-workflow-config.js`](../../scripts/check-ai-workflow-config.js) | 同上（planning#176 が新設した `genericBashDrift` を取り込む） |
| B | [`.claude/settings.json`](../../.claude/settings.json) | キットを土台に固有デルタを再適用 |
| B | [`.github/workflows/claude-coding.yml`](../../.github/workflows/claude-coding.yml) | 同上 |
| B | [`.github/workflows/claude-code-review.yml`](../../.github/workflows/claude-code-review.yml) | 同上 |
| B | [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) | doc-links ジョブの注記のみキット文へ戻す |
| B | [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) | pin と計画 ID レンジを追随（`ADR-0040` 起案・`ADR-0035` は予約のまま） |

## 設計

### 再適用した固有デルタ（IADR-0115 の 4 種のみ）

| 箇所 | 内容 | 種別 |
| --- | --- | --- |
| 両ワークフローの `--allowedTools` ／ `settings.json` | `git -C src/ai-stock-trading[/planning]` × 4 サブコマンド（計 8 エントリ） | 1（リポジトリ構成。submodule が入れ子で 3 つある） |
| 両ワークフローのプロンプト | 上記 submodule を「正の一覧」に明記 | 1 |
| 両ワークフローのプロンプト | `src/<unit>/backend/backend.slnx` 単位の 1 コマンドで実行させる | 1 ＋ 2（ユニット第一構成・.NET） |
| `ci.yml` doc-links ジョブ | planning submodule 未取得の注記と `doc-links-planning.yml` への案内 | 1 ＋ 3（本リポにしかないワークフロー） |
| `check-commit-messages.js` | `PLAN_PROJECT` 既定値 `microservices-platform` | 1（置換点） |

### 撤去した独自記述（4 種のいずれにも当たらない）

IADR-0115 は「固有デルタは 4 種のみ。それ以外の独自記述は同期時に削除する」と定める。
次はいずれも 4 種に当たらないため撤去し、キットの一般化された記述へ戻した。

- **実測 run ID の列挙**（run 30829121373 / 30830151995 / 30832367628 / 30833943957）。
  実測の記録は [`feedback/20260803_ai-review-execution-permissions.md`](../../feedback/20260803_ai-review-execution-permissions.md)
  と各 issue の仕様書に残っており、プロンプトから消えても失われない。キットの一般化文が同じ制約を伝える。
- **キット所有スクリプトの実名**（`scripts/scripts.test.js` / `scripts/check-ai-workflow-config.js`）と
  環境変数（`REQUIRE_REPO_TESTS` / `STRICT_AI_WORKFLOW_CONFIG`）。**いずれもキット側に実在する**ため
  「本リポにしか存在しない成果物・スクリプト」（種別 3）に当たらない。キットは
  「対象スクリプトの末尾を Read してガードの有無を自分で確認すること」という一般形を採っている。
- **`permission_denials_count` はジョブ末尾の Check permission denials ステップが権威**という注記。
  同ステップは**キット側の同ワークフローにも在る**ため固有ではない（環流候補・§未決事項）。
- 言い回しの差（`許可ツール一覧` ↔ `許可リスト（この claude_args が…）` 等）。

### 撤去しなかった `planning#167` 参照

`scripts/check-doc-links.js` と `ci.yml` に残る `planning#167` は、**キット本体が持つ由来コメント**
（なぜ `LINK_EXT` にコード拡張子が入り、なぜ `--self-test` を先に走らせるのか）である。
「未反映・撤去する」という趣旨の注記ではない。**消すとキットとの新たな差分になる**ため保持する。

## 受け入れ基準

- [x] 分類 A の 2 ファイルがキットと `cmp` でバイト一致
- [x] 「暫定デルタ」注記が生き設定（`.github/workflows/` / `.claude/` / `scripts/`）から 0 件
- [x] 両ワークフローのキットとの残差が**再適用した固有デルタのみ**
- [x] planning#176 が新設した `genericBashDrift` を本リポのワークフローへ実走し **ERROR 0**
- [x] submodule pin が `abb6a75`

## テスト方針

いずれも本作業ツリーで実走した結果である。

| 検査 | 結果 |
| --- | --- |
| `cmp` によるバイト一致（分類 A 2 件） | 一致 |
| `check-ai-workflow-config.js --self-test` | 30 件合格 |
| `check-ai-workflow-config.js`（本リポの 2 ワークフローへ実走） | ERROR 0 / warn 0 |
| `check-doc-links.js --self-test` | 34 件合格 |
| `check-doc-links.js`（本走） | 408 件に破損リンク 0 |
| `scripts/scripts.test.js` | 239 件合格 |
| `settings.json` の JSON 妥当性 | allow 69 / deny 35 で妥当 |

C# / TypeScript の変更を含まないため、バックエンド・フロントエンドのビルドとテストは CI に委ねる。

## 計画書との差異

なし。足場の同期であり、計画書の決定に触れない。

## 未決事項

- **環流候補**: 撤去した記述のうち次の 2 つは、キット側にあると全実装リポジトリで有用である。
  次回の `/plan-feedback` で環流するか判断する。
  1. `require.main` ガードの説明に**キット所有スクリプトの実名**を例示する
     （`scripts.test.js` はガード無し・`check-ai-workflow-config.js` はガード有り、という対比が
     そのまま教材になる）。
  2. `permission_denials_count` はジョブ末尾のステップが権威であり、AI が実行ログを取り直して
     再検証する必要は無い旨（キットの両ワークフローにも同ステップが在る）。
- **AST 側は未対応**である。planning#176 が新設した `genericBashDrift` は ai-stock-trading の
  `claude-coding.yml` に `Bash(git show:*)` の欠落（真陽性 1 件）を検出する。AST でキットを同期する
  際は、**同期 PR の中で先に当該エントリを足す**こと（そうしないと `ai-workflow-config` ジョブが
  その PR から赤くなる）。本リポジトリの作業範囲外。
