---
title: IADR-0620 SC-11 のドリフトの種別と深刻度は、計画（Q29）どおり契約の値のまま表示する（IADR-0129 決定 6 の表示名の写像を改める）
type: impl-adr
status: Accepted
related_ids: [SC-11, FR-15, IADR-0129, IADR-0029]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（§SC-11「ドリフト例の値域を揃えた（確定・2026-08-05・Q29）」）
related_specs:
  - ../specs/20261024_2022_sc08-sc10-sc11-e2e.md
---

# IADR-0620: SC-11 のドリフトは契約の値のまま表示する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2022。方針はオーケストレータの判断 (a)）

## 起点・関連

- 関連する計画書 ID: SC-11（構成ビューア）、FR-15（構成情報 API）。
- 計画の根拠: 05_screens §SC-11「ドリフト例の値域を揃えた（確定・2026-08-05。利用者裁定〔質問票 第12回 Q29〕）」の
  「**深刻度と種別は契約が返す値をそのまま表示する（日本語に訳さない）。** 本画面は運用者・管理者向けであり、実装との突合を容易にするためである」。
- 改める実装 ADR: [IADR-0129](IADR-0129_sc09-11-admin-ops-screen-composition.md) 決定 6 の表の `sc11-config/driftView.ts` の行
  （「ドリフト種別 5 値・深刻度 2 値 → `tone` ＋ 表示名」）のうち、**表示名**の部分。
- 作業仕様書: [20261024_2022_sc08-sc10-sc11-e2e](../specs/20261024_2022_sc08-sc10-sc11-e2e.md)。

## コンテキストと課題

- E2E の網羅（#2013 の網羅表 M-098「5 分類 × 深刻度 2 値を契約値のまま表示」）を書いたところ、画面は種別を
  「接続の不一致」などの日本語の表示名に、深刻度を「警告」「情報」に写していた。
- この写像は IADR-0129（2026-08-05）が入れたものである。IADR-0129 は計画の Q29（同日確定）を変えないまま、表示名を足していた。
- 計画の確定事項は、実装 ADR だけでは覆せない（CLAUDE.md「計画書（特に fixed / Accepted）に反する実装」の禁止）。

## 検討した選択肢

1. **計画どおり、契約の値のまま出す（採用）。**
2. 写像を維持し、計画の改定を求めて環流する。計画は理由（実装との突合）を明記して確定しており、変える根拠が無い。

## 決定

1. **ドリフトの種別（5 値）と深刻度（2 値）は、契約が返す値の文字をそのまま表示する。**
   - 種別: `MissingApply` / `UndeclaredSubscription` / `StaleStage` / `BindingMismatch` / `Unverifiable`
   - 深刻度: `Warning` / `Info`
   - 日本語の表示名へは写さない。値集合の外の値も、従来どおり生値のまま出す（丸めない）。
2. **深刻度から写すのは `StatusBadge` の tone だけにする**（`Warning` → `warning`、`Info` → `neutral`、未知 → `neutral`）。
   - tone ごとの固定アイコンと契約の値の文字が併存するので、「色だけで意味を持たせない」（INDEX 決定 21）は保たれる。
3. **IADR-0129 決定 6 の値集合の固定（純関数テスト）は維持する。** 固定する写像が「表示名」から「tone」に変わるだけである。
4. 画面仕様書 `docs/screens/SC-11_configuration-viewer.md`（対応表 14・モックの読み替えの表・§状態表示）と、
   テスト仕様書 `docs/tests/SC-11_configuration-viewer.md` を取り消し線と日付つき追記で改める。

## 理由

- 計画が確定済みで、理由も明記している。運用者・管理者が、ログ・API の応答・後段のコード（`DriftDetector` の定数）と
  画面の表示を、文字のまま突き合わせられる。
- 表示名を挟むと、ログに出る `BindingMismatch` と画面の「接続の不一致」を、利用者が頭の中で対応づける必要が生まれる。

## 結果

- 画面の翻訳カタログから、ドリフトの種別の表示名 5 語が消える（`lingui extract --clean`）。深刻度の「警告」「情報」は他の画面でも使うので、カタログに残る。初期ロードの床は `--update` で測り直す。
- E2E（`sc11-config.smoke.spec.ts` の M-098）が、表の各行の種別・深刻度のセルが契約の値と一致することを固定する。
  直す前に赤になることを確かめた。

## 関連

- Amends: [IADR-0129](IADR-0129_sc09-11-admin-ops-screen-composition.md) 決定 6（`driftView.ts` の表示名の写像）
- Supersedes: なし
