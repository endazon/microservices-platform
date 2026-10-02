---
title: IADR-0487 k8s-local-up.sh の ISTIO_MTLS_MODE を付けない再実行は helm の宣言から現行の mesh.mtlsMode を引き継ぎ、読めなければ止める
type: impl-adr
status: Accepted
related_ids: [NFR-16, ADR-0005, ADR-0021, IADR-0377, IADR-0317, IADR-0307, IADR-0488, IADR-0491]
author: claude
created: 2026-10-01
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-16 通信暗号化。恒久: サービス間 mTLS)
  - planning:projects/microservices-platform/07_adr/ADR-0005 (サービスメッシュ / Istio / mTLS)
related_specs:
  - ../specs/20261001_issue-1710_mesh-mtls-mode-inherit.md
---

# IADR-0487: 再実行は現行の mTLS モードを引き継ぎ、読めなければ止める（#1710）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-01
- 決定者: claude（#1710。監査からの owner 起票）

## 起点・関連

- 関連する計画書 ID: NFR-16（通信暗号化。恒久: サービス間 mTLS）
- 関連する計画 ADR: ADR-0005（メッシュ / mTLS）、ADR-0021（入口＝Istio Ingress Gateway）
- 関連する実装 ADR: [[IADR-0377]]（mode を書くのは helm だけ。決定 2 の一時降格・決定 3 の「未設定なら PERMISSIVE」）、
  [[IADR-0317]]（#1691 の 2026-09-28 追記: 移行済みの再実行）、[[IADR-0307]] 決定 4（段取り）

## コンテキストと課題

`k8s-local-up.sh` の [6/7] は `--reuse-values` 無しで `--set mesh.mtlsMode=${ISTIO_MTLS_MODE:-PERMISSIVE}` を渡す。
`ISTIO_MTLS_MODE` を付けない再実行は、**STRICT で動いているクラスタを PERMISSIVE へ黙って戻す**。
#1694 で足した拒否のエラー文が勧める `ISTIO=1 LOCALEDGE=1 bash scripts/k8s-local-up.sh --live` をそのまま写すと、この形になる。
IADR-0377 決定 3 の「未設定なら PERMISSIVE」は**初回の既定**としては正しいが、再実行では降格の口になっていた。

決めることは 3 点である。

1. 未指定の再実行で何を選ぶか
2. 現行の値をどこから読むか
3. 読めないときにどちらへ倒すか

## 検討した選択肢

### 論点 1: 未指定の再実行で何を選ぶか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | 従前のまま（未指定＝PERMISSIVE）。エラー文に `ISTIO_MTLS_MODE=STRICT` を足すだけ | ✗ エラー文を経ない再実行（手で打つ・履歴から呼ぶ）は引き続き黙って降格する |
| B | [6/7] に `--reuse-values` を足す | ✗ `values-local.yaml` から消したキーや、以前の実行で `--set` した opt-in（`LOCALEMBED` 等）が前の release の値として残り続ける。mode 以外の挙動まで変わる |
| **C** | **明示 ＞ 現行 ＞ PERMISSIVE**（**採用**） | ○ 明示の昇格・降格は従来どおり効き、未指定は現状を保つ。初回は従来どおり |

### 論点 2: 現行の値をどこから読むか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **a** | **`helm get values msp -o yaml`（利用者が与えた値）の `mesh.enabled` / `mesh.mtlsMode`**（**採用**） | ○ 書く口は helm ただ 1 つ（IADR-0377）なので helm の宣言が正。外部コマンド（jq 等）を増やさず awk で読める |
| b | `helm get values --all` | ✗ チャート既定（`values.yaml` の `mtlsMode: STRICT`）が混ざり、メッシュを宣言していないリリースを STRICT と読む |
| c | 稼働の `PeerAuthentication` を kubectl で読む | △ 実際の状態ではあるが、CRD 未導入・リソース無し・読めないの区別が増える。宣言との乖離は G12 が別に落とすので、起動器は宣言を正とする |

リリースの有無は `helm list -n <ns> -a -q --filter '^msp$'`（無ければ空で 0）で確かめ、**「無い」と「読めない」を分ける**。
`mesh.enabled` が true でない（`values-local.yaml` の `false` のまま）なら、引き継ぐものが無い＝初回と同じ扱いにする。

### 論点 3: 読めないときにどちらへ倒すか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| i | PERMISSIVE へ倒す（初回扱い） | ✗ #1710 そのもの（STRICT を黙って緩める） |
| ii | STRICT へ倒す | ✗ PERMISSIVE で動いているクラスタ、特に入口がまだ Traefik（メッシュ外）の構成を推測で STRICT にすると入口が 502 になる（#1072 の実測）。安全側に見えて別の事故を作る |
| **iii** | **[2/7] の前（副作用より前）に非 0 で止め、`ISTIO_MTLS_MODE` の明示を求める**（**採用**） | ○ 何も書き換えない唯一の側。利用者は 1 語足すだけで進める |

対象は `helm list` / `helm get values` の失敗、`mesh.enabled: true` なのに `mtlsMode` が無い、値域（STRICT / PERMISSIVE / DISABLE）外である。
明示（env）があるときは読まない（読めなくても止めない）。

## 決定

1. `ISTIO=1` かつ `ISTIO_MTLS_MODE` が空の実行は、`[2/7]` の前に `scripts/lib/mesh-mtls-mode.sh` の `current_mesh_mtls_mode` で現行の値を読む。
   読めたら `ISTIO_MTLS_MODE` へ引き継ぎ、`INFO: mesh.mtlsMode は現行の <mode> を引き継ぎます（ISTIO_MTLS_MODE 未指定）` を出す。
   以降は明示されたのと同じに扱う（未移行の `LOCALEDGE=1` なら IADR-0377 決定 2 のとおり [6/7] は PERMISSIVE、`istio-edge-up.sh` [5/5] で戻す）。
2. リリースが無い・メッシュ未宣言なら初回とみなし PERMISSIVE（IADR-0377 決定 3 は**初回の既定**として据え置く）。
3. 読めないときは止める（論点 3 の iii）。
4. #1694 のエラー文は `ISTIO=1 LOCALEDGE=1 ISTIO_MTLS_MODE=STRICT bash scripts/k8s-local-up.sh --live` を勧め、PERMISSIVE の場合と省いたときの挙動を添える。
5. `ISTIO` 未設定の既定経路では読まない（既定のバイト等価。`k8s-local-up.test.js` が固定する）。

## 理由

- 再実行は「現状を収束させる」操作であり、利用者が言っていないモードの変更を起こすべきではない。昇格・降格は明示でだけ起こす。
- 降格と昇格は対称に危険である（降格は NFR-16 の統制を外し、昇格は入口を 502 にし得る）。推測で片方を選ばず、止めて明示を求める。

## 結果

- 正: STRICT のクラスタに `ISTIO_MTLS_MODE` を付けずに再実行しても STRICT のまま。PERMISSIVE のクラスタも勝手に上がらない。
- 負: `ISTIO=1` の実行は helm を 2 回（`list` と `get values`）余分に叩く。helm に届かない環境では未指定の再実行が止まる（明示すれば進む）。
- 負: 引き継いだ STRICT の再実行では `istio-edge-up.sh` [5/5] が `--reuse-values` の helm upgrade を 1 回余分に走らせる（明示の STRICT と同じ）。
- 試験: `k8s-local-up.test.js` の helm スタブに既存リリースの模型（`STUB_HELM_VALUES` ほか）を足し、#1710 節で受け入れ基準と純関数の判定表を固定した。
  プローブの表（規則 11）と変異の結果は作業仕様書。

［2026-10-01 追記 / #1710］独立監査の是正と補足:

- **論点 2 の案 b の理由を正す。** 「`--all` だとメッシュ未宣言のリリースを STRICT と読む」は不正確だった。`values-local.yaml` が `mesh.enabled: false` を利用者の値として与えるので、`--all` でも未宣言のリリースは enabled=false のまま「初回」に落ちる。`--all` が実害を出すのは **`mesh.enabled: true` なのに `mtlsMode` を宣言していない**場合で、本来は「読めない」で止まるべきところをチャート既定の STRICT として読む。採らない結論は変わらない。
- **読み先の形を試験で固定した。** helm スタブを実機に寄せた（リリースは namespace `microservices-platform` にだけ在り、`-n` を落とすと list は空・get values は not found、`get values` の `--all` は拒否）。起動器の経路の試験で、読み先が `helm get values msp -n microservices-platform -o yaml` であることを固定した（独立監査の変異 M2 `--all` 混入・M6 `-n` 欠落はいずれも殺した）。
- **`helm list -a` が拾う状態。** `-a` は `deployed` 以外（`failed`・`pending-upgrade`・`pending-install` 等）も「在る」として返す。これらでも `helm get values` は最後に与えた値を返すので引き継げる。読めなければ「読めない」として止まる（fail-closed）ので、状態の種類で緩む経路は無い（PR の AI レビューの補足）。
- **残余（本件の射程外・別件）**: Istio で動いているクラスタで **`ISTIO` を付けずに**再実行すると、[6/7] が mesh の `--set` を付けずに upgrade し、`values-local.yaml` の `mesh.enabled: false` に戻ってメッシュ宣言がまるごと外れる（以前からの挙動で本件の退行ではない）。MSP#1713 で扱う。

［2026-10-02 追記 / #1713］上の残余（`ISTIO` を付けない再実行でメッシュ宣言がまるごと外れる）は [[IADR-0488]] で塞いだ。
`ISTIO` は 3 値（`1`／`0`／未指定＝現行を引き継ぐ）になり、未指定の再実行は本 ADR の読む口 `current_mesh_mtls_mode` を 1 回だけ呼んで
`mesh.enabled` と `mesh.mtlsMode` の両方を引き継ぐ。決定 5「`ISTIO` 未設定の既定経路では読まない（既定のバイト等価）」は成り立たなくなり、
バイト等価は `ISTIO=0` の側へ移った（`k8s-local-up.test.js` の #1713 節が固定する）。

［2026-10-02 追記 / #1722］論点 2 のリリースの有無の確かめ方 `helm list -n <ns> -a -q --filter '^msp$'` は、helm v4 が list の `-a` / `--all` を
廃したため旗の解析で 1 になり、常に「読めない」へ倒れていた（稼働 PC の v4.2.1・CI の v4.3.0。初回でも止まる）。
状態の旗 6 つの和（`--deployed --failed --pending --superseded --uninstalling --uninstalled`）へ替えた。v3 の `-a` と同じ集合を
v3 / v4 の両方で返すことを実測したので、上の「`helm list -a` が拾う状態」の補足は旗を替えたまま成り立つ。判断は [[IADR-0491]]。

## 関連

- 作業仕様書: `.ai-context/specs/20261001_issue-1710_mesh-mtls-mode-inherit.md`
- [[IADR-0377]] 決定 3 と [[IADR-0317]] の #1691 追記へ、本決定を指す日付つき追記を置いた。
