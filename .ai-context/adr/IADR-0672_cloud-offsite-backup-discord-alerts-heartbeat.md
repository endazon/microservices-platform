---
title: IADR-0672 クラウドの最小構成は、バックアップを S3 互換のオブジェクトストレージ（Object Lock・lifecycle）へ書き、警報を Discord へ送り、常に鳴る警報をクラスタの中から外の監視（Healthchecks.io）へ heartbeat として送る（#2402）
type: impl-adr
status: Accepted
related_ids: [NFR-21, NFR-03, NFR-05, ADR-0002, ADR-0006, ADR-0008, IADR-0471, IADR-0548, IADR-0571, IADR-0669, IADR-0670]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2402_cloud-backup-alerts-heartbeat.md
---

# IADR-0672: クラウドのバックアップ・警報・heartbeat（#2402）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（#2402。コーディネーターの指示 2026-11-05）

## 起点・関連

- 起点 issue: #2402（PoC セッションの監査・2026-11-05）。対の issue は #2401（クラウドの最小構成）。
- 前提:
  - IADR-0471（age で暗号化した日次のバックアップ）・IADR-0669（保管先のロックと改名の後の検算）・IADR-0670（依存先の待ち）。
  - IADR-0571（Alertmanager の受信先を Secret から initContainer で描画する形）。

## コンテキストと課題

- PoC を単一ノードの VPS へ移す。今の運用の部品は、自宅の PC に結び付いている。
  - バックアップの保管先は、PC の 2 つのドライブ（hostPath）だけである。
  - Alertmanager は、どこへも送らない。
  - 外からの死活監視は、PC の PowerShell が行う。
- ノードのファイアウォールは SSH だけを通す（#2401）。外から届く口は増やせない。
- インフラ費の上限は月 5,000 円である。ノードは 8 GB で、Prometheus の他に AST と MSP の最小構成が載る。

## 決定

1. **バックアップは、既存の本体（`backup.sh`）に S3 互換の保管先を足して書く。** 別の実装を作らない。
   - 受取人の検査・age の暗号化・依存先の待ち・staging・`SHA256SUMS` は、hostPath の回と同じ道を通る。
   - 送るのは rclone（Alpine の community パッケージ。版・sha256・署名で固定し、クラウドのイメージに同梱する）。
     資格情報は env（secretKeyRef）で rclone に渡し、引数に載せない。
   - 書く順: 同じ名前の回が無いことを確かめる → 成果物と `SHA256SUMS` を送る → 送った回を作業場所へ読み戻し、
     `SHA256SUMS` の一致と `sha256sum -c` を確かめる → 最後に `COMPLETE`（`SHA256SUMS` の sha256）を置く。
   - 🔴 **オブジェクトストレージには改名と `mkdir` の原子性が無い。** IADR-0669 のロックは使わず、
     **`COMPLETE` の無い回を完了と見なさない**ことで同じ約束（欠けた回を完了と記録しない）を守る。
     リストア試験も `COMPLETE` と `SHA256SUMS` の一致を確かめてから戻す。
   - hostPath の既定（`BACKUP_TARGETS` の既定値）は変えない。`BACKUP_TARGETS=none` で hostPath を使わない。
2. **保持は、日次 30 日・月次 12 か月とし、削除はバケットの lifecycle が行う。** スクリプトは S3 の回を消さない。
   - 月次は、その月の最初の完全な回を `monthly/` へも書く（`monthly/` に同じ月の回が無いときだけ）。
   - 消せない形: バケットを Object Lock（COMPLIANCE・既定の保持 30 日）で作る。保持の期間の内は、資格情報を持つ者でも消せない。
   - 資格情報は、バックアップのバケットだけを持つ専用のプロジェクトのものにする。スクリプトは存在の確かめと読み戻しをするので、
     読み・一覧・書きが要る。**書き込みだけには絞らない**（消せないことは Object Lock が持つ）。
   - 費用: Hetzner Object Storage の基本料（1 TB の保管・転送を含む）の範囲に収まる（暗号化したダンプは数十 MB/回）。
3. **警報は Discord の webhook へ送る。URL は Secret から initContainer が描画する。**
   - Alertmanager は v0.27.0 のまま（経路 B と同じ digest）。v0.27 の `discord_configs` は URL のファイル読みを持たない（amtool で実測。
     「no discord webhook URL provided」）ので、IADR-0571 と同じく initContainer が Secret の値を emptyDir の設定へ書く。
   - 🔴 URL が空・形が違う（`https://discord.com/api/webhooks/<数字>/<トークン>` でない）なら、Alertmanager を起動しない。
     黙って `default-null` へ落とさない —— 落ちた分は次の決定 4 の heartbeat が止まって見える。
   - Prometheus は保持 7 日・1GB、メモリの上限 768Mi（8 GB のノード）。Grafana・Loki・Tempo は置かない。
   - AST の警報（`PrometheusRule`）は、ConfigMap `ast-prometheus-rules` に入った YAML の `spec` を initContainer が取り出し、
     `promtool check rules` を通してから読む（Prometheus Operator を持ち込まない）。読めなければ起動しない。
     ConfigMap が無いときは起動し、警報 `AstAlertRulesNotLoaded` を Discord へ出す。
4. **死活は、常に鳴る警報（`Watchdog`）を Alertmanager から外の監視（Healthchecks.io の無料の枠）へ 5 分ごとに送る。**
   - クラスタの中から外へ送るだけで、受け口を増やさない。
   - Prometheus・Alertmanager・ノード・外への通信のどれが止まっても ping が止まり、外の監視がオーナーへ知らせる
     （監視の側が止まっても黙らない）。Alertmanager の設定の誤り（決定 3）も、同じく ping の停止として見える。
   - Discord への送信の失敗は、警報 `AlertmanagerDiscordFailing` が同じ check の `/fail` へ送る。
   - ping の URL も Secret から読む（Alertmanager の `webhook_configs` の `url_file`）。

## 却下した代替案

- **Alertmanager を v0.28 へ上げて Discord の URL をファイルで読む**: 経路 B と版が分かれ、digest の固定を 2 つ持つ。描画の形には先例（IADR-0571）がある。
- **外から HTTP で見る監視（uptime の SaaS がクラスタを叩く）**: 受け口を開ける必要がある。ファイアウォールの方針（SSH だけ）に反する。
- **バックアップ専用の別のスクリプト**: 暗号化・検算・依存先の待ちの約束が二重になる（片方だけ直る）。
- **スクリプトが保持の削除をする（hostPath と同じ）**: 削除の権限が要り、Object Lock と両立しない。

## 結果・残余

- 良い: オフサイトの暗号化したバックアップ・Discord への警報・外からの死活監視が、受け口を増やさずに揃う。
- 残余:
  - バックアップの Job の失敗を Prometheus は見ない（kube-state-metrics を持ち込まない）。週次のリストア試験と Job の一覧で見る。
  - Healthchecks.io そのものが止まると、知らせは届かない（外の監視を 2 重にはしない）。
  - Object Lock が使えないプロバイダでは、資格情報を持つ者が回を消せる（runbook の確かめの手順で検出する）。
  - `deploy/cloud/` の infra（#2401）との結線は、後にマージする側が行う。
