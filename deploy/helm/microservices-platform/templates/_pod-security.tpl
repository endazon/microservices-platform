{{- /*
NFR, IADR-0583（Refs GHSA-j4qv-2w99-xg5p）: Pod とコンテナのセキュリティ設定（多層防御・最小権限）の単一情報源。

- msp.podSecurityContext.restricted <uid> | (dict "uid" <uid> "gid" <gid>)
                                            : チャートが作るイメージ（.NET・SPA）と、利用者 ID が分かっている
                                              汎用イメージ（合成監視・ドリフト検出）。Pod Security Standards の
                                              restricted を満たす（非 root・seccomp RuntimeDefault）。
                                              gid を省くと uid と同じ値（イメージの主グループが uid と同じ番号のとき）。
                                              違うイメージ（curl の curl_user＝100:101）は dict で gid を渡す。
- msp.containerSecurityContext.restricted   : 権限昇格なし・ケーパビリティを全て落とす。
- msp.podSecurityContext.thirdParty         : 外部のイメージ（SeaweedFS・Wiki.js・TEI）。利用者 ID とデータの所有者を
                                              変えずに付けられる分だけ（seccomp RuntimeDefault）。
- msp.containerSecurityContext.thirdParty   : 権限昇格なし。

🔴 uid・gid は 0 や空を受け付けない（描画で止める）。runAsNonRoot と矛盾する値で黙って描かない。
*/}}
{{- define "msp.podSecurityContext.restricted" -}}
{{- $in := . -}}
{{- $rawGid := . -}}
{{- if kindIs "map" . -}}
{{- $in = .uid -}}
{{- $rawGid = .uid -}}
{{- if not (kindIs "invalid" .gid) -}}
{{- $rawGid = .gid -}}
{{- end -}}
{{- end -}}
{{- $uid := int (default 0 $in) -}}
{{- if le $uid 0 -}}
{{- fail (printf "Pod の runAsUser は 1 以上の整数で与えてください（受け取った値: %v。IADR-0583）" $in) -}}
{{- end -}}
{{- $gid := int (default 0 $rawGid) -}}
{{- if le $gid 0 -}}
{{- fail (printf "Pod の runAsGroup は 1 以上の整数で与えてください（受け取った値: %v。IADR-0583）" $rawGid) -}}
{{- end -}}
securityContext:
  runAsNonRoot: true
  runAsUser: {{ $uid }}
  runAsGroup: {{ $gid }}
  seccompProfile:
    type: RuntimeDefault
{{- end }}

{{- define "msp.containerSecurityContext.restricted" -}}
securityContext:
  allowPrivilegeEscalation: false
  capabilities:
    drop: ["ALL"]
{{- end }}

{{- define "msp.podSecurityContext.thirdParty" -}}
securityContext:
  seccompProfile:
    type: RuntimeDefault
{{- end }}

{{- define "msp.containerSecurityContext.thirdParty" -}}
securityContext:
  allowPrivilegeEscalation: false
{{- end }}
