---
title: IADR-0616 ESO（Merge）が値を持つ土台の Secret（keycloak-smtp・reset-gate-oidc・abac-seeder-oidc）は、ESO=1 で既に在れば起動器が上書きせず、読み手の前に「依頼の後の同期」を待つ
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0095, ADR-0124, IADR-0099, IADR-0103, IADR-0332, IADR-0404, IADR-0576]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0095 決定 3（ESO は読み取り専用の同期を維持する）
  - planning:projects/microservices-platform/07_adr/ADR-0124 決定 1（対になる秘密）
related_specs:
  - ../specs/20261024_2195_eso-merge-base-secrets-keep-existing.md
---

# IADR-0616: ESO（Merge）の土台の Secret を dev の値で上書きせず、依頼の後の同期を待つ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2195 の是正。対象の範囲・待ち方）

## 起点・関連

- 起点: #2195（稼働 PoC・一括配備 #2175 の §5.4）。
- 計画: ADR-0095 決定 3（同期するのは ESO 自身）・ADR-0124 決定 1。いずれも変えない。
- 前提:
  - IADR-0099 は、基盤の 3 つ（postgres・rabbitmq・keycloak-admin）の手動 apply を、ESO=1 でも保持すると定めた。種と同じ値なので、Merge は no-op になる。本 IADR はこれを変えない。
  - IADR-0332 / IADR-0404（keycloak-smtp・reset-gate-oidc）と IADR-0576（abac-seeder-oidc）は、「ESO の有無によらず作る」と定めた。本 IADR は、その ESO=1 の側を「無ければ作る」へ改める。
  - IADR-0103 は、rollout の前に同期を待つと定めた。

## コンテキストと課題

起動器の [3/7] は、`keycloak-smtp`・`reset-gate-oidc`・`abac-seeder-oidc` を毎回 dev の値（または env）で apply していた。
ESO=1 では、後段の ExternalSecret（`creationPolicy: Merge`）が Vault の値を重ねる。ところが ESO が同期し直すのは、`refreshInterval`（1h）が来るか、注釈などの spec が変わったときだけである。
Secret の中身を外から書き換えても、ESO はすぐには戻さない（稼働 PoC で実測。force-sync するまで長さ 32 の dev の値のままだった）。

ESO の段の待ち（`kubectl wait --for=condition=Ready`）も、前回の実行の同期で既に True なので、待たずに通る。
その結果、起動器を実行するたびに、次の読み手が dev の値で名乗っていた。

- 投入器（ABACSEED・SEARCHSEED・TAGSEED）: 401 になった（#2195 の実測。2 回とも）。
- 門（reset-gate）・近接 MTA（mail-relay）: [4/7] と ESO の段の rollout で、dev の値の env を掴んで起動する。次に作り直すまで、そのままになる。

## 検討した選択肢

1. **ESO=1 では土台を作らない** — Merge は Secret を作らない。新規クラスタでは Secret が無いまま同期に失敗する。[4/7] の非 optional な secretKeyRef（門・近接 MTA）も起動しない。採らない。
2. **既存の値が dev の値と違うときだけ上書きしない** — 値を読んで比べる必要があり、起動器が秘密の値を扱う箇所が増える。ESO=1 で既存の値を残す効果は 3 と同じなので、採らない。
3. **ESO=1 で Secret が既に在れば触らない。無ければ従来どおり作る**（採用）。
4. **force-sync の後、Secret の値が dev の値でなくなるまで待つ** — Vault の値が dev の値のまま（新規の dev の環境）だと終わらない。採らない。代わりに `status.refreshTime` の変化を見る（決定 2）。

## 決定

1. **土台の置き方**（`scripts/k8s-local-up.sh` の `apply_eso_merge_base_secret`）
   - 対象は `keycloak-smtp`・`reset-gate-oidc`・`abac-seeder-oidc` の 3 つである。
   - ESO=1 で Secret が既に在れば、何もしない。env を与えた場合も同じである（値の持ち主は Vault → ESO。変えるなら SC-22 か Vault の KV で変える）。
   - 無ければ従来どおり作る（Merge の土台）。ESO の無い経路は従来どおり毎回置く（値の持ち主は起動器）。
   - 基盤の 3 つ（IADR-0099）は対象外とし、従来どおりである。
2. **待ち方**（`eso_force_sync_wait`）
   - ESO の段では、上の 3 つについて Ready を待つのではなく、次の順で待つ。
     1. 今の `status.refreshTime` を控える。
     2. `force-sync=<unix 秒>` の注釈を付ける。
     3. `refreshTime` が控えた値から変わり、かつ Ready が True になるまで待つ。
   - 時刻は解釈しない（date の方言に依らない）。控えた後の同期は、すべて [3/7] より後に起きている。
   - 期限は `ESO_FORCE_SYNC_TIMEOUT` 秒（既定 90）である。過ぎたら warn を出して続ける（best-effort。既存の `eso_wait` と同じ）。
   - 投入器と rollout は、この待ちの後に走る。
3. 起動器の利用者に、env を新しく要求しない。

## 結果・残る制約

- Vault の値と稼働中の Keycloak の値が食い違っていれば、投入器は引き続き 401 になる。これは本 IADR の射程外である（Vault の値を正にする運用。ADR-0124 決定 1）。
- 新規クラスタの初回は、従来どおり dev の値の土台へ Vault の値が重なる。決定 2 の待ちで、読み手の前に重なっている。
- 投入器の有効・無効の切り替えで Keycloak 26 が `service_account` の client scope を足し、realm の追随の `--check` が差分を出す件（#2195 の追記）は、別の仕組み（realm の宣言と追随の比較）である。本 IADR では扱わない（別の issue）。

## 検証

`node scripts/k8s-local-up.test.js` で次を確かめる。いずれも変異つきである。

- ESO=1 で既存の Secret を上書きしない（env を与えても上書きしない）。基盤の Secret は従来どおり置く。
- 新規クラスタでは土台を作る。ESO の無い経路は、Secret が在っても置く。
- 3 つの ExternalSecret に、apply の後かつ投入器の前に同期を依頼し、依頼の後の同期を確かめる。同期が停滞したときは warn を出して続ける。
