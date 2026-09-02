---
title: IADR-0350 層の理由と段の理由は別に持ち、段を下げても層の説明を失効させない
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-17
  - UC-10
  - ADR-0033
  - ADR-0065
  - ADR-0068
  - IADR-0261
  - IADR-0280
  - IADR-0281
  - IADR-0282
  - IADR-0319
  - IADR-0334
  - IADR-0349
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30) 決定 2・5
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 1・2・3
---

# IADR-0350: `LinkEdgeSynchronizer` を `Features/GraphDocuments/Sync/` へ下ろす（#1094）

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（実装）

## コンテキストと課題

`GraphService/Features/GraphDocuments/LinkEdgeSynchronizer.cs` を使う操作は
`GraphDocuments/Sync` の 1 つだけである（`Delete` は参照していない）。
**`ADR-0068` 決定 2 により 3 段目へ下ろすのが正しい位置であり、判定に裁量は無い。**

争点は位置ではなく、**このファイルが冒頭コメントに書いている自己申告**である。

> 🔴 **配置は合成ルート側（現 `Features/GraphDocuments/`）である。** `IADR-0280` 決定 2 の写像では
> 調整サービスは Application だが、本クラスが触る `GraphDbContext` / `Edge` / `EdgeType` は
> 段 2 の移送が済んでおらず、まだ Api 側にある（同 決定 1 の段階計画）。**依存の向きに従い、
> 依存先と同じ層に置く。** 段 2 で Persistence / Domain が移るときに一緒に移す。

**この注記は「配置」と書いているが、述べているのは層（`Features/` に居る理由）だけである。**
段（2 段目か 3 段目か）については何も言っていない。**「配置」という 1 語が 2 つの問いを
覆っているため、移送する者は「この注記が動くのを止めている」と読み得る。**
実際 #1062 の移送 PR 群はこのファイルを 2 段目に残し、理由を「同期器（判断 5）」と書いた。

## 決定

**決定 1: `Features/GraphDocuments/LinkEdgeSynchronizer.cs` を
`Features/GraphDocuments/Sync/LinkEdgeSynchronizer.cs` へ下ろす。**
namespace は `GraphService.Features.GraphDocuments.Sync`（`IADR-0261` の `<Svc>Service.*` 規約を維持）。
`ADR-0068` 決定 5「純粋な移送に留める」の範囲に収め、**リンク辺の差分更新の規則
（`IADR-0281`）には触れない。**

**決定 2: 層の理由と段の理由をコメント上でも分けて書く。**

冒頭コメントは「配置」と一括りにせず、**層の段落**（`IADR-0280` 決定 2 の写像との差と、
その理由＝依存の向き）と**段の段落**（`ADR-0068` 決定 2 の適用結果）を別に持つ。
**段を下げても層の理由は失効しない**ことを、コメント自身が示す形にする。

`IADR-0349` 決定 3 が定めた順序（先に層、次に段）を、**記録の側にも同じ形で持たせるもの**である。

**決定 3: 移送で型を失った名前空間の `using` は落とす。足さない。**

移送後 `GraphService.Features.GraphDocuments` に型が 1 つも無くなるため、
これを `using` していた 4 ファイル（`Sync/GraphDocumentSyncConsumer.cs`・`Program.cs`・
テスト 2 件）から**その 1 行だけを落とす。** 新たな `using` は 1 行も足していない ——
`GraphDocumentSyncConsumer` と 2 つのテストは `…GraphDocuments.Sync` 名前空間に居るため、
移送後の型は無修飾で見える（`IADR-0334` 決定 5 と同じ、C# の外側名前空間探索）。

**決定 4: テストは `IADR-0334` 決定 3 に従って一緒に動かす。**
`Tests/Features/GraphDocuments/LinkEdgeSyncTests.cs` は `LinkEdgeSynchronizer` を直接 `new` する
（主題である）ため、`Tests/Features/GraphDocuments/Sync/` へ写す。

## 理由

**決定 1 に裁量は無い。** `ADR-0068` 決定 2 は「そのファイルが 1 つの操作にしか使われないか」だけを
問う。走査の結果は 1 であり、**陽性対照**（同じサービスの `AiSuggestionEndpoints` は同じ走査で
4 操作が出る）が走査の効きを示している。#1062 が採った「同期器だから」という理由は、
`IADR-0319` が退けた**内容の性質による判定**そのものである。

🔴 **決定 2 が要るのは、注記が「配置」という語で 2 つの問いを覆っていたからである。**
`ADR-0068` は段の規則、`ADR-0065` 決定 1 は層の規則であり、**片方の答えでもう片方を
動かせない**（`IADR-0349` 決定 3）。ところが日本語の「配置」も英語の placement も両方を指す。
**語が問いを覆っている限り、次に読む者は同じ読み違えをする。** だから語を直すのではなく、
**段落を分けて 2 つの理由を並置する** —— どちらが動いたのかが見える形にする。

**決定 3 で `using` を「落とすだけ・足さない」に閉じるのは、退行の余地を減らすためである。**
`IADR-0334` 決定 5 が 166 ファイルで採ったのと同じ判断であり、**言語仕様が既に解決している
問題を編集で解こうとしない。** 落とす側は機械的に安全である（型が無くなった名前空間を
指す行だけを消す）。

## 結果

- **良い影響**
  - **`Features/GraphDocuments/` 直下の `.cs` が 0 件**になり、`Delete/` と `Sync/` の
    2 フォルダだけが残る。`ADR-0068` 決定 1 の形（登録表は 2 段目・操作の処理は 3 段目）と
    整合する —— この集約は `MapGroup` を持たない購読側であり、**登録表そのものが無い**
    （`IADR-0319` 決定 3「存在しない登録表を新設しない」がそのまま当たる）。
  - **`IADR-0319` が #1062 の走査で見つけて起票した違反が閉じる。**
  - 層の理由と段の理由が並置され、**次の移送で同じ読み違えが起きにくい。**
- **悪い影響 / トレードオフ**
  - 🔴 **判定は時点に依存する。** `Delete` が `LinkEdgeSynchronizer` を使い始めたら 2 段目へ
    戻す必要がある。`IADR-0319` が受け入れたのと同じ依存であり、同じ理由で受け入れる。
  - **`IADR-0280` 決定 1 の段 2（Persistence / Domain の移送）が来たとき、本クラスは
    もう一度動く。** そのとき動くのは**層**であり、段は改めて数え直すことになる。
    決定 2 の並置は、そのときどちらの理由が効いているかを読み手に示すためのものである。
  - **機械検査は置かない。** `IADR-0319` / `IADR-0349` と同じ理由（シンボル解決が要る）。

## 関連

- 計画 ADR: `ADR-0068` 決定 2・5、`ADR-0065` 決定 1・2・3、`ADR-0033` 決定 3・6
- 実装 IADR: `IADR-0349`（層を先に決め、`Features/` に居ると決まったものだけに決定 2 を当てる。
  本 IADR はその順序を記録の書き方へ広げる）、`IADR-0319`（段は数えて決める。本件の違反を
  発見した走査）、`IADR-0281`（リンク辺の差分更新の規則。**本 IADR は位置だけを変え、
  規則には触れない**）、`IADR-0280` 決定 1・2（層の段階計画）、`IADR-0282`（標準樹形）、
  `IADR-0334`（テストの鏡写し・`using` を足さない）、`IADR-0261`（namespace 規約）
- 作業仕様書: `.ai-context/specs/20260903_issue-1094_link-edge-synchronizer-placement.md`
- issue: #1094
