---
title: IADR-0543 文書の本文（/bff/documents/{id}/content）は文書サービスが読み、BFF は east-west gRPC（DocumentRead.GetDocumentContent）で受け取る。BFF にストレージを配線せず、案内文の 200 への縮退を撤去して 404 / 413 / 502 を返す
type: impl-adr
status: Accepted
related_ids: [FR-06, UC-03, SC-03, NFR-09, NFR-16, ADR-0014, ADR-0029, ADR-0075, ADR-0089, ADR-0106, ADR-0119, IADR-0009, IADR-0038, IADR-0290, IADR-0379, IADR-0401, IADR-0402, IADR-0533, IADR-0537, IADR-0540]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0014_object-storage.md（ABAC 判定を通したサービス経由のアクセス）
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md（east-west は gRPC）
related_specs:
  - ../specs/20261015_1905_bff-document-content.md
---

# IADR-0543: 文書の本文は文書サービスが読み、BFF は east-west gRPC で受け取る（#1905）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-15
- 決定者: claude（オーナーの依頼 #1905）

## 起点・関連

- 起点 issue: #1905（PoC の実測・2026-10-11。AST の確定報告書 daily-2026-10-10〔`hasBody=true`〕の `/bff/documents/{id}/content` が
  381 バイトの案内文 `# <表題>\n\nコンテンツは storage://… から取得します。` を 200 で返した。AST#565 で観測）。
- 関連する計画書 ID: FR-06・UC-03・SC-03・NFR-09・NFR-16。
- 関連する計画 ADR: ADR-0014（本文はオブジェクトストレージ。アクセスは「ABAC 判定を通した署名付き URL またはサービス経由」で直接公開しない）・
  ADR-0029 / ADR-0075（east-west は gRPC）・ADR-0089 決定 1（REST の並走を残さない）・ADR-0106（SeaweedFS）・ADR-0119 決定 3（読み取りの主体）。
- 関連する実装 ADR: [IADR-0038](./IADR-0038_bff-document-read-abac-gating.md)（決定 3 を本 IADR で改める）・
  [IADR-0290](./IADR-0290_version-response-drops-body-reference.md)（版の応答は本文の参照を持たない。**本件は現行版の本文で、版には触れない**）・
  [IADR-0379](./IADR-0379_east-west-grpc-preconditions.md)（proto の置き場・番号の追加は非破壊）・
  [IADR-0401](./IADR-0401_east-west-grpc-authz-user-directory.md) 決定 5（「無い」は応答、「引けなかった」は status）・
  [IADR-0533](./IADR-0533_east-west-rest-retirement.md)（REST の並走は作らない）・
  [IADR-0537](./IADR-0537_document-list-pagination-offset-window-after-abac.md)（D-1 チャネルの受信上限 64 MiB）・
  [IADR-0540](./IADR-0540_ingestion-body-missing-is-permanent.md) 決定 1（NoSuchKey の判定）。
- 採番: `origin/develop` の最大は IADR-0540。開いている PR #1936 が 0541、#1937 が 0542 を採っている（push の直前に確かめた）。本件は **0543**。

## コンテキストと課題

- `DocumentBffEndpoints` の `GET /{id}/content` は ABAC 判定の後、BFF 自身の `IObjectStorageClient` で `storage://` を読んでいた（IADR-0038 決定 3）。
- BFF には helm（`services.bff` に `objectStorage: true` が無い）でも compose（`*objectstorage-env` を合成していない）でもストレージが配線されておらず、
  `AddPlatformObjectStorage` は `NullObjectStorageClient`（`CanResolve` は常に偽）へ縮退していた。`ReadMarkdownAsync` は `CanResolve` が偽のとき**黙って案内文を返す**。
  → **配備した環境では SC-03 の本文は一度も本文を出していなかった**（単体試験も「題名を含む」で緑だった）。
- 本文の実体が無い文書（9/26 の SeaweedFS 切り替えより前の 5,803 件。#1931・IADR-0540）も同じ案内文になり、利用者・監視とも区別できない。
- 決めること: (1) 本文をどこが読むか（BFF がストレージを直接読むか、文書サービス経由か）、(2) 本文を返せないときの応答、(3) 大きな本文の上限、(4) 配線の試験。

## 検討した選択肢

1. **BFF にストレージを配線する（helm `services.bff.objectStorage: true`・compose `*objectstorage-env`）。** 変更は最小。
   - BFF は north-south の入口（インターネットに面する）であり、そこへ**全バケット読み書き可の共有資格情報**（`global.objectStorage.existingSecret`。
     読み取り専用の資格情報は現行の Secret に無い）を置くことになる。BFF が破られたときの影響が本文の全件に及ぶ。
   - 本文の参照（`MarkdownUri`）の解釈と NoSuchKey の扱いが BFF と文書サービスの 2 か所に分かれる。
   - 個人資料の判定（ADR-0119 決定 3）が後段の面の外で本文に届く —— BFF の判定 1 枚だけになる。
2. **文書サービスの gRPC 面（`DocumentRead`）に本文の rpc を足し、BFF はそれで受け取る（採用）。**
   - 本文の実体を読むのは、台帳（参照）の所有者であり既にストレージを配線している文書サービスである（ADR-0002 のサービス境界・ADR-0014「サービス経由」）。
   - BFF はストレージの資格情報を持たない。BFF の ABAC（`FetchAuthorizedAsync`）を先に通し、後段も同じ利用者で `GetDocument` と同じ判定を通る（AND 合成）。
   - east-west は gRPC（ADR-0029）。REST の並走を作らない（ADR-0089 決定 1・IADR-0533）。
   - `DocumentRead` の「本文の口は出していない」（IADR-0401 決定 2 の作法＝呼び出し元が要らないものを出さない）は、呼び出し元が要るようになったので改める。
3. 署名付き URL を BFF が払い出し、SPA がストレージから直接読む（ADR-0014 のもう一方）。
   - ストレージをブラウザから到達可能にする（Ingress・CORS・外部 CDN 禁止の検査）必要があり、射程を大きく越える。本文の大きさの扱いも画面へ漏れる。却下。
4. 文書サービスに REST `GET /documents/{id}/content` を足す —— east-west の新しい REST 経路を作る（ADR-0029・ADR-0089 に反する）。却下。

## 決定

1. **本文は文書サービスが読み、BFF は `DocumentRead.GetDocumentContent` で受け取る。** BFF はストレージを読まない（`DocumentBffEndpoints.ReadMarkdownAsync` は撤去）。
   - 文書サービスの本体は `DocumentContentUseCase`（`Features/Documents/GetContent/`）。**読めるかの判定は `DocumentReadUseCase.GetAsync` を通る**（判定器を 2 つにしない）。
   - BFF の ABAC の位置は動かさない（`FetchAuthorizedAsync` → 本文の rpc。判定を通る前に本文の rpc を呼ばない）。利用者文脈は他の 4 口と同じく本文で運ぶ。
   - BFF の `AddPlatformObjectStorage` は残す（変換ジョブの図画像 `/bff/conversion/jobs/{id}/figures/{figureId}/image` がまだ使う。§残余）。
2. **本文の状態を応答で分け、どれも案内文の 200 にしない。**

   | 後段 | 意味 | BFF の応答 |
   | --- | --- | --- |
   | `found=false` | 無い・読めない | 404（存在秘匿。問題詳細なし —— 詳細と同じ） |
   | `AVAILABLE` | 本文 | 200 `DocumentContentDto` |
   | `NOT_STORED` | 参照が `storage://` でない（未設定を含む）。外部 URL は取りに行かない | 404 ＋ ProblemDetails（`type` = `urn:microservices-platform:problem:document-body-unavailable`・`reason` = `not_stored`） |
   | `MISSING` | オブジェクトが無い（NoSuchKey だけ） | 404 ＋ ProblemDetails（同上・`reason` = `missing`） |
   | `TOO_LARGE` | 上限超過 | 413 ＋ ProblemDetails（`sizeBytes` / `limitBytes`） |
   | `UNSPECIFIED`・未知 | 分からない応答 | 502（成功へ畳まない） |
   | status（`FAILED_PRECONDITION`＝ストレージ未構成、`INTERNAL` 等＝一時障害・バケットが無い、`UNAVAILABLE`＝宛先未構成） | 引けなかった | 502 ＋ ProblemDetails。WARN 1 行（`DocumentReadFailureLog`） |

   - 本文の欠落を 404 で返すのは、文書は読めている（BFF の判定を通った）ので秘匿の対象ではなく、「本文という資源が無い」ことを示すためである。
     問題詳細の有無で秘匿の 404 と区別できるが、区別できるのは**既に詳細を読める利用者**だけである（存在の漏れにはならない）。
   - NoSuchKey の判定は IADR-0540 決定 1 と同じ（SDK の型か `ErrorCode == "NoSuchKey"`。404 だけでは判定しない）。判定は共有の
     `ObjectStorageErrors.IsNoSuchKey`（`Platform.Shared.Infrastructure`）へ移し、取り込みの読み手も同じ関数を呼ぶ（挙動は変えない）。
   - 欠落・上限超過は文書サービスで WARN 1 行（文書 ID・本文の所在・大きさ。本文と利用者識別子は載せない）。
3. **ストレージが構成されていない（`CanResolve` が偽）は「本文が無い」ではなく `FAILED_PRECONDITION`。** 配線の欠落を 404 に化けさせない（#1905 の再発防止）。
4. **本文の上限は UTF-8 で 16 MiB（`DocumentContentUseCase.MaxBodyBytes`）。超過は本文を返さず、切り詰めない。**
   - 根拠: BFF の D-1 チャネルの受信上限（既定 64 MiB。IADR-0537）より十分に小さく（逆転すると上限ちょうどの本文が `RESOURCE_EXHAUSTED` → 502 に化ける）、
     本文の直接投入の上限（1 MB）や変換の出力より十分に大きい。画面は本文を等幅の `pre` で出すので、これを超える本文はブラウザで扱いにくい。
   - 切り詰めないのは、本文の一部を全体と読み違えさせないため（本文の受け入れの 413 と同じ向き）。
   - 測るのは読み込んだ後である（共有のポートに大きさの問い合わせが無い。§残余）。
5. **配線の試験**: `DocumentContentDeploymentWiringTests` が helm（`services.document.objectStorage: true`）と compose（`document-service` が `*objectstorage-env` を合成し、
   アンカーが 4 キーを持つ）を固定する。BFF → 文書サービスの gRPC 宛先（D-1）は既存の `EastWestGrpcDeploymentWiringTests` が固定している。
   **helm・compose の構成値は変えない**（文書サービスは配線済み。BFF は配線しない）。

## 理由

- ADR-0014 は本文へのアクセスを「ABAC 判定を通したサービス経由」と定める。読むのを台帳の所有者に寄せると、資格情報・参照の解釈・欠落の判定が 1 か所になる。
- エッジ（BFF）に全バケット読み書き可の資格情報を置かない。読み取り専用の資格情報を作るには SeaweedFS の identity と Secret・ESO の追加が要り、
  それでも「BFF が破られたら全本文が読める」は変わらない。
- 案内文の 200 は「壊れていることに気付けない」縮退である。状態コードで分ければ、画面（QueryState の失敗表示）も監視（BFF の 404 / 502 の率）も気付ける。

## 結果

- PoC: 配備後、`hasBody=true` で実体のある文書の `/content` は本文を返す。実体の無い文書（切り替え前の分）は 404 ＋ `reason=missing`。
- 往復: 本文の取得は BFF → 文書サービスの gRPC が 1 回増える（詳細 1 回 ＋ 本文 1 回）。本文の転送は 1 ホップ増える。
- 画面（SC-03）: 本文の取得失敗は既存の `QueryState` が「本文は利用できません。」＋状態の文言で出す（案内文前提の表示は無かった）。生成コード（orval）は 413 / 502 の型を足した。
- 試験: `GrpcDocumentContentTests`（実 h2c・12 件）・`BffDocumentContentTests`（12 件）・`DocumentContentDeploymentWiringTests`（2 件）。変異で赤になることを確かめた（仕様書）。
- 残余:
  - 変換ジョブの図画像（`/bff/conversion/jobs/{id}/figures/{figureId}/image`）は BFF のストレージを読み続けており、配備では同じく未配線（`CanResolve` 偽 → 404）。本件の射程外。
  - 上限の判定は本文を読み込んだ後である。共有のポート（`IObjectStorageClient`）に大きさの問い合わせ（HEAD）を足せば読む前に止められるが、実装が 18 あり射程を越える。
  - wiki-service・graph-service の本文の読み手は NoSuchKey の扱いを変えていない（IADR-0540 の残余のまま）。

## 関連

- #1905・#1931（本文の無い 5,803 件）・AST#565（観測）
