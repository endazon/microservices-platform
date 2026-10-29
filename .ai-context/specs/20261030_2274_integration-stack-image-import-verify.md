---
title: 作業仕様書 — integration-stack の赤（k3d image import が黙って入らない）を、取り込みの確かめと 1 回のやり直しで止める（#2274）
type: spec
status: done
related_ids: [NFR, IADR-0232, IADR-0605, IADR-0642]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2274"
---

# 作業仕様書 — integration-stack の赤（#2274）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `af891ca8`）。

## 現状（実測。失敗した 3 回の run のログを読んだ）

| run | コミット | 落ちた場所 | 原因 |
| --- | --- | --- | --- |
| 40251217288 | `b1f63d8d` | [1/7] の直後 | `helm list` が新しいクラスタで失敗し、「現行の mesh.mtlsMode を読めない」で止まった（fail-closed の設計どおり）。k3d の作成直後で API が応答していなかったとみられる。**直後の #2275**（`ad153375`。作成を上限つきにし、全ノードの Ready を待つ）で、作成の後に待つようになった。以後の 11 回は緑 |
| 40271395268 | `b8e7aef7` | [2/7] のビルド | `mirror.gcr.io/library/caddy` の層の取得が `not found` で失敗した（レジストリのミラー側の一時的な不整合）。リポジトリの外の要因 |
| 40288649179 | `f4b374aa` | 門（G1。Pod が 28 分 Pending） | 🔴 **`k3d image import` が全ノードで「`ctr: open /k3d/images/….tar: no such file or directory`」になったのに、k3d は「Successfully imported 20 image(s)」と言って 0 で返った。** 起動器はそのまま配備へ進み、イメージの無い Pod が Pending のまま門で落ちた。**取り込みを確かめる仕組みが無い** |

- 最後の失敗の後、develop の push と schedule で 11 回続けて緑（間欠）。3 件目は再び起き得るうえ、起きると原因の分からない 28 分の待ちになる。

## 方針

- `scripts/k8s-local-images.sh` の k3d の経路で、取り込みの後に**全ノード（server / agent）にイメージが在るか**を確かめる。
  - 確かめ方は `lib/backup-cronjob-gate.sh` と同じ（`docker exec <ノード> crictl inspecti <ref>`）。ノードは `docker ps --filter label=k3d.cluster=<名>` の `-server-N` / `-agent-N` で、serverlb・tools はイメージを持たないので外す。
  - 無ければ WARN を出して 1 回だけやり直す。それでも無ければ、ノードとイメージを名指して止める（配備へ進まない）。
  - ノードを列挙できなければ止める（確かめられないを「在る」にしない）。
  - 確かめるのは本体（MAPPING）だけ。LOCAL_ONLY（バックアップのイメージ）は、従来どおり不在でも起動を止めない（#1564。CronJob の門が見る）。
- 1 件目は #2275 で対処済みとみなす。2 件目は外部の一時的な失敗で、本件では扱わない。どちらも本文と PR に書いて、issue を閉じる判断の材料にする。

## 受け入れ基準

- [x] 取り込みが黙って失敗した世界（スタブ）で、確かめて 1 回やり直し、入れば起動を続ける（修正前は赤＝やり直さない）
- [x] やり直しても入らなければ helm の配備へ進まずに止まり、ノードとイメージを名指す（修正前は赤＝配備へ進んだ）
- [x] 陽性対照: 入っていれば取り込みは 1 回で、確かめは通る

## 母集合（規則 9）

- `git grep -n "k3d image import"`（scripts・workflows）→ `k8s-local-images.sh` の 1 か所だけ。他の取り込みの経路（rancher の nerdctl build）は containerd へ直接ビルドするので取り込みが無い。

## 規則 11（窓）

- 時間差を扱う是正ではない（取り込みの後に一度確かめるだけで、待ちの窓は無い）。表は描かない。
