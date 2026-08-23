---
title: 作業仕様書 — HelmChartConfig の reconcile 失敗を k8s-local-up.sh へ伝える（fail-closed 化・#953）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0021
  - IADR-0091
  - IADR-0206
  - IADR-0220
  - IADR-0248
  - IADR-0255
  - IADR-0258
author: claude
created: 2026-08-23
updated: 2026-08-23
issue: "#953"
---

# 作業仕様書: HelmChartConfig の反映を fail-closed で待つ（#953）

## 起点

`#953`。**`HelmChartConfig` の reconcile が失敗しても `scripts/k8s-local-up.sh` は EXIT=0 で返る。**
宣言は「適用に成功」し、反映は失敗し、誰も気付かない。

`#783` は k3s イメージを pin することでこれを**回避**した（`K3S_IMAGE` / [IADR-0248]）。
**回避と解決は別である。** pin が外れた・別バージョンへ動いた瞬間、同じ穴へ落ちる。
本 issue の主題は **気付ける形にすること**である。

## 🔴 着手前の実測 —— 欠陥はコード上で確定している

`scripts/k8s-local-up.sh` の `LOCALEDGE=1` ブロック（現行 382-383 行）:

```bash
echo "==> [opt-in] local edge aggregation (Traefik admin:50000 + Ingress, IADR-0091)"
kubectl apply -k deploy/local/edge
```

この直後に**待ち合わせが一切無い**。`deploy/local/edge/kustomization.yaml` の先頭資源は
`traefik-entrypoint.yaml`（`kind: HelmChartConfig`）であり、その効果（Traefik Service に
`admin=50000` が生えること）は **k3s の helm-controller が非同期に**実現する。
`kubectl apply` が見るのは「オブジェクトを置けたか」だけで、後段の `helm upgrade` が
`Error: UPGRADE FAILED: ... error calling eq: incompatible types for comparison` で落ちても
**呼び出し側へは伝わらない**（issue 本文の実測ログ・GitHub ホストランナー run `32554867883`）。

直後の 3 行（`coredns-edge-hosts.yaml` → `rollout restart` → `rollout status`）は
**coredns の**待ち合わせであり、traefik の反映とは無関係である。
`LOCALEDGE` ブロック後半の `kubectl wait --for=condition=Ready certificate/edge-tls` も
cert-manager（別の CRD・別のコントローラ）を見ており、traefik には触れない。

**したがって `admin=50000` が立たないまま、以降のすべての段が緑で通り、EXIT=0 で返る。**

### 既に在る門は、この穴を塞がない

`scripts/check-stack-ready.js` の **G5** は `kube-system/traefik` に `admin=50000` が在ることを
既に検査している（#953 を名指しでコメントしている）。**しかしこれは `k8s-local-up.sh` からは呼ばれない。**
呼ぶのは `.github/workflows/integration-stack.yml` だけで、しかもそのワークフローは
**nightly ＋ develop への push ＋ 手動**でしか起動しない（`scripts/README.md` の表）。

```
$ grep -rn "check-stack-ready" --include=*.sh scripts/
（0 件）
```

つまり **手元で `bash scripts/k8s-local-up.sh` を叩く経路には門が無い。**
受け入れ基準は「`k8s-local-up.sh` が非 0 で終了する」であり、G5 の存在は充足の根拠にならない。

## 母集合の走査（記憶で挙げない・誤りの側の文字列で引く）

規則 9（[`.claude/rules/traceability.repo.md`](../../.claude/rules/traceability.repo.md)）に従い、
**「他に同型が無い」を記憶で言わず、走査してから言う。**

### 走査 A: `HelmChartConfig` の全出現（追跡下・生の出力）

```
$ grep -rn "HelmChartConfig" --exclude-dir=.git --exclude-dir=node_modules .
./deploy/local/edge/README.md:14:| `traefik-entrypoint.yaml` | ... （`HelmChartConfig`） |
./deploy/local/edge/traefik-entrypoint.yaml:2:# k3s は Traefik を HelmChart(helm-controller) で導入するため、HelmChartConfig で values を…
./deploy/local/edge/traefik-entrypoint.yaml:21:kind: HelmChartConfig
./scripts/check-stack-ready.js:198:      ` **HelmChartConfig の reconcile が落ちても kubectl apply は成功する**（#953）。` +
./.ai-context/specs/20260817_issue-841_admin-entrypoint-https.md:349
./.ai-context/specs/20260720_issue-356_local-edge-aggregation.md:47,64
./.ai-context/specs/20260822_issue-783_integration-stack-ci.md:149,166,523
./.ai-context/adr/README.md:147
./.ai-context/adr/IADR-0091_local-edge-aggregation-traefik.md:76,111
./.ai-context/adr/IADR-0220_admin-entrypoint-tls-and-http-redirect.md:83
./.ai-context/adr/IADR-0248_integration-stack-ci-readiness-gate.md:88,186
./.ai-context/adr/IADR-0255_edge-smoke-step-loss-gate-and-unobservable-search.md:115
（一致ファイル 11 件）
```

**マニフェスト実体は 1 件だけ**（`deploy/local/edge/traefik-entrypoint.yaml`）。
残り 10 件は散文（README・仕様書・IADR）と検査器のコメントで、適用箇所ではない。

### 走査 B: kind ではなく **apiVersion**（helm-controller の API 群）で引き直す

`kind:` で引くと `HelmChart`（`HelmChartConfig` の親）を取り落とすため、規則 10 に従い引き直した。

```
$ grep -rn "kind:\s*HelmChart\b" --exclude-dir=.git --exclude-dir=node_modules .
(0 件)

$ grep -rn "helm.cattle.io" --exclude-dir=.git --exclude-dir=node_modules . | grep -v "\.md:"
./deploy/local/edge/traefik-entrypoint.yaml:20:apiVersion: helm.cattle.io/v1
```

**helm-controller に渡す宣言はリポジトリ全体で 1 件**である。
`src/ai-stock-trading`（submodule）は未取得のため走査対象外（`check-plan-id-qualification.js` の
除外と同じ扱い）。AST 側は独自の chart を持つが、k3s の helm-controller CR は使っていない
（`deploy/local/edge` は MSP 固有の overlay である）。

### 走査 C: `deploy/` 配下の全 `kind` の頻度（見落としが無いことの裏取り）

```
$ grep -rhn "^kind:" deploy/ | sed 's/^[0-9]*://' | sort | uniq -c | sort -rn
     27 kind: Service          17 kind: Deployment       11 kind: ExternalSecret
     11 kind: ConfigMap        10 kind: Ingress           9 kind: PersistentVolumeClaim
      8 kind: Kustomization     4 kind: NetworkPolicy     4 kind: ClusterRoleBinding
      4 kind: Certificate       3 kind: ServiceAccount    3 kind: Secret
      2 kind: Namespace         2 kind: ClusterSecretStore 2 kind: ClusterIssuer
      1 kind: VirtualService    1 kind: PodDisruptionBudget 1 kind: PeerAuthentication
      1 kind: Job               1 kind: HorizontalPodAutoscaler
      1 kind: HelmChartConfig   1 kind: Gateway           1 kind: DestinationRule
      1 kind: DaemonSet         1 kind: ClusterRole       1 kind: Application  1 kind: AppProject
```

`HelmChartConfig` は **1 件**。走査 A・B と一致する。

### 走査 D: 同型（「非同期コントローラが実現する宣言を apply して待たない／警告して続行する」）

`k8s-local-up.sh` の `kubectl apply` / `helm upgrade` は **38 箇所**。同型性で分類した。

| 適用箇所 | 反映の担い手 | 現在の待ち | 判定 |
| --- | --- | --- | --- |
| `apply -k deploy/local/edge`（`HelmChartConfig`） | k3s helm-controller（**非同期**） | **無し** | 🔴 **本 issue の対象** |
| `apply -k <infra kustomize>` | kubelet（Deployment） | `rollout status` ×6（fail-closed） | 対象外（既に待つ） |
| `helm upgrade --install msp`（`--wait` 無し） | kubelet | 無し | **対象外**: #783 が既知として扱い、門は `check-stack-ready.js`（[IADR-0248] 決定 4）。**本 issue の射程は helm-controller の reconcile** であり、ここへ `--wait` を足すのは別の裁定（up の所要時間が変わる） |
| `apply -f coredns-edge-hosts.yaml` | coredns（import 追加） | `rollout restart` ＋ `rollout status`（fail-closed） | 対象外（既に待つ） |
| `apply --server-side cert-manager.yaml` | cert-manager | `wait --for=condition=Established` ＋ `rollout status` ×2（fail-closed） | 対象外（既に待つ） |
| `apply -k deploy/local/edge/tls` | cert-manager | `wait --for=condition=Ready certificate/edge-tls` ×2〜3（fail-closed） | 対象外（既に待つ） |
| `apply -f .../externalsecret-*.yaml`（11 件） | External Secrets Operator（**非同期**） | `eso_wait` が **`\|\| echo "warn: ..."` で続行**（fail-open） | **同型だが射程外**（下記） |
| `apply -f .../aliases/*.yaml`（ExternalName Service） | kube-proxy / CoreDNS（宣言即時） | 無し | 対象外（非同期の reconcile を伴わない） |
| `apply -f deploy/argocd/*.yaml`・ArgoCD install | ArgoCD | 無し | 対象外（ArgoCD 自体が別の opt-in で、反映は ArgoCD の同期に委ねる設計） |
| `apply -k deploy/local/headlamp` ほか | kubelet | 無し | 対象外（Deployment。効果は pod の Ready であり `check-stack-ready.js` G1 の担当） |
| `kubectl patch configmap argocd-*` | ArgoCD | `rollout restart \|\| true` | 対象外（同上） |

**`ExternalSecret` の `eso_wait` は構造として同型である**（非同期・失敗しても続行）。
本 issue では**触らない**。理由は 3 つで、いずれも「今すぐ直せない」ではなく「別の裁定が要る」である。

1. **fail-open は意図された設計である**（[IADR-0103] の明記: 「best-effort（未デプロイ・未有効ゲート・
   同期遅延で `up` を止めない）」）。#953 は `HelmChartConfig` について「警告して続行は意味がない」と
   言い切っているが、**`ExternalSecret` について同じ裁定は下りていない。**
2. `ESO=1` は opt-in であり、`VAULT=1` の dev Vault の seed 状況に依存する。fail-closed 化は
   **既存の opt-in の挙動を変える**（バイト等価が崩れる）。
3. #953 の受け入れ基準は `HelmChartConfig` に閉じている。射程を広げるなら別 issue が要る（残件へ記す）。

## 決定（詳細は [IADR-0258]）

1. `kubectl apply -k deploy/local/edge` の**直後**に、`HelmChartConfig` の**観測可能な結果**
   （`kube-system/traefik` Service の `admin=50000`）を **`kubectl wait` で待ち、来なければ非 0 で落とす**。
2. **警告を出して続行しない。** 失敗時は診断（Service の実ポート・`HelmChartConfig` / `HelmChart` の
   状態・helm-install pod のログ）を stderr へ出してから `exit 1`。
3. `traefik-entrypoint.yaml` の**バージョン依存の注記を実測で書き直す**。現行の注記は
   「新しめ(chart v25+/Traefik v3)は下記のマップ形」と書いており、**これは誤りである**
   —— 実測は chart **25.0.3 が bool**、**26 以降が map** である（issue #953 の表）。
4. 待ちは **`--for=jsonpath`** を使う。ポーリングループを自前で書くと、`scripts/k8s-local-up.test.js` の
   stub-on-PATH ハーネス（kubectl が常に exit 0・標準出力は空）で**必ずタイムアウトし、既存の
   `LOCALEDGE=1` 系テストが全部落ちる**。`kubectl wait` なら既存の `certificate/edge-tls` 待ちと
   同じ形で、ハーネスを 1 バイトも変えずに済む。

## 受け入れ基準（issue #953）と本作業の対応

| # | 基準 | 対応 |
| --- | --- | --- |
| 1 | 反映が失敗したとき `k8s-local-up.sh` が**非 0 で終了する** | `kubectl wait --for=jsonpath` ＋ `exit 1` |
| 2 | **変異試験**（壊すと落ちる／壊す前は落ちない） | **実クラスタが無いため未実測**。手順を下に残す |
| 3 | `HelmChartConfig` 利用箇所の走査・件数・対象の記録 | 上記「母集合の走査」A〜C（**マニフェスト実体 1 件**） |
| 4 | 宣言のバージョン依存が注記されている | `traefik-entrypoint.yaml` の注記を実測で書き直す |

### 変異試験の手順（実クラスタを持つ者が実行する）

**必ずクラスタを作り直してから行う。** 既存クラスタでは、前回成功した Service が `admin=50000` を
保持したままなので、壊しても門が通ってしまう（後述の限界）。

```bash
bash scripts/k8s-local-down.sh                     # クラスタを消す
LOCALEDGE=1 K3S_IMAGE=rancher/k3s:v1.35.4-k3s1 bash scripts/k8s-local-up.sh
echo "EXIT_before=$?"                              # 期待: 0（壊す前は落ちない）

bash scripts/k8s-local-down.sh
# 壊す: expose を chart 26 以降が受け付けない bool へ（＝ #953 が踏んだ型不一致の裏返し）
sed -i 's/        expose:\n          default: true/        expose: true/' deploy/local/edge/traefik-entrypoint.yaml
LOCALEDGE=1 K3S_IMAGE=rancher/k3s:v1.35.4-k3s1 bash scripts/k8s-local-up.sh
echo "EXIT_after=$?"                               # 期待: 非 0（門が落とす）
```

## 既知の限界（隠さない）

- **既存クラスタへの再実行では、新たに壊した宣言を捕まえられない。** 前回の reconcile が成功して
  いれば Service は `admin=50000` を保持し続けるため、新しい `HelmChartConfig` の reconcile が
  失敗しても待ちは即座に成立する。**この門が確実に効くのはクラスタ作成直後**である。
  job レベル（`helm-install-traefik` の `condition=Complete`）まで見れば塞げるが、
  **job 名・ラベルが k3s のバージョン依存**であり、**バージョン依存を塞ぐ門をバージョン依存の
  識別子で書くのは自己矛盾**である。加えて誤ればローカル開発が全員即座に止まる（偽陽性の代償が大きい）。
  実クラスタで確かめられない本作業では採らない（[IADR-0258] 決定 3・残件へ）。
- **実クラスタでの実走は未実施**である（本作業環境に k8s クラスタが無い）。
  ただし `kubectl wait --for=jsonpath` の意味論は **kubectl のソースで検証した**（下記）。

## 変更するファイル

- `scripts/k8s-local-up.sh` — 反映待ちの追加（`LOCALEDGE` ブロック内・既定オフでは 1 バイトも実行されない）
- `deploy/local/edge/traefik-entrypoint.yaml` — バージョン依存の注記を実測で書き直す
- `deploy/local/edge/README.md` — 門の存在を運用手順へ 1 行足す
- `.ai-context/adr/IADR-0258_*.md` ＋ `.ai-context/adr/README.md` — 決定の記録と索引

## `kubectl wait --for=jsonpath` の意味論を **ソースで**確かめた（記憶で書かない）

クラスタが無いので実走できない。**代わりに kubectl の実装を読んで確かめた。**
`kubernetes/kubectl` の `release-1.30` / `release-1.31` / `release-1.34` を取得して確認した
（1.30 は `pkg/cmd/wait/wait.go`、1.31 以降は `pkg/cmd/wait/json.go` へ分割。**判定部の意味論は 3 版で同一**
——1.31 と 1.34 の `json.go` は `diff` で差分なし、1.30 の同名関数も字義一致）。

| 懸念 | 実装 | 結論 |
| --- | --- | --- |
| `--for=jsonpath=…` の値を素朴に `=` で split すると `[?(@.name=="admin")]` の `==` で壊れないか | `splitJSONPathInput()`: 「`=` で分割するが **`==` では分割しない**」と実装・コメントの両方で明示 | **壊れない**（2 要素に割れる） |
| フィルタ式が受け付けられるか | ヘルプの例に `--for='jsonpath={.status.conditions[?(@.type=="Ready")].status}=True'` が載っている | **受け付ける** |
| 複数値になって弾かれないか | `verifyParsedJSONPath()`: 結果が 2 個以上ならエラー。`admin` は 1 ポートにしか一致しない | **単一値で通る** |
| ポートが**無い**とき何が起きるか | `checkCondition()`: `len(parseResults[0]) == 0` → `(false, nil)`＝**条件未成立として待ち続ける** | **タイムアウト → 非 0**（fail-closed） |
| 数値と文字列の比較 | `compareResults()`: `fmt.Sprintf("%v", …)` で文字列化して比較 | `50000` と `"50000"` が一致 |

**残る未検証は「実クラスタで実際に admin=50000 を観測して通ること」だけ**である。

## 検証

```bash
bash -n scripts/k8s-local-up.sh
node scripts/k8s-local-up.test.js
node scripts/check-deploy-manifests.js
node scripts/check-adr-numbering.js
```
