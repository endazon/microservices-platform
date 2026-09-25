---
title: サービスの gRPC 試験器が起動時に待受のループバックを確かめ、全インタフェースへの bind を置換で隠さないようにする
type: spec
status: done
related_ids: [NFR-16, FR-05, ADR-0029, ADR-0075, IADR-0379]
author: Claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-16)
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: サービスの gRPC 試験器の待受をループバックに限ることを検査する

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 起票: #1509
- 非機能要件（NFR）: `NFR-16`（通信暗号化。暫定: サービス間平文はクラスタ／コンテナネットワーク内に限定して許容。
  試験の h2c は平文であり、**試験を走らせた端末の外へ開くのはこの限定の外**である）
- 関連 ADR: `ADR-0029`（gRPC / REST の使い分け）/ `ADR-0075`（east-west の gRPC 移行）/
  `IADR-0379` 決定 3（h2c 専用ポート。待受ホストは HTTP 側に従う —— #1298 で是正済み）
- ユースケース / 画面: なし

## 射程

利用者の指摘（2026-09-25）「Platform.Shared.Infrastructure.Tests が 0.0.0.0 でホストされているので 127.0.0.1 に」を受けた作業のうち、
**`Platform.Shared.Infrastructure.Tests` の `GrpcListenerBindingTests.cs` と本番 `GrpcListenerExtensions.cs` は
オーケストレータが別に是正した**（2026-09-25 の指示による射程の分割。#1507 / PR #1510、develop `36c51b99` でマージ済み。
ワイルドカードの陰性対照はソケットを開かない判定の試験へ置き換わった）。本書と本 PR はこの 2 ファイルに触れない。
本 PR は #1510 を取り込んだ develop に追随してから検証した。
本書の射程は**それ以外の試験ホスト**である。

## 母集合（自分で引いた。基点 `origin/develop` `c99cb1e4`、`git rev-parse --is-shallow-repository` = `false`）

### 軸 1: 全インタフェースの綴り（誤りの側から引く）

試験らしいパス（`test|Test|spec|e2e|TestSupport|testing|fixture` を含むパス。**拡張子で絞らない**）1670 本を
`git ls-files` で取り（`src/ai-stock-trading` を除く）、次の正規表現で走査した:

`ListenAnyIP|IPAddress\.(Any|IPv6Any)|https?://[*+]|0\.0\.0\.0|\[::\]|UseUrls|ASPNETCORE_URLS|ASPNETCORE_HTTP_PORTS|HTTP_PORTS|http_ports|UseKestrel|ConfigureKestrel|Listen\(`

| ヒット | 判定 |
| --- | --- |
| 7 サービスの `Tests/Grpc/GrpcKestrelFactory.cs`（AuthorizationService / LlmGateway / NotificationService / DashboardService / DocumentService / GraphService / RetrievalService）: `UseKestrel()` と `.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1")` | 🔴 **対象**。実ソケットで待ち受ける試験器。置換は全インタフェースへの bind を**隠す**側の仕組み |
| 同 7 サービスの `Tests/Grpc/GrpcTestConfiguration.cs`: `ASPNETCORE_URLS=http://127.0.0.1:0` | **対象**（検査の置き場）。値そのものはループバックで正しい |
| `Platform.Shared.Infrastructure.Tests/Foundation/Grpc/GrpcListenerBindingTests.cs:130`（`http://*:0` で実起動） | **除外（射程外）**: オーケストレータが是正中 |
| `Platform.Shared.Infrastructure.Tests/Foundation/Grpc/GrpcListenerExtensionsTests.cs`（`http://+:8080`・`0.0.0.0`・`[::]` 等） | **除外**: 構成解決の単体試験。文字列を `ResolveHttpAddresses` / `ResolveGrpcHost` へ渡すだけでソケットを開かない |
| `DataSourceService/Tests/Domain/SyncErrorRedactorTests.cs:25`（`https://***@…`） | **除外**: 秘匿化の期待文字列。`https?://[*+]` の偽陽性 |

### 軸 2: 他の待ち受け手段（Kestrel 以外）

全追跡ファイル（`src/ai-stock-trading` を除く。パスでも拡張子でも絞らない）を
`\.listen\(|createServer\(|UseKestrel\(|webServer|vite preview|--host\b|WireMockServer|new TcpListener|HttpListener\b` で走査した。

| ヒット | 判定 |
| --- | --- |
| `scripts/scripts.repo.test.js:10564` `srv.listen(0, '127.0.0.1', …)` | **除外**: 既にループバック |
| `deploy/mail-relay/mail-queue-exporter.js:216`（`'0.0.0.0'`）/ `deploy/mail-relay/reset-floor.js:247`（ホスト省略 = 全インタフェース） | **除外**: 本番のコンテナ実装であり試験ではない（Pod 外から scrape / 転送されるため全インタフェースが正）。`scripts/reset-floor.test.js` は `require` するだけで、`main()` は `require.main === module` で守られており待ち受けない |
| 各 `GrpcTestConfiguration.cs` / `Knowledge.IntegrationTests/Fixtures/BrokerTcpGate.cs` の `new TcpListener(IPAddress.Loopback, …)` | **除外**: 空きポートの探索・ループバック |
| `src/platform/frontend/playwright.config.ts`（`webServer` → `vite preview --port 4173`）・`package.json` の `"preview": "vite preview"` | **除外**: `--host` を渡しておらず、`vite.config.ts` にも `server.host` / `preview.host` が無い。Vite の既定は `localhost` |
| WireMock / `HttpListener` | ヒットなし |

### 軸 3: 実測（基点のコードで実行）

- 7 プロジェクトの gRPC 試験を実行し、`netstat -ano` をポーリングした（初回 0.3 秒間隔）。新たに現れた LISTENING は
  すべて `127.0.0.1:*` だった。
- 🔴 **ただしこの方法は陽性対照に落ちた**: 既知の全インタフェース bind（`GrpcListenerBindingTests` のワイルドカード試験。
  約 2 秒で閉じる）を、間隔なしのポーリングでも捕まえられなかった（Windows の `netstat` 1 回が遅い）。
  **よってポーリングの「無かった」は証拠にしない。** 代わりに**プロセス内で `IServerAddressesFeature` を検査する**
  （本 PR の検査がそれであり、試験のたびに走る）。

### AST（`src/ai-stock-trading`）

本ワークツリーでは submodule が未初期化のため、隣接クローン（`ai-stock-trading` `909241f5`）を読み取りだけで走査した。
`GrpcListenerExtensionsTests.cs`（構成解決の単体試験）と `JasperFxCommandLineTests.cs`（`--urls http://+:8080` の
コマンドライン解析）が文字列としてヒットするのみで、**実ソケットで全インタフェースへ待ち受ける試験ホストは無い**
（`UseKestrel` の試験器も無い）。**報告のみ。本リポジトリからは変更しない**（`IADR-0120`）。

## 設計

1. 各 `GrpcTestConfiguration` に `LoopbackOnlyGuard`（`IHostedLifecycleService`）を足す。`StartedAsync`
   （サーバの bind 後に呼ばれる）で `IServerAddressesFeature.Addresses` の全件を `BindingAddress.Parse` し、
   ホストが `localhost` か `IPAddress.IsLoopback` でなければ**サーバを止めてから**例外を投げる
   （`[::]` / `0.0.0.0` はいずれもループバックでない。全インタフェースの待受を試験プロセスの終了まで残さない）。
   例外は `StartServer()` まで伝わる。
2. 各 `GrpcKestrelFactory` の `ConfigureServices` で `AddHostedService<GrpcTestConfiguration.LoopbackOnlyGuard>()` を登録する。
   **`HttpAddress` を読む試験だけでなく、器を起動する全試験**で効く。
   - 🔴 **当初案の `CreateHost` の上書きは効かなかった（実測）。** `UseKestrel()` の器は `CreateHost` を通らない ——
     上書きに変異（判定を常に偽）を入れても 29 件が緑のままだった。ホストのライフサイクルに掛ける形へ改めた。
3. `HttpAddress` の `.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1")` を撤去する。
4. 共有の試験支援プロジェクトは作らない。既存の `GrpcTestConfiguration` / `FreeTcpPort` と同じく**サービスごとに同型で置く**
   （試験プロジェクトはサービス本体だけを参照しており、共有点を新設するのは本件の射程に対して過大）。

**本番コードは変更しない。**

## 受け入れ基準

- [x] 7 つの gRPC 試験器が起動直後に待受アドレスのループバック検査を行い、非ループバックなら例外で落ちる
- [x] `HttpAddress` の置換による隠蔽が無い
- [x] 検査が実際に効く（変異: 判定を常に偽にすると 7 プロジェクトの gRPC 試験が赤になる）
- [x] 対象 7 プロジェクトの試験が緑

## テスト方針

- 新しい試験ケースは足さない。**既存の gRPC 試験すべてが器の起動を通るので、検査は毎回走る**。
- 検査が配線されていることは変異（`IsLoopback` を常に `false`）で確かめる。ソケットを全インタフェースへ開く変異
  （`ASPNETCORE_URLS=http://*:0`）は、それ自体が本件の禁ずる待受になるため採らない。

### 実測（本 PR のコード）

- 検査が見た待受（AuthorizationService。検査に一時的な出力を入れて取得）: `http://127.0.0.1:<動的>` と
  `http://127.0.0.1:29817`（h2c）の 2 件のみ。**HTTP 側・h2c 側ともループバックであり、是正前から全インタフェースには
  開いていなかった**。本 PR は「開いていないこと」を毎回の試験で保証し、隠す置換を消すものである。
- 変異 `return false && (…)`（AuthorizationService、`GrpcResolveScope` 13 件）: **13 件すべて赤**、
  メッセージ「試験の待受がループバック以外に開いた: http://127.0.0.1:63006, http://127.0.0.1:27547」。
  🔴 最初の変異 `return false && A || B` は演算子の優先順位で `B` と同じになり、**変異になっていなかった**（緑のまま）。
  括弧で包んで取り直した。
- 変異を戻した状態で 7 プロジェクトの gRPC 試験: AuthorizationService 29 / LlmGateway 28 / NotificationService 12 /
  DashboardService 10 / DocumentService 46 / GraphService 75 / RetrievalService 53 件、すべて緑。

## 計画書との差異

- なし。試験器の内部の検査であり、計画の裁定は要らない。

## 未決事項

- なし。
