---
title: 作業仕様書 — ブローカのサービスごとのユーザーの権限を、使うキューと exchange の名前へ絞る（段 1 は報告・段 2 で当てる。#2412）
type: spec
status: done
related_ids: [NFR-18, IADR-0573, IADR-0577, IADR-0674]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2412"
---

# 作業仕様書 — ブローカの権限の絞り込み（#2412）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-05。develop `b05b19b9`）。

## 事象

- サービスごとのユーザー（`deploy/local/rabbitmq-setup/users.txt` の 18 行）は、どれも vhost 全体（`'.*' '.*' '.*'`）の権限を持つ。
- IADR-0573 §残る制約 1 と IADR-0577 §残る制約 1 は、キューと exchange の名前で絞ることを「別の作業」とした。本仕様書はその作業である。

## 方針

1. **名前は記憶で書かない。** 生成器 `scripts/gen-rabbitmq-permissions.js` が Wolverine の配線から導き、生成物 `permissions.txt` に書く。
   - 基盤: 各サービスの `Program.cs` の `ServiceName`・`BindPlatformQueue<T>`・`RoutePlatformEvent<T>`。キューは `<app>.*`、exchange は型の短い名前。
   - 取引ユニット: submodule（無ければ `AST_ROOT`）の各 Worker。キューは `ai-stock-trading.<app>.*`（`_error` を含む）、exchange は型の完全名。
     - 購読は Wolverine のハンドラ発見の規則で読む。
     - 発行は「そのサービスのコード（と、使う共有部品）がメッセージ型を作る」で読み、過大側に倒す。
   - Wolverine の共通の名前も入れる: `wolverine-dead-letter-queue`・`wolverine.response.*`・`amq.default`。
   - 規則の字面（キュー名・デッドレター名・exchange 名の導出）が変わったら、生成器は止まる。
2. **段に分ける。** `reconcile-users.sh` の `RABBITMQ_PERMISSIONS` で段を選ぶ。
   - 段 1（既定 `vhost`）: 権限は従前のまま。`--permissions-report` は読むだけで、ユーザーごとの差分と、ブローカに在るものに絞った権限で届くかを出す。
   - 段 2（`narrow`）: 生成物の行を当てる。次のときは何も変えずに止まる。
     - 行の欠けたユーザーが居る。
     - ブローカに在るものに届かない（押し切りは `RABBITMQ_PERMISSIONS_ALLOW_GAPS=1`）。
   - 戻すときは `RABBITMQ_PERMISSIONS=vhost` で回し直す。
   - 当てた段と生成物は ConfigMap `rabbitmq-permissions` に写す。ブローカの起動の直後の復元（postStart）は同じ段で作り直す。
3. **試験で固定する**（`scripts/rabbitmq-permissions.test.js`）。
   - スナップショット（生成物がコードと一致する）。
   - 欠け（要る操作が当たる。変異で赤になる）と、隔て（他のサービスのキュー・発行しない exchange に当たらない）。
   - 導出の検出力・段 1 / 段 2 / 点検の動き・復元の動き。

## 受け入れ基準

- [x] 修正の前に、新しい試験が赤になる（生成器・生成物・段の切り替えが無いので落ちる）。
- [x] 生成物は 18 行で、`users.txt` とちょうど一致する。どの行も vhost 全体ではない。
- [x] 欠けの試験は、購読・発行の名前を 1 つ抜く変異の全件で赤になる。
- [x] 段 1（既定）の動きは従前と同じ（`.*`）。既存の合わせ・復元の試験はそのまま通る。
- [x] 段 2 は、行の欠けか届かないものがあると何も変えずに止まる。`--permissions-report` は何も書かない。
- [x] 実ブローカ（RabbitMQ 3.13・同じイメージ）で、段 1 → 点検 → 段 2 を回す。基盤の模型と取引ユニットの実アセンブリの配線・発行で、拒否は 0 件だった。
- [x] 取引ユニットの購読の導出は、Wolverine 自身のハンドラ発見（ビルドした 11 サービス）と全件一致する。

## 母集合（規則 9・10）

- **規則 9**: 走査の語は次のとおり。
  - `set_permissions`・`'.*' '.*' '.*'`・`rabbitmq-users`・`users.txt`・`rabbitmqctl`。
  - 範囲は `deploy`・`scripts`・`docs`・`.github/workflows`・`.ai-context/adr` とした。
  - 改める:
    - `deploy/local/rabbitmq-setup/reconcile-users.sh`（合わせ）。
    - `deploy/local/infra/rabbitmq.yaml` の `restore-users.sh`（起動の直後の復元）と ConfigMap のマウント。
  - 対象外とした理由:
    - `disable-default-user.sh`: 既定のユーザーを消すだけで、権限を付けない。
    - `deploy/docker-compose.yml`: compose の経路は IADR-0577 §残る制約 4 で対象外。
    - `deploy/cloud/`（並行の作業 MSP#2401 が新設中。develop に無い）: 写しの追随はコーディネーターへ渡す。
    - CI の `rabbitmqctl list_*`（読むだけ）。
- **規則 10**: 次の 2 つを本変更で改める。
  - IADR-0573 §残る制約 1 と IADR-0577 §残る制約 1 の「`.*` のまま」に、後継の注記を付ける。
  - `deploy/local/README.md` の合わせの段落に、段の切り替えを足す。
- **規則 11**: 窓を扱う是正ではないので当てない。

## 実測（2026-11-05・手元の RabbitMQ 3.13 コンテナ。稼働クラスタには触れていない）

- 応答キューは `wolverine.response.<uuid>`（auto-delete）で、サービス名を含まない。全サービスの configure / read に入れる。
- **既定の exchange（`amq.default`）への write が要る。**
  - Wolverine はリスナーの停止のとき、受け取り済みの未処理のメッセージを既定の exchange 経由でキューへ戻す。
  - 書けないと戻しが拒まれ、20 通中 1 通が消えた（2 回試して 2 回とも欠けた）。書ければ欠けない。
- 段 2 の権限では、他のサービスの exchange への発行は exchange の宣言で拒まれる。
  - 実測: backtest のユーザーで OrderApproved を出す。
  - 🔴 Wolverine は発行の呼び出しを成功で返す。発行の権限の欠けは起動を止めず、**黙って届かない**形で現れる。このため段 1 の点検と生成器の突き合わせを置く。

## 残余リスク

- `amq.default` への write は、名前を知っていればどのキューへも直接入れられる（RabbitMQ は既定の exchange の宛先を権限で分けない）。configure・read と、名前のある exchange の write は絞れている。
- 応答キュー（`wolverine.response.*`）は名前にサービスを含まないので、どのサービスも他のサービスの応答キューを読める（要求・応答は使っていない）。
- 取引ユニットの発行の導出は過大側の近似である。作る型に書き込みが付く。発行の読み漏れは、受ける型に発行元が要る・pipeline.json の発行の 2 つで止める。

## 検証

- 修正の前: `node scripts/rabbitmq-permissions.test.js` は生成器が無いので落ちた。
- 修正の後:
  - `node scripts/rabbitmq-permissions.test.js` は 17 件が合格した（AST の写しの有無の両方）。
  - `node scripts/gen-rabbitmq-permissions.js` は一致した（AST を照合した）。
  - 既存の `k8s-local-up.test.js`・`scripts.repo.test.js` は通った。
