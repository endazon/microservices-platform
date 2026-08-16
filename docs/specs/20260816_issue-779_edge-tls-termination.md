---
title: 作業仕様書 — 経路B のエッジ TLS 終端基盤を cert-manager で入れる（#779）
type: spec
status: done
related_ids:
  - NFR-11
  - ADR-0023
  - ADR-0021
  - ADR-0008
  - IADR-0076
  - IADR-0086
  - IADR-0091
  - IADR-0105
  - IADR-0205
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md"
related_specs:
  - "../adr/IADR-0205_local-edge-tls-cert-manager.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../adr/IADR-0086_oidc-issuer-metadata-split.md"
  - "../adr/IADR-0105_remove-apiserver-oidc-flag-wiring.md"
  - "20260815_issue-454_open-issue-stocktake-and-waves.md"
---

# 作業仕様書: 経路B のエッジ TLS 終端基盤（#779 ／ #442 の子 1）

## 1. 起点と、着手できるようになった経緯

起点は **#779**（#442 の子 1／5）。

#442 は 2026-08-15 の棚卸しで `blocked` と判定されていたが、**2026-08-16 に別環境で 3 軸を測り直した結果、
ゲートが開いた**（#778 / 層 a §8 の同日追記）。層 a §8 は「波 5 で子 issue を起票しない」理由 ① に
**覆る条件**（「dotnet が入り #455 / #442 のどちらかに着手できるようになった時点」）を付しており、
それが満たされたため [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4 に従って
#442 を子 5 件（#779 / #780 / #781 / #782 / #783）へ分割した。本書はその子 1 の仕様である。

**本子が最初に来る理由**は、後続の全部がここに載るからである。

- k8s 1.30+ は apiserver の OIDC 設定を構造化認証設定 `jwt[0]` へ変換し、**`issuer.url` に https を強制**する
  （`IADR-0084`（Superseded by `IADR-0105`）の 2026-07-25 追記が単一情報源。実測エラー `URL scheme must be https`。
  `k3s v1.35.4+k3s1` で apiserver が 10 回連続起動失敗＝クラスタ停止を起こしている）
- しかも **https にするだけでは足りない**。apiserver がその証明書を検証できる
  （`oidc-ca-file` に渡せる）**安定した CA が k8s の中に存在する**必要がある
- その CA が、いま存在しない

## 2. 母集合（`.claude/rules/traceability.md` 規則 1〜8 ＋ `traceability.repo.md` 規則 9・10）

### 2.1 規則 1・2 —— 誤りの側から、変種を列挙して引く

是正対象は「**エッジの TLS を『対象外』と宣言している記述**」である。
**正しい側（`cert-manager` / `edge-tls`）で引いても 1 件も出ない**（そもそも存在しないため）。
誤りの側の語を **4 軸**で引いた。

| 軸 | 検索語 | 生のヒット数 |
| --- | ---: | --- |
| 1 | `実 TLS` / `実TLS` | 7 |
| 2 | `Tier 3` | 20 |
| 3 | `自己署名` / `self-signed` / `selfsigned`（大小無視） | 7 |
| 4 | `deploy/local/edge` 配下の `tls` | 0（**`spec.tls` は 1 件も無い**） |

走査は `git grep -nI`（追跡下・バイナリ除外）で、パスの除外だけを行った
（規則 3「拡張子で絞らない」・規則 4「行フィルタで絞らずパスから引く」）。
除外パスは `planning`（submodule・pin のみ）と `src/ai-stock-trading`（別プロジェクトの submodule）。

### 2.2 規則 5 —— 軸を 1 本で終わらせない

軸を変えると出続けた。**軸 1 だけでは `deploy/local/edge/README.md:76` と
`scripts/k8s-local-up.sh:366` が落ちる**（どちらも「実 TLS」の語を含まず「自己署名」とだけ書いている）。
軸 3 を足して初めて捕まった。

### 2.3 規則 6 —— 是正対象と、除外したものとその理由

**是正する（live な権威文書とコード・4 ファイル 8 箇所）**

| ファイル:行 | 記述 |
| --- | --- |
| `deploy/local/edge/README.md:60` | 「Traefik 既定の**自己署名証明書**（…実 TLS 証明書は本オーバーレイのスコープ外）」 |
| `deploy/local/edge/README.md:76` | 「443 は Traefik 既定の**自己署名証明書**で終端されるため別扱い」 |
| `deploy/local/edge/README.md:77` | 「実 TLS 証明書・admin entrypoint の TLS 化は本オーバーレイのスコープ外（Tier 3）」 |
| `deploy/local/edge/README.md:117` | 「実 TLS 証明書・本番相当のエッジ（Istio）・稼働率は **Tier 3**（対象外）」 |
| `deploy/local/edge/platform-frontend-ingress.yaml:5` | 「443 は Traefik 既定の自己署名証明書（ブラウザ警告・実 TLS は別途）」 |
| `docs/adr/IADR-0091:55` / `:56` | 決定 3 の本文（「443 は Traefik 既定の自己署名証明書（…実 TLS は別途）」） |
| `docs/adr/IADR-0091:90` | 「443 自己署名の制約あり（README 明記）」 |
| `scripts/k8s-local-up.sh:366` | 出力メッセージ「https://localhost/ (443・Traefik 既定自己署名)」 |

**除外する（理由つき）**

| 対象 | 除外理由 |
| --- | --- |
| `docs/specs/20260720_issue-356_local-edge-aggregation.md:78` | **point-in-time の記録**。#356 着手時点の対象外を書いたもので、後から書き換えない（`traceability.repo.md` の Superseded 引用書式：確定済みの `docs/specs/` は書き換えない） |
| `docs/specs/20260721_issue-353_grafana-edge-oidc-url.md:54` | 同上 |
| 軸 2 のうち **18 件**（`deploy/local/README.md:74`・`argocd/README.md:62`・`observability/README.md:71`・`vault/README.md:10,52`・`vault/eso/vault-auth-rbac.yaml:5`・`vault/oidc/policies/admin.hcl:2`・`IADR-0077:79,93`・`docs/adr/README.md:133`・`docs/security/security.md:184`・**specs 6 件**〔`20260719_issue-24:47,:68`・`20260720_issue-348:78`・`20260721_issue-353_vault-keycloak-oidc:68`・`20260807_issue-507:47`・`20260810_issue-583:165`〕・`scripts/check-cross-repo-refs.js:534`） | **TLS と無関係の「Tier 3」**（Hetzner 実 stand-up・Vault 本番運用・可観測性の本番リテンション・検査器のフィクスチャ）。本子は**エッジ TLS の Tier 境界だけ**を動かす |

**軸 2 の引き算を見せる（規則 8）**: 生ヒット **20 件** − 是正 **2 件**（`edge/README.md:77` / `:117`）
= 除外 **18 件**。
> **［是正 / クロス監査］当初ここは「specs 4 件」＝除外 16 件と書いており、`20 − 2 = 18` に閉じていなかった。**
> `git grep -nI "Tier 3" dca76ce -- docs/specs | wc -l` を引き直すと **6 件**である。
> **除外の妥当性は変わらないが、引き算が合わない書き方は追試で再現しない。**
| `deploy/helm/microservices-platform/**`（本番像） | **本子は経路B（`deploy/local/`）に閉じる**。本番は Istio Ingress Gateway が `edge-tls` を参照する形（`ADR-0023` の想定）で、子 4（#782）が扱う |
| `docs/adr/IADR-0103`（admin entrypoint 平文 http の根拠） | admin:50000 の TLS 化を**本子のスコープに入れない**ため（下記 §3.2）。触らない |

### 2.4 規則 10 —— この是正で新たに誤りになる自分の記述

是正後の語で base（`dca76ce`）を引き直し、**引き算を見せる**（規則 8）。

```
$ git grep -nIiE "cert-manager|certmanager|ClusterIssuer|letsencrypt|mkcert|edge-tls" dca76ce \
    -- . ':!planning' ':!src/ai-stock-trading' | wc -l
9
```

**生ヒット 9 件 − point-in-time 5 件 − 別紙とその自己試験 4 件 = 追随が要るもの 0 件。**

| 除外 | 内訳（**行まで書く**） | 理由 |
| --- | --- | --- |
| point-in-time **5 件** | `IADR-0170:47`・`20260811_issue-589_planning-pin-freshness.md:49`・`20260814_planning-pin-cff0e7b.md:11,42,43` | 決定・観測当時の記録（いずれも `ADR-0023` が `Proposed → Accepted` へ動いたことの記録）。後から書き換えない |
| 別紙 ＋ 自己試験 **4 件** | `docs/how-to/plan-id-range-history-annex.md:108,109` ＋ `scripts/scripts.repo.test.js:5663,5666` | **計画 pin の鮮度検知の記述**（「cert-manager は未配備」は planning 側の記述の引用）であり、本子が変えるのは実装側の配備であって planning の記述ではない。**自己試験が同じ文字列を固定しているので、触ると検査が落ちる** |

> **［是正 / クロス監査 D7］当初ここは「point-in-time 4 件・別紙 5 件」と書いていた。内訳が入れ替わっており、
> 合計 9 だけが偶然合っていた。** 引き算を見せる目的で足した記述が追試で再現しない状態だったので、
> **行番号まで書いて数え直した**（規則 8）。

**`deploy/` 配下の cert-manager 資産は 0 件**で、クラスタにも Namespace / CRD が無い。
**したがって「この是正で新たに誤りになる自分の記述」は生じない。**

> **［是正 / クロス監査］当初ここは「引き直したところ 0 件」とだけ書いていた。**
> 生の走査は 9 件を返すので、**そのまま追試すると再現しない**。引き算と除外理由を上に置いた。

> **導出値は走査ではなく計算し直した**（規則 10）。上表の件数は本書の執筆前に引いた値であり、
> **本書自身は追跡下に無い時点で数えている**（規則 8 の自己参照。本書が追跡下に入ると
> 軸 1・2・3 の件数はそれぞれ増える）。

## 3. 方式

### 3.1 採用: cert-manager ＋ selfsigned → CA `ClusterIssuer`

```
ClusterIssuer(selfSigned)
  └─ Certificate（ルート CA・isCA: true）→ Secret local-edge-root-ca
       └─ ClusterIssuer(ca, secretName: local-edge-root-ca)
            └─ Certificate{ secretName: edge-tls, dnsNames: [localhost, *.localhost] }
```

| 案 | apiserver の `oidc-ca-file` に渡せるか | 採否 |
| --- | --- | --- |
| Traefik 既定の自己署名 | **不可**。Secret 化されておらず（Traefik がメモリ内生成）再起動ごとに変わる。SAN にホスト名が入らず Go の TLS 検証が `x509: certificate is not valid for ...` で落ちる | 却下 |
| **cert-manager + selfsigned → CA** | **可**。ルート CA が Secret の `ca.crt` として安定して存在する。`dnsNames` に `*.localhost` を入れられる | **採用** |
| mkcert | 可。ただし **CA が開発者マシン固有でリポジトリから再現できない**。`k8s-local-up.sh` の「冪等・fail-safe・env 未設定で既定動作」と CI の stub-on-PATH に噛み合わない | 却下（README に任意手順として残す） |

**計画 `ADR-0023` との関係**: 同 ADR は `Accepted`（updated 2026-08-10。一次資料で確認）。
**論拠の正本は [IADR-0205](../adr/IADR-0205_local-edge-tls-cert-manager.md) 決定 2 にあり、ここへ複写しない**
（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)「参照点を 1 つに畳む」）。要点だけ:
**既定 CA が Let's Encrypt である同 ADR から、ローカルの selfsigned は外れる**。
消費側が Istio か Traefik かの違いと、`*.localhost` では同 ADR が示す DNS-01 / Vault PKI の
2 択がどちらも取れないことが根拠である。

> **［是正 / クロス監査 V4］当初ここは「同 ADR は prod の Istio Ingress Gateway 前提でローカルの
> Traefik については何も決めていない」と書いていた。誤りである** —— 同 ADR の本文に環境を限定する語は無く、
> `prod` は 0 回しか出現しない。**本文に無い限定を根拠にしていた。**
> IADR 側は是正したが本書と PR 本文が追随しておらず、**同じ PR の中で論拠が正反対を向いていた**
> （`traceability.repo.md` 規則 10「是正のたびに、この変更で新たに誤りになる自分の記述を引き直す」の破れ）。

同 ADR の設計要件は踏襲する。

- CA 固有設定は `ClusterIssuer` に閉じ込める（消費側は CA を知らない）
- **`secretName` を `edge-tls` に固定**し、`dnsNames` を安定させる（ADR-0023 が例示している名前をそのまま使う）
- 切り替えは `ClusterIssuer` を足して `issuerRef` を差し替えるだけにする

### 3.2 スコープ外（意図的に落とすもの）

| 落とすもの | 理由 |
| --- | --- |
| **Keycloak をエッジへ出すこと・issuer の変更** | 子 2（#780）。**本子は issuer 文字列を 1 バイトも変えない** |
| **apiserver の OIDC 配線** | 子 3（#781）。`IADR-0105` が除去した経路を復活させない。**回帰テスト 4 件が緑のままであることが本子の受け入れ基準**である |
| **admin:50000 の TLS 化** | `IADR-0103` の改定を伴い、7 クライアントの redirect 追記に波及する。子 2 と同時に扱うのが安全 |
| **http → https の恒久リダイレクト** | 現状 Traefik args に `redirections` が無い。足すと `http://*.localhost:50000` 前提の既存 docs と realm redirect が全部回り道になる。**本子は追加のみ・http を残す** |
| Let's Encrypt / Vault PKI | 本番像。`issuerRef` の差し替えで済むことを設計で担保するに留める |
| Istio | 子 4（#782） |

## 4. 変更点

| ファイル | 変更 |
| --- | --- |
| `deploy/local/edge/tls/cert-manager-issuers.yaml`（新規） | `ClusterIssuer(selfSigned)` ＋ ルート CA `Certificate` ＋ `ClusterIssuer(ca)` |
| `deploy/local/edge/tls/edge-certificate.yaml`（新規） | `Certificate{ secretName: edge-tls, dnsNames: [localhost, *.localhost] }` |
| `deploy/local/edge/tls/kustomization.yaml`（新規） | 上 2 つを束ねる |
| `deploy/local/edge/platform-frontend-ingress.yaml` | `spec.tls` を**追加**（`hosts` と `secretName: edge-tls`）＋ 冒頭コメントの是正 |

> **［着手中の訂正］`spec.tls` を足すのは `platform-frontend-ingress.yaml` の 1 件だけである。**
> 当初は「Ingress 8 件へ追加」と書いていたが、実測すると**管理ツール 7 件は `admin:50000` entrypoint に
> 載っており、その entrypoint に TLS が設定されていない**（Traefik の args に
> `--entryPoints.admin.http.tls` が無い）。**足しても効かず、「TLS になったつもり」の記述だけが残る。**
> 443（websecure）に載っている Ingress は `platform-frontend-edge` の 1 件だけである。
> admin:50000 の TLS 化は `IADR-0103` の改定と 7 OIDC クライアントの redirect 追記に波及するため #780 と同時に扱う。
> **この誤りを固定するテストを足した**（`admin:50000 の Ingress には spec.tls を足さない`）。

> **［着手中の訂正 2］`IADR-0091` の Supersede は決定 3 のみにした。**
> #779 の受け入れ基準は当初「決定 3 と**決定 5**（issuer は最小案維持）と却下代替案を Supersede する」と
> 書いていたが、**決定 5 は issuer の話であり、本子は issuer 文字列を 1 バイトも変えない**。
> 動かさないものを Supersede と書くのは、記録として誤りである。
> **ただし「決定 5 をいつか Supersede する」という約束が落ちてはいけない**ので、
> **#780 の受け入れ基準へ明示的に移した**（AI レビューの指摘。約束の行き先が無いまま
> #779 が `Closes` で閉じると、決定 5 の Supersede が黙って消える）。#779 の受け入れ基準も訂正した。
| `scripts/k8s-local-up.sh` | `LOCALEDGE=1` ブロックに cert-manager の導入・CRD Established 待ち・`tls/` の apply・証明書 Ready 待ちを足す。**出力メッセージを是正**。**既定経路は 1 バイトも変えない** |
| `scripts/k8s-local-up.test.js` | `OPTIN_TOKENS` に追加 ＋ `LOCALEDGE=1` の適用固定 ＋ **edge overlay の静的検査** |
| `docs/adr/IADR-0205_local-edge-tls-cert-manager.md`（新規） | 方式決定。**`IADR-0091` の決定 3 のみを Supersede**（決定 5 と却下代替案は #780 の射程。下記） |
| `docs/adr/IADR-0091_local-edge-aggregation-traefik.md` | **決定 3 のみ**に後継併記（**旧 ID を残す**。`traceability.repo.md` の Superseded 引用書式）。決定 5 の節は無変更 |
| `docs/adr/README.md` | `IADR-0205` の索引行 |
| `deploy/local/edge/README.md` | 「実 TLS は対象外（Tier 3）」4 箇所の是正・CA の取り出し手順・mkcert の任意手順 |

### 4.1 なぜ `tls/` を別ディレクトリにするのか

cert-manager の CRD は**クラスタに CRD が入る前に `kubectl apply -k`（サーバ側検証あり）へ渡すと失敗する**。
`deploy/local/edge` 本体の kustomization に混ぜると、cert-manager 未導入の環境で **edge overlay 全体が落ちる**。
`tls/` を分け、スクリプトが「cert-manager 導入 → CRD Established 待ち → `tls/` apply」の順で当てる。

## 5. 検証（受け入れ基準の写像）

| # | 受け入れ基準 | 検証方法 |
| --- | --- | --- |
| 1 | cert-manager が入り `ClusterIssuer` 2 種・ルート CA・`Secret edge-tls` が Ready | 実機で `kubectl get clusterissuer,certificate,secret -A` |
| 2 | `curl --cacert` で https が 200 | 実機。CA は `kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}'` |
| 3 | 既存 http 経路が無傷 | 実機。8 経路 ＋ `http://localhost/` |
| 4 | 既定（`LOCALEDGE` 未設定）でバイト等価 | `node scripts/k8s-local-up.test.js`（`EXPECTED_DEFAULT_CREATE` 厳密一致・`OPTIN_TOKENS` 不在） |
| 5 | `OPTIN_TOKENS` に `cert-manager` / `edge-tls` を追加 | 同上（**変異試験**: 既定経路に文字列を混ぜると落ちることを実測する） |
| 6 | apiserver に触っていない | `IADR-0105` の 4 テストが緑 |
| 7 | overlay の静的検査 | 新規テスト（`spec.tls.secretName` が `Certificate` の `secretName` と一致・`dnsNames` が Ingress のホスト集合を覆う） |
| 8 | `IADR-0091` **決定 3 のみ**の Supersede（決定 5 は #780 へ送り、同 issue の受け入れ基準に明記した） | `check-adr-numbering.js` ＋ 索引行 |
| 9 | 「実 TLS は対象外」の是正 | §2.3 の 8 箇所を走査で確認 |

## 6. テスト方針

`scripts/k8s-local-up.test.js` に足す（`scripts/scripts.test.js` は**変更禁止**＝`IADR-0115` 分類 A）。
**CI に `helm template` / `kustomize build` を実行するジョブは 1 件も無い**ため、
既存の型（マニフェストを読んで正規表現で固定する・外部依存ゼロ）で静的検査を置く。
本来の形（実際に build / lint するジョブ）は子 5（#783）が扱い、そのときに
**二重に持たない判断を明示する**（`IADR-0141`「参照点を 1 つに畳む」）。

## 7. 実測（2026-08-16・稼働中の k3s）

**変異試験**（`session-handoff.md` §5 型 4「変異試験をしていない検査を信用しない」）。
追加した検査を **20 通り**に壊し、**すべて RED（＝検査が効いた）**ことを確認した。
配線・スクリプト側が 12 通り（下表 M1〜M11）、**CA 鎖（決定 2）が 8 通り（MX1〜MX8）**である。

| # | 壊し方 | 結果 |
| --- | --- | :---: |
| M1 | `spec.tls.secretName` を `Certificate` と食い違わせる | RED |
| M2 | `dnsNames` から `*.localhost` を落とす | RED |
| M3 | `tls/` を親 kustomization に含める | RED |
| M4 | admin ingress に `spec.tls` を足す | RED |
| M5 | ルート CA を `cert-manager` 以外の namespace へ移す | RED |
| M6 | `--server-side` を外す | RED |
| M7 | バージョン固定を `releases/latest` へ変える | RED |
| M8 | http(80) を entrypoints から外す | RED |
| M9a / M9b | 既定経路に `cert-manager` の apply ／ `certificate/edge-tls` の待ちを混ぜる | RED / RED |
| M10 | `LOCALEDGE=1` に apiserver の OIDC 引数を配線する | RED |
| M11 | `tls/` overlay の apply を落とす | RED |
| — | 変異なし | GREEN |

> **★ 最初に組んだ M9 / M10 は GREEN を返したが、それは検査が弱いのではなく変異の作り方が誤っていた。**
> `runUp().lines` は**スタブログ（記録されたコマンドの argv）**であって、スクリプトの `echo` 出力ではない。
> `echo cert-manager` を混ぜても検査対象に入らない。**コマンド側で壊し直したら 4 件とも RED になった。**
> 変異試験そのものが誤りうる、という実例として残す。

### CA 鎖（決定 2）の変異 —— クロス監査 V2 が開けた穴

**上の 12 通りは「決定 2 そのもの」を一度も壊していなかった。**
クロス監査が独立に 4 通り当てたところ**全部すり抜けた**（`issuerRef` を CA→selfSigned・`kind` の取り違え・
ルート `secretName` の改名・`apiVersion` の取り違え）。原因は 2 つ:

1. 検査がリテラルの**存在**だけを見て、**結線**（`issuerRef.name` / `.kind` / `.group`、
   ルート `Certificate.spec.secretName` ↔ CA Issuer の `ca.secretName`）を突き合わせていなかった
2. 正規表現が **YAML ドキュメント境界（`---`）を跨ぐ lazy 一致**で、
   先頭の `kind: ClusterIssuer` が selfsigned 側にマッチしたまま別ドキュメントの値を拾えた

**是正**: `yamlDocs()` で `---` 分割してドキュメント単位に見るようにし、結線 3 本と `apiVersion` を突き合わせた。
その結果を 8 通りで確かめた。

| # | 壊し方 | 結果 |
| --- | --- | :---: |
| MX1 | 葉の `issuerRef` を CA→selfSigned へ（**2 段が 1 段へ崩れる＝本 ADR の存在理由が消える**） | RED |
| MX2 | CA Issuer の `kind` を `ClusterIssuer`→`Issuer` | RED |
| MX3 | ルート CA の `secretName` を改名（CA Issuer の `ca.secretName` と不整合） | RED |
| MX4 | 葉の `apiVersion` を `v1alpha2` へ | RED |
| MX5 | CA Issuer（2 段目）を丸ごと落とす | RED |
| MX6 | ルート CA の `isCA: true` を落とす | RED |
| MX7 | ルート CA を `platform-infra` namespace へ移す | RED |
| MX8 | ルート CA の `issuerRef` を selfSigned→CA へ（循環） | RED |

**教訓**: 変異は「検査が見ている場所」ではなく「**決定が主張している内容**」から作る。
12 通りは前者に寄っていたので、決定 2 を一度も試していなかった。

**実機の疎通**（稼働中の k3s・19 日稼働のスタックに対して適用）。

| 観点 | 実測 |
| --- | --- |
| cert-manager 導入 | `v1.21.1` を `--server-side --force-conflicts` で apply → CRD 2 種が Established・Deployment 2 種が rollout 完了 |
| `ClusterIssuer` | `local-edge-selfsigned` / `local-edge-ca` とも `READY True` |
| `Certificate` | `cert-manager/local-edge-root-ca` / `microservices-platform/edge-tls` とも `READY True` |
| Ingress | `platform-frontend-edge` の `PORTS` が **`80` → `80, 443`** へ |
| 提示される証明書 | `subject=CN=localhost` ／ `issuer=CN=microservices-platform local edge root CA` ／ SAN = `DNS:localhost, DNS:*.localhost` |
| CA 検証（`openssl s_client -CAfile`） | `localhost` → **`Verify return code: 0 (ok)`**。`keycloak.localhost` → **`0 (ok)`**（#780 の先取り確認） |
| CA を渡さない場合 | `Verify return code: 21 (unable to verify the first certificate)` ＝ **検証が本物である裏取り** |
| HTTP | `https://localhost/` = **200**、`https://localhost/bff/documents` = **200** |
| http(80) の無傷 | `http://localhost/` = 200・`http://localhost/bff/documents` = 200 |
| admin:50000 の 7 経路 | grafana 302 / headlamp 200 / vault 307 / qdrant 200 / minio 200 / wiki 200 / argocd 200 ＝ **無傷** |

> **★ Windows の curl は schannel バックエンドで `--cacert` によるカスタム CA 検証ができない**
> （`schannel: the revocation status is unknown` で失敗する）。**curl 側の制約であって配線の問題ではない。**
> 鎖の検証は `openssl s_client -CAfile` で行い、HTTP の疎通だけを見るなら `curl --ssl-no-revoke` を使う。
> README に書いた。

## 8. 未決事項（後続へ送るもの）

1. **CA の配布先**。apiserver へ渡すのは #781 だが、**backend の HttpClient が新 issuer を叩くときも
   同じ CA を信頼する必要がある**（#780）。本子は「**Secret として安定して在る**」ところまでを担い、
   配布は後続へ送る。
2. **admin:50000 の TLS 化**。`IADR-0103` の改定と 7 OIDC クライアントの redirect 追記に波及するため #780 と同時。
3. **`kustomize build` を実際に走らせる CI ジョブ**。本子は静的検査で代替した。#783 が本来の形へ置き換える際、
   **二重に持たない判断を明示する**（`IADR-0141`「参照点を 1 つに畳む」）。
4. `*.localhost` のワイルドカードは **1 段のサブドメインしか覆わない**（`a.b.localhost` は対象外）。
   現行のホストはすべて 1 段なので問題にならない。README に明記した。
