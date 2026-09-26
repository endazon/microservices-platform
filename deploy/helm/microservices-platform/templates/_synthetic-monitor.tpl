{{- /*
NFR-02, NFR-21, ADR-0076 決定 4, IADR-0378, IADR-0469 (#1287):
合成監視の標識（SyntheticMonitoring__Subjects__0）を受け取るサービスの集合（chart のキー。Deployment 名は <key>-service）。

🔴 **ここが唯一の定義である。** deployment.yaml（env を描く側）と synthetic-monitor.yaml（揃わなければ描画を止める側）の
両方がこれを読む。values の knob にしない —— knob にすると 1 つ外すだけで「除外が欠けた面に合成が流れる」構成が
1 行で書けてしまう（ADR-0076 決定 4「除外できない構成では配備しない」）。

中身はローカルの門（scripts/k8s-local-up.sh の SYNTHETIC=1 が `for d in bff dashboard aianalysis` で env を与える集合）と
一致していなければならない。一致は scripts/helm-synthetic-monitor.test.js が描画結果で突き合わせる。
外周（JWT の主体で判定する）は bff と dashboard。aianalysis は内周（ヘッダで判定）で Subjects を読まないが、
門が与えているのでそろえる（無害な重複）。LlmGateway も内周でヘッダだけを見るので env は要らない。
*/ -}}
{{- define "msp.syntheticMonitor.exclusionServices" -}}
bff,dashboard,aianalysis
{{- end -}}
