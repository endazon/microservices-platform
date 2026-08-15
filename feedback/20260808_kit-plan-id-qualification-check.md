---
title: キット環流 計画 ID 修飾の機械検査（check-plan-id-qualification.js）
type: plan-feedback
status: accepted
related_ids: [NFR, IADR-0115, IADR-0140, IADR-0143]
author: Claude
created: 2026-08-08
updated: 2026-08-14
dispatched: true
---

# 環流: 計画 ID 修飾の機械検査を `impl-handoff-kit` へ

## なぜ環流するか

[[IADR-0115]] 決定 3 は「**汎用的な改善はキットへ環流する**」と定める。#576 で
`.claude/rules/traceability.md` の**汎用節**（「複数プロジェクトを跨ぐ場合の ID 修飾」）へ
条文を +9 行足した。**固有設定節ではない**ため、固有デルタ種 3 では説明できない。
**環流しなければ次のキット同期で条文ごと消える**（[[IADR-0141]] が同じ理由で環流している）。

## 環流する内容

| 対象 | 種別 | 内容 |
| --- | --- | --- |
| `.claude/rules/traceability.md` | 汎用条文 | 「複数プロジェクトを跨ぐ場合の ID 修飾」節へ**機械検査の導線**を追加。`check-cross-repo-refs.js`（issue / PR 番号）とは**対象・ファイル走査範囲・`CHANGELOG.md` の扱いが違う**ことを明記 |
| `scripts/check-plan-id-qualification.js` | 新規スクリプト | `<PROJ>/<ID>` 書式の空白区切り違反を検出。`--self-test` つき・外部依存ゼロ |
| `scripts/scripts.repo.test.js` | 結線 | `ci.yml` の `scripts-tests` へ相乗り（新ワークフローを足さない） |

## キット側で汎用化が要る点

- **`PROJECT_PREFIXES` / `ID_KINDS` はリポジトリ固有**（本リポは `AST` のみ）。キットでは
  設定ファイルか環境変数から読む形にするか、**空なら fail-open で skip** する必要がある。
- **`EXCLUDED_PATH_RE` の `src/ai-stock-trading/` は固有**。キットでは `.gitmodules` から導出する
  （[[IADR-0120]] が同型の導出を確立している）。
- **`maskCode` を `check-cross-repo-refs.js` から借りている**ので、キットへは 2 本セットで渡す。

## 実装側で得た知見（キットの設計に効く）

1. **走査対象を `git ls-files` から引く検査器は、自分自身を走査して落ちる。** しかも
   **新設直後は untracked なのでローカルでは見えず、コミット後の CI で初発火する。**
   `__filename` からのパス導出で外すこと（除外リストは腐る）。[[IADR-0143]] 決定 4。
2. **自己除外の自己試験でファイル名をリテラルに書かない。** リネームで vacuous truth になる。
   導出したうえで「自ファイルは追跡下に在る」ことも併せて主張する。
3. **区切りは空白だけではない。** wiki リンク・TAB・バッククォート・全角括弧が実在する。
   **同一行で片方だけ直る**という形で引き漏らしが表面化した。

## 起票状況

| 送り先 | 状態 |
| --- | --- |
| `impl-handoff-kit` へのフィードバック | **完了。planning#316 として起票済み**（2026-08-11・`decision-needed`） |
| 計画リポ（`project-planning`）への裁定依頼 | **不要**（実装リポ内の規約であり計画の決定に触れない） |
