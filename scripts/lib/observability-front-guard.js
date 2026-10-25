'use strict';
/*
 * observability-front-guard.js — NFR-18 / ADR-0133 決定 3 / #1867
 *
 * **描画結果に Loki・Tempo が現れたら、ADR-0133 の 2 つの条件が同じ描画結果に揃っていることを要求する。**
 * 条件 1＝前段の認証、条件 2＝到達の制限。現れなければ「対象なし」で何も言わない。
 * 本番像の chart（`deploy/helm`）には現時点で Loki・Tempo が無い。chart へ足す PR が、前段も到達の制限も無いまま
 * 入るのを CI で止めるための検査で、ADR-0133 決定 3「前段が無いまま本番像へ足さない」の機械化である。
 * `check-deploy-manifests.js` が描画した chart の各 values と各 overlay の出力に当てる。経路 B の
 * `deploy/local/observability`（#1842 の gate と NetworkPolicy。IADR-0526）が、描画結果で通る陽性対照になる。
 *
 * ## 判定（名前では通さない。理由は作業仕様書 20261016_1867_chart-observability-front-guard）
 *
 * 対象: Pod を持つ資源のうち、コンテナのイメージの最後のセグメントが `loki` / `tempo`（`grafana-loki` 等の配布名を含む）のもの。
 *
 * 条件 1（前段）。次の全部を満たす:
 *   1a. 同じ Pod に、製品以外のリバースプロキシのコンテナ（イメージが FRONT_PROXY_IMAGES）がある。コンテナ名は見ない。
 *   1b. その前段が口（containerPort）を持つ。
 *   1c. その前段が Secret から資格情報を受け取る（env の secretKeyRef・envFrom の secretRef・Secret の volume。optional は数えない）。
 *       「認証を掛けない素通しのプロキシ」を前段として数えないための最低限の形である。
 *   1d. 製品の HTTP と gRPC が loopback だけで待つ（`-config.file` が指すマウントされたファイルの `server.http_listen_address` /
 *       `server.grpc_listen_address`、および同名のフラグの上書き）。読めなければ失敗（判定できない）。hostNetwork は失敗。
 *       前段があっても製品が 0.0.0.0 で待てば、Pod の IP へ直接届いて前段を迂回できるため。
 *   1e. 製品のコンテナが口を宣言しない。例外は Tempo の OTLP の書き込み（4317 / 4318。管理用の口ではない）だけ。
 *   1f. その Pod を選ぶ Service の各口の宛先（targetPort）が前段の口である。例外は 1e と同じ。
 *
 * 条件 2（到達の制限）。次の全部を満たす:
 *   2a. その Pod を**名指しで**選ぶ NetworkPolicy（Ingress）が 1 枚以上ある（podSelector の matchLabels か `In` で選ぶ）。
 *       `podSelector: {}` や NotIn だけで「結果として選ばれる」ものは数えない。
 *   2b. その Pod を選ぶ Ingress の NetworkPolicy は**全部**（名指しでないもの、例えば chart の allow-intra-namespace を含む）、
 *       規則ごとに from を持ち、from の各要素が狭い（podSelector が matchLabels か `In` を持つ。namespaceSelector を併せるなら
 *       それも同じく狭い。ipBlock・`{}`・namespaceSelector だけ、は広い）。NetworkPolicy は許可の和なので、1 枚でも広ければ開く。
 *
 * 描画結果を読めないとき、製品のイメージが字面に在れば失敗にする（fail-closed）。
 */

const { parseDocuments } = require('./yaml-subset');
const { yamlGet, podSpecOf, configMapIndex, containerView, flagValue } = require('./product-egress-defaults');

/** 対象の製品。イメージの最後のセグメント（tag・digest を除く）で識別する。ミラー（`registry/x/loki`）も拾う。 */
const TARGETS = [
  { name: 'loki', segments: ['loki', 'grafana-loki'], openPorts: [] },
  // OTLP の書き込みは前段を通さない（経路 B と同じ。到達は条件 2 で collector に絞る）。
  { name: 'tempo', segments: ['tempo', 'grafana-tempo'], openPorts: [4317, 4318] },
];

/** 前段として数えるリバースプロキシのイメージ（最後のセグメント）。足すときは認証を掛けられる製品に限る。 */
const FRONT_PROXY_IMAGES = ['caddy', 'nginx', 'nginx-unprivileged', 'envoy', 'oauth2-proxy', 'haproxy', 'traefik'];

const LOOPBACK = new Set(['127.0.0.1', 'localhost', '::1', '[::1]']);

const asArray = (x) => (Array.isArray(x) ? x : []);

/** `registry:5000/a/b:tag@sha256:...` → `b` */
function imageSegment(image) {
  const noDigest = String(image || '').split('@')[0];
  const last = noDigest.split('/').pop();
  return last.replace(/:[^:]*$/, '');
}

const targetOf = (image) => TARGETS.find((t) => t.segments.includes(imageSegment(image)));

/** LabelSelector の照合（k8s の規則。`{}` は全部に当たる）。知らない形は例外にする（fail-closed）。 */
function selectorMatches(sel, labels) {
  const s = sel || {};
  for (const k of Object.keys(s)) {
    if (!['matchLabels', 'matchExpressions'].includes(k)) throw new Error(`知らない selector の項目 ${k}`);
  }
  for (const [k, v] of Object.entries(s.matchLabels || {})) if (labels[k] !== String(v)) return false;
  for (const e of asArray(s.matchExpressions)) {
    const has = Object.prototype.hasOwnProperty.call(labels, e.key);
    const values = asArray(e.values).map(String);
    switch (e.operator) {
      case 'In': if (!(has && values.includes(labels[e.key]))) return false; break;
      case 'NotIn': if (has && values.includes(labels[e.key])) return false; break;
      case 'Exists': if (!has) return false; break;
      case 'DoesNotExist': if (has) return false; break;
      default: throw new Error(`知らない operator ${e.operator}`);
    }
  }
  return true;
}

/** 名指しの selector か（matchLabels が 1 つ以上、または `In` を持つ）。`{}`・NotIn・Exists だけは名指しでない。 */
function isNarrowSelector(sel) {
  if (!sel || typeof sel !== 'object') return false;
  if (Object.keys(sel.matchLabels || {}).length > 0) return true;
  return asArray(sel.matchExpressions).some((e) => e.operator === 'In' && asArray(e.values).length > 0);
}

/** from の 1 要素が狭いか。理由（広いとき）を返す。 */
function peerWideness(peer) {
  if (!peer || typeof peer !== 'object') return '読めない要素';
  if (peer.ipBlock) return 'ipBlock（Pod の身元で絞れない）';
  if (!peer.podSelector) return peer.namespaceSelector ? 'namespaceSelector だけ（その名前空間の全 Pod）' : '空の要素';
  if (!isNarrowSelector(peer.podSelector)) return 'podSelector が名指しでない（`{}`・NotIn・Exists だけ）';
  if (peer.namespaceSelector && !isNarrowSelector(peer.namespaceSelector)) return 'namespaceSelector が名指しでない（`{}` 等。全名前空間の同じラベルの Pod）';
  return null;
}

/** 前段のコンテナが Secret から資格情報を受け取るか（optional は数えない）。 */
function receivesSecret(c, pod) {
  const req = (ref) => ref && String(ref.optional) !== 'true';
  if (asArray(c.env).some((e) => req(e.valueFrom?.secretKeyRef))) return true;
  if (asArray(c.envFrom).some((e) => req(e.secretRef))) return true;
  const secretVolumes = new Set(asArray(pod.volumes).filter((v) => req(v.secret)).map((v) => v.name));
  return asArray(c.volumeMounts).some((m) => secretVolumes.has(m.name));
}

/** Pod のラベル（Pod は metadata、CronJob は jobTemplate の template、他は template）。値は文字列にそろえる。 */
function podLabelsOf(d) {
  let raw;
  if (d.kind === 'Pod') raw = d.metadata?.labels;
  else if (d.kind === 'CronJob') raw = d.spec?.jobTemplate?.spec?.template?.metadata?.labels;
  else raw = d.spec?.template?.metadata?.labels;
  return Object.fromEntries(Object.entries(raw || {}).map(([k, v]) => [k, String(v)]));
}

/** コンテナの口が containerPort（数）か name（文字列）で port に当たるか。 */
const portOf = (c, target) => asArray(c.ports).find((p) => String(p.containerPort) === String(target) || (p.name !== undefined && String(p.name) === String(target)));

/** 製品の待ち受け（HTTP・gRPC）が loopback だけかを見る（1d）。 */
function loopbackProblems(view) {
  const problems = [];
  const cfg = flagValue(view.args, 'config.file');
  const text = cfg === undefined ? undefined : view.files.get(cfg);
  for (const [key, flag] of [['server.http_listen_address', 'server.http-listen-address'], ['server.grpc_listen_address', 'server.grpc-listen-address']]) {
    const override = flagValue(view.args, flag);
    let v = override;
    if (v === undefined) {
      if (cfg === undefined) { problems.push(`-config.file が無く ${key} を判定できない（既定は全アドレスで待つ）`); continue; }
      if (text === undefined) { problems.push(`-config.file=${cfg} にファイルがマウントされていない（${key} を判定できない）`); continue; }
      try {
        v = yamlGet(text, key);
      } catch (e) {
        problems.push(`${cfg} を読めない（${e.message}）`);
        continue;
      }
    }
    if (v === undefined || !LOOPBACK.has(String(v))) {
      problems.push(`${key} が loopback でない（${v === undefined ? '未設定＝全アドレスで待つ' : v}）。Pod の IP へ直接届き、前段を迂回できる`);
    }
  }
  return problems;
}

/**
 * 描画済みの k8s マニフェスト（複数文書）を検査する。
 * @returns {{ problems: string[], found: Map<string, number> }} found は「条件を判定した製品のワークロード数」
 */
function checkObservabilityFront(yamlText, label) {
  const problems = [];
  const found = new Map();
  let docs;
  try {
    docs = parseDocuments(yamlText).filter((d) => d && typeof d === 'object');
  } catch (e) {
    const hit = /image:\s*["']?\S*\/?(grafana-)?(loki|tempo)(["':@\s]|$)/m.test(yamlText);
    if (hit) problems.push(`${label}: 描画結果を読めない（${e.message}）。Loki・Tempo の前段と到達の制限を判定できない`);
    return { problems, found };
  }
  const configMaps = configMapIndex(docs);
  const nsOf = (d) => d.metadata?.namespace ?? '';
  const services = docs.filter((d) => d.kind === 'Service');
  const policies = docs.filter((d) => d.kind === 'NetworkPolicy');

  for (const d of docs) {
    const pod = podSpecOf(d);
    if (!pod) continue;
    const containers = asArray(pod.containers);
    const all = [...asArray(pod.initContainers), ...containers];
    const productContainers = all.filter((c) => targetOf(c.image));
    if (productContainers.length === 0) continue;
    const labels = podLabelsOf(d);
    const ns = nsOf(d);
    const target = targetOf(productContainers[0].image);
    found.set(target.name, (found.get(target.name) || 0) + 1);
    const where = `${label}: ${d.kind}/${d.metadata?.name ?? '?'}（${target.name}）`;
    const fail = (cond, msg) => problems.push(`${where}: ${cond}: ${msg}（ADR-0133。#1867）`);
    const C1 = '条件 1（前段の認証）';
    const C2 = '条件 2（到達の制限）';

    // ---- 条件 1
    const fronts = containers.filter((c) => !targetOf(c.image) && FRONT_PROXY_IMAGES.includes(imageSegment(c.image)));
    const goodFronts = fronts.filter((c) => asArray(c.ports).length > 0 && receivesSecret(c, pod));
    if (fronts.length === 0) {
      fail(C1, `同じ Pod に前段（リバースプロキシ ${FRONT_PROXY_IMAGES.join(' / ')} のコンテナ）が無い`);
    } else if (goodFronts.length === 0) {
      fail(C1, `前段 ${fronts.map((c) => c.name).join(', ')} が口を持たないか、Secret から資格情報を受け取らない（認証を掛けない素通しを前段と数えない）`);
    }
    if (pod.hostNetwork === true || String(pod.hostNetwork) === 'true') fail(C1, 'hostNetwork で動く（loopback がノードの loopback になる）');
    for (const c of productContainers) {
      const t = targetOf(c.image);
      const { view, problems: vp } = containerView(d, c, configMaps);
      for (const p of [...vp, ...loopbackProblems(view)]) fail(C1, `コンテナ ${c.name ?? '?'}: ${p}`);
      const open = asArray(c.ports).filter((p) => !t.openPorts.includes(Number(p.containerPort)));
      if (open.length > 0) fail(C1, `コンテナ ${c.name ?? '?'} が前段を通らない口を宣言している: ${open.map((p) => p.containerPort).join(', ')}`);
    }
    for (const s of services) {
      if (nsOf(s) !== ns || !s.spec?.selector || Object.keys(s.spec.selector).length === 0) continue;
      if (!selectorMatches({ matchLabels: s.spec.selector }, labels)) continue;
      for (const sp of asArray(s.spec.ports)) {
        const tp = sp.targetPort ?? sp.port;
        if (goodFronts.some((c) => portOf(c, tp))) continue;
        const viaProduct = productContainers.find((c) => portOf(c, tp));
        const opened = viaProduct && targetOf(viaProduct.image).openPorts.includes(Number(portOf(viaProduct, tp).containerPort));
        if (!opened) fail(C1, `Service/${s.metadata?.name ?? '?'} の口 ${sp.port}（targetPort ${tp}）の宛先が前段の口でない`);
      }
    }

    // ---- 条件 2
    let selecting;
    try {
      selecting = policies.filter((p) => nsOf(p) === ns
        && (p.spec?.policyTypes ? asArray(p.spec.policyTypes) : ['Ingress']).includes('Ingress')
        && selectorMatches(p.spec?.podSelector, labels));
    } catch (e) {
      fail(C2, `NetworkPolicy を判定できない（${e.message}）`);
      continue;
    }
    if (!selecting.some((p) => isNarrowSelector(p.spec?.podSelector))) {
      fail(C2, 'この Pod を名指しで選ぶ Ingress の NetworkPolicy が無い（podSelector の matchLabels か In で選ぶ）');
    }
    for (const p of selecting) {
      const rules = asArray(p.spec?.ingress);
      rules.forEach((r, i) => {
        const from = asArray(r && r.from);
        if (from.length === 0) {
          fail(C2, `NetworkPolicy/${p.metadata?.name ?? '?'} の ingress[${i}] に from が無い（全ての送り元から届く）`);
          return;
        }
        from.forEach((peer, j) => {
          const why = peerWideness(peer);
          if (why) fail(C2, `NetworkPolicy/${p.metadata?.name ?? '?'} の ingress[${i}].from[${j}] が広い: ${why}。許可の和なので、この Pod を対象から外すか送り元を名指しにする`);
        });
      });
    }
  }
  return { problems, found };
}

module.exports = { checkObservabilityFront, TARGETS, FRONT_PROXY_IMAGES, imageSegment, selectorMatches, isNarrowSelector };
