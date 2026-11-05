---
title: IADR-0679 起動器が取り込む外部の helm chart と URL のマニフェストを、版に加えて内容の sha256 で固定する。chart は固定の URL の tgz を確かめてからそのパスで入れ、マニフェストは確かめた写しを当てる（#2410）
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0107, ADR-0135, IADR-0519, IADR-0547]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2410_pin-external-artifacts-sha256.md
---

# IADR-0679: 外部の chart とマニフェストを sha256 で固定する（#2410）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: コーディネーターの指示（2026-11-05）・claude（形）

## 起点・関連

- 起点 issue: #2410。計画 **ADR-0107 決定 3**・**ADR-0135 決定 2**。要求 **NFR-18**。
- 前提: IADR-0519・IADR-0547（版の固定。版の上書きは固定と同じ値しか受けない）。

## コンテキストと課題

- イメージは `tag@sha256` で固定している。chart と URL のマニフェストは版だけで固定していた。
- 同じ版の名前のまま中身が変わっても、起動器は気づかない。

## 検討した選択肢

1. **helm の provenance（`--verify`）を使う**: 対象の 5 つの chart のリポジトリが署名を配っていることを前提にできない。
2. **chart とマニフェストをリポジトリへ取り込む（vendoring）**: マニフェストが大きく、版の更新の差分が読めなくなる。
3. **取得した中身の sha256 を宣言と突き合わせる（採用）**: 取得元は変えず、中身だけを固定する。

## 決定

### 決定 1 — 口は `scripts/lib/pinned-artifacts.sh` の 1 つ

- chart: 表（chart・版 → URL・sha256）を持つ。起動器は tgz を一時ディレクトリへ取り、sha256 が合えばそのパスで `helm upgrade --install` する。`helm repo add / update` は使わない。
- マニフェスト: kustomization の `resources` の URL の sha256 を、同じディレクトリの `remote-resources.sha256` が持つ。起動器は作業ディレクトリの下の同じ相対パスへ写し、URL を確かめたファイルへ差し替えて当てる（`( cd <作業ディレクトリ> && kubectl apply -k <相対パス> )`）。
- 表に無い chart・固定の無い URL が残る kustomization は当てない。

### 決定 2 — 合わなければ何も当てずに止まる

- 名指して止まり、固定と取得の sha256 を出す（値は秘密ではない）。

### 決定 3 — 版を上げるときは対で改める

- `pin_version` の版・置き場のイメージの `tag@digest`・本表（または `remote-resources.sha256`）を同じ変更で改める。試験（`scripts/scripts.repo.test.js`）が版と表の対を突き合わせる。

## 結果

- 良い影響: 同じ版の名前のまま中身が変わったとき、起動器が当てる前に止まる。
- 悪い影響: 取得元が消える・移ると起動器が止まる。版を上げる手間が 1 つ増える。

## 残余

1. `deploy/cloud/` は並行の作業の範囲（同じ口を使えるようにしてある）。
