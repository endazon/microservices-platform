# 宣言的パイプライン構成（FR-14, ADR-0018, IADR-0028）

`pipeline.json` が**パイプライン構成の正**（single source of truth）である。
段（イベント購読→処理→発行）の有効/無効・キュー名の組み替えは、本ファイルの変更＋GitOps 適用のみで行う
（コア改修なし・1 営業日以内。ADR-0018）。

## 構成変更の手順

1. `pipeline.json` を編集する（例: 段の `enabled` を `false` にする、`queue` を上書きする）
2. PR を作成する → CI の `pipeline-config` ジョブがスキーマ検証（`scripts/validate-pipeline-config.js`。
   必須項目・イベント型整合・接続性・循環・重複）を行う。検証を通らない構成はマージ不可
3. マージ後、ArgoCD が同期する。ConfigMap（`pipeline-config`）の checksum 変更により、
   段をホストするサービス（`values.yaml` の `pipelineSteps: true`）がロールアウトされる
4. ロールバックは Git revert → ArgoCD 同期（直前構成へ即時復帰）

ローカル検証: `node scripts/validate-pipeline-config.js deploy/helm/microservices-platform/files/pipeline.json`

## ファイル

| ファイル | 役割 |
| --- | --- |
| `pipeline.json` | 宣言（正）。`events`（既知イベント契約型）・`sources`（同期 API 起点の発行）・`steps`（段） |
| `pipeline.schema.json` | JSON Schema（エディタ補完・レビュー用。CI 検証はスクリプトが同等規則＋意味検証を実施） |

## 実行時の挙動（誤構成対策 = fail-fast）

- 構成は `templates/pipeline-config.yaml` が `{"Pipeline": …}` 形の .NET 構成オーバレイに包んで
  ConfigMap 化し、`Pipeline__ConfigPath` でサービスへ渡る
- 各段は `IPipelineStep.StepName` で構成と対応付く。次の不整合は**起動失敗**になる:
  段の宣言漏れ／`consumer` 型完全名の不一致／`input` と実装の購読イベント型の不一致
- `enabled: false` の段は購読・キューを生成しない（警告ログのみ）
- 構成が全く無い場合（ローカル・テスト）は既定配線（全段有効）で動作する

## 新しい段（プラグイン）の追加

1. 対象サービスの `Features/<集約>/<操作>/` に `IConsumer<TIn>` ＋ `IPipelineStep` を実装する
   （コア改修不要。`*Consumer.cs`。旧 `Composable/Steps/` は単一プロジェクト構成への移送で無くなった）
2. `Program.cs` の合成ルートに `AddPlatformPipelineStep<T>(pipeline)` を 1 行追加する
3. `pipeline.json` に段を宣言する（イベント型は `events` に列挙されていること）

> 段の**入力イベント型の変更**は構成のみでは行えない（`IConsumer<TIn>` の型安全性を優先。IADR-0028）。
> 入力を変える場合はプラグイン改版（コード変更）として扱う。
