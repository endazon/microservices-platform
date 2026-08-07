---
title: 作業仕様書 — planning pin を裁定反映後（ADR-0033〜0037 が Accepted）へ進め、IADR-0119 の着手ゲートを追随させる
type: spec
status: done
related_ids: [NFR, IADR-0119, IADR-0116, FR-08, FR-17, FR-18, FR-19, FR-20, FR-21]
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
- 関連 IADR: [[IADR-0119]]（本作業で追補）／[[IADR-0116]] 規約 7（事実の更新）
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
| [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) | pin 参照（`e36b592` → `3e58b97`）と、2026-08-06 追記ブロック（「保留は解除されない」）の追随 |
| [`feedback/`](../../feedback/) | 計画側の記述の内部不整合 1 件を記録（後述「計画書との差異」） |

### 対象外（送り先を明記する）

| 対象外 | 理由 | 送り先 |
| --- | --- | --- |
| `planning/` の内容変更 | `CLAUDE.md` の規約。実装ブランチで許されるのは **pin 更新のみ** | — |
| FR-17 / FR-18 の実装着手（GraphService 等） | 本作業は**ゲートの記録**であり実装ではない。着手は各 issue の作業仕様書から始める | **#450 / #452（SC-18・SC-21）** |
| FR-08 の認可の実装（`/bff/feedback` の `RequireAuthorization`） | 同上。挙動の変更を伴うため独立した PR とテストが要る | **#521** |
| `src/ai-stock-trading` の pin | 本 issue の範囲外（#586 の禁止事項） | — |
| 過去の `docs/specs/` に残る「ADR-00xx は `Proposed`」の記述 | **時点つきの作業記録**であり、事後に書き換えると当時の判断根拠が読めなくなる（履歴不変の原則と同じ考え方） | — |

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
