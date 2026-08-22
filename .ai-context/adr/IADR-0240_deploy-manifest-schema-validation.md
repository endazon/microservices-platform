---
title: IADR-0240 chart / overlay の CI 検証へ kubeconform によるスキーマ突合を足し、CRD スキーマは datreeio/CRDs-catalog で解決する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0130
  - IADR-0209
  - IADR-0169
plan_refs:
  - "ADR-0007（CI/CD）"
  - "ADR-0021（エッジ・実行基盤）"
author: claude
created: 2026-08-22
updated: 2026-08-22
---

# IADR-0240 chart / overlay のスキーマ突合（kubeconform + CRDs-catalog）

## 状況

`scripts/check-deploy-manifests.js`（#783 前半、PR #878 で部分実装）は `helm lint` / `helm template` /
`kubectl kustomize` の**成功だけ**を chart / overlay の合否として見ている。この 3 つはいずれも
**構文検証**（Go テンプレートおよび YAML として妥当か）であり、**Kubernetes のスキーマに適合するか**
（型・必須項目・enum 値）は見ていない。

### 実測（2026-08-22。本セッションで測り直した）

`deploy/helm/microservices-platform/templates/deployment.yaml` の `replicas: {{ ... }}` を
`replicas: "not-a-number"`（整数期待の項目に文字列）へ書き換えて実行した。

```console
$ helm lint deploy/helm/microservices-platform
EXIT=0

$ helm template ci-check deploy/helm/microservices-platform
EXIT=0

$ helm template ci-check deploy/helm/microservices-platform | kubeconform -strict -summary \
    -schema-location default \
    -schema-location 'https://raw.githubusercontent.com/datreeio/CRDs-catalog/main/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json'
stdin - Deployment ingestion-service is invalid: ... at '/spec/replicas': got string, want null or integer
stdin - Deployment conversion-service is invalid: ... at '/spec/replicas': got string, want null or integer
Summary: 63 resources found parsing stdin - Valid: 61, Invalid: 2, Errors: 0, Skipped: 0
EXIT=1
```

同型の変異を kustomize overlay 側（`deploy/local/headlamp/headlamp.yaml`）でも実測し、
`kubectl kustomize` は EXIT=0、`kubeconform` は EXIT=1（`Deployment headlamp is invalid`）で
同じ非対称性を確認した。**このスキーマ不整合は現行の検証を一切止めない。**

`.ai-context/specs/20260821_issue-783_deploy-manifest-ci.md`（#783 前半の作業仕様書）は当初
これを「スコープ外」としていた。理由は「Istio / cert-manager 等の CRD を要する検証は
**CRD スキーマの供給元を決める判断が要る**」。本 IADR はその判断を確定する。

## 決定

1. **`kubeconform` を検証ツールへ追加する。** `helm` / `kubectl` と同じ fail-closed の対象にする
   （`REQUIRED_TOOLS = ['helm', 'kubectl', 'kubeconform']`）。3 点のいずれかが無ければ既定で exit 1、
   `DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` のときだけ notice を出して skip する
   （既存の抜け道と同じ形。CI では立てない）。

2. **スキーマの供給元は 2 段**（`-schema-location` を 2 回指定）。

   | 段 | 供給元 | 対象 |
   | --- | --- | --- |
   | 1 | `default`（kubeconform 同梱） | Deployment / Service / ConfigMap 等、標準 Kubernetes リソース |
   | 2 | `https://raw.githubusercontent.com/datreeio/CRDs-catalog/main/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json` | `DestinationRule` / `Gateway` / `PeerAuthentication` / `VirtualService` 等、本リポジトリが使う Istio CRD |

   実測（2026-08-22）: `datreeio/CRDs-catalog` 単体追加で `deploy/helm/microservices-platform` の
   全 63 リソース・全 8 overlay が `Valid` になった（Istio CRD 4 種を含む）。

3. **未知のスキーマは fail-closed。** `-ignore-missing-schemas` は使わない。1 段目・2 段目のいずれにも
   無いリソース種別は `Errors` として exit 1 になる —— 「スキーマが無い」ことを人が気づける形にする
   ためで、既存の「ツール不在は fail-closed」（要点 3）と同じ設計判断である。

4. **`-strict` を付ける。** 未知フィールド（typo したキー等）も違反として検出する。
   実測でこの 2 リポジトリの実コンテンツに `-strict`起因の偽陽性は 0 件だった
   （chart 1 件・overlay 8 件、全 122 リソースを実測）。

5. **kubeconform のバージョンを pin する。** 本リポジトリは `latest` 追従を問題として扱っている
   （`k8s-local-up.test.js` の ESO helm install `--version` 検査）。CI へ導入する際も同じ方針を採り、
   `latest` を使わず固定バージョンで導入する（導入手順は #783 後半＝ `ci.yml` 変更時に確定。
   本 IADR 執筆時点の実測は kubeconform v0.8.0）。

## 影響

- `scripts/check-deploy-manifests.js` の受け入れ基準（#783 の issue 本文にある
  「chart / overlay の**構文エラー・スキーマ不整合**が PR で止まる」）が、初めて**両方**満たされる。
  #878 時点では構文エラーのみだった。
- CI に**ネットワーク依存**が増える（kubeconform は既定でスキーマを HTTP から取得する）。
  GitHub Actions ランナーは既定でインターネットに出られるため実行は成立するが、
  `raw.githubusercontent.com` の一時的な不調時に検証が失敗し得る（fail-closed の裏返し）。
  **この運用リスクは受容する** —— 「スキーマが取得できない」と「スキーマに違反している」を
  区別できない状態のまま緑を返すほうが悪いという判断（要点 3 と同じ理由）。
- `scripts/scripts.repo.test.js` の CI 突合テスト（`static-checks-units` に helm / kubectl の
  導入があること）は、kubeconform の導入も併せて検査するよう #783 後半（`ci.yml` 変更）で拡張する。

## 検出しないこと

- **意味論・設計意図の違反**（例: 「この overlay は cert-manager 未導入では apply されない」）。
  これは `k8s-local-up.test.js` の静的検査が担う対象であり、本検証（レンダリング＋スキーマ突合）とは
  見ている面が違う（`.ai-context/specs/20260821_issue-783_deploy-manifest-ci.md` 基準 2 への回答を参照）。
- **クラスタの実際の受理**（例: リソースクォータ超過、既存リソースとの衝突、Admission Webhook 拒否）。
  これは実クラスタへの apply でしか検出できず、#783 後半（統合スタックを CI で起こす経路）の射程。
- **`helm template` のスナップショット比較**（意図した差分か否かの判定）。#783 前半の当初スコープ外
  のまま維持する（`.ai-context/specs/20260821_issue-783_deploy-manifest-ci.md` 「スコープ外」節）。

## 代替案

| 案 | 却下理由 |
| --- | --- |
| `kubeval`（kubeconform の前身） | 開発が停止しており（upstream 側が kubeconform への移行を案内）、CRD カタログの継続更新も無い |
| CRD スキーマを本リポジトリへベンダリング（同梱） | Istio のマイナーバージョン更新ごとに手動更新が要り、更新漏れが「検査しているが古いスキーマ」という気づきにくい形で腐る。`datreeio/CRDs-catalog` は継続更新されている外部カタログであり、本リポジトリが追随コストを負わない |
| `-ignore-missing-schemas` を既定で付ける | 「スキーマが無い」ときに緑を返してしまい、要点 3（fail-closed）と矛盾する。将来 CRD が増えて対応スキーマが無いとき、検証が静かに何もしなくなる |

## フォローアップ

- [x] `ci.yml` へ `kubeconform` の導入ステップを足す（curl によるバイナリ取得＋チェックサム検証。
      版 pin は `v0.8.0`）。本 PR 2 番目のコミット（`a7296a7`）で完了
- [x] `scripts/scripts.repo.test.js` の CI 突合テストへ kubeconform 導入の検査を追加する。
      同じく `a7296a7` で完了（変異試験 2 本を実測）

上記 2 点は「#783 後半」（統合スタックを CI で起こす経路。#466 の土台）とは別物である —— 本 PR が
足したのは前半（chart / overlay の検証ジョブ）への kubeconform 導入であり、後半は依然未着手のまま
（`.ai-context/specs/20260821_issue-783_deploy-manifest-ci.md`「後半の切り分け」節を参照）。
