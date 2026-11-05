---
title: IADR-0681 秘匿管理の運用者の policy は KV の値を読まず、メタデータの read / list を使っている接頭辞だけに与える（#2421）
type: impl-adr
status: Accepted
related_ids: [NFR-18, IADR-0094]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2421_vault-operator-policy-least-privilege.md
---

# IADR-0681: 秘匿管理の運用者の policy を絞る（#2421）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: コーディネーターの指示（2026-11-05）・claude（path の選び方）

## 起点・関連

- 起点 issue: #2421。要求 **NFR-18**。前提: IADR-0094。

## コンテキストと課題

- 運用者（`platform-operator`）の policy は KV の全部の値を読めた。運用者の役（どの秘密が在るか・いつ変わったか）に値の読み取りは要らない。値の投入・更新は SC-22 の画面（BFF の役）が行う。

## 検討した選択肢

1. **接頭辞ごとに値の read を残す**: 値を読む手順が無く、要らない権限が残る。
2. **メタデータだけを与える（採用）**: 在るか・版・更新の日時は見られ、値は読めない。
3. **運用者の policy を消す**: 秘匿管理の画面で状態を見る手段が無くなる。

## 決定

- `secret/metadata/` の list、`secret/metadata/{msp,ai-stock-trading,datasource}/*` の read / list、`sys/mounts` の read だけ。
- 値を読むのは管理者（`platform-admin`）。新しい KV の接頭辞を作ったら、この policy にもメタデータの一覧を足す（試験が ESO・BFF の policy の接頭辞と突き合わせる）。

## 結果

- 良い影響: 運用者の資格情報が漏れても、秘密の値は読まれない。
- 悪い影響: 運用者が値を確かめたいときは管理者に頼む。

## 残余

1. 稼働中の秘匿管理へは、利用者が `deploy/local/vault/oidc/bootstrap.sh` を再実行して当てる。
