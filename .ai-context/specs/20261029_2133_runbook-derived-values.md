---
title: 作業仕様書 — 手順書の合否判定値（警報ルール数・Secret のキー数・モデル・Ready 数）を実体と食い違わない形にする（#2133）
type: spec
status: done
related_ids: [NFR-21]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2133"
---

# 作業仕様書 — 手順書の導出値（#2133）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。develop `b10a08dc`）。親は #2047（指摘 M-28）。

## 現状（実測。issue の行番号は d8b97e98 時点なので引き直した）

| 文書 | 記述 | 実体 |
| --- | --- | --- |
| `docs/operations/operations.md`（Grafana 統合アラート・経路B の inline・通知先の注記・provisioning の確認） | 「24 ルール」「22 件」「22 ルール」「20 件返す」 | `deploy/prometheus/alerts.yml` の `- alert:` は 27 件 |
| `docs/operations/secret-item-live-sync-check-runbook.md` | 「`anthropic-api-key` と `openai-api-key` の 2 キー」「llmgateway が読むのは `anthropic-api-key` だけ」 | ExternalSecret の `data` は 3 キー（anthropic・openai・voyage）。llmgateway は `voyage-api-key` も読む（チャートの `Embedding__Voyage__ApiKey`） |
| `docs/operations/search-rerank-s3-measurement-runbook.md` | 「`rerank`・`rag-answer` がともに `claude-haiku-5-5`」「例: `claude-haiku-5-5`」 | `rag-answer` は `claude-sonnet-5-5`（`LlmGateway/appsettings.json` の `Llm:Routing:PurposeModels`）。`rerank` は `claude-haiku-5-5` |
| `docs/operations/local-sso-recovery-runbook.md` | `# 11 True` | ExternalSecret の数は配備の組で変わる（監査時 24〜26） |

## 方針

- **件数・モデル名を本文に書かない。** 導出の形にする。
  - 警報: 「`alerts.yml` と 1 対 1／同数」。数えるコマンド（`grep -c '^ *- alert:' deploy/prometheus/alerts.yml`）を添える。
  - Secret: キーの一覧と数は ExternalSecret の `data` が正。手順の中で実物を数える（手順 1-2 は既にそうしている）。
  - モデル: 構成（appsettings の既定と稼働の env 上書き）から引いて記録する。
  - Ready: 「True の行だけ（False・Unknown・空が無い）」。
- 是正の経緯（過去の数）は、警報の節で 1 行に畳んで残す（なぜ数を書かないかの根拠）。

## 受け入れ基準

- [x] 上記 4 文書に、実体と食い違う件数・モデル名が残らない
- [x] 誤りの側の文字列で全文書を走査し、追随先を引いた（下記）

## 母集合（規則 9。誤りの側の文字列で全 docs を走査）

- 警報の件数: `git grep -nE "[0-9]+ ?(ルール|件|本)" -- docs` を警報・Prometheus・Grafana の文脈で絞った → operations.md の 4 か所だけ。
  - `rag-first-token-latency.md` の「アラート 5 件のうち 4 件」と、operations.md の「当時の 5 ルールのうち 4 件」は過去の是正の記録である（当時の数）。外す。
  - `password-reset-relay-state-measurement-runbook.md` の「3 件のアラート」は、その節が扱う 3 ルールそのものを指す（全体の数ではない）。外す。
- Secret のキー数: `git grep -n "2 キー"` → 本件の 1 か所。他の当たり（ABAC の「文書の条件 2 キー」）は別の話である。
- モデル: `git grep -nE "rag-answer[^|]{0,40}haiku"` → 本件の 1 か所のほか、FR-11（鎖の第 2 候補・過去の割当の記録）と llm-model-pin-runbook（2026-08 時点の記録）。いずれも鎖・経緯の記述で、現行の割当の主張ではない。外す。
- Ready 数: `git grep -nE "[0-9]+ True"` → 本件の 1 か所だけ。
- 規則 10: 新しく書いた数え方のコマンドは実測で 27 を返す（`alerts.yml` と一致）。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
