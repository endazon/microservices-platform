# バックエンド ユニット規約（FR-14, ADR-0018 / IADR-0027 / IADR-0056）

`src/backend/<unit>/` は**ユニット**（基盤または可変機能セットの自己完結した実装単位）である。
本リポジトリの主たる成果物は **platform（プラットフォーム基盤）** であり、
**knowledge（ナレッジ活用機能）** は基盤に付随する必須の可変機能ユニットである。
追加の可変機能ユニットは、本規約に従うことで Git サブモジュール（別リポジトリ）として
`src/backend/<unit>/` へそのまま配置できる（issue #210 / [IADR-0056](../../docs/adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。

## ユニット構成

```
src/backend/
  Directory.Build.props        ← 共通 MSBuild 設定（単一情報源。ユニットで上書きしない）
  Directory.Packages.props     ← パッケージ中央管理（CPM。csproj に Version= を書かない）
  platform/                    ← 基盤ユニット（本リポジトリの主成果物）
    platform.slnx
    Shared/                    ←   契約（Shared.Contracts）・横断基盤（Shared.Infrastructure）
    Bff/                       ←   エッジ集約（フロントエンドの唯一の入口）
    Services/                  ←   基盤サービス（AuthorizationService = ABAC / LlmGateway = LLM エグレス統制）
  knowledge/                   ← ナレッジ機能ユニット（付随する必須の可変機能）
    knowledge.slnx
    Services/                  ←   Document / DataSource / Conversion / Ingestion / Retrieval /
                               ←   AiAnalysis / Wiki / Feedback / Dashboard
    Tests/                     ←   KnowledgePlatform.IntegrationTests（ユニット横断の統合テスト）
  <unit>/                      ← 追加の可変機能ユニット（git submodule でリンク）
```

## サービスユニットの標準レイアウト（ユニット内）

`<unit>/Services/<ServiceName>/` は従来どおりの**サービスユニット**（自己完結した実装単位）である。
区分の背景は [固定/可変区分表](../../docs/tech/composability-classification.md) と
[IADR-0027](../../docs/adr/IADR-0027_composability-folder-structure.md) を参照。

```
<unit>/Services/<ServiceName>/
  src/<ServiceName>.<Api|Worker>/
    Program.cs                       ← 合成ルート（可変部分を構成で束ねる唯一の場所）
    appsettings*.json
    TestMarker.cs                    ← テスト支援（WebApplicationFactory 用マーカー）
    Migrations/                      ← EF Core ツール既定出力（移動しない）
    Foundation/                      ← 固定（土台）: コア改修なしでは変えない部分
      Endpoints/                     ←   同期 API（契約: docs/api/openapi.yaml。組み替え対象外）
      Domain/                        ←   エンティティ・不変規約（冪等 ID 等）
      Persistence/                   ←   DbContext（DB per Service, ADR-0002）
      Ports/                         ←   差し替え点の抽象（インタフェース・オプション型）
      Services/                      ←   ドメインサービス（ABAC・正規化・検索編成等）
      <ドメイン固有>/                ←   必要なら追加可（例: LlmGateway の Routing/）
    Composable/                      ← 可変: 構成変更・プラグインで組み替える部分
      Steps/                         ←   パイプライン段（イベント購読→処理→発行）
      Adapters/                      ←   ポート実装（外部コンポーネント接続）
      Connectors/                    ←   データソースコネクタ
  tests/<ServiceName>.<Api|Worker>.Tests/
```

- 名前空間はフォルダ階層に一致させる（例: `IngestionService.Worker.Composable.Steps`）。
- 存在しない区分のフォルダは作らない（空フォルダを置かない）。

## 依存規則

1. **`Foundation/` は `Composable/` に依存しない**。可変実装へのアクセスは必ず
   `Foundation/Ports/` の抽象を介し、実装の選択・束ねは `Program.cs`（合成ルート）で行う。
   （`Foundation/` 配下に `using *.Composable.*` が現れたら規約違反。）
2. **`Composable/Steps/` の段どうしは直接参照しない**。段間の連携はイベント
   （`platform/Shared/KnowledgePlatform.Shared.Contracts/Events/`）経由のみとする。
3. **ユニット外への参照は `src/backend/platform/Shared/` の 2 プロジェクトのみ許可**する。
   platform → 可変機能ユニットの参照は禁止（一方向依存）。サービス間のコード参照
   （ProjectReference・型共有）も従来どおり禁止し、連携は同期 API（契約管理）または
   イベントに限る。この規則がユニットのサブモジュール切り出し可能性を担保する。
   - 例外: 統合テスト（`Tests/`）は検証対象サービスへの ProjectReference を許可する
     （例: IntegrationTests → AuthorizationService.Api）。

## ビルド

- 各ユニットはユニット直下の slnx（`platform.slnx` / `knowledge.slnx`）でビルドする。
  ルート集約ソリューションは置かない（CI はユニット毎に restore/build/test/format を実行する）。
- 共通 MSBuild 設定（`Directory.Build.props` / `Directory.Packages.props`）は `src/backend/` に
  置き、ディレクトリ階層でユニットへ自動継承される（submodule ユニットも配置により継承される。
  ユニット単独リポジトリでのビルドには自前の同等設定が必要）。

## ユニットをサブモジュールとして追加する場合

1. 新ユニットのリポジトリを本規約のレイアウト（`<unit>.slnx` + `Services/<Name>/` 構成）で作成する。
2. `git submodule add <repo-url> src/backend/<unit>` で配置する。
3. `KnowledgePlatform.Shared.*` への参照は相対パス
   `..\..\..\..\..\platform\Shared\<Project>\<Project>.csproj`（サービス csproj から）とする。
4. CI のビルド対象へユニットの slnx を追加する（`.github/workflows/ci.yml`）。
5. パッケージバージョンは中央管理（CPM）に従い、csproj に `Version=` を書かない。
