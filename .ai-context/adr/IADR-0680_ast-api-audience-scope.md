---
title: IADR-0680 取引ユニットの API の audience（ai-stock-trading-api）を専用のクライアントスコープで載せ、付ける先を bff と ai-stock-trading-owner に限る（platform-api を流用しない。#2418）
type: impl-adr
status: Accepted
related_ids: [NFR-09, NFR-18, IADR-0523]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2418_ast-api-audience-scope.md
---

# IADR-0680: 取引ユニットの API の audience のスコープ（#2418）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: コーディネーターの指示（2026-11-05）・claude（検査の形）

## 起点・関連

- 起点 issue: #2418。AST 側の検査は AST#1541。要求 **NFR-09**・**NFR-18**。
- 前提: IADR-0523（`platform-api` の audience と検査9）。

## コンテキストと課題

- AST の REST は、自分宛てのトークンだけを受けるため、AST 専用の audience を検査する。
- `platform-api` は platform のサービスの呼び出し元 15 件以上が持つ。これを AST の検査に使うと、platform の呼び出し元のトークンが AST の REST を通る。

## 検討した選択肢

1. **`platform-api` を流用する**: 上のとおり受け手の範囲が広すぎる。採らない。
2. **クライアントに写像を直付けする**: 範囲の検査を素通りしやすい（IADR-0523 の #1864 監査と同じ理由）。採らない。
3. **専用のスコープを作り、付ける先を検査で固定する（採用）**

## 決定

- スコープ `ai-stock-trading-api-audience`（`oidc-audience-mapper`・`included.custom.audience=ai-stock-trading-api`・access token だけ）。
- 付ける先は `bff`（利用者のトークンを AST へ中継する口）と `ai-stock-trading-owner`（AST の所有者の操作の主体）の既定のスコープだけ。
- `scripts/check-realm-constraints.js` の検査9 の 2 が、写像の形・写像の置き場所（このスコープの中だけ）・付ける先（ちょうど 2 つ。任意スコープ・realm の既定に置かない）を固定する。付ける先を足すときは、判断を記録して `AST_AUDIENCE_HOLDERS` へ加える。

## 結果

- 良い影響: AST の REST が受けるトークンの発行元を 2 つのクライアントに限れる。platform の呼び出し元のトークンは AST を通らない。
- 悪い影響: 付ける先を増やすたびに検査の表の更新が要る。

## 残余

1. AST 側の検査の有効化は AST のリポジトリ（AST#1541）。
