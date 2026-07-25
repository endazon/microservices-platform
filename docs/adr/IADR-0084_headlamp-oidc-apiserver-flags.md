---
title: IADR-0084 k3d クラスタ作成に apiserver OIDC 検証フラグを opt-in（HEADLAMP 連動）で配線し、issuer は in-cluster 正準名・claim は #271 の username bind に一致させる
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0004
  - IADR-0066
  - IADR-0076
  - IADR-0080
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_auth-keycloak.md (認証＝Keycloak)"
  - "../../planning/projects/microservices-platform/02_requirements/ (NFR 運用性・セキュリティ)"
---

# IADR-0084: k3d クラスタ作成への apiserver OIDC 検証フラグの opt-in 配線

- 状態: Accepted（**ただし k8s 1.30+ では本 ADR の手順は成立しない。下記「⚠️ 2026-07-25 追記」を必ず読むこと**）
- 日付: 2026-07-19
- 決定者: claude（実装）

## ⚠️ 2026-07-25 追記: k8s 1.30+ では http issuer を拒否するため本手順は適用不能（IADR-0103 / #354 / #388）

本 ADR の 4 フラグをそのまま渡すと、**Kubernetes 1.30 以降では apiserver が起動できず、クラスタが停止する**。
実測（`k3s v1.35.4+k3s1`・Rancher Desktop 内蔵 k3s）で確認した事実:

```
invalid authentication configuration:
  jwt[0].issuer.url: Invalid value: "http://keycloak:8080/realms/microservices-platform":
  URL scheme must be https
```

k8s 1.30+ はレガシーな `--oidc-*` フラグを内部で**構造化認証設定（`jwt[0]`）へ変換**し、`issuer.url` に
**https を強制**する。経路B の issuer は in-cluster 正準名の **http**（`http://keycloak:8080/realms/...`。token の
`iss` と一致必須）なので、この検証を通せない。scheme の例外や insecure 用の逃げ道は無い。

**したがって現行 k8s では:**

- **Headlamp の正規ログイン手順は SA トークン方式**とする（`headlamp-viewer` SA が cluster-admin に bind 済み）:
  ```sh
  kubectl -n platform-infra create token headlamp-viewer --duration=24h
  ```
  → `http://headlamp.localhost:50000` を開き Token 方式で貼付。
- **apiserver への OIDC フラグ付与は行わない**（`HEADLAMP_OIDC_APISERVER` を 1 にしても、k8s 1.30+ では
  クラスタが起動しなくなる）。
- OIDC 化は **全経路 HTTPS 化と同時**に行う。追跡は **#388**（issuer を https へ統一し、apiserver に
  `oidc-ca-file` を含めて再配線する）。

### 併せて判明した実務上の注意（再発防止）

1. **`config.yaml.d` の YAML で末尾コロンを含む値はクォート必須**。未クォートだと YAML が map と解釈し、
   apiserver が `Error: unknown flag: --[{oidc-groups-prefix` で起動不能になる（＝クラスタ停止）。
   ```yaml
   kube-apiserver-arg:
     - "oidc-username-prefix=oidc:"   # ← クォート必須
     - "oidc-groups-prefix=oidc:"
   ```
2. **apiserver → Keycloak の到達性検証は k3s の netns から行う**。Rancher Desktop では k3s が独自の
   network namespace で動くため、distro の既定 netns から測ると ClusterIP/Pod IP が到達不可に見え**誤判定する**。
   ```sh
   nsenter -t <k3s pid> -n -m -- wget -q -O - http://keycloak:8080/realms/microservices-platform/.well-known/openid-configuration
   ```
   到達性そのものは（ノード `/etc/hosts` に `<keycloak ClusterIP> keycloak` を追記すれば）**discovery/JWKS ともに 200**
   で問題ない。ブロッカーは到達性ではなく上記の https 強制である。
3. **Rancher Desktop では up-script から apiserver 引数を付与できない**（k3s を作らないため）。付与するには
   `/etc/rancher/k3s/config.yaml.d/*.yaml` のドロップイン＋k3s 再起動が必要。また `/etc/hosts` は WSL が
   再生成するため、`[network] generateHosts = false`（`/etc/wsl.conf`）なしでは揮発する。

> 以下の本文は **2026-07-19 時点の決定（k8s 1.29 以前を前提）** としてそのまま残す。現行環境へ適用する際は
> 上記の制約が優先する。

## 起点・関連

- 関連する計画書 ID: NFR（運用性＝Headlamp/ブラウザ OIDC の実ログインを手順の暗記なしに再現可能にする・セキュリティ＝
  認証を Keycloak に一元化したまま k8s 認可まで通す）／ADR-0004（認証＝Keycloak）
- 関連 ADR: [[IADR-0080]]（#271。Headlamp を OIDC token passthrough で導入・RBAC は `oidc:developer`=User に
  cluster-admin を bind＝本 ADR がその live 前提を配線）／[[IADR-0066]]（経路B＝k3d dev 環境・issuer 正準名）／
  [[IADR-0076]]（issuer ホスト名の解き方＝手順A・in-cluster `http://keycloak:8080`）
- 関連仕様書: `docs/specs/20260719_issue-328_headlamp-oidc-apiserver-wiring.md`
- Issue: #328（運用/dev・priority:should。#271＝PR #327 のフォローアップ）

## コンテキストと課題

[[IADR-0080]] の Headlamp は **OIDC token passthrough**（利用者 `id_token` を k8s API server の Bearer へ委譲）で認証する。
実リソース閲覧には **API server が OIDC トークンを検証**する必要があり、これは apiserver の OIDC フラグで有効化する。
ところが **これらのフラグはクラスタ作成時にしか渡せず、既存クラスタには後付けできない**（apiserver は静的 pod 引数で
起動する）。現状フラグは `deploy/local/README.md` に手動 `k3d cluster create` 例として置かれるだけで、
`scripts/k8s-local-up.sh` には配線されていない。決めるべき実装論点は 4 点: (1) 有効化方式（既定変更 vs opt-in）、
(2) issuer の値（到達性）、(3) claim マッピング（#271 の RBAC への対応）、(4) 既存クラスタ再利用時の扱い。

## 決定

### 1. `HEADLAMP_OIDC_APISERVER`（既定＝`HEADLAMP` 追従）で opt-in 付与し、既定は現行の cluster create を不変に保つ

`scripts/k8s-local-up.sh` の k3d 経路 `k3d cluster create`（[1/7]）に、OIDC フラグを `HEADLAMP_OIDC_APISERVER` が `1` の
ときだけ append する。この env は**未設定なら `HEADLAMP` の値に追従**する（`${HEADLAMP_OIDC_APISERVER:-${HEADLAMP:-}}`）。

- `HEADLAMP=1` → Headlamp deploy と apiserver OIDC 配線が**一括で**有効化され、live 経路が 1 フラグで成立する。
- `HEADLAMP_OIDC_APISERVER=1` 単独 → Headlamp を deploy せずフラグのみ付与（apiserver OIDC の単体検証用）。
- `HEADLAMP_OIDC_APISERVER=0` ＋ `HEADLAMP=1` → フラグを付けない escape-hatch（既存クラスタ再利用で再作成したくない等）。
- **既定（両 env 未設定）は `k3d cluster create` がバイト等価**（既存の opt-in 慣習 `OBSERVABILITY`/`VAULT`/`ARGOCD`/
  `HEADLAMP` と同型・後方互換・fail-safe）。Headlamp を使わない利用者・CI には一切影響しない。

これは既定オンにしない理由でもある: OIDC フラグを既定の全クラスタに付けると、issuer 名が dev 前提（`keycloak:8080`）に
固定され、Headlamp を使わない用途にも Keycloak 依存を持ち込む。opt-in なら「Headlamp を使う人だけが明示的に有効化する」。

### 2. issuer は in-cluster 正準名 `http://keycloak:8080/realms/microservices-platform` を用いる

apiserver（cluster 内）から見た Keycloak の正準 issuer は in-cluster サービス名 `keycloak:8080` である（[[IADR-0066]] /
[[IADR-0076]] 手順A）。ブラウザ側も手順A（hosts に `127.0.0.1 keycloak` ＋ `port-forward svc/keycloak 8080:8080`）で
同一 issuer 文字列 `http://keycloak:8080` を共有するため、**token の `iss` が apiserver 検証と一致**する。issuer は
`HEADLAMP_OIDC_ISSUER_URL` で上書き可能（既定は正準名）。

到達性: apiserver は起動直後に issuer が未到達でも**ブロックせず背景で OIDC discovery/JWKS 取得をリトライ**する
（SA トークン認証は不変）。よって OIDC フラグ付きクラスタでも通常運用（SA ベース）は成立し、Keycloak 起動を待つ必要はない。

### 3. claim は `username-claim=preferred_username`＋`username-prefix=oidc:`（#271 の User bind に一致）。groups-claim は付けない

#271 の `deploy/local/headlamp/headlamp.yaml` の `ClusterRoleBinding` は `subjects: [{ kind: User, name: "oidc:developer" }]`
を bind している（**username subject**）。よって apiserver 側は `preferred_username=developer` を `oidc:developer` に
マップする username-claim/prefix を用いて既存 bind に一致させる。

- **`groups-claim` は付与しない**: #271 は group を一切 bind していないため、groups-claim を足しても有効な認可には寄与
  しない（inert）。#271 の RBAC（realm/manifest）は本 issue の非スコープで無改変とするため、**bind されている subject に
  対応する最小フラグ（username-claim/prefix）だけ**を配線する。これは「起こり得ない/未使用のケースへの防御的実装を足さない」
  という本リポの方針にも沿う。
- ロール/グループ別の権限分離検証は [[IADR-0080]] 決定2どおり `poc-*` の役割であり、`developer` は dev スーパーユーザー
  （[[IADR-0066]]）。将来 group ベース RBAC を導入する場合は、その RBAC 追加とセットで groups-claim を足す（本 ADR の外）。

### 4. 既存クラスタ再利用時は再作成を促す WARN を出す（クラスタは破壊しない）

apiserver フラグは作成時のみ有効なので、`k3d cluster list` にヒットして reuse する経路で OIDC 有効化が要求されたら、
「既存クラスタには後付け不可・`k3d cluster delete <cluster>` で再作成せよ」という **WARN を stderr に出す**。fail-safe:
スクリプトはクラスタを削除しない（破壊は利用者判断）。既定オフ時はこの WARN も出ない。

Rancher Desktop 経路はスクリプトが k8s を作成しないため配線対象外とし、k3s の同等手順（`--kube-apiserver-arg` を
Rancher の override 設定で与える）を `deploy/local/README.md` にドキュメントとして追記する。

## 影響

- **live の恒久化**: `HEADLAMP=1 bash scripts/k8s-local-up.sh` を新規クラスタで実行すれば、Headlamp deploy と
  apiserver OIDC 検証が同時に整い、手順A のブラウザ配線だけで `developer` の実ログイン→リソース閲覧が成立する。
- **fail-safe/後方互換**: 既定経路（両 env 未設定）は `k3d cluster create` 引数もその後の [2/7]..[7/7] も不変。CI
  （#275 image-mapping ドリフト・doc-links・commit-messages・realm-constraints）は本変更が touch しないため非回帰。
  realm.json・headlamp manifest・values は無改変。
- 本番像（Helm/argocd/compose）・datasource（#305）・frontend・edge・infra 永続化（#324）には影響しない。
- **実ブラウザでの end-to-end 疎通（#271 の live 受け入れ）**は稼働 k3d 依存＝live。PR で手順を明記し `Refs #328` で残す。

## 却下した代替案

- **既定オン（全クラスタに OIDC フラグ）**: Headlamp 非利用者・CI へ Keycloak 依存（issuer 名固定）を持ち込む。opt-in で
  「使う人だけが有効化」に留める（§1）。
- **`groups-claim`（例: `groups`）も付与**: #271 が group を bind していないため inert。RBAC 追加とセットでないと機能せず、
  未使用ケースへの防御的実装になるため付けない（§3）。
- **既存クラスタを自動 delete して再作成**: apiserver フラグ後付け不可を「勝手なクラスタ破壊」で解くのは危険（他の
  作業状態を失う）。WARN で再作成を利用者に委ねる（§4・fail-safe）。
- **専用 OIDC config ファイル（structured authentication config）を apiserver に渡す**: k8s の新方式だが k3d/k3s の
  マウント配線が増え、dev 用途には過剰。4 フラグの `--kube-apiserver-arg` で十分（README の既存手動手順とも一致）。
