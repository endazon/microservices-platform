---
title: 作業仕様書 — SC-12 の無効化・再有効化を Keycloak のクライアントの enabled へ写し、照合が有効・無効の食い違いも検知する（IADR-0516 決定 4a。#1829）
type: spec
status: done
related_ids: [FR-16, FR-09, UC-09, SC-12, ADR-0123, ADR-0006, ADR-0088, IADR-0516, IADR-0481, IADR-0329]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 1・2（属性の正は IdP。登録簿は写し・食い違いは検知して知らせる）
issue: "#1829"
---

# 作業仕様書 — SC-12 の無効化の IdP への写し（決定 4a。#1829）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0516 への日付つき追記**に置く（決定 4a が「写す」ことを決めており、本段で決めるのは順序・失敗時の扱い・照合の比べ方という細部だけである。新しい IADR は起こさない）。
> 計画は project-planning の隣接クローン（読み取り専用）で読んだ。基点は MSP `origin/develop` `60bca28a`（段 3 の PR #1831 のマージ）。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 稼働の Keycloak での実測は CI の integration-stack（使い捨ての k3d クラスタ）の門に足す。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0123 決定 1**（属性の正は IdP）・**決定 2**（登録簿は写し。食い違いは検知して知らせる）。ADR-0006（警報は Alertmanager）。
- 機能要求: **FR-16**・**FR-09**。画面: **SC-12**（無効化は「即時に接続拒否」）。UC: **UC-09**。
- 起点 issue: **#1829**（#1786 の一部。#1817 から分離）。段 1 は PR #1816、段 2 は PR #1827、段 3 は PR #1831。
- 実装 ADR: **IADR-0516 決定 4a**（多層の防御として無効化を IdP のクライアントの `enabled` へ写す）・決定 4（IdP を先に書く・補償・入口の印）・決定 5（照合）。

## 受け入れ基準（issue #1829）→ 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | 無人のクライアントを無効化すると Keycloak のクライアントの `enabled` が false、再び有効化すると true | `KeycloakServiceAccountProvisionerTests`（偽の Keycloak の `PUT /clients/{id}`）・`IdpProvisioningEndpointTests`（API 面・プロセス内の口）・`--live` M8 |
| AC2 | 写す対象は入口の印（`managed-by`）を持つクライアントだけ。プラットフォームのクライアント（例 `abac-seeder`）は無効化経路から変えられない（否定形） | `KeycloakServiceAccountProvisionerTests`（印の無いクライアントへ `PUT` を送らない）・`IdpProvisioningEndpointTests`（無効化しても印の無いクライアントは有効のまま・再有効化は 400）・`--live` M8（`abac-seeder` の `enabled` が変わらない） |
| AC3 | IdP への書き込みが失敗しても登録簿の無効化は取り消さない。食い違いは #1818 の照合で検知できる形で残す | `IdpProvisioningEndpointTests`（失敗する口で 200・登録簿は無効）・`IdpReconciliationTests`（`enabled_differs`） |
| AC4 | 稼働 Keycloak での実測: 無効化後に `client_credentials` のトークン発行が拒否される（再有効化で戻る） | `check-mcp-client-provisioning.js --live` M8（`--self-test` が判定器を固定） |
| AC5 | IADR-0516 に日付つきの追記で実装の記録と #1817 から分離した経緯 | IADR-0516 ［2026-10-09 追記 / #1829］ |

## 現状（実測。`60bca28a`）

| 事実 | 確かめ方 |
| --- | --- |
| 無効化・再有効化は `McpClientEndpoints.SetEnabledAsync` の 1 つで、登録簿の `Enabled` を書くだけ（IdP に触れない） | `Features/McpClients/McpClientEndpoints.cs` |
| 即時の接続拒否は `McpSubjectResolver` が呼び出しごとに登録簿を引いて満たしている | `Infrastructure/Persistence/McpSubjectResolver.cs` |
| 書き込み口 `IServiceAccountProvisioner` は Create / ReplaceAttributes / Undo。Keycloak 版は入口の印の確かめ（`IsManaged`）とクライアントの完全一致の照会（`FindClientInternalIdAsync`）を持つ | `KeycloakServiceAccountProvisioner.cs` |
| 照合は `enabled` を比べない（決定 4a が未実装で、比べると無効化した行がすべて食い違いになるため） | `IdpReconciliationCheck.cs` L21・IADR-0516 #1818 追記 |
| 試験の器は `in-memory` の口（`InMemoryServiceAccountProvisioner.IsEnabled`）を持つ | `TestWebApplicationFactory.cs` |
| Keycloak の `PUT /clients/{id}` は null の項目を変えない（`RepresentationToModel.updateClient` が項目ごとに null を見る）。表現を丸ごと送り返すと、読んでから書くまでの間に回された secret を古い値で戻し得る | Keycloak 24 の管理 API の実装。稼働での確かめは M8（再有効化の後にトークンが出る・テンプレートの項目が残る） |

## 母集合（規則 9・10）

### 規則 9 — 誤りの側の文字列で全文書を走査した

`git grep -n -e '決定 4a' -e '有効・無効' -e 'enabled` も比べない' -e '無効化を認可サーバー' -e '認可サーバーのクライアントへ写す' -e '#1829' -- ':!.ai-context/specs' ':!CHANGELOG.md'`:

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `docs/screens/SC-12_mcp-client-management.md` §認可サーバーへの書き込み（「多層の防御は…後続で入れる」・「有人の行と有効・無効は比べない」）・§未決事項 2（「無効化を認可サーバーのクライアントへ写すか」） | **直す** | 本段で入る。未決 2 のうち無効化の写しは IADR-0516 決定 4a が決めた |
| `docs/api/FR-16_mcp-server.md`（無効化・再有効化の行に IdP の扱いが無い） | **足す** | 状態コードの写し方が変わる（再有効化の 400 / 502 / 503） |
| `docs/tests/FR-16_mcp-server.md` §未実施・残件「無効化を…写す多層の防御は未実装（照合も有効・無効を比べない）」 | **直す**・C-50〜を足す | 本段で入る |
| `docs/operations/operations.md` §MCP クライアント登録簿と認証基盤の照合「有効・無効も比べない」・種類の表 | **直す**（`enabled_differs` を足す） | 照合の種類が増える |
| `IdpReconciliationCheck.cs` L21（「`enabled` も比べない」） | **直す** | 本段で比べる |
| `.ai-context/adr/IADR-0516` 決定 4a「実装は後続の段（#1817）」・#1818 追記「`enabled` の写しと比較は決定 4a とともに後続」 | **日付つき追記だけ**（本文は書き換えない） | 凍結記録 |
| `.ai-context/specs/20261008_1786_*`・`20261009_1817_*`・`20261009_1818_*` | **直さない** | 確定済みの作業仕様書 |
| `docs/tests/SC-12_mcp-client-management.md` T-02（画面の操作の送り分け） | **据え置く** | 画面は変えない（応答の状態コードの写し方は画面の既存の失敗表示で足りる） |

### 規則 10 — この変更で新たに誤りになる自分の記述

- 照合の種類の数（5 → 6）: IADR-0516 #1818 追記「食い違いの種類は 5 つ」・SC-12「検知する種類は…5 つ」・運用仕様書の表・C# のゲージの説明。凍結記録以外を直す。
- 重大度の順（`attributes_differ` → `orphan` → … ）: C#・運用仕様書・C-42 の文言に `enabled_differs` を入れる。
- ゲージの意味「食い違ったクライアントの件数」: 1 行が 2 種類（属性違い ＋ 有効・無効違い）を持ち得るようになるので、**件数は種類の数でなくクライアントの数**で数える（説明と一致させる）。
- `check-mcp-client-provisioning.js` 冒頭（M1〜M7）・`integration-stack.yml` の門の注記・`docs/tests/FR-16` の門の行（C-29〜C-34・C-48）・終了時の「（M1〜M7）」: M8 を足す。
- `scripts/test-spec-coverage-baseline.json`: 新しいテストクラスを足さない（既存クラスへ足す）ので床は変わらない見込み。検査で確かめる。

## 設計（決定は IADR-0516 決定 4a。細部は同 IADR の追記）

1. **口に `SetEnabledAsync(clientId, enabled)` を足す**（`IServiceAccountProvisioner`）。結果は `IdpWrite`:
   - `EnabledChanged`（新しい種類）: 入口の印つきのクライアントの `enabled` を書いた（同じ値なら書かない）。`PreviousEnabled` / `WrittenEnabled` を持ち、取り消しは「現在値が書いた値のままのときだけ前の値へ戻す」（決定 4 の取り消しと同じ規則）。
   - `Absent`（新しい種類）: IdP に同じ clientId のクライアントが無い（段 1 より前の行）。**何も書かない。**
   - `AlreadyExists`（既存）: 入口の印が無い（`abac-seeder` 等）。**何も書かない。**
   - Keycloak 版: クライアントの完全一致の照会（`FindClientInternalIdAsync`。共有の 1 つ）→ `GET /clients/{id}` で印と現在の `enabled` → `PUT /clients/{id}` へ **`{"enabled": …}` だけ**を送る（表現を丸ごと送り返さない。secret を含むため）→ `GET` で読み戻す。読み戻しが合わなければ前の値へ戻してから `Failed`。要求の取り消しは伝えない（書き込みの口の規則）。
2. **無効化は登録簿が先、IdP が後**（SC-12「即時に接続拒否」が優先）。登録簿を無効にしてから口を呼ぶ。口の失敗（`Failed` / `Unavailable`）・`Absent`・`AlreadyExists` は**登録簿を取り消さずに 200**（ログで残す: 失敗は Error、未構成は Warning、印なしは Warning、無しは Information）。食い違い（登録簿は無効・IdP は有効）は照合の `enabled_differs` が拾う。同じ操作をもう一度送れば写し直せる（冪等）。
3. **再有効化は IdP が先、登録簿が後**（接続を開く操作。決定 4 の「IdP を先に書く」と同じ側）。`IdpFirstWrite` を通す: 口の失敗は 502 / 503 で登録簿を書かない。登録簿の失敗は IdP を取り消す（無効へ戻す）。**印なし（`AlreadyExists`）は 400 で登録簿も書かない**（入口を通らない主体へ接続を開かない）。`Absent` は IdP に何も無いので登録簿だけを書く（照合が `client_missing` を出し続ける）。
4. **有人の行は IdP に触れない**（決定 3 の逸脱のまま）。
5. **照合に `enabled_differs` を足す**（登録簿の無人の行の `Enabled` と、入口の印つきのクライアントの `enabled` が違う）。一覧の 1 要求で読めるので要求は増えない。重大度は `attributes_differ` → `orphan` → **`enabled_differs`** → `not_managed` → `service_account_missing` → `client_missing`（IdP が有効のまま登録簿が無効なのは多層の防御が欠けた状態で、`not_managed` 以下より先に見せる）。ゲージは**クライアントの数**（1 行が 2 種類を持ち得る）。警報の式・閾値は変えない（`>= 1`）。
6. **稼働の Keycloak での実測（M8）**: 入口で登録したクライアントの secret を master の管理者で読み → `client_credentials` でトークンが出る（陽性対照）→ SC-12 で無効化（200）→ Keycloak の `enabled` が false・トークンが拒否される（4xx・`access_token` なし）・登録簿は無効 → 再有効化（200）→ `enabled` が true・トークンが再び出る・テンプレートの項目（入口の印・人の流れの閉）が残る。否定形: 入口ができる前の登録簿の行 `abac-seeder`（M5 が置く）を無効化しても `abac-seeder` の `enabled` は true のまま、再有効化は 400。M7 の待ちに `enabled_differs`（master の管理者で直接 `enabled=false` にした印つきのクライアント）を足す。

## 本段に入れないもの

- 有人のクライアントの IdP への写し（決定 3 の逸脱。planning#751 の回答待ち）。
- 自動の修復（照合は検知して知らせるだけ。決定 5）。
- 行の排他（無効化と再有効化の交差は照合が `enabled_differs` として検知する。防ぎはしない）。

## 検証

- `dotnet build src/platform/backend/backend.slnx`（警告 0）・`dotnet test` McpServer.Tests・`dotnet format --verify-no-changes`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-mcp-client-provisioning.js --self-test`・`check-grafana-alerting`・`check-prometheus-alerts-parity`（警報は変えないが確かめる）・`check-test-spec-coverage`。
- 文書系: `check-trace-blocks`・`check-adr-numbering`・`check-doc-updated --base origin/develop`・`check-commit-messages`・`check-workflow-job-refs`・`gen-knowledge-graph --check`・`check-deploy-manifests`。
- 変異試験（鍵の論理を外す → 試験が赤）を下に記録する。

## ［2026-10-09 追記］実装後の記録

### 試験の番号（テスト仕様書 FR-16）

C-50 `IdpReconciliationTests`（`enabled_differs`・重大度・ゲージはクライアントの数）・C-51〜C-54 `KeycloakServiceAccountProvisionerTests`（`enabled` だけを送る・印なしと無しは書かない・読み戻しと閉じる側への補償・取り消し）・
C-55・C-56 `IdpProvisioningEndpointTests`（API 面の写し・印なしは無効化で変えず再有効化は 400・IdP に無い行）と `IdpFirstWriteTests`（`Absent` と拒否の文言）・
C-57 `IdpEnabledMirrorFailureEndpointTests`（写しの失敗で登録簿を取り消さず照合が拾う・再有効化は 502）と `UnconfiguredIdpProvisioningEndpointTests`（未構成で無効化 200・再有効化 503）・
C-58 `check-mcp-client-provisioning.js --live` の M8（と M7 の `enabled_differs`）。テストクラスの新しいファイルは足していない（被覆の床は 478 対のまま）。

### 変異試験（すべて戻し、緑に戻ることを確認）

| # | 変異 | 落ちた試験 |
| --- | --- | --- |
| 1 | 照合で有効・無効を比べない | 3 件（C-42 の有効・無効違い・C-50・C-57） |
| 2 | ゲージを種類の数で数える | 1 件（C-50） |
| 3 | Keycloak の口で入口の印を確かめない | 1 件（C-52） |
| 4 | `enabled` 以外の項目も送る | 1 件（C-51） |
| 5 | 読み戻しの確かめを外す | 2 件（C-51・C-53） |
| 6 | 無効化の書き込みの失敗でも前の値へ戻す | 1 件（C-53） |
| 7 | 無効化の写しの失敗（`IdpProvisioningException`）を外へ投げる | 1 件（C-57） |
| 8 | 再有効化で登録簿を先に書く | 3 件（C-56・C-57 の 2 つ） |
| 9 | プロセス内の口で入口の印を確かめない | 1 件（C-56） |
| S1 | `evaluateTokenRefused` が 5xx を拒否と数える | `--self-test` が赤 |
| S2 | `evaluateTokenRefused` がトークンの有無を見ない | 同上 |
| S3 | `evaluateClientEnabled` の比較を反転 | 同上 |
| S4 | `evaluateTokenIssued` が 200 だけで出たと読む | 同上 |

### 検証の結果

- `dotnet build src/platform/backend/backend.slnx`: 警告 0・エラー 0（AST の submodule は pin のコミットをローカルに展開してビルドし、後で消した）。
- `dotnet test` McpServer.Tests: 337 件緑。`dotnet format src/platform/backend/backend.slnx --verify-no-changes`: rc=0。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1013 件緑。`check-mcp-client-provisioning.js --self-test`: 14 件緑。`helm-mcp-client-provisioning.test.js`: 緑。
- `check-grafana-alerting`（24 / 24）・`check-prometheus-alerts-parity`（24 / 24）・`check-grafana-provisioning-parity`・`check-deploy-manifests`（chart 1 / overlay 17）・`check-trace-blocks`・`check-adr-numbering`・
  `check-workflow-job-refs`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-reading-budget`・`check-doc-links`・`check-test-spec-coverage`（478 対）・
  `check-test-traceability`・`check-doc-type-vocabulary`: 緑。`check-doc-updated --base origin/develop`・`check-commit-messages` はコミット後に回した（報告に記載）。
- 🔴 **稼働の Keycloak での M8 は本 PR の中では走っていない**（integration-stack は PR で起動しない。マージ後の最初の実行が初回の実測。稼働中のクラスタには何も実行していない）。
- 残余: `docs/api/openapi.yaml`（手書き・orval の生成元）の再有効化の応答に 400 / 503 を載せていない（説明文が生成物へ流れるため、生成物の再生成と同時に行う。差し替えの 503 も同じく未記載）。
