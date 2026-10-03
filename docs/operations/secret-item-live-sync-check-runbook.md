---
title: 運用 Runbook — 稼働クラスタで画面から 1 プロパティを書き、同期後の Secret を長さだけで確かめる（T-40）
type: runbook
status: draft
author: claude
created: 2026-10-04
updated: 2026-10-04
---
<!-- trace:
ids: [SC-22, FR-05, NFR-18]
adrs: [ADR-0095, ADR-0104, ADR-0110]
iadrs: [IADR-0103, IADR-0453, IADR-0454, IADR-0456, IADR-0460, IADR-0494]
specs: [20261004_issue-1472_sc22-t40-live-procedure, 20260915_issue-1467_sc22-audit-followups, 20260915_issue-1477_screen-only-poc-setup, 20260925_1502_sc22-supply-source-and-restart-notice, 20260928_issue-1682_paired-secrets-outside-sc22, 20261003_1728_eso-force-sync-after-bootstrap]
issues: [#1472, #1467, #1477, #1502, #1682, #1728]
-->

# 運用 Runbook: 稼働クラスタで画面から 1 プロパティを書き、同期後の Secret を長さだけで確かめる

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 本書は[秘密情報・接続設定の管理のテスト仕様書](../tests/SC-22_secret-item-management.md)の手動の項目 **T-40** を、
> 稼働クラスタで 1 回の作業として実施するための手順である。自動試験は保管先と Kubernetes API の**偽物**に対する固定であり、
> **「緑である」ことは「稼働クラスタで書けて、同期され、消費側に届く」ことを意味しない。** それを確かめるのが本書である。
>
> 🔴 **値はどこにも表示しない。Secret 側は長さとキー名だけを見る。** 本書で表示してよいのは、使い捨ての試験値（秘密ではない）だけである。

## この手順を実行する条件（いつ走らせるか）

- 保管先（Vault）と外部シークレット同期（ESO）を入れて、クラスタを**新しく立ち上げた**とき（`VAULT=1 ESO=1`）。
  T-40 は未実施であり、次にこの構成を立てた場で 1 回行う。
- 画面・境界層・同期依頼・Reloader・保管先の権限のいずれかを変えた後、稼働での成立を確かめ直したいとき。

**実行してはいけない場合**:

- 🔴 本番相当の環境（Reloader が入っていない）。
- 🔴 LLM を呼ぶ処理が走っている最中（利用者の LLM 機能の利用、株式自動売買の定時サイクルが LlmGateway を呼んでいる時間帯）。
  試験の書き込みと戻しで llmgateway-service が 2 回作り直される。作り直しは RollingUpdate なので新しい Pod が Ready になってから古い Pod が消え、
  ほとんど止まらないが、古い Pod が終わる瞬間に処理中の要求は切れ得る。定時サイクルの合間に行う。
- 🔴 任意の節 B（OpenD の鍵の生成）を、その節の実施条件（市場が閉まっている・自動売買を止めている・未約定の注文が無い）を満たさずに行うこと。
- 下の「前提の確認」のどれかが満たせないとき。**前提を本書の中で直さない**（立ち上げ直しは `scripts/k8s-local-up.sh` の通常経路）。

## 同じ場で確かめること（本書が 1 回で拾う項目）

| # | 確かめること | 本書の節 |
| --- | --- | --- |
| 1 | 実 Vault へ画面から書ける（境界層の権限・保管先のログイン） | 手順 3 |
| 2 | 書き込みの後、数秒で同期が終わる（境界層が付ける即時同期の注釈が効く） | 手順 4 |
| 3 | 同期後の Secret の、書いたプロパティの長さが試験値と一致し、同居するキーが減っていない | 手順 5 |
| 4 | Reloader が消費側を作り直す | 手順 6 |
| 5 | 書き込み応答の作成時刻と、保管先の metadata の版の作成時刻が一致する（一致しないと「最終更新者」が常に「記録なし」になる） | 手順 3・7 |
| 6 | 元の値へ戻せる | 手順 7 |
| 7 | （任意・株式自動売買を配備している場合）供給元の表示が配備に合う／生成した鍵を OpenD が読める | 任意の節 A・B |

## 前提

| 項目 | 内容 |
| --- | --- |
| 環境 | `VAULT=1 ESO=1 bash scripts/k8s-local-up.sh --live` で立てたクラスタ（ESO=1 は VAULT=1 が無いと止まる）。Vault は既定の永続化（`PERSIST=0` でない） |
| 画面に入れる利用者 | ロール **platform-admin** か **platform-operator** を持つ利用者（画面 `/admin/secrets`。左ナビ「運用」→「秘密情報・接続設定の管理」）。他のロールでは画面が「見つかりません」になる |
| クラスタの権限 | `kubectl` で `platform-infra`（`exec deploy/vault`）・`microservices-platform`（ExternalSecret・Secret・Deployment の読み取りと、**ExternalSecret の `patch`**＝手順 3・7 の `annotate`）・`reloader`（ログの読み取り）・**全名前空間の Deployment / StatefulSet / DaemonSet / CronJob の `list`**（手順 0-5）・`clustersecretstore` の読み取り |
| 必要なツール | `kubectl`・`base64`・`wc`・`openssl`（試験値の生成）・GNU coreutils の `date`（手順 4 で unix 秒を時刻へ直す `date -u -d @<秒>`。macOS 標準の BSD 版は `date -u -r <秒>` に読み替える）・ブラウザ（開発者ツールのネットワーク表示を使う）。**ホストに `vault` CLI は不要**（Vault Pod 内で実行する） |
| 所要時間の目安 | 30〜45 分（記録を含む。任意の節を除く） |

## 試験対象（なぜこのプロパティか）

**既定の試験対象は、項目 `llm-provider-credentials` のプロパティ `openai-api-key`** である。

| 条件 | この対象での実際 |
| --- | --- |
| 書き換えても機能が変わらない | 読み手が無い。llmgateway-service が読むのは同じ Secret の `anthropic-api-key` だけ（チャートの `Llm__ApiKey`） |
| 同居するキーがある | Secret `llm-provider-credentials` は `anthropic-api-key` と `openai-api-key` の 2 キー（`deploy/local/vault/eso/externalsecret-llm.yaml`） |
| 消費側の作り直しを観測できる | llmgateway-service に Reloader の注釈 `secret.reloader.stakater.com/reload: "llm-provider-credentials"` がある（読まないキーでも Secret の中身が変われば作り直される） |
| 即時同期の依頼が届く | 同期先の ExternalSecret `microservices-platform/llm-provider-credentials` が境界層の同期依頼の Role の対象に入っている |
| 対になる秘密でない | 認証基盤・データストア・サービス間の資格情報に触れない（それらは画面の対象外。回し方は [`paired-secret-rotation-runbook.md`](paired-secret-rotation-runbook.md)） |
| 戻し方がある | 保管先の版を戻す（`vault kv rollback`）。元の値を誰も見ないまま戻せる。元が空でも戻せる（画面は空の値を書けない） |

🔴 **別の項目に替えない。** 替えるなら、上の 6 条件を同じ表で確かめ、記録に理由を書く。
`keycloak-smtp`（送信が壊れ得る）・`wikijs-sync`（Wiki 同期が止まる）・`ast-app-secrets`（対になる秘密が同居する）・moomoo 系（OpenD の再認証が要る）は既定の対象にしない。

## 手順

以下、次の変数をシェルに置く（名前だけ。値ではない）。

```bash
NS=microservices-platform
ES=llm-provider-credentials        # ExternalSecret 名
SECRET=llm-provider-credentials    # 同期先 Secret 名
PROP=openai-api-key                # 書くプロパティ
VPATH=secret/msp/llm-provider-credentials
```

### 0. 前提の確認（1 つでも満たさなければ止める）

```bash
# 0-1 Vault が unseal されている（Sealed false）
kubectl -n platform-infra exec deploy/vault -- sh -c 'VAULT_ADDR=http://127.0.0.1:8200 vault status' | grep -E '^Sealed'

# 0-2 ストアと同期先が Ready
kubectl get clustersecretstore vault-backend \
  -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}{"\n"}'
kubectl -n "$NS" get externalsecret "$ES" \
  -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}{"\n"}'

# 0-3 Reloader が居る（名前を控える。手順 6 で使う）
kubectl -n reloader get deploy -o name

# 0-4 境界層の同期依頼の Role がある
kubectl -n "$NS" get role bff-externalsecret-sync

# 0-5 試験対象に読み手が無い。Secret を参照するワークロードを全名前空間で引き、参照しているキー名だけを並べる
#     （ワークロードの定義は Secret の参照だけで値を含まない。全体を YAML で落とさず、参照の欄だけを jsonpath で引く）
kubectl get deploy,sts,ds,cronjob -A -o jsonpath='{range .items[*]}{.kind}/{.metadata.namespace}/{.metadata.name}{" "}{..secretKeyRef.name}:{..secretKeyRef.key}{" "}{..secretRef.name}{"\n"}{end}' \
  | grep -- "$SECRET"
```

期待: 0-1 は `Sealed false`、0-2 は 2 行とも `True`、0-3 は 1 行以上、0-4 は見つかる。
0-5 は `Deployment/microservices-platform/llmgateway-service` の 1 行だけで、その行に `openai-api-key` が出ないこと
（行には、そのワークロードが参照する Secret の名前の列とキーの列が `:` の前後に並ぶ。対応は崩れるが、キー名 `openai-api-key` の有無は読める）。
`openai-api-key` が出る、2 行目のワークロードがある、`:` の後ろ〔`secretRef`＝Secret 全体を env に読む形〕に `llm-provider-credentials` が出る、
のいずれかなら試験対象を替える（上の「試験対象」の表を引き直す）。

続けて画面 `/admin/secrets` を開き、`llm-provider-credentials` の行が **状態「設定済み」・供給元「画面」** であることを見る。
「接続できません」なら保管先か権限が配備されていない —— 止める。

🔴 **作業中に `scripts/k8s-local-up.sh`（＝ `bootstrap.sh`）を `OPENAI_API_KEY` を付けて走らせない。** 在る KV でも env が空でないプロパティは差し替わり、試験の前後比較が崩れる。

### 1. 書く前の状態を控える（値は見ない）

```bash
# 1-1 現在の版（metadata だけ。値は出ない）。"Current Version"（current_version）を控える ＝ 版 B
kubectl -n platform-infra exec deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv metadata get '"$VPATH"'
'

# 1-2 Secret のキー名と数（値は読まない）
kubectl -n "$NS" get secret "$SECRET" -o go-template='{{range $k, $v := .data}}{{$k}}{{"\n"}}{{end}}'
kubectl -n "$NS" get secret "$SECRET" -o go-template='{{range $k, $v := .data}}{{$k}}{{"\n"}}{{end}}' | wc -l

# 1-3 書くプロパティの今の長さ（バイト数）＝ 長さ L0
kubectl -n "$NS" get secret "$SECRET" -o jsonpath="{.data.$PROP}" | base64 -d | wc -c

# 1-4 同期の時刻と注釈（参考。同期の完了の基準は手順 3 で取り直す）・消費側の世代と Pod
kubectl -n "$NS" get externalsecret "$ES" \
  -o jsonpath='{.metadata.annotations.force-sync}|{.status.refreshTime}{"\n"}'
kubectl -n "$NS" get deploy llmgateway-service -o jsonpath='{.metadata.generation}{"\n"}'
kubectl -n "$NS" get pods -l app=llmgateway-service -o name
```

- 1-1 で現在版の削除・破棄が出ている（`deletion_time` が埋まっている・`destroyed true`）なら止める（戻し先が無い）。
- 1-2 は `anthropic-api-key` と `openai-api-key` の 2 行、数は `2` のはずである。違えば数をそのまま控える（減ったかどうかを手順 5 で前後比較する）。
- 長さは**記録には書かない**。手順 5 の比較と手順 7 の戻しの確認にだけ使う。

🔴 **長さの測り方の約束**: `jsonpath` の後ろに `{"\n"}` を足さない（base64 の後ろに改行が入る）。`base64 -d` は Secret のバイト列をそのまま出し、`wc -c` はバイト数を数える。
キーが無いときも空のときも `0` になるので、**キー名の一覧（1-2）と組で見る**。

### 2. 試験値を作る（使い捨て。秘密ではない）

```bash
PROBE="$(openssl rand -hex 20)"
printf '%s' "$PROBE" | wc -c     # 期待長 ＝ 40（echo ではなく printf '%s'。echo は改行を足して 41 になる）
```

- 期待長が手順 1-3 の L0 と**同じなら作り直すか長さを変える**（長さが同じだと「変わった」ことを長さで示せない）。
- 試験値は実在の鍵ではなく、手順 7 で消える。画面へ貼るために表示してよい。**末尾に空白・改行を入れて貼らない**（境界層は値を整形しないので、その分だけ長くなる）。

### 3. 画面から 1 プロパティを書く

1. ブラウザの開発者ツールのネットワーク表示を開く。
2. `/admin/secrets` で `llm-provider-credentials` の「更新」→ プロパティ `openai-api-key` を選ぶ。
3. 値と確認入力に試験値を入れ、理由に「T-40 稼働クラスタの疎通試験（後で戻す）」と書く。
4. 送る。確認ダイアログが出たら（消費側が自動で再起動される旨）読む。**「書き込む」を押す直前に**、別の端末で同期の基準を取り直す
   （手順 1-4 の値は古い。その間に 1 時間ごとの定期同期が挟まると、基準が古いせいで「同期が終わった」と早まって判定する）:
   ```bash
   kubectl -n "$NS" get externalsecret "$ES" -o jsonpath='{.status.refreshTime}{"\n"}'   # ＝ 基準 T0
   ```
   取り直したらすぐ「書き込む」を押す。
5. 画面に**保存の成立と「即時同期を依頼しました」**が出ることを見る。

ネットワーク表示で `PUT /bff/secrets/llm-provider-credentials` の応答を見る（応答に値は無い）。

- ステータス **200**、本文の `version` が **版 B ＋ 1**（＝ 版 W）、`syncRequested` が **true** であること。
- 本文の `updatedAt` を控える（手順 7 で metadata と突き合わせる）。

画面の一覧を読み直し、`llm-provider-credentials` の行の **「最終更新者」が自分の利用者名**になっていることを見る。

> 境界層は、自分が書いた版の記録と保管先の現在版を、**版の番号と作成時刻の両方**で突き合わせて一致したときだけ名前を出す。
> 名前が出れば、書き込み応答の作成時刻と metadata の作成時刻が一致している（確認項目 5 の機能上の証明）。「記録なし」なら食い違っている —— 記録して続ける（誤った名前は出ない設計なので、止める理由にはならない）。

`syncRequested` が `false`（「即時同期を依頼できませんでした」）なら、確認項目 2 は**不合格として記録し**、手で注釈を付けてから手順 4 へ進む:

```bash
kubectl -n "$NS" annotate externalsecret "$ES" force-sync="$(date +%s)" --overwrite
```

### 4. 同期の完了を待つ

```bash
for i in $(seq 1 60); do
  kubectl -n "$NS" get externalsecret "$ES" \
    -o jsonpath='{.metadata.annotations.force-sync}|{.status.refreshTime}|{.status.conditions[?(@.type=="Ready")].status}{"\n"}'
  sleep 2
done
```

- **完了** ＝ 次の 3 つがそろったとき（起動器の force-sync の完了条件に、注釈との前後を足したもの）。そろったら Ctrl-C で抜ける。
  - `refreshTime` が手順 3 の基準 T0 と**違う値**に変わった。
  - `refreshTime` が `force-sync` の注釈の時刻（unix 秒）**以降**である（`date -u -d @<注釈の値> +%Y-%m-%dT%H:%M:%SZ` で同じ書式に直して比べる。BSD 版では `date -u -r <注釈の値> +%Y-%m-%dT%H:%M:%SZ`。注釈より前なら定期同期であり、まだ完了ではない）。
  - `Ready` が `True`。
- 所要時間 ＝ `refreshTime` − `force-sync` の注釈（境界層が付けた unix 秒）。注釈が手順 1-4 から変わっていることも見る（境界層の依頼が届いた証拠）。
- 合否: **10 秒以内**なら設計どおり。10 秒を超えて 120 秒以内に終われば合格だが逸脱として記録する。**120 秒たっても `refreshTime` が変わらなければ不合格** —— 止める条件へ。

### 5. Secret を長さとキー名で確かめる

```bash
# 5-1 書いたプロパティの長さ（手順 2 の期待長と一致すること）
kubectl -n "$NS" get secret "$SECRET" -o jsonpath="{.data.$PROP}" | base64 -d | wc -c

# 5-2 キー名と数（手順 1-2 と同じであること）
kubectl -n "$NS" get secret "$SECRET" -o go-template='{{range $k, $v := .data}}{{$k}}{{"\n"}}{{end}}'
kubectl -n "$NS" get secret "$SECRET" -o go-template='{{range $k, $v := .data}}{{$k}}{{"\n"}}{{end}}' | wc -l
```

- 5-1 が期待長（40）と一致する。🔴 **0 でないことだけでは足りない**（空でも同期は `Ready` になる）。
- 5-2 のキー名と数が手順 1-2 と同じ（`anthropic-api-key` が残っている）。🔴 **減っていたら止める**（部分更新でなく全置換で書かれた疑い。手順 7 で直ちに戻す）。

### 6. Reloader の作り直しを確かめる

```bash
kubectl -n "$NS" get deploy llmgateway-service -o jsonpath='{.metadata.generation}{"\n"}'
kubectl -n "$NS" rollout status deploy/llmgateway-service --timeout=180s
kubectl -n "$NS" get pods -l app=llmgateway-service -o name
# 0-3 で控えた Reloader の Deployment 名を入れる
kubectl -n reloader logs <0-3 の名前> --since=15m | grep llmgateway-service
```

- 世代（`generation`）が手順 1-4 より増え、Pod の名前が入れ替わり、`rollout status` が成功で終わる。
- Reloader のログに llmgateway-service を更新した行がある（行の文言は Reloader の版による。値は出ない）。
- 180 秒で Ready に戻らなければ止める条件へ。

### 7. 元へ戻し、作成時刻を文字列で突き合わせる

戻しは**保管先の版を戻す**（誰も元の値を見ない）。版 B を戻すと、版 B と同じ中身の新しい版（＝ 版 R ＝ 版 W ＋ 1）ができる。

🔴 **`rollback` はプロパティ単位ではなく KV 全体を版 B へ戻す**（同居する `anthropic-api-key` も版 B の値になる）。
作業中に誰か（画面・コンソール・起動器）が `anthropic-api-key` を差し替えていると、その差し替えが**黙って巻き戻り**、LLM の呼び出しが壊れる。
**戻す前に、現在版が自分の書いた版 W のままであることを確かめる。**

```bash
# 7-0 現在版を確かめる（metadata だけ。値は出ない）。current_version が 版 W と一致すること
kubectl -n platform-infra exec deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv metadata get -format=json '"$VPATH"'
' | grep '"current_version"'
```

**一致しなければ戻さずに止める**（止める条件の表）。版 W より新しい版を誰がなぜ書いたかを確かめ、その人と戻し方を決める
（`openai-api-key` だけを元へ戻したいなら、元の値を持っている人が画面から書く。元が空だった場合は画面では戻せないので、試験値のまま残したことを記録する）。

```bash
# 7-1 戻す（7-0 が一致したときだけ）。応答（-format=json）に値は含まれない。data.version（＝ 版 R）と data.created_time を控える
kubectl -n platform-infra exec deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv rollback -format=json -version=<版 B> '"$VPATH"'
'

# 7-2 直後の metadata（値は出ない）。versions の 版 W と 版 R の created_time を見る
kubectl -n platform-infra exec deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv metadata get -format=json '"$VPATH"'
'
```

作成時刻の突き合わせ（確認項目 5）:

- **文字列の一致**: 7-1 の `data.created_time` と 7-2 の `versions` の 版 R の `created_time` が**同じ文字列**であること。
  7-1 の応答に `created_time` が無ければ「確認できない」と記録し、手順 3 の「最終更新者」の結果だけで判定する。
- **画面の書き込み（補助）**: 手順 3 で控えた `updatedAt` と、7-2 の 版 W の `created_time` を**小数 7 桁まで**突き合わせる
  （保管先は小数 9 桁、境界層の応答は最大 7 桁で、**末尾の 0 が削られて 7 桁より短くなり得る**〔例: 7 桁目が 0 なら 6 桁〕。短い側の末尾に 0 を補って 7 桁で比べる。時刻帯の表記は `Z` と `+00:00` の違いがあり得る）。

戻しは画面を通らない書き込みなので、**境界層は同期を依頼しない**。手で促し、戻ったことを長さで確かめる:

```bash
kubectl -n "$NS" get externalsecret "$ES" -o jsonpath='{.status.refreshTime}{"\n"}'   # 基準 T0 を取り直す
kubectl -n "$NS" annotate externalsecret "$ES" force-sync="$(date +%s)" --overwrite
# 手順 4 と同じループで、完了（T0 と違う・注釈の時刻以降・Ready=True）を待ってから:
kubectl -n "$NS" get secret "$SECRET" -o jsonpath="{.data.$PROP}" | base64 -d | wc -c   # 手順 1-3 の L0 に戻ること
kubectl -n "$NS" get secret "$SECRET" -o go-template='{{range $k, $v := .data}}{{$k}}{{"\n"}}{{end}}' | wc -l   # 手順 1-2 と同じ
kubectl -n "$NS" rollout status deploy/llmgateway-service --timeout=180s
unset PROBE
```

画面の一覧では、`llm-provider-credentials` の「最終更新者」が**「記録なし」**に変わる（現在版は画面以外〔root トークン〕が書いた版 R であり、これが正しい）。

## 確認（この手順が成功したと言える条件）

- 手順 3: 応答が 200・版 W ＝ 版 B ＋ 1・`syncRequested: true`・「最終更新者」が自分の名前。
- 手順 4: `refreshTime` が基準 T0 と違い、注釈の時刻以降で、`Ready=True`。所要時間が 120 秒以内（10 秒以内が設計どおり）。
- 手順 5: 書いたプロパティの長さが試験値の長さと一致し、キー名と数が書く前と同じ。
- 手順 6: llmgateway-service の世代が増え、Pod が入れ替わり、Ready に戻る。
- 手順 7: 戻す前の現在版が 版 W だった。作成時刻が文字列で一致（または「確認できない」と記録）。戻した後の長さが書く前と同じで、キー名と数も同じ。

🔴 **「ExternalSecret が Ready」だけでは成功ではない。** 空の値でも同期は成功する。長さとキー名で見る。

## 止める条件

| いつ | 何が起きたら | どうする |
| --- | --- | --- |
| 手順 0 | 前提のどれかが満たせない／画面が「接続できません」／供給元が「画面」でない | 何も書かずに止める。前提を直すのは立ち上げの経路 |
| 手順 1 | 現在版が削除・破棄されている | 止める（戻し先が無い）。復元は [`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md)「現在の版が削除されているとき」 |
| 手順 3 | 応答が 200 でない（403・409・502・503 等） | 書き込みは成立していない。戻す必要は無い。ステータスと画面の表示を記録して止める（T-40 は不合格） |
| 手順 4 | 120 秒たっても `refreshTime` が変わらない／`Ready=False` | `kubectl -n "$NS" describe externalsecret "$ES"` の Events を記録し、**手順 7 で戻して**止める |
| 手順 5 | キーの数が減った・名前が変わった | **直ちに手順 7 で戻し**、戻った後のキー名と数を確かめて止める。不合格として起票する |
| 手順 6 | 180 秒で Ready に戻らない | 手順 7 で戻し、`kubectl -n "$NS" describe deploy llmgateway-service` とログを記録して止める |
| 手順 7 | 7-0 の `current_version` が 版 W でない（誰かが版 W の後に書いた） | **戻さずに止める**（戻すと後から書かれた値を巻き戻す）。誰が書いたかを保管先の audit と画面の「最終更新者」で確かめ、記録する。試験値が残ったまま止まるが、`openai-api-key` に読み手は無い |
| 手順 7 | `rollback` が失敗する | 版 B の状態（`vault kv metadata get`）を記録して止める。`openai-api-key` に読み手は無いので、急いで別の手段で書かない |

## 任意の節（株式自動売買を配備している場でだけ行う）

### A. 供給元の表示が配備に合う

画面の一覧の `ast-app-secrets` の行の供給元と、同期先の ExternalSecret の有無を突き合わせる（書き込みはしない）。

```bash
kubectl -n ai-stock-trading get externalsecret ast-secrets
```

- ExternalSecret が**在る**なら供給元は「画面」、**無い**（NotFound）なら「画面以外」であること。「確認できない」なら境界層の `get` が拒否されている（記録する）。
- もう一方の配備（株式自動売買のチャートの `externalSecrets.enabled` と `externalSecrets.appSecrets.enabled` の切り替え）でも同じことを確かめると、切り替えの両側が揃う。
  🔴 **切り替えは株式自動売買の側の Secret の所有を変える操作であり、本書は手順を持たない。** その配備手順で切り替えられる場でだけ行う。

### B. 生成した鍵を OpenD が読める

🔴 **鍵の生成は戻せない。** 生成すると、OpenD とクライアント（注文執行）が同じ鍵を共有する前提が一度崩れる。

- **食い違いの窓**: 生成が同期されると、Reloader が注文執行（order-execution。株式自動売買のチャートが RSA 鍵の Secret を作り直しの対象にしている）を
  **自動で**作り直して新しい鍵を読ませる。一方 **OpenD は Reloader の対象外で、手で作り直すまで古い鍵のまま**である。
  この間（生成の同期から、OpenD の作り直しと再認証が終わるまで）、注文執行は OpenD と鍵が合わず**発注できない**。
- **OpenD の作り直しで SMS／画像の認証を再び求められ得る。** 認証が通るまで窓は閉じない。

**実施条件（すべて満たすときだけ行う）**:

- 市場が閉まっている時間帯である。
- 自動売買を止めている（キルスイッチ等、株式自動売買の側の手段で発注の経路を止めてある）。
- 未約定の注文が無い。
- OpenD の再認証をその場で通せる人がいる。

満たせなければ本節は行わず、「行わなかった」と記録する。

1. 画面で `ast-moomoo-rsa` の `opend_rsa.pem` を「生成」→ 確認ダイアログを読んで「生成して書き込む」。
2. 同期先 Secret に鍵が入ったことを、値を出さずに確かめる:
   ```bash
   kubectl -n ai-stock-trading get secret moomoo-rsa -o jsonpath='{.data.opend_rsa\.pem}' | base64 -d | grep -c 'BEGIN RSA PRIVATE KEY'   # 1 であること
   ```
3. OpenD は Reloader の対象外なので手で作り直す: `kubectl -n ai-stock-trading rollout restart deploy/opend`。認証を通す。
4. 鍵を読むクライアント（注文執行）が OpenD へ接続できることを、株式自動売買の側の確認手順で見る。確かめたら自動売買の停止を解く（窓が閉じた後に限る）。

生成は戻さない（旧版へ戻しても OpenD が読み込んだ鍵は戻らない）。新しい鍵のまま運用する。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| 画面が「接続できません」（503） | `VAULT=1 ESO=1` でない／境界層の保管先の接続先が未設定 | 立ち上げをやり直す（本書の範囲外） |
| 書き込みが 502 | 保管先が書き込みを拒む（境界層の書き込み権限が未配備・パスが権限に無い） | `deploy/local/vault/eso/policy-bff-secret-write.hcl` が保管先に入っているかを確かめる（`bootstrap.sh` が入れる） |
| `syncRequested: false` | 同期依頼の Role が無い・ExternalSecret が無い・境界層の同期が無効 | 境界層のログで同期の監査行（`secret.item.sync` の `failed` と理由）を見る。値は出ない |
| 同期が終わらない | ストアの認証切れ・パスの綴り | `kubectl describe externalsecret` の Events |
| 長さが 1 多い | 試験値の末尾に改行か空白を入れて貼った | 手順 7 で戻し、貼り方を直して手順 3 からやり直す |
| 「最終更新者」が「記録なし」 | 書き込み応答と metadata の作成時刻が一致しない／境界層の記録の置き場（Redis）に届かない | 手順 7 の文字列の突き合わせの結果と合わせて記録する |

## 記録

**記録先は #1472 へのコメント**とする（T-40 の残作業を持つ issue）。**値・値の長さ・値の一部は書かない**（一致したか否か、キーの数と名前だけを書く）。
戻しは root トークンでの書き込みとして保管先の audit に残るので、戻した時刻と版を書いて audit の行と突き合わせられるようにする
（これは画面が使えないときの退避ではないので、退避手段の記録先には書かない）。

| 欄 | 書くこと |
| --- | --- |
| 実施日時・実施者 | タイムゾーンを添える |
| 環境 | `VAULT=1 ESO=1` の起動・MSP のコミット・helm の revision |
| 試験対象 | `msp/llm-provider-credentials` の `openai-api-key`（替えたなら理由） |
| 版 | 版 B → 版 W（画面）→ 戻す前の現在版（版 W であったか）→ 版 R（戻し） |
| 書き込み | ステータス・`syncRequested`・「最終更新者」が自分の名前だったか |
| 同期 | 所要時間（秒）・`Ready` |
| 長さ | 試験値と一致した／しなかった（数値は書かない） |
| 同居するキー | 前後のキーの数と名前（例: 2 → 2） |
| Reloader | 世代の前後・Pod の入れ替わり・Ready |
| 作成時刻 | 文字列で一致した／しなかった／確認できない。画面の応答との 7 桁の一致 |
| 戻し | 戻した時刻・版 R・戻した後の長さが書く前と一致したか・キーの数 |
| 任意の節 | A・B を行ったか・結果 |
| 判定 | T-40 の合否と、逸脱（同期が 10 秒を超えた等） |

実施した後は、テスト仕様書の T-40 の区分を「手動（実施 YYYY-MM-DD）」へ改め、同じ場で確かめた §未決事項の項を片付ける。

## 限界（この手順で担保できないこと）

- 🔴 **本書は稼働クラスタで実測していない。** 起草した作業はクラスタにも Vault にも触れていない。食い違いが出たら本書を直すこと。
  特に、`vault kv rollback -format=json` の応答に作成時刻が含まれること、Reloader のログの文言、手順 0-5 の複数種別をまとめた `jsonpath` の出力の形は未実測である。
- 試験対象は読み手の無いプロパティである。**「消費側が新しい値で動く」ことは確かめない**（作り直されることまで）。値を読む消費側（`anthropic-api-key` 等）での確認は、実際に鍵を差し替える運用（[`secret-rotation-runbook.md`](secret-rotation-runbook.md)）の場で行う。
- Reloader はローカルの `ESO=1` にしか入っていない。本番像の消費側の作り直しは本書の範囲外である。
- 戻しの書き込みは画面の監査ログに乗らない（保管先の audit に root トークンの行として残る）。人と理由は上の記録で残す。
