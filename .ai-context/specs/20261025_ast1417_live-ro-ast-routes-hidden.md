---
title: 作業仕様書 — live-ro に、e2e-user で取引機能の 4 画面が NotFound・ナビに株式自動売買が無いこと（AST 網羅表 A-002 の移し替え）を足す
type: spec
status: in-progress
related_ids: [NFR, IADR-0035]
author: claude
created: 2026-10-25
updated: 2026-10-25
issue: "AST#1417"
---

# 作業仕様書 — live-ro: 取引機能の 4 画面の閲覧者の陰性（AST の A-002 を移す）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。起点は AST の網羅表（AST#1410）の **A-002** と AST#1417（E-43）。
> 🔴 稼働中の PoC には触れない。live の確認は PR の `e2e-live`（使い捨てスタック）で行う。

## なぜ本リポジトリへ移すか

AST の live は、本リポジトリの live の setup が残した取引オーナーの storageState だけを読む（AST/IADR-0588・本リポジトリの IADR-0628 は取引オーナーだけを残す）。
A-002（閲覧者〔e2e-user〕で 4 画面が NotFound・ナビに株式自動売買が無い）を AST に置くと、e2e-user の state も残す必要があり、cookie を手元に置く面が広がる。
本リポジトリの live-ro は e2e-user の state で既に流れ、同じ実行の中で失効まで閉じるので、ここへ移す（AST/IADR-0589 決定 5）。

## 受け入れ基準

- [ ] `e2e/live/shell.ro.spec.ts` に A-002 の 1 件: e2e-user で `/settings`・`/settings/risk`・`/controls`・`/opend-auth` がどれも NotFound、ナビに「株式自動売買」が無い
- [ ] PR の `e2e-live` が緑・skip 0・teardown 失効 6・成果物の走査 0

## 判断

- 新しい IADR は置かない（AST/IADR-0589 決定 5 が移し替えを決めた）。
- 取引機能の画面のパスは共通シェルの直下（`/trading` の接頭辞は無い）。根拠: `/settings`・`/settings/risk`・`/controls` は
  `src/platform/frontend/src/app/routing/router.test.ts` の test「mounts the AST screens in the typed route tree」の
  `arrayContaining(['/settings', '/settings/risk', '/controls'])`、`/opend-auth` は AST の
  `frontend/src/features/sc04-opend-auth/routes/sc04OpendAuthRoute.tsx` の `path: '/opend-auth'`。
- **ナビの陰性はナビが描かれている画面（`/ask`）で見る**。NotFound の画面でナビが描かれていなければ「株式自動売買が 0 件」は空振りする。
  `/ask` で `navigation` の項目（検索・質問）が見えることを確かめてから 0 件を見る。M-013（4 役割の左ナビの表）と重なるが、A-002 の行を単独で
  読めるよう重複を受容する（陰性を空振りさせないことを優先）。
- 起点 ID の `NFR` は無採番である。traceability.md の「無採番を許す 2 場合」の **2**（ID 列はあるが、その作業に当たる番号が無い。E2E の網羅というメタ作業）に当たる（IADR-0179 決定 1・IADR-0188）。
- 権限外の画面は存在秘匿の NotFound を描く（IADR-0035。AST の画面は AST の `RequireRole` が同じ規約で描く）。
- 4 画面は `expect.soft` で見る（1 画面で落ちても、1 回の実行で 4 画面すべての結果が出る）。網羅表の行 A-002 は 1 つの test のまま。
