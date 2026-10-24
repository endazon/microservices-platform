---
title: 作業仕様書 — [E-41] live-ro: 検索・文書・グラフの到達と ABAC 対照
type: spec
status: in-progress
related_ids: [NFR, FR-03, FR-04, FR-05, FR-13, FR-17, SC-01, SC-02, SC-03, SC-04, SC-08, SC-18, IADR-0255, IADR-0284, IADR-0570, IADR-0626, IADR-0627]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-01/02/03/04/08/18）
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md（clearance の階段・deny-by-default）
issue: "#2028"
---

# 作業仕様書 — live-ro: 検索・文書・グラフの到達と ABAC 対照（E-41）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。親は #2013（網羅表 B 節）。先行は E-40（#2027・IADR-0626）。
> 🔴 **稼働中の PoC には触れない**。live の確認は PR の `e2e-live`（使い捨ての k3d スタック）だけで行う。

## 範囲（網羅表の行）

| 行 | 内容 | 役割 | 門 |
| --- | --- | --- | --- |
| M-029 | SC-01 到達・候補 3 軸 200・質問を送らない | user | なし |
| M-030 | SC-01 候補の対照（internal の文書にだけ在る部門が user に出て noaccess に出ない） | user / noaccess | `E2E_SEARCH_HITS` |
| M-036 | SC-02 一覧が 200 で描かれる／合言葉で seed 2 件がヒット | user | なし／あり |
| M-037 | SC-02 noaccess では public は出て internal は出ない | noaccess | あり |
| M-044 | SC-03 internal の seed 文書を開ける | user | あり |
| M-045 | SC-03 noaccess が internal の ID を直接開くと「文書が見つかりませんでした」・BFF 404。public は開ける | noaccess | あり |
| M-048 | SC-04 到達・ページ一覧 200・ツリーか空の表示 | user | なし |
| M-051 | SC-08 到達・候補 3 軸 200・分析を送らない | user | なし |
| M-056 | SC-18 public の seed 文書を起点に近傍 200・起点名とグラフか空の表示 | user | あり |

## 受け入れ基準（issue #2028）

- [ ] `SEARCH_HITS` 門で skip/judge が切り替わる —— `E2E_SEARCH_HITS=1` で判定、未設定で理由つき skip（`e2e-live.yml` は 1 で流す）
- [ ] `e2e-noaccess` で seed 文書が見えない —— internal の seed 文書が一覧・候補に出ず、直 URL は 404（両側で固定）
- [ ] PR の `e2e-live` が緑で、live-ro の skip 0・teardown 失効 6・成果物の走査 0

## 事前の確認（オーケストレーターの指示 1）

- `e2e-noaccess` の定義: realm の宣言（`deploy/keycloak/microservices-platform-realm.json`）は `/clearance/public` のみ・部門なし（IADR-0570）。
  基準書 §5 は役割の名前を挙げるだけで属性を定めていない。計画書は試験アカウントを定めていない（#2013 の設計）。
- seed の文書は従前 public の 1 件だけで、ABAC の階段（`deploy/local/abac-seed/policies.json`）は clearance=public に public を読ませる。
  したがって **noaccess にも seed 文書が見える**。「noaccess で seed 文書が見えない」は、internal の対照文書を足して初めて成り立つ。
  noaccess の定義（clearance=public の最小権限）とは矛盾しない（定義のままで、見えない文書を足す）。

## 判断（IADR-0627）

1. 検索用 seed に **confidentiality=internal・department=sales の 2 件目**を足す。user（internal）に見え、noaccess（public）に見えない。
   1 件目（public）は noaccess 側の陽性対照に使う。department=sales は 2 件目にだけ在る値で、候補の対照に使う。
   先頭は public のまま（`verify-oidc-edge-flow.sh` の日本語の門が `documents[0]` を読む）。2 件目も合言葉と、1 件目のタイトルから導く日本語の語（「検索導線の検証用文書」）を本文に含む（投入器の規約・`scripts.repo.test.js` #1118 の不変条件。実測で赤になったので本文へ足した）。
   `scripts.repo.test.js` の「全件 public」の不変条件を「先頭は public・internal の対照が 1 件以上・それ以外の区分を置かない」へ改める。
2. 門の変数は `E2E_SEARCH_HITS`（live の他の変数と同じ `E2E_` 接頭辞。値域は `1` か未設定・空。`E2E_LIVE=1` 無しは例外）。
3. `e2e-live.yml` は readiness の後に seed 投入器（ABAC・検索）を**明示的に再実行**して終了コードを見る（integration-stack.yml と同じ。up の中は best-effort）。
   live の段に `E2E_SEARCH_HITS=1` を渡す。paths に `deploy/local/search-seed/**`・`deploy/local/abac-seed/**` を足す。
4. noaccess 側の陰性の判定の前に、**user の別 context で 2 件とも索引に入ったことを確かめる**（取り込み前の空で緑にしない）。
5. 取り込みは非同期なので、ヒットは画面を開き直しながら `toPass`（上限 120 秒）で待つ。時間で待たない。
6. 網羅表 M-030 の「noaccess では候補が空」は、public の文書の部門（engineering）が noaccess にも出るので成り立たない。
   「internal の文書にだけ在る値が出ない」と読み替える（#2013 へ注記）。M-048 の「接続先未設定」の文言は撤去済み（#1200）なので、
   「ページツリーか空の表示、失敗の表示は出ない」と読む。

## 母集合（規則 9・10）

- 「seed は public だけ」を前提にする記述を `grep -rn "confidentiality.*public\|正の対照と負の対照が同じ 1 件"` で引いた:
  `deploy/local/search-seed/documents.json` の `_comment`（追記で更新）、`scripts/scripts.repo.test.js` の不変条件（改めた）、
  `scripts/seed-search-documents.js` の冒頭（1 件目の説明。変更不要）。`verify-oidc-edge-flow.sh` は `find`/`some` で合言葉を含む文書を
  探すので 2 件目で結果が変わらない（`documents[0]` を読むのは日本語の門だけで、先頭は変えない）。

## 検証

- `tsc`・`eslint`・`prettier`、`live-opt-in.smoke.spec.ts`（`E2E_SEARCH_HITS` の値域）、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
- `node scripts/seed-search-documents.js --dry-run`（2 件）。
- PR の `e2e-live` で live-ro が緑・skip 0・teardown 失効 6・走査 0。
