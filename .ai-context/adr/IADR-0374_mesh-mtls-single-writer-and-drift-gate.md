---
title: IADR-0374 稼働の mTLS モードは helm だけが書き、乖離は門 G12 が落とす
type: impl-adr
status: Proposed
related_ids:
  - NFR
  - NFR-11
  - ADR-0005
  - ADR-0021
  - ADR-0026
  - IADR-0026
  - IADR-0107
  - IADR-0307
  - IADR-0317
  - IADR-0336
  - IADR-0369
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_security-requirements.md
---

# IADR-0374: メッシュ設定のドリフトを断つ（#1159）

- 状態: Proposed
- 日付: 2026-09-04
- 決定者: claude（実装）

## 起点・関連

- 計画 ADR: **ADR-0005**（サービスメッシュ = Istio）／**ADR-0021**（入口 = Istio Ingress Gateway）／**ADR-0026**（セキュリティ要求）
- 実装 issue: **#1159**（#442 の子）。開ける先: **#1168**（#1115 の未実測 3 点）・**#442**
- 先行: [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md)（STRICT mTLS が暫定運用を解消する）／
  [IADR-0307](./IADR-0307_istio-optin-and-staged-mtls.md)（Istio opt-in と段階的 mTLS）／
  [IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)（エッジの Istio 化と STRICT）／
  [IADR-0336](./IADR-0336_backchannel-logout-destination-and-mesh-boundary.md)（バックチャネルログアウトのメッシュ境界）／
  [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（門 G9〜G11 の作法）
- 作業仕様書: [20260904_issue-1159](../specs/20260904_issue-1159_mesh-mtls-declaration-as-single-writer.md)

## コンテキストと課題

#1159 は「稼働 k3s の `PeerAuthentication microservices-platform-mtls` が `kubectl patch` で STRICT へ
ドリフトし、メッシュ外の `ai-stock-trading` から MSP への平文が全断している」と実測し、
原因を **「`deploy/istio/README.md` の手順を手で実行したもの。おそらく #442 の作業」** と推測した。

🔴 **その推測は誤りである。ドリフトを起こしているのはリポジトリ内のスクリプトそのものであった。**

`scripts/istio-edge-up.sh` [5/5] と `scripts/istio-edge-down.sh` [1/4] は、helm が所有する
`PeerAuthentication` を `kubectl patch` で直接書き換えていた。#1159 が `managedFields` から読み取った
「06:42 に PERMISSIVE で apply → 6.5 時間後に STRICT へ patch」は、
[IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md) の実測（2026-08-30）で
この 2 本を往復させた跡そのものである。

`scripts/k8s-local-up.sh` は同じ値を **2 経路で**与えていた —— [6/7] の
`helm upgrade --set mesh.mtlsMode=…`（宣言）と、末尾から呼ぶ `istio-edge-up.sh` の
`kubectl patch`（宣言の外）である。**値が一致している間は誰も気付かない。**

## 実測（2026-09-04・k3s `v1.35.4+k3s1` / Istio `1.30.4` / Helm `v4.2.1`）

### 1. 🔴 patch は「ドリフト」では済まない —— **release を恒久的に壊す**

Helm 4 はサーバサイド apply（`manager: helm` / `operation: Apply`）を使う。`kubectl patch` は
`Update` 操作なので `.spec.mtls.mode` の所有権を奪い、以後の `helm upgrade` は**必ず失敗する**:

```console
$ kubectl -n microservices-platform patch peerauthentication microservices-platform-mtls \
    --type=merge -p '{"spec":{"mtls":{"mode":"STRICT"}}}'
   mode=STRICT gen=2 managers=helm/Apply,kubectl-patch/Update

$ helm upgrade msp deploy/helm/microservices-platform -n microservices-platform --reuse-values
Error: UPGRADE FAILED: conflict occurred while applying object
  microservices-platform/microservices-platform-mtls security.istio.io/v1, Kind=PeerAuthentication:
  Apply failed with 1 conflict: conflict with "kubectl-patch" using security.istio.io/v1: .spec.mtls.mode

$ helm upgrade ... --set mesh.mtlsMode=PERMISSIVE   → 同じ conflict
$ helm upgrade ... --take-ownership                 → 同じ conflict
$ helm upgrade ... --force                          → "cannot use server-side apply and force replace together"
```

**「もう一度 `k8s-local-up.sh` を流せば収束する」は成り立たない。** [6/7] の `helm upgrade` が
そこで落ち、`set -euo pipefail` の下で up 全体が止まる。復旧は人手が要る:

```console
$ kubectl -n microservices-platform delete peerauthentication microservices-platform-mtls
$ helm upgrade msp deploy/helm/microservices-platform -n microservices-platform --reuse-values
   mode=PERMISSIVE gen=1 managers=helm/Apply     ← 収束（本実測で実際に復旧させた）
```

### 2. STRICT は効く（陽性対照つき）

宣言的経路（helm）で STRICT へ上げ、`ai-stock-trading`（サイドカー無し）の使い捨て Pod から測った:

| 宛先 | PERMISSIVE | **STRICT** | 戻した後 |
| --- | --- | --- | --- |
| `document-service.microservices-platform:8080/health/live` | 200 | 🔴 **000（curl 56 / RST）** | 200 |
| `llmgateway-service.microservices-platform:8080/health/live` | 200 | 🔴 **000（curl 56 / RST）** | 200 |
| `keycloak.platform-infra:8080/…/openid-configuration`（対照・メッシュ外） | 200 | **200** | 200 |
| `configuration-service:8080/health/live`（対照・AST ns 内） | 200 | **200** | 200 |

**#1159 が報告した障害はこれである。** 対照が 200 のままなので「Pod が壊れている」ではない。

### 3. #1168（= #1115 の未実測 3 点）を STRICT で弁別できた

`mesh.backchannelLogout.fromOutsideMesh` を **helm で** false / true に振り、
MFA 済みの実利用者で BFF セッションを張ってから管理 API の全セッションログアウトを撃った:

| | 2 枚組**なし** | 2 枚組**あり** |
| --- | --- | --- |
| `/bff/auth/me`（ログイン後） | 200 | 200 |
| `/bff/auth/me`（logout-all の 5 秒後） | 🔴 **200**（失効が届いていない） | ✅ **401**（即時失効） |
| Keycloak `KC-SERVICES0057` | 🔴 **1 行**（`SocketException: Connection reset`） | ✅ **0 行** |
| BFF の `BackchannelLogoutProcessor` 受理ログ | 🔴 **無し** | ✅ **有り** |

**PERMISSIVE では両側とも通ってしまい弁別できない**（#1168 の 2026-09-04 コメントが記録した限界）。
STRICT で初めて 2 枚組が「効いている」ことが示せた。

開けた口が 1 URI に絞られていることも、メッシュ外（`platform-infra`）の使い捨て Pod から測った:

```
POST (plaintext) /bff/auth/backchannel-logout => 400   ← BFF まで届く（logout_token が不正）
POST (plaintext) /bff/auth/me                 => 403   ← AuthorizationPolicy が DENY
POST (plaintext) /internal/config/drift-run   => 403
POST (plaintext) /                            => 403
```

### 4. 測れなかったこと（陽性対照で切り分けた）

新規に作った検査用利用者では BFF ログインが `/bff/auth/callback` で 500 になった。
**これは mTLS とは無関係である** —— **PERMISSIVE でも同じ 500 になる**（対照）。
realm が MFA を要求しており、TOTP 未登録の利用者はパスワードの段の後に OTP 登録画面へ進むためで、
`scripts/lib/keycloak-login-form.js` ＋ `totp.js` で OTP の段を通す実利用者に替えたら 200 になった。
**「STRICT がログインを壊す」と結論しかけたが、対照を取って否定した。**

## 決定

### 決定 1 — 稼働の `mesh.mtlsMode` を書いてよいのは **helm だけ**

`scripts/lib/mesh-mtls-mode.sh` の `set_mesh_mtls_mode <MODE>` を唯一の口にし、
`istio-edge-up.sh` / `istio-edge-down.sh` の `kubectl patch` を撤去した。
中身は `helm upgrade --reuse-values --set mesh.mtlsMode=<MODE>` である。

**リリースが無ければ何もせず 0 で返る**（`istio-edge-down.sh` の冪等性。メッシュ未導入のクラスタで
走らせても壊さない）。モードの値域は `STRICT` / `PERMISSIVE` / `DISABLE` に閉じ、typo を非 0 で弾く。

### 決定 2 — 🔴 STRICT の宣言は**入口を Envoy へ移した後**に置く

`k8s-local-up.sh` は `LOCALEDGE=1` かつ `ISTIO_MTLS_MODE=STRICT` のとき、[6/7] では
**PERMISSIVE を宣言**し、`istio-edge-up.sh` が入口を移した後の [5/5] で STRICT へ上げる。

従前は [6/7] が `--set mesh.mtlsMode=${ISTIO_MTLS_MODE:-PERMISSIVE}` を直接渡していたため、
**入口がまだ `kube-system` の Traefik（メッシュ外）である間に STRICT が効き、残りの段が
入口 502 のまま進んでいた**（#1072 / IADR-0307 §4 が実測した形）。
IADR-0307 決定 4 が書いた段取り「注入 → 全 Pod Ready → PERMISSIVE で疎通確認 → STRICT」を、
初めてスクリプトの側で満たす。`ISTIO=1` 単独（`LOCALEDGE` 無し）のときは昇格の口が無いので
[6/7] が要求どおりのモードを宣言する（既存の警告はそのまま）。

### 決定 3 — 経路B の既定は **PERMISSIVE のまま据え置く**。STRICT は明示 opt-in

`ISTIO_MTLS_MODE` 未設定なら PERMISSIVE である（IADR-0307 決定 4 のまま）。
本番像 `values.yaml` の `mtlsMode: STRICT` は**変えない**（`MeshMtlsTests` が回帰固定）。

**理由は §実測 2 である** —— 経路B の `ai-stock-trading` はメッシュ外のテナントであり、
STRICT の間 AST→MSP の平文（ナレッジ保存・日報の LLM 生成・**取引判断の LLM 呼び出し**）が全断する。
恒久像は AST をメッシュへ入れること（受け皿は AST#627）であって、MSP 側で平文を許すことではない。

### 決定 4 — 🔴 **AST↔MSP の跨 namespace mTLS の扱いを明文化する**（#1159 受け入れ基準 4）

IADR-0026 は `microservices-platform` namespace の内側しか想定しておらず、
**AST が別 namespace のテナントとしてメッシュ外から到達すること**を扱っていなかった。
[IADR-0107](./IADR-0107_ast-owned-service-single-deployment.md) は MSP→AST の跨 namespace 参照を
宣言しているのに、逆向き（AST→MSP）のメッシュ上の扱いがどこにも無い。ここで埋める:

1. **MSP の namespace 単位 `PeerAuthentication` は「MSP 宛の受信」を支配する。**
   AST の chart は MSP の namespace へ `PeerAuthentication` を置かない（置けない）。
2. **STRICT の間、メッシュ外からの AST→MSP 平文は拒否される。これは障害ではなく決定の帰結である。**
   例外を開けるなら #1115 の 2 枚組と同じ形（ワークロード ＋ `portLevelMtls` ＋ URI を絞る
   `AuthorizationPolicy`）でしか開けない。**namespace 全体を PERMISSIVE へ落とすのは採らない**
   （IADR-0026 決定 1 に真正面から反する）。
3. **逆向き（MSP→AST）は auto-mTLS が非注入の宛先へ平文でフォールバックするので、
   STRICT の影響を受けない**（実測: `configuration-service` 200）。
   **片方向だけが落ちる**ことを覚えておくこと —— 「メッシュを STRICT にしたら疎通が壊れた」を
   両方向の問題と読むと切り分けを誤る。
4. **恒久像は AST を mesh へ入れること**（AST#627）。そのとき MSP 側は無改修でよい。

### 決定 5 — 門 **G12** を足す（**同型の事故の 2 回目**だから）

`scripts/check-stack-ready.js` に G12 を置き、`PeerAuthentication` / `AuthorizationPolicy` /
`DestinationRule` について次を要求する:

- (a) 稼働の集合が helm の描画（`helm get manifest`）と一致する（**描画に無い資材が稼働に在れば失敗**）
- (b) mTLS モードが宣言（`helm get values -a` の `mesh.*`）と一致する
- (c) 🔴 **`spec` を書いている field manager が `helm` ただ 1 つである**

**規約は「同型の事故が 2 回起きたら検査器を足す」である**（1 回目は記録に留める）。数えた:

| | いつ | 何が起きたか | 出典 |
| --- | --- | --- | --- |
| 1 回目 | 2026-08-30 | `istio-edge-up.sh` の `kubectl patch` が STRICT を残し、宣言と食い違った | #1159 の `managedFields` 実測 |
| 2 回目 | 2026-09-04 | #1115 の計測スクリプトが描画物を `kubectl apply` で当て、**戻さなかった** | #1168 の 2026-09-04 コメントが自ら記録 |

🔴 **(c) が本体である。** 2 回とも「値は合っているのに壊れている」時間帯があった。
Helm 4 の下では所有権を奪われた時点で `helm upgrade` が失敗するので、
**値の一致だけを見る門は「壊れているのに緑」を返す。**

`mesh.enabled=false` の構成では notice で飛ばす（G5 / G7 と同じ作法）。ただし
**宣言が無いのに稼働にメッシュ資材が在る**なら飛ばさず失敗にする ——
IADR-0317 が「動いているが宣言が持っていない」として**記録に留めた**形そのものであり、
本 ADR がそれを門にする。

### 決定 6 — 収束の不変条件は `scripts/k8s-local-up.test.js` が固定する

- **追跡下のどのファイルも `PeerAuthentication` を `kubectl patch` しない**
  （`git ls-files --cached --others` で母集合を引く。`--others` を併せるのは、
  **追跡前の新しいスクリプトが母集合から漏れると新しい違反だけが素通りする**ため。規則 10）
- 両スクリプトが `lib/mesh-mtls-mode.sh` を source し、`set_mesh_mtls_mode` を呼ぶ
- STRICT への昇格が Gateway 導入より後に来る（決定 2 の順序）
- G12 の前提（テンプレートが `mode` を values から描画する）
- ArgoCD の許可種別に `AuthorizationPolicy` が在る（次節）

## 見つけて直した欠陥 —— ArgoCD の許可種別が 1 つ欠けていた

`deploy/argocd/appproject.yaml` の `namespaceResourceWhitelist` は `PeerAuthentication` と
`DestinationRule` を許可するが、**`AuthorizationPolicy` を許可していなかった**。
`mesh.backchannelLogout.fromOutsideMesh=true` の配備では chart が同種別をレンダリングするため、
**ArgoCD の本番同期はその時点で止まる**。IADR-0307 が同じ形の欠落（6 種別）を直したときの引き直しは
`fromOutsideMesh` が存在しない時点のものであり、#1152 が種別を 1 つ増やしたのに追随していなかった。

**これで同型は 2 回目なので、上の不変条件テストに許可種別の突合を入れた**（記録に留めない）。

## 影響・トレードオフ

- **良い影響**: 宣言と稼働の食い違いが 1 つの門で落ちる。`k8s-local-up.sh` が「毎回同じ状態へ収束する」
  ことが構造的に成り立つ（宣言の外から書く経路をリポジトリが持たなくなった）。
  IADR-0307 決定 4 の段取りが初めてスクリプトの側で満たされた。
- **悪い影響 / トレードオフ**:
  - `set_mesh_mtls_mode` は `helm upgrade` を 1 回走らせる。`kubectl patch` より遅い（数秒）。
    release 全体を再適用するが、変わるのは `PeerAuthentication` の 1 フィールドだけで Pod は作り直されない。
  - 決定 2 により、STRICT を要求した再実行では **[6/7] で一度 PERMISSIVE に戻り、入口を移した後に
    STRICT へ戻る**（数分の緩みの窓）。これは IADR-0307 決定 4 の段取りそのものであり、
    **意図した挙動**として記録する（事故ではない）。
  - G12 は `helm` を 2 回、`kubectl` を 1 回追加で叩く。`check-stack-ready.js` の実行時間が数秒伸びる。
- **フォローアップ**:
  1. **AST をメッシュへ入れる**（AST#627）。入るまで経路B の既定は PERMISSIVE のままである（決定 3）
  2. `mesh.backchannelLogout.fromOutsideMesh` の既定を経路B で true にしている根拠は
     「Keycloak が platform-infra（メッシュ外）に居る」ことである。Keycloak をメッシュへ入れたら外す

## 代替案

- **稼働を正として宣言を STRICT へ昇格する**: 採らない。決定 3 のとおり AST が落ちる。
  「宣言と実効を一致させる」という #1159 の要求は、**どちらへ寄せても満たせる** ——
  寄せ先を決めたのは AST の可用性である。
- **`helm upgrade --take-ownership` / `--force` で patch を吸収する**: 実測で**どちらも効かない**（§実測 1）。
- **`kubectl patch` を残したまま門だけ足す**: 門は「壊れたこと」を教えるだけで、
  **壊すのはリポジトリ自身**という状態が残る。出どころを断つのが先である。
- **`kubectl apply --server-side --force-conflicts --field-manager=helm` で所有権を取り返す**:
  復旧の一手としては成立するが、**helm が次に適用する集合と 1 バイトでも違うと別のずれが生まれる**。
  復旧手順は「delete して helm に作り直させる」に統一した（実測で確認した唯一の経路）。
