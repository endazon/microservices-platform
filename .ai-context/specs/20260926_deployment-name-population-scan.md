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
| 同上 | Deployment | `synthetic-monitor`（`SYNTHETIC=1`／chart では `syntheticMonitor.enabled=true`。#1555 で chart にも入った。名前は同じ） | `deploy/local/synthetic-monitor/synthetic-monitor.yaml`・`templates/synthetic-monitor.yaml` 69 行 |
| `platform-infra` | Deployment | `postgres` `rabbitmq` `redis` `keycloak` `qdrant` `otel-collector` `mailpit` `vault` `headlamp` `alertmanager` `grafana` `loki` `prometheus` `tempo` `mail-relay` `reset-gate` `reset-floor` | `deploy/local/{infra,observability,vault,headlamp}/*.yaml`・`deploy/mail-relay/**` |
| 同上 | DaemonSet | `inotify-sysctl` | `deploy/local/infra/inotify-sysctl.yaml` |
| 同上 | CronJob | `platform-backup-postgres` / `platform-backup-vault`（ラベル `app: platform-backup`。#1563 で追加） | `deploy/local/platform-backup/{postgres,vault}/cronjob.yaml` 15・18 行 |
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

## 2b. 再走査（`origin/develop` `a562d20e` を取り込んだ後。PR #1561 の衝突解消時）

［2026-09-26 追記 / #1561］本書を書いた後に develop へ #1548・#1556・#1555（合成監視を chart へ）・#1552・
#1563（platform-infra の暗号化バックアップ）・#1566 が入った。develop をマージコミットで取り込み、上と同じ軸・同じ除外で
**旧 `82801440` と取り込み後の作業ツリーを両方引き、行の集合の差（ファイル＋行内容で突き合わせ。行番号は無視）を 1 行ずつ読んだ**。
軸 A・B・C・D・F・G の旧側の値は上表と一致した（314 / 17 / 10 / 21 / 1 / 15）＝式を再現できている。E1 は再現した式で旧 6
（上表の 12 と式の細部が違う）なので**差だけを採る**。E2 は補助軸の前段（語の入った行）だけ引き直して 8 → 8。

| 軸 | 旧（`82801440`） | 新（取り込み後） | 差の中身と判定 |
| --- | --- | --- | --- |
| A: `種別/名前` | 314 | 333（+19） | 増えた 22 行・消えた 3 行。消えた 3 と増えた 3 は同じ行の書き換え（本 PR の #1 と、再生成された Lingui カタログ `messages.ts` 2 行）。**純増 19 = パスだけ 13**（`deploy/local` を指す: IADR-0471 4・`adr/README.md` 1・`platform-backup/**` のコメント 6・`operations.md` 970 行・`platform-infra-backup-runbook.md` 22 行）**＋資源参照 6**: `synthetic-monitor/README.md` 88 行 `-n microservices-platform scale deploy/synthetic-monitor`、`platform-infra-backup-runbook.md` 72・164・195 行と `scripts/backup-restore-drill.sh` 27・312 行の `-n platform-infra … deploy/postgres` → **すべて実在・名前空間も一致。誤り 0** |
| B: 空白区切り | 17 | 17 | 差なし |
| C: ラベル選択子 | 10 | 11（+1） | `platform-infra-backup-runbook.md` 137 行 `-n platform-infra get jobs -l app=platform-backup`。ラベルは両 CronJob の metadata と Pod テンプレートに宣言がある（`cronjob.yaml` 18・34/35 行）→ 実在 |
| D: 変数で組む名前 | 21 | 21 | 差なし。軸 D の式に掛からない形も差分で読んだ: `backup-restore-drill.sh` 146 行 `target="${LIVE_TARGET:-deploy/postgres}"`（`-` の直後なので軸 A の前置条件にも掛からない）、`scripts/helm-synthetic-monitor.test.js` の正規表現中の `deploy\/\$d-service`（既存の `k8s-local-up.sh` のループを照合するもの）→ 実在 |
| E1: 散文 | 6（再現式） | 7（+1） | `values.yaml` 1247 行「Deployment `synthetic-monitor`」→ chart の `templates/synthetic-monitor.yaml` 69 行と一致 |
| F: `svc/<x>` | 1 | 1 | 差なし |
| G: `llm-gateway` | 15 | 15 | 差なし |
| H（新設・隣接）: CronJob / Job | 5 | 10（+5） | #1563 が CronJob を足したので軸を足した（式 `(cronjob\|cronjobs\|cj\|job\|jobs\|job\.batch\|cronjob\.batch)/…` と空白区切りの `create job\|get\|logs … job <x>`）。増えた 5 行はすべて `platform-infra-backup-runbook.md` 123-126・140 行: `cronjob/platform-backup-{postgres,vault}` は実在、`job/platform-backup-postgres-manual-1` は直前の行で `create job --from=cronjob/…` が作る名前、`job/<名前>` は占位 → 誤り 0。旧 5 行（`job/helm-install-traefik`・`job/$JOB`）は前回の範囲で変化なし |
| A（凍結記録） | 250 | 269（+19） | 本書が追跡下に入った分 16 行と、新しい仕様書 3 行（`20260926_issue-1287` 41 行・`20260926_issue-1560` 14・21 行。いずれも `deploy/helm`・`deploy/local` のパス）。実在しない名前の新規は 0 |

- **再走査で新たに見つかった誤りは 0 件。** 直す箇所は上の #1・#2 のまま。
- 名前空間: 新しい資源参照はすべて `-n` を持ち、`synthetic-monitor` → `microservices-platform`、`postgres`・`platform-backup-*` → `platform-infra` で宣言と一致。
- 衝突は `docs/operations/operations.md` の trace ブロック 1 か所だけ（本 PR の本書名・#1558 と develop の IADR-0471・#1560 の仕様書・#1560・AST#346 を
  キーごとに併合）。本文の #1・#2 は develop 側で触れられておらず、そのまま残った。

## 3. この変更で新たに誤りになる記述（規則 10）

- 直した後に `deployment/wiki([^-a-z]|$)|wikijs Deployment|deploy/wiki([^-a-z]|$)` で引き直して 0 件。
  develop `a562d20e` を取り込んだ後も `deploy/llm-gateway` を足した同じ式で 0 件（#1561）。
- 導出値（行数）は本書にしか無く、本書は凍結記録の母集合（`.ai-context/specs`）へ入る。上の数は本書を書く前の値で、
  本書自身の行（`deploy/llm-gateway` 等を引用する行）は含まない（規則 8）。

## 受け入れ基準

- 走査の軸・行数・除外とその理由が本書にある。誤り 2 件が直っている。
- `check-trace-blocks` / `scripts.test.js`（`REQUIRE_REPO_TESTS=1`）/ `check-doc-links` / `check-plan-id-qualification` /
  `gen-knowledge-graph --check` が通る。稼働クラスタには触れていない。
