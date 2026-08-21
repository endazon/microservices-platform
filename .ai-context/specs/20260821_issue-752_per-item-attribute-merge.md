---
title: 作業仕様書 — 取り込み経路をアイテム単位の属性マージへ改める（#752 段 1）
type: spec
status: done
related_ids:
  - FR-05
  - UC-04
  - ADR-0036
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "06_technical/09_datasource-connectors.md §システム投入経路での owner / department（fixed）"
  - "ADR-0036（所有者ベース裁量制御）"
related_adrs:
  - IADR-0019
  - IADR-0051
issue: "#752"
---

# 作業仕様書: 取り込み経路をアイテム単位の属性マージへ改める（#752 段 1）

## 起点となる計画書（トレーサビリティ）

- 機能要求: `FR-05`（ABAC 属性）／ユースケース `UC-04`（データソース同期）
- 計画 ADR: `ADR-0036`（所有者ベース裁量制御）
- 計画書: `06_technical/09_datasource-connectors.md`（**fixed**）§システム投入経路での `owner` / `department`

## 着手前の調査で分かったこと —— **issue の想定より手前に問題がある**

`#752` は「`SourceItem` が更新者を運んでいない」ことを問題としている。**それは正しいが、契約を直すだけでは効果がゼロである。**

実測（`Foundation/Services/DataSourceSyncService.cs`）:

```csharp
// Map: データソースの既定 ABAC 属性（…）を原本へ付与する。
var attributes = source.GetEffectiveAttributes();   // ← ループの「外」で 1 回だけ

foreach (var item in items)
{
    …
    await bus.Publish(new RawDocumentFetched(
        …, new Dictionary<string, string>(attributes), [], …), ct);   // ← 同じ辞書の複製
}
```

🔴 **属性はソース単位で 1 回だけ計算され、全アイテムが同一の辞書を受け取る。**
**アイテムごとの属性をマージする経路が存在しない。** したがって `SourceItem` に更新者を足しても、
その値が `RawDocumentFetched` に載る道が無い。

**同じファイルのコメントが誤っている**（本段で是正する）:

> ソースのメタ（所在・部門・フォルダ・**更新者**等）の写像先は ABAC 基本属性であり、タグではない。
> **それらは上の `attributes` に載っている。**

**更新者は `attributes` に載っていない。** `attributes` はソース単位であり、
**アイテムごとの更新者を構造上運べない。** この記述は「載せる先はここである」という設計意図としては
正しいが、「載っている」という現況の記述としては**偽**である。

## スコープ（本段）

**アイテム単位の属性マージを可能にする配管だけ**を入れる。**値を載せるコネクタは本段では作らない。**

1. `SourceItem` に更新者を運ぶ器を足す（`string? UpdatedBy`。**null 許容**）
2. `DataSource` にアイテム単位の上書きを受け取る解決口を足す
3. `DataSourceSyncService.RunAsync` の属性計算を**アイテムごと**へ移す
4. 誤ったコメントを是正する
5. **どのコネクタも値を載せないので、挙動は 1 バイトも変わらない**ことをテストで固定する

### 優先順位（計画に従う）

`WithRequiredAttributeFailsafe` は「**明示指定は上書きしない**」を既に守っている。本段もそれに従う。

| 順位 | 供給元 | 根拠 |
| ---: | --- | --- |
| 1 | `DataSource.DefaultAttributes` の明示 `owner` | 「明示指定は上書きしない」（既存規約・`Create_WithExplicitOwner_PreservesValue`） |
| 2 | **アイテム単位の更新者**（本段で器だけ作る） | 計画「ソース側の更新者・作成者を利用者識別子へ解決して入れる」 |
| 3 | 予約値 `system` | 計画「解決できないとき」 |

### スコープ外（理由つき）

- **コネクタ 4 実装が値を載せること** —— 3 本は**構造上取れない**ことを実測した（下表）。
  1 本（`db`）は取れる見込みがあるが、**識別子の名前空間に裁定が要る**（後述）。段 2 で扱う。
- **利用者識別子への解決** —— 本リポジトリに Keycloak 管理 API を叩くコードは**実測 0 件**であり、
  ゼロから作ることになる。かつ名前空間の裁定待ち。
- **`owner=system` の件数の実測** —— `scripts/measure-abac-combinations.js` は**稼働クラスタへの
  実接続が要る**（`platform-infra` 名前空間の Postgres / Keycloak）。本環境では実行できない。
  **したがって issue の受け入れの観点 2 は本段では満たせない。#752 は閉じない。**

### コネクタ 4 実装の実測（段 2 の判断材料）

| コネクタ | 更新者を取れるか | 根拠 |
| --- | --- | --- |
| `filesystem` | ❌ **取れない** | `Directory.EnumerateFiles` ＋ `FileInfo` のみ。.NET の `FileSystemAclExtensions` は **Windows 専用**で Linux では使えず、本リポジトリに `stat(2)` P/Invoke の下地は**実測 0 件**。加えて**「ファイル所有者」は「最終更新者」ではない** |
| `wiki` | ❌ **取れない** | 一覧 API の契約が `{ id, title?, updatedAt }` のみ。更新者フィールドが**契約に無い** |
| `saas` | ❌ **取れない** | 同上（`{ id, title?, updatedAt }`） |
| `db` | ⚠ **取れる見込み** | SQL が `SELECT id, updated FROM ( {query} ) AS src` に固定されている。**列を 1 本足せば**管理者のクエリ次第で運べる |

## 受け入れ基準（本段）

1. `SourceItem` が更新者を運ぶ器（`string? UpdatedBy`）を持つ
2. `DataSourceSyncService` が**アイテムごとに**属性を解決する
3. **どのコネクタも値を載せないため、発行される属性辞書は本段の前後で同一である**
   —— これをテストで固定する（回帰させないことの証明）
4. アイテムが更新者を運んできたら、それが予約値 `system` より優先される（**明示 `owner` には負ける**）
5. 誤ったコメント（「更新者は `attributes` に載っている」）を是正した
6. `dotnet build` / `dotnet test src/knowledge/backend/backend.slnx` が Failed 0
7. **変異試験**: 優先順位の 3 段（明示 > アイテム > 予約値）のそれぞれを壊すと、対応するテストだけが落ちる

## 母集合（規則 9・10 に従い、誤りの側の文字列で引く）

引いたコマンド（追跡下のみ。`src/ai-stock-trading` は別プロジェクトのため除外）:

```
git grep -n "SourceItem(" -- . ':!src/ai-stock-trading'
git grep -n "GetEffectiveAttributes" -- . ':!src/ai-stock-trading'
git grep -n "更新者" -- . ':!src/ai-stock-trading'
```

### 結果（実装後に確定した値）

| 軸 | 生の件数 | ファイル数 | 追随が要るもの |
| --- | ---: | ---: | ---: |
| 1. `SourceItem(` | 20 行 | 13 | **2**（`IDataSourceConnector.cs` の契約と `docs/data/data-source.md`） |
| 2. `GetEffectiveAttributes` | 40 行 | 12 | **2**（`DataSource.cs` と `DataSourceSyncService.cs`。実装そのもの） |
| 3. `更新者`（誤りの側の語） | — | 13 | **4**（下表） |

🔴 **規則 8（自己参照）**: 軸 1・3 の件数は**本仕様書をコミットする前**の値である。
本仕様書は 3 軸すべての検索語を含むため、コミット後は各軸のファイル数が **+1** される
（13 → 14 / 12 → 13 / 13 → 14）。**引き算を見せておく。**

### 規則 10 —— この変更で新たに誤りになる自分の記述（live のみ）

| # | 箇所 | 偽になった記述 | 対応 |
| --- | --- | --- | --- |
| 1 | `DataSource.cs` owner 節 | 「**器そのものが無い**ため、解消は `SourceItem` の契約変更を要する」 | 日付つき追記で是正。**倒れる理由が「器が無い」から「載せるコネクタが無い」へ変わった** |
| 2 | `docs/data/data-source.md` 供給源の表 | 「前段は**器が無い**」 | 「器は在るが載せるコネクタがまだ無い」へ |
| 3 | 同 §注記 | 「`SourceItem(Path, ModifiedAt, Size)` に**更新者を運ぶフィールドが無い**」 | 4 実装それぞれの事情つきで是正 |
| 4 | 同 §予約値の割合 | 「コネクタは更新者を運ばない」 | 「器は在るが載せるコネクタが無い」へ |
| 5 | `DataSourceSyncService.cs` タグ注記 | 「それらは上の `attributes` に載っている」（更新者は載っていなかった） | 日付つき追記で是正（本文の §着手前の調査を参照） |

**除外したもの（理由つき）:**

- **凍結記録 5 件**（`.ai-context/adr/IADR-0019` / `IADR-0199` / `IADR-0153`、
  `.ai-context/specs/` の 20260705 / 20260809 / 20260815 系 4 件）—— **本文を書き換えない運用**である。
  いずれも「当時の現況」として正しく、訂正の参照点は live 側（上表）に 1 つ置いた（[[IADR-0141]]）。
- **`scripts/measure-abac-combinations.js`** —— 「更新者」の語は**計測対象の説明**であり、
  契約の有無に触れていない。誤りではない。
- **コネクタ 4 実装とそのテスト** —— `SourceItem(...)` を**構築している側**であり、
  省略可能引数を足したので**呼び出しは 1 箇所も壊れない**（ビルドで実証）。本段では値を載せないため変更不要。
- **`docs/tests/FR-01_data-source-catalog.md`** —— `GetEffectiveAttributes` を引くが、
  引数なし版の失敗安全の振る舞いを説明しており、本段で変わらない。

## 裁定が要る点（段 2 の前に環流する）

🔴 **OS ユーザー名・DB の列値を、そのまま ABAC の `owner`（利用者識別子）に入れてよいか。**

計画は「ソース側の更新者・作成者を**利用者識別子へ解決して**入れる」と定めるが、
**別の名前空間の識別子をどう扱うかを定めていない。** 実装が勝手に「OS ユーザー名 = 利用者識別子」と
決めると、受け入れの観点 3「**偽の識別子を入れない**」に抵触しうる。

**これは実装側だけで決められない。** `/plan-feedback` で計画へ環流し、裁定を待つ。
