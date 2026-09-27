---
title: Vault の audit device を宣言で有効にし、秘密の書き込みを可観測性基盤の監査として抽出する（値は記録しない。#1683）
type: spec
status: done
related_ids: [NFR-18, NFR-21, SC-22, ADR-0124, ADR-0095, ADR-0006, ADR-0042, IADR-0486, IADR-0485, IADR-0453, IADR-0433, IADR-0457, IADR-0077, IADR-0096, IADR-0216]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 2・決定 4（2 行目）・フォローアップ 3
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md 決定 4・フォローアップ 4
issue: "#1683"
---

# 仕様書: Vault の audit を可観測性基盤の監査へ取り込む（#1683）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`、`origin/main` を読み取り専用で参照）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-18**（シークレット管理）、NFR-21（障害検出。出力先が止まったときの扱い）、SC-22（画面経由の書き込み）
- 計画 ADR: **ADR-0124 決定 2**（Vault の audit device を有効にし、ログを可観測性基盤へ取り込み、秘密の書き込みを監査として抽出する。
  いつ・誰が・どの項目とプロパティへ書いたかを残し、値は記録しない〔audit device の値のハッシュ化を用いる〕。射程は監査）・
  決定 4 の 2 行目（3 点セット）・フォローアップ 3。ADR-0095 決定 4（退避手段を使った事実を残す）、ADR-0006（可観測性基盤）
- 関連 IADR: IADR-0453（SC-22 の残作業。フォローアップ 3 が本件）、IADR-0433（BFF の書き込み権限・k8s auth ロール `bff-secret-writer`）、
  IADR-0457（永続化した Vault の Pod 内ラッパー）、IADR-0077（経路B の可観測性・Vault オーバーレイ）、IADR-0096（k8s auth）、IADR-0216（ログの出口は OTLP）
- 起点 issue: #1683（planning#700 の利用者裁定 ② の実装）。対になる #1682 は別の作業者が進める

## 目的・背景

- 実測（planning#700）: Vault の audit device は無効。可観測性基盤のログの受信は OTLP だけで、監査を抽出する仕組みは無い。
- 退避手段（コンソールからの直接投入）を使った事実は、今は Runbook の issue コメントに人が書くしかなく、書き忘れを機械で検出できない。
- 本件は、**画面経由（BFF）とコンソール経由の秘密の書き込みを、同じ記録（Vault の audit）で辿れるようにする**。

## 母集合（規則 9: 記憶で挙げず走査で引いた。2026-09-28・`origin/develop` = `bd620f79`）

走査: `grep -rli vault deploy scripts`・`grep -rln "otel-collector\|loki" deploy`・`grep -rln "監査として抽出\|Audit=true" docs .ai-context/adr`・
`grep -rn "audit device\|audit enable\|vault audit\|監査ログに乗らない\|監査にならない\|監査を抽出する" --include=*.{md,yaml,sh,js,hcl}`（`.ai-context/specs` を除く）。

### Vault の配備宣言

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `deploy/local/vault-persistence/vault-entrypoint.sh` | **変更**（audit device の有効化を足す） | 経路B の Vault の既定（永続化）の起動器。Vault の状態を宣言どおりに寄せる唯一の場所 |
| `deploy/local/vault-persistence/vault-entrypoint.test.sh` | **変更**（試験を足す） | 上の分岐を `vault` スタブで固定する既存の試験 |
| `deploy/local/vault-persistence/{kustomization,deployment-patch,pvc}.yaml`・`local.hcl` | 変更しない | audit は `local.hcl`（サーバ構成）ではなく API（`vault audit enable`）で有効にする（起動器が kv-v2 の mount を同じやり方で宣言している前例に揃える）。起動の差し替え・PVC は現状で足りる |
| `deploy/local/vault/vault-dev.yaml`・`kustomization.yaml`・`clustersecretstore.yaml` | 変更しない（**除外**） | `PERSIST=0` の `-dev`（インメモリ・起動器なし）。起動のたびに全状態が消える使い捨ての構成で、起動器を持たないため宣言を差し込む場所が無い。**audit の対象外であることを IADR と文書に書く** |
| `deploy/local/vault/eso/bootstrap.sh`・`policy-*.hcl`・`externalsecret-*.yaml`・`clustersecretstore-k8s.yaml`・`vault-auth-rbac.yaml` | 変更しない（**除外**） | `bootstrap.sh` は #1682 の対象（触れない）。policy・ロール名は読むだけ（抽出の条件が BFF のロール名 `bff-secret-writer` を使う。一致は試験で固定する） |
| `deploy/local/vault/oidc/*` | 変更しない | 読むだけ（OIDC ログインの主体は audit の `auth.display_name` が `oidc-…` になる） |
| `deploy/local/platform-backup/vault/*` | 変更しない | audit は PVC へ書かない（標準出力と socket）ので、バックアップの対象は増えない |
| `deploy/helm/microservices-platform/values.yaml`（`vault:`） | 変更しない | helm は Vault を配備しない（BFF の書き込み先の宛先だけ）。本番の Vault は未配備（ADR-0124 実測 7） |
| `deploy/docker-compose.yml` | 変更しない（**除外**） | compose に Vault は居ない |
| `deploy/bootstrap/sc22-secret-items.json`・`scripts/reconcile-realm.js`・realm の宣言 | 変更しない（**除外**） | #1682 の対象 |

### collector の設定

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `deploy/local/infra/otel-collector.yaml` | **変更**（受信 `tcplog/vault-audit`・専用の `logs/vault-audit` パイプライン〔出口は debug〕・containerPort・Service のポート） | 既定（debug だけ）。**ここに受け口が無いと、観測を opt-in しない既定の起動で socket device を有効にできない**（Vault は有効化のときに試験の 1 行を書く） |
| `deploy/local/observability/otel-collector-forward.yaml` | **変更**（同じ受信・同じ処理で、出口を Loki へ） | 同名 ConfigMap を上書きする転送構成。**片方だけに置くと差し替えた瞬間に受け口が消える**（#1090 の形） |
| `deploy/otel-collector-config.yaml`（compose） | 変更しない（**除外**・コメントで明記） | compose に Vault は居ない。`prometheus/mail-relay` と同じ「意図した乖離」 |
| `deploy/local/aliases/microservices-platform-externalnames.yaml` | 変更しない | Vault と collector は同じ `platform-infra` に居るので別名は要らない |

### Loki の設定

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `deploy/local/observability/loki.yaml`・`deploy/loki-config.yaml` | 変更しない | 受信（push）の形は変わらない。保持期間は既定（削除しない。容量は `loki-data` PVC 2Gi で縛られる）のまま。**保持期間の確定は NFR「監査ログ保持」の運用整備（#198）に残す** |
| `deploy/local/observability-persistence/*` | 変更しない | Loki の PVC は既に在る |
| `deploy/grafana/provisioning/datasources/datasources.yaml` | 変更しない | Loki のデータソースは既に在る。抽出はそこへ LogQL を投げるだけ |

### 監査の抽出の約束の表

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `docs/security/security.md` §監査ログ | **変更**（Vault の audit の行・抽出の条件・経路の見分け方・値を残さない設定） | 監査の約束の表の正本 |
| `docs/operations/secret-item-console-injection-runbook.md` §0・§5・§限界 | **変更**（日付つき追記） | 「コンソール操作はどこにも残らない」が本件で部分的に誤りになる（規則 10）。**人の記録（実施者・理由）は引き続き要る**（共有の root トークンで書くため、audit は「画面以外の root トークン」までしか言えない） |
| `docs/operations/operations.md`（SC-22 の退避の節） | **変更**（日付つき追記 1 行） | 同上 |
| `docs/operations/secret-rotation-runbook.md` | 変更しない（**除外**） | #1682 の対象（触れない）。同書の「コンソールの操作は監査ログに乗らない」は、アプリの監査ログ（`Audit=true`）については引き続き真 |
| `docs/operations/llm-output-token-measurement-runbook.md` | 変更しない | 「記録先へ残す」という結論は変わらない（人の記録は引き続き要る） |
| `deploy/local/observability/README.md`・`deploy/local/vault/README.md` | **変更**（短く 1 節） | 配備の読み手が受け口と audit の存在を知る場所 |
| `.ai-context/adr/IADR-0453` フォローアップ 3 | **変更**（日付つき追記） | 本件がそのフォローアップの実装。裁定（planning#700）が「本裁定を引いて閉じる」とした。**フォローアップ 2 は #1682 側**（触れない） |
| `.ai-context/adr/IADR-0433` | 変更しない | 監査の記録項目（アプリ側）は変えない |

### 計画 ID のレンジ

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `.claude/rules/traceability.repo.md`・`docs/how-to/plan-id-range-history-annex.md` | **変更**（`ADR-0001..0122` → `0001..0124`） | コミット件名・trace ブロックに ADR-0124 を書くと宣言レンジの検査が落ちる。計画リポの `origin/main`（`74eb255`）を `git archive` で展開し `gen-plan-ranges.js --check` で「ADR [1, 124]・欠番なし」を実測した。**#1684（#1682）が同じ引き直しを持つので、行と別紙の節は同 PR と字義一致にした**（どちらが先にマージされても衝突しない） |

## 追記（2026-09-28・develop の取り込み）

- #1684（#1682）が先にマージされ、IADR-0485 はそちらのもの（`paired-secrets-create-only-declarations`）になった。本件の IADR は **IADR-0486** へ改番した。
- 退避の Runbook §5 の記録先は #1684 の決定どおり **#458 の 1 か所**を維持し、本件の追記（audit に残るのは画面以外の root トークンの行までで、人と理由は #458 に書き続ける）を同じ節にまとめた。
  #1684 の「取り込みができたら本節を差し替える」は「取り込みの後も本節は差し替えない」に改めた。

## 設計

### 1. audit device を 2 つ有効にする（出力先と冗長化）

| path | 種別 | 出力先 | 役割 |
| --- | --- | --- | --- |
| `stdout/` | `file`（`file_path=stdout`） | Vault コンテナの標準出力（kubelet のコンテナログ） | **止まらない方の device**。これがあるので、collector が止まっても Vault は止まらない |
| `otel-collector/` | `socket`（`socket_type=tcp`） | `otel-collector.platform-infra.svc:9514`（collector の `tcplog` 受信） | 可観測性基盤への取り込み。Loki で抽出する |

- **Vault は、有効な audit device の少なくとも 1 つが書けなければ要求を拒む**（1 つも無ければ記録しない）。socket 1 つだけにすると、
  collector・Loki の停止がそのまま Vault の停止になる（BFF の画面も ESO の同期も止まる）。標準出力の device を並べてこれを避ける。
- 標準出力の device の有効化に失敗したら**起動を失敗させる**（audit の無い Vault を動かさない。fail-closed）。
- socket の device の有効化は**起動を止めずに裏で再試行する**（collector より Vault が先に上がる順序でも立つ）。
  有効化は Vault の storage に残るので、2 回目以降の起動では既に在る。
- `write_timeout=2s`（socket が詰まったときに要求 1 本が待つ上限）。
- **値を記録しない**: 両方に `log_raw=false`・`hmac_accessor=true` を明示する（既定と同じ値を**書いて**固定する）。
  平文を出す設定（`log_raw=true`・mount の `audit_non_hmac_request_keys` / `audit_non_hmac_response_keys`）が `deploy/`・`scripts/` に無いことを試験で固定する。

### 2. collector → Loki

- 受信 `tcplog/vault-audit`（`0.0.0.0:9514`）。1 行 1 JSON（Vault の既定の形式）。
- 専用パイプライン `logs/vault-audit`: `memory_limiter` → `resource/vault-audit`（`service.name=vault-audit` → Loki の `job` ラベル）→
  `attributes/vault-audit`（`loki.format=raw`。本文の JSON をそのまま 1 行として送る）→ `batch`。**値で落とす段を置かない**（security.md の既存の約束と同じ）。
- 出口: 既定は `debug`（外へ出さない）、転送構成は `loki`。
- collector の Deployment と Service に `9514/TCP`（`vault-audit`）を足す。

### 3. 抽出の条件（security.md の約束の表に載せる）

```logql
{job="vault-audit"} | json
  | type="response"
  | request_operation=~"create|update|patch|delete"
  | request_path=~"secret/(data|metadata|delete|undelete|destroy)/.+"
```

- ［2026-09-28 追記・監査の指摘］`request_path` に `sys/(audit|config/auditing|policy|policies/acl|mounts)/.+` を足した（監査を弱める操作も抽出する。IADR-0486 決定 4 の追記）。
- 経路の見分け: `auth_metadata_role="bff-secret-writer"` は**画面（BFF）**、それ以外（`auth_display_name` が `token-local-dev-root` の root トークン・`oidc-…` の人のログイン）は**画面以外**。
- `error` で絞らない（拒否・失敗の行を落とさない。アプリ側の `outcome` を 2 値で列挙しないのと同じ理由）。
- 残るのは時刻（`time`）・主体（`auth_display_name`・`auth_metadata_*`・`auth_policies`）・パス（項目）・プロパティ名（`request_data_data` の**キー**）。
  値は HMAC（`hmac-sha256:…`）で、平文は残らない。

## 受け入れ基準（→ 試験）

| # | 基準 | 試験 |
| --- | --- | --- |
| AC-1 | 起動器が `stdout/`（file）を `log_raw=false hmac_accessor=true` で有効にし、既に在れば触らない | `vault-entrypoint.test.sh` T-1683-01・02 |
| AC-2 | 標準出力の device を有効にできなければ起動を失敗させる | T-1683-03 |
| AC-3 | 起動器が `otel-collector/`（socket・tcp）を `log_raw=false hmac_accessor=true write_timeout=2s` で有効にし、失敗しても上限まで再試行し起動は止めない | T-1683-04・05 |
| AC-4 | 起動器の socket の宛先ポートが collector の 2 つの k8s 設定の `tcplog` 受信・Deployment・Service のポートと一致する | `scripts.repo.test.js` #1683 |
| AC-5 | 2 つの k8s collector 設定が同じ `logs/vault-audit` パイプラインを持ち、転送構成は Loki へ出す。パイプラインに `filter` 系の段が無い | 同上 |
| AC-6 | `deploy/`・`scripts/` に平文を出す設定（`log_raw=true`・`audit_non_hmac_*`）が無い | 同上 |
| AC-7 | security.md の抽出の条件が collector の `service.name`・KV の mount・BFF のロール名と一致する | 同上 |

## 検証

- `bash deploy/local/vault-persistence/vault-entrypoint.test.sh`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・
  `node scripts/check-collector-self-telemetry.js [--self-test]`・`node scripts/k8s-local-up.test.js`・`node scripts/check-deploy-manifests.js`（ツールがあれば）
- 文書: `check-trace-blocks`・`check-test-spec-coverage`・`check-cross-repo-refs`・`check-plan-id-qualification`・`gen-knowledge-graph --check`・`check-commit-messages --range=origin/develop..HEAD`
- **稼働クラスタでの疎通は本件の外**（LIVE 未設定）。IADR に「稼働で確かめること」を列挙する。

## 範囲外

- `PERSIST=0`（`-dev`）の Vault の audit（上の除外）。本番の Vault（未配備）。
- 保持期間・改ざん防止（#198）。ダッシュボード・アラート（抽出の条件を約束するまで。裁定は「抽出できるようにする」）。
- 退避手段の記録先の一本化（ADR-0124 フォローアップ 4）、`deferred` の件数の是正（同 5）—— #1682 側（#1684 でマージ済み）。
