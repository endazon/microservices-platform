---
title: IADR-0709 クラウドの外向きの中継の許可表を送り元ごと（受け口のポートで名乗る）にし、API サーバーへの出の番地を名指し、collector が OTLP の送り手を送り元の Pod で確かめる
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, IADR-0705, IADR-0697, IADR-0689, IADR-0674, IADR-0675]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - ../specs/20261108_egress-per-source-otlp-identity.md
---

# IADR-0709: 外向きの中継の送り元ごとの表・API サーバーの番地・OTLP の送り手の名乗り

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-21**（観測）。前提: IADR-0705（外向きの中継）・IADR-0697（collector の受け口の絞り）・IADR-0689（OpenBao・ESO）。
- 作業仕様書: `.ai-context/specs/20261108_egress-per-source-otlp-identity.md`。取引ユニットの側の対（外向きを中継へ通す）は AST#1607。

## コンテキストと課題

- 中継の許可表は送り元を問わない 1 枚で、ある送り元が、別の送り元のための宛先へも出られた。クラウドで使わない埋め込みの提供元も載っていた。
- ESO・OpenBao の API サーバー向けの出は、宛先を問わない 443・6443 だった（IADR-0705 残余 5）。
- collector は送り手が名乗る `service.name` をそのまま `job` にするので、ある送り手が他の送り手の名で、警報の元の系列を書けた。

## 決定

### 決定 1 — 送り元の名乗りは中継の受け口のポート。届く Pod は NetworkPolicy が 1 つに絞る

- 中継は送り元ごとに受け口（ポート）を持つ（env `EGRESS_PROXY_SOURCES`＝`<送り元>:<ポート>` の空白区切り）。要求の中身では名乗りを信じない。
- 各受け口への入りは、中継の NetworkPolicy の 1 つの規則が「名前空間 かつ ラベル `app`」（同じ `from` の要素）の 1 組だけを許す。
- 受け口と Pod の組（取引ユニットの側が参照する）:

  | 送り元 | 受け口 | 名前空間 / `app` |
  | --- | --- | --- |
  | `llmgateway` | 3131 | `microservices-platform` / `llmgateway-service` |
  | `backup` | 3132 | `platform-infra` / `platform-backup` |
  | `alertmanager` | 3133 | `platform-infra` / `alertmanager` |
  | `ast-information-collection` | 3141 | `ai-stock-trading` / `information-collection-service` |
  | `ast-market-monitor` | 3142 | `ai-stock-trading` / `market-monitor-service` |
  | `ast-notification` | 3143 | `ai-stock-trading` / `notification-service` |
  | `ast-report` | 3144 | `ai-stock-trading` / `report-service` |
  | `ast-risk-management` | 3145 | `ai-stock-trading` / `risk-management-service` |
  | `ast-trade-decision` | 3146 | `ai-stock-trading` / `trade-decision-service` |
  | `ast-opend` | 3147 | `ai-stock-trading` / `opend` |

- 取引ユニットの Pod は `HTTPS_PROXY=http://egress-proxy.platform-infra.svc.cluster.local:<受け口>` を使い、出の NetworkPolicy は
  名前空間 `platform-infra` の `app: egress-proxy` の自分の受け口の TCP だけを許す。
- 表の行は `<送り元> <名前>[:<ポート>]`（ポートを書かなければ 443。443 でないポートは名指した行だけ）。`*.<名前>` はその名前の 1 段下だけに当たる
  （土台は 2 段以上）。宣言の表で `*.` を使うのは、名前が応答で決まる Discord の Bot の再接続先（`ast-notification *.discord.gg`）だけ（試験が固定する）。
- オーナーの表（`egress-proxy-extra-hosts`）も同じ形。送り元を書かない古い形の行は全体の表にだけ入り、どの送り元の行にもならない。
- ポートを分ける形を採り、送り元の IP から Pod を API で引く形を採らない（中継に API の権限を持たせない。受け口ごとの判定は NetworkPolicy の強制だけに依る）。

### 決定 2 — 使わない宛先を外し、保管先は自分のバケットの名前に寄せられるようにする

- 埋め込みの提供元（`api.voyageai.com`）をクラウドの表から外す（クラウドは埋め込みの鍵を置かない。試験が ESO の宣言と突き合わせる）。
- バックアップの保管先は、ConfigMap `platform-backup-s3-target` の `force_path_style` が `false` のとき、rclone が仮想ホスト形式で書き、
  中継の送り元 `backup` の行も `<バケット>.<endpoint の名前>` だけになる（バケットは `dest` から。読めなければ何も足さない）。既定（キーが無い）は従来のパス形式。
- trade-decision の SEC の宛先はクラウドで無効なので載せない。

### 決定 3 — 送り元ごとの段を既定 report にし、enforce へ進める

- `EGRESS_PROXY_SOURCE_MODE`: report は「全体の表には在るが、その送り元の行に無い」宛先を通して `report:` の行と `egress_proxy_source_mismatch_total` を出す。
  enforce は 403。全体の表に無い宛先は従来の段（`EGRESS_PROXY_MODE`）で判定する。
- 従来の口（3128。送り元 `legacy`）は、移す間と切り戻しの Pod のために残す。全体の表で判定し、送り元ごとの段が enforce なら何も通さない。
- 警報: `EgressProxySourceMismatch`（直近 15 分の不一致）。`EgressProxyReportMode` は送り元ごとの段が report のときも、入った日時から 14 日（か日時が無い）で鳴る。
- enforce へ進めるのは、不一致と `[legacy]` の行が 7 日出ないことを確かめた後の、リポジトリの変更である（手順書）。

### 決定 4 — API サーバーへの出は番地を名指す。宛先を問わない出は段 1 の別の宣言に分ける

- k3s は `--advertise-address 127.0.0.1` で `--service-cidr` を渡さない（README §1-4）。Pod の宛先は Service `kubernetes` の `10.43.0.1:443`、
  ノードで書き換わった先は `127.0.0.1:6443`。NetworkPolicy が書き換えの前と後のどちらで当たっても通るよう、両方を `ipBlock`（/32）で名指す。
  名指してよい Pod は ESO（名前空間の全 Pod）・OpenBao・collector だけ（試験が固定する）。
- ESO・OpenBao の宛先を問わない 443・6443 は、名前の末尾 `-ports-only-stage1` の別の宣言にだけ残す。読むだけの確かめ
  （`deploy/cloud/apiserver-egress-check.sh --live`。Service と EndpointSlice の番地を宣言と突き合わせる）の後、名指しで消して同期が続くことを確かめ、
  宣言から外す（段 2。手順書）。collector は段 1 を持たない（届かなければ印が付かず警報が鳴るだけで、系列は止まらない）。

### 決定 5 — collector は接続の送り元の Pod から印を付け、警報はその印で見る

- metrics の pipeline: `memory_limiter` → 送り手が付けた `k8s.*`（resource）と `otlp*sender*`（各点）の属性を消す → `k8sattributes` を名前空間ごとに 2 つ
  （`ai-stock-trading`・`microservices-platform`。接続の送り元の IP だけで引く。`passthrough: false`）で `k8s.namespace.name` とラベル `app` を付ける →
  各点へ `otlp_sender_namespace`・`otlp_sender_app` として写す → `batch`。Pod を引けなければ何も付けない。系列は落とさない（送り手を止めない）。
- 権限は名前空間ごとの Role（Pod の get・list・watch）だけ。ClusterRole は使わない。
- 基盤の既定（`deploy/cloud`）の collector は `k8sattributes/msp` だけを持つ。`ai-stock-trading` の Role と、`k8sattributes/ast` とそれを使う経路は、
  その名前空間が在ってから `deploy/cloud` の代わりに当てる重ね（`deploy/cloud-otlp-sender-identity-ast`）だけが足す（Role・名前空間が無いうちに
  collector が取引ユニットの Pod を引きに行かない＝当てる順に依らず collector が起きる。実物の collector で Role の無いときの振る舞いを確かめられないため）。
  重ねの設定は基盤の設定の写しで、試験が `k8sattributes/ast` の他は同じであることを突き合わせる。重ねは collector の Pod の注釈を足し、当て替えで作り直させる。collector は期限 1 時間の明示のトークンを読み取り専用で載せる。
- 警報: LLM ゲートウェイの費用・上限（`LlmMonthlyBudgetPaceExceeded`）・再起動（`LlmGatewayRestartLoop`）・費用の枠の置き場（`LlmCostStateUnavailable`）は
  `otlp_sender_namespace=~"microservices-platform|", otlp_sender_app=~"llmgateway-service|"` の系列を見る（LLM ゲートウェイの印か、**印の無い**系列）。
  ［2026-11-08 追記］印の無い系列も入れる。collector が API サーバーへ届かない（`127.0.0.1/32` の名指しが CNI で効かない等）・Role が無い間は
  印が付かず、印だけで絞ると費用の上限（所有者の裁定の 1 日の上限に関わる）を含む 3 つの警報が黙るため。collector には API サーバーへの出の段 1
  （宛先を問わない出）を置かない（置くと collector が外の任意の 443 へ出られる）。偽装の判定は、他の送り手の印を持つ系列を外すことと、
  印の無い送り手を知らせる `OtlpSenderUnverified` に任せる（他の送り手の Pod から届いた系列は必ず自分の印を持つので、印の無い形で偽れない）。
  `OtlpSendersMissing` は
  `target_info` を `(k8s_namespace_name, k8s_pod_label_app)` で数える。印の無い送り手は `OtlpSenderUnverified` が知らせる。
- 取引ユニットの規則（AST の PrometheusRule）が自分の系列を印で絞るのは取引ユニットの側の判断である（印は全系列に付く）。

## 検討した代替案

- 中継が送り元の IP から API で Pod を引く: 中継に API の権限が要り、外へ出る唯一の Pod の権限が広がる。不採用。
- collector の受け口を送り手ごとのポートに分ける: 取引ユニットの 11 の Pod の宛先の設定を個別に変える必要がある。k8sattributes は送り手の変更が要らない。不採用。
- 送り元の印で `service.name` を書き換える: 取引ユニットの規則の `job` の値が変わり、既存の送り手の警報を壊しうる。印を別のラベルにした。
- API サーバーの段 1 を移す段の重ね（cloud-transition）に置く: 基盤の宣言を当て直しただけで直ちに狭まり、確かめの前に ESO の同期を止めうる。別の宣言に分けた。

## 結果・残余

- 送り元ごとの段が enforce になり、3128 を外すまでは、他の送り元の行の宛先へも出られる（数えて警報する）。
- 段 1 の宣言を外すまで、ESO・OpenBao は宛先を問わない 443・6443 を持つ。その間、ESO の Pod の出の拒否の件数の警報は無い（NetworkPolicy の拒否は数えない）。
- collector の設定は手元の collector で読ませていない（実行物を取得できなかった）。初回の適用は `OtelCollectorDown`・`OtlpSenderUnverified` で見る。
- `*.discord.gg` は Discord の配下の 1 段下の任意の名前に当たる（discord.gg の名前の持ち主は Discord に限られる）。
- OpenD（`ast-opend`）の宛先とポートは取引ユニットの側が測って足す（中継の出は 443 だけ。443 でないポートは出の規則と対で足す）。
