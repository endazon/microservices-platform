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

### 3. 統合テストは PR から外し、`integration.yml` で全量回収する

> **［2026-08-22 追記 1］**利用者裁定「なるべく精度は落とさない方向で」により、PR へ一度戻した。
>
> **［2026-08-22 追記 2］🔴 追記 1 は読み違いだった。** 利用者の再指摘
> 「**プルリク時の精度低下は、どこかで回収できるならある程度許容する**ので、
> AI コーディング→プルリク→CI→AI コーディング…の**ループ効率が上がるように**してください」
> により、**回収先つきで再び外す**。以下は現在の設計である。

変更前は 1 ジョブが直列に回しており、クリティカルパスは「全ユニットの合計」だった（実測 462 秒）。

- `backend-build`（matrix: ユニット）— restore → build → test（**`--filter "Category!=Integration"`**）
- `build-and-test`（集約・必須 check 名）— 結果を判定し、カバレッジは**報告のみ**
- `integration.yml`（**回収先**）— push: develop ＋ 日次 ＋ 手動で**全量**を走らせ、**床を強制**

**外す根拠は実測**: 追記 1 の形（PR で全量）でのクリティカルパスは
`backend-build (knowledge)` の **2 分 44 秒**で、大半が Testcontainers の起動だった。

#### 🔴 回収先は `--filter` を付けず「全量」で回す

3 つの問題が同時に消える。

1. **床を置き直さずに済む。** 床 `line 39 / branch 27` は**全量 43/43 の実測**から置かれている
   （`src/coverage-floor.json` の根拠欄）。全量で回すならそのまま使える。
2. **二重集計が起きない。** 同じテストプロジェクトを 1 回しか実行しない。
3. **fail-closed の門が要らない。** フィルタが無いので「0 件実行のまま緑」が起こり得ない。
   🔴 **門を諦めたのではなく、フィルタへの依存自体を無くした。`--filter` を戻すなら門も戻す。**

さらに**両側でフィルタしない**ことで、`[Trait]` の付け忘れが **fail-safe に倒れる**
（付け忘れたテストは `ci.yml` 側に残る）。両側でフィルタすると「**どこでも走らない**」に倒れる。

#### 🔴 カバレッジ床は PR で `--report-only`、回収先で強制

床は全量の実測値なので、統合テストの無い PR へ当てると**必ず割れる**。
PR 用に低い床を別に置くと**床が 2 つ並んでどちらが本物か分からなくなる**ため、
**門は回収先に 1 つだけ**置く。🔴 **PR 側の `--report-only` を外すと PR が恒久的に赤くなる。**

#### 🔴 実測で 1 度踏んだ事故（追記 1 の実装）

追記 1 の実装は同じテストプロジェクトを 2 回実行していた
（`--filter "Category!=Integration"` と `"Category=Integration"`）。
**coverlet はテスト実行ごとに Cobertura を出すため、0 件一致の実行でも
「そのプロジェクトの全クラス・被覆 0」のレポートが出る。**
`check-coverage-floor.js` はレポートを単純合算するため、行の分母だけが 2 倍になった。

| | レポート | 行（被覆/全体） | line | branch |
| --- | --- | --- | --- | --- |
| 変更前（ベースライン） | 15 件 | 9958 / 24947 | 39.92% | 28.36% |
| 分離実装（run `32496937488`） | **30 件** | 10087 / **49894** | **20.22%** | **14.27%** → fail |
| 是正後（run `32499045863`） | 15 件 | 9960 / 24947 | 39.92% | 28.36% → OK |

🔴 **不変条件: 1 テストプロジェクト = 1 Cobertura レポート。**

#### Trait の欠落（着手前の走査で見つけた 1 件）

`Storage/ObjectStorageRoundTripTests.cs` は MinIO の Testcontainer を起動するのに
`[Trait("Category", ...)]` を持たなかった。**現在の設計でも是正は要る** ——
`ci.yml` の除外に掛からず PR に残り続けるためである（落ちはしないが速くならない）。

| Trait | クラス数 | `[DockerFact]` | PR（`ci.yml`）で走るか |
| --- | --- | --- | --- |
| `Category=Integration` | 11 | あり | **走らない**（回収先で走る） |
| `Category=Deployment` | 5 | なし | 走る（helm manifest 検証・Docker 不要） |
| `Category=EndpointRouting` | 1 | なし | 走る（インプロセス） |
| **Trait なし** | **1** | **2 件** | 🔴 走ってしまう → 是正済み |

### 4. CodeQL は PR だけ `build-mode: none`、push / 週次で回収する

> **［2026-08-22 追記 1］**一度「常に `manual`」へ戻した。
> **［2026-08-22 追記 2］**利用者裁定（ループ効率優先）により再び PR だけ `none` にする。

```yaml
build-mode: ${{ github.event_name == 'pull_request' && 'none' || 'manual' }}
```

**落とす精度**: PR では生成コード（実測 **270 クラス / 8990 行**）が解析対象外になる。
**回収先**: push（develop / main）＋ 週次 schedule が `manual` ＋ トレースビルドで全量解析。
🔴 **同じ check 名で PR と後段の精度が違う。最も誤解されやすい点である。**
本ワークフローは必須チェックではないため、PR で軽くしてもマージ判定は変わらない。

### 5. `security.yml` の `vulnerable-scan` は PR から外す

> **［2026-08-22 追記 1］**一度 PR へ戻した。**［2026-08-22 追記 2］**再び外す。

見ているのは「**既存の**推移依存 × **後から公開された** advisory」であり PR 差分とは元来無関係。
**PR で見えなくなるのは「新しく持ち込んだ依存に既知の advisory がある」場合だが、
これは `dependency-review`（PR の必須チェック）が捕まえる。**
**回収先**: push ＋ 週次 schedule。失敗は自動起票。

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
2. `build-and-test` の所要が **約 1分30秒**になる（変更前 8分03秒・追記 1 の形で 3分09秒）。
3. 走る検査器の本数と対象が統合前後で**一致する**。
4. **PR の CI ログに Testcontainers の起動が 1 件も残っていない**
   （`grep` で確認。残っていれば `[Trait]` の付け漏れであり「速くならない」形で失敗する）。
5. **PR の `build-and-test` が `--report-only` で exit 0** になり、集計値がログに出る。
6. 🔴 **回収先（`integration.yml`）の集計が レポート 15 件 / line 39.92%（9958〜9960/24947）/
   branch 28.36% と一致し、床 39 / 27 が強制されて緑になる**
   （ずれたら「1 テストプロジェクト = 1 レポート」が壊れている）。
7. NuGet キャッシュが 2 回目の run で**ヒットする**（`Cache restored from key:` を実測）。
8. CodeQL が **PR は `none` / push は `manual` ＋ トレースビルド**で走る。
   🔴 **PR の Security タブのアラートが 0 件になっていないこと**
   （0 件なら「速くなった」ではなく「何も解析していない」）。
9. `report-failure` が **① 失敗時に issue を立て ② 2 回目はコメントを足すだけ**である。
   **回収先が 3 本（`integration.yml` / `codeql.yml` / `security.yml`）に増えたぶん、
   この仕組みへの依存が強くなっている。**
10. `report-failure` が **PR では起票しない**。

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
