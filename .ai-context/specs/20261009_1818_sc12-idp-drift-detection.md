---
title: 作業仕様書 — SC-12 の登録簿と IdP のサービスアカウントの属性の食い違いを定期の照合で検知し、計器と警報で知らせる（段 3。#1818）
type: spec
status: done
related_ids: [FR-16, FR-09, UC-09, SC-12, ADR-0123, ADR-0006, ADR-0088, IADR-0516, IADR-0481, IADR-0385, IADR-0165]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 2（登録簿は写し・食い違いは検知して知らせる）・フォローアップ 2
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md（アラートは Alertmanager）
issue: "#1818"
---

# 作業仕様書 — SC-12 の登録簿と IdP の食い違いの検知（段 3。#1818）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0516 への日付つき追記**に置く（決定 5 が形を決めており、新しい決定は周期・期限・計器の名前・警報の閾値の具体値だけである。新しい IADR は起こさない）。
> 計画は project-planning `origin/main`（`82be7dc`）の隣接クローン（読み取り専用）で読んだ。基点は MSP `origin/develop` `ff20ce19`（段 2 の PR #1827 のマージ）。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 稼働の Keycloak での実測は CI の integration-stack（使い捨ての k3d クラスタ）の門に足す。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0123 決定 2**（登録簿は IdP へ書いた値の写し。**食い違ったら検知して知らせる**）・**フォローアップ 2**。ADR-0006（警報は Alertmanager）。
- 機能要求: **FR-16**・**FR-09**。画面: **SC-12**。UC: **UC-09**。
- 起点 issue: **#1818**（#1786 の段 3・AC3）。段 1 は PR #1816（`049a34e5`）、段 2 は PR #1827（`ff20ce19`）。
- 実装 ADR: **IADR-0516 決定 5**（形）・決定 4 の残余（交差した差し替えの後勝ち・並行登録の補償の隙）。先例 **IADR-0481**（定期の検査 ＋ 計器 ＋ 警報 2 本・写し 4 か所）。

## 受け入れ基準（issue #1818）→ 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | 登録簿の無人の行と IdP の属性が食い違う → 照合が走る → ゲージ ≥ 1・カウンタ `drift` が増え・警報「1 件以上」が鳴る | `IdpReconciliationTests`（ゲージ・カウンタ）・`scripts.repo.test.js` #1818 節（警報の式と C# のゲージ名）・`check-grafana-alerting.js`（Grafana 版が発火し得る） |
| AC2 | 検知の種類: クライアントが無い／属性が違う／`managed-by` の印が無い／印つきのクライアントに登録簿の行が無い（孤児） | `IdpReconciliationTests`（種類ごと 1 件）・`KeycloakServiceAccountDirectoryTests`（Keycloak の読み方）・`check-mcp-client-provisioning.js --live` M7（稼働の Keycloak での属性違いと孤児） |
| AC3 | 失敗・未実施はゲージの系列を出さず、カウンタ `failed`、警報「系列が無い」 | `IdpReconciliationTests`（未実施・失敗・失敗の後に前の値を残さない）・`scripts.repo.test.js`（`absent()`） |
| AC4 | 否定形: 照合は IdP にも登録簿にも書かない | `IdpReconciliationTests`（偽の Keycloak が受けた要求がすべて GET・登録簿の行が変わらない） |
| AC5 | 周期と各呼び出しの時間切れは有限。既定の周期と検知までの最大遅延を IADR に書く | `IdpReconciliationTests`（周期の既定・値域外は起動時に落ちる・1 回の照合の期限で `failed`・停止要求は失敗と数えない・並行の上限・常駐）・`KeycloakServiceAccountDirectoryTests`（要求の時間切れ）・IADR-0516 追記 |
| AC6 | 警報は写し 4 か所・`scripts.repo.test.js` で突き合わせ。並行する差し替えの後勝ちが照合で検知されることを試験で固定 | `scripts.repo.test.js` #1818 節・`IdpReconciliationRaceTests`（`IdpFirstWrite` を 2 本交差させる） |

## 現状（実測。`ff20ce19`）

| 事実 | 確かめ方 |
| --- | --- |
| McpServer に Meter は無い（`AddMeter` は認可サービス・LLM ゲートウェイ・通知だけ） | `grep -rn AddMeter src/platform/backend` |
| IdP への口 `IServiceAccountProvisioner` は書き込みだけ（Create / Replace / Undo）。Keycloak 版は `IsManagedAsync`・`FindClientInternalIdAsync`・`ReadAttributesAsync`・`Decode`・`SameAttributes` を内部に持つ | `KeycloakServiceAccountProvisioner.cs` |
| 交差した差し替えの後勝ち（IdP は後の要求・登録簿は先の要求）は**行の排他が無いので起き得る**。取り消しは現在値が書いた値のときだけ戻す | IADR-0516 決定 4・`IdpFirstWrite.cs` |
| IADR-0481 の検査は `PeriodicTimer`（TimeProvider なし）・周期 `OwnerReadPolicyCheck:Interval`（既定 1 分・下限 1 分・`hh:mm:ss`）。**helm にも compose にも周期の値は無い**（コードの既定で回る） | `OwnerReadPolicyCheck.cs`・`git grep OwnerReadPolicyCheck deploy` |
| 警報の写しは 4 か所: `deploy/prometheus/alerts.yml`・`deploy/local/observability/prometheus.yaml`・`deploy/grafana/provisioning/alerting/slo-alerts.yaml`・`deploy/local/observability/grafana.yaml`。現在 22 件ずつ | `check-grafana-alerting.js`・`check-prometheus-alerts-parity.js` |
| integration-stack は観測スタック（Prometheus）を起こさない（`OBS` は opt-in で門は付けない）。**ゲージの値を Prometheus から読む門は作れない** | `.github/workflows/integration-stack.yml`・`k8s-local-up.sh` |
| McpServer のコンソールログは既定の simple 形式（`Logging__Console__FormatterName` の上書きは無い） | `git grep FormatterName deploy` |

## 母集合（規則 9・10）

### 規則 9 — 誤りの側の文字列で全文書を走査した

`git grep -n -e '食い違いの検知' -e '定期の照合' -e '照合（' -e '照合〔' -e '#1818' -- ':!.ai-context/specs' ':!CHANGELOG.md'`、加えて警報の件数 `22 件`（`check-grafana-alerting.js`）:

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `docs/screens/SC-12_mcp-client-management.md`（冒頭の注記「食い違いの検知は後続の段」・§状態の写し方の 502 の行・§「食い違いの検知は後続の段（#1818）で入る」） | **直す** | 本段で入る |
| `docs/api/FR-16_mcp-server.md`（時間切れの行の「照合〔#1818〕が拾う」） | **直す**（照合の構成・計器の行を足す） | 実在する機能になる |
| `docs/tests/FR-16_mcp-server.md`（§未実施・残件「食い違いの検知は未実装」） | **直す**・C-36〜を足す | 本段で入る |
| `docs/operations/paired-secret-rotation-runbook.md` 手順 4（「定期の照合が入るまでは手で行う」） | **直す** | 照合が入る。手で行う代わりに計器とログを見る |
| `docs/operations/operations.md`（警報の手順と障害対応の表） | **足す** | IADR-0481 と同じ置き方（検知と通知の節 ＋ 障害対応の行） |
| `scripts/check-grafana-alerting.js` 冒頭の「22 件返す」 | **直す**（24 件へ数え直す） | 警報を 2 本足す |
| `src/.../KeycloakServiceAccountProvisioner.cs` L120・`IdpFirstWrite.cs` L14 の「照合（#1818）が拾う」 | **据え置く** | 本段で真になる（記述は誤りにならない） |
| `.ai-context/adr/IADR-0516` 決定 5「本 PR では実装しない」・統制表・残余 3 | **日付つき追記だけ**（本文は書き換えない） | 凍結記録 |
| `.ai-context/adr/IADR-0479` L84 | **対象外** | #1786 で追うと書いた注記であり、誤りにならない |
| `.ai-context/specs/20261008_1786_*`・`20261009_1817_*` | **直さない** | 確定済みの作業仕様書 |

### 規則 10 — この変更で新たに誤りになる自分の記述

- 警報の件数（Prometheus・Grafana とも 22 → 24）。`check-grafana-alerting.js` の冒頭の件数は**数え直して**書く。
- `scripts/test-spec-coverage-baseline.json`（仕様書 × テストクラスの対）: 新しいテストクラスを仕様書へ載せるので `--update` で床を上げる。
- `scripts/check-mcp-client-provisioning.js` の冒頭（M1〜M6）・`integration-stack.yml` の門の注記・`docs/tests/FR-16` の C-29〜C-34 の範囲: M7 を足すので「M1〜M7」へ。
- `InMemoryServiceAccountProvisioner` を読み取りの口にも使う → 「試験は Snapshot で読む」の注記は真のまま。

## 設計（決定は IADR-0516 決定 5。具体値は同 IADR の追記）

1. **読み取りの口 `IServiceAccountDirectory`**（`Domain/Ports`）: `ListClientsAsync`（IdP の全クライアントの clientId と入口の印の有無）と `ReadServiceAccountAttributesAsync`（`users?username=service-account-<client>&exact=true` ＝ 認可サービスと同じ照会。居なければ null）。**書き込みを持たない**（照合は書かない＝AC4 を型で保つ）。実装は書き込み口の 3 つ（Keycloak・プロセス内・未構成）がそれぞれ兼ね、選択は書き込み口と同じ 1 つ（`McpClientProvisioning:Provider`）に従う。
   - Keycloak 版: `GET clients?first=&max=100` を頁で読む（上限 100 頁 ＝ 1 万件。超えたら失敗）。取り消しは**伝える**（読むだけなので孤児は生まれない）。時間切れは口の HttpClient の `Timeout`（既定 10 秒）。
   - 未構成: `Unavailable` を投げる → 照合は失敗（系列なし・「系列が無い」が鳴る）。
2. **照合 `IdpReconciliation`**（`Features/McpClients/IdpReconciliation`）: 登録簿の無人の行を読み → IdP の全クライアントを読み → 行ごとに比べる（並行は最大 4）→ 孤児を数える。比べ方は書き込み口の読み戻しと同じ `SameAttributes`（集合値は集合・IADR-0385）。種類は `client_missing` / `not_managed` / `service_account_missing` / `attributes_differ` / `orphan`。**何も書かない。**
3. **計器** `microservices-platform.mcp-server`: ゲージ `mcp.idp_reconciliation.drifted`（直近の照合の食い違いの件数。未実施・失敗は系列なし）とカウンタ `mcp.idp_reconciliation.checks.total{mcp.idp_reconciliation.outcome=match|drift|failed}`（1 回の照合に 1 つ）。**クライアント ID を属性に載せない**（系列の数を有界に保つ。行はログで名指しする）。
4. **ログ**: 食い違いは行ごとに Warning（クライアント ID と種類。値は出さない。1 回に 20 件まで、超えた分は件数だけ）。1 回の照合ごとに Information の要約。失敗は Error（未構成は Warning）。
5. **常駐** `IdpReconciliationHostedService`: 起動時に 1 回、以後 `McpClientProvisioning:Reconciliation:Interval`（既定 `00:01:00`・下限 1 分・`hh:mm:ss`・値域外は起動時に落とす）ごと。1 回の照合の期限は周期と同じ長さ（`TimeProvider` で数える）。停止要求は失敗と数えない。helm・compose には値を置かない（IADR-0481 と同じくコードの既定で回す）。
6. **警報** `McpClientIdpDrift`（warning。`mcp_idp_reconciliation_drifted >= 1`、Grafana は生の値を `gt 0`・`noDataState: OK`）と `McpClientIdpReconciliationSeriesAbsent`（warning。`absent(…)`）を 4 か所へ。`for: 5m`（登録・差し替えの途中を照合が見た一時の食い違いは次の周期で消えるので、`for` が吸収する）。
7. **稼働の Keycloak での実測（M7）**: integration-stack に Prometheus が無いのでゲージは読めない。代わりに `kubectl logs` で照合のログを読む: (a) 入口で登録したクライアントのサービスアカウントの属性を master の管理者で直接書き換え → `attributes_differ` が名指しされる、(b) 入口の印つきのクライアントを master の管理者で直接作る → `orphan` が名指しされる。待ちは最大 150 秒（周期 1 分 ＋ 1 回の照合）。読み取りの口（クライアントの列挙が属性を含むこと・`mcp-client-admin` の権限で列挙と照会が通ること）は稼働の Keycloak でしか確かめられない。

## 本段に入れないもの

- **自動の修復**（IADR-0516 決定 5 は「検知して知らせる」。照合は書かない）。
- 無効化の IdP の `enabled` への写し（決定 4a。後続の issue）。したがって照合は `enabled` を比べない（比べると無効化した行がすべて食い違いになる）。
- 有人の行（IdP へ書かない既知の逸脱。決定 3）。

## 検証

- `dotnet build src/platform/backend/backend.slnx`（警告 0）・`dotnet test` McpServer.Tests・`dotnet format --verify-no-changes`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-grafana-alerting`・`check-prometheus-alerts-parity`・`check-grafana-provisioning-parity`・`check-mcp-client-provisioning.js --self-test`・`check-test-spec-coverage`。
- 文書系: `check-trace-blocks`・`check-adr-numbering`・`check-doc-updated --base origin/develop`・`check-commit-messages`・`check-workflow-job-refs`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-reading-budget`・`check-deploy-manifests`。
- 変異試験（鍵の論理を外す → 試験が赤）を下に記録する。

## ［2026-10-09 追記］実装後の記録

### 試験の番号（テスト仕様書 FR-16）

C-36〜C-40 `KeycloakServiceAccountDirectoryTests`・C-41〜C-46 `IdpReconciliationTests`・C-47 `IdpReconciliationRaceTests`・C-48 `check-mcp-client-provisioning.js --live` の M7・C-49 `scripts.repo.test.js` の #1818 節。

### 変異試験（すべて戻し、緑に戻ることを確認）

| # | 変異 | 落ちた試験 |
| --- | --- | --- |
| 1 | 属性の比べ方を外す（印つきなら常に一致） | 4 件（C-42 属性違い・C-43・C-44・C-47）。C-41 は通る |
| 2 | 孤児を数えない | 1 件（C-42 孤児） |
| 3 | 失敗で前の件数を残す | 1 件（C-43） |
| 4 | 1 回の照合の期限を外す | 1 件（C-45。見張り 10 秒で落ちる） |
| 5 | 並行の上限を「既定（-1）」へ外す | **落ちない（等価）**: `Parallel.ForEachAsync` の -1 は CPU 数（実測 4）に倒れる |
| 5' | 行ごとの読み取りを全行同時にする（`Task.WhenAll`） | 1 件（C-45 並行の上限） |
| 6 | 一覧で入口の印を見ない | 1 件（C-36） |
| 7 | 一覧を 1 頁で読み終える | 2 件（C-36・C-39） |
| 8 | 読み取りの呼び出し元の取り消しを失敗へ畳む | 1 件（C-40） |
| S1 | k8s の Prometheus の写しの閾値を `>= 2` へ | `scripts.test.js` が赤（写しの一致の検査が先に落ちる） |
| S2 | k8s の Grafana の写しの `absent()` の系列名を変える | 同上 |
| S3 | C# のゲージ名を変える | #1818 節が赤 |
| S4 | compose の Grafana の評価器を `lt 1` へ | 同上（`check-grafana-alerting` の自己検査が先に落ちる） |
| S5 | 説明が指すカウンタ名を変える | #1818 節が赤 |

### 検証の結果

- `dotnet build src/platform/backend/backend.slnx`: 警告 0・エラー 0（AST の submodule は pin のコミットをローカルに展開してビルドし、後で消した）。
- `dotnet test` McpServer.Tests: 323 件緑。`dotnet format src/platform/backend/backend.slnx --verify-no-changes`: rc=0。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1013 件緑。`check-mcp-client-provisioning.js --self-test`: 10 件緑。`helm-mcp-client-provisioning.test.js`: 8 件緑。
- `check-grafana-alerting`（24 / 24）・`check-prometheus-alerts-parity`（24 / 24）・`check-grafana-provisioning-parity`・`check-deploy-manifests`（chart 1 / overlay 17）・`check-trace-blocks`・`check-adr-numbering`・
  `check-workflow-job-refs`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-reading-budget`・`check-doc-links`・`check-unit-dependencies`・
  `check-backend-libraries`・`check-default-credentials`・`check-test-spec-coverage`（床 478 対へ上げた）・`check-test-traceability`・`check-doc-type-vocabulary`・`actionlint`（integration-stack）: 緑。
- 🔴 **稼働の Keycloak での M7 は本 PR の中では走っていない**（integration-stack は PR で起動しない。マージ後の最初の実行が初回の実測。稼働中のクラスタには何も実行していない）。
- promtool はリポジトリに無い（警報の式の検査は `check-grafana-alerting`・`check-prometheus-alerts-parity`・`scripts.repo.test.js` が持つ）。

## ［2026-10-09 追記 / PR #1831 の監査（条件付き GO）と AI レビューへの対応］

- 🟡1 名指しを重大度順（`attributes_differ` → `orphan` → `not_managed` → `service_account_missing` → `client_missing` → クライアント ID）にし、種類ごとの件数を毎回 1 行で出す。試験: 21 件の `client_missing` ＋ 孤児 ＋ 属性違い（C-42）。変異「クライアント ID 順へ戻す」で赤。
- 🟡2 1 行の読み取りの失敗を `IdpReconciliationRowException` で包み、Error ログで行を名指しする（全体を失敗にする設計は変えない）。運用仕様書に特定の手順。試験 C-43。変異「包まずに投げ直す」で赤。
- 🟡3 運用仕様書に配備直後の `client_missing` と対処（画面で保存し直す）。
- AI レビュー 🟡: 利用者名の完全一致の照会を `FindServiceAccountUsersAsync` へ括り出した（既存の書き込み口の試験は緑のまま）。🟢 要求回数の式を ⌊n/100⌋＋1 へ。
- 検証: McpServer.Tests 325 件緑、ほかは下の一式を回し直した（報告に記載）。
