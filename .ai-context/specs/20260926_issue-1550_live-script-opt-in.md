---
title: 作業仕様書 — #1550 稼働クラスタへ当たる scripts を、明示の指定（--live か LIVE=1）が無ければ何もせずに終わらせる
type: spec
status: done
related_ids: [NFR, IADR-0087, IADR-0248]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
issue: "#1550"
---

# 作業仕様書 — #1550 稼働クラスタへ当たる scripts の明示の指定

## 起点

- issue #1550（2026-09-26 の事故）。作業エージェントがワークフローの `node scripts/...` の行をまとめて実行し、
  `check-password-reset-mail.js` が稼働中の Keycloak へ本物のパスワード再設定を申請した（mailpit が受けた）。
  `check-login-existence-disclosure.js` も Keycloak へ当たり、`seed-abac-policies.js` も起動した。
- 起点 ID は **無採番の `NFR`**（メタ作業。運用の安全装置であり、計画側の NFR 表に当たる番号が無い。IADR-0179 決定 1）。

## 対象範囲

- 対象: `scripts/` 配下で稼働クラスタへ当たる入口。ワークフロー（`.github/workflows/*.yml`）と、稼働スタックに対して
  それらを呼ぶ live な文書・runbook の呼び出し行。新しいスクリプトにも同じ規則を課す機械検査。
- 対象外: 下の「除外」に理由つきで列挙する。

## 設計

1. **判定器を 1 か所に置く。** `scripts/lib/live-opt-in.js`（Node）と `scripts/lib/live-opt-in.sh`（bash）。
   同じ規則・同じ文言・同じ終了コード **3**（既存の「未知の引数 = 2」「失敗 = 1」と区別する）。
   - 指定は `--live`（引数）か `LIVE=1`（環境変数。`1` だけ）。無ければ stderr に理由と指定の仕方を書いて exit 3。
   - 🔴 **判定は副作用より前に置く。** 拒否の経路は子プロセスもネットワークも開かない。
   - 稼働クラスタに触れないモード（`--self-test` / `--dry-run` / `--input` / `--print-*`）は指定なしで今のまま動く。
   - bash 側は指定があれば `LIVE=1` を export する —— `k8s-local-up.sh` の中から呼ぶ子（`k8s-local-images.sh` /
     `istio-edge-up.sh` / `seed-*.js`）は親の指定を引き継ぐ。
2. **対象の一覧の単一情報源は `scripts/live-scripts.json`**（`live` / `ownFlag` / `offline` と、標識の正規表現・除外パターン）。
   人向けの一覧は `scripts/README.md`「稼働クラスタへ当たる scripts」節（一覧との一致はテストが見る）。
3. **機械検査**（`scripts/scripts.repo.test.js` の #1550 節。必須 check `scripts-tests` で走る）:
   - 閉包: `scripts/` を標識で走査した集合 ＝ `live ∪ ownFlag ∪ offline`（新しいスクリプトが標識を持てば分類するまで赤）。
   - `live` の各入口が判定器を呼んでいる（静的）。
   - `live` の各入口を**指定なしで**起こすと exit 3 と所定の文言で終わり、**どのツールも起動しない**（拒否の経路だけを試す）。
     安全網: Node は `-r` の前置きで `child_process` / `fetch` / `net` / `http(s)` を塞ぎ、bash は `BASH_ENV` の関数と
     PATH のスタブで `kubectl` / `helm` / `curl` / `node` などを塞ぐ（呼ばれたら印を残す）。`KUBECONFIG` は実在しないパスへ向ける。
   - ワークフローが `live` の入口を呼ぶ行は、オフラインのモードでない限り `--live` を持つ。
   - README の節が `live` の全件を載せている。
4. **呼び出し側の追随**: ワークフロー（`integration-stack.yml` の 7 行）と、稼働スタックに対して呼ぶ live 文書の行へ
   `--live` を足す（スクリプトパスの直後）。オフラインのモードの行は変えない。

## 母集合（規則 1〜10。誤りの側＝「稼働クラスタへ当たる」側の語で引く）

### 軸 1: `scripts/` の全ファイル（拡張子で絞らない。122 件）を当たりの語で走査

語: `kubectl` / `port-forward` / `localhost` / `127\.0\.0\.1` / `KC_URL` / `/admin/realms` / `\bhelm\b` / `nerdctl` / `\bk3d\b` /
`KUBECONFIG` / `mailpit` / `fetch\(` / `curl` / `docker` / `execFileSync|spawnSync|execSync|spawn\(` /
`http://[a-z0-9.-]+:[0-9]+` / `svc\.cluster\.local` / `BASE_URL|_URL\b`。

### 軸 2: 別の経路で当たる語

`rdctl` / `\bvault\b` / `psql` / `kcadm` / `rabbitmqctl` / `grpcurl` / `wget` / `require('http|https|net|tls|dgram')` /
`WebSocket` / `\bnc\b` / `\bmc\b` / `\.svc\b` / `:[0-9]{4,5}\b`。軸 2 で新しく当たったのは `check-contract-schema.js` /
`check-cross-repo-refs.js`（`\bnc\b` が本文の語に当たっただけ。静的）で、入口は増えなかった。

### 軸 1＋2 の和集合（51 件 / 122 件）と判定

| 判定 | 意味 | ファイル |
| --- | --- | --- |
| **L（指定を課す。18 件）** | 稼働クラスタ・稼働サービスへ当たる入口 | `check-login-existence-disclosure.js` / `check-password-reset-mail.js` / `check-stack-ready.js` / `seed-abac-policies.js` / `seed-search-documents.js` / `seed-tag-dictionary.js` / `measure-abac-combinations.js`（収集。`--input` はオフライン）/ `measure-cutover-inventory.js`（収集。`--input` / `--print-recreate-sql` はオフライン）/ `measure-search-ndcg.js`（収集。`--input` はオフライン）/ `verify-oidc-edge-flow.sh` / `verify-qdrant-attribute-payload.sh` / `verify-qdrant-fulltext-index.sh` / `verify-tool-oidc-logins.sh` / `k8s-local-up.sh` / `k8s-local-down.sh`（既定の `--dry-run` も稼働クラスタを読む）/ `k8s-local-images.sh`（クラスタのランタイムへ取り込む）/ `istio-edge-up.sh` / `istio-edge-down.sh` |
| **O（自前の `--live` で既に閉じている。1 件）** | 稼働クラスタへ触れる経路が、もともと明示の `--live` の後ろにある | `backup-restore-drill.sh`（`--live` のときだけ読み取り専用の psql で数える。`--live-counts` は使い捨てのコンテナだけ。`--self-test` はスタブだけ。拒否の経路は同スクリプトの T-1560-39 / T-1560-41 が固定済み）|
| **S（静的・オフライン）** | 語は検査対象の文字列・正規表現・注記に現れるだけ | `check-bff-downstreams.js` / `check-collector-self-telemetry.js` / `check-default-credentials.js` / `check-deploy-manifests.js`（`kubectl kustomize` と `helm template` はクラスタへ当たらない）/ `check-grafana-alerting.js` / `check-image-mapping.js` / `check-integration-config-timing.js` / `check-permission-denials.js` / `check-realm-constraints.js` / `check-secret-injected-options.js` / `check-test-spec-coverage.js` / `check-unit-service-ownership.js` / `lib/tool-oidc-login.js`（判定だけの純関数。I/O は `verify-tool-oidc-logins.sh`）/ `lib/mesh-mtls-mode.sh`（`source` される関数定義だけ。呼ぶのは L の `istio-edge-up.sh` / `istio-edge-down.sh`）|
| **T（スタブで走る試験器）** | 実ツールを PATH のスタブで置き換えて走る | `helm-synthetic-monitor.test.js`（`helm template` だけ）/ `k8s-local-down.test.sh` / `k8s-local-up.test.js` / `keycloak-realm-reconcile.test.js` / `platform-backup.test.js`（`kubectl kustomize` だけ）/ `reset-floor.test.js` / `reset-gate.test.js` / `setup.test.js` / `scripts.test.js` / `scripts.repo.test.js` |
| **D（データ・文書）** | 実行されない | `README.md` / `action-versions.repo.json` / `default-credentials-baseline.json` |
| **X（クラスタ以外のネットワーク）** | 稼働クラスタへは当たらない。本 issue の射程外 | `backlog-audit.js` / `check-action-versions.js` / `check-ci-latency.js`（GitHub API）/ `setup.sh`（dotnet の導入スクリプトの取得）|
| **C（除外・理由つき）** | 稼働中のクラスタにもサービスにも当たらない | `compose-up.sh`（引数をそのまま `docker compose` へ渡す薄い包み。副作用はサブコマンドが明示する。どのワークフローも呼ばない）|

L 18 ＋ O 1 ＋ S 14 ＋ T 10 ＋ D 3 ＋ X 4 ＋ C 1 ＝ 51。

### 機械検査の標識（`live-scripts.json` の `markers`）で引いた集合

判定を機械へ渡すため、クラスタへ当たる語に絞った標識で引き直した（`.test.*` / `*.json` / `*.md` はパターンで除外）。
**29 件**（新設の `lib/live-opt-in.js` 自身を含む。注記に `kubectl` を持つため）。L 18 ＋ O 1 ＋ offline 10
（S のうち標識に当たる 9 件 ＋ `lib/live-opt-in.js`）。S のうち標識に当たらないもの（`check-bff-downstreams.js` など）は
一覧に載せない —— 載せると「標識に当たらないのに一覧にある」古い行が残る。

### 軸 3: 呼び出し側（ワークフロー・文書）

`git grep` で L ＋ O のパスを全追跡ファイルから引いた（293 行。`.ai-context/` / `CHANGELOG.md` / submodule / `scripts/*.test.*` を除く）。
うち呼び出しの形（`node scripts/…` / `bash scripts/…` / `./scripts/…`）の行は 156 行。判定と結果は下の「追随の結果」。

### 除外（理由つき）

- **`.ai-context/` の確定済み記録**（specs / adr / superpowers）: 凍結記録であり本文を書き換えない（CLAUDE.md の主従）。
- **`CHANGELOG.md`**: 生成物。手で書き足さない。
- **`deploy/**` のブートストラップ（`deploy/local/keycloak-setup/reconcile-realm.sh` / `deploy/local/wikijs-setup/bootstrap.sh` /
  `deploy/local/vault/eso/bootstrap.sh` / `deploy/local/vault/oidc/bootstrap.sh`）**: issue の射程は `scripts/`。
  いずれも L の `k8s-local-up.sh`（と `check-stack-ready.js` の `--check`）から呼ばれ、単独でも使う。**射程外として記録し、別 issue の候補にする。**
- **`src/ai-stock-trading`**（submodule）: 他リポジトリ。

## 受け入れ基準

- [ ] L の 18 入口が、指定なしでは exit 3 と所定の文言（`--live` と `LIVE=1` を名指し）で終わり、ツールを 1 つも起動しない。
- [ ] オフラインのモード（`--self-test` / `--dry-run` / `--input` / `--print-*`）は指定なしで今のまま動く。
- [ ] ワークフローの呼び出しは `--live` を明示する。ワークフローの起動条件と必須 check は変わらない。
- [ ] 稼働スタックに対して呼ぶ live 文書の行は `--live` を持つ。
- [ ] `scripts/README.md` に対象の一覧と新しいスクリプトへの規則がある。新しいスクリプトは分類するまで `scripts-tests` が赤。
- [ ] スタブで走る既存の試験器（`k8s-local-up.test.js` / `k8s-local-down.test.sh` / `reset-floor.test.js`）が緑。

## テスト方針

- 拒否の経路だけを試す。**稼働の経路は一度も走らせない**（本作業のハード制約でもある）。
- 判定器そのもの（`isLiveOptedIn` / `withoutLiveFlag` / bash の `live_opt_in_scan`）は単体で試す。

## 計画書との差異

- 差異: なし（計画書は scripts の運用規約を定めない）。

## 追随の結果

- 軸 3 の呼び出しの形 156 行 ＝ `scripts/` 内 49 行 ＋ それ以外 107 行。
  - `scripts/` 内 49 行: 使い方の注記と、利用者へ出す案内文（`k8s-local-up.sh` の WARN・`istio-edge-up.sh` の ERROR 等）。
    稼働の経路を示す行へ `--live` を足した。オフラインのモードの行（`--self-test` / `--dry-run` / `--input` / `--print-*`）は変えていない。
  - それ以外 107 行 ＝ `--live` を足した 92 行（ワークフロー 7 行 ＋ 文書・runbook・設定の注記 85 行）＋ オフラインのモード 14 行 ＋
    `docs/operations/platform-infra-backup-runbook.md` の `backup-restore-drill.sh` 1 行（O。自前の `--live` / `--live-counts` を
    次の行で選ぶ形であり変えない）。
- 足した後に同じ語で引き直した（219 行）。`--live` もオフラインのモードも持たない行は、すべて呼び出しではない散文・注記の参照
  （「`scripts/k8s-local-up.sh` が作る」等）であることを 1 行ずつ確かめた。
- 規則 10（この変更で新たに誤りになる自分の記述）: 既存の試験がワークフローの行を `node scripts/check-stack-ready.js\n` の形で
  固定していた（`scripts.repo.test.js` の #783 後半の節）ので `--live` つきへ追随した。移行仕様書の引数を拾う試験（cutover）は
  `--live` を「スクリプトの文字列にあるか」で見ていたので、判定器を呼んでいるかで見る形へ直した。
  `verify-tool-oidc-logins.sh` をスタブへ向けて走らせる試験（#1163）は `LIVE=1` と判定器の複写を足した。
- 見つかった既存の穴: `scripts.repo.test.js` の #852 節は `check-stack-ready.js` / `check-password-reset-mail.js` /
  `check-login-existence-disclosure.js` を**引数なしで起こしていた**（git の呼び出しを数えるためのフックだけで、稼働クラスタへの
  接続は塞いでいない）。稼働クラスタのある作業機で `node scripts/scripts.test.js` を走らせるだけで本物の申請が起き得た。
  本変更で 3 本とも exit 3 で止まる。
- 🔴 ローカルで**走らせていない**もの: `k8s-local-up.test.js` / `k8s-local-down.test.sh` / `reset-floor.test.js`（PATH のスタブが
  この作業機の実ツールを確実に隠せる保証が無く、#1550 の監査で実際にスタブ漏れから本物の `kubectl delete` が飛んだため）。
  CI（ubuntu・クラスタ無し）の結果で確かめる。
