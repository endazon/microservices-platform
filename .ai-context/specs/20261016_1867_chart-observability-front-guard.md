---
title: 作業仕様書 — 本番像の chart へ Loki・Tempo を足すとき、前段の認証と到達の制限（NetworkPolicy）の両方を描画結果の検査で要求する
type: spec
status: done
related_ids: [NFR-18, ADR-0133, ADR-0112, ADR-0107, IADR-0526, IADR-0520, IADR-0240, IADR-0461, IADR-0550]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0133（インフラ製品の管理用の口。決定 3「前段が無いまま本番像へ足さない」）
---

# 作業仕様書 — chart の Loki・Tempo に前段と NetworkPolicy を要求する検査（#1867）

> 本仕様書は着手日（2026-10-16）に、判定の形を経路 B の実物で確かめながら作成した。基点は MSP `origin/develop` `45503af2`。起点は #1867（#1865 の独立監査 🟡-1。#1842 の後継追跡）。

## 起点（トレーサビリティ）

- 要求: **NFR-18**（セキュリティ）。計画 ADR: **ADR-0133** 決定 1 の条件 1（前段の認証）・条件 2（到達の制限）・決定 3（前段が無いまま本番像へ足さない）。ADR-0112（1 段の防御では設定の誤りひとつで開く）。
- 実装 ADR: IADR-0526（経路 B の 2 段。#1842 / PR #1865）・IADR-0520（描画結果の製品の判定。`lib/product-egress-defaults.js`）・IADR-0240（`check-deploy-manifests.js`）・IADR-0461 決定 10（chart の allow-intra-namespace から SeaweedFS を外した前例）。新しい決定は **IADR-0550**。

## 問題

- 本番像の chart（`deploy/helm`）には Loki・Tempo が無い。ADR-0133 決定 3 は「前段が無いまま本番像へ足さない」と定めるが、守る手段は人の注意だけで、#1842 は close されたため記録の上でしか追跡が残らない。

## 着手前の実測

- `node scripts/check-deploy-manifests.js`（helm v3.16.3・kubectl v1.31.0・kubeconform v0.8.0）: chart 1 件・overlay 19 件、緑。
- 描画結果で Loki・Tempo（`grafana/loki`・`grafana/tempo`）が現れるのは overlay `deploy/local/observability` と `deploy/local/observability-persistence` だけ。chart は既定・`ci/*.yaml` 3 派生のいずれにも現れない。
- 経路 B の形（IADR-0526）: 製品は `-config.file` の `server.http_listen_address` / `grpc_listen_address` を `127.0.0.1` にし、同じ Pod の Caddy（コンテナ名 `gate`）が Service の口を持ち、トークンを Secret `observability-gate` から env の `secretKeyRef` で受ける。製品のコンテナは口を宣言しない（Tempo の OTLP 4317 だけ例外）。NetworkPolicy `loki-ingress` / `tempo-ingress` が `matchLabels: {app: loki|tempo}` で名指しし、from は `app: otel-collector` / `app: grafana` の podSelector。
- chart の NetworkPolicy: `default-deny-ingress`（`podSelector: {}`・規則なし）と `allow-intra-namespace`（`app NotIn [seaweedfs]` を選び、from `podSelector: {}`＝同じ名前空間の全 Pod）。**chart へ Loki を足すと allow-intra-namespace が Loki の Pod を選び、名指しの NetworkPolicy を足しても許可の和で同じ名前空間の全 Pod から届く**（SeaweedFS が IADR-0461 決定 10 で踏んだのと同じ形）。
- `yaml-subset.js` は chart の描画（72 文書）と両 overlay の描画を読める。

## 母集合（規則 9・10）

- 誤りの側の文字列（`loki` / `tempo` のイメージ）で `deploy/` の描画結果を全部引いた（上記）。compose（経路 A・0 段）は ADR-0133 / IADR-0526 が 0 段として受け入れた経路で、本検査の対象外（描画結果ではない）。
- 本件で新たに誤りになる記述: `docs/security/security.md` の「本番像（helm chart）」の行と「機械の検査」の箇条（検査が無い前提）、`scripts/README.md` の `check-deploy-manifests.js` の行（自己試験の件数）。いずれも同じ PR で直す。

## 方式

- 新しい仕組みは作らない。`check-deploy-manifests.js` の描画結果への判定（要点 5・6 と同じ箇所）に要点 7 として足し、判定は `scripts/lib/observability-front-guard.js` に置く。CI は既存の `static-checks-units` の `Check charts and overlays render` / `Self-test deploy manifest checker` がそのまま走らせる（ステップは足さない。ワークフローのコメントだけ改める）。
- 対象は chart の既定と `ci/*.yaml` の全派生、**および全 overlay**。overlay も見るのは、経路 B を実物の陽性対照にするため（走査全体で Loki・Tempo が 1 度も判定されなければ失敗。要点 2 と同じ「0 件で緑を返さない」）。

### 判定（名前では通さない）

対象: Pod を持つ資源のうち、コンテナのイメージの最後のセグメント（tag・digest を除く）が `loki` / `tempo` / `grafana-loki` / `grafana-tempo` のもの（ミラーのレジストリも拾う。`promtail` 等は拾わない）。

条件 1（前段）— 全部:
1. 同じ Pod に製品以外のリバースプロキシのコンテナ（イメージの最後のセグメントが caddy / nginx / nginx-unprivileged / envoy / oauth2-proxy / haproxy / traefik）がある。**コンテナ名は見ない**（`gate` という名前の busybox は前段と数えない）。
2. その前段が口（containerPort）を持ち、**Secret から資格情報を受け取る**（env の secretKeyRef・envFrom の secretRef・Secret の volume。optional は数えない）。認証しない素通しのプロキシを前段と数えないため。
3. 製品の HTTP・gRPC が loopback だけで待つ（`-config.file` が指すマウントされたファイルの `server.http_listen_address` / `server.grpc_listen_address`、同名フラグの上書き）。読めなければ失敗。hostNetwork は失敗。前段があっても製品が 0.0.0.0 で待てば Pod の IP へ直接届くため。
4. 製品のコンテナが口を宣言しない（例外は Tempo の OTLP 4317 / 4318）。
5. Pod を選ぶ Service の各口の targetPort が前段の口（例外は 4 と同じ）。

条件 2（到達の制限）— 全部:
1. Pod を**名指しで**選ぶ Ingress の NetworkPolicy がある（podSelector の matchLabels か `In`）。`podSelector: {}` の既定拒否や NotIn だけで結果として選ぶものは数えない。
2. Pod を選ぶ Ingress の NetworkPolicy は**全部**、規則ごとに from を持ち、各要素が名指し（podSelector が matchLabels か In。namespaceSelector を併せるならそれも名指し）。ipBlock・`{}`・namespaceSelector だけ、は広い。NetworkPolicy は許可の和なので、1 枚でも広ければ開く。

この形は経路 B（サイドカー型）をそのまま一般化したもの。前段を別の Deployment に置く等の別の形で「同等」を作るときは、本判定が赤を返す（fail-closed）ので、その PR で判定を広げる（IADR-0550 決定 3）。

## 受け入れ基準と試験の写像

| 受け入れ基準 | 試験（`node scripts/check-deploy-manifests.js --self-test`） |
| --- | --- |
| 現行 chart（既定と `ci/*.yaml` 全派生）は「対象なし」で緑 | `#1867 実物の chart: …「対象なし」（陰性対照）…` |
| Loki を足して前段も NetworkPolicy も無い → 赤 / 前段だけ → 赤 / NetworkPolicy だけ → 赤 / 両方 → 緑。Tempo も同様 | 合成の 4 通り × 2 製品（`#1867 loki: …` / `#1867 tempo: …`）と、実物の chart の描画へ足した 4 通り（両方でも allow-intra-namespace が選ぶ限り赤、NotIn へ足せば緑） |
| 名前だけで通らない | Secret を受けない前段・0.0.0.0 待ち・Service の宛先が製品・from `{}`・from ipBlock・from 無し・名前だけ `gate` の busybox・名指しでない既定拒否だけ、がそれぞれ赤 |
| 経路 B は緑で、壊すと赤 | `#1867 実物の経路 B …`（NetworkPolicy を外す・前段のイメージを替える） |
| CI へ配線 | 既存の `static-checks-units` の 2 ステップ（helm / kubectl / kubeconform 導入済み） |

変異（判定器側）: 前段の口・Secret の要件／loopback の判定／from の広さ／名指しの要件／Service の宛先／from 無し／プロキシのイメージの要件を 1 つずつ外すと、自己試験が赤になることを確かめた。端から端の変異: chart の templates へ前段の無い Loki の Deployment を足すと本走査が exit 1（条件 1・2 の失敗を列挙）。

## PoC 手順

不要（CI の検査だけ。配備物は変えない）。

## 計画への環流

不要（ADR-0133 決定 3 の機械化。計画の記述は変えない）。
