---
title: ADR-0023（エッジ TLS の cert-manager ＋ Let's Encrypt）の適用範囲に経路B が含まれるかが読み取れない
type: plan-feedback
status: accepted
category: 要求の不足
related_ids:
  - ADR-0023
  - ADR-0021
  - NFR-11
source_repo: microservices-platform
source_ref: "PR #792 / docs/adr/IADR-0206_local-edge-tls-cert-manager.md / docs/specs/20260816_issue-779_edge-tls-termination.md"
author: claude
created: 2026-08-16
dispatched: true
planning_issue: 383
---

# ADR-0023 の適用範囲（経路B を含むか）が本文から読み取れない

## 何が起きたか

実装側で **経路B（ローカル k8s・`LOCALEDGE=1`）のエッジ TLS 終端**を入れた（#779 / `IADR-0206`）。
その際、`ADR-0023` の既定 CA（Let's Encrypt）を採れず **selfsigned CA** を選んだ。
**この選択が同 ADR からの逸脱にあたるのかどうかが、本文からは判断できなかった。**

## 本文を読んだ結果（pin `4d6a7d6` の一次資料）

- **環境を限定する語が無い。** `prod` は **0 回**出現する（`grep -oi prod … | wc -l` → 0）。
  「本番だけを対象にした ADR」とは書かれていない。
- 一方で決定は **「Istio Ingress Gateway（Envoy）がその Secret を参照して TLS 終端する」** と、
  消費側を名指ししている。**エッジを Istio と定めたのは `ADR-0021`** である。
- **経路B のエッジは Traefik** であり、これは実装側の決定（`IADR-0091`）で、計画側には現れない。
- §結果 は「**当初から社内限定・閉域ドメインの場合、HTTP-01 は成立せず、DNS-01 か Vault PKI 直行を選ぶ
  必要がある**」と述べる。しかし経路B のホストは **`*.localhost`** であり、
  **DNS-01 は `.localhost` を持つ DNS プロバイダが存在しないため成立しない**。
  **Vault PKI は `VAULT=1` の opt-in** で、`LOCALEDGE=1` の前提に置くと fail-safe が壊れる。
  **示された 2 択がどちらも取れない。**

## 実装側で採った扱い（暫定）

`IADR-0206` 決定 2 で **「`ADR-0023` は経路B を含むと読んだうえで、消費側（Traefik）と CA（selfsigned）を
局所的に外した」** という立場を明記した。同 ADR の**設計要件 3 点はそのまま踏襲**している。

- CA 固有設定を `ClusterIssuer` に閉じ込める
- `secretName`（`edge-tls`）と `dnsNames` を安定させる
- 切り替えは `ClusterIssuer` の追加と `issuerRef` の差し替えのみ

したがって **本番で Let's Encrypt / Vault PKI へ寄せるコストは上げていない。**

> **★ 当初の実装側の記述は誤っていた。** 「同 ADR は prod の Istio Ingress Gateway 前提でローカルの
> Traefik については何も決めていない」と書いていたが、**本文に無い限定を根拠にしていた**。
> クロス監査が一次資料を読んで検出し、是正した。**この誤読が起きたこと自体が、
> 範囲が読み取れないことの傍証である。**

## 計画側へ確認したいこと

1. **`ADR-0023` の適用範囲に、ローカル検証環境（経路B）は含まれるか。**
   含まれないなら、その旨を本文へ 1 行足していただきたい（実装側が毎回読み替えずに済む）。
2. 含まれる場合、**閉域ローカル（`*.localhost` のように DNS-01 も Vault PKI も取れない環境）で
   selfsigned CA を使うことは許容されるか。** §結果 の 2 択に第 3 の選択肢を足す形になる。
3. あわせて **`NFR-11`（全経路の HTTPS 化・平文 HTTP を残さない・運用系ツールを含む）の適用範囲**も
   同じ論点を持つ。実装側は「経路B は `LOCALEDGE=1` が loopback へ bind する閉域であり、
   『外部から到達し得る』に当たらない」と読んで**適用外**として扱った（`IADR-0206` 決定 4）。
   **この読み方で正しいか。**

## 提案（実装側の案）

`ADR-0023` の §決定 か §結果 に、次のいずれかを 1 行:

- **案 A**: 「本 ADR は外部公開エッジ（North-South）を対象とし、**ローカル検証環境は対象外**とする。
  ローカルの証明書運用は実装側が決めてよい」
- **案 B**: 「ローカル検証環境も対象に含む。ただし **DNS-01 も Vault PKI も取れないドメイン
  （`*.localhost` 等）では selfsigned CA を許容する**。設計要件（`ClusterIssuer` への隔離・
  `secretName` / `dnsNames` の安定）は同じく守る」

**実装側は案 B の形で先行実装している。**
どちらでも `IADR-0206` の側を追随させるので、計画側の読み方を確定していただきたい。
