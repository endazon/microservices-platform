---
title: 作業仕様書 — 経路B の管理系経路（admin:50000）を HTTPS 化する（#841）
type: spec
status: done
related_ids:
  - NFR-11
  - ADR-0047
  - ADR-0023
  - ADR-0021
  - IADR-0091
  - IADR-0103
  - IADR-0206
  - IADR-0220
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "../adr/IADR-0220_admin-entrypoint-tls-and-http-redirect.md"
  - "../adr/IADR-0206_local-edge-tls-cert-manager.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "20260816_issue-779_edge-tls-termination.md"
---

# 作業仕様書: 経路B の管理系経路（admin:50000）を HTTPS 化する（#841）

## 1. 起点

起点は **#841**（#834 から切り出した**実体側**）。#834 は条文の追随
（[IADR-0206](../adr/IADR-0206_local-edge-tls-cert-manager.md) の「`NFR-11` 適用外」整理の撤回）を担い、
**本作業は実体の HTTPS 化だけを担う**。したがって
**`docs/adr/IADR-0206_local-edge-tls-cert-manager.md` の本文は本作業では 1 バイトも触らない**。

根拠となる計画は次の 2 つである。

- **`NFR-11`**（全経路の HTTPS 化）: 「**外部から到達し得るすべてのエンドポイントを HTTPS とし、平文 HTTP を
  残さない**。対象は … **運用系ツール（Grafana・Kiali・ArgoCD UI・Headlamp）を含む**」。
  **★ 適用範囲は環境を問わない（利用者裁定 2026-08-16。裁定依頼 planning#383）** ——
  **ローカル検証環境（経路B）も適用内である。** 実装側の「loopback へ bind する閉域であり
  『外部から到達し得る』に当たらない」という読みは**採らない**と明記された
  （[01_requirements](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) NFR-11 行）。
- **`ADR-0047`**（[原文](../../planning/projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md)）:
  エッジ TLS 証明書の運用は**経路B にも及ぶ**。`*.localhost` のように DNS-01 も Vault PKI も取れない
  ドメインでは **selfsigned CA を許容**する（決定 2）。設計要件 3 点（CA 固有設定を `ClusterIssuer` に隔離・
  `secretName` と `dnsNames` の安定・切替は `issuerRef` の差し替えのみ）は**守る**。

**証明書基盤そのものは #779（[IADR-0206](../adr/IADR-0206_local-edge-tls-cert-manager.md)）で既に在る。**
残っているのは**適用範囲**である。

## 2. 母集合（`.claude/rules/traceability.md` 規則 1〜8 ＋ `traceability.repo.md` 規則 9・10）

### 2.1 規則 9 —— 誤りの側の文字列で、追跡下の全ファイルを走査する

**拡張子で絞らない。** パスの除外（`:!planning` `:!src/ai-stock-trading`）だけで取った。
走査基準は本ブランチの分岐元 `5ed54b02`。

| 軸 | 走査コマンド | 生のヒット数 |
| --- | --- | --- |
| 1 | `git grep -I -o -e 'http://[A-Za-z0-9.-]*localhost:50000' -- . ':!planning' ':!src/ai-stock-trading'` | **104 件 / 31 ファイル** |
| 2 | `git grep -I -o -e ':50000' -- . ':!planning' ':!src/ai-stock-trading'` | **243 件 / 42 ファイル** |
| 3 | `git grep -I -o -e 'https\?://[A-Za-z0-9.-]*localhost:50000' -- . ':!planning' ':!src/ai-stock-trading'` | **108 件**（＝軸 1 の 104 ＋ 既に https の 4 件） |
| 4 | `git grep -I -c -e 'entryPoints' -e 'entrypoints' -- . ':!planning' ':!src/ai-stock-trading'` | **12 ファイル** |

**軸 1 が是正の母集合**である。軸 2 は軸 1 の上位集合で、`:50000` だけを書いている記述
（ポート番号・k3d の `-p` 引数・散文）を含むため、**そのままでは是正対象にならない**。
軸 3 は「既に https で書かれている 4 件」を洗い出すために引いた —— **4 件とも Vault である**。
`vault` client の `redirectUris` / `webOrigins`（`deploy/keycloak/microservices-platform-realm.json` の
500 行目・505 行目）、`deploy/local/vault/oidc/bootstrap.sh:26` の `REDIRECTS`、
`deploy/local/vault/oidc/README.md:58` の 1 件である。
**Vault だけは [IADR-0094](../adr/IADR-0094_vault-keycloak-oidc.md) が「TLS 化に備え両登録」を先取りしていた**
（同 ADR は `http(s)://…` と書いており、この表記は `https://` の走査に掛からない）。

### 2.2 規則 6 —— 引いた結果と、除外したものとその理由

**軸 1 の 104 件を、1 件も概数にせずに分ける。**

| 区分 | ファイル数 | 件数 | 扱い |
| --- | ---: | ---: | --- |
| A. `deploy/` ＋ `scripts/` ＋ `docs/operations/`（live な設定とコードと手順書） | 16 | **65** | **是正する** |
| B. `docs/specs/`（確定済みの作業仕様書） | 10 | **27** | **除外** |
| C. `docs/adr/`（過去の決定の記録と、その索引） | 5 | **12** | **除外** |
| 合計 | 31 | **104** | 104 − 65 − 27 − 12 = **0**（引き算が合う） |

**区分 A の内訳（16 ファイル・65 件）**

| ファイル | 件数 |
| --- | ---: |
| `deploy/keycloak/microservices-platform-realm.json` | 12 |
| `deploy/local/edge/README.md` | 10 |
| `deploy/local/wiki-oidc/README.md` | 8 |
| `deploy/local/README.md` | 5 |
| `docs/operations/local-sso-recovery-runbook.md` | 5 |
| `scripts/k8s-local-up.sh` | 3 |
| `deploy/local/argocd/README.md` | 3 |
| `deploy/local/observability/README.md` | 3 |
| `deploy/local/values-local.yaml` | 3 |
| `deploy/local/vault/oidc/README.md` | 3 |
| `deploy/local/minio-oidc/README.md` | 2 |
| `deploy/local/observability/grafana.yaml` | 2 |
| `deploy/local/vault/oidc/bootstrap.sh` | 2 |
| `scripts/check-realm-constraints.js` | 2 |
| `deploy/helm/microservices-platform/values.yaml` | 1 |
| `deploy/local/argocd/oidc/argocd-cm-patch.yaml` | 1 |
| 小計 | **65** |

**区分 B の除外理由（10 ファイル・27 件）**: `docs/specs/` の確定済み仕様書である。
`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」が
**確定済みの `docs/specs/` は書き換えない**と定めている。これらは「その時点で何をどう実装したか」の記録であり、
**後から URL を書き換えると、当時の実装と食い違う記録になる**。
内訳: `20260725_issue-344_wiki-base-url-edge-alignment.md` 5 /
`20260721_issue-353_wikijs-keycloak-oidc.md` 4 /
`20260725_issue-353_edge-oidc-redirect-uris-headlamp-spa.md` 3 /
`20260720_issue-353_argocd-keycloak-oidc.md` 3 / `20260720_issue-344_frontend-wiki-url.md` 3 /
`20260728_issue-385_wiki-oidc-site-url-consistency.md` 2 /
`20260726_issue-385_wiki-oidc-portforward-redirect.md` 2 /
`20260721_issue-353_minio-keycloak-oidc.md` 2 / `20260721_issue-353_grafana-edge-oidc-url.md` 2 /
`20260720_issue-356_local-edge-aggregation.md` 1。

**区分 C の除外理由（5 ファイル・12 件）**: 過去の決定の記録である。
`docs/adr/README.md`（4 件）は各 ADR 本体の要約であり、**本体を書き換えない以上、索引も動かさない**
（動かすと索引と本体が食い違う）。
`IADR-0084`（1 件）は **`IADR-0105` が Superseded にした**もので live ではない。
`IADR-0092`（2 件）/ `IADR-0093`（2 件）/ `IADR-0095`（3 件）は
**当時 `http://…:50000` で登録すると決めたことの記録**であり、本作業はその決定を上書きするのではなく
**新しい決定（`IADR-0220`）で置き換える**。後継 ID は `IADR-0220` の §関連 が持つ。
**`IADR-0206` は軸 1 に 0 件**（`http://…:50000` を本文に書いていない）であり、
そもそも本作業の母集合に入らない —— 条文の追随は **#834** の領分である。

### 2.3 規則 10 —— この変更で新たに誤りになる記述を引き直す

**是正前の語（`http://…:50000`）では捕まらない**ため、**是正後に**次の語で引き直す。

- `git grep -I -n -e '平文' -- . ':!planning' ':!src/ai-stock-trading'`
- `git grep -I -n -e 'admin entrypoint' -e 'entryPoints.admin' -- . ':!planning' ':!src/ai-stock-trading'`
- `git grep -I -n -e '恒久リダイレクト' -e 'redirections' -- . ':!planning' ':!src/ai-stock-trading'`

**引き直して実際に見つかった、是正前の語では捕まらない記述**（いずれも `:50000` も `http://` も含まないか、
含んでいても軸 1 では別の意味に見えていた）:

| ファイル | 是正前の記述 | 扱い |
| --- | --- | --- |
| `deploy/keycloak/microservices-platform-realm.json` | `platform-spa` の `redirectUris` / `webOrigins` / `post.logout.redirect.uris` が `http://localhost`（エッジ 80 の origin） | **是正**（80 が https へリダイレクトするため origin が https になる） |
| `deploy/local/argocd/oidc/argocd-cmdparams-patch.yaml` | 「edge は**平文 http で終端する**ため `server.insecure=true`」 | **是正**（設定は据え置き、理由の記述だけ改める） |
| `docs/security/security.md` | 「通信時暗号化（外部→BFF）… **ローカルは平文**」 | **是正** |
| `deploy/local/edge/README.md` | 「**admin entrypoint の TLS 化は Tier 3**」 | **是正** |
| `docs/operations/local-sso-recovery-runbook.md` | 「`https://<tool>.localhost:50000` は **404**（admin entrypoint は平文 http のみ）」 | **是正** |
| `deploy/local/README.md` / `docs/operations/local-sso-recovery-runbook.md` | SPA/BFF の到達 URL が `http://localhost/` | **是正** |
| `docs/adr/IADR-0091` §決定 3 の 2026-08-16 追記 | 「[[IADR-0103]]（admin:50000 は平文 http）は動いていない」 | **是正**（日付つき追記。**誤帰属＋陳腐化の 2 点**。下記 §2.5） |
| `docs/adr/IADR-0094` §決定 / §代替案 | 「edge admin:50000 は**現状 http**・TLS 化に備え両登録」 | **除外**（区分 C。過去の決定の記録。後継は `IADR-0220`） |
| `docs/adr/IADR-0206` §決定 4 ほか | 「admin:50000 は平文のまま」「`NFR-11` は適用外」 | **除外**（**#834 の領分**） |

**導出値（件数）は走査せずに計算し直す**（規則 10）。本書の表の合計は上の引き算で閉じている。

### 2.4 規則 5 —— 軸を 1 本で終わらせない

上の 4 軸に加え、**実体側の軸**として次を引いた（是正箇所の取りこぼし防止）。

- `git grep -I -n -e 'spec:' -e 'tls:' -- deploy/local/edge` → `spec.tls` を持つのは
  `platform-frontend-ingress.yaml` の **1 件のみ**（＝管理系 4 ファイルは 0 件）。
- `git grep -I -n -e 'router.entrypoints' -- deploy/local/edge` → `admin` が **7 ルータ**、
  `web,websecure` が **1 ルータ**。

### 2.5 誤帰属の是正 —— 「admin entrypoint は平文 http」は `IADR-0103` の決定ではない

**`IADR-0103` を実測した。**

```
$ grep -n -e '50000' -e 'entrypoint' -e '平文' docs/adr/IADR-0103_*.md
（0 件）
```

同 ADR が扱うのは **`admin` という「ユーザー」**（realm への恒久定義・ツール別 claim 設計・ESO 後の rollout・
`argocd` DNS エイリアス・Vault の listing visibility）であって、**`admin` という「entrypoint」ではない**。
**同じ語だが別物である。**

**にもかかわらず、複数の文書が「admin entrypoint は平文 http」の根拠を同 ADR へ帰していた。**
`IADR-0103` を全走査して母集合を引いた（規則 7。出力を加工せずに読んだ）。

| 場所 | 扱い |
| --- | --- |
| `docs/adr/IADR-0220`（本 PR で新設） §関連 / §結果 / §起点 | **是正**（`Supersedes` は `IADR-0206` 決定 4 の後半だけにし、誤帰属の経緯を明記） |
| `docs/adr/IADR-0091` §決定 3 の 2026-08-16 追記 | **是正**（日付つき追記で帰属と陳腐化の両方を訂正。本文は書き換えない） |
| `docs/operations/local-sso-recovery-runbook.md` | **是正済み**（本 PR で当該行を書き換えた際に帰属ごと解消した。**ここが誤帰属の発生源**である） |
| `docs/adr/IADR-0206` 2 箇所（34 行 / 158 行） | **除外** —— **#834（PR #843）が編集中**であり、本 PR は同ファイルに 1 バイトも触らない |
| `docs/specs/20260816_issue-779_...` / `20260725_issue-354_...` | **除外**（確定済みの作業仕様書） |

**`IADR-0103` の本文は触らない。同 ADR は何も間違っていない。**

**教訓**: 誤帰属は**出典に当たらずに引き写す**ことで伝播する。本 PR の初稿は、書き換えた当の行から
`IADR-0103` を `Supersedes` へ引き写していた。**`Supersede` すると書く前に、その ADR を開いて
当該決定が実在することを確かめる。**

## 3. 対象範囲

- **対象**: `admin`（50000）entrypoint の TLS 終端、管理系 Ingress 4 ファイル（7 ルータ）の `spec.tls`、
  `platform-infra` / `argocd` namespace の葉証明書、Keycloak realm の redirect、
  区分 A の 16 ファイルの URL 追随、`scripts/k8s-local-up.test.js` の 2 試験の期待値反転、`IADR-0220`。
- **対象外**:
  - `deploy/local/edge/platform-frontend-ingress.yaml` の `spec.tls`（**#779 で TLS 済み**。二重に直さない）
  - 本番像の HTTPS 化（#780 / #782）
  - `IADR-0206` の条文（**#834**）
  - `src/ai-stock-trading`（向こうの issue）
  - 区分 B・C（上の除外理由）

## 4. 設計

### 4.1 `admin` entrypoint を TLS 終端にする

`deploy/local/edge/traefik-entrypoint.yaml` の `HelmChartConfig` に `additionalArguments` を足し、
**Traefik の引数として明示的に**次を渡す。

```text
--entryPoints.admin.http.tls=true
--entryPoints.web.http.redirections.entryPoint.to=websecure
--entryPoints.web.http.redirections.entryPoint.scheme=https
--entryPoints.web.http.redirections.entryPoint.permanent=true
```

**Helm chart の `ports.<name>.tls.enabled` / `ports.web.redirectTo` を使わない理由**は、同ファイルが既に
`expose` のスキーマについて注意している通り、**values のスキーマが chart バージョンで変わる**ためである。
`additionalArguments` は Traefik のコマンドライン引数へそのまま流れるので、chart 版に依存しない。

`web`(80) に**恒久リダイレクト**を入れるのは `NFR-11` の「**平文 HTTP を残さない**」に従うためである。
`admin`(50000) は TLS 終端になるので、**そこにはもう平文が無い**（平文で叩くとハンドシェイクが失敗する）。

### 4.2 管理系 Ingress 4 ファイルへ `spec.tls` を足す

`secretName` は **`IADR-0206` が安定させた `edge-tls` をそのまま使う**（`ADR-0047` 決定 2 の設計要件
「名前の安定」。**新しい名前を作らない**）。`hosts` は葉証明書の `dnsNames` と一致させるため
**`"*.localhost"`** と書く（`grafana.localhost` のようにホストを列挙すると `dnsNames` の
リテラルと一致しなくなる）。

### 4.3 葉証明書を namespace ごとに置く

**`spec.tls.secretName` は同じ namespace の Secret しか参照できない。** 管理系 7 ルータは 3 つの
namespace に散っている。

| namespace | ルータ | 葉証明書 |
| --- | --- | --- |
| `microservices-platform` | minio / wiki（＋ frontend） | **既存**（`tls/edge-certificate.yaml`） |
| `platform-infra` | grafana / headlamp / vault / qdrant | **追加**（同ファイルへ 2 つ目の `Certificate`） |
| `argocd` | argocd | **追加**（`tls/argocd-certificate.yaml`・条件付き apply） |

`argocd` namespace は `ARGOCD=1` の別 opt-in でのみ作られる。したがって
**`argocd-ingress.yaml` と同じ扱い**にする ——
`tls/kustomization.yaml` に含めず、`k8s-local-up.sh` が「`argocd` ns 存在時のみ」apply する（fail-safe）。

`issuerRef` は 3 件とも同じ `local-edge-ca`（`ClusterIssuer`）を指す。**CA 固有設定は `ClusterIssuer` に
閉じたまま**であり、`ADR-0047` 決定 2 の設計要件 3 点は崩れない。

### 4.4 URL の追随

区分 A の 16 ファイルで `http://<host>.localhost:50000` を `https://<host>.localhost:50000` にする。
**realm の `redirectUris` / `webOrigins` は置換であって追加ではない**（`NFR-11`「平文 HTTP を残さない」）。
ただし **`vault` client は既に https を併記済み**であり、http 側を落とすだけでよい。
**port-forward 用の `http://localhost:<port>` は本作業の対象外**（エッジを経由しない別経路であり、
`:50000` を含まないため軸 1 に入らない）。

### 4.5 試験の反転

| 現在の試験 | 反転後 |
| --- | --- |
| `admin:50000 の Ingress には spec.tls を足さない` | **4 ファイルとも `spec.tls`（`secretName: edge-tls`・`hosts` が `dnsNames` に含まれる）を持ち、`admin` entrypoint に `--entryPoints.admin.http.tls=true` がある** |
| `http(80) 経路を残している（恒久リダイレクトを足さない）` | **`web`→`websecure` の恒久リダイレクトが入っている（平文 HTTP を残さない）** |

**反転した試験は変異試験で着地させる** —— 設定を平文へ一時的に戻し、
**試験が実際に落ちること**を判定行で確認したうえで復元し、`git diff` で復元を確かめる。

## 5. 受け入れ基準

- [x] `--entryPoints.admin.http.tls=true` が `traefik-entrypoint.yaml` に在る
- [x] 管理系 4 ファイル（7 ルータ）が `spec.tls`（`secretName: edge-tls`）を持つ
- [x] `platform-infra` / `argocd` の葉証明書が在り、`argocd` 側は ns 存在時のみ apply される
- [x] realm の `http://*.localhost:50000` が **0 件**になる
- [x] 区分 A の 16 ファイルで `http://…localhost:50000` が **0 件**になる
- [x] 反転した 2 試験が、平文へ戻すと**落ちる**（変異試験の生ログを PR に残す）
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
- [x] `IADR-0220` を起こし、`docs/adr/README.md` の索引へ追加する

## 6. テスト方針

`scripts/k8s-local-up.test.js` の既存の型（マニフェストを読んで正規表現で固定する・外部依存ゼロ）に従う。
CI に `kustomize build` / `kubeconform` を走らせるジョブは無い（#783 の領分）ため、静的検査で担保する。
`scripts/check-realm-constraints.js` の必須 URL 表も https へ揃え、realm 側と二重化しない。

## 6.1 ★ 検証の限界 —— 実 TLS ハンドシェイクは確認していない

**本作業の担保は静的検査（`k8s-local-up.test.js` 75 件・うち本件が 3 件）と変異試験 5 通りだけである。
「HTTPS 化が動作することを検証した」とは読まないこと。**

| 確かめたこと | 手段 |
| --- | --- |
| マニフェストに TLS 設定が在る | `k8s-local-up.test.js`（正規表現でマニフェストを読む） |
| 7 ルータすべてが `spec.tls(edge-tls)` を持つ | 同上（**ドキュメント単位**で見る。ファイル単位では 1 件の平文化を見逃した） |
| 3 つの namespace に葉証明書が在る | 同上 |
| 壊すと試験が落ちる | 変異試験 5 通り（生ログは PR 本文） |

| **確かめていないこと** | 理由 |
| --- | --- |
| **実 TLS ハンドシェイクが成立するか** | 作業環境に k8s / k3d が無く、クラスタを立てていない |
| **CI が代わりに確かめてくれるか** | **確かめない。** CI の `k8s-local-up-smoke` ジョブは **15 秒で終わっており、実クラスタを立てていない**（静的検査である） |
| ブラウザ・`curl` が証明書を検証できるか | 信頼ストアは環境の側にある（[[IADR-0206]] の「検出しないこと」と同じ） |
| OIDC ラウンドトリップが https で最後まで通るか | 上と同じ。**realm の redirect を書き換えた以上、ここは実機で踏むまで未知である** |

**つまりローカルでも CI でも実 TLS ハンドシェイクは未確認である。**
実機確認は `LOCALEDGE=1 bash scripts/k8s-local-up.sh` を実走できる環境で行う必要がある。

## 7. 計画書との差異

- 差異: **なし**。`NFR-11`（平文 HTTP を残さない・環境を問わない）と `ADR-0047`（経路B も対象・
  selfsigned CA を許容・設計要件 3 点）に**そのまま従う**。

## 8. 未決事項

- **`IADR-0206` 決定 4 の後半（`http` 経路を残す・恒久リダイレクトを足さない）は本作業が実体で覆す。**
  **条文の側は #834 が持つ**ため、本作業では `IADR-0206` の本文を触らない。
  **`IADR-0220` が「決定 4 を Supersede する」と宣言する**ことで、条文と実体が逆を向いたままにならないようにする。
