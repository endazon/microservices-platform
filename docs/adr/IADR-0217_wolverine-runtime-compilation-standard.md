---
title: IADR-0217 Wolverine のコード生成は実行時コンパイルを標準とし、事前 codegen は採らない
type: impl-adr
status: Accepted
related_ids:
  - ADR-0027
  - ADR-0030
  - ADR-0041
author: implementation-agent
created: 2026-08-16
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md (Wolverine 移行と移行時の必須設定)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (バックエンドライブラリ標準)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Result 型の外部ライブラリ)"
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md (ライブラリ表と Wolverine 移行チェックリスト)"
---

# IADR-0217: Wolverine のコード生成は実行時コンパイルを標準とし、事前 codegen は採らない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: implementation-agent（計画 `ADR-0027` の下位決定として）

## 起点・関連

- 関連する計画書 ID: 計画 `ADR-0027`（Wolverine 移行・Accepted）/ 計画 `ADR-0030`（ライブラリ標準）/
  計画 `ADR-0041`（Result 型の外部ライブラリ・`Proposed`。**`Proposed` は決定の効力を停止しない**）
- 関連する実装仕様書:
  [`docs/specs/20260816_issue-455_wolverine-codegen-mode.md`](../specs/20260816_issue-455_wolverine-codegen-mode.md)
- 関連する実装 ADR: [`IADR-0196`](./IADR-0196_shared-kernel-result-library-allowlist.md)（`SHARED_KERNEL_ALLOWED`）/
  [`IADR-0117`](./IADR-0117_platform-shared-kernel-placement.md)（`Platform.Shared.Kernel` の置き場）
- 起票: #455（バックエンドアプリケーション層標準）の断片

## コンテキストと課題

`src/Directory.Packages.props` は #455 で「各サービスの再実装 issue（#438〜#451）が参照するための中央定義」を
持ったが、**`WolverineFx.RuntimeCompilation` が未宣言**である。

ただし**版を足すだけでは意味がない。** 可変機能ユニット `ai-stock-trading` の環流記録
（`src/ai-stock-trading/feedback/20260804_adr0027-wolverine-migration-caveats.md`）は、
Wolverine 6.24.5 / net10.0 を実際に構成した**実測**として次を報告している。

> Wolverine 6 系はコア本体（`WolverineFx`）からランタイムコンパイラ（Roslyn）を**別パッケージへ分離**しており、
> 既定の `TypeLoadMode.Dynamic` のまま `UseWolverine(...)` したホストは**起動時に例外で停止する**。

そのうえで回避策を 2 案示し、**どちらを標準に据えるかの判断を基盤側へ明示的に差し戻していた**
（同記録 L121-122「基盤側は本番のコンテナ起動特性の要求が AST より厳しい可能性があり、案 B を標準に据える
判断もあり得る。どちらを標準とするかは基盤側で決めていただきたい」）。

| 案 | 内容 | 代償 |
| --- | --- | --- |
| **A** | `WolverineFx.RuntimeCompilation` を参照する | 実行時にコード生成する。起動時コストと配布物の増加 |
| **B** | 事前 codegen（`dotnet run -- codegen write`）＋ `TypeLoadMode.Static` | ビルド手順が増える。生成物の追随が要る |

**この決定が無いと、計画 `12_backend-application-stack` の「Wolverine 移行チェックリスト」8 手順の 1 手目
（手順 1 の対応表作成に続く手順 2「全サービスへ参照追加」）が踏み出せない。**

### 差し戻しは既に上位で解決している（読み手が最初に確認すべき事実）

環流記録の差し戻しは 2026-08-04 に書かれたが、**同日の計画側の裁定（planning#181）で解決済み**である。
計画 `ADR-0027` §決定 の「［2026-08-04 追記・移行時の必須設定］」1 が、次のとおり案 A を確定している。

> **1. `WolverineFx.RuntimeCompilation` の参照が必須である。** …… 事前生成（`codegen write` ＋
> `TypeLoadMode.Static`）は採らない —— 生成コードの版管理という運用上の決定を伴い、起動時間の要求が
> 顕在化していない現段階でその負債を負う理由がないためである。

`12_backend-application-stack`（`fixed`）も Infrastructure 層のライブラリ表へ
`WolverineFx.RuntimeCompilation` を**★採用**として追加済みであり、移行チェックリスト手順 2 は
「`WolverineFx.RuntimeCompilation` を全サービスへ参照追加する」と定めている。

**したがって本 ADR は未決を新たに決めるものではなく、上位の確定を実装側の標準として確定・記録し、
必要な CPM 宣言を伴わせるものである。** この経緯を書くのは、環流記録だけを読んだ次の実装者が
「基盤側は未決」と誤解するのを防ぐためである。

## 検討した選択肢

軸を明示して比較する。**軸 1 だけで結論が決まる**が、上位決定が将来見直される場合に備えて他の軸も残す。

| 軸 | 案 A（実行時コンパイル） | 案 B（事前 codegen ＋ Static） | 判定 |
| --- | --- | --- | --- |
| **1. 上位決定との整合（拘束）** | 計画 `ADR-0027` §決定 の追記が**必須**と明記。`12_backend-application-stack` のライブラリ表に★採用 | 同追記が**「採らない」と明記** | **A**。案 B は `Accepted` / `fixed` への逸脱にあたる |
| **2. 起動の確実性** | 現物のアセンブリから毎回生成するため、生成物と実装がずれない | 生成物の再生成漏れは**古い挙動のまま緑**になる。検査を別途置かないと検出できない | **A** |
| **3. 配布物の大きさ・起動コスト** | Roslyn を同梱するためイメージが大きくなり、起動時に生成が走る | 小さく・速い | **B**（案 A が負う唯一の不利） |
| **4. ビルド手順の複雑さ** | 増えない | サービス数分の `codegen write` 実行・生成物の版管理・再生成差分の CI 検査が要る。生成にホスト起動を伴うため、DB / ブローカ接続なしで起動できる構成を保つ制約も付く | **A** |
| **5. 実測の裏づけ** | `ai-stock-trading` が全 10 サービスで案 A を実運用し、起動とトポロジを実 RabbitMQ で検証済み（`AST/IADR-0129` 決定 6） | 実測なし | **A** |

## 決定

### 決定 1: 案 A を基盤の標準とする

**`WolverineFx.RuntimeCompilation` を参照し、Wolverine のコード生成は実行時コンパイルで行う。**
事前 codegen（`codegen write`）＋ `TypeLoadMode.Static` は**採らない**。

`src/Directory.Packages.props` へ次を宣言する。版は既存の `WolverineFx` 系 3 つと**同値に揃える**
（family の版ずれを作らない）。

```xml
<PackageVersion Include="WolverineFx.RuntimeCompilation" Version="6.24.4" />
```

**`.csproj` への `PackageReference` 追加（＝チェックリスト手順 2）は本 ADR の射程外**であり、
各サービスの再実装 issue（#438〜#451 / #441）が移行と同時に行う。本 ADR は方式の確定と中央定義の宣言までである。

### 決定 2: `CSharpFunctionalExtensions` も同時に CPM へ宣言する

計画 `ADR-0041` 決定 1 が採用を確定した Result 型ライブラリを、同じ中央定義へ宣言する。

```xml
<PackageVersion Include="CSharpFunctionalExtensions" Version="3.7.0" />
```

### 決定 3: `BANNED` 掲載と CPM 宣言は衝突しない（明記する）

**次の読み手は必ずここで止まる** —— `CSharpFunctionalExtensions` は
`scripts/check-backend-libraries.js` の `BANNED` に載っている。それでも CPM へ宣言してよい。理由は 2 つある。

1. **`BANNED` に残しているのは「参照の可否」の話である。** `Platform.Shared.Kernel` **以外**での
   直接参照を素通りさせないために残しており、共有カーネルのときだけ `bannedListFor()` が
   `SHARED_KERNEL_ALLOWED` を差し引く（[`IADR-0196`](./IADR-0196_shared-kernel-result-library-allowlist.md)）。
   `BANNED` から消すと、`Domain` / `Application` / `Api` / `Infrastructure` の直接参照が検出できなくなる。
2. **`PackageVersion` は違反にしない設計である。** 同スクリプト冒頭のコメントが明示している ——
   検査対象は `.csproj` / `.props` / `.targets` の `PackageReference`・`GlobalPackageReference` と
   `.cs` の `using` だけであり、**CPM のバージョン定義は対象外**である。baseline を消化し切るまで
   不採用パッケージの版定義が `src/Directory.Packages.props` に正当に残る設計であるため、
   ここを違反にすると残件と同数の偽陽性が出る。

すなわち **CPM の版定義は「どこで使えるか」ではなく「使うときにどの版か」を決めるもの**であり、
参照の可否は別の検査が持つ。よって本宣言で CI は赤くならない（実測は作業仕様書 §検証）。

### 決定 4: 未参照エントリであることを中央定義のコメントに残す

`src/Directory.Packages.props` のコメントブロックは既に「CPM の未参照エントリは無害」と許容している。
本決定で足す 2 件も同じ扱いであることを、由来（本 ADR / 計画 `ADR-0027` / 計画 `ADR-0041`）とともに追記する。

## 理由

- **軸 1 が拘束である。** 計画 `ADR-0027` は `Accepted`、`12_backend-application-stack` は `fixed` であり、
  案 B を採ることは確定済み計画への無断逸脱にあたる。実装側で覆すには新たな計画 ADR か
  `/plan-feedback` による差し戻しが要るが、**そのための材料（起動時間・イメージサイズの数値要求）が
  現時点で存在しない**。
- **軸 2・4 が同じ方向を向いている。** 案 B の代償は「手順が 1 段増える」ことに留まらず、
  **再生成漏れが静かに古い挙動を残す**点にある。Wolverine 移行では既に 2 つの「静かに壊れる」退行
  （pub/sub の competing consumer への退行・`internal` 実装型に依存するハンドラの受信時失敗）が
  実測されており、**同種の静かな失敗をもう 1 つ増やす選択は割に合わない**。
- **軸 3 の代償は測れる形で残す。** 現時点で起動時間・イメージサイズの数値要求は非機能要件として
  顕在化していない。顕在化した時点で測って再評価する（下記フォローアップ）。

## 結果

- 良い影響:
  - Wolverine 移行チェックリスト手順 2 に着手できる（`.csproj` に何を足せばよいかが確定した）。
  - 生成物の版管理・再生成差分検査という運用を新設せずに済む。
  - `ai-stock-trading` と同じ方式になり、両リポジトリで移行手順とトラブルシュートを共有できる。
- 悪い影響・トレードオフ:
  - **Roslyn を同梱するためコンテナイメージが大きくなる。**
  - **起動時にコード生成が走り、起動時間とピークメモリが増える。** 起動の遅さはローリング更新・
    ヘルスチェックの猶予・オートスケールの応答性に効く。
  - 却下した案 B が得たはずのもの（起動の速さと配布物の小ささ）は、上記の裏返しとして失われる。
    **再検討したくなったときに必要な情報**は §検討した選択肢 の軸 3・4 に残した。
- フォローアップ:
  - **再評価条件**（どちらかを満たしたら案 B を測り直す。`AST/IADR-0129` 決定 6 と同じ条件に揃えた）:
    1. 起動時間またはイメージサイズの**数値要求が非機能要件として顕在化した**時点。
    2. 生成コードの管理コストが読める時点（＝サービス数と再生成頻度が確定した時点）。
  - `.csproj` への `PackageReference` 追加は各サービスの再実装 issue（#438〜#451 / #441）で行う。
  - `templates/unit-template/` の CPM サンプルへの追加は、**同雛形へ `PackageReference` を足すときに同時に行う**
    （同ファイルは「雛形の 7 プロジェクトが `PackageReference` する全パッケージ」と自ら範囲を定めており、
    参照の無い版定義を先に足すとその範囲宣言と食い違う）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 出典（他プロジェクト）: `ai-stock-trading` の環流記録
  `src/ai-stock-trading/feedback/20260804_adr0027-wolverine-migration-caveats.md`（実測の一次資料）と
  `AST/IADR-0129`（同リポジトリの Wolverine トポロジ決定）。裁定は planning#181。
