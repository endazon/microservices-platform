---
title: IADR-0520 インフラ製品（Grafana・Loki・Tempo・Qdrant・Mailpit）の既定の外部通信を、製品が動く全経路の配備で止め、描画結果・compose・Testcontainers を読む検査器で固定する。Mailpit は止める設定を持つ版へ上げる
type: impl-adr
status: Accepted
related_ids: [ADR-0107, ADR-0135, ADR-0006, ADR-0009, ADR-0045, IADR-0514, IADR-0461, IADR-0240, IADR-0168, IADR-0344, IADR-0315]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md コンポーネント別の統制表の製品別の行（Grafana・Loki・Tempo・Qdrant・Mailpit・TEI。2026-10-09 追加。planning#750）
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 4（基準 C）・フォローアップ 3
related_specs:
  - ../specs/20261009_1841_disable-default-egress.md
---

# IADR-0520: インフラ製品の既定の外部通信を全経路の配備で止め、検査器で固定する（#1841）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: claude（設定の置き方・検査器の形・Mailpit の版）

## 起点・関連

- 起点 issue: **#1841**（planning#750 の裁定の 3。計画 ADR-0107 フォローアップ 3 で 08_data-egress-policy の統制表に足された製品別の行の実装）。
- 計画: **ADR-0107 決定 4**（基準 C: 既定の外部通信を無効化できること）・08_data-egress-policy の製品別の行（「配備後に無効化する」「現在は未設定であり、送信し得る」）。
- 前提: [IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（初回点検・digest 固定）・[IADR-0461](./IADR-0461_object-storage-seaweedfs-deployment.md)（SeaweedFS の `-master.telemetry=false`。同じ統制の先例）・[IADR-0240](./IADR-0240_deploy-manifest-schema-validation.md)（`check-deploy-manifests.js` の描画とスキーマ突合）・[IADR-0168](./IADR-0168_grafana-provisioning-parity.md)（Grafana の provisioning の経路間パリティ）・[IADR-0344](./IADR-0344_dev-mail-capture-mta.md)（Mailpit）・[IADR-0315](./IADR-0315_qdrant-server-version-follows-client.md)（試験の Qdrant は配備と同じ版）。
- 基点コミット: MSP `origin/develop` `afa9b917`。

## コンテキストと課題

初回点検（2026-10-08）で、Grafana・Loki・Tempo・Qdrant・Mailpit は既定で外へ送る（利用統計・更新確認・テレメトリ）のに、配備に無効化が入っていないことが分かった。計画は統制表へ製品別の行を足し、配備への投入を実装へ回した。製品が動く経路は 3 つある —— **経路 A（compose）**・**経路 B（`deploy/local/` の overlay）**・**統合試験（Testcontainers。CI の runner から外へ出る）**。本番像の chart（`deploy/helm/`）はこの 5 製品を配備しない（TEI だけを opt-in で持つ）。

着手時の実測（作業仕様書）で、計画の行だけでは止まらないことが 2 つ分かった:

1. **Mailpit v1.21.8 は最新版の確認を止める設定を持たない。** `MP_DISABLE_VERSION_CHECK` は v1.26.2 で入った（上流の `config/config.go` をタグごとに確認）。v1.21.8 へ与えても `/api/v1/info` を読むたびに `api.github.com` へ問い合わせる（捕捉プロキシで実測）。この API は本リポジトリの `check-stack-ready`（G8）と `check-password-reset-mail` が読むので、**稼働のたびに問い合わせていた**。
2. **Grafana 11.0.0 は計画の 3 鍵の他にも既定で `grafana.com` へ行く。** 3 鍵を止めても、プラグイン署名の公開鍵の取得（`[plugins] public_key_retrieval_disabled`。起動時と 60 秒ごと）と Angular 検出パターンの取得（機能トグル `pluginsDynamicAngularDetectionPatterns`。起動時）が残る（捕捉プロキシで実測）。機能トグルは環境変数で個別に切れない（`GF_*` は ini に在る鍵しか上書きしない。上流 `pkg/setting/setting.go` の `applyEnvVariableOverrides`）。

## 検討した選択肢

### 設定の置き方

- **A. 製品ごとに上流が文書化した設定を、各経路の配備の定義に入れる（採用）** —— Qdrant・Mailpit は env、Loki・Tempo は設定ファイルの鍵（計画の行が名指す鍵）、Grafana は ini ファイル。SeaweedFS の先例（配備と同時に効かせる）と同じ。
- B. Loki・Tempo はフラグ（`-reporting.enabled=false`）で止める —— 検査はコンテナの引数だけで済むが、計画の行・上流文書が名指すのは設定ファイルの鍵であり、compose と k8s の設定ファイルが「同内容を inline」する現行の二重管理から外れる。採らない（フラグによる**上書き**は検査で見る）。
- C. egress の既定拒否（NetworkPolicy）で止める —— 計画の §リスク・未決事項で実装の確認が未決の別件。compose と Testcontainers には効かない。採らない（本件は製品側で止める）。

### Grafana の ini

- **ini ファイルに止める設定 7 つをすべて置く（採用）**。経路 A は `deploy/grafana/grafana.ini` を `/etc/grafana/grafana.ini` へ読み取り専用でマウント、経路 B は同じ内容を ConfigMap `grafana-config`（新ファイル `deploy/local/observability/grafana-config.yaml`）に inline して subPath でマウントする。
- env と ini の併用（6 つは env、トグルだけ ini）は置き場が 2 つに割れる。採らない。
- ConfigMap を `grafana.yaml` に置かないのは、`check-grafana-provisioning-parity.js` が同ファイルの ConfigMap の鍵を compose の `provisioning/` と 1 対 1 で突き合わせるため（`grafana.ini` は provisioning ではない）。

### Mailpit

- **止める設定を持つ版（v1.31.4。調査時点の最新）へ上げる（採用）**。v1.26.2（最小）でなく最新を採るのは、点検の手順（ADR-0135 の即時の版固定）と同じく、上げるなら保守の続く版へ寄せるため。API（`/api/v1/info`・`/messages`・`/message/{id}`）・env（`MP_SMTP_AUTH_ACCEPT_ANY`・`MP_SMTP_AUTH_ALLOW_INSECURE`・`MP_MAX_MESSAGES`）・`/readyz` `/livez` が使える形のままであることを手元の v1.31.4 で確かめた（作業仕様書）。digest は Docker Hub と `mirror.gcr.io` の 2 か所で index の digest が一致することを確かめた。
- 上げずに残余として記録する —— 本リポジトリの検査が稼働のたびに外へ問い合わせ続ける。採らない。

### 検査器

- **`check-deploy-manifests.js` に統合し、判定は `scripts/lib/product-egress-defaults.js` に置く（採用）**。描画の段（chart の各 values・各 overlay）で同じ出力を判定するので、overlay の差分（永続化の patch）による上書きも見える。compose と Testcontainers はツールが要らないので、ツール不在で飛ばす分に含めない。
- `check-static-egress.js` へ足す —— あちらは SPA の静的成果物（ブラウザが取りに行く参照）を見る検査器で、対象が違う。採らない。
- 素のマニフェストを正規表現で見る —— 「設定の文字列がどこかに在る」しか言えず、別の ConfigMap・別のコンテナに在っても通る。採らない。**判定は「その製品のプロセスから見える設定」で行う**（env・`command` / `args`・volume → volumeMount で辿ったマウント先のファイル）。
- YAML の読み取りは `scripts/lib/yaml-subset.js`（部分集合のパーサ。Node 標準のみ）。外部の YAML パーサを持ち込まない方針（`check-grafana-provisioning-parity.js` と同じ）に従う。読めない形は例外で止める。実物の描画結果 38 件を PyYAML と突き合わせ、型の解釈（`0755` を 8 進数として読むか）以外が一致することを確かめた。

## 決定

### 決定 1: 製品ごとの設定（全経路）

| 製品（版） | 止めるもの（送信先） | 設定 | 経路 A（compose） | 経路 B（`deploy/local/`） | Testcontainers |
| --- | --- | --- | --- | --- | --- |
| Grafana（11.0.0） | 利用統計（stats.grafana.org）・更新確認・プラグインの更新確認・ニュース（grafana.com）・Gravatar（ブラウザ）・公開鍵の取得・Angular 検出パターンの取得（grafana.com） | ini の 7 鍵（`[analytics] reporting_enabled` / `check_for_updates` / `check_for_plugin_updates`・`[news] news_feed_enabled`・`[security] disable_gravatar`・`[plugins] public_key_retrieval_disabled`・`[feature_toggles] pluginsDynamicAngularDetectionPatterns`） | `deploy/grafana/grafana.ini` をマウント | ConfigMap `grafana-config` を subPath でマウント | 使わない |
| Loki（3.0.0） | 利用統計（stats.grafana.org/loki-usage-report） | `analytics.reporting_enabled: false` | `deploy/loki-config.yaml` | `observability/loki.yaml` の inline | 使わない |
| Tempo（2.5.0） | 利用統計（stats.grafana.org/tempo-usage-report） | `usage_report.reporting_enabled: false` | `deploy/tempo.yaml` | `observability/tempo.yaml` の inline | 使わない |
| Qdrant（v1.18.1） | テレメトリ（telemetry.qdrant.io。起動直後と 1 時間ごと） | env `QDRANT__TELEMETRY_DISABLED=true` | `environment` | `infra/qdrant.yaml` の env | `QdrantTestImage.CreateBuilder()`（唯一の入口） |
| Mailpit（v1.21.8 → **v1.31.4**） | 最新版の確認（api.github.com） | env `MP_DISABLE_VERSION_CHECK=true` | 配備しない | `infra/mailpit.yaml` の env | 使わない |

計画の行に無い 4 つ（Grafana のニュース・Gravatar・公開鍵・Angular 検出パターン）は、計画の基本原則（既定の外部通信を無効化する）の適用であり、計画の行を変えない。計画の行へ足すかは計画側の判断である（環流の要否は作業仕様書）。

### 決定 2: 検査器（`check-deploy-manifests.js` ＋ `lib/product-egress-defaults.js`）

- 製品の表 `PRODUCTS`（イメージの照合と判定）を単一情報源とする。コンテナのイメージが表の製品なら、そのコンテナから見える設定を組み立てて判定する。
- **上書きも見る**: Loki / Tempo の `-reporting.enabled`（フラグが設定ファイルに勝つ）・Grafana の `GF_*` の env（ini に勝つ）・`GF_PATHS_CONFIG` / `--config`（ini の置き場を変える）。
- **版の穴も見る**: Mailpit は v1.26.2 より前を、env が在っても落とす。
- **0 件で緑にしない**: 走査全体（chart・overlay・compose）で 5 製品のどれかが 1 度も見つからなければ失敗。Testcontainers の `new QdrantBuilder(` が 0 件でも失敗。
- **知らない経路は失敗**: Grafana・Loki・Tempo・Mailpit のイメージを `.cs` から起こしたら失敗（この検査が判定を持たない）。
- 経路 B の稼働では、`check-stack-ready` の G8 が Mailpit の `/api/v1/info` の `LatestVersion` が `"disabled"` であることを確かめる（**稼働で効いていることの実測**。静的検査では「設定は在るが効かない版」を版の照合でしか見られない）。

### 決定 3: TEI は対象外

TEI（埋め込み）の外部通信は起動時のモデルの取得そのもので、設定で止めるものではない。配備は既定で無効（compose の profile・chart の opt-in）であり、計画の統制は「有効化の前にモデルを事前に配置するか、自社管理のミラーから取得する形にする」である。本件では扱わない（有効化の作業の前提として残余に記録する）。

## 結果

- 良い影響: 3 経路で 5 製品の既定の外部通信が止まり、外すと CI（`static-checks-units` の `check-deploy-manifests`）が落ちる。Mailpit は稼働の検査が外へ問い合わせなくなった。Qdrant の統合試験は CI の runner から外へ送らなくなった。
- 悪い影響: 検査器が YAML の部分集合のパーサを持つ（保守の対象が増える）。Grafana の ini と ConfigMap が二重管理になる（同内容であることは `scripts.repo.test.js` が固定する）。Mailpit の版が上がった（dev 限定）。

## 残余

1. **egress の既定拒否が無い**。設定を外した変更が検査をすり抜けた場合（例: 表に無い新製品）に送信を止める手段は無い。計画の §リスク・未決事項（default-deny の実装確認）の別件。
2. **ブラウザ側の通信は捕捉していない**。Grafana のニュース・Gravatar は利用者のブラウザが取りに行くもので、実測はサーバのプロセスの捕捉に限る（設定は上流の既定値の文書で確かめた）。
3. **Loki・Tempo の送信は 4 時間ごと**で、捕捉プロキシの実測窓（15 秒）では送信の発生そのものを観測できない。実効設定（`/config`・`/status/config`）が `reporting_enabled: false` を返すことを実測した。
4. **TEI**（決定 3）。
5. 表に無い製品（headlamp 等。点検の記録では「既知の既定の外部通信は無い（未実測）」）は本検査の対象外。新しい製品は点検（基準 C）で表へ足す。

## フォローアップ

- なし（本 IADR と同じ PR で配備・検査・文書まで入れた）。
