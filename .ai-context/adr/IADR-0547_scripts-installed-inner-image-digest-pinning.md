---
title: IADR-0547 scripts/ が入れる製品の chart・上流マニフェストの内側のイメージは、chart は values、上流マニフェストは kustomize の images 置換、k3s は起動器の既定で `tag@sha256:…` に固定し、版の env は固定と同じ値しか受けない。検査は check-image-digests.js の延長と描画結果の検査で行う
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0135, ADR-0107, ADR-0005, ADR-0007, ADR-0008, ADR-0023, ADR-0095, ADR-0110, IADR-0514, IADR-0519, IADR-0240]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0135_infrastructure-inspection-population-and-immediate-version-pinning.md 決定 2（digest は点検の追補で他の製品と同じ扱い）
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 3（digest 固定）
related_specs:
  - ../specs/20261015_1858_pin-inner-image-digests.md
---

# IADR-0547: scripts/ が入れる製品の内側のイメージの digest 固定（#1858）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-15
- 決定者: claude（固定の手段・版の env の扱い・検査の範囲）

## 起点・関連

- 起点 issue: **#1858**（#1843 の後続）。[IADR-0519](./IADR-0519_scripts-installed-products-version-pinning.md) 決定 3（内側のイメージの digest は固定せず別に回す）と残余 1 を引き継ぐ。
- 計画: **ADR-0135 決定 2**（digest は点検の追補で他の製品と同じ扱い）・**ADR-0107 決定 3**（イメージは digest で固定する）。
- 前提: [IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（表記 `repo:tag@sha256:<index digest>`・検査器 `check-image-digests.js`・Renovate は採らない）、[IADR-0240](./IADR-0240_deploy-manifest-schema-validation.md)（描画結果のスキーマ照合）。
- 基点コミット: MSP `origin/develop` `2fa97acd`。

## コンテキストと課題

6 製品（Istio・ESO・Reloader・cert-manager・Argo CD・k3s）は版で固定されているが、内側のイメージは tag の参照のままだった（相異なる参照 11。母集合の引き方は作業仕様書）。
本リポジトリに参照の行があるのは k3s だけで、残りは chart の既定値か上流のマニフェストの中にある。`check-image-digests.js` は `deploy/` と `src/` の字面しか読まない。

## 検討した選択肢

### 固定の手段

1. **chart は values で上書き、上流マニフェストは kustomize の images 置換、k3s は起動器の既定**（**採用**）
2. helm の post-render（kustomize を post-renderer に挟む）で全製品を同じ形にする — chart の values で足りる 3 製品に余分な段を足す。post-render の失敗は helm の外で起き、`--wait` の失敗と見分けにくい。
3. 上流マニフェストをリポジトリへ取り込み（vendoring）、字面を書き換える — `install.yaml`（Argo CD）・`cert-manager.yaml` はそれぞれ数 MB 級で、版の更新のたびに巨大な差分になる。字面の `image:` を手で書き換えると、版の更新で上書きし忘れる。

chart の values で足りるかは製品ごとに実測した:

| 製品 | values の口 | 実測（`helm template`） |
| --- | --- | --- |
| Istio（istiod 1.30.4） | `pilot.image`・`global.proxy.image`・`global.proxy_init.image` は `/` を含めば**そのまま**使われる（`contains "/"` の分岐） | istiod の Deployment と、注入テンプレート（sidecar・init・gateway）の 3 箇所に `tag@digest` が入る。gateway chart は `image: auto` で注入される |
| ESO（2.8.0） | `image`・`webhook.image`・`certController.image` の `repository`・`tag`（`<repository>:<tag>` で描く。digest の口は無い） | `tag: v2.8.0@sha256:…` で 3 Deployment とも `tag@digest` |
| Reloader（2.2.17） | `image.repository`・`image.tag`（`image.digest` もあるが `<repository>@<digest>` で描き tag を落とす） | `tag: v1.4.22@sha256:…` で `tag@digest`。tag はラベルに使われない |

kustomize（kubectl 1.34 同梱の v5.7.1）は `images:` の項目に `newTag` と `digest` を両方書くと `<name>:<tag>@<digest>` で描く（実測）。描画は images 以外が上流のファイルと一致する（キーを整列した JSON の比較で差 0）。

### 版の上書きの env

1. **固定と同じ値のときだけ受け、違えば起動前に止める**（**採用**。`scripts/lib/pin-version.sh`）
2. 従前どおり上書きを受ける — 版だけを変えると、新しい chart / マニフェストに古い版のイメージが入る（Istio は 1.31 の istiod の chart に 1.30.4 の pilot が入る）。黙って食い違った組み合わせを入れる。
3. env を黙って無視して既定へ戻す — 与えた人はその版が入ると思っている。#1843 の「ブランチ名を拒否する」と同じく、止めて案内する方が誤解が残らない。

### 検査の範囲（issue の論点「check-image-digests の対象を描画結果まで広げるか」）

1. **字面の検査（`check-image-digests.js`）を kustomization の `images:` と `scripts/*.sh` の `*_IMAGE` の既定へ広げ、描画結果の検査は `check-deploy-manifests.js`（すでに overlay・chart を描画している）に足す**（**採用**）
2. `check-image-digests.js` が自分で描画する — 同検査器は「ネットワークも外部ツールも使わない・決定的」を設計の前提にしている（IADR-0514 決定 3）。描画には helm・kubectl と上流の取得が要る。
3. 上流の chart（Istio・ESO・Reloader）も CI で描画する — chart の取得（helm repo）を CI に足し、製品の列挙を検査器に持つことになる。values の口が効くことは手元の `helm template` で確かめ、版と tag の対は試験で突き合わせるに留める（残余 1）。

## 決定

### 決定 1: 固定の置き場

| 製品 | 置き場 |
| --- | --- |
| Istio | `deploy/istio/istiod-values-local.yaml`（`pilot.image`・`global.proxy.image`・`global.proxy_init.image`） |
| ESO | `deploy/local/vault/eso/external-secrets-values.yaml`（`image`・`webhook.image`・`certController.image` の `tag: <版>@sha256:…`） |
| Reloader | `deploy/local/reloader/values.yaml`（`image.tag: <版>@sha256:…`。従前の `--set image.tag` は外した） |
| cert-manager | `deploy/local/edge/cert-manager/kustomization.yaml`（release の URL を `resources:` に、`images:` に `newTag`・`digest`） |
| Argo CD | `deploy/argocd/install/kustomization.yaml`（版のタグの URL を `resources:` に、`images:` に argocd・dex・redis。経路を問わない置き場にし、手順書 3 か所も `-k` で当てる） |
| k3s | `scripts/k8s-local-up.sh` の `K3S_IMAGE` の既定（`rancher/k3s:v1.35.4-k3s1@sha256:…`） |

- 表記は IADR-0514 決定 1 のとおり `repo:tag@sha256:<multi-arch の index の digest>`。digest は 2026-10-15 に匿名で 2 回解決して一致し、#1843 の追補の記録とも一致した。
- k3s: k3d v5.8.3 が digest つきの参照を受けて pull・ノードの起動まで進むことを手元で確かめた（IADR-0519 の選択肢 3 が「実測できない」とした点）。`K3S_IMAGE` は参照全体の上書きであり、従前どおり受ける（与えた人が版と digest を一緒に決める）。

### 決定 2: 版の env は固定と同じ値しか受けない（IADR-0519 決定 1 の「`ARGOCD_VERSION` で上書きできる」を改める）

- `ISTIO_VERSION`・`ESO_CHART_VERSION`・`RELOADER_CHART_VERSION`・`CERT_MANAGER_VERSION`・`ARGOCD_VERSION` は `pin_version <変数> <固定の版> <置き場>` が決める。空か同じ値なら固定の版、違えば何もする前に exit 1。`stable` 等のブランチ名もここで止まる（#1843 の拒否を包含する）。
- `RELOADER_IMAGE_TAG` は廃止し、与えられたら止める（tag だけの上書きは固定を外す）。
- `istio-edge-up.sh` の `ISTIO_VERSION` も同じ口を通す（gateway は istiod の注入で proxyv2 を得るので、版は istiod と揃っていなければならない）。

### 決定 3: 検査

- `check-image-digests.js` は kustomization（`kustomization.yaml` / `.yml` / `Kustomization`）の `images:` の項目と、`scripts/*.sh` の `<名前>_IMAGE="${<名前>_IMAGE:-<ref>}"` の既定も読む。表記・例外・`[digest-mismatch]` は従前の規則のまま効く（Istio の proxyv2・ESO の 3 参照は同じ repo:tag を複数か所に書くので、片側だけの digest の差し替えは赤になる）。
- `check-deploy-manifests.js` は描画結果（overlay・chart）の外部イメージが digest つきであることも見る（要点 6）。上流が版の更新でイメージを**足した**とき、kustomization の `images:` に項目が無いまま tag だけで描かれるのは字面では見えないため。
- 版と tag・digest の対（chart の版と values の tag、URL の版と `newTag`）は `scripts/scripts.repo.test.js` が突き合わせ、片側だけの更新の変異で赤を確かめる。

### 決定 4: kubeconform は `CustomResourceDefinition` という kind そのものを照合しない

- 上流のマニフェストを読む overlay は CRD の定義を同梱するが、CRD という kind のスキーマは既定のカタログにも CRD カタログにも無い（2026-10-15 に両方 404）。IADR-0240 の fail-closed をそのまま当てると検査が恒久的に赤になる。
- 照合しないのは CRD の**定義**だけで、CRD で定義された kind のリソース（Certificate・ExternalSecret 等）は従前どおり照合し、スキーマが無ければ fail-closed のままである。CRD の定義は上流の字面のまま（kustomize は images 以外を変えない）で、apiserver が apply の時点で検証する。

## 統制と現在の実現手段

| | |
| --- | --- |
| **統制** | ADR-0107 決定 3（digest 固定）を `scripts/` が入れる 6 製品にも当てる（ADR-0135 決定 2） |
| **現在の実現手段** | 11 参照を `deploy/` の values・kustomization と起動器の既定に `tag@sha256:…` で置いた。字面は `check-image-digests.js`、kustomization の描画結果は `check-deploy-manifests.js`（CI の `static-checks`）、版と tag の対は `scripts/scripts.repo.test.js` が見る |
| **配備までの暫定手段** | 上流の chart（Istio・ESO・Reloader）の描画は CI で見ていない（values の口が効くことは手元の `helm template` で確かめた。残余 1） |

## 結果

- 良い影響: 同じタグの中身の差し替えで 6 製品の入るものが変わらなくなる（基準 B の残りの解消）。版だけを変える操作が起動前に止まる。
- 悪い影響 / トレードオフ: 版の取り込みは版と digest の対の更新になる（他の製品と同じ）。`check-deploy-manifests.js` が上流のマニフェスト（GitHub の release・raw）を取得するようになり、CI のその段の外部依存が増える（スキーマの取得で既に外へ出ている段である）。

## 残余

1. **上流の chart（Istio・ESO・Reloader）の描画は CI で見ていない。** chart の版を上げて values の口の形が変わった（例: `global.proxy.image` の解釈が変わる）とき、字面と対の試験は通っても描画は tag だけになり得る。版を上げる作業で `helm template` を当てて確かめる（運用仕様書の点検の手順）。
2. **Argo CD・cert-manager の上流のマニフェストは git のタグ・release の URL で読み、マニフェスト自体のハッシュは照合しない**（IADR-0519 残余 4 と同じ）。内側のイメージは digest で固定されたので、付け替えで変わり得るのはイメージ以外の定義である。
3. `K3S_IMAGE` を tag だけで与えれば固定は外れる（運用者の明示の選択として受ける）。
4. 本作業では稼働クラスタで起動して確かめていない（k3d は作業環境でクラスタの起動を完了できなかった。digest つきの参照の受理と pull までは実測）。integration-stack の dispatch と PoC の引け後の起動で確かめる。
5. 既存の注入済みの Pod のサイドカーは、作り直すまで旧い文字列（tag だけ）のまま動く（中身は同じ）。

## 関連

- 作業仕様書: [20261015_1858_pin-inner-image-digests](../specs/20261015_1858_pin-inner-image-digests.md)
- 実装: `deploy/istio/istiod-values-local.yaml`・`deploy/local/vault/eso/external-secrets-values.yaml`・`deploy/local/reloader/values.yaml`・`deploy/local/edge/cert-manager/kustomization.yaml`・`deploy/argocd/install/kustomization.yaml`・`scripts/k8s-local-up.sh`・`scripts/istio-edge-up.sh`・`scripts/lib/pin-version.sh`・`scripts/check-image-digests.js`・`scripts/check-deploy-manifests.js`・試験（`k8s-local-up.test.js`・`scripts.repo.test.js`）・`docs/operations/operations.md` §インフラ製品の点検
