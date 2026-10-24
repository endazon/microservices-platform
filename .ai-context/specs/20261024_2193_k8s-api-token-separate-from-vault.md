---
title: 作業仕様書 — BFF が Kubernetes API へ名乗るトークンを Vault 用の projected token と分ける（#2193）
type: spec
status: done
related_ids:
  - SC-22
  - NFR-18
  - ADR-0095
  - ADR-0104
  - ADR-0132
  - IADR-0456
  - IADR-0460
  - IADR-0585
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095 決定 3（BFF が Vault へ書く。ESO は読み取り専用の同期）
  - planning:projects/microservices-platform/07_adr/ADR-0104 決定 2（供給元は配備の事実を出す）
  - planning:projects/microservices-platform/07_adr/ADR-0132（秘匿管理は OpenBao）
related_specs:
  - 20261022_ghsa-j4qv_vault-k8s-auth-audience
issue: "#2193"
---

# 作業仕様書 — Kubernetes API 用のトークンと Vault 用のトークンを分ける

## 目的と射程

稼働 PoC（一括配備 #2175）で、#2082 の 1 段目（`VAULT_ROLE_AUDIENCE_DEFER=1`）のあと BFF の Kubernetes API への要求がすべて 401 になった。
影響は 2 つある。SC-22 の「供給元」が全項目「確認できない」になる（`ExternalSecretPresenceReader`）。保存時の即時同期（`ExternalSecretSyncRequester`）も通らない。

**射程**: BFF の 2 つの Kubernetes API の呼び出しが名乗るトークンの読み先。
**射程外**: Vault への login（通っている）・RBAC・ESO・role の audience（IADR-0585 の 2 段目）。

## 原因

IADR-0585 決定 2 は「k8s API 用の既定のトークンは残す（BFF は ExternalSecret の同期に k8s API を使う）が、秘匿管理へはそれで名乗らない」と定めた。
しかしコードでは、Vault の login と Kubernetes API の 2 つの呼び出しが同じ口 `IServiceAccountTokenReader` を共有していた。この口は `Vault:ServiceAccountTokenPath` を読む。
#2082 で、この既定のパスと helm の env を Vault 用の projected token（`/var/run/secrets/vault/token`・audience: vault）へ移した。その結果、Kubernetes API にも Vault 用のトークンが送られた。
kube-apiserver は対象者（audience）が API サーバでないトークンを拒むので、401 になった。

試験で見えなかった理由: `FakeKubernetesApi` が受け付ける Bearer と `FakeVault` の SA の JWT が同じ値だった。読み先を取り違えても、偽物は通していた。

## 方式

1. **型を分ける**: `IKubernetesApiTokenReader` と `FileKubernetesApiTokenReader` を新設した。読み先は `ExternalSecretSync:ServiceAccountTokenPath`
   （既定は `/var/run/secrets/kubernetes.io/serviceaccount/token`＝kubelet が自動でマウントする既定の SA トークン）である。
   `ExternalSecretSyncRequester` と `ExternalSecretPresenceReader` はこちらを使う。Vault の login（`VaultKvClient`）は従来どおり `IServiceAccountTokenReader` を使う。
   同じ口を共有すると片方の読み先の変更が黙ってもう片方へ波及するので、構成キーだけでなく型も分けた。
2. **フォールバックしない**: Kubernetes API 用のファイルが読めなければ、従来どおり `Failed` / `Unknown` に倒す。Vault 用で代用しない。
3. **Helm は変えない（コメントだけ）**: `deployment.yaml` は、`serviceAccount` を持つサービス（BFF・datasource）に `automountServiceAccountToken: false` を描かない。
   既定の SA トークンは #2082 の後もマウントされている。`scripts/helm-pod-security.test.js` の 3 が「専用の SA を持つのにトークンを外している」を落とす形で、すでに固定している。
   API サーバ用の projected token を別に足す案は採らない。既定のトークンで足りるうえ、マウントが増えるだけで、守れる範囲は広がらない。
   `ExternalSecretSync__Enabled` の注釈を「既定の SA トークンで名乗り、Vault 用は送らない」に直した。

**IADR は起こさない**。新しい判断は無く、IADR-0585 決定 2 が既に定めた形（API 用の既定のトークンを残し、Vault へはそれで名乗らない）へコードを合わせる是正である。

## 母集合（規則 9）

`grep -rn "IServiceAccountTokenReader\|ServiceAccountTokenPath\|KubernetesApi" src deploy`（`obj/` を除く）で引いた。

| ヒット | 扱い |
| --- | --- |
| `ExternalSecretSync.cs`（`ExternalSecretSyncRequester`） | **対象**。口を `IKubernetesApiTokenReader` へ |
| `ExternalSecretPresence.cs`（`ExternalSecretPresenceReader`） | **対象**。同上 |
| `SecretItemServiceCollectionExtensions.cs` | **対象**。新しい口を登録する |
| `VaultKvClient.cs`（`VaultKvClient`・`VaultOptions`） | 対象外（Vault の login。読み先は正しい）。口の説明だけ直す |
| `DataSourceService/Infrastructure/Secrets/*` | 対象外。datasource は Kubernetes API を呼ばない（Vault の読み取りだけ） |
| 試験 `FakeKubernetesApi.cs`・`BffTestFactory.cs`・`BffSecretItemEndpointTests.cs` | **対象**。Kubernetes API 用の JWT を Vault 用と別の値にする |
| `deploy/helm/.../deployment.yaml` | 注釈だけ。描画は変えない |

## 受け入れ基準

- [x] Kubernetes API への 2 つの要求（`get` / `patch`）は、Kubernetes API 用のトークンで名乗る。Vault 用のトークンは送らない。
  `KubernetesApiTokenSeparationTests` と `BffSecretItemEndpointTests`（T-68・同期依頼）で確かめた。偽の API サーバは Kubernetes API 用の値以外を 401 で返す。
- [x] 本番の登録（`AddSecretItemInjection`）で、2 つの口がそれぞれの構成のファイルを読む（2 つのパスを両方与えて測る）。
- [x] Kubernetes API 用のファイルが無いとき、Vault 用へ倒れずに `Failed` / `Unknown` になり、要求を送らない。
- [x] 既定のパスが 2 つで違う（`/var/run/secrets/kubernetes.io/serviceaccount/token` と `/var/run/secrets/vault/token`）。
- [x] 変異: `ExternalSecretPresenceReader` を `IServiceAccountTokenReader` へ戻すと、4 本が赤になる（実測）。

## 検証

- `dotnet test Bff/Platform.Bff.Tests`（全件）・`dotnet format --verify-no-changes`
- `node scripts/helm-pod-security.test.js`・`node scripts/vault-k8s-auth-audience.test.js`・`helm lint`（要 helm）

## PoC

配備前の手順・配備後の確認は PR 本文に書く。
