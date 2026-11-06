---
title: IADR-0691 ブローカの権限を利用者ごとの最小権限へ寄せる。既定の exchange への write は受信キューを持つ利用者だけに与え、応答キューは Guid の形の名前だけに当てる。購読先の exchange の configure は、購読側の宣言を passive に替えるまで残す
type: impl-adr
status: Accepted
related_ids: [NFR-18, IADR-0573, IADR-0577, IADR-0681, IADR-0689]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_rabbitmq-least-privilege.md
---

# IADR-0691: ブローカの権限を利用者ごとの最小権限へ寄せる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: claude（コーディネーターの指示 2026-11-07）

## 起点・関連

- 前提: IADR-0681（配線から導く生成器・段 1 / 段 2）。本 IADR は同 決定 2 の表の write と応答キューの行を改める。
- クラウドの写し: IADR-0689 決定 12（クラウドは段 2 だけ。取引ユニットの行を ConfigMap に写す）。写し方は変えない。
- 作業仕様書: `.ai-context/specs/20261107_rabbitmq-least-privilege.md`（Wolverine が要る操作の洗い出しと実ブローカの実測）。

## コンテキスト

- IADR-0681 決定 2 は、全利用者の write に既定の exchange（`amq.default`）を入れた。RabbitMQ は既定の exchange への発行に、宛先のキューの権限を求めない。
  このため、どの利用者も名前を知るキューへ直接入れられた。
- 同じ表は、configure に購読の exchange を入れた。RabbitMQ の configure は宣言と削除を区別しない。
- 応答キューは `wolverine.response.*`（接頭辞の後ろは任意）だった。
- Wolverine（6.24.5）の実装を読み、実ブローカ（RabbitMQ 3.13.7・基盤と同じイメージ）で確かめた（作業仕様書 §洗い出し・§実測）:
  - 既定の exchange への発行は、リスナーの戻し（`RabbitMqListener.RequeueAsync`。受け取り済みの未処理のメッセージを nack してから、
    自分のキューの送り手＝既定の exchange で送り直す）だけが使う。ping（`AutoPingListeners`）は既定で切れている。要求・応答はどちらのユニットも使っていない。
  - 受信キュー（リスナー）を持たない発行だけの利用者は、戻しをしない。
  - 購読側の Wolverine は、起動時に購読の exchange を passive でなく宣言する（基盤の `BindPlatformQueue` の `DeclareExchange`、取引ユニットの規約ルーティング。
    どちらも AutoProvision）。RabbitMQ は在る exchange の宣言し直しにも configure を求める。外すと購読側は起動できない（実測）。
  - 応答キューの名前は `wolverine.response.{Guid.NewGuid()}`（既定の書式）である。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| a. 既定の exchange の write を全員から外す | 受信キューを持つ利用者の戻しが拒まれ、メッセージが欠ける（IADR-0681 の実測）。採らない |
| **b. 既定の exchange の write を受信キューを持つ利用者だけに与える（採用）** | 発行だけの利用者（基盤 2・取引ユニット 4）から外れる。受信キューを持つ利用者の戻しは保つ |
| c. 購読の exchange の configure を外す | 購読側の起動時の宣言が拒まれ、起動できない（実測）。本 IADR では採らない（§残る制約 2） |
| **d. 応答キューを Guid の形の名前だけに当てる（採用）** | Wolverine の名付けと一致する。接頭辞をそろえた他の名前に当たらない |
| e. 受信キューの名前を型ごとの完全一致にする | 接頭辞は自分のサービスの名前空間であり、他の利用者の名前に当たらない（試験で固定済み）。得るものが小さいので採らない |

## 決定

### 決定 1: 既定の exchange への write は、受信キューを持つ利用者だけに与える

- 生成器の導出に `hasListener` を足す。
  - 基盤: `BindPlatformQueue<T>` か `ListenToPlatformQueue` / `ListenToRabbitQueue` の呼び出しがあれば真。
  - 取引ユニット: ハンドラの受ける型が 1 つ以上あれば真（規約ルーティングは受ける型ごとにリスナーを立てる）。
- 真の利用者だけ、write に `amq\.default` を入れる。要る操作の表（`requiredAccess`）も同じ条件で戻しを数える。
- 今の生成物で外れる利用者: `msp-document`・`msp-datasource`・`ast-backtest`・`ast-configuration`・`ast-information-collection`・`ast-report`。

### 決定 2: 応答キューは Guid の形の名前だけに当てる

- configure と read の `wolverine\.response\..*` を、`wolverine\.response\.` ＋ 小文字の 16 進 8-4-4-4-12 に替える。
- 点検（`reconcile-users.sh` の awk）が区間 `{n}` を読めない実装でも同じに読めるよう、文字クラス `[0-9a-f]` を並べて書く。
  試験の字句の許可（awk / RabbitMQ / JS で同じに読める）に `[0-9a-f]` だけを足す。

### 決定 3: 購読の exchange の configure は残し、範囲を試験で固定する

- configure に入る exchange は、購読と発行の exchange ちょうどに限る（使わない exchange の宣言・削除は拒む）。試験で固定する。
- 購読の exchange の configure を外すのは、購読側の宣言を passive に替える変更（§残る制約 2）の後に行う。

### 決定 4: 試験で固定する

- `scripts/rabbitmq-permissions.test.js` に足す:
  - 既定の exchange へ書けるのは受信キューを持つ利用者だけ。取引ユニットで外れる 4 利用者を名前で固定する。
  - 利用者ごとの陰性対照（拒まれるべき操作の表）: 受信キューの無い利用者の既定の exchange、使わない exchange の configure・read・write、
    応答キューの形でない名前、錨の外（前後に字を足した名前）。
  - 変異（規則を元に戻す）: 全員に既定の exchange・応答キューを `.*` へ戻す・全 exchange の configure・錨を外す。どれも陰性対照が赤になる。
  - 基盤の 2 行（購読する利用者と発行だけの利用者）の値を字面で固定する。

## 結果

- 段 2 で、発行だけの利用者は既定の exchange から他の利用者のキューへ入れられない（実ブローカで拒否を確かめた）。
- 応答キューの権限は、Wolverine が名付ける形の名前にだけ当たる。
- 段 1（既定 `vhost`）の挙動は変わらない。ローカルの段 2（`RABBITMQ_PERMISSIONS=narrow`）とクラウドの段 2 は、生成物の新しい行を当てる。
- 切り替えの経路（段 1 → 点検 → 段 2・ConfigMap の写し・起動の直後の復元）は変えない。

## 残る制約

1. 受信キューを持つ利用者は、既定の exchange への write を持ち続ける。名前を知るどのキューへも直接入れられる経路は、これらの利用者に残る。
   - 外すには、Wolverine の戻しを既定の exchange を使わない形へ替える必要がある（上流の変更か、受信を耐久の受信箱にして戻しを要らなくする変更）。
   - 受け手の側で発行元を確かめる仕組み（発注の承認の印と同じ形を、発注・統制の入力になる型へ広げる）は、取引ユニット側の作業である。
2. 購読する利用者は、購読の exchange の configure（＝削除も含む）を持ち続ける。
   - 外すには、両方のユニットで購読側の exchange の宣言を passive（または外部所有）に替え、exchange を誰が先に宣言するか
     （発行側か、運用の初期化か）を決める変更が要る。購読側が先に起動すると passive の宣言は存在しない exchange で失敗するので、
     初期化の順序の設計を伴う。
3. 応答キューは名前にサービスを含まず、どの利用者も他の利用者の応答キューを宣言・購読できる（要求・応答は使っていない。IADR-0681 §残る制約 2 と同じ）。
4. 受信キューの有無の導出は、生成器が読む配線の字面に依る。リスナーを立てる新しい配線の形を足したら、導出を直す（試験の陰性対照が、受信キューの無い利用者の戻しの欠けを
   検出するわけではない。実ブローカでの確かめは作業仕様書の範囲）。
