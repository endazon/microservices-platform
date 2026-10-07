---
title: 作業仕様書 — bootstrap.sh の KV 作成経路で wikijs-sync / keycloak-smtp の秘密値を sh -c の引数へ載せない（#1767）
type: spec
status: done
related_ids: [NFR-18, SC-22, ADR-0095, IADR-0096, IADR-0456, IADR-0494, IADR-0504]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1767"
---

# 作業仕様書 — KV 作成経路の秘密値を argv から外す（#1767）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `5b4d8b11`。
> 計画は project-planning `aa068ac`（隣接クローン・読み取り専用）を読んだ。
> 🔴 **稼働中のクラスタ・実 Vault には何も実行しない**（kubectl の記録スタブの下での実走試験だけ）。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR-18**（シークレット管理）。起点 issue の件名は `NFR-05` だが、計画の NFR-05 は可用性（99.9%）であり
  本件に当たらない。bootstrap.sh の同じ区画のコメント（IADR-0494・IADR-0504）が引く **NFR-18** を採る。
- 計画 ADR: ADR-0095（秘密情報の投入の面）。画面: SC-22（wikijs-sync・keycloak-smtp は SC-22 の項目）。
- 先行: #1764 / IADR-0504（`msp/llm-provider-credentials` の作成経路を「空で作る → `vkv_patch_nonempty`（値は stdin）」へ直した）。
  本件は**同じ形を残り 2 KV へ揃える**だけで、新しい判断を持たない —— **IADR は起こさない**（判断は IADR-0504 の監査対応で済んでいる）。

## 問題

`deploy/local/vault/eso/bootstrap.sh` の `vexec` は第 1 引数を `kubectl exec … -- sh -c '<前置き>; '"$1"` で渡す。
KV が無いときの作成文が env の値を `"…apiKey='${WIKIJS_SYNC_APIKEY:-}'"` の形で埋め込むため、値が

1. ホスト側 `kubectl` の argv（`ps`・`/proc/<pid>/cmdline`）
2. Pod 内 `sh -c` と `vault` の argv

に載る。値に `'` を含むとコマンドが壊れるか注入になる。

## 受け入れ基準

- AC1: `msp/wikijs-sync` が無いとき、`WIKIJS_SYNC_APIKEY` の値は `sh -c` の引数・kubectl の引数・出力のいずれにも現れない。
  作成は `apiKey=''`、値は env が空でないときだけ `vkv_patch_nonempty`（`apiKey=-`・stdin）で入れる。
- AC2: `msp/keycloak-smtp` が無いとき、`SMTP_FROM` / `SMTP_USER` / `SMTP_PASSWORD` の値は同じく現れない。
  作成は構成値（host / port / starttls）を従来どおり入れ、`from='' user='' password=''`。値は env が空でないものだけ stdin で入れる。
- AC3: env が無い初回は空で作るだけで部分更新しない（従来と同じ「書いた KV」＝ force-sync の対象）。
- AC4: 在る KV の経路（`vkv_patch_nonempty` / `vkv_patch_config`）・`-cas=0`・`vkv_exists` の分岐は不変
  （`SecretItemBootstrapSeedTests` の字面検査を満たす）。
- AC5: 旧形へ戻すと試験が落ちる（変異で確かめる）。

## 母集合（規則 9・10。`5b4d8b11` 時点）

### 規則 9（誤りの側の文字列で走査）

走査: `git ls-files '*.sh' | xargs grep -nE "(kv put|kv patch|vault write)…\$\{?…(KEY|SECRET|PASS|TOKEN|APIKEY)"`（`=-` を除く）、
`exec … -- (sh|bash) -c`、`--from-literal`、`vault write` を含む全スクリプト（`deploy/` `scripts/`）。

| 箇所 | 形 | 扱い |
| --- | --- | --- |
| `deploy/local/vault/eso/bootstrap.sh` `msp/wikijs-sync` 作成 | env の鍵を `sh -c` 引数へ | **対象**（AC1） |
| 同 `msp/keycloak-smtp` 作成 | env の from/user/password を `sh -c` 引数へ | **対象**（AC2） |
| 同 `msp/llm-provider-credentials` | — | #1764 で是正済み（陽性対照の試験あり） |
| 同 `vkv_create_if_absent`（対になる秘密 22 KV。OIDC client secret・s2s・DB/ブローカ/Keycloak 管理者のパスワード） | env 上書き時に値を `sh -c` 引数へ | **除外**（下記 E1） |
| 同 `ai-stock-trading/app-secrets` 作成 | リポジトリ内の dev 既定（realm と同値）だけ。env 由来の値は無い | 除外（argv に載るのは公開済みの dev 既定のみ） |
| `deploy/local/wikijs-setup/bootstrap.sh:276-277` | 値は stdin で `sh` へ渡すが、Pod 内で `vault kv put … apiKey="$K"` と展開し **Pod 内の vault の argv** に載る | **除外**（E2） |
| `deploy/local/wikijs-setup/bootstrap.sh` `write_secret`（`--from-literal` / `patch -p`） | 発行した API キーを kubectl の argv へ | **除外**（E3） |
| `scripts/k8s-local-up.sh` `apply_secret`（`--from-literal`） | env 由来のパスワード・鍵を kubectl の argv へ | **除外**（E3） |
| `deploy/local/vault/oidc/bootstrap.sh` `oidc_client_secret="$CLIENT_SECRET"` | ホストの vault CLI の argv へ | **除外**（E3） |

除外の理由と外す条件:

- **E1（vkv_create_if_absent）**: 対になる秘密は**原子的に作る**必要がある。llm と同じ「空で作る → patch」に分けると、
  途中で失敗したとき空のプロパティを持つ KV が残り、次回以降は「在るので触らない」（#1682）ため**空の秘密が恒久化する**。
  直すには JSON を stdin で渡す `vault kv put -cas=0 secret/<path> -` 形と、値の JSON エスケープ、
  `SecretItemBootstrapSeedTests` の呼び出し字面（`vkv_create_if_absent <path> "…"`）の改定が要る。issue の範囲（2 KV）を越えるため別 issue とする。
  既定値はリポジトリに在る dev 既定であり、env で上書きしたときだけ露出する。
- **E2**: kubectl の argv には載らず、Pod 内の短命プロセスに限られる。直し方は `apiKey=-` ＋ `printf '%s'`（vault 1.16 の kv-builder は stdin を末尾改行ごと読むので here-string は不可）。E1 と同じ別 issue へ束ねる。
- **E3**: kubectl / ホスト vault CLI の引数という**別の経路**（`--from-literal` → `--from-file` か `-f -` の manifest、`oidc_client_secret=@file` 等）であり、
  bootstrap.sh の `sh -c` とは直し方も試験の置き場所も異なる。別 issue とする。
- **外す条件**: 上記の別 issue が是正されたら本表の除外は解消する。別 issue の起票は本 PR の報告で依頼する。

### 規則 10（この変更で新たに誤りになる記述）

- `docs/operations/keycloak-smtp-relay-setup-runbook.md` §1（env 由来 or 空既定で seed）・`deploy/local/vault/eso/README.md:97`・
  `deploy/local/README.md:216` は「env で値を渡すと seed される」と書くだけで、作成の内部形には触れない —— **不変で正しい**。
- bootstrap.sh 冒頭の用法コメント・末尾の案内（`#1477: … 無いときだけ作った（在るものは env が空でないキーだけ差し替えた）`）も不変で正しい。
- `SecretItemBootstrapSeedTests.Kv_puts_on_screen_written_paths_are_create_only_and_guarded` の陽性対照（4 KV の put を見つける）は、
  put 文が残るので不変（本環境に dotnet が無いため字面を手で突き合わせた: put は `else` 側・`-cas=0` を持ち、間に `fi` は無い）。

## 設計

- wikijs-sync: `vexec "vault kv put -cas=0 secret/msp/wikijs-sync apiKey=''"` → `mark_changed` → `vkv_patch_nonempty msp/wikijs-sync apiKey "${WIKIJS_SYNC_APIKEY:-}"`。
- keycloak-smtp: put から from/user/password の値を外して `''` にし、直後に 3 つを `vkv_patch_nonempty`。
- `vault kv patch -method=patch … key=-`（stdin）は既存の `vkv_patch_nonempty` が vault 1.16（`deploy/local/vault/vault-dev.yaml`）で使っている形であり、新しい CLI 形は増やさない。
- 挙動差: 作成直後の patch が失敗した場合は WARN して続行し、そのプロパティは空のまま残る（#1764 と同じ受容。画面・Runbook で直す）。
  従来は 1 回の put で入った。SC-22 の項目（画面で直せる）なので受容する。

## 試験

`scripts/scripts.repo.test.js` の #1728 の kubectl 記録スタブ（`$STUB_LOG.argv` が `sh -c` の引数を控える）に 3 件を足す:
AC1・AC2（目印の値が argv・kubectl 引数・出力に無い／作成の put は空／書き込み順）・AC3（env 無しは put だけ）。
目印は `dummy-value-1767-*` を実行時に組み立てる（gitleaks 対策）。

## 検証の記録

- 変異 1: bootstrap.sh を `origin/develop` 版へ戻す → `#1767: wikijs-sync …` が「秘密値が sh -c の引数に載った」で落ちる。
- 変異 2: keycloak-smtp の put だけ旧形へ戻す → `#1767: keycloak-smtp …` が同じ表明で落ちる。
- 是正後: `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 緑。`bash -n` 緑（shellcheck は本環境に無い）。
