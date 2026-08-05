import { describe, expect, it } from 'vitest';
import { okData } from './orvalSelect';
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
