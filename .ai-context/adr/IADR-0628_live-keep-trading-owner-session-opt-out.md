---
title: IADR-0628 live の teardown は E2E_KEEP_SESSIONS=trading-owner のときだけ取引オーナーのセッションを失効させずに 0600 で残す（他の役割は必ず失効）
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0032, IADR-0624, IADR-0625]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション）
related_specs:
  - ../specs/20261025_2231_live-keep-owner-session.md
---

# IADR-0628: live の teardown の取引オーナーだけの opt-out（#2231）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2231。AST の E-33＝AST#1416 の前提）

## 起点・関連

- 起点 issue: #2231。関連: [IADR-0624](./IADR-0624_live-playwright-config-setup-and-readiness.md)（setup・teardown）・[IADR-0625](./IADR-0625_e2e-live-workflow-disposable-stack.md)（e2e-live）。
  AST 側は AST/IADR-0588（AST の live 層は MSP の取引オーナーの state を読み、自分の teardown で失効させる）。
- 作業仕様書: [20261025_2231_live-keep-owner-session](../specs/20261025_2231_live-keep-owner-session.md)。

## コンテキストと課題

AST の画面は MSP の SPA の `/trading/*` にあり、試験アカウント `e2e-trading-owner` のログインは MSP の live の setup が行う（#2013 コメント 3 §2.3）。
IADR-0624 の teardown は実行の終わりに 6 役割すべてを失効させ state を消すので、AST の別の実行は取引オーナーの state を読めない。

## 決定

1. 環境変数 **`E2E_KEEP_SESSIONS`** を足す。値域は **`trading-owner` だけ**（役割を明示させる）。値域外（`1`・`all`・他の役割・カンマ区切り）と
   `E2E_LIVE=1` 無しの指定は、config の読み込みで例外にする。全部を残す指定は作らない。
2. 指定した役割は、teardown が失効とファイルの削除を飛ばし、state を **0600** にして残す。**他の 5 役割は従来どおり必ず失効させる**（失効できなければ赤）。
   setup は全役割の state を書いた直後に 0600 にする（cookie を持つファイル）。
3. **既定（未設定）は従来どおり全部失効。** `e2e-live.yml` は使わない（L1 で固定）。成果物の走査の門は変えない。
4. 残したセッションの後始末は AST の live の teardown が引き受ける（成否によらず失効させ、state を消す。失効できなければ exit 1）。

## 理由

- AST がログインを写すと、ログイン・秘密の入力・TOTP の部品が 2 か所に分かれる（AST/IADR-0588 の選択肢 B）。残すセッションを 1 役割に限れば、
  巻き添えの範囲（cookie が手元に残る時間）は AST の 1 回の実行の間だけになる。

## 影響

- 常設の PoC で AST の live を流すときの順は、作業仕様書と AST の作業仕様書（20261025_1416）の「流す順」。

## 残余リスク

- MSP の setup を opt-out つきで流した後に AST の live を流さないと、取引オーナーのセッションが寿命（30 日）まで残る（state は 0600 で手元に残る）。
  手順は「MSP の setup → AST の live」を続けて流す形にしている。
