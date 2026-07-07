---
title: IADR-0027 固定/可変分離のフォルダ・名前空間規約（Foundation / Composable）
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
---

# IADR-0027: 固定/可変分離のフォルダ・名前空間規約（Foundation / Composable）

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: claude（issue #102 実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: FR-14・FR-15・ADR-0018
- 関連する実装仕様書: [作業仕様書](../specs/20260708_issue-102_composability-fixed-variable-separation.md)・[固定/可変区分表（実装版）](../tech/composability-classification.md)

## コンテキストと課題

ADR-0018 はシステムを固定（土台: 同期 API 経路・ABAC・メッセージ基盤・イベント契約・正規化形式）と
可変（組み替え可能: パイプライン段・イベントバインディング・ポート実装・コネクタ）に区分した。
既存コード（FR-01〜13 実装済み）はこの区分がフォルダ構造に現れておらず（例: 段もアダプタも
ドメインサービスも同じ `Services/` フォルダに同居）、どこが組み替え対象かをコードを読まないと
判別できない。区分をコード構造として固定化する規約を決める必要がある。
あわせて、将来サービスを Git サブモジュールとして追加配置できるフォルダ構成が求められている。

## 検討した選択肢

1. **プロジェクト内を `Foundation/` / `Composable/` の二分構造へ再編し、名前空間をフォルダに一致させる**
   - 全プロジェクトで同一の規約。可変部分がフォルダ名だけで判別でき、`using` 宣言で固定/可変依存が可視化される。
   - 移動＋名前空間変更のため差分は大きいが、機械的でビルド・既存テストにより検証可能。
2. プロジェクト内のフォルダ移動のみ行い、名前空間は既存のまま維持する
   - 差分は最小だが、名前空間とフォルダの不一致が恒久化し、新規ファイルで規約が崩れていく。
3. 可変部分を別アセンブリ（`<Service>.Composable.csproj` 等）へ物理分離する
   - 分離は最も強いが、プロジェクト数が倍増し過剰分割（計画方針と不整合）。プラグイン化の設計は
     宣言的構成（後続 issue）で決めるべきで、現時点でのアセンブリ分割は先取りが過ぎる。

## 決定

選択肢 1 を採用する。全サービス・BFF・`Shared.Infrastructure` に以下を適用する。

```
<Project>/
  Program.cs  appsettings*.json  TestMarker.cs   # 合成ルート（構成で可変を束ねる）・テスト支援
  Migrations/                                    # EF Core ツール既定出力（移動しない）
  Foundation/                                    # 固定（土台）— ADR-0018 の「固定」に対応
    Endpoints/     # 同期 API（組み替え対象外・契約は docs/api/openapi.yaml）
    Domain/        # エンティティ・不変規約（冪等 ID 等）
    Persistence/   # DbContext（DB per Service）
    Ports/         # 差し替え点の抽象（インタフェース・オプション型）
    Services/      # ドメインサービス（正規化・ABAC・検索編成等）
    <ドメイン固有>/ # 必要なら追加可（例: LlmGateway.Api の Routing/ = エグレス統制）
  Composable/                                    # 可変（組み替え可能）— ADR-0018 の「可変」に対応
    Steps/         # パイプライン段（イベント購読→処理→発行）
    Adapters/      # ポート実装（外部コンポーネント接続）
    Connectors/    # データソースコネクタ（予約・未実装）
```

- **名前空間はフォルダ階層に一致**させる（例: `IngestionService.Worker.Composable.Adapters`）。
- **依存方向**: `Foundation/` → `Composable/` の参照を禁止する。可変実装へのアクセスは必ず
  `Foundation/Ports/` の抽象を介し、束ねるのは `Program.cs`（合成ルート）のみとする。
  可変実装を構成で選択する DI 登録ヘルパ（例: `ObjectStorageExtensions`）は合成コードであるため
  `Composable/` 側に置く（`Foundation/Extensions/` に置くと本規則に違反する）。
- `Composable/Steps/` が依存してよいのは `Shared.Contracts` のイベント型・自プロジェクトの
  `Foundation/Ports/`・`Foundation/Domain/` のみ。段どうしの直接参照を禁止する。
- **例外**: `Migrations/` は EF Core が既存移行と同じフォルダへ新規移行を生成するため直下に残す。
  `KnowledgePlatform.Shared.Contracts` は全体が契約（固定）のため二分構造を適用しない。
  `TestMarker` は `WebApplicationFactory<T>` 用のマーカーであり直下（ルート名前空間）へ置く。
- **サービスユニット規約**（サブモジュール考慮）: `src/Services/<Name>/` を自己完結の単位とし、
  ユニット外への参照は `src/Shared/` のみ許可する。規約の詳細は `src/Services/README.md`。

## 理由

- FR-14 の第一歩は「どこが組み替え可能か」が構造から自明になること。フォルダ・名前空間の一致は
  追加ツールなしで規約を可視化し、レビューで逸脱を検出しやすい（`Foundation/` 内に `using *.Composable.*`
  が現れたら違反）。
- 選択肢 2 は初期コストが最小だが、規約が構造に現れず漂流する。選択肢 3 は宣言的構成の設計前に
  物理境界を固定してしまい、後続 issue の設計自由度を奪う。
- サービス間コード参照の禁止と共通設定のディレクトリ継承（`Directory.Build.props` /
  `Directory.Packages.props`）により、`src/Services/<Name>/` は追加設定なしでサブモジュール化できる。

## 結果

- 良い影響: 固定/可変が構造として自明になり、後続の宣言的構成（段・バインディングの構成生成）や
  プラグイン化の対象範囲が `Composable/` 配下に確定する。サービス追加時のレイアウトが規約化される。
- 悪い影響・トレードオフ: 名前空間変更を伴う大きな（ただし機械的な）差分が一度発生する。
  名前空間が 1 階層深くなる。EF `Migrations/` のみ規約の例外として直下に残る。
- フォローアップ:
  1. 共通ステップインタフェースとイベント共通エンベロープの導入（issue #102 残項目、後続 PR）。
  2. 依存方向規則（`Foundation/` → `Composable/` 参照ゼロ）の CI 検査を追加する。当面は
     アーキテクチャテスト（`NetArchTest.Rules` 等）を各 `*.Tests` プロジェクトへ 1 ケース追加し、
     `Foundation/` 名前空間から `Composable/` 名前空間への依存が無いことを検証する。着手時期は
     共通ステップ IF を導入する後続 PR と同時（issue #102 残項目としてトラッキング）。
  3. 改行コード起因の巨大 diff 再発防止として `.gitattributes`（`* text=auto eol=lf`・`*.cs eol=lf`）
     を追加し、既存の CRLF ファイルを LF へ正規化済み（本 PR のレビュー反映）。

## 関連

- Supersedes: なし
- Superseded by: なし
