---
title: 作業仕様書 — SC-07 に人手補正の 2 ペイン編集・「補正あり」標識・再変換の確認を実装する（#651）
type: spec
status: draft
related_ids:
  - FR-12
  - UC-06
  - SC-07
  - IADR-0121
  - IADR-0127
  - IADR-0135
  - IADR-0154
  - IADR-0157
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
related_specs:
  - "./20260810_issue-543_manual-correction-api.md"
  - "./20260805_issue-503_sc05-08-admin-screens.md"
  - "./20260805_issue-501_retry-admin-only.md"
---

# 作業仕様書: SC-07 の人手補正 UI（#651 / Phase 1）

## 起点

- **FR-12**（文書正規化）／**UC-06** 代替フロー／**SC-07**（`05_screens` 2026-08-04 確定）
- 契約は **#543（PR #650・[IADR-0154](../adr/IADR-0154_manual-figure-correction-phase1.md)）で揃っている**。本 issue は**画面側の作業に閉じる**。

## 母集合（着手時に自分でファイルから引いた。issue 本文は転記していない）

**#651 は自分で起票した issue である。**「自分が書いた本文」を母集合の代わりにすると、
起票時の思い込みがそのまま実装へ流れる。**拡張子で絞らず**（`.claude/rules/traceability.md` 規則 3）、
**語の 2 段目フィルタを掛けず**（規則 4）に引き直した。

```console
$ grep -ril 'figure\|correction\|補正' --exclude-dir={node_modules,.git,ai-stock-trading,planning} .
```

→ 130 ファイル。ここから**本 issue が触る資源**（SC-07 の画面と、その応答を読む層）へ落とす。

### 軸 1: 誰がこの口を呼ぶか（画面）

`src/knowledge/frontend/src/features/sc07-conversions/` の 6 ファイル。
`ConversionJobsPage.tsx` / `jobStatus.ts` / `useConversionJobs.ts` と各テスト、`index.tsx`。

### 軸 2: ★ 誰がこの応答を読むか（クライアントの解析層）

**この軸で本 issue の最大の障害が出た。#640 で引き漏らしたのと同じ軸である。**

`GET /bff/conversion/jobs/{id}/figures/{figureId}/image` は**画像のバイト列**を返す。
生成フックの宣言型は `data: Blob` である（`conversion.ts:513`）。ところが**生成コードが通る
唯一の HTTP 出口** `foundation/api/orvalMutator.ts` の `bffFetch` は、**Content-Type を見ずに**
`res.text()` → `JSON.parse(body)` を行う（`orvalMutator.ts:25-26`）。

**実測**（本ワークツリーの Node 22 で実行）:

```console
$ node -e 'const png=Buffer.from("89504e470d0a1a0a0000000d49484452","hex");
           new Response(png,{headers:{"Content-Type":"image/png"}}).text()
             .then(t=>{try{JSON.parse(t)}catch(e){console.log(e.constructor.name,e.message)}})'
SyntaxError Unexpected token '', "PNG\r\n\n   IHDR" is not valid JSON
```

**すなわち生成フック `useBffConversionJobFigureImage` は、生成された時点で実行不能である。**
`#543` は端点と生成物を載せたが、**その応答を読む層は誰も通していない**（画面がまだ無かったため）。

#### 母集合の広さを確かめた —— 本口は openapi 唯一の非 JSON 応答である

「他にも壊れている口があるのでは」を、**語ではなくメディア型で**引いた:

```console
$ grep -n 'application/octet-stream\|image/\|text/plain\|text/markdown\|format: binary' docs/api/openapi.yaml
1268:            image/*:
1269:              schema: { type: string, format: binary }
```

**1 件だけ**であり、それが本口（`.../figures/{figureId}/image`）である。
`IADR-0154` 決定 2 が「`GET /bff/documents/{id}/content` と同じ形」と書いているのは
**「サーバがオブジェクトストレージから解決する」という意味**であって、メディア型ではない
——`/bff/documents/{id}/content` の応答は `application/json`（`DocumentContentDto`）である
（`openapi.yaml:884-888` で確認した）。**「同じ形」という散文を、メディア型が同じだと読むと誤る。**

したがって**先行実装は無い**。解析層を広げるのは本 issue が最初である。

### 軸 3: 同型の先行実装（ADR 本文を開く）

| 引いたもの | 何のために |
| --- | --- |
| [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 7 ＋ `sc06-datasources/DataSourceManagementPage.tsx:56-70` | **`beginOperation()` の実体**。`Object.values(actions)` で列挙し、手書き配列にしない |
| [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 1 ＋ 同 `:78-95` | 権限で消す導線に**理由の文言**を残す形 |
| `sc09-admin-abac`（#640 のタグ削除） | **409 の本文を読んで件数を差し込む**形。`ApiError.body` を使う |
| [IADR-0135](../adr/IADR-0135_generated-client-adoption-and-cache-keys.md) 決定 4 ＋ `foundation/testing/bffResponse.ts` | 画面テストは `apiRequest` を差し替える。`bffFetch` が読む形をヘルパが決める |

**`ConversionJobsPage.tsx:112-115` のコメントが、まさに本 issue のことを予告していた**——
「本画面のミューテーションは retry の 1 本だけ…**2 本目を足すときは SC-05 / SC-06 の
`beginOperation()` と同じ形へ移すこと**」。補正投稿が 2 本目にあたるので、**この移行を行う**。

## 判断

### 判断 1: ★ `bffFetch` を直す（呼び出し側で迂回しない）

**選択肢と採否**:

| 案 | 内容 | 採否 |
| --- | --- | --- |
| A | 画面が `apiRequest` を直接呼び、`res.blob()` する | **却下**。`CLAUDE.md`「手書き HTTP クライアントは禁止」。ESLint も `foundation/api` 以外の `fetch` を止める。何より**生成フックが壊れたまま残る** |
| B | `foundation/api` に `apiBlob()` を足し、生成フックは使わない | **却下**。規約は満たすが、**壊れた生成フックが「使える顔をして」残る**。次に画像口が増えたとき同じ穴を踏む |
| **C** | **`bffFetch` を Content-Type で分岐させる** | **採る**。[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 3 が「SPA から出る HTTP はこの関数 1 箇所に収束させる」と定めた**その 1 箇所**であり、直す場所はここ以外にない（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 参照点を 1 つに畳む） |

**分岐の式**（保守的に置く）:

> Content-Type が**空**または `json` を含むなら従来どおり `text()` → `JSON.parse`。
> **それ以外のときだけ** `blob()` を返す。

「JSON でなければ blob」ではなく「**Content-Type が明示的に非 JSON のときだけ blob**」とする。
ヘッダを持たないモック応答・空ボディが**現行の経路のまま**通ることを保証するためである
（既存 100 超の画面テストが `new Headers()`〔空〕を返している。`bffResponse.ts:20`）。

**非 2xx はここへ到達しない**（`apiRequest` が `ApiError` を投げる）ので、
`application/problem+json` の扱いは本分岐の考慮外である。

### 判断 2: 縮退標識は `status` の 5 値目にしない

`diagramsRetained > 0` から**導出**する。`05_screens:320`「ジョブ状態モデルは 4 値である」
＋ [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md)「状態表示は契約から導出できる値だけで作る」。`deadLettered` と同じ扱いである。
**縮退したジョブの `status` は `succeeded`** である（変換自体は成功している）。

判定は `jobStatus.ts` へ純関数として置き、描画なしで試験する（既存の `jobStatusView` と同じ作法）。

#### ★ 引用の照合で、自分が #543 で撒いた行番号の誤りが出た

この節を書くとき**行番号を実際に開いて確かめた**ところ、**`05_screens:317` は誤り**で、
「ジョブ状態モデルは 4 値である」は **320 行**にあった（317 行は表の区切り行 `| --- |`）。

```console
$ grep -n 'ジョブ状態モデルは 4 値である' planning/projects/microservices-platform/05_screens/01_screens.md
320:  - **ジョブ状態モデルは 4 値である**: `queued`（受付済み・未着手）／…
```

**planning の pin は #543 以降動いていない**（現 pin `2cf0795`。pin を進めた最後のコミットは
#638 の `040edd6` で、#650 より前）。つまり**書いた時点で既に誤っていた**——pin のずれではない。

`:317` を書いた自分の記述を**パスから引き直した**（規則 8）:

| 箇所 | 扱い |
| --- | --- |
| `docs/api/openapi.yaml:2542` | **直す**（`bff.schemas.ts:574` は生成物なので `pnpm run codegen` で追随する） |
| `Knowledge.Contracts/Dtos/ConversionJobDto.cs:31` | **直す** |
| `docs/adr/IADR-0154:134` | **直す**（決定の中身ではなく参照先の誤記。変更履歴に日付つきで残す） |
| `docs/specs/20260810_issue-543_*.md:145` | **直さない。** 確定した過去 PR の作業仕様書であり、**書き換えは記録の改竄にあたる**。誤りは本仕様書のこの節が引き継ぐ |

**独立したコミットに分ける**——画面の実装とは別の資源であり、混ぜると「なぜ契約の
コメントが変わったのか」が PR の差分から読めなくなる。

### 判断 3: 確認ダイアログは **409 を受けてから**出す

**先にダイアログを出して、通ったら投げる、ではない。**
サーバ（`POST /retry`）が `hasCorrection` のジョブを **409 `corrections_would_be_lost`** で止める
（[IADR-0154](../adr/IADR-0154_manual-figure-correction-phase1.md) 決定 4）。画面は**その 409 を受けてから**確認を出し、`correctedFigures` を件数として
示し、確認後に `?discardCorrections=true` で再送する。

理由: `hasCorrection` を画面が読んで先に分岐すると、**一覧を取ってから押すまでの間に補正が入った場合に
無確認で消える**。件数もサーバの値でなければ嘘になる。#640 のタグ削除（`usageCount` つき 409）と同じ形。

### 判断 4: 画像は Blob → オブジェクト URL。`imageUri` を `<img src>` に入れない

`imageUri` は `storage://…` であり**ブラウザから解決できない**。かつ画像取得には Bearer が要るため
素の `<img src="/bff/...">` も使えない（`apiRequest` がヘッダを付ける経路を通らない）。
Blob を `URL.createObjectURL` で包み、**差し替え・アンマウント時に `revokeObjectURL` する**。

### 判断 5: 人手補正は管理者限定。運用者には理由を書く

[IADR-0154](../adr/IADR-0154_manual-figure-correction-phase1.md) 決定 6 で **3 口とも `x-roles: [platform-admin]`**（照会 GET も運用者は 403）。
画面は導線を出さないが、**無言で消さず理由を書く**（[IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 1 の再変換ボタンと同じ）。
**実効境界はサーバ側**であり、ここは表示制御にすぎない（[IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md) 決定 2）。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（issue） | テスト |
| --- | --- | --- |
| 1 | 管理者が 2 ペインを開きコードを投稿して本文が差し替わる | 画面テスト（一覧 → 補正を開く → 投稿 → 成功表示） |
| 2 | 運用者には導線が出ない | 権限別の**対**（管理者に出る／運用者に出ない ＋ 理由の文言） |
| 3 | 補正のあるジョブの再変換で確認を求め、**件数を示す** | 409 `corrections_would_be_lost` → ダイアログに件数 → 確認で `discardCorrections=true` が飛ぶ |
| 4 | 状態・備考・標識が契約から導出される（`status` は 4 値のまま） | `jobStatus.ts` の純関数テスト（4 値は不変・導出は別関数） |
| 5 | ★ **画像（binary）が解析層を通る** | `bffFetch` の単体テスト（`image/png` → Blob／JSON は従来どおり／**ヘッダ無しは従来どおり**） |
| 6 | 直近の操作結果だけが出る | retry 失敗 → 補正成功で**古い失敗バナーが消える**（[IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 7） |

**変異試験**: 判断 1 の分岐を戻すと 5 が落ち、かつ**他が落ちないこと**を実測して記録する
（＝新しい分岐が既存経路を変えていない証跡）。

## 射程外

- **Phase 2**（変換結果 Markdown 全体の編集）。`05_screens:330` が繰り延べている。
- **デッドレターの内訳表示**（契約は #533）。**同じ画面だが別の資源**であり束ねない（[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) 判定単位は資源）。
- **`apiRequest` の `Accept` 既定値の見直し**。本口では `Results.File` が内容交渉をしないため
  **破綻の原因ではない**（原因は `JSON.parse` 側である）。呼び出し側で `Accept` を正しく送るに留める。
