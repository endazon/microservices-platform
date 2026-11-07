---
title: 作業仕様書 — LLM ゲートウェイの当日の費用の枠を再起動を越えて残す（置き場・fail-closed・起動の計器と警報・提供元の支出上限）
type: spec
status: done
related_ids: [NFR-18, NFR-21, FR-11, ADR-0044, IADR-0678, IADR-0692, IADR-0674, IADR-0675, IADR-0698]
author: claude
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 作業仕様書 — LLM ゲートウェイの当日の費用の枠を再起動を越えて残す

> 本仕様書は、実装の前に作成した（着手 2026-11-07。develop `3d36d5ee9`）。決定は IADR-0698。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR-18（統制）・NFR-21（費用と運用の観測）。機能要求: FR-11（LLM 費用の計測）。計画 ADR-0044。
- 画面（SC）・ユースケース（UC）: なし。
- 関連 IADR: IADR-0678（送る前の上限）・IADR-0692（クラウドの当日の上限 合計 20 USD）・IADR-0674（クラウドの最小構成）・IADR-0675（クラウドの警報）。
- 所有者の裁定（2026-11-07）: 再起動しても残るように直す。提供元のコンソールの支出上限はオーナーが設定する。

## 現状の確認（変更の前に実測）

- `LlmCallLimiter` の当日の費用は `Dictionary<string, CostBucket>`（プロセス内）。再起動で 0 に戻る（IADR-0678 結果 2・IADR-0692 結果 1 が残余として記録）。
- ゲートウェイは DB を持たない（`LlmGateway.csproj` に EF Core・Npgsql が無い）。クラウドの Postgres は取引ユニットと Keycloak の DB だけで、
  MSP の名前空間から Postgres への NetworkPolicy も無い。
- クラウドのゲートウェイは 1 レプリカ・HPA なし・`readOnlyRootFilesystem: true`（書けるのは /tmp の emptyDir だけ）。
- メトリクスは OTel SDK → collector（remote write）→ Prometheus。リソースの `service.instance.id` はプロセスごと（`AddService` の既定）。送り出しの既定は 60 秒。
- 試験の器（`WebApplicationFactory`）が後から足す設定は、`Program.cs` が `builder.Configuration` を Build の前に読むと効かない（実測: 置き場の試験が送ってしまった）。
  → 置き場の選択は DI の解決の時に `IConfiguration` から読む。

## 受け入れ基準と試験の写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | 新しいインスタンス（同じ置き場）が前回の消費を引き継ぐ。実額へ置き換えた値・戻した見込みも引き継ぐ | `LlmCallLimiterPersistenceTests.新しいインスタンスが前回の消費を引き継ぎ_枠が0に戻らない`・`置き換えた実額を引き継ぐ_予約のままでは残らない`・`戻した見込みは引き継がない`・`CallLimitEndpointTests.置き場を与えると_ゲートウェイを作り直しても当日の費用の枠を引き継ぐ`（ホストを作り直す） |
| AC2 | 日の境界（UTC の 0 時）: 前の日の値は新しい日の枠に入らない。日を跨いで書いた置き場は新しい日の値だけを持つ | `前の日の消費は新しい日の枠に入らない`・`日を跨いで書いた置き場は新しい日の値だけを持つ` |
| AC3 | 前回の値を読めるまで、段 2 は費用の上限を持つ呼び手の呼び出しを送らない（`cost_state`）。読めたら判定へ戻る | `前回の値を読めるまで段2では送らず_読めたら判定へ戻る`・`置き場のファイルが壊れていれば段2では送らない`・`形の違うファイルは読めないとして扱う`・`置き場のディレクトリが無ければ作らずに段2では送らない`・`CallLimitEndpointTests.置き場のディレクトリが無ければ起動はするが段2では費用を数える呼び出しを送らない` |
| AC4 | 置き場へ書けなければ段 2 は送らず、予約を残さない | `予約を書けなければ段2では送らず_予約も残さない` |
| AC5 | 段 1 は置き場の障害でも送り、`cost_state` を報告する。読む前に数えた分は前回の値に足す | `段1では置き場の障害でも送り_種類を報告する`・`読む前に段1で数えた分は_読んだ前回の値に足す` |
| AC6 | 同時の要求で枠を越えて送らない。置き場の値も枠を越えない | `同時の要求でも枠を越えて送らず_置き場の値も枠を越えない`（200 件・並列 16） |
| AC7 | 書き手は 1 つ（2 つ目の書き手は送らない。前の書き手が離れたら引き継ぐ） | `同じ置き場を別の書き手が持つ間は送らず_離したら引き継ぐ` |
| AC8 | 起動の時刻のゲージを出し、作り直すと値が変わる | `LlmGatewayStartMetricsTests` |
| AC9 | クラウドの描画: PVC（RWO・keep）・置き場のパスはマウントの直下・1 レプリカ・Recreate。chart は 2 レプリカ・HPA・相対パスを描画で止める。既定・ローカルは変えない | `scripts/deploy-cloud.test.js` IADR-0698 SP1・SP2 と陰性対照 |
| AC10 | 警報: 30 分に 3 回の再起動で鳴り 2 回では鳴らない・ラベルが変わらなくても数える・置き場の障害で鳴る（promtool の単体試験） | `scripts/cloud-ops.test.js` IADR-0698 の 2 件（陰性対照つき） |
| AC11 | 手順書に提供元のコンソールの支出上限をクラウド稼働の前提条件として書く（値を書かない） | `deploy/cloud/README.md` §3 前提条件（目視） |
| AC12 | ［AI レビュー］置き場の障害の警報が、起動の直後の最初の 1 件（系列が 0 から現れずに 1 で現れる）も拾う | `scripts/cloud-ops.test.js` IADR-0698 の promtool の単体試験（右の項を外す・期待を反転する陰性対照つき） |
| AC13 | ［AI レビュー］置き場への保存の所要時間を結果つきで計器に出す（プロセス内だけの置き場では出さない） | `LlmCallLimiterPersistenceTests.置き場への保存ごとに所要時間を結果つきで計器に載せる`・`プロセス内だけの置き場では保存の所要時間を載せない` |

## 是正の母集合（規則 9・10）

誤りの側の文字列で走査した（`git grep -n "0 に戻る\|上限はプロセスごと\|状態はプロセス内\|状態はレプリカごと\|書き込みは /tmp の emptyDir だけ"`）。

| 場所 | 扱い |
| --- | --- |
| `IADR-0678` 決定 5・結果 2 | 日付つき追記（置き場を与えた構成では戻らない） |
| `IADR-0692` 結果 1・結果 5 | 日付つき追記（解消・手順書に明記） |
| `docs/observability/llm-usage-and-cost-metrics.md` §送る前の上限 | 本文を更新（`cost_state`・置き場・計器） |
| `deploy/cloud/values-cloud.yaml`（読み取り専用の注記） | 更新（PVC も書ける） |
| `scripts/deploy-cloud.test.js` AC14（書き込みは emptyDir だけ） | 状態の PVC だけを例外にし、他の書けるマウントは引き続き検出する |
| `.ai-context/specs/` の既存の仕様書 | 凍結記録なので直さない |
| 他サービスの「0 に戻る」（IADR-0148・IADR-0687 ほか） | 別の状態であり対象外 |

## 実装の範囲

- `Domain/Ports/ILlmSpendStore.cs`・`Domain/Limits/ProcessLocalLlmSpendStore.cs`・`Infrastructure/Persistence/FileLlmSpendStore.cs`・`LlmCallLimiter`（読み・書き・`cost_state`）。
- `Common/Observability/LlmGatewayStartMetrics.cs`・`Program.cs`（置き場の選択・起動の読み込み・ForceFlush）。
- chart `templates/deployment.yaml`（`persistence`）・`deploy/cloud/values-cloud.yaml`・`deploy/cloud/observability/prometheus.yaml`。
- 手順: `deploy/cloud/README.md`・`docs/operations/cloud-backup-alerts-heartbeat-runbook.md`（分岐）。

## 範囲の外

- 要求数の窓の永続化（60 秒の窓。再起動の影響は小さい）。複数レプリカでの共有の置き場。PVC のバックアップ。
