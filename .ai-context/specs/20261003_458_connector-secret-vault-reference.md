---
title: 作業仕様書 — コネクタ資格情報を「Vault 移行までの暫定マスク」から Vault 参照へ移す（設計段・#458 残射程 a）
type: spec
status: draft
related_ids: [FR-01, UC-04, SC-06, SC-22, NFR-18, ADR-0005, ADR-0042, ADR-0095, ADR-0104, ADR-0110, ADR-0124, IADR-0051, IADR-0053, IADR-0054, IADR-0055, IADR-0295, IADR-0403, IADR-0433, IADR-0453, IADR-0456, IADR-0485, IADR-0493]
author: claude
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-18
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-04
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-06 / §SC-22
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md §認証・秘匿情報
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 実測 6・決定 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0104_sc22-item-kinds-and-dual-path-for-env-ids.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0110_sc22-supplier-three-values-no-public-key-restart-confirmed-at-write.md 決定 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1
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
