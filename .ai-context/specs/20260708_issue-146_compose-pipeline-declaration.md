---
title: 作業仕様書 — compose 環境への宣言（pipeline.json）供給とドリフト突合の正常化
type: spec
status: done
related_ids:
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
related_specs:
  - ../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md
  - ../../docs/screens/SC-11_configuration-viewer.md
---

# 作業仕様書: compose への宣言供給（UndeclaredSubscription 警告の是正）

Issue: #146（親: #123 ／ 参照 #118 監査論点 5）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（宣言と実効の突合＝ドリフト検出）
- 関連 ADR: ADR-0018・IADR-0029

## 目的・背景

compose(dev) で BFF に宣言（pipeline.json）が供給されず、ドリフト検出が実効の全購読を
`UndeclaredSubscription` と誤判定していた（#118 監査）。

## 方針（要判断 → 決定）

**(a) compose にも宣言をマウントして dev でも正しく突合する**を採用（ユーザー判断）。

### 調査で判明した追加事実

- `DriftDetector` は段の `Input`/`Consumer`（**型由来**の自己申告）で突合するため、突合の基準（宣言）を
  持つ必要があるのは **BFF のみ**（段ホストは宣言をマウントしなくても自己申告は正しい）。
- **Helm でも BFF は宣言をマウントしていなかった**（マウント条件が `pipelineSteps` に限定され、段を
  ホストしない BFF は対象外だった）。issue の前提「Helm では正しく突合される」は実際には成立しておらず、
  Helm/prod でもドリフト検出が壊れていた。→ **compose と Helm の両方**を是正する。

## 対象範囲

- 対象:
  1. **ローダ**（`PipelineExtensions.AddKnowledgePlatformPipelineConfig`）を拡張し、`{"Pipeline": {...}}`
     オーバレイ（Helm）に加え、**生の pipeline.json**（compose が正の pipeline.json を直接マウント）も
     `"Pipeline"` セクションへ包んで読めるようにする（正の pipeline.json を複製しない）。
  2. **compose**: BFF に正の `pipeline.json` を読み取り専用でマウントし、`Pipeline__ConfigPath` を設定。
  3. **Helm**: `bff.pipelineDeclaration: true` を追加し、マウント条件を `pipelineSteps || pipelineDeclaration`
     に拡張して BFF にも `pipeline-config` ConfigMap をマウント（`Pipeline__ConfigPath` 注入・ロールアウト連動）。
  4. **テスト**: ローダが raw / wrapped 両形式を読めることを検証。
- 非対象: 即時検出（#145）・段ホストへの追加マウント（不要）。

## 受け入れ基準

- [x] 方針が判断され根拠が文書に記録されている（(a) 採用・本仕様書）。
- [x] dev 環境のドリフト応答が正しくなる（BFF が宣言を持ち UndeclaredSubscription 誤判定が解消）。
- [x] （追加）Helm/prod でも BFF が宣言を持つよう是正。
- [x] `dotnet build` / `dotnet test` 緑。`compose config` / `helm template` VALID。

## テスト

- `PipelineConfigLoaderTests`: 生の pipeline.json / `{"Pipeline":...}` オーバレイの双方を読み込めることを検証。
- `PipelineDeclarationMountTests`（回帰ガード）: BFF が宣言を受け取る配線が compose・Helm から失われて
  いないことを YAML テキストの静的検査で固定する（helm/docker バイナリに非依存。`NetworkIsolationTests` と同方針）。
  - compose: BFF が `Pipeline__ConfigPath` と正の pipeline.json の読み取り専用マウントを持つこと。
  - Helm: `values.yaml` の BFF に `pipelineDeclaration: true` があること。
  - Helm: `deployment.yaml` のマウント条件が `pipelineSteps` だけでなく `pipelineDeclaration` も含み、
    `pipelineSteps` のみへ後退していないこと。
- `helm template` で BFF・段ホスト 4 の計 5 サービスが `pipeline-config` をマウントすることを確認。
- `docker compose config` で BFF に raw pipeline.json がマウントされ `Pipeline__ConfigPath` が設定されることを確認。
