---
title: "Headlamp の apiserver OIDC 配線を http issuer 制約により blocked と確定し、SA トークン手順を恒久化する（Issue #328）"
type: spec
status: done
related_ids:
  - IADR-0104
  - IADR-0084
  - IADR-0080
  - IADR-0103
  - NFR
author: claude
created: 2026-07-26
updated: 2026-07-26
related_specs:
  - "../adr/IADR-0104_headlamp-apiserver-oidc-blocked-on-http-issuer.md"
  - "../adr/IADR-0084_headlamp-oidc-apiserver-flags.md"
  - "../adr/IADR-0080_headlamp-k8s-management-ui.md"
  - "../operations/local-sso-recovery-runbook.md"
  - "../../deploy/local/README.md"
  - "../../deploy/local/headlamp/headlamp.yaml"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: Headlamp apiserver OIDC の blocked 確定と SA トークン手順の恒久化（Issue #328）

## 起点

`#328`（#271 / PR #327 のフォローアップ）。当初の要求は「`scripts/k8s-local-up.sh` に apiserver OIDC
フラグの opt-in 配線を追加し、Headlamp の OIDC ログイン（SA トークンに頼らないブラウザログイン）を
恒久化する」だった。着手前の実機調査で、**この要求は現行の k8s では実現不能**であることが確定したため、
本 PR は要求を「blocked の確定＋実際に再現可能な手順の恒久化」へ読み替えて実装する。設計判断を伴うため
**IADR-0104** を採番する。

## 実機で確定した事実（2026-07-25・`k3s v1.35.4+k3s1` / Rancher Desktop 内蔵 k3s）

### 1. ブロッカーは k8s の https 強制であり、回避手段が無い

指示された 6 フラグを `/etc/rancher/k3s/config.yaml.d/99-headlamp-oidc.yaml` に配置して k3s を再起動すると、
**kube-apiserver が起動できずクラスタが停止する**。`%LOCALAPPDATA%\rancher-desktop\logs\k3s.log` に
19:46:33〜19:47:53 の間 **10 回連続**で記録されている:

```
Error: invalid authentication configuration: jwt[0].issuer.url:
  Invalid value: "http://keycloak:8080/realms/microservices-platform": URL scheme must be https
```

k8s 1.30+ はレガシー `--oidc-*` フラグを内部で構造化認証設定（`jwt[0]`）へ変換し、`issuer.url` に **https を
強制**する。scheme の例外・insecure 用の逃げ道は無い。19:48:03 にフラグ無しで再起動して復旧しており、
ドロップインは `/root/99-headlamp-oidc.yaml.disabled` として退避されたまま、`config.yaml.d/` は空である。

### 2. issuer を https にできないのは realm 側の固定に起因する

[`deploy/local/infra/keycloak.yaml`](../../deploy/local/infra/keycloak.yaml) は `KC_HOSTNAME_URL=http://keycloak:8080`
を指定しており、realm が発行する token の `iss` は**全経路で http に固定**されている。apiserver が受理できる
https issuer にするには Keycloak の hostname 自体を https へ移す必要があり、これは backend（`Auth__Authority`）・
ArgoCD・Grafana・MinIO・Vault・Wiki.js の `iss` 検証すべてに波及する＝**#388 の「全経路 HTTPS 化」そのもの**。

### 3. 到達性はブロッカーではない（誤診の記録）

apiserver → Keycloak の到達性は、ノード `/etc/hosts` に `<keycloak ClusterIP> keycloak` を追記すれば
discovery/JWKS ともに 200 で成立する。ただし **検証は k3s の netns から行う**必要がある（Rancher Desktop の
k3s は独自 netns で動くため、distro 既定の netns から測ると到達不可に見えて誤判定する）。

### 4. runbook が案内する SA トークン手順が、新規クラスタでは失敗する

[`docs/operations/local-sso-recovery-runbook.md`](../operations/local-sso-recovery-runbook.md) と IADR-0084 追記が
正規手順として案内する `kubectl -n platform-infra create token headlamp-viewer` の **`headlamp-viewer`
ServiceAccount と ClusterRoleBinding は、リポジトリのどこにも存在しない**（live クラスタには手作りで存在）。
`HEADLAMP=1 bash scripts/k8s-local-up.sh` を新規クラスタで実行しても作成されず、**唯一の正規ログイン手順が
NotFound で失敗する**。#328 の「再現可能化」の実体はこちらである。

### 5. k3d 経路にも同じ地雷が残っている

[`scripts/k8s-local-up.sh`](../../scripts/k8s-local-up.sh) の k3d 経路は `HEADLAMP_OIDC_APISERVER` 未設定時に
**`HEADLAMP` の値へ追従**して同じ 4 フラグを `k3d cluster create` へ付与する（IADR-0084 決定1）。k8s 1.30+ の
k3d でも同じ https 強制に当たるため、`HEADLAMP=1` と書いただけの利用者が**クラスタ作成に失敗する**。

## 変更内容

### コード / 自動化

| ファイル | 変更 |
| --- | --- |
| `deploy/local/headlamp/headlamp.yaml` | `headlamp-viewer` ServiceAccount ＋ 同名 ClusterRoleBinding（`cluster-admin`）を追加し、runbook の正規手順を **overlay 適用だけで再現可能**にする。`automountServiceAccountToken: false`（トークンは `kubectl create token` で都度発行し、Pod へは配らない） |
| `scripts/k8s-local-up.sh` | k3d 経路の apiserver OIDC 付与を `${HEADLAMP_OIDC_APISERVER:-${HEADLAMP:-}}` → **`${HEADLAMP_OIDC_APISERVER:-}`** へ変更し、`HEADLAMP` 追従を廃止する。明示的に `HEADLAMP_OIDC_APISERVER=1` と書いた利用者だけが従来動作（k8s 1.29 以前向け）。併せて危険性を警告する stderr メッセージを追加 |
| `scripts/k8s-local-up.test.js` | 回帰 4 件（下記） |

### ドキュメント

| ファイル | 変更 |
| --- | --- |
| `docs/adr/IADR-0104_*.md`（新規） | 決定 4 点と却下案（本 PR の設計判断） |
| `docs/adr/IADR-0084_*.md` | `status` を `Superseded`（by IADR-0104）へ。追記節から IADR-0104 へリンク |
| `docs/adr/README.md` | IADR-0104 の索引行 |
| `deploy/local/README.md` | Headlamp 節を全面改訂。冒頭に「現行 k8s では OIDC 不可・正規手順＝SA トークン」。apiserver フラグ手順は削除せず **#388 再開時の素材**として畳み、警告を付す。`config.yaml.d` のクォート必須・ノード `/etc/hosts` へ ClusterIP 投入・`generateHosts=false`・k3s 再起動はユーザー実行・再起動で Vault dev(インメモリ) が揮発するため bootstrap 再実行、を明記 |
| `docs/operations/local-sso-recovery-runbook.md` | Headlamp 行に「SA は overlay に恒久化済み（手動 apply 不要）」を追記 |

## 非対象

- **Headlamp の OIDC 化そのもの**: 全経路 HTTPS 化と同時に行う。**#388** で追跡する。
- **`Group oidc:platform-admin` / `User oidc:admin` の ClusterRoleBinding**: apiserver OIDC が成立しない現行では
  完全に inert（bind しても誰も `oidc:` 接頭辞の identity にならない）。未使用ケースへの防御的実装を足さない方針に
  従い、**#388 で apiserver OIDC 配線とセットで入れる**。live クラスタに手作りされた同名 CRB もリポには持ち込まない。
- **realm の `headlamp-realm-roles` mapper（`groups`）**: #389 / IADR-0103 で realm.json に恒久化済み。無改変。
- 本番 chart（`deploy/helm`）・compose・edge・ESO/Vault・frontend は無改変。

## 受け入れ基準と検証

- [x] `deploy/local/headlamp/` の overlay に `headlamp-viewer` SA と CRB が含まれ、`kubectl apply -k` で
      runbook の `kubectl -n platform-infra create token headlamp-viewer` が成立する
- [x] `HEADLAMP=1 bash scripts/k8s-local-up.sh`（k3d 経路）で apiserver OIDC フラグが **付かない**
- [x] `HEADLAMP_OIDC_APISERVER=1` を明示したときのみ従来どおり 4 フラグが付き、警告が stderr に出る
- [x] 既定（両 env 未設定）の `k3d cluster create` 引数は従来とバイト等価
- [x] `node --test scripts/` が全件 green
- [x] `node scripts/check-doc-links.js` が green（IADR-0104 の相互リンク・README の相対リンク）
- [x] CI（lint/build/test・gitleaks・doc-links・commit-messages）green

### 検証コマンド

```bash
node --test scripts/
node scripts/check-doc-links.js
```

## live（本 PR では実行しない）

- k3s 再起動を伴う操作は**破壊的なためユーザーが実行する**。本 PR は設定・手順の記述までに留める。
- #388 で https 化が済んだ後、実ブラウザで `admin` の Headlamp OIDC ログインを検証して #328 の当初要求を閉じる。
