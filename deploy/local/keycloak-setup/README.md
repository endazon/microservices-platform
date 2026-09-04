# Keycloak realm の追随（FR-05 / NFR-09 / ADR-0004・ADR-0026・IADR-0368・#1088 / #324）

> 起点: [IADR-0368](../../../.ai-context/adr/IADR-0368_persist-by-default-and-realm-reconcile-job.md) /
> 作業仕様書 [`.ai-context/specs/20260904_issue-1088_persist-by-default-and-realm-reconcile.md`](../../../.ai-context/specs/20260904_issue-1088_persist-by-default-and-realm-reconcile.md)

`reconcile-realm.sh` は **realm JSON（宣言）と稼働 realm の差分を Admin REST API で当てる**冪等な runtime 後追いである。
`scripts/k8s-local-up.sh` が**既定で**（[7/7] の後に）呼ぶ。

```sh
bash deploy/local/keycloak-setup/reconcile-realm.sh           # 差分を当てる（apply）。単独でも何度でも実行できる
bash deploy/local/keycloak-setup/reconcile-realm.sh --check   # 差分を数えるだけ（書き換えない）。1 件でも在れば exit 1
```

## なぜ要るか —— `--import-realm` は同名 realm が在ると黙って飛ばす

Keycloak は `start-dev --import-realm` で立つ。**同名 realm が既に在ると import は飛ばされる**（`IGNORE_EXISTING`。
エラーも警告も出ない）。永続化が既定（IADR-0368 決定 1）になり realm が PVC に残るようになった今、
**`deploy/keycloak/microservices-platform-realm.json` を直しても稼働 realm は変わらない**のが通常状態である。
かといって `OVERWRITE_EXISTING` は realm を丸ごと作り直し、永続化で守りたい runtime state（TOTP 資格情報・
追加利用者・セッション）を毎回消す。両立する形が「静的 import（空 PVC のときだけ）＋ 起動後に差分を当てる」である。

## 何をするか

| # | 段 | 中身 |
| --- | --- | --- |
| 0 | 前提 | `platform-infra/keycloak` Deployment の存在を確かめる（無ければ apply は何もしない。`--check` は失敗） |
| 1 | スクリプト本体 | `reconcile-realm.js` を ConfigMap `keycloak-realm-reconcile` にする（毎回上書き＝リポジトリの版が正） |
| 2 | Job | 前回の Job を消し、`realm-reconcile-job.yaml` を apply する（Job は immutable）。`--check` は Job 名と `RECONCILE_MODE` を差し替える |
| 3 | 完了待ち | `Complete` / `Failed` のどちらかが立つまで見る（既定 300 秒。`RECONCILE_JOB_TIMEOUT`） |
| 4 | ログ | Job のログを出す。**値（secret・パスワード）は出さない**（計画は操作の種類と対象と理由だけを出す） |

Job（`node:22-alpine`・`platform-infra`）は **ConfigMap `keycloak-realms`**（起動器 [3/7] が実 realm ファイルから作る＝
単一情報源。AST realm も同梱）を `/import` に、Secret `keycloak-admin`（`username` / `password`）を env に読み、
`http://keycloak:8080` を叩く。**エッジ・TLS・メッシュのどれにも依存しない。**

🔴 **Keycloak pod で `kcadm.sh` を exec しない。** 別 JVM がコンテナのメモリ制限を食い潰し、本体が OOMKilled で
再起動する（2026-09-02 実測）。旧 `reconcile-backchannel-logout.sh` はその経路だったので撤去した。

## 境界 —— 宣言が所有する層と、実行時が所有する層

| 層 | 対象 | 扱い |
| --- | --- | --- |
| 宣言（realm JSON が正） | realm の非コレクション設定 / `requiredActions` / realm・client ロール / グループ / client scopes ＋ mappers / clients（属性・redirect・secret・scope 割当 ＋ mappers）/ **seed 利用者の存在** / サービスアカウント利用者のロールと属性 | 差分があれば当てる。集合欄は宣言が全集合、実体は加算的（余剰は消さない） |
| 実行時（Keycloak / SC-17 / 本人が正） | 既存の人間の利用者の資格情報・属性・ロール・グループ・`requiredActions`・セッション / `smtpServer` | **触らない** |

seed 利用者の宣言（例: `requiredActions`）を変えて既存クラスタへ届けたいときは、破壊経路を使う:
`kubectl -n platform-infra delete pvc keycloak-data && kubectl -n platform-infra rollout restart deploy/keycloak`
（空 PVC へ再 import。実行時状態は失われる）。

## 収束と門

`apply` は「計画 → 適用 → 再計画」を最大 3 周し、最後の計画が 0 件でなければ非 0 で終える。`--check` は計画だけ。
標準出力の最終行 `realms=<n> drift=<m> applied=<k>` を **`scripts/check-stack-ready.js` の G9** が読む（fail-closed）。
up.sh からの呼び出しは best-effort（WARN）で、門は G9 が持つ。

計画器（`plan(desired, live)`）は純粋関数で、`node scripts/keycloak-realm-reconcile.test.js` が
「一致なら 0 件」「差分の種類ごとに 1 件」「実行時層には触れない」「前提が無い操作は deferred として残る」を固定する。
