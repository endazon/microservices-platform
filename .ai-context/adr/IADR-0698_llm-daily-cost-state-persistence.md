---
title: IADR-0698 LLM ゲートウェイの当日の費用の枠を、置き場（クラウドでは PVC の上の 1 つのファイル）に残して再起動を越えて引き継ぐ。読めない・書けない間は段 2 で送らない。起動の時刻を計器に出し、短い間の再起動の繰り返しを警報にする
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, FR-11, ADR-0044, IADR-0678, IADR-0692, IADR-0674, IADR-0675, IADR-0466]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_llm-cost-cap-persistence.md
---

# IADR-0698: LLM ゲートウェイの当日の費用の枠を再起動を越えて残す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「再起動しても残るように直す」・提供元のコンソールの支出上限はオーナーが設定する）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-21**（費用と運用の観測）・**FR-11**（LLM 費用の計測）。計画 ADR-0044。
- 前提: IADR-0678（送る前の上限。決定 5・結果 2 で「状態はプロセス内・再起動で当日の枠は 0 に戻る」と書いた）、
  IADR-0692（クラウドの当日の上限 合計 20 USD。結果 1 で同じ残余を書いた）、IADR-0674（クラウドの最小構成）、IADR-0675（クラウドの警報）。
- 作業仕様書: `.ai-context/specs/20261107_llm-cost-cap-persistence.md`。

## コンテキストと課題

- `LlmCallLimiter` は当日の費用（呼び手・呼び手×用途ごとの UTC の日の合計。見込み額の予約と実額への置き換えを含む）を
  `Dictionary` のプロセス内の状態で持っていた。ゲートウェイが再起動すると、その日の枠は 0 から数え直しになる。
- クラウドの Prometheus は emptyDir で、月次の警報（事後の観測）の窓も Prometheus の再起動で欠ける。送る前の上限が再起動で戻ると、
  1 日の支出が上限を超えても知る手段が弱い。
- クラウドの LLM ゲートウェイは 1 レプリカ（IADR-0692）。ゲートウェイは DB を持たない。

## 決定

### 決定 1 — 置き場はゲートウェイの Pod に付ける PVC の上の 1 つのファイル（基盤の Postgres は使わない）

- chart に汎用の `services.<名>.persistence.{enabled,mountPath,size,storageClass}` を足し、クラウドの値（`values-cloud.yaml`）だけで有効にする。
  PVC `llmgateway-service-state`（ReadWriteOnce・64Mi・既定の storage class＝k3s の local-path。`helm.sh/resource-policy: keep`）。
  既定とローカルの描画は変えない（バイト一致を確かめた）。
- ゲートウェイは `Llm:CallLimits:SpendStore:Path` を与えられたとき、そのファイル（`/var/lib/llm-gateway/daily-spend.json`）に置く。
  与えなければ従来どおりプロセス内だけ（`ProcessLocalLlmSpendStore`。ローカル・試験）。
- 形式は JSON（版・UTC の日付・鍵ごとの合計）。置き換えは一時ファイルへ書いて fsync し、名前を替える（途中で落ちても前の版か新しい版が残る）。
- 退けた案:
  - **基盤の Postgres にゲートウェイ用の DB と利用者を足す**: 利用者と DB の作成（Job）・OpenBao の値と ESO・MSP の名前空間から Postgres への
    NetworkPolicy・CA の検証・EF Core（またはドライバ）の導入と移行が要り、稼働中の Postgres と秘匿管理まで変更が及ぶ。保持したいのは
    「その日の数十個の数値」だけで、1 レプリカの前提では共有の置き場の利点（複数の書き手の一貫性）を使わない。障害の面も Postgres・TLS・資格情報の
    分だけ増える（どれが落ちても段 2 は送らない側へ倒れる）。
  - **Valkey 等の共有の置き場**: クラウドには持ち込んでいない（IADR-0674）。
  - **プロセス内のまま、起動の直後だけ止める（猶予）**: 再起動の後の消費が分からないので、どの値で再開しても戻る量を決められない。
- PVC は日次のバックアップの対象ではない（バックアップは Postgres だけ）。ノードを失って作り直すと、その日の枠は 0 から数え直す。提供元の支出上限（決定 5）が残る。

### 決定 2 — 前回の値を読めるまでは、段 2 で費用の上限を持つ呼び手の呼び出しを送らない（fail-closed）

- 起動の直後に前回の値を読む（`LlmCallLimiter.TryLoadState`）。読めなくても起動は止めない（readiness を落とすと、原因のログと計器ごと見えなくなる）。
  読めるまで、費用の上限を持つ呼び手の呼び出しは `Enforce` で送らない。種類 `cost_state` を足す（`llm.limit.kind` の値域は 7 値）。
  呼ばれるたびに読み直し、読めた時点で判定へ戻る。前の日の値は新しい日の枠に入れない。
- 読めないとみなすもの: ディレクトリが無い（ボリュームが付いていない。🔴 作らない ＝ 消える場所へ黙って書かない）・ファイルが壊れている・
  版・日付・鍵の形が違う・負の値・排他の鍵を取れない。ファイルが無いだけ（初めての起動）は 0 から始める。
- 読む前に置き場へ書かない（書くと前回の値を消す）。`Report`（段 1）は送って `cost_state` を数え、読めたら手元で数えた分を前回の値に足す。

### 決定 3 — 見込み額の予約は送る前に置き場へ書く。書けなければ段 2 では予約を戻して送らない

- 送ってから書けないと、再起動で消える消費が生まれる。予約を書けなかった呼び出しは `cost_state` で止め、要求数・費用の枠を消費しない。
- 実額への置き換え・戻し（送った後）を書けなかったときは、手元の値は正しいまま、次の書き込みで置き場が追いつく。
  置き場には予約（見込み額 ≧ 実額がふつう）が残るので、その間に再起動しても多めの側に倒れる。
- 判定と予約と書き込みは 1 つの錠の中で行う。同時の要求でも枠を越えて送らない（試験で固定）。書き込みは呼び出しごとに 1〜2 回で、
  要求数の上限（呼び手 300 / 分）の下では錠の待ちは小さい。

### 決定 4 — 書き手は 1 つ（1 レプリカ・Recreate・ファイルの排他の鍵）

- chart は `persistence` を持つサービスを Recreate で入れ替え、2 レプリカ以上・HPA（`scaling.services`）との併記を描画で止める。
  稼働中の Deployment（RollingUpdate の既定値を持つ）を `helm upgrade` で替えられるよう、`rollingUpdate: null` を併せて描く。
- ゲートウェイは置き場の隣の `.lock` を排他で開いたまま持つ（.NET は Unix で `flock(LOCK_EX|LOCK_NB)` を取る）。2 つ目のプロセスは鍵を取れず、
  `cost_state` で送らない。前のプロセスが終われば鍵が離れ、次の呼び出しで前回の値を読む。
- 🔴 **1 レプリカ・単一ノードが前提である。** 複数レプリカへ広げるときは、共有の置き場（決定 1 で退けた Postgres・Valkey）へ移す判断が要る。

### 決定 5 — 起動の時刻のゲージと、短い間の再起動の繰り返し・置き場の障害の警報。提供元の支出上限を稼働の前提条件にする

- 計器 `llm.gateway.start_time_seconds`（観測ゲージ・`{unix_second}`。Prometheus 側の名前 `llm_gateway_start_time_seconds`）。起動の直後に 1 回送り出す
  （既定の 60 秒を待つと、それより早く落ちるプロセスが数えられない）。
  - 「1 ずつ増えるカウンタ」にしなかった理由: プロセスごとに数え直すので、再起動のたびに 1 へ戻る系列は増えたことにならない。
    起動の時刻はプロセスごとに大きくなるので、`max` の変わった回数が、ラベル（`service.instance.id` はプロセスごと）が変わっても変わらなくても起動の回数になる。
- クラウドの警報（`deploy/cloud/observability/prometheus.yaml` の群 `llm-gateway-state`。promtool の単体試験は `scripts/cloud-ops.test.js`）:
  - `LlmGatewayRestartLoop`（warning）: `changes(max(llm_gateway_start_time_seconds)[30m:1m]) >= 3`（直近 30 分に 3 回以上の再起動）。
  - `LlmCostStateUnavailable`（critical）: 直近 10 分に `cost_state` で送らなかった呼び出しがある。
- 提供元のコンソールの支出上限を、クラウドの稼働の前提条件として手順書（`deploy/cloud/README.md` §3「前提条件」）に書いた。値は所有者が決め、リポジトリに書かない。
  ゲートウェイの上限はゲートウェイを通る呼び出しにしか効かない（ゲートウェイの不具合・鍵の漏れには効かない）。

## 結果・残余リスク

1. 置き場が壊れる・付かない・容量が尽きると、段 2 は取引ユニットの LLM の呼び出しを送らない（費用の上限を持つ呼び手のすべて）。
   `LlmCostStateUnavailable` が知らせる。壊れたファイルを退避すると、その日の枠は 0 から数え直す（手順書の分岐）。
2. 要求数の窓（60 秒）はプロセス内のままである（再起動で戻るが、窓が短いので費用への影響は小さい）。
3. ノードの電源断では、fsync の後の名前の替えがディレクトリに残る前に落ちると、1 つ前の版に戻り得る（直前の 1 件分）。
4. PVC は local-path（ノードのディスク）で、ノードを作り直すと消える。
5. 置き場を与えない構成（ローカル・試験）は従来どおり再起動で戻る。段 2 で置き場が無ければ起動の時に Warning を 1 行出す。
6. 合成監視・取引ユニットの側の振る舞いは変えない。`cost_state` で止まった呼び出しは、ほかの上限と同じ `Sent=false`・`FailureKind=limit_exceeded` で返る。

## 適用の順序（稼働中の構成）

1. ゲートウェイのイメージを、この変更を含む版でビルドしてノードへ取り込む（README §2）。
2. `kubectl apply -k deploy/cloud`（Prometheus の規則の ConfigMap）と、Prometheus の作り直し（`kubectl -n platform-infra rollout restart deploy/prometheus`）。
3. `helm upgrade` で chart を当てる。PVC が作られ、Deployment が Recreate で入れ替わる（**数十秒、LLM ゲートウェイが止まる**）。
   最初の起動では置き場のファイルが無いので、その日の枠は 0 から始まる（それまでのプロセス内の値は引き継げない）。
4. 起動のログ「当日の費用の枠を読み込んだ（durable=True …）」と、`llm_gateway_start_time_seconds` の系列が出ることを確かめる。
