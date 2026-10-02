---
title: IADR-0489 platform-backup イメージのベースを、匿名の取得にチャレンジを返さないミラー（mirror.gcr.io/library）から digest のまま取り、資格情報ヘルパーを呼ばずにビルドする
type: impl-adr
status: Accepted
related_ids: [NFR-21, ADR-0008, IADR-0471, IADR-0081, IADR-0066]
author: claude
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-21 バックアップ)
  - planning:projects/microservices-platform/07_adr/ADR-0008 (ローカル配備)
related_specs:
  - ../specs/20261001_1709_backup-image-build-credential-helper.md
---

# IADR-0489: バックアップのイメージのベースはチャレンジを返さないミラーから取る（#1709）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-02
- 決定者: claude（#1709。利用者裁定「資格情報ヘルパーの回避策を実装側で先に作る」）

## 起点・関連

- 関連する計画書 ID: NFR-21（バックアップ）
- 関連する計画 ADR: ADR-0008（ローカル配備）
- 関連する実装 ADR: [[IADR-0471]]（日次の暗号化バックアップ・同梱イメージ・LOCAL_ONLY_IMAGES のビルド失敗は WARN で続ける）、
  [[IADR-0081]]（frontend のベースを mirror.gcr.io/library へ替えた先例。同じ失敗の真因を実測した記録）、[[IADR-0066]]（ローカルのイメージ供給）

## コンテキストと課題

稼働 PoC では platform-backup の CronJob 2 本が `suspend: true` のまま、バックアップを 1 本も取れていない（#1700・#1709）。
前提の 1 つであるイメージ `k3d-local/platform-backup:pg16.15-age1.3.1-r6` のビルドが、毎回次の行で落ちていた（#1689 の実ログ）。

```
#2 [internal] load metadata for docker.io/library/postgres:16.15-alpine3.24@sha256:7218…
#2 ERROR: error getting credentials - err: exit status 22, out: `{ "errorCode" : 255 }`
```

### 原因（根拠）

1. **失敗の段はベースのメタデータの取得**である（ログの `#2 [internal] load metadata for docker.io/...`）。age を取る RUN にも到達していない。
2. **同じ環境で同じ文言の失敗の真因は、IADR-0081 が実測で特定済み**である。Rancher Desktop のビルダーは、レジストリが匿名の取得に
   `401 Www-Authenticate: Bearer` を返すと、トークンを取るために資格情報ヘルパーを呼ぶ。その環境ではヘルパーが
   `errorCode 255`（`exit status 22`）で落ちる。チャレンジを返さないレジストリ（`mcr.microsoft.com`・`mirror.gcr.io`）ではヘルパーは呼ばれず、
   成立する（.NET のサービスと frontend のイメージが同じ機械で作れているのはこのため）。
3. **バックアップのイメージだけが docker.io を直に引いていた**（`FROM docker.io/library/postgres:…@sha256:…`）。IADR-0081 の後に入った
   イメージ（#1564）で、先例が適用されていなかった。
4. **2026-10-02 の実測**（本 IADR の作業。素の docker 29.3.1・BuildKit）:
   - 匿名の `GET /v2/library/postgres/manifests/<digest>`: `registry-1.docker.io` は **401**（`Bearer realm="https://auth.docker.io/token"`）、
     `mirror.gcr.io` は **200** で `docker-content-digest` が同じ `sha256:721873c3…`。amd64・arm64 の manifest と先頭の blob も 200。
   - `exit 22` と `{ "errorCode" : 255 }` を返す資格情報ヘルパーを `credsStore` に置いた `DOCKER_CONFIG` の下で:
     従前の Dockerfile は **PoC と同じ文言**（`#2 ERROR: error getting credentials - err: exit status 22, out: …`）で落ち、ヘルパーの呼び出しは 2 回。
     取得元を `mirror.gcr.io/library` に替えた Dockerfile は**ヘルパーの呼び出し 0 回**でベースを取り、ビルドが完了し、
     `--network none` で `age --version`（v1.3.1）と `pg_dump --version`（16.15）が動いた。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A** | **ベースの取得元を `mirror.gcr.io/library` に替え、digest はそのまま**（`ARG BASE_REGISTRY` で上書き可）（**採用**） | ○ 原因（チャレンジ → ヘルパー）を経路から外す。digest 固定のため**中身は替わらない**（ミラーが別のものを返せば照合でビルドが止まる）。スクリプト・CI・取り込みの経路は無変更。frontend（IADR-0081）と同じ形で、同じ機械で成立が実測済み |
| B | ビルドのときだけ空の `DOCKER_CONFIG`（`credsStore` の無い config.json）を与える | △ ヘルパーを呼ばない点は同じだが、docker CLI では同じ設定ディレクトリにある `cli-plugins`（buildx）・`contexts`・`currentContext` も外れ、ビルダーや接続先が替わり得る（Rancher Desktop の接続の仕方に依存し、実機で確かめられない）。docker.io の匿名のレート制限も残る |
| C | 取り込みの経路を替える（`k3d image import` / `nerdctl load` 等に寄せる） | ✗ 失敗はビルドのベースの取得で起きており、取り込みの段ではない。経路を替えてもベースの取得は残る |
| D | `public.ecr.aws/docker/library` や `ghcr.io` へ替える | ✗ どちらも匿名に 401 のチャレンジを返し、同じヘルパーの失敗を招く（IADR-0081 の実測） |
| E | 資格情報ヘルパーを直す（利用者の環境） | △ 根治だが利用者の機械の設定に依存し、直るまでバックアップが取れない。A と両立する（A はヘルパーの状態に依存しない） |

## 決定

1. `deploy/local/platform-backup/image/Dockerfile` の `FROM` を `${BASE_REGISTRY}/postgres:<タグ>@sha256:<同じ digest>` とし、
   `ARG BASE_REGISTRY=mirror.gcr.io/library` を最初の `FROM` の前に置く。**digest は替えない。**
2. `BASE_REGISTRY` は逃げ道として残す（`--build-arg BASE_REGISTRY=docker.io/library` で従来の取得元へ戻せる。ヘルパーが動く環境でのみ通る）。
   `LOCAL_ONLY_IMAGES` に build args の欄は足さない（既定で足りる。起動スクリプトの経路では上書きしない）。
3. `scripts/platform-backup.test.js` の 10 番で、FROM の `${…}` を ARG の既定で解いてから、取得元が
   「匿名の取得にチャレンジを返さないことを実測した取得元」（現在は `mirror.gcr.io/library` だけ）であることを固定する。
   docker.io（取得元の省略を含む）・`public.ecr.aws`・`ghcr.io` へ戻す変異を落とす。
4. `scripts/k8s-local-images.sh` の WARN（原因が `registry` の分岐）から「Docker Hub へログインし直す」を外し、
   「ビルドのログの `load metadata` の行が `mirror.gcr.io/library/postgres` を指しているか」を最初の確認に置く。
   届くか確かめる先を `mirror.gcr.io` と `dl-cdn.alpinelinux.org` にする。起動を止めない（IADR-0471）ことは変えない。

## 結果・影響

- ビルドの全経路（起動スクリプトの nerdctl / docker の両経路、Runbook の手動ビルド、CI の `build-local (platform-backup)`）が
  同じ Dockerfile を読むため、経路ごとの変更は要らない。取り込み（`k3d image import`・nerdctl の `k8s.io` 名前空間への直接ビルド）は変わらない。
- イメージの中身・タグ（`platform-backup:pg16.15-age1.3.1-r6`）は変わらない（同じ digest のベース・同じ age）。CronJob の参照も変わらない。
- 外部依存: `mirror.gcr.io` の提供継続性を新たに負う（frontend と同じ）。ミラーが該当の digest を返さなくなったら、
  ビルドはメタデータの取得で止まる（黙って別の中身にはならない）。そのときは `BASE_REGISTRY` で取得元を替える。
- ヘルパーそのものの故障は残る（他のチャレンジを返すレジストリから取るときに再発する）。直すのは利用者の環境の作業で、本決定の範囲外。

## 残余リスク

- 稼働 PoC（Rancher Desktop）の実機でのビルドは、本作業では実施していない。根拠は IADR-0081 の同じ機械での実測（mirror.gcr.io からの
  取得とビルドの成立）と、本作業の再現実験（素の docker で、壊れたヘルパーの下でヘルパーを呼ばないこと）である。
  PoC 側での起動スクリプトの再実行（#1709 の ③）で確かめる。
- `mirror.gcr.io` は Docker Hub のうち取得の多いイメージのキャッシュである。版を上げるとき（Runbook §6）に、新しい digest が
  ミラーから 200 で返ることを確かめる手順を Runbook に置いた。
