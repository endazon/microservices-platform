---
title: 作業仕様書 — SC-21 AI 提案一覧（#918）
type: spec
status: done
related_ids:
  - FR-18
  - FR-17
  - FR-05
  - UC-10
  - SC-21
  - SC-03
  - SC-09
  - ADR-0033
  - ADR-0034
  - ADR-0043
  - IADR-0121
  - IADR-0122
  - IADR-0124
  - IADR-0125
  - IADR-0131
  - IADR-0134
  - IADR-0135
  - IADR-0242
  - IADR-0266
  - IADR-0271
  - IADR-0272
  - IADR-0276
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "05_screens/01_screens.md §SC-21（主要素 6 件・入力/バリデーション表・描いてはいけないもの・アクセス制御・ルート）"
  - "05_screens/01_screens.md §SC-03（AI 提案の承認欄。**主導線**。本作業の射程外＝#452）"
  - "05_screens/01_screens.md §知識グラフ表示の共通規則（SC-03 / SC-04 / SC-18 / SC-19 / SC-21）"
  - "02_requirements/01_requirements.md FR-18（承認 UI は SC-03 主・SC-21 従／一括承認を提供しない／同一一覧／却下の再提示）"
  - "03_usecases/01_usecases.md UC-10 代替フロー（承認の 2 経路。確定するのは SC-03 のみ）"
  - "07_adr/ADR-0033 決定 7・10（3 状態・却下の永久保持・本文変更でのみ解除）"
  - "07_adr/ADR-0034 決定 2（権限外は 404 に倒す・存在秘匿）"
  - "05_screens/mockups/hi-fi/sc-21.html / wireframe/sc-21.html（2026-08-02 受領）"
issue: "#918"
---

# 作業仕様書: SC-21 AI 提案一覧（#918）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-18（AI によるリンク・タグ提案と人間承認）／FR-17・FR-05（グラフと ABAC）
- ユースケース（UC）: UC-10 代替フロー（承認の 2 経路。**確定するのは SC-03 のみ**）
- 画面（SC）: SC-21（AI 提案一覧・**従**）／SC-03（承認の**主**。本作業の射程外）
- 関連 ADR: ADR-0033 決定 7・10／ADR-0034 決定 2／ADR-0043（BFF 境界）
- 関連 IADR: 本作業の IADR-0276／IADR-0272（グラフの書き込み認可）／IADR-0271（第 4 段の土台＝TanStack Table）／
  IADR-0124・IADR-0125・IADR-0131・IADR-0134・IADR-0135（SPA の骨格・生成契約・遅延チャンク）
- 計画書の参照手段: 隣接クローン `../project-planning`（`git fetch origin` 済み。`origin/main` = `b6c3cc0`・2026-08-22。読み取り専用）

## 着手前の実測 —— issue 本文の記述との差（2026-08-23）

issue #918 の本文は「前提 #914 が **OPEN・未着手**」と書き、コメント 2 は「フロント第 4 段（TanStack Table）待ち」と書く。
**どちらも古い。** 作業ツリー `claude/implementation-repo-all-issues-hilvbs` で自分で測った結果は次のとおり。

| 確かめたこと | 実測 | issue の記述との差 |
| --- | --- | --- |
| GraphService に提案の永続と 3 状態遷移があるか | **在る。** `AiSuggestion.cs`（`SuggestionKind` link/tag・`SuggestionState` pending/approved/rejected・`TryApprove` / `TryReject` / `TryReinstate`・`RejectedCount`・`ReinstatedReason`）、`AiSuggestionEndpoints.cs`（`GET /graph/suggestions/`〔`state` / `kind`〕・`POST {id}/approve`・`POST {id}/reject`・`POST /generate/{documentId}`）、EF マイグレーション `20260822074729_AddAiSuggestions` | 本文の「#914 は未着手」は**誤り**（コメント 3 が着地を記録している） |
| 提案の BFF 公開口があるか | **無い。** `GraphBffEndpoints.cs` は `/bff/graph/{id}`・`/bff/graph/{id}/neighbors`・`/bff/graph/edge-types` の 3 本のみ | 本文どおり（**本作業で足す**） |
| `IADR-0272`（write アクションでの認可）が載っているか | **載っている**（`status: Proposed`。原文で確認）。`approve` / `reject` は `IsSourceWritableAsync` で write スコープを見る。`generate` は read のまま（同 決定 6 で裁定待ちとして範囲外） | issue に記載なし（統括の指示どおり**前提を壊さない**＝本作業は BFF に書き込み口を足さない） |
| `@tanstack/react-table`（第 4 段の表の土台） | **在る。** `knowledge/frontend/package.json` に `^9.1.2`、`knowledge/frontend/src/components/DataTable.tsx`（v9・並べ替えのみ登録・`aria-sort` ＋ 矢印）。`IADR-0271` は `status: Accepted` | コメント 2・3 の「第 4 段（Table）待ち」は**解消済み** |
| `ADR-0039` の状態 | **`Accepted`**（原文 `status: Accepted` / `updated: 2026-08-22`）。かつ射程は SC-18 の描画方式のみで SC-21 に掛からない | 本文の訂正どおり |
| `ADR-0033` / `ADR-0034` の状態 | **どちらも `Accepted`**（原文で確認） | — |
| SC-03 に AI 提案の承認欄があるか | **無い。** `DocumentDetailPage.tsx` は承認欄を持たず、`DocumentDetailPage.test.tsx` の `does not render the AI suggestion panel or the knowledge-graph link (deferred features)` が**不在を機械で固定している** | issue の指示どおり **#452 の射程**。本作業は SC-03 を触らない |
| 却下解除（再提示理由）が実際に発火するか | **しない。** `AiSuggestionWiringTests` が `TryReinstate` に本番の呼び出し元が 0 件であることを固定している。発火は #911、本文変更の判定手段は ADR-0050 待ち | コメント 2・3 のとおり |
| タグ提案が SC-09 のタグ辞書の値域に収まるか | **収まらない。** `AiSuggestionGenerator.PersistAsync` は LLM が返したタグ値を trim して重複を除くだけで、辞書と突き合わせていない。`AiSuggestion.CreateTag` のコメント自身が「呼び出し側（サービス層）が辞書と突き合わせる」と書いているが、その実装が無い。辞書は DocumentService 側（`/tags`・**admin / operator 限定**）にあり、GraphService からも一般利用者の SPA からも引けない | issue の「必ず満たすこと」の 1 つ。**本作業では満たせない**（§未充足 を参照） |

**結論: 着手条件は満たされている。** 一覧を支える永続・状態遷移・ABAC 判定は在り、
残る欠落は「BFF の公開口」（本作業で足す）と「画面」（本作業で足す）である。

## 目的・範囲

SC-21（AI 提案一覧）を実装する。**一覧と導線まで**であり、承認・却下の実行は行わない。

### 射程に入るもの

1. `GET /bff/graph/suggestions`（読み取りのみ）を BFF へ足す。
2. `GET /graph/suggestions/` に `state=all`（すべて）を受け付けさせる（SC-21 入力表の 4 値目）。
3. 一覧の表示に要る**両端の文書名**を `AiSuggestionDto` へ足す（既定値つき＝非破壊）。
4. SPA の画面 `sc21-ai-suggestions`（ルート `/ai-suggestions`・既定 `?state=pending`）と左ナビ項目。
5. 画面仕様書 `docs/screens/SC-21_*.md`・テスト仕様書 `docs/tests/SC-21_*.md`・`IADR-0276`。

### 射程に入らないもの（**やらないことを明示する**）

- **SC-03 の AI 提案承認欄**（#452）。本作業は `sc03-document` の 1 行も触らない。
- **BFF への承認・却下の口**。消費者（SC-03 の承認欄）が無い口を先に開けない。IADR-0272 の write 認可の前提を壊さないため、
  口を開けるのは承認欄と同じ PR が正しい。
- **提案の生成**（#915 で着地済み）と**却下解除の発火**（#911・ADR-0050 待ち）。
- **タグ辞書の値域強制**（§未充足）。

## 母集合（自分で引いた結果と、除外したものとその理由）

規則 1〜6・9・10（`.claude/rules/traceability.md` ／ `traceability.repo.md`）に従い、
**誤りの側の文字列で**・**あり得る形を列挙して**・**拡張子で絞らず**・**行フィルタで絞らず**・**軸を複数**引いた。
走査は `git grep`（追跡下の全ファイル）で行い、`src/ai-stock-trading`（submodule。本リポから是正できない）だけを除外した。

| 軸 | 検索語 | 件数 | 使い道 |
| --- | --- | --- | --- |
| 1 | `SC-18`（直前に増えた画面の全波及先） | 68 ファイル | 「画面を 1 つ足すと何が動くか」の母集合。ここから下の追随先を起こした |
| 2 | `sc18` / `sc-18`（大小・ケバブ） | 71 ファイル | 軸 1 の取りこぼし（ファイル名・import パス・チャンク名）を拾う |
| 3 | `ai-suggestions` | 4 ファイル | 既存のテスト仕様書名・baseline の登録名 |
| 4 | `AI 提案`（日本語） | 51 ファイル | ID で書かれていない箇所（本文・カタログ・凡例） |
| 5 | `graph/suggestions`（後段パス） | 8 ファイル | BFF が引くパスの綴りと trailing slash の実測（`/graph/suggestions/`） |
| 6 | `AiSuggestionDto` | 5 ファイル | DTO 変更の波及先（契約 baseline を含む） |

### 追随先（軸 1・2 から起こした、画面を 1 つ足すと動くもの）

| # | 追随先 | 本作業での扱い |
| --- | --- | --- |
| 1 | `src/knowledge/frontend/src/features/index.ts`（ルートのタプル・左ナビ） | **足す** |
| 2 | `docs/api/openapi.yaml`（BFF 契約の単一情報源） | **足す**（`/bff/graph/suggestions` ＋ `AiSuggestion` スキーマ） |
| 3 | `src/platform/frontend/src/foundation/api/generated/**`（orval 生成物・コミット対象） | **再生成する** |
| 4 | `src/platform/frontend/src/foundation/i18n/locales/{ja,en}/messages.{po,ts}` | **再生成し、en の訳を埋める** |
| 5 | `scripts/chunk-budget-baseline.json`（初期ロードの ratchet） | **実測で更新する**（カタログ増分が初期チャンクへ入る） |
| 6 | `scripts/contract-schema-baseline.json`（契約スナップショット） | **`--update` で更新する**（非破壊の追加） |
| 7 | `scripts/test-spec-coverage-baseline.json`（仕様書 × テストクラスの対） | **`--update` で更新する** |
| 8 | `docs/tests/SC-21_*.md`（無いと `check-test-traceability` の逆方向が実装先行 fail） | **足す** |
| 9 | `.ai-context/adr/README.md`（IADR 索引） | **足す**（IADR-0276） |
| 10 | `src/platform/frontend/e2e/sc21-ai-suggestions.smoke.spec.ts` | **足す** |
| 11 | `src/platform/backend/Bff/Platform.Bff.Tests/`（BFF 層のテスト） | **足す**（`BffGraphSuggestionTests` ＋ `BffTestFactory` のスタブ） |

### 除外したものと、その理由

| 除外 | 理由 |
| --- | --- |
| `src/ai-stock-trading`（submodule） | 本リポジトリからは是正できない（IADR-0120）。別プロジェクトの名前空間であり SC-21 を持たない |
| `docs/api/BFF_bff-surface.md` のエンドポイント一覧 | **グラフの 3 本（#916a / #962）が既に載っていない。** 本作業が作ったずれではなく、既存の取り残しである。ここへ suggestions だけを足すと「グラフは無いのに提案だけある」表になり、かえって読み手を誤らせる。同書自身が「詳細は `openapi.yaml` を正とする」と宣言している。**別 issue の候補として報告する** |
| `docs/screens/SC-03_document-detail.md` / `docs/tests/SC-03_document-detail.md` | SC-03 の承認欄は #452 の射程。本作業は SC-03 の実装も記述も変えない（変えると #452 と衝突する） |
| `src/platform/frontend/vite.config.ts` の `manualChunks` | 画面はルート単位の遅延チャンク（`lazyRouteComponent`）であり、手で規則を足す対象ではない（SC-18 が ECharts のために足したのは**ベンダー**の規則であって画面ではない） |
| `scripts/knip-baseline.json` | 新しい export はすべて合成点（`features/index.ts`）から使われるため未使用にならない。**床は動かさない**（動いたら設計が誤っている合図として扱う） |
| `perf/graph-render/measure.mjs` | SC-18 の描画性能の計測器。一覧画面は描画ライブラリを使わない |

## 設計と判断

### 判断 1: BFF は**読み取り口だけ**を開ける（IADR-0276 決定 1）

`GET /bff/graph/suggestions?state=&kind=` の 1 本だけを足す。承認・却下・生成は**開けない**。

- SC-21 は「本画面では実行しない」と明記された**書き込みを一切しない画面**である（入力表 第 3 行）。
- 承認・却下の口を先に開けると、**消費者が居ないまま write 認可の境界（IADR-0272）が公開面へ出る**。
  境界を測るテストは SC-03 の承認欄と同じ PR に置くのが正しい（#916a の教訓:
  「GraphService 側で効いているから BFF 経由でも効く」は測った証拠にならない）。
- **一括承認の口はどの層にも作らない。** GraphService 側は `AiSuggestionEndpointsTests` が既に固定しており、
  BFF 側は本作業が固定する。

### 判断 2: 表示名はサーバ側で解決し、**辺の型名だけはクライアントで解決する**（IADR-0276 決定 2）

SC-21 の列は「提案の内容（**両端の文書名**・辺の型、またはタグ名）」である。現行 DTO は ID しか持たない。

- **文書名**: `AiSuggestionDto` に `SourceDocumentTitle` / `TargetDocumentTitle` を**既定値つきで**足す（非破壊。IADR-0122 決定 2）。
  一覧の処理は可視性判定のために既に両端の `GraphDocument` を読んでおり、**追加の照会は 1 件も増えない**。
  グラフのノード DTO が `(documentId, title)` を運ぶのと同じ形である。
- **辺の型名**: DTO へ入れない。**`/bff/graph/edge-types`（カタログ）でクライアントが解決する** ——
  ADR-0033 決定 9 が「表示名は辞書側で解決し、改名に追随させる」と定めており、SC-18 が既にこの形を採っている。
  DTO へ焼き込むと、型を改名しても一覧だけ古い名前を出し続ける。

### 判断 3: 状態フィルタの「すべて」は**後段が受ける**（IADR-0276 決定 3）

SC-21 の入力表は状態フィルタの値域を `pending`（既定）／`approved`／`rejected`／**すべて** とする。
現行の `GET /graph/suggestions/` は 1 状態でしか絞れず、`state=all` は `invalid_state` の 400 になる。

- **後段に `all` を足す。** クライアントで 3 回問い合わせて連結する案は採らない ——
  ①並び順が状態ごとに分断される、②可視性判定が 3 回走る、③「1 回だけ失敗した」という中途半端な状態が生まれる。
- `all` は**状態の値ではなくフィルタの解除**である。`SuggestionState` の値集合には足さない
  （`IsValid` に `all` を入れると、永続層に `all` という状態を書ける形になる）。端点側の定数として持つ。

### 判断 4: フィルタの単一情報源は URL（`/ai-suggestions?state=&kind=`）

SC-18 と同じ作法（IADR-0126 決定 3・IADR-0124）。Zustand などのクライアント状態を持ち込まない。
`state` は既定 `pending`（**URL に無くても pending**）、`kind` は既定 `all`（URL から省く）。
未知の値は既定へ縮退させる（手打ちの URL でエラー画面を出さない。防壁はサーバの 400 に在る）。

### 判断 5: 表は第 4 段の `DataTable`（TanStack Table v9）に載せる

`knowledge/frontend/src/components/DataTable.tsx` は IADR-0271（第 4 段）が用意した受け皿であり、
**本画面が最初の利用者**である。素の `<table>` を書くと土台が二重になる。
状態は `StatusBadge`（色 ＋ アイコン ＋ テキスト）で示し、**色だけで意味を持たせない**。

## 受け入れ基準

| # | 基準 | 出典 | 検証 |
| --- | --- | --- | --- |
| A-01 | 一覧が 種類 / 内容 / 状態 / SC-03 への導線 の 4 列を持つ | SC-21 主要素 1 | Vitest |
| A-02 | 状態フィルタが `pending`（既定）/ `approved` / `rejected` / すべて を持つ | SC-21 入力表 | Vitest ＋ BFF/後段テスト |
| A-03 | URL に `state` が無いとき `pending` で問い合わせる | SC-21 ルート | Vitest |
| A-04 | 種類フィルタ（すべて / リンク / タグ）が**同一の一覧**に同居する。種類ごとの別画面を作らない | SC-21 主要素 3・描いてはいけないもの | Vitest（ルート表の走査） |
| A-05 | **全行**が SC-03（`/docs/$id`）への導線を持つ | SC-21 主要素 4・描いてはいけないもの | Vitest |
| A-06 | 🔴 **一括承認・一括却下の手段が画面にも BFF にも無い** | FR-18・SC-21 描いてはいけないもの | Vitest（否定形＋陽性対照）＋ BFF テスト（ルート表の走査） |
| A-07 | 🔴 **承認・却下が本画面から実行できない**（承認は SC-03 経由のみ） | SC-21 入力表 第 3 行・UC-10 | Vitest（否定形＋陽性対照） |
| A-08 | 再提示された提案に**固定文言**が付く | SC-21 主要素 5 | Vitest |
| A-09 | 提案の根拠が表示される | SC-21 主要素 6 | Vitest |
| A-10 | 権限外の文書に関する提案は一覧にも件数にも現れない | SC-21 アクセス制御 | 後段テスト（既存 T-13）＋ BFF の透過テスト |
| A-11 | 辺の型名は辞書（カタログ）で解決し、改名に追随する | ADR-0033 決定 9 | Vitest |
| A-12 | 後段が引けないとき、空の一覧へ縮退しない | 共通規則（「無い」と「引けない」を混ぜない） | BFF テスト ＋ Vitest |

**満たせない基準**（§未充足 に理由を書く）: タグ提案が SC-09 のタグ辞書の値域に収まること。

## 未充足（実装しない／できないもの）

1. 🔴 **タグ提案の値域が SC-09 のタグ辞書に収まることを、どの層も強制していない。**
   - 実測: `AiSuggestionGenerator.PersistAsync` は LLM の返した `tagValue` を trim・重複除去するだけである。
   - **本作業では塞げない。** 辞書は DocumentService の `/tags` にあり **admin / operator 限定**である。
     一般利用者の SPA からは引けず（403）、画面側での検証は原理的に不可能である。
     GraphService から引くにはサービス間の新しい依存（named client ＋ 認可主体の決め方）が要り、
     それは #915 の射程かつ新しい IADR／裁定を要する。**推測で write のような主体を当てない。**
   - **本作業は「満たした」と書かない。** テスト仕様書へ未充足として明記し、報告で別 issue を推す。
2. **再提示理由が実際に付く経路は通っていない**（`AiSuggestionWiringTests` が固定）。
   画面は `reinstatedReason` が来たときの表示を実装し、**来る経路が無いことを仕様書に書く**。
3. **導線の先（SC-03 の承認欄）が未実装である。** 行のリンクは `/docs/$id` へ到達するが、
   その画面に承認 UI はまだ無い（#452）。**「承認できる」と書かない。**
4. **E2E（Playwright）で「一括承認が無いこと」を固定できない。** 本リポジトリの E2E は
   ビルド済みプレビューに対して走り、Keycloak も BFF も無い（`playwright.config.ts` / 既存 14 本の smoke が
   すべて「未認証 → /login」だけを見ている）。**認証済みの画面を Playwright で実走できない。**
   よって E2E は「ルートが実在し認証ガードが先に効く」ことだけを固定し、
   **A-06 / A-07 は Vitest（否定形＋陽性対照）で固定する。** issue 本文の「E2E で固定する」は
   この環境では満たせないため、**満たしたと書かない。**

## 変異試験（否定形テストの検出力の実測）

否定形（A-04 / A-06 / A-07）は「何も描かない実装」でも緑になる。**陽性対照と対で置き、変異で検出力を測った。**

| # | 変異 | 実測（落ちたテスト） |
| --- | --- | --- |
| M1 | 行に一括選択のチェックボックスと「選択した提案を一括承認」ボタンを足す | **2 件**（列数・「承認/却下/一括の操作が無い」） |
| M2 | 行に「承認」ボタンを足す | **1 件**（同上） |
| M3 | 種類フィルタを別ルート（`/ai-suggestions/tags`）へ割る | **1 件**（ルート表の走査） |
| M4 | 行の SC-03 リンクをタグ提案の行だけ落とす | **2 件**（全行の導線・操作の不在） |
| M5 | `state` の既定を `approved` にする | **2 件**（既定 pending・未知値の縮退） |
| M6 | 後段の失敗を空配列へ縮退させる | **1 件**（縮退させない） |
| B1 | 後段が `state=all` を無視して常に 1 状態で絞る | **1 件**（`すべて` の返り） |
| B2 | 不可視の端点でも表示名を返す | **1 件**（タグ提案の終点名が null） |
| B3 | BFF が後段パスの末尾スラッシュを落とす | **2 件**（経路の固定・ルート衝突） |
| B4 | BFF に承認の口を開ける | **1 件**（書き込み口の不在） |
| R1 | 画面のルート登録を features/index.ts から外す | **2 件**（計画ルート表・ナビ項目の解決）。**なお型検査も落ちる**（ナビの `to` がルート union に解決しなくなる） |
| E1 | 画面のルートのパスを改名する（**E2E の検出力の測定**） | 🔴 **0 件。ブラウザ E2E は落ちなかった** |

### 🔴 E1 の実測が示したこと —— E2E は「ルートの実在」を固定できない

既存のスモーク（`sc09-admin-abac.smoke.spec.ts` ほか）は
「ルートが登録されていないと NotFound が出て /login へ行かないため、この 1 本で
『ルートが実在すること』も同時に固定できる」と書いている。**これは誤りである。**

未知パスの受け皿（`catchAllRoute`）は **`RequireAuth` 配下の `shellRoute` の子**であり、
未認証なら受け皿へ到達する前にログインへ誘導される。**ルートを消しても改名しても E2E は緑のままである。**

- 本作業の E2E のコメントは、この実測に合わせて書き直した（「認証ガードが先に効くことだけを見る」）。
- **ルートの実在は `router.test.ts` が固定する**（計画のルート表 ＋ ナビ項目の解決）。
  本作業は同ファイルの計画ルート表へ SC-21 を 1 行足した。R1 でその検出力を実測した。
- **既存の他画面のスモークにある同じ誤った説明は本作業では直さない**（1 issue = 1 PR）。報告に残す。

**変異はすべて戻し、残渣 0 を走査（変異で入れた語の全数 grep）と `diff` で確認した。**

## 数値を動かした箇所（動かしたら、その値を持つ検査を引き直す）

| 検査 | 変化 | 引き直した結果 |
| --- | --- | --- |
| `chunk-budget`（初期ロードの床） | 582,839 → 586,001 B（+3.16 kB） | `--update` で更新し、増分の内訳を baseline のコメントに残した。**画面本体（3.81 kB）と表の土台（39.07 kB）は遅延チャンクで初期ロードに載らない**。`smallLazyChunks` は 6 のまま |
| `contract-schema`（契約スナップショット） | メンバー追加 2 件（**非破壊**） | `--update` で更新。破壊的 0 件・承認消費 0 件 |
| `test-spec-coverage`（仕様書 × テストクラスの対） | 133 → 135 対 | `--update` で更新（新しいテスト仕様書の 2 クラス分） |
| `check-knip`（未使用の床） | 一度 exports 17 件（+1）へ増えた | 🔴 **床を上げずに直した** —— `suggestionParams` は同一ファイル内でしか使わないので `export` を外した。床は 38 件のまま |
| カバレッジのしきい値 | 変えていない | 実測 98.13 / 91.91 / 94.24 / 98.13 に対し床は 93 / 87 / 89 / 93。**床は割っていない。** 余裕は本作業の前から在るものなので引き上げは行わない（別の判断である） |

## 検証手順

`src/` で `pnpm run typecheck` / `lint` / `format:check` / `test` / `test:coverage` / `build` / `test:e2e`、
`dotnet build`（knowledge / platform）と該当テストプロジェクトの `dotnet test`・`dotnet format --verify-no-changes`、
リポジトリルートで `check-*.js` 全数（`check-deploy-manifests` / `check-stack-ready` は helm/kubectl 不在の既知の環境要因）。
