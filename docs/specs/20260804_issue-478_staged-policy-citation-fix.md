---
title: 段階ポリシーの典拠「planning#146 / planning#149 / planning#160」の三つ組を develop 全域で是正する
type: spec
status: done
related_ids: [NFR, IADR-0115, IADR-0117, IADR-0118]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ./20260802_impl-handoff-kit-sync.md
  - ./20260803_issue-453_regression-test-foundation.md
  - ./20260803_issue-474_backend-floor-iadr-and-0116-followup.md
  - ./20260803_issue-469_ai-review-execution-permissions.md
  - ./20260803_issue-470_doc-links-code-extensions.md
  - "../adr/IADR-0118_backend-coverage-floor.md"
---

# 仕様書: 段階ポリシーの典拠「planning#146 / planning#149 / planning#160」の三つ組を develop 全域で是正する

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — 文書の典拠が誤ったままだと、後続が誤った先行事例を辿る）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR:
  [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キット由来ファイルの分類。
  どのファイルを編集してよいかの判断根拠）／
  [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)（ユニット外参照の 2 → 3 プロジェクト改定。
  追加回収 2-1 の根拠）／
  [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md)（決定 6 の是正後文言＝本作業の**見本**。PR #476 で develop 反映済み）
- 一次情報: [20260802_impl-handoff-kit-sync.md](./20260802_impl-handoff-kit-sync.md)
  （planning#145〜#162 の対応表。各 issue の役割の正）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
  「列挙形でも各番号を修飾する」（誤 `planning#146 / #149 / #160` / 正 `planning#146 / planning#149 / planning#160`）
- 本リポジトリの起点: #478

## 目的・背景

develop 上の複数ファイルが、「**成果物は正しいのに赤**を常態化させない段階ポリシー」の典拠として
`planning#146 / #149 / #160` という三つ組を挙げている。この三つ組には 2 つの誤りがある。

1. **planning#149 は無関係**。一次情報の対応表では planning#149 は「サブエージェント禁止の置き場所」であり、
   段階ポリシーとは何の関係もない。
2. **段階ポリシー導入の当事者 planning#161 / planning#162 が欠落**している。段階ポリシーへ改めた決定そのものは
   planning#162（「1 件でも失敗」の常態化が検査の目的を壊す）であり、その直前の実測が planning#161 である。

加えて、列挙形の `#149` / `#160` が**無修飾**であり、`.claude/rules/traceability.md` の
「列挙形でも各番号を修飾する」に違反する（GitHub 上で本リポジトリの issue #149 / #160 へ誤リンクする）。

同型の是正は [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) 決定 6 と `docs/adr/README.md` 索引で
PR #476 が済ませており、本作業は**残りの参照箇所を同じ文言へ揃える**ものである。

あわせて、orphan コミットや issue クローズで宙に浮いた作業仕様書の `status` 2 件と、
IADR-0117 に追随できていない `scripts/README.md` の記述 1 件を回収する（issue #478 本文・コメントで確定済み）。

### 一次情報として確認した事実（各 planning issue の役割）

[20260802_impl-handoff-kit-sync.md](./20260802_impl-handoff-kit-sync.md) の「取り込む是正 9 点」を正とする。

| issue | 役割 | 段階ポリシーとの関係 |
| --- | --- | --- |
| planning#146 | 成果物は正しいのに赤（読み取り系 git の拒否が差分と無関係に毎回出る） | **前段の失敗モード** |
| planning#149 | サブエージェント禁止の置き場所（`prompt:` を持たない実装用は `--append-system-prompt` に置くしかない） | **無関係**（三つ組から除去する） |
| planning#160 | 拒否報告が原因を隠す（複合コマンドのラベル付けが先頭セグメント固定で、許可済みの `git diff` / `git show` を「拒否された」と報告） | **前段の失敗モード** |
| planning#161 | ラベルが読めても拒否は残る（是正後もなお 4 件の拒否を実測） | **段階ポリシーの導入** |
| planning#162 | 「1 件でも失敗」の常態化が拒否の赤を無視する学習を生み、検査の目的を壊す → 許容件数とターン数比による段階判定へ改めた | **段階ポリシーの導入** |

**統一形**（[IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) 決定 6 の是正後文言に揃える）:

> planning#146・planning#160（前段の失敗モード）／planning#161・planning#162（段階ポリシーの導入）

## 対象範囲

### grep による全量洗い出し（実測 / `origin/develop` = `3d0078c`）

issue #478 は 5 ファイル（6 箇所）を名指しするが、#471〜#473 のマージによる増減を確認するため、
`planning#146` を含む行と `#149` を含む行の**両方向**から走査した（`--exclude-dir=.git` / `node_modules`）。

```
grep -rn 'planning#146' .
grep -rn '#149' .
grep -rn '#160' .
```

洗い出し結果を「段階ポリシーの典拠として誤った三つ組を挙げている箇所」に絞ると次のとおりで、
**issue 記載の 5 ファイル 6 箇所と完全に一致した（増減なし）**。

| # | ファイル | 行 | 形 | 扱い |
| --- | --- | --- | --- | --- |
| 1 | [`src/coverage-floor.json`](../../src/coverage-floor.json) | 12（`$comment`） | `planning#146・#149・#160` | 是正 |
| 2 | [`scripts/check-backend-libraries.js`](../../scripts/check-backend-libraries.js) | 17（ヘッダコメント） | `planning#146 / #149 / #160` | 是正 |
| 3 | [`docs/tech/tech-requirements.md`](../tech/tech-requirements.md) | 163 | `planning#146 / #149 / #160` | 是正 |
| 4 | [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | 121 | `planning#146 / #149 / #160` | 是正 |
| 5 | [20260803_issue-453](./20260803_issue-453_regression-test-foundation.md) | 26 | `planning#146 / #149 / #160` | 是正 |
| 6 | 同上 | 174 | `planning#146 / #149 / #160` | 是正 |

### 洗い出しで見つかった「是正しない」箇所と理由（据え置き判断）

| ファイル / 行 | 内容 | 据え置きの理由 |
| --- | --- | --- |
| [`scripts/check-permission-denials.js`](../../scripts/check-permission-denials.js) 30 | `issue #146 / #149 / #160 が繰り返し…` | **キット由来の分類 A（バイト一致）**。IADR-0115 決定 1 により本リポジトリで編集するとデルタが生じる。さらに**キットの名前空間では裸の `#146` が正しい**（planning リポジトリ自身の issue）。三つ組の内容誤りは**上流の誤り**であり、是正するなら `/plan-feedback` による環流であって本リポジトリのローカル編集ではない |
| [`.github/workflows/claude-coding.yml`](../../.github/workflows/claude-coding.yml) 150 / 202、[`scripts/check-ai-workflow-config.js`](../../scripts/check-ai-workflow-config.js) 99 / 389、[`scripts/scripts.test.js`](../../scripts/scripts.test.js) 189 | 単独の `issue #149` / `issue #160` | 段階ポリシーの典拠ではなく、**それぞれ正しい単独参照**（#149＝サブエージェント禁止の置き場所、#160＝報告ラベル）。誤りが無い。かつキット由来 |
| [20260802_impl-handoff-kit-sync](./20260802_impl-handoff-kit-sync.md) 24、[20260803_issue-460](./20260803_issue-460_ai-review-permission-denials.md) 25、`feedback/20260803_ai-workflow-grep-sort-and-submodule-git-c.md` 54 ほか | `planning#145 / #146 / #148 / …` 形の**上流の起点**列挙 | 段階ポリシーの典拠ではなく**単なる起点の列挙**であり、内容は正しい。無修飾番号は規約違反だが**本 issue のスコープ外**（過剰修正を避ける。是正するなら別 issue で全リポ横断に行うべき性質のもの） |
| [`scripts/check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js) 11 / 45 / 96 | 「`Shared/` の **2 プロジェクト**のみ許可」 | IADR-0117 の 2 → 3 改定に未追随だが、**issue #478 のコメントが名指ししたのは `scripts/README.md` の記述のみ**。検査ロジックは `Shared/` 配下をディレクトリで判定しており動作は 3 プロジェクトでも正しい。スコープ外として記録に留める（別 issue 候補） |
| [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) 69 | `誤: planning#146 / #149 / #160。正: …` | **規約の説明文中の「誤例」そのもの**。是正すると規約の例示が壊れる |
| [`docs/adr/README.md`](../adr/README.md) 144、[IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) 171、[20260803_issue-474](./20260803_issue-474_backend-floor-iadr-and-0116-followup.md) | 是正後の統一形 | PR #476 で**是正済み**。本作業の見本 |

### 過去仕様書（`docs/specs/`）を是正する / 据え置くの判断

**是正する。** 一次情報の慣行に合わせた結論である。

- PR #477（同型の是正）は 2026-07-08 の過去仕様書
  [20260708_ci_frontend-test-coverage.md](./20260708_ci_frontend-test-coverage.md) の破損リンクを
  **実際に書き換えた**（当時のパスは注記として本文に残し、リンク先だけ現在位置へ差し替える形）。
  「歴史記録だから触らない」という慣行は本リポジトリに存在しない。
- ただし PR #477 の作法は「**当時の事実は消さず、誤りだけを直す**」である。本件は planning#149 が
  当時から段階ポリシーと無関係であり（当時の事実ですらない）、planning#161 / planning#162 も当時すでに存在した
  （[20260802_impl-handoff-kit-sync.md](./20260802_impl-handoff-kit-sync.md) の 8 / 9 番目）。よって
  **注記を足さず本文を直接是正すれば足りる**（「当時はこう書いていた」を残す価値のある差分ではない）。
- issue #478 が [20260803_issue-453](./20260803_issue-453_regression-test-foundation.md) の 2 箇所を
  明示的に対象としていることとも一致する。

### 追加回収（issue #478 本文・コメントで確定済み）

| # | 対象 | 内容 |
| --- | --- | --- |
| 2-0 | [20260803_issue-470](./20260803_issue-470_doc-links-code-extensions.md) | `status: in-progress` → `done`（orphan コミット `6535e77` の回収。PR #477 でマージ済み） |
| 2-1 | [`scripts/README.md`](../../scripts/README.md) 13 行目 | `check-unit-dependencies.js` の説明「`platform/backend/Shared/` の **2 プロジェクト**のみ許可」→ **3 プロジェクト**（[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)） |
| 2-2 | [20260803_issue-469](./20260803_issue-469_ai-review-execution-permissions.md) | `status: in-progress` → `done`（run `30862005177` で拒否 0 件を確認し #469 はクローズ済み）。完了根拠を末尾に 1〜2 行追記 |

### 含まないもの

- コード動作の変更（本作業はコメント・文書・`$comment`・frontmatter のみ）。
- 段階ポリシーそのものの設計変更（許容値・判定式は触らない）。
- 上記「据え置き」表の各行。

## IADR-0115 の分類（編集可否の確認）

| ファイル | 分類 | 根拠 |
| --- | --- | --- |
| [`src/coverage-floor.json`](../../src/coverage-floor.json) | **C（リポ固有）** | #453 で本リポジトリが新規作成。キットに対応物なし |
| [`scripts/check-backend-libraries.js`](../../scripts/check-backend-libraries.js) | **C（リポ固有）** | ADR-0030 / #455 由来。固有デルタ 3「本リポにしか存在しないスクリプト」 |
| `docs/tech/` `docs/tests/` `docs/specs/` | **C（リポ固有）** | 雛形から書き起こした実体 |
| [`scripts/README.md`](../../scripts/README.md) | **B（キット＋固有デルタ）** | 表にリポ固有スクリプト（`check-unit-dependencies.js` / `check-image-mapping.js` 等）の行が既存デルタとして並ぶ。本作業の 2-1 は**既存デルタ行の中の数値 1 箇所の更新**であり、新しいデルタを増やさない |
| [`scripts/check-permission-denials.js`](../../scripts/check-permission-denials.js) | **A（バイト一致）** | 編集しない（上記「据え置き」表） |

## 受け入れ基準

- [x] `grep -rn 'planning#146 / #149'` および `planning#146・#149` の残存が、**典拠として使っている箇所では 0 件**
      （残るのは本仕様書の記述・`.claude/rules/traceability.md` の誤例・キット由来の分類 A ファイルのみ）
- [x] 是正した 6 箇所すべてが統一形「planning#146・planning#160（前段の失敗モード）／planning#161・planning#162（段階ポリシーの導入）」になっている
- [x] 各番号が `planning#NNN` と修飾されている（列挙形でも無修飾の `#NNN` を残さない）
- [x] `src/coverage-floor.json` が JSON として valid で、`backend.line` / `backend.branch` の値が変わっていない
- [x] `node scripts/check-doc-links.js` が成功する
- [x] `node scripts/scripts.test.js`（`REQUIRE_REPO_TESTS=1` 含む）が成功する
- [x] `node scripts/check-backend-libraries.js --self-test` が成功する（ヘッダコメント変更の副作用なし）
- [x] `node scripts/check-commit-messages.js --base origin/develop` が成功する
- [x] 追加回収 3 件が反映されている

## 検証結果（実測）

| コマンド | 結果 |
| --- | --- |
| `grep -rn 'planning#146 / #149\|planning#146・#149\|#146 / #149' .` | 典拠としての残存 **0 件**（ヒットは本仕様書の記述・`.claude/rules/traceability.md` の誤例・`check-permission-denials.js`（分類 A・据え置き）のみ） |
| `node -e "JSON.parse(...coverage-floor.json)"` | valid・`line 34` / `branch 17` 不変 |
| `node scripts/check-doc-links.js` | exit 0 |
| `node scripts/scripts.test.js` | exit 0 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | exit 0 |
| `node scripts/check-backend-libraries.js --self-test` | exit 0 |
| `node scripts/check-commit-messages.js --base origin/develop` | exit 0 |

## リスクと影響

- 影響は文書・コメントのみで、CI ゲートの判定・カバレッジ床の値・検査ロジックは一切変わらない。
- `src/coverage-floor.json` は `$comment` 配列の要素分割を伴うため、JSON parse の確認を受け入れ基準に含めた。
