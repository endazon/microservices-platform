---
title: IADR-0587 compose がホストへ出す口（ports:）はすべてループバック（127.0.0.1）に限り、check-deploy-manifests.js の要点 8 で全件を検査する
type: impl-adr
status: Accepted
related_ids: [NFR-09, NFR-18, ADR-0005, ADR-0112, IADR-0017, IADR-0026, IADR-0032, IADR-0403, IADR-0586]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md（多層防御の前提）
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md（管理用の口の到達の制限）
related_specs:
  - ../specs/20261022_j4qv_compose-loopback-bind.md
---

# IADR-0587: compose がホストへ出す口はループバックに限る（GHSA-j4qv-2w99-xg5p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: 利用者承認（2026-10-22。「別件で是正」）・claude（形の選択）

## 起点・関連

- 起点: GHSA-j4qv-2w99-xg5p（多層防御の強化。最小の公開面）。IADR-0586 の残余 2（経路 A の公開ポートは対象外）を本決定で扱う。
- 計画: **ADR-0005**（サービス間の第一防御は mesh。ネットワーク分離は多層防御）・**ADR-0112**（管理用の口は到達を制限する。本決定は経路 A でその到達の範囲を同じホストへ狭める）。要求 **NFR-09**・**NFR-18**。矛盾しない（公開するポートの集合は変えず、待ち受けるアドレスだけを狭める）。
- 前提: [IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md) / [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md)（内部サービスは `expose` のみ）・[IADR-0032](./IADR-0032_wikijs-dev-exposure-opt-in.md)（Wiki.js の dev 公開）・[IADR-0403](./IADR-0403_security-interim-posture-resolution.md) 決定 6（host 公開してよい縁の分類）。

## コンテキストと課題

`deploy/docker-compose.yml`（経路 A・dev）は、エッジ（BFF・フロント）と開発便宜のインフラ（Postgres・RabbitMQ・Keycloak・Qdrant・可観測性・Wiki.js）の計 18 の口をホストへ出している。書式は `"HOST:CONTAINER"` で、IP を省いた形は全インターフェースで待ち受ける。開発便宜として要るのは同じホストのブラウザ・CLI からの接続だけで、同じ LAN の別の端末からの接続を前提にした手順・CI は無い（作業仕様書の母集合）。経路 B（k3d）は既にホストの公開を loopback に固定している。

## 検討した選択肢

1. **口を個別に減らす（インフラの公開をやめる）** — 開発の手順（`psql`・管理 UI・Grafana への直接の接続）を壊す。公開の集合は IADR-0017 / IADR-0032 / IADR-0403 が決めており、本件の範囲を超える。採らない。
2. **既定の bind アドレスを変数で持つ（`${BIND_IP:-127.0.0.1}:HOST:CONTAINER`）** — 利用者が LAN へ広げる口を残せるが、値の展開を検査器が判定できず（値は実行時の環境に依存する）、「広げた」状態が字面に現れない。経路 B も広げる手段をリポジトリに持っていない。採らない。
3. **全件を `127.0.0.1:` で書き、検査器で全件を固定する（採用）** — 口の集合・ポート番号・コンテナ間の通信は変わらない。広げたい利用者は手元の上書き（`docker-compose.override.yml` 等、コミットしない）で行う。

## 決定

1. **書式**: `ports:` の項目は短い形式なら `"127.0.0.1:HOST:CONTAINER"`、長い形式なら `host_ip: 127.0.0.1` と書く。`network_mode: host` は使わない。
2. **検査**: `scripts/check-deploy-manifests.js` の要点 8（判定は `scripts/lib/compose-host-ports.js`）が、`deploy/docker-compose*.yml` の全サービスの `ports:` を読み、ループバック以外・IP の省略・`host_ip` の無い長い形式・変数展開・`network_mode: host`・読めない形を失敗にする。許す値は `127.0.0.1` の 1 つ（`::1` は使っていないので許さない。要るようになったら本決定を改める）。
3. **空の母集合を緑にしない**: 走査全体で口が 1 件も数えられなければ失敗にする（要点 2 と同じ）。ツール（helm / kubectl / kubeconform）が無くても見る。
4. **母集合の取り方は既存と同じ**: compose ファイルは `discoverComposeFiles`（`deploy/` 直下の `docker-compose*.yml`）で発見し、名前を列挙しない。

## 結果

- 良い影響: 経路 A の口は同じホストからしか届かない。公開の集合・URL（`http://localhost:...`）・コンテナ間の通信は変わらない。退行は CI の `check-deploy-manifests.js` で止まる。
- 悪い影響: 同じ LAN の別の端末から dev スタックへ繋ぐ使い方は、手元の上書きか SSH のポート転送が要る。`localhost` が IPv6（`::1`）にしか解決されないクライアントは `127.0.0.1` を指定する必要がある（how-to のよくある詰まりに記載）。

## 残余

1. submodule の取引ユニット（`src/ai-stock-trading`）の `docker-compose.yml` にも IP を省いた口がある。MSP からは変えない（取引ユニットのリポジトリで扱う）。
2. 既存の Deployment の試験（`NetworkIsolationTests`）は口の有無と番号を字面で見ており、本決定の書式でもそのまま通る。ループバックの固定は本検査器が持つ（試験側は変えない）。
