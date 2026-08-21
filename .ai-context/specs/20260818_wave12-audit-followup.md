---
title: 作業仕様書 — 波 12 末クロス監査の是正（テレメトリ opt-out・CHANGELOG 実体参照・キット分類・雛形 CI の IADR 化・仕様書の追記）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0025
  - ADR-0038
  - IADR-0060
  - IADR-0106
  - IADR-0115
  - IADR-0125
  - IADR-0141
  - IADR-0179
  - IADR-0183
  - IADR-0192
  - IADR-0204
  - IADR-0209
  - IADR-0224
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md (§非LLM外部送信の統制: 既定テレメトリのオプトアウト・status: fixed)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR 表 NFR-01..27。当たる番号が無いことの確認先)
  - planning:docs/ai-implementation-workflow-guide.md (§フェーズ末監査・§11 複数実装リポのパリティ)
related_specs:
  - "./20260818_issue-835_claude-review-planning-grep.md"
  - "./20260818_issue-824_devcontainer-net10-node22.md"
  - "./20260818_issue-830_template-backend-ci-build.md"
related_adrs:
  - IADR-0224
  - IADR-0060
  - IADR-0204
  - IADR-0141
  - IADR-0179
---

# 作業仕様書: 波 12 末クロス監査の是正

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし
- ユースケース（UC）/ 画面（SC）: 該当なし
- 非機能要件（NFR）: **無採番**。判定は下記「無採番 NFR の場合判定」を見ること。
- 計画 ADR: `ADR-0025` / `ADR-0038`（IADR-0106 の引用の但し書き）、
  計画 `06_technical/08_data-egress-policy.md`（テレメトリ opt-out・`status: fixed`）

## 起点

波 12 末クロス監査（`adr-guardian` / `traceability-auditor` をフレッシュな文脈で実走、および
#859 の `claude-review`）が挙げた 7 件。**本作業では他人の測定を転記せず、全件を自分で引き直した**
（引き直しの結果、指摘の数値が誤っていたものが 2 件ある。下記「指摘の側が誤っていたもの」）。

## 無採番 NFR の場合判定（`.claude/rules/traceability.md` 場合 1 / 場合 2）

着手前に計画の NFR 表を自分で読んだ（`planning/projects/microservices-platform/02_requirements/01_requirements.md`）。

```console
$ grep -c '^| NFR-' planning/projects/microservices-platform/02_requirements/01_requirements.md
```

`NFR-01`〜`NFR-27` は 性能(01-04) / 可用性(05,06) / スケーラビリティ(07,08,27) /
セキュリティ(09-18) / 運用・保守(19-21) / 拡張性(22-26) であり、**27 件とも稼働する製品の要件**である。
本作業は規約整備・文書統制・キット分類・CI 検証方式の記録＝**メタ作業**であり、当たる番号が無い。
最も近い `NFR-20`（デプロイ）は「サービス単位で独立デプロイ、ロールバック可能／GitOps（ArgoCD）」で
あり、開発環境・文書統制とは軸が違う。**無理に近い番号を付けない**（[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 2）。

→ **場合 2**。したがって**環流しない**。

## 母集合（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1 の規則 1〜6 ／ `traceability.repo.md` 規則 9・10）

走査は `git grep`、**拡張子で絞らず**、パス除外は `':!planning' ':!src/ai-stock-trading'` のみ
（いずれも submodule で編集禁止・射程外）。

### 軸 1: `TELEMETRY` / `telemetry`（誤りの側 ＝ opt-out が無い面）

追跡下の全ファイルを走査した。**opt-out の設定点は `.devcontainer/devcontainer.json:20` の 1 件だけ**で、
`scripts/setup.sh` には 0 件。他のヒットは `otel-collector` / `opentelemetry` パッケージ名と、
確定済み仕様書の記録であり、いずれも設定点ではない。

### 軸 2: `&lt;` / `&gt;`（HTML エンティティの混入面）

`node scripts/gen-changelog.js` の生出力に対して `grep -c '&lt;'` = **1**（`e3cb1075` の 1 行のみ）。
追跡下のコミット件名も走査し、同型は本件 1 件のみ。

### 軸 3: キット `repo-template/scripts/setup.sh` との差分（分類の再判定）

`diff` の実測は下記「3. キット分類」。

### 軸 4: キットの「4 サブコマンド」コメント面

**ここで指摘の数が誤っていた**（下記「指摘の側が誤っていたもの」①）。

### 引いたが除外したもの（と理由。規則 6:「黙って除外した」ことでも事故は起きる）

| 除外したもの | 理由 |
| --- | --- |
| `planning/`（submodule） | ブリーフの禁止事項。キット側の是正は**起票の申し送り**に回す（本 PR では触らない） |
| `src/ai-stock-trading`（submodule） | 同上・射程外 |
| `CHANGELOG.md` | **生成物**（`scripts/gen-changelog.js` ＋ `.github/workflows/changelog.yml` が自動更新）。手で書き足さない（`CLAUDE.md`「補助成果物の自動生成」）。是正は生成元（`changelog-overrides.json`）で行う |
| `src/pnpm-lock.yaml` | **生成物**（pnpm が書く lockfile）。文言の追随対象ではない |
| 確定済み `docs/specs/` の**既存本文** | `traceability.repo.md`「確定済みの `docs/specs/` は書き換えない」。**追記ブロックのみ**可 |
| `feedback/*` | 凍結の射程内（①＝状態欄の言い直しは不可）。本作業に該当する追記は無い |
| `.claude/rules/*` / `CLAUDE.md` | 必読予算 51,200B に対し現在 49,885B。**増やさない**（ブリーフの禁止事項） |
| キット配布物（分類 A。`.claude/rules/traceability.md` / `scripts/scripts.test.js` ほか） | 直接編集しない（[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)） |

## 指摘の側が誤っていたもの（引き直しで判明。**直さずに記録する**）

### ① キットの「4 サブコマンド」は **3 箇所ではなく 4 ファイル / 7 箇所**

ブリーフ（および監査指摘）は「キットの `.claude/settings.json` と 2 つの `*.example.yml`」＝ 3 箇所と
述べるが、自分で引くと **`HOWTO.md:262` にも同じ記述がある**。

```console
$ grep -rlo "4 サブコマンド" planning/tools/impl-handoff-kit/
planning/tools/impl-handoff-kit/HOWTO.md
planning/tools/impl-handoff-kit/repo-template/.github/workflows/claude-coding.example.yml
planning/tools/impl-handoff-kit/repo-template/.github/workflows/claude-code-review.example.yml
planning/tools/impl-handoff-kit/repo-template/.claude/settings.json

$ grep -rno "log / show / diff / ls-tree" planning/tools/impl-handoff-kit/ | wc -l
7
```

**4 ファイル・7 箇所**が正しい（`claude-coding.example.yml:201` の `--append-system-prompt` 本文に 2 回、
`claude-code-review.example.yml:458` にも 1 回ある）。申し送る issue の本文はこの数で書く。
なお**キットの許可リスト本体は既に `grep` を持つ**（`settings.json:17` ほか）ので、乖離は**コメント面だけ**である。

### ② 仕様書 6-3 の軸 2 は「40 件」では再現しない（正しくは **41 行 / 26 ファイル**）

軸 7 は「生成物 2 件を黙って除外していた」で説明がつくが、**軸 2 は説明がつかない**（下記 6-3）。

## 直すもの

### 1. `scripts/setup.sh` に `DOTNET_CLI_TELEMETRY_OPTOUT=1` を置く

計画 `08_data-egress-policy.md`（`status: fixed`）が既定テレメトリの opt-out を課す。
`.devcontainer/devcontainer.json` の `remoteEnv` は **devcontainer で起動したときだけ**効くのに対し、
#824 が足した SDK 導入ブロックは**素のコンテナ**を明示的に狙い、そこで `dotnet --version` と
restore を初回実行する（＝テレメトリの対象）。**確定制約への追随であり逸脱ではないので新 ADR は不要。**

**ブリーフとの差異（意図的）**: ブリーフは「PATH 追加の直後に 1 行」と指示するが、PATH 追加は
**2 箇所**あり（既存 `$HOME/.dotnet` を拾う経路と、新規導入した経路）、さらに**素のコンテナに
最初から dotnet がある**第 3 の経路では PATH 追加自体が起きない。3 経路すべてを覆うため、
**.NET セクションの先頭・最初の `dotnet` 実行より前に 1 行だけ**置く。行数は同じ 1 行で、
覆う範囲だけが広い。

### 2. `scripts/changelog-overrides.json` へ `e3cb1075` の remap を 1 件追加

`desc` のみ差し替え、`type` / `scope` は元の値を保つ（先例 `3441861` と同型）。
**desc 中の山括弧はバッククォートで囲む** —— `CHANGELOG.md` も GitHub が描画する面であり、
素の山括弧を入れ直すと同じ壊れ方を再生産する。

### 3. `scripts/kit-sync-classification.json` の `scripts/setup.sh` の理由を書き直す

`check-kit-sync.js` は理由文の中身を検査しない（実測: 同スクリプトに種別トークンの検証が無い）ため、
この陳腐化は機械に見えない。値域だけは `scripts.repo.test.js:5657` が `^([1-5]|X)\. ` を強制する。

**[IADR-0204](../adr/IADR-0204_kit-catchup-deferral-with-expiry-ratchet.md) 決定 2 の 3 点で自分で再判定した**（対象は検査器ではないので読み替えを明記する）:

| 決定 2 の観点 | setup.sh への読み替えと実測 |
| --- | --- |
| 1. 違反入力に対する検出結果 | 検出器ではない。読み替え＝通常のリポジトリでの挙動。キット版は「dotnet が無ければ restore をスキップ」、本リポ版は**その手前に「無ければ入れる」を足した**（54 行） |
| 2. 空・読めない・未充填のときの終了コードの向き | **両版とも fail-open**（`set -u` のみ・`set -e` を使わない）。本リポのデルタも fail-open を保つ（導出できない / curl が無い / DL 失敗のいずれも `log` して継続）。**向きは変わっていない** |
| 3. 本リポにしか無いモジュールへの結線 | **有り**。デルタは `src/Directory.Build.props` の `<TargetFramework>` を読んでチャネルを導出する。これはユニット第一構成の共通 props ＝本リポ固有の実体である |

```console
$ diff planning/tools/impl-handoff-kit/repo-template/scripts/setup.sh scripts/setup.sh
10a11,64      # ← SDK 自動導入ブロック 54 行（#824）
42,44c96,99   # ← 計画 pin 鮮度セクションの文言差
49,52c104,105 # ← 同上（PIPESTATUS 注記の文言差）
```

**判定 = 種 2（技術スタックとその CI 配線）。X から移す。** 根拠:

- デルタの実体は `dotnet-install.sh` / `--channel` / `Directory.Build.props` の `<TargetFramework>` で
  **.NET に固有**であり、他スタックのリポジトリへそのまま配れない。種 2 の定義に直接当たる。
- キット版 `setup.sh` 自身が「技術非依存の安全設計」「**スタックに合わせて必要なセットアップを
  追記すること（既定は C#/.NET 例）**」と述べており、**このデルタはキットが招いている追記**である。
- 種 5（キットが選択・追記を委ねている欄）は採らない。種 5 は「**空欄・空配列・未選択**で配り各リポが
  必ず埋めるもの」だが、キットは動く .NET 例を既に配っており空欄ではない。
- X（＝環流すべき汎用改善）も採らない。**発想**（「SessionStart の素のコンテナには toolchain が無い」）は
  汎用だが、**コードは汎用ではない**。X は「恒久的に正しいデルタを置かない」欄であり、
  スタック固有で恒久的に正しいこのデルタは X の性質と合わない。
  ただし**発想の側はキットへ環流する価値がある**ので、理由文に汎用化の余地として明記する。
- 「環流済み」は**計画 pin 鮮度デルタについては真**（キット版 `setup.sh:42` が同節を持つ）だが、
  **SDK 導入デルタについては偽**。理由文で射程を分ける。

### 4. 新規 [IADR-0224](../adr/IADR-0224_template-backend-ci-build-by-staged-copy.md)（雛形 backend を CI で検証する方式）

[IADR-0060](../adr/IADR-0060_submodule-unit-operations.md) 決定 3「テンプレートは本リポジトリのビルド対象ではない（`src/` 外・どの slnx にも
含めない）」の**括弧内 2 条件は保たれる**（一時複製をビルドする）が、**位置づけそのものは実質的に変わる**。
同型の先行例（雛形 frontend を CI 対象に入れた #801）は [IADR-0209](../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md) として記録されている。
**決定の記録が無いまま位置づけを変えない。**

記録する内容: 複製ビルド方式 / `.sample` 除去 / `--artifacts-path` / **件数下限**での判定 /
`dotnet format` を含めない判断 / `build-and-test` へ相乗りしない理由 / **ASCII 前提の既知の限界**。

`IADR-0060` へは**日付つき追記のみ**（本文は書き換えない）。`docs/adr/README.md` へ索引行を追加する。

### 5. `docs/adr/IADR-0106_rag-answer-sonnet-5.md:38` へ引用の但し書き

引用は `ADR-0025:33` の逐語であり**今も正確**（本文は書き換えられていない）。ただし `ADR-0025` 自身が
2026-08-02 の改訂注記で「`ADR-0038` が『最難関=Fable 5』を部分改定する」と述べている。
**引用は消さず**、日付つき追記で「引用当時の記述であり `ADR-0038` により改定されている」旨を添える。
`updated:` を前進させる。

### 6. 仕様書 3 本への日付つき追記（**既存本文は書き換えない**）

#### 6-1. `20260818_issue-835_claude-review-planning-grep.md`

前半（着手時に書いた部分）が、**同じ PR の終盤で実際に起きたこと**と矛盾したまま残っている。
末尾 `:450-492` には「`.claude/settings.json` の追随（利用者の許可を得て実施）」節が既にあり、
**そこだけが正しい**。実測:

```console
$ STRICT_AI_WORKFLOW_CONFIG=1 node scripts/check-ai-workflow-config.js; echo "EXIT=$?"
AI ワークフロー設定チェック: 2 件を検査
✓ AI ワークフローのツール許可設定に問題なし
EXIT=0

$ git show --stat e3cb1075   # .claude/settings.json が含まれる
 .claude/settings.json                    |  5 +-
 .github/workflows/claude-code-review.yml | 15 +-
 .github/workflows/claude-coding.yml      | 13 +-
 ...835_claude-review-planning-grep.md    | 492 +++++
```

矛盾する箇所: `:303`（受け入れ基準 3）/ `:313`（§未解決の阻害要因の見出しと本文）/ `:382`（検証結果表）/
`:294`（キットは既に正しい）/ `:256`（除外表）/ `:436-448`（規則 10 の引き直し）。

**母集合の時点混在（規則 8）**も自分で引き直した。4 軸を `3ad5ad15`（着手前）と `e3cb1075`（着地）の
両方で引く:

| 軸 | 走査文字列 | `3ad5ad15` | `e3cb1075` | 差の実体 | 仕様書の記載 |
| --- | --- | ---: | ---: | --- | --- |
| 1 | `git -C planning` | 28 | 29 | 仕様書自身 | **29（着地）** |
| 2 | `ls-tree` | 21 | 22 | 仕様書自身 | **22（着地）** |
| 3 | `allowedTools` | 22 | 23 | 仕様書自身 | **22（着手前）** |
| 4 | `notApplicable` | 10 | 11 | 仕様書自身 | **10（着手前）** |

**4 軸とも増分は仕様書自身の 1 行（自己参照）だけ**であり、「着手前 N → 自己参照 1 → 着地 N+1」が
きれいに成立する。にもかかわらず**記載は軸 1・2 が着地、軸 3・4 が着手前**で混在している。

`:294`「キットは既に正しい」は**許可リスト本体については真、コメント面については偽**
（上記「指摘の側が誤っていたもの ①」。4 ファイル / 7 箇所）。射程を限定する追記を入れる。

#### 6-2. `20260818_issue-824_devcontainer-net10-node22.md`

- `plan_refs` が `../../CLAUDE.md` を指し、**計画リポを 1 つも指していない**
  （`docs/README.md` 運用ルール 4 違反）。追記で正しい計画書リンクを補う。
- **無採番 `NFR` の場合判定の記録が無い**（レーン A / D の仕様書は [IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) を引いて場合 2 と明記）。
  本作業で計画の `NFR-01..27` を読んで**場合 2** と判定し、根拠つきで追記する。
- 同 PR で `scripts/setup.sh` を触ったのに**キット分類を引き直していない**（規則 10 の取りこぼし）。
  本 PR の 3 がその是正であることを記録する。

#### 6-3. `20260818_issue-830_template-backend-ci-build.md`

軸 2・軸 7 を `3ad5ad15` 時点で自分で引き直した:

| 軸 | 走査文字列 | 仕様書の記載 | 引き直し（`3ad5ad15`） | 判定 |
| --- | --- | --- | --- | --- |
| 1 | `ビルド対象外` | 13 件 | **13 行** / 10 ファイル | 再現（行数） |
| 2 | `ビルド対象` | 40 件 | **41 行** / 26 ファイル | **再現しない** |
| 7 | `unit-template`（ファイル単位） | 41 ファイル | **43 ファイル** | 生成物 2 件の除外で 41 |

軸 7 の 43 − 41 = 2 は **`CHANGELOG.md` と `src/pnpm-lock.yaml`**（いずれも生成物）である。
除外自体は正当だが**書かれていなかった**（規則 6）。追記で明示する。

軸 2 の 40 は再現しない。試した変種と結果（いずれも 40 にならない）:

```console
$ git grep -In "ビルド対象" 3ad5ad15 -- ':!planning' ':!src/ai-stock-trading' | wc -l   # 41
$ git grep -Il "ビルド対象" 3ad5ad15 -- ... | wc -l                                     # 26（ファイル数）
$ git grep -n  "ビルド対象" 3ad5ad15 -- ... | wc -l                                     # 41（-I なし）
$ git grep -In "ビルド対象" 3ad5ad15 -- ... ':!CHANGELOG.md' ':!src/pnpm-lock.yaml'      # 41（生成物除外）
$ git grep -In "ビルド対象" 3ad5ad15 -- ... ':!templates'                                # 39
```

**±1 の食い違いの原因は特定できなかった。** 追記では**正しい実測値（41 行 / 26 ファイル）と
走査コマンドを書き**、「40 は再現しない・原因未特定」と明記する（数を黙って直さない）。

### 8. `IADR-0112` が実在しない「決定 4」を有効と宣言している（レーン C 監査）

**自分で両方の形で引き直した**（規則 2:「あり得る形をすべて列挙してから引く」。
空白ありの `決定 4` だけで引くと半角の `決定4` を取りこぼす）:

```console
$ grep -n '^### 決定' docs/adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md
156:### 決定 1: 報告書を種別ごとの用途へ分離する
172:### 決定 2: `Models` / `NonZdrModels` は変更しない
182:### 決定 3: `trade-decision` を `claude-sonnet-5` へ改定し、…
        ← 決定は 1〜3 のみ

$ grep -n -e '決定 4' -e '決定4' docs/adr/IADR-0112_*.md
137:> 決定1 の…週報/日報の割当・決定3・決定4 は有効である。      ← 既存（#850 より前から在る）
150:> **決定 1 の…決定 3・決定 4 は引き続き有効である。**          ← #850 の追記が転記した
152:> **同じ改定は [[IADR-0113]] 決定 2・決定 4 の前提も覆すため…** ← 正しい（IADR-0113 は決定 4 を持つ）

$ grep -n '^### 決定' docs/adr/IADR-0113_*.md
137:### 決定 1: … / 151:### 決定 2: … / 161:### 決定 3: … / 166:### 決定 4: …   ← 4 つある
```

**137 行も 150 行も書き換えない。** `docs/adr/` の本文・既存追記は書き換えず、
**新しい日付つき追記ブロックを隣に置く**（波 11 の `IADR-0091` 誤帰属の是正と同じ流儀）。
`updated:` は #850 で既に `2026-08-18` へ前進済みのため据え置く。

### 9. 変異試験の再現条件（`IADR-0022` / `docs/tests/FR-11`）

**自分で構造を確かめた**:

```console
$ grep -n 'NonZdrModels = \["claude-fable-5"\]' src/platform/backend/Services/LlmGateway/tests/LlmGateway.Api.Tests/LlmRouterTests.cs
30:        NonZdrModels = ["claude-fable-5"]      ← 共有ヘルパ Claude() の中
253:            NonZdrModels = ["claude-fable-5"] ← テスト内のローカル合成 config
278:            NonZdrModels = ["claude-fable-5"] ← 同上
```

253 / 278 行は個々のテストが `Build(Opts(fableOnly))` のように**自前で組む** config であり、
共有ヘルパを空にしても影響を受けない。よって**除外系 5 本が落ちるのは 3 箇所すべてを空にしたとき**で、
共有ヘルパ 1 箇所だけなら 3 本にとどまる。**列挙されている 5 本のテスト名自体は正確**である。

`IADR-0022` は**日付つき追記**で、`docs/tests/FR-11_llm-egress-routing.md`（`type: test-spec`・live）は
**直接**条件を「3 箇所すべて」と明記する。

### 10. `#850` 仕様書 §6 に除外理由が 2 件分残っていない

**自分で引き直した**（誤りの側の文字列 `claude-fable-5` で全走査）:

```console
$ grep -rn 'claude-fable-5' --exclude-dir=planning . | grep '^\./\.github/'
./.github/workflows/claude-code-review.yml:118:  … then MODEL="claude-fable-5"
./.github/workflows/claude-coding.yml:111:      … then MODEL="claude-fable-5"
```

**逸脱ではない** —— 計画 `ADR-0038` 決定 2 の射程は実装の `claude-managed` プロバイダの `Models`
（基盤が利用者の文書を送る経路）であり、**開発時の AI レビュー / 実装経路のモデル選択ではない**。
#850 の受け入れ基準も対象を `src/` 配下に限っている。しかし #850 の仕様書 §6 は
「領域外に留めた文書: `docs/adr/README.md` のみ」と書いており、**この 2 ファイルを外した理由が無い**（規則 6）。
日付つき追記で除外理由を足す。**#859 の仕様書は確定済みのため既存本文は書き換えない**
（`related_ids` の `IADR-0113` / `IADR-0114` は #859 で追加済みなので重複させない）。

## 起票を申し送る issue（**本 PR では作らない。親が起票する**）

1. PR タイトル / コミット件名の **HTML エンティティ検査をキットへ**（`check-commit-messages.js` は
   #836 で機能差 0 を達成済み。**ここへローカル差分を作らない**）＋ 規約へ「PR タイトル」の面を追加
2. キットの「4 サブコマンド」コメントの環流（planning）。**4 ファイル / 7 箇所**
   （`HOWTO.md:262` / `claude-coding.example.yml:159,201×2` / `claude-code-review.example.yml:187,458` /
   `.claude/settings.json:145`）。許可リスト本体は既に `grep` を持つのでコメント面のみ
3. `lint` ジョブが雛形に当たらない同型の穴（`ci.yml` が「別 issue へ回す」と書いたが番号が無い）
4. `check-kit-sync.js` の `notApplicable` に改名写像が無く、乖離が機械に永久に見えない
5. 計画 `ADR-0038` 決定 3・4・6（フォールバック順序 / 429 と 400 系の区別 / 可観測化）の実装
6. `scripts/check-ai-workflow-config.js:305` の裸 `#163`（`planning#163` であるべき・既存の混入）

## 受け入れ基準

1. `scripts/setup.sh` が `DOTNET_CLI_TELEMETRY_OPTOUT=1` を **1 件** export し、`bash -n` が通る。
2. `node scripts/gen-changelog.js` の生出力で当該行が是正され、`grep -c '&lt;'` が **0**。
3. `scripts/kit-sync-classification.json` の `scripts/setup.sh` の値が `^2\. ` で始まり、
   SDK 導入デルタに触れ、「環流済み」の射程が分かれている。
4. `docs/adr/IADR-0224_*.md` が存在し、`docs/adr/README.md` に索引行があり、
   `node scripts/check-adr-numbering.js` が pass。
5. `IADR-0060` / `IADR-0106` は**本文不変**で、日付つき追記ブロックが増え `updated:` が前進している。
6. 仕様書 3 本は**既存本文不変**で、`［2026-08-18 追記 / #NNN］` 書式の追記のみが増えている。
7. 検査器が下記「検証」の全件で pass（判定行を生の出力で記録する）。
8. `CLAUDE.md` / `.claude/rules/` のバイト数が不変（`check-reading-budget.js` が同値）。

## 検証（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序）

`git add -A` → 検査器 → コミット → HEAD を読む検査器。**終了コードは判定ではない。判定行を読む。**
`check-kit-sync.js` は **planning submodule を populate してから**走らせる。
`scripts.test.js` に `KIT_DIR=/nonexistent-kit-for-skip` を**付けない**。

結果は §検証結果 に記す。

## 検証結果（判定行は生の出力）

| 検査 | EXIT | 判定行 |
| --- | ---: | --- |
| `check-doc-links.js` | 0 | `OK: 704 件の Markdown に破損した相対リンクはありません`（未 populate の `src/ai-stock-trading` 配下 2 件は対象外の notice つき） |
| `check-doc-status-vocabulary.js` | 0 | `OK: 663 件の仕様書の status が値域に収まっています` |
| `check-doc-type-vocabulary.js` | 0 | `OK: 677 件の文書の type が、テンプレート 19 種類の値域に収まっています` |
| `check-cross-repo-refs.js` | 0 | `走査 1796 件 / 除外 73 件` ＋ `OK: 1796 件に他リポジトリ参照の表記違反はありません` |
| `check-plan-id-qualification.js` | 0 | `OK: 1455 件に他プロジェクト ID の修飾違反はありません` |
| `check-adr-numbering.js` | 0 | `OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です` |
| `check-reading-budget.js` | 0 | `warn Claude Code: 49,885 バイト（予算 51,200 の 97.4%）` —— **着手前と同値。`CLAUDE.md` / `.claude/rules/` を増やしていない** |
| `check-kit-sync.js`（**submodule populate 後**） | 0 | `OK: キット 117 件を分類表と突合しました（A 80 件 / B 25 件 / C 4 件 / 対象外 8 件）` |
| `STRICT_AI_WORKFLOW_CONFIG=1 check-ai-workflow-config.js` | 0 | `✓ AI ワークフローのツール許可設定に問題なし` |
| `REQUIRE_REPO_TESTS=1 scripts.test.js`（`KIT_DIR` の skip 迂回**なし**） | 0 | `✓ 659 tests passed` |
| `bash -n scripts/setup.sh` | 0 | （出力なし＝構文 OK）。`grep -c DOTNET_CLI_TELEMETRY_OPTOUT scripts/setup.sh` = **1** |
| `gen-changelog.js`（2 の効果） | 0 | 441 行目 = ``- **NFR**: AI ワークフローの許可リストへ `git -C <submodule> grep` を 3 パス分足す (e3cb1075)`` ／ `grep -c '&lt;'` = **0** |

**判定の作法**: 終了コードは判定に使わず判定行を読んだ。終了コードはパイプで終端せず
`cmd > log 2>&1; echo "EXIT=$?"` の形で取った。走査の出力を `head` / `sed` で切っていない。

### 本文不改変の実測（追記のみであること）

```console
$ git diff --stat（確定済み仕様書 3 本 ＋ #850 仕様書）
 …issue-835_claude-review-planning-grep.md | 95 ++++++++++  （95 insertions, 0 deletions）
 …issue-824_devcontainer-net10-node22.md   | 61 ++++++++++  （61 insertions, 0 deletions）
 …issue-830_template-backend-ci-build.md   | 56 ++++++++++  （56 insertions, 0 deletions）
```

**削除行 0** であり、既存本文を 1 行も書き換えていない。`docs/adr/` の追記（`IADR-0060` /
`IADR-0106` / `IADR-0112` / `IADR-0022`）も同様に既存条文・既存追記を書き換えず、隣に置いている。
