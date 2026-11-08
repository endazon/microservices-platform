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
iadrs: [IADR-0708, IADR-0514, IADR-0584, IADR-0232]
specs: [20261108_ci-review-perms-image-scan]
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
| Dependabot（`.github/dependabot.yml` の `docker`） | 週次 | `deploy/cloud/**` の `image:` の新しい版を 1 本の PR にまとめて出す | 下の §更新の PR を仕上げる |

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

## 関連

- 自製のイメージの走査: `.github/workflows/images.yml`（報告だけ）。同じ規則（結果を公開の面へ出さない）を当てるかは本書の範囲の外
- digest の固定の規則と更新の手順: [operations.md §インフラ製品の点検](operations.md#インフラ製品の点検基準-ad選定基準の計画-adr--1787)
