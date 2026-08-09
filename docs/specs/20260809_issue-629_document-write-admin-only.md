---
title: 作業仕様書 文書の書き込み口を管理者限定へ狭める（#629）
type: spec
status: fixed
related_ids: [FR-06, UC-03, SC-05, IADR-0044, IADR-0039, IADR-0127, IADR-0128]
author: Claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "../screens/SC-05_document-management.md"
  - "../api/BFF_bff-surface.md"
  - "../adr/IADR-0044_backend-service-authorization-defense-in-depth.md"
  - "../adr/IADR-0039_datasource-management-bff-and-role-gating.md"
  - "../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
---

# 仕様書: 文書の書き込み口を管理者限定へ狭める（#629）

> **これは機能追加ではなく、計画との乖離の是正である。** 計画は 2026-08-05（裁定 Q19）から
> 「破壊的操作は管理者限定」と定めており、**実装だけが運用者にも開いたままだった**。
> **#628（データソース）と同型**であり、手順はそのまま適用できる。

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#629** ／ 起点 ID: **FR-06**（文書管理）・**UC-03**・**SC-05**（文書管理画面）
- 計画の正: `05_screens/01_screens.md` §SC-05「管理系 3 画面の閲覧ロール」
  （**破壊的操作の列挙の正**。2026-08-09 に planning#299 で一本化）
- 制約: [[IADR-0044]]（多層防御）／[[IADR-0128]] 決定 1（グループ既定を残して `AdminOnly` を積む）／
  [[IADR-0127]] 決定 1（押せないボタンを置かない）／[[IADR-0039]] 決定 2（実効境界はサーバ側）
- 先例: **#501**（再変換）→ **#628**（データソースの登録・無効化）
- 規約: `.claude/rules/traceability.md`

## 母集合の引き直し（[[IADR-0141]] 決定 1）

**走査基準**: `origin/develop` = **`c614c34`**。**issue 本文の表を転記せず、実ファイルから引き直した。**

### 軸 1: `/documents` を配るファイル（全数）

```console
$ grep -rln 'MapGroup("/documents")\|MapGroup("/bff/documents")' --include=*.cs src/
src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/Endpoints/DocumentEndpoints.cs
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs
```

**2 ファイルだけである。** 他に文書を書き込む口は無い。

### 軸 2: 書き込み口の全数（`write` グループに載っているもの）

| # | 口 | サービス | BFF | 計画上の分類 | 本作業 |
| --- | --- | --- | --- | --- | --- |
| 1 | `POST /` （登録） | `:45` | `:84` | **破壊的**（「登録」） | **`AdminOnly` を積む** |
| 2 | `PUT /{id}` （編集） | `:73` | `:97` | **破壊的**（「文書の編集」） | **積む** |
| 3 | `PATCH /{id}/metadata` | `:109` | **無い** | **破壊的**（「更新」「文書の編集」） | **サービスのみ積む** |
| 4 | `POST /{id}/publish` | `:138` | `:103` | **列挙に無い**（§判断 1） | **積む** |
| 5 | `POST /{id}/archive` | `:159` | `:109` | **列挙に無い**（§判断 1） | **積む** |
| 6 | `DELETE /{id}` （削除） | `:195` | `:115` | **破壊的**（「文書の削除」） | **積む** |

**★ issue 本文との差異 2 件（転記していたら踏んでいた）。**

1. **`PATCH /{id}/metadata` に BFF の口は無い。** issue は「BFF 側も同じ構造である」と書いているが、
   実測では BFF の書き込み口は **5 本**（`PATCH` が無い）である。**本作業で BFF へ足さない**
   —— 射程は「狭める」であって、口を増やすことではない。
2. **`docs/api/BFF_bff-surface.md` の `/bff/documents` は「6 行」ではなく 9 行**
   （うち書き込みが 5 行・`:117` `:119` `:120` `:123` `:124`、読み取りが 4 行）。**追随するのは書き込みの 5 行**である。

### 軸 3: 読み取り口（**狭めない**ことを固定する対象）

サービス `:29` `:38` `:172` `:186`（一覧・個別・版一覧・版個別）と BFF `:26` `:39` `:47` `:61`
（一覧・個別・版・本文）。**いずれも端点認可なし or admin ＋ operator のまま据え置く**
（裁定 Q19「閲覧は管理者・運用者に開く」）。

### 軸 4: 画面（`DocumentManagementPage.tsx`）が出しているボタン

**＋新規登録 / 編集 / 公開 / アーカイブ / 削除 の 5 つ。全部が書き込みである。**
**現状は運用者にも全部見えている**（ロール判定が 1 つも無い。実測: 同ファイルに `useHasAnyRole` は 0 件）。

### 引かなかった軸と理由

| 軸 | 引いたか | 理由 |
| --- | --- | --- |
| `AttachmentEndpoints` 等の別資源 | ❌ | 軸 1 のとおり `/documents` を配るのは 2 ファイルのみ |
| SC-03（`/docs/$id`）の画面 | ❌ | **読み取り専用**であり、書き込みボタンを持たない（実測） |
| ルートのゲート（`RequireRole`） | ❌ | **閲覧の下限**であり、狭めると Q19 に反する（据え置く） |
| `contract-schema-baseline.json` | ❌ | **契約（DTO）は変わらない**。変えるのは認可だけである |

## 判断（仕様書＝本書が正）

### ★ 判断 1: `publish` / `archive` は**管理者限定**にする

**計画の列挙に無い。** planning#299 が一本化した列挙は
「**登録・更新・無効化・再変換・文書の編集/削除**」であり、公開・アーカイブに触れていない。
issue は「planning#299 が新設した基準を当てはめて判断せよ」と指示している。

**その基準は 2 つの条件を同時に満たすことを求めている**（§SC-06 の手動同期の本文を読むと分かる）。

| 条件 | 手動同期（唯一の適用例） | `publish` | `archive` |
| --- | --- | --- | --- |
| (a) 既存データを壊さない | ✅ 増分同期＋変換の冪等性という**根拠つき** | ⭕ データは消えない | **❌ 可視性を落とす**（issue も同旨） |
| (b) 運用者の一次対応に要る | ✅ 「SC-10 で異常に気づいたその場で再同期」 | **❌ 該当なし** | **❌ 該当なし** |

**どちらも (b) を満たさない。** 手動同期が例外になったのは「壊さないから」だけではなく、
**Q19 が閲覧を開いた趣旨（運用者が原因画面へ入れないと一次対応が成立しない）と同じ判断だったから**である。
公開・アーカイブは異常への一次対応ではなく、**文書の公開範囲を決める統制行為**である。

**加えて、計画は例外を 1 つしか名指ししていない**——「**手動同期（SC-06）がこれにあたる**」。
**名指しの無い操作の既定は一般則（破壊的操作は管理者限定）である。**

**方向としても管理者限定が安全である。** 狭めすぎていた場合の是正は「後で開く」で済むが、
開いたままにして裁定が管理者限定と出た場合、**その間ずっと認可の穴が空いている**。

> **`decision-needed` へ回さない理由**: issue は「**判断が割れるなら**裁定へ回す」と条件付きである。
> 上表のとおり (b) で明確に分かれるため、**割れていない**。
> ただし**計画の列挙に名前が無いこと自体は計画側の不足**なので、
> `/plan-feedback` で「列挙へ公開・アーカイブを明示的に加える（または例外として明記する）」を環流する。

### 判断 2: グループ既定（admin ＋ operator）は**残す**

`write` グループの `RequireRole(Admin, Operator)` を消して `AdminOnly` へ置き換えるのではなく、
**各端点へ `AdminOnly` を積む**（AND 合成で実効 admin のみ）。
[[IADR-0128]] 決定 1 が #501 で確立し **#628 が踏襲した形**であり、
**グループ既定は「閲覧の下限」を表す**ので消さない。

### 判断 3: BFF へ `PATCH /{id}/metadata` を**足さない**

母集合 §軸 2 の差異 1 のとおり。**射程は「狭める」であって口を増やすことではない。**
画面は `PUT /{id}` を使っており（`useDocumentActions` の `update`）、**PATCH の口が無くて困っていない**。

### 判断 4: 画面は**ロールで出し分ける**（[[IADR-0127]] 決定 1）

**5 つ全部が書き込みなので、運用者には 5 つとも出さない。**
無言で消すと「何もできない壊れた画面」に見えるので、**理由の文言を残す**（#628 と同じ形）。
**実効境界はサーバ側**であり、ここは表示制御にすぎない（[[IADR-0039]] 決定 2）。

**一覧・詳細リンクは残す**——閲覧は運用者に開いたままだからである（Q19）。

### 判断 5: 新 IADR は**起こさない**

**既存の決定を変えていない。** [[IADR-0128]] 決定 1（積む形）・[[IADR-0127]] 決定 1（押せないボタンを置かない）・
[[IADR-0044]]（多層防御）を**そのまま適用する**だけである。
**#628 も同じ理由で新 IADR を起こしていない**（先例に倣う）。

## 受け入れ基準 → テストの写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 運用者は 6 口すべてで **403**（サービス直） | `DocumentEndpoints` 系テストへ追加 |
| 2 | 運用者は 5 口すべてで **403**（BFF 経由） | `BffDocumentEndpointTests` 系へ追加 |
| 3 | 管理者は従来どおり **成功**（狭めすぎていない） | 同上・対で固定 |
| 4 | 運用者の**閲覧は従来どおり**（一覧・個別・版・本文） | 同上・対で固定 |
| 5 | 画面が運用者へ 5 つのボタンを**出さない** | `DocumentManagementPage.test.tsx` |
| 6 | 画面が管理者へは**出す** | 同上・対で固定 |

**「狭める」と「狭めすぎない」を必ず対で固定する**（#628 が採った形）。

## 追随する文書

- `docs/screens/SC-05_document-management.md` §アクセス
- `docs/api/BFF_bff-surface.md` の書き込み **5 行**（軸 2 の差異 2 のとおり 6 行ではない）
- [[IADR-0039]] の 2026-08-09 追記（#628 が書いた「SC-05 は同型の逸脱が残っている」）を**解消済みへ**

## 検証（実走した結果・base = `c614c34`）

| コマンド | 結果 |
| --- | --- |
| `dotnet build knowledge/backend/backend.slnx` | **Build succeeded**（0 Error） |
| `dotnet test knowledge/backend/backend.slnx` | **Failed 0**。`DocumentService.Api.Tests` は **92 → 101**（**+9**） |
| `dotnet test platform/backend/Bff/Platform.Bff.Tests` | **Failed 0 / Passed 184 / Skipped 1**（**175 → 184**。**+9**） |
| `dotnet format knowledge/backend/backend.slnx --verify-no-changes` | **exit 0** |
| `pnpm run typecheck` | 3 プロジェクトとも Done |
| `pnpm run lint` | **0 errors**（既存の `react-refresh` 警告 9 件のみ） |
| `pnpm run format:check` | OK（初回は本画面 1 件が warn → `prettier --write` で整形） |
| `pnpm run test:coverage` | **63 ファイル / 612 件 Passed**（**609 → 612**。SC-05 は 16 → 19） |
| `pnpm run i18n` | 再生成差分をコミット（**新規文言 1 件の `en` 訳を追加**。§つまずいた点） |
| `node scripts/check-i18n-catalogs.js` | OK（未翻訳・fuzzy・obsolete なし） |
| `pnpm run build` ＋ `node scripts/check-chunk-budget.js` | 初回は **+0.20 kB** で床超過 → `--update` で **582.96 → 583.16 kB** へ更新 |
| `node scripts/check-static-egress.js --require .../dist` | OK（外部オリジンからの取得なし） |
| `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-contract-schema` / `check-test-spec-coverage` / `check-test-traceability` | **すべて OK** |

### カバレッジのラチェット（`src/vitest.config.ts`）

導出規則は **「MSP 所有分の実測から 5pt 下・切り捨て」**（`lcov.info` を `ai-stock-trading` の有無で分けて集計した）。

| | 全ユニット横断 | **MSP 所有分のみ** | 導かれる床 | 従前 |
| --- | --- | --- | --- | --- |
| lines / statements | 96.5%（5768/5977） | **96.01%**（4501/4688） | **91** | 90 |
| branches | 90.98%（1201/1320） | **92.20%**（934/1013） | **87** | 86 |
| functions | 91.94%（411/447） | **93.25%**（318/341） | 88 | 88 |

lines/statements を 90 → 91、branches を 86 → 87 へ引き上げた（functions は据え置き）。

### 変異試験（**テストが本当に穴を捕まえるか**）

**必須である。** `AdminOnly` を積む作業は、**積み忘れても既存テストが緑のまま通る**
（グループ既定は据え置いたので `viewer` は元から 403 になる）。

| 変異 | 結果 |
| --- | --- |
| BFF の `publish` から `AdminOnly` を外す | **Failed 1 / Passed 183** —— `Write_AsOperator_IsForbidden(POST, /bff/documents/{id}/publish)` **だけ**が落ちた |
| サービスの `DELETE` から `AdminOnly` を外す | **Failed 1 / Passed 100** —— `Write_OperatorRole_Returns403(DELETE, /documents/{id})` **だけ**が落ちた |
| 両方戻す | **Failed 0**（`git diff` も差分 0 で変異が残っていないことを確認） |

**落ちたのが狙った 1 件だけ**である点が重要である —— 巻き添えで落ちるなら、
そのテストは口ごとの認可ではなく別の何かを見ていることになる。

## つまずいた点（次に同じことをする人向け）

1. **`dotnet format` が `}).RequireAuthorization(...)` の直前のコメントを許さない。**
   ラムダ本体の閉じと登録の間にコメントを置くと `error WHITESPACE` が 12 件出る。
   **理由のコメントは `write.MapXxx(` の直前へ置く**（本 PR はそう直した）。
2. **新しい表示文言を足したら `en` の訳も書く。** `pnpm run i18n` が
   `Missing 1 translation(s)` で落ちる。`check-i18n-catalogs.js` は再生成後にしか効かない。
3. **`git worktree` には submodule が付いてこない。** `Platform.Bff` のビルドが
   `AiStockTrading` の名前解決で落ちるので、`git submodule update --init src/ai-stock-trading` が要る。

## 申し送り

- **計画の破壊的操作の列挙に `publish` / `archive` の名前が無い。** 本作業は planning#299 の基準を
  当てはめて管理者限定と判断したが（§判断 1）、**基準の当てはめであって明文ではない**。
  `/plan-feedback` で「列挙へ明示的に加える（または例外として明記する）」を環流する。
- **BFF に `PATCH /{id}/metadata` の口が無い**（§判断 3）。画面は `PUT` を使っており困っていないが、
  **サービス側にだけ在る書き込み口**であることは記録しておく。
