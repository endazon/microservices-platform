---
title: 作業仕様書 — #920（値レベル変異の不検出）は既存 IADR-0246 で解決済みと独立監査で確認する
type: spec
status: done
related_ids:
  - NFR
  - IADR-0233
  - IADR-0246
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0027（メッセージング基盤・移行チェックリスト 手順 3〜6）"
issue: "#920"
---

# 作業仕様書: #920（封じ込め検査器が値レベルの変異を検出しない件）の独立監査

## 起点

- `#920`「封じ込め検査器が値レベルの変異を検出しない件を追跡する」（`NFR`, `IADR-0233` 起点）
- 本 issue はフェーズ末監査の性質を持つ —— **裁定・実装を書いたエージェントとは別の、フレッシュな
  文脈のエージェント**（本セッション）が、既存の裁定の妥当性を証跡つきで再確認する。

## 🔴 着手時に判明した事実 —— 対応する裁定は既に存在し Accepted である

作業着手前に `scripts/check-backend-libraries.js` と `.ai-context/adr/` を読んだ時点で、以下が
**すでにリポジトリに存在**していることを確認した。

1. `.ai-context/adr/IADR-0246_confinement-checker-blind-spots.md`（`status: Accepted`）
   決定 2「値レベル（A3/A5/A6）は塞がない」が、本 issue の本文と**同一の実測表・同一の根拠**
   （テストが届く／偽陽性費用が高い／CLAUDE.md の「2 回」規準に対し発生 0 回）で記録済み。
   `.ai-context/adr/README.md` の索引行（322 行目）は本 IADR を明示的に **`#919 / #920`** の
   両方に紐づけている。
2. `.ai-context/specs/20260822_issue-919-920_confinement-checker-blind-spots.md`（`status: done`）
   —— #919（`dist/` 配下・UTF-16LE の不可視領域）と #920（値レベル）を同一 PR（#903 系統）で
   扱った作業仕様書。受け入れ基準・実装後の実測結果まで記録済み。
3. `scripts/check-backend-libraries.js` の `--self-test` に、**#920 を名指しした固定ケースが
   既に 2 件ある**（「規則 5 は代入値を見ない」「規則 5 は前置の有無を見ない」）。
4. `.ai-context/specs/20260822_issue-914_ai-suggestion-state-machine.md` は #920 のこの裁定を
   **他の作業から「意図した不検出を固定する」の先例として引用**しており、裁定は既に外部から
   参照される確定物として扱われている。

**結論として、issue #920 が要求する「記録」は既に存在し、CI（`--self-test`）で固定されている。**
本仕様書の役割は、その裁定を**書いたのと別文脈で独立に再現・検証すること**であり、新しい記録を
重ねて作ることではない（重ねると「どちらが正本か」が生まれ、traceability.repo.md 冒頭の
「複写しない」原則に反する）。

## 独立監査 —— 実際に変異を入れて確かめた

自己試験内の断定（`confinedApiViolations` を直接呼ぶ単体テスト）だけでなく、**実ファイルに実際の
変異を入れて検査器本体（`node scripts/check-backend-libraries.js`）を通した**（issue の実測を
第三者が追試する形）。対象は封じ込め APIの本拠
`src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/WolverineExtensions.cs`。

手順: 元ファイルをスクラッチパッドへバックアップ → `sed` で 1 箇所だけ変異 → 検査器を実行して
終了コードを記録 → 元ファイルへ復元 → 復元後の内容が `md5sum` で元と一致することを確認。
**変異はリポジトリに残していない**（各回ごとに直後に復元、最終確認は diff なしと checksum 一致）。

| 変異 | 内容 | 検査器の結果（実測） |
| --- | --- | --- |
| A3 | 手順 5 の代入値のみ反転（`AlwaysAllowed` → `NotAllowed`） | **EXIT=0**（素通り） |
| A5 | 手順 3 の適用点から前置を落とす（`PlatformQueueName` が引数をそのまま返す） | **EXIT=0**（素通り） |
| A6 | 手順 3 の区切りを `.` → `-` | **EXIT=0**（素通り） |
| （対照）変異なしの元ファイル | — | EXIT=0（ベースラインも OK） |

いずれも issue #920 本文・IADR-0246 の実測表と一致した。

**テスト側が届いていることも確認した**（`WolverineExtensionsTests.cs` を読み、ビルドはしていない
—— 静的にアサーションの対応を確認）。

- L61: `options.ServiceLocationPolicy.Should().Be(ServiceLocationPolicy.AlwaysAllowed);`
  → A3（`NotAllowed` への反転）で確実に落ちる。
- L67: `WolverineExtensions.PlatformQueueName("wiki-service", "DocumentUpdated")` の期待値アサーション
  → A5（前置なし）・A6（区切り違い）のいずれでも落ちる（期待文字列が変わるため）。

## 「同型の事故が 2 回起きたら」規準の再確認

- 本件は**実際の本番事故ではなく**、`#903` の U4 変異試験という**能動的な探索**で見つかった
  理論上の不可視領域である。実害（誤って本番へ混入した実例）は **0 件**。
- 同型（「シンボルの有無は見るが値は見ない」検査の限界）の**過去の別インシデント**を
  `.ai-context/adr/` 全文・`docs/` 全文で検索したが、該当は無い
  （`値レベル` / `値の中身` で全文検索した結果は上表のとおり、本件自身の記録以外に一致しない）。
- したがって **今回が 1 回目**であり、CLAUDE.md の「1 回目は記録に留める」に正しく該当する。
  IADR-0246 決定 2 の判断（塞がない）は本監査でも支持される。

## 結論

1. **判定: 記録に留める（検査器を強化しない）。** IADR-0246 決定 2 の裁定は妥当であり、独立監査でも
   覆らない。
2. **新規 IADR は作成しない。** 同一決定を記録する IADR-0246（Accepted、`#919/#920` を明示的に紐づけ
   済み）が既に存在するため、新規 IADR を起こすと**同じ決定が 2 つの ID に散る**（このリポジトリの
   CLAUDE.md・traceability.repo.md が繰り返し禁じる「複写・二重管理」そのものである）。本件のために
   事前割当されていた空き番号は消費せず、採番の欠番を作らないよう他の記録へ回した。
3. **`scripts/check-backend-libraries.js` への変更は不要。** 現状（#919 分の SX-1/SX-2 修正＋#920 分の
   自己試験の固定ケース）で緑であることを検証した。ファイル自体は編集していない
   （他エージェント（#919 担当）との同時編集を避ける制約に従う）。
4. **issue #920 は IADR-0246 ＋ 本仕様書を根拠にクローズしてよい。** 統括側で issue コメントに
   `IADR-0246` と本仕様書へのパスを引用してクローズすることを推奨する。

## 検証（証跡）

```text
$ node scripts/check-adr-numbering.js
[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。

$ node scripts/check-doc-links.js
[check-doc-links] OK: 820 件の Markdown（走査ルート: docs, .ai-context）に破損した相対リンクはありません。

$ node scripts/check-backend-libraries.js
[check-backend-libraries] OK: 新規混入 0 件 / Domain 依存規律 OK（既知残件 11 件は baseline 済み）。

$ node scripts/check-backend-libraries.js --self-test
[check-backend-libraries] 自己試験 117 件 OK。
```

変異試験（A3/A5/A6）は上記表のとおり。各回、変異適用直後に `node scripts/check-backend-libraries.js`
を実行して EXIT コードを記録し、その場で元ファイルへ復元、最後に `diff` と `md5sum` で復元を確認した
（差分残留なし）。

## 受け入れ基準

| # | 基準 | 結果 |
| --- | --- | --- |
| 1 | issue #920 の実測（A3/A5/A6 が EXIT=0）を独立に再現する | ○（上表） |
| 2 | 「同型の事故が 2 回起きたら」規準に照らし、現時点の判定の妥当性を再確認する | ○（1 回目と確定） |
| 3 | 判定に応じた記録を残す（既存記録の追認、または新規記録） | ○（既存 IADR-0246 が既に記録。重複作成はしない） |
| 4 | `check-backend-libraries.js` を変更する場合はその内容を報告書に書く | 対象外（変更不要と判定） |
| 5 | 一時ファイルを残さない | ○（変異は実ファイルへ直接加えた後に即時復元。バックアップはスクラッチパッドのみ） |
| 6 | 検証コマンドが green | ○（上記 3 コマンドとも exit 0） |
