---
title: 作業仕様書 — check-nul-bytes.js の CI 配線と README 登録（#956 残作業）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0247
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0030（バックエンドアプリケーション層標準）"
issue: "#956"
---

# 作業仕様書: check-nul-bytes.js の CI 配線と README 登録（#956 残作業）

## 起点

- `#956`（生の NUL バイト混入の検査）。検査器本体（`scripts/check-nul-bytes.js`）・自己試験・
  `scripts.repo.test.js` companion（自己試験 / 実データ走査 / 変異試験）・`IADR-0247`・
  先行仕様書 `.ai-context/specs/20260822_issue-956_nul-byte-check.md` は**着手前に実装済み**。

## 残作業の実測（着手前の確認）

`grep -rn "check-nul-bytes" .github/ src/package.json scripts/README.md` が **0 件**。

- 検査器本体・自己試験・companion 経由の実データ走査と変異試験は `node scripts/scripts.test.js`
  （`REQUIRE_REPO_TESTS=1`）経由で CI の `scripts-tests` ジョブに**既に**乗っている
  （`scripts.repo.test.js` 2524〜2604 行 / 5204〜5205 行 / 5416 行）。
- しかし **`ci.yml` の `static-checks` ジョブに専用ステップが無い**（他の検査器
  `check-doc-links.js` / `check-trace-blocks.js` / `check-reading-budget.js` は
  `--self-test` ＋ 本体の 2 ステップを持つのに対し、本検査器は持たない）。
- **`scripts/README.md` のスクリプト一覧表・CI 表・「使い方（ローカル）」のいずれにも未掲載**。

残作業は次の 2 点のみと判断する。

1. `ci.yml` `static-checks` ジョブへ `check-nul-bytes.js --self-test` ＋ 本体の専用ステップを追加する
   （他の同種検査器と同じ形。専用ステップにする理由は `check-test-spec-coverage.js` の
   ci.yml コメントと同じ——「companion 経由と二重に走っても、専用ステップは失敗をジョブ名で見せる」）。
2. `scripts/README.md` へ登録する（スクリプト一覧表・CI 表・「使い方（ローカル）」）。

## 母集合の確認（他に未配線の検査器が無いか）

「CI 未配線の検査器が他にもあるのでは」という母集合の取り違えを避けるため、
`scripts/README.md` に載っている検査器名を総ざらいし、`.github/workflows/*.yml` 全体
（`grep -rn` で `.js` ファイル名を横断）で 1 つずつ言及の有無を確認した。

- `check-nul-bytes.js` 以外はいずれも `ci.yml`（専用ステップ or companion 経由）・
  `frontend.yml` ／独立ワークフロー（`image-mapping.yml` 等）のいずれかで言及されている。
- **未配線として新たに見つかったものは無い。** 本 issue の残作業は `check-nul-bytes.js` 1 本に限る。
- 除外理由: `verify-qdrant-attribute-payload.sh` / `seed-abac-policies.js` /
  `verify-oidc-edge-flow.sh` / `measure-abac-combinations.js` は CI ゲートではなく手動実行の
  検証・投入スクリプトであり（README の「役割」欄に「投入」「実測」とあり、fail/pass のゲートを
  持たない設計）、CI 配線の対象ではない。

## 決定

### 決定 1: `static-checks` ジョブへ、他の依存ゼロ検査器と同じ位置付けの 2 ステップを追加する

挿入位置は「Check reading budget」ステップの直後・pipeline config 検証の直前とする。
理由: 本検査器は `docs/` や `.ai-context/` に限らず追跡下全体を対象にする横断的な走査であり、
特定のスタック（バックエンド/フロントエンド）や他の検査器の出力に依存しない。挿入によって
他 issue が触っている `check-backend-libraries.js` / `check-event-topology.js` の呼び出し箇所
（`static-checks` ジョブの後方）とは離れた位置になり、diff の競合を避けられる。

### 決定 2: companion 経由の実行は残す（重複ではない）

`scripts.repo.test.js` からの呼び出しは削除しない。`check-test-spec-coverage.js` の
ci.yml コメント（「専用ステップにすると失敗がジョブ名で見える」）と同じ理由で、
専用ステップと companion の二重実行は意図的な設計である。

## 受け入れ基準

1. `ci.yml` `static-checks` ジョブで `check-nul-bytes.js --self-test` と本体検査が実行される
2. `scripts/README.md` のスクリプト一覧表・CI 表・「使い方（ローカル）」に登録される
3. 変異試験: 追跡下ファイルへ NUL を 1 バイト仕込むと検査器が実際に fail し、消すと通ることを実測する
4. `node scripts/check-nul-bytes.js` と `node scripts/check-doc-links.js` が通る
5. 作業ツリーに一時ファイル・一時変更を残さない（`git status` がこの作業に無関係な差分だけになる）

## 変異試験（実施計画）

対象は `scripts/check-nul-bytes.js`（追跡済み・作業対象外＝改変しない前提のファイル）。
`git ls-files` の走査対象であることを確認済み。

| 手順 | 内容 | 期待 |
| --- | --- | --- |
| 1 | 対照: 変更前に `node scripts/check-nul-bytes.js` を実行 | EXIT=0 |
| 2 | コメント行の途中へ生の NUL バイトを 1 個挿入（構文を壊さない位置） | — |
| 3 | `node scripts/check-nul-bytes.js` を再実行 | EXIT≠0、ファイル名・行番号を名指し |
| 4 | `git checkout -- scripts/check-nul-bytes.js` で復元 | 復元後 `git diff` が空 |
| 5 | `node scripts/check-nul-bytes.js` を再実行 | EXIT=0（対照と一致） |
| 6 | `git status --porcelain -- scripts/check-nul-bytes.js` | 出力なし（clean） |

結果は本文末尾または報告本文（呼び出し元）に記載する。

## 検証

- `node scripts/check-nul-bytes.js`
- `node scripts/check-nul-bytes.js --self-test`
- `node scripts/check-doc-links.js`
- 上記の変異試験

## 計画書との差異

差異なし。CI 配線と README 登録は `#956` のチェックリスト（変異試験の実施を含む）の範囲内であり、
計画書（`ADR-0030`）や既存の `IADR-0247` の決定を変更しない。
