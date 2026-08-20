---
title: 作業仕様書 — タグ辞書の追加・改名・削除を BFF へ通し、SC-09 の画面から操作できるようにする（#640）
type: spec
status: done
related_ids:
  - FR-06
  - FR-09
  - SC-05
  - SC-09
  - UC-03
  - UC-05
  - IADR-0040
  - IADR-0044
  - IADR-0127
  - IADR-0129
  - IADR-0152
  - IADR-0153
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
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
| 辺の型辞書のタブ | **別要求**（FR-17 / ADR-0033）。[IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md) 決定 1 の理由 A であり、本 issue の理由 B とは別 |
| SC-05 のタグ入力補完（`(b) タグ辞書からの補完`） | **別画面の別要素**。SC-05 は `/bff/attribute-values` の `dictionary` を既に読めており、本作業で変わらない |

## 母集合（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1・走査基準 `a6d93fa`）

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
- **タグ辞書タブは意図的に置いていない** —— [IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md) 決定 1 が理由 **B: 契約の不在**と記録し、
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

### 除外したものと理由（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 6）

| 除外 | 理由 |
| --- | --- |
| `docs/specs/2026*`（#135 / #504 / #535 / #540 / #634 / #635 / #629 ほか） | **確定済みの作業仕様書は書いた時点の記録**である。書き換えは記録の改竄にあたる |
| `src/packages/ui/`（`Tabs.tsx` / `layout.test.tsx` / `Primitives.stories.tsx` / `README.md`） | そこの「タグ辞書」は **UI プリミティブのタブの見本文字列**であり、契約についての主張ではない |
| `docs/tests/SC-06,07,08,10,11` ／ `docs/screens/SC-06,07,08,10,11` の「契約の不在」 | **別資源・別画面の別要素**（デッドレター・人手補正・SLO・LLM コスト等）。本作業で解消しない |
| `docs/adr/IADR-0139` | **束ね判定の記録**（過去の判断の記録であり、現在の仕様の記述ではない） |
| `docs/screens/SC-01_...:127` ／ `feedback/20260804_sc01-03-...` | **一般利用者向け**のタグ候補（`/bff/attribute-values`）の話。管理面の辞書とは別（ADR-0043 決定 1） |
| `docs/screens/SC-05_...:120,238` | SC-05 のタグ**入力補完**。射程外（上記 §射程外） |
| [IADR-0152](../adr/IADR-0152_tag-dictionary-contract.md) | 決定は「辞書は DocumentService が持つ・**読み取り**は 1 系統へ相乗り」であり、**書き込み口を置かない理由ではない**。本作業で古くならない |

## 判断

### 判断 1: 口は **`/bff/tags` を `Knowledge.Bff.Endpoints` に新設**する（`/bff/admin/authz/tags` にしない）

| 群 | 実体 | ユニット | 後段 |
| --- | --- | --- | --- |
| `/bff/admin/authz` | `Platform.Bff/Foundation/Endpoints/AuthzBffEndpoints.cs` | **platform** | AuthorizationService |
| `/bff/attribute-values`・`/bff/documents` | `Knowledge.Bff.Endpoints/*.cs` | **knowledge** | RetrievalService / DocumentService |

タグ辞書の後段は **DocumentService（knowledge ユニット）**である。`/bff/admin/authz/tags` へ寄せると
**platform の群が可変ユニットの資源を配る**ことになり、`CLAUDE.md`「**platform → 可変ユニットは禁止**」に触れる。

**画面が同じ SC-09 であることは、口の所属を決めない** —— [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) 決定の条件 A が
「判定の単位は**ドメインではなく資源**」と定めているのと同じ切り分けである。

### 判断 2: 409（`usageCount` つき）は **本文ごと透過**する

`DocumentBffEndpoints.RelayAsync`（`:159`）が `Results.Content(content, contentType, statusCode)` で
**後段の本文をそのまま返す**。`{ error, message, usageCount }` は詰め替え不要で画面へ届く（[IADR-0040](../adr/IADR-0040_admin-abac-bff-passthrough-and-admin-only.md) と同型）。

**BFF で数え直さない** —— 数え方を 2 つ持つと一覧と削除拒否で件数が割れる（[IADR-0153](../adr/IADR-0153_tag-identity-storage-and-projection.md) 決定 6 の趣旨）。

### 判断 3: 認可は **BFF・サービスの両層で `AdminOnly`**（[IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md) 多層防御）

**読み取りは admin ＋ operator のまま**（[IADR-0152](../adr/IADR-0152_tag-dictionary-contract.md) 決定 5・裁定 Q18）。
**書き込みだけを admin 限定**にする —— SC-09 のアクセス制御が「システム管理者ロール限定」である。

**軸 3 のとおり機械クライアントは居ない**ので、#629 のような据え置きは要らない。

### 判断 4: 画面は**タブを足す**（[IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md) 決定 1 の理由 B を解除する）

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

## 実装中に決めたこと（仕様書からの差分）

### 1. ★ `usageCount` は**画面まで届いていなかった**（契約は正しいのに）

**判断 2（`RelayAsync` で本文ごと透過）だけでは受け入れ基準を満たせなかった。**
BFF は正しく透過していたが、**クライアント側の解析層が落としていた**（実測）。

```
後段の 409 本文        : { error, message, usageCount }
parseProblemDetails が読むキー: errors / detail / title   ← どれも持たない
→ ApiError.details = []  →  画面は「競合が発生しました。」しか出せない
```

**軸の引き漏らしである** —— 母集合に「**誰がこの応答を読むか**」を入れていなかった。
軸 3 で「誰がこの口を**呼ぶ**か」は引いたのに、**戻りを解釈する層**を見ていない。

是正:

1. `parseProblemDetails` が **`message` も読む**（一般の改善。`details` の意味は変えていない）
2. `ApiError` が**解析済みの本文を持つ**（`body`）。`details` は文字列しか持てず、
   **翻訳済みの文へ数値を差し込めない** —— サーバの日本語をそのまま出すと en ロケールで混ざる

`tagInUseCount()` が `error === 'tag_in_use'` のときだけ数値を返し、
**件数を持たない 409（重複名など）では既定の文言へ落ちる**ことも対でテストに固定した。

### 2. 射程外だが同時に直した 2 件（`openapi.yaml` の群コメント）

**`openapi.yaml` は自ら「正」を名乗る文書であり、誤ったまま残せない**ため直した。

| 箇所 | 誤り | 由来 |
| --- | --- | --- |
| `/bff/documents` の群コメント | 「書き込みは `platform-admin` / `platform-operator` に限定し」 | **#629 の追随漏れ**（実測: BFF の書き込み 5 口すべてに `AdminOnly`） |
| `/bff/datasources` の群コメント | 「閲覧・**操作**を `platform-admin` / `platform-operator` に限定する」 | #628 が 4 口へ `AdminOnly` を積んだことが書かれていない |

### 3. ★ #645 の走査が 1 件落としていた —— **規則 4 の違反**（同型 2 回目）

上の 1 件目は **#645 が是正しそこねたもの**である。走査式を再現して原因を特定した。

| 段 | 式 | 結果 |
| --- | --- | --- |
| 1 段目 | `admin ?/ ?operator｜platform-admin.{0,4}(または｜/｜、).{0,4}platform-operator` ほか | **137 件。当該行は当たっている** |
| 2 段目 | `\| grep -iE 'document\|文書\|/bff/documents'` | **34 件へ絞られ、当該行が落ちた** |

**変種の列挙（規則 2）は正しかった。** 落としたのは**2 段目の行フィルタ**で、
当該行は `/bff/documents:` の**直上のブロックコメント**にあり `document` も `文書` も含まない。

**これは規則 4「行フィルタで絞らない。パスから引く」の違反である。**
同規則の先例 #593 は「走査の末尾に `grep -i "feedback\|FR-08"` を継いだため、
該当行が両語を含まない `docs/api/openapi.yaml` が落ちた」——**同じ機構・同じファイル**である。

**規則を足さない。** #645 は規則 7・8 を新設したが、**今回落ちたのは既存の規則 4 であり、
規則の不足ではなく規則の不遵守**だった。`CLAUDE.md`「同型の事故が 2 回起きたら」の
条件を満たすのは**検査器**の側である（`openapi.yaml` の宣言ロールと
実装の `RequireAuthorization` を突き合わせる）。**本 issue の射程外なので別 issue とする**
（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4）。

## 検証記録（実測。base = `a6d93fa`）

| 検査 | 結果 |
| --- | --- |
| `dotnet build`（platform / knowledge） | Build succeeded・0 Error |
| `dotnet test Platform.Bff.Tests` | **184 → 196 Passed** / 1 Skipped / 0 Failed |
| `dotnet format --verify-no-changes`（両ユニット） | exit 0 |
| `pnpm typecheck` | Done |
| `pnpm lint` | **0 errors**（warning 9 件はすべて既存） |
| `pnpm format:check` | All matched files use Prettier code style |
| `pnpm build` | built in 5.96s |
| `pnpm i18n` | **ja / en とも未翻訳 0**（en に 12 件を追加した） |
| `pnpm test:coverage` | **612 → 621 Passed** / 63 files |
| `check-i18n-catalogs` / `check-doc-links` / `check-contract-schema` / `check-test-traceability` | すべて OK |
| `check-static-egress` | OK（25 ファイル・外部オリジン 0） |
| `check-chunk-budget` | 583.16 → **584.42 kB** へ更新 |

**カバレッジ床は据え置いた**（MSP 所有分の実測: lines 96.17 → 床 91〔現行と同じ〕・
functions 93.52 → 床 88〔現行と同じ〕・branches 91.95 → 床 86〔**現行 87 より低い**〕）。
**ラチェットなので下げない。**

新規ファイルの被覆: `TagDictionaryPanel.tsx` **L100 / B96.8 / F100**、
`useTagDictionary.ts` **L100 / B70 / F100**。

### ★ 変異試験

**`AdminOnly` を積み忘れても既存テストは緑のまま通る** —— 読み取り群が元から admin ＋ operator なので
`viewer` は積まなくても 403 になる。**だから運用者で引くテストを足し、効くことを変異で確かめた。**

| 変異 | 結果 |
| --- | --- |
| `/bff/tags` の `write` 群の `AdminOnly` を admin ＋ operator へ緩める | **Failed 3** —— `Write_AsOperator_IsForbidden` の POST / PUT / DELETE **だけ** |
| 戻す | **Failed 0**（196 Passed） |

### 4. ★ 走査に `.cs` のヘッダコメントを含めていなかった（レビュー 1 巡目の 🟢）

**§3 で「規則 4 の違反」を指摘しながら、自分の走査も同じ型で穴が空いていた。**

`openapi.yaml` の群コメントは直したのに、**同じ主張をしている実装ファイルのヘッダコメントを引いていない**
——走査対象を `docs/` と `*.yaml` に限っており、**`.cs` を含めていなかった**（規則 3
「拡張子で絞らない。パスの除外だけで取る」の違反である）。

是正として `.cs` へも同じ変種で走査し、**4 件を実測した**:

| # | 箇所 | 判定 |
| --- | --- | --- |
| 1 | `DataSourceBffEndpoints.cs:13`「閲覧・**操作**は管理者・運用者」 | **古い**（書き込み 4 口は `AdminOnly`）→ 直した |
| 2 | `BffDataSourceEndpointTests.cs:9`「管理者・運用者ロールに限定されること」 | **不完全**（書き込みの admin 限定に触れていない）→ 直した |
| 3 | `ConversionBffEndpoints.cs:12` | **正しい**——「再変換だけは管理者ロールのみ」「照会は据え置く」の但し書きが揃っている |
| 4 | `DataSourceEndpoints.cs:41`（service 側） | **正しい**——`AdminOnly` を積む理由が書かれている |

**1 件だけ直さない。** レビューは #1 だけを挙げたが、**同型を全数引いてから直す**のが規則 7 である
（#642 で「3 箇所直して 2 箇所取り残す」を実測している）。**#3 / #4 が正しいことも実測で確かめた**
——「指摘されなかったから正しい」ではなく、**引いて確かめた**結果である。

**この件も検査器（#647）の射程に入る** —— 突き合わせ対象を `openapi.yaml` だけでなく
**BFF 実装ファイルのヘッダコメント**にも広げれば、同じ穴は機械で塞がる。

### 5. 改名の波及件数を画面へ出した（レビュー 2 巡目の 🟡）

**指摘は「`republishedDocuments` が UI に出ていない」。妥当なので実装した。**

受け入れ基準の書き方（「**画面へ届くこと**」）としては満たしていたが、
**削除拒否では件数を見せて行動させているのに改名では見せない**のは非対称である。

**件数が要るのは射影の反映が非同期だからである** —— 出さないと管理者は
**「対象が 0 件だった」と「まだ届いていない」を区別できない**（どちらも
「一覧の名前は変わったのに検索結果が古いまま」に見える）。[IADR-0153](../adr/IADR-0153_tag-identity-storage-and-projection.md) 決定 3 が
`RepublishedDocuments` を契約へ載せた理由そのものである。

成功時の `Alert`（`tone="success"` / `role="status"`）は
`AttributeDictionaryPanel` が既に採っている形に揃えた。

### 6. ★ [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 7 を踏襲していなかった（レビュー 3 巡目の 🟡）

**確定済み ADR からの逸脱である。** 指摘を受けて**先に再現テストを書き、落ちることを実測**してから直した。

TanStack Query の `isError` は**そのミューテーション自身が再度 `mutate()` されるまで消えない**。
`failed = [create, rename, remove].find(m => m.isError)` だけだと:

1. 使用中のタグを消そうとして 409 → `remove.isError = true`
2. **別のタグを改名して成功** → `rename.isSuccess = true` だが `remove.isError` は残る
3. `republished = rename.isSuccess && !failed` の `!failed` が false になり、
   **改名成功の通知が出ないうえ、古い削除拒否の警告が残り続ける**

**管理者は「今の改名が失敗した」と誤解する。**

`beginOperation()`（全ミューテーションを `reset()`）を追加した。
**`AttributeDictionaryPanel` が同じ決定に従って既に持っていた形**である。

#### なぜ引き漏らしたか

**母集合に「同じ画面の隣の区画はどう解決しているか」という軸が無かった。**

新しい区画を足すとき、**同じディレクトリの既存パネルが従っている ADR を引いていない**。
`AttributeDictionaryPanel.tsx:97-107` にコメント付きで書かれており、
**読めば分かる場所にあった**（[IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 7 への参照も明記されている）。

**軸として立てるべきだったのは「この画面に既にある同種の部品」である** ——
`docs/tests/SC-09_admin-abac-settings.md` の観点 12「**操作を跨いだ状態**」も同じことを求めていた。

### 7. ★ [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 7 の**実装指定**まで踏襲していなかった（レビュー 4 巡目の 🟡）

**§6 で `beginOperation()` を足したが、決定 7 はその先の実装方法まで確定していた。**

> **列挙は `useDocumentActions()` / `useDataSourceActions()` の戻り値を `Object.values` で辿る。**
> 手書きの配列を残すと、4 本目のミューテーションを足したときに配列へ足し忘れて同じ穴が開く
> ——**指摘は 2 画面に対するものだったが、原因は「配列を手で並べたこと」であって画面の数ではない。**

**`const mutations = [create, rename, remove]` は、この文が名指しで禁じている形である。**

#### レビューの指摘には 1 件漏れがあった

レビューは `AttributeDictionaryPanel.tsx:100` を挙げたが、**走査すると SC-09 の 3 パネルすべてが手書き配列だった**。

| ファイル | 変更前 | 本数 |
| --- | --- | --- |
| `TagDictionaryPanel.tsx`（本 PR で新設） | `[create, rename, remove]` | 3 |
| `AttributeDictionaryPanel.tsx` | `[create, remove]` | 2 |
| **`PolicyEditorPanel.tsx`**（**レビュー未指摘**） | `[create, setActive, remove, validate]` | **4** |
| `DocumentManagementPage.tsx`（SC-05） | `Object.values(actions)` | — |
| `DataSourceManagementPage.tsx`（SC-06） | `Object.values(actions)` | — |

**`PolicyEditorPanel` は既に 4 本ある** —— 決定 7 が「4 本目を足したときに足し忘れる」と警告した状況に
**最も近いのはこのファイル**だった。**指摘された 1 件だけを直していたら、最も危ういものを残していた。**

**SC-09 の 3 パネルすべてを `Object.values(actions)` へ揃えた**（5 画面が同じ形になった）。

#### なぜ引き漏らしたか

**§6 で ADR を「引いた」つもりが、隣のファイルの実装を見ただけだった。**

`AttributeDictionaryPanel` のコメントは `IADR-0127 決定 7` を参照していたが、
**その ADR の本文を開いていない**。開けば `Object.values` の指定は決定の直後に書かれている。

**「隣がやっている形」は ADR の写しであって ADR ではない。** 隣が逸脱していれば逸脱ごと写る——
実際そうなった（`AttributeDictionaryPanel` の手書き配列を追認してコピーした）。
**参照先の ADR を必ず開く**、が正しい手順である。

## 申し送り

- **検査器の起票**（上記 §3）。`openapi.yaml` が宣言するロールと実装の
  `RequireAuthorization` を突き合わせる。**規則 4 の同型 2 回目**が根拠である。
- **辺の型辞書の区画**は本作業でも実装していない（[IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md) 決定 1 の理由 A = FR-17 の
  着手保留。#586 で保留理由は失効しているが、**引き受けは #504 / #452 の側**である）。
