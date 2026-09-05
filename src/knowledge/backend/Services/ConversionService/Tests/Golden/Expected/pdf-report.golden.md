# golden: pdf-report
# PDF 由来と宣言された変換器出力（図 2 件・いずれも画像保持）。機密区分の受け渡しと .svg / 未知拡張子 .bin への写像を固定する。目印を置かず、末尾へ append する経路も固定する。
# 原本は読んでいない。入力は変換器出力を模した Markdown である（IADR-0298 決定 2）。

## input
contentType     : application/pdf
originalPath    : /docs/reports/2026Q1-operations.pdf
storageUri      : storage://bucket/raw/2026Q1-operations.pdf
confidentiality : restricted

## result
documentId      : dc5f8cd0-9721-5fe2-b902-686b48bcb10c
markdownKey     : dc5f8cd097215fe2b902686b48bcb10c/document.md
markdownUri     : storage://normalized/dc5f8cd097215fe2b902686b48bcb10c/document.md
diagramsCoded   : 0
diagramsRetained: 2
hasBody         : true
markdownLength  : 249
markdownSha256  : 8d2bb5b794e92aff0eeda6d39db24b9b0a58ccd58c0c56c87df5b5e6e31f817c

## assets
1) key=dc5f8cd097215fe2b902686b48bcb10c/assets/fig-1.svg uri=storage://normalized/dc5f8cd097215fe2b902686b48bcb10c/assets/fig-1.svg contentType=image/svg+xml bytes=5 sha256=19b8ac52fee937c46dd1c188c08cd16549b10c0855b2e5d09a732271eb20d8f7
2) key=dc5f8cd097215fe2b902686b48bcb10c/assets/fig-2.bin uri=storage://normalized/dc5f8cd097215fe2b902686b48bcb10c/assets/fig-2.bin contentType=image/emf bytes=5 sha256=a1f35353066408407f5e0379ba2ae444a9f62dbf9b922293355a2d26271ab750

## diagramCoderCalls
1) figureId=fig-1 confidentiality=restricted
2) figureId=fig-2 confidentiality=restricted

## figures
1) id=fig-1 coded=false language=(null) code=(null) imageUri=|storage://normalized/dc5f8cd097215fe2b902686b48bcb10c/assets/fig-1.svg| imageContentType=|image/svg+xml| caption=|availability-trend|
2) id=fig-2 coded=false language=(null) code=(null) imageUri=|storage://normalized/dc5f8cd097215fe2b902686b48bcb10c/assets/fig-2.bin| imageContentType=|image/emf| caption=|capacity-plan|

## markdown
--8<-- markdown begin
# 2026 年度第 1 四半期 運用報告

## 稼働率

四半期の稼働率は 99.95% であった。

## 障害

計画外の停止は 1 件（12 分）である。


![fig-1](storage://normalized/dc5f8cd097215fe2b902686b48bcb10c/assets/fig-1.svg)


![fig-2](storage://normalized/dc5f8cd097215fe2b902686b48bcb10c/assets/fig-2.bin)
--8<-- markdown end
