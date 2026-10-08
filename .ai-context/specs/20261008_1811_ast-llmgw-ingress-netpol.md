---
title: 作業仕様書 — 本番の NetworkPolicy に、取引ユニット（AST）の名前空間から LLM ゲートウェイへの ingress の許可を values の切替で足す（#1811）
type: spec
status: done
related_ids: [NFR-09, FR-04, FR-11, ADR-0125, ADR-0084, ADR-0010, ADR-0044, IADR-0513, IADR-0424, IADR-0466, IADR-0026]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0125_ast-kb-reader-static-project-policy-with-confidentiality-ceiling.md 決定 4（本番の NetworkPolicy は別件）
issue: "#1811"
---

# 作業仕様書 — AST の名前空間から LLM ゲートウェイへの ingress の許可（#1811）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。判断の記録は **IADR-0513 の日付つき追記**（残余 2）に置く（新しい IADR は起こさない。理由は §設計）。
> 計画は project-planning `b5b584f`（`origin/main`）を読んだ。AST は `origin/develop` `8c7205cc` を読んだ。基点は MSP `origin/develop` `9bb5bbde`（PR #1810 のマージ）。
> 🔴 **稼働中のクラスタには何も実行しない**（`helm template` と単体の試験だけ）。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-09**（多層防御の一部としての NetworkPolicy）。到達先は LLM ゲートウェイ（**FR-04** 取引判断・報告書の生成の委譲先、**FR-11** 越境の判定）。
- 計画 ADR: **ADR-0125 決定 4**（本番の NetworkPolicy は別件）。認可は **ADR-0084 決定 1**（LLM ゲートウェイの端点は `ServiceCaller`）。費用の統制は **ADR-0044**。
- 起点 issue: **#1811**（#1756 の残余・IADR-0513 残余 2・PR #1810 の独立監査 🟡）。

## 計画が決めていること・決めていないこと

- AST が LLM ゲートウェイへ生成を委譲することは計画済み（`AST/ADR-0010`、MSP の ADR-0010・ADR-0084）。ネットワークの穴の形は計画が定めない（ADR-0125 決定 4 と同じ扱い）。
- 本件で決めるのは #1756 と同じく**穴の形と既定値だけ**で、実装の判断に収まる。LLM ゲートウェイ側の認可・費用統制は変えない（issue の「やらないこと」）。

## 現状（実測）

### MSP（`9bb5bbde`）

| 事実 | 確かめ方 |
| --- | --- |
| `services.llmgateway`: `enabled: true`・`port: 8080`（REST）・`grpcPort: 8081`（埋め込みとテキスト生成の gRPC） | `deploy/helm/microservices-platform/values.yaml` |
| Pod のラベルは `app: llmgateway-service`（`app: <キー>-service`。#1756 の行き先と同じ規則） | `templates/deployment.yaml`・既定の描画 |
| `networkPolicy.fromAst` は `kbReader`（→ `retrieval`）・`kbWriter`（→ `document`）の 2 用途。行き先は `$allowedTargets` で用途ごとに固定し、ほかの値は `fail` | `templates/networkpolicy.yaml`（PR #1810） |
| 既定の描画に `allow-ast-*` は 0 枚。試験は「AST 報告書 → LLM ゲートウェイは閉じたまま」を 1 行で固定している | `scripts/helm-ast-kb-ingress.test.js` |

### AST（`8c7205cc`）— LLM ゲートウェイを呼ぶ Pod の母集合

走査: `git grep -nE 'LlmGateway|llmgateway' origin/develop`（`backend/**`・`deploy/helm/ai-stock-trading/**`）。

| AST の Pod（`app`） | 構成キー | 本番既定（`values.yaml`） | 経路B（`values-local.yaml`） | 生成の口 | 用途（purpose） |
| --- | --- | --- | --- | --- | --- |
| `report-service` | `LlmGateway__BaseUrl` | `""`（Placeholder の定型散文） | `http://llmgateway-service.microservices-platform:8080` | REST `POST /complete` | `report-daily` / `report-weekly` / `report-monthly` |
| `trade-decision-service` | `LlmGateway__BaseUrl` | `""`（Placeholder＝常に Hold） | 同上 | 同上 | `trade-decision` / `trade-decision-screening` |

- コード側の呼び出し元も同じ 2 サービス（`git grep -l 'ILlmCompletionTransport|RestLlmCompletionTransport|AddLlm' -- backend/Services` → ReportService・TradeDecisionService だけ）。
  REST の輸送は `RestLlmCompletionTransport`（`/complete`）。`/complete/stream`・`/embed` は呼ばない。
- `NotificationService` の当たりは試験（`PolicyApprovalRealReportHostTests`）の中で報告書のホストを組むためのもので、本番の構成には `LlmGateway__*` を持たない。**clients に入れない。**
- `information-collection-service` は LLM ゲートウェイを配線していない（構成キーを持たない）。**clients に入れない。**
- gRPC（`LlmGateway__Grpc` → `:8081`）は AST の `values.yaml` に**コメントでだけ**示され（同ファイルの注記「既定は REST であり、このキーは意図的に置いていない」）、本番既定・経路B とも置いていない。**8081 は開けない**（下の残余）。
- s2s の主体は MSP の `platform` レルムの `ai-stock-trading-llm-caller`（realm ロール `platform-service`。`deploy/keycloak/microservices-platform-realm.json`）。AST の env `LlmGateway__Auth__ClientId` / `__ClientSecret` が `ast-secrets` の `llm-auth-client-id` / `llm-auth-client-secret` を読む。

### LLM ゲートウェイを開ける前のアプリ層の前提（kbWriter の内容の ABAC の門に当たるもの）

| 観点 | 実測 | 開ける前提になるか |
| --- | --- | --- |
| 認証・認可 | REST 3 口（`/complete`・`/complete/stream`・`/embed`）と gRPC 2 面は `ServiceCaller`（realm ロール `platform-service`）を要する（`Features/Completions/Complete/Endpoint.cs`・`Program.cs`。IADR-0424） | **ならない（既に閉じている）**。匿名・利用者のトークンは 401/403。穴を開けても認可は緩まない |
| AST の主体 | `ai-stock-trading-llm-caller` は KB の書き手と**別の主体**で、`platform-service` だけを持つ（realm の注記: 書き手へ `platform-service` を足すと東西端点すべてへ届くので分けた） | **前提**: 本番の認証基盤に同 client と秘密があり、AST の `ast-secrets` に `llm-auth-client-*` があること。無くても安全側（AST は Placeholder／401 で縮退） |
| `platform-service` の射程 | `platform-service` は LLM ゲートウェイ以外の東西の `ServiceCaller` 端点にも通る | **ネットワークで行き先を `llmgateway` に固定する理由**（`target` 固定・`fail`）。穴が LLM ゲートウェイだけなので他の東西端点へは L4 で届かない |
| 費用の統制 | 月次予算は**用途別**の設定値（`Llm:Budget:MonthlyLimits`。既定なし）で、**アラートだけ**（ゲージ `llm_budget_monthly_limit` と `llm_cost_total` を比べる。要求を止めない。IADR-0466） | **門ではない**（呼び出し元ごとの予算は無い。用途で数える）。金額が未設定のあいだアラートは不活性 → 運用仕様書で「開ける前に AST の用途の上限を設定する」ことを確かめ事項に書く |
| 用途の登録 | AST の 5 用途はすべて `Llm:Routing:PurposeModels` に登録済み（`appsettings.json`） | ならない（未登録なら `default` へ倒れる） |
| 越境の判定 | 送信先は越境マトリクス（`req.Confidentiality`）で決まる。保存済みデータを読む口ではない | **ならない**。kbWriter と違い、穴を開けても**保存済みの組織データの読み取りは広がらない**（費用と外部送信の量が増えうるだけ） |

**結論**: kbWriter の「内容の ABAC の門」に相当する、開ける前に**閉じていると情報が漏れる**アプリ層の門は無い。前提は (1) 主体 `ai-stock-trading-llm-caller` の秘密の投入、(2) 費用の監視（AST の用途の月次上限の設定）の 2 つで、どちらも欠けても安全側（呼べない／アラートが鳴らない）に倒れる。(2) は統制が「定めたが働いていない」状態になりうるので運用仕様書の確かめ事項に置く。

## 設計（正は IADR-0513 の追記）

1. values `networkPolicy.fromAst.llmGateway`: `enabled: false`・`target: llmgateway`・`clients: [report-service, trade-decision-service]`。
2. テンプレートは #1756 の繰り返しへ用途を 1 つ足すだけ（`$allowedTargets` に `llmGateway → llmgateway`、`range` の一覧に `llmGateway`）。形・`fail` の条件はそのまま共有する。
   NetworkPolicy の名前は `allow-ast-llm-gateway-ingress`（`kebabcase`）。
   - `from`: 同じ要素の `namespaceSelector`（`kubernetes.io/metadata.name`）＋ `podSelector`（`app In clients`）。
   - `ports`: `services.llmgateway.port`（REST 8080）だけ。`grpcPort`（8081）は開けない。
   - `target` は `llmgateway` だけを受ける（`platform-service` のトークンを他の東西端点へ向けさせない）。
3. **新しい IADR は起こさない。** 決定は「#1756 と同じ形で 1 用途を足す」であり、形・既定・`fail` の判断は IADR-0513 決定 2〜4 のまま。残余 2 への日付つき追記で、母集合と前提の実測・結論を書く。
4. CI の values（`ci/ast-kb-ingress-values.yaml`）に `llmGateway.enabled: true` を足す（有効の定義を 1 か所に保つ。`check-deploy-manifests.js` の lint / template / kubeconform も 3 枚を描く）。ファイル名は変えない（参照の追随を増やさない）。
5. 試験 `scripts/helm-ast-kb-ingress.test.js` を広げる（下の受け入れ基準）。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| B1 | 既定（本番像）と values-local の描画は develop とバイト等価（AST からの許可は 0 枚） | 前後の `helm template` の `cmp`・試験の陰性対照 |
| B2 | 陰性対照: 既定では LLM ゲートウェイへの AST の 2 本が落ちる（`problemsFor(OFF)` が読み手・書き手の 3 本＋ LLM の 2 本＝5 本） | 試験 |
| B3 | 有効時、`allow-ast-llm-gateway-ingress` がちょうどこの形（`app: llmgateway-service`・from の AND・ポート 8080 だけ） | 試験 |
| B4 | 呼び出し元の評価: 報告書・取引判断 → LLM REST だけが通り、LLM gRPC 8081・情報収集・発注・app の無い Pod・別の名前空間の同名 Pod は落ちる。KB の穴とは互いに混ざらない | 試験の評価器 |
| B5 | 片方だけの有効化（`llmGateway` だけ）はその 1 枚だけ。knob の追随（`clients`・`services.llmgateway.port`） | 試験 |
| B6 | 描画で止まる: `llmGateway.clients` が空・`services.llmgateway.enabled=false`・`target` が `llmgateway` 以外（`document`・`bff`） | 試験 |
| B7 | 変異: `range` の一覧から `llmGateway` を外すと（黙って描かない）、from の AND を割ると、ポートを外すと、LLM の呼び出し元の判定が赤になる | 試験 |
| B8 | 運用仕様書「本番の前提（ネットワーク）」の表に LLM ゲートウェイの行があり、前提（主体の秘密・費用の上限）と確かめ方・切り戻しが書かれている | 文書 |

## 母集合（規則 9・10。`9bb5bbde` 時点）

### 規則 9

走査: `git grep -nE "#1811|(AST|取引ユニット).{0,60}(LLM ゲートウェイ|llmgateway).{0,60}(塞|閉|NetworkPolicy|届か)|(LLM ゲートウェイ|llmgateway).{0,40}(閉じたまま|射程外)|fromAst"`（`src/ai-stock-trading`・`.ai-context/specs`・`CHANGELOG.md` を除く）。

| 当たり | 追随 |
| --- | --- |
| `docs/operations/operations.md:569・588`（「用途ごとに 1 本ずつ」「どちらも」・「この値では開かない（#1811 で扱う）」） | 追随（表に行を足し、588 を前提の記述へ書き換える） |
| `.ai-context/adr/IADR-0513:109-110`（残余 2「本 IADR では開けない。#1811 で追う」） | 追随（日付つき追記。本文は凍結） |
| `scripts/helm-ast-kb-ingress.test.js:247`（「閉じたまま」の固定） | 追随（切替で開くへ改める） |
| `templates/networkpolicy.yaml:146-157`・`values.yaml:150-170`（`kbReader / kbWriter` の 2 用途の注記） | 追随（用途を足す） |
| `ci/ast-kb-ingress-values.yaml` | 追随（`llmGateway` を足す） |
| `.ai-context/adr/IADR-0492:135`・`IADR-0500:136`（`fromAst.kbReader` の追記） | 追随しない（KB の読み手の話で、LLM ゲートウェイの記述を持たない） |
| `.github/workflows/ci.yml`・`scripts/README.md` の試験の説明（「KB の読み手・書き手」） | 追随（LLM ゲートウェイを足す。手順名の文言だけで、起動条件・必須チェックは変わらない） |

### 規則 10

- 運用仕様書の「既定はどちらも」「読み手の検索も書き手の保存も届かなくなる」は 3 用途になると誤りになる → 書き換える。
- IADR-0513 §結果「穴は 2 サービス × … AST の 3 Pod」は 3 サービス・3 Pod 種（Pod の種類は増えない）になる → 追記で数え直す（本文は凍結）。
- 試験の `problemsFor(OFF)` の件数（3）・「増えるのは 2 枚」・ci の values の 6 行の固定・values の既定の固定は、用途を足すと崩れる → 数え直す（5 本・3 枚・8 行）。

## 検証

- 前後の描画（既定・values-local）の `cmp` が一致。
- `node scripts/helm-ast-kb-ingress.test.js`・兄弟の `helm-private-notes-sync-authz.test.js`・`helm-synthetic-monitor.test.js`・`helm-llm-provider-keys.test.js`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-trace-blocks`・`check-adr-numbering`・`check-doc-updated --base origin/develop`・`check-commit-messages`・`check-cross-repo-refs`・`check-plan-id-qualification`・`gen-knowledge-graph --check`。

## 範囲外

- LLM ゲートウェイ側の認可・費用統制の変更（呼び出し元ごとの予算・要求の遮断は持ち込まない）。稼働中のクラスタへの適用。
- gRPC（8081）の穴。AST が `LlmGateway__Grpc` を有効にするときに別に足す（残余）。
