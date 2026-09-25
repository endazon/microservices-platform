---
title: 作業仕様書 — SC-22 の供給元の表示名を「画面以外」へ改め、再起動を伴う項目で書き込みの確認を消費側の再起動の確認とする（#1523・ADR-0110 決定 1〜3）
type: spec
status: done
related_ids:
  - SC-22
  - FR-05
  - NFR-18
  - ADR-0110
  - ADR-0104
  - ADR-0095
  - IADR-0460
  - IADR-0456
  - IADR-0453
  - IADR-0433
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0110_sc22-supplier-three-values-no-public-key-restart-confirmed-at-write.md (Accepted 2026-09-26)
  - planning:projects/microservices-platform/05_screens/01_screens.md (§SC-22 主要素 1・2 と §アクション の 2026-09-26 改訂)
related_specs:
  - 20260925_1502_sc22-supply-source-and-restart-notice.md
issue: "#1523"
---

# 作業仕様書 — SC-22: 「画面以外」と、書き込みの確認を再起動の確認とすること

## 目的と射程

planning#652 の裁定 2〜4（ADR-0110）を SC-22 に写す。

| ADR-0110 | 内容 | 本件 |
| --- | --- | --- |
| 決定 1 | 供給元は「画面／画面以外／確認できない」。表示名「Git」を改める | 表示名を改める。**契約の値 `git` は変えない**（下記 §判断 1） |
| 決定 2 | 公開鍵の欄を取り下げる | **実装しない**（もともと無い）。画面仕様書の「未決・計画への確認が要る」を「取り下げ」に改める。IADR-0433 に公開鍵の記述が無いことを確かめた（§判断 4） |
| 決定 3 | 再起動を伴う項目では送る前に確認の段を置き、再起動する消費側と断たれ得る処理の種類を出す。確認して書けば即時同期と自動の作り直しが続く。別の再起動の操作は置かない。BFF に権限を足さない。配備していない環境では確認の段と書き込み後の表示で「再起動するまで反映されない」。OpenD の項目は手動の再起動（書き込みでは再起動しない旨）。「画面以外」では出さない。「確認できない」では再起動し得る旨で出す | 確認ダイアログ（`ConfirmDialog`＝`@platform/ui` の `Dialog`）を置く。**BFF・契約・権限は変えない** |

**射程外**: 本番の消費側の作り直し方（IADR-0456 フォローアップ 3）。ADR-0110 フォローアップ 4 は「決めるときは利用者が確認した書き込みを契機とするものに限る」とだけ定めており、本件は何も決めない。

## 計画の読み（逐語で確かめたこと）

- 決定 3「**再起動を伴う項目では、送る前に確認の段を置く。その確認をもって、消費側の再起動の確認とする。書き込みとは別の再起動の操作は置かない。**」
- 「確認の段には、再起動する消費側と、断たれ得る処理の種類を出す。『同期を促す』の形も、この確認の段とする」
- 「自動の作り直しを配備していない環境では、確認の段と書き込み後の表示で『再起動するまで反映されない』旨を出す。配備の有無を画面が検出することは求めない」
- 「OpenD が消費する項目…確認の段では、書き込みでは再起動しない旨と、手動の再起動が稼働中の処理を断ち再認証を求め得る旨を出す」
- 「供給元が『画面以外』の項目では、再起動の確認を出さない。『確認できない』の項目では、再起動し得る旨として出す」
- フォローアップ 2「SC-22 の実装仕様書の『確認ダイアログは置かない』は、鍵の生成に加えてこの項目を例外とする形に改まる」
- 🔴 **OpenD の項目は「再起動を伴う項目」か。** 書き込みでは再起動しないが、決定 3 は OpenD の項目について「確認の段では…を出す」と書いており、確認の段を置く前提で読める。→ **置く**（反映に手動の再起動が要り、それが処理を断つことを送る前に確かめさせる）。

## 判断

1. **契約の値 `git` は変えない（表示名だけを改める）。** 値は識別子であり表示名ではない。値を変えると `SecretItemSupplySources.Git` の const 値の変更＝契約スナップショットの破壊的変更になり（`check-contract-schema.js` は const 値変更を fail にする）、BFF と画面を同時に替えても得るものが無い。OpenAPI・契約のコメントの**定義**を「同期先が無い（手で作った Secret・配備スクリプト・Git など。画面の表示は『画面以外』）」へ改める。記録は IADR-0460 の追記。
2. **確認の段は `ConfirmDialog`（ユニット共有の確認ダイアログ）で置く。** 土台は `@platform/ui` の `Dialog`（フォーカストラップ・Esc・初期フォーカスは取消）。SC-19 / SC-20 が既に使う部品であり、新しい部品を作らない。
3. **出す条件は「供給元が『画面以外』でない」または「鍵の生成」。** 対象 6 項目はいずれも消費側を持つ（自動 4・OpenD 2）ため、「画面以外」以外の書き込みはすべて確認を通る。表に無い項目も「再起動し得る」で確認を通す（断定しない）。
   - **鍵の生成の確認はこの確認の段へ統合する**（2 度確認させない）。従来のフォーム内の確認（「生成」→「生成して書き込む」）は、ダイアログの「生成して書き込む」になる。供給元が「画面以外」でも生成は確認を通す（鍵が置き換わる）。
   - 生成の文言の「OpenD に登録済みの鍵との対応が失効する」は ADR-0104 決定 3 の前提（登録が要る）に寄りかかった表現で、ADR-0110 決定 2 がその前提を誤りとした。**「OpenD が新しい鍵を読むまで、OpenD と同じ鍵で接続するクライアントの鍵が食い違う」へ改める**（AST の chart は注文執行を Reloader の対象に、OpenD を対象外にしている）。記録は IADR-0456 の追記。
4. **送る前の常設の注記（IADR-0460 決定 2）は確認の段へ移す。** 同じ内容を 2 箇所に置かない。供給元の注記（「画面以外」「確認できない」）はフォームに残す（書く前に効くかどうかを伝えるもので、再起動とは別の関心）。
5. **消費側と断たれ得る処理は画面の語彙（`secretItemVocabulary.ts`）に項目ごとに持つ**（作り直され方の表と同じ場所。IADR-0460 決定 2 の B3）。BFF は変えない。
6. **書き込み後の表示**: 自動の作り直しの項目（と表に無い項目）は「自動再起動を配備していない環境では、再起動するまで反映されない」、OpenD の項目は「OpenD を手動で再起動するまで反映されない」を成功の表示に添える。「画面以外」は従来どおり「反映されない」だけ。

## 受け入れ基準 → 試験（`SecretItemManagementPage.test.tsx`）

| 受け入れ基準 | 試験 |
| --- | --- |
| 一覧・フォーム・書き込み後で「画面以外」が出て「Git」が出ない | T-72 / T-73 の改訂 |
| 供給元が「画面」の自動の項目: 送信で送らずダイアログ（消費側・断たれ得る処理・配備していない環境の一文）。初期フォーカスは取消。取消で送らない・確認で送る。書き込み後に「再起動するまで反映されない」 | 新 T-76 |
| OpenD の項目: 「書き込みでは再起動しない」・手動の再起動のコマンド・再認証。書き込み後に「手動で再起動するまで反映されない」 | T-75 の改訂 |
| 「確認できない」: 「再起動することがある」（断定しない） | T-74 の改訂 |
| 「画面以外」: ダイアログを出さずに送る | T-73 の改訂 |
| 鍵の生成: 生成の確認と再起動の確認が 1 つのダイアログ。「やめる」で送らない | T-65 の改訂 |
| 既存の書き込みの試験はダイアログの確認を経て送る | 既存 5 本の改訂 |

## 母集合（規則 1〜6・9・10）

| 軸 | 引き方 | 結果 | 扱い |
| --- | --- | --- | --- |
| 1. SC-22 の供給元の識別子を持つファイル | `git grep -l -E "supplySource\|SupplySource\|secretSupplySource\|secret-supply\|ExternalSecretPresence" -- . ':!src/ai-stock-trading'` | 32 件（着手時。本仕様書は未追跡で含まない）。うち IADR-0096〜0099・IADR-0103・IADR-0342・IADR-0457 の 7 件と #310 系の仕様書 5 件・`deploy/local/vault/eso/README.md` の計 13 件は「secret-supply」（Vault/ESO の供給の一般語）で当たっただけ | 残る SC-22 の 19 件（IADR-0460・IADR 索引・#1502 の仕様書・BFF 通信仕様書・`openapi.yaml`・契約スナップショット・チャンク予算・画面 3・BFF 4・契約 1・e2e 1・生成物 3）を軸 2 で読む |
| 2. 誤りの側（表示名「Git」） | 軸 1 の 17 件＋ `docs/screens/SC-22_*`・`docs/tests/SC-22_*` に `git grep -n -E "Git\|git"` | 画面 `SecretItemManagementPage.tsx`（バッジ・注記・フォームの警告・書き込み後の警告・コメント）・語彙のコメント・画面の試験・画面仕様書（:40・:55・:82・:85・:96・:142・:185）・テスト仕様書（T-72〜T-74）・`docs/api/BFF_bff-surface.md:183`・`openapi.yaml` の説明 2 箇所と生成物の jsdoc・契約 `SecretItemDto.cs` のコメント | 表示名と定義を直す。**除外**: 契約の値 `git`・`SecretItemSupplySources.Git`・`ExternalSecretPresence.Absent`・BFF の試験（識別子であり表示名ではない。§判断 1）、`Git に置けない秘密情報`（画面の目的の文。供給元ではない）、他の BFF 端点の「Git 経由の公開構成変更」、`bff.schemas.ts` の構成バージョン（GitCommit） |
| 3. 「確認ダイアログは置かない」 | `git grep -n -E "確認ダイアログは置かない\|確認ダイアログを置かない\|確認の段" -- . ':!src/ai-stock-trading' ':!CHANGELOG.md'` | 画面仕様書（§アクション の下）・`SecretItemManagementPage.tsx:44`・IADR-0453 決定 6・IADR-0456 決定 3 の括弧書き・#1411 の確定済み仕様書 | live な 2 つ（画面仕様書・画面のコメント）を改め、IADR-0453・IADR-0456 に日付つき追記。確定済み仕様書は凍結 |
| 4. 送る前の常設の注記（`secret-restart-note`） | `git grep -n "secret-restart-note\|常設の注記"` | 画面・画面の試験・画面仕様書 §消費側の再起動・テスト仕様書 T-74 / T-75・IADR-0460 決定 2 | 確認の段へ移す（§判断 4） |
| 5. 公開鍵 | `git grep -n -i -E "公開鍵\|public.key\|publicKey" -- . ':!src/ai-stock-trading' ':!CHANGELOG.md' ':!*.po'` | IADR-0456:173・IADR-0460:100・#1502 の仕様書・画面仕様書 :187・:198 | 画面仕様書の対応表と未決を「取り下げ」へ。IADR-0456 / IADR-0460 は追記で「取り下げ」を示す。**IADR-0433 は 0 件**（追随不要。§判断 4 の確認） |
| 6. 生成の「登録済みの鍵」 | `git grep -n "登録済みの鍵"`（着手時は画面・IADR・画面仕様書だけを見ていた。変更後に `-- src docs` で引き直して増えた） | 画面の文言・IADR-0456 決定 3・IADR-0453 決定 6 の追記・画面仕様書 §アクション・🔴 **運用 Runbook 2 本**（`docs/operations/secret-rotation-runbook.md:115`「取引ユニットの手順で OpenD 側の登録を合わせてから行う」・`secret-item-console-injection-runbook.md:138`） | 画面・画面仕様書・Runbook 2 本の文言を改め、IADR-0456・IADR-0453 に追記（IADR の過去の本文は凍結）。**Runbook は軸 1〜5 のどれにも掛からなかった**（「供給元」「supplySource」を含まない）—— 軸を変えて初めて出た |
| 7. 本件で新たに誤りになる自分の記述（規則 10） | 変更後に `git grep -n -E "Git\b" -- src/knowledge/frontend/src/features/sc22-secrets docs/screens/SC-22_* docs/tests/SC-22_*` と `git grep -n "secret-supply-git\|secret-restart-note\|confirmingGenerate\|登録済みの鍵" -- src docs` | `Git` は「Git に置けない秘密情報」（目的の文）・「手で作った Secret・配備スクリプト・Git など」（画面以外の例示）・試験の「`Git` が出ない」検査だけ。識別子の残りは 0 件（Runbook 2 件は軸 6 で直した） | 残りは意図どおり |
| 8. 画面の書き込みの手順を述べる運用文書 | `git grep -n "生成して書き込む\|このプロパティを更新する\|再起動されます\|OpenD は自動では再起動" -- docs deploy scripts ':!*.po'` | 画面仕様書・テスト仕様書だけ | 追加の追随なし |

## 検証

- 画面: `vitest run knowledge/frontend/src/features/sc22-secrets` → 19 件合格。赤の確認（実測）: ①確認の段を出さない（`needsWriteConfirmation` を生成だけ真に）→ 書き込みの試験 8 本が失敗。②常に出す → 「画面以外」の試験と 409 の試験の 2 本が失敗（確認を経ずに送るはずの経路）。いずれも戻して緑。
- `pnpm run test:coverage` → 148 ファイル・1780 件合格、しきい値内。`pnpm run typecheck`・`pnpm run lint`（エラー 0。警告 12 は既存）・`pnpm run format:check` → 通過。
- `pnpm run i18n` → 未翻訳 0（ja / en で 27 キー追加・9 キー撤去。en の訳は手で入れた）。`node scripts/check-i18n-catalogs.js` → OK。
- `pnpm run codegen` → 生成物の差分は `openapi.yaml` の説明文の jsdoc だけ（`bff.schemas.ts` 2 行・`secret-items.ts` 1 行）。
- `pnpm run build` → `check-chunk-budget --require` が +3.11 kB を報告 → `--update` で床を 612,346 B へ（`$comment_initialTotalBytes_20260926_1523_sc22-restart-confirm` に記録）。`check-static-egress --require src/platform/frontend/dist` → OK。
- `dotnet build src/platform/backend/backend.slnx` → 警告 0・エラー 0。`dotnet format … --verify-no-changes` → 差分なし。`Platform.Bff.Tests` の `SecretItem` 系 → 111 件合格（BFF は変えていない）。
- `check-contract-schema`（`SecretItemSupplySources.Git` の値は不変）・`check-openapi-dto-drift` → OK。

## ［2026-09-26 追記 / #1530 の監査］指摘の反映

- ①「画面以外」の項目で鍵を生成すると、同期先が無いので新しい鍵は再起動しても OpenD に届かない。生成の本文を供給元で書き分け、「画面以外」では「OpenD にもクライアントにも届きません（再起動しても届きません）」とし、「読み込むまで食い違う」とは書かない（新 T-77）。
- ②生成の確定ボタンを破壊的（`destructive`）にした。値の書き込みは破壊的としない（T-76 に陰性の検査を足した）。
- ③初期フォーカスが取消にあることは T-76 が既に固定していた。生成の試験（T-65）と T-77 にも同じ検査を足した。
- 検証: `vitest run knowledge/frontend/src/features/sc22-secrets` → 20 件合格。赤の確認: 書き分けを外し `destructive` を外すと T-65・T-77 の 2 本が失敗。`pnpm run i18n` → 1 キー追加（en は手で訳した）・未翻訳 0。`pnpm run build` → `check-chunk-budget` +593 B を `--update` で床へ（612,939 B）。
- develop の取り込み（#1513・#1524）で `docs/operations/secret-rotation-runbook.md` の trace ブロックが衝突した。両側の ID を合わせて解決した。
