---
title: Obsidian 実機目視の範囲を実測で絞り、人が行う目視の手順と合格条件を手順ガイドに置く
type: spec
status: done
related_ids:
  - FR-20
  - SC-20
  - UC-11
  - SC-17
  - ADR-0037
  - ADR-0096
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# 作業仕様書: Obsidian 実機目視のチェックリスト（#1235）

## 背景

#1235 は Obsidian 本体での目視（Vault イベント・競合 3 択・設定タブ・失効トークン）を求め、
2026-09-11 に計画 ADR-0096 フォローアップ 2 が「無効化 → 同期トークンで API を叩く → 401」を足した。
2026-09-26 の利用者依頼は「AI がやれる範囲をやり、残りは正確な手順にする」である。

## AI 側で確かめたこと（2026-09-26・`origin/develop` `e868ddda`）

### 1. 「API 名や引数を間違えていても緑になる」は成り立たない（型検査が効いている）

プラグインは devDependency の公式型定義 `obsidian`（1.13 系）に対して `tsc --noEmit` される（`src/obsidian-plugin/package.json` の
`typecheck`。ルートの `pnpm -r run typecheck` を `frontend.yml` が `src/obsidian-plugin/**` の変更で起動する）。
検出力を変異で確かめた（作業場所は scratch への写し。主作業ツリーは変更していない。テスト用の `vitest/globals` を除いた
tsconfig で `*.test.ts` を外して実行）:

```console
$ tsc -p tsconfig.mh.json                                   → exit 0（基準）
M1 vault.on('modify' → 'modifyy')                            → TS2769 No overload matches this call（exit 2）
M2 'rename' の callback 第 1 引数を number に                → TS2769 …'(file: TAbstractFile, oldPath: string) => any'（exit 2）
M3 requestUrl({ throw: false } → throws)                     → TS2561 'throws' does not exist in type 'RequestUrlParam'
M4 saveLocalStorage を 1 文字削る                            → TS2345 'App' is not assignable to parameter of type 'LocalStorageLike'
復元                                                          → exit 0
```

したがって目視の射程は**実行時の振る舞い**（イベントが届く時機・通知が見えるか・ダイアログが描かれ押せるか・
トークンの保管場所）に絞れる。

### 2. 無効化で同期トークンは失効しない（実機なしで確定）

- 無効化端点（AuthorizationService `DisableUser`）は `SetEnabledAsync(false)` → `RevokeSessionsAsync` → 保持起点の刻印だけを行う。
- 同期トークンの検証（DocumentService `ObsidianSyncEndpoints.ResolveDeviceAsync`）は端末の `RevokedAt` と `ExpiresAt` だけを見る。
- `git grep -n "RevokeAll\|\.Revoke(" -- src ':!**/Tests/**' ':!src/ai-stock-trading'` → 本人操作の端点と BFF の中継と生成物のみ。無効化経路からの呼び出し 0 件。

→ 不具合として **#1532** を起票し、ADR-0096 フォローアップ 2 の結果として **planning#662** へ環流した（方式を実装判断で閉じてよいかの確認つき）。

## 変更

- `docs/how-to/obsidian-plugin-device-check.md` を新設（目視の 5 項目・手順・合格条件・記録表）。
  導入手順ガイド（`obsidian-plugin-install.md`）は open PR #1528 が編集中のため触らない（交差を避ける）。

## 受け入れ基準（本 PR の射程）

- [x] 機械で測れている面と、人が見る面の境界を実測つきで書いた
- [x] 人が見る 5 項目について、手順・見えるべきもの・証跡の形式・記録表がある
- [x] 無効化時の同期トークン失効をコードで確定し、起票・環流した
- [ ] 実機での目視そのもの —— **利用者の手が要る**。#1235 は閉じない

## 母集合

- 未検証面の母集合は #1235 の 2026-09-05 棚卸しの 6 モジュール（`main.ts` / `conflictModal.ts` / `vaultFileStore.ts` /
  `settingsTab.ts` / `settings.ts` / `obsidianTransport.ts`）＋ `tokenStore.ts` の実保管先。手順ガイドの項目 1〜4 が全モジュールを踏む
  （`vaultFileStore` は項目 1・2 の読み書き、`obsidianTransport` は全項目の送受信）。
- 振る舞いの記述は `docs/how-to/obsidian-plugin-install.md` §同期の振る舞い と `conflictModal.ts` の文言から写した
  （フォルダ外への移動は削除を送らない —— `pushPlanner.ts` の `movedOut` → untrack）。
