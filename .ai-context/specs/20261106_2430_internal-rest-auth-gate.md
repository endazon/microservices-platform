---
title: 作業仕様書 — 内部の AI 分析と Wiki の REST の群に認証の門を掛ける（段 1 は報告・段 2 で 401。#2430）
type: spec
status: done
related_ids: [NFR-18, NFR-09, ADR-0084, ADR-0032, IADR-0335, IADR-0424, IADR-0685]
author: claude
created: 2026-11-06
updated: 2026-11-06
issue: "#2430"
---

# 作業仕様書 — 内部の REST の群の認証の門（#2430）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-06。`origin/develop` `d98fd363b`）。

## 事象

- AI 分析の `/analysis`（`AnalysisEndpoints.cs`）と Wiki の `/wiki`（`WikiEndpoints.cs`）の群には、`RequireAuthorization` が無い。
- 未認証の要求は、ハンドラの中で「閲覧できるものが無い」（200 の空・404）に倒している（IADR-0335。`AnalysisEndpoints.IsAnonymous`・`WikiAccessResolver`）。

## 方針（IADR-0685）

- 共有の門 `InternalAuthGate.RequireAuthenticatedOrReport(group)`（`Platform.Shared.Infrastructure`）を 2 つの群に掛ける。
- 段は `Auth:InternalGate:Mode` で切り替える。段 1（既定）は報告だけ、段 2 は未認証を 401 で拒む。

## 受け入れ基準と試験

- [x] 段 2 では、2 つの群の全口（Wiki 4 口・AI 分析 3 口）が未認証の要求を 401 で拒み、認証済みの要求は通す（`InternalAuthGateTests`）。develop の上で Wiki 4 件・AI 分析 3 件が赤であることを、実装を外して確かめた。
- [x] 既定の段では、未認証の要求も従来どおり 200 で返す（同上と既存の `AnonymousAccessContractTests`）。

## 母集合（規則 9・10。着手前に引いた）

- 走査: knowledge と platform の各サービスの `Map(Get|Post|Put|Delete|Patch)` を数え、ファイルごとに `RequireAuthorization|AllowAnonymous` の数と突き合わせた。差がある群の定義も読んだ。
- 群にも端点にも門が無かったのは、AI 分析の `/analysis`（3 口）と Wiki の `/wiki`（4 口）の 2 つだけだった。
- 端点ごとに門を持っていたので除外した群:
  - グラフ `/graph`（4 口）
  - ダッシュボード `/dashboard`（4 口）・`/dashboard/knowledge-health`
  - フィードバック `/feedback`（3 口）
- 同期トークンが資格情報なので除外した群: 文書の `/private-notes/sync`（設計どおり）。

## 対象外

- BFF の外周（既に門がある）。ロールの判定の変更。段 2 への切り替え（稼働の設定）。
