---
title: サービス Dockerfile のイメージビルドを CI で検証する（Issue #268）
type: spec
status: review
related_ids:
  - NFR
  - ADR-0007
  - IADR-0063
  - IADR-0066
  - IADR-0067
author: claude
created: 2026-07-16
updated: 2026-07-16
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (非機能要件: 運用・保守)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md (CI/CD・イメージ配布)"
related_specs:
  - "../adr/IADR-0067_service-image-build-ci-gate.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../ai-workflow.md"
  - "../../deploy/docker-compose.yml"
---

# 仕様書: サービス Dockerfile のイメージビルドを CI で検証する（Issue #268）

> 本仕様書は実装着手前に作成する。計画書（`project-planning`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: —（CI ゲート整備）
- 非機能要件（NFR）: 運用・保守（デプロイ資産の健全性をマージ前に機械検査する）
- 関連 ADR: ADR-0007（CI/CD・GitOps。コンテナイメージが配布単位）
- 実装判断: [[IADR-0067]]（本作業で起票）／[[IADR-0063]]（BFF 合成点）／[[IADR-0066]]（ローカル k8s dev 環境＝発見元）
- Issue: #268（本 issue）／Refs #266（発見元・インライン修正済み）／関連 #229・#107・#82

## 目的・背景

ローカル k8s dev 環境（#266 / PR #267）でイメージをビルドしたところ、**サービスの Dockerfile が
ビルド不能**なものが連続して見つかった。

- **LlmGateway**: 中間 `dotnet build` 段のパスから `platform/backend/` 接頭辞が欠落（MSB1009）。
- **BFF**: [[IADR-0063]] の合成点として `knowledge/backend/...` を参照するが、Dockerfile が
  `src/platform/backend/` しか COPY せず `CS0246` で publish 失敗。

いずれも `ci.yml` が **ユニット単位の restore/build/test/format しか行わず、Dockerfile による
イメージビルドを検証していない**ために潜在化していた。コード構成変更（例: #229 の BFF 合成点移設）に
Dockerfile が追随できなくても CI は緑のままで、デプロイ／ローカル k8s 起動で初めて破綻する。

本作業は、この検出漏れを **マージ前の機械チェック**として恒久的に塞ぐ。

## スコープ

### 含む

- 全サービスの `docker build` を CI で実行する（push はしない。ビルド成立の検証のみ）。
- 対象は `deploy/docker-compose.yml` の `build` 定義（ビルドコンテキスト・dockerfile の単一情報源）から
  **動的に導出**する。サービス追加時に CI の編集を不要にする。
- `src/**` ／ Dockerfile ／ compose の変更時のみ重いビルドを走らせる（パスフィルタ）。
- 必須チェックとしてブランチ保護に組み込める形（安定したジョブ名の集約ジョブ）にする。

### 含まない

- イメージの push／レジストリ連携（ADR-0007 の Harbor 配布は別途）。
- 実コンテナの起動・疎通確認（実基盤依存。#82 の integration 系が担う）。
- `scripts/k8s-local-images.sh` の MAPPING（#266 由来の第2リスト）と compose の突き合わせ検査。
  → 本作業では扱わず、フォローアップとして [[IADR-0067]] に記す。

## 設計

詳細な判断根拠は [[IADR-0067]] に記す。要点のみ:

1. **新規ワークフロー `.github/workflows/images.yml`** を追加する（`ci.yml` は変更しない）。
   バックエンド／フロントの CI と独立させ、共有ファイルの競合も避ける。
2. **対象の導出**: `docker compose -f deploy/docker-compose.yml config --format json` を
   `jq` で絞り、`build` を持つサービス名の配列を得る。compose 自身をパーサとして使うため、
   独自の YAML 解析を持たず単一情報源とずれない。
3. **マトリクスビルド**: サービスごとに 1 ジョブで `docker compose build <service>`。
   失敗サービスを個別に特定でき、`fail-fast: false` で全滅を防ぐ。
4. **必須チェック互換**: パスフィルタをトリガに置くと、対象外 PR で必須チェックが
   永久 pending になりマージ不能になる。したがってトリガはパス無条件、**ジョブ内**で
   変更判定し、集約ジョブ `image-build`（安定名）が常に結果を報告する。

## 受け入れ基準（Issue #268 の提案チェックボックス）

| # | 基準 | 実現 |
| --- | --- | --- |
| 1 | 全サービス Dockerfile の `docker build` を実行するジョブを追加。対象は compose の build 定義と揃える | `images.yml` の `discover` → `build` マトリクス |
| 2 | `docker compose build` でまとめてビルドし push しない | `docker compose build <service>`（`--push` なし） |
| 3 | `paths` フィルタで Dockerfile / `src/**` 変更時に起動し、両スタックの CI と独立させる | `changes` ジョブのパス判定 ＋ 独立ワークフロー |
| 4 | 失敗を必須チェックにしてブランチ保護に組み込む | 集約ジョブ `image-build`（安定名）＋ `docs/ai-workflow.md` に必須チェックとして明記（設定自体はリポ管理者が行う） |

## テスト方針（TDD）と検証

CI ワークフローは単体テストの対象にならないため、次の順で「先に赤を確認してから緑にする」。

1. **赤の実証（既存）**: #266 が実際に検出したビルド不能（LlmGateway=MSB1009 / BFF=CS0246）が、
   本ジョブが存在しなかったために CI 緑をすり抜けた事実が「テストが無かった」ことの証拠である。
   本ジョブは同種の破壊を確実に落とす（Dockerfile 実行そのものが検査）。
2. **導出ロジックの検証（ローカル）**: `docker compose config --format json | jq ...` が
   compose の 13 サービスを過不足なく列挙することをローカルで確認する（下記「実行結果」）。
3. **緑の実証（CI）**: 本 PR の CI で 13 サービス全ジョブが緑になることを確認する。
   ローカルは Docker デーモン未起動のため、実イメージビルドの検証は CI 上で行う（実基盤依存の切り分け）。

### 導出ロジックのローカル実行結果

`docker compose -f deploy/docker-compose.yml config --format json | jq -r '.services | to_entries[] | select(.value.build) | .key'`

```
aianalysis-service, authorization-service, bff, conversion-service, dashboard-service,
datasource-service, document-service, feedback-service, frontend, ingestion-service,
llm-gateway, retrieval-service, wiki-service        （13 件 = compose の build 定義と一致）
```

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `.github/workflows/images.yml` | 新規（イメージビルド検証） |
| `docs/adr/IADR-0067_service-image-build-ci-gate.md` | 新規（設計判断） |
| `docs/adr/README.md` | IADR-0067 を索引へ追加 |
| `docs/ai-workflow.md` | 必須チェックへ `Images` を追記 |
| `ci.yml` ほか既存ワークフロー | **変更しない**（競合回避） |

## リスクと対策

- **CI 時間の増加**: 13 サービス × .NET publish。→ マトリクス並列（サービスごとに別ランナー）と
  パス判定で、無関係な PR（docs のみ等）では重いジョブを走らせない。
- **必須チェックの永久 pending**: 上記「必須チェック互換」で回避（集約ジョブが常に報告）。
- **compose の env 補間**: 全て `${VAR:-default}` 形式で既定値を持つため `config` は追加設定なしで解決する（確認済み）。
