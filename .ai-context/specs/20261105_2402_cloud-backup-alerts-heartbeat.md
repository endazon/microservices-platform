---
title: 作業仕様書 — クラウドの最小構成で、バックアップを S3 互換のオブジェクトストレージへ書き、警報を Discord へ送り、クラスタの中から外の監視へ heartbeat を送る（#2402）
type: spec
status: done
related_ids: [NFR-21, NFR-03, NFR-05, ADR-0002, ADR-0006, ADR-0008, IADR-0471, IADR-0548, IADR-0571, IADR-0669, IADR-0670, IADR-0672]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2402"
---

# 作業仕様書 — クラウドのバックアップ・警報・heartbeat（#2402）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-05。develop `b05b19b9`）。
> クラウドの最小構成そのもの（`deploy/cloud/` の infra・`values-cloud.yaml`）は #2401 が並行して作る。
> 本件は `deploy/cloud/backup/` と `deploy/cloud/observability/` だけを新設し、#2401 のファイルに触れない（結線は後にマージする側が行う）。

## 事象（issue の記録）

- バックアップの保管先は Windows の hostPath の 2 本だけで、オフサイトが無い。
- Alertmanager の通知先が無い（compose は `default-null`。経路 B は mail-relay 宛てだけ）。NFR-03 の障害に誰も気付けない。
- ホストの外からの死活監視が、PowerShell 前提である。

## 方針（IADR-0672）

1. **バックアップ**: 既存の `backup.sh` に、S3 互換の保管先（`BACKUP_S3_DEST`）へ書く経路を足す。
   - 受取人の検査・age・依存先の待ち・staging・`SHA256SUMS` は、そのまま流用する。
   - 送るのは rclone（S3 の資格情報は env の secretKeyRef。引数に載せない）。
   - 書き方: 回が既に無いことを確かめる → 成果物と `SHA256SUMS` を送る → 送った回を読み戻して検算する → 最後に `COMPLETE`（`SHA256SUMS` の sha256）を置く。
     ロック（IADR-0669）の代わりに、`COMPLETE` の無い回を「完了」と見なさない。
   - 保持: 日次（`daily/`）は 30 日、月の最初の完全な回（`monthly/`）は 12 か月。削除はバケットの lifecycle が行い、スクリプトは消さない。
   - 消せない形: バケットの Object Lock（COMPLIANCE・30 日）。資格情報は、バックアップ専用のプロジェクトに閉じる。
   - hostPath の既定（`BACKUP_TARGETS` の既定値）は変えない。クラウドの CronJob は `BACKUP_TARGETS=none` で hostPath を使わない。
   - リストア試験: `backup-restore-drill.sh --from-s3 <回>` が、rclone で暗号文だけを作業場所へ落とし、`COMPLETE` を確かめてから既存の手順で戻す。
2. **警報**: `deploy/cloud/observability/` に Prometheus と Alertmanager を置く（Grafana・Loki・Tempo は置かない）。
   - Discord の webhook の URL は Secret `alertmanager-discord` のキー `webhook-url` から、initContainer が描画する（Alertmanager v0.27 の `discord_configs` は URL のファイル読みを持たない。amtool で実測）。
   - URL が空・形が違えば、Alertmanager は起動しない（initContainer が失敗する）。そのとき heartbeat が止まり、外の監視が知らせる。
   - AST の `PrometheusRule` は、ConfigMap `ast-prometheus-rules` に入れたものを initContainer が `spec` だけ取り出し、`promtool check rules` を通してから読む。
     ConfigMap が無いときは起動するが、警報 `AstAlertRulesNotLoaded` が Discord へ出る。
   - 8 GB のノード: Prometheus は保持 7 日・1GB、メモリの上限 768Mi。
3. **heartbeat**: 常に鳴る警報 `Watchdog` を、Alertmanager の webhook で Healthchecks.io（無料）へ 5 分ごとに送る。
   - 受け口は開けない（クラスタの中から外へ送るだけ）。
   - Prometheus・Alertmanager・ノード・外への通信のどれが止まっても、ping が止まり、Healthchecks.io がオーナーへ知らせる。
   - Discord への通知の失敗は、警報 `AlertmanagerDiscordFailing` が同じ check の `/fail` へ送る（Discord が届かないことも黙らない）。
   - オーナーの手順は runbook に書く。

## 受け入れ基準（issue の写像）

- [x] AC1: バックアップの回が、オブジェクトストレージ（モック）に暗号化した成果物を書き、検算が通る（`backup.test.sh` T-2402-01〜08）。
- [x] AC2: リストア試験が、オブジェクトストレージの回から戻せる（`backup-restore-drill.sh --self-test` T-2402-20〜24）。
- [x] AC3: Alertmanager の描画に Discord の receiver があり、URL の値が描画に含まれない（`cloud-ops.test.js` T-2402-40〜47）。
- [x] AC4: heartbeat が途絶えたとき、外の監視からオーナーに通知が届く手順が書かれている（runbook。`cloud-ops.test.js` T-2402-60〜62）。
- [x] AC5: ローカルの PoC の既存のバックアップ（hostPath）は、そのまま動く（既存の試験すべて＋ T-2402-09・`platform-backup.test.js`）。
- [x] 修正の前に、上の試験が赤になる。

## 母集合（規則 9・10）

- **規則 9**: 走査の語は「`BACKUP_TARGETS`」「`--run-dir`」「`default-null`」「`webhook`」「`heartbeat`」「`host-liveness`」「`deploy/cloud`」である。
  範囲は `deploy/`・`scripts/`・`docs/operations/`・`.ai-context/adr/`・`.github/workflows/` とした。
  - 改める:
    - `deploy/local/platform-backup/script/backup.sh`・`backup.test.sh`（S3 の経路を足す。hostPath の分岐は変えない）。
    - `scripts/backup-restore-drill.sh`（`--from-s3`）。
    - `.github/workflows/ci.yml`（新しい試験 `scripts/cloud-ops.test.js` と amtool の導入）。
    - IADR の索引（`.ai-context/adr/README.md`）。
    - 新設: `deploy/cloud/backup/`・`deploy/cloud/observability/`・`docs/operations/cloud-backup-alerts-heartbeat-runbook.md`・IADR-0672。
  - 対象外:
    - `deploy/alertmanager/alertmanager.yml`（compose）と `deploy/local/observability/`: クラウドの経路ではない。変えない。
    - `scripts/host-liveness-monitor.ps1`: ローカルの PoC の監視として残す（クラウドへ移った後の扱いは #2401 と AST の移行の issue が決める）。
    - `scripts/check-prometheus-alerts-parity.js`: 突き合わせるのは compose と経路 B の inline だけで、クラウドの規則は突き合わせの外（クラウドの規則は自前の 3 本と AST の規則だけ）。
- **規則 10**: `backup.sh` の冒頭の「🔴 deploy/local 専用」は、クラウドの CronJob もこの本体を使うので誤りになる。冒頭の説明を直す。
  runbook（platform-infra-backup-runbook.md）は hostPath の手順書のまま正しい。クラウドの手順は新しい runbook へ分け、互いに参照する。

## 残すもの（本件の外）

- バックアップの Job の失敗を Prometheus で見る手段（kube-state-metrics 等）は持ち込まない。失敗はオーナーの週次のリストア試験と Job の一覧で見る（IADR-0672 の残余）。
- `deploy/cloud/` の infra（#2401）との結線（kustomization の resources への追加・Secret の名前の突き合わせ）は、後にマージする側が行う。
