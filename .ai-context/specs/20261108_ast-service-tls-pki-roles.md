---
title: 作業仕様書 — 取引ユニットのサービス間の TLS の受け側の証明書の PKI の role（11 個）と、policy ast-eso の名指しの発行の許可を足す
type: spec
status: done
related_ids: [NFR-05, NFR-18, IADR-0689, IADR-0693]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - 20261106_2471_cloud-openbao-eso.md
---

# 作業仕様書 — 取引ユニットのサービス証明書の PKI の role と発行の許可

> 着手 2026-11-08（develop `6f7da855b`）。稼働中のクラスタには何もしない。稼働中の OpenBao への書き込みは、オーナーが `root-token.sh apply-pki-roles`・
> `apply-policies` で行う（手順書 `docs/operations/cloud-openbao-init-unseal-runbook.md` §6）。新しい判断は無い（既存の role の作り〔IADR-0693〕と
> policy の名指しの作法〔IADR-0689 決定 6〕に沿って、名前を足すだけ）ので、IADR は作らない。

## 起点

- 取引ユニットの AST#1609（AST の IADR-0646）は、サービス間の HTTP を TLS にする段（`global.serviceTls.mode`。既定 `"Off"`）を入れた。
  受け側の証明書は、ESO の VaultDynamicSecret が基盤の OpenBao の `pki_cloud/issue/<サービス>-service` を、取引ユニットの SecretStore の role `ast-eso` で呼んで取る。
- `"Off"` から上げる前提は基盤の側の 2 つ（AST の `docs/operations/cloud-service-tls-runbook.md`「前提」）:
  1. `deploy/cloud/vault/lib.sh` の `OB_PKI_ROLES` に、11 個の `<サービス>-service:ai-stock-trading` を足す。
  2. `deploy/cloud/vault/policies/ast-eso.hcl` に、その 11 個の `pki_cloud/issue/<サービス>-service` への create・update を足す（ワイルドカードにしない。ほかの口は足さない）。

## サービスの一覧（記憶で書かず、取引ユニットの正から引いた）

- AST#1609 のマージの head `42d5851ab` の `deploy/helm/ai-stock-trading/values.yaml` の `services` のキー（11 個）:
  `audit` `backtest` `configuration` `cost-control` `information-collection` `market-monitor` `notification` `order-execution` `report` `risk-management` `trade-decision`。
- 同じ head の `docs/operations/cloud-service-tls-runbook.md`「前提」1 の列挙と一致する。
- VaultDynamicSecret（`templates/external-secrets.yaml`）: `path: pki_cloud/issue/<サービス>-service`・`common_name: <サービス>-service`・
  `alt_names: <名前>.ai-stock-trading,<名前>.ai-stock-trading.svc,<名前>.ai-stock-trading.svc.cluster.local`・`ttl: 2160h`。
  基盤の role の作り（`ob_pki_roles`。許す名前は短い名前と名前空間つきの 4 つの形・`max_ttl=8760h`）にそのまま合う。

## 母集合（規則 9・10）

- `git grep -n 'OB_PKI_ROLES\|ast-eso.hcl\|pki_cloud/issue'` → 一覧を読むのは `ob_pki_roles`（configure.sh・root-token.sh apply-pki-roles）と試験 2 つ:
  - `scripts/deploy-cloud-egress-audit.test.js` の `probeTargetProblems` は「測りの宛先 ＝ PKI の role（4 つ）」を求める。測りは基盤の受け口だけを測るので、
    取引ユニットの名前空間の role を除いて比べる形へ改めた（測りは足さない）。
  - `scripts/deploy-cloud.test.js` の `keycloakPkiProblems` は「取引ユニットの側が PKI に触れない」を求める。名指しの 11 個の発行のほか（CA の口など）だけを拒む形へ改めた。
- 手順書: `cloud-openbao-init-unseal-runbook.md` §6（apply-pki-roles）・`deploy/cloud/README.md` §4 の表・`configure.sh` の頭の注記。

## 受け入れ基準と試験

| # | 受け入れ基準 | 試験（陰性対照） |
| --- | --- | --- |
| AC1 | `OB_PKI_ROLES` の名前空間 `ai-stock-trading` の role が 11 のサービスとちょうど同じ | `deploy-cloud.test.js`「AST の IADR-0646」（1 つ欠く・余りを足すと赤） |
| AC2 | `ast-eso` の PKI の path は 11 個の `pki_cloud/issue/<サービス>-service` だけで、許可は create・update だけ | 同上（1 つ欠く・ワイルドカード・read を足す・sign の口を足すと赤） |
| AC3 | 基盤の側・MSP の側の policy はその 11 個を発行できない | 同上（MSP の側へ足すと赤）・`openbao-scripts.test.sh`（実物の OpenBao で deny） |
| AC4 | 実物の OpenBao で、取引ユニットの側のトークンが 11 個に `create, update` を持ち、名指しの外・ほかの口は deny。chart と同じ名前で発行でき（基盤の CA で検証できる）、名指しの外の名前は拒まれる | `openbao-scripts.test.sh`（policy を develop の形へ戻すと赤） |
| AC5 | 測りの宛先は従来の 4 つのまま。KT6 の「取引ユニットの側が PKI に触れない」は、11 個の発行のほかを引き続き拒む | `deploy-cloud-egress-audit.test.js` C1・`deploy-cloud.test.js`（CA の口を足すと赤） |

## 利用者の作業

- 稼働中の OpenBao へ `apply-pki-roles` → `apply-policies` の順で書く（手順書 §6）。取引ユニットの側の段を上げるのは、その後（取引ユニットの手順書）。
