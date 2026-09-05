---
title: 登録者が配れる機密区分は「confidentiality を条件に持つ分岐」からだけ読み、フィルタの不在を無制限と読まない
type: spec
status: done
related_ids: [FR-16, FR-05, FR-09, UC-09, SC-12, ADR-0062, ADR-0036, ADR-0004]
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/05_screens/01_screens.md#sc-12
---

# 仕様書: 登録者の認可スコープに `confidentiality` フィルタが無いことを「無制限」と読まない（issue #1242）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-16（MCP サーバー連携）・FR-05 / FR-09（ABAC）
- ユースケース（UC）: UC-09
- 画面（SC）: SC-12（MCP クライアント登録管理）
- 関連 ADR: ADR-0062 決定 2・3（部分集合の判定）／ADR-0036 D-01・D-02（`read` への所有者条件と
  `${current_user}` 束縛）／ADR-0004
- 実装 ADR: IADR-0385（本件の決定）／IADR-0366（本判定の置き場所と形。**決定 3 が本件の誤りの出所である**）／
  IADR-0253（認可スコープ契約の選言）／IADR-0373（MCP の `project` 除外）
- issue: #1242（出所は #1185 のフェーズ末監査）

## 目的・背景

`AuthorizationServiceRegistrarAttributes.ResolveClearanceAsync` は、登録者が読める機密区分の集合を
`POST /authz/scope`（action=read）の応答から読む。現行の読み方は次のとおりである。

```csharp
var filter = scope.AllowedFilters.FirstOrDefault(f => f.Key == "confidentiality");
return filter is null ? (true, []) : (false, filter.AllowedValues);
```

**`confidentiality` のフィルタが「無いだけ」で `ClearanceUnrestricted = true`（無制限）へ倒れる。**
`ServiceAccountAttributeSubset.Validate` は `ClearanceUnrestricted` のとき `clearance` の突き合わせを
丸ごと飛ばすため、**所有者ベースの read ポリシーだけにマッチする登録者が `restricted` の無人アカウントを
作れる。** ADR-0062 が塞いだ昇格経路そのものである。

契約（`AccessScopeDto.cs`:29）が「条件無しで許可（全件可）」と定めるのは **`AllowedFilters` が空**の
ときだけであり、**`owner` だけを持つ（空ではないが `confidentiality` を持たない）**場合は含まれない。

**これは fail-safe の向きを取り違えた欠陥である** —— 「その軸のフィルタが無い」を「その軸では
制約が無い」と読んだ。**正しくは「その軸で許可する根拠が無い」であり、deny 側へ倒す。**

## 母集合の取り方（着手前に自分で引いた。陽性対照つき）

`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方（規則 2・9・10）に従い、
issue 本文の実測を転記せず自分で引いた。基点 `develop` `3d5f8c99`。
`git rev-parse --is-shallow-repository` → **`false`**（履歴は打ち切られていない）。

### 母集合 A: 認可スコープを「特定のキーへ射影して値集合を得る」経路

```console
$ grep -rn "f => .*\.Key\|filter.Key ==\|\.Key, .*Key," --include=*.cs src/ | grep -v /obj/ | grep -v Tests/
src/ai-stock-trading/.../LlmUsageRecord.cs:114        （別件・辞書の整列）
src/platform/.../AuthorizationService/Domain/AbacEvaluator.cs:52   （union の突き合わせ。射影ではない）
src/platform/.../McpServer/Infrastructure/ExternalServices/AuthorizationServiceRegistrarAttributes.cs:143
```

**陽性対照**: 同じ走査が `AbacEvaluator.cs:52` と submodule 側の別件を当てている（空振りしていない）。
**射影して値集合を得る production の経路は 1 つだけである。**

### 母集合 B: `AllowedFilters` / `Branches` を読む production の全経路（射影でないものの確認）

```console
$ grep -rn "AllowedFilters" --include=*.cs src/ | grep -v /obj/ | grep -v Tests
AiAnalysisService/Domain/DataRangeScopeResolver.cs:42     文書 1 件の突き合わせ
GraphService/Domain/AbacNodeFilter.cs:45,49               ノード 1 件の突き合わせ
WikiService/Domain/AbacPageFilter.cs:40,44                ページ 1 件の突き合わせ
Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs:44  中継（読まない）
McpServer/.../AuthorizationServiceRegistrarAttributes.cs:142    ★ 射影
RetrievalService/Features/Search/AttributeValues/Endpoint.cs    ベクトルDB へ制約として渡す
```

**「文書 1 件が分岐の連言を満たすか」を見る経路では、キーの不在は正しく「その文書には条件を課さない」
である。** 射影（キー → 値集合）だけが「不在＝無制限」という別の意味を持ち込む。**したがって是正の
対象は 1 箇所であり、他の 5 経路は変えない。**

### 母集合 C: `ClearanceUnrestricted` を読む箇所

```console
$ grep -rn "ClearanceUnrestricted" --include=*.cs src/ | grep -v /obj/
McpServer/Domain/Ports/IRegistrarAttributeResolver.cs（定義・注記）
McpServer/Domain/ServiceAccountAttributeSubset.cs:（Validate の分岐）
McpServer/Infrastructure/ExternalServices/AuthorizationServiceRegistrarAttributes.cs（生成）
McpServer/Tests/StubRegistrarAttributeResolver.cs（ヘッダで注入）
```

**除外**: `.ai-context/specs/`（確定済み記録・書き換えない）／`src/ai-stock-trading`（submodule）。

## 顕在化の再現（陽性対照つき）

### 1. 現 seed では発現しない（陽性対照）

`deploy/local/abac-seed/policies.json` の `read` ポリシーは 4 本とも
`userConditions: {clearance: […]}` × `documentConditions: {confidentiality: […]}` の階段であり、
所有者ベースは `write` の 1 本だけである。したがって `read` のスコープには必ず
`confidentiality` フィルタが載り、`filter is null` の枝へ入らない。**#1185 の稼働再測で
`clearance=internal` の管理者が `restricted` を拒否されたのはこのためである。**

### 2. ADR-0036 の `read` 所有者条件で顕在化する

ADR-0036 は `read` 許可を「属性ベース ∨ **所有者ベース**（`doc.owner ∈ {${current_user}}`）∨
共有先ベース」の選言と定める。所有者ベースのポリシーは典型的に `userConditions` を持たない。
`AbacEvaluator.MatchesUserConditions` は `conditions == null` を**全利用者マッチ**として扱うため、
**`clearance` 属性を持たない（あるいは階段の値域外の）利用者にも所有者ポリシーだけがマッチする。**

そのとき `ResolveScope` は `Granted=true` / `AllowedFilters=[owner:["${current_user}"]]` /
`Branches=[{"所有者", [owner:[u1]]}]` を返す。**`confidentiality` フィルタは存在しない** →
現行コードは無制限 → `restricted` の無人アカウントが 201 で作れる。

**この 2 つを機械で固定する**（下の受け入れ基準 1・2）。

## 設計

### 決定: 「その軸のフィルタが無い」は「その軸では何も許可しない」へ倒す

読み取りを次の規則へ置き換える（`ResolveClearanceAsync` → `ReadAssignableConfidentiality`）。

1. `Granted == false` → **空集合**（従来どおり）。
2. `Branches` が 1 件以上 → 分岐ごとに見る。
   - フィルタを 1 つも持たない分岐 → **無制限**（契約と計画の
     「マッチしたポリシーに文書条件が無い場合は全件許可する」がここに当たる。
     07_abac-attribute-model §ポリシー評価モデル）。
   - フィルタがちょうど 1 つで、そのキーが `confidentiality` → **その許可値を足す**。
   - それ以外の分岐（`owner` だけ／`confidentiality` ＋ 他キーの連言）→ **何も足さない**。
3. `Branches` が空／null（未移行の発行者。契約の後方互換規則） →
   - `AllowedFilters` が空 → **無制限**（契約 `AccessScopeDto.cs`:29 の明文）。
   - `AllowedFilters` のキーが `confidentiality` **ただ 1 つ** → その許可値。
   - それ以外（`owner` を含む等） → **空集合**。

**なぜ「`confidentiality` ＋ 他キー」の分岐を数えないのか。** サービスアカウントは登録者の
所有権も部門も継がない。`{owner: u1, confidentiality: [restricted]}` は「**自分が持つ** restricted 文書を
読める」であって、「restricted を読める」ではない。**継がない条件が混ざった分岐から値を取り出すのは、
IADR-0366 決定 3 が避けようとした誤りと同型である。**

**過小に倒れる可能性は受容する。** 07_abac-attribute-model は「消費側が選言へ対応するまで、
**多キーの文書条件を持つポリシーを運用しない**」を暫定の統制として定めており、多キーの分岐は
運用上そもそも存在しない。**現 seed では 1 件も落ちない**（受け入れ基準 3 の陽性対照）。

### 倒せなかったところ（fail-safe の向きの記録）

- **`AllowedFilters` が空 ＝ 無制限は残す。** 契約の明文であり、計画（同 §具体判定規則）も
  「文書条件が無いポリシーは全件許可」と定める。**ここまで deny へ倒すと、計画が許可と定めた形を
  実装が黙って狭める。** 倒さない代わりに、**「空である」ことを積極的に確かめる**形へ変えた
  （不在を推論しない ——`Count == 0` を条件に書く）。
- `ClearanceUnrestricted` という型そのものは残す。消す（＝全値を列挙して返す）と、値域の正が
  ポリシーから実装側の列挙へ移り、IADR-0366 決定 2 が避けた「階段表を持つ」形に戻る。

### MCP の無人アカウント経路との噛み合わせ

本変更は**登録時の割当**にだけ効く。実行時の後段除外（`ServiceAccountDocumentFilter`。
ADR-0034 決定 9 の `private-note` ＋ IADR-0373 の `project=ai-stock-trading`）は独立の軸であり、
**どちらも変えない**。両者が二重に掛かることを退行防止のテストで確かめる（受け入れ基準 6）。

## 受け入れ基準

1. **陰性対照**: 認可スコープが `Granted=true` / `AllowedFilters=[owner:[…]]`（`confidentiality` を
   持たない）のとき、登録者は `restricted` を配れない（400・値を名指し）。
2. **顕在化の再現**: `AbacEvaluator` が ADR-0036 の所有者 `read` ポリシーに対して
   「`Granted=true` かつ `confidentiality` フィルタ無し」を返すことを固定する
   （上の陰性対照の入力が机上の作り物でないことの担保）。
3. **陽性（回帰）**: 階段ポリシー（現 seed と同型）では従来どおり配れる。
   `confidentiality:[public,internal]` の登録者は `internal` を配れ、`restricted` は配れない。
4. **陽性（無制限）**: `Granted=true` かつ `AllowedFilters` が空・`Branches` が空なら無制限。
   フィルタを 1 つも持たない分岐が 1 本でもあるときも無制限。
5. **`Granted=false`** は従来どおり空集合（「引けなかった」と混ぜない）。
6. `ServiceAccountDocumentFilter`（`private-note` / `project`）は本変更後も従来どおり効く。
7. **変異試験**: 新しい判定（分岐の読み分け）を旧実装へ戻すと、1 の陰性対照が落ちる。

## テスト方針

- `McpServer.Tests/Infrastructure/ExternalServices/AuthorizationServiceRegistrarAttributesTests.cs`
  （新規）: **本物の `AuthorizationServiceRegistrarAttributes`** に対して `HttpMessageHandler` を
  スタブし、`/authz/users` と `/authz/scope` の応答を差し替える。
  **現在この実装に対する単体テストは 1 本も無く、スタブ resolver 経由だけで緑になっていた。**
- `AuthorizationService.Tests/Domain/AbacEvaluatorTests.cs`: 受け入れ基準 2 を追加する。
- 既存の `ServiceAccountAttributeSubsetEndpointTests` は変更しない（経路の固定は従来どおり）。

## 実測（本作業で実行した。宣言ではない）

基点 `develop` `3d5f8c99`（`git rev-parse --is-shallow-repository` → `false`）。

- `dotnet build src/platform/backend/backend.slnx` → 成功・0 警告 0 エラー。
- `dotnet test McpServer.Tests` → 120/120 合格（新設 14 本を含む）。
- `dotnet test AuthorizationService.Tests` → 150/150 合格（顕在化の再現 1 本を含む）。
- **変異試験**: `ReadAssignableConfidentiality` を旧実装（`AllowedFilters` から引き、
  `filter is null` なら無制限）へ戻すと **13 本中 5 本が赤**（当時。受け入れ基準 6 の 1 本を足す前）。
  赤の内訳は陰性対照 3 本
  （`所有者ベースの分岐だけにマッチする登録者は機密区分を配れない` /
  `所有者分岐だけでは_restricted_を配れない` / `所有者と機密区分の連言は数えない`）＋
  `分岐を運ばない発行者で他キーが混ざる_union_は読まない` ＋
  `階段の登録者は自分より広い区分を配れない`。**残る 8 本は緑のまま通った**（＝陽性対照が
  変異で壊れていない＝「常に空集合を返す」実装でも通る試験ではない）。

### 未実測

- 🔴 **稼働 k3s での顕在化の実測は行っていない。** 再現には `deploy/local/abac-seed/policies.json`
  へ所有者ベースの `read` ポリシーを 1 本足す必要があり、**既存 seed を書き換えずには再現できない**。
  顕在化は `AbacEvaluator` の単体テスト（受け入れ基準 2）で機械に固定した。

## 計画書との差異

- 無し。ADR-0062 決定 2 の「部分集合」を、**より安全な側へ**正した変更である。
- **環流候補**: 07_abac-attribute-model の暫定「多キーの文書条件を持つポリシーを運用しない」が
  解除されると、本設計の「フィルタ 1 つだけの分岐を数える」は過小になる。解除の際は
  「所有権・共有先に依存しない分岐」を契約側で見分けられる形が要る。**今は環流しない**
  （暫定が生きている間は現実の欠落が無く、planning へ「将来こうなる」だけを送っても裁定材料にならない）。

## 未決事項

- 稼働 k3s での実測（所有者 `read` ポリシーを 1 本足して 400 を確認する）は、
  **既存 seed を書き換えずには再現できない**。実測の可否は §実測 に正直に書く。
