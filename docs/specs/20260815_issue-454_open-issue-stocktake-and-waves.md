---
title: 作業仕様書 — OPEN issue 全 38 件の棚卸しと波の割り当て（#454）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0116
  - IADR-0119
  - IADR-0120
  - IADR-0139
  - IADR-0141
  - IADR-0142
  - IADR-0179
  - IADR-0180
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md"
related_specs:
  - "../adr/IADR-0180_blocked-judgments-expire.md"
  - "../adr/IADR-0141_audit-rounds-and-population-drawing.md"
  - "../adr/IADR-0116_reimplementation-branching-and-pr-policy.md"
---

# 作業仕様書 — OPEN issue 全 38 件の棚卸しと波の割り当て（#454）

## 1. 起点

- **起点 issue**: **#454**（【親】計画大改定に伴う全面再実装のトラッキング）
- **起点 ID**: **無採番 `NFR`**（運用保守。メタ作業であり、製品側に当たる採番が無い。
  `.claude/rules/traceability.repo.md` §起点 ID の種別 が認めている形であり、
  「番号が無いこと」は実装側で新設してよいという意味ではない〔[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 2〕）
- **義務の出どころ**: `.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方 —
  **「着手前に母集合を自分で引き、結果と除外理由を作業仕様書へ書く」**。本書がその成果物である。
- **判定の据え置き禁止**: [IADR-0180](../adr/IADR-0180_blocked-judgments-expire.md) —
  **blocked は「前回できなかった」を根拠に据え置いてはならない。** 本書の判定はすべて本日測り直した。

## 2. 母集合の引き方（走査コマンドと日時）

**引いた日時: 2026-08-15。**

| 項目 | 値 |
| --- | --- |
| 走査手段 | `mcp__github__list_issues`（GitHub MCP） |
| 引数 | `owner=endazon` / `repo=microservices-platform` / `state=OPEN` / `perPage=100` |
| 応答の `totalCount` | **38** |
| 応答の `pageInfo.hasNextPage` | **`false`**（1 ページで全件。ページ落ちなし） |
| 自分で数えた件数 | **38**（表の行数と一致） |

> **★ 他人の数えを転記していない。** 本作業の指示文にも母集合の下敷きが添えられていたが、
> [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1（規則 2）に従い
> **上記コマンドで引き直した結果を正とした**。下敷きと食い違った点は §7 に全件挙げる。

### 除外したもの（と、その理由）

**38 件のうち 1 件も除外していない。表は全数である。** 走査の外に置いたものは次のとおり。

| 走査の外 | 理由 |
| --- | --- |
| CLOSED issue | 棚卸しの対象は「以降の波の判断根拠」であり、着手先にならない |
| Pull Request | `list_issues` は PR を返さない。**PR の状態は GitHub が正**であり、本書は持たない（[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md) の「人が更新する台帳を持たない」） |
| `endazon/project-planning` の issue | 別リポ。裁定依頼の宛先であって本リポの着手先ではない |
| `endazon/ai-stock-trading` の issue（`AST#520` 等） | 別リポ。submodule 側の債務であり本リポの PR では閉じられない |

## 3. 本セッションの実測環境（判定の前提）

**判定を測り直す前に、道具の側を先に測った。** 記憶ではなく実行結果である。

| 項目 | 実測 | 判定への効き方 |
| --- | --- | --- |
| `dotnet` | **無し**（`which dotnet` が空） | **`src/*/backend/**` に 1 行でも掛かる issue は `/verify` を満たせない → 今回は着手しない** |
| `gh` CLI | **無し** | GitHub 操作は MCP で代替できる。判定に効かない |
| `node` | v22.22.2 | `scripts/*.js` の検査器・自己試験は実走できる |
| `pnpm` | 10.33.0 | フロントの typecheck / lint / test / build は実走できる |
| `src/` の `pnpm install --frozen-lockfile` | **成功** | 同上 |
| submodule `src/ai-stock-trading` | pin `7f69fb5`・populate 済み | #747 の切り分けを再現できる |
| **submodule `planning`** | **本 worktree では未 populate**（gitlink は `4d6a7d6`、`planning/` は空） | **§7 の食い違い 4。`planning/` を読む作業は populate が前提** |
| 隣接クローン `/home/user/microservices-platform/planning` | `4d6a7d6`（`origin` = `github.com/endazon/project-planning`） | 計画 ADR の状態はここで実測した |
| 併走 worktree | `/home/user/wt-747`（`fix/nfr-ast-bump-frontend-ci-paths`）・`/home/user/wt-749`（`fix/nfr-pin-freshness-reverse-comparison`）— **いずれも作業差分ゼロ** | **波 1 の場は既に用意されている**（着手はまだ） |

## 4. 棚卸し表（OPEN 38 件・全数）

**列は 11 列である。`PR 番号` / `open|closed` / `CI 状態` の列は置かない** —— GitHub が正であり、
書けば二重管理になる（[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)）。

**種別の丸め**: 件名が `test(...)` の 2 件（#466 / #453）は、指定の語彙に `test` が無いため **`chore` へ丸めた**。
件名接頭辞を持たない 2 件（#336 / #271）は本文の性質から `track` / `feat` を当てた。

| 番号 | タイトル要約（20 字以内） | 種別 | 判定 | 判定根拠（1 行） | 最後に測った時点 | 宣言ファイル領域 | 依存（先行 issue） | 波 | ブランチ名案 | 除外理由 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| #747 | AST bump がフロント CI を起動しない | fix | **即着手可** | 資産は workflow と JSON のみ。pnpm 実走で床を再現でき dotnet 不要 | 2026-08-15 / #747 | `.github/workflows/frontend.yml`・`frontend-tests.yml`・`scripts/chunk-budget-baseline.json`（**回帰テストを置くなら `scripts/scripts.repo.test.js`**） | なし | 1 | `fix/nfr-ast-bump-frontend-ci-paths` | — |
| #749 | pin 鮮度検査が逆方向に比較する | fix | **即着手可** | 検査器 1 本と自己試験で閉じる。fail-open 要件は未 populate でも検証できる | 2026-08-15 / #749 | `scripts/check-planning-pin-freshness.js`・`scripts/scripts.repo.test.js`（`:6085-6180`） | なし | 1 | `fix/nfr-pin-freshness-reverse-comparison` | — |
| #748 | 無主の計画 ID を warn から分離 | feat | **条件付き可** | 突合材料の取り方（案 A 事前生成 / 案 B CI 限定）を IADR で確定してから着手 | 2026-08-15 / #748 | `scripts/check-test-traceability.js`・`scripts/scripts.repo.test.js`（`:676-770`）・新規 baseline JSON | **#749**（同一ファイル） | 2 | `feat/nfr-unowned-plan-id-detection` | — |
| #756 | 先行検査器 3 本の優劣判定 | chore | **条件付き可** | 判定手順の正が `planning/tools/impl-handoff-kit/HOWTO.md`。**submodule populate が前提** | 2026-08-15 / #755 | `scripts/check-commit-messages.js`・`check-cross-repo-refs.js`・`check-plan-id-qualification.js`・`kit-sync-classification.json`・`scripts/scripts.repo.test.js`（`:2690-2900`） | **#748**（同一ファイル） | 3 | `chore/nfr-kit-superiority-three-checkers` | — |
| #757 | scripts.test.js をキット版へ追随 | chore | **条件付き可** | キット版テストが前提とする検査器の追随と対。#756 の結論で射程が決まる | 2026-08-15 / #755 | `scripts/scripts.test.js`・`scripts/kit-sync-classification.json` | **#756**（直列） | 3 | `chore/nfr-scripts-test-kit-parity` | — |
| #493 | SPA 第 5 段 運用系ツーリング | chore | **条件付き可** | Renovate / Husky は単独で入る。**Plop は第 4 段（Zustand・TanStack Table・ECharts）待ちで本 issue は閉じない** | 2026-08-15 / #493 | `src/package.json`・`src/.husky/`・`renovate.json`・`src/knip.json` | #452（第 4 段） | 4 | `chore/adr-0031-spa-stage5-tooling` | — |
| #743 | feedback 本文の凍結と裁定保存の衝突 | docs | **decision-needed** | [IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 の改定にあたり実装が独断で決められない（3 択） | 2026-08-15 / #743 | `docs/adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md`・`scripts/feedback-body-addendum-baseline.json` | なし | 随時 | `docs/nfr-feedback-addendum-scope` | — |
| #572 | issue 消化率を上げる | chore | **decision-needed** | 施策 1（束ね）は [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) で既に裁定済み。残る施策 3（監査の重さ）は利用者判断 | 2026-08-15 / #572 | 文書のみ（`docs/adr/`） | なし | 随時 | `chore/nfr-throughput-audit-weight` | — |
| #752 | SourceItem が更新者を運ばない | fix | **decision-needed** | 更新者→利用者識別子の**解決手段**に裁定が要る。かつ本体は backend（dotnet 不在） | 2026-08-15 / #516 | `src/knowledge/backend/Services/DataSourceService/**`・コネクタ 4 実装 | #516 / 裁定 | 随時 → 5 | `fix/fr-05-sourceitem-modifier` | — |
| #754 | department の供給源が全滅 | fix | **decision-needed** | **フォルダ→部門コードの写像規則の粒度**に裁定が要る。SC-06 フォーム欄のみ frontend で先行可 | 2026-08-15 / #516 | `src/knowledge/frontend/src/features/sc06-datasources/**`＋`src/knowledge/backend/**` | #516 / 裁定 | 随時 → 4 | `fix/fr-05-department-supply` | — |
| #516 | 必須文書属性が付与されていない | fix | **blocked** | PR #753 は着地したが受け入れ基準 3 件が未達。**`AST#520`（別リポ）と #451 に従属** | 2026-08-15 / #516 | `src/knowledge/backend/**`・`scripts/measure-abac-combinations.js` | `AST#520` / #451 | 5 | `fix/fr-05-abac-required-attributes` | — |
| #546 | Alertmanager 未配備で予算超過を検知不能 | fix | **blocked** | **暫定措置 39〜41 は PR #666 で完了済み。残るは Alertmanager の配備時期＝実環境の判断** | 2026-08-15 / #546 | `deploy/**`・`docs/operations/llm-cost-monthly-review-runbook.md` | 実環境 | 5 | `fix/nfr-alertmanager-deploy` | — |
| #600 | FR-22 利用者本人への通知 | feat | **blocked** | pin 前提は解消したが、**発火源 3 種が FR-19/20（#451）に属し、SMTP 基盤も未実装**。本体は backend | 2026-08-15 / #600 | `src/platform/backend/**`・`docs/adr/`・`docs/api/openapi.yaml` | #451 | 5 | `feat/fr-22-user-notification` | — |
| #466 | E2E スモークを統合スタックで CI 実行 | chore | **blocked** | 主題が実行環境であり **#442 の成果に載る**。統合スタック（Istio＋Keycloak＋BFF）が要る | 2026-08-15 / #466 | `.github/workflows/`・`src/platform/frontend/e2e/**`・`scripts/k8s-local-up.sh` | **#442** | 5 | `chore/nfr-e2e-smoke-integrated-stack` | — |
| #388 | Headlamp OIDC を HTTPS 化と同時に有効化 | feat | **blocked** | k8s 1.30+ が issuer に https を強制。**全経路 HTTPS 化（#442）と同時にしか解けない** | 2026-08-15 / #388 | `deploy/local/**`・`deploy/keycloak/*-realm.json`・`docs/adr/IADR-0084_*.md` | **#442** | 5 | `feat/nfr-headlamp-oidc-https` | — |
| #271 | Headlamp を k8s 管理 UI として導入 | feat | **blocked** | 同上（issuer 到達性）。**dev クラスタの稼働が前提**で、#388 と資源が重なる | 2026-08-15 / #388 | `deploy/local/**`・`deploy/keycloak/*-realm.json` | **#388** | 5 | `feat/nfr-headlamp-k8s-ui` | — |
| #380 | Opus 5 実運用値の確認 | track | **blocked** | 出力トークンの実測・レート制限枠の確認はいずれも**稼働環境でしか測れない** | 2026-08-15 / #380 | `src/platform/backend/**`（LlmGateway）・`docs/adr/IADR-0101_*.md` | 実環境 | 5 | `track/nfr-opus5-production-values` | — |
| #336 | Ruri v3 実配備・nDCG@10 実測 | track | **blocked** | 実モデルの load・疎通・nDCG 実測・ゼロ保持認定がすべて**稼働環境依存** | 2026-08-15 / #336 | `deploy/helm/**`・`deploy/docker-compose.yml` | 実環境 | 5 | `track/nfr-selfhosted-embedding-rollout` | — |
| #442 | エッジ・実行基盤・CI/CD の再構築 | feat | **blocked** | **隘路。** k3s／Istio／ArgoCD の実クラスタが要り、`/verify` を満たす手段が無い | 2026-08-15 / #442 | `deploy/**`・`.github/workflows/**`・`scripts/k8s-local-*.sh` | なし（**他が従属**） | 5 | `feat/nfr-edge-runtime-cicd` | — |
| #455 | バックエンド層標準への全面移行 | feat | **blocked** | **隘路。** 両ユニットの全 backend に一律適用。**dotnet 不在で 1 行も検証できない** | 2026-08-15 / #455 | `src/platform/backend/**`・`src/knowledge/backend/**`・`src/Directory.Packages.props` | なし（**他が従属**） | 5 | `feat/nfr-backend-app-layer-standard` | — |
| #441 | メッセージング・サービス間通信基盤 | feat | **blocked** | backend 全域（Wolverine＋RabbitMQ/Kafka）。dotnet 不在 | 2026-08-15 / #455 | `src/*/backend/**`・`src/platform/backend/Shared/**` | **#455** | 5 | `feat/nfr-messaging-inter-service` | — |
| #438 | 認証認可（Keycloak＋ABAC）の再実装 | feat | **blocked** | backend 全域。SC-13〜16 のテーマも含む。dotnet 不在 | 2026-08-15 / #748 | `src/platform/backend/**`・`deploy/keycloak/**` | **#455** / **#442** | 5 | `feat/fr-05-authz-keycloak-abac` | — |
| #439 | BFF セッション方式（Token Handler）移行 | feat | **blocked** | **go-live ブロッカー。** BFF 実装は backend であり dotnet 不在 | 2026-08-15 / #454 | `src/platform/backend/Bff/**`・`src/platform/frontend/src/foundation/auth/**` | **#438** | 5 | `feat/nfr-bff-session-token-handler` | — |
| #440 | LLM ゲートウェイの再実装 | feat | **blocked** | backend（用途別モデル割当・フォールバック）。dotnet 不在 | 2026-08-15 / #454 | `src/platform/backend/Services/LlmGateway/**` | **#455** | 5 | `feat/fr-11-llm-gateway` | — |
| #443 | 可観測性・運用の再実装 | feat | **blocked** | backend の計測点＋Grafana。**#546 / #380 のしきい値確定の前提でもある** | 2026-08-15 / #546 | `src/*/backend/**`・`deploy/grafana/**` | **#455** / **#442** | 5 | `feat/fr-10-observability-ops` | — |
| #444 | 構成変更容易性の再実装 | feat | **blocked** | backend（宣言的パイプライン・プラグイン）＋SC-11。dotnet 不在 | 2026-08-15 / #454 | `src/knowledge/backend/**`・`src/knowledge/frontend/src/features/sc11-*/**` | **#455** | 5 | `feat/fr-14-composability` | — |
| #445 | MCP サーバー統合の再実装 | feat | **blocked** | backend（宣言的公開構成・個人資料の一律除外）。dotnet 不在 | 2026-08-15 / #748 | `src/knowledge/backend/Services/McpService/**` | **#455** | 5 | `feat/fr-16-mcp-server-integration` | — |
| #446 | SPA 基盤の再実装（第 3〜4 段） | feat | **blocked** | 第 1・2 段は着地済み。**残段は #439（BFF）と第 4 段に従属**し、完了条件は #452 待ち | 2026-08-15 / #493 | `src/platform/frontend/**` | **#439** / #452 | 5 | `feat/adr-0031-spa-foundation-remaining` | — |
| #447 | 取り込み・変換・コネクタの再実装 | feat | **blocked** | backend 全域。**#752 / #754 の器そのものを作る側**。dotnet 不在 | 2026-08-15 / #516 | `src/knowledge/backend/Services/{Ingestion,DataSource}Service/**` | **#455** | 5 | `feat/fr-01-ingest-convert-connectors` | — |
| #448 | 検索・RAG・AI 分析の再実装 | feat | **blocked** | backend 全域（ハイブリッド検索・根拠付き回答）。dotnet 不在 | 2026-08-15 / #454 | `src/knowledge/backend/Services/{Search,Rag}Service/**` | **#455** / **#440** | 5 | `feat/fr-03-search-rag-analysis` | — |
| #449 | 文書管理・Wiki 閲覧の再実装 | feat | **blocked** | **前提検証は #602 で完了し ADR-0046 が確定**。残るは backend 本体で dotnet 不在 | 2026-08-15 / #602 | `src/knowledge/backend/Services/{Document,Wiki}Service/**` | **#455** | 5 | `feat/fr-06-document-wiki` | — |
| #450 | 知識グラフ・AI 提案の新規実装 | feat | **blocked** | **保留は解除済み**（前提 ADR 5 件が `Accepted`）。残る制約は GraphService＝backend で dotnet 不在 | 2026-08-15 / 本書 | `src/knowledge/backend/Services/GraphService/**`・`src/knowledge/frontend/src/features/sc18-*/**` | **#455** | 5 | `feat/fr-17-knowledge-graph` | — |
| #451 | 個人資料と Obsidian 双方向同期 | feat | **blocked** | **保留は解除済み**（ADR-0037 の着手可否注記が 2026-08-15 に「解消」）。残る制約は backend で dotnet 不在 | 2026-08-15 / 本書 | `src/knowledge/backend/**`・`src/knowledge/frontend/src/features/sc19-*/**`・Obsidian プラグイン | **#455** / #447（FR-21） | 5 | `feat/fr-19-private-note-obsidian` | — |
| #452 | knowledge 全画面（SC-01〜21）の再実装 | feat | **blocked** | frontend 単独では動くが、**画面が読む契約が backend 側で未確定**。第 4 段の技術 3 種も未導入 | 2026-08-15 / #493 | `src/knowledge/frontend/src/features/**` | **#446** / #447〜#451 | 5 | `feat/sc-01-knowledge-screens` | — |
| #453 | 退行防止テスト基盤 | chore | **blocked** | 子 8 件中 7 件は着地済み。**残る #466 が #442 に従属**し、backend 側の床は dotnet 不在 | 2026-08-15 / #453 | `docs/tests/**`・`scripts/check-*.js`・`src/vitest.config.ts` | **#466** | 5 | `chore/nfr-regression-test-foundation` | — |
| #457 | 再実装版への切替計画 | chore | **blocked** | 最終フェーズ。**go-live は #439 完了が前提**で、破棄判断には利用者承認が要る | 2026-08-15 / #457 | `docs/specs/`（migration）・`deploy/argocd/**` | **#439** / #456 | 5 | `chore/nfr-cutover-plan` | — |
| #458 | セキュリティ暫定運用の解消 | feat | **blocked** | STRICT mTLS・Vault 集中管理はいずれも**実クラスタが要る**。go-live 条件 | 2026-08-15 / #458 | `deploy/**`・`src/*/backend/**` | **#442** / **#438** | 5 | `feat/nfr-security-interim-resolution` | — |
| #454 | 【親】全面再実装のトラッキング | 親 | **親** | 子 51 件のトラッキング。**それ自体では着手先にならない**（閉じるのは全フェーズ完了時） | 2026-08-15 / #454 | 本文のみ | — | — | （なし） | — |

### 判定の内訳（自分で数えた値）

| 判定 | 件数 | 番号 |
| --- | ---: | --- |
| **即着手可** | **2** | #747 / #749 |
| **条件付き可** | **4** | #748 / #756 / #757 / #493 |
| **decision-needed** | **4** | #743 / #572 / #752 / #754 |
| **blocked** | **27** | #516 / #546 / #600 / #466 / #388 / #271 / #380 / #336 / #442 / #455 / #441 / #438 / #439 / #440 / #443 / #444 / #445 / #446 / #447 / #448 / #449 / #450 / #451 / #452 / #453 / #457 / #458 |
| **親** | **1** | #454 |
| **合計** | **38** | 走査の `totalCount` と一致 |

> **blocked 27 件のうち 16 件は「XL（dotnet 不在で今回は着手不可）」である**（#442 / #455 / #441 / #438 /
> #439 / #440 / #443 / #444 / #445 / #446 / #447 / #448 / #449 / #450 / #451 / #452 / #453 のうち #453 を含め
> 17 件。うち #453 は文書側が先行できるため下表で別扱いとする）。**残る 11 件は実環境待ち・別リポ依存・利用者承認待ち**である。
> **判定の語彙は 5 値であり「XL」は判定ではなく波 5 の内訳**として扱う。

## 5. 波の割り当て

| 波 | 対象 | 並列 / 直列 | 根拠 |
| --- | --- | --- | --- |
| **波 1** | **#747・#749** | **並列（条件つき）** | 資産が分かれている。**ただし #747 が回帰テストを `scripts/scripts.repo.test.js` に置くなら直列化する**（§6） |
| **波 2** | **#748** | 単独 | `scripts/scripts.repo.test.js` で #749 と交差するため、波 1 の後 |
| **波 3** | **#756 → #757** | **直列** | #757 は #756 の優劣判定の結論で射程が決まる。かつ両者とも kit と突き合わせる |
| **波 4** | **#493（Renovate / Husky 先行分）・#754（SC-06 フォーム分）** | 並列 | 資産が `src/package.json` 系と `src/knowledge/frontend/**` で交差しない |
| **波 5** | **XL 群 16 件＋実環境待ち 11 件** | **文書化のみ** | **子 issue は起票しない。** 本書の §4 と §6 が着手ゲートの記録であり、番号を増やしても消化は進まない |
| **随時** | **#743・#572・#752・#754 の写像規則** | — | `decision-needed` ラベルで **planning へ小さく高頻度に流す**（運用ガイド §拘束点） |

> **波 2 が #748 の 1 件しかないのは、下敷きが波 2 に置いていた #546 を blocked へ移したためである**（§7 食い違い 1）。

## 6. 交差の根拠 —— `scripts/scripts.repo.test.js` は単一ファイルである

**並列可否は「宣言済みファイル領域の非重複」で機械的に判定する**（運用ガイド）。
本波で交差する唯一の資産が `scripts/scripts.repo.test.js`（**1 ファイル・389,473 バイト**）である。
**#748 / #749 / #756 が全部そこに書く。**

```console
$ grep -n "check-test-traceability\|check-planning-pin-freshness\|check-commit-messages" \
    scripts/scripts.repo.test.js | head
676:  // --- check-test-traceability: 受け入れ基準 → テストの写像（Issue #453） ---------
711:  // --- check-test-traceability: 逆方向検査（計画レンジ・Issue #472） --------------
768:  ok('check-test-traceability --self-test は exit 0（逆方向検査の正例・負例を含む）', ...
2815:        pathXrepo.join(__dirname, 'check-commit-messages.js')
2841:  // --- NFR / #579 / IADR-0145: check-commit-messages のレンジモードを実バイナリで通す ---
6085:    const pin = require('./check-planning-pin-freshness.js');
6087:    ok('check-planning-pin-freshness --self-test が通る', ...
```

| issue | 書き込む節（実測した行） |
| --- | --- |
| **#749** | `:6085-6180`（`check-planning-pin-freshness` の自己試験・fail-open・未 populate・ワークフロー結線） |
| **#748** | `:676-770` ＋ `:1244-1280`（`check-test-traceability` の写像・逆方向検査・自己試験） |
| **#756** | `:2690-2900`（`check-plan-id-qualification` / `check-cross-repo-refs` / `check-commit-messages` の 3 本） |
| **#747**（条件つき） | 回帰テストを置く場合。既存の `:1704-1720`（`check-chunk-budget`）と `:4293-4320`（ワークフロー走査）の近傍 |

**したがって #748 / #749 / #756 は直列化する**（波 1 → 波 2 → 波 3）。
**マージは FIFO で 1 本ずつ**（develop へ rebase → CI 通過 → マージ → 次の PR が rebase）。

> **★ #747 を波 1 で #749 と並べる条件**: **#747 の回帰担保を `scripts/scripts.repo.test.js` に置かないこと。**
> 同ファイル `:4300` は「**`paths:` の側は検査器にしない**」と既に述べており（frontend.yml は意図して
> `paths:` を持つ）、**#747 の受け入れは `.github/workflows/*.yml` の diff で示すのが素直**である。
> テストを置く判断をした時点で、**#747 は #749 の後ろへ回す**。

## 7. 下敷きと食い違った issue（測り直した結果）

**[IADR-0180](../adr/IADR-0180_blocked-judgments-expire.md) に従い、blocked を「前回できなかった」で据え置かなかった。**
本作業の指示文に添えられた下敷きと、本日の実測が食い違ったのは次の 5 点である。

### 食い違い 1 — **#546 は「条件付き可」ではなく blocked（実環境待ち）**

下敷きは #546 を波 2 の条件付き可に置いていた。**issue 本文と全 6 コメントを読んだところ、
暫定措置（計画 決定 39・40・41）は 2026-08-10 の PR #666 / `0296f1f` で全件着地済み**であり、
[IADR-0164](../adr/IADR-0164_llm-cost-monthly-review-interim-control.md) が `Accepted` である。
**実装するものは残っていない。** 残るのは Alertmanager の配備時期ただ 1 点で、これは実環境の判断である。
**issue 自身が 2026-08-15 のコメントで「#271 / #336 / #380 / #388 / #466 と同じ性質」と自己分類している。**
本 issue が open なのは、3 つの ADR（IADR-0164 / 0165 / 0168）から名指しされた**追跡アンカー**だからである。

### 食い違い 2・3 — **#450 / #451 の保留は解除済み。blocked の根拠が変わった**

下敷きは両者を blocked に置いていた。**その根拠（[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md)
決定 2 = 前提 ADR が `Proposed`）は、pin `4d6a7d6` で実測すると成立していない。**

```console
$ git -C planning log --oneline -1
4d6a7d6 docs: 必読規約の母集合を定め、分類 C を「埋めているか」で切る（#363 #364） (#365)

$ grep -m1 "状態:" planning/projects/microservices-platform/07_adr/ADR-003{3,4,5,6,7}_*.md
ADR-0033 … - 状態: Accepted
ADR-0034 … - 状態: Accepted
ADR-0035 … - 状態: Accepted
ADR-0036 … - 状態: Accepted
ADR-0037 … - 状態: Accepted
```

さらに **`ADR-0037` の「着手可否の注記」は 2026-08-15 に「解消」へ書き換わっている。**

> **★［2026-08-15］抱えていた前提は検証され、解消した。** …… **留保は外れ、FR-19 / FR-20 は
> SC-19 の本文編集導線を除外せずに着手してよい**（導線そのものが存在しないため）。

**したがって #450 / #451 は「計画側の保留」で止まってはいない。**
両者が波 5 に居る理由は **backend 実装を含み dotnet が無いこと 1 点だけ**である。**根拠を差し替えた。**

### 食い違い 4 — **#449 の Wiki.js 前提検証は「#450/#451 の凍結解除の前提」ではない。既に完了している**

下敷きは「#449 の Wiki.js 前提検証が #450/#451 の凍結解除の前提」としていた。**実測ではその検証は
#602 として #449 から切り出され、2026-08-15 に完了している**（`docs/specs/20260815_issue-602_wikijs-personal-scope-spike.md`・
`status: done`）。結果は「**編集導線では成立しない**」で、計画側は **`ADR-0046`（個人資料を Wiki.js へ
同期しない・`Accepted`）** を起案して決着させた。**#449 は依然 XL だが、その理由は前提検証ではなく backend 本体である。**

### 食い違い 5 — **本 worktree では submodule `planning` が populate されていない**

下敷きは「submodule `planning`（pin `4d6a7d6`）と `src/ai-stock-trading`: populate 成功」としていた。
**本 worktree（`/home/user/wt-stocktake`）では `planning/` は空である。**

```console
$ git submodule status
-4d6a7d6274373140b85679f1eab1a3d02890f026 planning        ← 先頭の "-" = 未初期化
-7f69fb507f5dc6c99c06efeb702869f94c6aa30d src/ai-stock-trading

$ git -C planning rev-parse --short HEAD
818923e                                    ← 親リポへ抜けている。planning のものではない
$ git -C planning remote -v
origin  https://github.com/endazon/microservices-platform (fetch)   ← 計画リポではない
```

**`git -C planning …` は空ディレクトリを素通りして親リポを答える。** #749 が問題にしている
「比較対象がどこから来たか出力に出ていないと気づけない」と**同型の罠**である。
計画 ADR の状態は隣接クローン `/home/user/microservices-platform/planning`（`4d6a7d6`・
`origin` = `project-planning`）で測った。**#756 は `planning/tools/impl-handoff-kit/HOWTO.md` を読む必要があるため、
着手前に populate すること**を条件に加えた。

> なお下敷きは「planning リポの HEAD は `5e53b9d`」としていたが、**本セッションからは確認できない**
> （隣接クローンは pin と同じ `4d6a7d6` にあり、`5e53b9d` はオブジェクトとして存在しない）。**転記しない。**

### 食い違いに当たらなかったもの

- **#516 は blocked のままである。** ただし根拠が変わった —— PR #753 は着地しており、
  残る未達は受け入れ基準 3 件で、**`AST#520`（別リポ）と #451 に従属する**。「未着手だから blocked」ではない。
- **#600 の pin 前提は解消している。** 起票時「`891b199` 未達」とされていた pin は現在 `4d6a7d6` であり
  `FR-22` は見える。blocked の根拠は **#451 従属と SMTP 基盤の不在**へ移った。
- **#572 の施策 1（束ね）は既に裁定済みである。** [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md)
  決定 1 が 6 条件・上限（名目 3 件・実効 2 件）を定めた。**decision-needed として残るのは施策 3（監査の重さ）**である。

## 8. XL の着手ゲート

各 XL issue について 3 条件を ○× で示す。**3 条件すべてが ○ になるまで着手しない。**

| issue | dotnet が入っている | 関連 ADR が Accepted | 先行 issue が develop に着地 | 隘路との関係 |
| --- | :---: | :---: | :---: | --- |
| **#455** | **×** | ○（ADR-0020 / 0027 / 0029 / 0030） | ○（先行なし） | **隘路 1。** #441 / #438 / #440 / #443 / #444 / #445 / #447 / #448 / #449 / #450 / #451 の全部が従属 |
| **#442** | **×** | ○（ADR-0021 / 0005 / 0007 / 0008 / 0023 すべて Accepted。**ADR-0023 は 2026-08-10 に確定済み**） | ○（先行なし） | **隘路 2。** #466 / #388 / #271 / #458 / #443 が従属。**実クラスタも要る**（blocked の理由はこれ 1 本） |
| #441 | **×** | ○（ADR-0027〜0029） | **×**（#455） | #455 従属 |
| #438 | **×** | ○（ADR-0036） | **×**（#455 / #442） | 隘路 2 本の合流点 |
| #439 | **×** | ○（ADR-0032 / 0026） | **×**（#438） | **go-live ブロッカー。** #457 / #446 / #493 が従属 |
| #440 | **×** | ○（ADR-0038 / 0025） | **×**（#455） | #448 が従属 |
| #443 | **×** | ○（ADR-0006 / 0044） | **×**（#455 / #442） | **#546 / #380 のしきい値確定の前提** |
| #444 | **×** | ○（ADR-0018） | **×**（#455） | — |
| #445 | **×** | ○（ADR-0024） | **×**（#455） | — |
| #446 | **×**（BFF 側） | ○（ADR-0031） | **×**（#439） | 第 1・2 段は着地済み。**完了条件は #452** |
| #447 | **×** | ○（ADR-0036 / 0012） | **×**（#455） | **#752 / #754 の器を作る側** |
| #448 | **×** | ○（ADR-0017 / 0035） | **×**（#455 / #440） | — |
| **#449** | **×** | ○（ADR-0011 / 0014 / 0015・**ADR-0046 で前提検証は決着**） | **×**（#455） | **★ 下敷きが「#450/#451 の凍結解除の前提」とした Wiki.js 検証は #602 で完了済み。もはや前提ではない** |
| **#450** | **×** | ○（**ADR-0033 / 0034 は Accepted**。ADR-0039 のみ `Proposed`） | **×**（#455） | **★ IADR-0119 の保留は解除済み。残る × は dotnet だけ** |
| **#451** | **×** | ○（**ADR-0036 / 0037 は Accepted。ADR-0037 の着手可否注記は 2026-08-15 に「解消」**） | **×**（#455 / #447） | **★ 同上。ADR-0046 が編集手段を確定させた** |
| #452 | **×**（読む契約が backend 側） | ○（ADR-0031・SC-01〜21） | **×**（#446 / #447〜#451） | **#446 第 2 段と #493 の完了条件** |
| #453 | **×**（backend 床） | ○（NFR） | **×**（#466 → #442） | 子 8 件中 7 件は着地済み |

### 依存順序（要点）

1. **#455 と #442 が隘路である。** 前者は backend 全域の設計様式とライブラリ標準を決め、後者は
   全 XL が載る実行基盤を作る。**この 2 本が動かない限り、下流 14 本のどれも着手条件を満たさない。**
2. **#449 の Wiki.js 前提検証は、もはや #450 / #451 の凍結解除の前提ではない。**
   検証は **#602 で完了**し（2026-08-15）、`ADR-0046`（`Accepted`）が「個人資料は Wiki.js へ同期しない・
   本文編集は Obsidian 経路に限る」と確定させた。**#450 / #451 のゲートで残っている × は dotnet 1 本だけ**である。
   —— **本項は下敷きの記述を実測で置き換えたものである**（§7 食い違い 2・3・4）。
3. **#439 は go-live ブロッカー**であり、#457（切替）・#446（SPA 残段）・#493（第 5 段）が後ろに連なる。

## 9. 受け入れ基準

- [x] OPEN 件数を**自分で引き直し**、走査コマンド・引数・引いた日時を本文に書いた（§2）
- [x] 除外したものと除外理由を書いた（§2。**除外 0 件であることを明示**）
- [x] 表が **11 列**で、`PR 番号` / `open|closed` / `CI 状態` の列を持たない（§4）
- [x] **blocked を「前回できなかった」で据え置かず、5 件の食い違いを実測で示した**（§7。IADR-0180）
- [x] 波の割り当てと、交差の根拠（`scripts/scripts.repo.test.js` が単一ファイル）を書いた（§5・§6）
- [x] XL の着手ゲートを 3 条件 × 17 件で示し、隘路と依存順序を書いた（§8）
- [x] **`CLAUDE.md` と `.claude/rules/` に 1 バイトも足していない**（必読規約 50KB 予算が 98%）
- [x] 波 5 の XL 群について**子 issue を起票していない**

## 10. テスト方針

**本作業は文書 1 枚であり、コードを変更しない。** 検証は文書検査器で行う（§11）。
波 1 以降の各 issue のテストは、それぞれの作業仕様書が受け入れ基準を写像する。

## 11. 検証結果

走らせたコマンドと結果（2026-08-15 実測）。

```console
$ node scripts/check-doc-links.js
[check-doc-links] OK: 624 件のリンクを検査し、破損はありません。
（未 populate の submodule 配下 1293 件は対象外）

$ node scripts/check-doc-status-vocabulary.js
[check-doc-status-vocabulary] OK: 584 件

$ node scripts/check-doc-type-vocabulary.js
[check-doc-type-vocabulary] OK: 598 件

$ node scripts/check-cross-repo-refs.js
[check-cross-repo-refs] OK: 1610 件

$ node scripts/check-plan-id-qualification.js
[check-plan-id-qualification] OK: 1323 件

$ node scripts/check-doc-updated.js
[check-doc-updated] OK
```

`CLAUDE.md` と `.claude/rules/` は 1 バイトも変更していない（`check-reading-budget.js` は
50,193 / 51,200 = 98% の既存 warn のまま。本書に起因する増加はない）。

## 計画書との差異

- 差異: **なし**。本書は計画書の内容を実装へ写像するものではなく、**実装リポ内の作業順序を決める棚卸し**である。
  計画側へ環流すべき論点（#743 / #572 / #752 / #754 の写像規則）は `decision-needed` として §5 に分離した。

## 未決事項

1. **#747 の回帰担保をどこに置くか。** `scripts/scripts.repo.test.js` に置くなら波 1 の並列は崩れる（§6）。
2. **#748 の突合材料（案 A 事前生成 JSON / 案 B CI 限定）。** 着手時に IADR で確定する。
3. **#493 の先行導入（Renovate / Husky のみ）を認めるか。** 認めると 1 issue = 1 PR
   （[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1）から外れる。
   **[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) の束ね条件は「同型な契約追加」に限られ、本件は当たらない。**
4. **本 worktree の submodule `planning` の populate。** #756 の着手前に必須（§7 食い違い 5）。
