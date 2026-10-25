---
title: 作業仕様書 — SC-17 のロールを計画の表示名で出す
type: spec
status: done
related_ids: [SC-17, FR-05, FR-09, UC-05, ADR-0026, IADR-0030]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（§SC-17 主要素・アクセス制御。origin/main b06dafe の 763 行・795 行）
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md（§決定「ロールは 利用者／管理者／運用者／システム管理者 の 4 種」。72 行）
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md（「4 ロールへの権限分離は後続フェーズで導入する」。191 行）
issue: "#2214"
---

# 作業仕様書 — SC-17 のロールを計画の表示名で出す

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。見つけた PR は #2213（E-15）。
> オーケストレータの指示: 機密区分の `confidentialityLabel`（#2198）と同じ作法で、Lingui の写像を 1 か所に置く。
> 値集合の外は生値のまま。送る値は識別子のまま。直す前に E2E の赤を確かめる。

## 受け入れ基準

- [x] SC-17 の一覧の「ロール」列・ロールフィルタの選択肢・ロール割当のチェックボックスが、計画の表示名で出る。
- [x] 値集合の外の識別子は生値のまま出る（既知の表示名へ丸めない）。
- [x] 送る値（`PUT /admin/users/{id}/roles` の本文・フィルタの値）は識別子のまま。
- [x] 写像は 1 か所（`@foundation/i18n` の `roleLabel`）。ja / en のカタログに載る。
- [x] 単体テストを付け、E2E（`e2e/sc17-users.smoke.spec.ts`）の期待を表示名に更新する。直す前に赤を確かめる。
- [x] chunk の床を実測で更新し、理由を `$comment` に書く。

## 表示名の正（出典）

| 識別子 | 表示名 | 出典 |
| --- | --- | --- |
| `platform-admin` | システム管理者 | 05_screens §SC-17 アクセス制御「システム管理者ロール限定（実装のロールは `platform-admin`。2026-10-23 に明記）」。同 §SC-17 個人資料の保存容量「操作できるのは本画面と同じシステム管理者（`platform-admin`）」 |
| `platform-operator` | 運用者 | IADR-0030「運用者ロールは platform-operator を新設し」 |
| （無い） | 利用者 | ADR-0026 §決定 の 4 種の 1 つ。**realm ロールが無い**（`deploy/keycloak/microservices-platform-realm.json` の realm ロールに該当なし） |
| （無い） | 管理者 | 同上。計画は「管理者」と「システム管理者」をどの識別子に当てるかを定めていない（05_screens 145 行は SC-22 について「管理者」が運用者・システム管理者のどちらに当たるかを「仕様未確定」と記録している）。07_abac-attribute-model は 4 ロールへの分離を後続フェーズとする |

用語集（`docs/glossary.md`）にはロールの項目が無い（2026-10-24 確認。`緊急アクセス` の説明に `platform-admin` が出るだけ）。

### 決めたこと

1. **表に載せるのは計画と実装の両方で対応が定まった 2 つだけ**（`platform-admin`→システム管理者、`platform-operator`→運用者）。
   利用者・管理者の識別子を先回りで作らない —— #2213 の E2E が使った `platform-user`・`knowledge-admin` は
   試験のための架空の値であり、realm に無い。表に載せると「管理者＝`knowledge-admin`」を実装が決めてしまう。
   ロールが足された日に表へ 1 行足す。
2. **置き場は `@foundation/i18n`**（`src/platform/frontend/src/lib/i18n/roles.ts`）。識別子の定数（`PlatformRole`）と
   ヘッダのロールタグが foundation にあり、ロールの語彙は基盤のものである。機密区分の写像と同じ公開面から引かせる。
3. **ヘッダのロールタグ（`app/Layout.tsx` の「システム管理」「運用」）は変えない。** hi-fi モックのタグの字面（略記）であり、
   ロールの名前とは別の語である。統合すると、モックに合わせたタグの見え方が変わる。
4. **E2E の割当可能ロールの束を realm の値域に寄せた**（`platform-admin`・`platform-operator`・`wiki-editor`。
   `InMemoryIdentityAdminClient` の値域と同じ）。`wiki-editor` は「表に無い値は生値」を E2E で示す。

## 母集合（規則 9: ロールを表示する箇所を全画面で走査した）

走査: `git grep -nE "roles\b|\brole\b|Role" -- '*/frontend/src/**/*.ts{,x}' ':!*.test.*'`（生成物を除く）と、
`git grep -n "platform-admin\|platform-operator\|wiki-editor"`（frontend の src・e2e）。

| 箇所 | ロールを画面に出すか | 扱い |
| --- | --- | --- |
| SC-17 一覧「ロール」列（`UserAccountManagementPage.tsx` の `roles` 列） | 出す（識別子のまま） | **直す** |
| SC-17 ロールフィルタの `<option>` | 出す（識別子のまま） | **直す**（`value` は識別子のまま） |
| SC-17 ロール割当のチェックボックス | 出す（識別子のまま） | **直す**（`toggleRole(role)` は識別子のまま） |
| 共通シェルのロールタグ（`app/Layout.tsx` `representativeRoleLabel`） | 出す（既に表示名。モックの略記「システム管理」「運用」） | 対象外（決めたこと 3） |
| 各画面のルート定義の `requiresAnyRole`（SC-05〜07・09〜12・17・22） | 出さない（ガードの判定だけ） | 対象外 |
| SC-19 緊急アクセスの管理者の指名・監査ログ | 利用者を出す。ロール名は出さない | 対象外 |
| ai-stock-trading の frontend（submodule） | `trading-owner` はガードの判定だけで、画面に出さない | 対象外 |
| E2E: `sc17-users.smoke.spec.ts` | 期待値がロールの字面を引く | **更新する** |
| E2E: `a11y.smoke.spec.ts`・`session-revocation.smoke.spec.ts`・`sc22-secrets.smoke.spec.ts` | `platform-user` 等をセッションや割当可能ロールの束に使うが、ロールの字面を期待値にしていない | 変えない（axe の走査は表示名に変わっても同じ構造を見る） |

## 実装

- `src/platform/frontend/src/lib/i18n/roles.ts`: `roleLabel(value, translate)`。`Object.hasOwn` で表を引き、無ければ生値。
  `msg({ message, context: 'ロール' })`。
- `src/platform/frontend/src/lib/i18n/index.ts`: `roleLabel` を再公開する。
- `UserAccountManagementPage.tsx`: 一覧・フィルタ・チェックボックスの 3 か所で `roleLabel(role, (d) => i18n._(d))`。
- カタログ: `pnpm run i18n`（en は System administrator / Operator）。

## テスト

- 単体 `lib/i18n/roles.test.ts`: 2 値の表示名、値集合の外（`wiki-editor`・`trading-owner`・`platform-user`・空・`toString`・大文字違い）は生値、en の訳。
- 単体 `UserAccountManagementPage.test.tsx`: 一覧が表示名で識別子を出さない／フィルタの option は表示名で value は識別子／
  チェックボックスを表示名で引いて送る本文は識別子／表に無い `wiki-editor` は 3 か所で生値。
- E2E `sc17-users.smoke.spec.ts`: 一覧の列とフィルタの選択肢、M-106 のチェックボックスの並び（システム管理者・運用者・wiki-editor）と送る本文。

### 赤の確認（直す前）

- 単体: 画面の修正だけ戻すと `UserAccountManagementPage.test.tsx` の 5 件が落ちた（21 件中）。
- E2E: 同じく戻してビルドすると 6 件中 2 件（管理者で開く本体・M-106）が落ちた。修正を戻すと 6 件とも通った。

## chunk の床

初期ロード 644,822 → 644,930 B（+108 B）。増分は初期チャンクに載る翻訳カタログへ足した 2 語 × 2 ロケール。
写像は SC-17 の遅延チャンク（`UserAccountManagementPage-*.js`）にある（ビルド成果物で確かめた）。

## 計画への環流

不要と判断した。計画は 4 種への分離を ADR-0026 のフォローアップ・07_abac-attribute-model の後続フェーズとして既に持っており、
本件は既に定まった 2 つの対応を表示へ写しただけである。「管理者」の識別子が未定であることは計画側の既知の未決である。
