---
title: IADR-0260 DB 層の防壁は「宣言」ではなく「発火」と「カタログ」の両方で確認する
type: impl-adr
status: Accepted
related_ids:
  - FR-17
  - SC-09
  - ADR-0033
  - IADR-0027
  - IADR-0231
  - IADR-0232
  - IADR-0242
author: claude
created: 2026-08-23
updated: 2026-08-23
---

# IADR-0260 DB 層の防壁は「宣言」ではなく「発火」と「カタログ」の両方で確認する

> **本 IADR の番号は仮番号である**（#941 の並行作業のため統括側が最終採番する）。

## 状況

GraphService の辺の型辞書には**アプリ層と DB 層の 2 段の防壁**がある。DB 層は
`ON DELETE RESTRICT`（`edges` → `edge_types`）・`ux_edge_types_name`・`ux_edges` の 3 つで、
`GraphDbContext` と `InitialCreate` マイグレーションの双方に宣言がある。

**にもかかわらず、この 3 つは一度も発火していなかった**（#941）。#910 の変異試験 G-1 が、
アプリ層の事前カウントを外すと「RESTRICT に弾かれて 500」ではなく **204 NoContent で参照中の型が
黙って消える**ことを実測している。原因は `GraphService.Api.Tests` が EF InMemory を使っていること
であり、**InMemory プロバイダは一意索引も外部キーも強制しない**。

構造として一般化できる問題が 2 つある。

1. **「宣言されている」と「機能している」が同じ語で語られていた。** モデル定義に
   `OnDelete(DeleteBehavior.Restrict)` と書けば「RESTRICT がある」と読めてしまう。
2. **「張っていない外部キー」は書き込み試験では反証できない。** `ai_suggestions` → `edge_types` と
   `edges` → `graph_documents` に FK を張らないのは意図した設計だが（前者は ADR-0033 決定 9 より
   厳しい規則を勝手に作らないため、後者はイベント到着順への人工的依存を作らないため）、
   **後から誰かが張っても、書き込みを試すテストは緑のまま**である。

## 決定

**DB 層の防壁は次の 4 点を満たす形で確認する。**

1. **スキーマはマイグレーションから作る。`EnsureCreatedAsync` を使わない。**
   `EnsureCreated` はモデルから直接スキーマを作るため、**マイグレーションの出力を一切検査しない**。
   GraphService の `Program.cs` は起動時に `MigrateAsync` を実行するので、統合テストの器で
   ホストを起こすだけでスキーマはマイグレーション出力そのものになる。
   （`DocumentService` 側の既存テストは `EnsureCreatedAsync` を呼んでいる。**同じにしない。**）

2. **防壁は HTTP ではなく `DbContext` を直接叩いて発火させる。**
   アプリ層のガードを外す変異を入れて確かめると、試験できるのは「変異を入れた版」だけであり、
   **出荷される版の防壁は未発火のまま残る**。DbContext 直接なら、出荷される版のまま
   アプリ層の事前検査だけを迂回できる。

3. **スキーマそのものを PostgreSQL のカタログで突合する**
   （`pg_constraint.confdeltype` / `pg_indexes.indexdef` / `information_schema.columns`）。
   これが上の (2) の「張っていない外部キー」を固定する唯一の手段である。**FK の一覧を
   完全一致で突合する**ことで、「増えたこと」も「消えたこと」も同じ 1 本のアサーションが捕まえる。
   一意索引は名前の実在だけでなく **`indexdef` の列並び**まで見る —— 列が 1 つ欠けても
   「UNIQUE 索引 `ux_edges` は在る」は成り立ってしまうためである。

4. **競合分岐（一意制約違反 → 409）は、決定的に再現する。**
   `Task.WhenAll` の同時投入は一意制約側では十分に決定的だが、削除の `RESTRICT` → 409 分岐は
   同時実行では踏めない。**別接続の未コミットトランザクションで辺を挿入しておく**と、
   (a) 削除要求の事前カウントは未コミット行を見ないのでガードを通過し、
   (b) `DELETE FROM edge_types` は親行のロック待ちでブロックし、
   (c) こちらが commit した時点で外部キー違反になる、
   という順序が PostgreSQL の MVCC と行ロックによって保証される。

## 実走の確認手順（本決定の要）

🔴 **`DockerRequired.SkipUnlessAvailable()`（[IADR-0231] 決定 3 の適用）は Docker が無ければ
skip する。「CI が緑だった」は「テストが実行された」の証拠にならない。**

- PR では走らない。`ci.yml` は `--filter "Category!=Integration"` で除外し、回収先は
  `integration.yml`（develop への push ＋ 日次 ＋ 手動）である（[IADR-0232] 決定 3）。
  **したがって本 PR がマージされるまで、この 6 件は 1 度も実行されない。**
- 確認は `integration.yml` の実行ログの**生の出力**で行う。見るのは 3 点:
  1. 対象 6 件が `Passed` として現れていること（skip 件数が `0` に変わっていること）
  2. `Knowledge.IntegrationTests` の合計実行件数が **6 件増えていること**
  3. `check-coverage-floor` の「出現レポート数の内訳」が **16 件のまま**であること
     （増えていたらテストプロジェクトが増えた／二重実行のいずれかである）
- **緑・0 件・skip はいずれも「測った証拠」にならない。**

## 影響

- `Knowledge.IntegrationTests` が `GraphService.Api` を参照するようになる。`Program` 型の衝突
  （CS0433）は `GraphServiceTestMarker`（[IADR-0027] の作法）で避ける。
- ユニット依存の向きは変わらない（knowledge → knowledge）。`check-unit-dependencies.js` で確認済み。
- 本決定は **GraphService に限らない**。実 DB の制約に依存する防壁を持つサービス
  （`DocumentService` のタグ辞書ほか）へ同じ形を横展開してよい。ただし
  **同型の事故が 2 回起きるまで検査器は足さない**（規約の追加条件）。

## 却下した案

- **アプリ層のガードを外す変異版で確かめる。** 出荷される版の防壁が未発火のまま残るので、
  #941 が問うている「機能したことの確認」にならない。
- **`EnsureCreatedAsync` で作った実 PostgreSQL で確かめる。** モデルの宣言は確認できるが、
  **マイグレーションが同じものを出力しているかは確認できない**。#941 が名指しで挙げている
  未確認事項がそのまま残る。
- **カタログ突合だけで済ませる。** 制約の存在は測れるが、**その制約が書き込みを実際に拒む
  ことは測れない**（例: `DEFERRABLE` や無効化された制約はカタログには現れる）。
  発火とカタログは互いの死角を埋めるので、**両方置く**。
