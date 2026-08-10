---
title: 作業仕様書 — テストの InMemory DB を固定名からクラス単位の一意名へ変え、並列競合を止める（#660）
type: work-spec
status: draft
related_ids:
  - NFR
  - IADR-0130
  - IADR-0159
  - IADR-0160
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# 作業仕様書: テスト DB の並列競合（#660）

## 起点

- **NFR**（品質・退行防止テスト基盤）
- 起点 issue: **#660**（出所は #656 / PR #659 の検証中に発火した）

## 母集合（自分でファイルから引いた）

### 軸 1: issue 番号で引く

```console
$ grep -rn '#660' --include=*.cs --include=*.ts --include=*.md --include=*.json \
    --exclude-dir={node_modules,.git,ai-stock-trading,planning} .
```

**0 件。** 引き継ぎは無い（起票したばかりだが、**引かずに「無い」と決めない**）。

### 軸 2: 計画書の現状

**計画はテストの実装方式を定めていない。** `02_requirements` の NFR にテスト分離の記述は無く、
本件は**実装側の判断に閉じる**（`CLAUDE.md`「テストは xUnit」以上の規定が無い）。
したがって**計画への環流も不要**である。**「無いこと」を確認したうえで書いている。**

### 軸 3: `UseInMemoryDatabase` の全数（**拡張子で絞らず、パスの除外だけで取る**）

```console
$ grep -rn 'UseInMemoryDatabase' \
    --exclude-dir={node_modules,.git,ai-stock-trading,planning,bin,obj} .
```

**19 箇所。** DB 名の与え方で 3 群に分かれる。

#### (a) **固定名を、アセンブリ内の複数クラスが共有している**（＝欠陥。**4 件**）

| サービス | DB 名 | ファクトリを使うテストクラス数 |
| --- | --- | --- |
| **AuthorizationService** | `"AuthzTest"` | **5** ← **実際に発火した** |
| **DocumentService** | `"DocumentTest"` | **7** |
| **WikiService** | `"WikiTest"` | **2** |
| **DataSourceService** | `"DataSourceTest"` | **7** |

いずれも `TestWebApplicationFactory` が `ReplaceDbContext<T>(services, "固定名")` を呼ぶ形で、
**`IClassFixture<TestWebApplicationFactory>` はクラスごとに別インスタンスを作るのに、DB は 1 つを共有する**。

#### (b) 固定名だが**単一クラスに閉じている**（潜在。現状は安全。**5 件**）

`ConversionService` の `"IntrospectionTest"`（1 クラス）／
`JsonbValueComparerContractTests` の `nameof(DictionaryJsonbComparers_HashIsContentBased_NotReference)`
（Authorization / Document / DataSource / Wiki の各アセンブリに 1 クラスずつ）／
`WikiService` の `nameof(PipelineRecomposeTests)`（1 クラス）。

**同じアセンブリに 2 クラス目が現れた瞬間に (a) と同じ欠陥になる。** 本 PR では**触らない**が申し送る。

#### (c) 既に一意（**先例が同一リポジトリ内に 2 例ある**）

| サービス | 形 |
| --- | --- |
| **FeedbackService** | `private readonly string _dbName = $"FeedbackTest_{Guid.NewGuid()}";` |
| **DashboardService** | `private readonly string _dbName = $"DashboardTest_{Guid.NewGuid()}";` |

コメントまで同一（「各テストクラスで DB を分離するための一意名（InMemory）」）。
そのほか `ConversionService` の `_dbName = Guid.NewGuid().ToString()`（2 クラス）、
`DocumentSyncConsumerTests` の `$"wiki-sync-{Guid.NewGuid()}"`、
`DocumentDeleteArchiveSyncTests` の `$"wiki-arch-{Guid.NewGuid()}"` 等も一意である。

> **★ 解はリポジトリ内に既にある。** 新しい作法を持ち込まず、**(c) の形へ揃える**。

## 再現の機構（実測）

1. DB 名が固定 → **プロセス内で同一の InMemory ストアを共有する**
2. `AuthorizationService.Api.Tests` に `[Collection]` も `DisableTestParallelization` も**無い**
   → **xUnit 既定＝テストクラスごとに並列**
3. `Validate_DoesNotPersistAnything` は `before` / `after` の **2 回**ポリシー数を読む
4. **その間に `AuthzManagementEndpointTests` が `POST /authz/policies` を呼ぶと落ちる**

```
Expected after!.Count to be 0 because dry-run は副作用を持たない, but found 1 (difference of 1).
```

**dry-run の副作用そのものは正しく無い。** 落ちているのは**他クラスの副作用を数えている**ためである。

## 判断

### 判断 1: **(c) の形へ揃える**（案 A）。並列は止めない（案 B を採らない）

`[Collection]` で直列化すれば競合は消えるが、**原因（共有）は残り、テスト時間だけ延びる**。
**分離が本筋**であり、しかも**同一リポジトリに先例が 2 例ある**。

### 判断 2: **4 件すべてを直す**（発火した 1 件だけにしない）

4 件は**同一の資源**（テストの DB 分離）であり、**同じ 1 行の形**である。
発火したのが `AuthzTest` だけなのは**クラス数と実行順の偶然**にすぎず、
`DocumentTest` は 7 クラス・`DataSourceTest` も 7 クラスが共有していて**より危ない**。
**1 件だけ直すと「残り 3 件は安全」と読める記録が残る。**

### 判断 3: (b) の 5 件は**触らない**が申し送る

単一クラスに閉じており**現状は安全**である。**予防で広げると差分が増え、レビューの焦点がぼける。**
「2 クラス目が現れたら (a) になる」ことを IADR と本仕様書に書く。

### 判断 4: **暗黙の依存が無いかを実測してから確定する**

分離すると「他クラスが作った状態」に暗黙依存していたテストが落ちうる。
**4 サービスすべてのテストを実走して確かめる。** 落ちたら、その依存自体が欠陥なので個別に直す。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | 確かめ方 |
| --- | --- | --- |
| 1 | `Validate_DoesNotPersistAnything` が落ちない | `dotnet test platform/backend/backend.slnx` を**連続 5 回** |
| 2 | 4 サービスのテストが全緑（**暗黙依存が無い**） | 両ユニットの全アセンブリ |
| 3 | 同型の共有が他に無い | 軸 3 の全数走査（本仕様書に記録済み） |
| 4 | **決定的な再現テスト**で回帰を止める | 下記 |

### ★ 「落ちるまで回す」では回帰テストにならない

競合は確率的なので、**再現を運に頼らない**。
**同じファクトリのインスタンスを 2 つ作り、一方で書き込み、他方から見えないことを主張する**
——これは決定的であり、DB 名を固定へ戻すと**必ず**落ちる。

**変異試験**: `_dbName` を固定文字列へ戻すと新テストが落ちることを実測する。

## 射程外

- **(b) の 5 件**（判断 3）。
- **`[Collection]` による直列化**（判断 1 で棄却）。
- **InMemory プロバイダ自体の限界**（一意インデックスを強制しない。#634 の作業仕様書が既に記録）。
