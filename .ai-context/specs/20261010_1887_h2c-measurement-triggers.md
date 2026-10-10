---
title: h2c の往復の実測で、ブラウザから発火できない 4 経路（L-4・D-3・D-2・N-1）を計測用に発火させる手段を置く
type: spec
status: done
related_ids: [NFR-16, FR-18, FR-19, FR-22, ADR-0117, ADR-0051, ADR-0037]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs: []
issue: "#1887"
---

# 仕様書: 計測用の発火手段（L-4・D-3・D-2・N-1。#1887）

## 起点

- 要求: `NFR-16`（east-west の通信の運用性）・計画 `ADR-0117`。オーナー判断（2026-10-10）「REST 退役（#1255 残射程 2・#1517）は全経路を稼働 k3s で実測してから」。
- 根拠: #1887 本文（c0dfd319 時点で、4 経路はブラウザの操作で発火できない）と、#1255 / #1517 のオーナー判断のコメント（G-1 は測定から外す・課金経路は各モード 1 回まで）。
- オーナーの制約: **計測専用・既定は無効・計測の窓だけ明示の構成で有効・面は最小・安全を優先**。
- 実装 ADR: [IADR-0530](../adr/IADR-0530_h2c-measurement-trigger-flags.md)。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | BFF の構成の鍵 `Measurement:EnableSuggestionGenerate` が無い・偽なら、`POST /bff/graph/suggestions/generate/{id}` はルート表に載らず 404。後段は呼ばれない | `BffMeasurementSuggestionGenerateTests.鍵が無いか偽なら口は無く404で後段へ行かない`・既存の `BffGraphSuggestionTests.No_bulk_approval_route_for_suggestions_is_exposed_by_the_bff`（既定で生成が無い） |
| 2 | 鍵が真なら、システム管理者だけが後段 `/graph/suggestions/generate/{id}` へ資格情報つきで届き、本文・状態コードは透過される | `鍵が真なら管理者は後段の生成の口へ資格情報つきで届き本文が素通りする`・`後段の404はそのまま返す` |
| 3 | 鍵が真でも、運用者・一般利用者は 403、未認証は 401。後段へ行かない | `鍵が真でも管理者以外は403で後段へ行かない`・`鍵が真でも未認証は401` |
| 4 | DocumentService の `PrivateNotes:Maintenance:InitialRunDelaySeconds` が未設定なら、定期処理の初回は従前どおり 1 周期後 | `既定の構成では前倒しは入らない`・既存の `周期の失敗が続いても次の拍まで待ってから再び判定する`（初回は 1 拍目を待つ） |
| 5 | 設定したときだけ、起動の N 秒後に本物の周期が 1 回走り、論理削除済みの資料の所有者へ週次の通知が出る | `前倒しを設定すると起動のN秒後に1回走り週次の通知が出る` |
| 6 | その通知は本番の gRPC 実装（`GrpcPrivateNoteNotifier`）で `NotificationIngress/Accept` へ届く（N-1 の経路そのもの） | `周期の通知は本番のgRPC実装でNotificationIngressのAcceptへ届く` |
| 7 | 不正な値（0・負・小数・単位つき・上限 86400 超）は起動を止める。未設定・空白は従前どおり | `前倒しの秒数を構成から読む`・`不正な前倒しの値は起動を止める` |
| 8 | helm の `measurement.*` は既定で無効で、既定の描画は 1 バイトも変わらない。有効にしたときだけ bff・document へ env を描く | 手元の `helm template` の差分（下の §検証） |
| 9 | 有効にしたサービスは起動時に警告を 1 行出す | コードの確認（BFF の合成時・定期処理の起動時） |
| 10 | runbook §1.2 の「発火」の欄が手段に書き換わり、オフに戻したことの確かめ方（`helm get values`・Deployment の env・404）がある | 文書 |

## 設計（決めたこと。理由は IADR-0530）

1. **L-4・D-3・D-2**: GraphService の既存の生成の口（変更なし）を、BFF に**構成の鍵があるときだけ**載せる。`AdminOnly` を端点に積む（群の認証と AND）。
   本文・状態コードは承認・却下と同じ `ForwardAsync` で透過する。新しい後段の口は作らない。
2. **L-4 の前提の調査結果**: 生成器（`AiSuggestionGenerator`）は類似の候補が 0 件なら D-3・L-4 の前に `[]` を返す。ただし候補の供給元の既定は**語の共起**
   （`TermOverlapSimilarityCandidateSource`。IADR-0380）であり、**ベクトル索引も Voyage の鍵も使わない**。前提は「管理者から見える、語を共有する文書が 2 件以上
   グラフに載っている」だけである。計測用に候補なしで LLM を呼ぶ別経路は作らない（製品の意味を歪めない）。`deterministic-hash-v1` は要らない。
3. **N-1**: 日次の定期処理に**初回の前倒し**（秒数）を足す。前倒しの 1 回は本物の周期で、週次の通知 ①-a が `GrpcPrivateNoteNotifier` を通る。
   「通知を 1 件送る管理の口」は採らない（新しい書き込みの口を足さずに、本番の契機と本番の経路で発火できるため）。容量のしきい値を下げる案も採らない
   （画面から作る個人資料は 0 バイトで、使用量を上げるには Obsidian の同期が要る。しかも 80% / 95% の発火記録は下回るまで再武装しない）。
4. **helm**: `measurement.suggestionGenerate: false` / `measurement.privateNoteMaintenanceInitialRunDelaySeconds: 0` を足し、真・正の値のときだけ bff・document に env を描く。
5. **OpenAPI**: 検査器 `check-bff-authz-docs` は BFF の端点を静的に読むので、計測の口も `x-roles: [platform-admin]` つきで載せた（説明に「既定は無効」を書く）。
   orval の生成物（graph のフック・MSW・faker）は `pnpm run codegen` で再生成した（手で書いていない）。

## 母集合（規則 9。誤りの側の文字列で走査した）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `git grep -n "suggestions/generate\|generate/{documentId}" -- ':!.ai-context/specs' ':!CHANGELOG.md'` | IADR-0272・0276・0300・0380（凍結）、`docs/api/openapi.yaml` の注記、`docs/tests/FR-18_ai-suggestions.md` の T-49、`GraphBffEndpoints.cs` の注記、GraphService の試験 3 件 | 凍結の IADR は書き換えない（IADR-0530 が例外を記録する）。openapi の注記・`GraphBffEndpoints.cs` の注記に「例外は計測の口 1 本」を追記。T-49 と GraphService の試験は後段の口の話で変わらない |
| `git grep -n "生成の口は引き続き公開しない\|生成（.generate" -- src docs` | `BffGraphSuggestionTests.cs`（ルート表の走査）・`GraphBffEndpoints.cs` | 走査の主張は「既定の構成では」として成り立つ。注記を足した |
| `git grep -n "個人資料の共有（通知の送出）\|初回実行は起動から 1 周期後"` | runbook §1.2 の N-1・`PrivateNoteMaintenanceService.cs`（・graph の `KnowledgeHealthHostedService.cs`） | runbook を直した。document の注記に前倒しを追記。graph の H-1 は本件の対象外（runbook §5.3 の段取り 1 で測る） |
| `git grep -n "PrivateNoteMaintenanceHostedService"` | `Program.cs`・本体・試験 1 ファイル | 登録を構成から読む形へ変えた。既存の試験は直接 new しており影響なし |
| runbook の `render-after`・`helm upgrade`・`render-now` の行 | §2.3・§2.4・§6.2 | `measurement.json` を足した（§6.2 は `$MEAS`）。§6.1 は保存した利用者値で戻るので外れる |

## 規則 10・11

- 規則 10: 追記で新たに誤りになる記述を引き直した —— runbook §2.3 の期待値（`added=26/28`）は `measurement.json` で 6 行増えるので書き足した。§2.4 の「13 サービス（＋ B-1 なら bff）」は
  計測用の値でも bff が作り直されるので直した。`BffGraphSuggestionTests` の「生成の口は引き続き公開しない」は既定の構成でだけ真なので注記した。
- 規則 11: 窓（前倒しの秒数と、材料を作る時刻・T0）を扱う。増える側（§2.4 の適用で document が作り直され、60 秒後に 1 回走る）と減る側（材料を作る前に走ると通知が出ない・
  週次の発火記録で 2 回目が出ない）の両方を runbook §3.2.2 に書いた。材料はモードの回の中で作り、`document-service` の再起動で走らせる形に決めた。

## 検証

- `dotnet build` / `dotnet test`（platform・knowledge の両 slnx）・`dotnet format --verify-no-changes`
- `pnpm run codegen`（差分は graph の生成物 3 ファイルだけ）・`pnpm run typecheck`・`pnpm run lint`・`node scripts/check-knip.js --require`
- `helm template`（v3.16.4。`deploy/local/values-local.yaml`）: 既定で描画差分 0 行、`--set measurement.suggestionGenerate=true --set measurement.privateNoteMaintenanceInitialRunDelaySeconds=120` で
  bff・document に 3 行ずつ（注記・`name:`・`value:`）。
- node の検査器（check-trace-blocks・gen-knowledge-graph --check・check-commit-messages・check-cross-repo-refs・check-adr-numbering・check-test-traceability・
  check-test-spec-coverage・check-doc-links・check-deploy-manifests・check-bff-authz-docs）。
- 稼働クラスタでの発火はしていない（クラウドのセッションからは触れない）。PoC が runbook §3.2.1・§3.2.2 で行う。
