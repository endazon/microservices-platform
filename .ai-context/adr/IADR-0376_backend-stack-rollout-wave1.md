---
title: IADR-0376 計画スタック 3 種の横展開は「参照実装をそのまま写せるか」で波を割り、波 1 は 6 サービスに限る（何を入れ、何を入れなかったか）
type: impl-adr
status: Accepted
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
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# IADR-0376: 計画スタック 3 種の横展開（波 1）で、どのサービスに何を入れ、何を入れなかったか（#1230）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0030`（ライブラリ標準）／`ADR-0041`（Result 型）／`ADR-0068` 決定 2
  （スライス 3 段目の切り出し基準）／`NFR`（無採番。ライブラリ標準の浸透は保守性の非機能要件であり、
  計画側の要求表に当たる番号が無い）
- 関連する実装 ADR: `IADR-0371`（参照実装の置き方。本 ADR はその適用記録である）／
  `IADR-0117`（`Platform.Shared.Kernel` の配置とユニット外参照 3 プロジェクト）／
  `IADR-0229`（Kernel が公開する `Result` / `Error` の操作面）／
  `IADR-0195`（source generator の出力をカバレッジ集計から落とす）／`IADR-0282`（単一プロジェクト＋VSA）
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1230_backend-stack-rollout-wave1.md`
- issue: #1230（親 #1064 / 環流 planning#490）。波 2 は #1248

## コンテキストと課題

`IADR-0371` が `FeedbackService` を参照実装として立て、残り 13 サービスへの展開を #1230 に切り出した。

**本 ADR が答えるのは「なぜこの形か」ではない**（それは `IADR-0371` が持つ）。
答えるのは **「どのサービスに何を入れ、何を入れなかったか。その基準は何か」** である。

着手時に基点 `f2b82d7d` で母集合を引き直した結果、**#1230 の数え方そのものに 1 点の見落としがあった。**

| 走査 | 実測 | 備考 |
| --- | --- | --- |
| `Platform.Shared.Kernel` への `ProjectReference` | **1/14 サービス** | #1230 の前提どおり（`FeedbackService` のみ）。残 13 |
| サービス個別の `Result` 型 | **0 件** | ヒット 2 件はいずれも Kernel 自身の `Result` / `Result<T>` |
| `Results.BadRequest` のガード節 | **23 箇所** | #1230 の表と件数・分布とも一致 |
| 🔴 `Results.ValidationProblem` の手書き検証 | **34 箇所** | **#1230 の母集合に入っていない**（DocumentService 20 ＋ AuthorizationService / McpServer のヘルパ経由 14） |

**陽性対照**（「1 件しか無い」を「無い」と読む前に走査器が生きていることを確かめた）: `PackageReference`
の走査は `WolverineFx` を 2 件、`ProjectReference` の走査は `*.Contracts.csproj` を 23 件、
`Result` 型の走査は Kernel の 2 件でヒットする。

→ **実際の手書き検証は 23 ではなく 57 箇所前後である。** #1230 の受け入れ基準「手書きのガード節が
残っていない」は、`Results.BadRequest` だけを見ていると**満たしたように見えて満たしていない**。

## 検討した選択肢

### 波の割り方

1. **残り 13 サービスを 1 PR で。** #1230 の受け入れ基準を 1 回で満たすが、`.csproj` 13 個・
   検証 23 箇所・写像 10 本へ同時に触る。レビュー単位が壊れる。
2. **サービスの規模順（小さい順）に割る。** 機械的だが、割れ目に意味が無い ——
   「なぜここで切ったか」がレビュアーにも次の担当にも読めない。
3. **「参照実装をそのまま写せるか」で割る。**

## 決定

### 決定 1: 波は「既存の設計判断を変えずに `IADR-0371` を写せるか」で割る（選択肢 3）

**波 1（本 PR）** = 参照実装の形をそのまま写せる **6 サービス**。
**波 2（追随 issue）** = 写す前に**新しい判断が要る**もの。

**この軸を採ったのは、割れ目そのものが次の担当への引き継ぎになるからである。**
「小さい順」で割ると、波 2 の担当は「なぜ残ったか」を自分で調べ直すことになる。
本 ADR の下表がそのまま波 2 の作業指示になる。

### 決定 2: 波 1 の 6 サービスに入れたもの・入れなかったもの

| サービス | FluentValidation | Riok.Mapperly | `Platform.Shared.Kernel` |
| --- | --- | --- | --- |
| **AiAnalysisService** | ✅ 2 規則（`Analyze`） | ❌ 下記 A | ✅ 検証の失敗を `Error.Validation` で表す |
| **ConversionService** | ✅ 1 規則（`CorrectFigure`） | ❌ 下記 B | ✅ 同上 |
| **DashboardService** | ✅ 3 規則（`RecordEvent` 1 ＋ `Report` 2） | ❌ 下記 B | ✅ 同上 |
| **NotificationService** | ❌ 下記 C | ✅ `ToDto` | ❌ 下記 D |
| **McpServer** | ❌ 下記 C | ✅ `ToView` | ❌ 下記 D |
| **AuthorizationService** | ❌ 下記 C | ✅ `ToDto`（`ToIdentityUser` は波 2） | ❌ 下記 D |

**入れなかった理由（4 種類しか無い）:**

- **A: 写像に見えるが写像ではない。** `AiAnalysisService.CitationMapper.ToCitations` は
  1:1 の詰め替えではなく**列の組み立て**である（1 起点の連番付与・スニペットの切り詰め・
  機密区分の既定値への縮退）。Mapperly は要素写像を生成する道具であり、採番と縮退を持ち込むと
  **生成規約の外の手書きが `[Mapper]` の中へ戻る**。
- **B: DTO ↔ ドメインの写像が存在しない。** `ConversionService.ToBody` はタプルを返す本文抽出、
  `ExtensionFor` / `ContentTypeFor` は文字列の対応表。`DashboardService` の応答は集計の投影である。
- **C: 検証はあるが器が違う。** `Results.ValidationProblem`（RFC7807）は**全違反を返す**契約であり、
  `Errors[0]` を採る `Results.BadRequest` 系とは応答の形が違う。**混ぜると片方の契約が壊れる。**
- **D: 失敗を `Result` で表す経路が無い。** 参照だけを足すのは `IADR-0371` 決定 4 が
  「適合しているように見えるだけ」として退けた形である。

**残り 8 サービス**（GraphService / DataSourceService / DocumentService / RetrievalService /
IngestionService / WikiService / LlmGateway / Platform.Bff）はいずれも波 2 か射程外である（決定 4）。

### 決定 3: 移送の等価性は「状態コード ＋ 本文 ＋ 規則の宣言順」で固定する

`IADR-0371` 決定 2 をそのまま適用する。波 1 で移した 6 箇所すべてについて:

- **規則の宣言順を移送前のガード節の順に揃える**（端点は `Errors[0].ErrorMessage` を本文へ載せる）。
- **メッセージは `internal` 定数として検証器が持ち、試験は定数とリテラルの両方へ当てる。**
- **登録は 1 検証器 1 行の明示登録**（`AddValidatorsFromAssembly` を使わない）。

🔴 **述語も元のまま写す。** `AnalyzeRequestValidator` の必須判定を `NotEmpty()` へ置き換えなかったのは、
移送前が `string.IsNullOrWhiteSpace` であり、**`NotEmpty()` の空判定と一致するかがライブラリの版に
依存する**ためである。移送で確かめるべきは等価性なので、確かめられない置き換えを混ぜない。

**既存の 400 の試験は状態コードしか見ていないものが多い**（DashboardService の 3 本、
ConversionService の 1 本）。**400 のままメッセージだけ変わる退行はそこでは捕まらない**ので、
本文の `error` 文字列まで見るよう拡張した。

### 決定 4: 波 2 へ送るものを、理由つきで列挙する

1. **GraphService の 12 箇所。** 純粋な入力検証は 9 箇所で、残り 3 箇所は DB 参照・後段の結果
   （`unknown_edge_type` / `unknown_tag`）である。🔴 **`Neighbors` と `AiSuggestions.List` は
   検証を認可より前に置くことが仕様である**（後ろへ動かすと文書の存在が漏れる）。かつ
   **検証対象がクエリ引数**なので、`AbstractValidator` に載せるには要求モデルを起こす判断が要る。
2. **DataSourceService の 4 箇所。** `ConnectionUriPolicy.Validate` は**既存値**を、
   `OwnerMappingValidation.ValidateAsync` は**外部の利用者名簿**を見る。検証器へ持ち込むと
   Infrastructure 依存が `Features/` の検証器へ入るので、置き場の判断が先に要る。
3. **`ValidationProblem` 系 34 箇所**（理由 C）。
4. **追加引数のある写像 7 本**（DocumentService 4 / GraphService 1 / RetrievalService 1 /
   AuthorizationService 1）。Mapperly の複数源引数の扱いを決める必要がある。
5. **`Platform.Bff` の 1 箇所は射程外。** `/bff/auth/logout` の `Results.BadRequest` は
   **セッションの `sid` との一致検査**であり、要求 DTO の入力検証ではない（本文も返さない）。
6. `LlmGateway` の `IValidateOptions` 手書き 2 本と `Error` → ProblemDetails の共通変換は
   #1230 が既に射程外と宣言している。

## 理由

- 決定 1・2 の基準は `IADR-0371` 決定 1 の言い換えである ——
  **義務は「その関心を実装する箇所では標準ライブラリを使うこと」であり、関心の無いサービスへ
  空の参照を足すことではない。** 上表の ❌ はすべてこの基準の適用結果であり、
  **「面倒だから見送った」ものは 1 つも無い。**
- 決定 3 の「述語を写す」は、移送作業一般の作法である。**ライブラリの便利な述語へ置き換えることは
  等価性の検証を難しくする方向にしか働かない**（置き換えたければ、移送が終わってから別の変更でやる）。
- 決定 4 の 1 は、`IADR-0242` / `IADR-0272` が積み上げた判定順の設計に触れる。
  **順序が仕様になっている箇所へ、検証の器を変える作業を同時に持ち込まない。**

## 結果

- 良い影響: `Platform.Shared.Kernel` の実参照が **1/14 → 4/14**、FluentValidation が **1 → 4**、
  Riok.Mapperly が **1 → 4** になった。**手書きのガード節は 23 → 17 箇所**（6 箇所を移送）、
  **DTO ↔ ドメインの手書き写像は 11 → 8 本**（3 本を移送）。
- 悪い影響 / トレードオフ:
  - **#1230 の受け入れ基準は本 PR では満たしきらない**（6/13 サービス）。残りは追随 issue が持つ。
  - **母集合が #1230 の宣言より広いことが判った**（`ValidationProblem` 系 34 箇所）。
    これは #1230 の受け入れ基準「手書きのガード節が残っていない」の判定を変える ——
    `Results.BadRequest` を 0 にしても手書き検証は残る。
  - `Errors[0]` を採る形は、**将来「全違反を返す」応答へ変えるときに書き換えが要る**
    （`IADR-0371` から引き継ぐトレードオフ）。
- フォローアップ: 決定 4 の 1〜4 を追随 issue **#1248** へ切り出した（群ごとに PR を刻んでよい）。
