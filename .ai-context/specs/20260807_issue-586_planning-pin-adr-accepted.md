---
title: 作業仕様書 — planning pin を裁定反映後（ADR-0033〜0037 が Accepted）へ進め、IADR-0119 の着手ゲートを追随させる
type: spec
status: done
related_ids: [NFR, IADR-0119, IADR-0116, IADR-0129, IADR-0010, IADR-0131, FR-08, FR-17, FR-18, FR-19, FR-20, FR-21, SC-03, SC-09, SC-10, SC-18, SC-19, SC-20, SC-21]
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
related_specs:
  - "../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md"
  - "../adr/IADR-0116_reimplementation-branching-and-pr-policy.md"
  - "../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md"
  - "../adr/IADR-0010_feedback-service-and-upsert.md"
  - "../../docs/screens/SC-09_admin-abac-settings.md"
  - "../../docs/screens/SC-10_operations-dashboard.md"
  - "../../docs/functional/FR-08_answer-feedback.md"
  - "../../docs/api/BFF_bff-surface.md"
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
- 関連 IADR: [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md)（本作業で追補）／[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 7・[IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md) 決定 1・[IADR-0010](../adr/IADR-0010_feedback-service-and-upsert.md)・[IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md)（いずれも事実の更新）
- 計画書リンク: 上記 `plan_refs`

## 目的・背景

計画リポジトリで 2026-08-07 に裁定 2 件（planning#236 = FR-08 の認可・planning#237 = ADR-0033〜0037 の
`Accepted` 化）が反映され、`main` が `3e58b97` まで進んだ。本リポジトリの pin は `e36b592` のままで
この裁定が見えていない。pin を進め、**[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) の着手ゲートが実際にどこまで外れたのかを実測して
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
| [`docs/screens/SC-09`](../../docs/screens/SC-09_admin-abac-settings.md) / [`SC-10`](../../docs/screens/SC-10_operations-dashboard.md) | **日付つき追記**。「ADR-0033 は `Proposed`」という時点なしの現在形を時点つきへ直し、解除を反映する（先例: [SC-01](../../docs/screens/SC-01_search-chat.md)`:119`「2026-08-04 時点で」・[SC-03](../../docs/screens/SC-03_document-detail.md)`:97`「planning `d980a01` 時点で」） |
| [`docs/functional/FR-08`](../../docs/functional/FR-08_answer-feedback.md) / [`docs/api/BFF_bff-surface.md`](../../docs/api/BFF_bff-surface.md) | **日付つき追記**。FR-08 の認可確定と live 文書（「認可なし」「`anonymous` 受理」「端点認可が未裁定」）の食い違いに**送り先（#521）**を書く。**記述は書き換えない**（現在の実装の事実としては正しい） |
| コード内コメント 3 箇所（`sc03-document` / `sc09-admin-abac` / `sc10-operations` の各 Page） | **事実の更新のみ**。時点なしの現在形で `Proposed` を主張している箇所を時点つきに直し、解除を追記する。**UI は変更しない**（画面の実装は #450 / #452 / #504 の射程） |
| [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) | pin 参照（`e36b592` → `3e58b97`）と、2026-08-06 追記ブロック（「保留は解除されない」）の追随。あわせて `Proposed` な計画 ADR の列挙を**実測で置き換える**（従前は 3 件しか挙げておらず不完全だった） |
| [`scripts/measure-abac-combinations.js`](../../scripts/measure-abac-combinations.js) 冒頭コメント | **事実の更新のみ**（「ADR-0035（Proposed）」）。コードは変更しない |
| `feedback/`（環流記録。計画リポ `projects/microservices-platform/10_feedback/` へ移設） | 計画側の記述の内部不整合 1 件を記録（後述「計画書との差異」） |
| [`docs/api/openapi.yaml`](../../docs/api/openapi.yaml)（**#593 レビュー R2 で追加**） | **日付つき追記のみ**。`/feedback` の `anonymous`・`/feedback/stats` の「AdminOnly は課さない」に送り先（#521）を書く。`/bff/*` 側は **YAML コメント**で書く（`description` へ書くと orval 生成物が動く）。**`responses` の `401` / `403` は足さない**（§実測 7） |
| `FeedbackService.Api/.../FeedbackEndpoints.cs`（**#593 レビュー Y3 で追加**） | **コメントのみ**（`:37` `:80` `:106`）。コメントのみの変更は C# コンパイラの対象外でビルド検証を要さず、同コミットの `.tsx` コメント変更とリスクが同一である。**挙動は変更しない** |
| [テスト仕様書 FR-08](../../docs/tests/FR-08_answer-feedback.md)（**#593 レビュー Y4 で追加**） | 計画が追加した受け入れ基準（無認証 401 / 権限外 403）へ **T-15 / T-16 を採番**する（`CLAUDE.md`「受け入れ基準をテストケースへ写像する」）。**実装は #521** |
| [テスト仕様書 SC-10](../../docs/tests/SC-10_operations-dashboard.md) / [`SC-01`](../../docs/screens/SC-01_search-chat.md) / [`SC-03`](../../docs/screens/SC-03_document-detail.md)（**#593 レビュー R1・Y1・Y5 で追加**） | **日付つき追記**。§未決事項に残った「保留解除待ち」、SC-01 の「条件は未充足である」（保留の根拠が別条件へ移った）、SC-03 の**コードと実質同じ予告**（後半のみ一致。🟢3）を対にする |
| [`scripts/check-cross-repo-refs.js`](../../scripts/check-cross-repo-refs.js) / [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)（**#593 レビュー Y6 で追加**） | `〔〕` 注記を**規約として明文化**し、`SEP` へ `〔` を足して機械検査に載せる。正例・負例を対で `--self-test` へ固定する（§実測 7） |

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
> 非 specs の `.md`（`docs/adr/README.md` の索引行 2 本・[IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md)・画面仕様書 2 本）を取りこぼした。
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

[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 2 は着手条件を FR ごとに別々に定めている。**ADR が `Accepted` になることは
FR-19〜21 の条件の一部でしかない。**

| FR | [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 2 の条件 | 実測（planning `3e58b97`） | 判定 |
| --- | --- | --- | --- |
| **FR-17 / FR-18** | `ADR-0033`・`0034`・`0035` の `Accepted` | 3 件とも `Accepted` | **解除** |
| **FR-19 / FR-20** | 上記に加えて `ADR-0036`・`0037` の `Accepted` **かつ Wiki.js の個人スコープ可視性の前提検証の完了** | ADR は 2 件とも `Accepted`。**前提検証は未了** | **保留継続** |
| **FR-21** | 計画側が当該要求を確定（`fixed` 扱い）させること | **起案段階（`draft` 相当）のまま** | **保留継続** |

FR-19 / FR-20 の前提検証が未了であることは、計画側が同じ文書で明言している
（02_requirements（計画リポ）
の FR-19・FR-20 注記）。

> 2. **編集手段（Wiki.js）の前提検証が未了である。** Wiki.js の権限はページ／グループ単位であり
>    **個人スコープの可視性制御を持たない**。（略）**検証結果によっては編集手段の裁定が覆り得る**
> 3. 同期方式は ADR-0037 で確定する。（略）ただし上記 2 の検証結果に依存する
>    （**この依存は解消していない**）

FR-21 が起案段階のままであることも同じ注記の冒頭（2026-08-01・起案）が示しており、本 pin で
変更されていない。

#### 実測 3-b: **画面（SC）単位での着手可否** —— SC-18〜21 を 1 件ずつ

FR 単位の判定（上表）を、[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 1（「保留の対象は当該 FR を実現するプロダクトコードと、
**その受け入れを担う画面**・API・データモデル」）に従って**画面へ写した**。
関連要求は計画 05_screens（計画リポ） の
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
| **SC-09 の辺の型辞書区画** | FR-17 | **着手可**（[IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md) §結果 フォローアップ 2 の発火） | **#504 / #452** |
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

`ADR-0039`（SC-18 のグラフ描画ライブラリ）は **[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 2 の着手条件に含まれていない**
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

### 実測 7: FR-08 軸の母集合を**パス起点で**引き直した（PR #593 レビュー R2 の是正）

**実測 6 の 2 本目の grep はまだ絞りすぎていた。** 末尾に `| grep -i "feedback\|FR-08"` を付けたため、
**該当行そのものに `feedback` も `FR-08` も含まれない行が落ちた**。`docs/api/openapi.yaml:1220`
（`利用者は JWT から特定する（テスト・開発環境では anonymous）。`）がそれである。
**語ではなくパスを起点に**引き直す。

```console
$ for f in docs/api/openapi.yaml docs/api/BFF_bff-surface.md docs/functional/FR-08_answer-feedback.md \
           docs/tests/FR-08_answer-feedback.md docs/adr/IADR-0010_*.md docs/adr/IADR-0131_*.md \
           src/knowledge/backend/**/FeedbackEndpoints.cs ; do
    grep -n "anonymous\|AdminOnly は課さない\|認可なし\|401\|403" "$f" ; done
```

行番号は**是正前 → 是正後**で併記する（本 PR の追記そのものが行をずらすため）。

| # | live な偽・不整合 | 本 PR の扱い |
| --- | --- | --- |
| 1 | `openapi.yaml:1220`→`:1228` `利用者は JWT から特定する（テスト・開発環境では anonymous）。` | **日付つき追記（#521 送り）** |
| 2 | `openapi.yaml:1288`→`:1311` `集計値のみで PII を含まないため AdminOnly は課さない（BFF が集約して画面へ）。`——**失効した根拠を、時点も送り先も無い素の現在形で述べる唯一の live 契約記述** | **日付つき追記（#521 送り）** |
| 3 | `/feedback`・`/bff/feedback` の `responses` に **`401` が無い** | **足さない。#521 へ送る**（YAML コメントで明示） |
| 4 | `/feedback/stats`・`/bff/feedback/stats` の `responses` に **`403` が無い** | 同上 |
| 5 | `FeedbackEndpoints.cs:37` / `:80` / `:106` のコメント（`:106` は前版で母集合から漏れていた） | **コメント追記（#521 送り）** |
| 6 | [テスト仕様書 FR-08](../../docs/tests/FR-08_answer-feedback.md) に新受け入れ基準（401 / 403）の T- 番号が無い | **T-15 / T-16 を採番**（実装は #521） |

**境界（対象外と判断したもの）**: `openapi.yaml:1349`（`/dashboard/events`）も
`利用者は JWT から特定する（テスト・開発環境では anonymous）` と述べるが、**FR-10 の端点であり
FR-08 の認可軸には属さない**。計画は FR-10 の認可を変えておらず、同端点の
「認証済みなら誰でも記録できる（認証は必須）」は今も正しい。**次に引き直す人が同じ語で
再検討しなくて済むよう、除外の理由をここに残す。**

#### なぜ `responses` の `401` / `403` を #586 で足さないのか

レビューは「**契約記述なので #586 で足しても安全**」と述べたが、**実測すると安全ではない**。

```console
$ # /bff/feedback の responses へ "401" を 1 個だけ足して再生成した
$ pnpm run codegen && git diff --stat -- src/platform/frontend/src/foundation/api/generated/
 .../generated/feedback/feedback.ts | 17 ++++++++++++-----
 1 file changed, 12 insertions(+), 5 deletions(-)
```

差分は JSDoc ではなく**公開型そのもの**だった——`bffSubmitFeedbackResponse` が
`Success | Error` の union になり、`TError` の既定が `unknown` → `void` へ、
`BffSubmitFeedbackMutationError` が `unknown` → `void` へ変わる。つまり
**`/bff/*` の `responses` は文章ではなく、フロントエンドが消費するコミット対象の型である**。

これに加えて、本書は [IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) が定めた **BFF 契約の源**である。BFF には
`RequireAuthorization` が無く 401 / 403 を返さないため、**先に契約だけ宣言すると
「実装が返さない応答を契約が約束する」**——偽の向きが変わるだけで、しかも**それを守るテストが無い**。

| 選択肢 | 判定 | 理由 |
| --- | --- | --- |
| **A. 追記のみ・`responses` は #521（採用）** | ○ | 契約・実装・テストが**同じ PR で揃う**。生成物の再生成差分ゼロ（実測）。既に [通信仕様書 `:112` / `:113`（`/bff/feedback` と `/bff/feedback/stats` の表 2 行）と `:147-154`（追記ブロック）](../../docs/api/BFF_bff-surface.md) が #521 へ送っており、**対を崩さない**（**#593 レビュー 🟢1 の是正**: 前版は `:144` と書いていたが、同行は `GET /bff/admin/config/drift` で本件と無関係だった） |
| B. #586 で `responses` だけ足す | × | 実装が返さない応答を契約が宣言する。生成物の**公開型**が動き、`TError` の既定が変わる（実測）。守るテストが無い |
| C. 何もしない | × | `:1220` / `:1288` が時点も送り先も無い現在形の偽として残る（R2 の指摘そのもの） |

**`/bff/*` 側の注記は YAML コメント（`#`）で書いた。** `description` へ書くと
orval が JSDoc へ写して**生成物が動く**ためである（実測で確認済み）。
コメントはパーサに渡らないので**再生成差分は 0**。

#### `〔裁定依頼 planning#NNN〕` 記法の数え方（PR #593 レビュー 軽微）

PR 本文が述べた「**18 箇所**の書き分け」は**数え方が実体と違う**。実測すると:

```console
$ git diff origin/develop...HEAD | grep "^+" | grep -c "〔"      # 462410b 時点
18                       # ← 「〔 を含む追加行」の数。〔2026-08-05〕〔`Accepted`〕等も混ざる
$ git grep -o "〔裁定依頼 planning#[0-9]*〕" 462410b -- . ':(exclude)planning' \
    ':(exclude)src/ai-stock-trading' | wc -l
11                       # ← 記法の適用箇所（.md 8 / .tsx 2 / .js 1）
```

**正しい数え方は「記法の適用 11 箇所」**である（`〔` を含む追加行 18 行のうち、
`〔裁定依頼 planning#NNN〕` 形は 11、残りは日付・状態値を囲う既存用法）。

**［時点注記 / #593 レビュー 🟢2］上の 11 は `462410b` 時点の値である。** 本節の後（レビュー Y6）で
記法を `check-cross-repo-refs.js` の自己試験へ固定し、`.cs` / `.yaml` へも適用を広げたため、
**本 PR の最終形 `de4eb05` では 32**（`.md` 17 / `.js` 7〔うち 6 は `check-cross-repo-refs.js` の
自己試験フィクスチャ〕/ `.cs` 3 / `.yaml` 3 / `.tsx` 2）になる。**develop 取り込み後のリポジトリ全体では 34**
——差の 2 件は develop 側（[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 本体・#595 の作業仕様書）が持ち込んだもので本 PR の適用ではない。
以後この値は増え続けるため、**件数ではなく `--self-test` が形を守る**（レビュー Y6）ことに依拠する。

#### 記法を機械検査へ載せた（レビュー Y6）

レビューの変異試験どおり、`scripts/check-cross-repo-refs.js` の `SEP` に `〔` が無く、
**採用形が崩れても検査が黙っていた**。`SEP` へ `〔`（＋見出し語）を足し、正例・負例を対で
`--self-test` に固定した（68 件 all passed）。是正後の変異試験:

| 形 | 是正前 | 是正後 |
| --- | --- | --- |
| 採用形 `PR planning#244〔裁定依頼 planning#237〕` | 検出なし | **検出なし**（正しい） |
| 変異A `〔裁定依頼 #237〕`（〔〕内を裸に） | 検出なし ★ | **`enum` で検出**（→ `〔裁定依頼 planning#237`） |
| 変異B `〔裁定依頼 planning #237〕`（空白修飾） | `spaced` | `spaced`（据え置き） |
| 変異C `PR #244〔裁定依頼 planning#237〕`（先頭を裸に） | 検出なし | **検出なし（仕様）** |
| 変異D `〔#237〕`（見出し語なしで裸） | 検出なし | **`enum` で検出** |

**変異C を検出しないのは仕様である。** 裸の `#244` は規約上「本リポジトリの PR」を意味する
**正しい表記**であり、意味の取り違えは構文から判定できない。負例として自己試験に固定した。

**全角丸括弧 `（` は `SEP` に入れなかった。** レビューは `〔` と併せて足すよう述べたが、
実測すると**偽陽性が出る**——`feedback/20260805_sc05-07-admin-contract-gaps.md:44` の
`planning#197（#502 由来）` の `#502` は本リポジトリの issue であり、止めてはならない。
`〔` のみを足した場合の追加検出は追跡下 526 件の `*.md` で **0 件**（＝偽陽性なし）だった。
この線引きは `.claude/rules/traceability.md`「関連番号を添える注記 `〔〕`」に**規約として明文化**した
（検査が規約の裏づけを持たないまま増えるのを避けるため）。

## 追補・改定・Superseded の判断（[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) をどう追随させるか）

**採るのは「日付つき追補」である。** 根拠は [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 6 そのものである。

> 6. **保留の解除は前提 ADR の確定を確認した時点で行う。** 解除時は本 IADR の §フォローアップを更新し、
>    着手する issue の作業仕様書に確定を確認した ADR とその状態を記録する。**前提が確定しないまま着手が
>    必要になった場合は、本 IADR を改める新 IADR を起票する**（本 IADR の本文は書き換えない）。

| 選択肢 | 判定 | 理由 |
| --- | --- | --- |
| **A. 日付つき追補（採用）** | ○ | 決定 6 が**解除の手順として §フォローアップの更新を自ら指定**している。条件つきの決定の条件が満たされたことを記録する行為であり、**新しい決定を含まない** |
| B. 新 IADR による改定 | × | 決定 6 が新 IADR を要求するのは「**前提が確定しないまま**着手が必要になった場合」であり、本件は正反対（前提が確定した） |
| C. `Superseded` にする | × | 決定 1〜6 のどれも覆っていない。しかも**保留は FR-19〜21 について継続している**ため、本 IADR は今も効力を持つ。`Superseded` にすると FR-19〜21 の保留根拠が宙に浮く |
| D. 何もしない（pin だけ進める） | × | #586 の受け入れ基準「IADR-0119 の記述が現状と一致する」を満たさない。加えて 2026-08-06 追記が「保留は継続する」と述べたままになり、**FR-17 / FR-18 の着手判断を誤らせる** |

状態は `Accepted` のまま据え置く。[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 7 についても同様に**事実の更新のみ**を
日付つき追記で行う（同規約には 2026-08-04 に同型の追記を行った先例がある）。

## 受け入れ基準

- [x] `git submodule status planning` が `3e58b97` を指す
- [x] `git diff --name-only origin/develop...HEAD` に `planning` が **1 エントリだけ**現れ、
      `planning/...` のパスが 1 件も現れない（＝ pin だけが動いている）
- [x] planning を populate した状態で `node scripts/check-doc-links.js` が緑
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
- [x] [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) の記述が現状（FR-17 / FR-18 は解除・FR-19〜21 は保留継続）と一致する
- [x] #450 / #451 / #452（SC-18〜21）/ #521 の着手可否を **1 件ずつ**実測して記録した
      （**§実測 3-b** に SC-18 / SC-19 / SC-20 / SC-21 の 4 行と issue 単位の対応を置いた）
- [x] **解除で偽になる live な記述が残っていない**——母集合をディレクトリではなく
      「時点つきの記録か live な現在形の主張か」で取り直し、`Proposed` 軸と FR-08 の認可軸の
      両方で引き直した（**§実測 6**）。FR-08 軸は**語ではなくパスを起点に**引き直した（**§実測 7**）
- [x] **是正したファイル自身の中に、追記と矛盾する現在形の偽が残っていない**——
      §未決事項・§関連仕様のような**本文から離れた節**まで見る（#593 レビュー R1）
- [x] **コードだけを直して仕様書を残す非対称が無い**——同じ予告が `.tsx` と画面仕様書の
      両方にある場合は**対で**是正する（#593 レビュー Y1。[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) の予告表は 3 件 → **4 件**）
- [x] **計画が追加した受け入れ基準がテスト仕様書へ写像されている**（T-15 / T-16。実装は #521）
- [x] **採用した記法が機械検査で守られている**——`〔〕` を規約へ明文化し、
      識別可能な変異で `check-cross-repo-refs.js` が落ちることを実測した（**§実測 7**）
- [x] **`pnpm run codegen` の再生成差分が 0**（`openapi.yaml` を触ったため実走して確認）

## テスト方針

本作業はプロダクトコードの**挙動**を変更しない（`.tsx` / `.cs` はコメントのみ）ため、
プロダクト側の新規ユニットテストは書かない。検証はリポジトリの機械検査
（`check-doc-links` / `scripts.test.js` / `check-commit-messages` / `check-cross-repo-refs`）で行う。
FR-08 の認可の**テスト**（無認証 401・権限外 403・重複投稿が 1 件）は **#521 の範囲**である
——ただし**受け入れ基準の写像は先に済ませる**（`CLAUDE.md` の必須事項）。テスト仕様書 FR-08 へ
**T-15 / T-16 を採番**し、実装欄を「未実装 —— #521」とした（#593 レビュー Y4）。

`scripts/check-cross-repo-refs.js` は変更したため、**自己試験を対で増やす**
（`〔〕` 記法の正例・負例を 10 件追加。68 件 all passed）。あわせて**識別可能な変異で
実際に落ちること**を実データで確認した（§実測 7 の変異表）。

## 計画書との差異

- 差異: **あり**（1 件。環流する）

計画 `02_requirements/01_requirements.md` の 2026-08-07 追記は

> **FR-17〜21 の着手を止めていた条件は解消している。**

と**FR-17〜21 を一括りにして**述べているが、**同じ文書の FR-19・FR-20 注記 2・3 は前提検証の未了と
その依存が「解消していない」ことを明記しており、FR-21 は起案段階のままである**。追記は文脈上
「前提 ADR の状態」だけを指していると読めるが、**この 1 文だけを読んだ実装側は FR-19〜21 まで
着手可能と誤読する**。実際、本 issue（#586）の解除表は #451 を「解除」と分類している。
`feedback/20260807_fr17-21-gate-scope-ambiguity.md` に記録した。

## develop 取り込み時の衝突解消（#585 / #596）と 🟢 申し送りの回収

develop が 2 つ進んだ（`78c9753` = PR #596〔#595〕／`3d26653` = PR #585〔#580〕）。
**衝突したのは [`docs/adr/README.md`](../adr/README.md) の 1 ファイルのみ**で、
#585 が索引テーブル 141 行を全面書き換え（ID セルのリンク化・`Superseded` 表記の統一・
`IADR-0061` のタイトル短縮）したのに対し、本 PR も索引行へ追記を入れたためテーブルが
まるごと 1 ハンクとして衝突した（`<<<<<<< HEAD` = `:56`、`>>>>>>> origin/develop` = `:340`）。

**解き方: develop 側（`3d26653`）を土台にし、本 PR の追記だけを載せ直した。**
本 PR が索引で実際に変えた行は**実測して 3 行**である（`git diff cf15568..de4eb05 -- docs/adr/README.md`
＝ 3 行の追加・3 行の削除）。

| 索引行 | 本 PR の追記 | develop 側の変更 |
| --- | --- | --- |
| [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（develop 版 `:172`） | 規約 7 の保留根拠が消滅した旨（2026-08-07 / #586） | **ID セルのリンク化のみ** |
| [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md)（develop 版 `:175`） | 2026-08-07 追補（解除は FR-17 / FR-18 に限る） | **ID セルのリンク化のみ** |
| [IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md)（develop 版 `:185`） | 決定 1 の「辺の型辞書 = A」の理由が失効した旨 | **ID セルのリンク化のみ** |

3 行とも「ID セル以外は base と完全一致」を実測で確認したうえで、**develop の ID セル ＋ 本 PR の
残りセル**として合成した。結果、解決後のファイルは **develop と 3 行だけ異なる**（`git diff -U0` で確認）。

**#585 が新設した検査に触れないことの実測**:

| 検査 | 結果 |
| --- | --- |
| 全行リンク形式（`not-linked`） | 索引 141 行すべてがリンク形式（違反 0） |
| `IADR-0061` の短縮 | タイトル **28 文字**・状態 `Accepted` のまま（戻していない） |
| `title-too-long`（`maxTitleChars` = 200） | 3 行とも**追記前から既に超過**しており baseline 登録済み。文字数は 0116 が 1260 → 1702、0119 が 1574 → 1915、0129 が 2305 → 2453。**新しい違反種別は 1 つも増えていない**ため baseline は 1 行も触っていない |
| `title-addendum`（`［YYYY-MM-DD 追記`） | 0116 / 0129 の追記は `［2026-08-07 / #586］`、0119 は `［2026-08-07 追補 / #586］` と**「追記」を使わない**ため新規混入なし。0119 は base 由来の `［2026-08-04 追記］` が残るので baseline 登録が stale にもならない |
| `title-drift`（本体 `title:` と LCS 12 文字以上） | 既存本文を残したうえでの追記のため共有文字は減らない。違反 0 |

> **［判断］タイトルセルを縮める是正は本 PR で行わない。** 3 行はいずれも develop 時点で
> 1200〜2300 文字あり、#585 の baseline に `title-too-long` として載っている既知債務である。
> 本 PR の追記は 148〜442 文字を足すが、**違反の種別を増やさず baseline も広げない**。
> 縮める作業は #580 が定めた「索引タイトルセルを本体 `title:` の要約へ縮める」方向の是正であり、
> **pin 更新の PR で 3 行だけ先行して縮めると、追記の内容ごと落ちる**（追記は「保留が解けた」という
> 事実そのもの）。**縮めるなら索引全体を対象にした別 PR**で行う。

### 回収した 🟢 申し送り 4 件（いずれも自分で実測して確認した）

| # | 指摘 | 実測 | 是正 |
| --- | --- | --- | --- |
| 🟢1 | §実測 7 の選択肢表 A 行「通信仕様書 `:144`」が誤り | `BFF_bff-surface.md:144` は `GET /bff/admin/config/drift`（FR-15 / SC-11）の行で無関係。正しくは **`:112`（`POST /bff/feedback`）/ `:113`（`GET /bff/feedback/stats`）**と **`:147-154`**（`［2026-08-07 追記 / #586］` の引用ブロック。**末尾は `:154`〔「同型の記述:」行〕まで**） | §実測 7 の A 行を差し替えた |
| 🟢2 | §実測 7 の「記法の適用 11 箇所」に時点注記が無い | `462410b` = **11**（`.md` 8 / `.tsx` 2 / `.js` 1）、`de4eb05` = **32**、develop 取り込み後 = **34**（差の 2 件は develop 側の [IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 本体と #595 の作業仕様書） | 時点注記と最終値を追記し、**件数ではなく `--self-test` に依拠する**旨を明記した |
| 🟢3 | [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 予告表 2 行目の「一語一句同じ予告」が不正確 | `DocumentDetailPage.tsx:38` =「**保留が解けた時点で** SC-18 / SC-21 の実装と同じ段で本画面へ足す」、`SC-03:98` =「**前提 ADR が `Accepted` になった時点で**、SC-18 / SC-21 の実装と同じ段で本画面へ足す」。一致するのは後半のみ | [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) の表と本書 §対象範囲の記述を「**実質同じ予告**」へ改め、差分を明記した |
| 🟢4 | frontmatter `updated` が据え置きで非対称 | SC-01 = `2026-08-06`、SC-03 = `2026-08-05`、テスト仕様書 FR-08 = `2026-07-03`、同 SC-10 = `2026-08-05` の 4 件が据え置き。同 PR で触った SC-09 / SC-10 画面仕様書・FR-08 機能仕様書・通信仕様書・IADR 5 本は `2026-08-07` | 4 件を **`2026-08-07`** へ更新した（本文は変更していない） |

## 未決事項・親への申し送り

1. **#451（FR-19 / FR-20）は着手できない。** Wiki.js の個人スコープ可視性の前提検証が未了であり、
   [IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 2 の条件を満たさない。**この検証は Wiki.js の実環境が要る**ため、
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
   （[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) の 2026-08-07 追補 §解除で発火した予告 に一覧がある。同表には
   [IADR-0129](../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md) §結果 フォローアップ 2 が予告していた **SC-09 の辺の型辞書区画**と
   **SC-10 のナレッジ健全性節**〔引き受け先 **#504 / #452**〕も載せた）。
7. **［#521 へ］FR-08 の認可について、live 文書 8 箇所が計画と食い違ったまま残っている。**
   本 PR は**送り先つきの日付追記**を入れただけで、記述と実装は変えていない。是正時に対にすること——
   [機能仕様書 FR-08](../../docs/functional/FR-08_answer-feedback.md)（API 表の「認可なし」・例外フローの
   `anonymous` 受理）／[通信仕様書](../../docs/api/BFF_bff-surface.md)（冒頭の `status` 理由・
   エンドポイント一覧の 2 行・§未決事項 3）／[IADR-0010](../adr/IADR-0010_feedback-service-and-upsert.md) 決定「一覧の認可」／
   [IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) §結果 フォローアップ 4／
   **[`docs/api/openapi.yaml`](../../docs/api/openapi.yaml)（`/feedback` の `anonymous`・`/feedback/stats` の
   「AdminOnly は課さない」・`/feedback` `/bff/feedback` の `401` 欠落・`/feedback/stats`
   `/bff/feedback/stats` の `403` 欠落）**／
   **`FeedbackService.Api/Foundation/Endpoints/FeedbackEndpoints.cs:37,80,106` のコード内コメント**／
   **[テスト仕様書 FR-08](../../docs/tests/FR-08_answer-feedback.md) の T-15 / T-16**（本 PR で採番だけ済ませた）。

   > **［是正 / #593 レビュー Y3］コード内コメントを触らなかった理由は誤っていた。**
   > 前版は「.NET SDK が無くビルド検証ができない」を理由に `FeedbackEndpoints.cs` を対象外にしたが、
   > **コメントのみの変更は C# コンパイラの対象外であり、ビルド検証を要しない**。同じコミットが
   > `.tsx` に対して行ったコメントのみの変更と**リスクは同一**である（線引きが成立していなかった）。
   > **本追加コミットで 3 箇所（`:37` `:80` `:106`）へ送り先つきコメントを足した。**
   > とくに `:106` は `/feedback/stats` の直上——**実装者が最初に読む場所**——であり、前版では
   > 母集合からも漏れていた。**コメント以外は 1 文字も変更していない**（`git diff` は 17 行の追加のみ）。
   > 正しい線引きは「**挙動が変わるか**」である: コメント・仕様書 = #586 で可、
   > `RequireAuthorization` の追加・OpenAPI の `responses` 追加・テスト = **#521**（後述 §実測 7）。
