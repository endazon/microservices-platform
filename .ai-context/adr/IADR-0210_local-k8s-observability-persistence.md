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
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md (可観測性)
  - planning:projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md (経路B の実行基盤)
  - planning:projects/microservices-platform/02_requirements/ (NFR 運用性・可観測性・信頼性)
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
  > - **同じ `deploy/local/observability/` を触った先行コミット `39d6973b`（#678 / [IADR-0168](./IADR-0168_grafana-provisioning-parity.md)）は
  >   `fix(NFR-19,IADR-0168):` と採番付きを使っている。** これは**矛盾ではない** ——
  >   あちらは **Grafana provisioning の経路間乖離**（compose と k8s でダッシュボード定義が食い違う）を
  >   埋めるもので、**「収集した可観測性データが実際に読める」という製品側の要件に直結する**。
  >   本件は**再起動をまたいで dev のデータが残るか**であり、収集そのものには触れていない。
  > - 起点 ID は [IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1 の無採番 `NFR`。**無いことは「実装側で採番してよい」ではない**
  >   （同 決定 2）。**環流しない。**
- 関連 ADR: [IADR-0082](./IADR-0082_local-k8s-infra-persistence.md)（経路B 基盤インフラの永続化。**qdrant の PVC 化を明文で却下した決定＝本 ADR が覆す**）／
  [IADR-0079](./IADR-0079_infra-persistence-compose.md)（compose 側の永続化。§3 の「config を書き換えず既存 storage パスへマウント」が本 ADR の先例。
  **ただし同 §3 の `user: "0:0"` は docker の named volume 固有の対処であり、k8s へは転用しない**＝決定 6）／
  [IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md)（経路B の可観測性 opt-in オーバーレイ）／[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（経路B の `emptyDir` 割り切り）
- 関連仕様書: `docs/specs/20260816_issue-787_k8s-observability-persistence.md`
- Issue: #787（**同じ issue に PR #815 と #816 の 2 本が並走し、利用者の裁定で 1 本へ統合した**。
  本 ADR は統合後の実装を記述する。経緯は仕様書 §12）

## コンテキストと課題

経路B（`deploy/local/`）の永続化は 2 回に分けて入っており、本 issue の 4 ワークロードは**どちらの射程にも入っていない**。

| 決定 | 射程 | 対象 |
| --- | --- | --- |
| [IADR-0079](./IADR-0079_infra-persistence-compose.md) | **compose のみ**（§4 が経路B を明示的に対象外と宣言） | Keycloak / Loki / Tempo |
| [IADR-0082](./IADR-0082_local-k8s-infra-persistence.md) | 経路B の**基盤インフラ**（opt-in `PERSIST=1`） | Keycloak / Postgres |

- **Qdrant**: IADR-0082 の「却下した代替案」が「qdrant / rabbitmq / redis / otel も PVC 化」を**明文で却下**している
  （理由＝「embeddings は再生成可能な派生データ（dev で再 ingest はまれ）」）。
- **Prometheus / Loki / Tempo（経路B）**: IADR-0079 §4 が「経路B は対象外」、IADR-0082 が対象を Keycloak/Postgres に
  限定。**どちらの射程からも外れる**。
- **Grafana（経路B）**: issue は挙げていないが、compose が `grafana-data:/var/lib/grafana` を持つのに k8s 側は
  ConfigMap 3 本のみで未マウントという、**Prometheus/Loki/Tempo とまったく同じ型のパリティ差**である。

決めるべきは 7 点: (1) ワークロードの形（StatefulSet 化するか）、(2) オーバーレイの置き場と有効化ゲート、
(3) 保持期間の指定方法、(4) compose とのパリティをどこまで鏡にするか、(5) Grafana を射程に入れるか、
(6) Loki/Tempo の書き込み権限、(7) PVC を掴む Deployment の更新戦略。

## 検討した選択肢

| 論点 | 案 A | 案 B | 採用 |
| --- | --- | --- | --- |
| (1) 形 | StatefulSet 化して `volumeClaimTemplates` を使う | Deployment のまま別建て PVC を足す | **B** |
| (2) 置き場 | 可観測性も `infra-persistence` へ相乗り | 対になる別オーバーレイ `observability-persistence` を新設 | **B** |
| (3) 保持期間 | `retention.time` だけ指定 | `time` ＋ `size`（`size < PVC 容量`） | **B** |
| (4) パリティ | compose の設定を丸ごと鏡にする | **鏡にするのはマウント先と retention 引数まで**（プラットフォーム固有の対処は写さない） | **B** |
| (5) Grafana | issue の逐語どおり 3 種に絞る | 同型のパリティ差として本 PR に含める | **B** |
| (6) 権限 | compose が `user: "0:0"` を付けている loki / tempo へ `runAsUser: 0` を付ける | **4 種とも非 root のまま**（local-path は `0777` で作る） | **B** |
| (7) 更新戦略 | base と同じ `RollingUpdate` のまま | PVC を掴む Deployment だけ `Recreate` へ落とす | **B** |

## 決定

### 1. 4 つとも「Deployment のまま PVC を足す」（StatefulSet 化しない）

本リポに `kind: StatefulSet` は **0 件**（実測）であり、既存 PVC の 4 例（postgres / keycloak / minio / wikijs）は
**すべて** Deployment ＋ 別建て PVC である。[IADR-0082](./IADR-0082_local-k8s-infra-persistence.md) は StatefulSet 化を「単一レプリカ dev では順序保証・
安定ネットワーク ID が不要」として**明文で却下**しており、その判断はいまも成り立つ。既存の形に揃える。

### 2. 可観測性は**対になる別オーバーレイ** `deploy/local/observability-persistence/` に置く

`infra-persistence` へ相乗りさせない。理由は**ゲートが違う**ことである —— 可観測性スタックは
`OBSERVABILITY=1` でしか立たず、base（`deploy/local/observability`）は `deploy/local/infra` の
kustomization に**含まれていない**（[IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md) の fail-safe）。相乗りさせると
「`PERSIST=1` だけで可観測性の PVC が作られる（が Pod は無い）」という宙に浮いた状態を作ってしまう。

`scripts/k8s-local-up.sh` は `INFRA_KUSTOMIZE` と**完全に同型**の `OBS_KUSTOMIZE` を
`OBSERVABILITY=1` ブロックの中に持ち、**`PERSIST=1` かつ `OBSERVABILITY=1`** のときだけ永続化版を選ぶ。
既定（`PERSIST` 未設定）は base ＝挙動不変・後方互換・fail-safe（provisioner 不在クラスタで Pod Pending 化させない）。

**Qdrant は既存の `infra-persistence` へ足す**（新規ディレクトリを作らない）。base が `deploy/local/infra` に
あり、ゲートが `PERSIST=1` だけで完結するためである。patch は postgres と同型の
JSON6902 `replace /spec/template/spec/volumes/0`。base に volumeMount が既にあるので二重に足さない。

**マウント先は config の実値から取り、config は 1 行も書き換えない**（[IADR-0079](./IADR-0079_infra-persistence-compose.md) §3 が確立した作法）。
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

### 4. compose を鏡にする —— ただし鏡にするのは**マウント先と retention 引数**までである

同じデータを持つサービスは、compose と k8s で**同じパスへマウントする**（`qdrant` = `/qdrant/storage` ／
`prometheus` = `/prometheus` ／ `loki` = `/tmp/loki` ／ `tempo` = `/tmp/tempo` ／
`grafana` = `/var/lib/grafana`）。Prometheus の retention 2 引数も両側へ同値で置く（決定 3）。

**鏡そのものを機械が見る** —— `scripts/k8s-local-up.test.js` は compose と k8s の**両側から読んで**、
同名データボリュームのマウント先が一致することと、retention 2 引数が同値であることを検査する。
片方だけ動かした瞬間に落ちる。

**ただし「鏡にする」を無制限に適用しない。** compose 側の設定には**docker というプラットフォーム固有の
対処**が混じっており、それを写すと k8s 側だけが不要な妥協を背負う。実際に `user: "0:0"` がそれに当たり、
本 ADR は**写さない**と決めた（決定 6）。鏡にしてよいのは、**両プラットフォームで同じ意味を持つもの**
（データの置き場所・保持期間）に限る。

### 5. Grafana も本 PR の射程に入れる

issue の逐語は 3 種だが、Grafana は**同型・同じ overlay・追加コスト小**（PVC 1 本 ＋ patch 1 つ）である。
経路B には provisioning されないダッシュボード（AST overview 等）を UI から import する運用が実在し
（`deploy/local/observability/README.md`）、**再起動のたびにそれが消える**のは同じ実害である。
落とすと同じ PR の中で「解消した」と「残した」が混在し、次に読む人が「なぜ Grafana だけ？」を毎回引き直す。

**Grafana は機械判定で出てきた 5 件目である。** issue の逐語（Qdrant と可観測性 3 種）を母集合として扱わず、
`deploy/local/` の Deployment 全 12 件を `volumes` / `volumeMounts` で機械的に判定した結果、
永続化されていないものが 5 件出た（仕様書 §2.4）。**「5 件測って 4 件だけ直す」は母集合の規則 7 の破れ**であり、
本リポで最も繰り返し起きている事故の型である。

### 6. 可観測性 4 種を **root 実行へ落とさない**（compose の `user: "0:0"` は k8s へ写さない）

`securityContext` は **4 種とも付けない**。compose の `user: "0:0"`（[IADR-0079](./IADR-0079_infra-persistence-compose.md) §3）は
**docker の named volume が root:root 0755 で生成される**ことへの対処であり、**k8s へは転用できない**。

- **local-path provisioner の setup は `mkdir -m 0777` でボリュームディレクトリを作る**
  （`kube-system/local-path-config` を実読）。root:root 0755 という前提そのものが k8s 側では成り立たない。
- **実測（2026-08-16・稼働中の k3s。PVC を当てた状態）**:

| ワークロード | 実行 uid | 書き込み先（モード） | 実データ | 状態 |
| --- | ---: | --- | ---: | --- |
| `loki` | **10001** | `/tmp/loki`（`drwxrwxrwx`） | **4.7M**（`chunks` / `compactor` / `index` / `index_cache`） | READY・RESTARTS 0 |
| `tempo` | **10001** | `/tmp/tempo`（`drwxrwxrwx`） | **5.0M**（`blocks` / `wal`） | READY・RESTARTS 0 |
| `grafana` | **472** | `/var/lib/grafana`（`drwxrwxrwx`） | **1.0M**（`grafana.db` / `csv` / `pdf` / `plugins`） | READY・RESTARTS 0 |
| `prometheus` | **65534** | `/prometheus`（`drwxrwxrwx`） | — | READY・RESTARTS 0 |

**「compose を鏡にする」という基準そのものが、この点ではプラットフォーム差を無視していた。**
compose の `user: "0:0"` は compose のままで正しく、[IADR-0079](./IADR-0079_infra-persistence-compose.md) §3 は**撤回しない** ——
誤っていたのは「docker の named volume の性質が PVC にもあるはずだ」という**推測**のほうである
（決定 4 が鏡の射程を限る理由でもある）。root 実行は本来減らすべきものであり、
不要と実測できた以上は付けない。

**機械が見る** —— `scripts/k8s-local-up.test.js` の
`#787: 可観測性 overlay は root 実行へ落とさない（local-path は 0777 で作る）` が、
4 種の patch に `runAsUser: 0` が**現れないこと**を検査する。

### 7. PVC を掴む Deployment は `strategy: Recreate` にする

`ReadWriteOnce` と `RollingUpdate` は両立しない。local-path は**単一ノードの hostPath** なので
**スケジューリングでは詰まらず、アプリのロックで詰まる**（新旧 Pod が同じディレクトリを同時に開く）。

- **Prometheus は `storage.tsdb.no-lockfile: false` を実測**（`/api/v1/status/flags`）。
  再起動後の `/prometheus/data` に **`lock` ファイルが実在した**。
- Postgres は `postmaster.pid`、Qdrant は RocksDB の LOCK、Grafana は SQLite。

実装は両オーバーレイの `kustomization.yaml` へ `labelSelector: "app in (...)"` の JSON6902 パッチとして入れる。
対象は **infra 側 = postgres / keycloak / qdrant**、**observability 側 = prometheus / loki / tempo / grafana** の
**計 7 件**である。

**[IADR-0082](./IADR-0082_local-k8s-infra-persistence.md) の既存 2 件（postgres / keycloak）にも `Recreate` が無かったので遡って付けた。**
新規だけ直して既存を残すのは**母集合の規則 7 の破れ**であり、同じ壊れ方を 2 か所に残すことになる。

**base（`emptyDir`）側は `RollingUpdate` のままでよい**（奪い合うボリュームが無い）。
オーバーレイだけに入れることで、既定経路はバイト等価のまま保たれる。

## 理由

- **既存の形（Deployment ＋ 別建て PVC）に揃える**ことで churn を最小にし、IADR-0082 の却下理由を尊重する。
- **ゲートの意味論を壊さない**（可観測性の PVC は可観測性ゲートの下にだけ現れる）。
- **config を単一情報源に保つ**（マウント先は config から導かれ、両側から突き合わされる）。
- **retention は「形」で塞ぐ**（設定値の善し悪しではなく、`size < PVC 容量` という不等式で壊れ方を消す）。
- **パリティは意味の一致で取る**（プラットフォーム固有の対処までは写さない。**推測ではなく稼働クラスタの実測に従う**）。
- **RWO と両立しない更新戦略を残さない**（`Recreate` は「詰まってから直す」のではなく、詰まる形を先に消す）。

## 結果

- 良い影響: `PERSIST=1`（＋ `OBSERVABILITY=1`）で dev の embeddings・メトリクス・ログ・トレース・
  Grafana 設定が Pod 再起動を跨いで残る。compose とのパリティ差（Prometheus / Grafana の未マウント、
  retention 無指定）が解消する。**4 種とも非 root のまま**であり、root 実行を 1 つも増やさない。
- 悪い影響・トレードオフ:
  - **PVC が 5 本増える**（qdrant-storage / prometheus-data / loki-data / tempo-data / grafana-data）。
    provisioner 不在クラスタでは Pending になりうるため、既定オフ（opt-in）を維持することが前提である。
  - **`Recreate` は入れ替え中にダウンタイムが出る**（旧 Pod を落としてから新 Pod を立てる）。
    単一レプリカの dev では実質差が無く、RWO で詰まる方が高くつく。
  - **`down` → `up` では PVC も消える**（IADR-0082 と同じ制約。namespace/クラスタごと消えるため）。
- フォローアップ:
  - **稼働クラスタでの受け入れは済んだ。** 本 ADR を書いた側の環境（PR #816）には
    `kubectl` / `helm` / `k3d` / `kustomize` が**いずれも無く**、クラスタも無かった（実測）。
    その時点では **(a)「Pod 再起動でデータが残る」と (b)「保持期間が実効している」を測っていなかった**
    （[IADR-0184](./IADR-0184_feedback-dispatch-checker-verbatim.md)）。**この事実は消さない。**
    **［2026-08-16 追記 / #787］** 統合したもう 1 本（PR #815）は**稼働中の k3s** を持つ環境で書かれており、
    そちらで (a)(b) とも実測できた —— PVC は **7 本すべて `Bound`**、`strategy` は **7 件すべて `Recreate`**、
    Qdrant のコレクションは **Qdrant 再起動後も 2 件残存**、Prometheus の `numSeries` は
    再起動前後で **8564 のまま**、非 root 書き込みは決定 6 の表のとおり。詳細と留保
    （**実測時の PVC 容量は #815 側の 5Gi で、統合後の manifest は #816 側の 2Gi を採る**）は仕様書 §7.7。
  - rabbitmq / redis / otel-collector は引き続き非永続（IADR-0082 の却下理由が成り立つ）。

## 関連

- Supersedes: なし（[IADR-0082](./IADR-0082_local-k8s-infra-persistence.md) の**却下した代替案の一部**（qdrant）を覆すが、決定本体は生きているため
  Supersede ではなく**追補**の関係にある。IADR-0082 側に `［2026-08-16 追記 / #787］` を入れて後継 ID を併記した）
- Superseded by: なし
