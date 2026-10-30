// SC-22 / NFR-18 / IADR-0659（#2240）: **React の制御された入力欄へ秘密の値を入れる**口。
//
// - 🔴 `locator.fill(value)` は値を step の題に載せ、html レポートに残す（`support/login.ts` の fillSecret と同じ理由）。
//   `evaluate` の引数は題に載らないので、値をページの中で入れる。
// - React の制御された入力欄は、`input.value = v` の代入では変化に気づかない（React が値の追跡に使う setter を通らない）。
//   `HTMLInputElement.prototype` の `value` の setter を直に呼んでから `input` を起こす。
import { expect, type Locator } from '@playwright/test';

export async function fillSecretInReactInput(locator: Locator, value: string): Promise<void> {
  await expect(locator).toBeEditable();
  await locator.evaluate((el, v) => {
    const g = globalThis as unknown as {
      HTMLInputElement: { prototype: object };
      Event: new (t: string, o: object) => unknown;
    };
    const setter = Object.getOwnPropertyDescriptor(g.HTMLInputElement.prototype, 'value')?.set;
    if (!setter) throw new Error('HTMLInputElement の value の setter が無い');
    setter.call(el, v);
    (el as unknown as { dispatchEvent: (e: unknown) => boolean }).dispatchEvent(
      new g.Event('input', { bubbles: true }),
    );
  }, value);
}
