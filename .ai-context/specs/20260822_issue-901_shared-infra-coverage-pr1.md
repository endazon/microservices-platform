---
title: 作業仕様書 — Platform.Shared.Infrastructure の被覆向上 第 1 PR（HealthCheck / CommonService）（#901）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0233
  - IADR-0236
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0006（相関 ID とログ）"
  - "ADR-0030（バックエンドアプリケーション層標準）"
issue: "#901"
---

# 作業仕様書: Platform.Shared.Infrastructure の被覆向上 第 1 PR（#901）

## 起点

- 実装 issue: `#901`（`#900` の完了で順序固定が解けた）
- 前提: `#900` / [[IADR-0236]] がレポート跨ぎの重複排除を入れ、床を `line 88` / `branch 68` へ置き直した
- 関連: `#455` U4 / [[IADR-0233]]（同ユニットのテストプロジェクトを新設した回。作法を継承する）

## 対象（第 1 PR）

| ファイル | 行数 | 公開面 |
| --- | ---: | --- |
| `Foundation/Extensions/HealthCheckExtensions.cs` | 29 | `AddPlatformHealthChecks` / `MapPlatformHealthChecks` |
| `Foundation/Extensions/CommonServiceExtensions.cs` | 15 | `UsePlatformMiddleware` |

**計 44 行。** 選定理由（優先順位は「依存の広さ × 壊れたときの静かさ」）:

1. **`MapPlatformHealthChecks`**（本番 **12** 箇所）—— `/health/live` の唯一の消費者は k8s の probe。
   `Predicate = _ => false` が `_ => true` へ退行すれば**依存障害で全 pod が再起動ループ**、
   ready 側が `_ => false` へ退行すれば**未準備 pod へ常時トラフィック**。
   🔴 **どちらも HTTP は 200 を返し続け、ビルドも通る。最も静かで最も広い。**
2. **`UsePlatformMiddleware`**（本番 **11** 箇所）—— ミドルウェアの**順序**が散文以外で守られていない。
   `CorrelationIdMiddleware` が抜けると相関 ID の `BeginScope` が消え、`ADR-0006` が静かに成立しなくなる（ログは出続ける）。

**両方 `WebApplication` 拡張なので試験の器を共有できる。**

### 触らないもの

- `Foundation/Pipeline` と `Foundation/Introspection` —— **U5 / Wolverine 移行が触る**ため非重複を保つ
- `AddPlatformObservability`（優先 3）・Introspection の運搬経路 3 クラス（優先 4・**U5 の後**）・
  `AddPlatformObjectStorage`（優先 5）—— 後続 PR

## 🔴 事前実測: 変異 E は「当たる」

調査セッションの案では変異 E（`UseAuthentication()` / `UseAuthorization()` の順序入替）について
**「当たらない可能性がある。素通りするなら順序のテストは書かないこと」**とされていた。
**書く前に実測した。**

測定方法: `WebApplication.CreateBuilder()` ＋ `UseTestServer()` で常に認証成功する
テスト用スキームを登録し、`.RequireAuthorization()` を付けた `/secure` を叩く。
正順（Authn→Authz）と入替（Authz→Authn）の応答を比較した。

```
正順 (Authn→Authz) = 200 OK
入替 (Authz→Authn) = 401 Unauthorized
```

**差が出る。したがって順序のテストは書く**（「通るだけのテスト」にはならない）。
`UseAuthorization()` が先に走ると `HttpContext.User` が未設定のまま認可が評価され、
`Challenge` で 401 になるためである。

> **前提の限定**: この差は「**認可を要求するエンドポイントが存在するとき**」にのみ現れる。
> 認可不要のエンドポイントしか無い構成では順序を入れ替えても素通りする。
> よって試験は**認可を要求するエンドポイントを自分で建てて**測る。

## 設計

### 器（両ファイル共通）

`WebApplication.CreateBuilder()` ＋ `builder.WebHost.UseTestServer()` でプロセス内に建て、
`app.GetTestClient()` で叩く。`Microsoft.AspNetCore.Mvc.Testing` を試験プロジェクトへ追加する
（CPM に登録済み・`check-backend-libraries.js` EXIT=0 を確認済み）。

🔴 **リフレクションのみの試験は行被覆に 1 行も寄与しない。**
`PartialMigrationSafetyValveTests` の 3 件はすべてリフレクションで、被対象の実行行を通らない。
**本 PR は実 HTTP 応答で観測し、`MapPlatformHealthChecks` / `UsePlatformMiddleware` の実行行を通す。**

### 🔴 U4 の作法を継承する —— 「適用前の既定値」を先に assert する

`WolverineExtensionsTests.cs` 冒頭の規範に従う。
`MapPlatformHealthChecks` はまさにこの型である ——
**`/health/live` は「ヘルスチェックを 1 つも登録しなければ、述語が何であっても 200」になる。**
適用後だけを見ると**ヘルパが何もしなくても緑**になり、変異が当たらない。

したがって **失敗するヘルスチェックを先に登録**し、次を**対で** assert する:

| 経路 | 期待 | 何を証明するか |
| --- | --- | --- |
| `/health/live` | **200** | `Predicate = _ => false` が効き、**失敗するチェックを無視している** |
| `/health/ready` | **503** | `Tags.Contains("ready")` が効き、**ready タグ付きの失敗を拾っている** |

**この対で初めて「述語が実際に効いている」ことが証明される**（片方だけなら器が壊れても気付けない）。

### 試験の構成

| # | 対象 | 内容 |
| --- | --- | --- |
| 1 | `MapPlatformHealthChecks` | 失敗する `ready` タグ付きチェックを登録 → `live` 200 / `ready` 503 |
| 2 | 同上 | チェックを 1 つも登録しない既定状態 → 両方 200（**適用前の既定値**。1 の対照） |
| 3 | 同上 | `ready` タグの**無い**失敗チェック → `ready` も 200（タグで選別していることの固定） |
| 4 | 同上 | 経路が `/health/live` と `/health/ready` の 2 本だけ登録される（別経路は 404） |
| 5 | `AddPlatformHealthChecks` | `IHealthChecksBuilder` を返し、`services` へ登録される |
| 6 | `UsePlatformMiddleware` | 相関 ID が応答に現れる（`CorrelationIdMiddleware` が入っている） |
| 7 | 同上 | 認可必須エンドポイントが認証済みで 200（**順序が正しい**） |

## 変異試験

| 変異 | 内容 | 期待 |
| --- | --- | --- |
| A | `/health/live` の `_ => false` → `_ => true` | 試験 1 が落ちる（`live` が 503 になる） |
| B | `/health/ready` の `Tags.Contains("ready")` → `_ => true` | 🔴 **`ready` 側だけが落ち `live` は 200 のまま**であることまで読む（両方落ちたら器が壊れただけ） |
| C | `MapHealthChecks("/health/ready")` → `/health/readyz` | 試験 4 が落ちる（404）。**A/B と別のテストが捕まえることを分けて確認する** |
| D | `UseMiddleware<CorrelationIdMiddleware>()` を削る | 試験 6 が落ちる |
| E | `UseAuthentication()` / `UseAuthorization()` の順序入替 | 試験 7 が落ちる（**事前実測で 200 → 401 の差を確認済み**） |

**変異が当たったことを先に assert する。落ちなかった変異はそれと分かるように報告する。**

## 🔴 守る制約

- **`using MassTransit;` を新規テストで書かない。** `check-backend-libraries.js` 規則 1 の残件 ratchet が fail する。
  **完全修飾名での回避も [[IADR-0233]] 決定 6 が「検査の回避であって遵守ではない」として却下済み。**
- `Foundation/Pipeline` / `Foundation/Introspection` に触れない（U5 との非重複）。
- **床は本 PR では引き上げない。** 引き上げは実測後に別途判断する。
  現在 `line 88` / `branch 68`、**branch の余裕は 0.66pt = 被覆分岐 13 本**しかない。
  🔴 **pt ではなく本数で判断する**（[[IADR-0236]] 決定 6b）。

## 受け入れ基準

1. 試験 1〜7 が通る
2. **変異 A〜E がすべて当たる**（当たらないものがあれば、その事実を報告し試験を足すか外す）
3. `dotnet build` / `dotnet test` が platform ユニットで通る（**EXIT はリダイレクトして読む**）
4. `check-backend-libraries.js` が EXIT=0（`MassTransit` の新規混入なし）
5. `Foundation/Pipeline` / `Foundation/Introspection` に差分が無い
6. 被覆の増分を実測し、床の引き上げ可否を**被覆行・被覆分岐の本数**で判断した記録を残す

## 検証環境

隔離 worktree `/c/wt901`（`origin/develop` から作成）。**共有作業ツリーは使わない** ——
`#900` の実装が共有ツリー経由で他 PR の squash に巻き込まれた事故（[[IADR-0236]] 冒頭の追記）を繰り返さない。
ローカルの .NET SDK は `10.0.301`。

## 実装後に確定した結果

### 試験と変異の対応（変異 A〜E は**すべて当たった**）

試験は 9 本（仕様の 7 本＋対照 2 本）。`dotnet test` は 38 → **47 件**合格。

| 変異 | 内容 | 変異が当たったことの assert | 落ちたテスト |
| --- | --- | --- | --- |
| A | live の述語 `_ => false` → `_ => true` | diff 1 file / 1 行 ＋ **BUILD EXIT=0** | 2 件: `失敗するreadyチェックが…`／`readyタグの無い失敗チェックは…` |
| B | ready の述語 `Tags.Contains("ready")` → `_ => true` | diff 1 file / 1 行 ＋ **BUILD EXIT=0** | 1 件: `readyタグの無い失敗チェックはready側でも拾われない` |
| C | 経路 `/health/ready` → `/health/readyz` | diff 1 file / 1 行 ＋ **BUILD EXIT=0** | 4 件（経路に触れる全試験） |
| D | `UseMiddleware<CorrelationIdMiddleware>()` を削る | diff 1 file / 1 行削除 ＋ **BUILD EXIT=0** | 2 件: `相関IDミドルウェアが…`／`要求に相関IDがあれば…` |
| E | `UseAuthentication` / `UseAuthorization` の順序入替 | diff 1 file / 1 行 ＋ **BUILD EXIT=0** | 1 件: `認可必須の経路が認証済みで200になる…` |

**BUILD EXIT=0 を毎回確かめている** —— コンパイルエラーで落ちたのでは「変異が当たった」ことにならない。
変異解除後は `git diff` が**空**で、47 件が再び全通することも確認した。

#### 🔴 B は「ready 側だけが落ち live は Passed のまま」まで読んだ

B で落ちたのは `HealthCheckExtensionsTests.cs:87`（`/health/ready` の assert）であり、
直前の `:86`（`/health/live` の assert）は**通っている**。

```
Expected (GetAsync(app, "/health/ready")) to be HttpStatusCode.OK {value: 200}
  because 述語が Tags.Contains("ready") ではなく _ => true へ退行していれば、ここで 503 になる,
  but found HttpStatusCode.ServiceUnavailable {value: 503}.
  at ... HealthCheckExtensionsTests.cs:line 87
```

**両方落ちていれば「器が壊れただけ」であり、述語の証明にならない。** 片側だけ落ちたので証明が成立している。

#### C を A / B と分けて確認した

C は**述語ではなく経路名**の退行である。C で落ちた 4 件には
`登録される経路はhealthliveとhealthreadyの2本だけである`（404 を見る試験）が含まれ、
**経路名の退行はこの試験が捕まえる**。A / B が捕まえるのは述語の退行であって経路名ではない。

### 床の実測（A/B。自分の追加なし / あり）

`Platform.Shared.Infrastructure.Tests` 単独レポート（ローカル・Debug）。

| | A（追加なし） | B（追加あり） | 差 |
| --- | --- | --- | --- |
| テスト合格 | 38 件 | **47 件** | +9 |
| line | 10.08%（103/1022） | **13.11%（134/1022）** | 被覆 **+31 行** |
| branch | 11.79%（33/280） | **12.5%（35/280）** | 被覆 **+2 本** |
| **分母 lines** | 1022 | **1022** | **0（不変）** |
| **分母 branches** | 280 | **280** | **0（不変）** |

🔴 **分母が動いていないことが最重要の確認である。** `Microsoft.AspNetCore.Mvc.Testing` を
試験プロジェクトへ足したが、**新しい assembly は分母へ入っていない**。
分母が増えていれば「増分の被覆率が全体平均を下回って床を押し下げる」経路が開く
（#450 が同じ測定で実際に踏んだ形）。本 PR にその経路は無い ——
**本 PR は製品コードを 1 行も足していない**ので、分子だけが増える。

> **注記**: 本測定の分母 1022 は、`#899` が記録した同プロジェクト単独レポートの `lines-valid 772` と
> 一致しない。ローカルは Debug、CI は Release であり、構成が違う。**本測定は増分の向きと
> 分母の不変性を見るためのものであり、CI の絶対値の代替ではない。**

### 床は引き上げない（本数で判断した）

現在の床は `line 88` / `branch 68`、分母は CI 集計で 7292 行 / 2087 分岐。
**pt ではなく本数で見る**（[[IADR-0236]] 決定 6b）。

| 指標 | 床を 1 上げるのに要る被覆増 | 本 PR の増分（上限） |
| --- | ---: | ---: |
| line | 約 **73 行**（7292 の 1%） | **+31 行** |
| branch | 約 **21 本**（2087 の 1%） | **+2 本** |

**いずれも 1 ポイント上げるに足りない。よって本 PR では床を引き上げない。**

🔴 **さらに、上の +31 / +2 は上限である。** 重複排除は被覆を **OR** で畳むため、
`MapPlatformHealthChecks` / `UsePlatformMiddleware` の行が**他のテストプロジェクトのレポートで
既に被覆されていれば、集計値は 1 行も動かない**。これらは各サービスが起動時に呼ぶ拡張であり、
**統合テストがアプリを起動する経路で既に通っている可能性が高い。**
集計での実効増分は `integration.yml` の実測でしか分からない。

### 変異 E の事前実測（再掲・仕様どおり書いた）

着手前の実測で `正順=200 / 入替=401` の差を確認したため、順序の試験を書いた。
実装後の変異 E でも 1 件が落ちており、**事前実測と実装後の変異が一致している**。

## 🔴 ［2026-08-22 追記 / #929］本番 call site の件数を引き直した

着手時に「`AddPlatformHealthChecks` 本番 11 / `UsePlatformMiddleware` 本番 10」と書いたが、
**`#929`（GraphService 新設・`3b3136ef`）が着地して利用側が 1 つ増えた**ため誤りになった。
`develop` を取り込んで**実測で引き直した**。

| 拡張 | 着手時 | 引き直し後 |
| --- | ---: | ---: |
| `AddPlatformHealthChecks` | 11 | **12** |
| `UsePlatformMiddleware` | 10 | **11** |

🔴 **引き直しの過程で自分の走査を 1 度間違えた（記録として残す）。**
`grep -v '/tests/' -v '/Tests/'` で試験を除いたつもりが、実際のパスは
`Platform.Shared.Infrastructure.Tests/` であり **`Tests` の直前が `/` ではなく `.`** のため
除外が効かず、自分の試験ファイル 7 件を本番 call site として数えて **19 / 13** という値を出した。
**生の出力を読んで気付いた。** 母集合の規則 4「行フィルタで絞らない。パスから引く」の実例である。
確定値は `--include='Program.cs'` で引き、`Program.cs` 以外に本番参照が無いことも別途確認した
（定義本体 2 件と自分の試験のみ）。

**この件数の変化は本 PR の内容に影響しない** —— 試験は拡張の**挙動**を固定するものであり、
利用側の数に依存しない。`develop` 取り込み後も 47 件全通（EXIT=0）を確認した。

### W4（Wolverine ブローカ readiness）との重なり

**12 の call site すべてが Wolverine / RabbitMQ を配線していない**（各サービス根を
`AddWolverine` / `UseRabbitMq` / `WolverineFx` で走査。GraphService を含めて 0 件）。
したがって W4 が `AddPlatformHealthChecks` の中で `ready` タグ付きチェックを登録すると、
**メッセージングを使っていない 12 サービス全部がブローカ停止時に `/health/ready` で 503 を返す**。
W4 は Wolverine を配線する場所で opt-in の拡張として足すのが妥当である（実測は下記）。
