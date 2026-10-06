---
title: 運用 Runbook — 経路B の LLM ゲートウェイへ埋め込み（Voyage AI）の鍵を入れ、値を表示せずに届いたことを確かめる
type: runbook
status: draft
author: claude
created: 2026-10-06
updated: 2026-10-06
---
<!-- trace:
ids: [FR-02, FR-03, SC-22, NFR-18]
adrs: [ADR-0016, ADR-0095, ADR-0127]
iadrs: [IADR-0504, IADR-0096, IADR-0103, IADR-0456, IADR-0494, IADR-0497]
specs: [20261006_1764_voyage-key-wiring]
issues: [#1764, #1762, #1740, #1696]
-->

# 運用 Runbook: 経路B の LLM ゲートウェイへ埋め込み（Voyage AI）の鍵を入れる

> **運用仕様書（[`operations.md`](operations.md)）の「埋め込みプロバイダの設定・ゼロ保持・再索引」の下位にあたる手順書である。**
> 再索引（`DocumentUpdated` の再発行）の**前提**であり、再索引の手順は同節にある。
>
> 🔴 **鍵の値はどこにも表示しない。** 本書が表示するのは長さ・キー名・HTTP の状態コード・トークン数だけである。
> 値をコマンドラインの引数に書かない（シェル履歴・`ps`・スクロールバックに残る）。

## 位置づけと前提の受け入れ

- 公開・社内（`public` / `internal`）の文書と検索クエリの埋め込みは、既定でティアB（Voyage AI・`voyage-3.5`・1024 次元）へ送る。
  LLM ゲートウェイがこの鍵（環境変数 `Embedding__Voyage__ApiKey`）を持たないと、埋め込みは失敗し、文書は索引に入らない。
- チャートは鍵を Secret `llm-provider-credentials` のキー `voyage-api-key` から渡す（`optional: true`）。
  Secret の中身は Vault の KV `secret/msp/llm-provider-credentials` のプロパティ `voyage-api-key` を ESO（外部シークレット同期）が写したものである。
- 🔴 **Voyage AI のゼロ保持（学習利用のオプトアウト）の認定は未了である**（#1740）。オーナーは 2026-10-06、認定の前に経路B で鍵を使うことを受け入れた。
  本番相当の環境へは、認定が済むまで本書を当てない（運用仕様書の同節の「未認定の環境へデプロイする場合」に従う）。
- 高機密文書（`confidential` / `restricted` / 機密区分が未指定・未知）は鍵の有無に依らず埋め込まれない（語彙索引にだけ載る）。本書はそれを変えない。

## 鍵の有無による挙動

| 状態 | LLM ゲートウェイの起動 | 埋め込みの応答 | 取り込み（`public` / `internal`） | ゲートウェイのログ |
| --- | --- | --- | --- | --- |
| Secret にキーが無い（古い Secret） | **起動する**（env を置かない） | `embedded=false`・`retryable=true` | 再試行の後 DLQ。索引に入らない | `Embedding call failed at endpoint voyage-managed` ＋ `Voyage AI の API キーが未設定です` |
| キーは在るが値が空（Vault の種の既定） | 起動する（env は空） | 同上 | 同上 | 同上 |
| 値が誤り・失効 | 起動する | 同上（上流が 401） | 同上 | `Embedding call failed ...` ＋ `401 (Unauthorized)` |
| 値が正しい | 起動する | `embedded=true`・1024 次元 | `knowledge_chunks_voyage_3_5` に点が入る | 失敗の行が出ない |

- 起動時に鍵の有無を検査しない（鍵が無くてもテキスト生成は使える。キーを必須にすると Secret が古いだけで LLM ゲートウェイ全体が `CreateContainerConfigError` で止まる）。
- 鍵は**プロセスの起動時に読む**。Secret を差し替えた後は Pod の作り直しが要る（経路B は Reloader が `llm-provider-credentials` の変更で作り直す）。

## 費用

- 課金は Voyage の契約に従う（計画が比較した時点の目安は `voyage-3.5` で約 0.06 USD / 100 万トークン。**正は契約の価格表**）。埋め込み 1 回 ＝ 1 チャンク（本文の無い文書は題名などで 1 回）。費用の母数は**再索引で埋め込みへ進む文書（`public` + `internal`）のチャンク数 × 平均トークン数**である。
  件数は再索引の駆動スクリプトの dry-run が `public` / `internal` の内訳で出す。単価は契約の価格表で確かめ、流す前に見積もる。
- 本書の確認（手順 5）は短い文字列を 1 回だけ送る（数トークン）。検索クエリも 1 回ごとに埋め込みを 1 回呼ぶ。

## 前提

| 項目 | 内容 |
| --- | --- |
| 環境 | `VAULT=1 ESO=1 bash scripts/k8s-local-up.sh --live` で立てた経路B（ESO と Reloader が入っている）。Vault は既定の永続化 |
| チェックアウト | 本書の変更（ExternalSecret のキー・values-local の env・Vault の種）を含む `develop` を手元に取っている |
| クラスタの権限 | `platform-infra` の `exec deploy/vault`、`microservices-platform` の ExternalSecret の `patch`（`annotate`）・Secret / Deployment の読み取り・Pod の作成と削除（手順 5）・`exec`（手順 4） |
| 必要なツール | `kubectl`・`base64`・`wc`・`date`。**ホストに `vault` CLI は不要**（Vault Pod の中で実行する） |
| 時間帯 | LLM ゲートウェイが作り直される（RollingUpdate）。株式自動売買の定時サイクルの合間に行う |

## 手順

### 0. 今の状態を控える（読み取りだけ）

```sh
NS=microservices-platform
# 同期が Ready か
kubectl -n "$NS" get externalsecret llm-provider-credentials \
  -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}{"\n"}'
# Secret のキー名だけ（値は出さない）。voyage-api-key がまだ無ければ未移行
kubectl -n "$NS" get secret llm-provider-credentials -o go-template='{{range $k,$v := .data}}{{$k}}{{"\n"}}{{end}}'
# LLM ゲートウェイの env の名前だけ。決定的ローカル埋め込み（Endpoints__2__Enabled）が在るなら、Voyage は使われない
kubectl -n "$NS" get deploy llmgateway-service \
  -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}{"\n"}{end}' | grep -E 'Voyage|Endpoints__[0-9]__Enabled'
```

`Embedding__Routing__Endpoints__2__Enabled` が出るクラスタは `LOCALEMBED=1` で立てた使い捨てのスタックである。手順 2 の再実行で `LOCALEMBED=1` を付けなければ外れる
（外すと取り込みと検索の両方が Voyage へ寄る。決定的ローカル埋め込みのコレクションは読まれなくなる）。

### 1. 鍵を Vault へ入れる（値を表示しない）

**1-a. 画面から（既定の面）**: `/admin/secrets`（platform-admin / platform-operator）で項目 `llm-provider-credentials` の「更新」→ プロパティ `voyage-api-key`。
プロパティの選択肢に `voyage-api-key` が無いなら、動いている BFF のイメージが本書の変更（秘密情報の項目表〔`deploy/bootstrap/sc22-secret-items.json`〕に `voyage-api-key` を足した）を
まだ含んでいない。そのときは 1-b を使う。画面から書くと、同期の依頼（`force-sync`）と LLM ゲートウェイの作り直し（Reloader）が自動で行われる。
期待長（手順 3・4 で突き合わせる）は、入れる値の長さを手元で測って控える（`read -rs` で読み、`printf '%s' "$V" | wc -c`、`unset V`）。

**1-b. コンソールから（画面が使えないとき）**:

```bash
# -s = 画面へ表示しない。read はシェル履歴に値を残さない（bash で実行する）
read -rs -p "voyage api key: " SECRET_VALUE && echo
# 手順 3 で突き合わせる期待長（printf '%s'。echo は改行を足して 1 多くなる）
EXPECTED_LEN="$(printf '%s' "$SECRET_VALUE" | wc -c | tr -d ' ')"; echo "expected length: $EXPECTED_LEN"
printf '%s' "$SECRET_VALUE" | kubectl -n platform-infra exec -i deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv patch -method=patch secret/msp/llm-provider-credentials voyage-api-key=-
'
unset SECRET_VALUE
```

- **パス `secret/msp/llm-provider-credentials`・プロパティ `voyage-api-key`** に書く。`anthropic-api-key` / `openai-api-key` と同じ KV に同居する。
- 🔴 **`vault kv put` を使わない**（KV 全体を置き換え、同居する鍵が消える）。`patch` は指定したプロパティだけを書く。
- `patch` が `no value found` で失敗したら KV そのものが無い。**ここで止める**（Vault の中身が消えている。立ち上げ直しは `k8s-local-up.sh` の通常経路）。
- 記録（1-b のとき）: 秘密情報の運用の記録先（[`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md) の手順 5）へ、日時・実施者・
  `msp/llm-provider-credentials` の `voyage-api-key`・コンソールを使った理由を書く。**値は書かない。**

### 2. 配線を当てる（ExternalSecret のキーと LLM ゲートウェイの env）

クラスタを立てたときと**同じ opt-in の環境変数**（`ISTIO` 等。`LOCALEMBED=1` は付けない）で起動器を再実行する。冪等である。

```sh
VAULT=1 ESO=1 bash scripts/k8s-local-up.sh --live
```

- Vault の種（`deploy/local/vault/eso/bootstrap.sh`）は在る値を書き換えない（`VOYAGE_API_KEY` を env で渡す必要は無い）。手順 1 を飛ばした場合は `voyage-api-key` を空で足す
  （ESO はプロパティが無いと同期全体を失敗させるため。空のままなら鍵なしの挙動になる）。
- 新しい ExternalSecret（キー `voyage-api-key` を足したもの）を当て、同期を待ち、`helm upgrade` で env を足し、LLM ゲートウェイを作り直す。
- 🔴 `helm upgrade` を手で打たない。起動器はメッシュの宣言・values-local・opt-in の引数を組み立てて当てており、手で打つとそれを取りこぼす。

### 3. Secret に届いたことを長さで確かめる

```sh
NS=microservices-platform
kubectl -n "$NS" annotate externalsecret llm-provider-credentials force-sync="$(date +%s)" --overwrite
kubectl -n "$NS" get externalsecret llm-provider-credentials \
  -o jsonpath='{.metadata.annotations.force-sync}|{.status.refreshTime}|{.status.conditions[?(@.type=="Ready")].status}{"\n"}'
# 長さだけ（jsonpath の後ろに改行を足さない）
kubectl -n "$NS" get secret llm-provider-credentials -o jsonpath='{.data.voyage-api-key}' | base64 -d | wc -c
```

- `Ready` が `True`、`refreshTime` が注釈の時刻（unix 秒。`date -u -d @<秒>` で直す）以降、長さが手順 1 の `EXPECTED_LEN` と一致すれば成功。
- `Ready` が `False` で理由に `voyage-api-key` が出るなら、KV にプロパティが無い（手順 1 か 2 の種が届いていない）。

### 4. LLM ゲートウェイの Pod に env が在ることを長さで確かめる

```sh
NS=microservices-platform
kubectl -n "$NS" rollout status deploy/llmgateway-service --timeout=180s
# 宣言: Secret の参照であり、リテラルではない
kubectl -n "$NS" get deploy llmgateway-service -o jsonpath='{range .spec.template.spec.containers[0].env[?(@.name=="Embedding__Voyage__ApiKey")]}{.valueFrom.secretKeyRef.name}/{.valueFrom.secretKeyRef.key} optional={.valueFrom.secretKeyRef.optional}{"\n"}{end}'
# 実体: 動いている Pod の env の長さだけ
kubectl -n "$NS" exec deploy/llmgateway-service -c llmgateway-service -- \
  sh -c 'printf %s "${Embedding__Voyage__ApiKey:-}" | wc -c'
```

- 1 行目の期待: `llm-provider-credentials/voyage-api-key optional=true`。
- 2 行目の期待: `EXPECTED_LEN` と同じ数。`0` なら Pod が古い Secret で起動している —— Reloader が作り直していない。
  `kubectl -n microservices-platform rollout restart deploy/llmgateway-service` で作り直し、もう一度測る。

### 5. 埋め込みが通ることを確かめる（最小の送信）

**5-a. 鍵そのもの**（ゲートウェイを通さない。同じ Secret を読む使い捨ての Pod から 1 回だけ送る。値は表示しない）:

```sh
NS=microservices-platform
kubectl -n "$NS" apply -f - <<'EOF'
apiVersion: v1
kind: Pod
metadata:
  name: voyage-key-probe
  annotations:
    sidecar.istio.io/inject: "false"
spec:
  restartPolicy: Never
  containers:
    - name: probe
      image: curlimages/curl:8.10.1
      env:
        - name: VOYAGE_KEY
          valueFrom:
            secretKeyRef: { name: llm-provider-credentials, key: voyage-api-key }
      command: ["sh", "-c"]
      args:
        - >-
          printf 'header = "Authorization: Bearer %s"\n' "$VOYAGE_KEY"
          | curl -sS -K - -o /tmp/r -w 'http=%{http_code}\n' https://api.voyageai.com/v1/embeddings
          -H 'content-type: application/json'
          -d '{"input":["probe"],"model":"voyage-3.5","output_dimension":1024,"input_type":"query"}';
          grep -o '"total_tokens":[0-9]*' /tmp/r || true
EOF
kubectl -n "$NS" wait --for=jsonpath='{.status.phase}'=Succeeded pod/voyage-key-probe --timeout=90s
kubectl -n "$NS" logs voyage-key-probe
kubectl -n "$NS" delete pod voyage-key-probe
```

期待: `http=200` と `"total_tokens":<小さな数>`。`http=401` は鍵の誤り、`http=000` はクラスタから外へ出られない（egress）。
応答本文（ベクトル）は Pod の中にだけ置き、表示しない。

- 🔴 鍵は `curl` の引数に書かない（`-H "Authorization: Bearer ..."` と書くと、展開後の値が Pod 内の `ps` に載る）。
  シェル組み込みの `printf` で設定（`header = ...`）を作り、`curl -K -` へ標準入力で渡す。
- 失敗の応答（`http=401` 等）では `total_tokens` が無いので `grep` は何も出さないが、`|| true` で Pod は `Succeeded` で終わる。
  `kubectl wait` が 90 秒待たされずに、すぐ `logs` で `http=` の行を読める。

**5-b. ゲートウェイの経路**（検索クエリの埋め込み）: 検索画面で無害な語（例「テスト」）を 1 回検索し、直後にゲートウェイのログを数える。

```sh
kubectl -n microservices-platform logs deploy/llmgateway-service -c llmgateway-service --since=5m \
  | grep -c 'Embedding call failed at endpoint voyage-managed'
```

期待: `0`。1 以上なら同じ行の例外（`API キーが未設定` ＝ 手順 4 へ戻る／`401` ＝ 鍵の誤り）を見る。

**5-c. 取り込みの経路**（決定的な確認）: 再索引の手順（運用仕様書の「埋め込みプロバイダの設定・ゼロ保持・再索引」節の `DocumentUpdated` の再発行）の
**カナリア**（最初の 1 ページ）を流し、DLQ が増えず、`knowledge_chunks_voyage_3_5` の `points_count` が 0 から増えることを確かめてから全件へ進む。

## 戻し方（鍵を外す）

- 鍵だけを空に戻す: 手順 1 と同じ形で空を書く（`printf '' | kubectl -n platform-infra exec -i deploy/vault -- sh -c '... vault kv patch -method=patch secret/msp/llm-provider-credentials voyage-api-key=-'`）。
  続けて手順 3 の `annotate` で同期を促す（Reloader が作り直す）。以後は鍵なしの挙動（上の表）に戻る。
- Voyage の経路そのものを止める: 運用仕様書の同節のとおり `Embedding__Routing__Endpoints__0__Enabled=false`（取り込みと検索クエリの両方の埋め込みが止まる）。

## うまくいかないとき

| 症状 | 原因の候補 | 対処 |
| --- | --- | --- |
| ExternalSecret が `SecretSyncedError`（`voyage-api-key` が無い） | KV に `voyage-api-key` が無いまま新しい ExternalSecret を当てた | 手順 1 を行う（または起動器を再実行して種に空で足させる）。既存の Secret は古いまま残るので、テキスト生成は止まらない |
| 手順 4 の長さが 0 | Pod が同期より前の Secret で起動した | `rollout restart` で作り直す |
| 5-a が `http=401` | 鍵の誤り・失効・別の組織の鍵 | 手順 1 から入れ直す |
| 5-a が `http=000` | クラスタから外へ出られない | ノードの外向き通信（プロキシ・ファイアウォール）を確かめる |
| 5-b が 0 なのに 5-c で点が増えない | 文書が高機密（語彙索引へ行く）／本文の所在が無い／`LOCALEMBED=1` のまま | dry-run の内訳と手順 0 の env を見直す |
