---
title: IADR-0157 SC-07 の人手補正 UI は非 JSON 応答を出口 1 箇所で解き、確認は 409 を受けてから出す
type: impl-adr
status: Accepted
related_ids:
  - FR-12
  - UC-06
  - SC-07
  - IADR-0009
  - IADR-0121
  - IADR-0127
  - IADR-0135
  - IADR-0141
  - IADR-0154
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
---

# IADR-0157: SC-07 の人手補正 UI（Phase 1）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **FR-12 / UC-06 / SC-07**。実装 issue: **#651** ／ 作業仕様書: [20260810_issue-651](../specs/20260810_issue-651_sc07-manual-correction-ui.md)
- 契約は **#543**（[[IADR-0154]]）で揃っていた。本 IADR は**画面側の決定**に閉じる。

## コンテキストと課題

計画（`05_screens:312`、2026-08-05 確定）は 2 ペイン編集・「補正あり」標識・
**補正が失われる旨の明示確認**を求めている。契約は揃っていたが、着手して初めて
**契約と画面のあいだの層が通っていない**ことが分かった。

## 決定 1: 非 JSON 応答は `bffFetch`（出口 1 箇所）で解く

`GET /bff/conversion/jobs/{id}/figures/{figureId}/image` は **`docs/api/openapi.yaml` 唯一の
非 JSON 応答**（`image/*` / `format: binary`）である。生成フックの宣言型は `data: Blob` だが、
生成コードが通る唯一の HTTP 出口 `foundation/api/orvalMutator.ts` の `bffFetch` は
**Content-Type を見ずに `res.text()` → `JSON.parse`** していた。

**すなわち生成フック `useBffConversionJobFigureImage` は、生成された時点で実行不能だった。**
実測（Node 22）:

```
SyntaxError: Unexpected token '', "PNG\r\n\n   IHDR" is not valid JSON
```

**#543 は端点と生成物を載せたが、その応答を読む層を誰も通していない**（画面がまだ無かった）。
これは **#640 で `usageCount` が画面まで届かなかったのと同じ型**——「誰がこの応答を読むか」の
軸を引き漏らすと、契約は正しいのに画面が動かない。

**直す場所を `bffFetch` にした理由**: [[IADR-0121]] 決定 3 が「SPA から出る HTTP はこの関数
1 箇所に収束させる」と定めた**その 1 箇所**である（[[IADR-0141]] 参照点を 1 つに畳む）。
呼び出し側で `apiRequest` を直に叩いて迂回する案・`foundation/api` に `apiBlob()` を足して
生成フックを使わない案はいずれも**壊れた生成フックが「使える顔をして」残る**ため採らなかった。
次に画像口が増えたとき同じ穴を踏む。

### 判定式は「非 JSON のときだけ blob」であって「JSON でなければ blob」ではない

> Content-Type が**空**または `json` を含むなら従来どおり `text()` → `JSON.parse`。
> **それ以外のときだけ** `blob()`。

ヘッダを持たない応答を**従来どおり JSON 経路へ通す**ためである。画面テストのスタブ
（`foundation/testing/bffResponse.ts`）は `new Headers()`＝Content-Type 無しを返しており、
ここが blob 側へ落ちると **100 超の画面テストが一斉に壊れる**。
分岐追加後も**既存 626 件が緑のまま**であることを実測した。

**非 2xx はここへ到達しない**（`apiRequest` が `ApiError` を投げる）ので、
`application/problem+json` は本判定の考慮外である。

## 決定 2: 再変換の確認は **409 を受けてから**出す

`hasCorrection` を画面が読んで**先に**分岐し、確認が通ってから投げる形にはしない。

理由は 2 つある。

1. **一覧を取ってから押すまでの間に補正が入ると、無確認で消える。** 画面の持つ
   `hasCorrection` は取得時点のスナップショットにすぎない。
2. **件数がサーバの値でなければ嘘になる。** 確認文に出す「n 件」は
   409 本文の `correctedFigures` を唯一の情報源とする（画面側に複製しない）。

したがって: 押す → サーバが **409 `corrections_would_be_lost`** で止める → その本文を読んで
確認を出す → 確認後に `?discardCorrections=true` で再送する。**#640 のタグ削除
（`usageCount` つき 409）と同じ形**である（[[IADR-0154]] 決定 4）。

`not_retryable` の 409 とは**明確に区別する**——取り違えると「実行中のジョブに補正破棄を
勧める」ことになる。本文の `error` で出し分ける。

## 決定 3: 縮退・補正の表示は導出であり、`status` の 5 値目にしない

`05_screens:320`「ジョブ状態モデルは 4 値である」＋ [[IADR-0127]]「状態表示は契約から
導出できる値だけで作る」に従い、`diagramsRetained` / `diagramsCoded` / `hasCorrection` から
**導出**する。`deadLettered` と同じ扱いである。

**縮退したジョブの `status` は `succeeded`** である——図のコード化に失敗して画像保持へ
落ちても、変換そのものは成功している。ここを `failed` と読むと再変換ボタンの出し分けまで狂う。

判定は `jobStatus.ts` の純関数（`hasRetainedFigures` / `isCorrectable`）に置き、描画なしで
試験する。**`isCorrectable` に権限を混ぜない**——混ぜると画面が「対象が無い」と「権限が無い」を
区別できず、[[IADR-0127]] 決定 1 が求める理由の文言を選べなくなる。

## 決定 4: ミューテーションが 2 本になったので `beginOperation()` 形へ移す

[[IADR-0127]] 決定 7。列挙は `Object.values(useConversionJobActions())` で辿る
（手書き配列にすると 3 本目で同じ穴が空く）。
`ConversionJobsPage.tsx` のコメント自身が「**2 本目を足すときは SC-05 / SC-06 の
`beginOperation()` と同じ形へ移すこと**」と予告していた。

## 決定 5: 画像は Blob → オブジェクト URL。`imageUri` を `<img src>` に入れない

`imageUri`（`storage://…`）は**ブラウザから解決できない**うえ、取得には Bearer が要る。
`URL.createObjectURL` で包み、**差し替え・アンマウント時に `revokeObjectURL` する**。
404（コード化済み・未知の図・ストレージ未解決）は区別せず中立に倒す（[[IADR-0009]]）が、
**空白にはしない**——読み込み中と見分けが付かなくなる。

## 結果

- 受け入れ基準 5 点を満たす。hi-fi 対応表 #10 / #12 が「する」になった。
- **Phase 2（変換結果 Markdown 全体の編集）は引き続き射程外**（`05_screens:330`）。
- **デッドレターの内訳表示は束ねなかった**——同じ画面だが別の資源である（[[IADR-0139]]）。

### 変異試験（いずれも復旧後に緑を確認）

| 変異 | 落ちる試験 |
| --- | --- |
| `bffFetch` の blob 分岐を戻す | mutator の試験 ＋ **2 ペインの試験** |
| `correctionsWouldBeLost` を常に `null` | 確認まわり 2 件 |
| `beginOperation()` の `reset` を外す | 決定 7 の試験 |
| 人手補正の管理者ゲートを外す | 運用者側の試験 |

### ★ 変異試験で**自分のテストの穴が 2 つ**出た（テスト側を直してから確定させた）

1. **画像の試験が、本 issue が直した欠陥を素通ししていた。** 当初は「`/image` へ要求が飛んだ」
   ことしか見ておらず、**blob 分岐を戻しても緑のまま**だった。要求は飛ぶが解析層で失敗し、
   右ペインが縮退表示へ倒れるだけだからである。`<img>` まで届いたことを見るよう改めた。
2. **決定 7 の試験が `reset` を試験していなかった。** 同じミューテーションを 2 回叩く形では
   TanStack Query が自分の `isError` を戻すため、**`reset` を外しても緑**だった。
   「**retry が失敗 → correct が成功**」という**別々のミューテーションの組み合わせ**でしか
   この穴は再現しない。

**いずれも「テストが通った」だけでは分からず、変異させて初めて出た。**
新しく書いた試験は、**通ることではなく落ちることを一度確かめる**。

## 申し送り

- **`apiRequest` の `Accept` 既定値（`application/json`）は見直していない。** 本口では
  `Results.File` が内容交渉をしないため**破綻の原因ではない**（原因は `JSON.parse` 側）。
- **jsdom の `Blob` は undici の `Blob` と別コンストラクタである**（実測: `instanceof` が false）。
  バイト列の試験は型名ではなく**実体**で判定する。`URL.createObjectURL` も jsdom は
  実装しないため、描画経路を試験するにはスタブが要る。
