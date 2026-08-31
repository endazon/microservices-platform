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
    Services/SampleService/                  ← 単一プロジェクト標準（IADR-0282。2026-08-28 裁定）
      SampleService.csproj                  ← platform Shared を相対参照（配置後に解決）。層は分割しない
      Program.cs                            ← 合成ルート（Minimal API + ヘルスチェック。束ねるだけ）
      Features/<集約>/<操作>/                ← Vertical Slice（Endpoint / Command|Query / Handler）
      Domain/                                ← エンティティ・値オブジェクト（**外部依存ゼロ**）
      Infrastructure/                        ← Persistence（EF Core）・Messaging 等のアダプタ
      Common/                                ← サービス固有の横断関心（Result は Platform.Shared.Kernel）
      Tests/SampleService.Tests.csproj       ← **テストは 1 プロジェクト**。フォルダは実装の鏡写し
        Features/                            ←   xUnit v3 + AwesomeAssertions + NSubstitute
        Domain/                              ←   （統合テストも対象スライスのフォルダへ。Testcontainers + Respawn + Mvc.Testing）
  frontend/
    package.json                            ← name: @<unit>/frontend（pnpm workspace で自動認識）
    tsconfig.json                           ← paths で @foundation を解決（無いと typecheck が動かない）
    src/                                     ← 計画 13_frontend-stack §ディレクトリ構成（Bulletproof React）
      app/          .gitkeep                ← providers / router / アプリシェル（通常は platform 側が持つ）
      config/       .gitkeep                ← 実行時 config（**shared 層**。app の兄弟。通常は platform 側が持つ）
      assets/       .gitkeep                ← 自己ホストのフォント・画像（外部 CDN は禁止）
      components/   .gitkeep                ← ユニット内の共通コンポーネント
      hooks/ lib/ stores/ testing/ types/ utils/   .gitkeep
      locales/      .gitkeep                ← ja / en（Lingui。カタログの実体は platform 側）
      features/                              ← Feature 単位
        index.ts                            ←   ユニットの束ね（ルート factory ＋ ナビ項目の 2 本を公開）
        sample/                              ←   **内部を api/components/hooks/routes/stores/types へ割る**
          index.ts                          ←     feature の公開面（再輸出したものだけを外から使う）
          api/useSampleList.ts              ←     サーバー状態（TanStack Query / orval 生成フック）
          components/SamplePage.tsx         ←     画面・部品（@platform/ui のプリミティブを使う）
          components/SamplePage.test.tsx    ←     テストは実装と同居させる
          hooks/useSampleFilter.ts          ←     feature 固有のクライアント状態
          routes/sampleRoute.ts             ←     ルート定義（createXxxRoute）とナビ項目
          stores/       .gitkeep            ←     Zustand ストア（第 4 段で導入。#788）
          types/index.ts                    ←     表示用の型（BFF の DTO は orval 生成物を使う）
```

> **中身が無い区分も、フォルダと `.gitkeep` だけは置いてある。** 何も無いと
> **その構成要素が意図的に不在なのか単に作り忘れなのかが一見して分からない**ためである
> （計画 `12_backend-application-stack` §規範性・粒度・置き場 がバックエンドについて同じ作法を
> 定めており、フロントにも同じ理由が当てはまる）。**使わない区分のフォルダを消さないこと** ——
> 消すと次の複製者に「その区分は不要」と伝わってしまう。
>
> `app/` と `locales/` は、ユニットでは通常空のままになる（アプリホストである
> `platform/frontend` が持つ）。枠だけ残して「ユニット側には置かない」ことを見せている。

> **［2026-08-23 更新 / #785］`src/knowledge/frontend` は本雛形と同じ構成へ揃った。** 従前ここは
> 「knowledge の各 feature はまだ内部を割っておらず 1 階層にファイルが並ぶ」と書いていたが、
> 13 feature すべてを `api/ components/ hooks/ routes/ stores/ types/` へ割り、ユニット直下の
> 区分も枠を置いた（IADR-0262）。
>
> **［2026-08-28 更新 / #785］第 2 段（`src/platform/frontend` の `foundation/` 分解）も完了した。**
> `foundation/` は計画のツリーに従って `app/`・`lib/`（api / auth）・
> `components/`（ui / notifications / ai-chat）・`testing/` へ分かれた（IADR-0262 決定 5 の第 2 段）。
> **`@foundation/<区分>` というエイリアス名は変えていない** ——
> 可変ユニット（本雛形を含む）が書く import は 1 行も変わらない。**参照先はどちらのユニットでもよい。**
>
> **［2026-08-30 更新 / ADR-0067］層の分類を原典（Bulletproof React）へ戻した。**
> `config` は `app/` の中ではなく **`src/config/`（shared 層）**、i18n の実行時部分は
> **`src/lib/i18n/`（shared 層）** である。`app/` に残るのは router・providers・アプリシェルで、
> `testing/` は**テスト専用の第 4 の層**として扱う（`shared` と `app` は参照してよいが `features` は不可。
> 本番コードからは参照されない）。**エイリアス名は変えていない**——動いたのは向き先だけである。
>
> 計画 13_frontend-stack（`status: fixed`）が **Feature 単位を上記 6 区分へ割る**と定め、
> 「計画書は絶対的な正である。実装を計画へ合わせる」（2026-07-30 裁定・2026-08-22 再確定）が
> 確定している。**1 階層へ戻さないこと**——これから作られる全ユニットが不適合を継承する。

- **アプリケーション層の標準は ADR-0030**（Vertical Slice / Minimal API / ローカルディスパッチも
  Wolverine ハンドラ / Domain は共有カーネルを除き外部依存ゼロ（ADR-0041。#500） / 採用・不採用ライブラリ）。実装側の要点は
  [`docs/tech/tech-requirements.md`](../../docs/tech/tech-requirements.md)「バックエンドアプリケーション層標準」。
  不採用ライブラリ（MediatR / AutoMapper / MassTransit / FluentAssertions / Serilog 等）の混入は
  `scripts/check-backend-libraries.js` が CI で止める。
- **サービスのテストは 1 プロジェクトにする**（計画 project-planning の
  `projects/microservices-platform/06_technical/12_backend-application-stack.md`
  §規範性・粒度・置き場。利用者裁定 2026-08-04 / planning#180）。プロジェクトを分けるとビルド時間と
  参照管理のコストが増えるためである。フォルダは **`Unit/` / `Integration/` の種別区分ではなく、
  実装のスライスを鏡写しにする**（`Tests/Features/`・`Tests/Domain/`。IADR-0282 決定 1。
  種別区分の計画側条文は改定を環流中 —— planning#490 のコメント参照）。1 プロジェクトに畳むので、
  `SampleService.Tests.csproj` は単体側（NSubstitute 等）と統合側（`Mvc.Testing` / Testcontainers /
  Respawn）の**和集合**を参照する。**テスト種別ごとに `.csproj` を割らないこと**
  —— 実サービス（`src/**` の `Services/<Name>/Tests/<Name>.Tests.csproj`）も全て 1 プロジェクトである。
- **テストは xUnit v3 で書く**（ADR-0030 の標準どおり。**［2026-08-21 更新］** 従前ここは
  「v2 で書く」だった。16 プロジェクトの一斉切替が完了したため v3 が現行である）。
  本体パッケージ ID は **`xunit.v3`** である（`xunit` は v2 系のまま更新されない別 ID）。
  `xunit.runner.visualstudio` は v2 用（2.x）と v3 用（3.x）で別系列であり、**CPM は 1 パッケージ 1 バージョン
  しか持てない**ため、v2 と v3 は共存できない。それゆえ **`xunit`（v2 本体）を参照するプロジェクトを
  作ってはならない**（非互換の runner と組み合わさる）。`scripts/check-backend-libraries.js` が
  本テンプレートを含めて**両方向**を検査し混入を止める。経緯は
  [`docs/tech/tech-requirements.md`](../../docs/tech/tech-requirements.md)「バックエンドアプリケーション層標準」を参照。
- 実サービスの標準レイアウト（単一プロジェクト＋ `Features/` `Domain/` `Infrastructure/` `Common/` `Tests/`）は
  [`src/README.md`](../../src/README.md) の「サービスユニットの標準レイアウト」に従う。
- ユニット固有のイベント契約は `backend/Shared/<Unit>.Contracts/Events/` に置く（段間連携イベント。
  契約階層化は #229 / IADR-0059）。

## 依存規則（機械検査は IADR-0057）

- ユニット外参照は `platform/backend/Shared/` の 3 プロジェクト（Contracts / Infrastructure / Kernel）のみ
  （[IADR-0117](../../.ai-context/adr/IADR-0117_platform-shared-kernel-placement.md) が IADR-0056 決定 3 を 2 → 3 へ
  部分改定。`Platform.Shared.Kernel` = ADR-0030 の共有カーネルで、
  [IADR-0229](../../.ai-context/adr/IADR-0229_shared-kernel-result-surface.md) が Result / Error を公開する実体を与えた）。
- platform → 可変ユニットの参照は禁止（一方向依存）。
- サービス内の参照方向は `Domain/` → `Features/` ・ `Infrastructure/` ・ `Common/` を禁じる一方向
  （共有基盤プロジェクトでは同じ規律を `Foundation/` → `Composable/` の禁止として表す）。
- フロントが参照してよいのは **`@foundation`（platform の基盤）と `@platform/ui`（共有 UI パッケージ）の 2 つ**
  （[IADR-0121](../../.ai-context/adr/IADR-0121_spa-stack-migration-staging.md) 決定 4 が
  [`src/README.md`](../../src/README.md) 依存規則 例外 2 を 1 → 2 へ部分改定した。`@platform/ui` は
  ドメイン・通信・ルーティング・認証・表示文言を持たないため切り出し可能性を損なわない。
  逆向き（`@platform/ui` → ユニット）は禁止）。**`@platform/ui` の深い参照
  （`@platform/ui/src/...`）は ESLint が禁止する**——公開面は `src/index.ts` の 1 ファイルだけである。
- フロントは platform の合成点（`@features`）を参照しない。合成点以外からの `@<unit>` import も
  ESLint で禁止（IADR-0057）。
- **BFF 呼び出しは orval 生成フックか `@foundation/api` 経由**（手書き HTTP クライアントは ESLint が
  error にする）。画面（features）からの `apiFetch` / `bffFetch` 直呼びも禁止（IADR-0146）。

> これらの禁止は雛形にも機械適用される（`src/eslint.templates.config.js`。禁止リストは
> `src/eslint.config.js` から import しており二重管理にならない）。

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

フォールバックの要点（詳細は各 `.sample` のヘッダコメントと [IADR-0064](../../.ai-context/adr/IADR-0064_standalone-build-props-fallback.md)）:

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
4. フロント: 雛形の `package.json` の `name` と `tsconfig.json` の `paths`（`@sample-unit` の行、および
   テンプレート位置向けの 2 つ目の候補パス）を自ユニット名へ直す。そのうえで **3 か所**を追加する
   （[IADR-0124](../../.ai-context/adr/IADR-0124_tanstack-router-unit-composition.md) 決定 1。
   [IADR-0056](../../.ai-context/adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 4 の
   「import 1 行」はこれに部分改定された）。
   - 合成点 `src/platform/frontend/src/features/index.ts` へ
     `import { createXxxRoutes, xxxNavItems } from '@<unit>/features';`
   - 同ファイルの `createUnitRoutes` へ `...createXxxRoutes(shell)` を 1 行
   - 同ファイルの `planNavItems` へ `...xxxNavItems` を 1 行
     — **ルートとナビは別経路である。**片方だけだと「画面は開けるのに左ナビに出ない」（逆も同様）。

   併せてエイリアスを **2 か所**へ足す（片方だけだと「ビルドは通るが `tsc` が落ちる」等の食い違いになる）。
   - `src/platform/frontend/vite.config.ts` の `resolve.alias`（`@knowledge` と同型）
   - `src/platform/frontend/tsconfig.app.json` の `paths`（型解決用）

   さらに **i18n の抽出対象**（`src/lingui.config.ts` の `catalogs[0].include`）へ
   `'<rootDir>/<unit>/frontend/src'` を足す。ここはハードコードの列挙で自動認識しない。
   **足し忘れると、そのユニットの文言が抽出されず未翻訳検査（IADR-0125 決定 4）の外側になる**
   ——「翻訳漏れ 0 件」に見えて実際は測っていない状態になる。詳細は
   [how-to](../../docs/how-to/adding-a-unit-submodule.md) §4。

   なお本計画に属さないユニットは `group` を宣言せず、合成点の `unitNavGroups` へ**ユニットの機能名**を
   見出しとするグループを 1 要素足す（IADR-0125 決定 9。総称としての「その他」は使わない）。
5. バージョン固定: submodule の pin を本体 PR で更新（Renovate `git-submodules` で自動化可）。
