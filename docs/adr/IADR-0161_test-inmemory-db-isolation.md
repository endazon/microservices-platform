---
title: IADR-0161 テストの InMemory DB はテストクラスごとに一意名で分離する
type: impl-adr
status: Accepted
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

# IADR-0161: テストの InMemory DB をクラスごとに分離する（#660）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **NFR**（品質・退行防止テスト基盤）。実装 issue: **#660**
- 作業仕様書: [20260810_issue-660](../specs/20260810_issue-660_test-db-isolation.md)
- 出所: **#656 / PR #659 の検証中に発火した**（本 PR とは無関係の既存欠陥として切り分けた）

## コンテキストと課題

`AuthorizationService.Api.Tests.PolicyDryRunValidationTests.Validate_DoesNotPersistAnything` が
**確率的に**落ちていた。

```
Expected after!.Count to be 0 because dry-run は副作用を持たない, but found 1 (difference of 1).
```

**dry-run の副作用そのものは正しく無い。落ちていたのは他クラスの副作用を数えていたためである。**

| 要素 | 実測 |
| --- | --- |
| DB 名 | `ReplaceDbContext<AuthorizationDbContext>(services, **"AuthzTest"**)` の**固定文字列** |
| 並列 | `[Collection]` も `DisableTestParallelization` も**無い** ＝ xUnit 既定で**クラス並列** |
| 当該テスト | `before` / `after` の **2 回**ポリシー数を読む |
| 競合相手 | `AuthzManagementEndpointTests` が `POST /authz/policies` を複数回呼ぶ |

**`IClassFixture` はクラスごとに別インスタンスを作るのに、ストアだけがプロセス内で共有されていた。**

## 決定 1: **クラスごとに一意な DB 名**（並列は止めない）

```csharp
private readonly string _dbName = $"AuthzTest_{Guid.NewGuid()}";
```

`[Collection]` で直列化すれば競合は消えるが、**原因（共有）は残り、テスト時間だけ延びる**。

**この形はリポジトリ内に既に 2 例ある** —— `FeedbackService` / `DashboardService` の
`TestWebApplicationFactory` が**コメントまで同一の形**で持っている。**新しい作法を持ち込まず、それへ揃えた。**

## 決定 2: **同型 4 件すべてを直す**（発火した 1 件だけにしない）

`UseInMemoryDatabase` を全数走査（19 箇所）して 3 群へ分類した。
**アセンブリ内で複数クラスが固定名を共有しているのは 4 件**である:

| サービス | DB 名 | 共有するテストクラス数 |
| --- | --- | --- |
| AuthorizationService | `"AuthzTest"` | 5 ← **発火した** |
| **DocumentService** | `"DocumentTest"` | **7** |
| WikiService | `"WikiTest"` | 2 |
| **DataSourceService** | `"DataSourceTest"` | **7** |

**発火したのが `AuthzTest` だけなのはクラス数と実行順の偶然にすぎない。**
`DocumentTest` と `DataSourceTest` は 7 クラスが共有しており**より危ない**。
**1 件だけ直すと「残り 3 件は安全」と読める記録が残る。**

## 決定 3: 単一クラスに閉じた固定名 5 件は**触らない**（申し送る）

`ConversionService` の `"IntrospectionTest"`／各アセンブリの
`JsonbValueComparerContractTests`（`nameof(...)`）／`WikiService` の `nameof(PipelineRecomposeTests)`。
**いずれも 1 クラスに閉じており現状は安全**である。**予防で広げるとレビューの焦点がぼける。**

> **ただし「同じアセンブリに 2 クラス目が現れた瞬間に決定 2 の 4 件と同じ欠陥になる」** ——
> これは申し送りに残す。**機械検査は置いていない**（後述）。

## 決定 4: 回帰テストは**確率に頼らない**

競合は確率的なので、「落ちるまで回す」形の再現テストは**書かない** —— CI で不安定になるだけである。
代わりに**分離そのもの**を主張する:

| テスト | 主張 |
| --- | --- |
| `SeparateFactoryInstances_DoNotShareTheirStore` | ファクトリを 2 つ作り、一方の書き込みが**他方から見えない** |
| `SameFactoryInstance_SharesItsStore` | 同一インスタンス内では**従来どおり共有される**（分離しすぎていない側） |

**これは決定的である。** DB 名を固定へ戻すと**必ず**落ちる（下記）。

## 結果

### 検証

| 対象 | 結果 |
| --- | --- |
| `dotnet test platform/backend/backend.slnx` を**連続 5 回** | **5 回とも 68/68 緑** |
| 両ユニット全アセンブリ | 全緑（**暗黙依存は無かった**） |
| 変異: `_dbName` を固定文字列へ戻す | `SeparateFactoryInstances_DoNotShareTheirStore` が**必ず**落ちる |

**「他クラスが作った状態」に暗黙依存していたテストは 1 件も無かった** —— 分離しても
4 サービス（Authorization 66 / Document 101 / Wiki 39 / DataSource 117）が全緑である。
**これは事前には分からなかったので、実測してから確定させた**（作業仕様書 §判断 4）。

### 検出しないこと（正直に書く）

- **新しく固定名を書くことを止める検査器は置いていない。** 決定 3 の 5 件は現状安全だが、
  **同じアセンブリに 2 クラス目が現れれば同じ欠陥になる**。
  `CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**」——
  **本件は 1 回目である**（4 件を直したが、**事故として発火したのは 1 件**）。**記録に留める。**

  > **★ 「1 回目」は数えて確かめた（記憶で決めていない）。**
  > 決定 1 の先例（`FeedbackService` / `DashboardService`）が**過去の同型事故への是正**なら、
  > 本件は 2 回目になり検査器が要る。そこで**導入経緯を履歴から引いた**:
  >
  > ```console
  > $ git log --oneline -L '/_dbName/,+1:<factory>.cs'
  > === FeedbackService.Api.Tests ===
  > 5bff408 feat(FR-08): 回答へのフィードバック（👍/👎・コメント）収集 (#53)
  > === DashboardService.Api.Tests ===
  > 7b237ad feat(FR-10): 利用状況・検索傾向・回答品質ダッシュボード (#54)
  > ```
  >
  > **どちらも機能追加の初版から一意名で生まれており、事故を受けて直した形跡は無い。**
  > よって**先行する同型事故は 0 件**であり、#660 が 1 回目である。
  > （数え方は **「いま残っている乖離の数」ではなく「起きた事故の回数」** で取る。
  > PR #657 でこれを取り違えて自己訂正した経緯があるため、本件では**先に履歴を引いた**。）
- **InMemory プロバイダ自体の限界**は本 ADR の対象外。一意インデックスを強制しないことは
  #634 の作業仕様書が既に記録している。

## 申し送り

- **単一クラスに閉じた固定名 5 件**（決定 3）。2 クラス目が現れたら一意名へ変えること。
- **2 回目が起きたら検査器を足す** —— `ReplaceDbContext`/`UseInMemoryDatabase` へ
  リテラル文字列を渡している箇所を落とす形が考えられる。
