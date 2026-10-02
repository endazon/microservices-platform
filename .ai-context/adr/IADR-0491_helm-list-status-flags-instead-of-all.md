---
title: IADR-0491 現行のメッシュ宣言を読む helm list は -a / --all をやめ、状態の旗 6 つの和を明示して helm v3 / v4 の両方で同じ集合を読む
type: impl-adr
status: Accepted
related_ids: [NFR-16, ADR-0005, IADR-0487, IADR-0488, IADR-0377]
author: claude
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-16 通信暗号化。恒久: サービス間 mTLS)
  - planning:projects/microservices-platform/07_adr/ADR-0005 (サービスメッシュ / Istio / mTLS)
related_specs:
  - ../specs/20261002_1722_helm-v4-list-compat.md
---

# IADR-0491: 現行のメッシュ宣言を読む helm list は状態の旗 6 つの和で絞り、-a / --all を使わない（#1722）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-02
- 決定者: claude（#1722）

## 起点・関連

- 関連する計画書 ID: NFR-16（通信暗号化。恒久: サービス間 mTLS）
- 関連する計画 ADR: ADR-0005（メッシュ / mTLS）
- 関連する実装 ADR: [[IADR-0487]]（読む口 `current_mesh_mtls_mode` と fail-closed。本決定はその「リリースの有無」の確かめ方だけを替える）、
  [[IADR-0488]]（`ISTIO` 未指定の再実行も同じ 1 回の読みを使う）、[[IADR-0377]]（mode は helm だけが書く）

## コンテキストと課題

`scripts/lib/mesh-mtls-mode.sh` の `current_mesh_mtls_mode` は、リリースの有無を `helm list -n <ns> -a -q --filter '^msp$'` で確かめていた
（IADR-0487 論点 2）。**helm v4 は list の `-a` / `--all` を廃した。** 旗の解析で `Error: unknown shorthand flag: 'a' in -a`・終了コード 1 になり、
`|| return 2`（読めない）へ倒れる。その結果、`ISTIO` か `ISTIO_MTLS_MODE` を付けない `k8s-local-up.sh` は**リリースの無い初回でも** [1/7] の直後に止まる。

- 稼働 PC（helm v4.2.1）: #1722。`ISTIO_MTLS_MODE` を明示すれば通る。
- CI integration-stack（`azure/setup-helm@v5` の `latest` ＝ v4.3.0）: run 36993205504 が `ERROR: 現行の mesh.mtlsMode を helm リリース msp から読めませんでした` で
  [1/7] の直後に落ちた（#1714。f63db8c2 以降ずっと赤）。
- 試験の helm スタブは list の旗を見ずに名前を返していたので、実機が拒否する形が緑のまま入った。

守る契約（IADR-0487 / IADR-0488）: 読めないときは止まる（fail-closed）・リリースが無ければ従来どおり（初回）・読みは 1 回。
加えて、IADR-0487 の補足「`-a` は `failed`・`pending-*` 等も『在る』として返し、引き継げる」を落とさない。

## 実測（2026-10-02）

helm v3.12.3 / v3.22.0（get.helm.sh から取得。v3.22.0 は同日時点で取得できた v3 の最新）/ v4.2.1。クラスタの代わりに、`/version` だけに答える
擬似 API ＋ `HELM_DRIVER=memory`（`HELM_MEMORY_DRIVER_DATA` で状態ごとのリリースを 1 件置く）で list の絞りを測った。届かない世界は `KUBECONFIG=/nonexistent`。

**ヘルプ**: v3 は `-a, --all  show all releases without any filter applied`、既定は「deployed or failed だけ」。v4 は `-a` / `--all` が無く、
既定は「all releases in any status」。状態の旗 `--deployed --failed --pending --superseded --uninstalling --uninstalled` は両方に在る。

**`current_mesh_mtls_mode` の戻り（終了コード/出力）を、実装の 3 つの形 × 3 版で実走した:**

| 形 ＼ 状態 | 無い | deployed | failed | pending-install / -upgrade | superseded | uninstalled | 届かない |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 従前 `-a`（v3.12 / v3.22） | 1 | 0/STRICT | 0/STRICT | 0/STRICT | 0/STRICT | 0/STRICT | 2 |
| 従前 `-a`（v4.2.1） | **2** | **2** | **2** | **2** | **2** | **2** | 2 |
| 案 a 旗なし（v3.12 / v3.22） | 1 | 0/STRICT | 0/STRICT | **1** | **1** | **1** | 2 |
| 案 a 旗なし（v4.2.1） | 1 | 0/STRICT | 0/STRICT | 0/STRICT | 0/STRICT | 0/STRICT | 2 |
| **案 c 6 旗の和（3 版とも）** | 1 | 0/STRICT | 0/STRICT | 0/STRICT | 0/STRICT | 0/STRICT | 2 |

`unknown` 状態は v3 の `-a`・v4 の既定・6 旗の和のいずれも返さない（3 者で同じ）。

**`helm status` の終了コード**: 無い（`Error: release: not found`）も届かない（v3 `Error: Kubernetes cluster unreachable: …` / v4 `Error: kubernetes cluster unreachable: …`）も **1**。
v3 と v4 で文言の大文字・小文字まで変わっている。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| a | `-a` を外すだけ | ✗ v4 では通るが、v3 では既定が deployed / failed に絞られ、`pending-*`・`superseded`・`uninstalled` のリリースを「無い」（1）と読む。中断した upgrade の後の再実行が初回扱い（メッシュ無し・PERMISSIVE）へ倒れ、IADR-0487 の補足と fail-closed の趣旨を v3 で破る |
| b | `helm status "$release" -n "$ns"` の終了コードで有無を判定 | ✗ 無いと届かないがどちらも 1。区別には stderr の文言を読むしかなく、その文言は v3 → v4 で既に変わっている。区別できなければ「読めない」を「無い」へ倒す（#1710 そのもの）か、初回を毎回止めるかのどちらかになる |
| **c** | **状態の旗 6 つの和 `--deployed --failed --pending --superseded --uninstalling --uninstalled` を明示する**（**採用**） | ○ 両方の版に在る旗だけで、v3 の `-a` と同じ集合を v3 / v4 の両方で返す（上の実測）。届かなければ従来どおり非 0 → 2。読みは 1 回のまま |
| d | `helm version` で版を見て v3 なら `-a` を付ける | ✗ 呼び出しが 1 回増え、分岐が版の数だけ増える。c で足りる |

## 決定

1. `current_mesh_mtls_mode` のリリースの有無は `helm list -n "$ns" -q --filter "^${release}\$" --deployed --failed --pending --superseded --uninstalling --uninstalled` で確かめる。
   `-a` / `--all` は使わない。終了コードの意味（0 宣言あり／1 無い・未宣言／2 読めない）と、`helm get values`（`--all` なし）の読み先は IADR-0487 のまま。
2. 試験の helm スタブを実機に寄せる。`STUB_HELM_MAJOR=4`（既定）は list の `-a` / `--all` を v4 と同じ文言・終了コード 1 で拒否し、旗が無ければ全状態を返す。
   `STUB_HELM_MAJOR=3` は `-a` / `--all` を全状態、旗なしを deployed / failed に絞る。`STUB_HELM_STATUS` でリリースの状態を与える。
   スタブ自身が実測表を写していることも試験で固定する。

## 理由

- 起動器が触る helm は PoC・CI とも v4 だが、v3 の利用者を切る理由が無い。両方に在る旗だけで同じ集合を読めるなら、版で分岐しない形がいちばん小さい。
- 有無の判定を stderr の文言に預けると、版の更新で黙って fail-open になり得る。list の「空で 0／失敗で非 0」は v3 / v4 で変わっていない。

## 結果

- 正: helm v4 でも `ISTIO` / `ISTIO_MTLS_MODE` 未指定の初回・再実行が止まらない。CI integration-stack の [1/7] 直後の失敗（#1714）は同じ原因で解ける。
- 正: v3 でも `pending-*` 等のリリースを「在る」と読み、宣言を引き継ぐ（IADR-0487 の補足のまま）。
- 負: 旗の列が長くなる。helm が状態を足しても自動では含まれない（v3 の `-a` も v4 の既定も `unknown` を返さず、現時点で 3 者は同じ集合）。
- 残余: `k8s-local-down.sh` の 8 段目は `helm list -A --no-headers`（旗なし）で残りのリリースを数える。v4 では全状態だが、v3 では deployed / failed だけになり
  `pending-*` を数え漏らし得る。v4 で旗の解析は通り（本件の失敗の型ではない）、PoC・CI とも v4 なので本件では変えない。
- 残余: `k8s-local-up.sh` の読めないときの ERROR 文は、`helm list` で落ちたときも `helm get values` を名指しする（従前から。本件では文言を変えない）。

## 関連

- 作業仕様書: `.ai-context/specs/20261002_1722_helm-v4-list-compat.md`（母集合・変異の結果）
- [[IADR-0487]] へ、本決定を指す日付つき追記を置いた。
