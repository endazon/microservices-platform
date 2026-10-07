---
title: 作業仕様書 — BFF の DataProtection 鍵リング共有を 2 つの WebApplicationFactory で統合テストに固定する（#1780）
type: spec
status: done
related_ids: [NFR-07, NFR-09, ADR-0032, IADR-0251, IADR-0273, IADR-0510]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1780"
---

# 作業仕様書 — BFF の鍵リング共有を 2 ホストの統合テストで固定する（#1780）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `b3208a1d`。
> 計画は project-planning `aa068ac`（隣接クローン・読み取り専用）。

## 起点（トレーサビリティ）

- NFR-07（スケーラビリティ。HPA による水平スケール。#1534 の起点と同じ）。計画 ADR-0032（BFF セッション方式・セッションストアは Redis）。
- IADR-0251 決定 5（DataProtection の鍵リングを Redis に共有する）・IADR-0273（セッションの失効・refresh）。本文は凍結記録として書き換えない。
- #1534（`blocked:env`）の受け入れ基準 1（「AI で先行できる」）の切り出し。#1773 の独立監査の指摘による。
  稼働クラスタでの確認（Runbook・`scripts/check-bff-multi-replica-session.js`。#1736）は #1534 に残す。

## 受け入れ基準

- AC1: 鍵リングを共有した 2 つのホスト（`WebApplicationFactory<Program>` を 2 つ）の間で、セッション Cookie を**相互に**復号できる
  （A が発行した Cookie で B の `/bff/auth/me` が 200・同じ `sub`、逆向きも同じ）。
- AC2: 鍵リングを共有しない場合は、B が A の Cookie を復号できない（401）ことを陰性対照として固定する。
  A 自身では 200 になること（Cookie が壊れているのではない）も併せて確かめる。
- AC3: 鍵リングの保存先の設定を変えて共有をやめると、テストが落ちる（変異で確かめ、結果を本書と PR に記録する）。
- AC4: テストは本番の配線（`AddBffSession` の DataProtection と鍵の保存先）をそのまま通す。テスト側で配線を書き直さない。
  差し替えるのは I/O の器（Redis への接続）だけにする。

## 設計

### 何を差し替え、何を本物のまま通すか

| 部品 | 扱い |
| --- | --- |
| `Program`（`AddBffSession` を含む全配線） | 本物。`BffTestFactory` を継いだ `WebApplicationFactory<Program>` を 2 つ立てる |
| `SetApplicationName("microservices-platform-bff")` | 本物 |
| 鍵の保存先（`RedisXmlRepository`、Redis キー `bff:dataprotection-keys`） | 本物 |
| Redis への接続（`IConnectionMultiplexer`） | **偽物**。プロセス内のリスト（`ListRange` / `ListRightPush` だけを実装）。共有する場合は 2 ホストが同じ実体を引く |
| セッション本体（`IDistributedCache`＝チケットストアの裏） | メモリ。陽性・陰性の両方で 2 ホストが**同じ**実体を共有する（変える変数を鍵リングだけにするため） |
| セッション Cookie の発行 | テスト専用の入口（`IStartupFilter` で前置）が本物の Cookie ハンドラへ `SignInAsync` する |

### 本番の配線を 1 点だけ変える（IADR-0510）

現状の `PersistKeysToStackExchangeRedis(() => lazyRedis.Value.GetDatabase(), ...)` は、Redis 接続を**クロージャに閉じ込めて**いる。
DI に登録した `IConnectionMultiplexer` とは別の経路で同じ `Lazy` を引くため、テストは接続を差し替えられない。
差し替えられないままテストを書くと、鍵の保存先をテスト側で設定し直すことになり（AC4 違反）、本番の保存先の設定を変えてもテストは落ちない（AC3 を満たせない）。

そこで、鍵の保存先は**DI の `IConnectionMultiplexer` から引く**形へ変える。

- `KeyManagementOptions.XmlRepository` に `RedisXmlRepository`（拡張メソッドが内部で置くものと同じクラス）を置き、
  データベースの取得は `IServiceProvider` から `IConnectionMultiplexer` を解決して行う。
- 接続の遅延（登録時に Connect しない）は保つ。`IConnectionMultiplexer` の登録（`Lazy` 経由）はそのまま。
- Redis キーとアプリケーション名を公開定数にする（`DataProtectionKeysRedisKey` / `DataProtectionApplicationName`）。
  値は変えない（Runbook の `LLEN bff:dataprotection-keys` と一致させたまま）。

### テストの形

`BffKeyRingSharingTests`（`Platform.Bff.Tests`）:

1. **陽性**: 共有の鍵ストア 1 つで A → B の順に起動。A で発行した Cookie で B の `/bff/auth/me` が 200・同じ `sub`。
   B で発行した Cookie で A も 200。加えて、
   - 鍵は共有ストアの `bff:dataprotection-keys`（**リテラルで固定**。定数を引くと定数ごと変えた変異で素通りする）にちょうど 1 件
     （B は A の鍵を使い、自前の鍵を作らない）。
   - 両ホストのアプリケーション識別子（`DataProtectionOptions.ApplicationDiscriminator`）が `microservices-platform-bff`。
     2 ホストは同じプロセス・同じ content root なので、`SetApplicationName` を外しても既定の識別子が一致して復号は通ってしまう。
     識別子を直接読まないと、この変異を捕まえられない。
2. **陰性対照**: 鍵ストアを 2 つに分けて同じ手順。A では 200、B では 401。
   各ストアに 1 件ずつ（それぞれ自前の鍵を作った）。

### 変異の計画

| 変異 | 期待 |
| --- | --- |
| M1: Redis キーをレプリカごとに変える（`bff:dataprotection-keys:` ＋ 登録時の GUID） | 陽性が赤（B が 401・キーに 0 件） |
| M2: 鍵の保存先の設定を外す（既定の保存先へ戻る） | 陽性が赤（共有ストアに 0 件）。陰性対照も赤（既定の保存先はファイルシステムで、同じマシンの 2 ホストは共有してしまう） |
| M3: `SetApplicationName` を外す | 陽性が赤（識別子の検査） |

## IADR

IADR-0510 を起こす（鍵の保存先を DI の接続から引く形へ変えた判断。拡張メソッドを使わない理由と、テストの射程）。

## 試験仕様書

`docs/tests/NFR-09_bff-edge-authentication.md` は BFF のセッション Cookie 経路（T-26）を持つ唯一の試験仕様書であるため、
同書に節を足し T-27（陽性・相互）・T-28（陰性対照）を置く。変異の表にも M1〜M3 を足す。

## 母集合（規則 9・10）

- 誤りの側の文字列（`dataprotection-keys` / `PersistKeysToStackExchangeRedis` / `SetApplicationName` / `BffSessionExtensions`）で
  `scripts/`・`docs/`・`deploy/`・`.ai-context/adr/` を走査: `scripts/README.md`・`scripts/check-bff-multi-replica-session.js`・
  `docs/operations/bff-multi-replica-session-runbook.md`・IADR-0359・IADR-0409 が当たる。
  いずれも Redis キーの名前（変えない）か過去の記録であり、拡張メソッド名に依存した記述は追随不要と判断した
  （走査結果は「結果」節に記す）。
- 「単体テストは `EphemeralDataProtectionProvider` を差し込んでいる」（#1534 本文）は `BffSessionFlowTests` のことで、本変更後も真のまま。

## 結果

### 実装

- `BffSessionExtensions.AddBffSession`: 鍵の保存先を `KeyManagementOptions.XmlRepository` ＝ `RedisXmlRepository`（DI の `IConnectionMultiplexer` から
  データベースを引く）へ。定数 `DataProtectionKeysRedisKey` / `DataProtectionApplicationName` を追加（値は不変）。
- `BffKeyRingSharingTests`（新規）: T-27（陽性・相互）・T-28（陰性対照）。`BffTestFactory` を継いだ `WebApplicationFactory<Program>` を 2 つ立てる。
  偽 Redis（`InMemoryRedisLists`）は `DispatchProxy` で `ListRange` / `ListRightPush`（と非同期版）だけを実装し、他は `NotSupportedException`。
- IADR-0510 を起こし索引へ追記。`docs/tests/NFR-09_bff-edge-authentication.md` に T-27・T-28 と変異 I〜K を追記（trace ブロックへ IADR-0510・本仕様書・#1534 / #1780）。

### 検証（2026-10-08）

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/platform/backend/backend.slnx` | 成功（警告 0・エラー 0） |
| `dotnet test src/platform/backend/backend.slnx` | 全 7 プロジェクト緑。`Platform.Bff.Tests` 867 合格・1 スキップ（既存）・0 失敗 |
| `dotnet test ... --filter FullyQualifiedName~BffKeyRingSharingTests` | 2 合格 |
| `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | 差分なし（EXIT 0） |
| `node scripts/scripts.test.js`（固有テスト込み。`check-test-spec-coverage` の床を `--update` で 1 件上げた）・node の文書検査（`check-trace-blocks` / `gen-knowledge-graph --check` / `check-reading-budget` / `check-doc-links` / `check-test-traceability` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-type-vocabulary` / `check-commit-messages` / `check-adr-numbering` / `check-trace-followthrough`） | すべて OK（PR 本文に出力を載せる） |

ビルドには `src/ai-stock-trading` submodule（pin `58fe8c24`）の populate が要った（`Platform.Bff` が AST の BFF 端点を参照する）。

### 変異（2026-10-08 実測。各変異は 1 つずつ当て、測った後に戻した）

| 変異 | T-27（陽性） | T-28（陰性対照） |
| --- | --- | --- |
| M1: キー名に登録時の GUID を足す（レプリカごとに別キー） | **赤**（B が A の Cookie で 401） | **赤**（所定キーに 0 件） |
| M2: 保存先の設定（`AddOptions<KeyManagementOptions>()...`）を外す | **赤**（所定キーに 0 件） | **赤**（B が 200 —— 既定の `~/.aspnet/DataProtection-Keys` を 2 ホストが共有） |
| M3: `SetApplicationName` を外す | **赤**（識別子の検査。復号そのものは通る） | 緑 |

M2 は陽性の「復号できる」主張だけなら素通りする形であり、陰性対照と「所定キーに 1 件」の主張が捕まえた。
M2 の実行で既定の鍵ディレクトリ（実行前から 2026-09-27 作成の鍵 2 件が在った）に新しい鍵は増えていない（既存の鍵を使った）。

### 母集合の走査結果

`PersistKeysToStackExchangeRedis` 単独では、実装後に当たるのは本変更のファイル（BFF の配線のコメント・本書・IADR-0510）だけである。
拡張メソッド名に依存して誤りになる記述は無い（Runbook・検査器はキー名だけを見る。IADR-0359 / IADR-0409 は凍結記録）。
