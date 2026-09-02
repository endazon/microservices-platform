---
title: 作業仕様書 — 画面 4 件の Playwright スモークを起こし、UC-11 の導線を E2E で実際に踏む（#1099）
type: spec
status: done
related_ids:
  - SC-12
  - SC-17
  - SC-19
  - SC-20
  - UC-05
  - UC-09
  - UC-11
  - FR-16
  - FR-19
  - FR-20
  - NFR
  - ADR-0031
  - ADR-0037
  - ADR-0046
  - IADR-0330
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 作業仕様書: 画面スモークの欠落 4 件と UC-11 の E2E 実走（#1099）

## 起点となる計画書（トレーサビリティ）

- ユースケース: `03_usecases/01_usecases.md` §UC-11（基本フロー 1〜5・代替フロー・例外フロー）を逐語で読んだ。
- 画面: `05_screens/01_screens.md` §SC-12 / §SC-17 / §SC-19 / §SC-20（主要素・固定文言・
  **描いてはいけないもの**・入力バリデーション）を逐語で読んだ。
- 関連 ADR: ADR-0031（SPA スタック）・ADR-0032（BFF セッション）・ADR-0037（Obsidian 同期）・
  ADR-0046（個人資料は Wiki.js へ同期しない＝本文編集の導線を持たない）。

## 母集合（規則 9・10 に従い、自分で引いた）

🔴 **issue 本文の「4 画面」を転記していない。** 以下はすべて本作業で実測した。

### 引き方（issue とは別の定義で引いた）

issue は `*Page.tsx` の有無で 17 件を数えている。私は **`check-route-manifest.js` と同じ定義**
（`features/sc<NN>-*/` 配下で `createRoute({ … path: … })` を宣言するもの＝ SPA ルートを持つ画面）
で引いた。**同じ検査器が「画面」と呼ぶ集合と一致させないと、後で足す検査器が別の母集合を見る。**

```console
$ node <scratch>/pop.cjs   # collectScreens(collectFeatureFiles()) を呼ぶだけの 30 行
screens_with_routes=17
SC-01 /ask        SC-02 /search   SC-03 /docs/$id   SC-04 /wiki       SC-05 /admin/documents
SC-06 /admin/sources  SC-07 /admin/conversions  SC-08 /analyze  SC-09 /admin/abac
SC-10 /admin/ops  SC-11 /admin/config-viewer    SC-12 /admin/mcp-clients
SC-17 /admin/users    SC-18 /graph  SC-19 /my/notes   SC-20 /my/obsidian   SC-21 /ai-suggestions

spec_files=15 sc_specs=13     # bundle-splitting / login は SC 番号を持たない
--- screens WITHOUT a sc*.smoke.spec.ts ---
SC-12 SC-17 SC-19 SC-20   (missing=4)
--- specs with no screen ---
(none)
```

**結論: issue 本文の数え（4 件・SC-12 / SC-17 / SC-19 / SC-20）は正しい。** 直近の #1106 では
issue の実測が誤っていたが、本件は同型ではない。**別の定義で引いても同じ 4 件に落ちる**ことまで確かめた。

### 除外の理由

- `bundle-splitting.smoke.spec.ts` / `login.smoke.spec.ts` は画面 1 枚に対応しないため SC 番号を持たない。
  母集合（画面 ↔ spec の対）から外す。
- **SC-04 は除外しない。** ルート（`/wiki`）を宣言しており spec も既にある。
  `PLANNED_ROUTES` から除外されているのは「計画のルートパス表に載らない」という別の理由であり、
  spec の要否とは別の軸である（本作業で足す検査器も `PLANNED_ROUTES` ではなく**画面**を母集合にする）。
- `src/ai-stock-trading`（submodule）は別プロジェクト。`excludedUnits` により走査対象外。

### 追随の母集合（規則 10 —— 本変更で新たに誤りになる自分の記述）

本作業は「**認証済みの画面を E2E で実走できない**」という既存の記述を**実測で覆す**（後述）。
覆した後に誤りになる記述を、誤りの側の文字列で全走査した。

```console
$ git grep -n "認証済みの画面を\|認証済みの導線を\|認証済みの遅延ルート\|実走できない" -- ':!src/ai-stock-trading'
```

| 置き場所 | 件数 | 本 PR での扱い |
| --- | --- | --- |
| `src/platform/frontend/e2e/*.smoke.spec.ts`（既存 8 本の理由コメント） | 8 | **宣言ファイル領域内。本 PR で直す** |
| `docs/tests/SC-12_mcp-client-management.md` §「ブラウザ E2E を置いていない」 | 1 | **本 PR の対象画面。直す** |
| `docs/tests/SC-19` / `SC-20`（E2E 行が無い） | 2 | **本 PR の対象画面。E2E 行を足す** |
| `docs/tests/SC-01_search-chat.md` §E2E の限界 / `docs/tests/SC-21_ai-suggestion-list.md` §E2E の限界 | 2 | **領域外。追随 issue を起票**（#1139） |
| `docs/how-to/session-handoff.md` | 2 | **領域外。同 issue** |
| `src/knowledge/frontend/src/features/{adminFlow,searchFlow}.test.tsx` | 2 | **領域外（knowledge ユニット）。同 issue** |
| `.ai-context/specs/*` / `.ai-context/adr/*` | 14 | **凍結記録。書き換えない**（IADR-0166 決定 2） |
| `.ai-context/specs/…` のうち dotnet / Docker 不在を述べるもの | — | **別事象**（本変更と無関係。誤りにならない） |

## 目的・背景

#452 §退行防止 は「Playwright E2E を主要 UC（**UC-01〜05, 10, 11**）の導線で整備する」と定める。
現状 **UC-11 は E2E で 1 度も踏まれていない**（SC-19 / SC-20 に spec が無い）。

### 🔴 「同じ形を 4 本足す」では受け入れ基準を満たせない（本作業の核心）

issue の受け入れ基準は 2 つが**互いに矛盾している**。

- 基準②「既存 15 本と**同じ形**である（未認証リダイレクト。認証後の画面を踏もうとしていない）」
- 基準④「**ルート定義を意図的に壊すと、その spec が落ちる**ことを変異試験で確かめる」

**②を満たすと④は原理的に満たせない。** `catchAllRoute` は `shellRoute`（＝ `RequireAuth`）の配下に
居るため、**ルートが無くても未認証なら同じく `/login` へ行く**。#918 が改名の変異を当てて
**落ちたテスト 0 件**を実測し、`check-route-manifest.js` が判定 2 でその主張の再混入を禁じている。

### 実測: 認証済みの画面は Playwright で踏める（既存の前提は誤り）

既存 spec と `docs/tests/` は「セッションは BFF（Keycloak）との往復で成立し、プレビューには
どちらも無いため認証済みの導線を実走できない」と書く。**これは実測せずに書かれた推定である。**

実測（本作業のスパイク。`e2e/spike.smoke.spec.ts` を書いて捨てた）:

- SPA が出す HTTP は `foundation/api/apiClient` の 1 箇所に収束し、宛先は**同一オリジンの相対パス
  `/bff/*`**（`bffBaseUrl`。絶対 URL は `assertSameOriginBffBaseUrl` が禁じる）。
- 認証状態の一次情報は **`GET /bff/auth/me` の応答だけ**である（`AuthProvider`）。
  **ブラウザは Cookie を読まない**（HttpOnly）ので、**応答を差し替えればセッションは成立する**。
- したがって **`page.route('**/bff/**')` で応答を与えるだけで認証済み画面が描画される。**
  Keycloak も BFF も要らない。実測した 4 画面すべてで、共通シェル・左ナビ・パンくず・
  **ルート単位の遅延チャンク**（`assets/PrivateNotesPage-*.js` 等）まで実際に読み込まれた。

| 画面 | 実測した BFF 呼び出し | 描画 |
| --- | --- | --- |
| `/my/notes` | `auth/me`・`notifications?limit=50`・`private-notes` | ○ |
| `/my/obsidian` | `auth/me`・`notifications`・`private-notes/devices` | ○ |
| `/admin/users` | `auth/me`・`notifications`・`admin/users`・`admin/users/assignable-roles`・`admin/authz/attributes` | ○ |
| `/admin/mcp-clients` | 上記 ＋ `admin/mcp-clients`・`admin/mcp-clients/tools` | ○（`tools` の応答型を誤ると実際にクラッシュした＝**本当に描いている**） |

**この経路は #466（実 BFF ＋ Keycloak の真の E2E）を侵さない。** 後段を 1 つも起動せず、
**契約（`docs/api/openapi.yaml` 由来の生成型）に沿った応答をネットワーク層で与えるだけ**である。

## 対象範囲

### 作る

1. `src/platform/frontend/e2e/support/bffSession.ts` —— BFF 応答スタブの共通土台。
2. `sc12-mcp-clients.smoke.spec.ts` / `sc17-users.smoke.spec.ts` /
   `sc19-private-notes.smoke.spec.ts` / `sc20-obsidian-settings.smoke.spec.ts`。
3. `check-route-manifest.js` に**判定 3**（画面 ↔ e2e spec の対応）を足す。
4. `.ai-context/adr/IADR-0330`（認証済み E2E をネットワーク層のスタブで成立させる決定）。
5. `docs/tests/SC-12 / SC-17 / SC-19 / SC-20` の追随。

### 作らない

- 実 BFF / Keycloak を起動する E2E（**#466 の射程**）。
- 既存 15 本の spec の**構造**の変更（理由コメントの是正だけ行う）。
- `docs/tests/SC-01` / `SC-21` / `session-handoff.md` / knowledge ユニットの test の追随（追随 issue）。

## 各 spec の設計（陽性対照と陰性対照を必ず対で置く）

**すべての spec に「未認証 → /login」を 1 本残す**（既存 15 本との連続性・基準②の趣旨）。
そのうえで**セッションを与えた本体**を置く。

| spec | 陽性対照（起きるべきことが起きる） | 陰性対照（起きてはならないことが起きない） | 起点 |
| --- | --- | --- | --- |
| SC-19 | 一般利用者で `/my/notes` が描かれ、固定文言「業務関連資料として扱われます」と `👤 個人資料（自分のみ）` が出る | **本文の編集手段を置かない**（`textarea` / `contenteditable` が 0 件。ADR-0046 D-02）。他人の資料の件数示唆が出ない | SC-19 主要素 4・描いてはいけないもの |
| SC-19 | UC-11 基本フロー 1: 作成すると一覧に現れ、**露出 3 トグルがすべて OFF** | 作成要求の本文に**本文フィールドが載らない**（`title` / `vaultPath` だけ） | UC-11 基本フロー 1 / 4 |
| SC-19 | UC-11 例外フロー: 100% で**新規作成が無効化**され固定文言が出る | **既存資料の更新（露出トグル）は無効化されない**（＝「全部止める」実装と区別できる） | SC-19 §保存容量 100% の非対称 |
| SC-19 | 論理削除の確認に「削除しても容量は空きません」が出る | — | SC-19 §削除の確認ダイアログ |
| SC-20 | UC-11 基本フロー 2: 「トークンを発行する」で**平文が一度だけ**出る | **一覧の再取得後に平文が消える**／`GET devices` の応答に平文が無い | SC-20 主要素 2 |
| SC-20 | 固定文言「同期できるのは、あなたが作成した個人資料のみです」 | **管理者承認のステップが無い**（「承認」を含む要素 0 件） | SC-20 描いてはいけないもの |
| SC-12 | `platform-admin` で「MCP クライアント登録管理」が描かれ、左ナビに「MCP管理」が出る | **一般利用者では NotFound**（見出しも左ナビ項目も出ない＝存在秘匿） | SC-12 ロール限定 / IADR-0009 |
| SC-17 | `platform-admin` で「ユーザーアカウント管理」が描かれ、左ナビに「ユーザー管理」が出る | **一般利用者では NotFound** | SC-17 ロール限定 |

### 変異試験（基準④）

陽性対照が**画面固有の見出し**を待つため、**ルートの path を変えると catch-all が
`NotFound` を描き、その待ちが落ちる**。4 本すべてで実測する（残渣 0 まで戻す）。

## 検査器（基準⑥ —— 「同型の事故が 2 回起きたら」に照らす）

同型（**列挙の伸ばし忘れ**）は既に **#1078**（lingui `files`）・**#1066**（feature 分割）で 2 回起きており、
本件が 3 回目である。よって **足す**。`check-route-manifest.js` の**判定 3**として、
「ルートを宣言する画面には `e2e/sc<NN>-*.smoke.spec.ts` が 1 本ある」を検査する。
除外は**理由の文字列とともにしか宣言できない**（判定 1 と同じ作法）。0 件走査は fail。

## 検証

`cd src && pnpm run lint && typecheck && test && build && format:check` ／
`node scripts/check-route-manifest.js` ほか文書・トレーサビリティの検査器一式 ／
`pnpm exec playwright test`（**実走**）／変異試験 4 件。

## リスクと限界

- 🔴 **スタブは契約の写しであって後段ではない。** 応答形が openapi と食い違えば、この E2E は
  「食い違ったまま緑」になる。防いでいるのは**生成型を import して組む**ことだけで、
  実応答との一致は **#466** と後段のテストが持つ。**本 spec で「後段まで固定した」とは書かない。**
- Docker 不在（containerd）につきバックエンド統合テストは本作業の対象外。
