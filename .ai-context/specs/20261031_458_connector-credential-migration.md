---
title: 作業仕様書 — データソースの平文の資格情報の移送と、移送期間の旗（#458 段 S4・S5）
type: spec
status: in-progress
related_ids: [NFR-18, FR-01, SC-06, SC-22, ADR-0126, IADR-0493, IADR-0495, IADR-0501, IADR-0664]
author: claude
created: 2026-10-31
updated: 2026-10-31
issue: "#458"
---

# 作業仕様書 — 平文の資格情報の移送と移送期間の旗（#458 段 S4・S5）

> 本仕様書は実装着手前に作成した（着手 2026-10-31。develop `996e543a` で着手し、`24962c62` へ進めた）。
> MSP の棚卸し（2026-10-31・コーディネーター承認）の (A)。設計の正本は IADR-0664。段割りと窓の表は作業仕様書 20261003_458 §設計 S4・§窓 1。

## 範囲

- **S4**: 一回きりの移送と、移送期間の旗。旗は、移送の後の平文の拒否（同期・書き込み・起動）と、平文の行 0 件の門を持つ。
- **S5**: 文書の追随。
  - `security.md` の暫定の節（移送と移送後の拒否・残余リスク）。
  - FR-01 の機能仕様書。
  - runbook（移送の手順・バックアップ・戻し方・保管先の版の巻き戻し・無効化したデータソースの値の棚卸し）。
- 経路 B の datasource-service が保管先を読む構成（`values-local.yaml`）。
- **範囲外**: 本番で保管先を読む配備の値（ADR-0126 フォローアップ 3。#1789）。ADR-0126 フォローアップ 4 の計画側の確定（環流は planning#866。PR のマージ前に起票した）。

## 受け入れ基準（作業仕様書 20261003_458 §受け入れ基準と §設計 S4 の試験欄の写像）

- [x] 窓 1 の表の 3 列を `[Fact]` で実測し、表どおりになる（`Sync_PlaintextWindow_MatchesTheBothEndsTable`）。
- [x] 移送の再実行が冪等である（`Rerunning_is_a_no_op_and_the_startup_gate_counts_zero`）。
- [x] 平文の行が残ると、旗を偽にできない（`The_service_refuses_to_start_…`／`The_service_starts_…`）。
- [x] 移送の後は、`Config` の秘密のキーへの平文の書き込みを 400 で拒む（登録・更新・部分更新。マスクの書き戻しは通す）。
- [x] 移送は Vault が先・設定が後である。Vault に書けない行・衝突した行の設定は触らない。同じパスの別のキーを消さない（check-and-set）。
- [x] 設定の置き換えは、読んだ平文と今の値が同じときだけ行う（実 PostgreSQL の `CredentialMigrationStoreTests`）。
- [x] 移送のコマンドは、トークン・値・パスを出力・ログ・例外の文言に出さない（コーディネーターの条件 (1)。不達の経路を通す試験を含む）。
- [x] 既定（旗が true）では、配備しても振る舞いが変わらない（コーディネーターの条件 (2)）。

## 母集合（規則 9・10）

- **規則 9**: 次の語で、文書・コード・配備物を全文走査した。
  - 語: 「平文」「段 S4」「移送」「PlaintextPassthrough」「plaintextAllowed」「datasource.vault」「データソースの接続資格情報」「Vault 連携」
  - `docs/security/security.md` §データソースのコネクタ資格情報: 移送の項を足し、残余リスクに追記する。
  - `docs/functional/FR-01_data-source-catalog.md` の「Vault 連携」: 取り消し線で改める。
  - `docs/operations/secret-rotation-runbook.md`「本書が扱わない秘密」の表: 「平文の移送は別段」を改め、移送の節を足す。
  - `PlaintextPassthroughConnectorSecretResolver` の冒頭（「段 S4 で撤去」）: 撤去しない（旗が真の間の素通しとして残る）。日付つきで追記する。
  - `VaultConnectorSecretResolverTests` の配線の試験: 外側に飾りが巻かれるので、内側を見る形へ改める。
  - `.ai-context/adr/IADR-0493`・`IADR-0495`・`IADR-0501`（「段 S4 まで」）: 凍結記録なので書き換えない。IADR-0664 が引く。
- **規則 10**（この変更で新たに誤りになる自分の記述）:
  - security.md の「緩和」の節の「読む経路」は、平文を移送期間として通すとしか書いていない → 移送の項で、旗が偽のときの拒否を書いた。
  - runbook の「確認」の節は、本節の対象を含まない（移送の節の中に確認の手順を置いた）。
- **規則 10 の取りこぼし（CI で判明・2026-10-31）**:
  - values-local に datasource の保管先の所在を足したため、`scripts/vault-k8s-auth-audience.test.js` が数える「values-local で Vault で名乗る Deployment」の数が 1 から 2 に変わった。
  - この試験は helm が要り、CI（static-checks-units）だけで走る。そのため手元の `scripts.test.js` では捕まらなかった。
  - 直し方: 期待値を 2 にした。そのうえで、試験 3c を足した。3c は名乗る側と受ける側を突き合わせ、datasource-service が次の 3 つを満たすことを名指しで確かめる。
    - audience=vault のトークンを持つ。
    - 名乗る role（datasource-connector-reader）が、その SA と名前空間に束縛されている。
    - その role が audience=vault を要求する。
  - 同じ変更で描画が変わる他の helm の試験も走査した（values-local を描く試験 20 本を「Vault__Address・egress-to-vault・projected・datasource」で引いた）。追随は不要だった。
    - Vault への egress の NetworkPolicy は `networkPolicy.enabled` の下にあり、values-local では描かない。
    - values-local の egress の workloads に、datasource-service は入っていない。
- **改番（2026-10-31）**: 先に出た #2375 が IADR-0663 を使っていたため、本件の IADR を IADR-0664 へ改めた。
  - 誤りの側の語「IADR-0663」で、ブランチ内を全文走査した。対象は ADR の本体、索引、作業仕様書、trace ブロックの 3 文書、配備物、スクリプト、コードとテストのコメントで、計 34 ファイルだった。そのうえですべて置き換えた。置き換えの後、「IADR-0663」は 0 件である。
  - develop には「IADR-0663」の出現が無かったので、本ブランチの外に追随先は無い。

## 検証

- DataSourceService の単体（489 件）、Platform.Bff の policy の試験。
- `Knowledge.IntegrationTests` の `CredentialMigrationStoreTests`（実 PostgreSQL。ローカルの Docker で流した後、イメージと dockerd を片付ける）。
- `dotnet format --verify-no-changes`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（コミットした状態で）。
- 文書系の検査。
