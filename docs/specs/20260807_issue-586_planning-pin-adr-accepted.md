---
title: 作業仕様書 — planning pin を裁定反映後（ADR-0033〜0037 が Accepted）へ進め、IADR-0119 の着手ゲートを追随させる
type: spec
status: done
related_ids: [NFR, IADR-0119, IADR-0116, IADR-0129, IADR-0010, IADR-0131, FR-08, FR-17, FR-18, FR-19, FR-20, FR-21, SC-03, SC-09, SC-10, SC-18, SC-19, SC-20, SC-21]
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md"
related_specs:
  - "../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md"
  - "../adr/IADR-0116_reimplementation-branching-and-pr-policy.md"
  - "../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md"
  - "../adr/IADR-0010_feedback-service-and-upsert.md"
  - "../screens/SC-09_admin-abac-settings.md"
  - "../screens/SC-10_operations-dashboard.md"
  - "../functional/FR-08_answer-feedback.md"
  - "../api/BFF_bff-surface.md"
  - "20260806_issue-560_planning-pin-follow.md"
author: Claude（実装）
created: 2026-08-07
updated: 2026-08-07
---

# 作業仕様書 — planning pin を `3e58b97` へ進め、IADR-0119 の着手ゲートを追随させる（#586）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-08（フィードバックの認可が確定）／ FR-17〜FR-21（着手ゲートの対象）
- ユースケース（UC）: 変更なし
- 画面（SC）: SC-01・SC-10（FR-08 の認可）／ SC-18〜SC-21（着手ゲートの対象）
- 関連 ADR: ADR-0033・0034・0035・0036・0037（`Proposed` → `Accepted`）
- 関連 IADR: [[IADR-0119]]（本作業で追補）／[[IADR-0116]] 規約 7・[[IADR-0129]] 決定 1・[[IADR-0010]]・[[IADR-0131]]（いずれも事実の更新）
- 計画書リンク: 上記 `plan_refs`

## 目的・背景

計画リポジトリで 2026-08-07 に裁定 2 件（planning#236 = FR-08 の認可・planning#237 = ADR-0033〜0037 の
`Accepted` 化）が反映され、`main` が `3e58b97` まで進んだ。本リポジトリの pin は `e36b592` のままで
この裁定が見えていない。pin を進め、**[[IADR-0119]] の着手ゲートが実際にどこまで外れたのかを実測して
記録する**。

## 対象範囲

### 対象

| 対象 | 内容 |
| --- | --- |
| `planning` submodule の pin | `e36b592` → `3e58b97`（**pin のみ**。planning の内容は 1 行も変更しない） |
| [`IADR-0119`](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) | **日付つき追補**（後述「追補・改定・Superseded の判断」）。解除された範囲と、解除されていない範囲を実測で記録する |
| [`IADR-0116`](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 7 | **日付つき追記（事実の更新）**。同規約の保留根拠（ADR-0035 の未起案 →`Accepted` 未達）が消えたことを記す。**決定内容は変えない** |
| [`IADR-0129`](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md)（Accepted） | **日付つき追記（事実の更新）**。決定 1 が「辺の型辞書を実装しない」根拠にした 2 事実（ADR-0033 の `Proposed`・FR-17 の保留）が消えたことを記す。**決定内容は変えない**。区画を実装するかは #504 / #452 が判断する |
| [`IADR-0010`](../adr/IADR-0010_feedback-service-and-upsert.md)（Accepted）／[`IADR-0131`](../adr/IADR-0131_openapi-as-bff-contract-source.md) | **日付つき追記**。FR-08 の認可確定により、前者の「統計は認可を課さない」が計画と食い違うこと・後者のフォローアップ 4「要裁定」が消化されたことを記す。**是正は #521** |
| [`docs/adr/README.md`](../adr/README.md)（**索引行**） | IADR-0119 / IADR-0116 / IADR-0129 の索引行を本文の追補・追記と同じ内容へ追随させる。**索引行は本文と独立に古くなるため、本文を触ったら必ず対にする** |
| [`docs/screens/SC-09`](../screens/SC-09_admin-abac-settings.md) / [`SC-10`](../screens/SC-10_operations-dashboard.md) | **日付つき追記**。「ADR-0033 は `Proposed`」という時点なしの現在形を時点つきへ直し、解除を反映する（先例: [SC-01](../screens/SC-01_search-chat.md)`:119`「2026-08-04 時点で」・[SC-03](../screens/SC-03_document-detail.md)`:97`「planning `d980a01` 時点で」） |
| [`docs/functional/FR-08`](../functional/FR-08_answer-feedback.md) / [`docs/api/BFF_bff-surface.md`](../api/BFF_bff-surface.md) | **日付つき追記**。FR-08 の認可確定と live 文書（「認可なし」「`anonymous` 受理」「端点認可が未裁定」）の食い違いに**送り先（#521）**を書く。**記述は書き換えない**（現在の実装の事実としては正しい） |
| コード内コメント 3 箇所（`sc03-document` / `sc09-admin-abac` / `sc10-operations` の各 Page） | **事実の更新のみ**。時点なしの現在形で `Proposed` を主張している箇所を時点つきに直し、解除を追記する。**UI は変更しない**（画面の実装は #450 / #452 / #504 の射程） |
| [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) | pin 参照（`e36b592` → `3e58b97`）と、2026-08-06 追記ブロック（「保留は解除されない」）の追随。あわせて `Proposed` な計画 ADR の列挙を**実測で置き換える**（従前は 3 件しか挙げておらず不完全だった） |
| [`scripts/measure-abac-combinations.js`](../../scripts/measure-abac-combinations.js) 冒頭コメント | **事実の更新のみ**（「ADR-0035（Proposed）」）。コードは変更しない |
| [`feedback/`](../../feedback/) | 計画側の記述の内部不整合 1 件を記録（後述「計画書との差異」） |

### 対象外（送り先を明記する）

| 対象外 | 理由 | 送り先 |
| --- | --- | --- |
| `planning/` の内容変更 | `CLAUDE.md` の規約。実装ブランチで許されるのは **pin 更新のみ** | — |
| FR-17 / FR-18 の実装着手（GraphService 等） | 本作業は**ゲートの記録**であり実装ではない。着手は各 issue の作業仕様書から始める | **#450 / #452（SC-18・SC-21）** |
| FR-08 の認可の実装（`/bff/feedback` の `RequireAuthorization`） | 同上。挙動の変更を伴うため独立した PR とテストが要る | **#521** |
| `src/ai-stock-trading` の pin | 本 issue の範囲外（#586 の禁止事項） | — |
| 過去の `docs/specs/` と `feedback/` に残る「ADR-00xx は `Proposed`」の記述 | **時点つきの作業記録**（ファイル名が日付を持ち、その日の判断根拠を保存する）であり、事後に書き換えると当時の判断根拠が読めなくなる（履歴不変の原則と同じ考え方） | — |
| FR-08 の認可の**実装と文書の書き換え** | 挙動の変更であり独立した PR とテストが要る。本作業は**食い違いの明示と送り先の記録**に留める | **#521** |
| SC-09 の辺の型辞書区画 / SC-10 のナレッジ健全性節 / SC-03 の AI 提案承認欄・SC-18 導線の**実装** | 保留は解けたが、本作業は**ゲートの記録**であり実装ではない | **#504 / #452** |

> **［是正 / #593 レビュー］対象の絞り方を誤った。** 当初は「`Proposed` が根拠になっている箇所」の母集合を
> **`docs/specs/` というディレクトリで絞った**ため、`.md` 以外（`.tsx` 3 件・`.js` 1 件）と
> 非 specs の `.md`（`docs/adr/README.md` の索引行 2 本・[[IADR-0129]]・画面仕様書 2 本）を取りこぼした。
> **母集合は「どこに置かれているか」ではなく「時点つきの記録か、それとも live な現在形の主張か」で切る。**
> 取り直しの手順は §実測 6 に残した。

## 着手時の実測

### 実測 1: 前提 ADR 5 件はすべて `Proposed` → `Accepted` へ移った

```console
$ for n in 0033 0034 0035 0036 0037; do
    for c in e36b592 3e58b97; do
      f=$(git -C planning ls-tree -r --name-only $c projects/microservices-platform/07_adr/ | grep "ADR-${n}_")
      printf "%s %s : " "$c" "$n"; git -C planning show "$c:$f" | grep -m1 '^status:'
    done
  done
e36b592 0033 : status: Proposed
3e58b97 0033 : status: Accepted
e36b592 0034 : status: Proposed
3e58b97 0034 : status: Accepted
e36b592 0035 : status: Proposed
3e58b97 0035 : status: Accepted
e36b592 0036 : status: Proposed
3e58b97 0036 : status: Accepted
e36b592 0037 : status: Proposed
3e58b97 0037 : status: Accepted
```

### 実測 2: ID レンジは変わっていない（`ADR-0001..0043`・欠番なし）

```console
$ ls planning/projects/microservices-platform/07_adr/ | grep -oE 'ADR-[0-9]{4}' | sort -u \
    | node -e 'const n=require("fs").readFileSync(0,"utf8").trim().split("\n").map(s=>+s.slice(4));
               const miss=[];for(let i=1;i<=Math.max(...n);i++)if(!n.includes(i))miss.push(i);
               console.log("count:",n.length,"max:",Math.max(...n),"missing:",JSON.stringify(miss))'
count: 43 max: 43 missing: []
```

`FR-01..21` / `UC-01..11` / `SC-01..21` も不変である（本 pin は既存 ADR の状態遷移と FR-08 の
記述追加であり、新規 ID を起こしていない）。

### 実測 3: **着手ゲートは全部は外れていない。外れたのは FR-17 / FR-18 だけである**

[[IADR-0119]] 決定 2 は着手条件を FR ごとに別々に定めている。**ADR が `Accepted` になることは
FR-19〜21 の条件の一部でしかない。**

| FR | [[IADR-0119]] 決定 2 の条件 | 実測（planning `3e58b97`） | 判定 |
| --- | --- | --- | --- |
| **FR-17 / FR-18** | `ADR-0033`・`0034`・`0035` の `Accepted` | 3 件とも `Accepted` | **解除** |
| **FR-19 / FR-20** | 上記に加えて `ADR-0036`・`0037` の `Accepted` **かつ Wiki.js の個人スコープ可視性の前提検証の完了** | ADR は 2 件とも `Accepted`。**前提検証は未了** | **保留継続** |
| **FR-21** | 計画側が当該要求を確定（`fixed` 扱い）させること | **起案段階（`draft` 相当）のまま** | **保留継続** |

FR-19 / FR-20 の前提検証が未了であることは、計画側が同じ文書で明言している
（[02_requirements](../../planning/projects/microservices-platform/02_requirements/01_requirements.md)
の FR-19・FR-20 注記）。

> 2. **編集手段（Wiki.js）の前提検証が未了である。** Wiki.js の権限はページ／グループ単位であり
>    **個人スコープの可視性制御を持たない**。（略）**検証結果によっては編集手段の裁定が覆り得る**
> 3. 同期方式は ADR-0037 で確定する。（略）ただし上記 2 の検証結果に依存する
>    （**この依存は解消していない**）

FR-21 が起案段階のままであることも同じ注記の冒頭（2026-08-01・起案）が示しており、本 pin で
変更されていない。

#### 実測 3-b: **画面（SC）単位での着手可否** —— SC-18〜21 を 1 件ずつ

FR 単位の判定（上表）を、[[IADR-0119]] 決定 1（「保留の対象は当該 FR を実現するプロダクトコードと、
**その受け入れを担う画面**・API・データモデル」）に従って**画面へ写した**。
関連要求は計画 [05_screens](../../planning/projects/microservices-platform/05_screens/01_screens.md) の
画面一覧（`:70`〜`:73`）を実測して採った。

| SC | 画面 | 画面一覧の関連要求（実測） | 判定 | 引き受け先 |
| --- | --- | --- | --- | --- |
| **SC-18** | ナレッジグラフビュー | `FR-17, FR-05` | **着手可**（FR-17 が解除） | **#450 / #452**。ただし描画ライブラリは `ADR-0039`（`Proposed`）に依拠するため、扱いは各 issue の作業仕様書で判断する（実測 5） |
| **SC-19** | 個人資料管理 | `FR-19, FR-21, FR-05` | **保留継続**（FR-19 = Wiki.js の前提検証が未了／FR-21 = 起案段階）。**関連要求 3 件のうち 2 件が保留対象**であり、片方だけでは着手できない | **#451**（前提検証の担い手が決まるまで着手しない） |
| **SC-20** | Obsidian 連携設定 | `FR-20, FR-19` | **保留継続**（FR-20・FR-19 とも Wiki.js の前提検証に依存。`ADR-0037` は `Accepted` だが計画側が「**この依存は解消していない**」と明記） | **#451** |
| **SC-21** | AI 提案一覧 | `FR-18, FR-17` | **着手可**（FR-18 / FR-17 とも解除） | **#452** |

**SC-01 / SC-03（既存画面の保留部分）も同じ規則で判定した。**

| 既存画面の保留部分 | 属する FR | 判定 | 引き受け先 |
| --- | --- | --- | --- |
| **SC-03 の AI 提案承認欄・SC-18 への導線** | FR-18 / FR-17 | **着手可**（コード内コメントが「保留が解けた時点で SC-18 / SC-21 の実装と同じ段で本画面へ足す」と予告しており、**その発火条件が本 pin で成立した**） | **#452** |
| **SC-09 の辺の型辞書区画** | FR-17 | **着手可**（[[IADR-0129]] §結果 フォローアップ 2 の発火） | **#504 / #452** |
| **SC-10 のナレッジ健全性節** | FR-17 / FR-18 | **着手可**（同上） | **#504 / #452** |
| **SC-01 の個人資料まわり**（`👤` トグル・出典行のラベル） | FR-19 / FR-21 | **保留継続** | **#451** |

**issue 単位の対応**（受け入れ基準の 1 件ずつの実測に対応する）:
**#450 = 着手可**（FR-17 / FR-18）／**#451 = 保留継続**（FR-19 / FR-20）／
**#452 = 一部着手可**（SC-18 / SC-21 / SC-03 の残りは可、SC-19 / SC-20 は保留）／
**#521 = 着手可**（FR-08 の認可が確定。実測 4）。

### 実測 4: FR-08 の認可が確定した（#521 の裁定待ちが解消）

`02_requirements/01_requirements.md` の FR-08 行と受け入れ基準に、次が追加された。

- 投稿には認証を要する（匿名投稿は許さない）／統計は**運用者・管理者に限って**参照できる
- 同一回答（`AnswerId`）への投稿は 1 利用者につき 1 件とし、再投稿は上書きする
- 受け入れ基準: 投稿端点が**無認証で 401**／統計端点は認証済みでも権限外は **403**／
  同一利用者が 2 回投稿しても**集計は 1 件のまま**

`05_screens/01_screens.md` の SC-01（投稿）・SC-10（統計）にも同じ線で反映されている。

### 実測 5: `ADR-0039` は `Proposed` のままである

```console
$ grep -m1 '^status:' planning/.../07_adr/ADR-0039_sc18-graph-rendering-library.md
status: Proposed
```

`ADR-0039`（SC-18 のグラフ描画ライブラリ）は **[[IADR-0119]] 決定 2 の着手条件に含まれていない**
ため、これ自体は FR-17 / FR-18 の保留を継続させない。ただし **#450 / #452 のスコープは SC-18 の
描画ライブラリ導入を ADR-0039 に依拠させている**ため、着手時に扱いの判断が要る（本作業では
決めない。各 issue の作業仕様書で扱う）。

### 実測 6: 「解除で偽になる記述」の母集合を取り直した（PR #593 レビューの是正）

**当初は `docs/specs/` で絞ってしまい取りこぼした。** 誤りの側から引き直す。

```console
$ grep -rn "ADR-003[3-7]" --exclude-dir=planning --exclude-dir=node_modules --exclude-dir=.git \
    --exclude-dir=ai-stock-trading --exclude-dir=dist --exclude-dir=bin --exclude-dir=obj . \
  | grep -i proposed
（ヒットした live 文書: docs/adr/README.md の索引行 2 本〔IADR-0116 / IADR-0129〕・
  docs/adr/IADR-0129 の 3 箇所・docs/screens/SC-09 の 2 箇所・docs/screens/SC-10 の 2 箇所・
  src/knowledge/frontend/.../{AdminAbacSettingsPage,OperationsDashboardPage,DocumentDetailPage}.tsx・
  scripts/measure-abac-combinations.js。
 時点つきで既に正しかったもの: docs/screens/SC-01:119〔「2026-08-04 時点で」〕・
  SC-03:97〔「planning d980a01 時点で」〕。
 対象外としたもの: docs/specs/ と feedback/ の時点つき作業記録）
```

**もう 1 本の軸（FR-08 の認可）も同じ理由で取りこぼしていた**ため、語を変えて引き直した。

```console
$ grep -rn "認可なし\|anonymous\|端点認可\|匿名" <同じ除外> . | grep -i "feedback\|FR-08"
（ヒットした live 文書: docs/functional/FR-08_answer-feedback.md:49,82 ／
  docs/api/BFF_bff-surface.md:35,109-110,265 ／ docs/adr/IADR-0010:57 ／ docs/adr/IADR-0131:191 ／
  src/knowledge/backend/.../FeedbackEndpoints.cs:37-38,80）
```

**教訓（再発防止）**: `Proposed` / 保留 / 未裁定 を根拠にした記述は、**ディレクトリではなく
「時点つきの記録か、live な現在形の主張か」で切り分ける**。live な主張には**必ず時点を書く**
（先例の書き方が同じディレクトリにあった——SC-01`:119` / SC-03`:97`）。
**本文を追記したら索引行（`docs/adr/README.md`）を対にする**——索引行は本文と独立に古くなる。

## 追補・改定・Superseded の判断（[[IADR-0119]] をどう追随させるか）

**採るのは「日付つき追補」である。** 根拠は [[IADR-0119]] 決定 6 そのものである。

> 6. **保留の解除は前提 ADR の確定を確認した時点で行う。** 解除時は本 IADR の §フォローアップを更新し、
>    着手する issue の作業仕様書に確定を確認した ADR とその状態を記録する。**前提が確定しないまま着手が
>    必要になった場合は、本 IADR を改める新 IADR を起票する**（本 IADR の本文は書き換えない）。

| 選択肢 | 判定 | 理由 |
| --- | --- | --- |
| **A. 日付つき追補（採用）** | ○ | 決定 6 が**解除の手順として §フォローアップの更新を自ら指定**している。条件つきの決定の条件が満たされたことを記録する行為であり、**新しい決定を含まない** |
| B. 新 IADR による改定 | × | 決定 6 が新 IADR を要求するのは「**前提が確定しないまま**着手が必要になった場合」であり、本件は正反対（前提が確定した） |
| C. `Superseded` にする | × | 決定 1〜6 のどれも覆っていない。しかも**保留は FR-19〜21 について継続している**ため、本 IADR は今も効力を持つ。`Superseded` にすると FR-19〜21 の保留根拠が宙に浮く |
| D. 何もしない（pin だけ進める） | × | #586 の受け入れ基準「IADR-0119 の記述が現状と一致する」を満たさない。加えて 2026-08-06 追記が「保留は継続する」と述べたままになり、**FR-17 / FR-18 の着手判断を誤らせる** |

状態は `Accepted` のまま据え置く。[[IADR-0116]] 規約 7 についても同様に**事実の更新のみ**を
日付つき追記で行う（同規約には 2026-08-04 に同型の追記を行った先例がある）。

## 受け入れ基準

- [x] `git submodule status planning` が `3e58b97` を指す
- [x] `git diff --name-only origin/develop...HEAD` に `planning` が **1 エントリだけ**現れ、
      `planning/...` のパスが 1 件も現れない（＝ pin だけが動いている）
- [x] planning を populate した状態で `node scripts/check-doc-links.js` が緑
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
- [x] [[IADR-0119]] の記述が現状（FR-17 / FR-18 は解除・FR-19〜21 は保留継続）と一致する
- [x] #450 / #451 / #452（SC-18〜21）/ #521 の着手可否を **1 件ずつ**実測して記録した
      （**§実測 3-b** に SC-18 / SC-19 / SC-20 / SC-21 の 4 行と issue 単位の対応を置いた）
- [x] **解除で偽になる live な記述が残っていない**——母集合をディレクトリではなく
      「時点つきの記録か live な現在形の主張か」で取り直し、`Proposed` 軸と FR-08 の認可軸の
      両方で引き直した（**§実測 6**）

## テスト方針

本作業はプロダクトコードを変更しないため、新規のユニットテストは書かない。検証は
リポジトリの機械検査（`check-doc-links` / `scripts.test.js` / `check-commit-messages`）で行う。
FR-08 の認可の**テスト**（無認証 401・権限外 403・重複投稿が 1 件）は **#521 の範囲**である。

## 計画書との差異

- 差異: **あり**（1 件。環流する）

計画 `02_requirements/01_requirements.md` の 2026-08-07 追記は

> **FR-17〜21 の着手を止めていた条件は解消している。**

と**FR-17〜21 を一括りにして**述べているが、**同じ文書の FR-19・FR-20 注記 2・3 は前提検証の未了と
その依存が「解消していない」ことを明記しており、FR-21 は起案段階のままである**。追記は文脈上
「前提 ADR の状態」だけを指していると読めるが、**この 1 文だけを読んだ実装側は FR-19〜21 まで
着手可能と誤読する**。実際、本 issue（#586）の解除表は #451 を「解除」と分類している。
`feedback/20260807_fr17-21-gate-scope-ambiguity.md` に記録した。

## 未決事項・親への申し送り

1. **#451（FR-19 / FR-20）は着手できない。** Wiki.js の個人スコープ可視性の前提検証が未了であり、
   [[IADR-0119]] 決定 2 の条件を満たさない。**この検証は Wiki.js の実環境が要る**ため、
   計画側・実装側のどちらが担うのかを含めて別途決める必要がある。
2. **FR-21 は計画側が `fixed` 扱いにするまで着手できない。** 本 pin で変わっていない。
3. **`ADR-0039`（SC-18 の描画ライブラリ）は `Proposed`。** #450 / #452 の SC-18 部分の着手時に、
   ライブラリ選定を IADR で先行して決めてよいのか（＝ ADR-0039 の `Accepted` を待つのか）を
   各 issue の作業仕様書で判断する。
4. **`ADR-0035` の稼働後の再実測**（#456）は、計画側が本 ADR の状態遷移の条件から**切り離した**。
   #456 の位置づけは「起案ブロッカーの解除」から「稼働後の検証項目」へ変わっている（本文の改訂が要る）。
5. **`feedback/` の status 追随**（#563）は本作業に混ぜない。本 pin で計画側の状態が動いた記録が
   あるため、ずれが増えている可能性がある。
6. **［#452 へ］`DocumentDetailPage.tsx` が書いていた予告の発火条件が本 pin で成立した。**
   同ファイルのコメントは「**保留が解けた時点で SC-18 / SC-21 の実装と同じ段で本画面へ足す**」と
   自ら予告しており、対象は **SC-03 の AI 提案承認欄（FR-18）と SC-18 への導線（FR-17）**である。
   本 PR はコメントの事実を更新しただけで **UI は変更していない**。**足す作業は #452 が持つ**
   （[[IADR-0119]] の 2026-08-07 追補 §解除で発火した予告 に一覧がある。同表には
   [[IADR-0129]] §結果 フォローアップ 2 が予告していた **SC-09 の辺の型辞書区画**と
   **SC-10 のナレッジ健全性節**〔引き受け先 **#504 / #452**〕も載せた）。
7. **［#521 へ］FR-08 の認可について、live 文書 5 箇所が計画と食い違ったまま残っている。**
   本 PR は**送り先つきの日付追記**を入れただけで、記述と実装は変えていない。是正時に対にすること——
   [機能仕様書 FR-08](../functional/FR-08_answer-feedback.md)（API 表の「認可なし」・例外フローの
   `anonymous` 受理）／[通信仕様書](../api/BFF_bff-surface.md)（冒頭の `status` 理由・
   エンドポイント一覧の 2 行・§未決事項 3）／[[IADR-0010]] 決定「一覧の認可」／
   [[IADR-0131]] §結果 フォローアップ 4。
   **`src/knowledge/backend/.../FeedbackService.Api/Foundation/Endpoints/FeedbackEndpoints.cs:37-38,80`
   のコード内コメントも同型で残っている**が、本作業環境に .NET SDK が無くビルド検証ができないため
   **触っていない**（同ファイルは #521 が実装ごと書き換える）。
