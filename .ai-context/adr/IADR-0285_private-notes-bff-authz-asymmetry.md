---
title: IADR-0285 個人資料 BFF の認可前段は資料の書き込みだけに置く（端末・トークン系と読み取りはゲート外）
type: impl-adr
status: Accepted
related_ids:
  - FR-19
  - FR-20
  - SC-19
  - SC-20
  - ADR-0036
  - ADR-0037
  - IADR-0039
  - IADR-0272
author: claude
created: 2026-08-28
updated: 2026-08-28
---

# IADR-0285 個人資料 BFF の認可前段は資料の書き込みだけに置く

## 起点・関連

- #451-a（`/bff/private-notes*` 11 端点。実装は `Knowledge.Bff.Endpoints/PrivateNoteBffEndpoints.cs`、
  経緯は作業仕様書 `20260828_issue-451a_private-notes-bff.md`）。
- 波 3 フェーズ末監査の懸念 C: **認可境界の非対称という判断がコード注釈と仕様書にしか無い**
  （CLAUDE.md「重要な実装判断は実装 ADR に必ず残す」）。本 ADR がその記録である。

## コンテキストと課題

個人資料 BFF の 11 端点のうち、**資料系の書き込み 5 口**（作成・論理削除・復元・露出変更・完全削除）
だけが `BffScopeAction.Write` の前段（`ForwardIfWritableAsync`）を持ち、**読み取りと端末・トークン系
4 口**（発行・再発行・個別失効・一括失効）は認証必須＋ Authorization 転送のみ
（`ForwardAsync`）である。この非対称は意図した設計だが、決定の記録が分散していた。

## 決定

1. **資料系の書き込みだけに `write` の ABAC 前段を置く**（IADR-0272 / #1010 の規律に整合。
   deny は 403）。
2. **読み取りに前段を置かない。** 個人資料の一覧・取得は下流（DocumentService）が
   `SubjectOf`（トークン主体）＋台帳 `OwnerId` で絞り、他者の資料は 404 で秘匿する。
   `BffScopeResolver.Matches` は**文書属性**を見る評価器であり、台帳の投影に当てると
   全件不一致になって**自分の資料が 0 件になる**（実測）。秘匿すべき相手も居ない
   （本人の資料しか返らない）。
3. **端末・トークン系（SyncDevice）の 4 口に前段を置かない。** 計画 SC-20 は個別失効を
   「端末紛失時の唯一の防御線」と定めており、**失効の可否を文書 ABAC ポリシーの整備状況に
   従属させない**（write ポリシー未登録の環境で失効まで deny になる事故を作らない）。
   本人性は下流の `OwnerId` 照合が担う（IADR-0039 の資格情報系の切り分けと同型）。
4. **write ゲートは「作成の封じ込め境界」ではない**と明記する —— 同期経路（Obsidian
   プラグイン）は同期トークンだけで資料を作れる（ADR-0037 課題 2 の設計）。過大申告しない。

## 検討した選択肢（要点)

- **全 11 口に write 前段**: 決定 2・3 の実測・防御線要件に反する（自分の資料が見えない／
  紛失時に失効できない）。不採用。
- **端末系だけ別 action（manage 等）を新設**: 計画に判定規則が無い action を増やすのは
  planning#491 の論点と同じ形で実装側では決められない。不採用。

## 結果

- 実装・テスト（`BffPrivateNoteEndpointTests` の陽性対照つき否定形一式）は #451-a で着地済み。
  本 ADR は判断の所在を実装 ADR へ揃える記録であり、コードの変更を伴わない。
- 変異試験（#451-a 仕様書）: Authorization 転送を落とすと 19 件 fail／`write` を `read` へ
  劣化させると 6 件 fail —— 非対称の両側が試験で固定されている。

## 関連

- 作業仕様書: `.ai-context/specs/20260828_issue-451a_private-notes-bff.md`
- IADR-0272（write の明示規律）・IADR-0039（資格情報系の切り分け）・計画 SC-20 / ADR-0037
