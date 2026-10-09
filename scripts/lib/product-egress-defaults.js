'use strict';
/*
 * product-egress-defaults.js — NFR / ADR-0107 決定 4 / #1841
 *
 * **インフラ製品の既定の外部通信（利用統計・更新確認・テレメトリ等）が、製品が動く全経路の配備で止まっていること**を
 * 機械で確かめる。計画のデータ外部送信方針（08_data-egress-policy）の統制表の製品別の行（Grafana・Loki・Tempo・
 * Qdrant・Mailpit）の「現在の実現手段」である。
 *
 * ## 見るもの（経路）
 *
 * - **k8s の描画結果**（`check-deploy-manifests.js` が描画した chart の各 values と各 overlay の出力）:
 *   Pod を持つ資源のコンテナごとに、イメージが下表の製品なら「その製品から見える設定」を組み立てて判定する ——
 *   env・`command` / `args`・**マウントされたファイル**（同じ描画結果の ConfigMap を volume → volumeMount で辿る）。
 * - **compose**（`deploy/docker-compose.yml`）: サービスごとに env・`command`・バインドマウントしたファイル。
 * - **Testcontainers**（`src/**` の `*.cs`）: 統合試験が起こす Qdrant（`new QdrantBuilder(` の連鎖に無効化の env）。
 * - **手で起こす経路**（`docs/` 配下の `.md` の手順と `scripts/` 配下の `.sh`）: `docker|nerdctl|podman run` で製品を起こす行。
 *
 * ## 設計の要点
 *
 * 1. **「設定がどこかに在る」ではなく「その製品のプロセスから見える」で判定する。** 例: Loki は `-config.file=` が
 *    指すパスにマウントされたファイルの `analytics.reporting_enabled` を見る。同じ文字列が別の ConfigMap に在っても
 *    数えない。逆に、上書き（Loki の `-reporting.enabled=true`・Grafana の `GF_ANALYTICS_*` の env）も見る。
 * 2. **製品が 1 つも見つからなければ失敗**（`check-deploy-manifests` が走査の最後に呼ぶ `missingProducts`）。
 *    イメージの照合が壊れて 0 件になったとき「何も無い」と「問題が無い」を同じ出力にしない（#797）。
 * 3. **版で決まる穴を見る。** Mailpit は v1.26.2 より前に無効化の設定が無く、env を与えても黙って無視する
 *    （#1841 で v1.21.8 を実測）。設定の有無だけでは止まっていることにならない。
 * 4. **読めない形は失敗にする（fail-closed）。** YAML の部分集合のパーサ（`yaml-subset.js`）が読めない、
 *    マウント元の ConfigMap が描画結果に無い、等は「判定できない」として落とす。
 *
 * 製品の表（`PRODUCTS`）に無い製品は見ない。TEI（埋め込み）は opt-in で既定は配備されず、外部通信（モデルの取得）は
 * 機能そのものであって設定で止めるものではないため対象外（統制は「有効化の前にモデルを事前配置する」。作業仕様書）。
 */

const fs = require('fs');
const path = require('path');
const { parseDocuments, YamlSubsetError } = require('./yaml-subset');

// ---------------------------------------------------------------- 製品の表

/** Grafana の ini で止める鍵（節・鍵・止める値）。根拠は deploy/grafana/grafana.ini のコメントと作業仕様書。 */
const GRAFANA_INI_SETTINGS = [
  ['analytics', 'reporting_enabled', 'false'],
  ['analytics', 'check_for_updates', 'false'],
  ['analytics', 'check_for_plugin_updates', 'false'],
  ['news', 'news_feed_enabled', 'false'],
  ['security', 'disable_gravatar', 'true'],
  ['plugins', 'public_key_retrieval_disabled', 'true'],
  ['feature_toggles', 'pluginsDynamicAngularDetectionPatterns', 'false'],
];
const GRAFANA_CONFIG_PATH = '/etc/grafana/grafana.ini';
const MAILPIT_MIN_VERSION = [1, 26, 2];

/** Grafana の EnvKey（pkg/setting の EnvKey と同じ規則）。 */
const grafanaEnvKey = (section, key) => `GF_${section.toUpperCase().replace(/[.-]/g, '_')}_${key.toUpperCase().replace(/\./g, '_')}`;

/** 引数から `-name=value` / `--name=value` / `-name value` / `-name`（真偽値の省略）を引く。最後の指定が勝つ。 */
function flagValue(args, name) {
  let found;
  for (let i = 0; i < args.length; i += 1) {
    const m = new RegExp(`^--?${name.replace(/\./g, '\\.')}(?:=(.*))?$`).exec(args[i]);
    if (!m) continue;
    if (m[1] !== undefined) found = m[1];
    else if (i + 1 < args.length && !/^-/.test(args[i + 1]) && !/=/.test(args[i])) found = args[i + 1];
    else found = 'true';
  }
  return found;
}

/** 簡易 ini（節・`key = value`・`;` / `#` コメント）。同じ鍵は後勝ち（Grafana と同じ）。 */
function parseIni(text) {
  const out = {};
  let section = '';
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line === '' || line.startsWith(';') || line.startsWith('#')) continue;
    const s = /^\[([^\]]+)\]$/.exec(line);
    if (s) {
      section = s[1].trim();
      continue;
    }
    const kv = /^([^=]+?)\s*=\s*(.*)$/.exec(line);
    if (kv) out[`${section}.${kv[1].trim()}`] = kv[2].trim();
  }
  return out;
}

/** YAML の設定ファイルから `a.b` の値を引く（無ければ undefined）。 */
function yamlGet(text, dotted) {
  const docs = parseDocuments(text);
  let v = docs[0];
  for (const k of dotted.split('.')) {
    if (!v || typeof v !== 'object' || Array.isArray(v)) return undefined;
    v = v[k];
  }
  return v;
}

/** コンテナから見える設定 `view` = { image, env: Map, args: string[], files: Map<path, text> } を判定する。 */
const PRODUCTS = [
  {
    name: 'grafana',
    image: /(^|\/)grafana\/grafana(-oss)?(:|@|$)/,
    check(view) {
      const problems = [];
      const cfgPath = view.env.get('GF_PATHS_CONFIG') ?? GRAFANA_CONFIG_PATH;
      const cfgFlag = flagValue(view.args, 'config');
      const effective = cfgFlag ?? cfgPath;
      const ini = view.files.get(effective);
      if (ini === undefined) {
        problems.push(`${effective} にファイルがマウントされていない（既定の外部通信を止める ini が読まれない）`);
      } else {
        const parsed = parseIni(ini);
        for (const [sec, key, want] of GRAFANA_INI_SETTINGS) {
          const got = parsed[`${sec}.${key}`];
          if (got === undefined || got.toLowerCase() !== want) {
            problems.push(`${effective} の [${sec}] ${key} が ${want} でない（${got === undefined ? '未設定' : got}）`);
          }
        }
      }
      for (const [sec, key, want] of GRAFANA_INI_SETTINGS) {
        const envKey = grafanaEnvKey(sec, key);
        if (view.env.has(envKey) && String(view.env.get(envKey)).toLowerCase() !== want) {
          problems.push(`env ${envKey}=${view.env.get(envKey)} が ini の無効化を上書きしている`);
        }
      }
      return problems;
    },
  },
  {
    name: 'loki',
    image: /(^|\/)grafana\/loki(:|@|$)/,
    check: (view) => usageReportCheck(view, 'analytics.reporting_enabled'),
  },
  {
    name: 'tempo',
    image: /(^|\/)grafana\/tempo(:|@|$)/,
    check: (view) => usageReportCheck(view, 'usage_report.reporting_enabled'),
  },
  {
    name: 'qdrant',
    image: /(^|\/)qdrant\/qdrant(:|@|$)/,
    check(view) {
      const v = view.env.get('QDRANT__TELEMETRY_DISABLED');
      if (flagValue(view.args, 'disable-telemetry') === 'true') return [];
      return v !== undefined && String(v).toLowerCase() === 'true'
        ? []
        : [`env QDRANT__TELEMETRY_DISABLED が true でない（${v === undefined ? '未設定' : v}）。テレメトリは既定で有効`];
    },
  },
  {
    name: 'mailpit',
    image: /(^|\/)axllent\/mailpit(:|@|$)/,
    check(view) {
      const problems = [];
      const tag = /:v?(\d+)\.(\d+)\.(\d+)(?:@|$)/.exec(view.image);
      if (!tag) {
        problems.push(`イメージの版を読めない（${view.image}）。無効化の設定は v${MAILPIT_MIN_VERSION.join('.')} 以上にしか無い`);
      } else {
        const ver = tag.slice(1, 4).map(Number);
        const older = ver[0] !== MAILPIT_MIN_VERSION[0] ? ver[0] < MAILPIT_MIN_VERSION[0]
          : ver[1] !== MAILPIT_MIN_VERSION[1] ? ver[1] < MAILPIT_MIN_VERSION[1] : ver[2] < MAILPIT_MIN_VERSION[2];
        if (older) {
          problems.push(`v${ver.join('.')} は最新版の確認を止める設定を持たない（v${MAILPIT_MIN_VERSION.join('.')} で入った。` +
            '古い版は MP_DISABLE_VERSION_CHECK を黙って無視する）');
        }
      }
      const env = view.env.get('MP_DISABLE_VERSION_CHECK');
      const flag = flagValue(view.args, 'disable-version-check');
      const on = (x) => x !== undefined && ['1', 'true', 'yes'].includes(String(x).toLowerCase());
      if (!on(env) && !on(flag)) {
        problems.push(`env MP_DISABLE_VERSION_CHECK が true でない（${env === undefined ? '未設定' : env}）。最新版の確認（api.github.com）は既定で有効`);
      }
      return problems;
    },
  },
];

/** Loki / Tempo: `-config.file` が指すファイルの利用統計の鍵が false で、フラグで上書きされていない。 */
function usageReportCheck(view, dotted) {
  const problems = [];
  const cfg = flagValue(view.args, 'config.file');
  const override = flagValue(view.args, 'reporting.enabled');
  if (override !== undefined && override.toLowerCase() !== 'false') {
    problems.push(`引数 -reporting.enabled=${override} が設定ファイルの無効化を上書きしている（フラグが勝つ）`);
  }
  if (override !== undefined && override.toLowerCase() === 'false') return problems;
  if (cfg === undefined) {
    problems.push(`-config.file が無い（設定ファイルが読まれず、${dotted} は既定の true）`);
    return problems;
  }
  const text = view.files.get(cfg);
  if (text === undefined) {
    problems.push(`-config.file=${cfg} にファイルがマウントされていない（判定できない）`);
    return problems;
  }
  let v;
  try {
    v = yamlGet(text, dotted);
  } catch (e) {
    problems.push(`${cfg} を読めない（${e.message}）`);
    return problems;
  }
  if (v === undefined || String(v).toLowerCase() !== 'false') {
    problems.push(`${cfg} の ${dotted} が false でない（${v === undefined ? '未設定＝既定の true' : v}）`);
  }
  return problems;
}

const productOf = (image) => PRODUCTS.find((p) => p.image.test(String(image || '')));

// ---------------------------------------------------------------- k8s（描画結果）

/** Pod の spec を持つ資源から pod spec を返す。 */
function podSpecOf(doc) {
  if (!doc || typeof doc !== 'object') return null;
  const spec = doc.spec || {};
  if (doc.kind === 'Pod') return spec;
  if (doc.kind === 'CronJob') return spec.jobTemplate?.spec?.template?.spec ?? null;
  return spec.template?.spec ?? null;
}

const asArray = (x) => (Array.isArray(x) ? x : []);
const asString = (x) => (x === null || x === undefined ? '' : String(x));

/**
 * 描画済みの k8s マニフェスト（複数文書）を検査する。
 * @returns {{ problems: string[], found: Map<string, number> }}
 */
function checkRenderedManifests(yamlText, label) {
  const problems = [];
  const found = new Map();
  let docs;
  try {
    docs = parseDocuments(yamlText);
  } catch (e) {
    // 製品のイメージが含まれないなら、読めなくても判定に影響しない（他の資源の書式で全体を赤くしない）。
    const hit = PRODUCTS.filter((p) => new RegExp(`image:\\s*["']?\\S*${p.image.source.replace(/^\(\^\|\\\/\)/, '')}`).test(yamlText));
    if (hit.length > 0) problems.push(`${label}: 描画結果を読めない（${e.message}）。${hit.map((p) => p.name).join(' / ')} の無効化を判定できない`);
    return { problems, found };
  }
  const configMaps = new Map();
  for (const d of docs) {
    if (d && d.kind === 'ConfigMap' && d.metadata?.name) {
      configMaps.set(`${d.metadata.namespace ?? ''}/${d.metadata.name}`, d.data || {});
    }
  }
  for (const d of docs) {
    const pod = podSpecOf(d);
    if (!pod) continue;
    const ns = d.metadata?.namespace ?? '';
    const volumes = new Map(asArray(pod.volumes).map((v) => [v.name, v]));
    for (const c of [...asArray(pod.initContainers), ...asArray(pod.containers)]) {
      const product = productOf(c.image);
      if (!product) continue;
      found.set(product.name, (found.get(product.name) || 0) + 1);
      const where = `${label}: ${d.kind}/${d.metadata?.name ?? '?'} のコンテナ ${c.name ?? '?'}（${product.name}）`;
      const env = new Map();
      for (const e of asArray(c.env)) env.set(e.name, e.value === undefined && e.valueFrom ? '<valueFrom>' : asString(e.value));
      const args = [...asArray(c.command), ...asArray(c.args)].map(asString);
      const files = new Map();
      for (const m of asArray(c.volumeMounts)) {
        const vol = volumes.get(m.name);
        if (!vol || !vol.configMap) continue;
        const data = configMaps.get(`${ns}/${vol.configMap.name}`);
        if (!data) {
          problems.push(`${where}: マウント元の ConfigMap ${vol.configMap.name} が同じ描画結果に無い（判定できない）`);
          continue;
        }
        const items = asArray(vol.configMap.items);
        const entries = items.length > 0 ? items.map((it) => [it.path, data[it.key]]) : Object.entries(data);
        for (const [p, text] of entries) {
          if (text === undefined || text === null) continue;
          if (m.subPath) {
            if (p === m.subPath) files.set(m.mountPath, asString(text));
          } else {
            files.set(path.posix.join(m.mountPath, p), asString(text));
          }
        }
      }
      for (const p of product.check({ image: asString(c.image), env, args, files })) problems.push(`${where}: ${p}`);
    }
  }
  return { problems, found };
}

// ---------------------------------------------------------------- compose

/** compose のサービス定義を「サービス名 → 本文」に切り出す（全体はアンカー・マージキーを含むので、サービス単位で読む）。 */
function splitComposeServices(text) {
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  const start = lines.findIndex((l) => /^services:\s*(#.*)?$/.test(l));
  if (start === -1) throw new YamlSubsetError('services: が無い');
  const out = new Map();
  let cur = null;
  for (let i = start + 1; i < lines.length; i += 1) {
    const l = lines[i];
    if (/^\S/.test(l) && !/^#/.test(l)) break; // 次の最上位の鍵
    const m = /^ {2}([A-Za-z0-9._-]+):\s*(#.*)?$/.exec(l);
    if (m) {
      cur = m[1];
      out.set(cur, []);
      continue;
    }
    if (cur) out.get(cur).push(l);
  }
  return new Map([...out].map(([k, v]) => [k, v.join('\n')]));
}

/** compose の `environment`（マップ／`K=V` のリスト）を Map にする。 */
function composeEnv(env) {
  const m = new Map();
  if (Array.isArray(env)) {
    for (const e of env) {
      const s = asString(e);
      const i = s.indexOf('=');
      if (i > 0) m.set(s.slice(0, i), s.slice(i + 1));
    }
  } else if (env && typeof env === 'object') {
    for (const [k, v] of Object.entries(env)) m.set(k, asString(v));
  }
  return m;
}

/** compose の `command` / `entrypoint`（リスト／文字列）を引数の配列にする。 */
const composeArgs = (cmd) => (Array.isArray(cmd) ? cmd.map(asString) : cmd ? asString(cmd).split(/\s+/).filter(Boolean) : []);

/** バインドマウントしたファイル（ディレクトリは配下を再帰で）を Map<コンテナ内パス, 本文> にする。 */
function composeFiles(volumes, composeDir, readFile, listDir) {
  const files = new Map();
  const add = (host, target) => {
    const abs = path.resolve(composeDir, host);
    const entries = listDir(abs);
    if (entries === null) {
      const text = readFile(abs);
      if (text !== null) files.set(target, text);
      return;
    }
    for (const e of entries) add(path.join(host, e), path.posix.join(target, e));
  };
  for (const v of asArray(volumes)) {
    if (typeof v === 'string') {
      const parts = v.split(':');
      if (parts.length >= 2 && /^[./~]/.test(parts[0])) add(parts[0], parts[1]);
    } else if (v && v.type === 'bind' && v.source && v.target) {
      add(v.source, v.target);
    }
  }
  return files;
}

const fsReadFile = (p) => {
  try {
    return fs.readFileSync(p, 'utf8');
  } catch {
    return null;
  }
};
const fsListDir = (p) => {
  try {
    return fs.statSync(p).isDirectory() ? fs.readdirSync(p).sort() : null;
  } catch {
    return null;
  }
};

/**
 * compose ファイルを検査する。
 * @returns {{ problems: string[], found: Map<string, number> }}
 */
function checkCompose(text, label, composeDir, { readFile = fsReadFile, listDir = fsListDir } = {}) {
  const problems = [];
  const found = new Map();
  let services;
  try {
    services = splitComposeServices(text);
  } catch (e) {
    return { problems: [`${label}: サービス定義を切り出せない（${e.message}）`], found };
  }
  for (const [name, body] of services) {
    const imageLine = /^\s+image:\s*["']?([^"'\s#]+)/m.exec(body);
    const product = imageLine && productOf(imageLine[1]);
    if (!product) continue;
    found.set(product.name, (found.get(product.name) || 0) + 1);
    const where = `${label}: サービス ${name}（${product.name}）`;
    let svc;
    try {
      [svc] = parseDocuments(body.split('\n').map((l) => l.replace(/^ {4}/, '')).join('\n'));
    } catch (e) {
      problems.push(`${where}: 定義を読めない（${e.message}。アンカー・マージキーは製品のサービスでは使わないこと）`);
      continue;
    }
    svc = svc || {};
    const view = {
      image: asString(svc.image),
      env: composeEnv(svc.environment),
      args: [...composeArgs(svc.entrypoint), ...composeArgs(svc.command)],
      files: composeFiles(svc.volumes, composeDir, readFile, listDir),
    };
    for (const p of product.check(view)) problems.push(`${where}: ${p}`);
  }
  return { problems, found };
}

// ---------------------------------------------------------------- Testcontainers（src/**/*.cs）

/**
 * 統合試験の Qdrant が無効化の env 付きで起きることを確かめる。`new QdrantBuilder(` から `;` までの連鎖に
 * `.WithEnvironment("QDRANT__TELEMETRY_DISABLED", "true")` が在ること。他の製品のイメージ文字列が .cs に現れたら
 * （この検査が経路を知らない）失敗にする。
 * @param {{rel: string, text: string}[]} csFiles
 */
function checkTestcontainers(csFiles) {
  const problems = [];
  let builders = 0;
  for (const { rel, text: raw } of csFiles) {
    // 行コメント（`//` `///`）は落とす（XML 文書コメントの中の `new QdrantBuilder(` を数えない）。行番号は保つ。
    const text = raw.split('\n').map((l) => (/^\s*\/\//.test(l) ? '' : l)).join('\n');
    const re = /new\s+QdrantBuilder\s*\(/g;
    let m;
    while ((m = re.exec(text)) !== null) {
      builders += 1;
      const end = text.indexOf(';', m.index);
      const chain = text.slice(m.index, end === -1 ? undefined : end);
      if (!/\.WithEnvironment\(\s*"QDRANT__TELEMETRY_DISABLED"\s*,\s*"true"\s*\)/.test(chain)) {
        const line = text.slice(0, m.index).split('\n').length;
        problems.push(`${rel}:${line}: new QdrantBuilder( の連鎖に .WithEnvironment("QDRANT__TELEMETRY_DISABLED", "true") が無い` +
          '（QdrantTestImage.CreateBuilder() を使うこと）');
      }
    }
    for (const p of PRODUCTS.filter((x) => x.name !== 'qdrant')) {
      const lit = new RegExp(`"[^"\\n]*${p.image.source.replace(/^\(\^\|\\\/\)/, '')}[^"\\n]*"`);
      if (lit.test(text)) problems.push(`${rel}: ${p.name} のイメージを試験から起こしている。無効化の検査を本ファイルへ足すこと`);
    }
  }
  return { problems, builders };
}

// ---------------------------------------------------------------- 手で起こす経路（手順書・スクリプトの `docker run`）

/**
 * 手順書（`docs/` 配下の `.md`）とスクリプト（`scripts/` 配下の `.sh`）に書かれた `docker|nerdctl|podman run` のうち、製品のイメージを
 * 起こすものを判定する（行末の `\` の継続は 1 行に繋ぐ。コメント行の中の例も対象 —— 人が写して打つため）。
 * 見えるのは `-e` / `--env` の env と、イメージより後ろの引数だけ。設定ファイルを辿れない製品（Grafana・Loki・Tempo）は、
 * env か引数で止まっていなければ失敗にする（判定できない形を手順に書かない）。
 * @param {{rel: string, text: string}[]} files
 */
function checkRunCommands(files) {
  const problems = [];
  let commands = 0;
  for (const { rel, text } of files) {
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i += 1) {
      let line = lines[i].replace(/^\s*#\s?/, '');
      const startNo = i + 1;
      while (/\\\s*$/.test(line) && i + 1 < lines.length) {
        i += 1;
        line = `${line.replace(/\\\s*$/, ' ')}${lines[i].replace(/^\s*#\s?/, '')}`;
      }
      if (!/\b(docker|nerdctl|podman)\s+(container\s+)?run\b/.test(line)) continue;
      const tokens = line.trim().split(/\s+/);
      const imageIdx = tokens.findIndex((t) => productOf(t));
      if (imageIdx === -1) continue;
      const product = productOf(tokens[imageIdx]);
      commands += 1;
      const env = new Map();
      for (let j = 0; j < imageIdx; j += 1) {
        const m = /^(?:-e|--env)(?:=(.*))?$/.exec(tokens[j]);
        if (!m) continue;
        const kv = (m[1] !== undefined ? m[1] : tokens[j + 1] || '').replace(/^['"]|['"]$/g, '');
        const eq = kv.indexOf('=');
        if (eq > 0) env.set(kv.slice(0, eq), kv.slice(eq + 1));
      }
      const args = tokens.slice(imageIdx + 1).filter((t) => !/^[#|;&]/.test(t));
      const view = { image: tokens[imageIdx], env, args, files: new Map() };
      for (const p of product.check(view)) problems.push(`${rel}:${startNo}: ${product.name} を手で起こす手順: ${p}`);
    }
  }
  return { problems, commands };
}

/** docs/ の .md と scripts/ の .sh を集める（手で起こす経路の母集合）。 */
function collectRunCommandFiles(repoRoot) {
  const out = [];
  const walk = (dir, ext) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) {
        if (!['node_modules', '.git'].includes(e.name)) walk(full, ext);
      } else if (e.name.endsWith(ext)) {
        out.push({ rel: path.relative(repoRoot, full).split(path.sep).join('/'), text: fs.readFileSync(full, 'utf8') });
      }
    }
  };
  walk(path.join(repoRoot, 'docs'), '.md');
  walk(path.join(repoRoot, 'scripts'), '.sh');
  return out;
}

/** リポジトリの src/ 配下の .cs を集める（bin/obj/node_modules を除く）。 */
function collectCsFiles(repoRoot) {
  const out = [];
  const walk = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) {
        if (!['bin', 'obj', 'node_modules', '.git'].includes(e.name)) walk(full);
      } else if (e.name.endsWith('.cs')) {
        out.push({ rel: path.relative(repoRoot, full).split(path.sep).join('/'), text: fs.readFileSync(full, 'utf8') });
      }
    }
  };
  walk(path.join(repoRoot, 'src'));
  return out;
}

/** 走査全体で 1 度も見つからなかった製品の名前（要点 2）。 */
function missingProducts(foundMaps) {
  const total = new Map();
  for (const f of foundMaps) for (const [k, v] of f) total.set(k, (total.get(k) || 0) + v);
  return PRODUCTS.map((p) => p.name).filter((n) => !total.get(n));
}

module.exports = {
  PRODUCTS,
  GRAFANA_INI_SETTINGS,
  MAILPIT_MIN_VERSION,
  checkRenderedManifests,
  checkCompose,
  checkTestcontainers,
  collectCsFiles,
  checkRunCommands,
  collectRunCommandFiles,
  missingProducts,
  flagValue,
  parseIni,
};
