---
title: IADR-0590 経路B でメッシュを入れたときの mTLS の既定を STRICT にする（引き継いだ PERMISSIVE は上げずに告げ、受信の例外はバックチャネルの 1 本に限り、稼働の効きを統合スタックの門で測る）
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0005, ADR-0021, IADR-0026, IADR-0307, IADR-0317, IADR-0377, IADR-0487, IADR-0488, IADR-0583, IADR-0589]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005（サービスメッシュ。サービス間通信を mTLS で徹底する）
  - planning:projects/microservices-platform/07_adr/ADR-0021（エッジ＝Istio Ingress Gateway。入口の境界で平文を許す緩和は不要）
related_specs:
  - ../specs/20261023_j4qv_mesh-mtls-strict-default.md
---

# IADR-0590: 経路B のメッシュの mTLS の既定を STRICT にする（GHSA-j4qv-2w99-xg5p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（形の選択。多層防御の強化の一環）

## 起点・関連

- 起点: GHSA-j4qv-2w99-xg5p（多層防御の強化。既定を fail-closed にする）。
- 計画: **ADR-0005**（サービス間通信を mTLS で徹底する）・**ADR-0021**（入口はメッシュの Envoy。入口の境界のための PERMISSIVE 化は不要）。要求 **NFR-18**（同じ GHSA の他の変更と同じ起点）。
- 前提: [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md)（STRICT mTLS を第一防御とする）・[IADR-0307](./IADR-0307_istio-optin-and-staged-mtls.md) 決定 4（経路B の既定は PERMISSIVE。本 IADR が改める）・[IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)（入口を Envoy へ移す）・[IADR-0377](./IADR-0377_mesh-mtls-single-writer-and-drift-gate.md) 決定 2（STRICT は入口を移した後）・決定 3（経路B の既定は PERMISSIVE のまま。本 IADR が改める）・決定 4（AST↔MSP の扱い）・[IADR-0487](./IADR-0487_mesh-mtls-mode-inherit-on-rerun-fail-closed.md)（未指定の再実行は現行を引き継ぐ）・[IADR-0488](./IADR-0488_istio-tristate-inherit-mesh-enabled-on-rerun.md)（`ISTIO` の引き継ぎ）。同じ GHSA の段: [IADR-0583](./IADR-0583_pod-security-context-and-admission.md)・[IADR-0589](./IADR-0589_egress-default-deny-network-policy.md)。

## コンテキストと課題

本番像（`values.yaml`）の `mesh.mtlsMode` は STRICT である。経路B の起動器（`scripts/k8s-local-up.sh`）は `ISTIO=1` の初回を PERMISSIVE で宣言し、STRICT は `ISTIO_MTLS_MODE=STRICT` の明示でだけ入っていた（IADR-0307 決定 4・IADR-0377 決定 3）。統合スタック（`integration-stack.yml`）も `ISTIO_MTLS_MODE` を与えないので、メッシュの門（G12）は PERMISSIVE の宣言と稼働の一致だけを測っていた。

PERMISSIVE の理由は 2 つだった。(1) 入口がメッシュ外の Traefik だと STRICT で 502 になる —— IADR-0317 / IADR-0377 決定 2 が「入口を移した後で STRICT へ上げる」段取りとして解いた。(2) AST の名前空間（サイドカー無し）から MSP への平文が STRICT で届かない —— IADR-0377 決定 4 は「恒久像は AST をメッシュへ入れること。MSP 側で平文を許すことではない」と決めている。AST 側には注入の設定点（AST の `mesh.sidecarInjection.*`）がある。

## 検討した選択肢

1. **据え置き（初回 PERMISSIVE・STRICT は opt-in）** — 新しく立てるクラスタも統合スタックも PERMISSIVE で動き続け、STRICT が稼働で測られない。採らない。
2. **AST の呼び出し先（LLM ゲートウェイ・文書・検索・MCP）に受信の例外を開ける**（ワークロード ＋ `portLevelMtls` ＋ `AuthorizationPolicy`） — 平文には principal が無いので、送り手を AST に絞れない（L4 の送り元の絞り込みは経路B では ingress の NetworkPolicy を描いていない）。開ける口が価値の高いサービスそのものになる。IADR-0377 決定 4 の「MSP 側で平文を許さない」にも反する。採らない。
3. **既定を STRICT にし、引き継いだ PERMISSIVE は上げずに毎回告げ、AST の前提は告げる（採用）。**
4. **引き継いだ PERMISSIVE も再実行で STRICT へ上げる** — 稼働の PoC が AST をメッシュへ入れる前に再実行すると、AST→MSP が黙って止まる。#1710（黙って下げる）の逆向きを作る。採らない。

## 決定

1. **`ISTIO=1` の初回（`msp` リリースが無い・メッシュ未宣言）の既定を STRICT にする。** 選び方は従前どおり 明示（`ISTIO_MTLS_MODE`）＞ 現行（`helm get values msp`）＞ 初回の既定 で、初回の既定だけを PERMISSIVE から STRICT へ変える。決めた値は `istio-edge-up.sh` [5/5] へ同じ変数で渡る。
   - `LOCALEDGE=1` の初回は IADR-0377 決定 2 の段取りのまま（`[6/7]` は PERMISSIVE、入口を Envoy へ移した後に STRICT）。
   - `ISTIO=1` 単独の初回は `[6/7]` が STRICT を宣言する。注入ラベルを helm より前に貼るので Pod は最初からサイドカー付きで生まれる。入口は port-forward のままで、port-forward は STRICT でも届く（実測。下記）。単独時の WARN の文言を「STRICT へ上げる前に」から「ブラウザのエッジ経路が要るなら」へ直す。
2. **引き継いだ PERMISSIVE は上げない。ただし再実行のたびに WARN で告げる**（上げ方の 1 行を添える）。緩める・緩めたままにする判断は明示（`ISTIO_MTLS_MODE=PERMISSIVE`）または引き継ぎに残る。
3. **受信の例外はバックチャネルログアウトの 1 本だけのまま**（`mesh.backchannelLogout.fromOutsideMesh`。BFF の受信ポートだけ PERMISSIVE ＋ principal の無い要求はその URI 以外 DENY）。新しい例外は足さない。
4. **AST の前提は止めずに告げる。** STRICT のとき、AST の名前空間に Pod が居て注入ラベルが無ければ WARN を出す。AST をメッシュへ入れる手順は `deploy/istio/README.md`「STRICT の前提」に置く。
5. **稼働の効きを統合スタックの門で測る。** `ISTIO=1` の実行で、(a) 名前空間全体の PeerAuthentication が STRICT、(b) サイドカーの無い Pod（同じ名前空間・別の名前空間）から平文の HTTP が届かない、(c) 対照としてサイドカー付きの Pod からは届く、を要求する。G12 は宣言と稼働の一致を見る門で、宣言が PERMISSIVE でも緑になるため、別に置く。

## 壊れ得る経路の洗い出し

| 呼び出し元 → MSP の Pod | STRICT での扱い | 対処 |
| --- | --- | --- |
| Istio Ingress Gateway | メッシュの中 | 不要 |
| `kube-system` の Traefik（入口を移していない間） | 502 | IADR-0377 決定 2 の段取り（入口を移した後で STRICT） |
| `kubectl port-forward`（`ISTIO=1` 単独の入口・投入器 ABACSEED / SEARCHSEED / TAGSEED） | 届く | 不要（実測） |
| kubelet の HTTP プローブ | 届く（pilot-agent 経由へ書き換え） | 不要（実測。readiness が STRICT 下で Ready） |
| MSP の名前空間の Job / Deployment（ドリフト検出の Job・synthetic-monitor） | 注入される | 不要（ドリフト検出の Job は `mesh.enabled` で注入を明示済み） |
| `platform-infra` の認可サーバ（バックチャネルログアウト） | 例外の 1 本だけ届く | 既存の 2 枚組（決定 3） |
| `ai-stock-trading`（サイドカー無し） | 届かない | AST をメッシュへ入れる（決定 4）。入れるまでは PERMISSIVE の明示 |
| observability の Prometheus | MSP の Pod を scrape しない（otel-collector だけ） | 不要 |
| MSP → `platform-infra` / AST（出る側） | auto-mTLS が平文で出す | 不要（STRICT は受信の規則） |
| 注入前の Pod（メッシュ無しのクラスタへ `ISTIO=1` を足した再実行） | rollout restart の間だけ落ちる | 作り直しで消える（結果に記録） |
| `wiki-js` の Ingress（`wikijs.ingress.enabled`。既定 false） | メッシュ外のコントローラなら届かない | 既定で描かない。opt-in するならエッジ（Gateway）経由にする |

## 理由

- 計画 ADR-0005 はサービス間 mTLS の徹底を求め、ADR-0021 は入口の境界のための PERMISSIVE 化を不要と定めている。経路B の既定だけが PERMISSIVE に残っていた理由（入口・AST）のうち入口は解消済みで、AST は MSP 側で平文を許さないと決めてある（IADR-0377 決定 4）。
- 既定を変えても、稼働のクラスタは引き継ぎ（IADR-0487）により黙って変わらない。変わるのは新しく立てるクラスタと統合スタックで、そこで STRICT が門として測られる。
- 引き継いだ PERMISSIVE を上げないのは、AST の前提を満たす前の再実行で取引系の呼び出しを黙って止めないためである。代わりに毎回告げる。

## 実測（2026-10-23。k3s v1.35.4+k3s1 ＋ Istio 1.30.4。起動器の既定の版）

- 注入した受け手（`/health/live` を返す最小の HTTP）と、チャートの `templates/istio-mtls.yaml` を values-local ＋ `mesh.enabled=true` ＋ `mesh.mtlsMode=STRICT` ＋ `mesh.backchannelLogout.fromOutsideMesh=true` で描いたもの（4 種）を当てた。
- STRICT: サイドカー付きの Pod から reached、同じ名前空間のサイドカーの無い Pod から refused、別の名前空間（default・`ai-stock-trading`）のサイドカーの無い Pod から refused。`nc -z` は open（接続は受けてから切る）。`kubectl port-forward svc/…` は 200。readiness（HTTP）は Ready。
- PERMISSIVE へ変えると 3 つとも reached になり、門は赤（exit 1）。
- `ai-stock-trading` に注入ラベルを貼ると、その名前空間の Pod（`istio-proxy` を持つ）から reached。

## 結果

- 良い影響: 経路B でメッシュを入れたときの既定が本番像と揃う。統合スタックが STRICT の効きを毎回測る。
- 悪い影響・トレードオフ:
  - メッシュ無しのクラスタへ `ISTIO=1` を足した再実行では、注入の rollout restart の間に限り、注入前の Pod から注入後の Pod への平文が落ちる（作り直しで消える）。
  - AST をメッシュへ入れるまで、STRICT の経路B では AST→MSP が届かない（WARN で告げる）。
- フォローアップ:
  1. AST の配備スクリプトが `mesh.sidecarInjection.enabled` を再実行で引き継げるようにする（AST リポジトリ側。今は値の受け口が無く、手で与えた値が次の配備で外れる）。
  2. 稼働の PoC を STRICT へ上げるのは 1 の後。手順は `deploy/istio/README.md`「STRICT の前提」。
  3. 基盤インフラ（platform-infra）側の通信の暗号化は別の段（本 IADR の範囲外）。
