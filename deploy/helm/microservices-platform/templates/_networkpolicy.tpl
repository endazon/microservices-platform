{{- /*
NFR-18, IADR-0589（Refs GHSA-j4qv-2w99-xg5p）: NetworkPolicy の部品（外向き通信の既定拒否で共有する）。

- msp.netpol.dnsEgressRule : egress の 1 規則。名前解決（UDP / TCP 53）を networkPolicy.egress.dns の
                             名前空間 × Pod ラベル（既定 kube-system の k8s-app: kube-dns）だけへ許す。
                             networkpolicy-egress.yaml と seaweedfs.yaml（allow-seaweedfs-s3-only）が使う。
- msp.netpol.podSelector   : Pod の app ラベルの一覧から `podSelector:` を作る。空の一覧は `podSelector: {}`（名前空間の全 Pod）。

🔴 dns の namespace / podLabels が空なら描画で止める（空の podLabels は「その名前空間の全 Pod の 53 番」になり、
   空の namespace は宛先を決められない。黙って広い許可へ倒さない）。
*/}}
{{- define "msp.netpol.dnsEgressRule" -}}
{{- $dns := (.Values.networkPolicy.egress | default dict).dns | default dict -}}
{{- if not $dns.namespace -}}{{- fail "networkPolicy.egress.dns.namespace が空である（名前解決の Pod が居る名前空間を与える。既定 kube-system）" -}}{{- end -}}
{{- if not $dns.podLabels -}}{{- fail "networkPolicy.egress.dns.podLabels が空である（名前解決の Pod のラベルを与える。既定 k8s-app: kube-dns）" -}}{{- end -}}
- to:
    - namespaceSelector:
        matchLabels:
          kubernetes.io/metadata.name: {{ $dns.namespace }}
      podSelector:
        matchLabels:
          {{- toYaml $dns.podLabels | nindent 10 }}
  ports:
    - protocol: UDP
      port: 53
    - protocol: TCP
      port: 53
{{- end -}}

{{- define "msp.netpol.podSelector" -}}
{{- if . -}}
podSelector:
  matchExpressions:
    - key: app
      operator: In
      values:
        {{- range . }}
        - {{ . | quote }}
        {{- end }}
{{- else -}}
podSelector: {}
{{- end -}}
{{- end -}}
