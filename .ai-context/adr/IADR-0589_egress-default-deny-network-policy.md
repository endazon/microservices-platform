---
title: IADR-0589 外向き通信（egress）は NetworkPolicy で既定拒否し、DNS は kube-dns だけ・宛先は名前空間 × Pod ラベル × ポートで 1 件ずつ許す（経路 B は段階的に当てる）
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0005, ADR-0008, IADR-0026, IADR-0461, IADR-0513, IADR-0520, IADR-0522, IADR-0573, IADR-0583]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md（§非LLM 外部送信の統制 §基本原則: egress は既定拒否・許可先を allowlist で管理する）
  - planning:projects/microservices-platform/07_adr/ADR-0005（サービスメッシュ）
  - planning:projects/microservices-platform/07_adr/ADR-0008（k3s 実行基盤）
related_specs:
  - ../specs/20261022_j4qv_egress-default-deny.md
---

# IADR-0589: 外向き通信（egress）の既定拒否（GHSA-j4qv-2w99-xg5p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: claude（形の選択。多層防御の強化の一環）

## 起点・関連

- 起点: GHSA-j4qv-2w99-xg5p（多層防御の強化。最小権限）。
- 計画: **08_data-egress-policy**（fixed）§基本原則「egress は既定拒否（default-deny）とし、許可先を allowlist で管理する」と §リスク・未決事項「default-deny ネットワークポリシーの実装確認」。本 IADR はその実装の段 1 であり、許す範囲を広げない。**ADR-0005**（メッシュ）・**ADR-0008**（k3s）。要求 **NFR-18**。
- 前提: [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md)（NetworkPolicy は多層防御として維持）・[IADR-0461](./IADR-0461_object-storage-seaweedfs-deployment.md) 決定 10・[IADR-0513](./IADR-0513_ast-kb-ingress-network-policy.md)・[IADR-0520](./IADR-0520_infra-product-default-egress-disabled.md)（製品の既定の外部通信は設定で止める。本 IADR はその下の網の層）・[IADR-0522](./IADR-0522_session-store-valkey-auth-required.md) 決定 4（経路 B の NetworkPolicy の強制は未実測と記録していた）・[IADR-0573](./IADR-0573_rabbitmq-per-service-users-and-ingress-policy.md)・[IADR-0583](./IADR-0583_pod-security-context-and-admission.md)（同じ GHSA の Pod 側）。

## コンテキストと課題

計画は egress の既定拒否と allowlist を求めている。チャートの egress の規則は ingress と同じ 1 枚（allow-intra-namespace）に同居し、`networkPolicy.enabled` の 1 つの knob に従っていた。経路 B（values-local）はこの knob を false にしている（ingress の NetworkPolicy を持たない理由は従前どおり）。egress の宛先は Pod ごとに違い、経路 B では基盤が別の名前空間（platform-infra）に居る。名前解決の宛先は名前空間と Pod で絞れる。

## 検討した選択肢

1. **Istio の `outboundTrafficPolicy: REGISTRY_ONLY` ＋ ServiceEntry** — サイドカーの無い Pod（経路 B の既定は ISTIO なし・Job・外部のイメージ）には効かず、アプリがサイドカーを迂回する通信も止まらない。L4 の網の層にならない。採らない（段 2 以降でクラスタの外の宛先を FQDN で絞るときの上乗せとしては残す）。
2. **ingress を含む `networkPolicy.enabled` を経路 B で true にする** — ingress の既定拒否が observability の収集・AST の名前空間からの呼び出し等に一度に当たり、1 PR で確かめ切れない。採らない。
3. **egress だけの組を独立させ、Pod の集合（workloads）を段ごとに広げる（採用）** — 本番像は従前どおり全 Pod、経路 B は確かめ切れた Pod から当てる。

## 決定

1. **egress の組を `templates/networkpolicy-egress.yaml` に置く。** 描く条件は `networkPolicy.enabled || networkPolicy.egress.enabled`。
   - `default-deny-egress`（Egress・規則なし）・`allow-egress-dns`・`allow-egress-intra-namespace`（同じ名前空間の Pod）・`allow-egress-istiod`（`mesh.enabled` のとき。既定 istio-system の `app: istiod` の 15012）・`allow-egress-to-<name>`（`destinations` の 1 件ごと）。
   - allow-intra-namespace は Ingress だけにする（egress は上の組へ移す）。本番像の許す範囲は、DNS を絞った分と istiod を足した分だけが変わる。
2. **名前解決は kube-dns の Pod の 53 番（UDP / TCP）だけへ許す**（`networkPolicy.egress.dns`。既定 kube-system の `k8s-app: kube-dns`）。部品 `msp.netpol.dnsEgressRule` を SeaweedFS の policy と共有する。空の namespace / podLabels は描画で止める。
3. **別の名前空間の宛先は「名前空間 × Pod ラベル × ポート」を同じ to の要素（AND）に置き、送り元（from）を app ラベルで限る。** 欠けた値（namespace・podLabels・ports・from）・重複・不正な name は描画で止める。クラスタの外の宛先は本 IADR の段 1 では描かない（既存の `apiServerEgress.cidrs` と同じ「宣言したときだけ ipBlock を描く」形で段 2 に足す）。
4. **本番像は名前空間の全 Pod に当てる。** `networkPolicy.enabled: true` のまま `egress.enabled: false` や workloads の絞り込みを与えると描画で止める（egress の制限を外す・狭める変更を黙って通さない）。両方 false のときだけ描かない（明示の退路）。サービス固有の 1 本（Vault・API サーバ・合成監視 → Keycloak）は従前の置き場・条件のまま。
   ingress を絞らない配備（`networkPolicy.enabled: false`）で egress だけを当てるときは workloads を必須にし、空なら描画で止める（全 Pod へ既定拒否を黙って描くと、宛先を宣言していない Pod がまとめて止まる）。
5. **経路 B は段ごとに Pod を広げる。** 段 1 はクラスタの外へ出ない 14 の Pod（.NET の 12 サービス・frontend・SeaweedFS）と確かめ用の `egress-probe`。宛先は platform-infra の postgres・rabbitmq・keycloak・otel-collector・qdrant の 5 件。段 2・段 3 の範囲は作業仕様書。
6. **試験は描画を k8s の評価規則で通信の一覧へ当てる**（`scripts/helm-egress-network-policy.test.js`）。描画される全 Pod が段 1 / 段 2 のどちらかに分類されていること（新しい Pod が黙って制限の外に残らない）と、宛先の送り元が env の宣言（DB・ブローカ・OTLP・Qdrant の appsettings）と一致することを見る。稼働のクラスタでの強制は統合スタックの門が対照つきで測る（対照の Pod が基盤へ届かなければ判定できないとして赤）。

## 理由

- 網の層（NetworkPolicy）はサイドカーの有無・アプリの実装に依らず、Pod の IP で効く。
- k3s（v1.35.4。起動器の既定と同じ）の内蔵のコントローラが egress の NetworkPolicy を強制し、ClusterIP の Service 名で引いた宛先にも Pod の selector が効くことを実測した（作業仕様書 §NetworkPolicy を強制するか）。
- 経路 B を段に分けるのは、確かめ切れない Pod へ当てて PoC を止めないため。段 1 の宛先は描画の env と基盤の実体に機械で結び付けたので、漏れは試験で落ちる。

## 結果

- 本番像: 全 Pod の egress は DNS（kube-dns）・同じ名前空間・istiod と、サービス固有の 1 本だけ。基盤が別の名前空間に居る配備は `destinations` を宣言しないと届かない（fail-closed。段 3）。
- 経路 B: 段 1 の Pod の egress は DNS・同じ名前空間・宣言した基盤の宛先だけ。段 2 の Pod は変わらない。
- 統合スタック（develop への push・定期・手動）で、強制が効いていることを毎回測る。

## 残余

1. 段 2・段 3 は未着手（作業仕様書 §段の分け方）。経路 B の bff・llmgateway・datasource・wiki-js・Job は本 IADR では制限しない。
2. 新しい Pod の IP へ規則が入るまでの窓（k3s で実測して数秒）は NetworkPolicy の実装に依り、本変更では閉じない。
3. NetworkPolicy を強制しないクラスタでは宣言が残るだけになる。経路 B は統合スタックの門が赤で知らせる。本番像のクラスタの CNI は配備先で確かめる。
4. NodeLocal DNSCache 等で名前解決の宛先が kube-dns と違う配備は `networkPolicy.egress.dns` を上書きする（しなければ名前解決が落ちる＝見える壊れ方）。
5. 計画 08_data-egress-policy §リスク・未決事項の「default-deny ネットワークポリシーの実装確認」は段 1 の範囲で進んだ。段 2・3 の完了後に計画へ環流する。
