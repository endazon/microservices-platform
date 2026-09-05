---
title: RFC7807（Results.ValidationProblem）系の手書き検証を FluentValidation へ移す —— PR-A（DocumentService Documents / Tags 集約）
type: spec
status: done
related_ids:
  - FR-05
  - FR-06
  - FR-09
  - FR-19
  - FR-20
  - FR-21
  - UC-03
  - SC-05
  - SC-09
  - ADR-0030
  - ADR-0041
  - ADR-0054
  - ADR-0058
  - ADR-0063
  - ADR-0065
  - ADR-0068
  - IADR-0229
  - IADR-0371
  - IADR-0393
  - IADR-0395
  - IADR-0398
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md 決定（検証 = FluentValidation）
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md 決定 1
---

# 仕様書: #1278 PR-A —— DocumentService `Documents` / `Tags` 集約の RFC7807 系ガード節を FluentValidation へ移す

> 本仕様書は #1278（親 #1248 / #1230 / #1064。環流 planning#490）の 4 分割のうち **PR-A** を対象とする。
> 設計は先行の設計パスが確定させており、本書はその適用と、着手前の母集合の自己導出を記録する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（機密区分の必須検証）/ FR-06（文書 CRUD）/ FR-09（タグ辞書）/ FR-19（doc_scope）/ FR-20（共有）/ FR-21（本文投入）
- ユースケース（UC）: UC-03（文書の登録・編集）
- 画面（SC）: SC-05（文書管理）/ SC-09（タグ辞書）
- 関連 ADR: ADR-0030（Application 層のライブラリ選定 —— 検証は FluentValidation）/ ADR-0041（Result 型の外部ライブラリ。参照の向き）/ ADR-0054・ADR-0058（doc_scope）/ ADR-0063（タグ反映の認可）/ ADR-0065（単一プロジェクト VSA）/ ADR-0068（3 段のスライス分割規則）
- 実装 ADR: IADR-0371（参照実装）/ IADR-0393（波 1）/ IADR-0395（波 2 第 1 弾 = `Results.BadRequest` 系）/ IADR-0229（`Error` は `Message` を 1 つだけ持つ）/ **IADR-0398（本 PR で起草）**
- 計画書リンク: `../project-planning/projects/microservices-platform/07_adr/`

## 目的・背景

IADR-0393 理由 C・IADR-0395 決定 9 が「群 3」として送った RFC7807（`Results.ValidationProblem`）系の手書きガード節を
FluentValidation の `AbstractValidator` へ移す。**応答本文は 1 バイトも変えない。**

### 🔴 着手前に自分で引き直した母集合（#1278 の前提の訂正）

基点 `origin/develop` @ `4eff9bb4`（`git rev-parse --is-shallow-repository` = **false**。`git log` を出典に使える）。

**軸 1**（`grep -rn "ValidationProblem" src --include=*.cs | grep -v Tests`）: 46 行ヒット。
うちコメント行 5（DataSource 3 / Authz 1 / Mcp 1）、`Platform.Bff` のコメント 4、ヘルパの**定義**行 4
（`AuthzEndpoints.cs:83` / `UserAdminEndpoints.cs:56` / `OwnerMappingValidation.cs:60` / `McpClientEndpoints.cs:85` は
呼び出しではない）を除いた**呼び出し行が 37** —— #1278 の数え（転記ではなく再計算）と一致した。

**軸 2**（McpServer の私有 sink `Problem(` 経由。軸 1 では落ちる）: 6 呼び出し。
**軸 3**（`Results.Problem(` = 413 / 409 / 402 相当。**400 の検証応答ではない**ので母集合外）。

**陽性対照**（「無い」を「無い」と読む前に走査器が生きていることを確かめた）:

| 走査 | ヒット | 意味 |
| --- | ---: | --- |
| `Results.BadRequest` の非テスト行 | 18 | 波 2 第 1 弾が残した「入力検証ではない」箇所が拾える（走査器は生きている） |
| FluentValidation の `PackageReference` を持つ csproj | 6 | IADR-0395 §結果「4 → 6」と一致 |
| `AddScoped<IValidator<` の `Program.cs` 登録行 | 11 | 1 検証器 1 行の明示登録が全部拾える（`AddValidatorsFromAssembly` は 0 件） |
| `DocumentService` の `ValidationProblem` 呼び出し行 | 20 | 本 PR が触る集約の上限（うち `Documents`/`Tags` は 15 行） |

★［2026-09-05 追記 / #1278］🔴 **登録行の数は当初「`IValidator<` … 10」と書いていた。2 回直して 2 回とも誤った。**
1 回目は着手時の数え違い（11 を 10 と書いた）。2 回目の是正では `grep -c "IValidator<"` を使ったため
**説明文のコメント行**（`DocumentService/Program.cs:73` が `` `IValidator<T>` `` を引用している）を
登録行として数え、「本 PR 適用後 20 行・DocumentService 9」と書いた —— 実測は **19 行・8** である。
**語で数えると散文が混ざる。走査語を `AddScoped<IValidator<` へ絞り直した。**
あわせて、同じ値を持つ `IADR-0398` §母集合の表も直した（規則 7: **値を 1 つ直したら、その値を持つ
ファイルを全走査し直す**。1 回目の是正では ADR 側だけを直して本表を見落としていた）。
🔴 **3 回目の誤りもした** —— この追記を表の途中へ挟んで**表を分断**した（GFM はヘッダと区切り行に
連続する行だけを表として読む）。**追記は表の後ろへ置く。**

### 🔴 #1278 の前提は半分誤りである —— 37 のうち「全違反を返す」のは 11 だけ

#1278 は 37 箇所を一律に「**全違反を返す** RFC7807 なので `Errors[0]` の規約が使えない」と書く。
**基点で各サイトの振る舞いを読むと 2 種類ある。**

- **形 α（最初の違反 1 件を 1 つの鍵で返す）= 26 箇所。** ガード節ごとに `return` しており、
  辞書は常に 1 鍵 1 メッセージである。**DocumentService の全ガードがこれ**である。
  実測（本 PR の射程内から 1 例）: `Features/Documents/Create/Endpoint.cs:34-38` は `title` を返して
  **その場で `return` する**ため、続く `:47`（機密区分）・`:54`（doc_scope）・`:56`（個人資料経路）へ
  **到達しない**。題名も機密区分も欠けた要求の応答は `{"errors":{"title":[...]}}` の 1 鍵だけである。
- **形 β（全件を 1 つの鍵へ）= 11 箇所。** AuthorizationService 6 / McpServer 2 / DataSourceService 2 /
  NotificationService 1。ドメイン関数が `List<string>` を返し、空でなければ全件を載せる。

→ **26 箇所では `IADR-0371` 決定 2「`Errors[0]` を採る」「宣言順が応答の契約」がそのまま持ち越せる。**
#1278 の「全違反」という枠を DocumentService へ転記して `result.ToDictionary()` で写していたら、
**複数違反の要求で鍵が増え、応答本文が変わっていた**（本 PR はこれを写像 `FirstViolation` で閉じる）。

## 対象範囲

### 対象（PR-A = 8 検証器 / 14 ガード）

| # | サイト（基点の `path:line`） | 鍵 | 位置 |
| --- | --- | --- | --- |
| D1 | `Features/Documents/AddTag/Endpoint.cs:40-45` | `name` | 入口 |
| D2 | `Features/Documents/Create/Endpoint.cs:34-38` | `title` | 入口 |
| D3 | `Create:47-48` → `DocumentEndpoints.cs:87-96` | `confidentiality` | **413（`:42-43`）の後ろ** |
| D4 | `Create:54-55` → `DocumentEndpoints.cs:115-124` | `doc_scope` | 同上 |
| D5 | `Create:56-64` | `doc_scope` | 同上 |
| D6 | `Features/Documents/Update/Endpoint.cs:15-19` | `title` | 入口（`FindAsync :29` より前） |
| D7 | `Update:22-23` | `confidentiality` | 入口 |
| D8 | `Update:26-27` | `doc_scope` | 入口 |
| D10 | `Features/Documents/UpdateMetadata/Endpoint.cs:18-19` | `confidentiality` | 入口 |
| D11 | `UpdateMetadata:22-23` | `doc_scope` | 入口 |
| D13 | `Features/Documents/GrantShare/Endpoint.cs:21-30` | `errors` | 入口（1 本の `||`） |
| D14 | `Features/Documents/PutBody/Endpoint.cs:22-26` | `body` | 入口 |
| D24 | `Features/Tags/Create/Endpoint.cs:16-21` | `name` | 入口 |
| D25 | `Features/Tags/Rename/Endpoint.cs:25-30` | `name` | 入口 |

付随: `Features/ValidationProblems.cs`（1 段目・新規 sink）、`Features/Documents/DocumentAttributeRules.cs`
（2 段目・共有規則。旧ヘルパ `ConfidentialityProblemOrNull` / `DocScopeProblemOrNull` を置換して削除）、
`.ai-context/adr/IADR-0398_*.md`（起草）、`UpdateDataSourceValidator.cs` の注記の引き直し、
`IADR-0395` 決定 9 への日付つき追記（群 3 の落着先）。

### 対象外（本 PR で**触らない**。理由つき）

| 箇所 | 理由 |
| --- | --- |
| DocumentService `PrivateNotes` / `ObsidianSync` / `SyncDevices`（D16–D21・D23） | **PR-B**。ファイル領域が本 PR と同じ `DocumentService/**` なので**直列**にする |
| McpServer（M1–M3）＋ AuthorizationService（A4） | **PR-C**（「鍵は sink が持つ」変種） |
| NotificationService（N1） | **PR-D**。#1278 を `Closes` するのは最後に着地する PR |
| `DocumentEndpoints.cs:213-217` `UnknownTagsProblem`（呼び出し 4） | **残す**。辞書照会の**結果**であり入力検証ではない。しかも認可の後ろ（`AddTag:50-56` の注記）で動かせない |
| `DocumentEndpoints.cs:101-111` `DocScopeChangedProblemOrNull`（D9 / D12） | **残す**。既存値 `doc.Attributes` が要る（`FindAsync` の後ろ） |
| `PrivateNotes/SetQuota/Endpoint.cs:17-27`（D22） | **残す**。ドメイン不変条件の**例外由来**。写すと不変条件が 2 箇所になる |
| DataSourceService `OwnerMappingValidation`（S1 / S2） | **残す**（IADR-0398 決定 6）。位置・2 段 1 関数・502 の 3 点 |
| 413 / 409 / 402 相当 / 401 / 404 / 502 の応答 | **母集合外**。400 の検証応答ではない（軸 3） |
| `Platform.Bff` の 4 行 | コメントであって呼び出しではない |

## 設計

適用する決定（設計パスが確定。IADR-0398 として起草する）:

1. **決定 1（応答の契約）**: 器（`Results.ValidationProblem(IDictionary<string,string[]>)`）は 1 バイトも変えない。
   形 α は `Errors[0]` を **その鍵で** 1 件だけ載せる（`ValidationProblems.FirstViolation`）。
   🔴 **鍵は必ず明示する**（`OverridePropertyName` / `Custom` の `AddFailure`）。推論名は `Title`（PascalCase）で
   移送前の `title` と一致しない。モデルレベル規則の推論名は空文字 `''` になる。
2. **決定 2（Kernel を参照しない）**: `Error` は `Message` を 1 つしか持たない（`Platform.Shared.Kernel/Error.cs`。
   `IADR-0229` 決定 1「`Error` を複数持つ表現を導入しない」）ので、鍵つき・複数件の応答を運べない。
   #1278 の受け入れ基準「`Result` / `Error` を使うサービスの `.csproj` は Kernel を参照している」は
   **条件節が偽であり空真で満たす**（DocumentService は本移送で `Result` を使わない）。
3. **決定 3（`RuleSet`）**: `Create` は `title` が入口、属性 3 規則が **413 の後ろ**にある。
   1 検証器・1 DI 鍵のまま `RuleSet("attributes")` で位置ごとの集合を名付ける。
   🔴 **ハザード**: `Validate(req)` は名前つき集合を走らせない。第 2 の呼び出しを消しても
   コンパイルも起動も通り、属性が黙って無検証になる → **位置の対試験で固定する**。
4. **決定 4（共有規則）**: 3 操作（Create / Update / UpdateMetadata）が共有する属性規則は
   `Features/Documents/DocumentAttributeRules.cs`（**2 段目**。ADR-0068 決定 1 —— 集約の複数操作が使うものは
   2 段目に残す。旧ヘルパと同じ場所）に拡張メソッド 1 組で置く。実体は `Domain/DocumentAttributes` の関数のまま
   （**Domain に FluentValidation を入れない**）。述語は `Custom` で書く（`Must` + `WithMessage(func)` だと同じ関数を 2 度呼ぶ）。
   **タグ名の規則（D1 / D24 / D25）は共有しない** —— 移送前も 3 箇所が同じ 4 行をそれぞれ持っており、
   共有化は「振る舞いを変えない」枠を超える整理である。
5. **sink**: DocumentService には共有 sink が無いので `Features/ValidationProblems.cs`（**1 段目**）を 1 つ足す。
   5 集約（Documents / Tags / PrivateNotes / SyncDevices / ObsidianSync）が使うため 2 段目には置けない。
6. **登録**: `Program.cs` に **1 検証器 1 行の明示登録**（`AddScoped<IValidator<T>, TValidator>()`）。
   `AddValidatorsFromAssembly` は使わない（`IADR-0371` 決定 2 —— 消したときに止まること）。
7. **等価性の軸**: 状態コード ＋ **鍵の列** ＋ **各鍵のメッセージ列** ＋ 判定の位置 ＋ 述語の粒度。
   述語は写す（`string.IsNullOrWhiteSpace` を `NotEmpty()` に置き換えない。`Tag.Normalize` 後の
   `IsNullOrEmpty` も同じ）。粒度も写す（D13 の `subjectId ∨ subjectType` は **1 本の `||` のまま 1 規則**）。

## 受け入れ基準

- [x] `Documents` / `Tags` 集約の 14 ガードが 8 つの `AbstractValidator` へ移り、端点に手書きの入力検証ガード節が残らない
- [x] 応答本文が移送前後で**バイト同一**である（形 α の代表 2 サイトで前後の JSON を実測して比較した）
- [x] 鍵はすべて明示（`OverridePropertyName` / `AddFailure`）であり、推論名に依存する規則が 1 つも無い
- [x] `Create` の属性 3 規則は `RuleSet` に入り、**413 の後ろ**で走る（位置の対試験が緑）
- [x] 旧ヘルパ `ConfidentialityProblemOrNull` / `DocScopeProblemOrNull` は削除され、`DocScopeChangedProblemOrNull` は残る
- [x] `Program.cs` の登録は 8 行の明示登録であり、`AddValidatorsFromAssembly` を使っていない
- [x] `Platform.Shared.Kernel` への参照を DocumentService に足していない（空真の理由を IADR に明記した）
- [x] Domain 層に FluentValidation の依存が入っていない（拡張メソッドは `Features/` にある）
- [x] 変異 4 種（規則 1 本を消す／登録行を消す／`OverridePropertyName` を消す／第 2 の `Validate` を消す）で**実際に赤になった**試験名と本数を PR に書いた
- [x] `dotnet test` の件数が前後で減っていない（DocumentService 257 → **320**、両ユニット合計 3350 → **3413**）
      ★［2026-09-05 追記 / #1278］**当初ここに書いた 297 / 3390 は作業途中の値であり、最終コミットへ追随していなかった**
      （AI レビューが実走して検出した）。**受け入れ基準に導出値を書くときは、最後にもう一度測って書き直す。**
- [x] `dotnet build` × 2 / `dotnet test` × 2 / `dotnet format --verify-no-changes` / 検査器 7 本が緑
- [x] IADR-0398 を起草し、番号がマージ時点で連続（`origin/develop` の最大は 0397、0398 は in-flight）であることを再確認した

## テスト方針

設計の §4 の試験行列のうち、**PR-A の射程にあるもの**を実装する。

| 試験 | 軸 | 何を見るか | 赤にする変異 |
| --- | --- | --- | --- |
| `<Validator>Tests.ValidRequest_Passes`（8 検証器すべて） | 陽性対照 | `IsValid` かつ `Errors` 空 | 「常に落ちる検証器」 |
| `<Validator>Tests.<Rule>_FailsWithOriginalKeyAndMessage` | K, M | `Errors[0].PropertyName` が**鍵の定数**かつ定数がリテラル。メッセージも定数とリテラルの両方 | `OverridePropertyName` を消す（`Title` になる） |
| `UpdateDocumentValidatorTests.MultipleViolations_ReportsTitleFirst` | O, C | 全違反で `Errors.Count == 3` かつ `Errors[0]` が `title` | 宣言順の入れ替え／規則を 1 本消す |
| `CreateDocumentValidatorTests.DefaultRuleSet_DoesNotRunAttributeRules` | P | `Validate(req)` で属性違反が**出ない**。`IncludeRuleSets` で出る | 属性規則を `RuleSet` の外へ出す |
| `GrantShareValidatorTests.BothInvalid_ReportsOneFailure` | G | `subjectId` 空 ∧ `subjectType` 不正 → `Errors.Count == 1` | `\|\|` を 2 本の `RuleFor` に割る |
| 端点契約試験 `…_Returns400WithOriginalBody`（`errors` を `JsonElement` で読む） | S, K, M | `errors` のプロパティを**列挙順**で比較 | 端点が `ToDictionary()` を呼ぶ／sink の鍵を変える |
| `CreateDocument…_OversizedBodyWithMissingConfidentiality_Returns413` と対の `…_MissingConfidentiality_Returns400` | P | 413 と 400 の**対** | 第 2 の `Validate(…IncludeRuleSets)` を消すと後者が 201 |
| `UpdateDocument…_UnknownIdWithEmptyTitle_Returns400` | P | 不存在 ID ＋ 空題名 → **400**（`FindAsync` より前） | 検証を `FindAsync` の後ろへ動かす |
| `AddTag…_EmptyName_Returns400BeforeAuthorization` | P | 認可・辞書照合より前 | 検証を認可の後ろへ動かす |
| **既存**（状態コードのみを見る 23 行ほか） | S | 登録行を消すと `IValidator<T>` が解決できず 500 | 登録行の削除 |

## 計画書との差異

- 差異: なし。**応答本文を 1 バイトも変えない移送**であり、計画の裁定を要する事項は無い
  （ADR-0030 は用途ごとのライブラリ指定、ADR-0041 は参照の向きについての決定であり、どちらも本作業と整合する）。
  planning への `decision-needed` 起票はしていない。

## 未決事項

- **採番の先着**: `IADR-0398` は in-flight PR が取る前提。マージ直前に `git ls-tree origin/develop .ai-context/adr/`
  で再確認し、先を越されていたら次の空き番号へ改番する（ファイル名・本文・仕様書・コード内コメント・PR タイトルの 5 項目を追随）。
- **PR-B〜D**: 本 PR で決めた形（`FirstViolation` / 鍵の明示 / `RuleSet`）の**適用**のみで、新しい判断は無い予定。
  判断が出たら IADR-0398 へ日付つき追記ブロックで足す。
