---
title: IADR-0693 クラウドのブローカの権限から、購読する（自分が発行しない）exchange の configure を外す。購読される exchange はブローカの起動の直後の初期化が、利用者の権限を当てる前に宣言する。ローカルの権限は変えない
type: impl-adr
status: Accepted
related_ids: [NFR-18, IADR-0681, IADR-0689, IADR-0691, AST/IADR-0628]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_rabbitmq-cloud-subscriber-no-exchange-configure.md
---

# IADR-0693: クラウドの購読側から購読の exchange の configure を外す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07: 購読側の権限を購読に要るものだけへ絞る）／ claude（起案・実装）

## 起点・関連

- 前提: IADR-0681（配線から導く生成器）・IADR-0689 決定 12（クラウドの写し）・IADR-0691（決定 3・§残る制約 2）。
- 取引ユニットの側: AST の IADR-0628（クラウドでは購読側が購読の exchange を受動で宣言する。values-cloud の `subscribedExchanges: Passive`）。
- 作業仕様書: `.ai-context/specs/20261107_rabbitmq-cloud-subscriber-no-exchange-configure.md`。

## コンテキスト

- IADR-0691 決定 3 は、購読の exchange の configure を「購読側の宣言を受動に替えるまで」残した。購読側の Wolverine が起動時に非受動で宣言し、
  RabbitMQ は在る exchange の非受動の宣言にも configure を求めるためである。configure は削除も許す。
- 取引ユニットは、クラウドでは購読の exchange を受動で宣言する形に替えた（AST の IADR-0628）。購読に要るのは exchange の read（`queue.bind`）と自分のキューの権限になる。
- 受動の宣言は exchange が無いと失敗する。発行だけの型の exchange は発行側の 1 通目の発行で作られるので、誰かが先に宣言する必要がある。
- 基盤のサービスはクラウドでブローカを使わない。ローカル（段 1・段 2）の基盤と取引ユニットは、従来どおり購読側も宣言する。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| a. ローカルの行も含めて購読の exchange の configure を外す | ローカルの購読側（基盤の `BindPlatformQueue`・取引ユニットの既定）は非受動で宣言するので、段 2 のローカルで起動できない。採らない |
| **b. クラウドの写しだけ外す（採用）** | クラウドの購読側は受動で宣言する。ローカルは変えない |
| c. exchange の宣言を購読側の再試行だけに任せる | 稀にしか発行しない型の購読側が、1 通目の発行まで起動の失敗と再起動を繰り返す。保険としてだけ残る（AST の IADR-0628） |
| **d. ブローカの起動の直後の初期化が、購読される exchange を定義ファイルから宣言する（採用）** | 生成器は購読される exchange を導いている。初期化は利用者の権限を当てる前に走るので、購読側が繋がる時点で exchange は在る |
| e. 定義ファイルをブローカの起動時の読み込み（`load_definitions`）で与える | 利用者・権限も定義ファイルが持つ前提になり、既定のユーザーの扱い（IADR-0577）と衝突する。採らない |

## 決定

### 決定 1: クラウドの写しの configure の exchange は、発行するものだけにする

- 生成器の `cloudRow`（ローカルの行から作る）: configure の exchange のうち write に在るもの（＝発行する exchange）だけを残す。write・read は変えない。
  行だけから作るので、AST の写しが無い checkout でも照合できる。導出からの値（`patternsFor(d, { cloud: true })`）との一致は試験が固定する。
- 要る操作の表（`requiredAccess(d, { cloud: true })`）は、購読の exchange の configure を数えない（受動の宣言は configure を要らない）。
- ローカルの生成物（`deploy/local/rabbitmq-setup/permissions.txt`）は変えない。

### 決定 2: 購読される exchange は、ブローカの起動の直後の初期化が利用者の権限を当てる前に宣言する

- 生成器は、クラウドの行の read の exchange の和を `exchanges.json`（ConfigMap `rabbitmq-permissions` の同じ表の別のキー）に書く。
  形は Wolverine の AutoProvision の宣言と同じ（fanout・durable・自動削除しない・内部でない・引数なし。違うと発行側の宣言が拒まれる）。vhost は `/`。
- `deploy/cloud/infra/rabbitmq.yaml` の `restore-users.sh` は、利用者の復元の前に `rabbitmqctl import_definitions` で読み込む。読み込みは追加だけで、
  在る exchange・利用者・権限を変えない（冪等。実ブローカで確かめた）。読み込めなくても利用者の復元は続ける（購読側は起動の再試行で待つ）。

### 決定 3: 適用の順序

- 取引ユニットの該当の変更（AST の IADR-0628）のマージと、取引ユニットのイメージの更新の後に、本変更を当てる。先に当てると、
  非受動で宣言する購読側が起動できない（実ブローカで 403 を確かめた）。手順は `deploy/cloud/README.md` に書く。

## 結果

- クラウドの購読側の利用者は、購読する（自分が発行しない）exchange を宣言も削除もできない（実ブローカで 403）。受動の確かめ・自分のキューの宣言と束ねは通る。
- 発行側は自分の exchange の configure を持ち続ける（宣言し続ける）。
- ローカルの段 1・段 2 は変わらない。
- 既定の exchange への write は、受信キューを持つ利用者に残る（IADR-0691 §残る制約 1）。その経路で届くメッセージは、受け手が発行元の印を確かめる仕組み（AST の IADR-0627）で扱う。

## 残る制約

1. 取引ユニットの型を足したら、生成器の再生成で `exchanges.json` も更新する（照合が止める）。ブローカの起動の後に足した型の exchange は、
   次のブローカの起動まで初期化では宣言されない（その間は発行側の 1 通目の発行か、購読側の再試行に依る）。
2. 定義ファイルの vhost は `/` に固定する（クラウドの vhost は 1 つ）。vhost を分けるなら生成器と復元を直す。
