---
title: 作業仕様書 — SC-17 の個人資料の容量の照会を一括の口にする（N+1 の解消・#2348）
type: spec
status: done
related_ids: [FR-19, NFR-27, SC-17, SC-19, ADR-0037, ADR-0031]
author: claude
created: 2026-10-31
updated: 2026-10-31
issue: "#2348"
---

# 作業仕様書 — SC-17 の容量の一括照会（#2348）

> 本仕様書は実装着手前に作成した（着手 2026-10-31。develop `996e543a`）。起点は #2330 の作業仕様書（`20261030_2330_sc17-private-note-quota.md`）§残余リスクの 🟡（N+1）。

## 現状（develop で確認）

- SC-17 の一覧の「個人資料の容量」の欄（`PrivateNoteQuotaCell`）は、行ごとに `GET /bff/private-notes/quotas/{ownerId}` を 1 本引く。利用者が N 人なら照会も N 本になる。
  - 一覧は `DataTable` で、ページ分けは無い。絞り込み後の全行を描く。
- 後段 DocumentService の `GET /private-notes/quotas/{ownerId}`（`AdminOnly`）は、使用量（`PrivateNoteUsage.UsedBytesAsync`。最新版と論理削除済みの合計）と上限（行が無ければ既定の 1 GB）を返す。
- BFF は同じ資格（`AdminOnly`）の中継だけを持つ。

## 方針（契約の追加は非破壊）

1. **後段に一括の口を足す**: `POST /private-notes/quotas/lookup`（`AdminOnly`）。
   - 本文: `{ "ownerIds": ["alice", "bob", …] }`
   - 応答: `{ "items": [{ "ownerId", "usedBytes", "limitBytes", "percent" }, …] }`
   - 使用量は所有者ごとの合計を 1 回の集計で、上限は該当行を 1 回の照会で引く。依頼の順に、重複を除いて返す。上限の行が無い利用者は既定の 1 GB。
   - 🔴 **返すのは利用者ごとの 2 つの数（と割合）だけ**。題名・件数・中身は返さない（SC-19 の禁止）。
   - 入力の検証は 400。空・1,000 件超・空白の名前・256 文字超の名前を拒む。
   - 読み取りだが POST にする。利用者名の列を URL へ載せると要求行の上限（Kestrel 8 KB）に当たり得るため。
2. **BFF に同じ資格（`AdminOnly`）の中継を足す**: `POST /bff/private-notes/quotas/lookup`。後段の状態と本文を詰め替えずに返す。
3. **画面**: SC-17 は表示中の行（絞り込み後）の利用者名でまとめて 1 本引く（1,000 件ごとに分ける）。各行のセルはその結果から描く。
   - 読めない間・読めない行は「—」（推測の値を出さない。現行どおり）。
   - 上限を保存した後は、一括の照会も引き直す。
4. **既存の 1 件の口（照会・変更）は残す**（編集の区画が使う。契約は変えない）。
5. OpenAPI（手書き）に 2 つの schema と経路を足し、orval で再生成する。POST の照会は、operationId の上書きで query のフックとして生成する。

## 母集合（規則 9）

- `git grep -n "quotas" -- src` で、容量の照会の呼び出し元を引いた。
  - 画面: `PrivateNoteQuotaCell`（一覧の行）と `PrivateNoteQuotaEditor`（編集の区画）の 2 か所。
  - e2e の BFF モック: `sc17-users`・`session-revocation`・`a11y` の 3 spec。
- 一覧の行のセルだけを一括の照会に替える。編集の区画は 1 件の口のまま。e2e のモックには一括の口の応答を足す。

## 受け入れ基準

- [x] SC-17 の一覧の容量の照会が、表示中の利用者数に比例する本数にならない（1,000 件までは 1 本）。
- [x] 一括の口は、システム管理者だけが使える（BFF と後段の二重の守り）。
- [x] 応答に題名・件数・中身が無い（陰性の試験）。
- [x] 既存の 1 件の口は変わらない。

## PoC 配備への影響

- DocumentService（後段の口の追加）・BFF（中継の追加）・SPA（SC-17 の一覧）の再配備が要る。契約は追加だけなので、順序の制約は無い。ただし、SPA だけ先に配備すると一括の口が 404 になり、容量の欄が「—」になる。
- データの移行は無い。
