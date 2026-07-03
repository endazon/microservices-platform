# 実装ADR（Implementation ADR）

本リポジトリ内の意思決定記録（Implementation ADR）の索引である。実装に閉じた技術・設計・運用の決定を `IADR-XXXX` として記録する（必須）。

## 計画ADR との違い

| | 計画ADR | 実装ADR |
| --- | --- | --- |
| 場所 | 計画リポ `projects/<name>/07_adr/` | 本リポ `docs/adr/` |
| ID | `ADR-XXXX` | `IADR-XXXX` |
| 対象 | 上流の意思決定（プロダクト全体） | 実装レベルの意思決定（内部設計・ライブラリ選定等） |

> 計画に影響する決定は、実装ADR に記録するのではなく `/plan-feedback` で計画側へ環流する。

## 運用ルール

- 1 ファイル = 1 意思決定。`IADR-<連番4桁>_<タイトル>.md`（雛形 `docs/templates/adr_template.md`、`/new-spec adr` で採番作成）。
- 連番はリポジトリ内で一意・昇順・欠番なし。
- 状態は `Proposed / Accepted / Deprecated / Superseded`。既存決定を覆す場合は新 IADR を作り、旧 IADR に `Superseded by IADR-XXXX` を追記する。
- 重要な実装判断は必ず IADR に残す（必須）。

## 一覧

| IADR | タイトル | 状態 |
| --- | --- | --- |
| IADR-0000 | 実装意思決定の記録方針 | Accepted |
| IADR-0001 | カタログの正本所有と DocumentNormalized の購読責務 | Accepted |
| IADR-0002 | 取り込みパイプライン構造・冪等チャンク ID・Qdrant ブートストラップ | Accepted |
| IADR-0003 | EFCore.Relational のバージョン直接ピン（MSB3277 解消） | Accepted |
| IADR-0004 | ABAC フィルタの多値 allow-list 化と deny-by-default | Accepted |
| IADR-0005 | 指定データ範囲は ABAC スコープと交差させ権限を広げない（narrowing-only） | Accepted |
| IADR-0006 | ABAC 属性・ポリシー管理の検証と DocumentService 疎結合 | Accepted |
| IADR-0007 | LLM 呼び出し先の切替は設定駆動のエンドポイント定義＋越境マトリクスで行う | Accepted |
| IADR-0008 | 正規化変換はポート分離＋deny-by-default 縮退＋決定的 DocumentId で構成する | Accepted |
| IADR-0009 | Wiki 閲覧の権限外アクセスは 404 で存在秘匿し、ABAC はメモリ内で後段評価する | Accepted |
| IADR-0010 | 回答フィードバックは専用サービスで保持し、1 ユーザー 1 回答は upsert で冪等化する | Accepted |
| IADR-0011 | 業務指標ダッシュボードは専用サービスで集計し、回答品質は FeedbackService を単一の出所とする | Accepted |
| IADR-0012 | Retrieval /search は Scope 未指定を deny 扱いにし fail-closed で ABAC を強制する | Accepted |
