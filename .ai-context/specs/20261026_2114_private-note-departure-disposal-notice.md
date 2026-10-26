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

［2026-10-26 改訂 / #2282 の監査］初版は SC-20 にも削除の 1 文を足し、退職の 2 文を共有の記述子 1 つで定義していた。
次の 2 点で改めた。

- **SC-20 は計画の確定文言どおりに戻す。** 計画にない 1 文を足すのは計画からの逸脱であり（CLAUDE.md 禁止事項）、判断するのは画面の持ち主（計画）である。
  SC-20 の 3 段落目への追加は、マージ後に planning へ提案する（同じ資料が ADR-0096 決定 1 で削除されるのに、2 画面の説明が食い違う）。
- **共有の記述子はやめる。** 共有する文は「閲覧の 1 文」だけになる。しかも共有モジュールを SC-19 と SC-20 の 2 つの遅延ルートから引くと、
  Rollup が 1 kB 未満の共有チャンクを 1 本切り出し、`check-chunk-budget` の床（12 本）を超えた。
  ペイロードを変えずに 1 文を共有する方法が無いので、各画面の `Trans` に書く（develop と同じ形）。

改訂後の方針:

- SC-19 の固定文言の `Trans` に「30 日が過ぎると、この資料は復元できない形で削除されます。」を足す（1 つのメッセージのまま）。
- SC-20 は変えない。試験で「計画の確定文言どおり（削除の 1 文を含まない）」を固定する。
- en の訳: "Private notes are treated as work-related material. When you leave the company, administrators may view them for 30 days from your last day. After 30 days, they are deleted and cannot be restored."
- 初期ロードの床: Lingui の ja / en カタログは入口チャンクに入るため、足した文の訳の分（+140 B）だけ増える。計画の文言であり避けられないので、
  `check-chunk-budget --update` で床を上げ、帰属を `scripts/chunk-budget-baseline.json` の `$comment` に残す。

## 受け入れ基準

- [x] SC-19: 固定文言に「30 日が過ぎると、この資料は復元できない形で削除されます」が出る（ja / en）。暫定文言（「定まっていません」）は出ない。修正前は赤
- [x] SC-20: 業務関連資料の段落が計画の確定文言どおりで、削除の 1 文を含まない（ja は全文一致、en も削除の文を含まない）
- [x] カタログを再生成し（`pnpm run i18n`）、未翻訳キーが無い
- [x] `check-chunk-budget --require`: 1 kB 未満の遅延チャンクは 12 本のまま。初期ロードの床の引き上げは帰属つき
- [x] 画面仕様書 SC-19・テスト仕様書（SC-19 / SC-20）を改める
- 取り下げた基準: 「文言の定義が 1 か所」（共有する文が 1 文だけになり、共有の形がチャンクの床を割るため。上記）

## 母集合（規則 9）

`git grep -n "閲覧することがあります\|may view them for 30 days"`（submodule と生成物 `messages.ts` を除く）で引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `sc19-private-notes/components/PrivateNotesPage.tsx` | **直す** | 本件 |
| `sc20-obsidian-settings/components/SyncTargetFoldersPanel.tsx` | 外す（改訂） | 計画の確定文言どおり。追加は planning へ提案する |
| `sc20-obsidian-settings/components/ObsidianSettingsPage.test.tsx` | **直す** | 確定文言どおりであることを固定する |
| `sc19-private-notes/components/PrivateNotesPage.test.tsx` | **直す** | 旧形を固定している |
| `platform/frontend/src/locales/{ja,en}/messages.po`（と生成物 `messages.ts`） | **直す**（再生成） | カタログ |
| `docs/screens/SC-19_private-notes.md`・`docs/tests/SC-19_*`・`docs/tests/SC-20_*` | **直す** | 文言そのものは引いていないが、固定文言の行に削除の文を足したこと（SC-20 は足さないこと）を書く |
| `docs/screens/SC-20_obsidian-settings.md` | 外す（改訂） | 画面は変えない |
| `scripts/chunk-budget-baseline.json` | **直す**（`--update`） | 初期ロードの床（帰属つき） |
| `docs/how-to/obsidian-plugin-install.md:63` | 外す | 退職の扱いを述べていない（業務関連資料になることだけ） |
| AST（submodule） | 外す | 本画面を持たない |

## 規則 11（窓）

- 時間差を扱う是正ではない（30 日の窓は文言の内容であり、本件の是正の形は窓を扱わない）。表は描かない。
