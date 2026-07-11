# unit-template — 追加可変機能ユニットの雛形（FR-14 / IADR-0056 / IADR-0060）

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
    Services/SampleService/
      src/SampleService.Api/
        SampleService.Api.csproj            ← platform Shared を相対参照（配置後に解決）
        Program.cs                          ← 合成ルート（最小 API + ヘルスチェック）
  frontend/
    package.json                            ← name: @<scope>/frontend-<unit>（workspaces で自動認識）
    src/features/
      index.ts                              ← ユニットの feature 束ね
      sample/index.ts                       ← サンプル feature
```

- 実サービスの標準レイアウト（`Foundation/` / `Composable/` の区分）は
  [`src/README.md`](../../src/README.md) の「サービスユニットの標準レイアウト」に従う。
- ユニット固有のイベント契約は `backend/Shared/<Unit>.Contracts/Events/` に置く（段間連携イベント。
  契約階層化は #229 / IADR-0059）。

## 依存規則（機械検査は IADR-0057）

- ユニット外参照は `platform/backend/Shared/` の 2 プロジェクト（Contracts / Infrastructure）のみ。
- platform → 可変ユニットの参照は禁止（一方向依存）。
- `Foundation/` は `Composable/` に依存しない。
- フロントは `@foundation` のみ参照可。合成点以外からの `@<unit>` import は ESLint で禁止。

## 単独リポジトリでビルドする場合（任意）

本体へ submodule 配置したときは `src/Directory.Build.props` / `src/Directory.Packages.props`（単一情報源）が
ディレクトリ階層で継承されるため、**ユニットに常設の `Directory.Build.props` を置いてはならない**
（置くと配置時に単一情報源より近い階層で発見され上書きしてしまう）。

ユニットを**単独**でビルドする必要がある場合のみ、リポジトリルートに次のフォールバックを置く。親（本体の
`src/Directory.Build.props`）が存在すればそれを継承し、無ければ単独用の設定を効かせる（配置時に上書きしない）。

```xml
<!-- Directory.Build.props（単独ビルド用フォールバック。submodule 配置時は親が優先されるよう import-chain する） -->
<Project>
  <!-- 上位に本体の Directory.Build.props があれば継承（submodule 配置時）。 -->
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))"
          Condition="'$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))' != ''" />
  <!-- 単独時のみ効かせる既定（本体継承時は親が既に定義済みのため上書きしない）。 -->
  <PropertyGroup Condition="'$(TargetFramework)' == ''">
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>13</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

（同様に `Directory.Packages.props` も単独時のみ必要。中央管理のバージョンは本体の値と揃える。）

## 組み込みチェックリスト

1. 本テンプレートを複製し新ユニットのリポジトリを作成。
2. `git submodule add <repo-url> src/<unit>`。
3. バックエンド: CI は `src/*/backend/backend.slnx` を自動発見（編集不要）。private submodule は
   checkout に `submodules: recursive` + トークンを与える（[how-to](../../docs/how-to/adding-a-unit-submodule.md) §3）。
4. フロント: 合成点 `src/platform/frontend/src/features/index.ts` へ import を 1 行追加。`@<unit>` エイリアスを
   `platform/frontend/vite.config.ts` に追加。
5. バージョン固定: submodule の pin を本体 PR で更新（Renovate `git-submodules` で自動化可）。
