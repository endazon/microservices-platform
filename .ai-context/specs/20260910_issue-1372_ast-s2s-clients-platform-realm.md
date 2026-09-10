---
title: AST の s2s クライアント 2 つと realm ロール trading-service を platform レルムへ足す（MSP 連結配備の認証レルム統一）
issue: "#1372"
plan_refs:
  - NFR-09
  - ADR-0005
  - ADR-0084
adr_refs:
  - IADR-0075
  - IADR-0197
status: done
created: 2026-09-10
---

# 作業仕様書: AST の s2s 客体を platform レルムへ足す（#1372）

## 起点

- issue #1372。統合 SPA から AST の画面（AST/SC-01〜SC-03）を開くと再読み込みの無限ループになる（2026-09-10 利用者報告）。
- 根本原因は AST 側 issue AST#727 と AST/IADR-0324 に記録した。要点: **AST サービスは AST レルムで JWT を検証しているが、
  BFF はセッションの MSP レルム（`platform`）のトークンを転送する**。上流が issuer 不一致の 401 を返し、SPA が
  セッション失効と解釈してログインへ飛び続ける。

```
MSP レルムのトークン → configuration-service GET /assumptions:
  401  WWW-Authenticate: Bearer error="invalid_token", error_description="The issuer 'https://keycloak.localhost/realms/platform' is invalid"
AST レルムのトークン → 同: 200
```

- compose は #283 決定 2e で「AST サービスの `Auth__Authority` は MSP レルム共通、`trading-owner` は MSP レルム側で定義」と
  決めて実装済み（`deploy/docker-compose.yml`）。k8s 側の AST chart が追随していなかった。

## 分担

| 側 | 変更 |
| --- | --- |
| AST（AST#727 / AST/IADR-0324） | `values-local.yaml` の `global.authAuthority` を `…/realms/platform` へ。chart は inbound 検証・`ServiceAuth`・CronJob・Discord OwnerAuth の token エンドポイントをこの 1 値から導出する |
| **MSP（本 issue）** | AST の s2s が MSP レルムへ移るのに要る客体を realm 宣言へ足す |

## 設計

`deploy/keycloak/microservices-platform-realm.json` へ、#1368（`ai-stock-trading-llm-caller`）と同型で足す。

| 客体 | 内容 | 由来 |
| --- | --- | --- |
| realm ロール `trading-service` | AST の読み取り専用 s2s ロール（`OwnerOrService`） | AST/IADR-0051 |
| client `ai-stock-trading-svc` | confidential・client_credentials のみ・service-account に `trading-service` | AST/IADR-0051 |
| client `ai-stock-trading-owner` | confidential・client_credentials のみ・service-account に `trading-owner` | AST/IADR-0098 |

- **dev secret は AST レルム（`src/ai-stock-trading/infra/keycloak/realm-export.json`）と同値**にする。稼働中の `ast-secrets`
  （`service-auth-*` / `discord-owner-auth-*`）が AST レルムの dev 値を持っており、同値にすれば Secret を触らずに移れる。
  本番 secret は従来どおり Vault 経由（既存クライアントと同じ扱い）。
- `description` は 255 文字以内（`check-realm-constraints.js`。#1368 で 430 文字が SQLSTATE 22001 で落ちた）。
- 稼働クラスタへの反映は `deploy/local/keycloak-setup/reconcile-realm.sh`（IADR-0369）。ConfigMap `keycloak-realms` を
  実ファイルから作り直してから走らせる。既存の人間の利用者（`developer` の資格情報・requiredActions）は実行時所有で触らない。

### 採らなかった案

- **AST レルムをそのまま使い、AST サービスで二重 issuer を受ける**: AST 側のコード変更が要り、`developer` が両レルムに
  居る曖昧さを残す（AST/IADR-0324 選択肢 B）。
- **secret を新規に振る**: `ast-secrets` の更新と AST 側の再投入が要り、変更が 2 リポ 3 箇所に広がる。dev 値の同値化で足りる。

## 走査した母集合（規則 2・9）

「realm のクライアント／service account を列挙している箇所」を `ai-stock-trading-kb-writer` で全走査した
（除外: `src/`（submodule）・`node_modules`・`.git`・`.claude/`・`CHANGELOG.md`・`.ai-context/specs/`）。

| 箇所 | 扱い |
| --- | --- |
| `deploy/keycloak/microservices-platform-realm.json` | **変更**（本件の実装点） |
| `docs/security/security.md:184-188`（クライアント一覧） | **変更**（2 件を足す。表示テキストへ ID を書かず trace ブロックへ） |
| `scripts/check-realm-constraints.js:93`（redirect 検査の対象外リスト） | **確認**: service account 専用で redirect を持たないクライアントは対象外とする規則。新 2 件も `redirectUris: []` なので規則どおり素通りするか、実行して確かめる |
| `.ai-context/adr/IADR-0075` / `IADR-0197:61`（「他の 8 クライアント」）/ `IADR-0373` / `IADR-0420` | **除外**: 凍結記録（当時の数） |
| `deploy/local/synthetic-monitor/README.md:55`・`docs/operations/local-sso-recovery-runbook.md:68` | **除外**: kb-writer 固有の記述 |
| `scripts/seed-tag-dictionary.js:16` | **除外**: kb-writer の権限に関する注記 |
| `scripts/keycloak-realm-reconcile.test.js:105`（`clients.length >= 5`） | **確認**: 下限検査。増える分には落ちない |

## 受け入れ基準

- [x] `node scripts/check-realm-constraints.js` が緑
- [x] `node scripts/keycloak-realm-reconcile.test.js` が緑（34 tests passed）
- [x] 稼働クラスタで `reconcile-realm.sh` が収束し、`ai-stock-trading-svc` の client_credentials で MSP レルムのトークンが取れ
      `realm_access.roles` に `trading-service` が載る（値は出さない）
- [x] AST 側の values 反映後、MSP レルムのトークンで AST の `GET /assumptions` が 200、AST レルムのトークンは 401（反転を確認）
- [x] `docs/security/security.md` に 2 件を足し、`check-trace-blocks.js` が緑

## 計画書との差異

- 差異なし。ADR-0005 / ADR-0084 の枠内で客体を足すだけ。「MSP 連結配備では AST の利用者認証を MSP レルムで行う」は
  AST 側の計画に無い前提なので、AST 側から planning へ環流する（本リポからは行わない）。

## 実施記録（2026-09-10・稼働クラスタ）

- `reconcile-realm.sh` は最初 `bff` の `service-account-user` で 400 になり止まった → #1373 として切り出して直した。
- 修正後の `--check` は drift=35（本件の 3 客体に加え、宣言済みで live に無かった east-west の s2s クライアント 11・
  `reset-gate`・`synthetic-monitor`・`ai-stock-trading-llm-caller`、`platform-service` ロール、`bff` の SA 有効化、
  `smtpServer`、`abac-attributes` の mapper 2）。apply で `realms=2 drift=0 applied=35` に収束した。
- platform レルムの `ai-stock-trading-svc` で client_credentials → `realm_access.roles` に `trading-service`。
  AST の values 反映後、その token で AST の `GET /assumptions` が 200（AST レルムの token は 401）。
