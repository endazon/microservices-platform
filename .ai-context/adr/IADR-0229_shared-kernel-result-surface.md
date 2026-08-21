---
title: IADR-0229 Platform.Shared.Kernel が公開する Result / Error の操作面を確定し、default を失敗として扱う
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0056
  - IADR-0117
  - IADR-0196
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
  - planning:projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md
---

# IADR-0229: Platform.Shared.Kernel が公開する Result / Error の操作面を確定し、`default` を失敗として扱う

- 状態: Accepted
- 日付: 2026-08-21
- 起点: #455（バックエンドアプリケーション層標準への全面移行）／ #500（ADR-0041 への追随）

## コンテキスト

計画 `ADR-0030` は共有カーネル `Platform.Shared.Kernel` に Result / Error を置くと定め、
`ADR-0041` はその**内部実装としてのみ** `CSharpFunctionalExtensions` を使うことを認めた
（決定 1・2）。配置は [IADR-0117](./IADR-0117_platform-shared-kernel-placement.md) が、
許可リストは [IADR-0196](./IADR-0196_shared-kernel-result-library-allowlist.md) が確定させ、
機械検査（`scripts/check-backend-libraries.js` の `SHARED_KERNEL_ALLOWED`）も配備済みだった。
**残っていたのは実体だけである。**

`ADR-0041` はフォローアップとして「**`SharedKernel` が公開する操作の一覧（`Bind` / `Map` /
`Tap` / `Combine` / 非同期版のうち何を出すか）を実装ガイドで確定する**」を挙げている。
本 IADR がその回答である。

## 決定 1: 公開する操作を 8 種＋非同期 3 種に限る

| 型 | 公開する操作 |
| --- | --- |
| `Result` | `Success` / `Failure` / `Bind`（値なし・値あり）/ `Tap` / `Match` / `Combine` / `BindAsync` |
| `Result<T>` | `Success` / `Failure` / `Map` / `Bind`（値あり・値なし）/ `Tap` / `Ensure` / `Match` / `Discard` / `MapAsync` / `BindAsync` |
| `Error` | `Code` / `Message` / `Kind` ＋ 種別ごとのファクトリ 5 種 ＋ `Uninitialized` |

**選別を怠ると封じ込めが形骸化する**（`ADR-0041` §結果のトレードオフ）。したがって
「外部ライブラリが持っているから」を理由に操作を増やさない。**必要になった時点で足す。**

`Combine` は**最初の失敗を返す**（全失敗の集約はしない）。集約が要る場面が実際に出るまで、
`Error` を複数持つ表現を導入しない —— 導入すると `Error` の形が変わり、全層に波及する。

## 決定 2: `default` は成功として扱わない

`Result` / `Result<T>` は構造体であり、`default(Result<T>)` で生成され得る。**初期化されて
いない値を成功へ倒すと、失敗が黙って成功に化ける。**

したがって内部に初期化フラグを持ち、`default` は `Error.Uninitialized` を持つ**失敗**として
振る舞う。参照型 `T` の場合に `default` が「成功（値は `null`）」になる経路を塞ぐためである。

これは本リポジトリが繰り返し踏んできた「**沈黙の exit 0**」（#797）と同じ型である
—— **「何も無い」と「問題が無い」を同じ出力にしない。**

## 決定 3: 封じ込めをリフレクションで機械的に固定する

`scripts/check-backend-libraries.js` は「**どのパッケージを参照してよいか**」までしか見ない。
**公開面に外部型が漏れているかは見ない。** csproj の検査だけでは `ADR-0041` 決定 2 の
「外部ライブラリの型・名前空間を直接参照してはならない」を担保できない。

`Platform.Shared.Kernel.Tests` に**リフレクションで公開面を走査するテスト**を置き、
公開型のメソッド・プロパティ・フィールド・コンストラクタのシグネチャに
`CSharpFunctionalExtensions` 名前空間の型が現れないことを固定する。ジェネリック引数・
配列要素・by-ref も展開して見る。

**併せて「内部実装では実際に使っている」ことも固定する。** 使っていなければ、この検査は
「そもそも依存が無い」ことを見ているだけになり、封じ込めを試験していない（0 件走査で緑を返す門と
同じ穴である）。

### 変異試験（実測）

| 変異 | 結果 |
| --- | --- |
| `Platform.Shared.Kernel.csproj` へ `Npgsql` を追加 | `check-backend-libraries.js` が **EXIT=1**（`SharedKernel 依存規律`） |
| `Platform.Shared.Contracts.csproj` へ `CSharpFunctionalExtensions` を追加 | 同 **EXIT=1**（不採用ライブラリの新規混入） |
| `Result` に外部型を返す公開メソッドを追加 | `公開面に外部ライブラリの型が現れない` が **FAIL**（漏れた型名を出力） |

## 決定 4: 既存サービスへの適用は本 IADR の射程外

本作業は**型を置くところまで**である。11 サービスを Result 型へ移す作業は各サービスの
再実装 issue（#438〜#451）に属する。ここで全サービスへ手を入れると 1 PR が
**400 行の起票規格**を大きく超え、監査が成立しない。

## 決定 5: テストは xUnit v2 を使う

CPM（`src/Directory.Packages.props`）の `xunit.runner.visualstudio` は **2.8.2（v2 系）**に
固定されており、v3 には 3.x が要る。**CPM は 1 パッケージに 1 バージョンしか持てない**ため、
v3 へ移ると既存の全テストプロジェクトが同時に移らざるを得ない。雛形
（`templates/unit-template/backend/.../SampleService.Tests.csproj`）も同じ理由で
「**切替 issue の完了まで `xunit.v3` を参照するプロジェクトを作ってはならない**」と明記している。

表明は `AwesomeAssertions` を使う（`FluentAssertions` は商用化のため不採用・ratchet 管理下）。

> ［2026-08-21 追記 / #455 A-3］**`FluentAssertions` は ratchet 管理下ではなくなった。** 段 1（platform）・
> 段 2（knowledge）で全 14 プロジェクトを `AwesomeAssertions` へ移し、baseline エントリと
> `src/Directory.Packages.props` の `PackageVersion` を削除した。`BANNED` 一覧には残るため、
> 再混入は引き続き検査器が fail させる。

## 棄却した案

| 案 | 棄却の理由 |
| --- | --- |
| 型エイリアス（`global using Result = CSharpFunctionalExtensions.Result;`） | `ADR-0041` 決定 2 が明示的に退けている。**戻り値の型は隠せるが、拡張メソッドと `Bind` / `Map` のチェーンから外部 API がそのまま漏れる** |
| `Result<T>` をクラスにする | `default` 問題は消えるが、ホットパスでの割り当てが増える。決定 2 の初期化フラグで同じ安全性が得られる |
| 外部ライブラリの操作をそのまま全部公開する | 選別を怠ると封じ込めが形骸化する（`ADR-0041` §トレードオフ）。差し替え時に呼び出し側が全滅する |
| `Combine` で全失敗を集約する | `Error` を複数持つ表現が要り、全層へ波及する。**必要になってからでよい** |

## 関連

- Supersedes: なし
- 部分改定: なし（[IADR-0117](./IADR-0117_platform-shared-kernel-placement.md) の「実体は未作成」という現況記述が本作業で解消される）
- 関連: [IADR-0196](./IADR-0196_shared-kernel-result-library-allowlist.md)（許可リスト）／ #797（沈黙の exit 0。決定 2 の考え方）


---

## ［2026-08-21 追記 / #455］決定 1 の「8 種」は実装と合わない。数と一覧を実測へ揃える

**クロス監査の指摘により訂正する。表の中身は実装と一致しており、誤っていたのは見出しの数と、
状態参照面の欠落である。**

### 同期の操作は 8 ではなく 9 名（メソッド実数 14）

実測した公開メソッド名: `Success` / `Failure` / `Map` / `Bind` / `Tap` / `Ensure` / `Match` /
`Combine` / `Discard` の **9 名**。オーバーロードを数えると `Result` 6 ＋ `Result<T>` 8 の **14 本**。
ファクトリ 2 件（`Success` / `Failure`）を除いても 7 で、**8 には落ちない。**

**非同期 3 種は一致している**（`Result.BindAsync` / `Result<T>.MapAsync` / `Result<T>.BindAsync`）。

### 決定 1 の表に無かった公開面（状態参照・等価性）

`ADR-0041` フォローアップは「**公開する操作の一覧を確定する**」を求めている。操作だけを挙げて
状態参照面を落とすと、**「選別した」ことにならない。** 次を一覧へ加える。

| 型 | 追加で公開しているもの |
| --- | --- |
| `Result` / `Result<T>` | `IsSuccess` / `IsFailure` / `Error`（＋ `Result<T>` は `Value`） |
| 同上 | `Equals` / `GetHashCode` / `ToString` / `operator ==` / `operator !=`（`IEquatable<T>` 実装） |

**これらを増やす予定は無い。** 決定 1 の趣旨（外部ライブラリが持っているからという理由で操作を
増やさない）はそのまま有効である。

### 併せて: 作業仕様書の記述が過大だった

`.ai-context/specs/20260821_issue-455_platform-shared-kernel.md` は
「`Map` / `Bind` / `Tap` / `Match` / `Ensure` / `Combine` **と、それぞれの `Task` 版**」と書いているが、
**`TapAsync` / `MatchAsync` / `EnsureAsync` / `CombineAsync` は存在しない。**
**実装は本 IADR（正本）に合っており、仕様書側が過大に書いていた。**
仕様書は凍結記録のため書き換えず、正本である本 IADR にこの追記を置く。

---

## ［2026-08-21 追記 / #455］封じ込めの限界 —— **推移参照は構造では断てない**（実測）

クロス監査（C-3）が「`Platform.Shared.Kernel.csproj` の `PackageReference` に
`PrivateAssets="all"` を付けて、推移参照をコンパイラ段で断つべきだ」と提案した。
**もっともな提案であり、実際に試した。落ちた。**

```console
$ # Platform.Shared.Kernel.csproj に PrivateAssets="all" を付けて
$ dotnet build src/platform/backend/backend.slnx
CSC : error CS0012: The type 'Result<,>' is defined in an assembly that is not referenced.
      You must add a reference to assembly 'CSharpFunctionalExtensions, Version=3.7.0.0, …'
      [Platform.Shared.Kernel.Tests.csproj]
CSC : error CS0012: The type 'UnitResult<>' is defined in an assembly that is not referenced. …
```

### なぜ落ちるのか

`Result` / `Result<T>` は **`readonly struct`** である。**値型の private フィールドの型は
レイアウト＝ABI の一部**であり、その構造体を**使うだけ**の消費側にもアセンブリ参照が要る。
参照型（class）ならフィールド型を解決せずに使えるが、本設計は
[決定 2 の理由](#決定-2-default-は成功として扱わない)（ホットパスの割り当てを避ける）で struct を採っている。

### したがって、封じ込めの担保は次の 2 段である。構造ではない

| 段 | 何を止めるか | 限界 |
| --- | --- | --- |
| `scripts/check-backend-libraries.js` の `using` 検査 | 消費側が `using CSharpFunctionalExtensions;` を書くこと | **完全修飾（`CSharpFunctionalExtensions.Result.Success<…>()`）は素通りする**（同スクリプト冒頭が二次防衛と自認） |
| `EncapsulationTests` のリフレクション走査 | SharedKernel の**公開面**へ外部型が出ること | 消費側の書き方は見ない |

**「消費側が外部アセンブリを参照できてしまう」状態そのものは残る。**
`ADR-0041` 決定 2 が求めるのは「外部ライブラリの**型・名前空間を直接参照してはならない**」であり、
**その規律は上の 2 段で担保している**が、**参照可能性そのものを構造で断ててはいない。**
本 IADR が決定 3 で「機械的に固定する」と書いた射程は**ここまで**である。

### 断ちたくなったときの選択肢（採らない理由つき）

| 案 | 採らない理由 |
| --- | --- |
| `Result` を class にする | `default` 問題は消えるが、ホットパスの割り当てが増える。決定 2 の初期化フラグで同じ安全性が得られている |
| CFE を捨てて完全自前実装にする | `ADR-0041` 決定 1 が明示的に `CSharpFunctionalExtensions` を採ると定めた。覆すには計画 ADR の改定が要る |
| フィールドを自前の値（`bool` ＋ `T` ＋ `Error`）にして CFE をメソッド本体でのみ使う | **CFE が実質的に何もしなくなる。** 採用の意味が失われ、`ADR-0041` 決定 1 の趣旨（自前実装の保守コストを避ける）に反する |

**現時点では受け入れる。** 断つ必要が生じたら `ADR-0041` の改定として扱う。
