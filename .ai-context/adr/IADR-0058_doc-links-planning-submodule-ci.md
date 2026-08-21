---
title: IADR-0058 planning submodule 配下の破損リンクはトークン付きの定期ジョブで検査する
type: impl-adr
status: Accepted
related_ids:
  - NFR
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: ドキュメント整合)
---

# IADR-0058: planning submodule 配下の破損リンクはトークン付きの定期ジョブで検査する

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（ドキュメント整合・CI ゲート）
- 関連仕様書: `docs/specs/20260711_issue-232_doc-links-planning-submodule.md`、[`scripts/README.md`](../../scripts/README.md)
- Issue: #232（発見元 #59 = doc-links 導入）

## コンテキストと課題

`scripts/check-doc-links.js` は planning サブモジュールが未 populate の場合、`planning/` 配下へのリンクを検査対象外にする（CI の `actions/checkout` が submodule を空プレースホルダとして作るため、未 populate と populate 済みを区別する安全弁）。本体 CI の `doc-links` ジョブ（[`ci.yml`](../../.github/workflows/ci.yml)）は submodule なしで checkout するため、**planning への破損リンクは CI で一切検出されない**。実際、再編作業（#209/#210）で破損リンク 6 件（`ADR-0004_abac-authorization-model.md` 等、planning 側実ファイル名との不一致）が CI をすり抜けて蓄積していた。

planning リポジトリ（`endazon/project-planning`）は **private** のため、submodule を CI で取得するにはデフォルトの `GITHUB_TOKEN`（当該リポジトリスコープ）では不足し、別リポジトリへ read できる PAT/デプロイキーが要る。トークン運用の判断が必要になる。

## 検討した選択肢

1. **本体 `doc-links` ジョブで submodule を毎回 checkout する（PR 毎）**: 最も確実だが、(a) 全 PR で private リポジトリ用トークンを要し、fork PR では secrets が渡らず失敗する、(b) トークン未設定時に全 PR CI がブロックされる、(c) 高速・トークン不要という現行 `doc-links` の利点を失う。
2. **planning リンク検査をトークン付きの別ジョブ（scheduled + 手動）に分離する（本決定）**: 本体 PR CI は従来どおり高速・トークン不要で非 planning リンクを毎回検査。planning リンクは専用ワークフローが夜間 + `workflow_dispatch` でトークン付き submodule を取得して検査する。破損は最大 1 日で検出でき、PR CI をブロックしない。
3. **planning リンクを相対パスから URL 参照へ規約変更する**: 相対パス参照は 198 ファイルに及び、全面書き換えは大きな churn を生む。URL 到達性検査はネットワーク依存で CI が不安定になり、ファイル名の綴り不一致（本件の実害）は URL でも起こり得るため検査の質が上がらない。

## 決定

**選択肢 2 を採用する。**

- 専用ワークフロー `.github/workflows/doc-links-planning.yml`（`schedule` 夜間 + `workflow_dispatch`。ADR-0048 決定 2 により撤去済み）が `actions/checkout` を `submodules: recursive` + `token: ${{ secrets.PLANNING_REPO_TOKEN }}` で実行し、`node scripts/check-doc-links.js --require-planning` を走らせる。
- `check-doc-links.js` に `--require-planning` を追加：planning が未 populate（＝トークン未設定/取得失敗）なら **fail** させ、planning リンクを黙って検査対象外にする事故を可視化する。
- 本体 `ci.yml` の `doc-links` ジョブは変更しない（高速・トークン不要のまま非 planning リンクを毎 PR 検査）。
- メンテナは Secret `PLANNING_REPO_TOKEN`（本リポジトリと planning 双方へ read 権限を持つ fine-grained PAT 推奨）を登録する。

## 理由

- **PR CI を壊さない**: private submodule 用トークンを全 PR に要求せず、fork PR でも本体 CI が回る。
- **fail-loud**: `--require-planning` により、submodule 取得漏れ（＝検査の空振り）を「成功」と誤認しない。今回の事故（検査すり抜け）の再発を構造的に防ぐ。
- **既存様式の維持**: `check-doc-links.js` は外部依存ゼロのまま。検査ロジックは共通で、planning 込み/抜きの差はトリガとチェックアウトの差のみ。
- **churn 最小**: 相対パス参照の規約（198 ファイル）を維持する。

## 結果

- `.github/workflows/doc-links-planning.yml`（新規）: 夜間 + 手動、トークン付き submodule 取得、`--require-planning`。
- `scripts/check-doc-links.js`: `--require-planning` フラグと `planningPopulated()` を追加。
- `scripts/scripts.test.js`: `parseArgs` / `planningPopulated` の単体テストを追加。
- `scripts/README.md`: 方式・前提（`PLANNING_REPO_TOKEN`）を追記。

## フォローアップ

- メンテナによる `PLANNING_REPO_TOKEN` の登録（未登録の間、定期ジョブは fail してその旨を示す）。
- 将来 planning を public 化する場合は、トークン不要（`submodules: recursive` のみ）に簡略化でき、本体 `doc-links` へ統合する選択肢が復活する。

## 関連

- Supersedes: なし
- Superseded by: なし
