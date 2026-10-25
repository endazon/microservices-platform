---
title: 作業仕様書 — 基盤のサーバ証明書の更新を作り直しで読ませる（IADR-0591 の後続。GHSA-j4qv-2w99-xg5p）
type: spec
status: in-progress
related_ids: [NFR-16, NFR-18, ADR-0023, IADR-0591]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-16 通信暗号化）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — 基盤のサーバ証明書の更新を作り直しで読ませる（GHSA-j4qv-2w99-xg5p）

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は IADR-0591 の日付つき追記に置く。参照は GHSA の ID だけとする。

## 起点

- #2094（IADR-0591 段 4b-1）の AI レビューの 🟡: postgres・rabbitmq はサーバ証明書を起動時にしか読まない（init コンテナが写す）。そのため cert-manager が更新した証明書が使われない。
- 同じレビューの 🟢: `UsePlatformRabbitMq` が素の `UseRabbitMq(Uri)` と同じ設定をしているかが未確認。
- 計画: NFR-16・ADR-0023。計画との矛盾は無い。

## 範囲

- 起動器（`scripts/k8s-local-up.sh`）: 基盤の apply の後に、Pod の雛形へ証明書の指紋を刻む。指紋が変われば作り直す。
- 試験: 起動器（刻む位置・未記録なら刻む・同じなら何もしない・中身を引数に載せない）と、.NET の共通ヘルパの同等性。
- 文書: `deploy/local/README.md`・`deploy/local/infra-tls/certificates.yaml` の注記・IADR-0591 の追記。
- 範囲外: Reloader などによる自動の作り直し。4b-2 以降の段。

## 母集合

- 証明書を起動時に読むもの: `deploy/local/infra/{postgres,rabbitmq}.yaml` の init コンテナ（`git grep -n "tls-src" -- deploy`）の 2 件。compose の `infra-tls-init` は更新しない（作り直すときはボリュームを消す。README に記載済み）ので対象外。

## 受け入れ基準

- [x] 既定の起動で、基盤の apply の後・rollout の待ちの前に、postgres・rabbitmq へ指紋の注釈を刻む（指紋が未記録のとき）。
- [x] 指紋が同じなら刻まない（再実行のたびに作り直さない）。
- [x] 証明書の中身を `kubectl` の引数に載せない。
- [x] 共通ヘルパは、受け入れてよい誤り以外を素の `UseRabbitMq(Uri)` と同じに設定する（amqps / amqp）。
- [ ] 統合スタック（`ISTIO=1`）を PR のブランチで流し、門がすべて緑。

## 影響

- 新しく立てるクラスタでは、注釈を刻むことで postgres・rabbitmq の作り直しが 1 回増える。
- 稼働のクラスタでは、初めて当てる再実行で 1 回作り直される（AST の接続も一度切れる）。以後は証明書が変わったときだけ作り直す。
