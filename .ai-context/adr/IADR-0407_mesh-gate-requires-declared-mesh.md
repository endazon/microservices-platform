---
title: IADR-0407 メッシュの門は「入れると宣言した実行」でだけ 0 件走査を失敗にし、統合スタックへ Istio を入れて実際に評価させる
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0005, ADR-0021, IADR-0130, IADR-0307, IADR-0317, IADR-0377]
author: Claude（実装）
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
---

# IADR-0407: メッシュの門は「入れると宣言した実行」でだけ 0 件走査を失敗にし、統合スタックへ Istio を入れて実際に評価させる

- 状態: Accepted
- 日付: 2026-09-06
- 決定者: Claude（実装）。**案の選択（0 件で赤 ＋ Istio を入れる）は利用者が 2026-09-06 に裁定した**
  —— 日次の所要時間と資源は利用者の裁量に属するため。

## 起点・関連

- 起票: #1304（#458 の作業中に判明）
- 直接の前提: [IADR-0377](./IADR-0377_mesh-mtls-single-writer-and-drift-gate.md)（G12 を置いた ADR）／
  [IADR-0130](./IADR-0130_test-spec-coverage-ratchet.md)（**0 件走査で緑を返さない**）
- 計画: `ADR-0005`（サービスメッシュ）／`ADR-0021`（入口＝Istio Ingress Gateway）
- 関連: [IADR-0307](./IADR-0307_istio-optin-and-staged-mtls.md) 決定 4（STRICT への段取り）／
  [IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)（動いているが宣言が持っていない）

## コンテキストと課題

**`IADR-0377` が置いた G12 は、必須の統合スタックの中で一度も評価されていなかった。**

```console
$ git grep -c "ISTIO" -- .github/            → 0
$ git grep -c "LOCALEDGE" -- .github/        → 4   ← 陽性対照（同じ走査で別の opt-in 変数は当たる）
```

`scripts/k8s-local-up.sh:310` が Istio の導入を `ISTIO=1` の opt-in にしているので、
統合スタックはコントロールプレーンを立てない。すると `PeerAuthentication` /
`AuthorizationPolicy` / `DestinationRule` は 1 つも存在せず、`evaluateMeshDrift` は
`declared === null` かつ `items.length === 0` の枝で `notices.push(...)` へ入り、**飛ばす**。

`integration-stack.yml` は G12 を含めて「すべて fail-closed で判定する」と書いていたが、
**この 1 つだけは実質 fail-open だった。**

🔴 **G12 の主眼は「値の一致」ではない。** `IADR-0377` はこう書いている ——
Helm 4 はサーバサイド apply なので `kubectl patch` / `kubectl apply` が同じフィールドを書くと
field manager を奪い、**以後の `helm upgrade` が conflict で恒久的に失敗する**。
**値が偶然一致していても壊れている**ので、値の一致だけを見る門では捕まらない。
その門が動いていなかった。

## 決定

### 決定 1: 「0 件なら常に赤」にはしない。**入れると宣言した実行**でだけ失敗にする

`evaluateMeshDrift` に `requireMesh` を足し、呼び出し側が `process.env.ISTIO === '1'` を渡す。
`requireMesh` が真のとき、次の 2 つの notice を failure へ変える。

1. helm の宣言が読めず、稼働にもメッシュ資材が無い
2. `mesh.enabled=false` で、稼働にもメッシュ資材が無い

🔴 **常に赤にしない理由**: `k8s-local-up.sh` の Istio は opt-in であり、**メッシュ無しのローカル起動は
正当な構成である**。常に赤にすると、開発者が素のスタックを起こすたびに門が嘘の欠陥を報告し、
**「G12 の赤は無視してよい」という学習を作る** —— それは門を消すより悪い。

**赤にすべきなのは「入れると宣言したのに入っていない」という食い違いのほう**であり、そこだけを閉じる。

**作法は新しくない。** G10 の `PERSIST`（`process.env.PERSIST !== '0'`）と
G13 の `SEARCHSEED`（`process.env.SEARCHSEED === '1'`）が同じ形で先例を 2 つ持っている。

### 決定 2: 統合スタックへ `ISTIO=1` を渡す。**up と門の両方へ渡す**

- up のステップ: `LOCALEDGE=1 ISTIO=1 ABACSEED=1 SEARCHSEED=1 LOCALEMBED=1`
- 門のステップ: `ISTIO=1 node scripts/check-stack-ready.js`

🔴 **門へ渡し忘れると、この作業は無意味になる。** up で Istio を入れても、門が宣言を受け取らなければ
G12 は従来どおり「メッシュ未導入」として飛ばす —— **入れたのに評価されない**という、
直前の状態と区別のつかない形になる。

**SEARCHSEED とは向きが逆である。** G13 では `SEARCHSEED=1` を門へ渡すと**間欠赤**になるので渡さない。
G12 では `ISTIO=1` を渡さないと**恒久緑**になるので渡す。**同じ env の作法でも、渡す / 渡さないの
判断は門ごとに違う。**

### 決定 3: `ISTIO=1` が入口も動かすことを、変更の重さとして記録する

`scripts/k8s-local-up.sh:884` は **`LOCALEDGE=1` の枝の内側**で `istio-edge-up.sh` を呼び、
**エッジを kube-system の Traefik から Istio Ingress Gateway へ移す**（`ADR-0021`）。
統合スタックは `LOCALEDGE=1` を渡しているので、**`ISTIO=1` を足すと入口が変わる。**

🔴 **後段の門（G4 エッジと issuer の一致 / パスワードリセットの送出 / 検索の疎通）はすべて
エッジ越しに測っている。** したがってこの 1 行は「env を 1 つ足す」以上の変更である。

**この重さを引き受ける手段**: `integration-stack` は PR で起動しない（契機は `schedule` /
`push: [develop]` / `workflow_dispatch`）ので、**`workflow_dispatch` を PR のブランチへ撃って
緑を見るまでマージしない**（`concurrency` は `github.ref` 単位なので develop の日次と競合しない）。

### 決定 4: `ISTIO_MTLS_MODE` は指定しない（既定 PERMISSIVE のまま）

STRICT への昇格は `IADR-0307` 決定 4 の段取り（注入 → 全 Pod Ready → PERMISSIVE で疎通確認 → STRICT）
に従う**別の作業**である。ここで上げると、サイドカーの入っていない `platform-infra`
（postgres / keycloak / rabbitmq / qdrant / redis …）との通信と、注入前の Pod からの通信が
**同時に**壊れる。**門を動かすことと、mTLS を締めることを 1 つの PR に混ぜない。**

### 決定 5: 採らなかった案 —— 「未評価であることを出力に明示するだけ」

#1304 の案 3（G12 が評価されていないことを `check-stack-ready.js` の出力に書く）は**採らない**。

理由: **出力の 1 行は門ではない。** 現に G12 は既に notice を出しており（「メッシュ未導入とみなして
飛ばす」）、**その notice は 1 度も読まれないまま門が緑を返し続けていた**。
文言を変えても、読む人が居なければ同じである。🔴 **「気づけるようにする」は「止める」の代わりにならない。**

ただし案 3 が持っていた良い性質（**素のスタックを赤にしない**）は決定 1 が引き受けている。

## 理由

- **`IADR-0130` の適用である。** 「0 件走査で緑を返さない」は本リポジトリが繰り返し踏んできた形であり、
  今回は**必須チェックの内側で**成立していた。検査器が壊れていたのではなく、**検査対象が無かった**。
- **env で厳しさを切り替える形を選んだのは、先例が 2 つあるからである**（G10 / G13）。
  「宣言した実行だけ fail-closed」は本リポジトリの既存の作法であり、新しい流儀を持ち込まない。
- **入口が動くことを決定として書いたのは、それが「気づいたら変わっていた」形で入るのが最も危ういからである。**
  実行時間の増加は測れば分かるが、**入口の移動は後段の門の意味を変える**ので、
  緑になったときに何が保証されたのかが変わる。

## 結果

- **良い影響**: G12 が実際に評価される。`IADR-0377` が名前を付けた
  「値が偶然一致していても field manager を奪われていれば壊れている」形が、日次で見張られる。
  あわせて `ADR-0005` / `ADR-0021` の構成が CI で毎日起動されるようになる（従来は誰も起こしていなかった）。
- **悪い影響 / トレードオフ**: 統合スタックの所要時間と資源が増える（istio-base ＋ istiod の helm install と、
  全アプリ Pod へのサイドカー注入）。**増分は実測して PR に書く**（健全時の実測は 11〜15 分）。
  入口が Istio Ingress Gateway へ移るので、**後段の門が測っている経路が変わる。**
- **測っていて、直していないこと**:
  🔴 **稼働クラスタでの field manager 奪取の実測**は行っていない。「G12 が実際に走ること」は
  統合スタックの緑で、「奪取を検出すること」は自己試験で示す。**この 2 つは別の主張であり、混ぜない。**
  🔴 **STRICT mTLS は宣言していない**（決定 4）。したがって `ADR-0005` の最終形はまだ CI に無い。
- **フォローアップ**:
  (1) 所要時間の増分が大きすぎる場合、決定 2 を利用者へ差し戻す（日次の資源は利用者の裁量）
  (2) STRICT への昇格を `IADR-0307` 決定 4 の段取りで別 PR に分ける
  (3) 稼働クラスタでの奪取の実測（`workflow_dispatch` に変異ステップを足して 1 度だけ走らせる形が候補）

## 関連

- Supersedes: なし（[IADR-0377](./IADR-0377_mesh-mtls-single-writer-and-drift-gate.md) を**補う**。
  同 ADR の決定は 1 つも覆していない —— 置いた門を実際に動かすだけである）
- Superseded by: なし
