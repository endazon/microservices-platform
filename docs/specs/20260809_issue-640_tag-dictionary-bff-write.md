---
title: 作業仕様書 — タグ辞書の追加・改名・削除を BFF へ通し、SC-09 の画面から操作できるようにする（#640）
type: work-spec
status: draft
related_ids:
  - FR-06
  - FR-09
  - SC-05
  - SC-09
  - UC-03
  - UC-05
  - IADR-0040
  - IADR-0044
  - IADR-0129
  - IADR-0152
  - IADR-0153
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../adr/IADR-0152_tag-dictionary-contract.md"
  - "../adr/IADR-0153_tag-identity-storage-and-projection.md"
  - "../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md"
  - "./20260809_issue-634_tag-dictionary-values-and-counts.md"
  - "./20260809_issue-635_tag-identity-migration.md"
---

# 作業仕様書 — タグ辞書の追加・改名・削除を BFF へ通し、SC-09 の画面から操作できるようにする（#640）

## 起点となる計画書（トレーサビリティ）

| 種別 | ID | 何を求めているか |
| --- | --- | --- |
| 画面 | **SC-09** | §主要素の 4 区画のひとつが「**タグ辞書**」である。§タグ辞書の削除・改名（確定 2026-08-02）は **参照が 1 件でもあるタグは削除拒否**・**改名は既存文書へ追随**・**削除前に使用件数を示す** |
| 画面 | **SC-05** | タグは「既定タグ辞書に整合」（辞書の中身を管理できることが前提） |
| 要求 | **FR-09** | ABAC 属性・ポリシーを管理する（管理機能） |
| 要求 | **FR-06** | 文書管理（タグは文書のメタデータ） |

**#542 の残件である。** 前半 = #634（値集合・使用件数）、後半 = #635（識別子参照・改名・削除）。
どちらも**口を DocumentService 側にだけ置いた**ので、**読めるが書けない**状態が 2 PR にわたって残った。

## 射程

- **`/bff/tags` を新設**し、追加（`POST`）・改名（`PUT`）・削除（`DELETE`）を通す
- **SC-09 の画面へ「タグ辞書」タブ**を足し、一覧・追加・改名・削除を操作できるようにする
- **削除拒否の 409 を使用件数つきで画面へ届ける**
- `docs/api/openapi.yaml` へ追加し **orval を再生成**する

### 射程外（理由つき）

| 項目 | 理由 |
| --- | --- |
| DocumentService 側 `/tags` の認可・振る舞い | **既に完成している**（後述の母集合 軸 2）。本作業は BFF と画面だけを足す |
| 辺の型辞書のタブ | **別要求**（FR-17 / ADR-0033）。[[IADR-0129]] 決定 1 の理由 A であり、本 issue の理由 B とは別 |
| SC-05 のタグ入力補完（`(b) タグ辞書からの補完`） | **別画面の別要素**。SC-05 は `/bff/attribute-values` の `dictionary` を既に読めており、本作業で変わらない |

## 母集合（[[IADR-0141]] 決定 1・走査基準 `a6d93fa`）

**issue 本文の表は転記していない。** 以下はすべて自分で引いた実測である。

### 軸 1: `/tags` を配る口（全数）

`src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/Endpoints/TagDictionaryEndpoints.cs`

| 行 | 口 | 認可 |
| --- | --- | --- |
| 28 | `MapGroup("/tags")` = `read` | `RequireRole(Admin, Operator)` |
| 34 | `MapGroup("/tags")` = `write` | `RequireAuthorization(AdminOnly)` |
| 38 | `read.MapGet("/")` | admin ＋ operator |
| 44 | `write.MapPost("/")` | **admin のみ** |
| 84 | `write.MapPut("/{id:guid}")` | **admin のみ** |
| 149 | `write.MapDelete("/{id:guid}")` | **admin のみ** |

**サービス側は既に計画どおりである。** 本作業で狭めるものも緩めるものも無い。

### 軸 2: BFF に `/tags` の書き込み口はあるか（**あり得る形を列挙してから引いた**。規則 2）

走査語: `"/bff/tags` ／ `MapGroup("/tags` ／ `bff/tag`

→ **0 件**。読み取りだけが `POST /bff/attribute-values` の `dictionary` フィールドに**相乗り**しており
（`SearchBffEndpoints.cs:73` の群 ＋ `:152` の後段呼び出し）、**`/bff/tags` という群は存在しない**。

### 軸 3: ★ **誰がこの口を呼ぶか**（機械クライアント。**#629 で引き漏らした軸**）

```console
$ grep -rn --exclude-dir={bin,obj,coverage,node_modules} -E '"/tags|/tags/' src/ \
    --include=*.cs --include=*.ts --include=*.tsx | grep -viE '/tests?/|\.test\.|\.spec\.|TestFactory'
```

| 呼び出し元 | 種別 |
| --- | --- |
| `SearchBffEndpoints.cs:152` | **BFF 自身の読み取り**（`GET /tags`） |
| `TagDictionaryEndpoints.cs:73` | `Results.Created` の Location（呼び出しではない） |
| `bff.schemas.ts:41` | orval 生成物の JSDoc（呼び出しではない） |

**`src/ai-stock-trading` からの `/tags` 呼び出しは 0 件である**（submodule を populate したうえで走査した）。

→ **#629 と違い、機械クライアントの巻き添えは無い。** BFF を admin 限定にしても止まる経路が無い。

### 軸 4: 画面（SC-09）の現状

- タブは **`属性体系` / `ポリシー定義` の 2 つだけ**（`AdminAbacSettingsPage.tsx`）
- **タグ辞書タブは意図的に置いていない** —— [[IADR-0129]] 決定 1 が理由 **B: 契約の不在**と記録し、
  「**空のタブを置かない**」（#502「動かない UI を置かない」）と明記している
- `AdminAbacSettingsPage.test.tsx:490` が **タブの不在を回帰テストで固定**しており、
  そのコメントは **`#640` を解除条件として名指ししている**

### 軸 5: ★ この変更で**新たに誤りになる記述**（規則 8）

**#640 は「契約の不在」という前提そのものを消す。** 誤りの側の語
（`契約の不在` / `書き込み口が無い` / `#640 へ起票` / `#640 待ち`）で全走査して引いた。

| # | 箇所 | 現在の記述 | 扱い |
| --- | --- | --- | --- |
| 1 | `docs/api/openapi.yaml:1726` | 「BFF の書き込み口は #542 の射程外で、#640 へ起票した」 | **直す ＋ `pnpm run codegen`** |
| 2 | `src/platform/frontend/src/foundation/api/generated/bff.schemas.ts:41` | 同上（#1 の生成物） | **codegen で追随** |
| 3 | `docs/functional/FR-09_...md:123-124` | 「BFF の書き込み口が要る…#640 へ起票した」 | 直す |
| 4 | `docs/adr/IADR-0129:78` | 「タグ辞書 \| **しない（B: 契約の不在）**」 | **日付つき追記で解除** |
| 5 | `docs/adr/IADR-0153:189-191` | 残件「改名・削除に BFF の書き込み口が無い」 | 解消を追記 |
| 6 | `docs/screens/SC-09_...md:38,87,109,215,219,268` | 「契約の不在」「未決事項 1」 | 直す（対応表・理由・未決事項） |
| 7 | `docs/tests/SC-09_...md:63,89,182` | 「実装しない（BFF の書き込み口が無い）」 | 直す ＋ 新テストを足す |
| 8 | `AdminAbacSettingsPage.tsx:20-21` | 「タグ辞書: 契約の不在」 | 直す |
| 9 | `AdminAbacSettingsPage.test.tsx:10,422,489-495` | **タブの不在**を固定する回帰テスト | **反転する**（削除ではなく） |
| 10 | `docs/api/BFF_bff-surface.md` | `/bff/tags` の行が無い | **足す**（3 行） |
| 11 | `feedback/20260805_sc09-11-admin-ops-contract-gaps.md` | 環流記録の提案 1（タグ辞書の契約） | 解消を追記 |

### 除外したものと理由（[[IADR-0141]] 決定 6）

| 除外 | 理由 |
| --- | --- |
| `docs/specs/2026*`（#135 / #504 / #535 / #540 / #634 / #635 / #629 ほか） | **確定済みの作業仕様書は書いた時点の記録**である。書き換えは記録の改竄にあたる |
| `src/packages/ui/`（`Tabs.tsx` / `layout.test.tsx` / `Primitives.stories.tsx` / `README.md`） | そこの「タグ辞書」は **UI プリミティブのタブの見本文字列**であり、契約についての主張ではない |
| `docs/tests/SC-06,07,08,10,11` ／ `docs/screens/SC-06,07,08,10,11` の「契約の不在」 | **別資源・別画面の別要素**（デッドレター・人手補正・SLO・LLM コスト等）。本作業で解消しない |
| `docs/adr/IADR-0139` | **束ね判定の記録**（過去の判断の記録であり、現在の仕様の記述ではない） |
| `docs/screens/SC-01_...:127` ／ `feedback/20260804_sc01-03-...` | **一般利用者向け**のタグ候補（`/bff/attribute-values`）の話。管理面の辞書とは別（ADR-0043 決定 1） |
| `docs/screens/SC-05_...:120,238` | SC-05 のタグ**入力補完**。射程外（上記 §射程外） |
| [[IADR-0152]] | 決定は「辞書は DocumentService が持つ・**読み取り**は 1 系統へ相乗り」であり、**書き込み口を置かない理由ではない**。本作業で古くならない |

## 判断

### 判断 1: 口は **`/bff/tags` を `Knowledge.Bff.Endpoints` に新設**する（`/bff/admin/authz/tags` にしない）

| 群 | 実体 | ユニット | 後段 |
| --- | --- | --- | --- |
| `/bff/admin/authz` | `Platform.Bff/Foundation/Endpoints/AuthzBffEndpoints.cs` | **platform** | AuthorizationService |
| `/bff/attribute-values`・`/bff/documents` | `Knowledge.Bff.Endpoints/*.cs` | **knowledge** | RetrievalService / DocumentService |

タグ辞書の後段は **DocumentService（knowledge ユニット）**である。`/bff/admin/authz/tags` へ寄せると
**platform の群が可変ユニットの資源を配る**ことになり、`CLAUDE.md`「**platform → 可変ユニットは禁止**」に触れる。

**画面が同じ SC-09 であることは、口の所属を決めない** —— [[IADR-0139]] 決定の条件 A が
「判定の単位は**ドメインではなく資源**」と定めているのと同じ切り分けである。

### 判断 2: 409（`usageCount` つき）は **本文ごと透過**する

`DocumentBffEndpoints.RelayAsync`（`:159`）が `Results.Content(content, contentType, statusCode)` で
**後段の本文をそのまま返す**。`{ error, message, usageCount }` は詰め替え不要で画面へ届く（[[IADR-0040]] と同型）。

**BFF で数え直さない** —— 数え方を 2 つ持つと一覧と削除拒否で件数が割れる（[[IADR-0153]] 決定 6 の趣旨）。

### 判断 3: 認可は **BFF・サービスの両層で `AdminOnly`**（[[IADR-0044]] 多層防御）

**読み取りは admin ＋ operator のまま**（[[IADR-0152]] 決定 5・裁定 Q18）。
**書き込みだけを admin 限定**にする —— SC-09 のアクセス制御が「システム管理者ロール限定」である。

**軸 3 のとおり機械クライアントは居ない**ので、#629 のような据え置きは要らない。

### 判断 4: 画面は**タブを足す**（[[IADR-0129]] 決定 1 の理由 B を解除する）

理由 B（契約の不在）は本作業で消える。**理由 A（辺の型辞書 = FR-17 の着手保留）は残る。**

| | 現在 | #640 後 |
| --- | --- | --- |
| タブ | `属性体系` / `ポリシー定義`（**2 つ**） | `属性体系` / **`タグ辞書`** / `ポリシー定義`（**3 つ**） |
| 辺の型辞書 | 無い（理由 A） | **無いまま**（理由 A は解除されていない） |

計画 §SC-09 §主要素は 4 区画を挙げるので、**4 区画中 3 区画**が揃うことになる。

## 実装方針

1. `Knowledge.Bff.Endpoints/TagDictionaryBffEndpoints.cs` を新設し `/bff/tags` の 4 口
   （`GET` 一覧・`POST` 追加・`PUT` 改名・`DELETE` 削除）を置く。読み取りは admin ＋ operator、書き込みは `AdminOnly`。
2. `Platform.Bff` の合成点へ登録する。
3. `docs/api/openapi.yaml` へ 4 口を追加し、`pnpm run codegen` で orval を再生成する。
4. SC-09 へ `TagDictionaryPanel` を足し、タブへ挿す。i18n は **ja / en の両方**を書く。
5. `BffTestFactory` の後段スタブへ書き込み口の応答（201 / 200 / 204 / 409）を足す。

## テスト（受け入れ基準の写像）

| 受け入れ基準 | テスト |
| --- | --- |
| 画面から追加・改名・削除できる（BFF 経由） | `Platform.Bff.Tests` の `/bff/tags` 3 口 ＋ 画面テスト |
| 改名すると既存文書の表示が追随する | `RenameTagResponse.RepublishedDocuments` が画面へ届くこと |
| 参照 1 件以上の削除が**使用件数つきで拒否**され画面が件数を表示する | BFF の 409 透過テスト ＋ 画面のエラー表示テスト |
| 一般利用者・運用者は書き込めない（**両層**） | BFF: `Write_AsOperator_IsForbidden` ／ サービス: 既存 `TagDictionaryTests` |

**変異試験を行う** —— `AdminOnly` を積み忘れても既存テストは緑のままになり得るため
（#629 で同じ穴を実測した）。狙った 1 件だけが落ちることを確認する。

## 検証記録（実測）

（実装後に記入する）
