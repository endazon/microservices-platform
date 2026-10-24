// platform ログインテーマ: 親テンプレート（keycloak.v2）のラベルの対応づけの欠陥を、描画後に補う
// （NFR-30・SC-13〜15・IADR-0619。テンプレートは複製しない＝IADR-0532 決定 1）。
//
// keycloak.v2（26.7.4）の一部の画面は、<label for="…"> の指す id が入力欄と食い違う。支援技術は入力欄の名前を読めず、
// axe は `label` 違反を出す（#2191 の実測）:
//   - login-config-totp.ftl（SC-14 初回登録）: ワンタイムコードとデバイス名の 2 つのラベルが、どちらも実在しない
//     `form-vertical-name` を指す（入力欄は #totp / #userLabel）。
//   - template.ftl の username マクロ（SC-14 ワンタイムコード等、利用者名を確定した後の画面）: ラベルは `username` を
//     指すが、読み取り専用の入力欄は #kc-attempted-username である。
//
// 補い方: `for` の指す要素が**実在しない**ラベルに限り、同じフォームグループの中に名前の付く入力欄がちょうど 1 つ
// あれば、そこへ付け替える。正しく対応づいたラベルには触れない —— 親が直れば本スクリプトは何もしなくなる。
// 外部への通信は無い（08_data-egress-policy）。インラインではなくファイルで読む（Keycloak の CSP の script-src 'self'）。
(function () {
  'use strict';

  var LABELABLE = 'input:not([type=hidden]):not([type=submit]):not([type=button]), select, textarea';

  function repair() {
    var labels = document.querySelectorAll('label[for]');
    for (var i = 0; i < labels.length; i++) {
      var label = labels[i];
      if (document.getElementById(label.htmlFor)) continue;
      var group = label.closest('.pf-v5-c-form__group');
      if (!group) continue;
      var controls = group.querySelectorAll(LABELABLE);
      if (controls.length !== 1 || !controls[0].id) continue;
      label.htmlFor = controls[0].id;
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', repair);
  } else {
    repair();
  }
})();
