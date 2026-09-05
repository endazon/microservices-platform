# golden: pdf-no-text-layer
# テキスト層を持たない PDF（スキャン相当）由来と宣言された抽出器出力（空）。本文なしで完了し（hasBody=false）、空の document.md が保管され、図も資産も作らないことを固定する（ADR-0070 決定 3）。
# 原本は読んでいない。入力は変換器出力を模した Markdown である（IADR-0298 決定 2）。

## input
contentType     : application/pdf
originalPath    : /docs/scans/signed-contract-2026.pdf
storageUri      : storage://bucket/raw/signed-contract-2026.pdf
confidentiality : confidential

## result
documentId      : f16792bd-c235-5ddc-b74b-6a917b40e360
markdownKey     : f16792bdc2355ddcb74b6a917b40e360/document.md
markdownUri     : storage://normalized/f16792bdc2355ddcb74b6a917b40e360/document.md
diagramsCoded   : 0
diagramsRetained: 0
hasBody         : false
markdownLength  : 0
markdownSha256  : e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855

## assets
(none)

## diagramCoderCalls
(none)

## figures
(none)

## markdown
--8<-- markdown begin

--8<-- markdown end
