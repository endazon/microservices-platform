---
title: k8s-local-images.sh の MAPPING と compose の build 定義のドリフト機械検査（Issue #275）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0007
  - IADR-0066
  - IADR-0067
  - IADR-0068
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md (CI/CD・イメージ配布)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (非機能要件: 運用・保守)
related_specs:
  - "../adr/IADR-0068_image-mapping-drift-check.md"
  - "../adr/IADR-0067_service-image-build-ci-gate.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
---

# 仕様書: MAPPING ↔ compose build 定義のドリフト機械検査（Issue #275）

## 起点となる計画書（トレーサビリティ）

- 非機能要件(NFR): 運用・保守（配布物である資産の CI 検証）
- 関連 ADR: ADR-0007（CI/CD・GitOps。**コンテナイメージが配布単位**）
- 実装判断: [IADR-0068](../adr/IADR-0068_image-mapping-drift-check.md)（機械突合スクリプト＋独立ワークフロー方式の採択・根治案の却下）
- 関連: [IADR-0067](../adr/IADR-0067_service-image-build-ci-gate.md)（compose 単一情報源のイメージビルド検証。本 issue を「フォローアップ」として切り出した元）／[IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md)（`k8s-local-images.sh` の追加元）
- Issue: #275（本 issue）／Refs #268（PR #274 / IADR-0067）／Refs #266（IADR-0066）

## 目的・背景

`scripts/k8s-local-images.sh`（#266 / IADR-0066）は **独自の `MAPPING` 配列**として「chart-image : Dockerfile パス」の
対応表を持つ。一方 `deploy/docker-compose.yml` の `build` 定義は #268 / IADR-0067 で **イメージビルド検証の単一情報源**
となった。両者は別々のビルド対象リストで**二重管理**になっており、突き合わせ検査が無い。

- `MAPPING`（12 件）と compose の `build` 定義（13 件）は既に差がある。差分は `frontend`
  （`src/platform/frontend/Dockerfile`）のみ。
- `images.yml` のゲートは **compose 側のみ**を担保するため、`MAPPING` が腐っても CI では検出できず、
  ローカル k8s（`bash scripts/k8s-local-images.sh`）の実行で初めて破綻する。これは #268 が塞いだ
  「デプロイ資産が CI 未検証」と同型の穴が別リストに残った状態。

## 調査で確定した事実（`frontend` の扱い）

- Helm チャート `deploy/helm/microservices-platform/` には **`frontend` の deployment テンプレートも
  `values.services` エントリも存在しない**（`templates/deployment.yaml` は `.Values.services` を反復。
  `values.yaml` の `services:` は `MAPPING` と同一の 12 件）。
- したがって `frontend` は **k8s へはデプロイされない compose 専用のビルド対象**（dev の SPA ホスト・
  nginx が静的配信＋`/bff` プロキシ）。`MAPPING` から `frontend` が欠けているのは**腐りではなく意図的な除外**。
- **結論**: `frontend` は `MAPPING` に含めない。ただしこの除外を**暗黙にせず明示・機械検証**する
  （除外リスト自体の腐りも検査する。将来 k8s に frontend を載せる際は除外を外し `MAPPING`＋`values.services`
  へ追加する導線を強制する）。

## 対象範囲

- 対象（新規/変更）:
  - `scripts/check-image-mapping.js`（新規）: `MAPPING` と compose `build` 定義の双方向突合。`--self-test` 内蔵。
  - `scripts/scripts.test.js`（変更）: 検査ロジックの単体テストを追加。
  - `.github/workflows/image-mapping.yml`（新規）: `--self-test` ＋実チェックを回す独立ワークフロー（`ci.yml` を避ける）。
  - `scripts/README.md`（変更・存在すれば）: 方式・根拠を追記。
  - `docs/adr/IADR-0068_image-mapping-drift-check.md`（新規）＋ `docs/adr/README.md`（1 行追記）。
- 対象外:
  - `k8s-local-images.sh` を compose から動的導出して `MAPPING` を廃止する根治案（[IADR-0068](../adr/IADR-0068_image-mapping-drift-check.md) で却下。理由は同 ADR）。
  - compose 側イメージが**ビルド可能か**の検証（[IADR-0067](../adr/IADR-0067_service-image-build-ci-gate.md) の `images.yml` が担う）。本検査は**対応表の整合のみ**を見る。
  - Helm `values.services` と `MAPPING` の突合（本 issue のスコープ外。`frontend` 判定の根拠としてのみ参照）。

## 検査ルール（`check-image-mapping.js`）

設定（スクリプト内定数・根拠付き）:

- `IMAGE_PREFIX = 'microservices-platform'`: chart-image の接頭辞（`values.yaml` / `values-local.yaml` と一致）。
- `COMPOSE_ONLY = { 'frontend' }`: k8s チャート非デプロイの compose 専用ビルド対象（上記「確定した事実」）。

突合ロジック（純粋関数 `computeDrift`）— 以下のいずれかに該当すれば違反:

1. **MAPPING 欠落**: compose の `build` 定義のうち `COMPOSE_ONLY` 以外に、対応する `MAPPING` エントリが無い。
2. **MAPPING 腐り（stale）**: `MAPPING` エントリの chart-image に対応する compose `build` 定義が無い。
3. **Dockerfile 不一致**: 対応が取れるのに `MAPPING` の Dockerfile と compose の `build.dockerfile` が異なる。
4. **命名不整合**: `MAPPING` の chart-image が `IMAGE_PREFIX/<compose-service 名>` に一致しない。
5. **除外リストの腐り**: `COMPOSE_ONLY` のサービスが compose の `build` 定義に実在しない。
6. **除外の二重掲載**: `COMPOSE_ONLY` のサービスが `MAPPING` にも掲載されている。

- compose は `build.dockerfile:` の**リテラル値**のみを抽出する（アンカー・補間は `build.dockerfile` の
  リテラルに影響しないため、外部依存ゼロの限定テキスト解析で足りる。[IADR-0068](../adr/IADR-0068_image-mapping-drift-check.md) 参照）。
- 検査器はロジック関数（`parseComposeBuildTargets` / `parseMappingEntries` / `computeDrift`）を `module.exports` し、
  `scripts.test.js` から単体テストする。`--self-test` で合成フィクスチャによる自己試験を行う。

## 受け入れ基準（Issue #275）との対応

- [x] `MAPPING` と compose の `build` 定義の対応を機械検査し、差分があれば **fail** する
  （欠落・腐り・Dockerfile 不一致・命名不整合・除外リスト腐りを検出）。外部依存ゼロの Node スクリプト＋`--self-test`。
- [x] `frontend` を対象に含めるかを判断した → **含めない**（k8s チャート非デプロイのため）。除外は明示・機械検証する。
- [x] 根治案（compose から `MAPPING` を導出し二重管理を解消）を検討し、**却下理由を [IADR-0068](../adr/IADR-0068_image-mapping-drift-check.md) に記録**。
- [x] 現行ツリーは検査を通過する（誤検知なし）。ライブ環境不要で検証完結（`--self-test` ＋ 静的ファイル読取のみ）。

## 検証

- `node scripts/check-image-mapping.js --self-test` → 自己試験 OK。
- `node scripts/check-image-mapping.js` → 現行ツリー ドリフト 0。
- `node scripts/scripts.test.js` → 追加テスト含め全 pass。
- CI: `image-mapping.yml`（`--self-test` ＋実チェック）が緑。`ci.yml` は変更しない（並行作業と競合しない）。

## 実装判断・フォローアップ

- 方式選定（機械突合＋独立ワークフロー vs. compose 導出の根治）は [IADR-0068](../adr/IADR-0068_image-mapping-drift-check.md) に記録。
- 将来 k8s に frontend を載せる場合: `COMPOSE_ONLY` から `frontend` を外し、`MAPPING`＋Helm `values.services`＋
  frontend deployment テンプレートを追加する（本検査が導線を強制する）。
- Helm `values.services` と `MAPPING` の三点突合は将来必要になった時点で別途検討（本 issue のスコープ外）。
