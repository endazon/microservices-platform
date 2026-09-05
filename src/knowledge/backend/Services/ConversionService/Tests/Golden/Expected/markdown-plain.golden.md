# golden: markdown-plain
# Markdown 原本（図なし）。本文が素通しであることと、決定的 DocumentId・本文キーを固定する。
# 原本は読んでいない。入力は変換器出力を模した Markdown である（IADR-0298 決定 2）。

## input
contentType     : text/markdown
originalPath    : /docs/handbook/expense-policy.md
storageUri      : storage://bucket/raw/expense-policy.md
confidentiality : internal

## result
documentId      : 67f9d162-d610-52a8-b9f4-d728afbda04b
markdownKey     : 67f9d162d61052a8b9f4d728afbda04b/document.md
markdownUri     : storage://normalized/67f9d162d61052a8b9f4d728afbda04b/document.md
diagramsCoded   : 0
diagramsRetained: 0
hasBody         : true
markdownLength  : 185
markdownSha256  : 10bc072bfe8f6a433193f2eb8c049e2d9a35d2e0a64d5b37d3dd0543d535e279

## assets
(none)

## diagramCoderCalls
(none)

## figures
(none)

## markdown
--8<-- markdown begin
# 経費精算規程

## 適用範囲

本規程は全社員に適用する。

## 精算の期限

| 区分 | 期限 |
|------|------|
| 国内出張 | 帰着後 5 営業日 |
| 海外出張 | 帰着後 10 営業日 |

- 領収書は原本を添付する。
- 上長の承認を経て経理へ回付する。

```text
申請 → 上長承認 → 経理確認 → 支払
```
--8<-- markdown end
