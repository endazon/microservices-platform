---
title: IADR-0398 RFC7807（`Results.ValidationProblem`）系の手書き検証を FluentValidation へ移すとき、器は変えず辞書の生産側だけを換える —— 先頭 1 件（形 α）と全件（形 β）の 2 契約・鍵の明示・位置が 2 つある端点の RuleSet・端点入口の入力検証の境界（何を入れ、何を入れなかったか）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0030
  - ADR-0036
  - ADR-0041
  - ADR-0054
  - ADR-0058
  - ADR-0062
  - ADR-0063
  - ADR-0065
  - ADR-0068
  - ADR-0074
  - IADR-0117
  - IADR-0153
  - IADR-0229
  - IADR-0270
  - IADR-0282
  - IADR-0371
  - IADR-0393
  - IADR-0395
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (Accepted 2026-07-25) 決定・選定基準 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Accepted 2026-08-22) 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md 決定 1
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (fixed 2026-08-30) 基本方針・実装状況・Application 層
---

# IADR-0398: 波 2 の第 2 弾（RFC7807 系）で先に決めたこと（#1278）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0030`（ライブラリ標準 —— 検証は FluentValidation）／`ADR-0041`（Result 型と
  参照の向き）／`ADR-0065` 決定 2（単一プロジェクト＋VSA）／`ADR-0068` 決定 1（3 段のスライス分割規則）／
  `ADR-0054`・`ADR-0058`（doc_scope の値域と不変性）／`ADR-0036` D-06/D-07（所有・共有）／
  `ADR-0062`・`ADR-0063`（無人アカウントの属性部分集合・タグ反映の認可）／`ADR-0074`（写像表の器）／
  `NFR`（無採番。ライブラリ標準の浸透は保守性の非機能要件であり、計画側の要求表に当たる番号が無い）
- 関連する実装 ADR: `IADR-0371`（参照実装の置き方。決定 1・2・4）／`IADR-0393`（波 1。理由 C が
  本件の起源）／`IADR-0395`（波 2 第 1 弾。決定 2・3・5・6・7・8 を物差しとして再利用する）／
  `IADR-0229` 決定 1（`Error` を複数持つ表現を導入しない）／`IADR-0117`（Kernel のユニット外参照）／
  `IADR-0270`（doc_scope）／`IADR-0153`（タグの識別子参照）／`IADR-0282`（単一プロジェクト＋VSA）
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1278_rfc7807-validation-documents.md`
- issue: #1278（親 #1248 / #1230 / #1064 / 環流 planning#490）

## コンテキストと課題

`IADR-0393` 理由 C・`IADR-0395` 決定 9 が「群 3」として波 2 へ送った、RFC7807
（`Results.ValidationProblem`）で 400 を返す手書きガード節が対象である。

着手時に基点 `origin/develop` @ `4eff9bb4` で母集合を引き直した
（`git rev-parse --is-shallow-repository` = `false`。`git log` を出典に使える）。

| 走査 | 実測 | 備考 |
| --- | ---: | --- |
| `ValidationProblem` の非テスト行 | 46 | うちコメント 9・ヘルパ**定義**行 4 を除いた**呼び出し**が 37 |
| McpServer の私有 sink `Problem(` の呼び出し | 6 | 軸 1 では落ちる（`Results.` が付かない） |
| `Results.BadRequest` の非テスト行（陽性対照） | 18 | 波 2 第 1 弾が残した箇所が拾える ＝ 走査器は生きている |
| FluentValidation の `PackageReference`（陽性対照） | 6 csproj | `IADR-0395` §結果「4 → 6」と一致 |
| `AddScoped<IValidator<` の `Program.cs` 登録行（陽性対照） | 11 行 | 本 PR 適用後は 19 行（うち DocumentService 8）。`AddValidatorsFromAssembly` は 0 件 |

呼び出し 37 は #1278 の数えと一致する（**転記ではなく再計算**）。

### 🔴 #1278 の前提は 26 箇所について逆である

#1278 は群 3 を一律に「**全違反を返す** RFC7807 なので `IADR-0371` 決定 2 の `Errors[0]` 規約は
当てはまらない」と書く。**基点で各サイトの振る舞い（字面ではなく応答）を読むと 2 種類ある。**

- **形 α（最初の違反 1 件を 1 つの鍵で返す）＝ 26 箇所。** ガード節ごとに `return` するため、
  辞書は常に 1 鍵 1 メッセージである。**DocumentService の全ガードがこれ**である。
  実測: `Features/Documents/Create/Endpoint.cs:34-38` は `title` を返して**その場で `return` する**ので、
  続く機密区分（`:47`）・doc_scope（`:54`）・個人資料経路（`:56`）へ**到達しない**。
- **形 β（全件を鍵つきバケットへ）＝ 11 箇所。** AuthorizationService 6 / McpServer 2 /
  DataSourceService 2 / NotificationService 1。ドメイン関数が `List<string>` を返し、全件を載せる。

→ **26 箇所では `IADR-0371` 決定 2「`Errors[0]` を採る」「宣言順が応答の契約」がそのまま持ち越せる。**
#1278 の枠を転記して `result.ToDictionary()` で写していたら、**複数違反の要求で鍵が増え、
応答本文が変わっていた**（本 ADR 決定 1 がそれを閉じる）。

ガード単位ではヘルパ 3 本を呼び出し 8 行へ展開して **42**（移す 26 / 残す 16）である。

## 検討した選択肢

### 応答の契約（決定 1）

| 案 | 移送前後のバイト同一性 | 等価性をどこで固定するか | `IADR-0371` との整合 | 判定 |
| --- | --- | --- | --- | --- |
| A. 形 α も `ToDictionary()` ＋ `ClassLevelCascadeMode = Stop` | ○（最初の失敗規則だけ残る） | **検証器の設定値 1 行**。外すと件数が増えるが端点は同じ | △ `Errors[0]` の読み方が消える | ✗ 等価性が設定 1 行に載り、消してもコンパイル・起動が通る |
| **B. 形 α は `Errors[0]` ＋ `PropertyName`、形 β は `ToDictionary()`** | ○ | **端点の写像 1 行**と検証器の宣言順。単体試験が全違反を見て順序を固定できる | ○ そのまま持ち越す | **採用** |
| C. Kernel の `Result` / `Error` を経由 | ✗ `Error` は `Message` 1 つ。鍵も複数件も運べない | – | ✗ `IADR-0229` 決定 1 に反する | ✗ |
| D. 全サービスを `errors` バケットへ統一 | ✗ DocumentService の鍵 6 種と Mcp の `request` が変わる | – | – | ✗ 「振る舞いを変えない」制約の外 |

### 位置が 2 つある端点（決定 3）

| 案 | 位置を保てるか | DI 鍵 | 実装量 | 判定 |
| --- | --- | --- | --- | --- |
| A. 全規則を入口で 1 回 | ✗ `Create`: 題名あり・本文 1 MB 超・機密区分なし → **413 が 400 に化ける** | ○ | 小 | ✗ 振る舞いが変わる |
| B. 位置ごとに検証器を分ける | ○ | ✗ **同じ `IValidator<CreateDocumentRequest>` が 2 つ**（`IADR-0395` 決定 3 が退けた衝突） | 中 | ✗ |
| C. 後段の規則を別の要求型へ起こす | ○ | ○ | 中〜大。3 操作で属性規則を共有するのに Create だけ追加規則があり型が割れる | ✗ 過剰 |
| D. 後段の規則を端点に残す | ○ | ○ | 最小 | ✗ `Create` は 4 規則中 3 規則が後段。**移したことにならない** |
| **E. `RuleSet` で位置ごとの集合を名付ける** | ○ | ○ | 小 | **採用** |

## 決定

### 決定 1: 器は変えず、辞書の生産側だけを換える。形 α は `Errors[0]` を鍵つきで、形 β は `ToDictionary()`

移送後の端点も同じ `Results.ValidationProblem(IDictionary<string, string[]>)` を呼ぶ。
変わるのは渡す辞書を誰が作るかだけである。

- **形 α**: `var f = result.Errors[0]; Results.ValidationProblem(new Dictionary<string, string[]> { [f.PropertyName] = [f.ErrorMessage] })`。
  `IADR-0371` 決定 2「宣言順が応答の契約」「`Errors[0]` を採る」を**そのまま持ち越す**。
  持ち越さないのは `{ "error": "..." }` という 1 欄の器だけで、代わりに**鍵**が契約に加わる。
- **形 β**: `Results.ValidationProblem(result.ToDictionary())`（`ToDictionary()` は `PropertyName` で
  群化し、群の出現順と群内のメッセージ順をどちらも保つ）。
- 🔴 **鍵は必ず明示する**（`OverridePropertyName(<internal const>)` または `Custom` 内の
  `AddFailure(<internal const>, message)`）。**推論名を使わない。** 実測: `RuleFor(r => r.Title)` の
  推論名は `Title`（移送前の鍵は `title`）である —— 本 PR の変異試験で HTTP 応答が
  `{"errors":{"Title":["タイトルは必須です。"]}}` になることを確認した。型では止まらない。
  しかも SPA の `parseProblemDetails`（`apiClient.ts`）は `errors` の値を鍵に関係なく平坦化するので
  **画面では発見できず、機械クライアントだけが壊れる**。
- **鍵の出どころは 2 通りあり、混ぜない。** (a) **サイトごとに鍵が違う** DocumentService →
  検証器の `PropertyName` が鍵。(b) **サービスの sink が鍵を持つ** AuthorizationService（`errors`）／
  McpServer（`request`）→ 端点は既存の sink へ `Errors[0].ErrorMessage` を渡し、**検証器は鍵を持たない**
  （持たせると鍵の正が 2 つになる）。

### 決定 2: これら 4 サービスに `Platform.Shared.Kernel` は参照させない

- `Error(string Code, string Message, ErrorKind Kind)` は**メッセージを 1 つしか持たない**。
  `IADR-0229` 決定 1 が「`Error` を複数持つ表現を導入しない」と定めている。
  **形 β はそもそも載らず、形 α でも鍵を運ぶ場所が無い。**
- `IADR-0393` 理由 D／`IADR-0371` 決定 4: 失敗を `Result` で表す経路が無いサービスへ参照だけ足すのは、
  適合しているように見えるだけである。
- **#1278 の受け入れ基準「`Result` / `Error` を使うサービスの `.csproj` は Kernel を参照している」は
  条件節が偽であり、空真で満たす。** 監査が「Kernel 0/4 は退行」と読まないよう、ここに明記する。
- 🔴 トレードオフ: `IADR-0371` 決定 1 の「3 ライブラリの噛み合い方が 1 スライスに揃って見える」形は
  再現しない。**これは計画の義務ではない**（同決定 1 の義務は「その関心を実装する箇所で標準ライブラリを
  使うこと」である）。

### 決定 3: 同じ要求型に検証の位置が 2 つある端点は `RuleSet` で位置ごとの規則集合を名付ける

`Create`（題名が入口、属性 3 規則が **413 の後ろ**）は、**1 つの検証器・1 つの DI 鍵**のまま、
入口の規則を既定集合に、後段の規則を `RuleSet(<internal const>)` に置く。端点は位置ごとに
`Validate(req)` と `Validate(req, o => o.IncludeRuleSets(<const>))` を呼ぶ。

- 🔴 **ハザード**: `Validate(req)`（オプション無し）は**名前つき集合を走らせない**。第 2 の呼び出しを
  消しても端点はコンパイルも起動も通り、後段の規則だけが**黙って無検証**になる
  （`IADR-0395` 決定 5 の `Guid.Parse` と同型の「型で守れていない」依存）。
  → **位置の対の試験で固定する**（「題名あり・本文超過・機密区分なし → 413」と
  「題名あり・本文適正・機密区分なし → 400 `confidentiality`」。本 PR で実測: 第 2 の呼び出しを
  消すと 10 試験、属性規則を `RuleSet` の外へ出すと 6 試験が赤になった）。
- 集合名は検証器の `internal const string` に置き、端点はその定数を使う（文字列を 2 箇所に書かない）。
- 同じ形は `ObsidianSync/Push`（`baseVersion` が 404 の後ろ）にも要る —— **適用は後続 PR**。

### 決定 4: 3 操作が共有する属性規則は `Features/Documents/DocumentAttributeRules.cs`（2 段目）の拡張メソッド 1 組に置く。タグ名の規則は複製のまま写す

`ConfidentialityProblemOrNull` / `DocScopeProblemOrNull` は Create / Update / UpdateMetadata の
3 操作が呼ぶ**単一の判定**である。移送後もその性質を保つため、`IRuleBuilder<T, Dictionary<string,string>?>`
への拡張メソッド `.Confidentiality()` / `.DocScope()` を 1 組だけ置き、3 つの検証器がそれを呼ぶ。
**旧ヘルパ 2 本は削除した**（呼び出し元がすべて移った）。`DocScopeChangedProblemOrNull` は残る。

- 置き場が 2 段目なのは `ADR-0068` 決定 1（3 段目へ下ろすのは「その操作の処理」。集約の複数操作が
  使うものは 2 段目に残す）の適用であり、**旧ヘルパと同じ場所**である。
- 実体は `Domain/DocumentAttributes` の関数のままである（**Domain は FluentValidation に依存しない**。
  `check-backend-libraries.js` の Domain 外部依存ゼロ規則。橋渡しの拡張メソッドを `Features/` に置く）。
- 🔴 述語は `Custom` で書く。`Must` ＋ `WithMessage(func)` だと同じ検証関数を 2 度呼び、失敗時の
  メッセージが**2 度目の呼び出し**由来になる。`Custom` なら 1 度で済み、鍵も同じ行に書ける。
- **タグ名の規則は共有しない。** 移送前も 3 箇所（`Documents/AddTag` / `Tags/Create` / `Tags/Rename`）が
  同じ 4 行を**それぞれ**書いている。移送で共有化すると「振る舞いを変えない」枠を超える整理になる。
  **3 つの検証器がそれぞれ定数を持つ**（同じリテラル `"タグ名は必須です。"`）。共有は別の変更でやる。

### 決定 5: McpServer の `kind` / `egressTier` は検証と解析を分け、対応表を 1 つに保つ

`TryParseKind` / `TryParseTier` は移送前も**検証と解析が同じ関数**である。検証器は
`Must(v => RegisterMcpClientEndpoint.TryParseKind(v, out _))` で**形式だけ**を見て、端点は検証通過後に
同じ関数で解析する。2 関数を `private` → `internal` にして**対応表を 1 つに保つ**（片方だけ変えると
「検証は通るが解析で落ちる」）。**適用は後続 PR。**

### 決定 6: `OwnerMappingValidation` は丸ごと端点側に残す。DataSourceService から本 issue で動くものは無い

`IADR-0395` 決定 6 の物差しを 3 点とも当てた結果、`ConnectionUriPolicy` と同じ側に落ちる。

1. **位置を動かせない。** `Update` では入口の検証器 → `FindAsync` → `ConnectionUriPolicy` の**後ろ**に
   居るため、形式検査だけを既存の検証器へ足すと**不存在 ID ＋ 不正な写像表が 404 から 400 に化ける**。
2. **述語が 2 段で 1 関数である。** 「形式 → 正規化 → 名簿取得 → **引けなければ 502** → 実在」を
   1 本で書き、**3 操作（Create / Patch / Update）が同じ 1 本を呼ぶ**ことが設計である。形式だけを
   3 つの検証器へ写すと、そのファイル自身が警告している穴（「登録では弾くのに PATCH では通る」）を自分で開ける。
3. **502 は `ValidationResult` で表せない。** `ValidationFailure` は状態コードを持たない。
   「確かめられなかった」を「存在しない」と混ぜないことが要件であり、検証器の器では守れない。

**`IPlatformUserDirectory` は `Domain/Ports` にあるので、仮に `Features/` の検証器が注入しても
`check-unit-dependencies.js` 規則 3-③ には触れない。** #1278 の「Infrastructure 依存が入る」は
正確には「外部呼び出しを伴う非同期規則が入る」であり、**退ける理由は依存方向ではなく上記 2・3 である**。

### 決定 7: NotificationService は射程内。検証器は端点ではなく `NotificationIngress` に注入する

`IADR-0371` 決定 1 の義務は「**その関心を実装する箇所**で標準ライブラリを使うこと」である。
`NotificationIngress.AcceptAsync` は要求 DTO の入力検証を先頭・DB より前で行い、端点は結果を
透過するだけである。**関心の在り処が端点でないことは射程を外す理由にならない。**

- **`request is null` は残す**（FluentValidation は null インスタンスを検証できない。
  「本文が無い」は DTO の検証ではなく束縛の失敗である）。
- **形 β・属性別の鍵。** 移送前は鍵ごとに `else if` で 1 件だけ入れるので、**規則レベルの
  `Cascade(CascadeMode.Stop)`** で写す。🔴 述語の粒度: 空白 300 文字の `subject` は移送前
  「必須」の 1 件だけである。`Stop` を外すと 2 件になり件数が変わる。**適用は後続 PR。**

### 決定 8: 残すものとその理由。sink ヘルパの行方

| 箇所 | 理由（`IADR-0395` 決定 7 の分類） |
| --- | --- |
| `DocumentEndpoints.UnknownTagsProblem`（呼び出し 4） | **後段の照会結果**（`TagResolver.ToIdsAsync` が辞書を引いた結果）。しかも**認可の後ろ**（辞書照合を先にすると書けない主体に情報が返る）。動かせない |
| `DocumentEndpoints.DocScopeChangedProblemOrNull` | **既存値**（`doc.Attributes`）が要る。`FindAsync` の後ろ |
| `PrivateNotes/SetQuota` | **例外由来**。述語もメッセージも `Domain/PrivateNote` の**不変条件**が持つ。写すと不変条件が 2 箇所になる |
| `Tags/Create`・`Tags/Rename` の重複（409） | DB の照会結果。状態コードも違う |
| McpServer の重複登録（`AnyAsync`）／`RejectUnassignableAsync` | 照会結果／登録と差し替えが**同じ 1 関数**を呼ぶことが統制（`ADR-0062` 決定 3） |
| AuthorizationService `AbacValidation` / `UserAssignmentValidation` 系 6 ＋ 競合捕捉 1 | 値域が DB / IdP から来るドメイン関数。形式と一意性が**1 つの配列**に積まれており、片方だけ移すと件数が変わる。`#535` の 3 経路共有もある |
| DataSourceService `OwnerMappingValidation` 2 | 決定 6 |
| NotificationService `request is null` | 決定 7 |

**sink ヘルパ 4 本（Authz 2 / Mcp 2）は生き残り、統合しない。** 同形の 2 本（Authz）が残ることは
**受容として記録する** —— 集約をまたぐ共有物の置き場を新たに決める整理は本 issue の「移送」ではない。
**DocumentService には sink が無いので `Features/ValidationProblems.cs`（`FirstViolation`。1 段目）を
1 つ足す** —— 5 集約が使うため 2 段目には置けず、`DocumentEndpoints` に置くと Tags / PrivateNotes /
SyncDevices の検証器が Documents 集約の合成点へ依存する形になる。

### 決定 9: 等価性は「状態コード ＋ 鍵の列 ＋ 各鍵のメッセージ列 ＋ 判定の位置 ＋ 述語の粒度」で固定する

`IADR-0371` 決定 2・`IADR-0393` 決定 3・`IADR-0395` 決定 8 に**鍵**を足す。

- **規則の宣言順を移送前のガード節の順に揃える**（形 α は `Errors[0]`、形 β は列全体が契約）。
- **鍵もメッセージも `internal const` / `static readonly` / 関数として検証器（または `Domain` の定数）が
  持ち、試験は定数とリテラルの両方へ当てる。**
- **述語を写す。** `string.IsNullOrWhiteSpace` を `NotEmpty()` に、`Tag.Normalize` 後の
  `IsNullOrEmpty` を `NotEmpty()` に置き換えない。`ShareSubjectType.IsValid` / `PolicyAction.IsValid` /
  `TryParseKind` は**同じ関数**を呼ぶ。
- 🔴 **述語の粒度を写す。** `GrantShare` の `subjectId ∨ subjectType` は 1 本の `||` のまま 1 規則にする
  （2 本へ割ると両方不正な要求で失敗が 2 件になる。本 PR で実測）。
- 🔴 **位置を試験で固定する。** 「不存在 ID ＋ 空題名 → 400」と「不存在 ID ＋ 妥当な入力 → 404」の**対**を
  各サイトへ置く（`RenameEdgeType` が「取得の**後ろ**」だったのと**逆向き**のサイトがある —— 名前が
  似ているからと揃えない）。
- **登録は 1 検証器 1 行の明示登録。** DocumentService は本 PR で 8 行増える。
- **既存の 400 の試験はほぼ状態コードしか見ていない**（本文の鍵を読むのは全リポジトリで 2 本だけ）。
  したがって**移す各サイトに鍵とメッセージを読む契約試験を足す。**

### 決定 10: PR は 4 本。本 ADR は 1 本目と同じ PR に置き、以後は適用に留める

| PR | 射程 | 検証器 | ガード |
| --- | --- | ---: | ---: |
| **PR-A（本 ADR と同じ PR）** | DocumentService `Documents` / `Tags` 集約 ＋ `ValidationProblems.cs` ＋ `DocumentAttributeRules.cs` | 8 | 14 |
| PR-B | DocumentService `PrivateNotes` / `ObsidianSync` / `SyncDevices` | 5 | 7 |
| PR-C | McpServer ＋ AuthorizationService（決定 5・決定 1(b)） | 2 | 4 |
| PR-D | NotificationService（決定 7）。**#1278 を閉じる** | 1 | 1 |

**なぜサービス単位でなく「契約が全部現れる最小の集約」で PR-A を切るか**: `IADR-0395` 決定 1 が
「新規約が同居する PR はレビュアーがどちらかを流し読みする」と退けたのと同じ理由で、**規約を決める PR は
規約の全要素を 1 回ずつ含み、それ以上を含まない**のがよい。PR-A には `PropertyName` 鍵・共有属性規則・
`RuleSet`・1 本の `||`・`Tag.Normalize` の述語写しが**それぞれ 1 回ずつ**現れる。
DocumentService の全 21 ガードを 1 本にすると 13 検証器・13 登録行・20 超の試験が同居し、
`RuleSet` や `PropertyName` の判断が埋もれる。

**IADR は 1 本だけ**（本書）。PR-B〜D に新しい判断は無い。判断が出た場合は本書へ日付つき追記ブロック
（`［YYYY-MM-DD 追記 / #NNN］`）で足す。

## 理由

- 決定 1 は移送前の振る舞いを**字面でなく応答で数え直した**結果である。「全違反を返す」という
  #1278 の前提を転記していたら、DocumentService の全ガードを `ToDictionary()` で写して
  **複数違反時の本文を変えていた**（`Create` の「題名も機密区分も欠けている」要求で鍵が 2 つになる）。
- 決定 2・6・7 はいずれも `IADR-0371` 決定 1「義務は関心を実装する箇所で標準ライブラリを使うこと」の
  適用である —— 関心の無い所へ参照を足さず（決定 2）、入力検証でないものを検証器へ押し込まず（決定 6）、
  関心が端点の 1 段下にあるからといって外さない（決定 7）。
- 決定 3 は `IADR-0395` 決定 2「実行は従前のガード節が居た位置」を、位置が 2 つある場合へ延長した
  ものである。DI 鍵の衝突（同決定 3）を避ける唯一の形が `RuleSet` だった。
- 計画の裁定を要する事項は無い（`ADR-0030` は用途ごとのライブラリ指定、`ADR-0041` は参照の向きで
  あり、応答本文は 1 バイトも変えない）。planning への `decision-needed` 起票はしていない。

## 結果

- 良い影響:
  - FluentValidation が 6 → 7 サービス（PR-A 時点。PR-C・PR-D の着地で 10 になる）。
  - DocumentService の `Documents` / `Tags` 集約から手書きの入力検証ガード節が消え、残る 400 は
    「入力検証ではないもの」（辞書照会・既存値・重複・上限）だけになった。
  - **鍵とメッセージを読む契約試験が初めて入った** —— 移送前は状態コードしか見ていなかった。
    移送前の端点に対しても同じ契約試験 16 本が緑になることを実測した（等価性の直接の証拠）。
  - Kernel の参照数は変わらない（決定 2）。
- 悪い影響 / トレードオフ:
  - 🔴 **鍵が推論名でなく明示に依存する。** `OverridePropertyName` の書き忘れは型では止まらず、
    試験（定数 ＋ リテラル）だけが止める（実測: 消すと鍵が `Title` になり 4 試験が赤）。
  - 🔴 **`RuleSet` の第 2 呼び出しの消し忘れは起動時に止まらず、位置の試験だけが止める**
    （実測: 消すと 10 試験が赤。うち既存試験 7 本）。
  - `Program.cs` に登録行が 8 行増える（PR-B 以降でさらに増える）。多いが `IADR-0371` 決定 2 の
    理由（消したときに止まる。実測: 1 行消すと 208 試験が赤）を優先する。
  - 4 サービスは FluentValidation だけを持ち、`Result` / Mapperly と揃った参照実装の形ではない（決定 2）。
  - sink ヘルパの同形 2 本（Authz）は残る（決定 8。受容）。
  - タグ名の規則が 3 複製のまま残る（決定 4。移送前と同じ状態を保つための意図的な選択）。
- フォローアップ:
  - PR-B / PR-C / PR-D（決定 10）。**#1278 を閉じるのは PR-D。**
  - `Error` → ProblemDetails の共通変換は #1230 が射程外と明記している。Kernel の `Error` に
    複数件を持たせるかは `ADR-0041` の裁定事項である。
  - #1279（写像 7 本）は DocumentService の `DocumentEndpoints.cs` / `PrivateNoteEndpoints.cs` に
    触るので PR-A / PR-B と交差する —— FIFO で直列化する。
