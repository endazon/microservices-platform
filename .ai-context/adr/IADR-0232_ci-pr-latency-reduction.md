---
title: IADR-0232 PR の CI は速さを採り、落とした精度は後段（マージ時・日次）で担保して失敗を自動起票する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0049
  - IADR-0067
  - IADR-0123
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md (決定 6・kit との乖離は受容する)
---

# IADR-0232 PR の CI は速さを採り、落とした精度は後段（マージ時・日次）で担保して失敗を自動起票する

## 状況

PR の CI が遅く、開発の反復が止まっていた。**着手前に実 run を測った**（2026-08-21・
run `32487460689` ほか）。1 push で 8 ワークフロー・約 40 ジョブが起動し、
CodeQL 11分24秒 / CI 8分03秒 / Claude Code Review 3分38秒 が並ぶ。

測って初めて分かったことが 2 つある。

**1 つ目。「ジョブが多いから遅い」ではなかった。** `ci.yml` の 20 ジョブは `needs:` を
1 つも持たず全並列であり、17 個の軽量ジョブ（各 10〜17 秒）は待ち時間をほとんど作っていない。
遅いのは `build-and-test` **1 本**（7分42秒）で、その中身は
2 ユニットの直列ループと Testcontainers 統合テストの同居である。
**ジョブ数を減らす施策と、クリティカルパスを縮める施策は別の問題である。**

**2 つ目。同じ C# を 1 PR で 5 回コンパイルしていた。**
`ci.yml:build-and-test` / `ci.yml:lint`（`dotnet format` が MSBuild ワークスペースをロードする）/
`codeql.yml`（トレース付き Release ビルド）/ `security.yml:vulnerable-scan`（全 sln restore）/
`ci.yml:template-backend-build`。しかも `setup-dotnet` は本リポで 8 箇所使われて
**1 つも `cache:` を設定していなかった**（`actions/cache` も `NUGET_PACKAGES` も皆無）。

一方で、CI を速くする手段の多くは**検証の精度を落とす**。何をどこまで落としてよいかは
実装側だけでは決められないため、利用者へ裁定を求めた。

## 決定

### 決定 1: PR の精度は落としてよい。ただし落とした分は後段で必ず担保する

利用者裁定（2026-08-21）: **「毎プルリクの精度は下がってもいいが、develop ブランチ
マージ時か日次実行にするなどしてどこかで担保できるようにする。そのときに失敗したら
自動で issue を起票する」。**

この裁定が本 IADR の他のすべての決定の性格を決める。以下の決定 3・4・5 は
「検証をやめる」のではなく**「検証を PR から後段へ移す」**である。両者は結果が違う。
**後段が黙って赤いまま放置されれば、移しただけで失っているのと同じになる。**
したがって**自動起票は付随的な便利機能ではなく、この設計の成立条件である。**

担保の場所と精度:

| PR で落とすもの | 担保する場所 | 後段の精度 |
| --- | --- | --- |
| Testcontainers 統合テスト | `integration.yml`（`push: develop` ＋ 日次） | **PR 前と同一** |
| CodeQL の生成コード解析 | `codeql.yml`（push ＋ 週次・`build-mode: manual`） | **現状と同一** |
| `vulnerable-scan` | `security.yml`（push ＋ 週次） | **現状と同一** |

### 決定 2: 必須 check 名は 1 つも変えない。matrix は集約ジョブで名前を保つ

ブランチ保護の必須 check 名はジョブ ID そのものであり、消えると PR が恒久的にマージ不能になる。
`build-and-test` を matrix 化すると `build-and-test (platform)` へ改名されてしまう。

そこで matrix は別ジョブ ID（`backend-build` / `backend-format`）へ切り出し、
`build-and-test` / `lint` は `needs:` ＋ `if: always()` で結果を判定するだけの
**集約ジョブ**として名前を維持する。

**これは新しい発明ではない。** `images.yml` が `changes` → `build`（16 並列 matrix）→
`image-build`（集約）で既に実装しており、同ファイルのコメントが理由まで書いている
（`IADR-0067`）。**リポ内の既存パターンの横展開であり、ブランチ保護の手動更新は発生しない。**

### 決定 3: Testcontainers 統合テストを PR 既定から外す（AST の IADR-0049 と同型）

`--filter "Category!=Integration"` を付け、新規 `integration.yml` で全件を回す。

🔴 **射程は「Testcontainers を起動するテスト」であって「`Knowledge.IntegrationTests`
プロジェクト全体」ではない。** 同プロジェクトの `Category=Deployment`（5 クラス）と
`Category=EndpointRouting`（1 クラス）は Docker を使わず速いため **PR に残す**。
manifest 配線の退行を PR 時点で捕まえる価値がある。

🔴 **着手前に Trait を全数走査して穴を 1 件見つけた。**
`Storage/ObjectStorageRoundTripTests.cs` は `MinioBuilder().WithImage(...)` で MinIO の
Testcontainer を起動するのに `[Trait("Category", ...)]` を持っていなかった。
**フィルタだけ足していたら、このクラスは既定 CI に残り、コンテナ起動も残っていた。**
「分離したのに速くならない」という、成功と見分けの付きにくい失敗の形である。
先に Trait を付けてから分離した。

この経験は一般化できる: **「フィルタで外す」という変更は、フィルタの母集合を
走査してからでないと外れたことにならない。** 件数を実測するまで「外した」と言わない。

### 決定 4: CodeQL は PR だけ `build-mode: none` にする（全面的に倒さない）

`build-mode` は式を受け付ける文字列入力（`none` / `autobuild` / `manual`）であるため、
イベントで切り替えられる。PR は `none`、push（develop/main）と週次 schedule は `manual`。
トレース付きビルドの 3 ステップには `if: github.event_name != 'pull_request'` を付ける。

**PR で落ちる精度は具体的である**: 生成コード（EF Migrations・source generator 出力）が
解析対象外になる。カバレッジログの実測でその量は **270 クラス / 8990 行**であり、
**無視できない量である**。だから全面的に `none` へ倒さず、後段で `manual` を維持する。

⚠️ **同じ check 名で PR と後段の精度が違う。** 読み手が最も誤解しやすい点なので、
`codeql.yml` のコメントに明記する。

### 決定 5: `vulnerable-scan` を PR から外す

`find` ＋ 全 sln restore ＋ `dotnet list --vulnerable` を毎 PR で回していた。
必須チェックではなく、PR 差分の依存レビューは `dependency-review` が既にカバーしている。
push / 週次 schedule のみにする。

### 決定 6: Node 検査ジョブは束ねるが、検査は 1 本も減らさない

17 ジョブを `static-checks` / `static-checks-units` の 2 つへ束ねる。
`commit-messages`（必須）・`scripts-tests`（45+ の子プロセスを回す実質ヘビージョブ）・
`template-backend-build`（dotnet 必要）は単独のまま残す。

🔴 **束ねる際、失敗の可読性を落としてはならない。** 単純に連続ステップへ並べると
**最初の失敗以降の検査が走らず**、1 回の CI で 1 件しか分からなくなって往復が増える。
1 ステップ内で全検査を回し、失敗を蓄積してから落とす形にする。

🔴 **既存ジョブのコメントを 1 行も捨てない。** このリポジトリのワークフローのコメントは
ADR 番号・事故の経緯・「これを外すと検査が空回りする」という設計要点を持つ資産である
（例: `deploy-manifests` の「overlay 名をここへ書かないこと。書くと次に overlay が
増えたとき静かに検査対象から外れる」）。統合先の各ステップ直前へ移設する。

## 却下した案

### 却下 A: 統合テストをプロジェクトごと nightly へ回す

`Deployment` / `EndpointRouting` の 6 クラスまで巻き添えになる。これらは Docker 不要で
速く、PR 時点で manifest 配線の退行を捕まえている。**遅いから外すのであって、
同じディレクトリに居るから外すのではない。**

### 却下 B: CodeQL を PR から丸ごと外す

11 分は完全に消えるが、PR 時点で SAST の指摘が一切出なくなる。
`build-mode: none` なら PR でも解析自体は走り（インクリメンタル）、
落ちるのは生成コードの分だけで済む。**落とす量を最小にできる選択肢がある以上、
大きく落とす案は採らない。**

### 却下 C: 自動起票を後追いの別 PR にする

「PR から外す変更」と「後段で担保する仕組み」を別 PR に割ると、
**その間だけ担保が無い窓ができる**。決定 1 の趣旨に反するため同じ PR に入れる。

### 却下 E: frontend の `e2e` を `build-test` へ依存させて二重ビルドを解消する

`frontend.yml` の 2 ジョブは `pnpm install` と `pnpm run build` を二重に行っており、
ランナー時間の無駄は実在する。しかし依存させると **e2e が build-test の完走
（storybook ビルドを含む）を待つ**ことになり、並列だった今より wall-clock が伸びうる。
**本 IADR の目的はランナー時間の節約ではなく PR の待ち時間の短縮**であり、目的に対して逆向きになる。
重複の解消は「依存させずに `dist` を共有する」形が要るため別 issue へ切り出す。

### 却下 D: ワークフローレベルの `paths:` で PR の実行を絞る

`paths:` を持つワークフローは対象外の PR で **report されず恒久 pending** になるため、
必須チェックに指定できない（`docs/ai-workflow.md` の既存ルール）。
出し分けが要る場合は上流ジョブの出力による**ジョブ条件 `if:`** で実装する
（`if` でスキップされたジョブは success として必須チェックを満たす）。

## 影響

- **PR の待ち時間は縮む。** 一方で **develop へのマージ時と日次実行はむしろ重くなる**
  （統合テストと全量 CodeQL がそちらへ移るため）。**それが狙いである** ——
  誰も待っていない場所へ重い検証を移す。
- **`develop` / `main` への push は `cancel-in-progress` の対象にしない。**
  担保がそこに載るため、途中でキャンセルしてはならない。
- 新しい依存として `actions/cache` / `actions/github-script` / `actions/download-artifact` が
  加わる。`scripts/action-versions.json` の `expected` へ同時に追加する
  （片方だけ直すと `check-action-versions.js` が CI で落ちる）。

## 残余リスク

- 🔴 **自動起票は「動かないことに誰も気付かない」種類の仕組みである。**
  失敗したときにしか走らないため、平時は常に skip で緑になる。
  `workflow_dispatch` の `force_failure` 入力で**実際に 2 回走らせて**
  ① issue が立つ ② 2 本目が立たずコメントが足される を実測すること。
  実測しないまま「起票を入れた」と言わない。
- 統合テストを外すぶんカバレッジ床が割れる可能性がある。
  **実測してから**必要なら床を下げ、理由をここへ追記する（黙って下げない）。
- PR と後段で CodeQL の精度が違う。PR が緑でも push で赤くなることがあり得る。
  それは退行ではなく**設計どおり**である。決定 4 のコメントで読み分けられるようにした。
