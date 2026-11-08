---
title: 運用 Runbook — 配備する第三者のイメージの脆弱性の走査（週次・結果は Security タブだけ）と更新の通知（Dependabot）の扱い
type: runbook
status: draft
author: claude
created: 2026-11-08
updated: 2026-11-08
---
<!-- trace:
ids: [NFR-18]
adrs: [ADR-0107]
iadrs: [IADR-0708, IADR-0711, IADR-0514, IADR-0584, IADR-0232]
specs: [20261108_ci-review-perms-image-scan, 20261108_dependabot-docker-safe-updates]
-->

# 運用 Runbook: 第三者のイメージの走査と更新

> 対象は `deploy/` が digest（`<repo>:<tag>@sha256:…`）で固定して配備する上流のイメージ（RabbitMQ・Prometheus・Alertmanager・
> collector・OpenBao・Keycloak・Postgres・node など）である。自製のイメージは `images.yml` の走査が見る。
>
> **状態を `draft` に留めている理由**: 週次の走査が develop でまだ 1 回も走っていない（最初の結果の扱いを記録していない）。
>
> 🔴 **本リポジトリは public である。走査の結果（どのイメージに何件・どの CVE を残しているか）を公開の面へ出さない。**
> 公開の面とは、issue・PR の本文とコメント・コミット・Actions のログと注記と実行の要約・ジョブの成否・リポジトリの中のファイルである。
> 結果を見る・扱うのは Security タブ（code scanning。書き込みの権限を持つ人だけが見る）の中だけで行う。

## 何が動くか

| 仕組み | いつ | 何をする | 結果を見る場所 |
| --- | --- | --- | --- |
| `Third-party Image Scan`（`.github/workflows/third-party-image-scan.yml`） | 週次（火曜 04:17 UTC）・手動（workflow_dispatch） | `node scripts/check-image-digests.js --scan-targets` の全イメージを Trivy で走査し、修正版のある CRITICAL / HIGH を SARIF で code scanning へ上げる。**issue は起票しない。見つかった脆弱性でジョブを落とさない** | Security タブ → Code scanning（カテゴリ `third-party-image/<イメージ名-tag>`）。新しい alert の通知は、リポジトリを watch する設定（Security alerts）で受ける |
| 同上（pull_request） | `deploy/**`・対象の選び方・本ワークフローを触る PR | 走査が完走したかだけを示す。SARIF を上げず、件数も出さない。**必須チェックではない** | なし（PR の版で増えたかは、マージ後の週次か手動の実行の alert で見る） |
| Dependabot（`.github/dependabot.yml` の `docker`） | 週次 | `deploy/cloud/**` の `image:` の修正版・小版を、**1 イメージ 1 本の PR** で出す（修正版と小版は PR の題で分かる）。大版・下の除外は出さない | 下の §更新の PR を仕上げる・§Dependabot が出さない更新 |

- ジョブが赤になるのは、走査そのものが失敗した（結果が無い・読めない）ときだけである。レジストリの一時的な失敗なら手動で再実行する。
  続くならイメージの参照（digest の実在）を `docker buildx imagetools inspect <ref>` で確かめる。
- 走査の対象は `deploy/` の参照だけ（`src/` の基底イメージ・Testcontainers は対象外）。対象の一覧は手元で
  `node scripts/check-image-digests.js --scan-targets` で出せる。

## alert が立ったら（Security タブの中で扱う）

判断の基準は **修正版がある CRITICAL / HIGH の open の alert を、次の週次の走査までに 0 件にする**こと。
次の 1〜3 のどれかを選ぶ。どれを選んでも、**件数・イメージ名・CVE を公開の面へ書かない**。

1. **上げる（既定）**: 修正の入った版の tag と digest に上げる。§更新の PR を仕上げるの手順で、同じイメージの参照を全部揃える。
   PR の本文・コミットには「<製品> を <版> へ上げる」とだけ書き、理由の脆弱性は書かない。上げたあとの週次の走査で alert が閉じる。
2. **届かないので受け入れる**: その脆弱性の在る部品をこの配備で使わない・外から届かない、と説明できるときだけ。
   Security タブで alert を **Dismiss**（`Won't fix`）し、注記に理由と見直しの日（今日から 90 日以内）を書く。
   - リポジトリの ignore ファイル（`.trivyignore` 等）には書かない（公開のファイルになる。`scripts/scripts.repo.test.js` が置くことを止める）。
   - dismiss には期限が無い。**月に 1 回、dismiss 済みの alert の見直しの日を確かめ、過ぎたものを reopen する**（オーナーの作業）。
3. **上げられない**（メジャーの移行が要る等）: alert を open のまま残し、移行を計画へ載せる。計画の記述・issue には
   「<製品> のメジャーの移行」とだけ書き、脆弱性には触れない。

## 更新の PR を仕上げる（Dependabot・手動）

Dependabot の docker は `deploy/cloud/**` だけを見る。同じイメージは `deploy/local/`・`deploy/docker-compose.yml`・helm の values にも在り、
**片側だけの更新は `scripts/check-image-digests.js` の `[digest-mismatch]` が CI で止める**（それで正しい）。

1. 同じイメージの参照箇所を出す: `node scripts/check-image-digests.js --list`（製品ごとの参照箇所）。
2. 新しい digest は multi-arch の image index のものを使う（`docker buildx imagetools inspect <repo>:<tag>` の先頭の `Digest:`）。
3. 1 の全箇所の tag と digest を揃え、`node scripts/check-image-digests.js` が OK になることを確かめる。
4. 版の上げ方に注意が要るもの（設定の互換・データの移行）は、上流のリリースノートを人が読む（AI のレビューは bot の PR では走らない）。
   RabbitMQ の 3.13 系から 4.x への移行は、機能フラグとキューの種類の確認が要るので、通常の更新と分けて計画に載せる。
5. 稼働中のクラスタへの反映は、各部品の runbook の手順（オーナーが行う）に従う。本書はリポジトリの中の更新までを扱う。

## Dependabot が出さない更新（設定は `.github/dependabot.yml` の `docker`）

1 本の PR に大版と小版を混ぜて 7 製品を束ねた更新（tag の選び誤りを含む）を閉じた経緯から、次を出させない。**出ない更新は週次の走査の alert と、
下の §大版の移行の棚卸しで拾う。**

| 出させないもの | 理由 |
| --- | --- |
| 全製品の大版（semver の major） | 設定・規則・CRD の互換を、更新の PR の CI では確かめられない |
| collector の全部の更新 | 上流は `<版>-386` のように**数字だけのアーキテクチャ接尾辞**の tag を出し、Dependabot はそれを版の続きと読んで選んだ。Dependabot の docker では接尾辞の型で除く設定が無い（下の注） |
| Alertmanager の小版 | 0.x の製品で、小版でも互換が崩れうる |
| External Secrets Operator の小版 | helm chart（`scripts/lib/pinned-artifacts.sh`）と CRD を対で上げる |

- 注: `versioning-strategy` は docker では使えない。`ignore` の `versions` は Bundler の版の書式で、「末尾が `-386`」のような型は書けない。
  `-amd64`・`-arm64` などの英字を含む接尾辞は、閉じた PR で選ばれなかった（同じ版に在ったのに `-386` だけが選ばれた）。
  英字の接尾辞を別の型として扱っていると**推定**しているが、文書には書かれていない。collector を上げたら、PR の差分の tag が接尾辞の無い形であることを人が確かめる。
- 手で上げる・Dependabot の PR を受けるときの tag は、**接尾辞の無い multi-arch の tag**（`docker buildx imagetools inspect` の
  `MediaType` が image index で、`Platform` に `linux/amd64` が在る）に限る。

## 大版の移行（後続の作業。1 製品ずつ別の PR で行う）

どれも、上げる PR の中で次の項目を確かめるまで上げない。確かめ方が CI に無いものは、使い捨てのコンテナかクラスタで実測して作業仕様書に残す。

| 製品 | 今の版 → 候補 | 確かめること |
| --- | --- | --- |
| Prometheus | v2 系 → v3 系 | 警報・記録の規則が v3 で読めること（`promtool check rules`・`promtool test rules` を v3 の promtool で）。v3 で変わった既定（UTF-8 の名前・範囲の選択の境界・`le` / `quantile` の正規化など）で式の結果が変わらないこと。scrape の設定・remote write・TSDB の既存データの読み込み。Grafana の data source から引けること |
| Alertmanager | 0.27 → 0.3x | 設定（`amtool check-config`）・route・receiver（メール）・テンプレートが新しい版で読めること。受信の API の版。Prometheus からの送りが届くこと |
| collector（contrib） | 0.102 → 0.16x | 接尾辞の無い multi-arch の tag を選ぶこと。設定の component の改名・廃止（receiver・processor・exporter）、`service.telemetry` の書式、OTLP の受け口と認証、Loki・Tempo・Prometheus への送り先の exporter が新しい版に在ること。メモリの上限 |
| External Secrets Operator | 2.8 → 2.1x | image と helm chart の版を対で上げる（chart の sha256 を表へ）。CRD の版（`v1` / `v1beta1`）の扱いと、ClusterSecretStore・ExternalSecret が Ready / SecretSynced のままであること。chart の values の改名 |
| node（Job・中継のイメージ） | 22 → 24（LTS） | 25 などの LTS でない版は採らない。Job のスクリプトが新しい版で動くこと（`fetch`・TLS の既定）。`src/` の Dockerfile・helm values・`deploy/local`・`deploy/mail-relay` の写しも同じ PR で揃える |
| Keycloak | 26.7 → 26.8 | 小版だが上流の移行の手引きに破壊的変更が並ぶ（管理の REST API の audience の検査・protocol mapper の管理の権限・クライアント登録のポリシー・SMTP の StartTLS・refresh token の期限・ログイン画面の IdP のボタン など）。realm の宣言の取り込み・MCP クライアントの払い出し（管理用のクライアント）・匿名のクライアント登録のポリシー・メール・テーマ（`check-realm-constraints.js` の同梱テーマの表を新しい版の配布物から引き直す）・統合スタックの門を新しい版で通す |

## 関連

- 自製のイメージの走査: `.github/workflows/images.yml`（報告だけ）。同じ規則（結果を公開の面へ出さない）を当てるかは本書の範囲の外
- digest の固定の規則と更新の手順: [operations.md §インフラ製品の点検](operations.md#インフラ製品の点検基準-ad選定基準の計画-adr--1787)
