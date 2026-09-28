---
title: 入口を Istio へ移した後の k8s-local-up.sh（LOCALEDGE=1 ISTIO=1）の再実行を冪等にする（Traefik へ戻さず待たない。#1691）
type: spec
status: done
related_ids: [NFR, ADR-0021, ADR-0005, IADR-0317, IADR-0258, IADR-0377, IADR-0091, IADR-0227]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md (入口＝Istio Ingress Gateway・Traefik は無効化)
  - planning:projects/microservices-platform/02_requirements/ (NFR 運用性。起動器の再実行で収束すること)
issue: "#1691"
---

# 仕様書: Istio へ移した入口の再実行で Traefik へ戻さない（#1691）

> 本仕様書は実装着手前に作成する。起点は #1691（稼働クラスタで `LOCALEDGE=1 ISTIO=1 ... k8s-local-up.sh --live` の再実行が LOCALEDGE の段で rc=1 になった実測。2026-09-14 に続き 2 回目＝同型の事故 2 回）。

## 起点となる計画書（トレーサビリティ）

- 要求: 無採番の `NFR`（運用性。起動器は再実行で収束する＝冪等である）。製品の FR には当たらない
- 計画 ADR: ADR-0021（入口は Istio Ingress Gateway・Traefik は無効化）、ADR-0005（サービスメッシュ）
- 関連 IADR: **IADR-0317**（入口の Istio 化。本件の判断は同 IADR へ日付つきで追記する）、IADR-0258（HelmChartConfig の反映待ち＝今回止まった門）、
  IADR-0377（mTLS は helm で書く）、IADR-0091（Traefik エッジ）、IADR-0227（CoreDNS の *.localhost 転送）

## 何が起きたか（issue 本文の要約）

1. 前回の実行で `istio-edge-up.sh` が `deploy/local/edge-istio/traefik-service-off.yaml`（HelmChartConfig `kube-system/traefik` を `service.enabled: false`）を当て、Traefik の Service を消した。
2. 再実行で `kubectl apply -k deploy/local/edge` が同じ HelmChartConfig を `traefik-entrypoint.yaml`（Service あり）へ戻す。
3. helm-controller の入れ直しが 180 秒を超え、`kubectl wait svc/traefik --timeout=180s` が先に諦めて rc=1。後段（`istio-edge-up.sh`・Wiki.js 初期化・seed・合成監視）が走らない。
4. 作り直された `svclb-traefik` は 80 / 443 / 50000 を istio-ingressgateway の svclb と取り合い Pending のまま残る。

## 直し方（決定）

### 1. 判定はクラスタの実際の状態で行う（フラグで判定しない）

`kubectl get helmchartconfig traefik -n kube-system -o jsonpath='{.spec.valuesContent}'` を読み、**トップレベルの `service:` の
直下の子に `enabled: false`** があれば「入口は Istio へ移し済み」と読む。それ以外は「未移行」。

- **判定は `scripts/lib/edge-state.sh` の 1 本だけ**にし、`k8s-local-up.sh`（`edge_on_istio`）と `k8s-local-down.sh`
  （`traefik_service_disabled`。読み取りは `kc_read` を通す）の両方がそれを呼ぶ。［2026-09-28 / PR 監査 #1694 の推奨］当初は up に
  「空白を潰して `service: enabled: false` を含むか」を置き、down の「どこかに `enabled: false`」と厳しさが違っていた。
  現行の 2 本の宣言では同じ結論でも、宣言が増えるとずれる形なので 1 本化した。
- 判定の表（`k8s-local-up.test.js` が up と down の両方に通して固定する。監査のプローブ `audit-1694-probe.sh` を写した）:

| valuesContent / kubectl | 判定 | 理由 |
| --- | --- | --- |
| `service:` ＋ 直下 `enabled: false`（`traefik-service-off.yaml` の実物） | 移行済み | 本来の印 |
| `service:` の直下で他のキーが先（`type: LoadBalancer` の後に `enabled: false`） | 移行済み | 直下の子であれば順序は問わない |
| flow 形 `service: {enabled: false}`・CRLF・行末コメント | 移行済み | 同じ YAML の別表記 |
| `traefik-entrypoint.yaml` の実物・`service.enabled: true`・空 | 未移行 | Service は在る |
| HelmChartConfig が無い（NotFound）・kubectl が失敗（出力があっても） | 未移行 | 読めないときは未移行へ倒す（下記） |
| `service:` 以外のキーの `enabled: false`（`ingressRoute.dashboard`） | 未移行 | 別の機能の無効化 |
| **入れ子の `x.service.enabled: false`**（`metrics.service` 等） | **未移行** | Traefik chart で LoadBalancer Service（80/443/50000 の svclb）を消すのはトップレベルの `service.enabled` だけである。`metrics.service` 等は別の Service を指す |
| `service:` の孫の `enabled: false`（`service.spec.enabled`） | 未移行 | 直下ではない |
| 引用符つきの `enabled: "false"` | 未移行 | Go テンプレートでは空でない文字列は真であり、Service は消えない |

- **読めないときに未移行へ倒す理由**: 移行済みを見落とした場合は、Traefik の反映待ちが**非 0 で止まる**（#1691 と同じく目に見える）。
  移行済みへ倒すと、Traefik がエッジのクラスタで反映待ちの門（IADR-0258）を**黙って**飛ばすことになる。黙る側を選ばない。
- 判定は `LOCALEDGE=1` のときだけ、`[1/7]`（クラスタ）の直後・`[2/7]` の前で 1 回行う。**副作用より前**に置くのは、
  下の 3. の拒否を何も書き換えないうちに出すためである。新規クラスタでは HelmChartConfig が無いので未移行と読む。

### 2. 移行済みかつ `ISTIO=1` のとき（本件）

LOCALEDGE の段で、**Traefik を前提とする 4 つの処理を飛ばす**（母集合は下表）:

- `kubectl apply -k deploy/local/edge`（HelmChartConfig を Service ありへ戻す ＝ 事故の起点）
- `kubectl wait ... svc/traefik`（admin=50000 の反映待ち ＝ 止まった門）
- `deploy/local/aliases/coredns-edge-hosts.yaml` の apply と CoreDNS の再起動（pod 側の `*.localhost` を消えた `traefik.kube-system` へ向け直す。`istio-edge-up.sh` が当て直すまでの間、in-cluster の OIDC が引けない）
- `deploy/local/edge/argocd-ingress.yaml`（Traefik しか読まない Ingress。切り戻しの `istio-edge-down.sh` が当て直す）

cert-manager とエッジ TLS（`deploy/local/edge/tls`）は**飛ばさない**（ClusterIssuer `local-edge-ca` は `istio-edge-up.sh` の前提）。
その後 `istio-edge-up.sh` を従来どおり呼ぶ。同スクリプトは冪等であり（[2/5] は Service が既に無いので即座に通る）、Gateway・経路・CoreDNS・mTLS を当て直す。

加えて `[6/7]` の mTLS の宣言: 未移行では「入口がまだ Traefik なので STRICT を要求されても PERMISSIVE を宣言し、`istio-edge-up.sh` が後で上げる」。
**移行済みでは入口は既に Envoy なので、要求どおりのモードを宣言する**（再実行のたびに STRICT → PERMISSIVE → STRICT と一時的に緩めない）。

### 3. 移行済みで `ISTIO` を指定せず `LOCALEDGE=1` だけで再実行したとき —— 拒否する（fail-closed）

利用者の意図は 2 通りあり得る（Istio のまま再実行したつもりで `ISTIO=1` を付け忘れた ／ Traefik へ戻したい）。どちらでも
**そのまま従来の Traefik 経路を流すのは誤り**である:

- Traefik へ戻す処理は istio-ingressgateway を撤去しないので、hostPort を取り合って `svclb-traefik` が Pending になる（#1691 の 4.）。
- `ISTIO` 無しの `[6/7]` は mesh の `--set` を付けずに helm upgrade するので、メッシュ設定を黙って外す。
- CoreDNS を Traefik へ向け直し、Istio の入口は残ったまま pod 側だけ消えた先を引く。

戻す正規の手段は `bash scripts/istio-edge-down.sh --live` の 1 コマンドである（IADR-0317 決定 7。順序＝mTLS を緩める → Gateway を撤去 → Traefik を戻す）。
そこで起動器は `[2/7]` の前に**非 0 で止め**、2 つの道（Istio のまま＝`ISTIO=1` を付けて再実行／Traefik へ戻す＝先に `istio-edge-down.sh --live`）を告げる。
推測で片方を選ばない。

## 母集合（規則 9: `traefik` の全文走査。2026-09-28・`origin/develop` = `6bb387df`）

走査: `git grep -il traefik -- ':!src/ai-stock-trading'`（`.ai-context/` の凍結記録 57 件は除く）＝ 38 ファイル。
「Istio 移行後に Traefik 前提で**動く**処理」かで分類した（注記・文書だけのものは動かないので対象外）。

| 箇所 | 移行後の再実行で何が起きるか | 扱い |
| --- | --- | --- |
| `k8s-local-up.sh` LOCALEDGE: `apply -k deploy/local/edge` | HelmChartConfig を Service ありへ戻す | **飛ばす**（本件の起点） |
| `k8s-local-up.sh` LOCALEDGE: `wait svc/traefik` | 180 秒で rc=1 | **飛ばす**（本件の門） |
| `k8s-local-up.sh` LOCALEDGE: `aliases/coredns-edge-hosts.yaml` ＋ CoreDNS 再起動 | pod 側 `*.localhost` が消えた Traefik を向く（`istio-edge-up.sh` [4/5] まで） | **飛ばす**（同型） |
| `k8s-local-up.sh` LOCALEDGE: `argocd-ingress.yaml` | Traefik 専用の Ingress を置くだけ（害は無いが Traefik 前提） | **飛ばす**（down が当て直す） |
| `k8s-local-up.sh` `[6/7]` の `ISTIO_MTLS_MODE_AT_INSTALL` | STRICT を要求されても PERMISSIVE へ一時降格 | **是正**（移行済みなら要求どおり） |
| `k8s-local-up.sh` `ISTIO=1` かつ `LOCALEDGE` 無しの WARN | 「移りません」と告げる（移行済みでも出る） | **対象外**: 動作は変えない注意書き。LOCALEDGE 無しでは判定を走らせない（既定経路の出力を増やさない） |
| `k8s-local-down.sh`（`traefik_service_disabled` / `restore_traefik_service`） | 移行済みを判定して Traefik の Service を戻す | **対象外**: 既に状態で判定している（2026-09-14 の 1 回目の是正） |
| `istio-edge-down.sh` | Traefik へ戻す正規の手段 | **対象外**: 意図して Traefik 前提 |
| `istio-edge-up.sh` [2/5] | Service の消滅を待つ | **対象外**: 移行済みでは即座に通る（冪等） |
| `check-stack-ready.js` | エッジを Traefik / Istio の両方で見る | **対象外**: 既に両対応 |
| `verify-oidc-edge-flow.sh` | 同上 | **対象外** |
| `.github/workflows/integration-stack.yml` | 新規クラスタでの診断出力のみ | **対象外**: 再実行を扱わない |
| `deploy/**`（Ingress・HelmChartConfig・README 等）・`docs/**`・`scripts/README.md`・`*.test.*` | 宣言・注記・試験 | **対象外**: 自ら動かない |

## 範囲外のリスク（記録に留める。実装しない。監査 #1694）

- **移行済みのクラスタを `LOCALEDGE` も `ISTIO` も付けずに再実行すると、[6/7] でメッシュの設定が外れる恐れがある。**
  `ISTIO` 無しの [6/7] は `mesh.*` の `--set` を付けずに helm upgrade するため、`mesh.enabled` が既定（false）へ戻り
  PeerAuthentication 等が消え得る（`LOCALEDGE` が無いので本件の判定は走らず、入口の Gateway は残る）。
  本件（`LOCALEDGE=1` のときの Traefik 前提）と同じ型の「フラグが現状と食い違う再実行」だが、`LOCALEDGE` の段を通らない別の経路であり、
  本 PR では扱わない。直すなら判定（`lib/edge-state.sh`）を `LOCALEDGE` の有無によらず走らせ、移行済みで `ISTIO` 無しなら止める形が候補である。

## 変更するもの

| ファイル | 変更 |
| --- | --- |
| `scripts/lib/edge-state.sh` | 新規。判定の単一の口（`edge_traefik_service_off` / `edge_values_traefik_service_off`） |
| `scripts/k8s-local-up.sh` | lib を読み `edge_on_istio` を 1 行呼び出しにする・`[2/7]` 前の判定と拒否・`[6/7]` の mTLS 宣言・LOCALEDGE の段の分岐 |
| `scripts/k8s-local-down.sh` | `traefik_service_disabled` を lib の 1 行呼び出しにする（判定の厳しさを up と揃える） |
| `scripts/live-scripts.json` / `scripts/README.md` | 新しい lib を `offline`（source される関数定義だけ）へ分類し、一覧に加える |
| `.ai-context/adr/IADR-0377_...md` | 「STRICT を要求した再実行では [6/7] で一度 PERMISSIVE に戻る（意図した挙動）」へ、移行済みでは降格しない旨の日付つき追記 |
| `scripts/k8s-local-up.test.js` | kubectl スタブに HelmChartConfig の状態の模型を足し、試験を足す（下記） |
| `.ai-context/adr/IADR-0317_...md` | 決定 7 の後へ日付つき追記（新規 IADR は起こさない） |
| `deploy/istio/README.md` | 「STRICT を要求した再実行では一度緩んでから上がる」が本変更で誤りになる（規則 10）。移行済みの再実行の扱いに書き換える |

## 受け入れ基準（→ 試験。`k8s-local-up.test.js`）

スタブの模型: `traefik-service-off.yaml` の apply で HelmChartConfig が「Service 無し」になり Service が消える。「Service 無し」の状態で
`apply -k deploy/local/edge` が来ると入れ直し中になり、`wait svc/traefik` は `services "traefik" not found` で非 0（#1691 の再現）。
試験の入力 `STUB_EDGE_ON_ISTIO=1` は「前回の実行で移行済み」の状態から始める（フラグではなく模型の初期状態）。

1. 移行済み ＋ `LOCALEDGE=1 ISTIO=1`: 起動器は 0 で終わり、`apply -k deploy/local/edge`・`wait svc/traefik`・Traefik 向け CoreDNS・`argocd-ingress.yaml` を出さない。エッジ TLS は出す。
2. 移行済みでも `istio-edge-up.sh` が呼ばれる（`traefik-service-off.yaml` の apply と `istio-ingressgateway` の helm が出る）。
3. 未移行 ＋ `LOCALEDGE=1 ISTIO=1`: 従来どおり `apply -k deploy/local/edge` → `wait svc/traefik` → CoreDNS → `istio-edge-up.sh` の順に出る。
4. 移行済み ＋ `LOCALEDGE=1`（`ISTIO` 無し）: 非 0 で止まり、`istio-edge-down.sh` と `ISTIO=1` を告げ、`[2/7]` 以降（helm upgrade）へ進まない。
5. 移行済み ＋ `ISTIO_MTLS_MODE=STRICT`: `[6/7]` が STRICT を宣言する（未移行では従来どおり PERMISSIVE。既存試験）。
6. HelmChartConfig が読めない（kubectl の失敗・未作成）なら未移行として従来の経路を通る（起動器の経路で試す）。
7. `service:` 以外のキーの `enabled: false` は移行済みと読まない（同上）。
8. up と down は `lib/edge-state.sh` を 1 行で呼び、自前の `enabled:` 照合を持たない（静的）。上の判定の表の全件で up と down が同じ結論になる（lib の単体試験）。

## 検証

- `node scripts/k8s-local-up.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / `check-trace-blocks` / `check-commit-messages --range=origin/develop..HEAD`
- 変異 3 件以上（飛ばしの分岐を消す・判定を常に偽にする・`istio-edge-up.sh` の呼び出しを移行済みで飛ばす 等）で赤になること
- 稼働クラスタでは実行しない（本 PR の検証はスタブのみ）
