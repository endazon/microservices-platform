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
  - IADR-0220
  - IADR-0227
author: claude
created: 2026-08-17
updated: 2026-08-21
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
related_specs:
  - "../adr/IADR-0227_edge-host-pod-side-resolution.md"
  - "../adr/IADR-0206_local-edge-tls-cert-manager.md"
  - "../adr/IADR-0086_oidc-issuer-metadata-split.md"
---

# 作業仕様書: Keycloak のエッジ公開と https issuer（#780 ＝ #442 の子 2）

> **本書は #780 を 2 段に分けて進める。第 1 段（土台）を [[IADR-0227]] として PR にした。**
>
> | 段 | 内容 | 状態 |
> | --- | --- | --- |
> | **1（本 PR）** | Keycloak をエッジへ出す ＋ エッジ host の pod 側解決を与える。**追加のみ・issuer は移さない** | **実装済み** |
> | 2 | `KC_HOSTNAME_URL` の変更・realm 作り直し・7 クライアントの redirect・[[IADR-0091]] 決定 5 の Supersede | 未着手 |
>
> 分けた理由は [[IADR-0227]]「射程」。第 1 段は**既存の挙動を 1 つも変えない**（issuer は旧値のまま動く）ため、
> 第 2 段は設定値の変更に集中できる。

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

## 8. 第 1 段（本 PR）の受け入れ基準と検証

- [x] Keycloak が `keycloak.localhost` の **websecure(443)** でエッジに出る（admin:50000 ではない）
- [x] エッジ host が **pod からも解決できる**（`coredns-custom`・`coredns` 本体は無改変）
- [x] 解決先は Traefik の**正準名**で、ClusterIP を焼き込まない
- [x] `apply` → `rollout restart` → `rollout status` の順序を検査で固定した
- [x] **既定（`LOCALEDGE` 未設定）はバイト等価**（`OPTIN_TOKENS` に 2 件追加）
- [x] **issuer は 1 バイトも変えていない**（`KC_HOSTNAME_URL` 不変・7 クライアントは現状のまま動く）

### 変異試験（10 通り・全件 RED）

| # | 壊し方 | 結果 |
| --- | --- | :---: |
| M1 | Ingress を kustomization から外す（ファイルだけ在って適用されない） | RED |
| M2 | entrypoint を `admin` へ変える | RED |
| M3 | host を素の `keycloak` にする | RED |
| M4 | 後段ポートを 8443 にする | RED |
| M5 | ConfigMap のキーを `.conf` にする（k3s の import glob に合わない＝置いても読まれない） | RED |
| M6 | 解決先を ClusterIP で焼き込む | RED |
| M7 | ConfigMap の namespace を変える | RED |
| M8 | `rollout restart` を落とす（置いたのに効かない状態） | RED |
| M9 | `coredns` の apply を落とす | RED |
| M10 | `restart` を `apply` より前へ動かす（古い ConfigMap で再起動する） | RED |
| — | 変異なし | GREEN（81 件） |

> **★ 検査が素通りする形を 1 度作った。** `sed` で正規表現を書き換えた際にバックスラッシュが落ち、
> `/router.entrypoints:s*(S+)/` という**何にも一致しない正規表現**になっていた。
> 一致しないので変数は空文字になり、`includes('admin')` は常に false ＝ **常に通るテスト**である。
> 気づけたのは変異試験ではなく**書いた直後に置換結果を目視した**からで、
> 変異試験（M2）は「別の検査」が拾っていた。**置換で正規表現を作らない**（ファイルへ書いて `node` で実行する）。

> **★ 変異試験の 1 度目はタイムアウトで中断し、変異が 1 つ作業ツリーに残った**（M7 の namespace 差し替え）。
> **変異試験は必ず復元を確認する** —— `git status` で確かめ、残っていたら戻す。

### ★ 着手中に develop が先行し、証明書が重複した

当初は `platform-infra` 側の `edge-tls` を本 PR で発行するつもりで書いていた。
develop を取り込んだ時点で、**[[IADR-0220]]（#841）が `edge-certificate.yaml` の中で既に同じものを
宣言していた**（`secretName` / `dnsNames` / `issuerRef` まで同一）ことが分かり、撤去した。
**着手前に引いた母集合は、着地までに腐る** —— 取り込みのたびに引き直すこと。

### 実機（2026-08-17・稼働中の k3s）

§4c の表のとおり。**その後クラスタが停止したため、本 PR の最終形での再実測はできていない**
（撤去した証明書 1 件を除き、検証時と同じマニフェストである）。

### 稼働クラスタに残っている状態

`kube-system/coredns-custom` ／ `platform-infra/keycloak-edge` Ingress が当たっている
（`platform-infra/edge-tls` は develop 由来）。撤去するなら:

```bash
kubectl -n kube-system delete cm coredns-custom
kubectl -n platform-infra delete ingress keycloak-edge
```

## 9. 第 2 段（#780 の残り）

1. `deploy/local/infra/keycloak.yaml` の **`KC_HOSTNAME_URL`** を `https://keycloak.localhost` へ
2. **realm の作り直し**（§3 手順 A。稼働 realm は旧名 `microservices-platform` のまま）
3. 7 クライアントの redirect / logout URI（`platform-spa` は `##` 区切り）
4. `check-realm-constraints.js` の `REQUIRED_CLIENT_URLS` に https 版
5. [[IADR-0091]] 決定 5 と却下代替案の Supersede
6. **admin:50000 の TLS 化は [[IADR-0220]] が済ませた** —— #780 の該当基準は既に満たされている
