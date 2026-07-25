---
title: IADR-0104 Headlamp の apiserver OIDC 配線は http issuer 制約により実装しない（blocked・#388 待ち）。代わりに SA トークン手順を overlay へ恒久化し、k3d の危険な自動追従を止める
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0004
  - IADR-0066
  - IADR-0076
  - IADR-0080
  - IADR-0084
  - IADR-0103
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md (認証＝Keycloak に一元化)"
  - "../../planning/projects/microservices-platform/02_requirements/ (NFR 運用性＝再構築の再現性・セキュリティ)"
author: claude
created: 2026-07-26
updated: 2026-07-26
---

# IADR-0104: Headlamp の apiserver OIDC 配線は実装せず、SA トークン手順を恒久化する

- 状態: Accepted
- 日付: 2026-07-26
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性＝経路B を再構築しても Headlamp へ入れること・セキュリティ＝認証を Keycloak に
  一元化する方針）／ADR-0004（認証＝Keycloak）
- 関連 ADR: [[IADR-0084]]（#328 の当初決定＝k3d cluster create へ OIDC フラグを配線する。本 ADR が **Superseded**
  にする）／[[IADR-0080]]（#271。Headlamp を OIDC token passthrough で導入）／[[IADR-0066]]・[[IADR-0076]]
  （経路B の issuer は in-cluster 正準名 `http://keycloak:8080`）／[[IADR-0103]]（#354。realm の `admin` 恒久化と
  ツール別 claim。headlamp の `groups` mapper は本 ADR の制約により inert）
- 関連仕様書: `docs/specs/20260726_issue-328_headlamp-apiserver-oidc-blocked.md`
- Issue: #328（#271＝PR #327 のフォローアップ）。再開の追跡は **#388**（全経路 HTTPS 化）

## コンテキストと課題

#328 の要求は「`scripts/k8s-local-up.sh` に apiserver OIDC フラグの opt-in 配線を追加し、Headlamp へ SA トークン
ではなくブラウザの Keycloak ログインで入れる状態を恒久化する」だった。[[IADR-0084]] は k3d 経路にこれを実装済みで、
Rancher Desktop（内蔵 k3s）経路の同等配線が残件だった。

着手前の実機調査で、**この要求は現行の k8s では実現不能**であることが確定した。決めるべき論点は 4 点:
(1) 要求どおり配線するか、(2) 実際に再現可能な代替手順は何か、(3) [[IADR-0084]] が k3d 経路に残した既存配線を
どうするか、(4) OIDC 用 RBAC を今入れるか。

## 実機で確定した事実（2026-07-25・`k3s v1.35.4+k3s1` / Rancher Desktop 内蔵 k3s）

`/etc/rancher/k3s/config.yaml.d/99-headlamp-oidc.yaml` に 6 フラグ（issuer/client-id/username-claim/username-prefix/
groups-claim/groups-prefix）を配置して k3s を再起動すると、**kube-apiserver が起動できずクラスタが停止した**。
`%LOCALAPPDATA%\rancher-desktop\logs\k3s.log` に 19:46:33〜19:47:53 の間 **10 回連続**で記録されている:

```
Error: invalid authentication configuration: jwt[0].issuer.url:
  Invalid value: "http://keycloak:8080/realms/microservices-platform": URL scheme must be https
```

19:48:03 にフラグ無しで再起動して復旧しており、ドロップインは `/root/99-headlamp-oidc.yaml.disabled` へ退避された
まま `config.yaml.d/` は空である（＝本 ADR 執筆時点で apiserver に `oidc-*` 引数は無い）。

なおその直前 19:46:23 には、prefix 値の**クォート漏れ**による別の起動失敗も記録されている
（`Error: unknown flag: --[{oidc-groups-prefix`）。値に末尾コロンを含む項目は YAML が map と解釈するためである。
これは #388 で再開する際に再び踏む罠なので手順として残す（[[IADR-0084]] 追記と同旨）。

## 決定

### 1. #328 の apiserver OIDC 配線は実装しない（blocked）。再開は #388 と同時

k8s 1.30+ はレガシー `--oidc-*` フラグを内部で構造化認証設定（`jwt[0]`）へ変換し、`issuer.url` に **https を強制**
する。scheme の例外も insecure 用の逃げ道も無い。そして [`deploy/local/infra/keycloak.yaml`](../../deploy/local/infra/keycloak.yaml)
は `KC_HOSTNAME_URL=http://keycloak:8080` を指定しており、realm が発行する token の `iss` は**全経路で http に
固定**されている。したがって「apiserver が受理できる issuer」と「realm が発行する issuer」は**両立し得ない**。

issuer を https へ移すには Keycloak の hostname 自体を変える必要があり、backend（`Auth__Authority`）・ArgoCD・
Grafana・MinIO・Vault・Wiki.js の `iss` 検証すべてに波及する。これは #328 の範囲を超え、**#388（全経路 HTTPS 化）
そのもの**である。よって #328 では配線せず、#388 で HTTPS 化と同時に行う。

### 2. 正規手順は SA トークン方式とし、その SA を overlay へ恒久化する

[[IADR-0084]] 追記と `docs/operations/local-sso-recovery-runbook.md` は、暫定の正規手順として
`kubectl -n platform-infra create token headlamp-viewer` を案内している。ところが **`headlamp-viewer`
ServiceAccount と ClusterRoleBinding はリポジトリのどこにも存在せず**、live クラスタに手作りされているだけだった。
新規クラスタで `HEADLAMP=1 bash scripts/k8s-local-up.sh` を実行しても作成されないため、**唯一の正規ログイン手順が
NotFound で失敗する**。#328 が求めた「再現可能化」の実体はここにある。

`deploy/local/headlamp/headlamp.yaml`（opt-in overlay）に `headlamp-viewer` SA と同名 ClusterRoleBinding
（`cluster-admin`）を追加する。fail-safe の考え方は [[IADR-0080]] から変えない:

- **Headlamp Pod が使う SA（`headlamp`）には引き続き広域権限を bind しない**。無認証でクラスタは可視化できない。
- 権限を持つのは `headlamp-viewer` という**別 SA** で、Pod には割り当てない（`automountServiceAccountToken: false`）。
  トークンは利用者が `kubectl create token`（既定 1h・`--duration` で調整）で**都度発行**する短命トークンであり、
  Secret としてクラスタにもリポにも常駐しない。
- overlay は `deploy/local/infra` の base に含めない（`HEADLAMP=1` のときだけ適用される）。dev 限定・ローカル閉域。

### 3. k3d 経路の `HEADLAMP` 追従を廃止する（明示 opt-in のみ残す）

[[IADR-0084]] 決定1 は `${HEADLAMP_OIDC_APISERVER:-${HEADLAMP:-}}` として、`HEADLAMP=1` だけで apiserver OIDC
フラグが付く設計にした。k8s 1.29 以前では妥当だったが、**現行の k3d（k8s 1.30+）では同じ https 強制に当たるため、
`HEADLAMP=1` と書いただけの利用者が `k3d cluster create` に失敗する**。「Headlamp を有効にする」意図が
「クラスタが作れなくなる」に化けるのは fail-safe ではない。

`${HEADLAMP_OIDC_APISERVER:-}` へ変更し、**明示的に `HEADLAMP_OIDC_APISERVER=1` と書いた利用者だけ**が従来動作
（k8s 1.29 以前向け・自己責任）になるようにする。有効時は stderr に「k8s 1.30+ では apiserver が起動できない」旨の
警告を出す。既定（両 env 未設定）の `k3d cluster create` 引数は**従来とバイト等価**のまま。

これは機能の削除ではなく既定の変更である。フラグ生成ロジック・上書き env（`HEADLAMP_OIDC_ISSUER_URL` /
`HEADLAMP_OIDC_CLIENT_ID`）・既存クラスタ reuse 時の WARN は [[IADR-0084]] のまま残し、#388 で再利用する。

### 4. OIDC 用 ClusterRoleBinding は今は入れない

live クラスタには調査中に手作りされた `headlamp-oidc-platform-admin`（`Group oidc:platform-admin` ＋
`User oidc:admin` → `cluster-admin`）が存在するが、**リポジトリには持ち込まない**。apiserver OIDC が成立しない
現行では `oidc:` 接頭辞を持つ identity が一切生成されないため、この bind は**完全に inert**（誰にも権限を与えない）。
「起こり得ないケースへの防御的実装を足さない」という本リポの方針に従い、**#388 で apiserver OIDC 配線とセットで
入れる**。realm 側の `headlamp-realm-roles` mapper（`groups`）は [[IADR-0103]] で恒久化済みのため無改変
（同じく #388 まで inert）。

## 影響

- **再現可能化**: `HEADLAMP=1 bash scripts/k8s-local-up.sh` だけで runbook の SA トークン手順が成立する
  （手動 `kubectl create sa` / `create clusterrolebinding` が不要になる）。
- **fail-safe の向上**: k3d 利用者が `HEADLAMP=1` でクラスタを壊す経路が塞がる。既定経路はバイト等価。
- **#388 への引き継ぎ**: https 化の際に必要な材料（ドロップインの記法・prefix のクォート必須・ノード `/etc/hosts` へ
  Keycloak ClusterIP を投入・`generateHosts=false`・k3s netns からの到達性検証・OIDC 用 CRB）は
  `deploy/local/README.md` に残す。削除しない。
- 本番 chart（`deploy/helm`）・compose（経路A）・edge・ESO/Vault・realm・frontend は無改変。
- **k3s の再起動を伴う操作は破壊的なため利用者が実行する**（スクリプトは行わない）。再起動すると dev Vault
  （インメモリ）の状態が失われるため、`deploy/local/vault/eso/bootstrap.sh` と `deploy/local/vault/oidc/bootstrap.sh`
  の再実行が要る（[[IADR-0103]] の runbook STEP 0）。

## 却下した代替案

- **要求どおり http issuer でドロップインを配置する**: 実機で 10 回連続の apiserver 起動失敗を確認済み。利用者が
  k3s を再起動した瞬間にクラスタが停止し、手でドロップインを退避するまで復旧しない。再現可能化どころか
  **再現可能な障害**を出荷することになる。
- **Keycloak だけを https 化して headlamp 用の issuer を分ける**: `KC_HOSTNAME_URL` が realm 全体の `iss` を固定
  しているため「headlamp のときだけ https の iss」にはできない。hostname を変えれば全ツールの `iss` が変わる＝#388。
- **structured authentication config（`--authentication-config`）を渡す**: 新方式でも `issuer.url` の https 検証は
  同じ（実測のエラー自体が `jwt[0]`＝変換後の構造化設定に対するもの）。記法を変えても通らない。
- **`oidc-ca-file` を添えて自己署名 https にする**: apiserver 側は満たせるが、realm が発行する `iss` が http のままなので
  token 検証が一致しない。結局 §1 の hostname 変更（#388）が先に要る。
- **#328 のスクリプト配線だけ先に入れて、既定オフで眠らせる**: 使えないゲートとテストが残り、「設定すれば動く」という
  誤ったシグナルを出す。#388 で issuer が https になった時点で、そのとき正しい形で入れるほうが安全。
- **`headlamp-viewer` を documentation だけで案内し続ける（現状維持）**: 「正規手順を書いたが新規クラスタでは
  その手順が失敗する」状態が残る。#328 の主旨（再現可能化）に真正面から反する。
- **`headlamp` Pod の SA に直接 cluster-admin を bind する**: Pod にマウントされたトークンで**無認証の閲覧**が
  成立してしまい、[[IADR-0080]] の fail-safe（ログイン無しでは可視化不可）が崩れる。別 SA ＋ 都度発行に留める。
