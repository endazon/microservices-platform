---
title: IADR-0332 keycloak-smtp の ExternalSecret を起動器へ配線する位置と、「起動器から参照されない配備宣言物が無い」ことの機械化
type: impl-adr
status: Accepted
related_ids: [SC-15, SC-16, FR-22, NFR, ADR-0026, ADR-0045, IADR-0261]
author: Claude（実装）
created: 2026-08-31
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
---

# IADR-0332: `keycloak-smtp` の ExternalSecret を起動器へ配線する位置と、「起動器から参照されない配備宣言物が無い」ことの機械化

- 状態: Accepted
- 日付: 2026-08-31
- 決定者: Claude（実装）
- 起点: issue #1102。先行する実装ADR: [IADR-0261](./IADR-0261_keycloak-theme-and-smtp-injection.md)
  （決定 2 で供給方式を決め、フォローアップ 1 が「起動器への組み込みは別 issue」と宣言した側）。
  同型の先例: [IADR-0316](./IADR-0316_bff-session-deploy-config.md)（#1107）。
- 関連する作業仕様書:
  [`.ai-context/specs/20260831_issue-1102_keycloak-smtp-externalsecret-wiring.md`](../specs/20260831_issue-1102_keycloak-smtp-externalsecret-wiring.md)

## コンテキストと課題

`ADR-0045` 決定 6（`Accepted`）は「**SMTP の資格情報は Vault で管理し、Kubernetes Secret として
供給する**」と定めている。`IADR-0261` 決定 2 がその実現方式（Vault → ExternalSecret → k8s Secret →
`kcadm` で realm へ反映）を確定させ、Vault の seed（`bootstrap.sh`）も ExternalSecret の定義
（`externalsecret-keycloak-smtp.yaml`）も**書かれた**。**起動器がそれを適用する 1 行だけが無かった。**

`deploy/local/vault/eso/externalsecret-*.yaml` は 16 本ある。`scripts/k8s-local-up.sh` が apply
するのは 15 本で、**落ちているのはちょうど `keycloak-smtp` 1 本**である。しかも `bootstrap.sh` の
最終行は `keycloak-smtp` を確認対象として案内しており、**案内どおり打つと必ず `NotFound` になる**。

決めるべきことは 3 つ。

1. **どこへ、どのゲートで置くか。** 他の 15 本は「常時／機能ゲート付き」「手動 apply と対」
   「`creationPolicy: Merge` で bootstrap 保持」の 3 通りに分かれており、`keycloak-smtp` は
   どれとも性質が違う（**env で読む Pod が 1 つも無い**）。
2. **`eso_wait` に入れるか。** 待つ理由が rollout でないなら、何のために待つのか。
3. **再発をどこで機械に持たせるか。** #1107 が新設した `check-secret-injected-options.js` に
   足すのか、別の場所か。

## 検討した選択肢

### 決定 1: apply の位置とゲート

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A. infra ns の並びへ常時 apply する** | `keycloak-admin` の直後。ゲートを付けない | **採用**。realm は `ESO=1` の経路で常に立っており、SMTP の供給先は常に存在する。空値の Secret ができるだけで秘匿値は 1 つも増えない |
| B. `SMTP_FROM` 等が与えられたときだけ apply する | 未供給時に空 Secret を残さない | **不採用**。**`bootstrap.sh` は env の有無によらず常に seed する**（空既定）。apply だけを条件付きにすると、Vault には在るのに Secret は無いという非対称ができ、案内文がまた嘘になる。加えて「無い」と「空」を運用者が区別できなくなる |
| C. `ESO != 1` 側に手動 apply の対を置く（postgres-app 等と同型） | 供給元が常に 1 つある | **不採用**。**`ESO=1` の外では Vault が無く、この Secret の値の出どころが無い**。dev 既定として空の Secret を手で作っても、runbook は「空なら `kcadm` を打つな」と定めており**使い道が無い**。同じ用途に 2 つ目の形を作らない |

### 決定 2: `eso_wait` の扱い

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A. `infra_sync` へ常時加える（rollout はしない）** | 同期の完了まで待つが、Deployment の再起動はしない | **採用**。理由は下記 |
| B. 待たない | 他と同じく apply しっぱなし | **不採用**。issue の受け入れ基準が明示的に待ち合わせを要求している。加えて **`up` の直後に運用者が案内文どおり打つ**設計であり、未同期の `NotFound` は「配線が無い」ときと**同じ見え方**をする（今回の欠陥そのものと区別がつかない） |
| C. 待ったうえで rollout もする | 他の ESO Secret と揃う | **不採用**。**この Secret を env で読む Pod は 1 つも無い。** Keycloak を無用に落とすだけで、`smtpServer` は再起動では反映されない（realm の実行時状態は `kcadm` が入れる） |

### 決定 3: 再発検査をどこへ置くか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A. `check-secret-injected-options.js` を拡張する | #1107 の検査器に相乗り | **不採用**。同検査器の母集合は **`*Options.cs` の doc コメント宣言**、突合先は **helm/compose の env** である。`keycloak-smtp` には C# の Options が無く env 注入もされない（**人間が `kcadm` で読む**）。**列挙を持たない**という同検査器の設計に「例外的な名前」を持ち込むことになり、その設計を壊す |
| B. 新しい検査器スクリプトを足す | 独立した不変条件として置く | **不採用**。判定に必要な「起動器が実際に発行する行」は `k8s-local-up.test.js` のドライラン器が既に持っており、静的 grep で作り直すと**ゲート付き apply を偽陽性にする**（`grafana-oidc` 等） |
| **C. `k8s-local-up.test.js` へ、列挙を持たない不変条件として足す** | ディレクトリの実体を母集合に、全ゲートの発行行の和と突合する | **採用**。母集合が実体なのでファイルへ名前を書き足す必要が無く、書き忘れが素通りしない |

## 決定

- **決定 1: 案 A。** `externalsecret-keycloak-smtp.yaml` を `ESO=1` ブロックの infra ns の並び
  （`externalsecret-keycloak-admin.yaml` の直後）で**常時** apply する。案内文（`infra_es`）へ
  `keycloak-smtp` を加え、infra ns の常時本数を 4 → 5 へ数え直す。
- **決定 2: 案 A。** `infra_sync` の初期値を `keycloak-smtp` にし、**常時待ち合わせる**。
  **rollout 対象には入れない**（env で読む Pod が無い）。待つ理由は「`up` 直後に案内文と runbook が
  そのまま実行できること」であり、rollout ではない —— この理由をコードのコメントに残す。
- **決定 3: 案 C。** `scripts/k8s-local-up.test.js` に
  **「`deploy/local/vault/eso/externalsecret-*.yaml` のすべてが、いずれかのゲート組み合わせで
  apply される」**を足す。母集合はディレクトリの実体、突合先は全ゲート run の和（`EMITTED_LINES`）、
  **0 件走査は fail-closed**。
- **決定 4（射程の境界）: realm への実値投入は行わない。** 利用者裁定 2026-08-15「設定手順の整備
  までが限度」の内側に留める。**本 PR が増やす秘匿値は 0 個**である（`from`/`user`/`password` は空のまま）。
- **決定 5（分割）: SC-15 の存在秘匿の破れは本 issue で直さない。** 別 issue として起票する
  （下記 §結果）。**本配線を入れても直らない**からである —— 空の `from` で `kcadm` を打てば
  Keycloak は `Please provide a valid address` を返し、同じ 500 になる。

## 理由

- **決定 1 の「常時」**: 本件の欠陥は「マニフェストが在るのに作られない」ことであり、**条件を足すと
  同じ欠陥の弱い版**（条件を満たさない人には作られない）が残る。空の Secret が残る不利益は、
  「案内が嘘になる」不利益より小さい。
- **決定 2 の「待つが再起動しない」**: `eso_wait` は元来 rollout の空振りを避けるために置かれた
  （IADR-0103）。**同じ関数を別の理由で使うので、その理由を書き残さないと後任が
  「なぜ rollout 対象に無いのか」を欠落と読む。**
- **決定 3 の向き**: 既存の個別検査（#1012 / #1022 / #1101）はいずれも
  **「手動 apply → 対応する ExternalSecret」**の向きで書かれており、コメントも「同型がもう一度
  起きたらこの向きで一般化せよ」と述べていた。**しかし #1102 で実際に起きたのは逆向きである** ——
  対を置く相手（手動 apply）がそもそも無い secret だったので、どの個別検査の視野にも入らなかった。
  **一般化の向きを、事故の側に合わせて選び直した。**
- **決定 5**: 存在秘匿の破れは realm・認証フロー・テーマのどれで塞ぐかの設計判断を伴い、
  `scripts/` の 1 行とは別種の変更である。**「ついでに直す」と、実測の重みが配線の PR に埋もれる。**

## 結果

- **良い影響**
  - `ADR-0045` 決定 6 の供給経路が `ESO=1` の既定経路で成立する。`bootstrap.sh` の案内と
    runbook の手順が**実行可能になる**（`NotFound` を踏まない）。
  - **「置いただけで配線されていない配備宣言物」が機械で止まる。** 次に ExternalSecret を足す人は、
    `k8s-local-up.sh` へ apply を書くまで CI で止まる。
- **悪い影響 / トレードオフ**
  - **値が空の Secret が platform-infra ns に常駐する。** 「無い」と「空」を運用者が読み分ける必要が
    あるが、runbook §2 が長さで判定する手順を持つ。
  - **`IADR-0261` の限界はそのまま残る** —— realm を再インポートすると `smtpServer` の実行時反映は
    消える（runbook の再実行が要る）。本 IADR はこれを直さない。
- **測っていて、直していないこと（重要）**
  - 🔴 **SC-15 の存在秘匿が稼働環境で破れている。** 実在する利用者名 → HTTP 500、実在しない利用者名
    → HTTP 200。**この差だけで利用者名を列挙できる。** 決定 5 のとおり分割起票した（**#1143**・`bug` / `priority:must`）。
  - **`ADR-0045` 決定 9 の捕捉用 MTA（開発環境で実送信しないための Mailpit 等）が未配備。**
    分割起票した（**#1144**）。`deploy/` 配下に実体は 0 件（同じ走査器・同じ範囲の陽性対照
    `qdrant` は 16 件＝走査は効いている）。**開発既定の宛先は外部の本番リレー**（`smtp.gmail.com:587`）
    のままであり、`from` に実値を入れた瞬間に決定 9 が禁じる側へ倒れる。
- **フォローアップ**
  1. **本 PR の 2 つの分割 issue（#1143 / #1144）は、どちらも本 PR とファイル領域が交差する。**
     #1143 は `docs/screens/SC-15_password-reset.md`、#1144 は `bootstrap.sh` /
     `k8s-local-up.sh` / `k8s-local-up.test.js` / runbook。**本 PR のマージ後に直列で着手する。**
  2. `eso_wait` の `MSP_NS` 側は `bff-oidc` / `identity-admin-oidc` / `postgres-app` /
     `rabbitmq-app` を待っていない（#1107 / #1101 / #1012 / #1022 の追加時に加えられなかった）。
     **本 IADR は infra 側だけを直した**（射程外）。同じ形の欠落として記録に留める
     （「同型が 2 回起きたら」の 1 回目）。

## 関連

- Supersedes: なし
- Superseded by: なし
