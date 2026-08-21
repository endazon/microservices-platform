---
title: IADR-0213 `OPTIN_TOKENS` の不在検査は部分文字列一致ではなく末尾境界一致で行い、各トークンの単独検出力を検査で固定する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0087
  - IADR-0105
  - IADR-0141
  - IADR-0179
  - IADR-0183
  - IADR-0206
  - IADR-0208
  - IADR-0210
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
  - planning:docs/ai-implementation-workflow-guide.md
---

# IADR-0213: `OPTIN_TOKENS` を末尾境界で照合し、「足したのに守っていないトークン」を検査で落とす

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: 実装担当（AI）／起票 #817

## 起点・関連

- 関連する計画書 ID: **`NFR`（無採番）** —— 検査器の判定意味論というメタ作業であり、計画側の
  非機能要件表に当たる番号が無い（`.claude/rules/traceability.md`「起点 ID の種別」の 2 の場合。
  [IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）。**環流しない。**
- 作業仕様書: [`docs/specs/20260816_issue-817_optin-token-boundary-matching.md`](../specs/20260816_issue-817_optin-token-boundary-matching.md)
- 関連 IADR: [IADR-0087](./IADR-0087_k8s-local-up-optin-smoke-test.md)（本 smoke test の方式）／
  [IADR-0210](./IADR-0210_local-k8s-observability-persistence.md)（穴が露見した変更）／
  [IADR-0208](./IADR-0208_companion-direct-run-guard.md)（同族: **外れても検出できない機構**）／
  [IADR-0183](./IADR-0183_false-green-warning-on-worktree-state.md)（**偽の緑**を黙らせない）／
  [IADR-0105](./IADR-0105_remove-apiserver-oidc-flag-wiring.md)（負のトークン `kube-apiserver-arg` の出所）

## コンテキストと課題

`scripts/k8s-local-up.test.js` の既定経路検査は、`OPTIN_TOKENS` の各トークンが発行コマンド列に
**現れないこと**を `String.prototype.includes` で見ていた。

```js
for (const tok of OPTIN_TOKENS) assert.ok(!lines.some((l) => l.includes(tok)), ...);
```

トークン同士に**接頭辞関係**があると、短い側が長い側の混入まで拾う。
PR #816（#787）は新 overlay に合わせて `deploy/local/observability-persistence` を足したが、
**既存の `deploy/local/observability` がその接頭辞**であるため、新トークンは
**足しても検出力が増えていなかった**。同 PR の変異試験 M2 が実測でそう出た（issue #817 に逐語）。

実害は無い（`includes` は「守りすぎ」であって「守れていない」のではない）。
問題は **「トークンを足した＝守られるようになった」と読める形なのに、実際は既存トークンが偶然
カバーしているだけ**、という状態が黙って積み上がることである。次に足すトークンが接頭辞を持たない
（＝本当に守っていない）場合と、**書いた側からも読んだ側からも区別がつかない。**

母集合を「誤りの側」から引き直した（接頭辞関係にあるトークンの組を全数評価）。
**issue 本文は 2 組を挙げていたが、実際は 3 組あった。**

| 短い側 | 長い側 | `includes` での長い側の単独検出行 |
| --- | --- | --- |
| `deploy/local/observability` | `deploy/local/observability-persistence` | 0（#816 の実測と一致） |
| `deploy/local/vault` | `deploy/local/vault/eso` | 0（**未知だった**） |
| `deploy/local/edge` | `deploy/local/edge/tls` | 0（issue が挙げた組） |

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | 照合を**末尾境界つき**にする（トークンの直後が識別子継続文字なら不一致） | 接頭辞関係の 3 組がそれぞれ独立に検出力を持つ。issue の提案どおり |
| **A'（併せて採用）** | **各トークンが単独で検出力を持つことを毎回検査する** | 一度きりの変異試験は次の追加で腐る。**冗長なトークンが在ること自体を機械が言う** |
| B | 冗長なトークンにコメントで「これ単独では穴が開かない」と書く（#816 の現状） | 正直だが、**次のトークンでも同じ判断を人がやり直す**。#816 は正しく書いたが、その正しさは偶然に依存する |
| C | 接頭辞になるトークンを列挙から外す | **採らない**（issue「やらないこと」）。列挙としての明示に価値がある |
| D | 完全一致（行を空白で分割してトークン単位で比較） | 引数の途中に現れる形（`--patch-file .../argocd-cm-patch.yaml`・JSON 中の `oidc.keycloak.clientSecret`）を落とす。**検査が弱くなる** |

## 決定

1. **照合は末尾境界つきで行う。** トークン直後の 1 文字が識別子継続文字 `[A-Za-z0-9_./-]`
   **でない**（または行末）のときだけ一致とする。実装は `matchesToken(line, token)`。
2. **先頭側の境界は見ない。** 接頭辞問題は末尾側にしか無く、先頭を縛ると
   `secret/msp/grafana-oidc` や `externalsecret/headlamp-oidc` を落として**検査が弱くなる**。
3. **ディレクトリ配下しか発行されないトークンは末尾 `/` で綴る**（「そのディレクトリと配下すべて」の意）。
   決定 1 だけだと、**そのパス自体が単独では発行されない**トークンが検出力ゼロになる。実測で 2 件:
   `deploy/argocd`（発行は `deploy/argocd/appproject.yaml` 等のみ）と
   `deploy/local/vault/eso`（発行は `deploy/local/vault/eso/*.yaml` の 13 行のみ）。
   これらを `deploy/argocd/` / `deploy/local/vault/eso/` と綴る。**カバー範囲は `includes` と同じで、弱めない。**
4. **「各トークンが単独で検出力を持つ」ことをテストとして常設する。**
   opt-in を立てた 2 通りの run（`PERSIST` / `ESO` は他ゲートの出力を*置換*するため片方を落とした run が要る）の
   実発行行を母集合に取り、各トークンについて (1) 1 行以上に一致する（dead でない）
   (2) **他のどのトークンも一致しない行が 1 行以上ある**（単独の検出力がある）を検査する。
   落ちたトークンは名指しで報告する。
5. **どのゲートも発行しない「負のトークン」だけは合成した混入行で測る。**
   現在 1 件（`kube-apiserver-arg`。IADR-0105 の除去が戻っていないことの回帰固定）。
   例外は明示テーブル `SYNTHETIC_CONTAMINATION` に理由つきで置き、**同じ 2 条件を課す**。
6. **トークンは減らさない。** 冗長と判明したものも列挙から外さない（issue「やらないこと」）。
   決定 1・3 の結果、**現在は冗長なトークンが 0 件になった**ため、決定 4 の検査は例外なしで通る。
7. **`k8s-local-up.sh` は無改変。** IADR-0087 の「スクリプトを触らずに固定する」方式を維持する
   （変異試験のあいだだけスクリプトへ混入を差し込み、終了後に復元した）。
8. **重複していた境界判定を 1 つにする。** #816 が `appliesBareObservability` に入れた
   `/apply -k deploy\/local\/observability(\s|$)/` は同じ規則の 2 つ目の実装だった。`matchesToken` へ寄せる。

## 理由

- **「守りすぎ」は無害ではない。** 過剰な一致は、**検出力の所在を隠す**。どのトークンが効いているかが
  読めないまま列挙が伸びると、効かないトークンが混ざった瞬間に誰も気づけない。
  これは IADR-0208 が扱った「**外れても検出できない機構**」と同族である。
- **一度きりの変異試験では足りない。** #816 は変異試験を正しくやり、正しくコメントへ書いた。
  それでも**次の追加者は同じ判断をやり直さなければならない**。判断を機械へ移す（決定 4）。
- **境界の定義は「パス区切りも境界を作らない」側に倒した。** `/` を境界に含めると
  `deploy/local/edge` が `deploy/local/edge/tls` に一致し、独立性が失われる。
  配下まで見たいトークンは決定 3 の綴りで**明示的に**そう宣言する。

## 変異試験（実測）

**全 22 トークンについて 1 つずつ実施した（サンプリングしていない）。** 手順は
「既定経路へ実発行行を 1 行挿入 → **変異が退行を模しているかを先に確認** → 全トークンありで RED →
当該トークンだけ外して既定経路検査が捕らえなくなれば単独の検出者」。
トークンごとの表は[作業仕様書](../specs/20260816_issue-817_optin-token-boundary-matching.md)「変異試験の実測結果」に置く。

- 対照（変異なし・全トークンあり）: **EXIT=0 GREEN**
- **`leak確認: OK` 22/22 ・ INVALID 0 件** —— 変異が退行を模していることを毎回先に確かめた
  （★ 過去 2 回、模していない変異に GREEN を返されている）
- **22/22 が LOAD-BEARING。冗長は 0 件。**
- うち 7 件は**当該トークンを外しても別の観点のテストが引っかかる**（多層防御）。
  suite の exit code で判定していたらこの 7 件を「冗長」と読み違えていた ——
  **判定は検査対象（既定経路の `OPTIN_TOKENS` 検査）のメッセージで行う。**

## 結果

- 良い影響:
  - 接頭辞関係にあるトークンが**それぞれ独立に**検出力を持つ。次に足すトークンが効くかどうかを
    人が判断しなくてよい（効かなければテストが落ちる）。
  - 境界判定の実装が 1 つになった。
- 悪い影響・トレードオフ:
  - opt-in を立てた run が 2 本増える（実測 60.5s → 65.9s。テスト件数 72 → 74）。
  - **ゲートが新設されてトークンを足すとき、綴りを実発行に合わせる必要が出る**
    （末尾 `/` の有無）。合っていなければ dead token として名指しで落ちるので、黙って外れることはない。
- フォローアップ: なし。他リポジトリへの環流も不要（`k8s-local-up.test.js` は本リポ固有・キット配布物ではない）。

## 関連

- Supersedes: なし
- Superseded by: なし
