---
title: 作業仕様書 — Grafana・Loki・Tempo・Qdrant・Mailpit の既定の外部通信を全経路の配備で無効化し、検査器で固定する（#1841・planning#750 の裁定の 3）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0107
  - ADR-0135
  - ADR-0006
  - ADR-0009
  - ADR-0045
  - IADR-0519
  - IADR-0514
  - IADR-0461
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md コンポーネント別の統制表の製品別の行（2026-10-09 追加・e66aa56）と表の下の注
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 4（基準 C）・フォローアップ 3
  - planning:projects/microservices-platform/10_feedback/20261009_infra-product-inspection-rulings.md 論点 3
related_specs:
  - 20261008_1787_infra-audit-digest-pin
issue: "#1841"
---

# 作業仕様書 — インフラ製品の既定の外部通信を全経路の配備で止める（#1841）

## 目的と射程

計画の 08_data-egress-policy の統制表に planning#750 で足された製品別の行のうち、**Grafana・Loki・Tempo・Qdrant・Mailpit** は
「配備後に無効化する」「現在は未設定であり、送信し得る」と書かれている。製品が動く**全経路**の配備に無効化を入れ、外すと CI が落ちる
検査を置き、文書に現在の実現手段を併記する。6 行目の **TEI** は「既定で無効（opt-in）・有効化の前にモデルを事前配置／ミラー」であり、
設定で止める対象ではない（決定は IADR-0519 決定 3。本件では配備を変えない）。

**射程外**: egress の既定拒否（default-deny。計画 §リスク・未決事項の別件）、表に無い製品の点検、TEI の有効化の前提作り。

基点: MSP `origin/develop` `afa9b917`（push の前に `f795f73a` へ rebase した。衝突は security・operations の trace ブロックだけ）。計画: `project-planning` `origin/main` `142c3e4`（08 の最終変更 `e66aa56`）。

## 計画の行（読み取り。隣接クローン・`git show origin/main:`）

| 行 | 既定で外へ送るもの（計画の記述） | 計画の統制 |
| --- | --- | --- |
| Grafana | `[analytics] reporting_enabled`（stats.grafana.org・24h）・`check_for_updates`・`check_for_plugin_updates`（grafana.com）。上流 `conf/defaults.ini` v11.0.0 | 3 キーをいずれも `false` |
| Loki | `analytics.reporting_enabled`（stats.grafana.org/loki-usage-report）。`pkg/analytics/stats.go` v3.0.0 | `analytics.reporting_enabled: false` |
| Tempo | `usage_report.reporting_enabled`（stats.grafana.org/tempo-usage-report）。`pkg/usagestats/stats.go` v2.5.0 | `usage_report.reporting_enabled: false` |
| Qdrant | テレメトリ（telemetry.qdrant.io）。`src/common/telemetry_reporting.rs` v1.18.1 | `QDRANT__TELEMETRY_DISABLED=true` |
| Mailpit | 最新版の確認（GitHub） | `MP_DISABLE_VERSION_CHECK` |
| TEI | 起動時に HF Hub からモデルを取得 | 既定で無効。有効化の前に事前配置かミラー |

## 母集合の引き方（規則 9・10・6）

**軸 1（製品が動く経路＝イメージの参照）**: `git grep -n -E 'grafana/grafana|grafana/loki|grafana/tempo|qdrant/qdrant|axllent/mailpit' -- . ':!.ai-context' ':!src/ai-stock-trading'`

| ヒット | 扱い |
| --- | --- |
| `deploy/docker-compose.yml`（qdrant・loki・tempo・grafana） | **対象（経路 A）** |
| `deploy/local/infra/qdrant.yaml`・`mailpit.yaml` | **対象（経路 B。`infra-persistence` overlay も同じ base を描画）** |
| `deploy/local/observability/grafana.yaml`・`loki.yaml`・`tempo.yaml` | **対象（経路 B の opt-in。`observability-persistence` も同じ base）** |
| `src/knowledge/.../Fixtures/QdrantTestImage.cs`（Testcontainers の参照） | **対象（統合試験。CI の runner から外へ出る）** |
| `docs/how-to/run-integration-tests-without-docker.md:47`（`nerdctl run … qdrant`） | **対象（手で起こす経路）** |
| `scripts/verify-qdrant-attribute-payload.sh:18`（コメントの `docker run` の例） | **対象（人が写して打つ）** |
| `scripts/check-grafana-alerting.js`・`check-image-digests.js`・`check-stack-ready.js` の試験の固定入力・コメント | 除外。製品を起こさない |
| `QdrantTestImageDefinitionTests.cs` の正規表現 | 除外。起こさない |

`deploy/helm/`（本番像の chart）は 5 製品を配備しない（TEI のみ opt-in）。描画結果で確かめた（`helm template` の既定と `ci/*.yaml` 3 本で 5 製品 0 件）。

**軸 2（`scripts/` が入れる製品）**: `git grep -n -E 'helm (upgrade|install)|kubectl apply -f https' -- 'scripts/*.sh'` —— istio（base / istiod / gateway）・external-secrets・reloader・本 chart。5 製品は無い。`deploy/istio/README.md` の `kubectl apply -f …/samples/addons/{kiali,prometheus}.yaml` は手順書の任意の手順で、5 製品ではない（除外）。

**軸 3（誤りの側の記述＝「配備は未設定」等）**: `git grep -n -E '配備は未設定|配備に無効化が入っていない|無効化が配備に入っていない|v1\.21\.8|MP_DISABLE_VERSION_CHECK|QDRANT__TELEMETRY_DISABLED|reporting_enabled|check_for_updates'`

| ヒット | 扱い |
| --- | --- |
| `docs/operations/operations.md` 点検の記録 2026-10-08 の表（qdrant・loki・tempo・grafana・mailpit の「配備は未設定」）と「残り」 | **対象**。時点つきの記録なので表は書き換えず、「残り」に日付つきの済みの印と、2026-10-09 の記録（mailpit の誤りの訂正を含む）を足した |
| `.ai-context/specs/20261008_1787_infra-audit-digest-pin.md:81-94` | 除外。確定済みの作業仕様書（凍結） |
| `scripts/check-stack-ready.js:1979` の `"Version":"v1.21.8"`（G8 の試験の固定入力） | **対象**。G8 に `LatestVersion` の判定を足したので固定入力を `v1.31.4`・`disabled` へ更新し、`v1.21.8` は陰性の入力へ移した |

**軸 4（Grafana の設定の置き場）**: `git grep -n -E 'grafana\.ini|/etc/grafana'` —— provisioning のマウントだけで、ini は無かった。`check-grafana-provisioning-parity.js` が `grafana.yaml` の ConfigMap の鍵を compose の `provisioning/` と 1 対 1 で突き合わせることを確かめ、ini の ConfigMap を別ファイルにした。

**規則 10（この変更で新たに誤りになる自分の記述）**: Mailpit の版を上げたので `v1.21.8` を引き直した（上の軸 3）。`deploy/local/observability/README.md` の構成表に `grafana-config.yaml` を足した。`scripts/README.md` の `check-deploy-manifests.js` の行に検査の追加を書いた。trace ブロックへ本仕様書・IADR-0519・#1841 を足した（security・operations・how-to）。

## 上流での設定の確認（版ごと。出典）

| 製品（固定の版） | 設定 | 出典（raw.githubusercontent.com の固定タグ） | 確かめたこと |
| --- | --- | --- | --- |
| Grafana 11.0.0 | `[analytics] reporting_enabled` / `check_for_updates` / `check_for_plugin_updates` | `grafana/grafana/v11.0.0/conf/defaults.ini` 244-266 行 | 既定 `true`。送信先 stats.grafana.org・grafana.com |
| 同 | `[news] news_feed_enabled` | 同 1405-1407 行 | 既定 `true` |
| 同 | `[security] disable_gravatar` | 同 330-331 行 | 既定 `false`（ブラウザが取りに行く） |
| 同 | `[plugins] public_key_retrieval_disabled` | 同 1587-1591 行 | 既定 `false`。実測で起動時と 60 秒ごとに `grafana.com/api/plugins/ci/keys` |
| 同 | `[feature_toggles] pluginsDynamicAngularDetectionPatterns` | `pkg/services/featuremgmt/registry.go` 521-527 行（`Expression: "true"`）・`pkg/services/pluginsintegration/angulardetectorsprovider/dynamic.go` 236-238 行（`IsDisabled`） | 既定で有効。実測で起動時に `grafana.com/api/plugins/angular_patterns` |
| 同 | 環境変数で上書きできる範囲 | `pkg/setting/setting.go` 635-650 行（`applyEnvVariableOverrides` は ini に在る鍵だけを回る）・`pkg/setting/setting_feature_toggles.go`（`enable` は有効化だけ） | 機能トグルを env で個別に切れない → ini ファイル |
| Loki 3.0.0 | `analytics.reporting_enabled`（フラグ `-reporting.enabled`） | `pkg/analytics/reporter.go` 35-51・68 行・`pkg/analytics/stats.go` 27 行・`pkg/loki/loki.go` 108 行（`yaml:"analytics"`） | 既定 `true`。送信は 4 時間ごと（`nextReport`） |
| Tempo 2.5.0 | `usage_report.reporting_enabled`（フラグ `-reporting.enabled`） | `pkg/usagestats/config.go`（`yaml:"reporting_enabled"`・既定 true）・`cmd/tempo/app/config.go` 57・134 行・`pkg/usagestats/stats.go` 27 行 | 既定 `true`。設定は `yaml.UnmarshalStrict`（鍵の誤りは起動失敗）、フラグは設定ファイルの後に解析（フラグが勝つ。`cmd/tempo/main.go` 193-207 行） |
| Qdrant v1.18.1 | `QDRANT__TELEMETRY_DISABLED`（`telemetry_disabled`） | `src/main.rs` 381 行（`!settings.telemetry_disabled && !args.disable_telemetry`）・`src/common/telemetry_reporting.rs` 19・38-45・75-85 行・`config/config.yaml` 369 行 | 既定で有効。起動直後に送り、以後 1 時間ごと |
| Mailpit v1.21.8 | （無し） | `config/config.go`・`cmd/root.go` に `DisableVersionCheck` が無い。`internal/stats/stats.go` 81-94 行で `/api/v1/info` のたびに `updater.GithubLatest`（`internal/updater/updater.go` 52 行。api.github.com） | **設定で止められない** |
| Mailpit v1.31.4 | `MP_DISABLE_VERSION_CHECK` | `cmd/root.go` 207 行・`internal/stats/stats.go` 100-102 行（`LatestVersion = "disabled"`）・`server/apiv1/application.go` 31 行 | タグを v1.22.0〜v1.27.0 で走査し、`DisableVersionCheck` は **v1.26.2 で初出**（v1.26.1 に無い） |

## 決定（詳細は IADR-0519）

- 設定: Grafana は ini（7 鍵）を経路 A はファイル、経路 B は ConfigMap `grafana-config`（別ファイル）で `/etc/grafana/grafana.ini` へ。Loki・Tempo は設定ファイルの鍵（compose と k8s の inline の両方）。Qdrant は env（compose・k8s・Testcontainers の唯一の入口 `QdrantTestImage.CreateBuilder()`・手順書・スクリプトの例）。Mailpit は v1.31.4 へ上げて env。
- 検査器: `check-deploy-manifests.js` に統合（判定は `scripts/lib/product-egress-defaults.js`、YAML は `scripts/lib/yaml-subset.js`）。稼働の Mailpit は `check-stack-ready.js` の G8。
- IADR は **IADR-0519**（push 時点の最大番号 +1。並行する PR が先に取った場合は改番する）。

## 実測（到達しないことの確かめ）

方法: 公式のリリースのバイナリ（配備と同じタグ。コンテナは docker デーモンが無く起こせない）を、**外へ転送しない捕捉プロキシ**
（`CONNECT` と平文の要求を記録して 403 を返す Node のスクリプト）を `HTTPS_PROXY` / `HTTP_PROXY` に与えて起動し、無効化の前後を比べた。
`NO_PROXY=127.0.0.1,localhost`。捕捉は「試行」の記録で、プロキシが 403 を返すので実際には 1 バイトも外へ出ていない。

```console
== qdrant-default (proxy log)
CONNECT telemetry.qdrant.io:443          # 起動の約 1 秒後。app.log: "Telemetry reporting enabled" / "Failed to report telemetry"
== qdrant-disabled (QDRANT__TELEMETRY_DISABLED=true, 25 秒)
（0 件）                                   # app.log: "Telemetry reporting disabled"
== mailpit21-default / mailpit21-disabled (MP_DISABLE_VERSION_CHECK=true)   # /api/v1/info を 1 回読む
CONNECT api.github.com:443                # 両方とも捕捉。info: "Version":"v1.21.8" "LatestVersion":""
== mailpit31-default
CONNECT api.github.com:443
== mailpit31-disabled
（0 件）                                   # info: "Version":"v1.31.4" "LatestVersion":"disabled"
== grafana-default (75 秒)
CONNECT grafana.com:443 ×4（起動時）+ ×1（60 秒後）
== grafana-disabled（計画の 3 鍵 + news + gravatar を env で）
CONNECT grafana.com:443 ×2（起動時）+ ×1（60 秒後）   # app.log: key_retriever "Get https://grafana.com/api/plugins/ci/keys"・
                                                       # angulardetectorsprovider "Get https://grafana.com/api/plugins/angular_patterns"
== grafana-ini（deploy/grafana/grafana.ini を --config で, 75 秒）
（0 件）                                   # /api/health 200
== loki（3.0.0。compose の設定の写し。ポートだけ変えた）  GET /config
変更前: analytics.reporting_enabled: true   変更後: analytics.reporting_enabled: false
== tempo（2.5.0。同） GET /status/config
変更前: usage_report.reporting_enabled: true  変更後: usage_report.reporting_enabled: false
```

- Loki・Tempo は送信の初回が 4 時間後（`nextReport`）で観測の窓に入らない。送信の不在ではなく**実効設定**で確かめた。Loki の捕捉に出た
  `CONNECT :19095`（宛先ホストが空）は自身の gRPC（loopback）へのプロキシ経由の接続で、外部ではない。
- Mailpit v1.31.4 の互換: SMTP で 1 通送り、`/api/v1/info` の `Messages`・`/api/v1/messages?limit=1` の `messages[0].ID`・
  `/api/v1/message/{id}` の `Subject` / `Text` / `To[].Address` / `Attachments`・`/readyz` `/livez` 200 を確かめた（`check-password-reset-mail` と G8 が読む形）。
- ブラウザ側（Grafana のニュース・Gravatar）は捕捉していない（残余）。

## 検査器の確かめ（陽性・陰性）

**YAML の部分集合のパーサ**: 実物の描画結果（overlay 17・chart 4 values）と素のマニフェスト 17 本の計 38 件を PyYAML（6.0.1）と突き合わせた。
差は `defaultMode: 0755`（PyYAML は 8 進数の整数 493 として読む。本パーサは文字列）の 1 件だけ。

**実物の変異（`check()` を全経路で回す。各変異を 1 つ当てて戻す）** —— 20 件すべて落ちた:

| # | 変異 | 結果（抜粋） |
| --- | --- | --- |
| M01 | compose の ini の `reporting_enabled = true` | compose: grafana の `[analytics] reporting_enabled` が false でない |
| M02 | k8s の ini から Angular のトグルを削除 | observability・observability-persistence の 2 件 |
| M03 | compose の ini のマウントを削除 | ini がマウントされていない |
| M04 | k8s の subPath を変える | 同（2 overlay） |
| M05 | k8s に `GF_ANALYTICS_CHECK_FOR_UPDATES=true` を足す | env が ini を上書き（2 overlay） |
| M06 | compose の Loki 設定を `true` | `analytics.reporting_enabled` が false でない |
| M07 | k8s の Loki の鍵を削除 | 未設定＝既定の true（2 overlay） |
| M08 | k8s の Loki に `-reporting.enabled=true` | フラグが上書き（2 overlay） |
| M09 | compose の Tempo の鍵を削除 | 未設定 |
| M10 | k8s の Tempo を `true` | 2 overlay |
| M11 | compose の Qdrant の env を `false` | Qdrant |
| M12 | k8s の Qdrant の env を削除 | infra・infra-persistence |
| M13 | Mailpit の env を削除 | 2 overlay |
| M14 | Mailpit を v1.21.8 へ戻す（env は残す） | 「v1.21.8 は最新版の確認を止める設定を持たない」 |
| M15 | `CreateBuilder()` の `WithEnvironment` を削除 | Testcontainers |
| M16 | 試験の 1 本を `new QdrantBuilder(…).Build()` へ戻す | Testcontainers（行番号つき） |
| M17 | Mailpit のイメージ名を照合に掛からない名前へ | 「mailpit が 1 度も見つからなかった」 |
| M18 | `infra-persistence` に env を `false` へ書き換える JSON patch を足す | **infra-persistence だけ**が落ちる（描画結果で見る理由） |
| M19 | how-to の `nerdctl run` から env を削除 | 手で起こす手順 |
| M20 | verify スクリプトのコメントの `docker run` から env を削除 | 同 |

**検査器のコードの変異（`scripts.repo.test.js` の #1841 の試験 14 件が落ちるか）**: Qdrant の判定を常に通す → 3 件落ちる／Mailpit の版の判定を外す → 1 件／
Loki・Tempo のフラグの上書きを見ない → 1 件／Grafana の env の上書きを見ない → 1 件／subPath の解決を壊す → 2 件。

## 受け入れ基準

- [x] 計画の製品別の行を読み、各製品の既定の外部通信を配備（`deploy/local` の overlay・compose・統合試験・手で起こす手順）で無効化した。helm chart は 5 製品を配備しない（描画で確認）
- [x] 無効化を機械で確かめる手段（`check-deploy-manifests.js` ＋ `lib/product-egress-defaults.js`、稼働は `check-stack-ready.js` の G8）を置き、変異 20 件がすべて落ちる
- [x] 外部の送信先に到達しないことを実測した（Qdrant・Mailpit・Grafana は捕捉 0 件、Loki・Tempo は実効設定。窓に入らない理由を記録）
- [x] 文書（`docs/security/security.md`・`docs/operations/operations.md`・`docs/how-to/run-integration-tests-without-docker.md`）へ統制と現在の実現手段を併記した
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-deploy-manifests`（`--self-test` と本走査）・`check-stack-ready --self-test`・`check-grafana-provisioning-parity`・`check-image-digests`・trace / ADR 採番 / コミット / 文書の各検査が緑

## 計画への環流の要否

- **Grafana の行は計画の 3 鍵だけでは止まらない**（公開鍵の取得・Angular 検出パターンの取得が残る。実測）。実装は基本原則（既定の外部通信を無効化する）に従って 7 鍵で止めたので統制は働いているが、計画の行の「3 キーをいずれも false」は不足である。**計画の行の追記を環流する価値がある**（本 PR では起票しない。オーケストレータへ報告し判断を仰ぐ）。
- **Mailpit の行の「`MP_DISABLE_VERSION_CHECK`」は v1.26.2 以上が前提**（当時の配備 v1.21.8 では効かない）。版を上げたので統制は成立する。行の前提（版）を補う環流の候補。
- 08 の各行の「🔴 現在は未設定であり、送信し得る」は、本 PR のマージで実装の事実と食い違う（計画側の更新事項。環流の issue で知らせる）。

## 必須チェックへの影響

`static-checks-units` の `check-deploy-manifests.js` が新しい失敗理由を持つ（既存のジョブ・名前は変えない）。ワークフローはコメントだけを足した（起動条件・必須チェックは不変）。
