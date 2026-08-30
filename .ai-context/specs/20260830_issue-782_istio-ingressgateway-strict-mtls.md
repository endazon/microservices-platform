---
title: 作業仕様書 — エッジを Istio Ingress Gateway へ移し、East-West mTLS を STRICT にする（#782 残段）
type: spec
status: done
related_ids:
  - NFR
  - NFR-11
  - ADR-0005
  - ADR-0021
  - ADR-0023
  - ADR-0047
  - IADR-0091
  - IADR-0206
  - IADR-0220
  - IADR-0227
  - IADR-0258
  - IADR-0307
  - IADR-0317
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md
  - planning:projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md
related_specs:
  - 20260830_issue-782_istio-mesh-optin.md
issue: "#782"
---

# 作業仕様書 — Istio Ingress Gateway 化と STRICT mTLS（#782 残段）

## 目的と射程

先行 PR #1072（[IADR-0307](../adr/IADR-0307_istio-optin-and-staged-mtls.md)）は Istio を `ISTIO=1` の
opt-in で導入し、**PERMISSIVE までは成立させた**。残っているのは 1 点だけである ——
**`PeerAuthentication` を STRICT にすると経路B のエッジが落ちる**（実測 502）。

原因は #1072 が特定済み: **`kube-system` の Traefik はメッシュの外**にあり、そこから
mesh 内の 4 つの Service（`frontend-service` / `bff-service` / `wiki-js` / `minio`）へ
**平文で入っている**。名前空間全体へ STRICT を掛けると Envoy がその平文を拒否する。

射程は **経路B（稼働中の Rancher Desktop k3s）でエッジをメッシュへ入れ、STRICT を実際に成立させるところまで**。

## 採る道と、その根拠（計画 ADR の逐語）

取りうる道は 3 つあった（A: Istio Ingress Gateway へ移す / B: Traefik をメッシュへ入れる /
C: `portLevelMtls` で穴を残す）。**計画は A を定めている。**

`ADR-0021`（`Accepted`・2026-07-23）§決定 の逐語:

> - **入口・TLS 終端・ルーティング・レート制限**: **Istio Ingress Gateway**（Envoy。Istio 制御面が管理）
> - **SPA（フロントエンド）静的配信の Web サーバー**: **Caddy**
>
> …実行基盤 k3s（ADR-0008）が既定同梱する **Traefik は無効化する**。

同 ADR §コンテキストと課題 は、**本件の障害そのものを事前に言い当てている**:

> 入口に**別系統のプロキシ**（Traefik / NGINX Ingress 等）を置くと、入口から mesh 内サービスへ
> 引き渡す一点で、**mTLS が STRICT のとき平文流入が Envoy に拒否される境界問題**が生じ、
> サイドカー注入や PERMISSIVE 化といった追加設計が必要になる。**この境界をそもそも発生させない構成が望ましい。**

§Istio（Envoy）との関係 は B・C を明示的に退けている:

> 別系統プロキシを入口に置いたときに生じる「入口から mesh 内サービスへ引き渡す一点での mTLS 境界問題」
> （STRICT 時の平文拒否）は、入口が mesh ネイティブであるため**構造的に発生しない**。
> …**入口境界のための特別な緩和（PERMISSIVE 化）は不要。**

**したがって B（Traefik へサイドカー注入）と C（`portLevelMtls` の穴）は計画に反する。**
選択肢 2「入口=Traefik」は ADR-0021 §検討した選択肢 に**挙げられた上で採られなかった**案である。
**裁定依頼は不要**（計画が既に決めている）。

`ADR-0047` 決定 1 は「エッジの実体が Istio Ingress Gateway か経路B の Traefik かは**問わない**」と書くが、
これは **証明書の適用範囲**についての文であり（同 ADR の主題は `ADR-0023` §決定 の部分改定）、
**エッジ製品の決定を差し戻すものではない**。同 ADR は「本 ADR は経路B のエッジ実装（Traefik）を変更せず」と
自ら射程を限っている。**エッジ製品の正本は `ADR-0021` である。**

## 母集合（着手時に自分で引いた・[[IADR-0141]] 決定 1）

**引いた軸 1 — 「Traefik から mesh 内へ入る経路」をパスから引く**（拡張子で絞らない・行フィルタで絞らない）:

```console
$ grep -rln "kind: Ingress" --exclude-dir=.git --exclude-dir=node_modules --exclude-dir=ai-stock-trading .
./.ai-context/specs/20260823_issue-953_helmchartconfig-fail-closed.md
./deploy/argocd/appproject.yaml
./deploy/helm/microservices-platform/templates/wikijs.yaml
./deploy/local/edge/admin-ingress-infra.yaml
./deploy/local/edge/admin-ingress-minio.yaml
./deploy/local/edge/admin-ingress-wiki.yaml
./deploy/local/edge/argocd-ingress.yaml
./deploy/local/edge/keycloak-ingress.yaml
./deploy/local/edge/platform-frontend-ingress.yaml
```

**引いた軸 2 — 稼働クラスタの実物**（宣言ではなく現物。`kubectl get ingress -A`）: **9 件**。
宣言（6 ファイル・9 オブジェクト）と一致した。

**引いた軸 3 — `traefik` の語で全文走査**（是正すると誤りになる自分の記述を先に洗う。規則 9・10）:
59 ファイル。うち **live な権威文書・コード**は `deploy/istio/README.md` /
`deploy/local/README.md` / `deploy/local/edge/README.md` / `deploy/local/edge/*.yaml` /
`deploy/local/aliases/coredns-edge-hosts.yaml` / `scripts/k8s-local-up.sh` /
`scripts/k8s-local-up.test.js` / `scripts/check-stack-ready.js` /
`scripts/verify-oidc-edge-flow.sh` / `.github/workflows/integration-stack.yml` /
`deploy/local/values-local.yaml` / `deploy/local/headlamp/headlamp.yaml` /
`deploy/local/argocd/oidc/argocd-cmdparams-patch.yaml` / `deploy/argocd/appproject.yaml`。

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `.ai-context/adr/*` `.ai-context/specs/*`（41 件） | **確定済みの凍結記録**。遡及書き換えしない（`traceability.repo.md`）。後継は本仕様書と `IADR-0317` が持つ |
| `deploy/helm/.../wikijs.yaml` の Ingress | **本番チャート**（`wikijs.ingress.enabled` 既定 false）。経路B のエッジ overlay ではない |
| `scripts/check-stack-ready.js` / `verify-oidc-edge-flow.sh` / `integration-stack.yml` / `values-local.yaml` / `headlamp.yaml` / `argocd-cmdparams-patch.yaml` | **既定経路（`ISTIO` 未設定）は 1 バイトも変えない**ため、Traefik を指す記述はそのまま正しい。Istio エッジは opt-in の**追加**であって置換ではない |
| `deploy/argocd/appproject.yaml` の `Ingress` | 本番チャートがレンダリングし得る種別。許可の削除はしない（受け入れ基準 6 は「全部入っていること」であり、余分を消すことではない） |

**既存で変える必要があるのは `deploy/local/edge/README.md` と `deploy/istio/README.md` と
`scripts/k8s-local-up.sh` と `scripts/README.md` だけ**である（`deploy/local/edge/` の
マニフェスト本体は 1 行も変えない —— 既定経路だからである）。残りは**追加**である:
`deploy/local/edge-istio/`（6 ファイル）・`deploy/istio/ingressgateway-values-local.yaml`・
`scripts/istio-edge-{up,down}.sh`・`scripts/k8s-local-up.test.js` への 6 検査。

## 阻害要因の実測（着手時・2026-08-30）

```console
$ kubectl -n kube-system get deploy traefik -o jsonpath='{.spec.template.spec.containers[*].name}'
traefik                       # ← サイドカー無し（メッシュ外）

$ kubectl -n kube-system get svc traefik
traefik  LoadBalancer  192.168.127.2  50000:31496/TCP,80:32716/TCP,443:32499/TCP

$ kubectl -n kube-system get ds
svclb-traefik-ad843cc0  1/1                  # ← hostPort 80/443/50000 を握っている

$ kubectl -n istio-system get svc
istiod  ClusterIP ...                        # ← istio-ingressgateway は無い
```

🔴 **k3s の ServiceLB（klipper）は hostPort でポートを握る。** したがって
**Traefik が 80/443/50000 を持ったままでは `istio-ingressgateway` を LoadBalancer で立てられない**
（2 つ目の svclb DaemonSet が bind に失敗する）。**ポートの明け渡しが先である。**

さらに **50000（admin entrypoint）には mesh 内の 2 件（`minio` / `wiki-js`）が載っている**ため、
「80/443 だけ Istio へ、50000 は Traefik のまま」は成立しない（その 2 件が STRICT で落ちる）。
minio / wiki を 443 へ移すのも不可 —— **7 つの OIDC クライアントの redirect URI が `:50000` 付きで
Keycloak に登録済み**である（`IADR-0093` / `IADR-0095` / `IADR-0220`）。

**帰結: 3 ポートすべてを Istio Ingress Gateway が持ち、Traefik の Service を落とす。**
これは `ADR-0021`「Traefik は無効化する」そのものである。

## 決めること（詳細は [IADR-0317](../adr/IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)）

1. `ISTIO=1` かつ `LOCALEDGE=1` のとき、エッジを `istio-ingressgateway` へ移す。**既定は不変**
2. Traefik は `HelmChartConfig` で `service.enabled: false` にして**ポートを明け渡す**（削除しない＝戻せる）
3. 9 本の Ingress を Gateway 2 本（`web`/`websecure` = 80/443、`admin` = 50000）＋ VirtualService へ写す
4. `edge-tls` を `istio-system` にも発行する（`secretName` と `dnsNames` は変えない＝`ADR-0023` の設計要件）
5. CoreDNS の `*.localhost` 転送先を `istio-ingressgateway.istio-system` へ差し替える（opt-in 側だけ）
6. **切り戻しは 1 コマンド**（`scripts/istio-edge-down.sh`）。**本番の変更を当てる前に実際に走らせて確かめる**

## 段取り（この順でしか測らない）

| 段 | 測ること | 合格 |
| --- | --- | --- |
| 0 | 切り戻しスクリプトを**先に**走らせる | EXIT=0・エッジ 200（現状のまま壊れない） |
| 1 | 注入 | 18/18 に `istio-proxy`（`initContainers` も見る） |
| 2 | 全 Pod Ready | `rollout status` 全件・28 Deployment available |
| 3 | PERMISSIVE で疎通 | Traefik 経由 200（基準線） |
| 4 | Istio エッジへ切替（PERMISSIVE のまま） | 9 host すべて基準線と同じ応答 |
| 4' | **切り戻しを 1 回本当に打つ** | Traefik へ戻って 200 |
| 4'' | 再度 Istio エッジへ | 4 と同じ |
| 5 | STRICT へ | `kubectl get peerauthentication -A` が STRICT |
| 6 | STRICT 後の疎通 | 4 と同じ（**`-k` を使わない**） |
| 7 | PERMISSIVE 残存ゼロ | 全 `PeerAuthentication` / `DestinationRule` を走査 |

🔴 **`curl -k` で測らない**（#1074 の事故）。Windows の curl は schannel なので私有 CA では
失効確認が `unknown` になり接続が落ちる。**`--ssl-revoke-best-effort` は失効確認だけを
best-effort にするもので、チェーン検証とホスト名照合は有効なまま**である（`-k` とは別物）。

## 実測（すべて 2026-08-30・k3s v1.35.4+k3s1 / Istio 1.30.4）

証明書検証は有効のまま測った（`-k` を使わない）。12 エンドポイント × 各 3 回。

| 段 | 状態 | 結果 |
| --- | --- | --- |
| S0 | 切り戻しスクリプトを**先に**実行 → Traefik / PERMISSIVE | EXIT=0・12/12 が基準線（200/401/301/200 ＋ 管理 7 件 200） |
| S1 | 注入 | `injected=18 / total=18`（`initContainers` も見た） |
| S2 | 全 Pod Ready | Deployment 60 件すべて `1/1`・非 Running の Pod 0 件 |
| S3 | PERMISSIVE 疎通（基準線） | 12/12 |
| S4 | Istio エッジ / PERMISSIVE | 12/12（基準線と完全一致） |
| S4' | **切り戻しを本当に打った** | Traefik svc が 80/443/50000 を取り戻し 12/12 |
| S4'' | 再度 Istio エッジ | 12/12 |
| **S5** | **STRICT へ** | `kubectl get peerauthentication -A` → `STRICT` |
| **S6** | **STRICT 後の疎通** | **12/12**（基準線と完全一致。Deployment 61 件 `1/1`） |
| S7 | PERMISSIVE 残存 | **0**（policy / Envoy リスナ / 変異試験の 3 方向で確認） |

**変異試験**（メッシュ外の Pod から平文で入る）: STRICT → `Connection reset by peer`、
PERMISSIVE → 200 / 401、STRICT → `Connection reset by peer`。**往復した。**

**S4'（本当に戻した回）で欠陥を 1 つ見つけた** —— `kubectl wait` は対象が存在しないと
**待たずに即エラーになる**。Service ごと消してから戻すため、**存在を待つループが先に要る**。
**Traefik は正しく復活したのにスクリプトだけが非 0 で終わった。**
S0（当てる前の空撃ち）は Service が消えていないので通ってしまい、**この欠陥を出せなかった**。
是正して再実行し EXIT=0 を確認した —— **空撃ちだけでは足りず、本当に 1 回戻す必要があった。**

詳細は [IADR-0317](../adr/IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md) §実クラスタで確かめたこと。

## 測れなかったこと（隠さない）

- **`istioctl` は PATH に無い**（`IADR-0307` 決定 2 で「配布バイナリを増やさない」と決めたため）。
  `istioctl authn tls-check` / `proxy-config listener` の代わりに
  `pilot-agent request GET config_dump` を `kubectl exec` で叩いた。**同じものを見ている。**
- **`kubeconform` が PATH に無い**ため `node scripts/check-deploy-manifests.js` は実行できなかった。
- **ブラウザでの OIDC ログイン往復は測っていない**（`curl` は discovery と 401 までである）。

## 受け入れ基準


- [x] `PeerAuthentication` が STRICT（実出力）
- [x] STRICT 後もエッジ経由の疎通が生きている（証明書検証を有効にした curl）
- [x] PERMISSIVE 残存がゼロ（実測して示す。測れないなら「測れなかった」と述べる）
- [x] 各段の実測が残っている
- [x] 切り戻しが 1 コマンドで、**効くことを試した記録**がある
- [x] `appproject.yaml` の `namespaceResourceWhitelist` に追加種別が全部入っている（走査で確認）
- [x] 既定（`ISTIO` 未設定）はバイト等価
