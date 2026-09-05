---
title: 集合値の利用者属性（tags / projects）の符号化を 1 か所で決め、Keycloak 多値・契約の線上表現・SC-17 の辞書照合を揃える
type: spec
status: done
related_ids: [FR-16, FR-09, SC-12, SC-17, ADR-0062, ADR-0026, ADR-0004]
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/05_screens/01_screens.md#sc-17
---

# 仕様書: 集合値の利用者属性の符号化を 1 か所で決める（issue #1243）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-16（MCP サーバー連携）・FR-09（ABAC 管理）
- 画面（SC）: SC-12（MCP クライアント登録管理）・SC-17（ユーザーアカウント管理）
- 関連 ADR: ADR-0062 決定 2（無人アカウントの属性は登録者の部分集合。**タグにも同じ規則**）／
  ADR-0026（ロール・属性の割当）／ADR-0004
- 実装 ADR: IADR-0386（本件の決定）／IADR-0366（部分集合判定の置き場所）／
  IADR-0301（Keycloak Admin 連携）／IADR-0329（`view-users` の単一主体）
- issue: #1243（出所は #1185 のフェーズ末監査）

## 目的・背景

登録者の**タグ集合**の解決が、Keycloak の多値属性を**先頭 1 値へ畳む**ため過小になる。
稼働再測（#1185 コメント。一時利用者 `tags=sales,hr`）では `hr` が落ちていた。

**符号化が 3 者で食い違っている。**

| 経路 | 集合値の表現 | 帰結 |
| --- | --- | --- |
| Keycloak（`KeycloakIdentityAdminClient.ToIdentityUser`） | 多値配列 → **先頭 1 値** | `["sales","hr"]` → `"sales"`。**`hr` が静かに消える** |
| 契約（`PlatformUserDto.Attributes` は 1 キー 1 値） | 表現の取り決めが**無い** | どちらの読み方も等しく「正しい」 |
| SC-17（`UserAssignmentValidation`） | 値**全体**を許可値へ突き合わせ | `"sales,hr"` を**作れない** |
| MCP（`ServiceAccountAttributeSubset.Tokens`） | **カンマ／空白区切り**で分割 | 上流の誰も作れない形を待っている |

**同じ文字列が読み手ごとに違う意味を持つ**のが欠陥の本体である。畳み込みは部分集合判定に対しては
fail-closed（配れる集合が狭まる）だが、**拒否理由が嘘になる**（「登録者が持つタグは 'sales' です」）。
`Available=false` と空集合を型で分けているのと同じ理由で、**安全側でも報告が嘘なのは受け入れない**。

## 母集合の取り方（着手前に自分で引いた。陽性対照つき）

基点 `develop`（#1242 の PR ブランチの上。`git rev-parse --is-shallow-repository` → `false`）。

### 母集合 A: 集合値として**分割して読む**経路

```console
$ grep -rn "ServiceAccountAttributeSubset.Tokens(" --include=*.cs src/ | grep -v /obj/ | grep -v Tests
McpServer/Domain/RestrictedProject.cs:58            projects / project（主体・文書）
McpServer/Domain/RestrictedProject.cs:64            project（文書）
McpServer/Domain/ServiceAccountAttributeSubset.cs:112  Difference（要求値の分解）
McpServer/Infrastructure/.../AuthorizationServiceRegistrarAttributes.cs:79-80  tags（登録者）
```

**集合値として読まれる利用者属性キーは `tags` と `projects` の 2 つである。**

### 母集合 B: 多値 → 単一値の畳み込み

```console
$ grep -rn "values?.FirstOrDefault" --include=*.cs src/ | grep -v /obj/ | grep -v Tests
AuthorizationService/Infrastructure/ExternalServices/KeycloakIdentityAdminClient.cs:258
```

**陽性対照**: 同じ走査を `FirstOrDefault(` に緩めると platform 配下で 40 件以上を当てる（空振りでない）。
**畳み込みの実装は 1 か所だけである。**

### 母集合 C: 利用者属性の値を**全体で**突き合わせる経路（分割と衝突しうる側）

```console
$ grep -rn "AllowedValues.Contains(value\|allowedValues.Contains(userValue" --include=*.cs src/ | grep -v /obj/ | grep -v Tests
AuthorizationService/Domain/UserAssignmentValidation.cs:124   SC-17 の辞書照合
AuthorizationService/Domain/AbacEvaluator.cs:87               MatchesUserConditions
```

**除外**: `.ai-context/specs/`（確定済み記録）／`src/ai-stock-trading`（submodule）／`bin` `obj`。

### 母集合 D: 計画側の定義

`07_abac-attribute-model` §利用者属性の表は `department` / `roles` / `clearance` / `projects` の
4 つで、**`tags` を持たない**。同書 §タグ は**文書のタグ**を論じており、「タグを付けるのは SC-05 の
管理者であり、取り込み経路はタグを生成しない」と定める。一方 `ADR-0062` 決定 2 は
「**無人アカウントへ割り当てられる機密区分とタグの集合は、登録者自身が持つ集合の部分集合**」と
**利用者のタグ**を前提にする。**計画内で食い違っている**（§計画書との差異）。

dev seed の属性辞書（`deploy/local/abac-seed/attributes.json`）にも `tags` は無く、
**scope=user のキーは `clearance` / `department` の 2 つだけ**である（実測）。

## 設計

### 決定 1: `tags` / `projects` は**集合値キー**であり、正の器は Keycloak の多値属性である

`ADR-0062` 決定 2 が「タグの**集合**」と書く以上、単一値（1 利用者 1 タグ）は採れない。
`07_abac-attribute-model` の `projects`（参加プロジェクト）も同じく集合である。

### 決定 2: 契約（1 キー 1 値）上の線上表現は**カンマ区切り**とし、連結と分割を対で 1 か所ずつ置く

契約 `PlatformUserDto.Attributes` は `Dictionary<string,string>` である（変えない —— 変えると
`check-contract-schema.js` の破壊的変更であり、BFF・SC-17・判定側の全経路に波及する）。
**集合値キーだけ**、Keycloak の配列を**カンマで連結**して契約へ載せる。分割規則は
`ServiceAccountAttributeSubset.Tokens`（カンマ／空白区切り）を据え置く。

**符号化の語彙・連結・分割を `Platform.Shared.Contracts` の 1 型（`UserAttributeEncoding`）へ置く。**
🔴 **AuthorizationService と McpServer は互いを直接参照できない**（`src/README.md` の依存規則）。
分割規則を 2 か所に写すと**まさに本 issue の食い違いを再生産する**ので、契約側に 1 つ持つ。

これにより 2 つの保存形が**同じ集合へ読める**（どちらも許容する）。

| Keycloak の保存形 | 契約の線上表現 | 分割後 |
| --- | --- | --- |
| `["sales","hr"]`（正準） | `"sales,hr"` | `{sales, hr}` |
| `["sales,hr"]`（SC-17 の単一値書き込みの結果） | `"sales,hr"` | `{sales, hr}` |

### 決定 3: 単一値キーの畳み込みは**変えない**

`clearance` / `department` は単一値である。多値が来たら従来どおり先頭を採る
（`BffScopeResolver` も `AbacEvaluator` も 1 値しか読まない）。
🔴 **ここを一律にカンマ連結へ変えると、`clearance: ["internal","public"]` が `"internal,public"` に
なり、階段ポリシーがどれもマッチしなくなる**（fail-closed だが静かに壊れる）。**キーで分ける。**

### 決定 4: SC-17 の辞書照合は集合値キーだけ**要素ごと**に行う

`UserAssignmentValidation` は値全体を `AllowedValues` へ突き合わせる。集合値キーでは
**分割して各要素を突き合わせる**（空要素はエラー）。そうしないと画面から集合を作れず、
決定 1 の「正の器は多値」が画面側で成立しない。**単一値キーの判定は 1 文字も変えない。**

### 決定 5: 書き込みも集合値キーは配列へ展開する

`ReplaceAttributesAsync` は 1 キー 1 値を単一要素の配列へ写している。集合値キーは
**分割して多値配列**で書く（正準形）。読み戻し（決定 2）で同じ文字列に戻るため
`EnsureAttributesWereApplied` の突合は保たれる。

### 倒せなかったところ / 変えないところ

- 🔴 **`AbacEvaluator.MatchesUserConditions` は変えない。** 利用者条件が集合値キー
  （`projects` 等）を名指すと、値全体（`"a,b"`）と許可値を比べて**どれにもマッチしない**（deny 側）。
  ポリシー評価の意味論（「利用者の属性値が許可値集合に含まれる」）を「交差が空でない」へ変えるのは
  **計画の判定規則そのものの変更**であり、実装側で決めてよい範囲を超える。**環流する。**
- **契約 `Dictionary<string,string>` は据え置く**（決定 2 の理由）。
- **`tags` を属性辞書へ足さない。** 辞書は SC-09 で管理者が定義するものであり、
  実装が seed へ足すと**計画が定めていない利用者属性を実装が新設する**ことになる（環流する）。

## 受け入れ基準

1. **陽性（本件の本体）**: Keycloak が `tags: ["sales","hr"]` を返す登録者は、`hr` を配れる。
2. **陰性対照**: 同じ登録者は `finance` を配れない（400・値を名指し）。
3. **陽性（もう 1 つの保存形）**: `tags: ["sales,hr"]`（単一値のカンマ列）でも同じ集合になる。
4. **単一値キーの退行なし**: `clearance: ["internal","public"]` は従来どおり `"internal"`。
5. **書き込み**: `tags="sales,hr"` の差し替えは Keycloak へ `["sales","hr"]` として送られる。
6. **SC-17**: 集合値キーは要素ごとに辞書照合される（`"sales,hr"` は両方が許可値なら通る／
   片方が辞書外なら**その値を名指して**落ちる）。単一値キーの判定は不変。
7. **変異試験**: 連結を先頭 1 値へ戻すと 1 が落ちる。要素ごとの照合を全体照合へ戻すと 6 が落ちる。
8. **記録**: `tags` が利用者スコープの属性辞書に無い現状を SC-12 の画面仕様へ残す。

## テスト方針

- `KeycloakIdentityAdminClientTests`: 受け入れ基準 1・3・4・5（写像と PUT 本文）。
- `AuthorizationServiceRegistrarAttributesTests`（#1242 で新設）: 受け入れ基準 1・2・3 を
  **登録者の解決から部分集合判定まで**通して固定する。
- `UserAssignmentValidationTests`: 受け入れ基準 6。
- 新設 `UserAttributeEncodingTests`（契約側）: 語彙・連結・分割の対称性。

## 実測（本作業で実行した。宣言ではない）

- `dotnet build src/platform/backend/backend.slnx` → 0 警告 0 エラー。
- `dotnet test`（platform 全ユニット）→ 新設・改修分はすべて緑
  （`Platform.Shared.Infrastructure.Tests` 283 / `AuthorizationService.Tests` 158 /
  `McpServer.Tests` 125）。
- `node scripts/check-contract-schema.js` → 型の追加 1 件（非破壊）。`--update` で baseline を更新し
  同じ PR に含めた。

### 変異試験（5 種。いずれも**対になる別のテストが落ちる**）

| # | 変異 | 落ちたテスト |
| --- | --- | --- |
| 1 | 集合値キーの連結を先頭 1 値へ戻す | `集合値キーの多値属性は畳まずに連結される` / `集合値キーの差し替えは多値配列として送られる`（2 件） |
| 2 | SC-17 の要素ごと照合を値全体の照合へ戻す | `ValidateAttributes_accepts_a_set_valued_tag_element_by_element` / `..._names_the_single_set_element_outside_the_dictionary`（2 件） |
| 3+4 | **すべてのキー**を集合値として扱う（連結・分割の両方） | `ValidateAttributes_does_not_split_single_valued_keys` / `単一値キーは従来どおり先頭だけを読む` / `It_maps_multi_valued_attributes_to_a_single_value`（3 件） |
| 5 | 契約側 `Split` が分割しない | McpServer 10 件 ＋ 契約側 8 件（登録者のタグ集合・既存の `Tokens` 依存経路） |
| 6 | 書き戻しの突合を序数比較へ戻す | `集合値キーは正準化後の集合として反映を突き合わせる`（3 件） |
| 7 | 集合値キーの突合を素通しにする | `集合値キーでも要素が落ちていれば失敗として上げる`（1 件） |

🔴 **3+4 が捕まることが重要である** —— 「一律に集合値として扱う」実装は 1・2 の陰性をすべて通す。
**単一値キーの陰性対照が無いと、階段ポリシーを静かに壊す変異が緑で通る。**

### 自分の変更で新たに作りかけた欠陥（自己レビューで捕まえた）

🔴 **決定 5（書き込みを配列へ展開する）を入れた時点で、`EnsureAttributesWereApplied` が
偽の失敗を上げるようになっていた。** 同関数は要求値と読み戻し値を**序数**で比べる。集合値キーは
正準化（分割して書き、連結して読む）を通るため、`tags = "sales hr"` と要求すると `"sales,hr"` が
返り、**realm の設定不備でもないのに「Keycloak が受け付けなかった」と例外になる**。

集合値キーだけ**集合として**比べる形へ直した。🔴 **緩めた結果「黙って捨てられた」を見逃したら
緩めた意味が無い**ので、陰性対照（要素が本当に落ちていれば失敗として上げる）を対で置いた
（変異試験 6・7）。

### 未実測

- 🔴 **稼働 k3s での実測は行っていない。** 再現には一時利用者へ多値 `tags` を付ける必要があるが、
  **利用者スコープの属性辞書に `tags` が無い**ため、SC-17 経由では作れない。
  Keycloak Admin REST で直接付ければ作れるが、**辞書に無い属性を稼働 realm へ入れることになる**
  ので行わなかった。符号化の往復は単体で固定した。
- **`LlmGateway.Tests` に 1 件の赤がある**（`LlmSyntheticUsageExclusionTests.
  PostCompleteStream_WhenSynthetic_ExcludesFromCostAndCountsExclusion`）。**本変更とは無関係**で
  ある —— 本作業の変更ファイルを 1 つもコンパイル閉包に含めない状態（新規の
  `UserAttributeEncoding.cs` を退避して当該プロジェクトだけをビルド）でも**同じ 1 件が落ちる**
  （陰性対照）。develop の `8623c702` で入った既存欠陥である。

## 計画書との差異

🔴 **計画内の食い違いを見つけた（環流する）。**

1. `ADR-0062` 決定 2 は**利用者のタグ集合**を前提にするが、`07_abac-attribute-model` §利用者属性の
   表に `tags` が無い（同書 §タグ は文書のタグを論じている）。**したがって
   「無人アカウントへタグを配る経路」は属性辞書の側が成立していない。**
2. `projects` は §利用者属性 の表にあるが**集合**であり、§ポリシー評価モデルの
   「利用者の属性値が許可値集合に含まれる」は**単一値を前提にした述語**である。
   集合値キーに対する評価の意味論が定まっていない。

**どちらも planning へ起票した（planning#545）。** 実装側では
決定 1〜5 の範囲に留め、辞書の新設も評価器の意味論変更も行わない。

## 未決事項

- 稼働 k3s での実測（一時利用者に多値 `tags` を付けて 400/201 を確認する）の可否は §実測 に書く。
