---
title: 近接 MTA へ投函できないときにリセット申請を機械で閉じる門 reset-gate を配備する（#1245 PR-C）
type: spec
status: draft
related_ids: [SC-15, SC-10, FR-05, FR-22, NFR-09, ADR-0026, ADR-0045, ADR-0078, IADR-0301, IADR-0329, IADR-0332, IADR-0344, IADR-0347, IADR-0369, IADR-0404]
author: Claude（実装）
created: 2026-09-07
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
---

# 仕様書: 近接 MTA へ投函できないときにリセット申請を機械で閉じる門 `reset-gate` を配備する（#1245 PR-C）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（認証・アカウント管理）／FR-22（通知。同じ送出基盤を将来使う）
- 画面（SC）: SC-15（パスワードリセット）／SC-10（運用ダッシュボード。**本 PR では触らない** = PR-B の射程）
- 非機能（NFR）: NFR-09
- 関連 ADR: **ADR-0078 決定 4**（投函できないときは**機械で** `resetPasswordAllowed=false` へ倒す）／
  ADR-0045 決定 2-b・5・8（ADR-0078 が部分改定）／ADR-0026（認証 UX の文言固定）
- 実装 ADR: [IADR-0404](../adr/IADR-0404_nearby-mta-relay-and-realm-ownership.md)（本 PR は同 ADR の
  **フォローアップ (2)** を着地させる）／IADR-0347（存在秘匿の 3 門）／IADR-0329（最小権限）／
  IADR-0369（realm の所有権境界）／IADR-0332（Secret 配線）／IADR-0344（捕捉用 MTA）

🔴 **引用する計画 ADR の題目を実ファイル名で確かめた**（レンジ検査は題目の正しさを見ない）:

```console
$ ls ../project-planning/projects/microservices-platform/07_adr/ | grep -E "ADR-0026|ADR-0045|ADR-0078"
（本作業機では隣接クローンを持たないため未実行。ADR-0078 / ADR-0045 / ADR-0026 の題目は
 .ai-context/specs/20260906_issue-1245_nearby-mta-relay.md L23-27 が同じ確認を実行した記録を持つ。
 レンジ（ADR-0001..0081）は .claude/rules/traceability.repo.md:7 が正本であり、3 件とも範囲内である）
```

## 前提（`git log` を出典に引く前の確認）

```console
$ git rev-parse --is-shallow-repository
false
```

**浅いクローンではない**ため、以下の `git log` / ブランチの出典は履歴の打ち切り位置ではない（planning#410）。

## 何を作るか（1 行）

**relay へ本物の SMTP 取引を周期的に打ち、投函できない状態を検知したら `resetPasswordAllowed` を
`false` へ倒し、回復したら宣言値へ戻す常駐の門**を配備する。人手（runbook §0）でしか閉じられなかった
窓 W1 / W1' / W2 を、**プローブ周期 ＋ PUT 往復**へ縮める。

## 母集合（自分で引き直した。規則 1〜10）

🔴 **設計メモの数えを転記していない。** 以下はすべて本ブランチ（`origin/develop` = `30e539c6`）で実行した走査である。
**誤りの側の文字列**で引き（規則 9）、**是正後に新たに誤りになる自分の記述**を引き直し（規則 10）、
**陽性対照を対で**置いた。`src/ai-stock-trading/`（submodule）と `CHANGELOG.md` は全走査から除く。

### S1: 「門はまだ無い / 人手である」を前提にした記述（＝門が着地すると偽になる側）

```console
$ grep -rln "機械で閉じる門\|門はまだ無い\|まだ人手\|人手（runbook\|門が入るまで\|#1245 PR-C" \
    --include=*.md --include=*.js --include=*.yaml --include=*.sh . | grep -v node_modules
./.ai-context/adr/IADR-0347_reset-existence-concealment.md
./.ai-context/adr/IADR-0369_persist-by-default-and-realm-reconcile-job.md
./.ai-context/adr/IADR-0404_nearby-mta-relay-and-realm-ownership.md
./deploy/local/keycloak-setup/reconcile-realm.js
./deploy/mail-relay/mail-relay.yaml
./docs/operations/keycloak-smtp-relay-setup-runbook.md
./src/ai-stock-trading/.ai-context/adr/IADR-0066_...（submodule。除外）
陽性対照（同じ走査器で "存在秘匿"）: 229 ファイル
```

追随の判断:

| ファイル | 追随 | 理由 |
| --- | --- | --- |
| `docs/operations/keycloak-smtp-relay-setup-runbook.md` §0 | **する** | 「この 1 箇所を人手に残していること自体が窓である」が偽になる。主＝機械の門 / 従＝人手の予備へ書き直す |
| `.ai-context/adr/IADR-0347_*.md` | **する（日付つき追記のみ）** | 2026-09-06 追記が「その門はまだ着地していない（#1245 PR-C）」と書いている。本文は書き換えない |
| `.ai-context/adr/IADR-0404_*.md` | **する（日付つき追記）** | フォローアップ (2) の着地。決定の追加もここへ置く（後述「実装 ADR の判断」） |
| `.ai-context/adr/IADR-0369_*.md` | **しない** | 追記本文は「門そのものは #1245 PR-C で着地する」＝**予定の記述**であり、着地しても誤りにならない |
| `deploy/local/keycloak-setup/reconcile-realm.js` | **しない** | ヘッダ注記は `GATE_OWNED_REALM_KEYS` の理由説明であり、門の着地で真偽が変わらない。**PR-A が既に実装済み**（下記 S8） |
| `deploy/mail-relay/mail-relay.yaml` | **しない（触らない）** | 本 PR の禁止事項。NetworkPolicy の追加は**別宣言**で行う（後述 D-3） |

### S2: 窓 W1 / W1' / W2 の名前を持つ文書

```console
$ grep -rln "窓 W1\|窓 W2\|W1'" --include=*.md --include=*.js --include=*.yaml . | grep -v node_modules
./.ai-context/adr/IADR-0347_reset-existence-concealment.md      # 追記で追随
./.ai-context/adr/IADR-0404_nearby-mta-relay-and-realm-ownership.md  # 追記で追随
./.ai-context/adr/README.md                                     # 索引の題目セル（変更不要）
./.ai-context/specs/20260906_issue-1245_nearby-mta-relay.md     # 凍結（書き換えない）
./.ai-context/specs/20260906_issue-1307_mail-relay-recipient-dns-reject.md  # 凍結
./deploy/mail-relay/mail-relay.yaml                             # 触らない
./docs/screens/SC-15_password-reset.md                          # 🔴 追随する
./scripts/check-realm-constraints.js                            # 追随不要（下記 S5）
陽性対照（"resetPasswordAllowed" 全体）: 19 ファイル
```

🔴 **`docs/screens/SC-15_password-reset.md` は S1 の走査に出ず、S2 で初めて出た**（規則 9 の
「誤りの側の語で引く」を 2 通り試して初めて母集合が閉じた例である）。窓の上限が「人手の分〜時間」から
「プローブ周期 ＋ 往復」へ変わるので追随する。

### S3: `resetPasswordAllowed` を人手で閉じる手順の在り処

```console
$ grep -rn "resetPasswordAllowed=false\|resetPasswordAllowed を false" --include=*.md --include=*.js --include=*.sh --include=*.yaml .
（手順として案内しているのは docs/operations/keycloak-smtp-relay-setup-runbook.md:101 の 1 箇所だけ。
 他 8 件は状態表・検査器の説明文であり、手順ではない）
```

### S4: `manage-realm` を「与えない」と書いている箇所（＝権限を広げると偽になり得る側）

```console
$ grep -rn "manage-realm\|realm-admin" --include=*.md --include=*.js --include=*.json --include=*.cs .
.ai-context/adr/IADR-0301_*.md:87,144                       # 主語は identity-admin → 真のまま
.ai-context/adr/IADR-0347_*.md:158                          # 承諾の記録（真）
.ai-context/adr/IADR-0404_*.md:19                           # 「着地は後続 PR」→ 追記で追随
.ai-context/specs/20260829_*, 20260831_*, 20260902_*, 20260906_*  # 凍結記録（書き換えない）
src/platform/backend/.../IdentityAdminRegistration.cs:95    # 主語は identity-admin → 真のまま
陽性対照（"realm-management"）: 13 ファイル
```

🔴 **`IADR-0301` とバックエンドのコメントは「`identity-admin` には与えない」であり、主語が違うので
偽にならない**。ここを「`manage-realm` は誰も持たない」と読み替えて一斉に直すと、SC-17 の最小権限の
記述を壊す。**追随しない理由をここに残す**（規則 9 が求める「挙げてから除外する」）。

### S5: `check-realm-constraints.js` の既存の門

`collectResetConcealmentGaps`（宣言の門）は**1 行も変えない** —— 不変条件（`resetPasswordAllowed=true`
なら `smtpServer.host` / `from` 非空）は門が入っても真である（IADR-0404 決定 6 が明記）。
本 PR が足すのは**別の不変条件**（サービスアカウントのロールの天井）である。

### S6: infra namespace の Secret の数え（導出値。走査ではなく計算し直す。規則 10）

```console
$ grep -n "infra ns は基盤" scripts/k8s-local-up.sh
550:  ... infra ns は基盤 3 本＋vault-oidc/keycloak-smtp 常時（#1102 で keycloak-smtp を追加し 4 → 5 へ数え直した）...
```

`reset-gate-oidc` を ESO の常時供給へ足すので、**5 → 6 へ計算し直す**（走査ではなく計算）。
`infra_es` / `infra_sync` の 2 つのリストと確認コマンドの案内文も同じ 1 行から導かれる。

### S7: `deploy/` の新しい Deployment を足したときに追随する側

```console
$ grep -rn "mail-relay" --include=*.md --include=*.js --include=*.sh --include=*.yaml --include=*.yml --include=*.json . \
    | grep -v node_modules | grep -v "^./deploy/mail-relay/" | awk -F: '{print $1}' | sort | uniq -c
   1 ./.ai-context/adr/IADR-0261...   1 ./.ai-context/adr/IADR-0344...   1 ./.ai-context/adr/IADR-0369...
   2 ./.ai-context/adr/IADR-0332...   3 ./.ai-context/adr/IADR-0404...
  12 ./.ai-context/specs/20260906_issue-1245...  4 ./.ai-context/specs/20260906_issue-1307...（凍結）
   1 ./deploy/keycloak/microservices-platform-realm.json
   4 ./deploy/local/README.md            1 ./deploy/local/infra/kustomization.yaml
   1 ./deploy/local/keycloak-setup/README.md   1 ./deploy/local/keycloak-setup/reconcile-realm.js
   1 ./deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml
  10 ./docs/operations/keycloak-smtp-relay-setup-runbook.md
   1 ./scripts/README.md                 6 ./scripts/check-realm-constraints.js   5 ./scripts/k8s-local-up.sh
```

このうち**本 PR で追随するのは** `deploy/mail-relay/kustomization.yaml`（新資材の取り込み）・
`deploy/local/README.md`（経路図に門を足す）・`scripts/k8s-local-up.sh`・`scripts/README.md`・
`docs/operations/*runbook.md` である。`deploy/local/infra/kustomization.yaml` は
`../../mail-relay` をディレクトリごと取り込むので**触らなくてよい**（新資材は base 側で足す）。

### S8: 🔴 PR-A が既に入れた分（本 PR で作り直さないもの）

```console
$ grep -n "GATE_OWNED_REALM_KEYS\|GATE_STATE_ATTRIBUTE" deploy/local/keycloak-setup/reconcile-realm.js
77:const RUNTIME_OWNED_REALM_KEYS = new Set([]);
83:const GATE_OWNED_REALM_KEYS = new Set(['resetPasswordAllowed']);
88:const GATE_STATE_ATTRIBUTE = 'reset-gate.state';
$ node scripts/keycloak-realm-reconcile.test.js | tail -1
32 tests passed.
$ grep -c "reset-gate" scripts/keycloak-realm-reconcile.test.js
4
```

**所有権の競合を止める側（`GATE_OWNED_REALM_KEYS` と `gateHoldsClosed()`）は PR-A が着地済みである**
（IADR-0404 決定 5 が「門より先にこれを入れる」と定めたとおり）。**本 PR は書き手側（門）を足し、
両者が同じ契約（属性 `reset-gate.state="closed"`）を使っていることを試験で固定する。**
設計メモ §4-5 の「`reconcile-realm.js` に GATE_OWNED を新設する」は**既に済んでいる**ので繰り返さない。

## ADR-0078 / IADR-0404 の制約（違反していないことの確認）

| # | 制約 | 本 PR での守り方 |
| --- | --- | --- |
| C1 | 門は**機械**である（ADR-0078 決定 4） | Deployment のループ。人手の手順は予備へ降格 |
| C2 | 検知は**能動**でなければ最初の 1 件が漏れる | relay へ本物の SMTP 取引を打つ（Pod Ready も監査イベントも見ない） |
| C3 | 門が閉じた状態を後追い Job が開き直してはならない（IADR-0404 決定 5） | 門は属性 `reset-gate.state=closed` を**同じ PUT で**書く。Job 側の除外条件は PR-A 実装済み |
| C4 | 属性 `reset-gate.state` を realm **宣言**へ書かない（同決定 5） | realm JSON にトップレベル `attributes` を足さない（下記 D-4 で確認） |
| C5 | 権限は `view-realm` ＋ `manage-realm` **だけ**（利用者が 2026-09-05 に承諾） | 専用機密クライアント。天井を宣言の門で固定する |
| C6 | 所要時間の閾値を決めない（ADR-0078 決定 1） | プローブ周期・再開回数は**マニフェストが与え**、コードは既定値を持たない |
| C7 | 「窓は無い」と書かない（IADR-0347 決定 5 の規律） | W1 / W1' / W2 は**縮んだが残る**と書き、上限の式を残す |

## 実装 ADR の判断（新規に起こすか、IADR-0404 へ追記するか）

**IADR-0404 への日付つき追記にする。** 理由:

1. 設計の正本（`.ai-context/specs/`… ではなく前セッションの設計メモ §6）が
   「**PR-C の権限決定は同じ ADR 本体に置き、着地時に日付つき追記で『実装済み』を記す（1 issue の決定を
   2 本に割らない）**」と裁定している。IADR-0404 はその ADR であり、**フォローアップ (2) が本 PR を名指ししている。**
2. IADR-0404 は `status: Proposed` の live な権威文書であり、**既に日付つき追記（2026-09-06 / #1307）を
   1 つ持つ**。追記は同 ADR の確立した更新機構である。
3. 🔴 **採番の実務**: 本ブランチ時点の最大は `IADR-0407`、`IADR-0408` は**開いている PR #1317 が確保済み**
   （`ac4e58c7 docs(FR-10,IADR-0408): 採番衝突により本 PR の実装 ADR を IADR-0408 へ改番する`）。
   `check-adr-numbering.js` は**欠番を許さない**ので、ここで `IADR-0409` を取ると本ブランチ単独では
   0408 が欠番になり CI が赤くなる。`IADR-0408` を取れば **2 本目の衝突**を作る。
   **どちらも避けられる**のが追記であり、上の 1・2 と結論が一致する。

```console
$ ls .ai-context/adr | grep -c "^IADR-"
408                      # IADR-0000..0407（本ブランチ = origin/develop）
$ gh pr list --state open --json number,title | grep -o "IADR-0408"
IADR-0408                # PR #1317 が確保中
$ node scripts/check-adr-numbering.js | tail -1
[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。
```

## 設計（決定と、設計メモからの意図的な差分）

### D-1: 門はプローブを **RCPT で終える**（DATA を送らない）

設計メモ §4-2 / §4-5 は「`DATA` まで送り、relay 側の `transport_maps = inline:{probe.invalid=discard:}`
で捨てる」形だった。**本 PR ではプローブを `MAIL FROM` → `RCPT TO` → `RSET` → `QUIT` で終える。**

```console
$ grep -n "transport_maps\|discard\|probe" deploy/mail-relay/mail-relay.yaml
（0 件）
$ grep -c "POSTFIX_" deploy/mail-relay/mail-relay.yaml
4                                  # 陽性対照。POSTFIX_ の env は 4 本あるが transport_maps は無い
```

理由は 2 つある。

1. 🔴 **`transport_maps` は着地した近接 MTA に入っていない**（上の走査）。`deploy/mail-relay/mail-relay.yaml`
   は本 PR の**編集禁止対象**である（別 PR が #1307 で触った直後）。
2. 🔴 **入っていない状態で `DATA` を送ると、プローブのメールが捕捉箱へ流れ込む。** relay は
   `RELAYHOST` へ**宛先によらず全部**中継する（`boky/postfix` の "behind closed doors" 構成。
   `smtpd_relay_restrictions=permit`）ので、`probe@probe.invalid` 宛でも上流の mailpit が受け取る。
   周期 10 秒なら 30 分で 180 通であり、**`check-password-reset-mail.js` の T-17（ちょうど 1 通）を壊す。**

**RCPT で終えても検知能力は落ちない**（むしろ #1307 の実測に照らすと上がる）:

| 捕まえたい状態 | Postfix が返す段 | RCPT 止まりで見えるか |
| --- | --- | --- |
| relay が居ない（W1） | TCP 接続拒否 | ○ |
| SYN が落ちる（W1'） | タイムアウト | ○（Keycloak と同じ 10 000 ms で待つ） |
| キュー満杯・ディスク不足（W2） | `452 4.3.1` は **MAIL FROM** で返る | ○ |
| 差出人拒否（W2） | `check_sender_access` は **`smtpd_recipient_restrictions` の中**＝ RCPT（IADR-0404 V12） | ○ |
| 宛先 DNS 検証の再混入（#1307 の再発） | `reject_unknown_recipient_domain` は RCPT | ○ |
| 宛先構文の拒否（W2 の残る入口） | `reject_non_fqdn_recipient` は RCPT | ○ |

**DATA 以降にしか現れない失敗は「キューへ書けない」系だが、それは 452 として MAIL FROM で先に出る。**
🔴 **測っていない**: 上表は上流イメージのソース（IADR-0404 V8〜V15）と Postfix の仕様からの**導出**であり、
稼働クラスタで打っていない（本作業機にクラスタが無い）。**PR-D の 4 状態実測で確かめる。**

### D-2: 判定は非対称（1 回の失敗で閉じ、連続成功で戻す）

純関数 `decide({ probeOk, declaredAllowed, liveAllowed, gateState, consecutiveSuccesses, reopenAfter })`:

- `probeOk=false` かつ 稼働が開いている → **`close`**（1 回で倒す。窓を最小化する）
- `probeOk=true` かつ 稼働が閉じている かつ **`gateState==="closed"`**（＝**門が閉じたと記録している**）
  かつ `consecutiveSuccesses >= reopenAfter` → **`reopen`**
- 🔴 **`declaredAllowed=false` なら決して `reopen` しない**（門は「開ける主体」ではなく「宣言どおりに戻す主体」）
- 🔴 **`gateState!=="closed"`（人が手で閉じた）なら `reopen` しない**（門が他人の意思を上書きしない）
- それ以外は `none`（**既に閉じているときに再 PUT しない** —— admin event を毎周期増やさない）

### D-3: 権限は専用機密クライアント `reset-gate` の service account に 2 ロールだけ

`view-realm`（GET realm）＋ `manage-realm`（PUT realm）。`standardFlowEnabled:false` /
`directAccessGrantsEnabled:false`（MFA 迂回禁止。`identity-admin` と同型）、
`defaultClientScopes: ["realm-management-roles"]`（これが無いと Admin API は 403。IADR-0329 決定 2）。

🔴 **realm の `users[]` へ service account 利用者を必ず入れる**（#1301 の事故: client はあるが
`users[]` に無く、ロールが誰にも付いていなかった）。**realm を読んで確かめる**（下記 D-4 の検査）。

面積を最小に保つ緩和:

1. 門のコードは **PUT 本文に `resetPasswordAllowed` と `attributes["reset-gate.*"]` 以外の変更を入れない**
   （GET した表現からこの範囲だけを差し替えて PUT。コレクション系のキーは本文から落とす）。
2. **宣言の門が天井を固定する**（次項 D-4）。
3. secret はリポジトリに置かず Secret `reset-gate-oidc`（dev 既定値 ＋ Vault/ESO 供給）から読む。

### D-4: `check-realm-constraints.js` に「サービスアカウントのロールの天井」を足す（IADR-0329 の 2 回目）

IADR-0329 §記録に留める は「同型の事故の**2 回目**が起きたら `check-realm-constraints.js` へ
『service account に `realm-management` のクライアントロールを持つクライアントは、
クライアントロールを載せるスコープを持つこと』を陽性対照つきで足せ」と申し送っている。
**本件が 2 つ目の主体であり、条件が満たされた。** 足す不変条件は 3 つ:

1. `realm-management` のクライアントロールを持つ SA 利用者に対応するクライアントは
   `defaultClientScopes` に `realm-management-roles` を持つ（IADR-0329 の申し送りそのもの）
2. **`manage-realm` を持つ SA は 1 つだけ**であり、それは `serviceAccountsEnabled` な機密クライアントである
   （**名前を書き写さない**。「1 つだけ」という**関係**を見る）
3. `manage-realm` を持つクライアントは `standardFlowEnabled` / `directAccessGrantsEnabled` が
   どちらも false であり、**`manage-users` を併せ持たない**（IADR-0329 が分けた主体の区切りを壊さない）

🔴 **`clients[].serviceAccountsEnabled` は宣言されていても `users[]` に SA 利用者が無ければ
ロールは誰にも付かない**（#1301）。よって 4 つ目:

4. `serviceAccountsEnabled: true` のクライアントには、対応する `serviceAccountClientId` を持つ
   利用者が `users[]` に**ちょうど 1 つ**存在する

### D-5: `check-password-reset-mail.js` は門の状態を読み、閉じていたら**理由を名指しして赤**にする

現状は「閉じている＝存在秘匿としては健全」で緑を返す（`:536-541`）。**門が入ると、CI で relay が
一時的に落ちたときにメールの試験（T-16 / T-17）が静かに飛ぶ。** 稼働 realm の
`attributes["reset-gate.state"]` を読み、`closed` なら失敗にする（`EXPECT_GATE_CLOSED=1` のときだけ反転。
PR-D のシナリオ実行が使う）。

🔴 **設計メモ §4-7 の「稼働 realm の読み出しを kcadm exec から Admin REST へ移す」は本 PR では行わない。**
理由: (a) §4-7 は本作業の指示が挙げた必読節に入っていない、(b) 経路の付け替えは**稼働クラスタでしか
確かめられない**のに本作業機にクラスタが無く、失敗すれば `integration-stack` が develop で赤くなる
（#1307 で実測した失敗の形そのものである）、(c) 本 PR の目的（門の配備）は読み出し経路に依存しない。
**IADR-0369 の禁則（Keycloak pod で kcadm を exec しない）に対する残債であることを追記に明記し、
環流の対象にする。**

## 変更するファイル（宣言済みファイル領域）

| # | ファイル | 変更 |
| --- | --- | --- |
| 1 | `deploy/mail-relay/reset-gate.yaml`（新設） | 門の Deployment ＋ NetworkPolicy（**relay の ingress へ門を足す 2 本目の宣言**。`mail-relay.yaml` は触らない） |
| 2 | `deploy/mail-relay/reset-gate.js`（新設） | 門の本体（`node:22-alpine`・依存ゼロ・`--self-test` を持つ） |
| 3 | `deploy/mail-relay/kustomization.yaml` | `reset-gate.yaml` を resources へ |
| 4 | `deploy/keycloak/microservices-platform-realm.json` | クライアント `reset-gate` ＋ SA 利用者 |
| 5 | `scripts/check-realm-constraints.js` | `collectServiceAccountRoleGaps` の新設（D-4）＋自己試験 |
| 6 | `scripts/check-password-reset-mail.js` | 門の状態の認識（D-5）＋自己試験 |
| 7 | `scripts/reset-gate.test.js`（新設） | 門の純関数の単体試験（`keycloak-realm-reconcile.test.js` と同型） |
| 8 | `scripts/k8s-local-up.sh` | `reset-gate-script` ConfigMap・`reset-gate-oidc` Secret・rollout・ESO 配線 |
| 9 | `deploy/local/vault/eso/externalsecret-reset-gate-oidc.yaml`（新設）＋ `bootstrap.sh` | Vault → Secret の供給経路 |
| 10 | `.github/workflows/ci.yml` | `reset-gate.test.js` のステップ（既存ジョブへ 1 行。**起動条件・必須チェック名は変えない**） |
| 11 | `docs/operations/keycloak-smtp-relay-setup-runbook.md` | §0 を「機械の門が主・人手は予備」へ |
| 12 | `docs/screens/SC-15_password-reset.md` | 残る窓の上限を「人手」から「プローブ周期 ＋ 往復」へ |
| 13 | `deploy/local/README.md` | 経路図に門を足す |
| 14 | `scripts/README.md` | 新しい検査・試験の行 |
| 15 | `.ai-context/adr/IADR-0404_*.md` / `IADR-0347_*.md` | 日付つき追記 |

**射程外（本 PR に入れない）**: 観測（exporter・collector・アラート・SC-10）= PR-B ／
4 状態の実測と計測器 = PR-D ／ `deploy/mail-relay/mail-relay.yaml`・`scripts/check-stack-ready.js`（編集禁止）／
`ISTIO` / `.github/workflows/integration-stack.yml`（#1316 が別に動く）。

## 受け入れ基準

| # | 基準 | 測り方 |
| --- | --- | --- |
| A1 | 1 回の失敗で閉じる | `reset-gate.test.js`（純関数） |
| A2 | 連続 N 回の成功で宣言値へ戻す。**宣言が false なら戻さない** | 同上 |
| A3 | **人が手で閉じた**（属性が無い）状態を門が開けない | 同上 |
| A4 | 既に閉じているとき再 PUT しない | 同上 |
| A5 | PUT 本文に 4 キー以外の変更が入らない | 同上（差分を取る） |
| A6 | 門が書く属性と Job が読む属性が**同じ綴り**である | `reset-gate.test.js` が両モジュールの定数を突合 |
| A7 | `manage-realm` を持つ SA は 1 つだけ・スコープを持つ・`users[]` に居る | `check-realm-constraints.js --self-test` ＋ 実データ走査 |
| A8 | 門が閉じているとき `check-password-reset-mail.js` が赤になる | 同スクリプトの `--self-test` |
| A9 | 変異試験が**閉じる側・開ける側・Job との競合**の 3 方向で赤になる | 実走（PR 本文に生出力） |
| A10 | 検査器一式・`scripts.test.js` が緑 | 実走 |

🔴 **稼働クラスタでの実測はできない**（本作業機にクラスタが無い）。**「確かめた」と書けるのは
静的検査と単体試験だけである。** 4 状態・窓の秒数・プローブが実際に relay の応答を受けることは
**PR-D で測る**。本 PR は「動くはず」を実測として書かない。

## 変異試験の計画（最低 5 つ・実走する）

| # | 変異 | 期待 | 方向 |
| --- | --- | --- | --- |
| M1 | `decide` の失敗判定を「2 回連続で閉じる」に緩める | A1 が赤 | **閉じる側** |
| M2 | `declaredAllowed=false` でも reopen する | A2 が赤 | **開ける側** |
| M3 | `gateState` を見ずに reopen する | A3 が赤 | **Job / 人との競合** |
| M4 | 門の属性名を `resetGate.state` に変える | A6 が赤（Job が開き直す形） | **Job との競合** |
| M5 | realm から SA 利用者 `service-account-reset-gate` を落とす | A7 が赤（#1301 の形） | 権限の天井 |
| M6 | `identity-admin` に `manage-realm` を足す | A7 が赤 | 権限の天井 |
| M7 | `check-password-reset-mail` の門認識を消す | A8 が赤 | 静かな skip |

## 実施結果

### 試験件数（前 → 後）

| 試験 | 前 | 後 |
| --- | --- | --- |
| `check-realm-constraints.js --self-test` | 105 | **117** |
| `check-password-reset-mail.js --self-test` | 21 | **25** |
| `deploy/mail-relay/reset-gate.js --self-test` | （無し） | **15** |
| `scripts/reset-gate.test.js` | （無し） | **12** |
| `scripts/keycloak-realm-reconcile.test.js` | 32 | 32（fixture の `realm-management` に `manage-realm` を足しただけ） |
| `scripts/k8s-local-up.test.js` | 159 | 159 |

### 変異試験（実走。生出力は PR 本文へ貼る）

| # | 変異 | 結果 | 捕まえた試験 |
| --- | --- | --- | --- |
| M1 | `decide` を「2 回連続の失敗で閉じる」に緩める（**閉じる側**） | **赤** | `reset-gate --self-test`（`reset-gate.test.js` は緑＝役割が違う） |
| M2 | 宣言 `false` でも reopen する（**開ける側**） | **赤** | `reset-gate --self-test` |
| M3 | `gateState` を見ずに reopen する（**人 / Job との競合**） | **赤** | `reset-gate --self-test` ＋ `reset-gate.test.js` |
| M4 | 門が書く属性名を `resetGate.state` へ変える（**Job が開き直す形**） | **赤** | `reset-gate.test.js` ＋ `check-password-reset-mail --self-test`（🔴 `reset-gate --self-test` 単独では緑＝**片側だけでは検知できない**ことの実証） |
| M5 | realm から SA 利用者 `service-account-reset-gate` を落とす（#1301 の形） | **赤** | `check-realm-constraints`（本走・自己試験）＋ `reset-gate.test.js` |
| M6 | `identity-admin` に `manage-realm` を足す（天井の破れ） | **赤** | 同上 |
| M6b | `reset-gate` から `realm-management-roles` スコープを外す（IADR-0329 課題 A） | **赤** | 同上 |
| M7 | `check-password-reset-mail` の門認識を消す（静かな skip の復活） | **赤** | 同スクリプトの `--self-test` |
| M8 | プローブの差出人を realm の `from` と違う値にする（常時 closed になる形） | **赤** | `reset-gate.test.js` |
| M9 | プローブに `DATA` を足す（捕捉箱を汚し T-17 を壊す形） | **赤** | `reset-gate.test.js` |

🔴 **M4 が本 PR の核心である。** 門の自己試験は単独では緑になり、**両モジュールを同時に読む試験だけが
競合を捕まえた。** 「門を書いた」ことと「門が効いている」ことは別である。

### 静的検査（すべて緑）

`check-realm-constraints`（自己試験・本走）／`check-adr-numbering`／`check-doc-links`／`check-trace-blocks`／
`check-plan-id-qualification`／`check-cross-repo-refs`／`check-doc-type-vocabulary`／
`gen-knowledge-graph --check`／`check-secret-injected-options`／`k8s-local-up.test.js`。
`kubectl kustomize deploy/mail-relay` ／ `deploy/local/infra` ／ `deploy/local/infra-persistence` は描画できる。

🔴 **`check-deploy-manifests.js` は本作業機で走らない**（`kubeconform` が PATH に無く fail-closed。
`DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS` は**立てていない**）。**スキーマ突合は CI で初めて行われる。**

### 🔴 実測していないこと

- **稼働クラスタでの挙動を 1 つも測っていない**（本作業機にクラスタが無い）。
  プローブが relay の応答を実際に受けること・窓の秒数・4 状態はいずれも**未実測**（#1245 PR-D）。
- 決定 D-1 の表（RCPT 止まりで W1 / W1' / W2 を捕まえられる）は**上流ソースからの導出**である。
- `reset-gate` の Deployment が起動すること・`reset-gate-oidc` で token が取れること・
  `manage-realm` で `PUT /admin/realms/platform` が通ることは**確かめていない**。
- 起動直後に relay がまだ Ready でない瞬間、門が 1 度閉じて約 `REOPEN_AFTER_SUCCESSES × 周期` 後に
  開き直す挙動が想定されるが、**これも未実測**である（`k8s-local-up.sh` は relay の rollout を
  門より先に待つが、両者は同じ `apply -k` で同時に作られる）。

## 環流（本 PR では直さず、計画へ返すもの）

1. 稼働 realm の読み出しが依然 `kcadm` の pod 内 exec である（IADR-0369 の禁則に対する残債。D-5）
2. 門自身が落ちている間は W1 が開いたまま（門を監視する門は作らない。観測は PR-B の `ResetGateProbeAbsent`）
3. プローブ周期・再開回数の値の正しさは実測で決める（ADR-0078 決定 1 §残るもの）
4. `probe@probe.invalid` 宛のプローブは、宛先 DNS 検証が再混入したとき **go-live では偽陽性になり得る**
   （実在ドメインは引けるため）。fail-closed 側なので受容するが、環流して閾値ではなく**構成の検査**で
   代替できるか計画に判断を仰ぐ
