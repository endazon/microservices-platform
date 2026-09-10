---
title: MSP 連結ローカルの BFF へ OpenD 認証ゲートウェイの上流（OpendAuth__BaseUrl）を配線する
issue: "#1376"
plan_refs:
  - NFR
adr_refs:
  - IADR-0070
  - IADR-0071
status: done
created: 2026-09-10
---

# 作業仕様書: BFF → OpenD 認証ゲートウェイの上流配線（#1376）

## 起点

- issue #1376。統合 SPA の「OpenD 認証操作」（AST/SC-04）で検証コードを入力できない（2026-09-10 利用者報告）。
- 画面は「ゲートウェイの状態を取得できていません（供給元がありません）」を出し、入力欄と送信は disabled になる。

## 原因（実測）

| 層 | 実測 |
| --- | --- |
| サイドカー | `opend` Pod は `opend` + `opend-auth` の 2/2。Pod へ直接 `GET /opend-auth/state` → `{"status":"waiting","prompt":"phone",…}` |
| Service | `opend`（ClusterIP）に port `auth:8080` がある |
| BFF | `bff-service` の env に `OpendAuth__*` が無い。AST/IADR-0321 決定 6 のとおり `OpendAuth:BaseUrl` の既定は空＝「未構成 → 供給が無い」（fail-safe）なので `/bff/opend-auth/state` は `NotSupplied` を宣言する |
| MSP の配線 | `deploy/local/values-local.yaml`・`deploy/local/aliases/` のどこにも `OpendAuth` が無い（#1366 は合成点への登録のみ） |

**画面は正しく縮退している。** 欠けていたのは MSP 連結ローカルの配線だけである。

## 設計

他の AST 上流（`configuration-service` ほか）と同じ型にする。

| 対象 | 変更 |
| --- | --- |
| `deploy/local/aliases/microservices-platform-externalnames.yaml` | ExternalName `opend` → `opend.ai-stock-trading.svc.cluster.local` を足す |
| `deploy/local/values-local.yaml` `services.bff.extraEnv` | `OpendAuth__BaseUrl=http://opend:8080`・`OpendAuth__DeviceTrustPersisted=true` |

- `EgressStable` は与えない（ローカルは固定 NAT ではない。未設定＝供給なし）。
- `DeviceTrustPersisted=true` は PVC `opend-persist` を持つ配備構成の事実（AST/IADR-0321「配備構成から静的に決まる」）。
- 本番 chart（`values.yaml`）は触らない（AST の配備有無で BFF の既定を左右させない。fail-safe）。

### 採らなかった案

- **values に FQDN を直書き**: 別名の型と揃わず、AST 側の namespace 名の追随点が 2 つになる。
- **`kubectl set env`**: env の所有が helm と割れ、次回の `helm upgrade` が衝突する（AST chart README の警告と同型）。

## 走査した母集合（規則 2・9）

`OpendAuth`（`src/` `node_modules` `.git` `.claude` 除く）で全走査: 0 件。`opend` で `deploy/local` を走査: 0 件。
追随すべき既存記述は無い。

## 受け入れ基準

- [x] `helm template -f deploy/local/values-local.yaml` で bff-service に 2 つの env が描画され、既定描画は不変
- [x] 稼働クラスタ: `bff-service` の env に `OpendAuth__BaseUrl` が入り、`opend:8080/opend-auth/state` が 200（下の実施記録）
- [ ] 稼働クラスタ: SC-04 の入力欄が有効になる（利用者の確認待ち）

## 実施記録（2026-09-10・稼働クラスタ）

### 🔴 事故: develop の chart で `helm upgrade` し、10 サービスの新 Pod が `CreateContainerConfigError` になった

稼働 `msp` リリース（rev 27・9/5）に対し、develop の chart ＋ `values-local.yaml` で `helm upgrade` した（rev 28）。
develop の chart は 9/5 以降の変更（east-west gRPC の port・`ServiceToken__*` の Secret 参照ほか。差分 545 行）を含み、
その Secret は Vault の再投入（利用者の予定作業）まで存在しないため、新 ReplicaSet の Pod が `CreateContainerConfigError`
で止まった。旧 Pod が残ったため利用者向けの断は無かった。

- 復旧: `helm rollback msp 27`。1 回目は完了済み Job `config-drift-postsync` の immutable field で失敗（rev 29 failed。
  一部の Deployment だけ戻った）。Job を消して再実行し rev 30（Rollback to 27）で全 Deployment が戻り、全 Pod 2/2。
- 教訓: **稼働リリースの chart と作業ツリーの chart は別物である。** 値だけ足したいときも、chart 全体が当たる。
  差分（`helm get manifest --revision N` と `helm template` の diff）を見てから当てる。

### 暫定の適用（SSA の部分適用）

恒久配線は本 PR の `values-local.yaml` だが、上記の理由で chart を当てられないため、稼働 BFF には
`kubectl apply --server-side --field-manager=opend-auth-stopgap` で env 2 件だけを重ねた（selector 等は触らない）。

- 🔴 最初に `--field-manager=helm` で部分適用したところ、**同一 manager の SSA は省略した field を「削除の意図」と解釈**し
  `spec.selector` を消そうとして拒否された（immutable）。何も変わらず安全側で止まった。**部分適用は専用の manager で行う。**
- 値は `values-local.yaml` と同一にしてある。次回の標準手順（`k8s-local-up.sh`）で helm が同じ値を apply しても
  SSA の衝突にならない（同値なら所有が共有されるだけ）。値を変えるときは先に `kubectl apply --server-side
  --field-manager=opend-auth-stopgap` で追随させるか、その manager の所有を外す。
- 結果: `bff-service` の env に `OpendAuth__BaseUrl=http://opend:8080` / `OpendAuth__DeviceTrustPersisted=true`。
  Pod は 2/2、既存 env 35 件は不変。クラスタ内から `opend:8080/opend-auth/state` → 200。
