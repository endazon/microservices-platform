---
title: IADR-0219 SharedKernel の粒度はサービス単位（ユニット単位と併存）・Worker を標準構成へ加えて 8 要素とする
type: impl-adr
status: Accepted
related_ids:
  - ADR-0019
  - ADR-0030
  - ADR-0041
  - IADR-0056
  - IADR-0117
  - IADR-0196
  - IADR-0218
  - NFR
author: implementation-agent
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md (§SharedKernel の粒度・Worker の追加。2026-08-17 確定・fixed。pin 767a9d48)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md (Result 型の封じ込め。配置には言及しない)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (ライブラリ標準・選定基準 1〜4)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md (ユニット第一構成・決定 4)"
---

# IADR-0219: `SharedKernel` の粒度はサービス単位（ユニット単位と併存）・`Worker` を標準構成へ加えて 8 要素とする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-17
- 決定者: implementation-agent（利用者裁定 planning#390 の実装側への写像として）

## 起点・関連

- 関連する計画書 ID: 計画
  [`12_backend-application-stack`](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)
  **§`SharedKernel` の粒度・`Worker` の追加（2026-08-17 確定・`status: fixed`）** / 計画
  [`ADR-0041`](../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md)（Result 型の封じ込め）/ 計画
  [`ADR-0030`](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)（ライブラリ標準）/ 計画
  [`ADR-0019`](../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md)（決定 4）
- 関連する実装 ADR:
  [`IADR-0117`](./IADR-0117_platform-shared-kernel-placement.md)（**本 IADR が決定 5 の読み替えと §結果 フォローアップ 2 を改める。
  決定 1〜4 は有効なため `Accepted` のまま**）/
  [`IADR-0218`](./IADR-0218_gitkeep-standard-components-scope.md)（**本 IADR が決定 1・2・3 を改める。
  決定 4 は無傷。`Accepted` のまま**）/
  [`IADR-0056`](./IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット第一構成）/
  [`IADR-0196`](./IADR-0196_shared-kernel-result-library-allowlist.md)（共有カーネルの許可リスト）
- 関連する実装仕様書:
  [`docs/specs/20260817_iadr-0219_sharedkernel-worker-amendment.md`](../specs/20260817_iadr-0219_sharedkernel-worker-amendment.md)（母集合・実測）
- 起票: #455（バックエンドアプリケーション層標準）。裁定は planning#390

## コンテキストと課題

2026-08-17 の利用者裁定（裁定依頼 planning#390）により、計画
`12_backend-application-stack` に **§`SharedKernel` の粒度・`Worker` の追加** が新設された
（同書 §変更履歴 2026-08-17 行）。**この裁定は実装側の決定を 2 つ覆した。**

| 実装側の決定 | 裁定 |
| --- | --- |
| [`IADR-0218`](./IADR-0218_gitkeep-standard-components-scope.md) 決定 1: `SharedKernel` は `.gitkeep` の対象**外** | **対象に含める**（計画の構成図が正・**サービス単位**） |
| [`IADR-0218`](./IADR-0218_gitkeep-standard-components-scope.md) 決定 2: `Worker` は `Api` の別形（環流不要） | **`Worker` を標準構成へ追加**（7 → **8 要素**。`Api` と**排他**） |
| [`IADR-0117`](./IADR-0117_platform-shared-kernel-placement.md): サービス単位の `SharedKernel`（案 B）を却下 | **サービス単位を標準構成として認める。ユニット単位（`Platform.Shared.Kernel`）とは併存** |

**2 つの決定を 1 本にまとめるのは、同一の裁定から出ており、`.gitkeep` の件数という
1 つの帰結へ収束するためである。** 別々の IADR にすると、件数を両方から引くことになり片方が必ず古くなる。

### 裁定が示した、実装側が見落としていた 2 点

**この 2 点は、次に同じ形で判断する者のために記録する。**

#### 見落とし 1 — `SharedKernel` は計画の 3 箇所に載っており、構成図から外すだけでは他の 2 箇所が宙に浮く

`IADR-0117` は計画の**構成図**（`SharedKernel` をサービス単位の木に置く）だけを見て、
「構成図はサービス内の**論理レイヤ**であり、本決定は**物理配置の具体化**である」と読み替えた
（同 決定 5）。しかし `SharedKernel` は計画の次の **3 箇所**に載っている。

1. **§基本方針**（同書 L31）: 「Domain 層は **`SharedKernel` を除き**外部ライブラリへ依存しない。
   **Result 型は `SharedKernel` が公開する自前の型**（`Result` / `Result<T>` / `Error`）を用い、
   その内部実装としてのみ外部ライブラリを使う」
2. **§プロジェクト構成（サービス単位）の構成図**（同書 L42）
3. **§採用ライブラリ一覧 Application 層のライブラリ表**（同書 L95。`CSharpFunctionalExtensions` の備考が
   「**`SharedKernel` の内部でのみ使う**」と規定する）

**構成図だけを「論理レイヤ」と読み替えると、1 と 3 の `SharedKernel` が何を指すのかが宙に浮く。**
裁定はこの読み替えを採らず、**構成図を正**とした（`.claude/rules/adr.md` の大原則
「実装が先行して乖離したら実装を計画へ合わせる」）。

#### 見落とし 2 — 計画 `ADR-0041` は「サービスをまたいで単一の `Result` 型」を求めていない

`IADR-0117` は案 B（サービス単位）を「**サービス 11 個ぶんの Result 型が分裂**し、
`Platform.Shared.Contracts` の契約に載せられない」として却下した。

**`ADR-0041` の全文を走査して確かめた（実測。推測ではない）。**

| 検索語 | 件数 |
| --- | --- |
| `配置` | **0 件** |
| `サービス単位` / `サービスごと` / `単一` / `per-service` | **0 件** |
| `置き場` / `置く` | **0 件**（`置き` は「自前実装を**置き**」＝ ADR-0030 の引用と「置き換え」の 2 用法のみ） |

`ADR-0041` が `SharedKernel` について定めるのは**封じ込め**である。決定 2 の原文は
「**`SharedKernel` に自前の型（`Result` / `Result<T>` / `Error`）を定義し、その内部実装としてのみ
外部ライブラリを使う。`Domain` / `Application` / `Api` / `Infrastructure` は `SharedKernel` が公開する型のみを
参照し、外部ライブラリの型・名前空間を直接参照してはならない**」であり、**どこに置くか（粒度）には
一言も触れていない。** 決定 3 も「`SharedKernel` が推移的に持ち込んでよい外部パッケージは 1 つ」という
**依存の限定**であって、配置の要求ではない。

したがって **`IADR-0117` の「11 分裂するから却下」は、計画の要求ではなかった。**
それは「**`SharedKernel` の型をそのまま契約（`Platform.Shared.Contracts`）へ載せる**」という設計を採った
場合の帰結である。裁定は**併存**という置き分けでその懸念を解いた —— 境界をまたぐ型はユニット単位側へ置く
（計画 §`SharedKernel` の粒度）。**懸念そのものが誤りだったのではなく、前提にしていた設計が唯一の道ではなかった。**

> **`IADR-0117` の却下理由は書き換えない。** 2026-08-03 時点でその判断をした記録であり、
> 消すと**なぜ 11 分裂を恐れたのか**が読めなくなる。同 IADR には日付つき追記ブロックのみを置いた。

## 検討した選択肢

| | A. 裁定に従い併存として認め、`Worker` を 8 要素目に加える（採用） | B. `IADR-0117` を維持し、計画へ再環流して裁定のやり直しを求める | C. 計画へ完全に合わせ、`Platform.Shared.Kernel` を廃止して 11 個の per-service へ移す |
| --- | --- | --- | --- |
| 計画（`fixed`・裁定済み）との整合 | 満たす | **反する**（裁定を経た `fixed` を実装都合で差し戻す） | 満たす |
| `ADR-0041` 決定 2 の封じ込め | 満たす（境界をまたぐ型はユニット単位側） | 満たす | 満たす |
| 契約に載る `Result` / `Error` の同一性 | **保たれる**（ユニット単位側が持つ） | 保たれる | **壊れる**（11 個の別型。`IADR-0117` が挙げた事故そのもの） |
| [[IADR-0056]] 決定 3（ユニット外参照は Shared の 3 つ） | 変更不要 | 変更不要 | **`Platform.Shared.Kernel` を消すため再改定が要る** |
| `.gitkeep` の適用総数 | **55**（＋雛形 1） | 44 | 55 |
| 適用の停止 | しない | **止まる**（裁定待ちが再度発生する） | しない |

**案 C を採らないのは、裁定が「併存」と明示したためである。** 計画 §`SharedKernel` の粒度 は
「**per-service と per-unit は併存する**」「サービス境界をまたいで同一性が要る型（契約に載る
`Result` / `Error`）はユニット単位側へ置く」と書いており、ユニット単位の廃止は求めていない。

## 決定

### 決定 1 — `SharedKernel` の粒度はサービス単位。per-service の枠を標準構成として認め、ユニット単位と併存させる

**サービス単位の `SharedKernel` を標準構成の 1 要素として認める。**
[`IADR-0117`](./IADR-0117_platform-shared-kernel-placement.md) が案 B を却下した判断は、
本決定により**サービス単位の枠を置くことについてのみ**改まる。

**`Platform.Shared.Kernel`（ユニット単位）は消さない。併存として引き続き有効である。**
[[IADR-0056]] 決定 3 の「ユニット外参照は `src/platform/backend/Shared/` の **3 プロジェクト**」
（`IADR-0117` が 2 → 3 へ改定した値）は**そのまま**である。本決定は許容を減らさない。

**置き分けを明記する。**

| 置き場 | 何を置くか |
| --- | --- |
| **サービス単位** `src/<unit>/backend/Services/<Name>/src/<Name>.SharedKernel/` | **自サービスに閉じた共通基底**（そのサービスの中でだけ意味を持つ基底型・値オブジェクトの土台など） |
| **ユニット単位** `src/platform/backend/Shared/Platform.Shared.Kernel/` | **サービス境界をまたいで同一性が要る型** —— **契約に載る `Result` / `Error`**（BFF が集約し、`Platform.Shared.Contracts` のイベント契約が失敗を表現するため、単一の型でなければならない） |

**これは計画 §`SharedKernel` の粒度 が `Contracts` の置き分け（同書 §規範性・粒度・置き場）に倣って
定めた形をそのまま採ったものである。** 計画は同時に、この類推が
[`ADR-0019`](../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md)
決定 4 の**類推**であること（同決定が明文で定めるのは**イベント契約**であり共通基底ではないこと）も明記している。

**`.gitkeep` を置くことは「作ってよい枠」を意味しない。** 計画が明示するとおり、
境界をまたぐ型をサービス単位側へ置けば置き分けに反する。**枠の存在は許可ではなく、粒度の宣言である。**

### 決定 2 — `Worker` を標準構成へ加えて 8 要素とする。`Api` と排他

**標準構成は 7 要素から 8 要素になる。** 計画の構成図の並びは次のとおりである。

```text
Api            # エンドポイント定義・DI 構成・ProblemDetails 変換
Worker         # 常駐処理を主とするサービスの実行入口（Api と排他）
Application    # ユースケース（Wolverine ハンドラ）・検証・マッピング
Domain         # エンティティ・値オブジェクト（外部依存なし）
Infrastructure # EF Core・Redis・オブジェクトストレージ等の実装
Contracts      # 公開契約（proto・イベント・DTO）
SharedKernel   # Result / Error・共通基底（過度な共通化は避ける）
Tests          # Unit / Integration
```

**`Api` と `Worker` は同一サービス内で排他である。** いずれか一方のみを持ち、
**持たない側は空フォルダを作らない**（`.gitkeep` の対象外）。**実行入口は 1 サービスに 1 つであり、
「空の実行入口」という状態が存在しないため**である。したがって `Worker` の追加によって
`.gitkeep` の総数は増えない。

**区別の軸は責務ではなく、ホストの主目的である。**
`IADR-0218` 決定 2 は「`Worker` は `Api` の別形である（層としては同じ合成ルート）」という
**責務の軸**で読み替えた。裁定はその読み替えを採らず、**ホストの主たる駆動**で分けた。

> **HTTP 面を持つことは `Worker` であることと矛盾しない。**
> 実装側の実測（`IADR-0218` 決定 2 の表）では `ConversionService.Worker` / `IngestionService.Worker` は
> ともに `Microsoft.NET.Sdk.Web` で `WebApplication` を建て、HTTP 経路を配信している。
> 計画は**この実測を受けたうえで** 8 要素目に加えた ——「差は HTTP 面の広さであって有無ではない」ことは、
> `Worker` を `Api` と呼ぶ理由ではなく、**`Worker` が HTTP を持ってよい理由**である。

**したがって、実装の現況（2 サービスが `.Worker`）は「標準に無い名前」ではなくなり、
`Api` を不在として `.gitkeep` を置く必要も生じない。** 実測は 11 / 11 のサービスが
`Api` か `Worker` のいずれか 1 つを持つ。

### 決定 3 — `.gitkeep` は 55 件（＋雛形 1 件）。**本 PR では適用しない**

**実測（自分で数え直した。導出値は走査ではなく計算する）:**

| 要素 | 実体 | `.gitkeep` |
| --- | --- | --- |
| `Api` / `Worker`（**排他**） | 11 / 11（`Api` 9・`Worker` 2） | **0** |
| `Application` | 0 / 11 | 11 |
| `Domain` | 0 / 11 | 11 |
| `Infrastructure` | 0 / 11 | 11 |
| `Contracts` | 0 / 11 | 11 |
| `SharedKernel` | 0 / 11 | **11**（本 IADR 決定 1 により新たに対象） |
| `Tests` | 11 / 11 | 0 |
| **計** | | **55** |

**雛形**（`templates/unit-template/backend/Services/SampleService/`）は 8 要素中 6 つが実体
（`Api` / `Application` / `Contracts` / `Domain` / `Infrastructure` / `Tests`）。
`Worker` は `Api` と排他で対象外。**残る `SharedKernel` が新たに対象になり、`IADR-0218` 決定 3-6 の
「適用対象 0 件」は「`SharedKernel` 1 件」へ変わる。**

**適用（55 ＋ 1 件）は本 PR では行わない。** 本 PR は決定の記録に限る。
適用は次の波で別 issue として起票する（`.gitkeep` の置き場・フォルダ名・空ファイルとする点は
`IADR-0218` 決定 3-3 / 3-4 がそのまま有効である）。

## 理由

- **計画は絶対的な正である**（`.claude/rules/adr.md` の大原則）。`12_backend-application-stack` は
  `status: fixed` であり、2026-08-17 の改定は利用者裁定（planning#390）を経ている。
  実装側の読み替え（`IADR-0117` 決定 5・`IADR-0218` 決定 1・2）は、**裁定でその読み替え自体が否定された**。
- **決定 1 で `Platform.Shared.Kernel` を消さないのは、裁定が「併存」と明示したためだけではない。**
  `IADR-0117` が挙げた「契約に載る `Result` が 11 分裂する」という事故は**実在の危険**であり、
  裁定はそれを否定せず、**置き分け**という別の手段で塞いだ。ユニット単位側を廃止すれば、
  塞いだはずの穴が開く。
- **決定 2 で「ホストの主目的」を軸に採るのは、標準構成の判定を曖昧にしないためである。**
  責務の軸（`IADR-0218` 決定 2）は「Worker も合成ルートを持つから `Api` である」という読み替えを許すが、
  それは**標準に無い名前が現場にある**状態を温存する。計画が述べるとおり、
  それでは「標準に揃った」の判定が曖昧になる。
- **決定 3 で件数を本 IADR に持つのは、`IADR-0218` の 44 が 8 箇所に散っているためである。**
  現行値を 1 箇所に置き、`IADR-0218` 側は日付つき追記ブロックで本 IADR を指す
  （**同じ値を 2 箇所に置くと片方が必ず古くなる**）。

## 結果

- 良い影響:
  - **計画の字面と実装の記録が一致する。** `IADR-0218` §結果 が抱えていた
    「計画の字面（7 要素すべてに枠）と実装（6 要素）がずれる」というトレードオフが解消する。
  - `IADR-0117` フォローアップ 2（「構成図は論理レイヤ・物理配置は実装裁量、と計画へ明記を提案する」）が
    **決着した** —— 提案は却下され、構成図が物理配置を含むことが確定した。**未達ではない。**
  - `Worker` を名乗るサービスが標準構成の名前を持ち、`Api` を不在と誤記する経路が消えた。
- 悪い影響・トレードオフ:
  - **`.gitkeep` が 44 → 55 に増え、適用作業の規模が 1.25 倍になる**（＋雛形 1 件）。
  - **サービス単位の `SharedKernel` の枠が 11 個並ぶ。** 枠は「作ってよい」の意味ではないが、
    **読む人がそう受け取る危険は残る**（`IADR-0218` 決定 1 が懸念したとおりである）。
    歯止めは決定 1 の置き分けの明文化と、適用 PR が置く注記（`IADR-0218` 決定 3-5 の枠組みを流用する）である。
    **機械検査は置かない**（`IADR-0218` 決定 4 は無傷。「同型の事故が 2 回」の条件を満たしていない）。
  - **`IADR-0117` / `IADR-0218` を読む人は、本 IADR まで辿らないと現行値に行き着かない。**
    両者の本文へ日付つき追記ブロックを置いて緩和するが、**追記を読み飛ばせば古い値を掴む。**
- フォローアップ:
  1. **`.gitkeep` 適用 PR の起票**（55 件 ＋ 雛形 `SampleService.SharedKernel/.gitkeep` 1 件 ＋ 読み替えの注記）。
     本 PR では起票のみで実装しない。
  2. `src/ai-stock-trading` への追随 issue（向こうのリポジトリ。計画の規則は及ぶ）。
  3. **`Platform.Shared.Kernel` の実体は依然として未作成である**（`IADR-0117` フォローアップ 1）。
     本 IADR は置き場を増やすだけで、実体作成の時期は変えない。

## 関連

- Supersedes: なし（**部分改定が 2 件**。
  [`IADR-0117`](./IADR-0117_platform-shared-kernel-placement.md) は §決定 5 の読み替えと
  §結果 フォローアップ 2 のみを改め、決定 1〜4（`Platform.Shared.Kernel` の新設・許容 2 → 3・
  外部依存の限定・改定範囲の限定・実体は後続 issue が作る）は引き続き有効なため `Accepted` を維持する。
  [`IADR-0218`](./IADR-0218_gitkeep-standard-components-scope.md) は決定 1・2・3 を改め、
  決定 4（機械検査は置かない）は無傷であるため `Accepted` を維持する）
- Superseded by: なし
