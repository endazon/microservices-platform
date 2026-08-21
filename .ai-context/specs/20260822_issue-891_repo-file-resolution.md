---
title: 作業仕様書 — リポジトリ根を辿るファイル解決を 1 箇所へ集約する（#891）
type: spec
status: done
related_ids:
  - UC-04
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0027（メッセージング基盤。#890 が 6 件目を足した経緯）"
issue: "#891"
---

# 作業仕様書: リポジトリ根を辿るファイル解決の集約（#891）

## 起点

- 実装 issue: `#891`（`#890` / U0d の AI レビュー指摘から起票）
- 関連: `#455` / `#441`

## 着手前の実測（母集合を自分で引いた。規則 9）

```
git grep -n "new DirectoryInfo(AppContext.BaseDirectory)" -- 'src/**/*.cs' ':!*/obj/*'
→ 6 件
```

| # | ファイル | 関数 | 返り値 | chart 前置 | 未解決時 |
| --- | --- | --- | --- | --- | --- |
| 1 | `Deployment/DataSourceSyncWiringTests.cs:68` | `ReadRepoFile` | 内容 | 呼び出し側 | `FileNotFoundException` |
| 2 | `Deployment/HpaPdbScalingTests.cs:88` | `ReadChartFile` | 内容 | **内包** | `FileNotFoundException` |
| 3 | `Deployment/MeshMtlsTests.cs:70` | `ReadHelmFile` | 内容 | **内包** | `FileNotFoundException` |
| 4 | `Deployment/NetworkIsolationTests.cs:155` | `ResolveRepoFile` | **パス** | なし | `FileNotFoundException` |
| 5 | `Deployment/PipelineDeclarationMountTests.cs:52` | `ReadRepoFile` | 内容 | なし | `FileNotFoundException` |
| 6 | `Fixtures/IntegrationTestFactory.cs:171` | `FindRepoFile`（protected） | **パス** | なし | `FileNotFoundException`（長い理由つき） |

**加えて 7 件目がある** —— `IntegrationTestFactory.cs:153` の `FindRepoFileForTests` は
`FindRepoFile` をそのまま呼ぶ**公開ラッパ**である（`QueueOverrideFanOutTests.cs:175` が使う）。
issue 本文は 6 件としか書いていないが、集約すればこのラッパも不要になる。

## 🔴 issue 本文の記述を 2 点、実測で訂正する

### 訂正 1: 「fail 時の挙動が揃っていない」は成り立たない

issue 本文は次のように書く。

> **特に fail 時の挙動が揃っていない**:
> - `FindRepoFile`（#890）: 見つからなければ `FileNotFoundException` で**止める**（fail-closed）
> - 他の 5 件: 用途が YAML テキストの静的検査であり、要求される厳しさが違う

**6 箇所すべてが `FileNotFoundException` を投げる**（上表。全件を目視した）。
揃っていないのは **返り値の型**（パス 2 / 内容 4）・**chart 前置を内包するか**・
**メッセージの詳しさ**であって、**fail するかどうかではない**。

したがって issue の「やること 4（fail 時の挙動を明示的に決める）」は、
**選択ではなく記述の是正**になる。集約後も `FileNotFoundException` で統一する ——
**既に全員がそうしているので、変更ではない。**

### 訂正 2: 統合テストは 46 件ではなく **47 件**

```
$ dotnet test Knowledge.IntegrationTests.csproj
Passed! - Failed: 0, Passed: 21, Skipped: 26, Total: 47
```

受け入れ基準は「1 件も減らない」なので、**47 を基準に測る**。
なお **26 件の skip は Docker デーモンが無い環境要因**であり、本作業とは無関係である
（CI では立つ）。**skip を「減った」と読み違えないこと。**

## 設計

`Fixtures/RepoFile.cs`（`internal static`）へ 1 実装を置く。

- `Find(string relative) -> string` —— パスを返す。未解決は `FileNotFoundException`
- `Read(string relative) -> string` —— `File.ReadAllText(Find(relative))`
- `ReadChart(string relative) -> string` —— `deploy/helm/microservices-platform/` を前置して `Read`
  （#2 と #3 が同じ前置を独立に持っているため、**前置も 1 箇所へ畳む**）

`IntegrationTestFactory.FindRepoFile` / `FindRepoFileForTests` は**両方とも撤去**し、
呼び出し側を `RepoFile.Find` へ差し替える。

🔴 **`#890` が書いた長い理由（`AddPlatformPipelineConfig` はパス未解決を黙って無視するので
ここで止める必要がある）は共通メッセージへ混ぜない。** それは `Pipeline:ConfigPath` 固有の
理由であり、`deploy/` の YAML を読む 5 箇所には当てはまらない。**理由は呼び出し側に残す。**

## 受け入れ基準

1. `git grep -c "new DirectoryInfo(AppContext.BaseDirectory)"` が **1**
2. 統合テストが **Total 47・Failed 0**（**1 件も減らない**）
3. `FindRepoFileForTests` が消え、呼び出し側が `RepoFile` を直接使う
4. 未解決パスで `FileNotFoundException` が飛ぶ（**集約後の挙動を実測する**）
5. `dotnet build|test|format` 両ユニットが通る
6. 検査器がすべて EXIT=0

## 変異試験

| # | 変異 | 期待 |
| --- | --- | --- |
| A | 存在しない相対パスを `RepoFile.Find` へ渡す | `FileNotFoundException`（型とメッセージを assert） |
| B | `ReadChart` の前置を落とす | chart を読む試験が落ちる |

**変異が当たったことを先に確認してから判定する。**

## 実装後に確定した結果

| 項目 | 実測 |
| --- | --- |
| `new DirectoryInfo(AppContext.BaseDirectory)` | **6 → 1**（`Fixtures/RepoFile.cs:28` のみ） |
| 撤去した重複 | 5 テストクラスのローカル実装 ＋ `FindRepoFile` ＋ `FindRepoFileForTests`（**計 7**） |
| 統合テスト | **Total 47 → 55 / Failed 0**（`RepoFileTests` 8 件を追加。**既存は 1 件も減らない**） |
| 両ユニット build / test / format | **すべて EXIT=0** |

### 🔴 受け入れ基準 1 の測り方を 1 度間違えた

`git grep` で数えたところ **0 件**と出た。**期待は 1 である。**
`git grep` は**追跡下のファイルしか見ない**ため、新規作成した `RepoFile.cs` が映らなかった。

**0 件を「集約できた」と読まなくてよかった** —— 期待値が 1 だったので食い違いに気づけた。
**もし期待値が 0 の検査だったら、この誤りは「成功」に見えていた。**
`git add` 後の `git grep --cached` と、`grep -rn` によるファイルシステム走査の**2 通り**で
1 件を確認し直した。

## 変異試験の実測

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| A1 | 存在しない相対パスを `Find` へ | `FileNotFoundException` | ✅ メッセージと `FileName` を確認 |
| A2 | 空白のみの相対パス | `ArgumentException` | ✅ |
| A3 | `Read` の未解決 | `FileNotFoundException` | ✅ |
| A4 | `ReadChart` の未解決 | `FileNotFoundException`（**前置つきパス**） | ✅ `deploy/helm/microservices-platform/…` |
| B | `ReadChart` の前置を落とす | chart を読む試験が落ちる | ✅ **Build succeeded のうえで 3 クラス 10 件**が Failed（`MeshMtlsTests` 4 / `HpaPdbScalingTests` 4 / `DataSourceSyncWiringTests` 2） |

**変異 B はビルド成功を先に確認してから判定した**（ビルドが落ちては検出力を証明できない）。
**復旧は `cmp` でバイト一致を確認した。**

### 🔴 ［2026-08-22 追記 / #898］変異 B の母集合を `head -6` で切って報告していた

当初この表には **「`MeshMtlsTests` / `DataSourceSyncWiringTests` が Failed」**（2 クラス）と書いた。
**実際は 3 クラス 10 件**である。`HpaPdbScalingTests` の 4 件が抜けていた。

原因は判定に使ったコマンドである。

```
dotnet test ... | grep -E "^  Failed |Passed!|Failed!" | head -6      ← head -6 で切っていた
```

`head -6` が 7 行目以降を落とし、**`HpaPdbScalingTests` の 4 件が表示されなかった**。
出力の最後にある `Failed! - Failed: 10` の行は読めていたのに、**10 という数と、
自分が挙げた 2 クラス（6 件）が合わないことに気づかなかった。**

🔴 **これは `traceability.md` 規則 7 が名指しで禁じている形そのものである** ——
「走査の出力を加工して読まない。`head` で切る・`sed` で潰すのいずれも
**見なかった行を見たことにする**同じ事故である」。**同じ規則の違反が本リポジトリで 3 度目**
（planning#317 の `head` 切り／planning#318 の `sed` 潰し／本件）。

切らずに測り直した結果:

```
$ dotnet test ... | grep -E "^  Failed " | sed 's/.*Deployment\.\([A-Za-z]*\)\..*/\1/' | sort | uniq -c
      2 DataSourceSyncWiringTests
      4 HpaPdbScalingTests
      4 MeshMtlsTests
```

**結論（変異が検出される）は変わらないが、証跡として記録した母集合が誤っていた。**

## 🔴 ［2026-08-22 追記 / #898］レビューと監査の指摘 3 件を是正した

| # | 指摘 | 是正 |
| --- | --- | --- |
| 🟡 | **`RepoFile` の永続的な単体テストが無い。** 変異試験 A1〜A4・B は**一度きりのローカル実測**でありコミットされた回帰試験になっていない | `Fixtures/RepoFileTests.cs` を新設（**8 件**）。**この 8 件だけで**前置欠落と fail-open の両方を検出することを実測した（前置欠落 → Failed 2 / fail-open → Failed 6） |
| F2 | `RepoFile` のクラスコメントが「**挙動の変更ではない**」と無条件に書いていたが、`ArgumentException.ThrowIfNullOrWhiteSpace` は**集約前の 6 実装のいずれにも無かった** | 「未解決時については」と限定し、増えた 1 点を明記した |
| F3 | **診断メッセージの後退。** 集約前は「`AddPlatformPipelineConfig` が黙って返る」理由が**例外メッセージに載って**いたが、集約でソースコメントへ移した。**赤い CI ログを読む人が見るのは例外メッセージだけ**である | `Find(relative, because:)` で呼び出し側固有の理由を例外へ載せられるようにした |

**🟢 の 2 件**（本節の NFR 判断の記録・委譲メソッドの不統一）も下記のとおり対応した。

### 起点 ID の判断（無採番 `NFR` を採った根拠）

`.claude/rules/traceability.md` は無採番 `NFR` を許す条件 2（メタ作業）を採るとき
「**作業を始める前に計画の ID 列を見て判断する**」ことを求めている。

本作業は**テスト基盤の重複解消**であり、計画側 `02_requirements/` の非機能要件は
**稼働する製品**の要件である。**当たる番号は無い**と判断し、無理に近い番号を付けなかった
（付けると監査が「その NFR の実装」として数えてしまい、無採番より劣化する）。
**条件 2 に当たるため環流もしない。**

### 委譲メソッドの方針（1 箇所だけ直接呼びに見える件）

`DataSourceSyncWiringTests.cs` は `ReadChartFile`（委譲）と `RepoFile.Read`（直接）を
**両方**使っている。不統一に見えるが規則がある ——
**委譲を残すのは「意味を足す」場合だけ**（`ReadChartFile` / `ReadHelmFile` は chart 前置という
意味を足す）。**根からの素直な読み出しは `RepoFile.Read` を直接呼ぶ**（素通しの委譲を挟んでも
名前が増えるだけで何も足さない）。`values-local.yaml` は chart 配下に無いため後者である。
