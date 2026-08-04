---
title: 作業仕様書 — CPM のバージョン直書き禁止を機械検査する（check-cpm-versions.js）
type: spec
status: done
related_ids: [NFR, ADR-0030, IADR-0115, IADR-0116, IADR-0120]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md"
related_specs:
  - ./20260803_issue-453_regression-test-foundation.md
  - ./20260803_issue-455_backend-application-standard.md
  - ./20260803_issue-471_backend-libraries-detection-gaps.md
  - ./20260803_issue-473_excluded-units-single-source.md
  - "../tests/TEST_STRATEGY.md"
  - "../tech/tech-requirements.md"
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "../adr/IADR-0116_reimplementation-branching-and-pr-policy.md"
  - "../adr/IADR-0120_excluded-units-from-gitmodules.md"
---

# 作業仕様書: CPM のバージョン直書き禁止を機械検査する

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — 依存バージョンの単一情報源を機械で守る）
- ユースケース（UC）/ 画面（SC）: なし
- **本作業が機械化する規約の典拠は [`CLAUDE.md`](../../CLAUDE.md)**（「技術スタック別ルール / C# / .NET」の
  「パッケージ」項）**であり、計画 ADR ではない**。計画リポジトリには CPM（Central Package Management）/
  `Directory.Packages.props` に関する決定が**存在しない**（`CPM` / `Central Package` /
  `Directory.Packages` の言及が全体で 0 件であることをクロス監査が grep で実証した）。
  検査器の失敗メッセージ・本書のいずれも、典拠として計画 ADR を挙げない。
- 関連 ADR:
  - `ADR-0030`（バックエンドアプリケーション層のライブラリ標準。Accepted）と棚卸し表
    `06_technical/12_backend-application-stack.md` は**隣接する制約**である。同 ADR が決めるのは
    「**どの**ライブラリを使うか」であり、「版を**どこに**書くか」には触れていない。本作業とは
    対象ファイル（`.csproj`）と `PackageReference` という走査面を共有するだけで、規約の典拠ではない
    （`check-backend-libraries.js` との関心の違いと同じ線引きである）。
  - [`IADR-0116`](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4
    （1 PR が大きくなる場合は issue を分割する）。本作業は #453 から分割された #467 である。
  - [`IADR-0115`](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キット同期規約）。
    新スクリプトの位置づけ確認に用いる（後述「IADR-0115 の位置づけ確認」）。
  - [`IADR-0120`](../adr/IADR-0120_excluded-units-from-gitmodules.md)（検査対象外ユニットの導出）。
    本検査器も同じヘルパで `ai-stock-trading` を除外する。
- 本リポジトリの起点: [#467](https://github.com/endazon/microservices-platform/issues/467)
  （親: [#453](https://github.com/endazon/microservices-platform/issues/453) /
  [#454](https://github.com/endazon/microservices-platform/issues/454)）
- 先行作業: [#455](https://github.com/endazon/microservices-platform/issues/455)（`check-backend-libraries.js` の新設）/
  [#471](https://github.com/endazon/microservices-platform/issues/471)（同検査器の走査対象拡張。XML 走査の流儀を借りる）/
  [#473](https://github.com/endazon/microservices-platform/issues/473)（`lib/excluded-units.js`。PR #482 でマージ済み）

## 目的・背景

[`CLAUDE.md`](../../CLAUDE.md)「技術スタック別ルール / C# / .NET」は

> **パッケージ**: Central Package Management。バージョンは `src/Directory.Packages.props` に集約し、
> `.csproj` の `PackageReference` にはバージョンを書かない。

と定めているが、**機械強制されていない**。全面再実装（#454）で 11 サービスを作り直す間、`.csproj` に
`Version=` が混ざっても誰も気付かない。CPM は「1 パッケージ 1 バージョン」を保証する仕組みであり、
直書きが 1 件混ざるとそのプロジェクトだけ別バージョンで解決される。ビルドは通り、テストも通り、
**実行時に初めて版差の挙動差が出る**種類の壊れ方であるため、レビューでの目視に頼るのは危うい。

`templates/` は特に重い。雛形は新サービスの出発点であり、**ここに直書きが入ると全新規サービスへ
伝播する**。さらに `templates/` は `ci.yml` のビルド対象外（ビルドするのは
`src/<unit>/backend/backend.slnx` のみ）のため、雛形の欠陥は誰かが手でコピーして `dotnet build` を
走らせるまで表面化しない。同型の見逃しは PR #463 のレビューで実際に指摘されている
（雛形の xUnit v3 と CPM runner 2.x の不整合）。

## 対象範囲

- 含むもの:
  1. [`scripts/check-cpm-versions.js`](../../scripts/check-cpm-versions.js) の新設（外部依存ゼロ・
     `--self-test` 付き）。
  2. [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) に独立ジョブ `cpm-versions` を追加。
  3. [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) に単体テストを追加。
  4. [`scripts/README.md`](../../scripts/README.md) の表・使い方・CI ジョブ表に行を追加。
  5. [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) のゲート一覧へ 1 行追加し、
     「後続 issue へ切り出す項目」の表の本項目を**取り消し線で消し込み**、#467 として実装済みである
     ことと参照先（ゲート一覧）を残す（行ごと削除すると「なぜ切り出したか」の経緯が消えるため）。
- 含まないもの:
  - **`Directory.Build.props` / `Directory.Packages.props` 側の検査**（`PackageVersion` の重複・
    未使用・並び順など）。#467 のスコープは「`.csproj` の `PackageReference` にバージョンを書かない」
    であり、CPM 定義ファイル自体の健全性は別の関心である。走査対象ファイル種の判断根拠は後述。
  - **`check-backend-libraries.js` への統合**。#455 は「**どの**ライブラリを使うか」、本検査は
    「バージョンを**どこに**書くか」であり関心が異なる（#467 が名指しで別スクリプトを指定）。
  - **ratchet / baseline**。着手時点の違反が 0 件であることを実測したうえで、最初から fail にする
    （後述「実測」）。既知違反が無いのに baseline 機構を持つと、機構の存在自体が
    「違反を baseline へ足せば通る」という抜け道になる。
  - `ai-stock-trading`（submodule）配下。別プロジェクトであり本リポジトリの規約を適用しない
    （[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)）。
  - **IADR の起票**。本作業は CLAUDE.md が既に確定している規約を機械化するだけで、新たな技術選定・
    制約の追加を伴わない（先例: [#471 の作業仕様書](./20260803_issue-471_backend-libraries-detection-gaps.md)）。
    検査仕様の設計判断は本書に記録する。

## IADR-0115 の位置づけ確認

| ファイル | キット原本（`planning/tools/impl-handoff-kit/repo-template/scripts/`） | 位置づけ | 本作業での扱い |
| --- | --- | --- | --- |
| `check-cpm-versions.js`（新設） | 無し | **固有デルタ種 3**（本リポにしか存在しない成果物・スクリプト） | 新規追加。キットへの環流はしない |
| `scripts.repo.test.js` | 無し（companion の受け口はキット側にある） | 固有デルタ種 3 | 追記する |
| `scripts.test.js` | 有り・バイト一致（分類 A） | 分類 A | **触らない** |
| `lib/excluded-units.js` | 無し | 固有デルタ種 3 | 参照のみ（改変しない） |
| `lib/ci-annotate.js` | 有り・バイト一致（分類 A） | 分類 A | 参照のみ |
| `scripts/README.md` | 有り・差分あり | 分類 B（固有デルタ種 3 を含む） | 同じ作法で追記 |
| `.github/workflows/ci.yml` | 有り・差分あり | 分類 B（固有デルタ種 2・3） | ジョブ 1 件を追加 |

CPM は .NET 固有であり、キット（技術スタック非依存）の前提ではない。よってキットへは環流しない。

## 検査仕様

### 判定

| 事象 | 扱い |
| --- | --- |
| `PackageReference` に `Version` 属性がある | **違反 → fail**（exit 1） |
| `PackageReference` に `<Version>` 子要素がある | **違反 → fail**（属性形と等価な MSBuild メタデータ記法） |
| `PackageReference` に `VersionOverride` 属性 / 子要素がある | **許可（warn）**。件数と箇所を実行サマリ・アノテーションへ出す |
| `PackageVersion` / `GlobalPackageReference` の `Version` | **対象外**（CPM の中央定義そのもの） |

`VersionOverride` を許可するのは CPM が公式に用意した回避口だからである。禁止すると、逃げ場を失った
実装者が `ManagePackageVersionsCentrally` を切るなど**より悪い抜け道**へ向かう。一方で無言で許すと
「中央定義があるのに実際には別の版で解決される」プロジェクトが静かに増えるため、**乱用の検出**として
可視化する（`ci-annotate` の `warn` ＋ `$GITHUB_STEP_SUMMARY` の表）。終了コードは変えない。

### 走査対象ファイル種（判断根拠）

**`.csproj` と `.csproj.sample` のみ**を走査する。`src/`（除外ユニットを除く）と `templates/` の双方。

- **`.props` / `.targets` を走査しない理由**: これらには**正当なバージョン記述が存在する**。
  `src/Directory.Packages.props` の `<PackageVersion Include="X" Version="Y" />` は CPM の中央定義
  そのものであり、`GlobalPackageReference` も同ファイルで版を伴って書くのが CPM の正しい書き方である。
  つまり props を走査対象へ入れると、「要素名で正当/違反を見分ける」責務が検査器に増える。
  #467 のスコープは `.csproj` であり、要素名の見分けを増やすだけの拡張は範囲外である。
  なお `Directory.Build.props` に `<PackageReference Update="X" Version="Y" />` を書いて配下へ一括注入
  する経路は理屈上あり得るが、**現状 0 件**であり（後述「実測」）、起こり得ないケースへの防御的実装は
  CLAUDE.md の禁止事項に当たる。必要になった時点で別 issue とする。
- **`.csproj.sample` も対象にする理由**: 雛形は実ビルドを避けるため `.sample` 付きで配布される場合が
  ある（`templates/unit-template/backend/Directory.Packages.props.sample` が実例）。現時点で
  `.csproj.sample` は 0 件だが、拡張子判定を `check-backend-libraries.js` の `isScannedBuildFile` と
  同じ流儀（末尾 `.sample` を許す）に揃えておく。判定関数だけで完結し、走査コストも増えない。
- **`.slnx` / `.cs` は対象外**: バージョン記述の場ではない。

### XML 走査の方針（`check-backend-libraries.js` の流儀に揃える）

正規表現ベースで走査する（XML パーサを持ち込まない＝外部依存ゼロの原則）。ただし本検査は
「属性の**不在**」を見るため、`check-backend-libraries.js` の「`Include` 値だけを拾う」1 本の正規表現
では足りない。要素単位で切り出し、属性と子要素の双方を見る 3 段構成にする。

1. `stripXmlComments()`: `<!-- ... -->` を除去する。
2. `packageReferenceElements()`: `<PackageReference ...>` を要素単位で切り出す
   （自己終了形 `/>` と、開始タグ〜`</PackageReference>` の対応形の双方）。
3. `parseAttributes()` / `metadataOf()`: 属性と子要素メタデータを取り出す。

### エッジケースと方針

| ケース | 例 | 方針 |
| --- | --- | --- |
| 属性形 | `<PackageReference Include="X" Version="1.0" />` | **違反** |
| 子要素形（メタデータ記法） | `<PackageReference Include="X"><Version>1.0</Version></PackageReference>` | **違反**。MSBuild では属性形と等価であり、これを見ないと検査は素通りする |
| 属性の順序 | `<PackageReference Version="1.0" Include="X" />` | 順序に依存しない（属性をトークン化して読む） |
| `Update` 形 | `<PackageReference Update="X" Version="1.0" />` | **違反**。パッケージ ID は `Include` → `Update` の順で解決し、無ければ `(不明)` と表示 |
| `VersionOverride` | `<PackageReference Include="X" VersionOverride="1.0" />` | **警告**（許可）。`\bVersion\s*=` は `VersionOverride=` に一致しないため、違反側へ誤って落ちない |
| `PackageVersion` | `<PackageVersion Include="X" Version="1.0" />` | **非対象**。要素名を `<PackageReference` の完全一致で見る |
| `GlobalPackageReference` | `<GlobalPackageReference Include="X" Version="1.0" />` | **非対象**。`<` 直後から要素名を見るため `<PackageReference` に一致しない |
| 条件付き `ItemGroup` | `<ItemGroup Condition="'$(TF)'=='net10.0'"><PackageReference Include="X" Version="1.0" /></ItemGroup>` | **条件を解釈しない＝違反**。条件付きでも直書きは直書きであり、むしろ条件付きの方が「特定条件でだけ別の版になる」ため危険度が高い |
| プロパティ参照 | `<PackageReference Include="X" Version="$(XVersion)" />` | **違反**。間接参照でも「版が csproj 側で決まる」ことに変わりはない |
| 単一引用符 | `<PackageReference Include='X' Version='1.0' />` | XML として妥当なため両対応にする |
| コメントアウト | `<!-- <PackageReference Include="X" Version="1.0" /> -->` | **違反にしない**。雛形・csproj には実際に説明コメントがあり（実測 52 ブロック / 34 ファイル）、例示を赤にすると検査が邪魔になる |
| 属性値に含まれる文字列 | `Condition="'$(C)'=='Version=1'"` | 属性をトークン化して読むため、値の中身を属性名と誤認しない |
| 空の `Version` | `<PackageReference Include="X" Version="" />` | **違反**。空文字でも「csproj に版の場がある」ことは同じで、意図が読めない記述を素通りさせない |

## 実装（変更点）

| ファイル | 変更 |
| --- | --- |
| [`scripts/check-cpm-versions.js`](../../scripts/check-cpm-versions.js) | 新設。公開 API は `toPosix` / `stripXmlComments` / `parseAttributes` / `packageReferenceElements` / `metadataOf` / `packageIdOf` / `isScannedProjectFile` / `inlineVersionFindings` / `scanTree` / `EXCLUDED_UNITS` / `isExcludedPath`。自己試験は CLI の `--self-test` からのみ実行する（他検査器と同じ作法） |
| [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) | ジョブ `cpm-versions` を追加（`backend-libraries` と同形式: self-test → 素実行）。既存ジョブの本文は触らない |
| [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) | 単体テストを追加（判定の境界・エッジケース・実リポジトリ走査・`--self-test` の exit 0） |
| [`scripts/README.md`](../../scripts/README.md) | 表・ローカル実行例・CI ジョブ表へ追記 |
| [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | ゲート一覧へ 1 行追加。「後続 issue へ切り出す項目」の本項目を取り消し線で消し込み（行は残す） |

### CI ジョブ

```yaml
  cpm-versions:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-node@v7
        with:
          node-version: "20"
      - name: Self-test CPM version checker
        run: node scripts/check-cpm-versions.js --self-test
      - name: Check CPM version pinning
        run: node scripts/check-cpm-versions.js
```

submodule は取得しない（対象は `platform` / `knowledge` / `templates` のみで、いずれも本リポジトリの
実体である）。`backend-libraries` ジョブと同じ形であり、既存ジョブの行には触れない（過去に `ci.yml`
のシェア行がインターリーブして衝突した事故があるため、追加は独立位置に最小差分で行う）。

## 受け入れ基準

- [x] `.csproj` に `Version` 直書きが入ると `node scripts/check-cpm-versions.js` が exit 1 になる
      （`--self-test` の**負例**が一時ツリーを実走査して固定する。属性形・子要素形・`Update` 形・
      条件付き `ItemGroup`・`templates/` 配下の 5 系統）。
- [x] `VersionOverride` の使用箇所が実行サマリ（`$GITHUB_STEP_SUMMARY`）とアノテーションに出る。
      終了コードは変わらない。**検出**（どの要素を `VersionOverride` と見なすか）は `--self-test` の
      正例が、**出力経路**（サマリの表と `::warning::` の実体）は `scripts.repo.test.js` の子プロセス
      テストが固定する。検出だけを試験すると `reportOverrides()` が壊れても緑のままになる。
- [x] 現状のリポジトリで違反 0 件・exit 0（実測は下記。見込みではなく実測値）。
- [x] `src/ai-stock-trading` 配下は走査対象外（`lib/excluded-units.js` から導出。ハードコードしない）。
- [x] `templates/` 配下も走査対象に含まれる。
- [x] `node scripts/check-cpm-versions.js --self-test` が exit 0。
- [x] `node scripts/scripts.test.js` が緑で、テスト件数が着手前から減っていない。
      `REQUIRE_REPO_TESTS=1` でも緑。
- [x] `node scripts/check-doc-links.js` が exit 0（本仕様書のリンクを含む）。
- [x] `.github/workflows/ci.yml` が YAML としてパースでき、既存ジョブが増減していない。
- [x] `node scripts/check-commit-messages.js --base origin/develop` が緑。

## 検証（実測）

### 測定条件

- 対象コミット: `origin/develop` = `5031483`（`docs(NFR): check-unit-dependencies.js のコメントを
  IADR-0117 の 3 プロジェクトへ追随 (#485)`）から作成した worktree。
- **submodule は未 populate**（`git submodule status` が `planning` / `src/ai-stock-trading` の双方に
  `-` プレフィクスを付ける状態）。したがって `src/ai-stock-trading` は空ディレクトリであり、
  除外の効き目は**走査件数には現れない**（除外規則そのものは `--self-test` のパス判定で固定する）。
  この条件を書かない実測値は再現不能である（#484 の教訓）。
- Node: v22.22.2（CI は 20。本検査器は Node 標準モジュールのみを使い、両者で挙動差は無い）。

### 実測値

| 項目 | 実測 |
| --- | --- |
| 走査した `.csproj` | **37 件**（`src/` 30 件 ＋ `templates/` 7 件。`.csproj.sample` は 0 件） |
| `PackageReference` 要素の総数 | **195 件** |
| バージョン直書き違反（`Version` 属性 / `<Version>` 子要素） | **0 件** |
| `VersionOverride` の使用 | **0 件** |
| 参考: `.props` / `.targets`（4 件）に `<PackageReference ... Version=` を書く一括注入 | **0 件**（走査対象外とした判断の裏付け） |
| 参考: 走査対象 `.csproj` 内の XML コメントブロック | **52 個 / 34 ファイル**（コメントを剥がす理由の裏付け） |

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-cpm-versions.js` | `OK: 37 プロジェクト / 195 件の PackageReference にバージョン直書き 0 件（VersionOverride 0 件）` / exit 0 |
| `node scripts/check-cpm-versions.js --self-test` | 自己試験 **49 件 OK** / exit 0 |
| `node scripts/scripts.test.js` | **209 tests passed** / exit 0（着手前 **197 件** → +12 件。着手前の値は `origin/develop` 版の `scripts.repo.test.js` へ一時的に戻して実測） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **209 tests passed** / exit 0 |
| `node scripts/check-doc-links.js` | OK: **398 件**の Markdown に破損リンクなし / exit 0（着手前 397 件。増分は本仕様書 1 件。未 populate の submodule 配下 673 件は対象外） |
| `python3 -c "import yaml; yaml.safe_load(...)"`（`ci.yml`） | パース成功。ジョブ **14 → 15**（増分は `cpm-versions` のみ。既存 14 ジョブ名は不変、`git diff` は **20 行の挿入のみ・削除 0 行**） |
| `node scripts/check-commit-messages.js --base origin/develop` | 緑（1 コミット） |

### 負例の実効性（fail することの実測）

`--self-test` は一時ツリーを `scanTree()` で実走査し、次の 5 系統がいずれも違反として検出されることを
固定する（関数単位の試験だけでは「走査対象に入っているか」を確かめられないため）。

| 負例 | 検出 |
| --- | --- |
| `src/platform/.../A.csproj` の `Version="1.0.0"` 属性 | 違反 1 件 |
| `src/knowledge/.../B.csproj` の `<Version>2.0.0</Version>` 子要素 | 違反 1 件 |
| `src/knowledge/.../C.csproj` の `Update` 形 ＋ 条件付き `ItemGroup` | 違反 1 件 |
| `templates/.../T.csproj` の `Version` 属性 | 違反 1 件（雛形も走査対象） |
| `src/ai-stock-trading/.../X.csproj` の `Version` 属性 | **検出しない**（除外ユニット） |

走査したプロジェクトは 5 件（AST の 1 件を除く）、違反は 4 件、`VersionOverride` は 1 件（別プロジェクトの
適合ファイルに置いた分）で、いずれも実測どおり固定されている。CPM の中央定義（`src/Directory.Packages.props`）
を同じ一時ツリーへ置き、**走査対象に含まれないこと**も併せて固定した。

### `VersionOverride` の可視化（自動テストで固定）

実リポジトリの使用は現状 0 件のため、`VersionOverride="9.9.9"` を持つ `.csproj` を実ツリーへ一時的に
設置して観測する。**この観測は手測定で終わらせず `scripts.repo.test.js` の子プロセステストにした**——
検出（`inlineVersionFindings()`）だけを試験すると、出力側の `reportOverrides()` が壊れても
「警告が出ない」ことは終了コードに現れず、CI は緑のまま通ってしまう。テストが固定するのは次のとおり。

- 終了コードは **0 のまま**（違反ではなく許可であること）。
- `GITHUB_ACTIONS=true` のとき stdout に
  `::warning::CPM の VersionOverride を 1 件使用しています…` と `パッケージ=版` が出る。
- `GITHUB_STEP_SUMMARY` の指すファイルへ `### CPM: VersionOverride の使用箇所` の表
  （プロジェクト / パッケージ / 版 / 記法）が追記される。
- プローブ撤去後は警告が出ない（残置して恒常的な警告になっていないこと）。

一時ファイル（プローブの `.csproj` とサマリ）は `finally` で必ず撤去し、`git status` がクリーンで
あることを確認している。あわせて手動でも、同じツリーへ直書き 2 件（属性形・子要素形）を足すと
**exit 1** になり、`VersionOverride` の警告は引き続き出たうえで違反 2 件が記法別
（`Version 属性` / `<Version> 子要素`）に報告されることを確認した。

あわせて `scripts.repo.test.js` の子プロセステストで、直書きを持つ `.csproj` を実ツリーへ一時的に置くと
素実行が **exit 1**（`バージョン直書き 1 件`）になり、撤去後は再び exit 0 に戻ることを固定した。
既存の追跡ファイルは書き換えず**新規ファイルの設置と撤去**で行う（テストが異常終了しても既存の
`.csproj` を壊さないため）。

## 影響・リスク

- **偽陽性で CI が止まるリスク**: 現状 0 件のため、赤が出るのは新規に直書きが入ったときのみである。
  正当な逃げ道（`VersionOverride`）は許可済みで、失敗メッセージにその旨と書き方を出す。
- **正規表現ベースの限界**: XML パーサを使わないため、`<PackageReference` を含む CDATA・文字列
  リテラル等は誤検出し得る。`.csproj` にそれらが現れる現実的な理由が無いこと、および XML パーサの
  導入は外部依存ゼロの原則に反することから、コメント除去のみで足りると判断した。
- **`.props` 経由の一括注入は見ない**: 上記「走査対象ファイル種」のとおり現状 0 件であり、
  必要になれば別 issue とする。`check-backend-libraries.js` は既に props / targets を走査しており
  （#471）、「どのライブラリか」の側からは覆えている。

## フォローアップ（本作業では行わない）

- **CPM 規約を計画側の制約へ昇格させるか**。現在、CPM の採用と「版は
  `src/Directory.Packages.props` に集約する」は [`CLAUDE.md`](../../CLAUDE.md) だけが典拠であり、
  計画リポジトリには対応する決定が無い（上記「起点となる計画書」の実証を参照）。本検査器は
  実装側の規約を機械化したものであり、この非対称は現時点で不整合ではない。
  ただし、CPM は「1 パッケージ 1 バージョン」という**サービス横断の制約**（xUnit v3 への移行が
  全テストプロジェクト同時になる、といった形で現に効いている）であるため、計画 ADR へ昇格させる
  価値はある。昇格させる場合は `/plan-feedback` で計画リポジトリへ ADR 起票を提案する。
  **本作業では環流しない**——#467 のスコープは既存規約の機械化であり、計画側の意思決定を
  実装 PR に混ぜない（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4）。
  昇格した場合は、本検査器の失敗メッセージと本書の典拠記述を新 ADR へ追随させること。
