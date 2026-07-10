---
title: doc-links が planning submodule 配下の破損リンクを検出できない不具合の修正（Issue #232）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0058
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: ドキュメント整合)"
related_specs:
  - "../adr/IADR-0058_doc-links-planning-submodule-ci.md"
---

# 仕様書: doc-links が planning submodule 配下の破損リンクを検出できない不具合の修正（Issue #232）

## 起点となる計画書（トレーサビリティ）

- 非機能要件(NFR): ドキュメント整合・CI ゲート
- 発見元: リポジトリ再編（#210）作業中。破損リンク 6 件が CI をすり抜けて蓄積（#59 で導入した doc-links の盲点）。
- 実装判断: [[IADR-0058]]
- Issue: #232

## 目的・背景

本体 CI の `doc-links` ジョブは `actions/checkout` を submodule なしで実行するため、private な planning サブモジュール配下への破損リンクを検出できない（`check-doc-links.js` は planning 未 populate 時は planning リンクを検査対象外にする安全弁を持つ）。planning リンクも CI（または定期ジョブ）で検出できるようにする。

## 対象範囲

- 対象（新規/変更）:
  - `.github/workflows/doc-links-planning.yml`（新規）: 夜間 + `workflow_dispatch`、トークン付き submodule 取得、`--require-planning`。
  - `scripts/check-doc-links.js`（変更）: `--require-planning` フラグ・`planningPopulated()`・`module.exports` を追加。
  - `scripts/scripts.test.js`（変更）: `parseArgs` / `planningPopulated` の単体テストを追加。
  - `scripts/README.md`（変更）: 方式・前提を追記。
  - `.github/workflows/ci.yml`（変更・コメントのみ）: `doc-links` ジョブに planning は専用ジョブが担う旨を注記。
- 対象外:
  - planning リンクの URL 参照化（選択肢 3。churn 大・検査の質が上がらず不採用。IADR-0058 参照）。
  - `PLANNING_REPO_TOKEN` の登録（メンテナ作業）。

## 実装方針（IADR-0058 採用方式 2）

1. 本体 PR CI（`ci.yml` の `doc-links`）は高速・トークン不要のまま維持（非 planning リンクを毎 PR 検査）。
2. planning リンクは専用ワークフローが夜間 + 手動でトークン付き submodule を取得して検査。
3. `--require-planning` で未 populate を fail 扱いにし、取得漏れ（＝検査の空振り）を「成功」と誤認しない。

## 受け入れ基準（Issue #232）との対応

- [x] planning 配下への破損リンクが CI（または定期ジョブ）で検出される
  - `doc-links-planning` ジョブが `submodules: recursive` + token で planning を取得し、`check-doc-links.js` が planning リンクを実在検査する。`--require-planning` で取得漏れは fail。
- [x] 採用方式と理由が IADR または scripts/README に記録される
  - [[IADR-0058]] と `scripts/README.md` に記録。

## 検証

- `node scripts/check-doc-links.js`（ローカル・planning populate 済み）→ 232 件 破損 0（planning リンクも検査済み）。
- `node scripts/check-doc-links.js --require-planning`（populate 済み）→ 通過。
- 未 populate を模した検証 → `--require-planning` が exit 1。
- `node scripts/scripts.test.js` → 追加テスト含め全 pass。
- 破損 planning リンク注入 → populate 済みでは検出される（回帰の実挙動確認）。

## 実装判断・フォローアップ

- 方式選定（別ジョブ scheduled・トークン付き / URL 化不採用）は [[IADR-0058]] に記録。
- `PLANNING_REPO_TOKEN` 登録はメンテナ作業（未登録の間、定期ジョブは fail してその旨を示す）。
- planning が将来 public 化されればトークン不要化・本体 doc-links への統合が可能。
