---
title: 作業仕様書 — Platform.Shared.Kernel を新設し Result / Error を公開する
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0030（バックエンドアプリケーション層のライブラリ標準）"
  - "ADR-0041（Result 型の実装に外部ライブラリを認め、SharedKernel で包んで差し替え可能に保つ）"
related_specs:
  - ../../src/README.md
related_adrs:
  - IADR-0117
  - IADR-0056
issue: "#455"
related_issues:
  - "#454"
  - "#500"
---

# 作業仕様書: Platform.Shared.Kernel を新設し Result / Error を公開する

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（NFR。保守性・アプリケーション層標準）
- ユースケース（UC）/ 画面（SC）: 該当なし
- 関連計画 ADR: `ADR-0030`（選定基準 3・共有カーネル）／ `ADR-0041`（Result 型・封じ込め）
- 関連実装 ADR: [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)（配置を確定・実体は未作成としていた）／[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)

## 目的・背景

`#455`（バックエンドアプリケーション層標準への全面移行）は **11 件（#438 / #440 / #441 / #443 /
#444 / #445 / #447 / #448 / #449 / #450 / #451）のコード依存上の先行**である。その中で
`Platform.Shared.Kernel` は「Domain が唯一 `ProjectReference` を許される先」であり、Result / Error を
一貫させるための土台にあたる。

`IADR-0117` が配置（`src/platform/backend/Shared/`）を確定させ、`src/README.md` 依存規則 3 が
参照可能な 3 プロジェクトの 1 つとして名前を挙げているが、**実体が無い**。本作業でこれを作る。

## 着手前の再検証（母集合と実測。IADR-0180）

`#455` 本文の実測値（2026-08-16）は一部が古くなっていた。2026-08-21 に測り直した値を記す。

| 項目 | #455 の記録 | 2026-08-21 の実測 | 引いたコマンド |
| --- | --- | --- | --- |
| 空フォルダ ＋ `.gitkeep` | 0 件 | **55 件**（着地済み） | `git ls-files 'src/**/.gitkeep' \| wc -l` |
| `WolverineFx.RuntimeCompilation` の CPM 宣言 | 未宣言 | **宣言済み**（`Directory.Packages.props:79`） | `git grep -n Wolverine -- src/Directory.Packages.props` |
| `CSharpFunctionalExtensions` の CPM 宣言 | 未宣言 | **宣言済み**（`:88`） | 同上 |
| `Platform.Shared.Kernel` | 未作成 | **未作成のまま** | `git ls-files 'src/platform/backend/Shared/'` |
| `#500`（ADR-0041 への検査改定） | 「SharedKernel 作成前に検査改定が要る」 | **改定済み**。`check-backend-libraries.js` が `SHARED_KERNEL`・`SHARED_KERNEL_ALLOWED`・`bannedListFor()`・`sharedKernelViolations()` を持つ | `git grep -n Kernel -- scripts/check-backend-libraries.js` |
| 既存の Result / Error 型 | — | **0 件**（新規作成で衝突しない） | `git grep -ln 'record Error\|class Result\b' -- 'src/**/*.cs'` |

**除外したもの**: `src/ai-stock-trading`（submodule。本リポの規約対象外）、`templates/`（雛形は
別途 `#455` 子 N で扱う）。

## スコープ

- `src/platform/backend/Shared/Platform.Shared.Kernel/` を新設し、自前の `Result` / `Result<T>` /
  `Error` を公開する
- 内部実装としてのみ `CSharpFunctionalExtensions` を使う（ADR-0041 決定 1・2）
- `backend.slnx`（platform）へ登録する
- テストプロジェクト `Platform.Shared.Kernel.Tests` を 1 本作る（ADR-0030「Tests は 1 プロジェクト」）
- `src/README.md` 依存規則 3 の「実体は未作成」を実態へ追随させる

### スコープ外（意図的に含めない）

- **既存サービスを Result 型へ移行すること**。移行は各サービスの再実装 issue（#438〜#451）が行う。
  ここで全サービスへ手を入れると 400 行を大きく超え、監査が成立しない
- **xUnit v3 への移行**。CPM の `xunit.runner.visualstudio` は 2.8.2（v2 系）に固定されており、
  雛形の csproj が「**切替 issue の完了まで `xunit.v3` を参照するプロジェクトを作ってはならない**」と
  明記している。本作業のテストは **xunit v2 ＋ AwesomeAssertions** で書く
- **`FluentAssertions` の使用**。BANNED かつ ratchet 管理下であり、新規プロジェクトへ入れない

## 設計

### 公開する型

| 型 | 役割 |
| --- | --- |
| `Error` | `Code` / `Message` / `Kind` を持つ不変のエラー表現 |
| `ErrorKind` | `Failure` / `Validation` / `NotFound` / `Conflict` / `Unauthorized` / `Forbidden` |
| `Result` | 値を伴わない成否 |
| `Result<T>` | 値を伴う成否 |

### 公開する操作（ADR-0041 フォローアップ「公開する操作の一覧を確定する」への回答）

`Map` / `Bind` / `Tap` / `Match` / `Ensure` / `Combine` と、それぞれの `Task` 版。
**外部ライブラリの型・名前空間は公開面に一切出さない**（ADR-0041 決定 2）。

### 封じ込めの担保

`CSharpFunctionalExtensions` の `Result<T, E>` を `private readonly` フィールドとして保持し、
公開メソッドの引数・戻り値・例外の型に外部型を出さない。型エイリアスは使わない
（ADR-0041 決定 2 が「拡張メソッドと `Bind` / `Map` のチェーンは外部ライブラリの API が
そのまま漏れる」として明示的に退けている）。

## 受け入れ基準

1. `dotnet build src/platform/backend/backend.slnx` が **0 警告 0 エラー**で通る
2. `dotnet test src/platform/backend/backend.slnx` が通り、**基準値（Failed=0）から退行しない**
3. `node scripts/check-backend-libraries.js` が **exit 0**（`SharedKernel` の許可リスト検査を通る）
4. `node scripts/check-unit-dependencies.js` が **exit 0**
5. `node scripts/check-cpm-versions.js` が **exit 0**（`PackageReference` に版を直書きしない）
6. **変異試験 A**: `Platform.Shared.Kernel.csproj` へ許可リスト外のパッケージ（例 `Npgsql`）を
   足すと `check-backend-libraries.js` が **exit 1 する**
7. **変異試験 B**: `Platform.Shared.Kernel` の**公開面**に `CSharpFunctionalExtensions` の型が
   現れていないこと（公開 API の走査で 0 件）
8. 新設テストが `Map` / `Bind` / `Tap` / `Match` / `Ensure` / `Combine` の成功・失敗の**両経路**を
   通ること

## 検証の順序（DEFINITION_OF_DONE §2）

`git add -A` → 検査器 → コミット → `check-doc-updated.js` / `check-commit-messages.js` → push。

## 実行結果（証跡）

| 受け入れ基準 | 実行したコマンド | 結果 |
| --- | --- | --- |
| 1. ビルド 0 警告 0 エラー | `dotnet build src/platform/backend/backend.slnx` | **Build succeeded. 0 Warning(s) 0 Error(s)** |
| 2. テスト退行なし | `dotnet test src/platform/backend/backend.slnx` | Kernel.Tests **Passed 23 / Failed 0**、既存も Failed 0（LlmGateway 182 / Authorization 68 / Bff 231） |
| 3. 不採用ライブラリ検査 | `node scripts/check-backend-libraries.js` | **EXIT=0**（新規混入 0 件 / Domain 依存規律 OK） |
| 4. ユニット依存方向 | `node scripts/check-unit-dependencies.js` | **EXIT=0**（csproj 133 件 / .cs 1576 件） |
| 5. CPM 版直書き | `node scripts/check-cpm-versions.js` | **EXIT=0**（38 プロジェクト / 192 PackageReference） |
| 6. 変異試験 A | SharedKernel へ `Npgsql` を足す | **EXIT=1**「PackageReference『Npgsql』は許可リスト外です」 |
| 6'. 変異試験 A2 | `Platform.Shared.Contracts` へ `CSharpFunctionalExtensions` を足す | **EXIT=1**「不採用ライブラリへの参照が baseline に無い状態で追加されています」 |
| 7. 変異試験 B（公開面の封じ込め） | `Result` に外部型を返す公開メソッドを追加 | `公開面に外部ライブラリの型が現れない` が **FAIL**（漏れた型名を出力）。復旧後 Passed 23 |
| 8. 両経路の写像 | 同上テスト 23 本 | Map / Bind / Tap / Match / Ensure / Combine / Discard / 非同期の**成功・失敗の両方**を通過 |

**変異試験で素通りしたものは無い。** 3 本とも期待どおり落ちた。

## 判明した追加事実（#455 本文へ反映すべきもの）

- `templates/unit-template/backend/.../tests/` は **既に `SampleService.Tests` 1 本**であり、
  #455 本文の「雛形だけが Tests 2 本で違反」は解消済みである（`find` で 1 件）
- `#500`（ADR-0041 への検査改定）は**着地済み**。`check-backend-libraries.js` が
  `SHARED_KERNEL` / `SHARED_KERNEL_ALLOWED` / `bannedListFor()` / `sharedKernelViolations()` を持つ
