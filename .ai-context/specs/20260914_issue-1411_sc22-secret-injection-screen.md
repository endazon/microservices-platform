---
title: "SC-22 秘密情報・接続設定の管理を画面から Vault まで通し、項目ごとの書き込み権限と BFF 専用 ServiceAccount を同時に配備する（#1411。計画 ADR-0095）"
type: spec
status: in-progress
related_ids: [SC-22, FR-05, NFR-11, NFR-18, ADR-0032, ADR-0040, ADR-0042, ADR-0095, IADR-0009, IADR-0030, IADR-0035, IADR-0096, IADR-0124, IADR-0125, IADR-0433, IADR-0453]
author: claude
created: 2026-09-14
updated: 2026-09-14
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
  - planning:projects/microservices-platform/07_adr/ADR-0042_ops-management-ui-production.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 仕様書: SC-22 秘密情報・接続設定の管理（画面 → BFF → Vault と、権限の同時配備）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。
>
> 🔴 **本 PR は画面・端点・Vault の書き込み権限・BFF 専用 ServiceAccount を 1 本で出す。**
> 計画 ADR-0095 §統制と現在の実現手段 が「決定 3 を実装する時点で同時に配備しなければ、
> 射程の無い書き込み権限が先に生まれる」と警告しているためである（IADR-0433 決定 7 の末尾も同じ）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-05`（管理・権限）
- 非機能要件（NFR）: `NFR-18`（シークレット管理）・`NFR-11`（全経路 HTTPS）
- ユースケース（UC）: なし（運用・保守要求）
- 画面（SC）: `SC-22`（秘密情報・接続設定の管理。起案 2026-09-11。**モックアップ未作成**）
- 関連 ADR: `ADR-0095` 決定 1〜4／`ADR-0042` 決定 2（公開範囲は運用者・システム管理者）／`ADR-0040` 決定 3（射程は Headlamp）／`ADR-0032`（BFF セッション）
- 関連 IADR: `IADR-0433`（本作業が実装する設計）・**`IADR-0453`（本作業で起こす。IADR-0433 が「モックアップ受領後」に残した決定）**・`IADR-0096`（Vault ＋ ESO・k8s auth）・`IADR-0009` / `IADR-0035`（存在秘匿・RequireRole）・`IADR-0030`（運用者ロール）・`IADR-0124` / `IADR-0125`（ルート契約・i18n）
- 計画書リンク: 隣接クローン `project-planning` の `origin/main`（2026-09-14 に `git fetch` して読んだ）
  - `projects/microservices-platform/05_screens/01_screens.md` §SC-22（967 行付近）
  - `projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md`
  - `projects/microservices-platform/07_adr/ADR-0042_ops-management-ui-production.md` 決定 2
- 起票: #1411（環流 planning#599 の裁定）

## 目的・背景

計画 ADR-0095 は「Git に置けない秘密情報だけを製品の画面から投入する」と裁定し、SC-22 を新設した。
前段 PR（#1416）は **退避手段（コンソール Runbook）と権限の形（IADR-0433）と allowlist ファイル**だけを置き、
画面・端点・権限の実配備は「画面と同時に」として残した。**本 PR はその残りを 1 本で出す。**

**ADR-0095 は「モックアップの受領を着手の条件にしない」と明記している**（着手可否の注記）。
したがって画面の要件は SC-22 の記述と ADR-0095 で定まる。見た目は既存の共通シェルと `@platform/ui` に従う。

## 着手前の実測（`origin/develop` `d59aa987`）

| 箇所 | 現状 |
| --- | --- |
| 画面 `/admin/secrets` | **無い**（`git grep "admin/secrets"` 0 件） |
| 端点 `/bff/secrets` | **無い**（IADR-0433 と前段仕様書の素描だけ） |
| Vault policy（BFF 用） | **無い**。`policy-eso-read.hcl`（read/list のみ）と role `eso` だけ |
| BFF の ServiceAccount | 🔴 **`serviceAccountName` が `deploy/helm/` に 0 件** —— 名前空間の `default` で動いている |
| allowlist | `deploy/bootstrap/sc22-secret-items.json` の `items[]` が **4 KV・13 プロパティ**（`node -e` で数えた） |
| BFF の Vault 関連構成 | **無い**（`git grep "Vault__\|Vault:Address"` 0 件） |
| ロール定義 | `platform-admin` / `platform-operator`。ポリシーは `AdminOnly` / `ConfigViewer`（admin ＋ operator） ほか |
| 監査 | `IAuditLogger.Record(action, subject, outcome, detail)`（`Audit=true` の構造化ログ） |
| BFF の Redis | セッション（`IDistributedCache` ＝ StackExchangeRedis）で既に使っている |
| テスト仕様書の対応 | `check-test-traceability.js` は **テストが参照する SC に `docs/tests/` の仕様書を要求する**（無いと「実装先行」で fail） |

## 対象範囲

- 対象:
  1. **BFF**: allowlist ローダ（fail-closed）・Vault KV v2 クライアント（k8s auth ログイン／metadata 読み取り／`merge-patch` 書き込み）・書き込み記録の保管（最終更新者）・端点 `GET /bff/secrets` / `PUT /bff/secrets/{item}`・認可ポリシー・監査
  2. **契約**: `docs/api/openapi.yaml` へ 2 口と DTO を宣言し、orval で再生成する
  3. **SPA**: `src/knowledge/frontend/src/features/sc22-secrets/`（ルート・左ナビ「運用」・パンくず・画面・i18n ja/en）と E2E スモーク
  4. **配備**: helm に BFF 専用 ServiceAccount `bff`・`serviceAccountName`・Vault 接続の env・NetworkPolicy の egress、`values-local.yaml` の接続先、Dockerfile への allowlist 同梱
  5. **Vault**: `deploy/local/vault/eso/policy-bff-secret-write.hcl`（完全一致パス・ワイルドカードなし）と `bootstrap.sh` の policy / role `bff-secret-writer`
  6. **検査**: HCL の path 集合が `items[]` と一致しワイルドカードを含まないことを xUnit で固定する
  7. **文書**: IADR-0453（新規）・画面仕様書 `docs/screens/SC-22_*`・テスト仕様書 `docs/tests/SC-22_*`・`BFF_bff-surface.md`・運用 Runbook と `operations.md`・`deploy/local/vault/eso/README.md`・`security.md` の監査表
- 対象外:
  - 🔴 **`deferred[]` / `excluded[]` を allowlist へ入れること**（IADR-0433 決定 3。本 PR は `items[]` を 1 行も動かさない）
  - 🔴 **値を読み出す口**（`GET /bff/secrets/{item}` は作らない。IADR-0433 決定 7）
  - `policy-eso-read.hcl` の変更（IADR-0433 決定 5）
  - 稼働クラスタ・稼働 Vault への操作（本 PR は 1 度も触らない）
  - 退避手段の使用記録の設計（ADR-0095 フォローアップ 4。計画の射程）

## 設計

### 1. IADR-0433 が先送りした決定（IADR-0453 に記録する）

| # | 論点 | 決定 | 根拠 |
| --- | --- | --- | --- |
| 1 | 運用者を含めるか | **含める**。新ポリシー `SecretItemWriter` ＝ `platform-admin` または `platform-operator` | SC-22「運用者・システム管理者ロール限定」・ADR-0042 決定 2。**`ConfigViewer` は流用しない**（閲覧の名前に書き込みを相乗りさせると、SC-11 の公開範囲を変えたときに書き込みまで動く） |
| 2 | UI の粒度 | **KV 項目 1 行・更新はプロパティ 1 つずつ**。1 回の `PUT` が 1 プロパティ | SC-22「1 項目ずつ更新する」。`keycloak-smtp` / `ast-app-secrets` は 1 KV に書ける値が複数あり、KV 単位の一括入力は「一括再投入」と同じ形になる（ADR-0095 決定 4） |
| 3 | 最終更新者の出所 | **BFF が書いた版の記録を Redis に置き、Vault の現在版と一致するときだけ表示する。** 一致しなければ「記録なし」 | Vault KV v2 metadata は更新者を持たず、`custom_metadata` は metadata の書き込み権限を要する（IADR-0433 決定 1 を広げる）。**Vault 権限は広げない。** 版で突き合わせるので、コンソールや bootstrap が書いた版に画面の利用者名が付くことは無い |
| 4 | 未設定と「読み出せない」 | metadata **404 → 未設定**／**200 かつ現在版が削除・破棄されていない → 設定済み**（現在版が削除・破棄 → 未設定）／**403・5xx・不達・解釈不能 → 取得できない** | 🔴 **プロパティ単位の空欄は判定できない**（data の read を持たないため）。bootstrap が空文字で seed した KV は「設定済み」と出る。画面に注記し、IADR-0453 のフォローアップへ残す |
| 5 | Vault 未配備時 | **`Vault:Address` 未設定 → 両口とも 503**（problem の `type` で区別）。ログイン不達・拒否 → 503 | 静かに成功させない。一覧も空で返さない（「項目が無い」と「保管先に届かない」を混同させない） |
| 6 | `deferred[]` | **対象外のまま** | IADR-0433 決定 3。本 PR は allowlist を広げない |
| 7 | 確認の形 | **確認入力（2 度目の入力）を必須にし、確認ダイアログは置かない** | SC-22 の入力規則。KV v2 は旧版を保持するので書き直しで戻せる。2 度入力がそのまま確認になる |
| 8 | 監査の `outcome` | `granted` / `denied` に加え、**Vault 側の失敗を `failed`** とする | IADR-0433 決定 6 は 2 値。書き込みを試みたが保管先が受け付けなかった事実は、許可・拒否のどちらでもない |

### 2. BFF

- **allowlist ローダ**（`Foundation/Secrets/SecretItemCatalog.cs`）: 起動時に 1 度だけ読む。
  🔴 **読めない・JSON が壊れている・`items[]` が空・`properties` と `notWritable` が交差する・パスに `*` や `..` がある → 例外で起動しない**。
  `deferred[]` / `excluded[]` は型に持たない（読まない）。既定の置き場は出力ディレクトリ（csproj の `Content` リンクで同梱）、`SecretItems:CatalogPath` で上書きできる。
- **Vault クライアント**（`VaultKvClient`）:
  - ログイン: `POST /v1/auth/{mount}/login`（`role` と Pod の SA トークン）。トークンはリース期限の手前まで保持し、403 のとき 1 度だけ取り直す。
  - 状態: `GET /v1/{mount}/metadata/{path}`。
  - 書き込み: 🔴 **`PATCH /v1/{mount}/data/{path}`、`Content-Type: application/merge-patch+json`、本文 `{"data":{"<property>":"<value>"}}`**。KV が無い（404）ときだけ **`POST` ＋ `options.cas=0`**（「存在しないときだけ作る」。既存の KV を置き換えない）で作る。
  - 🔴 **値をログ・例外メッセージ・監査・応答へ出さない。**
- **書き込み記録**（`SecretWriteRecordStore`）: `IDistributedCache` に `bff:sc22:write-record:<item>` ＝ `{version, updatedBy, property, updatedAt}`。読み書きの失敗は握って「記録なし」へ倒す（書き込み自体は既に成立しているため）。
- **端点**（`SecretItemBffEndpoints.cs`）:
  - 群 `/bff/secrets` に `RequireAuthorization()`（未認証 401）。**ロールはハンドラ内で `SecretItemWriter` を評価**し、拒否を監査してから 403 を返す（`RequireAuthorization(policy)` だと拒否が監査に残らない）。
  - `GET /bff/secrets` → `SecretItemStatusDto[]`（項目名・Vault パス・書けるプロパティ・状態・現在版・最終更新日時・最終更新者）。値の列は無い。
  - `PUT /bff/secrets/{item}` 本文 `{property, value, reason?}` → `SecretItemWriteResultDto`（項目・プロパティ・版・更新日時）。
    allowlist 外の項目 → **400**（404 にしない）／書けないプロパティ（`notWritable` を含む）→ 400／空の値・8192 文字超 → 400／理由 500 文字超 → 400。
  - 監査: `secret.item.list` / `secret.item.update`。`detail` は `item=… property=… version=… reason=…`（**値・長さ・ハッシュを入れない**）。拒否理由 `not-in-allowlist` / `property-not-writable` / `invalid-value` / `invalid-reason` / `forbidden`、失敗理由 `vault-not-configured` / `vault-unavailable` / `vault-rejected`。

### 3. SPA（`/admin/secrets`）

- ルート: `RequireRole anyOf=[admin, operator]`（権限外は NotFound。画面チャンクも取らない）。左ナビ「運用」グループ・パンくず `ホーム / 運用 / 秘密情報・接続設定の管理`。
- 一覧: 列は **項目名／用途／最終更新日時／最終更新者／操作** の 5 つ。🔴 **値の列を置かない。**
  最終更新日時の欄で **「未設定」「取得できない」を状態バッジ（色 ＋ アイコン ＋ テキスト）で描き分ける**。最終更新者が無い版は「記録なし」。
- 更新フォーム: 行の「更新」で開く。プロパティの選択（書けるものだけ）・新しい値（マスク）・確認入力（マスク）・更新の理由（任意）。
  **確認が一致しない／値が空なら送信ボタンを無効にし、理由を出す。** 成功したら入力を消し、版を表示する。
  🔴 **一括再投入のボタンを置かない。**
- 用途と表示名は **画面側の語彙**（Lingui のカタログ）に持つ。allowlist の `why` は採用理由の記録であって利用者向けの用途文ではないため描かない。未知の項目は識別子をそのまま出す。
- 失敗: 一覧の 503 は「保管先（Vault）に接続できません」を出す（空の一覧にしない）。

### 4. 配備

- helm: `services.bff.serviceAccount`（`create: true` / `name: bff`）→ `templates/serviceaccount.yaml` と `serviceAccountName`。
  `services.bff.vault`（`address` 既定は空＝503、`role: bff-secret-writer`、`authMount: kubernetes`、`namespace: platform-infra`、`port: 8200`）→ `Vault__*` env。
  NetworkPolicy 有効かつ `address` 非空のとき、BFF から Vault 名前空間の 8200 への egress を 1 本だけ開ける。
- `values-local.yaml`: `address: http://vault.platform-infra.svc.cluster.local:8200`（`VAULT=1` でなければ不達＝503）。
- Dockerfile: allowlist を `/deploy/bootstrap/` へ COPY（csproj の `Content` リンクがそこを引く）。
- Vault: `policy-bff-secret-write.hcl`（4 項目 × 2 path。data に `create`/`update`/`patch`、metadata に `read`。ワイルドカードなし）、
  `bootstrap.sh` に `vault policy write bff-secret-write` と `auth/kubernetes/role/bff-secret-writer`（`bound_service_account_names=bff` / `bound_service_account_namespaces=microservices-platform` / `ttl=1h`）。

## 走査した母集合

**記憶で挙げず、文字列で走査してから列挙した**（`.claude/rules/traceability.repo.md` 規則 9）。

| 走査 | コマンド | 結果 |
| --- | --- | --- |
| allowlist を引く箇所 | `git grep -n "sc22-secret-items" -- . ':!src/ai-stock-trading'` | IADR-0433・前段仕様書・`operations.md`・Runbook の 4 ファイル。**コードからの参照は 0 件**（本 PR が初めて読む） |
| 既存の `/bff/secrets` | `git grep -n "bff/secrets"` | IADR-0433 と前段仕様書の素描だけ。実装・契約は 0 件 |
| BFF の SA | `git grep -n "serviceAccountName" -- deploy/helm` | **0 件**（`default` で動いている） |
| Vault の構成キー | `git grep -n "Vault__\|\"Vault\"\|Vault:Address" -- src deploy` | 0 件 |
| SC-22 の言及 | `git grep -ln "SC-22"` | IADR-0433・前段仕様書・allowlist・`operations.md`・Runbook・`plan-id-range-history-annex.md`・`check-test-traceability.js`・`scripts.repo.test.js`。**最後の 3 つはレンジの記録で、本 PR の追随対象ではない** |
| ルートの網羅 | `router.test.ts` の `PLANNED_ROUTES` と `check-route-manifest.js` | 画面 feature を足すと **マニフェストと E2E スモークの両方が要る**（判定 1・3） |
| テスト仕様書の要求 | `check-test-traceability.js` 冒頭 | テストが SC を参照するなら `docs/tests/` の仕様書が要る |
| 認可の契約検査 | `check-bff-authz-docs.js` 冒頭 | `x-roles` と実効ロールを突合する。**ハンドラ内の `AuthorizeAsync` は同一ファイルの private ヘルパを 1 段だけ辿る** —— 評価は端点から 1 段のヘルパに置く |
| 契約と DTO | `check-openapi-dto-drift.js` 冒頭 | `components.schemas` と同名 record のプロパティ集合を突合する —— DTO は `Platform.Shared.Contracts/Dtos` に置き、OpenAPI と同名にする |
| IADR の最大番号 | `ls .ai-context/adr/ \| grep -oE "^IADR-[0-9]{4}" \| sort \| tail -1` | `IADR-0452`。**本作業は `IADR-0453` を使う**（事前に割り当て済み） |

**除外した理由**:

- `src/ai-stock-trading`（submodule）は走査しない。別リポジトリであり、本作業は AST の構成に触れない（allowlist の `ast-app-secrets` は既存の記載をそのまま使う）。
- `.claude/worktrees/` は同一ファイルの複製なので除外した。
- `deferred[]` / `excluded[]` の各パスは**意図的に読まない**（IADR-0433 決定 3）。

## 受け入れ基準

SC-22 と IADR-0433 から写す（`[x]` は検証で確かめたもの）。

- [ ] **AC-01** 画面 `/admin/secrets` があり、一覧の列が 項目名／用途／最終更新日時／最終更新者／操作 で、**値の列が無い**
- [ ] **AC-02** 更新は **1 回に 1 項目（1 プロパティ）**。**一括再投入のボタンが無い**
- [ ] **AC-03** 未設定の項目を「未設定」として出し、**「取得できない」と区別できる**（色だけに頼らない）
- [ ] **AC-04** 値の入力はマスクされ、**確認入力が一致しないと送信できない**
- [ ] **AC-05** 更新の理由は任意で、監査ログへ残る。**値は監査・ログ・応答のどこにも残らない**
- [ ] **AC-06** 到達できるのは **運用者・システム管理者だけ**。権限外にはメニュー・画面を出さない（NotFound）。BFF は未認証 401・権限外 403
- [ ] **AC-07** 左ナビ「運用」グループに項目がある
- [ ] **AC-08** allowlist 外の項目は **400**（404 ではない）。`notWritable` のプロパティも 400
- [ ] **AC-09** BFF は allowlist を読めないと**起動しない**。`items[]` だけを読み、`properties` と `notWritable` の交差を拒む
- [ ] **AC-10** 書き込みは **KV v2 の PATCH（`application/merge-patch+json`）**。KV が無いときだけ `cas=0` で作る。`put` の全置換をしない
- [ ] **AC-11** Vault が未配備（接続先未設定・不達）なら **503** で失敗を見せる（静かに成功しない・空の一覧にしない）
- [ ] **AC-12** **値を読み出す口が無い**（契約に `GET /bff/secrets/{item}` が無い）
- [ ] **AC-13** Vault policy の path 集合が `items[]` と**完全一致**し、ワイルドカード・`list`・`delete`・`destroy`・data の `read` を含まない（機械検査）
- [ ] **AC-14** BFF 専用 ServiceAccount `bff` が helm で作られ、BFF の Deployment が使う。k8s auth の role はそれに束縛され、`default` に束縛しない
- [ ] **AC-15** `policy-eso-read.hcl` は変更しない
- [ ] **AC-16** 監査は `secret.item.update` を `granted` / `denied` / `failed` で残し、`detail` は項目・プロパティ・版（と理由）だけ
- [ ] **AC-17** i18n の未翻訳キーが無い（ja / en）
- [ ] **AC-18** IADR-0453 が 1〜8 の決定を記録し、索引に登録されている

## テスト方針

| 層 | テスト | 対応 |
| --- | --- | --- |
| xUnit（ローダ） | `items[]` だけを読む／`notWritable` との交差・空の `items`・ワイルドカード・壊れた JSON・存在しないファイルで例外 | AC-09 |
| xUnit（Vault クライアント） | PATCH の方式・ヘッダ・本文の形／404 のとき `cas=0` の POST／ログインとトークンの再取得／metadata の 3 状態 | AC-10・AC-03 |
| xUnit（端点） | admin・operator は 200、他ロール 403 ＋ 監査 denied、未認証 401／allowlist 外 400（404 でない）／`notWritable` 400／値が監査・応答に出ない／Vault 未構成 503・不達 503／起動時に allowlist が読めないと例外 | AC-05・06・08・09・11・16 |
| xUnit（policy） | HCL の path 集合 ＝ `items[]` の 2 倍、capability が決められた集合、ワイルドカード無し | AC-13 |
| Vitest（画面） | 列に値が無い／確認不一致で送信不可／未設定と取得できないの描き分け／権限外は NotFound かつ一覧を呼ばない／一括の口が無い／送信本文に理由が載る | AC-01〜04・06・07 |
| E2E（Playwright） | 管理者で画面とナビに届く／権限外は NotFound で端点を呼ばない | AC-06・07 |
| 検査器 | `check-trace-blocks` ほか（下記） | 文書 |

変異試験（陽性対照の検出力の確認）は、主要な否定形（値の列が無い・一括の口が無い・値が監査に出ない）について実装を一時的に壊して落ちることを確かめる。

## 計画書との差異

- 差異: **あり（計画の要求の一部を実装だけでは満たしきれない）**。
  1. 🔴 **SC-22 主要素 3「値が入っていない項目を『未設定』として出す」は KV 単位でしか満たせない。**
     プロパティ単位の空欄は data の `read` が無いと判定できず、`read` を与えないことは IADR-0433 決定 1 の統制である。
     bootstrap が空文字で seed した KV は「設定済み」と出る。**画面に注記し、IADR-0453 のフォローアップへ問いを残す**（本 PR では planning へ起票しない）。
  2. **最終更新者は画面から書いた版にだけ付く。** コンソール・bootstrap が書いた版は「記録なし」。
     記録の置き場（Redis）が消えた場合も「記録なし」へ倒れる。**監査ログが正本であることは変わらない。**

## 未決事項

- プロパティ単位の未設定判定を計画としてどう扱うか（上記差異 1）。
- `deferred[]` の 20 件を画面で扱うか（IADR-0433 フォローアップ 4 のまま）。
- 退避手段の使用記録の設計（ADR-0095 フォローアップ 4）。
