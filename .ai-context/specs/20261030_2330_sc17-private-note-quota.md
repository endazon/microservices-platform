---
title: 作業仕様書 — SC-17 に個人資料の容量（使用量・上限）の欄と BFF の経路を置き、上限 0 を受け付ける（#2330）
type: spec
status: done
related_ids: [FR-19, NFR-27, SC-17, SC-19, ADR-0037, ADR-0036]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2330"
---

# 作業仕様書 — SC-17 の個人資料の容量（#2330）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `3c380992`）。親 #2135（BFF に経路の無い後段の端点の分類）。

## 計画（確定済み）

`05_screens` §SC-17（2026-10-23 追加・2026-10-24 追加の裁定。環流 planning#835）は、次を定める。

- 利用者の行ごとに、個人資料の**使用量**と**上限**を示し、上限を変更できる。
- 上限は 0〜1 TB、既定は 1 GB（NFR-27）。変更は監査ログに残す。
- **0 を許す**（2026-10-24）。0 は「新しい資料を作れない状態」である。
  - 扱いは FR-19 の容量 100% 到達（ADR-0037 決定 17）と同じにする。新規作成（SC-19 の作成・同期の新規 push・同期競合の別名の資料）を拒み、既存の資料の閲覧と更新は許す。
- 見せるのは使用量と上限の 2 つの数だけである。題名・件数・中身・同期設定は見せない（SC-19 の「管理者が他の利用者の個人資料を閲覧する画面を描かない」）。
- 操作はシステム管理者が行う（SC-17 の画面の資格）。

## 現状（develop で確認）

- 後段 DocumentService の `GET/PUT /private-notes/quotas/{ownerId}`（`AdminOnly`）と、監査 `private-note.quota.set` は在る。
- 🔴 `PrivateNoteQuota.SetLimit` は 0 を拒む（`limitBytes <= 0`）。
- `RejectsNewNote(used, new)` は `used >= limit || used + new > limit` なので、**上限 0 なら使用量 0 でも空の資料（0 バイト）の作成を拒む**。0 を受け付けさえすれば、新規作成の拒否はそのまま効く。更新はこの判定を通らない。
- BFF の経路と SC-17 の欄は無い。
- 入力の型 `SetQuotaRequest` は後段のフォルダに在る（BFF と共有する相手が居なかったため）。

## 方針

1. **後段**:
   - `SetLimit` の範囲を `0 ≤ limit ≤ 1 TB` にする。誤りの文言も直す。
   - 0 のときの新規作成の拒否と、更新の許可は、既存の判定のままでよい（試験で固定する）。
   - `PercentOf` は上限 0 で 100 のままにする（100% 到達と同じ扱い）。
2. **契約**: 入力の型を `Knowledge.Contracts` の `SetPrivateNoteQuotaRequest(long LimitBytes)` へ移す。BFF と後段が同じ型を使う。応答は既存の `PrivateNoteUsageDto`（使用量・上限・割合）で、2 つの数（と割合）だけを返す。
3. **BFF**: `GET/PUT /bff/private-notes/quotas/{ownerId}` を置く。
   - **システム管理者だけ**（`AdminOnly`）。後段も `AdminOnly` で、二重の守りになる。
   - 資格情報を伝播し、本文ごと透過する（400・403・404・502）。
   - ABAC の write の前段は置かない（文書ではなく利用者の容量の設定であり、ロールで絞る）。
4. **SC-17**:
   - 利用者の一覧に「個人資料の容量」の列を置く。行ごとに「使用量 / 上限」を表示する（1 行 1 照会。TanStack Query のキャッシュ）。
   - 編集の区画に「個人資料の保存容量の上限」の欄を置く（数値と単位 MB / GB / TB。0〜1 TB）。保存で PUT を送る。
   - 範囲外は画面で押させない（後段も 400）。
   - 題名・件数・中身・同期設定は出さない（応答にも無い）。
   - 上限 0 には「新しい資料を作れません（既存の資料の閲覧と更新はできます）」を添える。
5. **文書**:
   - SC-17 の画面仕様書・テスト仕様書。
   - 運用仕様書の「API を直接呼ぶ暫定の手順」を、画面からの手順へ改める。
   - FR-19 の機能仕様書の上限の範囲。
   - openapi。

## ファイル領域の直列化

#2346 と交差しない。BFF の試験基盤 `BffTestFactory.cs` は #2346 が触るので、本 PR は触らない。BFF の試験は、試験クラスの中で後段の HttpClient だけを差し替えて書く。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "1 バイト以上\|limitBytes <= 0\|SetQuotaRequest\|quotas/{ownerId}\|quotas/"` で、追跡下（submodule と凍結記録を除く）を走査する。結果は実装中の追記に書く。
- **規則 10**: 次の記述が誤りになる。
  - 後段の注釈「1 バイト以上」。
  - BFF の注釈「まだ載せていない（#2330）」。
  - 運用仕様書の「API を直接呼ぶ」暫定の手順と「0 は設定できない」。
  - FR-19 の機能仕様書の上限の範囲。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 上限 0 を後段が受け付ける（1 TB 超は 400 のまま） | `PrivateNoteQuotaTests` | 赤（0 は 400） |
| 上限 0 では新規作成（SC-19 の作成・同期の新規 push）を拒み、既存の資料の更新は許す | 同 | 赤（0 を設定できない） |
| BFF: システム管理者は照会・変更でき、資格情報と本文が後段へ届き、応答が透過する | `BffPrivateNoteQuotaTests` | 赤（経路が無い） |
| BFF: 管理者でない（運用者・一般利用者）は 403 で後段へ行かない。未認証は 401 | 同 | 赤 |
| BFF: 後段の 400 は本文ごと透過する。不達は 502 | 同 | 赤 |
| SC-17: 行に「使用量 / 上限」が出て、題名・件数は出ない | `UserAccountManagementPage.test.tsx` | 赤 |
| SC-17: 編集で上限を変えると PUT（バイト）を送る。0 は送れて注記が出る。1 TB 超は押せない | 同 | 赤 |

## 実装中の追記（2026-10-30）

### 母集合（規則 9）の走査結果

`git grep -n "1 バイト以上\|limitBytes <= 0\|SetQuotaRequest\|quotas/{ownerId}\|private-notes/quotas\|0 は設定できない\|API を直接\|#2330"`（submodule・`.ai-context/specs/`・`.ai-context/superpowers/`・生成物・`CHANGELOG.md` を除く）。

| 当たり | 扱い |
| --- | --- |
| `docs/functional/FR-19_private-notes.md`（上限変更「1 バイト〜1 TB」・保存容量の規則） | 直した（0〜1 TB・0 の扱い） |
| `docs/data/private-note.md`（`LimitBytes` の制約） | 直した |
| `docs/api/BFF_bff-surface.md`（「BFF に通す（未了）」の行） | 直した（通した。admin のみの 2 行を表へ足した） |
| `docs/api/FR-20_obsidian-sync.md`（上限管理は BFF に載せていない） | 経過追記と表の「BFF」欄を直した |
| `docs/api/openapi.yaml` の注釈（載せていない後段の口に quotas） | 直した |
| `docs/tests/FR-19_private-notes-lifecycle.md` の #6 | 0 を許す旨と 6b を足した |
| 後段・BFF の注釈（`PrivateNote.cs`・`SetQuota/Endpoint.cs`・`PrivateNoteBffEndpoints.cs`） | 直した（追記で経緯を残した） |
| IADR-0348・`deploy/local/edge-istio/virtualservice-app.yaml`・`scripts/k8s-local-up.test.js`（エッジから `/private-notes/quotas/*` は届かない） | 変えない（BFF の経路は `/bff/*` でエッジの扱いが別。後段の口をエッジへ出さない前提は変わらない） |
| 「API を直接」のその他の当たり（SC-07・SC-12 など） | 本件と無関係 |

- 🔴 方針 5 で挙げた「運用仕様書の API を直接呼ぶ暫定の手順」と「0 は設定できない」は、**走査で 1 件も当たらなかった**（手順書は書かれていなかった。暫定の手順を述べていたのは BFF 境界の通信仕様書の 1 行だけ）。記憶で挙げた追随先が実在しなかった例として残す（規則 9）。

### 結果

- 後段: `PrivateNoteQuotaTests` 赤 → 緑（0 は 200・負は 400・上限 0 の新規作成の拒否と更新の許可）。
- BFF: `BffPrivateNoteQuotaTests` 11 件 赤 → 緑。`BffTestFactory.cs` は触っていない（#2346 と交差するため）。
- SC-17: `UserAccountManagementPage.test.tsx` の 2 件 赤 → 緑。純関数 `privateNoteQuota.test.ts`。
- 初期ロードの床: 651,762 → 652,466 B（Lingui の訳 7 文の分。`check-chunk-budget.js --update`）。
- 契約の baseline: `SetPrivateNoteQuotaRequest` の追加（`check-contract-schema.js --update`）。
- 手元の環境: `WikiSlugNonUniquePostgresTests` は接続文字列が無いので落ちる（本変更と無関係）。

