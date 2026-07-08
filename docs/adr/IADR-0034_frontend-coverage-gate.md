---
title: IADR-0034 フロントエンド単体テストのカバレッジ計測とラチェット型しきい値ゲート
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0033
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens"
related_specs:
  - ../specs/20260708_ci_frontend-test-coverage.md
  - ./IADR-0033_frontend-spa-foundation.md
---

# IADR-0034: フロントエンド カバレッジゲート

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: claude（ユーザー依頼: frontend テスト用 CI の追加）

## 起点・関連

- [IADR-0033](./IADR-0033_frontend-spa-foundation.md)（SPA 基盤・`frontend.yml` の CI 骨組み）を拡張する。
- NFR（品質: テストカバレッジの可視化と回帰防止）。

## コンテキストと課題

SPA 基盤（[IADR-0033](./IADR-0033_frontend-spa-foundation.md)）で `frontend.yml` は typecheck / lint /
unit test / build / e2e を実行するが、**単体テストはカバレッジ計測なし**で走っていた。バックエンド CI
（`ci.yml`）は `dotnet test --collect` でカバレッジを取得しており、フロント側だけ盲点だった。SC-01..11 を
feature として順次追加していく過程で、テスト不足・テスト削除による品質の後退を機械的に検知したい。

## 決定

1. **カバレッジ provider は Vitest 標準の `@vitest/coverage-v8`**（V8 内蔵計測。Istanbul より追加依存が軽く、
   既存 Vitest 構成にそのまま載る）。設定は `vite.config.ts` の `test.coverage` に集約する。
2. **専用ワークフロー `frontend-tests.yml` を新設**し、複合 CI `frontend.yml` とは分離する。テスト＝品質ゲート
   を独立させ、`npm run test:coverage`（`vitest run --coverage`）＋カバレッジ成果物（lcov/html/text-summary）の
   アップロードを担う。`paths: ["frontend/**", ...]` で `frontend/` 変更時のみ起動し、バックエンド CI と独立。
3. **しきい値は「回帰防止のラチェット」**とする。SPA 基盤時点の実測値（Statements/Lines 約 28%・Functions 約 44%・
   Branches 約 65%）のわずかに下を床（lines/statements 25・functions 40・branches 60）に置き、床を割る変更を CI で
   止める。未テストの UI コンポーネントが多く現状の全体値は低いが、**画面テストを増やすたびに床を引き上げる**運用と
   する（高すぎる初期しきい値で CI を恒常的に赤くしない）。

## 検討した代替

- **`frontend.yml` の unit test ステップに `--coverage` を足すだけ**にする案: 変更は小さいが、テスト＝品質ゲートと
   ビルド/e2e が同一ジョブに混在し、カバレッジ成果物の扱いや必須チェック設定が分かりづらい。専用ワークフローで
   関心を分離した。
- **provider に Istanbul を使う**案: 計測は正確だが追加依存が増える。V8 で十分なため不採用。
- **初期から高いしきい値（例 80%）**を課す案: スケルトン段階では非現実的で CI が恒常的に赤くなる。ラチェット方式で
   段階的に引き上げる。

## 結果

- 良い影響: フロントの単体テストカバレッジが可視化され（PR 成果物）、後退が CI で止まる。バックエンドと同様の
  品質ゲートがフロントにも揃う。
- トレードオフ: 初期しきい値は低く、品質の絶対水準を保証しない（あくまで床＝回帰防止）。各画面（SC-01..11）実装で
  テストとしきい値を段階的に引き上げる前提。

## 関連

- Supersedes: なし
- Superseded by: なし
