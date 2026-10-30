---
title: 作業仕様書 — 利用者による辺の明示的な付与を BFF に開ける（POST /bff/graph/edges の透過中継。#2107 の分割 A）
type: spec
status: done
related_ids: [FR-17, UC-10, SC-19, ADR-0034, ADR-0033, ADR-0036]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2107"
---

# 作業仕様書 — POST /bff/graph/edges（#2107 の A）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `aba2070d`）。親 #2047（指摘 M-10）。

## 分割（2026-10-30・コーディネータ承認）

- **A（本 PR）**: BFF の `POST /bff/graph/edges` を後段 `POST /graph/edges` への**透過中継**として足す。
  - openapi・orval の生成物・BFF の試験を含む。
  - issue の受け入れ基準の案 1（BFF 経由で作れ、400・404 が本文ごと透過する）と、案 2（権限外の端点は存在秘匿の 404）を満たす。
- **B（子 issue #2343）**: SC-19 のリンク作成 UI。
  - 案 3（画面から作った辺が SC-18 に出る）は B へ移した。
  - 補完候補の供給元（表題検索の口を新設するか、`/bff/search` で足りるとするか）の設計論点は、#2343 に書いた。

## 計画と後段（develop で確認）

- FR-17「利用者による明示的な付与」と ADR-0034 決定 8（作成時に到達可能性を検証し、権限外・不存在は 404）が根拠である。
- 後段 `GraphService` の `CreateGraphEdgeEndpoint`（`POST /graph/edges`）は、次のとおり実装済みである。
  - 入力検証は 400。
  - read スコープで両端を検証し、外れれば 404。
  - 型が辞書に無ければ 400 `unknown_edge_type`。
  - 起点の write スコープを検証し、外れれば 404。
  - 重複は 409 `edge_exists`（共有タグの辺なら利用者付与として引き取る）。
  - 作成できれば 201 と `GraphEdgeCreatedDto`。
- 🔴 **write ポリシーが 1 件も無い間、後段はこの経路を全件 404 にする**（deny-by-default。既知）。BFF はそれを変えない。

## 方針

1. `GraphBffEndpoints` に `g.MapPost("/edges", ...)` を足す。
   - 群の `RequireAuthorization()`（認証のみ）に乗せる。
   - 本文は契約の型 `CreateGraphEdgeRequest` で受け、そのまま後段へ送る。
   - 資格情報（Authorization）を伝播する。読み取りや承認と同じ方式 A である。
2. **後段の状態コードと本文を詰め替えない。** 201・400・404・409 を、本文ごと返す。承認・却下の `ForwardAsync` / `RelayAsync` と同じ作法である。
   - 404 を 403 へ変えない（存在秘匿。ADR-0034 決定 2）。
   - 後段へ到達できなければ 502 を返す。成功へ縮退しない。
3. **BFF に前段の write ゲートを置かない。** 理由は群の冒頭の注釈と同じである。後段が read と write のスコープを自分で解決するので、BFF に門を置くと応答が 403 と 404 に割れる。
4. 後段の 201 は `Location: /graph/edges/{id}` を持つ。これは後段の内部のパスなので、公開面へは出さない（`RelayAsync` は本文と型と状態コードだけを返す）。
5. `ForwardAsync` に任意の本文の引数を足す。却下は従前どおり本文を送らない（既存の試験が固定している）。
6. 新しい IADR は作らない。透過中継の作法は IADR-0300 と群の注釈の当てはめである。

## 母集合（規則 9・10）

- **規則 9**:
  - `git grep -n '"/edges'` と `git grep -n 'graph/edges'` で、BFF・フロント・openapi を走査した。BFF とフロントに経路は無い（生成物を除く）。後段の 1 か所だけである。
  - 文書側では「辺を作る口が公開 API に無い」と書いた箇所を走査した。見つかったのは `docs/screens/SC-19_private-notes.md:247`（「リンクを作る口が公開 API に無い」）である。
- **規則 10**:
  - SC-19 の画面仕様書の上の行は、本変更で口が開くので誤りになる。「口は開いた。UI は #2343」へ改める。
  - `GraphBffEndpoints` の群の注釈「書き込みは承認・却下だけ」に当たる記述があれば改める（走査した結果は実装中の追記に書く）。

## 受け入れ基準 → 試験（`BffGraphEdgeCreateTests`）

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| BFF 経由で辺を作れ、201 と本文が透過し、資格情報と要求の本文が後段へ届く | `Create_forwards_credentials_and_body_and_relays_201` | 赤（404：経路が無い） |
| 後段の 400（`unknown_edge_type`）が本文ごと透過する | `Backend_400_passes_through_with_its_body` | 赤 |
| 権限外の端点（後段の 404）が 404 のまま返る（403 にしない） | `Backend_404_is_relayed_verbatim` | 赤 |
| 409 `edge_exists` が本文ごと透過する | `Backend_409_passes_through_with_its_body` | 赤 |
| 未認証は BFF の入口で 401 になり、後段へ行かない | `Unauthenticated_is_401` | 赤（経路が無いので 404） |
| 後段へ到達できないときは 502（成功へ縮退しない） | `Backend_unreachable_is_502` | 赤 |

## 実装中の追記（2026-10-30）

- **作法の例外**: 群の注釈には「消費者の無い書き込み口を先に公開面へ出さない」（#952 → #962 の教訓）とある。A は UI より先に口を開けるので、この例外に当たる。コーディネータの承認を受けて、根拠 3 つを経路の注釈と PR 本文に書いた。
  1. #2107 の受け入れ基準の案 1・2 が、口の公開そのものを求めている。
  2. write ポリシーが無い間、後段は全件 404 を返すので、先に開けても実害が出ない。
  3. 消費者は #2343 である。
- **規則 10 の走査結果**: 群の注釈に「書き込みは承認・却下だけ」と言い切った箇所は無かった。ただし `ForwardAsync` の注釈「承認・却下は本文を送らない」は、本文の引数を足したので書き足した。
- **openapi**: `GraphEdgeCreatedDto` の description が YAML のコロンで壊れ、orval が失敗した。引用符で囲んで直した。生成物は `git checkout` で develop の版に戻してから、`pnpm codegen` で再生成した。
