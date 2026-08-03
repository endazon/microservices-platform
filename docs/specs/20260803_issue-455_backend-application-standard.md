---
title: バックエンドアプリケーション層標準（ADR-0030）の確立と機械的強制
type: spec
status: in-progress
related_ids: [NFR, ADR-0020, ADR-0027, ADR-0029, ADR-0030, IADR-0116, IADR-0117]
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md"
---

# 仕様書: バックエンドアプリケーション層標準（ADR-0030）の確立と機械的強制

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・費用。アプリケーション層の標準化）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR:
  [ADR-0030](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)（Accepted・本作業の一次情報）／
  [ADR-0027](../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md)（Wolverine）／
  [ADR-0029](../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md)（gRPC/REST）／
  ADR-0020（.NET 10）／[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（進行規約）
- 計画書リンク: [12_backend-application-stack](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)（`fixed`・棚卸し表が正）
- 本リポジトリの起点: #455（親 #454 フェーズ 0）

## 目的・背景

2025〜2026 年の .NET OSS 商用化（MediatR / AutoMapper / MassTransit / FluentAssertions）と保守停滞（Mapster）を
受け、計画側がアプリケーション層のライブラリ標準と設計様式を確定した（ADR-0030・Accepted）。計画書は
「**実装リポジトリは MassTransit・FluentAssertions 等を使用中であり、移行の段取りは実装側で Issue 化する**」と
明記しており、本 issue がそれにあたる。

### #455 の本体は「標準の確立と強制」であって「全サービスの書き換え」ではない

#455 本文は「サービス個別の再実装 issue（#438〜#451）は本標準の上に実装する」と定めている。したがって
本作業の成果物は、**後続 13 issue が乗る土台**である。実際にコードを新標準へ書き換える作業は各サービスの
再実装 issue に属する。この分担を守らないと、#455 が 11 サービス分の実装を丸ごと抱えて
[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4（レビュー可能な変更単位）を破る。

### 現状の実測（`develop` = `3441861` 時点）

| 対象 | 実測値 |
| --- | --- |
| サービス数 | **11**（platform 2: AuthorizationService / LlmGateway、knowledge 9: AiAnalysis / Conversion / Dashboard / DataSource / Document / Feedback / Ingestion / Retrieval / Wiki） |
| BFF | 2（`Platform.Bff` / `Knowledge.Bff.Endpoints`） |
| `.csproj` 総数 | 30 |
| 現行のサービス内構成 | `src/<Name>.Api`（単一プロジェクト）＋ `tests/<Name>.Api.Tests`。**標準の 7 プロジェクト構成ではない** |
| MassTransit | `.csproj` 15 / `.cs` 59 |
| FluentAssertions | `.csproj` 14 / `.cs` 129 |
| Serilog | `.csproj` 3 / `.cs` 15 |
| xUnit | v2（`xunit 2.9.3`）。標準は **v3** |

**この規模がそのまま「参照禁止の機械的強制」と衝突する。** 不採用ライブラリを即時に禁止する検査を入れると、
既存 11 サービスが未移行である間ずっと CI が赤くなる。[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) が
警告した「成果物は正しいのに赤」の常態化そのものであり、採ってはならない。後述の **ratchet 方式**で解く。

## 対象範囲

- 対象:
  1. **技術要件書の書き直し**（[`docs/tech/tech-requirements.md`](../tech/tech-requirements.md)）— 新標準を反映する
  2. **標準プロジェクト構成の定義**とサービス雛形（`templates/`）
  3. **CPM への標準ライブラリ集約**（[`src/Directory.Packages.props`](../../src/Directory.Packages.props)）
  4. **不採用ライブラリの参照禁止**の機械的強制（新規 `scripts/check-backend-libraries.js` ＋ CI ジョブ）
  5. **Domain 層の外部依存ゼロ**検査（同スクリプト）
  6. 年 1 回の保守状況点検（AwesomeAssertions・Wolverine）を[運用仕様書](../operations/)に記載する
- 対象外:
  - **既存 11 サービスの新標準への書き換え**。各サービスの再実装 issue（#438〜#451）で行う。
  - サービス間通信（ブローカー・トランスポート・gRPC/REST 境界）— **#441** の担当。本作業は各サービス
    *内部*のアプリケーション層に限る（#455 本文の分担定義）。
  - フロントエンド（`src/*/frontend/`）— ADR-0031 系（#446）。
  - 退行防止テスト基盤そのもの（受け入れ基準→テスト写像規約・カバレッジ ratchet）— **#453** の担当。
    本作業が追加するのは「ライブラリ棚卸しからの逸脱」検査に限る。

## 設計

### 1. 標準プロジェクト構成（サービス単位）

計画書 12_backend-application-stack の構成をそのまま採る。

```text
src/<unit>/backend/Services/<Name>Service/
 ├── src/
 │    ├── <Name>.Api             # エンドポイント定義・DI 構成・ProblemDetails 変換
 │    ├── <Name>.Application     # ユースケース（Wolverine ハンドラ）・検証・マッピング
 │    ├── <Name>.Domain          # エンティティ・値オブジェクト（外部依存なし）
 │    ├── <Name>.Infrastructure  # EF Core・Redis・オブジェクトストレージ等の実装
 │    └── <Name>.Contracts       # 公開契約（proto・イベント・DTO）
 └── tests/
      ├── <Name>.UnitTests
      └── <Name>.IntegrationTests
```

`SharedKernel`（Result / Error・共通基底）は**サービス単位に置かない**。ユニット横断の共有は
[`src/README.md`](../../src/README.md) の依存規則により `src/platform/backend/Shared/` の 2 プロジェクトのみが
許可されているため、**`Platform.Shared.Kernel` として platform/backend/Shared 配下に 1 つ置く**。
計画書の構成図はサービス単位の論理レイヤを示したものであり、本リポジトリの
ユニット第一構成（[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）と両立させるための
読み替えである。**この判断は [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) で確定した**
（IADR-0056 決定 3 の部分改定。ユニット外参照の許容を 2 → 3 プロジェクトへ改定し、`Platform.Shared.Kernel` を加える）。

### 2. CPM への標準ライブラリ集約

[`src/Directory.Packages.props`](../../src/Directory.Packages.props) を棚卸し表に合わせる。**本作業では採用側の
`PackageVersion` を追加するだけで、不採用側の削除は行わない**（既存サービスがまだ参照しており、消すと
ビルドが壊れるため）。不採用側は次節の baseline で「増やさない」ことを強制し、各サービスの再実装 issue が
参照を落とし切った時点で削除する。

| 区分 | 追加する標準ライブラリ |
| --- | --- |
| Application | `WolverineFx`・`FluentValidation`・`Riok.Mapperly`・`Scrutor` |
| API | `Scalar.AspNetCore`・`Asp.Versioning.Http` |
| Infrastructure | `Microsoft.Extensions.Caching.Hybrid`・`Microsoft.Extensions.Http.Resilience`・`EFCore.NamingConventions`・`WolverineFx.RabbitMQ`・`WolverineFx.Kafka`・`Grpc.AspNetCore` 系・`Microsoft.AspNetCore.DataProtection` |
| テスト | `xunit.v3`・`AwesomeAssertions`・`NSubstitute`・`Respawn`・`Testcontainers.Redis`・`Testcontainers.Qdrant` |
| その他 | `Humanizer`・`Ardalis.GuardClauses` |

### 3. 不採用ライブラリの参照禁止 — ratchet 方式（本作業の要）

新規 `scripts/check-backend-libraries.js`（Node 標準モジュールのみ。既存
[`check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js) の作法に揃える）を追加し、
CI の独立ジョブから実行する。

**禁止対象**（棚卸し表の ★不採用 のうち、実際に混入し得るもの）:
`MediatR` / `AutoMapper` / `Mapster` / `MassTransit`（`MassTransit.RabbitMQ` 含む） / `FluentAssertions` /
`Serilog`（全パッケージ） / `Hellang.Middleware.ProblemDetails` / `OneOf` / `CSharpFunctionalExtensions` /
`Z.EntityFramework.Extensions` / `Hangfire` / `OpenIddict` / `BCrypt.Net-Next` / `DotNetEnv` /
`BouncyCastle.Cryptography` / `Kiota` / `NSwag`

**判定方式**: `.csproj` の `PackageReference` と `.cs` の `using` の両方を走査する（issue の
「PackageReference・using が入らないこと」に対応）。

**ratchet（既知の違反の扱い）**: `scripts/backend-library-baseline.json` に**現時点の違反をプロジェクト単位で
記録**する。検査は次の 3 判定を行う。

1. baseline に無いプロジェクトで違反 → **exit 1**（新規混入を止める）
2. baseline にあるプロジェクトの違反 → warn のみ（`$GITHUB_STEP_SUMMARY` に残件として出す）
3. baseline にあるのに違反が消えたプロジェクト → **exit 1**（「baseline を減らし忘れ」の検出）

3 が要点である。カバレッジ ratchet と同じく、**床は下げられるが上げっぱなしにはできない**。各サービスの
再実装 issue は移行と同時に baseline から自プロジェクトを削除することになり、残件がそのまま進捗指標になる。
baseline が空になった時点で不採用パッケージを `Directory.Packages.props` から削除できる。

### 4. Domain 層の外部依存ゼロ検査

同スクリプトで、`*.Domain.csproj` に `PackageReference` が 1 件も無いこと（`ProjectReference` は
`Platform.Shared.Kernel` のみ許可）を検査する。ADR-0030 の選定基準 3（層の依存規律）の機械化である。
現時点で `*.Domain` プロジェクトは存在しないため、本検査は**将来の新規プロジェクトに対してのみ働く**
（既知違反ゼロで開始でき、ratchet が不要）。

### 5. サービス雛形

`templates/` 配下に新標準のサービス雛形を置く。#455 本文の「新サービスが標準から始まることを保証する」に
対応する。雛形自体はビルド対象の `.slnx` に登録しない（雛形がビルドされると CPM・参照禁止検査の
二重管理になるため）。

## 受け入れ基準

- [ ] [`docs/tech/tech-requirements.md`](../tech/tech-requirements.md) が新標準（Vertical Slice / Minimal API /
      Wolverine ローカルディスパッチ / 棚卸し表）を反映し、旧記述（MassTransit・Serilog 前提）が残っていない
- [ ] `src/Directory.Packages.props` に §2 の標準ライブラリが揃っている
- [ ] `node scripts/check-backend-libraries.js` が成功する（既知違反は baseline 内に収まり、新規混入ゼロ）
- [ ] `node scripts/check-backend-libraries.js --self-test` が成功する
- [ ] baseline の増減検査（§3 の判定 1・3）が自己試験で検証されている
- [ ] `scripts/scripts.test.js`（または `scripts.repo.test.js`）に本スクリプトのテストが追加され全件成功する
- [ ] CI（`ci.yml`）に独立ジョブとして結線され、既存ジョブを壊していない
- [ ] `templates/` にサービス雛形があり、`docs/tech/` から参照できる
- [ ] 運用仕様書に年 1 回の保守状況点検（AwesomeAssertions・Wolverine）が記載されている
- [ ] `dotnet build` / `dotnet test` が両ユニットで通る（既存サービスを壊していない）
- [ ] `node scripts/check-doc-links.js` が破損リンク 0

## テスト方針

本作業の成果物は大半が「検査器」と文書であり、受け入れ基準は検査器自身のテストへ写像する。

| 受け入れ基準 | 検証手段 |
| --- | --- |
| 参照禁止の判定（新規混入・baseline 内・baseline 減らし忘れ） | `--self-test` ＋ `scripts.test.js` のケース（3 判定それぞれに正例・負例） |
| Domain 層の外部依存ゼロ | 同上（合成フィクスチャで `.Domain.csproj` に `PackageReference` を置いた負例） |
| CPM の整合 | `dotnet build` が両ユニットで成功すること（`Directory.Packages.props` の記述誤りはビルドで落ちる） |
| 既存サービスを壊していない | `dotnet test <unit>/backend/backend.slnx` が両ユニットで green |
| 文書のリンク健全性 | `node scripts/check-doc-links.js` |

**受け入れ基準 → テストの写像規約そのもの**（命名・起点 ID コメント）は #453 が定める。本作業のテストは
#453 完了後にその規約へ追随させる（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 6）。

## 計画書との差異

**差異: あり（2 件）。**

1. **`SharedKernel` の配置（構成の読み替え）**。計画書 12_backend-application-stack の構成図は `SharedKernel` を
   サービス単位に置く形で示されているが、本リポジトリはユニット第一構成
   （[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)・ADR-0019）を採り、ユニット外から
   参照できるのは `src/platform/backend/Shared/` の 2 プロジェクトのみと定めていた。サービスごとに
   `SharedKernel` を作ると Result 型が 11 個に分裂し、サービス間で型が異なるため契約に載せられない。
   **`Platform.Shared.Kernel` として 1 つに集約する**。計画の意図（Result を外部ライブラリに頼らず自前で持つ・
   Domain を外部依存ゼロにする）は満たすため、ADR に反する逸脱ではなく配置の具体化と判断する。
   → **[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) で確定済み**（IADR-0056 決定 3 を
   2 → 3 プロジェクトへ部分改定）。`/plan-feedback` で計画側へ「構成図はサービス内の論理レイヤであり
   物理配置は実装裁量」と明記するよう提案する（同 IADR フォローアップ 2）。

2. **テストフレームワークを xUnit v2 で出荷する（一時的逸脱）**。ADR-0030 §決定と棚卸し表は
   テストを **xUnit v3** と明記しているが、本作業が出荷する雛形（`templates/unit-template/`）と既存 30 の
   テストプロジェクトは **v2（`xunit 2.9.3`）** のままである。理由は `xunit.runner.visualstudio` が
   v2 用（2.x）と v3 用（3.x）で別系列であり、**CPM は 1 パッケージ 1 バージョンしか持てない**ためで、
   v3 へ移るには全テストプロジェクトが同時に移らざるを得ない。#455 の範囲（標準の確立と強制）で
   30 プロジェクトの一斉移行まで抱えると [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)
   規約 4（レビュー可能な変更単位）を破る。
   - **`/plan-feedback` は不要と判断する。** これは計画の決定そのものへの反対ではなく、**解消計画のある
     一時的逸脱**である（runner 3.x への CPM 一斉切替 issue で解消する。下記「未決事項」2）。計画書の
     記述を変える必要がないため、計画側へ返すべき情報が無い。
   - 逸脱が固定化しないよう、**`xunit.v3` を参照するプロジェクトを作ってはならない**という制約を
     `scripts/check-backend-libraries.js` の `xunitRunnerMismatch` 検査で機械的に固定した（`templates/` も検査対象）。
     CPM に `xunit.v3` の `PackageVersion` は先行して置くが、参照は runner を 3.x へ揃える切替 issue まで禁じる。

## 未決事項

1. ~~**`Platform.Shared.Kernel` の新設**（上記差異）。IADR を起こして確定する。~~
   **確定済み（2026-08-03）**: [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) が
   [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 3 を部分改定し、
   `src/platform/backend/Shared/` のユニット外参照可能プロジェクトを **2 → 3**（`Platform.Shared.Kernel` を追加）とした。
   同プロジェクトは .NET 標準以外の `PackageReference` を持たない。**実体は本作業では作成せず**、
   最初にそれを必要とするサービス再実装 issue（#438〜#451）が作成する。
2. **xUnit v2 → v3 の切替時期**（上記「計画書との差異」2 の解消計画）。v3 はプロジェクト形式（`Microsoft.NET.Test.Sdk` の扱い・`xunit.v3` パッケージ）が
   変わるため、既存 30 プロジェクトを一斉に切り替えると本作業が肥大する。**CPM に `xunit.v3` を追加するのみとし、
   実際の切替は各サービスの再実装 issue で行う**。ただし次の 2 点が未決である。
   - **`xunit.runner.visualstudio` は CPM 上 1 バージョンしか持てない**。現行 `2.8.2`（v2 用）に対し v3 は
     `3.x` を要する。最初に v3 へ移るサービスが出た時点で**全テストプロジェクトが同時に移る**か、
     あるいは当該プロジェクトだけ `VersionOverride` で凌ぐかを決める必要がある。前者なら xUnit 移行は
     独立した issue として切り出すのが妥当である（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4）。
   - `Xunit.SkippableFact`（現行 1.4.13）に v3 対応版が無い場合の代替（v3 標準の `Assert.Skip`）への
     置き換えが要る。
3. **`Refit` の扱い**。棚卸し表に記載が無い一方、本リポジトリはサービス間 HTTP に Refit を使っている
   （`Refit.HttpClientFactory` 8.0.0）。ADR-0029 は内部同期を gRPC としており、Refit は #441 の担当範囲で
   決着する。本作業では禁止対象にも標準にも入れない（現状維持）。
