---
title: 作業仕様書 — 計画スタック 3 種の横展開 波 1（AiAnalysis / Conversion / Dashboard / Notification / McpServer / Authorization の 6 サービス）（#1230）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - ADR-0041
  - ADR-0065
  - ADR-0068
  - IADR-0117
  - IADR-0195
  - IADR-0229
  - IADR-0282
  - IADR-0371
  - IADR-0376
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# 作業仕様書: 計画スタック 3 種の横展開 波 1（#1230）

起点: 実装 issue #1230（親 #1064 / 環流 planning#490）。参照実装は PR #1232（`43da2a76`）と `IADR-0371`。

## 0. 前提の確認

- 基点 `origin/develop` = **`f2b82d7d`**。`git rev-parse --is-shallow-repository` = **`false`**
  （履歴の打ち切りではないので `git log` を出典に使える）。
- 着手前に `git fetch origin && git merge origin/develop` を実行し、**Already up to date**。

## 1. 母集合（issue の数えを転記せず、基点 `f2b82d7d` で自分で走査した）

走査対象は `src/platform` と `src/knowledge`（`src/ai-stock-trading` は submodule ＝ 別プロジェクトのため除外）。
サービスは **14 件**（`src/*/backend/Services/*/` の一覧で確定）。

### 1-1. 3 ライブラリの現況

| 要素 | 走査 | 実測（`f2b82d7d`） |
| --- | --- | --- |
| FluentValidation の `PackageReference` | `Include="FluentValidation"` を `*.csproj` へ | **1 件**（FeedbackService のみ） |
| Riok.Mapperly の `PackageReference` | `Include="Riok.Mapperly"` を `*.csproj` へ | **1 件**（FeedbackService のみ） |
| `Platform.Shared.Kernel` への `ProjectReference` | `Platform.Shared.Kernel.csproj` を `*.csproj` へ | **1/14 サービス**（FeedbackService。ほかは Kernel 自身のテストのみ） |
| サービス個別の `Result` 型 | `(class\|record\|struct)\s+Result(<\|\s\|$)` を非テストの `*.cs` へ | **0 件**（ヒット 2 件はいずれも Kernel 自身の `Result` / `Result<T>`） |

**陽性対照**（「1 件しか無い」を「無い」と読む前に、走査器が生きていることを確かめた）:

- `PackageReference` の走査 → `Include="WolverineFx"` が **2 件**でヒットする。
- `ProjectReference` の走査 → `*.Contracts.csproj` が **23 件**でヒットする。
- `Result` 型の走査 → Kernel の `Result` / `Result<T>` の **2 件**でヒットする（走査自体は生きている）。

→ **#1230 が前提として書いた「`Kernel` は 0/14、#1064 で 1 本目」は、基点 `f2b82d7d` でも成り立つ。残 13。**

### 1-2. 手書きの入力検証

`Results.BadRequest` を返す端点内のガード節（テストを除く）は **23 箇所 / 5 サービス ＋ BFF**。
issue #1230 の表と**件数・分布とも一致した**（GraphService 12 / DataSourceService 4 / DashboardService 3 /
AiAnalysisService 2 / ConversionService 1 / Platform.Bff 1。FeedbackService に残る 1 件は移送済みの
`Result` → 400 の写像点であってガード節ではない）。

🔴 **ただし母集合の定義が狭い。** `Results.BadRequest` だけを数えると、**RFC7807 の
`Results.ValidationProblem` で返している手書き検証を丸ごと取り逃す**。同じ走査で数え直すと:

| 返し方 | 件数（非テスト・ヘルパ定義 5 件を除く） | 分布 |
| --- | --- | --- |
| `Results.BadRequest` | 23 | GraphService 12 / DataSourceService 4 / DashboardService 3 / AiAnalysisService 2 / ConversionService 1 / Platform.Bff 1 |
| `Results.ValidationProblem`（直接） | **20** | **DocumentService 20** |
| 同（サービス内ヘルパ経由） | **14** | AuthorizationService / McpServer |

**したがって実際の手書き検証は 23 ではなく 57 箇所前後である。** この差は #1230 の受け入れ基準
「手書きのガード節が残っていない」の判定にそのまま効くので、**下の「射程外」に理由つきで残し、
追随 issue へ送る**（本波では触らない —— `ValidationProblem` は**全違反を返す** RFC7807 であり、
`Errors[0]` を採る `BadRequest` 系とは応答の形が違う。混ぜると片方の契約が壊れる）。

### 1-3. 手書きの写像（`static To*()`）

宣言は 23 本。うち **DTO ↔ ドメインの写像**に当たるのは 11 本で、残り 12 本は別種
（`ToSlug` / `ToDateTimeOffset` / `ToBody` / `ToNames` / `ToIdsAsync` / `ToPayloadKey` /
`Normalize` 系 / `ExtensionFor` / `ContentTypeFor` / `Parse`）。

| # | 写像 | 源 → 先 | 追加引数 | 本波 |
| --- | --- | --- | --- | --- |
| 1 | `NotificationStore.ToDto` | `Notification` → `NotificationDto` | なし | ✅ |
| 2 | `McpClientEndpoints.ToView` | `McpClient` → `McpClientView` | なし（列 2 本が変換を挟む） | ✅ |
| 3 | `UserAdminEndpoints.ToDto` | `IdentityUser` → `PlatformUserDto` | なし | ✅ |
| 4 | `KeycloakIdentityAdminClient.ToIdentityUser` | `KeycloakUser` ＋ roles | **あり** | ❌ 波 2 |
| 5〜8 | `DocumentService` の 4 本 | Document 系 | **あり**（names / doc / now） | ❌ 波 2 |
| 9 | `GraphService.AiSuggestionEndpoints.ToDto` | AiSuggestion ＋ 3 引数 | **あり** | ❌ 波 2 |
| 10 | `RetrievalService.InMemoryVectorStore.ToResult` | `ChunkPayload` ＋ score | **あり** | ❌ 波 2 |
| 11 | `DataSourceService.ToResponse` | `DataSource` → **匿名型** | あり | ❌ 射程外（#1230 が明記） |
| — | `AiAnalysisService.CitationMapper.ToCitations` | 列の**組み立て** | — | ❌ 射程外（下記） |

## 2. 射程（本 PR ＝ 波 1）

**13 サービスを 1 PR に入れるとレビュー不能になる**（#1230 §補足が「サービス単位で PR を刻んでよい」
と明示している）。**割る軸は「既存の設計判断を変えずに参照実装を写せるか」である。**

- **波 1（本 PR）** = 参照実装（`IADR-0371`）の形をそのまま写せる 6 サービス。
- **波 2（追随 issue）** = 写す前に**新しい判断が要る**もの —— 検証を認可より前に置く順序が
  仕様になっている（GraphService）、検証が DB / 外部名簿の状態に依存する（DataSourceService）、
  写像に追加引数が要る（DocumentService ほか）、応答が RFC7807 で形が違う（DocumentService の 20 箇所）。

### 2-1. サービスごとの「入れる／入れない」と理由

| サービス | FluentValidation | Riok.Mapperly | `Platform.Shared.Kernel` |
| --- | --- | --- | --- |
| **AiAnalysisService** | ✅ 2 規則（`Analyze`。Instruction 必須 → 上限長） | ❌ **写像が無い。** `CitationMapper.ToCitations` は 1:1 の詰め替えではなく**列の組み立て**である（1 起点の連番付与・スニペットの切り詰め・機密区分の既定値への縮退）。Mapperly は要素写像を生成する道具であり、採番と縮退を持ち込むと生成規約の外の手書きが `[Mapper]` の中へ戻る | ✅ 検証の失敗を `Error.Validation` で表し、HTTP への写像を 1 箇所に閉じる |
| **ConversionService** | ✅ 1 規則（`CorrectFigure`。`FigureMarkdown.IsEmbeddable`） | ❌ **DTO ↔ ドメインの写像が無い。** `ToBody` はタプルを返す本文抽出、`ExtensionFor` / `ContentTypeFor` は文字列の対応表 | ✅ 同上 |
| **DashboardService** | ✅ 3 規則（`RecordEvent` 1 ＋ `Report` 2） | ❌ 写像が無い（応答は集計の投影であってエンティティの詰め替えではない） | ✅ 同上 |
| **NotificationService** | ❌ **端点に `Results.BadRequest` のガード節が無い**（受け口の検証は `ValidationProblem` 1 箇所で RFC7807 の形。波 2） | ✅ `ToDto` | ❌ **失敗を `Result` で表す経路が無い。** 参照だけ足すのは `IADR-0371` 決定 4 が退けた形である |
| **McpServer** | ❌ 検証は `Problem(IReadOnlyList<string>)` で**全違反を返す** RFC7807（波 2） | ✅ `ToView` | ❌ 同上 |
| **AuthorizationService** | ❌ 検証は `ValidationProblem(List<string>)` で全違反を返す RFC7807（波 2） | ✅ `ToDto`（`ToIdentityUser` は追加引数があるため波 2） | ❌ 同上 |
| GraphService | ❌ 波 2 | ❌ 波 2 | ❌ 波 2 |
| DataSourceService | ❌ 波 2 | ❌ 射程外（匿名型） | ❌ 波 2 |
| DocumentService | ❌ 波 2（20 箇所・RFC7807） | ❌ 波 2 | ❌ 波 2 |
| RetrievalService | ❌ 端点の入力検証が無い | ❌ 波 2 | ❌ 波 2 |
| IngestionService / WikiService / LlmGateway | ❌ 端点の入力検証が無い | ❌ 写像が無い | ❌ `Result` を使う経路が無い |
| Platform.Bff | ❌ **射程外。** `/bff/auth/logout` の `Results.BadRequest` は**セッションの `sid` との一致検査**であり、要求 DTO の入力検証ではない（本文も返さない）。`AbstractValidator` の器に入らない | ❌ 写像なし | ❌ 同上 |

🔴 **「3 種すべてを全サービスへ」ではない。** 計画は「その関心を実装する箇所では標準ライブラリを
使うこと」を義務づけており（`IADR-0371` 決定 1 が `ADR-0030` / `ADR-0041` を逐語で確かめた結論）、
**関心の無いサービスへ空の参照を足すことは求めていない。** 上表の ❌ はすべてこの基準の適用結果である。

### 2-2. 波 2 へ送るもの（追随 issue **#1248** を起票した。`Refs #1230`）

1. **GraphService の 12 箇所。** うち純粋な入力検証は 9 箇所（`CreateEdge` 2 / `Neighbors` 2 /
   `EdgeTypes.Create` 2 / `EdgeTypes.Rename` 1 / `AiSuggestions.List` 2）で、残り 3 箇所は
   DB 参照・後段の結果（`unknown_edge_type` / `unknown_tag`）であって入力検証ではない。
   🔴 **`Neighbors` と `AiSuggestions.List` は検証を認可より前に置くことが仕様である**
   （後ろへ動かすと文書の存在が漏れる）。かつ**検証対象がクエリ引数**なので、`AbstractValidator`
   に載せるには要求モデルを起こす判断が要る。
2. **DataSourceService の 4 箇所。** `ConnectionUriPolicy.Validate` は**既存値**を、
   `OwnerMappingValidation.ValidateAsync` は**外部の利用者名簿**を見る。検証器へ持ち込むと
   Infrastructure 依存が `Features/` の検証器へ入るので、置き場の判断が先に要る。
3. **`ValidationProblem` 系 34 箇所**（DocumentService 20 ＋ AuthorizationService / McpServer 14）。
   **全違反を返す**契約であり、`Errors[0]` を採る形とは応答が違う。
4. **追加引数のある写像 7 本**（DocumentService 4 / GraphService 1 / RetrievalService 1 /
   AuthorizationService 1）。Mapperly の複数源引数の扱いを決める必要がある。
5. `LlmGateway` の `IValidateOptions` 手書き 2 本（#1230 が既に射程外と宣言）。
6. `Error` → ProblemDetails の共通変換（#1230 が既に射程外と宣言。応答本文の変更を伴う）。

## 3. 実装方針（参照実装をそのまま写す）

`IADR-0371` の決定 2・3・4 を、独自の設計を足さずに写す。

- **検証**: `AbstractValidator<T>` を `Features/<集約>/<操作>/` に置く。**規則の宣言順を移送前の
  ガード節の順に揃え**、端点は `Errors[0].ErrorMessage` を本文へ載せる（**宣言順が応答の契約**）。
  メッセージは `internal` 定数として検証器が持ち、試験は定数と**リテラルの両方**へ当てる。
- **登録**: `AddValidatorsFromAssembly` を**使わない**。`Program.cs` に 1 検証器 1 行の明示登録。
- **写像**: `[Mapper]` の `internal static partial` クラス。**置き場は手書きだった頃と変えない**
  （複数操作が使うものは 2 段目のまま。`ADR-0068` 決定 2）。
- **`Result`**: 失敗経路を `Result` / `Result<T>` で束ね、`ErrorKind` → HTTP の分岐を端点に 1 箇所だけ置く。
  ヘルパは端点ファイル内の `private static` に留める（新しい層・プロジェクトを作らない）。
- **`.csproj`**: `PackageReference` にバージョンを書かない（CPM）。検証器・マッパは `internal` なので
  `InternalsVisibleTo` を試験プロジェクトへ開ける（既存の形に揃える）。

## 4. コミットの割り方

**1 サービス 1 コミット**（レビュー可能な粒度）。6 コミット ＋ 記録 1 コミット。

## 5. 受け入れ基準（本 PR の射程）

- [x] 波 1 の 6 サービスが、上表の ✅ どおりに 3 ライブラリを参照する（❌ には理由が仕様書と ADR にある）
- [x] 移送した 6 箇所の検証が**同じ状態コード・同じ本文**を返す（規則の宣言順を試験で固定）
- [x] 手書き写像 3 本が生成マッパに置き換わり、**全列の値が保たれる**
- [x] `.csproj` にバージョンを書いていない（CPM）
- [x] 既存テスト件数が減っていない（純粋な移送）
- [x] 変異試験（検証を外す／写像の列を取り違える）が**赤になることを実測**した
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が両ユニットで通る
- [x] `check-backend-libraries` / `check-cpm-versions` / `check-unit-dependencies` /
      `check-coverage-floor` / 文書検査器 / `scripts.test.js` が緑
- [x] 波 2 を追随 issue **#1248** として起票し `Refs #1230` にした（重複検索済み。同主題の open issue は #1064 / #1230 の 2 件のみだった）

## 6. 実測（着地後に引き直した）

| 走査 | 着手前（`f2b82d7d`） | 着地後 |
| --- | --- | --- |
| FluentValidation の `PackageReference` | 1 | **4** |
| Riok.Mapperly の `PackageReference` | 1 | **4** |
| `Platform.Shared.Kernel` を参照するサービス | 1/14 | **4/14** |
| `Results.BadRequest` のガード節 | 23 | **17**（`Results.BadRequest` の総数 22 のうち 5 件は `Result` → HTTP の写像点） |
| DTO ↔ ドメインの手書き写像 | 11 本 | **8 本** |

| テスト | 着手前 | 着地後 |
| --- | --- | --- |
| AiAnalysisService | 98 | 110 |
| ConversionService | 142 | 156（skip 6 は既存の環境依存） |
| DashboardService | 57 | 72 |
| NotificationService | 53 | 57 |
| McpServer | 106 | 115 |
| AuthorizationService | 149 | 153 |

**減った試験は 0 件である。**

### 変異試験（6 本とも実際に走らせた）

| 変異 | 結果 |
| --- | --- |
| AiAnalysisService: `Validate(...)` → `Result.Success()` | **失敗 4 / 110** |
| ConversionService: 同上 | **失敗 5 / 27**（`CorrectFigure` 系に絞った実行） |
| DashboardService: 両端点で同上 | **失敗 4 / 72** |
| NotificationService: `[MapProperty]` で `ThresholdPercent` を `Count` へ取り違え | **失敗 1 / 4**（マッパ系に絞った実行） |
| McpServer: `EgressTier` の変換を `Use = KindName` へ取り違え | **失敗 5 / 115** |
| AuthorizationService: `[MapProperty]` で `Username` を `DisplayName` へ取り違え | **失敗 1 / 153** |

いずれも復旧後は緑である。
