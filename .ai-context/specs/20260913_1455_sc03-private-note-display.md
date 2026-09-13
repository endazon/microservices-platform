---
title: "SC-03 に個人資料の表示（所有者・公開範囲・👤 ラベル）を置き、属性・タグパネルを 3 カテゴリへ閉じる（#1455。計画 ADR-0102）"
type: spec
status: done
related_ids: [FR-19, UC-11, SC-03, SC-19, ADR-0036, ADR-0098, ADR-0100, ADR-0101, ADR-0102, IADR-0444, IADR-0450, IADR-0451]
author: Claude
created: 2026-09-13
updated: 2026-09-13
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0102_private-note-display-to-non-owner-viewers.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 仕様書: SC-03 の個人資料の表示と、属性・タグパネルの 3 カテゴリ化

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-19`（個人資料）
- 非機能要件（NFR）: なし（画面表示。個別番号を当てない）
- ユースケース（UC）: `UC-11`
- 画面（SC）: `SC-03`（§個人資料の表示・§主要素）。供給元として `SC-19`
- 関連 ADR: `ADR-0102` 決定 1〜5（本作業が反映する決定）／`ADR-0101` 決定 1・2（所有者以外に `sharedWith` を返さない）／
  `ADR-0098` 決定 1（画面には表示名を出し識別子は出さない）／`ADR-0100`（表示名の到達範囲）／`ADR-0036` D-06・D-08・D-09
- 計画書リンク: `planning:projects/microservices-platform/07_adr/ADR-0102_private-note-display-to-non-owner-viewers.md`

## 目的・背景

計画 `ADR-0102` が**所有者以外の閲覧者への個人資料の描き方**を確定した。SC-03 側の実装は 2 つある。

1. **個人資料の表示を新設する**（現状は該当表示 0 件）—— 👤 のラベル・所有者・公開範囲・SC-19 への導線。
2. **属性・タグパネルを計画の 3 カテゴリへ閉じる** —— 現状は応答の属性を全件そのまま描いており、
   個人資料では `owner=<利用者名>` / `doc_scope` / 露出 3 トグルの生の行が読める者すべてに出ている（計画からの乖離）。

## 実装の現状（着手前の実測）

| 箇所 | 現状 |
| --- | --- |
| `features/sc03-document/types/attributes.ts` | ラベルを持つのは `confidentiality` / `department` の 2 キーだけで、**それ以外はキーも値も生のまま**（`orderedAttributes` が既知キー ＋ 残り全部を返す） |
| `features/sc03-document/components/DocumentDetailPage.tsx` | `AttributeList` が `doc.attributes` を全件描く。個人資料としての表示は無い |
| 個人資料の既定属性 | `doc_scope=private-note` / `owner=<利用者名>` / `confidentiality=restricted` ＋ 露出 3 トグル（バックエンド `PrivateNoteEndpoints.PrivateNoteDefaults`） |
| 公開範囲の供給 | `PrivateNoteDto.visibility`（3 状態）＋ `sharedUserCount` / `sharedGroupCount`。**所有者だけが読める**一覧の口（`GET /bff/private-notes`）に載る。🔴 **`PrivateNoteDto.id` は文書 ID である**（バックエンド `PrivateNoteMapper` が `PrivateNote.DocumentId` を写す） |
| 3 状態の導出 | **サーバが持つ**（`PrivateNoteEnrichment.VisibilityOf`。グループ共有があれば `groups`）。画面で導出し直さない |
| 表示名の供給 | `POST /bff/users/resolve`。SC-19 の `useResolvedUsers`（`features/sc19-private-notes/api/useUserLookup.ts`）が生成関数を `useQuery` に据える作法で実装済み |
| feature 間の import | 🔴 **できない**（`import/no-restricted-paths` の `featureIsolationZones`）。共有はユニットの `lib/` に置く（`lib/abac` / `lib/scope-filter` の前例。公開面は `index.ts` 1 枚） |
| `doc_scope` の語彙 | フロントに定数が無い（テストが文字列で書いているだけ）。`lib/abac/` が機密区分・部門・ライフサイクルの語彙を持つ |
| 現在の利用者 | `useAuth().user.name`（`/bff/auth/me` の `preferred_username`）。`owner` と同じ名前空間 |

## 設計（決定。詳細は `IADR-0451`）

1. **属性・タグパネルは既知キーだけを描く**（`confidentiality` / `department`）＋タグ。**未知キーは落とす**（whitelist）。
   `ATTRIBUTE_LABELS` が唯一の値域であり、**属性が増えても画面の統制が自動で緩まない**。
2. **個人資料の欄を新設する**（`PrivateNoteSummary`）。`doc_scope === 'private-note'` のときだけ描く。
   - **ラベル**: 👤 ＋「個人資料」。**所有者にだけ括弧書き「（自分のみ）」**を付す（ADR-0102 決定 5）。
   - **所有者**: `attributes.owner` を**表示名へ引いて**出す（`useResolvedUsers`）。🔴 **引けなければ「（不明な利用者）」**で、
     **利用者名へフォールバックしない**（ADR-0102 決定 3）。無効化済み（退職者）も表示名が返る。
   - **公開範囲**: **所有者にだけ**描く。所有者以外には**欄ごと出さない**（ADR-0102 決定 1）。
   - **SC-19 への導線**を置く（計画 §個人資料の表示）。
3. **公開範囲の供給は「所有者だけが読める口」から取る**（ADR-0102 決定 2）。`lib/private-notes` が
   **生成フックと同じ query key** で一覧を引き、`id === doc.id` の 1 件を選ぶ。**3 状態はサーバの値をそのまま使い、画面で導出しない**
   （SC-19 と導出点を 2 つにしない）。
   - **問い合わせは `doc_scope === 'private-note'` かつ `attributes.owner === セッションの利用者名` のときだけ発火する**（無駄な取得を避ける）。
   - 🔴 **可視性を決めるのはサーバである** —— 口は本人の資料しか返さないため、**門が誤っても他人の公開範囲は出ない**（多層）。
4. **共有物の置き場**: `useResolvedUsers` を `lib/users/` へ移し、SC-19 と SC-03 が同じ実装を使う。
   `doc_scope` の語彙は `lib/abac/docScope.ts`（公開面は `lib/abac/index.ts`）。**feature 間 import を作らない。**
5. **契約・バックエンドは変更しない。**

## 影響範囲（専有領域）

- `src/knowledge/frontend/src/features/sc03-document/**`（表示・属性・テスト）
- `src/knowledge/frontend/src/features/sc19-private-notes/api/useUserLookup.ts`・`components/ShareTargetsDialog.tsx`（`useResolvedUsers` の移設に伴う import 変更のみ）
- `src/knowledge/frontend/src/lib/users/**`（新設）・`src/knowledge/frontend/src/lib/private-notes/**`（新設）・`src/knowledge/frontend/src/lib/abac/**`（`docScope` 追加）
- `src/platform/frontend/src/locales/**`（Lingui カタログの再生成分）
- `docs/screens/SC-03_document-detail.md`（§属性の表示 の「上記以外＝キーをそのまま表示」を改める・個人資料の表示を足す）
- `.ai-context/adr/IADR-0451_*.md`（新規）・`.ai-context/adr/README.md`（索引）

### 母集合（規則 1〜10）

走査語: `orderedAttributes` / `attributeLabel` / `属性・タグ` / `個人資料の表示` / `useResolvedUsers` / `doc_scope`（追跡下の全ファイル。submodule を除く）。

| 出た箇所 | 扱い |
| --- | --- |
| `sc03-document` の `types/attributes.ts`・`components/DocumentDetailPage.tsx` とテスト | **対象**（本作業の中心） |
| `sc19-private-notes` の `api/useUserLookup.ts`・`components/ShareTargetsDialog.tsx` | **対象**（`useResolvedUsers` の移設。**振る舞いは変えない**） |
| `docs/screens/SC-03_document-detail.md` §属性の表示・§実装する / しない要素 | **対象**（規則 9・10。**「上記以外＝キーをそのまま表示」は本変更で誤りになる**） |
| `.ai-context/adr/IADR-0006` / `IADR-0353` / `IADR-0380`、`.ai-context/specs/` の過去分（`20260709_issue-129_sc03-document-detail` ほか） | 対象外。**凍結された記録**（point-in-time）であり、当時の実装を述べている |
| `sc05-documents` / `sc17-users` / `sc12-mcp-clients` の `doc_scope` 参照 | 対象外。属性の**編集・割り当て**の面であり、SC-03 の表示規則とは別 |
| `docs/screens/SC-19_private-notes.md` | 対象外。**本画面の表示は変わらない**（#1457 で書き分け済み） |

## 受け入れ基準（issue #1455 の写像）

- [x] 共有された相手が個人資料を開くと**公開範囲の欄が出ない**（空欄・既定値も出ない）
- [x] 所有者が開くと**公開範囲が 3 状態のいずれか**で出る（供給は所有者だけが読める口。`sharedWith` から導かない）
- [x] 共有された相手にも**所有者が表示名で**出る。**利用者名は画面に出ない**
- [x] 表示名を引けないときは**「（不明な利用者）」**（利用者名へフォールバックしない）
- [x] 無効化済み（退職者）の所有者でも表示名が出る
- [x] 所有者以外のラベルは「**個人資料**」、所有者は「**個人資料（自分のみ）**」
- [x] 属性・タグパネルに `owner` / `doc_scope` / 露出 3 トグルの行が**出ない**
- [x] 組織文書の表示は従来どおり（回帰なし）

## 検証（`/verify` 相当）

`npx vitest run knowledge/frontend` / `pnpm run format:check` / `pnpm run lint` / `pnpm run typecheck`（knowledge） /
`node scripts/check-i18n-catalogs.js` / `check-trace-blocks` / `check-doc-links` / `check-doc-type-vocabulary` /
`check-doc-status-vocabulary` / `check-adr-numbering` / `gen-knowledge-graph --check` /
`node scripts/check-commit-messages.js --range origin/develop..HEAD`。
