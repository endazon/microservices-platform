# Istio サービスメッシュ導入（STRICT mTLS）

> 起点: ADR-0005（サービスメッシュ / Istio / mTLS）
> 関連: IADR-0017（暫定: ネットワーク分離を第一防御）→ **IADR-0026（本 mTLS で Supersede）**
> 回帰テスト: `src/Tests/KnowledgePlatform.IntegrationTests/Deployment/MeshMtlsTests.cs`

サービス間通信を **STRICT mTLS**（平文フォールバック無し）で暗号化・相互認証する。
mTLS を強制する宣言（`PeerAuthentication` / `DestinationRule`）は Helm チャート
（`deploy/helm/knowledge-platform/templates/istio-mtls.yaml`）に含まれ、ArgoCD が同期する。

## 1. Istio 本体の導入

k3s クラスタへ Istio コントロールプレーンを導入する（istioctl または Helm）。

```sh
# istioctl（demo/default プロファイル。本番は運用要件に合わせる）
istioctl install --set profile=default -y

# サイドカー自動注入は Namespace ラベルで行う（Helm namespace.yaml が付与）
kubectl label namespace knowledge-platform istio-injection=enabled --overwrite
```

既存 Pod にはサイドカーが後から入らないため、ラベル付与後に再起動する:

```sh
kubectl rollout restart deployment -n knowledge-platform
```

## 2. STRICT mTLS の適用（Helm が宣言）

`mesh.enabled=true` / `mesh.mtlsMode=STRICT`（既定）で、以下がレンダリング・適用される:

- `PeerAuthentication knowledge-platform-mtls`（`mtls.mode: STRICT`）
  — Namespace 内ワークロードが受け付ける接続を mTLS のみに限定。
- `DestinationRule knowledge-platform-mtls`（`trafficPolicy.tls.mode: ISTIO_MUTUAL`）
  — 同 Namespace 宛の送信 TLS をメッシュ証明書での相互 TLS に固定。

> 実クラスタへの初回導入時は、一時的に `mesh.mtlsMode=PERMISSIVE` で移行を確認してから
> `STRICT` へ切り替える運用が安全（平文とmTLSを併存させて段階移行）。

## 3. Kiali（可観測性）

メッシュのトラフィック・mTLS 状態を可視化する Kiali を配備する。

```sh
kubectl apply -f https://raw.githubusercontent.com/istio/istio/release-1.24/samples/addons/kiali.yaml
kubectl apply -f https://raw.githubusercontent.com/istio/istio/release-1.24/samples/addons/prometheus.yaml
istioctl dashboard kiali
```

Kiali の Security バッジ（鍵アイコン）で、サービス間エッジが mTLS であることを確認する。

## 4. 検証（平文が存在しないこと）

```sh
# 各ワークロードの mTLS 状態（STRICT であること）
istioctl authn tls-check <pod>.knowledge-platform

# サイドカーのリスナ設定に平文（PERMISSIVE/DISABLE）が無いこと
istioctl proxy-config listener <pod> -n knowledge-platform

# PeerAuthentication が STRICT で適用されていること
kubectl get peerauthentication -n knowledge-platform -o yaml
```

受け入れ基準「平文の内部通信が存在しない」は、`istioctl authn tls-check` が全エッジで
`STRICT` を報告し、サイドカー未注入クライアントからの平文到達が拒否されることで確認する。
