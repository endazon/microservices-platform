---
title: 近接 MTA のキュー長・滞留・後送失敗を otel-collector 経由で SC-10 の導線へ載せる（#1245 PR-B）
type: spec
status: done
related_ids: [SC-10, SC-15, FR-05, FR-22, NFR-09, NFR-21, ADR-0006, ADR-0026, ADR-0045, ADR-0078, IADR-0130, IADR-0164, IADR-0165, IADR-0168, IADR-0304, IADR-0344, IADR-0347, IADR-0370, IADR-0383, IADR-0404, IADR-0421]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability.md
---

# 仕様書: 近接 MTA のキュー長・滞留・後送失敗を otel-collector 経由で SC-10 の導線へ載せる（#1245 PR-B）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（認証・アカウント管理）／FR-22（通知。同じ送出基盤を将来使う）
- 画面（SC）: **SC-10**（運用ダッシュボード。**画面そのものは変えない** —— 後述 D-6）／SC-15（パスワードリセット）
- 非機能（NFR）: NFR-09 ／ NFR-21（障害検出 5 分以内）
- 関連 ADR: **ADR-0078 決定 3**（上流停止の観測点を近接 MTA のキューへ移す）／
  ADR-0045 決定 8（同決定が部分改定した旧観測点）／ADR-0006（可観測性）／ADR-0026
- 実装 ADR: **IADR-0421**（本 PR の判断。IADR-0404 フォローアップ (1) を着地させる）／
  IADR-0404（PR-A・PR-C）／IADR-0344 ／ IADR-0347 ／ IADR-0165 ／ IADR-0168 ／ IADR-0304 ／ IADR-0370

🔴 **引用する計画 ADR の題目を実ファイル名で確かめた**（レンジ検査は題目の正しさを見ない）:

```console
$ ls /home/user/project-planning/projects/microservices-platform/07_adr/ | grep -i 0078
ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
```

**ADR-0078 の全文を読んだ**（決定 1〜5・§統制と現在の実現手段・§残るもの・§フォローアップ 1〜6）。
レンジ（`ADR-0001..0088`）は `.claude/rules/traceability.repo.md` が正本であり、0006 / 0026 / 0045 / 0078 とも範囲内である。

## 前提（`git log` を出典に引く前の確認）

```console
$ git rev-parse --is-shallow-repository
false
```

**浅いクローンではない。** ただし本仕様書は `git log` を出典に引いていない（作業ツリーを読んだ）。

🔴 **本 worktree は `Initial commit`（LICENSE のみ）に置かれていた。** `develop`（`7c9d184`）へ
付け替えてから作業した（`git checkout -B <worktree-branch> develop`）。基点はこのコミットである。

## 何を作るか（1 行）

**近接 MTA（mail-relay）のキュー長・滞留時間をサイドカーが Prometheus 形式で出し、
otel-collector の `prometheus` receiver が拾って既存経路（remote write → Prometheus / Grafana）へ流す。
あわせてアラート 3 件を 3 系統へ同内容で置き、しきい値が実測待ちの暫定値であることを明記する。**

## 母集合（自分で引き直した。規則 1〜10）

🔴 **設計コメントの数えを転記していない。** 以下はすべて本ブランチ（基点 `7c9d184`）で実行した走査である。
**誤りの側の文字列で引き**（規則 9）、**是正後に新たに誤りになる自分の記述を引き直し**（規則 10）、
**陽性対照を対で**置いた。`src/ai-stock-trading/`（submodule）・`.git/`・`node_modules/` は全走査から除く。

### S1: 「観測の配線はまだ無い」を前提にした記述（＝本 PR が着地すると偽になる側）

```console
$ grep -rln "その配線はまだ無い\|配線（収集とダッシュボードの導線）\|#1245 PR-B\|観測点が移った\|観測点の移動" \
    --include=*.md --include=*.js --include=*.yaml --include=*.yml --include=*.sh --include=*.json . | grep -v node_modules
./.ai-context/adr/IADR-0404_nearby-mta-relay-and-realm-ownership.md
./.ai-context/specs/20260906_issue-1245_nearby-mta-relay.md
./deploy/mail-relay/mail-relay.yaml
./docs/operations/keycloak-smtp-relay-setup-runbook.md
./docs/screens/SC-15_password-reset.md
陽性対照（"存在秘匿" を含むファイル数・パスで引く）: 398
```

| ファイル | 追随 | 理由 |
| --- | --- | --- |
| `docs/screens/SC-15_password-reset.md` | **する** | 「見るべきはキューの長さ・滞留時間・後送の失敗率であり、**その配線はまだ無い**」が偽になる |
| `docs/operations/keycloak-smtp-relay-setup-runbook.md` | **する** | §限界 の「配線は本手順にも本リポジトリの現状にも無い」が偽になる。あわせて**観測の見方**を足す（本 PR の射程） |
| `deploy/mail-relay/mail-relay.yaml` | **する** | 「観測（#1245 PR-B）が exporter を同居させるときに共有の emptyDir を張る」を**実行する**側になる |
| `.ai-context/adr/IADR-0404_*.md` | **しない** | 🔴 **凍結記録である。** フォローアップ (1) は「入れる」という**予定の記述**であり、着地しても誤りにならない。着地の記録は **IADR-0421** が持つ（IADR-0404 §フォローアップ が本 PR を名指ししているので参照は切れない） |
| `.ai-context/specs/20260906_issue-1245_nearby-mta-relay.md` | **しない** | 凍結記録（PR-A の作業仕様書） |

### S2: ADR-0045 決定 8 の観測点（「運用ダッシュボードで観測できる」）を書いている箇所

```console
$ grep -rn "運用ダッシュボードで観測\|死活と失敗率" --include=*.md . | grep -v node_modules
docs/functional/FR-22_user-notifications.md:164, 175
docs/tests/FR-22_user-notifications.md:47
docs/tests/SC-15_password-reset.md:62          （T-13）
docs/screens/SC-15_password-reset.md:111, 167
```

| ファイル | 追随 | 理由 |
| --- | --- | --- |
| `docs/tests/SC-15_password-reset.md:62`（T-13） | **する** | 🔴 **観測点が移ったのに「監査ログへ記録され運用ダッシュボードで観測できる」のままである。** 近接 MTA を挟んだ以後、**上流停止では監査ログに何も出ない** |
| `docs/screens/SC-15_password-reset.md:111`（図）・`:167`（対応表） | **しない** | どちらも主語が **Keycloak → 近接 MTA の投函失敗**である。ADR-0078 決定 3 は「**Keycloak → 近接 MTA の投函失敗も引き続き監査ログへ残す**」と明記しており、**偽にならない**（是正すると計画に反する） |
| `docs/functional/FR-22_*` ／ `docs/tests/FR-22_*` | **しない** | 主語が**通知サービスの送信上限**（`deferred` / `dropped`）であり、近接 MTA のキューではない。語が同じだけである |

🔴 **S2 は「同じ語だが主語が違うもの」を 3 件含んでいた。** 語で引いて主語で除外した（規則 9 が求める「挙げてから除外する」）。

### S3: `mail-relay` / `mailpit` / `postfix` を含む全ファイル（拡張子で絞らない・規則 3）

```console
$ grep -rlEi 'mail-relay|mailpit|postfix' . | grep -v node_modules | grep -v '^\./\.git/' \
    | grep -v '^\./src/ai-stock-trading/' | grep -v '^\./\.ai-context/specs/' | grep -v '^\./CHANGELOG.md'
（39 件。うち本 PR で触るのは下記 9 件）
```

| ファイル | 追随 | 理由 |
| --- | --- | --- |
| `deploy/mail-relay/mail-relay.yaml` | **する** | サイドカー・spool の emptyDir・Service の :9154・NetworkPolicy |
| `deploy/mail-relay/mail-queue-exporter.js` | **新設** | exporter 本体 |
| `deploy/local/infra/otel-collector.yaml` ／ `deploy/local/observability/otel-collector-forward.yaml` | **する** | `prometheus/mail-relay` receiver（**両方**。片方だけだと #1090 の形） |
| `deploy/otel-collector-config.yaml` | **する（コメントのみ）** | 🔴 **receiver を置かない理由**を書く。compose に近接 MTA が居ない（下記 S5） |
| `deploy/prometheus/alerts.yml` ／ `deploy/local/observability/prometheus.yaml` ／ `deploy/grafana/provisioning/alerting/slo-alerts.yaml` ／ `deploy/local/observability/grafana.yaml` | **する** | アラート 3 件 ×3 系統（＋ Grafana は k8s inline が同内容） |
| `deploy/grafana/provisioning/dashboards/microservices-platform-overview.json` ＋ k8s inline | **する** | パネル 2 枚 |
| `scripts/k8s-local-up.sh` | **する** | exporter の ConfigMap（`--from-file`） |
| `scripts/README.md` | **する** | 自己試験の実行行と CI の門の説明 |
| `deploy/local/README.md` | **する** | 経路図に観測を足す |
| `scripts/check-stack-ready.js` | **しない** | G8 が見ているのは**捕捉用 MTA**である。🔴 **exporter に probe は置かない**（D-8。観測がメールを止めてはならない）ので G1 も exporter の応答は見ない —— **exporter が答えないことは系列の不在のアラートが拾う**。**門を増やさない**（「同型の事故が 2 回」の条件を満たさない。本件は 1 回目でもない） |
| `deploy/keycloak/*-realm.json` ／ `reconcile-realm.js` ／ `check-realm-constraints.js` ／ `bootstrap.sh` ／ `externalsecret-keycloak-smtp.yaml` ／ `reset-gate.*` | **しない** | 送出経路の宣言と門であり、観測の追加で真偽が変わらない |
| `.ai-context/adr/IADR-0261 / 0332 / 0344 / 0369 / 0404` | **しない** | 凍結記録 |

### S4: 導出値（走査ではなく**計算し直す**。規則 10）

**アラートのルール件数**は 3 箇所に書かれた導出値である。

```console
$ node scripts/check-grafana-alerting.js        # 変更前
… Prometheus 12 件 / Grafana 12 件 …
```

内訳を**数え直した**: `platform-availability` 2 ／ `platform-slo` 4 ／
`platform-slo-evaluation-target` **4 → 5** ／ `knowledge-health-producers` 2 ／
**`mail-relay-queue` 2（新設）** ＝ **12 → 15**。
追随先は `deploy/grafana/provisioning/alerting/slo-alerts.yaml` の冒頭注記・その k8s inline・
`scripts/check-grafana-alerting.js` の冒頭注記の **3 箇所**（`grep -rn '12 件'` で引いた全数）。

### S5: compose スタックに近接 MTA が居ないことの確認（決定 4 の根拠。陽性対照つき）

```console
$ grep -n 'mail\|postfix\|keycloak:' deploy/docker-compose.yml
（mail / postfix のヒット 0 件。陽性対照 keycloak: 6 行）
```

**compose には近接 MTA も捕捉用 MTA も居ない。** 運用 Runbook も compose 経路の SMTP 手順を退役させている。
⇒ compose の collector 設定へ receiver を置くと**宛先の無い scrape が恒常的に失敗する**。**置かない**（S3 に記載）。

## ADR-0078 / IADR-0404 の制約（違反していないことの確認）

| # | 制約 | 本 PR での守り方 |
| --- | --- | --- |
| C1 | 観測点は**近接 MTA のキュー**である（決定 3） | キュー長・滞留時間を spool から採る |
| C2 | **Keycloak → 近接 MTA の投函失敗も引き続き監査ログへ残す**（同） | 監査ログ側は 1 行も触らない。SC-15 の図と対応表も**そのまま**（S2） |
| C3 | **所要時間の閾値を決めない**（決定 1 §残るもの） | しきい値・`for` は**暫定**と 3 系統すべてに明記し、確定を PR-D の実測へ送る |
| C4 | 「窓は無い」と書かない（IADR-0347 決定 5 の規律） | 門の不在が観測に載っていないことを**残件として明示**する |
| C5 | 外部送信の統制（08_data-egress-policy） | **外部イメージを増やさない**（IADR-0421 決定 1）。exporter は既に居る `node:22-alpine` |
| C6 | 唯一の scrape 対象という不変条件（#546 / #1090 / IADR-0304） | Prometheus の `scrape_configs` を**変更しない**。取り手は collector 側 |

## 設計（決定は IADR-0421 が正本。ここには実装の割り付けだけを書く）

- **D-1 exporter**: `deploy/mail-relay/mail-queue-exporter.js`。`postfix_up` ／
  `postfix_queue_size{queue}` ／ `postfix_queue_oldest_message_age_seconds{queue}` を出す。
- **D-2 spool**: `emptyDir` を postfix（rw）と exporter（ro）で共有。**永続性は変わらない**。
- **D-3 滞留時間**: キュー ID から到着時刻を復号する（更新時刻は未来を指す）。
- **D-4 経路**: k8s の collector 設定 2 つに `prometheus/mail-relay`。compose には置かない。
- **D-5 アラート**: 3 件 ×3 系統。`MailRelayQueueSeriesAbsent` は既存の
  `platform-slo-evaluation-target` 群へ入れる（IADR-0370 と同じ形）。
- **D-6 画面**: 🔴 **SC-10 の画面仕様書は変えない。** SC-10 は専用ツールへの**入口**であり、
  時系列は Grafana で見る（ナレッジ健全性の 2 指標と同じ扱い）。**画面へ数字を出す変更は計画に無い。**
- **D-8 probe を置かない**: 🔴 **観測がメールを止めてはならない。** Pod の Ready は全コンテナの Ready を
  要求するので、サイドカーに readiness を置くと**その不調が Service の endpoint を落とし、投函できなくなる**
  （＝ 観測の都合で存在秘匿の窓 W1 を開ける）。exporter が答えないことは系列の不在で拾う。
  🔴 **同居の宿命は残る** —— コンテナが起動できなければ Pod ごと NotReady になる。**受容である**
  （静かには壊れず、門が申請を閉じるので存在秘匿は保たれる）。
- **D-7 NetworkPolicy**: `mail-relay.yaml` の既存宣言は書き換えず、**別宣言**で otel-collector に :9154 を開く
  （PR-C が :587 を足したのと同じ形。NetworkPolicy は加算的である）。

## 受け入れ基準と、それをどこで測るか

| # | 基準 | 測り方 | 結果 |
| --- | --- | --- | --- |
| A-1 | exporter がキュー長と滞留時間を正しく出す | `node deploy/mail-relay/mail-queue-exporter.js --self-test`（12 件） | ✅ |
| A-2 | **読めなかったキューに 0 を出さない**（系列ごと欠かす） | 同上（変異試験。下記） | ✅ |
| A-3 | **復号できない ID で嘘の齢を出さない** | 同上（変異試験） | ✅ |
| A-4 | アラートが 3 系統で 1 対 1 に一致する | `check-prometheus-alerts-parity.js` ／ `check-grafana-alerting.js` | ✅ 15/15 |
| A-5 | Grafana の provisioning が経路 A/B で同内容 | `check-grafana-provisioning-parity.js` | ✅ |
| A-6 | collector の自己テレメトリ宣言を壊していない | `check-collector-self-telemetry.js` | ✅ 3 件 |
| A-7 | 起動器の opt-in ゲートを壊していない | `node scripts/k8s-local-up.test.js` | ✅ 164 件 |
| A-9 | 検査器・規約の回帰が無い | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | ✅ 757 件 |
| A-10 | 編集した宣言が YAML として妥当（inline を含む） | `python3 -c "yaml.safe_load_all(...)"` で 9 ファイル ＋ 3 つの inline | ✅ |
| A-8 | trace ブロック・文書リンク・ID 修飾が規約どおり | `check-trace-blocks.js` ／ `check-doc-links.js` ／ `check-plan-id-qualification.js` ／ `check-cross-repo-refs.js` | ✅ |

### 変異試験（守るべき性質を壊すと赤になること・実走）

| 壊した性質 | 期待 | 実測 |
| --- | --- | --- |
| 読めないキューに `0` を出す | 「測っていない」が「滞留なし」に見える（#1110 / #1246 の形） | `🔴 読めなかったキューは 0 を出さず系列ごと欠かす` が **fail する**ことを試験で固定（`assert.ok(!/queue="missing"/…)`） |
| 復号できない ID を無視して最古を出す | 最古でないものを最古として出す | `復号できない ID が混ざったら throw する` が **fail する**ことを固定 |
| 区切りを**最初の** `z` にする | 時刻部に `z` を含む ID で齢が壊れる | `区切りは最後の z である` が **fail する**ことを固定（`lastIndexOf` → `indexOf` にすると赤くなる） |

🔴 **上の 3 つはいずれも「値は出るが意味が違う」形である**（#1110 と同型）。**数字が出ていることを
正しさの証拠にしない。**

## 🔴 配備と実測は利用者の手が要る（本 PR では 1 つも動かしていない）

**ローカルの compose も k8s も起動できない**（作業機に Docker / クラスタが無い）。

🔴 **`.NET` のビルド／試験も本作業機では完走できない。** `src/ai-stock-trading`（submodule）が
**未 populate** であり、`Platform.Bff` がその BFF エンドポイントを参照しているため
`dotnet build src/platform/backend/backend.slnx` が `CS0246` で落ちる（**基点 `7c9d184` から在る環境条件**）。
🔴 **本 PR の変更集合に `.cs` / `.ts` / `.tsx` は 1 件も無い**（内訳: `.md` 9 / `.yaml` 7 / `.yml` 2 /
`.js` 2 / `.json` 1 / `.sh` 1）ので、この失敗は本 PR に起因しないし、本 PR が影響し得る面でもない。
CI では submodule を取得して走る。
`helm` / `kubectl` / `kubeconform` も無いため `check-deploy-manifests.js` は
`DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` でしか走らせられない（**CI ではこの抜け道を使っていない**ことは
`scripts.repo.test.js` が固定している）。

**PR-D（稼働クラスタでの実測）で測るもの**:

1. **空の spool（emptyDir）を被せても mail-relay が起動すること** —— IADR-0421 の V16〜V19 は
   **上流ソースの読み取り**であり稼働の確認ではない。**これが最初に確かめるべき 1 件である。**
2. サイドカーが `:9154/metrics` で 3 計器を返すこと（`postfix_up 1`）。
3. Prometheus に `job="mail-relay"` の系列が立つこと（remote write 経由・転送構成を有効にした状態）。
4. **Prometheus の scrape 対象が otel-collector 1 つのままであること**（`up` の系列を数える）。
5. 上流を止めたときに `deferred` が増え、`postfix_queue_oldest_message_age_seconds` が伸び、復旧で 0 へ戻ること。
   **ここでしきい値（現在 `> 0` / `> 1200` / `for: 5m`）を確定する。**
6. アラートが 3 系統とも登録されること（Prometheus `/api/v1/rules` と Grafana
   `/api/v1/provisioning/alert-rules` が **15 件**）。
7. NetworkPolicy を強制するクラスタで collector が :9154 へ到達できること。
8. Grafana の Platform Overview にパネル 2 枚が描かれること。

## やらなかったこと（射程外・理由つき）

- **後送の失敗率を「率」として測ること** —— spool から事後に数えられない（IADR-0421 決定 7）。
  ログの取り込み経路が要る。**代理値（破棄の直前に鳴らす）で置いた。**
- **門（`reset-gate`）の不在を観測へ載せること** —— IADR-0404 フォローアップ (9)。
  門に計器を持たせる変更であり、本 PR（relay の観測）とは別の射程である。**残件として明示した。**
- **SC-10 の画面へ数字を出すこと**（D-6）。
- **`check-collector-self-telemetry.js` の射程を receiver まで広げること** ——
  「同型の事故が 2 回」の条件を満たしていない（本件が 1 回目）。**記録に留める。**
- **compose の collector 設定へ receiver を置くこと**（S5）。

## 関連仕様

- 実装 ADR: `IADR-0421`（本 PR）／`IADR-0404`（PR-A・PR-C）
- 可観測性仕様書: `docs/observability/mail-relay-queue-metrics.md`（本 PR で新設）
- 運用 Runbook: `docs/operations/keycloak-smtp-relay-setup-runbook.md`（観測の見方を追加）
- テスト仕様書: `docs/tests/SC-15_password-reset.md`（T-13 の観測点を追随）
