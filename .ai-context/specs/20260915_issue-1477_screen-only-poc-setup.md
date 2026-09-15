---
title: "PoC の立ち上げを画面だけで行えるよう、SC-22 に moomoo 資格情報（MD5 変換）・OpenD の RSA 鍵（生成）・Discord の環境固有 ID を加え、書き込み後に即時同期と消費側の再起動を通す（#1477）"
type: spec
status: done
related_ids: [SC-22, FR-05, NFR-18, ADR-0095, IADR-0096, IADR-0103, IADR-0433, IADR-0453, IADR-0454, IADR-0456]
author: claude
created: 2026-09-15
updated: 2026-09-16
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
---

# 仕様書: PoC の立ち上げを画面だけで行う（MSP 側・#1477）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-05`（管理・権限）
- 非機能要件（NFR）: `NFR-18`（シークレット管理）
- ユースケース（UC）: なし（運用・保守要求）
- 画面（SC）: `SC-22`（秘密情報・接続設定の管理）
- 関連 ADR: `ADR-0095` 決定 1（Git に置けないもの）・決定 3（BFF が書く・ESO は読み取り専用）・決定 4（一括再投入をしない）
- 関連 IADR: `IADR-0433`（権限の形。**data の `read` なし・`update` なし・ワイルドカードなし・`items[]` だけ**）／`IADR-0453`・`IADR-0454`（画面と端点の決定）／`IADR-0096`（Vault ＋ ESO）／`IADR-0103`（env は Pod 起動時に 1 度だけ解決される）／**`IADR-0456`（本作業で起こす。事前割り当て）**
- 起票: #1477（利用者指示 2026-09-15「PoC の立ち上げをすべて画面から」）。AST 側は AST#795（同じ契約を並行実装）
- 計画への環流: planning#635（ADR-0095 決定 1 の射程・SC-22 の画面設計の追随の確認）。**オーナー指示により裁定を待たず実装する**。裁定が異なれば戻す

## 共通の契約（#1477 本文の表。**変えない**）

| Vault KV（mount `secret`） | プロパティ | 同期先 Secret（ns `ai-stock-trading`） | 画面での扱い |
| --- | --- | --- | --- |
| `ai-stock-trading/app-secrets` | 既存 15 ＋ 新設 `discord-bot-guild-id` / `discord-bot-channel-id` / `discord-bot-allowed-user-ids` / `discord-bot-user-mapping` | `ast-secrets` | 外部 API キー 7 ＋ Discord ID 4 を書ける。`*-auth-client-*` 8 は書けない |
| `ai-stock-trading/moomoo` | `login-account` / `login-pwd-md5` | `moomoo-credentials` | `login-account` はそのまま。パスワードは平文を受け、BFF が MD5（小文字 hex 32 桁）で `login-pwd-md5` に書く |
| `ai-stock-trading/moomoo-rsa` | `opend_rsa.pem` | `moomoo-rsa` | 値を入力しない。「生成」で BFF が RSA 1024 bit・PKCS#1 PEM を生成して書く |

反映: 書き込み成功後に ExternalSecret へ `force-sync` 注釈。env で読む消費側は Stakater Reloader が再起動する。
seed: `ai-stock-trading/app-secrets` を**無いときだけ**作る（`*-auth-client-*` は realm と同値、他は空文字）。`moomoo` / `moomoo-rsa` は seed しない。

［2026-09-16 追記 / #1477］契約の表に `sec-edgar-user-agent` を加える（**書ける 12 ＝外部 API キー 7 ＋ Discord ID 4 ＋ SEC EDGAR User-Agent 1**、秘密でない値＝`sensitive: false`、seed は空文字）。AST の `ast-secrets` はこのキーを optional の `secretKeyRef` で読んでおり、表から漏れていたため ESO 所有の経路では画面から入れられず SEC EDGAR だけが収集対象から外れていた（AST#796 の監査指摘・利用者指示「PoC の立ち上げをすべて画面から」）。AST 側は `dataFrom.extract` で取り込むため変更不要。

## 着手前の実測（`origin/develop` `1196f400`）

| 箇所 | 現状 |
| --- | --- |
| allowlist | `items[]` 4 KV・13 プロパティ。`ai-stock-trading/moomoo`・`moomoo-rsa` は `deferred[]` |
| `properties[]` の型 | 文字列の配列だけ。種別（変換・生成）・秘密かどうかを持てない |
| 反映 | ExternalSecret の `refreshInterval: 1h`。env で読む Pod は再起動まで旧値（IADR-0103）。BFF は ExternalSecret に触れない（テスト仕様書「対象外」） |
| k8s API への経路 | BFF の SA `bff` は Vault の k8s auth にだけ使っている。**chart に Role / RoleBinding は 1 つも無い**（`git grep "kind: Role" deploy/helm` 0 件） |
| NetworkPolicy | 本番像 `networkPolicy.enabled: true`（`allow-intra-namespace` が Egress を同 ns ＋ DNS に絞る）。ローカル `values-local.yaml` は `false` |
| `bootstrap.sh` の seed | SC-22 の 3 KV（`llm-provider-credentials` / `wikijs-sync` / `keycloak-smtp`）を **毎回 `vault kv put`（全置換）**。`up` を再実行すると**画面で入れた値が env 既定（空）で消える** |
| `ai-stock-trading/*` の seed | 無い（AST が ESO で受ける前提が立たない） |
| AST の dev 既定 | `src/ai-stock-trading/scripts/k8s-local-deploy.sh` の `AST_SECRET_KEYS`: svc `ai-stock-trading-svc` / `dev-only-service-secret`、kb `ai-stock-trading-kb-writer` / **空**、llm `ai-stock-trading-llm-caller` / **空**、owner `ai-stock-trading-owner` / `dev-only-owner-secret` |
| MSP realm | `deploy/keycloak/microservices-platform-realm.json`: svc `dev-only-service-secret`、kb-writer `ai-stock-trading-kb-writer-dev-secret-change-me`、llm-caller `ai-stock-trading-llm-caller-dev-secret-change-me`、owner `dev-only-owner-secret` |
| keycloak-smtp の消費側 | #1245 以降は **mail-relay（platform-infra）が env で読む**（realm reconcile ではない）。`k8s-local-up.sh` が ESO 供給後に rollout するだけで、以後の変更は反映されない |

🔴 **AST のスクリプトは kb / llm の secret の既定が空である。** 契約は「realm と同値」なので、seed は **MSP realm の値**を使う
（空だと KB 書き込み・LLM 呼び出しの client_credentials が 401 になる）。AST のスクリプト側の既定は本 PR の対象外で、PR 本文に書く。

## 対象範囲

- 対象（MSP 側だけ）:
  1. allowlist: Discord ID 4 件を `ast-app-secrets` へ、`ast-moomoo`（`login-account` ＋ `login-pwd-md5` の MD5 変換）・`ast-moomoo-rsa`（`opend_rsa.pem` の生成）を `deferred[]` から `items[]` へ。スキーマに**プロパティの種別**（`value` / `md5-from-password` / `generate-rsa-pkcs1`）と `sensitive` と、**項目ごとの ExternalSecret**（`externalSecret: { name, namespace }`）を足す。fail-closed を保つ
  2. BFF: 種別ごとの値の作り方（MD5・RSA 生成）、書き込み成功後の `force-sync` 注釈、応答の `syncRequested`、監査
  3. Vault policy に `ai-stock-trading/moomoo` と `moomoo-rsa` を完全一致で足す
  4. RBAC: 名前空間ごとの Role（`get` / `patch`・`resourceNames` を items[] の ExternalSecret に限る）を SA `bff` へ束縛。MSP ns は helm、platform-infra / ai-stock-trading は `deploy/local/vault/eso/` ＋ `k8s-local-up.sh`（ESO=1）
  5. Reloader: ESO=1 で Stakater Reloader（chart・image を pin、3 名前空間に限定）を入れ、MSP の消費 Deployment に注釈（llmgateway-service・wiki-service・mail-relay）
  6. `bootstrap.sh`: SC-22 の KV を**無いときだけ**作る。既に在る KV には env が**空でないときだけ**そのプロパティを部分更新する。`ai-stock-trading/app-secrets` の seed を足す
  7. SPA: 種別ごとの入力形（パスワード＋確認＋MD5 の注記／生成ボタン＋確認／秘密でない旨の平文入力）、同期を依頼できたかの表示。i18n・codegen
  8. 記録: IADR-0456・IADR-0433 と IADR-0453 への日付つき追記・索引・画面仕様書・テスト仕様書・BFF 境界の文書・openapi・Runbook・ESO README
- 対象外:
  - 🔴 **契約の表の変更**（パス・プロパティ・Secret 名・画面での扱い）
  - 🔴 **Vault の権限を広げること**（data の `read`・`update`・ワイルドカード・`list`）。**値を読み出す口も作らない**
  - AST リポジトリの変更（chart の ExternalSecret・消費側の Reloader 注釈・`k8s-local-deploy.sh`）は AST#795
  - `deferred[]` に残る OIDC / s2s 資格情報 18 件
  - 稼働クラスタ・実 Vault への操作（T-40 は手動・未実施のまま）
  - Keycloak の自動再起動（keycloak-smtp の消費側は mail-relay であり Keycloak ではない）

## 設計（決定の理由は IADR-0456）

### allowlist のスキーマ

```jsonc
{
  "item": "ast-moomoo",
  "vaultPath": "ai-stock-trading/moomoo",
  "properties": [
    "login-account",                                          // 文字列 = { kind: "value", sensitive: true }
    { "name": "login-pwd-md5", "kind": "md5-from-password" }
  ],
  "targetSecret":   { "name": "moomoo-credentials", "namespace": "ai-stock-trading" },
  "externalSecret": { "name": "moomoo-credentials", "namespace": "ai-stock-trading" }
}
```

fail-closed で拒むもの（起動しない）: 未知の `kind`／オブジェクトの未知のキー／`sensitive` が真偽値でない／**`sensitive: false` を `value` 以外に付ける**
（パスワードと生成鍵を「秘密でない」と宣言させない）／`externalSecret` の欠落・名前や名前空間の書式違反／`externalSecret` の重複。

### 値の作り方（BFF）

| 種別 | 要求の `value` | 保管する値 | 監査・ログ・応答 |
| --- | --- | --- | --- |
| `value` | 1〜8192 文字 | そのまま | 値を出さない（従来どおり） |
| `md5-from-password` | 平文 1〜8192 文字 | `MD5(UTF-8)` の小文字 hex 32 桁 | 🔴 平文もハッシュも出さない |
| `generate-rsa-pkcs1` | **空文字（または省略）**。空でなければ 400 `invalid-value` | BFF が生成した RSA 1024 bit の PKCS#1 PEM（見出しは `RSA PRIVATE KEY`） | 🔴 鍵を出さない。生成器は使い終えたら破棄する |

要求の型（`UpdateSecretItemRequest.Value` は `string`）は**変えない**。生成は `value: ""` で送る。

### 即時同期（`force-sync`）

- 書き込み（Vault）が成功し、書き込み記録を置いた**後**に、項目の `externalSecret` へ
  `PATCH /apis/external-secrets.io/v1/namespaces/{ns}/externalsecrets/{name}`（`application/merge-patch+json`・
  本文 `{"metadata":{"annotations":{"force-sync":"<unix 秒>"}}}`）を送る。認証は Pod の SA トークン（Bearer）、TLS は SA の `ca.crt`。
- 🔴 **同期の依頼が失敗しても書き込みを失敗にしない。** 応答は 200 のまま `syncRequested: false`。監査は
  `secret.item.sync`（`granted` / `failed`）を**別の行**で残す。理由は `sync-request-failed`（拒否・不達・5xx）と
  `sync-not-configured`（クラスタ外・無効化）。書き込みの監査行（`secret.item.update granted`）は変えない。
- 有効化: 構成 `ExternalSecretSync:Enabled`（helm の `services.bff.externalSecretSync.enabled`。本番像の既定 false、ローカル true）。
  API サーバの所在は `KUBERNETES_SERVICE_HOST` / `KUBERNETES_SERVICE_PORT`（上書き `ExternalSecretSync:ApiServer`）。
- RBAC: Role `bff-externalsecret-sync`（`external-secrets.io` / `externalsecrets` / `get`,`patch` / `resourceNames`）＋ RoleBinding（SA `microservices-platform/bff`）。
  MSP ns は helm（`llm-provider-credentials`・`wikijs-sync`）、platform-infra（`keycloak-smtp`）と ai-stock-trading（`ast-secrets`・`moomoo-credentials`・`moomoo-rsa`）は
  `deploy/local/vault/eso/rbac-bff-externalsecret-sync.yaml`。**名前集合が `items[].externalSecret` と完全一致することを xUnit で固定する。**
- NetworkPolicy: `networkPolicy.enabled` かつ同期有効かつ `services.bff.externalSecretSync.apiServerEgress.cidrs` が空でないときだけ
  BFF → API サーバ（ポート 443・6443）の Egress を描く。cidrs の既定は空（API サーバの所在は環境で違う。空なら穴を開けず、同期は `syncRequested:false` に倒れる）。

### 再起動（Reloader）

- `k8s-local-up.sh` の ESO=1: `ai-stock-trading` 名前空間を冪等に作り（Reloader の scoped RBAC と BFF の Role が要る）、
  `stakater/reloader` chart **2.2.17**（image `ghcr.io/stakater/reloader:v1.4.22`）を `reloader.watchGlobally=false`・
  `reloader.namespaces={microservices-platform,platform-infra,ai-stock-trading}` で入れる。上書きは `RELOADER_CHART_VERSION`。
- 注釈 `secret.reloader.stakater.com/reload`: llmgateway-service（`llm-provider-credentials`）・wiki-service（`wikijs-sync`）は
  `values-local.yaml` の `deploymentAnnotations`（chart の汎用テンプレートへ足す）、mail-relay（`keycloak-smtp`）は `deploy/mail-relay/mail-relay.yaml`。
  AST の消費側は AST#795。

### seed（`bootstrap.sh`）

- ヘルパ `vkv_exists <path>`（`vault kv metadata get`）で在否を見る。**無いときだけ `vault kv put`**。在るときは、env が**空でない**プロパティだけを `vault kv patch`。
- 対象: `msp/llm-provider-credentials`（`ANTHROPIC_API_KEY` / `OPENAI_API_KEY`）・`msp/wikijs-sync`（`WIKIJS_SYNC_APIKEY`）・
  `msp/keycloak-smtp`（`host`/`port`/`starttls` は構成なので毎回 patch、`from`/`user`/`password` は env が空でないときだけ）・
  `ai-stock-trading/app-secrets`（無いときだけ。`*-auth-client-*` 8 件は realm と同値、画面から書ける 12 件は空文字）。
- 作成は `vault kv put -cas=0`（Vault 側でも「無いときだけ」）。試験は「items[] のパスへの `kv put` はすべて `-cas=0` を持ち、`vkv_exists` の分岐の中にある」を固定する。
- `ai-stock-trading/moomoo` / `moomoo-rsa` は seed しない（Secret 不在で OpenD が待機＝fail-closed）。
- **items[] の全パスについて「無条件の `vault kv put`」が無いことを xUnit で固定する**。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| AC-1 | allowlist が 6 KV・21 プロパティ（書ける）を持ち、`deferred[]` に moomoo が無い。種別・sensitive・externalSecret の不正は起動しない | `SecretItemCatalogTests` |
| AC-2 | MD5 種別は平文ではなく小文字 hex MD5 を保管し、平文もハッシュも応答・監査・ログに出ない | `BffSecretItemEndpointTests` ＋ 変異（平文を保管） |
| AC-3 | 生成種別は値なしで RSA 1024 bit PKCS#1 PEM を保管し、応答・監査・ログに鍵が出ない。値を送ると 400 | 同上 ＋ 変異（鍵を応答に載せる） |
| AC-4 | 書き込み成功後に対象 ExternalSecret へ `force-sync` を merge-patch し `syncRequested:true`。失敗・未構成でも 200 で `syncRequested:false`、監査 `secret.item.sync failed` | 同上（偽の k8s API） |
| AC-5 | 書き込みが失敗したら同期を依頼しない | 同上 |
| AC-6 | Vault policy の path 集合が items[] と完全一致（6 KV × 2） | `SecretItemVaultPolicyTests` ＋ 変異（path を 1 本抜く） |
| AC-7 | RBAC の (ns, resourceNames) が items[] の externalSecret と完全一致し、verbs は get/patch だけ、束縛先は SA bff | `SecretItemExternalSecretRbacTests` ＋ 変異（名前を 1 つ足す） |
| AC-8 | bootstrap は items[] のどのパスも無条件に `kv put` しない。app-secrets の seed は realm と同値の 8 件と空の 12 件、moomoo は seed しない | `SecretItemBootstrapSeedTests` ＋ 変異（put を戻す） |
| AC-9 | ESO=1 で Reloader を pin して入れ、BFF の RBAC を apply する。ESO 未設定では入れない | `scripts/k8s-local-up.test.js` |
| AC-10 | 画面: 種別ごとの入力形、生成の確認、Discord ID の平文入力と注記、同期の表示。i18n に未翻訳なし | `SecretItemManagementPage.test.tsx` ＋ `check-i18n-catalogs` |
| AC-11 | 既存の SC-22 の試験（T-01〜T-49）が緑のまま | `dotnet test` ／ vitest |

## 走査した母集合（規則 1〜10）

走査は `origin/develop` `1196f400`、submodule（`src/ai-stock-trading`）と `CHANGELOG.md` を除く。

| 軸 | コマンド | 結果 | 扱い |
| --- | --- | --- | --- |
| A: 導出値（4 項目・13 プロパティ・4 KV） | `git grep -n -E "4 項目\|13 プロパティ\|4 KV\|HaveCount\(4\)\|Sum\(i => i.Properties.Count\)\|toHaveLength\(5\)\|…'更新'…toHaveLength"` | 30 行 | **直す**: `docs/screens/SC-22` :35、`docs/tests/SC-22` :45 :54 :71 :73、`deploy/local/vault/eso/README.md` :19、`SecretItemCatalogTests.cs` :21 :29、`BffSecretItemEndpointTests.cs` :653。`SecretItemManagementPage.test.tsx` :116 :145 は試験のモック（4 行）の数なので**変えない**。**除外**: 他 IADR・仕様書・他画面の「4 項目」（別の意味・16 行）。IADR-0433 :244 と IADR-0453 :171 :214 は**凍結本文**で、日付つき追記で数を更新する。凍結済みの仕様書 1411 の 2 件は書き換えない |
| B: moomoo | `git grep -n -i moomoo` | 13 行 | **直す**: `sc22-secret-items.json` :92（deferred から移す）、`SecretItemVaultPolicyTests.cs` :80（「moomoo は policy に無い」を反転）、IADR-0433 :150（追記）。**除外**: IADR-0077・仕様書 266 / 24 / 560 / 1088 / 763 / 1411（歴史記録）、`tag-seed/tags.json`（タグ語彙）、`deploy/local/vault/README.md` :6（ESO 名の説明で正しいまま） |
| C: SC-22 の KV への `kv put` | `git grep -n -E "kv put secret/(msp/llm-provider-credentials\|msp/keycloak-smtp\|msp/wikijs-sync\|ai-stock-trading/app-secrets)"` | 5 行 | **直す**: `bootstrap.sh` :45 :56 :139。**除外**: `wikijs-setup/bootstrap.sh` :277（Wiki.js が発行した鍵を書き戻す経路。単一プロパティ KV で、書く値は発行直後の実値＝消すものが無い）、`deploy/local/vault/README.md` :39（手動の例示）、仕様書 1102（凍結） |
| D: 「同期は触れない」「1h」 | `git grep -n -E "同期.*(触れない\|別の経路)\|最大 1 時間\|refresh 1h\|force-sync"` | 4 行 | **直す**: `docs/tests/SC-22` :28（対象外の行）、`bootstrap.sh` :144 の案内、Runbook :142 付近（画面なら自動で依頼される旨）。openapi :3562 は「ESO の同期とは別の経路で書く」で正しいまま（同期の依頼を追記する） |
| E: PEM / deferred の注記 | `git grep -n -E "PEM\|deferred\[\]" -- src/platform src/knowledge docs/screens docs/tests docs/api/BFF_bff-surface.md` | 6 行 | **直す**: `SecretItemBffEndpoints.cs` :28（「PEM は deferred」が偽になる）、`docs/screens/SC-22` :36 :148。**除外**: 試験の注記 2 行（items[] だけを読む、は変わらない） |

規則 8: 本表は自身が走査語を含む（軸 A〜E の検索語）。数は書く前の値である。

## テスト方針（TDD・赤の証跡は下の「実行記録」）

- C#: 既存の FakeVault に加え **FakeKubernetesApi**（要求の記録・状態の強制・例外）を `BffTestFactory` へ差す。
- 変異試験（手で壊して赤を確かめ、戻す）: ① MD5 → 平文を保管 ② RSA → 鍵を応答に載せる ③ RBAC の resourceNames に 1 つ足す ④ bootstrap の seed-if-absent を無条件 put に戻す ⑤ policy の path を 1 本抜く。
- 🔴 **試験コードに PEM の見出しを字面で書かない**（`.claude/hooks/guard-secrets.js` が秘密鍵の混入として止める）。生成された鍵は `RSA.ImportFromPem` で読み戻して形を確かめる。
- SPA: vitest（種別ごとの入力形・送信本文・生成の確認・syncRequested の表示）。
- シェル: `bash -n`・`node scripts/k8s-local-up.test.js`。

## 実行記録

環境: Windows 11・.NET SDK 10.0.301・helm v4.2.1・pnpm 10.33.0。`src/ai-stock-trading` は `git submodule update --init` 済み（BFF の試験のビルドに要る）。

### 赤（実装前）

| 対象 | コマンド | 結果 |
| --- | --- | --- |
| allowlist のスキーマ | 旧 `SecretItemCatalog.cs` に戻し `dotnet build src/platform/backend/Bff/Platform.Bff.Tests` | ビルド失敗: CS0103 `SecretPropertyKind` ×14、CS0246 `SecretPropertyDefinition` ×14・`ExternalSecretReference` ×2、CS1061 `PropertyDefinitions` ×10・`FindProperty` ×4・`ExternalSecret` ×2 |
| 端点（MD5・生成・同期・一覧の種別） | 試験と偽物（FakeKubernetesApi・構成キー）だけを先に入れて `dotnet test ... --filter BffSecretItemEndpointTests` | **失敗 13・合格 42**（新しい 13 件だけが落ち、既存 42 件は緑）: `Md5_property_stores_…`、`Generate_property_stores_…`、`Kind_specific_value_rules_reject_with_400(ast-moomoo-rsa…)`、`List_returns_property_details_…`、`Successful_write_requests_force_sync_…` ×4、`Failed_sync_request_does_not_fail_the_write` ×4、`Sync_not_configured_…` |

### 緑

| 対象 | 結果 |
| --- | --- |
| `SecretItemCatalogTests` ＋ `SecretItemVaultPolicyTests` | 合格 36 |
| SC-22 の BFF 試験（`SecretItem*`・`VaultKvClientTests`） | 合格 95 → RBAC / seed の試験を足して合格 103（途中 1 件は試験側の誤り —— `"secrets\"]"` が `"externalsecrets"]` に一致した —— を直した） |
| `node scripts/k8s-local-up.test.js` | 177 tests passed |
| `bash -n` | `bootstrap.sh`・`k8s-local-up.sh` とも OK |
| `helm template`（既定 / `-f deploy/local/values-local.yaml`） | 両方 exit 0。既定は Role も API サーバへの Egress も描かない。ローカルは Role（resourceNames 2）と Reloader の注釈（llmgateway・wiki）を描く。`--set externalSecretSync.enabled=true,apiServerEgress.cidrs={10.0.0.1/32}` で Egress（443・6443）を描く |
| `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | exit 0 |
| `check-openapi-dto-drift` | OK（同名 85 件） |
| `check-contract-schema` | 非破壊 3 件（`SecretItemStatusDto.PropertyDetails`・`SecretItemWriteResultDto.SyncRequested`・型 `SecretItemPropertyDto`）→ `--update`（文書化された流れ。破壊的 0・承認消費 0）→ OK |
| i18n | en 17 件を訳して `pnpm run i18n` の Missing 0、`check-i18n-catalogs` OK |
| SC-22 の vitest | 14 passed（既存 9 ＋ 新 5） |

### 変異試験（手で壊し、落ちるのを見てから戻した）

| # | 変異 | 落ちた試験と理由 |
| --- | --- | --- |
| ① | `SecretPropertyValues.Derive` の MD5 を「平文をそのまま返す」へ | `Md5_property_stores_…`: 保管値が期待の MD5 と index 0 で食い違う |
| ② | 端点の応答に生成した鍵を載せる（`mutant = generated ? derived : null`） | `Generate_property_stores_…`: `Did not expect raw … to contain`（応答の本文に鍵の見出し） |
| ③ | `rbac-bff-externalsecret-sync.yaml` の platform-infra の `resourceNames` に `bff-oidc` を足す | `Role_resource_names_equal_…`: `{"bff-oidc", "keycloak-smtp"} contains 1 item(s) too many` |
| ④ | `bootstrap.sh` の wikijs-sync の分岐の後ろに無条件の `vault kv put secret/msp/wikijs-sync …` を戻す | `Kv_puts_on_screen_written_paths_…`: その put が `-cas=0` を含まない |
| ⑤ | `policy-bff-secret-write.hcl` から `secret/data/ai-stock-trading/moomoo-rsa` を抜く | `Policy_paths_equal_the_allowlist_items_exactly` と `Policy_does_not_cover_deferred_or_excluded_paths` |

①②⑤ は 1 回の実行で 4 件失敗・3 件合格、③④ は 2 件失敗・6 件合格。戻した後はいずれも合格（MUTANT の字面が残っていないことを grep で確かめた）。

### 完了前の検証（2026-09-16）

| コマンド | 結果 |
| --- | --- |
| `dotnet test src/platform/backend/Bff/Platform.Bff.Tests` | **合格 743・スキップ 1・失敗 0**（3 分 10 秒） |
| `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | exit 0 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **782 tests passed**（1 回目は IADR 索引のタイトルセルが 200 字を超えて赤 → 本体 H1 の要約へ縮めて緑） |
| `node scripts/k8s-local-up.test.js` | 177 tests passed |
| `bash -n`（`bootstrap.sh`・`k8s-local-up.sh`） | OK |
| `helm template`（既定 / `-f deploy/local/values-local.yaml`） | exit 0 / exit 0 |
| `src/`: `pnpm run typecheck` | exit 0（submodule を init した後に `pnpm install --frozen-lockfile` をやり直して AST のワークスペースを結線した） |
| `src/`: `pnpm run lint` | exit 0（0 errors・12 warnings。警告はすべて本作業の外のファイル） |
| `src/`: `pnpm run format:check` | OK（新規 3 ファイルを prettier で整形した後） |
| `src/`: `vitest run knowledge/frontend/src/features/sc22-secrets` | 14 passed |
| `src/`: `pnpm run codegen` ／ `pnpm run i18n` | 生成物をコミット済み（en Missing 0） |
| `check-i18n-catalogs` | OK |
| `check-openapi-dto-drift` | OK（同名 85 件） |
| `check-contract-schema` | OK（非破壊 3 件を `--update` 済み） |
| `check-test-spec-coverage` | 床の上げ忘れ 2 件（`SecretItemExternalSecretRbacTests` / `SecretItemBootstrapSeedTests` を記載）→ `--update`（対 310 件）→ OK |
| `check-trace-blocks` | 1 回目は試験仕様書の本文の `PKCS#1` を参照と判定 → 「PKCS1 形式」へ言い換え（Runbook の同じ字面も）→ OK（177 件） |
| `gen-knowledge-graph --check` ／ `check-adr-numbering` ／ `check-doc-links` ／ `check-doc-type-vocabulary` ／ `check-plan-id-qualification` ／ `check-cross-repo-refs` | いずれも OK |

### 未実施

- T-40（稼働クラスタでの疎通・即時同期・Reloader・OpenD が生成した鍵を読むこと）。作業条件により稼働クラスタに触れていない。
