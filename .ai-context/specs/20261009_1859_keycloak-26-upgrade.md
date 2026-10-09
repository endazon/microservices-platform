---
title: 作業仕様書 — Keycloak を 24.0 から 26.7.4 へ上げ（CVE-2024-8883）、ループバックのポート必須を外すかの判断材料を門 M9 で測る（#1859）
type: spec
status: done
related_ids: [FR-16, SC-12, NFR-09, NFR-18, ADR-0134, ADR-0004, ADR-0107, ADR-0112, ADR-0086, ADR-0026, IADR-0516, IADR-0514, IADR-0243, IADR-0369, IADR-0518, IADR-0079, IADR-0082]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 1（リダイレクト URI の完全一致・ループバック）・フォローアップ 3
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 2〜5（点検の基準・digest 固定）
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md フォローアップ 4（Keycloak の版更新時の確認。計画側の作業）
issue: "#1859"
---

# 作業仕様書 — Keycloak 24.0 → 26.7.4（#1859）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0524** に置く。
> 基点は MSP `origin/develop` `842b970f`。計画は隣接クローン `project-planning` の `origin/main` を読んだ（読み取り専用）。
> 🔴 **稼働中のクラスタには何も実行しない。** 実測は手元の docker で Keycloak 24.0（`24.0.5`）と 26.7.4 の公式イメージを起こして行った（下の「実測」）。
> 🔴 **ループバックのポート必須（IADR-0516 の #1844・PR #1854 追記）は本 PR では外さない。** 外すかは製品判断であり、本 PR は版の更新と、判断に要る証拠（門 M9 の新しい対）だけを持つ。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: **#1859**（#1844 / PR #1854 の監査が残した暫定の統制を外す条件）。利用者裁定 2026-10-09「着手してよい」。
- 計画: **ADR-0134** 決定 1（有人のリダイレクト URI は完全一致・ループバックは `http://127.0.0.1` / `http://[::1]`）・フォローアップ 3（Keycloak の照合の確かめ）。ループバックのポートの扱いは計画が実装へ委ねた細目（IADR-0516 の #1844 追記）。
- 計画: **ADR-0107** 決定 2〜5（インフラ製品の基準・digest 固定）・**ADR-0112**（管理用の口）。版の更新は点検の記録へ載せる。
- 計画: **ADR-0086** フォローアップ 4（「決定 2 の着手可否の注記の 2 条件を、Keycloak の版更新時に確認する」）は**計画側の作業**である。本件はその契機に当たるので、PR 本文で計画への環流が要ると明記する（本 PR では起票しない）。
- 前提 IADR: [IADR-0516](../adr/IADR-0516_sc12-keycloak-service-account-provisioning.md)（#1844 追記・PR #1854 追記）・[IADR-0514](../adr/IADR-0514_infra-image-digest-pinning-and-checker.md)（digest 固定）・[IADR-0243](../adr/IADR-0243_keycloak-edge-issuer-migration.md)（issuer の単一情報源）・[IADR-0369](../adr/IADR-0369_persist-by-default-and-realm-reconcile-job.md)（永続化が既定・realm の後追い）・[IADR-0518](../adr/IADR-0518_realm-import-secret-with-env-client-secrets.md)（取り込み元の Secret）。

## 受け入れ基準（issue #1859）

| # | 基準 | 写像 |
| --- | --- | --- |
| 1 | Keycloak を 25.0.6 以降（できれば 26 系）へ上げる | 26.7.4（2026-09-16 の版。本日 2026-10-09 で 2 週間を超える最新の 26 系。26.7.5 は 2026-09-30 で 2 週間に満たない）。index の digest で固定（`check-image-digests`） |
| 2 | realm の import・管理 API の互換（部分 PUT）・テーマ・`check-stack-ready` の G9 を確かめる | 下の「実測」1〜12。G9 は realm の後追い（`reconcile-realm.js --check`）を 26.7.4 に当てて `drift=0` |
| 3 | M9 の否定ケース（`:<port>@evil.example` が 400）が緑のまま、ポートなしのループバック登録を許すかを決め、IADR-0516 に追記する | 門 M9 に「Keycloak に直接作ったポートなしのループバック」の対を足す（陽性対照＝任意のポートで進む／否定＝横取りの形が 400）。**判断は製品へ**（IADR-0516 追記に証拠と推奨） |
| 4 | digest の固定と、点検の表（operations.md）を更新する | `keycloak.yaml`・`docker-compose.yml` の 2 参照。operations.md §点検の記録に追補 |
| 5 | 新しい版で M9 が integration-stack で緑・`check-image-digests` と `check-stack-ready` が緑 | integration-stack はオーケストレーターが dispatch する（本 PR の検証は静的検査・自己試験・手元の docker の実測まで） |

## 実測（手元の docker。24.0 は配備と同じ digest、26.7.4 は採る digest）

| # | 事項 | 24.0.5 | 26.7.4 | 本 PR の対応 |
| --- | --- | --- | --- | --- |
| 1 | `KC_HOSTNAME_URL`（hostname v1） | issuer をエッジに固定 | **黙って無視**（`WARNING: Hostname v1 options [hostname-url] are still in use`）。issuer が要求の host になる（`http://172.17.0.2:8080/realms/master`） | `KC_HOSTNAME`（完全な URL）へ。検査器・試験の読み手も改める |
| 2 | バックチャネルの URL（in-cluster から discovery を引いたとき） | `token_endpoint`・`jwks_uri` は要求の host（in-cluster） | `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true` で同じ（`issuer`・`authorization_endpoint`・`end_session_endpoint` はエッジ、`token`・`jwks`・`userinfo`・`introspection` は要求の host） | `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`（.NET の `Auth:MetadataAddress` が in-cluster の jwks を引く前提を保つ。IADR-0243 決定 2） |
| 3 | ヘルス | `:8080/health/ready` 200 | `:8080/health/ready` **404**、`:9000/health/ready` 200（管理用のポート） | readinessProbe と compose の healthcheck を 9000 へ。Service には出さない |
| 4 | realm の取り込み（`DirImportProvider`） | ファイル名を問わない | **`File name / realm name mismatch. microservices-platform-realm.json, contains realm platform. File name should be platform-realm.json` で起動しない** | 取り込み元のファイル名を `<realm>-realm.json` へ（compose のマウント先・Secret `keycloak-realm-import` のキー）。AST の `ai-stock-trading-realm.json` は realm 名と一致済み |
| 5 | dev-file（H2）の資格 | 既定 `sa` / `password`（`DatabasePropertyMappers.resolveUsername/resolvePassword`） | 既定の差し替えが無くなり、24 が作った H2 を開けない（`Wrong user name or password [28000-240]`） | k8s の Keycloak に `KC_DB_USERNAME=sa`・`KC_DB_PASSWORD=password` を明示（24 が作った PVC を開くため。H2 のファイルの外へ出ない値で秘密ではない）。compose は Postgres なので対象外 |
| 6 | 24 → 26 の更新（PVC の H2） | — | 5 の資格を与えると Liquibase が移行し（`Updating database`）、realm・実行時に足した利用者と属性が残る。**一方向**（H2 2.4.240 の形式・26 のスキーマ） | 手順書に PVC の退避を必須の段として書く（戻すのは退避からだけ） |
| 7 | 利用者のアクセストークンの `sub` | 全クライアントで在る | **realm の宣言から取り込むと全クライアントで落ちる**（25 以降、`sub` は組み込みの `basic` スコープの写像。宣言が `clientScopes` を明示するので組み込みスコープが作られない）。ID トークンには在る | realm の宣言に `basic` スコープ（`oidc-sub-mapper`・`auth_time`）を足し、人の流れ（認可コード）を開く 6 クライアント（`wiki-js`・`bff`・`headlamp`・`grafana`・`argocd`・`vault`）の既定スコープへ入れる。サービスアカウントだけのクライアントは足さない（client_credentials のトークンは `basic` なしでも `sub` を持つ。実測）。宣言から作った 26 で、6 クライアントの利用者のトークンと全サービスアカウントのトークンのクレームの集合が 24 と一致（下の 8 を除く）。有人の MCP クライアントのテンプレートにも `basic` を足す |
| 8 | サービスアカウントのトークンの `client_id`・`clientHost`・`clientAddress` | 在る | 宣言から取り込むと無い（26 の `service_account` スコープへ移った）。`azp` は在る | 変えない。読み手（`McpSubjectResolver`・`MachinePrincipal`・`SyntheticTraffic`）は `azp` を先に読む。AST のコードは 3 つとも読まない（`git grep`） |
| 9 | 24 → 26 の移行で realm に起きること | — | 移行が `basic` を全クライアントへ足し、requiredAction の優先度を変える。宣言（7 の後）との差は 22 件（requiredAction 2・`basic` の定義 1・サービスアカウントだけの 19 クライアントの `basic` の余剰）で、後追い（apply）で 0 へ収束し、トークンのクレームも宣言から作った場合と一致（`synthetic-monitor` のサービスアカウントのトークンだけ 24 と同じ `client_id` 等を残す） | G9 は後追いの後に走るので緑のまま |
| 10 | クッキー（エッジの TLS 終端の後ろ） | `AUTH_SESSION_ID` は `Secure; SameSite=None`（http でも） | 非セキュアの文脈では `SameSite=Lax`・`Secure` なし（`Non-secure context detected`）。`KC_PROXY_HEADERS=xforwarded` と `X-Forwarded-Proto: https` で 24 と同じ `Secure; SameSite=None` | k8s に `KC_PROXY_HEADERS=xforwarded`（エッジ〔Traefik / Istio〕が付ける。床の器はヘッダをそのまま中継する）。hostname を固定しているので `X-Forwarded-Host` は issuer・認可の URL を変えない（バックチャネルの URL は要求者自身への応答だけが変わる。実測） |
| 11 | クライアントの部分 `PUT`（`{"enabled":false}` だけ） | SA の利用者を消して作り直す（IADR-0516 の監査 🔴1 の再現） | SA の利用者は同じ ID で残る | 書き込み口は現在値を同送し続ける（版に依存させない）。注記だけ改める |
| 12 | ポートなしのループバック（`http://127.0.0.1/cb`・`http://[::1]/cb` を登録） | `:49152@evil.example/cb`・`:1@evil.example/cb`・`:@evil.example/cb`・`:49152:1@evil.example/cb` が**ログイン画面へ進む**（横取りを許す）。`[::1]` は別のポートを拒む | 上の 4 形と `%40`・`.evil.example` の形がすべて **400**。`127.0.0.1` と `[::1]` は**任意のポートで進む**（RFC 8252 §7.3）。別の path・`localhost` は 400 | 門 M9 に対を足す（下の「設計」4）。ポート必須は外さない |
| 13 | その他（変化なし） | — | サービスアカウントの照会 `users?username=service-account-<c>&exact=true`・グループ検索の木の形・master の `accessTokenLifespan`（60）・匿名 DCR の Trusted Hosts と 403・ログイン画面の `platform` テーマ（CSS 200・`parseLoginForm` が `username`・`password` を読める）・`KEYCLOAK_ADMIN`（非推奨の警告だけで有効） | 変えない（`KEYCLOAK_ADMIN` の改名は残余） |
| 14 | アカウントコンソールの `platform` テーマ | 親 `keycloak` が無く組み込みへ落ちる（エラーログ） | 同じ | 既存の不具合（本件の回帰ではない）。残余に記録 |
| 15 | client secret の再生成（#1845 の口）と管理イベント | 再生成の管理イベントの表現に新しい値が残る（1 件）。`GET client-secret` は管理イベントを出さない。再生成の直後に旧 secret は 401 | 表現に値は**残らない**（0 件）。他は同じ | 注記（FR-16 通信仕様書・SC-12 画面仕様書・試験仕様書・書き込み口・門の観測）を改める。IADR-0516 に追記 |

## 設計（正は IADR-0524）

1. **版**: `quay.io/keycloak/keycloak:26.7.4@sha256:82a77884…`（index。2026-09-16）。`keycloak.yaml`・`docker-compose.yml` の 2 参照を同じ値に揃える。
2. **起動の設定**（k8s）: `KC_HOSTNAME=https://keycloak.localhost`・`KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`・`KC_PROXY_HEADERS=xforwarded`・`KC_DB_USERNAME=sa`・`KC_DB_PASSWORD=password`・`KC_HEALTH_ENABLED=true`。readinessProbe は `9000`。管理用のポートは containerPort に名前つきで宣言し、Service には出さない。（compose）: `KC_HOSTNAME=http://localhost:8080`・`KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`・healthcheck は `9000`・取り込みのマウント先を `platform-realm.json`。
3. **realm の宣言**: `basic` スコープを足し、人の流れ（認可コード）を開く 6 クライアント（`wiki-js`・`bff`・`headlamp`・`grafana`・`argocd`・`vault`）の既定スコープの先頭へ入れる（実測 7）。取り込み元の Secret のキーを `platform-realm.json` へ（`k8s-local-up.sh`。宣言の ConfigMap `keycloak-realms` のキーは変えない —— 読み手は後追いの Job と申請の門で、Keycloak の取り込みではない）。
4. **門 M9 の対（#1859）**: master の管理者で、SC-12 を通らずに**ポートなしのループバック**（`http://127.0.0.1/cb`・`http://[::1]/cb`）の公開クライアント（PKCE S256・認可コードだけ）を作る（SC-12 は 400 で拒むので、入口を通しては作れない）。
   - 陽性対照: 登録したそのまま・任意のポート（`:49152`）で**ログイン画面へ進む**（否定が空振りしていないこと。Keycloak がこのクライアントを全部拒んでいれば否定は何も示さない）。
   - 否定: 横取りの形（`127.0.0.1`・`[::1]` × `:<任意>@`・`:1@`・`:@`・`:<任意>:1@`）がすべて **400**。形は純関数 `portlessLoopbackHijackProbes` が作り、WHATWG の URL として宛先が `evil.example` になることを自己試験で確かめる。24 の応答（200 でログイン画面へ）は赤になる。
   - 既存の否定（SC-12 がポートなしを 400 で拒み Keycloak に何も作らない・ポートつきの横取りが 400）は**そのまま残す**。
   - 有人の例示のアクセストークンに `sub` が在ることを足す（実測 7 の回帰の固定）。有人のクライアントの表現の判定に既定スコープ `basic` を足す。
5. **書き込み口**（`KeycloakServiceAccountProvisioner`）: 有人のテンプレートの既定スコープを `["basic", "profile"]` に。読み戻しで `basic` も確かめる（欠ければ消して 502）。無人は変えない（client_credentials のトークンは `basic` なしでも `sub` を持つ。実測）。
6. **検査器の読み手**: `check-stack-ready.js`（G4 の issuer・G6 のエッジ host）と `check-password-reset-mail.js` は `KC_HOSTNAME` を読む。
7. **静的な固定**（`k8s-local-up.test.js`）: `keycloak.yaml` に `KC_HOSTNAME_URL` が無いこと（26 は黙って無視して issuer が揺れる）・`KC_HOSTNAME` がエッジの URL・バックチャネル・プロキシのヘッダ・readiness が 9000・取り込み元の Secret の**全キーが `<中身の realm 名>-realm.json`**（26 の起動条件）。`scripts.repo.test.js` 等の既存の試験はキー名の変更に追随する。
8. **ポート必須は外さない**。IADR-0516 に追記し、証拠（実測 12）と推奨を書く。外す PR は別（製品の判断の後）。

## 母集合の走査（規則 9・10）

誤りの側の文字列で全文書を走査した（`git grep`。凍結記録 `.ai-context/specs/`・`.ai-context/superpowers/`・確定済み IADR の本文と `CHANGELOG.md` は除く）。

| 走査語 | 当たり | 扱い |
| --- | --- | --- |
| `keycloak:24` / `keycloak/keycloak` | `deploy/local/infra/keycloak.yaml:40`・`deploy/docker-compose.yml:89`（`check-image-digests --list` も同じ 2 参照）。Testcontainers・CI・helm の values には無い | 2 参照を 26.7.4 の digest へ |
| `KC_HOSTNAME_URL` | 設定 2（`keycloak.yaml:58`・`docker-compose.yml:99`）。読み手: `check-stack-ready.js:41,191,1450-1562,1872-1891`・`check-password-reset-mail.js:41,69,235-237`・`k8s-local-up.test.js:3041-3052`。注記: `k8s-local-up.sh:117`・`k8s-local-up.test.js:710`・`grafana.yaml:1377`・`vault/oidc/bootstrap.sh:29`・`deploy/local/README.md:491,549,553`・`docs/operations/operations.md:161,854`・`docs/tech/20260707_wikijs-poc-record.md:63,76` | 設定と読み手は `KC_HOSTNAME` へ。注記で**現在の設定を述べるもの**は改め、経緯（「当時 `KC_HOSTNAME_URL=http://keycloak:8080` だった」）と PoC 記録は残す |
| `health/ready` × Keycloak | `keycloak.yaml:80`・`docker-compose.yml:100,124-126`・`docs/how-to/local-development.md:135` | 9000 へ。他の `/health/ready` は .NET サービスのもの（対象外） |
| `microservices-platform-realm.json`（取り込み） | `docker-compose.yml:118`（マウント先）・`k8s-local-up.sh` の取り込み元の Secret（`realm_import_args`）・`k8s-local-up.test.js:270-273,5133-5207` | 取り込みのファイル名だけ `platform-realm.json` へ。**宣言のファイル**（`deploy/keycloak/microservices-platform-realm.json`）と ConfigMap `keycloak-realms` のキーは変えない（読み手は後追いの Job・門・検査器で、ファイル名を realm 名と突き合わせない） |
| `KEYCLOAK_ADMIN` | `keycloak.yaml:46,51`・`docker-compose.yml:94-95`・`check-password-reset-mail.js:255,280-281`（Pod の env を読む）・runbook 2 本 | **変えない**（26.7.4 では警告だけで有効。改名は Pod の env を読む検査器・runbook に波及するので残余） |
| `Keycloak 24` / `24.0` / `（24.0）` | 現在の配備を述べるもの: `docs/api/FR-16_mcp-server.md:115`・`docs/screens/SC-12_mcp-client-management.md:224`・`docs/security/security.md:403`・`docs/operations/paired-secret-rotation-runbook.md:243`・`docs/how-to/local-development.md:135`・`docs/operations/operations.md:1851,1909`・`deploy/local/vault/eso/externalsecret-mcp-client-admin-oidc.yaml:12`・`deploy/mail-relay/reset-gate.yaml:30`・`RedirectUriRules.cs:13-18,48`・`mcpClientVocabulary.ts:134`・`mcpClientVocabulary.test.ts:48`・`RegisterMcpClientValidatorTests.cs:159,179`・`KeycloakServiceAccountProvisioner.cs:35,474`・`check-mcp-client-provisioning.js:41,48,105,1108`・`docker-compose.yml:122` | 改める（現在の版で言い直す。24 で実測・ソースを読んだ事実は「24 では」と残す） |
| 同上（経緯・ソースの読みの出典） | `check-realm-constraints.js`（24.0 のソースの読み 8 箇所）・`scripts/lib/keycloak-login-form.js:10`・`scripts.test.js:1477`・`KeycloakIdentityAdminClient.cs:16,203`・`KeycloakIdentityAdminClientTests.cs:783`・`owner-read-seed-scopes.json:17`・`measure-*.js`・`check-password-reset-mail.js:277,387`・`reset-gate.js:69`・`docs/operations/password-reset-relay-state-measurement-runbook.md:242-245`・`docs/tech/20260707_wikijs-poc-record.md:53`・`keycloak.yaml:70` | **残す**。「24 で読んだ／測った」という出典の記述で、事実として誤りにならない。26.7.4 で同じ振る舞いを測ったもの（SA の照会・グループ検索の木・ログイン画面の解析）は実測 13 に記録した。`reset-gate.js` は ConfigMap の中身なので注記のためだけに変えない |
| `docs/api/openapi.yaml:6021`（「認証基盤 Keycloak 24 の CVE-2024-8883 で…入れない」） | 生成物 `bff.schemas.ts:1796` に写る | **残す**。規則の由来の記述として誤りではなく、改めると orval の再生成が要る。規則を外す PR で併せて改める（IADR-0524 の残余） |

**この変更で新たに誤りになる自分の記述（規則 10）**: (a) IADR-0516 の FU3 の表・PR #1854 追記の「配備の Keycloak は 24.0」—— 凍結のため本文は改めず、IADR-0516 の日付つき追記で受ける。(b) `check-mcp-client-provisioning.js` 冒頭の M9 の説明（「Keycloak 24 の RedirectUtils は…」）—— 現在形を改め、24 の事実は「24 では」へ。(c) 運用仕様書の点検の表の keycloak の行（初回の記録）は書き換えず、追補で受ける。(d) `security.md` の「配備の認可基盤は 24.0 であり、現行の版では絞れない」は、26.7.4 では細粒度の管理権限 v2 が使える版になったので、「版は満たした・絞る作業は未着手」へ改める。(e) 取り込み元の Secret のキー名を変えると、`k8s-local-up.test.js` の取り込み元の試験（`r.realmImport['microservices-platform-realm.json']`）が誤りになる —— 追随する。

**窓（規則 11）**: 本件は時間差を扱わない（該当なし）。

## テスト

- `check-mcp-client-provisioning.js --self-test`: `portlessLoopbackHijackProbes` の形（8 形・宛先は `evil.example`・ポートつき・非ループバック・`localhost` は作らない）・24 の応答で赤・26 の応答で緑。有人の表現の判定に `basic`。有人のトークンに `sub`。
- `KeycloakServiceAccountProvisionerTests`: 有人のテンプレートの既定スコープに `basic`・読み戻しで `basic` が無ければ消して 502。
- `k8s-local-up.test.js`: 上の「設計」7。
- 既存の全試験（`scripts.test.js` 一式・McpServer.Tests）を緑のまま。
- **変異**: (a) `KC_HOSTNAME_URL` へ戻す (b) readiness を 8080 へ戻す (c) 取り込み元のキーを `microservices-platform-realm.json` へ戻す (d) realm の宣言から `basic` を外す（静的には `check-realm-constraints` が拾わないので、手元の docker の実測が根拠）(e) 有人のテンプレートから `basic` を外す (f) 門のポートなしの横取りの形の宛先をずらす —— (a)(b)(c)(e)(f) で試験が赤になることを確かめる。

## 検証

`dotnet build`（両ユニット・警告 0）・`dotnet test`（McpServer.Tests ほか Keycloak に触れる試験）・`dotnet format --verify-no-changes`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/k8s-local-up.test.js`・`check-mcp-client-provisioning --self-test`・`check-image-digests`・`check-deploy-manifests`・`check-stack-ready --self-test`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-updated --base origin/develop`・`check-commit-messages --base origin/develop`。

🔴 **IADR の番号**: 0522（PR #1860・Valkey）と 0523（#1846 の PR）が未マージのため、本 PR の 0524 は `check-adr-numbering` の欠番で赤になり得る。両 PR のマージで解消する（先にマージされた側に合わせて改番しない＝番号は予約済み）。
