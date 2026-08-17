---
title: IADR-0218 標準構成 7 要素の .gitkeep は 4 要素 × 11 サービスへ適用し、Worker は Api の別形・SharedKernel は集約先が持つ
type: impl-adr
status: Accepted
related_ids:
  - ADR-0019
  - ADR-0030
  - IADR-0027
  - IADR-0056
  - IADR-0060
  - IADR-0116
  - IADR-0117
  - NFR
author: implementation-agent
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md (§規範性・粒度・置き場。2026-08-04 確定・fixed)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (ライブラリ標準・選定基準 2)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md (ユニット第一構成・決定 4)"
---

# IADR-0218: 標準構成 7 要素の `.gitkeep` は 4 要素 × 11 サービスへ適用し、`Worker` は `Api` の別形・`SharedKernel` は集約先が持つ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-17
- 決定者: implementation-agent（計画 `12_backend-application-stack` §規範性・粒度・置き場 の下位決定として）

## 起点・関連

- 関連する計画書 ID: 計画
  [`12_backend-application-stack`](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)
  §規範性・粒度・置き場（**`status: fixed`**・利用者裁定 planning#180）/ 計画
  [`ADR-0030`](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)（選定基準 2「構成要素を増やさない」）/
  計画 [`ADR-0019`](../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md)（決定 4）
- 関連する実装 ADR: [`IADR-0117`](./IADR-0117_platform-shared-kernel-placement.md)（共有カーネルの物理配置。**本 IADR の決定 1 が依拠する**）/
  [`IADR-0027`](./IADR-0027_composability-folder-structure.md)（`Foundation` / `Composable` 構造）/
  [`IADR-0056`](./IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット第一構成）/
  [`IADR-0060`](./IADR-0060_submodule-unit-operations.md)（ユニット雛形）/
  [`IADR-0116`](./IADR-0116_reimplementation-branching-and-pr-policy.md)（1 issue = 1 PR）
- 関連する実装仕様書:
  [`docs/specs/20260817_issue-455_gitkeep-standard-components.md`](../specs/20260817_issue-455_gitkeep-standard-components.md)（母集合・現状表・実測）
- 起票: #455（バックエンドアプリケーション層標準）の断片

## コンテキストと課題

計画 §規範性・粒度・置き場（2026-08-04 確定）は「**7 つは全リポジトリ共通の標準構成である。実体が無いものは、
空のフォルダを作り `.gitkeep` だけを置く**（`.csproj` は作らない）」と定める。理由は「何も無いと、その構成要素が
**意図的に不在なのか単に作り忘れなのかが一見して分からない**」ことである。

**実測（基点 `d2901da4`）: `git ls-files 'src/**/.gitkeep'` は 0 件。** バックエンドには 1 件も適用されていない
（フロント雛形には 11 件・`docs/` には 15 件が適用済み）。11 サービスすべてが
`src/<Name>.{Api,Worker}` と `tests/<Name>.{Api,Worker}.Tests` の **2 プロジェクトに畳まれており**、
`Application` / `Domain` / `Infrastructure` / `Contracts` は 1 つも存在しない。

素朴に適用すると 11 サービス × 5 要素 = 55 になるが、**そのうち 2 つの列は素朴に数えられない**。

1. **`SharedKernel`**: 計画の図はサービス単位の木に置くが、本リポジトリは
   [`IADR-0117`](./IADR-0117_platform-shared-kernel-placement.md)（**Accepted**）で
   案 B「サービス単位に `<Name>.SharedKernel` を置く（計画構成図どおり）」を**明示的に却下**し、
   `src/platform/backend/Shared/Platform.Shared.Kernel` への集約を採っている。
2. **`Api`**: `ConversionService` / `IngestionService` は `.Api` ではなく **`.Worker`** であり、
   **`Worker` は 7 要素表に存在しない**。

この 2 点を決めないと、適用作業が「枠を可視化する」どころか**誤った不在を宣言する**。

## 検討した選択肢

### `.Worker` の扱い（本 IADR の唯一の新しい決定）

| | α. `Worker` は `Api` の別形＝実体あり（採用） | β. `Api` は不在とみなし `.gitkeep` を置く | γ. 7 要素表へ `Worker` を足すことを計画へ環流する |
| --- | --- | --- | --- |
| 実測との整合 | **合う**（両 Worker は `Microsoft.NET.Sdk.Web`・`WebApplication`・エンドポイントを Map する） | **合わない**（HTTP を出しているのに「Api 不在」と書く） | 合うが、要素を 1 つ増やす |
| 読み手へ伝わること | 「この枠は埋まっている」 | 「HTTP の口はここに作れ」＝**誤り**（既にある） | 「Api と Worker は別の要素」＝**層としては同じ**なので誤り |
| 計画との関係 | 読み替え（実名はホスト種別に合わせる。既に前例あり） | 計画に忠実に見えるが実態を誤記する | **計画の改定が要る**（fixed。裁定待ちで適用が止まる） |
| ADR-0030 選定基準 2（構成要素を増やさない） | 満たす | 満たす | **反する** |
| 適用の総数 | 44 | 46 | 44（＋計画改定後に表を 8 要素へ） |

### `SharedKernel` の扱い（上位で決着済みの記録 ＋ 適用範囲の帰結）

| | 集約先が持つ＝per-service には置かない（採用） | サービス単位に `SharedKernel/.gitkeep` を置く |
| --- | --- | --- |
| [[IADR-0117]]（Accepted）との整合 | 満たす | **反する**（案 B を却下した決定と読み合わせが割れる） |
| 読み手へ伝わること | （注記を置けば）「集約先にある」 | 「ここに作ってよい枠」＝**Result 型 11 分裂**という IADR-0117 が避けた事故の入口 |
| 計画の図との整合 | 論理粒度は一致・物理配置は読み替え | 字面に一致 |
| 適用の総数 | 44 | 55 |

## 決定

### 決定 1 — `SharedKernel` の粒度は「サービス単位」（上位の確定）。ただし物理配置は集約済みであり、per-service の `.gitkeep` は置かない

**計画の記述は「サービス単位」である。これは本 IADR が決め直すものではなく、記録する。** 出典は 3 つ。

1. 計画 §プロジェクト構成の見出しが**「（サービス単位）」**であり、その木が
   `src/ ├── Api … ├── SharedKernel └── Tests` と**同じ木の中に** `SharedKernel` を置いている。
2. 同計画 L29「Domain 層は **`SharedKernel` を除き**外部ライブラリへ依存しない。**Result 型は
   `SharedKernel` が公開する自前の型**を用い、その内部実装としてのみ外部ライブラリを使う」。
3. 同計画 L50 は `Contracts` について「**per-service と per-unit は併存する**」と明示するが、
   **`SharedKernel` には同種の記述が無い**。**書き分けられている以上、無い側を「両方あり」と読まない。**

**しかし、本リポジトリの物理配置は [[IADR-0117]]（Accepted）が確定済みである。**
同 IADR は案 B（サービス単位。計画構成図どおり）を却下し、`Platform.Shared.Kernel` への集約を採った。
却下理由は「サービス 11 個ぶんの Result 型が分裂し、BFF の集約と `Platform.Shared.Contracts` の
イベント契約に載せられない」である。`docs/tech/tech-requirements.md` も
**「共有カーネルはサービス単位に置かない」**と書いている。

**本 IADR は IADR-0117 を覆さない**（覆すには改定 IADR が要る。`CLAUDE.md` 禁止事項）。したがって:

- **`.gitkeep` の適用対象から `SharedKernel` を外す。** サービス配下に `<Name>.SharedKernel/` を作らない。
- 理由は「不在だから置かない」ではない。**`.gitkeep` は「まだ無い」と「置かないと決めた」を区別しない**からである。
  置けば次に読む人へ「作ってよい枠」と伝わり、**IADR-0117 が避けた 11 分裂そのものを招く**。
  枠の可視化という目的は、**空フォルダではなく注記**（決定 3）で果たす。

> **［環流が要る点］** IADR-0117 のフォローアップ 2「構成図はサービス内の論理レイヤであり物理配置は実装裁量、
> と計画へ明記を提案する」は**未達である**（2026-08-04 の §規範性 追補にも入っていない。実測）。
> §規範性 はむしろ「7 つは全リポジトリ共通の標準構成である」と物理的に読める書き方である。
> **`/plan-feedback` で計画側の裁定を仰ぐ。**

**再評価条件**: 計画側が「集約された構成要素にも per-service の枠を置く」と裁定した場合、
`.gitkeep` は 44 → 55 になる。その場合は本決定 1 を改定 IADR で覆し、
**同時に「この枠は集約先にあり、ここには作らない」と読める内容**（空でない `.gitkeep` か README）を要求する。

### 決定 2 — `.Worker` は `Api` の別形として「実体あり」と扱う（案 α）

`ConversionService` / `IngestionService` の `src/<Name>.Worker/` を **7 要素の `Api` が埋まっている状態**とみなす。
`Api/.gitkeep` は置かない。

**決め手は 1 本の軸である: その枠が果たすべき役割を、実在するプロジェクトが果たしているか。**
計画は `Api` を「**エンドポイント定義・DI 構成・ProblemDetails 変換**」と定義する。実測（推測ではない）:

| 実測点 | ConversionService.Worker | IngestionService.Worker |
| --- | --- | --- |
| `.csproj` の SDK | **`Microsoft.NET.Sdk.Web`** | **`Microsoft.NET.Sdk.Web`** |
| ホスト生成 | `WebApplication.CreateBuilder(args)` | `WebApplication.CreateBuilder(args)` |
| エンドポイント | `/internal/introspection`・`/health/live`・`/health/ready`・**`/jobs` 配下 5 経路** | `/internal/introspection` |
| DI 構成（合成ルート） | `Program.cs` が全依存を登録する | `Program.cs` が全依存を登録する |

**両者とも ASP.NET Core アプリである。** 差は HTTP 面の広さであって、有無ではない。
`.Worker` という名はホストの主たる駆動（メッセージ消費）を表しており、**層としては `Api` と同じ合成ルート**である。

**前例がある。** `docs/tech/tech-requirements.md` は `Tests` について
「`.csproj` の実名はサービスのホスト種別に合わせてよい（実装の現況は `<Name>.Api.Tests` / `<Name>.Worker.Tests`）」と
既に定めている。**同じ読み替えを `Api` へ適用するだけ**であり、新しい原理を持ち込まない。

**却下した案の代償と再評価条件**

- **案 β（`Api` 不在として `.gitkeep`）の代償**: `ConversionService` は `/jobs` で 5 経路を配信しているのに、
  その隣へ「Api は不在」と書く。読んだ人が**2 つ目の HTTP ホストを作る**と、同一サービスに合成ルートが 2 つ並び、
  DI 構成・health・introspection が割れる。**枠を可視化するはずの規則が、誤情報を配る。**
  - **再評価条件**: Worker から HTTP 面を完全に撤去したとき（`Microsoft.NET.Sdk.Web` → `Microsoft.NET.Sdk.Worker`、
    introspection を HTTP 以外の経路へ移す）。**そのときは `Api` が真に不在になるので β へ切り替える。**
- **案 γ（7 要素表へ `Worker` を足す）の代償**: 計画 ADR-0030 選定基準 2「構成要素を増やさない」に反する。
  加えて §規範性 は「実装リポジトリごとに異なる構成が『標準に揃った』と主張できる状態を解消する」ために
  書かれた条文であり、**実装側の都合で要素を増やす提案は、その趣旨を薄める**。計画は `fixed` なので
  裁定待ちの間、適用が止まる代償もある。
  - **再評価条件**: **HTTP 面をまったく持たないワーカーが 2 サービス以上**現れたとき。1 件では
    「その 1 つが特殊」で済むが、2 件は型である（`CLAUDE.md`「同型の事故が 2 回起きたら」に倣う閾値）。
    そのとき初めて `/plan-feedback` で γ を計画へ出す。

### 決定 3 — 適用は 1 PR で 44 ファイルを一度に置く。フォルダ名は `<Name>.<要素>`

1. **対象は 4 要素（`Application` / `Domain` / `Infrastructure` / `Contracts`）× 11 サービス = 44 ファイル。**
   決定 1 で `SharedKernel` を、決定 2 で `Api` を、実測で `Tests`（11 / 11 実体あり）を外した結果である。
2. **1 PR にまとめる。サービス単位に割らない。** 根拠は [[IADR-0116]] **規約 1**（1 issue = 1 PR）と
   `CLAUDE.md`「**人間がレビューできる変更単位を維持する**」である。44 ファイルはすべて空の `.gitkeep` で、
   **判断は本 IADR の決定 1〜3 に尽きて 1 つも増えない**。11 PR へ割ると、同じ判断を 11 回レビューし、
   FIFO のマージ待ち行列を 11 本占有するだけである。
   - **規約 4（「1 PR が大きくなる場合は issue を分割する」）は本件に当たらない。** 同規約が禁じるのは
     **1 issue に複数 PR をぶら下げる**ことであり、分割の単位は PR ではなく issue である。本件は
     分割していないので抵触しない。
   > ［2026-08-17 是正 / #455］初版はここで規約 4 を「**差分の行数ではなく判断の数で測る**」と要約して
   > いたが、**規約 4 にその言明は無い**（AI レビューの指摘）。趣旨の近い記述は `CLAUDE.md` と
   > [[IADR-0139]] 決定 1 の追記だが、後者は**複数 issue を 1 PR へ束ねる例外**の文脈で、規約 4 とは別の話である。
   > **根拠として引いた条文が言っていないことを、条文の要約として書いていた。**
3. **フォルダ名は `<Name>.Application` の形**とする（`Application` 単独にしない）。実体になったときの
   `.csproj` 名がそれであり（雛形 `templates/unit-template/backend/Services/SampleService/src/SampleService.Application/` が
   実在の先例）、**名前を変えずにファイルを足すだけでプロジェクト化できる**。
   置き場は `src/<unit>/backend/Services/<Name>/src/<Name>.<要素>/.gitkeep`。
4. **`.gitkeep` は空ファイルとする**（内容を持たせない）。計画は「`.gitkeep` だけを置く」と書いており、
   `docs/` 15 件・フロント雛形 11 件の既存もすべて空である。**説明は次項の注記が持つ。**
5. **適用 PR は次の 2 つの読み替えを 1 箇所へ書く**（空フォルダだけでは伝わらないため）。置き場は
   `src/README.md` の §サービスユニットの標準レイアウト。
   - **「空の枠は『プロジェクトとして切り出していない』の意味であって、『そのコードが無い』ではない。」**
     実測で `Foundation/Domain` は **8 / 11**、`Foundation/Persistence` は **7 / 11** のサービスが持つ
     ——層は [[IADR-0027]] の `Foundation/` 配下のフォルダとして既に存在する。
   - **`src/README.md:70`「存在しない区分のフォルダは作らない（空フォルダを置かない）」は
     `<Name>.{Api,Worker}/` の内部区分（`Foundation/` `Composable/` の中）についての条文であり、
     サービス直下の 7 要素には掛からない。** 階層が違うことを 1 文で書き分ける。
6. **雛形（`templates/unit-template/backend/`）は適用対象 0 件**である。7 要素中 6 つが実体としてあり、
   残る `SharedKernel` は決定 1 で対象外だからである。**既に適合しているので触らない。**
7. `src/ai-stock-trading` は別リポジトリであり、**追随は向こうの issue** で行う（計画の規則は及ぶ）。

### 決定 4 — 機械検査は置かない（置く条件を先に書く）

**置かない。**「同型の事故が 2 回起きたら検査器を足す」（`CLAUDE.md`）の条件を満たさないためである。

- **事故の型を定義する**: 「**適用済みのはずの標準構成の枠が、欠落する**」。
- **実測 0 回**である。バックエンドは**そもそも未適用**であり、「欠落」ではない。
  フロント雛形の件（2026-08-16）も未適用の是正であって、適用後の欠落ではない。
  **フロント側にも検査器は置かれていない**（実測: `scripts/` に `.gitkeep` を検査するものは無い）。
  同じ規則の別の面で片方だけ検査器を持つのは筋が通らない。
- **本 PR は適用しないので、いま検査器を置けば 44 件を即 fail させる**（fail-closed の意味が無い）。

**置く条件（先に書く）**: 適用 PR のマージ後に、次のいずれかが **2 回**起きたとき。
①既存の `.gitkeep` が削除される、②新規サービス追加時に枠が置かれない。
**そのときの実装の当たり**: `src/<unit>/backend/Services/*/src/` の直下に
`<Name>.{Api|Worker}` ＋ 4 要素の計 5 ディレクトリが揃うかを走査する（`Tests` は `tests/` 側）。
**要素名の一覧は本 IADR を単一情報源とし、検査器へ列挙し直さない。**

## 理由

- 決定 1・2 に共通する軸は「**`.gitkeep` は主張である**」という点にある。空フォルダは
  「この枠は認識しており、いま中身が無い」と主張する。**その主張が偽になる場所へは置かない** ——
  `SharedKernel` は「中身が無い」のではなく「別の場所にあると決めた」、`Api` は「中身がある」。
  規則の目的（意図的な不在と作り忘れの区別）は、**偽の主張を置いた瞬間に裏返る**。
- 決定 2 で α を採るのは、`Api` の定義（エンドポイント定義・DI 構成）が**ホストの駆動方式ではなく責務**で
  書かれているためである。Worker は責務を果たしている。
- 決定 3 で 1 PR にまとめるのは、**`CLAUDE.md`「人間がレビューできる変更単位を維持する」**に照らして
  44 個の空ファイルが 1 単位で読めるからである（**判断は本 IADR の決定 1〜3 に尽き、ファイルは 1 つも
  判断を増やさない**）。**[[IADR-0116]] 規約 4 は根拠に引かない** —— 同規約は「1 PR が大きくなる場合は
  **issue を分割する**」であって、認知負荷の測り方を定めた条文ではない（決定 3-2 の追記を参照）。

## 結果

- 良い影響:
  - 11 サービスすべてで「標準の枠を認識したうえで中身が無い」ことが一目で分かるようになる（適用後）。
  - `SharedKernel` を per-service に作ってはならないことが、**IADR-0117 を読まなくても**伝わる（決定 3-5 の注記）。
  - `.Worker` サービスの扱いが確定し、後続のサービス再実装 issue が同じ前提で着手できる。
- 悪い影響・トレードオフ:
  - **計画の字面（7 要素すべてに枠）と実装（6 要素）がずれる。** 決定 1 の環流が済むまで、
    この差は本 IADR の存在によってのみ説明される。**環流を未決事項として残す。**
  - 空フォルダが 44 個増え、`find` / IDE のツリーが縦に伸びる。ADR-0030 選定基準 2 が嫌う
    「構成要素を増やす」には当たらない（ビルド対象は 1 つも増えない）が、視覚的なコストはある。
  - 決定 2 の再評価条件（Worker から HTTP を撤去）が満たされたとき、2 サービス分の追随が要る。
- フォローアップ:
  1. **適用 PR の起票**（44 ファイル ＋ `src/README.md` の注記）。本 PR では起票のみ。
  2. **`/plan-feedback`**: 計画 §規範性 と [[IADR-0117]] の関係（集約された構成要素の枠をどう扱うか）の明記を提案する。
     IADR-0117 フォローアップ 2 の未達分と併せて 1 本にする。
  3. `src/ai-stock-trading` への追随 issue（向こうのリポジトリ）。

## 関連

- Supersedes: なし
- Superseded by: なし
