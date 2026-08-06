import { describe, expect, it } from 'vitest';
import { okArray, okData } from './orvalSelect';
import type { OkPayload } from './orvalSelect';

// SC-01〜SC-11, IADR-0135 決定 1: 生成フックの応答封筒を剥がす `select` の規約を固定する。
//
// ここが壊れると 9 画面すべてが「本文の代わりに封筒を読む」形になる（画面は `undefined` を描くだけで
// 例外にはならないため、テストが無いと静かに壊れる）。

/** 生成物と同じ形の応答 union（成功 200 ＋ 宣言されたエラー枝）。 */
type SampleResponse =
  | ({ data: { title: string }; status: 200 } & { headers: Headers })
  | ({ data: void; status: 404 } & { headers: Headers });

describe('foundation/api/orvalSelect', () => {
  it('unwraps the success payload from the generated envelope', () => {
    const res = {
      data: { title: '経費精算マニュアル' },
      status: 200,
      headers: new Headers(),
    } as SampleResponse;

    const payload: OkPayload<SampleResponse> = okData(res);

    // 型の上でも実行時にも、封筒ではなく本文が出る。
    expect(payload).toEqual({ title: '経費精算マニュアル' });
    expect(payload.title).toBe('経費精算マニュアル');
  });

  it('keeps the reference identity of the payload (no copying)', () => {
    const body = { title: 'a' };
    const res = { data: body, status: 200, headers: new Headers() } as SampleResponse;

    // TanStack Query の `select` は再描画のたびに呼ばれる。ここで新しいオブジェクトを作ると
    // 参照が毎回変わり、`data` を依存に持つ memo が効かなくなる。
    expect(okData(res)).toBe(body);
  });
});

// SC-03 / SC-09 / SC-11, IADR-0135 決定 7［2026-08-06 追記］: 配列を返す面の `select` の規約。
//
// **`okData(res) ?? []` は空ボディを守らない**——`bffFetch` は本文が空のとき `{}` を返すため
// `{} ?? []` は `{}` のままで、`??` は発火しない（載せ替え前は `apiFetch` が `undefined` を
// 返していたので発火していた）。この差が「空ボディでルートごとクラッシュ」という退行になった。
type ArrayResponse =
  | ({ data: { version: number }[]; status: 200 } & { headers: Headers })
  | ({ data: void; status: 404 } & { headers: Headers });

const envelope = (data: unknown) =>
  ({ data, status: 200, headers: new Headers() }) as ArrayResponse;

describe('foundation/api/orvalSelect okArray', () => {
  it('returns the payload as-is when the body is an array', () => {
    const body = [{ version: 3 }, { version: 2 }];

    // 参照も保つ（`okData` と同じ理由: `select` は再描画のたびに呼ばれる）。
    expect(okArray(envelope(body))).toBe(body);
  });

  it('degrades an empty body ({}) to an empty array', () => {
    // `bffFetch` が 204 / 205 / 304 と空ボディで作る値そのもの。**`??` では守れない形である。**
    expect(okData(envelope({}))).toEqual({});
    expect(okArray(envelope({}))).toEqual([]);
  });

  it('degrades any non-array payload to an empty array', () => {
    // JSON として妥当でも配列でない本文（後段の取り違え・プロキシの差し込み）で `.map` を割らない。
    expect(okArray(envelope(null))).toEqual([]);
    expect(okArray(envelope('unexpected'))).toEqual([]);
    expect(okArray(envelope({ items: [] }))).toEqual([]);
  });
});
