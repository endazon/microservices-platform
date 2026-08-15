---
title: システム投入経路の lifecycle 既定が未裁定である（owner / department だけが確定し 3 属性目が落ちている）
type: plan-feedback
status: open
category: 要求の不足
related_ids:
  - FR-05
  - ADR-0036
  - ADR-0034
source_repo: endazon/microservices-platform
source_ref: docs/specs/20260815_issue-516_abac-required-attributes.md
author: claude
created: 2026-08-15
dispatched: true
planning_issue: 361
---

# フィードバック: システム投入経路の `lifecycle` 既定が未裁定である

## 種別

要求の不足（**確定済みの節が、自分の前文が挙げた 3 属性のうち 2 つしか決めていない**）

## 起点となる計画書

- 機能要求（FR）: FR-05（文書属性・ABAC）
- ユースケース（UC）: UC-03（文書登録）
- 画面（SC）: SC-05（文書管理。`lifecycle` の表示と公開・アーカイブ操作）
- 関連 ADR: ADR-0036（所有者ベース裁量制御）／ADR-0034（ホップごとの ABAC 強制）
- 計画書リンク: `06_technical/09_datasource-connectors.md` §システム投入経路での `owner` / `department`（確定・2026-08-15）／`06_technical/07_abac-attribute-model.md` §文書の基本属性

## 現状（計画書の記述 / As-Is）

2026-08-15 の裁定（裁定依頼 planning#344）で `09_datasource-connectors.md` に
**§システム投入経路での `owner` / `department`** が確定として新設された。

同節の**前文はこう書いている**。

> 実装側の実測で、稼働中の 2,368 件に **`department` / `owner` / `lifecycle`** が
> **1 件も付与されていない**ことが判明したため、既定を明文化する

**しかし確定した既定表は `owner` と `department` の 2 行しかなく、`lifecycle` の行が無い。**
節の見出しも「`owner` / `department`」であり、3 属性目が落ちている。

`07_abac-attribute-model.md` §文書の基本属性 は `lifecycle` を**必須**・値域
`draft` / `active` / `archived` と定めるが、**どの経路がどの値で作るかはどこにも書かれていない**。
計画書全体を走査しても、`lifecycle` の**初期値**を定めた記述は存在しない
（`05_screens/01_screens.md:264` の SC-05 は「状態の**表示と公開・アーカイブの操作**」であり、
作成時の初期値ではない）。

## 問題点 / あるべき姿（To-Be）

**実装が `lifecycle` を決め打ちすると、その値がそのまま可視性を決めてしまう。**

- `active` を入れる → ポリシーの `allowedLifecycles` に `active` があれば**即座に閲覧対象**になる。
  裁定なしに実装が「公開してよい」と決めたことになる
- `draft` を入れる → 多くのポリシーで到達不能になり、**取り込んだ文書が誰にも見えない**。
  `owner` / `department` の裁定が「**deny 側に倒れることを確認したうえで**」予約値を選んだのに対し、
  `lifecycle` は**倒れる向きが有用性の側で問題になる**

`owner` / `department` は「解決できないときの予約値」という形で決着したが、
**`lifecycle` にはそもそも「ソース側から解決する」対応物が無い**（ファイルの状態は
`draft` / `active` / `archived` に写像できない）。したがって同じ形の解決にはならず、
**別途の裁定が要る**。

## 実装で判明した経緯

issue #516（取り込み経路が必須属性を付与していない）の着手前調査で、
属性を組み立てる全経路を実測して母集合を引いたところ判明した。

| 経路 | 属性の組み立て点 | `lifecycle` |
| --- | --- | --- |
| 取り込み | `DataSourceSyncService.cs:67` `source.GetEffectiveAttributes()` | 付かない |
| 人手（SC-03/05） | `DocumentBffEndpoints.cs` の `Attributes` 素通し | 付かない |
| 保存前検証 | `DocumentAttributes.ValidateConfidentiality` | 検証していない |
| フロント | `features/sc03-document/attributes.ts:34` `known = ['confidentiality','department']` | ラベルすら無い |

#516 の受け入れ基準は「計画の必須属性 **4 種がすべて**付与されている」であり、
**`lifecycle` の既定が決まらないと #516 は閉じられない。**

## 提案（計画への反映案）

- 反映先候補: **要求更新**（`09_datasource-connectors.md` §システム投入経路 へ 1 行追加）
- 提案内容: 次のいずれかをご裁定いただきたい。**実装側は推奨を示さない**
  （どちらも副作用があり、`owner` / `department` と違って「安全側」が一意に決まらないため）。

| 案 | 既定 | 副作用 |
| --- | --- | --- |
| **案 A** | `active` | 取り込み文書が既定で検索・閲覧の対象になる。**「未設定は公開しない」の思想と緊張する**が、取り込み文書は元々共有目的で置かれた資料である |
| **案 B** | `draft` | deny 側に倒れる。ただし**取り込んだ全文書が既定で不可視**になり、SC-05 の管理者が 1 件ずつ公開操作をしない限り**ナレッジベースとして機能しない** |
| **案 C** | データソース単位で管理者が選ぶ（既定属性へ `lifecycle` を含められるようにし、未設定時は案 A か案 B へ倒す） | 終端の既定は結局必要。ただし**運用で吸収できる** |

併せて、節の**見出しと前文の非対称**（前文は 3 属性・表は 2 属性）の解消もお願いしたい。
`lifecycle` を対象外とする裁定であれば、**その旨を明記**していただければ実装は
「必須だが取り込み経路では付けない」を確定事項として扱える。

## 影響範囲

- #516（本 issue）の完了条件。`lifecycle` が決まるまで**受け入れ基準を満たせない**
- ADR-0034（ホップごとの ABAC 強制）の検証。判定軸が `confidentiality` 1 本の状態が続く
- #450（FR-17/FR-18 知識グラフ・AI 提案）の前提
- SC-05 の公開・アーカイブ操作の初期状態
