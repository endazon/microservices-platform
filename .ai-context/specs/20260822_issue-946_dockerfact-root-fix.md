---
title: 作業仕様書 — FactAttribute 派生の skip 属性をやめ、xUnit1051 から隠れていた 81 箇所を移行する（#946 形 5 の根治）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - IADR-0231
  - IADR-0238
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "../adr/IADR-0231_xunit-v3-simultaneous-switch.md"
issue: "#946"
---

# 作業仕様書 — FactAttribute 派生の skip 属性をやめる（#946 形 5 の根治）

## 🔴 これは新しい決定ではない。[[IADR-0231]] 決定 3 の未完了分である

`IADR-0231` 決定 3 は **「動的スキップは `Assert.Skip*` に統一し、ソフトスキップを撲滅する」**と定めた。
リポジトリ内には既に **3 つの先例**がある:

| 箇所 | 形 |
| --- | --- |
| `PandocConversionServiceTests`（3 箇所） | `Assert.Skip` |
| `WolverineBrokerEdgeTests:149` | `Assert.Skip` |
| `BffDocumentWriteRoundtripBenchmark:155` | **`Assert.SkipUnless`** |

**属性で skip する 2 つのクラスだけが取り残されていた。** 本作業はその適用であり、
**新しいパターンの導入ではない**（＝新しい裁定を要さない）。

## 問題：`FactAttribute` 派生はアナライザから見えない

```csharp
public sealed class DockerFactAttribute : FactAttribute   // 26 メソッドで使用
public sealed class BrokerFactAttribute : FactAttribute   //  5 メソッドで使用
```

**xUnit1051 は `[Fact]` / `[Theory]` を持つメソッドの本体しか検査しない**（[[#946]]）。
派生属性は認識されないので、**31 メソッドの本体がまるごと未検査**だった。

`Knowledge.IntegrationTests` は `remaining: 0` / `migrated: true` を読みながら、
**81 箇所の未移行を抱えていた。** 「偽の完了」である。

### 実証（属性を変えるだけ）

```
[DockerFact] のまま            → error xUnit1051: 0
[DockerFact] → [Fact] に変更   → error xUnit1051: 78（一意）
＋ [BrokerFact] → [Fact]       → error xUnit1051: 81（一意）
```

**警告ではなく error として出る** —— 当プロジェクトは `migrated: true` なので
`WarningsAsErrors` が昇格させる。**つまり根治後、81 箇所はラチェットの下に入る。**
一回限りの返済ではなく、**以後守られる対象**になる。

🔴 **件数の訂正**: #946 で「48 件」と報告したが**実測は 81 件**だった。
48 はメソッド名の正規表現走査で、**複数行の呼び出しといくつかのメソッド名を取りこぼしていた**。
**コンパイラの数が権威である。**（今日 2 度目の "Counting is not verifying"。）

## 実施内容

### 1. 属性 → 静的ヘルパ ＋ `Assert.SkipUnless`

| 旧 | 新 |
| --- | --- |
| `DockerFactAttribute : FactAttribute` | `static class DockerRequired`（`SkipUnlessAvailable()`） |
| `BrokerFactAttribute : FactAttribute` | `static class BrokerRequired`（`SkipUnlessObtainable()`） |

各テストは `[Fact]` になり、**本体の先頭にガードを 1 行**入れた（26 + 5 = 31 箇所）。

```csharp
[Fact]
public async Task Foo()
{
    DockerRequired.SkipUnlessAvailable();
    …
}
```

**`[CallerFilePath]` / `[CallerLineNumber]` の配管を削除した。** あれは
**「派生属性だと skip / 失敗の報告が本ファイルの位置を指す」（xUnit3003）ためだけ**に在ったので、
**派生をやめると問題ごと消える。**

ファイル名も実体へ合わせた（`DockerRequiredFixture.cs` → `DockerRequired.cs` /
`BrokerFactAttribute.cs` → `BrokerRequired.cs`）。

### 2. 見えるようになった 81 箇所を移行

🔴 **メソッド名の集合では駆動できなかった。** 対象が
HTTP / EF Core（`SingleAsync` / `MigrateAsync` / `CountAsync` / `ToDictionaryAsync`）/
ストレージ（`PutTextAsync` / `GetBytesAsync`）/ ホスト（`StartAsync`）/ `Task.Delay` / `WaitAsync`
と多岐にわたるため、**コンパイラの `error xUnit1051` の位置情報を権威にして**挿入した。

## 🔴 途中で作り込んだ不具合（コンパイラが 14 件検出）

位置情報は**呼び出し式の先頭（レシーバ）**を指す。そこから「最初の `(`」を探す実装にしたところ、
**連鎖呼び出しで誤った括弧を掴んだ**:

```csharp
// 誤（GetRequiredService の括弧に入れてしまった）
scope.ServiceProvider.GetRequiredService<IBus>(<token>).Publish(evt);
// 正
scope.ServiceProvider.GetRequiredService<IBus>().Publish(evt, <token>);
```

同型が `GetService<IMigrator>().MigrateAsync(x)` に 3 件、
`probe.Recorder.Received(a, b).WaitAsync(t)` に 1 件。さらに
**同一文に 2 つの診断がある箇所ではタプルを作ってしまった**:

```csharp
// 誤（括弧式を呼び出しと誤認 → タプルになった）
await (await _client.GetAsync("/tags", <token>), <token>).Content.ReadFromJsonAsync<T>();
// 正
await (await _client.GetAsync("/tags", <token>)).Content.ReadFromJsonAsync<T>(<token>);
```

`CS1501` 12 件 ＋ `CS1061` 2 件。**7 行を個別に直して 0 エラーにした。**
**戻さず前へ直した**（81 件のうち 67 件は正しかったため）。

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| `FactAttribute` 派生がリポジトリから消える | ✅ クラス宣言としての派生 **0 件** |
| 81 箇所が移行済み | ✅ `-p:NoWarn=` 付き再測定で **0 件** |
| ビルドが通る | ✅ **0 エラー**（`CS1501` / `CS1061` はすべて解消） |
| **真の Skipped を保つ**（no-op Passed へ退化しない） | ✅ **変更前 31 合格 / 32 スキップ / 63 合計 → 変更後 まったく同一** |
| ガードが効いていることの実証 | ✅ M-SKIP（下記） |
| knowledge 全体 | ✅ **711 件・0 失敗**（`--filter "Category!=Integration"`） |
| 器の整合 | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験 M-SKIP：**「skip だから緑」と「通ったから緑」を区別する**

`Assert.SkipUnless` の条件を反転し、Docker が無い環境でテストを**実際に走らせた**。

| | 失敗 | 合格 | スキップ | 合計 |
| --- | ---: | ---: | ---: | ---: |
| 通常 | 0 | 31 | **32** | 63 |
| **条件を反転** | **24** | 33 | **6** | 63 |

**スキップが 32 → 6 に減り、24 件が実際に落ちた。**
ガードが**効いている**こと、そして**スキップされているテストは本当に Docker を要する**ことの実証である。
`IADR-0231` 決定 3 が警告する「走っていないのに Passed」ではない。

## `remaining` の意味について

本 PR の前後で `Knowledge.IntegrationTests` の `remaining` は **0 のまま**である。
**しかし意味が変わった** ——

- **前**: アナライザが 31 メソッドを見ていない状態での 0（**81 箇所の未移行を隠していた**）
- **後**: アナライザが**全メソッドを見た**うえでの 0

数値は動かないが、**同じ 0 の裏付けの強さがまったく違う。**
[[IADR-0238]] の `remainingMeasuredAt` を更新し、この経緯を baseline の `$comment` にも残す。

## 申し送り

- **形 5 は解消した。** #946 に残るのは**形 1〜4（ラムダ / ローカル関数 / private ヘルパ、実測 9 箇所）**で、
  これは**アナライザの設計から来るもの**である。**「直せる構成」と「射程外の盲点」を分けて扱うこと。**
- 🔴 **`FactAttribute` 派生を新設しない。** 新設すると、そのメソッド群が**静かに検査対象外**になる。
  skip 判定は `Assert.Skip*` ＋ 静的ヘルパで書く（本 PR の 2 クラスが手本）。
  **`BrokerFactAttribute` は本 PR の直前（#455 W3）に追加されたばかりだった** ——
  **放置すればこの形は増え続ける。**
