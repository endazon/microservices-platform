---
title: 作業仕様書 — 緊急アクセス（Break-glass）の BFF の中継と SC-03 の閲覧の後段判定の素通し（#2046 の 2 本目）
type: spec
status: done
related_ids: [FR-19, UC-11, SC-19, SC-03, ADR-0036, ADR-0034, ADR-0029, IADR-0610, IADR-0611]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md（D-08）
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-19・SC-03）
  - planning#841（2026-10-23T14:21Z の裁定コメント）
---

# 仕様書: 緊急アクセスの BFF の中継（#2046 の 2 本目）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-19（受け入れ基準「緊急アクセス」）。ユースケース: UC-11（例外フロー）。画面: SC-19（発動の導線）・SC-03（管理者の閲覧の導線）。
- 計画 ADR: ADR-0036 D-08（本人だけが発動・第三者の発動経路なし・サーバ側の発動記録で判定・監査ログ）、ADR-0034 決定 9（機械の主体は個人資料を読まない）、
  ADR-0029（east-west gRPC）。
- 裁定: planning#841（本人同意型の管理者の閲覧。指名した platform-admin 1 人・資料 1 件・閲覧だけ・本人の早期終了・閲覧も 1 件ずつ記録・
  監査ログは platform-admin と本人）。計画書への反映は計画側。
- 前提: 1 本目（#2179、`39ecb45b`。IADR-0610・作業仕様書 `20261023_2046_break-glass-document-service`）。
- 実装 ADR: IADR-0611。issue: #2046（部分対応。3 本目の画面が残る）。

## 範囲

- BFF: 発動・終了・状態（所有者本人のセッションだけ）と監査ログの読み取りを DocumentService へ中継する。
- SC-03 の BFF の詳細・本文が、後段（`DocumentReadUseCase.GetForViewAsync`）の判定をそのまま通す（BFF が独自に許可・拒否を足さない）。
- 上のために要る最小の契約の追加（gRPC の要求の旗・応答の印）と、文書サービスの gRPC 面の追随。
- 契約: `docs/api/openapi.yaml` に 4 端点と 4 スキーマ、orval の生成物の再生成（`pnpm run codegen`）、proto の baseline。
- 含めない: 画面（3 本目）。

## 現状の実測（着手時）

- BFF の `GET /bff/documents/{id}` と `/content` は `FetchAuthorizedAsync` → `IsReadable`（IADR-0447 決定 4）を通る。管理者の ABAC は静的属性の
  分岐だけなので、**後段が緊急アクセスとして返した個人資料を BFF が 404 にする**（1 本目の仕様書の「BFF 側で個人資料を先に弾いていないか」の答えは「弾く」）。
- gRPC の `GetDocument` は 1 本目で常に緊急アクセスを含むようになった。BFF は版の一覧・特定版・書き込みの前段でも `GetDocument` を呼ぶので、
  指名された管理者がそれらを叩くと**閲覧が返らないのに閲覧の監査ログが増える**。
- BFF にレート制限の仕組みは無い（`grep -rni ratelimit` が BFF の 2 プロジェクトで 0 件）。CSRF は全体のミドルウェア（`CsrfHeaderMiddleware`）が
  セッション Cookie つきの状態を変える動詞に効く。新設しない。

## 受け入れ基準

- [x] AC1 無認証は 4 端点とも 401（後段へ届かない）
- [x] AC2 所有者本人の発動・終了・状態は中継され、本人の資格情報が後段へ届く。発動の要求本文は詰め替えない
- [x] AC3 第三者・指名外の管理者・指名された管理者・機械の主体（所有者の資格情報を載せても）・組織文書は、発動・終了・状態とも 404 で後段へ送らない。
      **後段が誤って誰にでも許す状態でも止まる**（BFF の確認だけで止まる）
- [x] AC4 BFF の確認を通った要求は後段の判定に委ねる: 後段の 404・409・400・503 が本文ごと届く
- [x] AC5 本人の確認で後段に届かなければ 502（「無い」にしない）
- [x] AC6 発動だけ `write` スコープを要する（無ければ 403・後段へ送らない）。終了と状態は要しない
- [x] AC7 監査ログ: 管理者には全件・本人には自分の行だけが届く（絞るのは後段。各要求は呼び出し者の資格情報で中継）。機械は 403。
      `documentId` / `limit` は型で受けて組み立て直し、知らないクエリは渡さない。UUID でない `documentId` は 400
- [x] AC8 SC-03: 指名された管理者は有効な間、詳細と本文を開ける（BFF の ABAC の静的分岐では読めないもの）。閲覧の記録は詳細 1・本文の前段 1・本文 1
- [x] AC9 BFF は拒否を足さない: ABAC のスコープが無くても後段が許せば開ける（通常の判定で読めるもの〔所有者〕は従来どおり ABAC を AND で重ねる）
- [x] AC10 BFF は許可を足さない: 指名外の管理者・第三者・発動なしは詳細も本文も 404、閲覧の記録なし
- [x] AC11 機械の主体は SC-03 でも緊急アクセスで開かない（利用者を名指さない）
- [x] AC12 版の一覧・特定版・書き込みの前段・本人の確認は緊急アクセスを含めずに引き、閲覧の記録を作らない
- [x] AC13 通常の判定で開いた本文は本文の rpc に緊急アクセスを頼まない
- [x] AC14 文書サービスの gRPC: 旗が無ければ指名された管理者も読めず記録もしない。旗があれば読め、印が立ち、共有先の写しは載らず、記録は 1 行
      （属性・本文それぞれ）。所有者は旗があっても印が立たず記録もしない。指名外の人（管理者でも）は旗があっても読めない

## 設計

IADR-0611 を正とする。要点:

- proto（`knowledge.document.v1`）: `GetDocumentRequest.allow_break_glass = 3`・`GetDocumentContentRequest.allow_break_glass = 3`・
  `GetDocumentResponse.granted_by_break_glass = 3`（いずれも追加。非破壊）。
- 文書サービス: `DocumentReadUseCase.FindForViewAsync(principal, id, viewTarget, allowBreakGlass)` が `DocumentView(Document, ViaBreakGlass)` を返す。
  `GetForViewAsync`（REST）は `allowBreakGlass: true` の写し。gRPC は要求の旗をそのまま渡し、印を応答へ載せる。
  `DocumentContentUseCase.ReadAsync` は旗を必須の引数で受ける（既定値を置かない —— 将来の呼び出し元が黙って開かないように）。
- BFF:
  - `DocumentReadGrpcClient.GetForViewAsync`（旗を立てる。印を返す）、`GetContentAsync(…, allowBreakGlass)`（既定は立てない）。
  - `DocumentBffEndpoints.FetchForViewAsync`（詳細・本文の前段）: 印が立てばそのまま、立たなければ `IsReadable` を AND。
  - `BreakGlassBffEndpoints`（新設）: 4 端点。`EnsureOwnNoteAsync`（人の主体 ∧ 旗なしの `GetDocument` で個人資料 ∧ `owner` 一致）。
  - 合成は `MapPrivateNoteBffEndpoints` の末尾から呼ぶ（合成点の行は増やさない。ルート群は `/bff/break-glass` が 1 つ増える）。

## 母集合の取り方（規則 9・10）

- BFF から gRPC `GetDocument` を呼ぶ箇所: `grep -rn "GetAsync(http.User\|GetForViewAsync(http.User" src/knowledge/backend/Bff` —
  `FetchAuthorizedAsync`（版の一覧・特定版・書き込みの前段）・`FetchForViewAsync`（詳細・本文。新設）・`EnsureOwnNoteAsync`（新設）。
  旗を立てるのは `FetchForViewAsync` だけ。
- gRPC `GetDocumentContent` を呼ぶ箇所: `DocumentBffEndpoints` の本文の口 1 つ。
- 文書サービスの `GetForViewAsync` / `DocumentContentUseCase.ReadAsync` の呼び出し元: `grep -rn "GetForViewAsync\|contents.ReadAsync"` —
  REST `GET /documents/{id}`（変えない）・gRPC 2 口（旗を渡す）・1 本目の試験 2 か所（`ReadAsync` に旗を明示）。
- 文書の記述の追随: `grep -rln "緊急アクセス" docs` — FR-19 機能仕様書（⑥の注記と §緊急アクセスの表）・FR-19 テスト仕様書（行 26）・
  BFF 境界の通信仕様書（4 行）。SC-19 画面仕様書・SC-19 画面テスト仕様書は 3 本目（画面）で更新する（本 PR は画面を含まない）。
  `openapi.yaml` の詳細・本文の説明に緊急アクセスの 1 文を足した。

## 検証

- `dotnet build` / `dotnet test`（DocumentService.Tests・Platform.Bff.Tests）/ `dotnet format --verify-no-changes`
- 変異（PR 本文に表）: BFF の本人の確認を外す・印を無視する・詳細で旗を立てない・本人の確認で旗を立てる・本文で旗を立てない・発動の write を外す・
  機械を通す（2 か所）・すべての前段で旗を立てる・印に ABAC を重ねる／文書サービスで旗を無視する（2 口）・印を立てない・ユースケースで旗を無視する
- 契約: `check-proto-contracts`（baseline 更新）・`check-openapi-dto-drift`・`check-bff-authz-docs`・`pnpm run codegen` の再生成で差分が出ないこと

## 残余リスク・未決

- 本文の閲覧は前段の詳細の 1 行と本文の 1 行の 2 行になる（画面が詳細と本文を開けば 3 行）。少なく記録する向きではないので受容（IADR-0611 結果）。
- ABAC のスコープが無い利用者の詳細・本文でも後段を 1 回呼ぶようになった（従前は呼ばずに 404）。
- 画面が無い間は利用者の操作の経路は無い（3 本目）。
