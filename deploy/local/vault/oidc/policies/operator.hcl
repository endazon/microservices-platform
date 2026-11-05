# IADR-0094 (#353): dev Vault の運用者ポリシー（realm ロール platform-operator → external group 経由で付与）。
# NFR-18, IADR-0681 (#2421): **要る path と操作だけに絞る。** 運用者の役は「どの秘密が在るか・いつ変わったか」を見ることである。
#   秘密の値の投入・更新は SC-22 の画面（BFF の役。policy-bff-secret-*.hcl）が行い、運用者は秘匿管理の画面で値を読まない。
#   したがって KV の値（secret/data/）は与えず、メタデータ（版・更新の日時）の read と一覧だけを、使っている 3 つの接頭辞に限って与える。
#   値を読む必要があるときは管理者（platform-admin）が行う。書き込み・管理系は与えない（従前どおり）。
#   接頭辞を足したら（新しい KV の置き場を作ったら）ここも足す。scripts/scripts.repo.test.js が形を固定する。
path "secret/metadata/" {
  capabilities = ["list"]
}
path "secret/metadata/msp/*" {
  capabilities = ["read", "list"]
}
path "secret/metadata/ai-stock-trading/*" {
  capabilities = ["read", "list"]
}
path "secret/metadata/datasource/*" {
  capabilities = ["read", "list"]
}
path "sys/mounts" {
  capabilities = ["read"]
}
