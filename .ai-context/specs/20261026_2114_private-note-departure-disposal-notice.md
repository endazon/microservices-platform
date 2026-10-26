---
title: 作業仕様書 — SC-19 / SC-20 の業務関連資料の固定文言へ「30 日が過ぎると復元できない形で削除されます」を足し、退職の扱いの文を 1 か所で定義する（#2114）
type: spec
status: done
related_ids: [FR-19, FR-20, SC-19, SC-20, ADR-0036, ADR-0096, ADR-0057, IADR-0428, IADR-0431]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2114"
---

# 作業仕様書 — 退職後の削除の 1 文（#2114）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。親は #2047（指摘 M-17）。

## 現状（origin/develop で再確認）

- SC-19（`PrivateNotesPage.tsx`）の固定文言は「個人資料は業務関連資料として扱われます。退職時には、退職日から 30 日間、管理者が閲覧することがあります。」だけである。
  SC-20（`SyncTargetFoldersPanel.tsx`）の 3 段落目も同じ 2 文目を別のメッセージとして複製している。試験 `PrivateNotesPage.test.tsx` がこの形を固定している。
- 計画の画面設計 SC-19（2026-09-11 追加・利用者裁定・環流 planning#603）は、固定文言へ「**30 日が過ぎると、この資料は復元できない形で削除されます。**」を足すと定める。
  根拠は ADR-0096 決定 1。**削除が未配備のあいだは本文を出さず「退職後の保管期間は定まっていません」を併記し、「30 日間、管理者が閲覧することがあります」だけを出し続ける形は採らない**（裁定 planning#286 の 3 点セット）。
- **削除は配備済みである（実測）。**
  - IADR-0431（Accepted）が IADR-0428 決定 5（呼び出し元の未配線）を解消した。`PrivateNoteMaintenanceService.RunAsync` の先頭で `PurgeDepartedOwnersAsync` が走る。
  - 退職の窓の照会は `Services:AuthorizationServiceGrpc` が構成された配備で gRPC 実装になる（`DocumentService/Program.cs`）。
    helm（`deploy/helm/microservices-platform/values.yaml` の `document:`）と compose の両方で構成されている。
  - したがって暫定文言（「定まっていません」）ではなく、**本文（削除の 1 文）を出す**のが計画どおりである。
- 計画の SC-20 の固定文言（3 段落目）は、SC-19 の 2026-09-11 の追加に追随していない（削除の 1 文が無い）。

## 方針

- 退職の扱いの 2 文（「退職時には、退職日から 30 日間、管理者が閲覧することがあります。30 日が過ぎると、この資料は復元できない形で削除されます。」）を
  `lib/private-notes/departureNotice.ts` の 1 つのメッセージ記述子（`msg`）として定義し、`lib/private-notes/index.ts` から公開する。
  SC-19 と SC-20 は、各々の前後の文（「個人資料は……」「同期した資料は……」「同期対象フォルダに入れた私的なメモも……」）の `Trans` に
  プレースホルダとして差し込む（文の間の区切りは各言語の訳が持つ。英語の空白を JSX に書かない）。
- **SC-20 にも足す（判断点）。** 計画の SC-20 の確定文言には無いが、
  - SC-20 の 3 段落目が言う「同じ扱い」の対象（同期した個人資料）は ADR-0096 決定 1 の削除の対象と同じである。
  - SC-19 で「採らない」とした形（30 日で終わると読ませたまま、その後どうなるかを書かない）を SC-20 に残すと、同じ資料について 2 画面の説明が食い違う。
  - 計画の SC-20 の文言の追随は planning へ環流する（マージ後に起票。本 PR では起票しない）。
- 削除の説明（90 日の段落）とは引き続き別の段落に置く（SC-20 の「まとめない」）。追加する 1 文は 90 日の文ではない。
- en の訳: "When you leave the company, administrators may view them for 30 days from your last day. After 30 days, they are deleted and cannot be restored."（両画面の前の文の主語〔Private notes / Synced notes〕を `them` / `they` で受ける）。

## 受け入れ基準

- [x] SC-19: 固定文言に「30 日が過ぎると、この資料は復元できない形で削除されます」が出る（ja）。修正前は赤
- [x] SC-20: 業務関連資料の段落に同じ 1 文が出る。90 日の文は同じ段落に無いまま（既存の試験が緑のまま）。修正前は赤
- [x] en カタログ: 両画面の段落が英語で削除の文を含む（en ロケールで描いた試験）
- [x] 文言の定義が 1 か所: 退職の 2 文は `lib/private-notes` の記述子 1 つだけが持ち、両画面がそれを使う（両画面の描画が同じ記述子の訳を含むことを試験で固定）
- [x] カタログを再生成し（`pnpm run i18n`）、未翻訳キーが無い
- [x] 画面仕様書 SC-19 / SC-20・テスト仕様書を改める

## 母集合（規則 9）

`git grep -n "閲覧することがあります\|may view them for 30 days"`（submodule と生成物 `messages.ts` を除く）で引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `sc19-private-notes/components/PrivateNotesPage.tsx` | **直す** | 本件 |
| `sc20-obsidian-settings/components/SyncTargetFoldersPanel.tsx` | **直す** | 複製 |
| `sc19-private-notes/components/PrivateNotesPage.test.tsx` | **直す** | 旧形を固定している |
| `platform/frontend/src/locales/{ja,en}/messages.po`（と生成物 `messages.ts`） | **直す**（再生成） | カタログ |
| `docs/screens/SC-19_private-notes.md`・`docs/screens/SC-20_obsidian-settings.md`・`docs/tests/SC-19_*`・`docs/tests/SC-20_*` | **直す** | 文言そのものは引いていないが、固定文言の行に削除の文を足したことを書く |
| `docs/how-to/obsidian-plugin-install.md:63` | 外す | 退職の扱いを述べていない（業務関連資料になることだけ） |
| AST（submodule） | 外す | 本画面を持たない |

## 規則 11（窓）

- 時間差を扱う是正ではない（30 日の窓は文言の内容であり、本件の是正の形は窓を扱わない）。表は描かない。
