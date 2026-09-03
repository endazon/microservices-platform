# golden: pdf-text-layer
# テキスト層を持つ PDF 由来と宣言された抽出器出力（図なし・pdftotext のプレーンテキスト相当）。本文が素通しで document.md へ保管され、bodyAbsent が立たないことを固定する（ADR-0070 決定 2）。
# 原本は読んでいない。入力は変換器出力を模した Markdown である（IADR-0298 決定 2）。

## input
contentType     : application/pdf
originalPath    : /docs/manuals/backup-procedure.pdf
storageUri      : storage://bucket/raw/backup-procedure.pdf
confidentiality : internal

## result
documentId      : 829a0315-5fa0-5a66-a27d-275858693186
markdownKey     : 829a03155fa05a66a27d275858693186/document.md
markdownUri     : storage://normalized/829a03155fa05a66a27d275858693186/document.md
diagramsCoded   : 0
diagramsRetained: 0
bodyAbsent      : false
markdownLength  : 77
markdownSha256  : f61be03beadfc10ca1860d8003196552e25679ca1795b6e042b50e0499629a4a

## assets
(none)

## diagramCoderCalls
(none)

## figures
(none)

## markdown
--8<-- markdown begin
バックアップ手順書

1. 対象サーバーの業務停止を告知する。
2. フルバックアップを取得し、取得ログを保管する。

復旧試験は四半期ごとに実施する。
--8<-- markdown end
