---
title: IADR-0082 経路B（ローカル k8s dev）の基盤インフラは opt-in kustomize オーバーレイで Keycloak=H2-file-on-PVC／Postgres=data-on-PVC として local-path 永続化する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0004
  - IADR-0066
  - IADR-0079
  - IADR-0210
author: claude
created: 2026-07-19
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md (認証＝Keycloak)"
  - "../../planning/projects/microservices-platform/02_requirements/ (NFR 運用性・信頼性)"
---

# IADR-0082: 経路B 基盤インフラの永続化（opt-in オーバーレイ・Keycloak/Postgres を local-path PVC 化）

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性・信頼性＝Pod 再起動でインフラ状態を失わない）／ADR-0004（認証＝Keycloak）
- 関連 ADR: [[IADR-0066]]（経路B＝k3d + dev 専用 in-cluster インフラ。`emptyDir` 割り切りを採用した決定＝本 ADR が
  見直す対象）／[[IADR-0079]]（compose 側の永続化。別レイヤの先例・Keycloak 方式で対比する）
- 関連仕様書: `docs/specs/20260719_issue-324_infra-persistence-k8s.md`
- Issue: #324（運用/dev・priority:should。#282＝PR #323 が経路B を明示除外したためのフォローアップ）

## コンテキストと課題

経路B（`deploy/local/`）の infra は [[IADR-0066]] の割り切りで `emptyDir` であり、`deploy/local/README.md` にも
「永続化なし: infra は emptyDir（Pod 再起動で再 init）」と明記されている。このため **Keycloak Pod が再起動する
たびに realm が再 import され、管理コンソールで加えた runtime state（追加ユーザー・シークレット・セッション等）が
失われる**（実害報告あり）。Postgres も `emptyDir` のため全アプリ DB が再 init される。

課題は「経路B の該当インフラを PVC 化して再起動でも状態を保持する」こと。決めるべき実装上の論点は次の 4 点:
(1) 有効化方式（既定変更 vs opt-in）、(2) Keycloak の永続バックエンド（H2-file-on-PVC vs 共有 Postgres 外部 DB）、
(3) kustomize での volume 差し替え方法、(4) realm import 冪等性と更新反映手順。

## 決定

### 1. opt-in kustomize オーバーレイ（`PERSIST=1`）で有効化し、既定は現行 emptyDir を不変に保つ

`deploy/local/infra-persistence/`（kustomize オーバーレイ）を新設し、base `deploy/local/infra` を参照した上で
PVC を追加、postgres/keycloak の Deployment に volume/volumeMount パッチを当てる。`scripts/k8s-local-up.sh` は
**`PERSIST=1`** のとき適用先を `deploy/local/infra` → `deploy/local/infra-persistence` へ切り替える。

- **既定（env 未設定）は従来どおり `emptyDir`（挙動完全不変）**。これは (a) 後方互換、(b) `local-path` 等の動的
  provisioner や既定 StorageClass が **無いクラスタで PVC が Pending のままとなり Pod が起動しない失敗**を、opt-in に
  しないと既定経路へ持ち込むため。opt-in なら「provisioner がある環境だけが明示的に有効化する」形で **fail-safe** を保てる。
- 既存の opt-in オーバーレイ（`OBSERVABILITY=1` / `VAULT=1` / `ARGOCD=1` / `HEADLAMP=1`）と同一の env ゲート慣習に
  そろえる。compose 側（[[IADR-0079]]）は**既定オン**を採ったが、compose の名前付きボリュームは provisioner 不要で
  常に成立するのに対し、k8s の PVC は StorageClass 不在時に Pod Pending という**より重い失敗**を招くため、経路B では
  opt-in を選ぶ（基盤差に由来する方式差。理由は上記 (b)）。

### 2. Keycloak は H2-file-on-PVC（`/opt/keycloak/data`）で永続化し、共有 Postgres 外部 DB 化はしない

`start-dev` の既定 DB は **file ベースの H2（`${kc.home.dir}/data/h2`）** であり、`/opt/keycloak/data` に PVC を
マウントすれば realm と全 runtime state が永続化される。compose 側（[[IADR-0079]] §1）は Keycloak を**共有 Postgres の
`keycloak` DB**へ外部 DB 化したが、経路B では **H2-file-on-PVC** を採る。理由:

- **基盤差＝起動順の結合を避ける**: compose には `depends_on: postgres(condition: service_healthy)` があり Keycloak を
  Postgres 準備完了後に起動できる。k8s(経路B)には Pod レベルの同等機構が無く、Keycloak を Postgres 外部 DB 化すると
  Postgres 準備前に起動して crashloop する（自己回復はするが不健全）か、initContainer（`pg_isready` 待ち）等の追加
  複雑性を要する。**独立 PVC（H2）は Keycloak を Postgres 起動順に結合させず、オーバーレイを純加算パッチに保てる**。
- **障害分離とリセットが単純**: 各ステートフルサービスが独立 PVC を持つため、「片方の PVC だけ消してリセット」が容易。
- **ユーザー指示と整合**: 本 issue は「Keycloak のストレージ（H2 file or postgres データ）を PVC に載せる」と両案を許容。
- したがって [[IADR-0079]] §103 の「単一ファイル H2 は共有インフラ流用方針に反する」という却下理由は**compose 固有**
  （既に共有 Postgres が同一 compose 内にあり非対称になる文脈）であり、独立した k8s Pod 群で各々に PVC を与える経路B
  には当てはまらない。realm import 冪等性・更新反映の運用差分（§4）は両方式で同一。

### 3. volume 差し替えは kustomize の JSON6902 パッチ（明示 index/append）で行う

- **postgres**: base の `volumes[0]`（name=`data`・`emptyDir`）を `persistentVolumeClaim: {claimName: postgres-data}`
  へ `replace`。init ConfigMap マウント（`volumes[1]`）と volumeMount は不変。
- **keycloak**: base は `/opt/keycloak/data` を**マウントしていない**ため、volume（`keycloak-data`）と volumeMount
  （`/opt/keycloak/data`）を `add`（append）する。realm import 用 ConfigMap は `/opt/keycloak/data/import`（readOnly）に
  マウント済みで、PVC マウント（親パス）配下に**入れ子**で重なる（kubelet はパス深さ順にマウントするため import が上書き
  overlay される）。よって realm import は不変で機能する。
- strategic-merge で `emptyDir: null` により消す方式は list 要素内フィールド削除の挙動が曖昧なため採らず、対象を一意に
  指す JSON6902 を用いる（base の volume 順序は postgres=[data,init]・keycloak=[realms] で安定）。

### 4. realm import 冪等性と更新反映手順を docs に明記する

H2 永続化後は `--import-realm` が **既存 realm をスキップ**（既存を上書きしない）するため、`realm.json` の編集は
そのままでは反映されない（compose の [[IADR-0079]] と同じ運用差分）。runtime state 保持（本 issue の目的）と realm
定義の再現性（Git 単一情報源）のトレードオフを、次の運用手順で担保する（`deploy/local/README.md` /
`docs/operations/operations.md`）:

- **破壊的（推奨・realm を作り直してよい）**: `keycloak-data` PVC を削除して Pod を再作成 → 空 PVC に `--import-realm` が
  最新 `realm.json` を再投入。
- **非破壊（runtime state 保持・部分反映）**: 管理コンソール or `kcadm` の partial import で当該変更のみ適用。

## 影響

- **移行（重要）**: 既存のローカル環境で `PERSIST=1` に切り替えると、Deployment の volume 差分でローリング更新が走り、
  **初回は空 PVC → realm/DB が import/init で再生成**される。既存 emptyDir のデータは元々 Pod 生存期間のみの揮発データで、
  失うべき恒久データは無い（移行注記を README/operations に記載）。以後の再起動では PVC のデータが保持される。
- **fail-safe/後方互換**: `PERSIST` 未設定の既定経路は kustomize base・script・rollout 待ちすべて不変。CI（#275
  image-mapping・realm-constraints・ci.yml self-test）は本変更が touch しないため非回帰。realm.json 無改変。
- **[[IADR-0066]] の割り切りの見直し**: 「経路B infra は emptyDir で揮発許容」という当初方針を、**dev 常用時の runtime
  state 保持を opt-in で選べる**方針へ更新する（既定は従来どおり揮発を許容）。本 ADR がその根拠。
- 本番像（Helm/argocd/compose）・Headlamp・frontend・edge・realm client 定義には影響しない。

## 却下した代替案

- **既定オン（compose の [[IADR-0079]] と同様）**: StorageClass/provisioner 不在クラスタで PVC が Pending → Pod 起動
  失敗を既定経路に持ち込む。k8s では compose より重い失敗のため opt-in を採用（§1(b)）。
- **Keycloak を共有 Postgres 外部 DB 化（[[IADR-0079]] §1 と統一）**: k8s に compose の depends_on 相当が無く、起動順
  結合（crashloop）or initContainer の追加複雑性を招く。独立 PVC（H2）の方が純加算で単純（§2）。
- **qdrant / rabbitmq / redis / otel も PVC 化**: qdrant embeddings は再生成可能な派生データ（dev で再 ingest はまれ）、
  rabbitmq/redis は queue/cache で揮発前提、otel は stateless。損失影響が低く issue でも「必要に応じて」＝任意のため、
  スコープを Keycloak/Postgres に絞る（過剰実装を避ける）。qdrant は要すれば同型（PVC + patch）で拡張可能。

- **StatefulSet 化**: Deployment のまま volumeClaim を PVC 参照へ差し替える方が変更が小さく、単一レプリカ dev では
  StatefulSet の順序保証・安定ネットワーク ID は不要。churn を避け Deployment を維持する。

> **［2026-08-16 追記 / #787］上の「qdrant は要すれば同型で拡張可能」が予告した拡張を [[IADR-0210]] が行った。**
> **qdrant は対象へ入った** —— 「再生成可能」という判断自体は正しいが、**再生成の費用が無視できない**
> （埋め込み生成は LLM / TEI 呼び出しを伴い、コーパス規模に比例して時間と API 費用がかかる）。
> 加えて Pod は日次規模で再起動しており、**実測でコレクションは 0 件**だった。
> あわせて **Prometheus / Loki / Tempo / Grafana** も対象へ入った ——
> 本 ADR の `PERSIST` は `INFRA_KUSTOMIZE` しか差し替えておらず、**`deploy/local/observability` には
> 一切効いていなかった**（実測）。**rabbitmq / redis / otel は対象外のまま**である。
>
> **本 ADR の決定 1〜4 は有効。** [[IADR-0210]] が足したのは対象と、
> **PVC を掴む Deployment の `strategy: Recreate`** である。本 ADR はこれを付けておらず、
> postgres / keycloak が `ReadWriteOnce` + `RollingUpdate` のままだった（[[IADR-0210]] が遡って付けた）。
