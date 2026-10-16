---
title: IADR-0553 Loki の gRPC の受信・送信の上限を 4 MiB から 16 MiB へ上げ（compose と経路 B の両方）、ログの書き出しの既定の 1 ページを 5000 から 1000 行へ下げる
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0133, ADR-0006, IADR-0543, IADR-0539, IADR-0526]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0133_admin-endpoint-front-authentication.md
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md
related_specs:
  - ../specs/20261016_1965_loki-grpc-msg-size.md
---

# IADR-0553: Loki の gRPC の上限を 16 MiB へ上げ、書き出しの既定の 1 ページを 1000 行へ下げる（#1965）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-16
- 決定者: claude（オーナーの起票 #1965。どちらか、または両方を求められた）

## 起点・関連

- 起点 issue: #1965（PoC 2026-10-16。AST のログの書き出しが 1 時間の範囲でも毎回 120 秒で時間切れ）。関連: #1938・#1869。
- 関連する計画書 ID: NFR-18（運用の口の統制。前段は変えない前提として引く）。
- 関連する計画 ADR: ADR-0006（観測スタック）・ADR-0133（Loki の前段。本件は前段も到達の制限も変えない）。
- 関連する実装 ADR: [IADR-0543](./IADR-0543_operator-loki-read-path.md)（書き出しの道具と手順）・[IADR-0539](./IADR-0539_dotnet-gc-workstation-conserve-memory-default.md)（Loki の `GOMEMLIMIT=192MiB`）・
  [IADR-0526](./IADR-0526_loki-tempo-front-auth-and-networkpolicy.md)（経路 B の Loki は loopback で待つ）
- 採番: `origin/develop` `641a2e1a` の最大は IADR-0552。開いている PR に IADR を足すものは無い（2026-10-16 に確認）。本件は **0553**。

## コンテキストと課題

- Loki 3.0.0 の単一バイナリでは、query-frontend が要求を分け、querier が結果を gRPC で frontend へ返す（`frontend_worker`）。
  **受け手（frontend 側の gRPC サーバ）の受信の上限は `server.grpc_server_max_recv_msg_size`＝既定 4 MiB（4194304）である。**
- PoC の Loki のログ: `msg="error notifying frontend about finished query" err="rpc error: code = ResourceExhausted desc = grpc: received message larger than max (4609145 vs. 4194304)"`。
  querier の応答は捨てられ、frontend は応答を待ち続ける。HTTP の側には誤りが返らず、書き出し（`scripts/export-loki-logs.js`）は 1 要求の上限（120 秒）で時間切れになる。
- AST のログは 1 行が長い（判断の全文など）。既定の `--limit 5000` では 1 ページが 4 MiB を超える。`--limit 1000` なら通る（PoC 実測: 10-14 夜間 60,528 行・10-15 夜間 77,531 行）。
- 決めること: (1) Loki の上限を上げるか・いくつにするか、(2) 送り手（querier）のクライアント側の上限も揃える必要があるか、(3) 書き出しの既定の件数を下げるか、(4) 時間切れのときに自動で件数を下げて取り直すか。

## 実測（Loki 3.0.0 の公式バイナリ `loki-linux-amd64`・b4f7181。経路 B の inline の config をそのまま与えた）

| 項目 | 値 |
| --- | --- |
| 既定の `server.grpc_server_max_recv_msg_size` / `grpc_server_max_send_msg_size`（`-print-config-stderr`） | 4194304 / 4194304 |
| 既定の `frontend_worker.grpc_client_config.max_send_msg_size` / `max_recv_msg_size` | 104857600 / 104857600（100 MiB） |
| 再現: 1,220 バイトの行を `job=ai-stock-trading.demo` で 9,023 行 push し、現行の config で `--limit 5000` の書き出し | 20 秒の上限で時間切れ。Loki に `received message larger than max (6267034 vs. 4194304)` |
| 同じ config で `--limit 1000` | 9,023 行・10 ページで成功（sha256 `3df7fc97…`） |
| 上限を 16 MiB にした config で `--limit 5000` | 9,023 行・2 ページで成功（sha256 は上と一致）・`ResourceExhausted` 0 件 |
| その直後の Loki のプロセスの RSS | 約 160 MiB（163,252 KiB） |

（2）の答え: **送り手のクライアント側は既定で 100 MiB あり、詰まっているのは受け手のサーバ側の 4 MiB だけである。** クライアント側の鍵は足さない（同じ値を書いても効きは変わらず、版が変わったときに既定へ追随しない値が残るだけである）。

## 検討した選択肢

| 案 | 中身 | 評価 |
| --- | --- | --- |
| A | Loki の上限だけ上げる | 既定の 5000 行のまま、さらに長い行が来ると 16 MiB でも同じ形で詰まる（1 行あたり平均 3.3 KiB を超えると超える） |
| B | 書き出しの既定だけ下げる | Grafana の Explore（既定 1000 行）は通るが、`--limit 5000` を明示した運用者や他の読み手（logcli 等）は同じ形で詰まり、しかも誤りが返らない |
| **C（採用）** | 両方。上限は 16 MiB、既定は 1000 行。上限 5000 は受け付けたまま | 1000 行の 1 ページが 16 MiB を超えるには 1 行あたり平均 16 KiB が要り、AST の実測（5000 行で 4.6 MiB＝平均 約 0.9 KiB）から 18 倍の余裕がある |
| D | C に加えて、時間切れのときに件数を半分にして自動で取り直す | 時間切れは port-forward の詰まりでも起きる。それを件数のせいにして黙って取り直すと、原因の切り分けを誤らせる。上限を上げた後は起きにくく、手順書に「時間切れなら `--limit 500`」を書けば足りる。**採らない** |

## 決定

1. **`server.grpc_server_max_recv_msg_size` と `grpc_server_max_send_msg_size` を 16777216（16 MiB）にする。** compose（`deploy/loki-config.yaml`）と経路 B の inline（`deploy/local/observability/loki.yaml`）の両方に同じ値を書く。
   本番像の chart には Loki が無い（`deploy/helm/` を走査して 0 件）ので、chart は変えない。
2. **querier 側のクライアントの上限（`frontend_worker.grpc_client_config.*`）は書かない**（既定 100 MiB。上の実測）。
3. **`export-loki-logs.js` の既定の `--limit` を 1000 にする**（`DEFAULT_LIMIT`）。上限（`MAX_LIMIT`＝5000＝Loki の `max_entries_limit_per_query`）は据え置き、明示すれば 5000 まで使える。同じ時刻の取り直し（1 ns の幅）は従来どおり上限の件数で取る。
4. **時間切れの自動の取り直しは入れない**（案 D を採らない理由）。手順書の「困ったとき」に、`ResourceExhausted` の見分け方と `--limit 500` を書く。
5. **前段・到達の制限は変えない**（ADR-0133）。上げた上限は Pod の loopback の gRPC（`127.0.0.1:9095`）にだけ効き、ネットワークへ出る口は増えない。

## メモリへの影響

- **上限は事前に確保される量ではない。** gRPC は届いた 1 通の実際の大きさだけを確保する。上限を上げても、4 MiB 未満の通信のメモリは変わらない（実測の RSS 約 160 MiB は 5000 行・約 6 MiB の 2 ページを通した直後の値）。
- 増えうるのは、これまで捨てられていた大きな応答を**最後まで受け取るようになった分**である。最悪の見積り:
  querier の並列（`querier.max_concurrent`＝既定 4）× 16 MiB ＝ 64 MiB の受信バッファ。frontend は受けた応答を JSON に直して返すので、1 応答あたり 2〜3 倍の一時的な確保が要る（推測）。
  合計で 64〜200 MiB の一時的な増分が、4 本の大きな要求が重なったときにだけ生じる。
- 経路 B の Loki は `GOMEMLIMIT=192MiB`（ソフトな上限。GC を早める）と limits 512Mi（IADR-0539）。上の最悪でも limits の内側に収まる。重なりは運用者の書き出し（1 本ずつ直列）と Grafana の Explore だけで、取り込み（push）は HTTP であり本件の上限を通らない。
- **既定を 1000 行に下げたので、書き出しの 1 通は平均 約 1 MiB（AST）に下がる。** 日常の書き出しのメモリは、上限を上げる前の 5000 行（行の短い job）より小さい。

## 結果

- 良い点: 既定のままで AST のログを書き出せる。`--limit 5000` を明示しても AST の行の長さなら通る。Explore で大きな範囲を見ても応答が消えない。
- 悪い点・残余リスク:
  - 書き出しの要求の数が 5 倍になる（1 日 6 万行で 13 → 61 ページ）。1 ページは数秒なので、1 日分で数分の延びである（推測）。
  - 1 行が平均 16 KiB を超える job が現れると、既定の 1000 行でも再び詰まる。そのときの症状と退路（`--limit` を下げる）は手順書にある。
  - compose と経路 B の値が分かれると、片方でだけ詰まりが戻る。`scripts.repo.test.js` の #1965 節が両方の値を固定する。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（#1965 節: 既定 1000・要求の limit が 1000・両 config の 16 MiB）。
- `node scripts/check-deploy-manifests.js`（経路 B の描画・前段と NetworkPolicy・外部通信の既定）。
- 上の表の再現（公式バイナリ）。
