---
title: 作業仕様書 — Result 型の方針を計画 ADR-0041 へ追随させる（#500）
type: spec
status: done
related_ids:
  - ADR-0041
  - ADR-0030
  - IADR-0117
  - IADR-0196
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
related_specs: []
---

# 作業仕様書 — Result 型の方針を計画 ADR-0041 へ追随させる（#500）

## 1. 起点と目的

計画 [ADR-0041](../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md)（Result 型の実装に外部ライブラリを認め、SharedKernel で包んで差し替え可能に保つ）が新設され、本リポジトリの現行方針と食い違っている。この食い違いを解消する。

**ADR-0041 の状態は `Proposed` である。** 同 ADR は「**`Proposed` は決定の効力を停止しない**。保留しているのは記録としての承認だけである」と明記しており、決定内容は利用者裁定（質問票 第 1 回 Q9・第 2 回 Q27）で確定済みである。

> **`IADR-0119` 決定 2（`Proposed` は着手条件を満たさない）との関係**: 両者は射程が違う。IADR-0119 が定めるのは **FR-17〜21 の着手ゲート**であり、本作業はそれに含まれない。#500 本文も同じ整理をしている。

## 2. 着手前の実測（2026-08-15・`d645f0b`）

#500 の起票は 2026-08-04 で 11 日前である。**記述が現在も成り立つかを全項目について実測した。**

| # | #500 の記述 | 実測結果 |
| --- | --- | --- |
| 1 | `scripts/check-backend-libraries.js:75-76` の BANNED に `OneOf` / `CSharpFunctionalExtensions` | **成立**（`:75` / `:76`） |
| 2 | `docs/tech/tech-requirements.md:118` 「Result 型は共有カーネルに自前実装する」 | **成立。ただし行番号は `:119`** |
| 3 | `docs/tech/tech-requirements.md:151` 不採用欄 | **成立。ただし行番号は `:152`** |
| 4 | `Platform.Shared.Kernel` は未作成 | **成立**（`src/platform/backend/Shared/` は `Platform.Shared.Contracts` / `Platform.Shared.Infrastructure` の 2 つのみ） |
| 5 | 現時点で CI は落ちていない | **成立**。`scripts/backend-library-baseline.json` に `CSharpFunctionalExtensions` の記録は **0 件**（実在の使用が無い） |
| 6 | ADR-0041 の状態 | **`Proposed` のまま**（計画 pin 上） |

**行番号 2 件のずれ以外、記述はすべて現在も有効である。**

## 3. ADR-0041 が課す規則（決定の原文から）

| 決定 | 内容 |
| --- | --- |
| **決定 1** | `CSharpFunctionalExtensions`（MIT）を採用する。**`OneOf` は採らない** |
| **決定 2** | `SharedKernel` に自前の型を定義し、**その内部実装としてのみ**外部ライブラリを使う。`Domain` / `Application` / `Api` / `Infrastructure` は外部ライブラリの型・名前空間を**直接参照してはならない**。**この規則は csproj の静的解析で機械的に強制する** |
| **決定 3** | ADR-0030 選定基準 3 を改定。**`SharedKernel` が推移的に持ち込んでよい外部パッケージは Result 型の実装 1 つに限る**。**この 1 つ以外を `SharedKernel` へ追加してはならない** |
| **決定 4** | 基盤と可変機能ユニットの双方に及ぶ |

ADR-0041 §結果 のフォローアップは次の 2 点を明示的に求めている。

> - csproj の静的解析へ許可リスト 1 件を追加する。**許可リスト外のパッケージが `SharedKernel` へ入った場合に失敗すること**を検査で担保する。

**したがって本作業は「`CSharpFunctionalExtensions` を SharedKernel でだけ許す」（決定 2）だけでなく、「SharedKernel に他のパッケージが入ったら失敗する」（決定 3）も実装する。** 前者だけでは決定 3 が機械検査を持たないまま残り、ADR が「SharedKernel は外部依存の抜け道である」という読みを塞ぐために置いた限定が効かなくなる。

## 4. 実装方針

### 4.1 検査器（`scripts/check-backend-libraries.js`）

**`CSharpFunctionalExtensions` を BANNED から外さない。** 外すと SharedKernel 以外での使用（決定 2 が禁じる直接参照）が素通りする。**BANNED に残したまま、SharedKernel でだけ除外する**形を採る。

| 追加するもの | 内容 |
| --- | --- |
| `SHARED_KERNEL_ALLOWED` | SharedKernel の内部実装としてのみ許可される外部パッケージの許可リスト。現行は `['CSharpFunctionalExtensions']` の 1 件 |
| `isSharedKernelProject(relPath)` | `Platform.Shared.Kernel.csproj` か否か |
| `bannedListFor(projPath)` | SharedKernel なら `BANNED` から許可リスト分を差し引いたものを返す。それ以外は `BANNED` そのまま |
| `sharedKernelViolations(relPath, content)` | SharedKernel の `PackageReference` のうち**許可リストに無いものを違反**として返す（決定 3） |

`.cs` の `using` は `owningProject()` が解決した所属 csproj で判定する。**SharedKernel に属する `.cs` の `using CSharpFunctionalExtensions;` は許可され、他プロジェクトのものは従来どおり検出される。**

### 4.2 判定の境界（意図的に据える線）

- **許可はプロジェクト名で決める**（`Platform.Shared.Kernel.csproj`）。パス階層では決めない。ADR-0041 決定 2 が名指しするのは「SharedKernel」という**プロジェクト**であり、`Shared/` ディレクトリ配下の他プロジェクト（`Platform.Shared.Contracts` 等）は対象外だからである
- **`OneOf` は許可リストに入れない**（決定 1）。SharedKernel 内でも fail する
- 決定 3 の「1 つに限る」は**許可リストの要素数**で表現する。将来 Result 実装を差し替えるときは許可リストの中身を入れ替える（増やすのではない）

### 4.3 文書

| ファイル | 変更 |
| --- | --- |
| `docs/tech/tech-requirements.md:119` | 「自前実装する」→ 自前の公開型を置き、**内部実装としてのみ** `CSharpFunctionalExtensions` を使う旨へ |
| `docs/tech/tech-requirements.md:152` | 不採用欄から `CSharpFunctionalExtensions` を外し、採用欄の条件付き記述へ移す。**`OneOf` は不採用のまま残す** |
| `docs/adr/IADR-0117` | ADR-0030 選定基準 3 が ADR-0041 で改定された旨の**日付付き追記** |
| `docs/adr/IADR-0196`（新規） | 本作業の実装判断（許可の判定単位・BANNED に残す設計・決定 3 の機械検査）を記録 |
| `docs/adr/README.md` | IADR-0196 の索引行 |

## 5. 受け入れ基準（#500 から）

- [x] `check-backend-libraries.js` が「SharedKernel 内部のみ許可」を機械的に判定でき、**負例で fail することを実測**している
- [x] `--self-test` に該当ケースを追加している
- [x] `docs/tech/tech-requirements.md` が ADR-0041 と整合している
- [x] ADR-0030 選定基準を引用する既存 IADR へ改定の追記が入っている（`IADR-0117` 決定 2）
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が green（**516 tests passed**）

### 追加（ADR-0041 フォローアップ由来）

- [x] **許可リスト外のパッケージが SharedKernel へ入ると fail する**ことを実測している（`OneOf` / `Npgsql` の 2 系統）

### 実測ログ（2026-08-15）

```console
$ node scripts/check-backend-libraries.js --self-test
[check-backend-libraries] 自己試験 63 件 OK。      # 追加前 49 件（HEAD 版を実行して実測）→ 63 件

$ node scripts/check-backend-libraries.js
[check-backend-libraries] OK: 新規混入 0 件 / Domain 依存規律 OK（既知残件 42 件は baseline 済み）。

$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
✓ 516 tests passed

$ node scripts/check-adr-numbering.js
[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。

$ node scripts/check-doc-links.js
[check-doc-links] OK: 608 件の Markdown に破損した相対リンクはありません。

$ node scripts/check-cross-repo-refs.js
[check-cross-repo-refs] OK: 1591 件に他リポジトリ参照の表記違反はありません。
```

> **`check-cross-repo-refs.js` は最初 1589 件で「untracked 2 件は対象外」と警告した。** 本作業の新規 2 ファイル
> （本仕様書と `IADR-0196`）が未追跡だったためである。`git add` 後に 1591 件へ増えて初めて、
> **新規ファイルが実際に検査を通った**ことになる。追跡下だけを走査する検査器（IADR-0183）では、
> **`git add` 前の green は新規ファイルについて何も言っていない。**

## 6.1 索引タイトルの上限に掛かった記録

`docs/adr/README.md` へ IADR-0196 の索引行を足したとき、`scripts.repo.test.js` が
`title-too-long` で fail した（**上限 200 字**・`scripts/adr-index-title-baseline.json`）。

当初は他の決定（IADR-0138 等）に倣って決定内容を索引へ書き下したが、**それらは 65 件の既知残件として
baseline に載っているだけ**で、規約上の正ではない。ratchet は「**新規混入は fail**」であり、
是正の方向は「索引タイトルセルを本体 `title:` の要約へ縮める」である。**158 字へ縮めて解消した。**

## 6. 検証方法

`Platform.Shared.Kernel` は未作成であるため、**一時ツリーを組んで実測する**（`scanTree(root)` が走査起点を引数に取る作りになっており、既存の自己試験が同じ方法を採っている）。

| 例 | 期待 |
| --- | --- |
| 正例: SharedKernel の csproj に `CSharpFunctionalExtensions` | **検出しない** |
| 正例: SharedKernel 配下の `.cs` に `using CSharpFunctionalExtensions;` | **検出しない** |
| 負例: `*.Domain.csproj` に `CSharpFunctionalExtensions` | **検出する** |
| 負例: Application 層の `.cs` に `using CSharpFunctionalExtensions;` | **検出する** |
| 負例: SharedKernel に `OneOf` | **検出する**（許可リスト外） |
| 負例: SharedKernel に `Serilog` 等の任意パッケージ | **検出する**（決定 3） |

## 7. 対象外（本作業でやらないこと）

- **`Platform.Shared.Kernel` プロジェクトの作成そのもの**。#500 は検査と文書の追随を範囲としており、SharedKernel の実装は別 issue である
- `SharedKernel` が公開する操作の一覧（`Bind` / `Map` / `Tap` / `Combine` / 非同期版）の確定。ADR-0041 のフォローアップだが、**実体を作るときに決めるもの**である
- `ai-stock-trading`（別リポの submodule）への適用。決定 4 の射程には入るが、本リポジトリからは変更できない
- 計画 ADR-0041 を `Accepted` へ移すこと。**計画リポ側の作業**であり、`/sync-impl` の実行を要する
