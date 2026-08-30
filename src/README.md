# src/ ユニット規約（FR-14, ADR-0018 / IADR-0027 / IADR-0056）

`src/<unit>/` は**ユニット**（基盤または可変機能セットの自己完結した実装単位。`backend/` と
`frontend/` を持つ）である。本リポジトリの主たる成果物は **platform（プラットフォーム基盤）** であり、
**knowledge（ナレッジ活用機能）** は基盤に付随する必須の可変機能ユニットである。
追加の可変機能ユニットは、本規約に従うことで Git サブモジュール（別リポジトリ）として
`src/<unit>/` へそのまま配置できる（issue #210 / [IADR-0056](../.ai-context/adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。

## ユニット構成

```
src/
  Directory.Build.props        ← バックエンド共通 MSBuild 設定（単一情報源。ユニットで上書きしない）
  Directory.Packages.props     ← パッケージ中央管理（CPM。csproj に Version= を書かない）
  package.json                 ← フロントエンド pnpm workspace ルート（pnpm-workspace.yaml が正）
  pnpm-workspace.yaml          ← workspace メンバの単一情報源（本ファイルが正。列挙を他所へ複写しない。IADR-0121 決定 2）
  vitest.config.ts             ← フロント単体テスト＋カバレッジ（全ユニット横断・しきい値ゲート）
  eslint.config.js             ← フロント lint（全ユニット横断）
  packages/                    ← ユニットに属さない共有ワークスペースパッケージ（IADR-0121 決定 4）
    ui/                        ←   @platform/ui: デザイントークン(Tailwind v4)・cn()・shadcn/ui 派生プリミティブ・Storybook
                               ←   注: `.gitignore` の NuGet 用 `**/[Pp]ackages/*` と名前が衝突するため
                               ←   `!src/packages/**` で除外解除している。ここへパッケージを足すときは
                               ←   `git status` に現れることを必ず確認する（無視されてもビルドは通る）
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
[IADR-0027](../.ai-context/adr/IADR-0027_composability-folder-structure.md) を参照。

**現行の標準は次節「サービス直下の標準構成」ただ 1 つである。**

> **［歴史的経緯］** 本節にはかつて「現行実態（移送波までの経過措置）」として、
> `src/<ServiceName>.<Api|Worker>/` ・ `src/<ServiceName>.Application/` ・ `.Domain/` ・ `.Contracts/` ・
> `.SharedKernel/` ・ `tests/<ServiceName>.<Api|Worker>.Tests/` からなる 8 要素の樹形図と、
> それに付随する「名前空間はフォルダ階層に一致させる」「固定/可変の区分（`Foundation/` / `Composable/`）は
> **層プロジェクト内**の第 1 階層フォルダとして温存する」の 2 項が置かれていた。
>
> **これらは [IADR-0282](../.ai-context/adr/IADR-0282_single-project-vsa-structure.md)（8 要素プロジェクトの撤回）
> と計画 `ADR-0065` の時点で古くなっており、実在しない構造を「現行実態」と述べていた**ため撤去した。
> 実測（2026-08-30）: `src/` / `tests/` の中間層・層プロジェクトの `.csproj`・`Services/` 配下の
> `Foundation/` / `Composable/`・`.Api.Tests` / `.Worker.Tests` の `.csproj` は**いずれも 0 件**。
> 移送の記録と踏んだ罠は [`20260828_wave45-vsa-migration.md`](../.ai-context/specs/20260828_wave45-vsa-migration.md)、
> `Worker/` 中間層の撤去は [`20260830_issue-1061_remove-worker-layer.md`](../.ai-context/specs/20260830_issue-1061_remove-worker-layer.md)。
>
> 🔴 **`Foundation/` そのものが消えたわけではない。** ユニット共有プロジェクト
> （`Shared/Platform.Shared.Infrastructure`）では**今も現役**である（2026-08-30 実測で 48 ファイル）。
> 消えたのは**サービスの層プロジェクト内の第 1 階層フォルダとしての区分**であって、
> 固定/可変の分類そのもの（[IADR-0027](../.ai-context/adr/IADR-0027_composability-folder-structure.md)・
> [固定/可変区分表](../docs/tech/composability-classification.md)）ではない。

- **空のフォルダを置かない。** 実体を持たない区分のフォルダは作らない。
  この規則は**プロジェクトの内側の区分フォルダ**（`Shared/` 配下の `Foundation/` / `Adapters/` 等）に掛かる。
  次節のサービス直下の標準構成（`Features/` / `Domain/` / `Infrastructure/` / `Common/` / `Tests/`）は
  **いずれも実体を持つのが常態**であり、`.gitkeep` の枠置き規範は計画 `ADR-0065` 決定 4 が撤回した。

### サービス直下の標準構成（単一プロジェクト＋フォルダ規範。2026-08-28 裁定）

オーナー裁定 2026-08-28（[IADR-0282](../.ai-context/adr/IADR-0282_single-project-vsa-structure.md)）
により、サービスは**単一プロジェクト**（`Services/<Name>/<Name>.csproj`）とし、層は
**Features/<集約>/<操作>/（Endpoint / Command|Query / Handler）・Domain/・Infrastructure/・
Common/・Tests/ のフォルダ**で分ける。8 要素の実プロジェクト分割（IADR-0280。Superseded）と
`.gitkeep` の枠は撤回された。計画側の 8 要素条文（`12_backend-application-stack.md`）は
改定を環流中である（planning#490 のコメント）。

- **`Api` と `Worker` は排他**（実行入口は 1 サービスに 1 つ。
  [IADR-0219](../.ai-context/adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) 決定 2 は不変）
  だが、**それは `Program.cs` の形の違いであってディレクトリ階層の違いではない**（計画 ADR-0065 決定 6）。
  **`Services/<Name>/Worker/` のような中間ディレクトリは置かず、`.csproj` 名にも `.Worker` を付けない。**
  IADR-0282 決定 1 が置いていた「Worker は `Services/<Name>/Worker/` に残す」例外は ADR-0065 決定 6 が
  改めた（#1061 で ConversionService / IngestionService の 2 件を移送し、`.Worker` 接尾辞は 0 件になった）。
  **HTTP 面を持つことは `Worker` であることと矛盾しない。**
- **参照方向（Domain は Features / Infrastructure / Common.Behaviors を知らない）はフォルダ＝
  名前空間で守る**。機械検査（`scripts/check-unit-dependencies.js` 規則 3-③）は**名前空間走査版が
  稼働している**（IADR-0282 決定 2）。旧判定（8 要素プロジェクト参照の層方向）は対象 0 件になったが、
  経過措置としてコード上に残っている。
- **`Result` / `Error`・DDD 基底型はユニット単位の `Platform.Shared.Kernel`**（IADR-0229 不変。
  サービス個別の `Common/Result.cs` は置かない）。サービス間契約はユニットの Shared
  （`<Unit>.Contracts`）のまま。サービス個別の `Contracts` / `SharedKernel` プロジェクトは置かない。
- **操作単位のスライス分割（`Features/<集約>/<操作>/` の 3 分割）はまだ行っていない。**
  移送波は器の移送までで、端点は集約フォルダ直下に 1 枚のまま置かれている（IADR-0282 決定 4）。
  太いエンドポイントのハンドラ化・値オブジェクト化・ドメインイベント導入も別作業である。

## 依存規則

1. **`Foundation/` は `Composable/` に依存しない**。可変実装へのアクセスは必ず
   `Foundation/Ports/` の抽象を介し、実装の選択・束ねは `Program.cs`（合成ルート）で行う。
   （`Foundation/` 配下に `using *.Composable.*` が現れたら規約違反。）
2. **`Composable/Steps/` の段どうしは直接参照しない**。段間の連携はイベント経由のみとする。
   イベント契約はそのユニットの契約プロジェクト `<unit>/backend/Shared/<Unit>.Contracts/Events/`
   に置く（knowledge ユニットは `knowledge/backend/Shared/Knowledge.Contracts/Events/`。
   platform 横断の共通契約は `platform/backend/Shared/Platform.Shared.Contracts/`。IADR-0059）。
3. **ユニット外への参照は `src/platform/backend/Shared/` の 3 プロジェクトのみ許可**する
   （`Platform.Shared.Contracts` / `Platform.Shared.Infrastructure` / `Platform.Shared.Kernel`。
   IADR-0056 決定 3 の「2 プロジェクト」を
   [IADR-0117](../.ai-context/adr/IADR-0117_platform-shared-kernel-placement.md) が 3 へ部分改定した。
   `Platform.Shared.Kernel` は ADR-0030 の共有カーネルで、**Result / Error を公開する実体を持つ**
   （#455。公開する操作面と `default` の扱いは
   [IADR-0229](../.ai-context/adr/IADR-0229_shared-kernel-result-surface.md) が正本。
   外部ライブラリは内部実装としてのみ使い公開面へ出さない —— ADR-0041 決定 2）。
   platform → 可変機能ユニットの参照は禁止（一方向依存）。サービス間のコード参照
   （ProjectReference・型共有）も従来どおり禁止し、連携は同期 API（契約管理）または
   イベントに限る。この規則がユニットのサブモジュール切り出し可能性を担保する。
   - 例外1: 統合テスト（`Tests/`）は検証対象サービスへの ProjectReference を許可する
     （例: IntegrationTests → AuthorizationService.Api）。
   - 例外2: フロントエンドの可変ユニットは `@foundation`（platform/frontend の基盤）と
     `@platform/ui`（共有 UI パッケージ）を参照してよい（[IADR-0121](../.ai-context/adr/IADR-0121_spa-stack-migration-staging.md)
     決定 4 が本例外の許可先を 1 → 2 へ部分改定した。`@platform/ui` はドメイン・通信・ルーティング・認証を
     持たないため、ユニットの切り出し可能性を損なわない。逆向き（`@platform/ui` → ユニット）の参照は禁止）。
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
- **フロントエンド**: `src/` を pnpm workspace ルート
  （[IADR-0121](../.ai-context/adr/IADR-0121_spa-stack-migration-staging.md) 決定 2）とし、
  単一 lock（`pnpm-lock.yaml`）で管理する。**メンバの正本は [`pnpm-workspace.yaml`](pnpm-workspace.yaml) 自身**
  であり、ここへ列挙を複写しない。ユニットと共有パッケージのほかに**可変機能ユニットの雛形**
  （`../templates/*/frontend`。`src/` の外にあるため `../` を跨ぐ唯一のメンバ）を含む
  —— メンバにしないと `pnpm -r run typecheck` の射程から外れ、ずれても誰も気付かない（#784 が踏んだ）。開発コマンドは `src/` で実行する（詳細は
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
4. フロントエンド: pnpm workspace のパターンが `'*/frontend'` のため自動認識される。platform の合成点
   （`platform/frontend/src/features/index.ts`）へ **import 1 行 ＋ 2 か所へのスプレッド 1 行ずつ**を追加する
   （[IADR-0124](../.ai-context/adr/IADR-0124_tanstack-router-unit-composition.md) 決定 1。
   [IADR-0056](../.ai-context/adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 4 の
   「import 1 行」はこれに部分改定された）。
   ユニットが公開する契約は **`(shell: ShellRoute) => Route` のルート factory を束ねたタプル**と
   **ナビ項目（`NavItem[]`）**の 2 つである。
   - `import { createXxxRoutes, xxxNavItems } from '@xxx/features';`
   - `createUnitRoutes` へ `...createXxxRoutes(shell)` を 1 行
   - `unitNavItems` へ `...xxxNavItems` を 1 行
     — **これを忘れるとルートは載るが左ナビに項目が出ない**（ルートとナビは別経路である）。
   `createUnitRoutes` の**戻り値へ型注釈を書かない**——`readonly AnyRoute[]` を注釈すると
   ルート ID とパスの union が失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる。
   ナビ項目の `group` は 05_screens §共通シェル の 4 グループ（`user` / `personal` / `admin` / `ops`）で、
   **本リポジトリの計画に属するユニットは必ず宣言する**（型 `PlanNavItem` が `tsc` で強制する。
   総称のフォールバックが無いため、宣言漏れは「どのグループにも属さず静かに消える」ことを意味する）。
   **本リポジトリの計画に属さないユニット**（AST 等）は `group` を宣言せず、代わりに合成点
   `platform/frontend/src/features/index.ts` の `unitNavGroups` へ**ユニットの機能名**を見出しとする
   グループを 1 要素足す（例: `ai-stock-trading` → 「株式自動売買」）。
   **総称としての「その他」は使わない**（05_screens §共通シェル ［2026-08-04 確定］。
   左ナビのグループ名は利用者が機能を探す唯一の手掛かりであり、何が入っているか分からない名前を
   置くと導線が失われる。[IADR-0125](../.ai-context/adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) 決定 9）。
   旧契約（`FeatureModule { id, routes: {path, element}[], nav }`）は本リポジトリから変更できないユニット
   （`src/ai-stock-trading`。[IADR-0120](../.ai-context/adr/IADR-0120_excluded-units-from-gitmodules.md)）のための
   互換ブリッジであり、新規ユニットでは使わない。
5. パッケージバージョンは中央管理（CPM）に従い、csproj に `Version=` を書かない。ユニットは常設の
   `Directory.Build.props` を持たない（配置時に単一情報源を上書きするため。単独ビルドは how-to 参照）。
