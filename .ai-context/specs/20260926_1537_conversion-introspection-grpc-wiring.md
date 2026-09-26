---
title: ConversionService の実効構成の収集を gRPC で配線する（IADR-0462 フォローアップ 3）
type: spec
status: draft
related_ids: [FR-15, NFR-09, NFR-16, ADR-0029, ADR-0075, ADR-0109, IADR-0379, IADR-0462, IADR-0465]
author: Claude（実装）
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# 仕様書: ConversionService の introspection を gRPC で配線する（#1537）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（構成情報 API・ドリフト検出）／NFR-09（認可）／NFR-16（east-west の統一）
- 関連 ADR: `ADR-0029`（east-west は gRPC）／`ADR-0075`（移行順序・基盤先行）／`ADR-0109` 決定 3（ConversionService も中継された資格情報を検証する —— 本作業の前提が着地した根拠）
- 実装 ADR: [[IADR-0462]]（決定 3 の 🔴 が conversion を保留し、フォローアップ 3 が本作業）／[[IADR-0465]]（conversion の認証。#1520 / PR #1529 で着地）／[[IADR-0379]] 決定 3・5（h2c・並走）
- issue: #1537（Refs #1514 / #1255 / #1520）
- **新しい IADR は起こさない。** 決定は IADR-0462 の決定 3・5 がすでに持っており（「認証が着地した段で h2c リスナ・`grpcPort`・gRPC 宛先を足す」）、本作業はその履行である。IADR-0462 へ日付つき追記を置く。

## 起点の確認

基点 `origin/develop` `e30e1c76`。`git rev-parse --is-shallow-repository` = `false`。

## やること（参照実装 = #1524 の 5 サービスの配線と同じ形）

| # | 対象 | 変更 |
| --- | --- | --- |
| 1 | `src/knowledge/backend/Services/ConversionService/Program.cs` | `builder.AddPlatformGrpcListener();`（`AddPlatformAuth` の直後。DataSource / Ingestion と同じ位置・同じ注記） |
| 2 | helm `services.conversion` | `grpcPort: 8081` |
| 3 | compose `conversion-service` | `expose` に `"8081"`、`Grpc__Port: "8081"` |
| 4 | helm・compose の BFF | `Introspection__GrpcServices__conversion-service`（helm `http://conversion-service:8081` / compose 同）。「まだ入れない」の注記を着地の注記へ改める |
| 5 | `IntrospectionGrpcDeploymentWiringTests` | 保留の一覧から conversion-service を外す（一覧は空になる。仕組みは残す） |
| 6 | `ConversionService.Tests/IntrospectionEndpointTests` | 「配線は別作業」の注記を改め、`platform-service` で得た gRPC の申告を**収集器と同じ写し**（`IntrospectionGrpcMapping.ToDto`）で DTO へ戻し、REST の申告と一致すること・空でない `service` を持つこと（収集器が到達不能へ落とさない条件）を見る |
| 7 | IADR-0462 | 決定 3・5・結果・フォローアップ 3 へ日付つき追記（本文は書き換えない） |
| 8 | `docs/api/east-west-grpc.md` §12 つ目の面 | 変換サービスの段落に追記（配線した）。trace ブロックへ #1537・本仕様書 |
| 9 | `docs/tests/FR-15_config-info-api.md` 2h | 保留の宛先が無くなったことを反映。trace ブロックへ #1537・本仕様書・IADR-0465 |

## 受け入れ基準

- [ ] helm・compose の BFF の gRPC 宛先が REST の収集先 13 と一致する（保留 0）—— `IntrospectionGrpcDeploymentWiringTests` の 4 試験が緑
- [ ] conversion の本番 `Program.cs` が h2c リスナを立て、自己申告の面を張る（同上・試験 3）
- [ ] conversion の gRPC 面が `platform-service` に REST と同じ申告を返す（収集器の写しで一致）、s2s 無し = `UNAUTHENTICATED`、利用者トークン = `PERMISSION_DENIED`
- [ ] REST の収集先（`Introspection__Services__conversion-service`）は残す（並走中の正は REST。IADR-0379 決定 5）
- [ ] 両ユニットの build / test / `dotnet format --verify-no-changes`、`check-trace-blocks`、`gen-knowledge-graph --check` が緑

## 母集合（着手時に自分で引いた）

パスの除外は `src/ai-stock-trading`（submodule）・`CHANGELOG.md`（生成物）のみ。拡張子で絞らない。

1. **誤りの側（「conversion はまだ gRPC でない」と書いた記述）** —— `git grep -n -I -i conversion` をパスから引き、
   `introspection|grpcport|Grpc__Port|GrpcServices|h2c|fail-closed|IADR-0462|フォローアップ 3|12 の|13 の` を含む行を目視:
   - `deploy/docker-compose.yml`（BFF の「🔴 conversion-service はまだ入れない」）
   - `deploy/helm/microservices-platform/values.yaml`（同上・helm 側）
   - `IntrospectionGrpcDeploymentWiringTests.cs`（`PendingGrpcTargets`）
   - `ConversionService/Tests/IntrospectionEndpointTests.cs`（「配線は別作業であり、ここでは触らない」）
   - `.ai-context/adr/IADR-0462_…md`（決定 3 の 🔴・決定 5「conversion を除く 12」・結果・フォローアップ 3）
2. **文書の側（日本語の言い回し）** —— `git grep -n -I -E "変換サービス|ConversionService|conversion" -- docs README.md deploy src/*/README.md` を
   `gRPC|grpc|h2c|8081|introspection|自己申告|REST のまま` で目視:
   - `docs/api/east-west-grpc.md` L635-640（「変換サービスだけは REST のまま」＋ #1520 の追記「残るのは配線だけ」）
   - `docs/tests/FR-15_config-info-api.md` 2h（「保留の宛先は理由つきで列挙し REST に残す」）
3. **配線の形の側** —— `git grep -n -I -E "GrpcServices|grpcPort|Grpc__Port|AddPlatformGrpcListener"`:
   compose 12 行・helm 12 行（gRPC 宛先）、helm `grpcPort` 12 サービス、`templates/deployment.yaml`・`service.yaml` は `grpcPort` を持つサービスにだけ描画（worker でも同じ。ingestion が先例）
4. **試験の側** —— `git grep -n "Introspection_grpc_face_is_mapped\|IntrospectionEndpointTests" -- docs scripts .ai-context/adr`:
   試験名を名指す文書は IADR-0462（追記で扱う）と FR-15 テスト仕様書 2h（クラス名で引く。改名しないので不変）だけ

### 除外とその理由

- `.ai-context/specs/20260926_1514_introspection-grpc-fanout.md`・`20260926_1520_conversion-service-auth.md`: 確定済みの作業仕様書。
  経過追記は可だが、本作業の記録は本仕様書が持つので追記しない
- `docs/functional/FR-15_config-info-api.md` L41: 輸送の選び方を一般形で書いており、conversion を名指さない（不変）
- `docs/api/FR-16_mcp-server.md`: MCP サーバーは収集先に無い（IADR-0462 フォローアップ 2。本作業の外）
- `docs/tech/tech-requirements.md` L446「gRPC 面を持つ経路は 11」: 経路の数であり、本作業は 12 つ目の面の宛先を 1 つ足すだけで面の数を変えない
- `deploy/helm/.../templates/deployment.yaml` L353（worker の注記）: HTTP プローブの話であり h2c と無関係
- `IADR-0465`: conversion の認証の記録。IADR-0462 フォローアップ 3 を名指していない（追記しない）
- `HttpEffectiveConfigCollectorTests` / `IntrospectionRegistrationTests` 等の `"conversion-service"`: サービス名を試験の値として使うだけで配線と無関係
- BFF の `appsettings.json`（ローカル既定の REST 宛先 2 つ）: gRPC 宛先を持たない配備の既定であり、#1524 も触っていない

## 試験の写像

| 受け入れ基準 | 試験 |
| --- | --- |
| 配備の 4 か所が揃う | `IntrospectionGrpcDeploymentWiringTests`（4 試験。保留一覧から外すだけで conversion が検査対象へ入る） |
| 面の判定と申告の一致 | `ConversionService.Tests.IntrospectionEndpointTests.Introspection_grpc_face_is_mapped_behind_ServiceCaller_and_judges_the_caller` |
| 収集器の輸送選択・失敗の畳み方 | `IntrospectionGrpcTests`（既存。宛先に依らない。変更しない） |

実 Kestrel での conversion への h2c 往復は足さない —— 参照実装（#1524）も新たに配線した 5 サービスに器を足しておらず、
h2c の往復は `IntrospectionGrpcTests`（ループバックの実 Kestrel）が宛先に依らず固定している。器を 1 サービスぶん写すのは新しい抽象の追加に当たる。

## 未実測

- 稼働 k3s での conversion への h2c 往復（Pod の再構築を要する。LIVE は使わない）
