---
title: 作業仕様書 — #449（FR-06 / FR-13 文書管理・Wiki 閲覧）の残作業を実測で確定し、実環境不要分を実装する
type: spec
status: draft
related_ids:
  - FR-06
  - FR-09
  - FR-13
  - UC-03
  - UC-07
  - SC-03
  - SC-04
  - SC-05
  - ADR-0011
  - ADR-0014
  - ADR-0015
  - ADR-0046
  - ADR-0057
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "02_requirements 01_requirements.md FR-06（［2026-08-23 明確化］版管理の射程は作成・一覧・取得。版の復元は含めない。環流 planning#473）"
  - "02_requirements 01_requirements.md FR-13（正規化文書を Wiki サービスで閲覧。ABAC 適用）"
  - "05_screens 01_screens.md SC-03（バージョン履歴パネル＝版の一覧・取得まで。バックリンク欄は置かない）"
  - "05_screens 01_screens.md SC-04（ページツリー・本文・最終同期日時・SC-03 復帰リンク。ルートは Wiki.js 別ホスト）"
  - "05_screens 01_screens.md SC-05（タグは既定タグ辞書に整合。辞書は管理系ロールが引ける照会口から取得する。裁定 2026-08-05 質問票 第12回 Q18）"
related_adrs:
  - IADR-0009
  - IADR-0020
  - IADR-0021
  - IADR-0135
  - IADR-0152
issue: "#449"
---

# 作業仕様書: #449 残作業の確定と実装

## 1. 起点と本作業の性格

#449 は「文書管理（FR-06）・Wiki 閲覧（FR-13）の再実装」という大玉の器である。
**射程の相当部分は既に別 issue の作業として着地しており**（DocumentService の CRUD・版管理・
メタデータ・タグ識別子化、WikiService の ABAC ゲートウェイ・同期・存在秘匿・削除伝播、
SC-03 / SC-05 の画面）、本作業の第一歩は**受け入れ基準に対する現状のギャップ分析**である。

**#449 の 2026-08-23 コメントが残作業を W1〜W9 として列挙している。本作業はそれを鵜呑みにせず、
コードと計画書の実測で引き直した。** 引き直しの結果、**同コメントが「着手前に裁定が要る」とした
2 件のうち 1 件は、その後の計画側の裁定で決着している**（下記 §3 W1）。

`git log` / `git blame` は出典に使っていない —— `git rev-parse --is-shallow-repository` = `true`
（打ち切り位置を「最後に触ったコミット」と誤読する事故を避ける）。

## 2. ギャップ表（基準 × 実装状況 × 残作業）

判定は「着地済み / 本作業で実装 / 射程外」の 3 択。**射程外は理由を必ず書く。**

| # | 計画の基準 | 実装の現状（実測） | 判定 |
| --- | --- | --- | --- |
| 1 | FR-06 CRUD | `DocumentEndpoints` 8 端点 ＋ `DocumentBffEndpoints` 9 端点 ＋ SC-05 UI | **着地済み** |
| 2 | FR-06 版管理（**作成**） | `DocumentVersion.Capture`（append-only スナップショット）＋ 移行 `AddDocumentVersions` | **着地済み** |
| 3 | FR-06 版管理（**一覧**） | サービス `GET /documents/{id}/versions` ＋ BFF `GET /bff/documents/{id}/versions` ＋ SC-03 の版履歴表 | **着地済み** |
| 4 | FR-06 版管理（**取得**） | サービス `GET /documents/{id}/versions/{version}` は**在る**。🔴 **BFF が露出していない**ため利用者経路から到達できない | **本作業で実装（G5・BFF まで）** |
| 5 | FR-06 版管理（**復元**） | 実装に無い | **射程外**（計画が明文で除外。§3 W1） |
| 6 | FR-06 メタデータ・属性（サーバ側） | 機密区分・`doc_scope` の値域検証、`doc_scope` 不変（ADR-0058） | **着地済み** |
| 7 | SC-05 タグ「既定タグ辞書に整合」 | サーバ側は識別子化＋辞書照合で未知タグ 400。BFF `GET /bff/tags`（管理者・運用者）も在る。🔴 **SC-05 の入力が自由テキスト**（`DocumentForm.tsx` の `tagDraft`）で辞書を引いていない | **本作業で実装（G1）** |
| 8 | FR-13 ABAC ゲートウェイ | `WikiEndpoints`（一覧・slug・by-doc）＋ `AbacPageFilter`（deny-by-default・分岐選言）＋ 404 存在秘匿 | **着地済み** |
| 9 | FR-13 `private-note` を Wiki.js へ同期しない | `DocumentSyncConsumer.IsPrivateNote`（集合帰属で判定）＋ 否定形・陽性対照・再配信冪等のテスト | **着地済み**（#986 の成果） |
| 10 | FR-13 **読み取り経路**の `private-note` 否定形 | 🔴 **不在**。既存の否定形は同期側と `owner` 属性軸だけで、**`doc_scope=private-note` を軸にした読み取り経路（`/wiki/pages`・`/wiki/pages/{slug}`・`by-doc`）の否定形が無い** | **本作業で実装（G3）** |
| 11 | SC-03 にバックリンク欄を併置しない | 併置していないことは全文走査で確認。🔴 **固定しているテストが射程外れ** —— 既存の「無いこと」テストは AI 提案欄・知識グラフ導線だけを見て**バックリンク欄を見ていない** | **本作業で実装（G2）** |
| 12 | SC-04 の表示文言 | 🔴 `WikiAccessPage.tsx` が **Lingui を通さない生の日本語文字列**（`CLAUDE.md` §i18n 違反） | **本作業で実装（G4）** |
| 13 | SC-03 の SC-18 導線・AI 提案承認欄 | 未実装。コードと文書の両方で #452 へ明示委譲済み | **射程外**（#452 へ委譲済み） |
| 14 | SC-04 の閲覧 UI（ページツリー・本文・最終同期日時） | SPA 側は外部リンク 1 本（29 行）。ゲートウェイに BFF 露出なし | **射程外**（§3 W4。要 IADR） |
| 15 | SC-04 バックリンク欄・ローカルグラフ | 未実装 | **射程外**（計画が「実現方式は未確定」と明記。ADR-0011 見直しあり得る） |
| 16 | MinIO 保管（ADR-0014/0015） | 配線は実装済み（未設定時 `NullObjectStorageClient` へ縮退）。`ObjectStorageRoundTripTests` は Docker 不在で **Skipped** | **射程外**（実環境が要る。§3 W8） |
| 17 | 削除の伝播（FR-06 の受け入れ基準・ADR-0057） | 別 issue（#1016 / #911）の射程で進行中 | **射程外**（他 issue が持つ） |

### 本作業で実装する項目（G1〜G5）

| ID | 作業 | 対応する W |
| --- | --- | --- |
| **G1** | SC-05 のタグ入力を辞書照会口（`/bff/tags`）へ接続し、自由入力を廃す | W3 |
| **G2** | SC-03 が**バックリンク欄を持たない**ことを固定する回帰テスト | W6 |
| **G3** | Wiki **読み取り経路**の `doc_scope=private-note` 否定形テスト | W7 |
| **G4** | SC-04 の表示文言を Lingui へ載せる（ja / en 両方） | W9 |
| **G5** | BFF `GET /bff/documents/{id}/versions/{version}`（版の**取得**の露出）＋ 契約 | W2 の後段側 |

## 3. 射程外とその理由（W1〜W9 の引き直し）

| W | 2026-08-23 コメントの記述 | 本作業の判定 |
| --- | --- | --- |
| **W1** 版の復元 | 「計画に明文が無い。要否を計画側へ問うのが筋」 | 🔴 **裁定は出ている。実装しない。** 計画 FR-06 に **［2026-08-23 明確化］「バージョン管理」の射程は「版の作成・一覧・取得」までとし、版の復元（過去版へ戻す操作）は含めない**（利用者裁定 2026-08-23・環流 planning#473）が入り、SC-03 のバージョン履歴パネルにも同旨の括弧書きが付いた。**「裁定待ち」ではなく「否で決着」である。** 併せて計画は「復元が必要になったときは新しい要求として起こす」と定めた |
| **W2** 特定版の閲覧導線 | BFF `/versions/{version}` ＋ SC-03 の版行から遷移 | **後段（BFF・契約）は本作業で実装（G5）。前段（SC-03 の版ドリルダウン UI）は本作業では行わない** —— SC-03 は `/bff/documents` 群を **orval 生成フックで呼ぶ**規約（IADR-0135 決定 1）であり、生成フックは `pnpm run codegen` が要る。**本セッションは codegen を行わない**（波末に統括が 1 回実施する運用）。生成物の無い状態で `apiFetch` の手書きを差すと規約に反し、codegen 後に書き直す churn になる。**codegen 後の小作業として残す** |
| **W3** SC-05 タグ入力を辞書へ | — | **実装する（G1）** |
| **W4** SC-04 の閲覧 UI と Wiki 読み取りの BFF 露出 | 規模 L・一部裁定待ち | 🔴 **射程外（要 IADR）。** **計画と実装 ADR が「SC-04 の閲覧 UI の実体は誰か」を SPA 側だと言っていない。** 計画 SC-04 の §ルートは「**Wiki.js 別ホスト（例: `wiki.example.co.jp/...`。基盤 SPA とは別配信）**」であり、**IADR-0020（Accepted）の決定は「閲覧・編集 UI の実体は Wiki.js が担う」**、WikiService は前段の**認可ゲートウェイへ縮退**する、である。**SPA 内にページツリーと本文描画を作ると Wiki.js の UI を二重に持つことになり、上の 2 つと正面から衝突する。** 一方で `CLAUDE.md` の BFF 境界は「フロントは必ず `/bff/*` 経由」と定めており、**「SC-04 は Wiki.js が描くのか SPA が描くのか」は未解決の設計判断である。** これは新しい IADR（必要なら計画への環流）で決めるべき事柄であって、実装セッションが黙って一方に倒してよいものではない。**倒した側が誤りだったときの手戻りが L 規模である**ことも、先に決めるべき理由になる |
| **W5** SC-03 ↔ SC-04 のディープリンク | 現状は `/wiki` トップへ飛ぶだけ | **射程外。** 計画 SC-03 §アクションは「**Wiki リンクで SC-04 へ**」までで、**当該文書のページへ深リンクせよとは書いていない**。現行実装は `/wiki`（= SC-04 のルート）へ遷移しており**計画の記述は満たしている**。深リンクは W4 の設計判断（どこが SC-04 を描くか）が決まってからでないと行き先が定まらない |
| **W6** バックリンク欄の不在テスト | — | **実装する（G2）** |
| **W7** `doc_scope=private-note` 軸の否定形 | — | **実装する（G3）** |
| **W8** MinIO ラウンドトリップの実走 | Docker のある環境で | **射程外（実環境）。** 本セッションに Docker は無く、`ObjectStorageRoundTripTests` は Skipped のままである。**「通った」とは書かない** |
| **W9** SC-04 の i18n | — | **実装する（G4）** |

## 4. 設計

### G1: SC-05 のタグ入力を辞書照会口へ接続

計画 SC-05 の入力表は タグ =「**既定タグ辞書に整合**（**辞書は管理系ロールが引ける照会口から取得する**）」
であり、2026-08-05 の裁定（質問票 第12回 Q18）が「**自由入力を許すと辞書に無いタグが増え、
SC-09 の規則〔参照があるタグは削除拒否・改名は既存文書へ追随〕が成り立たなくなる**」と理由を明記している。
**現行の SC-05 は自由テキスト入力**であり、この確定に反している。

- 入力を `Input`＋`追加` から **`Select`（辞書の値集合）＋`追加`** へ替える。
- 辞書は **`GET /bff/tags`**（読み取りは管理者・運用者。IADR-0152 決定 5）から取る。
  **口も生成フック（`useBffTagList`）も既に在る**ので、契約変更も codegen も要らない。
- feature 境界を跨がない —— SC-09 の `useTagDictionary` を import せず、
  **`sc05-documents/api/useTagOptions.ts` を新設**して同じ生成フックを直接呼ぶ
  （`features/*` の内部へ他 feature から入らない。Bulletproof React の公開面規約）。
- **既に付いているタグのうち辞書に無いもの（過去データ）は表示と削除を保つ** ——
  選べなくするのは**追加**だけである（既存文書のタグを画面が黙って落とすと破壊的になる）。
- 取得中・失敗時は選択肢が無いので **`追加` を無効化**する。
  **自由入力へフォールバックしない** —— それでは裁定の意味が消える。

### G2: SC-03 にバックリンク欄が無いことの固定

計画 SC-03 は「**本画面はバックリンク欄を持たない**」「バックリンク欄・ローカルグラフは
**Wiki.js 側（SC-04）のみ**に置く。**SC-03 には併置しない**」（確定・2026-08-02。issue planning#70）。
既存の「無いこと」テストは AI 提案欄・知識グラフ導線だけを見ており、**バックリンク欄を見ていない**。

- 既存テスト（`does not render the AI suggestion panel or the knowledge-graph link`）と同じ
  「**導線の並びを全部描かせた状態**」（`wikiBaseUrl` 設定済み）で、
  バックリンク欄・被参照欄・ローカルグラフの語が現れないことを固定する。
- **起点 ID をテスト直前のコメントに書かない** —— `check-test-traceability.js` は
  直前コメントの ID を写像として拾う仕様で、**未着手機能の ID を書くと「実装が先行している」と
  誤報される**（既存テストが同じ理由で ID を書いていない。その作法に合わせる）。

### G3: Wiki 読み取り経路の `private-note` 否定形

ADR-0046 により**個人資料はそもそも Wiki.js へ同期されない**ので、`WikiPage` に
`doc_scope=private-note` の行は本来生じない。**それでも読み取り経路の否定形を置く**のは、
#449 の退行防止節が「**個人資料が Wiki 経路で漏れないこと**」を名指しで要求しているためであり、
**同期側の除外が破れたときに読み取り側が二重で止める**（多層防御・IADR-0044 と同じ形）ことを固定する。

- 既存の `WikiEndpointsAbacTests` へ追加する（**分ける必要が無い** —— 同じ経路・同じ fixture）。
- 軸は **`doc_scope`**。既存の否定形は `confidentiality` 軸であり、**別の属性軸である**。
- 一覧（`/wiki/pages`）・個別（`/wiki/pages/by-doc/{id}`）の両方を見る。
- 🔴 **陽性対照を必ず添える** —— 「`doc_scope` を一切見ない実装」でも否定形だけなら通るため、
  **同じスコープで組織文書は 200 で見える**ことを同時に固定する。
  これは同期側テスト（`DocumentSyncConsumerTests`）が既に採っている作法と同じである。

### G4: SC-04 の i18n

`WikiAccessPage.tsx` の表示文言（見出し・説明・リンク文言・未設定時の注記）を
`Trans` / `useLingui` へ載せ、**ja / en の両カタログを埋める**（en 訳まで書く。
`check-i18n-catalogs.js` が未翻訳キーを止める）。**画面の構造は変えない** ——
W4（SC-04 を誰が描くか）が未決なので、**文言の載せ替えだけに留める。**

### G5: BFF への「版の取得」の露出

- `DocumentBffEndpoints` に **`GET /bff/documents/{id}/versions/{version}`** を足す。
- **既存の `/versions`（一覧）と同じ形にする** —— 先に `FetchAuthorizedAsync(id, Read)` で
  ABAC を判定し、**スコープ外・不在はいずれも 404**（存在秘匿・IADR-0009）。
  判定を通ってから後段 `GET /documents/{id}/versions/{version}` を引き、
  **後段の 404（その版が無い）も 404 として透過**する。
- 契約（`docs/api/openapi.yaml`）へ `/bff/documents/{id}/versions/{version}` を追加する。
  **応答は既存の `DocumentVersionDto`**（新規スキーマを作らない）。
- 🔴 **`DocumentVersionDto` は本文を持たない。** 版行が持つのは
  タイトル・状態・`markdownUri`・属性・タグ・変更メモ・作成日時の**メタデータのスナップショット**であり、
  **本文の実体は版ごとに保持されていない**（オブジェクトキーが文書 ID から固定で決まり、
  再投入は同じキーを上書きする。計画も 2026-08-23 の変更履歴で同じ実測を書いている）。
  **「過去版の本文が読める」と読める書き方を契約に置かない。**

## 5. 受け入れ基準

- [x] G1: SC-05 のタグは**辞書の値からのみ**追加でき、辞書に無い値を新たに付けられない
- [x] G1: 既に付いている辞書外のタグは**表示され、削除できる**（過去データを黙って落とさない）
- [x] G1: 辞書が取得できないとき、**自由入力へフォールバックしない**（追加が無効になる）
- [x] G2: SC-03 にバックリンク欄・被参照欄・ローカルグラフが**現れない**ことがテストで固定される
- [x] G3: `doc_scope=private-note` のページが Wiki 読み取り経路（一覧・個別）に**現れない**
- [x] G3: 同じスコープで**組織文書は見える**（陽性対照。判定軸が効いていることの証明）
- [x] G4: SC-04 の表示文言が ja / en 両方で出る（生の日本語文字列が残らない）
- [x] G5: `GET /bff/documents/{id}/versions/{version}` が、権限内なら 200・**スコープ外/不在/版不在はいずれも 404**
- [x] 契約（openapi.yaml）が G5 の端点を持つ。`check-contract-schema.js --update` は**差分 0 件**
      （新しい DTO を作らず既存 `DocumentVersionDto` を使ったため、baseline は動かない）

## 5.1 変異試験の実測（5 種。うち 1 種が生存 → 死んだコードを撤去）

| # | 変異 | 結果 |
| --- | --- | --- |
| M1 | `AbacPageFilter.Matches` が属性フィルタを見ない（`doc_scope` を無視する実装） | **殺した** —— G3 の否定形 3 本が落ち、**陽性対照 2 本は通ったまま**（想定どおりの落ち方） |
| M2 | `WikiEndpoints` の個別取得だけ ABAC 判定を落とす（一覧は残す） | **殺した** —— 個別の否定形 2 本だけが落ちた（一覧側は無傷＝切り分けが効いている） |
| M3 | BFF の版取得で ABAC 判定を落として後段を直接引く | **殺した** —— `GetVersion_WhenScopeNotGranted_Returns404` が落ちた |
| M4 | SC-03 へバックリンク欄を併置する | **殺した** —— G2 の新テストだけが落ち、**既存の「無いこと」テストは通った**（＝新テストが本当に新しい軸を見ている証拠） |
| M5 | `addTag` の辞書照合 `tagOptions.includes(tagDraft)` を外す | 🔴 **生存した（24 件すべて素通り）** |
| M5' | タグ入力を `Select` から自由テキスト `Input` へ戻す（裁定前の挙動） | **殺した** —— G1 の 4 本が落ちた |

🔴 **M5 の生存は「テストが弱い」ではなく「そのコードが死んでいた」。**
`tagDraft` へ入るのは `selectable`（⊆ `tagOptions`）か空文字だけで、空文字は `canAddTag` が弾く。
つまり `tagOptions.includes(tagDraft)` は**UI から到達し得ない分岐**であり、
`CLAUDE.md` の禁止事項「**起こり得ないケースへの防御的実装**」に当たる。
**テストを足して通すのではなく、当該分岐を撤去した**（M5' が示すとおり、
「辞書の値だけ」の保証は**選択欄という形**が担っており、実行時の再確認より強い）。

## 6. テスト方針

| 対象 | 種別 | 置き場所 |
| --- | --- | --- |
| G1 | Vitest（jsdom + Testing Library） | `sc05-documents/components/DocumentManagementPage.test.tsx` |
| G2 | Vitest | `sc03-document/components/DocumentDetailPage.test.tsx` |
| G3 | xUnit（`TestContext.Current.CancellationToken`） | `WikiService.Api.Tests/WikiEndpointsAbacTests.cs` |
| G4 | Vitest（en ロケール） | `sc04-wiki/components/WikiAccessPage.test.tsx` |
| G5 | xUnit | `Platform.Bff.Tests/BffDocumentEndpointTests.cs`（BFF の試験はこのプロジェクトが持つ） |

**変異試験を最低 2 種、実測で行う**（宣言だけの「テストがある」は不合格）。

## 7. 母集合の取り方（規則 9・10）

- **G1 の追随先**: 「タグの自由入力」を持つ画面を、`tagDraft` / 自由入力の語ではなく
  **「タグを追加する UI」の文字列**で全走査して引いた。SC-05（`DocumentForm.tsx`）のみ。
  SC-09（`AdminAbacSettingsPage`）は**辞書そのものを編集する画面**であり、
  辞書へ新しい値を足すのが仕事なので**対象ではない**（自由入力が正しい）。
- **G4 の追随先**: 生の日本語文字列を持つ knowledge feature を走査した。SC-04 のみ。
- **G5 で新たに誤りになる自分の記述**（規則 10）: openapi の `/bff/documents/{id}/versions`
  の説明文は「版履歴一覧」であり、**取得の追加で誤りにならない**。
  `docs/` 配下に「BFF は版の取得を露出していない」と書いた記述は無い（走査で 0 件）。

## 7.1 検証の実測

| 検証 | 結果 |
| --- | --- |
| `dotnet build knowledge/backend/backend.slnx` | **0 Warning / 0 Error** |
| `dotnet test knowledge/backend/backend.slnx` | **Failed 0 / Passed 1,008 / Skipped 42**（Skipped は Docker 要のもの——MinIO ラウンドトリップ含む） |
| `dotnet test Platform.Bff.Tests`（BFF の試験はここが持つ） | **Failed 0 / Passed 367 / Skipped 1** |
| `dotnet format --verify-no-changes`（knowledge / platform 両方） | 差分なし |
| `pnpm run typecheck` | 通過 |
| `pnpm run lint` | **0 error**（warning 9 件はいずれも本作業が触っていないファイルの既存 `react-refresh` 警告） |
| `pnpm run format:check` | 通過 |
| `pnpm vitest run`（全ユニット） | **1,087 / 1,087**（2 回連続で緑） |
| `node scripts/check-i18n-catalogs.js` | OK（ja / en とも未翻訳・fuzzy・obsolete 0 件） |
| `node scripts/check-trace-blocks.js` | OK（150 件） |
| `node scripts/check-doc-links.js` | 🔴 **破損 1 件。ただし本作業の対象外・既存である** ——
`IADR-0281` 本文にある **Obsidian のリンク記法を説明するための literal な例**（`docs/` 配下の架空の
ノートを指す Markdown リンク）であって、実在するファイルを指していない。同 IADR は本作業で触っていない
（`git diff 3c82642..HEAD` に現れない）。**ここで当の記法をそのまま書き写すと検査が 2 件に増える**ので、
書式は再現せず説明にとどめる |

**フロント全体テストの 1 回目で `ai-stock-trading` の 1 件が落ちたが、フレークである。**
当該テスト（`RiskSettingsPage.monitorParameters.test.tsx`）は **AST submodule のもので本作業は 1 行も触っていない**。
単体で走らせると変更の有無にかかわらず 18 件とも通り、全体走行も 2 回目・3 回目は 1,087 件すべて緑だった。
**「自分の変更が原因ではない」と断ずる前に、変更なしの状態と単体走行の両方で確かめた。**

## 8. 計画書との差異

**無い。** 本作業は計画の確定記述（FR-06 の 2026-08-23 明確化、SC-05 の 2026-08-05 裁定、
SC-03 の 2026-08-02 確定、#449 の退行防止節）へ実装を寄せるものであり、
**計画に無い機能を足していない。** 射程外とした 5 件（W1・W4・W5・W8・W2 前段）は
いずれも「計画が明文で除外した」「設計判断が未決」「実環境が要る」「codegen 待ち」のいずれかで、
**足りないから諦めたものではない。**
