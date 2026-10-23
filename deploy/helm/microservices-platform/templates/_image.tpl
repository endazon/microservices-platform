{{/*
NFR-20, ADR-0007, IADR-0605 (#2132): 自製イメージ（services の全 Deployment と frontend）のタグを決める。
  引数: dict "root" $ "tag" <サービスごとの上書き（無ければ nil）> "what" <エラーに出す名前>
  - サービスごとの上書きが無ければ global.image.tag を使う。
  - 🔴 どちらも空なら描画を落とす（required）。`latest` へ黙って倒さない —— 浮動のタグと IfNotPresent の組では
    版の固定もロールバックも成り立たない（同じタグの中身が替わり、ノードのキャッシュが古い中身を配り続ける）。
  - `latest` を明示しても落とす（固定にならない）。
  - 文字列でない値（`--set` で数字だけの sha を渡すと int になる）は落とす。`--set-string` で渡す。
  - Docker のタグの文字種（[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}）に合わない値は落とす（描いても pull で落ちる）。
*/}}
{{- define "msp.selfBuiltImageTag" -}}
{{- $tag := .tag | default .root.Values.global.image.tag -}}
{{- if and $tag (not (kindIs "string" $tag)) -}}
{{- fail (printf "%s のイメージのタグが文字列でない（%v）。--set ではなく --set-string で渡す（数字だけの sha は数として読まれる。IADR-0605）" .what $tag) -}}
{{- end -}}
{{- $tag = required (printf "%s のイメージのタグが無い。ビルドした版の不変のタグ（git の sha）を --set-string global.image.tag=<sha> で渡す（latest へは倒さない。NFR-20 / IADR-0605）" .what) $tag -}}
{{- if eq $tag "latest" -}}
{{- fail (printf "%s のイメージのタグに latest は使えない。不変のタグ（git の sha）を渡す（NFR-20 / IADR-0605）" .what) -}}
{{- end -}}
{{- if not (regexMatch "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$" $tag) -}}
{{- fail (printf "%s のイメージのタグ %q は Docker のタグの形でない（[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}。IADR-0605）" .what $tag) -}}
{{- end -}}
{{- $tag -}}
{{- end -}}
