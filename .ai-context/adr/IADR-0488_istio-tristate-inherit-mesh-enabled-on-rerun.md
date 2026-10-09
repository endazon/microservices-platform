---
title: IADR-0488 k8s-local-up.sh の ISTIO を 3 値（1 / 0 / 未指定＝現行を引き継ぐ）にし、未指定の再実行は helm の宣言から mesh.enabled を引き継ぎ、読めなければ止める
type: impl-adr
status: Accepted
related_ids: [NFR-16, ADR-0005, ADR-0021, IADR-0487, IADR-0307, IADR-0317, IADR-0377]
author: claude
created: 2026-10-02
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-16 通信暗号化。恒久: サービス間 mTLS)
  - planning:projects/microservices-platform/07_adr/ADR-0005 (サービスメッシュ / Istio / mTLS)
related_specs:
  - ../specs/20261001_1713_mesh-enabled-inherit.md
  - ../specs/20261009_1850_dept-sync-carry-over.md
---

# IADR-0488: ISTIO を付けない再実行はメッシュ宣言を引き継ぎ、外すのは ISTIO=0 の明示だけにする（#1713）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-02
- 決定者: claude（#1713）／方針は利用者裁定（案 1＝引き継ぐ）

## 起点・関連

- 関連する計画書 ID: NFR-16（通信暗号化。恒久: サービス間 mTLS）
- 関連する計画 ADR: ADR-0005（メッシュ / mTLS）、ADR-0021（入口＝Istio Ingress Gateway）
- 関連する実装 ADR: [[IADR-0487]]（#1710: `ISTIO_MTLS_MODE` 未指定の引き継ぎ。読む口と fail-closed の型をそのまま使う）、
  [[IADR-0307]] 決定 1（`ISTIO=1` の opt-in・既定は不変）、[[IADR-0317]]（#1691: 移行済みの入口の拒否）、[[IADR-0377]]（mode は helm だけが書く）

## コンテキストと課題

`k8s-local-up.sh` の [6/7] は `--reuse-values` 無しで `helm upgrade --install msp … -f values-local.yaml $ISTIO_MESH_ARGS` を流す。
`ISTIO_MESH_ARGS` は `ISTIO=1` のときだけ `--set mesh.enabled=true …` で、`values-local.yaml` は `mesh.enabled: false` である。
したがって **Istio で動いているクラスタへ `ISTIO` を付けずに再実行すると、メッシュ宣言（PeerAuthentication・AuthorizationPolicy・
注入ラベルの宣言・`backchannelLogout.fromOutsideMesh`）がまるごと外れる**。#1710 が塞いだ「STRICT が黙って PERMISSIVE へ戻る」と同じ型であり、
IADR-0487 の残余として起票された。

決めることは 4 点である。

1. `ISTIO` 未指定の再実行で何を選ぶか（利用者裁定: 引き継ぐ）
2. 明示的に外す手段をどう与えるか
3. 読めないときにどちらへ倒すか
4. #1691 の拒否（移行済みの入口 × `ISTIO` 無し）との関係

## 検討した選択肢

### 論点 1・2: 値の意味

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | 従前のまま（`1` 以外は外す） | ✗ #1713 そのもの |
| B | [6/7] に `--reuse-values` を足す | ✗ IADR-0487 論点 1 の案 B と同じ（`values-local.yaml` から消したキーや以前の opt-in が残り続ける） |
| C | 未指定の再実行で現行が true なら**止めて**明示を求める | △ 黙っては外さないが、毎回の再実行に 1 語の追加を強いる。裁定は「引き継ぐ」 |
| **D** | **3 値: `1`＝入れる／`0`＝外す／未指定＝現行を引き継ぐ。それ以外は拒否**（**採用**） | ○ 明示の入れる・外すは効き、未指定は現状を保つ。初回（リリース無し）は従来どおりメッシュ無し |

値域を閉じるのは、未指定に意味ができた以上、`ISTIO=true` のような値を黙って「外す」と読むと #1713 の口が残るからである（`RESET_FLOOR` と同じく冒頭で拒否する）。

**引き継いだ `ISTIO=1` は明示と同じに扱う。** コントロールプレーンの `helm upgrade --install`（冪等）・注入ラベル・`rollout restart`・
`LOCALEDGE=1` なら Istio の入口（`istio-edge-up.sh`）まで通る。メッシュの宣言だけを足して Istio の段を飛ばす形は採らない ——
`ISTIO=1 ISTIO_MTLS_MODE=STRICT`（`LOCALEDGE` 無し）で立てたクラスタへ `LOCALEDGE=1` だけで再実行すると、Traefik の入口のまま STRICT が当たり入口が 502 になる（#1072）。
明示と同じ段を通せば IADR-0377 決定 2 の段取り（[6/7] は PERMISSIVE・入口を移した後に STRICT）がそのまま効く。

### 論点 2 の読む先

IADR-0487 論点 2 の採用案 a（`helm get values msp -n <ns> -o yaml`。`--all` ではない）をそのまま使う。`current_mesh_mtls_mode` の終了コードが既に
「メッシュ宣言あり（0）／リリースが無い・メッシュ未宣言（1）／読めない（2）」を分けているので、関数は足さない。**読みは 1 回で、`ISTIO` と
`ISTIO_MTLS_MODE` の両方の判定に使う**（未指定の `ISTIO` を引き継いだら、未指定の `ISTIO_MTLS_MODE` も同じ値から引き継ぐ）。

### 論点 3: 読めないとき

| 案 | 内容 | 評価 |
| --- | --- | --- |
| i | 外す側へ倒す（従前の既定） | ✗ #1713 そのもの |
| ii | 入れる側へ倒す | ✗ `ISTIO` 無しで立てたクラスタへ推測でメッシュを入れ、全 Pod を作り直す |
| **iii** | **[2/7] の前に非 0 で止め、`ISTIO=1`（＋`ISTIO_MTLS_MODE`）か `ISTIO=0` の明示を求める**（**採用**） | ○ 何も書き換えない唯一の側 |

`ISTIO_MTLS_MODE` を明示していても `ISTIO` 未指定なら止める。終了コード 2 は「helm に届かない」と「`mesh.enabled: true` なのに `mtlsMode` が壊れている」を
分けておらず、前者ではメッシュの有無そのものが分からないためである（`ISTIO=1` を足せば進む）。`ISTIO=0` は読まない（読めなくても進む）。

### 論点 4: #1691 の拒否

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **甲** | **拒否を据え置く（引き継ぎの読みより前で、`ISTIO` が 1 でなければ止める）**（**採用**） | ○ 止まる側であり、黙った変化は起きない。IADR-0317 の判断（付け忘れか Traefik へ戻す意図かを推測しない）を動かさない |
| 乙 | 読みを拒否の前へ移し、未指定 × メッシュ宣言ありなら通す | △ 引き継ぎとしては一貫するが、入口の選択（Istio のまま／Traefik へ戻す）の意図を `ISTIO` 未指定から読むことになり、IADR-0317 決定 7 の判断を変える。本件の射程（メッシュ宣言）を超える |

## 決定

1. `ISTIO` は `1`（入れる）・`0`（外す）・未指定（空。現行を引き継ぐ）の 3 値とし、それ以外は副作用より前に拒否する。
2. `ISTIO` 未指定、または `ISTIO=1` かつ `ISTIO_MTLS_MODE` 未指定のとき、[2/7] の前に `current_mesh_mtls_mode` を 1 回だけ読む。
   - `ISTIO` 未指定 × メッシュ宣言あり → `ISTIO=1` として以降を進め、`INFO: メッシュ（mesh.enabled: true）は現行を引き継ぎます（ISTIO 未指定。外すなら ISTIO=0）` を出す。
     `ISTIO_MTLS_MODE` も未指定なら現行のモードを引き継ぐ（IADR-0487 の INFO）。
   - `ISTIO` 未指定 × リリース無し・メッシュ未宣言 → 従来どおりメッシュ無し。
   - 読めない → 止める（論点 3 の iii）。`ISTIO=1` の側は IADR-0487 のまま。
3. `ISTIO=0` は読まず、従来の「`ISTIO` 無し」と同じ経路を通る（既定のバイト等価はこちらが持つ）。
4. #1691 の拒否は据え置く（論点 4 の甲）。エラー文の主語を「`LOCALEDGE=1` だけ（`ISTIO` 未指定・`ISTIO=0`）」に改める。
5. `RESET_FLOOR` の「効かない」の WARN は、引き継ぎの判定の後へ移す（値域の検査は冒頭に残す）。

## 理由

- 再実行は「現状を収束させる」操作であり、利用者が言っていない宣言の変更（メッシュを外す）を起こすべきではない。外すのは明示でだけ起こす（IADR-0487 と同じ）。
- 引き継いだ値を明示と同じに扱えば、`LOCALEDGE`・`ISTIO_MTLS_MODE` との組み合わせの段取りを別に持たずに済む（分岐を増やさない）。

## 結果

- 正: メッシュで動いているクラスタに `ISTIO` を付けずに再実行しても宣言は外れず、mTLS モードも保たれる。初回は従来どおり。
- 負: **既定経路（`ISTIO` 未指定）が `helm list` を 1 回読むようになる**（リリースが在れば `helm get values` も）。IADR-0307 決定 1 の「既定は完全に不変」は
  「`ISTIO=0` は完全に不変」へ移る。helm に届かない環境では未指定の再実行が止まる（`ISTIO=0` / `ISTIO=1` を明示すれば進む）。
- 負: 引き継いだ `ISTIO=1` は明示と同じく `rollout restart deployment` を走らせる（メッシュで動くクラスタを `ISTIO=1` で再実行したときと同じ）。
- 負: `ISTIO=0` で外しても、namespace の `istio-injection=enabled` ラベル（起動器が `kubectl label` で貼ったもの）と既存 Pod のサイドカーは残る。
  外れるのは helm の宣言（PeerAuthentication・AuthorizationPolicy 等）であり、従来の「`ISTIO` 無し」の再実行と同じ（本件で変えていない）。
- 残余: `check-stack-ready.js` の G12 は env の `ISTIO=1` で「メッシュ必須」になる。未指定で引き継いだ再実行の後に門を `ISTIO` 無しで走らせると G12 は
  宣言と稼働の一致だけを見る（緩い側）。宣言は外れていないので乖離は無く、門の意味論は変えない。
- 残余: CI（integration-stack）の比較実行は `ISTIO=''` を渡すが、毎回新しいクラスタ（リリース無し）なので未指定＝初回＝メッシュ無しで従来と同じ。
  `'0'` へ変えない（`t25-monthly-summary.js` が「床の無い比較実行」を `ISTIO` が空で見分けている）。
- 試験: `k8s-local-up.test.js` の #1713 節（3 値・引き継ぎ・fail-closed・`ISTIO=0` のバイト等価・RESET_FLOOR の WARN・#1691 の拒否の据え置き）。
  プローブの表（規則 11）と変異の結果は作業仕様書。

## 関連

- 作業仕様書: `.ai-context/specs/20261001_1713_mesh-enabled-inherit.md`
- [[IADR-0307]] 決定 1 と [[IADR-0487]] の残余へ、本決定を指す日付つき追記を置いた。

## ［2026-10-09 追記 / #1850］同じ形を部門属性の同期へ当てはめ、読みを共有した

> 上の本文は書き換えない。判断の本体は [[IADR-0473]] の 2026-10-09 追記（#1850）に置いた。ここには本決定の記述の射程が変わったことだけを足す（規則 10）。

- `k8s-local-up.sh` は同じ「明示 ＞ 現行 ＞ 初回の既定」と fail-closed で `DepartmentAttributeSync__Mode` を引き継ぐ（env `DEPT_SYNC_MODE`）。
- **リリースの値の読みは 1 回で、メッシュの判定と部門属性の同期の判定が共有する**（`scripts/lib/mesh-mtls-mode.sh` の `current_release_values`。`current_mesh_mtls_mode` は振る舞いを変えずそれを呼ぶ形にした）。
- そのため決定 3・論点 3 の「`ISTIO=0` は読まない（読めなくても進む）」は**メッシュの判定について**の記述になった。`ISTIO=0` でも `DEPT_SYNC_MODE` が未指定・`Report`・`Fix` なら
  部門属性の同期のために読み、読めなければ止まる。従来の既定と完全に同じ（helm を読まない・読めなくても進む）のは `ISTIO=0 DEPT_SYNC_MODE=Off`。
  `k8s-local-up.test.js` の #1710 / #1713 節の「明示なら読まない」「`ISTIO=0` は読まない」の試験は `DEPT_SYNC_MODE=Off` を併せた形へ改めた。
