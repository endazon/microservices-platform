---
title: check-deploy-manifests の hasTool が Windows で常に「全ツール欠落」を返す誤報を直す
type: spec
status: done
related_ids:
  - NFR
  - ADR-0007
  - IADR-0240
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs: []
---

# 作業仕様書: `hasTool` の platform 依存を直す

## 事象（実測で見つけた）

`scripts/check-deploy-manifests.js` は **PATH に在るツールまで「無い」と報告する。**

```console
$ node scripts/check-deploy-manifests.js
  - helm / kubectl / kubeconform が PATH にありません。…

$ for t in helm kubectl kubeconform; do command -v "$t" >/dev/null && echo "$t あり" || echo "$t 無し"; done
helm あり
kubectl あり
kubeconform 無し          ← 実際に欠けているのは 1 つだけ
```

### 原因

```js
function hasTool(bin) {
  const r = spawnSync('command', ['-v', bin], { shell: true, encoding: 'utf8' });
  return r.status === 0 && String(r.stdout || '').trim() !== '';
}
```

`shell: true` は Windows で `COMSPEC`（`cmd.exe`）を起こす。**`cmd.exe` に `command` 組み込みは無い。**
再現:

```console
$ node -e "…spawnSync('command',['-v','helm'],{shell:true})…"
helm: status=1 stdout="" err="'command' は、内部コマンドまたは外部コマンド…として認識されていません"
```

**3 つとも同じ理由で status=1 になるので、常に全欠落と報告される。**

### なぜ気づかれなかったか

- **CI（Linux）では再現しない。** `sh` に `command` 組み込みが在るので正しく動く
- 誤報は「検証を飛ばした」ではなく **「検証できない」**の形で出る。fail-closed の設計どおりに
  exit 1 で落ちるため、**壊れているのか環境が足りないのか区別がつかない**

## 母集合（規則 5: 軸を 1 本で終わらせない）

基点 `origin/develop`。`git rev-parse --is-shallow-repository` = `false`。

```console
$ git grep -n "'command', \['-v'\|command -v" -- scripts/
scripts/check-deploy-manifests.js:107      ← 🔴 JS。本件
scripts/ai-adapters/run-worker-*.sh（3 件）  ← bash。正しい
scripts/k8s-local-{up,down,images}.sh（6 件）← bash。正しい
```

**JS で `command -v` を使っているのは本 1 箇所だけである。** `.sh` は bash で走るので誤りではない。

**陽性対照 —— 本リポジトリには既に正しい作法が 2 つある:**

```console
$ git grep -n "'which'\|'where'" -- scripts/
scripts/check-password-reset-mail.js:84
scripts/check-stack-ready.js:237
  → どちらも process.platform === 'win32' ? 'where' : 'which'
```

**新しい流儀を持ち込む必要は無い。既にある作法へ揃えるだけである。**

## 設計

`hasTool` を `check-stack-ready.js:235-240` と**同一の実装**にする。差分は 4 行。

## 受け入れ基準

- [x] `helm` / `kubectl` が在る Windows 機で、欠落として報告されるのは **`kubeconform` だけ**である
- [x] 実行系そのもの（`node`）を「在る」と判定する（**在るものを無いと言わない**）
- [x] 実在しない名前は「無い」と判定する（**陰性対照**。何でも「在る」と言う実装で通らない）
- [x] `--self-test` が緑（6 → 8 件）
- [x] **変異試験**: 元の `command -v` 実装へ戻すと、`node` を検出する試験が実際に赤になる

## テスト方針

**両方向を対で置く。** 片側だけでは、`hasTool` が常に `true` を返す実装でも通ってしまう。

- 陽性: `hasTool('node')` —— **本試験を走らせている実行系そのもの**なので、環境に依らず必ず在る
- 陰性: `hasTool('msp-definitely-not-a-real-binary-xyz')`

🔴 **既存の「ツール不在は既定で失敗にする」試験ではこの欠陥を捕まえられない。**
同試験は `REQUIRED_TOOLS.every(hasTool)` が真なら早期 return する作りで、
**`hasTool` が常に false を返す壊れ方では、その分岐に入らず「不在の側」を試験して緑になる。**

## 実測した変異

```console
（hasTool を元の command -v 実装へ戻して --self-test）
AssertionError: node を検出できていない。hasTool が platform 依存で壊れている
```

## 計画書との差異

- 差異: なし（実装の可搬性の不具合であり、計画の裁定を要しない）

★［2026-09-06 追記］🔴 **起点 ID から `IADR-0130` を外した。**
当初はスコープへ載せていたが、同 ADR の主題は「テスト仕様書の節が丸ごと落ちることを
テストクラス単位の被覆 ratchet で検査する」ことであり、`hasTool` の PATH 検出とは無関係である。
本仕様書自身が「`IADR-0130` の適用でもない」と書いており、**適用しないと自分で書いた ID を
起点として載せるのは一貫していない**（AI レビューの指摘）。

🔴 **害は将来に出る** —— `IADR-0130` の実装箇所を棚卸ししたとき、本コミットが誤って算入される。
起点 ID は「実装が依拠した ID」であって「適用ではないと否定した引用先」ではない。

**コミット件名の `IADR-0130` は履歴に残る**（force push は禁止）。スカッシュ後の恒久件名は
PR タイトルなので、そちらから外した。

## 未決事項

- `kubeconform` 自体は本機に無いままである。**本 PR はそれを入れない** ——
  直したのは「何が無いかを正しく言うこと」であり、chart / overlay の検証は CI が行う。
