---
title: IADR-0699 取引ユニットのブローカの利用者から共有のデッドレター（wolverine-dead-letter-queue）の権限を外す。デッドレターはサービスごと（<ServiceName>.dead-letter・<キュー>_error）で、各サービスが自分の接頭辞の configure で宣言する。生成器は AST の共通配線の字面と呼び出しを読んで導出し、無ければ止まる。取引ユニットのイメージの後に当てる
type: impl-adr
status: Accepted
related_ids: [NFR-18, IADR-0681, IADR-0691, IADR-0694, IADR-0689, AST:IADR-0636, AST:IADR-0129]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_broker-per-service-dead-letter.md
---

# IADR-0699: 取引ユニットのデッドレターをサービスごとにし、共有のデッドレターの権限を外す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制。ブローカの最小権限の一連 IADR-0681・IADR-0691・IADR-0694 と同じ）。
- 送り手: AST:IADR-0636（取引ユニットの共通配線が、Wolverine の transport の既定のデッドレターを `<ServiceName>.dead-letter` にする）。
- 作業仕様書: `.ai-context/specs/20261107_broker-per-service-dead-letter.md`。

## コンテキストと課題

クラウドのブローカの権限（`deploy/cloud/infra/rabbitmq-permissions.yaml`。生成物）は、取引ユニットの全 11 利用者に、共有のデッドレター
`wolverine-dead-letter-queue`（キューと同名の exchange）の configure・write・read を与えていた（IADR-0681 の導出。Wolverine が全サービスで
同じ名前の既定のデッドレターを宣言するため）。1 本を共有すると、ある利用者が他のサービスの死んだメッセージを読めるうえ、configure は
宣言と削除を区別しないので、共有のデッドレターを消せもする。

AST:IADR-0636 で、取引ユニットの既定のデッドレターはサービスごとの `<ServiceName>.dead-letter` になった（受信キューのデッドレターは
従来から `<キュー>_error`）。どちらも各利用者の接頭辞 `^<ServiceName>\..*$` の中にある。

## 決定

### 決定 1 — 取引ユニットの利用者には共有のデッドレターの権限を与えない

生成器（`scripts/gen-rabbitmq-permissions.js`）は、取引ユニットの利用者の configure・write・read から共有のデッドレターを外す
（ローカルの行・クラウドの写しとも）。各利用者のデッドレターは自分の接頭辞の中なので、接頭辞の 1 項で足りる（項を足さない）。

- 要る操作（`requiredAccess`）は、共有のデッドレターに代えて自分の `<ServiceName>.dead-letter` の宣言・束ね（configure・write・read）を数える。
- 基盤（MSP）自身のサービスの行（ローカルだけ）は共有のデッドレターのまま（基盤の Wolverine の配線は変えていない。下の残余）。
- クラウドの写しを作る `cloudRow` は、共有のデッドレターを持つ行を受け付けない（止まる）。

### 決定 2 — デッドレターはサービス自身が宣言する（定義で先に作らない）

各サービスは自分の接頭辞の configure を持ち、Wolverine の AutoProvision が起動のときに自分のデッドレター（キュー・exchange・束ね）を宣言する。
ブローカの起動の直後の初期化（`exchanges.json`）で先に作る案は採らない。

- デッドレターは購読の exchange と違い、宣言するのも使うのも同じ 1 サービスだけである。configure を他の利用者へ開く理由が無い
  （購読の exchange を定義で先に作ったのは、発行側と購読側の 2 者が同じ exchange を使い、購読側の configure を外すためだった。IADR-0694）。
- 定義で作ると、サービスの追加・改名のたびに定義の表も直す必要が生じ、Wolverine の宣言の形（引数）との食い違いで PRECONDITION_FAILED を招き得る。
- configure の範囲は自分の接頭辞だけ（自分の DLQ とキュー）。他のサービスのデッドレター・共有のデッドレターには当たらない（試験で固定する）。

### 決定 3 — 生成器は AST の共通配線から読み、無ければ止まる

生成器は AST の shim（`WolverineExtensions.cs`）に、名前の規則（`return $"{serviceName}.dead-letter";`）と、それを transport の既定の
デッドレターへ当てる呼び出し（`CustomizeDeadLetterQueueing(new DeadLetterQueue(ServiceDeadLetterQueueNameFor(serviceName)))`。コメントを除いて読む）が
在ることを確かめ、無ければ止まる。共有の名前を宣言する配線のまま権限だけを外すと、全サービスが起動できなくなるためである。
submodule（`src/ai-stock-trading`）はこの変更を含む AST の版へ進める。

### 決定 4 — 試験で固定するもの（`scripts/rabbitmq-permissions.test.js` §4-b）

- どの取引ユニットの利用者も、共有のデッドレターに configure・write・read のいずれも持たない（ローカルの行・クラウドの写しとも）。
- どの取引ユニットの利用者も、他のサービスのデッドレター（`<ServiceName>.dead-letter`・`<キュー>_error`）に権限を持たない。自分のは要る操作として当たる。
- 変異: 共有のデッドレターを戻す・他のサービスのデッドレターを足すと赤になる。共有のデッドレターを持つ行からクラウドの写しは作れない。
  shim の規則の字面・呼び出しを外す（コメントアウトを含む）と生成器が止まる。

## 結果

- 良い: 取引ユニットの利用者は、他のサービスの死んだメッセージを読めず、共有の受け皿を宣言・削除できない。
- 稼働中への影響と順序: **取引ユニットのイメージ（AST:IADR-0636 を含む）を全 11 サービスに当てた後に**、この権限の表を当ててブローカを作り直す
  （`deploy/cloud/README.md`「段 2 の切り替え」）。逆の順だと、旧のイメージの Pod は共有の名前の宣言が 403 で拒まれ起動できない。
  戻すときは、この表を前の版へ戻してブローカを作り直す（取引ユニットのイメージはそのままでよい）。
- 旧の共有のデッドレターのキューはブローカに残る。件数を読みだけで確かめ、空なら消す（取引ユニットの手順書 `docs/operations/broker-per-service-dead-letter-runbook.md`）。
- submodule を進めたことで、取引ユニットの develop の他の変更（契約のイベントの追加）も生成物に入る（クラウドの写しが現物の配線に追いつく）。

## 残余

- 基盤（MSP）自身のサービスはローカルで共有のデッドレターを使い続ける。クラウドのブローカに基盤のサービスの利用者は居ないので、クラウドの権限には現れない。
  基盤のサービスをクラウドのブローカに載せるときは、同じ形（サービスごとの名前）へ寄せる。
- 旧の共有の exchange（同名）は消さない（メッセージを持たず、権限を当てた後は取引ユニットの誰にも当たらない）。
