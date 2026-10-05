# SC-22, NFR-18, 計画 ADR-0126 決定 2（ADR-0095 決定 3 の補完）, IADR-0501 決定 2 (#458 段 S2):
# BFF が SC-22 の**群**（実行時に成員が増える項目。いまは「データソースの資格情報」）へ書くための policy
# （role `bff-secret-writer` に、項目ごとの policy `bff-secret-write` と並べて付与する）。
#
# 🔴 群の書き込みの射程は 2 つの層で限る（ADR-0126 決定 2）。この policy は**権限の層**の側である。
#    - 専用接頭辞の下の **1 セグメント（`+`）だけ**。`*` を使わない —— `datasource/<ID>/<さらに下>` へは書けない。
#      `msp/*`（基盤の秘密）・`ai-stock-trading/*` には一致しない。
#    - 「登録済みの ID」はこの policy では限れない（接頭辞の内側の任意の 1 セグメントへ書ける）。限るのは BFF のコード
#      （`SecretItemGroupBffEndpoints` が成員に無い ID へは書かない）。ADR-0126 §結果 の受容したトレードオフである。
# 🔴 data には `create`・`patch` だけ（`read` も全置換の `update` も無い。静的な項目と同じ。IADR-0453 決定 10）。
# 🔴 metadata は `read` だけ（版・作成時刻。値を持たない。主要素 3 の「未設定」と最終更新日時に要る）。
#    `list` / `delete` / `destroy` はどこにも与えない —— 成員の一覧は可変ユニットから引き、Vault を列挙しない。
#    無効化したデータソースの値は Vault に残る（ADR-0126 フォローアップ 4。消す手段はまだ定めていない）。
#
# 接頭辞の集合の単一情報源は deploy/bootstrap/sc22-secret-items.json の `groups[].vaultPathPrefix` である。
# 本ファイルの path 集合がそれと完全一致することを Platform.Bff.Tests（SecretItemGroupVaultPolicyTests）が固定する。

# datasource-credentials（データソースの資格情報。datasource-service が IADR-0495 の read だけの role で読む）
path "secret/data/datasource/+" {
  capabilities = ["create", "patch"]
}
path "secret/metadata/datasource/+" {
  capabilities = ["read"]
}
