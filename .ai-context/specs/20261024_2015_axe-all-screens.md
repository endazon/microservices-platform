---
title: 作業仕様書 — [E-10] axe を全画面・操作後の UI へ広げ、タグを計画 NFR-30（WCAG 2.2 AA）にそろえる
type: spec
status: done
related_ids: [NFR-30, ADR-0031, ADR-0032, IADR-0614, IADR-0440, IADR-0442, IADR-0435, IADR-0532, IADR-0569, IADR-0330]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-30。planning#814 の裁定 2026-10-21・2026-10-22）
  - planning:projects/microservices-platform/05_screens/01_screens.md（§共通シェル「アクセシビリティ」）
issue: "#2015"
---

# 作業仕様書 — axe を全画面・操作後の UI へ広げる（E-10）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。判断の記録は **IADR-0614**（新規）に置く。
> 親は #2013（Playwright E2E の網羅。設計の正本）、網羅表の行は M-017（全画面・両テーマ・操作後 UI）と M-019（関連）。

## 計画の根拠

- planning#814（closed）の裁定: **WCAG 2.2 レベル AA を計画の NFR として確定**（MSP は NFR-30）。
  NFR-30 の本文（計画 `origin/main`）: 「機械検査（axe のタグ `wcag2a`・`wcag2aa`・`wcag21a`・`wcag21aa`・`wcag22aa`）が検出する違反を
  0 件に保つ。手動の検査は機械検査の代わりにしない。逆に、機械検査の 0 件は適合の証明ではない」。
  「`wcag21a`・`wcag21aa`・`wcag22aa` の機械検査は配備後に効く（受け皿 MSP#2015・E-10）」。
- 同裁定の残作業: タグを足す／テスト名の「WCAG 2.1」をタグと合わせる／「NFR-12（アクセシビリティ）」の誤参照を NFR-30 へ直す。

## 受け入れ基準

- [x] SPA が描く全画面 × 2 テーマ（light / dark）で axe の違反 0 件、または既知違反の台帳（指紋）と一致する。
- [x] ダイアログ・listbox・モーダル（確認ダイアログ）を開いた状態が走査に含まれる。
- [x] タグが NFR-30 の 5 つ（＋`best-practice`）であり、そのタグで走ったことを毎回確かめる。
- [x] テスト名・注記の「WCAG 2.1」が実際のタグと食い違わない。

## 方式

- 新規 `e2e/support/axe.ts`: `cspGuard` の `test` を拡張し、fixture `makeAxeBuilder`（NFR-30 のタグ付き `AxeBuilder`）と
  `expectAxeClean(surface)`（両テーマで走査 → 指紋を台帳と一致比較 → 走査タグと合格判定 1 件以上の陽性対照 → 結果を JSON で attach）を置く。
- 新規 `e2e/support/axeKnownViolations.ts`: 既知違反の台帳（キー `<面> / <テーマ>`、値は指紋と理由）。現在 0 件。
- `e2e/a11y.smoke.spec.ts` を 6 面 → **28 面**へ広げた（下表）。各面は最も緩い許可ロールで開き、`expectBffTrafficIsComplete` で応答の用意漏れを落とす。

| 群 | 面 |
| --- | --- |
| 入口・例外 | SC-13（SPA の入口。未認証）・共通シェル（管理者で全ナビ・右レール・通知を開く）・存在秘匿の 404 |
| 利用者 | SC-01・SC-02（結果 1 件）・SC-03・SC-04（ページ本文を開く）・SC-08・SC-18・SC-21 |
| 個人 | SC-19（一覧）・SC-19 削除確認ダイアログ・SC-19 共有先ダイアログ（listbox）・SC-19 緊急アクセスダイアログ（候補つき）・SC-20（競合の解決欄を開く）・SC-20 失効の確認ダイアログ |
| 運用者 | SC-05・SC-06・SC-07（失敗ジョブ 1 件）・SC-10・SC-11・SC-22・SC-22 書き込みの確認ダイアログ |
| 管理者 | SC-09（条件を足したポリシー登録）・SC-09 辺の型タブ・SC-12・SC-17（編集パネル）・SC-17 無効化の確認ダイアログ |

## 判断

1. **タグは NFR-30 の 5 つ＋`best-practice`**。`best-practice` は WCAG の適合タグではないが、ランドマークの規則はこのタグにしか無い（IADR-0442 決定 3）ので残す。
2. **既知違反は TS の台帳で「一致」を求め、`toMatchSnapshot` は採らない**（基準書 §8 は `toMatchSnapshot` と書く）。
   理由: `--update-snapshots` で理由なしに受け入れが増やせる／受け入れごとの理由を同じ場所に書けない。一致（部分集合ではない）なので、直して減っても台帳から消すまで落ちる（IADR-0614 決定 2）。
3. **SC-14〜16（と SC-13 の Keycloak 側）は L1 の対象外**。Keycloak のテーマ（FTL）が描き、SPA のビルド成果物に含まれず、基盤を起動しない層では描けない。
   Keycloak を起動する層での走査は別 issue の候補として報告する（IADR-0614 決定 3）。
4. **画面側で直せる違反は直した**（台帳は 0 件）:
   - SC-19 一覧: タブの器が `TabsList` だけを包み、`aria-controls` が描かれていないパネルを指していた（`aria-valid-attr-value`。WCAG 4.1.2）。
     器を一覧の末尾まで広げ、一覧を `TabsContent` に入れた。間隔は外側と同じ `gap-4` を持たせ、見た目を変えない。
   - SC-06・SC-07: `Note tone="warn"`（`text-warning` × `bg-surface-muted`）がライトで 4.44:1（`color-contrast`。WCAG 1.4.3）。
     要素ではなく組み合わせの問題なので、ライトの `--color-warning` を `#8a6210` → `#845f0f` に下げた（muted 面で 4.70:1・白面で 5.79:1。色相は不変）。
     Keycloak テーマのトークンは生成物なので `node scripts/gen-keycloak-theme-tokens.js` で再生成した（IADR-0532）。
5. 「NFR-12（アクセシビリティ）」の誤参照は、本 PR が触る e2e の 2 ファイル（`a11y.smoke.spec.ts`・`keyboard-navigation.smoke.spec.ts`）だけ NFR-30 へ直した。
   `src/eslint.config.js` の注記は本 issue の領域外、IADR-0440・0442 は凍結記録なので直さない（残作業として報告）。

6. ［2026-10-24 追記 / AI レビュー 🟡］台帳のキーが走査した「面 / テーマ」のどれかに当たることを専用の 1 本（`deadKnownViolationKeys`）で確かめる。
   変異: 存在しない面のキー `SC-99 存在しない面 / light` を足すとその 1 キーだけを挙げて赤、実在の `SC-01 / light` は挙げない。
   併せて `.claude/rules/traceability.repo.md` の NFR の採番を `NFR-01`〜`NFR-30` に広げ、別紙 §4 の内訳を追随した。
   ［2026-10-24 追記 / AI レビュー 🟡 2 回目］NFR の範囲の追随を、誤りの側の文字列（`NFR-29`・`NFR-01..29`・`NFR-01`〜`NFR-29`・`01〜29`・`〜29`）で
   `.ai-context/specs/`・`.ai-context/superpowers/`・`CHANGELOG.md`・submodule を除く全追跡ファイルで引き直した（`git grep`）。
   - 直した: `scripts/scripts.repo.test.js`（規範の固定 2・コメント 2）、`scripts/check-planning-adr-range.js`（注記・エラー文の例・書式の例の 3）、
     別紙の §4（内訳・根拠の件数）と履歴の節（［2026-10-24］`NFR-01..29` → `NFR-01..30` の見出しを足した）。
   - 除外: 確定済み IADR の本文（IADR-0508 の 2 か所。凍結記録）。別紙の過去の履歴の節（各回の時点の記述であり、当時は正しい）。
     `scripts/check-planning-adr-range.js:611・613` と `scripts/backlog-audit.js:371` の自己試験の固定値（宣言の遅れを意図して作ったもので、現状の主張ではない）。
     `NFR-29` を ID として引く箇所（警報・計測・IADR-0559 など。範囲ではなく要件そのものを指す）。ポート範囲の `20000〜29999`（無関係）。
   - 別件: 同じ計画の `origin/main`（`a47c050`）では計画 ADR が `0001..0143` で、宣言 `0001..0141` は 2 件遅れている。本 PR では引き直さない（報告）。
   ［2026-10-24 追記 / #2184］**上の NFR の範囲の追随一式（入口の 1 行・別紙・`scripts/` の固定値と注記）は、本 PR から外した。**
   AI レビューは `.claude/` を統合ブランチ版へ戻した作業ツリーで `scripts.test.js` を走らせるため、入口を直して固定値も直す PR は
   レビューの下限（`scripts.test.js` 全件）を構造的に満たせない（#2184 に起票）。外した一式は **#2184 の解消後に別の PR へ移した**。
   本 PR に残るのは、台帳の死に行の検査（判断 6 の前半）だけである。

## 母集合（規則 9・10）

- 誤りの側の文字列 `WCAG 2.1`・`wcag2a', 'wcag2aa'`・`6 面`・`走査面`・`NFR-12（アクセシビリティ）`・`#8a6210`・`5.48:1` で src・docs・deploy・scripts・.github を走査した。
- 追随: `docs/tests/E2E_STANDARDS.md` §7・§12（タグ・走査面・既知違反の台帳・Keycloak 画面の対象外）、
  `src/packages/ui/src/styles.css`（コントラストの実測の注記）、`deploy/keycloak/themes/platform/{login,account}/resources/css/tokens.css`（生成物）。
- 除外: `.ai-context/adr/IADR-0435`（`warning 5.48:1`）・`IADR-0440`・`IADR-0442`・`IADR-0569`・既存の `.ai-context/specs/*`（凍結記録）。
  `keyboard-navigation.smoke.spec.ts` の「WCAG 2.1.1」等は達成基準の番号であり誤りではない。`src/eslint.config.js:650` は領域外（報告）。
  `.claude/rules/traceability.repo.md` の「`NFR-01`〜`NFR-29`」は計画の NFR-30 追加で既に古い（本 PR の変更で生じた誤りではない。追随は #2184 の解消後の別 PR）。

## 検証

```bash
cd src && pnpm install --frozen-lockfile && pnpm run build
cd platform/frontend && PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers pnpm exec playwright test   # 103 passed（a11y 29 = 28 面 ＋ 台帳の検査）
cd src && pnpm run lint && pnpm run typecheck && pnpm run format:check
cd src && pnpm exec vitest run knowledge/frontend/src/features/sc19-private-notes packages/ui   # 219 passed
node scripts/check-knip.js --require && node scripts/gen-keycloak-theme-tokens.js --check
```

- 変異: 画面側の 2 修正を戻してビルドすると SC-19・SC-06・SC-07 の 3 面が赤（指紋つきで落ちる）。
  `makeAxeBuilder` から `wcag22aa` を外すと全面が「走査タグ」の陽性対照で赤。
