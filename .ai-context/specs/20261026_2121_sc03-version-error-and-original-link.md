---
title: 作業仕様書 — SC-03 の版履歴は取得失敗を再試行つきのエラー、0 件を空として描き分け、「原本」は原本の所在（originalUri）だけを指す（#2121）
type: spec
status: done
related_ids: [FR-06, FR-05, SC-03, SC-05, ADR-0031, ADR-0070, IADR-0126, IADR-0135, IADR-0561]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2121"
---

# 作業仕様書 — SC-03 の版履歴の失敗表示と原本リンク（#2121）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。親は #2047（指摘 M-24 の SC-03）。

## 現状（origin/develop で再確認）

- **版履歴**: `DocumentDetailPage.tsx` は `versions.isSuccess && versions.data.length > 0` のときだけパネルを出す。
  取得失敗（BFF は意図して 500 を返す）と 0 件が、どちらも「パネルが無い」になり、見分けが付かない。
  試験（`keeps the document readable when the version history fails`）と画面仕様書（§状態の「版履歴 失敗 → 版履歴パネルを出さない」）がこの形を固定している。
- 計画の共通シェルの状態表示の規約（`05_screens/01_screens.md`。**個別画面の記述より本規約を優先する**）は次のとおり定める。
  - 「空: 0 件は『取得に失敗した』と区別して描く」
  - 「エラー: 失敗は `role="alert"` で示し、再試行ボタンを必ず置く」
  - 「判定順は 失敗 → 待ち → 空 → 本体」
- **原本リンク**: `const sourceUri = content?.sourceUri ?? doc.markdownUri ?? null;`。
  BFF の `DocumentContentDto.SourceUri` には `doc.MarkdownUri`（正規化済み Markdown の `storage://` 参照）が入る（`DocumentBodyProblems.cs`）。
  そのため「原本」は**どちらへ落ちても正規化済み Markdown を指す**。
  原本の所在（`DocumentDto.originalUri`。台帳の `Document.OriginalUri`。IADR-0561）を使うのは SC-05 だけである。
  計画 SC-03 の主要素は「原本（ファイルサーバー等）リンク」で、アクションは「原本リンクで出典元へ遷移」である。

## 方針

- **版履歴**: パネル「バージョン」を常に置き、中身を共通の `QueryState` で描き分ける（判定順は部品が持つ）。
  - 失敗: `role="alert"` ＋「版履歴を取得できませんでした。」＋**再試行**（同じ取得を再実行）。**本文・属性の表示は続ける**（版履歴だけの縮退）。
  - 待ち: 「読み込み中…」。
  - 空（0 件・本文なしの 204 は `okArray` で 0 件になる）: 「版履歴はまだありません。」。
  - 本体: 従来どおり `VersionTable`。
- **原本**: `doc.originalUri` だけを使う。
  - `http(s)` ならリンク（新規タブ・`noopener noreferrer`）、それ以外（`file://`・UNC 等）は等幅表記、無ければ「—」。
  - **`markdownUri` へも `content.sourceUri` へも落とさない**（正規化済み Markdown は原本ではない）。
  - `content.sourceUri` の契約（中身は `markdownUri`）は本件では変えない（他の読み手が居ないことを確かめ、残余として報告する）。
- 試験を反転する（失敗＝エラーと再試行、0 件・204＝空）。原本は `originalUri` を指し、`markdownUri`・`sourceUri` を指さない試験を足す。
- 画面仕様書（§状態の表・§要素の表）とテスト仕様書を改める。
- 初期ロードの床: 足した 2 つの文言の ja / en の訳は入口チャンクのカタログに入るので +0.20 kB 増える。規約の描き分けの文言であり避けられないので、
  `check-chunk-budget --update` で床を上げ、帰属を `scripts/chunk-budget-baseline.json` の `$comment` に残す。

## 受け入れ基準

- [x] 版履歴の取得失敗で `role="alert"` のエラーと再試行が出て、押すと同じ取得をやり直す。本文は読める。修正前は赤（パネルが無い）
- [x] 版履歴 0 件・本文なし（204）で「版履歴はまだありません。」が出て、エラーは出ない
- [x] 「原本」リンクが `originalUri`（http(s)）を指す。`originalUri` が無ければ `markdownUri`・`sourceUri` へ落ちずに「—」。http(s) 以外はリンクにしない。修正前は赤
- [x] E2E（sc03 の smoke）が版履歴 0 件で空の表示になることを確かめる
- [x] 画面仕様書・テスト仕様書を改める

## 母集合（規則 9）

- `git grep -n "sourceUri\|markdownUri\|originalUri"`（frontend）
  - SC-03 の `SourceLinks` → **直す**。
  - SC-05 は既に `originalUri` → 外す。
  - SC-02 の `sourceUri`（出典の `CitationDto.sourceUri`。検索結果の出典の所在）→ 外す（別の契約で、本件の原本リンクではない）。
- `git grep -n "versions.data.length\|versions.isSuccess"` → SC-03 の 1 か所 → 直す。
- `DocumentContentDto.SourceUri` の書き手（BFF `DocumentBodyProblems.cs`。値は `MarkdownUri`）→ 外す（報告）。名前と中身が合っていない。読み手は SC-03 だけだった（本件で読まなくなる）。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
