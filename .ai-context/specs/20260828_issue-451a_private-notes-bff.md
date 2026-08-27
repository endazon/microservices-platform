---
title: 作業仕様書 個人資料と同期設定の BFF 端点 `/bff/private-notes*` を公開する（#451-a）
type: spec
status: in-progress
related_ids:
  - FR-19
  - FR-20
  - UC-11
  - SC-19
  - SC-20
  - ADR-0034
  - ADR-0036
  - ADR-0037
  - ADR-0046
  - ADR-0054
  - IADR-0009
  - IADR-0039
  - IADR-0041
  - IADR-0044
  - IADR-0253
  - IADR-0270
  - IADR-0272
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md (SC-19 / SC-20)
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
related_specs:
  - ./20260823_issue-451_private-note-obsidian-sync-core.md
  - ../adr/IADR-0270_private-note-obsidian-sync-backend-core.md
---

# 仕様書: 個人資料・同期設定の BFF 端点（#451-a）

> **この作業で #451 は閉じない。** 入れるのは **BFF 端点だけ**である。
> SC-19 / SC-20 の画面（フロントエンド）・orval のフック生成・Obsidian プラグイン本体は
> 後続であり、§対象範囲「対象外」に理由と送り先を書く。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-19（個人資料）・FR-20（Obsidian 双方向同期）
- ユースケース（UC）: UC-11
- 画面（SC）: SC-19（個人資料管理）・SC-20（Obsidian 連携設定）—— **本作業は画面が呼ぶ口だけ**
- 関連 ADR: `ADR-0036`（所有者ベース裁量制御。D-07 write 規則 / D-08 管理者の非閲覧）/
  `ADR-0037`（同期方式。決定 10〜15 の端末・トークン、17・19・20 の容量と完全削除）/
  `ADR-0046`（本文編集は Obsidian 経路のみ）/ `ADR-0054`（API リソース名は `private-notes`）
- 実装 IADR: 前提 `IADR-0270`（DocumentService 側の中核。本作業が公開する後段）/
  `IADR-0041`（BFF が ABAC スコープ照合の実施点）/ `IADR-0044`（多層防御）/
  `IADR-0009`（存在秘匿）/ `IADR-0039`（ABAC のスコープ対象でない資源の扱い）/
  `IADR-0272`（#1010: `BffScopeResolver` の action は既定値の無い必須引数）

## 母集合（着手前の実測。`.claude/rules/traceability.repo.md` 規則 1〜10）

**走査時点は本仕様書をコミットする前**である（規則 8。本仕様書自身は数に入っていない。
本ファイルが入ると `private-notes` は 21 → 22 になる）。除外は `obj` / `bin` / `node_modules` /
`.git` / `TestResults`（生成物）と `src/ai-stock-trading`（別プロジェクトの submodule）のみで、
**拡張子では絞っていない**（規則 3）。

### 軸 1: 後段（DocumentService）の端点を 1 つ残らず数える

`Map(Get|Post|Put|Delete)(` をパスで引いた**全 17 件**。これが「BFF で公開する面の選定」の母集合である。

| # | 後段のパス | メソッド | 群の認証 | BFF で公開するか |
| --- | --- | --- | --- | --- |
| 1 | `/private-notes/` | GET | JWT（ロール不要） | **する**（SC-19 一覧・容量） |
| 2 | `/private-notes/` | POST | JWT | **する**（SC-19 新規作成） |
| 3 | `/private-notes/{id}` | DELETE | JWT | **する**（SC-19 論理削除） |
| 4 | `/private-notes/{id}/restore` | POST | JWT | **する**（SC-19 復元） |
| 5 | `/private-notes/purge` | POST | JWT | **する**（SC-19 完全削除・単票／一括） |
| 6 | `/private-notes/{id}/exposure` | PUT | JWT | **する**（SC-20 露出 3 トグル） |
| 7 | `/private-notes/quotas/{ownerId}` | GET | JWT ＋ 管理者 | **しない**（下記 除外 A） |
| 8 | `/private-notes/quotas/{ownerId}` | PUT | JWT ＋ 管理者 | **しない**（下記 除外 A） |
| 9 | `/private-notes/devices/` | GET | JWT | **する**（SC-20 端末一覧） |
| 10 | `/private-notes/devices/` | POST | JWT | **する**（SC-20 トークン発行） |
| 11 | `/private-notes/devices/{id}/reissue` | POST | JWT | **する**（SC-20 手動再発行） |
| 12 | `/private-notes/devices/{id}` | DELETE | JWT | **する**（SC-20 個別失効） |
| 13 | `/private-notes/devices/revoke-all` | POST | JWT | **する**（SC-20 一括失効） |
| 14 | `/private-notes/sync/manifest` | GET | **同期トークン** | **しない**（下記 除外 B） |
| 15 | `/private-notes/sync/notes` | POST | **同期トークン** | **しない**（除外 B） |
| 16 | `/private-notes/sync/notes/{id}` | GET | **同期トークン** | **しない**（除外 B） |
| 17 | `/private-notes/sync/notes/{id}/delete` | POST | **同期トークン** | **しない**（除外 B） |

**除外 A（上限管理 2 件）**: 計画 SC-19 が「**管理者が他の利用者の個人資料・同期設定を閲覧・変更する
導線は設けない**」と明記している（05_screens SC-19 主アクター／「描いてはいけないもの」）。
上限の引き上げは管理者運用であり、**それを載せる画面が計画に無い**。BFF は画面のための口であるから、
画面が無いものを先に開けない。後段の口は残っており、管理画面が起案された時点で別 issue が開ける。

**除外 B（同期プロトコル 4 件）**: 資格情報が**別系統**である（`ADR-0037` 課題 2。同期トークンの
Bearer であって BFF セッションの JWT ではない）。呼び出す主体は Obsidian プラグインであり、
**ブラウザ SPA は 1 度も呼ばない**。`/bff/*` 配下へ載せると 1 本の経路に 2 系統の資格情報が混ざる。
加えて `scripts/check-bff-authz-docs.js` は「`/bff/*` に無認証の端点は存在しない」を不変条件として
おり、同期トークン認証は群の認可として見えないため**構造的にも載らない**。

### 軸 2〜5: 語彙の側から引く（既存と食い違う実装を作らないため）

| 走査語（あり得る形） | 件数 | 主な所在と本作業への含意 |
| --- | --- | --- |
| `bff/private-notes` | 2 | いずれも「未実装の残件」と書いた文書（作業仕様書 20260823・`docs/api/FR-20_obsidian-sync.md`）。**本作業で誤りになるので追随する**（規則 10） |
| `PrivateNoteBff` | 0 | 実装は 0 件。新規である |
| `private-notes` | 21 | 後段 4 ファイル・後段テスト 4・`DocumentBffEndpoints`（SC-05 からの除外）・`scripts/test-spec-coverage-baseline.json`・specs 3・IADR 1・docs 8 |
| `PrivateNoteDto` / `SyncDeviceDto` | 2 / 2 | **すべて DocumentService 側**（`Knowledge.Contracts` に契約 DTO は無い＝本作業が置く） |
| `obsidian-sync` | 24 | 仕様書・IADR・docs の見出しとファイル名。実装の綴りに影響しない |
| `BffScopeAction` | 4 | `BffScopeResolver`（定義）・`DocumentBffEndpoints`・`GraphBffEndpoints`・テスト。**action の渡し方はこの 4 件に倣う** |
| 陽性対照 `confidentiality` | 229 | 走査形が効いていることの対照 |

**含意**: 応答 DTO は後段（`DocumentService.Api.Foundation.Endpoints`）にしか無く、BFF は
ユニット外参照ができない。`Knowledge.Contracts/Dtos` へ**契約としての写し**を置く
（`DocumentBffEndpoints` が `IsPrivateNote` を持つのと同じ理由・同じ形）。名前と形は後段に一致させ、
`docs/api/openapi.yaml` の `components.schemas` と `scripts/check-openapi-dto-drift.js` で固定する。

## 対象範囲

- **対象（本 PR）**:
  1. `Knowledge.Bff.Endpoints/PrivateNoteBffEndpoints.cs`（新規）—— 上表の 11 件を
     `/bff/private-notes*` として公開する
  2. `Knowledge.Contracts/Dtos/PrivateNoteDto.cs`（新規）—— 応答・要求の契約 DTO
  3. 合成点（`BffEndpointComposition`）への 1 行追加と、合成点テストの期待値の更新
  4. `docs/api/openapi.yaml` への契約追加（`x-roles` 込み）
  5. BFF テスト（`Platform.Bff.Tests`）と、DocumentService スタブの個人資料経路
  6. 「BFF は未実装の残件」と書いた既存文書の追随（規則 10）
- **対象外（理由と送り先）**:
  - **SC-19 / SC-20 の画面**（`src/*/frontend/**`）—— 後続の画面 issue。本 PR は画面が呼ぶ口の契約を確定させる
  - **orval のフック生成**（`pnpm run codegen`）—— 統括が波の末尾で 1 回まとめて回す。
    生成物は `docs/api/openapi.yaml` の `/bff/` 配下から起こるので、**本 PR の契約がその入力になる**
  - **管理者の上限変更 UI と BFF**（除外 A）・**同期プロトコルの BFF 公開**（除外 B）
  - **Obsidian プラグイン本体**（`ADR-0037` 決定 1）
  - **露出トグル ON の消費側**（`IADR-0253` 段 3 の完了待ち。`IADR-0270` 決定 5 で deny 側に倒してある）

## 認可の設計（本作業の核心）

**個人資料は本人のみ**である。BFF がその「本人性」をどう担保するかを、層ごとに分けて書く。

1. **認証は必須**（群の `RequireAuthorization()`）。無認証は 401。
   `/bff/*` に無認証の端点を作らない不変条件（NFR-09 の暫定運用・`check-bff-authz-docs.js`）を満たす。
   **ロールは要求しない** —— 計画 05_screens は SC-19 / SC-20 を「**全利用者が利用でき、表示範囲は
   本人が所有する個人資料に限る**」と定めており、書かれていない制限を足さない。
2. **本人性の判定は後段（DocumentService）に委ねる。** 後段は主体を**トークンからしか採らず**
   （`PrivateNoteEndpoints.SubjectOf`）、台帳 `PrivateNote.OwnerId` との一致で絞り、他者の資料は
   **404 で存在ごと秘匿する**（`IADR-0270`）。**BFF はこの判定を複製しない** ——
   判定軸を 2 本持つと片方が壊れても気付けない（`DocumentBffEndpoints` が所有者判定を持ち込まないと
   決めたのと同じ理由）。**BFF の仕事は利用者の資格情報を後段へ確実に引き渡すこと**である。
   🔴 **したがって「Authorization の転送」が本 PR における本人絞りの実体である。** 落とすと後段は
   主体を決められず 401 になる —— 変異試験 1 はここを壊して検出力を測る。
3. **書き込みは ABAC の `write` スコープで前段を絞る**（`ADR-0036` D-07・#1010 / `IADR-0272`）。
   `BffScopeResolver.ResolveAsync(..., BffScopeAction.Write, ...)` が deny なら **403**。
   `POST /bff/documents` と同じ姿であり、**write ポリシーが 1 件も無い環境では書き込みが全件 403 になる**
   （deny-by-default の正しい帰結。配備時に write ポリシーの登録が前提になる）。
4. 🔴 **読み取りには ABAC の前段を置かない。** 理由を 2 つ記す。
   - **秘匿する相手が居ない。** 返すのは**呼び出し者自身の資料だけ**であり、`/bff/documents` の
     ように「権限外の存在を隠す」対象が無い。
   - **解決済みスコープを適用する手段が無い。** `BffScopeResolver.Matches` は**文書属性**を見るが、
     一覧応答（`PrivateNoteListResponse`）は台帳の投影であって属性を持たない。属性を持たない資料へ
     フィルタを当てれば**全件不一致**になり、利用者は自分の資料を 1 件も見られなくなる
     （`MatchesAll` は「キーを持たない文書は不一致」＝安全側に倒す実装である）。
   **代わりに後段の所有者スコープが唯一の実施点である**（上の 2）。この非対称は
   `IADR-0039`（ABAC のスコープ対象でない資源はロール／所有者で絞る）と同じ切り分けである。
5. 🔴 **端末・トークン群（`/bff/private-notes/devices*`）には ABAC の前段を置かない。**
   トークンは**文書ではなく本人の資格情報**である。加えて計画 SC-20 は
   「**個別失効は端末紛失時の唯一の防御線であり必須**」と定めている ——
   失効を文書 ABAC ポリシーの有無に依存させると、**ポリシー未整備の環境で紛失端末を失効できない**。
   安全側は「失効は通す」である。所有者スコープは後段が台帳で担保する（他者の端末は 404）。
6. **BFF は主体の口を作らない。** `ownerId` 等をクエリ・本文で受けない（後段と同じ規律）。

### 認可が及ばない範囲（過大申告しない）

**3 の write ゲートは、個人資料の作成に対する封じ込め境界ではない。** 同期プロトコル
（`/private-notes/sync/notes`）は同期トークンだけで資料を作れる —— これは `ADR-0037` が
意図した設計（別系統の資格情報）であり、本 PR が作った穴ではない。write ゲートは
**画面経路の多層防御の 1 枚**（`IADR-0044`）であって、それ以上ではない。

## 公開する端点（BFF パス → 後段パス・action）

| BFF | 後段 | action | 応答 |
| --- | --- | --- | --- |
| `GET /bff/private-notes` | `GET /private-notes/` | —（読み取り。前段なし） | `PrivateNoteListResponse` |
| `POST /bff/private-notes` | `POST /private-notes/` | `write` | 201 / 400 / 409 / **507** |
| `DELETE /bff/private-notes/{id}` | `DELETE /private-notes/{id}` | `write` | 200（`capacityFreed=false`）/ 404 |
| `POST /bff/private-notes/{id}/restore` | `POST /private-notes/{id}/restore` | `write` | 200 / 404 / 409 |
| `POST /bff/private-notes/purge` | `POST /private-notes/purge` | `write` | 200 / 400 / 404 / 409 |
| `PUT /bff/private-notes/{id}/exposure` | `PUT /private-notes/{id}/exposure` | `write` | 200 / 404 |
| `GET /bff/private-notes/devices` | `GET /private-notes/devices/` | —（読み取り） | `SyncDeviceDto[]` |
| `POST /bff/private-notes/devices` | `POST /private-notes/devices/` | —（資格情報。上の 5） | 201（**平文トークンは 1 回だけ**） |
| `POST /bff/private-notes/devices/{id}/reissue` | 同 | —（同上） | 200 / 404 |
| `DELETE /bff/private-notes/devices/{id}` | 同 | —（同上） | 204 / 404 |
| `POST /bff/private-notes/devices/revoke-all` | 同 | —（同上） | 200 |

**後段の応答は本文ごと透過する**（`RelayAsync`。`TagDictionaryBffEndpoints` と同型）。
507 の本文には SC-19 の固定文言の根拠（使用量・上限・容量を空ける手段）が入っており、
**詰め替えると画面が理由を出せない**。409 の `vault_path_conflict` / `not_deleted` も同じである。

## 受け入れ基準（計画からの写像。本 PR で満たす分）

- [ ] SC-19 の一覧・容量表示・新規作成・論理削除・復元・完全削除（単票／一括）が `/bff/*` から呼べる
- [ ] SC-20 の端末一覧・発行・再発行・個別失効・一括失効・露出 3 トグルが `/bff/*` から呼べる
- [ ] **本人は自分の資料へ到達できる**（陽性対照）
- [ ] **他人の資料・他人の端末へは到達できない。応答は 403 ではなく 404**（存在秘匿）
- [ ] **無認証は全端点で 401**（`/bff/*` の不変条件）
- [ ] **write ポリシーが無い主体の書き込みは 403**。同じ主体の**読み取りは通る**（陽性対照）
- [ ] 書き込み経路が `/authz/scope` へ送る action が **`write`** である（観測点で固定）
- [ ] 後段の 507（容量上限）・409（パス重複・未削除）の**本文が詰め替えられずに届く**
- [ ] トークンの平文が発行・再発行の応答**以外**に現れない
- [ ] `docs/api/openapi.yaml` の `x-roles` と実効ロールが一致する（`check-bff-authz-docs.js`）

## テスト方針

- 置き場所は `src/platform/backend/Bff/Platform.Bff.Tests/`（既存の BFF テストと同居。実測した慣行）。
  **新規テストプロジェクトは作らない。** 同プロジェクトは xUnit1051 未移行のため
  `TestContext.Current.CancellationToken` は不要である（`src/Directory.Build.props` の許可リストで実測）。
- `BffTestFactory` の `DocumentStubHandler` へ**個人資料の経路を足す**。スタブは実体と同じく
  **① Authorization が無ければ 401**（転送の観測点）**② 主体は Bearer の値から採る**
  **③ 他人の資料・端末は 404** を再現する。
  🔴 スタブが常に成功を返す作りだと「**BFF が資格情報を渡し忘れた**」が緑を通る（#948 の再発）。
- 🔴 否定形（拒否・不可視）は必ず陽性対照（許可・可視）と対で置く。
- 変異試験で検出力を実測し、元へ戻して残渣 0 を確認する。

## 変異試験（実施して結果を本書へ記録する）

| # | 変異 | 期待 |
| --- | --- | --- |
| 1 | `Forwarding` の Authorization 転送を落とす（＝本人絞りを外す） | 後段が主体を決められず 401 → **赤** |
| 2 | 書き込みの `BffScopeAction.Write` を `Read` へ劣化させる | write 拒否の主体が書き込めてしまう → **赤** |

## 未決事項（残件として報告書へ）

1. SC-19 / SC-20 の画面（フロントエンド）と orval のフック生成
2. 管理者の上限変更（除外 A。載せる画面が計画に無い）
3. Obsidian プラグイン本体（`ADR-0037` 決定 1）
4. 露出トグル ON の消費側（`IADR-0253` 段 3）
