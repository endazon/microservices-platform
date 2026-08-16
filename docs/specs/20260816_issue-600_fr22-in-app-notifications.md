---
title: 作業仕様書 FR-22 利用者本人への通知 — 実装 ADR の確定と、アプリ内通知の契約・受け皿の先行実装（#600）
type: spec
status: in-progress
related_ids:
  - FR-22
  - FR-19
  - FR-20
  - UC-11
  - ADR-0037
  - ADR-0045
  - ADR-0046
  - IADR-0116
  - IADR-0119
  - IADR-0121
  - IADR-0124
  - IADR-0125
  - IADR-0131
  - IADR-0132
  - IADR-0135
  - IADR-0141
  - IADR-0142
  - IADR-0197
  - IADR-0215
author: Claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/06_technical/02_service-decomposition.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md"
related_specs:
  - ../adr/IADR-0215_notification-service-and-in-app-delivery.md
  - ../functional/FR-22_user-notifications.md
  - ../tests/FR-22_user-notifications.md
  - ../api/BFF_notifications.md
  - ../api/BFF_bff-surface.md
---

# 仕様書: FR-22 利用者本人への通知（#600）

> **この作業では issue #600 を閉じない。** 理由は §受け入れ基準 と §保留・先送り に書く。
> PR 本文は `Refs #600` とし `Closes` にしない。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-22**（利用者本人への通知。優先度 Should）／発火源として **FR-19**（個人資料・保存容量）・**FR-20**（Obsidian 同期トークン）
- ユースケース（UC）: **UC-11**（自分の資料を作成・管理し、公開範囲を自ら設定する）例外フロー
- 画面（SC）: **共通シェル**（`05_screens` §共通シェル）。**FR-22 に固有の SC 番号は計画に無い**——通知はアプリシェル横断の要素であり、画面 1 枚に閉じない
- 関連 ADR: **ADR-0037** 決定 6（削除通知 3 段構え）／決定 17（80% / 95% 警告）／決定 18（同期トークン期限予告）、**ADR-0045** 決定 3（送信上限を通知の設計上限として扱う）、**ADR-0046**（個人資料は Wiki.js へ同期しない）
- 実装 ADR: **[IADR-0215](../adr/IADR-0215_notification-service-and-in-app-delivery.md)**（本作業で起案）
- 計画書リンク:
  [02_requirements/01_requirements.md](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) FR-22 ／
  [03_usecases/01_usecases.md](../../planning/projects/microservices-platform/03_usecases/01_usecases.md) UC-11 ／
  [07_adr/ADR-0045](../../planning/projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md)

## 母集合（是正・追随の対象をどう引いたか）

**規則の正本**は [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) 規則 1〜8 と
[`.claude/rules/traceability.repo.md`](../../.claude/rules/traceability.repo.md) 規則 9・10 である。
**記憶で「追随する文書」を挙げず、誤りの側の文字列で全文書を走査してから挙げた**（規則 1・9）。

- **走査基準コミット**: `98182bcd`（`origin/develop`。本ブランチ `feat/fr-22-in-app-notifications` の分岐元）
- **走査範囲**: `git ls-files`（追跡下の全ファイル。**拡張子で絞らない**＝規則 3）から
  `planning/`（submodule・別リポの正本）と `src/ai-stock-trading/`（別プロジェクトの submodule・[[IADR-0120]]）を除く。
  **除外はパスのみで行い、行フィルタ（`grep -i` の二段掛け）は使っていない**（規則 4）。

### 走査した軸と件数（着手前・`98182bcd` 時点）

| # | 軸（検索語） | 意図 | ファイル数 | 追随が要るもの |
| ---: | --- | --- | ---: | --- |
| 1 | `FR-22` | 新設 ID を既に引いている文書 | **13** | **0**（後述） |
| 2 | `(^\|[^I])ADR-0045` | 計画 ADR（メール基盤）の裸参照。`IADR-0045` の偽陽性を除くため直前 1 文字で分離 | **12** | 0 |
| 3 | `[Nn]otification\|[Nn]otify` | **英語側**。既存の「通知」実装と衝突しないか | **33** | **1**（`foundation/ui/notifications.tsx`） |
| 4 | `smtp\|postfix\|sendgrid\|mailpit` | メール基盤の不在を述べている記述 | **5** | 0 |
| 5 | `IADR-0215` | 採番の重複（先着尊重） | **0** | —（空き番号であることの確認） |

**軸を 1 本で終わらせていない**（規則 5）。軸 1（ID）だけでは既存の通知 UI（軸 3）に当たらず、
軸 3 だけでは ID レンジの記録（軸 1）に当たらない。実際、**追随が要る 1 件は軸 3 でしか出ない**。

### 除外したものと、その理由（規則 6）

| 除外したもの | 件数 | 理由 |
| --- | ---: | --- |
| `docs/specs/` の確定済み作業仕様書（`status: done`） | 軸 1 で 8 件 | **確定済みの仕様書は書き換えない**（`.claude/rules/traceability.repo.md`「記録の改竄」）。うち `20260807_issue-599_planning-pin-fr22.md` は「FR-22 の実装は本作業の対象外（→ #600）」と申し送った側であり、**本作業がその申し送りを消化する側**である |
| `docs/how-to/*-annex.md`（`cross-project-id-refs` / `plan-id-range-history`） | 2 | **ID レンジ `FR-01..22` は本作業で動かない**（FR-22 は既に取り込み済み）。誤りにならない |
| `docs/adr/IADR-0116` | 1 | 同上（レンジの転記の話であり、実装の有無を述べていない） |
| `feedback/20260811_*.md` | 1 | 当たったのは `NFR-22`（別 ID）。**軸 1 の偽陽性**である |
| `scripts/check-test-traceability.js` / `scripts/scripts.repo.test.js` | 2 | `FR-22` を**レンジ上端の固定値**として持つ。上端は動かない |
| `LICENSE` / `deploy/local/infra/inotify-*` / `IADR-0100` / `k8s-local-up*` | 軸 3 で 6 件 | **`inotify`（Linux のファイル監視）による偽陽性**。`notify` の部分一致である |
| `.github/workflows/*.yml` / `IADR-0107` / `IADR-0130` / `IADR-0127` / `docs/specs/2026072*`・`2026080*` ほか | 軸 3 で 25 件 | GitHub Actions の通知設定・トースト（`notify`）の既存記述・確定済み仕様書。**アプリ内通知（永続する通知）とは別物**であり、本作業で誤りにならない |
| `CHANGELOG.md` | 全軸 | **生成物**（`scripts/gen-changelog.js` が再生成する。手で書き足さない） |

### 追随させたもの（1 件）

- `src/platform/frontend/src/foundation/ui/notifications.tsx` —— 既存の **トースト**（一過性）を
  「共通シェルの通知」と呼んでいる。本作業で**永続するアプリ内通知**（FR-22）が同じシェルへ載るため、
  **語が 2 つの別物を指すようになる**。冒頭コメントに区別を 1 行足す（実装は変えない）。
  **これは規則 10（「この変更で新たに誤りになる自分の記述」を引き直す）で出たものであり、
  着手前の軸 1・2・4 のいずれでも出なかった。**

### 規則 8（自己参照）の引き算

軸 1（`FR-22`）は**本作業で作る文書とコード自身**を数え込む。したがって走査がそのまま返す数は
「書く前」と「書いた後」で違う。**引き算を見せる。**

```text
走査（同じコマンド・`git add -A` 後）がそのまま返す数 = 65 ファイル
  − 本作業が追加・編集し、かつ FR-22 に一致するもの 52 = 13 ファイル（着手前＝上表の軸 1）
```

**［2026-08-16 是正 / #600］初版は「64 − 51 = 13」と書いていた。** 引き算そのものは再現するが、
**引く側のラベルが誤っていた** —— 51／52 は「本作業が追加・編集したファイル」ではなく
「そのうち **`FR-22` の文字列に一致するもの**」である。本作業が触ったファイルは実際には **56 件**で、
差の **4 件は Lingui カタログ**（`foundation/i18n/locales/{ja,en}/messages.{po,ts}`）である。
カタログは文言の実体だけを持ち起点 ID を書かないので、軸 1 の走査には最初から掛からない。
**13 という結論は動かない**が、母集合のラベルは正しておく（規則 8 は「引き算を見せる」ことを
求めており、**引く側が何であるかを取り違えたままでは追試できない**）。

**52 の内訳**（すべて本 PR の成果物である）:

| 種別 | 件数 | 内容 |
| --- | ---: | --- |
| 新規の文書 | 5 | 本書 / IADR-0215 / `docs/functional/FR-22_*` / `docs/tests/FR-22_*` / `docs/api/BFF_notifications.md` |
| 編集した文書 | 2 | `docs/adr/README.md`（索引 1 行）/ `docs/api/openapi.yaml`（契約） |
| 床の記録 | 1 | `scripts/chunk-budget-baseline.json`（増加の根拠を追記） |
| **orval 生成物** | **34** | うち新規 3（`generated/notifications/*`）。**残り 31 は `info.description` へ `FR-22` を足したことによる先頭コメントの差分だけ**である |
| 新規のフロント実装・テスト | 6 | `foundation/notifications/` 配下 |
| 編集したフロント | 3 | `foundation/ui/Layout.tsx`（ベルの設置）/ `Layout.test.tsx`（`QueryClientProvider`）/ `foundation/ui/notifications.tsx`（語の区別） |

**生成物 34 件を「自分が書いた」と数えるのは、走査が実際にそれを返すからである**
（規則 7: 走査の出力を加工して読まない）。

## 目的・背景

計画 `FR-22` は **2026-08-07 の利用者裁定**で新設された。起案理由そのものが
「**従前、通知は FR-19 / FR-20 の受け入れ基準の中にしか存在せず、`02_service-decomposition` の
11 サービス＋BFF にも担い手が無かった**」である（ADR-0045 §結果 フォローアップ 1）。

**実装側でも同じ状態が続いている**——`docs/api/openapi.yaml` に通知系のパスは 0 件、
`docs/functional/` に `FR-22` の機能仕様書は無い。

計画は意図的に次を決めていない（`INDEX.md` が「計画としては決めないと決定した」と記録している）。

> **本要求が定めるのは「何を・いつ・誰へ通知するか」までである。アプリ内通知の実体（保持・既読・配信）と
> 送出主体（どのサービスが担うか）は実装設計に委ねる**

したがって**計画へ差し戻す論点ではなく、実装 ADR が要る**。本作業の主眼は
**[IADR-0215](../adr/IADR-0215_notification-service-and-in-app-delivery.md) の確定**であり、
実装はその決定を**契約とアプリ内通知の受け皿まで**先行させる。

## 対象範囲

### 対象

1. **実装 ADR [IADR-0215](../adr/IADR-0215_notification-service-and-in-app-delivery.md)** —— issue が求める 5 点（送出主体・アプリ内通知の実体・メール経路・送信レート・発火の検知）に加え、**決定 6 として本 PR の射程**を書く。
2. **必須仕様書** —— 機能仕様書 [`docs/functional/FR-22_user-notifications.md`](../functional/FR-22_user-notifications.md)、テスト仕様書 [`docs/tests/FR-22_user-notifications.md`](../tests/FR-22_user-notifications.md)、通信仕様書 [`docs/api/BFF_notifications.md`](../api/BFF_notifications.md)。
3. **BFF 契約** —— `docs/api/openapi.yaml` の `/bff/` 配下へ 2 本（一覧・既読化）。**スキーマにタイトル／本文の項目を作らない**（受け入れ基準 2 を契約で守らせる）。orval 生成物を再生成してコミットする。
4. **アプリ内通知の受け皿（フロント）** —— `platform/frontend` の共通シェルへ通知ベルと一覧を置く。TanStack Query（orval 生成フック）＋ Lingui（ja / en）＋ `@platform/ui` プリミティブ。
5. **追随** —— `docs/api/BFF_bff-surface.md` の端点表、`docs/adr/README.md` の索引、既存トーストの語の区別。

### 対象外（本 PR では触らない）

| 対象外 | 理由 |
| --- | --- |
| **`src/*/backend/**` の一切** | **この環境に `dotnet` が無い**（実測。`which dotnet` が exit 1）。`CLAUDE.md` は「`dotnet build` / `dotnet test` が両ユニットで通ること」を求めており、**ビルドもテストも実走できない変更を出すことは `/verify`（手順 7）を満たさない**。バックエンドは別 issue／別環境で行う |
| **メール送出の実装** | **SMTP は実装側に 1 件も無い**（利用者裁定 2026-08-15「実環境が要るものは触らない」／[[IADR-0197]] 決定 5）。**FR-22 が「アプリ内通知を主・メールを補助」と定めているため、メール不在は本 issue のブロッカーではない**（IADR-0215 決定 3） |
| **①②③ の発火の結線** | 発火源（個人資料の論理削除・保存容量・同期トークン）は **FR-19 / FR-20 の機能**であり、**#451 が保留中**（[[IADR-0119]] / [[IADR-0142]]）。**保留中の機能に結線すると、動かないコードが「実装済み」として残る** |
| **SSE による配信** | **移行第 4 段（#788）の射程**である。本作業ではポーリングを採る（IADR-0215 決定 2） |
| **`CLAUDE.md` / `.claude/rules/`** | 必読規約の総量予算（50 KB）に余白が 1,070 B しかない。**本作業に規約の追加は要らない**（同型の事故が 2 回起きていない。`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」） |

> **#454 の棚卸し（`docs/specs/20260815_issue-454_open-issue-stocktake-and-waves.md`、`status: done`）は
> #600 の宣言済みファイル領域を `src/platform/backend/**`・`docs/adr/`・`docs/api/openapi.yaml` と
> 記録している。本 PR の実領域はこれと異なる**（backend を外し `src/platform/frontend/**` と
> `src/packages/ui` 相当の参照を足す）。**確定済み仕様書は書き換えない**ので、差異はここに記す。
> **並列作業の非重複判定に使うのは本仕様書の領域である。**

## 設計

### 通知の 3 契機（計画が確定させたもの。ここは実装が決めてよい範囲に無い）

| # | 契機 | 内容 | 宛先 | 根拠 |
| --- | --- | --- | --- | --- |
| ① | 個人資料の削除通知 | 週次／完全削除の **7 日前**／完全削除の**事後** | 所有者本人のみ | ADR-0037 決定 6 |
| ② | 保存容量の警告 | **80% / 95%** に達した時点で**各 1 回** | 本人のみ | FR-19 / ADR-0037 決定 17 |
| ③ | 同期トークンの期限予告 | 期限の **7 日前**（当日通知は設けない） | 所有者本人のみ | ADR-0037 決定 18 |

**いずれも件数と期限のみを含み、資料のタイトル・本文を含めない。**

### 契約（BFF）

```text
GET  /bff/notifications                 本人宛の通知一覧（+ 未読件数）
POST /bff/notifications/{id}/read       1 件を既読にする（+ 更新後の未読件数）
```

**`NotificationDto` にタイトル／本文の項目を作らない。** 構成は
**種別（`kind`）・件数（`count`）・閾値（`thresholdPercent`）・期限（`deadline`）・発生時刻（`occurredAt`）・既読（`read`）**
だけであり、**表示文言はフロントが Lingui カタログから組み立てる**。

**これは受け入れ基準 2（本文が件数と期限のみ）を「実装が守る」から「契約が守る」へ移す設計判断である**
（IADR-0215 決定 2）。**契約に項目が無ければ、後段がうっかりタイトルを載せることは型の上でできない。**
この不変条件そのものを検査するテストを置く（§テスト方針 T-01）。

`required` は応答スキーマに必ず付ける（[[IADR-0132]]。`required` の無いスキーマは orval が
全プロパティを省略可で生成し、型検査の網にならない）。

### フロントの置き場 —— `platform/frontend`（foundation）

**`platform/frontend`（foundation）に置く。** 根拠は 3 つある。

1. **通知の受け皿はアプリシェル横断の要素である。** `05_screens` §共通シェル が持つ要素であり、
   画面（feature）1 枚に属さない。共通シェル（`foundation/ui/Layout.tsx`）は既に platform 側にあり、
   トースト（`foundation/ui/notifications.tsx`）も同じ場所にある。
2. **契約が「件数と期限のみ」でドメイン語彙を持たない。** `kind` は列挙であり、
   資料のタイトルも検索語も回答内容も含まない。**`knowledge` ユニットのドメインに依存しない。**
3. **`platform` → 可変ユニットの参照は禁止**（`CLAUDE.md` §サービス境界／[[IADR-0056]]）。
   仮に `knowledge/frontend` へ置くと、**共通シェルが可変ユニットを参照する**ことになり禁止に触れる。
   逆向き（`knowledge` → `platform`）は許されるので、将来 `knowledge` の画面が通知へリンクすることは
   この置き場でも妨げられない。

**却下した案**: `knowledge/frontend` へ置く案。発火源（個人資料・保存容量・同期トークン）が
FR-19 / FR-20 = knowledge 側の機能であることが唯一の根拠になるが、**発火源は本 PR の射程外**であり、
かつ 3 の禁止に触れる。**発火源の所在で受け皿の所在を決めない。**

置き場は `src/platform/frontend/src/foundation/notifications/` とする
（**既存のトースト `foundation/ui/notifications.tsx` とは別物**。前者＝永続する通知、後者＝一過性のトースト）。

### 表示（色だけで意味を持たせない）

`@platform/ui` の **`StatusBadge`**（色 ＋ アイコン ＋ テキストを API で強制する）と **`Alert`** を使う。
アイコンは **lucide-react**（外部 CDN・Web フォント・analytics は使わない。`08_data-egress-policy`）。
**`@platform/ui` に表示文言は入れない**（[[IADR-0125]] 決定 1）——文言はすべて呼び出し側の Lingui マクロで書く。

## 受け入れ基準

issue #600 の受け入れ基準は 6 項目である。**本 PR で満たせるのは 1 と 6 のみ**であり、
**2〜5 は 4 項目すべてがバックエンドの振る舞い**である。

| # | 受け入れ基準 | 本 PR | 理由 |
| ---: | --- | --- | --- |
| 1 | 実装 ADR に 5 点（送出主体・アプリ内通知の実体・メール経路・送信レート・発火の検知）が決定として書かれている | **満たす** | [IADR-0215](../adr/IADR-0215_notification-service-and-in-app-delivery.md) 決定 1〜5 |
| 2 | **本文が件数と期限のみで構成されることをテストが検証している** | **契約レベルで満たす／振る舞いは backend 待ち** | **スキーマにタイトル／本文の項目が無いことを契約テストが検査する**。ただし「後段が実際に何を詰めるか」は backend の振る舞いであり `dotnet` 不在で検証できない |
| 3 | 所有者本人以外へ届かないことをテストが検証している | **backend 待ち** | 宛先の解決（JWT の `sub` で絞る）は BFF ＋ 後段の実装である |
| 4 | メールが送れなくてもアプリ内通知が届くことをテストが検証している | **backend 待ち** | アプリ内通知とメールの独立性は送出側（outbox）の振る舞いである |
| 5 | 送信上限を超える通知が静かに落ちないことが観測できる | **backend 待ち** | 監査ログ・SC-10 のメトリクスは backend ＋ 可観測性の配線である |
| 6 | 保留したもの・先送りしたものを明示する | **満たす** | 本節・§保留・先送り・IADR-0215 決定 6・PR 本文 |

**したがって `Closes #600` にしない。** `Refs #600` とし、issue は開いたまま残す。

- [x] IADR-0215 に決定 1〜5 ＋ 射程（決定 6）が書かれている
- [x] BFF 契約に通知の 2 端点が載り、**スキーマにタイトル／本文の項目が無い**
- [x] その不変条件を検査するテストがある
- [x] アプリ内通知の受け皿が共通シェルに載り、ja / en の文言が揃っている
- [x] 状態表示が色だけで意味を持たない（色 ＋ アイコン ＋ テキスト）
- [x] 保留・先送りが仕様書・IADR・PR 本文に明示されている

## テスト方針

写像の詳細は [`docs/tests/FR-22_user-notifications.md`](../tests/FR-22_user-notifications.md) が正本である。要点のみ:

| ID | 何を固定するか | 種別 |
| --- | --- | --- |
| T-01 | **`NotificationDto` にタイトル／本文に相当する項目が無い**（契約そのものの不変条件） | Vitest（`docs/api/openapi.yaml` を読む） |
| T-02 | 3 契機（①②③）の文言が**件数と期限だけ**から組み立てられる | Vitest（純関数） |
| T-03 | 一覧の描画・未読件数・既読化（TanStack Query ＋ MSW 相当のスタブ） | Vitest + Testing Library |
| T-04 | 状態表示が色だけで意味を持たない（アイコンとテキストが常に付く） | Vitest + Testing Library |
| T-05 | 取得失敗時にシェルが壊れない（縮退） | Vitest + Testing Library |
| **未実装** | 受け入れ基準 3・4・5（本人以外へ届かない／メール不在でも届く／上限超過が落ちない） | **`[Fact]` への写像は #600 で追跡**。本 PR では書かない |

## 検証で分かったこと（判断が要った 4 点）

1. **共通シェルのテストに `QueryClientProvider` が無かった。**
   `foundation/ui/Layout.test.tsx` は `AuthContext.Provider` ＋ `RouterProvider` だけで描画しており、
   通知ベルが TanStack Query を読んだ瞬間に**シェルごと落ちた**（15 件が一斉に失敗）。
   実アプリ（`App.tsx`）は `QueryClientProvider` を持っているので、**テストの器が実アプリより狭かった**
   だけである。器を実アプリへ揃えた（描画のたびに検査用クライアントを作る。`renderUnitRoute` と同じ作法）。

2. **テストの起点 ID に `UC-11` を書かない。**
   書くと `check-test-traceability.js` が「実装先行・仕様書なし」として `docs/tests/UC-11_*.md` を要求する
   （実測: exit 1）。UC-11 は FR-19 / FR-20 / FR-21 / FR-22 をまたぐユースケースであり、
   **保留中の機能を含む UC のテスト仕様書は今は書けない**。
   `scripts/test-traceability-allowlist.json` の `$comment_specMissing`［2026-08-05 / #502］が
   「**保留対象の ID は、その機能に着手する issue が初めて書く**」と定めているので、
   **allowlist を増やさず、テスト側から ID を外した**。UC との対応は機能仕様書とテスト仕様書が持つ。

3. **カバレッジ床は動かさない。**
   実測（`pnpm run test:coverage` / 77 ファイル **968 件**すべて成功。**最終コミット時点**）:
   全ユニット横断 lines 97.04%（7827/8065）/ branches 91.31%（1745/1911）/ functions 93.37%（536/574）。
   MSP 所有分のみ lines 96.56%（5222/5408）/ branches 92.38%（1115/1207）/ functions 93.64%（368/393）。
   本ファイルが定める導出規則（**MSP 所有分の実測から 5pt 下・切り捨て**）から出る床は
   **lines/statements 91 / branches 87 / functions 88** であり、**現行と同値なので引き上げない**。
   `coverage.exclude` は 1 件も増やしていない。

4. **チャンク床は動かす（+7.01 kB）。**
   622,675 → **629,689** バイト、小チャンク 6 → 4 本。**切り分け済み**——同一環境で本 PR を外して
   ビルドすると 622.67 kB（床ちょうど）で通る。根拠と内訳は
   `scripts/chunk-budget-baseline.json` の `$comment_initialTotalBytes`［2026-08-16 / #600］に書いた。
   **通知の受け皿は共通シェルに載るので、必ず初期ロードへ入る**（遅延させると未読件数が
   シェル描画の 1 往復後にしか出ない）。Knip の床は**動かしていない**（41 件のまま）。

   **［2026-08-16 是正 / #600］床は 2 段で動いた。** 初版は 629,286 で、その後の是正
   （AI レビュー指摘の**閉じる導線**）で **629,689（+0.40 kB）**へ再更新した。
   **根因は「床を動かす変更」と「床の更新」を別コミットに分けたことである** ——
   実装だけを積んだコミットで CI が赤くなった。**バンドルに載る変更は、床の更新を同じ
   コミットに含める。**（`kind` を開いた是正ではチャンク合計は動かなかった。orval が
   生成していた `enum` の `const` 群は元から tree-shaking で落ちていたためである）

## 計画書との差異

- 差異: **なし**。計画が実装設計へ委ねた範囲（送出主体・アプリ内通知の実体・メール経路・送信レート・発火の検知）を
  IADR-0215 で埋めたものであり、**計画の確定記述に反する実装は無い**。
- 計画への環流: **不要**。issue #600 が引く planning `INDEX.md:61` のとおり
  「**計画としては決めないと決定した**」状態であり、計画側に不足があるわけではない。

## 保留・先送り（issue #600 受け入れ基準 6）

| 保留したもの | 送り先 | 解除条件 |
| --- | --- | --- |
| **バックエンド（送出主体 `NotificationService`・BFF 端点の実装・outbox・レート制御）** | **#600（開いたまま）** | `dotnet` が実走できる環境 |
| **①②③ の発火の結線** | **#451（FR-19 / FR-20）** | #451 の保留解除（[[IADR-0119]] / [[IADR-0142]]） |
| **メール送出（SMTP リレー）** | **#600 ＋ ADR-0045 のメール基盤の実装** | 実環境の SMTP 資格情報（[[IADR-0197]] 決定 5 の射程外） |
| **SSE への切り替え** | **#788（移行第 4 段）** | 第 4 段の着手 |
| **通知の一覧画面（SC 番号つきの独立画面）** | 未起票 | 計画に SC 番号が無い。**必要になったら計画へ環流して SC を起こす**（実装側で番号を作らない。[[IADR-0179]] 決定 2） |

## 未決事項

1. **通知の保持期間を 90 日としたが、計画に根拠は無い**（IADR-0215 決定 2）。個人資料の論理削除の
   保管期間（90 日・ADR-0037 決定 5）へ揃えたものであり、**実装側の判断である**。運用で不足が出たら改定 IADR を起こす。
2. **ポーリング間隔 60 秒も同様に実装側の判断である**（IADR-0215 決定 2）。SSE 化（#788）で消える見込み。
3. **`x-roles: []`（端点認可なし・認証必須）は宣言のみで、実効ロールとの突合は行われていない**
   ——`scripts/check-bff-authz-docs.js` は**実装 → 契約**の一方向しか見ず、**実装が無い端点は検査されない**（実測）。
   backend が入った時点で初めて突合が効く。**この穴をここに開示しておく。**
