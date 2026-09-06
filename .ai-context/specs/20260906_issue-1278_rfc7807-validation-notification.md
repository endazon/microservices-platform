---
title: RFC7807（Results.ValidationProblem）系の手書き検証を FluentValidation へ移す —— PR-D（NotificationService。検証器を Application サービスへ注入する唯一の例。#1278 を閉じる）
type: spec
status: done
related_ids:
  - FR-19
  - FR-20
  - FR-22
  - UC-11
  - SC-19
  - SC-20
  - ADR-0030
  - ADR-0037
  - ADR-0041
  - ADR-0045
  - ADR-0065
  - ADR-0068
  - IADR-0215
  - IADR-0229
  - IADR-0270
  - IADR-0371
  - IADR-0393
  - IADR-0395
  - IADR-0398
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md 決定（検証 = FluentValidation）
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md 決定 6・17・18（通知の契機）
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md 決定 3（送信上限）
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md 決定 1
---

# 仕様書: #1278 PR-D —— NotificationService の受け口検証を FluentValidation へ移し、#1278 を閉じる

> 本仕様書は #1278（親 #1248 / #1230 / #1064。環流 planning#490）の 4 分割のうち **PR-D**、
> すなわち**最後のスライス**を対象とする。契約は PR-A が `IADR-0398` として確定させ、
> PR-B / PR-C が適用済みである。**本 PR もその適用であり、新しい判断は無い。**
> `IADR-0398` 決定 10 の表が本 PR の射程（1 検証器 / 1 ガード）を定めている。
> PR-A〜C と違うのは **検証器の注入先が端点ではなく Application サービス**であること、
> および **本 PR だけが形 β（全違反を複数の鍵で返す）である**ことである。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-22（利用者本人へ通知を届ける。件数と期限のみ・宛先は所有者本人）/ FR-19（保存容量の警告）/ FR-20（同期トークンの期限予告）
- ユースケース（UC）: UC-11（個人資料の作成・同期・削除）
- 画面（SC）: SC-19（個人資料管理）/ SC-20（Obsidian 連携設定）—— 通知の契機がこの 2 画面の要求に由来する
- 関連 ADR: ADR-0030（Application 層のライブラリ選定 —— 検証は FluentValidation）/ ADR-0037（決定 6 削除通知・決定 17 容量・決定 18 トークン期限予告）/ ADR-0041（Result 型の外部ライブラリ。参照の向き）/ ADR-0045（決定 3 送信上限）/ ADR-0065（単一プロジェクト VSA）/ ADR-0068（3 段のスライス分割規則）
- 実装 ADR: **IADR-0398（PR-A が確定させた契約。本 PR はその適用。特に決定 7 が本サービスを名指ししている）**/ IADR-0371 決定 1・2（関心を実装する箇所で標準ライブラリを使う・明示登録）/ IADR-0393（波 1）/ IADR-0395（波 2 第 1 弾。決定 2・8）/ IADR-0229 決定 1（`Error` は `Message` を 1 つだけ持つ）/ IADR-0215（通知サービスの設計）/ IADR-0270 決定 6（検知は DocumentService・送出は NotificationService）
- 計画書リンク: `../project-planning/projects/microservices-platform/07_adr/`

## 目的・背景

`IADR-0398` 決定 10 の PR-D。NotificationService の受け口（`POST /internal/notifications`）に残る
RFC7807（`Results.ValidationProblem`）系の手書き入力検証を `AbstractValidator` へ移す。
**状態コードも応答本文も 1 バイトも変えない。**

本 PR は #1278 を閉じるので、**issue の受け入れ基準を 5 サービス横断で決算する**（§#1278 の決算）。

### 🔴 着手前に自分で引き直した母集合（PR-A / PR-B / PR-C / 設計書の数えを転記しない）

基点は `origin/develop` @ **`1e1afb36`**（PR-C `#1300` が着地した直後）。
`git rev-parse --is-shallow-repository` = **`false`** —— 履歴が打ち切られていないので
`git log` を出典に使える。

🔴 **`src/ai-stock-trading`（submodule）を走査から外す。** ただし **checkout は必須**である ——
未初期化のままだと `Platform.Bff` がコンパイルできず **`Platform.Bff.Tests` の 510 件が黙って消える**。
いっぽう checkout した状態で走査すると母集合が化ける。**実測した化け方**:

| 走査（非テスト行） | submodule 除外 | submodule 込み |
| --- | ---: | ---: |
| `Results\.BadRequest` | **18** | 46 |
| `AddValidatorsFromAssembly`（**語**で数えた場合） | 9 | 16 |
| `ValidationProblem` | **49** | 49（submodule 側に該当なし） |

**以下の数値はすべて submodule 除外である。**

**軸 1**（`grep -rn "ValidationProblem" src --include=*.cs | grep -v Tests`、submodule 除外）: **49 行**。
本 PR のサービス（NotificationService）に属するのは **1 行**だけである ——
`Features/Notifications/Accept/NotificationIngressEndpoints.cs:35`
（`return Results.ValidationProblem(outcome.Errors!);`）。コメント行も sink の定義も無い。

**軸 2**（サービス内の私有 sink ヘルパ。McpServer で軸 1 が落とした形）:
`grep -rn "Problem(" src/platform/backend/Services/NotificationService --include=*.cs | grep -v Tests`
= **1 行**（軸 1 と同じ行）。**NotificationService に sink ヘルパは無い。**

**軸 3**（400 の別の器）: `Results.BadRequest` は NotificationService に **0 件**。
`Results.Problem` も **0 件**。**本サービスの 400 は上の 1 行だけである。**

**陽性対照**（「無い」を「無い」と読む前に、走査器が生きていることを確かめた）:

| 走査（submodule 除外・非テスト。**構文で数える**） | ヒット | 意味 |
| --- | ---: | --- |
| `Results\.BadRequest` | **18** | 波 2 第 1 弾が「入力検証ではない」として残した箇所が拾える ＝ 走査器は生きている |
| `PackageReference Include="FluentValidation"` を持つ csproj | **9** | PR-C の結果表「9」と一致（PR-A で DocumentService、PR-C で McpServer / AuthorizationService が加わった）。本 PR 適用後は **10** |
| `AddScoped<IValidator<` の登録行 | **26** | PR-C の結果表「26」と一致。本 PR 適用後は **27** |
| `AddValidatorsFromAssembly(` の**呼び出し** | **0** | 🔴 **語**で数えると **9** ヒットするが**すべて説明文のコメント行**である。**語ではなく構文で数える** |
| `ValidationProblems\.FirstViolation(` | **15** | DocumentService の sink が実在する。**本 PR は使わない**（§設計 2 の理由） |
| `\.OverridePropertyName(` | **15** | PR-A / PR-B が書いた 15 本。本 PR 適用後は **20**（5 規則すべてが鍵を明示する） |
| `[Fact]` / `[Theory]` の属性行 —— `NotificationService.Tests` | **38** | 試験の母数（`dotnet test` の 57 件は `[Theory]` の展開を含む） |

### 🔴 形 α か形 β か —— **制御フローを読んで測った**（依頼文・設計書・`IADR-0398` の枠を転記しない）

`outcome.Errors` の**型が辞書である**ことは、形 β の証明にならない。形 α のサイトも辞書を作る
（DocumentService の 20 サイトはすべて 1 鍵 1 件の辞書である）。**測るのは辞書の型ではなく生産側の制御フローである。**

`NotificationIngress.Validate`（`NotificationIngress.cs:78-113`）を読むと:

- **`request is null` の枝だけが `return` する**（`:80-84`）。鍵 `body` 1 件で打ち切る。
- **残る 5 項目の判定は独立した `if` であり、`else if` で連鎖していない**（`:88` / `:93` /
  `:100` / `:103` / `:106`）。**したがって複数の鍵が同時に埋まる。**
- **1 つの鍵の中だけが `if` / `else if`** である（`subject` の「必須」→「長さ」、`kind` も同じ）。
  **各鍵の配列は常に要素 1 である。**

→ **本サイトは形 β である**（`IADR-0398` 決定 1 の後者。写像は `result.ToDictionary()`）。
**#1278 の 37 サイトのうち形 β は 11 で、本 PR はそのうち唯一「移す」判定のサイトである。**

🔴 **測定（移送前のコードに対する実測）**: `subject` 空白 ＋ `kind` 空 ＋ `occurredAt` 欠落 ＋
`count: -1` ＋ `thresholdPercent: 101` の要求に対する生の応答本文は

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"subject":["subject は必須である。"],"kind":["kind は必須である。"],"occurredAt":["occurredAt は必須である。"],"count":["count は 0 以上である。"],"thresholdPercent":["thresholdPercent は 0〜100 である。"]}}
```

**鍵が 5 つ同時に出る。** 形 α なら 1 つしか出ない。**移送後もバイト同一である**ことを契約試験が固定する。

🔴 **述語の粒度**（`IADR-0395` 決定 8 / `IADR-0398` 決定 7）: 空白 300 文字の `subject`
（`SubjectMaxLength` = 255 を超える）は、移送前は `IsNullOrWhiteSpace` が真になった時点で
`else if` の長さ検査へ**到達しない**ので **1 件**（「必須」）である。
FluentValidation は既定で 1 つの `RuleFor` の全 validator を走らせるため、**規則レベルの
`Cascade(CascadeMode.Stop)`** が要る。外すと 2 件になり件数が変わる。**この 1 例を試験で固定する。**

## 対象範囲

### ガード単位の判定表（基点 `1e1afb36` の `path:line`）

物差しは `IADR-0395` 決定 6・7（i 述語が要求 DTO だけで閉じるか／ii 従前のガード節が居た位置で
実行できるか／iii ドメインの方針として意図的に 1 箇所へ束ねられていないか）を再利用する。

| # | サイト | 鍵 | 形 | 位置 | (i) | (ii) | (iii) | 判定 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| N0 | `NotificationIngress.cs:80-84`（`request is null`） | `body` | α（打ち切り） | `AcceptAsync` の先頭 | ❌ **束縛の失敗であって DTO の検証ではない**。FluentValidation は null インスタンスを検証できない（`Validate(null)` は `ArgumentNullException`） | – | – | **残す** |
| N1 | 同 `:87-90`（`subject` 必須） | `subject` | β | 同上（DB より前） | ✅ | ✅ | – | **移す** |
| N2 | 同 `:91-92`（`subject` 長さ） | `subject` | β（N1 と `else if`） | 同上 | ✅ `SubjectMaxLength` | ✅ | – | **移す**（`Cascade(Stop)` で粒度を写す） |
| N3 | 同 `:94-95`（`kind` 必須） | `kind` | β | 同上 | ✅ | ✅ | – | **移す** |
| N4 | 同 `:96-97`（`kind` 長さ） | `kind` | β（N3 と `else if`） | 同上 | ✅ `KindMaxLength` | ✅ | – | **移す**（同上） |
| N5 | 同 `:101-102`（`occurredAt` 必須） | `occurredAt` | β | 同上 | ✅ | ✅ | – | **移す** |
| N6 | 同 `:104-105`（`count` 非負） | `count` | β | 同上 | ✅ | ✅ | – | **移す** |
| N7 | 同 `:107-108`（`thresholdPercent` 値域） | `thresholdPercent` | β | 同上 | ✅ | ✅ | – | **移す** |

集計: ガード **8**（うち `IADR-0398` 決定 10 の表が数える「1 ガード」は `Validate` 関数 1 本 ＝
移す 7 判定を 1 単位としたものである。**本仕様書は判定単位で 8 と数え直し、N0 だけを残す**）。
検証器は **1 つ**（`IADR-0398` 決定 10 の表と一致）。

### 対象（PR-D = 1 検証器 / 5 規則）

| 検証器 | 置き場（3 段目 ＝ その操作専用） | 要求型 | 規則（宣言順が契約） |
| --- | --- | --- | --- |
| `NotificationIngressValidator` | `NotificationService/Features/Notifications/Accept/` | `NotificationIngressRequest` | `subject`（N1+N2）→ `kind`（N3+N4）→ `occurredAt`（N5）→ `count`（N6）→ `thresholdPercent`（N7） |

### 対象外（本 PR で**触らない**。理由つき。規則 6 の再掲）

| 箇所 | 理由 |
| --- | --- |
| N0（`request is null`） | **残す**（`IADR-0398` 決定 7）。FluentValidation は null インスタンスを検証できず、「本文が無い」は DTO の検証ではなく**束縛の失敗**である |
| `deadline` に規則を置くこと | 移送前も**制約が無い**（`NotificationIngress.cs:110-111`）。**過去の期限も正当である**（`IADR-0215` 決定 4）。規則を足すのは移送ではない |
| `kind` の**値集合**を閉じること | 移送前も値そのものは検証しない（`IADR-0215` 決定 2: 値集合は開いている）。閉じると「種別を増やしたら未更新の受け側が既存の値ごと拒否する」を再現する |
| 重複の畳み込み（`:44-66`） | **DB の照会結果**であり入力検証ではない。しかも 400 ではなく 200 を返す |
| `DocumentService` / `DataSourceService` / `McpServer` / `AuthorizationService` | PR-A / PR-B / PR-C が着地済み、または**恒久的に残す**（`OwnerMappingValidation` は `IADR-0398` 決定 6） |
| `Features/ValidationProblems.cs`（DocumentService の sink） | 🔴 **使えない・使わない。** ①`DocumentService.Features` の **`internal`** であり、②**ユニット境界を跨ぐ**（DocumentService = `knowledge` / NotificationService = `platform`）。`check-unit-dependencies.js` 規則 1 はユニット外参照を `platform/backend/Shared/` の 3 プロジェクトにしか許さない（`isSharedProject` がパス接頭辞で判定する）。③そもそも `FirstViolation` は**形 α の写像**であり、本サイト（形 β）に当てると **5 鍵が 1 鍵に化ける**。**NotificationService は sink を新設せず、`ToDictionary()` を端点の 1 行手前で呼ぶ** |
| sink ヘルパの新設・統合 | `IADR-0398` 決定 8 の受容。本サービスには sink が無く、必要も無い（写像は `ToDictionary()` の 1 呼び出し） |

## 設計

`IADR-0398` の決定をそのまま適用する。**新しい判断は無い**（したがって新 IADR も起こさない）。

1. **決定 7（注入先は `NotificationIngress`）**: 検証器は端点ではなく `NotificationIngress` の
   主コンストラクタへ注入し、`Validate` の呼び出しを**従前のガード節が居た位置**
   （`AcceptAsync` の先頭・DB 照会より前）に置く。端点（`NotificationIngressEndpoints.cs`）は
   **1 行も変えない**。`IADR-0371` 決定 1 の義務は「**その関心を実装する箇所**で標準ライブラリを
   使うこと」であり、関心が端点の 1 段下にあることは射程を外す理由にならない。
2. **決定 1（形 β の写像）**: `NotificationIngressOutcome.Invalid(result.ToDictionary())`。
   `ToDictionary()` は `PropertyName` で群化し、**群の出現順と群内のメッセージ順をどちらも保つ**。
   `Errors` プロパティの型を `Dictionary<string, string[]>?` → `IDictionary<string, string[]>?` へ
   広げる（`ToDictionary()` の戻り値型）。**端点の `Results.ValidationProblem(outcome.Errors!)` は
   `IDictionary` を受けるので変更不要**である。
3. 🔴 **鍵は必ず明示する**（決定 1）。`OverridePropertyName(<internal const>)` を 5 規則すべてに置く。
   推論名は `Subject` / `Kind` / `OccurredAt` / `Count` / `ThresholdPercent`（PascalCase）であり、
   移送前の `subject` / `kind` / `occurredAt` / `count` / `thresholdPercent` と**一致しない**。
   型では止まらない。**本サービスには sink が無いので、鍵の正は検証器ただ 1 つである**
   （PR-C の「鍵は sink が持つので検証器は鍵を持たない」とは**逆**であり、PR-A / PR-B と同じ側である）。
4. 🔴 **述語の粒度を写す**（決定 9）。`subject` / `kind` は**規則レベルの `Cascade(CascadeMode.Stop)`**。
   `IsNullOrWhiteSpace` を `NotEmpty()` に置き換えない（空白のみの文字列で振る舞いが割れる）。
   `count` / `thresholdPercent` は `null` を**通す**（移送前の `is < 0` / `is < 0 or > 100` は
   null に対して偽である）—— `NotNull()` を足さない。
5. **決定 2（Kernel を参照しない）**: `Platform.Shared.Kernel` を足さない。`Error` は `Message` を
   1 つしか持たず（`IADR-0229` 決定 1）、**5 鍵 5 件の応答はそもそも載らない**。
6. **メッセージは検証器が持つ**（決定 9）。補間を含む 2 本（`subject` / `kind` の長さ）は `const` に
   できないので `internal static readonly`、残る 5 本は `internal const`。数値は
   `NotificationIngress.SubjectMaxLength` / `KindMaxLength` から作る（**DB 列長と同じ定数**）。
   試験は**定数とリテラルの両方**へ当てる。
7. **`RuleSet` は使わない。** 🔴 **本サイトは検証の位置が 1 つしかない**（`AcceptAsync` の先頭に
   7 判定が連続する）。`IADR-0398` 決定 3 は「位置が 2 つある端点」に限った決定であり、位置が 1 つの
   ところへ導入すると `Validate(req)` が名前つき集合を走らせないハザードを**理由なく**持ち込む。
   **したがって「規則が `RuleSet` から逃げる」変異は本 PR に適用対象が無い。**
8. **1 本の `||` ガードは本 PR に存在しない。** 7 判定の述語はいずれも単項である。
   **「1 本の `||` を割る」変異も適用対象が無い。**
9. **登録**: `Program.cs` に **1 検証器 1 行の明示登録**（`AddScoped<IValidator<T>, TValidator>()`）を
   `AddScoped<NotificationIngress>()` の隣へ 1 行足す（全リポジトリ 26 → 27）。
   `AddValidatorsFromAssembly` は使わない（`IADR-0371` 決定 2 —— 消したときに止まること）。
10. **`.csproj`**: `<PackageReference Include="FluentValidation" />` を足す（版は書かない。CPM）。
    **`Domain/` には入れない**（`check-backend-libraries.js` の Domain 外部依存ゼロ規則）——
    検証器は `Features/` に置き、`Domain/` の型には触らない。

### 母集合の取り方（`traceability.repo.md` 規則 9・10）

- **規則 9**（追随する文書を記憶で挙げない）: 「本 PR で新たに誤りになる自分の記述」を、
  **誤りの側の文字列で追跡下の全ファイルを走査してから**挙げた。走査語は
  `AddScoped<IValidator<`（26 → 27）・`PackageReference Include="FluentValidation"`（9 → 10）・
  `\.OverridePropertyName(`（15 → 20）・`Dictionary<string, string\[\]>` の
  `NotificationIngressOutcome` 周り（型の変更）である。
- **規則 10**（導出値は走査ではなく計算し直す）: `IADR-0398` §結果の
  「FluentValidation が 6 → 7 サービス（PR-A 時点。PR-C・PR-D の着地で 10 になる）」は
  **本 PR の着地で 10 になる**。これは同 ADR が予告した増え方**そのもの**であり、
  **ADR 側の追記は要らない**（追記が要るのは「新しい判断が出たとき」だけ。`IADR-0398` 決定 10 末尾）。
  🔴 ただし **`IADR-0398` §結果の「4 サービス」という数え**（決定 2 の見出し）は
  DocumentService / McpServer / AuthorizationService / NotificationService を指し、
  **csproj の 10 とは母集合が違う**（csproj は Tests を含まない実プロジェクトであり、
  波 1 以前から FluentValidation を持つ 6 サービスを含む）。
- **除外理由**: `IADR-0398` §母集合の表の「本 PR 適用後は 19 行」は **PR-A 時点の値として明記**
  されており、PR-B が 24・PR-C が 26 を記録している。**どれも古くならない書き方**なので
  追随の必要は無い。

## 受け入れ基準

- [x] `NotificationIngress.Validate` の 7 判定が 1 つの `AbstractValidator` の 5 規則へ移り、
      `AcceptAsync` に残る手書きガードは `request is null` の 1 本だけである
- [x] 🔴 **形 β を保つ**（`ToDictionary()`）。複数項目が同時に不正な要求で**鍵が 5 つ**出ることを試験が固定する
- [x] 応答本文が移送前後で**バイト同一**である（生の JSON を文字列比較する試験を含む）
- [x] 🔴 **鍵は 5 規則すべてで明示**（`OverridePropertyName` が 5 件）。推論名（`Subject` 等）が応答に出ない
- [x] 🔴 **空白 300 文字の `subject` が「必須」1 件だけ**である（`Cascade(Stop)` の粒度）
- [x] `count: null` / `thresholdPercent: null` / 過去の `deadline` / 未知の `kind` が**通る**（述語を広げていない）
- [x] 端点（`NotificationIngressEndpoints.cs`）に**変更が無い**（`git diff` が空）
- [x] `Program.cs` の登録は明示登録であり（全リポジトリ 27 行）、`AddValidatorsFromAssembly` を使っていない
- [x] `Platform.Shared.Kernel` の参照を足していない（`IADR-0398` 決定 2。空真で満たす）
- [x] 🔴 DocumentService の `ValidationProblems.FirstViolation` を**参照していない**
      （ユニット境界・`internal`・形が違う。`check-unit-dependencies.js` が緑）
- [x] 契約試験を**先に**書き、`NotificationIngress.cs` を `origin/develop` へ戻した状態でも**緑**になることを実測した
- [x] 変異で**実際に赤になった**試験名と本数を PR に書いた。**適用対象が無い変異はその旨を書いた**
- [x] `dotnet test` の件数が前後で減っていない（**測り直して書く**。試験の削除・skip 化は 0 件）
- [x] `dotnet build` × 2 / `dotnet test` × 2 / `dotnet format --verify-no-changes` / 検査器 6 本 ＋
      `REQUIRE_REPO_TESTS=1 scripts.test.js` が緑
- [x] **#1278 の受け入れ基準を 5 サービス横断で決算した**（§#1278 の決算）

## テスト方針

`IADR-0398` 決定 9 の軸（S 状態コード / K 鍵の列 / M メッセージ列 / P 位置 / G 述語の粒度 /
O 宣言順 / C 件数）へ写像する。

| 試験（置き場） | 軸 | 何を見るか | 赤にする変異 |
| --- | --- | --- | --- |
| `NotificationIngressValidatorTests.ValidRequest_Passes`（陽性対照） | – | `IsValid` かつ `Errors` 空 | 「常に落ちる検証器」 |
| `…ValidatorTests.<Rule>_FailsWithOriginalKeyAndMessage`（5 項目） | K, M | `PropertyName` が**鍵の定数**と**リテラル**の両方に一致し、`ErrorMessage` も両方に一致 | `OverridePropertyName` を消す（`Subject` になる）／定数だけ・リテラルだけ書き換える |
| `…ValidatorTests.AllInvalid_KeysInDeclarationOrder` | K, O, C | `ToDictionary().Keys` が `[subject, kind, occurredAt, count, thresholdPercent]` に**順序どおり**一致し、各値が 1 件 | 宣言順の入れ替え／規則を 1 本消す |
| `…ValidatorTests.WhitespaceSubjectOverLimit_ReportsRequiredOnly` | **G, C** | 🔴 空白 300 文字 → `subject` の失敗が **1 件**で「必須」 | **`Cascade(Stop)` を外す**（2 件になる） |
| `…ValidatorTests.NullCountAndThreshold_Pass` / `LongDeadlineInPast_Passes` | G | null と過去の期限が通る（述語を広げていない） | `NotNull()` を足す／`deadline` に規則を足す |
| 端点契約試験 `NotificationIngressValidationProblemContractTests`（`errors` を `JsonElement` で**列挙順**に読む） | S, K, M, C | 🔴 5 項目同時違反で **鍵が 5 つ・順序どおり・各 1 件** | 端点／`AcceptAsync` が `FirstViolation` 相当へ丸める（鍵が 1 つに減る） |
| 同 `…_KeepsRfc7807Envelope_ByteForByte` | S, K, M | 🔴 生の JSON を**文字列比較**（`type` / `title` / `status` / `errors` ごと） | 器を変える／鍵を変える |
| 同 `…_MissingBody_ReturnsBodyKey` | K | `request is null` が `body` 1 鍵のまま（移していないことの固定） | `request is null` を検証器へ押し込む（`Validate(null)` が例外 → 500） |
| 同 `…_ValidationRunsBeforePersistence` | **P** | 5 項目同時違反で 400、かつ `Notifications` / `EmailOutbox` が **0 行** | 検証を DB 照会の後ろへ動かす |
| 同 `…_DuplicateFoldingStillWorksAfterValidation` | P | 妥当な要求の 201 / 200（畳み込み）が変わらない | 検証器が妥当な要求を落とす |
| **既存**: `NotificationIngressTests`（`不正なペイロードは400を返す` の 9 ケース ＋ 永続化 0 件 ＋ 畳み込み ＋ 未知種別 ＋ 過去期限 ＋ 無認証） | S | 状態コード | **登録行を消す**（`IValidator<T>` が解決できず 500） |

🔴 **等価性の直接の証拠**: 契約試験を**先に**書き、**`NotificationIngress.cs` だけ**を
`origin/develop` の内容へ戻して同じ試験を走らせる。移送前のコードでも緑であることが
「応答を変えていない」の実測である（PR-A / PR-B / PR-C と同じ作法）。

## 結果（着地時に測り直した実測値）

🔴 **導出値は push の直前にすべて測り直した**（作業途中の値を受け入れ基準へ残さない）。
基点は `origin/develop` @ `1e1afb36`。**すべて submodule 除外・構文で数えた値である。**

| 走査（走査語をそのまま添える） | 前 | 後 |
| --- | ---: | ---: |
| `AddScoped<IValidator<`（全リポジトリ） | 26 | **27** |
| `PackageReference Include="FluentValidation"` を持つ csproj | 9 | **10** |
| `\.OverridePropertyName(`（全リポジトリ） | 15 | **20**（+5 = 本 PR の 5 規則） |
| `AddValidatorsFromAssembly(` の**呼び出し** | 0 | **0** |
| `Results\.BadRequest`（非テスト。陽性対照） | 18 | **18** |
| `ValidationProblem`（非テスト。**行**） | 49 | **50** |
| `Results\.ValidationProblem(` の**呼び出し** —— NotificationService | 1 | **1** |
| `Platform.Shared.Kernel.csproj` を参照する csproj | 7 | **7** |

★ 🔴 **`ValidationProblem` の行が 1 増えたのは呼び出しではなくコメントである**
（`NotificationIngress.cs:102`「端点の `Results.ValidationProblem` は `IDictionary` を受けるので…」）。
**呼び出し行は 1 のまま**であり、その 1 行（`NotificationIngressEndpoints.cs:35`）は**変更していない**。
`AddValidatorsFromAssembly` を語で数えると 9 ヒットする（全部コメント）のと同じ罠である ——
**語ではなく構文で数える。**

試験数（`dotnet test`。**削除・skip 化は 0 件**）:

| プロジェクト | 前 | 後 |
| --- | ---: | ---: |
| `NotificationService.Tests` | 57 | **90**（+33） |
| platform ユニット合計 | 1585 | **1618** |
| knowledge ユニット合計 | 2035 | **2035**（無変更） |
| 両ユニット合計 | 3620 | **3653** |

**等価性の直接の証拠**: 端点契約試験 `NotificationIngressValidationProblemContractTests`（**13 本**）を
**移送より前に書き**、**リポジトリの `src/` が `origin/develop` そのものの状態**
（検証器のファイルも登録行も csproj の `PackageReference` も無く、検証器の単体試験も置いていない状態）で
**13/13 緑**であることを実測した（`合格: 70` ＝ 既存 57 ＋ 契約 13）。移送後も 13/13 緑である。
**生の JSON を文字列比較する 1 本**（`ValidationProblem_KeepsRfc7807Envelope_ByteForByte`）を含む
—— RFC7807 の封筒と 5 鍵の中身が 1 バイトも変わらないことの直接の証拠である。

**変異試験**（1 つずつ適用して戻した。母数は `NotificationService.Tests` **90**）:

| 変異 | 赤 | 内訳・代表的な試験名 |
| --- | ---: | --- |
| 規則を 1 本消す（`count`） | **6** | 新設 5 ＋ **既存 1**（`NotificationIngressTests.不正なペイロードは400を返す(count が負)`）。ほか `AllFiveInvalid_ReportsFiveFailuresInDeclarationOrder` / `SubjectAndCountInvalid_ReturnsExactlyThoseTwoKeys` |
| 登録行を 1 行消す | **31** | 新設 13 ＋ **既存 18**。`IValidator<T>` が解決できず 500。受け口を使う既存試験が全滅する（`IADR-0371` 決定 2「消したときに止まる」の実測） |
| 🔴 **辞書を 1 件へ丸める**（`ToDictionary()` → `Errors[0]` の 1 鍵） | **3** | `AllFiveFieldsInvalid_ReturnsFiveKeysInDeclarationOrder` / `SubjectAndCountInvalid_ReturnsExactlyThoseTwoKeys` / `ValidationProblem_KeepsRfc7807Envelope_ByteForByte`。**3 本とも本 PR が新設したものである —— 既存の 57 本は 1 本も捕まえない**（状態コードは 400 のままだから） |
| 🔴 **`Cascade(CascadeMode.Stop)` を外す** | **7** | 新設 4 ＋ **既存 3**。`WhitespaceSubjectOverTheLimit_ReportsRequiredOnly`（検証器・端点の 2 本）ほか。**既存 3 本が赤になる機序は件数ではなく 500 である** —— `Stop` が無いと `subject` が `null` のときに長さの述語 `s!.Length` へ到達して `NullReferenceException` になる（`不正なペイロードは400を返す(subject 欠落 / kind 欠落)` と `不正なペイロードは通知を1件も作らない`）。**「必須 → 長さ」の粒度そのものを捕まえるのは新設の 4 本だけである** |
| `OverridePropertyName` を消す（`subject`） | **8** | `MissingSubject_FailsWithOriginalKeyAndMessage` ほか。鍵が `Subject`（PascalCase）に化ける。**8 本とも本 PR が新設したものである** |
| 「規則を `RuleSet` の外へ出す」 | — | 🔴 **適用対象が無い。** 本サイトは検証の位置が 1 つしかなく、`RuleSet` を導入していない（設計 7） |
| 「1 本の `\|\|` を割る」 | — | 🔴 **適用対象が無い。** 7 判定の述語はいずれも単項である（設計 8） |

🔴 **この 5 変異のうち 2 つ（辞書の丸め・`OverridePropertyName`）は、移送前の 57 本が 1 本も
捕まえない欠陥である。** 移送前の受け口の 400 の試験は**状態コードしか見ていなかった**
（`IADR-0398` 決定 9 が全リポジトリについて観測したのと同じ状態）。本 PR で初めて固定した。

**検査器・ビルド・整形**（すべて緑。出力は PR 本文に転記した）:
`dotnet build` × 2 / `dotnet test` × 2 / `dotnet format src/platform/backend/backend.slnx --verify-no-changes` /
`check-backend-libraries` / `check-cpm-versions` / `check-unit-dependencies` / `check-coverage-floor`
（line 92.75% ・ branch 79.21%。床 90 / 75）/ `check-trace-blocks` / `check-adr-numbering` /
`check-test-traceability` / `REQUIRE_REPO_TESTS=1 scripts.test.js`。

## #1278 の決算（5 サービス横断。本 PR が issue を閉じる）

### 基準 1: 「手書きのガード節が残っていない」 —— 🔴 **文字どおりには真ではない**

**基点 `1e1afb36` ＋ 本 PR で引き直した最終状態**（`grep -rn "ValidationProblem(" src --include=*.cs`
＋ McpServer の私有 sink `Problem(` の 2 軸。非テスト・submodule 除外。sink の**定義**行と
検証器由来の写像行は数えない）:

**残る手書きガードは 16 本（呼び出し 20 箇所）である。** 各行に、残す理由を `IADR-0395` 決定 7 /
`IADR-0398` 決定 8 の分類で付す。

| # | サービス | 箇所 | 呼び出し | 残す理由（分類） |
| --- | --- | --- | ---: | --- |
| 1 | DocumentService | `DocumentEndpoints.DocScopeChangedProblemOrNull`（`Update` / `UpdateMetadata`） | 2 | **既存値**（`doc.Attributes` が要る。`FindAsync` の後ろ） |
| 2 | DocumentService | `DocumentEndpoints.UnknownTagsProblem`（`AddTag` / `Create` / `Update` / `UpdateMetadata`） | 4 | **後段の照会結果**（`TagResolver` が辞書を引いた結果）。しかも**認可の後ろ**（先に照合すると書けない主体に情報が返る）。動かせない |
| 3 | DocumentService | `PrivateNotes/SetQuota/Endpoint.cs:23` | 1 | **例外由来**。述語もメッセージも `Domain/PrivateNote` の**不変条件**が持つ（写すと不変条件が 2 箇所になる）。位置も DB 照会の後ろ |
| 4 | DataSourceService | `OwnerMappingValidation.cs:28`（形式） | 1（3 操作が共有） | **形式検査と実在検査が 1 関数の 2 段**であり、3 操作（Create / Patch / Update）が同じ 1 本を呼ぶことが設計である。形式だけを写すとファイル自身が警告する穴（「登録では弾くのに PATCH では通る」）を開ける |
| 5 | DataSourceService | 同 `:53`（実在） | 1 | **外部名簿**（`IPlatformUserDirectory`）。**引けなければ 502** であり、`ValidationFailure` は状態コードを持てない（「確かめられなかった」を「存在しない」と混ぜない） |
| 6 | AuthorizationService | `Authz/CreateAttribute/Endpoint.cs:19` | 1 | **値域が DB 由来**（`existing` で一意性）。`AbacValidation` が**形式と一意性を 1 つの配列**に積む —— 片方だけ移すと件数が変わる |
| 7 | AuthorizationService | 同 `:31` | 1 | **例外由来**（`DbUpdateException` の競合捕捉）。検証ではない |
| 8 | AuthorizationService | `Authz/CreatePolicy/Endpoint.cs:18` | 1 | **値域が DB 由来** ＋ 🔴 **#535「3 経路が 1 関数を呼ぶ」** |
| 9 | AuthorizationService | `Authz/UpdateAttribute/Endpoint.cs:24` | 1 | **既存値**（`attr.Key` / `attr.Scope`）＋ DB |
| 10 | AuthorizationService | `Authz/UpdatePolicy/Endpoint.cs:21` | 1 | **値域が DB 由来** ＋ #535 |
| 11 | AuthorizationService | `Users/ReplaceAttributes/Endpoint.cs:21` | 1 | **値域が DB 由来**（`definitions`） |
| 12 | AuthorizationService | `Users/ReplaceRoles/Endpoint.cs:18` | 1 | **値域が IdP 由来**（`ListAssignableRolesAsync`） |
| 13 | McpServer | `McpClientEndpoints.cs:69`（`RejectUnassignableAsync` 前段） | 1（登録 / 差し替えの 2 経路が共有） | **要求型が 2 つで 1 関数を共有することが統制**（`ADR-0062` 決定 3）。形 β（最大 2 件） |
| 14 | McpServer | 同 `:78` | 1（同上） | **外部解決**（`IRegistrarAttributeResolver`）。形 β |
| 15 | McpServer | `RegisterClient/Endpoint.cs:43` | 1 | **DB の照会結果**（`AnyAsync` の重複登録） |
| 16 | NotificationService | `NotificationIngress.AcceptAsync` の `request is null` | 1 | **束縛の失敗であって DTO の検証ではない**。FluentValidation は null インスタンスを検証できない |

［2026-09-06 追記 / #1278］ 上表の 16 は `IADR-0398` §コンテキストの「残す 16」と**同じ数だが
内訳が違う**。同 ADR は `DocScopeChangedProblemOrNull` の 2 呼び出しを 2 ガードと数え、
NotificationService の `request is null` を「移す 1 ガード」の内側に畳んでいた。
本表は**ヘルパ 1 本 = 1 ガード**と数え直し、`request is null` を独立の 1 行として立てている。
**数え方を明示しないと同じ 16 が別のものを指す。**

**残ったのはすべて「端点入口の入力検証ではないもの」である** ——
後段の照会結果（2・15）／既存値（1・9）／例外由来（3・7）／値域が DB・IdP 由来（6・8・10・11・12）／
形式検査と一意性・実在が 1 配列 1 関数に混ざる（4・5・6）／外部解決（5・14）／
ドメインの統制として意図的に 1 箇所へ束ねられている（4・13・14）／束縛の失敗（16）。
🔴 **「手書きのガード節が残っていない」は文字どおりには達成していない。達成したのは
「残っている手書きのガード節が、1 本残らず『入力検証ではない』と説明できる状態」である。**

**sink ヘルパは 6 本残る**（統合しない。`IADR-0398` 決定 8 の受容）:
`AuthzEndpoints.ValidationProblem` / `UserAdminEndpoints.ValidationProblem`（同形の 2 本）/
`McpClientEndpoints.Problem` 2 本 / `OwnerMappingValidation` の私有 1 本 /
DocumentService に新設した `ValidationProblems.FirstViolation` 1 本。

### 基準 2: 「`Platform.Shared.Kernel` を参照している」 —— **空真で満たす**（参照は 1 本も足していない）

- 実測: `Error` の定義は `Platform.Shared.Kernel/Error.cs:35`
  `public sealed record Error(string Code, string Message, ErrorKind Kind = ErrorKind.Failure)` であり、
  **`Message` を 1 つしか持たない**（`IADR-0229` 決定 1「`Error` を複数持つ表現を導入しない」）。
  RFC7807 の `errors` は**鍵つき・複数件**であり、**そもそも `Error` に載らない。**
- 実測: 移送した 4 サービスの `.cs` に `using Platform.Shared.Kernel` は **0 件**
  （DocumentService / McpServer / AuthorizationService / NotificationService。走査語をそのまま添える）。
  **`Result` / `Error` を使う経路が 1 本も無い** ので、基準の条件節が偽である。
- 実測: `Platform.Shared.Kernel.csproj` を参照する csproj は **7**（移送前後で不変。
  6 サービス ＋ Kernel 自身の試験）。**使わない参照を足していない**（`IADR-0371` 決定 4 が退けた形）。

### 基準 3: 「移送前後で状態コードも本文も同じ / 件数が減っていない / 変異試験で赤」

| PR | 契約試験（移送前のコードに対して緑） | 変異で赤になった代表 | 試験数 |
| --- | --- | --- | --- |
| PR-A（DocumentService `Documents` / `Tags`） | **16 本**（移送前の端点に対しても緑） | `OverridePropertyName` を消す → 4 / 第 2 の `Validate` を消す → 10（既存 7 含む）／登録行 1 行 → 208 | DocumentService.Tests が増加 |
| PR-B（同 `PrivateNotes` / `ObsidianSync` / `SyncDevices`） | 契約試験を先に書き、端点を develop へ戻して緑 | `baseVersion` を既定集合へ移す（404 が 400 に化ける）ほか | 同上 |
| PR-C（McpServer ＋ AuthorizationService） | **23 本**（`src/` を develop に戻して 23/23 緑） | 形 β を `Errors[0]` へ丸める → 2 ／ sink の鍵を変える → 11 / 4 ／ 登録行 → 33 / 71 | McpServer 146 → 175、Authz 185 → 201 |
| **PR-D（NotificationService。本 PR）** | **13 本**（`src/` を develop に戻して 13/13 緑。**生の JSON のバイト比較 1 本を含む**） | 辞書を 1 件へ丸める → 3 ／ `Cascade(Stop)` を外す → 7 ／ `OverridePropertyName` → 8 ／ 登録行 → 31 ／ 規則 1 本 → 6 | NotificationService 57 → **90** |

**試験の削除・skip 化は 4 本の PR を通じて 0 件である。** 両ユニット合計は
**PR-C 着地時 3620 → 本 PR で 3653**。

### 基準 4: 検査器

`check-backend-libraries` / `check-cpm-versions` / `check-unit-dependencies` / `check-coverage-floor`
はいずれも緑（本 PR の実測は §結果末尾）。

### 🔴 #1278 が**達成しなかったこと**（閉じるにあたって正直に書く）

1. **「手書きのガード節が残っていない」は達成していない。** 16 本が残る（上表）。達成したのは
   「残る 1 本ごとに、入力検証ではない理由が言える状態」である。**基準の文言のほうが実態に合っていなかった。**
2. **`Platform.Shared.Kernel` の参照は 1 本も増えていない**（空真）。`IADR-0371` 決定 1 の
   「3 ライブラリ（Result / FluentValidation / Mapperly）の噛み合い方が 1 スライスに揃って見える」形は
   **この 4 サービスでは再現しない。** 再現させるには `Error` に複数件・鍵を持たせる必要があり、
   それは `ADR-0041` の裁定事項である（#1230 が射程外と明記）。
3. **`Error` → ProblemDetails の共通変換は作っていない**（#1230 が射程外と明記）。
   結果として RFC7807 の `errors` の**鍵の出どころが 2 通り**のまま残る ——
   サイトごとの `PropertyName`（DocumentService / NotificationService）と
   サービスの sink（McpServer の `request` / AuthorizationService の `errors`）である。
4. **sink ヘルパの同形 2 本**（`AuthzEndpoints.ValidationProblem` と
   `UserAdminEndpoints.ValidationProblem`）**は統合していない**（`IADR-0398` 決定 8 の受容）。
5. **DataSourceService からは 1 行も動いていない**（`IADR-0398` 決定 6）。#1278 の母集合に
   2 箇所が入っていたが、**恒久的に残す**判断である。
6. **タグ名の規則は 3 複製のまま**である（`IADR-0398` 決定 4。移送前と同じ状態を保つための選択）。
7. **`RuleSet` の第 2 呼び出しの消し忘れは型では止まらない**（`Create` / `Push`）。
   起動もコンパイルも通り、位置の試験だけが止める —— **移送で新たに背負った依存である。**
8. **移送前の 400 の試験が状態コードしか見ていなかったという事実は、移送では直っていない
   —— 直したのは移した箇所だけである。** 残る 16 ガードの本文を読む試験は、本 issue の射程外である。

## 計画書との差異

- 差異: なし。**状態コードも応答本文も 1 バイトも変えない移送**であり、計画の裁定を要する事項は無い
  （ADR-0030 は用途ごとのライブラリ指定、ADR-0041 は参照の向き、ADR-0037 / ADR-0045 は
  通知の契機と送信経路についての決定であり、いずれも本作業と整合する）。
  planning への `decision-needed` 起票はしていない。

## 未決事項

- **IADR は起こさない**（`IADR-0398` 決定 10「PR-B〜D に新しい判断は無い」）。
  作業中に新しい判断が出た場合は `IADR-0398` へ日付つき追記ブロック（`［YYYY-MM-DD 追記 / #NNN］`）で足す
  —— 🔴 **表の途中へ挟まない**（GFM の表が分断される。PR-A で実測された失敗）。
  新 IADR が要る場合の空き番号は基点 `1e1afb36` で **`IADR-0402`** である。
- **本 PR が #1278 を閉じる**（`Closes #1278`）。§#1278 の決算に、閉じるにあたって
  **達成しなかったこと**も明記する。
