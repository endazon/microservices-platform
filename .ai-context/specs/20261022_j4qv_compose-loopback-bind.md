---
title: 作業仕様書 — compose がホストへ出す口（ports:）をループバック（127.0.0.1）に限り、CI で全件を検査する（GHSA-j4qv-2w99-xg5p）
type: spec
status: done
related_ids: [NFR-09, NFR-18, ADR-0005, ADR-0112, IADR-0587, IADR-0017, IADR-0026, IADR-0032, IADR-0403, IADR-0586]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md（多層防御の前提）
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md（管理用の口の到達の制限）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — compose がホストへ出す口をループバックに限る（GHSA-j4qv-2w99-xg5p）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0587**（新規）に置く。
> 多層防御の強化（最小の公開面）。IADR-0586 の残余 2（経路 A の公開ポート）を扱う後続である。参照は GHSA の ID だけとする。
> 利用者承認: 2026-10-22（「別件で是正」）。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-09**・**NFR-18**。計画 ADR: **ADR-0005**（ネットワーク分離は多層防御）・**ADR-0112**（管理用の口の到達の制限）。公開する口の集合は変えないので矛盾しない。
- 実装 ADR: **IADR-0587**（新設）。前提 **IADR-0017** / **IADR-0026**（内部サービスは `expose`）・**IADR-0032**（Wiki.js の dev 公開）・**IADR-0403** 決定 6（host 公開してよい縁）・**IADR-0586**（残余 2）。

## 要件

1. `deploy/docker-compose.yml` のホストへ出す口を、全インターフェースからループバック（`127.0.0.1:HOST:CONTAINER`）へ限る。
2. 利用者向けの文書・CI がホスト外からの接続を前提にしていれば追随する。
3. 回帰防止の検査を既存の compose 検査に足す（変異で赤を確かめる。空の母集合を緑にしない）。

## 母集合（規則 9・10）

走査: `git ls-files | grep -iE 'compose'`（compose ファイルの発見）、各 compose の `ports:` / `expose:` / `network_mode` / `host_ip` / `published` を全文で、`templates/**`（compose なし）、`src/*/deploy/**`（該当なし）、submodule の取引ユニットは AST の `origin/develop` の `docker-compose.yml` を読んだ（変えない）。消費側は `.github/workflows/*.yml` の `docker compose`・`scripts/**` の `docker-compose.yml` 参照・`docs/` と `README.md` の `localhost:<port>` / `0.0.0.0` / LAN の語。

### compose の口（`deploy/docker-compose.yml`。18 件・全件を変更）

| サービス | 変更前 | 変更後 | 分類 |
| --- | --- | --- | --- |
| postgres | `5432:5432` | `127.0.0.1:5432:5432` | 開発便宜のインフラ |
| rabbitmq | `5672:5672`・`15672:15672` | `127.0.0.1:` 付き | 同上 |
| keycloak | `8080:8080` | `127.0.0.1:8080:8080` | 同上（ブラウザの OIDC 経路 `http://localhost:8080`） |
| qdrant | `6333:6333`・`6334:6334` | `127.0.0.1:` 付き | 同上 |
| otel-collector | `4317`・`4318`・`8888` | `127.0.0.1:` 付き | 同上 |
| prometheus | `9090:9090` | `127.0.0.1:` 付き | 同上 |
| alertmanager | `9093:9093` | `127.0.0.1:` 付き | 同上 |
| loki | `3100:3100` | `127.0.0.1:` 付き | 同上 |
| tempo | `3200:3200`・`4327:4317` | `127.0.0.1:` 付き | 同上 |
| grafana | `3000:3000` | `127.0.0.1:` 付き | 同上 |
| wiki-js | `3001:3000` | `127.0.0.1:3001:3000` | dev 便宜（IADR-0032） |
| bff | `5000:8080` | `127.0.0.1:5000:8080` | エッジ（IADR-0403 決定 6） |
| frontend | `3100:8080` | `127.0.0.1:3100:8080` | エッジ（同上） |

- 既に `127.0.0.1` のもの: 0 件。長い形式（`published` / `host_ip`）・流れ形式・`network_mode: host`: 0 件。
- **公開が意図的に必要な（ホスト外から繋ぐ）もの: 0 件。** ホスト外からの接続を前提にした手順・CI は見つからなかった（下表）。
- `expose:`（コンテナ網の中だけ）の 19 サービス（seaweedfs の `8333`・opt-in の embedding を含む）は対象外（ホストへ出さない）。

### 消費側

| 場所 | 前提 | 扱い |
| --- | --- | --- |
| `.github/workflows/images.yml` | `docker compose config` / `build` だけ（起動しない） | 影響なし |
| `.github/workflows/ci.yml`・`image-mapping.yml` | compose を字面で読む（build 定義） | 影響なし |
| integration / e2e | Testcontainers・Playwright。compose を起動しない | 影響なし |
| `scripts/k8s-local-images.sh`・`check-image-mapping.js` | compose の build 定義だけを読む（経路 B は compose を起動しない） | 影響なし |
| `NetworkIsolationTests`（knowledge の Deployment 試験） | `ports:` の有無と `5000:8080`・`3001:3000` の部分一致 | 新しい書式でもそのまま通る（変えない） |
| `docs/how-to/local-development.md` §起動後のエンドポイント | `http://localhost:<port>`（同じホスト） | ループバック限定の注記と、IPv6 の `localhost` の詰まりを足す |
| `docs/security/security.md` 多層防御の compose の行 | host 公開の範囲 | 追記ブロックでループバック限定と検査を書く |
| `README.md`・`docs/how-to/deployment.md`・`docs/operations/operations.md` | `localhost` の URL・`docker compose exec` | 影響なし（同じホスト・コンテナ内） |
| compose 内の `0.0.0.0`（seaweedfs の `-s3.ip.bind`）・`deploy/*.yaml` の受け口 `0.0.0.0` | コンテナの中の待ち受け | 対象外（ホストへの公開は `ports:` が決める） |

### 対象外（報告のみ）

- **取引ユニット（submodule・AST の `docker-compose.yml`）**: `postgres`（`5432:5432`）・`rabbitmq`（`5672:5672`・`15672:15672`）・`keycloak`（`8081:8080`）の 4 件が IP を省いた形である。MSP からは変えない。
- **既存の衝突（本件の範囲外）**: `loki` と `frontend` がどちらもホストの `3100` を使う（変更前から。同時に起こすと後発が bind に失敗する）。ポート番号は変えない。

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | compose の 18 の口がすべて `127.0.0.1` で待ち受ける | `docker compose -f deploy/docker-compose.yml config --format json` の `host_ip` が全件 `127.0.0.1`（手元実測）・`check-deploy-manifests.js --self-test`「実物の compose は…（陽性対照）」 |
| AC2 | IP の省略・`0.0.0.0`・変数展開・`host_ip` の無い長い形式・`network_mode: host`・読めない形を落とす | 同「短い形式・流れ形式・長い形式・network_mode を形ごとに判定する」 |
| AC3 | 実物の 1 件から `127.0.0.1` を外すと赤（変異） | 同「…1 件から 127.0.0.1 を外すと赤（変異）」・変更前の compose で本走査が 18 件の失敗（手元実測） |
| AC4 | 口が 1 件も数えられない（compose が無い・`ports:` が無い）ときは赤 | 同「口を 1 件も数えられない compose（空の母集合）は赤」 |
| AC5 | ツール（helm / kubectl / kubeconform）が無くても見る | 本走査は `check()` のツール判定より前で要点 8 を回す（`DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` でも失敗を返すことを変異で実測） |

## 影響範囲

- 変更: `deploy/docker-compose.yml`・`scripts/lib/compose-host-ports.js`（新設）・`scripts/check-deploy-manifests.js`（要点 8）・`scripts/lib/product-egress-defaults.js`（`splitComposeServices` の公開）・`scripts/README.md`・`docs/how-to/local-development.md`・`docs/security/security.md`・IADR-0587・索引。
- PoC（経路 B・k3s）: compose を起動しないので影響なし。配備前の手順は不要。
