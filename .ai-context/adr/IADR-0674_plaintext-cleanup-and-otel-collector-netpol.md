---
title: IADR-0674 復号した物と秘密は手元のディスク・シェルの環境・コマンドの引数に残さない（リストア試験は tmpfs と trap、手順書は read -rs・age へのパイプ）。otel-collector の到達は opt-in の NetworkPolicy で絞る（#2413）
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, IADR-0471, IADR-0548, IADR-0589, IADR-0526]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2413_plaintext-cleanup-and-otel-netpol.md
---

# IADR-0674: 復号物・秘密の後始末と otel-collector の到達の制限（#2413）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（#2413。コーディネーターの指示 2026-11-05）

## 起点・関連

- 起点 issue: #2413。
- 前提:
  - IADR-0471（バックアップは age で暗号化し、平文をディスクへ書かない）・IADR-0548（秘匿管理の raft スナップショット）。
  - IADR-0589（NetworkPolicy は段に分けて当てる）・IADR-0526（Loki・Tempo の到達の制限）。
- 作業仕様書: `.ai-context/specs/20261105_2413_plaintext-cleanup-and-otel-netpol.md`。

## コンテキスト

- バックアップの側は平文をディスクへ書かない（IADR-0471）。戻す側と手順書の側は、同じ約束を守っていなかった。
  - Postgres のリストア試験は、名前の無いボリュームに戻した平文を残す（実測）。
  - 秘匿管理のリストア試験は、手元へ復号して最後に消すだけ（途中で止まると残る）。
  - 手順書は、固定の値や秘密を `export` させ、トークン・パスワードを引数に載せ、ダンプを平文で書く箇所があった。
- otel-collector だけが NetworkPolicy を持たない。送り手（Loki・Tempo・mail-relay）は collector からの到達だけを許している。

## 決定

### 決定 1: 使い捨てのコンテナに戻す平文は tmpfs に置き、ボリュームごと消す

- Postgres: `--tmpfs /var/lib/postgresql/data -e PGDATA=/var/lib/postgresql/data`。片付けは `rm -f -v`。
- 秘匿管理: 新しい `scripts/vault-restore-drill.sh`。
  - 復号物は `age -d … | docker exec -i … 'umask 077; cat > /drill/…'` でコンテナの tmpfs へ直に流す。
  - 手元に一時ファイルを作らない（`age -o` も使わない）。スナップショットの検算もコンテナの中で行う。
- 両方とも、片付けを EXIT の trap に置く。INT / TERM / HUP は `exit` へ変え、EXIT の片付けへ進める。
- 自己試験は成功・失敗（流し込み・起動）・中断（SIGTERM）の各経路で、ボリュームごと消すことを固定する。

### 決定 2: 手順書は秘密を履歴・環境・引数に置かない

| 形 | 替える先 |
| --- | --- |
| `export X="固定の値"` / `export X="$(秘密を読む)"` | `read -rs` で読む。`export` は `( )` の中で、起動する 1 回だけに効かせる。手元のシェルの変数で足りるなら `export` しない |
| `-H "Authorization: Bearer $T"` | `-H @<(printf 'Authorization: Bearer %s\n' "$T")`。ネイティブの curl（Git Bash から C:/ の形で渡す手順）は `$WORK` の 0600 のファイル |
| `-d password="$(…)"`・`sh -c "… secret='$SEC'"` | `--data-urlencode password@<(…)`・`printf '%s' "$SEC" \| … key=-`（標準入力） |
| `pg_dump` / `pg_dumpall` / realm の書き出し / tar の退避 `> file` | `\| age -R <受取人> -o file.age`（バックアップと同じ受取人）。読むときは復号をパイプの先へだけ流す |

- `scripts/runbook-secret-handling.test.js` が `docs/`・`deploy/` の .md のコードを走査し、上の左の形を止める。直す前の字面の全件を検出することも固定する。

### 決定 3: otel-collector の到達は opt-in の NetworkPolicy で絞る（段 1）

- `deploy/local/otel-collector-netpol/`（kustomize の overlay）。既定の経路 B（`deploy/local/infra`）には入れない。
- 許す相手:
  - ingress: 基盤・取引ユニットの名前空間の Pod から 4317、Prometheus から 8888、Vault から 9514。
  - egress: kube-dns 53、Prometheus 9090・Tempo 4317・Loki 3100・mail-relay 9154・reset-floor 8080。
- `scripts/otel-collector-network-policy.test.js` が固定する:
  - 送り先は collector の両方の設定（既定・転送）から導き、宛先の Pod とポートは Service の宣言から引く。
  - 送り手は helm の endpoint・取引ユニットの otlpEndpoint・Prometheus の scrape・Vault の audit の宛先から導く。
  - 評価は k8s の規則で行い、それ以外が通らないこと・規則を 1 つ外す変異で赤になることも見る。

## 理由

- 復号物の置き場をメモリに限れば、片付けが漏れても（コンテナを消せば）ディスクに残らない。trap は失敗の経路を、tmpfs は trap が走らない経路（SIGKILL）を受け持つ。
- 手順書は人が貼り付けて使う。貼り付けた行は履歴に残り、`export` した値は以後の全プロセスへ渡る。
- otel-collector の到達は、IADR-0589 と同じく、まず opt-in で当てる。既定へ入れるのは強制を実測してから。

## 残る制約

1. tmpfs の大きさは手元のメモリが上限である。
2. SIGKILL では trap が走らずコンテナが残る。名前で消す。tmpfs なので、消せば平文は残らない。
3. otel-collector の NetworkPolicy は opt-in（段 1）。統合スタックでの強制の実測と既定への組み込み（段 2）は別の作業。
4. 手順書の検査は字面による。秘密らしく見えない名前の変数はすり抜ける。
5. `deploy/cloud/`（並行の作業）の写しは本 IADR の対象外。
