---
title: IADR-0530 h2c の実測でブラウザから発火できない 4 経路を、既定無効の計測用の構成で発火させる。L-4・D-3・D-2 は BFF に管理者限定の生成の口を構成の鍵があるときだけ載せ、N-1 は個人資料の定期処理の初回を前倒しする
type: impl-adr
status: Accepted
related_ids: [NFR-16, FR-18, FR-19, FR-22, ADR-0117, ADR-0051, ADR-0037, ADR-0034, IADR-0300, IADR-0380, IADR-0431]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0117（east-west の gRPC。REST 退役の前提の実測）
  - planning:projects/microservices-platform/07_adr/ADR-0051（AI 提案の生成。1 実行 = 1 利用者のスコープ）
  - planning:projects/microservices-platform/07_adr/ADR-0037（個人資料。通知の 3 契機）
related_specs:
  - ../specs/20261010_1887_h2c-measurement-triggers.md
---

# IADR-0530: 計測用の発火手段を既定無効の構成で置く（#1887）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-10
- 決定者: **利用者（製品の判断）**。オーナー判断 2026-10-10「計測用の発火手段を実装セッションに頼む。計測専用・既定は無効・計測の窓だけ明示の構成で有効・面は最小・安全を優先」（#1887・#1255 / #1517 のコメント）。手段の選択・鍵の名前・試験の形は claude。

## 起点・関連

- 起点 issue: **#1887**。REST 退役（#1255 残射程 2・#1517）は全経路を稼働 k3s で実測してから進める（オーナー判断 2026-10-10）。
  コードから引くと、L-4（graph → llmgateway `LlmCompletion/Complete`）・D-3（graph → document `TagDictionary/ListNames`）・D-2（graph → document `DocumentTagWrite/AddTag`）・
  N-1（document → notification `NotificationIngress/Accept`）はブラウザの操作で発火できない。
- 計画: NFR-16・ADR-0117。生成の起動について計画は導線を置いていない（[IADR-0300](./IADR-0300_ai-suggestion-approval-bff.md) が「生成は開けない」とした理由）。
- 前提: [IADR-0380](./IADR-0380_term-overlap-similarity-candidate-source.md)（類似度候補の既定は語の共起）、[IADR-0431](./IADR-0431_departed-owner-private-note-disposal.md)（日次の定期処理）。
- 作業仕様書: [20261010_1887_h2c-measurement-triggers](../specs/20261010_1887_h2c-measurement-triggers.md)（母集合・受け入れ基準）。

## コンテキストと課題

- **L-4・D-3**: 生成の口 `POST /graph/suggestions/generate/{documentId}` は GraphService に在るが、BFF に出ていない（意図的。IADR-0276・IADR-0300）。
  D-3 は同じ要求の中で L-4 の直前にだけ呼ばれる。
- **D-2**: 承認の口は BFF に在るが、保留中の**タグ**提案を作れるのは L-4 の応答だけである。
- **N-1**: 個人資料の共有では送らない。送るのは容量の 80% / 95% の跨ぎ（同期 push・完全削除の時）と、日次の定期処理（削除予告・週次・トークン期限）だけで、
  定期処理の初回は起動の 24 時間後であり、設定の鍵が無い。
- オーナーの制約: 計測専用・既定は無効・面は最小。課金の経路は各モード 1 回まで。

## 決定

### 決定 1 — L-4・D-3・D-2: BFF に「計測専用の生成の口」を、構成の鍵があるときだけ載せる

- 鍵は BFF の `Measurement:EnableSuggestionGenerate`（env `Measurement__EnableSuggestionGenerate`。helm `measurement.suggestionGenerate`、既定 `false`）。
  **偽・未設定なら端点を Map しない**（ルート表に載らず 404）。合成時に読むので、切り替えは BFF の再起動を伴う（helm の env の変更で作り直される）。
  値が bool として読めなければ BFF の起動が落ちる（`GetValue<bool>`）。黙って無効へ倒さない。
- 端点は `POST /bff/graph/suggestions/generate/{documentId:guid}`。後段は GraphService の既存の生成の口（**変更しない**）で、承認・却下と同じ
  `ForwardAsync`（資格情報の伝播・本文と状態コードの透過）で中継する。**新しい後段の口・新しいサービス間の権限は作らない。**
- **システム管理者限定**（端点に `PlatformAuthPolicies.AdminOnly` を積む。群の認証と AND で合成され、実効は platform-admin だけ。運用者・一般利用者は 403）。
  後段の ABAC（起点が見えなければ 404。1 実行 = 1 利用者のスコープ）はそのまま効く。
- 有効なときは BFF の合成時に警告を 1 行出す（計測の窓の外で残っていれば気付ける）。
- OpenAPI（`docs/api/openapi.yaml`）にも載せる。BFF の実効ロールと契約の `x-roles` を突き合わせる検査器（`check-bff-authz-docs`）が端点を静的に読むため、
  載せないと赤になる。説明に「既定は無効・製品の口ではない」を書く。orval の生成物はリポジトリの手順（`pnpm run codegen`）で再生成した。画面からは呼ばない。

**却下した案**:
- GraphService の口を直接叩く（`kubectl port-forward` ＋ 利用者の JWT）: BFF セッション方式では利用者の JWT がブラウザに無く、取り出す手段を足すほうが面が広い。
- 新しい「計測用の後段の口」: 後段に計測専用の書き込みの口を足すことになり、本番の生成の経路も通らない。
- 生成を定期実行・起動時実行にする: 1 利用者のスコープ（ADR-0051 決定 4）を満たす主体が居ない。

### 決定 2 — 候補が無いときに LLM まで届かせる別経路は作らない（前提を runbook に書く）

- 生成器（`AiSuggestionGenerator`）は類似の候補が 0 件か、スコープで全部落ちたら `[]` を返し、**D-3 も L-4 も呼ばない**。
- ただし**類似度候補の既定の供給元は語の共起**（`TermOverlapSimilarityCandidateSource`。IADR-0380）であり、graph 自身の DB（`graph_documents` と語の表）だけを読む。
  **ベクトル索引も Voyage の鍵も使わない。** 稼働の「Voyage の鍵が無く索引が空」は生成の前提に関係しない。`deterministic-hash-v1` も要らない。
- 前提は「管理者から見える、表題か本文の語を共有する別の文書が、グラフに載っている」こと（似ている度合いの下限 0.1）。runbook §3.2.1 に、例（3 文書）と、
  モードごとに起点を変える理由（提案済みの組・辺のある組は候補から外れる）と、D-2 に要るタグ辞書の値を書いた。
- 「計測モードでは候補なしでも LLM を呼ぶ」経路は作らない。生成の意味（候補を LLM に選ばせる）を計測のために歪めると、本番で通らない経路を測ることになる。

### 決定 3 — N-1: 日次の定期処理の初回を、構成で 1 回だけ前倒しする（本番の契機・本番の経路）

- 鍵は DocumentService の `PrivateNotes:Maintenance:InitialRunDelaySeconds`（env `PrivateNotes__Maintenance__InitialRunDelaySeconds`。
  helm `measurement.privateNoteMaintenanceInitialRunDelaySeconds`、既定 `0` ＝ 描画しない）。
- 設定したときだけ、起動の N 秒後に**本物の周期**（`PrivateNoteMaintenanceService.RunAsync`）を 1 回走らせ、以後は従前どおり 24 時間ごとに走る。
  前倒しの 1 回の後の周期は、その 1 回が終わってから作る新しい `PeriodicTimer` で数えるので、次は前倒しの周期の終わりから 24 時間後になる（計測には差し支えない。受け入れる）。
  周期の中身・判定・発火記録は変えない。論理削除済みの個人資料を持つ所有者へ週次の通知（①-a）が出て、`GrpcPrivateNoteNotifier` が `NotificationIngress/Accept` を呼ぶ。
- 値は整数（InvariantCulture）で 1〜86400 だけを採る。0・負・小数・単位つき・上限超は**起動を止める**（[IADR-0528](./IADR-0528_anthropic-http-timeout-config.md) の「不正は既定へ倒す」と
  逆である。あちらは本番の調整値で、止めると製品が止まる。こちらは計測者が明示に入れる値で、黙って既定へ倒すと「前倒しが効いた」と思ったまま 24 時間待つことになる）。
  未設定・空白は従前どおり。構成は解決時に読む（`Program.cs` の登録で `ResolveInitialRunDelay`）。
- 有効なときは起動時に警告を 1 行出す。
- 前倒しの 1 回は本番の周期そのものなので、そのとき対象に当たる他の処理（退職者の完全削除・90 日の削除・削除予告・トークン期限・同期履歴の削除）も走る。
  いずれも 24 時間以内に本番の周期が行うことを数十秒早めるだけで、発火記録で二重には送らない。**受け入れる。**
- 週次の通知は所有者ごとに 7 日に 1 通なので、2 つのモードでは別の利用者で材料を作る（runbook §3.2.2）。

**却下した案**:
- 通知を 1 件送る管理の口: 発火の契機が本番に無い経路になり、新しい書き込みの口（BFF ＋ 後段）を 2 つ足す。gRPC の往復は同じでも、面が増える。
- 容量の警告のしきい値・上限を構成で下げる: 画面から作る個人資料は 0 バイトで、使用量を上げるには Obsidian の同期が要る。80% / 95% の発火記録は下回るまで
  再武装しないので、2 つ目のモードで発火させるには使用量を一度下げる操作も要る。しきい値の構成は製品の判定（FR-22 ②）にも触れる。
- 定期処理の周期そのものを構成で短くする: 週次・7 日の判定は時刻で決まるので、周期を短くしても 2 回目の通知は出ない。製品の周期に触れる面だけが増える。

### 決定 4 — helm の `measurement.*` は既定で無効、既定の描画は 1 バイトも変えない

- `values.yaml` に `measurement.suggestionGenerate: false` と `measurement.privateNoteMaintenanceInitialRunDelaySeconds: 0` を置き、真・正の値のときだけ
  `deployment.yaml` が bff・document に env を描く（注記つき）。既定の `helm template` の差分は 0 行（手元で実測。v3.16.4・`values-local.yaml`）。
- 外し方は runbook §6.1（保存した利用者値で `helm upgrade`）で、外れたことを `helm get values`・Deployment の env・BFF の 404 の 3 つで確かめる。

## 結果

- PoC は runbook §3.2.1・§3.2.2 の手順で、L-4・D-3・D-2・N-1 を各モード 1 回ずつ発火させられる。L-4 は課金（各モード 1 回まで）。
- 既定の構成では挙動が 1 つも変わらない（BFF のルート表・定期処理の初回の時刻・描画）。

## 残余リスク

- **鍵の残置**: 計測の後に `measurement.*` を外し忘れると、管理者が LLM の費用の出る口を叩ける状態と、再起動のたびに定期処理が前倒しで走る状態が続く。
  外す手順と確かめ方を runbook §6.1 に置き、有効なサービスは起動時に警告を出す。機械の検査（稼働値の監視）は置かない（同型の事故が起きていない）。
- **BFF に LLM 経路の流量制限は無い**（AI 分析・図の変換も同じ）。本口の歯止めは管理者限定・既定無効と、ゲートウェイの用途別の月次上限の警報である。
- **D-2 の材料は LLM の応答次第である**: タグ辞書に当てはまる値があっても、LLM がタグを提案しなければ D-2 の材料が生まれない。もう一方のモードの回の
  タグ提案を保留のまま残して承認してよい（runbook §3.2.1）。それでも無ければ「未測定」と書く。
