---
title: 検索の絞り込み指定が ABAC の許可値集合を広げる欠陥を塞ぎ、narrowing の規則を 1 か所へ寄せる
type: spec
status: done
related_ids: [FR-03, FR-05, FR-07, FR-19, NFR-09, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0036, ADR-0043, ADR-0046, IADR-0012, IADR-0151, IADR-0253, IADR-0410, IADR-0415]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 呼び出し元の指定は絞るだけで広げない（#1340）

## 起点

- FR-03（検索）／FR-05（ABAC）／SC-01・SC-08（対象範囲で絞る操作）／NFR-09（認可）
- 計画 ADR: `ADR-0004`（ABAC）／`ADR-0043`（権限内属性値）／`ADR-0046` D-06・`ADR-0036`（read の選言）
- 実装 ADR: [[IADR-0012]]（deny-by-default）／[[IADR-0151]]（検索と候補で同じフィルタ）／
  [[IADR-0253]] 決定 1・2（名前つき分岐・**union は選言の上位集合ではない**）／
  [[IADR-0410]]（呼び出し元が解決した scope を信じない）／[[IADR-0415]]（本 PR で新設）
- issue: #1340（本体）／#1339（同じ受け口の別の穴。**本 PR では閉じない**）

## 🔴 実測（`develop` `2a7c2a44`。試験を書いて走らせた）

`POST /search` に対し、**認証済み一般利用者が SPA から送れる値だけ**で:

| 送ったもの | 期待 | 実測 |
| --- | --- | --- |
| 許可 `dept ∈ {sales}` ＋ 指定 `dept=hr` | hr は現れない | 🔴 **hr が返る**（権限の拡大） |
| 許可 `dept ∈ {sales, eng}` ＋ 指定 `dept=sales` | eng は消える | 🔴 **eng が返る**（絞り込みが効かない） |

**偽装は要らない。** `AttributeFilters` は SC-01 / SC-08 の「対象範囲で絞る」操作そのものであり、
`SearchBffEndpoints.cs:76` がクライアントの値を**検証せずそのまま後段へ渡している**。

### 原因

`HybridSearchService.BuildFilters` の**分岐が無い経路**が、利用者指定と ABAC 許可を
**同じキーの下へ union している**（`Add` が値を追記する）。評価は
「フィルタ間は AND、値集合内は OR」なので、許可値集合に利用者の値が足される。

🔴 **分岐がある経路は正しい** —— `ScopeFilter(BuildUserFilters(...), branches)` は
利用者指定を選言全体と **AND** で重ねる。**壊れているのは非分岐の経路だけである。**

### なぜ既存試験が捕まえなかったか

`HybridSearchEndpointTests.PostSearch_AppliesAbacFilter_ExcludesUnauthorized` は
スコープを `AccessScope([], GrantsAccess: true)`（**フィルタ空**）で組んでおり、
**利用者指定と ABAC 許可が同じキーで重なる場合を 1 度も通っていない。**
「ABAC フィルタを適用する」と名乗る試験が、**重なりという肝心の場合を測っていなかった。**

## 🔴 正しい規則は既にリポジトリの中にある

`AiAnalysisService.Domain.DataRangeScopeResolver` が**同じ問題に対する規則**を、
分岐の扱いまで含めて明記・実装している。

- 「実効スコープは ABAC 許可スコープの**部分集合**でなければならない。範囲は権限を一切広げない」
- ABAC が制約するキー ∧ 範囲が同キーを指定 → **値集合の積**。積が空なら **deny**
- ABAC が制約しないキーを範囲が指定 → 追加（安全な narrowing）
- 分岐があるときは**分岐ごとに独立して**交差させ、**積が空の分岐だけを捨てる**
- 🔴 **キー単位 union へ畳まない**（[[IADR-0253]] 決定 2 の反例）

**呼び出し元側にあるその規則が、受け口側に無い。** これが #1340 の正体である。

## 決定

### 決定 1: 🔴 narrowing の規則を**共有点へ 1 つだけ**置く（`Knowledge.Contracts.ScopeNarrowing`）

`DataRangeScopeResolver` の本体を `Knowledge.Contracts` へ移し、
**AiAnalysis も RetrievalService も同じ関数を通す**。

🔴 **写して直さない。** 同じ規則を 2 か所に持つのは本リポジトリが繰り返し踏んでいる形であり
（[[IADR-0412]] 決定 6 / #1330 の 5 巡 / [[IADR-0411]] の抽出点 6 か所）、
**しかも今回は「片方にしか無い」ことが欠陥そのものだった。**

`DataRangeScopeResolver` は**呼び出し元の語彙（データ範囲）で受ける薄い入口**として残す
（`AnalysisDataRange` を知っているのは AiAnalysis だけである）。

### 決定 2: `BuildFilters` は利用者指定を**交差**させてから組み立てる

非分岐経路の union をやめ、`ScopeNarrowing` を通した実効スコープから `ScopeFilter` を作る。

🔴 **分岐経路の現行挙動は変えない**（元から正しい）。**変えない部分を変えない**ことも実測で固定する。

### 決定 3: 🔴 積が空なら **deny**（当該キーだけを黙って外さない）

`DataRangeScopeResolver` の既存規則をそのまま使う ——
「積が空 = 範囲が権限の外を指している。安全側に倒し全体を deny」。

**「そのキーの絞り込みを無かったことにする」は採らない** ——
権限の外を指した指定が**全件表示に化ける**（いちばん危ない縮退）。

### 決定 4: 本 PR は #1339（主張された `Scope` を信じる件）を**閉じない**

#1339 は「そもそも `Scope` を呼び出し元から受けてよいのか」であり、**別の判断**である。
本 PR は **`Scope` を受ける前提のまま、利用者指定が権限を広げないこと**だけを直す。
🔴 **2 つを混ぜると、どちらが直ったのかレビューで分からなくなる。**

## 触るファイル

| 面 | ファイル | 内容 |
| --- | --- | --- |
| 共有 | `Knowledge.Contracts/Dtos/ScopeNarrowing.cs`（新規） | narrowing の規則（分岐込み）の唯一の実装 |
| 呼び出し元 | `AiAnalysisService/Domain/DataRangeScopeResolver.cs` | 本体を委譲へ。データ範囲の入口だけ残す |
| 受け口 | `RetrievalService/.../HybridSearchService.cs` | `BuildFilters` が交差を通る |
| 試験 | `RetrievalService/Tests/.../ScopeIsNotWidenedByCallerTests.cs`（新規） | 拡大しない・絞れる・陽性対照 |
| 記録 | `.ai-context/adr/IADR-0415_….md` ＋ 索引行 | 決定 1〜4 |

## テスト（受け入れ基準）

- [x] 🔴 許可 `{sales}` ＋ 指定 `hr` → hr は現れない（M-1 / M-3 が固定）
- [x] 🔴 許可 `{sales, eng}` ＋ 指定 `sales` → eng は現れない（絞り込みが効く。M-1 / M-3 が固定）
- [x] 陽性対照: 許可の中の指定は結果に残る
- [x] 積が空 → **全体 deny**（当該キーを無視して全件にしない）（M-2 が固定）
- [x] 分岐経路の挙動が変わらない（利用者指定は選言全体と AND のまま）
- [x] `DataRangeScopeResolverTests` が**そのまま緑**（委譲で意味が変わっていないこと）
- [x] `/search/attribute-values` に同じ穴が無いことを確かめる（**利用者指定の入口を持たず `Scope` だけを見る**。ただし #1339 の対象ではある）
- [x] 変異試験で赤を実測する

## 変異試験（実出力。すべてビルドし直して実走し、戻したことは試験の緑で確認した）

| # | 変異 | 位置 | 赤になった試験 |
| --- | --- | --- | --- |
| M-1 | 利用者指定を交差させない（無視する） | `HybridSearchService` | **2** |
| M-2 | 積が空のときの deny を `ScopeFilter.Empty`（制約なし）へ倒す | 同上 | **1** |
| M-3 | 共有点の**交差を union へ戻す**（元の欠陥の再現） | `ScopeNarrowing` | 🔴 **8** |

M-3 の内訳: 本 PR の 2 本 ＋ **AiAnalysis の既存 6 本**。

🔴 **M-3 が両サービスの試験を同時に殺すことが、決定 1 の「共有点が本当に共有されている」証拠である。**
規則が 1 か所なら、そこを壊すと両方が赤くなる。**それは写しでは起こらない。**

## 実測（数字）

`RetrievalService.Tests` 211 → **213**。`AiAnalysisService.Tests` **138（不変）** ——
委譲で意味が変わっていないことの現れである。knowledge 全ユニット失敗 0・スキップの増減なし。

## やらないこと

- **#1339 を閉じること**（決定 4）
- **`/search` に認可を掛けること**（#1318 欠陥 B。別の判断）
- **BFF で `AttributeFilters` の値域を検証すること** —— 受け口が守るべき性質を
  呼び出し元へ移すことになる（後段が守らなければ、別の呼び出し元から同じ穴が開く）
