---
title: 作業仕様書 — 必須仕様書が指す存在しないコードパスを是正し、同型を機械検査する（#592）
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0027
  - IADR-0062
  - IADR-0130
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 作業仕様書: 必須仕様書のコードパス（#592）

## 起点

- **NFR**（仕様書と実装の一致）／[IADR-0062](../adr/IADR-0062_namespace-assembly-unit-rename.md)（ユニット改名）／
  [IADR-0027](../adr/IADR-0027_composability-folder-structure.md)（Composable / Foundation 分割）
- 起点 issue: **#592**（出所は定期監査 2 回目・adr-guardian・`cf15568`）

## 母集合（自分で引き直した）

**#592 自身が「上記 4 件は監査の抽出結果であり、転記せずに自分で取り直すこと」と書いている。**
取り直した結果、**候補は 11 件で、真の誤りは 4 件**だった —— **残り 7 件はすべて偽陽性**である。

### 軸 1: issue 番号で引く

```console
$ git ls-files -z ':!planning' ':!src/ai-stock-trading' | xargs -0 grep -ln '#592'
```

**0 件。** 引き継ぎは無い（**引かずに「無い」と決めない**）。

### 軸 2: 計画書の現状

**計画は仕様書のパス表記を定めていない。** `planning/docs/` にも
`projects/microservices-platform/` にもソースパスの記載規約は無く、本件は**実装側の文書規約に閉じる**。
したがって**計画への環流も不要**である。

### 軸 3: 誤りの側から引く（**拡張子で絞らず、パスの形で取る**）

`docs/` 配下の Markdown 全件から**パス様の文字列**を抽出し、`git ls-files` と突合した。

```console
$ # 抽出: (?<![\w./-])((?:src|deploy|scripts|tools|\.github|docs)/[A-Za-z0-9_./-]+)
$ # 判定: git ls-files に無く、かつファイルシステムにも無い
```

**延べ 4338 件のうち不在は 532 件。** ディレクトリ別の内訳:

| ディレクトリ | 不在 | 性質 |
| --- | --- | --- |
| `docs/specs/` | **333** | **履歴文書**（#592 が対象外と明記） |
| `docs/superpowers/plans/` | **143** | **旧計画文書**（同上の理由で対象外。**#592 は触れていない**） |
| `docs/adr/` | **40** | **決定当時の構造を説明する記録**（下記） |
| **必須仕様書（tests / functional / api / operations / tech）** | **11** | **本 PR の母集合** |
| その他（`ai-workflow.md` / `how-to`） | 5 | 手順ガイド |

### 必須仕様書 11 件の 1 件ずつの判定（**開いて確かめた**）

| # | 箇所 | パス | 判定 |
| --- | --- | --- | --- |
| 1 | `docs/tests/FR-01_data-source-catalog.md:100` | `…/Tests/KnowledgePlatform.IntegrationTests/DataSourceService/DataSourceTests.cs` | **真**（IADR-0062） |
| 2 | `docs/tests/FR-06_document-crud-versioning.md:70` | `…/DocumentService/DocumentCrudTests.cs` | **真**（IADR-0062） |
| 3 | `docs/tests/FR-07_data-range-analysis.md:77` | `…/AiAnalysisService/RagOrchestratorTests.cs` | **真**（IADR-0062） |
| 4 | `docs/functional/FR-13_wiki-browsing.md:59` | `…/WikiService.Api/Endpoints/WikiEndpoints.cs` | **真**（IADR-0027） |
| 5 | `docs/api/BFF_bff-surface.md:293` | `docs/screens/SC-` | **偽陽性 (c) 省略形** |
| 6 | `docs/api/BFF_bff-surface.md:297` | `scripts/generate-openapi.sh` | **偽陽性 (d) 不在を述べる文** |
| 7-9 | `docs/operations/local-sso-recovery-runbook.md:80/111/124` | `deploy/trade-decision-service` 他 | **偽陽性 (a) kubectl 資源参照** |
| 10 | `docs/tech/tech-requirements.md:67` | `src/index.ts` | **偽陽性 (e) 相対表記** |
| 11 | `docs/tests/NFR-01_performance-load-test.md:61` | `src/platform/frontend/dist` | **偽陽性 (b) ビルド生成物** |

**真の 4 件は実体が改名後の場所に在る**（`git ls-files` で確認済み）:

```
src/knowledge/backend/Tests/Knowledge.IntegrationTests/{DataSourceService/DataSourceTests,
  DocumentService/DocumentCrudTests, AiAnalysisService/RagOrchestratorTests}.cs
src/knowledge/backend/Services/WikiService/src/WikiService.Api/Foundation/Endpoints/WikiEndpoints.cs
```

## ★ 偽陽性が 6 クラスある —— 検査器の設計はここで決まる

| クラス | 実例 | なぜパスとして扱えないか |
| --- | --- | --- |
| **(a) kubectl 資源参照** | `kubectl -n … logs deploy/wiki-js` | `deploy/<name>` は **Deployment 資源**であってファイルではない |
| **(b) ビルド生成物** | `--require src/platform/frontend/dist` | ビルドするまで存在しないのが正しい |
| **(c) 省略形** | `docs/screens/SC-` | `SC-*.md` を地の文で切った形 |
| **(d) 不在を述べる文** | `（scripts/generate-openapi.sh は無い）` | **文の主旨が「存在しないこと」である**。`openapi.yml` が `if [ -f … ]` で守る**任意フック**で、4 箇所が不在前提で書いている |
| **(e) 相対表記** | `src/index.ts` | パッケージ内の相対パス（実体は `src/packages/ui/src/index.ts`） |
| **(f) 省略記号入り** | `src/Tests/.../Deployment/MeshMtlsTests.cs` | `...` は省略であってディレクトリ名ではない |

> **★ したがって #592 の案 a（`check-doc-links.js` へコードスパン内パスの汎用的な実在検査を足す）は
> 採らない。** 素朴に入れると**無関係な運用 Runbook（(a) ×3）と通信仕様書（(c)(d)）を落とす** ——
> [IADR-0159](../adr/IADR-0159_openapi-dto-drift-checker.md) が実測した
> 「**偽陽性は見逃しより重い**」（無関係な PR の CI を誤って落とす）に真正面から当たる。

## 判断

### 判断 1: **`.cs` パスだけを見る**（偽陽性 6 クラスが機械的に消える）

`.cs` で終わるパスだけへ絞ると、**上記 (a)〜(e) は 1 件も入らない**（実測）:

```console
$ # `.cs` の参照は延べ 175 件。不在は 126 件で、その内訳は:
docs/specs/ 36 ／ docs/superpowers/ 82 ／ docs/adr/ 4 ／ **必須仕様書 4**
```

**必須仕様書に残る `.cs` の不在は、真の誤り 4 件ちょうどである。**
`.cs` は「C# のソースファイル」以外を意味しようがなく、資源名・生成物・省略形・相対表記の
どれとも取り違えられない。**種類で絞るのではなく、曖昧さの無い形だけを見る。**

### 判断 2: 検査は **`check-test-spec-coverage.js` へ足す**（#592 の案 b）

同検査器は冒頭で**方向 (a)「仕様書が挙げるテスト名が実在するか」を検討して採らなかった**と書いている
——#510 の欠陥（節の消失）は方向 (b) でしか止まらないからである。**本件は別の欠陥**であり、
方向 (a) がちょうど当たる。**2 つの方向は競合せず補い合う**ので、同じ検査器に両方を置く。

新設しない理由: 必読規約の総量に 50KB の予算がある（`CLAUDE.md`）。**同じ資源
（`docs/tests/` と実体の対応）を見る検査器を 2 本に割らない。**

### 判断 3: **`docs/adr/` も対象外にする**（#592 は `docs/specs/` しか挙げていない）

`docs/adr/` の 4 件（`src/Bff/KnowledgePlatform.Bff/…` 等）は、**改定前の構造を説明する文脈**である。
追随させると「当時こう決めた」という記録として壊れる —— **`docs/specs/` を除外するのと同じ理屈**が
そのまま当たる。`docs/superpowers/plans/`（旧計画文書・82 件）も同様。

**#592 は `docs/specs/` だけを対象外と書いているが、それでは足りない。** 線引きを本仕様書と ADR で
明文化する: **「作業当時の事実を記録した文書」は追随させない。対象は「現在を記述する必須仕様書」に限る。**

### 判断 4: **別プロジェクトの submodule を巻き込まない**

`docs/adr/IADR-0072` は `src/ai-stock-trading/…/MonitorSettingsEndpoints.cs` を指す。
実体は submodule の中にあり、**`git ls-files` には出ない**（gitlink のため）。
CI で submodule を populate していない場面では**実在しても不在に見える**。
既存ヘルパ `scripts/lib/excluded-units.js`（`.gitmodules` を単一情報源に導出）で除外する
——**名指しでハードコードしない**。

### 判断 5: **`...` を含むパスは判定しない**

`src/Tests/.../Deployment/MeshMtlsTests.cs` の `...` は省略記号である。
**「省略された表記」と「壊れた表記」を機械が区別できない**ので、判定を試みない（黙って飛ばさず、
除外理由を本仕様書と ADR に書く）。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#592） | 確かめ方 |
| --- | --- | --- |
| 1 | 4 本の必須仕様書が実在するパスを指している | 是正後に検査器が違反 0 |
| 2 | **母集合を自分で取り直した結果**が記録されている | 本仕様書 §軸 3（**4 件 → 候補 11 件・真 4 件**。増減の内訳つき） |
| 3 | 検査を足すか足さないかの判断と根拠が実装 ADR に残る | IADR-0163 |
| 4 | 足す場合、**改名前のパスを 1 つ書いて落ちること**を実測する | 変異試験 M1 |
| 5 | `docs/specs/` を対象外とする線引きが明文化されている | 判断 3（**`docs/adr/` と `docs/superpowers/` へ拡張**） |

### 変異試験

| 変異 | 期待 |
| --- | --- |
| **M1: 是正した 1 行を改名前のパスへ戻す** | **落ちる**（#592 の受け入れ基準 4） |
| M2: `docs/specs/` に不在の `.cs` パスを書く | **落ちない**（履歴文書は対象外＝**除外が効いている側**） |
| M3: `docs/tests/` に `...` 入りの不在パスを書く | **落ちない**（判断 5） |
| M4: `docs/tests/` に `src/ai-stock-trading/…/X.cs` を書く | **落ちない**（判断 4） |
| M5: `docs/tests/` に `deploy/wiki-js` を書く | **落ちない**（`.cs` でない＝判断 1） |

**M2〜M5 が要である。** 本検査は**新しく落とす側**なので、
**落としてはいけないものを落とさないこと**を主張しないと、次の PR で偽陽性として現れる。

## 射程外

- **`.cs` 以外のパス**（`.ts` / `.yaml` / `.sh` / ディレクトリ）。判断 1 のとおり曖昧さが残る。
  **見逃す側に倒す**（偽陽性より軽い）。申し送る。
- **`docs/specs/` / `docs/adr/` / `docs/superpowers/` の不在パス 458 件**（判断 3）。
- **#572 施策 9 との束ね**（#592 が「束ね候補」に挙げている）。
  [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) 決定 1 の**判定単位は資源**であり、
  #572 は issue 消化率の施策で**資源が異なる**。束ねない。
