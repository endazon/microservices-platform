'use strict';
/*
 * yaml-subset.js — NFR / ADR-0107 決定 4 / #1841
 *
 * **描画済みのマニフェスト（kustomize と `helm template` の出力）と compose のサービス定義を読むための、
 * YAML の部分集合のパーサ**である。本リポジトリの検査器は Node 標準モジュールだけで動かす方針であり、
 * 外部の YAML パーサを持ち込まない（`check-grafana-provisioning-parity.js` と同じ方針）。
 *
 * 扱うもの: 複数文書（`---`）・ブロックのマッピング／シーケンス（親の鍵と同じ字下げのシーケンスを含む）・
 * フローのマッピング／シーケンス（`{ name: X, value: "true" }`・`["-a", "-b"]`）・プレーン／一重引用／二重引用の
 * スカラー（二重引用の複数行の折り返しとエスケープ `\n` `\"` `\U0001F534` 等を含む）・ブロックスカラー（`|` `|-` `|+`）・
 * アンカー（`&x` は読み捨てる）・コメント。
 *
 * 🔴 **扱わないものは例外で止める（fail-closed）**: エイリアス（`*x`）・マージキー（`<<`）・折り畳みのブロックスカラー（`>`）・
 * 複雑な鍵（`? `）・タグ（`!!`）。**読めないものを黙って空として返すと、検査が何も検査しないまま緑になる**ため。
 * 値はすべて文字列として返す（`true` / `3000` も文字列。型の解釈は呼び出し側が行う）。null は `null`。
 */

class YamlSubsetError extends Error {}

/** 行の前処理: 字下げと本文（行末コメントはここでは剥がさない — 引用の中の `#` を壊さないため）。 */
function toLines(text) {
  return text.replace(/\r\n?/g, '\n').split('\n').map((raw, no) => {
    const m = /^( *)(.*)$/.exec(raw);
    return { no: no + 1, indent: m[1].length, body: m[2], raw };
  });
}

const isBlankOrComment = (l) => l.body.trim() === '' || /^#/.test(l.body.trim());

/** プレーンスカラーの行末コメント（空白＋#）を剥がす。 */
function stripPlainComment(s) {
  const m = /(^|\s)#/.exec(s);
  return (m ? s.slice(0, m.index) : s).trim();
}

/** 二重引用スカラーの中身（引用符の内側。改行を含み得る）を復号する。 */
function decodeDoubleQuoted(inner) {
  // 折り返し: 改行（とその前後の空白）は空白 1 つ。空行は改行。行末の `\` は折り返しを打ち消す。
  const parts = inner.split('\n');
  let folded = '';
  for (let i = 0; i < parts.length; i += 1) {
    let seg = parts[i];
    if (i > 0) seg = seg.replace(/^[ \t]+/, '');
    if (i < parts.length - 1) {
      // 行末の奇数個のバックスラッシュ → 改行のエスケープ（区切りを足さない）
      const tail = /(\\+)$/.exec(seg);
      if (tail && tail[1].length % 2 === 1) {
        folded += seg.slice(0, -1);
        continue;
      }
      seg = seg.replace(/[ \t]+$/, '');
      folded += seg;
      // 続く空行の数だけ改行、無ければ空白 1 つ
      let blanks = 0;
      while (i + 1 < parts.length - 1 && parts[i + 1].trim() === '') {
        blanks += 1;
        i += 1;
      }
      folded += blanks > 0 ? '\n'.repeat(blanks) : ' ';
    } else {
      folded += seg;
    }
  }
  const simple = { 0: '\0', a: '\x07', b: '\b', t: '\t', '\t': '\t', n: '\n', v: '\v', f: '\f', r: '\r', e: '\x1b',
    ' ': ' ', '"': '"', '/': '/', '\\': '\\', N: '\u0085', _: ' ', L: ' ', P: ' ' };
  let out = '';
  for (let i = 0; i < folded.length; i += 1) {
    const c = folded[i];
    if (c !== '\\') {
      out += c;
      continue;
    }
    const e = folded[i + 1];
    if (e in simple) {
      out += simple[e];
      i += 1;
    } else if (e === 'x' || e === 'u' || e === 'U') {
      const len = e === 'x' ? 2 : e === 'u' ? 4 : 8;
      const hex = folded.slice(i + 2, i + 2 + len);
      if (!/^[0-9a-fA-F]+$/.test(hex) || hex.length !== len) throw new YamlSubsetError(`不正なエスケープ \\${e}${hex}`);
      out += String.fromCodePoint(parseInt(hex, 16));
      i += 1 + len;
    } else {
      throw new YamlSubsetError(`未対応のエスケープ \\${e}`);
    }
  }
  return out;
}

/** 一重引用スカラーの中身を復号する（`''` → `'`、折り返しは二重引用と同じ）。 */
function decodeSingleQuoted(inner) {
  const parts = inner.split('\n');
  let out = '';
  for (let i = 0; i < parts.length; i += 1) {
    let seg = i > 0 ? parts[i].replace(/^[ \t]+/, '') : parts[i];
    if (i < parts.length - 1) {
      seg = seg.replace(/[ \t]+$/, '');
      out += seg;
      let blanks = 0;
      while (i + 1 < parts.length - 1 && parts[i + 1].trim() === '') {
        blanks += 1;
        i += 1;
      }
      out += blanks > 0 ? '\n'.repeat(blanks) : ' ';
    } else {
      out += seg;
    }
  }
  return out.replace(/''/g, "'");
}

/**
 * 引用スカラーの終端を探す（複数行にまたがり得る）。`text` は値の開始位置からの残り全体（改行で連結済み）。
 * 戻り値: [復号値, 消費した文字数]
 */
function readQuoted(text) {
  const q = text[0];
  for (let i = 1; i < text.length; i += 1) {
    if (q === '"' && text[i] === '\\') {
      i += 1;
      continue;
    }
    if (text[i] === q) {
      if (q === "'" && text[i + 1] === "'") {
        i += 1;
        continue;
      }
      const inner = text.slice(1, i);
      return [q === '"' ? decodeDoubleQuoted(inner) : decodeSingleQuoted(inner), i + 1];
    }
  }
  throw new YamlSubsetError('引用が閉じていない');
}

/** フローのコレクション（`[...]` / `{...}`）を読む。戻り値: [値, 消費した文字数] */
function readFlow(text) {
  let i = 0;
  const ws = () => {
    while (i < text.length && /[\s]/.test(text[i])) i += 1;
    if (text[i] === '#') throw new YamlSubsetError('フローの中のコメントは未対応');
  };
  const scalar = (terminators) => {
    ws();
    if (text[i] === '"' || text[i] === "'") {
      const [v, n] = readQuoted(text.slice(i));
      i += n;
      return v;
    }
    if (text[i] === '[' || text[i] === '{') return value();
    let s = '';
    while (i < text.length && !terminators.includes(text[i])) {
      s += text[i];
      i += 1;
    }
    s = s.replace(/\s+/g, ' ').trim();
    if (s.startsWith('*')) throw new YamlSubsetError('エイリアスは未対応');
    if (s.startsWith('!')) throw new YamlSubsetError('タグは未対応');
    if (s === '' || s === '~' || s === 'null') return null;
    return s;
  };
  const value = () => {
    ws();
    const open = text[i];
    if (open === '[') {
      i += 1;
      const arr = [];
      ws();
      if (text[i] === ']') {
        i += 1;
        return arr;
      }
      for (;;) {
        arr.push(scalar([',', ']']));
        ws();
        if (text[i] === ',') {
          i += 1;
          ws();
          if (text[i] === ']') {
            i += 1;
            return arr;
          }
          continue;
        }
        if (text[i] === ']') {
          i += 1;
          return arr;
        }
        throw new YamlSubsetError('フローのシーケンスが閉じていない');
      }
    }
    if (open === '{') {
      i += 1;
      const obj = {};
      ws();
      if (text[i] === '}') {
        i += 1;
        return obj;
      }
      for (;;) {
        const key = scalar([':', ',', '}']);
        if (text[i] !== ':') throw new YamlSubsetError('フローのマッピングに `:` が無い');
        i += 1;
        obj[key] = scalar([',', '}']);
        ws();
        if (text[i] === ',') {
          i += 1;
          ws();
          if (text[i] === '}') {
            i += 1;
            return obj;
          }
          continue;
        }
        if (text[i] === '}') {
          i += 1;
          return obj;
        }
        throw new YamlSubsetError('フローのマッピングが閉じていない');
      }
    }
    throw new YamlSubsetError('フローのコレクションではない');
  };
  const v = value();
  return [v, i];
}

/** `key: rest` の分解。鍵が引用されていても良い。マッピング行でなければ null。 */
function splitKey(body) {
  let key;
  let rest;
  if (body[0] === '"' || body[0] === "'") {
    const [k, n] = readQuoted(body);
    const after = body.slice(n);
    const m = /^\s*:(?:\s+|$)(.*)$/.exec(after);
    if (!m) return null;
    key = k;
    rest = m[1];
  } else {
    const m = /^([^\s#'"[\]{},][^#]*?|[^\s#'"[\]{},]):(?:\s+(.*)|)$/.exec(body);
    if (!m) return null;
    key = m[1].trim();
    rest = m[2] === undefined ? '' : m[2];
  }
  if (key === '<<') throw new YamlSubsetError('マージキー（<<）は未対応');
  if (key.startsWith('? ')) throw new YamlSubsetError('複雑な鍵は未対応');
  return { key, rest };
}

class Parser {
  constructor(lines) {
    this.lines = lines;
    this.i = 0;
  }

  skip() {
    while (this.i < this.lines.length && isBlankOrComment(this.lines[this.i])) this.i += 1;
  }

  peek() {
    this.skip();
    return this.i < this.lines.length ? this.lines[this.i] : null;
  }

  fail(msg, line) {
    const l = line || this.lines[Math.min(this.i, this.lines.length - 1)];
    throw new YamlSubsetError(`${msg}（${l ? `${l.no} 行目: ${l.raw.trim().slice(0, 80)}` : '末尾'}）`);
  }

  /** 字下げ `minIndent` 以上で始まるノードを読む。無ければ null。 */
  node(minIndent) {
    const l = this.peek();
    if (!l || l.indent < minIndent) return null;
    if (/^-(\s|$)/.test(l.body)) return this.seq(l.indent);
    if (splitKey(l.body)) return this.map(l.indent);
    return this.scalarLines(l.indent, minIndent);
  }

  /** 単独のスカラー（シーケンスの項目が素の値で、次行へ続く場合を含む）。 */
  scalarLines(indent, minIndent) {
    const first = this.lines[this.i];
    this.i += 1;
    return this.inlineValue(first.body, first, minIndent);
  }

  seq(indent) {
    const arr = [];
    for (;;) {
      const l = this.peek();
      if (!l || l.indent !== indent || !/^-(\s|$)/.test(l.body)) break;
      const rest = l.body.replace(/^-\s*/, '');
      const restCol = l.indent + (l.body.length - rest.length);
      if (rest === '' || /^#/.test(rest)) {
        this.i += 1;
        arr.push(this.node(indent + 1));
      } else {
        // コンパクトな項目（`- key: v`）: 行を「restCol の字下げで始まる行」に置き換えて読む。
        this.lines[this.i] = { ...l, indent: restCol, body: rest };
        arr.push(this.node(restCol));
      }
    }
    return arr;
  }

  map(indent) {
    const obj = {};
    for (;;) {
      const l = this.peek();
      if (!l || l.indent !== indent) break;
      if (/^-(\s|$)/.test(l.body)) break;
      const kv = splitKey(l.body);
      if (!kv) this.fail('マッピングの行ではない', l);
      this.i += 1;
      let { rest } = kv;
      const anchor = /^&\S+\s*/.exec(rest);
      if (anchor) rest = rest.slice(anchor[0].length);
      if (rest === '' || /^#/.test(rest)) {
        const nx = this.peek();
        if (nx && nx.indent === indent && /^-(\s|$)/.test(nx.body)) obj[kv.key] = this.seq(indent);
        else obj[kv.key] = this.node(indent + 1);
      } else {
        obj[kv.key] = this.inlineValue(rest, l, indent + 1);
      }
    }
    return obj;
  }

  /** 行の途中から始まる値（引用・フロー・ブロックスカラー・プレーン。いずれも次行へ続き得る）。 */
  inlineValue(rest, line, contIndent) {
    if (rest.startsWith('*')) this.fail('エイリアスは未対応', line);
    if (rest.startsWith('!')) this.fail('タグは未対応', line);
    if (/^[|>]/.test(rest)) {
      const h = /^([|>])([+-]?)(\d?)\s*(#.*)?$/.exec(rest);
      if (!h) this.fail('ブロックスカラーの見出しが読めない', line);
      if (h[1] === '>') this.fail('折り畳みのブロックスカラー（>）は未対応', line);
      return this.blockScalar(contIndent, h[2], h[3] ? line.indent + Number(h[3]) : null);
    }
    if (rest[0] === '"' || rest[0] === "'" || rest[0] === '[' || rest[0] === '{') {
      // 値の開始から、文書の残り（必要なだけの行）を連結して読む。
      let buf = rest;
      let j = this.i;
      for (;;) {
        try {
          const [v, n] = rest[0] === '"' || rest[0] === "'" ? readQuoted(buf) : readFlow(buf);
          const tail = buf.slice(n);
          const nl = tail.indexOf('\n');
          const sameLineTail = nl === -1 ? tail : tail.slice(0, nl);
          if (sameLineTail.trim() !== '' && !/^\s+#/.test(sameLineTail) && sameLineTail.trim()[0] !== '#') {
            this.fail('値の後ろに余分な文字がある', line);
          }
          // 消費した行数だけ進める（buf に足した行の数）
          this.i = j;
          return v;
        } catch (e) {
          if (!(e instanceof YamlSubsetError) || !/閉じていない/.test(e.message) || j >= this.lines.length) throw e;
          buf += `\n${this.lines[j].raw}`;
          j += 1;
        }
      }
    }
    // プレーン（複数行に折り返され得る）
    let s = stripPlainComment(rest);
    while (this.i < this.lines.length) {
      const n = this.lines[this.i];
      if (n.body.trim() === '') break;
      if (n.indent < contIndent || /^#/.test(n.body.trim())) break;
      if (/^-(\s|$)/.test(n.body) || splitKey(n.body)) break;
      s += ` ${stripPlainComment(n.body)}`;
      this.i += 1;
    }
    if (s === '' || s === '~' || s === 'null') return null;
    return s;
  }

  blockScalar(minIndent, chomp, explicitIndent) {
    const body = [];
    let contentIndent = explicitIndent;
    while (this.i < this.lines.length) {
      const l = this.lines[this.i];
      if (l.body === '' && l.raw.trim() === '') {
        body.push('');
        this.i += 1;
        continue;
      }
      if (contentIndent === null) {
        if (l.indent < minIndent) break;
        contentIndent = l.indent;
      }
      if (l.indent < contentIndent) break;
      body.push(l.raw.slice(contentIndent));
      this.i += 1;
    }
    // 末尾の空行は chomp に従う
    let trailing = 0;
    while (body.length > 0 && body[body.length - 1] === '') {
      body.pop();
      trailing += 1;
    }
    let text = body.join('\n');
    if (chomp === '+') text += '\n'.repeat(trailing + 1);
    else if (chomp !== '-' && body.length > 0) text += '\n';
    return text;
  }
}

/** 複数文書の YAML を読み、文書ごとの値の配列を返す（空の文書は落とす）。読めなければ YamlSubsetError。 */
function parseDocuments(text) {
  const docs = [];
  let cur = [];
  const flush = () => {
    if (cur.some((l) => !isBlankOrComment(l))) {
      const p = new Parser(cur);
      const v = p.node(0);
      if (p.peek()) p.fail('文書の残りを読めない');
      docs.push(v);
    }
    cur = [];
  };
  for (const l of toLines(text)) {
    if (/^---(\s|$)/.test(l.raw) || /^\.\.\.\s*$/.test(l.raw)) {
      flush();
      const rest = l.raw.replace(/^---\s*/, '');
      if (/^---/.test(l.raw) && rest !== '' && !/^#/.test(rest)) cur.push({ ...l, indent: 0, body: rest, raw: rest });
      continue;
    }
    cur.push(l);
  }
  flush();
  return docs;
}

module.exports = { parseDocuments, YamlSubsetError, decodeDoubleQuoted };
