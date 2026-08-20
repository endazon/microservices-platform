---
title: 作業仕様書 他リポジトリ（AST）の計画 ID / ADR ID の修飾を規約書式へ揃え、機械検査へ載せる（#576）
type: spec
status: done
related_ids: [NFR, IADR-0116, IADR-0140, IADR-0141]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs: []
related_specs:
  - ../adr/IADR-0140_cross-repo-issue-ref-checker.md
  - ../adr/IADR-0141_audit-rounds-and-population-drawing.md
  - 20260807_issue-590_fullpath-owner-check.md
  - 20260807_issue-570_ast-project-rename.md
---

# 仕様書: AST の計画 ID / ADR ID の修飾を規約書式へ揃える（#576）

> 本仕様書は実装着手前に作成した。**#590（issue / PR 番号の表記）とは別物**である ——
> こちらは **計画 ID / ADR ID**（`AST/FR-17`）の修飾で、`.claude/rules/traceability.md` の
> 別々の節が定めている。

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#576**（親 #454）／起点 ID: **NFR**
- 発見: **#570 のマージ前クロス監査**（traceability-auditor・2026-08-07）
- 別物: **#507 / #590**（issue / PR 番号の表記。**重複起票ではない**）
- 規約: `.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合の ID 修飾」
  「本リポジトリでの名前空間（固有設定）」

### #590 と束ねない理由

[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) の束ね例外は**「契約追加」に限定**されており、検査器型の束ねは authorize されて
いない。[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1 のまま **2 本の独立した PR** とする（利用者裁定済み）。

## 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4）

**「機械検査を新設・改修する ＋ 表記を是正する」** —— クロス監査は **全面 1 巡 ＋ 是正差分 1 巡**。
分類は件名ではなく差分から決めた。

## 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**走査基準**: `origin/develop` = **`296b4f6`**（#597 / #603 マージ後。`33acb17` で取り込み済み）。
着手時は `7b6232b` で測ったが、`7b6232b..296b4f6` を再走して**型 A の新規混入 0 件**を確認した。`git ls-files` から引き、**パスの除外のみ**
（`^planning/` `^src/ai-stock-trading/`）。**拡張子で絞らない・行フィルタで継がない**。

### 軸と実測値

| 軸 | 走査 | 実測 |
| --- | --- | ---: |
| 軸 1 | **型 A**: 空白形 `AST <ID>`（誤りの側から） | **28 occurrence / 14 ファイル** |
| 軸 2 | 正しい `AST/<ID>` 形（**壊してはならない側**） | **154** |
| 軸 3 | 空白形が現れるファイルの**置き場所別**内訳 | `docs/adr` 8・`docs/specs` 3・`deploy` 2・`CHANGELOG.md` 1 |
| 軸 4 | **型 B**: AST 文脈で裸の計画 ID（issue が名指しした 6 箇所） | **6**（実在を確認） |
| 軸 5 | 型 B を**近傍規則で引き直す**（`AST` を含む行の裸 ID・コード/設定も） | **12 以上** ← ★**引き切れていなかった**（下記） |
| 軸 7 | **型 A の区切りに記号が挟まる形**（`AST [[IADR-0080]]` / TAB / バッククォート） | **2** ← ★**当初の軸に無かった** |
| 軸 6 | コミット件名・本文 | **未走査**（shallow clone。下記の限界） |

### ★ issue 本文の「12 箇所」「6 箇所」はどちらも母集合ではない

**#590 に続いて 2 度目である。**

| issue の記載 | 実測 | 差 |
| --- | ---: | ---: |
| 型 A（空白形）「12 箇所」 | **28** | **+16** |
| 型 B（裸の AST 計画 ID）「6 箇所」 | **12 以上** | **+6 以上** |

型 B の引き漏らしは **`deploy/` の SC-02 / SC-03 参照**である。issue は
`deploy/docker-compose.yml:486` と `values.yaml:338`（`FR-17, UC-06`）だけを挙げているが、
**同じファイルの `:514` `:538`（`AST リスク設定（SC-02）・統制状態参照（SC-03）`）と
`values.yaml:353` `:370` も同型**であり、`deploy/keycloak/microservices-platform-realm.json`
の `:16`（`AST FR-17/UC-06`）・`:274`（`AST FR-08`）も入る。

## 3 択の仕分け（#601 の教訓を適用。「直す / 直さない」の 2 択にしない）

| 置き場所 | 判定 | 理由 |
| --- | --- | --- |
| **live な `docs/adr/` 8 本** | **A. 直す** | 権威文書。表記が割れていると監査の突合が揺れる |
| **`deploy/` 3 ファイル**（compose / values / realm.json） | **A. 直す** | 設定のコメント。規約は「コード内コメント」を適用先に挙げている |
| **`src/platform/backend/…/BffTestFactory.cs`** | **A. 直す** | 同上（型 B。`SC-01` が MSP の SC-01＝検索/チャットへ誤帰属する） |
| **確定済み `docs/specs/` 3 本** | **C. 触らない** | 一時点の記録。**ただし #590 で確立したとおり「壊れたポインタの修復」は別** —— 本件は誤帰属であってリンク切れではないので**触らない** |
| **`CHANGELOG.md`** | **B. 生成物なので直接は触らない** | `scripts/gen-changelog.js` が再生成する。是正するなら `changelog-overrides.json` の `remap`（履歴は不変・生成物のみ是正） |
| **`.claude/rules/traceability.md` の説明文** | **C. 触らない** | `AST の FR-17` は**規約自身が誤帰属を説明している地の文**であり、修飾すると説明が成立しない |

### `CHANGELOG.md` をどう扱うか（先に決めた）

CHANGELOG の 3 行（`AST SC-02/03 の …`・`AST 監視銘柄（SC-02 watchlist）…`・
`AST 由来の SC-02/SC-03 参照…`）は**過去コミットの件名そのもの**である。
規約は「**既存履歴は不変**。CHANGELOG 生成時の `changelog-overrides.json`（`remap`）または
今後の編集で生成物 / 本文のみ是正する」と定める。
**本 PR では remap しない** —— remap は「誤った起点 ID」を直す道具であり、
`AST SC-02` は**誤りではなく修飾書式が古いだけ**で、件名の意味は正しい。
`desc` を書き換えると過去の件名と CHANGELOG が食い違い、追跡可能性がむしろ落ちる。
**この判断を除外理由として明記する**（黙って除外しない。規則 6）。

## やること

1. **型 A（空白形 28 → `AST/`）の是正** —— live な `docs/adr/` 8 本と `deploy/` 2 ファイル。
2. **型 B（裸の AST 計画 ID）の是正** —— `deploy/` 3 ファイル ＋ `BffTestFactory.cs` 4 箇所。
3. **機械検査の新設** —— 型 A（空白形）を検出する。`.github/workflows/` は編集不可なので
   **既存の呼び出し口から到達できる形**で実装する（[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 決定 2 と同じ結線）。
4. **`--self-test` に正例・負例を対で常設**（`AST/FR-17` は通り、`AST FR-17` は落ちる）。
5. **変異試験**で「壊すと落ちる」ことを実測し、**素通りするもの・誤検出し得るものを開示する**。

### 型 B を機械で検出するか（結論: しない。理由を開示する）

issue は「同じコメント塊が `AST` を含むのに ID が裸」といった**近傍規則**を提案している。
**採らない。** 実測すると偽陽性が避けられない ——
`.claude/rules/traceability.md:104` の「AST の `FR-17`（当時 MSP は FR-15 まで）」は
**規約が誤帰属を説明している地の文**であり、近傍規則では止まってしまう。
`docs/adr/IADR-0071` のように **MSP の ID と AST の ID が同じ段落に混在する**文書も多い。
**偽陽性を 1 件でも出すと検査は外される**（[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 決定 3 が同じ理由で裸 `#NNN` の
一律検出を棄却している）。

したがって **型 B は今回の是正で 0 件にするが、機械では守らない。**
**これは #590 の「検出しないことを明記する」と同じ作法である。**

## 本作業で塞がない穴（開示）

- **型 B（AST 文脈の裸 ID）は機械検査を持たない**（上記）。再混入は人と AI が防ぐ。
- **軸 6（コミット件名・本文）を走査していない** —— この作業ツリーは **shallow clone**
  （`git rev-list --all --count` = 65 に対し真値 342。[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 決定 7）。
  #590 と同じ経路で `CHANGELOG.md`（全履歴から生成）を代替に用いる。
- **`CHANGELOG.md` の 3 行は残る**（上記の判断）。

## ★ 是正そのものが新しい欠陥を 3 種つくった（実測）

### (1) 一括置換が列挙の後続 ID を未修飾のまま残した —— 4 件

`AST FR-17/UC-06` を機械的に `AST/` へ寄せると **`AST/FR-17/UC-06`** になる。
**後続の `UC-06` は裸のまま**で、MSP の UC-06 へ誤帰属する —— 是正が新しい誤帰属を作った。

| ファイル | 置換直後 | 是正後 |
| --- | --- | --- |
| `deploy/keycloak/…realm.json:16` | `AST/FR-17/UC-06` | `AST/FR-17・AST/UC-06` |
| `docs/adr/IADR-0071:2` / `README.md:127` | `AST/SC-02/SC-03` | `AST/SC-02・AST/SC-03` |
| `docs/adr/IADR-0071:136` | `AST/SC-02/03` | `AST/SC-02・AST/SC-03` |

**正しい形はリポジトリ内に既にあった** —— `BffTestFactory.cs` 等が `AST/SC-02/AST/SC-03` と
書いている。**「動く形を書く前に既存パターンを探す」**を怠ると同じ型を踏む。

### (2) 検査器のフィクスチャが検査器自身に引っかかった

本検査は `.md` に限らず**追跡下の全ファイル**を走査するため、`scripts.repo.test.js` に
フィクスチャをリテラルで書いた結果、**そのファイル自身が違反として上がった**（実測 2 件）。
[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 決定 4 が「`.js` へ広げると検査器の自己試験文字列で必ず落ちる」と述べていた当の型である。
**除外リストは作らず**、文字列連結で組み立てて解決した（`check-cross-repo-refs` の repo テストと同じ定石）。

### (3) ★ 検査器が自分自身に落ちたのを、ローカルでは検出できなかった

(2) を直したあとも**検査器のソース自身**（ヘッダの説明と自己試験のフィクスチャ）が残っており、
**CI で初めて発火した**。ローカルで緑だった理由は単純である ——

> **新設直後のファイルは untracked なので `git ls-files` に載らない。**
> **コミットして追跡下に入った瞬間に、初めて自分を走査した。**

**「ローカルで緑」は「CI で緑」を意味しない**——走査対象を `git ls-files` から引く検査器は、
**自分自身がまだ追跡されていない状態**で書かれるため、この死角が構造的に生じる。

`__filename` から自分のパスを導出して除外した。**除外リストではないので腐らない**
（ファイル名を変えても追随する）。自己除外が外れないことを `--self-test` で常設した。

## ★★ クロス監査と AI レビューが、受け入れ基準 2 件の「達成」を偽と判定した

**私は `[x]` を 2 つ付けたが、どちらも事実ではなかった。** 母集合の引き方が 2 通りに破れている。

### (A) 型 A の軸が 1 本しか無く、`AST [[IADR-0080]]` 形が丸ごと落ちた —— 2 件

走査式が「`AST` ＋ 空白 ＋ **ID 直結**」しか見ておらず、wiki リンク括弧が挟まる形を拾えなかった。

| 箇所 | 残っていた形 |
| --- | --- |
| `docs/adr/IADR-0070_…:30` | `AST [[IADR-0080]]` |
| `docs/adr/IADR-0071_…:31` | `AST [[IADR-0084]]` |

**これは表記ゆれではなく生きた誤帰属だった** —— 本リポジトリに `IADR-0080_headlamp-k8s-management-ui.md`
と `IADR-0084_headlamp-oidc-apiserver-flags.md` が**実在**し、wiki リンクが**そちらへ解決していた**。
`check-doc-links.js` は `[[...]]` を見ないのでリンクは実在扱いになり、誰も止めない。

**引き漏らしの動かぬ証拠**: `IADR-0071:31` は**同じ行**で `AST IADR-0086` → `AST/IADR-0086` を
直しながら、**3 文字左の `AST [[IADR-0084]]` を残した**。目視で気づけない位置ではない。
**走査式が拾わなかったから直らなかった** —— [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 規則 2・5 の違反である。

### (B) 型 B の軸が「issue が名指しした箇所 ＋ `deploy/` の近傍」で止まっていた —— 32 occurrence

`docs/adr/` と `src/` を型 B の軸で引き直していなかった。しかも**同型の並びを跨いで割れていた**:

- `realm.json:274` の `AST FR-08` は直したのに、`IADR-0075:30` の**同じ AST の FR-08** は裸のまま
- `BffTestFactory.cs` の `SC-01` 4 箇所は直したのに、**隣接ファイル** `BffEndpointCompositionTests.cs:58`
  は**一行の中で `SC-01` だけ裸・`AST/SC-02` / `AST/SC-03` は修飾**という状態
- `IADR-0071` は frontmatter を直して**見出し（`:20`）を直していない**

**是正した**（`IADR-0071` / `IADR-0072` / `IADR-0075` / `features/index.ts` / `BffEndpointCompositionTests.cs`）。
そのうえで**型 A の検査器を記号挟み・TAB へ広げ**、wiki リンク形を正例として常設した。

> **教訓**: 「置換した」は「置換された」ことを意味しない —— に加えて、
> **「走査式が返さなかった」は「存在しない」ことを意味しない。**
> 母集合は**引き方の設計**であり、軸を 1 本にした時点で結論は決まってしまう。

## 変異試験の結果（実測）

| 段階 | 実測 |
| --- | --- |
| 是正**前**（空白形 28 件） | **exit 1** |
| 是正**後** | **exit 0**（`OK: 1173 件`） |
| 空白形を 1 つ戻す変異 | **exit 1**・`deploy/docker-compose.yml:489 [空白区切りの ID 修飾] AST IADR-0048 → AST/IADR-0048` |
| `--self-test` | **35 件 all passed**（内訳は書かない —— 内訳と合計を 2 箇所に持つと片方が古くなる。#590 の教訓） |
| CI ゲート（`scripts.test.js`） | **288 tests passed**（本検査の 3 テストを含む） |

**自己試験を書いたとき、私は負例の主張を 1 件取り違えた** —— `src/AST FR-17` を「検出する」と
書いて落ちた。実挙動（直前が `/` なら後読みで除外）が正しく、`check-cross-repo-refs.js` の
owner 判定とも揃っている。**テストが実装ではなく私の思い込みを正した。**

## 受け入れ基準（#576 より）

- [x] 数えた母集合と検索式が仕様書に残っている
- [x] 空白形 0 件（**★ 当初 2 件残っていた**。下記「監査が見つけた 2 件」）
- [x] 裸の AST ID が 0 件（**★ 当初 32 occurrence 残っていた**。下記）
- [x] 検査が CI の既存呼び出し口から走り、`--self-test` を持つ
- [x] 変異試験の結果（素通り含む）が開示されている

## 検証

```
node scripts/check-cross-repo-refs.js --self-test
node scripts/check-doc-links.js
node scripts/check-test-traceability.js
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
```
