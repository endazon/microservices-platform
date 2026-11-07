---
title: 作業仕様書 — クラウドの外向きの通信を中継へ集め、Keycloak の一時管理者を要る間だけ置き、ESO の名前空間と API の監査を足し、サーバ証明書の期限を警報にする
type: spec
status: in-progress
related_ids: [NFR-18, NFR-16, NFR-21, NFR-05, IADR-0703, IADR-0674, IADR-0675, IADR-0689, IADR-0693, IADR-0696, IADR-0697, IADR-0700]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - 20261107_cloud-infra-pod-hardening.md
  - 20261107_cloud-openbao-tls.md
  - 20261107_s2s-per-service-clients.md
---

# 作業仕様書 — クラウドの外向きの中継・一時管理者・ESO の名前空間・API の監査・証明書の期限

> 着手 2026-11-07（develop `cc8f316f1`）。判断の記録は IADR-0703。稼働中のクラスタ・ノード・外部のサービスには何もしない。秘密の値は扱わない
> （手元の試験の値・証明書はその場で作り、表示せず、試験の後に消す）。

## 起点

- 要求 **NFR-18**（統制）・**NFR-16**（通信の暗号化）・**NFR-21**（運用の監視）・**NFR-05**（秘匿は OpenBao）。
- 所有者の裁定（2026-11-07）: 稼働の前に low もすべて直す。
- 統括の指示: 外向きの 443 の宛先は段階的に入れ、宛先の値が設定に無いものは利用者が埋める形にする。API サーバーの監査は k3s の起動の引数で、手順・スクリプトを直して
  稼働中のノードに触らない。証明書の期限は OpenBao の CA の作り（IADR-0696）に揃える。

## 現状（develop `cc8f316f1` で読んだ）

| 項目 | 現状（ファイルと行） |
| --- | --- |
| 一時管理者 | `deploy/cloud/infra/keycloak.yaml:37-40` が `KC_BOOTSTRAP_ADMIN_USERNAME`・`_PASSWORD` を常に持つ。master realm の総当たりの検知は宣言に無い（既定の無効） |
| 外の 443 | `infra/networkpolicy.yaml:211-217`（LLM ゲートウェイ）・`backup/networkpolicy.yaml:29-35`・`observability/networkpolicy.yaml:31-36`（Alertmanager）が ipBlock `0.0.0.0/0`（私設を除く）の 443。`vault/networkpolicy.yaml:46-48`（OpenBao）はポートだけ |
| ESO の名前空間 | `deploy/cloud/README.md:179-180` の `--create-namespace`。PSA のラベル・NetworkPolicy なし |
| API の監査 | README §1-4 の `INSTALL_K3S_EXEC` に監査の指定なし |
| 証明書の期限 | README §6「期限の警報は置いていない」。IADR-0693 残余 1・IADR-0696 残余 2 |
| 付随の発見 | `vault/external-secrets-values.yaml` は chart 2.8.0 で描画が止まる（`processPushSecret: false` に `processClusterPushSecret: false` の併記が要る。`helm template` の実測） |

## 母集合（規則 9: 誤りの側の字面で走査した）

- 外の 443 を持つ NetworkPolicy: `git grep -n '0.0.0.0/0' -- deploy/cloud deploy/cloud-transition` → 上の 3 つ（`infra/postgres.yaml` の `hostssl … 0.0.0.0/0` は pg_hba で別物）。
  ポートだけの 443 は `vault/networkpolicy.yaml`（OpenBao。API サーバー。受容のまま）。chart の values-cloud は NetworkPolicy を描かない。
- 外の宛先（設定から集めた）: LLM の提供元 = `LlmGateway/appsettings.json` の Voyage の BaseUrl（`api.voyageai.com`）と Anthropic の SDK の既定（`api.anthropic.com`。
  `AnthropicClientWiringTests.cs:67`）。Discord = `observability/alertmanager.yaml` の render.sh の形（`discord.com`・`discordapp.com`）。
  バックアップの保管先 = ConfigMap `platform-backup-s3-target` の `endpoint`（オーナーが作る。手順書の例 `fsn1.your-objectstorage.com`）。
  heartbeat = Secret `alertmanager-heartbeat` の URL の中（設定に無い → オーナーが埋める表）。
- 一時管理者を使うもの: `git grep -n 'keycloak-admin\|KC_BOOTSTRAP' -- deploy/cloud docs/operations/cloud-*` → `keycloak-clients/job.yaml`（管理の API）と、その手順書
  `cloud-s2s-per-service-clients-runbook.md` の段 2。ほかは値の置き場（ESO・seed・policy）だけ。
- 証明書の消費側: `vault/lib.sh` の `OB_PKI_ROLES`（postgres・rabbitmq・keycloak・llmgateway-service）と ESO の `tls-*.yaml` の common_name。OpenBao の受け口は `tls-cert.sh`。
- 「警報は置いていない」の字面: `git grep -n '警報は置いていない\|期限の警報'` → README §6・`cloud-keycloak-tls-runbook.md` §4・`cloud-openbao-tls-runbook.md` §4・IADR-0693 残余 1・IADR-0696 残余 2。
- 規則 10（この変更で新たに誤りになる自分の記述）: README §3（`--create-namespace`・`keycloak-clients.sh` の順）・§4（管理者は初回の後は使わない）・§6、
  `networkpolicy.yaml` の冒頭の注記（LLM ゲートウェイの外の 443）、`backup/`・`observability/` の注記、`external-secrets-values.yaml` の注記、IADR-0674 の「外の 443 を許す」、
  `cloud-ops.test.js` の T-2402-38（443 を期待）、`deploy-cloud.test.js` の Keycloak・MSP の入りの一覧。どれも直した。

## 方式

IADR-0703 の決定 1〜5 のとおり。

## 受け入れ基準と試験（`scripts/deploy-cloud-egress-audit.test.js` ほか）

| # | 基準 | 試験 | 陰性対照 |
| --- | --- | --- | --- |
| K1 | Keycloak の Deployment は `KC_BOOTSTRAP_ADMIN_*` を持たない | K1 | env を足すと赤 |
| K2 | 開け閉めの Job・入口（Secret への参照だけ・open/close/check・`--live` 必須・値を扱わない・close は retire → 外す → check）。手順は総当たりの検知を platform realm と同じ値で有効にし、一時管理者を消して資格が通らないことを確かめる | K2・`master-admin.test.js` | 開いていない retire は書かない・閉じ忘れは 3 |
| E1 | ipBlock で宛先を名指す出は中継の Pod だけ（基盤・chart・vault・ESO の描画）。宛先を問わない出（ポートだけ）は OpenBao と ESO の API サーバー向けの 2 つだけ（中身は絞らない。IADR-0703 残余 5） | E1 | ポートだけの出を足すと赤。バックアップへ直の 443 を足すと赤 |
| E2 | 3 つの Pod は中継の 3128 へ出て中継を使う設定を持つ（NO_PROXY にクラスタの中） | E2・`cloud-ops.test.js` T-2402-38 | proxy_url・HTTPS_PROXY を外すと赤 |
| E3 | 最終の段は enforce。移す段は report で直の 443 を戻し、ほかは最終の段と同じ | E3 | 移す段を enforce の判定に当てると赤 |
| E4 | 許可表は設定に在る宛先を全部持つ。中継の入りは 3 つの呼び手と Prometheus だけ | E4 | 提供元を外す・送り元を足すと赤 |
| E5 | 中継は表の名前の 443 だけを通し、私設の範囲へ回り込ませない | `proxy.test.js` | 表を空にすると enforce で通さない |
| N1 | ESO の名前空間は restricted・既定で拒否・出と入りの限定 | N1 | baseline・外の出を足すと赤 |
| N2 | 手順書は名前空間を helm に作らせず先に当てる。chart 2.8.0 の Pod は restricted を満たす | N2（固定の tgz を描く） | 権限の昇格を足すと赤 |
| N3 | 監査の方針は本文を残さず Secret 等を必ず残し、記録は暗号化した領域。手順書が導入の前に置く | N3 | RequestResponse・置き場・順を崩すと赤 |
| C1 | 測りの宛先は PKI の role の 4 つで名前は ESO と同じ。受け口の入りは測りの Pod から TLS の口だけ | C1・`deploy-cloud.test.js` KT1〜KT3・AC6 | 宛先を外す・名前を変える・送り元を広げると赤 |
| C2 | 期限・検証・不在・CA・OpenBao の受け口の警報。閾値は作り直しの周期と期限の間。OpenBao は CA − 365 日 | C2（promtool の単体試験） | 日数・周期を変えると赤。残り 200 日で鳴ると期待すると promtool が落ちる |
| C3 | 測りは TLS・Postgres の SSLRequest の後に、検証を切らずに握手する。正しい証明書は notAfter を読み、期限切れは CERT_HAS_EXPIRED（残り 0）、別の CA・名前違いは検証の失敗として出す（検証を通らない証明書の notAfter は出さない） | `probe.test.js` C1〜C3・C5 | SSLRequest なしでは読めない。出した CA を信頼すると通る・CA を渡さないと正しい証明書も通らない。期限内の証明書は期限切れにならない |

試験を外したとき赤になることは、修正前の宣言（develop の `keycloak.yaml`・`backup/networkpolicy.yaml`・`external-secrets-values.yaml`）へ戻して確かめた
（K1・E1〜E3・N2 が赤）。

## 利用者（オーナー）の作業

- 稼働中の構成: `docs/operations/cloud-egress-proxy-runbook.md`（移す段 → 最終の段）、`cloud-keycloak-admin-runbook.md` §3（残っている一時管理者を消す）、
  `cloud-eso-namespace-runbook.md`（名前空間のラベルと到達の制限）、`cloud-apiserver-audit-log-runbook.md`（稼働中のノードへ監査を足す）。
- heartbeat の宛先の名前を ConfigMap `egress-proxy-extra-hosts` に書く（設定に無い宛先）。
