---
title: 変換ジョブの図画像の口（/bff/conversion/jobs/{id}/figures/{figureId}/image）は所有者の変換サービスが読み、BFF は中継するだけにする。BFF からオブジェクトストレージの配線を外す（#1941）
type: spec
status: done
related_ids: [FR-12, UC-06, SC-07, NFR-09, ADR-0014, ADR-0029, ADR-0106, ADR-0109, IADR-0154, IADR-0465, IADR-0540, IADR-0544, IADR-0558]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0014_object-storage.md（ABAC 判定を通したサービス経由のアクセス）
  - planning:projects/microservices-platform/07_adr/ADR-0109_bff-user-credential-relay-is-edge.md（BFF の中継はエッジ。後段は中継された資格情報で自ら判定する）
issue: "#1941"
---

# 仕様書: 変換ジョブの図画像を所有者の変換サービスから受け取る（#1941）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: #1941（親 #1905。#1939 の監査で見つかった範囲外）。issue 本文の起点 ID は FR-06 だが、図画像の口は
  **FR-12（変換）・UC-06・SC-07（変換ジョブ・人手補正）** の口である（`ConversionBffEndpoints` の起点 ID と同じ）。本件は FR-12 で記録する。
- 計画 ADR: ADR-0014（オブジェクトストレージへのアクセスは「ABAC 判定を通したサービス経由」。直接公開しない）・ADR-0106（SeaweedFS）・
  **ADR-0109 決定 1・3**（BFF が利用者の資格情報を付けて中継する呼び出しはエッジであり east-west に数えない。後段は中継された資格情報で
  自ら門を判定する。ConversionService も検証する）・ADR-0029（east-west は gRPC）。
- 先例: [IADR-0544](../adr/IADR-0544_document-content-via-document-service-grpc.md)（文書の本文は文書サービスが読み、BFF はストレージを持たない）・
  #1940（上限つきの読み取り）。
- 改める実装判断: [IADR-0154](../adr/IADR-0154_manual-figure-correction-phase1.md) 決定 2（画像は BFF がストレージから返す）。
- 実装判断の記録: **IADR-0558**（新規）。

## 背景（コードで確認）

- BFF の `GET /bff/conversion/jobs/{id}/figures/{figureId}/image` は、後段の `GET /jobs/{id}/figures` で図の `imageUri` を引き、
  **BFF 自身の `IObjectStorageClient`** でバイト列を読んで返す（IADR-0154 決定 2）。
- BFF には helm（`services.bff` に `objectStorage: true` が無い）・compose（`bff` が `*objectstorage-env` を合成しない）ともストレージが
  配線されておらず、`AddPlatformObjectStorage` は `NullObjectStorageClient`（`CanResolve` が常に偽）へ縮退する → **配備では常に 404**。
  #1905 と同じ構図（IADR-0544 §残余に記録済み）。
- 変換サービスは図の所有者であり（`ConversionJobStore` が図の台帳を持ち、`PandocConversionService` が画像を書く）、helm・compose とも
  ストレージを配線済み（`services.conversion.objectStorage: true`・`conversion-service` が `*objectstorage-env` を合成）。
- BFF → 変換サービスの他の 5 口は**利用者の資格情報を付けた REST の中継**であり、ADR-0109 決定 1 でエッジに分類され、変換サービスは
  IADR-0465 で中継された JWT を自ら検証して 5 口に BFF と同じロールの門を張っている。

## 決めたこと（IADR-0558）

1. **図画像は変換サービスが読む。** 変換サービスに `GET /jobs/{id}/figures/{figureId}/image`（**群 ∧ `AdminOnly`**。他の人手補正 2 口と同じ門）を足す。
   本体は `FigureImageReader`（`Features/ConversionJobs/GetFigureImage/`）。図の特定は `IConversionJobStore.ListFiguresAsync` を通る（図の一覧と同じ）。
2. **BFF は中継するだけにする。** 利用者の資格情報を付けて後段の画像の口を呼び、本文をストリームで返す（バッファしない）。
   **BFF からストレージの配線（`AddPlatformObjectStorage`）を外す** —— BFF で `IObjectStorageClient` を使う口は本件で 0 になる（下の母集合）。
3. **輸送は gRPC ではなく、既存のエッジの REST 中継に揃える。** issue の提案（east-west gRPC）を採らない理由:
   - ADR-0109 決定 1 は「BFF が利用者の資格情報を付けて中継する呼び出しはエッジ」と定め、変換サービスへの BFF の 5 口はすべてこの形である。
     画像だけを gRPC にすると、変換サービスは利用者の資格情報を持たない面（`ServiceCaller` ＋本文の利用者文脈）でロールを判定することになり、
     IADR-0465 決定 2（`platform-service` だけのサービス間トークンは通さない）と門の作りが 2 通りに割れる。
   - 文書の本文（IADR-0544）が gRPC なのは、BFF → 文書サービスの読み取りが既に BFF 自身の s2s の gRPC 面（`DocumentRead`。ADR-0109 決定 2 で
     east-west のまま）だったからである。先例の要点は「所有者が読み、BFF はストレージを持たない」であり、輸送の種類ではない。
   - 新しい仕組み（BFF → 変換サービスの gRPC クライアント・宛先の構成キー・helm / compose の配線・proto と基線）を作らずに済む。
4. **応答の写し**（図の一覧と同じく 404 は秘匿の 404 と理由つきの 404 を分ける）:

   | 変換サービスの判定 | 変換サービス | BFF |
   | --- | --- | --- |
   | ジョブが無い・図が無い・コード化済み（画像を持たない） | 404（問題詳細なし。IADR-0154「未知の図と区別しない」） | 404（透過） |
   | 画像の参照が `storage://` でない | 404 ＋ ProblemDetails（`type` = `urn:microservices-platform:problem:figure-image-unavailable`・`reason` = `not_stored`） | 404（本文ごと透過） |
   | オブジェクトが無い（NoSuchKey だけ。IADR-0540 決定 1） | 404 ＋ ProblemDetails（同上・`reason` = `missing`）。WARN 1 行 | 404（本文ごと透過） |
   | ストレージが構成されていない（`CanResolve` が偽） | 503 ＋ ProblemDetails（`type` = `…:figure-image-unreachable`）。「画像が無い」に化けさせない（#1905 の再発防止） | 502 |
   | 一時障害（時間切れ・5xx・バケットが無い） | 例外のまま（500） | 502 |
   | 後段不達 | — | 502 |
   | 401 / 403 | 後段の門 | 透過 |

5. **大きさの上限は入れない。** 画像は変換が書いたもので、上限の契約（413）を持たない。BFF はストリームで中継するので BFF のメモリは画像の
   大きさに比例しない。変換サービスは従来の BFF と同じく全体を読む（共有のポートにバイト列の上限つき取得が無い。§残余）。

## 母集合（規則 9）

### BFF の `IObjectStorageClient` の利用者（`grep -rn "IObjectStorageClient\|AddPlatformObjectStorage" src/platform/backend/Bff src/knowledge/backend/Bff --include=*.cs`）

| 箇所 | 処置 | 理由 |
| --- | --- | --- |
| `ConversionBffEndpoints` の図画像の口 | **変える** | 本件 |
| `Platform.Bff/Program.cs` の `AddPlatformObjectStorage` | **撤去** | 利用者が 0 になる。残すと「BFF はストレージを持たない」が構成だけで崩れ得る |
| `DocumentBffEndpoints`（本文） | 変更なし | IADR-0544 で既に使っていない（注記のみ） |

### 「BFF がストレージから返す」と書いた live な記述（`grep -rn "BFF がオブジェクトストレージ\|figures/{figureId}/image"`）

| 箇所 | 処置 |
| --- | --- |
| `ConversionBffEndpoints.cs`・`ConversionFigureDto.cs`・`ListFigures/Endpoint.cs`・`ConversionJobEndpoints.cs`（門の表） | 改める |
| `docs/api/openapi.yaml`（description・502）と生成物（orval） | 改める・再生成 |
| `docs/api/BFF_bff-surface.md` の行 | 変えない（ロールと「生成フックを使わない」は不変） |
| IADR-0154 決定 2・決定 6 追記、IADR-0544 決定 1・§残余 | 日付つき追記で IADR-0558 を指す |
| `.ai-context/specs/` の確定済み記録 | 書き換えない |

## 受け入れ基準と試験の写像

| # | 受け入れ基準（#1941） | 試験 |
| --- | --- | --- |
| AC-1 | 配備で図画像が 200 で返る | `ConversionFigureImageTests`（変換サービスが画像のバイト列と Content-Type を返す）・`BffConversionEndpointTests`（BFF がバイト列と Content-Type をそのまま中継する）・`ConversionFigureImageDeploymentWiringTests`（helm・compose の conversion がストレージを配線している） |
| AC-2 | BFF にストレージの資格情報を配らない | `ConversionFigureImageDeploymentWiringTests`（helm の bff に `objectStorage` が無い・compose の bff が `*objectstorage-env` を合成しない）・`BffConversionEndpointTests`（BFF の DI に `IObjectStorageClient` が無い） |
| AC-3 | 権限外は従来どおり | 運用者は BFF・変換サービスとも 403（既存の BFF 試験 ＋ `ConversionJobAuthorizationTests` の AdminOnly 表へ足す）・資格情報なしは 401・未知の図は 404 |
| — | 画像を返せないときに 200 や「無い」に化けない | `ConversionFigureImageTests`（not_stored・missing・未構成 503）・`BffConversionEndpointTests`（404 本文の透過・5xx → 502・不達 → 502） |

> AC-3 の「従来どおり 404」について: 変換ジョブにはジョブの所有者の ABAC は無く、門はロール（admin のみ）である（IADR-0154 決定 6・IADR-0465）。
> 「従来どおり」は**運用者・一般利用者は 403、未知のジョブ・図は 404** を指すと読み、これを変えない。

## 変異（赤になることを確かめる）

1. 変換サービスの画像の口から `AdminOnly` を外す → 運用者 403 の試験が赤。
2. `CanResolve` が偽を 404 にする → 未構成 503 の試験が赤。
3. BFF の中継で Content-Type を固定値にする → 中継の試験が赤。
4. BFF に `AddPlatformObjectStorage` を戻す → DI の試験が赤。

## 検証

- `dotnet build` / `dotnet test`: ConversionService.Tests・Platform.Bff.Tests。`dotnet format --verify-no-changes`（両 slnx の変更プロジェクト）。
- `pnpm run codegen` の再生成差分。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-adr-index-sync`。

### 結果（2026-10-17）

| 対象 | コマンド | 結果 |
| --- | --- | --- |
| ConversionService | `dotnet test .../ConversionService.Tests.csproj` | 235 件緑（skip 9 は既存） |
| BFF | `dotnet test .../Platform.Bff.Tests.csproj` | 989 件緑（skip 1 は既存） |
| 変異 1〜4 | 上の各変異を入れて対象の試験クラスを実行 | いずれも 1 件赤（M1 運用者 403・M2 未構成 503・M3 Content-Type の中継・M4 DI） |
| 整形 | `dotnet format <変更プロジェクト> --verify-no-changes`・`pnpm run format:check` | 差分 0 |
| 生成物 | `pnpm run codegen`・`pnpm run typecheck` | `conversion.ts` に 502 と説明の差分だけ・型検査緑 |
| 文書・規約 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・ci.yml の静的検査一式 | 1111 件緑・`check-deploy-manifests` 以外緑（helm 等が手元に無い。deploy は触っていない） |

## PoC での確認手順

1. 配備前（現状の再現）: 縮退した図を持つ成功ジョブの `id` と `figureId` を `GET /bff/conversion/jobs/{id}/figures` で引き、
   `curl -s -o /dev/null -w '%{http_code}\n' -b '<管理者の Cookie>' https://<host>/bff/conversion/jobs/<id>/figures/<figureId>/image` → `404` を見込む。
2. bff・conversion-service を配備する（helm の値は変えない。conversion はストレージを配線済み）。
3. 同じ `curl` → `200`。`-o fig.png` で保存して画像として開ける。`Content-Type` は図の `imageContentType`（例 `image/png`）。
4. 権限外: 運用者の Cookie → `403`。存在しない `figureId` → `404`。
5. BFF Pod の環境変数に `ObjectStorage__*` が無いこと: `kubectl -n <ns> exec deploy/bff -- env | grep -c ObjectStorage__` → `0`。

## 残余

- 変換サービスは画像の全体を読んでから返す（従来の BFF と同じ）。共有のポートにバイト列の上限つき・ストリームの取得を足すかは、画像の大きさの
  上限を決めるとき（新しい決定が要る）に扱う。
