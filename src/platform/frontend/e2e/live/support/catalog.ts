// NFR / ADR-0031 / IADR-0627（#2028）: live の spec が画面の文言を**製品の ja カタログから**引くための部品。
// live の spec に文言を写すと、カタログの変更が live（nightly・PR の e2e-live）でしか見つからない。カタログの msgid を
// 鍵にして msgstr を引き、msgid が無ければ**読み込みで例外**にする —— L1 の `live-opt-in.smoke.spec.ts` が live の spec を
// `--list` で読み込むので、文言の食い違いは L1 で赤になる。
import { readFileSync } from 'node:fs';

const CATALOG = new URL('../../../src/locales/ja/messages.po', import.meta.url);

/** `.po` の文字列リテラル（`"..."`）を外す。エスケープは `\"` と `\\` と `\n` だけを戻す。 */
function unquote(literal: string): string {
  return literal.slice(1, -1).replace(/\\(["\\n])/g, (_, c: string) => (c === 'n' ? '\n' : c));
}

/** msgid → msgstr（複数行の連結を含む）。純関数。 */
export function parsePo(text: string): Map<string, string> {
  const entries = new Map<string, string>();
  let field: 'msgid' | 'msgstr' | null = null;
  let id = '';
  let str = '';
  const flush = () => {
    if (id !== '') entries.set(id, str === '' ? id : str);
    id = '';
    str = '';
  };
  for (const raw of text.split('\n')) {
    const line = raw.trim();
    const head = /^(msgid|msgstr) (".*")$/.exec(line);
    if (head) {
      if (head[1] === 'msgid') flush();
      field = head[1] as 'msgid' | 'msgstr';
      if (field === 'msgid') id = unquote(head[2]);
      else str = unquote(head[2]);
      continue;
    }
    if (/^".*"$/.test(line) && field !== null) {
      if (field === 'msgid') id += unquote(line);
      else str += unquote(line);
      continue;
    }
    field = null;
  }
  flush();
  return entries;
}

let cache: Map<string, string> | null = null;
const catalog = () => (cache ??= parsePo(readFileSync(CATALOG, 'utf8')));

/** ja の文言。`{name}` を `vars` で埋める。msgid がカタログに無ければ例外（文言を写して食い違わせない）。 */
export function ja(msgid: string, vars: Record<string, string> = {}): string {
  const msgstr = catalog().get(msgid);
  if (msgstr === undefined) throw new Error(`ja のカタログに msgid が無い: ${msgid}`);
  return msgstr.replace(/\{(\w+)\}/g, (whole, name: string) => vars[name] ?? whole);
}

/** ja の文言を、`{name}` を数字（`\d+`）として受ける正規表現にする（件数が実行ごとに変わる文言）。全体一致。 */
export function jaPattern(msgid: string): RegExp {
  const escaped = ja(msgid).replace(/[.*+?^$()|[\]\\]/g, '\\$&');
  return new RegExp(`^${escaped.replace(/\{\w+\}/g, '\\d+')}$`);
}
