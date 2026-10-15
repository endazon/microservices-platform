---
title: 運用者が経路 B の Loki を前段を弱めずに読む手段（読み取りの経路・資格・保全・保持と永続化の確認）（#1935）
type: spec
status: done
related_ids: [NFR-18, ADR-0133, ADR-0112, ADR-0006, IADR-0526, IADR-0210, IADR-0369, IADR-0543]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0133_admin-endpoint-front-authentication.md 決定 1
issue: "#1935"
---

# 仕様書: 運用者が経路 B の Loki を読む手段（#1935）

## 起点となる計画書（トレーサビリティ）

- NFR-18（運用の口の統制）。ADR-0133 決定 1（条件 1 の前段・条件 2 の到達の制限）は**変えない**前提として引く。
- 実装判断の記録: [IADR-0543](../adr/IADR-0543_operator-loki-read-path.md)。前段の形は [IADR-0526](../adr/IADR-0526_loki-tempo-front-auth-and-networkpolicy.md)、可観測性の PVC は IADR-0210 / IADR-0369（永続化の既定オン）。
- 起点: #1935（PoC。PC の再起動で `kubectl logs` が消え、10-14 のセッションの監査のために Loki を読もうとして前段で止まった）。関連: #1842（前段）・#1867（chart 側の追跡。本件では触れない）。

## 受け入れ基準と写像

| 受け入れ基準（#1935） | 写像 |
| --- | --- |
| 前段・到達の制限を弱めずに、運用者がローカルから読める経路を決めて記録する（身元を足すか否かを含む） | IADR-0543 決定 1・2。`deploy/` は 1 行も変えない（差分で確認） |
| runbook ①: 時間範囲・job を指定して `query_range` をファイルへ書き出す（10-14 の例） | `docs/operations/loki-log-read-runbook.md` §4。道具は `scripts/export-loki-logs.js` |
| runbook ②: 前段を通る資格（Secret 名と取得コマンドだけ） | 同 §2 |
| runbook ③: 保持期間と永続化の確認 | 同 §1・§5 |
| 読み取り専用の補助スクリプト（`live-scripts.json` の規約・ページ送りを試験で固定） | `scripts/export-loki-logs.js`・`live-scripts.json`（`live`・offline は `--help`）・`scripts.repo.test.js` の #1935 節（7 件） |
| サービス名・ポート・Secret 名を描画から確かめる | 下の「描画で確かめた値」 |

## 母集合（規則 9・10）

- 「Loki を運用者がどう読むか」を書いている箇所を、`port-forward.*loki|loki.*port-forward|3101|observability-gate` で走査した。
  - 当たり: `deploy/local/observability/observability-gate.yaml`（break-glass の注記）・`deploy/local/observability/README.md`（break-glass の 1 行）・IADR-0526 決定 2・作業仕様書 20261009_1842（凍結）。
  - 追随: README に runbook への 1 行を足す。gate の注記と IADR-0526 は break-glass の記述として正しいので変えない（IADR-0543 決定 2 が同じ扱いを続ける）。
- 運用仕様書の「読む場面」の表に行を足す（`docs/operations/operations.md`）。
- `scripts/` に新しい入口を足すので、#1550 の母集合（`live-scripts.json`）・`scripts/README.md` の 2 つの表・検査器の母集合の除外（`NOT_CHECKERS`）へ追随した。どれも試験が落ちて名指しした箇所である。
- 新たに誤りになる自分の記述（規則 10）: 無し。runbook は新規であり、既存の文書の主張（保持・永続化）は変えていない。

## 描画で確かめた値（helm v3.16.2 / kubectl v1.33.4 の kustomize）

- `helm template t deploy/helm/microservices-platform`: loki・tempo・grafana の出現は **0 件**（chart に Loki は無い。#1867 の前提どおり）。各サービスは `Otlp__Endpoint=http://otel-collector:4317` で送る。
- `kubectl kustomize deploy/local/observability-persistence`（`PERSIST` 既定オン × `OBSERVABILITY=1` の配備）の値:
  - Deployment `loki`（`platform-infra`）。containers は `loki`（0 番）と `gate`。`gate` の `containerPort` は 3100（名前 `http`）。
  - Service `loki` は port 3100 → targetPort `http`。PVC は `loki-data`（2Gi・`local-path`）で、`/tmp/loki` へマウントされる。
  - Secret の参照は `observability-gate` の `writer` / `reader`。名前空間は起動器の `INFRA_NS="platform-infra"`。
- job ラベル: collector の `loki` exporter（0.102.0）は、`service.namespace` が無ければ `service.name` を `job` にする。MSP は `microservices-platform.<サービス>`（各 `Program.cs` の `ServiceName`）である。AST は `ai-stock-trading.<サービス>`（AST の各 `Program.cs`）、Vault の audit は `vault-audit`。

## 実測（配備と同じ版・digest の公式イメージ。docker で Pod を模した）

描画した ConfigMap（`loki-config`・`observability-gate`）をそのまま使った。Loki と gate を同じネットワーク名前空間（`--network container:`）で起動し、gate の 3100 だけを外へ出した。

| 確かめたこと | 結果 |
| --- | --- |
| 前段: トークン無しで GET `/loki/api/v1/labels` | 401 |
| 前段: writer のトークンで GET `/loki/api/v1/labels` | 403 |
| 前段: reader のトークンで GET `/config` | 403（読み取りの身元は運用の口を通らない） |
| 前段: reader のトークンで GET `/loki/api/v1/status/buildinfo` | 200 |
| 前段の内側（Pod の中で `wget http://127.0.0.1:3101/config`） | `retention_enabled: false`・`retention_period: 0s`（2 か所）・`max_query_length: 30d1h`・`max_entries_limit_per_query: 5000`。WAL の `dir: /tmp/loki/wal` |
| `/tmp/loki` の中身 | `chunks compactor index index_cache wal`（すべて PVC のマウント先の内側） |
| 書き出し（3 job × 4000 行、同じ ms に 3 行、全 job では同じ時刻に 9 行） | `--limit 1000`: 12000 行・13 ページ。既定の 5000: 12000 行・3 ページ。`{job="vault-audit"}` を `--limit 7`: 4000 行・667 ページ。**どれも重複 0**（`jq` で job と連番の組を数えた） |
| ページ全体が同じ時刻（2 job × 20 時刻 × 4 行 = 同じ ns に 8 行） | `--limit 3 / 5 / 7`: 160 行・61 ページ（同じ時刻の取り直しの経路）。`--limit 9`: 160 行・20 ページ。どれも重複 0 |
| `--summary` | job ごとの件数（各 4000）と最古・最新の時刻を返す |
| `--summary` の件数と書き出しの行数 | 同じ範囲で、`count_over_time` は job ごとに 7997、書き出しは 8000。差の 3 行は正時ちょうど（`03:00:00.000000000Z`）の行で、Loki がメトリクスのクエリを時間で分けた境目で数え落とす。**件数は目安と表示し、正は書き出しの行数とした**（スクリプトの出力と runbook の両方） |
| Loki と gate を再起動（`docker restart`。コンテナの層＝PVC を模す）した後の `--summary` | 再起動の前のログがすべて読める（WAL と chunks が `/tmp/loki` の内側にある） |
| runbook の LogQL（バッククォートの正規表現 `` {job=~`ai-stock-trading\..+`} ``・行に含む文字列での絞り）と `label/job/values` の RFC 3339 の `start` / `end` | どれも期待どおりに返る |

実測の注記: Loki は既定で直近 3 時間の範囲だけを ingester に問い合わせる（`query_ingesters_within`）。押し込んだ時刻が 3 時間より古く、まだ flush されていない行はクエリに出ない（試験の押し込みで 1 度当たった）。稼働の経路はほぼ実時刻で届くので、10-14 の監査には効かない。

## 試験（`scripts.repo.test.js` の #1935 節）と変異

- 7 件を置いた。ページ送り（limit 4・5・3・2・1）・素朴なページ送りの陰性対照・同じ時刻の取り直しの上限・`--summary` と 401 / 403 の文言・時刻と引数・main の書き出しと上書きの拒否・前段の列挙との突き合わせ。
- 変異（1 か所ずつ戻して、#1935 節だけを走らせた）:

| 変異 | 結果 |
| --- | --- |
| M1 境目の重複を落とさない | 赤（39 行など、3 件） |
| M2 同じ時刻の取り直しで書かない | 赤（2 件） |
| M3 取り直しの上限の検査を外す | 赤（1 件） |
| M4 `query` の道を `/loki/api/v1/delete` に替える | 赤（列挙の外を名指し、2 件） |
| M5 トークンが無くても Authorization を送る | 赤（1 件） |
| M6 出力先の存在の確認を外す | **緑**（`openSync` の `wx` が二重に止めるため。意図した二重化であり、試験は `wx` を含めた振る舞いを見ている） |

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: ✓（#1935 の 7 件と #1550 の拒否の経路を含む）。
- 文書系の検査は PR 本文に記録する。

## 射程外

- 保持期間の設定（削除しないまま PVC が満杯になる可能性）。IADR-0543 残余 2。
- chart への Loki の追加と前段の強制は #1867。
