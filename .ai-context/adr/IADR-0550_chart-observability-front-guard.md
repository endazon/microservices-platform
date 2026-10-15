---
title: IADR-0550 描画結果に Loki・Tempo が現れたら、前段の認証（同じ Pod の Secret を受けるリバースプロキシ・製品の loopback 待ち・Service の宛先）と、名指しの NetworkPolicy（Pod を選ぶ全 NetworkPolicy の from が名指し）の両方を check-deploy-manifests.js で要求する。chart は対象なしで通り、経路 B が陽性対照になる
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0133, ADR-0112, ADR-0107, IADR-0526, IADR-0520, IADR-0240, IADR-0461]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0133（インフラ製品の管理用の口。決定 3）
related_specs:
  - ../specs/20261016_1867_chart-observability-front-guard.md
---

# IADR-0550: chart へ Loki・Tempo を足すときの前段と到達の制限を、描画結果の検査で強制する（#1867）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-16
- 決定者: claude（オーナーの依頼 #1867）

## 起点・関連

- 起点 issue: #1867（#1865 の独立監査 🟡-1。#1842 の後継追跡）。
- 計画 ADR: ADR-0133 決定 1 の条件 1・2、決定 3（前段が無いまま本番像へ足さない）。ADR-0112。
- 実装 ADR: IADR-0526（経路 B の 2 段）・IADR-0520（描画結果の製品の判定）・IADR-0240（描画とスキーマの検査）・IADR-0461 決定 10（allow-intra-namespace から外した前例）。

## コンテキストと課題

本番像の chart には Loki・Tempo が無く、ADR-0133 決定 3 を守る手段が人の注意しか無かった。検査を置くなら、
「前段がある」「到達の制限がある」を**名前ではなく形で**判定しなければならない（`gate` という名前のコンテナや
`loki-ingress` という名前の NetworkPolicy があっても、認証しないプロキシ・誰からでも届く規則なら 2 段にならない）。
加えて chart には同じ名前空間の全 Pod から届く `allow-intra-namespace` があり、名指しの NetworkPolicy を足すだけでは許可の和で開いたままになる。

## 検討した選択肢

1. **`check-deploy-manifests.js` の描画結果への判定に足す（採用）**。chart の既定と `ci/*.yaml` の全派生・全 overlay を既に描いており、CI のステップも helm も揃っている。
2. 新しい試験（`helm-*.test.js`）を置く。描画の列挙を二重に持つことになり、`ci/*.yaml` が増えたときに片側を取りこぼす（要点 1 の事故の型）。
3. 名前（コンテナ `gate`・NetworkPolicy の存在）で判定する。issue の要求「名前だけで通る形にしない」に反する。

## 決定

1. **対象**: Pod を持つ資源のうち、コンテナのイメージの最後のセグメント（tag・digest を除く）が `loki` / `tempo` / `grafana-loki` / `grafana-tempo` のもの。対象が無い描画は何も言わない（chart の現状）。
2. **判定**（`scripts/lib/observability-front-guard.js`。詳細は作業仕様書）:
   - 条件 1: 同じ Pod に、イメージがリバースプロキシ（caddy / nginx / nginx-unprivileged / envoy / oauth2-proxy / haproxy / traefik）で、口を持ち、
     **Secret から資格情報を受ける**（optional でない）前段がある。製品の HTTP・gRPC が `-config.file` の設定とフラグで loopback 待ち（読めなければ失敗）。
     製品のコンテナは口を宣言しない、Pod を選ぶ Service の宛先は前段の口（いずれも Tempo の OTLP 4317 / 4318 だけ例外）。hostNetwork は失敗。
   - 条件 2: Pod を**名指しで**（matchLabels か In）選ぶ Ingress の NetworkPolicy があり、Pod を選ぶ Ingress の NetworkPolicy は**全部**、規則ごとに名指しの from を持つ
     （ipBlock・`{}`・namespaceSelector だけ、は広い）。**chart へ足すときは allow-intra-namespace の NotIn に製品の app を足す**ことになる（IADR-0461 決定 10 の SeaweedFS と同じ）。
3. **形は経路 B（サイドカー型）の一般化に限る**。前段を別の Deployment に置く等の別の形の「同等」は赤になる（fail-closed）。その形を採る PR が判定を広げ、自己試験を足す。
4. **overlay にも当て、走査全体で Loki・Tempo が 1 度も判定されなければ失敗**にする。経路 B が実物の陽性対照になり、イメージの識別が壊れて 0 件になったときに緑を返さない（要点 2 と同じ）。
5. 描画結果の「プロセスから見える設定」の組み立て（ConfigMap → volume → volumeMount）は `lib/product-egress-defaults.js` から `containerView` として切り出して共用する（挙動は変えない）。

## 理由

- 形で判定することで、ADR-0112 の「1 段の防御では設定の誤りひとつで開く」に対し、2 段のどちらが欠けても（または名前だけ置いても）赤になる。
- 既存の検査器の延長なので、描画の列挙・ツールの fail-closed・CI の配線を新たに持たない。

## 結果

- chart へ Loki・Tempo を前段なし・NetworkPolicy なしで足す PR は `static-checks-units` の `Check charts and overlays render` で止まる。
- 前段の認証の**中身**（道の列挙・トークンの強さ）は本検査では見ない。経路 B は `scripts/scripts.repo.test.js`（#1842）が見ており、chart へ足すときは同等の突き合わせをその PR で置く。
- 前段のプロキシ製品の一覧に無い製品（例: 自製の認証プロキシ）を使うときは一覧を広げる必要がある（認証を掛けられる製品に限る）。

## 関連

- 作業仕様書: [20261016_1867_chart-observability-front-guard](../specs/20261016_1867_chart-observability-front-guard.md)
- [IADR-0526](./IADR-0526_loki-tempo-front-auth-and-networkpolicy.md)
