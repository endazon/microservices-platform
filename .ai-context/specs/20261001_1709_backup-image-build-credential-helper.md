---
title: platform-backup イメージを資格情報ヘルパーに依存せずビルドできるようにする（#1709 の ②）
type: spec
status: done
related_ids: [NFR-21, ADR-0008, IADR-0489, IADR-0471, IADR-0081, IADR-0066]
author: claude
created: 2026-10-02
updated: 2026-10-02
issue: "#1709"
---

# 仕様書: platform-backup イメージを資格情報ヘルパーに依存せずビルドする（#1709 の ②）

## 起点（トレーサビリティ）

- #1709（監査 2026-10-01・high）の残り 4 項目のうち **② イメージを作り直す（資格情報ヘルパーの失敗で 3 回とも失敗）**。
  利用者裁定: 「② の回避策を実装側で先に作る」。③（起動スクリプトの再実行で有効化）④（手動 Job とリストア試験）は
  ①（age の受取人。利用者）と ② が揃ってから PoC 側へ依頼する。本 PR は issue を閉じない。
- NFR-21（バックアップ）・ADR-0008（ローカル配備）。判断は IADR-0489（新規）。先例は IADR-0081（frontend）。
- ファイル名の日付は依頼の指定（20261001）に従う。作業日は 2026-10-02。

## 事実（原因の特定）

1. **失敗の段**: #1689 の実ログ（`scripts/k8s-local-up.test.js` に固定）は
   `#2 [internal] load metadata for docker.io/library/postgres:16.15-alpine3.24@sha256:7218…` →
   `#2 ERROR: error getting credentials - err: exit status 22, out: { "errorCode" : 255 }`。
   ベースのメタデータの取得で落ち、age を取る RUN に到達していない。
2. **同じ文言の真因は IADR-0081 が同じ機械（Rancher Desktop）で実測済み**: レジストリが匿名の取得に
   `401 Www-Authenticate: Bearer` を返すと、ビルダーがトークンのために資格情報ヘルパーを呼び、ヘルパーが
   `errorCode 255`（`exit status 22`）で落ちる。チャレンジを返さない `mcr.microsoft.com`・`mirror.gcr.io` ではヘルパーは呼ばれない。
   .NET サービス（`mcr.microsoft.com`）と frontend（`mirror.gcr.io/library`。IADR-0081）が同じ機械でビルドできているのと整合する。
3. **バックアップのイメージだけが docker.io を直に引いていた**（`FROM docker.io/library/postgres:…`。#1564 で IADR-0081 の後に入った）。
4. **本作業の実測（2026-10-02・docker 29.3.1 / BuildKit）**:
   - 匿名 `GET /v2/library/postgres/manifests/sha256:721873c3…`: `registry-1.docker.io` → **401**（Bearer）。`mirror.gcr.io` → **200**、
     `docker-content-digest` 同値。amd64・arm64 の manifest と先頭 3 blob も 200。
   - `exit 22` ＋ `{ "errorCode" : 255 }` を返すヘルパーを `credsStore` に置いた `DOCKER_CONFIG` の下で、
     **従前の Dockerfile は PoC と同じ 2 行で落ちた（ヘルパー呼び出し 2 回）**。取得元を `mirror.gcr.io/library` にした Dockerfile は
     **ヘルパー呼び出し 0 回**でベースを取り、ビルドが完了した（RUN の wget だけはこの砂場のプロキシの CA を足した検証用の写しで通した。
     リポジトリの Dockerfile には入れていない）。`--network none` で `age` v1.3.1・`pg_dump` 16.15 を確かめた。

## 母集合（規則 9）

### ビルド・取り込みの全経路（`git grep -n "platform-backup/image\|LOCAL_ONLY_IMAGES"`、`src/` と `.ai-context/specs/` を除く）

| 経路 | ビルド | 取り込み | 扱い |
| --- | --- | --- | --- |
| 起動スクリプト `[2/7]` → `k8s-local-images.sh`（Rancher: nerdctl） | `nerdctl --namespace k8s.io build -f … -t k3d-local/platform-backup:… …` | 不要（`k8s.io` 名前空間へ直接） | Dockerfile の変更で直る（同じ Dockerfile を読む） |
| 同（k3d: docker） | `docker build -f … -t …` | `k3d image import … -c <cluster>` | 同上。取り込みはレジストリに触れない（ヘルパーの経路でない） |
| Runbook §1 の 5（手動） | `nerdctl --namespace k8s.io build …`（k3d のコマンドを追記） | 同上 | 同上。文書に k3d の 2 行を足した |
| CI `images.yml` の `build-local (platform-backup)` | `docker build --progress plain -f …` | なし | 取得元が `mirror.gcr.io` に替わるだけ（frontend の CI と同じ） |
| 稼働時の pull | なし（`imagePullPolicy: IfNotPresent`・レジストリに無い） | — | 対象外 |

### 誤りの側の文字列（`git grep -n -i "docker hub\|docker\.io\|docker login\|credsStore"`）

| 箇所 | 扱い |
| --- | --- |
| `deploy/local/platform-backup/image/Dockerfile` の `FROM docker.io/library/postgres` | **変更**（`${BASE_REGISTRY}`＝`mirror.gcr.io/library`。digest 同じ） |
| `scripts/k8s-local-images.sh` の WARN「Docker Hub へログインし直す」「Docker Hub … へ届くか」 | **変更**（取得元の確かめ方と `mirror.gcr.io` へ） |
| `scripts/k8s-local-up.test.js` の `/docker login/` の期待 | **変更**（新しい案内を期待し、Docker Hub へのログインを案内しないことを見る） |
| Runbook の失敗の分岐の表の 1 行（Docker Hub へログインし直す） | **変更** |
| Runbook §6 の 1（digest を registry-1.docker.io から引く） | 残す（docker.io の digest が正）。**ミラーが同じ digest を 200 で返す確認を足した** |
| `scripts/k8s-local-up.test.js` の実ログ（`load metadata for docker.io/...`）と分類器の表の docker.io の文言 | 残す（実物のログ・分類器の入力） |
| `deploy/docker-compose.yml`・`values.yaml`・`mail-relay.yaml`・seaweedfs の Runbook の `docker.io` | 対象外。稼働時に containerd（CRI）が pull するもので、ビルダーも `~/.docker/config.json` も通らない |
| `.ai-context/adr/IADR-0471`・既存の作業仕様書 | 変更しない（凍結記録） |

### 運用文書

| 文書 | 扱い |
| --- | --- |
| `docs/operations/platform-infra-backup-runbook.md` §2 冒頭の「現状: 停止中」の注記 | ①→②→③④ の順にコマンドつきで書き直す |
| 同 §1 の 5 | k3d の経路とベースの取得元の説明を足す |
| `docs/operations/operations.md` の「現状: 停止中」 | イメージの項を「ヘルパーを呼ばずに作れるようにした・作り直しは未実施」へ。手順の所在を Runbook §2 冒頭と明記 |
| `deploy/local/README.md` の永続化の節 | イメージの取得元と、前提が揃うまで suspend で置くこと・再開手順の所在を足す |

## 規則 10（この変更で新たに誤りになる自分の記述）

- 「ビルドが資格情報ヘルパーの失敗で通っていない」（operations.md・Runbook §2）→ 現在形のままだと修正後に誤り。過去形と「作り直しは未実施」へ。
- `k8s-local-up.test.js` の試験名「ログイン・…を案内」→ 案内からログインを外したので試験名を改めた。
- Runbook §6 の 3「Dockerfile の FROM（タグと digest）」→ 取得元（`BASE_REGISTRY`）は替えないと明記。
- Dockerfile 冒頭の「ベースは digest で固定」は引き続き正しい（取得元を替えても digest は同じ）。

## 規則 11

該当しない。時刻の窓を扱う是正ではない。

## 設計

- 決定は IADR-0489。Dockerfile は `ARG BASE_REGISTRY=mirror.gcr.io/library` ＋ `FROM ${BASE_REGISTRY}/postgres:<タグ>@sha256:<同じ digest>`。
- 起動スクリプトの止める／続ける（LOCAL_ONLY_IMAGES のビルド失敗は WARN で続け、CronJob は門が suspend で置く）は変えない（IADR-0471・#1699）。
  利用者の環境に依存する失敗（ヘルパーそのものの故障・到達）は、従来どおり WARN で原因（`registry`）と確かめる順を示す。
- 採らなかった案（空の `DOCKER_CONFIG`・取り込み経路の変更・ECR/GHCR・ヘルパーの修理）は IADR-0489 の表。

## 受け入れ基準 → 試験

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | ベースの取得元がチャレンジを返さない取得元（`mirror.gcr.io/library`）で、digest 固定・PG 16 のまま | `scripts/platform-backup.test.js` の 9 / 10（実物） |
| A2 | 取得元を docker.io（明示・省略）・public.ecr.aws・ghcr.io へ戻すと落ちる | 同 10 の変異 |
| A3 | FROM の `${BASE_REGISTRY}` / `$BASE_REGISTRY` を ARG の既定で解き、既定の無い ARG は通さない | 同 10 の抜き出しの試験 |
| A4 | 資格情報の失敗の WARN は、取得元の確かめ方（load metadata の行・BASE_REGISTRY の既定）を案内し、Docker Hub へのログインを案内せず、版上げへ導かない | `scripts/k8s-local-up.test.js`「#1689 / #1709: 資格情報ヘルパーの失敗…」 |
| A5 | 壊れた資格情報ヘルパーの下でビルドが通り、ヘルパーが呼ばれない | 手動の実測（上の「事実」4。CI に docker の壊れたヘルパーを置く試験は足さない —— CI の `build-local` が同じ Dockerfile を実ビルドする） |

試験 ID は既存の流儀（`platform-backup.test.js` の番号つき項目・`k8s-local-up.test.js` の issue 番号つき試験名）に従った。
`*.test.sh` は足していない —— 変えた 2 ファイル（Dockerfile・k8s-local-images.sh）の既存の試験はどちらも node の試験であるため。

## 検証

- `node scripts/platform-backup.test.js`（kubectl が要る）・`node scripts/k8s-local-up.test.js`・`bash deploy/local/platform-backup/script/backup.test.sh`
- 文書・トレーサビリティの検査器（check-trace-blocks / check-test-traceability / gen-knowledge-graph --check / check-cross-repo-refs /
  check-plan-id-qualification / check-doc-links / check-adr-index-sync / check-reading-budget / check-commit-messages）
- 自己変異 4 個以上で赤を確かめる（退避は作業ツリーの外の専用ディレクトリへ）。

## 範囲外

- ①（age の受取人の作成。利用者）・③（稼働 PoC での起動スクリプトの再実行）・④（手動 Job とリストア試験）。
- 稼働 PoC の実機（Rancher Desktop）でのビルド。資格情報ヘルパーそのものの修理。

## 結果（2026-10-02）

### 自己変異（退避は作業ツリー外の専用ディレクトリへ cp。戻したあと対照が緑）

| # | 変異 | 試験 | 結果 |
| --- | --- | --- | --- |
| M1 | Dockerfile の `ARG BASE_REGISTRY` の既定を `docker.io/library` へ戻す | `platform-backup.test.js` | 赤（取得元 docker.io/library が mirror.gcr.io/library でない） |
| M2 | `FROM` から取得元を外す（`FROM postgres:…`＝docker.io） | 同上 | 赤（docker.io/library（省略）） |
| M3 | 試験の FROM の ARG の解決を外す（`${BASE_REGISTRY}` のまま見る） | 同上 | 赤（取得元 `${BASE_REGISTRY}`） |
| M4 | 許す取得元に `docker.io/library` を足す | 同上 | 赤（変異「取得元を docker.io/library へ戻す」を見逃した） |
| M5 | `k8s-local-images.sh` を変更前（Docker Hub へログインの案内）へ戻す | `k8s-local-up.test.js` | 赤（ベースの取得元の確かめ方が無い） |
| M6 | 実ビルド: 変更前の Dockerfile（docker.io）を壊れたヘルパーの下で | `docker build` | 失敗（PoC と同じ `error getting credentials - err: exit status 22`、ヘルパー 2 回）。変更後はヘルパー 0 回で成功 |

### 検証

- `node scripts/platform-backup.test.js` 12 件 OK（kubectl v1.37.1 を作業用に取得して実行）、`node scripts/k8s-local-up.test.js` 237 件 OK、
  `bash deploy/local/platform-backup/script/backup.test.sh` 129 件 OK。
- check-trace-blocks / check-test-traceability / gen-knowledge-graph --check / check-cross-repo-refs / check-plan-id-qualification /
  check-doc-links / check-reading-budget / check-doc-type-vocabulary: OK。`check-adr-index-sync.js` はこのリポジトリに無い（索引の一致は check-adr-numbering が見る）。
- `check-adr-numbering`（と、それを呼ぶ `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）は **IADR-0488 の欠番**で赤。
  0488 は並行の #1713 が使う番号で、先に着地すれば解ける（依頼の採番どおり）。0488 を仮置きした状態では scripts.test.js 853 件 OK を確かめ、仮置きは消した。
