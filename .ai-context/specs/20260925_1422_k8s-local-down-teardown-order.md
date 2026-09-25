---
title: k8s-local-down.sh を実測した撤去順で作り直す（webhook → finalizer → Helm → 名前空間 → CRD / PV → 検証）
issue: "#1422"
type: spec
status: draft
related_ids:
  - NFR
  - ADR-0006
  - IADR-0066
adr_refs:
  - IADR-0066
author: Claude Opus 5.5 (worker)
created: 2026-09-25
updated: 2026-09-25
---

# 作業仕様書: k8s-local-down.sh を実測した撤去順で作り直す（#1422）

## 起点

- issue #1422。2026-09-11 の稼働クラスタ（Rancher Desktop 内蔵 k3s）の完全クリーン実測で、旧 `k8s-local-down.sh`
  （`helm uninstall msp/ast/reloader` → `kubectl delete namespace …` だけ）は次で必ず止まる／残ることが分かった:
  ESO の Helm uninstall が CRD の Terminating で失敗／webhook が残ると finalizer の patch が失敗／ArgoCD の
  Application の finalizer で `argocd` が Terminating のまま／istio・cert-manager・argoproj・external-secrets の CRD・
  webhook・PV が残る。
- issue が求める順序: アプリの Helm → admission webhook → finalizer を空に → 残りの Helm → 名前空間（`--wait=false`・
  4 分待って Terminating なら中の finalizer 付きオブジェクトを空に）→ CRD と Bound でない PV → 検証（exit 1）。
  `set -e` にせず各段は続行。`--dry-run` 既定・`--apply` で実行。OpenD の PVC が消えることを冒頭に明示。
- 2026-09-14 の再構築（同じ手順の 2 回目）で追加で分かったこと（利用者の運用記録）:
  - ESO のコントローラが動いている間に ExternalSecret の finalizer を空にしても**付け直される**。
    `helm uninstall external-secrets` の**後にもう一度**空にする必要がある。
  - `istio-edge-up.sh` が当てる `deploy/local/edge-istio/traefik-service-off.yaml`（HelmChartConfig `service.enabled: false`）
    が残ると Traefik の Service が描画されず、次の `k8s-local-up.sh` のエッジ待ちが `services "traefik" not found` で落ちる。
    撤去の最後に `deploy/local/edge/traefik-entrypoint.yaml` を当て直す必要がある。

## 走査した母集合

### 軸 1: up が作る名前空間と Helm リリース（撤去対象の全数）

```
$ grep -ohE "(-n|--namespace)[ =]+[a-z\"\$][a-zA-Z_\"\$-]*" scripts/k8s-local-up.sh scripts/istio-edge-up.sh | sort | uniq -c
$ grep -nE "helm (upgrade|install)|create namespace" scripts/k8s-local-up.sh
```

- 名前空間: `platform-infra` / `microservices-platform` / `ai-stock-trading` / `external-secrets` / `reloader` /
  `istio-system` / `argocd` / `cert-manager`（8 件）。`kube-system` は k3s 内蔵のため撤去しない。
- Helm リリース: `msp`（microservices-platform）/ `external-secrets` / `reloader` / `istio-base` / `istiod`
  （istio-system）＋ `istio-ingressgateway`（`istio-edge-up.sh`）＋ `ast`（AST の配備スクリプト）。
- **cert-manager と ArgoCD は Helm ではなく `kubectl apply` のリモートマニフェスト**で入る。名前空間・CRD・webhook の
  各段で撤去する（Helm の段には無い）。
- 除外: `kube-system` に置く HelmChartConfig（Traefik）・CoreDNS の上書き・エッジの Ingress。k3s 内蔵の部品の設定であり、
  撤去対象ではない（Traefik の Service を戻す 1 点だけ 7 段目で扱う）。ESO / istio / cert-manager / ArgoCD が作る
  ClusterRole / ClusterRoleBinding も issue の検証条件に無いので本件の対象外（残る。後述「残るもの」）。

### 軸 2: 撤去する webhook（誤りの側＝「名前に部品名を含まない webhook」から引く）

稼働クラスタへの dry-run（下記「検証」）で実測した validating / mutating webhook 設定:
`cert-manager-webhook`（両方）/ `externalsecret-validate` / `secretstore-validate` / `istio-validator-istio-system` /
`istiod-default-validator` / `istio-sidecar-injector`。

- 🔴 **ESO の `externalsecret-validate` / `secretstore-validate` は名前に `external-secrets` を含まない。**
  名前だけで引く（旧手順の走査）とこの 2 件を取りこぼす。**呼び先 Service の名前空間**（`external-secrets`）でも引く。

### 軸 3: 撤去する CRD（グループで引く）

`(^|\.)(istio|external-secrets|cert-manager|argoproj)\.io$`。稼働クラスタで 46 件（ESO の `generators.external-secrets.io`
配下・`acme.cert-manager.io`・`extensions.istio.io` / `telemetry.istio.io` を含む）。issue 本文の「24 件」は 2026-09-11 時点。
除外: k3s 内蔵（`*.k3s.cattle.io` / `*.traefik.io` / `helm.cattle.io`）。

### 軸 4: 本スクリプトを呼ぶ・説明する箇所（追随先）

```
$ git grep -n "k8s-local-down" -- . ':!CHANGELOG.md' ':!.ai-context/specs'
deploy/local/README.md:58 / :138
docs/operations/operations.md:245
.ai-context/adr/IADR-0080_… / IADR-0456_…（凍結記録。書き換えない）
```

- **CI・他スクリプトからの呼び出しは 0 件**（integration-stack は `k3d cluster delete` を直接呼ぶ）。既定を dry-run へ
  変えても壊れる自動経路は無い。`deploy/local/*/README.md` の `k3d cluster delete msp-ast-dev` は k3d を直接叩く手順で、
  本スクリプトを経由しないため対象外。

## 対象範囲

- **対象**: `scripts/k8s-local-down.sh`（作り直し）／`scripts/k8s-local-down.test.sh`（新設）／`.github/workflows/ci.yml`
  の `scripts-tests` へのテスト配線／`scripts/README.md` への登録／`deploy/local/README.md`・`docs/operations/operations.md`
  の呼び方（`--apply`）。
- **対象外**: `--apply` の稼働クラスタでの実走（稼働クラスタは利用者の取引 PoC を動かしている）。ClusterRole 等の
  cluster スコープの RBAC の撤去。k3d 経路の中身（クラスタごと消すので段は要らない）。

## 設計

- 引数: `[--dry-run|--apply] [cluster-name]`。**既定は dry-run**。未知のオプションは exit 2（`--aply` の打ち間違いを実行に倒さない）。
- **dry-run が読み取り専用であることの担保**: クラスタへ触る口を 4 つに限る。
  - `kc_read`（`get` / `api-resources` / `config current-context` だけ）・`helm_read`（`list` だけ）・`k3d_read`（`cluster list` だけ）
    —— 許可リストに無い呼び出しは exit 70（dry-run でも apply でも同じ）。
  - `mutate` —— dry-run では表示するだけで**実行ファイルを起動しない**。
  - テスト（T-1422-10）が、この 4 つ以外の箇所から kubectl / helm / k3d をコマンド位置で起動していないことを静的に固定する。
- 段（Rancher Desktop 経路）: 0 現状 → 1 アプリの Helm → 2 webhook → 3 finalizer → 4 残りの Helm ＋ finalizer 再実行 →
  5 名前空間（待機 `NS_WAIT_SECONDS` 既定 240・Terminating の名前空間は中の finalizer 付きオブジェクトを種類を問わず空に）→
  6 CRD（先に 5 段目で拾えない CR —— cluster スコープ・対象外の名前空間 —— の finalizer を空に）と Bound でない PV →
  7 Traefik の Service を戻す（HelmChartConfig が `enabled: false` のときだけ）→ 8 検証。
- `list_finalized` の区切りは `|`（タブは IFS の空白類で、cluster スコープの物の空の名前空間欄が詰められ、名前がずれる。
  テスト T-1422-04 の初回実行で実際に踏んだ）。
- 検証: 名前空間は `default` / `kube-*` だけ・PV 0・軸 3 の CRD 0・Helm は `kube-system/traefik(-crd)` だけ。
  1 つでも残れば名指しして exit 1。

## 受け入れ基準

1. 既定（引数なし）は dry-run で、クラスタを一切変えない（T-1422-01）。dry-run が起動するのは読み取りだけ（T-1422-02）。
2. 計画は issue の順序どおりに並ぶ（T-1422-03）。`--apply` でも同じ順序で呼ぶ。ESO の撤去後に finalizer をもう一度空にする（T-1422-05）。
3. 名前に部品名を含まない webhook も呼び先の名前空間で拾う。無関係な webhook・k3s 内蔵の CRD / Helm は触らない（T-1422-04）。
4. 撤去し切れば exit 0、1 つでも残れば名指しして exit 1（T-1422-06 / 07）。
5. 待っても消えない名前空間は中の finalizer 付きオブジェクトを空にする（T-1422-08）。
6. k3d 経路もクラスタ削除は `--apply` のときだけ（T-1422-09）。未知のオプションは exit 2（T-1422-11）。
7. CRD の削除前に、5 段目で拾えない CR の finalizer を空にする（T-1422-12）。
8. 稼働クラスタへの dry-run が読み取りだけで完走する（読み取り専用ガード越しに実測）。

9. 【監査指摘 a】`--apply` の検証は、`kc_read get namespaces` が失敗するか `default` を含まなければ exit 1（T-1422-13）。
   読み取りの口は stderr を捨てて 0 件に倒れるため、これが無いと到達不能なクラスタで「OK」と出る。
10. 【監査指摘 b】残りの件数を終了コードで返さない（256 で 0 に巻き戻る）。`[ "$bad" -eq 0 ]` で返す（T-1422-14）。
11. 【監査指摘 c】静的検査は「コマンド位置の列挙」をやめ、許す形（引用符内・コメント・`mutate <bin>`・`command -v <bin>`・
    読み取りの口の本体 3 行）を消してから語として残る kubectl / helm / k3d をすべて数える。`if` / `then` / `!` / `command` /
    `xargs` / バッククォート / `"$( … )"` の 8 形を捕まえ、k3d の分岐へ `if kubectl delete ns bogus` を差し込んだ写しも捕まえる
    （T-1422-10）。k3d の dry-run の後にも読み取り以外の呼び出しが 0 件であることを見る（T-1422-09）。
12. 【監査指摘 d】試験は `KUBECONFIG=/nonexistent` を export し、kubectl / helm / k3d / nerdctl がスタブへ解決されることを
    `command -v` で確かめてから走る（外れていれば 1 つも走らせず exit 2）。

## 検証

- `bash scripts/k8s-local-down.test.sh`（スタブ。実クラスタ不要）
- 稼働クラスタへの dry-run は、**PATH の先頭に読み取り以外を拒否するガード**（kubectl は `get` / `api-resources` /
  `config current-context`、helm は `list` だけを実バイナリへ渡し、それ以外は exit 99、k3d は全拒否）を置いて走らせ、
  全呼び出しを記録する。結果は PR 本文に書く。**`--apply` は走らせない。**

## 残るもの

- ClusterRole / ClusterRoleBinding / APIService 等、各部品が作る cluster スコープの RBAC は撤去しない（issue の検証条件外）。
  次の up は冪等に上書きするため再構築は妨げない。
- `--apply` の実クラスタでの通し実測は未実施（稼働クラスタを壊せないため）。次に利用者がクラスタを作り直すときに実測する。
