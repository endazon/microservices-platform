---
title: 作業仕様書 — BFF の SA の ExternalSecret の更新を force-sync の注釈だけに限る ValidatingAdmissionPolicy を置く（GHSA-j4qv-2w99-xg5p）
type: spec
status: done
related_ids: [SC-22, NFR-18, ADR-0095, IADR-0588, IADR-0433, IADR-0456, IADR-0583]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md（決定 3: BFF が Vault へ書く。ESO は読み取り専用の同期を維持する）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — BFF の SA の ExternalSecret の更新を force-sync の注釈だけに限る（GHSA-j4qv-2w99-xg5p）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0588**（新規）に置く。
> 多層防御の強化（最小権限）。参照は GHSA の ID だけとする。

## 起点となる計画書（トレーサビリティ）

- 画面: **SC-22**（秘密情報・接続設定の管理。書き込み後の即時同期）。非機能要求: **NFR-18**。
- 計画 ADR: **ADR-0095** 決定 3（BFF が Vault へ書く。ESO は読み取り専用の同期を維持する）。本変更は BFF の権限を狭めるだけで、同期の主体（ESO）・同期の依頼の形（force-sync の注釈）は変えない。矛盾しない。
- 実装 ADR: **IADR-0588**（新設）。前提 **IADR-0456** 決定 4（書き込み後の force-sync の依頼・RBAC は `resourceNames` で items[] に限る）・**IADR-0433** 決定 4（BFF 専用の SA）・**IADR-0583**（同じ GHSA の Pod 側の多層防御）。

## 要件

1. BFF の SA が ExternalSecret に対して通せる更新を、**注釈 `force-sync` を数字（unix 秒）にする変更だけ**に限る。spec・status・labels・他の注釈・finalizers・ownerReferences・generateName の変更、作成・削除・サブリソースへの要求は拒否する。
2. BFF 以外の主体（ESO・運用者・他の SA）には効かない。
3. 評価できないときは拒否する（`failurePolicy: Fail`・`validationActions: [Deny]`）。
4. BFF の現行の要求（merge-patch で `metadata.annotations.force-sync` だけ）はそのまま通る。
5. 判定を静的に検証する試験（陽性・陰性・主体）を置き、変異で赤を確かめる。

## 母集合（規則 9・10）: BFF が k8s API に対して行う操作と持つ権限

走査: `grep -rn "/apis/\|/api/v1\|HttpMethod\.\|merge-patch" src/platform/backend/Bff --include=*.cs`（試験を除く）、`grep -rln "RoleBinding" deploy scripts src`（全 RoleBinding / ClusterRoleBinding）と各 `subjects`、`grep -rn "system:serviceaccounts\|name: bff$" deploy`（グループ束縛・SA 名）、取引ユニット（`src/ai-stock-trading` と隣接クローンの `deploy/`）の RoleBinding。

### コードが送る要求（`src/platform/backend/Bff/Platform.Bff/Foundation/Secrets/`）

| 場所 | 動詞 | 対象 | 形 |
| --- | --- | --- | --- |
| `ExternalSecretSync.cs`（`ExternalSecretSyncRequester`） | **PATCH** | `/apis/external-secrets.io/v1/namespaces/{ns}/externalsecrets/{name}` | `application/merge-patch+json`。本体は `{"metadata":{"annotations":{"force-sync":"<unix 秒>"}}}` の 1 キーだけ。キーは定数 `ForceSyncAnnotation = "force-sync"` |
| `ExternalSecretPresence.cs` | GET | 同上 | 読み取り（一覧の供給元の判定） |

- 他に k8s API を呼ぶコードは無い（`VaultKvClient.cs` は Vault の API。SA トークンは Vault の k8s 認証に使うだけで、k8s への書き込みではない）。

### BFF の SA（`microservices-platform/bff`）に束縛された権限

| 束縛 | 置き場 | 名前空間 | 動詞 | 対象 |
| --- | --- | --- | --- | --- |
| Role / RoleBinding `bff-externalsecret-sync` | チャート `templates/bff-externalsecret-sync-rbac.yaml`（`services.bff.externalSecretSync.enabled`） | microservices-platform | get, patch | externalsecrets（`llm-provider-credentials`・`wikijs-sync`） |
| 同上 | `deploy/local/vault/eso/rbac-bff-externalsecret-sync.yaml`（`ESO=1` で apply） | platform-infra | get, patch | externalsecrets（`keycloak-smtp`） |
| 同上 | 同上 | ai-stock-trading | get, patch | externalsecrets（`ast-secrets`・`moomoo-credentials`・`moomoo-rsa`） |

- **ExternalSecret 以外に BFF が書けるリソースは無い。** ClusterRoleBinding（Headlamp の 3 つ・Vault の auth-delegator）はいずれも BFF を束縛しない。`system:serviceaccounts` 等のグループ束縛も無い。取引ユニットの配備に RoleBinding は無い。
- **CREATE / DELETE / update（PUT）/ サブリソースの権限は RBAC 側で持たせていない**（動詞は get / patch だけ）。本 PR は RBAC を変えない。
- 範囲: 本 PR で限るのは上の表の ExternalSecret の更新（全名前空間）。範囲外（報告のみ）: なし。

### 消費側（変える・確かめるもの）

| 場所 | 扱い |
| --- | --- |
| `docs/operations/secret-item-live-sync-check-runbook.md` 0-4 | 事前確認に policy と binding の存在を足す |
| 運用者・起動器の `kubectl annotate ... force-sync=...`（runbook・`deploy/local/README.md`・`bootstrap.sh`・`k8s-local-up.sh`） | 運用者（人）や起動器の資格情報で走る。BFF の SA ではないので当たらない（変えない） |
| `deploy/argocd/appproject.yaml` | 本番像の既定は `externalSecretSync.enabled=false` で Role も policy も描かない。許可種別は変えない（下の残余） |

## 配置

- **チャート**（`templates/bff-externalsecret-sync-admission.yaml`）に置き、Role と同じ条件（`services.bff.enabled` かつ `externalSecretSync.enabled`）で描く。RBAC と同じ場所の原則に従う。
- policy はクラスタ全体の資源で、利用者名（`system:serviceaccount:<namespace.name>:<serviceAccount.name>`）で当てる。チャートの外（`deploy/local`）の Role で得た権限にも同じく効くので、`deploy/local` 側に重複して置かない（helm の所有と衝突する）。

## 受け入れ基準と写像

| # | 基準 | 写像先 |
| --- | --- | --- |
| 1 | Role と同じ条件で描く（既定は描かない・values-local は描く）。利用者名は values から | `scripts/helm-bff-externalsecret-admission.test.js`「描画」2 本 |
| 2 | Fail / [Deny] / binding が policy を名指す / CREATE・UPDATE・DELETE とサブリソースに当たる | 同「形」 |
| 3 | 陽性（force-sync の値だけ・注釈の無い物に足す・変化なし）／陰性（spec・status・labels・他の注釈の追加 / 変更 / 削除・force-sync の削除や数字以外・finalizers・ownerReferences・generateName・CREATE・DELETE・status）／主体（BFF 以外・別名前空間の同名 SA・人） | 同「判定」2 本（CEL を評価） |
| 4 | 注釈のキー・要求の形・RBAC の動詞が供給側と一致 | 同「供給側」2 本 |
| 5 | 変異で赤 | 同「変異」・評価器の自己検査 |
| 6 | 実 API サーバ（v1.35.0）で同じ行列が通る | 下の実測（手元） |

## 実測（実 API サーバ）

envtest の kube-apiserver v1.35.0（etcd つき・RBAC 有効）に、ESO 2.8.0 の ExternalSecret の CRD・チャートと `deploy/local` の Role / RoleBinding・描画した policy / binding を当て、`kubectl --as=system:serviceaccount:microservices-platform:bff` で 20 通りを試した。全て期待どおり（陽性 3・陰性 14・BFF 以外 3）。RBAC を一時的に広げた状態でも、作成・削除・サブリソースは policy の文言で拒否された。

- 実測で見つけた点: **主資源への要求では `request` に `subResource` の鍵が無い**（空文字ではない）。`request.subResource == ''` は評価の誤りになり、`failurePolicy: Fail` で BFF の正規の要求まで拒否された。`!has(request.subResource) || ...` の形にした。試験の評価器も鍵が無い形で要求を作り、この備えを外す変異が赤になることを固定した。

## 統合スタック（integration-stack）での実測

- 統合スタックは `ESO=1` で起こさないので ExternalSecret の CRD も実体も無く、BFF の更新を当てて確かめることはできない。policy はチャートに含まれて `helm upgrade --install` で当たるので、**式が API サーバ（k3s v1.35）に受理されること**だけはその段が確かめる（受理されなければ起動が落ちる）。判定の行列は上の静的試験と手元の実測が持つ。門（`check-stack-ready.js`）は足さない。

## 残余

1. `deploy/argocd/appproject.yaml` の許可種別は、Role / RoleBinding / ServiceAccount と同じく ValidatingAdmissionPolicy も持たない。本番像の既定（`externalSecretSync.enabled=false`）では描かれないので現状は影響しない。本番で有効にするときは Role 等と合わせて許可種別を足す。
2. `externalSecretSync.enabled` を false にしたまま `deploy/local` の Role だけを当てた構成では policy が無い（起動器は `values-local.yaml` で true を渡すので、手順どおりなら起きない）。
