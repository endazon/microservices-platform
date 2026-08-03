# 完了の定義（Definition of Done）

実装作業が「完了」と見なせる条件を定める。AI・人間ともに、PR を出す前・マージ前にこのチェックリストを満たす。

## 仕様・トレーサビリティ

- [ ] 着手前に作業仕様書（`docs/specs/`）を作成し、それに沿って実装した
- [ ] 該当する必須仕様書（機能/画面/通信/データ/技術/テスト/運用/セキュリティ）を作成・更新した
- [ ] 重要な実装判断を実装ADR（`docs/adr/`、`IADR-XXXX`）に記録した
- [ ] 起点 ID（FR/UC/SC/ADR）をブランチ名・コミット・コード・PR に残した
- [ ] 計画書（fixed/Accepted）に反していない。差異があれば `/plan-feedback` で環流した

## 品質・検証

- [ ] ビルドが成功する
- [ ] 受け入れ基準・ユースケースのフロー（基本/代替/例外）をテストに写像し、テストが green
- [ ] **テストの直前コメントに起点 ID を書いた**（`// FR-03, UC-01: ...`。写像規約は
      [`docs/tests/TEST_STRATEGY.md`](tests/TEST_STRATEGY.md)。`check-test-traceability.js` が検査する）
- [ ] フォーマット/lint が通る
- [ ] `/verify` を実行し合格した
- [ ] 計画外の機能追加・過剰な抽象化・不要な防御的実装がない

### 全面再実装（#454）期間の追加条件

再実装では既存実装を破棄し得るため、退行の検知手段をテストへ移す（#453）。各ドメイン issue
（#438〜#452）の PR は次も満たす。

- [ ] **カバレッジ床を下回っていない**。バックエンドは [`src/coverage-floor.json`](../src/coverage-floor.json)、
      フロントは [`src/vitest.config.ts`](../src/vitest.config.ts) の `thresholds`（[[IADR-0034]]）。
      **テストを増やしたら床を引き上げる**（ratchet。床の据え置きは事実上の緩和である）
- [ ] **ADR-0030 の不採用ライブラリを増やしていない**。移行したら `scripts/backend-library-baseline.json`
      から自プロジェクトを削除する（残件が減らないと標準への移行が終わらない）。
      ※ この baseline は #455 で導入される（本項は #455 マージ後に有効）
- [ ] 写像を後回しにした場合、[`scripts/test-traceability-allowlist.json`](../scripts/test-traceability-allowlist.json)
      へ**理由とともに**追加し、テストを書いた PR で削除する

## 安全

- [ ] 秘密情報（鍵・トークン・接続情報）をコミットしていない
- [ ] ADR で確定した制約（技術スタック・アーキテクチャ等）に違反していない
- [ ] 依存関係に既知の重大脆弱性がない（CI のセキュリティチェックが green）

## レビュー

- [ ] PR テンプレートのチェックリストを記入した
- [ ] CI（lint/build/test/security）が green
- [ ] 必要なレビュー（CODEOWNERS）の承認を得た
