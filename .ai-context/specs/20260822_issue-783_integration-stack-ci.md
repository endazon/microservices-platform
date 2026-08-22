---
title: 作業仕様書 — 統合スタックを CI で起こす経路を作り、「起きていないのに緑」を機械で止める
type: spec
status: draft
related_ids:
  - NFR
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0007（CI/CD）"
  - "ADR-0021（エッジ・実行基盤）"
related_adrs:
  - IADR-0091
  - IADR-0206
  - IADR-0227
  - IADR-0232
  - IADR-0240
  - IADR-0243
issue: "#783"
related_issues:
  - "#442"
  - "#466"
  - "#780"
  - "#948"
---

# 作業仕様書: 統合スタックを CI で起こす経路（#783 後半・やること②）

本書は **#783 の「やること②」＝統合スタックを CI ランナー上で起こす経路**を対象とする。
やること①（chart / overlay の検証ジョブ・スキーマ突合・必須チェック昇格）は
`.ai-context/specs/20260821_issue-783_deploy-manifest-ci.md` が扱い、PR #878 と PR #925 で着地済みである。

## 起点となる計画書（トレーサビリティ）

- 機能要求: 該当なし（**NFR**。CI 基盤＝メタ作業であり、計画側に当たる番号が無い。
  `traceability.repo.md`「メタ作業（規約・検査器・文書統制）は代表例で、製品の作業にも当たる番号が無いことはある」）
- 関連計画 ADR: `ADR-0007`（CI/CD）／ `ADR-0021`（エッジ・実行基盤）
- 親 issue: **#442**（エッジ・実行基盤・CI/CD の再構築）の子 5
- 開ける先: **#466**（E2E スモークを統合スタックで CI 実行）

## 1. 着手前の再検証 —— 依存は解けている（走査で確認した）

#783 は `blocked` ラベルを持ち、依存として **#779**・**#780** を挙げている。前任セッションは
「後半は #466 が実ブラウザ OIDC ログインを要求するため #780 に依存し得る。**この切り分けは未確定**」
と引き継いでいた。**自分で `origin/develop`（`6cfdb94b`）の中身を読んで裏を取った。**

| 依存 | 状態 | 実測した根拠 |
| --- | --- | --- |
| #779（エッジ TLS 終端基盤） | CLOSED | `deploy/local/edge/tls/` が在り、`k8s-local-up.sh` の `LOCALEDGE=1` が cert-manager → `edge-tls` を発行する |
| #780（Keycloak をエッジへ・issuer https 化） | **OPEN だが本作業に要る部分は着地済み** | 下記 |

**#780 のうち本作業が要る部分は develop に載っている。**

- `deploy/local/infra/keycloak.yaml`: `KC_HOSTNAME_URL` がエッジ issuer の単一情報源
- `deploy/local/edge/kustomization.yaml`: **`keycloak-ingress.yaml` が overlay に入っている**（[IADR-0227]）
- `scripts/k8s-local-up.sh` の `LOCALEDGE=1` ブロック: `deploy/local/aliases/coredns-edge-hosts.yaml` を
  apply して `coredns` を rollout restart する＝**pod からも `*.localhost` が引ける**
- `scripts/verify-oidc-edge-flow.sh`: 手順A（hosts 追記 ＋ port-forward）前提から脱却済み（[IADR-0243]）

**したがって PR #522 が「本 issue の実体」と名指した障害（CI では手順A を用意できない）は消えている。**
#780 に残っている作業（`helm upgrade` のフル反映・6 ツールのブラウザログイン検証・
`REQUIRED_CLIENT_URLS` の扱い）は、**本作業の前提ではない**（#780 のコメントに整理済み）。

## 2. 実測（GitHub ホストランナー・probe ブランチ `probe/783-runner-capability`）

**着手前に、律速になり得る未知をすべてランナー上で測った。** probe は 3 ラウンド走らせた。
🔴 **probe の各ステップは `set +e` で走らせている。ジョブが success なのは「最後まで走った」という意味だけであり、
合否は各ステップが印字する `EXIT_*` で判定している。**

### 2.1 ランナーの素性（run `32554145102`）

`ubuntu-24.04` / **4 vCPU** / **15 GiB RAM** / **145 GB ディスク（87 GB 空き）** / Docker 28.0.4。

### 2.2 🔴 律速だった未知 —— `*.localhost` はランナーで解決する

```
--- getent ahostsv4 keycloak.localhost
127.0.0.1       STREAM keycloak.localhost
EXIT_ahostsv4_keycloak.localhost=0

keycloak.localhost: 127.0.0.1    -- link: lo
-- Data from: synthetic
EXIT_resolvectl=0
```

`hosts: files dns` ＋ systemd-resolved（stub `127.0.0.53`）で、`.localhost` は **RFC 6761 の合成応答**として
`127.0.0.1` に返る。`grafana.localhost` も 2 段の `a.b.localhost` も同じ（いずれも EXIT=0）。
**ここが割れていたら設計をやり直す必要があった。**

### 2.3 PR #522 の記録の訂正 —— hosts 追記は CI でできる

```
EXIT_append=0            # echo "127.0.0.1 ..." | sudo tee -a /etc/hosts
EXIT_lookup=0
EXIT_after_restore=2     # 復元も確認した
```

GitHub ホストランナーは passwordless sudo を持つ。**「CI ではどちらも用意できない」は片方が誤りだった。**
**本作業の判断は変わらない**（エッジ issuer 経路のほうが正しい）が、
**誤った記録が独り歩きすると次の人が同じ壁を信じる**ため #466 へ訂正コメントを残した。

### 2.4 スタックの起動（run `32554340800`）

**`LOCALEDGE=1 K8S_LOCAL_RUNTIME=k3d bash scripts/k8s-local-up.sh` が CI で完走した（EXIT=0 / 400 秒）。**

| 段 | 秒 |
| --- | ---: |
| `[1/7]` `k3d cluster create`（80 / 443 / 50000 を 127.0.0.1 へ公開） | 22 |
| `[2/7]` イメージ **16 本**のビルド | **244** |
| `[2/7]` `k3d image import`（16 本） | **37** |
| `[3/7]` secret ＋ realm ConfigMap（AST realm 同梱） | 1 |
| `[4/7]` infra 適用 ＋ rollout 待ち | 61 |
| `[5/7]` アプリ secret | 1 |
| `[6/7]` `helm upgrade --install` | 1 |
| `[7/7]` ExternalName エイリアス | 0.2 |
| `[opt-in]` エッジ（Ingress 8 本 ＋ coredns-custom ＋ cert-manager ＋ edge-tls） | 33 |
| **合計** | **400** |

ディスクは 74 GB 使用 / 71 GB 空きで、**上限に当たっていない**。

- **pod からの `keycloak.localhost` 解決**: `NSLOOKUP_EXIT=0`。既存の in-cluster 名も無傷（`REGRESSION_EXIT=0`）
- **issuer**: `KC_HOSTNAME_URL=https://keycloak.localhost` がデプロイに載り、
  discovery の `issuer` が `https://keycloak.localhost/realms/platform` で一致（`EXIT_edge_disco=0`）
- **OIDC 導線**: SPA → `config.js` → ログインフォーム → 認可コード → PKCE → `access_token` →
  クレーム（`iss` / `preferred_username` / `clearance=restricted` / `department=engineering`）が
  **すべて PASS**（`verify-oidc-edge-flow.sh` の PASS 9）

### 2.5 🔴 本作業の中心になる発見 —— `EXIT=0` は readiness の証明にならない

同じ実行で `/bff/*` は **502 が 5 件**（`PASS 9 / FAIL 5` / `EXIT_verify=1`）だった。
**これは BFF の欠陥ではない。** up が EXIT=0 で戻った時点の pod:

```
bff-service-f97557678-44q7z          0/1  Running  1 (16s ago)
aianalysis-service-54d648fc47-rlfkf  0/1  Running  0
minio / wiki-js                      0/1  Running  0
Warning  Unhealthy  pod/bff-service  Liveness probe failed: ... connection refused
```

**`helm upgrade --install` は `--wait` を付けていない**ため、スクリプトは**アプリ pod の起動を待たずに戻る**。

> 🔴 **これが本作業の設計の中心である。**
> **CI ジョブが自前の readiness ゲートを持たなければ、「立ち上がっていないスタックに緑を返すジョブ」になる。**
> #783 が最も避けたい形そのものであり、しかも**無音で起きる**（up は EXIT=0 のまま）。

### 2.6 🔴 「ローカルで通ったから CI でも通る」が成立しない実例

エッジ overlay が適用する Traefik の `HelmChartConfig`（admin:50000 entrypoint・[IADR-0091]）が
**k3d 上では効かなかった。**

```
kube-system  helm-install-traefik-7jlcc  0/1  CrashLoopBackOff  1 (16s ago)
traefik  LoadBalancer  10.43.183.188  80:31891/TCP,443:32095/TCP     ← 50000 が無い
```

- 手元の Rancher Desktop: **k3s v1.35.4+k3s1** —— 同じ overlay が効いている（admin:50000 が在る）
- k3d 5.7.4 の既定: **k3s v1.30.4+k3s1**

**しかも up は EXIT=0 のまま返る＝静かに素通りする。**

**根因まで取った**（run `32554867883` の `helm-install-traefik` ログ）:

```
+ helm_v3 upgrade ... traefik https://10.43.0.1:443/static/charts/traefik-25.0.3+up25.0.0.tgz \
    --values /config/values-01_HelmChart.yaml --values /config/values-10_HelmChartConfig.yaml
Error: UPGRADE FAILED: template: traefik/templates/service.yaml:21:12:
executing "traefik/templates/service.yaml" at <eq $config.expose true>:
error calling eq: incompatible types for comparison
```

🔴 **k3d 側の問題ではない。同梱される traefik chart のスキーマの問題である。**

`deploy/local/edge/traefik-entrypoint.yaml` は `expose` を **map**（`expose: {default: true}`）で書いている。
これは traefik chart **26 以降**の書式である。k3s v1.30.4 が同梱するのは **traefik 25.0.3** で、
そこでは `expose` は **bool** であり、`eq $config.expose true` が型不一致で落ちる。

**つまり本リポジトリの宣言は、k3s のバージョンに暗黙に依存している。**
そして `k8s-local-up.sh` は `kubectl apply` の成否しか見ないため、**適用は成功し、反映は失敗し、EXIT=0 で返る。**

### 2.8 pin 候補の突合（run `32555313631`）

4 組を並列で突合した（イメージビルド無し・1 組あたり 1 分未満）。

| 組 | 実際の k3s Server | `admin=50000` | 判定 |
| --- | --- | --- | --- |
| k3d 5.7.4 / 既定 | v1.30.4+k3s1 | — | **測れていない**（下記） |
| k3d 5.7.4 / `rancher/k3s:v1.31.5-k3s1` | v1.31.5+k3s1 | **有り**（`ADMIN_ENTRYPOINT_FOUND=1`） | ✅ |
| k3d 5.8.3 / 既定 | v1.31.5+k3s1 | **有り** | ✅ |
| k3d 5.8.3 / `rancher/k3s:v1.35.4-k3s1` | v1.35.4+k3s1 | **有り** | ✅ |

> 🔴 **対照群（k3d 5.7.4 / 既定）は assert に到達していない。** その時点で `svc/traefik` が未作成で、
> ステップ末尾のコマンドが非 0 を返してジョブが落ち、以降のステップが skip された。
> **「対照群でも admin が立たないことを、本ラウンドで確かめた」とは言えない。**
> **v1.30.4 で立たないことの根拠は §2.6（run `32554867883` の完全な実行）であり、そちらは
> `helm-install-traefik` の型不一致エラーと `web=80 / websecure=443` のみという出力まで取れている。**
>
> （probe のステップ設計の不備であり、測定の失敗としては 3 度目である。ステップの合否を
> 「末尾コマンドの終了コード」に委ねたのが原因。実装するジョブでは各 assert を明示的に判定する。）

**採用候補は k3d **v5.8.3** ＋ `--image rancher/k3s:v1.35.4-k3s1`。**
手元の Rancher Desktop（v1.35.4+k3s1）と**同じ k3s になる**ため、§2.6 の「ローカルで通ったから CI でも通る
が成立しない」という乖離そのものを消せる。クラスタ作成は 30 秒（他候補は 21〜22 秒）で、差は無視できる。

### 2.7 readiness ゲートの費用と、Ready 後の導線（run `32554867883`）

**up が EXIT=0 で戻ってから全 pod が Ready になるまで — 33 秒。**

```
EXIT_wait_platform-infra=0            SECONDS_wait_platform-infra=1
EXIT_wait_microservices-platform=0    SECONDS_wait_microservices-platform=32
SECONDS_readiness_total=33
```

**安い。** ゲートを置かない理由が費用にはない。

Ready を待ってから `verify-oidc-edge-flow.sh` を再実行した結果、**502 は消えた**（§2.5 の 502 が
「未 Ready を測っただけ」だったことの裏取りでもある）。

```
PASS  /bff/documents → 200 []
FAIL  /bff/dashboard/summary → 401
PASS  /bff/datasources → 200 []
FAIL  GET /bff/documents（無トークン）→ 401（現行設計は 200。#458 適用済みなら本判定を更新する）
PASS  POST /bff/documents（無トークン）→ 401
結果: PASS 12 / FAIL 2      EXIT_verify=1
```

**残った FAIL 2 件は性質が違う。混ぜないこと。**

1. **`/bff/dashboard/summary` → 401** は **#948 が CI で再現した**もの。
   ただし **`/bff/datasources` は CI では 200** で再現しなかった（#948 を訂正済み）。
2. **`GET /bff/documents`（無トークン）→ 401** は **欠陥ではなく、検証スクリプトの期待値が古い**。
   スクリプト自身が「現行設計は 200。**#458 適用済みなら本判定を更新する**」と書いており、
   実測はまさにその状態である。**段 2 で PASS 件数を baseline 化する前に、この期待値を更新する必要がある**
   （更新しないと baseline に恒久的な FAIL 1 件を焼き付けることになる）。

## 3. 対象範囲

### 対象（段 1・本作業＝ #783 を閉じる）

- **統合スタックを CI で起こすワークフロー**を新設する（k3d ＋ `k8s-local-up.sh`）
- **自前の readiness ゲート**を置き、「起きていないのに緑」を止める
- **fail-closed の門**（走査 0 件で緑にしない／ツール不在で素通りしない／pod 数を数える）
- **エッジ・issuer の最小 assert**（#780 の成果の退行を止める）
- **「PR ゲートの上限」を定義し**、nightly へ分離する判断を明示する
- **変異試験**（壊すと実際に落ちること・**ゲートを外すと素通りすること**の両方を実測する）
- 上記の判断を記録する **新規 IADR**（番号は**マージ直前に develop の最大 ＋1 を実測して取り直す**）

### 対象外（段 2 ＝ #466 を開ける・別 PR）

- `verify-oidc-edge-flow.sh` を CI ジョブに載せ、**PASS 件数を baseline 化**して退行を止めること
- 主要導線（ログイン → 検索 → 文書詳細 → ABAC 不可視）のスモーク本体
- **#948**（有効トークンで `/bff/dashboard/summary`・`/bff/datasources` が 401）の解決
  —— これが残る限り「認証後に**結果が出る**」は固定できない

> **なぜここで切るか。** CLAUDE.md は「実装判断の記録は実装変更と同一 PR に置く」と定めているため、
> **IADR だけの PR は規約に反する**。したがって境界は「IADR / ジョブ」ではなく
> **「#783 を閉じる / #466 を開ける」**に引く（利用者裁定 2026-08-22）。

### 明示的にスコープ外

- **Istio の導入**（#782）。本作業のエッジは Traefik である（[IADR-0091]）
- **`images.yml` からの成果物再利用**。同ワークフローは**レジストリへ push していない**（ビルド検証のみ）ため、
  244 秒を「pull で置き換える」ことは現在の配線ではできない。**別の判断が要るので広げない**

## 4. 設計

### 4.1 実行基盤: k3d（compose ではない）

`k8s-local-up.sh` がそのまま走る。**経路を二重実装しない**のが最大の理由である。
実測で EXIT=0 / 400 秒を確認済み（§2.4）。

### 4.2 🔴 k3s イメージを pin する

**理由は「バージョンを揃えたいから」ではない。揃っていないことが静かに素通りするからである**（§2.6）。
admin:50000 の entrypoint が k3d では立たず、しかも up は EXIT=0 で返った。
**pin しなければ、k3d 側の既定が動いた瞬間に、誰も気付かないまま検証範囲が変わる。**

### 4.3 起動契機: nightly ＋ `workflow_dispatch` ＋ develop への push

`integration.yml`（[IADR-0232] 決定 3 改定 3）と同型に揃える。同ワークフローが develop push を含める理由
（「日次だけだと朝までの間に別の PR が積み上がって原因の切り分けが難しくなる」）は本作業にも当てはまる。

**「PR ゲートの上限」は現在どこにも定義されていない。** 定義されていないものを根拠に「載らない」とは言えないため、
**本作業の IADR で定義する**（#783 の「実行時間が PR ゲートの上限を超えるなら nightly へ分離する」を
判定可能にするために要る）。

### 4.4 fail-closed の門

| 門 | 何を止めるか |
| --- | --- |
| G1 **readiness ゲート** | up が EXIT=0 でも pod が Ready でないまま先へ進むこと（§2.5） |
| G2 **0 件で緑にしない** | 走査した Deployment が 0 件のときにゲートが失敗を 1 件も返さないこと |
| G3 **ツール不在で素通りしない** | kubectl / curl が入っていないのに検証を飛ばして緑になること。**抜け道の env を置かない** |
| G4 **エッジ・issuer の assert** | `keycloak-edge` Ingress と discovery の `issuer` が失われても気付かないこと |
| G5 **entrypoint の assert** | admin:50000 が立っていないことが静かに素通りすること（§2.6） |
| G6 **pod 側の名前解決** | クラスタ内で `*.localhost` が引けなくなっても、ランナーからの curl（G4）は通ってしまうこと |

> 🔴 **［2026-08-22 訂正 / #783］G2 の根拠を書き直した。**
> 当初は「`kubectl wait --for=condition=Ready pods --all` は**対象が 0 件のとき成功する**から G2 が要る」
> と書いていた。**実測は逆だった。**
>
> ```
> error: no matching resources found
> WAIT_EXIT_platform-infra=1
> ```
> （run 32556579646 / `empty-cluster`。namespace を作っただけで pod が 1 件も無い状態）
>
> **したがって当初の根拠は成り立たない。** G2 が要る本当の理由は
> **ゲートが単独で完結する判定でなければならない**ことである。G2 が無いと `evaluateDeployments([])` は
> 失敗を 1 件も返さないため、**アプリのサービスが 1 つもデプロイされていない状態でゲートが緑になる**。
> 実測でもそうなっている —— 同 run の `control-pinned`（infra とエッジは健全、`microservices-platform` は空）で
> **ゲートが返した失敗は `[G2]` の 1 件だけ**だった。**捕まえたのは G2 だけである。**
>
> 待つステップは検査ではなく、弱められても消されても誰も気づかない。だから判定をそこに置かない。

### 4.5 変異試験（設計段階の計画。実測は実装時に差し替える）

| # | 変異 | 落ちるべき門 |
| --- | --- | --- |
| M1 | `deploy/local/edge/keycloak-ingress.yaml` を overlay から外す | G4 |
| M2 | `coredns-edge-hosts.yaml` の rewrite 先を存在しない svc へ | pod DNS assert |
| M3 | `KC_HOSTNAME_URL` を in-cluster 値へ戻す | G4（issuer 突合） |
| M4 | いずれか 1 つの Deployment の image を存在しないタグへ | G1（readiness） |
| M5 | **ゲートを外したら素通りする**ことを示す（下記のとおり 2 つに割れる） | **緑になることを実測する** |
| M6 | pod が 1 件も無い状態で走らせる | G2（0 件で緑にしない） |
| M7 | k3s の pin を外す（v1.30.4） | G5（admin=50000） |

> 🔴 **M5 は他と向きが逆である。** 他は「壊すと落ちる」を示すが、M5 は「**ゲートを外すと素通りする**」を示す。
> **両方が無いと、ゲートが機能していることの証明にならない**（落ちたのがゲートのおかげだと言えない）。
>
> 🔴 **［2026-08-22 追記 / #783］M5 の当初の書き方は不正確だった。** 当初は「M4（image を壊す）を
> 当てたまま readiness ゲートを外すと緑になる」と書いたが、**M4 は pod が生成される限り
> `kubectl wait` が落とす**ため、ゲートが無くてもジョブは赤になる可能性が高い。**M5 は 2 つに割れる。**
>
> - **M5a（M4 に対して）**: `wait` とゲートのどちらが捕まえたのかを**両方の EXIT を並べて**測る。
>   `wait` が捕まえるなら「ゲートが唯一の防壁」とは言えない —— **そう書かないために測る。**
> - **M5b（pod が 0 件のとき）**: 当初は「`wait --all` が 0 件で成功するのでゲートだけが落ちる」と
>   見込んでいたが、**実測では `wait` も落ちた**（`no matching resources found` / exit 1）。
>   **M5b もゲート単独の証明にはならない。**
>
> 🔴 **［2026-08-22 訂正 / #783］したがって「ゲートを外すと素通りする」を示せるのは M4 でも 0 件でもない。**
> **示せるのは「pod はすべて Ready なのにエッジ・issuer・名前解決・entrypoint が壊れている」場合**であり、
> M1（Ingress 欠落）・M2（coredns 破壊）・M7（pin 外し）がそれに当たる。
> **その 3 ケースで `kubectl wait` が通ることを実測する**（第7ラウンド）。それが M5 の正しい形である。

## 5. 受け入れ基準（#783 の受け入れ基準のうち、後半に当たるもの）

- [ ] 統合スタックが CI ランナー上で起動する（**実行コマンドと出力の証跡がある**）
- [ ] **起動失敗・未 Ready のときに緑にならない**（変異試験 M4 で実測）
- [ ] **readiness ゲートを外すと素通りする**ことを実測し、ゲートが効いていることを示す（M5）
- [ ] **0 件・ツール不在で緑にならない**（M6・G3）
- [ ] 実行時間を実測し、**「PR ゲートの上限」を定義**したうえで nightly 分離の判断を明示する
- [ ] `.github/workflows/` の変更で**起動条件・必須チェックが変わっていない**ことを確認した記録がある
- [ ] 新規 IADR に §2.5（EXIT=0 は readiness の証明にならない）と §2.6（k3s の乖離が静かに素通りする）を記録する

## 6. 宣言ファイル領域

- `.github/workflows/`（新規ワークフロー 1 本。既存ファイルの `on:` は触らない）
- `scripts/`（readiness ゲートと assert を置く場合）
- `.ai-context/adr/`（新規 IADR）・`.ai-context/specs/`（本書）
- `docs/ai-workflow.md`（**必須チェックの表は触らない**。本ジョブは PR で起動しないため必須にできない
  —— 「その PR で起動しないことがあるチェックを必須にしてはならない」に該当する）

**並列作業との交差**: 2026-08-22 時点で open な PR は #873（`automation/changelog-update-develop`）のみで、
`.github/workflows/` ・ `scripts/` ・ `deploy/` に触れていない（実測）。**交差は無い。**

## 7. 未決事項

1. ~~admin:50000 entrypoint をどう扱うか~~ → **解決（§2.8）**。
   **k3d v5.8.3 ＋ `rancher/k3s:v1.35.4-k3s1` へ pin し、`admin=50000` の存在を assert する**（案 a）。
   手元と同じ k3s になるため乖離自体が消える。**「50000 が立っていること」は G5 として門にする。**
2. ~~readiness ゲートの実測値~~ → **解決（33 秒）**。§2.7。
3. ~~#948 の 401 が CI で再現するか~~ → **解決。部分的に再現した**（§2.7）。
   `/bff/dashboard/summary` は再現、`/bff/datasources` は再現せず。#948 を訂正済み。
4. **`verify-oidc-edge-flow.sh` の期待値の陳腐化**（§2.7 の FAIL 2 件目）。
   段 2 で baseline 化する前に更新が要る。**段 1 のスコープ外**だが、段 2 の前提として記録しておく。

## 8. 計画書との差異

- 差異: なし。#783 のスコープ（「統合スタックを CI ランナー上で起こす経路を用意する」
  「実行時間が PR ゲートの上限を超えるなら nightly へ分離する」）に忠実である。

## 9. probe の後始末

計測に使った `probe/783-runner-capability` ブランチと `.github/workflows/probe-783-*.yml` は
**本作業のブランチには持ち込まない**（`ci/issue-783-integration-stack-ci` は `origin/develop` から切ってある）。
**段 1 が着地するまで probe ブランチは残す**（測定の証拠がそこにあるため。利用者裁定 2026-08-22）。
削除はリモートブランチへの破壊的操作なので、**実行前に必ず報告する**。
