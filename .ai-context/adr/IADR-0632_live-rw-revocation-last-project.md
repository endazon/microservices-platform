---
title: IADR-0632 e2e-revocable を無効化して全セッションを失効させる試験は専用の project で、同じ実行の live-ro・live-rw の後に流し、後始末の有効化が戻らなければ赤にする
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-14, SC-17, ADR-0032, IADR-0626, IADR-0630]
author: claude
created: 2026-10-25
updated: 2026-10-25
related_specs:
  - ../specs/20261025_2033_live-rw-revocation.md
---

# IADR-0632: e2e-revocable の失効の試験を最後に流す（#2033）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2033 / E-53。条件はオーケストレーターの指示 2026-10-25: 失効の試験は他の試験が e2e-revocable の状態を使い終わってから流し、
  その順序を仕組みで固定する）

## 起点・関連

- 起点 issue: #2033（親 #2013 の網羅表 M-009・M-109）。関連: [IADR-0626](./IADR-0626_live-ro-auth-errors-on-revocable-fresh-session.md)（auth-errors は
  e2e-revocable で流す）・[IADR-0630](./IADR-0630_live-rw-disposable-stack-gate-and-run-id.md)（live-rw の門・rw は ro の後）。
- 作業仕様書: [20261025_2033_live-rw-revocation](../specs/20261025_2033_live-rw-revocation.md)。

## コンテキストと課題

SC-17 の無効化は「無効化と全セッション失効は 1 つの操作」（バックチャネルログアウトで BFF のセッションが subject 単位で消える）で、再有効化しても
セッションは戻らない。e2e-revocable は auth-errors（live-ro）と、E-54 のリセット申請・アカウントコンソール（live-rw）も使う。失効の試験がそれらと
並ぶ・先に流れると、それらのログインが無効の利用者に当たる。

## 決定

1. **専用の project `live-rw-revocation`**: `revocation.rw.spec.ts` は `live-rw` から外し（`testIgnore`）、`live-rw-revocation` で流す。
   `live-rw-revocation` は同じ実行で選んだ `live-ro`・`live-rw` に依存する（project の依存で順序を固定する）。e2e-revocable を使う他の試験は
   `live-ro`・`live-rw` に置く。project の組み立ては純関数（`e2e/live/support/projects.ts` の `liveProjects`）にし、L1 が依存を直接見る。
2. **e2e-revocable は新しくログインする**（setup の state は auth-errors のログアウトで失効済み）。OTP は次の周期を待ち、誤りは送らない。
3. 秘密を入力するので、このファイルは trace・スクリーンショット・動画を切る。
4. **後始末は有効化で、戻らなければ赤**: afterEach が API で e2e-revocable の `enabled` を確かめ、偽なら有効化を送り、もう一度読んで真でなければ
   落とす（`cleanup.ts` は失敗しても test を落とさないので使わない）。

## 理由

- 決定 1: spec のファイル名の順や `workers: 1` は順序を約束しない（Playwright は project の中のファイルを並べ替えうる）。project の依存は
  Playwright が保証する順序で、依存先が落ちれば失効の試験は流れない（無効の利用者を作ったまま他の試験へ進まない）。
- 決定 4: 無効のまま残ると、次の実行の auth-errors と setup が e2e-revocable でログインできない。使い捨てのスタックでは次の実行は新しいスタックだが、
  手元で同じスタックに流し直すときに効く。

## 影響

- e2e-revocable の状態を使う試験を足すときは `live-ro` か `live-rw` に置く（`live-rw-revocation` には置かない）。
- teardown は e2e-revocable の `/bff/auth/me` が 401 なら失効済みとして数える（失効 6 のまま）。
