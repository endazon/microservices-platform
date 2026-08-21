---
title: IADR-0196 共有カーネルの Result ライブラリは「BANNED からの除外」ではなく「プロジェクト名で限定した許可リスト」で機械強制する
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0030, ADR-0041, IADR-0117]
author: Claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
related_specs:
  - ../specs/20260815_issue-500_result-type-adr-0041-followup.md
  - ../../docs/tech/tech-requirements.md
---

# IADR-0196: 共有カーネルの Result ライブラリは「BANNED からの除外」ではなく「プロジェクト名で限定した許可リスト」で機械強制する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-15
- 起点: [#500](https://github.com/endazon/microservices-platform/issues/500)（計画 ADR-0041 への追随）

## コンテキストと課題

計画 ADR-0041（計画リポ） が
`CSharpFunctionalExtensions` を **`SharedKernel` の内部実装としてのみ**採用可能とし、ADR-0030 選定基準 3
（「Domain は外部依存ゼロ」）を「**名指しの 1 つ**」へ部分改定した。同 ADR は決定 2 で
「**この規則は csproj の静的解析で機械的に強制する**」と述べ、§結果 のフォローアップで
「**許可リスト外のパッケージが `SharedKernel` へ入った場合に失敗すること**を検査で担保する」を求めている。

本リポジトリの `scripts/check-backend-libraries.js` は `CSharpFunctionalExtensions` を `BANNED` に
持っており、そのままでは ADR-0041 と食い違う。**どう追随させるかに複数の実装がありうる。**

なお `Platform.Shared.Kernel` は**未作成**であり、`scripts/backend-library-baseline.json` にも
`CSharpFunctionalExtensions` の記録は 0 件である。**現時点で fail する対象は無い。**
それでも先に検査を改める理由は、**作った瞬間に CI が赤くなる**のを避けるためである（#500 の指摘）。

## 検討した選択肢

1. **`BANNED` から `CSharpFunctionalExtensions` を削除する** — 最も単純。しかし
   **決定 2 が禁じる「他層からの直接参照」が素通りする**。ADR-0041 が型エイリアスを退けた理由
   （拡張メソッドと `Bind` / `Map` のチェーンが漏れる）と同じ穴が、検査側に空く。
2. **`BANNED` に残し、共有カーネルでのみ差し引く** — 直接参照は従来どおり検出しつつ、
   内部実装だけを許せる。
3. **パス階層（`Shared/` 配下）で許可する** — 実装は簡単だが、`Platform.Shared.Contracts` /
   `Platform.Shared.Infrastructure` にも外部 Result ライブラリが入れるようになる。

## 決定

### 決定 1: 選択肢 2 を採る —— `BANNED` からは外さず、共有カーネルでだけ差し引く

`SHARED_KERNEL_ALLOWED`（現行 `['CSharpFunctionalExtensions']`）を置き、`bannedListFor(projPath)` が
共有カーネルのときだけ `BANNED` から差し引く。**`BANNED` 本体からは外さない。**

外すと、`Domain` / `Application` / `Api` / `Infrastructure` が直接 `using CSharpFunctionalExtensions;`
と書いても検出されない。ADR-0041 決定 2 の実体は「**他層に漏らさないこと**」であり、
採用したこと自体ではない。**検査が守るべきはこの境界である。**

### 決定 2: 許可の判定単位は「所属プロジェクト」とする（`Shared/` の階層では判定しない）

`isSharedKernelProject()` は `.csproj` のベース名が `Platform.Shared.Kernel` かどうかだけを見る。
そして**すべての走査対象ファイルを、まず所属プロジェクトへ帰属させてから**この判定に掛ける。

| 対象 | 帰属のさせ方 |
| --- | --- |
| `.csproj` | 自身 |
| `.cs` | `owningProject()` が解決した所属 `.csproj` |
| **`.props` / `.targets`** | **`owningProject()`（`.cs` と同じ）。属する `.csproj` が無ければ自身**（＝共有カーネル扱いにしない） |

ADR-0041 決定 2 が名指しするのは「SharedKernel」という**プロジェクト**である。
`src/platform/backend/Shared/` には [IADR-0117](./IADR-0117_platform-shared-kernel-placement.md) が定めた
3 プロジェクトが同居しており、**`Shared/` の階層で判定すると `Platform.Shared.Contracts`（シリアライズ／
イベント契約を持つ）にも外部ライブラリが入る**。IADR-0117 が選択肢 C を退けた理由がそのまま復活するため、採らない。
**ここで採るのは「プロジェクトの所属」であって「`Shared/` 配下かどうか」ではない。**

> **`.props` / `.targets` を帰属対象に含めたのは、含めないと穴が空くことを実測したためである**（PR #742 の
> AI レビュー指摘を検証した結果。当初は `.csproj` だけで判定していた）。共有カーネルの
> `PackageReference` を隣の `.props` へ移すだけで、**(a) 許可パッケージが誤検出され、
> (b) 決定 3 の許可リスト検査が丸ごと素通りした**。**(b) のほうが重い** —— 本 IADR が決定 3 のために
> 置いた検査そのものが回避できることを意味する。`#471` は `Directory.Build.props` を**実在の混入経路**として
> 実測しており（だから props / targets を走査対象に加えた）、この抜け道は仮想的なものではない。
>
> **属する `.csproj` が無い場合に自身へフォールバックさせるのが要点である。** `src/Directory.Build.props` は
> どのプロジェクトにも属さないため共有カーネル扱いにならず、**#471 が塞いだ一括注入の経路は残る**。

### 決定 3: 許可リストは「除外リスト」と「許可リスト」を兼ねる —— 外のものは 1 件でも違反にする

`sharedKernelViolations()` を新設し、共有カーネルの `PackageReference` のうち
`SHARED_KERNEL_ALLOWED` に無いものを違反（`shared-kernel-package`）として報告する。
**あわせて共有カーネルの `ProjectReference` を 0 件に固定する**（`shared-kernel-project`）。

**これは `BANNED` の判定とは独立である。** `BANNED` に載っていない任意のパッケージ（例: `Npgsql`）でも、
共有カーネルへ入れば違反になる。Domain は共有カーネルだけを `ProjectReference` できるため、
**共有カーネルの `PackageReference` はそのまま Domain の推移的な外部依存になる**からである。

決定 1 だけでは ADR-0041 決定 3（「1 つに限る」）が機械検査を持たないまま残り、
ADR が「SharedKernel は外部依存の抜け道である」という読みを塞ぐために置いた限定が効かない。

> **`ProjectReference` まで見るのは、決定 3 が「推移的に」「`Domain` の推移閉包を検査する」と
> 書いているためである。** `PackageReference` だけを見ると 1 段しか見ておらず、
> **`Domain` → `Kernel` → 任意のプロジェクト（`Npgsql` 等）** という経路が違反 0 件で素通りする
> （PR #742 の ADR 監査が実測して再現した）。`domainViolations()` は `Domain` の直下しか見ないため、
> そちらでも捕まらない。**`ProjectReference` 1 行で抜け道が復活する。**
>
> 共有カーネルは Result / Error・共通基底を置く**最下層**であり（[IADR-0117](./IADR-0117_platform-shared-kernel-placement.md) 決定 1）、
> 他プロジェクトを参照する理由が無い。**0 件に固定するのが最も単純で、条文の「推移閉包」に最も近い。**
> 参照が必要になったらそれは共有カーネルの位置づけが変わったということであり、IADR-0117 の改定が要る。

### 決定 4: リストを増やす操作は禁じ、差し替えは「入れ替え」で行う

`SHARED_KERNEL_ALLOWED` の要素数が 1 であることを `--self-test` が検査する。
Result 実装を差し替えるときは**要素を入れ替える**。増やす必要が生じた場合は
**ADR-0041 の改定が要る**（実装側の判断で増やしてよいものではない）。

## 理由

- 決定 1 は、ADR-0041 が型エイリアスを退けた論法をそのまま検査側へ写したものである。
  「採用したか」ではなく「**他層へ漏れていないか**」を見る形にしないと、封じ込めが検査で担保されない。
- 決定 3 の「BANNED と独立」は、ADR-0041 が挙げたトレードオフ
  （「**選別を怠ると封じ込めが形骸化する**」）への直接の対処である。BANNED 由来の判定だけでは、
  不採用リストに載っていない新しいパッケージが共有カーネルへ入るのを止められない。

## 結果

- 良い影響: `Platform.Shared.Kernel` を作る前に検査が整い、**作った瞬間に CI が赤くなる事態を避けられる**。
  ADR-0041 のフォローアップ 2 点（許可リスト 1 件の追加・許可リスト外での失敗）が実装で満たされる。
- 悪い影響 / トレードオフ:
  - **共有カーネルのプロジェクト名が検査にハードコードされる**（`SHARED_KERNEL`）。改名時は検査も直す。
    ただしこの結合は本 IADR 以前から `domainViolations()` に存在しており、新たに増やしたものではない。
  - 共有カーネルは `PackageReference` を 1 件しか持てないため、実装時に
    `Microsoft.Extensions.*` 等が必要と判明した場合は**そこで詰まる**。その場合に許可リストへ足すのは
    決定 4 が禁じており、**ADR-0041 の改定（計画側）が必要になる**。
### 検出しないこと（限界の明記）

**検査は次を捕まえない。** 「新規混入 0 件」の緑をこれらの不在の証拠と読んではならない
（限界を明記する作法は [IADR-0138](./IADR-0138_coverage-exclude-generated-code.md) §検出しないこと と揃えた）。

| # | 検出しないもの | なぜ |
| --- | --- | --- |
| 1 | **`src/Directory.Build.props` 等による一括注入が共有カーネルへ届く経路** | 決定 2 の帰属は「その props がどのプロジェクトに属するか」で決まる。リポジトリ全体へ効く props はどのプロジェクトにも属さないため、共有カーネルの許可リスト判定に合流しない。**`BANNED` 非掲載のパッケージ（`Npgsql` 等）を全体注入すると、共有カーネルへ届いても何も出ない**（PR #742 の ADR 監査が実測）。塞ぐには props の合成結果を各プロジェクトへ展開する必要があり、`Condition` の評価まで含めると静的解析の範囲を超える |
| 2 | **`PackageVersion`（CPM の版定義）** | 本検査の対象外（`PackageReference` のみ）。`src/Directory.Packages.props` に版を書いただけでは違反にならない。実際に参照した時点で捕まる |
| 3 | **`Platform.Shared.Kernel` が公開する API の選別が妥当か** | ADR-0041 が「選別を怠ると封じ込めが形骸化する」と述べた点。**外部ライブラリの型をそのまま公開する自前型**を書いても、`using` と `PackageReference` が共有カーネルに閉じている限り検査は通る。**人が見る** |
| 4 | **`src/ai-stock-trading`（submodule）** | 別リポジトリであり走査対象外（IADR-0120）。ADR-0041 決定 4 の射程には入るが、本リポジトリからは変更できない |

**1 と 3 は、この検査が「参照の有無」しか見ないことの帰結である** —— `scripts/README.md` が
`check-backend-libraries.js` について既に述べている限界（結合の深さは見ない）と同じ性質である。

- フォローアップ:
  - **限界 1（一括注入）を塞ぐか否か**は、共有カーネルの実体が出来て props 構成が定まってから判断する。
    現時点で塞ごうとすると、存在しない構成に対する防御的実装になる。
  - `SharedKernel` が公開する操作の一覧（`Bind` / `Map` / `Tap` / `Combine` / 非同期版のうち何を出すか）は
    **実体を作るときに決める**。ADR-0041 のフォローアップだが本 IADR の範囲外である。
  - 計画 ADR-0041 は `Proposed` のままである（保留しているのは記録としての承認のみ）。
    計画側で `/sync-impl` を実行し `Accepted` へ移すことは**計画リポの作業**である。

## 検証

`Platform.Shared.Kernel` が未作成のため、`scanTree(root)` に一時ツリーを与えて実測した
（`--self-test`。既存の #471 由来の実地確認と同じ方法）。

| 例 | 期待 | 結果 |
| --- | --- | --- |
| 共有カーネルの csproj に `CSharpFunctionalExtensions` | 検出しない | OK |
| 共有カーネル配下の `.cs` に `using CSharpFunctionalExtensions;` ＋ `using MassTransit;` | **MassTransit だけ検出** | OK |
| `*.Domain.csproj` に `CSharpFunctionalExtensions` | 検出する | OK |
| Application の `.cs` に `using CSharpFunctionalExtensions;` | 検出する | OK |
| 共有カーネルに `OneOf` / `Npgsql` | **両方を決定 3 違反として検出** | OK |
| 共有カーネル配下の **`.props`** に `CSharpFunctionalExtensions` | 検出しない | OK |
| 共有カーネル配下の **`.props`** に `Npgsql` | **決定 3 違反として検出** | OK |
| `src/Directory.Build.props` に `CSharpFunctionalExtensions` | **検出する**（免除しない） | OK |

**正例には変異を仕込んである。** 共有カーネル配下の `.cs` に `using MassTransit;` を併記し、
**それが検出されることを合格条件にした**。検出されなければ「`.cs` が走査されていない」
「所属プロジェクトを解決できていない」のいずれかであり、**正例の「検出しない」は空振りで通っている**
ことになる。これは #471 が記録した型（BANNED に足しても `scanTree` が当該ファイルを開かなければ
検出されない）の再発防止である。

## 関連

- 計画: ADR-0041（計画リポ）（本 IADR の起点）・
  ADR-0030（計画リポ）（選定基準 3 の原典）
- 実装 ADR: [IADR-0117](./IADR-0117_platform-shared-kernel-placement.md)（決定 2 に日付付き改訂注記を追加した）
- 仕様書: [作業仕様書 #500](../specs/20260815_issue-500_result-type-adr-0041-followup.md)
- 検査器: `scripts/check-backend-libraries.js`
