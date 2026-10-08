---
title: 作業仕様書 インフラのイメージ参照を変える配備は場中を避ける旨を運用仕様書に書く（#1822）
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-10-08
updated: 2026-10-08
---

# 作業仕様書 インフラのイメージ参照を変える配備は場中を避ける旨を運用仕様書に書く（#1822）

## 背景

#1813 の配備（2026-10-08 23:16 JST・場中）で platform-infra の Postgres・RabbitMQ・Keycloak が作り直され、AST 全サービスの DB 接続が一時的に切れた（PoC の報告）。

## 受け入れ基準

- [x] `docs/operations/operations.md` の Third-party イメージの節に、インフラのイメージ参照を変える配備は引け後に行う旨と理由を書く。trace ブロックに #1822 を足す。

## 母集合（規則 9・10）

- 規則 9: `grep -rn "digest" docs/operations` — 配備の時間帯に触れる記述は他に無い。年次点検の「digest の解決と更新」も同じ扱いと明記した。
- 規則 10: 新たに誤りになる記述なし。
