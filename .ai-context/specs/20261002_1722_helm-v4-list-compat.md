---
title: helm v4 が受け付けない helm list -a をやめ、helm v3 / v4 の両方で現行のメッシュ宣言を読む（#1722 / #1714）
type: spec
status: done
related_ids: [NFR-16, ADR-0005, IADR-0491, IADR-0487, IADR-0488]
author: claude
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-16 通信暗号化。恒久: サービス間 mTLS)
  - planning:projects/microservices-platform/07_adr/ADR-0005 (サービスメッシュ / Istio / mTLS)
issue: "#1722"
---

# 仕様書: helm v4 でも現行のメッシュ宣言を読めるようにする（#1722 / #1714）

> 本仕様書は実装着手前に作成する。起点は #1722（稼働 PC の helm v4.2.1 で `k8s-local-up.sh` が [1/7] の直後に止まる）。
> 同じ原因で CI integration-stack が赤のまま（#1714）。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-16**（通信暗号化。恒久: サービス間 mTLS）。読む口は #1710 / #1713 で NFR-16 の統制（STRICT・メッシュ宣言を黙って外さない）のために置いたもので、
  その口が helm v4 で常に「読めない」になり、起動器そのものが止まっている。番号は #1710 / #1713 と同じ。
- 計画 ADR: ADR-0005（メッシュ / mTLS）
- 関連 IADR: IADR-0487（読む口・fail-closed）、IADR-0488（`ISTIO` 未指定の引き継ぎ）。本件の判断は **IADR-0491** に置く。

## 何が起きているか

- `scripts/lib/mesh-mtls-mode.sh` の `current_mesh_mtls_mode` は `helm list -n "$ns" -a -q --filter "^${release}\$"` を呼ぶ。
- helm v4 は list の `-a` / `--all` を廃した。`Error: unknown shorthand flag: 'a' in -a`・終了コード 1 → `|| return 2`（読めない）。
- `k8s-local-up.sh` は `ISTIO` 未指定、または `ISTIO=1` かつ `ISTIO_MTLS_MODE` 未指定のとき、この読みが 2 なら [2/7] の前に止まる（fail-closed）。
  **リリースの無い初回でも止まる**（list が旗の解析で落ちるので、有無を見る前に 2 になる）。
- CI integration-stack: run 36993205504 は `azure/setup-helm@v5`（`version: latest`）で **v4.3.0** を入れ、`ISTIO: 1`・`ISTIO_MTLS_MODE` 未指定・新しい k3d クラスタで
  `k8s-local-up.sh --live` を流し、`==> [1/7] cluster` の直後に `ERROR: 現行の mesh.mtlsMode を helm リリース msp から読めませんでした（helm get values msp -n microservices-platform）。`
  で exit 1 になった（ジョブログで確認）。f63db8c2（#1712）以降の develop で同じ形。→ #1714 は本件と同じ原因。
- 試験の helm スタブ（`k8s-local-up.test.js` の `HELM_STUB`）は list の旗を見ずに名前を返していた（`--all` を拒否していたのは `get values` の側だけ）。
  実機が拒否する形が緑のまま入った。

## 実測（2026-10-02）

版: helm **v4.2.1**（稼働 PC と同じ版。scratchpad の既存バイナリ）、**v3.22.0**（get.helm.sh。同日 3.22.1 以降は 404 で、取得できた v3 の最新）、**v3.12.3**（古い v3 の代表）。

1. **ヘルプ**（`helm list --help`）: v3 は `-a, --all  show all releases without any filter applied`・既定は「deployed or failed だけ」。
   v4 は `-a` / `--all` が無く、既定は「all releases in any status」。`--deployed --failed --pending --superseded --uninstalling --uninstalled` は両方に在る。
2. **旗の解析（`KUBECONFIG=/nonexistent`＝届かない）**: v4 の `list … -a` は `Error: unknown shorthand flag: 'a' in -a`、`--all` は `Error: unknown flag: --all`、
   いずれも stdout 空・終了コード 1。v3 は同じ指定で `Error: Kubernetes cluster unreachable: …`（旗は通る）。6 旗の和は v3 / v4 とも `cluster unreachable`（旗は通る）。
3. **状態の絞り**: `/version` だけに答える擬似 API ＋ `HELM_DRIVER=memory`（`HELM_MEMORY_DRIVER_DATA` で状態ごとに msp を 1 件）で `helm list -n ns -q --filter '^msp$'` を測った。

   | 状態 | v3 旗なし | v3 `-a` | v4 旗なし | 6 旗の和（v3.12.3 / v3.22.0 / v4.2.1） |
   | --- | --- | --- | --- | --- |
   | 無い | 空 | 空 | 空 | 空 |
   | deployed / failed | msp | msp | msp | msp |
   | pending-install / -upgrade / -rollback | **空** | msp | msp | msp |
   | superseded / uninstalling / uninstalled | **空** | msp | msp | msp |
   | unknown | 空 | 空 | 空 | 空 |

4. **関数ごとの実走**（`current_mesh_mtls_mode` を実バイナリの下で呼んだ戻り値。表は IADR-0491）: 従前の `-a` は v4 で全状態 2、
   旗なし（案 a）は v3 で pending-* 等が 1（無い扱い）、**6 旗の和は 3 版とも 無い=1・在る=0/STRICT・届かない=2**。
5. **`helm status`**: 無い＝`Error: release: not found`、届かない＝v3 `Error: Kubernetes cluster unreachable` / v4 `Error: kubernetes cluster unreachable`。**どちらも終了コード 1**。

## 直し方（決定。詳細と却下案は IADR-0491）

1. `current_mesh_mtls_mode` の list を `helm list -n "$ns" -q --filter "^${release}\$" --deployed --failed --pending --superseded --uninstalling --uninstalled` にする。
   - 案 a（`-a` を外すだけ）は v3 で pending-* 等を「無い」と読み、初回扱いへ倒れる（IADR-0487 の補足「`-a` は pending-* も拾う」を破る）ので採らない。
   - 案 b（`helm status` の終了コード）は無いと届かないを分けられない（どちらも 1、文言は版で変わる）ので採らない。fail-closed を壊さない。
2. 契約は変えない: 0＝宣言あり／1＝リリース無し・未宣言／2＝読めない。読みは 1 回（list 1 回、在れば get values 1 回）。
3. スタブを実機に寄せる（`STUB_HELM_MAJOR` 既定 4・`STUB_HELM_STATUS`）。v4 は `-a` / `--all` を実機と同じ文言・終了コード 1 で拒否し、旗なしは全状態。
   v3 は `-a` / `--all` を全状態、旗なしは deployed / failed。状態の旗は和で効く。

## 規則 11（窓の形の表）

**該当なし。** 本件は「時間差のある 2 つの端（前の宣言と後の指定）」を突き合わせる是正ではなく、同じ時点の 1 回の読みの**旗の互換**の是正である。
増える側／減る側の窓が無いので 3 形の表は作らない。代わりに、版（v3 / v4）× 形（従前 `-a`／案 a／案 c）× リリースの状態の実測表（上の 3・4 と IADR-0491）で形を決めた。

## 母集合（規則 9: helm の呼び出しを scripts・.github・deploy・docs で走査）

走査は 2026-10-02・`origin/develop` = `b4eb21ce`（変更前）。生の出力に対して判断した（規則 7）。本仕様書は走査範囲の外（`.ai-context/`）なので自己参照は無い（規則 8）。

- 軸 1（helm を語として含むファイル）: `git grep -l -E '(^|[^[:alnum:]_./-])helm[[:space:]]' origin/develop -- scripts .github deploy docs` ＝ **69 ファイル**。
- 軸 2（list の呼び出し）: `git grep -n -E '(^|[^[:alnum:]_./-])helm[[:space:]]+(list|ls)([[:space:]]|$)' origin/develop -- scripts .github deploy docs` ＝ 14 行
  （実装 1 行 `scripts/lib/mesh-mtls-mode.sh:105`、残り 13 行は試験の名前・アサーション）。加えて口を介した呼び出し `helm_read list` ＝ 2 行（`k8s-local-down.sh:129`・同 test の静的検査の素材）。
- 軸 3（JS の配列形）: `spawnSync('helm', …)` / `run('helm', …)` ＝ 4 行（`check-stack-ready.js` の `get values -a` / `get manifest` / `template`、`check-deploy-manifests.js` の `lint` / `template`）。
- 軸 4（`-a` / `--all` を helm の行に持つもの）: `git grep -n -E '(^|[^[:alnum:]_./-])helm[[:space:]][^|;]*[[:space:]](-a|--all)([[:space:]]|$|`|\))' origin/develop -- scripts .github deploy docs` ＝ 6 行。
- 軸 5（旗の互換）: 軸 1 の行（コード 163 行・Markdown 479 行。`.md` は全リポジトリを含む上位集合で引いた）と、`\` で続く複数行の呼び出し 7 件
  （`k8s-local-up.sh` 486・488・559・686・707、`istio-edge-up.sh` 75、`values-local.yaml` 2 のコメント）から（副コマンド, 旗）の対を抜き出し（26 対 ＋ 継続行の
  `--wait` / `--timeout` / `--create-namespace`）、**v4.2.1 の `helm <副コマンド> --help` に無い旗**を引いた。続けて実際の形を v3.22.0 / v4.2.1 で解析させた
  （`list -A --no-headers`・`get values -a -o json`・`get manifest --revision`・`status`・`uninstall`・`upgrade --install … --create-namespace --version --wait --timeout`・
  `upgrade … --reuse-values --take-ownership -f --set`・`install`・`template -f --set --show-only -s`）。**旗の解析で落ちるのは `list -a` だけ**だった。

| 呼び出し | v4 の互換 | 扱い |
| --- | --- | --- |
| `scripts/lib/mesh-mtls-mode.sh` `current_mesh_mtls_mode` の `helm list -n … -a -q --filter` | ✗ `-a` を拒否 | **是正**（6 旗の和） |
| `scripts/lib/mesh-mtls-mode.sh` `set_mesh_mtls_mode` の `helm status` / `helm upgrade --reuse-values --set` | ○ | **対象外**: `status` は有無だけを見て無ければ 0 で返す（書く口。届かない世界では続く upgrade が落ちる）。本件の失敗の型ではない |
| `scripts/k8s-local-down.sh:129` `helm_read list -A --no-headers` | ○（`-A` は `--all-namespaces`。両版に在る） | **対象外・残余**: v3 では旗なしの既定が deployed / failed に絞られ、8 段目が pending-* を数え漏らし得る。PoC・CI とも v4（全状態）なので本件では変えない（IADR-0491 残余） |
| `scripts/check-stack-ready.js` の `helm get values … -a -o json` | ○（`get values` の `-a` は v4 にも在る） | **対象外**: G12 は意図してチャート既定込みを読む（IADR-0377） |
| `scripts/check-stack-ready.js` / `check-deploy-manifests.js` の `get manifest` / `template` / `lint` | ○ | **対象外** |
| `k8s-local-up.sh` / `istio-edge-up.sh` の `helm upgrade --install …`（`--create-namespace --version --wait --timeout -f --set`）・`helm repo add/update` | ○ | **対象外** |
| `scripts/README.md:44`・`check-stack-ready.js:100/345/648` の `helm get values -a`（軸 4） | ○（`get values` の旗） | **対象外**: 記述は正しい |
| `k8s-local-up.test.js:214` スタブの `get values` の `--all` / `-a` 拒否（軸 4） | — | **据え置き**（`get values` の `--all` を拒否する意図は IADR-0487 のまま） |
| IADR-0377 / #1159 仕様書 / `deploy/istio/README.md:124` / `scripts/README.md:67` の `helm upgrade … --force` | v4 では `--force` が無い | **対象外**: 呼び出しではなく「`--force` は効かない」という実測の記述。リポジトリのどこからも `--force` 付きの upgrade は呼ばない |
| `.github/workflows/{ci,integration-stack}.yml` の `azure/setup-helm@v5` | `latest`＝v4 | **対象外**: 版を v3 へ固定して逃げない（稼働 PC も v4） |
| `scripts/k8s-local-up.test.js` の `HELM_STUB` の list | 旗を見ない | **是正**（上の 3） |
| `scripts/k8s-local-down.test.sh` の helm スタブ（list は旗を見ずに `releases` を返す） | — | **対象外**: down は `-A --no-headers` しか渡さず、v3 / v4 の差は状態の絞りだけ（残余と同じ）。旗の拒否を写す必要が無い |
| `scripts/reset-floor.test.js` の helm スタブ（全呼び出し 0） | — | **対象外**: `istio-edge-up.sh` の `set_mesh_mtls_mode`（`status` / `upgrade`）だけを通る |

**規則 10（この変更で新たに誤りになる自分の記述）**: `-a` を述べる箇所は軸 4 と `.ai-context` の引き直しで
`IADR-0487`（論点 2 の本文・2026-10-01 追記の「`helm list -a` が拾う状態」）と `20261001_issue-1710_mesh-mtls-mode-inherit.md`（45 行）の 2 ファイル。
仕様書は凍結記録なので書き換えない。IADR-0487 は本文を書き換えず、日付つき追記で IADR-0491 を指す（補足の結論「pending-* も在ると読む」は 6 旗で成り立つことを書く）。
`scripts/README.md:67` の `lib/mesh-mtls-mode.sh` 行は list の旗を述べていないので変えない。`k8s-local-up.sh` の ERROR 文が list の失敗でも `helm get values` を名指しする点は従前からで、本件では変えない（IADR-0491 残余）。

## 変更するもの

| ファイル | 変更 |
| --- | --- |
| `scripts/lib/mesh-mtls-mode.sh` | `current_mesh_mtls_mode` の list を 6 旗の和へ。注記 |
| `scripts/k8s-local-up.test.js` | `HELM_STUB` の list を実機に寄せる（`STUB_HELM_MAJOR` / `STUB_HELM_STATUS`）。#1722 節を足す |
| `.ai-context/adr/IADR-0491_...md`（新規）・`README.md`（索引） | 判断の記録 |
| `.ai-context/adr/IADR-0487_...md` | 日付つき追記（IADR-0491 を指す）・`related_ids` |

## 受け入れ基準（→ 試験。`k8s-local-up.test.js` の #1722 節）

1. スタブが実測を写す: v4 は `-a` / `--all` を実機の文言・終了コード 1 で拒否（届かない世界でも旗の拒否が先）。状態の絞りが上の表と一致。
2. 読み先の list は 1 回だけで、形が `helm list -n microservices-platform -q --filter ^msp$ --deployed --failed --pending --superseded --uninstalling --uninstalled`。
3. v4 / v3 の各スタブで、初回（リリース無し）は `ISTIO` / `ISTIO_MTLS_MODE` 未指定でも止まらずメッシュ無し。CI の形（`ISTIO=1`・mTLS 未指定・新しいクラスタ）も止まらず PERMISSIVE。
4. v4 / v3 の各スタブで、宣言あり（STRICT）は引き継ぎ、宣言なしは外したまま、`ISTIO=1` でも STRICT を引き継ぐ。
5. v4 / v3 の各スタブで、failed・pending-install / -upgrade / -rollback・superseded のリリースも在ると読み、宣言と STRICT を引き継ぐ。
6. v4 / v3 の各スタブで、helm に届かない・`get values` が失敗するときは `ISTIO` 未指定・`ISTIO=1` のどちらでも従来どおり副作用の前に止まる。
7. 既存の #1710 / #1713 節（fail-closed・読みは 1 回・`ISTIO=0` は読まない）が既定の v4 スタブの下で緑のまま。

## 変異（2026-10-02 実測）

`k8s-local-up.test.js` は最初の失敗で止まるので、各変異で最初に赤になった試験を記す。変異は作業ツリーとは別の worktree（`origin/develop` に本件の 2 ファイルを写したもの）へ当てた。
変異前の全件は **257 件で緑**。

| 変異 | 結果（exit=1 と最初の失敗） |
| --- | --- |
| M1. `-a` を戻す（従前の形。6 旗を外す） | 赤: 前提「既定実行は exit 0」で `ERROR: 現行のメッシュ宣言（mesh.enabled / mesh.mtlsMode）を helm リリース msp から読めませんでした` ＝ #1722 / #1714 の再現 |
| M2. `-a` を外すだけ（案 a） | 赤: 「helm list の形が違う（1 回だけ・-a なし・6 旗）」。形の試験を無効にした M2b でも赤: helm v3 相当の「pending-install: リリースを「無い」と読み、メッシュ宣言を黙って外した」（v4 相当の同じ試験は緑＝v3 でだけ壊れることを分けて捕まえる） |
| M3. `--pending` だけ落とす | 赤: 形の試験。形の試験を無効にした M3b でも赤: helm v4 相当の「pending-install: …初回の扱いへ倒れた」 |
| M4. list の失敗を「無い」へ倒す（`|| return 2` を `|| return 1`） | 赤: 既存の #1710 節「helm list が失敗: 読めないのに進んだ（PERMISSIVE へ倒すと STRICT を黙って緩める）」 |

## 検証

- `node scripts/k8s-local-up.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / `bash scripts/k8s-local-down.test.sh`
- `node scripts/check-adr-numbering.js` / `check-trace-blocks.js` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js` / `check-doc-links.js` / `gen-knowledge-graph.js --check` / `bash -n`
- `node scripts/check-commit-messages.js --range origin/develop..HEAD`
- 稼働クラスタでは実行しない（スタブ ＋ 実 helm バイナリの擬似 API / memory driver での実測のみ）。
