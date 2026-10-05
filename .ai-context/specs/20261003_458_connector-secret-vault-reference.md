---
title: 作業仕様書 — コネクタ資格情報を「Vault 移行までの暫定マスク」から Vault 参照へ移す（設計段・#458 残射程 a）
type: spec
status: draft
related_ids: [FR-01, UC-04, SC-06, SC-22, NFR-18, ADR-0005, ADR-0042, ADR-0095, ADR-0104, ADR-0110, ADR-0124, ADR-0126, IADR-0051, IADR-0053, IADR-0054, IADR-0055, IADR-0295, IADR-0403, IADR-0433, IADR-0453, IADR-0456, IADR-0460, IADR-0485, IADR-0493, IADR-0495, IADR-0501]
author: claude
created: 2026-10-03
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-18
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-04
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-06 / §SC-22
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md §認証・秘匿情報
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 実測 6・決定 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0104_sc22-item-kinds-and-dual-path-for-env-ids.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0110_sc22-supplier-three-values-no-public-key-restart-confirmed-at-write.md 決定 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1
  - planning:projects/microservices-platform/07_adr/ADR-0126_datasource-credentials-as-sc22-group-admin-write-runtime-supply.md 決定 1〜5（2026-10-06 追記）
related_specs:
  - 20260828_issue-458_connector-credential-exposure
  - 20260906_issue-458_security-posture
  - 20260928_issue-1682_paired-secrets-outside-sc22
issue: "#458"
---

# 作業仕様書 — コネクタ資格情報の Vault 参照化（設計段・#458）

> 本仕様書は**設計段（段 1）**である。製品コードは書かない。
> 実装の判断記録（新 IADR）は、本書 §計画への環流 の裁定が降りてから起こす（理由は §判定）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（データソース登録・同期）
- ユースケース（UC）: UC-04（事前条件「接続情報（認証情報はVault管理）が用意されている」）
- 画面（SC）: SC-06（データソース管理）・SC-22（秘密情報・接続設定の管理）
- 非機能要件: NFR-18（シークレット管理。Vault で集中管理・ローテーション）
- 関連 ADR（計画）: ADR-0005（#458 の起点）・ADR-0042（運用画面のロール）・ADR-0095・ADR-0104・ADR-0110・ADR-0124
- 関連 IADR: IADR-0403 決定 8（本件の移行段の設計）・IADR-0295（露出経路の封鎖）・IADR-0433 / IADR-0456（SC-22 の allowlist と書き込み policy）・IADR-0485（対になる秘密）
- 計画の基点: 隣接クローン `project-planning` `main` `b9d0d27`。🔴 **このクローンは shallow である**
  （`git rev-parse --is-shallow-repository` → `true`）。**以下の計画側の根拠はファイル本文の引用だけで、`git log` / `git blame` は 1 つも使っていない。**
- 実装の基点: `origin/develop` `233f432d`（`git rev-parse --is-shallow-repository` → `false`）。

## 目的・背景

#458 の 2026-09-11 監査が残射程 3 として挙げた「コネクタ資格情報の Vault 化が設計のみ」を進める。
現状は `DataSourceService` の `DataSources.Config`（jsonb）と `ConnectionUri` に**平文で保存**し、
外へ出る経路をマスクで塞いでいる（IADR-0295）。IADR-0403 決定 8 は移行を 4 段で設計した:

1. 書き込み時に値そのものではなく Vault の参照（`vault:msp/datasource/<id>#<key>`）を jsonb へ入れる
2. 読み出しはコネクタ実行時にのみ解決する（`IConnectorSecretResolver`）
3. 既存行の移送と、参照が解決できないときの fail-closed
4. ローテーションは Vault 側の版管理に委ねる

🔴 **ただし決定 8 は 2026-09-06 の起案で、その後に計画が投入の面を決めた**（2026-09-11 の ADR-0095 と SC-06 への追記）。
**段 1 の「書き込み時に（データソース側で）参照へ変換する」は、データソース側に投入の面があることを前提にしている。**
本書はその食い違いを計画の本文で確かめ、裁定が要るかを判定する。

## 対象範囲

- **対象（本段）**: 母集合の引き直し（規則 9・10）、窓の扱い（規則 11）、計画の決定範囲の判定、計画への環流文案、段割り。
- **対象外（本段）**: 製品コード・マニフェスト・文書（`docs/`）の変更。新 IADR の起案（§判定）。planning への起票（文案まで）。
- **対象外（本件全体）**: データソース以外の秘密（SC-22 の既存 6 項目・対になる秘密）。保存の暗号化（IADR-0295 決定 5）。Vault サーバ自体の本番運用（unseal・HA）。

## 判定 —— 計画は投入の面を決めているか

### 結論

| 問い | 判定 | 根拠（引用は下） |
| --- | --- | --- |
| ① 投入の面は SC-06 か SC-22 か | ✅ **計画が決めている —— SC-22 である** | 引用 1・2・3 |
| ② データソースごとに増える動的な項目が SC-22 の型に収まるか | 🔴 **計画は決めていない。かつ現行の決定と 3 点で衝突する** | 引用 4〜8 |
| ③ IADR-0403 決定 8 段 1 をそのまま実装してよいか | 🔴 **不可**（①に反する。データソース API が秘密を受け取り Vault へ書く形になる） | ① |

**したがって「計画の範囲で決められるか」への答えは No である。** ①は決まっているが、①に従うと②が未決のまま残り、
②の答え次第で Vault のパス設計・BFF の書き込み policy・ロール・画面の供給元表示がすべて変わる。
**実装 ADR で②を決めると、ADR-0095 決定 3・ADR-0110 決定 1・SC-06 のアクセス制御（確定 2026-08-05）のいずれかを実装側で読み替えることになる**
（`traceability.repo.md`「無いことは『実装側で作ってよい』ではない」と同じ向き）。→ §計画への環流 の文案を出す。新 IADR は裁定後に起こす。

### 計画本文の引用（①投入の面）

> 引用中の Markdown リンクは計画リポ内の相対パスであり本リポでは切れるため、リンクを外して表示名だけを残した（文言は変えていない）。

**引用 1** — `projects/microservices-platform/05_screens/01_screens.md` §SC-06 主要素（文書の status は `draft`）:

> 手動同期・設定の操作、認証情報は Vault 管理である旨の注記、変換ジョブ（SC-07）への導線。🔴 **［2026-09-11 追記］認証情報の投入は本画面では行わず SC-22 が担う**（ADR-0095 決定 1）。**本画面の注記は「Vault 管理である」ことを伝えるものであり、投入の面を示していなかった。**

**引用 2** — `projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md`（status `Accepted`）実測 6:

> SC-06（データソース管理）の主要素は「**認証情報は Vault 管理である旨の注記**」であり、**入力欄ではない。**
> 🔴 **データソースの資格情報についても、投入経路は画面の外にあると一度は選ばれている。** **本 ADR はその選択を、秘密情報については改める。**

**引用 3** — 同 ADR 決定 1 の表と §理由:

> | **秘密情報**（資格情報・API キー・接続秘密・通知の webhook） | **製品の画面**（SC-22） | 🔴 **Git に置けない。** … |

> **SC-06 はデータソース、SC-09 は ABAC である。** 🔴 **属さないものを既存画面へ足すと、その画面の目的が 2 つになる。**
> （検討した選択肢「投入の面」2: **既存画面へ投入欄を足す**（SC-06 と SC-09）— 新画面を作らないが、**秘密情報の扱いが 2 画面に分かれ** … → 不採用）

→ **Accepted の ADR が、データソースの資格情報を名指ししたうえで SC-22 へ寄せている。** ADR-0124 決定 1 の例外（対になる秘密）は
「データストアの資格情報（実装の `excluded`。7 件）」＝基盤自身のストア（`msp/postgres` 等）であり、**外部コネクタの資格情報は含まない**
（`deploy/bootstrap/sc22-secret-items.json` の `excluded[].vaultPaths` 7 件に datasource は無い。外部の発行元が回す点で LLM の API キーと同型）。

### 計画本文の引用（②動的な項目の扱い）

**引用 4（Vault の書き込み射程）** — ADR-0095 決定 3:

> 🔴 **書き込みの射程を項目で限る。** **BFF が Vault の任意のパスへ書けるようにしない** —— SC-22 が扱う項目の集合の外へは書けないこと。

実装はこれを「完全一致パスのみ・ワイルドカード禁止」と読んでいる（`deploy/local/vault/eso/policy-bff-secret-write.hcl` 冒頭:
「`secret/data/msp/*` と書いた瞬間に『SC-22 の項目の集合の外へ書けない』（ADR-0095 決定 3）が成立しなくなる」。IADR-0433 決定 1）。
**データソースごとの項目は登録のたびに増えるので、完全一致パスの静的 policy には載らない。** 接頭辞（`msp/datasource/*`）で切れば
「集合の外へ書けない」は**接頭辞の内側**としては成り立つが、それが決定 3 の「項目で限る」に当たるかは計画の文面から読めない。

**引用 5（供給元の 3 値）** — ADR-0110 決定 1:

> - **画面**: 同期先（ExternalSecret）が在る。
> - **画面以外**: 同期先が無い（手で作った Secret・配備スクリプト・Git など、画面から供給されていない）。

**引用 6（コネクタは実行時に取得）** — `06_technical/09_datasource-connectors.md`（status `fixed`）§認証・秘匿情報:

> すべての接続情報（サービスアカウント・APIトークン・OAuth・DB資格情報）は **HashiCorp Vault** で集中管理し、コネクタは実行時に取得する。コミット・ログへ出力しない。

→ コネクタの資格情報は ExternalSecret → k8s Secret の経路に**載らない**（データソースは実行時に増え、Pod の起動時に材料化できない。IADR-0403 決定 8）。
**引用 5 の判定規則をそのまま当てると、画面から書いた資格情報が「画面以外」と表示される。** ADR-0104 決定 2 は
「供給元は画面が推測しない」「配備時のスイッチが決めた事実を出す」と定めており、**事実と食い違う表示を出すことになる。**
引用 7 の再起動の確認も同じ前提（ESO 経由の供給）に立つ。

**引用 7（再起動の確認）** — ADR-0110 決定 3:

> **再起動を伴う項目では、送る前に確認の段を置く。** … **供給元が「画面以外」の項目では、再起動の確認を出さない**（書いても Secret が変わらず、再起動も起きない）。

→ 実行時解決の項目は再起動を伴わない（次の同期から効く）。**「再起動を伴わないが画面から供給される」項目の型が計画に無い。**

**引用 8（ロール）** — 05_screens §SC-06 アクセス制御（確定・2026-08-05）と §SC-22 アクセス制御:

> SC-06: **閲覧は管理者・運用者。登録・更新・無効化は管理者限定**（確定・2026-08-05。…）

> SC-22: **運用者・システム管理者ロール限定**（ADR-0042 決定 2 と揃える）。

→ 実装も同じ割り当てである（`DataSourceBffEndpoints.cs` の POST / PUT / PATCH / DELETE は `PlatformAuthPolicies.AdminOnly`。SC-22 の BFF 端点は運用者・システム管理者）。
**資格情報を SC-22 へ寄せると、SC-06 で管理者に限った「データソースの更新」の一部（接続の資格情報）を運用者が書けるようになる。**
これが意図された拡大かは計画に書かれていない。

## 母集合（規則 9・10）

> **他人の数えを転記していない。** 2026-08-28 の作業仕様書（`20260828_issue-458_connector-credential-exposure.md`）の表は参考にとどめ、
> **誤りの側の文字列**（平文の器 `Config` / `ConnectionUri`・秘密キー名・「Vault 移行までの暫定」）で引き直した。

### 引き方（実行したコマンド）

```console
$ git grep -n -E "\.Config\b|Config\[|ConnectionUri" -- src/knowledge/backend/Services/DataSourceService ':!*Tests*' ':!*Migrations*'
$ git grep -c -E "\"(apiToken|password)\"" -- src/knowledge/backend/Services/DataSourceService ':!*Tests*'
  → DatabaseConnector.cs:2 / SaaSConnector.cs:2 / WikiConnector.cs:2（各 1 行はコメント・1 行が実際の読み出し）
$ git grep -l -iE "apiToken" -- . ':!src/ai-stock-trading'           → 29 ファイル（下表に振り分け）
$ git grep -n "Vault 移行" -- .                                        → 15 行
$ git grep -ln -E "DataSourceDto|CreateDataSourceRequest|UpdateDataSourceRequest|PatchDataSourceRequest|/datasources" -- 'src/**/*.cs' ':!src/ai-stock-trading'
$ git grep -ln -iE "apiToken|connectionUri|datasource" -- 'src/*/frontend/**' 'src/packages/**'
$ git grep -n -E "path \"|capabilities" -- 'deploy/**/*.hcl'
$ git grep -ln "pg_dump" -- deploy scripts
```

陽性対照: `apiToken` の走査は Grafana のデータソース定義（`deploy/grafana/provisioning/datasources/`）を**別概念として**拾っており、
走査は deploy 配下まで届いている（下の除外を参照）。

### 引いた結果（経路別）

| # | 経路 | 箇所 | 現状 | 本件で変わるか |
| --- | --- | --- | --- | --- |
| W1 | 書き込み（サービス） | `DataSourceService/Features/DataSources/{Create,Update,Patch}/Endpoint.cs` → `Domain/DataSource.cs`（`Create` / `Update` / `Patch`。`SecretConfigMask.PreserveMasked`・`ConnectionUriPolicy.Preserve`） | `Config` の秘密キーを平文で受けて保存 | 🔴 **変わる** —— 秘密キーの受理をやめる（参照のみ、または受理しない） |
| W2 | 書き込み（BFF） | `Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs`（POST / PUT / PATCH。`AdminOnly`） | 素通しで中継 | 🔴 変わる（W1 に追随） |
| W3 | 契約 | `Knowledge.Contracts/Dtos/DataSourceDto.cs`（Create / Update / Patch の `Config`）・`docs/api/openapi.yaml`（PUT の説明「秘密（`apiToken` 等）が黙って消える」）・生成物 `src/platform/frontend/src/lib/api/generated/data-sources/data-sources.ts`・`scripts/openapi-dto-drift-allowlist.json` | `Config` は自由辞書 | 変わる（説明文と生成物） |
| W4 | 書き込み（画面 SC-06） | `sc06-datasources/components/DataSourceForm.tsx`（L21「認証情報はここに入力しない」） | **入力欄は既に無い**。`connectionUri` だけ | 変わらない（導線の追加のみ。案による） |
| W5 | 書き込み（画面 SC-22） | `sc22-secrets/*`・`Platform.Bff/Foundation/Endpoints/SecretItemBffEndpoints.cs`・`Foundation/Secrets/SecretItemCatalog.cs`（`externalSecret` 必須）・`deploy/bootstrap/sc22-secret-items.json` | 静的 6 項目のみ | 🔴 **変わる**（案 A / C）。§判定 ② が未決 |
| W6 | 種（seed） | `deploy/` `scripts/` に **データソースを作る種は 0 件**（`apiToken` の deploy 側のヒットは Grafana のみ） | — | 変わらない |
| S1 | 保存 | `Infrastructure/Persistence/DataSourceDbContext.cs`（`Config` jsonb・`ConnectionUri` varchar(2048)）・Migrations | 平文 | 🔴 変わる（既存行の移送） |
| S2 | 保存の複製 | `deploy/local/platform-backup/{postgres/cronjob.yaml,script/backup.sh}`（DB ごとの `pg_dump -Fc` を age で暗号化） | **平文の資格情報がバックアップへ入る**（暗号化はされる） | 移送後は参照だけが入る。**移送前のバックアップは平文を含み続ける**（残余リスクとして記録） |
| R1 | 読み出し（消費） | `DatabaseConnector.cs:139`（`password`）・`SaaSConnector.cs:175`（`apiToken`）・`WikiConnector.cs:117`（`apiToken`）。`FileSystemConnector` は資格情報を読まない | `source.Config` から直接 | 🔴 **変わる** —— 実行時に解決（`IConnectorSecretResolver`） |
| M1 | マスク（応答） | `Domain/SecretMask.cs`（`KeyMarkers`）・`SecretConfigMask.cs`（L27「Vault 移行までの暫定措置」）・`ConnectionUriPolicy.cs`・`DataSourceEndpoints.cs`（L44・L58・L61）・`List/Endpoint.cs`（L9） | 秘密キーの値を `***` に | 残す（多層。移送後は参照文字列も伏せるか判断。§設計） |
| M2 | マスク（画面） | `DataSourceManagementPage.tsx` L293（注記「接続情報（認証情報）は Vault 管理です」）・L329（`connectionUri` 表示） | 注記のみ | 注記の文言を「SC-22 で設定」へ（案による） |
| L1 | ログ | `Domain/SyncErrorRedactor.cs`・各コネクタの `LogWarning`・`DataSourceSyncService.cs` | 例外をオブジェクトで渡さない（IADR-0295） | 解決器の失敗ログを同じ規律に載せる（**値・参照先の値を出さない**） |
| E1 | 外部送出 | イベント（`Knowledge.Contracts` に `Config` を運ぶイベントは 0 件）・MCP（`McpServer` の datasource ヒットは属性解決で無関係） | 無し | 変わらない |
| V1 | Vault policy（ESO） | `deploy/local/vault/eso/policy-eso-read.hcl`（`secret/data/msp/*` に `read`） | 🔴 **`msp/datasource/*` に置くと ESO も読める** | 🔴 **変わる** —— パスを `msp/` の外へ置くか、ESO の policy から除く |
| V2 | Vault policy（BFF） | `policy-bff-secret-write.hcl`（完全一致パスのみ。`SecretItemVaultPolicyTests` が json と完全一致を固定） | 静的 | 🔴 変わる（案 A / C。§判定 ② 引用 4） |
| V3 | Vault の消費者 | `datasource-service` は Vault を一度も引かない（アプリコードに Vault クライアント 0 件。IADR-0403 決定 8）。`externalsecret-datasource-service-token.yaml` は s2s トークン | — | 🔴 変わる —— `datasource-service` に読み取り専用の k8s auth role を与える（**アプリが Vault を引く最初のサービスになる**） |
| V4 | 配備 | `deploy/helm/microservices-platform/values.yaml`（datasource-service の env）・`deploy/local/vault/eso/bootstrap.sh`・`vault-auth-rbac.yaml` | — | 変わる |
| D1 | 文書（live） | `docs/security/security.md` §「データソースのコネクタ資格情報 — DB 平文保存（Vault 移行までの暫定）」と L507・`docs/functional/FR-01_data-source-catalog.md` L96-97・`docs/screens/SC-06_datasource-management.md`（L88・L182・L191）・`docs/screens/SC-22_secret-item-management.md`（「扱う項目の集合は … `items[]` だけ（6 項目・21 プロパティ）」）・`docs/operations/secret-rotation-runbook.md` L210（「データソースの接続資格情報 … データソース管理画面の更新で差し替える」）・`docs/tests/FR-01_data-source-catalog.md`（T-13・T-18） | 「暫定」「画面の更新で差し替える」 | 🔴 変わる。**runbook L210 は計画（引用 1）と既に食い違っている**（SC-06 の SPA には入力欄が無く、差し替えは API 直叩きでしかできない） |
| D2 | 凍結記録（追記のみ） | `IADR-0051` / `0053` / `0054` / `0055`（「Vault 移行までの暫定」への 2026-08-28 追記）・`IADR-0295`・`IADR-0403` 決定 8・`IADR-0433` 決定 1 | — | 実装 IADR を起こすときに日付つき追記ブロックで後継を指す（本文は書き換えない） |
| T1 | 試験 | `DataSourceCredentialExposureTests`・`DataSourceSecretRedactionTests`・`DataSourceUpdateEndpointTests`・`UpdateDataSourceValidatorTests`・`SaaSConnectorTests`・`WikiConnectorTests`・`SyncErrorRedactorTests`・`BffDataSourceEndpointTests`・`SecretItemCatalogTests`・`BffSecretItemEndpointTests`・`SecretItemBootstrapSeedTests`・`DataSourceManagementPage.test.tsx` | — | 追随（各段の試験欄） |

### 除外とその理由

| 除外 | 理由 |
| --- | --- |
| `deploy/grafana/provisioning/datasources/` と `scripts/check-grafana-*.js` 等 | Grafana の「データソース」で、コネクタではない（陽性対照として扱った） |
| `src/ai-stock-trading`（submodule） | 別プロジェクト |
| `.ai-context/specs/` の 6 件（`apiToken` ヒット） | 凍結記録。書き換えない |
| `PostgresAdvisoryLockLeaseCoordinator.cs` の `LogWarning(ex, …)` | サービス自身の DB 接続であり、コネクタ資格情報ではない（2026-08-28 の除外を再確認） |
| `IADR-0079` L66 | 基盤 secret の話で主題が違う（同上） |

### 規則 10（この変更で新たに誤りになる自分の記述）

本段は `.ai-context/specs/` に 1 ファイル足すだけで、既存の記述を変えない。**本書の中で将来誤りになる記述**は次の 2 つであり、実装段で引き直す:

- 「`datasource-service` は Vault を一度も引かない」（V3）・「SC-22 は静的 6 項目」（W5 / D1）—— 段 S1・S2 が着地した時点で偽になる。
- 計画の引用 1〜8 —— 裁定で計画が改訂されたら、本書ではなく新 IADR が改訂後の本文を引く。

## 窓（規則 11）

本件には窓が 2 つある。

### 窓 1: 既存行の移送（平文の行 ↔ 参照の行が混在する期間）

- **増える側のプローブ P+**: 新規・移送済みの行（参照だけを持ち、平文を持たない）で同期が成功する。
- **減る側のプローブ P−**: 未移送の行（平文を持つ）で、移送期間中は同期が成功し、**移送期間の終了後は fail-closed で止まる**（「資格情報が未設定」の状態で失敗し、平文を使わない）。

| 形 | P+（参照の行） | P−（平文の行・期間中） | P−（平文の行・期間後） |
| --- | --- | --- | --- |
| 後の端だけ（参照だけを解決し、平文を即座に無視） | ○ | 🔴 ×（既存データソースが一斉に止まる） | ○ |
| 前の端だけ（平文を使い続け、参照を解決しない） | 🔴 × | ○ | 🔴 ×（平文を使い続ける＝fail-open） |
| **両端**（参照があれば参照、無ければ「移送期間フラグが真のときだけ」平文。期間の終了は平文の行が 0 件であることを数えてから切る） | ○ | ○ | ○ |

🔴 **この表は設計時の期待値であり、実測ではない**（製品コードがまだ無い）。**形は仮決めで、段 S4 の受け入れ基準を「この 3 列を `[Fact]` で実測し、表どおりになること」とする。**
期間の終了を時刻で切らない —— **平文の行の件数（`Config` の秘密キーに `vault:` 以外の値が残る行）を数える readiness 検査**を置き、0 件でなければフラグを偽にできない（偽にすると起動を拒否する）。

### 窓 2: SC-22 での書き込みと同期の実行

- 同期の実行は**実行の開始時に 1 回だけ**解決する（実行の途中で版が変わっても混ぜない）。
- **増える側**: 書き込み後に開始した同期は新しい版を使う。**減る側**: 書き込み前に開始した同期は古い版で完走する（途中で失敗させない）。
- 形は「開始時に解決」の 1 つで、両プローブとも満たす（解決を Discover / Fetch の呼び出しごとにすると、1 回の同期の中で版が混ざる —— 却下）。
- データソースの削除（`DELETE`）では Vault の値を消さない（BFF / datasource-service に `delete` / `destroy` を与えない。IADR-0433 と同じ）。孤児になった値の棚卸しは運用手順へ送る。

## 計画への環流（起票はしない。文案）

> 起票前の重複確認（2026-10-03）: `project-planning` の open issue は 9 件で、本件に当たるものは無い（`list_issues state=OPEN`）。
> 意味検索（「SC-22 秘密情報」）は planning#635（closed。moomoo 等への拡張）だけを返した。完了記録 `10_feedback/20260911_secret-input-face.md` は
> SC-06 への追記（引用 1）までで、動的な項目には触れていない。**同件の既存 issue は無い。**

---

**タイトル**: [feedback] データソースの資格情報を SC-22 から投入すると、データソースごとに増える項目が SC-22 の型（書き込み射程・供給元・ロール）に収まらない

**フィードバック元**: microservices-platform#458（セキュリティ暫定運用の解消・残射程「コネクタ資格情報の Vault 化」。#447 から委譲）／作業仕様書 `.ai-context/specs/20261003_458_connector-secret-vault-reference.md`

**実装側の根拠**: `IADR-0403` 決定 8（移行段の設計。2026-09-06）・`IADR-0433` 決定 1（BFF の書き込み policy は完全一致パスのみ）・`IADR-0456`（SC-22 の項目は ExternalSecret を必須で持つ）・`IADR-0295`（現状の担保は応答のマスク）

**起点 ID**: NFR-18 / FR-01 / UC-04 / SC-06 / SC-22 / ADR-0095 / ADR-0104 / ADR-0110 / ADR-0042

**種別**: 不足（決定の空白）＋ 衝突の確認（`decision-needed`）

**現状**:

- 投入の面は決まっている —— ADR-0095 実測 6・決定 1 と SC-06 の 2026-09-11 追記が、データソースの資格情報を SC-22 へ寄せた。
- しかしデータソースの資格情報は、SC-22 の既存項目と 3 点で型が違う。
  1. **数が実行時に増える。** ADR-0095 決定 3「SC-22 が扱う項目の集合の外へは書けないこと」を、実装は完全一致パスの静的な Vault policy で守っている（ワイルドカードを書いた時点で決定 3 が成り立たなくなる、と読んでいる）。データソースごとの項目はこの形に載らない。
  2. **ExternalSecret を経ない。** `06_technical/09_datasource-connectors.md`（fixed）は「コネクタは実行時に取得する」と定めている。ADR-0110 決定 1 の判定（ExternalSecret の有無）をそのまま当てると、**画面から書いた資格情報が「画面以外」と表示される**（ADR-0104 決定 2「供給元は事実を出す」と食い違う）。決定 3 の再起動の確認も同じ前提に立っており、「再起動を伴わないが画面から供給される」項目の扱いが無い。
  3. **ロールが違う。** SC-06 の更新は管理者限定（確定 2026-08-05）、SC-22 は運用者・システム管理者（ADR-0042 決定 2）。資格情報を SC-22 へ寄せると、データソースの接続の資格情報を運用者が書けるようになる。

**To-Be（案）**:

- **案 A — SC-22 に「データソースの資格情報」の項目群を設ける（推奨）**
  - 項目群はデータソースの登録で 1 件ずつ増え、削除（無効化）で一覧から消える。プロパティはコネクタ種別が決める（Wiki / SaaS = API トークン、業務 DB = パスワード）。
  - **書き込み射程**: 群の Vault パスを専用の接頭辞（例 `datasource/<データソース ID>`）に固定し、BFF の書き込みはその接頭辞と「登録済みのデータソース ID」の両方で限る。ADR-0095 決定 3 の「項目の集合」は、群については「接頭辞 ＋ 登録済み ID の集合」と読むことを明記する。
  - **供給元**: この群は 4 つ目の値ではなく、**「画面（実行時に取得）」**として出す（ExternalSecret の有無で判定しない）。再起動の確認は出さない（次の同期から効く旨を出す）。
  - **ロール**: この群の書き込みは管理者（SC-06 の更新と同じ）に限り、運用者には一覧の閲覧だけを開く。または SC-06 の確定を改め、運用者にも開く。どちらかを選ぶ。
  - SC-06 は各行から SC-22 の当該項目へ導線を置く（「認証情報を設定」）。登録直後は「未設定」と出る（SC-22 主要素 3）。
  - 影響: ADR-0095 決定 3 の補完・ADR-0110 決定 1・3 の部分改定・SC-22 / SC-06 の追記。
- **案 B — SC-06 の登録・更新フォームに書き込み専用の資格情報欄を置き、データソース側（BFF 経由）が Vault へ書く**
  - 管理者限定のまま、登録と資格情報が 1 画面で閉じる。IADR-0403 決定 8 段 1 の形そのもの。
  - 影響: ADR-0095 決定 1 の選択肢「既存画面へ投入欄を足す」を、データソースについてだけ採ることになる（実測 6 の判断と SC-06 の 2026-09-11 追記を改める）。秘密の投入面が 2 画面に分かれる（ADR-0095 §理由が避けた形）。
- **案 C — 資格情報は SC-22 の静的項目として運用者が先に作り、SC-06 はその項目名を「参照」として選ぶ**
  - UC-04 の事前条件「接続情報（認証情報はVault管理）が用意されている」に最も素直に沿う。SC-22 の型（静的な集合・ExternalSecret）を変えない。
  - 影響: 新しいデータソースのたびに **Git（`sc22-secret-items.json`）への項目追加と配備**が要る。データソースの登録が画面で閉じなくなり、FR-01 の運用（管理者が画面で登録）と噛み合わない。

**推奨**: **案 A**。投入の面（SC-22）を動かさず、ADR-0095 の「秘密情報の扱いを 1 画面に集める」を保つ。実行時取得（09_datasource-connectors）とも整合する。
裁定を要するのは 3 点 —— ①決定 3 の「項目の集合」に接頭辞＋登録済み ID の群を含めてよいか、②供給元の表示（「画面（実行時に取得）」）、③書き込みのロール（管理者限定か運用者にも開くか）。

**背景**: 実装は平文保存＋応答のマスクで止まっている（IADR-0295）。#458 の恒久 3 項目のうち Vault 集中管理の最後の残りであり、#447 も本件の着地で閉じる。
実装側の移行設計（IADR-0403 決定 8）は投入の面の裁定（2026-09-11）より前に書かれ、データソース側で参照へ変換する形を前提にしていた。

---

## 設計（裁定が案 A で降りた場合の段割り）

> 🔴 **S0 と S1 は案に依らない**（どの案でも実行時解決と fail-closed は要る。引用 6）。**裁定を待たずに着手できるのはこの 2 段だけ**である。
> S2 以降は案 A を前提にしており、案 B / C なら S2・S3 を差し替える。新 IADR（次番号 = 現在の最大 0492 ＋ 1 = **0493**。`check-adr-numbering.js` は「重複・欠番なし」を返している）は S0 の PR で起こし、IADR-0403 決定 8 段 1 を改める。

| 段 | 内容 | 主な対象 | 試験（受け入れ基準の写像） |
| --- | --- | --- | --- |
| **S0** 解決器の器（案に依らない） | `Domain/Ports/IConnectorSecretResolver` を置き、3 コネクタの資格情報の読み出し（R1）をすべて解決器経由にする。値の形は `vault:<path>#<key>`。**実装は Fake と「平文を返す移送期間用」だけ**で、Vault には繋がない。解決は同期の開始時に 1 回（窓 2） | `DataSourceService` Domain / ExternalServices / `DataSourceSyncService` | `[Fact]` 3 コネクタが `Config` を直接読まない（解決器の呼び出しを数える）／参照の解決失敗で同期が「資格情報未設定」の失敗状態になり、外部へ要求を出さない（fail-closed）／解決失敗のログ・`SyncError` に参照先の値も参照文字列のキー以外も出ない（`SecretMask` を通す）／同期の途中で版が変わっても 1 回の同期で混ざらない |
| **S1** Vault の読み取り経路（案に依らない） | `datasource-service` の ServiceAccount に k8s auth role（`read` のみ・専用接頭辞のみ）を与え、Vault 解決器を実装する。🔴 **パスを `msp/` の外（例 `secret/data/datasource/*`）に置き、ESO の `msp/*` policy（V1）に入らないようにする**。Vault 不達は fail-closed（同期を失敗させ、平文へ倒さない） | `deploy/local/vault/eso/`（policy・role・bootstrap）・helm values・compose・`check-secret-injected-options.js` の母集合確認 | policy の path 集合が専用接頭辞の `read` だけであることを固定する検査（`SecretItemVaultPolicyTests` と同型）／ESO の policy から読めないこと／Vault 不達・403・版削除で同期が止まり平文を使わないこと |
| **S2** SC-22 の項目群（案 A） | BFF の SC-22 カタログに「データソースの資格情報」群を足す（静的 `items[]` ＋ DataSourceService の一覧から引く群）。書き込みは専用接頭辞 ＋ 登録済み ID に限る。BFF の書き込み policy に専用接頭辞（`create` / `patch` のみ、`read` / `list` / `delete` 無し）。供給元は「画面（実行時に取得）」、再起動の確認は出さない | `Platform.Bff/Foundation/Secrets/*`・`SecretItemBffEndpoints.cs`・`policy-bff-secret-write.hcl`・`sc22-secret-items.json`（群の宣言） | 未登録 ID への書き込みが 404 で Vault へ届かない／ロール（裁定 ③）の 401 / 403／値を読み返す口が無い／群の項目に再起動の確認が出ない |
| **S3** 画面（案 A） | SC-22 に群を表示（未設定の明示）。SC-06 の各行から SC-22 の当該項目への導線、注記の文言を改める | `sc22-secrets/*`・`sc06-datasources/*`・i18n カタログ・`docs/screens/SC-06*` / `SC-22*` | Vitest: 群の表示・未設定・導線／e2e スモーク（既存 `sc06-datasources.smoke.spec.ts` に 1 本） |
| **S4** 移送と平文の受理停止 | 既存行の秘密キーを Vault へ移し `vault:` 参照へ置き換える一回きりのジョブ（書き手は運用者のトークン。BFF 経由ではない —— 移送は seed と同じく画面化の対象外。ADR-0095 決定 1）。移送期間フラグと readiness 検査（平文の行 0 件）。期間後は `Config` の秘密キーへの平文の書き込みを 400 で拒否（W1・W2・W3） | `DataSourceService`・移送スクリプト・helm values | 🔴 **窓 1 の表 3 列を `[Fact]` で実測する**（表どおりでなければ形を確定しない）／移送の再実行が冪等／平文の行が残るとフラグを偽にできない |
| **S5** 文書と記録 | D1 の live 文書の是正（security.md の「暫定」節を恒久の記述へ、FR-01、SC-06 / SC-22、secret-rotation-runbook L210 を「SC-22 で更新・Vault の版で戻す」へ）。ローテーション手順を runbook に足す（版管理による戻し）。D2 の IADR へ日付つき追記 | `docs/` 一式・`.ai-context/adr/` 追記 | `check-trace-blocks`・`gen-knowledge-graph --check`・runbook のリハーサル記録は環境待ち（#458 の残射程 4 と同じ） |

### 受け入れ基準（本件全体。S4 着地時に判定）

- [ ] `DataSources.Config` に秘密キーの平文を持つ行が 0 件である（readiness 検査が数える）
- [ ] コネクタが資格情報を得る経路は `IConnectorSecretResolver` の 1 本だけである
- [ ] 参照が解決できないとき、同期は外部へ要求を出さずに失敗し、平文へ倒れない
- [ ] 資格情報の投入・更新は計画が裁定した面（案 A なら SC-22）からだけ行え、値は読み返せない
- [ ] `datasource-service` の Vault 権限は専用接頭辞の `read` だけで、ESO の policy はその接頭辞を読めない
- [ ] 回転は Vault の版で行い、直前の版へ戻せる（runbook に手順。リハーサルは環境待ち）

## 計画書との差異

- 差異: **あり**。§判定 ② の 3 点（書き込み射程・供給元・ロール）が未決。→ §計画への環流 の文案。
- 実装側の既存記述の食い違い: `docs/operations/secret-rotation-runbook.md` L210 の「データソース管理画面の更新で差し替える」は計画（引用 1）と食い違う（SPA に入力欄は無く、実際は API 直叩き）。S5 で是正する（本段では触らない）。

## 未決事項

1. 計画の裁定（§計画への環流 の ①〜③）。**S2 以降の着手条件。**
2. Vault 解決器の実装手段（VaultSharp か素の HTTP クライアントか）。IADR-0403 決定 8 の「`VaultSharp` が全サービスへ入る」懸念は 1 サービスに限られるが、依存の追加は S1 の IADR で決める。
3. 参照文字列（`vault:…`）を応答で伏せるか。値ではないが Vault のパス構造を漏らす。既定は「伏せる」（`SecretMask` の対象を `Config` の秘密キー名で判定する現行規則のままなら自然に伏せられる）。
4. 移送前のバックアップ（母集合の表の S2 行）に残る平文の扱い —— 保持期間の満了で消えるのを待つか、移送後に世代を切るか。運用の判断。

## ［2026-10-03 追記 / #458］planning#716 の起票と段 S0 の実装

### 経過

- §計画への環流 の文案を **planning#716** として起票した（2026-10-03。`decision-needed`）。起票前の重複確認は本書 §計画への環流 の冒頭のとおり。
- 段 **S0**（案に依らない）を実装した。新 IADR は §設計 の注記どおり S0 の PR で起こした —— [IADR-0493](../adr/IADR-0493_connector-secret-resolver-port-and-fail-closed.md)
  （採番は `check-adr-numbering.js` の「重複・欠番なし」を確かめてから最大 0492 ＋ 1）。IADR-0403 決定 8 には日付つき追記で IADR-0493 決定 4 を指した（本文は書き換えていない）。
- **S1 以降は未着手。** S2・S3 は planning#716 の裁定待ち。

### 規則 10（この追記で誤りになる本書の記述）

- 冒頭の「本仕様書は設計段（段 1）である。製品コードは書かない」「新 IADR は裁定が降りてから起こす」と §対象範囲 の「対象外（本段）: 製品コード … 新 IADR の起案」は、
  **設計段の時点の記述**であり、本追記以後の段 S0 には当てはまらない（§設計 の注記「S0 と S1 は案に依らない … 新 IADR は S0 の PR で起こし」が S0 の着手根拠）。本文は凍結のため書き換えない。
- 母集合 V3「`datasource-service` は Vault を一度も引かない」は**S0 の後も真**である（S0 の解決器は Vault に繋がない）。S1 で偽になる。
- 母集合 R1「`source.Config` から直接」は**偽になった**。引き直し:
  `git grep -n -E '"(apiToken|password)"' -- src/knowledge/backend/Services/DataSourceService ':!*Tests*'` → 読み出しは 0 件
  （ヒットはコメント 4 行と、`CredentialKeys` が宣言するキー名の定数 3 行 `WikiConnector.ApiTokenKey` / `SaaSConnector.ApiTokenKey` / `DatabaseConnector.PasswordKey`）。
- live 文書のうち S0 で**新たに誤りになった**のは `docs/tests/FR-01_data-source-catalog.md` の T-13・T-18（「`Config.apiToken` 設定 → Bearer 送出」）だけで、本 PR で是正し、T-67・T-68 を足した。
  `docs/security/security.md` §データソースのコネクタ資格情報 は誤りにはならないが、読む経路の現状を 1 項足した。その他の D1（「暫定」節の恒久化・runbook L210）は S5 のまま。

### 段 S0 の設計（IADR-0493 の要約）

- ポート `Domain/Ports/IConnectorSecretResolver.ResolveAsync(configuredValue, ct)` → `ConnectorSecretResolution`（成功の値 / 失敗の符号。`ToString` は値を伏せる）。
- コネクタは `IDataSourceConnector.CredentialKeys` で資格情報のキーを宣言する（wiki / saas = `apiToken`、db = `password`、filesystem = なし）。
  ポートの `DiscoverAsync` / `FetchAsync` に `ConnectorCredentials` を足し、コネクタは `Config` ではなくそこから読む。
- `DataSourceSyncService` がコネクタを引いた直後・探索の前に、宣言されたキーだけを 1 回ずつ解決し、探索と全取得へ同じ `ConnectorCredentials` を渡す（§窓 2 の「開始時に解決」）。
- 参照の形 `vault:<path>#<key>`（`Domain/ConnectorSecretReference`。接頭辞は大文字小文字を区別しない。`ToString` はパスを伏せる）。パスの接頭辞は S1 で決める。
- 実装: 本番は `Infrastructure/Secrets/PlaintextPassthroughConnectorSecretResolver`（平文は素通し・`vault:` は `ResolverUnavailable`・形の誤りは `MalformedReference`）。試験は `Tests/FakeConnectorSecretResolver`。
- fail-closed: 失敗（`MalformedReference` / `ResolverUnavailable` / `NotFound` / `Empty` / `Unreachable`）で探索の前に止め、外部へ要求を出さない。
  `SyncResult.CredentialsResolved=false`・`LastSyncError` と応答は `credentials not resolved for '<キー名>' (<理由の符号>)`・連続失敗に数える・watermark は進めない。
  ログはキー名と理由の符号だけ。解決器の例外は型名だけを記録し、例外オブジェクトもメッセージも渡さない。

### 試験（§設計 S0 の試験欄の写像）

| S0 の試験欄 | `[Fact]` / `[Theory]` |
| --- | --- |
| 3 コネクタが `Config` を直接読まない（解決器の呼び出しを数える） | `ConnectorSecretResolutionTests.Sync_RealConnector_ResolvesOnce_AndSendsOnlyTheResolvedValue`（wiki / saas）・`Sync_RealDbConnector_ResolvesOnce_AndComposesOnlyTheResolvedPassword`／各コネクタ単体の `*_IgnoreConfig*_WhenCredentialsAreNotResolved` と `*_FromResolvedCredentials_NotFromConfig`（db は `Resolve_QuotesPasswordWithSpecialCharacters_IntoConnectionString` を解決済みの値へ改めた） |
| 解決失敗で「資格情報未設定」の失敗状態・外部へ要求を出さない | `Sync_UnresolvableCredential_FailsClosed_WithoutAnyOutboundRequest`（6 理由）・`Sync_Db_UnresolvableCredential_NeverOpensAConnection`／対照 `Sync_NoCredentialConfigured_DoesNotCallResolver_AndSyncsWithoutAuthorization` |
| ログ・`SyncError` に値も参照の内部も出ない | `Sync_ResolutionFailure_LeaksNeitherValueNorReferencePath`（3 理由。解決器の例外文に値とパスを入れた陽性対照つき） |
| 同期の途中で版が変わっても混ざらない | `Sync_VersionChangeMidSync_DoesNotMixWithinOneRun_AndTheNextRunUsesTheNewVersion` |
| （形の単体） | `ConnectorSecretReferenceTests`（参照の形・接頭辞の大小文字・移送期間用解決器・`ToString` が値とパスを出さない・宣言キーが `SecretMask.IsSecretKey` に当たる） |

### 変異試験（1 つずつ当てて戻した。フィルタ `ConnectorSecret|ConnectorTests` の 89 件で実行）

| # | 変異 | 結果 | 落ちた試験 |
| --- | --- | --- | --- |
| M1 | `WikiConnector.DiscoverAsync` が `new ConnectorCredentials(source.Config)` で接続（`Config` を直接読む） | 殺した | wiki の同期 1・単体 2 |
| M2 | 同じ変異を `SaaSConnector` へ | 殺した | saas の同期 1・単体 2 |
| M3 | `DatabaseConnector` のパスワードを `Config(source, "password", …)` から読む | 殺した | db の同期 1・単体 2 |
| M4 | 解決失敗の分岐を通さない（`credentials is null && false`） | 殺した | fail-closed 7 |
| M4b | 解決失敗を `ConnectorCredentials.None`（認証なし）へ倒す | 殺した | fail-closed 7 |
| M5 | 取得のたびに解決し直す（窓 2 の却下した形） | 殺した | 呼び出し回数 2・版の混在 1 |
| M6a | 解決器の例外の警告にマスク済みの `ex.Message` を足す | 殺した | 漏洩（unreachable） |
| M6b | 解決失敗の警告に `Config` の値（参照の文字列）を足す | 殺した | 漏洩（not-found / resolver-unavailable） |
| M6c | 解決器の例外の警告へ例外オブジェクトを渡す | 殺した | 漏洩（unreachable。`Exception` が null でない） |
| M7 | 解決した値が空でも失敗にしない | 殺した | fail-closed（empty） |
| M8 | 参照の接頭辞を大文字小文字を区別して判定する | 殺した | 単体 3・fail-closed（大文字の接頭辞） |

### 検証

- `dotnet build src/knowledge/backend/backend.slnx` → エラー 0（警告 1 件は未変更の `Knowledge.IntegrationTests/Search/IngestToSearchQdrantTests.cs` の CS0618。既存）
- `dotnet test` `DataSourceService.Tests` → 371 件すべて成功。`Knowledge.IntegrationTests` の DataSourceService 3 件は Postgres / RabbitMQ が無く skip（本環境）
- `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` → 差分なし
- `check-adr-numbering` / `check-trace-blocks` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-links` / `gen-knowledge-graph --check` / `REQUIRE_REPO_TESTS=1 scripts.test.js` / `check-commit-messages --range origin/develop..HEAD` → コミット時の結果は PR 本文に記す

### 残余リスク（S0 の時点）

- **平文保存はそのまま。** 移送期間用の解決器は平文を素通しする（S4 まで）。外への経路は IADR-0295 の応答のマスクが引き続き塞ぐ。
- `vault:` 参照を保存した行は、Vault 解決器（S1）が配備されるまで同期が `resolver-unavailable` で失敗し続ける（意図した fail-closed）。
  現状、参照を書き込む経路（S2・S3）は無いので、参照の行は API 直叩きでしか生まれない。
- SC-06 の画面は失敗を直近エラーの文で出すだけで、「資格情報未設定」を専用の表示にしていない（S3 で扱う）。

［2026-10-03 追記 / #458・CodeQL］PR #1727 の CodeQL `cs/cleartext-storage-of-sensitive-information`（2 件・high）が、解決失敗のログに資格情報の項目のキー名（`DatabaseConnector.PasswordKey` = `password`）を載せる行を検出した。値ではないが、安全側に倒して**ログ・`LastSyncError`・応答からキー名を外し、コネクタが宣言する順の 1 始まりの番号（`credential #1`）に置き換えた**。上の S0 の記述（`credentials not resolved for '<キー名>'`）はこの追記で改める。

［2026-10-03 追記 / #458・独立監査］PR #1727 の独立監査（任意の指摘 2 件）と AI レビュー（🟢 1 件）を受けた是正。

- **規則 10 の引き直し（上の「S0 で新たに誤りになったのは T-13・T-18 だけ」は不完全だった）。**
  `git grep -n -E 'Config\.(password|apiToken)|apiToken|password|キー名と理由|credentials not resolved' -- docs .ai-context/adr/IADR-0493*` で引き直し、
  コネクタ・同期に関わる行だけを残した（`SC-13` / `SC-15` のパスワードリセット・各 runbook の DB・ブローカ・認証基盤の `password` は別系統なので除外）。
  - `docs/tests/FR-01_data-source-catalog.md` T-25: 入力が「`Config.password` に `;`/`'`」のままだった。試験は `Config` に参照を置き、特殊文字は解決済みの `ConnectorCredentials` で渡す形へ S0 で改めていた → 入力・期待を改めた。
  - 同 T-21: 業務DB の「`Config` の値だけでは接続文字列へ合成しない」（`DiscoverAndFetch_IgnoreConfigPassword_WhenCredentialsAreNotResolved`）が表に無かった（Wiki・SaaS は T-13・T-18 に在る）→ 足した。
  - 同 T-67: 「資格情報を `Config` に持つ」→ 試験は参照（`vault:…`）を置いているので「資格情報の参照を `Config` に持つ」へ改めた。
  - `docs/security/security.md` §データソースのコネクタ資格情報 の「失敗の記録に出るのはキー名と理由の符号だけ」は、直前の CodeQL 追記（キー名を番号へ置換）で**誤りになっていた** → 番号と理由の符号へ改めた。
  - [IADR-0493](../adr/IADR-0493_connector-secret-resolver-port-and-fail-closed.md) 決定の「ログ・`SyncError`・応答に出すのは `Config` のキー名と理由の符号だけ」も同じ理由で誤りになっていた（同じ PR で起こした IADR なので本文を直した）。
  - T-13・T-18・T-68 は現状どおりで正しい。
- **T-68 の「応答」に試験が無かった。** サービス層の試験は `SyncResult.Message` までしか見ず、`POST /{id}/sync` の応答本文を測る試験が無かった →
  `DataSourceSyncEndpointTests.Sync_UnresolvableCredential_ResponseMessageLeaksNeitherReferenceNorKeyNorResolverText`（`resolver-unavailable` は本番配線の解決器、`unreachable` は例外文にパスを入れた解決器を DI で差し替え）を足した。
  変異: 端点の `message` に `ds.Config` の値（参照）を連結 → 2 件とも赤、戻して緑。
- `Program.cs` の解決器の登録を完全修飾から `using DataSourceService.Infrastructure.Secrets;` へ改めた（AI レビュー 🟢）。

## ［2026-10-03 追記 / #458・S1］段 S1（Vault の読み取り経路）の実装

### 経過

- 段 **S1**（案に依らない）を実装した。基点は `origin/develop` `84c80853`（S0 ＝ PR #1727 / IADR-0493 を含む。`git rev-parse --is-shallow-repository` → `false`）。
- 新 IADR は [IADR-0495](../adr/IADR-0495_connector-secret-vault-read-path-dedicated-prefix.md)。🔴 **採番の注意**: develop の最大は 0493 で、0494 は未マージの別ブランチ（#1728・`fix/NFR-18-1728-eso-force-sync-after-bootstrap`）が取る見込みのため 0495 とした。
  本ブランチ単独では `check-adr-numbering.js` が `[missing-number] IADR-0494 が欠番` を返す（意図した状態）。**#1728 の後にマージする**こと。#1728 が先に入らない場合はマージ時に 0494 へ改番する（ファイル名・自称番号・索引・本書・コード内コメント・IADR-0403 の追記・`docs/security/security.md` の trace ブロック・PR タイトル）。
- §未決事項 2（VaultSharp か素の HTTP クライアントか）は **素の HttpClient** に決めた（IADR-0495 決定 2。BFF の `VaultKvClient` が前例）。`src/Directory.Packages.props` は変えていない。

### 段 S1 の設計（IADR-0495 の要約）

- **パスと権限**: 参照のパスは KV マウントからの相対で専用接頭辞 `datasource/`（例 `vault:datasource/<データソース ID>#apiToken`）。
  policy `datasource-connector-read`（`deploy/local/vault/eso/policy-datasource-connector-read.hcl`）は `secret/data/datasource/*` の `read` の 1 本だけ。
  role `datasource-connector-reader` を SA `microservices-platform/datasource-service`（helm の `services.datasource.serviceAccount` が作る）にだけ束縛（`bootstrap.sh`）。
  🔴 ESO の `policy-eso-read.hcl` は**変えていない**（`secret/data/msp/*`・`ai-stock-trading/*` はこの接頭辞に一致しない）。母集合 V1 はこれで閉じる。
- **解決器**: `Infrastructure/Secrets/VaultConnectorSecretResolver`（k8s auth ログイン ＋ KV v2 の GET。トークンはリース満了の 30 秒前まで使い回し、403 で 1 度だけ取り直す）。
  平文は `PlaintextPassthroughConnectorSecretResolver` へ委ねて素通し、`vault:` は形と接頭辞（大文字小文字を区別・空 / `.` / `..` のセグメント拒否）を確かめてから Vault へ。接頭辞の外は Vault へ送らず `MalformedReference`。
- **失敗の写像**（列挙は増やさない）: 404・削除 / 破棄の版・`data.data` null・キー無し・非文字列 → `NotFound`、空白 → `Empty`、ログイン不能・403（取り直し後）・5xx・不達・時間切れ・解釈不能 → `Unreachable`。呼び出し側の取り消しだけは外へ出す。**どの失敗でも平文へ倒さない。**
- **ログ**: 状態コードと例外の型名だけ（値・パス・キー名・例外オブジェクト・例外文を出さない）。
- **配線**: `AddConnectorSecretResolver()`（`Program.cs`）。`Vault:Address` が在れば Vault 解決器、空なら S0 の素通し（判定は解決時。試験の構成の上書きが効くように起動前の `builder.Configuration` を読まない）。
  helm の `services.datasource.vault`（`address: ""` 既定・`role: datasource-connector-reader` を明示）が `Vault__Address` / `Vault__Role` / `Vault__AuthMount` を描く。compose は Vault を持たないので変えない（素通しのまま）。
- **NetworkPolicy**: 「BFF → Vault」を「`vault.address` を持つサービス → Vault」へ一般化（`allow-<name>-egress-to-vault`）。helm v3.16.4 で描画を比べ、
  BFF の分（`--set services.bff.vault.address=…`）は**変更前後で同一**、差分は datasource の SA・`serviceAccountName`・`Vault__*` の 3 つだけ、
  `services.datasource.vault.address` を与えたときだけ `allow-datasource-egress-to-vault` が描かれることを確かめた。`helm lint` → 0 failed。
- **母集合の確認**: `check-secret-injected-options.js` → OK（宣言 2 件）。`VaultConnectorSecretOptions` は秘密を持たない（名乗りは SA トークン）ので、宣言の語を付けておらず母集合に入らない（意図どおり）。
- age 鍵（`deploy/local/platform-backup/`）・バックアップの制約には触れていない。

### 規則 10（この追記で誤りになる記述の引き直し）

`git grep -n -E 'Vault を一度も引かない|Vault クライアント 0 件|Vault に繋がない|Vault 解決器は段 S1|段 S1|Vault の解決器は未配備|VaultSharp' -- docs deploy src scripts ':!src/ai-stock-trading'` で引いた。

- 本書 母集合 V3「`datasource-service` は Vault を一度も引かない」は**偽になった**（`Vault:Address` を構成した配備では引く）。本文は凍結のため書き換えない。
- `docs/security/security.md` §データソースのコネクタ資格情報 の「（Vault の解決器は未配備）」が**誤りになった** → 構成による配線・fail-closed・権限（専用接頭辞・ESO と交わらない）の 2 項へ改め、trace ブロックへ IADR-0495 を足した。
- コード内コメント 3 箇所（`IConnectorSecretResolver` の「Vault 解決器は段 S1 で足す」、`PlaintextPassthroughConnectorSecretResolver` の合成の予告、`ConnectorSecretReference` の「接頭辞は段 S1 が決める」）を現状へ改めた。
- `scripts/verify-oidc-edge-flow.sh` の「段 S1」は別件（検索の合言葉）で除外。`docs/security/security.md` §Vault 監査の「`auth_metadata_role="bff-secret-writer"` の行が画面、それ以外が画面以外」は**書き込み**の抽出条件の説明で、datasource の role は読み取りしかしないので抽出に現れない → 誤りにならない（変えない）。
- IADR-0493 の「Vault 解決器は段 S1」等は凍結記録 → 書き換えず、IADR-0403 決定 8 に日付つき追記で IADR-0495 を指した。

### 試験（§設計 S1 の試験欄の写像）

| S1 の試験欄 | `[Fact]` / `[Theory]` |
| --- | --- |
| policy の path 集合が専用接頭辞の `read` だけ | `ConnectorSecretVaultPolicyTests.DatasourcePolicy_IsExactlyReadOnTheDedicatedPrefix`・`ResolverPrefix_AndKvMount_MatchThePolicy`・`DatasourcePolicy_CannotReadOutsideTheDedicatedPrefix`（6 例。`msp/*`・AST・`datasourcex/`・metadata） |
| ESO の policy から読めない | `EsoPolicy_CannotReadTheDedicatedPrefix`（3 例。照合器の陽性対照 `secret/data/msp/postgres` つき）・`BffWritePolicy_DoesNotCoverTheDedicatedPrefix` |
| role の束縛先・helm との一致 | `Bootstrap_BindsTheReaderRoleToTheDatasourceServiceAccountOnly`・`HelmValues_DeclareTheDedicatedServiceAccount_RoleAndEmptyAddressByDefault` |
| Vault 不達・403・404・版削除で止まり平文を使わない | `VaultConnectorSecretResolverTests.VaultResponses_MapToTheExistingFailureCodes_AndNeverFallBackToPlaintext`（13 例）・`Forbidden_RetriesOnceWithAFreshToken_ThenFailsClosed`・`LoginFailure_FailsClosed_WithoutReadingData`（3 例）・`UnreadableServiceAccountToken_FailsClosed_WithoutAnyRequest`・`TransportFailure_FailsClosed_AndLeaksNothing`（不達・時間切れ・ログイン不達） |
| 形の誤り・接頭辞の外 | `ReferenceOutsideTheDedicatedPrefixOrMalformed_IsRejected_WithoutAnyRequest`（10 例）・`DedicatedPrefix_AcceptsNestedPaths` |
| 成功・平文の素通し・トークン | `Reference_IsReadFromTheKvV2DataPath_WithTheLoginToken`・`Plaintext_PassesThrough_WithoutTouchingVault`・`Forbidden_ThenSuccess_AfterTokenRefresh_Resolves`・`Token_IsReusedWithinTheLease_AndRenewedBeforeExpiry`・`CallerCancellation_Propagates` |
| ログに値・パス・キー名・例外文が出ない | 上の失敗系の全例 ＋ `Success_LogsNothingSensitive`（例外文に値・パス・キー名を入れた陽性対照つき） |
| 配線 | `Wiring_ChoosesTheResolverByVaultAddress`（4 例）・`Wiring_WithoutVault_KeepsReferencesFailClosedAsResolverUnavailable` |

`DataSourceService.Tests` は 371 → 431 件（+60）。

### 変異試験（1 つずつ当てて戻した。フィルタ `VaultConnectorSecret|ConnectorSecretVaultPolicy|ConnectorSecret` の 95 件で実行。スクリプトで適用・試験・復元）

| # | 変異 | 結果（赤の件数） |
| --- | --- | --- |
| M1 | Vault の失敗の状態コードで参照文字列を値として返す（平文へ倒す） | 殺した（3） |
| M2 | 専用接頭辞の判定を外す | 殺した（4） |
| M3 | `..` セグメントを通す | 殺した（1） |
| M4 | 接頭辞を大文字小文字を無視して比較する | 殺した（1） |
| M5 | 404 を `NotFound` にしない（`Unreachable` へ落ちる） | 殺した（3） |
| M6 | 削除・破棄の metadata を見ない | 殺した（2） |
| M7 | 空白の値を成功にする | 殺した（1） |
| M8 | 文字列でない値を受け入れる | 殺した（1） |
| M9 | 例外オブジェクトをログへ渡す | 殺した（3） |
| M10 | 例外文をログへ出す | 殺した（4） |
| M11 | 状態コードのログに参照のパスとキーを足す | 殺した（2） |
| M12 | 403 でトークンを取り直さない | 殺した（2） |
| M13 | トークンを使い回さない | 殺した（1） |
| M14 | 呼び出し側の取り消しも `Unreachable` へ畳む | 殺した（1） |
| M15 | 配線: 常に素通し | 殺した（1） |
| M16 | 配線: 常に Vault | 殺した（4） |
| M17 | policy を `secret/data/*` へ広げる | 殺した（6） |
| M18 | policy に `list` を足す | 殺した（1） |
| M19 | ESO の policy に `secret/data/datasource/*` を足す | 殺した（2） |
| M20 | role を `default` にも束縛する | 殺した（1） |
| M21 | `eso` role に datasource の policy を相乗りさせる | 殺した（1） |
| M22 | helm の values から role を省く（テンプレートの既定 = BFF の role で名乗る） | 殺した（1） |
| M23 | 平文も Vault へ送る（素通しへ委ねない） | 殺した（1） |

### 検証

- `dotnet build src/knowledge/backend/backend.slnx` → エラー 0（警告 1 件は未変更の `IngestToSearchQdrantTests.cs` の CS0618。既存）
- `dotnet test` `DataSourceService.Tests` → 431 件すべて成功
- `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` → 差分なし
- `bash -n deploy/local/vault/eso/bootstrap.sh` → OK（実クラスタでの bootstrap の実行は本環境に k8s / Vault が無く未実施）
- `helm template` / `helm lint`（v3.16.4）→ 上の §設計 NetworkPolicy のとおり
- 文書・規約の検査器はコミット時の結果を PR 本文に記す（`check-adr-numbering` は IADR-0494 の欠番 1 件を返す —— 上の採番の注意のとおり）

### 残余リスク（S1 の時点）

- **参照先へ書く面が無い**（段 S2・S3 は planning#716 の裁定待ち）。いまは運用者がコンソールで `secret/datasource/<…>` へ書き、API 直叩きで `Config` に参照を置くしかない。
- helm の既定は `vault.address: ""` なので、**配備の値を変えるまで本番の挙動は S0 と同じ**（参照は `resolver-unavailable`）。Vault を有効にした配備で `bootstrap.sh` を再実行しないと role が無く、ログインが失敗して `unreachable` で止まる（fail-closed）。
- NetworkPolicy の一般化は helm の描画を手で比べただけで、描画を固定する自動試験は無い（既存の BFF の分にも無い）。
- 実 Vault（KV v2 の 404 の本文・削除済み版の応答）との結合は偽のハンドラによる単体試験だけで、実測していない（環境待ち。#458 の残射程 4 と同じ）。
- SA トークンの読み口が BFF とサービス内の 2 つになった（IADR-0495 決定 2）。

［2026-10-03 追記 / #458・S1 監査］S1 の独立監査（条件付き GO）の指摘と対応。

| 指摘 | 対応 | 追加の変異（1 つずつ当てて戻した。フィルタ `ConnectorSecret` の 112 件） |
| --- | --- | --- |
| 🟡-1 試験の KV v2 成功応答が実 Vault の形と違う | `KvBody` の既定 metadata を実応答の形（`created_time`・`custom_metadata: null`・`deletion_time: ""`・`destroyed: false`・`version`）へ。削除・破棄の例にも `created_time` を足した | A1 空の `deletion_time` を削除済み扱い → 殺した（3） |
| 🟡-2 ログ漏洩の試験が SA の JWT と Vault のトークンを見ていない | JWT・`client_token` を一意な目印にし、`AssertNoLeak`（値・パス・キー名・JWT・トークン・例外オブジェクト）をログイン失敗・403・失敗写像の全例・不達・成功で断言 | A2 ログイン失敗のログに JWT → 殺した（3）／A3 非 2xx のログに `X-Vault-Token` → 殺した（9） |
| 🟡-3 `EscapePath` が試験で固定されていない | `PathSegments_AreUrlEncoded_AndStayUnderTheDedicatedPrefix`（`%2e%2e`・`..%2f`・`%2E%2E`・`\..\`・`?x=1`）。送られた `AbsoluteUri` が `/v1/secret/data/datasource/` で始まり、`%` は `%25` へ符号化されることを断言 | A4 `EscapePath` を恒等写像 → 殺した（5） |
| 🟢 networkpolicy の `$bff` | 未使用ではなく後続の「BFF → API サーバ」の段が使っていたので、**定義をその段の直前へ移した**（Vault の段は `$bff` に依らない）。helm v3.16.4 で既定・BFF の Vault と同期依頼の有効化・datasource の Vault 有効化の 3 通りを描き、変更前後で同一 | — |
| 🟢 404・500 ではトークンを取り直さない | `NonForbiddenFailure_DoesNotRelogin`（ログイン 1 回・GET 1 回） | A5 403 以外でも取り直す → 殺した（7） |
| 🟢 リース ≤ 60 秒の分岐 | `ShortLease_IsUsedInFull_NotShortenedByTheRenewMargin`（60 秒のリースは 59 秒後も使い、60 秒で取り直す） | A6 常にリース − 30 秒 → 殺した（1） |
| 🟢 `Vault:Address` の起動時検証 | `ValidateOnStart` で空（Vault なし）か絶対の http / https の URI に限る（`VaultConnectorSecretOptions.IsAcceptableAddress`）。`VaultAddress_IsValidatedOnStart`（9 例。`IStartupValidator`） | A7 `ValidateOnStart` を外す → 殺した（4）／A8 スキームを問わない → 殺した（4） |

`DataSourceService.Tests` は 431 → 448 件。

## ［2026-10-06 追記 / #458・S2・S3］planning#716 の裁定（計画 ADR-0126）を受けた段 S2・S3

### 経過と基点

- planning#716 は 2026-10-03 に裁定された（利用者裁定 → 計画 **ADR-0126**。案 A を採用。完了記録は計画リポ
  `projects/microservices-platform/10_feedback/20261003_datasource-credentials-sc22-group.md`）。本追記は裁定の本文
  （ADR-0126 決定 1〜5・05_screens §SC-22 / §SC-06 の 2026-10-03 追記）を隣接クローン `project-planning` `main` `c3ad458`
  で読んでから書いた。🔴 このクローンは shallow である（`git rev-parse --is-shallow-repository` → `true`）——
  根拠はファイル本文の引用だけで、`git log` / `git blame` は使っていない。
- 実装の基点は `origin/develop` `c63351de`（`git rev-parse --is-shallow-repository` → `false`）。S0（#1727）・S1（#1732）を含む。
- 新 IADR は **IADR-0501**（着手時の develop の最大は 0497。0498〜0500 は並行作業が予約していたため取らない。
  予約分はその後 develop へ入り、rebase 後の `check-adr-numbering.js` は欠番を返さない）。

### 裁定の要点（ADR-0126 から。実装が従う線）

| 決定 | 内容 | 本段での写像 |
| --- | --- | --- |
| 1 | SC-22 に「データソースの資格情報」の群。登録で 1 件増え、無効化で消える。パスは専用接頭辞 `datasource/<データソース ID>`。SC-06 は入力欄を置かず導線（「認証情報を設定」） | S2（群の一覧・書き込み）・S3（画面・導線） |
| 2 | 書き込みの射程は**専用接頭辞（権限の層）**と**登録済みの ID（BFF のコード）**の両方。BFF には専用接頭辞への `create`・`patch` だけ（`read`・`list`・`delete` なし） | S2（policy・登録済みの検査） |
| 3 | 群の書き込みは管理者だけ。運用者は閲覧だけ（項目名・未設定か・最終更新日時・最終更新者） | S2（認可）・S3（更新の操作を出さない） |
| 4 | 供給元は ExternalSecret の有無で判定しない。参照あり →「画面」＋「実行時に取得・次の同期から効く」、平文 →「画面以外」、判定不能 →「確認できない」。再起動の確認は出さない | S2（判定）・S3（表示） |
| 5 | 3 点セット: 読み取り側だけ実装済み。群・書き込み・画面は無い | 本段で「群・書き込み・画面」が入る。平文の移送（S4）と本番の Vault 有効化は残る |

### 設計（IADR-0501 の要約）

- **配置（ユニットの依存規則を守る形）**: 群の仕組み（認可・専用接頭辞・Vault への書き込み・監査・値を返さない）は
  platform の BFF（`Platform.Bff/Foundation`）が持ち、**群の成員（登録済みのデータソース）と参照の配置**は knowledge の
  BFF モジュールが `Platform.Shared.Infrastructure` のポート `ISecretItemGroupSource` を実装して供給する。
  platform の基盤コードは DataSourceService も `Knowledge.Contracts` も参照しない（規則 1・例外 3 の内側）。
- **群の宣言（単一情報源）**: `deploy/bootstrap/sc22-secret-items.json` に `groups[]` を足す
  （`group: datasource-credentials`・`vaultPathPrefix: datasource`・`writers: admin`）。接頭辞は 1 セグメントで、
  `items[]` のどのパスの先頭セグメントとも交わらないことを起動時に検査する（fail-closed）。
- **Vault の権限**: 新しい policy `bff-secret-group-write`（`deploy/local/vault/eso/policy-bff-secret-group-write.hcl`）。
  `secret/data/datasource/+` に `create`・`patch`、`secret/metadata/datasource/+` に `read` だけ。🔴 `+` は 1 セグメント
  だけに一致する（`datasource/<ID>/<さらに下>` へは書けない）。metadata の `read` は静的な項目と同じ形
  （版と作成時刻だけ。値を持たない）で、主要素 3 の「未設定」と最終更新日時に要る。role `bff-secret-writer` に既存の
  `bff-secret-write` と並べて付ける。既存の静的 policy（完全一致・ワイルドカードなし）は変えない。
- **DataSourceService の 2 端点**（`Features/DataSources/Credentials/`）:
  - `GET /datasources/credentials`（管理者・運用者）: 有効で、コネクタが資格情報のキーを宣言するデータソースだけを返す。
    キーごとに `reference`（`vault:datasource/<自分の ID>#<キー>` の正規の参照）/ `other`（平文・別の場所の参照）/
    `absent`（値なし）。**値も参照の文字列も返さない。**
  - `PUT /datasources/{id}/credentials/{key}/reference`（管理者だけ）: キーが `absent` のときだけ正規の参照を置く。
    `reference` は何もしない。`other`（平文）は**置き換えない**（移送は段 S4。決定 4「画面以外」）。
- **BFF の 2 端点**（`/bff/secrets/groups/{group}`）:
  - `GET`（運用者・システム管理者）: 成員ごとに状態（KV 単位の 3 値）・版・最終更新日時・最終更新者・供給元と、
    利用者がこの群を書けるか（`writable`）を返す。
  - `PUT /{memberId}`（**管理者だけ**）: 認可 → 群 → **登録済みの検査（成員に無ければ 404。Vault へ届かない）** →
    本文（上限つきの手読み。静的な項目と同じ）→ プロパティ（成員のキーに無ければ 400）→ 値・理由 → Vault へ 1 プロパティ →
    書き込み記録 → 監査 → **参照の配置**（`absent` のときだけ）。応答は版・日時・書き込み後の供給元だけで、**値を返さない**。
    ExternalSecret の同期依頼はしない（同期先が無い）。
- **供給元**（決定 4）: 成員のキーのどれかが `other` →「画面以外」（契約の値 `git`）、それ以外 →「画面」（契約の値 `screen`）。
  `absent` は「画面」とする —— 画面から書けば参照が置かれて次の同期から効くので、「書いても効かない」ではない。
  BFF が成員を取れなければ一覧そのものを 502 にする（2 値へ寄せない）。書き込み後に参照の配置が失敗したら `unknown`。
- **画面（S3）**: SC-22 に群の表を足す（項目名＝データソース名と種別、`datasource/<ID>`、最終更新日時と状態、最終更新者、
  供給元「画面（実行時に取得・次の同期から効く）」／「画面以外」／「確認できない」、操作）。**更新の操作は `writable` のときだけ**。
  更新フォームはマスク入力＋確認入力 2 度、再起動の確認の段は無い。SC-06 の各行（有効なもの）に
  「認証情報を設定」（運用者には「認証情報の状態」）の導線 → `/admin/secrets?datasource=<ID>`。SC-22 はその行を強調し、
  書ける利用者には更新フォームを開く。注記の文言を「認証情報は『秘密情報・接続設定の管理』画面で設定します」へ改める。

### 母集合（規則 9・10。着手前に引いた）

```console
$ git grep -n -E "items\[\] だけ|6 項目|書き手は段 S2|段 S2|S2・S3|planning#716|Vault 管理です|認証情報はここに入力しない|データソース管理画面の更新で差し替える" -- docs deploy src scripts .ai-context/adr ':!src/ai-stock-trading' ':!*.po'
$ git grep -ln -E "/bff/secrets|SecretItemStatusDto|sc22-secret-items" -- . ':!src/ai-stock-trading'
```

| 箇所 | この段で誤りになるか | 扱い |
| --- | --- | --- |
| `deploy/bootstrap/sc22-secret-items.json` の `$comment`「items[] だけが allowlist」 | 🔴 なる（群が加わる） | 群の行を足す |
| `deploy/helm/.../values.yaml` L978「書ける項目は … items[] だけ」 | 🔴 なる | 群を併記 |
| `docs/api/openapi.yaml` L3737 の注記 | 🔴 なる | 群の 2 端点を足し、注記を改める |
| `deploy/local/vault/eso/policy-datasource-connector-read.hcl` L7「書き手は段 S2 以降で決める」 | 🔴 なる | 書き手（BFF の群 policy）を指す |
| `ConnectorSecretVaultPolicyTests.cs` L97 のコメントと `BffWritePolicy_DoesNotCoverTheDedicatedPrefix` | 🔴 なる（BFF は群の policy で書ける） | 静的 policy は触れない・群 policy は書き込みだけで読めないこと、へ改める |
| `docs/screens/SC-22_secret-item-management.md` L35「items[] だけ（6 項目）」 | 🔴 なる | 群の節を足す |
| `docs/screens/SC-06_datasource-management.md`（注記・導線） | 🔴 なる | 導線と注記 |
| `docs/operations/secret-rotation-runbook.md` L210「データソース管理画面の更新で差し替える」 | 既に誤り（S0 時点で記録済み）。この段で正しい面ができる | 「SC-22 の群で更新」へ改める |
| `docs/security/security.md` §データソースのコネクタ資格情報 | 🔴 なる（書き込みの面ができた） | 書き込みの項を 1 つ足す（平文保存の「暫定」節は S4/S5 のまま） |
| `docs/tests/SC-22_secret-item-management.md` | 試験が増える | 群の行を足す |
| `DataSourceManagementPage.tsx` L293 の注記と試験 L393 | 🔴 なる | 文言と試験を改める |
| `DataSourceForm.tsx` L21「認証情報はここに入力しない」 | ならない（入力欄は置かない） | 変えない |
| IADR-0403 決定 8・IADR-0433・IADR-0460・IADR-0495 | 凍結記録 | 日付つき追記で IADR-0501 を指す（本文は書き換えない） |
| `secret-rotation-runbook.md` L34「`items[]`（6 項目）」・`docs/tests/SC-22` T-10/T-27/T-29「6 項目」 | ならない（静的な項目の数は変わらない） | 変えない |

除外: `.ai-context/specs/`（凍結）・`src/ai-stock-trading`（別プロジェクト）・`6 項目` の他の文脈（通知・gRPC）。

### 窓（規則 11）

本段の窓は「SC-22 で書く」と「参照を置く」の間（2 つの書き込みが別の器にある）。

- **増える側のプローブ P+**: 値が無い（`absent`）データソースへ画面で書く → 次の同期が書いた値で認証する。
- **減る側のプローブ P−**: 平文（`other`）を持つデータソースへ画面で書く → 同期は**平文のまま**（画面の値に切り替わらない。移送は S4）。

| 形 | P+ | P− |
| --- | --- | --- |
| 後の端だけ（Vault へ書くだけで、参照は置かない） | 🔴 ×（参照が無いので書いた値は使われない） | ○ |
| 前の端だけ（参照を常に置く＝平文を参照で上書き） | ○ | 🔴 ×（平文の移送を画面が黙って行う。決定 4 の「画面以外」と食い違い、Vault の値が平文と違えば同期の認証が変わる） |
| **両端**（Vault へ書いた**後**に、`absent` のときだけ参照を置く） | ○ | ○ |

順序: **Vault が先、参照が後。** 逆にすると参照の置かれた行が値の無い Vault を指し、次の同期が `not-found` で止まる
（fail-closed なので漏れはしないが、書き込み失敗時に同期を壊す）。BFF の試験で 2 プローブと順序を `[Fact]` で固定する
（`Write_to_a_member_without_a_value_writes_vault_then_places_the_reference`・`Write_to_a_member_with_plaintext_keeps_it_and_reports_not_screen`）。

### 受け入れ基準と試験（S2・S3）

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC-1 | 群の書き込みは管理者だけ。運用者は 403（監査に残り、Vault へ届かない）。未認証 401 | BFF `Group_write_is_admin_only_and_operator_denial_is_audited_without_touching_vault`・`Anonymous_gets_401_for_the_group` |
| AC-2 | 運用者・管理者は一覧を引ける。他ロールは 403。一覧は `writable` を役割どおりに返す | BFF `Group_list_is_open_to_operators_and_admins_and_reports_writable_by_role`・`Other_roles_get_403_for_the_group_list` |
| AC-3 | 未登録・無効・資格情報を持たない種別の ID へは 404 で、Vault へ届かない | BFF `Unregistered_member_gets_404_and_vault_is_never_touched` |
| AC-4 | 書き込みは専用接頭辞の 1 セグメントの下だけ（ID の形が崩れていれば 404。`..` やスラッシュを Vault のパスに入れない） | BFF `Malformed_member_ids_never_reach_vault`・`Write_goes_to_the_dedicated_prefix_path_only` |
| AC-5 | 値は応答・監査・ログのどこにも出ない | BFF `Group_write_never_echoes_the_value_in_response_audit_or_logs` |
| AC-6 | policy は専用接頭辞の `+` に `create`・`patch`（data）と `read`（metadata）だけ。`msp/*`・AST・`list`・`delete` なし。role に付く | BFF `SecretItemGroupVaultPolicyTests`（5 件） |
| AC-7 | 供給元: 参照 → 画面、平文 → 画面以外、値なし → 画面、成員が取れない → 502 | BFF `Supply_source_follows_the_members_credential_state`・`Registry_unavailable_returns_502_not_an_empty_group` |
| AC-8 | 書いた後、値なしのキーにだけ正規の参照が置かれる（窓の両端） | BFF 上記 2 件・DataSourceService `PlaceReference_*`（4 件） |
| AC-9 | DataSourceService の一覧は有効・資格情報を持つ種別だけを返し、値も参照の文字列も返さない。参照の配置は管理者だけ | DataSourceService `ListCredentials_*`・`PlaceReference_is_admin_only` |
| AC-10 | 群の宣言は fail-closed（接頭辞が `items[]` と交わる・書き手が admin 以外・形の誤り） | BFF `SecretItemCatalogTests` の群の 4 件 |
| AC-11 | 画面: 群の表・未設定・供給元の 3 値・運用者に更新を出さない・更新フォーム（確認入力 2 度・再起動の確認なし）・`?datasource=` で行を強調 | Vitest `DataSourceCredentialGroupPanel.test.tsx` |
| AC-12 | SC-06: 各行の導線（管理者「認証情報を設定」・運用者「認証情報の状態」）と注記 | Vitest `DataSourceManagementPage.test.tsx` |
| AC-13 | e2e スモーク: SC-22 に群が出る（運用者に更新が無い）・SC-06 から導線で SC-22 へ | `sc22-secrets.smoke.spec.ts` / `sc06-datasources.smoke.spec.ts` に各 1 本 |

### 対象外（本段）

- **S4（平文の移送と平文の書き込み拒否）**: 既存行の平文は `other` のまま残り、画面は「画面以外」と出す。
- 本番で Vault を読む配備の値（ADR-0126 フォローアップ 3）。
- 無効化したデータソースの Vault の値の扱い（フォローアップ 4。計画・実装の両方で未決）。

### 実装の結果（S2・S3）

- 実装は §設計（IADR-0501 の要約）どおり。着手後に変えた点は 2 つ:
  1. 🔴 **群の認可のヘルパは群の端点のファイルに置いた。** 当初は静的な項目の `DenyUnlessWriterAsync` にポリシーを引数で渡す形にしたが、
     `check-bff-authz-docs.js` は同一ファイルのヘルパ本体の `AuthorizeAsync(…, PlatformAuthPolicies.X)` から実効ロールを読むため、静的な項目の 2 端点が
     「ロール制約なし」、群の `GET` が（`writable` の判定を拾って）「管理者だけ」と読まれた。ヘルパを各ファイルへ戻し（ポリシー名は本体に直書き）、
     表示用の `writable` の判定はポリシー名を変数で渡す別のヘルパにした（閲覧の可否ではないため）。拒否の記録（`Forbidden`）だけを共有する。
  2. 成員 ID の形の試験（`Member_id_pattern_admits_only_one_lowercase_segment`）を足した。後段の一覧が 2 段目の守りになり、形の検査を外す変異が
     端点の試験だけでは生き残り得るため（下の M4）。
- `..`・`%2e%2e` の ID は URI の正規化で 1 段上（`PUT /bff/secrets/groups` ＝ 静的な項目 `groups`）へ畳まれて 400 になる（どちらでも Vault へは届かない。試験は 404 と 400 の両方を許す）。

### 変異試験（1 つずつ当てて戻した。スクリプト `scratchpad/mut/run.py` で適用・試験・復元）

BFF はフィルタ `SecretItemGroup|SecretItemCatalog`、DataSourceService は `DataSourceCredentialGroup|ConnectorSecretVaultPolicy`、画面は当該の Vitest ファイル。

| # | 変異（守りの種類） | 結果（赤の件数） |
| --- | --- | --- |
| M1 | 群の書き込みの認可を `SecretItemWriter`（運用者も可）へ緩める（認可） | 殺した（1） |
| M2 | 群の一覧の認可を `AdminOnly` へ狭める（認可） | 殺した（1） |
| M3 | 登録済みの検査を外す（成員の照合を常に真）（射程） | 殺した（3） |
| M4 | 成員 ID の形の検査を外す（射程） | 殺した（9） |
| M5 | Vault のパスから接頭辞を落とす（射程） | 殺した（4） |
| M6 | 書き込みの応答に値を載せる（値を返さない） | 殺した（2） |
| M7 | 監査の detail に値を載せる（値を返さない） | 殺した（1） |
| M8 | 参照を Vault への書き込みより前に置く（窓の順序） | 殺した（2） |
| M9 | 値なしを「画面以外」に数える（供給元） | 殺した（1） |
| M10 | 平文を無視して常に「画面」（供給元） | 殺した（3） |
| M11 | `writable` を常に真（認可の表示） | 殺した（1） |
| M12 | 成員が取れないとき空の群へ縮退（失敗の可視化） | 殺した（1） |
| M13 | 群の接頭辞と `items[]` の交差を検査しない（宣言の fail-closed） | 殺した（1） |
| M14 | 群の書き手に `admin` 以外を受け入れる（宣言の fail-closed） | 殺した（1） |
| M15 | 群の policy を `+` から `*` へ広げる（権限の層の射程） | 殺した（3） |
| M16 | 群の policy の data に `read` を足す（値を読み返さない） | 殺した（1） |
| M17 | 参照の配置で平文も置き換える（窓の減る側） | 殺した（1） |
| M18 | 参照の配置の端点から `AdminOnly` を外す（後段の認可） | 殺した（1） |
| M19 | 群の成員に無効なデータソースも入れる（射程） | 殺した（1） |
| M20 | どんな `vault:` 参照でも `reference` とする（供給元） | 殺した（1） |
| M21 | 成員の一覧に設定の値を載せる（値を返さない） | 殺した（3） |
| M22 | 画面: `writable` を無視して「更新」を出す（認可の表示） | 殺した（2） |
| M23 | 画面: 「実行時に取得・次の同期から効く」を落とす（供給元の表示） | 殺した（1） |
| M24 | SC-06: 無効なソースにも導線を出す（導線） | 殺した（1） |

### 検証（2026-10-06）

- `dotnet build`（platform・knowledge）→ エラー 0（警告は未変更の `IngestToSearchQdrantTests.cs` の CS0618 だけ。既存）
- `dotnet test` platform → 全件成功（`Platform.Bff.Tests` 837 ＋ skip 1）。knowledge → `DataSourceService.Tests` 448 → 464 件すべて成功。
  🔴 `Knowledge.IntegrationTests` の `ObjectStorageRoundTripTests` 3 件が S3 互換ストレージのコンテナの内部エラーで失敗（本変更と無関係。本環境のコンテナ起因）
- `dotnet format --verify-no-changes`（両ユニット）→ 差分なし
- `src/`: `pnpm install --frozen-lockfile`・`lint`（エラー 0。警告は既存）・`typecheck`・`format:check`・`build`・`codegen` の再生成差分なし・`i18n` の再生成差分なし・
  `check-i18n-catalogs` OK・`test:coverage` 1827 件（🔴 本環境 4 コアでは 5 秒の既定タイムアウトで落ちる試験が実行ごとに入れ替わって出る —— SC-22 の既存 3 件のほか
  `searchFlow`・SC-10 も。単独実行では全件成功し、SC-22 の既存試験の所要時間は変更の前後で同じ〔約 0.5〜1 秒〕）・Playwright 全 76 件成功
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → rebase 前は `check-adr-numbering` の IADR-0498〜0500 の欠番（予約）だけで止まった（予約番号の仮ファイルを置くと 918 件すべて成功）。rebase 後の結果は PR 本文に記す
- 文書・規約: `check-trace-blocks`・`check-test-traceability`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-links`・
  `check-unit-dependencies`・`check-openapi-dto-drift`・`check-bff-authz-docs` → OK。`check-contract-schema --update`・`check-test-spec-coverage --update` で基準を更新（型の追加・仕様書 × クラスの対の追加）
- コミット後に `check-doc-updated --base origin/develop`・`check-commit-messages --range origin/develop..HEAD`・gitleaks を走らせる（PR 本文に記す）

### 残余リスク（S2・S3 の時点）

- 🔴 **接頭辞の内側では、登録済みの ID への限定は BFF のコードに依る**（ADR-0126 §結果 が受容した形）。
- 🔴 **既存の平文の行は「画面以外」のまま**（画面で書いても使われない）。段 S4 まで。
- 🔴 **本番は Vault を読まない**（helm の既定）。群で書いた値が本番で効くのは配備の値を改めた後。
- 🔴 **無効化したデータソースの値は Vault に残る**（削除の権限が無い。ADR-0126 フォローアップ 4）。
- 群の policy の `+` と metadata の `read` は、実 Vault に対して未実測（試験は字面と偽の Vault）。稼働クラスタでの疎通は #458 の残射程 4 と同じ場。
- 書き込みの後の参照の配置は 2 つ目の要求であり、Vault の書き込みと原子的ではない（失敗すると供給元 `unknown` を返し、更新し直しで直る。参照が無いままなら同期は認証なしのまま —— 書く前と同じ状態）。

### 独立監査の指摘と是正（2026-10-06。PR #1759 の head `deb191f6` に対する監査は GO・指摘 6 件）

着手時に `origin/develop` を merge した（rebase ではない。衝突なし）。指摘ごとに、是正の前に赤・後に緑になる試験を足した（是正を一時的に戻して赤を確かめた。下の変異 M25〜M31）。

| 指摘 | 是正 | 試験 |
| --- | --- | --- |
| 🟡1 一覧で、保管先（Vault）が `set` なのに成員の設定が値を持たない（参照の配置が失敗・並行する更新に負けた）とき「画面」と出る —— コネクタはその値を読まない | `SupplySourceOf(supplies, vaultHasValue)`: 保管先に値あり かつ `Unset` →「確認できない」（`unknown`）。書き込みの応答は「保管先に値あり」として同じ規則で判定する | BFF `Vault_set_but_member_without_a_value_is_reported_as_unknown`（陽性対照: 保管先が空なら `screen`）・`Supply_source_never_folds_unknown_into_screen_or_not_screen` |
| 🟡2 後段の未知の符号を「画面以外」へ倒していた（ADR-0126 決定 4「不明を 2 値へ寄せない」と食い違う） | ポートに `SecretItemGroupSupply.Unknown` を足し、knowledge の `SupplyOf` は `other` だけを `OtherSource`、未知を `Unknown` へ写す。BFF の優先は `git` → `unknown` → `screen`。画面は既に `unknown` を「確認できない」（`StatusBadge` の色＋印＋語）で描くので、表の注記に意味と次の手を 1 文足した（ja / en） | BFF `Unknown_supply_codes_are_reported_as_unknown_not_folded_into_two_values`・`Unknown_reference_result_code_is_reported_as_unknown`、Vitest `shows unknown supply as cannot-confirm without folding it into screen or not-screen` |
| 🟡3 参照の配置が `Config` 全体を作り直して並行性の制御なしに保存する —— 別のキーへの配置・SC-06 の更新と重なると参照・平文を消し、しかも `reference` と報告する | `CredentialReferencePlacement.PlaceAsync`: 実 DB では `jsonb_set` を**そのキーにだけ・キーが無い／空白だけのときだけ**当てる 1 文の条件つき更新にし、**置いた後の状態を読み直して**返す。移行も並行性の印（xmin）も足さない（印は `Config` に触れない同期の記録まで競合で落とすため）。InMemory は従来の実体の更新 | 統合（実 PostgreSQL）`CredentialReferencePlacementTests.Concurrent_placements_for_different_keys_both_survive`・`Placement_never_overwrites_a_value_written_after_it_read_the_row` |
| 🟡4 成員 ID の型 `^…$` が末尾の改行を通す（.NET の `$` は末尾の `\n` の前でも一致） | 終端を `\z` に。`SecretItemCatalog` の型 5 つ（1 セグメント・パス・プロパティ・Kubernetes の名前 2 つ）も同じく | BFF `Member_id_pattern_admits_only_one_lowercase_segment`（`"a\n"`・`"<GUID>\n"`）、`SecretItemCatalogTests.Invalid_group_declarations_fail_closed`（群名・接頭辞の末尾改行）・`Wildcard_or_traversal_paths_fail_closed`（パスの末尾改行） |
| 🟢1 ポートが HTTP・JSON・時間切れ以外の例外を外へ出す —— Vault へ書けた後なら BFF は 500 を返し、画面は「値は保存されていません」と出す（偽） | 捕まえる条件を「呼び出し元の取り消し以外のすべて」へ広げた（ログは例外の型名だけ）。配置の失敗は null → 200・`unknown`・配置の監査行 `failed`、一覧の失敗は 502 | BFF `Unexpected_exception_after_the_vault_write_keeps_the_write_and_reports_unknown`・`Unexpected_exception_while_listing_members_returns_502` |
| 🟢2 書き込みの変更（mutation）が送った値（変数）を TanStack Query の変更キャッシュに既定の 5 分残す | 群・静的な項目の両方の変更に `gcTime: 0` | Vitest `secretMutationCache.test.tsx`（2 件。検査用の QueryClient は変更の gcTime を上書きしない） |

#### 規則 10（この是正で誤りになる記述の引き直し）

`git grep -n -E "未知の符号|画面以外」へ倒|値なし → .screen|値なし → 画面|\{0,63\}\$" -- docs .ai-context/adr src ':!src/ai-stock-trading'` で引いた。

| 箇所 | 扱い |
| --- | --- |
| IADR-0501 決定 2（成員 ID の型）・決定 3（配置）・決定 4（供給元・未知の符号）・決定 5（画面）・§結果 | 本 PR の新規 IADR なので本文を改めた |
| IADR-0460 の 2026-10-06 追記（群の供給元） | 本 PR の追記なので「判定できない」の中身を足した |
| `docs/screens/SC-22_secret-item-management.md` 群の節・計画との対応表 | 改めた |
| `docs/tests/SC-22_secret-item-management.md` T-85 | 「未知の符号 → `git`」を `unknown` へ改め、T-100〜T-107 を足した |
| `docs/api/openapi.yaml` の `SecretItemGroupMemberStatusDto.supplySource` の説明・`SecretItemDto.cs` の同 DTO のコメント（`git grep -n "群の参照を持つ"` で追加に引いた） | `unknown` の中身と「保管先も空」を足した。orval の生成物（`bff.schemas.ts` のコメント）を再生成した |
| 本書 §設計（S2・S3）の「供給元」の項（`absent` は「画面」・`other` 以外は「画面」） | 🔴 **この節の時点の記録として残す**（本節が改める） |

#### 変異試験（1 つずつ当てて戻した。スクリプト `scratchpad/mut458.sh`）

| # | 変異 | 結果 |
| --- | --- | --- |
| M25 | 保管先に値あり かつ `Unset` の分岐を外す（🟡1） | 殺した（2） |
| M26 | 未知の符号を `OtherSource` へ戻す（🟡2） | 殺した（2） |
| M27 | 配置を従来の `Config` 全体の保存へ戻す（実 DB でも実体の更新）（🟡3） | 殺した（2。統合） |
| M28 | 成員 ID の型の終端を `$` へ戻す（🟡4） | 殺した（2） |
| M29 | 1 セグメントの型の終端を `$` へ戻す（🟡4） | 殺した（2） |
| M30 | パスの型の終端を `$` へ戻す（🟡4） | 殺した（1） |
| M31 | 捕まえる例外を HTTP・時間切れ・JSON に戻す（🟢1） | 殺した（2） |
| M32 | 変更の `gcTime: 0` を外す（🟢2） | 殺した（2） |
| M33 | 画面: 「確認できない」を「画面以外」へ寄せ、注記の 1 文を落とす（🟡2 の表示） | 殺した（1） |

#### 検証（独立監査の是正）

- `dotnet build`（platform・knowledge）→ エラー 0（警告は既存の `IngestToSearchQdrantTests.cs` の CS0618 だけ）。`dotnet format --verify-no-changes`（両ユニット）→ 差分なし
- `dotnet test`: `Platform.Bff.Tests` 865 成功＋skip 1、`DataSourceService.Tests` 464 成功、`CredentialReferencePlacementTests` 2 成功（実 PostgreSQL のコンテナ）
- `src/`: `lint`（エラー 0）・`typecheck`・`format:check`・`codegen` の再生成差分なし・`i18n` の再生成差分なし・Vitest（`sc22-secrets`・`sc06-datasources`）成功・
  `build` → `check-chunk-budget --require` が +0.55 kB 超過 → `--update`（`$comment_initialTotalBytes_20261006_458_audit-fixes`）
- 文書・規約の検査器は PR 本文に記す

#### 残余リスク（是正後）

- 🔴 **SC-06 の更新（`PATCH` / `PUT /datasources/{id}`）は従来どおり `Config` 全体を並行性の印なしで書く**（本段の射程外・既存の挙動）。参照の配置が SC-06 の更新の
  「読んでから書くまで」の間に入ると、SC-06 の更新が古い辞書で参照を消し得る。消えた場合、一覧は保管先に値があるのに設定が値を持たない状態を「確認できない」と出す（🟡1 の是正）ので、黙って「画面」とは出ない。
- 「確認できない」の判定（🟡1）は成員単位である（KV v2 の metadata はキーごとの有無を持たない）。資格情報のキーを 2 つ以上宣言するコネクタが入ると、片方だけ書いた途中の状態も「確認できない」と出る（いまのコネクタは 1 つ）。
- 並行性の試験（T-104・T-105）は統合の分類で、PR の CI では走らない（push と日次の統合試験で走る。IADR-0232 決定 3）。

### 段 S4 に要るもの

- 既存の行の秘密キー（`Config` の `apiToken` / `password`）を `datasource/<ID>` へ移し、正規の参照（`ConnectorSecretReference.CanonicalFor`）へ置き換える一回きりのジョブ
  （書き手は運用者のトークン。BFF 経由ではない。冪等）。本段の `PlaceCredentialReference` は値なしのキーにしか置かないので、移送は別の経路で `other` → `reference` へ移す。
- 移送期間フラグと readiness 検査（平文の行の件数＝`CredentialSupplyOf` が `other` を返すキーのうち `vault:` で始まらないものが 0 件）。§窓 1 の表 3 列を `[Fact]` で実測する。
- 移送の後、`Create` / `Update` / `Patch` で秘密キーへの平文の書き込みを 400 で拒む（`vault:` の正規の参照と `***` の書き戻しだけを通す）。
- 文書（S5）: `docs/security/security.md` の「暫定」節の恒久化、`docs/functional/FR-01_data-source-catalog.md`、runbook の版での戻しの手順。
