---
title: IADR-0558 変換ジョブの図画像（/bff/conversion/jobs/{id}/figures/{figureId}/image）は変換サービスが読み、BFF は利用者の資格情報を付けた既存のエッジの REST 中継で受け取る。BFF からオブジェクトストレージの配線を外す
type: impl-adr
status: Accepted
related_ids: [FR-12, UC-06, SC-07, NFR-09, ADR-0014, ADR-0029, ADR-0106, ADR-0109, IADR-0029, IADR-0154, IADR-0465, IADR-0540, IADR-0544]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0014_object-storage.md（ABAC 判定を通したサービス経由のアクセス）
  - planning:projects/microservices-platform/07_adr/ADR-0109_bff-user-credential-relay-is-edge.md（決定 1・3）
related_specs:
  - ../specs/20261017_1941_conversion-figure-image-via-owner.md
---

# IADR-0558: 変換ジョブの図画像は変換サービスが読み、BFF は中継するだけにする（#1941）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-17
- 決定者: claude（オーナーの依頼 #1941）

## 起点・関連

- 起点 issue: #1941（親 #1905。#1939 の監査で範囲外として切り出された）。
- 関連する計画書 ID: FR-12・UC-06・SC-07（人手補正 Phase 1 の 2 ペイン）・NFR-09。
- 関連する計画 ADR: ADR-0014（オブジェクトストレージへのアクセスは「ABAC 判定を通したサービス経由」）・ADR-0106（SeaweedFS）・
  **ADR-0109 決定 1**（BFF が利用者の資格情報を付けて中継する呼び出しはエッジであり east-west に数えない）・**決定 3**（エッジの後段は中継された
  資格情報で自ら門を判定する。ConversionService も検証する）・ADR-0029（east-west は gRPC）。
- 改める実装判断: [IADR-0154](./IADR-0154_manual-figure-correction-phase1.md) 決定 2（画像のバイト列は BFF がストレージから解決して返す）。
- 先例: [IADR-0544](./IADR-0544_document-content-via-document-service-grpc.md)（文書の本文は文書サービスが読み、BFF はストレージを持たない）。
- 関連: [IADR-0465](./IADR-0465_conversion-service-validates-relayed-user-credential.md)（変換サービスは中継された利用者の資格情報を自ら検証する）・
  [IADR-0540](./IADR-0540_ingestion-body-missing-is-permanent.md) 決定 1（NoSuchKey の判定）。
- 採番: `origin/develop` の最大は IADR-0557。開いている PR（#1982 ほか）に新しい IADR は無い。本件は **0558**。

## コンテキストと課題

- BFF の図画像の口は、後段の `GET /jobs/{id}/figures` で図の `imageUri` を引き、BFF 自身の `IObjectStorageClient` で読んでいた（IADR-0154 決定 2）。
- BFF には helm・compose ともストレージが配線されておらず、`NullObjectStorageClient`（`CanResolve` が常に偽）へ縮退して**配備では常に 404** だった。
  #1905（文書の本文）と同じ構図であり、IADR-0544 §残余に記録されていた。
- 図の所有者は変換サービスである（図の台帳を持ち、画像を書く）。変換サービスには helm・compose ともストレージが配線済みである。
- BFF → 変換サービスの既存の 5 口は、利用者の資格情報を付けた REST の中継である。ADR-0109 決定 1 はこれをエッジと分類し、変換サービスは
  IADR-0465 で中継された JWT を自ら検証して BFF と同じロールの門を張っている。
- 決めること: (1) 画像をどこが読むか、(2) BFF から変換サービスへの輸送、(3) 画像を返せないときの応答。

## 検討した選択肢

1. **BFF にストレージを配線する（helm `services.bff.objectStorage: true`・compose `*objectstorage-env`）。**
   IADR-0544 案 1 と同じ理由で却下する。エッジに全バケット読み書き可の共有資格情報を置くことになる。
2. **変換サービスに east-west gRPC の面を足し、BFF は自身の s2s で呼ぶ（issue の提案）。**
   - 変換サービスの画像の口の門は「利用者が admin か」である。gRPC の面で判定するには、`ServiceCaller` ＋本文の利用者文脈（ADR-0086）を
     変換サービスに新しく信頼させることになる。変換サービスの他の 5 口は中継された JWT で判定しており（IADR-0465 決定 2 は `platform-service`
     だけのサービス間トークンを通さない）、**同じサービスで門の作りが 2 通りに割れる**。
   - BFF → 変換サービスの gRPC クライアント・宛先の構成キー・helm / compose の配線・proto と基線が新しく要る。
   - 文書の本文（IADR-0544）が gRPC なのは、BFF → 文書サービスの読み取りが既に BFF 自身の s2s の gRPC 面（`DocumentRead`。ADR-0109 決定 2 で
     east-west のまま）だったからである。変換サービスには BFF 向けの gRPC 面が無い。
3. **変換サービスに REST の画像の口を足し、BFF は他の 5 口と同じエッジの中継で受け取る（採用）。**
   - 画像を読むのは図の所有者であり、BFF はストレージの資格情報を持たない（ADR-0014「サービス経由」・IADR-0544 の要点）。
   - 輸送と門は変換サービスの既存の 5 口と同じである（ADR-0109 決定 1・3）。新しい仕組みを作らない。
   - IADR-0154 決定 2 が「ワーカーへ画像配信を足さない」とした根拠（IADR-0029「ワーカーは最小 HTTP サーフェス」）は、IADR-0465 で変換サービスの
     `/jobs` が利用者の資格情報を検証する管理 API になった時点で、図の一覧と同じ群の口を 1 つ足すことを妨げない。

## 決定

1. **図画像は変換サービスが読む。** `GET /jobs/{id}/figures/{figureId}/image`（群 ∧ `AdminOnly`。図の一覧・人手補正の投稿と同じ門）を足す。
   本体は `FigureImageReader`（`Features/ConversionJobs/GetFigureImage/`）。図の特定は `IConversionJobStore.GetFigureAsync` を通る。
2. **BFF は利用者の資格情報を付けて中継するだけである。** 本文はストリームで返し（BFF でバッファしない）、Content-Type は後段の値をそのまま使う。
   **BFF は `AddPlatformObjectStorage` を呼ばない**（BFF で `IObjectStorageClient` を使う口が 0 になった。IADR-0544 決定 1 の「残す」を改める）。
3. **画像の状態を応答で分け、どれも「画像が無い」に畳まない。**

   | 変換サービスの判定 | 変換サービス | BFF |
   | --- | --- | --- |
   | ジョブが無い・図が無い・コード化済み | 404（問題詳細なし。IADR-0154「未知の図と区別しない」） | 404 |
   | 参照が `storage://` でない（外部 URL は取りに行かない） | 404 ＋ ProblemDetails（`type` = `urn:microservices-platform:problem:figure-image-unavailable`・`reason` = `not_stored`） | 404（本文ごと透過） |
   | オブジェクトが無い（NoSuchKey だけ） | 404 ＋ ProblemDetails（同上・`reason` = `missing`）。WARN 1 行 | 404（本文ごと透過） |
   | ストレージが構成されていない（`CanResolve` が偽） | 503 ＋ ProblemDetails（`type` = `…:figure-image-unreachable`） | 502 |
   | 一時障害・バケットが無い | 例外のまま（500） | 502 |
   | 後段不達 | — | 502 |

4. **大きさの上限は入れない。** 画像は変換が書いたもので、413 の契約を持たない。BFF はストリームで中継し、変換サービスは従来の BFF と同じく全体を読む。
5. **配線の試験**: `ConversionFigureImageDeploymentWiringTests` が helm・compose で conversion がストレージを配線し、bff が配線しないことを固定する。
   **helm・compose の構成値は変えない。**

## 理由

- ADR-0014 はストレージへのアクセスを「サービス経由」と定める。読むのを図の所有者に寄せると、資格情報・参照の解釈・欠落の判定が 1 か所になる。
- ADR-0109 は BFF → 変換サービスをエッジと分類している。輸送を既存の 5 口に揃えると、門の作り（中継された JWT・同じロール）が 1 通りのまま保てる。

## 結果

- PoC: 配備後、縮退した図の `/image` は 200 で画像を返す。運用者は 403、未知の図は 404。BFF の Pod に `ObjectStorage__*` は無い。
- 往復: 画像の取得は BFF → 変換サービスが 1 回（従来は図の一覧 1 回 ＋ BFF からストレージ 1 回）。
- 画面（SC-07）: 失敗（404・502）は既存の空状態「元の画像を表示できません。」に落ちる（画面は変えない）。生成コード（orval）は 502 の型と説明を足した。
- 試験: `ConversionFigureImageTests`（8 件）・`ConversionFigureImageDeploymentWiringTests`（4 件）・`ConversionJobAuthorizationTests`（画像の口を 6 口目として追加）・
  `BffConversionEndpointTests`（中継 8 件）。変異で赤になることを確かめた（作業仕様書）。
- 残余: 変換サービスは画像の全体を読んでから返す。共有のポートにバイト列の上限つき・ストリームの取得を足すかは、画像の大きさの上限を決めるときに扱う。

## 関連

- #1941・#1905・#1939（IADR-0544）・#1940
