---
title: 作業仕様書 — scripts/ が入れる 6 製品の chart・上流マニフェストの内側のイメージを digest で固定する（#1858）
type: spec
status: done
related_ids: [NFR, ADR-0135, ADR-0107, ADR-0005, ADR-0007, ADR-0008, ADR-0023, ADR-0095, ADR-0110, IADR-0547, IADR-0519, IADR-0514]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0135_infrastructure-inspection-population-and-immediate-version-pinning.md 決定 2（digest は点検の追補で他の製品と同じ扱い）
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 3（digest 固定）
issue: "#1858"
---

# 作業仕様書 — chart・上流マニフェストの内側のイメージの digest 固定（#1858）

> 本仕様書は実装着手前に作成した（着手 2026-10-15）。判断の記録は **IADR-0547** に置く。
> 基点は MSP `origin/develop` `2fa97acd`。🔴 **稼働中のクラスタには何も実行しない。** 上流へは匿名の HEAD / GET（manifest・chart・install.yaml）だけを送った。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0135 決定 2**（版が固定されていない製品は即時に固定する。digest は点検の追補で他の製品と同じ扱い）、**ADR-0107 決定 3**（イメージは digest で固定する）。名指しの製品は ADR-0005（Istio）・ADR-0007（Argo CD）・ADR-0008（k3s）・ADR-0023（cert-manager）、前提として引くのは ADR-0095（ESO）・ADR-0110（Reloader）。
- 非機能要件: 無採番（工程の統制。メタ作業）。
- 起点 issue: **#1858**（#1843 の後続。IADR-0519 残余 1）。前提: [IADR-0514](../adr/IADR-0514_infra-image-digest-pinning-and-checker.md)（表記・検査器）、[IADR-0519](../adr/IADR-0519_scripts-installed-products-version-pinning.md)（版の固定・決定 3＝digest は別 issue）。

## 受け入れ基準（issue #1858）

| # | 基準 | 写像 |
| --- | --- | --- |
| 1 | 6 製品の内側のイメージ参照が、すべて digest つきで描画される（または固定しない理由が製品ごとに記録されている） | 下の母集合の 11 参照すべてを `deploy/` の values・kustomization と `scripts/k8s-local-up.sh` の既定に `tag@sha256:…` で置く。`check-image-digests.js` が読む（自己試験・本走）。描画は手元の `helm template` / `kubectl kustomize` で確かめ、kustomization の 2 つは CI の `check-deploy-manifests.js` が描画結果を検査する |
| 2 | タグを据え置いたまま digest だけ違うものへ差し替える変異を、検査が赤にする | `[digest-mismatch]`（同じ `repo:tag` の参照が複数ある Istio・ESO）と、版と digest の対の突き合わせ（`scripts.repo.test.js`）。変異で赤を確かめる（下の「変異」） |
| — | 手段を決めて IADR に記録する（IADR-0519 決定 3 を引き継ぐ） | IADR-0547 |
| — | `check-image-digests` の対象を描画結果まで広げるかを判断する | IADR-0547 決定 3 |
| — | 暫定の記録（operations.md の B 列の digest）を固定の実体へ置き換える | `docs/operations/operations.md` §インフラ製品の点検 |

## 母集合（規則 9・10。issue 本文の「13 参照」は転記せず、ここで引き直した）

引き方: `grep -nE 'helm upgrade --install|kubectl apply .*https|k3d cluster create' scripts/*.sh` で 6 製品を引き、各製品を**配備と同じ版・同じ values**で描画して `image:` を数えた（2026-10-15）。

| 製品 | 入れ方（版） | 描画で現れたイメージ（相異なる参照） | 数 |
| --- | --- | --- | --- |
| Istio | chart `istio/base`・`istio/istiod`・`istio/gateway` 1.30.4（`k8s-local-up.sh`・`istio-edge-up.sh`） | `registry.istio.io/release/pilot:1.30.4`（istiod）・`registry.istio.io/release/proxyv2:1.30.4`（注入テンプレートの `istio-proxy` と `istio-init`。gateway chart は `image: auto` で注入される） | 2 |
| External Secrets Operator | chart 2.8.0 | `ghcr.io/external-secrets/external-secrets:v2.8.0`（本体・webhook・cert-controller の 3 Deployment が同じ参照） | 1 |
| Reloader | chart 2.2.17・`--set image.tag=v1.4.22` | `ghcr.io/stakater/reloader:v1.4.22` | 1 |
| cert-manager | release v1.21.1 の `cert-manager.yaml` | `quay.io/jetstack/cert-manager-{controller,webhook,cainjector}:v1.21.1` | 3 |
| Argo CD | `install.yaml` v3.5.4 | `quay.io/argoproj/argocd:v3.5.4`（8 か所）・`ghcr.io/dexidp/dex:v2.45.1`・`public.ecr.aws/docker/library/redis:8.2.3-alpine` | 3 |
| k3s | `k3d cluster create --image` | `rancher/k3s:v1.35.4-k3s1` | 1 |
| **計** | | | **11** |

- **issue の「13」との差**: 2026-10-09 の追補の表も同じ 11 種を列挙している。13 は数え方（ESO の 3 Deployment を別に数える等）による差とみて、本件は**相異なる参照 11** を母集合とする（全件を固定するので、数え方の差で漏れは生じない）。
- **除外（理由つき）**:
  - istiod chart の `files/grpc-simple.yaml` の `busybox:1.28` と `files/agentgateway.yaml` の agentgateway — gRPC プロキシレス・agentgateway の注入テンプレートであり、本リポジトリは使っていない（注入の注釈を付けていない）。描画される ConfigMap の中のテンプレート文字列であって、Pod のイメージにならない。
  - `scripts/` の他の行（`docker run` 等）にイメージの参照は無い（`grep -nE 'docker (run|pull)|kubectl run|--image[ =]' scripts/*.sh scripts/lib/*.sh` で 0 件。`backup-restore-drill.sh:338` は試験の期待文字列）。
  - CI のワークフローのイメージ（`services:`・`container:`）は `scripts/` が入れる製品ではなく本件の対象外。

## digest の実測（2026-10-15T14:42Z。匿名で 2 回解決して一致）

手順は運用仕様書 §digest の解決と更新（匿名トークン → `HEAD /v2/<repo>/manifests/<tag>`・Accept に index 型 → `docker-content-digest`）。11 参照とも index / manifest list 型で、**2 回とも一致し、2026-10-09 の追補の B 列の前方 8 桁とも一致した**（上流でタグの中身は差し替わっていない）。

| 参照 | digest |
| --- | --- |
| `registry.istio.io/release/pilot:1.30.4` | `sha256:c236c1df5cc127fe193e5a17d8ece9cdd0dc17c5d89b4e20baf01d464f029dce` |
| `registry.istio.io/release/proxyv2:1.30.4` | `sha256:43b6aeab7428470d3d0ea6b6f0bc217e5b36df2b279bba337643df48590226d9` |
| `ghcr.io/external-secrets/external-secrets:v2.8.0` | `sha256:24c0dd3699e0988520afd2218612758cd97d1f702757b5b4fcf89adaa33ef679` |
| `ghcr.io/stakater/reloader:v1.4.22` | `sha256:def2480040adc164b3a57340e77674eadfd88fb7f0593df25e93297f29c7fd7c` |
| `quay.io/jetstack/cert-manager-controller:v1.21.1` | `sha256:416a2d76870d996460e62bd7f521bf14fa017be9e3e904aab92163a331fcb61a` |
| `quay.io/jetstack/cert-manager-webhook:v1.21.1` | `sha256:d8b3961b51c8c7320633f8208dc46bf88aa13804d0f7cbe48a096b2c523cee42` |
| `quay.io/jetstack/cert-manager-cainjector:v1.21.1` | `sha256:ccf6b919ec0500745a47a910118f834f9636d0aac1ff221245cd2557ed8c7c98` |
| `quay.io/argoproj/argocd:v3.5.4` | `sha256:49dff79439bb38b1b942b19a113fe1fec7e6b6c671ddf9fe6a4a7c46bde0b77b` |
| `ghcr.io/dexidp/dex:v2.45.1` | `sha256:8499afd690c437f52301efd2b05b2455da5bd2dfc20332cd697dc9937f808462` |
| `public.ecr.aws/docker/library/redis:8.2.3-alpine` | `sha256:08ad0b1d280850169a790dba1393ff7a90aef951fc19632cf4d3ce4f78e679ba` |
| `docker.io/rancher/k3s:v1.35.4-k3s1` | `sha256:475e036b3fd595472c13ec708c148ffd95459d5f9e40e6df76ba4d5b27098570` |

## 方式（詳細は IADR-0547）

| 製品 | 固定の置き場 | 描画の確かめ |
| --- | --- | --- |
| Istio | `deploy/istio/istiod-values-local.yaml` の `pilot.image`・`global.proxy.image`・`global.proxy_init.image`（`/` を含む値は chart がそのまま使う） | `helm template` で istiod の Deployment と注入テンプレート（sidecar・init・gateway）の 3 箇所が `tag@digest` |
| ESO | 新規 `deploy/local/vault/eso/external-secrets-values.yaml` の `image`・`webhook.image`・`certController.image`（`tag: v2.8.0@sha256:…`） | `helm template` で 3 Deployment が `tag@digest` |
| Reloader | 新規 `deploy/local/reloader/values.yaml` の `image`（`tag: v1.4.22@sha256:…`。chart の `image.digest` は tag を落とすので使わない） | `helm template` |
| cert-manager | 新規 `deploy/local/edge/cert-manager/kustomization.yaml`（release の URL ＋ `images:` の `newTag`・`digest`） | `kubectl kustomize`（CI の `check-deploy-manifests.js` も描画する） |
| Argo CD | 新規 `deploy/argocd/install/kustomization.yaml`（版のタグの URL ＋ `images:`） | 同上 |
| k3s | `scripts/k8s-local-up.sh` の `K3S_IMAGE` の既定を `tag@digest` に | `k3d cluster create --image <tag@digest>` を手元で実行し、k3d が参照を受けて pull・ノード起動まで進むことを確かめた（下の「k3d の実測」） |

- 版の上書きの env（`ISTIO_VERSION`・`ESO_CHART_VERSION`・`RELOADER_CHART_VERSION`・`RELOADER_IMAGE_TAG`・`CERT_MANAGER_VERSION`・`ARGOCD_VERSION`）は、**固定と同じ値のときだけ受け、違えば起動前に止める**（版だけを変えると固定した digest と食い違う組み合わせが入る）。`K3S_IMAGE` は参照全体の上書きなので従前どおり受ける。
- 検査器 `check-image-digests.js` の延長: kustomization の `images:` の項目と、`scripts/*.sh` の `<名前>_IMAGE="${<名前>_IMAGE:-<参照>}"` の既定を読む。`check-deploy-manifests.js` は描画結果の外部イメージが digest つきであることを見る（kustomization の `images:` から漏れた上流の新しいイメージを拾う）。

## k3d の実測（IADR-0519 の選択肢 3 が「実測できない」とした点）

- k3d v5.8.3 で `k3d cluster create t1858 --image rancher/k3s:v1.35.4-k3s1@sha256:475e…8570` を実行した。**k3d は参照を受け、イメージを pull し、ノードのコンテナを作って起動した**（`docker image ls --digests` で同じ digest）。
- その後の CoreDNS の待ちで時間切れになったが、**tag だけの参照（`rancher/k3s:v1.35.4-k3s1`）でも同じ箇所で同じく時間切れになった**（実行機のコンテナの中で k3s が動かない環境の制約）。digest つきの参照に固有の失敗ではない。確認後にクラスタとイメージは消した。
- integration-stack の dispatch（実クラスタ）での確認はマージ前にオーケストレーターへ依頼する。

## 変異（赤になることを確かめる）

| 変異 | 期待 |
| --- | --- |
| ESO の `webhook.image` の digest だけを別の値に（tag 据え置き） | `check-image-digests` が `[digest-mismatch]` |
| Istio の `global.proxy_init.image` の digest だけを別の値に | 同上 |
| cert-manager の kustomization から `digest:` を消す | `[tag-only]` |
| `K3S_IMAGE` の既定から `@sha256:…` を外す | `[tag-only]`・`scripts.repo.test.js` |
| kustomization の `images:` から 1 項目（cainjector）を外す | `check-deploy-manifests` の描画結果の検査が赤 |
| 版の env を固定と違う値で与える（`ISTIO_VERSION=1.31.0` 等） | `k8s-local-up.sh` が起動前に非 0 |
| 固定の tag と chart / URL の版を食い違わせる（values の pilot だけ 1.30.5 に） | `scripts.repo.test.js` |

## 実装中に決めたこと（IADR-0547）

- Argo CD の kustomization は経路を問わない置き場 `deploy/argocd/install/` に置き、手順書 3 か所（`deploy/argocd/README.md`・`deploy/local/argocd/README.md`・`docs/how-to/deployment.md`）も URL の直接 apply をやめて `-k` で当てる（URL を直接 apply すると digest の固定を経ない）。
- Reloader の values は `deploy/local/reloader/values.yaml` に置いた（`deploy/local/vault/eso/` に置くと、起動器の opt-in トークンの検出力の試験で `stakater/reloader` が冗長になる）。
- `check-deploy-manifests.js` は kubeconform で `CustomResourceDefinition` という kind そのものを照合しない（スキーマがどのカタログにも無い。IADR-0547 決定 4）。

## 検証（2026-10-15〜16）

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-image-digests.js`（`--self-test`） | OK（外部イメージの参照 96 件・製品 35 種。自己試験 46 件） |
| `node scripts/check-deploy-manifests.js`（helm 3.19・kubectl 1.34・kubeconform 0.6.7） | OK（chart 1・overlay 19。新しい overlay 2 つを含む） |
| `node scripts/k8s-local-up.test.js` | 322 件通過 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 1087 件通過 |
| `shellcheck -x`（`k8s-local-up.sh`・`istio-edge-up.sh`・`lib/pin-version.sh`） | 既存の info（SC1091・SC2028）のみ |
| 文書系（`check-trace-blocks`・`check-adr-numbering`・`gen-knowledge-graph --check`・`check-reading-budget`・`check-plan-id-qualification`・`check-cross-repo-refs`・`check-doc-links`） | すべて OK |
| 変異（上の表の 7 種） | すべて赤（実ツリーを書き換えて検査器を走らせ、戻した） |

## PoC への影響

- 同じタグの同じ中身（digest は 2026-10-09 の記録と一致）なので、**入るものは変わらない**。参照の文字列が変わるため、次に `k8s-local-up.sh` を流すと istiod・ESO・Reloader・cert-manager・Argo CD の Pod テンプレートが変わり **1 回ずつ作り直される**（イメージの再取得は不要。中身が同じ）。サイドカーは注入済みの Pod を作り直すまで旧い文字列のまま（中身は同じ）。k3s は既存クラスタでは変わらない（作り直したときだけ）。
- インフラのイメージ参照を変える配備にあたるため、**大引け後に流す**（運用仕様書 §インフラのイメージ参照を変える配備）。

## 環流

- 計画への環流は不要（ADR-0135 決定 2 の実装。計画の決定は変えない）。
