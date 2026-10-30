# NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, IADR-0663 決定 3 (#458 段 S4): データソースの設定の平文の資格情報を
# Vault へ移す**一回きりの移送**の権限。role には束縛しない —— scripts/migrate-datasource-credentials.sh が、この policy の
# **短命のトークン**（既定 10 分・使用回数の上限つき）を発行して移送のコマンドへ標準入力で渡し、終わったら失効させる。
#
# 🔴 専用接頭辞の 1 セグメント（`+`）だけ。`*`（入れ子）・list・delete・destroy は与えない。
#    - data の `read` は、同じパスの既存のキーを残して書き足すため（読んだ版で check-and-set）と、書いた後の読み直しのため。
#    - metadata の `read` は、現在版が削除・破棄されたパスの版を check-and-set のために読むため（値は持たない）。
# datasource-service の常用の role（datasource-connector-reader。read だけ）は変えない。
path "secret/data/datasource/+" {
  capabilities = ["create", "update", "read"]
}

path "secret/metadata/datasource/+" {
  capabilities = ["read"]
}
