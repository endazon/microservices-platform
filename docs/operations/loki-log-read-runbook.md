---
title: 運用 Runbook — 経路 B の Loki のログを読む・保全する（読み取りの経路・資格・保持と永続化の確認）
type: runbook
status: draft
author: claude
created: 2026-10-15
updated: 2026-10-15
---
<!-- trace:
ids: [NFR-18]
adrs: [ADR-0133, ADR-0112, ADR-0006]
iadrs: [IADR-0541, IADR-0526, IADR-0210, IADR-0369]
specs: [20261015_1935_loki-log-read-runbook, 20261009_1842_loki-tempo-front-auth]
issues: [#1935, #1842, #1867]
-->

# 運用 Runbook: 経路 B の Loki のログを読む・保全する

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
>
> 🔴 **ローカル開発・PoC 環境（`deploy/local`）の経路 B 専用である。** 本番像の chart には Loki が無い。
>
> 🔴 **Loki の前段（認証）と到達の制限は弱めない。** 本書の経路は、すでにある読み取りの身元と `kubectl` の権限だけを使う。
> 本書のどの手順も、トークンの値を表示しない。

## 何が・どこに・どう守られているか

| 物 | 名前（名前空間 `platform-infra`） | 中身 |
| --- | --- | --- |
| Loki | Deployment `loki` の container `loki` | Pod の loopback（`127.0.0.1:3101`）だけで待つ。ネットワークへは出ていない |
| 前段 | 同じ Pod の container `gate`（Caddy） | Service の口 3100 を持つ。Bearer トークンで 2 つの身元を見分ける。書き込み（`writer`）は push だけ、読み取り（`reader`）は GET の読み取り API だけを通す。運用の口（`/config`・`/flush` 等）と削除 API はどちらの身元でも 403、トークン無しは 401 |
| 到達の制限 | NetworkPolicy `loki-ingress` | 3100 へ届く Pod は otel-collector と Grafana だけ |
| 入口 | Service `loki`（port 3100 → gate） | — |
| トークン | Secret `observability-gate`（鍵 `writer` / `reader`） | 起動器（`OBSERVABILITY=1`）が乱数で作り、再実行では引き継ぐ |
| 保存先 | PVC `loki-data`（2Gi・`local-path`）→ `/tmp/loki` | chunks・index・WAL（`/tmp/loki/wal`）がすべてこの中にある。永続化は既定オン（`PERSIST=0` のときだけ外れる） |

**運用者の読み取りの経路は 2 つある。**

| 経路 | いつ使うか | 何が通るか |
| --- | --- | --- |
| **A（正規）**: `port-forward svc/loki 3100` ＋ 読み取りのトークン | 日常の読み取り・ログの保全 | 前段の読み取りの列挙だけ（`query_range`・`query`・`labels` 等）。運用の口・削除 API には触れない |
| **B（break-glass）**: `deploy/loki` の Pod の中の 3101 | 保持の実効値（`/config`）を見るとき。前段が壊れていて A が使えないとき | Loki のすべての口（運用の口・削除 API を含む）。**GET の読み取りだけに使う** |

どちらの経路も Kubernetes の認証・認可（Secret の get か `pods/portforward`・`pods/exec`）を通る。
port-forward と exec は kubelet を経由して Pod の中から繋ぐので、クラスタの他の Pod への到達の制限はそのまま残る。

対話的に眺めるだけなら、Grafana の Explore（edge 経由の SSO。`platform-operator` 以上）でも読める。
ただし Explore の表示は件数の上限で切れる。**監査のために残す写しは、下の §4 の手順で書き出す。**

---

## 1. Loki が居て、ログが永続化されているかを確かめる（最初に必ず）

ログが無いときは、どこを探しても無い。まずここで切り分ける。

```sh
kubectl -n platform-infra get deploy loki
```

- 期待: `loki   1/1   1   1   <経過時間>`。
- `Error from server (NotFound)` のとき、**可観測性を有効にして起動していない**（`OBSERVABILITY=1` が無い）。
  collector の既定の構成は debug 出力だけで、どこにもログを残さない。**その期間のログは Loki に無く、取り戻す手段は無い。**
  これから残すには、`OBSERVABILITY=1 bash scripts/k8s-local-up.sh --live` で起動し直す。

```sh
kubectl -n platform-infra get pod -l app=loki \
  -o jsonpath='{range .items[*]}{.metadata.name}{"  開始 "}{.status.startTime}{"  "}{range .status.containerStatuses[*]}{.name}{"="}{.ready}{"/再起動"}{.restartCount}{" "}{end}{"\n"}{end}'
```

- 期待: `loki-<ハッシュ>  開始 2026-10-15T…Z  gate=true/再起動0 loki=true/再起動0`。
- PC を再起動すると、開始時刻が新しくなる（または再起動の回数が増える）。これ自体は異常ではない。再起動の前のログは PVC に残っている（下で確かめる）。

```sh
kubectl -n platform-infra get pvc loki-data \
  -o jsonpath='{.status.phase}{"  "}{.status.capacity.storage}{"  "}{.spec.storageClassName}{"  作成 "}{.metadata.creationTimestamp}{"\n"}'
kubectl -n platform-infra get deploy loki \
  -o jsonpath='{.spec.template.spec.volumes[?(@.name=="data")].persistentVolumeClaim.claimName}{" → "}{.spec.template.spec.containers[0].volumeMounts[?(@.name=="data")].mountPath}{"\n"}'
```

- 期待（1 行目）: `Bound  2Gi  local-path  作成 <日時>`。
- 期待（2 行目）: `loki-data → /tmp/loki`。
- 2 行目が ` → ` だけ（空）のとき、**永続化が外れている**（`PERSIST=0` の配備）。Pod を作り直すたびにログが消える。`PERSIST=0` を外して起動し直す。
- PVC の `作成` が監査したい日より後のとき、**その日より後に PVC が作り直されている**（`k8s-local-down.sh --apply` が名前空間と PV を消した等）。それより前のログは無い。

```sh
kubectl -n platform-infra exec deploy/loki -c loki -- du -sh /tmp/loki/chunks /tmp/loki/index /tmp/loki/wal
kubectl -n platform-infra exec deploy/loki -c loki -- df -h /tmp/loki
```

- 期待: 3 つのディレクトリに容量がある。`df` の使用率は 90% 未満である。
- 使用率が 100% に近いとき、新しいログの取り込みが失敗している（古いログは残る）。保持の設定が無いので、Loki は自分では消さない（§5）。

## 2. 前段を通る資格（読み取りのトークン）を用意する

**トークンは Secret `observability-gate` の鍵 `reader` である。** 値はリポジトリにも本書にも無い。手元のシェルの変数へ読み込み、画面には出さない。

```sh
export LOKI_TOKEN="$(kubectl -n platform-infra get secret observability-gate -o jsonpath='{.data.reader}' | base64 -d)"
echo "reader のトークン: ${#LOKI_TOKEN} 文字（値は表示しない）"
```

- 期待: `reader のトークン: 64 文字（値は表示しない）`。
- 0 文字のとき、Secret が無い（`OBSERVABILITY=1` で起動していない）か、権限が無い。前段は Secret が無いと起動しない。
- 🔴 **`writer` を使わない。** 書き込みの身元は読み取りの道を通らない（403）。書き込みの身元を運用者の手元へ持ち出す理由も無い。
- 作業が終わったら `unset LOKI_TOKEN` で消す。

## 3. 前段の口へ繋ぐ（経路 A）

別の端末で port-forward を立てたままにする。

```sh
kubectl -n platform-infra port-forward svc/loki 3100:3100
```

- 期待: `Forwarding from 127.0.0.1:3100 -> 3100`（と `[::1]:3100`）。
- 手元の 3100 が埋まっているときは `13100:3100` とし、以降の URL を `http://localhost:13100` に読み替える（スクリプトは `--url http://localhost:13100`）。

元の端末で、前段の判定を 3 つ確かめる。トークンは `-H @<(...)`（プロセス置換）で渡し、コマンドの引数に載せない。

```sh
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:3100/loki/api/v1/status/buildinfo
curl -s -o /dev/null -w '%{http_code}\n' -H @<(printf 'Authorization: Bearer %s\n' "$LOKI_TOKEN") http://localhost:3100/loki/api/v1/status/buildinfo
curl -s -o /dev/null -w '%{http_code}\n' -H @<(printf 'Authorization: Bearer %s\n' "$LOKI_TOKEN") http://localhost:3100/config
```

- 期待: `401`（トークン無し）→ `200`（読み取りの身元）→ `403`（読み取りの身元は運用の口を通らない）。
- 2 つ目が `401` のとき、トークンが違う。Secret を回した後に Pod を再起動していない可能性がある（前段は起動時の値で照合する）。
- 1 つ目から繋がらない（`000`）とき、port-forward が落ちている。端末を確かめて立て直す。

## 4. ログを保全する（時間範囲を指定してファイルへ書き出す）

道具は `scripts/export-loki-logs.js`（読み取り専用。GET の `query_range` / `query` だけを呼ぶ）である。
**MSP のリポジトリの直下で実行する。** 稼働クラスタへ当たるので、`--live`（または `LIVE=1`）を付ける。トークンは §2 の環境変数 `LOKI_TOKEN` から読む。

- 時刻は**時差つき**の RFC 3339 で与える（`2026-10-14T00:00:00+09:00`）。時差の無い書き方は拒まれる。
- `--from` を含み、`--to` を含まない。1 回の範囲は 30 日と 1 時間まで（Loki の `max_query_length`）。
- 1 応答の 5000 件を超える分は、スクリプトが古い順にページを送って全部書く。同じ時刻の行も 1 度ずつ書く。読み切れないときは、黙って打ち切らずに失敗する。
- 既に在るファイルへは書かない（上書きしない）。

### 4-1. まず範囲に何があるかを見る（`--summary`）

```sh
node scripts/export-loki-logs.js --live --summary \
  --from 2026-10-14T00:00:00+09:00 --to 2026-10-15T00:00:00+09:00
```

期待（件数と job は環境による）:

```text
範囲: 2026-10-13T15:00:00.000000000Z 〜 2026-10-14T15:00:00.000000000Z（--to は含まない）
クエリ: {job=~".+"}
最古の行: 2026-10-13T15:00:02.118000000Z
最新の行: 2026-10-14T14:59:58.904000000Z
job ごとの件数（目安。正は書き出しの行数）:
  52311	microservices-platform.bff
  20877	ai-stock-trading.market-monitor-service
  …
  312	vault-audit
```

- `job ごとの件数: 0 件` で `最古の行: 無し` のとき、その日のログは Loki に無い。§1 に戻って原因を切り分ける（§5 の表）。
- 最古の行が範囲の始まりより大きく遅いとき、それより前が欠けている。PVC の作り直しか、Loki が居なかった期間がある。

**ラベル**: 
- `job` は、送り手の OTel の `service.name` である。MSP は `microservices-platform.<サービス>`（例 `microservices-platform.bff`・`microservices-platform.authorization-service`）、AST は `ai-stock-trading.<サービス>`（例 `ai-stock-trading.audit-service`・`ai-stock-trading.order-execution-service`）、秘匿管理の audit は `vault-audit` である。
- `service_name` は Loki が `job` から補う同じ値である。
- 実際の値は次の 1 行で一覧できる。

```sh
curl -s -G -H @<(printf 'Authorization: Bearer %s\n' "$LOKI_TOKEN") \
  --data-urlencode 'start=2026-10-14T00:00:00+09:00' --data-urlencode 'end=2026-10-15T00:00:00+09:00' \
  http://localhost:3100/loki/api/v1/label/job/values | jq -r '.data[]'
```

### 4-2. 書き出す（例: 10-14 の 1 日分）

```sh
mkdir -p ~/loki-audit/20261014
node scripts/export-loki-logs.js --live \
  --from 2026-10-14T00:00:00+09:00 --to 2026-10-15T00:00:00+09:00 \
  --out ~/loki-audit/20261014/all.jsonl
```

期待:

```text
範囲: 2026-10-13T15:00:00.000000000Z 〜 2026-10-14T15:00:00.000000000Z（--to は含まない）
クエリ: {job=~".+"}
書き出し: /home/<あなた>/loki-audit/20261014/all.jsonl（<N> 行・<P> ページ）
最古の行: …
最新の行: …
sha256: <64 桁>
```

- `<N>` は §4-1 の件数の合計とほぼ一致する。§4-1 の件数は Loki の集計による目安で、時間の分け目にちょうど載る行を数え落とすことがある（実測で 8000 行に対し 7997）。**正は書き出しの行数である。**

サービスや時間帯で絞るときは `--query` と範囲を変える（LogQL の正規表現はバッククォートで囲むと、`\.` をそのまま書ける）。

```sh
# AST の全サービス
node scripts/export-loki-logs.js --live --query '{job=~`ai-stock-trading\..+`}' \
  --from 2026-10-14T00:00:00+09:00 --to 2026-10-15T00:00:00+09:00 --out ~/loki-audit/20261014/ast.jsonl
# AST の監査サービスだけ・取引の時間帯（JST 22:30〜翌 05:00）
node scripts/export-loki-logs.js --live --query '{job="ai-stock-trading.audit-service"}' \
  --from 2026-10-14T22:30:00+09:00 --to 2026-10-15T05:00:00+09:00 --out ~/loki-audit/20261014/ast-audit-night.jsonl
# MSP の BFF で、本文に session を含む行だけ
node scripts/export-loki-logs.js --live --query '{job="microservices-platform.bff"} |= `session`' \
  --from 2026-10-14T00:00:00+09:00 --to 2026-10-15T00:00:00+09:00 --out ~/loki-audit/20261014/bff-session.jsonl
# 秘匿管理の audit
node scripts/export-loki-logs.js --live --query '{job="vault-audit"}' \
  --from 2026-10-14T00:00:00+09:00 --to 2026-10-15T00:00:00+09:00 --out ~/loki-audit/20261014/vault-audit.jsonl
```

書き出したら、写しの改ざんを後から見分けられるように指紋を残す。

```sh
(cd ~/loki-audit/20261014 && sha256sum *.jsonl > SHA256SUMS && cat SHA256SUMS)
```

**ファイルの形**: 1 行が 1 件の JSON である。`{"ts":"2026-10-14T01:02:03.123456789Z","ns":"…","labels":{"job":"…",…},"line":"…"}`。
`ts` は UTC である。`line` はアプリのログの本文で、OTel 由来の行では JSON（`body`・`attributes`・`traceid` 等）になる。読み方の例:

```sh
jq -r '.ts + "  " + .labels.job + "  " + .line' ~/loki-audit/20261014/ast.jsonl | less
jq -r '.line | fromjson? | .body // empty' ~/loki-audit/20261014/ast-audit-night.jsonl | head
```

> 1 ページだけを curl で見るとき（5000 件を超える分は返らない。保全には使わない）:
> `curl -s -G -H @<(printf 'Authorization: Bearer %s\n' "$LOKI_TOKEN") --data-urlencode 'query={job="vault-audit"}' --data-urlencode 'start=2026-10-14T00:00:00+09:00' --data-urlencode 'end=2026-10-15T00:00:00+09:00' --data-urlencode 'limit=100' --data-urlencode 'direction=forward' http://localhost:3100/loki/api/v1/query_range | jq -r '.data.result[].values[][1]'`
>
> logcli を手元に持っているなら、同じ口（`--addr http://localhost:3100`）と読み取りのトークン（`--bearer-token-file`）で使える。本リポでは導入も実測もしていない。

## 5. 保持期間を確かめる・ログが消える経路を知る

**Loki の設定に保持（retention）の指定は無い。** compactor の retention は既定で無効なので、Loki 自身はログを消さない。
設定を見る（経路 A の読み取りの身元では `/config` は 403 になるので、ConfigMap と Pod の中から見る）:

```sh
kubectl -n platform-infra get configmap loki-config -o jsonpath='{.data.config\.yaml}' \
  | grep -nE 'retention|compactor|limits_config' || echo '保持の指定なし（既定＝削除しない）'
kubectl -n platform-infra exec deploy/loki -c loki -- wget -qO- http://127.0.0.1:3101/config \
  | grep -E '^\s*(retention_enabled|retention_period|max_query_length|max_entries_limit_per_query):'
```

期待:

```text
保持の指定なし（既定＝削除しない）
  retention_enabled: false
  max_query_length: 30d1h
  max_entries_limit_per_query: 5000
  retention_period: 0s
  retention_period: 0s
```

- 2 つ目のコマンドは経路 B（Pod の中の Loki 本体）である。**GET の `/config` だけを読む。** 同じ口に削除・flush 等の運用の口があるので、それ以外を叩かない。
- Loki に残っている最古の行は、§4-1 の `--summary` を 30 日の範囲で当てて見る（例 `--from 2026-09-16T00:00:00+09:00 --to 2026-10-16T00:00:00+09:00`）。

**ログが消える（または残らない）経路**。どれも、消えたログを取り戻す手段は無い。

| 経路 | 見分け方 | 何が消えるか |
| --- | --- | --- |
| 可観測性を有効にせずに起動した | §1 で `deploy/loki` が NotFound | その期間のログはそもそも Loki に無い |
| `PERSIST=0`（永続化を外した） | §1 の 2 行目が空 | Pod を作り直すたびに全部 |
| `k8s-local-down.sh --apply`（名前空間と PV の削除） | §1 の PVC の作成日時が新しい | それより前の全部 |
| PVC の満杯 | §1 の `df` が 100% に近い | 満杯の後に届いた新しいログ（古いログは残る） |
| PC の再起動・Pod の再起動 | — | **消えない**（WAL と chunks は PVC の中にあり、起動時に WAL を読み戻す）。消えるのは `kubectl logs` で見ていたコンテナの標準出力だけである |

`kubectl logs` は、Pod が作り直されると読めなくなる。監査に使う記録は Loki 側にあり、それを §4 で書き出して残す。

## 6. 後片付け

```sh
unset LOKI_TOKEN
# port-forward の端末は Ctrl-C で止める
```

書き出したファイル（`~/loki-audit/`）には、アプリのログの本文がそのまま入る。共有するときは、本文に個人の情報が含まれ得ることを前提に扱う。

## 困ったとき

| 症状 | 原因と対処 |
| --- | --- |
| スクリプトが `[export-loki-logs] 稼働クラスタへ当たるため…` で終わる（exit 3） | `--live` を付け忘れている |
| `HTTP 401: 前段がトークンを受け付けなかった` | `LOKI_TOKEN` が空か違う。§2 をやり直す |
| `HTTP 403: 前段がこの道を通さなかった` | `writer` のトークンを入れている。`reader` に替える |
| `HTTP 400: Loki が要求を拒んだ` | LogQL の誤り、または範囲が 30 日と 1 時間を超えている。本文を見て範囲を分ける |
| `Loki へ要求できなかった（port-forward は動いているか）` | port-forward が落ちている。§3 を立て直す |
| `--from は RFC 3339 の時差つきの時刻で与える` | `+09:00` か `Z` を付ける |
| `出力先が既に在る（上書きしない）` | 別の名前にするか、前の写しを退避してから消す |
| `同じ時刻（…）に 5000 件以上の行があり先へ進めない` | 同じナノ秒に 5000 件を超える行がある。範囲を分けても読めない。Loki の `max_entries_limit_per_query` を一時的に上げる判断が要る（設定の変更なので、運用者だけで決めない） |
