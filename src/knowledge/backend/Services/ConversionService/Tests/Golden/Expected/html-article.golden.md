# golden: html-article
# HTML 原本（図 1 件・コード化不能で画像保持）。画像埋め込みの綴りと資産キー（.png）、および目印（figure:fig-N）が本文中の元の位置で最終参照へ替わることを固定する。
# 原本は読んでいない。入力は変換器出力を模した Markdown である（IADR-0298 決定 2）。

## input
contentType     : text/html
originalPath    : /wiki/onboarding/index.html
storageUri      : storage://bucket/raw/onboarding.html
confidentiality : internal

## result
documentId      : 4ee96c10-a85a-51e2-9d18-92ae84e92871
markdownKey     : 4ee96c10a85a51e29d1892ae84e92871/document.md
markdownUri     : storage://normalized/4ee96c10a85a51e29d1892ae84e92871/document.md
diagramsCoded   : 0
diagramsRetained: 1
bodyAbsent      : false
markdownLength  : 181
markdownSha256  : cc89497387957c3192cf17535136fec691b263d7166e03ca57266fc4502fd352

## assets
1) key=4ee96c10a85a51e29d1892ae84e92871/assets/fig-1.png uri=storage://normalized/4ee96c10a85a51e29d1892ae84e92871/assets/fig-1.png contentType=image/png bytes=5 sha256=19b8ac52fee937c46dd1c188c08cd16549b10c0855b2e5d09a732271eb20d8f7

## diagramCoderCalls
1) figureId=fig-1 confidentiality=internal

## figures
1) id=fig-1 coded=false language=(null) code=(null) imageUri=|storage://normalized/4ee96c10a85a51e29d1892ae84e92871/assets/fig-1.png| imageContentType=|image/png| caption=|onboarding-flow|

## markdown
--8<-- markdown begin
# 入社手続きの流れ

入社日までに以下を完了すること。

1.  アカウント発行を申請する
2.  貸与端末を受け取る
3.  セキュリティ研修を受講する

![fig-1](storage://normalized/4ee96c10a85a51e29d1892ae84e92871/assets/fig-1.png)

> 研修の受講記録は人事が保管する。
--8<-- markdown end
