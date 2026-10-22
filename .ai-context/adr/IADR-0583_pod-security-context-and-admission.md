---
title: IADR-0583 チャートの Pod は restricted の形（非 root・権限昇格なし・ケーパビリティなし・seccomp RuntimeDefault）で描き、外部のイメージは利用者 ID を変えずに付けられる分だけ付け、専用の SA を持たない Pod はトークンを置かず、Namespace に Pod Security Admission の水準を貼る
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0005, ADR-0007, ADR-0008, ADR-0021]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0008 （k3s 実行基盤）
  - planning:projects/microservices-platform/07_adr/ADR-0005 （サービスメッシュ）
related_specs:
  - ../specs/20261022_ghsa-j4qv_pod-security-context.md
---

# IADR-0583: Pod のセキュリティ設定と Pod Security Admission（GHSA-j4qv-2w99-xg5p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: コーディネーター指示（2026-10-22。Pod のセキュリティ設定）・claude（水準・uid・外部のイメージの扱い）

## 起点・関連

- 起点: GHSA-j4qv-2w99-xg5p（多層防御の強化・最小権限）。本 IADR はその 1 本目（Pod のセキュリティ設定）。
- 計画: **ADR-0008**（k3s 実行基盤）・**ADR-0005**（サービスメッシュ）・**ADR-0007**（Deployment の骨格）・**ADR-0021**（SPA の配信は Caddy）。
  いずれとも矛盾しない（実行基盤・メッシュ・配信の選択は変えない。Pod の設定を足すだけ）。

## コンテキストと課題

- チャート（`deploy/helm/microservices-platform`）の Pod は `securityContext` を持たず、コンテナはイメージの既定の利用者で動いていた。
  .NET の aspnet イメージと Caddy のイメージの既定の利用者は root である。
- Namespace に Pod Security Admission の水準が無く、水準から外れた Pod を作っても警告も記録も出なかった。
- 専用の ServiceAccount を持たない Pod にも、名前空間の `default` のトークンが置かれていた。

## 検討した選択肢

1. **Namespace に enforce=restricted を貼るだけ**: メッシュの注入（istio-init は NET_ADMIN / NET_RAW を要る）と外部のイメージが
   満たさないため、配備できなくなる。採らない。
2. **全 Pod を一律に非 root へ**: SeaweedFS・Wiki.js・TEI はイメージの利用者と PVC 上のデータの所有者が結び付いている。
   利用者を変えるとデータを読めなくなる。本対応では採らない（残る制約）。
3. **チャートが作るイメージと利用者 ID が分かっている汎用イメージは restricted の形、外部のイメージは付けられる分だけ、
   Namespace は warn / audit を restricted・enforce を baseline（採用）**。

## 決定

1. **restricted の形**（部品は `templates/_pod-security.tpl`）: Pod に `runAsNonRoot: true`・`runAsUser` / `runAsGroup`（1 以上。0 や数でない値は描画で止める）・
   `seccompProfile: RuntimeDefault`。コンテナに `allowPrivilegeEscalation: false`・`capabilities.drop: ["ALL"]`。対象と uid:
   - `services` の全 Deployment（.NET）: aspnet イメージの `APP_UID`＝**1654**（`podSecurity.dotnetRunAsUser`）。
   - frontend（Caddy）: **10001**（`frontend.runAsUser`）。Dockerfile に同じ `USER` を置き、`caddy` のファイルケーパビリティを外し
     （待受は 8080。付いたままだとケーパビリティを落としたコンテナで exec できない）、書けるのは `config.js`・`/config`・`/data` だけにする。
   - 合成監視（node イメージの node＝**1000**。overlay も同じ値）・ドリフト検出の PostSync Job（curl イメージの curl_user＝**100**）。
2. **外部のイメージ**（SeaweedFS・Wiki.js・TEI）: `seccompProfile: RuntimeDefault` と `allowPrivilegeEscalation: false` まで。利用者 ID は変えない。
3. **トークン**: 専用の ServiceAccount を持たない Pod は `automountServiceAccountToken: false`。持つ Pod（BFF・datasource。Vault の k8s auth と
   ExternalSecret の同期に使う）は従前どおり。メッシュのサイドカーは自前の projected token を使うので影響しない。
4. **Pod Security Admission**: Namespace に `warn` / `audit` = restricted を常に貼る。`enforce` = baseline は**メッシュの注入が無いときだけ**貼る
   （注入される istio-init が baseline を満たさないため。Istio CNI を入れない限り）。チャート（`namespace.create` のとき）と
   経路B の起動器（`k8s-local-up.sh` [5/7]。ISTIO=1 または注入ラベルが残る Namespace では enforce を外す）が同じ規則で貼る。
5. 試験 `scripts/helm-pod-security.test.js`（CI の static-checks-units）が、既定・values-local・任意部品の描画、描画で止まる条件、
   Dockerfile・overlay・起動器との一致、変異を固定する。

## 結果

- チャートが作る Pod は root で動かず、権限昇格とケーパビリティを持たない。水準から外れた Pod は警告と監査記録に出る。
- frontend のイメージ自体が非 root になる（compose でも同じ）。

## 残る制約

- メッシュの注入がある Namespace（本番像・ISTIO=1 の経路B）では enforce を貼れない。enforce を効かせるには Istio CNI の導入が要る（別の判断）。
- 外部のイメージ（SeaweedFS・Wiki.js・TEI）は root のまま動き得る。非 root 化は PVC のデータの所有者の移行と組で行う（別の判断）。
- `readOnlyRootFilesystem` は付けていない（.NET の一時ファイル・Caddy の `/config` `/data` の置き場を emptyDir へ移す設計が要る）。
- 基盤（platform-infra）・観測（observability）の名前空間の Pod は本 IADR の射程外。
