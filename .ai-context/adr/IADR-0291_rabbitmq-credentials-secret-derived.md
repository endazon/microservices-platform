---
title: IADR-0291 ブローカの資格情報は Secret 由来にし、未注入は起動失敗にする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0003
  - ADR-0027
author: claude
created: 2026-08-28
updated: 2026-08-28
---

# IADR-0291 ブローカの資格情報は Secret 由来にし、未注入は起動失敗にする

- 状態: Accepted
- 日付: 2026-08-28

## 起点・課題

#1022。#1012（IADR-0286）が DB で行った是正の**残件**である。RabbitMQ 側には

```csharp
var rabbit = builder.Configuration["RabbitMq:ConnectionString"]
    ?? "amqp://guest:guest@rabbitmq:5672";
```

が 13 箇所（`Program.cs` 7 ＋ 本番 `appsettings.json` 6）残っており、**構成の注入漏れが
「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れて**いた。#1012 がこれを射程から外したのは、
撤去に**ブローカ側の資格情報変更を伴う配備作業**が要るためである。

### 🔴 着手時の実測で覆った前提

**issue が求めた「ブローカの資格情報を dev 既定から分離する」を、値の変更としては実施できない。**
共有 RabbitMQ（`platform-infra` の 1 実体）へは **AST の 3 サービスも接続し**、AST chart
（`src/ai-stock-trading/deploy/helm/ai-stock-trading/values.yaml`）は
`global.rabbitmqConnectionString: amqp://guest:guest@rabbitmq:5672` を**自前でハードコードして持つ**。
submodule は本リポジトリの規約の対象外（不変）であり、ブローカの利用者名／パスワードを変えると
**AST がローカル k8s で認証エラーになる**。

## 決定

### 決定 1 — 撤去するのは「イメージへ焼かれる既定」であって「dev 既定の値」ではない

本番 `appsettings.json` から `RabbitMq` を撤去し、`Program.cs` の `??` を `?? throw` にする。
**dev 既定の値（`guest`/`guest`）は残す。** #1012 が `kp/kp` を dev 既定として残したまま
イメージから撤去したのと**同じ形**である（IADR-0286 決定 1）。受け入れ基準
「ブローカ側の資格情報が Secret 由来になる（dev 既定は env で上書き可）」は**供給元の話**であり、
値そのものの変更は求めていない。値を変える判断は AST 側の対応とセットでしか成立しない（§申し送り）。

**`appsettings.Development.json` は残す**（IADR-0286 決定 1 と同じ理由。`dotnet run` の利便であり、
イメージの本番既定ではない）。

### 決定 2 — 合成ルートは 1 サービス 1 解決点にし、未設定なら落とす

`graph` / `retrieval` / `datasource` は `UseRabbitMq(new Uri(...))` の**引数の中で**構成を読んでいた。
`UseWolverine` の前へ巻き上げて 1 解決点にし、`?? throw new InvalidOperationException(...)` を置く
（IADR-0286 決定 2 と同形。共有ヘルパは作らない）。

**例外は「接続失敗」と読み分けられなければならない**（#1022 の明示要件）。未注入は
`InvalidOperationException`（メッセージにキー名と注入手段）、繋ぎ先はあるが届かないときは Wolverine の
`BrokerInitializationException` —— **型もメッセージも重ならない**。従前は未注入が
「`rabbitmq` という名前が引けない接続失敗」に化けており、この 2 つが区別できなかった。

### 決定 3 — 配備側の注入は `global.db` と同型にする

`global.messaging`（`host` / `port` / `user` / `existingSecret` / `passwordKey`）と per-service
`messaging: true` を足し、**パスワードは Secret から `RABBITMQ_PASSWORD` として入れ、接続文字列は
k8s の `$(VAR)` 補間で組む**（env の値へ平文パスワードを描画しない）。対象は
`RabbitMq:ConnectionString` を読む 7 サービス。`aianalysis` は `appsettings.json` に値を持つが
**一度も読まない**ので撤去のみとし、helm には足さない（#1012 の AiAnalysis の DB と同じ判断）。

⚠️ `$(VAR)` は同一 container 内で先に定義した env しか参照できない —— 順序を崩さないこと。

### 決定 4 — 「配備側」は手動 apply と ESO の 2 本ある（IADR-0286 の追記を先例として適用する）

app 側の Secret `rabbitmq-app` を `k8s-local-up.sh` の `ESO != 1` ブロックで手動 apply する以上、
**`ESO=1` 側の供給元（`externalsecret-rabbitmq-app.yaml` ＋ Vault seed ＋ apply）を同じ PR で置く。**
#1012 はこれを落として「ESO=1 で供給元が 0 本」の状態でコミットされた（IADR-0286 の 2026-08-28 追記）。
**先例に倣った適用であり、事故の 2 回目ではない** —— よって横断の機械検査は足さず、
対の試験 2 本（`k8s-local-up.test.js`）に留める。

ブローカ側（`platform-infra` の Secret `rabbitmq`）は **`username` を新設**して利用者名も Secret 由来にする。
こちらは bootstrap 必須のため `ESO=1` でも手動 apply をスキップしない（`creationPolicy: Merge`。IADR-0099）。

### 決定 5 — テストは「実配備と同じ経路」で注入する

`[ModuleInitializer]` で環境変数 `RabbitMq__ConnectionString` を置く（`TestRabbitMqConfiguration.cs`・
7 プロジェクト）。**従前の `ConfigureAppConfiguration` の上書きは一度も効いていなかった** ——
`Program.cs` はトップレベル文で読むため間に合わず、実際に効いていたのは `appsettings.json` の
既定値の側である（#1022 で実測）。2 つ置くと「どちらが効いているか」が分からなくなるので撤去する。

統合テスト器（`Knowledge.IntegrationTests`）は `builder.UseSetting(...)` を使っており**今も効く**。
ただしフィクスチャの `ConnectionString` が null のときの fail-closed guard は
`ConfigureAppConfiguration` 側にあって**間に合わなくなる**ため、`UseSetting` の側へ引き上げた。

## 結果

- baseline（`scripts/default-credentials-baseline.json`）が **13 件 → 0 件**になった。
- 未注入は起動時に落ちる（変異 M1: GraphService.Tests が 95 件 `InvalidOperationException` で失敗）。
  再混入は CI が止める（変異 M2: `[added] … [amqp-credentials]` で exit 1）。
- 両ユニットの全テストが緑（knowledge 12 / platform 7 プロジェクト）。`k8s-local-up.test.js` は 104 → 107 件。

## 申し送り

- 🔴 **dev 既定 `guest`/`guest` を実際に別の資格情報へ変えるには AST 側の対応が要る**
  （AST chart の `global.rabbitmqConnectionString` と AST の `appsettings`）。本リポジトリからは
  `RABBITMQ_USER` / `RABBITMQ_PASSWORD` と `global.messaging.user` で上書きできる形まで用意した。
- `rabbitmq-app` は `eso_wait` と ESO 供給後の `rollout restart` の対象に**入れていない** ——
  `postgres-app`（#1012）も入っておらず、**両者に共通する既存の穴**である（ESO の初回同期は
  helm/apply 直後には完了しておらず、`secretKeyRef` の env は Pod 起動時に一度だけ解決される。
  IADR-0103）。片方だけ直すと非対称になるので、**2 つまとめて別 issue で扱うこと**。
- 本環境では helm のレンダリング検証ができない（helm / kubectl 不在）。CI の
  `check-deploy-manifests` に委ねる。
- 領域宣言の外にあるため触っていない追随: `scripts/README.md` の `check-default-credentials.js` 行
  （「既知の残件（RabbitMQ 13 箇所…別 issue）」）と `.github/workflows/ci.yml` の同趣旨のコメント。
  **どちらも 0 件になった事実に追随させること。**

## 関連

- #1022（本 ADR の起点）・#1012 / IADR-0286（DB 側の先行是正）・#1032 / IADR-0289（統合テスト器）
- 作業仕様書: `.ai-context/specs/20260828_issue-1022_rabbitmq-default-credentials.md`
