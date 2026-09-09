---
title: ローカル起動器に合成監視の opt-in 門（SYNTHETIC=1）を用意する
type: spec
status: done
related_ids: [NFR-02, NFR-21, ADR-0044, ADR-0071, ADR-0072, ADR-0076, ADR-0079, IADR-0066, IADR-0096, IADR-0103, IADR-0213, IADR-0258, IADR-0313, IADR-0369, IADR-0378, IADR-0404]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 合成監視を起動器の門にする（#1287）

## 起点

- issue: #1287（ラベル `blocked:env`。**本文が「AI 側で先行できるのは『既定の起動器へ入れる差分の用意』まで」と明記している**）
- 計画 ADR: `ADR-0076` 決定 3・4（標識と除外は同時に入れる／**除外できない構成では配備しない**）、
  `ADR-0079` 決定 1（常時トラフィック＝60 秒・LLM を呼ばない）・決定 2（費用は間隔で実質的に固定・
  **課金の承認は利用者**）・§フォローアップ 1〜5
- 実装 ADR: [[IADR-0378]]（標識と除外・overlay）／[[IADR-0096]]（ESO の secret 供給）／
  [[IADR-0103]]（`secretKeyRef` は Pod 起動時に 1 度だけ解決される）／[[IADR-0258]]（門は fail-closed で待つ）
- **本 PR で IADR は起こさない**（後述 §5。新しい方式を決めていない ——「既存の門と同じ形で 1 つ足した」）

## 1. 母集合（着手前に自分で引いた）

**誤りの側から引く**（規約の規則 1）。「合成監視は opt-in で起動器に無い」と書いている箇所が、
本 PR の後に**誤りになる**側である。検索語は `synthetic`（大文字小文字を無視）と `SyntheticMonitoring`
の 2 軸で、`scripts/` `deploy/` `docs/` を**拡張子で絞らず**全走査した。

```console
$ grep -rniIc --exclude-dir=node_modules -e 'synthetic' -e 'SyntheticMonitoring' scripts/ deploy/ docs/ | grep -v ':0$'
deploy/local/synthetic-monitor/README.md:14
deploy/local/synthetic-monitor/synthetic-monitor.yaml:12
deploy/local/synthetic-monitor/probe.js:11
docs/operations/operations.md:8
docs/observability/synthetic-traffic-exclusion.md:8
docs/observability/llm-usage-and-cost-metrics.md:6
deploy/local/synthetic-monitor/kustomization.yaml:5
scripts/k8s-local-up.test.js:4
deploy/keycloak/microservices-platform-realm.json:4
deploy/prometheus/alerts.yml:2
deploy/mail-relay/reset-gate.yaml:1
deploy/mail-relay/reset-gate.js:1
```

**12 ファイル。**（走査時点はこのファイルを書く前であり、本仕様書は `.ai-context/` にあって
走査範囲の外にあるため、規則 8 の自己参照ぶんの引き算は生じない。）

| ファイル | 追随 | 理由 |
| --- | --- | --- |
| `scripts/k8s-local-up.sh` | ✅ **本体**（走査 0 件＝これまで 1 行も無かった。それが本 issue である） | 門を足す |
| `scripts/k8s-local-up.test.js` | ✅ | 門の試験・トークン表・decontamination 一覧 |
| `deploy/local/synthetic-monitor/README.md` | ✅ | 「手で当てる」しか書いていない。起動器の口を足す |
| `deploy/local/synthetic-monitor/kustomization.yaml` | ✅ | 適用コマンドの注記に起動器の口を併記 |
| `deploy/local/synthetic-monitor/synthetic-monitor.yaml` | ✅ | 「既定の起動器には入れない」の注記へ門の存在を追記 |
| `deploy/local/README.md` | ✅（走査 0 件。**opt-in 一覧の表があるので追随先である** —— 語で引けない追随先は規則 9 の「記憶で挙げない」の例外にならないよう、`OBSERVABILITY=1` の並びから引き直した） | `SYNTHETIC=1` を一覧へ |
| `docs/operations/operations.md` | ✅ | 「既定の起動器には入っていない」が誤りになる。表と未決事項も |
| `deploy/local/vault/eso/bootstrap.sh` ／ `externalsecret-*.yaml` | ✅（走査 0 件。**ESO 委譲を作る以上、種と宣言が要る**。`wikijs-oidc` の並びから引いた） | 種の投入と ExternalSecret の新設 |
| `deploy/keycloak/microservices-platform-realm.json` | ❌ | client `synthetic-monitor` は宣言済み。**本 PR は realm を変えない** |
| `deploy/prometheus/alerts.yml` | ❌ | `RagLatencySeriesAbsent` の前提（「overlay が当たっているクラスタだけ意味を持つ」）は**変わらない**。門は当て方を増やしただけである |
| `docs/observability/synthetic-traffic-exclusion.md` | ❌ | 標識と除外の**仕様**であり、配備の口の話をしていない |
| `docs/observability/llm-usage-and-cost-metrics.md` | ❌ | 同上（費用計測側の指標定義） |
| `deploy/local/synthetic-monitor/probe.js` | ❌ | プローブの実装。門は呼び方を変えない |
| `deploy/mail-relay/reset-gate.{js,yaml}` | ❌ | 「probe.js と同じ作法である」という**言及**のみ |

**規則 10（是正で新たに誤りになる自分の記述）**: 門を足したことで
「合成監視は起動器に無い」と書いた箇所が誤りになる。上表の ✅ がそれである。**導出値は持っていない**
（ExternalSecret の常時本数 18 は変わらない —— 追加分は**有効ゲート側**であり、
`k8s-local-up.sh` の注記もそのように直した）。

## 2. 決めたこと

### 2.1 既定は **オフ**（`SYNTHETIC=1` で有効）

`ADR-0079` §フォローアップ 1 は「常駐プローブを**本番構成へ**投入する」と課している。
**本番構成は `deploy/helm/microservices-platform` であり、そこに合成監視は無い。**
「既定の起動器」はその本番像を指す語であって、ローカルの `k8s-local-up.sh` のことではない。

加えてローカルで既定 ON にすると、**捨てるつもりの dev クラスタが常に `/analysis/ask` 系を叩き続ける**
（LLM は呼ばないが、検索までは走るので Qdrant / Postgres への負荷と利用イベントが常時立つ）。
`ABACSEED` / `SEARCHSEED` / `LOCALEMBED` が既定オフである理由と同じ性質である。

🔴 **利用者が既定 ON を望んだときに 1 行で変えられる位置に置く**（issue の要件）。
門の条件は `${SYNTHETIC:-$SYNTHETIC_DEFAULT}` で読み、`SYNTHETIC_DEFAULT="0"` を門の直前に置いた。
**この 1 行を `"1"` にするだけでよい**（試験がその形を固定する）。

### 2.2 標識は **live の Deployment へ `kubectl set env`** で与える

`SyntheticMonitoring__Subjects__0=synthetic-monitor` を BFF / DashboardService / AiAnalysisService へ与える。
**空だと `SyntheticTraffic.IsSyntheticPrincipal` が常に false を返し（fail-closed）、除外が 1 件も効かない。**

helm の `--set` を採らなかった理由: chart の `services.<name>.extraEnv` は**リスト**であり、
helm は `--set` のリストを**マージではなく置換**する。`--set services.bff.extraEnv[N].name=...` を撃つと
BFF の `Services__*` がまとめて消える。`LOCALEMBED` が `--set` で足せるのは**スカラの真偽値**だからである。

代わりに**起動後の live オブジェクトを触る**。これは `ARGOCD=1` の門が `kubectl patch configmap` で
ArgoCD の CM を後追いするのと同型である。helm の再実行はこの env を戻すが、**門は毎回 up の後段で
当て直すので収束する。** 既知の限界は 1 つ、`ARGOCD=1` で同期させているクラスタでは ArgoCD が
chart の姿へ戻す —— これは README と `docs/operations/operations.md` に明記した（隠さない）。

### 2.3 順序と **fail-closed**

`realm 追随 → 標識の env → 除外 3 サービスの rollout → overlay の apply → プローブの rollout`。

🔴 **除外の 3 サービスが揃わなければ、プローブを配備せずに `up` ごと落とす。**
`ADR-0076` 決定 4 の「除外できない構成では配備しない」は、**警告して続行**では満たせない
（それは「配備した」と同じである）。落ちる形は `LOCALEDGE` の HelmChartConfig の門（[[IADR-0258]]）に
そろえた。**指標が汚れるのは静かで、汚れたことが表示に出ない** —— だから止める側に倒す。

### 2.4 「除外規則が入ったイメージ」条件は **ローカルでは構造的に満たされる**

`ADR-0079` §フォローアップ 1 は「イメージの再ビルド」を 🔴 の依存として挙げているが、
ローカルは `scripts/k8s-local-images.sh`（`[2/7]`）が**この作業ツリーのソースから**イメージを作る。
したがって `SYNTHETIC=1` で立てたクラスタのイメージは、必ず除外規則を含む。
🔴 **稼働クラスタ（レジストリのタグを引く）へこの根拠は移せない。** そこが利用者の手の要る部分である。

### 2.5 Secret は `ESO=1` なら ExternalSecret へ委譲

`bff-oidc` / `wikijs-oidc` と同じ形にした。

- 既定（ESO 未設定）: `apply_secret` で dev 置き値 `synthetic-monitor-dev-secret-change-me` から作る
  （`SYNTHETIC_MONITOR_CLIENT_SECRET` で上書き可）。**他の dev OIDC クライアントと完全に同じ扱い**である。
  README の「置き値をそのまま使わない」は**本番**の話であることを補足した。
- `ESO=1`: 手動 apply をスキップし、`externalsecret-synthetic-monitor-oidc.yaml`（`creationPolicy: Owner`）へ委譲。
  種は `bootstrap.sh` が**無条件に**入れ、ExternalSecret の apply は `SYNTHETIC=1` のときだけ行う
  （立てていないゲートの ExternalSecret を作ると、案内どおり打った運用者が NotFound を踏む。#1102 の形）。
- **`eso_wait` の対象に入れた。** プローブは `secretKeyRef` で読むので、同期前に Pod が立つと
  空の値を掴んだまま固定される（[[IADR-0103]]）。

### 2.6 `AllowLlmEgress` は **設定しない**

60 分側（LLM を呼ぶ）は `ADR-0079` 決定 1 が**別の配備単位**と定めており、しかも決定 2 のとおり
**実際に費用を発生させる操作の承認は利用者の判断**である。門はこの env に一切触れない
（試験が overlay と起動器の両方を走査して固定する）。

## 3. 変更点

| ファイル | 変更 |
| --- | --- |
| `scripts/k8s-local-up.sh` | `SYNTHETIC=1` の門（§2.1〜2.4）／ESO ブロックへ ExternalSecret の条件付き apply と `eso_wait`／`msp_es` の案内／冒頭の env 一覧 |
| `deploy/local/vault/eso/externalsecret-synthetic-monitor-oidc.yaml` | 新設 |
| `deploy/local/vault/eso/bootstrap.sh` | `secret/msp/synthetic-monitor-oidc` の種と案内 |
| `scripts/k8s-local-up.test.js` | 門の試験 10 件／`OPTIN_TOKENS` に 3 トークン／`GATES_ALL` に `SYNTHETIC`／decontamination に `SYNTHETIC`・`SYNTHETIC_ROLLOUT_TIMEOUT`／kubectl stub に `STUB_EXCLUSION_ROLLOUT_STALLS` |
| `deploy/local/synthetic-monitor/{README.md,kustomization.yaml,synthetic-monitor.yaml}` | 起動器の口・既定オフの理由・限界 |
| `deploy/local/README.md` | opt-in 一覧と overlay 索引 |
| `docs/operations/operations.md` | 「既定の起動器には入っていない」の更新・配備の口の行・未決事項の追記（trace ブロックへ `#1287` と本仕様書） |

## 4. 試験と受け入れ基準の写像

`node scripts/k8s-local-up.test.js`（記録スタブ・副作用ゼロ）。**164 → 174 件**（+10）。

| 試験 | 何を守るか |
| --- | --- |
| `SYNTHETIC 未設定: 合成監視が 1 バイトも現れない` | 既定オフ（fail-safe）。`OPTIN_TOKENS` の横断検査にも 3 トークンを登録した |
| `標識の許可集合が除外の 3 サービスすべてへ与えられる` | 1 つでも欠けるとその面だけが合成を実利用として数える |
| `プローブの Secret は dev 既定で作られる` | 401 を打ち続ける状態にしない |
| `overlay の apply と rollout 待ちが出る` | Pod が立たなくても緑で終わらない |
| `順序 —— realm 追随 → 標識の env → rollout → overlay` | `ADR-0076` 決定 4 の順序 |
| `ESO=1: ExternalSecret へ委譲し、同期を待ってから配備する` | 二重所有回避と [[IADR-0103]] |
| `ESO=1 単独: 合成監視の ExternalSecret は apply されない` | 立てていないゲートの NotFound を作らない |
| 🔴 `除外の 3 サービスが揃わなければ配備せず落ちる` | **陽性対照**。fail-closed が実際に効くこと |
| `既定 ON へ切り替える口が 1 行で読める位置に在る` | issue の要件 |
| `overlay は AllowLlmEgress を設定しない` | `ADR-0079` 決定 2（課金の承認は利用者） |

### 変異試験（実走。すべて赤になることを確認し、都度復元した）

| # | 変異 | 結果 |
| --- | --- | --- |
| M1 | 門の条件を反転（`= "1"` → `!= "1"`） | 🔴 赤 `既定オフなのに "deploy/local/synthetic-monitor" が現れた: kubectl apply -k deploy/local/synthetic-monitor` |
| M2 | overlay の apply を rollout 待ちの**前**へ動かす | 🔴 赤 `🔴 除外が揃う前にプローブを配備している（ADR-0076 決定 4 違反）` |
| M3 | fail-closed の `exit 1` を落として「警告して続行」へ | 🔴 赤 `除外が揃わないのに exit 0 で終わった（警告して続行は不可）` |
| M4 | 標識の投入先から `dashboard` を落とす | 🔴 赤 `dashboard-service へ SyntheticMonitoring__Subjects__0=synthetic-monitor が与えられていない` |

M2・M3・M4 は「門は在るが守っていない」形を直接作るものであり、**M1 だけでは検出力を測れない**
（既定オフの検査は通り道を 1 本しか見ない）。

## 5. IADR を置かない理由

新しい方式を決めていない。門の形（env ゲート・`apply_secret` と ESO 委譲の二段・`apply → wait` の 3 段・
fail-closed の落とし方）は**すべて既存の門から写した**ものであり、判断はこの仕様書に収まる範囲である
（[[IADR-0378]] が決めた標識と除外を、既存の起動器の作法で当てるだけである）。
`kubectl set env` を選んだ理由（§2.2）だけが新しい選択だが、これは
「helm の `--set` がリストを置換する」という**制約からの一意の帰結**であって、方式の選択ではない。

## 6. 🔴 やっていないこと（利用者の手が要る）

`ADR-0079` §フォローアップ 5 件のうち、**本 PR が動かしたのは項 1 のローカル側だけ**である。

| # | 残っていること | 誰の手が要るか |
| --- | --- | --- |
| 1 | **本番構成（helm チャート）への投入**と稼働クラスタでの適用 | **イメージの再ビルドと稼働クラスタ** |
| 1' | 受け入れ基準の実測（`absent` が鳴らないこと／プローブを止めると鳴ること＝陽性対照） | **稼働クラスタ** |
| 2 | **60 分間隔・`AllowLlmEgress` 有効**の SLO 評価用配備単位 | **課金の承認**（概算 月約 4,400 円） |
| 3 | `RagFirstTokenP95High` の評価窓の実測と環流（`rate()` の範囲＋`for`＋間隔 ≦ 8 時間） | 項 2 に従属（標本が要る） |
| 4 | 合成の LLM 費用の実測値の環流 | 項 2 に従属 |
| 5 | 実利用が立ち上がった時点での必要性・間隔の見直し | 実利用の立ち上がり |

**`blocked:env` は外れない。** 本 PR は issue が明示した「AI 側で先行できる範囲」の終端である。

## 7. 実行した検証

| コマンド | 結果 |
| --- | --- |
| `bash -n scripts/k8s-local-up.sh` / `bash -n deploy/local/vault/eso/bootstrap.sh` | OK |
| `node scripts/k8s-local-up.test.js` | ✓ 174 tests passed |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 後述（§8） |
| `node scripts/check-doc-links.js` / `check-trace-blocks.js` / `check-doc-updated.js --base origin/develop` | 緑 |
| `node scripts/check-cross-repo-refs.js` / `check-plan-id-qualification.js` | 緑 |
| `node scripts/check-realm-constraints.js` / `check-secret-injected-options.js` | 緑 |
| `node scripts/check-deploy-manifests.js` | **helm / kubectl / kubeconform が PATH に無く検証できない**（本作業環境の制約。CI では走る） |

## 8. 補足

- `check-deploy-manifests.js` はレンダリング＋スキーマ突合に helm / kubectl / kubeconform を要求する。
  本環境には無い（`DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS` は**立てていない** —— CI で立てないことを
  `scripts/scripts.repo.test.js` が突合しているため、ここでも立てない）。
  新設した ExternalSecret は既存 6 本と構造が同一であり、差分は `metadata.name` と `remoteRef.key` だけである。
