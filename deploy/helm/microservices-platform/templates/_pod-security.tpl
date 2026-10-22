{{- /*
NFR, IADR-0582（Refs GHSA-j4qv-2w99-xg5p）: Pod とコンテナのセキュリティ設定（多層防御・最小権限）の単一情報源。

- msp.podSecurityContext.restricted <uid>   : チャートが作るイメージ（.NET・SPA）と、利用者 ID が分かっている
                                              汎用イメージ（合成監視・ドリフト検出）。Pod Security Standards の
                                              restricted を満たす（非 root・seccomp RuntimeDefault）。
- msp.containerSecurityContext.restricted   : 権限昇格なし・ケーパビリティを全て落とす。
- msp.podSecurityContext.thirdParty         : 外部のイメージ（SeaweedFS・Wiki.js・TEI）。利用者 ID とデータの所有者を
                                              変えずに付けられる分だけ（seccomp RuntimeDefault）。
- msp.containerSecurityContext.thirdParty   : 権限昇格なし。

🔴 uid は 0 や空を受け付けない（描画で止める）。runAsNonRoot と矛盾する値で黙って描かない。
*/}}
{{- define "msp.podSecurityContext.restricted" -}}
{{- $uid := int (default 0 .) -}}
{{- if le $uid 0 -}}
{{- fail (printf "Pod の runAsUser は 1 以上の整数で与えてください（受け取った値: %v。IADR-0582）" .) -}}
{{- end -}}
securityContext:
  runAsNonRoot: true
  runAsUser: {{ $uid }}
  runAsGroup: {{ $uid }}
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
