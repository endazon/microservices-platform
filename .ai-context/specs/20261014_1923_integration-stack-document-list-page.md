---
title: 作業仕様書 — 実機の検証器が文書一覧をページとして辿るようにする（#1925 のマージ後の integration-stack）
type: spec
status: done
related_ids: [FR-06, SC-05, NFR-09, IADR-0537]
author: claude
created: 2026-10-14
updated: 2026-10-14
plan_refs: []
issue: "#1923"
---

# 作業仕様書 — 実機の検証器が文書一覧をページとして辿る（#1923）

> 本仕様書は実装着手前に作成した（着手 2026-10-14）。基点は MSP `origin/develop` `186b57f7`（#1925 のマージ直後）。

## 起点

- #1925（IADR-0537）で `GET /bff/documents` の応答が全件の配列から 1 ページ（`{ items, total, offset, limit }`）へ変わった。
- マージ後の develop で `integration-stack`（run 38574914487）が赤になった。失敗した段は「Bring up the integration stack」で、
  **31 秒で終わっている**（直前の緑の run は同じ段に 14 分かかる）。この段は k3d の作成と前提の確認までしか進んでいない時間であり、
  #1925 の変更（アプリのコード・文書・baseline 4 本。`scripts/` の実行物と `deploy/` は変えていない）が効く段ではない。
  失敗した job を再実行して確かめる（結果は PR に書く）。
- 一方で、起動の後に走る門 `verify-oidc-edge-flow.sh`（ABAC_POSITIVE=1 / SEARCH_HITS=1）は、一覧を**配列として**読んでいる
  （段 14 の正の対照・段 15 の負の対照・S1 の seed 確認）。#1925 の後は配列でないので、段 14 は件数 -1（空と同じ FAIL）、
  段 15 は -1（0 でないので「全開放」の FAIL）、S1 は「配列ではない」の FAIL になる。**起動が通れば必ず落ちる回帰**である。

## 母集合（規則 9）

develop `186b57f7` で `bff/documents`（一覧）と `ListDocuments` を、`.md`・OpenAPI・生成物・C# 試験を除く全追跡ファイルと、
隣接の AST リポジトリで引いた。

| 呼び出し元 | 対応 |
| --- | --- |
| `scripts/verify-oidc-edge-flow.sh` 段 14・15・S1（セッションつきの一覧の GET） | **ページを辿る形へ直す**（本件） |
| 同 段 1〜3 の無トークン 401 の確認・段 13 の POST | 応答の形を読まないので変えない |
| `src/knowledge/frontend/.../useDocumentAdmin.ts` | #1925 で追随済み |
| BFF `DocumentReadGrpcClient.ListPageAsync`（`ListDocuments` の唯一の呼び出し元） | #1925 で追随済み |
| `scripts/seed-*.js`・`deploy/`・`k8s-local-up.sh` | 一覧を読まない（`POST /documents` で投入するだけ） |
| AST（`ai-stock-trading`） | 0 件 |

## 設計

- `fetch_document_list <jar> <out>` を足す。`offset` / `limit=100` で `total` まで辿り、items を 1 つの配列として `out` へ書く。
  非 200 はその本文をそのまま書き（判定は状態コードを先に見る）、ページの形でなければ配列でない値を書く（fail-closed）。上限 50 ページ。
- 3 段の判定（件数・作成した文書の有無・seed の markdownUri）は**変えない**。読む配列を「全ページの items」にするだけ。
- `total` は ABAC で絞った後の件数なので、負の対照（属性の無い利用者は 0 件）の意味は変わらない。

## 試験

- `scripts.repo.test.js` に、3 段がページを辿ること・ページの形を確かめることを固定する（1 か所を直の GET に戻す変異で落ちる）。
- 関数を擬似サーバ（250 件・0 件・配列のまま）に当てて、全件の集約・空・fail-closed を手元で確かめた。
- 実機の門は integration-stack（develop への push・schedule・手動でだけ走る。PR では走らない）で確かめる。
