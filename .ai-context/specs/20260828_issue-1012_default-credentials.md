---
title: 作業仕様書 — 既定資格情報の排除と構成注入漏れの fail-fast 化（#1012）
type: spec
status: draft
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

## 変異試験（実測は締めのコミットまでに本節へ追記）
