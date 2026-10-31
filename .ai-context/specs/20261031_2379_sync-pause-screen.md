---
title: 作業仕様書 — SC-20 に同期の状態と一時停止・再開の操作を置き、同期履歴の停止期間の行を描く（#2379。#2127 の子 B。計画 ADR-0145 決定 5）
type: spec
status: done
related_ids: [FR-20, UC-11, SC-20, ADR-0145, ADR-0099, IADR-0667, IADR-0446, IADR-0668]
author: claude
created: 2026-10-31
updated: 2026-10-31
issue: "#2379"
---

# 作業仕様書 — SC-20 の同期の一時停止（画面）（#2379）

> 本仕様書は実装着手前に作成した（着手 2026-10-31。develop `830c6c84`。子 A #2381 はマージ済み）。
> ADR-0145 フォローアップ 2「サーバの拒否より先に画面の操作を置かない」は、子 A のマージで満たされた。

## 計画の要点（SC-20 主要素 7・ADR-0145 決定 5）

- 同期の状態（有効／一時停止中）と、一時停止・再開の操作を**見出しの近く**に置く。端末一覧の行ごとには置かない（利用者単位）。
- 操作の近くで 2 点を伝える。「すべての端末の同期が止まります。トークンは残り、再開すれば同期が戻ります」。
- 「一時停止中」は ADR-0145 の一時停止だけを指す。トークンの期限切れ・同期停止（ADR-0037 決定 4）を含めない。
- 停止中の拒否は同期履歴（主要素 6）に、停止期間ごとに 1 行で出る。拒否回数と最初・最後の拒否の時刻を出す。

## 現状（実測。develop `830c6c84`）

- BFF に `POST /bff/private-notes/sync-settings/pause`・`/resume` があり、orval の生成物に `useBffSyncSettingsPause`・`useBffSyncSettingsResume` がある（子 A）。
- `SyncSettingsDto` は `syncPaused`・`syncPausedAt` を持つ。`SyncHistoryEntryDto` は、停止期間の行だけが `rejectedCount`・`firstRejectedAt`・`lastRejectedAt` を持つ。その行の `direction` と `deviceName` は空、内訳は 0。
- SC-20 の画面（`ObsidianSettingsPage.tsx`）に、一時停止の表示・操作は 0 件。同期履歴の失敗理由の写像（`types/syncHistory.ts`）は 7 値で、`sync_paused` が無い（未知の値として「同期に失敗しました（理由: sync_paused）」になる）。
- 画面仕様書 `docs/screens/SC-20_obsidian-settings.md` は、一時停止を「しない（呼ぶ口が無い）」と書いている（:31・:165・:188）。

## 母集合（規則 9）

`git grep -n -E "一時停止|syncPaused|sync_paused|SYNC_FAILURE_REASONS|呼ぶ口が"` を `src/knowledge/frontend`・`src/platform/frontend/e2e`・`docs/screens/SC-20*`・`docs/tests/SC-20*` に掛けて引いた。

| 対象 | 扱い |
| --- | --- |
| `sc20-obsidian-settings/components/SyncPausePanel.tsx`（新規） | 状態の表示・操作・注記 |
| `sc20-obsidian-settings/api/useSyncSettings.ts` | 一時停止・再開の操作（成功後に同期設定と同期履歴を引き直す） |
| `sc20-obsidian-settings/components/ObsidianSettingsPage.tsx` | 見出しの直下に区画を置く |
| `sc20-obsidian-settings/types/syncHistory.ts`・`components/SyncHistoryPanel.tsx` | `sync_paused` の文言・停止期間の行（端末・方向・時刻の欄） |
| 単体試験（`ObsidianSettingsPage.test.tsx`・`SyncHistoryPanel.test.tsx`・`types/syncHistory.test.ts`） | 追加 |
| `src/platform/frontend/e2e/sc20-obsidian-settings.smoke.spec.ts` | 一時停止・再開の 1 本と、POST のモック |
| Lingui のカタログ（ja・en） | 抽出と英訳 |
| `docs/screens/SC-20_obsidian-settings.md`・`docs/tests/SC-20_obsidian-settings-screen.md` | 「しない」を「する」へ改め、経緯を日付つきで残す |

除外:
- サーバ・契約・BFF（子 A で済んだ）。
- Obsidian プラグイン（ADR-0145 決定 2 で、統制はサーバが担う。プラグインの表示は本 issue の受け入れ基準に無い）。
- `.ai-context/specs/` の過去の記録（凍結）。

## 方針（IADR-0668）

1. **区画**: 見出し（h1）の直下に「同期の状態」の区画を置く。
   - 状態は `StatusBadge` で、色＋文字で出す。有効は「同期: 有効」、停止中は「同期: 一時停止中（{開始} から）」。
   - 操作のボタンは 1 つで、状態に応じて「一時停止する」か「再開する」になる。
   - 注記は常に出す。計画の 2 点「すべての端末の同期が止まります。トークンは残り、再開すれば同期が戻ります」と、期限は止まらないこと。
2. **確認ダイアログは置かない。** 一時停止は再開で戻せる操作で、データも消さない。端末の失効（戻せない）とは重さが違う。
3. **失敗の表示**: 操作の失敗は、区画の中の Alert（`role="alert"`）に出す。成功後は、同期設定と同期履歴を引き直す（拒否の行が見えるように）。
4. **同期履歴の停止期間の行**:
   - 失敗理由の文言は「同期を一時停止していたため、端末からの同期を拒否しました。再開すると、同じトークンのまま同期が戻ります。」。回数は内訳の列に「拒否 N 回」で出す（［2026-10-31 追記 / #2384 の AI レビュー 🟢］当初は文言に回数を含める案だったが、回数は内訳の列に置いたので文言から外した）。
   - 端末の欄は「すべての端末」。行は特定の端末のものではなく、空のままでは取得漏れと区別できない。
   - 方向・内訳は「—」。
   - 実行日時の欄は「{最初} 〜 {最後}」。
5. **並び順は、サーバの順（`occurredAt` の降順）のまま変えない**（AI レビュー #2381 の 🟢 の引き継ぎ）。理由は 3 つある。
   - 停止中は同期の口がすべて拒まれ、その停止期間の拒否は 1 行にしか積まれない。停止期間の間に他の同期の行は書かれないので、最初の拒否の時刻で並べても、その行より新しい行が停止期間の内側に入ることは無い。
   - 再開の後の行が上に来るのは、実際に後で起きたので正しい。
   - 表示の 50 件はサーバが `occurredAt` で切る。画面だけ `lastRejectedAt` で並べ替えると、切り方と並べ方が食い違う。最後の拒否の時刻は、行の中（実行日時の欄の範囲）で見せる。

［2026-10-31 追記 / #2384 の AI レビュー 🟡1・🟡2］設定の取得中は「同期: 確認中」、取得の失敗は「同期: 状態を確認できません」と区画の中の Alert にし、
どちらの間も「同期: 有効」を出さない。停止中で開始の時刻が無いときは「同期: 一時停止中」だけにする。

## 受け入れ基準

- [x] SC-20 で一時停止・再開でき、状態の表示が変わる。注記の 2 点が出る
- [x] 停止中の拒否の行が、理由・回数・最初と最後の時刻で読める
- [x] 「一時停止中」は ADR-0145 の一時停止だけを指し、トークンの期限切れや同期停止（ADR-0037 決定 4）を含めない（期限切れの端末があっても、状態は「有効」のまま）
- [x] 操作は利用者単位で 1 つ。端末一覧の行に一時停止の操作は無い（否定形は陽性対照と対で）

## 赤→緑

結果は PR 本文に載せる。
