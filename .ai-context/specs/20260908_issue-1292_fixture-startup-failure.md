---
title: コンテナ起動の失敗を握り潰さず、原因を添えて落とす（#1292）
type: spec
status: done
related_ids: [NFR, ADR-0027, IADR-0231]
author: Claude（実装）
created: 2026-09-08
updated: 2026-09-08
---

# 仕様書: コンテナ起動の失敗を握り潰さず、原因を添えて落とす（#1292）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR（当たる番号が無い。[[IADR-0188]] 決定 1 の運用）
- 計画 ADR: ADR-0027（メッセージング。実ブローカ結合の手順 8）
- 実装 ADR: [[IADR-0231]] 決定 3（**動的スキップは `Assert.Skip*` に統一する**。
  「走っていないのに Passed」を撲滅した当の決定）
- issue: #1292

## 射程

🔴 **一過性のフレーク自体は再発していない。** develop の直近 3 回の非 cancelled 実行はすべて
`success` である（`0ae0fdbf` / `80b90916` / `e568c0eb`）。**本 PR はフレークを直すものではない。**

直すのは issue が「残る欠陥」として挙げた **器の側**である ——
**コンテナの起動失敗が skip にも明示的な失敗にもならず、`NullReferenceException` 系で出る。**

## 事象（issue の実測。run 33962157954）

```
Failed Knowledge.IntegrationTests.AuthorizationService.AbacScopeTests.CreatePolicy_ThenResolveScope_ReturnsFilters
Error Message: System.ArgumentNullException : Value cannot be null. (Parameter 'client')
```

失敗 5 件はすべて `_client` が null であることによる例外だった。連鎖はこうである。

| 段 | 何が起きるか | 位置 |
| --- | --- | --- |
| 1 | コンテナ起動が失敗する | `PostgresFixture.cs:51` / `RabbitMqFixture.cs:55` |
| 2 | **例外を握り潰し、理由を 1 行も残さない**（`catch { IsAvailable = false; }`） | 同上 |
| 3 | 試験クラスが `if (!postgres.IsAvailable) return;` で早期 return し、`_client` が `null!` のまま残る | 20 箇所 |
| 4 | 試験本体のガードは `DockerRequired.SkipUnlessAvailable()` だが、**CI では無条件に「Docker あり」を返す** | `DockerRequired.cs:30-31` |
| 5 | ⇒ **skip されず、null の `_client` に触って落ちる。しかも原因はログに 1 行も無い** | —— |

🔴 **握り潰しているので、なぜ起動に失敗したかは特定できない**（`ryuk` / `testcontainers.org` /
`16-alpine` / `Cannot connect to the Docker` はいずれもログに 0 件だった）。

## 母集合（自分で引き直した走査。規則 1〜10）

`git rev-parse --is-shallow-repository` = **`false`**。

| 軸 | 検索語 | 件数 | 内訳 |
| --- | --- | --- | --- |
| 1 | `IsAvailable` | 21 ファイル | fixture 4 / 試験クラス 17 |
| 2 | `catch`（`Fixtures/` 配下） | **13 箇所** | **起動の握り潰しは 2 箇所**（Postgres / RabbitMq）／**除外 11 箇所** |
| 3 | `if (!…IsAvailable…) return` | 20 箇所 | 試験クラスの早期 return |

**除外（理由つき。合計 11 箇所）**:

- `BrokerTcpGate.cs` の **4 箇所** —— **片付け（`Stop` / `Close`）と到達性の判定**であり、
  握り潰しが正しい（「既に停止していてよい」と注記済み）
- `RawDocumentFetchedEdge.cs` の **2 箇所** ＋ `WolverineBrokerEdge.cs` の **4 箇所** ——
  **メッセージ受信の待ち合わせ**であり、起動の成否ではない
- `DockerRequired.cs:41` の **1 箇所** —— **名前付きパイプへの接続可否そのもの**が判定であり、
  例外＝「Docker が無い」という情報を持っている（握り潰しではない）

⇒ **直すのは 2 箇所だけ**である。

★［2026-09-08 追記 / #1331 レビュー］🔴 **初稿の表は「12 箇所 / 除外 10」と書いていた。誤りである。**
数え直すと `\bcatch\b` に当たる行は **15** で、うち **2 行はコメント**
（`IntegrationTestFactory.cs:127` と、**本 PR が新設した `ContainerStartupFailure.cs:11`**）。
差し引き **13 箇所 / 除外 11**。判断（直すのは 2 箇所）は変わらないが、**数が違っていた** ——
`traceability.repo.md` 規則 7（導出値は走査ではなく数え直す）を破っていた。

🔴 **ここでも「記録が母集合を動かす」**（規則 8）——
**是正の理由を書いた私のコメントが走査に出る**。#1312 の allowlist とまったく同じ形である。

## 決定（実装方針）

### 決定 1: 起動の失敗を `StartupError` に記録する

`catch` を `catch (Exception ex)` にし、`IsAvailable = false` と併せて例外を保持する。
**理由を捨てない。**

### 決定 2: 🔴 **Docker があるのに起動できないなら、skip ではなく失敗にする。判定は 1 か所に置く**

```csharp
catch (Exception ex)
{
    IsAvailable = false;
    StartupError = ex;
    var fail = ContainerStartupFailure.ToThrow(
        nameof(PostgresFixture), "PostgreSQL", ExternalEndpointVariable,
        ex, DockerRequired.IsAvailable());
    if (fail is not null) throw fail;
}
```

🔴 **判定そのものは `ContainerStartupFailure` が 1 つだけ持ち、2 つの fixture はそれを呼ぶ。**
初稿は同じ `if` を両 fixture へ写していたが、それは
**PR #1330 が 5 巡かけて潰した「同じ規則を 2 か所に持つ」形そのもの**である
（片方だけ直した状態が作れる）。**副産物として判定が単体で試験できるようになった** ——
実際に起動を失敗させなくても、`dockerAvailable` を引数で与えれば倒し方を測れる。

- **Docker が無い環境**（`IsAvailable()` が偽）→ 従来どおり握り潰して skip へ倒す。**挙動は変わらない**
- **Docker がある環境**（CI を含む）→ **原因を添えて落とす**

🔴 **`DockerRequired.IsAvailable()` が CI で無条件に真を返すのは、ここでは欠陥ではなく前提である。**
CI は Docker がある前提で回っており（実測: 同 run で 84 件中 78 件が実際に走っている）、
**そこでコンテナが起きないのは skip すべき事情ではなく、報告すべき失敗である。**
[[IADR-0231]] 決定 3 の「走っていないのに Passed」を撲滅した向きと同じである。

### 決定 3: 試験クラス 20 箇所は**触らない**

早期 return の形は残す。**Docker がある環境ではそこへ到達しなくなる**（fixture が先に落ちる）ため、
`_client` が null のまま使われる経路は消える。
**20 箇所を書き換えるより、原因の側 2 箇所で断つほうが小さく、抜けが無い。**

### 決定 4: 外部エンドポイント経路は**変えない**

`PLATFORM_TEST_POSTGRES` / `PLATFORM_TEST_RABBITMQ` が設定されているときは
コンテナを起こさず `IsAvailable = true` にする既存の fail-closed（接続失敗はテストの失敗として出す）を
**そのまま据え置く**。本 PR が触るのは**コンテナ経路の catch だけ**である。

## テスト（受け入れ基準）

- [x] Given Docker が無い環境 / When fixture を初期化する / Then 従来どおり `IsAvailable = false` で例外は出ない
- [x] Given Docker がある環境 / When コンテナ起動が失敗する / Then **原因を内包した例外で落ちる**
- [x] Given 同上 / When 例外メッセージを読む / Then **どの fixture か**と**元の例外**が分かる
- [x] Given 起動が成功する / When 走らせる / Then 従来どおり `IsAvailable = true`（回帰なし）
- [x] 外部エンドポイント経路の挙動が変わらない
- [x] `dotnet test` の knowledge 全体が緑（件数を減らさない）

## 変異試験（実走した。実出力を記録する）

判定を `ContainerStartupFailure.ToThrow` へ 1 つに寄せたので、**そこを壊せば必ず赤になる**。

| # | 変異 | 意味 | 赤くなった試験 |
| --- | --- | --- | --- |
| **M1** | 常に `null` を返す | **従前の握り潰しへ戻す**（#1292 の欠陥そのもの） | **3 本** — `DockerIsAvailable_ButStartupFailed_...` ＋ `BothFixtures_ShareTheSameDecision` の 2 ケース |
| **M2** | 常に投げる | Docker の無い開発機で統合テストが **skip ではなく失敗**になる | **3 本** — `DockerIsNotAvailable_FallsBackToSkip` ＋ 同 2 ケース |
| **M3** | `cause` を捨てて投げる | **原因を握り潰す**（#1292 の核心が戻る） | **1 本** — `..._ProducesAnExceptionThatKeepsTheCause` |

🔴 **M1 と M2 は対である。** 片方だけだと縮退実装が通る ——
「常に null」は元の欠陥、「常に投げる」は開発機を壊す。**両方向を測って初めて挟める。**
**M3 は別の軸**である（倒す / 倒さないは正しいが、**理由を落とす**形）。

変異を戻したあと**試験が緑に戻ったこと**まで確認した（`合格 5 / 5`）。

## 試験件数の前後

`Knowledge.IntegrationTests` **85 → 90**（合格 41 → 46 / スキップ 44 は不変）。
knowledge ユニット全体は**失敗 0**、`dotnet format --verify-no-changes` 差分なし。

★［2026-09-08 追記 / #1331 レビュー］🔴 **「0 警告」は誤りだった。正しくは 4 件である。**

```console
$ dotnet build backend.slnx --no-incremental
  ObjectStorageRoundTripTests.cs(55,21|87,21|122,21): warning CS0618 'MinioBuilder.MinioBuilder()' は旧形式です
  IngestToSearchQdrantTests.cs(52,19):               warning CS0618 'QdrantBuilder.QdrantBuilder()' は旧形式です
    4 個の警告
```

**4 件とも本 PR が触っていないファイルの既存警告**であり、持ち込んだものではない。
しかし**「0 警告」と書いたのは私の測り方の誤り**である ——
🔴 **増分ビルドは警告を再出力しない。** 直前に同じ試験プロジェクトを個別ビルドしていたため、
続く `dotnet build backend.slnx` はその射影を再コンパイルせず、**サマリが `0 個の警告` になった**。

**規律**: **警告の件数を主張するときは `--no-incremental` で測る。**
温まったビルドの `0 個の警告` は「警告が無い」ではなく「**その射影を今回コンパイルしていない**」である。
#1324 の mtime の罠（変異を戻したのに古いバイナリが走っていた）と**同じ family** ——
**ビルドが「何もしなかった」ことを「問題が無かった」と読まない。**

🔴 **スキップ 44 件が動いていないことが重要である。** 本 PR は
「Docker が無い環境で skip される試験」を 1 件も増減させていない ——
**変えたのは「Docker があるのに起きなかったとき」の倒し方だけ**である。

## 🔴 実装中に判明したこと（予定に無かった事実）

### 2 つの fixture へ同じ規則を書いた直後に、それが本セッションで直し続けた形だと気づいた

最初の実装は `if (DockerRequired.IsAvailable()) throw new InvalidOperationException(…)` を
**両方の fixture へ写していた**。これは PR #1330 が 5 巡かけて潰した
「**同じ規則を 2 か所に持つ**」形そのものである（片方だけ直した状態が作れる）。

⇒ `ContainerStartupFailure` へ**判定を 1 つだけ**置き、両 fixture はそれを呼ぶだけにした。
**副産物として判定が単体で試験できるようになった** —— 実際に起動を失敗させなくても、
`dockerAvailable` を引数で与えれば倒し方を測れる。

## やらないこと

- **一過性のフレークそのものを追うこと**（再発していない。原因はログに残っていない）
- 試験クラス 20 箇所の早期 return を書き換えること（決定 3）
- `DockerRequired.IsAvailable()` の CI 分岐を変えること（決定 2 の前提であり、欠陥ではない）
- 外部エンドポイント経路の fail-closed を変えること（決定 4）
