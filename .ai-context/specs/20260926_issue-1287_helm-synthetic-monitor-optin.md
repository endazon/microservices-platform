---
title: helm チャートへ合成監視の常駐プローブを既定オフで入れ、有効化したときは標識と同じ描画で揃える
type: spec
status: done
related_ids: [NFR-02, NFR-21, ADR-0044, ADR-0072, ADR-0076, ADR-0079, IADR-0096, IADR-0103, IADR-0240, IADR-0378, IADR-0469]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md 決定 3・4（標識と除外は同時に入れる／除外できない構成では配備しない）
  - planning:projects/microservices-platform/07_adr/ADR-0079_synthetic-monitoring-interval-and-slo-window.md 決定 1・2・§フォローアップ 1
related_specs:
  - 20260909_issue-1287_synthetic-monitor-launcher-gate
issue: "#1287"
---

# 作業仕様書 — helm チャートへ合成監視を既定オフで入れる（#1287）

## 起点

- issue: #1287（`blocked:env`）。**利用者裁定（2026-09-26）で案 A を採った** —— チャート
  `deploy/helm/microservices-platform` に `syntheticMonitor.enabled`（**既定 false**）を足し、
  テンプレート・OIDC クライアントの Secret 参照・標識の許可集合を揃える。**有効化はイメージの再ビルド（#1378）の後に利用者が行う。**
- 計画 ADR（隣接クローンではなく `gh api` で `project-planning` の `main` を読んだ）:
  - `ADR-0076` 決定 4 —— 合成トラフィックは標識を持ち、LLM 費用と利用状況・検索傾向から除外する。
    **除外できない構成では配備しない。標識と除外は同時に入れる。**
  - `ADR-0079` 決定 1 —— 常時トラフィック＝**60 秒・LLM を呼ばない**／SLO 評価用＝60 分・呼ぶ（**別の配備単位**）。
    決定 2 —— 費用の上限は間隔で実質的に固定する。**課金の承認は利用者**（本 PR では 60 分側を作らない）。
    §フォローアップ 1 —— 常駐プローブを本番構成へ投入する。条件は「除外規則が入ったイメージであること」。
- 実装 ADR: [[IADR-0378]]（標識と除外・overlay）／[[IADR-0096]]（ESO の secret 供給）／
  [[IADR-0103]]（`secretKeyRef` は Pod 起動時に 1 度だけ解決される）／[[IADR-0240]]（chart の描画とスキーマ突合）。
  **本 PR の設計判断は [[IADR-0469]] に置く**（下記 §3）。

## 1. 既存の姿（着手時に読んだもの）

| 何 | 所在 | 要点 |
| --- | --- | --- |
| overlay | `deploy/local/synthetic-monitor/`（kustomization / Deployment / `probe.js` / README） | `node:22-alpine`・`PROBE_INTERVAL_SECONDS=60`・`PROBE_PATHS=/bff/analysis/ask,/bff/analysis/ask/stream`・Secret `synthetic-monitor-oidc` の `client-secret`。`AllowLlmEgress` は設定しない |
| ExternalSecret | `deploy/local/vault/eso/externalsecret-synthetic-monitor-oidc.yaml` | Vault `msp/synthetic-monitor-oidc` → Secret `synthetic-monitor-oidc` |
| ローカルの門 | `scripts/k8s-local-up.sh` の `SYNTHETIC=1` | `for d in bff dashboard aianalysis` へ `SyntheticMonitoring__Subjects__0=synthetic-monitor` → 同じ 3 つの rollout を待つ → 揃わなければ `exit 1` → overlay |
| チャート | `deploy/helm/microservices-platform` | 合成監視のテンプレートは 0 件（`grep -rln synthetic deploy/helm` → 0）。**ExternalSecret はチャートの外に置く流儀**（チャートは `existingSecret` を参照するだけ。`bff-oidc` / `*-service-token` と同じ） |

🔴 **呼び出し時の指示は「3 サービス＝BFF / DashboardService / LlmGateway」と書いていたが、門が標識を渡すのは
`bff` / `dashboard` / `aianalysis` である。** 両者は別のものを数えている —— **除外が起きる場所**は
BFF・DashboardService（外周＝JWT 主体）と LlmGateway（内周＝ヘッダ）であり、**許可集合（`Subjects`）を読むのは外周だけ**である。
LlmGateway と AiAnalysisService はヘッダ `X-Synthetic-Traffic` を見る（`RagOrchestrator.IsSyntheticRequest`）ので `Subjects` を要さない。
**チャートは門と同じ 3 つ（bff / dashboard / aianalysis）へ渡す** —— 門との一致を試験で固定するためであり、
aianalysis への 1 行は無害な重複である（門が既にそうしている）。

## 2. 母集合（着手前に自分で引いた。規約の規則 1・2・5・6・9）

**軸 1 —— 誤りの側の文言**（「チャートに合成監視が無い」と書く箇所が、本 PR の後に誤りになる側）:

```console
$ git grep -n -i -E "合成監視が無い|chart には無い|helm.{0,20}(投入|未着手)|本番構成.{0,20}(投入|未着手)|synthetic.{0,40}helm|helm.{0,40}synthetic|合成.{0,30}(chart|チャート)|(chart|チャート).{0,30}合成" -- . ':!src/ai-stock-trading'
.ai-context/specs/20260823_issue-788_spa-stage4-foundations.md:210:  （`platform/frontend` が `knowledge` の features を合成する）…
.ai-context/specs/20260909_issue-1287_synthetic-monitor-launcher-gate.md:74:`ADR-0079` §フォローアップ 1 は「常駐プローブを**本番構成へ**投入する」と課している。
deploy/local/synthetic-monitor/README.md:28:（`deploy/helm/microservices-platform`）を指しており、そちらには合成監視が無い。…
deploy/local/synthetic-monitor/README.md:37:（門は live の Deployment を触るが、chart には無い設定であるため）。…
deploy/local/synthetic-monitor/README.md:131:**本番構成（helm）への投入・稼働クラスタでの実測は利用者の手が要るため未着手である。**
scripts/chunk-budget-baseline.json:21:    "features を合成する）で、SC-10 が EChart を使うため …
scripts/k8s-local-up.sh:1107:#   「常駐プローブを**本番構成へ**投入する」と課しており、本番像は `deploy/helm/microservices-platform`
scripts/k8s-local-up.sh:1108:#   である。そこには合成監視が無い ——「既定の起動器」は本番像を指す語であって、ローカルの
```

**軸 2 —— 語 `synthetic-monitor` / `SyntheticMonitoring__` / `合成監視` を持つファイル**（`git grep -l -i`、submodule を除く）: 76 ファイル。
そのうち**配備の状態を述べる live 文書**を、語「helm / chart / 本番構成 / opt-in / 既定の起動器 / SYNTHETIC=1」で絞らず**ファイルごとに開いて**読んだ
（規則 4 —— 行フィルタで落とさない）: `docs/operations/operations.md`・`docs/observability/synthetic-traffic-exclusion.md`・
`docs/observability/llm-usage-and-cost-metrics.md`・`deploy/local/README.md`・`deploy/local/synthetic-monitor/README.md`。

| ファイル | 追随 | 理由 |
| --- | --- | --- |
| `deploy/local/synthetic-monitor/README.md`（28・37・131 行） | ✅ | 「チャートに無い」が誤りになる。チャートの口と有効化の手順・前提を足す |
| `scripts/k8s-local-up.sh`（1107〜1108 行・門の注記） | ✅ | 「そこには合成監視が無い」を「既定オフで在る」へ。**門の挙動は 1 行も変えない** |
| `docs/how-to/deployment.md` | ✅（新しい節） | チャートの配備手順の入口。有効化の前提と手順を足す（trace ブロックも） |
| `deploy/helm/microservices-platform/values.yaml` | ✅ 本体 | `syntheticMonitor` ブロック（既定 false）と注記 |
| `docs/operations/operations.md`（866〜867 行「本番構成（helm チャート）にはまだ入っていない」・1096〜1098 行） | ❌ **除外** | **利用者の指示で触らない**（別の PR が同じファイルを触る）。本 PR の後も「**有効化はされていない**」は真のままで、「**器が無い**」の読みだけが古くなる。**PR 本文にフォローアップとして書く** |
| `docs/observability/synthetic-traffic-exclusion.md` | ❌ | 配備の口を述べていない（除外の仕組みの説明）。開いて確認した |
| `docs/observability/llm-usage-and-cost-metrics.md` | ❌ | 同上（費用計測の説明。合成の配備状態に触れない） |
| `deploy/local/README.md`（80・87 行） | ❌ | ローカルの `SYNTHETIC=1` の説明であり、本 PR の後も正しい |
| `.ai-context/specs/20260909_…`・`.ai-context/adr/IADR-0378_…` | ❌ | 凍結記録（書き換えない） |
| `20260823_issue-788…` / `chunk-budget-baseline.json` | ❌ | 別義（「features を**合成**する」）。検索語の偶然一致 |

## 3. 設計（判断の根拠は [[IADR-0469]]）

1. **既定オフ・無効時はバイト等価**: `syntheticMonitor.enabled: false`。無効時に描画される文字列は 1 バイトも増えない
   （新テンプレートの注記は Go テンプレートのコメント `{{/* */}}` か `if` の内側に置く）。
2. **有効時は同じ描画で 3 点を揃える**（`ADR-0076` 決定 4）:
   - `Deployment/synthetic-monitor` ＋ `ConfigMap/synthetic-monitor-probe`（overlay と同名。`probe.js` の checksum 注釈で差し替え時に作り直す）
   - `bff` / `dashboard` / `aianalysis` の env に `SyntheticMonitoring__Subjects__0=<clientId>`
   - `networkPolicy.enabled` のとき、プローブ → Keycloak（`platform-infra` の `app: keycloak`・8080）の egress を 1 本だけ開ける
     （`allow-intra-namespace` が egress を同 NS ＋ DNS に絞るため。BFF → Vault と同じ型）
3. **3 サービスの集合は values の knob にしない**（テンプレートに直書き）。knob にすると 1 つ外すだけで
   「除外が欠けた面に合成が流れる」構成が 1 行で書けてしまう。
4. **描画時の fail-closed**（`helm template` の段階で止める）:
   - 有効なのに 3 サービスのどれかが `enabled: false` → `fail`（除外できない構成では配備しない）
   - 有効なのに `aianalysis` の `extraEnv` / `extraEnvAppend` が `SyntheticMonitoring__AllowLlmEgress` を `false` 以外で持つ → `fail`
     （60 秒のプローブが LLM を呼ぶと月 43,200 回＝概算 月約 264,000 円。**60 分側は別の配備単位で、課金の承認が先**）
5. **Secret はチャートの外**: `existingSecret: synthetic-monitor-oidc`（既存 ExternalSecret の target と同名）を参照するだけ。
   ExternalSecret をチャートへ複製しない（二重所有になる。既存の流儀どおり）。
6. **`probe.js` は写しを置き、バイト一致を試験で固定する**: helm の `.Files` はチャートの外を読めず、kustomize の
   `configMapGenerator` も既定では overlay の外を読めない（load restrictor）。どちらからも 1 ファイルを共有できないため、
   **overlay 側を正本**とし、チャート `files/synthetic-monitor/probe.js` を写しとする。
7. **overlay と同時に当てない**: 同名の資源になるので、ローカルの `SYNTHETIC=1` とチャートの有効化は併用しない（README に書く）。

## 4. 受け入れ基準

- [x] 既定（無効）: `helm template` の出力に `synthetic` が 0 回（大文字小文字無視）。3 サービスの env は有効時から標識 1 行を除いたものと一致し、
      **develop（`0a945ba3`）の描画とバイト等価**（既定値・`values-local.yaml` の 2 通り）
- [x] 有効: プローブの Deployment と ConfigMap、3 サービスの標識、Keycloak への egress が**同じ描画**に出る。`AllowLlmEgress` はどこにも出ない。ExternalSecret・Secret 本体は出ない
- [x] 有効 ＋ 3 サービスのどれかが無効 → `helm template` が失敗する
- [x] 有効 ＋ `aianalysis` に `AllowLlmEgress=true` → `helm template` が失敗する
- [x] 門との一致: 集合（bff / dashboard / aianalysis）・主体名（`synthetic-monitor`）・プローブの env（間隔・経路・Keycloak・BFF・Secret 名と鍵）・イメージ・`probe.js` のバイト列
- [x] 変異: 一時複製したチャートでテンプレートの集合から 1 つ落とすと、試験の判定が赤になる
- [x] `helm lint` と、有効時の描画も kubeconform に掛かる（`ci/` の values を `check-deploy-manifests.js` が拾う）

## 5. テスト方針

- 新規 `scripts/helm-synthetic-monitor.test.js`（**実際に `helm template` を叩く**）。`helm` が無ければ fail-closed。
  CI は `static-checks-units` ジョブ（`azure/setup-helm` 済み）に 1 ステップ足す。
  `k8s-local-up.test.js` に置かない理由: そのジョブ（`static-checks`）は helm を入れておらず、試験は helm を PATH のスタブへ差し替える。
- `check-deploy-manifests.js` は chart ごとに `ci/*.yaml` を見つけたら、それぞれの values でも lint / template / kubeconform を回す
  （自己試験に発見の 1 件を足す）。有効時の描画がスキーマに適合することをここで持つ。

## 計画書との差異

- 差異: なし。**60 分側（LLM を呼ぶ）は作らない**（`ADR-0079` 決定 2・課金の承認は利用者）。

## 未決事項

- **稼働クラスタでの有効化と実測（issue の受け入れ基準 ①②）は利用者の手順**。前提はイメージの再ビルド（#1378）。
- `docs/operations/operations.md` の「本番構成（helm チャート）にはまだ入っていない」の追随は別 PR の着地後（本 PR の指示で除外）。
- `check-adr-numbering` は IADR-0466〜0468（未マージの PR が確保）が着地するまで欠番で赤になる。
