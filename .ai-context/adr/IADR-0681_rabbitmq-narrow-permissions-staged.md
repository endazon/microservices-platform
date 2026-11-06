---
title: IADR-0681 ブローカのサービスごとのユーザーの権限は、コード（Wolverine の配線）から導いたキューと exchange の名前の正規表現へ絞る。段 1 は報告だけ、段 2 で明示して当てる（#2412）
type: impl-adr
status: Accepted
related_ids: [NFR-18, IADR-0573, IADR-0577, IADR-0591]
author: claude
created: 2026-11-05
updated: 2026-11-07
related_specs:
  - ../specs/20261105_2412_rabbitmq-narrow-permissions.md
---

# IADR-0681: ブローカの権限を使う名前へ絞る（段 1 は報告・段 2 で当てる。#2412）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（#2412。コーディネーターの指示 2026-11-05）

## 起点・関連

- 起点 issue: #2412。
- 前提:
  - IADR-0573（サービスごとのユーザー・単一情報源 users.txt・vhost は 1 つ）。
  - IADR-0577（基盤のサービスの切り替え・起動の直後の復元）。
  - 両者の §残る制約 1（権限は vhost 全体のまま。名前で絞るのは別の作業）を本 IADR が引き継ぐ。
- 作業仕様書: `.ai-context/specs/20261105_2412_rabbitmq-narrow-permissions.md`。

## コンテキスト

- 18 のユーザーは、どれも vhost 全体の configure / write / read を持つ。どのサービスも、他のサービスのキューを読め、どの exchange へも発行でき、宣言を消せる。
- 名前の導出は 2 系統ある。
  - 基盤: 明示の配線。キューは `<app>.<キュー>`、exchange は型の短い名前。
  - 取引ユニット: Wolverine の規約ルーティング。キューは `<ServiceName>.<型名>` と `<キュー>_error`、exchange は型の完全名。
- 絞り方を誤ると、宣言・購読・発行が拒まれる。発行の拒否は Wolverine が成功で返すので、**黙って届かない**（実測。作業仕様書）。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| a. 名前を手で書いた表 | サービス・イベントの増減に追随できない。IADR-0573 が段 1 で採らなかった理由そのもの |
| **b. 配線から導く生成器 ＋ 生成物のスナップショット ＋ 段の切り替え（採用）** | 導出の規則の字面が変われば止まり、生成物と食い違えば試験が赤になる。切り替えは明示で、戻せる |
| c. サービスの接頭辞だけで絞る（exchange は `.*`） | キューの読み取りは隔てられるが、発行の偽装（他のサービスのイベントを出す）が残る |
| d. vhost を分ける | IADR-0573 でオーナー裁定により採らない |

## 決定

### 決定 1: 生成器が配線から導き、生成物を単一の値にする

- `scripts/gen-rabbitmq-permissions.js` が `deploy/local/rabbitmq-setup/permissions.txt`（1 行 = `<ユーザー> <configure> <write> <read>`）を作る。
- `--write` で書き、既定で照合し、`--explain` で導出を表示する。
- 導き方:
  - 基盤: `ServiceName`・`BindPlatformQueue<T>`（購読）・`RoutePlatformEvent<T>`（発行）。コメントは読まない。接頭辞が users.txt の app と違えば止める。
  - 取引ユニット:
    - 購読: Wolverine のハンドラ発見の規則（public の `*Handler` / `*Consumer` 型の `Handle`・`Consume` 系の第 1 引数）。ビルドした 11 サービスで Wolverine 自身の発見と全件一致することを確かめた。
    - 発行: そのサービスのコードがメッセージ型を作る形（`new T(`・`T x = new(`・戻り値の型）と、使う共有部品（`backend/Shared`）が作る型。過大側に倒す。
    - 読み漏れは 2 つの突き合わせで止める: 受ける型には発行元が要る、`pipeline.json` の発行と購読は宣言のサービスに要る。
  - 規則の字面（`QueueNameFor`・`DeadLetterQueueNameFor`・`PlatformQueueName`・`PlatformExchangeName`・規約ルーティング）が変われば止める。
- 正規表現は `^(…|…)$`・`\.`・`.*` だけで書く。awk（点検）・RabbitMQ（PCRE）・JS（試験）が同じに読める。

### 決定 2: 正規表現の中身

| 権限 | 入れる名前 |
| --- | --- |
| configure | 自分のキュー（接頭辞）・`wolverine.response.*`・`wolverine-dead-letter-queue`・購読と発行の exchange |
| write | 自分のキュー・`wolverine-dead-letter-queue`・`amq.default`・発行の exchange |
| read | 自分のキュー・`wolverine.response.*`・`wolverine-dead-letter-queue`・購読の exchange |

- 根拠は RabbitMQ の権限の表である。
  - 宣言は configure。`x-dead-letter-exchange` 付きの宣言は DLX への write。
  - 束ねはキューへの write と exchange の read。発行は exchange の write。購読はキューの read。
- `amq.default` を入れる理由: Wolverine はリスナーの停止のとき、受け取り済みの未処理のメッセージを既定の exchange でキューへ戻す。書けないと 1 通が消えた（実測）。
- ［2026-11-07 追記 / IADR-0691］本表の write の `amq.default` は、受信キューを持つ利用者だけに改めた（発行だけの利用者は戻しをしない）。応答キューの `wolverine.response.*` は、Wolverine の名付けの形（`wolverine.response.<Guid>`）だけに当てる形へ改めた。決定は IADR-0691 決定 1・2。

### 決定 3: 段に分ける

- `reconcile-users.sh` の `RABBITMQ_PERMISSIONS`。
  - `vhost`（既定・段 1）: 従前のまま。`--permissions-report` は読むだけで、差分とブローカに在るもの（キュー・exchange・束ね）に届くかを出す。届かなければ exit 1。
  - `narrow`（段 2）: 生成物の行を当てる。行の欠け・届かないものがあれば何も変えずに止める（押し切りは `RABBITMQ_PERMISSIONS_ALLOW_GAPS=1`）。
  - 戻すときは `vhost` で回し直す。
- 当てた段と生成物を ConfigMap `rabbitmq-permissions` に写す。起動の直後の復元は同じ段で作り直す。
  - 写すのは 2 回: 当てる前に `applying`、全ユーザーを当て終えてから確定の段。
  - 途中で失敗・中断すると `applying` が残り、復元は vhost 全体（広い側）で作り直す。絞る途中・広げる途中のどちらで止まっても、
    再起動の後は通信を止めない側に倒れる。確定させるには合わせを回し直す。
  - `narrow` で行の無いユーザーには権限を付けない（vhost 全体へ倒さない）。
  - ConfigMap が無い間は段 1。

### 決定 4: 試験で固定する

- `scripts/rabbitmq-permissions.test.js` が固定する:
  - スナップショット。
  - 欠け（全件の変異で赤）・隔て。
  - 導出の検出力。
  - 段 1 / 段 2 / 点検 / 復元の動き。
- CI は submodule を取るジョブ（static-checks-units）で `RABBITMQ_PERMISSIONS_REQUIRE_AST=1` を付けて回す。取引ユニットの行も照合する。

## 結果

- 段 2 では、各サービスは自分のキューと、自分が使う exchange にしか触れない。
  - 他のサービスのキューを読めない。
  - 他のサービスの発行の exchange へ出せない。
  - 他のサービスのキューを宣言・削除できない。
- 🔴 **ただし、他のサービスのキューへの直接の発行は残る。** write に `amq.default`（既定の exchange）を許すので、
  キューの名前を知っていれば、そのキューへ名前のある exchange を通さずに入れられる（RabbitMQ は既定の exchange の宛先を権限で分けない）。
  - 今の段で外せない理由: Wolverine はリスナーの停止のとき、受け取り済みの未処理のメッセージを既定の exchange でキューへ戻す。
    外すと戻しが拒まれ、メッセージが欠ける（実測。作業仕様書）。
  - 残る危険を補うもの:
    - 取引ユニットの発注の承認は、発行元の HMAC の印を発注執行が発注の手前で確かめる（AST#1538 でマージ済み。ただし段 1 は報告のみで、印の無い承認をまだ止めない）。
    - 段 2 の configure・read の隔て（他のサービスのキューを宣言・削除・購読できない）は、この経路に依らずに効く。
- 段 1 のままでも、利用者は点検で切り替えの可否を読める。

## 残る制約

1. `amq.default` への write は、名前を知るどのキューへも直接入れられる。RabbitMQ は既定の exchange の宛先を権限で分けない。消すには Wolverine の戻しを publish ではない形へ替える必要がある（上流の変更）。
1-a. **段 2 の後に検討する**: Wolverine のリスナーの戻しの経路を固定の exchange（サービスごとの名前のある exchange）に寄せ、
   `amq.default` の write を外せるか（Wolverine の設定か上流の変更が要る。外せれば上の直接の発行の経路が閉じる）。
2. 応答キュー `wolverine.response.*` は名前にサービスを含まず、どのサービスも読める（要求・応答は使っていない）。
   - ［2026-11-07 追記 / IADR-0691］上の 1 は受信キューを持つ利用者だけに狭まった（発行だけの利用者からは外した）。1・1-a の残りと、購読の exchange の configure（削除を含む）を外す道筋は IADR-0691 §残る制約 1・2 が引き継ぐ。
3. 取引ユニットの発行の導出は過大側の近似である。作るが出さない型にも write が付く。
4. 段 2 は稼働クラスタで当てていない。切り替えは利用者が点検（`--permissions-report`）の後に行う。手元の RabbitMQ 3.13 で、基盤の模型と取引ユニットの実アセンブリを使って拒否 0 件を確かめた。
5. `deploy/cloud/`（並行の作業）の写しは本 IADR の対象外。追随はコーディネーターへ渡した。
6. 取引ユニットの写しは MSP の submodule の pin で照合する。取引ユニットが配線を変えて pin が進んだら、`--write` で作り直す（照合が赤になる）。
