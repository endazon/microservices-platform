---
title: IADR-0068 k8s-local-images.sh の MAPPING と compose build 定義のドリフトは機械突合スクリプト＋独立ワークフローで検査する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0007
  - IADR-0066
  - IADR-0067
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md (CI/CD・イメージ配布)"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (非機能要件: 運用・保守)"
---

# IADR-0068: MAPPING ↔ compose build 定義のドリフトは機械突合スクリプト＋独立ワークフローで検査する

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用・保守）／ADR-0007（CI/CD・GitOps。**コンテナイメージが配布単位**）
- 関連 ADR: [[IADR-0067]]（compose 単一情報源のイメージビルド検証。本件をフォローアップとして切り出した元）／[[IADR-0066]]（`k8s-local-images.sh` の追加元。`MAPPING` の出自）
- 関連仕様書: `docs/specs/20260718_issue-275_image-mapping-drift-check.md`
- Issue: #275（本 issue）／Refs #268（PR #274 / IADR-0067）／Refs #266（IADR-0066）

## コンテキストと課題

`scripts/k8s-local-images.sh`（[[IADR-0066]]）は、ローカル k8s へイメージを供給するために
「chart-image（`values.yaml` の `services.<name>.image`）: Dockerfile パス」の対応表を **bash 配列 `MAPPING`** として持つ。
一方 `deploy/docker-compose.yml` の `build` 定義は [[IADR-0067]] で **イメージビルド検証の単一情報源**になった。

両者は別々のビルド対象リストで**二重管理**であり、突き合わせ検査が無い。

- `MAPPING`（12 件）と compose の `build` 定義（13 件）は既に差がある。差は `frontend`
  （`src/platform/frontend/Dockerfile`）のみ。
- `images.yml` のゲートは compose 側しか担保しないため、`MAPPING` が腐っても CI では検出できず、
  `bash scripts/k8s-local-images.sh` の実行という「最も遅いフィードバック地点」で初めて破綻する。
  これは #268 が塞いだ穴（デプロイ資産が CI 未検証）と同型の穴が別リストに残った状態。

### `frontend` は腐りか、意図的除外か

調査の結果、Helm チャート `deploy/helm/microservices-platform/` には **`frontend` の deployment テンプレートも
`values.services` エントリも存在しない**（`templates/deployment.yaml` は `.Values.services` を反復するだけ。
`values.yaml` の `services:` は `MAPPING` と同一の 12 件）。したがって `frontend` は **k8s へはデプロイされない
compose 専用のビルド対象**（dev の SPA ホスト）であり、`MAPPING` 非掲載は**腐りではなく意図的な除外**である。

## 決定

### 1. `MAPPING` は残し、compose との対応を機械突合する（根治案は却下）

外部依存ゼロの Node スクリプト `scripts/check-image-mapping.js` を追加し、`MAPPING` と compose の `build`
定義を**双方向**に突合する。差分（欠落・腐り・Dockerfile 不一致・命名不整合・除外リスト腐り・除外の二重掲載）が
あれば `exit 1`。`--self-test` を内蔵し、ロジック関数を `module.exports` して `scripts/scripts.test.js` から単体テストする。
これは既存流儀（`check-unit-dependencies.js` / `validate-pipeline-config.js`）と同型。

**根治案（`k8s-local-images.sh` が compose から対象を動的導出し `MAPPING` を廃止）を却下した理由**:

- `k8s-local-images.sh` は Rancher Desktop（nerdctl）／k3d の**最小の dev 環境で実行**される。compose からの
  導出には `docker compose config` か YAML パーサ（yq 等）が要り、**ビルドヘルパに実行時依存を新規に増やす**。
  現状の bash は `nerdctl`/`docker`＋`k3d` のみに依存しており、この単純さは維持する価値がある。
- 導出しても **chart-image 名 ↔ compose サービス名の対応付け**と **`frontend` 等の compose 専用除外**を
  どこかに符号化する必要は残る（compose だけでは「どれを k8s に載せるか」は決まらない）。根治案でも
  ロジックは消えず、置き場所が変わるだけ。
- 二重管理の実害は「ズレに気づけないこと」であり、それは **CI の突合検査**で十分に塞げる。ランタイムの
  結合を増やさずに目的（マージ前にドリフトを落とす）を達成できる。

### 2. `frontend` は `MAPPING` に含めない。除外を明示・機械検証する

`frontend` は k8s チャート非デプロイのため `MAPPING` に含めない。ただし除外を暗黙にせず、スクリプト内の
`COMPOSE_ONLY = { 'frontend' }` として明示し、**除外リスト自体の腐りも検査**する:

- `COMPOSE_ONLY` のサービスが compose の `build` に実在しない → 違反（除外が腐った）。
- `COMPOSE_ONLY` のサービスが `MAPPING` にも載っている → 違反（除外の二重掲載）。

これにより「将来 k8s に frontend を載せる」際は、除外を外して `MAPPING`＋Helm `values.services`＋deployment
テンプレートを追加する導線が強制される。

### 3. YAML は `build.dockerfile` のリテラルのみを限定抽出する

[[IADR-0067]] は「独自 YAML パーサで build 定義を抽出」を却下した。理由は**実ビルド**において補間・アンカー・
既定値（`${VAR:-default}`）の解釈が compose と乖離するリスクがあったため。本検査は**実ビルドをしない**——
`build.dockerfile:` の**リテラル文字列**（compose 内で補間・アンカーを一切含まない素のパス）を比較するだけである。
そのため限定的なテキスト解析で足り、外部依存ゼロ（`--self-test` で解析器自体を回帰固定）を保てる。
compose を「ビルドの単一情報源」とする [[IADR-0067]] の役割分担は変えない（本検査は**対応表の整合のみ**を見る）。

### 4. `ci.yml` に足さず、独立ワークフロー `image-mapping.yml` にする

[[IADR-0067]] と同じ理由: `ci.yml` は複数作業が同時に触る共有ファイルで競合しやすい。独立ワークフローで
`node scripts/check-image-mapping.js --self-test` と実チェックを回す（外部依存ゼロのため `setup-node` のみで足りる）。

## 影響

- 以後、`MAPPING` と compose の `build` 定義がズレた PR はマージ前に落ちる（新サービス追加時の `MAPPING`
  追記漏れ・Dockerfile リネーム追随漏れ・stale エントリを検出）。
- 現行ツリーは上記ルールで整合しており、本 PR 時点では**緑**（ドリフト 0）。ガードの追加であり挙動変更はない。
- `k8s-local-images.sh` の実行時挙動は変えない（`MAPPING` はそのまま。ランタイム依存を増やさない）。

## 代替案と却下理由

| 案 | 却下理由 |
| --- | --- |
| `k8s-local-images.sh` が compose から導出し `MAPPING` 廃止（根治） | ビルドヘルパに `docker compose`/yq のランタイム依存を新規追加。chart-image↔service 対応と compose 専用除外の符号化は結局残る。CI 突合で実害は塞げる |
| `frontend` を `MAPPING` に追加してリスト一致させる | k8s チャートに frontend deployment/`values.services` が無く、載せても何もデプロイしない。誤ったイメージを毎回ビルドするだけ |
| 除外を暗黙（コメントのみ）にする | 除外の腐り（対象が消えた／二重掲載）を検出できない。明示定数＋検査で導線を強制する |
| `ci.yml` に検査ジョブを追加 | 共有ファイルの競合。独立ワークフローが既存方針（`images.yml`/`frontend*.yml`）と整合 |
| 実ビルドで検証（`images.yml` を MAPPING にも拡張） | 本 issue は**対応表の整合**が論点。ビルド可否は既に `images.yml` が compose 側で担保済み |

## フォローアップ（本 ADR のスコープ外）

- Helm `values.services` と `MAPPING`・compose の三点突合（`MAPPING` の chart-image が実在の chart サービスか）。
  現状は 12 件で一致しており未整備でも実害は小さい。必要になった時点で本スクリプトへ拡張する。
