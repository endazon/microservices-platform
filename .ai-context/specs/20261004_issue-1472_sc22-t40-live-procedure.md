---
title: SC-22 テスト T-40（稼働クラスタでの書き込み→同期→長さの確認）の手順書を書く（#1472 項目 4）
type: spec
status: done
related_ids: [SC-22, FR-05, NFR-18, ADR-0095, ADR-0104, ADR-0110, IADR-0453, IADR-0454, IADR-0456, IADR-0460, IADR-0494]
author: claude
created: 2026-10-04
updated: 2026-10-04
plan_refs:
  - planning:projects/microservices-platform/05_screens/ (SC-22 秘密情報・接続設定の管理)
  - planning:projects/microservices-platform/07_adr/ADR-0095 決定 3（書き込みの後は即時同期を依頼する）
  - planning:projects/microservices-platform/07_adr/ADR-0110 決定 3（確認した書き込みを契機に即時同期と自動の作り直しが続く）
issue: "#1472"
---

# 仕様書: SC-22 テスト T-40 の稼働クラスタ手順を書く（#1472 項目 4）

> 本仕様書は文書の作成に着手する前に作成する。起点は #1472 の項目 4（稼働クラスタでの疎通 T-40 ＋ IADR-0454 フォローアップ 1）。
> 基点: `origin/develop` `82b864f8`。**本作業はクラスタにも Vault にも触れない**（手順を書くだけ。実施は次のクラスタ構築時）。

## 起点となる計画書（トレーサビリティ）

- 画面: **SC-22**（秘密情報・接続設定の管理）。要求 FR-05、NFR-18。
- 計画 ADR: ADR-0095 決定 3（即時同期）、ADR-0104（供給元の表示）、ADR-0110 決定 3（書き込みの確認＝再起動の確認）。
- 関連 IADR（決定は変えない。手順が引くだけ）:
  - IADR-0454 フォローアップ 1: 書き込み応答の `created_time` と metadata の `versions[n].created_time` の一致を T-40 と同じ場で確かめる。
  - IADR-0456 フォローアップ 2: force-sync で数秒以内に Secret が変わる・Reloader が消費側を作り直す・生成した鍵を OpenD が読める。
  - IADR-0460: 供給元（`screen` / `git` / `unknown`）の判定。テスト仕様書 §未決事項の「2 通りの配備で切り替わる」。
  - IADR-0494: bootstrap の force-sync の完了条件（促す前と違う `refreshTime` ＋ `Ready=True`）。本手順の「同期の完了」の判定はこれに揃える。
- **新しい IADR は起こさない。** 本作業は手順書（docs）の追加であり、実装の判断を変えない。試験対象の選び方（下の決定 2）は
  運用手順の選択であって設計判断ではない。

## 何が足りないか

テスト仕様書 `docs/tests/SC-22_secret-item-management.md` の T-40 は「画面から 1 プロパティを更新し、同期後の Secret を長さだけで確かめる」の 1 行だけで、
次が無い（PoC のセッションが求めた欠落）:

1. 前提（`VAULT=1 ESO=1` で立てた環境・何が Ready なら始めてよいか）
2. どのプロパティを書くか（安全に変えられ、戻し方があるもの。サインイン・s2s・データストアを壊さない。#1682 で対になる秘密は画面の対象外になった）
3. 値を出さずに長さだけを測るコマンド（末尾の改行の扱い）
4. 同期の完了の判定（`refreshTime` の前後・`Ready`・`force-sync` の注釈）
5. 同居するキーの数え方（前後）
6. `created_time` の比較の手順
7. Reloader の作り直しの確認
8. 元の値への戻し方
9. 止める条件
10. 記録の表と記録先

## 決定

1. **置き場は新しい Runbook `docs/operations/secret-item-live-sync-check-runbook.md`**（`docs/templates/runbook_template.md` から作る）。
   既存の `secret-item-console-injection-runbook.md` は「画面が使えないときの退避」であり、目的が違う（混ぜると「画面を使う手順」と「画面を使わない手順」が 1 冊に入る）。
   テスト仕様書の T-40 の行と §未決事項、運用仕様書 `operations.md` の秘密情報の節から可視リンクで張る（いずれも `docs/` 内のリンク）。
2. **試験対象は `llm-provider-credentials` の `openai-api-key`**（既定）。根拠（リポジトリの実測）:
   - `git grep -n openai-api-key` の読み手は無い。llmgateway は `anthropic-api-key` だけを `Llm__ApiKey` で読む（`deploy/helm/microservices-platform/values.yaml`・`deploy/local/values-local.yaml`）。
     → 試験値を入れても機能は変わらない。
   - 同じ Secret に `anthropic-api-key` が同居する（`deploy/local/vault/eso/externalsecret-llm.yaml` の `data[]` が 2 件）→ 「同居するキーが減っていない」を確かめられる。
   - 消費側 llmgateway-service に Reloader の注釈がある（`values-local.yaml`）→ Secret の data が変われば作り直される（読まないキーでも Secret のハッシュが変わる）。
   - 同じ MSP 名前空間に同期先の ExternalSecret があり、BFF の同期依頼の Role（チャートの `bff-externalsecret-sync`）の `resourceNames` に入っている。
   - 対になる秘密（`*-auth-client-*`・`deferred[]`・`excluded[]`）でない。サインイン・s2s・データストアに触れない。
   - 戻し方: 書く前の版番号を控え、`vault kv rollback -version=<控えた版>`（Vault Pod 内。値を表示しない）。前例は `paired-secret-rotation-runbook.md`。
     元の値が空でも戻せる（画面は空の値を書けない＝T-21 なので、画面では戻せない場合がある）。
   - 却下: `keycloak-smtp`（`from` を変えると送信が壊れ得る・構成値は bootstrap が揃え直す）、`wikijs-sync`（Wiki 同期が止まる）、
     `ast-app-secrets`（AST の配備が前提・対になる秘密が同居し、事故の影響が s2s に及ぶ）、moomoo 系（OpenD の再認証が要る）。
     ただし OpenD の鍵の確認（IADR-0456 フォローアップ 2）と供給元の切り替え（IADR-0460）は、AST を配備している場でだけ行う**任意の節**として置く。
3. **長さは `kubectl get secret -o jsonpath='{.data.<key>}' | base64 -d | wc -c`**。`jsonpath` に `{"\n"}` を足さない。期待値は `printf '%s' "$PROBE" | wc -c`
   （`echo` は改行を足して 1 多くなる）。キーが無い場合も 0 になるので、キー名の一覧（`go-template`。値を読まない。IADR-0494 の追記と同じ書き方）と組で見る。
4. **同期の完了**＝書く前と違う `status.refreshTime` ＋ `Ready=True`（IADR-0494 決定 4 と同じ条件）。所要時間は `force-sync` の注釈（BFF が付けた unix 秒）と `refreshTime` の差で測る。
   合否: 書く前と違う `refreshTime` に変わらないまま 120 秒（bootstrap の `ESO_FORCE_SYNC_TIMEOUT` の既定）を超えたら不合格。数秒（10 秒以内）なら設計どおり、それを超えて 120 秒以内なら合格だが逸脱として記録する。
5. **`created_time` の確認は 2 段**:
   (a) 画面の一覧で「最終更新者」が自分の名前になること（BFF が `record.UpdatedAt == versions[current].created_time` を満たしたときだけ名前を出す＝一致の機能上の証明）。
   (b) 文字列の一致: 戻しの `vault kv rollback -format=json` の応答 `data.created_time` と、直後の `vault kv metadata get -format=json` の `versions["<新しい版>"].created_time` を並べて見る（どちらも値を含まない）。
   補助: ブラウザの開発者ツールで PUT 応答の `updatedAt`（.NET の 7 桁）と metadata（Vault の 9 桁）を 7 桁まで突き合わせる。
6. **記録先は #1472 へのコメント**（T-40 の残作業を持つ issue）。**値・値の長さ・値の一部は書かない**（一致したか否かとキーの数・名前だけ）。
   戻しは root トークンでの書き込みとして保管先の audit に残るので、記録に戻した時刻と版を書いて突き合わせられるようにする。
   退避手段の記録先（#458）には書かない（本手順は退避ではない）。実施後はテスト仕様書の T-40 の区分を更新する。
7. 止める条件を表で持つ（前提が満たせない・書き込みが 200 でない・キーが減った・消費側が Ready に戻らない・同期が 120 秒で終わらない 等）。

## 母集合（規則 9: 追随する文書を走査で引く）

走査は 2026-10-04・`82b864f8`。

- `git grep -n "T-40" -- docs ':!docs/tests/SC-22_secret-item-management.md'` → 23 行。**すべて他の試験仕様書の別の T-40**（FR-01・FR-03・SC-17 等。番号は文書ごと）で、SC-22 の T-40 を指すものは 0 行。
- 同じテスト仕様書の中: §テスト対象・範囲の 2 行（実 Vault の疎通・同期と作り直し）、テストケース一覧の T-40 の行、§未決事項の 2 項。
- `git grep -n "稼働クラスタ" -- docs/screens/SC-22_secret-item-management.md docs/operations/operations.md` → 画面仕様書 222 行（「供給元の判定は稼働クラスタで未実測。テスト仕様書の手動の項」）は
  テスト仕様書を指すだけで、手順への追随は要らない（文言は変わらず正しい）。運用仕様書の 7 行はいずれも別の機能。
- 結果と扱い:

| 文書 | 該当 | 扱い |
| --- | --- | --- |
| `docs/tests/SC-22_secret-item-management.md` | T-40 の行・§テスト対象・範囲・§未決事項 | **リンクを足す**（区分は「手動（未実施）」のまま） |
| `docs/operations/operations.md` | 秘密情報の節（画面が既定の投入面） | **リンクを 1 文足す** |
| `docs/operations/secret-item-console-injection-runbook.md` | T-40 の言及なし | 触らない（目的が違う） |
| `docs/screens/SC-22_secret-item-management.md` | 222 行がテスト仕様書の手動の項を指す | 触らない（テスト仕様書からリンクで辿れる） |
| `.ai-context/adr/IADR-0454*`・`IADR-0456*` | フォローアップが T-40 の場を指す | **触らない**（凍結記録。手順書の側から引く） |

## 受け入れ基準

- [x] 新しい Runbook が前提・対象の選び方・長さだけの測り方・同期の完了・同居キー・`created_time`・Reloader・戻し方・止める条件・記録の表を持つ。
- [x] コマンドの名前空間・ExternalSecret 名・Secret 名・Vault のパス・注釈のキー・画面のルートとロールがリポジトリの実物に一致する。
- [x] どのコマンドも値を表示しない（試験値の生成を除く。試験値は使い捨てで秘密ではない）。
- [x] テスト仕様書の T-40 と運用仕様書から可視リンクで辿れる。`updated:` は 2026-10-04。
- [x] 文書検査（trace ブロック・リンク・updated・相互参照・計画 ID の修飾・知識グラフ）とコミット検査・gitleaks が通る。

## 限界

- 手順は稼働クラスタで実測していない（作業条件）。初回の実施で食い違いが出たら Runbook を直す。
- `vault kv rollback -format=json` の応答に `created_time` が含まれることは CLI の仕様からの想定であり、実測していない。含まれなければ (a) だけで判定し、その旨を記録する手順にした。
