---
title: 作業仕様書 — 秘匿管理を HashiCorp Vault から OpenBao へ差し替える（#1840・planning#750 の裁定）
type: spec
status: done
related_ids:
  - NFR-18
  - NFR-21
  - SC-22
  - ADR-0132
  - ADR-0124
  - ADR-0107
  - ADR-0112
  - ADR-0023
  - IADR-0525
  - IADR-0457
  - IADR-0471
  - IADR-0486
  - IADR-0514
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0132_secret-management-openbao.md 決定 1〜5・着手可否の注記・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 2（audit の 2 つの記録）
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 基準 A〜C
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md 決定 1（基準 D）
related_specs:
  - 20261008_1787_infra-audit-digest-pin
  - 20260928_issue-1683_vault-audit-to-observability
issue: "#1840"
---

# 作業仕様書 — 秘匿管理を HashiCorp Vault から OpenBao へ差し替える（#1840）

## 目的と射程

- **目的**: 計画 ADR-0132 決定 1（製品を OpenBao とする）を実装し、決定 4 の受入条件 1〜6 を実イメージで確かめて記録する。
- **射程**: 経路 B（`deploy/local/`）の秘匿管理の配備（`-dev` と永続化の 2 構成）・Pod 内ラッパー・起動器・バックアップの注記・
  稼働中の旧 Vault からの移行の手順・運用／セキュリティ仕様書。**本番像の chart（`deploy/helm/`）に秘匿管理の配備は無い**ので射程外
  （計画の実測 5。`services.*.vault.address` の既定は空のまま）。
- **射程外**: 型名・設定キー・環境変数・k8s のリソース名の改名（決定 2 が求めない。下の「名前を変えない」）。計画文書の「Vault」の書き換え。
- 基点: MSP `origin/develop` `842b970f`（`git rev-parse --is-shallow-repository` = `false`）。

## 計画の読み取り（隣接クローン・`git show origin/main:`）

- ADR-0132（Accepted・2026-10-09）: 決定 1 製品は OpenBao／決定 2「Vault」は Vault API 互換の製品と読む。実装の型名・設定キー・環境変数名の
  改名は求めない／決定 3 版は実装の IADR が選ぶ／決定 4 受入条件 1〜6／決定 5 3 点セット（現在の実現手段「無い」）。
- **着手可否**: 「実装は決定 1 の製品で直ちに着手してよい」。覆る条件は受入条件 1〜3 のいずれかが成り立たないこと → 下の実測で**全部通った**。
  **作業を止めるほどの曖昧さは無い**（audit device の作り方は変わるが、ADR-0124 決定 2 の「2 つの記録を同じ形で」は満たせる）。

## 母集合の引き方（規則 9・10・6）

誤りの側の文字列で全追跡ファイルを走査した（`.ai-context/specs/`・`.ai-context/superpowers/` は凍結の記録として除外。
`.ai-context/adr/` の既存 IADR も凍結の本文として書き換えない。`CHANGELOG.md` は生成物）。

| 走査の語 | 当たり | 扱い |
| --- | --- | --- |
| `hashicorp/vault` | `deploy/local/vault/vault-dev.yaml`（唯一のイメージ参照）・IADR-0433（凍結） | イメージを差し替え。IADR は触らない |
| `storage "file"`・`file ストレージ`・`file storage`・`disable_mlock` | `vault-persistence/{local.hcl,kustomization.yaml,pvc.yaml,deployment-patch.yaml,vault-entrypoint.sh}`・`platform-backup/vault/{cronjob,kustomization}.yaml`・`deploy/local/README.md`（2）・`deploy/local/vault/{README,eso/README,oidc/README}.md`・`docs/operations/{operations,local-sso-recovery-runbook,paired-secret-rotation-runbook,platform-infra-backup-runbook,secret-item-console-injection-runbook,secret-rotation-runbook}.md`・`docs/security/security.md`・`scripts/k8s-local-up.sh`（2）・`scripts/k8s-local-up.test.js`（注記 1）・IADR-0369 / 0457 / 0471（凍結）・CHANGELOG | raft へ改める（生きた文書・配備・試験）。凍結と生成物は触らない |
| `audit enable`（API での audit device の作成） | `vault-entrypoint.sh`（2）・`vault-entrypoint.test.sh`・`scripts/scripts.repo.test.js`（#1683 の試験） | 設定での宣言へ改め、試験を書き直す（OpenBao は API での作成を拒む） |
| `VAULT_DEV_ROOT_TOKEN_ID`・`VAULT_ADDR`・`VAULT_TOKEN` | `vault-dev.yaml`・`vault-persistence/*`・`eso/bootstrap.sh`・`oidc/{bootstrap.sh,README.md}`・`wikijs-setup/bootstrap.sh`・手順書 8 本（`kubectl exec … VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"`） | **変えない**（CLI が `VAULT_*` を読む。実測）。サーバの `-dev` 用に `BAO_DEV_ROOT_TOKEN_ID` を足す |
| `VAULT=1`・`deploy/vault`・Service `vault`・`app: vault`・Secret `vault-dev-token` / `vault-oidc`・`vault-data` | 起動器・試験・helm の NetworkPolicy（`app: vault`）・edge の 2 経路（`vault.localhost` → Service `vault`）・バックアップ・手順書多数 | **変えない**（決定 2。改名は稼働中の PoC の PVC・Secret・DNS・OIDC の redirect を巻き込む） |
| `/ui/vault/auth/oidc/oidc/callback` | `oidc/bootstrap.sh`・realm・`check-realm-constraints.js`・`verify-tool-oidc-logins.sh`・試験 | **変えない**（OpenBao の UI も cluster 名 `vault` の経路を使う。実イメージの `vault.js` の router で確認） |
| ESO（`provider.vault`） | `clustersecretstore.yaml`・`eso/clustersecretstore-k8s.yaml`・ExternalSecret 28 本 | **変えない**（下の受入条件 2） |
| `{job="vault-audit"}`・`tcplog/vault-audit` | security.md の抽出の条件・collector の 2 設定・`scripts.repo.test.js` | **変えない**（行の形が同じ。名前を変えると Loki の保持中の行と抽出の条件が割れる） |
| 「HashiCorp Vault」（製品名としての言及） | `scripts/check-backend-libraries.js` の試験名・`docs/tech/system-architecture.md`・`docs/how-to/plan-id-range-history-annex.md`（計画 ADR の要約。正しい） | 試験名と構成の行を改める |
| 点検の表（基準 A〜D） | `docs/operations/operations.md`「インフラ製品の点検」 | 差し替えの記録の節を足し、初回の表は書き換えない（#1841 と同じ形） |
| PKI（`Vault PKI`） | `deploy/local/edge/tls/*.yaml`・`edge-istio/tls/*.yaml`（注記） | 変えない（受入条件 4 で成り立つ） |
| Grafana・アラート・ダッシュボード | `vault` を含む規則・パネルは無い（`deploy/grafana`・`deploy/prometheus` を走査） | 変更なし |
| `check-stack-ready` の門 | Vault を見る門は無い（走査） | 変更なし |
| argocd の overlay | `deploy/local/argocd` に秘匿管理の配備は無い（README の言及のみ） | 変更なし |

**規則 10（この変更で新たに誤りになる自分の記述）**: 起動器の「Vault を file ストレージ＋PVC で永続化」の echo、`deploy-patch` の
「file ストレージ＋ラッパー起動」、wrapper の「audit device を有効にする」、試験の `ensure_audit_socket &` の正規表現、
`scripts.repo.test.js` の「audit enable が 2 つ」を引き直した（いずれも改めた）。

## 実測（2026-10-09・手元の Docker。クラスタは使っていない）

イメージ: `openbao/openbao:2.7.1@sha256:6d2b93856e3fcf7b18ad855a0b51eaba474dc8b79cf554379ea32034797d2acf`（index。匿名で 2 回解決して一致）。
旧: `hashicorp/vault:1.16@sha256:c5e04689…`（1.16.3）。

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | `unknown storage type file`。OpenBao 2.7.1 は file ストレージを持たない | 現行の `local.hcl` で起動 |
| 2 | API での audit device の作成は 400（`cannot enable audit device via API; use declarative, config-based audit device management instead`）。設定の鍵 `unsafe_allow_api_audit_creation` で戻せる | 現行のラッパーで起動・バイナリの文字列 |
| 3 | 設定で宣言した socket device が試験の 1 行を書けないと **init が 400 で失敗し、鍵を返さないまま初期化済みになる**（`operator init -status` = 初期化済み） | collector 無しで init |
| 4 | 一度作られた socket device は、collector が居なくても再起動の unseal を妨げない。SIGHUP の読み直しで宣言を足せる・外せる。足せなかった読み直しでもサーバは unseal のまま動く | 宣言の追加・削除と SIGHUP |
| 5 | `-dev` の root トークンは `BAO_DEV_ROOT_TOKEN_ID` だけで決まる（`VAULT_DEV_ROOT_TOKEN_ID` だけだと乱数）。イメージの `docker-entrypoint.sh` が `-dev-root-token-id="$BAO_DEV_ROOT_TOKEN_ID"` を渡す | 両方を与えて比較 |
| 6 | CLI は `VAULT_ADDR` / `VAULT_TOKEN` を読む。`BAO_ADDR` が在ればそちらが勝つ。`/usr/bin/vault` は `bao` へのリンク。実行ユーザーは uid 100 / gid 1000（旧 Vault と同じ） | イメージの中で比較 |
| 7 | 旧 Vault 1.16.3 の file ストレージを `vault operator migrate` で raft へ写すと、OpenBao 2.7.1 が同じ unseal 鍵で開ける。KV の値と版の履歴・policy・auth mount・固定の root トークンが残る | 旧イメージで種を作り、写して OpenBao で開いた |
| 8 | 旧 Vault の API で作った audit device が残ったまま写すと、OpenBao は宣言と衝突して unseal 後の準備に失敗する（`was already created by API`）。写す前に外せば通る | 同上 |
| 9 | 受入条件 1〜4 は下の「受け入れ基準」の通り | — |
| 10 | dev サーバを 130 秒観測し、loopback 以外の接続は 0 件 | `/proc/net/{tcp,tcp6,udp,udp6}` を 2 秒ごとに読んだ |

## 決定（詳細は IADR-0525）

1. **製品・版**: OpenBao 2.7.1（最新のタグ）を digest で固定する。
2. **名前を変えない**: k8s のリソース名・Secret・opt-in のフラグ・アプリの設定キー・Loki のストリーム名・CLI の `vault` と `VAULT_*`。
   サーバの `-dev` 用に `BAO_DEV_ROOT_TOKEN_ID` だけを足す。Pod に `BAO_ADDR` / `BAO_TOKEN` を置かない。
3. **ストレージは raft（単一ノード）**。パスは `/vault/data/raft`（旧 file ストレージと分ける）。
4. **audit device は設定で宣言する**。標準出力は `local.hcl`、socket はラッパーが collector に届いてから emptyDir の宣言を足して SIGHUP。
   `unsafe_allow_api_audit_creation` は使わない。
5. **移行の門を 2 か所に置く**: ラッパー（旧 file ストレージが在って raft が無ければ起動しない）と起動器（稼働中が旧 Vault で PVC が在れば
   入れ替えの前に止める）。移行は手順書（旧イメージの使い捨ての Pod で `operator migrate`）。

## 受け入れ基準（ADR-0132 決定 4）

- [x] **1** KV v2 の読み書き・kubernetes auth・OIDC auth・dev モードが実イメージで通る。BFF の `VaultKvClient`（既存の部品）を実イメージへ
  向けて `patch`・`cas=0` の作成・metadata・policy の外の拒否を確かめ、datasource-service の `VaultConnectorSecretResolver` が BFF の作った
  群の KV を読めること・無い KV が `NotFound`・別の SA が読めないことを確かめた（使い捨ての試験を一時的に置いて走らせ、コミットしない）。
  ESO の種まき `bootstrap.sh` と OIDC の種まき `oidc/bootstrap.sh` は実イメージに対してそのまま 2 回走り通った。
- [x] **2** ESO の Vault プロバイダのクライアント（`hashicorp/vault/api` v1.22.0・`api/auth/kubernetes` v0.10.0）で、kubernetes 認証のログイン・
  `lookup-self`・KV v2 の読み取り・`eso-read` の外の拒否・token 認証の store が通る。ESO のコントローラそのものは未確認（残余リスク）。
- [x] **3** 2 つの audit device（標準出力・collector への socket）が同じ形で残る（作り方は宣言へ変わる）。行のキーと HMAC は同じ。平文の値は 0 件。
- [x] **4** PKI: root の生成・role・`issue`・CSR の `sign` が通る → ADR-0023 の選択肢は減らない（環流は不要）。
- [x] **5** 環境変数: 実測 5・6。名前は変えず、`BAO_DEV_ROOT_TOKEN_ID` を足す。
- [x] **6** 基準 B〜D: digest 固定・既定の外部通信なし（環流は不要）・管理用の口は token 必須（dev の root トークンは既知の dev 値で opt-in に限る）。
- [x] 実装の IADR（IADR-0525）。受入条件 1〜3 が通ったので、計画へ着手可否の充足の環流が要る（フォローアップ 2。本 PR では起票しない）。
- [x] 試験: `vault-entrypoint.test.sh`（移行の門・宣言の追加と消去・SIGHUP）、`k8s-local-up.test.js`（移行の門と陰性対照）、
  `scripts.repo.test.js`（#1683 の宣言の検査を宣言の形へ）。

## 計画への環流の要否

- **要る（本 PR では起票しない）**: ADR-0132 フォローアップ 2（受入条件 1〜3 の充足と決定 5「現在の実現手段」の更新）。
- 要らない: 受入条件 4（PKI は成り立つ）、基準 C（外部通信なし）。

## 必須チェックへの影響

- 起動条件・必須 check 名は変えない（`ci.yml` の `vault-entrypoint.test.sh`・`k8s-local-up.test.js`・`scripts.repo.test.js` の既存のステップで走る）。
- IADR の欠番検査は、先行の PR（IADR-0522〜0524）が入るまで赤になり得る（採番は依頼どおり 0525）。
