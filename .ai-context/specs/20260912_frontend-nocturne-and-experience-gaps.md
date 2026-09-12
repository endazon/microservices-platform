---
title: フロントエンドを hi-fi モック（Nocturne）の構造へ合わせ、体験の穴（再試行・三部品・AI 対話・エラー境界・a11y）を塞ぐ
type: spec
status: done
related_ids: [NFR, NFR-12, FR-04, FR-08, FR-14, SC-01, SC-02, SC-03, SC-04, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, SC-12, SC-17, SC-18, SC-19, SC-20, SC-21, UC-01, ADR-0031, IADR-0009, IADR-0037, IADR-0119, IADR-0120, IADR-0121, IADR-0124, IADR-0125, IADR-0126, IADR-0129, IADR-0134, IADR-0135, IADR-0365, IADR-0435, IADR-0436, IADR-0437, IADR-0438, IADR-0439, IADR-0440, IADR-0441]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# UI/UX 改善: Nocturne への適合と体験の穴

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。
>
> **［記録の性質］本書は 1 PR ぶんの横断仕様である。** 実装は複数のエージェントが専有領域を
> 分けて並行して行い、各主題の判断は実装ADR（`IADR-0435`〜`IADR-0441`）が持つ。
> 本書が持つのは **範囲・母集合・受け入れ基準・検証記録・計画書との差異** である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-04`（根拠付き AI 回答）／`FR-08`（回答フィードバック）／`FR-14`（可変ユニットの合成）
- 非機能要件（NFR）: `NFR-12`（アクセシビリティ）。**見た目の適合・規約整備・検査器の追加に当たる番号は
  計画の非機能要件表に無い**ため、それらは無採番の `NFR` とする
  （`.claude/rules/traceability.md` §起点 ID の種別 の場合 2。**環流しない**）
- ユースケース（UC）: `UC-01`（検索・質問する）
- 画面（SC）: `SC-01`〜`SC-12`・`SC-17`〜`SC-21` の **17 画面**
  - 🔴 **`SC-13`〜`SC-16` は対象外である。** ログイン・ワンタイムコード・パスワードリセット・
    アカウント設定は **Keycloak のテーマ側**の画面であり、本 SPA は描画しない。
    「17 画面」の内訳がこの 17 枚であることを明示する（**黙って除外しない**）。
- 関連 ADR: `ADR-0031`（フロントエンドスタック）
- 計画書リンク: 計画リポジトリ（GitHub URL または隣接クローン `../project-planning`。読み取り専用）の
  `projects/microservices-platform/` 配下 —— `05_screens/01_screens.md` と `05_screens/mockups/hi-fi/`
  （**Nocturne デザインシステム。実装の正**）、`06_technical/13_frontend-stack.md`、
  `06_technical/08_data-egress-policy.md`、`07_adr/ADR-0031_frontend-stack.md`
- 起点の検討メモ: **第 3 弾「UI 部品の現在地」**・**第 4 弾「体験の穴」**
  （2026-09・**別途送付**）。🔴 **中間成果物であり、どちらもリポジトリに実体を持たない。
  したがってリンクを張らず平文で引用する**（切れるリンクを作らない）。
  裁定と決定の内容そのものは本書と `IADR-0435`〜`IADR-0441` に残してある。

## 目的・背景

第 3 弾・第 4 弾の 2 本の検討メモが挙げた指摘は、要約すると 2 系統である。

1. **見た目が計画のモックと違う。** 計画の hi-fi モックアップ（Nocturne）は暗い面・3 カラム・
   区画の語彙（`.panel` / `.stat` / `.bar` / `.kv` / `.note`）を持つが、実装は明るい面・2 カラム・
   素の `div` で近似していた。共有 UI（`@platform/ui`）にはダークのトークンが 1 つも無かった。
2. **体験に穴がある**（効く順）: (1) 失敗時の再試行導線が無い (2) 待ち・空・エラーが画面ごとに違う
   (3) AI 回答の追従スクロール・停止・再生成が無い (4) 画面単位のエラー境界が無い
   (5) 画面設計テンプレに空状態・エラー文言の欄が無い (6) a11y の機械検査が無い。
   次の段として Markdown 描画＋コピー／出典の対応印／遷移時の先読み／skip link とダイアログの閉じ込め。

🔴 **触ってはいけない良さがある**（第 4 弾が名指しした既存の資産。本作業で壊さないこと）:
自動再試行の判定（4xx を再試行しない）／0 件と失敗の区別／確認ダイアログの初期フォーカス（取消側）／
色だけに頼らない状態表示／役割で引く E2E／取得前に器を描かない。

## 利用者裁定（2026-09-12。すべて確定）

| # | 項目 | 裁定 |
| --- | --- | --- |
| 1 | テーマ | ライト＋ダーク両対応。**既定は system**（`prefers-color-scheme`）。ヘッダの切替ボタンは `system → system の反転 → もう一方（明示） → system` の 3 状態循環、localStorage 保持 |
| 2 | 計画側 | 画面設計テンプレ改訂を含める（計画リポにも PR）。加えて両プロジェクトの `01_screens.md` §共通シェル へ「待ち・空・エラー・再試行の横断表示規約」を追記（本プロジェクト側は `draft` のため直接、もう一方は `fixed` のため変更履歴付き） |
| 3 | 可変ユニットの i18n | Lingui を導入するが**英訳は不要**（ja カタログのみ、en は ja へ倒す） |
| 4 | 実装体制 | サブエージェントを専有領域で分けて並行させる |
| 5 | Dialog の土台 | Base UI が活発なら採用 → 実測 `@base-ui/react` 1.8.0（2026-09-04 公開・1.4〜1.8 が月次）→ **採用**（旧名 `@base-ui-components/react` は rc.0 で停止。使わない） |
| 6 | 次の段 | **4 項目すべて含める**: Markdown 描画＋コピー／遷移時の先読み／skip link ＋ ダイアログ閉じ込め／出典と本文の対応印（脚注方式） |
| 7 | a11y | **error で導入し、既存違反も今回すべて直す**（抑制ファイルへ逃がさない） |
| 8 | 画面 | **17 画面すべてをモックの構造へ合わせる**（画面群を 3 分割して並列） |

## 対象範囲

- 対象:
  - `src/packages/ui/**`（トークン・共有部品・Storybook）
  - `src/platform/frontend/src/{app,components,lib,features}/**`（シェル・ルータ・テーマ・AI 対話・
    `QueryState`・i18n の登録口・合成点）
  - `src/knowledge/frontend/src/features/**`（17 画面）と `src/knowledge/frontend/src/components/**`
  - `src/eslint.config.js`（`jsx-a11y` の**新しいブロック**）・`src/pnpm-workspace.yaml`
  - `src/platform/frontend/vite.config.ts`（`manualChunks`）・`scripts/chunk-budget-baseline.json`・
    `scripts/knip-baseline.json`
  - `.ai-context/{specs,adr}/**` と `docs/{screens,README.md}`
- 対象外:
  - **バックエンド・BFF 契約**（1 行も触らない）。契約に無い要素は描かない（§計画書との差異）。
  - **`src/ai-stock-trading`（submodule）のポインタ前進。** 当該ユニットの実装は別リポジトリの
    同日 PR で行い、**本 PR には submodule SHA の前進を含めない**（マージ後の自動 PR に委ねる）。
  - `docs/ai-workflow.md` の**必須 check の表**。a11y の配線は既存の lint / E2E ジョブへ乗るので
    新しい check 名は増えない（`IADR-0440` 決定 5）。
  - `SC-13`〜`SC-16`（Keycloak テーマ側。前掲）。
  - 計画リポジトリ側の変更（テンプレ改訂・横断規約の追記）。**別 PR** で着地済みである。

## 設計

判断の正本は実装ADR 7 本である（本書へ複写しない）。

| IADR | 主題 |
| --- | --- |
| `IADR-0435` | Nocturne トークンの二層化とテーマ切替（system 既定・3 状態循環） |
| `IADR-0436` | 共有 UI への三部品・区画部品の追加と、重なりの部品の土台に Base UI を採る判断 |
| `IADR-0437` | 取得結果の 3 相を 1 部品へ閉じ込め、再試行の可否を画面が決める |
| `IADR-0438` | 画面単位のエラー境界・待ち表示と、リンクに触れた時点の先読み |
| `IADR-0439` | AI 対話の追従スクロール・停止・再生成・出典・Markdown と脚注方式の対応印 |
| `IADR-0440` | a11y の機械検査（`jsx-a11y` を error・axe の E2E）と抑制ファイルへ逃がさない裁定 |
| `IADR-0441` | 可変ユニットの文言カタログの登録口と、共有 UI の公開 hoist |

### 実装範囲（コミット単位）

作業ブランチ `claude/frontend-ui-ux-improvement-3lwuzw`、基点 `366de29`。

| # | コミット | 内容 | 主な IADR |
| --- | --- | --- | --- |
| 1 | `adc85f2` | トークンを Nocturne へ二層化。既存プリミティブ（Button / Input / Table / Tag / Tabs / Label / Card）をモックの部品クラスへ。`@source '.'` の是正 | 0435 |
| 2 | `cdde500` | 共有 UI へ三部品・区画部品・`Dialog` / `Tooltip`（Base UI）を追加。`vendor-baseui` を `manualChunks` と `requiredChunks` へ | 0436 |
| 3 | `72f3b8e` | テーマ切替（純関数 ＋ `useTheme` ＋ `ThemeToggle`） | 0435 |
| 4 | `9163736` | `registerUnitMessages`・合成点の任意項目読み・`publicHoistPattern` | 0441 |
| 5 | `ec00e79` | `QueryState` を foundation へ追加（陰性対照テスト付き） | 0437 |
| 6 | `f53fd82` | `QueryState` の `query` 型を `UseQueryResult<T, unknown>` へ広げる（orval 生成フックを直接渡す） | 0437 |
| 7 | `ac84eab` | `SC-18`〜`SC-21` をモックの区画へ。`ConfirmDialog` を Base UI `Dialog` へ載せ替え | 0436 / 0437 |
| 8 | `4b6650f` | 共通シェルを 3 カラムへ（skip link・ロールタグ・テーマ切替・アバター）。ルータの既定エラー／待ち／先読み | 0435 / 0438 |
| 9 | `89906e3` | AI 対話（右レール・`SC-01`）: 追従・停止・再生成・出典・Markdown・コピー・脚注対応印 | 0439 |
| 10 | `a51b059` | `SC-09`〜`SC-12`・`SC-17` をモックの区画へ。三部品を `QueryState` 11 か所へ統一 | 0437 |
| 11 | `d5c45f4` | `SC-02`〜`SC-08` をモックの区画へ。手書き三状態 35 箇所を撤去 | 0437 |
| 12 | `42267c6` | 翻訳カタログ（ja / en）の統合再生成 | — |
| 13 | `3947b13` | 統合後のチャンク床・knip 床の再測。`SC-05` の未使用 export 型を内部化 | 0436 / 0439 |

実測（`git diff --stat 366de29..HEAD -- src scripts`）: **146 ファイル・+9,127 / −3,150 行**。

a11y（`IADR-0440`）の実装は**同じ PR の別タスクが並行して実施中**である（`src/eslint.config.js` の
新ブロックと E2E 2 本、既存違反の修正）。本書の §検証記録 にその実測を残す。

## 母集合（規則 1〜10 に従って引いた）

> `.claude/rules/traceability.md` §是正・追随の母集合の取り方（規則 1〜8）と
> `traceability.repo.md` §同（規則 9・10）。**引いた結果と、除外したものとその理由を書く**（規則 6）。

### 走査 1: 無効な任意値記法（`IADR-0435` 決定 5 の是正対象）

**誤りの側の文字列から引く**（規則 1）。区切りの形をすべて列挙する（規則 2）——
接頭辞は `text-` だけではないので、**トークン種別で引く**。パスで絞り、拡張子では絞らない（規則 3・4）。

```console
$ git grep -c '\[--\(color\|radius\|shadow\|space\|font\)-' 366de29 -- src | grep -v ai-stock-trading
（ファイル 51 / 行 280）

$ git grep -c 'text-\[--color-' 366de29 -- src | grep -v ai-stock-trading
（ファイル 49 / 行 249）           ← 軸 1 だけだと 2 ファイル・31 行を取りこぼす（規則 5）
```

**着手前の内訳**: `src/packages/ui` 11 ファイル／`platform` ＋ `knowledge` 38 ファイル 218 行。

```console
$ git grep -l '\[--\(color\|radius\|shadow\|space\|font\)-' HEAD -- src | grep -v ai-stock-trading
HEAD:src/knowledge/frontend/src/lib/scope-filter/ScopeFilter.tsx   （7 行）
HEAD:src/packages/ui/src/styles.css                                （1 行）
HEAD:src/platform/frontend/src/lib/auth/LoginPage.tsx              （2 行）
```

- **51 ファイル 280 行 → 3 ファイル 10 行**（HEAD `3947b13` 時点）。
- 🔴 **完了していない。** 残る実体は `ScopeFilter.tsx` 7 行と `LoginPage.tsx` 2 行の
  **2 ファイル 9 行**である。どちらも本 PR の各タスクの専有領域の外（`lib/scope-filter` と
  `lib/auth`）にあり、**画面群の分割からも共通シェルからも漏れた**。
- **除外 1 件**: `packages/ui/src/styles.css:46` の 1 行は**注記の中の引用**であり
  クラス文字列ではない（`rounded-[--radius-control]` が旧実装の綴りだったことを説明している）。
  是正の対象にしない。
- 🔴 **この走査は T7（a11y）が並行して `src/**` を編集中に実行した**（規則 8）。
  上の数は **HEAD（`3947b13`）に対する `git grep`** であり作業ツリーではないので、
  並行編集の影響を受けない。**未コミットの作業ツリーでは値が違い得る。**

### 走査 2: `role="status"` の直書き（`IADR-0437` 決定 1 の効果測定）

```console
$ git grep -c 'role="status"' 366de29 -- src | grep -v ai-stock-trading   → ファイル 25 / 行 41
$ git grep -c 'role="status"' HEAD     -- src | grep -v ai-stock-trading   → ファイル 21 / 行 25
```

**残る 25 行は三部品の外の用途**である（ストリーミング中の通知・検証結果の告知・
件数の読み上げなど）。`QueryState` に寄せるべきものではないので**是正の対象ではない**。

### 走査 3: `QueryState` の普及（規則 7 —— 値を直したら全走査し直す）

```console
$ git grep -c '<QueryState' HEAD -- src | grep -v ai-stock-trading   → ファイル 20 / 出現 36
$ git grep -c '<QueryState' 366de29 -- src                           → 0 件
```

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `src/ai-stock-trading/**`（submodule） | **別プロジェクトである**（`IADR-0120`）。本リポジトリからは是正できない。当該ユニット側の同日 PR が同じ走査を自分で行う |
| `CHANGELOG.md` | 生成物である。コミット件名は書き換えず、必要なら `scripts/changelog-overrides.json` の `remap` で生成物の側を是正する |
| `.ai-context/specs/` / `.ai-context/superpowers/` の既存記録 | **凍結記録**である（`traceability.repo.md` §凍結の射程）。当時の記述として正しく、後から表記だけ直すと当時の判断と食い違う |
| `docs/` の既存文書のうち本作業で実態が変わらないもの | 走査 1〜3 はいずれも**コードの記法と部品の採用**であり、文書の記述を誤りにしない |
| `function Section` 等のヘルパ抽出 | **別プロジェクト側の作業**である。本リポジトリの母集合に入れない |

### 走査 4（規則 10 —— この変更で新たに誤りになる自分の記述）

| 記述 | どうしたか |
| --- | --- |
| `IADR-0125` 決定 2「Dialog は移植しない」 | **前提が変わった**。`IADR-0436` が補完し、`IADR-0125` §関連 へ「補完される」を追記した（**本文プロズは書き換えていない**。`updated:` を前進） |
| `.ai-context/adr/README.md` の索引 | 7 行を追記した |
| `docs/screens/SC-01_search-chat.md` §未決事項 4「右レール AI チャットパネル（移行第 4 段）」 | 右レールは既に着地しており、本作業でさらに停止・再生成・Markdown・対応印が入った。§停止・再生成… の節を追記した |
| `docs/README.md` | 横断の表示規約の所在を 1 か所に足した |
| `scripts/chunk-budget-baseline.json` の床と `$comment` | 統合後の実測へ更新済み（コミット 13） |

## 受け入れ基準

- [x] **Given** OS がダーク、**When** 初回に開く、**Then** Nocturne のダークで描かれ、`<html>` に
      `data-theme` 属性が無い（system の表現は「属性が無いこと」である）
- [x] **Given** 任意のテーマ、**When** ヘッダの切替ボタンを 1 回押す、**Then** **必ず見た目が変わる**
      （`system` → `system` の反転）。3 回で `system` へ戻り、選択は localStorage に残る
- [x] **Given** localStorage が読めない環境、**When** 起動する、**Then** 例外を投げず `system` で描く
- [x] **Given** 取得が失敗した画面、**When** 描画する、**Then** `role="alert"` の失敗表示と
      **再試行ボタン**が出る。**「該当するものはありません」は出ない**（陰性対照）
- [x] **Given** 取得が成功して 0 件、**When** 描画する、**Then** 空状態が出て**再試行ボタンは出ない**
- [x] **Given** 存在秘匿にかかる 404（文書詳細・Wiki・グラフ）、**When** 描画する、**Then**
      中立の空状態が出て**再試行ボタンを押させない**
- [x] **Given** 1 画面がクラッシュする、**When** 描画する、**Then** **共通シェル（ヘッダ・左レール）は残り**、
      本文だけが「再読み込み」「ホームへ戻る」を持つ表示に差し替わる
- [x] **Given** AI 回答がストリーミング中、**When** 下端に居る、**Then** 内容が増えるたび下端へ追従する。
      **上へ遡ると止まり**、「最新へ」で復帰する
- [x] **Given** ストリーミング中、**When** 停止を押す、**Then** 部分回答が「停止」の印つきで残り、
      **失敗表示にはならない**
- [x] **Given** 回答に `[n]` が含まれ出典が n 件以上ある、**When** 描画する、**Then** `[n]` が脚注
      アンカーへ結ばれる。**出典の件数を超える番号は結ばれない**
- [x] **Given** 回答に `<img>` や `<script>` を含む Markdown、**When** 描画する、**Then** いずれも除去される
- [x] **Given** 確認ダイアログを開く、**When** 初期フォーカスを測る、**Then** **取消側**に当たる（既存の規律が維持されている）
- [x] **Given** キーボードだけで操作、**When** 最初に Tab を押す、**Then** skip link が現れ、
      `#main-content` へ飛べる
- [x] **Given** ログイン後のシェル・`SC-01`・`SC-05`、**When** axe（`wcag2a` / `wcag2aa`）を走らせる、
      **Then** 違反 0 件 —— light / dark 両テーマで実測（§検証記録「統合後の実測（a11y）」）
- [x] **Given** `src/`、**When** `pnpm run lint` を走らせる、**Then** `jsx-a11y` の違反 0 件で、
      `eslint-suppressions.json` に a11y の項目が 1 件も無い —— 同上（既存違反 2 件は修正し、抑制は 0）
- [x] **Given** 17 画面、**When** モックと並べる、**Then** 区画（`Panel` / `Stat` / `ProgressBar` /
      `Kv` / `Note`）の構成が一致する。**モックの `.badges` 行（画面番号・進捗バッジ）は
      メタ情報であり実装しない**
- [x] 文書のゲート（§検証記録）がすべて緑

## テスト方針

- **純関数はテストで固定する**: テーマの 3 状態循環と `system` の解決（OS がダーク／ライトの両方）、
  出典の種別判定、Markdown の描画（未閉じの記法・脚注の結び・サニタイズ）、追従スクロールの下端判定。
- **`QueryState` は陰性対照を必須とする**: 「失敗しているときに空状態を出さない」を明示的に測る。
  正常系だけでは判定順を入れ替えても全部緑のまま通る。
- **E2E は役割で引く**（`getByRole`）。testid へ逃げない。壊れたらテストではなく実装を直す。
- **新規 E2E の spec 名に `sc` プレフィクスを使わない**（`scripts/check-route-manifest.js` の
  母集合を乱さない）。
- **既存テストの置換は「実装の役割が変わった箇所」に限る。**
  `ConfirmDialog` は Base UI が `aria-modal` を付けず外側を `inert` にするため、
  `role="dialog"` ＋ accessible name / description で測る形へ変えた（**緩めたのではなく、
  測る対象を実装の作法へ合わせた**）。
- **誤った否定テストは外す**: データソース管理の「次回同期列が無いこと」を固定していたテストは、
  列を実装したので外した（不在の固定ではなく**誤った不在**を固定していた）。

## 検証記録

### 本書の作成時点で実行したゲート（文書・トレーサビリティ）

リポジトリ直下で実行。いずれも exit 0。

```console
$ node scripts/check-trace-blocks.js
$ node scripts/gen-knowledge-graph.js --check
$ node scripts/check-doc-type-vocabulary.js
$ node scripts/check-doc-status-vocabulary.js
$ node scripts/check-doc-links.js
$ node scripts/check-adr-numbering.js
$ node scripts/check-reading-budget.js
$ node scripts/check-cross-repo-refs.js
$ node scripts/check-plan-id-qualification.js
```

### 各タスクが実装時に測った値（コミット本文と床ファイルの `$comment` が正本）

| ゲート | 実測 |
| --- | --- |
| チャンク床（初期ロード） | 着手前 555.16 kB → トークン・共有部品まで 556.01 kB → AI 対話 656,554 B → **統合後 705,386 B**（床ファイルの値）。コミット件名の「705,390 B」は kB 丸め（705.39 kB）を B で書いた表記であり、**床の実値は 705,386 B** である |
| 内訳で切り分けられた分 | 右レールの `Tooltip` 初利用で `vendor-baseui` +85,457 B／確認ダイアログの載せ替えで **+29.49 kB**（85,457 → 114,124 B）。残り +19.35 kB は共通シェル・`QueryState`・ルータの既定表示・17 画面の追加文言（約 100 msgid × 2 ロケール）の合算で、**並行作業の統合後に測ったため個別には切り分けていない**（切り分けなかった事実を残す。推測で分けて書かない） |
| 遅延側に残ったもの | `vendor-markdown` 44,545 B（`marked`）／`purify.es` 28,930 B（`dompurify`。Wiki と共有）。**Markdown 処理系は初回描画まで届かない** |
| CSS 出力 | 19,887 → 30,255 B（`@source '.'` の是正分。**チャンク検査の母数ではない**） |
| `smallLazyChunks` | 6 → 9（warn のみ） |
| knip 床 | `types` 16 → **15**（`SC-05` の未使用 export 型を内部化） |
| 共有 UI の単体テスト | 95 件を追加（`cdde500`） |

### 親（統合）の最終ゲートで記録するもの

本書の作成時点では**まだ走らせていない**。並行タスク（a11y）が `src/**` を編集中であり、
統合後に一度だけ通すのが正しい。

- `pnpm -r run typecheck` / `pnpm run lint` / `pnpm run lint:templates` / `pnpm run format:check`
- `node scripts/check-knip.js --require`
- `pnpm run codegen` と `pnpm run i18n` の**再生成差分ゼロ** / `node scripts/check-i18n-catalogs.js`
- `pnpm run build` → `node scripts/check-chunk-budget.js --require`
- `pnpm --filter @platform/ui run build-storybook` →
  `node scripts/check-static-egress.js --require packages/ui/storybook-static --require platform/frontend/dist`
- `pnpm run test:coverage`（カバレッジ床）／`node scripts/check-route-manifest.js`
- **Playwright（smoke ＋ a11y ＋ keyboard）。axe の違反件数と、`jsx-a11y` の既存違反修正の実測は
  ここで初めて出る**（`IADR-0440` フォローアップ）。
- 合成の確認（submodule を取得して当該ユニットのブランチを checkout し、typecheck / build /
  チャンク検査を再実行する）。**本 PR に submodule SHA の前進は含めない**ので、
  実測は PR 本文へ貼る。

### 統合後の実測（a11y。IADR-0440 のフォローアップ）

- **`jsx-a11y` は `settings['jsx-a11y'].components` の対応表が無いと効かない。** recommended をそのまま
  入れた最初の実測は **182 ファイルで違反 0 件**だった。画面が `<Input>` / `<Label>` / `<Button>` / `<Table*>`
  （`@platform/ui` のプリミティブ）で書かれており、jsx-a11y は JSX の要素名しか見ないためである。
  プリミティブ → ネイティブ要素の対応表を `settings` に与えて初めて検査が効く（**「0 件」を「違反が無い」と
  読むのが一番危険な状態**だった。`eslint.config.js` のブロック冒頭に記録）。
- 対応表を与えた後の違反: **2 件・1 ファイル**（`packages/ui/src/components/formControls.test.tsx` の
  `label-has-associated-control` ×2）。関連付け先の `<Input id>` を伴わせて修正。**`eslint-disable` は
  追加せず、`eslint-suppressions.json` も未編集。** 他ルールは全ファイルで 0 件（probe ファイルで
  プラグインが効いていることを確認済み）。
- axe（`@axe-core/playwright` 4.13.0、`wcag2a` / `wcag2aa`）: 共通シェル（AI チャットパネルと通知一覧を
  開いてから）／`SC-01`／`SC-05` × light / dark。**修正前 1 件 → 修正後 0 件**。落ちたのは
  `color-contrast`（serious・ダークのみ）で、右レール「AI チャットを開く」（`text-accent` の ghost ボタンが
  `bg-surface-muted` に載る）が **4.38:1**。ボタン 1 つを避難させる形ではなく、ダークの
  `--color-brand` / `--color-accent` を **`#9184d9` → `#988cdb`** へ持ち上げた（HSL 明度のみ +0.02。
  実測: bg 5.96 / surface 5.14 / surface-muted 4.79 / accent-soft 4.84）。**モックの accent 値からの
  意図的な逸脱**であり、IADR-0435 決定 4 の補足に記録した。
- axe の測り方で踏んだ罠 2 つ（対策済み）: (1) テーマ切替直後は `transition` 途中の色を読む →
  `page.addStyleTag` で `transition/animation: none` を注入してから測る。(2) `violations.length` を
  比べず、規則 ID と対象セレクタまで組み立てた配列で比較し、`passes.length > 0` を陽性対照にする
  （axe の注入失敗も `violations: []` になるため）。
- キーボード E2E（`e2e/keyboard-navigation.smoke.spec.ts`）: 2.4.1（skip link → `main`）／2.1.1
  （左ナビを Tab で辿り Enter で遷移）／2.1.2・2.4.3（`SC-19` の削除確認ダイアログの初期フォーカスが
  「やめる」、Tab が外へ出ない、Esc でトリガへ復帰）の 3 本すべて合格。`sr-only` は 1px へ潰す実装で
  Playwright は visible と答えるため、bounding box の幅で「押すまで見えない」を測っている。
- 新 E2E 2 本: **6/6 passed**。`check-route-manifest.js` の母集合（画面 17 件）は不変。

## 計画書との差異

差異: **あり**。下表のとおり。🔴 **「差異」と「計画どおり」を混ぜない** —— 計画が明示的に
禁じているものを実装していないことは差異ではない。

### A. モックと計画の読み分け（環流不要）

**「差異」と「順守」を分けて書く。** 下表のうち `SC-03` / `SC-21` は**モックも計画も同じことを言っており、
実装はそれに従っている**（差異ではない）。ここに載せるのは、**モックに無いものを実装していない**のを
「作り忘れ」と誤読されないためである。

| 画面 | モックが描くもの | 実装 | 根拠 |
| --- | --- | --- | --- |
| `SC-02` | **本文に見出し（`.ttl`）が無い**（実測: 検索行から始まる。パンくず末尾は「検索結果」・左ナビは「結果一覧」） | `<h1>`「検索結果一覧」を**維持**した | 計画本文の画面名が「検索結果一覧」であり、導線テストと E2E がこの文言で画面を特定している。**見出しを消すと画面を特定する手掛かりが無くなる** |
| `SC-03` | **モック自身が「併置しない」と注記している**（実測: 該当の語はその注記 1 行にしか現れない） | バックリンク欄・ローカルグラフを**併置しない**（順守） | 計画の裁定（2026-08-02）が「バックリンク欄・ローカルグラフは `SC-04` のみに置き、`SC-03` には併置しない」と定めている |
| `SC-21` | **モック自身が「一括承認のボタンは置かない」と注記している** | 一括承認ボタンを**置かない**（順守） | 計画が「**一括承認は持たない**」と明示し、「描いてはいけないもの」に列挙している |
| 全画面 | `.badges` 行（画面番号・進捗・仕様書リンク） | 描かない | モックアップの**メタ情報**であり画面の要素ではない（crumb を持たない画面にも付いている） |
| 全画面 | `@import` で Inter を Google Fonts から読む | 読まない | 外部 CDN・Web フォントの利用禁止（`08_data-egress-policy`）。`check-static-egress` が成果物で止める |

### B. 契約に無いので実装しない（**繰り延べであって放棄ではない**。環流候補）

| 画面 | モックの要素 | 契約の状況 |
| --- | --- | --- |
| `SC-08` | チャート・グラフ | **モックにも計画本文にも図の語が無い**。`AiAnswerDto` も系列・集計値を持たない。**計画側は 2026-08-23 に技術検討の備考を訂正済み**であり、本作業で新たに生じた差異ではない |
| `SC-10` | LLM 利用実績の表・「ナレッジ健全性」節・SLO カード・「人/日」 | `DashboardSummaryDto` に該当項目が無く、**BFF に健全性の口も無い**（あるのは `/bff/dashboard/summary` のみ）。既存の画面仕様書が理由を記載済み |
| `SC-19` | 公開範囲・同期状態・タグ | 契約に項目が無い |
| `SC-20` | 同期対象範囲・競合・同期履歴 | 同上 |
| 右レール | モデル選択・フォールバックモデル・データ越境設定・画面コンテキスト添付の ON/OFF・回答の詳しさ | `/bff/analysis/ask/stream` の要求本文は `question` と `attributeFilters` だけである。**置くと「操作できるのに何も変わらない」設定になる** |

### C. 計画の記述に不足があると判断したもの（環流候補）

| 対象 | 観測 |
| --- | --- |
| 共通シェルの 3 カラム | **計画本文（技術検討・画面設計）に列構成の明文が無い**（実測: `13_frontend-stack.md` に `178` / `244` / 「3 カラム」の語は 1 件も無い）。列の構成を定めているのは **hi-fi モックの `.hf` だけ**である（`grid-template-columns: 178px minmax(0,1fr) 244px`）。実装はモックに合わせた。**「モックが正」という位置づけは計画が与えているが、シェルの骨格のような横断事項が本文に無いのは読み落としを招く** |
| ライトテーマの値 | モックはダーク 1 本しか与えていない。ライト値は **Nocturne の ramp を反転して実装側で定義した**（コントラストは実測で 4.5:1 以上。`IADR-0435` 決定 4）。**計画がライト値を定めた場合は上書きされる** |

🔴 **環流（issue 起票）は本書の作成時点で未了である。** `docs/README.md` 運用ルール 5 のとおり、
**「環流した」と書いてよいのは起票し終えたときだけ**なので、ここでは「環流候補」と書く。
起票は PR の段で行う（**起票前に同件の既存 issue を必ず検索する**）。

## 残余リスク

1. 🔴 **初期ロードの約 12%（114,124 B）が Base UI である。** 右レールの `Tooltip` と
   確認ダイアログの `Dialog` が原因で、`React.lazy` では消えない静的辺である。
   **次に初期ロードを増やす作業は、先に切り離しを検討すること** ——
   (a) 右レールから `Tooltip` を外す、(b) `manualChunks` を `@base-ui` のサブパスで分ける
   （`vendor-baseui-dialog`）。どちらでも 85 kB 前後を遅延へ動かせる見込みである（**未実測**）。
2. **submodule の bump で `pnpm-lock.yaml` が動く。** 本 PR には含めないため、bump PR に差分が出る。
   あわせて合成点の任意項目読み（`as unknown as {...}`）を**名前付き import へ戻す**こと。
3. ~~🔴 **`NotFound` が自前の `<main>` を持ち、`Layout` の `<main id="main-content">` と入れ子になる。**~~
   **解消済み**（［2026-09-12 追記 / #1438］。`IADR-0442`）。既存負債として据え置いていたもので、
   「片方に合わせるともう片方が `<main>` を失う」はシェル外の経路にだけ器を与えて解いた。
   検出できなかった件も axe のタグ（`best-practice`）と走査面（存在秘匿の 404）で塞いだ。
4. ~~無効な任意値記法が 2 ファイル 9 行残っている~~ **解消済み**（統合段で `ScopeFilter.tsx` /
   `LoginPage.tsx` を名前付きユーティリティへ是正。`git grep '\[--color-'` は `styles.css` の注記引用を除き 0 件）。
5. ~~a11y の検査は並行タスクが実装中~~ **実測済み**（§検証記録「統合後の実測（a11y）」）。
   ただし **ダークの accent をモックの値から持ち上げた**ので、計画がダーク値を再定義した場合は
   コントラストの実測を添えて再裁定が要る。
6. **`jsx-a11y` の `components` 対応表は手で維持する。** プリミティブを足して足し忘れると、
   その部品を使う画面が静かに検査から外れる（機械検査は無い）。
7. **テーマの CSS は 4 段の順序に依存する。** 段を並べ替えると静かに壊れる
   （テストが固定しているのは純関数の巡回であって CSS の段ではない）。

## 未決事項

1. 環流 issue の起票（§計画書との差異 B・C）。**PR の段で行う。**
2. 残存する任意値記法 2 ファイル 9 行の是正。
3. ~~`NotFound` の `<main>` 入れ子の解消。~~ **完了**（［2026-09-12 追記 / #1438］。`IADR-0442`）。
4. ~~axe の走査面を 3 面から広げるか（実行時間の実測を見てから判断する）。~~
   **5 面へ広げた**（［2026-09-12 追記 / #1438］。`IADR-0442` 決定 3）。
5. 契約が出典の位置情報を持ったとき、脚注方式から本文中の印へ移すか。
