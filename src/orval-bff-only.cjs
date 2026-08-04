/*
 * ADR-0031 / IADR-0121 決定 3: orval の入力から BFF 以外のパスを落とす前処理。
 *
 * `docs/api/openapi.yaml` は BFF（`/bff/` 配下）とサービス直接 API（`/documents`・`/authz/`・
 * `/dashboard/` 配下 …）を 1 ファイルに束ねている。SPA が触れてよいのは BFF だけである
 * （BFF 境界。IADR-0033 決定 5 → IADR-0121 決定 3 が継承）。生成の段階で落としてしまえば、
 * 「サービスを直接叩く関数がそもそも存在しない」状態になり、レビューの見落としが起こり得ない。
 *
 * orval の `input.filters` を使わない理由（実測）: filters はタグ／スキーマ単位でしか効かず、
 * `/feedback` と `/bff/feedback`、`/dashboard/summary` と `/bff/dashboard/summary` のように
 * **同一タグに BFF と非 BFF が混在する**ため、タグでは切り分けられない。
 */
const BFF_PREFIX = '/bff/';

module.exports = (spec) => {
  const paths = {};
  for (const [p, item] of Object.entries(spec.paths ?? {})) {
    if (p.startsWith(BFF_PREFIX)) paths[p] = item;
  }
  if (Object.keys(paths).length === 0) {
    // BFF のパスが 1 本も無いのは「OpenAPI の構成が変わった」合図である。空の生成物を静かに
    // 出すと、手書きクライアントへ逆戻りする口実になるため、ここで止める。
    throw new Error(
      `[orval-bff-only] ${BFF_PREFIX} で始まるパスが OpenAPI に 1 本もありません。` +
        'BFF 境界（IADR-0121 決定 3）の前提が崩れています。docs/api/openapi.yaml を確認してください。',
    );
  }
  return { ...spec, paths };
};
