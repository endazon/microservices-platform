# golden: html-article
# HTML 原本（図 1 件・コード化不能で画像保持）。画像埋め込みの綴りと資産キー（.png）を固定する。
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
markdownLength  : 182
markdownSha256  : e0966ff21fc577a7036aafb484d5cdbbfc0612a2448584f5a6fd518c24f75c47

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

> 研修の受講記録は人事が保管する。


![fig-1](storage://normalized/4ee96c10a85a51e29d1892ae84e92871/assets/fig-1.png)
--8<-- markdown end
