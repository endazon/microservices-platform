---
title: 作業仕様書 — 切替リハーサル（#457）のうち CI の k3d で再現できる部分を workflow_dispatch のジョブにする（#1781）
type: spec
status: in-progress
related_ids: [NFR-05, NFR-18, IADR-0459, IADR-0461, IADR-0488, IADR-0515]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1781"
---

# 作業仕様書 — 切替リハーサルの CI 化（#1781。#457 の切り出し）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `1d71b2b3`（shallow でない）。
> 🔴 **稼働クラスタには何も実行しない。** 破壊的な手順は CI のランナーが作る k3d クラスタ（ジョブの終わりに消える）にだけ当たる。

## 起点（トレーサビリティ）

- 非機能要件: **NFR-05**（可用性・運用。切替の手順とリハーサル）、**NFR-18**（秘密は合成値だけを使う・argv へ載せない）。
- 親: #457（切替計画。`blocked:env`）。独立監査 #1773 が「CI の k3d で回せる部分は AI で先行できる」と指摘した。
- 手順の正本: `docs/migration/cutover-discard-and-rebuild.md`（§手順・§リハーサル）。判定器: `scripts/measure-cutover-inventory.js`（IADR-0459）。
- 起こし方の正本: `.github/workflows/integration-stack.yml`（`scripts/k8s-local-up.sh` ＋ `scripts/check-stack-ready.js`）。
- 新しい設計判断は IADR-0515（新しいワークフローにする・手順を scripts/ の道具にしない・一時の pull_request 起動・検証スクリプトの追随）。

## 受け入れ基準

- AC1: CI の k3d で、破棄・再構築の手順の再現できる部分が、少なくとも 1 回 success で走る（run ID を PR 本文に書く）。
- AC2: 再現できない手順の一覧と理由を、#457 に記録する文面として本書と PR 本文に置く（**#457 への投稿は利用者がマージ後に行う**）。
- AC3（否定形）: 既存のワークフローの起動条件と必須チェック（`docs/ai-workflow.md` の表）を変えない。`integration-stack.yml` は 1 バイトも変えない。
- AC4（否定形）: リポジトリに鍵らしい偽の秘密を書かない。秘密はジョブの中で乱数から作り、伏せる。
- AC5: 終状態を機械で判定する —— 検証スクリプト（`--since` ＋ `--baseline`）と readiness の門がどちらも緑、合成の利用者が消えている、
  事前実測が空振りでない（点・オブジェクト・document_svc の行・合成の利用者が在る）、陰性対照 2 本が fail を出す。

## 仕分け（AC2 の本体）

移行仕様書の手順を、CI の k3d（GitHub ホストランナー・使い捨て・合成データ）で再現できるかで分ける。

### 再現できる（ワークフロー `cutover-rehearsal.yml` が行う）

| 手順 | ワークフローでの形 |
| --- | --- |
| リハーサル 1: 起動器で経路B を立てる | integration-stack と同じ環境変数（`LOCALEDGE=1 ABACSEED=1 SEARCHSEED=1 LOCALEMBED=1`・メッシュ既定オン）と同じ pin（k3d・k3s） |
| リハーサル 2: データを入れる | 検索検証用の文書（`seed-search-documents.js`）・ABAC の seed・合成の利用者 1 人（realm `platform`）。索引に点が入るまで待つ |
| 1: 事前実測 | `measure-cutover-inventory.js --live --dump before.json`。空振りでないこと（点・オブジェクト・document_svc の行・合成の利用者）を判定する |
| 2: 静止（破棄の開始時刻の記録） | `--since` の時刻を記録する。サービスは止めない（移行仕様書のとおり） |
| 3(a): MSP の DB を作り直す | `--print-recreate-sql` を psql へ流す |
| 3(b): MSP のキューの滞留を空にする | 接頭辞を pipeline.json から導き、滞留のあるキューだけを purge する |
| 3(c): realm を消す | realm `platform`（と旧名。無ければ 404 を無視） |
| 3(d): PVC ごと作り直す | `qdrant-storage`・`seaweedfs-data`・`wiki-js-data`（可観測性は無効なので PVC が無い） |
| 4: 再構築 | Keycloak の再起動・MSP の再起動・起動器の再実行（`ABACSEED=1 TAGSEED=1`。`SEARCHSEED` は付けない） |
| 5: 検証 | `measure-cutover-inventory.js --live --since --baseline` と `check-stack-ready.js --live` がどちらも緑 |
| リハーサル 3: 窓の長さを測る | 2 の開始から 5 の緑までの秒数をジョブの要約に出す（このランナーでの値） |
| リハーサル 4: 陰性対照 | AST の DB を 1 つ DROP（`--baseline` つきで fail）／`postgres-data` を消して作り直す（触らない側が fail） |

### 再現できない（#457 に残す。理由つき）

下の表が #457 へ記録する文面の本体である（後掲「#457 への記録文」）。

| 手順 | 理由 | 分類 |
| --- | --- | --- |
| 0-1 go-live の前提（BFF セッション方式の完了・セキュリティ暫定運用の解消）の確認と、切替を go-live と同時に行うかの判断 | 運用判断。CI に判定の材料が無い | 利用者の判断 |
| 0-2 Vault に秘密情報の画面で入れた値が残っていることの確認 | 稼働クラスタの Vault の実値。CI のクラスタは `VAULT` を立てない（立てても中身は合成値） | 資格情報・利用者の環境 |
| 0-3 realm の client secret を宣言値から変えていないかの確認 | 稼働の realm の実値との比較であり、CI のクラスタは宣言値そのもので立つ | 資格情報・利用者の環境 |
| 0-4 オーナーが実行時に作った利用者の書き出し | 実データ（実在の利用者） | 実データ |
| 0-5 起動器を「今の稼働クラスタと同じ環境変数」で再実行できることの確認 | 稼働クラスタの環境変数（`OBSERVABILITY` / `VAULT` / `ARGOCD` / `ESO` ほか）は利用者の環境にしか無い。CI は integration-stack の組み合わせで代える | 利用者の環境 |
| 0-6・6-4 AST の身元（`ai-stock-trading-*` クライアントの secret・実行時に付けた `trading-owner` などのロール）の書き出しと復元 | 稼働の AST の資格情報と実行時の付与。CI は AST を配備しない（同居するのは AST の DB と realm だけ） | 資格情報・外部連携 |
| 0-7・2・6-5 ArgoCD の自動同期の停止と復帰 | CI のクラスタは `ARGOCD` を立てない。稼働クラスタの設定 | 利用者の環境 |
| 2 AST の書き込み（取り込み・LLM 呼び出し）の停止、6-6 再開と取り込みが 400 にならないことの確認 | AST の配備と売買 PoC の運用判断。外部 LLM の呼び出しを伴う | 外部連携・利用者の判断 |
| 5 の後段: AST の読み手の資格情報が `ast-secrets` に載っていること | AST の名前空間と ESO（`ESO=1`）が要る。CI のクラスタには無い | 外部連携・資格情報 |
| 6-1 TOTP の登録し直し、6-2 実行時に作った利用者の作り直し、6-3 secret の配り直し | 人の資格情報（TOTP は人の端末） | 資格情報 |
| 任意の保全（`pg_dumpall`・realm の書き出し） | 取るかどうかはオーナーの判断。中身は実データ（AST の DB を含む） | 実データ・利用者の判断 |
| 窓の時刻の選定 | 売買 PoC を止めてよい時間帯の判断 | 利用者の判断 |
| 窓の長さの本番の見積り | CI で測る秒数はランナーと合成データでの値であり、稼働クラスタのデータ量・ディスク・メッシュの構成を反映しない（参考値に留める） | 実データ・利用者の環境 |
| 旧 ArgoCD Application・イメージ・不要ブランチの整理 | 稼働クラスタ・レジストリ・リモートへの破壊的操作 | 利用者の環境 |

### 部分的に再現できる（CI での形と、残るもの）

- **可観測性の PVC（Prometheus / Loki / Tempo）**: CI は `OBSERVABILITY` を立てない（integration-stack と同じ）ので、3(d) の該当行と検証の行は skip になる。
  稼働クラスタで `OBSERVABILITY=1` なら、その行は稼働でしか確かめられない。
- **Vault の PVC（`vault-data`）を消していないこと**: CI は `VAULT` を立てないので skip。
- **メッシュの mTLS が STRICT の構成**: CI は初回の既定（PERMISSIVE）で立つ。検証スクリプトの Qdrant / Prometheus の読みは API サーバのサービスプロキシ経由であり、
  STRICT では通らないことがある（移行仕様書の環境変数の注記）。稼働で STRICT なら port-forward の URL を渡す。

## 母集合（規則 9・10。`1d71b2b3` 時点）

### 規則 9: 誤りの側の字面で走査した

- `minio-data` / `app=minio` / `app in (minio` / `parseMinioListing` / `minioObjects` / `data.minio`（`.ai-context/` を除く追跡下の全ファイル）:
  `docs/migration/cutover-discard-and-rebuild.md` の 3 行と `scripts/measure-cutover-inventory.js`・`scripts/scripts.repo.test.js`。
  **これが本件の対象である。**（`docs/operations/object-storage-seaweedfs-cutover-runbook.md` の 1 行は MinIO → SeaweedFS の移し替えの手順の中の
  旧名であり、正しい —— 除外。）
- `MinIO` の語（36 ファイル）のうち、切替（6 資産の破棄）の文脈にあるもの: 移行仕様書（判断表・破棄の境界・検証の表・手順 3(d)・4 の注記・未決事項）と
  `scripts/README.md` の `measure-cutover-inventory.js` の行。他（ADR の経緯・SeaweedFS への移し替えの runbook・ツールの OIDC の撤去の注記ほか）は
  MinIO を**過去のもの**として正しく書いているので除外。
- `measure-cutover-inventory` / `cutover-discard-and-rebuild` を引くファイル（15）: 上記のほか `scripts/live-scripts.json`（引数の分類。変更なし）と
  `.ai-context/`（凍結記録。書き換えない）。
- `integration-stack` を名指しする試験（`scripts.repo.test.js`・`k8s-local-up.test.js`）: `integration-stack.yml` の手順の並び・`ISTIO` の行数・
  起動条件を固定している。**本件は `integration-stack.yml` を変えない**ので影響なし（新しいワークフローを別ファイルにした理由の 1 つ。IADR-0515）。

### 規則 10: この変更で新たに誤りになる自分の記述

- 移行仕様書の「検証スクリプトの収集部は稼働環境で未検証」（未決事項）: CI の k3d で収集部を通したので、**CI で確かめた範囲と残る範囲**へ書き直す。
- 移行仕様書の §オーナー作業「リハーサルの実施 —— 使い捨てクラスタが要る」: CI で回せる部分ができたので、残る部分へ絞って書き直す。
- `scripts/README.md` の `measure-cutover-inventory.js` の行の「MinIO のオブジェクト 0」「収集部は稼働環境で未検証」: 追随する。
- 導出値: 「AST の DB 7 本・MSP 13 本」は初期化 SQL から試験が計算する（文書に数を書かない）。

## 設計

- 新しいワークフロー `.github/workflows/cutover-rehearsal.yml`（`workflow_dispatch`）。integration-stack に足さない理由・手順を scripts/ に置かない理由・
  一時の `pull_request`（paths は本ファイルだけ）を置く理由は IADR-0515。
- 起こし方は integration-stack と同じ手順を写す（`k8s-local-up.sh` と `check-stack-ready.js` を呼ぶ・同じ待ち・同じ pin）。pin が揃っていることは
  `scripts.repo.test.js` が突き合わせる。
- **SeaweedFS への追随**: MinIO は 2026-09-25 に SeaweedFS へ置き換わった（#1499 / IADR-0461）が、検証スクリプトと移行仕様書は MinIO のまま
  （PVC `minio-data`・Pod `app=minio`・`ls -R /data`）だった。このままでは正しく作り直しても「存在しない」「読めなかった」の fail になり、
  AC1 は原理的に満たせない。PVC を `seaweedfs-data` へ、収集を filer の一覧（Pod の loopback の `127.0.0.1:8888`。Pod 内の `wget`）へ直す。
  オブジェクト数は filer のディレクトリのビット（Go の `os.ModeDir`）で数え、`.uploads`（マルチパートの途中）は数えない。
- 秘密: Keycloak の管理者パスワードだけをジョブの中で `openssl rand` から作り、`::add-mask::` で伏せ、起動器（`KEYCLOAK_ADMIN_PASSWORD`）と
  検証スクリプト（`CUTOVER_KC_ADMIN_PASSWORD`）へ渡す。kcadm へは標準入力で渡す（#1793）。

## 実行の記録

### run 37734487908（`pull_request`・head `9e16d5e1`）— failure（手順 5 の検証）

- 起動・事前実測（空振りでない: Qdrant の点 3・オブジェクト 3・合成の利用者あり）・静止・破棄 3(a)〜(d)・再構築は緑。窓（2 の開始から 4 の終わり）は約 5.5 分。
- 検証スクリプトの fail 2 件。**いずれも検証スクリプトの側の誤りであり、手順は正しく動いていた**:
  1. `[Keycloak] 人間の利用者はすべて作り直し後に作られた` — seed 利用者 4 人が「作り直し前」。realm.json は利用者の `createdTimestamp` を宣言せず、
     取り込みは宣言の値をそのまま入れるので、取り込んだ利用者は作成時刻を持たない（判定は `null >= since` で偽）。合成の利用者は消えており
     （利用者 5 → 4）、realm は作り直されていた。→ 作成時刻の無い利用者は `--baseline` の同じ利用者の ID と比べる（realm.json は ID を宣言しないので、取り込み直すと変わる）。
     収集に `id` を足す。
  2. `[オブジェクトストレージ] オブジェクトが 0 件` — バケット `.system` に 1 件。SeaweedFS の内部の置き場であり、S3 のバケット名になり得ない（先頭が `.`）。→ 数えない。
- ワークフローの誤り: 検証の手順で `m=$?` の形にしていたため、既定のシェル（`bash -e`）が検証スクリプトの赤で手順を終え、`check-stack-ready.js` が走らなかった。→ `|| m=$?` の形へ。
  失敗時の材料として、記録の手順に事前・事後の利用者（ID・作成時刻）とオブジェクトストレージの生の値を出す。
- 同じ head の `static-checks` の赤（`Check knowledge graph edge existence`）は、作業仕様書が `related_ids` で引く IADR-0515 をまだ置いていなかったため（実在しないエッジ先 1 件）。IADR-0515 を置いて解消。

（以降の run をここへ追記する。）
