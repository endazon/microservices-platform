---
title: platform-backup の CronJob を、前提（イメージ・age の受取人）が揃うまで suspend で置く（#1699）
type: spec
status: done
related_ids: [NFR-21, ADR-0008, IADR-0471, IADR-0066]
author: claude
created: 2026-09-29
updated: 2026-09-29
issue: "#1699"
---

# 仕様書: platform-backup の CronJob を前提が揃うまで止めて置く（#1699）

## 起点（トレーサビリティ）

- #1699（PoC セッションの実測。2026-09-29 20:30 JST）: 稼働クラスタの再起動のあと、CronJob が取りこぼしていた回を実行した。
  対象は `platform-backup-postgres` と `platform-backup-vault` の 2 本で、どちらの Pod も `ImagePullBackOff` のまま残った。
  - イメージ `k3d-local/platform-backup:pg16.15-age1.3.1-r6` は稼働クラスタに無い。ビルドは資格情報ヘルパーの失敗で落ちていた（#1689）。
  - age の受取人も、利用者がまだ用意していない。
- NFR-21（バックアップ）・IADR-0471（日次の暗号化バックアップ）。計画 ADR の新たな制約は無い。

## 問題

`k8s-local-up.sh` は、前提が欠けていても CronJob を**有効のまま**適用する。その結果、毎日の回が失敗した Pod を残す。
これは監視の雑音になるうえ、「CronJob がある＝バックアップが動いている」と誤読させる。

## 決定

1. `k8s-local-up.sh` の最後（すべての apply の後）で、2 本の CronJob それぞれについて前提を判定する。
   欠けていれば `suspend: true`、揃っていれば `suspend: false` を `kubectl patch` で**毎回明示的に**書く。
   - 明示的に書く理由: `apply -k` はマニフェストに無い `suspend` を触らない。書かないと、一度止めた CronJob が、前提が揃っても止まったままになる。
2. 前提は 2 つ。
   - **image**: CronJob が実際に使うイメージの参照（`kubectl get cronjob … jsonpath` で CronJob から読む）が、ランタイムに在ること。
     - Rancher: `nerdctl --namespace k8s.io image inspect`
     - k3d: `docker exec k3d-<cluster>-server-0 crictl inspecti`
   - **recipients**: ConfigMap `platform-backup-age-recipients` の `recipients.txt` が、CronJob の中で走る `backup.sh` の `check_recipients` を通ること。
     - `BACKUP_LIB=1` で読み込み、**同じ関数**で判定する。規則を複写すると、門と CronJob の中で判定がずれる。
     - 検査の文言は捨てる（公開鍵でも中身は表示しない）。
3. 止めて置いたときは、欠けている前提の名前（`image` / `recipients`）を WARN で名指しし、直し方を 1 回だけ案内する。
4. `PERSIST=0`（CronJob を置かない起動）では、門を動かさない。
5. 判定は `scripts/lib/backup-cronjob-gate.sh` に閉じる（試験できる形）。

### 採らなかった案

- **CronJob を適用しない**: 適用しない形では取り外しの操作が要り、再実行のたびに状態が揺れる。suspend なら `get cronjob` で状態が見え、再実行で戻せる。
- **イメージの有無をノードの `.status.images` で見る**: kubelet は既定で 50 件までしか報告しない。MSP と AST のイメージで溢れると、在るのに無いと判定し、黙ってバックアップを止める。
- **`[2/7]` のビルド結果を覚えて判定する**: ビルドを飛ばした再実行や、手で作り直したイメージを反映できない。

## 母集合（規則 9）

- `git grep -n "platform-backup" -- 'deploy/**/kustomization.yaml'` で、CronJob を取り込むのは次の 2 つである。
  - `deploy/local/infra-persistence`（postgres）
  - `deploy/local/vault-persistence`（vault。VAULT=1 のときだけ）
  - どちらも PERSIST=0 では入らない。vault の CronJob が無い起動は、`get cronjob` の失敗で飛ばす。
- 試験 `scripts/k8s-local-up.test.js` の既定の検査に、「受取人の ConfigMap の名前を含む行が無い」（#1560）がある。
  門が ConfigMap を**読む**ようになるので、禁じる対象を「作り直し（`create configmap`）」に絞った。検査の意図（運用者の鍵を上書きしない）は変わらない。
- 手順書 `docs/operations/platform-infra-backup-runbook.md`: §2（日々の確認）と「失敗したときの分岐」に、停止で置かれた状態と戻し方を追記した。

## 受け入れ基準 → 試験（`scripts/k8s-local-up.test.js`）

1. 既定（受取人なし）: 2 本とも `"suspend":true` で置き、`recipients` だけを名指しする（image は名指ししない）。
2. 門は `apply -k deploy/local/infra-persistence` より後で書く。
3. イメージと正しい受取人が揃えば、2 本とも `"suspend":false` にし、停止を告げない。
4. 受取人が揃っていてもイメージが無ければ（`crictl inspecti` が失敗）、`"suspend":true` で `image` だけを名指しする。
5. 受取人が占位のまま、または前後に空白のある鍵なら、`"suspend":true` にする。
6. `PERSIST=0` では門を動かさない。

## 検証

- `node scripts/k8s-local-up.test.js`: 224/224 成功。
- `bash -n` で `k8s-local-up.sh` と `lib/backup-cronjob-gate.sh` を検査し、構文エラーなし。

## 残余

- 既に残っている失敗の Job と Pod は消さない。Pod は Job の `activeDeadlineSeconds`（3600 秒）で終わり、Job の記録は履歴の上限（7）で古い順に消える。
- 前提が揃ったかどうかは、起動スクリプトを再実行したときにだけ反映される（常駐の監視はしない）。
