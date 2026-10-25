---
title: 作業仕様書 — SC-12 のリダイレクト URI の試験を貼り付けに替え、Vitest の既定の時間上限との余裕を取り戻す
type: spec
status: done
related_ids: [SC-12, UC-09, FR-16]
author: claude
created: 2026-10-23
updated: 2026-10-23
issue: "#2089"
---

# 作業仕様書 — SC-12 のリダイレクト URI の試験を貼り付けに替える

> 本仕様書は実装着手前に作成した（2026-10-23）。変更は試験ファイル 1 つだけで、製品コード・設定（`testTimeout`）は変えない。

## 起点

- PR #2089（AST の pin 更新。frontend に差分なし）の frontend-tests `test` ジョブで、`McpClientManagementPage.test.tsx` の「requires redirect URIs only for the attended kind and sends them line by line」が 5053ms で Vitest の既定の上限 5000ms を超えて落ちた。
- develop の直近の成功 run（39778715536、2026-10-22）でも同じ試験は 2908ms で、同ファイル内で最も重い。CI の並列度次第で上限に届く揺らぎであり、PR の差分とは無関係。

## 原因（実測）

- リダイレクト URI を `user.type` で 1 文字ずつ打っていた（2 回で約 120 打鍵）。入力欄の状態は画面全体のフォームの状態にあり、1 打鍵ごとに一覧の表を含む画面が再描画される。
- 手元で同試験のみを 3 回ずつ実行: 変更前 2756 / 3198 / 2811ms、変更後 1638 / 1569 / 1679ms（単独実行のため初回描画の立ち上がりを含む）。ファイル全体で走らせると変更後 434ms。

## 方式

- URI の 2 箇所を `user.click`（または直前の `user.clear` による焦点）＋ `user.paste` に替える。改行は `\n` で渡す。
- 検証は「登録」押下時に純関数（`validateRegistration`）で行う実装であり、打鍵の途中経過は本試験の対象ではない。送る値・期待値は変えない。
- 採らない案: `testTimeout` の引き上げ（対症で、重さが残る）・skip（禁止）。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | 期待値（否定形の 2 種・陽性対照の送信本文・無人での入力欄の消滅）が変わらない | `git diff` が入力手段の行とコメントだけ |
| AC2 | 入力が実際に状態へ届いていることを試験が見張っている | 変異: 入力欄の `onChange` を no-op にすると赤（実測: 「Expected element to have text content」で失敗） |
| AC3 | 同試験の所要時間が下がる | 上表の実測。CI の frontend-tests で確認 |
| AC4 | lint・format・typecheck・同ファイルの全試験が緑 | `npx eslint` / `npx prettier --check` / `pnpm run typecheck`（knowledge/frontend）/ `npx vitest run <file>`（21 passed） |
