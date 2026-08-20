---
title: IADR-0028 宣言的パイプライン構成は JSON 単一宣言＋起動時 fail-fast 照合で実現する
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md
---

# IADR-0028: 宣言的パイプライン構成は JSON 単一宣言＋起動時 fail-fast 照合で実現する

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: claude（issue #111 実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: FR-14・ADR-0018
- 関連する実装仕様書: [作業仕様書](../specs/20260708_issue-111_declarative-pipeline-config.md)・
  [IADR-0027](./IADR-0027_composability-folder-structure.md)・
  [運用手順](../../deploy/helm/microservices-platform/files/README.md)

## コンテキストと課題

ADR-0018 は「段構成・イベント接続を Git 管理の構成定義で宣言し GitOps で適用する」ことを決定したが、
宣言の形式・配送方法・実装との整合の取り方（誤構成対策）は実装リポジトリの設計に委ねられた。
決める点: (1) 宣言の形式と置き場所、(2) 実行時への配送、(3) 宣言と実装の不整合の検出方法、
(4) 組み替え自由度の初期範囲。

## 検討した選択肢

1. **JSON 単一宣言（Helm チャート内 `files/pipeline.json`）＋ ConfigMap 配送＋起動時 fail-fast 照合（採用）**
   - 宣言は 1 ファイル。CI（依存なし Node スクリプト）でスキーマ＋意味検証。Helm が
     `{"Pipeline": …}` オーバレイに包んで ConfigMap 化し、`.NET` 構成としてそのまま読む。
     段は `IPipelineStep.StepName`（static abstract）で構成と対応し、不整合は起動失敗。
2. サービスごとの appsettings に分散宣言
   - 配送は単純だが「全体の配線」が 1 か所で見えず、接続性・循環の検証ができない。FR-15
     （実効構成の集約）とも整合しない。
3. MassTransit トポロジを完全リフレクション生成（アセンブリスキャン＋構成のみで購読型も決定）
   - 自由度は最大だが、`IConsumer<T>` の型安全性を放棄し、構成ミスが実行時（メッセージ到達時）
     まで顕在化しない。誤構成リスク（ADR-0018 のトレードオフ）を安全弁なしで増幅する。

## 決定

選択肢 1 を採用する。

- **宣言の形式**: JSON（`deploy/helm/knowledge-platform/files/pipeline.json`）。YAML でなく JSON と
  したのは、CI（Node 標準モジュールのみ）・.NET 構成（`AddJsonFile`）・JSON Schema の三者で
  追加依存なしに同一ファイルを扱えるため。
- **検証**: CI 必須ジョブ `pipeline-config` が V1〜V6（必須項目・一意性・イベント型整合・接続性・
  循環・型名形式）を検証する。検証器自体も `--self-test`（違反フィクスチャ内蔵）で毎回試験する。
- **配送**: Helm ConfigMap ＋ checksum アノテーション（構成変更→ロールアウト）。ホットスワップは
  行わない（計画どおり再デプロイ反映）。
- **実装との整合（fail-fast）**: 段は `IPipelineStep.StepName` で宣言と対応する。起動時に
  ①段の宣言漏れ ②`consumer` 型完全名の不一致 ③`input` と `IConsumer<TIn>` の型名不一致を検出し
  即時起動失敗とする。`enabled: false` は購読・キュー非生成。**構成が無い場合は既定登録**
  （現行等価。ローカル・テスト互換）。
- **組み替え自由度の初期範囲**: 段の有効/無効・キュー名上書きに限定する。**段の入力イベント型の
  実行時再バインドは行わない**。入力変更はプラグイン改版（コード変更＋宣言更新）として扱う。

## 理由

- 単一宣言はレビュー・監査・FR-15（宣言と実効の突合）の基礎になる。分散宣言では循環・接続性を
  検証できない。
- fail-fast は ADR-0018 が求める安全弁（誤構成対策）の実行時側の実装。CI（宣言内の整合）と
  起動時（宣言と実装の整合）の二段構えにすることで、適用漏れ・名称ずれが本番でメッセージを
  取りこぼす前に検出される。
- 型安全性の放棄（選択肢 3）は、得られる自由度（入力型の構成変更）に対して障害モードが悪すぎる。
  計画も「初期は直列＋購読追加に限定」としており、実需要が出た時点で共通エンベロープ
  （#102 残項目）とセットで再設計するのが妥当。

## 結果

- 良い影響: 段の有効/無効・購読の組み替えがコード改修なし（pipeline.json 変更＋GitOps 適用のみ）で
  可能になる。全体配線が 1 ファイルで可視化され、#112（構成情報 API・ドリフト検出）の突合対象が
  確定する。共通ステップインタフェース（`IPipelineStep`）により #102 残項目の一つが解消する。
- 悪い影響・トレードオフ: 宣言と実装の二重管理が残る（fail-fast で不整合は検出されるが、宣言更新の
  手間はある）。`outputs` は実行時未検証（CI の静的検証のみ。実効検証は #112 ドリフト検出で扱う）。
- フォローアップ: イベント共通エンベロープ導入時に `events` 宣言をバージョン付きへ拡張する。
  #112 で自己申告（イントロスペクション）に本構成の実効値を含める。

## 関連

- Supersedes: なし
- Superseded by: なし
