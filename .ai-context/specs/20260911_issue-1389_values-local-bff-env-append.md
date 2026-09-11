---
title: values-local の BFF 上書きが本番既定の env を丸ごと消していた退行を extraEnvAppend で直す
issue: "#1389"
plan_refs:
  - NFR
adr_refs:
  - IADR-0232
status: done
created: 2026-09-11
---

# 作業仕様書: values-local の BFF env 置換（#1389）

## 起点

- #1389（integration-stack が develop で失敗）。#1385 で BFF の起動時クラッシュ（#1382）を直した後も G1 が `bff-service` を
  Ready=False で落とす。BFF ログは readiness の URL 検査 4 件（retrieval / aianalysis / feedback / dashboard）が 1 秒で
  `Unhealthy`。下流 Pod 自体は 2/2 Running。

## 原因（実測）

integration-stack の直近の緑（`3e8b1875`・09-10 12:18）と最初の赤（`37c6eaca`・13:44）の間にあるのは #1377 だけ。
#1377 は `deploy/local/values-local.yaml` に `services.bff.extraEnv`（`OpendAuth__*` 2 件）を足した。**Helm はリストを
置換する**ため、本番既定 `values.yaml` の `services.bff.extraEnv` 23 件（`Services__*` 10 件・`Introspection__Services__*`
13 件）が描画から消え、BFF はコード既定 `http://retrieval-service:5003` 等（k8s の Service ポートと不一致）へ向いて
readiness が落ちた。稼働クラスタでは Helm を経ず SSA で 2 件だけ足していたため顕在化しなかった。

```
helm template（既定）→ bff env 23 件 / helm template -f values-local → Services__* 0 件
```

## 設計

| 対象 | 変更 |
| --- | --- |
| `templates/deployment.yaml` | `extraEnv` の直後に **`extraEnvAppend`** を同じ形で描画する。上書きファイルが env を「足す」ための鍵（置換されない） |
| `deploy/local/values-local.yaml` | `services.bff.extraEnv` → `services.bff.extraEnvAppend`（内容は不変） |

既定描画（`helm template` 引数なし）は**バイト等価**（変更前後で `cmp` 一致）。検査器は足さない——本リポジトリでは同型
1 回目（AST は #279 で 2 回目に検査器を足した）。2 回目が出たら「values-local 描画から既定の env 名が失われない」検査を
足す（AST `helm.yml` の「Helm リスト置換の防御」と同型）。

## 走査した母集合（規則 2・9）

`extraEnv:` で `deploy/local/values-local.yaml` を走査: `llmgateway`（`Llm__ApiKey` 1 件）と `bff`。llmgateway は本番既定に
`extraEnv` が無く、描画差分は既定 0 件 → 据え置き。bff のみ変更。`deploy/helm/microservices-platform/README.md` に
`extraEnv` の説明は無い（追記なし）。

## 受け入れ基準

- [x] `helm template -f values-local` の bff env が既定 23 件 ＋ `OpendAuth__*` 2 件（既定からの欠落 0）
- [x] 既定描画が変更前後でバイト一致
- [x] `helm lint`（values-local）緑
- [ ] develop の次の integration-stack で G1 が bff-service を通す
