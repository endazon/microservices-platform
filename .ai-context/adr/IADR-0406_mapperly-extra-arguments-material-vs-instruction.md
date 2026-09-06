---
title: IADR-0406 追加引数のある写像は「材料」だけを [Mapper] へ渡し「導出の指示」は端に残す —— 7 本のうち 5 本を移し 2 本を理由つきで残す
type: impl-adr
status: Accepted
related_ids: [NFR, FR-06, FR-09, FR-18, FR-19, FR-20, UC-03, UC-11, SC-03, SC-19, SC-20, SC-21, ADR-0030, ADR-0033, ADR-0036, ADR-0063, ADR-0065, ADR-0068, IADR-0153, IADR-0195, IADR-0231, IADR-0238, IADR-0270, IADR-0282, IADR-0290, IADR-0358, IADR-0364, IADR-0371, IADR-0385, IADR-0393, IADR-0395]
author: Claude（実装）
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md §決定（マッピング = Riok.Mapperly）・選定基準 4
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0063_tag-suggestion-reflection-and-approval-authz.md 決定 3〜5
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
---

# IADR-0406: 追加引数のある写像は「材料」だけを `[Mapper]` へ渡し、「導出の指示」は端に残す（#1279）

- 状態: Accepted
- 日付: 2026-09-06
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: **ADR-0030 §決定**（マッピング = Riok.Mapperly。選定基準 4「実行時リフレクションより
  コンパイル時生成を優先する」）／**ADR-0068 決定 2**（3 段の分割は「1 つの操作にしか使われないか」だけで決める）／
  ADR-0065 決定 2（単一プロジェクト VSA）／ADR-0063 決定 3〜5（承認資格）／ADR-0033 決定 7・10（AI 提案）／
  ADR-0036（個人資料は本人のみ）／FR-06・FR-09・FR-18・FR-19・FR-20・UC-03・UC-11・SC-03・SC-19・SC-20・SC-21
- 🔴 **`ADR-0041`（Result 型の外部ライブラリ）は本 ADR の射程に当たらない。** #1279 の起点欄には載っているが、
  移した 5 本は `Result` / `Error` を 1 度も経由しない。**適用しない ID をスコープに書かない。**
- 起点 issue: **#1279**（親 #1248 / #1230 / #1064。環流 planning#490）
- 先行する実装 ADR:
  [IADR-0393](./IADR-0393_backend-stack-rollout-wave1.md)（波 1。決定 2 理由 A が本 ADR の物差しの元である）／
  [IADR-0371](./IADR-0371_backend-stack-reference-implementation.md) 決定 3（参照実装・置き場）／
  [IADR-0395](./IADR-0395_backend-stack-wave2-badrequest-validation.md)（波 2 第 1 弾。「位置を動かせない時点で入力検証ではない」の同型）／
  [IADR-0290](./IADR-0290_version-response-drops-body-reference.md)（`DocumentVersion.MarkdownUri` を出さない）／
  [IADR-0364](./IADR-0364_tag-suggestion-reflection-and-dictionary-enforcement.md) 決定 4（`CanDecide` を行ごとに運ぶ）／
  [IADR-0195](./IADR-0195_coverage-exclude-source-generator-output.md) 決定 1（生成物は `obj/` でカバレッジ対象外）／
  [IADR-0153](./IADR-0153_tag-identity-storage-and-projection.md) 決定 2・4（識別子 → 表示名の変換点を 1 つに閉じる）／
  [IADR-0282](./IADR-0282_single-project-vsa-structure.md) 決定 1（Domain にアダプタを置かない）／
  [IADR-0358](./IADR-0358_bodyless-document-metadata-point.md) 決定 3（本文なしの点の射影）／
  [IADR-0385](./IADR-0385_set-valued-user-attribute-encoding.md)（集合値属性）／
  [IADR-0231](./IADR-0231_xunit-v3-simultaneous-switch.md) / [IADR-0238](./IADR-0238_xunit1051-staged-adoption-ratchet.md)（`WarningsAsErrors` の先例と罠）
- 関連する実装仕様書: `.ai-context/specs/20260906_issue-1279_mapperly-extra-arguments.md`

## コンテキストと課題

波 1（`IADR-0393`）で Riok.Mapperly を入れた 4 本は、いずれも**引数 1 つの 1:1 の詰め替え**であった。
#1279 の 7 本は**追加引数を伴う**。基点 `c1dfb1eb`（`git rev-parse --is-shallow-repository` = `false`）で
引き直した母集合は issue の表と一致する（走査と陽性対照は仕様書 §母集合）。

決めるのは **「追加引数が詰め替えの材料か、導出の指示か」の線**である。`IADR-0393` 決定 2 理由 A が
「採番と縮退を持ち込むと生成規約の外の手書きが `[Mapper]` の中へ戻る」と書いており、**同じ物差しを当てる。**

### 🔴 着手前に自分で実測したライブラリの挙動（Riok.Mapperly 4.3.1・net10.0・`TreatWarningsAsErrors=false`）

本リポジトリの実物（`AiSuggestionMapper` / `DocumentMapper`）を変異させて測った。設計時の使い捨て probe の
主張を**そのまま信じず引き直した結果、1 件は条件が違っていた**（V6）。

| # | 事実 | 測り方 |
| --- | --- | --- |
| V1 | 追加引数は**対象メンバ名との一致でしか**結び付かない。`[MapProperty("sourceTitle", …)]` による**改名は不可** | 属性を足すと **error RMG006** `Specified member sourceTitle on source type … was not found` |
| V2 | 一致しない追加引数は既定では **RMG082 警告 1 本**、名前の合わない対象メンバは **RMG012 警告 1 本**。**どちらもビルドは通る** | `-p:NoWarn=RMG012;RMG082` で **0 警告・0 エラー**（`WarningsAsErrors` は抑止に負ける） |
| V3 | 🔴 **その状態は「緑のビルドで誤った本番データ」である。** 引数名を `names` にすると生成物は `Tags = MapToListOfString(d.Tags)` ——**タグ名ではなく GUID 文字列** | 生成された `DocumentMapper.g.cs` を読んだ |
| V4 | 🔴 既定値つきのコンストラクタ引数はさらに静かである。引数名を 1 文字違えると `SourceDocumentTitle` は**既定の `""`** で組まれ、呼び出し側が渡した題は消える | 生成された `AiSuggestionMapper.g.cs` を読んだ |
| V5 | `[MapperIgnoreSource]` した源メンバを**対象 DTO に足し直すと RMG012 が出る**（`[MapperRequiredMapping(RequiredMappingStrategy.Target)]` を併記していても出る） | `DocumentVersionDto` に `MarkdownUri` を戻すと error RMG012 |
| V6 | ⚠ **設計 probe の「`[MapperIgnoreSource]` を外すと源の `Tags` が採られる」は本リポジトリの形では再現しない。** 4.3.1 は**名前の一致する追加引数を源メンバより優先する**。V3 が起きるのは**引数名が対象メンバ名と違うとき**である | 属性を外して再生成 → `Tags = tags` のまま |
| V7 | `RequiredMappingStrategy.Target` は RMG012 の**重大度を上げない**が、**RMG020（源メンバ未使用）は黙らせる**。属性はクラスではなく**メソッドにしか付かない** | クラスに付けると error CS0592。メソッドに付けると RMG020 が 8 → 0 |
| V8 | 波 1 の 4 プロジェクト ＋ 新規 2 プロジェクトは `RMG012;RMG082` の error 化で**すべて緑**（新規の警告も 0） | 両ユニットのフルビルド |

## 決定

### 決定 1: 線 —— `[Mapper]` へ渡してよい追加引数は「そのまま 1 つの対象メンバに載る完成値」だけである

**材料の条件は 3 つ。**（a）完成値であること、（b）**ちょうど 1 つ**の対象メンバにそのまま載ること、
（c）**引数名が対象メンバ名と一致する**こと。

**判定は 1 つの問いで済む: 演算子・メソッド呼び出し・`??` を伴わずにコンストラクタ引数として書けるか。**
書けなければ**導出の指示**であり、**それを今持っている端**（登録表 `<集約>Endpoints.cs`、または 3 段目の
`Endpoint.cs`）に残す。辞書引き（`TagResolver.ToNames(d.Tags, names)`）・メソッド呼び出し（`d.IsActive(now)`）・
縮退（`doc?.Title ?? ""`）・2 メンバの合成（`Excerpt(c.Text, c.HasBody)`）・キーごとの分岐は、すべて指示である。

🔴 **改名したくなる引数は指示である。** `now` / `names` / `doc` / 別の意味の `roles` —— 対象メンバの名前で
呼びたくない引数は、その値がまだ対象メンバの値になっていないことを意味する。**この線はライブラリ自身が
強制する**（V1: 改名は RMG006）—— B 案（本決定）の境界だけが機械で守られる。

**端に置くラッパは 1 式であり、列の詰め替えを含まない。** ラッパを `[Mapper]` クラスの中の非 `partial`
メソッドとして置くことは**しない** —— 波 1 が記録したとおり（`McpClientMapper`）、Mapperly は
**型の組み合わせだけで**ユーザー実装の写像を選ぶため、そこに置いた手書きは将来の別の写像に黙って使われる。

### 決定 2: 認可の**判定結果の名**は写してよい。**判定**は写さない

`AiSuggestionMapper.ToDto(..., bool canDecide = false)` が写すのは、契約が既に持つ語である
（`AiSuggestionDto.CanDecide`。ADR-0063 決定 3〜5 / `IADR-0364` 決定 4「資格はサーバが判定し `CanDecide` で
行ごとに運ぶ」）。**DTO が平のメンバとして持っている値を写すことを拒むのは、契約を写すことを拒むことである。**
写さないのは**判定**のほうで、`CanDecideAsync` / `ClaimsPrincipal` / `AccessScopeResponse` / `IsInRole` は
登録表に残る。**既定 `false` は deny 側**であり、`partial` メソッドの宣言側に置いた既定値が生成側へ効く（実測）。

### 決定 3: 時計は写像に入れない

`SyncDeviceMapper.ToDto(SyncDevice d, bool active)`。呼び出し側（`ListSyncDevicesEndpoint`）が
`d.IsActive(now)` を済ませる —— そこは既に `now` を持っている端である。`DateTimeOffset` や `TimeProvider` を
写像へ渡す形は生成マッパをテストから固定しづらくするうえ、**Mapperly ではそもそも書けない**
（追加引数は入れ子の写像へ渡らないため、`MapPropertyFromSource` の変換器から `now` を参照できない）。
`Revoked` は 1 メンバの `Use=` 変換であり、波 1 `McpClientMapper` と同じ型である。

### 決定 4: 置き場は `ADR-0068` 決定 2 をファイル単位で当てる

- `DocumentMapper`（9 操作 / 2 操作）・`PrivateNoteMapper`（4 操作）・`AiSuggestionMapper`（4 操作）は **2 段目**。
- `SyncDeviceMapper` は **3 段目**（`Features/SyncDevices/List/`）。⚠ **手書きだった頃とは場所が違う。**
  `ToDto` は 5 操作が使う登録表の中に居たが、**この写像を使う操作は一覧の 1 つだけ**である。
  決定 2 は**ファイル**について「1 つの操作にしか使われないか」だけで決めると定めているので、新しいファイルは
  3 段目に置く。**規則の適用結果であって漂流ではない。**
- 🔴 **Infrastructure が使う写像は Infrastructure に置く。** `scripts/check-unit-dependencies.js` 規則 3-③
  （`VSA_FORBIDDEN_TARGETS = { Domain: ['Features','Infrastructure','Common.Behaviors'], Infrastructure: ['Features'] }`）
  が `Infrastructure → Features` を禁じる。`Domain/` へも置かない ——
  `Riok.Mapperly.Abstractions` の属性を Domain へ付けることになる（`IADR-0282` 決定 1）。
  **本 ADR で残した 2 本はどちらも Infrastructure に在るので実際の移動は起きない。これは 8 例目のための規則である。**
- 生成物は `obj/…/Riok.Mapperly/…/*.g.cs` に出るので**カバレッジの床は動かない**（`IADR-0195` 決定 1）。

### 決定 5: `MarkdownUri` の省略は属性・ビルド・試験の 3 層で可視にする

`DocumentVersion.MarkdownUri` を出さない（#1011 / `IADR-0290`）という**載っていないことが仕様である**列は、
黙って落とすと事故と区別がつかない。3 層で守る:

1. `[MapperIgnoreSource(nameof(DocumentVersion.MarkdownUri))]` ——「源が持っていることを知ったうえで落とす」
   と書いた唯一の行。**これが無いと Mapperly は 1 件も診断を出さない**（対象に欄が無いので当然である）。
2. **RMG012 の error 化**（決定 6）—— `DocumentVersionDto` に欄を戻すと**ビルドが赤になる**（V5 で実測）。
   属性を消す明示の操作が要る。
3. `DocumentMapperTests.Dto_HasNoMarkdownUriMember` —— 契約側の事実そのもの。**源が実際に持っている**
   ことを陽性対照として同じ試験で見る（「落とすと決めた対象が実在する」）。

`[MapperRequiredMapping(RequiredMappingStrategy.Target)]` は重大度を上げないので、代替にはならない（V7）。

### 決定 6: RMG012 / RMG082 を `WarningsAsErrors` にする。RMG020 はしない

`src/Directory.Build.props` に **1 行**（`<WarningsAsErrors>$(WarningsAsErrors);RMG012;RMG082</WarningsAsErrors>`）。
V2〜V4 が示すとおり、この 2 つは**放っておくと「緑のビルドで誤った応答」になる**種類の警告である。

**xUnit1051（`IADR-0238`）と違って許可リストを要らない。** あちらは助言警告で既存 943 件の残件があったため
段階採用が要ったが、RMG012 / RMG082 は**本ファイルが届く範囲（AST submodule を含む）に既存の発生が 0 件**
である（V8）。残件ゼロなので「赤の常態化」は起きない。AST は Riok.Mapperly を参照していないため診断が生じない。

🔴 **`NoWarn` / `.editorconfig` の抑止は `WarningsAsErrors` に勝つ**（`IADR-0238` が xUnit1051 で実測した罠が
そのまま当てはまる。本 ADR でも V2 で再実測した）。**抑止を足さないこと。**

**RMG020（源メンバ未使用）は警告のままにする。** 上げると `Document` の 8 メンバすべてに
`[MapperIgnoreSource]` が要り、🔴 な省略（`MarkdownUri` / `TokenHash` / 指紋 / `OwnerId`）が些事に埋もれる
—— 波 1 が `NotificationMapper` で採った「**意図した省略だけを宣言する**」作法が壊れる。

### 決定 7: 「部分射影である」ことは 1 回だけ宣言する（`[MapperIgnoreSource]` を薄めない）

決定 6 の裏返しとして、**源の多くを出さない写像は `[MapperRequiredMapping(RequiredMappingStrategy.Target)]`
をメソッドに 1 つ付けて「これは部分射影である」と宣言する**（`DocumentMapper` の 2 本）。8 個の
`[MapperIgnoreSource]` を並べるのと結果は同じだが、**`[MapperIgnoreSource]` は「これを出さないと決めた」
という個別の合図に取っておく**ほうが読み手に効く。RMG012（対象側の取りこぼし）は宣言しても効いたままである（V5）。

### 決定 8: 移したもの・残したもの（理由 E は新設）

| 写像 | 判定 | 置き場 | 端に残る導出 | 理由 |
| --- | --- | --- | --- | --- |
| `DocumentEndpoints.ToDto` | ✅ 移す | 2 段目 `Features/Documents/DocumentMapper.cs` | `TagResolver.ToNames(d.Tags, names)` | 辞書 = 引きの指示、解決済みの `List<string>` = 材料 |
| `DocumentEndpoints.ToVersionDto` | ✅ 移す | 同上 | 同上 | 同上。あわせて `MarkdownUri` の省略を可視化（決定 5） |
| `PrivateNoteEndpoints.ToDto` | ✅ 移す | 2 段目 `Features/PrivateNotes/PrivateNoteMapper.cs` | `doc?.Title ?? ""` / `doc?.Version ?? 0` | null 許容の**オブジェクト**は写像で取り出せず（RMG006）、null 許容の**スカラ**を渡すと `?? throw` が生成される。縮退は端の判断 |
| `SyncDeviceEndpoints.ToDto` | ✅ 移す | **3 段目** `Features/SyncDevices/List/SyncDeviceMapper.cs` | `d.IsActive(now)` | 時計 = 指示、`active` = 材料（決定 3・4） |
| `AiSuggestionEndpoints.ToDto` | ✅ 移す | 2 段目 `Features/AiSuggestions/AiSuggestionMapper.cs` | なし（3 引数とも既に完成値） | 決定 2 |
| `InMemoryVectorStore.ToResult` | ❌ **残す** | — | — | **E（新設）: 射影の相手が Mapperly で書けない対であり、片側だけ器を替えると「同じ射影」の保証が割れる。** 兄弟の `QdrantVectorStore.MapPayload` は `IReadOnlyDictionary<string, Value>` からキーごとに引いて型を判定しながら組み立てており、生成マッパでは表せない。クラスの冒頭コメントが「Qdrant 実装と同じ射影を通す唯一の点」を不変条件として宣言している（`IADR-0358` 決定 3）。あわせて A: `Text` は 2 メンバの導出である |
| `KeycloakIdentityAdminClient.ToIdentityUser` | ❌ **残す** | — | — | **A（`IADR-0393` 決定 2 と同じ）: 導出が支配的。** 6 メンバのうち 4 が導出（属性のキーごとの分岐 = `IADR-0385`／表示名の連結と縮退／`Id`・`Username` の `?? string.Empty`）で、写しは `Enabled` と `roles` の 2 つだけ。移せば手書きが `[Mapper]` の中へ戻る。加えて源の `KeycloakUser` は `private sealed record` であり、2 列のために可視性を広げることになる |

🔴 **残した 2 本は「やり残し」ではない。** 現場（両ファイルの当該メソッドの直前）に理由へのポインタを置いた。

## 理由

- 決定 1 の B 案（材料のみ）を採ったのは、**境界をライブラリが機械で守る唯一の案**だからである。
  Mapperly が改名も取り出しもできない（V1）範囲が、そのまま「指示」の集合と一致する。
  「Mapperly で表せるものは何でも入れる」案（`MapPropertyFromSource` で 2 メンバ導出まで入れる）は、
  境界を毎回の趣味に戻し、`IADR-0393` 理由 A の状態を再生産する。
  「追加引数のある写像は一切移さない」案は判断を要らないが、`OwnerId` / `TokenHash` / 指紋の
  **非公開が暗黙のまま**残る —— 波 1 が明示的に離れた形である。
- 決定 6 を「同型の事故が 2 回起きたら」の規則の例外にしない: これは**新しい検査器ではなく、
  ピン留め済みパッケージに同梱のアナライザの重大度設定 1 行**である。

## 結果

- **手書き写像 8 → 3**（移した 5、残した 2、波 1 が残した `CitationMapper.ToCitations`）。
  Riok.Mapperly の `PackageReference` **4 → 6**（DocumentService・GraphService が加わる）。
  `[Mapper]` を持つファイル **4 → 8**。
- 応答の**列の値も並びも変わらない**（生成物を読んで確認した。値・順序ともに移送前と一致）。
- テスト件数は**減らない**（実数は PR 本文に記す）。既存の端点試験が移送の等価性オラクルである。
- トレードオフ: `SyncDeviceMapper` の置き場が 2 段目 → 3 段目へ動く（決定 4 の適用結果）。
- 🔴 **`NoWarn` / `.editorconfig` の抑止を足さないこと**が新しい暗黙の制約になった（決定 6）。
  xUnit1051 には `scripts/check-xunit1051-ratchet.js` が在るが、**RMG には検査器を置いていない** ——
  「同型の事故が 2 回起きたら」の規則に従い、**1 回目は記録に留める。**

## フォローアップ

- RMG020 の error 化は、同型の事故（意図した省略が黙って埋もれる）が 2 回目に起きた時点で判断する。
- 抑止の混入を止める検査器も同様（上記）。
- 8 例目が来たときは決定 1 の問い（「演算子・呼び出し・`??` なしでコンストラクタ引数に書けるか」）と
  決定 4 の置き場の規則をそのまま当てる。**新しい判断が要るのは、この問いで割り切れなかったときだけである。**
