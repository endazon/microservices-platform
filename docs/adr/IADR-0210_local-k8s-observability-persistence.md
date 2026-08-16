---
title: IADR-0210 経路B の Qdrant と可観測性 4 種も Deployment のまま PVC を足して永続化し、Prometheus の保持期間は time ＋ size で「溢れない形」にする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0006
  - IADR-0066
  - IADR-0077
  - IADR-0079
  - IADR-0082
  - IADR-0168
  - IADR-0179
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md (可観測性)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md (経路B の実行基盤)"
  - "../../planning/projects/microservices-platform/02_requirements/ (NFR 運用性・可観測性・信頼性)"
---

# IADR-0210: 経路B の Qdrant／可観測性 4 種の永続化と Prometheus 保持期間

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性・可観測性・信頼性＝Pod 再起動でデータを失わない）／ADR-0006（可観測性）

  > ［2026-08-16 追記 / #787］ **なぜ無採番の `NFR` で、`NFR-19` を当てなかったのか**（波 7 末クロス監査の指摘）。
  > 本 ADR は判定根拠を書いていなかった。**判断は「無採番で妥当」で変わらないが、根拠を残す。**
  >
  > - 計画 `02_requirements/01_requirements.md` の `NFR-19` は
  >   「**可観測性 / メトリクス・ログ・分散トレースを全サービスで収集**（Prometheus/Loki/Jaeger）」である。
  >   同表の直前注記は「**本表の射程は「稼働する製品」の要件である**」と定める（確定 2026-08-11 / planning#311）。
  > - **本 ADR の射程は dev 経路B に閉じる。** 本番像（`deploy/helm/microservices-platform/`）に
  >   prometheus / loki / tempo / grafana / qdrant の**ワークロードは 1 つも無い**（実測。
  >   `grep -rniE "prometheus|loki|tempo|grafana|qdrant" deploy/helm/microservices-platform/` が当たるのは
  >   `values.yaml` のコメント 2 行だけで、`templates/` は 0 件）。したがって本 ADR の変更は
  >   **稼働する製品の可観測性を 1 ミリも動かさない** —— `NFR-19` を当てると
  >   「その NFR の実装」として監査に数えられ、**無採番より劣化する**（同注記の逐語）。
  > - **同じ `deploy/local/observability/` を触った先行コミット `39d6973b`（#678 / [[IADR-0168]]）は
  >   `fix(NFR-19,IADR-0168):` と採番付きを使っている。** これは**矛盾ではない** ——
  >   あちらは **Grafana provisioning の経路間乖離**（compose と k8s でダッシュボード定義が食い違う）を
  >   埋めるもので、**「収集した可観測性データが実際に読める」という製品側の要件に直結する**。
  >   本件は**再起動をまたいで dev のデータが残るか**であり、収集そのものには触れていない。
  > - 起点 ID は [[IADR-0179]] 決定 1 の無採番 `NFR`。**無いことは「実装側で採番してよい」ではない**
  >   （同 決定 2）。**環流しない。**
- 関連 ADR: [[IADR-0082]]（経路B 基盤インフラの永続化。**qdrant の PVC 化を明文で却下した決定＝本 ADR が覆す**）／
  [[IADR-0079]]（compose 側の永続化。§3 の「config を書き換えず既存 storage パスへマウント」と `user: "0:0"` の
  実測が本 ADR の先例）／[[IADR-0077]]（経路B の可観測性 opt-in オーバーレイ）／[[IADR-0066]]（経路B の `emptyDir` 割り切り）
- 関連仕様書: `docs/specs/20260816_issue-787_k8s-observability-persistence.md`
- Issue: #787

## コンテキストと課題

経路B（`deploy/local/`）の永続化は 2 回に分けて入っており、本 issue の 4 ワークロードは**どちらの射程にも入っていない**。

| 決定 | 射程 | 対象 |
| --- | --- | --- |
| [[IADR-0079]] | **compose のみ**（§4 が経路B を明示的に対象外と宣言） | Keycloak / Loki / Tempo |
| [[IADR-0082]] | 経路B の**基盤インフラ**（opt-in `PERSIST=1`） | Keycloak / Postgres |

- **Qdrant**: IADR-0082 の「却下した代替案」が「qdrant / rabbitmq / redis / otel も PVC 化」を**明文で却下**している
  （理由＝「embeddings は再生成可能な派生データ（dev で再 ingest はまれ）」）。
- **Prometheus / Loki / Tempo（経路B）**: IADR-0079 §4 が「経路B は対象外」、IADR-0082 が対象を Keycloak/Postgres に
  限定。**どちらの射程からも外れる**。
- **Grafana（経路B）**: issue は挙げていないが、compose が `grafana-data:/var/lib/grafana` を持つのに k8s 側は
  ConfigMap 3 本のみで未マウントという、**Prometheus/Loki/Tempo とまったく同じ型のパリティ差**である。

決めるべきは 5 点: (1) ワークロードの形（StatefulSet 化するか）、(2) オーバーレイの置き場と有効化ゲート、
(3) 保持期間の指定方法、(4) Loki/Tempo の書き込み権限、(5) Grafana を射程に入れるか。

## 検討した選択肢

| 論点 | 案 A | 案 B | 採用 |
| --- | --- | --- | --- |
| (1) 形 | StatefulSet 化して `volumeClaimTemplates` を使う | Deployment のまま別建て PVC を足す | **B** |
| (2) 置き場 | 可観測性も `infra-persistence` へ相乗り | 対になる別オーバーレイ `observability-persistence` を新設 | **B** |
| (3) 保持期間 | `retention.time` だけ指定 | `time` ＋ `size`（`size < PVC 容量`） | **B** |
| (4) 権限 | 全 4 種へ `runAsUser: 0` を付けて安全側に倒す | compose が `user: "0:0"` を付けているものだけに付ける | **B** |
| (5) Grafana | issue の逐語どおり 3 種に絞る | 同型のパリティ差として本 PR に含める | **B** |

## 決定

### 1. 4 つとも「Deployment のまま PVC を足す」（StatefulSet 化しない）

本リポに `kind: StatefulSet` は **0 件**（実測）であり、既存 PVC の 4 例（postgres / keycloak / minio / wikijs）は
**すべて** Deployment ＋ 別建て PVC である。[[IADR-0082]] は StatefulSet 化を「単一レプリカ dev では順序保証・
安定ネットワーク ID が不要」として**明文で却下**しており、その判断はいまも成り立つ。既存の形に揃える。

### 2. 可観測性は**対になる別オーバーレイ** `deploy/local/observability-persistence/` に置く

`infra-persistence` へ相乗りさせない。理由は**ゲートが違う**ことである —— 可観測性スタックは
`OBSERVABILITY=1` でしか立たず、base（`deploy/local/observability`）は `deploy/local/infra` の
kustomization に**含まれていない**（[[IADR-0077]] の fail-safe）。相乗りさせると
「`PERSIST=1` だけで可観測性の PVC が作られる（が Pod は無い）」という宙に浮いた状態を作ってしまう。

`scripts/k8s-local-up.sh` は `INFRA_KUSTOMIZE` と**完全に同型**の `OBS_KUSTOMIZE` を
`OBSERVABILITY=1` ブロックの中に持ち、**`PERSIST=1` かつ `OBSERVABILITY=1`** のときだけ永続化版を選ぶ。
既定（`PERSIST` 未設定）は base ＝挙動不変・後方互換・fail-safe（provisioner 不在クラスタで Pod Pending 化させない）。

**Qdrant は既存の `infra-persistence` へ足す**（新規ディレクトリを作らない）。base が `deploy/local/infra` に
あり、ゲートが `PERSIST=1` だけで完結するためである。patch は postgres と同型の
JSON6902 `replace /spec/template/spec/volumes/0`。base に volumeMount が既にあるので二重に足さない。

**マウント先は config の実値から取り、config は 1 行も書き換えない**（[[IADR-0079]] §3 が確立した作法）。
`loki` は `common.path_prefix`、`tempo` は `storage.trace.local.path` / `wal.path` の親である。

### 3. Prometheus の保持期間は `time` ＋ `size` を args で明示する

```
--storage.tsdb.retention.time=7d
--storage.tsdb.retention.size=4GB
```

- **`size` も入れるのが本決定の肝である。** `time` だけでは remote-write の流入量次第で **PVC が満杯になり
  書き込み不能**になる。`size`（4GB ＝ 4.0e9 B）を PVC 容量（5Gi ＝ 5.37e9 B）**未満**に置けば、
  流入が増えても古いブロックから落ちるだけで**構造的に溢れない**。
  これは「設定した」ではなく「**壊れない形にした**」に当たる。
- **7d を選ぶ理由**: dev ローカル用途である。本番像（`deploy/helm/microservices-platform/templates/`）には
  本 issue の 4 ワークロードが**1 つも存在しない**（実測）。本 ADR の射程は **dev 経路B に閉じる**。
- **compose（`deploy/docker-compose.yml` の `prometheus.command`）にも同じ 2 引数を入れる。**
  現状は両方とも retention 無指定で**対称**であり、片方だけ入れると**新たなパリティ差**を作るためである。
- Tempo の `compactor.compaction.block_retention: 1h` は**別物**（Tempo 自身の圧縮設定）であり、触らない。

### 4. Loki / Tempo にだけ root 実行を付ける —— 判断基準は「**compose を鏡にする**」

`loki` / `tempo` の Pod へ `securityContext: {runAsUser: 0, runAsGroup: 0}` を足す。
compose の `user: "0:0"` の等価物である。

**この判断の根拠は推測ではなく実測された先例である。** [[IADR-0079]] §3 は、空の名前付きボリュームが
root:root 0755 で生成され、非 root イメージ（uid 10001）が直下に index/chunks/wal を作れず
**起動時 permission denied で回帰した**ことを根拠に `user: "0:0"` を入れた。PVC（local-path provisioner）でも
同じ性質があるため、同じ措置を採る。

**Prometheus と Grafana には付けない。** compose も両者に `user:` を付けておらず、compose では
`prometheus-data:/prometheus` と `grafana-data:/var/lib/grafana` が**現に動いている**。
「安全側に倒して全部 root にする」は、**動いている実測を根拠なく広げる**ことであり採らない
（root 実行は本来減らすべきものである）。

**基準そのものを機械が見る** —— `scripts/k8s-local-up.test.js` は compose と k8s の**両側から読んで**、
`user: "0:0"` を持つサービスの集合と `runAsUser: 0` を持つサービスの集合が**一致すること**を検査する。
片方だけ動かした瞬間に落ちる。

### 5. Grafana も本 PR の射程に入れる

issue の逐語は 3 種だが、Grafana は**同型・同じ overlay・追加コスト小**（PVC 1 本 ＋ patch 1 つ）である。
経路B には provisioning されないダッシュボード（AST overview 等）を UI から import する運用が実在し
（`deploy/local/observability/README.md`）、**再起動のたびにそれが消える**のは同じ実害である。
落とすと同じ PR の中で「解消した」と「残した」が混在し、次に読む人が「なぜ Grafana だけ？」を毎回引き直す。

## 理由

- **既存の形（Deployment ＋ 別建て PVC）に揃える**ことで churn を最小にし、IADR-0082 の却下理由を尊重する。
- **ゲートの意味論を壊さない**（可観測性の PVC は可観測性ゲートの下にだけ現れる）。
- **config を単一情報源に保つ**（マウント先は config から導かれ、両側から突き合わされる）。
- **retention は「形」で塞ぐ**（設定値の善し悪しではなく、`size < PVC 容量` という不等式で壊れ方を消す）。

## 結果

- 良い影響: `PERSIST=1`（＋ `OBSERVABILITY=1`）で dev の embeddings・メトリクス・ログ・トレース・
  Grafana 設定が Pod 再起動を跨いで残る。compose とのパリティ差（Prometheus / Grafana の未マウント、
  retention 無指定）が解消する。
- 悪い影響・トレードオフ:
  - **Loki / Tempo を root で動かす**（compose と同じ妥協。dev 専用オーバーレイに閉じ、本番像には及ばない）。
  - **PVC が 5 本増える**（qdrant-storage / prometheus-data / loki-data / tempo-data / grafana-data）。
    provisioner 不在クラスタでは Pending になりうるため、既定オフ（opt-in）を維持することが前提である。
  - **`down` → `up` では PVC も消える**（IADR-0082 と同じ制約。namespace/クラスタごと消えるため）。
- フォローアップ:
  - **★ 稼働クラスタでの受け入れが未了である。** 実装環境に `kubectl` / `helm` / `k3d` / `kustomize` が
    **いずれも無く**、クラスタも無い（実測）。したがって
    **(a)「Pod 再起動でデータが残る」と (b)「保持期間が実効している」は測っていない**（[[IADR-0184]]）。
    機械で確かめたのは `scripts/k8s-local-up.test.js` の範囲 —— ゲート分岐・PVC の属性・
    **マウント先が config の実値と一致すること**・compose とのパリティ・`size < PVC 容量`、まで。
    配備時に次を確かめること: 本 ADR が足した PVC がすべて `Bound`（`kubectl -n platform-infra get pvc`）、
    `kubectl -n platform-infra delete pod -l app=qdrant` 後にコレクションが残る、
    `curl prometheus:9090/api/v1/status/runtimeinfo` の `storageRetention` が `7d` を返す。
  - rabbitmq / redis / otel-collector は引き続き非永続（IADR-0082 の却下理由が成り立つ）。

## 関連

- Supersedes: なし（[[IADR-0082]] の**却下した代替案の一部**（qdrant）を覆すが、決定本体は生きているため
  Supersede ではなく**追補**の関係にある。IADR-0082 側に `［2026-08-16 追記 / #787］` を入れて後継 ID を併記した）
- Superseded by: なし
