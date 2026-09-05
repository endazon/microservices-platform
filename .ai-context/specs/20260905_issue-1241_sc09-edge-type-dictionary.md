---
title: SC-09 に辺の型辞書の区画を足し、BFF に使用件数つきの読み取りと書き込み口を開ける（#1241）
type: spec
status: done
related_ids: [FR-17, SC-03, SC-09, SC-10, SC-18, SC-21, UC-05, UC-10, ADR-0033, ADR-0034, ADR-0066, IADR-0044, IADR-0119, IADR-0127, IADR-0129, IADR-0135, IADR-0152, IADR-0153, IADR-0242, IADR-0281, IADR-0388]
author: Claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
  - planning:projects/microservices-platform/INDEX.md
issue: "#1241"
---

# #1241: SC-09 の辺の型辞書と、BFF の書き込み口

## 起点となる計画書（トレーサビリティ）

- 画面: `SC-09`（管理者設定〔ABAC〕）。参照する面として `SC-10` / `SC-18` / `SC-03` / `SC-21`
- 機能要求: `FR-17`（文書間リンク＝知識グラフ）
- ユースケース: `UC-05`（ABAC 権限を管理する）
- 計画 ADR: `ADR-0033`（辺の型・データモデル。決定 3 = 3 層の値集合と `related` フォールバック、
  決定 9 = 削除拒否・改名追随）／`ADR-0034`（ホップごと ABAC）
- 計画 INDEX 決定 18: 「**辺の型は参照が 1 件でもあれば削除を拒否し、改名は許す**（既存の辺は
  新しい名前へ追随）。**同じ規則をタグ辞書にも適用**する。**`SC-10` に型ごとの使用件数を表示**する」
- 実装 ADR: `IADR-0129`（`SC-09` の未実装区画の理由）/ `IADR-0152` `IADR-0153`（タグ辞書の先例）/
  `IADR-0281`（`related` フォールバック）/ 本作業の `IADR-0388`

## 1. 事象（自分で測った。陽性対照つき）

### 1-1. `SC-09` に辺の型の区画が無く、不在を固定するテストだけが在る

```console
$ git grep -n "辺の型" -- src/knowledge/frontend/src/features/sc09-admin-abac
AdminAbacSettingsPage.test.tsx:429:    expect(screen.queryByRole('tab', { name: '辺の型' })).not.toBeInTheDocument();
AdminAbacSettingsPage.test.tsx:430:    expect(screen.queryByText(/辺の型/)).not.toBeInTheDocument();
AdminAbacSettingsPage.tsx:12://   - **辺の型辞書**: **着手保留**（IADR-0119。本画面の実装時点 = 2026-08-05）
```

**陽性対照**（同型の区画は実在する。作り方は既に在る）:

```console
$ ls src/knowledge/frontend/src/features/sc09-admin-abac/components
AdminAbacSettingsPage.tsx  AttributeDictionaryPanel.tsx  PolicyEditorPanel.tsx  TagDictionaryPanel.tsx
```

### 1-2. 保留の理由はコード自身が「失効した」と書き、判断先が消えている

`AdminAbacSettingsPage.tsx` L12-22 が「`ADR-0033` は `Accepted` へ移り保留は解除された。
**本区画を足すかどうかは #504 / #452 の作業仕様書で判断する**」で止まっている。**#504 は
CLOSED で、判断は残されなかった。** `SC-03` の導線（#1240）と**同じ機序**である。

### 1-3. 後段は完成している。欠けているのは画面と BFF だけである

`GraphService/Features/EdgeTypes/{Create,Rename,Delete,List,Catalog}/Endpoint.cs` が実在し、
削除拒否（`usage > 0` → 409 ＋ `ON DELETE RESTRICT`）と改名追随（Id 据え置き）は
`EdgeTypeEndpointsTests.cs` が固定している。認可も `read` = admin ＋ operator /
`write` = AdminOnly で**タグ辞書と同じ形**である。

### 1-4. BFF は読み取り 1 本だけで、しかも件数の無いほうを叩いている

```console
$ git grep -n "edge-types" -- src/knowledge/backend/Bff
GraphBffEndpoints.cs:80: g.MapGet("/edge-types", … "/graph/edge-types/catalog" …)
```

**陽性対照**（タグ辞書側は 4 本揃っている）: `TagDictionaryBffEndpoints.cs` に
`MapGet` / `MapPost` / `MapPut` / `MapDelete`。

## 2. 母集合（規則 9・10）

走査 1: `git grep -rn "4 区画"` → `AdminAbacSettingsPage.tsx` / `docs/screens/SC-09_admin-abac-settings.md`。
走査 2: `git grep -n "辺の型"` → 上記 ＋ `docs/screens/SC-09_admin-abac-settings.md`（対応表 #3・#16・#17・
§実装しない要素の理由 (a)・§未決事項 4）。
走査 3: `git grep -n "edge-types"` → BFF・OpenAPI・生成物・`sc18-graph` / `sc03-document` / `sc21-ai-suggestions` の
カタログ利用箇所。

**除外理由**: `.ai-context/specs/` と `.ai-context/superpowers/` は凍結記録のため本文を書き換えない。
`IADR-0119` / `IADR-0129` 本体は「保留は解除済み」と既に書いており、本作業で決定は変わらない
（解除済みの保留を消費するだけ）。

走査 4（規則 10 —— 是正後に新たに誤りになる自分の記述）:
**`AdminAbacSettingsPage.test.tsx` の「保留対象の ID をここへ書くと `check-test-traceability.js` が
誤報する」という注記**が誤りになる（着手したので `FR-17` を書いてよい）。実際に書き換えた。

## 3. 判断

### 判断 1: 画面の口は**新設**し、公開カタログは据え置く

issue の受け入れ基準 1 は「読み取りも使用件数つきの `/graph/edge-types` を叩くよう改める」と読めるが、
**現行の `/bff/graph/edge-types` をそう向け替えてはならない。** 同じ受け入れ基準が
「**画面の口と、公開カタログの口を取り違えないこと**」とも言っており、後者が正しい。

実測（陽性対照）: `/bff/graph/edge-types` の消費者は `sc18-graph` / `sc03-document` /
`sc21-ai-suggestions` の 3 feature であり、**いずれも一般利用者の画面**である。
向け替えると全員 403 になる。詳細は `IADR-0388` 決定 1。

→ **`/bff/edge-types` を新設**する（タグ辞書が `/bff/tags` であるのと同じ形）。

### 判断 2: 書き込みは admin 限定、読み取りは admin ＋ operator

後段 `EdgeTypeEndpoints` と同じ（`SC-10` が型ごとの使用件数を出すため読みは運用者にも開く）。
issue の「admin / operator 限定」は読み取りについては正しく、書き込みは admin のみが正しい。

### 判断 3: 409 と使用件数は素通しにする（`RelayAsync`。`ProxyAsync<T>` を使わない）

`ProxyAsync<T>` は `Results.StatusCode` で status だけ返すため**本文が落ちる**。
落ちると `SC-09` の「削除前に使用件数を示す」が満たせない。

### 判断 4: **「逆向きの表示語」の列は作らない**

hi-fi モックは列を描くが、`ADR-0033` が逆向きの語を定めているのは「**バックリンク欄での表示**」で
あって辞書の管理項目ではない。ドメイン（`EdgeType`）にも契約（`EdgeTypeDto`）にもその欄が無い。
**そのバックリンク欄自体、`SC-04` 側で実現方式が未確定である**（#1240 で確認済み）。
**消費者の無い管理項目を先に作ると、計画が決めたときに実装ではなく画面が先に決めたことになる。**

### 判断 5: 「同じ規則をタグ辞書にも適用」は**削除拒否と改名追随**であって、`related` フォールバックではない

INDEX 決定 18 の「同じ規則」は**直前に列挙された 2 つ**（削除拒否・改名追随）を指す。
タグ辞書は既に両方を満たしている（`IADR-0153` 決定 1・6）。

🔴 **`related` フォールバックをタグへ持ち込んではならない。** タグは
「**辞書に無い名前は 400 で拒否する**」と計画が確定しており（`IADR-0153` 決定 5・2026-08-09 の裁定。
画面からの手入力を自動登録しない）、辺の型の「丸めて警告」と**意図的に逆**である。
同じ語の「同じ規則」を広く取ると、この確定を壊す。

### 判断 6: `related` フォールバックは再実装しない（既に在る）

`EdgeTypeResolver` ＋ `LinkEdgeSynchronizer` が丸め・警告ログ・`EdgeTypeFallbackMetrics` を
実装済みである（`IADR-0281` / #912）。**画面は帰結を告げるに留める** ——
管理者は「型を消すと以後の抽出が `related` に寄る」ことを知って判断する必要がある。

## 4. 実装

| 層 | 変更 |
| --- | --- |
| 契約 | `docs/api/openapi.yaml` に `/bff/edge-types`（GET/POST）・`/bff/edge-types/{id}`（PUT/DELETE）と 4 スキーマ。`pnpm run codegen` で生成物をコミット |
| BFF | `EdgeTypeDictionaryBffEndpoints.cs`（新規）。合成登録簿へ 1 行（20 → 21） |
| BFF 試験 | `BffEdgeTypeDictionaryTests.cs`（新規・13 本）。`BffTestFactory` に辞書用 knob と method 別の枝 |
| 画面 | `EdgeTypeDictionaryPanel.tsx` / `useEdgeTypeDictionary.ts`（新規）。`AdminAbacSettingsPage.tsx` へタブ 1 つ |
| 単体試験 | 不在テスト 1 本を存在の固定へ反転。辺の型の振る舞い 10 本を追加 |
| E2E | `sc09-admin-abac.smoke.spec.ts` に 1 本（区画の実在 ＋ 取り違えの検査 ＋ 逆向き列の不在） |
| 文書 | `docs/screens/SC-09_admin-abac-settings.md` を実態へ |

## 5. 受け入れ基準

1. `/bff/edge-types` に読み取り（admin ＋ operator）と書き込み（admin のみ）がある。
2. 後段の 409 と `usageCount` が**中立化されず**画面へ届く。
3. `SC-09` に「辺の型」区画があり、タグ辞書と同じ操作体系である。
4. 不在テストが**存在の固定へ置き換わっている**。
5. E2E が区画の実在と**口の取り違えの不在**を固定する。
6. 画面仕様書の「4 区画のうち 3 区画」が実態に合っている。
7. `pnpm run codegen` の再生成差分が無い。
8. `dotnet test`（Platform.Bff.Tests）・`lint` / `typecheck` / `test` / `format:check` / `i18n` が緑。

## 6. 変異試験

| # | 変異 | 落ちるべきテスト |
| --- | --- | --- |
| M1 | BFF の後段を `/graph/edge-types` → `/graph/edge-types/catalog` にする（＝取り違え） | `It_calls_the_admin_listing_not_the_catalog_path` |
| M2 | 削除の中継を `RelayAsync` → `Results.StatusCode` にする（＝本文を落とす） | `Delete_AsAdmin_WhenInUse_Returns409WithUsageCount` |
| M3 | 書き込み群の `AdminOnly` を読み取り群と同じ（admin ＋ operator）にする | `Write_AsOperator_IsForbidden` |
| M4 | 画面のフックを `useBffGraphEdgeTypes`（カタログ）へ差し替える | `reads the admin dictionary, never the public drawing catalog` |
