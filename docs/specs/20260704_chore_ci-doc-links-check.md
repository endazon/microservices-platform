---
title: 作業仕様書 — docs 相対リンク検査を CI に組み込む
type: work-spec
status: completed
related_ids:
  - NFR
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/01_architecture-overview.md"
related_specs:
  - ../README.md
issue: "#59"
---

# 作業仕様書: docs 相対リンク検査を CI に組み込む

## 背景

Issue #59（必須仕様書の欠落補完・リンク切れ修正）の再発防止として、`docs/`
配下 Markdown の相対リンク実在を検査する [`scripts/check-doc-links.js`](../../scripts/check-doc-links.js)
を追加済みである（外部依存ゼロ・Node 標準のみ、破損リンクで終了コード 1）。

当初は `.claude/hooks/check-impl.js`（PostToolUse ガードレール）へ直接組み込む
案だったが、当該フックは保護対象で GitHub App 権限では編集できず、標準スクリプト
として切り出した。残タスクとして、このスクリプトを CI ゲートで自動実行し、
リンク切れを含む PR をマージ前に検出できるようにする。

## 方針

`.github/workflows/ci.yml` に、既存の .NET スタック用ジョブから独立した
スタック非依存の `doc-links` ジョブを追加し、`node scripts/check-doc-links.js`
を実行する。

- Node 標準モジュールのみで動くため、`actions/setup-node` で Node を用意すれば
  依存インストールは不要。
- `planning/` サブモジュール未チェックアウト時はスクリプト側が `planning/` 配下
  リンクを検査対象外とするため、CI ではサブモジュールを取得しない（軽量・安定）。

## 作業範囲

### 含むもの
- `.github/workflows/ci.yml` に `doc-links` ジョブを追加

### 含むもの（追記）
- `scripts/check-doc-links.js` の `planning/` 未チェックアウト判定の修正（CI 実測で
  判明。下記「追記: CI 実測による判定ロジック修正（`cd3b8d2`）」参照）。

### 含まないもの
- `.claude/hooks/check-impl.js` の変更（保護対象・当初案から切替済み）
- 既存 .NET ジョブ（lint / build-and-test）の変更

> 注: 当初は「`scripts/check-doc-links.js` のロジック変更」をスコープ外としていたが、
> CI 追加後の実測で `planning/` 未チェックアウト判定に漏れが判明したため、
> `cd3b8d2` で最小限のロジック修正を行った（下記追記参照）。

## 受け入れ基準

- [x] `node scripts/check-doc-links.js` がローカルで終了コード 0（現状 93 件の
      Markdown に破損リンクなし）
- [x] `ci.yml` に `doc-links` ジョブが追加され、PR 時に `node scripts/check-doc-links.js`
      を実行する
- [x] 追加ジョブは .NET SDK に依存せず Node のみで完結する
- [ ] （メンテナ確認）PR で `doc-links` を必須ステータスチェックに設定する

## リスク・注意事項

- `.github/workflows/` は GitHub App 権限では編集不可のため、本変更はローカルで
  コミットする。App 経由の自動 push では `workflows` スコープが必要になる点に注意。
- 破損リンクを含む既存 PR は本ジョブ追加後に赤くなる。マージ前に修正すること。

## 追記: CI 実測による判定ロジック修正（`cd3b8d2`）

`doc-links` ジョブ追加後、CI 上で「破損リンク 148 件」により失敗した。148 件はすべて
`planning/` サブモジュール配下（planning/projects 以下の Markdown への相対リンク）だった。

- **原因**: `scripts/check-doc-links.js` はサブモジュール未チェックアウト時に `planning/`
  配下リンクを検査対象外にする設計だが、旧判定は `fs.existsSync(subRoot)` のみだった。
  CI の `actions/checkout`（サブモジュール取得なし）は `planning/` を**空のプレース
  ホルダディレクトリ**として作るため `existsSync` が `true` を返し、スキップされずに
  全リンクが「破損」と誤検知されていた。
- **修正（`cd3b8d2`）**: 判定を「ディレクトリが存在し **かつ中身が空でない**（実際に
  populate 済み）」に変更した。

  ```js
  let populated = false;
  try { populated = fs.existsSync(subRoot) && fs.readdirSync(subRoot).length > 0; }
  catch (e) { populated = false; }
  if (!populated) return false;
  ```
- **検証**: 空の `planning/`（CI と同条件）で planning リンクを正しくスキップ／非 planning
  の実在しないリンクは従来どおり破損検出（回帰なし）／submodule populate 済みの実 docs
  で破損リンクなしを確認。
- **設計上の帰結**: `planning/` 配下リンクは CI（submodule 未取得）では検査されず、
  submodule がチェックアウトされた環境（ローカル / AI レビュー）でのみ検査される。
  これはスクリプト冒頭コメント記載の元設計に沿ったもの。
