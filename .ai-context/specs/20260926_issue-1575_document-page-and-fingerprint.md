---
title: 作業仕様書 — 文書 API に属性の絞り込みとページングの口・本文の指紋を足し、外部 ID と機械クライアントの所有文書の更新・削除は計画へ環流する（#1575）
type: spec
status: done
related_ids:
  - FR-06
  - FR-21
  - UC-03
  - SC-05
  - NFR-08
  - ADR-0050
  - ADR-0036
  - ADR-0034
  - ADR-0054
  - ADR-0060
  - ADR-0091
  - ADR-0029
  - IADR-0012
  - IADR-0041
  - IADR-0045
  - IADR-0075
  - IADR-0122
  - IADR-0379
  - IADR-0402
author: claude
created: 2026-09-26
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md FR-06（文書の CRUD・バージョン管理・メタデータ管理）・NFR-08（文書 数万〜数十万件）
  - planning:projects/microservices-platform/06_technical/02_service-decomposition.md §文書管理サービス
  - planning:projects/microservices-platform/07_adr/ADR-0050_document-body-fingerprint.md 決定 1（本文の内容のみに依存する不透明な値）・フォローアップ 3（算出方法は実装側）
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-07（書き込みの動的束縛は個人資料の投入経路）・D-08・§未確定事項 6
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-05「管理系 3 画面の閲覧ロール」（作成・編集・公開・アーカイブ・削除は管理者限定。Q19）
  - planning:projects/microservices-platform/10_feedback/20260809_document-write-machine-client.md（status open。機械クライアントへの破壊的操作の統制の射程は裁定待ち）
issue: "#1575"
---

# 作業仕様書 — 文書 API の不足 4 点（#1575）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-06**（文書の CRUD・バージョン管理・メタデータ管理）。
  🔴 issue の表題は `FR-06,FR-08` だが、**MSP の FR-08 は回答フィードバックであり本件と無関係**である
  （AST の FR-08＝確定報告書の KB 保存と取り違えたもの）。本作業の起点 ID は FR-06 とする。
- ユースケース（UC）: UC-03
- 画面（SC）: なし（SC-05 の統制は項目 4 の判定の根拠として読む）
- 関連 ADR: ADR-0050（本文指紋）・ADR-0036（所有者ベースの裁量制御）・ADR-0034 決定 9 / ADR-0054（個人資料は機械の主体の対象外）・ADR-0091（文書の同一性は文書 ID）・ADR-0060（人の経路の `owner`）
- 計画書の版: planning `origin/main` `b3e0751`（2026-09-26 取得）

## 目的・背景

AST#1028（AST#1038・AST/IADR-0436）で AST が KB の入れ直しを実装した際、基盤の文書 API に次の 4 点の不足があり、
AST は迂回している（全件を 1 回引いて属性で突き合わせる）。

1. 外部 ID での引き当て・upsert が無い
2. `GET /documents` に属性の絞り込みとページングが無い
3. 返す文書に本文の指紋が無い
4. サービスアカウントが自分の所有する文書のメタデータ更新・削除をできない（管理者限定）

## 計画との照合（着手前の判定）

| # | 項目 | 計画の記述 | 判定 |
| --- | --- | --- | --- |
| 1 | 外部 ID | 計画は文書の同一性を**文書 ID ただ 1 つ**で扱う（ADR-0091 等）。`projects/microservices-platform/**` を `外部 ID` / `external_id` / `externalId` / `自然キー` / `冪等キー` / `upsert` で走査して**該当 0 件**（IADR-0010 の表行 1 件は FeedbackService の話） | **実装しない。新しい同一性の概念であり計画に無い。環流の下書きを作る** |
| 2 | 絞り込み・ページング | 計画は一覧の形に沈黙している。FR-06「CRUD」の射程内の読み取りの詳細化。NFR-08 の規模（数万〜数十万件）は全件一括を前提にしない | **実装する（絞るだけ。見える集合を広げない形に限る）** |
| 3 | 本文の指紋 | ADR-0050 決定 1 は「本文の内容のみに依存する不透明な値」を定め、算出方法を実装へ委ねた（フォローアップ 3）。台帳は既に `ContentFingerprint` を持つ | **実装する（既存値を応答へ載せるだけ）** |
| 4 | 機械の主体の所有文書の更新・削除 | SC-05 は「作成・編集・公開・アーカイブ・削除は管理者限定」（Q19）。ADR-0036 D-07 の所有者ベースの書き込みは**個人資料の投入経路**の規定。機械クライアントへ統制が及ぶかは環流 `20260809_document-write-machine-client`（**status open・裁定待ち**）が問うたまま | **実装しない。計画の裁定が要る。環流の下書きを作る** |

## 対象範囲

- 対象:
  - 項目 3: `DocumentDto` へ `ContentFingerprint`（`string?`・末尾追加・既定 null）を足す。gRPC `DocumentSummary` へも `optional string content_fingerprint = 11` を足し、REST と同じ形を保つ。
  - 項目 2: 新しい読み取り口 `GET /documents/page`（属性の完全一致の絞り込み・カーソルによるページング）。
  - OpenAPI（`DocumentDto`・`DocumentPageDto`・新しいパス）、契約スナップショット 2 種（`contract-schema-baseline.json`・`proto-contract-baseline.json`）、orval 生成物（`DocumentDto` が BFF 応答に載るため）。
  - 機能仕様書・テスト仕様書・データ仕様書・通信仕様書の追随、実装 ADR 1 件。
- 対象外:
  - 項目 1・4（上表のとおり計画の裁定待ち。下書きは PR 本文と報告に置き、起票はしない）。
  - 既存の `GET /documents`（BFF と gRPC `ListDocuments` が同値を前提にしている。**形も集合も変えない**）。
  - BFF の一覧（`/bff/documents`）への絞り込み・ページングの追加（画面が要求していない）。
  - `HasBody` の意味の変更（ADR-0070 決定 3 が「原本が本文を持っていたか」と定めた値。本文なしで作った文書で true になるのは既定値の仕様どおり）。

## 設計

### 項目 3 — 本文の指紋

- 値は台帳の `Document.ContentFingerprint` をそのまま写す（生成マッパが同名で写す）。本文を持たない文書・指紋化できなかった文書は null。
- **全経路が同じ関数で作る**（`DocumentBodyIntake.Fingerprint` ＝ 格納した本文の UTF-8 バイト列の SHA-256 小文字 hex。
  作成時の本文・`PUT /documents/{id}/body`・正規化の取り込み・Obsidian 同期のいずれも同じ）。
  したがって**本文を投入した呼び出し側は、送った本文から同じ値を計算して突き合わせられる**。この性質を実装 ADR に記録する（ADR-0050 フォローアップ 3）。
- 公開面での扱い: 個人資料の `contentHash`（SC-19）と同じく、**本文を読める主体に本文の指紋を見せても新しい情報は漏れない**。BFF は `DocumentDto` をそのまま中継する。
  AI 提案の「却下時点の指紋を公開面に出さない」（`AiSuggestion`）は提案の内部状態の話で、本件と別物である。

### 項目 2 — `GET /documents/page`

| 要素 | 仕様 |
| --- | --- |
| 認可 | **認証を要する**（`RequireAuthorization()`。既存の `GET /documents` は認証すら要らないが、新しい口は狭い側で開ける） |
| 対象集合 | **組織文書だけ**。`doc_scope=private-note` の文書は絞り込みの値に依らず返さない（ADR-0036 D-08・ADR-0034 決定 9・ADR-0054。BFF の `IsManageable` が管理一覧から個人資料を外しているのと同じ線） |
| 絞り込み | `attr.<キー>=<値>`（0 個以上・AND・値は完全一致〔大文字小文字を区別〕）。同じキーの重複・空のキー・空の値は 400 |
| 並び | **`CreatedAt` 昇順**、同時刻は `Id` 昇順（全順序。並びのキーは不変） |
| ページング | `limit`（既定 100・1〜500 に丸める。FeedbackService の一覧と同じ作法）・`cursor`（前ページの `nextCursor`。不透明な文字列。壊れていれば 400） |
| 応答 | `DocumentPageDto { items: DocumentDto[], nextCursor: string? }`。`nextCursor` が null なら終端 |

- **並びのキーは作成時刻（不変）で、カーソルはキーセット（最後の要素の `CreatedAt` と `Id`）である。**
  更新時刻で並べると、走査の途中で更新された文書が先頭へ移り、未読のまま飛ばされる
  （AST の入れ直しは「読んだ文書へ本文を入れる」ので、既読の側は動いても害が無いが、他者が未読の文書を更新すると飛ぶ）。
  オフセットだと、前のページの文書が消えたときに後続のページが 1 件ずつずれて読み飛ばす。
  **不変のキー × キーセットなら、走査の間ずっと在った文書はちょうど 1 回ずつ返る**（途中で作られた文書は末尾に現れる）。
  既存の `GET /documents`（更新の新しい順）とは並びが違うが、別の口なので揃える必要が無い。
- **絞り込みは台帳を読んだ後にメモリ上で行う。** 属性は jsonb へ値変換で写しており、LINQ から SQL へ訳せない。
  既存の `GET /documents` も全件をメモリへ読むので、DB の負荷は増えない（減らせるのは応答の量）。
  台帳の規模が問題になったら jsonb の包含演算子による SQL 側の絞り込みへ移す（実装 ADR の再検討条件）。
- 本体は `Features/Documents/ListPage/` に置く（ADR-0065 決定 2。1 操作だけが使う）。gRPC 面には足さない（呼び出し元が居ない）。

### 見える集合を広げないことの論証

- DocumentService の読み取りは**ABAC の判定を持たない**（実施点は BFF の `BffScopeResolver` ＋ `IsManageable` ただ 1 つ。IADR-0041 / IADR-0045、`IADR-0012`）。
  直接の呼び出し元（メッシュ内のサービスアカウント）に見えている集合は、今日の `GET /documents` の全件である。
- 新しい口の集合は **「`GET /documents` の全件」∩「組織文書」∩「全絞り込みに一致」** であり、構成上その部分集合にしかならない。
  絞り込みの項目を足すほど狭くなり、どの値を与えても広がらない（試験で固定する）。
- 判定器を DocumentService に 2 つ目として足すものではない（個人資料の除外は属性 1 つの集合帰属で、BFF と同じ線の構造的な除外）。

## 母集合の引き直し（IADR-0141 決定 1・traceability.repo.md 規則 9・10）

**軸 1 — `DocumentDto` の形に依存するもの**（`git grep -l "DocumentDto\b"`、`src/ai-stock-trading`・`.ai-context` を除く。60 件）:

| 区分 | 件 | 扱い |
| --- | --- | --- |
| 契約本体・写像（`DocumentDto.cs`・`DocumentMapper.cs`・`DocumentReadGrpcMapping.cs`・`document_read.proto`） | 4 | **更新** |
| 契約スナップショット（`scripts/contract-schema-baseline.json`）・proto スナップショット（`scripts/proto-contract-baseline.json`） | 2 | **`--update` で更新** |
| OpenAPI（`docs/api/openapi.yaml` の `DocumentDto`） | 1 | **更新** |
| orval 生成物（`src/platform/frontend/src/lib/api/generated/**`） | — | **再生成**（手で書かない） |
| 機能仕様書 `docs/functional/FR-06_document-crud-versioning.md`（出力・エンドポイント一覧） | 1 | **更新** |
| DocumentService / BFF / Contracts の試験・本番コード（DTO を読むだけ） | 40 余 | **除外**: 項目の追加は既定値付きの末尾追加で、既存の読み手の意味を変えない |
| フロントエンドの画面（`sc03` / `sc05` / `sc18` / `private-notes`） | 9 | **除外**: 指紋を表示する要求が無い（画面の計画に無い） |
| `docs/screens/SC-03`・`SC-05`・`docs/tests/SC-03`・`docs/data/conversion-job.md` | 4 | **除外**: 画面・変換ジョブの記述で、指紋に触れない |
| `.github/workflows/integration-stack.yml`・`scripts/verify-oidc-edge-flow.sh` | 2 | **除外**: 名前を出すだけの疎通手順（形に依存しない） |
| `src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/DocumentAttributeEncoding.cs`・`AttributeFilterMatchTests.cs` | 2 | **除外**: 属性の符号化だけを扱う |

**軸 2 — 誤りの側（「一覧は全件・絞り込みなし」「指紋は返さない」と書いている記述）**:
`git grep -n -i "絞り込み" -- docs/functional/FR-06* docs/data/document-and-version.md docs/api docs/screens/SC-05*` と
`git grep -n -i "contentFingerprint\|本文指紋\|指紋" -- docs`:

- `document_read.proto` の「一覧に絞り込みは無い（REST も無い）」 —— **`GET /documents` についてはなお真**（新しい口は別パスで gRPC の同値の相手ではない）。文言は変えず、新しい口が別物であることを 1 行足す。
- `docs/api/openapi.yaml` の `AiSuggestion` と却下の口の「本文指紋は公開面に出さない」 —— **提案の内部状態（却下時点の指紋）の話で、文書の現在の指紋ではない**。変えない（設計 §項目 3 に理由）。
- `docs/api/openapi.yaml` の `PrivateNoteDto.contentHash` —— 同じ値を既に公開面へ出している先例。変えない。
- `docs/data/knowledge-graph.md`・`docs/functional/FR-18`・`docs/tests/FR-10`・`FR-17`・`FR-18`・`docs/observability/knowledge-health-indicators.md` —— 購読側の指紋の使い方で、`DocumentDto` と無関係。**除外**。

**軸 3 — `GET /documents` の意味に依存するもの**（BFF `FetchListAsync`・gRPC `ListDocuments`・`docs/api/east-west-grpc.md`）:
**既存の口を変えないので追随不要**。新しい口は別パス。

**規則 10（この変更で新たに誤りになる自分の記述）**: `DocumentDto` の説明を持つ `docs/functional/FR-06` の「出力」行と、
`docs/data/document-and-version.md` の `ContentFingerprint` の説明（持っていれば）を引き直す —— 実装後に再走査する。

**［2026-09-26 追記 / #1575］実装後の再走査の結果**:

- `docs/data/document-and-version.md` は台帳の `ContentFingerprint` 列を**持っていなかった**（#911 で列が増えたときの追随漏れ）。
  応答へ載せる本件で読み手が増えるため、列の行と ER 図の 1 行を足した。
- 軸 4（認可の記述）: `git grep -n -i "kb-writer\|KB 書き込み\|KB の書き込み" -- docs` で 7 行。
  `docs/security/security.md` の DocumentService の認可の段落に新しい口の認可（認証必須・個人資料を返さない）と、
  機械クライアントが所有文書を更新・削除できないことを足した。`docs/screens/SC-05` の 2 行（`POST` の据え置き）は
  本件で変わらないので除外。`docs/migration/cutover-discard-and-rebuild.md`・`docs/operations/local-sso-recovery-runbook.md`・
  `docs/security/security.md` の資格情報の行（234・251〜）はクライアント名の列挙で、口の認可に触れないので除外。
- `docs/api/east-west-grpc.md` の「presence で運ぶ」段落に本文指紋を足し、絞り込みの口を gRPC 面に足していないことを書いた。
- `GET /documents` の表記（`git grep -n "GET /documents" -- docs ':!docs/api/openapi.yaml'`）は FR-06 の機能・試験仕様書だけで、
  いずれも既存の一覧の意味を変えていない。

## 受け入れ基準

- [x] `GET /documents/{id}`・`GET /documents`・作成・本文投入の応答に `contentFingerprint` が載り、本文を投入した文書では**送った本文の UTF-8 の SHA-256 小文字 hex に一致**する。本文の無い文書では null。
- [x] 本文を差し替えると `contentFingerprint` が変わり、メタデータだけの更新では変わらない（ADR-0050 決定 1 の性質）。
- [x] gRPC の `GetDocument` / `ListDocuments` でも同じ値が往復し、null は null のまま戻る。
- [x] `GET /documents/page?attr.project=X` は `project=X` の組織文書だけを返し、**結果は常に `GET /documents` の部分集合**である。
- [x] 絞り込みを足すと結果は広がらない（2 条件の結果 ⊆ 1 条件の結果）。
- [x] **個人資料は、`attr.doc_scope=private-note` を与えても、所有者本人が呼んでも返らない。**
- [x] `limit` とカーソルで全ページを辿ると、絞り込み結果と同じ集合を重複なく得る。走査の途中で文書が更新・削除・追加されても、走査の間ずっと在った文書を読み飛ばさない。
- [x] 同じキーの重複・空のキー・空の値・壊れたカーソルは 400。
- [x] 新しい口は認証を要求する（端点の認可メタデータで確認）。
- [x] 既存の `GET /documents` の応答集合・並びは変わらない。

## テスト方針

- DocumentService のエンドポイント試験（InMemory・`TestWebApplicationFactory`）で上の基準を 1 つずつ固定する。
  ABAC の観点（部分集合・個人資料の除外・絞り込みの単調性）は**陽性対照と対で**置く（除外が効いていることを、除外しない側の件数で確かめる）。
- 契約: `DocumentReadGrpcMappingTests` に指紋の往復（値あり・null）を足す。
- **［2026-09-27 追記 / #1603 の監査］** 監査（GO）の指摘で次を足した。①原本が本文を持たない文書（`HasBody=false`）は台帳に空本文の指紋を持つため、
  応答では null に倒す（`DocumentEndpoints.ToDto`）。②作成時刻が同じ tick の文書で `Id` の同時刻の枝を試験する（同時刻の枝を落とす変異が生き残っていた）。
  ③`doc_scope` の値の大小の揺れ（`Private-Note`）でも個人資料を返さない試験。④`DocumentMapper` の「指紋は応答に出さない」注記の是正。
  ⑤機械の呼び出し元が ABAC を経ずに機密区分の高い組織文書を読めること（既存の一覧と同じ露出）と、ロールの門は planning#680 の裁定待ちであることを IADR へ記録した。
- 所有者だけの削除の試験（issue の依頼）は、項目 4 を実装しないため**既存の管理者限定の試験（`Write_OperatorRole_Returns403` 等）が現状を固定している**ことを確認するに留める。

## 計画書との差異

- 差異: あり（**項目 1・4 は計画の裁定待ちで実装しない**）。環流の下書きを PR 本文と報告に置く（起票はコーディネータ／利用者が行う）。
- issue 表題の `FR-08` は MSP の FR-08 ではない（上記）。

## 未決事項

- 項目 1・4 の裁定（計画側）。
- 項目 2 の SQL 側の絞り込みへの移行時期（規模次第。実装 ADR の再検討条件）。
