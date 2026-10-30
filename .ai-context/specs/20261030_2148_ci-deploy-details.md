---
title: 作業仕様書 — CI と配備の細部（雛形ビルドの門・Loki の derivedFields・手順書の導出値・床の注記）を直し、残りを受容として記録する（#2148）
type: spec
status: done
related_ids: [NFR-21, ADR-0006, ADR-0007, IADR-0232, IADR-0507, IADR-0656, IADR-0657]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2148"
---

# 作業仕様書 — CI と配備の細部（#2148）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `5a9cee5c`）。親は #2047（監査の L-19）。方針はコーディネータの指示（2026-10-30）による。

## 実測（develop `a0d166b1` / `5a9cee5c`。読み取りのみ）

| # | 項目 | 実測 | 扱い |
| --- | --- | --- | --- |
| 1 | `template-backend-build` が `build-and-test` の `needs` に無い | 全 PR で起動（`ci.yml` の `pull_request` に `paths:` 無し）、所要 27 秒（run 41017702568。`backend-build` の脚は 1.5〜2.7 分） | 是正: `needs` に足し、判定ステップで success 以外を落とす。律速にならない。必須 check 名は変えない |
| 2 | Loki のデータソースに `derivedFields` が無い | compose・k8s とも無い（Tempo → Loki の `tracesToLogs` だけ）。collector は 2 経路とも旧 `loki` exporter（JSON 行。`traceid` のキー） | 是正: `"traceid":"(\w+)"` → Tempo の derivedField を 2 か所に置く。🔴 実ログの書式はローカルで測れない（Docker 無し）ので、配備時に Explore で確かめる手順を運用仕様書に書く |
| 3 | 手順書の導出値 | 下表 | 是正: 数を書き直すのではなく、導出の手順（どのファイルの何を数えるか）に置き換える |
| 4 | 「床を PR に当てると必ず割れる」 | PR の `build-and-test` の実測 line 95.07% / branch 85.19%、床 90 / 75。現状は割れない | 是正: 注記の根拠を事実に直す（`--report-only` は維持） |
| 5 | 床の PR での強制化 | IADR-0232 改定 3 を改める判断 | 対象外として記録 |
| 6 | 恒久 skip・SC-13〜16 の E2E | `WolverineBrokerEdgeTests.cs` の `Assert.Skip`（外部ブローカ未設定時）は IADR-0507 で受容。SC-13〜16 は #2191 の `keycloak-theme-a11y.yml`（`sc13-16-theme-a11y.kc.ts`）で入った | 受容・解消済みとして記録 |
| 7 | compose で `ResetFloorUpSeriesAbsent` / `MailRelayQueueSeriesAbsent` が恒常発火 | compose に床の器も近接 MTA も居ない既知の状態（`otel-collector-config.yaml` 冒頭・`alerts.yml` に明記）。compose の Alertmanager の既定はどこへも送らない | 受容として記録 |
| 8 | Argo CD `targetRevision: main` | `main` の最終は 2026-06-26、develop より 1314 コミット遅れ。既定ブランチは develop | 事実だけを `application.yaml` のコメントと `deployment.md` に明記。方針は要裁定として issue に残す |
| 追 | IADR-0656 の「`IsEnabled` は Error 以上・費用はエラーログ数に比例」 | #2361 で Information 以上を受けるようになった（#2363 の AI レビュー 🟢） | IADR-0656 に経過追記、`RetryBlockErrorMetrics.cs` に費用の一言 |

### 3 の内訳（手順書の導出値）

| 文書 | 記述 | 実体 | 置き換え |
| --- | --- | --- | --- |
| `paired-secret-rotation-runbook.md` 群 1 | OIDC 8 ＋ サービス間 9 ＋ 投入器 1 ＝ 18 | `deferred[].vaultPaths` は 20（OIDC 10。`mcp-client-admin-oidc`・`synthetic-monitor-slo-oidc` が抜け） | 件数は `sc22-secret-items.json` の `deferred[].vaultPaths` を数える式に |
| `secret-rotation-runbook.md` | `deferred[]`（18 項目） | 20 | 同上 |
| `secret-item-console-injection-runbook.md` | 28 項目・24 KV | 書く KV は 30 余、作るだけの KV は 26（`deferred` 20 ＋ `excluded` 6） | 「すべての項目」と、数え方（`vkv_create_if_absent` の行・`deferred`＋`excluded`） |
| `bootstrap.sh` の終わりの案内 | 25 KV | 26 | 数を出力時に数える |
| `bff-multi-replica-session-runbook.md`・`check-bff-multi-replica-session.js` の注記 | 既定の env 36 件（72 行） | helm の描画で数える値（helm は使わない約束なので未測定） | 数を書かず、描画で数える旨に |
| `operations.md` 呼び出し側の期限の表 | 方針の改訂: pin `58fe8c24` で 60 秒・95 秒は pin に未反映 | pin `e3254f03` の既定は 95 秒（`ReportService/Program.cs` `DefaultPolicyRevisionTimeoutSeconds`） | 行を直し、表の値の出所（各行の設定キーの既定）と引き直し方を書く |

## 母集合（規則 9・10）

- 誤りの側の文字列で走査した: `git grep -nE "env 36|36 件|72 行|24 KV|28 項目|サービス間 9|OIDC クライアント [0-9]|18 項目|25 KV|必ず割れる|targetRevision"`（`.ai-context/specs` と CHANGELOG を除く）。
  - 上表の各所のほか、`IADR-0232` の「必ず割れる」（日付つき追記で現状を併記）。
  - 除外: `IADR-0485`（24 KV。当時の記録）、`IADR-0118`・`coverage-floor.json`・`IADR-0236` の「72 行」（別の話）、`coredns-edge-hosts.yaml` の「OIDC クライアント 7 つ」（hostAliases を採らない理由の例示で、件数の正ではない。数は消して例示に留める）。
- 規則 10: 新しく書く数え方のコマンドは、実測で上表の「実体」と同じ値を返すことを確かめる。

## 受け入れ基準

- [x] 1〜4・追は是正、5〜7 は issue と本仕様書に受容の記録、8 は事実の明記と issue の要裁定
- [x] `check-grafana-provisioning-parity`・repo テスト・trace ブロックの検査が緑

## 残余リスク

- derivedFields の正規表現は実ログで未確認（配備時の手順で確かめる）。
- 床は PR で強制されないまま（5）。
