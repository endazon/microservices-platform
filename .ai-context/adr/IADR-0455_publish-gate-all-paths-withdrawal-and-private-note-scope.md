---
title: IADR-0455 DocumentUpdated の発行はすべて門を通す。属性を書き換える経路は撤収の形の門を使い、門の述語は個人資料にだけ効かせる
type: impl-adr
status: Accepted
related_ids:
  - FR-19
  - FR-06
  - FR-21
  - UC-11
  - SC-19
  - ADR-0054
  - ADR-0058
  - ADR-0061
  - IADR-0396
author: claude
created: 2026-09-15
updated: 2026-09-27
plan_refs:
  - "planning:projects/microservices-platform/07_adr/ADR-0061 決定 1・2・4（露出 3 トグルの索引への載せ方）"
related_specs:
  - ../specs/20260915_issue-1471_publish-gate-all-paths.md
  - ../specs/20260915_issue-1474_sync-conflict-resolve-publish.md
---

# IADR-0455: `DocumentUpdated` の発行はすべて門を通す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-15
- 決定者: claude（実装判断）／起点 issue #1471

## 起点・関連

- 関連する計画書 ID: `FR-19` / `FR-06` / `FR-21` / `UC-11` / `SC-19` / `ADR-0061` 決定 1・2・4 / `ADR-0054` / `ADR-0058` 決定 1・2
- 関連する実装 ADR: [IADR-0396](IADR-0396_private-note-exposure-index-production.md) 決定 4（発行の門）・決定 5（撤収）。**本 ADR は同決定 4 の実装形を補う**
- 関連する実装仕様書: [`20260915_issue-1471_publish-gate-all-paths.md`](../specs/20260915_issue-1471_publish-gate-all-paths.md)（母集合・経路ごとの判断の正本）

## コンテキストと課題

IADR-0396 決定 4 は「個人資料は露出 3 トグルのうち 1 つでも ON のときだけ `DocumentUpdated` を出す」門を
`DocumentEndpoints.PublishUpdatedIfIndexableAsync` として置いた。PR #1281 のレビューで「門を一部の経路にしか付けていない」と
指摘され、修正 `c4830568` が作られたが、**squash マージの後に push されたため develop に入らなかった**（#1471）。
2026-09-15 の develop では 10 経路が門を通らずに発行している。

`c4830568` の意図（全経路を `PublishUpdatedIfIndexableAsync` へ寄せ、SetExposure だけを例外にする）を現在のコードへ当て直す際に、
**そのままでは正しくない点が 2 つ**見つかった。

1. **撤収は SetExposure だけの問題ではない。** 管理者の `PUT /documents/{id}` / `PATCH /documents/{id}/metadata`、
   および正規化の取り込みは**属性を全置換する**。個人資料の露出キーを落とす・`excluded` にする保存は、SetExposure と同じ
   ON → OFF の遷移を作る。単純な門を当てると撤収のイベントが弾かれ、**本文が索引に残る**（IADR-0396 決定 5 が禁じた形）。
2. **「組織文書は `IsIndexable` が常に true」は構造の保証ではない。** `DocumentExposure.IsAllowed` は明示値を文書種別より優先し、
   組織文書が露出キーを持つことを拒否する検証は無い。露出キーを全 `excluded` にした組織文書は門で発行が止まり、
   `WikiService` のアーカイブ（ページの非公開化）が届かなくなる。

## 検討した選択肢と決定

### 決定 1: 門を 2 つの形で持ち、素の発行（`PublishUpdatedAsync`）は `private` にする

| # | 案 | 評価 |
| --- | --- | --- |
| 1-A | `c4830568` のとおり全経路を単純な門へ寄せ、SetExposure だけ直接呼び出しを残す | 属性を全置換する 3 経路で撤収が落ちる（上記 1）。例外が 1 つ残り「すべて門を通る」と言えない |
| **1-B** | **単純な門（今通るとき）と、撤収の形の門（今通る、または書き換える前は通った）を `DocumentEndpoints` に並べ、素の発行を `private` にする**（採用） | 経路の性質（属性を変えるか）で門を選ぶだけになる。**例外が 0**。`DocumentEndpoints.` 付きの直接呼び出しはコンパイルで止まる |
| 1-C | 門を 1 つにし、常に「直前の状態」を引数で渡させる | 属性を変えない 6 経路にも無意味な前状態の取得を強いる。渡し間違い（書き換えた後に取る）の機会が増える |

- 属性を変えない経路: `PublishUpdatedIfIndexableAsync`。
- 属性を書き換え得る経路: `PublishUpdatedIfIndexableOrWithdrawingAsync(…, wasPublishable, …)`。前状態は
  **書き換える前に** `DocumentEndpoints.PassesPublishGate(doc)` で取る（SetExposure の「変更『前』の値で判定する」と同じ作法）。
- 🔴 **ポートを直接叩く形（`bus.PublishUpdatedAsync(`）は型では止まらない。** DocumentService の本番ソースを走査し、
  ポート宣言・アダプタ・`DocumentEndpoints.cs` 以外に現れたら落ちる試験（`PublishGateCoverageTests`）で止める。

**［2026-09-15 追記 / #1474］「すべての本番経路」には、発行を 1 度も呼ばない経路が抜けていた。** 本 ADR の走査は
「素の発行を呼ぶ経路」（誤りの側）から引いたため、**本文を書き換えるのに何も発行しない経路**は母集合に入らなかった。
`SyncConflicts/Resolve` の `local`（ローカル本文で資料を上書きする）と `both`（ローカル本文で別名の新規資料を作る）がそれで、
露出 ON の個人資料で `local` を選ぶと索引は古い本文のまま残っていた（#1471 の作業仕様書が「対象外・未決」として残した件）。
#1474 で両分岐を**単純な門**（`PublishUpdatedIfIndexableAsync`）へ通した —— どちらも属性を書き換えない（`local` は現在の属性を
そのまま渡し、`both` は新規作成）。`both` の別名資料は作成の既定（露出 3 トグル OFF）で作られ元の資料の露出を継がないため、
現行は門に弾かれて発行されない（push の新規作成と同じ形）。**`PublishGateCoverageTests` は「呼ばない経路」を検出しない**
（素の発行の走査である）。本文を書く／資料を作る経路の母集合と挙動の試験は
[`20260915_issue-1474_sync-conflict-resolve-publish.md`](../specs/20260915_issue-1474_sync-conflict-resolve-publish.md) が正本である。

### 決定 2: 門の述語は**個人資料にだけ**効かせる

| # | 案 | 評価 |
| --- | --- | --- |
| 2-A | 門の述語を `IsIndexable` のまま全文書に当てる | 露出キーを明示した組織文書で発行が止まる（上記 2）。「組織文書は不変」がデータ次第になる |
| **2-B** | **`!DocumentScopes.IsPrivateNote(attrs) \|\| DocumentExposure.IsIndexable(attrs)`**（採用） | 組織文書は常に通る（データに依らず不変）。個人資料は `IsIndexable` そのもの |
| 2-C | 組織文書が露出キーを持つことを検証で拒否する | 既存データ・取り込み経路への影響を測れていない。**露出の意味を組織文書へ広げるかは計画の射程**であり、本件で決めない |

**IADR-0396 決定 4 の「生産側と消費側が同じ関数を呼ぶ」は崩していない。** 個人資料に対して門が評価するのは
`IsIndexable` そのものであり、消費側（`IngestionService`）も同じ関数で削除を判断する。組織文書は従来どおり門の外にあり、
受け手が同じ述語で削除する（直接呼び出しだった経路の挙動のまま）。

## 理由

- **決定 1** は「発行の門がある」という説明とコードの実態を一致させ（PR #1281 のレビュー指摘）、かつ撤収（決定 5）を
  経路の書き漏らしで失わないため。門の選択を「その経路が属性を書き換えるか」という**コードから読める性質**に結び付けた。
- **決定 2** は IADR-0396 決定 4 の本文（「個人資料は…のときだけ流す」「組織文書は常に true」）を、データの前提から構造へ移したもの。

## 結果

- **良い影響**:
  - `DocumentUpdated` を出す本番経路 13 本がすべて門を通る（直接呼び出し 0）
  - 管理者の属性全置換・正規化による ON → OFF でも撤収が届く（試験で固定）
  - 組織文書の発行は、露出キーの有無に依らず従来と同一
- **悪い影響・トレードオフ**:
  - 全 OFF の個人資料に対する AddTag / Archive / Publish / PutBody / Rename / 管理者更新は、発行しなくなる。
    受け手の作用は従来も空振り（削除・撤収・Wiki は個人資料を同期しない）であり、失われる作用は無いと判断した
  - 共有の付与・取り消しは、露出キーを全 `excluded` にした組織文書でだけ発行されるようになる（消費側は冪等）
  - SetExposure の全 OFF → 全 OFF の保存でタグ辞書の読み取りが 1 回増える（発行はしない）
- **未実測**: 稼働クラスタで「露出キーを持つ組織文書」が実在するかは測っていない（本作業ではクラスタに触れない）。決定 2 により挙動は実在の有無に依存しない

> **［2026-09-27 追記 / #1629］管理者の `PUT /documents/{id}` / `PATCH /documents/{id}/metadata` は個人資料に届かなくなった**
> （[IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md) 決定 1 の 2026-09-27 追記。主体を問わず 404）。
> 上の「コンテキストと課題」1 と「結果」の「管理者の属性全置換による ON → OFF でも撤収が届く」は、管理者が他人の個人資料を
> 書き換えられることを前提にしており、その前提自体が計画 ADR-0036 D-08・ADR-0119 決定 3 の違反だった。
> 2 口は撤収の形の門（決定 1）を使い続けるが、到達するのは組織文書だけになり、組織文書では撤収の形と単純な門は同値である
> （決定 2 により組織文書は常に門を通る）。**管理の口からの個人資料の撤収の経路は無くなり、所有者の SetExposure が担う**（正規化の取り込みの撤収の形は決定 1 のまま変えない）。
> #1471 が置いた `PrivateNoteExposurePublishTests` の管理者更新の 2 試験（撤収が出る／全 OFF で出ない）は、
> 「管理者の保存は 404 で撤収も書き換えも起きず、所有者の SetExposure なら撤収まで届く」の 1 試験（PUT / PATCH）へ置き換えた。

## 関連

- Supersedes: なし（IADR-0396 は現行。同 ADR へ日付つき追記で本 ADR を併記した）
- Superseded by: なし
- 実装 issue: **#1471（本 ADR を起こした issue）** / #1184（IADR-0396）/ PR #1281（元の実装とレビュー指摘）
