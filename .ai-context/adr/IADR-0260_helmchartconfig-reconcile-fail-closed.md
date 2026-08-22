---
title: IADR-0260 HelmChartConfig は「置けたこと」ではなく「効いたこと」を待ち、来なければ落とす
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0021
  - IADR-0091
  - IADR-0103
  - IADR-0206
  - IADR-0220
  - IADR-0248
  - IADR-0255
author: claude
created: 2026-08-23
updated: 2026-08-23
---

# IADR-0260 HelmChartConfig は「置けたこと」ではなく「効いたこと」を待つ

## 状況

`scripts/k8s-local-up.sh` は `LOCALEDGE=1` で `deploy/local/edge` を apply する。その先頭資源
`traefik-entrypoint.yaml` は `kind: HelmChartConfig` であり、効果（k3s 内蔵 Traefik に
追加 entrypoint `admin:50000` が生えること・[IADR-0091] / [IADR-0220]）は **helm-controller が
非同期に**実現する。

`kubectl apply` が見るのは「オブジェクトを置けたか」だけである。実測（#953・GitHub ホストランナー
run `32554867883`）では、helm-controller の `helm upgrade` が

```
Error: UPGRADE FAILED: template: traefik/templates/service.yaml:21:12:
error calling eq: incompatible types for comparison
```

で失敗し、`admin=50000` が立たないまま **`k8s-local-up.sh` は EXIT=0 で返った**。
原因は `expose` の values スキーマが traefik chart 25.0.3 では bool、26 以降では map であること
—— **リポジトリの宣言が k3s のバージョンに暗黙に依存していた**。

#783（[IADR-0248]）は `K3S_IMAGE` で k3s を pin して**回避**した。**回避と解決は別である。**
pin が外れれば同じ穴へ落ちる。残る構造は「reconcile の失敗が呼び出し側へ伝わらない」ことである。

既に `scripts/check-stack-ready.js` の **G5** が `admin=50000` を検査しているが、**これは
`k8s-local-up.sh` からは呼ばれない**。呼ぶのは `integration-stack.yml`（nightly ＋ develop への
push ＋ 手動）だけであり、**手元で up を叩く経路には門が無い**。

## 決定 1: 適用の直後に、**観測可能な結果**を待つ。来なければ非 0 で落とす

`kubectl apply -k deploy/local/edge` の直後に置く。

```bash
kubectl -n kube-system wait --for=jsonpath='{.spec.ports[?(@.name=="admin")].port}'=50000 \
  svc/traefik --timeout=180s
```

- 待つのは**宣言の状態ではなく結果**である。`HelmChartConfig` 自身の `status` を見ないのは、
  そこに何が載るかが **k3s（helm-controller）のバージョンに依存する**からである。
  **バージョン依存を塞ぐ門を、バージョン依存の識別子で書いてはならない。**
  Service の port は Kubernetes のコア API であり、chart の版が変わっても意味が変わらない。
- **`--for=jsonpath` を使い、自前のポーリングループを書かない。** ループにすると
  `scripts/k8s-local-up.test.js` の stub-on-PATH ハーネス（kubectl が常に exit 0・標準出力は空）で
  必ずタイムアウトし、`LOCALEDGE=1` 系のテストが全部落ちる。`kubectl wait` なら既存の
  `certificate/edge-tls` 待ち（[IADR-0206]）と同じ形で、ハーネスを 1 バイトも変えずに済む。

## 決定 2: 🔴 **警告を出して続行しない**

失敗時は診断（Service の実ポート・`helmchartconfig,helmchart traefik`・`job/helm-install-traefik` の
ログ）を stderr へ出してから **`exit 1`** する。

**警告して続行することは EXIT=0 と同じである。** #953 が塞ごうとしているのは
「宣言は成功し、反映は失敗し、誰も気付かない」ことであり、警告は「誰も気付かない」を変えない
（up のログは長く、緑で返れば誰も読まない）。[IADR-0255] 決定 1 と同じ判断である
——「段が消えても PASS が減るだけで緑」を、数える門で置き換えた。

## 決定 3: **job レベル（`helm-install-traefik` の Complete）までは見ない**

見れば「既存クラスタへの再実行で、新しく壊した宣言を捕まえられない」という限界（下記）を
塞げる。それでも採らない。

1. **job 名・ラベルが k3s のバージョン依存**である（`helm-install-<chart>` /
   `helmcharts.helm.cattle.io/chart`）。決定 1 で退けた理由がそのまま当てはまる。
2. **偽陽性の代償が大きい。** 名前が違えば `kubectl wait` は即座に「not found」で非 0 を返し、
   **ローカル開発が全員その場で止まる**。
3. **実クラスタで確かめられない。** 本作業（#953）はクラスタを持たない環境で行われており、
   確かめずに入れる門は、確かめずに入れた宣言（＝本 issue の原因）と同じ性質を持つ。

将来 job レベルの検査を入れるなら、**実クラスタでラベルを実測してから**にすること。

## 帰結

- `LOCALEDGE=1` の up は、traefik の反映が済むまで最大 180 秒ブロックする（従来は待たなかった）。
  既定（`LOCALEDGE` 未設定）では本ブロックに入らないため **1 バイトも実行されない**。
- 反映が失敗すると、後続（coredns / cert-manager / TLS）へ進まずその場で落ちる。
  **落ちる位置が原因の位置に近くなる**（従来は最後まで走り切って緑だった）。
- `deploy/local/edge/traefik-entrypoint.yaml` の注記を実測で書き直した。**従前の注記は誤りで**、
  「新しめ(chart v25+)は下記のマップ形」と書いていたが、chart 25.0.3 は bool である。

## 既知の限界（隠さない）

**既存クラスタへの再実行では、新たに壊した宣言を捕まえられない。** 前回の reconcile が成功して
いれば Service は `admin=50000` を保持し続けるため、新しい `HelmChartConfig` の reconcile が
失敗しても待ちは即座に成立する。**この門が確実に効くのはクラスタ作成直後**である。
変異試験は必ず `k8s-local-up.down.sh` でクラスタを消してから行うこと。

**実クラスタでの実走は未実施**である（本作業環境に k8s クラスタが無い）。
`kubectl wait --for=jsonpath` の意味論は `kubernetes/kubectl` の実装（`release-1.30` / `1.31` / `1.34`）を
読んで確かめた —— `splitJSONPathInput()` は **`==` では分割しない**（フィルタ式が壊れない）、
`verifyParsedJSONPath()` は単一値を要求する（`admin` は 1 ポートにしか一致しない）、
ポートが無いとき `checkCondition()` は「未成立」を返して待ち続ける（**タイムアウトで非 0** ＝ fail-closed）。
3 版で判定部の意味論は同一である。**残る未検証は「実クラスタで実際に通ること」だけ**である。
検証の詳細は作業仕様書に残した。

## 射程（`ExternalSecret` は含めない）

`ESO=1` の `eso_wait` は構造として同型（非同期・失敗しても `warn` で続行）だが、本決定の射程外である。
そこでの fail-open は [IADR-0103] が意図して選んだもので（「未デプロイ・未有効ゲート・同期遅延で
`up` を止めない」）、覆すには別の裁定が要る。#953 の受け入れ基準は `HelmChartConfig` に閉じている。

## 走査（記憶で挙げない）

`HelmChartConfig` のマニフェスト実体はリポジトリ全体で **1 件**（`deploy/local/edge/traefik-entrypoint.yaml`）。
`kind: HelmChart` は **0 件**、`helm.cattle.io` を含む非 md ファイルも同じ 1 件のみ。
走査の生の出力は作業仕様書 `.ai-context/specs/20260823_issue-953_helmchartconfig-fail-closed.md` に残した。
