---
title: 作業仕様書 — フロントエンド単体テスト＋カバレッジの専用 CI
type: work-spec
status: completed
related_ids:
  - NFR
  - IADR-0033
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens"
related_specs:
  - ../adr/IADR-0034_frontend-coverage-gate.md
  - ../adr/IADR-0033_frontend-spa-foundation.md
---

# 作業仕様書: フロントエンド単体テスト＋カバレッジの専用 CI

決定は [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)。SPA 基盤 [IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md) の CI を拡張する。

## 起点となる計画書（トレーサビリティ）

- [IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md)（フロントエンド SPA 基盤・`frontend.yml`）
- NFR（品質: テストカバレッジの可視化と回帰防止）

## 目的・背景

`frontend.yml`（typecheck/lint/unit test/build/e2e）は既にフロントのテストを実行するが、**単体テストが
カバレッジ計測なし**で走っており、バックエンド CI（`ci.yml` の `dotnet test --collect`）にあるカバレッジ
可視化・回帰防止がフロント側に無かった。フロント用のテスト専用 GitHub Actions を追加し、カバレッジの
レポート生成としきい値ゲートを設ける。

## 対象範囲

- 含むもの:
  1. [`frontend/vite.config.ts`](../../frontend/vite.config.ts): `test.coverage`（provider=v8・reporter・include/exclude・thresholds）を追加。
  2. [`frontend/package.json`](../../frontend/package.json): `test:coverage`（`vitest run --coverage`）スクリプトと `@vitest/coverage-v8` を追加。
  3. [`.github/workflows/frontend-tests.yml`](../../.github/workflows/frontend-tests.yml): 単体テスト＋カバレッジ専用ワークフロー（成果物アップロード）を新設。
  4. [`CLAUDE.md`](../../CLAUDE.md): 「技術スタック別ルール」に実スタック（.NET 10 / React+TS+Vite / CI）の規約を追記。
- 含まないもの:
  - `frontend.yml`（既存複合 CI）の変更。テストゲートは新ワークフローに分離する。
  - 各画面（SC-01..11）のテスト追加。カバレッジ床の引き上げは後続で行う。
  - バックエンド CI（`ci.yml`）の変更。

## 方針

- provider は Vitest 標準の `@vitest/coverage-v8`。設定は `vite.config.ts` に集約。
- しきい値は**回帰防止のラチェット**。SPA 基盤時点の実測値のわずかに下を床に置く（`lines`/`statements` 25・`functions` 40・`branches` 60）。
- ワークフローは `paths: ["frontend/**", ...]` で `frontend/` 変更時のみ起動し、Node 22（既存 `frontend.yml` と同一）で `npm ci` → `npm run test:coverage` → カバレッジ成果物アップロード（失敗時も `always()` で保存）。

## 受け入れ基準

- [x] `npm run test:coverage` がローカルで緑（13 件）かつ設定しきい値を満たし終了コード 0。
- [x] カバレッジレポート（text-summary/lcov/html）が `frontend/coverage/` に生成される（`.gitignore` 済み・コミットしない）。
- [x] `.github/workflows/frontend-tests.yml` が `frontend/**` 変更の push/PR で `npm run test:coverage` を実行し、成果物をアップロードする。
- [x] しきい値はラチェットで、床を割る変更（テスト削除・無検証コード増）で CI が失敗する設計。
- [x] `CLAUDE.md`「技術スタック別ルール」に .NET 10 / React+TS+Vite / CI の規約が追記されている。
- [ ] （メンテナ確認）PR で `Frontend Tests / test` を必須ステータスチェックに設定する。

## テスト・検証（実行済み）

- `npm run test:coverage`（Vitest v8）: 13 件緑・しきい値達成・終了コード 0。実測 Statements 28.36% / Branches 64.58% / Functions 44.44% / Lines 28.36%。
- `git check-ignore frontend/coverage`: 無視対象（成果物は非コミット）。

## リスク・注意事項

- `.github/workflows/` は GitHub App 権限では編集不可（[doc-links 追加時](20260704_chore_ci-doc-links-check.md)と同様）。本ワークフローはローカル（`workflow` スコープ）でコミット/プッシュする。
- 初期しきい値は低く品質の絶対水準は保証しない。SC-01..11 実装でテストと床を段階的に引き上げる。
