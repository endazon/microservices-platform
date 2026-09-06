---
title: h2c リスナが HTTP 側の待受ホストに従うようにし、試験が全インタフェースへ開かないようにする
type: spec
status: done
related_ids:
  - NFR
  - NFR-16
  - FR-05
  - ADR-0004
  - ADR-0029
  - ADR-0075
  - IADR-0379
  - IADR-0397
  - IADR-0400
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_service-communication.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration.md
---

# 作業仕様書: h2c リスナの待受ホストを HTTP 側に従わせる

## 事象（利用者の指摘から）

「テスト用に使用しているサーバーが 0.0.0.0 で起動していないか」という指摘を受けて実測した。**そのとおりだった。**

`AddPlatformGrpcListener`（`Platform.Shared.Infrastructure/Foundation/Grpc/GrpcListenerExtensions.cs`）は、
HTTP 側は構成（`urls` → `http_ports` → Kestrel 既定）から読み直して再宣言する一方、
**h2c ポートだけを無条件に `kestrel.ListenAnyIP(...)` で bind していた。**

```csharp
foreach (var address in httpAddresses)
    Listen(kestrel, address, HttpProtocols.Http1AndHttp2);

kestrel.ListenAnyIP(grpcPort.Value, o => o.Protocols = HttpProtocols.Http2);  // ← 常に全インタフェース
```

🔴 **試験は HTTP 側をループバックへ絞っている**（`GrpcTestConfiguration`:
`ASPNETCORE_URLS=http://127.0.0.1:0`）。**にもかかわらず h2c ポートだけが素通しで、
試験を走らせた端末の外から到達できた。** 運用者が片側で表明した意図を、もう片側が無視していた。

## 母集合（自分で引いた。基点・生出力・陽性対照つき）

基点 `origin/develop` `32724227`。`git rev-parse --is-shallow-repository` = `false`。

```console
$ git grep -n "0\.0\.0\.0|IPAddress\.Any|\[::\]|--host 0" -- src/ ':!src/ai-stock-trading'
AuthorizationService/Tests/.../GrpcKestrelFactory.cs:59   ← 表示文字列の置換（bind ではない）
LlmGateway/Tests/Grpc/GrpcKestrelFactory.cs:66            ← 同上
Platform.Shared.Infrastructure/Foundation/Grpc/GrpcListenerExtensions.cs:102 ← Listen() のホスト判定
```

**`ListenAnyIP` の呼び出しそのものは上の走査に出ない**（文字列に `0.0.0.0` を含まない）。
軸を変えて引き直した:

```console
$ git grep -n "ListenAnyIP" -- src/ ':!src/ai-stock-trading'
GrpcListenerExtensions.cs:55   ← 🔴 gRPC ポート。**無条件**
GrpcListenerExtensions.cs:102  ← Listen() の中。ホストがワイルドカードのときだけ
```

**陽性対照**（走査器が生きていることの確認）: 同じ走査は `ListenLocalhost` を `:104` にヒットさせる。

### フロントエンドの試験サーバも確かめた（射程外だが、指摘の射程を狭く取らないため）

```console
$ grep -n "host|webServer|preview" src/platform/frontend/playwright.config.ts
15: const PREVIEW_URL = `http://localhost:${PREVIEW_PORT}`;
33: command: `pnpm run preview -- --port ${PREVIEW_PORT} --strictPort`
$ grep -rn "host:" src/*/frontend/vite.config.ts
（dev proxy の target のみ。`server.host` / `preview.host` の宣言は無い）
$ grep -n '"dev"|"preview"|--host' src/platform/frontend/package.json
"dev": "vite" / "preview": "vite preview"   ← --host を渡していない
```

**Vite は `dev` / `preview` とも既定が `localhost` であり、`--host` も `host: true` も無い。
フロント側は既にループバックのみである。** 是正対象は h2c リスナ 1 箇所。

## 設計

**新しい構成キーを増やさない。** 運用者の意図は既に HTTP 側（`urls` / `http_ports`）に表明されている。
**h2c はそれに従う。**

```csharp
var grpcHost = ResolveGrpcHost(httpAddresses);
Listen(kestrel, BindingAddress.Parse($"http://{grpcHost}:{grpcPort.Value}"), HttpProtocols.Http2);
```

| 面 | HTTP 側の構成 | 解決される gRPC ホスト | bind |
| --- | --- | --- | --- |
| コンテナ | `ASPNETCORE_HTTP_PORTS=8080`（`aspnet:10.0` の既定）→ `http://*:8080` | `*` | 全インタフェース（**従前どおり**） |
| 試験 | `ASPNETCORE_URLS=http://127.0.0.1:0` | `127.0.0.1` | ループバックのみ |
| 何も設定なし | Kestrel 既定 `http://localhost:5000` | `localhost` | ループバックのみ |

🔴 **ホストが食い違うときは広い側（`*`）へ倒す。** 狭めると「本番で繋がらない」に化けるが、
広げても従前の振る舞いに戻るだけである。**片方向にしか壊れない側を選ぶ。**

### 本番の振る舞いを変えていないことの根拠

`mcp.microsoft.com/dotnet/aspnet:10.0` は `ASPNETCORE_HTTP_PORTS=8080` を既定で置く。
`ResolveHttpAddresses` は `http_ports` を `http://*:8080` として読むので、ホストは `*` になり
`ListenAnyIP` が選ばれる。**メッシュ内の他 Pod とサイドカーからの到達性は変わらない。**

## 受け入れ基準

- [x] HTTP 側がループバックのみのとき、h2c ポートが**外向きには開かない**
- [x] HTTP 側がワイルドカードのとき、h2c ポートは**従前どおり全インタフェースへ開く**（陰性対照）
- [x] ホストが食い違うときはワイルドカードへ倒れる
- [x] 何も構成されていなければ Kestrel 既定（`localhost`）に従う
- [x] **変異試験**: `ListenAnyIP` へ戻すと「外から繋がらない」試験が実際に赤になる
- [x] 既存の gRPC 試験（実 Kestrel 往復）が緑のまま

## テスト方針

**2 層に分けた。**

1. `GrpcListenerExtensionsTests`（+6 件・14 → 20）—— **構成の解決**を固定する。
   ワイルドカード 4 形（`*` / `+` / `0.0.0.0` / `[::]`）・ループバック 2 形・食い違い・同一ホスト複数・既定。
2. `GrpcListenerBindingTests`（新設 2 件）—— 🔴 **解決した値が本当に bind へ届いているか**を
   **ソケットで**確かめる。これは別の主張であり、1 だけでは配線漏れを捕まえられない。
   - 陽性対照を対で置く: ループバックからは**繋がる**（＝待受が立っている。立っていないことと区別する）
   - 陰性対照: ワイルドカードなら**外からも繋がる**（狭める側だけを見ると本番を壊しても気づけない）
   - ループバック以外の IPv4 が無い環境では skip する（CI のコンテナは 1 枚しか持たないことがある）

**実測した変異**: `Listen(...)` を `ListenAnyIP(...)` へ戻すと
`Expected CanConnect(outside!, grpcPort) to be False ... but found True` で **1 件が赤**になる。

## 計画書との差異

- 差異: なし。`ADR-0029` / `ADR-0075` は h2c 専用ポートを定めるが**待受ホストは定めていない**。
  `docs/api/east-west-grpc.md` §3 も「専用ポートに `HttpProtocols.Http2` だけを bind する」までであり、
  インタフェースの範囲は実装裁量である。**計画の裁定は要らない。**

## 未決事項

- なし。
