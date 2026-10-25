---
title: 作業仕様書 — BFF の DataProtection 鍵リングの Valkey 接続が初回の失敗を覚えないようにする（#2242）
type: spec
status: done
related_ids: [NFR, NFR-07, NFR-18, ADR-0032, IADR-0251, IADR-0510, IADR-0522, IADR-0633]
author: claude
created: 2026-10-25
updated: 2026-10-25
issue: "#2242"
---

# 作業仕様書 — 鍵リングの接続が初回の失敗を覚えない（#2242）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。発端は #2232 と #2241 の e2e-live（BFF が Running/Ready なのに `/bff/auth/login` が全部 500）。
> 同型の事故 2 回目であり、#2241（E-54）はこの修正を先に入れる（オーケストレーターの裁定 2026-10-25・(c) 案）。

## 受け入れ基準（#2242）

- [ ] 最初の接続が失敗し、その後 Valkey が回復したら、次の `/bff/auth/login` は pod（プロセス）を作り直さずに 302 を返す（xUnit で固定）
- [ ] 鍵リングの接続とセッションストアのヘルスチェックの接続が、同じ「失敗を覚えない」遅延の方針を使う（xUnit で固定）
- [ ] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（BFF の slnx）が通る
- [ ] e2e-live が緑（PR の CI）

## 原因（調べた結果）

- `BffSessionExtensions.cs:74` は `new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(...))`。既定の
  `LazyThreadSafetyMode.ExecutionAndPublication` は**ファクトリの例外を覚える**。`AbortOnConnectFail` は既定（true）なので、初回の
  `Connect` が 1 度でも失敗すると（起動時の DNS の差し替えなど）、そのプロセスの鍵リングは以後ずっと例外を返す。
  ログインの challenge は nonce の cookie を DataProtection で保護するので、`/bff/auth/login` が全部 500 になる。
- `Program.cs:24` のヘルスチェック用の接続は `PublicationOnly`（失敗を覚えない）なので、readiness だけが回復して緑になる。

## 判断（IADR-0633）

1. **失敗を覚えない遅延を 1 か所に置く**: `SessionStoreConnection.Lazy(connect)`（`PublicationOnly`）を新設し、鍵リングとヘルスチェックの
   両方がこれを使う。方針が 2 か所で分かれていたことが原因なので、置き場を 1 つにする。
2. **`AbortOnConnectFail` は据え置く（true）**: 失敗は `Connect` の時点で例外になり、次の利用で `Connect` をやり直す。false にすると
   `Connect` は届かなくても成功し、失敗は各操作へ散る（鍵リングでは「鍵が無い」と区別できない読み取りの失敗になりうる）。根拠は IADR に置く。
3. **試験の継ぎ目**: 鍵リングの接続を作る関数（`SessionStoreConnect`）を DI に置く（既定は `ConnectionMultiplexer.Connect`。`TryAdd`）。
   試験は「1 回目は `RedisConnectionException`、2 回目からは偽の接続」を与え、本番の配線（`AddBffSession` の遅延・`RedisXmlRepository`・
   OIDC の challenge）のまま `/bff/auth/login` を 2 回送る。
4. **方針の一致の固定**: `Platform.Bff` の本番コードで `new Lazy<IConnectionMultiplexer>` を書いてよいのは `SessionStoreConnection` だけ、
   を走査で固定する（片方だけ既定の Lazy へ戻ると落ちる）。

## 変更するファイル

- `src/platform/backend/Bff/Platform.Bff/Foundation/Session/SessionStoreConnection.cs`（新規）・`BffSessionExtensions.cs`・`Program.cs`
- `src/platform/backend/Bff/Platform.Bff.Tests/BffKeyRingConnectionRecoveryTests.cs`（新規）
- IADR-0633・`.ai-context/adr/README.md`

## PoC への影響

BFF の再配備だけ（設定・Secret・Valkey は変えない）。平日の取引時間外に行い、SC-01 からのログインで確かめる（PR 本文に手順）。

## 検証

- `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（`src/platform/backend/backend.slnx`）
