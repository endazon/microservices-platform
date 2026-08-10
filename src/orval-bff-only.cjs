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
 *
 * IADR-0131 決定 4（#506）: **SSE（`text/event-stream`）の操作も落とす。**
 * orval は SSE を扱えない（TanStack Query のフックは 1 回の応答を前提とする）が、**黙って落ちるのではなく
 * “動きそうに見える” mutation フックを生成してしまう**（実測: `/bff/analysis/ask/stream` に対し
 * `useBffAnalysisAskStream` が生成され、mutator が本文を全部読んでから `JSON.parse` するため
 * ストリーミングにならず SSE 本文で例外になる）。**罠を置かないため生成の段階で落とす。**
 * 同時に OpenAPI 側には宣言を**残せる**ようになり、「載せ忘れ」と「意図的な対象外」を
 * 契約ファイル 1 か所で区別できる（区別の一覧は docs/api/BFF_bff-surface.md）。
 *
 * ★ 落とすのは `paths` **だけ**である。`components.schemas` は素通りする（#520 の変異試験 M8・#558）。
 * 本ファイル末尾の `module.exports = (spec) => { … }` は `return { ...spec, paths }` であり、
 * `components` には一切触れていない（**この前処理に名前付き関数は無い**——`grep` で探すときは
 * `module.exports` を見ること）。
 * つまり **BFF が使わないスキーマも `bff.schemas.ts` へ出力される**（宣言数と生成数は一致する）。
 *   $ grep -c '^export interface ' platform/frontend/src/foundation/api/generated/bff.schemas.ts
 * **ここに件数を書かない。** 契約が増えれば動くためで、実際 #558 が引用した #520 時点の件数は
 * 起票から本コメント追記までの間に古くなっていた。**数えたいときは上のコマンドで数える。**
 *
 * **帰結**: 「型が生成されていない」ことを当てにした網は張れない。**網の有無を決めるのは
 * 「生成されるか」ではなく「画面がその型を読んでいるか」である**（IADR-0132。
 * 区別の一覧は docs/api/BFF_bff-surface.md §横断の規約 5）。
 *
 * この除外規則に専用の単体テストは置かない。**生成物がコミットされ、CI が再生成差分を検査する**
 * （`pnpm run codegen && git diff --exit-code -- …/generated`）ため、除外が壊れた瞬間に
 * `useBffAnalysisAskStream` が生成物へ現れて差分検査が落ちる——規則そのものが成果物で固定されている。
 */
const BFF_PREFIX = '/bff/';
const SSE_MEDIA_TYPE = 'text/event-stream';
const JSON_MEDIA_TYPE = 'application/json';

// OpenAPI の path item のうち、操作（operation）を表すキー。`parameters` / `$ref` / `summary` 等は除く。
const HTTP_METHODS = ['get', 'put', 'post', 'delete', 'options', 'head', 'patch', 'trace'];

/**
 * その操作が SSE 専用か（= 生成対象外か）。
 * 「SSE の応答を持ち、かつ JSON の応答を 1 つも持たない」を判定条件にする。JSON も返す操作
 * （content negotiation で両方を提供する形）は生成できるため落とさない。
 */
function isServerSentEventsOnly(operation) {
  const responses = Object.values(operation?.responses ?? {});
  let hasSse = false;
  for (const response of responses) {
    const content = response?.content ?? {};
    if (Object.prototype.hasOwnProperty.call(content, SSE_MEDIA_TYPE)) hasSse = true;
    if (Object.prototype.hasOwnProperty.call(content, JSON_MEDIA_TYPE)) return false;
  }
  return hasSse;
}

/** path item から SSE 専用の操作を取り除く。1 つも操作が残らなければ null を返す（パスごと落とす）。 */
function withoutSseOperations(item) {
  const kept = { ...item };
  let operations = 0;
  for (const method of HTTP_METHODS) {
    if (!kept[method]) continue;
    if (isServerSentEventsOnly(kept[method])) delete kept[method];
    else operations++;
  }
  return operations > 0 ? kept : null;
}

module.exports = (spec) => {
  const paths = {};
  for (const [p, item] of Object.entries(spec.paths ?? {})) {
    if (!p.startsWith(BFF_PREFIX)) continue;
    const kept = withoutSseOperations(item);
    if (kept) paths[p] = kept;
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
