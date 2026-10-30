---
title: 作業仕様書 — live-rw の M-114（SC-22 の試験キー）を Vault の居る使い捨てのスタックで、既存の項目に書いて流す（#2240 / E-54b）
type: spec
status: in-progress
related_ids: [SC-22, NFR-18, NFR-10, ADR-0095, ADR-0110, IADR-0453, IADR-0630, IADR-0658, IADR-0659]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2240"
---

# 作業仕様書 — live-rw の SC-22 の試験キー（#2240）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `df558cd7`）。前提の #2230（e2e-live に Vault と BFF の書き込みの役。IADR-0658）はマージ済み。

## 方針（オーケストレーターの裁定 2026-10-25。issue 本文）

- e2e 専用の項目は足さない（`deploy/bootstrap/sc22-secret-items.json` に差分を出さない）。**既存の項目の 1 つ**へ試験用の値を書く。
- 確かめること:
  - 一覧と再読込で値が出ないこと。
  - 発行値として成果物の走査（`--issued-file`）が探すこと。
  - 書き込みの監査が記録されること。
- 後始末は空に戻さない（スタックごと消える）。空の書き込みが仕様上できるかを確かめ、網羅表と食い違えば #2013 で訂正する。
- 選ぶ項目は、書き換えても同じ実行のほかの試験を揺らさないもの。

## 実測

- **空の書き込みはできない**: BFF の更新は値が空なら 400 `invalid-value`（`SecretItemBffEndpoints` の `valueInvalid`）。計画 SC-22（`05_screens/01_screens.md`）・ADR-0095 も空へ戻す操作を定めていない。→ 網羅表の「空に戻す」は #2013 で訂正する。
- **書ける役割は運用者・管理者**（`PlatformAuthPolicies.SecretItemWriter`）。`e2e-operator` の storageState を使う。
- **書いても揺れない**: 使い捨てのスタックには ESO が無い（IADR-0658）ので、Vault へ書いた値は Secret へ同期されない。同期の依頼は失敗に倒れても 200（`syncRequested=false`）。
  - 選ぶのは `ast-app-secrets` の `fred-api-key`（株式自動売買の外部の経済指標の API キー。秘匿のプロパティ）。
- **監査**: `AuditLogger` の構造化ログ 1 行（`Audit: action=secret.item.update subject=… outcome=granted detail=item=… property=… version=…`）。画面にも API にも出ないので、BFF の Pod のログを `kubectl logs -l app=bff-service`（context は `k3d-<E2E_STACK>` に固定）で読む。
  - 画面の「最終更新者」は書き込みの記録（`ISecretWriteRecordStore`）で、監査の主体と同じ利用者名である。
- **`fill` は値を step の題に載せ、html レポートに残す**（`support/login.ts` の fillSecret の実測）。React の制御された入力欄には、`value` の setter を直に呼んでから `input` を起こす形で入れる。
- **`expect(x).not.toContain(secret)` は失敗の文言に値を載せる**。真偽で比べる。

## 実装

1. `e2e/live/secrets.rw.spec.ts`（M-114。`@p1 @live-rw`）:
   - `E2E_SECRETS_LISTED=1` でなければ理由つきで skip する。
   - trace・スクリーンショット・動画は切る。
   - 値は書く前に `recordIssuedSecret('E2E_ISSUED_SECRET_ITEM_VALUE')` で記録する。
   - 画面で書き、応答に値の鍵が無く、版が整数であることを見る。
   - 再読込: 一覧の応答に値の鍵も値も無く、行は「設定済み」で「記録なし」でない。
   - 監査の granted の行（項目・プロパティ・版）が 1 行で、その主体が行の「最終更新者」に出る。値はログ・DOM のどこにも無い。
2. `support/bffAudit.ts`（BFF のログから監査の行と「値がログに在るか」の真偽だけを返す）と `support/secretInput.ts`（React の入力欄へ値を入れる）。
3. 文書: テスト仕様書 SC-22 に T-108、E2E_STANDARDS の live-rw の節に追記。

## 受け入れ基準

- [ ] M-114 が live-rw で流れる（e2e-live で skip 0・passed）
- [ ] 書いた値は成果物にも画面にも出ない（成果物の走査 0）
- [ ] 本番の SC-22 の項目一覧は変わらない（`sc22-secret-items.json` に差分が無い）

## 母集合（規則 9）

- `git grep -nE "M-114|試験キー"`:
  - IADR-0634・作業仕様書 20261025_2034 の「#2240 へ切り出し」→ 凍結の記録で、触らない。
  - `docs/tests/E2E_STANDARDS.md` の「使い捨てのスタックに秘密情報の保管先が入るまで別の作業に分けた」→ 日付つきで追記する。
- 網羅表（#2013 の issue 本文）の M-114 の「空に戻す」→ #2013 へコメントで訂正を依頼する。

## 残余リスク

- 監査は BFF のログで確かめる（ログの書式〔コンソールの既定〕が変わると行を拾えず赤になる。黙って緑にはならない）。
- ESO の同期・消費側への反映は確かめない（IADR-0658 の残余リスクと同じ）。
