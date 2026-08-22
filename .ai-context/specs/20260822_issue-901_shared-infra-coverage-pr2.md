---
title: 作業仕様書 — Platform.Shared.Infrastructure の被覆向上 第 2 PR（Observability / ObjectStorage）（#901）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0006
  - IADR-0233
  - IADR-0236
  - IADR-0244
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0006（可観測性・相関 ID）"
  - "ADR-0014 / ADR-0015（オブジェクトストレージ）"
issue: "#901"
---

# 作業仕様書: Platform.Shared.Infrastructure の被覆向上 第 2 PR（#901）

## 起点

- 実装 issue: `#901`。第 1 PR は `#931`（`79fa6841`）で着地済み
- 優先順位は「依存の広さ × 壊れたときの静かさ」。本 PR は**優先 3**（`AddPlatformObservability`）と
  **優先 5**（`AddPlatformObjectStorage`）
- **優先 4（Introspection の運搬経路）は Wolverine チェーンの C3 の後**のため含めない

## 対象

| ファイル | 行数 | 本番 call site |
| --- | ---: | ---: |
| `Foundation/Extensions/ObservabilityExtensions.cs` | 74 | `AddPlatformObservability` **13** / `AddPlatformLogging` **13** |
| `Composable/Adapters/Storage/ObjectStorageExtensions.cs` | 53 | `AddPlatformObjectStorage` **5** |

**同一 PR に含めた理由**: どちらも `IServiceCollection` 拡張で試験の器を共有でき、
ObjectStorage 側は分岐 1 本の観測で足りて安価だから。分けると PR の往復が増えるだけである。

## 🔴 設計の中心: 「何が観測可能か」を先に実測した

`AddPlatformObservability` は**壊れても何一つ落ちない**。したがって
「テストをどう書くか」ではなく「**何が観測できるか**」から決めた。実測結果と、
そこから導いた決定は [[IADR-0244]] が正本。要点:

| 面 | 観測可否 |
| --- | --- |
| トレース / メトリクス / **ログ** のリソース属性 | **可**（各プロバイダの `GetResource()`） |
| `IncludeScopes` / `IncludeFormattedMessage` / `ParseStateValues` | **可** |
| **OTLP 送信先** | 🔴 **不可**（3 経路で確認） |

🔴 **OTLP 送信先が観測不能であることは 3 経路で確かめた** —— 名前総当り（どの名前も既定値）／
`IConfigureOptions<OtlpExporterOptions>` の登録数 0 ／ `TracerProviderSdk` の
リフレクション走査で `Uri` を持つ Otlp 型が見つからず。
**1 経路で見つからないのは「その形では見つからない」でしかない。**

### 製品コードへ試験用シームを入れた（第 1 PR と違う点）

第 1 PR は製品コードに触れなかったが、本 PR は `OtlpEndpointOf` を
`private static` → **`internal static`** にし、`InternalsVisibleTo` を足した。
**理由と、それでも残る観測不能領域**は [[IADR-0244]] 決定 3 と「検出しないこと」に書いた。
要約すると —— シームが無いと OTLP 送信先は**変異を当てられない対象として丸ごと残る**。
シームを入れても「導出結果が exporter へ届いているか」は**依然として証明できない**。

## 試験（15 本）

| # | 対象 | 内容 |
| --- | --- | --- |
| 1 | Observability | トレースのリソースに `service.name` / `service.version` が載る |
| 2 | 同上 | **メトリクスのリソースがトレースと完全一致** |
| 3 | 同上 | **ログのリソースがトレースと完全一致** |
| 4 | 同上 | `IncludeScopes` / `IncludeFormattedMessage` / `ParseStateValues` が真 |
| 5 | 同上 | OTLP 送信先が設定値を尊重する（シーム経由） |
| 6 | 同上 | OTLP 送信先が設定なしで既定値へ落ちる（シーム経由） |
| 7 | 同上 | **適用前は各プロバイダが解決できない**（U4 の作法。対照） |
| 8–9 | ObjectStorage | 資格情報が揃えば実クライアント / 無ければ縮退クライアント |
| 10–14 | 同上 | `IsConfigured` の AND 3 条件を 1 つずつ欠く（`Theory` 5 ケース） |
| 15 | 同上 | 設定オブジェクトが登録され既定値を持つ |

## 実装後に確定した結果

`dotnet test` は 47 → **62 件**（+15）。**変異 10 種すべてが当たった。**

| 変異 | 内容 | 落ちたテスト |
| --- | --- | --- |
| O1 | `IncludeScopes` → `false` | `ログのスコープ取り込みが有効である…` |
| O2 | `ParseStateValues` → `false` | 同上 |
| O3 | `IncludeFormattedMessage` → `false` | 同上 |
| O4 | メトリクスの `SetResourceBuilder` を落とす | `メトリクスのリソースがトレースと一致する` |
| O5 | ログの `SetResourceBuilder` を落とす | `ログのリソースがトレースと一致する` |
| O6 | 既定 OTLP 先を書き換える | `OTLP送信先は設定が無ければ既定値へ落ちる` |
| O7 | 設定キー名を変える | `OTLP送信先は設定値を尊重する` |
| O8 | サービスバージョンを変える | `トレースのリソースにサービス名とバージョンが載る` |
| S1 | `IsConfigured` → 常に真 | `設定が無ければ縮退クライアントを登録する` |
| S2 | `IsConfigured` → 常に偽 | `資格情報が揃っていれば実クライアントを登録する` |

各変異で **`BUILD EXIT=0`** を確認した（コンパイルエラーで落ちたのでは当たったことにならない）。
変異解除後は製品コードの差分がシームの 2 箇所のみに戻り、62 件が再び全通する。

### 🔴 O1〜O3 は同一テストが落ちるが、どのフラグかは失敗本文で特定できる

3 つのフラグを 1 つのテストで見ているため落ちるテスト名は同じだが、
**assert のメッセージをフラグごとに分けてある**ので本文で判別できる。O1 の実測:

```
Expected options.IncludeScopes to be True because false だと CorrelationIdMiddleware の
BeginScope が LogRecord から消える。ログは出続けるので気付けない, but found False.
```

`ADR-0006` が静かに成立しなくなる退行を、名指しで捕まえている。

## 🔴 手順の逸脱を 1 件記録する

CLAUDE.md は「**仕様書なしで実装へ着手しない**」と定めているが、本 PR は
**事前実測（観測可能面の探索）→ 実装 → 本仕様書**の順になった。
第 1 PR の仕様書が `#901` 全体の方針を持っており、その延長と扱ってしまったためである。
**設計の中心（何が観測可能か）は実測してから決めており、行き当たりで実装したわけではない**が、
順序としては逸脱である。次の PR は着手前に仕様書を置く。

## 検出しないこと

[[IADR-0244]]「検出しないこと」を正とする。要約:
`OtlpEndpointOf` の戻り値が exporter へ届いているか／収集器との疎通／
リソース属性の意味的な妥当性／計装の登録有無。

## 床

**引き上げない。** 第 1 PR と同じ理由で、増分が 1 ポイントに要る本数（line 約 73 行 /
branch 約 21 本）に届かないため。実効増分は `integration.yml` の実測でしか出ない
（重複排除が被覆を OR で畳むため、他レポートで既に被覆済みなら集計は動かない）。

## 検証環境

隔離 worktree `/c/wt901`。ローカル .NET SDK `10.0.301`。
