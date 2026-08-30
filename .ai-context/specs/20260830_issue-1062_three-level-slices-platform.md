---
title: 作業仕様書 — platform ユニットのスライスを Features/<集約>/<操作>/ の 3 段へ移送する（#1062）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0065
  - IADR-0282
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30)
related_specs:
  - ./20260830_issue-1061_remove-worker-layer.md
  - ./20260828_wave45-vsa-migration.md
issue: "#1062"
---

# 作業仕様書 — スライスの 3 段化（platform ユニット）

## 目的と射程

計画 `ADR-0065` 決定 2 が「スライスは **`Features/<集約>/<操作>/` の 3 段**」を規範とした。
**1 ユースケースのファイルを 1 フォルダへ束ねる**（`Endpoint` ／ `Command`|`Query` ／ `Handler` ／
そのユースケースが発行するイベントを同居させる。REPR）。実装リポジトリのユニット雛形
（`templates/unit-template/backend/Services/SampleService/Features/Samples/Create/`）が既に 3 段で
書かれており、**同決定は雛形の側を正とした**。

**射程は `src/platform/backend/Services/` の 4 サービスだけである**（issue #1062 補足「ユニット単位・
サービス単位で PR を刻んでよい」）。`src/knowledge/backend/Services/` は**別 PR が担当する**ため
1 ファイルも触らない。したがって本 PR は `Refs #1062`（`Closes` にしない）。

**純粋な移送であり、挙動は 1 つも変えない。** 担保はテスト件数の移送前後突合である。

### 射程外（本 PR で触らない）

- `src/knowledge/backend/Services/**`（別 PR。23 集約）
- `Tests/` の鏡写し化（`ADR-0065` 決定 3 ＝ issue #1063）。**テストは名前空間・`using` の追随だけ**行う
- 🔴 **`Features/` の外への移動**（`Infrastructure/` や `Domain/Ports/` へ出す等）。より正しく見えても
  **別の変更**である。`ADR-0065` 決定 2 はスライスの**段**の話であり、層の再配置ではない。
  候補は後述「追随課題」に記録するに留める
- `src/ai-stock-trading`（submodule。別リポジトリ）

## 移送の判定基準（3 段目へ降ろすもの／集約直下に残すもの）

**判断の要は「2 段目は集約 ＝ ビジネス能力、3 段目は 1 ユースケース」であり、
`Features/` 直下のファイルを機械的に 1 つずつ降ろすことではない。**

| 規則 | 内容 |
| --- | --- |
| **A** | **1 ユースケース専用の Endpoint / Command|Query / Handler / そのユースケースが発行するイベント** → **3 段目へ降ろす** |
| **B** | **同じ集約の複数操作が共有するもの**（DTO 束・ストア・port とその既定実装・ホステッドサービス・トランスポート・共有ヘルパ・エンドポイント群の**合成点**） → **集約直下に残す** |
| **C** | **1 クラスが複数操作の実体を同時に担い、分割が振る舞いの結線（DI 登録・プロトコル結線）を変えるもの** → 分割が**メンバを共有しない**なら降ろす（結線の等価性を Program.cs で保つ）。**設計意図として 1 本であることが統制の前提になっているもの**は残す |
| **D** | 集約（2 段目）は**エンドポイントごとに切らない**。既存の 7 集約はいずれもビジネス能力の単位であり、**本 PR では 2 段目を 1 つも動かさない** |

**規則 C の適用先は 2 件だけである** —— `McpToolHandlers`（降ろす）と `ToolInvocationService`（残す）。
判断の根拠は後述する。

### ファイル名は改名しない

**`ADR-0065` 決定 2 が規範としたのはフォルダの段であってファイル名ではない。**
既存ファイルを丸ごと降ろすときは**名前を変えない**（`EmailOutboxDispatcher.cs` を `Handler.cs` へ
改めると、クラス名との対応と grep 可能性を失う）。**複数操作を 1 ファイルに平積みしていた
エンドポイント束を分割して新設するファイルだけ、雛形どおり `Endpoint.cs` とする。**

## 母集合（自分で引いた。規則 9・10）

```console
$ find src/platform/backend/Services -path '*/Features/*' -name '*.cs' | wc -l
21
$ git grep -l -I -E "AuthorizationService\.Features|LlmGateway\.Features|McpServer\.Features|NotificationService\.Features" \
    -- . ':(exclude)src/ai-stock-trading' | wc -l
40
$ git grep -l -I -E "Features/(Authz|Users|Completions|Embeddings|McpClients|Tools|Notifications)/" \
    -- . ':(exclude)src/ai-stock-trading'
.ai-context/specs/20260828_wave45-vsa-migration.md
$ git grep -n "McpToolHandlers" -- . ':(exclude)src/ai-stock-trading' | wc -l
4   # 自ファイル 1 ＋ McpServer/Program.cs 3。テスト・文書からの参照はゼロ
```

| 区分 | 件数 | 扱い |
| --- | --- | --- |
| 移送対象の `Features/**.cs`（platform 4 サービス） | **21** | `git mv` ＋ 名前空間更新、または分割 |
| 追随する `Program.cs` | **4** | `using` と DI 登録（McpServer のみ 2 → 2 の差し替え） |
| 追随する `Tests/**.cs`（`using` の追加のみ） | **15** | McpServer 8 ／ NotificationService 7 |
| 凍結記録として除外 | **1** | `.ai-context/specs/20260828_wave45-vsa-migration.md`（確定済み作業仕様書。本文プロズを書き換えない） |

**旧パスを文字列で持つ `docs/` ・`deploy/` ・`scripts/` は 0 件である**（上の 2 本目の走査）。
`docs/tests/*` ・`.ai-context/adr/*` が持つのは**クラス名**であり、本 PR はクラス名を
`McpToolHandlers`（分割）以外**1 つも変えない**ため追随不要。`McpToolHandlers` は
リポジトリ全体で自ファイルと `Program.cs` にしか現れない（上の 4 本目の走査）。

**規則 10 の引き直し**: 本変更で新たに誤りになる自分の記述を、**変更後の語**（`Features/<集約>/<操作>`）
でも引いた。`src/README.md` はユニット構成と依存規則を述べるだけでスライスの段に言及しておらず、
`CLAUDE.md` ・`traceability.repo.md` にも該当記述は無い。追随は不要である。

## 変更内容

### 1. AuthorizationService（2 集約 → 20 操作）

`Features/Authz/`（**ABAC 認可** —— スコープ解決・ポリシー・属性辞書は 1 つのビジネス能力である。
`AbacPolicy` と `AttributeDefinition` を別集約へ割ると、両者を同時に検証する
`AbacValidation.ValidatePolicy` が集約を跨ぐ）

| 操作フォルダ | 面 |
| --- | --- |
| `ResolveScope/` | `POST /authz/scope` |
| `ListPolicies/` `GetPolicy/` `CreatePolicy/` `UpdatePolicy/` `SetPolicyActive/` `DeletePolicy/` | ポリシー CRUD |
| `ValidatePolicy/` | `POST /authz/policies/validate`（dry-run） |
| `ListAttributes/` `GetAttribute/` `CreateAttribute/` `UpdateAttribute/` `DeleteAttribute/` | 属性辞書 CRUD |
| `ValidateAttributes/` | `POST /authz/attributes/validate` |

集約直下に残すもの:

- `AuthzEndpoints.cs` — **合成点**（`/authz` グループと `admin` サブグループの構築）＋
  **`ValidatePolicyAsync`**（規則 B）。🔴 **この共有は設計上の要である** —— 元コードのコメントが
  「保存（POST / PUT）と dry-run の **3 経路がこの 1 つを呼ぶ**」「複製すると『検証は通ったのに保存で
  矛盾が出る』が構造的に可能になる」と明記している。**操作ごとに複製したら計画 #535 の裁定を壊す。**
  `private` → `internal` にするだけで、呼び出し関係は変えない
- `AuthzContracts.cs` — `CreatePolicyRequest`（Create / Update / Validate の 3 操作が共有）ほか
  要求・応答 DTO（規則 B）。`AuthzEndpoints.cs` の末尾から切り出す

`Features/Users/`（**利用者アカウント管理**）: `ListUsers/` `ListAssignableRoles/` `ReplaceAttributes/`
`ReplaceRoles/` `DisableUser/` `EnableUser/` の 6 操作。集約直下に `UserAdminEndpoints.cs`（合成点＋
全操作が共有する `ToDto` と `ValidationProblem`）を残す。

### 2. LlmGateway（2 集約 → 3 操作）

- `Features/Completions/{Complete,CompleteStream}/` ＋ 集約直下 `CompletionEndpoints.cs`
  （合成点＋両操作が共有する `LogStopReason`）。`SseJson` はストリーム経路専用なので
  `CompleteStream/Endpoint.cs` へ持って行く
- `Features/Embeddings/Embed/EmbeddingEndpoints.cs` —— **単一操作の集約なので合成点を残さず、
  ファイルを丸ごと降ろす**（改名しない。規則 B の共有物がゼロ）

### 3. McpServer（2 集約 → 8 操作）

`Features/McpClients/`: `ListClients/` `RegisterClient/` `DisableClient/` `EnableClient/`
`ReplaceAttributes/` `ListEffectiveTools/`。集約直下に残すのは

- `McpClientEndpoints.cs` — 合成点＋`ToView` / `TierName`（4 操作が共有）／ `Problem`（2 操作）／
  `SetEnabledAsync`（**Disable と Enable が共有する 1 個のハンドラ**。真に 1 つなので複製しない）
- `McpClientContracts.cs` — DTO 束（規則 B。移動しない）

`TryParseKind` / `TryParseTier` は `RegisterClient` 専用なので `RegisterClient/Endpoint.cs` へ降ろす。

`Features/Tools/`（規則 C の適用先）:

- `McpToolHandlers.cs` を **`ListTools/Handler.cs`（`McpListToolsHandler`）と
  `CallTool/Handler.cs`（`McpCallToolHandler`）へ分割する**。同クラスの 2 メソッドは
  **メンバを 1 つも共有していない**（`EmptyObjectSchema` / `ParseSchema` は一覧専用、
  `JsonOptions` / `Error` は実行専用）。それぞれ UC-08 基本フロー 1 と 2〜5 の**ちょうど 1 ユースケース**である。
  DI 登録は `AddScoped<McpToolHandlers>()` 1 件 → 2 件へ、SDK の
  `WithListToolsHandler` / `WithCallToolHandler` の解決先を差し替える（結線は等価）
- 🔴 **`ToolInvocationService.cs` は集約直下に残す。** 同クラスの冒頭が
  「**経路を 1 本にすることが統制の前提である**。ADR-0034 決定 9 の個人資料一律除外は…
  経路が分かれていると、どれか 1 本に除外を入れ忘れた瞬間に静かに破れる」と書いている。
  **2 操作へ割ると、この ADR の統制が構造的に破れる。** 規則 C の後段（設計意図として 1 本で
  あることが前提のもの）に当たる

### 4. NotificationService（1 集約 → 5 操作）

`Features/Notifications/` の 13 ファイルのうち **3 段目へ降ろすのは 5 ファイル**で、**分割で 2 ファイルを新設する**。
残る 8 ファイルは集約直下に留まる。

| 操作フォルダ | 中身 | 根拠 |
| --- | --- | --- |
| `ListNotifications/Endpoint.cs` | `GET /notifications`（新設。`NotificationEndpoints.cs` から分割） | 規則 A |
| `MarkRead/Endpoint.cs` | `POST /notifications/{id}/read`（新設。同上） | 規則 A |
| `Accept/` | `NotificationIngressEndpoints.cs`（`POST /internal/notifications`）・`NotificationIngress.cs`（受理判断＋`NotificationIngressOutcome`）・`NotificationIngressDtos.cs`（この 1 操作でしか使わない要求／応答） | 規則 A。**受け口 1 ユースケースの Endpoint・Handler・契約がちょうど揃う**（REPR の教科書形） |
| `DispatchEmails/` | `EmailOutboxDispatcher.cs`（＋`DispatchSummary`） | 規則 A。outbox の送出は 1 ユースケース |
| `PurgeExpired/` | `NotificationRetention.cs` | 規則 A。保持期限切れの掃除は 1 ユースケース |

**集約直下に残す 8 ファイルと、その理由**:

| ファイル | 残す理由 |
| --- | --- |
| `NotificationEndpoints.cs` | **合成点**へ変わる（`/notifications` グループの構築と `RequireAuthorization()`）。2 操作が同じグループ・同じ認可の下にあることを 1 箇所で表す（規則 B） |
| `NotificationStore.cs` | **`ListNotifications` と `MarkRead` の両方**が使う（`ListAsync` / `MarkReadAsync` / `CountUnreadAsync`）。**ユースケースではなく読み書きの器**であり、片方の操作フォルダへ入れると他方が兄弟フォルダを参照する（規則 B） |
| `NotificationDtos.cs` | `NotificationDto` を一覧と既読化の両方が返す（`NotificationListDto` / `NotificationReadResultDto` が内包）。**契約テストが固定する集合**でもある（規則 B） |
| `NotificationPublisher.cs` | コメントが「**本メソッドはその 5 経路すべての共通の出口である**」と明記。現に `Accept` が使い、発火の結線（#451 解除後）が増える。**1 ユースケースのものではない**（規則 B） |
| `IEmailTransport.cs`（＋`EmailMessage` / `EmailSendResult`） | **port**。`DispatchEmails` が使うが、実装（SMTP）は差し替え前提であり、port を 1 操作の下へ隠すと差し替え先が操作フォルダを参照する（規則 B） |
| `IEmailAddressResolver.cs`（＋既定実装 `UnresolvedEmailAddressResolver`） | 同上 |
| `UnconfiguredSmtpEmailTransport.cs` | port の既定実装（アダプタ）。同上 |
| `NotificationMaintenanceHostedService.cs` | **`DispatchEmails` と `PurgeExpired` の 2 操作を周期で回す器**。どちらか一方の下には置けない（規則 B） |

> **追随課題（本 PR ではやらない）**: `IEmailTransport` / `IEmailAddressResolver` は `Domain/Ports/` へ、
> `UnconfiguredSmtpEmailTransport` は `Infrastructure/ExternalServices/` へ、
> `NotificationMaintenanceHostedService` は `Infrastructure/` へ置くのが `ADR-0065` 決定 1 の層分けには
> 合う。**`Features/` の外へ出す変更は本 issue の射程外**（決定 2 は段の話である）。別途起票する。

### 5. 実装 ADR（IADR）は作らない

**本件は計画 `ADR-0065` 決定 2 の実行であり、実装側で決める余地が無い。**
同型の移送である #1061（`Worker/` 撤去）も IADR なしで着地している。
集約（2 段目）は 1 つも動かさないため「非自明な集約境界」の判断も生じていない。
規則 C の 2 件（`McpToolHandlers` を割り、`ToolInvocationService` を残す）は**既存の ADR-0034 決定 9 と
クラス自身の設計意図に従った適用**であり、新しい決定ではない。経緯は本仕様書と PR に残す。

## 受け入れ基準

- [x] `src/platform/backend/Services/*/Features/` の深さ 2 のディレクトリが 0 ではない（**36 件**）
- [x] 各操作フォルダに、そのユースケースの Endpoint（＋あれば Handler・契約）が同居している
- [x] 2 段目（集約）は 7 件のまま動いていない（エンドポイント単位に割っていない）
- [x] `dotnet build src/platform/backend/backend.slnx` が成功
- [x] `dotnet test src/platform/backend/backend.slnx` が緑で、**件数が移送前と一致**
- [x] `dotnet format src/platform/backend/backend.slnx --verify-no-changes` が差分なし
- [x] `node scripts/check-unit-dependencies.js` 違反 0 件
- [x] `check-commit-messages` / `check-trace-blocks` / `check-doc-links` / `gen-knowledge-graph --check` 緑
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 緑
- [x] `src/knowledge/backend/**` の差分が 0 件

## 検証記録（2026-08-30 実走）

### 移送の実測

| 単位 | 移送前 | 移送後 |
| --- | ---: | ---: |
| `Features/<集約>/` の集約ディレクトリ（platform） | 7 | **7**（1 つも動かしていない） |
| `Features/<集約>/<操作>/` の操作ディレクトリ（platform） | 🔴 **0** | **36** |

内訳: AuthorizationService 20（Authz 14 / Users 6）・LlmGateway 3（Completions 2 / Embeddings 1）・
McpServer 8（McpClients 6 / Tools 2）・NotificationService 5（Notifications 5）。

### テスト件数の突合（純移送の担保）

移送**前**（`origin/develop` = `7b57319a`）と**後**で、`dotnet test src/platform/backend/backend.slnx` の
per-project 件数が**完全に一致**した（7 テストプロジェクト・合計 **1191**／合格 **1190**／スキップ **1**）。

| テストプロジェクト | 前 | 後 |
| --- | --- | --- |
| `AuthorizationService.Tests` | 140 | 140 |
| `LlmGateway.Tests` | 202 | 202 |
| `McpServer.Tests` | 66 | 66 |
| `NotificationService.Tests` | 53 | 53 |
| `Platform.Bff.Tests` | 446（合格 445 / スキップ 1） | 446（同） |
| `Platform.Shared.Infrastructure.Tests` | 242 | 242 |
| `Platform.Shared.Kernel.Tests` | 42 | 42 |
| **合計** | **1191** | **1191** |

> `Platform.Bff` は `src/ai-stock-trading`（submodule）を `ProjectReference` するため、
> **未 populate の worktree では `CS0246` でビルドできない**。本作業では
> `git submodule update --init --depth 1 src/ai-stock-trading` を実行してから前後とも計測した
> （#1061 の作業仕様書が「検証できなかった」と記録した箇所を、本 PR では計測できている）。

### 実行した検査

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/platform/backend/backend.slnx` | 0 エラー / **0 警告** |
| `dotnet test src/platform/backend/backend.slnx` | 緑・件数一致（上表） |
| `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | 差分なし |
| `node scripts/check-unit-dependencies.js` | OK（csproj 140 / .cs 1976・VSA 層分類 **346**。違反 0） |
| `node scripts/check-trace-blocks.js` | OK（158 件） |
| `node scripts/check-doc-links.js` | OK（1002 件） |
| `node scripts/gen-knowledge-graph.js --check` | OK（in-repo エッジ 4508 件） |
| `node scripts/check-commit-messages.js` | OK |
| `node scripts/check-adr-numbering.js` | OK |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | OK |

### 検証できなかったこと

- **`docs/api/openapi.yaml` の再生成差分**: 生成は CI（`openapi.yml`）が行う。ローカルでは
  サービスを起動していないため未検証。**ルートの追加・削除・パス変更は 1 つもしていない**
  （登録順のみ元と同一に保った）ため差分は出ない見込みだが、**CI に委ねる**。
- **`src/knowledge/backend/backend.slnx`**: 本 PR は knowledge を 1 ファイルも触っていない
  （`git status` で確認済み）。並列の別 PR が担当するため、こちらではビルドしていない。
- `git rev-parse --is-shallow-repository` = `false`（`git log` を出典に引ける状態）。

### 追随課題（本 PR の射程外・別途起票する）

`ADR-0065` 決定 1 の層分けに照らすと `Features/` の外が正しいものが 4 つある。
🔴 **決定 2 は段の話であり層の再配置ではないため、本 PR では動かさない。**

| 対象（NotificationService） | あるべき置き場 |
| --- | --- |
| `IEmailTransport.cs` / `IEmailAddressResolver.cs` | `Domain/Ports/` |
| `UnconfiguredSmtpEmailTransport.cs` / `UnresolvedEmailAddressResolver` | `Infrastructure/ExternalServices/` |
| `NotificationMaintenanceHostedService.cs` | `Infrastructure/`（周期実行の器） |
| `NotificationStore.cs` | `Infrastructure/Persistence/` か `Features/` 直下かは要判断（読み書きの器） |
