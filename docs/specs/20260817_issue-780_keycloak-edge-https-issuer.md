---
title: 作業仕様書 — Keycloak をエッジへ出し OIDC issuer を https のエッジ host へ移す（#780）
type: spec
status: in-progress
related_ids:
  - NFR-09
  - FR-05
  - ADR-0004
  - ADR-0023
  - IADR-0076
  - IADR-0086
  - IADR-0091
  - IADR-0197
  - IADR-0206
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
related_specs:
  - "../adr/IADR-0206_local-edge-tls-cert-manager.md"
  - "../adr/IADR-0086_oidc-issuer-metadata-split.md"
---

# 作業仕様書: Keycloak のエッジ公開と https issuer（#780 ＝ #442 の子 2）

> **本書は着手前の設計固めである。** 実装は #821 の着地後に始める（`scripts/k8s-local-up.*` が交差するため）。

## 1. 起点

**#442 の子 2／5。** 先行の **#779**（PR #792・[[IADR-0206]]）で
`edge-tls`（`localhost` / `*.localhost` の SAN を持つ証明書）と安定した CA が k8s Secret として存在するようになった。
本子はそのエッジへ Keycloak を出し、**issuer を https のエッジ host へ移す**。

開ける先: **#388**（Headlamp の OIDC ログイン）／**#466**（統合スタックの E2E を CI で）／**#781**（apiserver OIDC 再配線）。

## 2. ★ 中核設計の決着 —— `coredns-custom` を使う（実測 2026-08-17）

issue が「**本子の必須設計事項**」とした「pod から新 issuer host を解決させる手段」を実測で決めた。

### 測ったこと

k3s の CoreDNS の `Corefile`（`kube-system/coredns` ConfigMap）は**末尾に拡張点を持っている**:

```
    import /etc/coredns/custom/*.override
}
import /etc/coredns/custom/*.server
```

Deployment 側も**既にマウント済み**である:

| volume | ConfigMap | optional | mountPath |
| --- | --- | :---: | --- |
| `config-volume` | `coredns` | — | `/etc/coredns` |
| `custom-config-volume` | **`coredns-custom`** | **`true`** | `/etc/coredns/custom` |

そして **`coredns-custom` ConfigMap は存在しない**（`Error from server (NotFound)`）。
現状 pod から `keycloak.localhost` は引けない（`Can't find keycloak.localhost: No answer`）。

### 決定（[[IADR-0091]] 決定 5 の Supersede と併せて新 IADR に書く）

**`kube-system/coredns-custom` ConfigMap を新規に置く。** 理由:

| 案 | 採否 |
| --- | --- |
| **`coredns-custom` を置く** | **採用**。**k3s が管理する `coredns` ConfigMap を 1 バイトも触らない**（触ると k3s の再適用で戻される）。既にマウントされ optional なので、**置くだけで効き、消せば元に戻る**（fail-safe）。対象が 1 箇所で済む |
| 各 Deployment へ `hostAliases` | 却下。**7 つの OIDC クライアント＋今後増える pod すべて**に書く必要があり、母集合の規則 7 が破れる形そのもの。追加を忘れた pod だけが静かに解決できない |
| `ExternalName` Service | **不可**。`keycloak.localhost` は**ドットを含むため Service 名にできない**（DNS-1035 label 制約）。issue の指摘どおり |
| `coredns` ConfigMap を直接編集 | 却下。k3s の管理物であり、再適用で失われる |

**解決先は Traefik の Service**（`kube-system/traefik`・`ClusterIP 10.43.227.97`・ports `80/443/50000`）。
**ClusterIP をハードコードしない** —— `.server` ブロックで `traefik.kube-system.svc.cluster.local` へ
CNAME/rewrite する形にし、再作成でアドレスが変わっても壊れないようにする。

> **これで [[IADR-0086]] の metadata / issuer 分離に頼らずに済む。** 同 ADR は .NET 側だけを救う仕組みで、
> Grafana / ArgoCD / Vault / MinIO / Headlamp / Wiki.js には使えなかった（issue の「最大の技術リスク」）。
> **pod から実際に到達できるなら、7 クライアント全部が同じ 1 つの issuer を使える。**
> .NET 側の `Auth:MetadataAddress` は**残す**（設定として無害で、CoreDNS が無い環境の逃げ道になる）。

## 3. 前提の障害 —— 稼働クラスタの realm 名が旧名のまま

リポジトリは `realms/platform` 42 件 / `realms/microservices-platform` **0 件**（#578 / [[IADR-0197]] で改名済み）だが、
**稼働中の Keycloak が持つ realm は旧名 `microservices-platform`** である。
[[IADR-0079]] が記録したとおり、**PVC 永続化により `--import-realm` が既存 realm をスキップする**ため。

**解消手段は [[IADR-0082]] 決定 4 が既に用意している。**

| 手段 | 適否 |
| --- | --- |
| **(A 破壊的)** `keycloak-data` PVC を消して Pod 再作成 → 空 PVC へ最新 `realm.json` を再投入 | **これを採る。** #457 の裁定で**既存データ資産はすべて破棄**と決まっており、失う恒久データが無い |
| (B 非破壊) 管理コンソール / `kcadm` の partial import | 採らない。realm 名そのものは partial import で変えられない |

**★ この作業は本 PR の diff には現れない**（クラスタ操作であってリポジトリ変更ではない）。
仕様書に手順として残し、受け入れ基準の確認時に実施する。

## 4. 母集合（着手前に引く・**実装時に引き直す**）

issue が挙げた 46 ファイル・7 クライアントは**起票時点の数**である。
[[IADR-0141]] と規則 10 に従い、**実装の着手時に自分で引き直す**（起票者の数えを検証せずに転記しない）。

### 引いた結果（2026-08-17・`git grep -l`・`planning` を除く）

| 軸 | 検索語 | 件数 |
| --- | --- | ---: |
| 1 | `http://keycloak:8080`（issuer の文字列そのもの） | **60** |
| 2 | `keycloak:8080`（ホスト:ポートだけの形） | **63** |
| 3 | `realms/platform` | **46** |
| 3b | `realms/microservices-platform`（**旧名**） | **20** |
| 4 | `KC_HOSTNAME` | 14 |
| 5 | `authority` / `ValidIssuers` / `MetadataAddress` | 35 |
| 6 | `keycloak.platform-infra` | 2 |

### ★ issue の「46 件」は生きた資産を 10 件取りこぼしていた

issue は軸 3（`realms/platform`）だけで数えていた。**軸 2 で引くと 63 件**あり、
記録類（`docs/adr` / `docs/specs` / `docs/superpowers`）を除いた**生きた資産 36 件**のうち、
**次の 10 件は `realms/platform` を含まないため 46 件に入っていなかった**。

```
deploy/local/aliases/argocd-externalnames.yaml
deploy/local/argocd/README.md
deploy/local/edge/README.md
deploy/local/minio-oidc/README.md
docs/operations/local-sso-recovery-runbook.md
docs/tech/20260707_wikijs-poc-record.md
scripts/k8s-local-up.sh
scripts/k8s-local-up.test.js
scripts/seed-abac-policies.js
scripts/verify-oidc-edge-flow.sh
```

**規則 2（あり得る形をすべて列挙してから引く）の実例である。** realm パスを含む形だけで引くと、
**ホスト名だけを持つ設定・スクリプト**が構造的に落ちる。

### 除外したものと理由

| 除外 | 件数 | 理由 |
| --- | ---: | --- |
| `docs/adr/` ・ `docs/specs/` ・ `docs/superpowers/` | 27 | **point-in-time の記録**。確定済みの仕様書・ADR は遡及書き換えしない（`traceability.repo.md`） |
| `realms/microservices-platform` の 20 件 | （上に含む） | **全件が `docs/specs`(11) と `docs/adr`(9)** ＝ #578 / [[IADR-0197]] 以前の記録。**生きた設定に旧名は 1 件も無い**ことを確認した |
| `planning/` | — | 計画リポの submodule |

## 4b. ★ 既存の ExternalName エイリアス機構と、その罠（[[IADR-0103]]）

母集合の引き直しで **`deploy/local/aliases/` の既存機構**が出てきた。**これは本子の設計に直接効く。**

`microservices-platform` / `ai-stock-trading` / `argocd` の 3 namespace に
**`keycloak` という名の `ExternalName` Service** が置かれ、`keycloak.platform-infra.svc.cluster.local` を指している
（稼働クラスタで実在を確認）。[[IADR-0103]] が置いた理由が、そのまま本子の罠である:

> エイリアスが無いと DNS はクラスタ内で解決できず**ノードのリゾルバへフォールスルー**し、
> 手順 A のために hosts へ入れた `127.0.0.1 keycloak` を拾ってしまう。結果 argocd-server は
> **自分自身の :8080** へ discovery を投げ、404 で OIDC が壊れる。

**`keycloak.localhost` でも同じことが起きうる。** `localhost` は**ノードのリゾルバ側で必ず何かに解決される**
名前空間であり、CoreDNS が答えを持たなければ `127.0.0.1` へ落ちる ——
つまり **pod が自分自身を Keycloak だと思い込む**。§2 の `coredns-custom` は
「解決できるようにする」だけでなく、**この誤解決を塞ぐためにも要る**。

**併せて決めること**: issuer が `keycloak.localhost` へ移った後、既存の `keycloak` エイリアス 3 件を
**残すか消すか**。残す（本 PR の判断）—— [[IADR-0086]] の `MetadataAddress`（in-cluster）経路が
まだ .NET 側で有効であり、エイリアスはその解決に要る。**消すのは別 issue**（射程を広げない）。

## 4c. 実機で検証したこと（2026-08-17・稼働中の k3s）

**設計の中核は実機で通った。** ここまでは**追加のみ**で、既存の挙動を 1 つも壊していない。

| 検証 | 結果 |
| --- | --- |
| `coredns-custom` を置いて CoreDNS を再起動 | `configmap/coredns-custom created` ／ rollout 成功 |
| pod から `keycloak.localhost` | **`traefik.kube-system.svc.cluster.local` → `10.43.227.97`**（Traefik の ClusterIP と一致） |
| pod から `http://keycloak.localhost/` | **HTTP 200**（Traefik の catch-all。到達している） |
| **回帰**: pod から `qdrant.platform-infra.svc.cluster.local:6333` | **200**（`10.43.188.64`。in-cluster 解決は無傷） |
| **回帰**: pod から `keycloak:8080`（既存 ExternalName 経由） | **200**（`10.43.179.173`） |
| `platform-infra` の 2 本目の `edge-tls` | **`Ready=True`**（同じ `local-edge-ca` ClusterIssuer から発行） |
| `keycloak-edge` Ingress ＋ TLS | ホストから **`Verify return code: 0 (ok)`**（`local-edge-root-ca` で検証・`subject=CN=localhost`） |
| **エッジ経由の Keycloak** | `https://keycloak.localhost/realms/microservices-platform` → **HTTP 200** |
| discovery の `issuer` | **`http://keycloak:8080/realms/microservices-platform`**（＝ **まだ移っていない**。次の作業） |
| `https://keycloak.localhost/realms/platform` | **404**（§3 の realm 名の食い違いを裏づける） |

> **★ `nslookup` の出力に騙されかけた。** busybox の `nslookup` は A を返していても
> **AAAA が無いと `*** Can't find …: No answer` を出す**。これを見て「in-cluster DNS を壊した」と
> 一度判断しかけた。**実到達（`wget`）で確かめ直したら 200 が返り、壊れていなかった。**
> 名前解決の判定は**出力の書式ではなく到達で見る**こと。

### 残っている作業（issuer を実際に移す）

1. `deploy/local/infra/keycloak.yaml` の **`KC_HOSTNAME_URL`** を `https://keycloak.localhost` へ
   （**issuer の単一情報源はこの 1 行**）。`KC_PROXY` / `KC_HOSTNAME_STRICT*` の要否も併せて決める
2. **realm の作り直し**（§3 手順 A）。`keycloak-data` PVC を消して `--import-readm` を再実行させる
3. 7 クライアントの redirect / logout URI（`platform-spa` は `##` 区切りの連結文字列）
4. `scripts/k8s-local-up.sh` の `LOCALEDGE=1` ブロックへ本 ConfigMap と Ingress の適用を足す
   （**#821 が着地したので `k8s-local-up.*` の交差は解消済み**）
5. `check-realm-constraints.js` の `REQUIRED_CLIENT_URLS` に https 版を宣言
6. 新 IADR（[[IADR-0091]] 決定 5 と却下代替案の Supersede を含む）＋ 静的検査 ＋ 変異試験

## 5. 受け入れ基準（issue より・現時点の状態）

- [ ] 稼働クラスタの realm 名がリポジトリと一致（§3 の手順 A）
- [ ] `/.well-known/openid-configuration` の `issuer` と発行 token の `iss` が**文字列として完全一致**
- [ ] ブラウザ OIDC を持つ 7 クライアントすべてでログインが成立
- [ ] `scripts/verify-oidc-edge-flow.sh` が **hosts 追記と port-forward の前提なしに**完走
- [ ] `check-realm-constraints.js` の `REQUIRED_CLIENT_URLS` に https 版 URL を宣言
- [ ] [[IADR-0076]] を改定するか、手順 B の既定化を決める新 IADR を起こす（**どちらかを明示的に選ぶ**）
- [ ] **[[IADR-0091]] 決定 5 と却下代替案「Keycloak も 50000 集約」を Supersede**
      （#779 から移してきた約束。#779 は issuer を 1 バイトも変えないため決定 3 のみを Supersede した）
- [ ] **admin:50000 の TLS 化を扱うか、扱わないなら別 issue へ送る**

## 6. 個別の落とし穴（issue が挙げたもの・着手時に再確認する）

- **Grafana は `GF_SERVER_ROOT_URL` から `redirect_uri` を一意生成する。** realm 側に redirect を足すだけでは実効経路が http のまま
- **ArgoCD の `server.insecure: "true"` は「エッジが平文 http」前提**。TLS 化したら判断が要る。
  加えて argocd-server 自身が discovery を叩くため metadata / issuer を分離できない（→ §2 の CoreDNS で解ける）
- **Wiki.js の `settings.host` だけは manifest 自動化不可**（DB / 管理 UI 保持・[[IADR-0095]]）。手作業が 1 本発生する
- **Vault は既に http / https 両方の `allowed_redirect_uris` を持つ**唯一のツールで realm 追記が不要
- **SPA は `redirect_uri: ${origin}/callback` で origin 由来**のため自動追随する
- `platform-spa` の `post.logout.redirect.uris` は **`##` 区切りの連結文字列**。3 フィールドすべてに追記が要る

## 7. 依存と順序

- **#779 は着地済み**（PR #792・[[IADR-0206]]）。`edge-tls` と CA は在る
- **実装の着手は #821 の着地後**。`scripts/k8s-local-up.*` が交差する（ゲート追加が要る）
- 開ける先: #388 ／ #466 ／ #781

## 8. セッション終了時点の状態（2026-08-17・引き継ぎ用）

**本仕様書とマニフェストは作成済み・未 PR である。** 実装（issuer の切り替え）はここから。

### 稼働クラスタに当たっているが、まだ develop に無いもの

次の 3 つは**実機で検証するために当てた**。いずれも**追加のみで、既存の挙動を壊していない**
（回帰は §4c の表で確認済み）。撤去したいときは下のコマンドで元に戻る。

| 実機の状態 | リポジトリ側 | 撤去 |
| --- | --- | --- |
| `kube-system/coredns-custom` ConfigMap | `deploy/local/aliases/coredns-edge-hosts.yaml` | `kubectl -n kube-system delete cm coredns-custom` |
| `platform-infra/edge-tls` Certificate | `deploy/local/edge/tls/keycloak-certificate.yaml` | `kubectl -n platform-infra delete certificate edge-tls` |
| `platform-infra/keycloak-edge` Ingress | `deploy/local/edge/keycloak-ingress.yaml` | `kubectl -n platform-infra delete ingress keycloak-edge` |

### まだ当てていない（＝ issuer は今も `http://keycloak:8080`）

`KC_HOSTNAME_URL` は 1 バイトも変えていない。**discovery が返す issuer は旧値のまま**であり、
7 つの OIDC クライアントは**すべて従来どおり動いている**。切り替えは §4c「残っている作業」の 1〜6 を
まとめて行う必要がある（issuer だけ変えて realm と redirect が追随していない状態は、ログインが全滅する）。

### ★ 採番はここでは決めない

**develop は本セッション中に `IADR-0226` まで進んだ**（着手時は `0213`）。
並行セッションが速いため、**採番は実装に着手する直前に引き直すこと**（過去に 3 回衝突している）。
