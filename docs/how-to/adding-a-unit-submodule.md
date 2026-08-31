<!-- trace:
ids: [FR-14]
adrs: [ADR-0030, ADR-0032, ADR-0048]
iadrs: [IADR-0027, IADR-0056, IADR-0057, IADR-0058, IADR-0059, IADR-0060, IADR-0064, IADR-0065, IADR-0117, IADR-0120, IADR-0121, IADR-0124, IADR-0125, IADR-0228, IADR-0262, IADR-0327]
specs: [20260712_issue-260_dependabot-gitsubmodule, 20260831_issue-1092_planning-submodule-residual-refs]
issues: [#229, #230, #245, #785, #1092]
-->

# 追加可変機能ユニットを submodule として組み込む手順

本リポジトリは **platform（基盤）** を主成果物とし、**可変機能ユニット**（knowledge 等）を
`src/<unit>/`（`backend/` + `frontend/`）として持つ（ユニット第一のリポジトリ構成による）。
追加の可変機能ユニットは**別リポジトリの git submodule** として `src/<unit>/` にリンクする。本書はその
組み込み手順の運用ガイドである。規約の要点は [`src/README.md`](../../src/README.md) を参照。

> 状態: 本手順の CI 自動発見・テンプレート・単独ビルド規約は整備済み。**サンプルユニットでの end-to-end
> 通し検証（別リポジトリ作成が必要）は未実施**（submodule 運用の実装 ADR のフォローアップ、Issue #230）。

## 1. ユニットリポジトリをテンプレートから作成する

`templates/unit-template/` を雛形として新ユニットのリポジトリを作成する（内容は当該ディレクトリの
[`README.md`](../../templates/unit-template/README.md) を参照）。最小構成は次のとおり。

```
<unit>/                         ← 新ユニットのリポジトリルート（= submodule 配置時の src/<unit>/）
  backend/
    backend.slnx                ← ユニットの集約ソリューション（サービスを登録）
    Services/<Name>/
      <Name>.csproj             ← 単一プロジェクト（層をプロジェクト分割しない）
      Program.cs                ← 合成ルート
      Features/<集約>/<操作>/   ← 端点・段（Endpoint / Command|Query / Handler / *Consumer）
      Domain/                   ← エンティティ・値オブジェクト・ポート（Domain/Ports/）
      Infrastructure/           ← Persistence/ ・ Messaging/ ・ ExternalServices/（アダプタ）
      Common/                   ← サービス固有の横断関心（Exceptions/・Behaviors/）
      Tests/<Name>.Tests.csproj ← テストは 1 プロジェクト
    Shared/<Unit>.Contracts/    ← 任意: ユニット固有のイベント契約（段間連携イベント。契約階層化は #229/IADR-0059）
  frontend/
    package.json                ← name: @<unit>/frontend、pnpm workspace で自動認識。**依存を明示宣言する**
    tsconfig.json               ← paths で @foundation を解決（無いと typecheck が動かない）
    src/features/               ← 画面 feature 群と合成用 index.ts。Feature 単位を
                                ←   api/ components/ hooks/ routes/ stores/ types/ へ割る
                                ←   （Bulletproof React。6 区分は閉じた集合で utils/ を足さない）
```

- **フロントの依存は雛形の `package.json` に宣言済みのものを引き継ぐ。** pnpm は npm workspaces と違い
  ユニットごとの宣言を厳密に守るため、宣言しない依存は解決できない（`src/package.json` の
  `//overrides` 注釈）。雛形は React 19 / TanStack Router / TanStack Query / Lingui / `@platform/ui` を
  宣言している。`oidc-client-ts` は計画 13_frontend-stack で**不採用**（BFF セッション方式へ
  移行するため）なので、新ユニットへは入れない。

- **命名**: 名前空間はフォルダ階層に一致させる（固定/可変分離のフォルダ・名前空間規約）。ユニット固有イベント契約は
  `Shared/<Unit>.Contracts/Events/` に置き、wire URN は移設時に `[MessageUrn]` で固定する（契約の階層化による）。
- **依存規則**（[`src/README.md`](../../src/README.md) §依存規則。機械検査は軽量スクリプト＋フロント ESLint で行う）:
  - ユニット外参照は `platform/backend/Shared/` の 3 プロジェクト（Contracts / Infrastructure /
    Kernel）のみ（共有カーネルの配置を定めた実装 ADR が、ユニット構成の決定 3 を
    2 → 3 へ部分改定した。`Platform.Shared.Kernel` は計画側が定める共有カーネルであり、
    2026-08-21 に Result / Error を公開する実体を持った）。
  - platform → 可変ユニットの参照は禁止（一方向依存）。
  - サービス内の参照方向は一方向にする（`Domain/` は `Features/` ・ `Infrastructure/` ・ `Common/` を
    知らない）。共有基盤プロジェクトでは同じ規律を `Foundation/` → `Composable/` の禁止として表す。

## 2. submodule として配置する

```bash
git submodule add <repo-url> src/<unit>
git commit -m "chore(FR-14): add <unit> unit as submodule"
```

- submodule は **gitlink（特定コミット）で固定**される。更新はユニット側でタグ/コミットを進めた後、
  本体リポの PR で pin を更新する（下記 6）。

## 3. バックエンドを組み込む

- サービス csproj から platform の共通契約・基盤を相対パスで参照する。**配置後のサービス csproj は
  `src/<unit>/backend/Services/<Name>/<Name>.csproj` にあるので、`src/` までは 4 階層上**である
  （層プロジェクトを廃し `src/` 中間層を置かなくなったため、従前の 6 階層から縮んだ）:

  ```xml
  <ProjectReference Include="..\..\..\..\platform\backend\Shared\Platform.Shared.Contracts\Platform.Shared.Contracts.csproj" />
  <ProjectReference Include="..\..\..\..\platform\backend\Shared\Platform.Shared.Infrastructure\Platform.Shared.Infrastructure.csproj" />
  ```

- **サービス CI 発見は編集不要**（submodule 運用の実装 ADR）。`ci.yml` の `lint` / `build-and-test` は
  `src/*/backend/backend.slnx` を**自動発見**して検査・ビルド・テストする。チェックアウト済みのユニットは
  自動的に対象になる。
  - ただし submodule は既定の `actions/checkout` では取得されない。**追加ユニットを CI で取得する**には
    ビルド系ジョブ（`lint` / `build-and-test`）に、checkout 直後の取得ステップを足す。
    - **注意（checkout の `submodules:` オプションは使わない）**: `submodules: recursive`/`true` には
      **取得対象を選ぶ手段が無い**。代わりに **`src/*` のユニット submodule のみを非再帰で init** する。
      理由は 2 つあり、いずれも現在の構成でそのまま効く（submodule 取得の実装判断による）。
      - **`src/*` 限定**: ユニットの実体が要るのはビルド・テスト・機械検査のジョブだけである。
        将来 `src/` の外へ submodule を足したとき、それらのジョブが不要な取得と権限要求を抱え込まない。
      - **非再帰**: ユニットが内包する入れ子 submodule を辿らない。入れ子が private だと既定
        `GITHUB_TOKEN` では read できず、**checkout ステップごと `Repository not found` で落ちる**
        （ジョブ本体に入る前に失敗するため、原因が読み取りにくい）。
    - **public ユニット（トークン不要）**: 既定 `GITHUB_TOKEN` で read できる。

      ```yaml
      - uses: actions/checkout@v7
      - name: Fetch unit submodules (src/*, public, non-recursive)
        run: |
          git config --file .gitmodules --get-regexp '^submodule\..*\.path$' \
            | awk '$2 ~ /^src\// { print $2 }' \
            | xargs -r -n1 git submodule update --init
      ```

    - **private ユニット**: 上記の init に read 権限を持つ PAT を与える
      （`git -c http.https://github.com/.extraheader=...` またはトークン付き clone で取得する）。
      **現時点で private なユニットは無く、この経路を使うワークフローも無い**（PAT を使う定期ジョブは撤去済み）。

    未取得の間はユニットのディレクトリが空となり、自動発見の glob に現れず**ビルド対象外**になる
    （＝取りこぼしに注意。取得の有効化が組み込みの前提）。実例: `ai-stock-trading`（public）は
    上記 `src/*` 非再帰 init で `lint` / `build-and-test` に取り込まれる（Issue #245。public ユニットはトークン不要）。

## 4. フロントエンドを組み込む（合成点 1 行）

- pnpm workspace は `src/pnpm-workspace.yaml` の `'*/frontend'` により**自動認識**される
  （同ファイルの追記不要。#591: 従前ここは「npm workspaces（`package.json` 追記不要）」と書いていたが、
  パッケージ管理は SPA 新スタック移行の決定 2 で pnpm へ移行済み）。
  **メンバの現行値は同ファイル自身が正本**である（`src/` の外にある雛形 `../templates/*/frontend` も
  メンバに含む。#802 / #777。理由は同 決定 2 の 2026-08-16 追記）。
- ユニットが公開する契約は **`(shell: ShellRoute) => Route` のルート factory を束ねたタプル**と
  **ナビ項目（`PlanNavItem[]`）**の 2 つである（TanStack Router とユニット合成の実装 ADR
  決定 1）。platform の合成点
  [`src/platform/frontend/src/features/index.ts`](../../src/platform/frontend/src/features/index.ts) へ
  **import 1 行 ＋ スプレッド 2 行**を追加して束ねる:

  ```ts
  import { createXxxRoutes, xxxNavItems } from '@<unit>/features';

  export const createUnitRoutes = (shell: ShellRoute) =>
    [...createKnowledgeRoutes(shell), ...createXxxRoutes(shell)] as const;

  export const planNavItems: readonly PlanNavItem[] = [...knowledgeNavItems, ...xxxNavItems];
  ```

  - **`createUnitRoutes` の戻り値へ型注釈を書かない。** `readonly AnyRoute[]` を注釈すると
    ルート ID とパスの union が失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる
    （同実装 ADR §実測）。
  - **ルートとナビは別経路である。** `planNavItems` への追加を忘れると、画面は開けるのに左ナビに出ない。
  - ナビ項目の `group` は 05_screens §共通シェル の 4 グループ（`user` / `personal` / `admin` / `ops`）で、
    **本リポジトリの計画に属するユニットは必ず宣言する**（型 `PlanNavItem` が `tsc` で強制する。
    総称のフォールバックが無いため、宣言漏れは「どのグループにも属さず静かに消える」ことを意味する）。
    計画に属さないユニットは `group` を宣言せず、合成点の `unitNavGroups` へ**ユニットの機能名**を
    見出しとするグループを 1 要素足す（共有 UI プリミティブの実装 ADR 決定 9）。
  - **旧契約（`FeatureModule { id, routes: {path, element}[], nav }`）は使わない。** 本リポジトリから
    変更できないユニット（`src/ai-stock-trading`。検査対象外ユニットの単一情報源は `.gitmodules` である）
    のための互換ブリッジであり、`src/README.md` §項 4 が「新規ユニットでは使わない」と定めている。
  - `@<unit>` エイリアスは **2 か所**へ追加する（`@knowledge` と同型）。片方だけだと
    「ビルドは通るが `tsc` が落ちる」等の食い違いになる。
    - `src/platform/frontend/vite.config.ts` の `resolve.alias`
    - `src/platform/frontend/tsconfig.app.json` の `paths`
  - 依存規則: 可変ユニットが参照してよいのは `@foundation` と `@platform/ui` の 2 つ
    （SPA 新スタック移行の決定 4 が `src/README.md` 例外 2 を
    1 → 2 へ部分改定）。合成点以外からの `@<unit>` import は
    ESLint（`no-restricted-imports`。依存方向の機械検査）で禁止される。

- **i18n の抽出対象へ足す**（[`src/lingui.config.ts`](../../src/lingui.config.ts) の `catalogs[0].include`）。
  ここは**ハードコードの列挙**（`platform/frontend/src` と `knowledge/frontend/src`）であり、pnpm workspace の
  `'*/frontend'` のような自動認識をしない。**足し忘れると、そのユニットの `msg` / `Trans` が
  `pnpm run i18n` の抽出対象に入らず、未翻訳キーの検査（`check-i18n-catalogs.js`。共有 UI プリミティブの実装 ADR 決定 4）の
  外側になる** —— カタログに現れないので「翻訳漏れが 0 件」と見えるが、実際は測っていない。

  ```ts
  include: [
    '<rootDir>/platform/frontend/src',
    '<rootDir>/knowledge/frontend/src',
    '<rootDir>/<unit>/frontend/src', // ← 追加
  ],
  ```

  **グロブ（`*/frontend/src`）へ置き換えないこと。** 抽出対象は「**本リポジトリが所有するユニット**」に
  限る決まりで、別プロジェクトの submodule（`ai-stock-trading`。検査対象外ユニット）は含めない
  （同ファイル冒頭のコメントが理由を述べている）。グロブにすると AST を巻き込む。

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
  単独ビルド用フォールバック props の実装 ADR による）。

## 6. バージョン固定・更新

- submodule は gitlink で固定。更新はユニット側で進めた後、本体リポの PR で pin を更新する。
- `.gitmodules` の `branch = <name>`（`git submodule add -b` 由来）は **`git submodule update --remote` の
  追跡先**を示すだけで、通常のビルド/CI 取得（`git submodule update --init`・`--remote` 無し）や既定
  checkout では参照されず、**常に gitlink で pin されたコミット**が取得される。pin の前進は上記の PR で
  明示的に行う（`--remote` を使う場合のみ `branch` が効く）。
- **Dependabot**: `gitsubmodule` エコシステムで submodule の pin 更新 PR を自動化する。本リポジトリは
  [`.github/dependabot.yml`](../../.github/dependabot.yml) で有効化済み（Issue #260）。
  `directory: "/"` は root の `.gitmodules` に列挙された **全 submodule**を対象にする（現在は
  `src/ai-stock-trading` の 1 件だけである）。ユニットを追加しても `dependabot.yml` の追記は不要
  （`.gitmodules` への submodule 追加だけで自動的に対象になる）。既定は週次スケジュール・**自動マージなし**
  （pin 更新は必ず PR 経由・人手レビュー必須）。**private な submodule を足す場合**は、Dependabot が
  当該リポを read できる権限が要る（詳細は作業仕様書を参照。導線は本書の trace ブロックにある）。

## 7. 通し検証（サンプルユニット）

- テンプレートから最小ユニット（1 feature + 1 サービス）を作成 → submodule 追加 → ビルド・テスト・
  `docker compose` 起動までを end-to-end で確認する。
- **本手順は別リポジトリ（サンプルユニット）作成を要するため本リポジトリ内では未完**。Issue #230 に残す。

## 参照

- ユニット第一構成
- 依存方向の機械検査
- 契約階層化（ユニット固有イベント契約） — #229 で導入予定
- submodule 運用（本書の決定）
- 単独ビルド用フォールバック props の MSB4092 回避・実ファイル同梱
- [`src/README.md`](../../src/README.md) ユニット規約・依存規則
