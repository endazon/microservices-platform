---
title: 追加引数のある写像 7 本に「材料か導出の指示か」の線を引き、5 本を Riok.Mapperly へ移して 2 本を理由つきで残す
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - ADR-0033
  - ADR-0063
  - ADR-0065
  - ADR-0068
  - IADR-0153
  - IADR-0195
  - IADR-0231
  - IADR-0238
  - IADR-0270
  - IADR-0282
  - IADR-0290
  - IADR-0364
  - IADR-0371
  - IADR-0385
  - IADR-0393
  - IADR-0395
  - IADR-0405
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md 決定（マッピング = Riok.Mapperly。選定基準 4）
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0063_tag-suggestion-reflection-and-approval-authz.md 決定 3〜5
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
---

# 仕様書: #1279 —— 追加引数のある写像 7 本を裁定し、5 本を生成マッパへ移す

> #1279（親 #1248 / #1230 / #1064。環流 planning#490）。波 1（`IADR-0393`）は引数 1 つの 1:1 だった。
> 本 issue の 7 本は**追加引数を伴う**ため、写す前に「追加引数が詰め替えの材料か導出の指示か」を裁定する。
> 裁定は `IADR-0405` に残す（本仕様書はその適用）。

## 起点となる計画書（トレーサビリティ）

- 非機能要求（NFR）: バックエンド応用層のライブラリ標準化（#1064 の系列。**製品側の FR に当たる番号は無い**——
  メタ作業に当たる番号が無いことは `traceability.repo.md` が認めている）
- 関連 ADR: **ADR-0030**（応用層ライブラリ選定 §決定「マッピング = Riok.Mapperly」・選定基準 4
  「実行時リフレクションよりコンパイル時生成を優先する」）/ **ADR-0068 決定 2**（3 段の分割は
  「1 つの操作にしか使われないか」だけで決める）/ **ADR-0065 決定 2**（単一プロジェクト VSA）/
  **ADR-0063 決定 3〜5**（提案の承認資格。`CanDecide` の意味）/ **ADR-0033 決定 7・10**（AI 提案の状態と指紋）
- 実装 ADR: **IADR-0405（本 PR が起こす裁定）**／`IADR-0393` 決定 2 理由 A（波 1 で写像を入れなかった物差し）／
  `IADR-0371` 決定 3（参照実装・置き場）／`IADR-0290`（`DocumentVersion.MarkdownUri` を出さない）／
  `IADR-0364` 決定 4（`CanDecide` は行ごとに運ぶ）／`IADR-0195` 決定 1（生成物は `obj/` でカバレッジ対象外）／
  `IADR-0153` 決定 2（識別子 → 表示名の変換点を 1 つに閉じる）／`IADR-0282` 決定 1（Domain にアダプタを置かない）／
  `IADR-0385`（Keycloak の集合値属性）／`IADR-0270` 決定 3（同期端末）／`IADR-0231` `IADR-0238`（`WarningsAsErrors` の先例）
- 🔴 **`ADR-0041`（Result 型の外部ライブラリ）は本 PR の射程に当たらない。** #1279 の起点欄には載っているが、
  本 PR は `Result` / `Error` を 1 箇所も経由しない（写像は DTO を直接返す）。**スコープに書かない。**
- 計画書リンク: `../project-planning/projects/microservices-platform/07_adr/`

## 🔴 着手前に自分で引き直した母集合（設計の数えを転記しない）

基点は `origin/develop` @ **`c1dfb1eb`**。`git rev-parse --is-shallow-repository` = **`false`** ——
履歴が打ち切られていないので `git log` を出典に使える。

🔴 **`src/ai-stock-trading`（submodule）を初期化したうえで、走査からは外す。**
初期化しないと `Platform.Bff` がコンパイルできず platform ユニットのテストが静かに消える。
一方で初期化した状態のまま走査すると本リポジトリ外のヒットが混ざるため、**走査は必ず除外する**。

### 走査（対で陽性対照を置いた）

| 走査（`/obj/`・`/Tests/`・submodule を除外） | ヒット | 陽性対照 |
| --- | ---: | --- |
| `grep -rnE "static .* To[A-Z][A-Za-z]*\([^)]*,[^)]*\)" src --include=*.cs` | **8** | 同じ形の 1 引数版（`static partial .* To[A-Z]...(1 引数)`）が波 1 の 4 本を拾う ＝ 走査器は生きている |
| 複数行シグネチャ（`static .* To[A-Z][A-Za-z]*\($`） | **3**（`AiSuggestionEndpoints.ToDto` / `TagResolver.ToIdsAsync` / `GrpcService.ToProto`） | 同上 |
| `[Mapper]` を持つファイル | **4** | 波 1 の 4 本（Feedback / Authorization / McpServer / Notification）と一致 |
| `Riok.Mapperly` の `PackageReference` | **4 csproj**（＋ `Directory.Packages.props:112` の `4.3.1`） | 版は中央宣言済み ＝ `.csproj` に版を書かない |
| `RMG0` の重大度設定（`.props` / `.csproj` / `.editorconfig`） | **0** | `xUnit1051` の同種設定は `src/Directory.Build.props` に在る ＝ 走査語は妥当 |

**8 − 2（射程外）＋ 1（複数行）= 7。** 射程外の 2 本と理由:

| 箇所 | 除外理由 |
| --- | --- |
| `DataSourceService/Features/DataSources/DataSourceEndpoints.cs:48` `ToResponse` | **匿名型（`object`）を返す。** #1279 と #1230 が明示的に射程外と宣言している（写す先の型が無い） |
| `DocumentService/Infrastructure/Persistence/TagResolver.cs:21` `ToNames` | **DTO 写像ではない**（`List<Guid>` → `List<string>` の解決ヘルパ）。本 PR ではこれを**端で呼ぶ側**に回る |

### 波 1 の直接呼び出し（陽性対照つき）

`/Tests/` に限定した本 7 本の直接呼び出しは **0 件**（`ToVersionDto(` / `ToIdentityUser(` / `.ToResult(`）。
陽性対照として波 1 の 4 マッパを同じ形で引くと **ヒットする** ＝ 「0 件」は走査の失敗ではない。
つまり**移送に伴って既存テストが 1 本も壊れない**が、同時に**写像の列を直接見ている試験も 1 本も無い**
——これが本 PR で `<X>MapperTests` を新設する理由である。

### 7 本の内訳（呼び出し箇所を数え直した）

| # | 写像（基点の `path:line`） | 追加引数 | 呼び出す操作の数 | 判定 |
| --- | --- | --- | ---: | --- |
| 1 | `DocumentService/Features/Documents/DocumentEndpoints.cs:111` `ToDto` | タグ名の辞書 | **9**（AddTag / Archive / Create / GetById / List / Publish / PutBody / Update / UpdateMetadata。10 行） | ✅ 移す |
| 2 | 同 `:134` `ToVersionDto` | 同じ辞書 | **2**（GetVersion / ListVersions） | ✅ 移す |
| 3 | `DocumentService/Features/PrivateNotes/PrivateNoteEndpoints.cs:106` `ToDto` | 文書（`Document?`） | **4**（Create / List / Restore / SetExposure） | ✅ 移す |
| 4 | `DocumentService/Features/SyncDevices/SyncDeviceEndpoints.cs:49` `ToDto` | 現在時刻 | **1**（List） | ✅ 移す（**3 段目へ**） |
| 5 | `GraphService/Features/AiSuggestions/AiSuggestionEndpoints.cs:120` `ToDto` | 両端の文書名・承認可否 | **4**（Approve / Generate / List / Reject） | ✅ 移す |
| 6 | `RetrievalService/Infrastructure/ExternalServices/InMemoryVectorStore.cs:63` `ToResult` | スコア | 3（同一クラス内） | ❌ **残す（理由 E）** |
| 7 | `AuthorizationService/Infrastructure/ExternalServices/KeycloakIdentityAdminClient.cs:270` `ToIdentityUser` | ロール一覧 | 2（同一クラス内） | ❌ **残す（理由 A）** |

## 対象範囲

### 対象（5 本の移送 ＋ 裁定 ＋ 重大度の引き上げ）

| 新設 `[Mapper]` | 置き場（`ADR-0068` 決定 2 の適用） | 追加引数 |
| --- | --- | --- |
| `DocumentMapper`（`ToDto` / `ToVersionDto`） | **2 段目** `Features/Documents/` | `List<string> tags` |
| `PrivateNoteMapper.ToDto` | **2 段目** `Features/PrivateNotes/` | `string title, int version` |
| `SyncDeviceMapper.ToDto` | **3 段目** `Features/SyncDevices/List/` （**1 操作しか使わない**） | `bool active` |
| `AiSuggestionMapper.ToDto` | **2 段目** `Features/AiSuggestions/` | `string sourceDocumentTitle, string? targetDocumentTitle, bool canDecide = false` |

### 対象外（触らない。理由つき）

| 箇所 | 理由 |
| --- | --- |
| `InMemoryVectorStore.ToResult` | **理由 E（新設）**: 兄弟の `QdrantVectorStore.MapPayload` が `IReadOnlyDictionary<string, Value>` から写しており Mapperly では書けない。クラスの冒頭コメントが「両者が**同じ射影**を通す」ことを不変条件として宣言しているため、**片側だけ器を替えるとその保証が割れる**。加えて `Text` は 2 メンバの導出（`Excerpt(c.Text, c.HasBody)`）である |
| `KeycloakIdentityAdminClient.ToIdentityUser` | **理由 A（`IADR-0393` 決定 2 と同じ物差し）**: 6 メンバのうち 4 が導出（属性のキーごとの分岐・表示名の連結と縮退・`?? string.Empty`）。写しなのは `Enabled` と `Roles` の 2 つだけ。加えて源が `private sealed record` であり、2 列を写すために可視性を広げることになる |
| `DataSourceEndpoints.ToResponse` / `TagResolver.ToNames` | 母集合外（上表） |
| 各端点の呼び出し行（Documents / PrivateNotes） | **ラッパを端に残すので 0 行変わる**（`DocumentEndpoints.ToDto(d, names)` は署名のまま 1 式になる） |
| RMG020（源メンバ未使用）の error 化 | **しない。** 上げると `Document` の 8 メンバすべてに（実測） `[MapperIgnoreSource]` が要り、🔴 な省略（`MarkdownUri` / `TokenHash` / 指紋）が些事に埋もれる。波 1 が「意図した省略だけを宣言する」作法を採ったのを壊す |

## 設計（`IADR-0405` の適用）

### 決定 1: 線 —— 「材料」だけが `[Mapper]` に入る

追加引数が `[Mapper]` へ入れるのは、**そのまま 1 つの対象メンバに載る完成値**のときだけである。
演算子・メソッド呼び出し・`??` を伴わずにコンストラクタ引数として書けるか、が判定である。
辞書引き・`d.IsActive(now)`・`doc?.Title ?? ""`・2 メンバの合成・キーごとの分岐は**導出の指示**であり、
**それを今持っている端**（登録表 `<集約>Endpoints.cs`、または 3 段目の `Endpoint.cs`）に残す。
端のラッパは**1 式**であり、列の詰め替えを含まない。

### 🔴 ライブラリの実測（Riok.Mapperly 4.3.1。本 PR で再検証する）

| 事実 | 帰結 |
| --- | --- |
| 追加引数は**名前一致**（大文字小文字を無視）で対象メンバへ写る。`[MapProperty("param", …)]` による**改名は不可**（RMG006）、`"doc.Title"` のような**メンバ取り出しも不可**（RMG006） | **引数名は対象メンバ名にする**（決定 8）。改名したくなる引数（`now` / `names` / `doc`）は**指示である** |
| 一致しない追加引数は **RMG082 警告**のみ。名前の合わない対象メンバは **RMG012 警告**のみ。`TreatWarningsAsErrors=false` なので**どちらもビルドが通る** | 🔴 最悪ケース: タグ名の辞書を `names` という名前のまま渡すと `Tags = MapToListOfString(d.Tags)`（**GUID 文字列**）が黙って生成される。**緑のビルドで誤った本番データ** |
| null 許容の引数を非 null のコンストラクタ引数へ渡すと `?? throw new ArgumentNullException` が生成される（`?? string.Empty` にはならない） | `PrivateNoteEndpoints` の `?? string.Empty` / `?? 0` は**端に残す** |
| `RequiredMappingStrategy.Target` は RMG012 の重大度を上げない | 省略の可視化には使えない（**ただし RMG020 は黙る**。決定 7 で使う） |

→ **`src/Directory.Build.props` に `RMG012;RMG082` の `WarningsAsErrors` を 1 行足す**（決定 7）。

### 決定 2〜6（要旨。正本は `IADR-0405`）

- **決定 2**: 導出は今それを持っている端に残す。`[Mapper]` クラスには `partial` 宣言と `Use=` 変換だけを置く
  （非 `partial` のメソッドを `[Mapper]` へ入れない —— Mapperly は**型の組み合わせだけで**それを選ぶ）。
- **決定 3**: 認可の**判定結果の名**（`canDecide`）は写してよい。**判定**（`CanDecideAsync` / `ClaimsPrincipal` /
  `AccessScopeResponse` / `IsInRole`）は `[Mapper]` に入れない。
- **決定 4**: 時計は写像に入れない。`SyncDeviceMapper.ToDto(SyncDevice d, bool active)`、呼び出し側が
  `d.IsActive(now)` を済ませる。`Revoked` は 1 メンバの `Use=` 変換（波 1 `McpClientMapper` の型）。
- **決定 5**: 置き場は `ADR-0068` 決定 2 をファイル単位で当てる。**Infrastructure が使う写像は Infrastructure に置く**
  ——`scripts/check-unit-dependencies.js` 規則 3-③ は `Infrastructure → Features` を禁じる。Domain へも置かない
  （`Riok.Mapperly.Abstractions` の属性を Domain に付けることになる。`IADR-0282` 決定 1）。
  **本 PR では 6・7 を残すので実際の移動は起きない**——これは 8 例目のための規則である。
- **決定 6**: `MarkdownUri` の省略を 3 層で可視にする ——
  `[MapperIgnoreSource(nameof(DocumentVersion.MarkdownUri))]` ＋ RMG012 の error 化 ＋ 反射試験
  `Dto_HasNoMarkdownUriMember`（DTO に戻せば**ビルドが赤**になる）。

### 母集合の取り方（`traceability.repo.md` 規則 9・10）

- **規則 9**（追随する文書を記憶で挙げない）: 「本 PR で新たに誤りになる自分の記述」を、
  **誤りの側の文字列で走査してから**挙げた。走査語は `[Mapper]`（4 → 8 ファイル）、
  `Riok.Mapperly` の `PackageReference`（4 → 6 csproj）、`static .* To[A-Z]...(2 引数)`（8 → 3）である。
- **規則 10**（導出値は計算し直す）: 手書き写像の残数は**走査ではなく計算**する ——
  母集合 7 のうち移すのが 5、残すのが 2。波 1 が残した `CitationMapper.ToCitations` を足して **3 本**である。
- **除外理由**: `IADR-0393` は「波 1 で入れなかった理由 A〜D」を持つが、**件数は書いていない**ので追記は要らない。

## 受け入れ基準

- [x] Given 母集合 / When 着手する / Then **基点で自分で走査し、陽性対照を対で置いている**（本仕様書 §母集合）
- [x] Given 「先に決めること」3 点 / When 着手する / Then **裁定が `IADR-0405` に残っている**
- [x] Given DTO ↔ ドメインの写像 5 本 / When 実装を読む / Then Mapperly の生成マッパを使っている
- [x] Given 移送した写像 / When 移送前後の応答を比べる / Then **列の値も並びも同じ**である
- [x] Given 各サービスのテスト / When 実行する / Then **件数が減っていない**（削除・skip 化は 0 件。実数を PR に書く）
- [x] Given 変異試験 / When 写像の列を取り違える・追加引数を綴り違える・`MarkdownUri` を DTO へ戻す /
      Then **赤（またはビルドエラー）になることを実測**している
- [x] Given `RMG012;RMG082` の error 化 / When 既存 4 プロジェクト ＋ 新規 2 プロジェクトをビルドする /
      Then **緑である**（弱めない。壊れたら報告する）
- [x] Given 残した 2 本 / When 読む / Then **理由（E / A）が `IADR-0405` にあり、現場にも指し示すコメントがある**
- [x] Given `check-coverage-floor.js` / When 回す / Then 緑である（生成物は `obj/` へ出る）
- [x] Given `dotnet build` × 2 / `dotnet test` × 2 / `dotnet format --verify-no-changes` / 検査器 ＋
      `REQUIRE_REPO_TESTS=1 scripts.test.js` / When 実行する / Then 成功する

## テスト方針

| 試験 | 何を見るか | 赤にする変異 |
| --- | --- | --- |
| `<X>MapperTests.ToDto_CopiesEveryProperty`（4 マッパ） | 対象メンバを**1 つずつ**。追加引数の列（`Tags` / `Title`・`Version` / `Active` / 3 つ）も含む | 源メンバの改名・`[MapProperty]` の削除 → RMG012（決定 7 で**エラー**） |
| `ToDto_KeepsNullOptionalFields` | `ContentHash` / `DeletedAt` / `PurgeAt` / `LastSyncAt` / `TargetDocumentTitle` / `ChangeNote` が null のまま | `?` 引数を非 null にして `?? ""` を足す |
| `ToDto_ExtraArgIsCopiedVerbatim`（SyncDevice / AiSuggestion） | `active` / `canDecide` が両方向で写る。3 引数呼び出しで `CanDecide == false` | 追加引数の綴りを変える → RMG082（**エラー**） |
| `Revoked_IsTrueIffRevokedAtIsSet` | `Revoked` が `RevokedAt` に従う | `Use = nameof(IsRevoked)` を外す → 変換が無く RMG エラー |
| `DocumentEndpointsMappingTests.ToDto_ResolvesTagIdsToNames` | 端に残した導出（辞書引き）。未知 ID は落ちる | `d.Tags` をそのまま渡す → GUID 文字列になり赤 |
| `PrivateNoteEndpointsMappingTests.ToDto_WithoutDocument_DefaultsTitleAndVersion` | 端に残した `?? string.Empty` / `?? 0` | null をそのまま生成マッパへ渡す → `ArgumentNullException` |
| `Dto_HasNo<X>Member` 反射試験（`DocumentVersionDto.MarkdownUri` / `SyncDeviceDto.TokenHash` / `SyncDeviceDto.OwnerId` / `PrivateNoteDto.OwnerId` / `AiSuggestionDto.SourceFingerprint`） | 契約側の事実 | DTO にメンバを足す → 試験が赤（かつ RMG012 でビルドも赤） |
| 既存の端点試験（`SyncDeviceTokenTests` / `TagSuggestionApprovalTests` / DocumentService の各試験） | 応答の等価性 | 変更しない。**移送の等価性オラクル**である |


---

## 実測（着地時。押す直前に数え直した）

### 変異試験（すべて実物のコードを変異させて測った）

| # | 変異 | 実測の結果 |
| --- | --- | --- |
| M1 | `AiSuggestionMapper` の `sourceDocumentTitle` を `sourceTitle` へ改名 | **error RMG082** ＋ **error RMG012**（ビルド赤） |
| M2 | 同じ変異に `-p:NoWarn=RMG012;RMG082` を付ける | **0 エラー・0 警告**。生成物は `SourceDocumentTitle` を**既定の `""`** で組み、呼び出し側が渡した題が消える —— 🔴 **抑止は `WarningsAsErrors` に勝つ** |
| M3 | `[MapProperty("sourceTitle", nameof(AiSuggestionDto.SourceDocumentTitle))]` を足す（改名の試み） | **error RMG006**（追加引数は改名できない） |
| M4 | `AiSuggestionDto` に `SourceFingerprint` を足し直す | **error RMG012**（`[MapperIgnoreSource]` した源が対象を埋められない） |
| M5 | `DocumentVersionDto` に `MarkdownUri` を足し直す | **error RMG012**。`[MapperRequiredMapping(RequiredMappingStrategy.Target)]` を併記していても出る |
| M6 | `DocumentMapper.ToDto` の引数を `List<string> tags` → `IReadOnlyDictionary<Guid,string> names` へ | **error RMG082**。抑止すると生成物は `Tags = MapToListOfString(d.Tags)`（**GUID 文字列**） |
| M7 | `[MapperIgnoreSource(nameof(Document.Tags))]` を外す（設計 probe の主張の再現） | ⚠ **再現しない。** 4.3.1 は**名前の一致する追加引数を源メンバより優先**し、`Tags = tags` のままである。危険なのは M6（引数名が対象メンバ名と違うとき）であって、属性の有無ではない |

### テスト件数（`dotnet test` の実数。submodule は `Platform.Bff.Tests` の中に入るので別掲しない）

| プロジェクト | 前 | 後 | 差 |
| --- | ---: | ---: | ---: |
| `DocumentService.Tests` | 380 | 400 | **+20** |
| `GraphService.Tests` | 463 | 471 | **+8** |
| `RetrievalService.Tests` | 197 | 197 | 0（コメントのみ） |
| `AuthorizationService.Tests` | 201 | 201 | 0（コメントのみ） |

**削除・skip 化は 0 件。** 他の 15 プロジェクトは 1 件も動いていない。

### 導出値（走査ではなく数え直した）

- `[Mapper]` を持つクラス: **4 → 8**（`grep -rnE "^\[Mapper\]$"` で数えた。🔴 **語で数えると 11 になる** ——
  本 PR が追加した説明文の中の `[Mapper]` を 3 件拾うためである。**構文で数える。**）
- `Riok.Mapperly` の `PackageReference` を持つ csproj: **4 → 6**
- 手書きの写像: **8 → 3**（残した 2 ＋ 波 1 の `CitationMapper.ToCitations`）。
  端に残ったラッパ 3 本（`DocumentEndpoints.ToDto` / `.ToVersionDto` / `PrivateNoteEndpoints.ToDto`）は
  **1 式で列の詰め替えを含まない**ので手書き写像には数えない。

### 設計との差分（自分で測って変えたところ）

1. **M7**: 設計 probe の「`[MapperIgnoreSource]` を外すと源の `Tags` が採られる」は本リポジトリの形では
   再現しなかった。危険の引き金は**引数名の不一致**である。コードのコメントと `IADR-0405` V6 を実測に合わせた。
2. **RMG020 の扱い**: 設計は「警告のまま放置」でよいとしたが、`DocumentMapper` を入れると
   **新規に 8 件の警告が出る**（実測）。`check-backend-libraries.js` が記録する
   「赤（警告）の常態化は無視する学習を生む」に反するので、`[MapperRequiredMapping(RequiredMappingStrategy.Target)]`
   を**メソッドに 1 つ**付けて「部分射影である」と宣言し、`[MapperIgnoreSource]` は
   🔴 な省略の合図に取っておく（`IADR-0405` 決定 7）。**新規の警告は 0 件になった。**
   🔴 この属性は**クラスには付かない**（`error CS0592`）。
