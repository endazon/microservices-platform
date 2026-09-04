---
title: 作業仕様書 — MCP サービスアカウントの project 属性割当禁止と、サービスアカウント実行経路での一律除外（#1190）
type: spec
status: done
related_ids:
  - FR-16
  - UC-08
  - UC-09
  - SC-12
  - ADR-0024
  - ADR-0034
  - ADR-0054
  - ADR-0062
  - IADR-0269
  - IADR-0373
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0032_mcp-non-exposure-is-enforced-by-attributes-not-the-allowlist.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
---

# 作業仕様書: MCP サービスアカウントの project 属性割当禁止と、サービスアカウント実行経路での一律除外

## 起点となる計画書（トレーサビリティ）

- 起点 issue: #1190（AST 起点は AST#662、AST 実装 PR は AST#665）
- 計画 ADR（AST）: `AST/ADR-0032` 決定 2（(2) サービスアカウントへの割当禁止 / (3) CI スキーマ検証）・決定 3
- 計画 ADR（MSP）: `ADR-0024`（2026-08-02 注記）／`ADR-0034` 決定 9（サービスアカウント実行経路での
  一律除外）／`ADR-0054`（`doc_scope` の前例）／`ADR-0062` 決定 2・3（登録と差し替えが同じ関数を呼ぶ）
- 関連 IADR: `IADR-0269` 決定 8（検証関数を構成検証と管理 API で共用する）
- 本作業の実装 ADR: `IADR-0373`
- FR-16（MCP サーバー）／UC-08・UC-09／SC-12

## 目的・背景

`AST/ADR-0032` 決定 2 は、AST の文書を MCP から到達不能にする手段を**許可リストではなく文書属性と
認可**と定め、`private-note` の前例と同じ 3 点構成を採った。

1. AST が保存する文書は `project=ai-stock-trading` を必須で持つ（**AST 側で実装済み**。AST#665）。
2. MCP のサービスアカウントへ、この値を含む属性割当を構成上禁止する。← 本作業
3. CI のスキーマ検証で弾く。← 本作業

### 🔴 (2) だけでは統制が 1 ミリも働かない

`AST/ADR-0032` 決定 3 と #1190 の「明記してほしいこと」が述べるとおり、**基盤の ABAC は `project` を
判定軸に持たない**（`07_abac-attribute-model` §具体判定規則 の評価式に現れるのは
`confidentiality` / `department` / `lifecycle` / `owner` / `shared_with` だけ）。

したがって —— **サービスアカウントに `project=ai-stock-trading` が付いていても付いていなくても、
AST の文書への到達可否は変わらない。** (2) の割当禁止は「設定ミスを防ぐ」だけで、**到達は 1 件も
止まらない。**

`private-note` が実際に効いているのは ABAC ではなく **`ServiceAccountDocumentFilter`（サービス
アカウント実行経路での一律除外。`ADR-0034` 決定 9）** である。`AST/ADR-0032` 決定 3 の但し書きも
「`private-note` の前例が働く仕組みはサービスアカウント実行経路での一律除外であり、汎用の ABAC
ポリシー評価に依存しない。**同じ形を採れば** …成立する」と書いている。

**#1190 は「どちらの設計を採るかを含めて基盤側の裁量に委ねる」としている。** 本作業は
**「`ServiceAccountDocumentFilter` へ `project` 判定を足す」を採る**（`AST/ADR-0032` 決定 3 が
名指しした 2 案のうち前者。ABAC 判定軸への追加は採らない）。理由は `IADR-0373` に記す。

## 対象範囲

### 母集合（自分で走査した。issue の記述は転記していない）

**走査 1 —— 前例（`private-note`）が MCP のサービスアカウント経路で効いている点を全数で引く。**

```
$ git grep -n "DocumentScope" -- . ':!src/ai-stock-trading'
  → 追跡下で 89 行。うち **`McpServer.Domain.DocumentScope`（platform の MCP 経路）を使うのは 3 行だけ**:
      src/platform/backend/Services/McpServer/Domain/ServiceAccountDocumentFilter.cs:25   実行経路の一律除外
      src/platform/backend/Services/McpServer/Domain/ToolPublicationConfigValidator.cs:70 割当禁止（構成検証）
      src/platform/backend/Services/McpServer/Domain/ToolPublicationConfigValidator.cs:74 同上（メッセージ）
```

残り 86 行は **knowledge ユニット側の別語彙**である —— `Knowledge.Contracts.DocumentScopes`
（`AiInputExposure`。RAG の AI 入力除外）、`GraphService.GraphDocumentScope`、
`DocumentService.DocumentAttributes`。**これらは `private-note` を「AI へ渡すか」「グラフに出すか」
という別の統制で見ており、`ADR-0034` 決定 9（MCP サービスアカウント経路）ではない。**
可変ユニットから platform の McpServer は参照できない（`IADR-0274` / `IADR-0292`）ので、**本作業は
platform の McpServer に閉じる**。

**走査 2 —— 共用されている検証関数の呼び出し点。**

```
$ git grep -rn "ValidateServiceAccountAttributes" -- src/
  ToolPublicationConfigValidator.cs      定義（2 オーバーロード）＋ Validate() からの呼び出し
  McpClientEndpoints.cs:68               管理 API（登録・差し替え）からの呼び出し
```

→ **呼び出し点は 2 つで、どちらも同じ 1 関数を通る**（`IADR-0269` 決定 8 / `ADR-0062` 決定 3
「登録だけ塞いで差し替えが緩い形」を許さない）。**したがって関数 1 つを直せば両方に効く。**

**走査 3 —— 属性キーの綴り（陽性対照つき）。**

```
$ grep -n "project" planning:.../06_technical/07_abac-attribute-model.md
  41: | `project`（プロジェクト） | 任意 | プロジェクトコード | プロジェクト限定 |   ← 文書側
  82: | `projects` | Keycloak／グループ | 参加プロジェクト |                        ← 主体側
```

🔴 **文書側は単数 `project`、主体側は複数 `projects` である。** #1190 の本文は
`attributes["project"]` と書き、`AST/ADR-0032` 決定 2 (2) は「サービスアカウントの `projects` に
`ai-stock-trading` を入れない」と書いている。**割当は主体側の属性なので綴りは `projects` が正だが、
`project` で書かれても素通りさせない** —— **両方の綴りを禁止対象にする**（片方だけ塞ぐと、綴りを
変えただけで抜ける）。

陽性対照: 同じ走査で `confidentiality` / `department` / `owner` / `shared_with` / `lifecycle` は
§具体判定規則の評価式に現れるが、**`project` は現れない**（＝ ABAC が見ていないことの実測）。

**走査 4 —— realm に MCP サービスアカウントの属性割当があるか。**

`deploy/keycloak/microservices-platform-realm.json` を実際にパースして数えた。

```
clients=11 / serviceAccountsEnabled=3
  abac-seeder                      client attributes={}
  identity-admin                   client attributes={}
  ai-stock-trading-kb-writer       client attributes={}

service-account users=3
  service-account-abac-seeder                  attributes={"clearance":["restricted"],"department":["engineering"]}   ← 陽性対照
  service-account-identity-admin               attributes={}
  service-account-ai-stock-trading-kb-writer   attributes={}

users with project/projects attr = 0
```

→ **realm に MCP クライアントは 1 つも無く、`project` / `projects` を持つ主体も 0 である。**
陽性対照（`abac-seeder` が `clearance` / `department` を実際に持つ）を対で置いたので、この 0 は
「読めていない 0」ではない。

🔴 **`ai-stock-trading-kb-writer` は AST が DocumentService へ書くための機密クライアントであって
MCP クライアントではない**（`AST/ADR-0032` 決定 2 (2) が禁じるのは **MCP の**サービスアカウントへの
割当である）。**したがって realm へ検査を置くと、統制の対象でないものを対象にしてしまう。**

MCP クライアントの属性は `McpDbContext`（`McpClient.Attributes`）と公開構成 JSON が持つ。
**`scripts/check-realm-constraints.js` は本件の置き場所ではない**（同スクリプトが見ているのは MFA・
SMTP・サーバ間 URL であり、MCP クライアント属性ではない）。**issue が指す「①②と同じ場所・同じ形」は
`ToolPublicationConfigValidator` である。**

### 対象範囲

- **対象**: `src/platform/backend/Services/McpServer/Domain/` の 3 ファイル（新規 1・既存 2）と
  その単体テスト 2 ファイル。
- **対象外（明示）**:
  - **ABAC の判定軸へ `project` を加えること。** `AST/ADR-0032` 決定 3 が挙げた 2 案のうち後者で
    あり、`07_abac-attribute-model` §必須指定と実データの乖離 と同じ性質の作業（ポリシー契約・
    `/authz/scope`・選言の制約に波及する）。**本作業では採らない**（`IADR-0373` §決定 3）。
  - **文書保存時に `project` を必須化すること**（`AST/ADR-0032` フォローアップ 2 の別項目）。
    `DocumentAttributes` が静的に検証しているのは `confidentiality` 1 つだけであり、必須化は
    別の射程である。
  - **AST 側の作業**（`IADR-0120`。本リポジトリからは変更しない）。AST#662 / AST#665 で (1) は完了済み。
  - **knowledge ユニット側の `DocumentScopes` / `GraphDocumentScope`**（走査 1 のとおり別統制）。

## 設計

### 1. `McpServer.Domain.RestrictedProject`（新規）

`DocumentScope` と**同じ形**にする（1 ファイル 1 語彙・集合帰属で判定）。

```
DocumentKey = "project"     // 文書側の属性キー（07_abac-attribute-model §基本属性）
SubjectKey  = "projects"    // 主体側の属性キー（同 §利用者属性）
Values      = { "ai-stock-trading" }   // 大文字小文字を問わない集合
```

- `IsRestricted(documentAttributes)` —— 🔴 **集合帰属で判定する。**「`ai-stock-trading` でない」
  と否定で書くと、`project` を持たない既存文書がすべて該当して**組織文書が一斉に落ちる**
  （`DocumentScope` が同じ理由で集合帰属を選び、`ADR-0036` D-04 も評価の性質をそう定めている）。
- `AssignedValues(subjectAttributes)` —— `project` / `projects` の**両綴り**を見て、制限値に
  当たるトークンを**入力の綴りのまま**返す（拒否理由に載せる。`ADR-0062` §結果「理由を丸めない」）。
  多値の分解は **`ServiceAccountAttributeSubset.Tokens` を再利用する**（分割規則を 2 つ持たない）。

**値の集合を構成（appsettings）から読まない。** 構成にすると**統制を無効化する抜け道**になる
（`check-stack-ready.js` G3「抜け道の環境変数を置かない」と同じ理由）。ユニットが増えたら
この 1 箇所へ足す。

### 2. `ToolPublicationConfigValidator.ValidateServiceAccountAttributes`（既存を拡張）

`private-note` の判定の隣に `RestrictedProject.AssignedValues` の判定を足す。**呼び出し点 2 つ
（公開構成のスキーマ検証・管理 API）は関数を共用しているので、直すのは 1 箇所**（走査 2）。

🔴 **両方の違反を返す**（`private-note` を見つけたら早期 return する現在の形を、エラーを積む形へ
変える）。丸めると「もう 1 つの違反は次回の実行で気づく」ことになる。

### 3. `ServiceAccountDocumentFilter`（既存を拡張）

`private-note` と**同じ後段**で、`RestrictedProject.IsRestricted` に当たる文書も落とす。

- **ツール名で分岐しない**（既存の方針どおり全ツール共通の後段）。
- **件数（`TotalCount`）からも引く**（`ADR-0034` 決定 2・4 の存在秘匿。既存の実装をそのまま使う）。
- **有人実行では落とさない**（`AST/ADR-0032` 決定 2「有人経路（利用者本人）は従来どおり本人権限で
  読める。ADR-0012 が閉じるのは MCP という外部エージェント向け経路だけ」）。

### 4. 「CI のスキーマ検証」の実体（(3)）

`private-note` について既に置かれているのは **`dotnet test` が走らせる単体試験**
（`ToolPublicationConfigValidatorTests`）と **起動時 fail-fast**
（`ToolPublicationConfigLoader` → `ToolPublicationFailFastTests`）である。**新しい機構は作らず、
同じ 2 か所へ `project` の対を足す**（issue の「①②と同じ場所・同じ形」）。

## 受け入れ基準

- [x] 公開構成の `service_account_attributes` に `projects=ai-stock-trading` を書くと、スキーマ検証が
      エラーを返す（＝起動が fail-fast する）
- [x] 綴りが `project`（単数）でも同じく弾かれる
- [x] 管理 API（登録・差し替え）でも同じ関数を通って 400 になる（関数共用の維持を試験で固定する）
- [x] `private-note` と `project` を同時に含む割当では**両方の理由が返る**（1 件へ丸めない）
- [x] サービスアカウント実行では `project=ai-stock-trading` の文書が応答にも件数にも現れない
- [x] 🔴 陽性対照: **有人実行では同じ文書が返る**／**`project` を持たない文書は落ちない**／
      **別の `project` 値の文書は落ちない**（否定形で書いていないことの証明）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が platform ユニットで通る

## テスト方針

- `ToolPublicationConfigValidatorTests` に (2)(3) の陰性・陽性を足す（既存の `private-note` の
  ケースと対になる形）。
- `ServiceAccountDocumentFilterTests` に一律除外の否定形 3 件と**陽性対照 3 件**を足す。
  🔴 **陽性対照が無いと「全部落としている実装」と区別できない**（既存テストの冒頭コメントが
  そう書いている）。
- `McpClientEndpoints` 経由の 400 は既存の統合／単体テストの形に合わせる。

## 計画書との差異

- 差異: なし。ただし **#1190 が「基盤側の裁量に委ねる」とした設計判断を本作業で確定させた**
  （`ServiceAccountDocumentFilter` 拡張を採り、ABAC 判定軸への追加は採らない）。根拠は `IADR-0373`。

## 未決事項・親への申し送り

1. **`AST/ADR-0032` フォローアップ 2 のうち、本 PR が満たすのは「割当の禁止」「CI 検証」＋
   「実行経路での一律除外」である。** 残るのは **①文書保存時の `project` 必須化**（基盤側で
   「任意」のままなら付与漏れが検出されない。`AST/ADR-0032` §結果 が自ら挙げたトレードオフ）と
   **②ABAC 判定軸への `project` 追加**。どちらも別 issue が要る。
2. **他ユニットが基盤 KB を使う場合の一般化は計画側の判断**（`AST/ADR-0032` フォローアップ 4）。
   実装側は `RestrictedProject.Values` の 1 箇所に集合を持たせてあるので、値を足すだけで済む。
3. AST 側の残射程は無い（AST#662 / AST#665 で (1) は実装済み）。
