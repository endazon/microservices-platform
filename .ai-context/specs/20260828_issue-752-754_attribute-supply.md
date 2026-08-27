---
title: 取り込み経路の owner / department 供給源 — 実装可能な範囲の確定と SC-06 更新経路の実装
type: spec
status: done
related_ids: [FR-05, UC-04, SC-06, ADR-0036, ADR-0054, IADR-0199]
author: Claude（実装）
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/10_feedback/20260815_ingestion-owner-department-resolution.md
  - planning:projects/microservices-platform/10_feedback/20260821_ingestion-owner-resolution-rule.md
---

# 仕様書: 取り込み経路の `owner` / `department` 供給源

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（ABAC 必須属性）／FR-01・FR-02（データソース管理）
- ユースケース（UC）: UC-04（データソース同期）
- 画面（SC）: SC-06（データソース管理画面）
- 関連 ADR: ADR-0036（所有者ベース裁量制御）・ADR-0034（ホップごとの ABAC 強制）・ADR-0054（`doc_scope`・属性の裁定）
- 実装 ADR: IADR-0199（必須属性フェイルセーフ）・IADR-0055（業務DB コネクタ）・IADR-0051（コネクタポート）
- 起点 issue: #752（`owner` 側）／#754（`department` 側）

## 目的・背景

#752 / #754 はいずれも「予約値が恒久的に積み上がる」ことへの環流債務である。計画
`09_datasource-connectors.md` §システム投入経路 は `system` / `unassigned` を
「**解決できなかったことの記録であり、既定ではない**」と定めており、積み上がること自体が
「コネクタが更新者・部門を運んでいない」という**報告**である。

本作業は当初「段 2: 4 コネクタが `SourceItem.UpdatedBy` を供給する」「`department` の
フォルダ写像を実装する」を射程として着手した。**着手前の実測により、その 2 つはいずれも
現時点では実装してはならないことが判明した。** 代わりに、**同じ issue が引き受けたまま
未実装で残っていた SC-06 の更新経路**を実装する。

## 対象範囲

- **対象**: SC-06 データソース管理画面の**既定属性の更新経路**（`confidentiality` /
  `department` / `lifecycle` の 3 属性を登録済みデータソースに対して変更できるようにする）。
- **対象外（理由は §計画書との差異）**:
  - コネクタ 4 実装が `SourceItem.UpdatedBy` に値を載せること（#752 段 2）
  - フォルダ → 部門コードの写像（#754 供給源 1）
  - ソース側権限情報からのヒント取り込み（#754 供給源 3。優先度低）
  - **既存データの遡及適用**（既に取り込み済みの文書の属性の埋め直し）。#516 の裁定待ちであり
    本作業では扱わない。本作業が効くのは**本変更以降に取り込まれる分**（前方経路）だけである。
  - `owner=system` / `department=unassigned` の**件数の実測**。受け入れの観点が求める
    `scripts/measure-abac-combinations.js` の実測には**稼働クラスタへの実接続**が要り、
    本環境（Docker 無し）では測れない。#752 / #754 を閉じるにはこれが別途要る。

## 実測（着手前）

### 1. コネクタ別 `UpdatedBy` 供給可否

**コネクタ実装のアイテム生成箇所と DTO を直接読んで確かめた**（計画側の可否表と一致する）。

| コネクタ | 実測 | 供給可否 | 予約値の読み |
| --- | --- | --- | --- |
| `filesystem` | `FileSystemConnector.cs:76` が `FileInfo`（`Length` / 更新日時）だけから `SourceItem` を作る | **不可** | **意図的な縮退。環流債務に数えない**（Linux にファイル所有者取得の下地が無く、**そもそもファイル所有者は最終更新者ではない**） |
| `wiki` | `WikiConnector.cs:100` の `WikiPage(Id, Title, UpdatedAt)` に更新者フィールドが無い | **不可** | 未対応。**REST 契約の拡張**で解消し得るため環流債務に数える |
| `saas` | `SaaSConnector.cs:175` の `SaaSItem(Id, Title, UpdatedAt)` に更新者フィールドが無い | **不可** | 同上 |
| `db` | `DatabaseConnector.cs:44` が管理者クエリを `SELECT id, updated FROM (...)` で包む。列の追加は可能 | **可（ただし後述の理由で載せない）** | ①② で解決できた分だけ入る |

**4 コネクタのうち 3 本は構造上取れない。** 残る `db` の 1 本も、後述のとおり現時点では載せてはならない。

### 2. 解決器（①②）の不在

`owner` の解決順は **① Keycloak ユーザー検索 → ② データソース単位の写像表 → 予約値 `system`**
と確定している（裁定 2026-08-16。planning#371 / planning#372）。**そのどちらも実装が無い。**

```console
$ grep -rni "admin/realms|KeycloakAdmin|client_credentials|admin-cli|写像表" src/knowledge/backend/
→ 該当 0 件（ヒットしたのは無関係の TagResolver / GraphAccessResolver のみ）
```

計画側も同じ実測を独立に行い、「① の Keycloak ユーザー検索は未配備である（管理 API を呼ぶ
実装が 0 件）」と記録している（planning `20260821_ingestion-owner-resolution-rule.md` §5）。

### 3. 🔴 `UpdatedBy` は解決を経ずに `owner` へ直結している

`DataSourceSyncService.cs:47-50`:

```csharp
private static Dictionary<string, string>? PerItemAttributes(SourceItem item)
    => string.IsNullOrWhiteSpace(item.UpdatedBy)
        ? null
        : new Dictionary<string, string> { [DataSource.OwnerKey] = item.UpdatedBy };
```

**解決段が挟まっていない。** 現在はどのコネクタも値を載せないため無害（`perItem` は常に null）
だが、**コネクタが生の値を載せた瞬間に、別名前空間の識別子がそのまま `owner` になる。**

### 4. SC-06 の `department` 入力欄（登録側）は実装済み

`DataSourceForm.tsx:50/84/91/167` に入力欄と送信経路があり、テスト
（`DataSourceManagementPage.test.tsx:155/249/493`）も存在する。**#767 で着地済みである。**

### 5. 母集合の走査（規則 6: 引いた結果と除外理由を残す）

**軸 1**「SC-06 の登録フォームに入力欄が無い」と述べた箇所（全文書・拡張子で絞らない）:
13 件ヒット。うち **`department` の文脈は 5 件**で、内訳は次のとおり。

| 箇所 | 扱い |
| --- | --- |
| `DataSource.cs:170` | **本作業で是正**（宣言済みファイル領域内。事実として誤り） |
| `IADR-0199` L68 | 既に `［2026-08-15 追記 / #767］` で是正済み。**変更不要** |
| `docs/data/data-source.md` L81 | 既に L82-89 の追記で是正済み。**変更不要** |
| `.ai-context/specs/20260815_issue-767_*.md`（2 件） | **凍結記録**（確定済み作業仕様書）。書き換えない |

除外した 8 件は SC-02 / SC-05 / IADR-0126 / IADR-0278 の**別画面・別文脈**であり、
`department` とも SC-06 とも関係しない（検索語が同じだけである）。

**軸 2**「未裁定」と述べた箇所（`DataSourceService/` 配下）: 4 件。

| 箇所 | 扱い |
| --- | --- |
| `DataSourceSyncService.cs:43` | **本作業で是正**（解決規則は 2026-08-16 に確定済み。「未裁定」は誤り） |
| `DataSource.cs:180` | **本作業で是正**（同上） |
| `DataSource.cs:153` | `lifecycle` の話であり、既に「裁定が下りた」と書いてある。**変更不要** |
| `DataSourceSyncServiceTests.cs:197` | 同じ誤りを含むが、**テストの意図の説明としては成立する**。本作業で併せて是正する |

## 設計

［2026-08-28 追記 / #1021］波 1 監査の指摘: 上表に `IDataSourceConnector.cs` の行が無いが、同ファイルの陳腐化注記も実際には是正済みである（走査語「未裁定」が異形「裁定が要る」を取りこぼした＝規則 2 の破れ。コード側に残存は無いことを再走査で確認済み）。

### 実装するもの: SC-06 既定属性の更新経路

計画 SC-06 は「**登録・更新フォーム**はデータソースの既定属性 3 つ（`confidentiality` /
`department` / `lifecycle`）を持つ」と**確定**している（2026-08-16）。登録側だけが実装され、
**更新側が無いため、登録済みデータソースの部門は後から設定できない**状態であった。

- 画面: 一覧の各行に「既定属性を編集」を置き、開くと現在値を初期表示するフォームを出す。
- 通信: **既存の orval 生成フック `useBffDataSourcePatch`**（PATCH）を使う。生成物は
  コミット済みで、**再生成も openapi の変更も要らない**。
- 🔴 **PATCH の `defaultAttributes` は「指定したときのみ**差し替える**」＝全置換である**
  （`DataSource.Patch` L263）。したがってフォームは**3 属性の完全な意図を毎回送る**。
  部分的に送ると他のキーが落ちる。
- 🔴 **PUT を使わない。** PUT は `config` の明示を要求し、応答のマスク済みの値（`***`）を
  書き戻すと**秘密を破壊する**（IADR-0148 決定 6 / IADR-0053）。PATCH は `config` を
  省略でき、この経路を踏まない。
- 未入力の `department` / 未指定の `lifecycle` は**キーごと送らない**（登録側と同じ規約。
  #767 / #796）。値の有無ではなくキーの有無が「指定しなかった」を表す。

### 実装しないもの（と、その理由）

**`db` コネクタに更新者列を足すことは、現時点では計画違反になる。** 上の実測 3 のとおり
`UpdatedBy` は解決を経ずに `owner` へ入る。計画は次を明記している。

> **別名前空間の識別子をそのまま `owner` へ入れてはならない。**
> **誤った写像は偽の所有者を作る**（同姓同名・退職者・共有アカウント）。
> **裁量制御が意図しない相手に開く**ため、**安全側は「解決しない」である。**

`owner` は ADR-0036 の裁量制御の判定軸である。解決器が無い状態で DB の列値を載せると、
**偽の所有者に裁量権が渡る**。したがって **`db` の値搭載は解決器（①②）の配備とセットでのみ
行える。** ①は未配備、②は写像表の保守責任者・置き場所が**組織側で未確定**である。

**フォルダ → 部門の写像も同様に実装してはならない。** 裁定は
「**部門コードの値域が定まるまで `department` の写像は行わない**」と明記しており、
値域（既存の部門マスタの所在）は**組織側で未確定**である。実装側で推定規則を決めない。

## 受け入れ基準

- [x] SC-06 の一覧から、登録済みデータソースの既定属性 3 つを更新できる
- [x] 更新は PATCH で行い、`config` を送らない（秘密のマスク書き戻しを踏まない）
- [x] 未入力の `department` / 未指定の `lifecycle` はキーごと送らない
- [x] 3 属性の完全な意図を毎回送る（PATCH の全置換セマンティクスに合わせる）
- [x] `owner` の挙動は 1 バイトも変わらない（コネクタは引き続き値を載せない）
- [x] `dotnet build src/knowledge/backend/backend.slnx` が緑
- [x] `pnpm run typecheck` / `pnpm run lint` がエラー 0
- [x] 管理しない属性（API から明示指定された `owner` 等）を更新で消さない
- [ ] `owner=system` / `department=unassigned` の件数が減ることの実測 —— **本環境では測れない**
      （稼働クラスタへの実接続が要る）。#752 / #754 を閉じる条件として残る

### 実測（コミット前）

| 検証 | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | **緑**（0 Warning / 0 Error） |
| `dotnet test`（`DataSourceService.Api.Tests`） | **136 / 136 passed** |
| `dotnet format --verify-no-changes` | **緑** |
| `vitest run knowledge/frontend/src/features/sc06-datasources` | **43 / 43 passed**（うち新規 5 件） |
| `pnpm run lint` | **エラー 0**（警告 8 件はすべて既存。`packages/ui` / `platform/frontend`） |
| `pnpm run format:check` | **緑** |
| `node scripts/check-contract-schema.js` | **OK**（90 型が baseline と一致。契約は変更していない） |
| `pnpm run typecheck` | `knowledge/frontend` は **Done**。`platform/frontend` は**既存の失敗**——
  `@ai-stock-trading/features` が解決できない（submodule `src/ai-stock-trading` が本 worktree に
  未チェックアウト）。**本変更に起因しない**（当該ファイルは触っていない） |

### 波末に要る後処理（本作業では行わない）

- **i18n カタログの再生成**（`pnpm run i18n`）。本作業は新規の表示文言を足しており、
  カタログ（`foundation/i18n/locales/<locale>/messages.{po,ts}`）が未追随である。
  **手で編集しない**（CI が再生成差分を検査する）。新規文言は
  「既定属性を編集: {sourceName}」「既定属性の編集」「既定属性」「更新する」
  「既定属性を更新しました。」「既定属性は、これ以降に取り込まれる文書に適用されます。
  取り込み済みの文書は変わりません。」の 6 つで、**`en` の訳が要る**
  （既存の「既定の部門」等は同じメッセージ ID を再利用するため追加不要）。
- **orval 生成物の再生成は不要である。** `useBffDataSourcePatch` は既にコミット済みの
  生成物に含まれており、`docs/api/openapi.yaml` も変更していない。

## テスト方針

- フロント（Vitest + Testing Library）: 既定属性の編集フォームが現在値を初期表示すること、
  PATCH に 3 属性の意図が載ること、未入力の `department` がキーごと落ちること、
  `config` を送らないこと。既存 `DataSourceManagementPage.test.tsx` の形式を踏襲する。
- バックエンド: **変更なし**（本作業はバックエンドの挙動を変えない）。既存の
  `DataSourceUpdateEndpointTests` が PATCH の意味論を既に固定している。

## 計画書との差異

- **差異: あり。**「4 コネクタが更新者を供給する」（#752 段 2）は、計画の解決順が求める
  ①② が未配備であるため**実施しない**。実施すると計画が明文で禁じる「偽の識別子」を作る。
  **これは計画との不一致ではなく、計画の条件付き記述（配備後に効く）に従った結果である。**
- **差異: あり。** 「フォルダ → 部門の写像」（#754 供給源 1）は、部門コードの値域が
  組織側で未確定のため**実施しない**（裁定の明文に従う）。
- **新たな環流は起票しない。** 上の 2 点はいずれも**既に裁定済み**であり、計画側に不足は無い。
  残っているのは組織側の取り決めと配備であって、計画の記述の問題ではない。

## 未決事項

- ① Keycloak ユーザー検索の配備（着手時は ADR-0026 / ADR-0032 とサービスクライアントの
  権限範囲を突き合わせること）
- ② `owner` の写像表・`department` のフォルダ写像表の保守責任者と置き場所（組織側）
- 部門コードの値域（組織側）
- 予約値の件数の実測環境（稼働クラスタ）
