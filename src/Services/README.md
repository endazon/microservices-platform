# サービスユニット規約（FR-14, ADR-0018 / IADR-0027）

`src/Services/<ServiceName>/` は**サービスユニット**（自己完結した実装単位）である。
将来のサービス追加は、本規約に従うことで Git サブモジュール（別リポジトリ）としても
そのまま配置できる。区分の背景は
[固定/可変区分表](../../docs/tech/composability-classification.md) と
[IADR-0027](../../docs/adr/IADR-0027_composability-folder-structure.md) を参照。

## 標準レイアウト

```
src/Services/<ServiceName>/          ← サービスユニット（サブモジュール境界）
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
      Connectors/                    ←   データソースコネクタ（予約）
  tests/<ServiceName>.<Api|Worker>.Tests/
```

- 名前空間はフォルダ階層に一致させる（例: `IngestionService.Worker.Composable.Steps`）。
- 存在しない区分のフォルダは作らない（空フォルダを置かない）。

## 依存規則

1. **`Foundation/` は `Composable/` に依存しない**。可変実装へのアクセスは必ず
   `Foundation/Ports/` の抽象を介し、実装の選択・束ねは `Program.cs`（合成ルート）で行う。
   （`Foundation/` 配下に `using *.Composable.*` が現れたら規約違反。）
2. **`Composable/Steps/` の段どうしは直接参照しない**。段間の連携はイベント
   （`KnowledgePlatform.Shared.Contracts/Events/`）経由のみとする。
3. **サービスユニット外への参照は `src/Shared/` のみ許可**する。サービス間のコード参照
   （ProjectReference・型共有）を禁止する。サービス間の連携は同期 API（契約管理）または
   イベントに限る。この規則がサブモジュール切り出し可能性を担保する。

## サブモジュールとして追加する場合

1. 新サービスのリポジトリを本規約のレイアウト（`src/` + `tests/`）で作成する。
2. `git submodule add <repo-url> src/Services/<ServiceName>` で配置する。
3. `src/KnowledgePlatform.slnx` に csproj を追記する。
4. ビルド共通設定（`src/Directory.Build.props`・`src/Directory.Packages.props`）は
   ディレクトリ階層で自動継承されるため追加設定は不要。パッケージバージョンは
   中央管理（CPM）に従い、csproj に `Version=` を書かない。
5. `KnowledgePlatform.Shared.*` への参照は相対パス
   `../../../../Shared/<Project>/<Project>.csproj` とする（ユニットの配置場所が保証する）。
