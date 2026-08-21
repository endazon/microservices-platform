---
title: 作業仕様書 — 裁定 planning#390 の追随（SharedKernel の粒度・Worker の標準構成入り）を IADR-0219 として記録する
type: spec
status: in-progress
related_ids:
  - NFR
  - ADR-0019
  - ADR-0030
  - ADR-0041
  - IADR-0056
  - IADR-0117
  - IADR-0196
  - IADR-0218
  - IADR-0219
author: implementation-agent
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (§SharedKernel の粒度・Worker の追加。2026-08-17 確定・fixed。pin 767a9d48)
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (ライブラリ標準・選定基準 1〜4)
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Result 型の外部ライブラリと SharedKernel での封じ込め)
  - planning:projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md (ユニット第一構成・決定 4)
---

# 仕様書: 裁定 planning#390 の追随を IADR-0219 として記録する（`.gitkeep` の実適用はしない）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を
> 一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（製品機能ではない）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 非機能要件（NFR）: **無採番**。本作業は「標準構成の枠の定義と、その記録の整合」を保つ文書統制であり、
  計画側の非機能要件表（`NFR-01`〜`NFR-27`）は**稼働する製品**の要件を持つ。
  `.claude/rules/traceability.md` §起点 ID の種別 の場合 2（ID 列はあるが当たる番号が無いメタ作業）に当たる。
  **この場合は環流しない。**
- 関連 ADR:
  - 計画 `12_backend-application-stack`（計画リポ）
    §`SharedKernel` の粒度・`Worker` の追加（**2026-08-17 確定 / `status: fixed`**。裁定 planning#390）
  - 計画 `ADR-0030`（計画リポ）（選定基準 1〜4）
  - 計画 `ADR-0041`（計画リポ）（Result 型の封じ込め）
  - 実装 [`IADR-0117`](../adr/IADR-0117_platform-shared-kernel-placement.md) / [`IADR-0218`](../adr/IADR-0218_gitkeep-standard-components-scope.md)（本作業が後継 `IADR-0219` で部分改定する）
- 計画書リンク: 上記（submodule pin `767a9d48`）

## 目的・背景

2026-08-17 の利用者裁定（planning#390）が、計画 `12_backend-application-stack` を改定し、
**実装側の決定を 2 つ覆した**。

| 実装側の決定 | 裁定 |
| --- | --- |
| [IADR-0218](../adr/IADR-0218_gitkeep-standard-components-scope.md) 決定 1: `SharedKernel` は `.gitkeep` の対象**外** | **対象に含める**（計画の構成図が正・**サービス単位**） |
| [IADR-0218](../adr/IADR-0218_gitkeep-standard-components-scope.md) 決定 2: `Worker` は `Api` の別形（環流不要） | **`Worker` を標準構成へ追加**（7 → **8 要素**。`Api` と**排他**） |
| [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md): サービス単位の `SharedKernel` を却下 | **サービス単位を標準構成として認める。ユニット単位（`Platform.Shared.Kernel`）とは併存** |

**本 PR は決定の記録だけを行う。`.gitkeep` の実適用（55 件）は次の波（10-3）で行う。**

## 対象範囲

- 対象:
  1. **新規 `docs/adr/IADR-0219_*.md`** — 2 つの決定を 1 本にまとめて記録する（同一裁定から出ており、
     `.gitkeep` の件数という 1 つの帰結へ収束するため）
  2. **`docs/adr/IADR-0117_*.md`** — 決定 5 の直後と §結果 フォローアップ 2 へ日付つき追記ブロック。
     `updated:` 前進・`related_ids` へ `IADR-0219` を併記。**`status` は `Accepted` のまま**
  3. **`docs/adr/IADR-0218_*.md`** — 決定 1〜4 と §結果 への追記。**あわせて `ADR-0030` の誤引用 1 件を訂正**
  4. **`docs/tech/tech-requirements.md`** — L138 の「共有カーネルはサービス単位に置かない」を併存の形へ、
     構成図（L126-130）を 8 要素へ。`updated:` 前進
  5. **`docs/adr/README.md`** — `IADR-0219` の索引行を追加
- 対象外:
  - **`.gitkeep` の実適用**（55 件 ＋ 雛形 1 件）。次の波で行う。**`src/` を 1 バイトも変更しない**
  - `planning/` 配下（計画リポの submodule）
  - `src/ai-stock-trading/`（別リポジトリ。追随は向こうの issue）
  - `.claude/rules/` と `CLAUDE.md`（必読規約の予算。1 バイトも増やさない）
  - 確定済み（`status: done`）でマージ済みの `docs/specs/`（下表で個別に除外理由を書く）
  - `IADR-0117` の**却下理由そのもの**（§検討した選択肢 の列 B・§理由 第 1 項・§コンテキスト 1）。
    **1 文字も書き換えない。** 2026-08-03 時点でその判断をした記録であり、消すと**なぜ 11 分裂を恐れたのか**が読めなくなる

## 母集合（着手前に自分で引いた結果）

**規則**（`.claude/rules/traceability.md` 規則 1〜8 ＋ `traceability.repo.md` 規則 9・10）に従い、
**誤りの側の文字列で**・**あり得る形を列挙してから**・**拡張子で絞らず**・**行フィルタを継がず**・
**軸を 1 本で終わらせず**引いた。走査は `git grep --untracked`（新規ファイルは追跡外で落ちるため）。
除外は**パスのみ**（`planning`＝submodule、`CHANGELOG.md`＝自動生成）。

**規則 8 の但し書き**: 下記の値は**本仕様書を書く前**に引いたものである。本書は検索語
（`SharedKernel`・`44`・`7 要素`・`構成要素を増やさない`・`サービス単位に置かない`）を多数含むため、
**同じコマンドを本書のコミット後に流すと各軸が本書のぶんだけ増える**。数は着手時点の値である。

### 軸 1 — 「サービス単位に置かない」（誤りの側の文字列。裁定に真正面から反する形）

```
git grep --untracked -n "サービス単位に置かない" -- . ':(exclude)planning'   → 4 行
```

| 行 | 是正するか |
| --- | --- |
| `docs/tech/tech-requirements.md:138` | **する**（対象 4）。live な技術要件書であり、裁定に真正面から反する |
| `docs/adr/IADR-0218_...:109` | **しない**（本文の引用）。ただし決定 1 へ追記ブロックを置き、現行値を示す（対象 3） |
| `docs/specs/20260803_issue-455_backend-application-standard.md:100` | **しない**（下表・凍結） |
| `docs/specs/20260817_issue-455_gitkeep-standard-components.md:230` | **しない**（下表・凍結） |

派生軸「サービス単位」全件（44 行）も引いた。是正対象は上記のほか無い
（デプロイ単位・サブモジュール境界・統合テスト層など、共有カーネルと無関係な用法である）。

### 軸 2 — 「7 要素」「7 つ」「7要素」「7つ」（要素数）

```
git grep --untracked -n "7 要素" -- . ':(exclude)planning'                   → 15 行
git grep --untracked -n "7 つ" -- . ':(exclude)planning'                     →  9 行
git grep --untracked -n -e "7要素" -e "7つ" -- . ':(exclude)planning'        →  0 行（EXIT=1）
```

是正対象は `docs/adr/IADR-0218`（タイトル・本文 8 行）と `docs/adr/README.md:274`（索引行）。
**タイトルと本文の「7 要素」は書き換えない**（2026-08-17 以前の決定の記録である）。
追記ブロックで「現行は 8 要素」と示す。
`docs/specs/20260804_issue-502_...:302`（SC-01 の 8 要素/7 要素）・
`docs/specs/20260816_issue-817_...:85`（`APISERVER_OIDC_TOKENS` の 7 要素）は**別物**（除外）。

### 軸 3 — 「44」（`.gitkeep` の旧件数）

**裸の `44` は issue 番号・ポート・run 番号に大量に当たる**（全リポで 259.8KB）。
そこで**あり得る計数形を列挙してから**引いた（規則 2。行フィルタで後ろから削ってはいない）。

```
git grep --untracked -n -E "44 ?(件|ファイル|個|ディレクトリ|になる|へ|は|を|、|\)|）)|= ?44|→ ?44|44 ?→" \
  -- . ':(exclude)planning' ':(exclude)CHANGELOG.md'
```

`.gitkeep` の件数を指す 44 は次のとおり。

| 行 | 是正するか |
| --- | --- |
| `docs/adr/IADR-0218_...` L124・164・166・169・206・223・236・240（8 行） | **本文は書き換えない**。決定 3 へ追記ブロックで「現行は 55」を示す（対象 3） |
| `docs/adr/README.md:274`（`IADR-0218` の索引行） | **する。索引は現行値を示す面である** —— 初版の値（7 要素・4 要素 × 11 = 44）を残したうえで、**現行（8 要素・5 要素 × 11 = 55）を同じセルへ併記**する。上限 200 字と本体 `title:` との LCS 12 以上を機械で確認した（実測 195 字 / LCS 75） |
| `docs/specs/20260817_issue-455_gitkeep-standard-components.md` L182・191・259・261 | **しない**（下表・凍結） |

無関係な 44（除外）: `IADR-0211` / `src/knip.jsonc` の knip 件数（648 → 44）、
`IADR-0138` / `src/coverage-floor.json` の run_number 1144、
`IADR-0140` / `IADR-0144` / `IADR-0116` ほかの issue・PR 番号（#144 / #244 / #344 / #444 / #544）、
`docs/specs/20260805_issue-504_...` の変異試験 44 件、`deploy/keycloak/...` のポート 4466。

### 軸 4 — 「構成要素を増やさない」（`ADR-0030` の誤引用）

```
git grep --untracked -n "構成要素を増やさない" -- . ':(exclude)planning'     → 4 行
git grep --untracked -n "構成要素" -- . ':(exclude)planning'                 → 12 行
git grep --untracked -n -e "選定基準 2" -e "選定基準2" -- . ':(exclude)planning' → 8 行
```

| 行 | 是正するか |
| --- | --- |
| `docs/adr/IADR-0218_...:38`（`plan_refs` 直後の起点・関連） | **する**（対象 3・誤引用の訂正） |
| `docs/adr/IADR-0218_...:80`（案 γ の比較表の評価軸） | **する**（同上） |
| `docs/adr/IADR-0218_...:156`（案 γ の代償） | **する**（同上） |
| `docs/specs/20260817_issue-455_gitkeep-standard-components.md:49` | **しない**（下表・凍結） |
| `docs/adr/IADR-0218_...:123`・`:237`・`:241` の「構成要素」 | 誤引用ではない（一般名詞）。触らない |
| `IADR-0216` の「選定基準 2（標準機能優先）」 | **正しい引用**。触らない |

**実測（自分で走査した）**: `ADR-0030` に「構成要素を増やさない」は **0 件**、「構成要素」自体が **0 件**。
同 ADR の選定基準 2 は「**標準機能優先**」である（§実測 に生出力）。

> **［着手後の訂正 / 親からの指摘］作業指示は「156 行目付近」の 1 件だけを挙げていたが、
> それは母集合の規則 7 が禁じる「指摘された 1 件だけ直す」形である。**
> 指摘を受けて**行番号を信用せず自分で全走査し直した**（除外は `':!planning' ':!src/ai-stock-trading'`）。
>
> ```
> git grep -n --untracked "構成要素を増やさない" -- . ':!planning' ':!src/ai-stock-trading'
> git grep -n --untracked "選定基準 2"           -- . ':!planning' ':!src/ai-stock-trading'
> git grep -n --untracked "選定基準2"            -- . ':!planning' ':!src/ai-stock-trading'   → 0 件（自己参照 1 行のみ）
> ```
>
> **結果、誤引用は `IADR-0218` に 3 箇所あった**（起点・関連 / 案 γ の比較表の評価軸 / 案 γ の代償）。
> **さらに §結果 の「ADR-0030 選定基準 2 が嫌う『構成要素を増やす』には当たらない」も同じ誤引用**であり、
> **合わせて 4 箇所**である。**上の表は指摘前に自分で引いて 3 箇所＋§結果 を挙げており、修正済みである。**
> **`IADR-0218` の外に是正対象は無い。**
>
> | 走査で出た他の行 | 扱い |
> | --- | --- |
> | `docs/specs/20260817_issue-455_gitkeep-standard-components.md:49` | **触らない。`status: done` を実際に確認した**（同ファイル frontmatter・`updated: 2026-08-17`）。コミット `26e45293` でマージ済みであり、**走査基準つきの過去の測定記録**である |
> | `docs/adr/IADR-0216_...:33`・`:93` | **正しい引用**（「選定基準 2（標準機能優先）」）。触らない |
> | `docs/adr/IADR-0117_...:124` | **触らない。**「過度な共通化は避ける」を**計画構成図の注記**として引いており、出典は正しい（`ADR-0030` の記述としては引いていない）。実際に読んで確認した |
> | 本仕様書自身の行 | 自己参照（規則 8） |

### 軸 5 — `SharedKernel` / 「共有カーネル」（規則 5。語を変えると出続ける）

```
git grep --untracked -c "SharedKernel" -- . ':(exclude)planning'   → 10 ファイル
git grep --untracked -c "共有カーネル" -- . ':(exclude)planning'   → 18 ファイル
```

裁定に反する記述は **`docs/tech/tech-requirements.md:138` の 1 件のみ**である。
残りは次のいずれかで、**併存の裁定を受けても誤りにならない**ため除外した。

| 除外したもの | 理由 |
| --- | --- |
| `src/README.md:85`・`templates/unit-template/README.md:96`・`docs/how-to/adding-a-unit-submodule.md:42`・`docs/adr/README.md:173`・`docs/adr/IADR-0056:87` | いずれも**ユニット単位の `Platform.Shared.Kernel`**（ユニット外参照 2 → 3）の話。裁定は**併存**を認めたので有効なまま |
| `scripts/check-backend-libraries.js`（36 行）・`scripts/README.md:24`・`scripts/scripts.repo.test.js:1420`・`.github/workflows/ci.yml:270`・`src/Directory.Packages.props:86`・`templates/.../SampleService.Domain.csproj` | `Platform.Shared.Kernel` の**許可リスト検査**。per-service の枠を認めても検査対象は変わらない（実体が 1 つも無い） |
| `docs/adr/IADR-0196` / `IADR-0217` | Result ライブラリ許可リスト・Wolverine codegen。粒度に触れていない |
| `docs/specs/20260815_issue-500_...`（24 行）・`20260816_issue-455_wolverine-codegen-mode.md` | 凍結（下表） |

### 軸 6 — `.gitkeep`（適用面の全走査）

```
git grep --untracked -n "gitkeep" -- . ':(exclude)planning'   → 48 行
```

`scripts/kit-sync-classification.json` の 15 件・`templates/unit-template/README.md` の
フロント雛形 11 件は**別の面**（`docs/<種別>/` とフロント features）であり、
バックエンド標準構成とは階層が違う。**本作業では触らない**（適用も次の波）。

### 軸 7 — 「55」（**新しい値。規則 10 = 是正で新たに誤りになる記述を引き直す**）

```
git grep --untracked -n -E "55 ?(件|ファイル|個|ディレクトリ|になる|へ|は|を|、)|= ?55|→ ?55|55 ?→" \
  -- . ':(exclude)planning' ':(exclude)CHANGELOG.md'   → 12 行
```

`.gitkeep` の件数としての 55 は `IADR-0218:60`（素朴な計算）・`:124`（再評価条件）と
`docs/specs/20260817_issue-455_...:259` の 3 行のみで、**いずれも「もし裁定がこうなれば 55」という予測**
である。**予測が的中した**ので、`IADR-0218` の該当箇所へ「発火した」と追記する。
他の 55（`IADR-0140` の型 1 = 55 件、`check-cross-repo-refs` の OK 555 件、issue #455）は無関係。

### 軸 8 — 「8 要素」「8 つ」（**新しい値**）

```
git grep --untracked -n -e "8 要素" -e "8要素" -e "8 つ" -- . ':(exclude)planning'   → 6 行
```

`.gitkeep` / 標準構成の文脈は `IADR-0218:81`（案 γ の比較表「44（＋計画改定後に表を 8 要素へ）」）のみ。
**この予測も的中した。** 他 5 行は無関係（規則の数・SC-01 の要素数・ファイル数）。

### 軸 9 — `Worker`（標準構成の文脈になり得る散文）

```
git grep --untracked -c "Worker" -- . ':(exclude)planning' ':(exclude)CHANGELOG.md'   → 119 ファイル
```

`src/**` の大半は**プロジェクト名・名前空間**（`ConversionService.Worker` / `IngestionService.Worker`）であり、
標準構成の教義を述べていない。教義を述べているのは `docs/adr/IADR-0218`（21 行）・`docs/adr/README.md:274`・
`src/README.md`（`<ServiceName>.<Api|Worker>` の形。**既に排他の形で書かれており是正不要**）・
`docs/tech/tech-requirements.md:136`（`Tests` の実名。**正しいので触らない**）である。

### 凍結して触らないもの（`status: done` でマージ済みの `docs/specs/`）

| ファイル | 該当軸 | 除外理由 |
| --- | --- | --- |
| `docs/specs/20260817_issue-455_gitkeep-standard-components.md`（`status: done`） | 軸 1・2・3・4・5・6・7 | **確定済み仕様書は書き換えない**（`traceability.repo.md`「確定済みの `docs/specs/` は書き換えない」）。当時の走査と決定の記録であり、**現行値は `IADR-0219` が持つ** |
| `docs/specs/20260817_planning-pin-767a9d48.md`（`status: done`） | — | 同上（pin 前進の記録） |
| `docs/specs/20260815_issue-500_result-type-adr-0041-followup.md` | 軸 5 | 同上。Result ライブラリ許可リストの記録で、粒度に触れていない |
| `docs/specs/20260803_issue-455_backend-application-standard.md`（`status: in-progress`） | 軸 1・5 | **`in-progress` だが本作業の PR の仕様書ではない**（別作業の作業中仕様書）。射程を跨いで書き換えると、その作業の記録が壊れる。**現行値は `IADR-0219` が持つ** |
| `docs/specs/20260816_*` ほか | 軸 5・6 | 別作業の記録。誤りになる記述が無い |

### 実測（走査ではなく数え直した導出値。規則 7・10）

`.gitkeep` の件数は**走査ではなく計算し直した**。自分で実測した現状は次のとおり。

```
git ls-files "src/*/backend/Services/*/src/*/*.csproj"   → 11 行（＝ 11 サービス。9 が .Api / 2 が .Worker）
git ls-files "src/*/backend/Services/*/tests/*/*.csproj" → 11 行（Tests は 11/11 実体あり）
git ls-files "src/**/.gitkeep"                            →  0 件
```

| 要素 | 実体 | `.gitkeep` |
| --- | --- | --- |
| `Api` / `Worker`（**排他**） | 11 / 11（`Api` 9・`Worker` 2） | **0** |
| `Application` | 0 / 11 | 11 |
| `Domain` | 0 / 11 | 11 |
| `Infrastructure` | 0 / 11 | 11 |
| `Contracts` | 0 / 11 | 11 |
| `SharedKernel` | 0 / 11 | **11**（裁定により新たに対象） |
| `Tests` | 11 / 11 | 0 |
| **計** | | **55** |

**11 サービスの内訳**（走査で得た実名）: platform = `AuthorizationService` / `LlmGateway`。
knowledge = `AiAnalysisService` / `ConversionService` / `DashboardService` / `DataSourceService` /
`DocumentService` / `FeedbackService` / `IngestionService` / `RetrievalService` / `WikiService`。

**`Api` / `Worker` の排他は実測で成立している** —— `git ls-files "src/*/backend/Services/*/src/*/*.csproj"` が
**ちょうど 11 行**を返し、内訳は `.Api` 9 件 / `.Worker` 2 件（`ConversionService` / `IngestionService`）。
**両方を持つサービスは 0 件**である。

**55 スロットとも実体は 0 件である。** 既存の `Contracts` / `Infrastructure` を名乗るプロジェクト
（`Platform.Shared.Contracts` / `Platform.Shared.Infrastructure` / `Knowledge.Contracts` 等）は
**ユニット階層 `src/*/backend/Shared/` にあり、サービス配下ではない** ——
**裁定が認めた「per-service と per-unit の併存」の形そのもの**であり、per-service の枠を埋めない。

雛形 `templates/unit-template/backend/Services/SampleService/`: 8 要素中 6 つが実体
（`Api` / `Application` / `Contracts` / `Domain` / `Infrastructure` / `Tests`）。`Worker` は `Api` と排他で対象外。
**残る `SharedKernel` が新たに対象になり、雛形は 0 件 → 1 件。**

## 設計

### IADR-0219 に書くこと

1. **`SharedKernel` の粒度はサービス単位**。per-service の枠を標準構成として認める。
   **ユニット単位の `Platform.Shared.Kernel` は併存として有効**（消さない）。置き分けは
   **サービス単位 = 自サービスに閉じた共通基底 / ユニット単位 = 境界をまたいで同一性が要る型（契約に載る `Result` / `Error`）**
2. **`Worker` を標準構成へ加える（8 要素）。`Api` と排他。** 区別の軸は**ホストの主目的**であり、
   **HTTP 面を持つことは `Worker` であることと矛盾しない**
3. **`.gitkeep` は 55 件（＋雛形 1 件）**
4. 典拠は**裁定 planning#390 と計画 `12_backend-application-stack` の 2026-08-17 改定**
5. **`IADR-0117` / `IADR-0218` の後継**であること（旧 ID を残し後継を併記）
6. **裁定が示した、実装側が見落としていた 2 点**を記録する

### 追記の書式

`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」に従う。
**旧 ID を残し後継を併記**、frontmatter の ID リストは**後継 ID を項目として併記**（説明を混ぜない）、
決定を変える追記は**日付つき追記ブロック `［YYYY-MM-DD 追記 / #NNN］`**、`updated:` を前進させる。

**`status` は両者とも `Accepted` のまま**とする。**先例**: [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) は決定 3 が
[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) に部分改定された後も `Accepted` を維持し、本文へ日付つき追記ブロックを置き、
`related_ids` へ `IADR-0117` を併記している（L85-95・L193-195 を実際に読んで確認した）。

## 受け入れ基準

- [ ] `docs/adr/IADR-0219_*.md` が新設され、決定 1〜3・典拠・後継関係・見落とし 2 点をすべて含む
- [ ] `IADR-0117` の**却下理由（§検討した選択肢 列 B・§理由 第 1 項・§コンテキスト 1）が 1 文字も変わっていない**
- [ ] `IADR-0117` 決定 5 の直後と §結果 フォローアップ 2 に追記ブロックがあり、`status` は `Accepted`
- [ ] `IADR-0218` の決定 1・2・3・4 と §結果 に追記があり、**結論が生き残るもの（決定 2・4）と根拠だけ入れ替わるものを書き分けている**
- [ ] `IADR-0218` の `ADR-0030` 誤引用 3 箇所が訂正されている
- [ ] `docs/tech/tech-requirements.md` の L138 が併存の形になり、構成図が 8 要素になっている
- [ ] `docs/adr/README.md` に `IADR-0219` の行があり、**タイトル 200 字以内・本体 `title:` との LCS 12 以上**
- [ ] `src/` の差分が 0（`.gitkeep` を 1 つも置いていない）
- [ ] `planning/` の差分が 0
- [ ] `.claude/rules/` と `CLAUDE.md` の差分が 0
- [ ] 検査器がすべて通る（下記）

## テスト方針

機械検査で担保する。**終了コードをパイプで終端せず** `cmd > log 2>&1; echo "EXIT=$?"` の形で取り、
**終了コードではなく判定行を読む**（`skip` と `pass` はどちらも EXIT=0）。

```
node scripts/check-doc-links.js
node scripts/check-doc-type-vocabulary.js
node scripts/check-cross-repo-refs.js
node scripts/check-plan-id-qualification.js
node scripts/check-adr-numbering.js
node scripts/check-reading-budget.js
node scripts/check-kit-sync.js
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
```

`scripts/scripts.repo.test.js` の**単体実行はしない**（#797 / [IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md) のガードで exit 1 になる仕様）。

## 計画書との差異

- 差異: **なし**。本作業は計画 `12_backend-application-stack`（2026-08-17 改定・`fixed`）へ
  実装側を合わせる作業であり、`.claude/rules/adr.md` の大原則「実装が先行して乖離したら実装を計画へ合わせる」に従う。

## 未決事項

1. **追記ブロックの起票 ID**。本波に固有の issue 番号は与えられていない。`IADR-0117` / `IADR-0218` が
   ともに起点として引く **#455**（バックエンドアプリケーション層標準）を採り、裁定は `planning#390` として
   併記する。**別の番号を採るべきなら人が裁定する。**
2. **`.gitkeep` の実適用（55 件 ＋ 雛形 1 件）は次の波（10-3）で起票する。** 本作業では行わない。
3. `src/ai-stock-trading` への追随は向こうのリポジトリの issue で行う（計画の規則は及ぶ）。
