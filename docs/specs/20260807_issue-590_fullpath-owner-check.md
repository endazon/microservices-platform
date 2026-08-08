---
title: 作業仕様書 フルパス形式の owner 誤りを検出する型 4 を check-cross-repo-refs へ追加する（#590）
type: spec
status: done
related_ids: [NFR, IADR-0116, IADR-0140, IADR-0141]
author: Claude
created: 2026-08-07
updated: 2026-08-07
plan_refs: []
related_specs:
  - ../adr/IADR-0140_cross-repo-issue-ref-checker.md
  - ../adr/IADR-0141_audit-rounds-and-population-drawing.md
  - 20260807_issue-507_cross-repo-issue-refs.md
---

# 仕様書: フルパス形式の owner 誤りを検出する型 4 を追加する（#590）

> 本仕様書は実装着手前に作成した。本作業は計画書由来の機能実装ではなく、実装リポジトリの
> 表記規約（`.claude/rules/traceability.md`）とその機械検査の穴を塞ぐ **NFR** の作業である。

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#590**（親 #454）
- 起点 ID: **NFR**
- 関連 IADR: **[[IADR-0140]]**（検査器の新設。本作業はこれに型 4 を足す）／
  **[[IADR-0141]]**（母集合の引き直しを実装側の義務とした）／**[[IADR-0116]]**（1 issue = 1 branch = 1 PR）
- 系譜: #507（型 1・型 2）／#583（型 3）／#576（計画 ID / ADR ID の修飾。**別 PR**）

### 本作業を #576 と束ねない理由

[[IADR-0139]] の束ね例外は**「契約追加」に限定**されており（決定 1 の条件 F・§結果「D 群には効かない」）、
**検査器型の束ねは authorize されていない**。#572 施策 9 が第 2 の束ね軸を提案しているが未裁定である。
したがって [[IADR-0116]] 規約 1 のまま **#590 と #576 は独立した 2 本の PR** とする（利用者裁定済み）。

## 分類（[[IADR-0141]] 決定 4）

**「機械検査を新設・改修する」** —— 検査器 `scripts/check-cross-repo-refs.js` に検出型を追加する。
よってクロス監査は **全面 1 巡 ＋ 是正差分 1 巡**が規定である（記録の追随のみの打ち切りは適用しない）。
分類は件名ではなく差分から決めた（差分の主体は検査器と自己試験である）。

## 母集合の引き直し（[[IADR-0141]] 決定 1）

**走査基準**: `origin/develop` = `7b6232b`（#601 マージ後）。
`git ls-files` から引き、**パスの除外のみ**（`^planning/` と `^src/ai-stock-trading/`）で絞った。
**拡張子で絞らない・行フィルタで継がない**（規則 3・4）。

### 軸と実測値

| 軸 | 走査 | 件数 |
| --- | --- | ---: |
| 軸 1 | フルパス形式で owner が `endazon` 以外（**誤りの側から**） | **1** |
| 軸 2 | 使われている owner 名の全数（既知集合の根拠） | `endazon` **30** / `endodazon` **1** |
| 軸 3 | フルパス形式で参照されている**リポ名**の全数 | 8 種（下表） |
| 軸 4 | 大文字小文字ゆれを含めた再走査 | **+0**（軸 1 と同一の 1 件） |
| 軸 5 | コミット件名・本文（`check-commit-messages.js` 経路）の誤 owner | **0** |
| 軸 6 | URL 形式 `github.com/<owner>/<repo>` の owner | `endazon` **245** / 他 **0** |

**和集合 = 1 件**（是正対象）。

```console
$ git ls-files | grep -vE '^planning/|^src/ai-stock-trading/' \
  | xargs grep -nE "[A-Za-z0-9_.-]+/(project-planning|ai-stock-trading|microservices-platform)#[0-9]+" \
  | grep -vE "(^|[^A-Za-z0-9_.-])endazon/"
docs/specs/20260718_issue-283_ast-frontend-integration.md:36:
  - Issue: MSP #283（本 issue）／ AST endodazon/ai-stock-trading#106（T2）
```

### ★ 集めたファイルの中身を全数で読んで分かったこと

**母集合はファイルの集合ではない。** 軸 3 で挙がった 8 種のリポ名を 1 件ずつ読んだ結果、
**「フルパス形式なら owner は `endazon`」という素朴な規則は成立しない**ことが分かった。
検査を素朴に書くと、以下がすべて偽陽性になる。

| スラッシュの前後に現れる形 | 実体 | 件数 | 型 4 で止めてはならない理由 |
| --- | --- | ---: | --- |
| `anthropics/claude-code-action#723` | **第三者リポジトリ**への正当な参照 | 1 | owner が `endazon` でないのが**正しい** |
| `owner/repo#123` | 書式を説明する**リテラルな見本** | 4 | `check-contract-schema.js` の実装と自己試験・仕様書の本文 |
| `AST#186/AST#192` | `/` は**列挙の区切り**であって owner ではない | 7 | 規約が正とする書き方そのもの |
| `…spa-router-shell.md#2` | Markdown の**アンカーリンク** | 3 | `#` の後ろが数字なだけ |
| `endazon/microservices-platform#56` | **自リポジトリ**への正当なフルパス参照 | 4 | owner は正しい |

**したがって型 4 は「フルパス形式一般」ではなく「自組織が持つ 3 リポジトリ名に限定して owner を検査する」
形で実装する。** 対象リポ名を `project-planning` / `ai-stock-trading` / `microservices-platform` に
固定すれば、上表 5 種はいずれも構造的に掛からない（`claude-code-action` と `repo` はリポ名が違い、
`AST` と `.md` もリポ名ではない）。

### 除外したものとその理由（規則 6）

| 除外 | 件数 | 理由 |
| --- | ---: | --- |
| `endazon/` の正しいフルパス形式 | 30 | 規約が明示的に許す形。**型 4 追加後も通ること**を変異試験の正例で固定する |
| URL 形式 `github.com/endazon/...` | 245 | 誤 owner は **0 件**（軸 6 で実測）。下記「本作業で塞がない穴」に開示する |
| `planning/` `src/ai-stock-trading/` 配下 | — | submodule。本リポジトリの規約の適用外（既存 3 型と同じ扱い） |
| 上表 5 種の非 owner スラッシュ | 19 | owner ではないため型 4 の対象外。**偽陽性を出さないことを負例で固定する** |

## やること

1. **実在違反 1 件の是正** —— `docs/specs/20260718_issue-283_ast-frontend-integration.md:36` の
   `AST endodazon/ai-stock-trading#106` を **`AST#106`** へ。
2. **型 4 の実装** —— `scripts/check-cross-repo-refs.js` に
   「自組織の 3 リポジトリ名を指すフルパス形式で、owner が既知集合（`endazon`）でない」型を追加する。
3. **`--self-test` に正例・負例を対で常設**（`endazon/ai-stock-trading#106` は通り `endodazon/…` は落ちる）。
4. **規約への条文追加** —— `.claude/rules/traceability.md` に owner が `endazon` である旨を明記する
   （現規約は owner の正しさを明文化していないため、検査だけ足すと典拠が無い）。
5. **[[IADR-0140]] へ日付つき追記**（決定は変わらないので新 IADR は不要）。

### 確定済み `docs/specs/` を書き換えてよいか（先に決めた）

`.claude/rules/traceability.md`「この書式を適用する母集合」は、確定済みの `docs/specs/` を
**書き換えない**と定める。ただしこれは「**後から注記を足すこと**」を禁じる条文であり
（記録が当時何を主張していたかを変えてしまうため）、**壊れたポインタの修復は対象外**である。
先例も在る —— `0a70796` は計画 ADR の改名に伴い確定済み `docs/specs/` 内の参照を是正している。
本件は誤字により**存在しない owner へのリンクが描画される**状態であり、是正しても記録の主張は
1 文字も変わらない。よって**是正してよい**と判断する。

## 本作業で塞がない穴（開示）

- **URL 形式（`https://github.com/<owner>/<repo>/...`）の owner は検査しない。** 実測 245 件の
  owner はすべて `endazon` で違反ゼロだが、**同じ誤字が URL 側で起きれば同じく死んだリンクになる**。
  #590 の受け入れ基準は `#NNN` 形式に限定されているため本 PR の射程外とし、ここに開示する。
- **型 4 は「owner が `endazon` でない」ことしか見ない。** リポ名自体の誤字
  （`ai-stock-tradnig` 等）は依然として素通りする（リポ名の集合に一致しないため型 1 にも掛からない）。

## 変異試験の結果（実測）

### (a) 実データでの識別可能な変異

| 段階 | 実測 |
| --- | --- |
| 是正**前**の `node scripts/check-cross-repo-refs.js` | **exit 1**・`[フルパス形式の owner 誤り] endodazon/ai-stock-trading#106 → AST#106` |
| 是正**後** | **exit 0**・`OK: 533 件` |

### (b) 検査器そのものの変異（`KNOWN_OWNERS` に `endodazon` を足す）

| 検査 | 変異なし | **変異注入時** |
| --- | --- | --- |
| `--self-test` | 82 件 all passed | **4 件 FAIL**（ケース名つきで出る） |
| `scripts.test.js`（CI ゲート） | 284 passed | **AssertionError で fail** |

**★ 変異試験がこの PR 自身の欠陥を 1 件見つけた。** 当初 `findViolations(...)[0].suggestion` と
素で書いていたため、**検出が壊れると `TypeError` で落ち、どのケースが失敗したのか出なかった**。
`?.` へ変えて**名前つきの FAIL** になるよう直した（既存ケースは同じ書き方のままで、本 PR の射程外）。

### (c) 通ることを確かめた形（型 4 を足したせいで落ちてはならない）

`endazon/ai-stock-trading#106`（規約が許すフルパス形式）／`anthropics/claude-code-action#723`
（第三者リポジトリ）／`owner/repo#123`（書式の見本）／`AST#186/AST#192`（列挙の区切り）／
`….md#2`（アンカー）—— **いずれも exit 0**。

### (d) 素通りする形（開示）

| 形 | 実測 | 理由 |
| --- | --- | --- |
| `https://github.com/endodazon/project-planning#50` | exit 0 | **URL 形式は射程外**（上記「本作業で塞がない穴」） |
| `endazon/ai-stock-tradnig#106` | exit 0 | **リポ名自体の誤字**。リポ名の集合に一致しないため型 1 にも型 4 にも掛からない |

### (e) 正確を期すための注記

`Endazon/ai-stock-trading#106`（先頭大文字）も **exit 1 になる**。ただしこれは
**死んだリンクだからではない** —— GitHub の owner 名は大文字小文字を区別しないため、この形は
リンクとしては機能する。**表記を 1 つに保つ**という規約（「短縮形とフルパス形式を混在させない」
と同じ趣旨）に照らして止めており、**害の種類が他の変異とは違う**。他人の数え・他人の判定を
そのまま転記しないのと同じ理由で、ここを「死んだリンクを 4 件検出した」とまとめない。

## 受け入れ基準（#590 より。テストへの写像）

- [x] 実在の 1 件が是正されている（`docs/specs/20260718_issue-283_…:36` → `AST#106`）
- [x] 誤 owner を含む `.md` を渡すと **exit 1**、正しい owner なら **exit 0** であることを実測している（上記 (a)）
- [x] `--self-test` が正例・負例を**対で**持つ（正例 7 ＋ 負例 7。82 件 all passed）
- [x] 規約（`.claude/rules/traceability.md`）に owner の条文がある
- [x] 変異試験の結果（素通りしたものを含む）が開示されている（上記 (b)〜(e)）
- [x] **CI ゲート**（`scripts.repo.test.js` の実バイナリ経路）が型 4 を検査している

## 検証

```
node scripts/check-cross-repo-refs.js --self-test
node scripts/check-cross-repo-refs.js
node scripts/check-doc-links.js
node scripts/check-test-traceability.js
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
```
