---
title: 作業仕様書 — バックアップの回は書き始める前に依存先（Postgres・OpenBao・名前解決）の起動を上限つきで待ち、PC の復帰の直後の後追いの回を通す（#2394）
type: spec
status: done
related_ids: [NFR-21, NFR-05, ADR-0002, ADR-0008, IADR-0471, IADR-0548, IADR-0669, IADR-0670]
author: claude
created: 2026-11-04
updated: 2026-11-04
issue: "#2394"
---

# 作業仕様書 — 後追いの回が依存先の起動を待つ（#2394）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-04。develop `20fef6c8`）。

## 事象（issue の記録。稼働 PoC・2026-11-04）

- PC のスリープ（07:51〜18:38 JST）で、12:00 の回を取り逃した。復帰の直後に、CronJob が後追いの回を起こした。
- 依存先は、まだ立ち上がっていなかった（Postgres は 8 回目・OpenBao は 22 回目の再起動の直後）。
  - postgres の回: `could not translate host name "postgres"` → `DB の一覧を取れません`。
  - vault の回: `秘匿管理へ接続できません`。
- `backoffLimit: 0` なので、その 1 回の失敗で後追いは終わった。fail-closed なので、壊れた回は書かれていない。

## 方針（IADR-0670）

- **書き始める前に、依存先が揃うのを上限つきで待つ**（スクリプトの側。`backoffLimit: 0` は変えない）。
  - postgres の回: `pg_isready` が 0 を返すまで待つ。PGHOST の名前解決と、接続の受け付けを含む。
  - vault の回: `GET /v1/sys/health` が 200（初期化済み・unseal 済み・active）を返すまで待つ。名前解決と TCP の接続を含む。既存の `/dev/tcp` の HTTP を使う。
  - 上限は `BACKUP_DEPS_WAIT_SECONDS`（既定 600 秒）、間隔は `BACKUP_DEPS_WAIT_INTERVAL`（既定 5 秒）。
  - 待つのは、受取人と age の検査の後で、staging を作る前である。上限を過ぎたら、**何も作らず・何も書かずに失敗する**（fail-closed）。
- **再試行はしない**（`backoffLimit: 0` のまま）。
  - 待つのは、書き始める前の 1 か所だけである。書き込みの途中の失敗（`pg_dump` の失敗・保管先への書き込みの失敗）を、やり直さない。
  - `podFailurePolicy` で「届かない失敗だけを再試行する」案は採らない。
    - 終了コードで失敗を分けると、分け方の誤りがそのまま上書きの再試行になる。
    - Pod を作り直すたびに、後追いの締切（`startingDeadlineSeconds`）とは別の時間が過ぎる。
    - 待つのも、1 本の Pod の中で上限を持てば足りる。
- **時間の重なり**: 依存先の待ち（600）＋ロックの待ち（900。IADR-0669）＋作業の余裕（300）が、Job の `activeDeadlineSeconds`（postgres 3600・vault 1800）を超えないこと。これを試験で固定する。
- runbook の「失敗したときの分岐」に、待ち切れたときの行を足す。

## 受け入れ基準（issue の 1〜3 の写像）

- [x] 1: 依存先が初めは立ち上がっておらず、待つうちに立ち上がると、その回は成功する（postgres・vault の両方）。
- [x] 2: 依存先が上限までに立ち上がらなければ、何も書かずに失敗する（staging も保管先も空。生成器を呼ばない）。
- [x] 3: 書き込みの途中の失敗を再試行しない（`pg_dump` の失敗した DB を 2 度呼ばない・CronJob の `backoffLimit: 0` を保つ）。
- [x] 待ちの上限とロックの待ちの合計が、`activeDeadlineSeconds` の内側に収まる（試験で固定する）。
- [x] 修正の前に、上の試験が赤になる。

## 母集合（規則 9・10）

- **規則 9**: 走査の語は「`backoffLimit`」「`startingDeadlineSeconds`」「`activeDeadlineSeconds`」「後追い」「取りこぼし」「接続できません」「DB の一覧を取れません」である。範囲は `deploy/local/platform-backup`・`docs/operations`・`scripts`・`.ai-context/adr` とした。
  - 改める:
    - `backup.sh`（冒頭の手順の説明・main・待ちの関数）。
    - `backup.test.sh`。
    - runbook（失敗したときの分岐・何がどこに置かれるかの後追いの説明）。
    - IADR-0471（日付つき追記。スケジュール）。
    - `cronjob.yaml` の冒頭の注記（後追いの回が依存先を待つこと）。
  - 対象外:
    - `scripts/backup-restore-drill.sh`: 運用者が手で流すもので、後追いは無い。
    - `scripts/platform-backup.test.js`: マニフェストの値を変えない。
- **規則 10**: runbook の「PC を止めていて取りこぼした回は、22:00 JST までにクラスタが戻れば後追いで走る」は、依存先を待つことを足して正しくなる。

## 残余リスク

- 依存先が 10 分を超えて立ち上がらない日は、従前どおり失敗として残る（警報は無い。runbook §2 の確認）。

## 検証（2026-11-04）

- 修正の前（develop `20fef6c8` の `backup.sh`）に、追加の試験を流した。13 件が赤だった（T-2394-01・02・04・05・06）。
  - T-2394-03（再試行しない）は、修正の前から緑である。退行の止めとして置いた。
- 修正の後、`backup.test.sh` は 264 件が合格した（3 回続けて流して同じ。1 回 約 26 秒）。
- 着手後に分かったこと: 既存の T-1868-04・05（ServiceAccount のトークンが無い・形が違うなら秘匿管理へ要求を送らない）が、待ちの sys/health で赤になった。
  - ServiceAccount のトークンの検査（`check_vault_login_inputs`）を、待つ前へ移して直した。
  - 既存の T-1868-06（届かないことをログに出す）のために、待ち切れたときに最後の確かめの理由を 1 度だけ出すようにした。

## レビューの指摘への対応（#2395・2026-11-04）

- 🟡1（1 回の確かめに時間制限が無い）を直した。
  - `BACKUP_DEPS_PROBE_TIMEOUT`（既定 10 秒）で、子のサブシェルと見張りで止める。`pg_isready` は直に exec させ、`-t` も同じ値にする。
  - 試験 T-2394-07（Postgres）と T-2394-08（秘匿管理）を足した。修正の前は赤だった（それぞれ 20 秒以上かかった）。
  - 着手後に分かったこと: 止めた確かめの子（`sleep`）が残ると、試験の出力のパイプを握って戻らない。本物の `pg_isready` は 1 つのプロセスなので、スタブも `exec sleep` にした。理由の確かめ（`deps_reason`）も、`pg_isready` を直に包む形にした。
- 🟡2（vault の余りが 0）: 数え直すと、ロックの待ちは保管先ごとに起きる（2 × 900）。vault の 1800 には収まらなかった。
  - vault の `activeDeadlineSeconds` を 3600 へ上げた（余り 885 秒）。
  - 試験 T-2394-06 を「最悪の合計 < 時間切れ」に厳しくした。修正の前は vault で赤だった。
- 🟢: `deps_reason` の接頭辞の直書きを、`err` に改めた。
- 修正の後、`backup.test.sh` は 274 件が合格した（3 回続けて流して同じ。1 回 約 30 秒。止めた確かめの子は残らない）。

