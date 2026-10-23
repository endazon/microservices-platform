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

## 母集合（規則 9・10）

- 誤りの側の文字列 `WCAG 2.1`・`wcag2a', 'wcag2aa'`・`6 面`・`走査面`・`NFR-12（アクセシビリティ）`・`#8a6210`・`5.48:1` で src・docs・deploy・scripts・.github を走査した。
- 追随: `docs/tests/E2E_STANDARDS.md` §7・§12（タグ・走査面・既知違反の台帳・Keycloak 画面の対象外）、
  `src/packages/ui/src/styles.css`（コントラストの実測の注記）、`deploy/keycloak/themes/platform/{login,account}/resources/css/tokens.css`（生成物）。
- 除外: `.ai-context/adr/IADR-0435`（`warning 5.48:1`）・`IADR-0440`・`IADR-0442`・`IADR-0569`・既存の `.ai-context/specs/*`（凍結記録）。
  `keyboard-navigation.smoke.spec.ts` の「WCAG 2.1.1」等は達成基準の番号であり誤りではない。`src/eslint.config.js:650` は領域外（報告）。
  `.claude/rules/traceability.repo.md` の「`NFR-01`〜`NFR-29`」は計画の NFR-30 追加で既に古い（本 PR の変更で生じた誤りではない。報告）。

## 検証

```bash
cd src && pnpm install --frozen-lockfile && pnpm run build
cd platform/frontend && PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers pnpm exec playwright test   # 103 passed（a11y 29）
cd src && pnpm run lint && pnpm run typecheck && pnpm run format:check
cd src && pnpm exec vitest run knowledge/frontend/src/features/sc19-private-notes packages/ui   # 219 passed
node scripts/check-knip.js --require && node scripts/gen-keycloak-theme-tokens.js --check
```

- 変異: 画面側の 2 修正を戻してビルドすると SC-19・SC-06・SC-07 の 3 面が赤（指紋つきで落ちる）。
  `makeAxeBuilder` から `wcag22aa` を外すと全面が「走査タグ」の陽性対照で赤。
