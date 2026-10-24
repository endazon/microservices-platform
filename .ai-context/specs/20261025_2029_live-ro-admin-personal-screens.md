---
title: 作業仕様書 — [E-42] live-ro: 個人・管理・運用画面の到達
type: spec
status: in-progress
related_ids: [NFR, NFR-18, FR-01, FR-05, FR-06, FR-09, FR-10, FR-12, FR-15, FR-16, FR-18, FR-19, FR-20, SC-05, SC-06, SC-07, SC-09, SC-10, SC-11, SC-12, SC-17, SC-19, SC-20, SC-21, SC-22, IADR-0035, IADR-0337, IADR-0626, IADR-0627]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（各画面のアクセス制御）
issue: "#2029"
---

# 作業仕様書 — live-ro: 個人・管理・運用画面の到達（E-42）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。親は #2013（網羅表 B・C 節）。先行は E-40（#2027）・E-41（#2028）。
> 🔴 **稼働中の PoC には触れない**。live の確認は PR の `e2e-live`（使い捨ての k3d スタック）だけで行う。

## 範囲（網羅表の行）

| 行 | 画面 | 役割 | 見るもの |
| --- | --- | --- | --- |
| M-061 | SC-19 `/my/notes` | user | 見出し・`GET /bff/private-notes` 200 |
| M-066 | SC-20 `/my/obsidian` | user | 見出し・`GET /bff/private-notes/devices` 200 |
| M-070 | SC-21 `/ai-suggestions` | user | 見出し・`GET /bff/graph/suggestions` 200（0 件でよい） |
| M-075 | SC-05 `/admin/documents` | operator | 見出し・`GET /bff/documents` 200・「＋ 新規登録」と操作の列が無い。seed の行が在る状態で行の破壊操作が 0（門 `E2E_SEARCH_HITS`） |
| M-081 | SC-06 `/admin/sources` | operator | 見出し・`GET /bff/datasources` 200 |
| M-086 | SC-07 `/admin/conversions` | operator | 見出し・`GET /bff/conversion/jobs` 200 |
| M-090 | SC-09 `/admin/abac` | admin | 見出し・属性体系とポリシーの取得 200・seed のポリシー名が揃う |
| M-091 | SC-09（＋SC-12・SC-17） | operator | NotFound・BFF 403（AdminOnly） |
| M-096 | SC-10 `/admin/ops` | operator | 見出し・`GET /bff/dashboard/summary` 200・KPI「検索総数」 |
| M-099 | SC-11 `/admin/config-viewer` | operator / user | operator: 見出し・`GET /bff/admin/config` 200。user: NotFound・BFF 404（存在秘匿） |
| M-103 | SC-12 `/admin/mcp-clients` | admin | 見出し・一覧 200 |
| M-108 | SC-17 `/admin/users` | admin | 見出し・一覧 200・`e2e-*` 6 名が居る |
| M-113 | SC-22 `/admin/secrets` | operator | 見出し・一覧は 200（応答に `value` の鍵が無い）か、保管先が無い構成の 503（type が vault-not-configured / vault-unavailable・画面がその旨を出す）のどちらか。値の列は無い |

## 受け入れ基準（issue #2029）

- [ ] 各画面の h1 が出る —— 全行で `openScreen`（見出し＋画面が引く GET の 200 を同じ表示で）
- [ ] operator は SC-09 で NotFound —— M-091（UI の NotFound ＋ BFF 403。同じ管理者限定の SC-12・SC-17 も）
- [ ] PR の `e2e-live` が緑・skip 0・teardown 失効 6・成果物の走査 0

## 判断

1. 新しい IADR は置かない（IADR-0627 の門・seed・カタログの形をそのまま使う）。
2. 「到達」は見出しだけでなく、**画面が引く BFF の GET が 200** を同じ表示で見る（見出しは描けても後段が 5xx の画面を緑にしない）。部品は `e2e/live/support/screens.ts`。
3. 否定系は隣の役割で見る（IADR-0337）。M-091 は operator で SC-09 に加え SC-12・SC-17 も見る（同じ AdminOnly の境界）。M-099 の陰性は user（構成ビューアは 403 ではなく 404 で存在を秘匿する。`ConfigBffEndpoints`）。
4. M-075 の行の破壊操作の不在は、行が在る状態でしか意味を持たないので、seed 文書が在る環境（`E2E_SEARCH_HITS=1`）でだけ判定する。「＋ 新規登録」と操作の列の不在は門なしで見る。
5. 文言は ja のカタログから引く（`support/catalog.ts`）。seed のポリシー名・文書名は seed の宣言から読む（写さない）。

## e2e-live で判明したこと（PR #2229 の初回）

- **M-108**: SC-17 の利用者の列は 1 つのセルに表示名（例「E2E User」）とユーザー名を縦に並べる（`UserAccountManagementPage` の user 列）。
  セルの名前の完全一致（`getByRole('cell', { name: 'e2e-user', exact: true })`）は当たらない —— **spec の誤り**。セルの中のユーザー名の行を引き、
  あわせて `GET /bff/admin/users` の応答に 6 名が居ることを見る形に直した（L1 の `sc17-users.smoke.spec.ts` も部分一致で引いている）。
- **M-113**: `GET /bff/secrets` が 503。BFF は Vault を配備していない構成で 503（`vault-not-configured`）を返す設計である
  （`SecretItemBffEndpoints.VaultUnavailableAsync`・IADR-0453 決定 5）。`e2e-live.yml` の使い捨てスタックは VAULT を立てないので、
  **製品は設計どおりで、spec の期待（常に 200）が環境を取り違えていた**。設計にある 2 つの形（200／503 と表示）のどちらかを確かめ、どちらの形でも値が出ない
  ことを見る形に直した。どちらの形だったかは test の annotation に残す。CI で通るのは 503 の形だけであり、200 の形（値の鍵が無い）は Vault の在る PoC で確かめる。

## 検証

- `tsc`・`eslint`・`prettier`、`E2E_LIVE=1 … --list`（live-ro に admin 12・personal 3）。
- PR の `e2e-live` で live-ro が緑・skip 0・teardown 失効 6・走査 0。
