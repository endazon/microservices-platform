---
title: IADR-0494 Vault の seed（eso/bootstrap.sh）は、その実行で書いた KV を覚え、最後にそれを読む ExternalSecret だけをクラスタから引いて force-sync を付け、同期の完了（refreshTime の更新＋Ready、足したキーの在否）を有限時間で待って、終わらなければ名指しして止める
type: impl-adr
status: Accepted
related_ids: [NFR-18, SC-22, ADR-0095, ADR-0124, IADR-0096, IADR-0103, IADR-0456, IADR-0485, IADR-0492]
author: claude
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR-18 秘密情報の管理)
  - planning:projects/microservices-platform/07_adr/ADR-0124 決定 1（対になる秘密は相手と対で回す）
  - planning:projects/microservices-platform/07_adr/ADR-0095 決定 3（画面の書き込みは即時同期を依頼する）
related_specs:
  - ../specs/20261003_1728_eso-force-sync-after-bootstrap.md
---

# IADR-0494: bootstrap が書いた KV を読む ExternalSecret にだけ同期を促す（#1728）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-03
- 決定者: claude（#1728 の「期待」を実装の形へ落とした）
- 採番: **IADR-0493 は別ブランチの PR #1727 が使っている**ので本件は 0494 とする（先着尊重。#1727 より先にマージすると一時的に 0493 が欠番になる）。

## 起点・関連

- 起点 issue: #1728（PoC 2026-10-03 の配備。MSP `233f432d` / helm rev 15）。関連: #1696（読み手クライアント）、AST#1078
- 関連する計画書 ID: NFR-18、SC-22
- 関連する計画 ADR: ADR-0124 決定 1、ADR-0095 決定 3
- 関連する実装 ADR: [[IADR-0096]]（ESO の bootstrap）、[[IADR-0103]]（env は Pod 起動時に 1 度だけ解決）、
  [[IADR-0456]]（画面の force-sync と Reloader・無いときだけの seed）、[[IADR-0485]]（対になる秘密は無いときだけ作る）、
  [[IADR-0492]]（kb-reader-auth-client-* を足した）
- 基点コミット: `origin/develop` `233f432d`

## コンテキストと課題

`deploy/local/vault/eso/bootstrap.sh` は在る KV を壊さないように「無いときだけ作る」「無いプロパティだけ足す」「env が空でないときだけ差し替える」
へ変わった（IADR-0456・IADR-0485）。その結果、**在る KV へ新しいキーが入る経路**（`vkv_patch_if_missing` ほか）ができた。
しかし ExternalSecret は `refreshInterval: 1h` でしか Vault を読み直さず、`kubectl apply` は spec が同じなら同期を起こさない。

PoC 2026-10-03: bootstrap が `ai-stock-trading/app-secrets` へ `kb-reader-auth-client-*` を足したが、AST の `ast-secrets`
（`dataFrom.extract`）は配備の前（02:08Z）に同期したきりで、Secret に新しいキーが無かった。`force-sync` の注釈を手で付けると入り、
Reloader が trade-decision を作り直した。それまで読み手は空の秘密で動いた。

### 決めること

| # | 論点 | 選択肢 |
| --- | --- | --- |
| 1 | 誰が同期を促すか | (a) bootstrap.sh の末尾／(b) k8s-local-up.sh の後段／(c) AST の配備スクリプト |
| 2 | 対象の決め方 | (a) 名前を書いたリスト／(b) クラスタから引き、書いた KV のパスで絞る／(c) すべての ExternalSecret |
| 3 | 「書いた」の判定 | (a) 書き込みの呼び出しを数える／(b) 値が変わったときだけ |
| 4 | 同期を待つか・失敗の扱い | (a) 促すだけ／(b) 有限時間待って warn で続ける／(c) 有限時間待って名指しして止める |

## 決定

1. **bootstrap.sh の末尾で促す**（1-a）。bootstrap は `k8s-local-up.sh` のほか、runbook（SSO 復旧・対になる秘密）から単独でも呼ばれる。
   書いた本人が促せば、どの入口から呼んでも窓が閉じる。
2. **対象はクラスタから引く**（2-b）。`kubectl get externalsecret -A` の `spec.secretStoreRef.name`・`spec.data[].remoteRef.key`・
   `spec.dataFrom[].extract.key` を読み、**ストアが `ESO_STORE`（既定 `vault-backend`）で、この実行で書いた KV のパスを読むものだけ**に
   `force-sync=<epoch 秒>` を付ける（`--overwrite`）。AST のチャートが描く `ast-secrets` を基盤のリストへ写すと、片方だけ増えて漏れるので名前を書かない。
   まだ apply されていない ExternalSecret は対象に入らない（作られたときの初回の同期で読む）。
3. **「書いた」は書き込みが成功したときだけ数える**（3-a）。数える経路は 4 つ: `vkv_create_if_absent` の put・SC-22 の 4 KV の無いときの put・
   `vkv_patch_nonempty` の patch・`vkv_patch_if_missing` の patch。patch に失敗したキーは数えない（WARN だけ。従前どおり止めない）。
   **keycloak-smtp の構成値（host / port / starttls）は今の値と違うときだけ書く**（`vkv_patch_config`。3-b を構成値に限って採る）。
   従前は毎回 patch していたので、再実行のたびに「書いた KV」になり、同じ値なのに毎回 keycloak-smtp を促して待つことになる。
   構成値は秘密ではないので、今の値を読んで比べてよい。秘密（from / user / password・app-secrets 等）は従前どおり値を読まない。
4. **有限時間待ち、終わらなければ名指しして非 0 で止める**（4-c）。完了の条件は、促す前と違う `status.refreshTime` ＋ `Ready=True`。
   在る KV へ足したプロパティは、`dataFrom.extract` で読む ExternalSecret の同期先 Secret にキーが在ることまで見る（値は出さない）。
   上限は `ESO_FORCE_SYNC_TIMEOUT`（秒・既定 120）、間隔は `ESO_FORCE_SYNC_INTERVAL`（秒・既定 2）。`ESO_FORCE_SYNC_TIMEOUT=0` は促すだけで待たない。
   一覧を引けない・注釈を付けられないときも、手で促すコマンドを出して非 0 で止める。

> **［2026-10-03 追記 / #1728・独立監査］決定 4 の「足したキーの在否」は、同期先 Secret のキー名だけを引いて確かめる。値（base64）をシェル変数へ入れない。**
> 従前は `kubectl get secret -o jsonpath='{.data}'` で値ごと引いて文字列照合していた。`bash -x` や後から足すデバッグの echo で秘密がログへ出る。
> 今は `-o go-template='{{range $k, $v := .data}}{{$k}}{{"\n"}}{{end}}'`（1 行 1 キー）で引き、行の完全一致で在否を見る。「値は出さない」を「値を読まない」へ強めた。
> あわせて、時間の指定の先頭の 0 は 10 進として読む（`08` を算術展開が 8 進と読んで落ちる・`00` を「待たない」と読めない、を防ぐ）。
> 促す前の `refreshTime` は名前空間・名前の完全一致で引く（部分一致だと `infra` と `platform-infra` を取り違える）。決定の向きは変えない。

## 理由

- 4-b（warn で続ける）は、`k8s-local-up.sh` の既存の同期待ち（`eso_wait`）と同じ best-effort だが、`eso_wait` は `condition=Ready` を見るだけで、
  **古いまま Ready の ExternalSecret を即座に通す**（PoC の状態がまさにそれ）。ここで warn に倒すと、症状（空の秘密で動く）が緑の起動の後ろに隠れる。
  #1728 の期待は「新しいキーを足したら同期を強制する」であり、強制できなかったことは利用者が知るべき失敗である。
- 対象を書いた KV に限るので、何も書かない再実行は ExternalSecret を引きもしない（静か）。止まる危険は「実際に新しい値を届ける必要がある」ときに限られる。
- refreshTime の比較は文字列の不一致で見る（epoch への変換をしない）。`date -d` の方言に依らない。

## 却下した案

| 案 | 却下の理由 |
| --- | --- |
| 1-b（k8s-local-up.sh の後段で促す） | runbook から bootstrap だけを呼ぶ経路で窓が残る |
| 1-c（AST の配備スクリプトで促す） | 書き手は基盤の bootstrap であり、AST は Vault を書かない。書いていない側が「書かれたかもしれない」を推測することになる |
| 2-a（名前のリスト） | AST のチャートの ExternalSecret 名を基盤へ写すことになり、片方だけ変わると漏れる（#1477 の契約は同期先の名前までで、読み手の全集合ではない） |
| 2-c（すべてに付ける） | 書いていない KV を読む ExternalSecret まで毎回同期させ、待ちの失敗の面が全 ExternalSecret に広がる |
| 3-b を秘密にも広げる | 比較のために秘密の値を bootstrap のシェル変数へ読み出すことになる。秘密の経路は「書いたときだけ数える」で足りる（無いキーを足す・env が空でないときだけ差し替える、のどちらも本当に値を変える操作） |
| `refreshInterval` を短くする | Vault への読みが常時増える。在る KV へ足すのは bootstrap の実行時だけであり、その時に促せば足りる |

## 結果

- 正: bootstrap が在る KV へキーを足した直後に、それを読む ExternalSecret（`ast-secrets` を含む）が同期し、Reloader が消費側を作り直す。
- 正: 何も書かない再実行は静か（ExternalSecret を引かず、待たない）。keycloak-smtp の構成値の無駄な patch も無くなった。
- 負: 同期が上限内に終わらないと bootstrap（＝ `k8s-local-up.sh`）が止まる。逃げ道は `ESO_FORCE_SYNC_TIMEOUT=0`（促すだけ）か上限を延ばしての再実行。
- 残余: `dataFrom.find`（名前の正規表現で探す形）で読む ExternalSecret は対象に入らない。現時点で MSP・AST のどちらにも無い。
- 残余: `deploy/local/wikijs-setup/bootstrap.sh` は `msp/wikijs-sync` を Vault へ書いた後、Secret を直接書いて Pod を作り直す（窓が無い）ので本件の対象外。
- 残余: キーの在否の確認は `dataFrom.extract` の ExternalSecret だけ（`data[]` はプロパティから Secret のキーへの写像を読む必要があり、Ready＋refreshTime で足りる）。

## 関連

- 作業仕様書: `.ai-context/specs/20261003_1728_eso-force-sync-after-bootstrap.md`（母集合・変異の結果）
