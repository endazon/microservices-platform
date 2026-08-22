---
title: BFF が /feedback/stats へ資格情報を渡していない — 401 はロール不足ではなく後段の challenge の中継
type: spec
status: draft
related_ids: [FR-08, FR-10, SC-10, UC-05, NFR-09, ADR-0004, IADR-0044, IADR-0158]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-08 統計は運用者・管理者)
  - planning:projects/microservices-platform/05_screens/ (SC-10 運用ダッシュボード)
---

# 仕様書: BFF から FeedbackService への資格情報伝播（#948）

## 起点

欠陥修正。**有効なトークンで `/bff/dashboard/summary` が 401 を返す**（#948）。

## 根因 —— 認証スキームの問題ではない

`DashboardBffEndpoints.cs` の**同一ハンドラ内で 2 つの後段の扱いが違う**。`DashboardService` には
`Authorization` を転送するが、**`FeedbackService` には転送しない**。そして `/feedback/stats` は
**2026-08-10 に `RequireRole(admin, operator)` を獲得している**（#521 / IADR-0158）。集約は後段の
非 2xx を字義どおり中継するため、**後段の challenge（401）がそのまま利用者へ出る**。

**issue が「説明できない」とした 2 点が、これで両方説明できる。**

- **「ロール不足なら 403 のはず」** → ロール不足ではない。**資格情報が付いていない**リクエストへの
  challenge なので 401 が正しい
- **「`developer` は両ロールを持っている」** → 無関係。そのトークンは FeedbackService へ届いていない

issue の観察「dashboard-service の直近ログに着信が無い」は**正しく、かつ手がかりだった** ——
落ちているのは DashboardService ではなく **FeedbackService** である。

### 潰した対抗仮説

| 仮説 | 実測 |
| --- | --- |
| 資格情報を転送する `DelegatingHandler` が在る | 0 件（唯一の実装は LlmGateway の無関係な応答サニタイザ） |
| `UseAuthentication` の欠落 | `UsePlatformMiddleware()` 経由で該当サービス全てに配線済み |
| サービスごとの `Auth__*` 設定漏れ | helm の `deployment.yaml` はループ内で全サービスへ無条件注入 |
| `realm_access.roles` → `ClaimTypes.Role` 展開の不具合（issue の推定） | **この経路は評価されていない**（トークンが後段へ届いていない） |

## 射程 —— 2 箇所を同一 PR で直す

**#521 がロール要求を足したとき、BFF 側の呼び出し 2 本が両方とも取り残された。**

1. `DashboardBffEndpoints`（ダッシュボード集約）—— #948 が報告した症状
2. `FeedbackBffEndpoints:64`（`GET /bff/feedback/stats`）—— **issue に記録が無い。走査で発見**

伝播していたのは投稿（`POST /bff/feedback`）だけで、**その 1 本に転送テストがあるため「伝播している」と
読めてしまう形だった。1 本のテストが隣の 2 本の欠落を隠していた。**

**片方だけ直すと、同じ欠陥が残ったまま issue が閉じる。** よって同一 PR で扱う（#948 へコメントで記録済み）。

## 再発防止 —— 機械検査は足さない（範囲を実測して決めた）

BFF の後段呼び出しを全件見た。

| 呼び出し | 後段が認可を要求するか | 転送するか | 判定 |
| --- | --- | --- | --- |
| `DashboardBffEndpoints` → `/feedback/stats` | **する** | しない | 🔴 欠陥 |
| `FeedbackBffEndpoints:64` → `/feedback/stats` | **する** | しない | 🔴 欠陥 |
| `DocumentBffEndpoints:69` → `/documents`（読み取り群） | しない | しない | 正しい |
| `SearchBffEndpoints:51,109` → `/search` | しない | しない | 正しい |

**「全 BFF 呼び出しは資格情報を転送せよ」を機械で要求すると、正しい 3 箇所を叩く（偽陽性 60%）。**
正確な規則（後段が認可を要求するときだけ）は URL 文字列からルート宣言を解決する必要があり、脆い。

代わりに**検出をテスト側へ置く** —— `FeedbackStubHandler` へ「Authorization が無ければ 401」を
再現する knob（`FeedbackRequiresAuthorization`）を足し、**スタブが実体の契約を模せる**ようにする。

🔴 **既定は `false` である。実測で決めた。** 常時 true にすると `GetStats_ReturnsAggregatedStats` が
落ちるが、それは**装置由来の偽陽性**である —— テストの受信要求には `Authorization` ヘッダ自体が無い
（`TestAuthHandler` は独自スキームで認証する）ため、転送実装が正しくても渡すものが無い。

**本件は「1 つの変更（#521）が生んだ 2 箇所」であって、独立した 2 回の事故ではない。**
CLAUDE.md「同型の事故が 2 回起きたら検査器」の数え方としては **1 回目**である。
**3 例目が出たら機械検査へ切り替える。**

## テスト

| # | 確かめること | 実装 |
| --- | --- | --- |
| T-15 | 集約が FeedbackService へ資格情報を伝播する（転送を直接見る） | `DashboardBffEndpointTests` |
| T-16 | 後段が実体どおり認可を要求しても 200（症状の側から固定） | 同上 |
| T-17 | `GET /bff/feedback/stats` も伝播する | `FeedbackBffEndpointTests` |

**T-15 / T-16 は修正前に赤を実測した**（`失敗: 2 / 合格: 7 / スキップ: 0`）。落ちた理由も確認している ——
`... to be "Bearer feedback-token", but found <null>` と `... to be OK {200}, but found Unauthorized {401}`。
**設定ミスによる赤ではない。**

🔴 **T-17 は修正後に書いたため赤を見ていない。** 検出力が未証明なので**変異試験**にかけた ——
転送の 3 行だけを外し、`git diff` が当該箇所しか変えていないことと **`BUILD_EXIT=0` / `error CS` 0 件**を
先に読んでから、T-17 が `... but found <null>` で落ちることを確認し、復元した。

## 完了条件

- 上記 3 テストが通る
- `Platform.Bff.Tests` 全体が緑（skip は既存の env 制御ベンチマーク 1 件のみ。**skip を通過と数えない**）
- CI 緑

## 残す未解明

**稼働クラスタの `/bff/datasources` 401 は本根因では説明できない。** 同端点は FeedbackService に
依存せず、CI でも再現しない。**drift の疑いとして未解明のまま残す** —— 1 つの根因で全部を説明しない。

**実機（稼働クラスタ・Keycloak）では確認していない。** 上の連鎖は原文と単体テストの実測による。
