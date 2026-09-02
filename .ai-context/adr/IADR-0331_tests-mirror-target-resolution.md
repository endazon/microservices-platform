---
title: IADR-0331 テストの鏡写し先は「検証する本体の要素が置かれた場所」で決め、対応物が無いものは Tests/ 直下に残す
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0065
  - ADR-0068
  - IADR-0282
  - IADR-0298
  - IADR-0319
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 1・3・4
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30) 決定 1・2・5
---

# IADR-0331: テストの鏡写し先の決め方（#1063）

- 状態: Accepted
- 日付: 2026-08-31
- 決定者: claude（実装）

## コンテキストと課題

計画 `ADR-0065` 決定 3 は `Tests/` を本体の鏡写しとし、**「`Tests/Features/` ／ `Tests/Domain/`
の形を採る」**とだけ書いている。この 2 つでは足りない。

- **本体には `Infrastructure/` と `Common/` もある**（`ADR-0065` 決定 1 の標準樹形）。
  14 サービスの実測で、`Infrastructure/` を検証するテストは **38 件**、`Common/Observability` は
  **2 件**あり、どちらも `Features/` でも `Domain/` でもない。
- **本体に対応物を持たないテストがある。** テスト専用の器（`TestWebApplicationFactory` 等）と、
  `Program.cs` の配線だけを検証するもの（`/health`・`/internal/introspection`）と、
  主題が `Platform.Shared.*` にあるものである。**「鏡写し」には写す相手が要る。**
- **1 つのテストが複数の操作を叩く。** 3 段目（操作フォルダ）へ下ろせないものが必ず出る。

`IADR-0319` が本体側で示したとおり、**この種の判定は「印象」で決めると 3 人が同じ誤りに達する。**
テスト側にも同じ危険がある —— ファイル名（`*EndpointTests`）や中身の汎用さは、
**所属についての問いを内容についての問いへ読み替えさせる。**

## 決定

**決定 1: 鏡写しの相手は「そのテストが検証する本体の要素が置かれたディレクトリ」であり、
`Features/` と `Domain/` に限らない。** `Infrastructure/<Sub>/`・`Common/<Sub>/`・`Domain/Ports/`
も鏡写す。相手のパスをそのまま `Tests/` 配下へ写す。

**決定 2: エンドポイント経由で検証するテストの段は、叩く操作を数えて決める。**

- 1 つの操作だけを叩く → **3 段目**（`Tests/Features/<集約>/<操作>/`）
- 同じ集約の 2 つ以上の操作を叩く → **2 段目**（`Tests/Features/<集約>/`）
- 集約をまたぐ → **主題が属する集約の 2 段目**

`ADR-0068` 決定 2 が本体に課した「そのファイルが 1 つの操作にしか使われないか」を、
テストへ「そのテストが 1 つの操作しか叩かないか」として適用したものである。
**「登録表は 2 段目」（同決定 1）と対称になる** —— 集約の複数操作にまたがる検証は、
集約全体の面の性質であって 1 ユースケースのものではない。

> 準備・後片付けのために叩く経路は数えない（作成してから削除を検証する、等）。
> **数えるのは検証している経路である。**

**決定 3: 型を直接呼ぶテストは、その型が定義された本体ファイルのディレクトリへ置く。**
参照数の多寡ではなく**主題**で決める —— テストは検証対象より DTO・契約型を多く参照することが
あり、参照数の最頻値は主題と一致しない（実測: `TagDictionaryTests` は `Domain` の `Tag` を 8 回
参照するが、検証しているのは `/tags` の 2 操作である）。

**決定 4: 本体に対応物が無いものは `Tests/` 直下に残す。** 3 種ある。

| 種別 | 例 | 理由 |
| --- | --- | --- |
| テスト専用の器 | `TestWebApplicationFactory` / `TestAuthHandler` / `TestDatabaseConfiguration` / `TestRabbitMqConfiguration` / `Recording*` / `TestDoubles` / `GlobalUsings.cs` / xUnit の collection 定義 | **全スライスが使う。** `ADR-0068` 決定 2 の「2 つ以上の操作が使うものは下ろさない」がそのまま当たる |
| `Program.cs` 由来 | `HealthEndpointTests` / `IntrospectionEndpointTests` | 🔴 **これも鏡写しである。** 本体でも `Program.cs` はサービス直下にあり、その鏡写しの位置は `Tests/` 直下である |
| 主題が `Platform.Shared.*` | `ConfigViewerPolicyTests` / `KeycloakRolesClaimsTransformationTests` / `PlatformAuthJwtBearerOptionsTests` / `PipelineConfigLoaderTests` | 自サービスの本体に写す相手が無い。**共有プロジェクトのテストへ移すのは移送ではなく再配置**であり、#1063 の射程外 |

**決定 5: 名前空間をフォルダへ追随させる（`<Svc>.Tests` → `<Svc>.Tests.<移送先>`）。
`using` は 1 行も足さない。**

C# は**外側の名前空間を自動で探索する**ため、`Tests/` 直下に残る器
（`<Svc>.Tests` 名前空間）は `<Svc>.Tests.Features.<集約>.<操作>` からも無修飾で見える。
雛形（`templates/unit-template`）が `SampleService.Tests.Features` を採っているのと同じ形である。

> **名前空間を据え置く案を採らない理由**: フォルダと名前空間が食い違ったまま 166 ファイルが
> 残ると、次に読む者が「どちらが正か」を判断できない。**移送の目的は経路を 1 本にすることである。**

**決定 6: `ConversionService/Tests/Golden/` は動かさない。**

`NormalizationGolden.cs` は `[CallerFilePath]` で `Cases/` と `Expected/` を解決する。
移すと**資材の解決が静かに壊れる**（テストは失敗ではなく「case 0 件」の fail-closed へ倒れる）。
`IADR-0298` 決定 3・5 が置いた構造であり、本 IADR はそれに触れない。
器の名前空間も `ConversionService.Tests` のまま据え置く —— `NormalizationServiceTests` は
`ConversionService.Tests.Features.ConversionJobs.Normalize` へ移るが、決定 5 の外側探索により
器を無修飾で参照できる。

## 理由

**決定 1〜3 は 1 つの見方から出ている** —— **鏡写しの値打ちは「本体を読む人が対応するテストを
同じ経路で辿れること」**（`ADR-0065` 決定 3 の理由）であり、辿る経路は**検証対象の置き場所**で
決まる。テストの名前でも、参照数でも、テストの技術的種別（単体 / 結合）でもない。

**決定 4 の 2 行目（`Program.cs` 由来）は例外ではない。** `Tests/` 直下に残すことを
「鏡写しから漏れた残り」と読むと、次の是正で「どこかへ入れるべきだ」という圧力が生じる。
**本体のルート直下にあるものの鏡写し先はテストのルート直下である** —— これは規則の適用結果である。

**決定 5 で `using` を足さないのは、退行の余地を減らすためである。** 166 ファイルへ機械的に
using を足すと、足し忘れ 1 件がビルドエラーになり、余分に足した 1 件が
`dotnet format --verify-no-changes` を落とす。**言語仕様が既に解決している問題を、
編集で解こうとしない。**

## 結果

- **良い影響**
  - 14 サービスすべてで `Features/<集約>/<操作>/` の経路がテスト側にも通る（3 段目 28 フォルダ）。
  - 判定が機械的に追える。`IADR-0319` が本体側で立てた「数える手続き」がテスト側にも揃う。
  - **`check-unit-dependencies.js` 規則 3 の対象外であることが構造的に保たれる** ——
    同検査は `Services/<Svc>/` 直下の最初のセグメントが層フォルダ名であるものだけを見るため、
    `Services/<Svc>/Tests/Features/...` は入らない（検査器を 1 バイトも触らずに済む）。
- **悪い影響 / トレードオフ**
  - 🔴 **決定 2 の判定は時点に依存する。** テストが 2 つ目の操作を叩き始めたら 2 段目へ戻す必要がある。
    `IADR-0319` が本体側で受け入れたのと同じ依存であり、同じ理由で受け入れる。
  - **`Tests/` 直下に 86 ファイルが残る。** 「フラットなままではないか」と見えるが、
    内訳は器 62・`Program.cs` 由来 20・`Platform.Shared.*` 4 であり、**どれも写す相手が無い。**
  - **機械検査は置かない。** 「テストの主題」を静的に判定するにはシンボル解決が要る。
    同型の事故が 2 回起きたら検査器を検討する（`CLAUDE.md`）。**本 IADR は 0 回目の記録である。**

## 関連

- 計画 ADR: `ADR-0065` 決定 1・3・4、`ADR-0068` 決定 1・2・5
- 実装 IADR: `IADR-0282`（標準樹形）、`IADR-0319`（段は数えて決める）、`IADR-0298`（ゴールデン資材）
- 作業仕様書: `.ai-context/specs/20260831_issue-1063_tests-mirror-body-structure.md`
- issue: #1063
