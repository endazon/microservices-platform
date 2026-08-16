---
title: 作業仕様書 — 経路B の Qdrant と可観測性スタックに PVC を足し、Prometheus の保持期間を明示する（#787）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0006
  - IADR-0066
  - IADR-0077
  - IADR-0079
  - IADR-0082
  - IADR-0116
  - IADR-0180
  - IADR-0210
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md"
  - "../../planning/projects/microservices-platform/02_requirements/"
related_specs:
  - "../adr/IADR-0210_local-k8s-observability-persistence.md"
  - "../adr/IADR-0082_local-k8s-infra-persistence.md"
  - "../adr/IADR-0079_infra-persistence-compose.md"
  - "../adr/IADR-0077_local-observability-vault-gitops-overlays.md"
  - "20260719_issue-324_infra-persistence-k8s.md"
  - "20260719_issue-282_infra-persistence-compose.md"
  - "20260720_issue-334_k8s-optin-gates-smoke-test.md"
---

# 作業仕様書: 経路B の Qdrant／可観測性スタックの永続化と Prometheus 保持期間（#787）

> **★ Grafana を含めて 4 種**である（issue の逐語は 3 種。判断と根拠は §4.7・母集合の全数は §2.4）。
>
> **★ 本書は 2 本の PR（#815 / #816）を 1 本へ統合した後の状態を記述する。** 統合の経緯は §12。
> 統合で変わった決定（root 実行を**やめた**／`strategy: Recreate` を**足した**）は §4.6 / §4.8 にある。

## 0. 前提（★ 本書の読者が最初に知るべきこと）

### 0.1 IADR 番号は `IADR-0210` を使う（`IADR-0209` の欠番は解消済み）

本 PR が立てる実装 ADR は **`IADR-0210`** である。`IADR-0209` は **#801 の PR（#814）が先に取る**前提で
採番し、それまでは本ブランチ単独で `node scripts/check-adr-numbering.js` が判定 2（欠番なし）で
fail していた（0208 → 0210 が飛ぶため）。

**［2026-08-16 追記 / #787］この前提は満たされた。** `IADR-0209`
（`docs/adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md`）は **PR #814 が着地して develop に
実在する**。本ブランチを rebase 済みの状態で再実行すると `check-adr-numbering.js` は **EXIT=0** である（§7.6）。
**改番は不要**になった。

### 0.2 「この環境では受け入れ基準 1・3 の実測ができない」——★ 統合で覆った

**本節は #816 を書いた環境についての記述であり、事実として残す**（[[IADR-0180]] 決定 2「覆った事実を消さない」）。

`kubectl` / `helm` / `k3d` / `kustomize` がいずれも**不在**で、稼働クラスタも無い（§7.0 に実測を残す）。
したがって次の 2 つは**#816 の環境では測れなかった**:

- 受け入れ基準 1 の後半「**Pod 再起動でデータが残る**」
- 受け入れ基準 3 の後半「**保持期間が実効していることを実測で示す**」

**［2026-08-16 追記 / #787］統合したもう 1 本（PR #815）は稼働中の k3s を持つ環境で書かれており、
そちらで両方とも実測できた。** 切替前後の実測は **§7.7**、受け入れ基準ごとの帰結は §8 に反映してある。
**留保は §7.7 の末尾に明記する**（実測時の PVC 容量は #815 側の値であり、統合後の manifest とは要求値が違う）。

**できないことを「できた」と書かない**（[[IADR-0184]]）。§8 は引き続き
**「静的に固定できた範囲」と「稼働クラスタで実測した範囲」を受け入れ基準ごとに分けて**書く。

## 1. 起点

起点は **#787**（Qdrant と可観測性 3 種に永続ボリュームが無く、再起動でデータが全消失する）。

経路B（ローカル k8s dev・`deploy/local/`）の永続化は、これまで 2 回に分けて入っている。

| 決定 | 射程 | 対象 |
| --- | --- | --- |
| [IADR-0079](../adr/IADR-0079_infra-persistence-compose.md) | **compose のみ**（§4 で経路B を明示的に対象外） | Keycloak / Loki / Tempo |
| [IADR-0082](../adr/IADR-0082_local-k8s-infra-persistence.md) | 経路B の**基盤インフラ**（opt-in `PERSIST=1`） | Keycloak / Postgres |

本 issue が指す 4 つは、**この 2 つのどちらの射程にも入っていない**。

- **Qdrant**: IADR-0082 が「却下した代替案」で **明文で却下している**
  （「qdrant / rabbitmq / redis / otel も PVC 化」→ 「embeddings は再生成可能な派生データ」）。
- **Prometheus / Loki / Tempo（経路B）**: IADR-0079 §4 が「対象は compose のみ・経路B は対象外」と書き、
  IADR-0082 は対象を Keycloak/Postgres に絞った。**どちらの射程からも外れている。**

したがって本件は**既存の決定を覆す／広げる**変更であり、新規 ADR（`IADR-0210`）と
IADR-0082 への追補が要る（§6）。

## 2. 母集合（`.claude/rules/traceability.md` 規則 1〜8 ＋ `traceability.repo.md` 規則 9・10）

### 2.1 規則 2・9 —— 「誤りの側の文字列」で全文書を走査する

是正対象は「**経路B の Qdrant / 可観測性 3 種は永続化しない、と宣言している記述**」である。
**正しい側（`observability-persistence` / `qdrant-storage` / `prometheus-data`〔k8s〕）で引いても
0 件**（まだ存在しないため）。誤りの側の語で引いた。走査は
`git grep -l <語> -- . ':!planning' ':!src' ':!CHANGELOG.md'`（submodule・生成物・自動生成 CHANGELOG を除外）。

| # | 走査語 | 生ヒット（ファイル数） | 母集合に入れた数 | 除外の理由 |
| ---: | --- | ---: | ---: | --- |
| 1 | `emptyDir 継続` | 3 | **2** | `docs/specs/20260719_issue-324_*.md` は**確定済みの仕様書**（`traceability.repo.md`「確定済みの `docs/specs/` は書き換えない」）。 |
| 2 | `emptyDir` | 14 | **2**（#1 と同じ 2 件） | ADR 本体（0066/0079/0082）は決定の記録＝§6 の追補で扱う（本文の遡及書き換えはしない）。`deploy/local/infra/*.yaml` は base で変更しない。`docs/adr/README.md` は索引＝§6 で 1 行追加。他は確定済み仕様書。 |
| 3 | `PERSIST` | 21 | **4** | `deploy/local/README.md` / `docs/operations/operations.md` / `deploy/local/observability/README.md` / `scripts/k8s-local-up.sh`（＋テストとオーバーレイは実装対象）。ほかは確定済み仕様書・別ゲートの ADR（0087/0099/0100/0133）・`ci.yml`（`PERSIST` は環境変数名ではなく別語のヒット）・`scripts/README.md`（ゲート一覧＝§5.6 で確認して不要と判定）。 |
| 4 | `qdrant` | 53 | **2** | 永続化の可否に言及しているのは `deploy/local/README.md` と `docs/operations/operations.md` の 2 件のみ。他 51 件は接続先・検索・ABAC・イメージ等の文脈で、永続化の記述を含まない（1 件ずつ用途を確認）。 |
| 5 | `retention` | 5 | **1** | `deploy/local/observability/tempo.yaml`（`block_retention: 1h`＝Tempo の圧縮設定。**触らない**。ただし §5.4 で Prometheus の保持期間と混同されないよう ADR に書く）。`deploy/tempo.yaml`（compose 側の同値）・`frontend-tests.yml`（artifact 保持日数・**他エージェントが編集中のため触らない**）・`docs/superpowers/`（保管された旧計画・対象外）・`operations.md` は #6 で拾う。 |
| 6 | `リテンション` | 4 | **1** | `deploy/local/observability/README.md`「本番相当のリテンションは **Tier 3**（対象外）」＝ 本 PR が Prometheus の保持期間を明示することと**衝突しうる唯一の記述**。他 3 件はデータ仕様書（文書・利用イベントの保持期間）と #779 の仕様書で無関係。 |
| 7 | `対象外` | 367 | **1** | 語が汎用すぎるため、#6 で特定した `deploy/local/observability/README.md` の「Tier 境界」節に絞る。IADR-0079 §4 の「対象外」は決定の記録で、§6 の新 ADR が射程を上書きする形で扱う（本文は書き換えない）。 |
| 8 | `grafana-data` | 3 | **0**（ただし §5.5 の判断材料） | `deploy/local/observability/grafana.yaml` のヒットは `grafana-datasources` / `grafana-dashboards` の**部分一致による偽陽性**（実測: k8s 側に `grafana-data` ボリュームは無い）。compose にのみ実在する。 |
| 9 | `prometheus-data` | 1 | **0**（同上） | compose のみ。k8s 側には存在しない＝**同じ型のパリティ差**が Grafana と Prometheus の両方にある。 |

**母集合（追随が要る文書）= 4 件**

1. `deploy/local/README.md` — 「qdrant / rabbitmq / redis / otel は emptyDir 継続」が**誤りになる**（qdrant が外れる）。永続化テーブルにも行追加が要る。
2. `docs/operations/operations.md` — 同じ文（「qdrant/rabbitmq/redis/otel は emptyDir 継続」）と「経路B の永続化」節。
3. `deploy/local/observability/README.md` — 「本番相当のリテンションは Tier 3（対象外）」が retention 明示と衝突しないかの確認 ＋ 永続化 overlay の案内。
4. `docs/adr/README.md` — 索引に `IADR-0210` を 1 行追加。

### 2.2 規則 10 —— 「この変更で新たに誤りになる自分の記述」を引き直す

是正**後**の語で引き直した結果、次を追加で直す:

- `deploy/local/README.md`「既知の制約」の「`PERSIST=1` で **Keycloak/Postgres を** PVC 永続化できる」
  —— Qdrant と可観測性 4 種が増えるため列挙が古くなる。**是正前の語（`emptyDir 継続`）では捕まらない。**
- `deploy/local/README.md` L65 のゲート早見表の `PERSIST=1` 行（説明が Keycloak/Postgres だけ）。
- `scripts/k8s-local-up.sh` の `PERSIST=1` echo 文（同上）。
- **導出値は走査ではなく計算し直す**: 本書は「PVC の本数」を持たない（増えるたびに腐るため）。
  README の表は**サービスごとの行**として持ち、総数は書かない。

### 2.3 母集合に**入れない**と判断したもの（理由つき）

- `docs/specs/` の確定済み仕様書（`20260719_issue-324_*` 他）: 規約上**書き換えない**。
- `docs/adr/IADR-0066` / `IADR-0079` / `IADR-0082` の**本文**: 決定の記録である。
  ただし **IADR-0082 は明文の却下を覆される**ため、`traceability.repo.md` §Superseded/Deprecated の書式に従い
  **日付つき追記ブロック ＋ 後継 ID の併記**を入れる（§6.2）。IADR-0079 は射程が compose のままで
  誤りにならない（新 ADR が経路B を引き受ける）ので触らない。
- `.github/workflows/frontend-tests.yml` / `scripts/scripts.repo.test.js`: **別エージェントが編集中**。触らない。
- `scripts/scripts.test.js`: キット配布物（分類 A）。変更しない。

### 2.4 もう 1 本の軸 —— **マニフェストの全数**で「永続化されていない Deployment」を引く

§2.1〜§2.3 は**文書**の走査（9 語のテキスト走査）である。これは「誤った記述」を漏らさないためのもので、
**「直す対象そのものを漏らさない」ことは保証しない**（規則 5「軸を 1 本で終わらせない」）。
統合したもう 1 本（#815）は**マニフェスト側から**同じ問いを引いており、その結果を畳み込む。
**両者は補完関係にあり、どちらも落とさない。**

**「永続化されていない Deployment」を全数で引いた。** `deploy/local/` 配下の Deployment は **12 件**、
`StatefulSet` は **0 件**。各ファイルの `volumes` / `volumeMounts` を機械的に判定した。

| 判定 | 件数 | 内訳 |
| --- | ---: | --- |
| **対象（本 PR）** | **5** | qdrant（emptyDir）／prometheus・loki・tempo・grafana（データ用 volume 無し） |
| 対応済み | 2 | postgres・keycloak（`IADR-0082`） |
| 対象外 | 5 | rabbitmq・redis（queue / cache は揮発前提）／otel-collector（stateless）／headlamp（stateless UI）／vault（`-dev` は in-memory backend が仕様） |

**★ この軸が無ければ Grafana を落としていた。** 当初 issue のタイトルは「Qdrant と可観測性 3 種」で
Grafana が入っておらず、**機械判定で 5 件目として出てきたので入れた**（判断と根拠は §4.7）。
**「5 件測って 4 件だけ直す」は母集合の規則 7 の破れ**であり、
**本リポで最も繰り返し起きている事故の型**である（[[IADR-0141]] 決定 1）。
issue の逐語を母集合として扱わないこと。

## 3. 対象範囲

- **対象**: `deploy/local/infra-persistence/`（Qdrant 追加）／新規 `deploy/local/observability-persistence/`／
  `deploy/local/observability/prometheus.yaml`（retention args）／`deploy/docker-compose.yml`（同 args のパリティ）／
  `scripts/k8s-local-up.sh`（`OBS_KUSTOMIZE` 分岐）／`scripts/k8s-local-up.test.js`（アサート追加）／
  §2.1 の母集合 4 件／`docs/adr/IADR-0210_*.md`（新規）／`IADR-0082`（追補）。
  **統合で加わった対象**: 両オーバーレイの `strategy: Recreate` patch（§4.8。
  **`IADR-0082` の既存 2 件へ遡及**するため `infra-persistence/kustomization.yaml` の射程が広がる）。
- **対象外**:
  - **rabbitmq / redis / otel-collector の PVC 化** —— IADR-0082 の却下理由（queue/cache は揮発前提・otel は stateless）が
    **いまも成り立つ**。本 issue も挙げていない。
  - **StatefulSet 化** —— IADR-0082 が明文で却下しており、本リポに StatefulSet は **0 件**（実測）。
    既存 PVC 4 例（postgres / keycloak / minio / wikijs）**すべて**が Deployment ＋ 別建て PVC である。
  - **本番像（Helm チャート）** —— 本 issue の 4 ワークロードは Helm チャートに**1 つも存在しない**（実測 §5.4）。
    射程は dev 経路 B に閉じる。
  - **`deploy/local/observability/*.yaml` の config 書き換え** —— IADR-0079 §3 が確立した作法
    （**config を書き換えず既存 storage パスへマウントする**）に従う。

## 4. 設計（確定）

### 4.1 全体方針: 4 つとも「Deployment のまま PVC を足す」

本リポに StatefulSet は 0 件で、既存 PVC 4 例すべてが Deployment ＋ 別建て PVC である。
IADR-0082 が StatefulSet 化を明文で却下しているため、**Deployment を維持**する。

### 4.2 Qdrant — 既存 overlay へ追加（新規ディレクトリを作らない）

`deploy/local/infra-persistence/pvcs.yaml` へ PVC `qdrant-storage`（`ReadWriteOnce` / `storageClassName: local-path` /
**2Gi** / `labels: {app: qdrant}`）を追加し、`kustomization.yaml` へ **postgres と完全に同型**の JSON6902 patch
（`replace /spec/template/spec/volumes/0`）を足す。base の `volumes[0]` は `name: storage` の `emptyDir` である（実測）。
**volumeMount は base に既にある**（`/qdrant/storage`）ので触らない。

### 4.3 可観測性 4 種 — 新規 overlay `deploy/local/observability-persistence/`

`resources: [../observability, pvcs.yaml]` ＋ JSON6902 patch。base に**データ用 volume が無い**ので
`add`（append）を使う（keycloak の patch と同型）。

| PVC 名 | ワークロード | マウント先 | 容量 | 根拠（マウント先の出どころ） |
| --- | --- | --- | ---: | --- |
| `prometheus-data` | prometheus | `/prometheus` | 5Gi | TSDB の既定パス。compose の `prometheus-data:/prometheus` と同一 |
| `loki-data` | loki | `/tmp/loki` | 2Gi | ConfigMap `loki-config` の `common.path_prefix: /tmp/loki`（実読） |
| `tempo-data` | tempo | `/tmp/tempo` | 2Gi | ConfigMap `tempo-config` の `storage.trace.local.path: /tmp/tempo/blocks` / `wal.path: /tmp/tempo/wal` の**親**（実読） |
| `grafana-data` | grafana | `/var/lib/grafana` | 1Gi | compose の `grafana-data:/var/lib/grafana`（§5.5） |

### 4.4 `scripts/k8s-local-up.sh` の分岐

`INFRA_KUSTOMIZE` と**完全に同型**の 2 つ目の変数を、`OBSERVABILITY=1` ブロックの中に置く。

```bash
OBS_KUSTOMIZE="deploy/local/observability"
if [ "${PERSIST:-}" = "1" ]; then OBS_KUSTOMIZE="deploy/local/observability-persistence"; fi
kubectl apply -k "$OBS_KUSTOMIZE"
```

**`PERSIST=1` かつ `OBSERVABILITY=1` のときだけ効く。** `PERSIST=1` 単独では可観測性スタック自体が立たないので
新 overlay は現れない（§5.7 のテスト 2 が固定する）。

### 4.5 Prometheus の保持期間

base（`deploy/local/observability/prometheus.yaml`）の args へ 2 つ足す。

```
- "--storage.tsdb.retention.time=7d"
- "--storage.tsdb.retention.size=4GB"
```

- **`size` も入れる理由**: `time` だけでは remote-write の流入量次第で PVC が満杯になり**書き込み不能**になる。
  `size < PVC 容量`（4GB = 4.0e9 B ＜ 5Gi = 5.37e9 B）にしておけば**構造的に溢れない**。
  これは「設定した」ではなく「**壊れない形にした**」に当たる。
- **7d を選ぶ理由**: dev ローカル用途であり、本番像（Helm チャート）には本 issue の 4 ワークロードが
  **1 つも存在しない**（＝射程は dev 経路B に閉じる）。
- **compose にも同じ 2 引数を入れる**（`deploy/docker-compose.yml` の `prometheus.command`）。
  現状は両方とも retention 無指定で**対称**であり、片方だけ入れると**新たなパリティ差**を作るため（受け入れ基準 5）。

### 4.6 書き込み権限 — **root 実行へ落とさない**（★ 統合で決定が覆った）

**#816 の当初設計は「compose を鏡にする」として `loki` / `tempo` へ
`securityContext: {runAsUser: 0, runAsGroup: 0}` を足すものだった。統合でこれを撤回した**
（[[IADR-0210]] 決定 6）。**撤回の根拠は稼働中の k3s での実測である。**

- compose の `user: "0:0"`（IADR-0079 §3）は、**docker の named volume が root:root 0755 で生成される**ことへの
  対処である。**k8s へは転用できない** —— local-path provisioner の setup は **`mkdir -m 0777`** で
  ボリュームディレクトリを作る（`kube-system/local-path-config` を実読）。
- **実測（2026-08-16・稼働中の k3s、PVC を当てた状態）**:

| ワークロード | 実行 uid | 書き込み先（モード） | 実データ | 状態 |
| --- | ---: | --- | ---: | --- |
| `loki` | **10001**（変わらず） | `/tmp/loki`（`drwxrwxrwx`） | **4.7M**（`chunks` / `compactor` / `index` / `index_cache`） | READY・RESTARTS 0 |
| `tempo` | **10001**（変わらず） | `/tmp/tempo`（`drwxrwxrwx`） | **5.0M**（`blocks` / `wal`） | READY・RESTARTS 0 |
| `grafana` | **472**（変わらず） | `/var/lib/grafana`（`drwxrwxrwx`） | **1.0M**（`grafana.db` / `csv` / `pdf` / `plugins`） | READY・RESTARTS 0 |
| `prometheus` | **65534** | `/prometheus`（`drwxrwxrwx`） | — | READY・RESTARTS 0 |

**4 件とも READY かつ RESTARTS 0**。非 root のまま書けている。

**基準そのものが誤っていた。** 「compose を鏡にする」は**プラットフォーム差を無視していた** ——
docker の named volume の性質が PVC にもあるはずだ、というのは**推測**であって実測ではなかった。
**compose 側の `user: "0:0"` は compose のまま正しく、IADR-0079 §3 は撤回しない。**
鏡にしてよいのは**両プラットフォームで同じ意味を持つもの**（マウント先・retention 引数）だけである
（[[IADR-0210]] 決定 4 が鏡の射程を限る）。

**検査も入れ替えた**（§5.7）:

| 統合前（#816） | 統合後 |
| --- | --- |
| `#787: root 実行にするのは compose が user: "0:0" を付けているサービスだけ` | **`#787: 可観測性 overlay は root 実行へ落とさない（local-path は 0777 で作る）`** |

なお **マウント先のパリティ検査**（`#787: データボリュームのマウント先が compose と k8s で一致する`）は
そのまま残る —— 鏡にする射程が狭まっただけで、鏡そのものは生きている。

### 4.7 判断: Grafana を**スコープに入れる**

issue は Grafana を挙げていないが、k8s 側の `grafana.yaml` は ConfigMap 3 本のみで `/var/lib/grafana` が未マウント、
compose は `grafana-data:/var/lib/grafana` を持つ。**Prometheus/Loki/Tempo とまったく同じ型のパリティ差**である。

**入れる**と判断した。根拠:

1. **同型・同じ overlay・追加コスト小**（PVC 1 本 ＋ patch 1 つ）。
2. 受け入れ基準 5 は「compose 側とのパリティ差が解消したか、**残るなら理由を書く**」である。
   Grafana を落とすと**同じ PR の中で「解消した」と「残した」が混在**し、次に読む人が
   「なぜ Grafana だけ？」を毎回引き直すことになる。
3. Grafana が失うのは**ダッシュボードの手動 import 結果・アラートの silences・ユーザー設定**であり、
   経路B には provisioning されないダッシュボード（AST overview 等）を UI から import する運用が実在する
   （`deploy/local/observability/README.md`）。**再起動のたびに消える**のは同じ実害である。

### 4.8 `strategy: Recreate` —— PVC を掴む Deployment の更新戦略（★ 統合で足した）

**#816 の設計にはこの決定も記述も無かった。** 統合で足した（[[IADR-0210]] 決定 7）。

`ReadWriteOnce` と `RollingUpdate` は両立しない。local-path は**単一ノードの hostPath** なので
**スケジューリングでは詰まらず、アプリのロックで詰まる**（同一ノードに新旧 Pod が同居でき、
同じディレクトリを同時に開こうとする）。

- **Prometheus は `storage.tsdb.no-lockfile: false` を実測**（`/api/v1/status/flags`）。
  再起動後の `/prometheus/data` に **`lock` ファイルが実在した**。
- Postgres は `postmaster.pid`、Qdrant は RocksDB の LOCK、Grafana は SQLite。

実装は**両オーバーレイの `kustomization.yaml`** へ `labelSelector: "app in (...)"` の JSON6902 パッチとして
入れる（対象を Deployment 名で列挙せず、ラベルで一括して当てる）。

| オーバーレイ | `labelSelector` の対象 |
| --- | --- |
| `deploy/local/infra-persistence` | **postgres / keycloak / qdrant** |
| `deploy/local/observability-persistence` | **prometheus / loki / tempo / grafana** |

- **`IADR-0082` の既存 2 件（postgres / keycloak）にも `Recreate` が無かったので遡って付けた。**
  **新規だけ直して既存を残すのは母集合の規則 7 の破れ**である（同じ壊れ方を 2 か所に残すことになる）。
- **base（`emptyDir`）側は `RollingUpdate` のままでよい** —— 奪い合うボリュームが無い。
  オーバーレイだけに入れることで、**既定経路のバイト等価（受け入れ基準 2）も保たれる**。
- 対象集合の一致は機械が見る（§5.7 の 5）。**1 件でも漏れるとその Deployment だけ `RollingUpdate` に残る**ため、
  検査は「`Recreate` が在ること」ではなく「**対象集合が PVC を掴む Deployment 全件と一致すること**」を見る。

## 5. 実装詳細

### 5.1 `deploy/local/infra-persistence/pvcs.yaml`

PVC `qdrant-storage` を追加（postgres / keycloak と同じ書式・ラベル・コメント様式）。

### 5.2 `deploy/local/infra-persistence/kustomization.yaml`

qdrant の JSON6902 `replace /spec/template/spec/volumes/0` patch を追加（postgres と同型）。
**加えて `labelSelector: "app in (postgres,keycloak,qdrant)"` の `add /spec/strategy` patch**（§4.8）。

### 5.3 `deploy/local/observability-persistence/{kustomization.yaml,pvcs.yaml}`（新規）

§4.3 の 4 PVC と 4 patch（`add` で volume と volumeMount を append）。
**`securityContext` は 1 つも入れない**（§4.6。統合前の設計から**削除**した）。
**`labelSelector: "app in (prometheus,loki,tempo,grafana)"` の `add /spec/strategy` patch** を足す（§4.8）。

### 5.4 retention（§4.5）

**実測（Helm チャートに 4 ワークロードが無いこと）**は §7.1 に残す。

### 5.5 Grafana（§4.7）

### 5.6 `scripts/README.md` は変更しない

`PERSIST` のヒットは**ゲート一覧の 1 行**で、対象サービスを列挙していない（実測）。誤りにならないため触らない。

### 5.7 `scripts/k8s-local-up.test.js` に足すアサート

既存の型（`OPTIN_TOKENS` ＋ ゲート別テスト ＋ マニフェスト静的検査）に乗せる。

1. `OPTIN_TOKENS` へ `deploy/local/observability-persistence` を追加。
   **★ 正直な注記**: 既存トークン `deploy/local/observability` は新トークンの**接頭辞**であり、
   既定経路検査は**すでに新 overlay の混入も落とす**。新トークンは**明示のため**に置くもので、
   これ単独を外しても穴は開かない（§7.3 の M2 が実測でそう出る）。
2. `PERSIST=1` 単独 → `observability-persistence` が**現れない**。
3. `PERSIST=1 OBSERVABILITY=1` → `apply -k deploy/local/observability-persistence` が現れ、
   **素の** `apply -k deploy/local/observability` が現れない。
   （**部分一致に注意**: `-persistence` 付きの行は素の文字列を含むため、
   行末／空白境界を見る正規表現で判定する。）
4. **マニフェスト静的検査**（既存の同種検査と同じ型・YAML パーサを持ち込まない）:
   - PVC の `storageClassName: local-path` / `accessModes: [ReadWriteOnce]` / 容量が意図どおり。
   - **各 volumeMount の mountPath が、対応する ConfigMap 内 config の storage パスと実際に一致する。**
     **Loki の `path_prefix`・Tempo の `storage.trace.local.path` / `wal.path` を実際にパースして突き合わせる**
     （値をハードコードして写すのではなく、**両側から読んで比較**する。ハードコードすると config を
     変えたときに静かに嘘になる）。
   - **compose を鏡にする判断そのものを検査する**: 同名データボリュームのマウント先が
     compose と k8s で一致すること（**両側から読む**）。
   - **root 実行へ落としていないこと**（★ 統合で入れ替えた。§4.6）: 可観測性 4 種の patch に
     `runAsUser: 0` が**現れない**こと。
     **入れ替え前**は「`user: "0:0"` を持つサービス（loki・tempo）**だけ**が k8s 側でも
     `runAsUser: 0` を持つ」という**集合一致**の検査だった。
   - **retention のパリティと安全性**: k8s base と compose の双方に同じ 2 引数があること、
     かつ `retention.size` を**バイトへ換算して** `prometheus-data` の PVC 容量**未満**であること
     （§4.5 の「構造的に溢れない」を機械が見る）。
5. **★ 統合で足した 4 件**（#815 が持っていた検査を畳み込んだもの。テスト名は逐語）:
   1. `#787: PVC を掴む Deployment は Recreate（RWO と RollingUpdate は両立しない）`
      —— 両オーバーレイについて `/spec/strategy` の `Recreate` patch が在ること、かつ
      **`labelSelector` の対象集合が PVC を掴む Deployment 全件と一致すること**（§4.8）。
   2. `#787: /tmp そのものはマウントしない（Loki / Tempo）`
      —— `/tmp` を覆うと Go の `os.TempDir()` が使う一時ファイルまで PVC に載り、`/tmp` の意味も壊れる。
      覆うのは `/tmp/<svc>` だけでよい（§7.4 の M3 は「config の値とずれる」を見る検査で、こちらは**別の型**）。
   3. `#787: base（既定経路）は書き換えていない —— PVC はオーバーレイにしか無い`
      —— `deploy/local/infra` と `deploy/local/observability` の YAML に `persistentVolumeClaim:` が
      1 件も無いこと。**base に PVC を持ち込むと provisioner 不在クラスタで既定経路が Pending になる**（fail-safe が壊れる）。
   4. `#787: 永続化オーバーレイの claimName がすべて同じ overlay の PVC を指す`
      —— 綴り違いは `kustomize build` では通り、**apply して初めて Pod が Pending で止まる**型の事故を先に潰す。

**件数**: 統合前 **68 件** → 統合後 **72 件**（入れ替え 1 件 ＋ 追加 4 件）。実行結果は §7.6。

## 6. ADR

### 6.1 新規 `docs/adr/IADR-0210_local-k8s-observability-persistence.md`

理由: IADR-0082 は「qdrant / rabbitmq / redis / otel も PVC 化」と「StatefulSet 化」を**明文で却下**しており、
qdrant の PVC 化はその却下を**覆す**。可観測性 4 種は IADR-0079 §4（k8s 対象外）と
IADR-0082（Keycloak/Postgres 限定）の**どちらの射程からも外れる**ため、新規 IADR が要る。

### 6.2 `IADR-0082` へ日付つき追補ブロック

`traceability.repo.md` §Superseded / Deprecated な ADR を引用するときの書式に従い、
**旧 ID を残して後継 ID を隣へ併記**し、`［2026-08-16 追記 / #787］` ブロックを入れ、`updated:` を前進させる。
**ID の付け替えはしない。**

### 6.3 `docs/adr/README.md` の索引

1 行追加。**索引セルは 200 字以内**（`scripts/adr-index-title-baseline.json` のラチェット）。

## 7. 検証・変異試験（実測）

> **★ §7.1〜§7.5 は #816 の環境で、その時点の実装に対して取った記録である。**
> 出力に見える **`✓ 68 tests passed`** は**その時点の件数**であり、統合後は **72 件**である（§5.7 / §7.6）。
> **記録した数を後から書き換えない**（測っていない出力を作らない）。統合後に再実行した結果は §7.6 に置く。
> **稼働クラスタでの実測は §7.7**（#815 の環境）、**#815 が行った変異試験は §7.8**。

### 7.0 「この環境では測れない」の実測（§0.2 の根拠。★ 統合で覆った）

**この記録は #816 の環境についてのものであり、消さない**（[[IADR-0180]] 決定 2）。

```
$ for t in kubectl helm k3d kustomize docker; do printf "%s: " "$t"; command -v "$t" >/dev/null 2>&1 && echo present || echo ABSENT; done
kubectl: ABSENT
helm: ABSENT
k3d: ABSENT
kustomize: ABSENT
docker: present
```

`kubectl` / `helm` / `k3d` / `kustomize` がいずれも無い。**`kustomize build` すら実行できない**ため、
マニフェストの整合は §5.7 の静的検査で担保する（#779 が同じ事情で採った型）。

**［2026-08-16 追記 / #787］** 統合したもう 1 本（PR #815）は**稼働中の k3s** を持つ環境で書かれており、
**そちらで受け入れ基準 1・3 の後半を実測できた**（§7.7）。
**「測れない」は環境の性質であって実装の性質ではない**、という当たり前のことが 2 本の並走で可視化された形である。
なお **§5.7 の静的検査は捨てない** —— CI には `kustomize build` / `kubeconform` を走らせるジョブが
1 件も無く（#779 の実測）、**稼働クラスタを持たない環境で回帰を止めるのは静的検査だけ**だからである。

### 7.1 設計の前提として測ったこと

```
$ git grep -n "kind: StatefulSet" -- . ':!planning' | cat
$ git grep -c "kind: StatefulSet" -- . ':!planning' | wc -l
0

$ git grep -n "kind: PersistentVolumeClaim" -- . ':!planning' | cat
deploy/argocd/appproject.yaml:33:      kind: PersistentVolumeClaim
deploy/helm/microservices-platform/templates/minio.yaml:116:kind: PersistentVolumeClaim
deploy/helm/microservices-platform/templates/wikijs.yaml:86:kind: PersistentVolumeClaim
deploy/local/infra-persistence/pvcs.yaml:5:kind: PersistentVolumeClaim
deploy/local/infra-persistence/pvcs.yaml:20:kind: PersistentVolumeClaim

$ git grep -in "prometheus\|loki\|tempo\|grafana\|qdrant" -- deploy/helm/ | cat
deploy/helm/microservices-platform/values.yaml:571:  # 任意の追加 env（config.js の GRAFANA_URL / JAEGER_URL / KIALI_URL / WIKI_BASE_URL 等）。既定は空で、
deploy/helm/microservices-platform/values.yaml:573:  # dev の Grafana(3000) を直挿しするが、k8s は可観測性/外部 UI を経路B の opt-in オーバーレイ（ADR-0006・
```

- **StatefulSet は 0 件** → §4.1 の「Deployment のまま」が既存の形に一致する。
- **PVC は 4 例**（minio / wikijs〔Helm〕・postgres / keycloak〔経路B〕）で、**すべて Deployment ＋ 別建て PVC**。
- **Helm チャートに 4 ワークロードが 1 つも無い**（ヒットは values.yaml のコメント 2 行のみ）
  → §4.5 の「射程は dev 経路B に閉じる」が成り立つ。

### 7.2 変異試験 M1 —— `OBS_KUSTOMIZE` 分岐を壊す

`scripts/k8s-local-up.sh` の `OBS_KUSTOMIZE="deploy/local/observability-persistence"` を
`"deploy/local/observability"` に書き換えた（＝`PERSIST=1` でも素の overlay を使う）。

```
$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=1
  ok  前提: 既定実行は exit 0（stub 下で副作用なく完走）
  ok  既定: k3d cluster create 引数がバイト等価
  ok  既定: opt-in 由来リソースが一切現れない
  ok  既定: minio-oidc app-secret が作られる
  ok  既定: llm-provider-credentials が手動 apply される（ESO 未設定）
  ok  HEADLAMP=1: apiserver OIDC フラグを付けず cluster create は既定とバイト等価
  ok  除去済み: HEADLAMP_OIDC_APISERVER=1 を明示しても no-op
  ok  除去済み: HEADLAMP_OIDC_ISSUER_URL / CLIENT_ID は引数へ影響しない
  ok  HEADLAMP=1 × 既存クラスタ reuse: 再作成 WARN も apiserver 引数も出ない
  ok  PERSIST=1: infra-persistence を apply
  ok  OBSERVABILITY=1: observability を apply・grafana-oidc secret を作成
  ok  PERSIST=1 単独: observability-persistence は現れない（スタック自体が立たない）
node:internal/assert/utils:77
    throw err;
    ^

AssertionError [ERR_ASSERTION]: observability-persistence が apply されない
    at /home/user/wt-persist/scripts/k8s-local-up.test.js:306:10
    at ok (/home/user/wt-persist/scripts/k8s-local-up.test.js:169:3)
    at Object.<anonymous> (/home/user/wt-persist/scripts/k8s-local-up.test.js:303:1)
  generatedMessage: false,
  code: 'ERR_ASSERTION',
  actual: false,
  expected: true,
  operator: '==',
  diff: 'simple'
}
```

**検出した。** 復元後:

```
$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=0
  ok  #787: Prometheus の保持期間が args で明示され、compose と同値である

✓ 68 tests passed
```

### 7.3 変異試験 M2 —— `OPTIN_TOKENS` から新トークンを外す

**★ 本節は「検出できなかった」ことを記録する節である。**

**M2-a**: `OPTIN_TOKENS` から `'deploy/local/observability-persistence'` の 1 行だけを外し、
スクリプトは正しいまま実行した。

```
$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=0

✓ 68 tests passed
```

**穴は開かなかった。** これは「トークンを外しても検査が緩まない」という意味であり、理由は
**既存トークン `'deploy/local/observability'` が新トークンの接頭辞**だからである。
既定経路検査は `includes` で見るため、上位トークンだけで新 overlay の混入も落ちる。

**M2-b**: トークンを外したまま、既定経路へ混入させる変異をスクリプトへ入れた
（`if [ "${OBSERVABILITY:-}" = "1" ]` → `if true`、かつ `OBS_KUSTOMIZE` の既定値を永続化版に）。

```
$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=1
  ok  前提: 既定実行は exit 0（stub 下で副作用なく完走）
  ok  既定: k3d cluster create 引数がバイト等価
node:internal/assert/utils:77
    throw err;
    ^

AssertionError [ERR_ASSERTION]: 既定オフなのに "deploy/local/observability" が現れた
    at /home/user/wt-persist/scripts/k8s-local-up.test.js:188:12
```

**M2-c**: トークンを戻し、変異はそのまま。

```
$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=1
AssertionError [ERR_ASSERTION]: 既定オフなのに "deploy/local/observability" が現れた
    at /home/user/wt-persist/scripts/k8s-local-up.test.js:189:12
```

**結論（正直に書く）**: 受け入れ基準 2（既定でバイト等価）は守られているが、
**それを守っているのは新トークンではなく既存の接頭辞トークンである**。
新トークンは**明示のための記述**であり、検出力を増やしてはいない。
テスト側のコメントにも同じことを書いた（`OPTIN_TOKENS` の該当行）。

### 7.4 変異試験 M3 —— `mountPath` を config の値からずらす

**M3-a**: `loki` の `mountPath` を `/tmp/loki` → `/loki`、`tempo` を `/tmp/tempo` → `/tmp` に変えた。

```
$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=1
AssertionError [ERR_ASSERTION]: loki の mountPath(/loki) が path_prefix(/tmp/loki) と違う
+ actual - expected

+ '/loki'
- '/tmp/loki'

    at /home/user/wt-persist/scripts/k8s-local-up.test.js:1251:10
```

**M3-b**: loki だけ戻し、tempo の変異（`/tmp` ＝ storage パスの祖先ではあるが**直接の親ではない**）を残した。

```
$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=1
AssertionError [ERR_ASSERTION]: tempo の mountPath(/tmp) が storage パスの直接の親でない: /tmp/tempo/blocks,/tmp/tempo/wal
    at /home/user/wt-persist/scripts/k8s-local-up.test.js:1273:10
```

**両方とも検出した。** 「配下にあるか」だけを見ると `/tmp` を通してしまうため、
**直接の親であること**まで見ている（過剰に広いマウントを弾く）。

### 7.5 変異の復元（バイト単位）

```
$ cmp <backup>/obskust.bak deploy/local/observability-persistence/kustomization.yaml && echo "kustomization.yaml: 一致"
kustomization.yaml: 一致
$ cmp <backup>/up.sh.bak scripts/k8s-local-up.sh && echo "k8s-local-up.sh: 一致"
k8s-local-up.sh: 一致
$ cmp <backup>/test.js.bak scripts/k8s-local-up.test.js && echo "k8s-local-up.test.js: 一致"
k8s-local-up.test.js: 一致
$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=0

✓ 68 tests passed
```

### 7.6 検査器の終了コード（`git add -A` 済み）

**#816 時点の記録**（統合後の再実行は §7.6.2）。

| 検査器 | EXIT | 備考 |
| --- | ---: | --- |
| `check-doc-links.js` | **0** | 654 件の Markdown に破損リンクなし（未 populate submodule 配下 2 件は対象外） |
| `check-doc-type-vocabulary.js` | **0** | |
| `check-doc-status-vocabulary.js` | **0** | |
| `check-cross-repo-refs.js` | **0** | |
| `check-plan-id-qualification.js` | **0** | |
| `check-adr-numbering.js` | **1** | **`[missing-number] IADR-0209 が欠番`。§0.1 のとおり想定内**（#801 が 0209 を取る前提） |
| `check-reading-budget.js` | **0** | `CLAUDE.md` / `.claude/rules/` は 1 バイトも増やしていない |
| `check-kit-sync.js` | **0** | キット配布物（`scripts/scripts.test.js` 等）は無改変 |
| `k8s-local-up.test.js` | **0** | 68 tests passed（うち本件の新規 9 件） |
| `scripts.test.js` | **1** | 失敗は上と同一の `IADR-0209 が欠番` **1 件のみ** |
| `REQUIRE_REPO_TESTS=1 scripts.test.js` | **1** | 同上 |

> **★ 検証コマンドはパイプで終端していない。** すべて `cmd > log 2>&1; echo "EXIT=$?"` の形で
> 終了コードを別途取った（`| tail` を挟むと終了コードが `tail` のものになり、クラッシュが exit 0 に見える）。

#### 7.6.1 「欠番以外の失敗が無い」ことの実測（一時プローブ・すぐ戻した）

`IADR-0210` を一時的に `IADR-0209` へ改番して同じ検査を走らせた。

```
$ node scripts/check-adr-numbering.js; echo "EXIT=$?"
EXIT=0
[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。

$ node scripts/scripts.test.js; echo "EXIT=$?"
EXIT=0
  ok  submodule の除外を .gitmodules から導出する

✓ 633 tests passed
```

**残る失敗は欠番だけである**ことが確認できた。プローブは元へ戻し（`IADR-0209` の残存 0 件を
`git grep -c` で確認済み）、`check-doc-links.js` / `k8s-local-up.test.js` の再実行も EXIT=0 である。

#### 7.6.2 統合後の再実行（★ 欠番は解消済み・テストは 72 件）

統合ブランチ（#816 を土台に #815 を畳み込んだ状態）で再実行した。

```
$ node scripts/check-adr-numbering.js; echo "EXIT=$?"
EXIT=0
[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。

$ node scripts/k8s-local-up.test.js; echo "EXIT=$?"
EXIT=0
  ok  #787: 可観測性 overlay は root 実行へ落とさない（local-path は 0777 で作る）
  ok  #787: Prometheus の保持期間が args で明示され、compose と同値である
  ok  #787: PVC を掴む Deployment は Recreate（RWO と RollingUpdate は両立しない）
  ok  #787: /tmp そのものはマウントしない（Loki / Tempo）
  ok  #787: base（既定経路）は書き換えていない —— PVC はオーバーレイにしか無い
  ok  #787: 永続化オーバーレイの claimName がすべて同じ overlay の PVC を指す

✓ 72 tests passed
```

- **`check-adr-numbering.js` は EXIT=0**。`IADR-0209` は **PR #814 が着地して develop に実在する**ため、
  §0.1 / §7.6 の「欠番で fail する」は**解消済み**である（改番は不要）。
- **`k8s-local-up.test.js` は 72 件**（§5.7 の入れ替え 1 件 ＋ 追加 4 件を含む）。

### 7.7 稼働クラスタでの実測（#815 の環境。★ §0.2 / §7.0 が「測れない」と書いた範囲）

**#816 の環境では測れなかった受け入れ基準 1・3 の後半を、#815 の環境（稼働中の k3s）で実測した。**

**切替前（永続化する前の状態）**

| 対象 | 実測 |
| --- | --- |
| Qdrant | `emptyDir`。**コレクション 0 件**（`/qdrant/storage/collections` が空） |
| Prometheus | **データ用 volume が無い**（volumes は ConfigMap のみ）。**実効保持は約 4.7 時間**（`runtimeinfo.startTime` と最古サンプルの差）。`storage.tsdb.retention.time` は **`0s`**（未指定） |
| Loki | 同上。`/tmp/loki` に 16.3M |
| Tempo | 同上。`/tmp/tempo` に 13.4M |
| Grafana | 同上。`/var/lib/grafana`（SQLite）が未マウント |

Pod は日次規模で再起動していた（**RESTARTS 9〜40**）。
**「再起動でデータが全消失する」は仮定ではなく、実際に起き続けていた**ということである。

**切替後**

| 観点 | 実測 |
| --- | --- |
| PVC | **7 本すべて `Bound`**（既存 2 ＋ 新規 5） |
| strategy | postgres / keycloak / qdrant / prometheus / loki / tempo / grafana の **7 件すべて `Recreate`** |
| Prometheus retention | `0s` → 実効値が args のとおりに反映（`/api/v1/status/flags` で確認） |
| **Qdrant の永続化** | 切替直後 0 件 → ingestion 再起動で 2 件再作成 → **Qdrant 再起動後も 2 件残存** |
| **Prometheus の永続化** | 再起動前後で `numSeries` が **8564 のまま**。`/prometheus/data` に `chunks_head` / `wal` / **`lock`** が残存 |
| 非 root 書き込み | §4.6 の表のとおり **4 件とも非 root・RESTARTS 0** |

**★ 留保（ごまかさずに書く）**

1. **実測時の PVC 要求容量は #815 側の値である。** qdrant / loki / tempo を **5Gi** で測った。
   統合後の manifest が採ったのは **#816 側の 2Gi** である（§4.2 / §4.3）。
   **local-path は hostPath なので要求容量を強制しない**（ノードのディスクを共有する）ため、
   **`Bound` / `Recreate` / 永続性という観測は容量に依存しない**。
   ただし**実測時と manifest の要求値が違う**ことは事実として明記する。
   なお**既存クラスタで容量を縮める再 apply は API サーバが拒否する**
   （`deploy/local/README.md`「永続化」節に実測が残っている）。5Gi で作った PVC が手元にあるなら、
   2Gi の manifest を当て直す前に PVC を作り直すこと。
2. **retention の値は #816 側の `7d` を採用した**（compose とのパリティを取るため、
   overlay patch ではなく **base の args** に置く方式も #816 側を採った）。
   **#815 は overlay patch で `15d` を当てていた**。したがって上表の「retention」欄は
   **「args で指定した値が `/api/v1/status/flags` に反映されること」を実測したもの**であり、
   **`15d` を実測したとは書かない**（統合後の `7d` での再実測はしていない）。

### 7.8 #815 が行った変異試験（10 通り・全件 RED）—— 統合後の実装に当たる 5 型

**★ 本節は「#815 で RED だった」という事実の記録である。統合後の実装に対する再実測はしていない。**
（§7.2〜§7.5 の M1 / M2 / M3 は #816 の環境で取ったもので、こちらとは別。両方残す。）

#815 は 10 通りの変異を入れて全件 RED を確認した。そのうち**統合後の実装にも当てはまる**のは次の 5 型で、
いずれも §5.7 の 5 で足した 4 テストが受け止める位置にある。

| 型 | 変異の内容 | 受け止めるテスト |
| --- | --- | --- |
| MP-a | `infra-persistence` の `Recreate` patch を落とす | `#787: PVC を掴む Deployment は Recreate（…）` |
| MP-b | `observability-persistence` の `Recreate` patch を落とす | 同上 |
| MP-c | grafana の PVC を落とす（4 → 3 件） | `#787: 全 PVC が local-path / ReadWriteOnce / 意図した容量を持つ` ＋ `#787: 永続化オーバーレイの claimName が…` |
| MP-d | `claimName` を PVC 名と食い違わせる | `#787: 永続化オーバーレイの claimName がすべて同じ overlay の PVC を指す` |
| MP-e | base に PVC を持ち込む／Loki の `mountPath` を `/tmp` にする | `#787: base（既定経路）は書き換えていない…` ／ `#787: /tmp そのものはマウントしない（Loki / Tempo）` |

**★ MP10 は 1 度目 GREEN を返した。** 挿入位置が `OBSERVABILITY=1` ブロックの**内側**で、
PERSIST 単独では実行されない —— **変異が退行を模していなかった**。位置を直したら RED になった。
#779 でも同じ型の誤りを踏んでおり（`echo` を壊してスタブログに出なかった）、**2 回目である。**

> **同型 2 回目の記録として残す。** 「テストが GREEN のままだった」を**検出力の不足**と読む前に、
> **変異が本当に退行を模しているか**を先に疑うこと。
> [[IADR-0141]] の「同型の事故が 2 回起きたら検査器・規約を足す」の母数に当たる 1 件である
> （**本 PR では足さない**。変異試験そのものを機械が見る形は、いまのところ検討していない）。

## 8. 受け入れ基準

issue の逐語 5 項目。**各項目に「静的に固定できた範囲」と「稼働クラスタで実測した範囲」を分けて書く**（§0.2）。
統合により、#816 単独では「未了」だった 1・3 の後半が **#815 の環境で実測済み**になった（§7.7）。

| # | 受け入れ基準（issue 逐語） | 静的に固定できた範囲 | 稼働クラスタでの実測（§7.7） |
| ---: | --- | --- | --- |
| 1 | `PERSIST=1` で Qdrant / Prometheus / Loki / Tempo が PVC を持ち、Pod 再起動でデータが残る | **前半は済**（PVC 定義・patch・ゲート分岐・マウント先の config 一致・`Recreate` の対象集合を `k8s-local-up.test.js` が固定。M1 / M3 で検出力を実測） | **後半も済**。PVC **7 本すべて `Bound`**／Qdrant のコレクションが**再起動後も 2 件残存**／Prometheus の `numSeries` が再起動前後で **8564 のまま** |
| 2 | 既定（`PERSIST` 未設定）でバイト等価 | **済**（`OPTIN_TOKENS` の既定経路検査が緑。M2-b/c で「混入したら落ちる」を実測）。ただし守っているのは接頭辞トークンである（§7.3）。**base に PVC を持ち込まないことも検査に足した**（§5.7 の 5-3） | なし |
| 3 | Prometheus の保持期間が args で明示され、実効していることを実測で示す | **「明示」と「壊れない形」は済**（args の存在・compose とのパリティ・`size(4.0e9 B) < PVC(5.37e9 B)` を数として検査） | **「実効している」も済**。切替前は `0s`（未指定・実効保持 約 4.7 時間）で、切替後は **args の値が `/api/v1/status/flags` に反映**された。**★ 実測時の値は #815 側の指定であり、`7d` での再実測はしていない**（§7.7 留保 2） |
| 4 | `IADR-0082` を改定するか追補する | **済**（`［2026-08-16 追記 / #787］` ブロック ＋ 後継 `IADR-0210` の併記、`updated:` 前進、frontmatter へ後継 ID 追加）。**`Recreate` は既存 2 件へ遡って当てた**（§4.8） | なし |
| 5 | compose 側（`IADR-0079`）とのパリティ差が解消したか、残るなら理由を書く | **済**（Prometheus / Grafana の未マウントと retention 無指定を解消。**マウント先のパリティを機械が両側から突き合わせる**）。**`user: "0:0"` は意図して写さない**＝§8.1 に理由つきで残すパリティ差として書く | **済**（非 root のまま 4 件とも READY・RESTARTS 0） |

### 8.1 解消しなかったパリティ差（理由つき）

- **`user: "0:0"`（root 実行）**: compose は `loki` / `tempo` に付け、**経路B は付けない**。
  **これは是正すべきパリティ差ではなく、プラットフォーム差に由来する意図的な差である** ——
  docker の named volume は root:root 0755 で作られ、local-path provisioner は `mkdir -m 0777` で作る。
  実測でも 4 件とも非 root のまま書けている（§4.6）。**compose 側は撤回しない。**
- **rabbitmq / redis**: compose は `rabbitmq-data` / `redis-data` を持つが、経路B は `emptyDir` のまま。
  IADR-0082 の却下理由（queue/cache は揮発前提）が成り立ち、本 issue も挙げていない。**意図的に残す。**
- **minio**: compose は `minio-data`、経路B は **Helm チャート側**の PVC（`templates/minio.yaml`）で永続化済み。
  経路が違うだけでパリティ差ではない。
- **Keycloak の永続化方式**: compose は共有 Postgres 外部 DB、経路B は H2-file-on-PVC。
  IADR-0082 決定 2 が**基盤差に由来する意図的な差**として記録済み。本 PR は触らない。

## 9. テスト方針

受け入れ基準を `scripts/k8s-local-up.test.js` へ写像する（§5.7）。
**CI には `kustomize build` / `kubeconform` を走らせるジョブが 1 件も無い**（#779 の実測）ため、
マニフェストの整合は同ファイルの既存の型（読んで正規表現で固定・外部依存ゼロ）で担保する。
**変異試験 M1 / M2 / M3 で検出力を実測する**（§7.2〜§7.5。#815 側の 10 通りは §7.8）。

**件数は 68 → 72 件**（統合で入れ替え 1 件 ＋ 追加 4 件）。統合後の全件は §7.6.2 の実行結果で **EXIT=0**。
本件が持ち込んだのは、**`#787:` を冠するテスト 12 件**（`grep -c "^ok('#787" scripts/k8s-local-up.test.js` = 12）
と、**ゲート意味論のテスト 2 件**（`PERSIST=1 単独: observability-persistence は現れない（スタック自体が立たない）` /
`PERSIST=1 + OBSERVABILITY=1: observability-persistence へ置換される（素の overlay は現れない）`）である。

| 統合で入れ替えた／足したテスト（逐語） | 対応する決定 |
| --- | --- |
| `#787: 可観測性 overlay は root 実行へ落とさない（local-path は 0777 で作る）`（**入れ替え**） | §4.6 / [[IADR-0210]] 決定 6 |
| `#787: PVC を掴む Deployment は Recreate（RWO と RollingUpdate は両立しない）` | §4.8 / [[IADR-0210]] 決定 7 |
| `#787: /tmp そのものはマウントしない（Loki / Tempo）` | §4.3 |
| `#787: base（既定経路）は書き換えていない —— PVC はオーバーレイにしか無い` | 受け入れ基準 2 |
| `#787: 永続化オーバーレイの claimName がすべて同じ overlay の PVC を指す` | §4.2 / §4.3 |

## 10. 計画書との差異

- 差異: なし（NFR 運用性・信頼性の具体化であり、計画 ADR-0006 の可観測性方針を変えない）。

## 11. 未決事項・起票者へ委ねること

- **#801 との採番順序（§0.1）は解決済み。** `IADR-0209` は develop に実在し、
  `check-adr-numbering.js` は EXIT=0（§7.6.2）。**改番は不要**である。
  （参考: 本作業中に一時プローブで改番したとき、`IADR-0210` の参照は **15 ファイル**にあった。
  もし将来改番が要るなら `.claude/rules/traceability.md`「採番衝突時の改番手順」に従い、
  ファイル名・自称番号・索引・本仕様書・コード内コメント・**PR タイトル**をすべて追随させること。）
- **稼働クラスタでの受け入れは §7.7 で済んだ。** 統合後の manifest（retention `7d`・
  qdrant / loki / tempo = 2Gi）での**再実測は行っていない**。手元にクラスタがある人が確かめるなら:
  1. `PERSIST=1 OBSERVABILITY=1 bash scripts/k8s-local-up.sh`
  2. `kubectl -n platform-infra get pvc` —— 有効にしたゲートの PVC がすべて `Bound`
     （**5Gi で作った PVC が残っていると 2Gi への縮小は拒否される**。§7.7 留保 1）
  3. Qdrant にコレクションを作り `kubectl -n platform-infra delete pod -l app=qdrant` → 復帰後も残ること
  4. `kubectl -n platform-infra port-forward svc/prometheus 9090:9090` →
     `curl -s localhost:9090/api/v1/status/runtimeinfo | grep -o '"storageRetention":"[^"]*"'` が `7d` を返すこと
     （**args を読み返すのではなく、Prometheus 自身が申告する実効値を見る**）
  5. `kubectl -n platform-infra get deploy -o custom-columns=NAME:.metadata.name,STRATEGY:.spec.strategy.type`
     —— PVC を掴む **7 件が `Recreate`** であること（§4.8）
  6. Loki / Tempo の Pod が `CrashLoopBackOff` にならないこと
     （**非 root のまま PVC へ書けていることの確認**。§4.6）
- **Grafana の `/var/lib/grafana` は SQLite を含む**。RWO 単一レプリカの dev 用途では問題にならないが、
  レプリカを増やす場合は別途の判断が要る（本 PR の射程外。経路B は `replicas: 1` 固定）。
  なお `Recreate`（§4.8）は**単一レプリカ前提の割り切り**でもある —— 入れ替え中は Pod が 0 になる。

## 12. 統合の経緯（同じ #787 に 2 本の PR が並走した）

**隠さずに書く。** 同じ issue #787 に対し、**PR #815 と PR #816 が 44 秒差で並走**した。
どちらも #787 の受け入れ基準を満たす独立の実装で、設計はおおむね収束していたが、
**root 実行の要否**・**`strategy: Recreate` の有無**・**retention の指定方式と値**・**PVC 容量**で食い違っていた。

**利用者の裁定で 1 本へ統合した。** 土台は **#816**、そこへ **#815** を畳み込んだ。
**[[IADR-0116]] 規約 1（1 issue = 1 PR）へ戻すための統合**である
（束ねてよいのは「裁定済みの同型な契約追加」だけで、本件はそれに当たらない）。

| 論点 | #815 | #816 | 統合後 | 決め手 |
| --- | --- | --- | --- | --- |
| root 実行 | 付けない | loki / tempo へ付ける | **付けない**（§4.6） | **稼働中の k3s での実測**（#815 側にしかクラスタが無かった） |
| `strategy` | `Recreate` | 記述なし | **`Recreate`**（§4.8） | RWO と RollingUpdate は両立しない。既存 2 件へも遡及 |
| retention | overlay patch で `15d` | base の args で `7d` ＋ `size` | **base の args で `7d` ＋ `size`** | **compose とのパリティ**（base に置かないと compose 側と対称にできない） |
| PVC 容量（qdrant / loki / tempo） | 5Gi | 2Gi | **2Gi** | dev 用途。**実測は 5Gi で取った**ので §7.7 留保 1 に明記 |
| 検査 | 10 通りの変異試験 | 68 件の静的検査 | **和を採る（72 件）** | どちらも落とさない（§5.7 / §7.8） |

**この節を残す理由**: 統合後の実装だけを見ると「なぜ root 実行を付けないのか」「なぜ実測値と manifest の
容量が違うのか」が読み取れない。**覆った決定と、その根拠になった実測を残す**（[[IADR-0180]] 決定 2）。
