---
title: 統合スタックへメッシュを既定で入れ、G12 を実際に評価させる（#1304）
type: spec
status: in-progress
related_ids: [NFR, ADR-0005, ADR-0021, IADR-0130, IADR-0248, IADR-0307, IADR-0317, IADR-0377, IADR-0407]
author: Claude（実装）
created: 2026-09-08
updated: 2026-09-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_ingress-and-edge.md
---

# 仕様書: 統合スタックへメッシュを既定で入れ、G12 を実際に評価させる（#1304）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR（**当たる番号が無い**。`ADR-0005` / `ADR-0021` が定めた構成が CI で評価されていない
  ことの是正であり、製品の非機能要件表の 1 項目には対応しない。[[IADR-0188]] 決定 1 の運用に従う）
- 計画 ADR: ADR-0005（サービスメッシュ）／ADR-0021（入口＝Istio Ingress Gateway）
- 実装 ADR: [[IADR-0377]]（G12 を置いた）／[[IADR-0407]]（G12 の fail-closed 側。**決定 2 が本件を #1316 へ分離した**）／
  [[IADR-0130]]（0 件走査を緑にしない）／[[IADR-0248]]（統合スタックの門）
- issue: #1304。**前提だった #1316 は `5e72d8c6` で着地済み**

## 射程

**#1304 の 3 案のうち、残っているのは案 2（統合スタックで Istio を入れる）だけ**である。

| 案 | 状態 |
| --- | --- |
| 1. G12 に「0 件なら fail」を足す | ✅ **PR #1313 で着地**（[[IADR-0407]] 決定 1。**入れると宣言した実行**でだけ失敗にする） |
| 2. 統合スタックで Istio を入れる | 🔄 **本 PR**。前提の #1316 が片付いたので実行できる |
| 3. 未評価であることを出力に明示するだけ | ❌ **採らない**（[[IADR-0407]] 決定 5 に記録済み。良い性質は決定 1 が引き受けた） |

## 実測（着手の根拠。**#1316 の PR で撃った実走**）

[run 34138046452](https://github.com/endazon/microservices-platform/actions/runs/34138046452)

```
開始 2026-09-07T15:23:20Z → 終了 2026-09-07T15:36:43Z   = 13 分 23 秒 / conclusion=success
```

- **ジョブ上限 45 分に対して余裕がある。** `ISTIO` 無しの健全時（11〜15 分）と**ほぼ同じ**
- **G12 が初めて実際に評価された**:
  `G12: 宣言 mesh.mtlsMode=PERMISSIVE / 資材 4 件（AuthorizationPolicy/bff-service-plaintext-only-backchannel,
  DestinationRule/microservices-platform-mtls, PeerAuthentication/bff-service-backchannel-logout,
  PeerAuthentication/microservices-platform-mtls） / spec の書き手=helm。`
- **後段の門（パスワードリセット / ABAC / 検索）もすべて success**
  —— issue #1316 が「まだ何も分かっていない」と書いていた点への回答である

🔴 **#1304 が「実行時間と資源の裁量を伴う。実装だけでは決めない」と書いていた判断材料はこれである。**
所要時間の増分が実質 0 であることを測ったうえで、既定で入れる。

## 決定（実装方針）

### 決定 1: ジョブレベルの `env` で **既定 `1`** にする

```yaml
env:
  ISTIO: ${{ (github.event_name == 'workflow_dispatch' && !inputs.istio) && '' || '1' }}
```

- **schedule / push(develop) / 手動（既定）**: `1`
- **手動で `istio=false`**: 空（比較のためメッシュ無しで起こせる。**退路を残す**）
- 🔴 **up と門の両方へ同じ宣言が届くこと自体が要件である** ——
  片方だけへ渡すと `check-stack-ready.js` の G12 が「宣言が門へ届いていない」で落ちる（[[IADR-0407]] 決定 2）。
  **ジョブレベルの `env` に置くのはそのため**であり、up のコマンド行へ直書きしない。

### 決定 2: `ISTIO_MTLS_MODE` は指定しない（既定 PERMISSIVE のまま）

[[IADR-0407]] 決定 4 を据え置く。STRICT への昇格は [[IADR-0307]] 決定 4 の段取りに従う別の作業である。

### 決定 3: 受け入れ基準の本体（field manager の奪取）は**稼働クラスタで測る**

#1304 の受け入れ基準 2 は「`kubectl patch` で `PeerAuthentication` の `spec` を書くと
**G12 が field manager の奪取を検出して赤になる**（変異試験。**これが本体である**）」。

`check-stack-ready.js --self-test` は同じ判定をスタブで持つが、**それは「判定器が正しい」までしか言わない。**
**稼働クラスタで実際に赤くなること**は別に測る —— 使い捨てのブランチへ `kubectl patch` の段を足して
`workflow_dispatch` を撃ち、**赤を実測してからその段を捨てる**（run の記録は残る）。

🔴 **patch の段を本流へ残さない。** CI が自分で field manager を奪う手を常設すると、
**G12 が守っている性質そのものを CI が壊せる**ようになる。

## テスト（受け入れ基準）

- [ ] Given 統合スタック / When G12 が走る / Then **走査対象 0 件で緑を返さない**（[[IADR-0407]] 決定 1・実装済み）
- [ ] Given メッシュを入れた統合スタック / When 何も patch しない / Then **緑**（陰性対照・run 34138046452 で実測済み）
- [ ] Given 同じスタック / When `kubectl patch` で `PeerAuthentication` の `spec` を書く /
      Then **G12 が赤になる**（🔴 本体。稼働クラスタで実測する）
- [ ] Given 採らなかった案 3 / When 記録を読む / Then **理由が [[IADR-0407]] 決定 5 に在る**
- [ ] `ISTIO` の宣言がジョブレベルの `env` に 1 度だけ在り、**既定で `1`** である（自己診断で固定）
- [ ] **手動で `false` にすればメッシュ無しで起こせる**（比較の退路。自己診断で固定）
- [ ] up のコマンド行へ `ISTIO=1` を直書きしていない（門へ届かない形の再発防止）

## 変異試験（実装後に実出力で埋める）

## やらないこと

- `ISTIO_MTLS_MODE=STRICT` の宣言（[[IADR-0307]] 決定 4 の段取り）
- `kubectl patch` の段を本流のワークフローへ残すこと（決定 3）
- `docs/ai-workflow.md` の必須チェック表を変えること（本ワークフローは PR ゲートではない）
