---
title: IADR-0588 BFF の SA が ExternalSecret に対して通せる更新を、ValidatingAdmissionPolicy で force-sync の注釈（数字）の変更だけに限る
type: impl-adr
status: Accepted
related_ids: [SC-22, NFR-18, ADR-0095, IADR-0433, IADR-0456, IADR-0583]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md（決定 3: ESO は読み取り専用の同期を維持する）
related_specs:
  - ../specs/20261022_j4qv_bff-externalsecret-admission-policy.md
---

# IADR-0588: BFF の SA の ExternalSecret の更新を force-sync の注釈だけに限る（GHSA-j4qv-2w99-xg5p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: claude（形の選択。多層防御の強化の一環）

## 起点・関連

- 起点: GHSA-j4qv-2w99-xg5p（多層防御の強化。最小権限）。
- 計画: **ADR-0095** 決定 3（BFF が Vault へ書く。ESO は読み取り専用の同期を維持する）。画面 **SC-22**・要求 **NFR-18**。同期の主体と依頼の形は変えないので矛盾しない。
- 前提: [IADR-0456](./IADR-0456_sc22-property-kinds-force-sync-reloader-and-seed-if-absent.md) 決定 4（書き込み後に ExternalSecret へ force-sync の注釈を merge-patch する。RBAC は get / patch を `resourceNames` で items[] に限る）・[IADR-0433](./IADR-0433_bff-vault-write-scoped-to-sc22-items.md) 決定 4（BFF 専用の SA）・[IADR-0583](./IADR-0583_pod-security-context-and-admission.md)（同じ GHSA の Pod 側）。

## コンテキストと課題

BFF は SC-22 の書き込みの後、ESO の同期を待たずに反映させるため、同期先の ExternalSecret に `force-sync: <unix 秒>` の注釈を merge-patch する。RBAC は動詞（get / patch）と名前（`resourceNames`）までは限れるが、patch で**どのフィールドを変えるか**は限れない。BFF の用途は注釈 1 つの更新だけなので、それ以外の更新が API サーバで通らない形にして最小権限にそろえる。

## 検討した選択肢

1. **RBAC だけで持つ** — フィールド単位の制限が無い。採らない。
2. **Validating admission webhook（自作・Kyverno・Gatekeeper）** — 判定を外部の Pod に置くので、可用性・証明書・配備物が増える。本件の判定は旧新の比較だけで足りる。採らない。
3. **ValidatingAdmissionPolicy（`admissionregistration.k8s.io/v1`、CEL。採用）** — API サーバの中で評価し、追加の配備物が無い。k8s 1.30 で GA。ローカル（Rancher Desktop の k3s）と CI の統合スタックはともに k3s v1.35。

## 決定

1. **置き場**: チャートの `templates/bff-externalsecret-sync-admission.yaml`。BFF の Role と同じ条件（`services.bff.enabled` かつ `externalSecretSync.enabled`）で、policy と binding を 1 つずつ描く。名前は `bff-externalsecret-force-sync-only`。
2. **当てる範囲**: `external-secrets.io` の `externalsecrets` と `externalsecrets/*` の CREATE / UPDATE / DELETE。`matchConditions` で利用者名が `system:serviceaccount:<namespace.name>:<services.bff.serviceAccount.name>` のときだけ当てる。名前空間・ラベルでは狭めない（クラスタ全体の資源なので、チャートの外の名前空間の Role で得た権限にも効く）。
3. **許す変更**: 主資源への UPDATE で、旧新を比べて次の全てを満たすものだけ。
   - spec・status が同じ（有無も含む）。
   - labels・finalizers・ownerReferences・generateName が同じ（無いときは空とみなす）。
   - 注釈は `force-sync` 以外の鍵と値が同じ（追加・変更・削除のいずれも拒否）。
   - 新しい側に `force-sync` があり、値が `^[0-9]{1,19}$`（unix 秒）に合う。
   - サーバが変える `resourceVersion`・`managedFields`・`generation` 等は比べない。
4. **失敗時**: `failurePolicy: Fail`・`validationActions: [Deny]`（警告・監査だけにしない）。
5. **式の書き方**: 主資源への要求では `request` に `subResource` の鍵が無い（空文字ではない。実 API サーバで実測）。`!has(request.subResource) || request.subResource == ''` と書く。旧新の値は `variables` で 1 度だけ作る。
6. **RBAC は変えない**: 動詞は get / patch のまま（CREATE / DELETE を持たせていないことを試験で固定する）。policy は RBAC の上に重ねる層であり、RBAC の代わりにしない。
7. **試験**: `scripts/helm-bff-externalsecret-admission.test.js`（CI の static-checks-units）が、描画の条件・形・CEL の判定の行列（本試験の小さな評価器で評価。未知の構文は例外で落とす）・BFF の要求と RBAC の動詞との一致・変異を固定する。実 API サーバ（envtest の kube-apiserver v1.35.0）で同じ行列を手元で実測した（作業仕様書）。

## 結果

- 良い影響: BFF の SA の ExternalSecret への更新は、同期の合図（注釈 1 つ・数字）だけになる。BFF の要求の形（merge-patch・1 キー）は変わらないので、BFF のコードと SC-22 の挙動は変わらない。BFF 以外（ESO・運用者の `kubectl annotate`）には当たらない。
- 悪い影響: BFF が将来 ExternalSecret の他のフィールドを変える必要が出たら、本 policy を同時に改める必要がある（試験の行列が赤になって気付ける）。
- 統合スタックは ESO を起こさないので、判定そのものは静的試験と手元の実測が持つ。式が k3s v1.35 に受理されることは、チャートを当てる段が確かめる。

## 残余

1. `deploy/argocd/appproject.yaml` の許可種別は Role / RoleBinding / ServiceAccount と同じく ValidatingAdmissionPolicy も持たない。本番像の既定（`externalSecretSync.enabled=false`）では描かれない。本番で有効にするときは合わせて足す。
2. `externalSecretSync.enabled=false` のまま `deploy/local` の Role だけを当てた構成では policy が無い（起動器は true を渡すので手順どおりなら起きない）。
