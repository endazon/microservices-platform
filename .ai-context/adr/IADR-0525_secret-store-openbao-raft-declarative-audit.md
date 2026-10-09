---
title: IADR-0525 秘匿管理の製品を OpenBao 2.7.1 とし、名前（deploy/vault・VAULT_*・Vault__*）は変えない。永続化は raft、audit device は設定で宣言し collector への socket は届いてから足す。旧 Vault のデータは operator migrate で移し、移していない永続データの上には起動しない
type: impl-adr
status: Accepted
related_ids: [ADR-0132, ADR-0124, ADR-0107, ADR-0112, ADR-0023, IADR-0457, IADR-0471, IADR-0486, IADR-0514, IADR-0094, IADR-0096, IADR-0433, IADR-0495]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0132_secret-management-openbao.md 決定 1〜5（決定 3 の版の選択・決定 4 の受入条件 1〜6 の記録）
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 2
related_specs:
  - ../specs/20261009_1840_secret-store-openbao.md
---

# IADR-0525: 秘匿管理を OpenBao へ差し替える —— 名前は変えず、raft と宣言の audit へ改め、旧データは移してから立てる（#1840）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: claude（版・名前の扱い・ストレージ・audit の作り方・移行の形）。製品は計画の裁定（ADR-0132 決定 1）

## 起点・関連

- 起点 issue: **#1840**（planning#750 の裁定。計画 ADR-0132）。
- 計画: **ADR-0132** 決定 1〜5。**ADR-0124 決定 2**（audit の 2 つの記録）。**ADR-0107** 基準 A〜C・**ADR-0112 決定 1**（基準 D）。**ADR-0023**（Vault PKI は将来の選択肢）。
- 前提（覆さない）: [IADR-0457](./IADR-0457_local-vault-file-storage-pvc-and-in-pod-unseal.md)（Pod 内ラッパーで init / unseal / 固定 root トークン）、
  [IADR-0486](./IADR-0486_vault-audit-two-devices-stdout-and-collector-socket.md)（audit device を 2 つ）、
  [IADR-0471](./IADR-0471_platform-infra-encrypted-daily-backup.md)（`/vault/data` の暗号化バックアップ）、
  [IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（digest 固定と点検）、IADR-0094（OIDC）・IADR-0096（ESO）・IADR-0433（BFF の書き込み）・IADR-0495（datasource の読み取り）。
- **改める部分**: IADR-0457 の「`storage "file"`」は raft へ、IADR-0486 の「起動器が `vault audit enable` で 2 つ作る」は設定での宣言へ改める
  （両 IADR の他の決定はそのまま）。両 IADR の本文は凍結の記録として書き換えない。
- 基点コミット: MSP `origin/develop` `842b970f`。作業仕様書: `.ai-context/specs/20261009_1840_secret-store-openbao.md`。

## コンテキストと課題

計画は秘匿管理の製品を HashiCorp Vault（1.15 以降 BUSL-1.1。配備の 1.16.3 はコミュニティ保守も終了）から OpenBao（MPL-2.0。Vault 1.14 のフォーク）
へ差し替えると裁定し、受入条件 6 つ（実装の API・ESO・audit・PKI・環境変数・基準 B〜D）の確認と版の選択を実装へ委ねた。配備は経路 B の opt-in
（`VAULT=1`）だけで、`-dev`（`PERSIST=0`）と永続化（既定）の 2 構成がある。本番像の chart には秘匿管理の配備が無い。

実イメージ（`openbao/openbao:2.7.1`）で現行の配備をそのまま動かすと、次の 3 点で動かない（作業仕様書の実測）:

1. **file ストレージが無い**（`unknown storage type file`）。永続化の構成が起動しない。稼働中の PoC の `/vault/data` は file ストレージである。
2. **API での audit device の作成を拒む**（`cannot enable audit device via API`）。現行のラッパーは標準出力の device を作れず、起動を拒む（IADR-0486 の設計どおりに止まる）。
3. **`-dev` の root トークンが `BAO_DEV_ROOT_TOKEN_ID` でしか決まらない**。`VAULT_DEV_ROOT_TOKEN_ID` だけだと乱数になり、ESO の token 認証の store・種まき・手順書がすべて通らなくなる。

## 検討した選択肢

### A. 名前（k8s のリソース・Secret・フラグ・環境変数・設定キー）

1. **変えない（採用）** — 計画は改名を求めない（決定 2）。`deploy/vault`・Service `vault`（edge の 2 経路・helm の NetworkPolicy の `app: vault`）・
   Secret `vault-dev-token` / `vault-oidc`・PVC `vault-data`・`VAULT=1`・アプリの `Vault__*` は、稼働中の PoC の PVC・Secret・DNS・OIDC の redirect・
   手順書 8 本の `kubectl exec … VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"` に及ぶ。CLI は `VAULT_*` を読むので、名前を変えずに動く（実測）。
2. 一斉に `openbao` / `BAO_*` へ改める — 稼働中の PoC の移行の手間が増え、計画の「Vault」の読み替えと語が割れる。却下。

### B. 永続化のストレージ

1. **raft（単一ノード。採用）** — OpenBao の標準。旧 Vault の `operator migrate` で file から写せ、OpenBao が開ける（実測）。
2. PostgreSQL — 同じ Postgres に秘匿管理の鍵を同居させ、Postgres の停止が秘匿管理の停止になる。却下。

### C. audit device の作り方

1. **設定で宣言する。標準出力は `local.hcl`、collector への socket はラッパーが届いてから宣言を足して SIGHUP（採用）**。
2. 2 つとも `local.hcl` に宣言する — collector が居ないときの初回の init が**鍵を返さずに失敗し、初期化済みになる**（実測）。PVC を消すほかなくなる。却下。
3. `unsafe_allow_api_audit_creation = true` で従来のラッパーを使う — 上流が既定で閉じた口（audit device の `file_path` を通じて、root を持つ者が
   コンテナ内の任意のファイルへ書ける）を開く。root トークンは既知の dev 値なので、開けると到達できる者すべてに渡る。却下。

### D. 稼働中の旧 Vault のデータ

1. **旧イメージの使い捨ての Pod で `operator migrate`（file → raft）し、OpenBao で開く（採用）** — 同じ unseal 鍵で開き、KV の値と版の履歴・policy・
   認証の設定・固定の root トークンが残る（実測）。写す元は書き換えないので、戻せる。
2. 種まきからやり直す（PVC を消す） — 画面 SC-22 で入れた値は種に無く、失われる。却下（移せないときの最後の手段として手順書に残す）。

## 決定

1. **製品・版**: `openbao/openbao:2.7.1`（2026-10-01 のリリース・最新のタグ）を index の digest `sha256:6d2b93856e3fcf7b18ad855a0b51eaba474dc8b79cf554379ea32034797d2acf` で固定する。
2. **名前は変えない**（選択肢 A-1）。追加は `BAO_DEV_ROOT_TOKEN_ID`（`vault-dev.yaml`。同じ Secret の値）だけ。🔴 **Pod に `BAO_ADDR` / `BAO_TOKEN` を置かない**
   （`BAO_*` が `VAULT_*` より優先され、手順書とラッパーの `VAULT_TOKEN` が黙って効かなくなる。実測）。Loki のストリーム名 `{job="vault-audit"}` も変えない
   （行の形は同じ。名前を変えると保持中の行と抽出の条件が割れる）。
3. **永続化は raft（単一ノード）**。パスは `/vault/data/raft`（旧 file ストレージの `core/` 等と分ける）。`disable_mlock` は OpenBao に無いので外す。
4. **audit device は設定で宣言する**（選択肢 C-1）。ラッパーは、標準出力の device が在ることを確かめられなければ起動を失敗させ（IADR-0486 の不変条件を保つ）、
   socket の宣言を emptyDir（`/vault/audit.d`）へ書いて SIGHUP で読み直させ、足せなければ宣言を消して再試行する。起動のたびに宣言を消してから始める。
   値の扱い（`log_raw=false`・`hmac_accessor=true` の明示）は変えない。
5. **移行の門を 2 か所に置く**: ラッパーは「`/vault/data/core` が在り `/vault/data/raft/vault.db` が無い」なら起動しない（init で旧データの unseal 鍵を
   上書きしないため）。起動器は「稼働中の `deploy/vault` が旧 Vault のイメージで PVC `vault-data` が在る」なら入れ替えの前に止め、手順書
   （`docs/operations/secret-store-openbao-migration-runbook.md`）を名指しする。手順書は、旧 audit device を外す → 止める → 旧イメージで `operator migrate` →
   OpenBao を当てる → 数と版を突き合わせる、と戻し方を持つ。旧イメージは手順書が稼働中の Deployment から取り、リポジトリには持たない。

## 受入条件の記録（ADR-0132 決定 4）

| # | 結果 | 確かめ方（2026-10-09・手元の Docker。クラスタは未使用） |
| --- | --- | --- |
| 1 | **通った**。KV v2（`patch`・`cas=0`・metadata・削除の検知）・kubernetes auth（束縛した SA だけ）・OIDC auth（config・role・external group・`auth_url`）・dev モード | BFF の `VaultKvClient` と datasource-service の `VaultConnectorSecretResolver` を実イメージ・実 policy・実 role に向けた使い捨ての試験（コミットしない）。`eso/bootstrap.sh`・`oidc/bootstrap.sh` を実イメージへそのまま 2 回 |
| 2 | **通った**（クライアントの水準）。kubernetes 認証のログイン・`lookup-self`・KV v2 の読み取り・policy の外の拒否・token 認証 | ESO が使う `hashicorp/vault/api` v1.22.0 と `api/auth/kubernetes` v0.10.0 で同じ要求。ESO のコントローラそのものは未確認 |
| 3 | **通った**（作り方は宣言へ）。2 つの device・行のキー（`type`・`request.path`・`request.operation`・`auth.metadata.role`）・HMAC が同じ。平文の値は 0 件 | ラッパーの実走と、collector の代役が受けた行・コンテナログ |
| 4 | **成り立つ**。root の生成・role・`issue`・CSR の `sign` | 実イメージで PKI を有効にして発行 |
| 5 | CLI は `BAO_*` が無いとき `VAULT_ADDR` / `VAULT_TOKEN` を読む（`BAO_*` が優先）。サーバの `-dev` の root トークンは `BAO_DEV_ROOT_TOKEN_ID` だけ。`vault` は `bao` へのリンク | 実イメージで比較 |
| 6 | B: 匿名取得・index の digest を 2 回解決して一致。C: 既定の外部通信なし（130 秒・loopback 以外 0 件）。D: API・UI は token 必須。dev の root トークンは既知の dev 値で経路 B の opt-in に限る。永続化の鍵と初期 root トークンは PVC 上の 0600 ファイル（IADR-0457 と同じ水準） | 上の各実測 |

## 理由

- 名前を変えずに済むのは、OpenBao が `vault` のリンクと `VAULT_*` の読み替えを持つからである（実測）。変えない方が稼働中の PoC の移行が最小になる。
- 宣言の audit は上流の既定であり、API の口を開けずに IADR-0486 の 2 つの記録と「audit の無い秘匿管理を動かさない」を保てる。socket を後から足すのは、
  初回の init を collector の有無に依存させない（鍵を失う失敗を作らない）ためである。
- 門を 2 か所に置くのは、ラッパーの門だけだと起動器の rollout が 180 秒の時間切れで落ちるだけで理由が見えず、起動器の門だけだと手で
  `kubectl apply -k` したときに守れないからである。

## 結果

- 良い影響: 非 OSS・保守切れの製品が配備から消える。PKI の選択肢は残る。API の audit の口が閉じたまま 2 つの記録が残る。
- 悪い影響 / トレードオフ:
  - 稼働中の旧 Vault を永続化している PoC は、手順書の移行（約 5 分の停止）が要る。差し替えの前に取ったバックアップは file ストレージの写しで、戻した後に移行が要る。
  - socket の device を足す読み直しのたびに、collector が居なければサーバのログに ERROR が 1 行出る（再試行の回数だけ）。
  - raft の単一ノードは起動・unseal のときに `cannot find peer` 等の ERROR を出すが、動作に影響しない（実測）。
  - 🔴 **［#1866 監査 🟡3］日次バックアップ（IADR-0471）の整合の確かめ方が raft では弱くなる。** バックアップは稼働中の `/vault/data` を tar で写し、
    前後のファイル一覧（名前・サイズ・更新時刻の秒）を比べる。raft の `vault.db` / `raft.db` は bbolt で、ページをその場で書き換えるため、
    同じ秒・同じサイズの書き込みを見分けられず、破れた写しを成功として残し得る（file ストレージはキーごとのファイルの書き換えで、穴はより小さかった）。
    本 IADR の射程（製品の差し替え）では改めず、限界としてバックアップの手順書に記録し、整合したスナップショット（`operator raft snapshot save`
    または `GET /v1/sys/storage/raft/snapshot`）への改めを **#1868** に切り出した。バックアップのイメージには bao も curl も無く、Job へのトークンの
    受け渡し・戻し方（`snapshot restore`）・リストア試験の改訂を伴うためである。
- フォローアップ:
  1. **計画への環流**: ADR-0132 フォローアップ 2（受入条件 1〜3 の充足・決定 5 の「現在の実現手段」の更新）。
  2. **稼働中の PoC の移行**（利用者の手）: 手順書に沿って移し、確認の結果を issue に残す。
  3. ESO のコントローラでの確認は、PoC の移行の確認（手順書の「確認」の ESO の行）で兼ねる。
  4. **#1868**: 秘匿管理の日次バックアップを、稼働中の bbolt の tar から整合した raft のスナップショットへ改める（上の悪い影響の 🔴）。
  5. ［#1866 監査］移行の門は、`vault-persistence` を apply する起動器の**すべての経路**（`VAULT=1` のブロックと `ESO=1` のブロックの当て直し）で
     同じ関数（`vault_openbao_migration_gate`）を通す。ラッパーは、未初期化なのに鍵ファイルが在るときも init しない（鍵ファイルの上書きを防ぐ）。

## 関連

- 作業仕様書: `.ai-context/specs/20261009_1840_secret-store-openbao.md`
- 手順書: `docs/operations/secret-store-openbao-migration-runbook.md`
- 配備: `deploy/local/vault/vault-dev.yaml`・`deploy/local/vault-persistence/`・`scripts/k8s-local-up.sh`
