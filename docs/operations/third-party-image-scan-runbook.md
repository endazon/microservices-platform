---
title: 運用 Runbook — 配備する第三者のイメージの脆弱性の走査（週次）と更新の通知（Dependabot）の扱い
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
> **状態を `draft` に留めている理由**: 週次の走査が develop でまだ 1 回も走っていない（最初の赤の扱いを記録していない）。

## 何が動くか

| 仕組み | いつ | 何をする | 赤・PR になったら |
| --- | --- | --- | --- |
| `Third-party Image Scan`（`.github/workflows/third-party-image-scan.yml`） | 週次（火曜 04:17 UTC）・手動（workflow_dispatch） | `node scripts/check-image-digests.js --scan-targets` の全イメージを Trivy で走査する。修正版のある CRITICAL / HIGH が 1 件でもあるイメージの脚を落とす。結果は Security タブ（code scanning）へ載る | 失敗の issue が自動で立つ（同じワークフローの失敗は 1 本へ集まる）。下の §赤になったら |
| 同上（pull_request） | `deploy/**`・対象の選び方・本ワークフロー・受け入れのファイルを触る PR | 件数を注記するだけで落とさない（既存の古い版で関係の無い PR を赤くしない）。**必須チェックではない** | 注記が出たら、その PR が上げた版で増えていないかを見る |
| Dependabot（`.github/dependabot.yml` の `docker`） | 週次 | `deploy/cloud/**` の `image:` の新しい版を 1 本の PR にまとめて出す | 下の §更新の PR を仕上げる |

- ログには件数だけを出す。脆弱性の一覧は Security タブ → Code scanning（カテゴリ `third-party-image/<イメージ>`）で見る。
  見られるのは書き込みの権限を持つ人だけである。
- 走査の対象は `deploy/` の参照だけ（`src/` の基底イメージ・Testcontainers は対象外）。対象の一覧は手元で
  `node scripts/check-image-digests.js --scan-targets` で出せる。

## 赤になったら（週次・手動の走査）

判断の基準は **修正版がある CRITICAL / HIGH を、次の週次の走査までに 0 件にする**こと。
次の 1〜3 のどれかを選ぶ。

1. **上げる（既定）**: 修正の入った版の tag と digest に上げる。§更新の PR を仕上げるの手順で、同じイメージの参照を全部揃える。
   Dependabot の PR が既に在れば、それを仕上げる。
2. **届かないので受け入れる**: その脆弱性の在る部品をこの配備で使わない・外から届かない、と説明できるときだけ。
   `.github/image-scan.trivyignore.yaml` に 1 件ずつ足す。
   ```yaml
   vulnerabilities:
     - id: CVE-2026-12345
       statement: どの部品で、なぜこの配備では届かないか。追跡の issue
       expired_at: 2026-12-31
   ```
   - `statement` と `expired_at`（今日から 90 日以内）は必須（`scripts/scripts.repo.test.js` が落とす）。期限が来ると再び赤になる。
   - 書いてよいのは公開の CVE だけである。非公開のアドバイザリの ID を書かない。
3. **上げられない**（メジャーの移行が要る等）: 追跡の issue を立て、2 の受け入れに期限つきで載せ、移行を計画へ載せる。

- 走査そのものの失敗（「走査の結果が無い」）は合格ではない。レジストリの一時的な失敗なら、手動で再実行する。
  続くならイメージの参照（digest の実在）を `docker buildx imagetools inspect <ref>` で確かめる。
- 自動の issue は、直したあとの週次の走査が緑になったら閉じる。

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

- 自製のイメージの走査: `.github/workflows/images.yml`（報告だけ）
- digest の固定の規則と更新の手順: [operations.md §インフラ製品の点検](operations.md#インフラ製品の点検基準-ad選定基準の計画-adr--1787)
