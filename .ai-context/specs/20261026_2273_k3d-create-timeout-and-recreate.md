---
title: 作業仕様書 — 起動器の k3d クラスタの作成に短い上限・診断・1 回の作り直しを入れ、ノードの Ready を明示で待つ（#2273）
type: spec
status: done
related_ids: [NFR, IADR-0642]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2273"
---

# 作業仕様書 — k3d クラスタの作成の上限・診断・作り直し（#2273）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。依頼はコーディネーター（2026-10-26）。

## 事象（issue の記録。同型の事故 2 回目）

| 日時 (UTC) | ワークフロー | 止まった箇所 | 結果 |
| --- | --- | --- | --- |
| 2026-10-25 14:45 | integration-stack（run 40203706433） | `k3d cluster create` の `Starting node 'k3d-integration-stack-agent-0'` から 30 分無出力 | ステップ上限 30 分で打ち切り（#2257） |
| 2026-10-25（#2261 の CI） | e2e-live | flannel の `subnet.env` が無く、ノードが Ready にならない | ランナーが停止。再実行 1 回で緑 |

- どちらもコードの差分と無関係で、起きると 30 分を無言で使い切り、原因の手がかりがログに残らない。

## 現状（実測）

- `scripts/k8s-local-up.sh` の [1/7] は、`k3d cluster list` で無ければ `k3d cluster create "$CLUSTER" "${CREATE_ARGS[@]}"` を素で呼ぶ。上限も診断も無い。
- ノードの Ready は明示で待っていない（k3d の既定の `--wait` は server の起動を待つだけで、上限は 0＝無制限）。
- ワークフローは起動器を呼ぶだけで、上限はステップの `timeout-minutes`（integration-stack・e2e-live は 30 分）だけである。

## 方針

- 作成の 1 試行を「`k3d cluster create`（上限つき）→ 全ノードの Ready 待ち（上限つき）」とする。
  - 作成の上限は `K3D_CREATE_TIMEOUT`（秒。既定 300）。診断の 1 コマンドの上限は `K3D_DIAG_TIMEOUT`（秒。既定 20）。coreutils の `timeout` で包む。`timeout` が無い環境（macOS の素の環境）は包まずに warn を出す。
  - Ready 待ちは `kubectl wait --for=condition=Ready node --all --timeout=<K3D_NODE_READY_TIMEOUT>s`（既定 180）。
- 試行が失敗したら、診断を出す。
  - `docker ps -a`・`docker info`・ノードのコンテナ（`k3d-<cluster>-server-0`・`k3d-<cluster>-agent-0`）の `docker logs --tail 200`。
  - 各ノードのコンテナで `/run/flannel/subnet.env` の有無を見て、無ければ名指しで出す。
  - kubeconfig が出来ていれば `kubectl get nodes -o wide`。
- 診断の後、**この実行が作ったクラスタだけを** `k3d cluster delete` で消し、1 回だけ作り直す。
  - 既存のクラスタ（reuse の経路。PoC を含む）は消さない・作り直さない。作成の経路に入ったとき（`k3d cluster list` で無かったとき）だけ働く。
  - 2 回目も失敗したら、診断を出して止まる（exit 1）。作り直しは 1 回まで。
- 作り直しは見えるようにする。
  - 起動器の出力に `k3d recreate count: 1` を出す。
  - GitHub Actions の下（`GITHUB_ACTIONS=true`）では `::warning title=k3d cluster recreated::…` の注記を出し、`GITHUB_STEP_SUMMARY` があればそこへも 1 行足す。
  - 作り直しは試験の skip ではない。作り直した後のスタックで全ての門が同じように走る。
- 試験に時間による救済（再試行・待ちの延長）は入れない。作り直しは起動器のクラスタの作成だけに限る。
- ワークフローは変えない（起動器が持つ）。各ステップの `timeout-minutes`（30 分）は、2 試行の最長（(300 + 180) × 2 秒＝ 16 分）と診断に足りる。

## 受け入れ基準

- [x] 既定で、作成は 1 回だけ呼ばれ、上限（300 秒）と Ready 待ち（180 秒）の値が出力に出る。作り直しの数は 0
- [x] 作成が失敗した（非 0）とき、診断（`docker ps -a`・`docker info`・2 ノードの `docker logs`）を出し、この実行のクラスタを消して 1 回だけ作り直す。作り直しの数 1 と注記を出し、起動は続く
- [x] 作成が上限を超えたとき（試験は上限 1 秒と止まる k3d のスタブで再現）、上限超過として同じく 1 回作り直す
- [x] ノードが Ready にならないとき、同じく診断を出して 1 回作り直す。flannel の `subnet.env` が無いノードを名指しで出す
- [x] 2 回とも失敗したら、作り直しは 1 回のまま止まる（exit 非 0。3 回目の作成を呼ばない）
- [x] 既存のクラスタ（reuse）では、作成も削除も Ready 待ちも呼ばない
- [x] 上限は env で変えられ、数値でない値は起動前に止める
- [x] ［監査の指摘］診断の 1 コマンドごとに上限（`K3D_DIAG_TIMEOUT`・既定 20 秒）を掛ける。docker が固まるスタブで、診断が上限で終わり作り直しへ進むことを確かめる
- [x] ［監査の指摘］上限の値の検査は k3d の経路で、クラスタに触る前に行う。Rancher Desktop の経路では止めない
- [x] ［監査の指摘］Ready の前に、同じ上限の中で期待する数（server 1 ＋ agents）のノードが見えるまで待つ。最初の数回が 0 件・1 件でも作り直さない。揃わなければその試行は失敗する

## 母集合（規則 9: 誤りの側の文字列で全文を走査した）

`git grep -n "k3d cluster create\|k3d cluster delete"`（submodule・`.ai-context/` は除く）。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `scripts/k8s-local-up.sh` の `k3d cluster create` | **直す** | 本件 |
| `.github/workflows/` 3 本の後始末の `k3d cluster delete` | 外す | 後始末であり、作成の経路ではない |
| `scripts/k8s-local-down.sh`（とその試験） | 外す | 利用者の明示の削除。作成と無関係 |
| `deploy/local/*/README.md` の `k3d cluster delete msp-ast-dev` | 外す | 利用者の手順 |
| `docs/operations/operations.md` の母集合の grep の式 | 外す | イメージの母集合の引き方で、作成の振る舞いを述べていない |
| `deploy/local/README.md` の起動器の説明 | **直す**（上限・作り直しの env を足す） | 利用者が上限を変える口 |
| `scripts/live-scripts.json`（稼働クラスタへ当たる scripts の分類） | **直す**（`lib/k3d-create.sh` を offline へ） | 新しい lib は分類が要る（`scripts.repo.test.js` の #1550 節） |
| `.github/workflows/e2e-live.yml` の `paths` | 外す（触らない） | `scripts/lib/**` を既に含む |

## 規則 11（窓）

- 作成の上限・Ready の上限は時間の窓である。ただし是正の対象は「片方の端が来ないこと（無限に待つ）」で、増える側・減る側の 2 つの量を突き合わせる形ではない。上限を置く 1 通りだけを採り、表は描かない。
- 上限が短すぎる側の誤り（正常な作成を打ち切る）は、作り直し 1 回と注記で見えるようにする。常態化したら上限を引き上げる（IADR-0642 に戻す条件を書く）。
