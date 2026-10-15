---
title: 文書の本文（/bff/documents/{id}/content）を文書サービスから east-west gRPC で受け取り、案内文の 200 をやめる（#1905）
type: spec
status: done
related_ids: [FR-06, UC-03, SC-03, NFR-09, NFR-16, ADR-0014, ADR-0029, ADR-0075, ADR-0089, ADR-0106, ADR-0119, IADR-0038, IADR-0290, IADR-0379, IADR-0401, IADR-0533, IADR-0537, IADR-0540, IADR-0544]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0014_object-storage.md
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-03 文書詳細）
issue: "#1905"
---

# 仕様書: 文書の本文を文書サービスから受け取り、案内文の 200 をやめる（#1905）

## 起点となる計画書（トレーサビリティ）

- FR-06（文書管理）・UC-03・SC-03（文書詳細）・NFR-09・NFR-16。
- ADR-0014（本文はオブジェクトストレージ。アクセスは ABAC 判定を通したサービス経由）・ADR-0029 / ADR-0075（east-west は gRPC）・
  ADR-0089 決定 1（REST の並走を作らない）・ADR-0106（SeaweedFS）・ADR-0119 決定 3（読み取りの主体）。
- 実装判断の記録: [IADR-0544](../adr/IADR-0544_document-content-via-document-service-grpc.md)。IADR-0038 決定 3 を改める（同 IADR へ日付つき追記）。
- 起点: #1905（PoC・2026-10-11。AST#565 の文書で `/content` が 381 バイトの案内文を 200 で返した）。

## 背景（コードで確認）

- `DocumentBffEndpoints.ReadMarkdownAsync` は `storage.CanResolve(uri)` が偽のとき**黙って案内文を返す**。
- BFF は helm（`services.bff` に `objectStorage` 無し）・compose（`bff` に `*objectstorage-env` 無し）のどちらでもストレージ未配線 →
  `NullObjectStorageClient`（`CanResolve` 常に偽）→ 配備では本文が一度も出ていなかった。単体試験は「題名を含む」で緑だった。
- 文書サービスは helm・compose ともストレージ配線済み。gRPC `DocumentRead` は「本文の口は出していない」（呼び出し元が要らなかった）。

## 決めたこと（要約。詳細は IADR-0544）

1. 本文は文書サービスが読み、BFF は新しい rpc `DocumentRead.GetDocumentContent` で受け取る。BFF はストレージを読まない（`ReadMarkdownAsync` 撤去）。
   読めるかの判定は `DocumentReadUseCase.GetAsync` と同じ関数。BFF の ABAC（`FetchAuthorizedAsync`）を先に通る。
2. 応答の状態: `AVAILABLE` / `NOT_STORED` / `MISSING`（NoSuchKey だけ）/ `TOO_LARGE`（16 MiB 超・UTF-8）。ストレージ未構成は `FAILED_PRECONDITION`、一時障害は例外のまま。
3. BFF の写し: `found=false` → 404（秘匿）、`NOT_STORED` / `MISSING` → 404 ＋ ProblemDetails（`reason`）、`TOO_LARGE` → 413、status・`UNSPECIFIED` → 502。案内文の 200 は無い。
4. NoSuchKey の判定を共有の `ObjectStorageErrors.IsNoSuchKey` へ移し、取り込み（IADR-0540）も同じ関数を呼ぶ（挙動は不変）。
5. helm・compose の構成値は変えない（文書サービスは配線済み。BFF は配線しない）。配線試験を足す。helm の document の注記だけ更新。

## 母集合（規則 9。誤りの側の文字列で走査）

`git grep -n "プレースホルダ本文\|から取得します\|プレースホルダへ縮退\|プレースホルダへフォールバック"`（`.ai-context/specs`・`CHANGELOG.md`・submodule を除く）と
`git grep -n "ReadMarkdownAsync\|IObjectStorageClient" -- src/knowledge/backend/Bff src/platform/backend/Bff` を `origin/develop` `02b4d146` で走査した。

| 箇所 | 本件で変えるか | 理由 |
| --- | --- | --- |
| `Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs`（`/content`・`ReadMarkdownAsync`・冒頭の注記） | **変える** | 本件の対象 |
| `Bff/Platform.Bff/Program.cs`（`AddPlatformObjectStorage` の注記） | **注記だけ変える** | 登録は残す（変換の図画像が使う） |
| `Bff/Knowledge.Bff.Endpoints/ConversionBffEndpoints.cs`（図画像） | 変えない | 射程外（SC-07）。同じく配備で未配線 → 404。報告に残す |
| `Platform.Bff.Tests/BffDocumentEndpointTests.cs`（「題名を含む」で案内文を受け入れていた） | **変える** | 本文そのものを比べる |
| `docs/screens/SC-03_document-detail.md`（「未配備時はプレースホルダ本文」）・`docs/tests/SC-03_document-detail.md` #7 | **変える** | 事実が変わった |
| `docs/api/openapi.yaml`・`BFF_bff-surface.md`・`east-west-grpc.md` | **変える** | 契約の記述 |
| `.ai-context/adr/IADR-0038`（決定 3） | **日付つき追記** | 決定を改めた |
| 生成物 `platform/frontend/src/lib/api/generated/{bff.schemas.ts,documents/documents.ts}` | **変える** | openapi の 413 / 502・説明文の追随（orval の出力の形に合わせて手で写した。CI の再生成差分検査で確かめる） |
| Ingestion / Wiki / Graph の読み手の案内文（取り込みの縮退） | 変えない | 射程外（要求・応答の経路ではない） |
| `ConversionService` のプレースホルダ本文・`FR-12` / `UC-06` の文書 | 変えない | 変換の縮退で別物 |

規則 10（この変更で新たに誤りになる自分の記述）: proto の冒頭「書き込み・本文・共有の口はこの面に存在しない」・helm の document の注記
「書き込み・本文・共有は出していない」・`GrpcService.cs` の「読み取り 4 口」を追随した。`east-west-grpc.md` の 5 つ目の面の「書き込み・本文…は移さない」の表は
BFF の書き込み経路の話（本文投入）であり、本文の**読み取り**は別行なので表は変えず、日付つきの追記で足した。

## 受け入れ基準と試験の写像

| 受け入れ基準 | 試験 |
| --- | --- |
| AC-1 本文を返す（案内文ではない） | `GrpcDocumentContentTests.本文を返す`・`BffDocumentContentTests.本文を返す`・`BffDocumentEndpointTests.GetContent_WhenAuthorized_ReturnsMarkdownAndSourceUri`（本文の完全一致へ改めた） |
| AC-2 本文欠落（NoSuchKey）・参照が無いは 404 ＋ 問題詳細（200 の案内文にならない） | `GrpcDocumentContentTests.本文のオブジェクトが無いときはMISSINGで案内文を返さない`（型・コードの 2 形）・`本文の参照がストレージでなければNOT_STORED`・`BffDocumentContentTests.本文が無いときは404と問題詳細で案内文を返さない` |
| AC-2' 解決できない（ストレージ未構成・一時障害・バケットが無い）は 200 にならない | `GrpcDocumentContentTests.ストレージが構成されていなければFAILED_PRECONDITION`・`バケットが無いのはMISSINGではなくstatusで返す`・`BffDocumentContentTests.引けなかったときは502で案内文を返さない`（3 status）・`状態が分からない応答は502` |
| AC-3 権限外は従来の拒否（404 秘匿・本文の rpc を呼ばない） | `BffDocumentContentTests.権限外は従来の404で本文を引かない`・`後段で読めないときも秘匿の404`・`GrpcDocumentContentTests.他人の個人資料の本文は返らず所有者には返る`・既存 `BffPrivateNoteExclusionTests` / `BffSharedDocumentReadTests` / `BffOwnerReadSeedTests` の `/content` |
| AC-4 大きな本文の上限（16 MiB・切り詰めない・境界） | `GrpcDocumentContentTests.上限を超える本文は返さず大きさと上限を返す`（上限ちょうどは返す）・`上限はUTF8のバイトで数える`・`BffDocumentContentTests.上限を超える本文は413で大きさと上限を返す`・`本文の上限はD1チャネルの受信上限に収まる` |
| AC-5 配線試験（1 行消すと赤） | `DocumentContentDeploymentWiringTests`（helm・compose）・既存 `EastWestGrpcDeploymentWiringTests`（D-1） |
| 利用者文脈を運ぶ | `BffDocumentContentTests.本文の読み取りは呼び出し元の利用者を運ぶ`・`BffDocumentEndpointTests.Reads_carry_the_caller_as_the_user_context_and_not_the_token`（本文を足した） |
| s2s の門 | `GrpcDocumentContentTests.トークンの無い呼び出しは拒否される` |

### 変異試験（赤になることを確かめた）

| 変異 | 結果 |
| --- | --- |
| M1 文書サービス: NoSuchKey の catch を外す | 赤 2 件 |
| M2 文書サービス: NoSuchKey の判定を「状態コード 404」へ | 赤 2 件（NoSuchBucket・型の形） |
| M4 文書サービス: 上限の判定を外す | 赤 2 件 |
| M5 文書サービス: `>` を `>=` へ（境界） | 赤 1 件 |
| M6 文書サービス: バイトではなく文字数で数える | 赤 1 件 |
| B1 BFF: `MISSING` を案内文の 200 へ戻す（#1905 の再現） | 赤 1 件 |
| B2 BFF: 未知の状態を 200 へ畳む | 赤 1 件 |
| W1 helm の `services.document.objectStorage: true` を消す | 赤 |
| W2 compose の `document-service` から `*objectstorage-env` を外す | 赤 |

## 画面（SC-03）の確認

- `DocumentDetailPage.ContentView` は 200 の `markdown` を `pre` へそのまま出す。失敗は `QueryState` が「本文は利用できません。」＋状態の文言で出す。
  **案内文を前提にした表示は無い**（コードで確認）。原本の導線（`SourceLinks`）は本文の取得結果に依らず `doc.markdownUri` で出る。
- フロントのコードは変えない。生成物（orval）の型だけ 413 / 502 を足した（`useDocumentQueries` は `TError=unknown` を明示しているので影響しない）。
- 未決: 413 の文言は汎用（「要求が失敗しました（413）。」）。専用の文言にするには翻訳カタログの追加が要る（本件では見送り。報告に残す）。

## 検証

- `dotnet test` DocumentService.Tests（1047 件）・Platform.Bff.Tests（949 件・1 skip）・IngestionService.Tests（191）・Knowledge.Contracts.Tests（102）・
  Platform.Shared.Infrastructure.Tests（Storage / Wiring 103）すべて緑。
- `dotnet format --verify-no-changes`（変更ファイル）緑。`helm lint` 緑・`helm template` で document-service に `ObjectStorage__*`、bff に `Services__DocumentServiceGrpc` を確認。
- フロント（typecheck / codegen 差分 / vitest）は手元に node_modules が無く（ディスク逼迫）CI に委ねた。

## PoC での確認手順

1. 配備（document-service と bff の両方のイメージを更新。proto が変わるので**両方**が要る。document-service が旧いと bff は `UNIMPLEMENTED` → 502）。
2. `curl -s -o /tmp/body.md -w '%{http_code} %{size_download}\n' -b <セッション Cookie> https://<host>/bff/documents/<AST#565 の文書 ID>/content`
   → 期待: `200` かつバイト数が 381 ではない（本文の大きさ）。`jq -r .markdown` の先頭が `# 案内文` ではなく報告書の本文。
3. SC-03（`/docs/<id>`）で本文の位置に報告書の本文が出る。
4. 実体の無い文書（9/26 より前）で同じ curl → `404` と `{"type":"urn:microservices-platform:problem:document-body-unavailable","reason":"missing",…}`。
   document-service のログに `Document body object missing (NoSuchKey)`。
