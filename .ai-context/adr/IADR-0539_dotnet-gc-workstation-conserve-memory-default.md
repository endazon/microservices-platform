---
title: IADR-0539 MSP の .NET サービスへ AST が意図した GC の設定（Workstation GC・GCConserveMemory=5・GCHeapHardLimitPercent=0x3C＝60%。env の値は 16 進数）をチャートの共通の値として本番の既定で入れる。glibc の MALLOC_* は入れない。あわせて経路 B の Keycloak のヒープと Tempo・Loki の Go のヒープを絞る
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, ADR-0006, ADR-0007, ADR-0008, ADR-0111, IADR-0066, IADR-0077, IADR-0210]
author: claude
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md
  - planning:projects/microservices-platform/07_adr/ADR-0111_reset-floor-replicas-and-no-bypass-on-failure.md
related_specs:
  - ../specs/20261014_1927_reduce-resident-memory.md
---

# IADR-0539: MSP の .NET サービスへ AST と同じ GC の設定を本番の既定で入れる（#1927）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-14
- 決定者: claude（オーナーの依頼 #1927。本番の既定にするかの判断を本 IADR に委ねられた）

## 起点・関連

- 起点 issue: #1927（PoC のノードのメモリが 87% に張り付く）。関連: #1919（requests / limits の静的な棚卸し。所見 ①②）。
- 関連する計画書 ID: 無採番の `NFR`（運用の前提）。NFR-05（可用性）は reset-floor を変えない理由として引く。
- 関連する計画 ADR: ADR-0007（Helm values は環境ごとに上書き可能）・ADR-0008（k3s）・ADR-0006（観測スタック）・ADR-0111（reset-floor は 2 レプリカ以上）
- 関連する実装 ADR: [IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（経路 B）・[IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md)（観測スタックの opt-in）・
  [IADR-0210](./IADR-0210_local-k8s-observability-persistence.md)（Prometheus の保持 35d は変えない）
- 関連する実装仕様書: `.ai-context/specs/20261014_1927_reduce-resident-memory.md`（母集合・判断表・見込みの計算）
- 先例: AST `deploy/helm/ai-stock-trading/templates/deployment.yaml`（AST#778・AST#782 で全 .NET サービスへ無条件に入れた 3 つの env。AST#808・AST#811 の MALLOC_* の実測）
- 採番: `origin/develop` `764bd780` の最大は IADR-0536。開いている PR #1925 が IADR-0537、#1926 が IADR-0538 を使う。本件は **0539**（マージは #1925 → #1926 → 本 PR の順）。

## コンテキストと課題

- PoC のノード（WSL2 の上限 15.5 GiB）で 13.7 GiB（87%）。MSP の名前空間が 4,313 MiB で最大。上位は keycloak 787・graph 498・wiki 406・document 280・ingestion 262・tempo 260 MiB。
- **MSP の .NET 15 サービスには GC の設定が 1 つも無い**（チャート・Dockerfile・csproj を走査して 0 件）。ASP.NET Core の既定は Server GC で、ヒープ上限は memory の limits の 75%。
- AST の .NET 11 サービスは同じ requests / limits（128Mi / 512Mi）で 3 つの env を持ち、平均 ≈150 MiB（#1927）。
- 決めること: (1) 入れるか、(2) 入れる場所（チャートの共通／サービスごと／イメージ）、(3) **本番の既定にするか、経路 B（`values-local.yaml`）だけにするか**、(4) AST の MALLOC_* も入れるか。

## 検討した選択肢

| 案 | 中身 | 本番 | 経路 B | 評価 |
| --- | --- | --- | --- | --- |
| A | `values-local.yaml` だけで与える | 既定（Server GC・75%） | 3 つの env | 本番と経路 B で GC の挙動が分かれ、経路 B で測った RSS が本番の見積りにならない。values-local は `services.<名>` の上書きなので 15 サービスぶん書くことになる |
| **B（採用）** | チャートの `global.dotnetGc` から `templates/deployment.yaml` が全 .NET へ描く。サービス単位で上書き可 | 3 つの env | 同じ | 1 か所で全 .NET に効く。環境ごとの差し替えは ADR-0007 の values の上書きで済む |
| C | イメージに焼く（csproj の `ServerGarbageCollection=false`・Dockerfile の `ENV`） | 3 つの env | 同じ | `GCHeapHardLimitPercent` は配備の limits と対で決まる値で、イメージに焼くと環境ごとに変えられない。サービスごとに 14 の Dockerfile を触る |

## 決定

1. **案 B。** `global.dotnetGc`（`gcServer: "0"`・`conserveMemory: "5"`・`heapHardLimitPercent: "0x3C"`）を `values.yaml` に置き、`templates/deployment.yaml` が
   `services` の各 Deployment の env に `DOTNET_gcServer`・`DOTNET_GCConserveMemory`・`DOTNET_GCHeapHardLimitPercent` を描く。**本番の既定である。**
2. サービス単位の上書きは `services.<名>.dotnetGc`（鍵ごと）。**空文字の鍵は描かない**（そのサービスだけ .NET の既定へ戻す退路）。
   `extraEnv` / `extraEnvAppend` に同じ名前があれば**描画で落とす**（同名の env が 2 つ並ぶと、どちらが効くかが字面から読めない）。
3. **`MALLOC_ARENA_MAX`・`MALLOC_MMAP_THRESHOLD_`・`MALLOC_TRIM_THRESHOLD_` は入れない。**
4. 経路 B の付随の決定（常駐を下げる同じ目的。いずれも本番像には存在しない部品）:
   - Keycloak: `JAVA_OPTS_KC_HEAP` を `-XX:MaxRAMPercentage=40 -XX:MinHeapFreeRatio=10 -XX:MaxHeapFreeRatio=20 -XX:G1PeriodicGCInterval=60000` にする（limits 2Gi は据え置き）。
   - Tempo・Loki: 本体のコンテナに `GOMEMLIMIT=192MiB`。Loki の ingester の `chunk_idle_period: 5m`・`max_chunk_age: 30m`（compose も同じ値）。
   - **変えないもの**: Tempo の保持（既に 1h）・Tempo の停止・Prometheus の保持と取り込み・reset-floor のレプリカ数（計画 ADR-0111 決定 1）・wiki-js と mailpit の有効・Istio のサイドカー。理由は作業仕様書の判断表。

## 理由

### 本番の既定にする理由（スループットとの釣り合い）

- **本番の CPU の limits は 500m（aianalysis だけ 1000m）である**（`values.yaml` は本番像そのもの）。.NET は cgroup の CPU の上限から `Environment.ProcessorCount` を 1 と見なすので、
  Server GC の利点（コアごとのヒープと並列の GC）は働かず、Server GC の大きな世代 0 の予算と遅いヒープの返却だけが残る。Workstation GC（並行 GC は既定で有効）に替えて失うスループットは
  1 コアでは小さい（推測。実測は PoC の前後で見る）。
- **本番の処理量は Pod 内の GC の並列ではなく、HPA の水平の増減で稼ぐ設計である**（`scaling.services`・`hpa`）。1 Pod の RSS が下がれば、同じノードに載る Pod の数が増える。
- `GCConserveMemory=5` は断片化が大きいときに圧縮を増やす（CPU を少し払ってメモリを返す）。0〜9 の中間で、AST が選んだ値である。
- `GCHeapHardLimitPercent=0x3C`（16 進数で 60。512Mi で ≈307Mi）は、ネイティブの分（スレッド・JIT・**Wolverine の実行時コード生成**）の余地を limits の内側に残す。
  MSP の 7 サービス（conversion・datasource・document・graph・ingestion・retrieval・wiki）は計画が codegen の事前生成を禁じており（#1919 の所見 ②）、GC ヒープの外が伸びうる。
  既定の 75% のままだと、GC ヒープが上限に届く前にネイティブ分と合わせて limits を超え、OOMKilled（137）になる —— AST#778 / AST#782 が実測した形である。
- **AST の実績**: 同じ 3 つの env を全 .NET サービスへ無条件に描き（ただし HeapHardLimitPercent は `"60"`＝実効 96% で、上限は実質 limits の近くにある。下の「値を 16 進数で書く理由」）（テンプレートに固定＝本番の既定）、PoC で 1 か月運用して平均 ≈150 MiB。AST の記録（AST#778・AST#782・AST#808・AST#811）は
  RSS と OOMKilled の件であり、3 つの env によるスループットの劣化は論点に上がっていない。
- 案 A（経路 B だけ）は、PoC の実測で得た数字が本番の見積りに使えなくなる。両方で同じにしておき、本番で変えたいときは values の上書き（ADR-0007）で戻せる方を採る。

### 値を 16 進数で書く理由（［2026-10-14 / #1928 のレビュー］）

- **GC の設定を環境変数で与えると、値は 16 進数として読まれる。** Microsoft Learn「Garbage collector config settings」
  （https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector）の表は「Environment variable | 0xn or n | n is interpreted as a hex value in either format」と書き、
  `GCHeapHardLimitPercent` の例を「30% → 0x1E or 1E」とする（runtimeconfig.json の値は 10 進）。
- 初版は AST に倣って `"60"` と書いていた。これは **0x60＝96%** で、上限は 512Mi で ≈492Mi になり、ネイティブの分の余地を残す狙いが成り立たない。
  **`"0x3C"`（60%）と書き、16 進数であることを値から読めるようにした。** 1 桁の `gcServer "0"`・`conserveMemory "5"` は 10 進と同じ値なので変えない。
- `scripts/resident-memory.test.js` が描画した値を 16 進数として読んで 60 になること、2 桁以上の値に `0x` があることを固定する（`"60"` に戻す変異で赤）。

### MALLOC_* を入れない理由

- AST#808 → AST#810 → AST#812 → AST#813 の実測で、3 つとも**保持されるメモリの総量を減らさなかった**（保持先がアリーナ → [heap] → mmap 領域へ移っただけ）。
  伸びを止めたのは codegen の事前生成（AST#811）である。AST は「無害なので据え置く」としているが、効かないと分かっている設定を新しく入れる理由は無い。

### Keycloak・Tempo・Loki

- Keycloak 26.7.4 の `kc.sh` は G1 を使い、「G1 は最大ヒープまで取りに行きがち」と自ら書く。起動のたびの augmentation（`start-dev`）でヒープを広げ、既定の空き率の上限（70%）では返さない。
  上限を ≈1,024Mi → ≈819Mi に下げ、空き率の上限 20% と周期 GC（60 秒）で、暇な間に返させる。非ヒープ（metaspace ≤256m・コードキャッシュ ≤240m・スレッド）を足しても ≈1.4Gi で limits 2Gi の内側。
  上限を下げすぎると augmentation が Java のヒープ不足になり、`-XX:+ExitOnOutOfMemoryError`（kc.sh の既定）で落ち続ける。1Gi の limits 時代の既定（70%＝≈717Mi）より上に留めた。
- `GOMEMLIMIT` はソフトな上限で、超えても落ちない（GC の CPU は Go の実行時が 50% で頭打ちにする）。目標の手前で GC を回し、返せるページを OS へ返させる。
  Tempo の実測 260 MiB に対して 192MiB。Prometheus には入れない（メモリはほぼ head の系列で決まり、H-1 / AST#690 の系列を落とさずに減らせる量が小さい）。
- Loki の ingester は書き出し前のチャンクを既定で最大 2 時間メモリに持つ。30 分へ縮めても保持（ディスク）は変わらない。

## 結果

- 良い影響: .NET 15 の常駐が下がる見込み（静的な見込み −0.5〜−1.0 GiB）。Keycloak −0.2〜−0.3 GiB、Tempo・Loki −0.07〜−0.14 GiB。合計 −0.8〜−1.4 GiB。
  本番と経路 B で GC の挙動が揃い、PoC の実測が本番の見積りに使える。
- 悪い影響・トレードオフ:
  - **GC ヒープが 60% に届いたサービスは OOMKilled ではなく `OutOfMemoryException` になる**（プロセスは例外の扱い次第で生き残るか落ちる）。
    候補は graph（実測 498 MiB。H-1 のナレッジ健全性の集計で約 4 万件を扱う）。そのときは `services.graph.dotnetGc.heapHardLimitPercent` で上げるか空文字で既定へ戻す。
  - Workstation GC・ConserveMemory による GC の CPU の増加（小さい見込み。未実測）。
  - **見込みの合計では、15.5 GiB の上限で 70% 以下（≈10.9 GiB）には届かない**（13.7 → 12.3〜12.9 GiB）。`.wslconfig` の 24GB（#1919）が要る。
- フォローアップ:
  - PoC が配備の前後に `kubectl top node`・`kubectl top pod -A --containers` を取り、#1927 に書く。graph の `OutOfMemoryException`・再起動の有無も見る。
  - 実測が出たら requests を実使用量へ寄せる（#1919 の見直し案）。
  - **AST のチャートは同じ誤りを持つ**（`DOTNET_GCHeapHardLimitPercent` が `"60"`＝16 進数で 96%）。AST の「60% に固定した」という記録（AST#782）は実効値と食い違う。
    AST 側はオーケストレーターが別の issue で直す（本 PR の射程外）。
  - さらに要るときの候補（本 IADR では採らない）: Tempo を既定で止める（Grafana・SC-10・NetworkPolicy・永続化が前提にしている）、Istio の `Sidecar` で egress の宛先を絞る（宛先の棚卸しが要る）、
    Keycloak を `start --optimized` にして augmentation を無くす。

## 関連

- Supersedes: なし
- Superseded by: なし
