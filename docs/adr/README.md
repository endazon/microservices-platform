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
| IADR-0010 | フィードバックサービスと upsert | Accepted |
| IADR-0011 | ダッシュボードサービスの利用状況集計 | Accepted |
| IADR-0012 | Retrieval /search は Scope 未指定を deny 扱いにし fail-closed で ABAC を強制する | Accepted |
| IADR-0013 | Wiki 閲覧は自前軽量読み取り API を採用し ADR-0011 の Supersede を計画へ提案する | Superseded（by IADR-0020） |
| IADR-0014 | Qdrant の ABAC 属性ペイロードは両表現で復元し、フィルタキー解釈を実機確認する | Accepted |
| IADR-0015 | CI トリガーの develop 整合・コミット規約チェック・CHANGELOG 誤帰属補正 | Accepted |
| IADR-0016 | Microsoft.OpenApi を推移的ピンでパッチ版に固定し NU1903 を解消する | Accepted |
| IADR-0017 | mesh 導入までのサービス間認証はネットワーク分離を第一防御とする | Superseded by IADR-0026 |
| IADR-0018 | 推移依存の脆弱性を CI で定期スキャンする | Accepted |
| IADR-0019 | データソースが原本へ既定 ABAC 属性（機密区分）を付与する | Accepted |
| IADR-0020 | Wiki.js を配備し WikiService を「同期・ABAC ゲートウェイ」へ縮退する（IADR-0013 を Supersede、ADR-0011 に追従） | Accepted |
| IADR-0021 | Wiki.js への同期は GraphQL API push を採用する | Accepted |
| IADR-0022 | 既定モデルを opus 化し、fable-5（最難関）と GitHub Copilot 経路を設定駆動で追加する | Accepted |
| IADR-0023 | 文書の削除・アーカイブを Wiki.js へ伝播する（削除イベント新設＋status 拡張） | Accepted |
| IADR-0024 | MinIO のバケット/キー設計・バージョニング・アクセス制御と共有クライアント | Accepted |
| IADR-0025 | 埋め込みを機密区分ルーティング（Voyage 既定＋高機密セルフホスト fail-closed）とモデル別コレクションで実装する | Accepted |
| IADR-0026 | Istio STRICT mTLS をサービス間認証の第一防御とし、IADR-0017（ネットワーク分離）を解消する | Accepted |
| IADR-0027 | 固定/可変分離のフォルダ・名前空間規約（Foundation / Composable、ADR-0018 対応） | Accepted |
| IADR-0028 | 宣言的パイプライン構成は JSON 単一宣言＋起動時 fail-fast 照合で実現する（FR-14, ADR-0018） | Accepted |
| IADR-0029 | 構成情報 API は BFF 配下の管理 API へ同居させ、自己申告集約＋宣言突合でドリフトを検出する（FR-15, ADR-0018） | Accepted |
| IADR-0030 | 運用者ロールは platform-operator を新設し ConfigViewer ポリシーで判定する（FR-15, SC-11） | Accepted |
| IADR-0031 | 送信者名クレームは preferred_username を Identity.Name に解決する（FR-08, FR-15） | Accepted |
| IADR-0032 | Wiki.js の dev ホスト公開は残し、本番系(Helm)の非公開を回帰ガードで保証する（IADR-0020 追補） | Accepted |
| IADR-0033 | フロントエンド SPA 基盤（React+TS+Vite、foundation/features 分離、Keycloak OIDC、BFF 境界） | Accepted |
| IADR-0034 | フロントエンド カバレッジゲート（単体テストのカバレッジ計測＋ラチェット型しきい値 CI） | Accepted |
| IADR-0035 | フロントエンドのロールベース・ナビゲーションと存在秘匿（SC-09/10/11、realm ロール判定） | Accepted |
| IADR-0036 | SC-11 構成ビューアの可視化方式（グラフ描画ライブラリ非導入、CSS チェーン＋表） | Accepted |
| IADR-0037 | LLM 回答の SSE ストリーミング（egress ゲート保持、SC-01・FR-04/FR-11） | Accepted |
| IADR-0038 | 文書閲覧の BFF 側 ABAC ゲーティングと本文サーバサイド取得（SC-03・FR-06/FR-12） | Accepted |
| IADR-0039 | データソース管理の BFF 集約と管理系画面のロールゲーティング | Accepted |
| IADR-0040 | 管理者設定（ABAC）の BFF 透過中継と AdminOnly ゲーティング | Accepted |
| IADR-0041 | 文書管理（書き込み）の BFF 集約とスコープ内限定・楽観ロック透過 | Accepted |
| IADR-0042 | 変換ジョブ読み取りモデル（インメモリ）と状況照会・人手補正 API | Accepted |
| IADR-0043 | 変換ジョブ読み取りモデルの永続化（Postgres+EF）と非同期ストア | Accepted |
| IADR-0044 | バックエンドサービスの書き込み/管理APIへの認可強制（多層防御） | Accepted |
| IADR-0045 | BFF 文書書き込みのスコープ確認往復は多層防御の要のため現時点で維持し最適化を保留する | Accepted |
| IADR-0046 | 構成バージョン履歴の正データ源は GitOps 層とし、API は注入スライスを surfacing する | Accepted |
| IADR-0047 | 文書の必須属性（機密区分）のサーバー側検証 | Accepted |
| IADR-0048 | バックエンドは .NET 10 / C# 13 を採用する（計画制約「.NET 8」からの乖離） | Accepted |
| IADR-0049 | コンポーザビリティ標準（共通エンベロープ・CI契約テスト・ステージング適用順序）の段階適用と繰延条件 | Accepted |
| IADR-0050 | HPA/PDB の適用対象はステートレス要求処理系に限定し、キュー駆動ワーカーは対象外とする | Accepted |
| IADR-0051 | データソースコネクタのポート分離（Discover/Fetch）と filesystem コネクタ・同期基盤 | Accepted |
| IADR-0052 | 性能負荷試験ツールに k6 を採用する | Accepted |
| IADR-0053 | Wiki コネクタは設定駆動の汎用 REST 契約で実装し、製品固有アダプタは後続とする | Accepted |
| IADR-0054 | SaaS コネクタは設定駆動の汎用 REST 契約＋カーソルページング＋429 バックオフで実装する | Accepted |

> **索引 backfill に関する注記**: 本 PR は既存債務（0039–0046 未掲載）の解消と併せて索引を欠番なしに揃える。
> 実体ファイルの所在は **0047＝PR #211（マージ済）／0050＝PR #213（マージ済）／0048・0049＝本 PR**。#211・#213 は
> 既に develop へマージ済みで対応ファイルが存在するため、本 PR マージ後の索引に不整合は残らない（#211/#213 は
> README を編集しないため索引更新の競合も生じない）。
