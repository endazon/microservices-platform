---
title: 作業仕様書 — CodeQL のオープンアラートを引き直して塞ぐ（#1019・自リポジトリ分 4 件）
type: spec
status: done
related_ids:
  - NFR
  - FR-03
  - FR-05
  - FR-15
  - FR-17
  - UC-10
  - ADR-0004
  - ADR-0034
  - ADR-0018
  - IADR-0216
  - IADR-0242
  - IADR-0272
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_observability.md (FR-15 監査ログの出口)
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-authorization.md (存在秘匿・404 の一本化)
related_specs:
  - "20260829_issue-901_shared-infrastructure-coverage.md"
---

# 作業仕様書: CodeQL のオープンアラートを引き直して塞ぐ（#1019）

## 背景

#1019 は、PR #1009 の「**CodeQL の 4 件はすべて `develop` 側のアラートで、本 PR が作ったものは
1 件も無い**」という主張が検証しきれていないことを問題にしている。起票者は
「セッションからアラートを列挙する手段が無い」と書いており、`RetrievalService` を指すアラート
**#18** の帰趨が説明できないままレビュースレッドが未解決で残っている、としている。

🔴 **本作業は列挙手段があることを前提に、まず自分でアラートを引き直すところから始める。**
#1019 本文の表（#16 / #17 / #11 / #12 の 4 件）は**現時点では古い**。

## 実測 1 — オープンなアラートの全件（2026-08-30）

```console
$ gh api "repos/endazon/microservices-platform/code-scanning/alerts?state=open&per_page=50" \
    --jq '.[] | "#\(.number) \(.state) \(.rule.security_severity_level // .rule.severity) \(.rule.id) \(.most_recent_instance.location.path):\(.most_recent_instance.location.start_line)"'
#24 open medium cs/log-forging src/knowledge/backend/Services/RetrievalService/Features/Search/HybridSearchService.cs:123
#23 open high cs/user-controlled-bypass src/knowledge/backend/Services/GraphService/Features/Graph/GraphEndpoints.cs:94
#22 open high cs/user-controlled-bypass src/knowledge/backend/Services/GraphService/Features/Graph/GraphEndpoints.cs:94
#19 open medium cs/log-forging src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Audit/AuditLogger.cs:22
#12 open medium cs/log-forging src/ai-stock-trading/backend/Services/ReportService/src/ReportService.Infrastructure/Foundation/Adapters/HttpReportNarrativeDrafter.cs:55
#11 open medium cs/log-forging src/ai-stock-trading/backend/Services/ReportService/src/ReportService.Infrastructure/Foundation/Adapters/HttpReportNarrativeDrafter.cs:55
```

**オープンは 6 件で、うち 2 件（#11 / #12）は submodule `src/ai-stock-trading` 配下**である。
本リポジトリは submodule を編集しない（CLAUDE.md）。**#11 / #12 は射程外**で、AST#1015 が追う。

## 実測 2 — #16 / #17 / #18 の帰趨（state を外して全件を引く）

```console
$ gh api "repos/endazon/microservices-platform/code-scanning/alerts?state=&per_page=100" --jq '...'
#18 fixed fixed_at=2026-08-28T08:45:42Z cs/log-forging          .../RetrievalService/src/RetrievalService.Api/Foundation/Services/HybridSearchService.cs:123
#17 fixed fixed_at=2026-08-28T08:45:42Z cs/user-controlled-bypass .../GraphService/src/GraphService.Api/Foundation/Endpoints/GraphEndpoints.cs:95
#16 fixed fixed_at=2026-08-28T08:45:42Z cs/user-controlled-bypass .../GraphService/src/GraphService.Api/Foundation/Endpoints/GraphEndpoints.cs:95
```

🔴 **`fixed` は「直った」を意味していない。** 3 件とも **`dismissed_reason` は空**で、
**旧パス**（`Services/<Name>/src/<Name>.Api/...`）を指している。同じ日付
（2026-08-28）に **VSA 移送**（IADR-0282。`src/<Name>.Api/` を廃して `Features/` へ移す）が
着地しており、現行のオープン 3 件は**同じ規則・同じ内容を新パスで指している**。

| 旧（fixed） | 新（open） | 規則 |
| --- | --- | --- |
| #18 `RetrievalService.Api/Foundation/Services/HybridSearchService.cs:123` | **#24** `RetrievalService/Features/Search/HybridSearchService.cs:123` | `cs/log-forging` |
| #16 / #17 `GraphService.Api/Foundation/Endpoints/GraphEndpoints.cs:95` | **#22 / #23** `GraphService/Features/Graph/GraphEndpoints.cs:94` | `cs/user-controlled-bypass` |

**したがって #1019 の前提（#18 が未解決のまま残っている）は、番号としては古いが、
指摘の実体は生きている。** ファイルが動いたぶんだけ採番が振り直された。
**「#18 が fixed になった」ことを「対処済み」と読んではならない** —— これが本作業の一次的な発見である。

なお `fixed` になった理由は「旧パスのファイルが解析対象から消えたため」と**推定**できるが、
GitHub API は `fixed` の理由を返さない。**移送と同日・同時刻・dismissed 無し・内容一致という
状況証拠までが実測できる範囲**であり、それ以上は断定しない。

## 実測 3 — 各アラートのメッセージ

```console
$ gh api ".../alerts/22" --jq '{rule, msg}'
{"rule":"cs/user-controlled-bypass","msg":"This condition guards a sensitive action, but a user-provided value controls it."}
$ gh api ".../alerts/19" --jq '{rule, msg}'   → {"rule":"cs/log-forging","msg":"This log entry depends on a user-provided value."}
$ gh api ".../alerts/24" --jq '{rule, msg}'   → {"rule":"cs/log-forging","msg":"This log entry depends on a user-provided value."}
```

## 母集合（着手前に自分で引く。traceability.repo.md 規則 9・10）

**引き方**: 「ログへ利用者由来の文字列を渡している箇所」ではなく、**CodeQL が現に指している
4 件**を母集合とする。理由 —— 本作業の受け入れ基準は「開いているアラートを閉じる」であり、
規則の一般化（全ログ経路の走査と是正）は射程が違う別作業である（CLAUDE.md「計画外の大規模
リファクタを行わない」）。

**除外理由つきの内訳**:

| 対象 | 扱い | 理由 |
| --- | --- | --- |
| #19 `AuditLogger.cs:22` | **直す** | 自リポジトリ・真陽性 |
| #24 `HybridSearchService.cs:123` | **直す** | 自リポジトリ・値域は閉じているがテイントは実在（後述） |
| #22 / #23 `GraphEndpoints.cs:94` | **直さない**（報告する） | 偽陽性。指摘に従うと実在の情報漏れを作る（後述） |
| #11 / #12 `HttpReportNarrativeDrafter.cs:55` | **射程外** | submodule `src/ai-stock-trading`。AST#1015 が追う |

既存の sanitize 実装（`ToolInvocationService.SanitizeForLog` / `LlmRouter.Sanitize`）は
**母集合に入れない** —— どちらも現にアラートが出ておらず（#3 / #4 は 2026-07-06 に fixed 済み）、
移行は本 PR の受け入れ基準に含まれない。IADR-0304 に非目標として明記する。

## 方針

### #19 `AuditLogger`（`cs/log-forging`・真陽性）

`Record(action, subject, outcome, detail)` の 4 引数はすべて呼び出し側由来である。
`subject` は利用者名（トークンのクレーム）、`detail` は自由文である。**値域が閉じていない。**
本番の `Program.cs` は `ClearProviders` を呼んでおらず Console プロバイダが有効なので、
改行を通すと**偽の監査行を注入できる**（CWE-117）。

**共有の sanitize を置く。** 置き場所は `Platform.Shared.Infrastructure/Foundation/Logging/`
（`AuditLogger` と同じプロジェクト）。判断の記録は **IADR-0304**。

- `Platform.Shared.Kernel` **は選ばない** —— Kernel は Result / Error と DDD 基底型の共有カーネルで、
  `src/README.md` 依存規則により **Domain からのみ参照される**。Infrastructure → Kernel の辺を
  新設することになり、ログ整形は Infrastructure の関心である。
- `Platform.Shared.Infrastructure` は**ユニット外参照が許された 3 プロジェクトの 1 つ**であり、
  `RetrievalService.csproj` は**既に参照している**。新しい ProjectReference は 1 本も要らない。

### #24 `HybridSearchService`（`cs/log-forging`・**発生源で断つ**）

#1019 の起票者の分析（`SearchModes.Normalize` が値域を 3 定数へ閉じている）は**正しい**。
だが**テイントは実在する**:

```csharp
public static string Normalize(string? mode) =>
    IsValid(mode) ? mode!.ToLowerInvariant() : Hybrid;
```

`IsValid` が真のとき返るのは **`mode` から作った新しい文字列**であって、定数そのものではない。
値は定数と一致するが、**オブジェクトは利用者入力由来**である。CodeQL は正しく追っている。

🔴 **したがって直し方は sink の sanitize ではなく、正規化の境界で許可リスト側の定数を返すことである。**
これは本リポジトリに先例がある —— `LlmRouter.ResolveModel` が
「利用者由来の文字列ではなく設定側が保持する正規の文字列を返す。テイント源を選択結果に
持ち込まない」と明記して同じことをしている。

**観測可能な振る舞いは変わらない**（`IsValid` は `OrdinalIgnoreCase` の一致なので、
妥当な入力の `ToLowerInvariant()` は必ず当該定数と文字列等価）。変わるのは**返す実体**だけである。

`SearchSorts.Normalize` も**同一構造**（同じファイルの 20 行下）なので併せて直す。
片方だけ直すと、次に読む人が「なぜ片方だけ」を復元できない。

**sink（`WarnEmbeddingUnavailable`）には sanitize を置かない。** 発生源を断った後の `mode` は
許可リストの定数そのものであり、そこへ sanitize を足すのは
**「起こり得ないケースへの防御的実装」**（CLAUDE.md 禁止事項）に当たる。

### #22 / #23 `GraphEndpoints.cs:94`（`cs/user-controlled-bypass`・**直さない**）

指摘箇所は多ホップ探索の `hops` 検証である。

```csharp
var requested = hops ?? GraphTraversal.DefaultHops;
if (requested < 1 || requested > GraphTraversal.MaxHops)
    return Results.BadRequest(new { error = "hops_out_of_range", ... });
```

CodeQL の言い分は "This condition guards a sensitive action, but a user-provided value controls it."
——「利用者入力が、機微な操作（＝この後の認可）を守る条件を制御している」。

🔴 **これはバイパスではない。** この分岐は要求を**拒否するだけ**であり、通過した場合の認可
（`accessResolver.ResolveAsync` → `AuthorizedNode.Authorize`）は**無条件に実行される**。
利用者入力で認可を*飛ばせる*経路は無い。

**そして指摘に従うと、実在の脆弱性ができる。** 検証を認可の後ろへ動かすと:

- 権限外・不存在の文書 → 404
- 可視の文書 → 400（`hops_out_of_range`）

となり、**`hops=99` を投げるだけで文書の存在が判別できる**。ADR-0034 決定 2 の存在秘匿
（すべて同一の 404 に倒す）が壊れる。`GraphEndpointsSecrecyTests` が固定している性質である。

**この事情は既にコード上のコメント（89〜92 行目・108〜112 行目）に書かれている。**
書いた人は CodeQL がここを指すことを承知のうえで、意図してこの順序にしている。

**よって本 PR ではコードを変更しない。** 正しい終わらせ方は
**「false positive として dismiss する」** というトリアージ判断であり、これは
リポジトリのセキュリティ状態を変える操作なので**人の裁定に委ねる**（#1019 のコメントで提案する）。

## 変更対象ファイル（宣言）

- `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Logging/LogSanitizer.cs`（新規）
- `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Audit/AuditLogger.cs`
- `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/Logging/LogSanitizerTests.cs`（新規）
- `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/Audit/AuditLoggerTests.cs`
- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchDto.cs`
- `src/knowledge/backend/Services/RetrievalService/Tests/`（正規化のテイント遮断テスト）
- `.ai-context/adr/IADR-0304_log-sanitization-placement.md`（新規）
- 本仕様書

🔴 **`src/knowledge/backend/Services/GraphService/Features/Graph/GraphEndpoints.cs` は変更しない。**

## 受け入れ基準

1. `AuditLogger.Record` が書く**ログ行そのもの**に改行・制御文字が現れない（4 引数すべて）。
2. 上の**陽性対照** —— 通常の値はログ行にそのまま現れる（「何も書かない」実装で緑にならない）。
3. `SearchModes.Normalize` / `SearchSorts.Normalize` が**許可リストの定数の実体**を返す
   （`ReferenceEquals` で固定。`ToLowerInvariant()` を残す実装では落ちる）。
4. 上の**陽性対照** —— 大文字・混在ケースの入力が従来どおり正規化される（値は不変）。
5. **変異試験**: sanitize を外すと陰性テストが落ち、戻すと緑に戻る。**両方向の件数を記録する。**
6. `dotnet build` / `dotnet test` が platform / knowledge 両ユニットで緑。
7. `check-unit-dependencies` / `check-backend-libraries` / `check-commit-messages` /
   `check-trace-blocks` / `scripts.test.js` が緑。

## やらないこと（明示）

- `GraphEndpoints.cs` の変更（上記のとおり。直すと脆弱性ができる）
- `src/ai-stock-trading/` への変更（submodule。#11 / #12 は AST#1015）
- `ToolInvocationService.SanitizeForLog` / `LlmRouter.Sanitize` の共有実装への移行
  （現にアラートが出ておらず、本 PR の受け入れ基準に含まれない。IADR-0304 に非目標として記録）
- 全ログ経路の走査と一括是正（射程が違う）
- #1019 の close（判断は起票者へ返す）
