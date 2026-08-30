---
title: 作業仕様書 — エッジ証明書の SAN をワイルドカード頼みから明示列挙へ変える（#781 の前提）
type: spec
status: done
related_ids:
  - NFR
  - NFR-11
  - ADR-0023
  - IADR-0220
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md
related_specs:
  - "20260830_issue-782_istio-mesh-optin.md"
issue: "#781"
---

# 作業仕様書 — エッジ証明書の SAN を明示列挙へ（#781 の前提）

## 何を見つけたか

**エッジ証明書は、標準的な TLS クライアントから見て `keycloak.localhost` を覆っていなかった。**

`deploy/local/edge/tls/edge-certificate.yaml` の `dnsNames` は `localhost` と `*.localhost` の 2 つだけである。
同ファイルのコメントは「`*.localhost` は 1 段のサブドメインしか覆わない。現行ホストは全て 1 段」と書き、
**ワイルドカードで足りると想定していた。足りない。**

```console
$ # rancher-desktop VM から、cert-manager の root CA で検証する
$ curl --cacert <root CA> https://localhost/            → HTTP 200      （明示 SAN）
$ curl --cacert <root CA> https://keycloak.localhost/   → curl: (60)
    SSL: no alternative certificate subject name matches target hostname 'keycloak.localhost'
```

**機序**: 標準的な TLS 実装は「ワイルドカードの右に 1 ラベルしか無い」証明書を拒否する。
`*.com` のような証明書を防ぐための制限で、**`*.localhost` も同じ形に当たる。**

## なぜ今まで露見しなかったか

🔴 **エッジを叩く経路が例外なく検証を切っていた。**

- `scripts/verify-oidc-edge-flow.sh` は `-k`（ヘッダに「TLS はローカル CA の自己署名なので検証しない」と明記）
- ブラウザは手動で例外を入れる
- `check-stack-ready.js` の discovery 取得も検証しない

**「検証する利用者」が現れて初めて壊れる**種類の欠陥である。
そして **apiserver の OIDC 検証（#781）がまさにその最初の利用者**である ——
`oidc-ca-file` に正しい CA を渡しても、**ホスト名照合で落ちる。**

## 直し方

エッジ host を**明示的に列挙する**。`*.localhost` は、受け付けるクライアント向けの保険として残す。

母集合は**記憶で挙げず走査して引いた**（規則 9）:

```console
$ grep -rhoE "[a-z0-9-]+\.localhost" deploy/local/ --include=*.yaml | sort -u
argocd / grafana / headlamp / keycloak / minio / qdrant / vault / wiki （＋ b.localhost はテスト用資料）
$ kubectl get ingress -A -o jsonpath=...   # 稼働クラスタ
→ 同じ 8 件（b.localhost は Ingress に無い）
```

**`b.localhost` は除外した** —— `deploy/local/` の資料には現れるが Ingress のホストではない。

`ADR-0023` の「`dnsNames` を安定させる」（CA を差し替えても Secret 名とドメインを変えない）要件は、
**列挙を安定させる**ことで満たす。新しいエッジ host を足すときは Ingress と本一覧の両方を触る。

## 実測（適用後）

```console
$ kubectl apply -f deploy/local/edge/tls/edge-certificate.yaml   # 2 名前空間とも configured
$ openssl s_client -connect localhost:443 -servername keycloak.localhost | openssl x509 -ext subjectAltName
  DNS:localhost, DNS:argocd.localhost, DNS:grafana.localhost, DNS:headlamp.localhost,
  DNS:keycloak.localhost, DNS:minio.localhost, DNS:qdrant.localhost, DNS:vault.localhost,
  DNS:wiki.localhost, DNS:*.localhost

$ # VM から検証つきで（apiserver と同じ条件）
  keycloak.localhost     HTTP 302
  localhost              HTTP 200
  grafana.localhost      HTTP 200
  discovery              HTTP 200   ← 🔴 これが #781 の前提
```

**検証を切らずに discovery が 200 を返すようになった。**

## 主張の限界

- **8 host すべてのブラウザ動作は確かめていない**（curl での TLS 検証のみ）。
- `b.localhost` を除いた判断は「Ingress に無い」ことに基づく。**資料側の用途は追っていない。**
- 本作業は **#781 の前提を外しただけ**であり、apiserver の OIDC 検証そのものはまだ入れていない。

## CI が拾った落とし穴 —— リストの中にコメントを置くと検査が読み落とす

初回の push で `static-checks` が落ちた。

```
AssertionError: spec.tls.hosts の "*.localhost" が Certificate の dnsNames に無い（SNI 不一致）
  at scripts/k8s-local-up.test.js:1574
```

原因は**私の書き方**である。`dnsNames` のリスト項目の途中に説明コメントを挟んでいた。
同ファイルの `listValues()`（1427 行）は `- ` で始まらない行に当たると `break` するため、
**コメント以降の host を読まなくなる**。

```js
const m = /^\s*-\s*(.+?)\s*$/.exec(line);
if (!m) break;          // ← コメント行でリストが打ち切られる
```

**検査器は fail-open ではなく fail-loud だった**（読めた件数が減った結果、
`*.localhost` が見つからず落ちた）ので実害は無い。コメントをリストの外へ出し、
**同じ罠に次の人が落ちないよう YAML 側へ注意書きを置いた。**

**`listValues()` 自体は直していない** —— 「同型の事故が 2 回起きたら」の規約に従い、1 回目は記録に留める。
直すなら「コメント行と空行は読み飛ばす」の 1 行だが、共有ヘルパの挙動を変えるため
別途 issue にするのが筋である。
