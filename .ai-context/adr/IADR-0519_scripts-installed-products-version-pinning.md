---
title: IADR-0519 scripts/ が入れる製品は版で固定する。Argo CD は stable が指していた版のタグの URL、k3s はスクリプトの既定を単一の情報源とし、chart・上流マニフェストの内側のイメージの digest 固定は理由つきで別に回す
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0135, ADR-0107, ADR-0112, ADR-0007, ADR-0008, IADR-0514, IADR-0248, IADR-0077, IADR-0258]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0135_infrastructure-inspection-population-and-immediate-version-pinning.md 決定 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 3（digest 固定）・決定 5
related_specs:
  - ../specs/20261009_1843_pin-argocd-k3s-inspection-population.md
---

# IADR-0519: scripts/ が入れる製品の版の固定（#1843）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: claude（版の選び方・情報源の置き場・digest の扱い）

## 起点・関連

- 起点 issue: **#1843**（planning#750 の裁定 5）。計画 **ADR-0135** 決定 2（版が固定されていない製品は即時に固定する。digest は点検の追補で他の製品と同じ扱い）。
- 前提: [IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（`deploy/` の digest 固定・初回の点検。残余 1 が本件）、[IADR-0248](./IADR-0248_integration-stack-ci-readiness-gate.md) 決定 6（k3s の pin を `K3S_IMAGE` の opt-in で足した）、[IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md)（Argo CD の server-side apply）。
- 基点コミット: MSP `origin/develop` `afa9b917`。

## コンテキストと課題

`scripts/k8s-local-up.sh` は Argo CD を `argo-cd/stable/manifests/install.yaml` から入れていた。`stable` はブランチであり、取得のたびに中身が変わり得る。k3s は `K3S_IMAGE` を与えたときだけ `--image` を付け、既定は k3d が同梱する版だった。CI の 2 ワークフローは同じ値を env で与えていた。

Istio・ESO・Reloader・cert-manager は chart / release の版で固定されているが、中のイメージは tag の参照である。`check-image-digests.js` は `deploy/` と `src/` だけを走査し、これらを見ない。

## 検討した選択肢

### Argo CD の版

1. **`stable` が今指している版（v3.5.4）**（**採用**）— 両 URL の `install.yaml` はバイト一致（sha256 `1feb02cc…e010`。2026-10-09 実測）。固定の前後で入るものが変わらない。
2. 最新のリリースを別に選ぶ — 2026-10-09 時点で最新のタグも v3.5.4 であり、結果は同じ。ただし「固定の作業で版を上げない」原則を明示するため、1 の言い方を採る。

### k3s の版の情報源

1. **スクリプトの既定 1 か所。ワークフローは与えない**（**採用**）— 同じ値を 2 か所に持つと片側だけ動く。ワークフローに残すと「既定は誰が決めるか」が 2 つになる。
2. スクリプトの既定とワークフローの両方に書き、一致をテストで突き合わせる — 一致の検査が要るのは情報源が 2 つあるからであり、1 で検査ごと要らなくなる。

### chart・上流マニフェストの内側のイメージの digest

1. **本件では版の固定（chart / release / タグ）に留め、digest は追補の点検に「固定していない」と理由つきで記録して別に回す**（**採用**）
2. 本件で全製品の digest を固定する — 製品ごとに values の上書き（Istio は `global.hub`/`tag` に digest を渡す口が無く、proxy の注入テンプレートにも及ぶ）か post-render が要り、変更面が 6 製品の起動経路の全体に広がる。issue #1843 の受け入れ基準（版の固定・点検の追補）を越える。
3. k3s の `--image` だけ digest つきにする — 本リポジトリに参照の行がある唯一の製品だが、k3d が digest つきの参照で起動することを本作業では実測できない（実行機にクラスタ・docker が無い）。integration-stack を壊す危険を測らずに入れない。

## 決定

### 決定 1: Argo CD は版のタグの URL から入れる

- `ARGOCD_VERSION="${ARGOCD_VERSION:-v3.5.4}"`、URL は `argo-cd/${ARGOCD_VERSION}/manifests/install.yaml`。
- **`vX.Y.Z` の形以外は `ARGOCD=1` のとき起動前に拒否する**（`stable`・`master` を与えると固定が外れる）。
- 手順書（`deploy/argocd/README.md`・`deploy/local/argocd/README.md`・`docs/how-to/deployment.md`）の URL も同じ版にする。リポジトリ内の Argo CD の install の URL が既定と同じ版であることは `scripts.repo.test.js` が見る（凍結記録は除く）。
- 版を上げるときは全箇所を同じ版へ変える（検査が片側だけの更新を止める）。

### 決定 2: k3s はスクリプトの既定で固定する（IADR-0248 決定 6 の「opt-in・未設定なら 1 バイトも変えない」を改める）

- `K3S_IMAGE="${K3S_IMAGE:-rancher/k3s:v1.35.4-k3s1}"` とし、`--image` を常に付ける。空を与えても既定へ戻る（浮動にはできない）。
- 版は CI がすでに実証していた `v1.35.4-k3s1`（IADR-0248 決定 6・手元の Rancher Desktop と同じ）。固定の作業で版を上げない。
- `integration-stack.yml`・`cutover-rehearsal.yml` は `K3S_IMAGE` を与えない。実効の `k3d cluster create` の引数は変わらない。
- **Rancher Desktop 経路は対象外**（内蔵 k3s の版は Rancher Desktop の設定が決め、スクリプトは選ばない）。

### 決定 3: chart・上流マニフェストの内側のイメージは digest で固定しない（理由つきの記録）

- 対象: Istio（pilot・proxyv2）・ESO・Reloader・cert-manager（3 つ）・Argo CD（argocd・dex・redis）・k3s。
- 点検の追補（`docs/operations/operations.md` §点検の記録）の基準 B に「版で固定・digest は固定していない」と書き、各イメージの index の digest（2026-10-09 に匿名で 2 回解決して一致）を記録する。
- `check-image-digests.js` の走査は広げない（参照の行が本リポジトリに無い）。

## 結果

- 良い影響: Argo CD・k3s は同じ起動で同じ版が入る（ADR-0135 決定 2）。Argo CD の版がブランチ名へ戻ることをスクリプトとテストの 2 段で止める。k3s の版の情報源が 1 つになる。
- 悪い影響 / トレードオフ: 版の取り込み（Argo CD・k3s のパッチ）が手作業になる。k3s v1.35 系はすでに v1.35.9 まで出ており、固定した v1.35.4 は修正を取り込んでいない（次回の点検で扱う）。

## 残余

1. **chart・上流マニフェストの内側のイメージの digest 固定**（決定 3）。基準 B の「同じタグの中身の差し替えを検知できない」は 6 製品で残る。#1858 で扱う。
2. **Rancher Desktop 経路の k3s の版**はスクリプトの統制の外にある（決定 2）。
3. **既存のクラスタは作り直すまで版が変わらない**（k3d は `cluster create` の時点でイメージを決める）。
4. Argo CD の `install.yaml` は git のタグで固定したが、タグの付け替えは検知しない（manifest の sha256 の照合は入れていない）。
5. 本作業ではクラスタで起動して確かめていない（stub の試験と静的検査のみ）。integration-stack の dispatch で確かめる。

## 関連

- 作業仕様書: [20261009_1843_pin-argocd-k3s-inspection-population](../specs/20261009_1843_pin-argocd-k3s-inspection-population.md)
- 実装: `scripts/k8s-local-up.sh`（`ARGOCD_VERSION`・`K3S_IMAGE`）・`scripts/k8s-local-up.test.js`・`scripts/scripts.repo.test.js`・`.github/workflows/{integration-stack,cutover-rehearsal}.yml`・`docs/operations/operations.md` §インフラ製品の点検
