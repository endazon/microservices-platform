---
title: 作業仕様書 — 秘匿管理の運用者の policy を、要る path と操作だけに絞る（#2421）
type: spec
status: in-progress
related_ids: [NFR-18, IADR-0094, IADR-0683]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2421"
---

# 作業仕様書 — 秘匿管理の運用者の policy（#2421）

> 着手 2026-11-05（develop `170f1efb`）。判断の記録は **IADR-0683**（新規）。稼働中のクラスタへは当てない（利用者が `deploy/local/vault/oidc/bootstrap.sh` を再実行して当てる）。

## 起点

- 要求 **NFR-18**。前提: **IADR-0094**（秘匿管理の OIDC ログイン。`platform-operator` → policy `operator`）。

## 現状（develop で読み直した）

- `deploy/local/vault/oidc/policies/operator.hcl` は `secret/data/*` と `secret/metadata/*` に read / list を与える（KV の全部の値を読める）。

## 母集合（規則 9）

- 運用者の操作の走査: `platform-operator` を docs・deploy で引いた。運用者が秘密の値に触るのは SC-22 の画面（`/admin/secrets`。BFF の役 `policy-bff-secret-*.hcl` が Vault へ書く）だけで、秘匿管理の画面で値を読む手順は無い。
- KV の接頭辞の走査（`deploy/local/vault/eso/*.hcl` の `path "secret/…"`）: `msp/`・`ai-stock-trading/`・`datasource/` の 3 つ。

## 方針

- KV の値（`secret/data/`）は与えない。メタデータ（版・更新の日時）の read / list を 3 つの接頭辞だけに与え、マウントの根の一覧（`secret/metadata/` の list）と `sys/mounts` の read を残す。
- 値を読む必要があるときは管理者（`platform-admin`）が行う。

## 受け入れ基準

- AC-1: 運用者の policy が `secret/data/` を含まず、上の path と操作だけを持つ（試験。旧の policy で落ちることを確かめた）。
- AC-2: 使っている接頭辞（ESO・BFF の policy が引くもの）が、運用者のメタデータの一覧に全部入っている（試験）。

## 残余

1. 稼働中の秘匿管理の policy は、利用者が OIDC の bootstrap を再実行するまで旧のまま。
