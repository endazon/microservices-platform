---
title: IADR-0247 統合スタックを CI で起こす経路は nightly に置き、`k8s-local-up.sh` の EXIT=0 を成功と見なさず自前の readiness ゲートで判定する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0007
  - ADR-0021
  - IADR-0091
  - IADR-0206
  - IADR-0220
  - IADR-0227
  - IADR-0232
  - IADR-0240
  - IADR-0243
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_runtime-platform.md
---

# IADR-0247: 統合スタックを CI で起こす経路（#783 後半）

- 状態: Accepted
- 日付: 2026-08-22
- 決定者: claude（実装セッション）／利用者裁定 2026-08-22

## 起点・関連

- 関連する計画書 ID: `ADR-0007`（CI/CD）／ `ADR-0021`（エッジ・実行基盤）。**NFR**（メタ作業のため計画側に当たる番号が無い）
- 関連する実装仕様書: `.ai-context/specs/20260822_issue-783_integration-stack-ci.md`
- issue: #783（#442 子 5）のやること②。開ける先: #466

## コンテキストと課題

#783 の前半（chart / overlay のレンダリングとスキーマ突合）は PR #878 / PR #925（[IADR-0240]）で着地した。
後半は「**統合スタックを CI ランナー上で起こす経路**」であり、#466（E2E スモークを統合スタックで CI 実行）の土台になる。

**着手前に、律速になり得る未知をすべて GitHub ホストランナー上で実測した**（probe ブランチ
`probe/783-runner-capability` の 5 ラウンド）。その結果、**当初の想定が 2 つ覆り、想定していなかった穴が 2 つ見つかった。**

### 覆ったこと 1: 依存（#780）は解けていた

PR #522 は「認証には hosts 追記と `port-forward` が要り、**CI ではどちらも用意できない**」を本 issue の実体としていた。
実測すると、

- **`*.localhost` はランナー上で解決する。** `ubuntu-24.04` は systemd-resolved が RFC 6761 の**合成応答**として
  `127.0.0.1` を返す（`-- Data from: synthetic`）。2 段の `a.b.localhost` も同様
- **hosts 追記も CI でできる**（passwordless sudo。`EXIT_append=0` を実測）。**PR #522 の記述は片方が誤りだった**

さらに #780 第2段（[IADR-0243]）が着地し、issuer はエッジ host へ移り、pod 側の名前解決も
[IADR-0227] の `coredns-custom` で解けている。**手順A への依存そのものが消えている。**

### 覆ったこと 2: 費用は想定より安い

`LOCALEDGE=1 bash scripts/k8s-local-up.sh` は **CI で完走した（EXIT=0 / 400 秒）**。

| 段 | 秒 |
| --- | ---: |
| `k3d cluster create`（80 / 443 / 50000 を 127.0.0.1 へ公開） | 22 |
| イメージ 16 本のビルド | 244 |
| `k3d image import`（16 本） | 37 |
| infra 適用 ＋ rollout 待ち | 61 |
| `helm upgrade --install` | 1 |
| エッジ（Ingress ＋ coredns-custom ＋ cert-manager ＋ edge-tls） | 33 |
| **合計** | **400** |

readiness を待つ費用は **33 秒**（`platform-infra` 1 秒 / `microservices-platform` 32 秒）。

### 🔴 見つかった穴 1: `EXIT=0` は readiness の証明にならない

`k8s-local-up.sh` は `[6/7]` で `helm upgrade --install` を **`--wait` 無しで**呼ぶ。
**アプリ pod の起動を待たずに EXIT=0 で戻る。** 実測（run 32554340800）では、戻った時点で

```
bff-service-f97557678-44q7z          0/1  Running  1 (16s ago)
aianalysis-service-54d648fc47-rlfkf  0/1  Running  0
Warning  Unhealthy  pod/bff-service  Liveness probe failed: ... connection refused
```

であり、導線を叩くと 502 が 5 件並んだ。**up の成否だけを見るジョブは、立ち上がっていないスタックに緑を返す。**
しかも**無音**である（up は EXIT=0 のまま）。

### 🔴 見つかった穴 2: 宣言が k3s のバージョンに暗黙に依存している

エッジ overlay の Traefik `HelmChartConfig`（admin:50000・[IADR-0091] / [IADR-0220]）が k3d 上で効かなかった。

```
Error: UPGRADE FAILED: template: traefik/templates/service.yaml:21:12:
executing "traefik/templates/service.yaml" at <eq $config.expose true>:
error calling eq: incompatible types for comparison
```

`deploy/local/edge/traefik-entrypoint.yaml` は `expose` を **map**（`{default: true}`＝traefik chart 26 以降の書式）で
書いているが、k3s v1.30.4 が同梱するのは **traefik 25.0.3** で `expose` は **bool** である。
**`kubectl apply` は成功し、reconcile だけが落ち、up は EXIT=0 で返る**（構造そのものは #953 で別途扱う）。

手元の Rancher Desktop は k3s **v1.35.4+k3s1** で、そこでは効いている。
**「ローカルで通ったから CI でも通る」が成立しない実例である。**

## 検討した選択肢

### 実行基盤

| 案 | 評価 |
| --- | --- |
| **A. k3d ＋ `k8s-local-up.sh`（採用）** | 経路を二重実装しない。実測 400 秒で完走済み |
| B. compose（`compose-up.sh`）| 軽いが、**検証したい対象（k8s の overlay・Ingress・coredns）が存在しない**。#466 が要る導線を再現できない |
| C. 既存イメージの再利用 | `images.yml` は**レジストリへ push していない**（ビルド検証のみ）。244 秒を pull で置き換える配線が無い。**別の判断が要るので広げない** |

### 起動契機

| 案 | 評価 |
| --- | --- |
| **D. nightly ＋ develop への push ＋ 手動（採用）** | `integration.yml`（[IADR-0232] 決定 3 改定 3）と同型。日次だけだと朝までに PR が積み上がり切り分けが難しくなる |
| E. PR ゲート | 8〜10 分。`ci.yml` の `build-and-test`（数分）の 2〜3 倍で、待ち時間が支配的になる |

### k3s の扱い

| 案 | 評価 |
| --- | --- |
| **F. k3d `v5.8.3` ＋ `rancher/k3s:v1.35.4-k3s1` へ pin（採用）** | **手元の Rancher Desktop と同じ k3s** になり、乖離そのものが消える |
| G. k3d の既定に任せる | 既定が動いた瞬間に検証範囲が静かに変わる。**実測で v1.30.4 は admin:50000 が立たない** |
| H. `traefik-entrypoint.yaml` を chart 25 系と両立する書式へ直す | 本 issue の射程外（エッジの宣言を書き換える）。#953 で扱う |

## 決定

1. **統合スタックは k3d ＋ `scripts/k8s-local-up.sh`（`LOCALEDGE=1`）で起こす。** 経路を二重実装しない。
2. **契機は nightly（UTC 18:30）＋ develop への push ＋ `workflow_dispatch`。PR では起動しない。**
   **必須チェックにはしない**（PR で起動しないチェックを必須にすると恒久 pending になる。`docs/ai-workflow.md`）。
3. 🔴 **「PR ゲートの上限」を定義する: 全 PR で起動するジョブは、`ci.yml` の `build-and-test` の所要時間を
   基準として、その 2 倍を超えてはならない。** 超えるものは nightly へ分離する。
   —— #783 は「上限を超えるなら nightly へ」と書いているが、**上限はどこにも定義されていなかった。**
   定義されていない基準では「超えた」と言えないため、ここで定義する。本ジョブは 8〜10 分で明確に超える。
4. 🔴 **`k8s-local-up.sh` の EXIT=0 を成功と見なさない。** `scripts/check-stack-ready.js` を門として置き、
   **6 つの門をすべて fail-closed** で判定する。
   - **G1** Deployment の `availableReplicas` と Pod の Ready
   - **G2** 走査 0 件を緑にしない（`kubectl wait --all` は**対象が 0 件のとき成功する**）
   - **G3** `kubectl` / `curl` 不在は失敗。**抜け道の環境変数を置かない**
   - **G4** `keycloak-edge` Ingress の存在と、discovery の `issuer` が `KC_HOSTNAME_URL` ＋ `/realms/<realm>` と
     **文字列として完全一致**すること（[IADR-0243] の受け入れ基準を CI で固定する）
   - **G5** `kube-system/traefik` に `admin=50000` が在ること（穴 2 への手当て）
   - **G6** **pod から**エッジ host が引けること（[IADR-0227]）
5. 🔴 **G6 は G4 で代替しない。** ランナー側は systemd-resolved の合成応答で `.localhost` が引けてしまうため、
   **クラスタ内の名前解決が壊れていても G4 は通る**。非 .NET の 6 クライアント（Grafana / ArgoCD / Vault /
   MinIO / Headlamp / Wiki.js）は [IADR-0086] の metadata/issuer 分離を使えず pod から issuer を引くので、
   G6 が無いと「6 ツールが壊れているのに緑」になる。
6. **k3s を pin する**（k3d `v5.8.3` ＋ `rancher/k3s:v1.35.4-k3s1`）。`K3S_IMAGE` を `k8s-local-up.sh` へ
   opt-in で足す（**未設定なら 1 バイトも変えない**）。
   🔴 **pin する理由は「バージョンを揃えたいから」ではない。揃っていないことが静かに素通りするからである。**
7. **列挙を持たない。** 対象 namespace は 2 つ書くが、**その中のサービス名は一切書かない**。
   realm 名も `deploy/keycloak/*-realm.json` を走査して得る（[IADR-0240] / `check-deploy-manifests.js` 要点 1 と同じ判断）。
8. **失敗は `ci-failure-issue.yml` で issue にする**（[IADR-0232] 決定 1 と同型）。
   nightly の失敗は、見ていなければ無かったことになる。

## 理由

- 決定 4 が本 IADR の中心である。**#783 が最も避けたい形は「検証していないのに緑」であり、
  この経路ではそれが無音で起きる。** ゲートの費用は 33 秒で、置かない理由が費用にはない。
- 決定 3 は「判定できない基準を根拠にしない」ための措置である。上限が未定義のまま
  「nightly へ分離する」と書いても、**次に誰かが PR ゲートへ載せたときに止められない。**
- 決定 6 の理由づけを「揃えたいから」にすると**好みに見え、次の人が外す**。
  「揃っていないことが静かに素通りする」と書けば、外すことの危険が読み取れる。

## 結果

- 良い影響:
  - #466 の土台ができる。**OIDC 導線（SPA → 認可コード → PKCE → クレーム）が CI で通ることは実測済み**
    （`verify-oidc-edge-flow.sh` の PASS 9）
  - [IADR-0243] の issuer 一致が CI で固定される（G4）
  - [IADR-0227] の pod 側名前解決が CI で固定される（G6）
  - #953 の構造的な穴（reconcile 失敗が伝わらない）が、**少なくとも traefik の admin entrypoint については
    気付ける形**になる（G5）
- 悪い影響・トレードオフ:
  - nightly なので、**壊れてから気付くまで最大 1 日**かかる（develop への push でも走るので、実際は
    マージのたびに回収される）
  - ランナー時間を 1 回あたり 8〜10 分消費する
  - **k3s を pin したことで、pin と実環境がずれる新しい経路が生まれる。** #953 が塞ぐまでは、
    pin を上げるときに `admin=50000` を目視で確かめる必要がある（G5 が落ちるので気付ける）
- フォローアップ:
  - **#953**: `HelmChartConfig` の reconcile 失敗が `k8s-local-up.sh` へ伝わらない構造そのもの
  - **#466（段 2）**: `verify-oidc-edge-flow.sh` を載せ、PASS 件数を baseline 化する。
    🔴 **その前に検証スクリプトの期待値の陳腐化を直すこと** —— `GET /bff/documents`（無トークン）の期待が
    200 のままで、#458 適用済みの現状（401）と食い違っている。**直さずに baseline 化すると、
    壊れた期待値を恒久的な FAIL 1 件として焼き付ける**
  - **#948**: 有効トークンで `/bff/dashboard/summary` が 401（CI の新規デプロイでも再現）

## 関連

- Supersedes: なし
- Superseded by: なし
