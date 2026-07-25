---
title: "Headlamp のローカルログインを token 方式として正式手順化し、apiserver OIDC フラグ方式を wontfix とする（Issue #328）"
type: spec
status: done
related_ids:
  - IADR-0084
  - IADR-0080
  - IADR-0103
  - NFR
author: claude
created: 2026-07-26
updated: 2026-07-26
related_specs:
  - "../adr/IADR-0084_headlamp-oidc-apiserver-flags.md"
  - "../adr/IADR-0080_headlamp-k8s-management-ui.md"
  - "../operations/local-sso-recovery-runbook.md"
  - "../../deploy/local/README.md"
---

# 仕様書: Headlamp の token ログイン正式手順化（Issue #328）

軽量仕様書（docs のみ・コード変更なし）。設計判断は既存 [IADR-0084](../adr/IADR-0084_headlamp-oidc-apiserver-flags.md)
の「⚠️ 2026-07-25 追記」が単一情報源であり、**新規 IADR は起こさない**。

## 起点と結論

`#328` は「`scripts/k8s-local-up.sh` に apiserver OIDC フラグを opt-in で配線し、Headlamp のブラウザ OIDC ログインを
恒久化する」ことを求めていた。**この方式は現行の k8s では実装不能**であり、**#328 は wontfix**、OIDC 化の追跡は
**#388（全経路 HTTPS 化）へ統合**する。本仕様書はその結論を利用者向け手順へ反映する docs 変更を対象とする。

## 根拠（実測・`k3s v1.35.4+k3s1` / Rancher Desktop 内蔵 k3s）

k8s 1.30+ はレガシー `--oidc-*` を構造化認証設定（`jwt[0]`）へ変換し `issuer.url` に **https を強制**する。一方
`deploy/local/infra/keycloak.yaml` の `KC_HOSTNAME_URL=http://keycloak:8080` により realm の `iss` は **http 固定**で
あり、両立し得ない。ドロップイン `/etc/rancher/k3s/config.yaml.d/99-headlamp-oidc.yaml` を置いて k3s を再起動すると、
apiserver が

```
Error: invalid authentication configuration: jwt[0].issuer.url:
  Invalid value: "http://keycloak:8080/realms/microservices-platform": URL scheme must be https
```

で **19:46:33〜19:47:53 に 10 回連続で起動失敗し、クラスタが停止**した（フラグを外して 19:48:03 に復旧）。
issuer を https へ移すと backend/ArgoCD/Grafana/MinIO/Vault/Wiki.js の `iss` 検証すべてに波及するため、#388 の範囲。

## 変更内容（すべて docs）

| ファイル | 変更 |
| --- | --- |
| `deploy/local/README.md` | Headlamp 節を改訂。① ローカルは **token 方式が正式手順**（`kubectl -n platform-infra create token headlamp-viewer`）で OIDC は #388 と同時にのみ可能、と冒頭に明記／② 根本原因（issuer scheme must be https × `KC_HOSTNAME_URL=http`）を 1 段落／③ **罠の明示**（`99-headlamp-oidc.yaml` を置いて k3s 再起動するとクラスタ停止・退避済みの `.disabled` は無効のまま・k3d 経路は `HEADLAMP=1` が追従付与するため `HEADLAMP_OIDC_APISERVER=0` を併記）／④ `headlamp-viewer` が overlay に無いため NotFound 時の作成コマンド／⑤ realm mapper・ClusterRoleBinding は恒久化済みで inert・無害・#388 でそのまま機能する旨 |
| `docs/adr/IADR-0084_*.md` | 追記節に「#328 の処遇（wontfix・#388 へ統合）」を追加。あわせて `headlamp-viewer` SA が **overlay に含まれていない**（クラスタ側の手作りに依存）事実を訂正記載し、退避済みドロップインを戻さない注意を追加 |
| `docs/operations/local-sso-recovery-runbook.md` | Headlamp 行に、SA が overlay 外で `NotFound` なら作成が要る旨と README への導線を追記 |
| `docs/specs/20260719_issue-328_*.md` | 旧仕様書を `superseded` にし、冒頭に「ここに書かれた apiserver フラグ手順を適用してはならない」誘導を追加（直接読んだ利用者が危険手順をなぞらないようにする） |

## 非対象（コード・設定は一切変更しない）

- `scripts/k8s-local-up.sh`（`HEADLAMP_OIDC_APISERVER` の追従含む）・`deploy/local/headlamp/` の manifest・realm.json・
  Helm chart・compose・edge・ESO/Vault は**無改変**。
- apiserver への `oidc-*` 付与は**実装しない**。退避済みドロップインの再有効化も行わない。
- OIDC 用 ClusterRoleBinding（Group `oidc:platform-admin` / User `oidc:admin`）の追加は #388 の範囲。

## 受け入れ基準と検証

- [x] `deploy/local/README.md` の Headlamp 節に token 方式が正式手順として明記されている
- [x] 根本原因（https 強制 × `KC_HOSTNAME_URL=http`）が 1 段落で説明されている
- [x] ドロップインを置くとクラスタが起動不能になる罠と、退避済みファイルを戻さない指示が明記されている
- [x] k3d 経路で `HEADLAMP=1` 単独が危険であること（`HEADLAMP_OIDC_APISERVER=0` の併記）が明記されている
- [x] realm mapper / ClusterRoleBinding が inert・無害・#388 で機能する旨が明記されている
- [x] `node scripts/check-doc-links.js` が green
- [x] CI（doc-links / gitleaks / commit-messages / pr-title ほか）green

### 検証コマンド

```bash
node scripts/check-doc-links.js
```

## 残課題（本 PR の非対象・別 issue 候補）

- `headlamp-viewer` SA と ClusterRoleBinding が overlay に無いため、新規クラスタでは token 方式の手順に**手動作成が
  1 ステップ挟まる**。manifest 化すれば `HEADLAMP=1` だけで再現可能になるが、本 PR は docs のみのため見送る。
