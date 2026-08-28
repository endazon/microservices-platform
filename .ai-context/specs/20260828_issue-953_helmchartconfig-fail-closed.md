---
title: 作業仕様書 — HelmChartConfig の変異試験を stub ハーネスへ実装する（#953 受け入れ基準 2 の充足）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0021
  - IADR-0087
  - IADR-0091
  - IADR-0213
  - IADR-0248
  - IADR-0255
  - IADR-0258
author: claude
created: 2026-08-28
updated: 2026-08-28
issue: "#953"
---

# 作業仕様書: `HelmChartConfig` の変異試験（#953・第 2 次）

## 起点

`#953`「`HelmChartConfig` の reconcile が失敗しても `scripts/k8s-local-up.sh` は EXIT=0 で返る」。

**本作業は #953 の第 2 次である。** 第 1 次（作業仕様書
`.ai-context/specs/20260823_issue-953_helmchartconfig-fail-closed.md` / [IADR-0258]）が既に develop へ
着地しており、受け入れ基準のうち 3 つは充足済みである。**残っていたのは 1 つだけで、それが本作業の対象**である。

## 🔴 着手前の実測 —— 何が済んでいて、何が残っているか

**記憶で「未実装」と言わない。** base（`b1da69e`）の実体を読んで確認した。

| # | #953 の受け入れ基準 | base での状態 | 本作業 |
| --- | --- | --- | --- |
| 1 | 反映を待ち、来なければ**非 0 で終了**する | ✅ 済（`scripts/k8s-local-up.sh` 405-441 行・`kubectl wait --for=jsonpath` ＋ `exit 1`） | 触らない |
| 2 | **変異試験**（壊すと落ちる／壊す前は落ちない）を**実測** | 🔴 **未充足**（下記） | **本作業の対象** |
| 3 | `HelmChartConfig` 利用箇所の走査・件数・対象の記録 | ✅ 済（[IADR-0258]「走査」節）。本書で**引き直して**再確認する | 再走査のみ |
| 4 | 宣言のバージョン依存が注記されている | ✅ 済（`deploy/local/edge/traefik-entrypoint.yaml` 冒頭の実測表） | 追補 1 行 |

### 基準 2 が未充足である根拠（実測）

第 1 次の作業仕様書は基準 2 を **「実クラスタが無いため未実測。手順を下に残す」** と自ら書いている。
[IADR-0258]「既知の限界」も **「実クラスタでの実走は未実施である」** と開示している。

`scripts/k8s-local-up.test.js` には #953 の試験が 2 本あるが、**変異させているのは stub の env フラグ**
（`STUB_TRAEFIK_ADMIN_MISSING=1`）**であって、宣言（`traefik-entrypoint.yaml`）ではない**。

```
$ grep -n "traefik-entrypoint" scripts/k8s-local-up.test.js
1369:const TRAEFIK_YAML = fs.readFileSync(path.join(EDGE_DIR, 'traefik-entrypoint.yaml'), 'utf8');
1378:    'traefik-entrypoint.yaml に --entryPoints.admin.http.tls=true が無い（admin:50000 が平文のまま）',
```

**ハーネスがこの宣言を読むのは TLS 引数の有無だけで、`expose` の型も `admin` のポート番号も見ていない。**
帰結として、base では次の変異がすべて**緑のまま素通りする**:

- `expose` を map 形から bool 形へ戻す（＝ chart 26 以降で `#953` が踏んだ型不一致そのもの）
- `admin` の `port` を 50000 以外へ書き換える（門は 50000 を待つので実クラスタでは無限に待つ）
- `ports.admin` ブロックごと削除する（門が空振りする）

つまり **門（`k8s-local-up.sh`）は在るが、門が守っている宣言は無検査**である。#953 が塞ごうとした
「宣言はバージョン依存で、壊れても誰も気付かない」の**後半**が、テスト側にそのまま残っていた。

## 母集合の走査（記憶で挙げない・誤りの側の文字列で引く）

規則 9（[`.claude/rules/traceability.repo.md`](../../.claude/rules/traceability.repo.md)）に従い、
**着手前に自分で引いた**。第 1 次の記載を転記していない（規則: 他人の数えを検証せず転記しない）。

### 走査 A: `kind: HelmChartConfig`（マニフェスト実体）

```
$ grep -rn "kind:[[:space:]]*HelmChartConfig" --exclude-dir=.git .
./deploy/local/edge/traefik-entrypoint.yaml:36:kind: HelmChartConfig
./scripts/k8s-local-up.sh:407:  # `deploy/local/edge` の先頭資源 traefik-entrypoint.yaml は `kind: HelmChartConfig` であり、その効果
./.ai-context/specs/20260823_issue-953_helmchartconfig-fail-closed.md:41:（`kind: HelmChartConfig`）であり…
./.ai-context/adr/IADR-0258_helmchartconfig-reconcile-fail-closed.md:24:`traefik-entrypoint.yaml` は `kind: HelmChartConfig` であり…
```

**マニフェスト実体は 1 件**（`deploy/local/edge/traefik-entrypoint.yaml`）。残りは散文である。

### 走査 B: 親 `kind: HelmChart` と API グループ（`kind:` だけで引くと取り落とすため引き直す。規則 10）

```
$ grep -rn "kind:[[:space:]]*HelmChart\b" --exclude-dir=.git .
./.ai-context/adr/IADR-0258_helmchartconfig-reconcile-fail-closed.md:122:`kind: HelmChart` は **0 件**、…

$ grep -rn "helm.cattle.io" --exclude-dir=.git . | grep -v "\.md:"
./deploy/local/edge/traefik-entrypoint.yaml:35:apiVersion: helm.cattle.io/v1
```

**`kind: HelmChart` の実体は 0 件。`helm.cattle.io` を含む非 md ファイルも同じ 1 件だけ**である。

### 走査 C: この宣言を apply する経路

```
$ grep -rn "deploy/local/edge" --include=*.sh scripts/
scripts/k8s-local-up.sh:403:  kubectl apply -k deploy/local/edge
scripts/k8s-local-up.sh:454:    kubectl apply -f deploy/local/edge/argocd-ingress.yaml
scripts/k8s-local-up.sh:472:    kubectl apply -k deploy/local/edge/tls && break
```

`HelmChartConfig` を含む overlay を apply するのは **`k8s-local-up.sh` の 1 経路だけ**である
（403 行。454/472 行は Ingress と TLS で `HelmChartConfig` を含まない）。
**同型の穴は他に無い**——同じ検証を横展開すべき先は存在しない。

## 設計

### 決定 1: **stub を helm-controller の模型にする**（宣言を試験の入力にする）

`scripts/k8s-local-up.test.js` の `kubectl` stub は、これまで全呼び出しを記録して `exit 0` を返すだけ
だった。**`kubectl wait --for=jsonpath=… svc/traefik …=50000` の 1 呼び出しだけ**を例外にする ——
この呼び出しは「`HelmChartConfig` が実際に効いたか」を問う唯一の呼び出しであり、そこを無条件 0 で
返すことは **helm-controller が必ず成功する世界を仮定する**ことに等しい。#953 が起きた世界はそうではない。

模型が読むのは 2 つだけである。

1. **宣言**: `deploy/local/edge/traefik-entrypoint.yaml`（env `STUB_TRAEFIK_MANIFEST` で差し替え可。
   既定は**リポジトリの実物**）。`ports.admin` の `port` と `expose` の型（map 形 / bool 形）を読む。
2. **chart の版**: env `STUB_TRAEFIK_CHART_MAJOR`（既定 26）。

判定は #953 で実測された事実そのものである（`traefik-entrypoint.yaml` 冒頭の表）。

| chart | 受け付ける `expose` | 由来 |
| --- | --- | --- |
| 25 以下 | **bool**（`expose: true`） | k3s v1.30.4 同梱 chart 25.0.3 の実測（#953） |
| 26 以上 | **map**（`expose: {default: true}`） | 現行宣言が通っている版 |

加えて `admin` の `port` が 50000 でなければ、版に関係なく反映は成立しない（門が待つ値と食い違う）。

模型は awk 1 本（ハーネス内の定数）に閉じる。**bash stub の作法は変えない** —— 記録スタブ・
PATH 差し替え・`STUB_LOG` は [IADR-0087] のまま、追加は「1 呼び出しの戻り値を宣言から決める」だけである。

### 決定 2: 対照は**リポジトリの実物**を読ませ、変異は**一時ファイル**で行う

「壊す前は落ちない」側は実物の `traefik-entrypoint.yaml` を読む。こうすると、**誰かが実物を bool 形へ
戻した瞬間に対照が落ちる** —— 門と宣言が初めて結ばれる。変異側はリポジトリを書き換えず一時ファイルへ
書く（テストが作業ツリーを汚さない）。

### 決定 3: **[IADR-0258] 決定 3 との緊張を隠さない**

[IADR-0258] 決定 3 は「**バージョン依存を塞ぐ門を、バージョン依存の識別子で書いてはならない**」と
述べ、`k8s-local-up.sh` の門から chart 版・job 名を排した。本作業は**テスト側に**版依存の模型を持ち込む。
矛盾ではない。分けている:

- **門**（本番経路）は版に依存しない。見るのは Service の port という Kubernetes コア API だけである。
- **模型**（試験だけ）は版に依存する。**版依存の事故を再現するには版を持つほかない。**

代償は **模型が実物からずれ得る**ことである（chart 27 が別の型を要求したら模型は古くなる）。
それは `traefik-entrypoint.yaml` 冒頭の実測表が古くなることと**同じ 1 つの事実**であり、
更新点は増えない —— 表を直すときに模型も直す。**この対応を宣言側へ 1 行書いて結ぶ**（変更 3）。

## 受け入れ基準

- [x] `traefik-entrypoint.yaml` を chart が受け付けない書式へ壊すと `k8s-local-up.sh` が**非 0** で終わる
- [x] 壊す前（リポジトリの実物）は**落ちない**（EXIT=0 で完走する）
- [x] 上の 2 方向を `scripts/k8s-local-up.test.js` の**既存の作法**（bash stub-on-PATH）で実測する
- [x] #953 が実際に踏んだ組（現行宣言 ＋ chart 25）を**再現**し、非 0 になることを示す
- [x] 「どんな変異でも落ちる」模型ではないこと（bool 形 ＋ chart 25 は**通る**）を対で示す
- [x] `admin` の port ずれ・`ports.admin` の消失も捕まえる
- [x] 既存の `scripts.test.js` が全件通り、件数が増えている

## テスト方針

`scripts/k8s-local-up.test.js` へ、既存の `runUp()` を使う試験を 5 本足す。
**変異と対照は必ず対で置く** ——「常に落ちる実装」も「常に通る実装」も緑にしないため（既存の #953
試験群と同じ方針）。

| 方向 | 宣言 | chart | 期待 |
| --- | --- | --- | --- |
| 対照 | **実物**（map） | 26（既定） | EXIT=0・後続段（cert-manager）へ進む |
| 変異 | bool へ壊す | 26（既定） | **非 0**・後続段へ進まない |
| 再現 | **実物**（map） | 25 | **非 0**（#953 が踏んだ組そのもの） |
| 陰性対照 | bool へ壊す | 25 | EXIT=0（版が合えば bool は正しい ＝ 模型は「変異なら落ちる」ではない） |
| 変異 | port を 50001 へ／`admin` 削除 | 26 | **非 0** |

## 変更するファイル

1. `scripts/k8s-local-up.test.js` — `kubectl` stub に helm-controller の模型を足し、上の 5 本を追加
2. `.ai-context/adr/IADR-0258_*.md` — 「実クラスタでの実走は未実施」の限界へ日付つき追記（何が塞がり、
   何がまだ塞がっていないかを更新する）
3. `deploy/local/edge/traefik-entrypoint.yaml` — 実測表が**機械検査に結ばれた**ことを 1 行注記する
   （表を直すときに模型も直す、という対応関係を宣言側に残す）

`scripts/k8s-local-up.sh` は**触らない**。門は第 1 次で正しく入っており、本作業の欠陥はテスト側にある。

## 計画書との差異

- 差異: なし

## 未決事項・申し送り

1. **実クラスタでの実走は依然として未実施**である。本作業が足したのは**模型に対する**実測であり、
   「実物の k3s で本当に落ちるか」は変わらず未検証のままである（[IADR-0258] の限界は消えていない）。
   模型の判定表は #953 の実測（GitHub ホストランナー run `32554867883`）に基づく。
2. **既存クラスタへの再実行では新しく壊した宣言を捕まえられない**という [IADR-0258] の限界も残る。
   模型はクラスタ作成直後だけを写している。job レベルの検査は同 決定 3 が退けたままである。
3. **実装 ADR を新規に起こしていない。** 本作業の判断（決定 1〜3）は [IADR-0258] の決定 1・2 の射程内で、
   同 ADR が自ら開示した限界を埋めるものであるため、**同 ADR への日付つき追記**として記録した。
   予約されていた `IADR-0289` は**使っていない** —— 直前の採番は `IADR-0287` であり、`IADR-0288` を
   飛ばして起こすと `scripts/check-adr-numbering.js` の**欠番判定**で `scripts.test.js` が中断し、
   受け入れ条件である「全件緑・件数の提示」が満たせなくなる（並行作業が `IADR-0288` を保持しているため）。
   新規 ADR が要ると判断されるなら、`IADR-0288` の着地後に改めて起こすこと。
