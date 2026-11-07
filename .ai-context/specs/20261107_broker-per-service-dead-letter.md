---
title: 作業仕様書 — 取引ユニットのブローカの利用者から共有のデッドレターの権限を外し、各利用者は自分のデッドレターだけを持つ
type: spec
status: done
related_ids: [NFR-18, IADR-0699, IADR-0681, IADR-0691, IADR-0694, AST:IADR-0636]
author: claude
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 作業仕様書 — 取引ユニットのデッドレターをサービスごとにする（基盤の権限の側）

> 本仕様書は、生成規則を変える前に作成した（着手 2026-11-07。develop `30588d467`。shallow ではない）。

## 起点

- 所有者の裁定（2026-11-07「全部直してから稼働」）。クラウドのブローカで、取引ユニットの全 11 利用者が共有のデッドレター
  `wolverine-dead-letter-queue` に configure・write・read を持つ（`deploy/cloud/infra/rabbitmq-permissions.yaml`）のを改める。
- 送り手は AST:IADR-0636（取引ユニットの既定のデッドレターを `<ServiceName>.dead-letter` にする）。決定は IADR-0699。

## 1. 現状

- 生成器 `scripts/gen-rabbitmq-permissions.js` の `patternsFor` は、全利用者の configure・write・read に共有のデッドレターを足す。
  `requiredAccess` は共有のデッドレターの宣言・束ねを全利用者の要る操作に数える。`cloudRow` は configure の先頭 3 項（接頭辞・応答キュー・共有の
  デッドレター）を前提に読む。`subscribedExchanges` は read の 4 項目以降を exchange とみなす。
- 取引ユニットの受信キューのデッドレターは `<キュー>_error`（接頭辞の中）。AST:IADR-0636 の後は既定のデッドレターも接頭辞の中。
- submodule（`src/ai-stock-trading`）は `3141b4f7` を指す。AST の develop はその後に契約のイベント（`OwnerSecondFactorChecked`）を足しており、
  AST の develop の配線から導くと、クラウドの写しはそのイベントの分も食い違っていた（`AST_ROOT` を develop の写しにして実測）。

## 2. 母集合（規則 9・10: 誤りの側の文字列で走査した）

`grep -rn "wolverine-dead-letter\|共有のデッドレター\|共有の DLQ"`（`.ai-context/`・CHANGELOG・submodule を除く）。

| 箇所 | 扱い |
| --- | --- |
| `scripts/gen-rabbitmq-permissions.js`（注記・`patternsFor`・`requiredAccess`・`cloudRow`・`subscribedExchanges`） | 取引ユニットの行から共有を外す。基盤の行は残す |
| `deploy/local/rabbitmq-setup/permissions.txt`・`deploy/cloud/infra/rabbitmq-permissions.yaml`（生成物） | `--write` で作り直す |
| `scripts/rabbitmq-permissions.test.js`（基盤の 1 行の字面・陰性対照・クラウドの写しの `.slice(3)`） | 基盤の字面は不変。`.slice(2)` へ。§4-b を足す |
| `deploy/cloud/README.md`「段 2 の切り替え」 | 適用の順序（取引ユニットのイメージの後）を足す |
| 基盤のサービスの DLQ を扱うもの（`DeadLetterMetrics.cs`・`WolverineExtensions.cs`・`alerts.yml`・`republish-document-updated.js`・`admin-page-driver.js`・`operations.md`・`cutover-discard-and-rebuild.md`・`scripts.repo.test.js` の再発行の試験） | 変えない（基盤のサービスは共有のまま。IADR-0699 の残余） |
| 既存の IADR-0681・IADR-0694 の本文 | 凍結記録のため書き換えない |

## 3. 作るもの

1. 生成器: 取引ユニットの導出に `serviceDeadLetter`（`<ServiceName>.dead-letter`）を持たせ、`patternsFor` で共有のデッドレターを外す。
   `requiredAccess` は自分のデッドレターを数える。`cloudRow` は共有のデッドレターを持つ行で止まる。AST の shim の規則の字面と
   `CustomizeDeadLetterQueueing` の呼び出しが無ければ止まる。
2. 生成物を作り直す。submodule を AST の変更の版へ進める。
3. 試験 §4-b（共有・他のサービスのデッドレターに権限が無い・自分のは当たる・変異・生成器の検出力）。
4. `deploy/cloud/README.md` に適用の順序。

## 4. 受け入れ基準

1. クラウドの写しとローカルの取引ユニットの行に、共有のデッドレターの名前が無い。どの取引ユニットの利用者も、共有のデッドレターと他のサービスの
   デッドレター（`<ServiceName>.dead-letter`・`<キュー>_error`）に configure・write・read のいずれも持たない。
2. 自分のデッドレター（`<ServiceName>.dead-letter`）の宣言・束ねは各利用者の configure・write・read に当たる（サービス自身が宣言する）。
3. 共有のデッドレターを戻す変異・他のサービスのデッドレターを足す変異で試験が赤になる。shim の配線を外すと生成器が止まる。
4. 基盤の行（ローカル）は変わらない。
5. `scripts.test.js` 全件・`deploy-cloud.test.js`・`rabbitmq-permissions.test.js`・文書の検査一式が通る。

## 5. 適用の順序

取引ユニットのイメージ（AST:IADR-0636）を全 11 サービスに当てる → 旧の共有のデッドレターの件数を読みだけで確かめる →
本変更の ConfigMap を当ててブローカを作り直す → 旧の共有のデッドレターは空なら消す（取引ユニットの手順書）。
逆の順だと、旧のイメージの Pod は共有の名前の宣言が 403 で拒まれ起動できない。

## 6. 範囲外・残り

- 基盤のサービス自身のデッドレター（ローカル）。
- submodule の指す版: PR の時点では AST の PR のブランチの先頭を指す。AST の PR が develop へ入った後、develop のその版へ指し直す（マージの前）。
