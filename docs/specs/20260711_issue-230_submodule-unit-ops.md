---
title: 追加可変機能ユニットの submodule 運用整備（Issue #230・起草＋CI 自動発見）
type: spec
status: done
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0060
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14)"
related_specs:
  - "../adr/IADR-0060_submodule-unit-operations.md"
  - "../how-to/adding-a-unit-submodule.md"
  - "../../src/README.md"
---

# 仕様書: 追加可変機能ユニットの submodule 運用整備（Issue #230）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: IADR-0056（ユニット第一構成）／IADR-0057（依存検査）／IADR-0058（private submodule の CI 取得）
- 実装判断: [[IADR-0060]]
- Issue: #230（フォローアップ 4）

## 目的・背景

追加可変機能ユニットを `src/<unit>/` の git submodule でリンクする構成は確定済み（IADR-0056）だが、
実運用（テンプレート・CI 連携・単独ビルド規約・バージョン固定）が未整備。本リポジトリ内で完結できる
範囲（テンプレート雛形・CI 自動発見・運用手順の起草）を整備し、外部リポ/実環境依存の残作業（サンプル
ユニット通し検証）は Issue に明記して繰延する。

## 対象範囲

- 対象（新規/変更）:
  - `.github/workflows/ci.yml`: `lint` / `build-and-test` を `src/*/backend/backend.slnx` の**自動発見ループ**へ
    （チェック名不変。ユニット追加で CI 編集不要）。
  - `templates/unit-template/`（新規）: 新ユニット雛形（backend slnx + サンプルサービス、frontend package.json +
    features 合成点、単独ビルド用フォールバック props の記載）。本体のビルド対象外（`src/` 外）。
  - `docs/how-to/adding-a-unit-submodule.md`（新規）: 通し運用手順。
  - `src/README.md`: サブモジュール追加節を how-to へリンク・CI 自動発見に更新。
  - `docs/adr/IADR-0060`（新規）: 方式の決定。
- 対象外（[[IADR-0060]] フォローアップ・#230 に残す）:
  - **サンプルユニットでの end-to-end 通し検証**（別リポジトリ作成が必須。本リポジトリ内で完結不可）。
  - CI の submodule 取得トークン（`UNIT_REPO_TOKEN`）登録と checkout への適用（実ユニット追加時）。
  - Renovate/Dependabot の `git-submodules` 有効化（メンテナ判断）。

## 実装方針

1. **CI 自動発見**: マトリクス化は必須チェック名が分岐しブランチ保護の再設定が要るため採らず、単一ジョブ内で
   `src/*/backend/backend.slnx` をループする（チェック名安定。IADR-0060 選択肢3）。`bash -eo pipefail` 既定で
   いずれか失敗すれば job も失敗する。
2. **テンプレート**: `src/` 外の `templates/` に置き、どの slnx / workspaces / 依存検査にも含めない
   （相対 ProjectReference は配置後の位置前提で、テンプレート位置ではビルドしない）。
3. **単独ビルド規約**: ユニットは常設 `Directory.Build.props` を持たない（submodule 配置時に単一情報源を
   上書きするため）。単独時のみ import-chain フォールバックを使う（テンプレ README に記載）。

## 受け入れ基準（Issue #230）との対応

- [~] 新規ユニットを「テンプレートから作成 → submodule 追加 → 合成点 1 行 + CI 1 行」で組み込める
  → **達成（CI は自動発見でむしろ 0 行。合成点 1 行 + private の場合の submodule 取得有効化）**。手順は how-to に整備。
- [ ] 通し検証（ビルド・テスト・compose 起動）がサンプルユニットで確認済み
  → **未達（別リポジトリ作成が必須のため本リポジトリ内で完結不可）**。#230 にコメントで残す。本 PR は `Refs #230`。

## 検証

- CI 自動発見ループをローカル模擬: `for slnx in src/*/backend/backend.slnx; do dotnet format "$slnx" --verify-no-changes; done` → platform/knowledge とも exit 0。
- `node scripts/check-doc-links.js` → 破損 0（how-to / テンプレ README のリンク実在）。
- `node scripts/check-unit-dependencies.js` → 違反 0（templates/ は `src/` 外で検査対象外）。
- テンプレートは本体ビルド・workspaces・ESLint の対象外であることを確認（`src/` 外配置）。

## 実装判断・フォローアップ

- 方式（CI 自動発見 / マトリクス不採用 / 単独ビルド規約）は [[IADR-0060]] に記録。
- サンプルユニット通し検証・CI 取得トークン・Renovate 有効化は #230 に残す（外部リポ/メンテナ判断）。
