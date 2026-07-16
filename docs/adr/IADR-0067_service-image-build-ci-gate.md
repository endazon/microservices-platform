---
title: IADR-0067 サービスイメージのビルド検証は compose を単一情報源とする独立ワークフローで行い、集約ジョブを必須チェックにする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0007
  - IADR-0063
  - IADR-0066
author: claude
created: 2026-07-16
updated: 2026-07-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md (CI/CD・イメージ配布)"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (非機能要件: 運用・保守)"
---

# IADR-0067: サービスイメージのビルド検証は compose を単一情報源とする独立ワークフローで行う

- 状態: Accepted
- 日付: 2026-07-16
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用・保守）／ADR-0007（CI/CD・GitOps。**コンテナイメージが配布単位**）
- 関連 ADR: [[IADR-0063]]（BFF のユニット別エンドポイント合成点）／[[IADR-0066]]（ローカル k8s dev 環境。発見元）
- 関連仕様書: `docs/specs/20260716_issue-268_service-image-build-ci-gate.md`
- Issue: #268（本 issue）／#266（発見元）

## コンテキストと課題

`ci.yml` はユニット単位（`src/*/backend/backend.slnx`）の restore/build/test/format を行うが、
**各サービスの Dockerfile によるイメージビルドを一切検証していない**。ADR-0007 において配布単位は
コンテナイメージであるから、これは「配布物そのものが CI 未検証」という状態にあたる。

実際に #266（ローカル k8s dev 環境）でイメージをビルドした際、ビルド不能な Dockerfile が連続して
見つかった（PR #267 でインライン修正済み）。

- **LlmGateway**: 中間 `dotnet build` 段のパスから `platform/backend/` 接頭辞が欠落（MSB1009）。
- **BFF**: [[IADR-0063]] の合成点として `knowledge/backend/Bff/Knowledge.Bff.Endpoints` を無条件参照するが、
  Dockerfile は `src/platform/backend/` しか COPY せず、参照先不在で `CS0246` により publish 失敗。

いずれも **ソリューションのビルドは通るが、イメージのビルドは通らない**。つまり既存 CI の観測範囲外に
あり、#229（BFF 合成点移設）のような構成変更に Dockerfile が追随しなくても緑のまま通る。破綻は
デプロイ／ローカル k8s 起動という「最も遅いフィードバック地点」で初めて露見していた。

## 決定

### 1. ビルド対象は `deploy/docker-compose.yml` の `build` 定義から動的に導出する

`docker compose config --format json` を `jq` で絞り、`build` を持つサービス名を列挙する。

- compose は既に**ビルドコンテキストと dockerfile の単一情報源**であり、対象リストを CI に
  ハードコードすれば必ず腐る（`ci.yml` が `src/*/backend/backend.slnx` を自動発見するのと同じ思想。
  [[IADR-0060]] 系の「ユニット追加時に CI を編集しない」方針の踏襲）。
- **YAML を自前で解析しない**。compose 自身をパーサとして使うことで、アンカー・補間・既定値
  （`${VAR:-default}`）の解釈が実際のビルドと必ず一致する。

### 2. `ci.yml` に足さず、独立ワークフロー `images.yml` にする

- `ci.yml` は複数の作業が同時に触る共有ファイルであり、競合が起きやすい。
- パスフィルタ（後述）を `ci.yml` に置くと**全ジョブ**に効いてしまい、他の検査まで止まる。
- バックエンド／フロントの CI を独立させる既存方針（`frontend.yml` / `frontend-tests.yml`）と整合する。

### 3. サービスごとに 1 ジョブのマトリクスでビルドする（push しない）

`docker compose build <service>` をマトリクス（`fail-fast: false`）で実行する。

- 単一ジョブで 13 サービスを直列ビルドすると壁時計時間が伸び、どのサービスが壊れたかも埋もれる。
- マトリクスなら**サービス単位で赤が特定**でき、ランナーも並列に使える。
- `--push` は行わない。本ゲートの目的は**ビルド成立の検証**であり、レジストリ配布は ADR-0007 の
  リリース経路が担う（PR 段階でイメージを配布しない）。

### 4. パスフィルタはトリガではなく**ジョブ内判定**とし、集約ジョブを必須チェックにする

トリガ側 `on.pull_request.paths` は使わず、`changes` ジョブで変更パスを判定し、`build` を条件実行する。
最後に `image-build` という**安定名の集約ジョブ**を `if: always()` で必ず走らせ、これを必須チェックにする。

**理由（重要）**: GitHub のブランチ保護は「必須チェックが report されるまで」マージを許さない。
トリガのパスフィルタでワークフロー自体がスキップされると、そのチェックは**永久に pending** となり、
対象外の PR（docs のみの変更等）が恒久的にマージ不能になる。Issue #268 は「paths フィルタで起動」と
「必須チェック化」の両方を求めているため、両立させるにはこの形しかない。

- 既存 `frontend.yml` はトリガ側にパスフィルタを置く。本 ADR はそれと**意図的に異なる**形を採る。
  差分の理由は必須チェック互換性であり、`frontend.yml` を必須チェックにする際は同じ変換が要る。

## 影響

- **緑の意味が変わる**: 以後、CI 緑は「ソリューションがビルドできる」だけでなく「配布物である
  イメージがビルドできる」ことを含む。#266 型の破綻はマージ前に落ちる。
- **CI 時間**: 対象パスに触れる PR では 13 ジョブが並列に増える。docs のみの PR では `build` を
  スキップし、`image-build` のみが即座に緑を報告する。
- **ブランチ保護の設定変更が要る**（リポ設定であり本 PR のコード変更では完結しない）。
  `docs/ai-workflow.md` の必須チェック一覧へ `Images / image-build` を追記し、設定はリポ管理者が行う。

## 代替案と却下理由

| 案 | 却下理由 |
| --- | --- |
| `ci.yml` に build ジョブを追加 | 共有ファイルの競合。パスフィルタが全ジョブに波及する |
| 単一ジョブで `docker compose build`（全サービス一括） | 壁時計時間が長く、赤の所在が埋もれる。ランナー並列を活かせない |
| 対象サービスを workflow にハードコード | サービス追加時に必ず腐る。compose との二重管理 |
| 独自 YAML パーサ（Node スクリプト）で build 定義を抽出 | 補間・アンカー解釈が compose と乖離するリスク。compose 自身を使えば不要 |
| 静的検査のみ（Dockerfile の存在・パス整合） | #266 の実バグ（MSB1009 / CS0246）はいずれも**実ビルドでしか**検出できない |
| トリガ側パスフィルタ ＋ 必須チェック | 対象外 PR が永久 pending でマージ不能になる（GitHub 仕様） |

## フォローアップ（本 ADR のスコープ外）

- `scripts/k8s-local-images.sh` の `MAPPING` は compose とは別のビルド対象リストであり（#266 由来・
  `frontend` を含まない）、compose との**ドリフト検査は未整備**。本ゲートは compose 側のみを担保する。
  MAPPING の腐敗は本ゲートでは検出できないため、突き合わせ検査は **Issue #275** に切り出した。
