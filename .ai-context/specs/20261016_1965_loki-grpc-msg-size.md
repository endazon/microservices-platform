---
title: Loki のログ書き出しが AST のログで時間切れになる（querier → frontend の gRPC 4 MiB 上限）を、上限の引き上げと既定の件数の引き下げで直す（#1965）
type: spec
status: done
related_ids: [NFR-18, ADR-0133, ADR-0006, IADR-0543, IADR-0553]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0133_admin-endpoint-front-authentication.md
issue: "#1965"
---

# 仕様書: Loki の gRPC 上限とログ書き出しの既定の件数（#1965）

## 起点となる計画書（トレーサビリティ）

- NFR-18（運用の口の統制）。ADR-0133（Loki の前段）・ADR-0006（観測スタック）は**変えない**前提として引く。
- 実装判断の記録: [IADR-0553](../adr/IADR-0553_loki-grpc-msg-size-and-export-default-limit.md)。書き出しの道具は [IADR-0543](../adr/IADR-0543_operator-loki-read-path.md)。
- 起点: #1965（PoC 2026-10-16 20:45 JST。AST のログの書き出しが毎回 120 秒で時間切れ。Loki に `ResourceExhausted ... (4609145 vs. 4194304)`）。

## 受け入れ基準と写像

| 受け入れ基準（#1965 と依頼） | 写像 |
| --- | --- |
| Loki の `server.grpc_server_max_recv_msg_size` / `grpc_server_max_send_msg_size` を引き上げる（例 16 MiB） | `deploy/loki-config.yaml`・`deploy/local/observability/loki.yaml` に 16777216。試験 `#1965 Loki の gRPC の上限` |
| querier・frontend 間のクライアント側の上限も揃える | 実測で既定 100 MiB（Loki 3.0.0 の `-print-config-stderr`）。**揃える必要が無いので書かない**（IADR-0553 決定 2） |
| Helm（と compose）の設定 | chart に Loki は無い（`grep -rni loki deploy/helm` 0 件）。compose と経路 B（kustomize）を直す |
| 書き出しスクリプトの既定 `--limit` を 1000 に下げる | `DEFAULT_LIMIT = 1000`。試験 `#1965 export-loki-logs: --limit を与えなければ 1 ページ 1000 行で要求する` |
| 運用手順を直す | `docs/operations/loki-log-read-runbook.md` §4（既定 1000・上げない）・§5（gRPC の上限の確かめ方）・困ったとき（時間切れの切り分け） |
| 値とメモリへの影響を IADR に残す | IADR-0553「メモリへの影響」 |

## 母集合（規則 9・10）

- 誤りの側の文字列で走査した: `git grep -n -E "5000 (件|行)|--limit|grpc_server_max|max_send_msg_size|MAX_LIMIT" -- docs deploy scripts/README.md .ai-context/adr '*.md'`（`.ai-context/specs/` は凍結のため除く）。
  - 当たり: `docs/operations/loki-log-read-runbook.md` §4 の「1 応答の 5000 件を超える分は」→ 直した。同「同じ時刻に 5000 件以上」・curl の注記の「5000 件を超える分は返らない」は `max_entries_limit_per_query` の話で正しい（変えない）。
  - IADR-0543 の「1 応答は `max_entries_limit_per_query`＝5000 件で切れる」は Loki の上限の記述で正しい（凍結記録でもある）。
  - `scripts/README.md` の表は既定の件数を書いていない（変えない）。
- `deploy/helm/` に Loki は無い（`grep -rni loki deploy/helm` 0 件。描画に Loki が現れたら前段を要求する検査は既にある＝IADR-0550）。
- 新たに誤りになる自分の記述（規則 10）: スクリプトのコメント「5000 行の 1 ページは数秒で返る」は行の短い job に限ると直した。`loki.yaml` 冒頭の「`server` と `frontend` の節だけは compose と違う」は、足した 2 鍵が両方で同じ値なので引き続き正しい。

## 実測（Loki 3.0.0 公式バイナリ。経路 B の inline の config を抜き出して与えた）

- 既定値: `grpc_server_max_recv_msg_size: 4194304`・`frontend_worker.grpc_client_config.max_send_msg_size: 104857600`。
- 再現: 1,220 バイトの行 9,023 行。現行の config で `--limit 5000` → 時間切れ・`received message larger than max (6267034 vs. 4194304)`。`--limit 1000` → 成功（10 ページ）。
- 直した config で `--limit 5000` → 成功（2 ページ・sha256 一致・`ResourceExhausted` 0 件）。RSS 約 160 MiB。
- `/config` を素の `grep` で引くと、同じ名前の鍵が別の節から値 0 で 2 行出る（実測）。runbook §5 は `awk` で `server:` の節だけを切り出す形にし、出力（16777216 の 2 行だけ）を実測して期待の欄へ写した（レビューの指摘）。

## 試験（`scripts.repo.test.js`）と変異

- `#1965 export-loki-logs: --limit を与えなければ 1 ページ 1000 行で要求する（上限 5000 は据え置き）` — 変異 `DEFAULT_LIMIT = 5000` で赤。
- `#1965 Loki の gRPC の上限: compose と経路 B の inline の両方で recv / send が 16 MiB` — 変異（片方の行を消す）で赤。
- 既存の `#1935 ... 時刻は時差つきだけ` の「既定が MAX_LIMIT」を「上限は受け付ける」へ置き換えた（既定と上限が分かれたため）。

## 射程外

- 時間切れのときの自動の取り直し（IADR-0553 決定 4）。
- Loki の `max_entries_limit_per_query` の変更。chart への Loki の追加。
