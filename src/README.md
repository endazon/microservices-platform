# src/ ユニット規約（FR-14, ADR-0018 / IADR-0027 / IADR-0056）

`src/<unit>/` は**ユニット**（基盤または可変機能セットの自己完結した実装単位。`backend/` と
`frontend/` を持つ）である。本リポジトリの主たる成果物は **platform（プラットフォーム基盤）** であり、
**knowledge（ナレッジ活用機能）** は基盤に付随する必須の可変機能ユニットである。
追加の可変機能ユニットは、本規約に従うことで Git サブモジュール（別リポジトリ）として
`src/<unit>/` へそのまま配置できる（issue #210 / [IADR-0056](../docs/adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。

## ユニット構成

```
src/
  Directory.Build.props        ← バックエンド共通 MSBuild 設定（単一情報源。ユニットで上書きしない）
  Directory.Packages.props     ← パッケージ中央管理（CPM。csproj に Version= を書かない）
  package.json                 ← フロントエンド npm workspaces ルート（workspaces: ["*/frontend"]）
  vitest.config.ts             ← フロント単体テスト＋カバレッジ（全ユニット横断・しきい値ゲート）
  eslint.config.js             ← フロント lint（全ユニット横断）
  platform/                    ← 基盤ユニット（本リポジトリの主成果物）
    backend/
      backend.slnx
      Shared/                  ←   契約（Shared.Contracts）・横断基盤（Shared.Infrastructure）
      Bff/                     ←   エッジ集約（フロントエンドの唯一の入口）
      Services/                ←   基盤サービス（AuthorizationService = ABAC / LlmGateway = LLM エグレス統制）
    frontend/                  ←   SPA 基盤（アプリホスト + foundation。可変ユニットの features を合成）
  knowledge/                   ← ナレッジ機能ユニット（付随する必須の可変機能）
    backend/
      backend.slnx
      Shared/                  ←   ユニット固有契約（Knowledge.Contracts = ドメインイベント。IADR-0059）
      Services/                ←   Document / DataSource / Conversion / Ingestion / Retrieval /
                               ←   AiAnalysis / Wiki / Feedback / Dashboard
      Tests/                   ←   Knowledge.IntegrationTests（ユニット横断の統合テスト）
    frontend/                  ←   ナレッジ画面 features（home, sc01..sc11）
  <unit>/                      ← 追加の可変機能ユニット（git submodule でリンク。backend/・frontend/ を持つ）
```

## サービスユニットの標準レイアウト（backend 内）

`<unit>/backend/Services/<ServiceName>/` は従来どおりの**サービスユニット**（自己完結した実装単位）である。
区分の背景は [固定/可変区分表](../docs/tech/composability-classification.md) と
[IADR-0027](../docs/adr/IADR-0027_composability-folder-structure.md) を参照。

```
<unit>/backend/Services/<ServiceName>/
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
2. **`Composable/Steps/` の段どうしは直接参照しない**。段間の連携はイベント経由のみとする。
   イベント契約はそのユニットの契約プロジェクト `<unit>/backend/Shared/<Unit>.Contracts/Events/`
   に置く（knowledge ユニットは `knowledge/backend/Shared/Knowledge.Contracts/Events/`。
   platform 横断の共通契約は `platform/backend/Shared/Platform.Shared.Contracts/`。IADR-0059）。
3. **ユニット外への参照は `src/platform/backend/Shared/` の 2 プロジェクトのみ許可**する。
   platform → 可変機能ユニットの参照は禁止（一方向依存）。サービス間のコード参照
   （ProjectReference・型共有）も従来どおり禁止し、連携は同期 API（契約管理）または
   イベントに限る。この規則がユニットのサブモジュール切り出し可能性を担保する。
   - 例外1: 統合テスト（`Tests/`）は検証対象サービスへの ProjectReference を許可する
     （例: IntegrationTests → AuthorizationService.Api）。
   - 例外2: フロントエンドの可変ユニットは `@foundation`（platform/frontend の基盤）を参照してよい。
     platform/frontend 側から可変ユニットを参照するのは合成点（`platform/frontend/src/features/index.ts`）のみとする。
   - 例外3: BFF の合成点（`platform/backend/Bff/Platform.Bff/`。合成点 `Composition/`）のみ、可変ユニットの BFF
     エンドポイントプロジェクト（`<unit>/backend/Bff/`）を参照してよい（例外2 の backend 版。IADR-0063）。
     可変ユニットは自分の BFF エンドポイントを合成点経由で BFF へ組み込む。合成点以外の platform → 可変ユニット
     参照は引き続き禁止（BFF → 可変ユニットのサービス直接参照も不可。連携は同期 API/イベント）。

## ビルド

- **バックエンド**: 各ユニットの `backend/backend.slnx` でビルドする。ルート集約ソリューションは
  置かない（CI はユニット毎に restore/build/test/format を実行する）。共通 MSBuild 設定
  （`Directory.Build.props` / `Directory.Packages.props`）は `src/` に置き、ディレクトリ階層で
  全ユニット（submodule ユニット含む）へ自動継承される（ユニット単独リポジトリでのビルドには
  自前の同等設定が必要）。
- **フロントエンド**: `src/` を npm workspaces ルート（`workspaces: ["*/frontend"]`）とし、
  単一 lock で管理する。開発コマンドは `src/` で実行する（詳細は
  [platform/frontend/README.md](platform/frontend/README.md)）。

## ユニットをサブモジュールとして追加する場合

詳細な運用手順（テンプレート・CI・トークン・バージョン固定）は
[`docs/how-to/adding-a-unit-submodule.md`](../docs/how-to/adding-a-unit-submodule.md) を参照。要点は以下。

1. 新ユニットのリポジトリを雛形 [`templates/unit-template/`](../templates/unit-template/README.md) から作成する
   （`backend/backend.slnx` + `backend/Services/<Name>/`、`frontend/package.json` + `frontend/src/features/`）。
2. `git submodule add <repo-url> src/<unit>` で配置する。
3. バックエンド: `Platform.Shared.*` への参照は相対パス
   `..\..\..\..\..\..\platform\backend\Shared\<Project>\<Project>.csproj`（サービス csproj から）とする。
   **CI は編集不要**（`.github/workflows/ci.yml` は `src/*/backend/backend.slnx` を自動発見する。IADR-0060）。
   追加ユニットが private submodule の場合は checkout の `submodules: recursive` + トークンを有効化する。
4. フロントエンド: workspaces は `"*/frontend"` のため自動認識される。platform の合成点
   （`platform/frontend/src/features/index.ts`）へ import を 1 行追加する。
5. パッケージバージョンは中央管理（CPM）に従い、csproj に `Version=` を書かない。ユニットは常設の
   `Directory.Build.props` を持たない（配置時に単一情報源を上書きするため。単独ビルドは how-to 参照）。
