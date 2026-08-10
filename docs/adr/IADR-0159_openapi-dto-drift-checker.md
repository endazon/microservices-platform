---
title: IADR-0159 AccessScopeResponse へ granted を載せ、契約と C# DTO の乖離を機械検査する
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - NFR-09
  - UC-01
  - UC-05
  - ADR-0004
  - IADR-0004
  - IADR-0122
  - IADR-0132
  - IADR-0116
  - IADR-0139
  - IADR-0140
  - IADR-0156
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# IADR-0159: `AccessScopeResponse.granted` と、契約 ↔ C# DTO の乖離検査（#525）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **FR-05**（ABAC）／**UC-01**・**UC-05**／**ADR-0004**。実装 issue: **#525**（出所は #520 の監査）
- 作業仕様書: [20260810_issue-525](../specs/20260810_issue-525_access-scope-granted.md)

## コンテキストと課題

`docs/api/openapi.yaml` の `AccessScopeResponse` は `userId` と `allowedFilters` しか持たず、
C# 契約（`AccessScopeDto.cs`）の **`bool Granted`** に対応するフィールドが無かった。

**これは「フィールドが 1 つ足りない」以上の意味を持つ。** 計画
`06_technical/07_abac-attribute-model.md`（`fixed`）§ポリシー評価モデル の具体判定規則は
**2 つの状態を明確に分けており、しかも両方とも `allowedFilters` が空である**:

| 計画の規則 | 状態 |
| --- | --- |
| 利用者にマッチするポリシーが 1 件も無い場合は**全件遮断** | `granted=false` ＋ `[]` |
| マッチしたポリシーに文書条件が無い場合は**全件許可** | `granted=true` ＋ `[]` |

**`granted` を落とすと、契約の上でこの 2 つが同一の応答になる。**
契約だけを読んで実装する者は「フィルタが無い＝制限なし」と解釈しうる ——
**deny-by-default が全件開放へ反転する**。

## 決定 1: `granted` を `properties` と **`required` の両方**へ入れる

[[IADR-0132]] 論点 B の **B1**。C# の `bool Granted = false` の既定値は**呼び出し側の引数の既定**であって
シリアライズの省略とは無関係で、`System.Text.Json` は既定でプロパティを省略しない ——
**JSON には必ず出る**。

**`required` に入れないと目的を半分しか達しない。** orval は `required` の無いプロパティを `?` で
生成するため `granted?: boolean` となり、読み手は `undefined` を `false` と同一視してよいか判断できない
—— **#525 が消そうとしている曖昧さがそのまま残る。**

## 決定 2: 説明文は**書き換える**（申し送りを消して、意味論を書く）

旧説明文は「**フィールドの追加は本 issue の範囲外**」という #520 時点の申し送りであり、
**回収した以上は残すと嘘になる**。代わりに計画の 2 規則を `granted` × `allowedFilters` の表として
書き下ろした —— 契約を読むだけで区別が付くことが本件の目的だからである。

## 決定 3: **契約 ↔ C# DTO の乖離を機械検査する**（`check-openapi-dto-drift.js`）

`CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**」。**4 回起きている**
——リポジトリ自身の記録から数えた:

| 回 | 事故 | 記録の場所 |
| --- | --- | --- |
| 1 | パスが 3 か所誤っていた（`/search` → 実体は `/bff/search` 等） | `openapi.yaml` の `Issue #118 監査` 注記 ×3 |
| 2 | `AiAnswerDto.citations` の型が誤っていた（`SearchResultDto[]` → 実体は `CitationDto[]`） | 同 `Issue #506 監査` |
| 3 | 応答スキーマに `required` が無く、生成型が全プロパティ省略可だった | #520 |
| 4 | **`AccessScopeResponse.granted` の欠落** | 本件 |

**いずれも人手の監査で偶然見つかった。** #520 §未決事項 4 は「C# → OpenAPI の追随は人手のまま」を
**構造的な穴**として申し送っていた。ここで塞ぐ。

### 見るもの・見ないもの

**見るのはプロパティ名の集合と `required` だけである。型は見ない。**
C# の `List<CitationDto>` と OpenAPI の `array`/`$ref` の対応は 1 対 1 でなく、判定器を書くと
誤検出の保守コストが上回る。**したがって上表 2 回目（#506 の型誤り）は本検査器では捕まらない**
——正直に書いておく。名前の欠落・余剰と非 null 性は表現に依らず判定できる。

**パス（1 回目の型）も見ない。** それは [[IADR-0156]]（#647 の `check-bff-authz-docs`）が
端点の側から見ている領域である。

### 意図的な差は理由つきで通す

`scripts/openapi-dto-drift-allowlist.json`。**理由の無いエントリは自己試験が落とす**
——「黙って除外した」を残さないためである（`.claude/rules/traceability.md` 母集合の規則 6）。
現在の 2 件はいずれも `scope` で、**ABAC スコープを契約へ出さない**という決定である
（出すとクライアントが送ってよいと読め、**権限昇格の入口を契約自身が示唆する**）。

### C# パーサの限界を実測した（**偽陽性の側を優先して塞いだ**）

**検査器の怖いところは見逃しではなく、無関係な PR を誤って落とすことである。**
PR を出したあと、8 通りの書き方を投げて実測した:

| 書き方 | 当初 | 対応 |
| --- | --- | --- |
| **折り返された `required`**（prettier が長い配列を複数行にする） | **「`required` に無い」と誤報** | **塞いだ。** `DataSourceDto` の 10 件がこれで、**実データは最初から一致していた**（上記 決定 4） |
| **record 本体の `public static`** | **拾ってしまう** | **塞いだ。** `System.Text.Json` は static を直列化しないので、契約へ出せと要求するのは誤りである ——**record 本体に定数を 1 つ置いた瞬間に、無関係な PR の CI が落ちる** |
| `record struct` / `readonly record struct` | 拾えず | **拾うようにした**（見逃しの側） |
| ジェネリック record（`record Foo<T>(...)`） | 拾えず | **意図的にそのまま**。OpenAPI のスキーマ名に対応するものが無く、拾っても照合相手が存在しない |
| 式本体プロパティ（`public string B => ...`） | 拾えず | **そのまま**（見逃し）。実データに 0 件で、直列化される計算プロパティを契約 DTO へ置く設計自体を採っていない |
| `sealed record` / 属性つき / `required` 修飾子 / 改行を含む位置引数 | 拾える | — |

C# 側の 4 件は**実データに 0 件**である（`Contracts/Dtos` を走査して確認した）——
すなわち将来の誤検出と見逃しである。**折り返された `required` だけは現時点の実害があり、
10 件を偽の債務として記録しかけていた**（決定 4）。

**3 件とも、指摘される前に自分で投げて測って見つけた。**

### CI への載せ方

`.github/workflows/` は GitHub App 権限で編集できないため、`scripts/scripts.repo.test.js`
（`ci.yml` の `scripts-tests` ジョブが `REQUIRE_REPO_TESTS=1` で読む）から子プロセスで起動する。
[[IADR-0140]] 決定 2 の相乗り。

## 決定 4: `required` の不一致は**ラチェット**にする（既存 10 件は据え置く）

`required` の検査を全数へ当てたところ、**着手時点で 10 件が一致していなかった**（実測）。
**#525 は `AccessScopeResponse` の資源に閉じており**（[[IADR-0139]] 決定 1）、10 件の是正は別の作業である
（**#658** が引き受ける）。リポジトリ既定のラチェット方式
（`backend-library-baseline` / `landed-subject-baseline` と同じ）で**既存を据え置き、新規混入だけを落とす**。

### ★ 当初この数は 20 だった。**うち 10 件は自分の検査器の偽陽性である**

`DataSourceDto` の 10 プロパティを「`required` に無い」と報告していたが、**実データは最初から一致していた**。
原因は `collectSchemas` が **1 行形式の `required: [a, b]` しか読めていなかった**ことで、
prettier が折り返した複数行形式を「`required` が無い」と誤って報告していた。

**「是正待ちの債務」として baseline へ据え置きかけた。** 気づいたのは
「応答側は安全なのだから直せたのでは、と指摘されうる」と考えて**費用を測りに実データを開いた**ときである。
——`DataSourceDto` の `required` を見にいったら、**10 件とも既に載っていた**。

**教訓: baseline へ入れる前に 1 件ずつ実データを開く。** 検査器が出した一覧を、
検査器の正しさを確かめずに債務として記録すると、**存在しない債務が恒久的に残る**。
折り返し形とブロック形の両方に自己試験を置き、`DataSourceDto` の 10 件は実データの回帰として固定した。

### 据え置きの内訳（**2 種類に分かれ、まとめて直してはならない**）

| 種別 | 件数 | 是正の安全性 |
| --- | --- | --- |
| 応答スキーマ（`ConversionJobDto`） | 3 | **安全**。サーバは必ず送っており、生成型が `?` から必須へ変わるだけ。**費用も実測した** —— 試しに直すと生成物 5 行の差分・`typecheck` 緑である |
| 要求スキーマ（`SearchRequest` 等） | 5 | **危険**。`required` を足すと呼び出し側が送信を強制される（#520 §未決事項 6）。既定値を持つなら「送らなくてよい」が正しく、`entries` へ理由つきで移すのが正解でありうる |
| 要求スキーマの「嘘の必須」（`UpdateDataSourceRequest`） | 2 | **安全**（緩和の向き） |

**応答側 3 件は安いと分かっていて、なお本 PR で直していない。** `ConversionJobDto` は SC-07 の資源であり、
`1 issue = 1 PR`（[[IADR-0116]] 規約 1）と [[IADR-0139]] 決定 1 の「判定の単位は資源」に反するからである。
**測った費用は #658 へ渡した**ので、次に着手する者は測り直さなくてよい。

**「10 件あるから検査を諦める」でも「10 件を今ここで直す」でもない。** 測って、線を引いて、送り先を書いた。

## 決定 5: 挙動は**変えない**。テストは「区別が本文に載る」ことを固定する

`granted` の消費側を全数確認したところ、**実装は全数が見ている**
（`BffScopeResolver` 2 か所・`AbacPageFilter` 2 か所・`RagOrchestrator` 3 経路・`SearchEndpoints`）。
本件は**契約側だけの欠落**である。

`AbacEvaluatorTests`（T-01 / T-04）は C# オブジェクトの `Granted` を見ており、
**シリアライズを通っていない**。#525 が言っているのは「**契約から**区別できない」ことなので、
新しいテストは**本文（JSON）を直接読む**。

### ★ 端点越しに値を固定しなかった理由（実測して避けた）

`TestWebApplicationFactory` は InMemory DB を**固定名 `AuthzTest`** で張っており、プロセス内の
全テストで共有される。**既存テストは利用者条件が空のポリシーを複数作っており**
（`AbacEvaluator.MatchesUserConditions` は条件が空なら**全利用者にマッチ**する）、
`granted=false` を端点越しに固定すると**テストの実行順に依存して壊れる**。

よって「値の対応」は決定的なシリアライズで、「本文に載っていること」は端点で固定した。
**書いてから落ちるのを待つのではなく、書く前に共有 DB の中身を引いた。**

## 結果

### 変異試験（いずれも復旧後に緑を確認）

| 変異 | 落ちるもの |
| --- | --- |
| `granted` を `properties` から消す | `check-openapi-dto-drift`（`missing-in-openapi`） |
| `granted` を `required` から外す | 同（`missing-in-required`） |
| `AccessScopeResponse` の 2 状態を同じ JSON にする | **T-18**（`allowAll` と `denyAll` の本文が同一になる） |
| 検査器を消す・壊す | `scripts.repo.test.js`（`scripts-tests` ジョブ） |

### ★ 検査器を入れるまで、この PR の OpenAPI 変更は**無防備だった**（正直に書く）

最初に書いたのは C# のシリアライズ試験だけで、**`openapi.yaml` から `granted` を消しても 1 件も落ちなかった**。
生成物の再生成差分検査も、**openapi を変えて再生成すれば両者は一致する**ので止まらない。

**「テストを書いた」と「変更が守られている」は別である。** 変異試験を回して初めてこれに気づき、
検査器の追加へ踏み切った。**変異試験をやらなければ、守られていないものを守られていると書いていた。**

## 申し送り

- **`required` 不一致 20 件の是正**（決定 4）。応答側 13 件と要求側 7 件で扱いが違う。**別 issue。**
- **型の不一致は検査していない**（決定 3）。#506 の型誤りの再発は捕まらない。
- **`/bff/*` の 5 端点が無認証で到達できる**（#656 として起票）。`granted` の消費側を全数確認する過程で
  見つけたもので、**別の資源**なので束ねていない。
- **#520 の作業仕様書は書き換えていない。** 確定した過去 PR の記録であり、後から注記を足すのは
  記録の改竄にあたる（`.claude/rules/traceability.md`）。回収の事実は本 ADR と [[IADR-0132]] に残す。
