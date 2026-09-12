---
title: IADR-0450 共有先の写し（`DocumentDto.SharedWith`）は所有者にだけ返す — BFF が利用者へ返す直前の 1 点で落とし、所有者は「`owner` 分岐で許可されたか」で決める
type: impl-adr
status: Accepted
related_ids: [FR-19, UC-11, SC-03, SC-19, ADR-0036, ADR-0098, IADR-0253, IADR-0396, IADR-0447, IADR-0448, IADR-0450]
author: Claude
created: 2026-09-13
updated: 2026-09-13
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
related_specs:
  - ../specs/20260913_1451_shared-with-copy-owner-only-response.md
---

# IADR-0450: 共有先の写しは所有者にだけ返す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-13
- 決定者: Claude（実装）。裁定は利用者（planning#626）

## 起点・関連

- 関連する計画書 ID: FR-19・UC-11・SC-03（文書詳細＝`GET /bff/documents/{id}` の消費面）／ADR-0098 §結果 **フォローアップ 5**（「受容するか、応答から共有先を落とすかは別途裁定する」）／ADR-0036 D-05・D-06
- 関連 issue: planning#626（裁定記録。ADR-0098 フォローアップ 5）、#1451（本作業）、#1447（写しを載せた元の作業）
- 関連 IADR: IADR-0447（決定 4「所有者が写しを運ぶ」。§結果 残余リスクに本件を記録していた）・IADR-0448（`AttributeFilterMatch`）・IADR-0253（分岐の選言）・IADR-0396（個人資料の露出）
- 作業仕様書: `../specs/20260913_1451_shared-with-copy-owner-only-response.md`

## コンテキストと課題

IADR-0447 決定 4 で DocumentService は応答 `DocumentDto.SharedWith` に共有台帳の写し（利用者名・グループ ID）を載せ、BFF はそれを `shared_with` の集合値属性として判定に使う。判定後の `GET /bff/documents/{id}` は DTO をそのまま返していたため、**共有先ベースの分岐で読めた相手（所有者ではない）にも `sharedWith` が届き、他の共有先の識別子を読めた**。ADR-0098 フォローアップ 5 はこれを「受容するか、応答から共有先を落とすか」の裁定事項とし、利用者は **「所有者だけに返す」** を選んだ（planning#626・2026-09-13）。

実測（2026-09-13）:

- 利用者へ `DocumentDto` を返す口は BFF の `GET /bff/documents/{id}`（詳細）と `GET /bff/documents`（SC-05 の一覧。管理者・運用者）の 2 つ。`/content`・`/versions` は別 DTO で `sharedWith` を持たない。作成・更新の中継（`RelayAsync`）は管理者限定の組織文書であり、作成直後に共有は無い。
- 検索（`SearchResultDto.Attributes`）は `c.Attributes` だけを写し、`shared_with` は Qdrant / InMemory の判定用ペイロードに別項目で持つ（応答に出ない）。グラフはノードの属性を利用者へ返す口を持たない。**露出経路は BFF の 2 口だけである。**
- SPA は `sharedWith` を読んでいない（`src/*/frontend/src` で生成物以外の参照 0 件。SC-19 の共有先一覧は `DocumentShareDto` の口を使う）。落としても画面は変わらない。
- BFF は `${current_user}` の値を知らない（束縛は認可サービス。BFF へは分岐の中の値として届く）。

## 検討した選択肢

| 案 | 判定 |
| --- | --- |
| **BFF が返す直前の 1 点で、所有者でない閲覧者の `SharedWith` を落とす。所有者かどうかは「許可した分岐のうち `owner` を条件に持つ分岐が一致したか」で決める** | **採る** |
| BFF が `owner` 属性と現在の利用者名（`HttpContext.User.Identity.Name`）を直接比べる | 採らない。**所有者判定の 2 本目の軸**になる（`IsManageable` が所有者判定を持ち込まない理由と同じ）。`${current_user}` の束縛は認可サービスの仕事で、BFF が利用者名の解決を持つと 2 か所が食い違い得る |
| DocumentService が呼び出し主体を見て落とす | 採らない。後段は BFF が渡す主体を知らず（ADR-0088: 主体は認可サービスが IdP から引く）、判定の実施点は BFF である。索引・イベントの写しは判定に要るので落とせない |
| 所有者以外には空集合 `[]` を返す | 採らない。空でない集合を返す実装との差で「共有の有無」の 1 ビットが漏れ、「共有が無い」と「見せていない」が応答の形で区別できない |
| 自分（または所属グループ）に当たる項目だけ返す | 採らない（裁定で退けた案 3）。BFF に所属の突合が増え、所有者判定と同じ 2 本目の軸になる |

## 決定

1. **`DocumentBffEndpoints.ForViewer(doc, scope)`** を利用者へ返す直前の唯一の点とし、詳細（`FetchAuthorizedAsync` の戻り）と一覧（`IsManageable` の後）の両方がここを通る。**判定へ渡す像（`AuthzView`）は変えない**（読めるかどうかは 1 ビットも変わらない）。
2. **所有者の判定は `GrantedAsOwner`**: 許可した分岐（`Branches`。未移行の応答は `Filters` の連言を 1 分岐と見る）のうち、`PrivateNoteVisibility.OwnerKey`（`owner`）を条件に持つ分岐が `AttributeFilterMatch.MatchesAll` で一致すれば所有者。述語・キーは既存のものを再利用し、新しい語彙を持たない。
3. **所有者以外には項目ごと落とす（`null`）。** 空集合を含め、所有者以外には `SharedWith` を運ばない。`null` は契約上「共有なし」と同じ読み（`DocumentDto` の既定）であり、旧応答と同じ形に畳む。
4. **`DocumentDto` を `record` にする。** `doc with { SharedWith = null }` で写しを作るため。項目を 1 つずつ写す複製は、項目を足したときに黙って落ちる。JSON の形・既定値は変わらない（契約は不変。`check-openapi-dto-drift` / `check-contract-schema` は緑）。
5. 契約の注記（`openapi.yaml` の `DocumentDto.sharedWith`）・BFF 通信仕様書・権限仕様書に「所有者にだけ返す」を書き、IADR-0447 の残余リスクを閉じる。

## 結果

- **良い影響**: 共有された相手は「他に誰と共有されているか」を応答から読めない。ADR-0098 決定 1「識別子は出さない」と同じ向きに閉じる。SPA の変更は無い。
- **悪い影響 / 残余リスク**:
  - `owner` を条件に持つポリシーが配備されていない環境では、所有者にも `sharedWith` が返らない。ただしその環境では所有者は `GET /bff/documents/{id}` 自体を読めない（`IsReadable` は裁量の分岐を要求する）ので、「読めるのに写しだけ無い」形にはならない。所有者向けの共有先一覧は SC-19 の `DocumentShareDto` の口が持つため、機能上の欠けも無い。
  - 所有者の判定を「`owner` 分岐が一致したか」に委ねるため、`owner` を条件に持つ分岐に `owner` 以外の条件が混ざるポリシー（例: `owner` かつ `confidentiality`）でも、一致すれば所有者と見なす。`owner` の値は `${current_user}` に束縛されるので、他人が一致することは無い。
  - 管理者・運用者の一覧（SC-05）は組織文書だけを返し、共有は個人資料の機能であるため実質は空だが、同じ 1 点を通すことで経路の割れを作らない。
- 計画側は本裁定を ADR-0101（ADR-0098 の補完。起案中）として記録する。
