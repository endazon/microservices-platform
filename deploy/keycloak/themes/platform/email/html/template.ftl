<#-- SC-15 / ADR-0045 / IADR-0532: メール（HTML 版）の外枠。本文（<#nested>）は親テーマの各テンプレートが出す。
     色は src/packages/ui/src/styles.css のライトの意味トークンの値（bg / surface / border / fg / brand）だけを使う
     （scripts/gen-keycloak-theme-tokens.js --check が確かめる）。画像・外部の CSS・フォントは使わない。 -->
<#macro emailLayout>
<html lang="${locale.language}" dir="${(ltr)?then('ltr','rtl')}">
<body style="margin:0;padding:0;background-color:#f3f5fe;">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#f3f5fe;">
  <tr>
    <td align="center" style="padding:32px 16px;">
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:520px;">
        <tr>
          <td style="padding:0 4px 16px;font-family:system-ui,-apple-system,'Segoe UI','Hiragino Kaku Gothic ProN','Yu Gothic','Meiryo',sans-serif;font-size:16px;font-weight:500;color:#1b1a17;">
            <span style="display:inline-block;width:9px;height:9px;border-radius:2px;background-color:#5d5294;margin-right:8px;vertical-align:middle;"></span><span style="vertical-align:middle;">${realmName}</span>
          </td>
        </tr>
        <tr>
          <td style="background-color:#ffffff;border:1px solid #cfd3e5;border-radius:14px;padding:24px;font-family:system-ui,-apple-system,'Segoe UI','Hiragino Kaku Gothic ProN','Yu Gothic','Meiryo',sans-serif;font-size:14px;line-height:1.7;color:#1b1a17;">
            <#nested>
          </td>
        </tr>
      </table>
    </td>
  </tr>
</table>
</body>
</html>
</#macro>
