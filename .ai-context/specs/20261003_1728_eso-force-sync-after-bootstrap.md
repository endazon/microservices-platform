---
title: bootstrap が Vault に書いた KV を読む ExternalSecret にだけ force-sync を付け、同期の完了を待つ（#1728）
type: spec
status: done
related_ids: [NFR-18, SC-22, ADR-0095, ADR-0124, IADR-0494, IADR-0456, IADR-0485, IADR-0492, IADR-0103]
author: claude
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-18 秘密情報の管理)
  - planning:projects/microservices-platform/07_adr/ADR-0124 決定 1
  - planning:projects/microservices-platform/07_adr/ADR-0095 決定 3
issue: "#1728"
---

# 仕様書: bootstrap の書き込みの後に、それを読む ExternalSecret の同期を促す（#1728）

> 本仕様書は実装着手前に作成する。起点は #1728（PoC 2026-10-03 の配備。MSP `233f432d` / helm rev 15）。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-18**（秘密情報の管理）。#1682 / #1696 と同じ番号（bootstrap の seed の意味論）。画面 SC-22 の項目（`ai-stock-trading/app-secrets`）を含む。
- 計画 ADR: ADR-0124 決定 1（対になる秘密）、ADR-0095 決定 3（書き込みの後は即時同期を依頼する。画面の側は BFF が既に行う）
- 関連 IADR: IADR-0456（画面の force-sync・Reloader・無いときだけの seed）、IADR-0485（対になる秘密は無いときだけ）、IADR-0492（`kb-reader-auth-client-*` を足した）、
  IADR-0103（env は Pod 起動時に 1 度だけ解決）。本件の判断は **IADR-0494** に置く。
- **採番**: IADR-0493 は別ブランチの PR #1727 が使っている。先着尊重で本件は **0494** とする。本ブランチ単独では `check-adr-numbering` が
  「IADR-0493 が欠番」を出す（#1727 のマージ後に rebase すれば消える。#1727 より先にマージするなら、その間だけ欠番になる）。

## 何が起きたか

1. bootstrap（`deploy/local/vault/eso/bootstrap.sh`）が在る `secret/ai-stock-trading/app-secrets` へ `kb-reader-auth-client-*` を足した（`vkv_patch_if_missing`）。
2. AST の `ast-secrets`（`dataFrom.extract`・`refreshInterval: 1h`）は配備の前（02:08Z）に同期したきり。`kubectl apply` / `helm upgrade` は spec が同じなら同期を起こさない。
3. 手で `force-sync` を付けるとキーが入り、Reloader が trade-decision を作り直した。それまで KB 検索の読み手（AST#1078）は空の秘密で動いた。
4. `k8s-local-up.sh` の既存の待ち（`eso_wait`）は `condition=Ready` だけを見るので、古い同期のまま Ready の ExternalSecret を即座に通す。

## 直し方（決定。詳細と却下案は IADR-0494）

1. bootstrap.sh が、その実行で**書けた** KV を覚える（`mark_changed`）。数える経路: `vkv_create_if_absent` の put、SC-22 の 4 KV の「無いとき」の put、
   `vkv_patch_nonempty` / `vkv_patch_if_missing` の成功した patch。在る KV へ足したプロパティは「パス プロパティ」で別に覚える。
2. keycloak-smtp の構成値（host / port / starttls）は `vkv_patch_config` で**今と違うときだけ**書く（従前は毎回 patch ＝毎回「書いた」になる）。
3. 末尾の `eso_force_sync_changed`: 何も書いていなければ何もしない。書いていれば `kubectl get externalsecret -A` を 1 回引き、
   ストア `ESO_STORE`（既定 `vault-backend`）で、書いた KV を `data[].remoteRef.key` か `dataFrom[].extract.key` で読むものだけに `force-sync=<epoch>` を付ける。
4. 待つ: 促す前と違う `status.refreshTime` ＋ `Ready=True`、`dataFrom.extract` の ExternalSecret なら足したキーが同期先 Secret に在ること。
   上限 `ESO_FORCE_SYNC_TIMEOUT`（既定 120 秒。`0` は促すだけ）。超えたら未完の ExternalSecret を名指しして非 0。一覧・注釈の失敗も手順を出して非 0。
5. 年齢（age）・秘密鍵の扱いには触れない（本件は Vault → Secret の同期の契機だけ）。値は表示しない（Secret はキーの有無だけを見る）。

## 母集合（規則 9: 書く側と読む側を、誤りの側の文字列から引く）

走査は 2026-10-03・`origin/develop` = `233f432d`（作業中に develop は `3eea5ff6`＝submodule の更新 1 件だけ進んだ。走査対象のファイルは変わっていない）。
生の出力に対して判断した（規則 7）。本仕様書と IADR-0494 は `.ai-context/` で、軸 A・B の走査は `.ai-context` を除いた（規則 8）。

### 軸 A: Vault の KV へ書く箇所（誤りの側＝「書いたのに同期を促さない」）

- 引き方 1: `git grep -n -E 'vault[[:space:]]+(kv[[:space:]]+(put|patch)|write[[:space:]]+secret/)' origin/develop -- . ':!.ai-context'` ＝ 25 行（10 ファイル）。
- 引き方 2（HTTP で KV v2 を書く口）: `git grep -l -E 'secret/data/|/v1/secret' origin/develop -- . ':!.ai-context'` ＝ 9 ファイル（policy・BFF の Vault クライアントの試験・security.md）。
- 引き方 3（AST）: AST の作業コピー（`40d992ee`）で `git grep -n -E 'vault kv|force-sync' -- '*.sh'` ＝ 1 行（`scripts/k8s-local-deploy.sh:102` のコメント。Vault は書かない）。

| 書く箇所 | 窓 | 扱い |
| --- | --- | --- |
| `deploy/local/vault/eso/bootstrap.sh` の在る KV への patch（`vkv_patch_if_missing`＝app-secrets の auth 10 件、`vkv_patch_nonempty`＝llm 2・wikijs-sync 1・keycloak-smtp 6） | **在る**（PoC の型） | **是正** |
| 同 無いときの put（`vkv_create_if_absent` の 24 KV、llm / wikijs-sync / keycloak-smtp / app-secrets の 4 KV） | 在り得る（ExternalSecret が先に在り、KV が消えていた場合。エラー後の再試行を待つ） | **是正**（数える。害は無い） |
| `deploy/local/wikijs-setup/bootstrap.sh:277`（`msp/wikijs-sync` の put） | 無い（直後に Secret を直接書き、Pod を作り直す） | **対象外**（IADR-0494 残余） |
| BFF（画面 /admin/secrets）の KV への書き込み | 無い（書いた後に `ExternalSecretSync` が force-sync する。IADR-0456 決定 4） | **対象外** |
| runbook の手での patch / put（`llm-output-token-measurement-runbook.md:250`、`secret-item-console-injection-runbook.md:131`、`paired-secret-rotation-runbook.md:113`、`secret-rotation-runbook.md:144`） | 無い（いずれも直後に force-sync の手順を持つ: 252・165・120・154 行） | **対象外** |
| `object-storage-seaweedfs-cutover-runbook.md:67`（`object-storage-credentials` の put） | 無い（同じ手順で ExternalSecret を新しく apply し、初回の同期を Ready で待つ） | **対象外** |
| `deploy/local/vault/README.md:41`（ESO 導入前の手での put の例） | 対象外（ESO 経路の説明ではない。AST の runbook へ誘導） | **対象外** |
| その他の行（bootstrap.sh のコメント 2 行、runbook の注意書き 5 行、C# 試験 4 行） | — | 書き込みではない |

### 軸 B: その KV を読む ExternalSecret（誤りの側＝「書いた KV を読むのに促されない」）

- MSP: `git grep -l '^kind: ExternalSecret' origin/develop -- .` ＝ **27 ファイル**（すべて `deploy/local/vault/eso/externalsecret-*.yaml`。ストアはすべて `vault-backend`、
  キーはすべて `msp/<名前>`、形はすべて `data[].remoteRef`）。`deploy/helm` / `deploy/mail-relay` の該当はコメント・RBAC で ExternalSecret の定義ではない。
- AST（`40d992ee`）: `deploy/helm/ai-stock-trading/templates/external-secrets.yaml` の 3 本 —— `moomoo-credentials` / `moomoo-rsa`（`data[]`。KV は seed しない）、
  **`ast-secrets`（`dataFrom.extract` `ai-stock-trading/app-secrets`）**。ストアは values の `vault-backend`。
- **結論: 名前のリストを持たず、クラスタから引いて「書いた KV を読むもの」で絞る**（両リポの全 30 本を同じ規則で覆う。AST が増やしても追随不要）。
  `dataFrom.find` の ExternalSecret は 0 本（残余として IADR-0494 に記録）。

### 軸 C: 成功確認・案内の文書（「refresh を待つ」「Ready を見る」）

`git grep -n -E 'refresh ?1h|refreshInterval|次の同期|次の refresh|1 時間|最大 1h|最大 1 時間' -- docs deploy scripts/README.md README.md ':!*.yaml'` ＝ 29 行。
本件に関わるのは次の 4 件（残りはアラート・ダッシュボード・Obsidian 同期・BFF セッションの別の周期）。

| 箇所 | 扱い |
| --- | --- |
| `docs/operations/keycloak-smtp-relay-setup-runbook.md:175`（§1 の再 seed の後「次の refresh を待つ」） | **是正**（§1 の bootstrap が force-sync して待つ。長さ 0 は env の渡し忘れ） |
| `docs/operations/secret-item-console-injection-runbook.md:74`・`docs/migration/cutover-discard-and-rebuild.md:124`・`bootstrap.sh:196`（手動 apply の Secret が「次の同期で戻る」） | **対象外**: Vault ではなく起動器の手動 Secret の話（paired-secret-rotation-runbook の「起動の後に同期を促す」が受ける） |
| `docs/operations/paired-secret-rotation-runbook.md:117`・`secret-rotation-runbook.md:151`・`secret-item-console-injection-runbook.md:161`（手で書いた後の force-sync） | **対象外**（手での書き込みの手順として正しい） |

加えて #1728 が名指しした成功確認: `docs/operations/local-sso-recovery-runbook.md` STEP 0（起動ログの force-sync 行・`ast-secrets` の読み手のキー）、
`docs/migration/cutover-discard-and-rebuild.md` 手順 5（同）を**是正**。`deploy/local/vault/eso/README.md` に仕組みを 1 段落足す。

## 規則 10（この変更で新たに誤りになる自分の記述）

- `bootstrap.sh` の終わりの案内「refresh 1h。画面…は BFF が force-sync」→ bootstrap 自身も促す旨を足した。
- `keycloak-smtp-relay-setup-runbook.md` §2 の注記（上の軸 C）→ 是正。
- `secret-item-console-injection-runbook.md:72`「keycloak-smtp の構成値は毎回 env と Git の値へ揃え直され」→ 結果（値が揃う）は変わらないので**据え置き**。
- 凍結記録（IADR-0456・#1477 / #1682 の仕様書）の「毎回 patch」等の記述は書き換えない。
- `SecretItemBootstrapSeedTests` の補助関数の形の検査（`[ -n "$3" ] || return 0` が patch より前・`$2=-`・`&& return 0`・`if vkv_exists "$path"; then` → `return 0` → put）は、
  関数の形を `if …; then mark_changed; else WARN; fi` に変えても成り立つ（下の検証で dotnet test を回して確かめる）。

## 規則 11（窓の形の表）

窓 ＝ 「Vault に書いた時刻（前の端）」から「消費側 Secret がその値を持つ時刻（後の端）」まで。
プローブ: **増える側** ＝ 在る KV へキーを足した（Secret はそのキーを得なければならない）、**減る側** ＝ 何も書かない再実行（何も促さず・待たない）、
加えて **止まる側** ＝ 同期しない ExternalSecret（緑で終わってはならない）。

| 形 | 増える側 | 減る側 | 止まる側 | 実測（変異） |
| --- | --- | --- | --- | --- |
| 後の端だけ（Ready を待つ＝既存の `eso_wait`） | ✗ 古い同期のまま Ready で通る（PoC） | ○ | ✗ | M6（refreshTime を見ない）・M8（キーを見ない）で再現。それぞれ 1 本が殺す |
| 前の端だけ（書いた後に注釈を付けて終わる） | △ いずれ入るが確かめない | ○ | ✗ 同期しなくても緑 | M5（常に待たない）。5 本が殺す |
| **両端（促す前の refreshTime を控え、変わる＋Ready＋キーの在否まで待つ）** | ○ | ○ | ○ | 採用 |

## 変更するもの

| ファイル | 変更 |
| --- | --- |
| `deploy/local/vault/eso/bootstrap.sh` | `mark_changed`・`vkv_patch_config`・`eso_force_sync_changed`、書き込みの 4 経路で記録、終わりの案内 |
| `scripts/scripts.repo.test.js` | #1728 節（kubectl の記録スタブで bootstrap.sh を実走。13 本） |
| `deploy/local/vault/eso/README.md` | 仕組みの段落 |
| `docs/operations/local-sso-recovery-runbook.md`・`docs/migration/cutover-discard-and-rebuild.md`・`docs/operations/keycloak-smtp-relay-setup-runbook.md` | 成功確認（trace ブロックへ IADR-0494・本仕様書・#1728） |
| `.ai-context/adr/IADR-0494_*.md`（新規）・`README.md`（索引） | 判断の記録 |

## 受け入れ基準（→ 試験。`scripts/scripts.repo.test.js` の #1728 節）

1. 在る app-secrets へ無い 2 キーだけを足し、それを読む `ast-secrets` にだけ、**最後の patch の後で** `force-sync=<数字>` を付ける。
   同じ名前空間の別 KV・書いていない KV・別ストア・値の同じ構成値の ExternalSecret には付けない。同期先 Secret のキーを確かめる。
2. 何も書かない再実行は ExternalSecret を引かず、付けない。
3. 無いときの put（SC-22 の llm・対になる秘密の bff-oidc）も数え、それを読む ExternalSecret に付ける。
4. 構成値は今と違うときだけ書き、書いたら keycloak-smtp に付ける。
5. 同期しない（`ast-secrets`・`data[]` の llm）・Ready にならない・足したキーが無い → 名指しして非 0。
6. 一覧・注釈の失敗 → 手順を出して、待たずに非 0。
7. `ESO_FORCE_SYNC_TIMEOUT=0` は付けるだけで待たない。整数でない指定は付ける前に止める。
8. patch に失敗したキーは数えない（WARN のみ）。

## 検証の記録（2026-10-03・本ブランチ）

| 検査 | 結果 |
| --- | --- |
| `bash -n deploy/local/vault/eso/bootstrap.sh` | OK |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 本ブランチ単独では `check-adr-numbering` の companion 試験が「IADR-0493 が欠番」で落ち、以降が走らない（上の採番）。**0493 の仮置き（コミットしない）を置いて流すと 869 件すべて通過**（#1728 節 13 本を含む） |
| `node scripts/k8s-local-up.test.js`（bootstrap.sh をスタブで通す既存の試験） | 257 件通過 |
| `bash scripts/k8s-local-down.test.sh` | 75 passed / 0 failed |
| `node scripts/check-adr-numbering.js` | 「IADR-0493 が欠番」のみ（#1727 待ち。仮置きで OK を確認） |
| `check-trace-blocks` / `check-cross-repo-refs` / `check-plan-id-qualification` / `gen-knowledge-graph --check` / `check-doc-links` / `STRICT_AI_WORKFLOW_CONFIG=1 check-ai-workflow-config` | すべて OK |
| `Platform.Bff.Tests` の `SecretItemBootstrapSeedTests`（bootstrap.sh の形を読む C# 試験） | この環境に dotnet が無い。**同じ正規表現を node へ移して当て、5 つの検査（put の分岐・3 補助関数の形・app-secrets の在る側の 10 件）が成り立つことを確かめた**。CI の dotnet test で確定する |

### 変異試験（`bootstrap.sh` に 1 つずつ入れて #1728 節の 13 本を流す。基準は 13/13 通過）

| # | 変異 | 落ちた本数 |
| --- | --- | --- |
| M1 | `vkv_patch_if_missing` の成功で記録しない | 8 |
| M2 | 末尾の `eso_force_sync_changed` を呼ばない | 11 |
| M3 | ストアで絞らない | 5 |
| M4 | 書いた KV のパスで絞らない（全部に付ける） | 4 |
| M5 | 常に待たない | 5 |
| M6 | `refreshTime` の変化を見ない | 1（初回の 11 本では生存 → `data[]` で読む llm の試験を足した） |
| M7 | Ready を見ない | 1 |
| M8 | 足したキーの在否を見ない | 1 |
| M9 | 時間切れで 0 を返す | 4 |
| M10 | 一覧の失敗で 0 を返す | 1 |
| M11 | 構成値を毎回書く（従前） | 5 |
| M12 | `vkv_create_if_absent` の put で記録しない | 1（初回は生存 → bff-oidc の試験を足した） |
| M13 | llm の無いときの put で記録しない | 2 |
| M14 | patch の失敗でも記録する | 1 |
| M15 | 時間の指定の整数検査を外す | 1 |
| M16 | 注釈の失敗で止めない | 1（初回は生存。止める文言は出たまま待ちへ進んでいた → 「待たずに止める」の表明を足した） |

16 個すべてを殺した（生存 0）。

## ［2026-10-03 追記 / #1728・独立監査］監査所見 5 件への対処

上の記録（検証・変異試験の表）は書き換えない。独立監査（PR #1730）の所見に次のとおり対処した。

| 所見 | 対処 |
| --- | --- |
| 🟡1 待ちの中で同期先 Secret を `jsonpath='{.data}'` で引き、base64 の値をシェル変数へ入れていた（`bash -x` で漏れる） | `-o go-template='{{range $k, $v := .data}}{{$k}}{{"\n"}}{{end}}'` でキー名だけを引き、行の完全一致で在否を見る。IADR-0494 決定 4 へ日付つき追記 |
| 🟡2 値が出ないことの表明が無い | スタブの Secret を 1 行 1 キー「名前=値」（値は `c2VjcmV0LTE3MjgtdmFsdWU=`）に変え、go-template にはキー名、`{.data}` には値つき JSON を返す。`run1728` の全実行で stdout＋stderr に値が無いことを表明し、`bash -x` で流す試験を足した。試験 1 は「go-template で引いた」「`{.data}` で引いていない」も表明する |
| 🟢2 促す前の `refreshTime` を `grep -F "ns\|name\|" \| head -n 1` で引いていた（部分一致） | `awk -F'\|' '$1 == ns && $2 == n'` の完全一致へ。`platform-infra/keycloak-smtp`（同期する・促す前の時刻が別）と `infra/keycloak-smtp`（同期しない）を並べる試験を足した（スタブに `refresh/<ns>_<name>` を追加） |
| 🟢5 `ESO_FORCE_SYNC_TIMEOUT=08` が `$((…))` で落ちる | 整数検査の後に `$((10#…))` で正規化（INTERVAL も）。`08`（待って緑・`8s 以内`）と `00`（待たない）の試験を足した |
| 🟢1 `docs/migration/cutover-discard-and-rebuild.md` §5 で `--baseline` の注記がコードフェンスの後ろへずれた | 注記を挿入ブロックの前（「どちらも緑…」の段落の直後）へ戻し、フェンスの後に空行を置いた |

### 変異試験（追記分。`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` を流し、最初に落ちた表明を記す。基準は 872 件通過）

| # | 変異 | 結果 |
| --- | --- | --- |
| A1 | キーの引き方を `jsonpath='{.data}'`＋JSON の文字列照合へ戻す | 赤（試験 1「キー名だけで確かめていない」） |
| A2 | A1 に加え、試験 1 の引き方の表明 2 つを外す | 赤（`bash -x` の試験で「同期先 Secret の値が出力に出た」。xtrace に `keys='{"service-auth-client-id":"c2Vj…` が出る） |
| A3 | go-template のまま、`{.data}` を引いて echo する行を足す | 赤（試験 1 で「同期先 Secret の値が出力に出た」） |
| A4 | 完全一致を `grep -F … \| head -n 1` へ戻す | 赤（「infra の同期前の時刻を platform-infra の行から引き、…緑で終わった」） |
| A5 | `10#` の正規化を外す | 赤（`08: value too great for base`） |

5 個すべてを殺した（生存 0）。各変異の後に `bootstrap.sh`・試験を元へ戻した。
