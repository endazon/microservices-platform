---
title: 作業仕様書 — 認可スコープの選言 実装 5 段のうち本セッション担当分（段 5 前提の IADR 改定・段 5・段 3 WikiService・段 4 DocumentShare）
type: spec
status: draft
related_ids: [FR-05, FR-19, FR-20, FR-21, UC-11, ADR-0004, ADR-0034, ADR-0036, ADR-0046, ADR-0054, IADR-0253]
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md
---

# 作業仕様書: 認可スコープの選言（#989）— 本セッション担当分

> 方針の正本は `IADR-0253`（Proposed）と、その決定 5 への本作業の追記
> ［2026-08-23 追記 / #989］である。5 段の定義は同 IADR 決定 6。
> 前提の作業仕様書: [`20260822_adr-0046-d06-part3_authz-scope-disjunction.md`](20260822_adr-0046-d06-part3_authz-scope-disjunction.md)

## 走査基準（実測の再現条件）

| 対象 | ref | 備考 |
| --- | --- | --- |
| 実装 `microservices-platform` | 作業ツリー（ブランチ `claude/implementation-repo-all-issues-hilvbs`、HEAD `395d5f73`、`origin/develop` = `2c5f269e` を含む） | 🔴 **作業ツリーには他セッションの未コミット変更が同居している**（#915 GraphService / #448 RetrievalService / #600 Notification）。本書の実測はこの状態に対するもの |
| 計画 `project-planning` | `origin/main` = `b6c3cc07`（fetch 済・作業ツリー一致・クリーンを確認） | `07_abac-attribute-model` §ポリシー評価モデル に `write` 追記済み（planning#466） |

段 1（#999 / `01d4564d`）・段 2（#1001 / `890c3e7e`）は develop 着地済みであることを
作業ツリーの実装（`AccessScopeDto.cs` の `Branches` / `AbacEvaluator` の分岐組み立てと
`BindPlaceholders`）で確認した。

## 1. 本作業でやる段・やらない段（担当の宣言）

**他セッションとのファイル領域の重複を避けるため、統括が担当範囲を限定している。**
触ってよいのは `WikiService` / `DocumentService` / `Platform.Shared.*` の認可契約まわり /
`AuthorizationService` のみ。

| 段 | 内容 | 本作業 | 理由 |
| --- | --- | --- | --- |
| 1 | 契約へ `Branches` 追加 | 対象外 | ✅ 着地済み（#999） |
| 2 | 評価器の分岐組み立て | 対象外 | ✅ 着地済み（#1001） |
| 3 | 消費側の分岐対応（GraphService → WikiService → RetrievalService → AiAnalysisService） | **WikiService のみ実施** | GraphService は #915 が同時編集中（**tripwire `AbacUnenforcedAxisTests` は GraphService 配下のため本作業では書き換え不可**）。RetrievalService / AiAnalysisService は #448 が同時編集中 → **変更内容を報告書に記して統括へ返す** |
| 4 | `DocumentShare` と共有先ベースの分岐 | **DocumentShare（実体・永続化・EF マイグレーション・所有者限定の共有管理 API）を実施** | DocumentService は空いている。**分岐 ③ を消費側がどう評価するか（`shared_with` の越境参照）は未決であり実装しない**（§6） |
| 5 | `Action` の解決 | **IADR-0253 決定 5 の改定（write 値域）＋契約・端点・値域の実装を実施** | 全変更が担当範囲（契約 + AuthorizationService）に閉じる。#993 が本段の完了を待っている |

## 2. 母集合（是正・追随の対象。引いた結果と除外理由）

### 2-a. 契約 `AccessScopeRequest` へ `Action` を足す（段 5）— 3 表現形＋参照元

走査語: `AccessScopeRequest`（`src` / `docs` / `scripts` 全域。除外: `obj/` `bin/` `node_modules` `src/ai-stock-trading`〔別名前空間の submodule。本契約への参照が無いことは走査で確認済み〕）。

| # | ファイル | 扱い |
| --- | --- | --- |
| 1 | `src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/AccessScopeDto.cs` | **改定**（`Action` 追加・既定 `"read"`・末尾） |
| 2 | `docs/api/openapi.yaml`（`AccessScopeRequest` スキーマ） | **改定**（`action` を optional で追加） |
| 3 | `src/platform/frontend/src/foundation/api/generated/bff.schemas.ts`（orval 生成物） | 🔴 **フロントは本セッション触らない指示のため再生成しない。** 統括へ「マージ列で `pnpm run codegen` を 1 回走らせる」ことを報告（§6） |
| 4 | `scripts/contract-schema-baseline.json` | **`--update` で更新**（既定値付き末尾追加＝非破壊の判定を確認する） |
| 5 | `AuthzEndpoints.cs`（発行を受ける端点） | **改定**（ハードコード解消） |
| 6 | 呼び出し側 5 件（`BffScopeResolver` / `WikiAccessResolver` / `RagOrchestrator` / `GraphAccessResolver` / `Knowledge.IntegrationTests/AbacScopeTests`） | **無改修で従来挙動**（既定値 `read`）。GraphService 書き込み経路へ `manage` 等を渡す改修は #993 の射程（本作業ではない） |
| 7 | テスト（`AccessScopeContractTests` / `AbacEvaluatorTests`） | **追補** |

### 2-b. `PolicyAction` へ `write` を足す（段 5 前提）— 値域の写し先

走査語: `read / analyze / manage`（列挙の形）と `PolicyAction`。

| # | ファイル | 扱い |
| --- | --- | --- |
| 1 | `AuthorizationService/.../Domain/AbacEntities.cs`（`PolicyAction`） | **改定**（`Write` 追加） |
| 2 | `AbacValidation.cs` | 無改修（`PolicyAction.All` 経由で自動追随。エラーメッセージも `All` を連結） |
| 3 | `docs/functional/FR-09_abac-attribute-policy-management.md` 業務ルール ④ | **改定**（値域 4 値へ） |
| 4 | `docs/data/abac-policy.md`（3 箇所） | **改定** |
| 5 | `docs/screens/SC-09_admin-abac-settings.md` / `docs/tests/SC-09_admin-abac-settings.md` | **注記を追記**（契約は 4 値・画面の選択肢は 3 値のまま。画面側の追随は本作業の範囲外として報告） |
| 6 | `docs/api/openapi.yaml`（policy スキーマの `action` 説明 2 箇所） | **改定** |
| 7 | 🔴 `src/knowledge/frontend/src/features/sc09-admin-abac/types/abacVocabulary.ts`（`POLICY_ACTIONS` 3 値）＋同 `.test.ts` | **触らない**（フロント禁止）。**統括へ報告**: 値域拡張に SC-09 の選択肢・語彙テストが未追随になる（機能欠落であり権限は緩まない。write ポリシーを画面から作れないだけ） |

### 2-c. 段 3（WikiService）— 分岐評価の適用点

走査語: `AllowedFilters` / `AccessScopeResponse`（WikiService 配下）。

| # | ファイル | 扱い |
| --- | --- | --- |
| 1 | `WikiService/.../Services/AbacPageFilter.cs` | **改定**（`Branches` があれば分岐間 OR・分岐内 AND で評価） |
| 2 | `WikiService/.../Services/WikiAccessResolver.cs` | 無改修（`/authz/scope` の応答をそのまま運ぶ。閲覧経路の action は既定 `read` のまま） |
| 3 | `WikiEndpoints.cs` | 無改修（`AbacPageFilter` 経由でのみ判定） |
| 4 | テスト `AbacPageFilterTests` / `WikiEndpointsAbacTests` | **追補**（否定形＋陽性対照の対、#989 退行防止表 1〜4 の写像） |

WikiService には GraphService の `AbacUnenforcedAxisTests` に相当する tripwire は**無い**
（走査: `Unenforced` / `tripwire` で WikiService 配下 0 件）。書き換え対象の tripwire は本担当分には存在しない。

### 2-d. 段 4（DocumentShare）

| # | ファイル | 扱い |
| --- | --- | --- |
| 1 | `DocumentService/.../Domain/DocumentShare.cs`（新設） | 文書 ID × 被共有主体（user / group）。IADR-0253 決定 4 |
| 2 | `DocumentDbContext.cs` | `DbSet` ＋一意制約（DocumentId × SubjectType × SubjectId）＋連動削除 |
| 3 | `Migrations/`（`dotnet ef migrations add AddDocumentShares`） | **生成物を目視検算**（`--no-build` を使わない） |
| 4 | `DocumentEndpoints.cs`（共有の付与・取消・一覧） | **所有者限定**（`doc.owner ∈ { ${current_user} }`。`DocumentBodyIntake.CanWrite` を再利用し規則を 1 か所に保つ） |
| 5 | `docs/data/document-share.md`（新設。データ仕様書はエンティティ単位で必須） | 新規作成 |
| 6 | テスト | 追補（所有者は付与・取消できる／**非所有者・被共有者は変更できない（再共有不可）**の対） |

## 3. 段 5 の設計（IADR-0253 決定 5 の改定内容の要約）

正本は `IADR-0253` の追記ブロック［2026-08-23 追記 / #989］。要点:

1. **`PolicyAction` へ `Write = "write"` を足す**（値域 4 値）。計画は `07_abac-attribute-model`
   §ポリシー評価モデル 2026-08-22 追記で値域へ `write` を加えており（planning#466。ADR-0036 D-07 /
   D-01 の帰結）、実装が追随する。**deny-by-default により、write ポリシーが 1 件も無い間は
   `write` スコープは全件遮断のまま**（値域拡張そのものは何も許可しない）。
2. **`AccessScopeRequest` へ `string Action = "read"` を足す**（末尾・既定値付き＝非破壊）。
   契約プロジェクトから `PolicyAction`（AuthorizationService のドメイン型）は参照できないため
   **リテラル `"read"`** を既定にする（値の一致は評価器側のテストで固定する）。
3. **`/authz/scope` は `req.Action` を `PolicyAction.IsValid` で検証し、不正値は 400**。
   有効値は `AbacEvaluator.ResolveScope(req, policies, req.Action)` へ渡す。
   未知アクションを黙って空スコープにしない（呼び出し側の設定誤りを可視化する。
   400 は全消費側で deny へ縮退することを確認済み——5 呼び出し元とも非 2xx を Granted=false 扱いする）。

## 4. 受け入れ基準（完了時に実測で埋めた）

- [x] `IADR-0253` 決定 5 が日付つき追記ブロックで改定され、`updated:` が前進している
- [x] （段 5）`write` アクションのポリシーが作成でき、`/authz/scope` が action 別にスコープを返す。
      **write ポリシーが無い状態の write スコープは Granted=false**（否定形）と、
      **write ポリシーがある状態で分岐が返る**（陽性対照）が対で固定されている
- [x] （段 5）read のスコープに write ポリシーが混ざらない／その逆も無い（アクション分離の対）
- [x] （段 3 Wiki）#989 退行防止表 1〜4 が `AbacPageFilter` のテストに写像されている
      （両方の集合が見える／owner 無し文書が組織分岐で見える／他人の個人資料が見えない／自分のは見える）
- [x] （段 3 Wiki）変異試験: 分岐間 OR を AND に潰す・分岐を無視して従来評価に戻す、の 2 変異で
      該当テストが赤くなることを実測し、変異を戻して緑へ復帰している
- [x] （段 4）EF マイグレーション生成物を目視検算した記録がある
- [x] （段 4）**非所有者（被共有者を含む）が共有を付与・取消できない**（再共有不可・403）と
      **所有者ができる**（陽性対照）が対で固定されている
- [x] 契約検査（`check-contract-schema.js`）で今回の変更が**非破壊**と判定され、baseline 更新差分が
      `AccessScopeRequest.Action` の追加のみである
- [x] `dotnet format` / 対象テストプロジェクトの `dotnet test` が通る（統合テストの skip は「通った」と数えない）

## 5. 実測で判明した追加の事実（決定 2 の根拠の限界）

🔴 **「`AllowedFilters` は `Branches` の部分集合（deny 側へ倒れる）」（IADR-0253 決定 2）には反例がある。**

反例: ポリシー A `{ confidentiality: [internal], department: [hr] }`・
ポリシー B `{ confidentiality: [public], department: [sales] }` が同時マッチのとき、
`AllowedFilters` はキー単位 union で `confidentiality ∈ {internal, public} AND department ∈ {hr, sales}`
になり、**文書 `(confidentiality=internal, department=sales)` を許可する。この文書はどちらの
ポリシー単独でも許可されない**（分岐評価では不可視）。つまりキー単位 union は
**「どのポリシーも意図しない混成の組合せ」を許す向き（情報が漏れる向き）の乖離**を含む。

- **現実データへの影響**: 現在の実効的な認可軸は `confidentiality` 1 本（#516）であり、
  キーが 1 つのときは union ＝選言そのもので混成は起きない。**今日の実データでは漏れない。**
- **本作業の扱い**: 決定 2（据え置き）は変えない——未移行サービスの挙動を変えないことが目的で
  あり、この乖離は本作業以前から在る既存挙動である。**評価器のテストへ現状固定テスト
  （tripwire 型）を追加して記録し、`AllowedFilters` 廃止判断（IADR-0253 フォローアップ 2）の
  材料として IADR へ追記した。** 移行順の含意: **複数キーのポリシーを運用へ入れる前に段 3 の
  全サービス移行を終えること。**

## 6. 射程外（統括へ返す・残すもの）

1. **段 3 の GraphService**（#915 と競合。tripwire の書き換えを含む）/ **RetrievalService・AiAnalysisService**（#448 と競合）——必要な変更内容は最終報告に記載
2. **分岐 ③（shared）を消費側がどう評価するか** —— `shared_with` は属性辞書に載せない（決定 4）ため、
   消費側（Wiki / Graph / Retrieval）は自 DB に共有情報を持たない。越境参照の方式
   （イベントで各サービスへ複製する／スコープ解決時に文書 ID 集合へ展開する 等）は IADR-0253 が
   決めておらず、**新たな決定（IADR 改定）が要る**。本作業は貯蔵（DocumentShare）と所有者限定の
   管理 API までを実装し、分岐 ③ の生成・評価は実装しない
3. **orval 生成物の再生成**（`pnpm run codegen`）と **SC-09 フロントの語彙追随**（`POLICY_ACTIONS` へ
   `write`、選択肢・翻訳・語彙テスト）
4. **write ポリシーの配備**（計画の write 規則 `doc.owner ∈ { ${current_user} }` をどのポリシーとして
   シードするか）。FR-21 の本文書き込みは `DocumentBodyIntake.CanWrite` が直接同じ規則を強制しており、
   `/authz/scope` の write 解決が待たれるのは #993（GraphService の書き込み経路）である
5. **`${current_groups}` の束縛**（ADR-0036 D-03 の 2 変数目）——IADR-0253 決定 3 が
   「増やさない」を維持しており、共有先ベース分岐の実装時に併せて判断する
