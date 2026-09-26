---
title: IADR-0469 helm チャートの合成監視は既定オフの 1 つの鍵で、プローブと標識を同じ描画に揃え、除外できない構成は描画の段階で止める
type: impl-adr
status: Accepted
related_ids: [NFR-02, NFR-21, ADR-0044, ADR-0072, ADR-0076, ADR-0079, IADR-0096, IADR-0103, IADR-0240, IADR-0378]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md 決定 4（標識と除外は同時に入れる／除外できない構成では配備しない）
  - planning:projects/microservices-platform/07_adr/ADR-0079_synthetic-monitoring-interval-and-slo-window.md 決定 1・2・§フォローアップ 1
related_specs:
  - ../specs/20260926_issue-1287_helm-synthetic-monitor-optin.md
---

# IADR-0469: helm チャートの合成監視を既定オフで入れ、除外できない構成は描画で止める（#1287）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（#1287。利用者裁定 2026-09-26「案 A ——既定 false で器を用意し、有効化は利用者が行う」の実装）

## 起点・関連

- 関連する計画書 ID: NFR-02・NFR-21／**ADR-0076 決定 4**（標識と除外は同時に入れる。**除外できない構成では配備しない**）／
  **ADR-0079 決定 1**（常時トラフィック＝60 秒・LLM を呼ばない）・**決定 2**（LLM を呼ぶ合成は認めるが費用は間隔で固定。課金の承認は利用者）・§フォローアップ 1
- 関連する実装 ADR: [[IADR-0378]]（標識は外周＝JWT 主体・内周＝ヘッダ／overlay）・[[IADR-0096]]（ESO の secret 供給）・
  [[IADR-0103]]（`secretKeyRef` は Pod 起動時に 1 度だけ解決される）・[[IADR-0240]]（chart の描画とスキーマ突合）
- 関連する実装仕様書: `.ai-context/specs/20260926_issue-1287_helm-synthetic-monitor-optin.md`

## コンテキストと課題

合成監視の常駐プローブはローカルの overlay（`deploy/local/synthetic-monitor/`）と起動器の門（`SYNTHETIC=1`）にだけ在り、
本番像のチャートには 0 件だった。利用者は「チャートへ既定オフで入れ、有効化は再ビルドの後に自分で行う」と裁定した。

決めることは 5 つある。**(A) 無効時の描画をどう保つか**、**(B) 有効時に「標識と除外を同時に」をどう担保するか**、
**(C) Secret をどこで作るか**、**(D) プローブの中身（`probe.js`）を overlay とどう共有するか**、**(E) 何で試験するか**。

## 検討した選択肢

### (B) 「標識と除外を同時に」の担保

1. **1 つの鍵（`syntheticMonitor.enabled`）でプローブと 3 サービスの標識を同じ描画に出し、除外できない構成は `fail` で描画を止める**（採用）
2. 標識をサービスごとの `extraEnvAppend` に書かせる — 門が `set env` でしているのと同じ形だが、**プローブだけ有効で標識が欠ける構成が 1 行で書ける**。
   しかも欠けても描画は通り、表示も正常なので気付けない（ADR-0076 決定 4 がまさに禁じる形）
3. 3 サービスの集合を values の knob にする — 環境ごとに変えられるが、**1 つ外すだけで除外の欠けた面へ合成が流れる**。変える正当な理由が無い

### (D) `probe.js` の共有

1. **overlay を正本とし、チャートの `files/synthetic-monitor/probe.js` に写しを置き、バイト一致を試験で固定する**（採用）
2. 1 ファイルを両方から読む — helm の `.Files` はチャートの外を読めず、kustomize の `configMapGenerator` も既定の load restrictor で
   overlay の外を読めない。起動器の `kubectl apply -k` に `--load-restrictor` を足すのは門の挙動を変えるので採らない
3. 門をチャート経由に作り替える（ローカルも `--set syntheticMonitor.enabled=true` で当てる） — 単一情報源にはなるが、門の順序
   （rollout 待ち → fail-closed → 配備）と ESO 委譲・既定バイト等価の試験を作り直すことになり、本件の射程を超える

## 決定

### 決定 1: 既定オフ・無効時はバイト等価

`syntheticMonitor.enabled: false`。新テンプレートの注記はすべて Go テンプレートのコメントか `if` の内側に置き、**無効時は 1 バイトも描画しない**。
develop の描画（既定の values・`values-local.yaml` の 2 通り）とバイト一致することを確認し、以後は
「有効時の描画から合成の資源と標識 1 行を抜くと無効時と一致する」ことを試験が固定する。

### 決定 2: 有効時は同じ描画で揃え、除外できない構成は描画で止める

- `templates/synthetic-monitor.yaml` が `Deployment` / `ConfigMap`（overlay と同名）と、`networkPolicy.enabled` のときプローブ → Keycloak の
  egress 1 本を描く（`allow-intra-namespace` が egress を同 NS ＋ DNS に絞るため。BFF → Vault と同じ型）。
- `deployment.yaml` が同じ条件で `bff` / `dashboard` / `aianalysis` に `SyntheticMonitoring__Subjects__0=<clientId>` を描く。
  **集合は `_synthetic-monitor.tpl` の 1 か所**にあり、values の knob にしない。プローブの `SYNTHETIC_CLIENT_ID` と標識は同じ値から描く。
  集合は門と同じにする —— 外周（JWT 主体）で読むのは bff と dashboard であり、aianalysis は内周（ヘッダ）なので読まないが、
  門が与えているのでそろえる（無害な重複。一致を試験で固定する方を取る）。LlmGateway はヘッダだけを見るので env は要らない。
- **描画時の fail-closed**: 集合のサービスが 1 つでも無効なら `fail`。`aianalysis` の `extraEnv` / `extraEnvAppend` に
  `SyntheticMonitoring__AllowLlmEgress` が `false` 以外（`secretKeyRef` を含む）で立っていれば `fail`。
  - ［2026-09-26 追記 / #1287 監査］**鍵の名前は .NET の構成が同じ鍵として読む綴りをすべて同じものとして比べる。**
    構成の鍵は大文字小文字を区別せず、`:` と `__` を同じ区切りとし、`WebApplication.CreateBuilder` は `DOTNET_` /
    `ASPNETCORE_` 接頭辞の環境変数も接頭辞を外して読む。初版は完全一致で比べており、
    `SYNTHETICMONITORING__ALLOWLLMEGRESS` / `SyntheticMonitoring:AllowLlmEgress` / `DOTNET_SyntheticMonitoring__AllowLlmEgress`
    がどれも `true` のまま描画を通った（監査が rc=0 を実測）。比較の前に名前を小文字化し、接頭辞を外し、`:` を `__` にそろえる。
  - ［同］**値の判定は「リテラルの false か」の 1 条件だけにした**（前後の空白・大文字小文字は無視）。初版の
    `or .secretKeyRef (値が false でない)` の `.secretKeyRef` の枝は効いていなかった —— Secret 参照の項目は `value` を持たず、
    値の条件だけで既に止まる（監査の変異 M5 が生き残った理由）。Secret 参照・`valueFrom`・値の無い項目は、いずれも
    「リテラルの false ではない」として止まる。`value: false` と Secret 参照を両方書いた項目は、deployment.yaml が `value` を描いて
    Secret 参照を無視するので実際に false が入り、通す（試験が描画の中身まで確かめる）。
  60 秒のプローブが LLM を呼ぶと月 43,200 回（ADR-0079 実測 2 の概算で月約 264,000 円）になる。**60 分側は別の配備単位で課金の承認が先**であり、
  本チャートに `AllowLlmEgress` の knob は置かない。
- 🔴 **チャートはイメージの中身を確かめられない。** 「除外規則が入ったイメージであること」（ADR-0079 §フォローアップ 1）は
  有効化する人の手順で担保する（README・values の注記）。描画時に確かめる手段は無い —— **これは残るものである**（下記）。

### 決定 3: Secret はチャートの外で作る

チャートは `existingSecret: synthetic-monitor-oidc` を参照するだけにする。作るのは既存の
`deploy/local/vault/eso/externalsecret-synthetic-monitor-oidc.yaml`（target 名が同じ）である。`bff-oidc` / `*-service-token` と同じ流儀であり、
ExternalSecret をチャートへ複製すると同じ Secret の所有者が 2 つになる。

### 決定 4: `probe.js` は写しを置いてバイト一致で固定する（上の (D) 1）

ConfigMap は `probe.js` の sha256 を Pod の注釈に持つ（overlay では手で `rollout restart` が要った差し替えが、チャートでは自動で作り直しになる）。

### 決定 5: 試験は実際に `helm template` を叩き、helm が無ければ落ちる

- 新規 `scripts/helm-synthetic-monitor.test.js`。無効時の不在とバイト等価、有効時の揃い方、描画時の fail-closed（陽性・陰性対照つき）、
  門・overlay との一致（集合・主体名・env・イメージ・コマンド・`probe.js`・Secret 名と鍵）、変異 2 種（一時複製したチャートで集合から 1 つ落とす／
  描画から 1 サービスの標識を消す → 判定が赤）。**helm が無ければ fail-closed**（黙って飛ばすと「検査していない」と「問題が無い」が同じ出力になる）。
- CI は `static-checks-units`（`azure/setup-helm` 済み）に置く。`k8s-local-up.test.js` に足さないのは、そのジョブ（`static-checks`）が helm を入れておらず、
  同試験が helm を PATH のスタブへ差し替えるためである。
- `check-deploy-manifests.js` は chart の `ci/*.yaml` を走査で見つけ、各 values でも lint / template / kubeconform を回す。
  **既定オフの opt-in テンプレートは既定の描画に 1 度も現れず、スキーマ突合から漏れる**ためである。有効の定義は
  `ci/synthetic-monitor-values.yaml` の 1 か所に置き、上の試験もこれで描く。

## 理由

- **ADR-0076 決定 4 は「配備しない」と書いている。警告して続行する形は取れない。** 門（ローカル）は rollout を待って `exit 1` で落とす。
  チャートでは同じ判断を描画の段階へ前倒しできる —— 描画が失敗すれば `helm upgrade` も ArgoCD の同期も何も適用しない。
- **knob を増やさないことが安全側である。** 集合も `AllowLlmEgress` も、変える正当な理由が本件の射程に無い。変える理由が生じたときは
  テンプレートを変える PR になり、試験（門との一致・fail の陽性対照）がその差分を必ず赤で見せる。
- **写しは 2 か所に置くことになるが、どちらが正本かを決めてバイト一致で縛れば食い違いは CI で止まる。** 単一ファイル化のために
  起動器や kustomize の既定を変える方が、壊れ方が見えにくい。

## 結果

- 良い影響: 利用者が `syntheticMonitor.enabled=true` にするだけで、プローブ・標識・egress が 1 つの描画で揃う。ArgoCD で同期する構成でも
  標識が巻き戻されない（門の `set env` は chart の姿へ戻されていた）。除外の面が欠ける構成と LLM を呼ぶ構成は適用前に止まる。
- 悪い影響・トレードオフ: `probe.js` が 2 か所に在る（試験で縛る）。ローカルの門とチャートの有効化は同名の資源になるので併用できない（README に書いた）。
- 残るもの:
  - 🔴 **イメージが除外規則を含むかは描画で確かめられない。** 古いイメージのまま有効にできてしまう。確かめる主体は有効化する人である。
  - 稼働クラスタでの有効化と実測（issue #1287 の受け入れ基準 ①②）は利用者の手順（前提は #1378 の再ビルド）。
  - 60 分側（LLM を呼ぶ）は作っていない（ADR-0079 決定 2・課金の承認）。
  - `docs/operations/operations.md` の「本番構成（helm チャート）にはまだ入っていない」は、同じファイルを別の PR が触っているため本 PR では追随していない。
- フォローアップ: 上の operations.md の追随。60 分側は課金の承認の後に別 issue で。

## 関連

- Supersedes: なし
- Superseded by: なし
