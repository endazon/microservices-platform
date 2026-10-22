---
title: 作業仕様書 — 経路B のメッシュの mTLS を既定で STRICT にする（GHSA-j4qv-2w99-xg5p）
type: spec
status: done
related_ids: [NFR-18, ADR-0005, ADR-0021, IADR-0590, IADR-0026, IADR-0307, IADR-0317, IADR-0377, IADR-0487, IADR-0488, IADR-0589]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005（サービスメッシュ。サービス間通信を mTLS で徹底する）
  - planning:projects/microservices-platform/07_adr/ADR-0021（エッジ＝Istio Ingress Gateway）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — 経路B のメッシュの mTLS を既定で STRICT にする（GHSA-j4qv-2w99-xg5p）

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0590**（新規）に置く。
> 多層防御の強化（既定を fail-closed にする）。参照は GHSA の ID だけとする。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0005**（Accepted。サービス間通信を mTLS で徹底する）・**ADR-0021**（Accepted。入口はメッシュの Envoy。入口の境界のための PERMISSIVE 化は不要）。計画との矛盾は無い（本変更は既定を計画の側へ寄せる）。
- 非機能要求: **NFR-18**（同じ GHSA の他の変更と同じ起点）。
- 実装 ADR: **IADR-0590**（新設）。改める決定: IADR-0307 決定 4・IADR-0377 決定 3（いずれも経路B の既定 PERMISSIVE）。前提: IADR-0026・IADR-0317・IADR-0377 決定 2 / 4・IADR-0487・IADR-0488。

## 範囲

- 本 PR（段 4a）: **サービスメッシュの mTLS**（MSP の名前空間の Pod が受ける側）を経路B の既定で STRICT にする。
- 範囲外: 基盤インフラ（platform-infra の postgres・rabbitmq・keycloak 等）との通信の TLS（別の段）。AST の配備スクリプト（別リポジトリ）。

## 要件

1. `ISTIO=1` の初回（リリースが無い・メッシュ未宣言）は `mesh.mtlsMode=STRICT` を宣言する。`LOCALEDGE=1` の初回は入口を Envoy へ移した後で STRICT へ上げる（段取りは従前どおり）。
2. 明示（`ISTIO_MTLS_MODE`）と引き継ぎ（`helm get values msp`）の優先は変えない。引き継いだ PERMISSIVE は上げないが、毎回 WARN で告げる。
3. 受信の例外を増やさない（バックチャネルログアウトの 1 本だけ）。
4. STRICT のとき、AST の名前空間に Pod が居て注入ラベルが無ければ WARN を出す（止めない）。
5. 統合スタックの `ISTIO=1` の実行で、STRICT が稼働で効いていること（平文が届かない・サイドカー付きなら届く）を門で測る。
6. 本番像（`values.yaml` の `mtlsMode: STRICT`）は変えない。

## 母集合（規則 9・10。着手前に引いた）

- 書き手: `mesh.mtlsMode` を書くのは helm だけ（`templates/istio-mtls.yaml`。`set_mesh_mtls_mode` と起動器の `[6/7]`）。`git grep -n "mtlsMode\|ISTIO_MTLS_MODE"` で起動器・`istio-edge-up.sh` / `istio-edge-down.sh`・`lib/mesh-mtls-mode.sh`・`check-stack-ready.js`（G12）・試験を引いた。既定の値を持つのは起動器の `[6/7]` の 1 か所（`${ISTIO_MTLS_MODE:-PERMISSIVE}`）と、その説明の注記・文書。
- 「既定 PERMISSIVE」を書いている文書: `git grep -n "PERMISSIVE" -- deploy docs README.md scripts/README.md` で引いた。直すのは `deploy/istio/README.md`・`deploy/local/README.md`・`deploy/local/edge-istio/README.md`。`docs/operations/east-west-grpc-h2c-roundtrip-measurement-runbook.md` は両モードで測る手順で、既定に依らない（除外）。`docs/security/security.md`・`docs/how-to/obsidian-plugin-install.md` は「STRICT（既定）」を本番像について書いており、正しいまま（除外）。
- 試験: `scripts/k8s-local-up.test.js` の「初回は PERMISSIVE」を期待する 2 か所（#1710 の初回・#1722 の CI の形）。
- 受ける側の経路（壊れ得る経路）: `git grep -n "microservices-platform.svc\|bff-service:8080\|-service:8080"` を deploy・scripts に掛け、加えて `sidecar.istio.io/inject` の全件、Job / CronJob の全件、Prometheus の `scrape_configs` の全件を引いた。結果は IADR-0590 §壊れ得る経路の洗い出し。

## 設計

- 起動器 `[6/7]`: `ISTIO_MTLS_MODE` が空（初回）なら STRICT を入れて INFO を出し、以降は従前の段取り（`LOCALEDGE=1` かつ未移行なら `[6/7]` は PERMISSIVE、`istio-edge-up.sh` [5/5] が STRICT）。
- 引き継ぎ（`[2/7]` の前）: 引き継いだモードが STRICT でなければ WARN（上げ方の 1 行）。
- 末尾: `ISTIO=1` かつ STRICT のとき、`kubectl get namespace ai-stock-trading` の注入ラベルと `kubectl -n ai-stock-trading get pods` の数を読み、Pod が居てラベルが無ければ WARN。`ISTIO=1` 単独の WARN の文言を直す（port-forward は STRICT でも届く）。
- 統合スタック: 外向き通信の門の後、ログイン経路の門（最後に置く規則）の前に「メッシュの mTLS が STRICT で効いている」を置く。busybox（ノードに在る digest 固定のイメージ）で、サイドカー付き（同じ名前空間）・サイドカー無し（同じ名前空間・default）の 3 つの Pod から `document-service` の `/health/live` を `wget` で叩き、reached / refused / refused を要求する。`env.ISTIO == '1'` のときだけ走る。

## 受け入れ基準

- [x] `ISTIO=1 LOCALEDGE=1` の初回は `[6/7]` が PERMISSIVE、`istio-edge-up.sh` [5/5] が STRICT を宣言する（試験）。
- [x] `ISTIO=1` 単独の初回・メッシュ未宣言のリリースは `[6/7]` が STRICT を宣言する（試験）。
- [x] 引き継いだ PERMISSIVE は PERMISSIVE のまま、WARN を出す。引き継いだ STRICT では出さない（試験）。
- [x] STRICT で AST の Pod が居て注入ラベルが無ければ WARN。注入済み・Pod 無し・PERMISSIVE・メッシュ無しでは出さない／読まない（試験）。
- [x] 既定を PERMISSIVE へ戻す変異で上の試験が赤になる（変異 1。3 件が赤）。
- [x] 統合スタックの門は STRICT で緑、PERMISSIVE で赤（手元の k3s で実測。IADR-0590 §実測）。
- [x] チャートの描画は不変（テンプレート・values を触らない）。
- [ ] 統合スタックを PR のブランチで `ISTIO=1` で流し、新しい門と既存の門が緑（PR 作成後）。

## 影響範囲

- `scripts/k8s-local-up.sh`・`scripts/k8s-local-up.test.js`・`.github/workflows/integration-stack.yml`・`deploy/istio/README.md`・`deploy/local/README.md`・`deploy/local/edge-istio/README.md`・IADR-0590（新規）・IADR-0307 / IADR-0377（日付つき追記）。
- 稼働のクラスタ: 再実行は現行のモードを引き継ぐので、PERMISSIVE のクラスタは変わらない（WARN が増える）。STRICT へ上げるのは `ISTIO_MTLS_MODE=STRICT` の明示で、前提は AST のメッシュ参入。
