---
title: IADR-0392 更新者は wiki / saas が構成可能な JSON 項目・db が opt-in の列から取り、取れなかったことと空だったことを分けて数える
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - UC-04
  - ADR-0036
  - ADR-0074
  - IADR-0051
  - IADR-0053
  - IADR-0054
  - IADR-0055
  - IADR-0199
  - IADR-0359
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md (決定 3・5 / §残るもの)
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md (§システム投入経路)
---

# IADR-0392: コネクタが更新者を取得する経路と、「取れなかった」と「空だった」の分離（#752）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 計画: `FR-05` / `UC-04` / `ADR-0036`（所有者ベース裁量制御）/ `ADR-0074`（写像表の器）/
  `06_technical/09_datasource-connectors.md` §システム投入経路。
- 実装: [IADR-0359](./IADR-0359_owner-mapping-table-container-and-resolver.md)（写像表の器と解決器。#1194）。
  本 IADR は**その入口**（値の取得元）を決める。
- 作業仕様書: `.ai-context/specs/20260905_issue-752_connector-updated-by.md`。

## コンテキストと課題

取り込み経路の `owner` は解決順（① 身元プロバイダのユーザー検索 → ② データソース単位の写像表 →
予約値 `system`）で決まる。② の器と解決器（`DataSource.ResolveOwner`）は #1194 で着地したが、
**入力が無かった** —— 実測（`origin/develop` `25b9485a`）で、製品コードの `SourceItem` 生成 4 箇所の
うち `UpdatedBy` を載せているものは **0 件**だった（陽性対照: `SourceItem` の出現は 58 箇所）。

従前の記録は残る 3 本の取得可否をこう書いていた。**そのうち 2 つは、測り直すと誤りだった。**

| コネクタ | 従前の記述 | 実測 |
| --- | --- | --- |
| `filesystem` | 構造上取れない | **正しい。** ファイル所有者を取る自明な手段が無く、そもそも所有者は最終更新者ではない |
| `wiki` / `saas` | 「**REST 契約に更新者フィールドが無い**」 | 🔴 **誤り。** その「契約」は外部組織のものではなく、`WikiPage` / `SaaSItem` という**自前の record** である。接続先は構成可能な汎用 JSON エンドポイントで、読む項目を決めているのは本リポジトリのコードだった |
| `db` | 「**解決器が入るまで列を足さない**」 | **当時は正しく、いまは前提が消えた。** `ADR-0074` 決定 5 の先後条件は #1194 で満たされた |

## 決定

1. **`wiki` / `saas` は更新者を「構成可能な JSON 項目名」で受ける。** DTO へ `[JsonExtensionData]` を
   足して未知項目を捕え、`Config["updatedByField"]`（既定 `updatedBy`）で引く。
   **名前を固定した宣言済みプロパティにしない** —— 実製品ごとに `lastModifiedBy` / `author` 等と
   名前が違い、実装が 1 つに決め打つと既定名のソースでしか当たらない。
2. **`db` は opt-in の `Config["updatedByColumn"]` が在るときだけ列を 1 本足す。**
   🔴 **無条件に足さない** —— `SELECT id, updated FROM ( {query} ) AS src` の射影を増やすと、
   その別名を持たない**既存の管理者クエリが全件 SQL エラー**になり、同期そのものが落ちる。
   列名は `IsSafeSqlIdentifier`（ASCII 英数字と `_`・先頭は英字か `_`・63 文字以内）で検証し、
   **通らなければ未設定として扱って警告する**。`query` を自由に書ける経路が別に在ることは、
   識別子を無検査で連結してよい理由にならない。
3. **`filesystem` は `UpdatedBy: null` を名前付き引数で明示的に運ぶ。**
   `ADR-0074` 決定 3 のとおり**構造上運べないことは欠陥ではない**。省略ではなく明示にするのは、
   「まだ書いていない」と「運べないと決めた」を読み分けられるようにするためである。
4. 🔴 **「取れなかった」と「取ったら空だった」を混ぜない。** 由来を `SourceUpdatedByOrigin` の
   4 値（`NotCarried` / `BlankAtSource` / `Unreadable` / `Carried`）で分類し、
   `Discover` 1 サイクルにつき 1 行だけ集計を記録する。
   **`NotCarried` では警告しない**（項目を構成していないのは正常な状態である）。
5. **契約の破壊的変更は 1 件も無い。** `SourceItem.UpdatedBy` は 2026-08-21 に既定値つきで入っており、
   本変更は追加していない。`Shared.Contracts` / `Knowledge.Contracts` は触らない。
   JSON 側は加算のみ、SQL 側は opt-in である。

## 理由

- **決定 1 が「構成可能」なのは、値域が本リポジトリの外にあるからである。** 既定名を 1 つ決めて
  終わりにすると、名前が違うソースでは**静かに `NotCarried` になり続ける** —— 誤りが警告にすら
  ならず、予約値の山として現れる。名前を運用側から与えられる形にしておく。
- **決定 2 が opt-in なのは、既定を変える変更が破壊的だからである。** 射影を 1 列増やすことは
  こちらから見れば小さな差分だが、ソース側から見れば**既存の同期が全部落ちる**。逃げ道の無い
  強制は入れない。
- **決定 4 は、`owner` の予約値を読む側のためである。** 計画は予約値の件数を測定値として読むと
  定めている（`ADR-0074` 決定 3 は**本 issue の完了判定には使わない**と限定した）。
  由来を潰すと「項目名の設定を間違えている」のか「ソース側が本当に空なのか」を
  区別できず、**同じ数字が別の意味を持ってしまう**。
- **`Carried` は `owner` を意味しない。** 解決段は写像表の完全一致（`Ordinal`）であり、
  当たらなければ `null` を返して予約値へ倒れる。**生の識別子は 1 件も `owner` へ入らない**
  （`ADR-0036`。誤った写像は偽の所有者を作り、裁量制御が意図しない相手に開く）。

## 検証

- 単体（xUnit・`DataSourceService.Tests` 234 件が緑）: 由来 4 値の分類、SQL 識別子の検証（`src.author` /
  `x FROM users; DROP TABLE t --` 等を拒否）、各コネクタの陽性・陰性。
- 端から端まで: 実 `WikiConnector` に JSON を食わせ、`SourceItem.UpdatedBy` → `ResolveOwner` →
  発行イベントの属性まで通す（陽性）。写像表に当たらないときは予約値へ倒れ、
  **生の識別子も他人の写像先も混ざらない**（陰性）。
- **変異試験**: `WikiConnector` の受け渡しだけを外すと**4 件が落ちる**（陽性 2・端から端まで 1・
  変異試験自身 1）。戻すと 234 件が緑に戻る。**陽性が空の主張でないことを対で示した。**

## 結果

- `filesystem` 由来の文書の `owner` は**今後も予約値のままである**（意図的な縮退）。
  🔴 **予約値の件数を達成度として読まない**（`ADR-0074` 決定 3）。
- `updatedByField` / `updatedByColumn` は `Config` 辞書のキーであり、**SC-06 のフォームには
  コネクタ設定の汎用編集欄が無い**ため、当面は API（`config`）から設定する。
- **写像表の中身は運用時に管理者が画面から入れる実行時データ**であり、実装の前提ではない。
  ① 身元プロバイダのユーザー検索は未配備のままでよい（解決順は ② だけで閉じる）。

### 残るもの

- 実 Wiki / SaaS 製品ごとの項目名の実測（**構成可能にしたので実装の変更は要らない**）。
- 稼働クラスタでの実接続は**測っていない**。`ADR-0074` 決定 3 が件数による完了判定を撤回しており、
  本変更は in-repo で閉じる。
