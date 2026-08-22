---
title: IADR-0249 上流ポート検査の母集合はサービス間 named client まで含める（判定は実効値で、上書きの有無ではない）
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - FR-13
  - IADR-0089
  - IADR-0245
  - IADR-0247
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0023_mesh-and-ports.md
---

# IADR-0249 上流ポート検査の母集合

## 状況

`check-bff-downstreams.js` の母集合は `Platform.Bff/Program.cs` **1 ファイル**だった。
**サービス間（service → service）の named HttpClient は、どの検査器も見ていなかった。**

その死角で **WikiService と AiAnalysisService が AuthorizationService を `:5005` で呼んでいた**。
`authorization-service` が開けているのは **8080 だけ**である（compose `expose: ["8080"]` /
helm `port: 8080`）。**`#342` / `IADR-0089` とまったく同じ形の 2 回目**であり、
CLAUDE.md「同型の事故が 2 回起きたら」を満たす。

### 壊れ方が `#342` より静か

到達失敗時の縮退は `AccessScopeResponse(userId, [], false)` ＝ **deny-by-default**。
方向は正しいが、**利用者には「権限が無い／文書が無い」としか見えない**。
`#342` は「21 秒待って 502」で気付けたが、**こちらは待った末に静かに空になる**。

## 決定 1: 新しい検査器を足さず、既存の母集合を広げる

`#919` で `SKIP_DIRS` から `dist` を外したのと同じ型である。`CALLERS` に
`Platform.Bff` / `AiAnalysisService` / `GraphService` / `WikiService` を持つ。

🔴 **compose と helm でサービスキーの綴りが違う**（`wiki-service` / `wiki`、
`aianalysis-service` / `aianalysis`）ので、呼び出し元ごとに両方を持つ。

## 決定 2: 判定は「実効値が 8080 でない」。「上書きが無い」ではない

**後発サービスはコード既定が既に `:8080`** であり、**上書きが無くても正しい** ——
`GraphService` / `ConversionService` / `ConfigurationService` / `RiskManagementService` /
`MarketMonitorService` がこれに当たる。
「上書きが無い」を違反にすると**これらが全部偽陽性になる**。

既存の `computeViolations` は最初から実効値ベース（`override ?? default` のポートを見る）
だったので、**母集合を広げるだけで済んだ**。

## 決定 3: 🔴 上書きの探索は必ず「呼び出し元のブロック内」で行う

**全文検索にしてはならない。** `Services__AuthorizationService` をファイル全体から探すと
**BFF 用の上書きが見つかり**、呼び出し元のブロックには無いのに「上書きあり」と判定される。

**本 issue の調査中に実際にこれで「違反 0 件」と誤答した。** 加えて、自作の粗い helm ブロック
抽出は `values.yaml` に `bff:` が 2 箇所（`ingress` 配下と `services` 配下）あるため
**2 行しか取れず**、存在しない違反 7 件を計上した。

**検査器は `extractServiceBlock`（`services:` 直下を見る）を必ず通す。**
🔴 **調査手順の欠陥は、そのまま検査器の欠陥になる。**

## 検出しないこと

- 🔴 **`:5005` が live で実際に不達であること。** 実測したのは**構成値とコード経路まで**であり、
  **「届かない」は `IADR-0089` からの帰結であって観測ではない**。
  `Integration Stack`（k3d ＋ helm）で確かめる余地はあるが、**本 PR は既に直してあるため、
  確かめられるのは「直った後に届くこと」**である。正の側の確認は別 issue の射程。
- **`Program.cs` 以外での named client 登録**。`AddHttpClient` を拡張メソッドや別ファイルへ
  出した場合は母集合から漏れる。**現時点では全サービスが `Program.cs` に置いている**（実測）。
- **`?? "http://..."` 形以外のコード既定**。パーサはこの形しか読まない。
  導出できなければ**違反として報告する**（黙って 0 件にしない）。
- **ポート以外のドリフト**（ホスト名の誤り・スキーム）。見るのはポートだけである。

## 影響

- 母集合が 1 → **4 呼び出し元**、downstream は 12 → **17 件**。
- 検出した 3 件（wiki の compose・helm、aianalysis の helm）を**直した**。
  **baseline（grandfather）は使っていない** —— 件数を実測してから決めた（3 件）。
- 診断メッセージを呼び出し元に依らない形へ変えた（「manifest の bff env に」→「当該サービスの env に」）。

## 代替案

- **新しい検査器を作る** —— 同じ不変条件を 2 本で持つことになる（`IADR-0144` が避けた形）。
- **違反を baseline に登録して ratchet にする** —— **3 件しかないので直せる**。
  baseline は grandfather であって承認ではなく、残せば「直さなくてよい」と読まれる。
- **全サービスの `Program.cs` を機械発見する**（`git ls-files -- '*Program.cs'`）——
  呼び出し元と compose / helm のキーの対応が機械では決まらない（綴りが違う）。
  **対応表を明示する形を採った**。新しいサービスが named client を足したら
  `CALLERS` へ追加する必要があり、**それを忘れると無音で漏れる**のが本決定の弱点である。
