# unit-template — 追加可変機能ユニットの雛形（FR-14 / IADR-0056 / IADR-0060 / IADR-0064）

本ディレクトリは、本体リポジトリ（microservices-platform）へ **git submodule** として組み込む
**追加可変機能ユニット**の最小雛形である。新ユニットのリポジトリを作成する際の出発点として複製する。

> このディレクトリは本体リポジトリの**ビルド対象ではない**（`src/` 外に置かれ、どの `backend.slnx` にも
> 含まれない）。相対 `ProjectReference` は submodule 配置後の位置（`src/<unit>/`）を前提に記述してあり、
> このテンプレート位置のままではビルドしない。組み込み手順は
> [`docs/how-to/adding-a-unit-submodule.md`](../../docs/how-to/adding-a-unit-submodule.md) を参照。

## 構成

```
<unit>/                                     ← 新ユニットのリポジトリルート（= 配置時の src/<unit>/）
  backend/
    backend.slnx                            ← ユニットの集約ソリューション
    Directory.Build.props.sample            ← 単独ビルド用フォールバック（配置時は使わない。IADR-0064）
    Directory.Packages.props.sample         ← 単独ビルド用 CPM フォールバック（同上）
    Services/SampleService/                  ← ADR-0030 の標準プロジェクト構成
      src/SampleService.Api/                 ← エンドポイント・DI 構成・ProblemDetails 変換
        SampleService.Api.csproj            ← platform Shared を相対参照（配置後に解決）
        Program.cs                          ← 合成ルート（Minimal API + ヘルスチェック）
      src/SampleService.Application/         ← ユースケース（Wolverine ハンドラ）・検証・マッピング
      src/SampleService.Domain/              ← エンティティ・値オブジェクト（**外部依存ゼロ**）
      src/SampleService.Infrastructure/      ← EF Core・Redis 等の実装
      src/SampleService.Contracts/           ← 公開契約（proto・イベント・DTO）
      tests/SampleService.UnitTests/         ← xUnit v2 + AwesomeAssertions + NSubstitute
      tests/SampleService.IntegrationTests/  ← Testcontainers + Respawn + Mvc.Testing
  frontend/
    package.json                            ← name: @<scope>/frontend-<unit>（workspaces で自動認識）
    src/features/
      index.ts                              ← ユニットの feature 束ね
      sample/index.ts                       ← サンプル feature
```

- **アプリケーション層の標準は ADR-0030**（Vertical Slice / Minimal API / ローカルディスパッチも
  Wolverine ハンドラ / Domain は外部依存ゼロ / 採用・不採用ライブラリ）。実装側の要点は
  [`docs/tech/tech-requirements.md`](../../docs/tech/tech-requirements.md)「バックエンドアプリケーション層標準」。
  不採用ライブラリ（MediatR / AutoMapper / MassTransit / FluentAssertions / Serilog 等）の混入は
  `scripts/check-backend-libraries.js` が CI で止める。
- **テストは xUnit v2 で書く**（ADR-0030 の標準は **v3** だが、本リポジトリの現行は v2 である）。
  `xunit.runner.visualstudio` は v2 用（2.x）と v3 用（3.x）で別系列であり、**CPM は 1 パッケージ 1 バージョン
  しか持てない**ため、v3 へ移るには既存の全テストプロジェクトが同時に移る必要がある。この切替は
  **独立した issue** で行う。それまで **`xunit.v3` を参照するプロジェクトを作ってはならない**
  （非互換の runner と組み合わさる）。`scripts/check-backend-libraries.js` が本テンプレートを含めて検査し
  混入を止める。経緯と切替方針は
  [`docs/tech/tech-requirements.md`](../../docs/tech/tech-requirements.md)「バックエンドアプリケーション層標準」を参照。
- 実サービスの標準レイアウト（`Foundation/` / `Composable/` の区分）は
  [`src/README.md`](../../src/README.md) の「サービスユニットの標準レイアウト」に従う。
- ユニット固有のイベント契約は `backend/Shared/<Unit>.Contracts/Events/` に置く（段間連携イベント。
  契約階層化は #229 / IADR-0059）。

## 依存規則（機械検査は IADR-0057）

- ユニット外参照は `platform/backend/Shared/` の 3 プロジェクト（Contracts / Infrastructure / Kernel）のみ
  （[IADR-0117](../../docs/adr/IADR-0117_platform-shared-kernel-placement.md) が IADR-0056 決定 3 を 2 → 3 へ
  部分改定。`Platform.Shared.Kernel` = ADR-0030 の共有カーネル・実体は未作成）。
- platform → 可変ユニットの参照は禁止（一方向依存）。
- `Foundation/` は `Composable/` に依存しない。
- フロントは `@foundation` のみ参照可。合成点以外からの `@<unit>` import は ESLint で禁止。

## 単独リポジトリでビルドする場合（任意）

本体へ submodule 配置したときは `src/Directory.Build.props` / `src/Directory.Packages.props`（単一情報源）が
ディレクトリ階層で継承されるため、**ユニットに常設の `Directory.Build.props` を置いてはならない**
（置くと配置時に単一情報源より近い階層で発見され上書きしてしまう）。

ユニットを**単独**でビルドする必要がある場合のみ、同梱の実ファイル
[`backend/Directory.Build.props.sample`](backend/Directory.Build.props.sample) と
[`backend/Directory.Packages.props.sample`](backend/Directory.Packages.props.sample) を、**拡張子 `.sample` を外して**
バックエンドのリポジトリルート（配置時の `src/<unit>/backend/` 相当）に置く。親（本体の
`src/Directory.Build.props` / `src/Directory.Packages.props`）が存在すればそれを継承し、無ければ単独用の設定を
効かせる（配置時に上書きしない）。**スニペットをコピペするのではなく実ファイルを複製する**（コピペ時の引用符
取りこぼしで MSB4092 を招かないため。IADR-0064）。

```bash
# 単独ビルドする場合のみ（submodule 配置時は置かない）
cd backend
cp Directory.Build.props.sample    Directory.Build.props
cp Directory.Packages.props.sample Directory.Packages.props
dotnet build backend.slnx
```

フォールバックの要点（詳細は各 `.sample` のヘッダコメントと [IADR-0064](../../docs/adr/IADR-0064_standalone-build-props-fallback.md)）:

```xml
<!-- Directory.Build.props.sample（抜粋）。パスをプロパティへ束ね、Condition は単純参照にして MSB4092 を避ける。 -->
<Project>
  <PropertyGroup>
    <ParentDirectoryBuildProps>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</ParentDirectoryBuildProps>
  </PropertyGroup>
  <Import Project="$(ParentDirectoryBuildProps)" Condition="'$(ParentDirectoryBuildProps)' != ''" />
  <PropertyGroup Condition="'$(TargetFramework)' == ''">
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>13</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

> **なぜプロパティへ束ねるか**: `Condition` 属性に `GetPathOfFileAbove('Directory.Build.props', '...')` を直接
> 書くと、条件の外側クォートと関数引数の内側クォートが衝突し MSBuild が **MSB4092** で失敗する。パスを一旦
> プロパティに入れ、`Condition="'$(ParentDirectoryBuildProps)' != ''"` の単純参照にすると衝突しない（IADR-0064）。

（`Directory.Packages.props.sample` も同じ import-chain。中央管理のバージョンは本体の値と揃える。）

## 組み込みチェックリスト

1. 本テンプレートを複製し新ユニットのリポジトリを作成。
2. `git submodule add <repo-url> src/<unit>`。
3. バックエンド: CI は `src/*/backend/backend.slnx` を自動発見（編集不要）。private submodule は
   checkout に `submodules: recursive` + トークンを与える（[how-to](../../docs/how-to/adding-a-unit-submodule.md) §3）。
4. フロント: 合成点 `src/platform/frontend/src/features/index.ts` へ import を 1 行追加。`@<unit>` エイリアスを
   `platform/frontend/vite.config.ts` に追加。
5. バージョン固定: submodule の pin を本体 PR で更新（Renovate `git-submodules` で自動化可）。
