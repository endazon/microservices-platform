---
title: 作業仕様書 — PR の CI 所要時間を実測起点で縮め、落とした精度を後段で担保する
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0056
  - IADR-0060
  - IADR-0067
  - IADR-0116
  - IADR-0123
  - IADR-0139
  - IADR-0230
  - IADR-0232
author: claude
created: 2026-08-21
updated: 2026-08-22
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md
related_specs:
  - "../adr/IADR-0232_ci-pr-latency-reduction.md"
  - "../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md"
  - "../adr/IADR-0139_domain-bundled-contract-prs.md"
---

# 仕様書: PR の CI 所要時間を実測起点で縮め、落とした精度を後段で担保する

> 起点 ID は**無採番 `NFR`**（場合 2・メタ作業。`.claude/rules/traceability.md`「起点 ID の種別」／
> [IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 2）。稼働する製品の要件ではなく
> 開発工程の性能であるため、計画側の非機能要件表に当たる番号が無い。**環流しない**。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（CI・工程のメタ作業）
- ユースケース（UC）/ 画面（SC）: 該当なし
- 関連 ADR: 計画 `ADR-0048` 決定 6（kit との乖離は受容する）／
  実装 [IADR-0232](../adr/IADR-0232_ci-pr-latency-reduction.md)（本作業の判断）

## 背景 —— 推測ではなく実測

利用者から「PR の GitHub Actions が遅く作業が止まる」と指摘があった。
**着手前に実 run を測った。** 対象は 2026-08-21 の run（PR `claude/msp-all-issues-moclv4`）。

### 1 push あたりのワークフロー（run `32487460689` ほか・実測）

| ワークフロー | 所要 | 必須チェック |
| --- | --- | --- |
| CodeQL | **11分24秒** | 必須にしない（`paths:` 持ち） |
| CI | **8分03秒** | `build-and-test` / `lint` / `commit-messages` |
| Claude Code Review | 3分38秒 | `claude-review` |
| Security | 2分19秒 | — |
| Images | 1分50秒 | `image-build` |
| PR Size / PR Title / Image Mapping | 各 10〜15秒 | `pr-title` |

### CI 8分03秒の内訳（全 20 ジョブ・`needs:` はゼロ＝全並列）

- **`build-and-test` = 7分42秒**（単一ステップ「Restore, build and test」が **462 秒**）
  —— **クリティカルパスはこの 1 本だけである。**
- `lint`（`dotnet format` を 2 ユニット直列）= 2分42秒
- `template-backend-build` = 23秒
- **残り 17 ジョブ = 各 10〜17 秒。** 実処理は 1 秒未満で、
  checkout 2 秒 ＋ setup-node 5 秒 ＋ ジョブ起動 3 秒の**オーバーヘッドが 9 割**である。

### 測って初めて分かったこと

**「ジョブが多いから遅い」ではなかった。** 20 ジョブは全並列なので、
17 個の軽量ジョブは**待ち時間をほとんど作っていない**。
遅いのは `build-and-test` 1 本であり、その中身は
**2 ユニットの直列ループ ＋ Testcontainers 統合テストの同居**である。

ただし軽量ジョブ 17 個は無害ではない。**同時実行スロットを 17 個占有**し、
実測で run 作成から**最大 53 秒後**に開始したジョブがある。
つまり本命の開始を遅らせている。この 2 つは別の問題であり、別々に直す。

## 変更内容

### 1. `concurrency` を追加（`ci.yml` / `security.yml` / `codeql.yml` / `frontend*.yml` / `image-mapping.yml`）

実測で確認したところ、`concurrency` を持つのは `pr-title` / `pr-size` / `claude-*` /
`images.yml` だけで、**重いワークフローには 1 つも無かった**。
PR ブランチへ 5〜10 分間隔の push が続く運用（実 run 履歴）なので、
古い run がキャンセルされずランナーを占有し続けていた。

`cancel-in-progress` は式で分岐し、**`develop` / `main` への push は完走させる**
（後述 4 の担保がそこに載るため、途中でキャンセルしてはならない）。

### 2. `build-and-test` を分割し、必須 check 名は集約ジョブで維持する

`backend-build`（matrix: platform / knowledge）で 2 ユニットを**並列**に
restore → build → 単体テストし、Cobertura を artifact へ上げる。
`build-and-test` は `needs: [backend-build]` ＋ `if: always()` の**集約ジョブ**として
結果を判定し、artifact を集めてカバレッジ床を**従来どおり 1 回で**集計する。

**この形は新しい発明ではない。** `images.yml` が
`changes` → `build`（16 並列 matrix）→ `image-build`（集約）で既に実装しており、
同ファイルのコメントが理由まで書いている:
「マトリクスのジョブ名は対象が増減すると変わるため必須チェックに使えない。
安定名の集約ジョブが常に結果を報告し、これをブランチ保護の必須チェックにする」。
**リポ内の既存パターンの横展開である。ブランチ保護の手動更新は発生しない。**

`lint` も同型に分割する（`backend-format` matrix ＋ 集約 `lint`）。

### 3. 統合テストは PR に残し、直列ループをユニット並列（matrix）へ組み替える

> **［2026-08-22 追記 1］利用者裁定が更新された。**「なるべく精度は落とさない方向で」。
> 当初は AST の IADR-0049 に倣い「PR から外して日次へ」だったが、**取り下げた**。
>
> **［2026-08-22 追記 2］追記 1 の実装（`backend-build` ＋ `backend-integration` の
> 2 ジョブ分割）は実機で床を割ったため、統合ジョブを畳んだ。** 実測は下記。
> 以下は畳んだ後の設計である。

変更前は 1 ジョブが `for slnx in ...; do restore; build; test; done` と**直列**に回しており、
クリティカルパスは「全ユニットの合計」だった（実測 462 秒）。次へ組み替える。

- `backend-build`（matrix: ユニット）— restore → build → **test（`--filter` 無し・全件）**
- `build-and-test`（集約・必須 check 名）— 結果を判定し、カバレッジを 1 回で集計

**PR で走るテストの集合は変更前と同一である。** 変わったのは走り方だけで、
クリティカルパスが「合計」から「最長の 1 本」になった。

#### 🔴 実測: 統合テストを別ジョブへ切り出すと床が割れる（1 度踏んだ）

追記 1 の実装は同じテストプロジェクトを 2 回実行していた
（`--filter "Category!=Integration"` と `--filter "Category=Integration"`）。
**coverlet はテスト実行ごとに Cobertura を出すため、0 件一致の実行でも
「そのプロジェクトの全クラス・被覆 0」のレポートが出る。**
`check-coverage-floor.js` はレポートを単純合算する（`aggregateReports` にクラス単位の
重複排除は無い）ため、行の分母だけが 2 倍になった。

| | レポート | 行（被覆/全体） | line | branch | 床 |
| --- | --- | --- | --- | --- | --- |
| 変更前（ベースライン） | 15 件 | 9958 / 24947 | 39.92% | 28.36% | 39 / 27 |
| 分割実装（run `32496937488`） | **30 件** | 10087 / **49894** | **20.22%** | **14.27%** | 39 / 27 → **fail** |

診断出力が同じクラスを 2 行（被覆 0 と被覆 15）並べていたのが証拠である。

🔴 **不変条件: 1 テストプロジェクト = 1 Cobertura レポート。**
`check-coverage-floor.js` がレポート間の重複排除を持たない限り、
**同じテストプロジェクトを 1 つの PR の中で 2 回実行してはならない。**

`integration.yml`（日次 ＋ `workflow_dispatch`）は残すが、**PR の肩代わりではない**。
担うのは PR では原理的に見えないもの —— 非決定な失敗（flaky）・環境ドリフト・
自動起票の実働経路 —— である。`push: [develop]` は持たせない
（マージ時は `ci.yml` の `backend-build` が同じテストを走らせるため二重になる）。

🔴 **着手前に Trait を全数走査したところ、穴が 1 件あった。**

| Trait | クラス数 | `[DockerFact]` | `integration.yml`（日次）で走るか |
| --- | --- | --- | --- |
| `Category=Integration` | 11 | あり | 走る |
| `Category=Deployment` | 5 | なし | 走らない（helm manifest 検証・Docker 不要） |
| `Category=EndpointRouting` | 1 | なし | 走らない（インプロセス） |
| **Trait なし** | **1** | **2 件** | 🔴 **穴** |

`Storage/ObjectStorageRoundTripTests.cs` は `MinioBuilder().WithImage(...)` で
MinIO の Testcontainer を起動するのに `[Trait("Category", ...)]` を持たない。
**`ci.yml` が `--filter` を捨てた後もこの是正は要る** —— `integration.yml`（日次）は
`--filter "Category=Integration"` で走るため、Trait が無いと**日次から静かに落ちる**
（PR 側は `--filter` を持たないので緑のまま。成功と見分けの付きにくい失敗の形である）。
同プロジェクトの他 11 クラスと同じ形に揃えた。

**PR ではこの表の 4 種すべてが走る**（`ci.yml` に `--filter` を付けていないため）。
表は「日次が何を拾うか」の母集合であって、PR の出し分けではない。

### 4. CodeQL の `build-mode` は常に `manual`（PR でも精度を落とさない）

> **［2026-08-22 追記］**当初は「PR だけ `build-mode: none`」だった。**取り下げた。**

`none` にすると生成コード（EF Migrations・source generator 出力。カバレッジログの実測で
**270 クラス / 8990 行**）が解析対象外になる。加えて「PR は緑だが push で赤」という
**読み分けの難しい状態**を作る。速さは `concurrency` と NuGet キャッシュで取る ——
どちらも**解析の中身を 1 行も削らない**。本ワークフローは必須チェックではないため、
残る所要はマージを止めない。

### 5. `security.yml` の `vulnerable-scan` は PR に残す

> **［2026-08-22 追記］**当初は PR から外していた。**取り下げた。**

外した根拠は「PR 差分は `dependency-review` がカバーしている」だったが、
**`dependency-review` は依存グラフの差分を見るのに対し、本ジョブは実際に restore した
推移閉包を `--include-transitive` で見る。見ている面が違う以上「重複だから外してよい」と
言い切れない。** 迷ったら残す。`security.yml` は CI と並列で**クリティカルパスではない**。

### 6. Node 検査ジョブ 17 個 → 2 個へ統合

`static-checks`（submodule 不要）と `static-checks-units`（submodule ＋ helm / kubectl）へ束ねる。
`commit-messages`（必須）・`scripts-tests`（45+ の子プロセスを回す実質ヘビージョブ）・
`template-backend-build`（dotnet 必要）は単独のまま残す。

🔴 **検査は 1 本も減らさない。** ジョブが減るだけで、走る検査器の本数と対象は不変である。

🔴 **失敗の可読性を落とさない。** 単純に連続ステップへ並べると
**最初の失敗以降の検査が走らず**、CI の往復が増える。
1 ステップ内で全検査を回し、失敗を蓄積してから落とす。

🔴 **コメントを 1 行も捨てない。** 既存ジョブのコメントは ADR 番号・事故の経緯・
「これを外すと検査が空回りする」という設計要点を持つ資産である
（例: `deploy-manifests` の「overlay 名をここへ書かないこと」）。統合先の各ステップ直前へ移設する。

### 7. NuGet キャッシュ

`setup-dotnet` は本リポで 8 箇所使われているが、**1 つも `cache:` を設定していなかった**。
`packages.lock.json` が無いため `setup-dotnet` の `cache: true` は使えないので、
`actions/cache` を `~/.nuget/packages` へ直接張る。

### 8. フロントエンド

Playwright のブラウザをキャッシュする（`--with-deps` は apt も叩くため未キャッシュ時 1〜2 分）。

🔴 **計画にあった「`e2e` を `needs: build-test` にして `dist` を artifact で受け取る」は採らなかった。**
`build-test` と `e2e` は確かに `pnpm install` と `pnpm run build` を二重に行っているが、
**依存させると e2e が build-test の完走（storybook ビルドを含む）を待つことになり、
両者が並列だった今より wall-clock が伸びうる**。本作業の目的は
**ランナー時間の節約ではなく PR の待ち時間の短縮**であり、目的に対して逆向きになる。
重複の解消は別 issue（依存させずに `dist` を共有する形が要る）へ切り出す。
着手前の見立てを実装時に測り直した結果であり、判断の変更として残す。

### 9. 失敗時の自動 issue 起票

利用者裁定は 2 段階で入った。**後が前を狭めている。**

- **［08-21］**「毎 PR の精度は下がってもよいが、develop マージ時か日次実行でどこかで担保する。
  そこで失敗したら自動で issue を起票する」
- **［08-22］**「なるべく精度は落とさない方向で」

後者により **3・4・5 の「PR から外す」案はすべて取り下げた**。
一方 **`report-failure` は残す** —— 外すのをやめても、日次（`integration.yml`）・
週次（`codeql.yml` / `security.yml`）の実行は**誰も見ていない**ためである。
落ちたまま何日も気付かれない形を塞ぐ。

## 受け入れ基準

1. 必須 check 名（`build-and-test` / `lint` / `commit-messages` / `pr-title` /
   `image-build` / `claude-review`）が**1 つも消えていない**。PR 上で report された
   check 名を全数列挙して `docs/ai-workflow.md` の表と突合する。
2. `build-and-test` の所要が **7分42秒から半分以下**になる。
3. 走る検査器の本数と対象が統合前後で**一致する**。
4. **PR で走るテストの総数が変更前と一致する**（`backend-build` の各脚の合計）。
5. **カバレッジ集計が レポート 15 件 / line 39.92%（9958/24947）/ branch 28.36% と一致する**
   （変更前のベースラインと同値。ずれたら「1 テストプロジェクト = 1 レポート」が壊れている）。
6. NuGet キャッシュが 2 回目の run で**ヒットする**（`Cache restored from key:` を実測）。
7. CodeQL が **PR / push の両経路**で意図どおりのモードで走る。
8. `report-failure` が **① 失敗時に issue を立て ② 2 回目はコメントを足すだけ**である。
9. `report-failure` が **PR では起票しない**。

## 検証（実測でしか確かめない）

- ジョブ単位の `started_at` / `completed_at` を変更前後で突合する
  （run 全体の所要だけを見ない。ジョブ単位の退行が埋もれる）。
- キャッシュは**2 回 push して**比較する（初回は必ずミスするため）。
- 統合ジョブは**変異試験**で確かめる: 1 検査を故意に失敗させ、
  ① ジョブが赤くなり ② **後続の検査も走り切って全失敗が一度に出る**ことを実測する。
- 自動起票は `workflow_dispatch` の `force_failure` 入力で**実際に 2 回走らせて**
  ① 立つ ② 重複しない を実測する。
  **平時は常に skip で緑になる仕組みなので、実測しない限り壊れていることに永久に気付けない。**

## 未決事項

- カバレッジ床（現在 line 39 / branch 27、実測 line 39.92% / branch 28.36%・レポート 15 件）は
  **両ジョブの artifact を合わせて 1 回で集計する**ことで維持する設計である。
  **改定後は統合テストを PR から外さないため、床が割れる理由は無い** ——
  割れたら集計の配線（artifact 名・`--collect`）が壊れている。**「テストが減った」と
  取り違えないこと。**
