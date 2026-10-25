---
title: 作業仕様書 — e2e-live の pull_request.paths を、試験される側（e2e-live が建てるイメージと配備物）まで広げる（#2244）
type: spec
status: done
related_ids: [NFR, NFR-30, IADR-0569, IADR-0625, IADR-0635]
author: claude
created: 2026-10-25
updated: 2026-10-25
issue: "#2244"
---

# 作業仕様書 — e2e-live の paths（#2244）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。発端は #2243（BFF の鍵リングの修正）。ログインの経路を直す PR なのに e2e-live が起動せず、
> 手動の workflow_dispatch で確かめた。

## 受け入れ基準（#2244 ＋ オーケストレーターの条件 2026-10-25）

- [x] BFF だけを変える PR で e2e-live が起動する
- [x] docs だけ・`.ai-context` だけを変える PR では起動しない
- [x] paths の一覧（足すもの・外すもの）を `scripts.repo.test.js` で固定する（代表の変更で起動する／しないを判定する）
- [x] 必須チェックが恒久 pending にならない（e2e-live は必須ではない）ことを確かめ、根拠を書く

## 母集合の取り方（規則 9: 記憶で挙げない）

e2e-live（`ro,rw`）が**実際に建てて配備するもの**から引いた。根拠は 3 つ。

1. **run の実測**（#2241 の run 40184060930 のジョブのログ）: `k8s-local-up.sh --live` の `[2/7] build & import images` が建てたイメージ 20 個
   （aianalysis・authorization・bff・configuration・conversion・dashboard・datasource・document・feedback・frontend・graph・ingestion・llm-gateway・
   market-monitor・mcp・notification・retrieval・risk-management・wiki の各サービスと platform-backup）。ログに出た配備物と検査器
   （`deploy/bootstrap/sc22-secret-items.json`・`deploy/local/{abac-seed,infra-tls,keycloak-setup,platform-backup/image,rabbitmq-setup,search-seed}`・
   `scripts/{k8s-local-up.sh,check-stack-ready.js,seed-abac-policies.js,seed-search-documents.js,gen-e2e-accounts.js,check-e2e-*.js}`）。
2. **イメージの中身**（`scripts/k8s-local-images.sh` の対応表が指す Dockerfile の `COPY`）:
   - バックエンド 15 個: `src/Directory.Build.props`・`src/Directory.Packages.props`・`src/platform/backend/`（15 個とも）・`src/knowledge/backend/`（11 個）。
     BFF はさらに `src/ai-stock-trading/backend/Bff/`・`deploy/bootstrap/sc22-secret-items.json`。AST の 3 個は `src/ai-stock-trading`（submodule）。
   - フロントエンド: `src/{package.json,pnpm-lock.yaml,pnpm-workspace.yaml,lingui.config.ts}`・`src/platform/frontend/`・`src/knowledge/frontend/`・
     `src/packages/ui/`・`src/ai-stock-trading/frontend/`。
   - platform-backup: `deploy/local/platform-backup/image/`。
3. **起動器が当てる配備物**（`scripts/k8s-local-up.sh`。e2e-live の旗は `LOCALEDGE=1 ABACSEED=1 SEARCHSEED=1 LOCALEMBED=1`、`ISTIO`・`VAULT`・`ESO`・
   `ARGOCD`・`HEADLAMP`・`SYNTHETIC` は立てない）: `deploy/helm/microservices-platform/`・`deploy/local/`（下の除外を除く）・`deploy/keycloak/`・
   `deploy/mail-relay/`（近接 MTA・床・門）・起動器が読み込む `scripts/lib/`・`scripts/k8s-local-images.sh`。

## 足すもの・外すもの（判断。IADR-0635）

- **足す**: 上の母集合。バックエンドは**全ユニット**（`src/platform/backend/**`・`src/knowledge/backend/**`）。live の試験は BFF 越しに knowledge の
  サービスも読む（検索・個人資料・文書・管理リソース）ので、BFF と認証の経路だけに絞ると、knowledge の変更で live が壊れても PR で捕まらない。
  1 回 30 分は、変更が試験される側に入るときだけ払う（下の除外で、イメージに入らない変更は除く）。
- **外す**（`!` の否定。順序が効くので、足す行の後に置き、e2e の live の行を最後にもう一度足す）:
  - 試験だけの変更: `src/**/Tests/**`・`src/**/*.Tests/**`（バックエンドの試験プロジェクト。`dotnet publish` はサービスの csproj だけを建てる）、
    フロントエンドの単体試験 `src/**/*.test.ts`・`*.test.tsx`、モックの E2E `src/platform/frontend/e2e/**`（`e2e/live/**`・`e2e/support/**` は最後に戻す）。
  - 文書: `**/*.md`。
  - 起動器が e2e-live の旗では当てない配備物: `deploy/local/{vault,vault-persistence,argocd,headlamp,edge-istio,edge-istio-reset-floor,synthetic-monitor,reloader}/**`
    （それぞれ `VAULT=1`・`ARGOCD=1`・`HEADLAMP=1`・`ISTIO=1`・`SYNTHETIC=1`・`ESO=1` のときだけ）。
  - 足していないもの（もともと起動しない）: `docs/**`・`.ai-context/**`・`deploy/istio/**`・compose だけの設定（`deploy/{alertmanager,grafana,prometheus,…}`）・
    ほかの workflow。
- **必須チェック**: e2e-live は必須ではない（`docs/ai-workflow.md` の必須の表で取り消し線。paths で絞る workflow を必須にすると恒久 pending になるため）。
  ルールセット `develop-rule` の必須 check は `image-build` だけ（同表の注記）。よって起動しない PR が pending で止まることは無い。

## 変更するファイル

- `.github/workflows/e2e-live.yml`（`pull_request.paths`）
- `scripts/scripts.repo.test.js`（paths の固定と、代表の変更での起動／非起動）
- IADR-0635・`.ai-context/adr/README.md`

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1200 passed（新規 1: 起動する 21 例・起動しない 13 例・土台の実在・行の順序）。
  変異: `src/platform/backend/**` を消すと「起動しない: src/platform/backend/…」、`!src/**/*.Tests/**` を消すと「起動してしまう: …Tests…」で赤。
- `check-workflow-job-refs.js`・`check-ai-workflow-config.js`・`check-ci-latency.js`: 緑。文書の検査: 緑。
- PR 自身の e2e-live（`.github/workflows/e2e-live.yml` を変えたので新しい paths で起動。head aeb9bde4・run 40188650131・層 ro,rw）: success。
  55 passed・skip 0・失効 6・走査 0（成果物 2 ファイルに資格情報 12 個・発行値 8 個の値は無い）・sweep の dry-run は残骸 0 件・後始末済み 2 件。
