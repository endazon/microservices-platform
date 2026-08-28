---
title: 作業仕様書 — 既定資格情報の排除と構成注入漏れの fail-fast 化（#1012）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0002
  - ADR-0008
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "planning:projects/microservices-platform/02_requirements/01_requirements.md（非機能要件・セキュリティ）"
related_adrs:
  - IADR-0286
---

# 作業仕様書: 既定資格情報の排除（#1012）

## 起点

issue #1012「8 サービスの接続文字列に既定資格情報が埋まっており、構成の注入漏れが『起動成功』に倒れる」。
同 issue は**着手前に配備側の依存を実測せよ**と定めている（落とすと止まるため）。本節はその実測である。

## 母集合の実測（2026-08-28・head 6357a30）

軸を 4 本引いた。**issue 本文の一覧を母集合にしていない**（規則 1・2）。

| 軸 | 走査 | 実測 |
| --- | --- | --- |
| 1 | `grep -rn 'Password=kp' --include=Program.cs src/`（submodule 除く） | **15 箇所 / 8 サービス** |
| 2 | `find … -name 'appsettings*.json'` の `DefaultConnection` | 🔴 **10 サービス × 2 ファイル（json / Development.json）が値を持つ** |
| 3 | `deploy/docker-compose.yml` の `ConnectionStrings__DefaultConnection` | **12 行**（MSP 8 ＋ AST 3 ＋ 共通 anchor 1） |
| 4 | `deploy/helm/**` の `ConnectionStrings` | 🔴 **0 件**（注入していない） |

### 🔴 実測で覆った前提（issue の記述の訂正）

**`Program.cs` の `?? "…Password=kp"` は実際には到達しない。** 軸 2 のとおり **`appsettings.json` が同じ
既定値を持つ**ため、構成解決は常に成功する。したがって:

- **k8s が helm で注入していなくても動いていた理由**は「コード既定」ではなく **`appsettings.json`**（イメージに焼かれる）である。
- **#1009 が McpServer / NotificationService へ入れた fail-fast も同じ理由で不発**である
  （throw は書かれているが `appsettings.json` が値を供給するため発火しない）。#1012 の「対応済み」は
  **throw を置いた**という意味に留まり、**既定資格情報はイメージに残っている**。
- ゆえに **`Program.cs` だけを直すと「直したように見えて欠陥は残る」**。撤去すべきは
  **イメージへ焼かれる `appsettings.json` の側**である。

`kp/kp` は init スクリプト（`deploy/create-multiple-dbs.sh` と `deploy/local/infra/postgres.yaml`）が
compose・k8s の双方で実際に作る**開発用の実資格情報**である（架空の値ではない）。

## 対象と除外

| 区分 | 対象 | 理由 |
| --- | --- | --- |
| ✅ 対象 | MSP の DB 利用 10 サービス（authorization / conversion / dashboard / datasource / document / feedback / graph / wiki / aianalysis / mcp / notification のうち DB を持つもの）の **`appsettings.json` の `ConnectionStrings` 撤去** | イメージへ焼かれる本番既定であり、これが注入漏れを隠す |
| ✅ 対象 | 同サービスの **`Program.cs` の `??` 既定 → fail-fast**（#1009 の先例と同形） | 撤去後に「未設定で起動成功」へ倒れないため |
| ✅ 対象 | **helm の注入**（`global.db` ＋ per-service `database` ＋ Secret 参照） | 軸 4 のとおり k8s は appsettings に依存していた。**順序は「配備側で注入 → コードから既定値を外す」**（issue の指示） |
| ✅ 対象 | `k8s-local-up.sh` の app namespace Secret 作成（dev 既定 `kp`・env 上書き可） | helm の Secret 参照先を用意する |
| ⛔ 除外（着手後の実測で覆った） | compose の `aianalysis-service` の DB 配線 | **AiAnalysisService は `GetConnectionString` を一度も呼ばない**（走査で確認）。compose でも `*db-env` を継いでおらず、`appsettings.json` の `DefaultConnection` は**誰も読まない死んだ設定**である。撤去のみで足り、compose の変更は要らない |
| ✅ 対象 | テスト器（`WebApplicationFactory` 系）への構成注入 | Program.cs を起動するため。**明らかにダミーと分かる値**を使う |
| ⛔ 除外 | `appsettings.Development.json`（`Host=localhost;…`） | **イメージの本番既定ではない**（`dotnet run` のローカル利便）。撤去すると開発者の手元が壊れ、得るものが無い |
| ⛔ 除外 | RabbitMQ の `amqp://guest:guest@rabbitmq:5672`（7 箇所） | **同型の欠陥だが射程外**。helm values が「コード既定が in-cluster DNS で解決される」と明記しており、撤去には**ブローカ側の資格情報変更を伴う配備作業**が要る。**別 issue として起票する**（本仕様書 §申し送り） |
| ⛔ 除外 | MinIO の `minioadmin`（k8s-local-up の dev 既定） | Secret 経由の注入が既に成立しており（`global.objectStorage.existingSecret`）、**イメージに焼かれていない** |
| ⛔ 除外 | AST（`src/ai-stock-trading`）の 3 サービス | submodule 不変の規約。helm values の該当キーにも `database` を足さない（挙動不変） |

## 受け入れ基準

- [ ] `appsettings.json`（Development を除く）に `ConnectionStrings` が 1 件も残らない
- [ ] 対象サービスの `Program.cs` が未設定時に `InvalidOperationException` で落ちる（1 サービス 1 解決点）
- [ ] helm が対象サービスへ `ConnectionStrings__DefaultConnection` を注入する（パスワードは Secret 参照）
- [ ] compose・テスト・ローカル開発（`dotnet run`）がいずれも壊れない
- [ ] 検査器が「`Program.cs` / `appsettings.json` の接続文字列リテラル」の再混入を止める（**直してから置く**）
- [ ] 変異試験: 注入を外すと実際に起動が落ちる／既定値を戻すと検査器が捕まえる

## 実施の記録（実測）

| 実行 | 結果 |
| --- | --- |
| `dotnet build`（両 slnx） | 0 Error |
| `dotnet test src/knowledge/backend/backend.slnx` | **全 12 プロジェクト緑**（AiAnalysis 95 / Dashboard 26 / DataSource 136 / Conversion 74+2skip / Feedback 21 / Ingestion 28 / Document 205 / Graph 250 / Contracts 27 / Retrieval 131 / Integration 30+40skip / Wiki 64） |
| `dotnet test src/platform/backend/backend.slnx` | **全 7 プロジェクト緑**（McpServer 66 / Authz 95 / LlmGateway 202 / Shared.Infrastructure 125 / Shared.Kernel 42 / Bff 404+1skip / Notification 53） |
| `dotnet format --verify-no-changes`（両 slnx） | 差分なし |
| `node scripts/check-default-credentials.js` | OK（走査 30 件・新規 0・既知 13 件は baseline） |
| `node scripts/k8s-local-up.test.js` | 97 件緑 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **638 件緑**（検査器ラチェット 43 → 44 を追随） |

### 🔴 テスト器への注入は環境変数で行う（実測で判明した罠）

`WebApplicationFactory.ConfigureAppConfiguration` では**間に合わない**。トップレベル文の
`builder.Configuration.GetConnectionString(...)` は `builder.Build()` より前に評価されるため、
ホスト構築時に足すコールバックは**既に読まれた後**に適用される（注入したのに起動が落ちた）。
`[ModuleInitializer]` で環境変数へ入れる形（`TestDatabaseConfiguration.cs`・10 プロジェクト）へ改めた。
**実配備と同じ経路で注入する**ことになり、器としても正しい。DbContext は InMemory へ差し替わるため
**資格情報を持たない値**（`Host=localhost;Database=<svc>_test`）で足りる。

## 変異試験（実測）

| 変異 | 実測 |
| --- | --- |
| **M1**: テストの注入キーを `ConnectionStrings__MUTATED` へ変える（＝未注入を再現） | **GraphService.Api.Tests が全件 `InvalidOperationException: ConnectionStrings:DefaultConnection が未設定である` で落ちた** —— 未注入が「起動失敗」へ倒れることを実測 |
| **M2**: `appsettings.json` へ既定資格情報を戻す | **`check-default-credentials` が `[added] … [connection-string-credentials]` で exit 1** —— 再混入を捕まえることを実測 |
| **陰性対照**: 資格情報を持たない値（`Host=postgres;Database=graph_svc`） | 検査器は落とさない（self-test 6 件に固定。**「秘密を書かせない」検査であって「設定を書かせない」検査ではない**） |

いずれも変異を戻して緑への復帰を確認した（`git status` 空）。

## 🔴 事後に見つけた欠陥: ESO=1 で `postgres-app` の供給元が無かった（レビュー指摘・是正済み）

**着地後の AI レビューが 2 回続けて 🔴 で指摘し、実測で裏が取れたので是正した。**

`k8s-local-up.sh` は `postgres-app` の手動 apply を `minio-credentials` / `wikijs-db` / `wikijs-sync` と
**同じ `if [ "${ESO:-}" != "1" ]` ブロック**へ入れた。このブロックの意味は
「**ESO=1 のときは Vault→ExternalSecret 供給へ委譲するので手動 apply をスキップする**」であり、
3 兄弟にはいずれも対応する `deploy/local/vault/eso/externalsecret-*.yaml` が実在する。
**`postgres-app` にはそれが無かった。**

したがって `ESO=1` では **供給元が 1 つも無くなる**。helm の `deployment.yaml` は
`global.db.existingSecret`（＝ `postgres-app`）を **非 optional** な `secretKeyRef` で参照するため、
DB を持つ 8 サービス（document / datasource / conversion / authorization / wiki / dashboard / graph /
feedback）が `CreateContainerConfigError` で起動しない。

**なぜ自分で気づけなかったか。** 本仕様書の「対象と除外」は
「**配備側の注入が先、コードの撤去は後**」（IADR-0286 決定 3）という順序だけを守り、
**注入経路が 2 本ある**こと（既定の手動 apply と ESO 経由）を数えていなかった。
`ESO != 1` ブロックへ足す判断は 3 兄弟の**見た目に合わせた**もので、
そのブロックが何を意味するか（＝もう 1 本の供給を約束すること）を読んでいない。
**母集合の規則 2「あり得る形をすべて列挙してから引く」を、供給経路に対して適用しなかった事故である。**

### 是正（4 点）

| # | 変更 |
| --- | --- |
| 1 | `deploy/local/vault/eso/externalsecret-postgres-app.yaml` 新設（MSP ns・`msp/postgres-app` → `password`・`creationPolicy: Owner`） |
| 2 | `bootstrap.sh` へ `secret/msp/postgres-app` の seed 追加（dev 既定 `kp`。手動 apply と同値） |
| 3 | `k8s-local-up.sh` の ESO=1 分岐へ apply を追加。供給一覧の echo と確認コマンドにも `postgres-app` を足し、**導出値「MSP ns は常時 5 本」を 6 本へ計算し直した**（母集合の規則 7） |
| 4 | `k8s-local-up.test.js` へ対の 2 本（ESO=1 で ExternalSecret を apply し手動 apply はしない／既定では手動 apply する） |

**変異試験（実測）**: ESO=1 側の apply 行を落とすと
`externalsecret-postgres-app.yaml が apply されない…` で fail、手動 apply をブロックの外へ出すと
`ESO=1 なのに postgres-app を手動 apply している（二重所有）` で fail。**両方向とも試験が捕まえる。**
テストは 97 → **99** 件。

### 一般化しなかった判断

「`ESO != 1` ブロック内の `apply_secret` は、対応する `externalsecret-<name>.yaml` の実在と apply を伴う」
という**横断の機械検査は置いていない**。この対応漏れは**今回が 1 回目**であり、
`CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**を条件とする（1 回目は記録に留める）」に従う。
**2 回目が起きたら一般化すること**を `k8s-local-up.test.js` の当該コメントへ書いた（記録の所在を 1 箇所にする）。

## 申し送り

- 🔴 **RabbitMQ の `amqp://guest:guest@`（13 箇所）は残件**である。baseline に凍結して**増やせない**が、
  撤去には**ブローカ側の資格情報変更を伴う配備作業**が要るため本 issue の射程から外した。**別 issue を起票する。**
- helm の注入は `$(DB_PASSWORD)` 補間を使う（env の値に平文パスワードを描画しない）。
  **k8s の `$(VAR)` は同一 container 内で先に定義した env しか参照できない** —— 順序を崩さないこと。
- 本環境では helm のレンダリング検証ができない（helm / kubectl 不在）。**CI の `check-deploy-manifests` に委ねる。**
