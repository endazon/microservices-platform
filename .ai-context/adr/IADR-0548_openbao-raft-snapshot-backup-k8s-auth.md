---
title: IADR-0548 秘匿管理（OpenBao・raft）の日次バックアップは、稼働中の bbolt の tar をやめ、raft の整合したスナップショットを専用の SA の k8s auth（sys/storage/raft/snapshot の read だけ・1 回きりのトークン）で取り、中の SHA256SUMS で検算してから暗号化する。HTTP は bash の /dev/tcp で話し、旧 tar 経路は残さない
type: impl-adr
status: Accepted
related_ids: [NFR-21, NFR-18, NFR-05, ADR-0132, ADR-0008, IADR-0471, IADR-0525, IADR-0457, IADR-0096]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0132（秘匿管理の製品を OpenBao へ）
related_specs:
  - ../specs/20261016_1868_openbao-raft-snapshot-backup.md
---

# IADR-0548: 秘匿管理の日次バックアップを raft の整合したスナップショットへ改める（#1868）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-16
- 決定者: claude（オーナーの依頼 #1868）

## 起点・関連

- 起点 issue: #1868（PR #1866 の独立監査 🟡3）。
- 関連する計画書 ID: NFR-21（障害検出〜復旧・Runbook）・NFR-18（シークレット管理）・NFR-05。関連する計画 ADR: ADR-0132・ADR-0008。
- 関連する実装 ADR: [IADR-0471](./IADR-0471_platform-infra-encrypted-daily-backup.md)（日次バックアップ。**決定 4 を本 IADR が改める**）・
  [IADR-0525](./IADR-0525_secret-store-openbao-raft-declarative-audit.md)（OpenBao・raft。フォローアップ 4 が本件）・
  [IADR-0457](./IADR-0457_local-vault-file-storage-pvc-and-in-pod-unseal.md)（永続化と Pod 内 unseal・init のファイル）・
  [IADR-0096](./IADR-0096_vault-eso-secret-supply-k8s-auth.md)（k8s auth と TokenReview の reviewer）。

## コンテキストと課題

- CronJob `platform-backup-vault` は稼働中の `/vault/data` を tar で写し、前後のファイル一覧（名前・サイズ・更新時刻の秒）を比べて
  整合を確かめていた（IADR-0471 決定 4）。IADR-0525 で永続化が raft になり、`vault.db` / `raft/raft.db` は bbolt になった。
  bbolt はページを**その場で**書き換えるため、同じ秒・同じサイズの書き換えはこの比較で見分けられず、**破れた写しを成功として残し得る**。
- OpenBao は一貫した時点の FSM をスナップショットとして返す API（`GET /v1/sys/storage/raft/snapshot`。`bao operator raft snapshot save` と同じ）を持つ。
  ただし、Job が秘匿管理へ到達し、トークンを得て、HTTP を話す必要がある。バックアップのイメージ（Postgres 16 の Alpine ＋ age）には
  bao も curl も無い。
- 秘匿に関わるので、権限は最小にし、root トークンを使わず、トークンを平文でログ・マニフェスト・リポジトリ・プロセスの引数に置かない。
  失敗は fail-closed（空・壊れたスナップショットを「成功」として置かない）。

## 検討した選択肢

| 論点 | 選択肢 | 採否 |
| --- | --- | --- |
| 整合の取り方 | (a) raft のスナップショット API / (b) 写す間だけ秘匿管理を止める（scale 0） | **(a)**。(b) は停止が生じ、Job に Deployment を操作する権限と kubectl が要る |
| 認証 | (a) k8s auth（専用 SA） / (b) Secret `vault-dev-token`（固定 root）を渡す / (c) ラッパーが PVC に置く専用トークン | **(a)**。(b) は root。(c) は長命のトークンが PVC に残り、失効と更新の仕組みが要る |
| HTTP の道具 | (a) bash の `/dev/tcp` / (b) busybox wget / (c) bao を同梱 / (d) curl を同梱 | **(a)**。(b) はヘッダのトークンが argv（`/proc/<pid>/cmdline`）に出る。(c) は 181 MB（静的バイナリ）を足し、`snapshot save` はファイルにしか書けない。(d) は依存パッケージ 8 前後を版・sha256 で固定する保守が要る |
| 旧 tar 経路 | (a) 撤去 / (b) 旧 Vault（file ストレージ）のときだけ tar へ自動で戻す | **(a)**。自動の後退は「整合を保証できない写しを成功として置く」を作り直す。移行前の写しは移行の手順書の手順 1（移行前の CronJob）で取る |

## 決定

1. **Vault の回の成果物を改める。** `vault-raft.snap.age`（raft のスナップショット）と `vault-init.age`（init のファイル＝unseal 鍵と初期 root トークン）。
   `vault-data.tar.gz.age` は作らない。スナップショットは同じ鍵でしか開けないので、init のファイルを同じ回に並べる。
   暗号化（age・受取人）・保持・保管先（C: / E: と目印）・時刻・イメージは IADR-0471 のまま変えない。
2. **認証は k8s auth。** 専用の ServiceAccount `platform-infra/platform-backup-vault`（k8s の RBAC なし・`automountServiceAccountToken: false`）。
   CronJob は projected の serviceAccountToken（期限 600 秒・既定の audience）だけを `/var/run/secrets/platform-backup/jwt` に載せる。
   role `platform-backup-vault` は **その SA・その名前空間にだけ束縛**し、`token_policies=platform-backup-raft-snapshot`・
   `token_no_default_policy=true`・`token_ttl=5m`・`token_max_ttl=10m`・**`token_num_uses=1`**（スナップショットの 1 要求で使い切る）。
   ポリシー `platform-backup-raft-snapshot` は **`sys/storage/raft/snapshot` の read だけ**（update＝restore・KV の read を足さない）。
3. **role・ポリシーの投入は runtime bootstrap**（`deploy/local/platform-backup/vault/bootstrap.sh`。ESO の bootstrap と同型・冪等）。
   Vault の SA への TokenReview の権限（`vault-auth-rbac.yaml`）・kubernetes 認証の enable と config（ESO と同じ値）・ポリシー・role を入れる。
   root を使うのはこの運用者の操作だけで、vault Pod の中の env で読み、値はスクリプトの引数・出力に現れない。
   起動スクリプトは永続化の overlay を当てて unseal を待った後に毎回呼ぶ（VAULT ブロック・ESO ブロックの当て直しの両方）。
   **ESO の有無によらない。** 失敗しても起動は止めず WARN を出す（秘匿管理そのものは動いており、Job の失敗として見える）。
4. **HTTP は bash の `/dev/tcp`（HTTP/1.0）。** 宛先は `http://vault.platform-infra:8200`（秘匿管理はクラスタ内で `tls_disable`）。
   HTTP/1.0 で送るので、サーバは chunked を使わず本文の終わりで接続を閉じる（本文をそのまま写せる）。トークンと JWT は bash の組み込み
   （`printf`・変数）でだけ扱い、**プロセスの引数に載せない**。ログには状態コードと Vault の `errors` の文言だけを出す。
   ログインの応答（トークンを含む）は作業場所に置いて読んだらすぐ消す。宛先が `http://<ホスト>:<ポート>` の形でなければ送らない。
5. **fail-closed の検査。** ログイン・スナップショットの状態コードが 200 でない、本文が空、gzip として最後まで読めない、tar として読めない、
   `meta.json`・`state.bin`・`SHA256SUMS` のどれかが無い、`SHA256SUMS` の `meta.json` / `state.bin` の行が中身から計算した値と一致しない、
   `state.bin` が空 —— のどれでも**スナップショットを置かず、回そのものを書かない**（exit 1）。init のファイルが無い・unseal 鍵の行が無いときは
   スナップショットだけの `-partial` の回にする（exit 1。同じ秘匿管理へは戻せるが、失ったときには開けない）。
6. **作業場所はメモリの emptyDir**（`/scratch`・256 Mi。コンテナのメモリの上限を 256 Mi → 512 Mi）。スナップショットは検査の後に age へ渡して消す。
   スナップショットの中身は barrier の鍵で暗号化されたままだが、「平文をディスクへ書かない」約束（IADR-0471）をここでも守る。
7. **旧 tar 経路は撤去する**（決定 4 の前後の比較・やり直し・`BACKUP_VAULT_TRIES` を含む）。旧 Vault（file ストレージ）にはスナップショットの API が無く、
   新しい CronJob はそこでは失敗して何も書かない。移行前の写しは、移行の手順書の手順 1 を**移行前の CronJob**で取る（手順書に注記した）。
8. **戻し方**（手順書 §5。手元で往復を実測した）:
   - 同じ秘匿管理へ: 復号したスナップショットを vault Pod へ渡し、root で `snapshot restore`。audit device が健全なら unseal のまま。
     sealed になったら Pod を作り直す（ラッパーが unseal する）。
   - 失った後: 空の PVC でラッパーが新しい鍵で init → root で `snapshot restore -force` → sealed → init のファイルを写しの方へ差し替え →
     Pod を作り直すとラッパーが元の鍵で unseal する（固定 root トークン・KV・k8s auth の role まで戻る）。

## 理由

- スナップショットは OpenBao 自身が一貫した時点で書き出す。外から写す限り、bbolt の書き換えの途中を「整合」と判定できる比較は作れない。
- k8s auth は ESO・BFF・datasource-service と同じ認証で、仕組みが既にある（IADR-0096）。専用 SA に束縛し、トークンを 1 回きり・5 分にすれば、
  漏れても使える窓がほぼ無い。Job が読む init のファイルには初期 root トークンが在るが、Job はそれを**暗号化して写すだけ**で使わない
  （従前の tar の写しも同じものを含んでいた。守りの水準は下がらない）。
- `/dev/tcp` はイメージを変えない（タグ・digest・CI のビルドの範囲に触れない）。HTTP の扱いは 1 要求・平文・HTTP/1.0 に限るので小さく、
  壊れた応答は後段の gzip / tar / SHA256SUMS の検査が必ず拾う。

## 結果

- 良い影響: 破れた写しを成功として残す穴が閉じる（中の SHA256SUMS で検算した一貫した時点の写しだけを置く）。戻し方が手順として確かになる。
  Job は root を持たず、秘匿管理に対してスナップショットを取ること以外できない。
- 悪い影響 / トレードオフ:
  - Job が秘匿管理の API と k8s auth に依存する。秘匿管理が sealed・停止中・bootstrap 未投入なら回は取れない（失敗として見える）。
  - 旧 Vault の file ストレージの残り（移行後に片付けるまで `/vault/data` 直下に在るもの）は日次の写しに入らなくなる（移行の手順書 手順 8 に注記）。
  - k8s auth の config を ESO の bootstrap と 2 か所で書く（同じ値）。値を変えるときは両方を揃える。
  - スナップショットが 256 Mi を超えるとメモリの emptyDir が溢れて失敗する（PoC の実測は数十 KB。超えたら上限を上げる）。
  - 同じ秘匿管理へ戻すとき、collector に届かない socket の audit device が残っていると sealed になる（Pod の作り直しで戻る。実測）。
- 試験: `backup.test.sh` の T-1868-00〜10（node の偽の秘匿管理で `/dev/tcp` の HTTP を通す。検査ごとの文言まで見る）・
  `scripts/platform-backup.test.js` の 11（SA・projected token・ポリシー・role・root の Secret なし・メモリの作業場所・curl / wget なし。変異 12 種）。
  検査（gzip・空・状態コード・必須の中身・SHA の突合・平文の後始末）を 1 つずつ外すと赤になることを確かめた。
- フォローアップ:
  1. 稼働中の PoC への適用（大引け後）: 移行が済んでいることを確かめ、起動スクリプトの再実行（または bootstrap と kustomize の当て直し）→
     手動の Job で初回を確かめる（手順書 §1 の 5〜6）。
  2. リストア試験（§3）の 5 の使い捨ての OpenBao での戻しを、四半期の試験で一度通す。

## 関連

- [作業仕様書](../specs/20261016_1868_openbao-raft-snapshot-backup.md)
- [バックアップの手順書](../../docs/operations/platform-infra-backup-runbook.md)・[移行の手順書](../../docs/operations/secret-store-openbao-migration-runbook.md)
