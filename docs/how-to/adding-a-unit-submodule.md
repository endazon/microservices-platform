# 追加可変機能ユニットを submodule として組み込む手順（FR-14 / IADR-0056 / IADR-0060 / IADR-0064）

本リポジトリは **platform（基盤）** を主成果物とし、**可変機能ユニット**（knowledge 等）を
`src/<unit>/`（`backend/` + `frontend/`）として持つ（[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。
追加の可変機能ユニットは**別リポジトリの git submodule** として `src/<unit>/` にリンクする。本書はその
組み込み手順の運用ガイドである。規約の要点は [`src/README.md`](../../src/README.md) を参照。

> 状態: 本手順の CI 自動発見・テンプレート・単独ビルド規約は整備済み。**サンプルユニットでの end-to-end
> 通し検証（別リポジトリ作成が必要）は未実施**（[IADR-0060](../adr/IADR-0060_submodule-unit-operations.md) フォローアップ、Issue #230）。

## 1. ユニットリポジトリをテンプレートから作成する

`templates/unit-template/` を雛形として新ユニットのリポジトリを作成する（内容は当該ディレクトリの
[`README.md`](../../templates/unit-template/README.md) を参照）。最小構成は次のとおり。

```
<unit>/                         ← 新ユニットのリポジトリルート（= submodule 配置時の src/<unit>/）
  backend/
    backend.slnx                ← ユニットの集約ソリューション（サービスを登録）
    Services/<Name>/
      src/<Name>.Api/           ← Program.cs（合成ルート）・Foundation/・Composable/
      tests/<Name>.Api.Tests/
    Shared/<Unit>.Contracts/    ← 任意: ユニット固有のイベント契約（段間連携イベント。契約階層化は #229/IADR-0059）
  frontend/
    package.json                ← name: @<scope>/frontend-<unit>、workspaces で自動認識
    src/features/               ← 画面 feature 群と合成用 index.ts
```

- **命名**: 名前空間はフォルダ階層に一致させる（IADR-0027）。ユニット固有イベント契約は
  `Shared/<Unit>.Contracts/Events/` に置き、wire URN は移設時に `[MessageUrn]` で固定する（IADR-0059）。
- **依存規則**（[`src/README.md`](../../src/README.md) §依存規則、機械検査は IADR-0057）:
  - ユニット外参照は `platform/backend/Shared/` の 2 プロジェクト（Contracts / Infrastructure）のみ。
  - platform → 可変ユニットの参照は禁止（一方向依存）。
  - `Foundation/` は `Composable/` に依存しない。

## 2. submodule として配置する

```bash
git submodule add <repo-url> src/<unit>
git commit -m "chore(FR-14): add <unit> unit as submodule"
```

- submodule は **gitlink（特定コミット）で固定**される。更新はユニット側でタグ/コミットを進めた後、
  本体リポの PR で pin を更新する（下記 6）。

## 3. バックエンドを組み込む

- サービス csproj から platform の共通契約・基盤を相対パスで参照する（サービス csproj から 6 階層上）:

  ```xml
  <ProjectReference Include="..\..\..\..\..\..\platform\backend\Shared\KnowledgePlatform.Shared.Contracts\KnowledgePlatform.Shared.Contracts.csproj" />
  <ProjectReference Include="..\..\..\..\..\..\platform\backend\Shared\KnowledgePlatform.Shared.Infrastructure\KnowledgePlatform.Shared.Infrastructure.csproj" />
  ```

- **サービス CI 発見は編集不要**（IADR-0060）。`ci.yml` の `lint` / `build-and-test` は
  `src/*/backend/backend.slnx` を**自動発見**して検査・ビルド・テストする。チェックアウト済みのユニットは
  自動的に対象になる。
  - ただし submodule は既定の `actions/checkout` では取得されない。**追加ユニットを CI で取得する**には
    ビルド系ジョブ（`lint` / `build-and-test`）に、checkout 直後の取得ステップを足す。
    - **注意（planning を巻き込まない）**: 本体リポと各ユニットは private な `planning`
      （`endazon/project-planning`）を submodule として持つため、checkout の `submodules: recursive`/`true` は
      使わない（planning まで取得しようとして `Repository not found` で失敗する。IADR-0058 / IADR-0065）。
      代わりに **`src/*` のユニット submodule のみを非再帰で init** する。
    - **public ユニット（トークン不要）**: 既定 `GITHUB_TOKEN` で read できる。

      ```yaml
      - uses: actions/checkout@v7
      - name: Fetch unit submodules (src/*, public, non-recursive)
        run: |
          git config --file .gitmodules --get-regexp '^submodule\..*\.path$' \
            | awk '$2 ~ /^src\// { print $2 }' \
            | xargs -r -n1 git submodule update --init
      ```

    - **private ユニット**: 上記の init に read 権限を持つ PAT を与える（IADR-0058 の `doc-links-planning.yml`
      と同型。`git -c http.https://github.com/.extraheader=...` またはトークン付き clone で取得する）。

    未取得の間はユニットのディレクトリが空となり、自動発見の glob に現れず**ビルド対象外**になる
    （＝取りこぼしに注意。取得の有効化が組み込みの前提）。実例: `ai-stock-trading`（public）は
    上記 `src/*` 非再帰 init で `lint` / `build-and-test` に取り込まれる（Issue #245 / IADR-0065）。

## 4. フロントエンドを組み込む（合成点 1 行）

- npm workspaces は `src/` ルートの `"*/frontend"` により**自動認識**される（`package.json` 追記不要）。
- platform の合成点 [`src/platform/frontend/src/features/index.ts`](../../src/platform/frontend/src/features/index.ts) へ
  **import を 1 行追加**して feature を束ねる:

  ```ts
  import { features as <unit>Features } from '@<unit>/features';
  export const features: FeatureModule[] = [...knowledgeFeatures, ...<unit>Features];
  ```

  - `@<unit>` エイリアスは `platform/frontend/vite.config.ts` に追加する（`@knowledge` と同型）。
  - 依存規則: 可変ユニットは `@foundation` のみ参照可。合成点以外からの `@<unit>` import は
    ESLint（`no-restricted-imports`、IADR-0057）で禁止される。

## 5. 単独ビルド規約（submodule 配置時に共通設定を上書きしない）

- 共通 MSBuild 設定（`src/Directory.Build.props` / `Directory.Packages.props`）は `src/` 直下の**単一情報源**で、
  ディレクトリ階層により全ユニットへ継承される。**ユニットは常設の `Directory.Build.props` を持たない**
  （持つと submodule 配置時に `src/` の単一情報源より近い階層で発見され上書きするため。MSBuild は最も近い 1 つで停止）。
- ユニットを**単独リポジトリでビルド**する必要がある場合のみ、テンプレート同梱の実ファイル
  [`Directory.Build.props.sample`](../../templates/unit-template/backend/Directory.Build.props.sample) /
  [`Directory.Packages.props.sample`](../../templates/unit-template/backend/Directory.Packages.props.sample) を
  拡張子 `.sample` を外して `backend/` 直下に置く（親を import-chain するフォールバック。submodule 配置時は置かない）。
  スニペットのコピペではなく実ファイル複製を使う（コピペ時の引用符取りこぼしで **MSB4092** を招かないため。
  Condition に `GetPathOfFileAbove` を直接書かず、パスをプロパティへ束ねて単純参照にするのが要点。詳細は
  [`templates/unit-template/README.md`](../../templates/unit-template/README.md) と
  [IADR-0064](../adr/IADR-0064_standalone-build-props-fallback.md)）。

## 6. バージョン固定・更新

- submodule は gitlink で固定。更新はユニット側で進めた後、本体リポの PR で pin を更新する。
- **Renovate/Dependabot**: `git-submodules` マネージャで submodule の更新 PR を自動化できる（有効化はメンテナ判断）。
  例（Renovate `renovate.json`）:

  ```json
  { "git-submodules": { "enabled": true } }
  ```

## 7. 通し検証（サンプルユニット）

- テンプレートから最小ユニット（1 feature + 1 サービス）を作成 → submodule 追加 → ビルド・テスト・
  `docker compose` 起動までを end-to-end で確認する。
- **本手順は別リポジトリ（サンプルユニット）作成を要するため本リポジトリ内では未完**。Issue #230 に残す。

## 参照

- [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) ユニット第一構成
- [IADR-0057](../adr/IADR-0057_unit-dependency-machine-check.md) 依存方向の機械検査
- IADR-0059 契約階層化（ユニット固有イベント契約） — #229 で導入予定
- [IADR-0060](../adr/IADR-0060_submodule-unit-operations.md) submodule 運用（本書の決定）
- [IADR-0064](../adr/IADR-0064_standalone-build-props-fallback.md) 単独ビルド用フォールバック props の MSB4092 回避・実ファイル同梱
- [`src/README.md`](../../src/README.md) ユニット規約・依存規則
