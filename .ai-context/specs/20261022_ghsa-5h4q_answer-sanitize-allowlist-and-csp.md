---
title: 作業仕様書 — AI 回答・Wiki 本文の sanitize を allowlist へ絞り、SPA の配信に CSP を置く（GHSA-5h4q-4m2j-rjhm の 1 本目）
type: spec
status: done
related_ids: [NFR, ADR-0021, ADR-0031, IADR-0578, IADR-0362, IADR-0365, IADR-0134]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md （SPA の静的配信は Caddy）
  - planning:projects/microservices-platform/07_adr/ADR-0031 （フロントエンドの技術スタック）
issue: "GHSA-5h4q-4m2j-rjhm"
---

# 作業仕様書 — AI 回答・Wiki 本文の sanitize の allowlist 化と、SPA 配信の CSP

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0578**（新規）に置く。
> 多層防御の強化（許可を allowlist で閉じる・既定を最小権限にする）。参照は GHSA の ID だけとする。
> GHSA-5h4q-4m2j-rjhm の対応は 2 本に分ける（コーディネーター指示 2026-10-22）。本書は **1 本目**（描画の sanitize ＋ CSP）。
> 2 本目（RAG のプロンプトの信頼境界）は別の作業仕様書・別の PR で扱う。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR**（セキュリティの多層防御。該当する採番は無い）。計画 ADR: **ADR-0021**（SPA の静的配信は Caddy）・
  **ADR-0031**（フロントエンドの技術スタック）。いずれとも矛盾しない（配信サーバ・ライブラリの選定は変えない。
  計画書に CSP を定める記述・禁じる記述は無い——`grep -rni "csp\|content-security-policy" projects/microservices-platform` が 0 件）。
- 実装 ADR: **IADR-0578**（新設）。関連 **IADR-0362**（Caddy の配信。「CSP は入れない——別 issue の仕事」をここで引き取る）・
  **IADR-0365**（Wiki 本文の sanitize）・**IADR-0134**（初期ロードの ratchet。Zod を初期ロードへ載せない）。

## 要件（コーディネーター指示 2026-10-22）

1. AI 回答（RAG の出力）の描画の sanitize を絞る（`style`・フォーム部品を残さない）。
2. SPA の配信に CSP を置く。既存の画面（knowledge ユニットの features を含む）が壊れないことを E2E か単体試験で確かめる。
3. `src/` で lint / typecheck / 単体試験を通す。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| A1 | AI 回答の描画は、フォーム部品・`style`（要素・属性）・埋め込み（`iframe`/`object`/`embed`/`svg`/`math`）・`id`/`name`/`data-*`/`aria-*` を通さない | `markdown.test.ts`「drops form controls, style, embeds and presentational attributes」 |
| A2 | 表の揃え・番号付きリストの開始・脚注の印は残る（陽性対照） | 同「keeps the markup that markdown needs」 |
| A3 | リンクの URI は `http(s)` / `mailto`・断片・同一オリジンのパスだけ（スキーム相対・`data:` 等は落ちる） | 同「allows only http(s), mailto, fragment and same-origin path urls」 |
| A4 | `class` は脚注の印 `<sup class="cite">` にだけ残る | 同「keeps class only on the footnote marker」 |
| A5 | Wiki 本文の sanitize もフォーム部品と `style` を落とし、本文は残す | `sanitizeWikiHtml.test.ts`「drops form controls and style」 |
| A6 | 配信（Caddyfile）が CSP を返し、E2E の配信（vite preview）が同じ値を返す | `csp.smoke.spec.ts` 1 本目 |
| A7 | CSP の下でインラインのスクリプト・外部の画像・外部への通信が拒まれ、ガードがそれを観測できる（陰性対照） | `csp.smoke.spec.ts` 2 本目 |
| A8 | 既存の全 E2E（全画面）が CSP の違反 0 件で通る | `e2e/support/cspGuard.ts`（auto fixture。全 spec が import） |

変異の確認: A1・A3 は sanitize を従前の設定（html プロファイル）へ戻すと赤になる（手元で実測）。A8 のガードは、
着手時点で SC-08 の Zod の eval 試行を違反として実際に捕まえた（`jitless` で解消）。

## 母集合（規則 9・10）

- HTML として描く経路: `git grep -n "dangerouslySetInnerHTML\|innerHTML" -- src/*/frontend/src src/packages` →
  `MarkdownAnswer.tsx`（AI 回答）と `WikiBrowsePage.tsx`（Wiki 本文）の 2 か所だけ。両方を絞る。
- CSP の影響を受ける構成: `git grep -n "<iframe\|new Worker\|WebSocket\|EventSource\|<img\|new Function\|eval("`（AST ユニットを含む）。
  `<img>` は `/bff/...`（同一オリジン）と `blob:`（図）だけ。iframe・Worker・WebSocket は無い。eval は Zod の試行だけ（ビルド成果物で確認）。
- 「CSP は入れない」の記述: `git grep -n "CSP は"` → `Caddyfile`（本変更で書き換え）・IADR-0362・作業仕様書（凍結記録のため書き換えない。IADR-0578 から引き取る旨を書く）。

## 実装

- `src/platform/frontend/src/components/ai-chat/markdown.ts`: タグ・属性・URI を allowlist に閉じる。
- `src/knowledge/frontend/src/features/sc04-wiki/types/sanitizeWikiHtml.ts`: フォーム部品・`style` を落とす。
- `src/platform/frontend/Caddyfile`: CSP（単一情報源）。`vite.config.ts` の preview が同じ行を読む。
- `src/platform/frontend/e2e/support/cspGuard.ts`: 違反を落とす auto fixture。全 spec の import を差し替える。
- `src/knowledge/frontend/src/features/sc08-analysis/types/analysisFormSchema.ts`: Zod を `jitless` にする。

## 配備前の手順（PoC）

- 配備は閉場後。frontend のイメージを差し替えるだけ（値の変更・Secret の追加は無い）。
- 配備後の確認: 画面を開き、応答の `Content-Security-Policy` ヘッダが Caddyfile の値であること、ブラウザの開発者ツールに
  CSP の違反が出ないこと、AI チャットの回答と Wiki の本文が従前どおり描かれることを確かめる。
