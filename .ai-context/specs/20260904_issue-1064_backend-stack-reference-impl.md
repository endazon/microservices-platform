---
title: 作業仕様書 — 計画スタック（FluentValidation / Riok.Mapperly / Platform.Shared.Kernel）の参照実装を FeedbackService に置く（#1064）
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
  - IADR-0196
  - IADR-0229
  - IADR-0282
  - IADR-0371
author: claude
created: 2026-09-04
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 1・2・3・4
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# 作業仕様書: 計画スタックの参照実装（#1064）

起点: 実装 issue #1064（環流 planning#490 / 計画 `ADR-0065` §結果 のフォローアップ 6）。

## 0. 前提の確認

- 基点 `origin/develop` = **`888e307d`**。`git rev-parse --is-shallow-repository` = **`false`**
  （履歴の打ち切りではないので `git log` を出典に使える）。
- issue #1064 の「補足・制約」が着手条件として挙げる #1061 / #1062 / #1063 は **3 件とも closed**
  （`gh api repos/:owner/:repo/issues/<n> --jq .state` で実測）。着手条件は満たされている。

## 1. 母集合（着手時に自分で引き直した。issue の数えは転記していない）

issue 本文の表は **2026-08-30 の `0784dd2` 時点**の実測である。**転記せず、基点 `888e307d` で引き直した。**
走査対象は `src/platform` と `src/knowledge`（`src/ai-stock-trading` は submodule ＝ 別プロジェクトのため除外。
`scripts/lib/excluded-units.js` と同じ切り分け）。

### 軸 1 — `.csproj` の `PackageReference`

```console
$ grep -rl 'Include="FluentValidation' --include=*.csproj src/platform src/knowledge | wc -l
0
$ grep -rn 'Riok.Mapperly' --include=*.csproj src/platform src/knowledge | wc -l
0
```

**陽性対照**（同じ走査器が `PackageReference` を拾えることの確認）:

```console
$ grep -rl 'Include="WolverineFx"' --include=*.csproj src/platform src/knowledge | wc -l
2
```

→ 走査器は生きている。**FluentValidation 0 件・Riok.Mapperly 0 件は「無い」で正しい。**

### 軸 2 — `.cs` の `using`

```console
$ grep -rn 'using FluentValidation' --include=*.cs src/platform src/knowledge | wc -l
0
$ grep -rn 'Riok.Mapperly\|\[Mapper\]' --include=*.cs src/platform src/knowledge | wc -l
0
```

### 軸 3 — `Platform.Shared.Kernel` への `ProjectReference`

🔴 **issue の数え（4/14）は現時点では古い。実測は 0/14 である。**

```console
$ grep -rln 'Platform.Shared.Kernel.csproj' --include=*.csproj src/platform src/knowledge
src/platform/backend/Shared/Platform.Shared.Kernel.Tests/Platform.Shared.Kernel.Tests.csproj
```

14 サービス（platform 4 / knowledge 10）を 1 つずつ当たっても **参照は 0 件**であった。
issue が挙げた 4 サービス（`FeedbackService` / `RetrievalService` / `DocumentService` / `GraphService`）
には **`ProjectReference` ではなくコメントだけが残っている**（「Result / Error・DDD 基底型は
Platform.Shared.Kernel を使う」）。単一プロジェクト＋VSA への移送（#1061〜#1063 / `IADR-0282`）で
層プロジェクトが撤去された際に、参照そのものが落ちたものである。

**陽性対照**（同じ走査器が `ProjectReference` を拾えることの確認）:

```console
$ grep -rln 'Platform.Shared.Contracts.csproj' --include=*.csproj src/platform src/knowledge | wc -l
18
```

### 軸 4 — 暫定手段（手書き）の現在量

| 暫定手段 | 実測（`888e307d`） | 計画の記述（2026-08-30） |
| --- | --- | --- |
| `IValidateOptions` の手書き実装 | **2 本**（`LlmGateway` の `ModelPricingOptionsValidator` / `EmbeddingRoutingOptionsValidator`） | 3 本 |
| エンドポイント内のガード節（`Results.BadRequest`） | **26 箇所** / 6 サービス ＋ BFF | （数えなし） |
| 手書きの `To*` 写像（`static` 宣言） | **19 本**（うち DTO ↔ ドメインの写像は 11 本前後。残りは `ToSlug` / `ToDateTimeOffset` 等の別種） | 16 本 |
| サービス個別に定義された `Result` 型 | **0 件**（`record/class/struct Result` の全走査。Kernel 由来のみ） | 0 件 |

**ガード節のサービス別内訳**: GraphService 12 / DataSourceService 4 / DashboardService 3 /
**FeedbackService 3** / AiAnalysisService 2 / ConversionService 1 / Platform.Bff 1。

### 軸 5 — `src/Directory.Packages.props`

**FluentValidation `12.1.1` と Riok.Mapperly `4.3.1` は既に中央宣言済み**（`#455` の ADR-0030 ブロック）。
issue の受け入れ基準「バージョンが中央管理されている」は**着手時点で既に満たされている**。
本作業で足すのは `.csproj` 側の `PackageReference`（バージョンなし）だけである。

## 2. 計画が義務づけているもの（逐語で確かめた）

**「全サービスに入れよ」という条文は無い。** 逐語は次のとおり。

- `ADR-0030` §決定: 「主要決定: マッピング = Riok.Mapperly、検証 = FluentValidation、
  Result = SharedKernel 自前実装（ProblemDetails 変換は API 層）」——
  **用途ごとの標準（何を使うか）であって、適用サービス数の義務ではない。**
- `12_backend-application-stack` §Application 層の表: FluentValidation「採用」・Riok.Mapperly「★採用」。
  **同じく「採否」の欄であり、網羅の指定ではない。**
- `ADR-0041` 決定 2: 「`Domain` / `Application` / `Api` / `Infrastructure` は `SharedKernel` が
  公開する型のみを参照し、外部ライブラリの型・名前空間を直接参照してはならない」——
  **`Result` を使うなら Kernel 由来であれ、という制約**であって「全サービスが `Result` を使え」ではない。
- `12_backend-application-stack` §実装状況 の「配備までの暫定手段」: 「未参照の 10 サービスは
  **同型を使わず、例外と戻り値で表している**」と書き、**型の分裂は起きていない**と評価している。

→ **義務は「その関心を実装する箇所では標準ライブラリを使うこと」である。**
関心の無いサービスへ空の参照を足すことを計画は求めていない。

## 3. 射程の決定（本 PR で何をやり、何をやらないか）

**本 PR は `FeedbackService` を 3 ライブラリすべての参照実装にする。残り 13 サービスへの展開は別 issue へ切り出す。**

### 根拠

1. §2 のとおり「全サービス必須」と読める条文は無い。**1 ライブラリ 1 PR に割る前提が成立しない。**
2. 14 サービス × 3 ライブラリを一度に入れると、`Results.BadRequest` 26 箇所・写像 19 本・
   `.csproj` 14 個へ触る巨大 PR になり、**レビュー単位を維持する**という CLAUDE.md の目的に反する。
3. `FeedbackService` は **3 つの関心をすべて 1 サービス内に持つ**（ガード節 3 本・1:1 の
   `ToDto` 1 本・400 と 401 という 2 つの失敗経路）。**3 ライブラリの噛み合い方まで 1 スライスで示せる**
   ため、展開時に写せる型が 1 本で済む。
4. issue 自身が「**サービス単位・関心単位で PR を刻んでよい。刻む場合は本 issue を親として追跡する**」
   と明示している。

### やらないこと（別 issue）

- 残り 13 サービスへの展開（ガード節 23 箇所 / 写像 10 本前後）。
- `LlmGateway` の `IValidateOptions` 手書き 2 本の FluentValidation 化
  （**設定値の検証**であり、端点の入力検証とは器が違う。`ValidateOptions` の合流点を先に決める必要がある）。
- `Error` → ProblemDetails の共通変換ヘルパの新設。**本 PR は振る舞いを変えない**制約を持ち、
  既存端点の応答本文は `new { error = ... }` である。共通化は応答本文の変更を伴うため別件。

## 4. 実装（振る舞いを変えない）

### 4.1 FluentValidation

`Features/Feedback/Submit/SubmitFeedbackValidator.cs` を新設し、`Submit/Endpoint.cs` の
ガード節 3 本を `AbstractValidator<FeedbackRequest>` へ写す。

🔴 **同じ 400・同じ本文を返すこと。** 具体的には:

- 規則の宣言順を元のガード節の順（`AnswerId` → `Rating` → `Comment`）に揃え、**最初の失敗**の
  `ErrorMessage` を本文に載せる。FluentValidation は既定で全規則を走らせるが、
  `Errors[0]` を採ることで元の「最初の違反で返す」と同じ文字列になる。
- メッセージ文字列は元のリテラルをそのまま定数として持ち上げる
  （`answerId is required` / `rating must be 'up' or 'down'` / `comment must be N characters or fewer`）。

登録は `Program.cs` で `IValidator<FeedbackRequest>` として行い、端点はハンドラ引数で受ける
（FluentValidation 自身のインタフェース。**独自の抽象を足さない**）。

### 4.2 Riok.Mapperly

`Features/Feedback/FeedbackMapper.cs` を新設し、`FeedbackEndpoints.ToDto`（手書き 1 本）を
生成マッパへ置き換える。`AnswerFeedback` → `FeedbackDto` は 8 プロパティすべて同名の 1:1 であり、
Mapperly の既定規約で写る。

- **置き場は 2 段目**（`Features/Feedback/`）。投稿と一覧の **2 操作が使う**ためであり、
  `ADR-0068` 決定 2 の基準（1 操作にしか使われないものだけ 3 段目）に一致する。
- 生成物は `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（`IADR-0195` 決定 1）。
  **床は動かない。**

### 4.3 `Platform.Shared.Kernel`

`Submit/Endpoint.cs` の 2 つの失敗経路（入力不正 → 400 / 未認証 → 401）を、
Kernel の `Result` / `Result<T>` / `Error` / `ErrorKind` で 1 つの値に束ね、
**API 層で 1 度だけ HTTP へ写像する**（`ADR-0030` §決定 の「ProblemDetails 変換は API 層」と
`ADR-0041` §結果 の「エラー表現を `Domain` から `Api` まで一貫させられる」の形）。

```text
Validate(req)                 : Result          … 入力不正なら Error.Validation
  .Bind(() => Identify(http)) : Result<string>  … 未認証なら Error.Unauthorized
→ 失敗なら ErrorKind で 400 / 401 を分け、成功なら Value が userId
```

- **判定の順序は元のまま**（検証 → 利用者特定）。ステータスも本文も変わらない。
- ヘルパは端点ファイル内の `private static` 2 本に留める。**新しい層・新しいプロジェクトを作らない。**

### 4.4 参照の追加

`FeedbackService.csproj` に次を足す（バージョンは書かない ＝ CPM）。

- `<PackageReference Include="FluentValidation" />`
- `<PackageReference Include="Riok.Mapperly" />`
- `<ProjectReference ... Platform.Shared.Kernel.csproj />`

`check-unit-dependencies.js` 規則 1 が knowledge → `platform/backend/Shared/` の 3 プロジェクトを
許可している（`IADR-0117`）ため、ユニット外参照として適法である。

あわせて `<InternalsVisibleTo Include="FeedbackService.Tests" />` を足す。検証器と生成マッパは
スライスの内側であり `internal` であるため、**単体で直に試験するには試験プロジェクトへ開ける必要がある**。
`FeedbackService` はこれを持っていなかったが、**他 9 サービス（`DocumentService` / `GraphService` 等）は
既に同じ形を持っている**（実測）。新しい規約ではなく、欠けていた 1 件を揃える変更である。

## 5. テスト

新規は `Tests/Features/<集約>/<操作>/` の鏡写し経路に置き、`[Trait("TestKind", "Unit")]` を付ける（#1145）。

| ファイル | 内容 |
| --- | --- |
| `Tests/Features/Feedback/Submit/SubmitFeedbackValidatorTests.cs` | 3 規則の**陽性・陰性の対**（有効な要求は通る／各違反は落ちる）＋ 最初の失敗のメッセージが元のリテラルと一致すること |
| `Tests/Features/Feedback/FeedbackMapperTests.cs` | 生成マッパの**陽性**（全 8 プロパティが写る）と**陰性**（`Comment` / `Question` の null が null のまま写る） |

**変異試験**: `Submit/Endpoint.cs` から検証呼び出しを外して既存の結合テスト
（`FeedbackEndpointTests` T-04 / T-05 / T-06）が赤になることを実際に走らせて確かめる。
証跡（コマンドと出力）は §7 に貼る。

**既存テストの件数は不変**であること（新規追加のみ）。

## 6. 受け入れ基準（本 PR の射程）

- [x] `FeedbackService` が FluentValidation・Riok.Mapperly・`Platform.Shared.Kernel` の 3 つを参照する
- [x] `Submit` の手書きガード節 3 本が `AbstractValidator` に写り、**同じ 400・同じ本文**を返す
- [x] `FeedbackEndpoints.ToDto`（手書き写像）が消え、生成マッパに置き換わっている
- [x] `Submit` の失敗経路が Kernel の `Result` / `Error` を経由し、401 / 400 の分岐が `ErrorKind` で決まる
- [x] `.csproj` にバージョンを書いていない（CPM）
- [x] `dotnet build` / `dotnet test` が両ユニットで成功し、**既存テスト件数が減っていない**
- [x] `check-backend-libraries.js` / `check-cpm-versions.js` / `check-unit-dependencies.js` /
      `check-coverage-floor.js` / `check-doc-*.js` が緑
- [x] 変異試験が赤になることを実測した証跡がある
- [x] 残射程が別 issue として起票され、本仕様書と PR から参照されている

## 7. 検証の証跡

### 7.1 ビルド・テスト（両ユニット）

```console
$ dotnet build src/knowledge/backend/backend.slnx    → ビルドに成功しました（0 エラー / 3 警告※）
$ dotnet build src/platform/backend/backend.slnx     → ビルドに成功しました（0 エラー / 0 警告）
```

※ 3 警告は `Knowledge.IntegrationTests` の `MinioBuilder()` 廃止予定（CS0618）で、**本作業と無関係の既存分**である。

**platform 側は最初 `error CS0246: AiStockTrading`（`Platform.Bff` の合成点）で落ちた。**
原因は**本 worktree に submodule が checkout されていなかった**ことで、本作業とは無関係である
（`git submodule update --init src/ai-stock-trading` の後は 0 エラー）。**環境の欠落を実装の赤と読み違えない。**

```console
$ dotnet test src/knowledge/backend/backend.slnx
  … 12 アセンブリすべて成功。FeedbackService.Tests: 合格 38 / 失敗 0
$ dotnet test src/platform/backend/backend.slnx
  … 7 アセンブリすべて成功（Platform.Shared.Kernel.Tests 42 ほか）
$ dotnet format src/knowledge/backend/backend.slnx --verify-no-changes   → EXIT=0
$ dotnet format src/platform/backend/backend.slnx  --verify-no-changes   → EXIT=0
```

**既存テストの件数は不変である。** `FeedbackService.Tests` は着手前 **21**、実装後 **38**
（＝ 21 ＋ 新規 17）。**減った試験は 0 件**である。

### 7.2 検査器

```console
$ node scripts/check-backend-libraries.js   → OK: 新規混入 0 件 / Domain 依存規律 OK（既知残件 9 件は baseline 済み）
$ node scripts/check-cpm-versions.js        → OK: 41 プロジェクト / 245 件にバージョン直書き 0 件
$ node scripts/check-unit-dependencies.js   → OK: csproj 39 件 / .cs 1013 件、ユニット依存方向の違反なし
$ node scripts/check-coverage-floor.js --report-only
  → レポート 19 件: line 91.64%（16062/17527） / branch 76.59%（3416/4460）。床 line 90 / branch 75。OK
$ node scripts/check-doc-links.js / check-doc-status-vocabulary.js / check-doc-type-vocabulary.js
  / check-doc-updated.js / check-trace-blocks.js / check-trace-followthrough.js
  / check-plan-id-qualification.js          → いずれも EXIT=0
$ node scripts/gen-knowledge-graph.js --check → OK: in-repo エッジ先の実在に違反はありません
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js → ✓ 705 tests passed
```

🔴 **`scripts.test.js` を通すために 2 つの環境要因を先に潰した。両方とも「実装は正しいのに赤」である。**

1. **`check-coverage-floor` の素実行試験が落ちた。** 同試験は「作業ツリーにレポートが 1 件だけ」を
   前提にフィクスチャを置くため、**自分の `--collect:"XPlat Code Coverage"` が残した 19 件の
   `src/**/TestResults/`** がそのまま偽の赤になる。撤去してから走らせた。
2. **`check-adr-numbering` が `IADR-0369` / `IADR-0370` の欠番で落ちた（当時）。** 並行セッションが
   確保していた番号である。`scripts.test.js` はここで**中断する**ため、その後ろの試験が走らなかった。
   **一時的に `0369` へ改番して全量を走らせ（705 件緑）、その後 `0371` へ戻した。**

   ［2026-09-05 追記 / #1064］**欠番は解消した。** PR を出した後に `origin/develop` を取り込んだところ、
   `IADR-0369`（#1227）と `IADR-0370`（#1228）が着地しており、**`0371` がそのまま連番の次になった**。
   取り込み後の実測は `check-adr-numbering` **EXIT=0**（重複・欠番なし、索引とも双方向で一致し昇順）である。
   衝突したのは `.ai-context/adr/README.md` の**索引末尾 1 箇所だけ**で、双方とも append であったため
   develop 側（0369 / 0370）→ 本ブランチ側（0371）の順に並べて昇順を保った。

### 7.3 変異試験（2 本とも実際に走らせた）

**変異 1: 検証を外す。** `Submit/Endpoint.cs` の `Validate(validator, req)` を `Result.Success()` へ差し替える。

```console
$ dotnet test .../FeedbackService.Tests.csproj
失敗!   -失敗:     7、合格:    31、合計:    38
  失敗 FeedbackEndpointTests.EmptyAnswerId_Returns400
  失敗 FeedbackEndpointTests.InvalidRating_Returns400
  失敗 FeedbackEndpointTests.TooLongComment_Returns400
  失敗 SubmitFeedbackResponseContractTests.EmptyAnswerId_Returns400WithOriginalBody
  失敗 SubmitFeedbackResponseContractTests.InvalidRating_Returns400WithOriginalBody
  失敗 SubmitFeedbackResponseContractTests.TooLongComment_Returns400WithOriginalBody
  失敗 SubmitFeedbackResponseContractTests.MultipleViolations_ReturnsFirstRuleBody
```

→ **既存 3 本と新規 4 本の計 7 本が捕まえる。** 復旧後は 38/38 緑。

**変異 2: 写像から 1 列を落とす。** `[MapperIgnoreSource(nameof(AnswerFeedback.Comment))]` を足す。

```console
$ dotnet test .../FeedbackService.Tests.csproj
obj/Debug/net10.0/Riok.Mapperly/Riok.Mapperly.MapperGenerator/FeedbackMapper.g.cs(10,30):
  error CS7036: 'Id' の必要なパラメーター 'FeedbackDto.FeedbackDto(...)' に対応する特定の引数がありません
```

→ **試験ではなくコンパイラが止める。** `FeedbackDto` は位置指定レコードであり、
**列を黙って落とすことができない**（この形では「静かな取りこぼし」が起こり得ない）。
なお**この出力は、生成物が `obj/` 配下に出ることの実測でもある**（`IADR-0195` のカバレッジ除外に入る）。

**変異 3: 写像で列を取り違える。** `[MapProperty(Question → Comment)]` を足す（こちらは compile が通る）。

```console
失敗!   -失敗:     4、合格:    34、合計:    38
  失敗 FeedbackMapperTests.ToDto_CopiesEveryProperty
  失敗 FeedbackMapperTests.ToDto_ReflectsUpdatedState
  失敗 FeedbackEndpointTests.PostDownWithComment_Persists
  失敗 FeedbackEndpointTests.SameUserSameAnswer_Upserts
```

→ **新規 2 本と既存 2 本が捕まえる。** 復旧後は 38/38 緑。

## 8. 積み残し

- **残り 13 サービスへの展開は #1230 が持つ**（検証 23 箇所 / 5 サービス ＋ BFF、写像 10 本前後）。
  **#1064 は閉じない**（親として追跡する）。
- **`LlmGateway` の `IValidateOptions` 手書き 2 本**は #1230 でも射程外とした。**設定値の検証**であり
  端点の入力検証とは器が違うため、`ValidateOptions` との合流点を先に決める必要がある。
- **`Error` → ProblemDetails の共通変換**は応答本文の変更を伴うため、計画側の確認が要る。
- **`DataSourceService.ToResponse` は匿名型を返すため Mapperly の対象外**である。先に DTO を起こすか、
  射程外として理由を残すかを #1230 で決める。
- ~~`check-adr-numbering.js` は `IADR-0369` / `IADR-0370` の欠番で赤のままである。~~
  **［2026-09-05 追記 / #1064］解消した。** `origin/develop` の取り込みで両番号が着地し、
  `IADR-0371` は改番せずそのまま連番の次になった（§7.2 の 2 を参照）。
