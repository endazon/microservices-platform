---
title: MinIO イメージの取得元を Docker Hub から MinIO 公式の quay.io へ移し、develop の Integration / integration-stack を戻す
type: spec
status: in-progress
related_ids: [FR-06, FR-12, ADR-0014, ADR-0015, IADR-0024, IADR-0232]
author: claude
created: 2026-09-15
updated: 2026-09-15
---

# 仕様書: MinIO イメージの取得元を quay.io へ移す（#1434 / #1435）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06（文書の保管）／FR-12（本文変換の資産）— MinIO が本文・資産の実体を持つ
- 関連 ADR: ADR-0014（オブジェクトストレージ）／ADR-0015（MinIO の採用）
- 関連 IADR: IADR-0024（MinIO の配置と非公開）／IADR-0232（develop の後段 CI と自動起票）
- 起票: #1434（[CI] Integration が失敗している）／#1435（[CI] integration-stack が失敗している）

## 目的・背景

develop の `Integration` と `integration-stack` が 2026-09-11 以降、毎回失敗している（自動起票 #1434 / #1435 にコメントが積み上がっている）。
いずれも原因は同じで、**Docker Hub の `minio/minio` リポジトリが取得できなくなった**ことである。

| ワークフロー | 失敗の形（2026-09-14 `eed1ff24` の実行） |
| --- | --- |
| Integration | `Knowledge.IntegrationTests.Storage.ObjectStorageRoundTripTests` が Testcontainers の pull で `pull access denied for minio/minio, repository does not exist` |
| integration-stack | `check-stack-ready.js` G1: `microservices-platform/minio` が `availableReplicas=0`。Pod は `ImagePullBackOff`（`docker.io/minio/minio:RELEASE.2025-04-08T15-41-24Z`: `repository does not exist or may require authorization`） |

Docker Hub の API も `https://hub.docker.com/v2/repositories/minio/minio/` に `{"message":"object not found"}` を返す（2026-09-15 実測）。
一方 **MinIO 公式の `quay.io/minio/minio` には同じタグ `RELEASE.2025-04-08T15-41-24Z` が残っている**（quay.io API: manifest list `sha256:8834ae47a2de3509b83e0e70da9369c24bbbc22de42f2a2eddc530eee88acd1b`・子 manifest 3 件すべて present）。

🔴 **ローカルクラスタで気付かなかったのは、containerd に以前 pull した同タグが残っていたからである**（新規クラスタ・CI ランナーでは必ず落ちる）。

## 対象範囲

- 対象: MinIO イメージの参照 3 か所と、それを手順として書いている文書 1 か所の取得元を `quay.io/minio/minio` へ移す。**タグ（リリース）は変えない。**
- 対象外:
  - MinIO のバージョン更新（別の判断。本件は取得元だけを移す）
  - オブジェクトストレージの製品選定（ADR-0015 は変えない）
  - ミラー（mirror.gcr.io 等）の導入 —— mirror.gcr.io は Docker Hub の**公式ライブラリ**のプルスルーであり、撤去された `minio/minio`（公式ライブラリではない）は引けない

## 走査した母集合

誤りの側の文字列（`minio/minio`・`docker.io` と MinIO の組・リリースタグ）で全ファイルを走査した（`.claude/rules/traceability.repo.md` 規則 9）。

| 走査 | コマンド | 結果 |
| --- | --- | --- |
| MinIO イメージ参照（非 Markdown） | `git grep -n -E "minio/(mc\|minio\|operator)\|quay\.io/minio\|bitnami/minio\|chainguard/minio" -- . ':!*.md' ':!src/ai-stock-trading'` | **3 件**: `deploy/docker-compose.yml:154` / `deploy/helm/microservices-platform/values.yaml:1007`（`registry: docker.io` と組）/ `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Storage/ObjectStorageRoundTripTests.cs:34` |
| helm の組み立て | `git grep -n "image:" -- deploy/helm/microservices-platform/templates/minio.yaml` | `"{{ $m.registry }}/{{ $m.image }}:{{ $m.tag }}"` —— `registry` を変えれば足りる |
| values の上書き | `deploy/local/values-local.yaml` の `minio:` ブロック | `registry` / `image` / `tag` の上書き**なし** |
| ワークフロー | `git grep -n -i minio -- .github/workflows` | イメージ参照**なし**（`ci.yml` のコメント 1 行のみ） |
| 文書 | `git grep -n -E "minio/minio\|RELEASE\.2025-04-08\|Docker Hub" -- docs .ai-context/adr README.md deploy/local/README.md` | 下表 |

文書の走査結果と扱い:

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `docs/how-to/run-integration-tests-without-docker.md` の手順（`minio/minio:RELEASE…`） | **追随する** | 読者がそのまま実行するコマンドであり、書かれたとおり打つと同じ pull 失敗になる |
| `docs/tech/tech-requirements.md` の技術表（`RELEASE.2025-04-08`） | 変更しない | バージョンだけを持ち、取得元を書いていない。バージョンは変えていない |
| `.ai-context/adr/IADR-0081`・`IADR-0138`・`IADR-0404` の Docker Hub への言及 | 変更しない | いずれも MinIO と無関係（frontend base のミラー・カバレッジの実測環境・postfix の digest 確認）であり、凍結記録である |

## 設計

- `deploy/helm/microservices-platform/values.yaml`: `minio.registry` を `docker.io` → `quay.io`（`image: minio/minio`・`tag` は据え置き）。
- `deploy/docker-compose.yml`: `image: quay.io/minio/minio:RELEASE.2025-04-08T15-41-24Z`。
- `ObjectStorageRoundTripTests.cs`: `WithImage("quay.io/minio/minio:RELEASE.2025-04-08T15-41-24Z")`。
- 文書の手順を同じ参照へ揃える。
- 3 か所それぞれに「なぜ quay.io か」（Docker Hub から撤去された）を 1〜2 行のコメントで残す。

**実装 ADR は起こさない。** 製品選定・バージョン・構成の判断を変えておらず、**同一リリースの取得元の差し替え**に留まるためである（判断の根拠は本書に残す）。
並行する #1467 が IADR-0454 を予約しており、本 PR が番号を取ると develop に欠番が生じる事情もある。

## 受け入れ基準

- [ ] 非 Markdown の MinIO イメージ参照 3 か所が `quay.io/minio/minio:RELEASE.2025-04-08T15-41-24Z` を指す（`docker.io` の MinIO 参照が 0 件）
- [ ] `helm template`（既定 values と `values-local.yaml` の両方）が `quay.io/minio/minio:RELEASE.2025-04-08T15-41-24Z` を描画する
- [ ] 手順書のコマンドが同じ参照を使う
- [ ] 本ブランチで `Integration` を `workflow_dispatch` で実行し、`ObjectStorageRoundTripTests` が通る
- [ ] 本ブランチで `integration-stack` を `workflow_dispatch` で実行し、`check-stack-ready.js` の G1 に `minio` の失敗が出ない
- [ ] 文書検査器（trace-blocks / doc-links / doc-updated / cross-repo-refs / plan-id-qualification / knowledge graph）が通る

## テスト方針

- 手元（Windows・Rancher Desktop）には Docker API が無く Testcontainers を起こせないため、**実 pull を伴う検証は CI のワークフローを本ブランチで手動起動して行う**（`gh workflow run integration.yml --ref <branch>` / `integration-stack.yml`）。
- 手元では `helm template` の描画と文書検査器を実行する。

## 計画書との差異

- 差異: なし（ADR-0015 の MinIO 採用・IADR-0024 の配置は変えない）。

## 未決事項

- quay.io も撤去・有料化された場合の取得元（自前レジストリへのミラー）。**今回は起きていないため置かない**（同型の事故 2 回で検討する）。
