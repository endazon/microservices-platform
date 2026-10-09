---
title: 作業仕様書 — SC-12 の書き込みの門（check-mcp-client-provisioning --live）が、長い実走の途中の管理用トークンの期限切れで赤になるのを止める（#1835）
type: spec
status: done
related_ids: [FR-16, SC-12, ADR-0123, IADR-0516]
author: claude
created: 2026-10-09
updated: 2026-10-09
issue: "#1835"
---

# 作業仕様書 — SC-12 の門のトークンの取り直し（#1835）

> 本仕様書は修正の着手前に作成した（2026-10-09）。基点は MSP `origin/develop` `c66c5641`。判断は小さく、新しい IADR は起こさない（門の作法の是正であり、IADR-0516 の決定は変えない）。

## 事象

develop への push（`38f2e27f`、PR #1833 のマージ）で integration-stack の run 37861898017 が赤になった。失敗した段は「🔴 Gate — SC-12 の IdP への書き込み（#1817）」（`node scripts/check-mcp-client-provisioning.js --live`）。

- M1〜M7 は全部 ✓。M8 の前提（無効化の対象の登録が 201）も ✓。その直後の Keycloak の管理 API の照会 `GET clients?clientId=…` が **401** になり、例外で門が落ちた。片付けの GET もすべて 401 だった。
- 同じ門は、PR #1833 の head での dispatch（run 37860317986）と、直前の develop（`2f5d9c04` の push・schedule）では緑だった。#1833 の差分は起動器（Secret・Vault の初期投入・realm の後追い）だけで、この門の経路を変えていない。

## 原因

門は master の管理者のトークン（`admin-cli` の password grant）を、実走の最初に **1 度だけ** 取って最後まで使い回していた。master の realm のアクセストークンの既定の寿命は 60 秒である。M7 は照合の結果を最大 150 秒待つので、実走が長引くと M8 の時点で管理者のトークンが切れ、401 が出る。照合が早く名指せば待ちが短くなり期限内に収まる。だから同じコードで緑と赤が分かれた（時間に依存する）。登録者（platform の realm。寿命 300 秒）と `mcp-client-admin` のトークンも同じく 1 度だけ取っており、実走が長引けば同じ形で切れ得る。

"Flake" ではなく、門のトークンの扱いの欠陥である。

## 決定

- 管理者・登録者・`mcp-client-admin` の 3 つのトークンを **取り直せる形**（`bearerSource`）で持つ。
- 管理 API・入口の API への要求は、**401 なら 1 度だけ取り直して送り直す**（`sendWithRefresh`）。2 度目も 401 なら、その 401 をそのまま返す。本当に拒まれた要求を緑にしないためである。401 以外は送り直さない。
- トークンの発行そのものを見る M8 の判定（`tokenAttempt`）は対象外で、従来どおり 1 回の応答をそのまま判定する。
- 退けた案:
  - 実走の途中で定期的に取り直す: どこで期限が切れるかは実走の長さ次第で、取り直す場所の列挙が腐る。
  - master の realm の寿命を延ばす: 門の都合で認証基盤の設定を変えることになり、稼働の設定と食い違う。

## 母集合（規則 9・10）

`git grep -n "await token(" scripts/` で、トークンを 1 度取って使い回す live の門を走査した。

| 箇所 | 扱い |
| --- | --- |
| `check-mcp-client-provisioning.js` の管理者・登録者・`mcp-client-admin` | 本件で是正 |
| 他の live の門（`check-stack-ready.js` など） | 本件では触らない。実走が短く、同じ赤は観測されていない。同じ型の赤が出たら同じ形で直す（検査器・規約の追加は同型事故 2 回から） |

## 受け入れ基準と試験

- [x] 期限切れの 401 の後、取り直したトークンで送り直して通る（self-test）
- [x] 2 度目の 401 はそのまま返す。401 以外は送り直さない。取り直せないトークンは送り直さない（self-test）
- [x] トークンは取り直すまで同じ値を使い、取り直した後は新しい値を使う（self-test）
- [x] 変異「401 でも送り直さない」で self-test が赤になる（実施）
- [ ] integration-stack（dispatch）で門が緑になる
