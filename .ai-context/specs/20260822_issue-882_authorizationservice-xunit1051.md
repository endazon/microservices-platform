---
title: 作業仕様書 — AuthorizationService.Api.Tests を xUnit1051 から剥がす（実測 74 箇所・初の platform ユニット）（#882）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - IADR-0238
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "20260822_issue-882_wikiservice-xunit1051.md"
issue: "#882"
---

# 作業仕様書 — AuthorizationService.Api.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で次（**報告 74 箇所・6 ファイル**）。**初の platform ユニット**。
`Refs #882`（`Closes` は最後の 1 本だけ）。起点 ID の置き方は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の同名節が正本。

🔴 **platform ユニットのビルドには AST submodule の初期化が要る**
（`Platform.Bff` が `AiStockTrading` 名前空間を参照する）。worktree では
`git submodule update --init --reference <元クローン> src/ai-stock-trading` を先に済ませる。
**submodule の pin をコミットへ混入させないこと**（適用後に `git status` で 0 件を確認した）。

## 着手前の call site 読み

| ファイル | 件数 |
| --- | ---: |
| `AuthzManagementEndpointTests.cs` | 47 |
| `PolicyDryRunValidationTests.cs` | 16 |
| `TestDbIsolationTests.cs` | 5 |
| `AccessScopeContractTests.cs` | 2 |
| `HealthEndpointTests.cs` | 2 |
| `IntrospectionEndpointTests.cs` | 2 |

**全 74 箇所が HTTP クライアント呼び出し**である。内訳:
`PostAsJsonAsync` 30 / `ReadFromJsonAsync` 19（＋複数行 5）/ `DeleteAsync` 8 / `GetAsync` 5 /
`PutAsJsonAsync` 3 / `PatchAsJsonAsync` 2 / `SendAsync` 1 / `ReadAsStringAsync` 1。

- **同居するテストダブルは無い**（`public ... Task ...Async(` の宣言が 0 件）。
  #949 / #951 で問題になった「宣言に引数を足す」型の危険は無い
- **自ドメインのメソッドは無い**（`TransformAsync` は 5 箇所あるが**報告されていない**ので触らない）
- `PatchAsJsonAsync` は置換器の既定集合に無いので、**この回だけ**明示的に足した

### 🔴 レシーバを 1 件ずつ確かめた（#951 の教訓の適用）

#951 で `Any` の**出現数だけ**を数えて LINQ の `Any()` を壊した。今回は
**衝突し得る名前のレシーバを 1 件ずつ目で見た**:

```
.DeleteAsync(       → 8 件すべて Client.
.GetAsync(          → 5 件すべて Client. / factory.CreateClient(). / client.
.SendAsync(         → 1 件 Client.
.PutAsJsonAsync(    → 3 件すべて Client.
.PatchAsJsonAsync(  → 2 件すべて Client.
.ReadAsStringAsync( → 1 件 res.Content.
```

**LINQ / EF との衝突は無い。**

## 報告数と置換数が初めて完全一致した

置換 **74**、報告 **74**、ファイル別も一致。
これまで 4 回連続で差が出ていた（3 回は [[#946]] の盲点、1 回は自分の不具合）が、
本プロジェクトには**ラムダ・ローカル関数・private ヘルパの中の対象呼び出しが無かった**。
**差が出ないこと自体は「盲点が無い」ことの確認**であり、差が出たときと同じく意味がある。

## 🔴 変異試験 M-1 が一度空振りした（結果を読まずに済んだ経緯）

最初に `PolicyDryRunValidationTests.cs` の `ReadAsStringAsync` を戻そうとしたが、
**そのファイルに `ReadAsStringAsync` は無かった**（実際は `AccessScopeContractTests.cs`）。
置換前の件数 assert が `0` で落ち、**変異は 1 文字も当たらなかった**。

このとき後続のビルドは `EXIT=0` / `error xUnit1051: 0` を出したが、
**これは「再発してもビルドが落ちない」証拠ではない。変異が当たっていないので何も測れていない。**
正しいファイルでやり直し、`git diff --stat` で 1 行の変化を確認してから結果を読んだ。

**「変異が当たったことを先に assert する」規律が、そのまま偽の緑を防いだ事例である。**

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 74 箇所が移行済み | ✅ 再測定で**一覧から消えた**。platform 合計 **421 → 347**（ちょうど −74） |
| 置換の総数が説明できる | ✅ **74 = 報告 74**（差ゼロ）。先頭カンマの壊れた形は 0 件 |
| **再発したらビルドが落ちる** | ✅ M-1（`error xUnit1051` / exit 1）。**やり直した回の結果** |
| **テスト件数が減らない** | ✅ **68 → 68**（属性数も develop と一致: 69 / 69） |
| 他プロジェクトの残件が変わらない | ✅ 他 2 プロジェクト（＋後述）の実測が baseline と一致 |
| submodule pin を混入させていない | ✅ `git status` に `ai-stock-trading` が 0 件 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | 引数ゼロの `ReadAsStringAsync()` を戻す | **ビルド失敗** | **`error xUnit1051` 2 行 / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 74` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

### 検証

`dotnet build src/platform/backend/backend.slnx`（Release）→ **0 警告・0 エラー**／
`dotnet test` platform **582 件・0 失敗**（🔴 **`--filter "Category!=Integration"` 付き**）／
`scripts.test.js` **584 件 all passed**。

## 🔴 発見: `Platform.Shared.Infrastructure.Tests` の残件が 0 → 4 になっている

着手時の実測で、**据え置き中の `Platform.Shared.Infrastructure.Tests` に xUnit1051 が 4 件**現れた
（baseline の記録は `remaining: 0`）。Wolverine 移行チェーンが**新しいテストを追加した**ためである。

- **ゲートの判断が正しかったことの実証である。** [[IADR-0238]] 決定 4 で「移行チェーンが
  本プロジェクトへテストを追加中なので先に剥がさない」と据え置いたが、**実際に追加された**。
  先に `WarningsAsErrors` を入れていれば、並行 PR が自分の追加分で落ちていた
- **baseline の `remaining: 0` は現在の実態と食い違う。** 検査器は件数を見ない（[[IADR-0238]] の
  「検出しないこと」に明記）ので CI は落ちないが、**単一情報源としては不正確**である
- **本 PR では直さない**（1 PR = 1 プロジェクトの射程外で、他プロジェクトの行を触ると
  レビューの焦点がぼやける）。**別途 baseline の実測値を更新する**

## 申し送り

残件 **724 → 650**。移行済み 12 本。次は `DataSourceService.Api.Tests`（75。knowledge ユニット）。

- **platform ユニットは AST submodule の初期化が要る。** pin の混入に注意
- **`Platform.Shared.Infrastructure.Tests` の baseline 実測値の更新**（上記）
