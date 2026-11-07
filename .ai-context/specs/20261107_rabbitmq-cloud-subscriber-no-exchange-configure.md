---
title: 作業仕様書 — クラウドのブローカの権限から購読する exchange の configure を外し、購読される exchange を起動の直後の初期化で宣言する
type: spec
status: done
related_ids: [NFR-18, IADR-0681, IADR-0689, IADR-0691, IADR-0694]
author: claude
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 作業仕様書 — クラウドの購読側から購読の exchange の configure を外す

> 本仕様書は、生成規則を変える前に作成した（着手 2026-11-07。develop `79c1039c5`）。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR-18（サービス間の通信の保護）。機能要求（FR）・ユースケース（UC）・画面（SC）: なし。
- 起点: 所有者の裁定（2026-11-07）「購読側の権限を、購読に要るもの（exchange の read と自分のキューへの束ね）だけへ絞る」。
- 関連 IADR: IADR-0691 決定 3・§残る制約 2（本作業が引き継ぐ）。本作業の決定は IADR-0694。取引ユニットの側は AST の IADR-0628。

## 対象範囲

- 対象: `scripts/gen-rabbitmq-permissions.js`（クラウドの写し・`exchanges.json`）、生成物 `deploy/cloud/infra/rabbitmq-permissions.yaml`、
  `deploy/cloud/infra/rabbitmq.yaml`（起動の直後の復元に宣言を 1 段足す）、`scripts/rabbitmq-permissions.test.js`、`deploy/cloud/README.md`（順序）、
  IADR-0694（新規）と IADR-0691（日付つきの追記）、索引。
- 対象外:
  - ローカルの生成物 `deploy/local/rabbitmq-setup/permissions.txt`（ローカルの購読側は従来どおり宣言する）。
  - 既定の exchange への write（受信キューを持つ利用者の戻し。IADR-0691 §残る制約 1）。
  - submodule（`src/ai-stock-trading`）のポインタ。生成は submodule の pin（`3141b4f`）を `AST_ROOT` で与えて行った。
  - `deploy/cloud/infra/keycloak.yaml`・`networkpolicy.yaml`・`vault/`（触らない）。

## 母集合（規則 9・10）

- 「購読の exchange の configure」を持つ箇所を走査した（`grep -rn "configure" scripts/gen-rabbitmq-permissions.js deploy/cloud deploy/local/rabbitmq-setup`）:
  生成規則（`patternsFor` の configure）・要る操作の表（`requiredAccess`）・生成物 2 つ・試験（`最小権限: exchange の configure ...`）。
  ローカルは対象外（上）。クラウドは `renderCloud` だけが写すので、そこで変える。
- `renderCloud` の呼び手: 生成器の `generate` と `scripts/deploy-cloud.test.js`（ローカルの行から作って生成物と突き合わせる）。
  どちらもローカルの行を渡すので、`cloudRow` を `renderCloud` の中で当てれば両方が新しい値になる（呼び手は変えない）。
- この変更で古くなる自分の記述: 生成器の configure の注記（「外すには…両方のユニットに要る」）・クラウドの生成物の見出し・
  IADR-0691 決定 3 と §残る制約 2・`deploy/cloud/README.md` の段 2 の表。いずれも更新した。

## 受け入れ基準

- [x] クラウドの写しの configure の exchange は、発行するもの（write に在る exchange）だけ。write・read はローカルの行と同じ。
- [x] クラウドの行は、ローカルの行から作った値と、導出から作った値（`patternsFor(d, { cloud: true })`）の両方と一致する。
- [x] 購読に要る操作（受動の宣言・束ね）と発行に要る操作は当たる。購読する（発行しない）exchange の configure は誰も持たない（陰性対照）。
  ローカルの行のまま写す変異で赤になる。
- [x] `exchanges.json` は購読される exchange の和（68 個）で、Wolverine の宣言と同じ形。exchange 以外を持ち込まない。
- [x] クラウドの復元は、利用者の権限を当てる前に `exchanges.json` を読み込む。表が無ければ読み込まず、利用者の復元は続ける。
- [x] 読み込めないときは、stderr を 1 行に詰めて 200 字までに切って記録する（標準出力は捨てる。読み込むのは exchange の表だけ）。
- [x] ローカルの生成物は変わらない。

## 実測（2026-11-07。手元の RabbitMQ 3.13.7・基盤と同じ digest のイメージ。クラスタには触れていない）

- `rabbitmqctl import_definitions <exchanges だけのファイル>` は 2 回続けても成功し（冪等）、利用者・権限を変えない。
- 生成物の ConfigMap の値とクラウドの `restore-users.sh` をそのまま使って 2 利用者（購読の多い利用者と発行もする利用者）を復元した:
  exchange 68 個が宣言され、権限が当たった。購読側の利用者で、受動の宣言＝通る・非受動の宣言＝403・削除＝403・自分のキューの宣言と束ね＝通る。
  発行側の利用者で自分の exchange の非受動の宣言＝通る。
- 取引ユニットの購読側（受動の宣言）を、購読の exchange の configure の無い利用者で起動・受信できることは、AST の Integration E2E が確かめる
  （購読側を従来の宣言に戻すと 403 で起動に失敗する）。

## 適用の順序

1. 取引ユニットの該当の PR のマージと、取引ユニットのイメージの更新（values-cloud の `subscribedExchanges: Passive`）。
2. 本変更（ConfigMap `rabbitmq-permissions` と `rabbitmq-boot`）の適用とブローカの作り直し。

## 残余リスク

- IADR-0694 §残る制約 1・2。
