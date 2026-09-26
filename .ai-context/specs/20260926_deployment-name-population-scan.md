---
title: 作業仕様書 — 文書・スクリプト中の Kubernetes Deployment 名の誤りを母集合で洗い出して直す
type: spec
status: done
related_ids: [FR-13, ADR-0007, ADR-0011]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
related_specs:
  - 20260926_issue-1558_runbook-nits
issue: "1558"
---

# 作業仕様書 — Deployment 名の母集合走査

## 起点

- 所有者の報告: `docs/operations/secret-item-console-injection-runbook.md` 163-164 行の `deploy/llm-gateway` は
  実在しない Deployment 名である。**この 1 件は #1558 / PR #1559（`82801440`）で `deploy/llmgateway-service` へ直し済み**
  （`origin/develop` で 163-164 行を読んで確認）。本作業はやり直さない。
- 本作業は所有者が併せて求めた**残りの母集合走査**である（規則 9・10: 誤りの側の文字列で全追跡ファイルを引く）。
- 文書・スクリプトだけの変更。稼働クラスタには一切触れない（kubectl / helm install / nerdctl / port-forward を実行しない）。

## 手順

1. 実在する名前を宣言から導く（chart・`deploy/local/**`・`deploy/mail-relay/**`・AST chart）。
2. 誤りの側の書式（`deploy/<x>`・`deployment/<x>`・`deployment.apps/<x>`・`ds/<x>`・空白区切りの
   `get deploy <x>`・`-l app=<x>`・`"deploy/$var"` のループ）で追跡下の全ファイルを走査する。
3. ヒットごとに「名前が名前空間に実在するか」を判定し、誤りだけを直す。結果と除外理由を本書へ書く。

## 1. 実在する名前（宣言から導出。稼働クラスタは見ていない）

| 名前空間 | 種別 | 名前 | 出典 |
| --- | --- | --- | --- |
| `microservices-platform` | Deployment | `<key>-service`。key = `document` `datasource` `conversion` `ingestion` `retrieval` `aianalysis` `authorization` `wiki` `llmgateway` `dashboard` `graph` `notification` `mcp` `feedback` `bff`（既定 enabled）＋ `configuration` `risk-management` `market-monitor`（既定 disabled） | `templates/deployment.yaml` 12 行 `name: {{ $name }}-service` × `values.yaml` の `services:` キー |
| 同上 | Deployment | `frontend-service` | `templates/frontend.yaml`（`edge.frontend.service`） |
| 同上 | Deployment | `embedding-service`（`embedding.enabled=true` のときだけ） | `templates/embedding.yaml` |
| 同上 | Deployment | `seaweedfs` / `wiki-js` | `templates/seaweedfs.yaml` / `templates/wikijs.yaml` |
| 同上 | Job | `config-drift-postsync` | `templates/drift-postsync-job.yaml` |
| 同上 | Deployment | `synthetic-monitor`（`SYNTHETIC=1`） | `deploy/local/synthetic-monitor/synthetic-monitor.yaml` |
| `platform-infra` | Deployment | `postgres` `rabbitmq` `redis` `keycloak` `qdrant` `otel-collector` `mailpit` `vault` `headlamp` `alertmanager` `grafana` `loki` `prometheus` `tempo` `mail-relay` `reset-gate` `reset-floor` | `deploy/local/{infra,observability,vault,headlamp}/*.yaml`・`deploy/mail-relay/**` |
| 同上 | DaemonSet | `inotify-sysctl` | `deploy/local/infra/inotify-sysctl.yaml` |
| `ai-stock-trading` | Deployment | `<key>-service`（`trade-decision` `order-execution` `risk-management` 等）・`opend` | AST chart（submodule pin `471cbf31` の `deploy/helm/ai-stock-trading/templates/{deployment,opend}.yaml` を隣接クローンで `git show`） |
| `kube-system` / `argocd` / `cert-manager` / `istio-system` | Deployment | `coredns` / `argocd-server` / `cert-manager`・`cert-manager-webhook` / `istiod` | 上流配布物の既定名（本リポは宣言を持たない。名前の誤りの判定対象外とし、名前空間の組だけ見た） |

- compose（`deploy/docker-compose.yml`）のサービス名は k8s と違うものがある: `llm-gateway` `bff` `frontend` `embedding`
  （k8s は `llmgateway-service` `bff-service` `frontend-service` `embedding-service`）。**compose の文脈では正しい**ので誤りに数えない。
- コンテナ名も併せて確かめた: chart の各サービスは `{{ $name }}-service`（`deployment.yaml` 53 行）、`mail-relay` は
  `postfix` / `queue-exporter`、`reset-gate` は `gate`、`wiki-js` は `wiki-js`。

## 2. 走査（誤りの側の書式で追跡下の全ファイル。値は `origin/develop` `82801440` 時点・本書を書く前）

除外（パスで）: `CHANGELOG.md`（生成物）・`src/ai-stock-trading`（submodule。この worktree では未展開）・
`.ai-context/specs/`・`.ai-context/superpowers/`（凍結記録。書き換えない。軸 A だけ別に数えて下に列挙）。

| 軸 | コマンド（`git grep -nIE <pattern> -- . ':!CHANGELOG.md' ':!src/ai-stock-trading' ':!.ai-context/specs' ':!.ai-context/superpowers'`） | 行数 | 判定 |
| --- | --- | --- | --- |
| A: `種別/名前` | `(^\|[^A-Za-z0-9_./-])(deploy\|deployment\|deployments\|deployment\.apps\|statefulset\|sts\|daemonset\|ds)/[A-Za-z0-9][A-Za-z0-9-]*([^A-Za-z0-9/-]\|$)` | 314 | 114 行はファイルパスだけ（`deploy/docker-compose.yml` 87・`deploy/create-multiple-dbs.sh` 8・`deploy/helm` 10・`deploy/prometheus.yml` 5・`deploy/otel-collector-config.yaml` 4・`deploy/local` 7 ほか。末尾が `/` か `.拡張子`）。残る 200 行を 1 行ずつ読んだ → **誤り 1**（下表 #1） |
| B: 空白区切り（`get deploy <x>` 等） | `(rollout (restart\|status\|…)\|scale\|set (env\|image\|resources)\|describe\|port-forward\|logs\|exec\|get\|delete\|…) +(-opt val +)*(deployment\|deploy\|…) +[a-z][a-z0-9-]*` | 17 | すべて実在（`get deploy minio` は「NotFound であること」を確かめる切替後の確認で、正しい） |
| C: ラベル選択子 | `(-l\|--selector)[ =]+'?"?app(\.kubernetes\.io/name)?=[a-z0-9-]+` | 10 | すべて実在（`app: <name>` ラベルを manifest で確認。AST は `app: {{ $name }}-service`） |
| D: 変数で組む名前 | `(deploy\|deployment\|deployments\|statefulset\|daemonset)/[$\{<]` | 21 | 変数の値を読んだ（`WIKI_DEPLOY=wiki-js`・`SYNC_DEPLOY=wiki-service`・`KEYCLOAK_DEPLOY=keycloak`・`for d in bff dashboard aianalysis` → `deploy/$d-service`・`for d in seaweedfs llmgateway-service wiki-service wiki-js …`・`target.deploy='mailpit'`）→ すべて実在 |
| E: 散文の「`名前` Deployment」 | (E1) `(Deployment\|StatefulSet\|DaemonSet\|デプロイメント)( 名は\| 名\| の名前は)? ?` + バッククォート名、およびその逆順（全体）／(E2) `git grep -nIE "Deployment\|StatefulSet\|DaemonSet" -- docs scripts deploy src/platform src/knowledge .github .claude AGENTS.md README.md ':!**/*.cs'` の出力のうち `llm-gateway\|mcp-server\|minio\|wikijs\|wiki` かバッククォートのサービス key を含む行 | E1 12 ／ E2 9（直した後の値。E2 は語の入った行を拾う補助軸で、主軸は A〜D） | **誤り 1**（下表 #2。バッククォートなしの `wikijs Deployment`）。他は実在 |
| F（隣接）: Service 参照 `svc/<x>` | `(svc\|service\|services)/(llm-gateway\|bff\|frontend\|embedding\|minio\|wiki\|…)` | 1 | `svc/minio` は MinIO を残した**切替前の稼働クラスタ**で中身を数える手順で、正しい |
| G（compose 名の混入） | `\bllm-gateway\b -- docs scripts deploy/local deploy/helm deploy/mail-relay deploy/argocd deploy/istio .github README.md` | 15 | すべて compose サービス名・イメージ名・Meter 名・「compose の名前である」と断る注記。k8s の資源参照として使う行は無い |

- 名前空間の組も全ヒットで見た: 基盤のアプリ・`wiki-js`・`seaweedfs`・`synthetic-monitor` は `microservices-platform`、
  インフラは `platform-infra`（`$INFRA_NS` 既定値）、AST の `trade-decision-service` `order-execution-service`
  `risk-management-service` `opend` は `ai-stock-trading`、`coredns` は `kube-system`。**名前空間の取り違えは 0 件。**
- `-n` を持たない行: `operations.md` の #1 と、`.ai-context/adr/IADR-0327`（`kubectl exec deploy/wiki-js`。名前は実在）、
  検査器の自己テストの文字列（`check-test-spec-coverage.js` の `kubectl logs deploy/wiki-js`・`excluded-units.js` の `deploy/x`。
  資源参照ではなく入力例）。

### 誤り（直したもの）

| # | 場所 | 誤り | 直し | 根拠 |
| --- | --- | --- | --- | --- |
| 1 | `docs/operations/operations.md` 475 行（Wiki.js API キーのローテーション） | `kubectl rollout restart deployment/wiki` | `kubectl -n <namespace> rollout restart deployment/wiki-service` | キーは `wiki` サービスの `WikiJs__ApiKey`（`values.yaml` 614 行 `wikijs-sync`）が読む → Deployment は `wiki-service`。`-n <namespace>` は同じ節の `kubectl create secret … -n <namespace>` と揃えた |
| 2 | 同 468 行 | `wikijs Deployment が参照` | `wiki-js Deployment が参照` | `wikijs-db` を `DB_PASS` で読むのは `templates/wikijs.yaml` の Deployment `wiki-js`（8・39-43 行） |

- `docs/` の表示テキストへ ID を足していない。`operations.md` の trace ブロックへ本書名と #1558 を足した（`updated:` は既に 2026-09-26）。

### 所有者の報告分（やり直さない）

- `docs/operations/secret-item-console-injection-runbook.md` 163-164 行 → #1559 で `deploy/llmgateway-service` 済み。
  同じ Runbook・`llm-output-token-measurement-runbook.md` の `llmgateway-service` 参照も軸 A で実在を確認した。

### 凍結記録（列挙のみ。書き換えない）

- 軸 A を `.ai-context/specs` `.ai-context/superpowers` に当てた行数は 250（本書は未追跡のため含まない）。実在しない名前は:
  - `deploy/llm-gateway` / `deployment/llm-gateway`: `20260926_issue-1558_runbook-nits.md` 40・63 行（誤りの記録そのもの）、
    `20260926_issue-380_output-token-measurement-runbook.md` 142 行（誤りを見つけた記録）。
  - `deploy/monitoring`（`superpowers/plans/2026-06-26-P0-foundation.md` 2539 行）・`deploy/argocd-foo`（`20260816_issue-817` 115 行。
    照合器の反例）: 資源参照ではない（パス・入力例）。
- `.ai-context/adr/` は live だが、ヒット（`deploy/conversion-service` `deploy/wiki-js` `deploy/prometheus` `deploy/argocd-server`
  `deploy/keycloak` `deploy/mail-relay` `deploy/reset-floor` `deploy/opend` `deploy/vault` `ds/inotify-sysctl`）はすべて実在で、追記は要らない。

### 範囲外で気付いたこと（直さない）

- `deploy/helm/microservices-platform/templates/deployment.yaml` 48 行と `values.yaml` 149 行が `deploy/secrets`（`deploy/secrets/README.md`）を
  指すが、そのディレクトリは無い。Deployment 名ではなくパスの参照切れなので本作業の対象外。

## 3. この変更で新たに誤りになる記述（規則 10）

- 直した後に `deployment/wiki([^-a-z]|$)|wikijs Deployment|deploy/wiki([^-a-z]|$)` で引き直して 0 件。
- 導出値（行数）は本書にしか無く、本書は凍結記録の母集合（`.ai-context/specs`）へ入る。上の数は本書を書く前の値で、
  本書自身の行（`deploy/llm-gateway` 等を引用する行）は含まない（規則 8）。

## 受け入れ基準

- 走査の軸・行数・除外とその理由が本書にある。誤り 2 件が直っている。
- `check-trace-blocks` / `scripts.test.js`（`REQUIRE_REPO_TESTS=1`）/ `check-doc-links` / `check-plan-id-qualification` /
  `gen-knowledge-graph --check` が通る。稼働クラスタには触れていない。
