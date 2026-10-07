---
title: IADR-0510 BFF の DataProtection 鍵の保存先は DI の IConnectionMultiplexer から引く RedisXmlRepository を KeyManagementOptions に置き、2 つの WebApplicationFactory で共有と陰性対照を固定する
type: impl-adr
status: Accepted
related_ids: [NFR-07, ADR-0032, IADR-0251, IADR-0273]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
related_specs:
  - ../specs/20261008_1780_bff-keyring-shared-two-hosts.md
---

# IADR-0510: BFF の鍵の保存先は DI の接続から引き、2 ホストの統合テストで共有を固定する（#1780）

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: claude（#1780。#1534 受け入れ基準 1 の切り出し。作業仕様書 20261008_1780）

## 起点・関連

- 起点 issue: #1780（#1534 の受け入れ基準 1。#1773 の独立監査が「blocked の下で見落とされる」と指摘）
- 計画: ADR-0032（BFF セッション方式。セッションストアは Redis）。NFR-07（スケーラビリティ。HPA による水平スケール。#1534 と同じ起点）
- 前提の決定: IADR-0251 決定 5（DataProtection の鍵リングを Redis に共有する）。本文は書き換えない
- 基点コミット: `origin/develop` `b3208a1d`

## コンテキストと課題

- 鍵リングの共有は `BffSessionExtensions.AddBffSession` の 1 箇所で配線していた:
  `PersistKeysToStackExchangeRedis(() => lazyRedis.Value.GetDatabase(), "bff:dataprotection-keys")`。
- Redis 接続（`lazyRedis`）は**クロージャに閉じていた**。DI にも `IConnectionMultiplexer` として登録していたが、鍵の保存先は DI を通らない。
- そのため、テストが Redis の器を差し替える手段が無かった。既存の単体テスト（`BffSessionFlowTests`）は
  `EphemeralDataProtectionProvider` を差し込んで鍵リングごと迂回しており、「2 つのレプリカが同じ鍵リングを引く」ことは一度も測られていない（#1534）。
- 迂回のままテストを書くと、鍵の保存先をテスト側で設定し直すことになる。その場合、本番の保存先の設定を変えてもテストは落ちない。

## 検討した選択肢

| | A. 実 Redis（Testcontainers）で 2 ホスト | B. テストが `IXmlRepository` を差し替える | C. 保存先を DI の接続から引く形へ変え、接続だけを偽物にする（**採用**） |
| --- | --- | --- | --- |
| 本番の配線の変更 | 無し | 無し | 1 箇所（保存先の取り方） |
| 本番の保存先の設定を通るか | 通る | **通らない**（テストが置き直す） | 通る（`RedisXmlRepository`・キー名・アプリケーション名は本番が置く） |
| 「保存先を変えて共有をやめる」変異で落ちるか | 落ちる | **落ちない** | 落ちる（実測。下記） |
| 実行環境 | Docker が要る。BFF の単体テストの CI 経路に乗らない | 要らない | 要らない |

## 決定

1. **鍵の保存先は `KeyManagementOptions.XmlRepository` に `RedisXmlRepository` を置き、データベースは DI の `IConnectionMultiplexer` から引く。**
   置くクラスは `PersistKeysToStackExchangeRedis` が内部で置くものと同じである。データベースは使う時に解決し、登録時に Redis へ接続しない（従来の遅延を保つ）。
2. Redis キー（`bff:dataprotection-keys`）とアプリケーション名（`microservices-platform-bff`）を `BffSessionExtensions` の公開定数にする。値は変えない。
3. **共有は 2 つの `WebApplicationFactory<Program>` で固定する**（`BffKeyRingSharingTests`）。差し替えるのは Redis 接続（リスト操作だけを持つ偽物）とセッション本体の器（メモリ）だけ。
   - 陽性: 同じ偽 Redis を引く 2 ホストが互いの Cookie で `/bff/auth/me` 200。鍵は `bff:dataprotection-keys`（テストではリテラル）にちょうど 1 件。両ホストのアプリケーション識別子が固定値。
   - 陰性対照: 偽 Redis を分けると、B は A の Cookie で 401（A 自身では 200）。
   - アプリケーション識別子は直接読む。同じプロセスの 2 ホストは content root が同じで、`SetApplicationName` を外しても既定の識別子が一致して復号は通るためである。

## 変異の結果（2026-10-08 実測）

| 変異 | 結果 |
| --- | --- |
| M1: キー名をレプリカごとに変える（`bff:dataprotection-keys:` ＋ 登録時の GUID） | 陽性が赤（B が A の Cookie で 401）。陰性対照も赤（所定キーに 0 件） |
| M2: 保存先の設定を外す | 陽性が赤（所定キーに 0 件）。**陰性対照が赤（B が 200）** —— 既定の保存先（`~/.aspnet/DataProtection-Keys`）を同じマシンの 2 ホストが共有するため。陽性だけでは素通りする形を陰性対照が捕まえた |
| M3: `SetApplicationName` を外す | 陽性が赤（識別子の検査）。陰性対照は緑 |

## 結果・影響

- 本番の挙動は変わらない（同じリポジトリクラス・同じキー・同じアプリケーション名）。変わるのは、データベースの取得が DI の登録を通ることだけである。DI の `IConnectionMultiplexer` を差し替えると、鍵の保存先も一緒に差し替わる。
- 稼働クラスタでの確認（2 レプリカ・Pod 再起動）は #1534 に残る。本決定はプロセス内の 2 ホストまでしか測らない。鍵の自動ローテーション直後の挙動も測っていない。
