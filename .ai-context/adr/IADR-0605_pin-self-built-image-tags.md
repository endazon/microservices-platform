---
title: IADR-0605 自製イメージは不変のタグ（git の sha）を global.image.tag で必ず渡し、無ければ chart の描画を落とす。経路B は作業ツリーの sha でビルドして同じ値を渡す
type: impl-adr
status: Accepted
related_ids: [NFR-20, ADR-0007, ADR-0008, IADR-0088, IADR-0369, IADR-0066]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-20）
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md
related_specs:
  - ../specs/20261023_2132_pin-image-tags.md
---

# IADR-0605: 自製イメージのタグを不変に固定し、未指定なら描画を落とす（#2132）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2132。親 #2047 の指摘 M-27。方式は利用者の裁定〔案 A〕）

## 起点・関連

- 関連する計画書 ID: NFR-20（デプロイ「サービス単位で独立デプロイ、ロールバック可能」・GitOps（ArgoCD））。
  計画 ADR-0007（GitOps。ArgoCD ＋ Helm ＋ Harbor）、ADR-0008（k3s）。
- 改める実装 ADR: [IADR-0088](./IADR-0088_image-reference-redeploy-safety.md) 決定 1（自製イメージの既定 `tag: latest` を「CD 上書き用の
  placeholder」として維持する）。同決定の「`IfNotPresent` を既定に保ち `Always` にしない」は維持する。
- 関連: [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（check-stack-ready の G11）、
  [IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（経路B のローカルビルド）。
- 作業仕様書: [20261023_2132_pin-image-tags](../specs/20261023_2132_pin-image-tags.md)（母集合の表）。

## コンテキストと課題

chart は全サービス（`services.*` 18 件と `frontend`）を `tag: latest` ＋ `pullPolicy: IfNotPresent` で描き、template も `default "latest"` で
倒していた。IADR-0088 は「CD が一意のタグを `--set` で渡す」契約を文書で置いたが、渡さなくても描画は通り、稼働 PoC（経路B）も
`k3d-local/<image>:latest` を作り直して上書きしていた。同じタグの中身が替わってもノードのキャッシュが古い中身を配り続け、
どの版が動いているかを宣言から読めず、前の版を名指しして戻すこともできない（NFR-20 が成り立たない）。
ArgoCD の Application にもタグを注入する口が無かった（`config.appliedBy` だけ）。

## 決定

### 決定 1: chart は自製イメージのタグを持たない。`global.image.tag` が無ければ描画を落とす

- 共通のタグ `global.image.tag`（既定は空）と、サービスごとの上書き `services.<名>.tag` / `frontend.tag`（既定は書かない）を持つ。
- 自製イメージの 2 箇所（`deployment.yaml`・`frontend.yaml`）は共通の部品 `msp.selfBuiltImageTag`（`templates/_image.tpl`）を通す。部品は:
  - どちらも空なら `required` で落とす（何を渡せばよいかを告げる）。**`latest` へ黙って倒さない。**
  - `latest` の明示も落とす（不変にならない）。
  - 文字列でない値を落とす —— `--set` は数字だけの sha（先頭が 0 でない）を int として読む。`--set-string`（ArgoCD は `forceString: true`）で渡す。
  - Docker のタグの形（`[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}`）でない値を落とす（描いても pull で落ちるだけなので、描画で止める）。
- digest（`@sha256:`）での固定は本決定に含めない（自製イメージを push するレジストリがまだ無い。ADR-0007 の Harbor は未配備）。

### 決定 2: pullPolicy は `IfNotPresent` のままにする

タグが不変なら、タグが変わると Pod の template が変わって入れ替わり、新しいタグはノードに無いので取りに行く。`Always` は要らない。
経路B は擬似レジストリ（`k3d-local`）に取り込んだイメージを使うので `Always` にできない（IADR-0088 の理由のまま）。
タグが無いときは描画そのものが落ちるので、「latest ＋ IfNotPresent で古い中身を配る」へ倒れる経路は残らない。

### 決定 3: 経路B は作業ツリーの git の sha でビルドし、同じ値を helm へ渡す

- タグは `scripts/lib/local-image-tag.sh` が決める: `IMAGE_TAG` があればそれ（決定 1 と同じ規則で検査する）、無ければ HEAD の sha（12 桁）。
  **作業ツリーに変更があれば `<sha>-dirty-<差分の sha256 の先頭 8 桁>`** にする（追跡下の差分・submodule の差分・追跡していない新しいファイル）。
  HEAD の sha だけにすると中身の違うイメージが同じタグで取り込まれ、latest と同じ事故になる。差分が同じなら同じ値（再実行でタグが増えない）。
- `k8s-local-up.sh` は冒頭（副作用より前）でタグを決めて export し、[2/7]（`k8s-local-images.sh`）が同じ値でビルド・取り込み、
  [6/7] が `--set-string "global.image.tag=${IMAGE_TAG}"` を渡す。決められなければ何もする前に止まる。
- `--reuse-values` の後続（メッシュの mTLS の方式の切り替え等）はタグを引き継ぐ。**この変更より前に立てたクラスタは稼働リリースに
  タグが無い**ので、最初に起動器を流してタグを入れる（運用文書の「初回の切り替え」）。
- ロールバックは前のタグで `helm upgrade --reuse-values --set-string global.image.tag=<前のタグ>`（前のタグのイメージはノードに残る）。

### 決定 4: ArgoCD は Application の `helm.parameters` の `global.image.tag` を正とし、Git で更新する

- `deploy/argocd/application.yaml` に `global.image.tag`（`value: ""`・`forceString: true`）を置く。空のままでは同期が描画の失敗で止まる
  （latest で配らない）。配備する人／CD が値をビルドした sha へ改める PR をマージし、Application を `kubectl apply` で適用する
  （Application は ArgoCD の外から一度だけ入れたもので、ArgoCD は自分を管理していない）。サービス単位は `services.<名>.tag` を足す。
- ロールバックは前の sha へ値を戻す（`git revert` → 適用）。`argocd app set --helm-set-string` は急ぐときだけ使い、同じ値を Git へも入れる
  （入れないと、Application を適用し直したときに Git の値へ戻る）。
- Argo CD Image Updater は導入しない（利用者裁定。案 B は不採用）。

### 決定 5: 描画だけの試験・検査器は固定のタグを渡し、稼働の検査は稼働リリースの宣言で描く

- 描画だけの試験・検査器（helm の試験 16 本・`check-deploy-manifests.js`・`check-bff-multi-replica-session.js --plan`）は
  `scripts/lib/helm-render-tag.js` の固定のタグを chart の直後に渡す（配備には使わない値）。
- `check-stack-ready.js` の G11（宣言と稼働のイメージ参照の一致）は、稼働リリースの `global.image.tag`（`helm get values`。`IMAGE_TAG` で上書き可）で
  chart を描く。宣言が無いリリースは失敗にする（latest のまま動いていることを見逃さない）。サービスごとの上書きは宣言に含めない（#1088 と同じく、
  共通のタグから外れたイメージを乖離として出す）。

## 却下した代替案

- **values-local.yaml に固定のタグを書く**: 経路B の版が作業ツリーと結び付かず、ビルドした中身とタグが別々に動く（latest と同じ）。作業ツリーの sha で
  ビルドして同じ値を渡せるので採らない。
- **タグが無いとき pullPolicy を `Always` に切り替えて描く**: 描画は通るが latest を配る形が残り、経路B では起動できない。描画で止める。
- **Argo CD Image Updater（案 B）**: 利用者裁定で不採用。
- **`argocd app set` だけで更新する**: 値が Git に残らず、Application の再適用で消える。Git の parameters を正にする。
- **HEAD の sha だけをタグにする（変更のある作業ツリーでも）**: 同じタグに中身の違うイメージが入る。差分の hash を付ける。

## 結果

- 良い影響: 動いている版が宣言（`helm get values` / Application の parameters）から読め、前の版のタグを渡し直せば戻る。タグを渡し忘れた配備は
  描画で止まる（latest で配らない）。
- 悪い影響 / トレードオフ:
  - ノードのイメージは版ごとに残る（latest の上書きと違い、古い版が消えない）。容量の逼迫には `crictl rmi --prune` 等で掃除する（運用文書）。
  - この変更より前に立てた稼働クラスタは、最初に起動器を流すまで `--reuse-values` だけの upgrade が描画で止まる（何も変えずに止まる）。
  - ArgoCD の Application は `kubectl apply` で入れ直すまで Git の値の変更が届かない（Application を自己管理していない既存の構成のまま）。
- 計画への環流: 無し（NFR-20・ADR-0007 の範囲の実装手段であり、計画の記述と矛盾しない）。

## 検証

| # | 内容 | 結果 | 根拠 |
| --- | --- | --- | --- |
| 1 | 既定・values-local の描画で、自製イメージの全件（既定 16・values-local は宣言から数えた件数）が `global.image.tag` を参照・latest 0 件 | 通った | `helm-image-tag.test.js` |
| 2 | タグなし（既定・values-local）・latest・サービスの上書きに latest・`--set` の数・タグの形でない値で描画が落ち、理由がタグ | 通った | 同上 |
| 3 | 変異: `default "latest"` を戻す／`required` を外す／latest の拒否を外す → それぞれ判定が赤。描画の 1 件を latest へ替える → 赤 | 赤を確認 | 同上（試験に組み込み・毎回走る） |
| 4 | ローカルのタグ: clean は sha、変更ありは `-dirty-<8 桁>`・同じ差分は同じ値・戻せば sha・未追跡のファイルを数える・`IMAGE_TAG=latest` 等を拒む・git が無ければ止まる | 通った | 同上 |
| 5 | 起動器: [6/7] の helm に `--set-string global.image.tag=<タグ>`、[2/7] のビルド・取り込みが同じタグ、`IMAGE_TAG=latest` は副作用の前に止まる、`IMAGE_TAG` 未指定は git の sha | 通った | `k8s-local-up.test.js`（#2132 の節） |
| 6 | 既存の helm 試験 16 本・`check-deploy-manifests.js`・`check-stack-ready.js --self-test`・`k8s-local-up.test.js` | 通った | 手元実行（2026-10-23） |
