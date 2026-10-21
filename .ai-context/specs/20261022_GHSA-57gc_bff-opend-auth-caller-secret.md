---
title: MSP 連結ローカルの BFF へ、AST の OpenD 認証サイドカーに名乗る共有シークレット（OpendAuth__CallerSecret）を ESO で配線し、AST の submodule を前進させる（Refs GHSA-57gc-27c7-gvwm）
plan_refs:
  - NFR
adr_refs:
  - AST/IADR-0566
  - IADR-0458
status: done
created: 2026-10-22
---

# 作業仕様書: BFF → AST の OpenD 認証サイドカーの呼び出し元検証（送り手の配線）

## 起点

- Refs GHSA-57gc-27c7-gvwm（多層防御の強化。オーナー裁定 2026-10-21・本 PR はオーナー承認 2026-10-22）。
- AST 側（AST#1426・AST/IADR-0566）が、OpenD 認証サイドカーに呼び出し元の共有シークレットの検証を足した。
  送り手の BFF は基盤の Pod であり、AST の BFF ライブラリ（`AiStockTrading.Bff.Endpoints`）を submodule から合成して動く。
  ライブラリは構成キー `OpendAuth:CallerSecret` の値をヘッダーに載せ、未構成なら上流を呼ばない。
- 方式の選択（mTLS との比較を含む）は AST/IADR-0566 にある。基盤側は配線だけで、新しい判断を足さないため IADR は起こさない。

## 変更（3 点）

| # | 箇所 | 内容 |
| --- | --- | --- |
| 1 | `deploy/local/values-local.yaml` の `services.bff.extraEnvAppend` | `OpendAuth__CallerSecret` を Secret `opend-auth-caller-secret`（キー `caller-secret`）から。`optional: true`（Secret が無くても BFF は起動する） |
| 2 | `deploy/local/vault/eso/externalsecret-opend-auth-caller-secret.yaml`・`scripts/k8s-local-up.sh`（ESO=1 のときだけ apply）・`services.bff.deploymentAnnotations` | 基盤の名前空間の ExternalSecret（Vault `ai-stock-trading/opend-auth`・プロパティ `caller-secret`＝AST の chart と同じキー）と Reloader の注釈 |
| 3 | `src/ai-stock-trading` | AST の submodule を AST#1426 のマージ後の develop へ前進 |

- ESO の読み取り policy（`policy-eso-read.hcl`）は既に `secret/data/ai-stock-trading/*` を許す。変更しない。
- 本番像（`deploy/helm/microservices-platform/values.yaml`）は触らない（本番の BFF は `OpendAuth__BaseUrl` も持たない）。
- `extraEnv` ではなく `extraEnvAppend` に足す（#1389。本番既定の env を消さない）。

## 母集合（規則 9・10）

走査語: `OpendAuth`・`opend-auth`（`src/`・`.ai-context/specs/`・`CHANGELOG.md` を除く）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `deploy/local/values-local.yaml`（bff の OpendAuth 2 件） | 1 件足す・注釈を足す |
| 2 | `deploy/local/aliases/microservices-platform-externalnames.yaml`（ExternalName `opend`） | 変更不要（宛先は同じ） |
| 3 | `scripts/k8s-local-up.test.js`（extraEnvAppend の試験の断片） | 変更不要（BaseUrl の断片を使う別の試験） |
| 4 | `.ai-context/adr/IADR-0458`・過去の仕様書・手順書の OpendAuth の記述 | 書き換えない（凍結記録・宛先の記述は変わらない） |

## 受け入れ基準

- [x] `helm template -f deploy/local/values-local.yaml` の bff-service に `OpendAuth__CallerSecret`（secretKeyRef・optional）と Reloader の注釈が描かれ、既存の `OpendAuth__*` 2 件は残る
- [x] ESO=1 で ExternalSecret を apply し、既定（ESO 未設定）では apply しない・手動の Secret を作らない（`k8s-local-up.test.js`）
- [x] submodule が AST#1426 を含む

## 配備の順序（閉場後）

**MSP → AST の順で、閉場後に配備する。**

1. Vault に `ai-stock-trading/opend-auth` のプロパティ `caller-secret`（32 文字以上の乱数）を入れる（値は書かない）。
2. 本 PR を配備する（`ESO=1` の `k8s-local-up.sh`）。基盤の名前空間に Secret `opend-auth-caller-secret` が同期され、BFF が作り直される。
   この時点では旧サイドカーは検証を持たないので、BFF がヘッダーを載せても影響しない。
3. AST を配備する（AST#1426 の手順。OpenD の Pod は 1 回作り直される）。

- 🔴 手順 1 を後回しにした場合: Reloader は既定で Secret の**更新**だけを見て**作成**を見ない（`reload-on-create` を設定していない）。
  後から ESO が Secret を作っても BFF は古い env のままなので、`kubectl -n microservices-platform rollout restart deploy/bff-service` を手で打つ。
  以後の値の回し（Secret の更新）は Reloader が作り直す。
