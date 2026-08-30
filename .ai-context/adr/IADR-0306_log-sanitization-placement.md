---
title: IADR-0306 ログ偽造対策は「発生源で断つ」を第一とし、断てない口の sanitize を Shared.Infrastructure へ置く
type: impl-adr
status: Accepted
related_ids: [NFR, FR-03, FR-11, FR-15, FR-17, UC-10, ADR-0004, ADR-0024, ADR-0034, IADR-0216, IADR-0229, IADR-0282]
author: Claude
created: 2026-08-30
updated: 2026-08-30
related_specs:
  - ../specs/20260830_issue-1019_codeql-open-alerts.md
---

# IADR-0306: ログ偽造対策の置き場所と、発生源で断つことの優先

## 文脈

CodeQL の `cs/log-forging` が、本リポジトリの 2 箇所を指していた（#1019 の実測）。

| アラート | 場所 |
| --- | --- |
| #19 | `Platform.Shared.Infrastructure/Foundation/Audit/AuditLogger.cs:22` |
| #24 | `knowledge/.../RetrievalService/Features/Search/HybridSearchService.cs:123` |

本リポジトリには**同型の私有実装が既に 2 つ**ある —— `LlmRouter.Sanitize`（FR-11）と
`ToolInvocationService.SanitizeForLog`（ADR-0024）。どちらも制御文字を `_` へ置換する。
このまま各所へ足すと**4 つ目の複製**になる。

なお `LlmRouter` のアラート（#3 / #4）は 2026-07-06 に `fixed` になっており、
**手書きの置換実装が CodeQL のバリアとして現に機能した**ことは実測で分かっている。

## 決定

### 決定 1: 値域を閉じられる口は、**発生源で断つ**。sink の sanitize で誤魔化さない

#24（`HybridSearchService`）は、#1019 の起票者が「`SearchModes.Normalize` が値域を 3 定数へ
閉じているので偽陽性」と論じていた。**値域についての主張は正しい。だが結論は誤りだった。**

```csharp
// 従前
public static string Normalize(string? mode) =>
    IsValid(mode) ? mode!.ToLowerInvariant() : Hybrid;
```

`IsValid` が真のとき返るのは **`mode` から作った新しい文字列**であって、定数ではない。
**「値域が閉じている」ことと「実体が利用者入力由来でない」ことは別の主張**であり、
CodeQL が追っていたのは後者である。**後者は真だった。**

したがって直し方は許可リスト側の定数を返すことである。

```csharp
public static string Normalize(string? mode) =>
    All.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)) ?? Hybrid;
```

**観測可能な振る舞いは変わらない**（`IsValid` は `OrdinalIgnoreCase` の一致なので、妥当な入力の
`ToLowerInvariant()` は必ず当該定数と文字列等価）。変わるのは返す実体だけである。
同型の先例は `LlmRouter.ResolveModel`（「テイント源を選択結果に持ち込まない」）。

**この形にできる口では sink の sanitize を置かない。** 正規化後の値は許可リストの定数そのもので
あり、そこへ sanitize を足すのは「起こり得ないケースへの防御的実装」（CLAUDE.md 禁止事項）である。

`SearchSorts.Normalize` も同一構造なので併せて直した。片方だけ直すと理由が復元できない。

### 決定 2: 値域を閉じられない口の sanitize は `Platform.Shared.Infrastructure/Foundation/Logging/` へ置く

#19（`AuditLogger`）の 4 引数は**値域が閉じていない**。`subject` は利用者名（トークンのクレーム）、
`detail` は自由文である。ここは発生源で断てないので sink で sanitize する。

置き場所は **`Platform.Shared.Infrastructure`** とし、`LogSanitizer` を新設する。

**`Platform.Shared.Kernel` は選ばない。**

- Kernel は Result / Error と DDD 基底型の共有カーネル（IADR-0229）であり、`src/README.md`
  依存規則により **Domain からのみ参照される**。ここへ置くと Infrastructure → Kernel の辺を
  新設することになる。
- ログ整形は**出力の関心**であってドメインの関心ではない。
- Kernel は `PackageReference` を `CSharpFunctionalExtensions` 1 つに限る強い制約下にあり
  （ADR-0041 決定 3・`check-backend-libraries.js` の `SHARED_KERNEL_ALLOWED`）、
  性格の違うものを混ぜる場所ではない。

**`Platform.Shared.Infrastructure` を選ぶ理由。**

- **ユニット外参照が許された 3 プロジェクトの 1 つ**である（IADR-0117）。
- `AuditLogger` が既にそこに居る。
- `RetrievalService.csproj` は**既にこれを参照している** —— 新しい `ProjectReference` は 1 本も要らない。

### 決定 3: 4 引数すべてを通す（2 つだけ通さない）

`action` / `outcome` は現状すべて呼び出し側のリテラルである。それでも sanitize を通す。
**同じ 1 行に載る引数のうち一部だけを通す形にすると、後から引数を足した人が「どれを通すのか」を
復元できない。** 通すことの費用は無視できる。

## 非目標（意図的にやらないこと）

- **`LlmRouter.Sanitize` / `ToolInvocationService.SanitizeForLog` の `LogSanitizer` への移行。**
  どちらも現にアラートが出ておらず（#3 / #4 は fixed 済み）、本 PR の受け入れ基準に含まれない。
  1 issue = 1 PR（IADR-0116 規約 1）を守り、差分を読める大きさに保つ。
  **複製が 3 つ残ることは承知のうえで受容する。** 統合するなら別 issue で行う。
- **全ログ経路の走査と一括是正**。射程が違う。検査器の追加も行わない
  （「同型の事故が 2 回起きたら」の条件は、CodeQL 自身が検査器として機能しているため満たさない）。

## 決定 4: `cs/user-controlled-bypass`（#22 / #23）は**コードを変えない**

`GraphEndpoints.cs:94` の `hops` 検証を CodeQL が high で 2 件指している
（"This condition guards a sensitive action, but a user-provided value controls it."）。

🔴 **これは偽陽性であり、指摘に従うと実在の脆弱性ができる。**

- この分岐は要求を**拒否するだけ**である。通過した場合の認可（`ResolveAsync` →
  `AuthorizedNode.Authorize`）は**無条件に実行される**。利用者入力で認可を飛ばす経路は無い。
- 検証を認可の後ろへ動かすと、権限外・不存在は 404 / 可視の文書だけ 400 となり、
  **`hops=99` を投げるだけで文書の存在が判別できる**。ADR-0034 決定 2 の存在秘匿が壊れる
  （`GraphEndpointsSecrecyTests` が固定している性質）。

**この事情は既にコード上のコメント（89〜92 行目・108〜112 行目）に書かれており**、
書いた人は CodeQL がここを指すことを承知のうえで意図してこの順序にしている。

**抑制コメントも置かない。** #1019 の起票者が #18 について述べた理由がそのまま当てはまる ——
抑制を置くと、安全性が「検証が認可より前にある」ことに依存している事実が見えなくなる。

正しい終わらせ方は **Security タブで false positive として dismiss する**トリアージ判断である。
これはリポジトリのセキュリティ状態を変える操作なので、**人の裁定に委ねる**（#1019 で提案した）。

## 帰結

- 監査ログへの改行注入が塞がった（#19）。変異試験で 6 件が落ちることを確認済み。
- 検索モードの正規化が利用者入力の実体を下流へ渡さなくなった（#24）。同 5 件。
- `GraphEndpoints.cs` は 1 行も変えていない。**認可の順序は不変**である。
- `LogSanitizer` という置き場所ができたので、次に「値域を閉じられないログの口」が出たときは
  複製せず済む。**ただし決定 1 が先である** —— まず発生源で断てないかを問うこと。
