---
title: IADR-0227 Keycloak をエッジの websecure へ出し、エッジ host の pod 側解決は coredns-custom で与える
type: impl-adr
status: Accepted
related_ids:
  - NFR-09
  - ADR-0004
  - ADR-0023
  - IADR-0076
  - IADR-0086
  - IADR-0091
  - IADR-0103
  - IADR-0206
  - IADR-0220
author: claude
created: 2026-08-20
updated: 2026-08-21
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
---

# IADR-0227: エッジ host の pod 側名前解決（`coredns-custom`）と Keycloak のエッジ公開

- 状態: Accepted
- 日付: 2026-08-20
- 決定者: claude（実装）

## 起点・関連

- Issue: **#780**（#442 の子 2）。仕様書: `../specs/20260817_issue-780_keycloak-edge-https-issuer.md`。
- 先行: [[IADR-0206]]（#779。`edge-tls` と安定した CA）／[[IADR-0220]]（#841。admin:50000 の TLS 終端と
  `platform-infra` 側の葉証明書）。
- **本 ADR は #780 の受け入れ基準を満たさない。** issuer は移していない（下記「射程」）。

## 射程 —— issuer は移さない

#780 は「OIDC issuer を https のエッジ host へ移す」ことを求めるが、**本 ADR が決めるのはその前段だけ**である。

| 本 ADR | #780 の残り |
| --- | --- |
| Keycloak が**エッジに出ている**こと | `KC_HOSTNAME_URL` の変更（issuer の単一情報源） |
| エッジ host が **pod からも引ける**こと | realm の作り直し（稼働 realm は旧名のまま） |
| — | 7 クライアントの redirect / logout URI |
| — | [[IADR-0091]] 決定 5 の Supersede |

**分けた理由**: issuer を変える作業は **realm・7 クライアント・46 以上のファイル**が同時に動く。
一方、本 ADR の 2 点は**追加のみで既存の挙動を 1 つも変えない**（issuer は旧値のまま動き続ける）。
先に土台だけを入れておくと、issuer 切り替えの PR は**設定値の変更に集中できる**。
[[IADR-0116]] 規約 4（着手時の分割）に沿う。

## 決定

### 1. エッジ host の pod 側解決は `kube-system/coredns-custom` で与える

**k3s の CoreDNS は Corefile の末尾に拡張点を持つ。**

```
    import /etc/coredns/custom/*.override      （サーバブロックの内側）
}
import /etc/coredns/custom/*.server            （トップレベル）
```

そして `coredns` Deployment は **`coredns-custom` ConfigMap を optional で既にマウントしている**
（`custom-config-volume` @ `/etc/coredns/custom`）。**このファイルは存在しなかった**（実測）。

| 案 | 採否 |
| --- | --- |
| **`coredns-custom` を置く** | **採用**。**k3s が管理する `coredns` ConfigMap を 1 バイトも触らない**（触っても再適用で戻される）。optional マウント済みなので**置けば効き、消せば元に戻る**（fail-safe）。対象は 1 箇所 |
| 各 Deployment へ `hostAliases` | 却下。**7 つの OIDC クライアント＋今後増える pod すべて**に書く必要があり、**追加を忘れた pod だけが静かに誤解決する**。母集合の規則 7 が破れる形そのもの |
| `ExternalName` Service | **不可**。`keycloak.localhost` は**ドットを含むため Service 名にできない**（DNS-1035 label 制約）。[[IADR-0103]] の既存エイリアスが素の `keycloak` にしか使えないのはこのためである |
| `coredns` ConfigMap を直接編集 | 却下。k3s の管理物である |

**なぜ pod 側の解決が要るのか**: [[IADR-0086]] の metadata / issuer 分離は **.NET 側しか救わない**。
Grafana / ArgoCD / Vault / MinIO / Headlamp / Wiki.js は **pod から issuer host を実際に引いて
discovery を叩く**。ここが解決できなければ、issuer を移した瞬間に 6 つのツールが同時に壊れる。

### 2. 解決先は Traefik の**正準名**であり、ClusterIP を焼き込まない

`rewrite` で `*.localhost` を `traefik.kube-system.svc.cluster.local` へ向ける。
ClusterIP を書くと **Service 再作成で静かに壊れる**（引けるが誰も居ないアドレスを返す）。検査で固定した。

### 3. ★ 塞いでいるのは「引けないこと」だけではない —— 誤解決も塞ぐ

[[IADR-0103]] が記録した事故の再来を防ぐ。CoreDNS が答えを持たないと、クラスタ外の名前として
**ノードのリゾルバへフォールスルー**する。`*.localhost` は手順 A の hosts 追記や OS の既定で
**`127.0.0.1` に解決されるのが普通**であり、その場合 **pod は自分自身へ discovery を投げて 404** になる
（argocd-server で実際に起きた形）。

**つまり本 ConfigMap は「解決できるようにする」ためだけでなく、「誤って自分自身を指さないようにする」
ためにも要る。** 既存の `keycloak` ExternalName エイリアス 3 件は**残す**（[[IADR-0086]] の
`MetadataAddress`（in-cluster）経路がまだ生きており、その解決に要る）。撤去は別 issue。

### 4. `import` の追加は `reload` では拾われないので `rollout restart` する

`reload` プラグインが見るのは **Corefile 自身**である。`import` 先のファイルを増やしても
Corefile のバイト列は変わらないため、**置いたのに効かない**状態になりうる。
`k8s-local-up.sh` は apply の直後に `rollout restart` と `rollout status` を置く。**順序を検査で固定した。**

### 5. Keycloak は `websecure`(443) の `keycloak.localhost` へ出す（admin:50000 ではない）

Keycloak は「管理ツール」ではなく**認証の基盤**であり、7 つの OIDC クライアントと SPA がブラウザから叩く。
**admin:50000 に置くと redirect URI が全クライアントで `:50000` 付きになり**、[[IADR-0220]] の改定と
7 クライアントの追記に波及する（#780 が意図的にスコープ外にした領域である）。

**パスは絞らない。** `/realms` と `/resources` だけを通すと `login-actions` やアカウント管理が 404 になり、
**ブラウザ OIDC が成立しない**。管理コンソール（`/admin`）の露出可否は admin:50000 の議論と併せて別途扱う。

### 6. 葉証明書は作らない —— [[IADR-0220]] が既に `platform-infra` に置いている

`Ingress` の `spec.tls.secretName` は**同じ namespace の Secret しか参照できない**ため
`platform-infra` にも `edge-tls` が要るが、**[[IADR-0220]]（#841）が `edge-certificate.yaml` の中で
既に宣言している**（`secretName` / `dnsNames` / `issuerRef` まで同一）。

> **★ これは着手中に develop が先行した実例である。** 本 PR は当初、同じ証明書を独立に書いていた。
> develop を取り込んだ時点で**完全な重複**と分かり撤去した。**着手前に引いた母集合は、着地までに腐る。**

## 影響・トレードオフ

- **既定（`LOCALEDGE` 未設定）は一切変わらない。** ConfigMap も Ingress も opt-in の中にしか無い
- **`localhost` ゾーンだけを預かる**ため、他の名前解決に影響しない（実測で in-cluster 解決は無傷）
- **CoreDNS を再起動する。** `LOCALEDGE=1` の起動時に一度だけで、`rollout status` で待つ
- **issuer は移らない。** discovery は従来どおり `http://keycloak:8080/...` を返し、
  7 クライアントは**現状のまま動き続ける**

## 検出しないこと（明示）

- **pod から実際に引けるかは CI では検査しない**（クラスタが要る）。静的検査が固定するのは
  「ConfigMap のキーが k3s の import glob に合う」「解決先が正準名である」「apply → restart の順序」まで
- **ブラウザ OIDC の成立**は #780 の残りが扱う（issuer を移すまで意味を持たない）
- **admin:50000 の TLS 化**は [[IADR-0220]] が済ませており、本 ADR は触らない

## 実機で確認したこと（2026-08-17・稼働中の k3s）

| 観点 | 実測 |
| --- | --- |
| pod から `keycloak.localhost` | `traefik.kube-system.svc.cluster.local` → Traefik の ClusterIP |
| pod から HTTP | **200**（Traefik へ到達） |
| **回帰** | `qdrant.platform-infra…:6333` / `keycloak:8080` とも **200**（in-cluster 解決は無傷） |
| エッジ経由の Keycloak | `https://keycloak.localhost/realms/…` → **200**、ホストからの TLS 検証 `Verify return code: 0 (ok)` |

> **★ `nslookup` の出力に騙されかけた。** busybox の `nslookup` は A を返していても
> **AAAA が無いと `*** Can't find …: No answer` を出す**。これを見て「in-cluster DNS を壊した」と
> 一度判断しかけた。**名前解決の判定は出力の書式ではなく到達で見ること。**
