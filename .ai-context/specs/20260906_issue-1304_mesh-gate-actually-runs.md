---
title: G12（メッシュ宣言のドリフト門）を実際に評価させる —— 宣言した実行では 0 件走査を緑にせず、統合スタックへ Istio を入れる
type: spec
status: done
related_ids:
  - NFR
  - ADR-0005
  - ADR-0021
  - IADR-0130
  - IADR-0377
  - IADR-0407
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs: []
---

# 作業仕様書: G12 を実際に走らせる

## 事象（#1304。実測）

`scripts/check-stack-ready.js` の **G12（メッシュ宣言の乖離）** は必須の統合スタックで呼ばれているのに、
**検査対象が存在しないまま緑を返している。**

```console
$ git grep -c "ISTIO" -- .github/            → 0
$ git grep -c "LOCALEDGE" -- .github/        → 4   ← 陽性対照（同じ走査で別の opt-in 変数は当たる）
```

`scripts/k8s-local-up.sh:310` が `if [ "${ISTIO:-}" = "1" ]` で Istio の導入を opt-in にしている。
`.github/` のどこにも `ISTIO` が無い以上、**統合スタックは Istio を入れない。**
コントロールプレーンが無ければ `PeerAuthentication` / `AuthorizationPolicy` / `DestinationRule` は
1 つも存在せず、`evaluateMeshDrift` は `declared === null` かつ `items.length === 0` の枝に入り、
`notices.push(...)` で**飛ばす**。

`integration-stack.yml:150-160` は G12 を含めて「すべて fail-closed で判定する」と書いているが、
**この 1 つだけは実質 fail-open である。**

🔴 これは `IADR-0130`（0 件走査で緑を返さない）が禁じている形そのものであり、
**必須チェックの中で「検査しているつもりで何も見ていない」状態が成立している。**

## 利用者の裁定（2026-09-06）

案 1（0 件で赤）＋ 案 2（Istio を入れる）を**両方**採る。案 3（未評価であることを出力に明示するだけ）は採らない。

## 母集合（規則 1〜10）

基点 `origin/develop`（`109d0bbd`）。`git rev-parse --is-shallow-repository` = `false`。

### 軸 1: 誤りの側 ——「宣言していないから飛ばす」枝

```console
$ grep -n "notices.push" scripts/check-stack-ready.js   （G12 の枝）
:686  helm の宣言を読めず、稼働にもメッシュ資材が無い。メッシュ未導入とみなして飛ばす。
:698  宣言が mesh.enabled=false で稼働にも資材が無い（一致）。
```

**G12 が緑を返す「何も見ていない」経路はこの 2 本だけである**（他の枝はすべて failure か、
実際に突き合わせる本体へ進む）。

### 軸 2: 同じ作法（env で宣言した実行だけ厳しくする）の先例

```console
$ grep -n "process.env.PERSIST\|process.env.SEARCHSEED" scripts/check-stack-ready.js
:1614  expectPersist: process.env.PERSIST !== '0'      ← G10
:1661  expectPoints: process.env.SEARCHSEED === '1'    ← G13
```

**2 件の先例がある。** 新しい流儀を持ち込まず、`ISTIO=1` を同じ形で足す。

### 軸 3: CI で `ISTIO` を宣言している箇所

```console
$ git grep -n "ISTIO" -- .github/   → 0 件
$ git grep -n "ISTIO" -- scripts/   → k8s-local-up.sh（導入・エッジ移設）/ istio-edge-up.sh ほか
```

**除外**: `scripts/k8s-local-up.test.js` の既定バイト等価試験は触らない（既定＝ISTIO 未設定の挙動は変えない）。

## 🔴 着手中に判明した事実 —— `ISTIO=1` は入口も動かす

`scripts/k8s-local-up.sh:885-889`（`:884` は直前のコメント行）:

```sh
if [ "${ISTIO:-}" = "1" ]; then
  echo "==> [opt-in] エッジを Istio Ingress Gateway へ移す (ADR-0021 / #782)"
  ISTIO_MTLS_MODE="${ISTIO_MTLS_MODE:-}" bash "$ROOT/scripts/istio-edge-up.sh"
```

**`LOCALEDGE=1` の枝の内側にある。** 統合スタックは `LOCALEDGE=1` を渡しているので、
`ISTIO=1` を足すと**エッジが kube-system の Traefik から Istio Ingress Gateway へ移る**。

🔴 **これは「実行時間と資源が増える」より重い変更である。** 後段の門（G4 エッジと issuer の一致 /
パスワードリセットの送出 / 検索の疎通）はすべて**エッジ越しに測っている**ので、
入口が変われば**全部が影響を受ける**。

**したがって案 2 は「env を 1 つ足す」作業ではない。** 実際に走らせて測るまで、
入るか入らないかを断定しない。

### 測り方（`integration-stack` は PR で起動しない）

`integration-stack.yml` の契機は `schedule` / `push: [develop]` / `workflow_dispatch` である。
**PR では起動しない**ので、マージ前の検証は **`workflow_dispatch` を本 PR のブランチに対して撃つ**
しかない（`concurrency` は `github.ref` 単位なので develop の日次と競合しない）。

🔴 **撃って緑を見るまで、案 2 の変更はマージしない。**

## 設計

### 変更 1: G12 に「メッシュを要求する」宣言を足す（`IADR-0130` の適用）

`evaluateMeshDrift` に `requireMesh` を足し、上の**軸 1 の 2 本の notice を failure へ変える**。
呼び出し側は `requireMesh: process.env.ISTIO === '1'`（G10 / G13 と同じ形）。

🔴 **「0 件なら常に赤」にはしない。** `k8s-local-up.sh` の Istio は opt-in であり、
**メッシュ無しのローカル起動は正当な構成である**。赤にすべきなのは
「**入れると宣言したのに入っていない**」という食い違いのほうであり、そこだけを閉じる。

### 変更 2: `integration-stack.yml` で `ISTIO=1` を渡す —— 🔴 **測った結果、本 PR から外した（#1316 へ分離）**

- up のステップ（`LOCALEDGE=1 ABACSEED=1 SEARCHSEED=1 LOCALEMBED=1`）へ足す
- 🔴 **門のステップ（`node scripts/check-stack-ready.js`）にも渡す。**
  渡さないと G12 は従来どおり飛ばす（＝この PR の目的が達成されない）
- ジョブ名（`integration-stack`）は変えない。**必須チェックの表も変えない**

`ISTIO_MTLS_MODE` は**指定しない**（既定 PERMISSIVE）。STRICT は `IADR-0307` 決定 4 の段取りに従い、
入口を移した後の別の作業である。**本 PR で STRICT へは上げない。**

### 変更 3: 採らなかった案を実装 ADR へ残す

`IADR-0407` を起こし、①なぜ「0 件で常に赤」にしないのか ②なぜ案 3（出力に明示するだけ）を採らないのか
③`ISTIO=1` が入口も動かすこと ④STRICT へ上げないこと、を記録する。

## 受け入れ基準（Given-When-Then）

**ローカルで確かめたもの:**

- [x] Given `ISTIO=1` を宣言した実行 / When helm の宣言が読めず稼働にも資材が無い / Then **failure**
- [x] Given `ISTIO=1` を宣言した実行 / When `mesh.enabled=false` で稼働にも資材が無い / Then **failure**
- [x] Given `ISTIO` を宣言していない実行 / When 同じ状態 / Then **従来どおり notice で飛ばす**（陰性対照）
- [x] Given `ISTIO=1` / When メッシュが実際に入っている / Then **本体の突き合わせへ進む**
      （要求が本体を殺していない。field manager の奪取を見続けることまで確かめた）
- [x] **変異試験を実走した**（下の §実測した変異）。`--self-test` は **67 → 71 件**、
      `REQUIRE_REPO_TESTS=1 scripts.test.js` は **747 件**緑

**🔴 稼働クラスタでしか確かめられないもの:**
- [ ] Given `kubectl patch` で `PeerAuthentication` の `spec` を書く / When G12 が走る /
      Then **field manager の奪取を検出して赤**（自己試験で固定。🔴 **稼働クラスタでの実奪取は測っていない**）
- [ ] Given 統合スタック / When `workflow_dispatch` で本ブランチを走らせる /
      Then **緑であり、G12 が飛ばされていない**（出力に notice が出ないこと）
- [ ] Given 所要時間 / When 同上 / Then **実測値を PR に書く**（健全時の実測 11〜15 分と比べる）

## 実測した変異

`requireMesh` の分岐を 2 方向へ壊し、**両方とも自己試験が赤になる**ことを実走で確かめた。

```console
（1）} else if (requireMesh) {  →  } else if (false) {   ※ 要求を無視する
AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:  actual: 0, expected: 1
  → 赤（変異を捕まえた）

（2）} else if (requireMesh) {  →  } else if (true) {    ※ 常に失敗にする（既定を赤にする）
AssertionError [ERR_ASSERTION]: Expected values to be strictly deep-equal
  → 赤（変異を捕まえた）
```

🔴 **(2) を置いた理由**: 要求した側だけを試すと、「常に赤」の実装でも陽性 2 本は通ってしまう。
**メッシュ無しのローカル起動を赤にしないこと**は、この作業で壊してはいけない性質である。

## テスト方針

| 試験 | 何を見るか | 赤にする変異 |
| --- | --- | --- |
| `requireMesh` 陽性 1 | 宣言が読めない × 資材 0 × `ISTIO=1` → failure 1 | 要求を無視して notice へ倒す |
| `requireMesh` 陽性 2 | `mesh.enabled=false` × 資材 0 × `ISTIO=1` → failure 1 | 同上 |
| `requireMesh` 陰性 | 同じ状態で `ISTIO` 未宣言 → failure 0・notice 1 | 常に failure にする（素のローカル起動を赤にしない） |
| `requireMesh` × 本体 | `ISTIO=1` かつメッシュが宣言どおり → failure 0 | 要求が本体を殺していないこと |
| 既存 G12 の 8 本 | 値の乖離・field manager の奪取・迷子の資材 | **1 本も減らさない** |

## 計画書との差異

- 差異なし。`ADR-0005`（メッシュ）と `ADR-0021`（入口）の確定内容は変えない。
  本 PR は**確定済みの門を実際に評価させる**作業である。

## 未決事項

- 🔴 **稼働クラスタでの field manager 奪取の実測**は本 PR では行わない（自己試験で固定するに留める）。
  「G12 が実際に走ること」と「奪取を検出すること」は**別の主張**であり、前者は統合スタックの緑で、
  後者は自己試験で示す。**両者を混ぜて書かない。**
- 所要時間の増分が大きすぎる場合、案 2 は利用者へ差し戻す（日次の資源は利用者の裁量である）。


★［2026-09-06 追記 / #1304］🔴 **撃って測った。案 2 は入らなかったので分離した。**

`workflow_dispatch` を作業ブランチへ撃った結果（run **34037589847**）は
**failure / 所要 35 分**（`ISTIO` 無しの健全時 11〜15 分。ジョブ上限 45 分）。

| ステップ | 結果 |
| --- | --- |
| Bring up the integration stack（`ISTIO=1` つき） | **success** —— Istio の導入もエッジ移設も通る |
| **Wait for pods to become Ready** | **failure**（600 秒で **28 Pod** が `timed out waiting for the condition`） |
| 門以降 | skipped —— **G12 は今回も 1 度も評価されていない** |

### 実測した原因 2 つ

**(1) ExternalName の別名がサイドカー注入より後に当たる。**

```
14:05:33  ==> [opt-in] Istio sidecar injection (rollout restart)
14:06:07  error probes  Request to probe app failed: Get ".../health/live": connection refused
14:18:33  service/rabbitmq created      ← [7/7] の別名。**12 分後**
```

作り直された Pod は依存の別名が無い状態で起動し、health が 500 を返して**8 回まで再起動**する
（15 サービス分の `Readiness probe failed: statuscode: 500`）。`rollout status` は
`|| echo WARN` の best-effort なので **10 分待って先へ進む**。
🔴 **`ISTIO` 無しでは作り直しが起きないので表面化しない。**

**(2) 旧 ReplicaSet の Pod が待ちの対象に残る。**
タイムアウトした 28 件は**サービスあたり 2 つ**（例 `bff-service-58fd854cc8-gb5cg` と
`bff-service-67f96846cf-hgzsm`）で新旧両方。診断ダンプでは新しい側が `2/2 Running` だが、
**旧側は Ready にならない。** `kubectl wait --for=condition=Ready` は原理的に成立しない。
**#1055（完了 Job の Pod を待たない）と同型**が別の入口から入っている。

### 判断

🔴 **「`ISTIO=1` を CI へ足す」は env を 1 つ足す作業ではなかった。**
`ADR-0005` / `ADR-0021` の構成は**これまで CI で一度も起動されたことがなく**、
実際に起こすと立ち上がらない。**#1316 へ分離した。**

**本 PR に残したのは変更 1（と `IADR-0407`）だけである。CI の挙動は 1 バイトも変わらない。**
🔴 **#1304 は閉じない。** 門はまだ 1 度も評価されていない。

### 🔴 まだ何も分かっていないこと

**サイドカーが入った状態で後段の門（G4 / パスワードリセット / ABAC / 検索）が通るか**は、
ステップ 11 以降へ一度も到達していないため**まったく測れていない**。入口が
Istio Ingress Gateway へ移るので、これらが測っている経路そのものが変わる。
