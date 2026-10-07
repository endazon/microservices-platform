---
title: 作業仕様書 — ローカル・PoC のスクリプトで秘密値・資格情報をプロセスの引数へ載せない（#1793）
type: spec
status: done
related_ids: [NFR-18, ADR-0095, SC-22, IADR-0096, IADR-0327, IADR-0457, IADR-0485, IADR-0494, IADR-0504]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1793"
---

# 作業仕様書 — 秘密値・資格情報を argv から外す（#1793。#1767 の後続）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `62b059be`。
> 計画は project-planning `aa068ac`（隣接クローン・読み取り専用）の NFR-18 を読んだ。
> 🔴 **稼働中のクラスタ・実 Vault・実 Keycloak には何も実行しない**（記録スタブの下での実走試験と静的検査だけ）。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR-18**（シークレット管理。認証情報・API キーを集中管理する）。
- 計画 ADR: **ADR-0095**（秘密情報の投入の面）。画面: SC-22（wikijs-sync・keycloak-smtp・llm-provider-credentials の項目）。
- 先行: #1764 / IADR-0504（llm-provider-credentials）、#1767 / PR #1792（wikijs-sync・keycloak-smtp の作成経路）。
  #1767 の作業仕様書の除外表 E1〜E3 は**本件で解消する**（同仕様書は凍結記録なので書き換えない）。
- **IADR は起こさない。** 直し方はすべて「値を argv から stdin・0600 のファイルへ移す」の同じ型であり、
  新しい設計判断を持たない（各箇所の手段の選択理由は下の「設計」に置く）。

## 問題

秘密値・資格情報がプロセスの引数（`ps`・`/proc/<pid>/cmdline`。同じノードの誰でも読める）に載る経路が、
ローカル・PoC のスクリプトに残っている。issue は 7 か所を挙げるが、**規則 9 で引き直すと 14 群ある**（下の母集合）。

## 受け入れ基準

- AC1: 下の母集合の「対象」各箇所で、秘密値・資格情報を与えて実行しても、その値は**どのプロセスの引数にも現れない**
  （ホストの kubectl / curl / vault / node と、Pod 内の sh / vault / curl / kcadm の両方）。記録スタブで実走を固定し、
  実走を置けない箇所（Pod 内の kcadm・Node の spawn）は静的な検査で固定する。
- AC2: `vkv_create_if_absent`（対になる秘密の 24 KV）の作成は**原子的**である —— 1 回の `vault kv put -cas=0 … -` で
  全プロパティを入れ、後から patch しない。作成が失敗すれば KV は残らない（空の秘密を持つ KV が残らない）。
  `SecretItemBootstrapSeedTests` を追随させて緑にする。
- AC3（否定形）: 冪等性は不変 —— KV が既に在るときは何も書かない。Secret の中身（キー・値）は従来と同じ。
  試験値は実在の鍵に見えない形（`dummy-…` を実行時に組み立てる。gitleaks）。
- AC4: 旧形へ戻すと試験が落ちる（変異で確かめる）。
- AC5: 母集合（規則 9・10）を `*.sh` に限らず `.js` と `kubectl exec` の中で展開される式まで含めて引き直し、除外は理由と外す条件を書く。

## 母集合（規則 9。`62b059be` 時点）

### 走査

追跡下のファイル（`git ls-files`）から `.ai-context/`・`docs/`・`*.md`・`*.test.js`・`*.cs`・`*.ts(x)`・`*.json` を除いた 403 本を、
誤りの側の字面で走査した:

- `--from-literal` / `stringData` を含む `-p` / `Authorization: Bearer` / `--password` / `kcadm` / `password=$`
- `curl … (-d|--data*|-H|-u|--user) … $` / `vault (kv put|kv patch|write) … =$`（`=-` を除く）/ `helm --set …(secret|password|token|key)=`
- 秘密らしい変数（`PASSWORD|SECRET|TOKEN|APIKEY|API_KEY|_key|KEY`）の展開を含む行のうち、プロセスの引数を組む文
- `.js` の `spawnSync|execFileSync|spawn(|execFile(` のうち引数に資格情報を組むもの
- YAML（manifest・workflow）の `curl|kcadm|vault|psql|redis-cli … $…(PASSWORD|SECRET|TOKEN|KEY)` / `Bearer` / `PGPASSWORD`
- 上記の手当てで足りない形の補足: `vault operator unseal "$…"`・`node … totp.js "$…"`・`-H "api-key: …"`

### 対象（14 群）

| # | 箇所 | 何が載るか | issue の 7 か所 |
| --- | --- | --- | --- |
| T1 | `deploy/local/vault/eso/bootstrap.sh` `vkv_create_if_absent`（呼び出し **24**。issue 本文の「22」は起票後に増えた） | env で上書きした値が Pod 内 `sh -c` と kubectl の引数 | 1 |
| T2 | `deploy/local/wikijs-setup/bootstrap.sh:277` | Pod 内 vault の引数に `apiKey="$K"` | 2 |
| T3 | 同 `write_secret`（create の `--from-literal` と **patch の `-p '{"stringData":…}'`**） | 管理者パスワード・API キーが kubectl の引数 | 3（patch は追加） |
| T4 | `scripts/k8s-local-up.sh` `apply_secret`（呼び出し 28） | env 由来のパスワード・鍵が kubectl の引数 | 3 |
| T5 | `scripts/k8s-local-up.sh:965` argocd-secret の `patch -p '{"stringData":{"oidc.keycloak.clientSecret":…}}'` | client secret が kubectl の引数 | **追加** |
| T6 | `deploy/local/vault/oidc/bootstrap.sh:59` | ホスト vault の引数に `oidc_client_secret=…` | 4 |
| T7 | `deploy/local/wikijs-setup/bootstrap.sh` `wiki_post`（呼び出し元 236・248・**259**・267・**468**） | JWT・API キーが kubectl と Pod 内 curl の引数 | 5（259・468 は追加） |
| T8 | `scripts/check-stack-ready.js` `wikiJsGraphql`（G7） | API キーが kubectl と Pod 内 curl の引数（T7 と同形） | **追加** |
| T9 | `scripts/check-password-reset-mail.js:277-278` | Pod 内 kcadm の引数に `--password` | 6 |
| T10 | `scripts/measure-abac-combinations.js` `kcadmLogin` | kubectl と Pod 内 kcadm の引数に `--password` | **追加** |
| T11 | `scripts/measure-cutover-inventory.js` Keycloak の収集 | 同上 | **追加** |
| T12 | `scripts/verify-oidc-edge-flow.sh` `acquire_session` | ①パスワード（345）②TOTP の生シークレット `totpSecret=`（425）③OTP の値（424）④`node totp.js "$otp_secret"` / `--encode "$otp_raw"`（403・420） | 7（②〜④は追加） |
| T13 | `scripts/verify-qdrant-attribute-payload.sh:60` / `scripts/verify-qdrant-fulltext-index.sh:70` | Qdrant の API キーがホスト curl の引数（`-H "api-key: …"`） | **追加** |
| T14 | `deploy/local/vault-persistence/vault-entrypoint.sh:96` | unseal 鍵が Vault コンテナ内 vault の引数（`operator unseal "$key"`） | **追加** |

### 除外（理由と外す条件）

| 箇所 | 理由 | 外す条件 |
| --- | --- | --- |
| eso/bootstrap.sh `ai-stock-trading/app-secrets` の作成 | 引数に載るのは**リポジトリに在る dev 既定**（realm と同値）と空文字だけ。env 由来の値を持たない | env での上書きを足したら対象 |
| eso/bootstrap.sh `keycloak-smtp` 作成の host / port / starttls | 構成値（秘密ではない）。from / user / password は #1767 で stdin 化済み | — |
| eso/bootstrap.sh `vexec` の `VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"` | Pod 内の env の参照であり、引数には `$VAULT_DEV_ROOT_TOKEN_ID` の字面しか載らない | — |
| vault-entrypoint.sh:112 `token create -id="$fixed"` | 値は既知の dev 既定（`VAULT_DEV_ROOT_TOKEN_ID`。Pod の env と Secret `vault-dev-token` に同じ値が在る）。直すと CLI 形（`auth/token/create-orphan` への write）が変わり起動経路の危険が増える | root トークンを dev 既定以外（乱数・回転）にしたら対象 |
| `fetch()` に `Authorization: Bearer` を付ける Node（seed-*.js・backlog-audit.js・check-ci-latency.js・reset-gate.js・reconcile-realm.js・synthetic-monitor probe.js・k6） | プロセス内の HTTP ヘッダであり、子プロセスの引数を組まない | — |
| check-login-existence-disclosure.js:461 | 要求本文をプロセス内で組んで送る（子プロセスなし） | — |
| verify-oidc-edge-flow.sh の `-H "$CSRF_HDR"` / `-b "$SESSION_JAR"` | CSRF ヘッダの値は定数 `1`（秘密でない）。セッション Cookie はファイル（jar）経由 | — |
| wikijs-setup 段 3・段 8 の psql | 値は heredoc（stdin）で渡す | — |
| measure-*.js の `psql -c <sql>` | SQL に資格情報を含まない | — |
| k8s-local-up.sh:1329 `patch cronjob … suspend` | 秘密でない | — |
| `docs/`・`deploy/**/README.md` の手順中の `--from-literal` / `kcadm --password`（8 本） | 人が打つ手順書であり、スクリプトの経路ではない（issue の範囲はスクリプト）。本 PR は手順書の字面を変えない | 手順書の書き換えを別 issue で起票したら |
| `.ai-context/`（凍結記録）・`*.test.js`（スタブ・試験値）・`src/ai-stock-trading`（別リポ） | 経路ではない／別リポ | — |

## 母集合（規則 10。この変更で新たに誤りになる記述）

- `deploy/local/wikijs-setup/bootstrap.sh` 冒頭「なお curl の引数はコンテナ内のプロセス表に一瞬現れる（…許容する）」→ **誤りになる。書き換える。**
- `scripts/k8s-local-up.test.js` の `--from-literal=username=` 等を見る 2 試験・argocd の `oidc.keycloak.clientSecret` トークン → **追随する**（`--from-file=` と patch ファイル名へ）。
- `SecretItemBootstrapSeedTests.Paired_secret_kvs_are_created_only_when_absent` の呼び出し字面（`vkv_create_if_absent <path> "…"`）→ **追随する**。
- `deploy/local/vault-persistence/vault-entrypoint.test.sh` の `operator unseal STUB-UNSEAL-KEY` の表明 → **追随する**。
- `check-password-reset-mail.js` の JSDoc「資格情報は Pod の env のまま使い」→ 不変で正しい（env のまま stdin へ流す）。
- docs の `apply_secret` への言及（secret-rotation-runbook・paired-secret-rotation-runbook・security.md・eso/README.md）は
  名前と役割だけを書き、`--from-literal` に触れない —— **不変で正しい**。
- 導出値: bootstrap.sh 末尾の案内「対になる秘密（… 24 KV）」は呼び出し数 24 と一致（計算し直した）。

## 設計（各箇所の手段と選んだ理由）

| # | 手段 | 理由 |
| --- | --- | --- |
| T1 | `vkv_create_if_absent <path> <key> <value> [<key> <value> …]` へ署名を変え、ホストで JSON を組み（bash の置換だけ。exec しない）、`printf '%s' "$json" \| vexec "vault kv put -cas=0 secret/$path -"` | 1 回の put で全プロパティを入れる（原子的）。`key=-` は 1 プロパティしか stdin から取れないため、2 プロパティの KV（object-storage・rabbitmq）を 1 回で作るには JSON の stdin（`-` 単独）しかない |
| T2 | `printf '%s' "$new_key" \| … vault kv put … apiKey=-` | vault 1.16 の kv-builder は stdin を末尾改行ごと読む（#1767 の実測）ので here-string ではなく `printf '%s'` |
| T3・T4・T5 | `mktemp -d`（0700）＋ `umask 077` のファイル（0600）へ `printf`（bash 組み込み）で書き、`--from-file=<key>=<file>` / `--patch-file <file>` へパスだけを渡す。サブシェル関数の `trap … EXIT` で必ず消す | `kubectl create secret` に標準入力から値を取る形は無い。manifest を stdin へ流す案は YAML のエスケープを自前で持つことになる。`--from-file` は値をバイトのまま入れる（`--from-literal` と同じ結果） |
| T6 | `printf '%s' "$CLIENT_SECRET" \| vault write … oidc_client_secret=-` | issue の候補どおり。他の引数は秘密でない |
| T7・T8 | Pod 内で `sh -c '<script>' sh curl …` を起動し、stdin の**1 行目**をベアラー（空なら付けない）として `read` で読み、残りを本文として curl の `--data-binary @-` へ渡す。ヘッダは組み込みの printf でパイプへ書き、`3<&0 0<&4`（元の stdin は `exec 4<&0` で退避）で curl の fd 3 にして `-H @/dev/fd/3` で読ませる | stdin は本文で使っているので `-H @-` は使えない。`read` は組み込みで 1 バイトずつ読むため残りの本文はそのまま curl へ届く。script は 1 行に収める（heredoc は改行が要り、kubectl の記録スタブが 1 呼び出し 1 行で控える前提を崩す）。`/dev/fd/3` は sh が作るパイプなので開ける（kubectl exec の stdin の種類に依らない）。`-H @file` は curl 7.55 以降 |
| T9〜T11 | kcadm の `--password` を省き、パスワードを stdin へ流す（Keycloak 24 の `config credentials` は「`echo <pw> \| kcadm.sh config credentials … --user admin`」を自身の用法に載せている。`IoUtil.readSecret` が stdin を読む） | env 変数（`KC_CLI_PASSWORD`）は Keycloak 24 の kcadm に無い（ソースで確認）。T9 は Pod 内で `printf '%s\n' "$KEYCLOAK_ADMIN_PASSWORD" \| kcadm.sh …`（printf は組み込み）、T10・T11 は `kubectl exec -i` の `input` へ流す |
| T12 | パスワードは `--data-urlencode password@-`（stdin）。OTP 画面の `totpSecret` と OTP の値は、実行ごとの私用一時ディレクトリ（0700。既存の `cleanup_session_jars` の EXIT trap で消す）の 0600 ファイルから `--data-urlencode name@file`。`totp.js` は `-` を受けたら stdin から読む（引数形は残す＝`scripts.test.js` の既存試験と互換） | 1 回の curl で stdin は 1 つしか使えないため、2 値以上の要求はファイル |
| T13 | 鍵が在るときだけ `printf 'api-key: %s\n' "$QDRANT_API_KEY" \| curl … -H @-` | 両スクリプトとも curl の stdin を使っていない（本文は引数かファイル） |
| T14 | `printf '%s' "$key" \| vault write sys/unseal key=- >/dev/null` | `vault operator unseal` は引数か端末からしか読まない（stdin が端末でないと失敗する）。`sys/unseal` は同じ API（`PUT /v1/sys/unseal {"key":…}`）で、`vault write` の `key=-` は stdin から読む |

挙動差: Secret・KV・Vault 設定の中身は不変。T1 は値に `'` を含むと従来は `sh -c` が壊れたが、JSON なので通るようになる（改善）。

## 試験

- T1: `scripts/scripts.repo.test.js` の #1728 記録スタブ（`$STUB_LOG.argv` が Pod 内 `sh -c` の引数を控える）を拡張し、
  `put` の stdin を控える・`put-fail` で作成を失敗させる。24 KV すべてについて「目印が argv・kubectl 引数・出力に無い／
  stdin の JSON に入っている／書き込みは PUT 1 回だけ」、作成失敗で KV が残らないこと、在る KV には書かないことを固定。
  C#: `SecretItemBootstrapSeedTests` を新しい呼び出し字面へ追随し、helper が `secret/$path -` と stdin を使うことを足す。
- T2・T3・T7: `deploy/local/wikijs-setup/bootstrap.sh` を、`exec … -- <cmd>` を**ローカルで実行する** kubectl スタブと、
  引数を控える curl / vault スタブの下で実走させる（Pod 内のプロセスの引数まで見える）。
- T4・T5: `scripts/k8s-local-up.test.js` の記録スタブで、env の目印がどの kubectl の引数にも出ないこと・`--from-literal` が無いこと。
- T6: oidc/bootstrap.sh を vault・kubectl・jq スタブの下で実走し、目印が vault の引数に無く stdin に在ること。
- T12・T13: 静的検査（実走はエッジと Keycloak が要る）＋ `totp.js -` の実走。
- T8〜T11: 静的検査（関数を取り出して組む引数を検査する）。
- T14: `vault-entrypoint.test.sh` のスタブを `write sys/unseal key=-`（stdin）へ追随し、鍵が引数に無いことを足す。

## 検証の記録

すべて本環境（稼働クラスタなし）で実行した。

- 試験（`scripts/scripts.repo.test.js` に #1793 の 14 件。#1728 の記録スタブの拡張と、`exec … -- <cmd>` を手元で実行する kubectl スタブ）:
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 緑。`node scripts/k8s-local-up.test.js` 緑（262 件。#1793 の 2 件を含む）。
  `bash deploy/local/vault-persistence/vault-entrypoint.test.sh` 42 passed。
- C#: `dotnet test …/Platform.Bff.Tests.csproj --filter SecretItemBootstrapSeedTests` 6 件緑（`src/ai-stock-trading` を手元の AST へ一時的に向けてビルド。コミットには含めない）。
- 変異（対象ファイルを `origin/develop` 版へ戻して #1793 の試験を流す）: wikijs-setup/bootstrap.sh・check-stack-ready.js・
  check-password-reset-mail.js・vault/oidc/bootstrap.sh・verify-oidc-edge-flow.sh・measure-abac-combinations.js・
  measure-cutover-inventory.js・verify-qdrant-fulltext-index.sh・lib/totp.js の**それぞれで 1 件以上が落ちる**。
  eso/bootstrap.sh は「JSON を引数へ埋める」変異で「秘密値が sh -c の引数に載った」と落ちる。
  vault-entrypoint.sh を戻すと vault-entrypoint.test.sh が 5 件落ちる。k8s-local-up.sh の apply_secret を戻すと k8s-local-up.test.js が落ちる。
- 手元の実物で形を確かめた: T7 の Pod 内 script を dash / bash と実物の curl 8.5 で流し、ベアラーがヘッダに、本文が本文に届くこと。
  T13 の `-H @-` を実物の curl で確かめた。T1 の JSON 組み立て（`"` `\` 改行 タブ `'` `&` を含む値）が JSON.parse で往復すること。
- 静的: 触ったシェルすべて `bash -n` 緑（vault-entrypoint.sh は `sh -n` も）。shellcheck 0.11.0 は触る前と件数が同じ
  （wikijs-setup/bootstrap.sh の +1 は意図した単一引用符の SC2016・note）。
- 🔴 **稼働環境では未確認**: Pod 内 kcadm の stdin 入力（Keycloak 24 の用法に載っている形）、`vault write sys/unseal key=-`、
  wiki-js コンテナ（busybox ash）での T7 の script。いずれも次回の `k8s-local-up.sh`・各検証スクリプトの実走で確かめる。
