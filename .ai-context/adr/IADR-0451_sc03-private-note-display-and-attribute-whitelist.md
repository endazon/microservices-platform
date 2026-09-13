---
title: IADR-0451 SC-03 の個人資料は「供給が返るかどうか」で描き、属性・タグパネルは既知キーの whitelist に閉じる — 共有する語彙はユニットの lib/ に置く
type: impl-adr
status: Accepted
related_ids: [FR-19, UC-11, SC-03, SC-19, ADR-0036, ADR-0098, ADR-0100, ADR-0101, ADR-0102, IADR-0444, IADR-0445, IADR-0450, IADR-0451]
author: Claude
created: 2026-09-13
updated: 2026-09-13
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0102_private-note-display-to-non-owner-viewers.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - ../specs/20260913_1455_sc03-private-note-display.md
---

# IADR-0451: SC-03 の個人資料の描き方と、属性・タグパネルの whitelist 化

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-09-13
- 決定者: Claude（実装）。裁定は利用者（planning#628 → 計画 ADR-0102）

## 起点・関連

- 関連する計画書 ID: FR-19・UC-11・SC-03（§個人資料の表示・§主要素）・SC-19（供給元）／
  ADR-0102 決定 1〜5／ADR-0101 決定 1・2（所有者以外に `sharedWith` を返さない）／
  ADR-0098 決定 1（識別子を出さない）／ADR-0100（表示名の到達範囲）／ADR-0036 D-06・D-08・D-09
- 関連 issue: #1455（本作業）、planning#628（裁定依頼）、#1456 / #1457（SC-18 側のラベル是正）
- 関連 IADR: `IADR-0450`（応答から共有先の写しを落とす）・`IADR-0444`（公開範囲 3 状態の契約）・
  `IADR-0445`（共有先の UI と利用者検索の面）
- 作業仕様書: [`20260913_1455_sc03-private-note-display.md`](../specs/20260913_1455_sc03-private-note-display.md)

## コンテキストと課題

計画 ADR-0102 は「所有者以外の閲覧者へ何を描くか」を定めた。実装するとき決めることが 3 つある。

1. **画面はどうやって「所有者か」を知るのか。** 認可の判定はサーバにあり（ADR-0036 D-05 の束縛は認可サービス、
   `IADR-0450` は BFF の分岐一致で所有者を決める）、**画面に 2 本目の判定軸を作りたくない。**
2. **公開範囲の 3 状態をどこから取るのか。** 応答の `sharedWith` は所有者にしか返らないうえ、
   🔴 **指定先の種別を運ばないので個人指定とグループ指定を区別できない**（実測）。
3. **共有する語彙・フックをどこに置くのか。** 表示名の解決も公開範囲の語彙も SC-19 が既に持っているが、
   🔴 **feature 間の import は境界規則（`import/no-restricted-paths`）が error で止める。**

## 検討した選択肢

| 論点 | 案 | 判定 |
| --- | --- | --- |
| 所有者の判定 | **セッションの利用者名と `owner` 属性を突き合わせ、表示の分岐にだけ使う** | **採る**（下記 決定 1） |
| 同上 | 応答の `sharedWith` の null / 非 null を所有者の代理シグナルにする | 採らない。**項目の有無という間接的な結合**であり、契約の書き方が変わると静かに壊れる |
| 同上 | 画面から所有者を問い合わせる口を新設する | 採らない。**契約の追加**が要り、認可の判定軸が 2 本になる |
| 公開範囲の供給 | **所有者だけが読める一覧の口（`GET /bff/private-notes`）から、文書 ID で 1 件を選ぶ** | **採る**（決定 2）。3 状態は**サーバが導出した値**であり、SC-19 と同じ 1 点である |
| 同上 | `GET /bff/private-notes/{id}` を新設して 1 件だけ引く | 採らない。**契約の追加**に見合わない（一覧は SC-19 と同じ query key で共有でき、追加の往復が無い） |
| 同上 | `sharedWith` の中身から画面で 3 状態を導く | 採らない。**種別を運ばないので導けない**うえ、導出点が 2 つになる |
| 共有物の置き場 | **ユニットの `lib/`（`lib/users` / `lib/private-notes` / `lib/abac`）へ移し、公開面を `index.ts` 1 枚にする** | **採る**（決定 4）。`lib/abac` / `lib/scope-filter` と同じ形 |
| 同上 | SC-03 が同じ実装を書き写す | 採らない。**表示名の解決も 3 状態の写像も 2 か所に増える** |
| 属性パネル | **既知キーの whitelist（`ATTRIBUTE_LABELS` が唯一の値域）** | **採る**（決定 3） |
| 同上 | `owner` / `doc_scope` / 露出 3 トグルだけを blacklist で落とす | 採らない。**属性が増えるたびに画面の統制が自動で緩む**（計画 ADR-0102 §理由 が退けた向き） |

## 決定

### 決定 1: 所有者かどうかは**表示の分岐にだけ**使い、可視性はサーバに委ねる

`attributes.owner` とセッションの利用者名（`useAuth().user.name` ＝ `/bff/auth/me` の `preferred_username`）を
突き合わせ、**ラベルの括弧書きと、公開範囲を問い合わせるかどうかの門**にだけ使う。

🔴 **これは認可の判定ではない。** 公開範囲の値そのものは**所有者だけが読める口**から来るため、
**門が誤っても他人の公開範囲は描けない**（多層。サーバが最終権限）。

### 決定 2: 公開範囲は所有者専用の口から取り、**画面で導出し直さない**

`usePrivateNoteVisibility(documentId, enabled)`（`lib/private-notes`）が**生成フックと同じ query key**で
一覧を引き、`PrivateNoteDto.id === documentId` の 1 件を `select` で選ぶ（`PrivateNoteDto.id` は**文書 ID** である）。
**3 状態はサーバが導出した値をそのまま描く** —— SC-19 と導出点を 2 つにしない。
query key を共有するため、**SC-19 を開いた後の SC-03 は追加の往復を起こさない。**

### 決定 3: 属性・タグパネルは**既知キーの whitelist** に閉じる

`orderedAttributes` は `ATTRIBUTE_LABELS`（`confidentiality` / `department`）に載るキーだけを返す。
**未知のキーは描かない。** 落ちた値は**それぞれの持ち場**が描く —— 所有者は個人資料の欄、
個人資料であることは 👤 のラベル、露出 3 トグルは SC-19 である。

🔴 **blacklist にしない。** 3 キーを名指しで落とす形は、**次に属性が増えたときに黙って画面へ出る**。
whitelist なら「出したい属性にはラベルを与える」という明示の判断が要る。

### 決定 4: 2 画面が要る語彙とフックは**ユニットの `lib/`** へ移す

- `useResolvedUsers`（利用者名 → 表示名）: `features/sc19-private-notes/api/useUserLookup.ts` → **`lib/users/`**
- 公開範囲の語彙（`VisibilityKey` / `visibilityKeyOf` / `VISIBILITY_TONES`）: `features/sc19-private-notes/types/noteBadges.ts` → **`lib/private-notes/`**
- 文書スコープの語彙（`DOC_SCOPE_KEY` / `DOC_SCOPE_PRIVATE_NOTE` / `isPrivateNote`）: **`lib/abac/` に新設**

**検索（`useUserLookup`）と同期状態の語彙は SC-19 だけの持ち物なので feature に残す。**
公開面は各ディレクトリの `index.ts` 1 枚に閉じる（`lib/abac` の前例）。

### 決定 5: 表示名を引けないときは**利用者名へフォールバックしない**

「（不明な利用者）」と描く（計画 ADR-0102 決定 3）。**フォールバックを許すと「識別子を出さない」統制が
失敗時にだけ破れる。** 無効化済み（退職者）の利用者は `resolve` が返すため、30 日の窓で読む管理者にも表示名が出る。

### 決定 6: 契約・バックエンドは変更しない

供給はすべて既存の口で足りる（`GET /bff/documents/{id}` の属性・`GET /bff/private-notes`・`POST /bff/users/resolve`）。

## 結果

- **良い影響**:
  - 所有者以外が個人資料から読めるのは「誰の資料か」までで、**公開範囲・共有先・件数は読めない**。
  - **属性が増えても画面の統制が自動では緩まない**（whitelist）。
  - 表示名の解決と公開範囲の語彙が**ユニットに 1 つ**になり、SC-19 と SC-03 で食い違わない。
- **悪い影響 / 残余リスク**:
  - **所有者の判定に `preferred_username` を使う。** IdP 側で `preferred_username` と文書属性 `owner` の
    名前空間がずれると、**所有者に「（自分のみ）」が付かず、公開範囲も出ない**（安全側の壊れ方であり、
    他人へ漏れる向きには壊れない）。
  - SC-03 で所有者が公開範囲を見るとき、**一覧の応答を 1 本取る**（SC-19 と同じキーなのでキャッシュを共有する）。
    資料が非常に多い利用者では応答が大きい。**1 件だけ引く口が要るほどになったら契約の追加を検討する。**
  - **`lib/` へ移した 3 つは、SC-19 のテストが引き続き規則を固定している**（`noteBadges.test.ts` は
    移設後も同じ規則を検査する）。移設で検査が薄くなってはいない。
