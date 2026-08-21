---
title: 作業仕様書 — `OPTIN_TOKENS` の既定経路検査を `includes` から境界判定へ変え、各トークンの検出力を変異試験で実測する（#817）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0087
  - IADR-0141
  - IADR-0179
  - IADR-0183
  - IADR-0206
  - IADR-0208
  - IADR-0210
  - IADR-0213
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
  - planning:docs/ai-implementation-workflow-guide.md
related_specs:
  - "../adr/IADR-0213_optin-token-boundary-matching.md"
  - "../adr/IADR-0087_k8s-local-up-optin-smoke-test.md"
  - "../adr/IADR-0210_local-k8s-observability-persistence.md"
  - "../adr/IADR-0208_companion-direct-run-guard.md"
  - "../../docs/how-to/session-handoff.md"
---

# 作業仕様書: `OPTIN_TOKENS` の判定を境界判定へ統一し、全トークンの検出力を変異試験で実測する（#817）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）/ ユースケース（UC）/ 画面（SC）: なし。**製品の機能を変えない。**
- 非機能要件: **`NFR`（無採番）** —— 検査器（smoke test）の判定意味論に関するメタ作業であり、
  計画側の非機能要件表（`NFR-01`〜`NFR-27`）に当たる番号が無い（`.claude/rules/traceability.repo.md`
  「起点 ID の種別（固有）」。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）。
  **無いことは「実装側で採番してよい」ではない**（同 決定 2）。**環流しない。**
- 関連 ADR: [IADR-0087](../adr/IADR-0087_k8s-local-up-optin-smoke-test.md)（本 smoke test の方式）、
  [IADR-0210](../adr/IADR-0210_local-k8s-observability-persistence.md)（穴が露見した変更）、
  [IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md)（同族: 外れても検出できない機構）。
- 起点 issue: #817（発見は PR #816 の変異試験 M2 / #787）。

## 目的・背景

`scripts/k8s-local-up.test.js` の既定経路検査は、`OPTIN_TOKENS` の各トークンが発行コマンド列に
**現れないこと**を `String.prototype.includes` で見る。トークン同士に**接頭辞関係**があると、
短い側が長い側の混入も拾うため、長い側のトークンは**足しても検出力が増えない**。
PR #816 の変異試験 M2 が `deploy/local/observability` / `deploy/local/observability-persistence` で
これを実測した（issue #817 に逐語）。

いま実害は無い。問題は「**トークンを足した＝守られるようになった**」と読める形なのに、
実際には既存トークンが偶然カバーしているだけ、という状態が積み上がることである。

## 対象範囲

- 対象: `scripts/k8s-local-up.test.js` の `OPTIN_TOKENS` とその判定、および判定の自己検査。
- 対象外:
  - `scripts/k8s-local-up.sh`（**無改変**。IADR-0087 の「スクリプトは触らない」方式を維持する）。
  - `OPTIN_TOKENS` 以外の `includes` 判定（`anyLineHas` の他の用途・`APISERVER_OIDC_TOKENS`）。
    接頭辞関係が無く、本 issue の症状が出ない（下の母集合参照）。
  - **トークンを減らす整理**（issue「やらないこと」）。列挙としての明示に価値がある。
  - **検査を弱める変更**（issue「やらないこと」）。いまの `includes` は「守りすぎ」であって
    「守れていない」のではない。

## 母集合の引き直し（`.claude/rules/traceability.repo.md` 規則 9・10）

**誤りの側から引いた。** 誤りは「**接頭辞関係にあるトークンの組**」である。記憶で挙げず、
`OPTIN_TOKENS` の全 22 要素について総当たりで `b.startsWith(a)` を機械的に評価した
（`probe.tmp.js`。作業用スクリプトのためコミットしない）。

結果（**全数・3 組**）:

| 短い側 | 長い側 | 現行 `includes` での長い側の単独検出行 |
| --- | --- | --- |
| `deploy/local/observability` | `deploy/local/observability-persistence` | **0**（＝冗長。#816 の実測と一致） |
| `deploy/local/vault` | `deploy/local/vault/eso` | **0**（＝冗長。**未知だった**） |
| `deploy/local/edge` | `deploy/local/edge/tls` | **0**（＝冗長。issue 本文が挙げた組） |

**issue 本文は 2 組（observability / edge）を挙げていたが、実際は 3 組あった**
（`deploy/local/vault` ⊂ `deploy/local/vault/eso`）。issue 本文の列挙を母集合として転記しなかったこと
（規則: 他人の数えを検証せず転記しない）で捕まえた。

除外したものと理由:

- `APISERVER_OIDC_TOKENS`（同ファイル・7 要素）: 総当たりで接頭辞関係**ゼロ**。症状が出ない。
  かつ「どの経路でも一切現れない」ことだけを見る負の列挙で、個々の検出力は `kube-apiserver-arg` と同型。
- 他リポ/他検査器の `includes`: **本 issue の症状は「同一列挙の中に接頭辞関係がある」ことに依存する**。
  `scripts/` 配下で「トークン列挙 × 不在検査」の形を取るのは本ファイルの 2 か所のみ
  （`OPTIN_TOKENS` / `APISERVER_OIDC_TOKENS`）。走査で確認した。
- `deploy/local/observability` の `apply -k` 判定（`appliesBareObservability`）: **既に境界判定**
  （#816 で入れた `(\s|$)`）。共通判定へ寄せる（重複実装を 1 つにする）だけで意味は変えない。

## 設計

### 決定 1: 判定を「末尾境界」で見る

`OPTIN_TOKENS` の照合を、部分文字列一致から**末尾境界つき一致**へ変える。
トークンの直後の 1 文字が**識別子継続文字 `[A-Za-z0-9_./-]` でない**（または行末である）ときだけ一致とする。
先頭側は見ない（接頭辞問題は末尾側にしか無く、先頭を縛ると `secret/msp/grafana-oidc` の類を落とす）。

これで `deploy/local/observability` は `...-persistence` に一致せず、
`deploy/local/edge` は `deploy/local/edge/tls` に一致しない ——
**接頭辞関係にある 3 組が、それぞれ独立に検出力を持つ。**

### 決定 2: ディレクトリ配下を見るトークンは末尾 `/` で綴る

末尾境界だけにすると、**そのパス自体が単独では発行されない**トークンが検出力ゼロ（dead）になる。
実測で 2 件該当した:

- `deploy/argocd` —— 発行されるのは `deploy/argocd/appproject.yaml` / `deploy/argocd/application.yaml` のみ
- `deploy/local/vault/eso` —— 発行されるのは `deploy/local/vault/eso/*.yaml` のみ（13 行）

そこで**末尾 `/` を「このディレクトリと配下すべて」の意**とし、この 2 件を
`deploy/argocd/` / `deploy/local/vault/eso/` と綴る。カバー範囲は現行 `includes` と同じで、
**弱めない**（`deploy/argocd-foo` のような別物に当たらなくなるだけ）。

### 決定 3: 「各トークンが単独で検出力を持つ」ことを検査へ組み込む

一度きりの変異試験は次のトークン追加で腐る。**冗長なトークンが在ること自体は許すが、
黙って在ることは許さない**——「どのトークンが効いているか」を機械が持つ形にする。

opt-in を全部立てた実行（2 通り。`PERSIST`/`ESO` は他ゲートの出力を*置換*するため片方を落とした run も要る）で
採取した実コマンド列を母集合とし、各トークンについて

1. **1 行以上に一致する**（dead token でない）
2. **他のどのトークンも一致しない行が 1 行以上ある**（= そのトークンだけが検出できる混入が実在する）

を検査する。落ちたら「そのトークンは冗長」と名指しで報告する。
`kube-apiserver-arg` だけは**どのゲートも発行しない負のトークン**（IADR-0105 の除去の回帰固定）なので、
合成した混入行を明示テーブルで与えて同じ 2 条件を課す。**例外は 1 件・理由つきでコード上に在る。**

## 受け入れ基準（issue #817 より転記）

- [x] `OPTIN_TOKENS` の各トークンが、**それ単独で対応する混入を検出する**ことを**変異試験で実測**する
- [x] 冗長なトークンが残る場合、その事実が**コード上で読み取れる**
      （→ 決定 3 で「冗長なら検査が落ちる」形にした。残った例外 `kube-apiserver-arg` は
      明示テーブル＋理由コメントで読み取れる）
- [x] 既定経路のバイト等価検査が引き続き緑

## テスト方針

### 変異試験（`docs/how-to/session-handoff.md` §5 型 4）

**全 22 トークンについて 1 つずつ**（サンプリングしない）:

1. `scripts/k8s-local-up.sh` の**既定経路**（`[7/7]` の直後・どの opt-in ブロックの内側でもない位置）へ、
   そのトークンに対応する**実際の発行コマンド行**を 1 行挿入する（スタブ経由で記録される）。
2. **変異が本当に「壊れた状態」になっているかを先に確かめる** —— 既定実行のログに当該文字列が
   現れることを確認する。現れなければ INVALID として扱い、テストの結果を信用しない
   （★ 過去 2 回、変異が退行を模しておらず GREEN が返った）。
3. 全トークンありでテストを走らせる → **RED**（検出できている）であること。
4. そのトークンだけ列挙から外して走らせる → **GREEN**（穴が開く）なら、そのトークンが単独の検出者。
   **RED のままならそのトークンは冗長。**
5. スクリプトとテストを復元する。

対照として「変異なし・全トークンあり → GREEN」も取る。

### 変異試験の実測結果（2026-08-16 / 全 22 トークン）

対照（変異なし・全トークンあり）: **EXIT=0 GREEN**。
**22 件すべてで「変異が退行を模していること」を先に確認した（`leak確認: OK` 22/22・INVALID 0 件）。**

判定は**検査対象＝既定経路の `OPTIN_TOKENS` 検査**で行う（suite 全体の RED/GREEN で見ると、
別のテストが同じ混入を捕まえた場合に「冗長」と誤判定する。実際に 7 件がそう出た）。

| # | トークン | 全トークンあり | 当該のみ除去 | 判定 |
| --- | --- | --- | --- | --- |
| 1 | `deploy/local/infra-persistence` | RED | GREEN | LOAD-BEARING |
| 2 | `deploy/local/observability` | RED | 既定経路検査は捕捉せず（別テストが捕捉） | LOAD-BEARING |
| 3 | `deploy/local/observability-persistence` | RED | 同上 | LOAD-BEARING |
| 4 | `grafana-oidc` | RED | GREEN | LOAD-BEARING |
| 5 | `deploy/local/vault` | RED | 同上（別テストが捕捉） | LOAD-BEARING |
| 6 | `vault-dev-token` | RED | GREEN | LOAD-BEARING |
| 7 | `vault-oidc` | RED | GREEN | LOAD-BEARING |
| 8 | `deploy/local/headlamp` | RED | GREEN | LOAD-BEARING |
| 9 | `headlamp-oidc` | RED | GREEN | LOAD-BEARING |
| 10 | `deploy/argocd/` | RED | GREEN | LOAD-BEARING |
| 11 | `namespace argocd` | RED | GREEN | LOAD-BEARING |
| 12 | `argocd-cm-patch.yaml` | RED | GREEN | LOAD-BEARING |
| 13 | `oidc.keycloak.clientSecret` | RED | GREEN | LOAD-BEARING |
| 14 | `kube-apiserver-arg`（合成） | RED | 同上（別テストが捕捉） | LOAD-BEARING |
| 15 | `deploy/local/edge` | RED | GREEN | LOAD-BEARING |
| 16 | `50000` | RED | 同上（別テストが捕捉） | LOAD-BEARING |
| 17 | `external-secrets` | RED | GREEN | LOAD-BEARING |
| 18 | `deploy/local/vault/eso/` | RED | GREEN | LOAD-BEARING |
| 19 | `seed-abac-policies.js` | RED | GREEN | LOAD-BEARING |
| 20 | `cert-manager` | RED | GREEN | LOAD-BEARING |
| 21 | `deploy/local/edge/tls` | RED | 同上（別テストが捕捉） | LOAD-BEARING |
| 22 | `certificate/edge-tls` | RED | 同上（別テストが捕捉） | LOAD-BEARING |

**冗長なトークンは 0 件。** 接頭辞関係にあった 3 組（#2/#3・#5/#18・#15/#21）は、
`includes` のままなら長い側が REDUNDANT に出る（下の「変更前の実測」）。

「別テストが捕捉」は**多層防御**であって冗長ではない。当該トークンを外すと
**既定経路検査は当該混入を捕らえられなくなり**、たまたま別の観点のテスト
（置換の意味論・順序・reuse 経路・apiserver 引数）が引っかかっただけである。内訳:

| トークン | 捕まえた別テストの主張 |
| --- | --- |
| `deploy/local/observability` | OBSERVABILITY 無効なのに素の observability が apply された |
| `deploy/local/observability-persistence` | OBSERVABILITY 無効なのに可観測性の永続化 overlay が現れた |
| `deploy/local/vault` | CRD 無なのに kustomize 経路が通った |
| `kube-apiserver-arg` | HEADLAMP=1: apiserver OIDC の痕跡が現れた |
| `50000` | reuse なのに cluster create が呼ばれた |
| `deploy/local/edge/tls` | tls overlay の apply が CRD 待ちより前にある |
| `certificate/edge-tls` | 証明書 Ready 待ちが apply より前にある |

### 変更前（`includes`）の実測

同じ母集合で `includes` 判定を評価すると、**接頭辞を持つ 3 トークンが単独検出行 0 行**になる
（`deploy/local/observability-persistence` / `deploy/local/vault/eso` / `deploy/local/edge/tls`）。
これが #816 の M2 が観測した状態であり、本変更後は 0 件になる。

### 回帰

`node scripts/k8s-local-up.test.js` 全件（既定バイト等価・各ゲートの意味論を含む）。

## 計画書との差異

- 差異: なし。

## 未決事項

- なし。
