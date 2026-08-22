---
title: 作業仕様書 — 統合スタックを CI で起こす経路を作り、「起きていないのに緑」を機械で止める
type: spec
status: in-progress
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

### 2.7 pin 候補の突合（run `32555313631`）

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

### 2.8 readiness ゲートの費用と、Ready 後の導線（run `32554867883`）

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

- [x] 統合スタックが CI ランナー上で起動する（§2.4。`EXIT=0` / 400 秒。段ごとの秒数まで実測）
- [x] **起動失敗・未 Ready のときに緑にならない**（§10.2 `broken-image-wait-vs-gate`。`[G1]` で落ちる）
- [x] **ゲートを外すと素通りする**ことを実測した（§10.3 の M1 / M2 / M7。**`WAIT_EXIT=0` / `GATE_EXIT=1`**）
      —— ただし**当初の M5 の形（M4／0 件）では示せなかった**ことも併せて記録した
- [x] **0 件・ツール不在で緑にならない**（§10.2 `empty-cluster` ＝ `[G2]`／G3 は抜け道の env を持たないことを
      `scripts.repo.test.js` が突合）
- [x] 実行時間を実測し、**「PR ゲートの上限」を定義**したうえで nightly 分離を選んだ（[IADR-0247] 決定 3）
- [x] `.github/workflows/` の変更で**起動条件・必須チェックが変わっていない**（§10.7）
- [x] 新規 IADR に §2.5（EXIT=0 は readiness の証明にならない）と §2.6（k3s の乖離が静かに素通りする）を記録した

## 6. 宣言ファイル領域

- `.github/workflows/`（新規ワークフロー 1 本。既存ファイルの `on:` は触らない）
- `scripts/`（readiness ゲートと assert を置く場合）
- `.ai-context/adr/`（新規 IADR）・`.ai-context/specs/`（本書）
- `docs/ai-workflow.md`（**必須チェックの表は触らない**。本ジョブは PR で起動しないため必須にできない
  —— 「その PR で起動しないことがあるチェックを必須にしてはならない」に該当する）

**並列作業との交差**: 2026-08-22 時点で open な PR は #873（`automation/changelog-update-develop`）のみで、
`.github/workflows/` ・ `scripts/` ・ `deploy/` に触れていない（実測）。**交差は無い。**

## 7. 未決事項

1. ~~admin:50000 entrypoint をどう扱うか~~ → **解決（§2.7）**。
   **k3d v5.8.3 ＋ `rancher/k3s:v1.35.4-k3s1` へ pin し、`admin=50000` の存在を assert する**（案 a）。
   手元と同じ k3s になるため乖離自体が消える。**「50000 が立っていること」は G5 として門にする。**
2. ~~readiness ゲートの実測値~~ → **解決（33 秒）**。§2.8。
3. ~~#948 の 401 が CI で再現するか~~ → **解決。部分的に再現した**（§2.8）。
   `/bff/dashboard/summary` は再現、`/bff/datasources` は再現せず。#948 を訂正済み。
4. **`verify-oidc-edge-flow.sh` の期待値の陳腐化**（§2.8 の FAIL 2 件目）。
   段 2 で baseline 化する前に更新が要る。**段 1 のスコープ外**だが、段 2 の前提として記録しておく。

## 8. 計画書との差異

- 差異: なし。#783 のスコープ（「統合スタックを CI ランナー上で起こす経路を用意する」
  「実行時間が PR ゲートの上限を超えるなら nightly へ分離する」）に忠実である。

## 9. probe の後始末

計測に使った `probe/783-runner-capability` ブランチと `.github/workflows/probe-783-*.yml` は
**本作業のブランチには持ち込まない**（`ci/issue-783-integration-stack-ci` は `origin/develop` から切ってある）。
**段 1 が着地するまで probe ブランチは残す**（測定の証拠がそこにあるため。利用者裁定 2026-08-22）。
削除はリモートブランチへの破壊的操作なので、**実行前に必ず報告する**。

---

## 10. 実測結果（実装後）

すべて GitHub ホストランナー上の実測である。**probe の各ステップは合否を明示的に判定させ、
ステップ末尾コマンドの終了コードには委ねていない**（第4ラウンドでその形の誤読を踏んだため）。

### 10.1 ローカルで完結する変異試験（6 本）

**いずれも「変異が当たったこと」を先に示してから EXIT を読んでいる。**

| # | 変異 | 当たった証跡 | 結果 |
| --- | --- | --- | --- |
| L1 | `check-stack-ready.js` の 0 件判定を外す | backup との `diff` に 1 行 | `EXIT=1`「0 件が失敗になっていない」 |
| L2 | issuer 比較を `startsWith` へ緩める | 同上 | `EXIT=1`「末尾スラッシュの差を通してしまっている」 |
| L3 | `k8s-local-up.sh` の `K3S_IMAGE` 対応を削除 | 同上（3 行） | `EXIT=1`「`--image` が付いていない」 |
| L4 | ワークフローから門の本走査を削除 | 同上（2 行） | `EXIT=1`「門の本走査を呼んでいない」 |
| L5 | `K3S_IMAGE` を `latest` へ | 同上 | `EXIT=1`「pin されていない」 |
| L6 | ワークフローへ `pull_request` を足す | 同上 | `EXIT=1`「必須チェック表との整合が崩れる」 |

各変異のあと復旧を確認し、`591 tests passed` / `self-test OK: 9 件` へ戻ることを実測した。

> 🔴 **L1 の初回は変異が当たっていなかった。** `sed` の区切り文字が `||` と衝突していた。
> **`git diff` が空だったので気づいたが、その `git diff` 自体が未追跡ファイルには効かない。**
> 以後は backup との `diff` で判定している。**証明力のない変異を「検出漏れ」と読まないこと。**

### 10.2 生きたクラスタでの変異試験（run 32556579646 / 32556863348）

| ケース | 期待 | 結果 |
| --- | --- | --- |
| `full-baseline`（正の対照） | **ゲートが通る** | ✅ `GATE_EXIT=0`。Deployment **21 件** available（infra 6 ＋ app 15）、Pod 24 件を判定 |
| `control-pinned`（対照） | G4/G5/G6 が出ない | ✅ 4 種すべて「出ていない」 |
| `empty-cluster` | `[G2]` | ✅ |
| `no-keycloak-ingress` | 「Ingress が無い」 | ✅ |
| `broken-coredns` | `[G6]` | ✅ |
| `unpinned-k3s-1.30.4` | `[G5]` | ✅ `admin=50000 が無い（実際: web=80 / websecure=443）` |
| `issuer-reverted-fixed` | 「issuer が一致しない」 | ✅（初回は変異が当たらず。§10.4） |
| `broken-image-wait-vs-gate` | `[G1]` | ✅ ただし `WAIT_EXIT=1`（§10.3） |

**`full-baseline` が最重要である。** これが無ければ「落ちることしか示していない」検査になる。

### 10.3 🔴 M5 は成り立たなかった —— 自分の主張の範囲が実測で狭まった

作業仕様書 §4.5 の当初の M5「M4 を当てたまま readiness ゲートを外すと緑になる」は**否定された**。

| 壊し方 | `kubectl wait` | ゲート |
| --- | --- | --- |
| image を存在しないタグへ（M4） | **EXIT=1**（捕まる） | EXIT=1 `[G1]` |
| pod が 1 件も無い | **EXIT=1**（`error: no matching resources found`） | EXIT=1 `[G2]` |

**どちらも `wait` で捕まる。** したがって**「ゲートが唯一の防壁である」とは書けない。**
ゲートが単独で要ることを示せるのは、**pod がすべて Ready なのにエッジ・issuer・名前解決・
entrypoint が壊れている場合**だけである。**それを測り直した**（run 32557491427）。

| ケース | 壊し方 | `kubectl wait` | ゲート | 判定 |
| --- | --- | --- | --- | --- |
| M1 | `keycloak-edge` Ingress を overlay から外す | **EXIT=0（通る）** | EXIT=1「Ingress が無い」 | **ゲートだけが捕まえた** |
| M2 | `coredns-custom` の rewrite 先を存在しない svc へ | **EXIT=0（通る）** | EXIT=1 `[G6]` | **ゲートだけが捕まえた** |
| M7 | k3s の pin を外す（v1.30.4） | **EXIT=0（通る）** | EXIT=1 `[G5]` | **ゲートだけが捕まえた** |

いずれも `platform-infra` の pod は**すべて Ready**（`kubectl wait --for=condition=Ready pods --all` が EXIT=0）で、
**それでもエッジ・名前解決・entrypoint は壊れている**。

> 🔴 **これが「ゲートを外すと素通りする」の実測である。**
> **M5 の当初の書き方（image を壊す／pod を 0 件にする）では示せなかった。**
> 変異試験をしたから主張の範囲が狭まり、**「ゲートが唯一の防壁だ」と一般化せずに済んだ。**
> 正確には —— **ゲートが唯一の防壁になるのは、pod の健康状態に現れない壊れ方に対してだけ**である。

### 10.4 🔴 変異が当たらなかった 3 件（いずれも probe の設計ミスであり、検出漏れではない）

| # | 何が起きたか | 何が原因か |
| --- | --- | --- |
| P1 | 第4ラウンドの対照群が assert に到達しなかった | ステップ末尾の `grep` が非 0 を返しジョブが落ちた。**合否を末尾コマンドに委ねていた** |
| P2 | `issuer-reverted` が捕まらなかった | `perl` に `/g` が無く、**コメント行だけが置換され `KC_HOSTNAME_URL` は無傷**だった |
| P3 | 第7ラウンド初回が測定に到達しなかった | `microservices-platform` namespace を作っておらず `kubectl apply -k deploy/local/edge` が NotFound |

**3 件とも「ゲートが見逃した」ではない。** 混同すると、実際には検査されていないものを
「検査済み」と数えてしまう。

### 10.5 検出の解像度について（正直に残す）

`issuer-reverted-fixed` でゲートは落ちたが、**落ちた理由は「issuer 文字列が違う」ではなく
「discovery を取得できなかった」**だった。

```
[G4] issuer が一致しない。期待 "http://keycloak:8080/realms/platform" /
     実際 "(取得できなかった: curl が失敗した (exit 6): http://keycloak:8080/realms/...)"
```

**同じ門が、同じ文言で「不一致」と「到達不可」の両方を報告している。**
検出はできているが**切り分けの解像度は低い**。
🔴 **将来この門が赤くなったとき、issuer 文字列だけを疑って時間を溶かさないこと。**
まず discovery が取得できているかを見ること。

### 10.6 CI の未実行を切り分ける順序（利用者提供・2026-08-22 の訂正後の分類）

本作業のジョブが「走っていない」ように見えたときは、次の順に見る。
**webhook の取りこぼしを最初に疑わないこと**（確認できた事例は 0 件と裁定済み）。

1. `gh pr view <N> --json mergeable,mergeStateStatus` —— `CONFLICTING` / `DIRTY` ならチェックは走らない
2. `check-suites` に `github-actions` の suite が在るか
3. リポジトリ全体の run
4. 最後に webhook

### 10.7 起動条件・必須チェックが変わっていないことの確認

`.github/workflows/` への変更は**新規 1 本の追加だけ**であり、既存ワークフローの `on:` ブロックには
1 バイトも触れていない。`docs/ai-workflow.md` の必須チェックの表も変更していない。

```console
$ git diff --stat origin/develop...HEAD -- .github/workflows/ docs/ai-workflow.md
 .github/workflows/integration-stack.yml | 153 ++++++++++++++++++++++++++++++++
 1 file changed, 153 insertions(+)

$ git diff origin/develop...HEAD -- .github/workflows/ | grep -E '^\+\+\+|^---'
--- /dev/null
+++ b/.github/workflows/integration-stack.yml
```

**本ジョブは必須チェックにしない。** `pull_request` で起動しないため、必須に指定すると
恒久 pending でマージ不能になる（`docs/ai-workflow.md`「必須チェックに指定する際の注意」）。
**`pull_request` を足すと `scripts.repo.test.js` が落ちる**ようにしてあり、
「必須にできない形」を検査で固定している（§10.1 の L6）。

### 10.8 本作業から派生した issue

| # | 内容 | なぜ本作業で閉じないか |
| --- | --- | --- |
| **#948** | 有効トークンで `/bff/dashboard/summary` が 401 | BFF の欠陥であり、CI 経路の問題ではない。段 2（#466）の前提 |
| **#953** | `HelmChartConfig` の reconcile 失敗が `k8s-local-up.sh` へ伝わらない | 本作業は **pin で回避**する。**回避と解決は別**であり、構造は残る |

**#953 を「pin したから解決した」と書かないこと。** pin は「当面起きない」ようにしただけで、
「起きたときに気付ける」ようにしたのは G5 である。**構造そのもの（reconcile 失敗が呼び出し側へ
伝わらない）は手つかずで残っている。**
