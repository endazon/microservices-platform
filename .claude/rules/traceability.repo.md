# トレーサビリティ規約（本リポジトリ固有）

`traceability.md`（キット配布物）を補う **microservices-platform 固有**の規範を置く。配布物は直接編集しない。同ディレクトリの `*.md` は自動適用される（[[IADR-0201]]）。

## 起点 ID の種別（固有）

- 裸の ID は **MSP** を指す。レンジは `FR-01..22` / `UC-01..11` / `SC-01..21` / `ADR-0001..0047`（**欠番なし**。**走査基準: planning `767a9d48`**。引き直しの記録は別紙 [`plan-id-range-history-annex.md`](../../docs/how-to/plan-id-range-history-annex.md)。**世代数は書かない**——別紙が増えるたびに腐る導出値である）。
- **`Proposed` でも ID としては実在する**（[[IADR-0119]] 決定 2）。
- **着手条件は FR 単位で読む。** 範囲の正は計画 `ADR-0037` の「着手可否の注記」であり**ここへ転記しない**（[[IADR-0142]]）。
- **CI は計画 ADR の実在性を守っていない**（別紙 §3）。
- **`NFR` の採番は `NFR-01`〜`NFR-27`**（内訳は別紙 §4）。**実在性は検査されない**（書き手が守る）。既存の無採番 `NFR` は遡及書き換えしない。**メタ作業（規約・検査器・文書統制）は代表例で、製品の作業にも当たる番号が無いことはある**（#716）。**無いことは「実装側で作ってよい」ではない**（[[IADR-0179]] 決定 2）。

## 複数プロジェクトを跨ぐ場合の ID 修飾（固有設定）

- **計画 ID の `<PROJ>`**: ai-stock-trading = `AST`（`AST/FR-17`）。AST の採番は `FR-01..21` / `UC-01..07` / `SC-01..03`（pin `767a9d48`）。
- `check-plan-id-qualification.js`（#576）の対象は追跡下の全ファイル（submodule・`CHANGELOG.md`・`docs/specs/`・`feedback/` を除く）。「AST 文脈で裸の ID」と列挙の後続 ID は検出しない（人と AI が守る）。
- **issue / PR 番号は短縮形に寄せる**: `AST#NNN` / `planning#NNN`。フルパス形式は自動リンクが要る箇所だけ。**列挙形でも各番号を修飾する**。**Markdown の明示リンクもテキストは短縮形**（#507）。**修飾語と番号の間に空白を入れない**（誤: `AST #24`。自リポを指す `MSP #266` は裸でよい）。**フルパス形式の owner は `endazon` ただ 1 つ**（#590。第三者リポは除く）。経緯は別紙 [`cross-project-id-refs-annex.md`](../../docs/how-to/cross-project-id-refs-annex.md)。

### Superseded / Deprecated な ADR を引用するときの書式（#580）

- **旧 ID を残し、後継を併記する。ID を後継へ付け替えてはならない。** frontmatter の ID リスト: 旧 ID を残し**後継 ID を項目として併記**（説明を混ぜない）。散文・コード / 設定のコメント: `ADR-0003（Superseded by ADR-0027）`。
- **注記そのものへ起票 ID を書き**、`updated:` を前進させる（決定を変える追記は日付つき追記ブロック `［YYYY-MM-DD 追記 / #NNN］`）。**後継 ID は旧 ID の隣に置く。**
- 適用先は **live な権威文書とコード**（`docs/adr/` に限らない）。確定済みの `docs/specs/`・`docs/superpowers/` は**書き換えない**（作業中の PR の仕様書は別）。「書き換えない」の対象は本文への後付け注記である。frontmatter の状態欄は対象外（#717 / [[IADR-0191]] 決定 2）。
- **凍結の射程は記録種ごとに違う**（planning#387 / [[IADR-0166]] 決定 2 の 2026-08-17 追記が正本）。①＝状態欄を本文で言い直した追記。**`feedback/` は①だけ不可**（トリアージ結果・裁定・自己是正は残す。planning#369）／**`docs/specs/` は `［YYYY-MM-DD 追記 / #NNN］` 書式の経過追記が可**／**`docs/superpowers/` は不可**。
- **機械検査は置いていない**（別紙 [`adr-supersede-citation-annex.md`](../../docs/how-to/adr-supersede-citation-annex.md)）。

## 是正・追随の母集合の取り方（固有の追加）

- キットの規則 1〜8 に加え **規則 9・10**（旧 7・8）を持つ。**9**: **「追随する文書」を記憶で挙げない。誤りの側の文字列で全文書を走査してから挙げる（規則 2 と併用）**。**10**: **是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す。是正前の語で引いても捕まらない**。導出値は**走査ではなく計算し直す**。
- 破れた実例は 1 箇所にしか置かない —— 規則 1〜6 は [[IADR-0141]] 決定 1、9・10 は別紙 [`population-drawing-annex.md`](../../docs/how-to/population-drawing-annex.md)。
- issue 本文の「反映先」は母集合ではない。他人の数えを検証せず転記しない。**機械検査は無い。**
- **着手前に母集合を自分で引き、結果と除外理由を作業仕様書へ書く。**

## コミットメッセージの機械チェック（固有）

- **FR / UC / SC の実在性**（#579）: スコープの ID が上のレンジに実在することを検査する（パーサは `check-test-traceability.js` と共用。別紙 [`commit-message-rules-annex.md`](../../docs/how-to/commit-message-rules-annex.md) §実在性検査）。
- PR タイトル検査・除外する自動コミットは同別紙 `commit-message-rules-annex.md`、CHANGELOG 補正は別紙 [`changelog-overrides-annex.md`](../../docs/how-to/changelog-overrides-annex.md)。

### 検査対象から除外する自動コミット

キット配布物（`traceability.md`）が pin 179a69a の別紙化で本見出しを持たなくなったため、**確定済み記録が節名で引く本見出しをここで保持する**（#686 段 1 / #869）。内容（bot 著者・マージコミット・Revert・`[skip ci]` の除外、`BOT_AUTHORS` の更新規則）は別紙 [`commit-message-rules-annex.md`](../../docs/how-to/commit-message-rules-annex.md) を参照。
