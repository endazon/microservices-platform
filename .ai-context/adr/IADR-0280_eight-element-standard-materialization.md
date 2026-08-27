---
title: IADR-0280 計画 8 要素標準を実プロジェクトとして実体化する（配置写像と段階計画）
type: impl-adr
status: Superseded
related_ids:
  - NFR
  - ADR-0002
  - ADR-0019
  - ADR-0030
  - ADR-0041
  - IADR-0027
  - IADR-0056
  - IADR-0117
  - IADR-0218
  - IADR-0219
  - IADR-0229
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (§基本方針・§プロジェクト構成・§規範性・粒度・置き場。fixed)
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (選定基準 3: 層の依存規律)
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Result 型の封じ込め)
related_specs:
  - ../specs/20260828_arch-foundation_eight-element-materialization.md
---

# IADR-0280: 計画 8 要素標準を実プロジェクトとして実体化する（配置写像と段階計画）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: リポジトリオーナーの裁定（2026-08-27。planning#490 に環流済み）＋ 実装（claude）

## 起点・関連

- 関連する計画書 ID: 無採番 `NFR`（アーキテクチャ構造の保守性。稼働する製品の採番 NFR に当たる番号が無い
  メタ寄りの土台作業だが、[IADR-0179](IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 2 のとおり
  「番号が無い」は「実装側で作ってよい」ではない —— 本決定はオーナー裁定 2026-08-27 を根拠に持つ）/
  計画 `12_backend-application-stack`（fixed）/ 計画 `ADR-0030`・`ADR-0041`
- 関連する実装 ADR:
  - [IADR-0027](IADR-0027_composability-folder-structure.md)（**改定対象**。選択肢 3
    「可変部分を別アセンブリへ物理分離」の却下理由「プロジェクト数が倍増し過剰分割（計画方針と不整合）」を
    本決定が改める。Foundation / Composable の区分そのものと Foundation → Composable 参照禁止は存続）
  - [IADR-0218](IADR-0218_gitkeep-standard-components-scope.md) /
    [IADR-0219](IADR-0219_sharedkernel-granularity-and-worker-standard-component.md)（**改定対象**。
    「実体が無い要素は `.gitkeep` の枠だけを置く」という適用形を、`SharedKernel` を除く 4 要素について
    「実プロジェクトとして実体化する」へ改める。8 要素・`Api`/`Worker` 排他・`SharedKernel` 併存の決定は存続）
  - [IADR-0117](IADR-0117_platform-shared-kernel-placement.md) /
    [IADR-0229](IADR-0229_shared-kernel-result-surface.md)（`Platform.Shared.Kernel` の配置と公開面。
    本決定はここへ DDD 基底型を足す —— 覆さない）
- 関連する実装仕様書:
  [20260828_arch-foundation_eight-element-materialization.md](../specs/20260828_arch-foundation_eight-element-materialization.md)
- 起点: オーナー裁定 2026-08-27（planning#490）

## コンテキストと課題

計画 `12_backend-application-stack`（fixed）は 8 要素標準
（`Api`/`Worker`・`Application`・`Domain`・`Infrastructure`・`Contracts`・`SharedKernel`・`Tests`）と
「Pragmatic Clean Architecture ＋ Vertical Slice」「Domain 層は SharedKernel を除き外部ライブラリへ
依存しない」を定める。一方、実装は全 14 サービスが単一の `.Api`（または `.Worker`）プロジェクトに
畳まれ、8 要素のうち 5 要素は `.gitkeep` のみの空枠（60 件）だった。実コードは
`*.Api/Foundation/{Domain,Endpoints,Persistence,Ports,Services}` と `*.Api/Composable/{Adapters,Steps}`
に同居している。

この形を支えていたのは [IADR-0027](IADR-0027_composability-folder-structure.md) 選択肢 3 の却下
（「別アセンブリ分離は過剰分割」）だが、**計画の「Domain 層は外部ライブラリへ依存しない」は
同一アセンブリ内のフォルダ分けでは機械的に担保できない**（`*.Api.csproj` の EF Core 参照は
`Foundation/Domain/` の .cs からも見える）。`scripts/check-backend-libraries.js` の規則 2
（`*.Domain.csproj` は PackageReference を持てない）も対象 0 件で空振りしていた。
**オーナーが 2026-08-27 に 8 要素の実体化（ソースの実プロジェクトへの物理配置）を裁定した**（planning#490 に環流済み）。

［2026-08-28 追記 / #1021］**オーナー裁定により本 ADR は Superseded となった
（Superseded by [IADR-0282](IADR-0282_single-project-vsa-structure.md)）。**
サービスは単一プロジェクト＋ Features / Domain / Infrastructure / Common のフォルダ規範へ改める。
決定 6（DDD 基底型は `Platform.Shared.Kernel`）のみ存続。以下の決定は歴史的記録として凍結する。

## 決定

### 決定 1 — 段階計画: 土台＋パイロット → 残り 13 サービス → 新規は即・新配置

1. **段 1（本 PR）**: 土台（`Platform.Shared.Kernel` への DDD 基底型・全 14 サービスの
   空プロジェクト実体化・レイヤ依存方向の機械検査）と、パイロット 1 サービス
   （**FeedbackService**。Foundation/{Domain,Endpoints,Persistence} のみ・Composable 無し・小規模）の完全移送。
2. **段 2（後続波）**: 残り 13 サービスの移送。サービスごとに本決定の写像（決定 2）を適用する。
3. **移行期間中も、新規コードは最初から新配置で書く。** 空プロジェクトが実体化済みなので、
   旧配置（`*.Api/Foundation/Domain/` 等）へ新規のドメイン型・永続化を足さない。

### 決定 2 — 配置写像（現行フォルダ → 8 要素プロジェクト）

| 現行（単一 `.Api` / `.Worker` 内） | 新配置 |
| --- | --- |
| `Foundation/Domain/` | **`<Svc>.Domain`**（プロジェクト全体が固定。区分フォルダは持たず、ファイルはプロジェクト直下から置く。ProjectReference は `Platform.Shared.Kernel` のみ・PackageReference ゼロ） |
| `Foundation/Ports/`・`Foundation/Services/`（ユースケース調整）・Wolverine ハンドラ | **`<Svc>.Application`**（`Foundation/` を第 1 階層フォルダとして温存する） |
| `Foundation/Persistence/`（DbContext）・`Composable/Adapters/`・`Composable/Connectors/` | **`<Svc>.Infrastructure`**（`Foundation/` / `Composable/` を第 1 階層フォルダとして温存する） |
| `Foundation/Endpoints/`（薄い端点）・`Composable/Steps/`（メッセージ購読の受け口）・`Program.cs`（合成ルート）・`appsettings*.json`・`TestMarker.cs` | **`<Svc>.Api`**（または **`<Svc>.Worker`**。排他は [IADR-0219](IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) 決定 2 のまま） |
| `Migrations/` | **`<Svc>.Infrastructure/Migrations/`**（決定 4） |
| サービス単独公開の契約（proto・DTO） | **`<Svc>.Contracts`**（現状 0 件。ユニット共有の契約は従来どおり `<Unit>.Contracts` —— 計画 ADR-0019 決定 4 の併存） |

- **名前空間はフォルダ階層に一致させる**（IADR-0027 の規約を維持）。例:
  `FeedbackService.Domain` / `FeedbackService.Infrastructure.Foundation.Persistence` /
  `FeedbackService.Infrastructure.Migrations` / `FeedbackService.Api.Foundation.Endpoints`。
- **Foundation / Composable の「固定/可変」区分は層内の第 1 階層フォルダとして温存する**（`Domain` と
  `Contracts` は全体が固定のため区分フォルダを持たない）。**Foundation → Composable 参照禁止は維持**し、
  既存の機械検査（`check-unit-dependencies.js` 規則 2）がそのまま効く。可変実装の束ねは従来どおり
  `Program.cs`（合成ルート）のみが行う。
- エンドポイントは薄く保つ。ユースケース調整の切り出しは**振る舞いを変えない移送**の範囲でのみ行い、
  移送のついでのリファクタはしない（`CLAUDE.md` 禁止事項「計画外の大規模リファクタ」）。

### 決定 3 — 参照方向: Domain ← Application ← Infrastructure ← Api

```text
<Svc>.Domain          ← ProjectReference は Platform.Shared.Kernel のみ
<Svc>.Application     → <Svc>.Domain
<Svc>.Infrastructure  → <Svc>.Application（推移的に Domain）
<Svc>.Api / .Worker   → <Svc>.Infrastructure（推移的に Application / Domain。Application の直接参照も可）
<Svc>.Contracts       → 参照なし（公開契約。他 7 要素を参照しない）
<Svc>.Tests           → 従来どおり 1 プロジェクト（参照制約の対象外）
```

- 逆向き（Domain → Application 等）と同格横断は違反とし、`scripts/check-unit-dependencies.js` の
  **規則 3** が機械検査する（①同一サービス内 8 要素間の ProjectReference の方向、
  ②`*.Domain` プロジェクト内 .cs への `using Microsoft.EntityFrameworkCore` / `using MassTransit` /
  `using Wolverine` / `using Refit` の混入）。
- `Tests` は 1 プロジェクトのまま（計画 §規範性・粒度・置き場）。`.csproj` の実名はホスト種別に合わせた
  現況（`<Svc>.Api.Tests` / `<Svc>.Worker.Tests`）を維持する。

### 決定 4 — EF Migrations は Infrastructure へ移す（DbContext と同一アセンブリ）

`Migrations/` を `<Svc>.Infrastructure/Migrations/` へ移す。**EF Core の既定の MigrationsAssembly は
「DbContext が属するアセンブリ」**であり、DbContext と Migrations を同じ `<Svc>.Infrastructure` に
置けば `MigrationsAssembly` の明示指定は不要になる（起動プロジェクト = Api、対象プロジェクト =
Infrastructure という `dotnet ef` の標準形）。IADR-0027 の例外「`Migrations/` は直下に残す」は、
DbContext が Api にあった当時の帰結であり、**DbContext の移動に随伴して Migrations も移る**。
`Microsoft.EntityFrameworkCore.Design`（PrivateAssets）は **startup project 側（Api / Worker）に残す**
—— EF Core Tools は Design を startup project に要求し、`PrivateAssets="all"` のため
Infrastructure からは推移しない（パイロットで実測。Infrastructure 側へ置いた形は
`dotnet ef` が「doesn't reference Microsoft.EntityFrameworkCore.Design」で拒む）。

- 既存 Migration の `.Designer.cs` / `ModelSnapshot.cs` が持つ CLR 型名文字列
  （例: `"FeedbackService.Api.Foundation.Domain.AnswerFeedback"`）は新名前空間へ**機械的に追随させる**。
  テーブル・カラム定義は変えないため、次回 `dotnet ef migrations add` で空でない差分が出ない
  （出るなら移送に誤りがある）。MigrationId（`[Migration("...")]`）は変えないため、
  適用済み DB に対する `MigrateAsync()` の挙動も変わらない。

### 決定 5 — サービス単位 `SharedKernel` は実体化せず、`.gitkeep` の枠を維持する

14 サービスの `<Svc>.SharedKernel` は**空の `.csproj` を作らず、`.gitkeep` の枠のまま残す**
（枠が無かった McpServer / NotificationService へは枠を新設してドリフトを是正する）。理由:

1. **中身が 0 件である。** 「自サービスに閉じた共通基底」（[IADR-0219](IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) 決定 1 の置き分け）に
   当たる型は現状どのサービスにも無い。空の `.csproj` を 14 個並べることは、計画 §規範性 が
   `.gitkeep` を採った理由（「空の `.csproj` を並べるとソリューションとビルド対象が無用に増える」）
   そのものに反する。
2. **参照できない死枠になる。** `scripts/check-backend-libraries.js` は `*.Domain.csproj` の
   ProjectReference を `Platform.Shared.Kernel` ただ 1 つに限っており（ADR-0041 決定 3 の推移閉包検査）、
   空の `<Svc>.SharedKernel` を作っても Domain から参照した瞬間に検査が落ちる。
3. **他の 4 要素と違い、移送元となる既存コードが無い。** Application / Infrastructure / Contracts は
   段 2 で実コードを受け取るために実体が要るが、SharedKernel には移すものが無い。

**最初に自サービス閉じの共通基底を必要とするサービスが実体化する。** その PR は
`<Svc>.Domain → <Svc>.SharedKernel` の参照を許すよう `check-backend-libraries.js`
（domainViolations の許容）と `check-unit-dependencies.js` 規則 3 を同時に改定すること
（ADR-0041 の封じ込め —— 外部パッケージの持ち込み規律 —— を per-service 側にも定義してから開けること）。

### 決定 6 — DDD 基底型は `Platform.Shared.Kernel` に置く

`Entity<TId>` / `ValueObject` / `AggregateRoot<TId>`（`DomainEvents` / `Raise` / `ClearDomainEvents`）/
`IDomainEvent` を `Platform.Shared.Kernel` へ追加する。**Domain の唯一の許容参照先が
`Platform.Shared.Kernel` である以上、全サービスの Domain が共有する基底型はそこにしか置けない**。
計画の構成図も `SharedKernel` の内容を「Result / Error・**共通基底**」と定めており、置き分け
（境界をまたいで同一性が要る型はユニット単位側）とも整合する。外部ライブラリは使わず
（既存 `CSharpFunctionalExtensions` は Result 型の内部実装のみ）、公開面の封じ込め
（[IADR-0229](IADR-0229_shared-kernel-result-surface.md)・EncapsulationTests）を保つ。
**過度な基盤化はしない** —— 仕様化するのは同一性（Entity は Id、ValueObject は構成要素）と
ドメインイベントの蓄積だけで、監査列・楽観ロック・Specification 等の先回りは足さない。

### 決定 7 — 空プロジェクトの形

- `.csproj` は `<Project Sdk="Microsoft.NET.Sdk">` ＋ ProjectReference のみ（`TargetFramework` 等は
  `src/Directory.Build.props` が既定を与える。CPM のためバージョンは書かない）。
- 実体化した 4 要素 ×14 サービスの `.csproj` は所属ユニットの `backend.slnx` へ登録する。
- `.gitkeep` は実体化した要素のぶんだけ削除する（`SharedKernel` の枠は決定 5 のとおり残す）。

## 検討した選択肢（要点）

| | A. 実体化（採用） | B. 現状維持（単一 Api ＋ .gitkeep 枠） | C. SharedKernel まで含め 5 要素すべて実体化 |
| --- | --- | --- | --- |
| 計画 fixed「Domain は外部ライブラリへ依存しない」の機械担保 | **できる**（csproj 境界 ＋ 検査器） | できない（フォルダ分けは参照を切らない） | できる |
| オーナー裁定 2026-08-27 | 従う | **反する** | 従う |
| ビルド対象の増分 | 56 プロジェクト | 0 | 70 プロジェクト（うち 14 は中身 0 件・参照不能） |
| check-backend-libraries 規則 2 | 実対象化（14 件） | 空振りのまま | 実対象化するが per-service SharedKernel の規律が未定義のまま枠だけ開く |

## 結果

- 良い影響: Domain 層の外部依存ゼロが csproj 境界と検査器で機械的に担保される。計画の 8 要素標準と
  実装の物理配置が一致し、「標準に揃った」の判定が曖昧でなくなる。段 2 の移送はサービスごとに
  同じ写像を適用するだけになる。
- 悪い影響・トレードオフ: ビルド対象が 56 プロジェクト増え、両 slnx のビルド時間が延びる
  （実測は作業仕様書に記録）。段 2 完了まで旧配置と新配置が併存し、読み手は決定 1-3 を知らないと
  どちらが正か迷う（`src/README.md` の注記で緩和）。`.gitkeep` の適用形を定めた
  IADR-0218 / IADR-0219 の記述が部分的に古くなる（両 IADR へ日付つき追記ブロックを置いた）。
- フォローアップ:
  1. 残り 13 サービスの移送 issue（後続波。本 PR の射程外）。
  2. `templates/unit-template/backend/`（SampleService 雛形）の新配置への追随（後続波。
     kit との乖離は受容の枠内で、雛形は現行の gitkeep 形のまま動く）。
  3. `src/ai-stock-trading` への追随は向こうのリポジトリの issue で行う。

## 関連

- Supersedes: なし（[IADR-0027](IADR-0027_composability-folder-structure.md) の選択肢 3 却下と
  [IADR-0218](IADR-0218_gitkeep-standard-components-scope.md) /
  [IADR-0219](IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) の
  「枠のみ設置」を**部分改定**する。旧 ID は残し、各 IADR へ日付つき追記ブロックと Amended by を置いた）
- Superseded by: [IADR-0282](IADR-0282_single-project-vsa-structure.md)（決定 6 を除く全決定）
