---
title: RFC7807（Results.ValidationProblem）系の手書き検証を FluentValidation へ移す —— PR-B（DocumentService PrivateNotes / ObsidianSync / SyncDevices 集約）
type: spec
status: done
related_ids:
  - FR-19
  - FR-20
  - UC-11
  - SC-19
  - SC-20
  - ADR-0030
  - ADR-0037
  - ADR-0041
  - ADR-0065
  - ADR-0068
  - IADR-0229
  - IADR-0371
  - IADR-0393
  - IADR-0395
  - IADR-0398
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md 決定（検証 = FluentValidation）
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md 決定 2・7・8・11・17・20
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md 決定 1
---

# 仕様書: #1278 PR-B —— DocumentService `PrivateNotes` / `ObsidianSync` / `SyncDevices` 集約の RFC7807 系ガード節を FluentValidation へ移す

> 本仕様書は #1278（親 #1248 / #1230 / #1064。環流 planning#490）の 4 分割のうち **PR-B** を対象とする。
> 契約は PR-A が `IADR-0398` として確定済みであり、**本 PR はその適用である**（新しい判断は無い）。
> `IADR-0398` 決定 10 の表が本 PR の射程（5 検証器 / 7 ガード）を定めている。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-19（個人資料の作成・完全削除・容量）/ FR-20（Obsidian Vault との双方向同期・同期資格情報）
- ユースケース（UC）: UC-11（個人資料の作成・同期・削除）
- 画面（SC）: SC-19（個人資料管理）/ SC-20（Obsidian 連携設定）
- 関連 ADR: ADR-0030（Application 層のライブラリ選定 —— 検証は FluentValidation）/ ADR-0037（同期方式。決定 2 リネームは版を進めない・決定 7 競合は解決しない・決定 8 1 編集 1 版・決定 11 トークン発行・決定 17 容量・決定 20 完全削除）/ ADR-0041（Result 型の外部ライブラリ。参照の向き）/ ADR-0065（単一プロジェクト VSA）/ ADR-0068（3 段のスライス分割規則）
- 実装 ADR: **IADR-0398（PR-A が確定させた契約。本 PR はその適用）**/ IADR-0371 決定 2（`Errors[0]`・宣言順が契約・明示登録）/ IADR-0393（波 1）/ IADR-0395（波 2 第 1 弾）/ IADR-0229（`Error` は `Message` を 1 つだけ持つ）
- 計画書リンク: `../project-planning/projects/microservices-platform/07_adr/`

## 目的・背景

`IADR-0398` 決定 10 の PR-B。DocumentService に残る RFC7807（`Results.ValidationProblem`）系の手書き入力検証ガード節
（`PrivateNotes` / `ObsidianSync` / `SyncDevices` の 3 集約）を `AbstractValidator` へ移す。
**応答本文は 1 バイトも変えない。**

### 🔴 着手前に自分で引き直した母集合（PR-A の数えを転記しない）

基点は `origin/develop` @ **`32724227`**（PR-A `7bac08c0` と IADR-0399 / IADR-0400 の 2 本を含む）。
`git rev-parse --is-shallow-repository` = **`false`** —— 履歴が打ち切られていないので `git log` を出典に使える。

🔴 **`src/ai-stock-trading`（submodule）を走査から外す。** 初期化した状態で走査すると
`Results.BadRequest` が 18 → **46** に、`AddValidatorsFromAssembly` が 0 → **7** に化ける
（前者は submodule 側の実コード、後者は説明文）。**母集合は本リポジトリの追跡ファイルだけである。**

**軸 1**（`grep -rn "ValidationProblem" src --include=*.cs | grep -v Tests`、submodule 除外）: **48 行**。
うち**コメント行 9**を除いた 39 行が定義・呼び出しである（PR-A 適用後の値。PR-A 前は「呼び出し 37」であった）。

**本 PR の射程**（`grep -rn "Results\.ValidationProblem(" Features/{PrivateNotes,ObsidianSync,SyncDevices}`）: **8 行**。
うち **7 を移し、1（`SetQuota`）を残す**（`IADR-0398` 決定 8）。

**陽性対照**（「無い」を「無い」と読む前に、走査器が生きていることを確かめた）:

| 走査（submodule 除外） | ヒット | 意味 |
| --- | ---: | --- |
| `Results.BadRequest` の非テスト行 | 18 | 波 2 第 1 弾が「入力検証ではない」として残した箇所が拾える ＝ 走査器は生きている |
| FluentValidation の `PackageReference` を持つ csproj | 7 | `IADR-0398` §結果「6 → 7」（PR-A で DocumentService が加わった）と一致 |
| `AddScoped<IValidator<` の登録行 | 19（DocumentService 8） | `IADR-0398` の「本 PR 適用後は 19 行・うち DocumentService 8」と一致。本 PR 適用後は **24 行・DocumentService 13** になる |
| `AddValidatorsFromAssembly` の**呼び出し** | 0 | 7 ヒットは**すべて説明文のコメント行**である（`grep -c` で語を数えると 7 になる。**語ではなく構文で数える**） |
| PR-A が入れた `ValidationProblems.FirstViolation` の呼び出し | 9 | 本 PR が再利用する sink が実在する（**2 つ目の sink を作らない**） |

### 各サイトの「1 件か全件か」を基点で読み直した（#1278 の前提の訂正を自分で再検証）

#1278 は群 3 を一律に「**全違反を返す**」と書く。**本 PR の 8 サイトはすべて形 α（先頭 1 件・1 鍵）である。**
根拠は字面ではなく制御フローである —— **どのガードも違反を見つけたその場で `return` しており、
2 つ目のガードへ到達しない**。したがって `IADR-0371` 決定 2（`Errors[0]` を採る／宣言順が応答の契約）が
そのまま持ち越せ、sink は `ValidationProblems.FirstViolation` である（`ToDictionary()` **ではない**）。

実測の代表: `ObsidianSync/Move/Endpoint.cs:32-36` は `vaultPath` を返して**その場で `return` する**ため、
続く `version` の検査（`:37-41`）へ**到達しない**。`vaultPath` も `version` も欠けた要求の応答は
`{"errors":{"vaultPath":[...]}}` の **1 鍵 1 件**である。

## 対象範囲

### 対象（PR-B = 5 検証器 / 7 ガード）

| # | サイト（基点 `32724227` の `path:line`。`Features/` 起点） | 鍵 | 形 | 判定の位置 |
| --- | --- | --- | --- | --- |
| D16 | `ObsidianSync/Move/Endpoint.cs:32-36` | `vaultPath` | α | **401（`:28-29` 端末解決）の後ろ**・404（`:44-45`）の前 |
| D17 | `ObsidianSync/Move/Endpoint.cs:37-41` | `version` | α | 同上（D16 の直後） |
| D18 | `ObsidianSync/Push/Endpoint.cs:34-39` | `errors` | α（**1 本の 4 項 `\|\|`**） | 401（`:30-31`）の後ろ・**413（`:43-48`）の前** |
| D19 | `ObsidianSync/Push/Endpoint.cs:109-113` | `baseVersion` | α | **更新分岐の中**。404（`:100` / `:105`）・409 deleted（`:101-102`）の後ろ、409 version_conflict（`:114`）の前 |
| D20 | `PrivateNotes/Create/Endpoint.cs:17-21` | `title` | α | 401（`:16`）の後ろ・容量 402 相当（`:26-27`）の前 |
| D21 | `PrivateNotes/Purge/Endpoint.cs:27-31` | `ids` | α | 401（`:26`）の後ろ・DB 照会（`:34`）の前 |
| D23 | `SyncDevices/Issue/Endpoint.cs:20-24` | `deviceName` | α | 401（`:18-19`）の後ろ |

新設する検証器（**PR-A の置き場・命名の作法をそのまま使う** —— 3 段目 = その操作専用）:

| 検証器 | 置き場 | 要求型 | 規則 |
| --- | --- | --- | --- |
| `MoveNoteValidator` | `Features/ObsidianSync/Move/` | `MoveNoteRequest` | D16 → D17（**宣言順が契約**） |
| `PushNoteValidator` | `Features/ObsidianSync/Push/` | `PushNoteRequest` | 既定集合 = D18 ／ `RuleSet(BaseVersionRuleSet)` = D19 |
| `CreatePrivateNoteValidator` | `Features/PrivateNotes/Create/` | `CreatePrivateNoteRequest` | D20 |
| `PurgePrivateNotesValidator` | `Features/PrivateNotes/Purge/` | `PurgePrivateNotesRequest` | D21 |
| `IssueSyncDeviceValidator` | `Features/SyncDevices/Issue/` | `CreateSyncDeviceRequest` | D23 |

### 対象外（本 PR で**触らない**。理由つき）

| 箇所 | 理由 |
| --- | --- |
| `PrivateNotes/SetQuota/Endpoint.cs:23-26`（D22。鍵 `limitBytes`） | **残す**（`IADR-0398` 決定 8）。**例外由来**であり、述語 `limitBytes <= 0 \|\| > MaxLimitBytes` もメッセージも `Domain/PrivateNote` の**不変条件**が持つ。写すと不変条件が 2 箇所になり、片方だけ直る。位置も `GetOrCreateQuotaAsync`（DB）の後ろで動かせない |
| McpServer ＋ AuthorizationService | **PR-C**（`IADR-0398` 決定 5・決定 1(b)。「鍵は sink が持つ」変種） |
| NotificationService | **PR-D**。**#1278 を `Closes` するのは PR-D** である |
| `DocumentEndpoints.UnknownTagsProblem` / `DocScopeChangedProblemOrNull` | PR-A が「残す」と決めた（`IADR-0398` 決定 8）。本 PR でも触らない |
| DataSourceService `OwnerMappingValidation`（S1 / S2） | **恒久的に残す**（`IADR-0398` 決定 6。位置・2 段 1 関数・502 の 3 点） |
| 413 / 409 / 402 相当 / 401 / 404 の応答（`Push:43-48` の 413、`Move:46-58` の 409、`Purge:38-40` の 404/409、`Create:26-27` の容量、`PathConflictProblem` ほか） | **母集合外**。400 の入力検証応答ではない（`IADR-0398` の軸 3） |
| `Features/ValidationProblems.cs` / `Features/Documents/DocumentAttributeRules.cs` | **再利用するだけで変更しない。** 2 つ目の sink を作ったらそれは契約から漂流した合図である |

## 設計

`IADR-0398` の決定をそのまま適用する。**新しい判断は無い**（したがって新 IADR も起こさない）。

1. **決定 1（応答の契約）**: 器（`Results.ValidationProblem(IDictionary<string,string[]>)`）は変えない。
   7 サイトはすべて形 α なので `ValidationProblems.FirstViolation(result)` で写す。
   🔴 **鍵は必ず `OverridePropertyName(<internal const>)` で明示する**。推論名は `VaultPath` / `Title` /
   `Ids` / `DeviceName`（PascalCase）であり、移送前の `vaultPath` / `title` / `ids` / `deviceName` と一致しない。
   **型では止まらない**ので、検証器の試験が「鍵の定数」と「リテラル」の両方を見る。
2. **決定 3（`RuleSet`）**: `Push` は**判定の位置が 2 つある**。D18 は入口（401 の後ろ・413 の前）、
   D19 は**更新分岐の 404 の後ろ**である。1 検証器・1 DI 鍵のまま
   `RuleSet(PushNoteValidator.BaseVersionRuleSet)` で後段の集合を名付け、端点は
   `Validate(req)` と `Validate(req, o => o.IncludeRuleSets(...))` を位置ごとに呼ぶ。
   🔴 **ハザード**: `Validate(req)` は名前つき集合を走らせない。第 2 の呼び出しを消しても
   コンパイルも起動も通り、`baseVersion` が**黙って無検証**になる → **位置の対試験で固定する**
   （「不存在 noteId ＋ baseVersion なし → **404**」と「実在 noteId ＋ baseVersion なし → **400** `baseVersion`」）。
   集合名は検証器の `internal const string` に置き、端点はその定数を使う（文字列を 2 箇所に書かない）。
3. **決定 9（等価性の軸）**: 状態コード ＋ **鍵の列** ＋ **各鍵のメッセージ列** ＋ 判定の位置 ＋ 述語の粒度。
   - **述語を写す**。`string.IsNullOrWhiteSpace` を `NotEmpty()` に置き換えない。
     `req.Version is not { } version` は `Must(v => v is not null)` へ、
     `req.Ids is not { Count: > 0 }` は `Must(ids => ids is { Count: > 0 })` へ、**同じ形のまま**写す。
   - 🔴 **述語の粒度を写す**。D18 は `title` ∨ `vaultPath` ∨ `edits` 空 ∨ `content` null の
     **1 本の 4 項 `||`** であり、1 規則・1 メッセージである。4 本の `RuleFor` に割ると
     全部不正な要求で失敗が 4 件になり、粒度が変わる。**式を否定してそのまま 1 つの `Must` に入れる**
     （`||` の短絡評価も保つ —— `Edits` が null のとき `req.Edits.Any(...)` を評価しない形が要る）。
   - **宣言順を移送前のガード節の順に揃える**（`Move` は `vaultPath` → `version`）。
4. **決定 8（sink）**: `Features/ValidationProblems.cs` の `FirstViolation` を**再利用する**。
   DocumentService の sink はこれ 1 つであり、2 つ目を作らない。
5. **登録**: `Program.cs` に **1 検証器 1 行の明示登録**（`AddScoped<IValidator<T>, TValidator>()`）を 5 行足す
   （8 → 13 行）。`AddValidatorsFromAssembly` は使わない（`IADR-0371` 決定 2 —— 消したときに止まること）。
6. **Kernel を参照しない**（決定 2）。本 PR も `Result` / `Error` を経由しない。
7. **メッセージ・鍵は `internal const`**（`Move` の 2 本、`Push` の 2 本、`Create` / `Purge` / `Issue` の各 1 本）。
   **タグ名の規則と同じく、同じリテラルを持つ検証器があっても共有しない**（決定 4 の作法。
   `CreatePrivateNoteValidator` の `"タイトルは必須です。"` は `CreateDocumentValidator` と同文だが、
   共有は「振る舞いを変えない」枠を超える整理である）。

### 母集合の取り方（`traceability.repo.md` 規則 9・10）

- **規則 9**（追随する文書を記憶で挙げない）: 「本 PR で新たに誤りになる自分の記述」を、
  **誤りの側の文字列で走査してから**挙げた。走査語は `AddScoped<IValidator<`（19 → 24）と
  `Results.ValidationProblem(`（`Features/{PrivateNotes,ObsidianSync,SyncDevices}` の 8 → 1）である。
- **規則 10**（導出値は走査ではなく計算し直す）: `IADR-0398` §母集合の表は
  「本 PR 適用後は 19 行（うち DocumentService 8）」と書いており、これは**PR-A 時点の値として正しい**。
  PR-B は決定 10 の表が「検証器 5」と予告しているとおりの増分なので、**ADR 側の追記は要らない**
  （追記が要るのは「新しい判断が出たとき」だけである。`IADR-0398` 決定 10 末尾）。
  **除外理由**: `IADR-0398` の 19 という値は「PR-A 適用後」と明記された時点の値であり、
  後続 PR で増えることは同 ADR 決定 9・10 が既に述べている。**古くならない書き方になっている。**

## 受け入れ基準

- [x] `PrivateNotes` / `ObsidianSync` / `SyncDevices` の 7 ガードが 5 つの `AbstractValidator` へ移り、
      3 集約の端点に手書きの入力検証ガード節が残らない（`SetQuota` の例外由来 1 本を除く）
- [x] 応答本文が移送前後で**バイト同一**である（代表 1 サイトで生の JSON を前後比較した）
- [x] 鍵はすべて明示（`OverridePropertyName`）であり、推論名に依存する規則が 1 つも無い
- [x] `Push` の `baseVersion` 規則は `RuleSet` に入り、**更新分岐の 404 の後ろ**で走る（位置の対試験が緑）
- [x] `SetQuota`（D22）は移送しておらず、`OwnerMappingValidation` にも触っていない
- [x] sink は `ValidationProblems.FirstViolation` 1 つのままである（2 つ目を作っていない）
- [x] `Program.cs` の登録は明示登録 13 行であり、`AddValidatorsFromAssembly` を使っていない
- [x] 端点契約試験を**先に**書き、端点 5 ファイルを `origin/develop` へ戻した状態でも**緑**になることを実測した
- [x] 変異（規則を消す／登録行を消す／`OverridePropertyName` を消す／第 2 の `Validate` を消す／
      `RuleSet` の外へ出す／1 本の `||` を割る）で**実際に赤になった**試験名と本数を PR に書いた
- [x] `dotnet test` の件数が前後で減っていない（**測り直して書く**。試験の削除・skip 化は 0 件）
- [x] `dotnet build` × 2 / `dotnet test` × 2 / `dotnet format --verify-no-changes` / 検査器 7 本＋
      `REQUIRE_REPO_TESTS=1 scripts.test.js` が緑

## テスト方針

`IADR-0398` 決定 9 の軸（S 状態コード / K 鍵 / M メッセージ / P 位置 / G 粒度 / O 宣言順）へ写像する。

| 試験 | 軸 | 何を見るか | 赤にする変異 |
| --- | --- | --- | --- |
| `<Validator>Tests.ValidRequest_Passes`（5 検証器すべて） | 陽性対照 | `IsValid` かつ `Errors` 空 | 「常に落ちる検証器」 |
| `<Validator>Tests.<Rule>_FailsWithOriginalKeyAndMessage` | K, M | `Errors[0].PropertyName` が**鍵の定数**、かつ定数がリテラルと一致。メッセージも定数とリテラルの両方 | `OverridePropertyName` を消す（`VaultPath` になる） |
| `MoveNoteValidatorTests.BothMissing_ReportsVaultPathFirst` | O | 両方欠けたとき `Errors[0]` が `vaultPath` | 宣言順の入れ替え／規則を 1 本消す |
| `PushNoteValidatorTests.AllFourInvalid_ReportsOneFailure` | G | 4 項すべて不正 → `Errors.Count == 1` | 4 項の `\|\|` を 4 本の `RuleFor` に割る |
| `PushNoteValidatorTests.DefaultRuleSet_DoesNotRunBaseVersionRule` | P | `Validate(req)` で `baseVersion` 違反が**出ない**。`IncludeRuleSets` で出る | `baseVersion` 規則を `RuleSet` の外へ出す |
| `PushNoteValidatorTests.NullEdits_DoesNotThrow` | G | `Edits` が null でも `NullReferenceException` にならない（短絡評価の保存） | `\|\|` の順序入れ替え／`Any` を先に置く |
| 端点契約試験 `ValidationProblemContractTests`（`errors` を `JsonElement` で列挙順に読む） | S, K, M | 鍵の列と各鍵のメッセージ列 | 端点が `ToDictionary()` を呼ぶ／鍵を変える |
| `Push…_UnknownNoteWithoutBaseVersion_Returns404` と対の `…_ExistingNoteWithoutBaseVersion_Returns400` | P | 404 と 400 の**対** | 第 2 の `Validate(…IncludeRuleSets)` を消すと後者が壊れる |
| `Move…_UnknownNoteWithEmptyVaultPath_Returns400` と対の `…_UnknownNoteWithValidInput_Returns404` | P | 検証は `FindOwnedAsync` より前 | 検証を取得の後ろへ動かす |
| `…_Unauthenticated…_Returns401`（5 端点） | P | **401 が検証より前**である（無資格の呼び出しに入力の形を教えない） | 検証を端末解決 / `SubjectOf` より前へ動かす |
| 生 JSON のバイト比較 1 本 | S, K, M | RFC7807 の外枠ごと文字列一致 | 器を変える |
| **既存**: `ObsidianSyncMoveTests` / `ObsidianSyncProtocolTests` / `PrivateNoteLifecycleTests` / `PrivateNoteQuotaTests` / `SyncDeviceTokenTests` | S | 状態コード | **登録行を消す**（`IValidator<T>` が解決できず 500） |

🔴 **等価性の直接の証拠**: 契約試験を先に書き、**端点 5 ファイルだけを `origin/develop` の内容へ戻して**
同じ試験を走らせる。移送前のコードでも緑であることが「応答を変えていない」の実測である（PR-A と同じ作法）。

## 結果（着地時に測り直した実測値）

🔴 **導出値は push の直前にすべて測り直した**（作業途中の値を受け入れ基準へ残さない。PR-A で
AI レビューが検出した失敗と同型）。基点は `origin/develop` @ `32724227`。

| 走査（submodule 除外） | 前 | 後 |
| --- | ---: | ---: |
| `AddScoped<IValidator<` の登録行（全リポジトリ） | 19 | **24** |
| 同・DocumentService `Program.cs` | 8 | **13** |
| `Results.ValidationProblem(` —— `Features/{PrivateNotes,ObsidianSync,SyncDevices}` | 8 | **1**（`SetQuota` のみ） |
| `return ValidationProblems.FirstViolation(` の**呼び出し**（DocumentService 非テスト） | 9 | **15** |
| `internal static IResult FirstViolation` の**定義** | 1 | **1**（2 つ目の sink を作っていない） |
| `AddValidatorsFromAssembly` の呼び出し | 0 | **0** |

★［2026-09-06 追記 / #1278］🔴 **上表の `FirstViolation` の行を 16 → 15 に直した。**
当初は走査語を `ValidationProblems\.FirstViolation` としていたため、**本 PR で自分が書いた
`MoveNoteValidator.cs:12` のコメント**（sink の説明で同じ名前を引用している）を呼び出しとして数えていた。
**同じ表の `AddValidatorsFromAssembly` の行では「語ではなく構文で数える」を明示的にやっているのに、
この 1 行だけ素朴な行マッチのままだった**（AI レビューが実走して検出。`traceability.repo.md` 規則 10 ——
**是正のたびに、その変更で新たに誤りになる自分の記述を引き直す**）。走査語を
`return ValidationProblems\.FirstViolation(` へ絞り直した。
**「前」の 9 は着手時の実測であり、当時この名前を引くコメントは 1 件も無かったので変わらない。**
本値は PR 本文には出していないため、追随先は本表 1 箇所だけである（誤りの側の文字列
`FirstViolation` で追跡下の全ファイルを走査して確認した）。

試験数（`dotnet test`。**削除・skip 化は 0 件**）:

| プロジェクト | 前 | 後 |
| --- | ---: | ---: |
| `DocumentService.Tests` | 320 | **369**（+49） |
| knowledge ユニット合計 | 1958 | **2007** |
| platform ユニット合計 | 1506 | **1506**（無変更） |
| 両ユニット合計 | 3464 | **3513** |

**等価性の直接の証拠**: 端点契約試験 `SyncValidationProblemContractTests`（19 本）を**移送の前に書き**、
**移送前の端点に対して 19/19 緑**であることを実測した（移送後も 19/19 緑）。
生の JSON をバイト比較する 1 本（`ValidationProblem_KeepsRfc7807Envelope`）を含む。

**変異試験**（1 つずつ適用して戻した。母数は `DocumentService.Tests` の 369 本）:

| 変異 | 赤になった本数 | 代表的な試験名 |
| --- | ---: | --- |
| M1 規則を 1 本消す（`MoveNoteValidator` の `version`） | 4 | `MoveNoteValidatorTests.MissingVersion_*` / `BothMissing_ReportsVaultPathFirst` / `SyncValidationProblemContractTests.Move_NoVersion_*` / `ObsidianSyncMoveTests`（既存） |
| M2 登録行を 1 行消す（`IssueSyncDeviceValidator`） | **227** | `IValidator<T>` が解決できず 500。トークン発行を下ごしらえに使う既存試験が広く落ちる |
| M3 `OverridePropertyName` を消す | 4 | 鍵が `DeviceName` に化ける（`IssueSyncDeviceValidatorTests` / 契約試験 2 本） |
| M4 第 2 の `Validate(…IncludeRuleSets)` を消す | 1 | `SyncValidationProblemContractTests.Push_ExistingNoteWithoutBaseVersion_*`（**黙って無検証**になる） |
| M5 規則を `RuleSet` の外へ出す | **42** | 不存在 noteId の 404 が 400 に化け、新規 push が全滅する（既存の同期試験を含む） |
| M6 1 本の `\|\|` を 3 本へ割る | 1 | `PushNoteValidatorTests.AllFourInvalid_ReportsOneFailure`（**`Errors[0]` は変わらないので契約試験では捕まらない** —— 粒度の試験だけが止める） |

## 計画書との差異

- 差異: なし。**応答本文を 1 バイトも変えない移送**であり、計画の裁定を要する事項は無い
  （ADR-0030 は用途ごとのライブラリ指定、ADR-0037 は同期の意味論についての決定であり、
  どちらも本作業と整合する。状態コードも本文も変えない）。
  planning への `decision-needed` 起票はしていない。

## 未決事項

- **IADR は起こさない予定**である（`IADR-0398` 決定 10「PR-B〜D に新しい判断は無い」）。
  作業中に新しい判断が出た場合は `IADR-0398` へ日付つき追記ブロック（`［YYYY-MM-DD 追記 / #NNN］`）で足す
  —— **表の途中へ挟まない**（GFM の表が分断される。PR-A で実測された失敗）。
  新 IADR が要る場合の空き番号は基点 `32724227` で **`IADR-0401`** である（0400 は #1295 が取得済み）。
- **#1279（写像 7 本）は `PrivateNoteEndpoints.cs` に触る**ため本 PR と交差し得る。FIFO で直列化する。
- **#1278 を閉じるのは PR-D** である。本 PR の PR 本文は `Refs #1278` に留める。
