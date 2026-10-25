---
title: IADR-0635 e2e-live の pull_request.paths は試験される側（スタックが建てるイメージの中身・起動器と配備物）まで覆い、イメージに入らない変更と e2e-live の旗で当たらない配備物は外す
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-30, IADR-0569, IADR-0625]
author: claude
created: 2026-10-25
updated: 2026-10-25
related_specs:
  - ../specs/20261025_2244_e2e-live-paths-system-under-test.md
---

# IADR-0635: e2e-live の起動条件を試験される側まで広げる（#2244）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2244。条件はオーケストレーターの指示 2026-10-25: 母集合を e2e-live が実際に起動するイメージと配備物から引く・外すものの扱いを決める・
  必須チェックが恒久 pending にならないことを確かめる）

## 起点・関連

- 起点 issue: #2244。発端は #2243（BFF の鍵リングの修正。e2e-live が起動せず、手動の workflow_dispatch で確かめた）。
- 関連: [IADR-0569](./IADR-0569_e2e-layers-and-live-opt-in.md)（live の実行層）・[IADR-0625](./IADR-0625_e2e-live-workflow-disposable-stack.md)（e2e-live の workflow）。
- 作業仕様書: [20261025_2244_e2e-live-paths-system-under-test](../specs/20261025_2244_e2e-live-paths-system-under-test.md)。

## コンテキストと課題

`e2e-live.yml` の `pull_request.paths` は試験の側（`e2e/live/**`・realm・seed・検査器）しか含まず、試験される側（BFF のログインの経路、スタックの起動と構成）を
覆っていなかった。e2e-live は同じチェックアウトからイメージを建ててスタックを起こすので、バックエンドやフロントエンドの変更こそ live で確かめる対象である。

## 決定

1. **母集合は e2e-live が実際に建てて配備するものから引く**: run の実測（`[2/7]` が建てる 20 個のイメージ）、`scripts/k8s-local-images.sh` の対応表が指す
   Dockerfile の `COPY`、起動器（`k8s-local-up.sh --live`）が e2e-live の旗（`LOCALEDGE`・`ABACSEED`・`SEARCHSEED`・`LOCALEMBED`）で当てる配備物。
2. **バックエンドは全ユニットを覆う**（BFF と認証の経路だけに絞らない）。live の試験は BFF 越しに knowledge のサービスも読む。
3. **外す**（`!`。足す行の後に置く）: 試験だけの変更（バックエンドの試験プロジェクト・フロントエンドの単体試験・モックの E2E）、`**/*.md`、
   e2e-live の旗では当たらない配備物（`deploy/local/{vault,vault-persistence,argocd,headlamp,edge-istio,edge-istio-reset-floor,synthetic-monitor,reloader}`）。
   live の試験の行は外す行の後に置き、モックの E2E を外した中から `e2e/live/**`・`e2e/support/**` を戻す。
4. **一覧と代表の判定を `scripts.repo.test.js` で固定する**（GitHub の paths の意味で、起動する変更 21 例・起動しない変更 13 例・足す行の土台の実在・行の順序）。

## 理由

- 決定 2: knowledge の変更で live の検索・個人資料・文書が壊れても、BFF に限ると PR で捕まらない。1 回 30 分は、変更がイメージか配備物に入るときだけ払う
  （決定 3 で、イメージに入らない変更と当たらない配備物を除く）。
- 決定 3 の順序: GitHub の `paths` は後の行が前の行を上書きする。live の試験を外す行より前に置くと、`!src/platform/frontend/e2e/**` が live の試験まで外す。

## 影響

- e2e-live は必須チェックではない（`docs/ai-workflow.md` の必須の表で取り消し線。ルールセット `develop-rule` の必須 check は `image-build` だけ）。
  起動しない PR が pending で止まることは無い。起動が増える分、ランナーの時間が増える（PR の concurrency は同じ PR の古い実行を取り消す）。
- 起動器が新しい配備物を当てるようになったら、ここへ足す（決定 1 の母集合の取り直し）。旗の外の配備物を常に当てるようにしたら、外す行から消す。
