---
title: 作業仕様書 — Dialog の試験が初期フォーカスを同期で評価し、フレームが遅いと落ちる
type: spec
status: done
related_ids: [NFR, IADR-0125]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031（フロントの技術スタック）
issue: "#2224"
---

# 作業仕様書 — Dialog の初期フォーカスの試験を waitFor で待つ（#2224）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。PR #2223 の `test`（frontend-tests.yml）が、無関係な
> `packages/ui/src/components/Dialog.test.tsx:61` で 3 回中 2 回落ちたことから起こした。

## 受け入れ基準（issue #2224）

- [x] 初期フォーカスを同期で評価している箇所を `waitFor` に改める。負荷を再現して、直す前に落ち・直した後に落ちないことを示す

## 原因

Base UI の Dialog は、dialog を描いた**後で**、アニメーションフレームを待って `initialFocus` を当てる。試験は
`expect(await screen.findByRole('button', { name: '取消' })).toHaveFocus()` で、`findByRole` は**ボタンが在る**まで待つだけ
（ボタンは dialog と同時に在る）なので、フォーカスの適用より先に 1 回だけ評価する。フレームが遅い（CI の負荷が高い）と落ちる。
同じ形が `knowledge/frontend/src/components/ConfirmDialog.test.tsx:46` にもある。

## 判断

1. **`waitFor(() => expect(getByRole(…)).toHaveFocus())` で待つ**（リポ内の `ConversionJobsPage.test.tsx` / `SecretItemManagementPage.test.tsx` が既に採っている形）。
   skip・retry・timeout の引き上げはしない。期待（取消側に当たる）は変えない —— 当たらなければ従来どおり赤になる。
2. **再現の方法**: `requestAnimationFrame` を 150ms 遅らせる注入を先頭に足した一時の複製で流す（コミットしない）。
   直す前の形は**毎回**落ち（CI と同じく「期待: 取消／実際: 開くボタン」）、直した形は 3 回とも通った。
   - ［記録］最初は CPU に負荷を掛けて `--pool=forks --poolOptions.forks.singleFork` で反復したが、これは**別の現象**を拾った:
     1 つの fork で 2 つ目に流れたファイルの「閉じる」試験が毎回落ちる（develop の元の試験でも同じ。既定の pool では起きない）。
     試験の手段の副作用なので本件の根拠にしない。既定の pool で負荷を掛けた 30 回の反復では、直す前も後も落ちなかった（CI の負荷を再現できなかった）。
3. 範囲は初期フォーカスの 2 箇所。`toHaveFocus` を同期で見るほかの箇所（`layout.test.tsx` のタブ移動・`NotificationBell.test.tsx` の Escape 後）は、
   フォーカスをイベントの中で同期に移す実装なので当たらない（走査して確かめた）。

## 検証

```bash
cd src && pnpm run test:coverage      # 161 files / 2039 tests passed
cd src && pnpm run lint && pnpm run typecheck && pnpm run format:check
```
