---
title: グラフ多ホップ探索 — prune-before-expand・表示上限・ホップ超過の拒否
type: spec
status: draft
related_ids: [FR-17, UC-10, ADR-0033, ADR-0034, IADR-0241]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
---

# 仕様書: グラフ多ホップ探索（#909 / 親 #450 子2）

> **本仕様書は #908 のマージ前に書いている。** #908 のスキーマはレビューで動き得るため、
> **着手は #908 マージ後**とする。動いた場合は本書の §設計を引き直してから実装する。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-17**（探索は ABAC スコープ内に限定し**判定はホップごと**。閲覧権のない文書への辺は件数・匿名ノードを含め一切返さない）
- ユースケース: **UC-10**（関係を辿って根拠に到達する。hops 既定 2 / 上限 3）
- 関連 ADR: **ADR-0034** 決定 1・2・3・4 / ADR-0033 決定 6
- 実装 ADR: **IADR-0241**（暫定番号。#908 と同時に develop の最大＋1 へ付け替える）

## 対象範囲

### 対象

1. `GET /graph/{documentId}/neighbors?hops=` の BFS 探索
2. **prune-before-expand**（フロンティア拡張のたびに述語を評価し、拡張前に刈る）
3. 表示上限 ノード 200 / 辺 500。**述語通過後にのみ計数する**
4. `hops` 既定 2 / **上限 3 超過は 400 エラー**（黙って切り詰めない）
5. 打ち切りの決定的順序（`CreatedAt`, `Id`）

### 対象外

イベント購読（#911）、リンク抽出（#912）、辺の作成 API（#913）、AI 提案（#914）、BFF 公開（#916）。

## 設計

`AuthorizedNode` 型ゲート（#908）の上に実装する。フロンティアの要素型が `AuthorizedNode` であり、
`IGraphStore.LoadIncidentEdgesAsync` がそれしか受け取らないため、**非許可ノードからの展開は型として書けない**。

```
GetNeighborhood(userCtx, startDocId, hops):
  ① if hops > 3: return 400          // 黙ってクランプしない（決定 3）
     if hops is null: hops = 2
  ② scope = ResolveScope(userCtx)     // 1 回だけ。失敗は Granted=false
     if !scope.Granted: return 404
  ③ start = LoadNode(startDocId)
     if start is null: return 404
     origin = AuthorizedNode.Authorize(start, scope)
     if origin is null: return 404    // 非許可・欠落・不存在をすべて同一の 404 に倒す
  ④ visited = {startDocId}; nodes=[origin]; edges=[]; truncated=false
     frontier = [origin]
     for depth in 1..hops:
       candidateEdges = LoadIncidentEdges(frontier)      // 双方向（バックリンク）
       neighborNodes  = LoadNodes(candidateEdges.otherEnds() - visited)
       next = []
       for edge in candidateEdges (CreatedAt, Id の順):
         other = neighborNodes[edge.OtherEnd(frontier)]
         ⑤ authorized = other is null ? null : AuthorizedNode.Authorize(other, scope)
            if authorized is null: continue        // ★ホップごと判定。展開・計数の前★
         ⑥ if edges.count >= 500: truncated = true; break-all
            edges.add(edge)
            if authorized.DocumentId not in visited:
              if nodes.count >= 200: truncated = true; continue
              visited.add(...); nodes.add(authorized); next.add(authorized)
       frontier = next
       if frontier is empty: break
  ⑦ return AuthorizedGraphView.Seal(UnfilteredSubgraph(nodes, edges, truncated), scope)
```

**⑤ が ⑥ より前にあることが本 issue の全体である。** 逆順にすると、上限の計数が権限外品目を数え、
非許可ノードが「橋」として働く。

## 受け入れ基準

- [ ] `A→X→B`（X が非許可・他に許可経路なし）で **B が応答のどの形でも現れない**
- [ ] 権限外ノードが上限計数に入らない
- [ ] `hops=4` が 400、`hops` 省略が 2、`hops=3` が成功
- [ ] 対称型が双方向から辿れ、バックリンクが逆引きで返る
- [ ] 打ち切りが決定的（同一入力で 2 回実行して同一応答）
- [ ] `dotnet build` 警告 0 / `dotnet test` 全通過

## テスト方針

| ケース | 内容 |
| --- | --- |
| T-08 | **橋の否定形**: `A→X→B`、X 非許可、`hops=2` → 応答に B が無い |
| T-08P | **陽性対照**: 同一トポロジで **X を許可**にすると B が現れる |
| T-09 | 許可 150 ＋ 権限外 100 の隣接、`hops=1` → ノード 151・打ち切りなし |
| T-09P | **陽性対照**: 許可 250 → 打ち切りが立つ |
| T-10 | `hops=4` → 400 |
| T-10P | **陽性対照**: `hops=3` → 200、`hops` 省略 → 深さ 2 相当の結果 |
| T-11 | 対称辺を逆向きから辿れる／バックリンクが返る |
| T-12 | 打ち切り時の応答が 2 回実行で同一 |

🔴 **陽性対照（P 付き）を必ず対にする。** 否定形テストは、フィクスチャが壊れて「そもそも B に
到達し得ない」状態でも緑になる。陽性対照が無い否定形テストは**何も測っていない**。

## 変異試験の設計

### 前提となる手順（#908 と同じ）

1. 変異を入れる → 2. `git diff` で**当該箇所のみ**変化したことを生で読む → 3. `dotnet build` が
`Build succeeded` / EXIT=0 であることを読む → 4. **その後にはじめて**テスト結果を読む →
5. 逆変異で復元し、差分 0 行を確認する。

**ビルドが落ちる変異はテストの検出力を何も示さない。**

### 🔴 等価変異（equivalent mutant）の罠 —— 本 issue に固有の最重要点

**「prune を expand の後ろへ動かす」変異は、多くのグラフで結果集合が変わらない。**
非許可ノード X の先に**許可ノードが 1 つも無い**なら、早く刈っても遅く濾しても答えは同じである。
このとき変異は**等価変異**であり、テストが落ちないのは正常である。

> **ここで「落ちなかった＝実装が正しい」と読んではならない。** 正しい読みは
> **「このフィクスチャには検出力が無い」**である。両者を取り違えると、**橋の穴を素通しする
> テストを『変異試験で確認済み』と称して出荷する**ことになる。

したがって変異 M-A では、**変異版で結果が実際に変わることを先に確かめる**手順を踏む。

| 変異 | 内容 | 落ちるべきテスト | 非等価性の担保 |
| --- | --- | --- | --- |
| **M-A** | ⑤ の判定を ⑥ の後ろへ移す（＝展開してから濾す） | T-08 | フィクスチャを `A→X→B`・**A→B の直辺なし**・B への許可経路が 2 ホップ以内に存在しないものにする。この形なら変異版は B をノードに含める（辺は両端点条件で落ちるが**ノードとして現れる**）。**変異適用後に T-08 が落ちることを確認できなければ、フィクスチャを疑う**（実装ではなく） |
| **M-B** | ホップ上限 3 → 4 | T-10 | `hops=4` が成功してしまう。T-10P があるので「全部 400」で緑になる形にはならない |
| **M-C** | ホップ超過を 400 ではなく 3 へ切り詰める | T-10 | 同上。M-B と同じテストが捕まえるが**失敗の形が違う**（M-B は深さ 4 の結果、M-C は深さ 3 の結果）ので、両方を実測する |
| **M-D** | ⑥ の計数を ⑤ の前へ移す（権限外を数える） | T-09 | 許可 150 ＋ 権限外 100 = 250 > 200 で打ち切りが立つ。T-09P が上限自体は働くことを示す |
| **M-E** | 打ち切り順序を非決定にする（`OrderBy` を外す） | T-12 | 2 回実行の応答一致が壊れる。**ただし小さなフィクスチャでは偶然一致し得る**ため、上限を跨ぐ件数のフィクスチャで測る |

**M-E も等価変異になりやすい。** DB が偶然同じ順序を返せば緑になる。落ちなかった場合は
「順序保証がある」ではなく「**この規模では差が出ない**」と記録し、件数を増やして測り直す。

## 計画書との差異

- 差異: なし（現時点）。#908 で記録した既知の未強制（owner に基づく判定・個人資料の境界。#516）は本 issue でも解消しない。

## 未決事項

1. ホップ展開結果のキャッシュ —— ADR-0034 が実装ガイドへ送った未決事項。**本 issue では導入しない**。導入する場合はキャッシュキーに subject を含めること（ADR-0036 D-14）
2. 述語を SQL（jsonb）へ押し込む最適化 —— 意味論一致の検証が難しくなるため初期実装では採らない。実データ規模の実測後に判断する
