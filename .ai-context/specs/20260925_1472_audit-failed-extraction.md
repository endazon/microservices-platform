---
title: 監査の抽出側が outcome=failed を拾うかを確かめる（SC-22 残作業の項目 3）
issue: "#1472"
type: spec
status: draft
related_ids:
  - SC-22
  - FR-15
  - ADR-0095
  - ADR-0004
  - IADR-0453
  - IADR-0216
adr_refs:
  - IADR-0453
  - IADR-0216
author: Claude Opus 5.5 (worker)
created: 2026-09-25
updated: 2026-09-25
---

# 作業仕様書: 監査の抽出側が `outcome=failed` を拾うかを確かめる（#1472 項目 3）

## 起点

- issue #1472 項目 3 ＝ IADR-0453 フォローアップ 4:「SC-22 は監査の `outcome` に `failed` を足した。可観測性基盤の抽出側が
  2 値前提なら追随が要る。**実装側で確かめられる**」。
- **本作業の射程は項目 3 だけ**。項目 1（`deferred[]` を画面で扱うか）・項目 2（退避手段の使用記録）は計画の判断待ち、
  項目 4（稼働クラスタでの疎通 T-40）は次のクラスタ構築時。いずれも本 PR では触らない（issue は閉じない。`Refs #1472`）。

## 走査した母集合

「2 値前提の抽出」を探すので、**誤りの側＝`granted` / `denied` だけを列挙する書き方、および監査属性を名指しする箇所**から引く。

### 軸 1: 監査属性を名指しする箇所（リポジトリ全体・拡張子で絞らない）

```
$ git grep -n -iE "AuditOutcome|audit_outcome|audit\.outcome|auditoutcome" -- . ':!CHANGELOG.md' ':!src/ai-stock-trading'
.ai-context/specs/…（凍結記録 4 行）
src/platform/backend/Bff/Platform.Bff.Tests/PlatformLoggingTests.cs:159
src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/Audit/AuditLoggerTests.cs:57 / :109
src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Audit/AuditLogger.cs:31
```

- **記録する側（`AuditLogger`）とその試験だけ**。抽出する側（クエリ・ルール・ダッシュボード・収集器の処理）は 0 件。

### 軸 2: 可観測性基盤の設定（パスから引く）

```
$ git grep -n -E "Audit|audit" -- deploy docs/observability
deploy/local/infra/postgres.yaml:53: CREATE DATABASE audit_svc OWNER ai;   ← AST の監査サービスの DB（別物）
$ git grep -n -iE "outcome|granted|denied" -- deploy
（監査と無関係の 7 行: DB 権限エラーの注記・ABAC の Granted・LLM 利用ダッシュボードの egress_denied・合成監視の usage_event_outcome）
```

- Grafana ダッシュボード（`deploy/grafana/provisioning/dashboards/*.json`・`deploy/local/observability/grafana.yaml`）・
  Prometheus / Loki の設定に、監査を抽出するパネル・ルールは**無い**。Loki の ruler も無い（`ruler|alerting_rules` 0 件）。

### 軸 3: 収集器のログ経路（抽出の前段で落としていないか）

- `deploy/otel-collector-config.yaml`・`deploy/local/infra/otel-collector.yaml`・
  `deploy/local/observability/otel-collector-forward.yaml` の `logs` パイプラインは、いずれも processors が
  `memory_limiter, batch` だけ。**filter / transform / attributes の処理は無い**ので、`AuditOutcome` の値で落とす・
  書き換える段は無い。

### 軸 4: 文書に書かれた抽出クエリ（LogQL）

```
$ git grep -n -E "\| json|\|= |\{service_name|\{job=|logql|LogQL" -- . ':!CHANGELOG.md' ':!src/ai-stock-trading' ':!*.lock'
（PromQL の `up{job=…}` 等のみ。LogQL の監査抽出は 0 件）
```

### 軸 5: 記録側の値域（`outcome` はもともと 2 値か）

```
$ git grep -h -oE "\.Record\([^;]{0,200}" -- 'src/**/*.cs' ':!**/Tests/**' ':!**/*.Tests/**' ':!src/ai-stock-trading' \
    | grep -oE "\"[a-z_-]+\"" | sort | uniq -c | sort -rn
  15 "granted" / 10 "denied" / 9 "failed" / "recorded" 1 / "reached" 1（ほか action・subject のリテラル）
```

- **SC-22 より前から 2 値ではない**: `private-note.sync.conflict` は `recorded`、通知の送信上限は `reached`、
  `EmailOutboxDispatcher.Record` は送信の結末をそのまま `outcome` に入れる。
- ところが `IAuditLogger` の注記は `outcome: 結果（"granted" / "denied"）` と 2 値で書かれている（**古い**）。
  試験 `AuditLoggerTests.許可も拒否も同じ形で記録する` も `granted` / `denied` の 2 つしか固定していない。

## 結論

- **抽出側に 2 値前提のものは無い。そもそも本リポジトリに監査の抽出クエリが無い**（軸 1〜4）。収集器は監査のログを値で
  落とさず Loki へ送るので、`outcome=failed` の記録は他の値と同じ経路で可観測性基盤へ届く。**追随すべき抽出側は存在しない。**
- 2 値前提が残っていたのは**記録側の注記と試験**である（軸 5）。これを直す:
  1. `IAuditLogger` の注記を実際の値域に合わせる（`granted` / `denied` / `failed` ほか。値域は閉じていない）。
  2. `AuditLoggerTests.許可も拒否も同じ形で記録する` に `failed` を足す（失敗も同じ形で記録されることを固定する）。
  3. `docs/security/security.md`「監査ログ」に、**抽出するときは `Audit=true` で絞り、`outcome` を 2 値で列挙しない**ことを書く
     （将来クエリを足す人への追随の指示。現時点で抽出クエリが無いことも書く）。
  4. IADR-0453 フォローアップ 4 に確認結果を追記して閉じる。
- **稼働クラスタの Loki で `failed` の行を実際に引くこと**は本作業ではしない（稼働クラスタは利用者の PoC を動かしており、
  本作業では読み取りも行わない）。項目 4（T-40）の場で、画面から失敗を 1 件起こして Loki で引けることを併せて確かめる
  ことを追記に残す。

## 対象範囲

- **対象**: `AuditLogger.cs`（注記のみ）／`AuditLoggerTests.cs`（`InlineData("failed")` を 1 行）／`docs/security/security.md`／
  `.ai-context/adr/IADR-0453_…`（追記ブロック）。
- **対象外**: 項目 1・2・4。抽出クエリ・ダッシュボードの新設（計画外。監査の運用設計は `docs/operations/` と可観測性基盤側の裁定事項）。

## 受け入れ基準

1. 母集合（抽出側の候補）を全軸で引き、抽出側に 2 値前提が無いことを根拠つきで記録している（本書）。
2. `AuditLogger` が `failed` を `granted` / `denied` と同じ形（Information・`AuditOutcome`・`Audit=true`）で記録することを試験が固定する。
3. `dotnet test` で `Platform.Shared.Infrastructure.Tests` が通る。
4. `docs/` の表示テキストに計画 ID・IADR・仕様書名・修飾付き issue 参照を書かない（trace ブロックへ）。

## 検証

- `dotnet test src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests --filter FullyQualifiedName~AuditLoggerTests`
- `dotnet format src/platform/backend/backend.slnx --verify-no-changes`（変更したファイルのみ整形差分が無いこと）
- scripts/README.md の既定検査（trace ブロック・リンク・コミット件名 ほか）
