#!/usr/bin/env node
'use strict';
/*
 * scripts.repo.test.js
 * 本リポジトリ固有スクリプトの単体テスト。scripts.test.js（impl-handoff-kit の配布物）から
 * 自動で読み込まれる companion ファイルである（IADR-0115 / planning#112 / planning#116）。
 *
 * ここへ書く理由: scripts.test.js はキットの更新のたびに差し替わるため、固有テストを直接
 * 追記すると同期のたびに手動マージが要り、キットが同じテストを取り込んだ際に重複も生じる。
 * 本ファイルへ分離することで scripts.test.js をキットとバイト一致に保てる。
 *
 * **必ずコミットすること。** 未追跡だと CI（clean checkout）に存在せず、固有テストが黙って
 * 走らなくなる（scripts.test.js が未追跡を検出して警告する）。消失そのものは ci.yml の
 * scripts-tests ジョブに REQUIRE_REPO_TESTS=1 を設定して検出する。
 *
 * 実行: node scripts/scripts.test.js（本ファイル単体では実行しない）
 */

// NFR, #797, IADR-0208: 単体実行を fail-fast にする。
// 本ファイルは companion であり、直接実行すると module.exports へ関数を代入するだけで
// テストが 1 件も走らないまま出力ゼロで exit 0 になる。**沈黙の exit 0 は全件通過の exit 0 と
// 区別できない** —— 実際に確定済み仕様書へ空の証跡が 1 件残り、別の作業でも緑と読みかけた。
// require() 経由（受け口 loadCompanionTests）では require.main が本ファイルにならないため、
// 本来の経路の挙動は一切変わらない。ガードの回帰テストは下の「companion の単体実行」節にある。
if (require.main === module) {
  process.stderr.write(
    `✗ ${require('path').basename(__filename)} は companion であり、単体では 1 件も検査しない。\n` +
      '  この呼び出しは「沈黙の exit 0」を返すだけで、検証の証跡にはならない。\n' +
      '  正しい入口:\n' +
      '    node scripts/scripts.test.js\n' +
      '    REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js   # companion の消失も検出する\n'
  );
  process.exit(1);
}

module.exports = ({ ok, assert }) => {
  // --- companion の単体実行: 沈黙の exit 0 を返さない（NFR / #797 / IADR-0208） -------

  {
    const { spawnSync } = require('child_process');

    // ガードは「外れても誰も気づかない」種類の変更である（外れた状態＝元の沈黙）。
    // よって CI が実際に叩く入口から、子プロセスで本ファイルを直接起動して固定する。
    const runSelfDirectly = (env) =>
      spawnSync(process.execPath, [__filename], {
        encoding: 'utf8',
        env: { ...process.env, ...env },
      });

    ok('companion: 単体で直接実行すると exit 1 になる（沈黙の exit 0 を返さない）', () => {
      const r = runSelfDirectly({});
      assert.strictEqual(
        r.status,
        1,
        `単体実行が exit ${r.status} を返した。沈黙の exit 0 は「全件通過」と区別できない（#797）`
      );
    });

    ok('companion: 単体実行の失敗メッセージが正しい入口を示す', () => {
      const out = runSelfDirectly({}).stderr || '';
      assert.match(out, /companion/, '何が起きたかを書くこと');
      assert.match(out, /node scripts\/scripts\.test\.js/, '正しい入口を書くこと');
      assert.match(out, /REQUIRE_REPO_TESTS=1/, '消失検出つきの入口も書くこと');
    });

    ok('companion: REQUIRE_REPO_TESTS=1 を付けた単体実行も exit 1 になる', () => {
      // 環境変数は受け口（scripts.test.js）が読む。companion へ直接付けても効かないため、
      // 「1 を付ければ走るはず」という誤解ごと止める。
      const r = runSelfDirectly({ REQUIRE_REPO_TESTS: '1' });
      assert.strictEqual(r.status, 1, 'REQUIRE_REPO_TESTS=1 でも単体実行は検査しない');
    });
  }

  // --- seed-abac-policies: 冪等性の核（#517 / IADR-0133） ---------------------------

  {
    const seed = require('./seed-abac-policies.js');
    const fsSeed = require('fs');
    const pathSeed = require('path');

    ok('seed: 属性は key+scope で突合し、未登録のものだけ返す', () => {
      const wanted = [
        { key: 'confidentiality', scope: 'document' },
        { key: 'department', scope: 'document' },
        { key: 'department', scope: 'user' }, // 同じ key でも scope が違えば別物
        { key: 'lifecycle', scope: 'document' },
      ];
      const existing = [
        { key: 'Confidentiality', scope: 'Document' }, // 大小文字は同一視する
        { key: 'department', scope: 'user' },
      ];
      assert.deepStrictEqual(
        seed.selectMissingAttributes(wanted, existing).map((a) => `${a.scope}/${a.key}`),
        ['document/department', 'document/lifecycle']
      );
      // 既存が全部揃っていれば no-op（2 回目の実行が何もしないこと＝冪等の核）。
      assert.deepStrictEqual(seed.selectMissingAttributes(wanted, wanted), []);
      // 既存ゼロなら全件が対象（初回投入）。
      assert.strictEqual(seed.selectMissingAttributes(wanted, []).length, 4);
    });

    ok('seed: ポリシーは name で突合し、未登録のものだけ返す', () => {
      const wanted = [{ name: 'dev: A' }, { name: 'dev: B' }];
      assert.deepStrictEqual(
        seed.selectMissingPolicies(wanted, [{ name: 'DEV: A' }]).map((p) => p.name),
        ['dev: B']
      );
      assert.deepStrictEqual(seed.selectMissingPolicies(wanted, wanted), []);
      assert.strictEqual(seed.selectMissingPolicies(wanted, []).length, 2);
    });

    // 投入データそのものの回帰。**階段の最下段（clearance=public）を欠くと、
    // clearance=public の利用者はどのポリシーにもマッチせず public 文書すら読めない**
    // （deny-by-default）。README が謳う「階段」と投入データを一致させ続けるために固定する。
    ok('seed: clearance の階段が 4 段すべて揃っている', () => {
      const file = pathSeed.resolve(__dirname, '..', 'deploy', 'local', 'abac-seed', 'policies.json');
      const readPolicies = JSON.parse(fsSeed.readFileSync(file, 'utf8')).policies.filter(
        (p) => p.action === 'read'
      );
      for (const level of ['public', 'internal', 'confidential', 'restricted']) {
        // その clearance を持つ利用者にマッチする read ポリシーが 1 本以上あること。
        const matched = readPolicies.filter((p) => (p.userConditions.clearance || []).includes(level));
        assert.ok(matched.length > 0, `clearance=${level} にマッチする read ポリシーが無い`);
        // マッチしたポリシーの文書条件の和 = その利用者が読める機密区分。
        const visible = new Set(matched.flatMap((p) => p.documentConditions.confidentiality || []));
        assert.ok(visible.has('public'), `clearance=${level} が public 文書すら読めない`);
      }
      // 上位ほど広い（階段であること）。
      const visibleFor = (level) =>
        new Set(
          readPolicies
            .filter((p) => (p.userConditions.clearance || []).includes(level))
            .flatMap((p) => p.documentConditions.confidentiality || [])
        );
      assert.ok(visibleFor('restricted').size >= visibleFor('confidential').size);
      assert.ok(visibleFor('confidential').size >= visibleFor('internal').size);
      assert.ok(visibleFor('internal').size >= visibleFor('public').size);
      // 最上段は 4 区分すべてを読める。
      assert.strictEqual(visibleFor('restricted').size, 4);
    });
  }

  // --- #524: PR タイトル検査が GitHub App 作成 PR で skipped にならないこと ------------

  {
    const fsGate = require('fs');
    const pathGate = require('path');
    const { isBotLogin } = require('./check-commit-messages.js');
    const WORKFLOW = pathGate.resolve(__dirname, '..', '.github', 'workflows', 'pr-title.yml');

    // YAML のコメント行を落とす。**経緯の説明で `user.type` に言及すること自体は禁じない**——
    // 禁じたいのは「効いている条件」であって、なぜそれを止めたかの記録ではない。
    const withoutComments = (yml) =>
      yml
        .split('\n')
        .filter((l) => !/^\s*#/.test(l))
        .join('\n');

    ok('#524: pr-title.yml が user.type で bot を除外していない', () => {
      const yml = withoutComments(fsGate.readFileSync(WORKFLOW, 'utf8'));
      // `user.type != 'Bot'` は dependabot だけでなく App 代行 PR（claude[bot]）まで除外し、
      // 「最後の砦」が外れる。除外は名前で行う（判定はスクリプト側の BOT_AUTHORS）。
      assert.ok(!/user\.type/.test(yml), 'pr-title.yml に user.type 判定が残っている');
      assert.ok(/PR_AUTHOR:\s*\$\{\{\s*github\.event\.pull_request\.user\.login/.test(yml),
        'PR_AUTHOR（作成者ログイン）が渡されていない');
    });

    ok('#524: 同型の user.type 判定が他のワークフローに無い', () => {
      const dir = pathGate.resolve(__dirname, '..', '.github', 'workflows');
      const offenders = fsGate
        .readdirSync(dir)
        .filter((f) => f.endsWith('.yml') || f.endsWith('.yaml'))
        .filter((f) => /user\.type/.test(withoutComments(fsGate.readFileSync(pathGate.join(dir, f), 'utf8'))));
      assert.deepStrictEqual(offenders, [], `user.type 判定が残っている: ${offenders.join(', ')}`);
    });

    // ★ NFR / #757: **キット版 scripts.test.js と重複する言明はここへ書かない。**
    //   キット版が既に固定しているもの（本ブロックから削った分）:
    //     - `isBotLogin` の dependabot[bot] / renovate / claude[bot] / 空 / null
    //       → キット版「isBotLogin は大小文字を無視して完全一致する」
    //     - `checkSingleTitle` の bot=skip / App=検査 / 規約適合 / 作成者未指定
    //       → キット版「PR 作成者 …」6 件
    //   ここに残すのは**キット版が見ていない形だけ**である（下の 4 群）。
    ok('#524: 除外は名前で判定する（App 代行 PR は検査対象に残る）', () => {
      // 群 1: BOT_AUTHORS の 3 番目。キット版は dependabot / renovate しか見ていない。
      assert.strictEqual(isBotLogin('github-actions[bot]'), true);
      // 群 2: 人間のログイン。キット版は claude[bot] だけを負例に持つ。
      assert.strictEqual(isBotLogin('endazon'), false);
      // 群 3: 未定義。キット版は null のみ（`== null` を `=== null` へ狭める変異を捕まえる）。
      assert.strictEqual(isBotLogin(undefined), false);
      // 群 4: 照合は完全一致。部分一致だと BOT_AUTHORS の語を含む**人間のログイン**まで
      // 除外され、最後の砦を無検査で素通りする（PR #527 のレビュー指摘）。
      // キット版は `not-dependabot-really` 1 件のみ。**前方・後方・中間の 3 方向**を残す。
      assert.strictEqual(isBotLogin('the-renovate-guy'), false);
      assert.strictEqual(isBotLogin('dependabot-team'), false);
      assert.strictEqual(isBotLogin('my-github-actions-fan'), false);
      // 大小文字・前後の空白は無視する（キット版は大小のみ）。
      assert.strictEqual(isBotLogin('  Dependabot[Bot] '), true);
    });
  }

  // --- #799: PR タイトル末尾の (#NNN) が PR 自身の番号と一致すること ------------------
  //
  // 検査は形状（`\(#\d+\)$`）しか見ておらず、**起点 issue の番号をタイトルへ書いた PR** が
  // 素通りしていた。実測（2026-08-16・全 PR 443 件）: 末尾に番号を持つ 66 件のうち
  // **自番号と一致するものは 0 件**。GitHub の UI からマージすると自動付加が重なり
  // `… (#796) (#798)` と二重になる（develop に既に 58 件着地しており、事後修正できない）。
  //
  // ★ ［2026-08-17 追記 / #836］**環流が着地し、キット版 scripts.test.js も同じ 4 方向を
  //   固定するようになった**（計画 pin 767a9d48。`checkSingleTitle` を 3 引数で呼ぶ）。
  //   従前ここには「キット版は 2 引数でしか呼ばないため、ここでしか固定されない」と書いていたが、
  //   **その前提は偽になった**。
  //
  //   **それでも本群は削らない。重複しているが、本リポ版が厳密に上位だからである。**
  //   キット版に無い assert: ①違反理由の**文言**（`/外すか/`・`/Closes/`。CI ログを読んで直す人が
  //   要る情報）②**文字列**で渡した PR 番号（`'794'`。実運用は環境変数経由＝必ず文字列である）
  //   ③`normalizePrNumber('12x')`（数字で始まるが数値でない形）④**develop に実際に着地した
  //   6 件名の回帰 fixture と反証** ⑤**ジョブ ID `^  pr-title:$` と起動条件 `types:` の不変性**
  //   （必須チェックの context はジョブ ID であり、改名するとブランチ保護が黙って外れる）。
  //   `scripts.test.js` は分類 A（バイト一致）なので、**上位の assert は companion にしか置けない**。

  {
    const fsNum = require('fs');
    const pathNum = require('path');
    const {
      checkSingleTitle: checkTitle,
      validateTitlePrNumber,
      normalizePrNumber,
    } = require('./check-commit-messages.js');

    // stdout/stderr を抑止して戻り値（0=合格 / 1=違反）だけを見る。
    const silent = (fn) => {
      const so = process.stdout.write;
      const se = process.stderr.write;
      process.stdout.write = () => true;
      process.stderr.write = () => true;
      try {
        return fn();
      } finally {
        process.stdout.write = so;
        process.stderr.write = se;
      }
    };

    // ── 変異試験 4 方向（受け入れ基準そのもの）
    ok('#799: 末尾の番号が PR 自身の番号と違えば fail（方向 1）', () => {
      // 実例: PR #798 のタイトルが起点 issue の番号 (#796) を末尾に持っていた。
      assert.strictEqual(
        silent(() => checkTitle('docs(NFR): 波 6 末クロス監査の指摘を追随させる (#796)', 'endazon', 798)),
        1
      );
    });

    ok('#799: 末尾の番号が PR 自身の番号と一致すれば pass（方向 2）', () =>
      assert.strictEqual(
        silent(() => checkTitle('docs(NFR): 波 6 末クロス監査の指摘を追随させる (#798)', 'endazon', 798)),
        0
      ));

    ok('#799: 末尾に番号が無ければ pass（方向 3。番号は任意のまま）', () =>
      assert.strictEqual(
        silent(() => checkTitle('docs(NFR): 波 6 末クロス監査の指摘を追随させる', 'endazon', 798)),
        0
      ));

    ok('#799: PR_NUMBER 未設定なら形状のみ（方向 4。コミット件名モードを全滅させない）', () => {
      const subject = 'docs(NFR): 波 6 末クロス監査の指摘を追随させる (#796)';
      // 3 引数目を渡さない＝コミット件名モードと同じ扱い。
      assert.strictEqual(silent(() => checkTitle(subject, 'endazon')), 0);
      assert.strictEqual(silent(() => checkTitle(subject, 'endazon', null)), 0);
      assert.strictEqual(silent(() => checkTitle(subject, 'endazon', undefined)), 0);
    });

    // ── 判定器そのもの（理由文言まで固定する。CI ログを読んで直す人が要る情報）
    ok('#799: 違反理由に「外すか、PR 自身の番号にする」直し方が書いてある', () => {
      const reasons = validateTitlePrNumber('chore(NFR): 何か (#791)', 794);
      assert.strictEqual(reasons.length, 1);
      assert.match(reasons[0], /\(#791\)/);
      assert.match(reasons[0], /#794/);
      assert.match(reasons[0], /外すか/);
      assert.match(reasons[0], /Closes/);
    });

    ok('#799: prNumber 未指定・末尾番号なしはいずれも違反 0', () => {
      assert.deepStrictEqual(validateTitlePrNumber('chore(NFR): 何か (#791)', null), []);
      assert.deepStrictEqual(validateTitlePrNumber('chore(NFR): 何か (#791)', undefined), []);
      assert.deepStrictEqual(validateTitlePrNumber('chore(NFR): 何か', 794), []);
      // 文字列で渡ってきても（環境変数経由の実運用）数値として突き合わせる。
      assert.deepStrictEqual(validateTitlePrNumber('chore(NFR): 何か (#794)', '794'), []);
      assert.strictEqual(validateTitlePrNumber('chore(NFR): 何か (#791)', '794').length, 1);
    });

    ok('#799: normalizePrNumber は未設定を null・読めない値を NaN にする', () => {
      assert.strictEqual(normalizePrNumber(undefined), null);
      assert.strictEqual(normalizePrNumber(null), null);
      assert.strictEqual(normalizePrNumber(''), null);
      assert.strictEqual(normalizePrNumber('   '), null);
      assert.strictEqual(normalizePrNumber('798'), 798);
      assert.strictEqual(normalizePrNumber(798), 798);
      assert.strictEqual(normalizePrNumber(' 798 '), 798);
      // 読めない値を黙って null（＝検査しない）へ落とさない。呼び出し側が notice を出す。
      assert.ok(Number.isNaN(normalizePrNumber('abc')));
      assert.ok(Number.isNaN(normalizePrNumber('0')));
      assert.ok(Number.isNaN(normalizePrNumber('-1')));
      assert.ok(Number.isNaN(normalizePrNumber('12x')));
    });

    // ── 既存履歴の回帰: **develop に実際に着地した件名**（二重付加を含む）を
    //    コミット件名モードで通し、番号一致を要求していないことを固定する。
    //    fixture は develop d121ee8c から採った実データである（git に依存させると
    //    shallow clone で黙って 0 件検査になるため、実件名を焼き込む）。
    ok('#799: 実際に着地した件名はコミット件名モードで 1 件も落ちない', () => {
      const landed = [
        'chore(NFR,IADR-0205): 必読規約を減量してキット traceability.md を追随し、分類を A へ戻す (#805)',
        'refactor(FR-14,IADR-0063): BffScopeResolver を Shared.Infrastructure へ切り出す (#229) (#251)',
        'ci(NFR,IADR-0058): planning submodule 込みの doc-links 検査を定期ジョブで追加する (#232) (#236)',
        'docs(IADR-0139): 束ねの上限を 2 件から 4 件へ改定する (#791) (#794)',
        'chore(NFR): 計画 pin を 8cae89d へ進め、キット追随の分類 X を再判定する (#790) (#795)',
        'docs(NFR): 見送り条件が解消した 3 箇所を追随させる (#804)',
      ];
      for (const s of landed) {
        assert.deepStrictEqual(
          validateTitlePrNumber(s, null),
          [],
          `コミット件名モードで番号一致を要求している: ${s}`
        );
      }
      // 反証（この試験が空回りしていないこと）: 同じ件名を PR タイトルとして
      // 別番号で渡せば、必ず違反として上がる。
      assert.strictEqual(validateTitlePrNumber(landed[0], 806).length, 1);
    });

    // ── ワークフロー配線。**起動条件・必須チェックの context を変えていないこと**も固定する。
    ok('#799: pr-title.yml が PR_NUMBER を渡し、起動条件とジョブ ID は不変', () => {
      const yml = fsNum.readFileSync(
        pathNum.resolve(__dirname, '..', '.github', 'workflows', 'pr-title.yml'),
        'utf8'
      );
      assert.match(
        yml,
        /PR_NUMBER:\s*\$\{\{\s*github\.event\.pull_request\.number\s*\}\}/,
        'PR_NUMBER（PR 自身の番号）が渡されていない'
      );
      // 必須チェックの context はジョブ ID である。改名すると保護設定が黙って外れる。
      assert.match(yml, /^ {2}pr-title:$/m, 'ジョブ ID pr-title が変わっている');
      // 起動条件（pull_request の 4 イベント）を狭めていないこと。
      assert.match(yml, /types:\s*\[opened, edited, reopened, synchronize\]/);
    });
  }

  const fs = require('fs');
  const path = require('path');
  const os = require('os');

  // --- check-doc-links: コードファイルへのリンクも検査対象（Issue #470） ----------
  //
  // LINK_EXT にコード拡張子が無かったため、仕様書からコードへの live link は一切検査されず、
  // 破損したまま「OK: 384 件」と報告された（検査器を作る PR が、検査器の穴で自分の参照切れを
  // 見逃した）。正例（実在 → OK）と負例（不在 → 検出）を対で固定する。

  const {
    LINK_EXT: DOC_LINK_EXT,
    isBrokenRef: isBrokenDocRef,
    collectBroken: collectBrokenDocLinks,
  } = require('./check-doc-links.js');

  const CODE_EXTS = ['js', 'mjs', 'cjs', 'ts', 'tsx', 'cs', 'csproj', 'props', 'targets', 'slnx', 'sh'];

  ok('LINK_EXT はコードファイルの拡張子を含む（#470）', () => {
    for (const ext of CODE_EXTS) {
      assert.ok(DOC_LINK_EXT.test(`a.${ext}`), `.${ext} が検査対象に入っていない`);
    }
    // 既存の対象（仕様書・図・スキーマ）を落としていないこと。
    for (const ext of ['md', 'yaml', 'yml', 'json', 'puml', 'mmd', 'png', 'jpeg', 'svg', 'drawio']) {
      assert.ok(DOC_LINK_EXT.test(`a.${ext}`), `.${ext} の検査が落ちている`);
    }
    // 無関係な拡張子まで広げていないこと（誤検知の芽）。
    for (const ext of ['txt', 'tsv', 'log', 'lock']) {
      assert.ok(!DOC_LINK_EXT.test(`a.${ext}`), `.${ext} は検査対象にしない`);
    }
  });

  ok('.js リンクは正例で OK・負例で検出（#470）', () => {
    const here = __dirname;
    assert.strictEqual(isBrokenDocRef('./check-doc-links.js', here), false, '実在する .js を破損としない');
    assert.strictEqual(isBrokenDocRef('./__no_such_script__.js', here), true, '不在の .js を検出する');
    // 対象外の拡張子は従来どおり素通し（実在しなくても検出しない）。
    assert.strictEqual(isBrokenDocRef('./__no_such__.txt', here), false);
  });

  ok('collectBroken は本文・フロントマター・インラインコードの .js を拾う（#470）', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'doclinks-code-'));
    fs.writeFileSync(path.join(dir, 'real.js'), '// fixture\n');
    const md = path.join(dir, 'a.md');
    fs.writeFileSync(
      md,
      '---\nrelated_specs:\n  - ./real.js\n  - ./fm-missing.js\n---\n\n' +
        '# A\n\n[ok](./real.js) と [ng](./missing.js)。\n\nインラインの `./inline-missing.js`。\n'
    );
    const broken = collectBrokenDocLinks(md);
    assert.ok(!broken.includes('./real.js'), '実在する .js を報告しない');
    for (const x of ['./missing.js', './fm-missing.js', './inline-missing.js']) {
      assert.ok(broken.includes(x), `${x} を検出していない: ${JSON.stringify(broken)}`);
    }
    fs.rmSync(dir, { recursive: true, force: true });
  });

  // 自己試験そのものが緑であること（子プロセスで終了コードを実測する）。
  ok('check-doc-links --self-test は exit 0（#470）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-doc-links.js'), '--self-test'], {
      encoding: 'utf8',
    });
    assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}${r.stderr}`);
    assert.match(String(r.stdout), /自己試験 \d+ 件 OK/);
  });

  // --- check-unit-dependencies: ユニット依存方向の検査（Issue #231） -------------

  const {
    pathUnit,
    isSharedProject,
    isTestsProject,
    isBffCompositionHost,
    isUnitBffEndpoints,
    classifyProjectReference,
    scanFoundationComposable,
  } = require('./check-unit-dependencies.js');

  const KNOWLEDGE_DOC =
    'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/DocumentService.Api.csproj';
  const PLATFORM_BFF = 'src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj';
  const SHARED_CONTRACTS =
    'src/platform/backend/Shared/Platform.Shared.Contracts/Platform.Shared.Contracts.csproj';
  const PLATFORM_AUTH =
    'src/platform/backend/Services/AuthorizationService/src/AuthorizationService.Api/AuthorizationService.Api.csproj';
  const INTEGRATION_TESTS =
    'src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj';

  ok('pathUnit は src/<unit>/ を返す', () => {
    assert.strictEqual(pathUnit(KNOWLEDGE_DOC), 'knowledge');
    assert.strictEqual(pathUnit(PLATFORM_BFF), 'platform');
    assert.strictEqual(pathUnit('.ai-context/adr/README.md'), null);
  });

  ok('isSharedProject は platform/backend/Shared 配下のみ true', () => {
    assert.strictEqual(isSharedProject(SHARED_CONTRACTS), true);
    assert.strictEqual(isSharedProject(PLATFORM_AUTH), false);
  });

  ok('isTestsProject は *.Tests.csproj / tests/（大文字小文字問わず）を検出', () => {
    assert.strictEqual(isTestsProject(INTEGRATION_TESTS), true);
    assert.strictEqual(isTestsProject('src/knowledge/backend/Services/X/tests/X.Api.Tests/X.Api.Tests.csproj'), true);
    assert.strictEqual(isTestsProject(KNOWLEDGE_DOC), false);
  });

  ok('可変ユニット → platform Shared は許可', () =>
    assert.strictEqual(classifyProjectReference(KNOWLEDGE_DOC, SHARED_CONTRACTS).ok, true));

  ok('統合テスト → platform サービスは許可（例外）', () =>
    assert.strictEqual(classifyProjectReference(INTEGRATION_TESTS, PLATFORM_AUTH).ok, true));

  ok('platform → 可変ユニットは違反', () =>
    assert.strictEqual(classifyProjectReference(PLATFORM_BFF, KNOWLEDGE_DOC).ok, false));

  // 例外3（BFF 合成点）: Platform.Bff → 可変ユニットの <unit>/backend/Bff/ のみ許可（IADR-0063）。
  const KNOWLEDGE_BFF = 'src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj';
  ok('isBffCompositionHost / isUnitBffEndpoints', () => {
    assert.strictEqual(isBffCompositionHost(PLATFORM_BFF), true);
    assert.strictEqual(isBffCompositionHost(PLATFORM_AUTH), false);
    assert.strictEqual(isUnitBffEndpoints(KNOWLEDGE_BFF), true);
    assert.strictEqual(isUnitBffEndpoints(KNOWLEDGE_DOC), false); // Services 配下は BFF エンドポイントでない
    assert.strictEqual(isUnitBffEndpoints('src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj'), false); // platform は対象外
  });
  ok('例外3: BFF 合成点 → knowledge BFF エンドポイントは許可', () =>
    assert.strictEqual(classifyProjectReference(PLATFORM_BFF, KNOWLEDGE_BFF).ok, true));
  ok('例外3外: platform サービス → knowledge BFF は違反', () =>
    assert.strictEqual(classifyProjectReference(PLATFORM_AUTH, KNOWLEDGE_BFF).ok, false));
  ok('例外3外: BFF 合成点 → knowledge サービスは違反', () =>
    assert.strictEqual(classifyProjectReference(PLATFORM_BFF, KNOWLEDGE_DOC).ok, false));

  ok('可変ユニット（非テスト） → platform 非 Shared は違反', () =>
    assert.strictEqual(classifyProjectReference(KNOWLEDGE_DOC, PLATFORM_AUTH).ok, false));

  ok('Foundation 配下の using .Composable を違反として検出', () => {
    const v = scanFoundationComposable(
      'src/knowledge/backend/Services/DocumentService/src/DocumentService.Api/Foundation/X.cs',
      'using DocumentService.Api.Composable.Steps;\n',
    );
    assert.strictEqual(v.length, 1);
  });

  ok('Foundation 外 / Composable を含まない using は無視', () => {
    assert.strictEqual(
      scanFoundationComposable('src/.../Foundation/X.cs', 'using DocumentService.Api.Foundation.Domain;\n').length,
      0,
    );
    assert.strictEqual(
      scanFoundationComposable('src/.../Program.cs', 'using DocumentService.Api.Composable.Steps;\n').length,
      0,
    );
  });

  ok('Foundation 配下のエイリアス / static using .Composable も検出', () => {
    assert.strictEqual(
      scanFoundationComposable('src/.../Foundation/X.cs', 'using Step = DocumentService.Api.Composable.Steps.SomeStep;\n')
        .length,
      1,
    );
    assert.strictEqual(
      scanFoundationComposable('src/.../Foundation/X.cs', 'using static DocumentService.Api.Composable.Helpers;\n').length,
      1,
    );
  });

  // --- check-image-mapping: MAPPING ↔ compose build ドリフト検査（Issue #275 / IADR-0068） ---

  const {
    parseComposeBuildTargets,
    parseMappingEntries,
    computeDrift,
  } = require('./check-image-mapping.js');

  const IMG_COMPOSE = [
    'services:',
    '  document-service:',
    '    build:',
    '      context: ..',
    '      dockerfile: src/a/Dockerfile',
    '    expose:',
    '      - "8080"',
    '  postgres:',
    '    image: postgres:16-alpine',
    '  frontend:',
    '    build:',
    '      context: ..',
    '      dockerfile: src/platform/frontend/Dockerfile',
    'volumes:',
    '  document-data:',
    '',
  ].join('\n');

  ok('parseComposeBuildTargets は build を持つサービスのみ抽出（infra/ブロック外を除く）', () => {
    const t = parseComposeBuildTargets(IMG_COMPOSE);
    assert.strictEqual(t.length, 2);
    // Issue #283: context/args も抽出する（既定 args は空・context は compose 記載どおり）。
    assert.deepStrictEqual(t[0], { service: 'document-service', context: '..', dockerfile: 'src/a/Dockerfile', args: {} });
    assert.strictEqual(t[1].service, 'frontend');
  });

  ok('parseComposeBuildTargets は build:/dockerfile: の行末コメントを無視する', () => {
    const yaml = [
      'services:',
      '  document-service:',
      '    build:  # comment',
      '      context: ..',
      '      dockerfile: src/a/Dockerfile  # comment',
      '    expose:',
      '      - "8080"',
    ].join('\n');
    const t = parseComposeBuildTargets(yaml);
    assert.strictEqual(t.length, 1);
    assert.deepStrictEqual(t[0], { service: 'document-service', context: '..', dockerfile: 'src/a/Dockerfile', args: {} });
  });

  ok('parseMappingEntries は MAPPING=( ... ) 内の "image|dockerfile" のみ抽出', () => {
    const bash = [
      '# comment',
      'MAPPING=(',
      '  "microservices-platform/document-service|src/a/Dockerfile"',
      '  "microservices-platform/bff|src/b/Dockerfile"',
      ')',
      'echo "microservices-platform/outside|ignored"',
    ].join('\n');
    const e = parseMappingEntries(bash);
    assert.strictEqual(e.length, 2);
    // Issue #283: 2 フィールドエントリは context='.'（リポルート）・args={} 既定へ分解する。
    assert.deepStrictEqual(e[0], { image: 'microservices-platform/document-service', context: '.', dockerfile: 'src/a/Dockerfile', args: {} });
  });

  const IMG_OK_COMPOSE = [
    { service: 'document-service', dockerfile: 'src/a/Dockerfile' },
    { service: 'frontend', dockerfile: 'src/platform/frontend/Dockerfile' },
  ];
  const IMG_OK_MAPPING = [{ image: 'microservices-platform/document-service', dockerfile: 'src/a/Dockerfile' }];

  ok('computeDrift: 整合（compose 専用除外）は違反 0', () => {
    // #313 / IADR-0078: 除外機構は production 既定（空の COMPOSE_ONLY）に依存せず composeOnly を明示して検証する。
    assert.strictEqual(computeDrift({ mappingEntries: IMG_OK_MAPPING, composeTargets: IMG_OK_COMPOSE, composeOnly: ['frontend'] }).length, 0);
  });

  ok('computeDrift: 新サービスの MAPPING 欠落を検出', () => {
    const v = computeDrift({
      mappingEntries: IMG_OK_MAPPING,
      composeTargets: [...IMG_OK_COMPOSE, { service: 'new-service', dockerfile: 'src/n/Dockerfile' }],
    });
    assert.ok(v.some((x) => x.kind === 'missing-mapping'));
  });

  ok('computeDrift: Dockerfile 不一致を検出', () => {
    const v = computeDrift({
      mappingEntries: [{ image: 'microservices-platform/document-service', dockerfile: 'src/OLD/Dockerfile' }],
      composeTargets: IMG_OK_COMPOSE,
    });
    assert.ok(v.some((x) => x.kind === 'dockerfile-mismatch'));
  });

  ok('computeDrift: stale な MAPPING エントリを検出', () => {
    const v = computeDrift({
      mappingEntries: [
        ...IMG_OK_MAPPING,
        { image: 'microservices-platform/removed-service', dockerfile: 'src/x/Dockerfile' },
      ],
      composeTargets: IMG_OK_COMPOSE,
    });
    assert.ok(v.some((x) => x.kind === 'stale-mapping'));
  });

  ok('computeDrift: chart-image の接頭辞違い（命名不整合）を検出', () => {
    const v = computeDrift({
      mappingEntries: [{ image: 'wrong-prefix/document-service', dockerfile: 'src/a/Dockerfile' }],
      composeTargets: IMG_OK_COMPOSE,
    });
    assert.ok(v.some((x) => x.kind === 'naming'));
  });

  ok('computeDrift: compose 専用除外の MAPPING 二重掲載を検出', () => {
    // #313 / IADR-0078: composeOnly を明示して除外機構を検証（frontend は現在 k8s 化済み・MAPPING 掲載が正）。
    const v = computeDrift({
      mappingEntries: [
        ...IMG_OK_MAPPING,
        { image: 'microservices-platform/frontend', dockerfile: 'src/platform/frontend/Dockerfile' },
      ],
      composeTargets: IMG_OK_COMPOSE,
      composeOnly: ['frontend'],
    });
    assert.ok(v.some((x) => x.kind === 'compose-only-in-mapping'));
  });

  ok('computeDrift: 除外リストの腐り（除外対象が compose から消失）を検出', () => {
    // #313 / IADR-0078: composeOnly を明示して除外機構を検証する。
    const v = computeDrift({
      mappingEntries: IMG_OK_MAPPING,
      composeTargets: [{ service: 'document-service', dockerfile: 'src/a/Dockerfile' }],
      composeOnly: ['frontend'],
    });
    assert.ok(v.some((x) => x.kind === 'compose-only-stale'));
  });

  // --- check-realm-constraints: realm フィールド長検査（Issue #18 再発防止） ---

  const {
    charLen,
    collectFields,
    findViolations,
    checkRealmText,
    collectMissingUrls,
    REQUIRED_CLIENT_URLS,
  } = require('./check-realm-constraints.js');

  ok('charLen はコードポイント数（マルチバイトも 1 文字 = 1）', () => {
    assert.strictEqual(charLen('あ'.repeat(300)), 300);
    assert.strictEqual(charLen(null), 0);
  });

  ok('findViolations: 255 文字は合格・256 文字は違反', () => {
    const ok255 = collectFields({ clients: [{ clientId: 'x', description: 'a'.repeat(255) }] });
    const over = collectFields({ clients: [{ clientId: 'x', description: 'a'.repeat(256) }] });
    assert.strictEqual(findViolations(ok255).length, 0);
    assert.strictEqual(findViolations(over).length, 1);
  });

  ok('collectFields は client/role/group/realm を横断走査する', () => {
    const long = 'a'.repeat(256);
    const v = findViolations(collectFields({
      realm: 'r', displayName: long,
      roles: { realm: [{ name: 'a', description: long }], client: { c: [{ name: 'b', description: long }] } },
      groups: [{ name: 'g', subGroups: [{ name: long }] }],
    }));
    assert.strictEqual(v.length, 4);
  });

  ok('collectFields は clientScopes / protocolMappers も走査する', () => {
    const long = 'a'.repeat(256);
    const v = findViolations(collectFields({
      clients: [{ clientId: 'x', protocolMappers: [{ name: long }] }],
      clientScopes: [{ name: 'ok', description: long, protocolMappers: [{ name: long }] }],
    }));
    assert.strictEqual(v.length, 3);
  });

  ok('checkRealmText: 欠損フィールドは例外を投げず無視', () => {
    assert.strictEqual(
      checkRealmText(JSON.stringify({ clients: [{ clientId: 'x' }], roles: {}, groups: null })).length,
      0,
    );
  });

  // --- check-realm-constraints: 経路ごとに必須の URL の欠落検査（Issue #385 再発防止） ---

  const REQ_FIXTURE = {
    'wiki-js': { redirectUris: ['http://localhost:3300/*', 'http://localhost:3001/*'] },
  };

  ok('collectMissingUrls: 必須 URL が揃っていれば欠落なし', () => {
    const realm = {
      clients: [{ clientId: 'wiki-js', redirectUris: ['http://localhost:3001/*', 'http://localhost:3300/*'] }],
    };
    assert.strictEqual(collectMissingUrls(realm, REQ_FIXTURE).length, 0);
  });

  ok('collectMissingUrls: k8s port-forward 用 3300 の欠落を検出する（#385 の回帰）', () => {
    const realm = { clients: [{ clientId: 'wiki-js', redirectUris: ['http://localhost:3001/*'] }] };
    const missing = collectMissingUrls(realm, REQ_FIXTURE);
    assert.strictEqual(missing.length, 1);
    assert.strictEqual(missing[0].url, 'http://localhost:3300/*');
    assert.strictEqual(missing[0].path, 'clients[wiki-js].redirectUris');
  });

  ok('collectMissingUrls: 対象 client が無い realm では検査しない', () => {
    assert.strictEqual(collectMissingUrls({ clients: [{ clientId: 'other' }] }, REQ_FIXTURE).length, 0);
    assert.strictEqual(collectMissingUrls({}, REQ_FIXTURE).length, 0);
  });

  ok('collectMissingUrls: フィールド欠損は必須 URL 全件を欠落として返す', () => {
    assert.strictEqual(collectMissingUrls({ clients: [{ clientId: 'wiki-js' }] }, REQ_FIXTURE).length, 2);
  });

  ok('実 realm の wiki-js は経路別の必須 URL（50000/3300/3001/wiki-js:3000）を満たす', () => {
    const realmPath = path.join(__dirname, '..', 'deploy', 'keycloak', 'microservices-platform-realm.json');
    const realm = JSON.parse(fs.readFileSync(realmPath, 'utf8'));
    assert.deepStrictEqual(collectMissingUrls(realm), []);
    // 経路の取り違え（#385）防止: 4 経路すべてが表に載っていること
    assert.strictEqual(REQUIRED_CLIENT_URLS['wiki-js'].redirectUris.length, 4);
  });

  // --- check-unit-service-ownership: AST 所有サービスの重複デプロイ検査（Issue #407 再発防止） ---

  const {
    parseServiceKeys,
    parseEnabledFlags,
    effectiveEnabled,
    findDuplicateOwnership,
    AST_OWNED_FALLBACK,
  } = require('./check-unit-service-ownership.js');

  const MSP_BASE_FIXTURE = [
    'services:',
    '  document:',
    '    enabled: true',
    '  risk-management:',
    '    enabled: false',
    '    image: microservices-platform/risk-management-service',
    '  market-monitor:',
    '    enabled: false',
    'networkPolicy:',
    '  enabled: true',
  ].join('\n');

  const AST_CHART_FIXTURE = [
    'services:',
    '  risk-management:',
    '    image: ai-stock-trading/risk-management-service',
    '  market-monitor:',
    '    image: ai-stock-trading/market-monitor-service',
    '  trade-decision:',
    '    image: ai-stock-trading/trade-decision-service',
  ].join('\n');

  ok('parseServiceKeys は top-level services 直下のキーのみを返す（深い階層・後続 top-level を拾わない）', () => {
    assert.deepStrictEqual(parseServiceKeys(MSP_BASE_FIXTURE), ['document', 'risk-management', 'market-monitor']);
    assert.deepStrictEqual(parseServiceKeys(AST_CHART_FIXTURE), ['risk-management', 'market-monitor', 'trade-decision']);
  });

  ok('parseServiceKeys: services ブロックが無ければ空', () => {
    assert.deepStrictEqual(parseServiceKeys('global:\n  image:\n    registry: k3d-local\n'), []);
  });

  ok('parseEnabledFlags は enabled の明示値のみを拾う（未指定は欠落＝undefined）', () => {
    const flags = parseEnabledFlags(MSP_BASE_FIXTURE);
    assert.strictEqual(flags.get('document'), true);
    assert.strictEqual(flags.get('risk-management'), false);
    assert.strictEqual(flags.get('market-monitor'), false);
    assert.strictEqual(parseEnabledFlags(AST_CHART_FIXTURE).has('risk-management'), false);
  });

  ok('effectiveEnabled: values-local の enabled: true が本番像の false を上書きする（Helm のマップ deep-merge）', () => {
    const override = 'services:\n  risk-management:\n    enabled: true\n';
    const eff = effectiveEnabled(MSP_BASE_FIXTURE, override);
    assert.strictEqual(eff.has('risk-management'), true);
    assert.strictEqual(eff.has('market-monitor'), false, '上書きの無い false は無効のまま');
    assert.strictEqual(eff.has('document'), true, '本番像の true は維持される');
  });

  ok('effectiveEnabled: 上書きが enabled を書かなければ本番像の値が残る', () => {
    const override = 'services:\n  risk-management:\n    extraEnv:\n      - { name: X, value: "1" }\n';
    assert.strictEqual(effectiveEnabled(MSP_BASE_FIXTURE, override).has('risk-management'), false);
  });

  ok('effectiveEnabled: 上書き側にしか無いサービスも評価対象になる', () => {
    const override = 'services:\n  newcomer:\n    enabled: true\n';
    assert.strictEqual(effectiveEnabled(MSP_BASE_FIXTURE, override).has('newcomer'), true);
  });

  ok('findDuplicateOwnership: AST 所有サービスが MSP で有効なら違反（#407 の回帰）', () => {
    const v = findDuplicateOwnership(new Set(['document', 'risk-management', 'market-monitor']), ['risk-management', 'market-monitor', 'trade-decision']);
    assert.deepStrictEqual(v, ['market-monitor', 'risk-management']);
  });

  ok('findDuplicateOwnership: MSP 固有サービスは AST と同名でなければ違反にならない', () => {
    assert.deepStrictEqual(findDuplicateOwnership(new Set(['document', 'wiki', 'bff']), AST_OWNED_FALLBACK), []);
  });

  ok('findDuplicateOwnership: MSP 側で無効なら AST 所有でも違反にならない（本番像 fail-safe 既定）', () => {
    assert.deepStrictEqual(findDuplicateOwnership(effectiveEnabled(MSP_BASE_FIXTURE, ''), parseServiceKeys(AST_CHART_FIXTURE)), []);
  });

  ok('AST_OWNED_FALLBACK は submodule 未取得時のフォールバックとして 3 画面系を含む', () => {
    for (const s of ['configuration', 'risk-management', 'market-monitor']) {
      assert.ok(AST_OWNED_FALLBACK.includes(s), `${s} が欠けている`);
    }
  });

  ok('実ファイル: 経路B(values-local) で AST 所有サービスが有効化されていない（#407 の回帰）', () => {
    const { checkTree } = require('./check-unit-service-ownership.js');
    assert.deepStrictEqual(checkTree(), []);
  });

  // --- check-test-traceability: 受け入れ基準 → テストの写像（Issue #453） ---------

  const trace = require('./check-test-traceability.js');

  ok('specIdOf: 仕様書ファイル名から起点 ID を取り出す（NFR は連番を丸める）', () => {
    assert.strictEqual(trace.specIdOf('FR-01_data-source-catalog.md'), 'FR-01');
    assert.strictEqual(trace.specIdOf('SC-11_configuration-viewer.md'), 'SC-11');
    assert.strictEqual(trace.specIdOf('NFR-01_performance-load-test.md'), 'NFR');
    assert.strictEqual(trace.specIdOf('TEST_STRATEGY.md'), null);
  });

  ok('idsInText: 修飾付き（AST/FR-17）を除外し裸の ID だけ拾う', () => {
    assert.deepStrictEqual([...trace.idsInText('// FR-03, UC-01: 検索')].sort(), ['FR-03', 'UC-01']);
    assert.strictEqual(trace.idsInText('// AST/FR-17: 別プロジェクト').has('FR-17'), false);
    assert.deepStrictEqual([...trace.idsInText('// AST/FR-17 と FR-03')], ['FR-03']);
    assert.strictEqual(trace.idsInText('XFR-01').size, 0); // 単語の一部は拾わない
    assert.strictEqual(trace.idsInText('// FR-3').has('FR-03'), true); // ゼロ埋め正規化
  });

  ok('classifyAgainstAllowlist: 未写像は blocked、allowlist 内は pending、写像済み残置は stale', () => {
    assert.deepStrictEqual(trace.classifyAgainstAllowlist(['FR-17'], []).blocked, ['FR-17']);
    assert.deepStrictEqual(trace.classifyAgainstAllowlist(['FR-17'], ['FR-17']).pending, ['FR-17']);
    assert.deepStrictEqual(trace.classifyAgainstAllowlist([], ['FR-17']).stale, ['FR-17']);
    const mixed = trace.classifyAgainstAllowlist(['FR-17', 'SC-18'], ['FR-17']);
    assert.deepStrictEqual(mixed.blocked, ['SC-18']);
    assert.deepStrictEqual(mixed.pending, ['FR-17']);
  });

  ok('実ファイル: 仕様書のある起点 ID がすべて写像済み（allowlist の残置も無い）', () => {
    const unmapped = trace.unmappedIds(trace.collectSpecIds(), trace.collectTestIds());
    const { blocked, stale } = trace.classifyAgainstAllowlist(unmapped, trace.readAllowlist());
    assert.deepStrictEqual(blocked, [], `未写像（allowlist 外）: ${blocked.join(' / ')}`);
    assert.deepStrictEqual(stale, [], `allowlist の減らし忘れ: ${stale.join(' / ')}`);
  });

  // --- check-test-traceability: 逆方向検査（計画レンジ・Issue #472） --------------

  // 実ファイルの構造を模したフィクスチャ。**後段に AST（別プロジェクト）の採番レンジを置く**のが要点。
  // 節スコープを外した実装はここで AST のレンジを拾い、計画レンジを取り違える。
  const RULES_FIXTURE = [
    '---', 'paths:', '  - "**/*"', '---', '',
    '## 起点 ID の種別（固有）', '',
    '本リポジトリではそれが **MSP** であり、ID レンジは',
    '`FR-01..21` / `UC-01..11` / `SC-01..21` / `ADR-0001..0039`（`ADR-0035` は番号予約のみ）',
    'である。', '',
    '## 複数プロジェクトを跨ぐ場合の ID 修飾', '',
    '**AST 側が自前で採番しているレンジは `FR-01..20` / `UC-01..07` / `SC-01..03`**',
  ].join('\n');

  ok('planRangeSection / parsePlanRanges: 「起点 ID の種別（固有）」節だけを見る（AST レンジを拾わない）', () => {
    const section = trace.planRangeSection(RULES_FIXTURE);
    assert.ok(section !== null && !section.includes('AST 側'), '節スコープが後段まで伸びている');
    assert.deepStrictEqual(trace.parsePlanRanges(section), {
      FR: { from: 1, to: 21 }, UC: { from: 1, to: 11 }, SC: { from: 1, to: 21 },
    });
    // ADR-xxxx はテスト仕様書の対象外なので拾わない。
    assert.strictEqual(trace.parsePlanRanges(section).ADR, undefined);
    // 節が無ければ null（fail-loud の入口）。
    assert.strictEqual(trace.planRangeSection('# 見出しのみ\n\n本文'), null);
  });

  // NFR: レンジが読めなくなると逆方向検査は「計画 0 件・不足 0 件」という最も安全に見える出力で
  // 素通りする（#472 が塞ごうとしている fail-open そのもの）。壊れた入力は例外にすることで固定する。
  ok('expandPlanIds / readPlanIds: 壊れた入力は例外（黙って 0 件検査に戻さない）', () => {
    assert.throws(() => trace.expandPlanIds({ FR: { from: 1, to: 3 } }), /UC/);
    assert.throws(() => trace.expandPlanIds({ FR: { from: 5, to: 1 }, UC: { from: 1, to: 1 }, SC: { from: 1, to: 1 } }), /範囲/);
    assert.throws(() => trace.readPlanIds(path.join(__dirname, '..', 'no-such-rules-file.md')), /読めません/);
  });

  ok('missingSpecIds / implementedWithoutSpec: 未着手と実装先行を切り分ける', () => {
    const missing = trace.missingSpecIds(['FR-01', 'FR-16', 'UC-01'], new Set(['FR-01', 'NFR']));
    assert.deepStrictEqual(missing, ['FR-16', 'UC-01']);
    // テストが参照済みのものだけが fail 対象（実装先行）。未着手は warn のまま。
    assert.deepStrictEqual(trace.implementedWithoutSpec(missing, new Set(['UC-01'])), ['UC-01']);
    assert.deepStrictEqual(trace.implementedWithoutSpec(missing, new Set(['FR-01'])), []);
  });

  // NFR: 件数は .claude/rules/traceability.md の ID レンジと 1:1 で連動する（#599 で 53 → 54）。
  // 変えるときは「計画側で ID が増減した」ことを実測してから同ファイルと同時に動かす——
  // 先にこの数だけ合わせると、レンジの追随漏れを検出する唯一の網が消える。
  ok('実ファイル: 計画レンジ 54 件を読み、実装先行はすべて allowlist 済み（specMissing の残置も無い）', () => {
    const planIds = trace.readPlanIds();
    assert.strictEqual(planIds.length, 54, `計画レンジの件数が変わった: ${planIds.length}`);
    // FR-22（通知・#599 で planning 891b199 から取り込み）を含む上端を固定する。
    for (const id of ['FR-22', 'UC-11', 'SC-21']) assert.ok(planIds.includes(id), `${id} が欠けている`);
    const missing = trace.missingSpecIds(planIds, trace.collectSpecIds());
    const implFirst = trace.implementedWithoutSpec(missing, trace.collectTestIds());
    const { blocked, stale } = trace.classifyAgainstAllowlist(implFirst, trace.readSpecMissingAllowlist());
    assert.deepStrictEqual(blocked, [], `仕様書なしで実装が先行（allowlist 外）: ${blocked.join(' / ')}`);
    assert.deepStrictEqual(stale, [], `specMissing の減らし忘れ: ${stale.join(' / ')}`);
  });

  // --- check-test-traceability: 担当 issue の突合（無主の区別・Issue #748） --------

  // NFR, #748: warn の並び（未着手）と「引受先が未定（無主）」が区別できず、同型の誤判定が
  // 3 回起きた。3 件とも issue 本文は**範囲表記**で ID を挙げており（#438 の本文に `SC-14` と
  // いう文字列は無い）、素の文字列一致では捕まらない。原文（#748 本文の引用）で回帰させる。
  const OWNER_FIXTURE = [
    { number: 438, text: 'スコープ: Keycloak 統合: 認証画面テーマ（SC-13〜16）／起点 ID: SC-09, SC-13〜17' },
    { number: 445, text: 'スコープ: MCP クライアント登録管理（SC-12 / UC-09）／起点 ID: FR-16 / UC-08, UC-09 / SC-12' },
    { number: 450, text: '起点 ID: FR-17, FR-18 / UC-10 / SC-03, SC-09, SC-10, SC-18, SC-21' },
  ];

  ok('claimedIds: 範囲表記（〜 / ～ / .. / -）を展開し、根拠の表記を保つ（NFR, #748）', () => {
    const claimed = trace.claimedIds(OWNER_FIXTURE[0].text);
    for (const id of ['SC-13', 'SC-14', 'SC-15', 'SC-16', 'SC-17']) assert.ok(claimed.has(id), `${id} が展開されない`);
    assert.strictEqual(claimed.get('SC-14'), 'SC-13〜17'); // 根拠は元の表記そのもの
    assert.ok(trace.claimedIds('SC-13〜SC-17').has('SC-15')); // 両端に種別が付く形
    assert.ok(trace.claimedIds('SC-13～17').has('SC-15'));
    assert.ok(trace.claimedIds('FR-01..22').has('FR-10'));
    assert.ok(trace.claimedIds('SC-09-11').has('SC-10'));
    // 負例: 種別が食い違う形・修飾付き（別プロジェクト）は範囲として展開しない。
    assert.strictEqual(trace.claimedIds('SC-13〜FR-17').has('SC-15'), false);
    assert.strictEqual(trace.claimedIds('AST/SC-01〜03').has('SC-02'), false);
  });

  // NFR, #748: 過去 3 件（SC-14 / SC-15 → #438、UC-09 → #445、UC-10 → #450）の回帰。
  ok('buildIssueOwnership: 過去 3 件の見落としを「担当あり」と判定し根拠を出す（NFR, #748）', () => {
    const ownership = trace.buildIssueOwnership(OWNER_FIXTURE);
    assert.strictEqual(trace.formatOwnership('SC-14', ownership), '#438「SC-13〜17」');
    assert.strictEqual(trace.formatOwnership('SC-15', ownership), '#438「SC-13〜17」');
    assert.match(trace.formatOwnership('UC-09', ownership), /^#445/);
    assert.strictEqual(trace.formatOwnership('UC-10', ownership), '#450「UC-10」');
    assert.deepStrictEqual(trace.unownedPlanIds(['SC-14', 'SC-15', 'UC-09', 'UC-10'], ownership), []);
  });

  // ★ **変異試験**（#748 受け入れ基準 3）。正例だけの緑では、突合対象に入っていなくても
  //   「無主 0 件」が成立してしまう。**無主を仕込むと本当に fail するか**を spawn で実測する。
  ok('担当 issue の突合: 無主を仕込むと exit 1（変異試験。NFR, #748）', () => {
    const { spawnSync } = require('child_process');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'plan-id-owners-'));
    const file = path.join(dir, 'owners.json');
    fs.writeFileSync(file, JSON.stringify({ generatedAt: '2026-08-15T00:00:00Z', issues: OWNER_FIXTURE }));
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-test-traceability.js')],
      { encoding: 'utf8', env: { ...process.env, PLAN_ID_OWNERS: file } });
    const out = `${r.stdout}\n${r.stderr}`;
    assert.strictEqual(r.status, 1, `無主があるのに fail しない:\n${out}`);
    assert.match(out, /担当 issue が無い計画 ID/);
    // 無主として名指しされるのは 3 issue が引き受けていない ID だけである。
    assert.match(out, /SC-20/);
    // ★ 同じ 1 回の実行で、**過去 3 件が無主に混じらない**ことも確かめる（回帰と変異の同時固定）。
    const unowned = (out.match(/\[担当 issue が無い計画 ID\] ([^\n]*)/) || [])[1] || '';
    for (const id of ['SC-14', 'SC-15', 'UC-09', 'UC-10']) {
      assert.ok(!unowned.split(' / ').includes(id), `${id} が無主として出ている: ${unowned}`);
    }
    fs.rmSync(dir, { recursive: true });
  });

  // ★ **実装が完了して issue が閉じた ID を「無主」にしない**（NFR, #748）。
  //
  //   突合材料は **open issue だけ**を載せる（closed の絞り込みは生成側の責務）。したがって
  //   母集合を計画レンジ全件にすると、**完了済みの FR/UC/SC が軒並み「担当 issue が無い」**
  //   になり、CI が全 PR のマージ経路を塞ぐ。本 issue が解こうとしている「未着手と無主の混同」を
  //   「完了済みと無主」の間で作り直すだけである。母集合は `missingSpec` に限る。
  //
  //   この形は **AI レビューが擬似 owners.json で実際に再現して見つけた**（実装済み 29 件が
  //   丸ごと無主として fail した）。素通りしていた理由は、材料が無い間は skip されるため
  //   **生成側が動き出すまで顕在化しない**ことにある。
  ok('担当 issue の突合: テスト仕様書がある ID は無主に混ぜない（NFR, #748）', () => {
    const { spawnSync } = require('child_process');
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'plan-id-owners-closed-'));
    const file = path.join(dir, 'owners.json');
    // **どの ID も引き受けていない**材料（open issue が 1 件も無い状態に相当）。
    fs.writeFileSync(file, JSON.stringify({ generatedAt: '2026-08-15T00:00:00Z', issues: [] }));
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-test-traceability.js')],
      { encoding: 'utf8', env: { ...process.env, PLAN_ID_OWNERS: file } });
    const out = `${r.stdout}\n${r.stderr}`;
    const unowned = ((out.match(/\[担当 issue が無い計画 ID\] ([^\n]*)/) || [])[1] || '').split(' / ');
    // テスト仕様書があるものは、引受先が空でも無主にならない（母集合が missingSpec だから）。
    const specIds = trace.collectSpecIds();
    assert.ok(specIds.size > 0, 'テスト仕様書の ID を 1 件も読めていない（走査が壊れている）');
    const wrongly = [...specIds].filter((id) => unowned.includes(id));
    assert.deepStrictEqual(
      wrongly,
      [],
      `テスト仕様書がある ID を無主として出している（母集合が planIds になっている）:\n${wrongly.join(' / ')}`,
    );
    fs.rmSync(dir, { recursive: true });
  });

  // NFR, #748: 突合材料は「あれば読む、無ければ skip」。**skip を黙って通さない**
  //（「無主 0 件」と「見ていない」が読み分けられないと、検査しているつもりの状態が残る）。
  ok('担当 issue の突合: 材料が無ければ skip し、skip したことを出力する（NFR, #748）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-test-traceability.js')],
      { encoding: 'utf8', env: { ...process.env, PLAN_ID_OWNERS: path.join(os.tmpdir(), 'no-such-owners.json') } });
    assert.strictEqual(r.status, 0, `材料が無いのに落ちる:\n${r.stdout}\n${r.stderr}`);
    assert.match(`${r.stdout}\n${r.stderr}`, /突合は skip しました/);
  });

  ok('check-test-traceability --self-test は exit 0（逆方向検査の正例・負例を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-test-traceability.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // --- check-coverage-floor: バックエンドのカバレッジ床（Issue #453） -------------

  const cov = require('./check-coverage-floor.js');

  const COBERTURA_FIXTURE = [
    '<coverage><packages><package><classes><class><lines>',
    '<line number="1" hits="1" />',
    '<line number="2" hits="0" />',
    '<line number="3" hits="5" branch="true" condition-coverage="50% (1/2)" />',
    '<line number="4" hits="2" branch="true" condition-coverage="100% (2/2)" />',
    '</lines></class></classes></package></packages></coverage>',
  ].join('\n');

  ok('parseCobertura: 行・分岐を数える（属性順・hits 欠落・空入力に耐える）', () => {
    const t = cov.parseCobertura(COBERTURA_FIXTURE);
    assert.strictEqual(t.lines, 4);
    assert.strictEqual(t.covered, 3);
    assert.strictEqual(t.branches, 4);
    assert.strictEqual(t.coveredBranches, 3);
    assert.strictEqual(cov.parseCobertura('<line hits="1" number="9" />').lines, 1);
    assert.strictEqual(cov.parseCobertura('<line number="1" />').lines, 0);
    assert.strictEqual(cov.parseCobertura('').lines, 0);
  });

  ok('rate: 分母 0 は null（未計測を 100% と誤らせない）', () => {
    assert.strictEqual(cov.rate(3, 4), 75);
    assert.strictEqual(cov.rate(0, 0), null);
  });

  ok('findReportsDetailed: 検出/除外の内訳を返す（0 件の原因を切り分けられること）', () => {
    const d = cov.findReportsDetailed();
    // 実リポジトリでは dotnet test 未実行なら 0 件。内訳の整合だけを検証する。
    assert.strictEqual(d.included.length + d.excluded.length, d.all.length);
    assert.ok(d.excluded.every((p) => cov.isExcludedPath(p)));
    assert.ok(d.included.every((p) => !cov.isExcludedPath(p)));
  });

  ok('isExcludedPath（カバレッジ）: AST を合算しない（PR #464 レビュー指摘）', () => {
    assert.strictEqual(cov.isExcludedPath('src/ai-stock-trading/backend/x/TestResults/g/coverage.cobertura.xml'), true);
    assert.strictEqual(cov.isExcludedPath('src/platform/backend/x/TestResults/g/coverage.cobertura.xml'), false);
    assert.strictEqual(cov.isExcludedPath('src/knowledge/backend/x/TestResults/g/coverage.cobertura.xml'), false);
  });

  ok('idsInText: //FR-03（スペース無し）は拾い、AST/FR-17（修飾付き）は拾わない', () => {
    assert.strictEqual(trace.idsInText('//FR-03: x').has('FR-03'), true);
    assert.strictEqual(trace.idsInText('// AST/FR-17').has('FR-17'), false);
    assert.strictEqual(trace.idsInText('XFR-01').size, 0);
  });

  ok('compareToFloor: 床未満は違反・床ちょうどは違反にしない・未計測は判定しない', () => {
    const t = cov.parseCobertura(COBERTURA_FIXTURE); // line 75% / branch 75%
    assert.strictEqual(cov.compareToFloor(t, { line: 80, branch: 70 }).violations.length, 1);
    assert.strictEqual(cov.compareToFloor(t, { line: 75, branch: 75 }).violations.length, 0);
    assert.strictEqual(cov.compareToFloor(t, { line: 90, branch: 90 }).violations.length, 2);
    const empty = { lines: 0, covered: 0, branches: 0, coveredBranches: 0 };
    assert.strictEqual(cov.compareToFloor(empty, { line: 80, branch: 70 }).violations.length, 0);
  });

  // NFR: 床が null へ戻ると check-coverage-floor は集計するだけで判定しなくなる（fail-open）。
  // 「配線済み・未武装」は緑のまま穴が開いた状態であり、退行として検知できない。ここで固定する。
  ok('coverage-floor.json: 床が武装されている（null へ戻る退行を止める）', () => {
    const floorPath = path.join(__dirname, '..', 'src', 'coverage-floor.json');
    const floor = JSON.parse(fs.readFileSync(floorPath, 'utf8')).backend;
    for (const metric of ['line', 'branch']) {
      assert.strictEqual(
        typeof floor[metric], 'number',
        `backend.${metric} が数値でない（null は未武装＝fail-open）: ${JSON.stringify(floor[metric])}`,
      );
      assert.ok(floor[metric] > 0, `backend.${metric} は正の値であること: ${floor[metric]}`);
    }
  });

  // NFR: 床が武装されていても、テストプロジェクトが coverlet.collector を参照していなければ
  // `dotnet test --collect:"XPlat Code Coverage"` は Cobertura を 1 件も出さない。レポート 0 件は
  // fail-open（warn で素通り）のため、床は緑のまま静かに無効化される——#453 の実測でまさに
  // これが起きていた（MSP 14 プロジェクト中 0 件が参照。CI が拾っていた 38 件はすべて AST）。
  // 床の null 化と同じ性質の穴であり、参照が外れる退行をここで固定する。
  ok('テストプロジェクトはすべて coverlet.collector を参照する（カバレッジの無音失効を止める）', () => {
    const repoRoot = path.join(__dirname, '..');
    // ai-stock-trading は別プロジェクト（submodule）であり床の対象外。
    // check-coverage-floor.js / check-test-traceability.js の EXCLUDED_UNITS と同じ切り分け。
    const units = ['platform', 'knowledge'];
    const skipDirs = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'coverage']);
    const found = [];
    const walk = (dir) => {
      let entries = [];
      try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch (e) { return; }
      for (const e of entries) {
        const full = path.join(dir, e.name);
        if (e.isDirectory()) {
          if (!skipDirs.has(e.name)) walk(full);
        } else if (/Tests\.csproj$/.test(e.name)) {
          found.push(full);
        }
      }
    };
    for (const u of units) walk(path.join(repoRoot, 'src', u));

    // ratchet: テストプロジェクトを増やしたらこの実数を更新する。「N 件以上」にすると
    // 走査が壊れて 0 件になったときにテストが空振りで green になる（穴を塞ぐのが本テストの目的）。
    // ［2026-08-21 / #455］Platform.Shared.Kernel.Tests の新設で 14 → 15。
    // ［2026-08-22 / #455 U4］Platform.Shared.Infrastructure.Tests の新設で 15 → 16
    // （ADR-0027 手順 3〜5 の共通ヘルパと、部分移行の安全弁を試験する）。
    assert.strictEqual(
      found.length, 16,
      `テストプロジェクトの検出数が想定と異なる（走査の破損 or 増減。増えたなら本数を更新する）: ${found.length} 件\n` +
        found.map((f) => path.relative(repoRoot, f)).join('\n'),
    );

    const missing = found.filter((f) => {
      const xml = fs.readFileSync(f, 'utf8');
      return !/<PackageReference\s+Include="coverlet\.collector"/.test(xml);
    });
    assert.deepStrictEqual(
      missing.map((f) => path.relative(repoRoot, f)), [],
      'coverlet.collector を参照しないテストプロジェクトがある（XPlat Code Coverage が何も出力せず床が無効化される）',
    );
  });

  // --- check-coverage-floor: 合成点テスト経由の混入を filename 帰属で除く（#468 / IADR-0123） ---
  //
  // NFR（#468）: Platform.Bff は BFF の合成点として AST の Bff エンドポイントを ProjectReference するため、
  // src/platform/ 配下に出るレポートの**中身**に AST のクラスが入る。レポートファイルのパスによる除外
  // （isExcludedPath）はこれに届かない。ここで固定するのは次の 3 点である。
  //   1. <class filename> でユニットへ帰属させ、除外ユニットの行が集計から落ちること
  //   2. filename の形（相対 / 絶対 / <sources> 結合）に依らず帰属できること
  //      ——coverlet は base path で始まらないファイルを**絶対パスのまま**書くため、片方に決め打つと
  //        フィルタが何にもマッチせず「除外したつもりで素通り」になる（黙って混入が残る）
  //   3. 二重記載（<methods> 配下 と class 直下）を class 直下だけで数えること

  const AST_UNIT = [...cov.EXCLUDED_UNITS][0];

  /** class 1 件ぶんの XML（<methods> 配下と class 直下に同じ行を書く coverlet の形）。 */
  const coberturaClass = (name, filename, lines) => {
    const body = lines.map(([n, h]) => `<line number="${n}" hits="${h}" />`).join('');
    return `<class name="${name}" filename="${filename}">` +
      `<methods><method name="M"><lines>${body}</lines></method></methods>` +
      `<lines>${body}</lines></class>`;
  };
  const coberturaReport = (classes, { sources = [], attrs = '' } = {}) =>
    `<?xml version="1.0"?><coverage ${attrs}>` +
    (sources.length ? `<sources>${sources.map((s) => `<source>${s}</source>`).join('')}</sources>` : '') +
    `<packages><package name="Platform.Bff"><classes>${classes.join('')}</classes></package></packages></coverage>`;

  ok('parseCobertura: 二重記載は class 直下の <lines> だけを数える（<methods> 配下は内訳）', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Bff.X', 'src/platform/backend/Bff/X.cs', [[1, 1], [2, 0], [3, 4]]),
    ]);
    const p = cov.parseCobertura(xml);
    // 素朴に全 <line> を数えると 6 行になる（PR #464 のレビューで 266/230 と実測が割れた原因）。
    assert.strictEqual(p.lines, 3);
    assert.strictEqual(p.covered, 2);
  });

  ok('parseCobertura: 除外ユニットへ帰属した行を集計から落とす（相対 filename）', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Bff.X', 'src/platform/backend/Bff/X.cs', [[1, 1], [2, 0]]),
      coberturaClass('AiStockTrading.Bff.Endpoints.MonitorBffEndpoints',
        `src/${AST_UNIT}/backend/Bff/MonitorBffEndpoints.cs`, [[1, 3], [2, 3], [3, 3]]),
    ]);
    const p = cov.parseCobertura(xml);
    assert.strictEqual(p.lines, 2, '集計は platform の 2 行だけ');
    assert.strictEqual(p.excluded.lines, 3);
    assert.strictEqual(p.excluded.covered, 3, '混入行はすべて被覆済み＝実測値を押し上げる方向にしか働かない');
    assert.strictEqual(p.excluded.classes.length, 1);
    assert.match(p.excluded.classes[0].name, /MonitorBffEndpoints/);
  });

  ok('parseCobertura: 絶対 filename でも帰属する（base path で始まらないファイルは絶対のまま書かれる）', () => {
    const xml = coberturaReport([
      coberturaClass('X', `/home/runner/work/msp/msp/src/${AST_UNIT}/backend/Bff/X.cs`, [[1, 1]]),
    ], { sources: ['/home/runner/work/msp/msp/src/platform/backend/Bff/Platform.Bff/'] });
    const p = cov.parseCobertura(xml);
    assert.strictEqual(p.excluded.lines, 1);
    assert.strictEqual(p.diagnostics.how.absolute, 1);
  });

  ok('parseCobertura: <sources> と結合して帰属する（base path が src/ より深い場合）', () => {
    const xml = coberturaReport([
      coberturaClass('X', `${AST_UNIT}/backend/Bff/X.cs`, [[1, 1]]),
      coberturaClass('Y', 'platform/backend/Bff/Y.cs', [[1, 0]]),
    ], { sources: ['/home/runner/work/msp/msp/src/'] });
    const p = cov.parseCobertura(xml);
    assert.strictEqual(p.excluded.lines, 1);
    assert.strictEqual(p.lines, 1);
    assert.strictEqual(p.diagnostics.how['source-joined'], 2);
  });

  ok('parseCobertura: 帰属できない行は集計に残す（黙って落とさない）', () => {
    const xml = coberturaReport([coberturaClass('X', 'Foo/Bar.cs', [[1, 1], [2, 1]])]);
    const p = cov.parseCobertura(xml);
    assert.strictEqual(p.lines, 2);
    assert.strictEqual(p.excluded.lines, 0);
    assert.strictEqual(p.diagnostics.how.unattributed, 1);
  });

  ok('attributionMessages: 帰属 0 件は warn（フィルタが何にもマッチしない＝素通りの検出）', () => {
    const agg = cov.aggregateReports([cov.parseCobertura(
      coberturaReport([coberturaClass('X', 'Foo/Bar.cs', [[1, 1]])]))]);
    const msgs = cov.attributionMessages(agg);
    assert.ok(msgs.some((m) => m.level === 'warn' && /帰属できませんでした/.test(m.text)),
      `帰属 0 件で warn が出ていない: ${JSON.stringify(msgs)}`);
  });

  ok('attributionMessages: 除外 0 行は notice に留める（恒常的な警告にしない）', () => {
    const agg = cov.aggregateReports([cov.parseCobertura(
      coberturaReport([coberturaClass('X', 'src/platform/backend/X.cs', [[1, 1]])]))]);
    const msgs = cov.attributionMessages(agg);
    assert.ok(msgs.some((m) => m.level === 'notice' && /0 行でした/.test(m.text)));
    assert.deepStrictEqual(msgs.filter((m) => m.level === 'warn'), []);
  });

  ok('attributionMessages: class の外にある <line> は warn（除外できない行の可視化）', () => {
    const agg = cov.aggregateReports([cov.parseCobertura('<coverage><line number="1" hits="1" /></coverage>')]);
    assert.ok(cov.attributionMessages(agg).some((m) => m.level === 'warn' && /<class> にも属さない/.test(m.text)));
  });

  ok('aggregateReports: 除外前の値は coverlet 自身の lines-valid と照合できる（IADR-0123 決定 4）', () => {
    const xml = coberturaReport([
      coberturaClass('X', 'src/platform/backend/X.cs', [[1, 1], [2, 0]]),
      coberturaClass('Y', `src/${AST_UNIT}/backend/Y.cs`, [[1, 1]]),
    ], { attrs: 'lines-valid="3" lines-covered="2"' });
    const agg = cov.aggregateReports([cov.parseCobertura(xml)]);
    assert.strictEqual(agg.totals.lines, 2);
    assert.strictEqual(agg.excluded.lines, 1);
    assert.strictEqual(agg.beforeExclusion.lines, 3);
    assert.strictEqual(agg.diagnostics.reported.lines, 3, 'coverlet の lines-valid と一致する（前提の裏づけ）');
    assert.strictEqual(agg.diagnostics.reported.covered, 2);
  });

  // NFR（#468）: 混入行数の確定と床の置き直しは CI ログの診断出力から読み取る。
  // 出力が壊れると「測り直す手段」が黙って失われるため、鍵となる文言と数値をここで固定する。
  ok('formatDiagnostics: 除外行数・除外前の実測・解釈の内訳・除外クラス一覧が出る', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Bff.X', 'src/platform/backend/X.cs', [[1, 1], [2, 0]]),
      coberturaClass('AiStockTrading.Bff.Endpoints.MonitorBffEndpoints',
        `src/${AST_UNIT}/backend/Bff/MonitorBffEndpoints.cs`, [[1, 1]]),
    ]);
    const text = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(xml)])).join('\n');
    assert.match(text, /除外（filename 帰属・#468）/);
    assert.match(text, /1 行（被覆 1）/);          // 混入行数（確定値の読み取り口）
    assert.match(text, /除外前: line 66\.67%（2\/3）/); // 除外前の実測（床の置き直しの突き合わせ）
    assert.match(text, /そのまま\(相対\) 2/);       // filename の解釈
    assert.match(text, /MonitorBffEndpoints/);      // 除外したクラス
  });

  // NFR（#468）: CI 初回実走（run 1144 / commit 594117a）で **行は完全一致・分岐だけ乖離**した。
  // 本実装の「分岐」は <line> の condition-coverage の分母/分子、coverlet の branches-valid は別定義
  // であり、一致を期待しない（IADR-0123 決定 4 の［2026-08-04 追記］）。同列に「乖離」と出すと
  // 期待される差が異常に見えるため、書き分けをここで固定する。
  ok('formatDiagnostics: 行の乖離は要調査・分岐の差は定義差として書き分ける', () => {
    const report = (attrs) => coberturaReport([
      `<class name="X" filename="src/platform/backend/X.cs"><lines>` +
      '<line number="1" hits="1" branch="true" condition-coverage="50% (1/2)" /></lines></class>',
    ], { attrs });
    const same = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(
      report('lines-valid="1" lines-covered="1" branches-valid="4" branches-covered="1"'))])).join('\n');
    assert.ok(same.includes('lines-valid 1（本実装 1・一致）'), same);
    assert.ok(same.includes('差 -2（定義差・期待される乖離）'), same);
    assert.ok(!same.includes('**乖離'), `分岐の差を行と同列の「乖離」で出している:\n${same}`);

    const drift = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(
      report('lines-valid="9" lines-covered="9" branches-valid="2" branches-covered="1"'))])).join('\n');
    assert.ok(drift.includes('**乖離 -8・要調査**'), drift);

    // branches-covered も出す（coverlet 側の実際の値が CI ログから読めること）。
    assert.ok(same.includes('branches-covered 1（本実装 1・一致）'), same);

    // 床の値は src/coverage-floor.json が単一情報源（IADR-0118 決定 1）。診断へ数値を焼き込むと
    // ratchet で床を上げた瞬間に同じログの中で自己矛盾する。引数の floor を反映すること。
    const withFloor = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(
      report('lines-valid="1" lines-covered="1" branches-valid="4" branches-covered="1"'))]), { branch: 18 }).join('\n');
    assert.ok(withFloor.includes('床 18 はこの方式'), withFloor);
    assert.ok(!withFloor.includes('床 17'), `床の値をハードコードしている:\n${withFloor}`);

    // 分岐が一致していれば注記は出さない（恒常的なノイズにしない）。
    const branchSame = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(
      report('lines-valid="1" lines-covered="1" branches-valid="2" branches-covered="1"'))])).join('\n');
    assert.ok(!branchSame.includes('※ 分岐は'), branchSame);
  });

  // NFR（#468 / IADR-0123 決定 5）: 分岐は定義差のため coverlet 値との照合が反証力を持たない。
  // 分岐側の二重記載排除が壊れても値が増えるだけで CI ログには何も現れない（無音の失敗）。
  // 「全 <line>（<methods> 重複込み）」と「class 直下のみ」の比を観測点として出すことを固定する。
  ok('formatDiagnostics: 二重記載の観測（全 <line> と class 直下の比）を出す', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Bff.X', 'src/platform/backend/X.cs', [[1, 1], [2, 0]]),
    ]);
    const text = cov.formatDiagnostics(cov.aggregateReports([cov.parseCobertura(xml)])).join('\n');
    assert.ok(text.includes('全 <line>（<methods> 重複込み）= 行 4'), text);
    assert.ok(text.includes('class 直下のみ（除外前の集計）= 行 2'), text);
    assert.ok(text.includes('比 行 2.00'), text);
  });

  // --- check-coverage-floor: レポート跨ぎの行重複排除（#900 / IADR-0235） ---
  //
  // NFR（#900）: テストプロジェクト A と B が同じ共有ライブラリを参照すると、同じソース行が
  // 両方の Cobertura に載り、単純合算では分母に 2 回入る（#899 で実際に床が割れた）。
  //
  // 🔴 ここで固定するのは「素朴な合算」と「畳み込み後」が**同じ入力で異なる**ことである。
  //   差の存在を assert しないと、aggregateReports が畳み込みを呼ばなくなる変異（配線切れ）が
  //   すり抜ける —— check-backend-libraries.js 規則 5 が「(a) だけでは静かに no-op になる」で
  //   踏んだ穴と同型である。**合算はテスト内で再実装せず、既存 export の mergeTotals を使う。**
  ok('レポート跨ぎの重複排除: 素朴な合算と畳み込み後が同じ入力で異なる（配線ごと効いている）', () => {
    const xml = coberturaReport([
      coberturaClass('Platform.Shared.Infrastructure.Db',
        'src/platform/backend/Shared/Platform.Shared.Infrastructure/Db.cs', [[1, 1], [2, 0]]),
    ]);
    const parsed = [cov.parseCobertura(xml), cov.parseCobertura(xml)];
    const naive = cov.mergeTotals(parsed);        // 旧方式（単純合算）
    const agg = cov.aggregateReports(parsed);     // 配線済みの入口を通す
    assert.strictEqual(naive.lines, 4, '単純合算は分母が 2 倍になる（前提の確認）');
    assert.strictEqual(agg.totals.lines, 2, '畳み込み後は 1 部ぶん');
    assert.strictEqual(agg.totals.covered, 1);
    assert.notStrictEqual(agg.totals.lines, naive.lines, '差が出ること自体を固定（no-op 化の検出）');
    assert.strictEqual(agg.diagnostics.dedup.droppedLines, naive.lines - agg.totals.lines,
      '診断の droppedLines は単純和との差と一致する');
    // 🔴 決定 4 の照合は重複排除**前**の単純和で行う（畳んだ値から組むと恒常的に割れる）。
    assert.strictEqual(agg.beforeExclusion.lines, 4, 'beforeExclusion は単純和のまま');
  });

  ok('レポート跨ぎの重複排除: キーは正規化経路で作る（生 filename では畳めない）', () => {
    const a = coberturaReport([coberturaClass('Shared.X', 'src/platform/backend/Shared/X.cs', [[1, 1]])]);
    const b = coberturaReport([coberturaClass('Shared.X', 'platform/backend/Shared/X.cs', [[1, 0]])],
      { sources: ['/home/runner/work/msp/msp/src/'] });
    const agg = cov.aggregateReports([cov.parseCobertura(a), cov.parseCobertura(b)]);
    assert.strictEqual(agg.totals.lines, 1, '表記の違う同一ファイルが畳まれていない');
    assert.strictEqual(agg.totals.covered, 1, 'hits>0 の OR が効いていない');
    assert.strictEqual(
      cov.dedupFileKey(cov.unitOfFilename('src/platform/backend/Shared/X.cs')).key,
      cov.dedupFileKey(cov.unitOfFilename('platform/backend/Shared/X.cs', ['/home/runner/work/msp/msp/src/'])).key,
    );
  });

  ok('レポート跨ぎの重複排除: Foo と <Foo>d__2 は潰さない（キーに class name が要る）', () => {
    const only = (name, hits) => coberturaReport([
      coberturaClass(name, 'src/platform/backend/X.cs', [[10, hits]]),
    ]);
    const agg = cov.aggregateReports([cov.parseCobertura(only('Foo', 1)),
      cov.parseCobertura(only('Foo/<Foo>d__2', 0))]);
    assert.strictEqual(agg.totals.lines, 2,
      '同一行を異なる観点で計測した行が潰れている（IADR-0123 選択肢 C の破れ）');
  });

  ok('check-coverage-floor --self-test は exit 0（帰属・二重記載・warn 経路を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-coverage-floor.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // 実ツリーへレポートを 1 件だけ置いて素実行する。関数単位の試験では「走査 → 除外 → 出力」の
  // 配線（レポートが実際に読まれ、診断が CI ログへ出ること）を確かめられないため。
  // coverage*.xml は .gitignore 済みだが、異常終了時に残さないよう finally で必ず撤去する。
  ok('素実行: 実ツリーのレポートから AST 由来の行を落とし、診断を出して exit 0', () => {
    const { spawnSync } = require('child_process');
    const repoRoot = path.join(__dirname, '..');
    const dir = path.join(repoRoot, 'src', 'platform', 'backend', '.coverage-floor-probe', 'TestResults', 'probe');
    const file = path.join(dir, 'coverage.cobertura.xml');
    try {
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(file, coberturaReport([
        coberturaClass('Platform.Bff.X', 'platform/backend/Bff/X.cs', [[1, 1], [2, 0], [3, 1]]),
        coberturaClass('AiStockTrading.Bff.Endpoints.MonitorBffEndpoints',
          `${AST_UNIT}/backend/Bff/MonitorBffEndpoints.cs`, [[1, 1], [2, 1]]),
      ], { sources: ['/home/runner/work/msp/msp/src/'], attrs: 'lines-valid="5" lines-covered="4"' }));

      const r = spawnSync(process.execPath, [path.join(__dirname, 'check-coverage-floor.js')], { encoding: 'utf8' });
      assert.strictEqual(r.status, 0, `素実行が失敗:\n${r.stdout}\n${r.stderr}`);
      assert.match(r.stdout, /レポート 1 件を集計: line 66\.67%（2\/3）/);
      assert.match(r.stdout, /由来 1 クラス \/ 2 行（被覆 2）/);
      assert.match(r.stdout, /除外前: line 80%（4\/5）/);
      assert.match(r.stdout, /lines-valid 5（本実装 5・一致）/);
    } finally {
      fs.rmSync(path.join(repoRoot, 'src', 'platform', 'backend', '.coverage-floor-probe'), { recursive: true, force: true });
    }
  });

  // --- check-backend-libraries: ADR-0030 ライブラリ標準の機械強制（Issue #455） ---

  const backendLibs = require('./check-backend-libraries.js');

  ok('bannedInCsproj: 不採用パッケージを検出し採用パッケージは無視する', () => {
    assert.deepStrictEqual(
      backendLibs.bannedInCsproj('<PackageReference Include="MassTransit.RabbitMQ" /><PackageReference Include="FluentValidation" />'),
      ['MassTransit']);
    assert.deepStrictEqual(backendLibs.bannedInCsproj('<PackageReference Include="Riok.Mapperly" />'), []);
  });

  ok('bannedInSource: using の各形（global / static / エイリアス）を拾い、ブロック構文は拾わない', () => {
    assert.deepStrictEqual(
      backendLibs.bannedInSource('global using Serilog;\nusing static FluentAssertions.AssertionExtensions;\nusing M = MassTransit.IBus;\n'),
      ['FluentAssertions', 'MassTransit', 'Serilog']);
    assert.deepStrictEqual(backendLibs.bannedInSource('using (var x = new MassTransitThing()) { }\n'), []);
  });

  ok('matchesBanned: 前方一致はドット区切りのときだけ効く（Serilog vs SerilogExtras）', () => {
    assert.strictEqual(backendLibs.matchesBanned('Serilog.AspNetCore', 'Serilog'), true);
    assert.strictEqual(backendLibs.matchesBanned('SerilogExtras', 'Serilog'), false);
  });

  ok('classifyAgainstBaseline: 新規混入は added（fail 対象）', () => {
    const r = backendLibs.classifyAgainstBaseline({ 'a.csproj': ['MassTransit'] }, {});
    assert.strictEqual(r.added.length, 1);
    assert.strictEqual(r.known.length, 0);
    assert.strictEqual(r.stale.length, 0);
  });

  ok('classifyAgainstBaseline: baseline どおりは known（warn のみ）', () => {
    const r = backendLibs.classifyAgainstBaseline({ 'a.csproj': ['MassTransit'] }, { 'a.csproj': ['MassTransit'] });
    assert.strictEqual(r.known.length, 1);
    assert.strictEqual(r.added.length, 0);
    assert.strictEqual(r.stale.length, 0);
  });

  ok('classifyAgainstBaseline: 解消済みなのに baseline に残るのは stale（減らし忘れ検出）', () => {
    const r = backendLibs.classifyAgainstBaseline({}, { 'a.csproj': ['MassTransit'] });
    assert.strictEqual(r.stale.length, 1);
    assert.strictEqual(r.added.length, 0);
  });

  ok('domainViolations: Domain は外部依存ゼロ・共有カーネル参照のみ許可', () => {
    const p = 'src/platform/backend/X.Domain.csproj';
    assert.strictEqual(backendLibs.domainViolations(p, '<PackageReference Include="FluentValidation" />').length, 1);
    assert.strictEqual(
      backendLibs.domainViolations(p, '<ProjectReference Include="../Shared/Platform.Shared.Kernel/Platform.Shared.Kernel.csproj" />').length, 0);
    assert.strictEqual(
      backendLibs.domainViolations(p, '<ProjectReference Include="../X.Infrastructure/X.Infrastructure.csproj" />').length, 1);
    // Domain 以外は対象外
    assert.strictEqual(backendLibs.domainViolations('src/platform/backend/X.Api.csproj', '<PackageReference Include="MediatR" />').length, 0);
  });

  ok('isExcludedPath: ADR-0030 は MSP の決定であり ai-stock-trading（別プロジェクト）は対象外', () => {
    assert.strictEqual(backendLibs.isExcludedPath('src/ai-stock-trading/backend/Services/X/src/X.Api/X.Api.csproj'), true);
    assert.strictEqual(backendLibs.isExcludedPath('src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj'), false);
    assert.strictEqual(backendLibs.isExcludedPath('src/knowledge/backend/Shared/Knowledge.Contracts/Knowledge.Contracts.csproj'), false);
  });

  ok('xunitRunnerMismatch: xunit.v3 と CPM の runner 2.x の同居を検出（PR #463 レビュー指摘の回帰）', () => {
    const v3 = '<PackageReference Include="xunit.v3" /><PackageReference Include="xunit.runner.visualstudio" />';
    assert.strictEqual(backendLibs.centralVersionOf('<PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />', 'xunit.runner.visualstudio'), '2.8.2');
    assert.strictEqual(backendLibs.majorOf('2.8.2'), 2);
    assert.strictEqual(backendLibs.majorOf('3.1.5'), 3);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v3, '2.8.2').length, 1);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v3, '3.1.5').length, 0);
    // v2 の組み合わせ・runner 非参照・CPM 未定義はいずれも判定しない
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj',
      '<PackageReference Include="xunit" /><PackageReference Include="xunit.runner.visualstudio" />', '2.8.2').length, 0);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', '<PackageReference Include="xunit.v3" />', '2.8.2').length, 0);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v3, null).length, 0);
  });

  ok('xunitRunnerMismatch: 逆方向も検出する —— xunit（v2）と CPM の runner 3.x の同居（#455 A-2）', () => {
    // A-2 で runner を 3.x へ上げた。以後は「v2 のまま取り残されたプロジェクト」が非互換になる。
    // CPM は 1 パッケージ 1 バージョンしか持てず v2/v3 は共存できないため、一斉切替でしか成立しない。
    // その「一斉である」性質を機械で担保する半分がこれである。
    const v2 = '<PackageReference Include="xunit" /><PackageReference Include="xunit.runner.visualstudio" />';
    const v3 = '<PackageReference Include="xunit.v3" /><PackageReference Include="xunit.runner.visualstudio" />';
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v2, '3.1.5').length, 1);
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v2, '2.8.2').length, 0);
    // runner を参照しなければ判定しない（両方向とも）
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', '<PackageReference Include="xunit" />', '3.1.5').length, 0);
    // 前方一致の取り違え防止: xunit.v3 だけを参照するプロジェクトを v2 と誤認しない
    assert.strictEqual(backendLibs.xunitRunnerMismatch('t/X.Tests.csproj', v3, '3.1.5').length, 0);
  });

  // --- check-backend-libraries: 検出漏れの是正（Issue #471） ---

  ok('BANNED: Kiota は実在 ID（Microsoft.Kiota.*）で登録され、旧 "Kiota" の死にエントリが残っていない', () => {
    // 'Kiota' は完全一致にも 'Kiota.' 前方一致にも当たらず 1 件も検出できなかった（#471）。
    assert.strictEqual(backendLibs.BANNED.includes('Kiota'), false);
    assert.strictEqual(backendLibs.bannedNameOf('Microsoft.Kiota.Abstractions'), 'Microsoft.Kiota');
    assert.deepStrictEqual(
      backendLibs.bannedInCsproj('<PackageReference Include="Microsoft.Kiota.Abstractions" Version="1.0.0" />'),
      ['Microsoft.Kiota']);
  });

  ok('BANNED: ADR-0030 棚卸し表の不採用・置換対象（Kafka / RabbitMQ 素クライアント・Key Vault・Argon2）を含む', () => {
    for (const id of ['Confluent.Kafka', 'RabbitMQ.Client', 'Azure.Security.KeyVault.Secrets',
      'Azure.Extensions.AspNetCore.Configuration.Secrets', 'Konscious.Security.Cryptography.Argon2',
      'Isopoh.Cryptography.Argon2']) {
      assert.notStrictEqual(backendLibs.bannedNameOf(id), null, `${id} が BANNED に無い`);
    }
    // 採用側・無関係を巻き込まない（前方一致の境界）。
    for (const id of ['WolverineFx.Kafka', 'WolverineFx.RabbitMQ', 'Azure.Identity',
      'Konscious.Security.Cryptography.Blake2', 'Isopoh.Cryptography.Blake2b']) {
      assert.strictEqual(backendLibs.bannedNameOf(id), null, `${id} を誤検出している`);
    }
  });

  ok('isScannedBuildFile: props / targets（雛形の .sample 含む）も走査対象', () => {
    for (const p of ['src/x/X.csproj', 'src/Directory.Build.props', 'src/Directory.Build.targets',
      'src/x/Custom.props', 'templates/unit-template/backend/Directory.Packages.props.sample']) {
      assert.strictEqual(backendLibs.isScannedBuildFile(p), true, `${p} が対象外`);
    }
    for (const p of ['src/x/X.cs', 'src/x/backend.slnx', 'src/x/README.md']) {
      assert.strictEqual(backendLibs.isScannedBuildFile(p), false, `${p} が対象になっている`);
    }
  });

  ok('PackageVersion は違反にせず GlobalPackageReference は違反にする（CPM 走査追加の偽陽性防止）', () => {
    // Directory.Packages.props は baseline 消化まで不採用パッケージの**版定義**を正当に持つ。
    // ここを違反にすると走査対象への追加だけで 42 件の偽陽性が出る（#471）。
    assert.deepStrictEqual(
      backendLibs.bannedInCsproj('<PackageVersion Include="MassTransit" Version="8.4.1" />'
        + '<PackageVersion Include="Serilog.AspNetCore" Version="10.0.0" />'), []);
    // 一方 GlobalPackageReference は全プロジェクトへ参照を注入するため違反。
    assert.deepStrictEqual(
      backendLibs.bannedInCsproj('<GlobalPackageReference Include="Serilog" Version="4.0.0" />'), ['Serilog']);
  });

  ok('実ファイル: CPM の props（本体・雛形）は不採用パッケージの版定義を持つが違反 0', () => {
    for (const rel of ['src/Directory.Packages.props', 'src/Directory.Build.props',
      'templates/unit-template/backend/Directory.Packages.props.sample',
      'templates/unit-template/backend/Directory.Build.props.sample']) {
      const xml = fs.readFileSync(path.join(__dirname, '..', rel), 'utf8');
      assert.deepStrictEqual(backendLibs.bannedInCsproj(xml), [], `${rel} で偽陽性`);
    }
  });

  ok('--self-test は exit 0（検出漏れ 3 種の実地確認を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-backend-libraries.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  ok('実ファイル: 新規混入 0 件・Domain 依存規律 OK（baseline との突合）', () => {
    const { current, domain } = backendLibs.scanTree();
    const baseline = JSON.parse(fs.readFileSync(path.join(__dirname, 'backend-library-baseline.json'), 'utf8')).projects;
    const { added, stale } = backendLibs.classifyAgainstBaseline(current, baseline);
    assert.deepStrictEqual(added, [], `baseline に無い新規混入: ${JSON.stringify(added)}`);
    assert.deepStrictEqual(stale, [], `解消済みなのに baseline に残る行: ${JSON.stringify(stale)}`);
    assert.deepStrictEqual(domain, [], `Domain 依存規律違反: ${JSON.stringify(domain)}`);
  });

  // --- lib/excluded-units: 除外ユニットの単一情報源（Issue #473） -----------------

  const excl = require('./lib/excluded-units.js');
  const testTrace = require('./check-test-traceability.js');
  const coverageFloor = require('./check-coverage-floor.js');

  const sortedUnits = (s) => [...s].sort();

  ok('除外ユニットは .gitmodules の src/<unit> から導出される（planning は含まない）', () => {
    const gitmodules = fs.readFileSync(path.join(__dirname, '..', '.gitmodules'), 'utf8');
    const derived = sortedUnits(excl.excludedUnitsFromText(gitmodules));
    assert.deepStrictEqual(derived, ['ai-stock-trading'], `導出結果: ${JSON.stringify(derived)}`);
    // planning はリポジトリ直下の submodule でユニットではない（issue #473 の注意点）。
    assert.strictEqual(derived.includes('planning'), false);
    // 実リポジトリのルートから読んでも同じ結果になること。
    assert.deepStrictEqual(sortedUnits(excl.excludedUnits()), derived);
  });

  // 単一情報源であることの核: 3 検査器が同じ集合を持つ。ハードコード時代は 3 箇所を人手で
  // 揃える運用であり、submodule ユニットが増えると 3 箇所同時に狭すぎになった（#473）。
  ok('3 検査器の EXCLUDED_UNITS が単一情報源から導出され一致する', () => {
    const derived = sortedUnits(excl.excludedUnits());
    for (const [name, mod] of [
      ['check-backend-libraries', backendLibs],
      ['check-test-traceability', testTrace],
      ['check-coverage-floor', coverageFloor],
    ]) {
      assert.deepStrictEqual(sortedUnits(mod.EXCLUDED_UNITS), derived, `${name} の除外集合が導出値と異なる`);
      assert.strictEqual(mod.isExcludedPath('src/ai-stock-trading/backend/x/XTests.cs'), true, `${name}: AST が対象内`);
      assert.strictEqual(mod.isExcludedPath('src/platform/backend/x/XTests.cs'), false, `${name}: platform が対象外`);
      assert.strictEqual(mod.isExcludedPath('src/Directory.Packages.props'), false, `${name}: src 直下を除外している`);
    }
  });

  // 逆戻り防止: ハードコードへ戻すと .gitmodules への自動追随が黙って失われる
  // （check-doc-links の `planning/` 固定判定を .gitmodules 由来へ一般化した #139 と同じ作法）。
  ok('3 検査器に除外ユニットのハードコードが残っていない', () => {
    for (const f of ['check-backend-libraries.js', 'check-test-traceability.js', 'check-coverage-floor.js']) {
      const src = fs.readFileSync(path.join(__dirname, f), 'utf8');
      // クォート形は両対応にする。片方だけだと `new Set(["ai-stock-trading"])` が素通りし、
      // 「逆戻りを検出するテスト」自体が逆戻りを見逃す（監査指摘）。
      assert.doesNotMatch(src, /new Set\(\[\s*["']ai-stock-trading["']/, `${f} にハードコードが残っている`);
      assert.match(src, /require\('\.\/lib\/excluded-units\.js'\)/, `${f} がヘルパを参照していない`);
    }
  });

  ok('仮の submodule を .gitmodules に足すと除外が自動追随する（フィクスチャ）', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'excluded-units-repo-'));
    fs.writeFileSync(path.join(base, '.gitmodules'),
      '[submodule "planning"]\n\tpath = planning\n\turl = x\n'
      + '[submodule "src/ai-stock-trading"]\n\tpath = src/ai-stock-trading\n\turl = x\n'
      + '[submodule "src/next-unit"]\n\tpath = src/next-unit\n\turl = x\n');
    assert.deepStrictEqual(sortedUnits(excl.excludedUnits({ root: base })), ['ai-stock-trading', 'next-unit']);
    const isExcluded = excl.makeIsExcludedPath(excl.excludedUnits({ root: base }));
    assert.strictEqual(isExcluded('src/next-unit/backend/x/X.csproj'), true);
    assert.strictEqual(isExcluded('src/knowledge/backend/x/X.csproj'), false);
    fs.rmSync(path.join(base, '.gitmodules'));
    fs.rmdirSync(base);
  });

  // fail-closed: 読めないときに空集合を返すと、別プロジェクトを自リポジトリの規約で検査してしまう。
  ok('.gitmodules が読めなければ例外（空集合＝fail-open にしない）', () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), 'excluded-units-missing-'));
    assert.throws(() => excl.excludedUnits({ root: base }), /\.gitmodules を読めませんでした/);
    fs.rmdirSync(base);
  });

  ok('lib/excluded-units.js --self-test は exit 0', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'lib', 'excluded-units.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // --- check-cpm-versions: CPM のバージョン直書き禁止（Issue #467） ---------------

  const cpm = require('./check-cpm-versions.js');
  const cpmViolations = (xml) => cpm.inlineVersionFindings('src/x/X.csproj', xml).violations;
  const cpmOverrides = (xml) => cpm.inlineVersionFindings('src/x/X.csproj', xml).overrides;

  ok('CPM: Version 属性の直書きを違反として検出する', () => {
    assert.deepStrictEqual(cpmViolations('<PackageReference Include="X" Version="1.0.0" />'),
      [{ project: 'src/x/X.csproj', package: 'X', source: 'attribute', value: '1.0.0' }]);
    // 属性の順序に依存しない。
    assert.strictEqual(cpmViolations('<PackageReference Version="1.0.0" Include="X" />').length, 1);
  });

  ok('CPM: 子要素形（MSBuild メタデータ記法）の直書きも違反', () => {
    // 属性だけを見る実装だと素通りする経路。MSBuild では属性形と等価である。
    const found = cpmViolations('<PackageReference Include="X"><Version>2.0.0</Version></PackageReference>');
    assert.strictEqual(found.length, 1);
    assert.strictEqual(found[0].source, 'element');
    assert.strictEqual(found[0].value, '2.0.0');
  });

  ok('CPM: Update 形・プロパティ参照・空文字・単一引用符・条件付き ItemGroup も違反', () => {
    assert.strictEqual(cpmViolations('<PackageReference Update="X" Version="1.0.0" />')[0].package, 'X');
    assert.strictEqual(cpmViolations('<PackageReference Include="X" Version="$(XVersion)" />').length, 1);
    assert.strictEqual(cpmViolations('<PackageReference Include="X" Version="" />').length, 1);
    assert.strictEqual(cpmViolations("<PackageReference Include='X' Version='1.0.0' />").length, 1);
    assert.strictEqual(cpmViolations(
      '<ItemGroup Condition="\'$(TargetFramework)\'==\'net10.0\'">'
      + '<PackageReference Include="X" Version="1.0.0" /></ItemGroup>').length, 1);
  });

  ok('CPM: PackageVersion / GlobalPackageReference（中央定義）は違反にしない', () => {
    // 走査対象は .csproj のみだが、要素名の見分け自体を境界として固定しておく。
    assert.deepStrictEqual(cpmViolations('<PackageVersion Include="X" Version="1.0.0" />'), []);
    assert.deepStrictEqual(cpmViolations('<GlobalPackageReference Include="X" Version="1.0.0" />'), []);
    assert.deepStrictEqual(cpmViolations('<PackageReferenceFoo Include="X" Version="1.0.0" />'), []);
  });

  ok('CPM: コメントアウトされた例示と属性値の中の Version= は違反にしない', () => {
    assert.deepStrictEqual(cpmViolations('<!-- <PackageReference Include="X" Version="1.0.0" /> -->'), []);
    assert.deepStrictEqual(cpmViolations('<PackageReference Include="X" Condition="\'$(C)\'==\'Version=1\'" />'), []);
  });

  ok('CPM: VersionOverride は許可（違反 0）しつつ使用箇所を警告として拾う', () => {
    assert.deepStrictEqual(cpmViolations('<PackageReference Include="X" VersionOverride="1.0.0" />'), []);
    assert.strictEqual(cpmOverrides('<PackageReference Include="X" VersionOverride="1.0.0" />').length, 1);
    assert.strictEqual(
      cpmOverrides('<PackageReference Include="X"><VersionOverride>1.0.0</VersionOverride></PackageReference>').length, 1);
  });

  ok('CPM: 走査対象は .csproj（雛形の .sample 含む）のみ', () => {
    for (const p of ['src/x/X.csproj', 'templates/unit-template/backend/x/X.csproj.sample']) {
      assert.strictEqual(cpm.isScannedProjectFile(p), true, `${p} が対象外`);
    }
    // props / targets には正当な版記述（PackageVersion / GlobalPackageReference）があるため対象外。
    for (const p of ['src/Directory.Packages.props', 'src/Directory.Build.props',
      'src/Directory.Build.targets', 'src/x/X.cs', 'src/x/backend.slnx']) {
      assert.strictEqual(cpm.isScannedProjectFile(p), false, `${p} が対象になっている`);
    }
  });

  ok('CPM: 除外ユニットはハードコードせず lib/excluded-units.js から導出する', () => {
    const src = fs.readFileSync(path.join(__dirname, 'check-cpm-versions.js'), 'utf8');
    assert.doesNotMatch(src, /new Set\(\[\s*["']ai-stock-trading["']/);
    assert.match(src, /require\('\.\/lib\/excluded-units\.js'\)/);
    assert.strictEqual(cpm.isExcludedPath('src/ai-stock-trading/backend/x/X.csproj'), true);
    assert.strictEqual(cpm.isExcludedPath('src/platform/backend/x/X.csproj'), false);
  });

  ok('CPM: 実リポジトリは違反 0 件で templates/ も走査対象に入っている', () => {
    const r = cpm.scanTree();
    assert.deepStrictEqual(r.violations, [], `バージョン直書き: ${JSON.stringify(r.violations)}`);
    assert.ok(r.projects.length > 0, '走査対象が 0 件（0 件検査への退行）');
    assert.ok(r.projects.some((p) => p.startsWith('templates/')), 'templates/ が走査対象に入っていない');
  });

  ok('CPM: --self-test は exit 0（負例の実地走査を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-cpm-versions.js'), '--self-test'], { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // 受け入れ基準（#467）「.csproj にバージョン直書きが入った PR が CI で止まる」の実測。
  // 純関数の試験だけでは「走査経路に乗っているか」「素実行が exit 1 になるか」を確かめられないため、
  // 直書きを持つ .csproj を実ツリーへ一時的に置き、子プロセスの終了コードを見る。
  // 追跡ファイルは書き換えない（テストが異常終了しても既存ファイルを壊さないよう、新規ファイルを
  // 置いて finally で消す方式にする）。
  ok('CPM: 直書きのある .csproj を置くと素実行が exit 1（負例の実効性）', () => {
    const { spawnSync } = require('child_process');
    const probe = path.join(__dirname, '..', 'src', 'platform', 'backend', 'cpm-check-probe.csproj');
    const run = () => spawnSync(process.execPath, [path.join(__dirname, 'check-cpm-versions.js')], { encoding: 'utf8' });
    assert.strictEqual(run().status, 0, '設置前は exit 0 のはず');
    fs.writeFileSync(probe,
      '<Project><ItemGroup><PackageReference Include="Probe" Version="9.9.9" /></ItemGroup></Project>\n');
    try {
      const r = run();
      assert.strictEqual(r.status, 1, `直書きを置いたのに exit ${r.status}`);
      assert.match(String(r.stderr), /バージョン直書き 1 件/);
    } finally {
      fs.rmSync(probe, { force: true });
    }
    assert.strictEqual(run().status, 0, '撤去後に exit 0 へ戻らない');
  });

  // 受け入れ基準（#467）「VersionOverride の使用箇所が実行サマリに出る」の実測。
  // inlineVersionFindings() の検出だけを試験すると、出力側 reportOverrides() が壊れても緑のままになる
  // （警告が出ないことは終了コードに現れないため、CI も緑で通る）。よって出力経路ごと子プロセスで見る。
  ok('CPM: VersionOverride は exit 0 のまま実行サマリとアノテーションへ出る（出力経路）', () => {
    const { spawnSync } = require('child_process');
    const probe = path.join(__dirname, '..', 'src', 'platform', 'backend', 'cpm-override-probe.csproj');
    const summary = path.join(os.tmpdir(), `cpm-summary-${process.pid}-${Date.now()}.md`);
    fs.writeFileSync(probe,
      '<Project><ItemGroup><PackageReference Include="OverrideProbe" VersionOverride="9.9.9" />'
      + '</ItemGroup></Project>\n');
    try {
      const r = spawnSync(process.execPath, [path.join(__dirname, 'check-cpm-versions.js')], {
        encoding: 'utf8',
        // GITHUB_ACTIONS=true で ci-annotate が workflow コマンド（::warning::）を stdout へ出す。
        env: { ...process.env, GITHUB_ACTIONS: 'true', GITHUB_STEP_SUMMARY: summary },
      });
      // 許可であって違反ではない: 終了コードは 0 のまま。
      assert.strictEqual(r.status, 0, `VersionOverride で exit ${r.status}（許可のはず）`);
      assert.match(String(r.stdout), /::warning::CPM の VersionOverride を 1 件使用しています/);
      assert.match(String(r.stdout), /OverrideProbe=9\.9\.9/);
      const written = fs.readFileSync(summary, 'utf8');
      assert.match(written, /### CPM: VersionOverride の使用箇所/);
      assert.match(written, /OverrideProbe/);
      assert.match(written, /9\.9\.9/);
    } finally {
      fs.rmSync(probe, { force: true });
      fs.rmSync(summary, { force: true });
    }
    // 撤去後はサマリへも警告へも出ない（プローブが残って恒常的に警告が出る状態にしない）。
    const after = spawnSync(process.execPath, [path.join(__dirname, 'check-cpm-versions.js')], {
      encoding: 'utf8', env: { ...process.env, GITHUB_ACTIONS: 'true' },
    });
    assert.strictEqual(after.status, 0);
    assert.doesNotMatch(String(after.stdout), /VersionOverride を [1-9]/);
  });

  // --- check-contract-schema: Shared.Contracts の後方互換（Issue #465 / IADR-0122） -----

  const contracts = require('./check-contract-schema.js');
  const cTypes = (src) => contracts.extractTypes(src);
  const cOne = (src) => { const r = cTypes(src); return r[Object.keys(r)[0]]; };
  const cSnap = (types) => ({ 'src/x/backend/Shared/X.Contracts': types });
  const cDiff = (a, b) => contracts.diffSnapshots(cSnap(a), cSnap(b));
  const cRec = (members) => ({ kind: 'record', members });
  const cReq = (type, position) => ({ source: 'positional', type, required: true, position });
  const cOpt = (type, position) => ({ source: 'positional', type, required: false, position });

  ok('契約: 位置引数 record の型・必須・位置を抽出する', () => {
    const e = cTypes('namespace N;\npublic record E(Guid Id, string? Note = null);')['N.E'];
    assert.strictEqual(e.kind, 'record');
    assert.strictEqual(e.members.Id.required, true);
    assert.strictEqual(e.members.Id.position, 0);
    assert.strictEqual(e.members.Note.required, false);
    assert.strictEqual(e.members.Note.type, 'string?');
  });

  ok('契約: ジェネリクス内のカンマで位置引数を割らない（配列型も読む）', () => {
    // 素朴な split(',') だと Dictionary<string, List<string>> が 2 引数に割れて契約が壊れて記録される。
    assert.strictEqual(
      cOne('namespace N;\npublic record E(Dictionary<string, List<string>>? A = null);').members.A.type,
      'Dictionary<string,List<string>>?');
    assert.strictEqual(cOne('namespace N;\npublic record E(float[] V);').members.V.type, 'float[]');
  });

  ok('契約: enum の暗黙序数を計算する（並べ替えを値の変更として捕まえるため）', () => {
    const e = cOne('namespace N;\npublic enum E { A, B, C }');
    assert.deepStrictEqual([e.members.A.value, e.members.B.value, e.members.C.value], [0, 1, 2]);
    const f = cOne('namespace N;\npublic enum F { A = 5, B }');
    assert.deepStrictEqual([f.members.A.value, f.members.B.value], [5, 6]);
  });

  ok('契約: const の値と型・メンバーの属性まで固定する', () => {
    // "queued" 等のリテラルは配線そのもの。属性はシリアライズ表現（enum の文字列化・JSON 名）を変える。
    assert.strictEqual(
      cOne('namespace N;\npublic static class C { public const string A = "queued"; }').members.A.value,
      '"queued"');
    assert.deepStrictEqual(
      cOne('namespace N;\n[JsonConverter(typeof(JsonStringEnumConverter<E>))]\npublic enum E { A }').attributes,
      ['JsonConverter(typeof(JsonStringEnumConverter<E>))']);
    assert.deepStrictEqual(
      cOne('namespace N;\npublic class D { [JsonPropertyName("t")]\n public string T { get; init; } = ""; }')
        .members.T.attributes, ['JsonPropertyName("t")']);
  });

  ok('契約: internal 型・public メソッド・コメント中の宣言はスキーマに含めない', () => {
    assert.strictEqual(Object.keys(cTypes('namespace N;\ninternal record E(string A);')).length, 0);
    assert.strictEqual(
      cOne('namespace N;\npublic static class C { public const string A = "a";\n'
        + ' public static bool IsValid(string? x) => true; }').members.IsValid, undefined);
    assert.strictEqual(
      Object.keys(cTypes('namespace N;\n// public record Ghost(string A);\npublic record E(string A);')).length, 1);
  });

  ok('契約: 文字列リテラル中の // をコメントと誤認しない', () => {
    // storage:// のようなリテラルを持つ const を壊さないための境界。
    assert.match(contracts.stripComments('var x = "storage://a"; // c'), /storage:\/\/a/);
  });

  ok('契約: 削除・型変更・必須化・並べ替えは破壊的', () => {
    assert.strictEqual(cDiff({ 'N.A': cRec({ X: cReq('int', 0) }) }, { 'N.A': cRec({}) })[0].severity, 'breaking');
    assert.strictEqual(
      cDiff({ 'N.A': cRec({ X: cReq('int', 0) }) }, { 'N.A': cRec({ X: cReq('long', 0) }) })[0].kind,
      'memberTypeChanged');
    assert.strictEqual(
      cDiff({ 'N.A': cRec({ X: cOpt('int', 0) }) }, { 'N.A': cRec({ X: cReq('int', 0) }) })[0].kind,
      'memberRequired');
    assert.strictEqual(
      cDiff({ 'N.A': cRec({ X: cReq('int', 0), Y: cReq('int', 1) }) },
        { 'N.A': cRec({ X: cReq('int', 1), Y: cReq('int', 0) }) })
        .filter((c) => c.kind === 'memberReordered').length, 2);
  });

  ok('契約: 既定値付きの追加は非破壊・既定値の無い追加は破壊的', () => {
    // 既定値が無ければ旧発行者のメッセージが必須項目を欠く（＝後方互換ではない）。逃げ道は既定値を付けること。
    assert.strictEqual(cDiff({ 'N.A': cRec({}) }, { 'N.A': cRec({ X: cOpt('int', 0) }) })[0].severity, 'additive');
    assert.strictEqual(cDiff({ 'N.A': cRec({}) }, { 'N.A': cRec({ X: cReq('int', 0) }) })[0].kind,
      'memberAddedRequired');
  });

  ok('契約: 承認エントリは 5 項目すべてを必須にする', () => {
    const good = { key: 'typeRemoved:N.A', reason: 'r', approvedBy: 'a', issue: '#1', date: '2026-08-04' };
    assert.deepStrictEqual(contracts.validateApprovals([good]), []);
    for (const f of ['key', 'reason', 'approvedBy', 'issue', 'date']) {
      assert.ok(contracts.validateApprovals([{ ...good, [f]: '' }]).some((e) => e.includes(f)),
        `${f} が空でも通ってしまう（理由・承認者の残らない承認は記録にならない）`);
    }
    assert.ok(contracts.validateApprovals([{ ...good, date: '2026/08/04' }]).length > 0);
    assert.ok(contracts.validateApprovals([{ ...good, issue: '123' }]).length > 0);
  });

  ok('契約: 承認は破壊的変更を通し、対応する変更が無ければ stale として検出する', () => {
    const baseline = { projects: cSnap({ 'N.A': cRec({ X: cReq('int', 0) }) }) };
    const removed = { projects: cSnap({ 'N.A': cRec({}) }) };
    const key = 'memberRemoved:N.A.X';
    const approval = { key, reason: 'r', approvedBy: 'a', issue: '#465', date: '2026-08-04' };
    const e0 = contracts.evaluate({ snapshot: removed, baseline, allowlist: { approvals: [] } });
    assert.strictEqual(e0.unapproved.length, 1);
    assert.strictEqual(e0.unapproved[0].key, key);
    const e1 = contracts.evaluate({ snapshot: removed, baseline, allowlist: { approvals: [approval] } });
    assert.strictEqual(e1.unapproved.length, 0);
    assert.strictEqual(e1.approved.length, 1);
    const e2 = contracts.evaluate({
      snapshot: { projects: baseline.projects }, baseline, allowlist: { approvals: [approval] },
    });
    assert.strictEqual(e2.stale.length, 1, '承認だけが残ると次の破壊的変更を黙って通す');
  });

  ok('契約: 承認の記録は baseline の $acceptedBreakingChanges へ追記され消えない', () => {
    const nb = contracts.nextBaseline({
      snapshot: { projects: { p: {} } },
      baseline: { $acceptedBreakingChanges: [{ key: 'old' }], projects: {} },
      approved: [{ key: 'typeRemoved:N.A', reason: 'r', approvedBy: 'a', issue: '#1', date: '2026-08-04' }],
    });
    assert.strictEqual(nb.$acceptedBreakingChanges.length, 2);
    assert.strictEqual(contracts.emptyAllowlist().approvals.length, 0);
  });

  ok('契約: 除外ユニットはハードコードせず lib/excluded-units.js から導出する', () => {
    const src = fs.readFileSync(path.join(__dirname, 'check-contract-schema.js'), 'utf8');
    assert.doesNotMatch(src, /new Set\(\[\s*["']ai-stock-trading["']/);
    assert.match(src, /require\('\.\/lib\/excluded-units\.js'\)/);
    assert.strictEqual(contracts.isExcludedPath('src/ai-stock-trading/backend/Shared/X.Contracts'), true);
    assert.strictEqual(contracts.isExcludedPath('src/platform/backend/Shared/Platform.Shared.Contracts'), false);
  });

  ok('契約: 実リポジトリは baseline と一致し、契約プロジェクトを 2 件検出する', () => {
    const found = contracts.findContractProjects();
    assert.deepStrictEqual(found, [
      'src/knowledge/backend/Shared/Knowledge.Contracts',
      'src/platform/backend/Shared/Platform.Shared.Contracts',
    ], '走査経路の退行（0 件検査・対象取りこぼし）');
    const r = contracts.evaluate({
      snapshot: contracts.buildSnapshot(),
      baseline: JSON.parse(fs.readFileSync(contracts.BASELINE_FILE, 'utf8')),
      allowlist: JSON.parse(fs.readFileSync(contracts.ALLOWLIST_FILE, 'utf8')),
    });
    assert.deepStrictEqual(r.changes, [], `baseline との差分: ${JSON.stringify(r.changes)}`);
    assert.deepStrictEqual(r.stale, []);
    assert.deepStrictEqual(r.approvalErrors, []);
  });

  ok('契約: --self-test は exit 0（負例の実地走査を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-contract-schema.js'), '--self-test'],
      { encoding: 'utf8' });
    assert.strictEqual(r.status, 0, `self-test が失敗:\n${r.stdout}\n${r.stderr}`);
  });

  // 受け入れ基準（#465）「後方互換を壊す契約変更が CI で止まる」の実測。
  // 純関数の試験だけでは「走査経路に乗っているか」「素実行が exit 1 になるか」を確かめられないため、
  // 破壊的変更を持つ .cs を実ツリーへ一時的に置き、子プロセスの終了コードを見る。
  // 追跡ファイルは書き換えない（テストが異常終了しても既存の契約を壊さないよう、新規ファイルを
  // 置いて finally で消す方式にする）。
  ok('契約: 契約に型を足すと素実行が exit 1（走査経路が実ツリーに効いていること）', () => {
    const { spawnSync } = require('child_process');
    const probe = path.join(__dirname, '..', 'src', 'knowledge', 'backend', 'Shared',
      'Knowledge.Contracts', 'Events', 'ContractCheckProbe.cs');
    const run = () => spawnSync(process.execPath,
      [path.join(__dirname, 'check-contract-schema.js')], { encoding: 'utf8' });
    assert.strictEqual(run().status, 0, '設置前は exit 0 のはず');
    // 非破壊（型の追加）でも baseline と差分がある限り止める＝契約変更を必ず PR の diff に載せる設計。
    fs.writeFileSync(probe,
      'namespace Knowledge.Contracts.Events;\npublic record ContractCheckProbe(Guid Id);\n');
    try {
      const r = run();
      assert.strictEqual(r.status, 1, `型を足したのに exit ${r.status}`);
      assert.match(String(r.stderr), /typeAdded:Knowledge\.Contracts\.Events\.ContractCheckProbe/);
      assert.match(String(r.stderr), /--update/);
    } finally {
      fs.rmSync(probe, { force: true });
    }
    assert.strictEqual(run().status, 0, '撤去後に exit 0 へ戻らない');
  });

  // 受け入れ基準（#465）「破壊的変更が CI で止まり、承認で通る」の出力経路まで含めた実測。
  // 判定関数だけを試験すると、承認済みの可視化（reportApproved）が壊れても終了コードに現れない。
  ok('契約: 破壊的変更は素実行で exit 1・承認済みは実行サマリとアノテーションへ出る（出力経路）', () => {
    const { spawnSync } = require('child_process');
    const baselinePath = contracts.BASELINE_FILE;
    const allowPath = contracts.ALLOWLIST_FILE;
    const baselineOrig = fs.readFileSync(baselinePath, 'utf8');
    const allowOrig = fs.readFileSync(allowPath, 'utf8');
    const summary = path.join(os.tmpdir(), `contract-summary-${process.pid}-${Date.now()}.md`);
    const run = (env = {}) => spawnSync(process.execPath,
      [path.join(__dirname, 'check-contract-schema.js')],
      { encoding: 'utf8', env: { ...process.env, ...env } });
    // baseline 側へ「実在しないメンバー」を足すと、実ツリーとの差分は削除（＝破壊的）として現れる。
    // 実ファイル（契約そのもの）は一切書き換えない。
    const key = 'memberRemoved:Knowledge.Contracts.Events.IngestionCompleted.__Probe';
    try {
      const b = JSON.parse(baselineOrig);
      b.projects['src/knowledge/backend/Shared/Knowledge.Contracts']
        ['Knowledge.Contracts.Events.IngestionCompleted'].members.__Probe =
          { source: 'positional', type: 'int', required: true, position: 9 };
      fs.writeFileSync(baselinePath, JSON.stringify(b, null, 2) + '\n');

      const unapproved = run();
      assert.strictEqual(unapproved.status, 1, '未承認の破壊的変更で exit 1 にならない');
      assert.match(String(unapproved.stderr), /後方互換を壊す契約変更が 1 件あります/);
      assert.match(String(unapproved.stderr), new RegExp(key.replace(/\./g, '\\.')));
      assert.match(String(unapproved.stderr), /contract-breaking-allowlist\.json/);

      const a = JSON.parse(allowOrig);
      a.approvals = [{ key, reason: '受け入れ基準の実測', approvedBy: 'test', issue: '#465', date: '2026-08-04' }];
      fs.writeFileSync(allowPath, JSON.stringify(a, null, 2) + '\n');

      const approved = run({ GITHUB_ACTIONS: 'true', GITHUB_STEP_SUMMARY: summary });
      // 承認しても baseline が古いままなら止める（--update で差分を PR に載せさせるため）。
      assert.strictEqual(approved.status, 1, '承認済みでも baseline 未更新なら止めるはず');
      assert.match(String(approved.stdout), /::warning::承認済みの破壊的な契約変更が 1 件あります/);
      assert.match(String(approved.stderr), /--update/);
      const written = fs.readFileSync(summary, 'utf8');
      assert.match(written, /### 契約: 承認済みの破壊的変更/);
      assert.match(written, /受け入れ基準の実測/);

      // 対応する変更が無い承認（stale）は fail する。
      fs.writeFileSync(baselinePath, baselineOrig);
      const stale = run();
      assert.strictEqual(stale.status, 1, 'stale な承認で exit 1 にならない');
      assert.match(String(stale.stderr), /対応する変更が無い承認が 1 件残っています/);
    } finally {
      fs.writeFileSync(baselinePath, baselineOrig);
      fs.writeFileSync(allowPath, allowOrig);
      fs.rmSync(summary, { force: true });
    }
    assert.strictEqual(run().status, 0, '復元後に exit 0 へ戻らない');
  });
  // --- Issue #496 / ADR-0031 / IADR-0125: i18n カタログ検査と静的 egress 検査 -------

  // 各スクリプトの --self-test（正例・負例を含む）を子プロセスで走らせる。
  // 本体の純粋ロジックはそこで網羅しているので、ここでは「自己試験が通ること」と
  // 「本リポの実データに対して現に green であること」を固定する。
  for (const script of ['check-i18n-catalogs.js', 'check-static-egress.js']) {
    ok(`${script} --self-test が通る`, () => {
      const { spawnSync } = require('child_process');
      const r = spawnSync(process.execPath, [path.join(__dirname, script), '--self-test'], {
        encoding: 'utf8',
      });
      assert.strictEqual(r.status, 0, `${script} の自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
      assert.match(String(r.stdout), /all passed/);
    });
  }

  // --- Issue #556 / ADR-0031 / IADR-0134: manualChunks の規則構成の検査 ---------------
  //
  // **この経路（ci.yml の scripts-tests）には dist が無い。** 実成果物に対する検査は
  // frontend.yml の build-test（Build ステップの後）へ `--require` 付きで結線している。
  // ここで固定するのは自己試験——とくに「**baseline の requiredChunks が vite.config.ts の
  // manualChunks と一致すること**」で、これは dist 無しで判定でき、かつ
  // 「規則を足したのに床へ入れ忘れ、新しい規則だけ検査されない」状態を止める。
  ok('check-chunk-budget.js --self-test が通る（規則欠落の変異 2 件を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(
      process.execPath,
      [path.join(__dirname, 'check-chunk-budget.js'), '--self-test'],
      { encoding: 'utf8' },
    );
    assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
    assert.match(String(r.stdout), /件すべて通過/);
    // 実測に基づく 2 つの変異（ui / vendor-react の規則欠落）が実際に走っていること。
    // 件数だけを見ると、変異ケースを消しても「通過」しつづける。
    assert.match(String(r.stdout), /変異 M6/);
    assert.match(String(r.stdout), /変異 M7/);
  });

  // --- Issue #493 / ADR-0031 / IADR-0121 決定 1 / IADR-0211: 未使用コード・依存のラチェット ---
  //
  // **この経路（ci.yml の scripts-tests）には src/node_modules が無い。** 実データに対する
  // 走査は frontend.yml の build-test へ `--require` 付きで結線している（Knip 本体は
  // src/ の devDependency であり、pnpm install 済みのジョブでしか走らない）。
  // ここで固定するのは自己試験——とくに次の 2 つは node_modules 無しで判定でき、
  // かつ「外れても誰も気付かない」種類の不変条件である。
  //   (1) `.gitmodules` の `src/<unit>` submodule が src/knip.jsonc の ignoreWorkspaces で外れていること
  //       （別プロジェクトの未使用が本リポの床へ雪崩れ込むのを止める。IADR-0211 決定 2）
  //   (2) Knip の JSON 出力が読めない形になったとき **0 件で緑にせず throw する**こと（IADR-0183）
  ok('check-knip.js --self-test が通る（床の増減・新区分・fail-closed の変異を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(process.execPath, [path.join(__dirname, 'check-knip.js'), '--self-test'], {
      encoding: 'utf8',
    });
    assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
    assert.match(String(r.stdout), /件すべて通過/);
    // 件数だけを見ると、変異ケースを消しても「通過」しつづける。個々の変異が走っていることを見る。
    assert.match(String(r.stdout), /変異 M1 相当/);
    assert.match(String(r.stdout), /変異 M2 相当/);
    assert.match(String(r.stdout), /変異 M3 相当/);
    assert.match(String(r.stdout), /変異 M4 相当/);
    // 整形（prettier の末尾カンマ）で検査器だけが設定を読めなくなる退行を固定する。
    assert.match(String(r.stdout), /末尾カンマを許す/);
  });

  // 床の区分と src/knip.jsonc の存在は、検査器の自己試験とは別の面（設定の実在）である。
  ok('check-knip: 床と設定ファイルが実在し、床が 0 件ではない', () => {
    const knip = require('./check-knip.js');
    const baseline = JSON.parse(
      fs.readFileSync(path.join(__dirname, 'knip-baseline.json'), 'utf8'),
    );
    const configText = fs.readFileSync(path.join(__dirname, '..', 'src', 'knip.jsonc'), 'utf8');
    // prettier（trailingComma: "all"）が .jsonc へ末尾カンマを足すため、コメントだけでなく
    // 末尾カンマも落としてから読む（parseJsonc）。
    const config = knip.parseJsonc(configText);
    assert.ok(knip.total(baseline.counts) > 0, '床が 0 件だと fail-closed の門が効かない');
    assert.ok(
      Array.isArray(config.ignoreWorkspaces) && config.ignoreWorkspaces.length > 0,
      'knip.jsonc の ignoreWorkspaces が空（別プロジェクトの submodule を走査してしまう）',
    );
    // 床に書いてよいのは既知の区分だけ（typo は判定を黙って外す）。
    for (const name of Object.keys(baseline.counts)) {
      assert.ok(
        knip.KNOWN_ISSUE_TYPES.includes(name),
        `knip-baseline.json に未知の区分 "${name}" がある`,
      );
    }
  });

  ok('check-i18n-catalogs: 本リポのカタログに未翻訳が無い（実データ）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(
      process.execPath,
      [path.join(__dirname, 'check-i18n-catalogs.js')],
      { encoding: 'utf8' },
    );
    assert.strictEqual(r.status, 0, `未翻訳・fuzzy・obsolete が残っている:\n${r.stderr}`);
  });

  // 退行防止: Lingui のカタログ検査が見ているロケール集合が、i18n の実装が対応すると
  // 宣言しているロケール集合と一致していること。片方だけ増えると「宣言はしたが訳が無い」
  // あるいは「訳はあるが読み込まれない」状態が静かに生まれる。
  ok('lingui.config.ts の locales と foundation/i18n の SUPPORTED_LOCALES が一致する', () => {
    const { parseLinguiConfig } = require('./check-i18n-catalogs.js');
    const root = path.resolve(__dirname, '..');
    const cfg = parseLinguiConfig(fs.readFileSync(path.join(root, 'src/lingui.config.ts'), 'utf8'));
    const i18nSrc = fs.readFileSync(
      path.join(root, 'src/platform/frontend/src/foundation/i18n/index.ts'),
      'utf8',
    );
    const m = /SUPPORTED_LOCALES\s*=\s*\[([^\]]*)\]/.exec(i18nSrc);
    assert.ok(m, 'foundation/i18n から SUPPORTED_LOCALES を読み取れない');
    const supported = m[1]
      .split(',')
      .map((x) => x.trim().replace(/^['"]|['"]$/g, ''))
      .filter(Boolean);
    assert.deepStrictEqual(
      [...supported].sort(),
      [...cfg.locales].sort(),
      'lingui.config.ts の locales と SUPPORTED_LOCALES が食い違っている',
    );
  });

  // 退行防止: 08_data-egress-policy が禁じる代表的なホストが、検査器の禁止リストから
  // 抜け落ちていないこと（リストを空にしても自己試験の大半は通ってしまうため）。
  ok('check-static-egress: 計画が名指しする禁止先が検査対象に入っている', () => {
    const { FORBIDDEN_HOSTS, inspectFile } = require('./check-static-egress.js');
    // 08_data-egress-policy §SPA フロントエンド: 外部CDN・Webフォント（Google Fonts等）・
    // 解析（analytics）・エラー報告SaaS。
    for (const host of ['fonts.googleapis.com', 'www.google-analytics.com', 'cdn.jsdelivr.net']) {
      assert.ok(FORBIDDEN_HOSTS.includes(host), `${host} が禁止リストに無い`);
      assert.ok(
        inspectFile('bundle.js', `x="https://${host}/a"`).some((h) => h.kind === 'forbidden-host'),
        `${host} を検出できない`,
      );
    }
  });

  // --- Issue #510 / IADR-0130: 実在するテストがテスト仕様書に載っているかの検査 --------
  //
  // ここに置く理由: .github/workflows/ は GitHub App 権限では編集できないため、実装エージェントの
  // 手では専用ジョブを足せない。ci.yml の scripts-tests ジョブ（REQUIRE_REPO_TESTS=1）が本 companion
  // を実行するので、**自己試験と実データの本走をここへ置くことで CI ゲートになる**
  // （check-i18n-catalogs.js の実データ検査と同じ結線）。
  //
  // ［2026-08-05］専用ステップは ci.yml の test-traceability ジョブへ入った（a415e29。親がローカル
  // 権限で実施）。**本ブロックは残す**——二重に走るのは無駄ではなく、専用ステップは失敗をジョブ名で
  // 見せ、こちらはワークフローを編集できない環境でも検査が外れないことを担保する（IADR-0130 決定 6）。

  ok('check-test-spec-coverage --self-test が通る（ratchet 4 判定・fail-closed の負例を含む）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(
      process.execPath,
      [path.join(__dirname, 'check-test-spec-coverage.js'), '--self-test'],
      { encoding: 'utf8' },
    );
    assert.strictEqual(r.status, 0, `自己試験が失敗:\n${r.stdout}\n${r.stderr}`);
    assert.match(String(r.stdout), /自己試験 \d+ 件 OK/);
  });

  ok('check-test-spec-coverage: 本リポの実データが green（節の消失が無い）', () => {
    const { spawnSync } = require('child_process');
    const r = spawnSync(
      process.execPath,
      [path.join(__dirname, 'check-test-spec-coverage.js')],
      { encoding: 'utf8' },
    );
    assert.strictEqual(
      r.status,
      0,
      `docs/tests/ の記載が床を割っている（#510 の再発の可能性）:\n${r.stdout}\n${r.stderr}`,
    );
  });

  // 退行防止: #510 で復帰させた 4 クラスが床に残っていること。
  // 床は --update で誰でも下げられるため、「節を消して床も下げる」で黙らせる経路が残る。
  // 本 issue の核心にあたるクラスだけは、床とは別に固定して差分をレビューへ強制的に出す。
  ok('#510 で復帰させたバックエンドテストが床に残っている', () => {
    const { readBaseline, pairKey } = require('./check-test-spec-coverage.js');
    const documented = new Set(readBaseline());
    const SC05 = 'docs/tests/SC-05_document-management.md';
    const SC06 = 'docs/tests/SC-06_datasource-management.md';
    for (const [spec, name] of [
      [SC05, 'BffDocumentWriteEndpointTests'], // SC-05 §BFF（書き込み）
      [SC05, 'DocumentVersioningTests'], // SC-05 §状態遷移ガード（ドメイン）
      [SC05, 'DocumentEndpointVersioningTests'], // SC-05 §状態遷移ガード（API）
      [SC06, 'BffDataSourceEndpointTests'], // SC-06 §BFF
    ]) {
      assert.ok(
        documented.has(pairKey(spec, name)),
        `${spec} の ${name} が scripts/test-spec-coverage-baseline.json から消えている` +
          '（#510 が復帰させた記載を再び落としていないか当該のテスト仕様書を確認すること）',
      );
    }
  });
  // --- measure-abac-combinations: ABAC 属性組み合わせ数の実測（FR-17 / FR-18・Issue #456） ---

  const abac = require('./measure-abac-combinations.js');

  // 文書行の固定入力。実機と同じ形（属性・タグでまとめた重みつきの行）にする。
  const ABAC_ROWS = [
    { attributes: { confidentiality: 'internal', kind: 'Quote', publishedAt: '2026-07-31' }, tags: ['Quote'], count: 900 },
    { attributes: { confidentiality: 'internal', kind: 'Quote', publishedAt: '2026-07-30' }, tags: ['Quote'], count: 500 },
    { attributes: { confidentiality: 'restricted', department: 'hr' }, tags: [], count: 3 },
    { attributes: { kind: 'Daily' }, tags: [], count: 1 },
  ];
  const ABAC_USERS = [
    { username: 'admin', attributes: { clearance: ['restricted'], department: ['engineering'] }, realmRoles: ['platform-admin', 'platform-operator'] },
    { username: 'developer', attributes: { clearance: ['restricted'], department: ['engineering'] }, realmRoles: ['platform-operator', 'platform-admin'] },
    { username: 'poc-user', attributes: { clearance: ['internal'], department: ['engineering'] }, realmRoles: [] },
  ];

  // #516 / IADR-0199: システム投入経路の予約値の件数（環流債務の測定値）。
  // 計画が「両方とも件数を観測し、環流債務の測定値として読む」と明示している。
  ok('countUnresolvedReservedValues は予約値・解決済み・欠落を区別して数える', () => {
    const rows = [
      // 予約値へ倒れた分（重み付き）
      { attributes: { owner: 'system', department: 'unassigned' }, count: 900 },
      // 解決できた分
      { attributes: { owner: 'alice', department: 'hr' }, count: 100 },
      // 属性そのものが無い分（旧データ）。**予約値とは別に数える**
      { attributes: { confidentiality: 'internal' }, count: 5 },
      // 空白のみは「無い」と同じ扱い
      { attributes: { owner: '   ', department: '' }, count: 2 },
    ];
    const r = abac.countUnresolvedReservedValues(rows);
    const owner = r.find((e) => e.key === 'owner');
    const dept = r.find((e) => e.key === 'department');

    assert.strictEqual(owner.reserved, 900);
    assert.strictEqual(owner.resolved, 100);
    assert.strictEqual(owner.absent, 7, '空白のみと欠落を合算して absent に数えていない');
    assert.strictEqual(owner.reservedValue, 'system');
    // 割合は予約値 ÷（予約値＋解決済み）。欠落は分母に入れない
    assert.ok(Math.abs(owner.reservedRatio - 0.9) < 1e-9, `予約値の割合が 0.9 でない: ${owner.reservedRatio}`);

    assert.strictEqual(dept.reservedValue, 'unassigned');
    assert.strictEqual(dept.reserved, 900);
    assert.strictEqual(dept.resolved, 100);
  });

  // 0 除算を「0%」と偽らない —— 分母が 0 のときは null にする。
  // **「予約値 0%」と「そもそも計測対象が無い」を読み分けられないと、債務が無いように見える。**
  ok('countUnresolvedReservedValues は分母 0 のとき割合を null にする', () => {
    const r = abac.countUnresolvedReservedValues([{ attributes: { confidentiality: 'internal' }, count: 3 }]);
    for (const e of r) {
      assert.strictEqual(e.reservedRatio, null, `${e.key} の割合が null でない`);
      assert.strictEqual(e.reserved, 0);
      assert.strictEqual(e.absent, 3);
    }
  });

  // 空入力でも落ちない（rows が null / [] の経路）
  ok('countUnresolvedReservedValues は空入力でも全キーを返す', () => {
    for (const input of [null, []]) {
      const r = abac.countUnresolvedReservedValues(input);
      assert.strictEqual(r.length, abac.UNRESOLVED_RESERVED_VALUES.length);
      assert.deepStrictEqual(
        r.map((e) => e.key),
        abac.UNRESOLVED_RESERVED_VALUES.map((e) => e.key)
      );
    }
  });

  // 予約値の語彙が実装（DataSource.cs）とずれていないこと。
  // **ずれると「予約値 0 件」と報告され、債務が見えなくなる。**
  ok('予約値の語彙が DataSource.cs の定数と一致する', () => {
    const src = fs.readFileSync(
      path.join(
        __dirname,
        '..',
        'src/knowledge/backend/Services/DataSourceService/src/DataSourceService.Api/Foundation/Domain/DataSource.cs'
      ),
      'utf8'
    );
    for (const { key, value } of abac.UNRESOLVED_RESERVED_VALUES) {
      const constName = key === 'owner' ? 'UnresolvedOwner' : 'UnresolvedDepartment';
      const m = new RegExp(`public const string ${constName} = "([^"]+)";`).exec(src);
      assert.ok(m, `${constName} が DataSource.cs に無い`);
      assert.strictEqual(m[1], value, `${key} の予約値が実装（${m[1]}）と測定（${value}）でずれている`);
    }
  });

  ok('resolveDocumentAbacKeys は属性辞書を計画既定より優先する', () => {
    const observed = ['confidentiality', 'kind', 'publishedAt'];
    const withDict = abac.resolveDocumentAbacKeys(
      [
        { key: 'confidentiality', scope: 'document' },
        { key: 'kind', scope: 'document' },
        { key: 'clearance', scope: 'user' }, // scope=user は文書側の軸に混ぜない
      ],
      observed
    );
    assert.strictEqual(withDict.source, 'dictionary');
    assert.deepStrictEqual(withDict.abacKeys, ['confidentiality', 'kind']);
    assert.deepStrictEqual(withDict.outOfScopeKeys, ['publishedAt']);
  });

  ok('resolveDocumentAbacKeys は辞書が空なら計画の文書基本属性へ縮退する', () => {
    const r = abac.resolveDocumentAbacKeys([], ['confidentiality', 'kind', 'publishedAt', 'symbol']);
    assert.strictEqual(r.source, 'plan');
    // 高基数のメタデータ（publishedAt 等）を軸にすると「実在する組み合わせ数」が
    // タイムスタンプの異なり数になってしまう。対象外として分離されること。
    assert.deepStrictEqual(r.abacKeys, ['confidentiality']);
    assert.deepStrictEqual(r.outOfScopeKeys, ['kind', 'publishedAt', 'symbol']);
    assert.ok(r.unusedCandidateKeys.includes('lifecycle'), '計画の未使用候補キーが挙がらない');
  });

  ok('countCombinations は count で重みづけし、件数降順で並べる', () => {
    const r = abac.countCombinations(ABAC_ROWS, ['confidentiality']);
    assert.strictEqual(r.total, 1404);
    assert.strictEqual(r.distinct, 3); // internal / restricted / 属性なし
    assert.strictEqual(r.entries[0].label, 'confidentiality=internal');
    assert.strictEqual(r.entries[0].count, 1400);
    // 属性が無い文書は「値なし」を 1 つの組み合わせとして数える（黙って落とさない）。
    assert.ok(r.entries.some((e) => e.label === `confidentiality=${abac.ABSENT}` && e.count === 1));
  });

  ok('countConfidentiality は設計上の 4 値のうち実在しない値を挙げる', () => {
    const r = abac.countConfidentiality(ABAC_ROWS);
    assert.deepStrictEqual(r.observedValues.sort(), ['internal', 'restricted']);
    assert.deepStrictEqual(r.unusedPlannedValues, ['public', 'confidential']);
  });

  ok('countRoleSets はロール保有集合を順序非依存で数える', () => {
    const r = abac.countRoleSets(ABAC_USERS);
    // admin と developer は順序違いの同一集合＝1 組にまとまる。ロール無しは別の 1 組。
    assert.strictEqual(r.distinct, 2);
    assert.strictEqual(r.entries[0].label, 'platform-admin + platform-operator');
    assert.strictEqual(r.entries[0].count, 2);
  });

  ok('countUserAttributeCombinations は多値属性の先頭を取って数える', () => {
    const r = abac.countUserAttributeCombinations(ABAC_USERS);
    assert.strictEqual(r.distinct, 2);
    assert.strictEqual(r.entries[0].label, 'clearance=restricted / department=engineering');
  });

  ok('missingRequiredDocumentAttributes は計画の必須属性の欠落を挙げる', () => {
    assert.deepStrictEqual(abac.missingRequiredDocumentAttributes(['confidentiality', 'kind']), [
      'department',
      'owner',
      'lifecycle',
    ]);
    assert.deepStrictEqual(
      abac.missingRequiredDocumentAttributes(['confidentiality', 'department', 'owner', 'lifecycle']),
      []
    );
  });

  ok('必須・値集合の判定は属性辞書があればそちらを正とする', () => {
    const defs = [
      { key: 'confidentiality', scope: 'document', required: true, allowedValues: ['internal', 'secret'] },
      { key: 'project', scope: 'document', required: true, allowedValues: [] },
      { key: 'kind', scope: 'document', required: false, allowedValues: [] },
      { key: 'clearance', scope: 'user', required: true, allowedValues: [] }, // 文書側の必須に混ぜない
    ];
    // 辞書が必須と宣言したキー（confidentiality / project）のうち、実データに無いのは project。
    assert.deepStrictEqual(abac.missingRequiredDocumentAttributes(['confidentiality', 'kind'], defs), ['project']);
    // 値集合も辞書の AllowedValues が正になる（計画の 4 値ではなく 2 値で判定する）。
    const conf = abac.countConfidentiality(ABAC_ROWS, defs);
    assert.strictEqual(conf.plannedValuesSource, 'dictionary');
    assert.deepStrictEqual(conf.plannedValues, ['internal', 'secret']);
    assert.deepStrictEqual(conf.unusedPlannedValues, ['secret']);
    // 辞書が無ければ計画の値集合へ縮退する。
    assert.strictEqual(abac.countConfidentiality(ABAC_ROWS, []).plannedValuesSource, 'plan');
  });

  ok('countReachablePairs はポリシー 0 件なら deny-by-default で 0 を返す', () => {
    const r = abac.countReachablePairs(ABAC_USERS, ABAC_ROWS, []);
    assert.deepStrictEqual(r.grantedUsers, []);
    assert.strictEqual(r.reachablePairs, 0);
    assert.strictEqual(r.reachableDocuments, 0);
  });

  ok('countReachablePairs は AbacEvaluator と同じ意味論で到達数を数える', () => {
    const policies = [
      {
        name: 'internal-read',
        action: 'read',
        userConditions: { clearance: ['internal', 'restricted'] },
        documentConditions: { confidentiality: ['internal'] },
        isActive: true,
      },
      // 無効なポリシーは評価しない。
      { name: 'off', action: 'read', userConditions: {}, documentConditions: {}, isActive: false },
    ];
    const r = abac.countReachablePairs(ABAC_USERS, ABAC_ROWS, policies);
    assert.strictEqual(r.activePolicyCount, 1);
    assert.strictEqual(r.grantedUsers.length, 3);
    // internal の 2 行 × 3 人。restricted 行と属性なし行は条件不一致（キー欠落は不一致）。
    assert.strictEqual(r.reachablePairs, 6);
    assert.strictEqual(r.reachableDocuments, 4200);
  });

  ok('summarize は 3 粒度と乖離をまとめて返す（同一入力で同一出力）', () => {
    const data = { realm: 'test', documents: ABAC_ROWS, users: ABAC_USERS, definitions: [], policies: [] };
    const r = abac.summarize(data);
    assert.strictEqual(r.scope.documentCount, 1404);
    assert.strictEqual(r.scope.userCount, 3);
    assert.strictEqual(r.byAttributeCombination.distinct, 3); // 粒度 1
    assert.strictEqual(r.byRole.distinct, 2); // 粒度 2
    assert.strictEqual(r.byConfidentiality.observedValues.length, 2); // 粒度 3
    // department は固定入力の 1 行に付いているため欠落に挙がらない（owner / lifecycle は欠落）。
    assert.deepStrictEqual(r.missingRequiredDocumentAttributes, ['owner', 'lifecycle']);
    // 再現性: 同一入力なら同一出力（乱数・現在時刻に依存しない）。
    assert.deepStrictEqual(abac.summarize(data), r);
    assert.match(abac.renderText(r), /粒度 3: 機密区分単位/);
  });

  // --- NFR / #581 / IADR-0144: IADR 採番の機械検査 ---------------------------------
  // 採番の一意性・連続性（`0000` 起点）・索引との双方向一致・索引行の「形」（#580 から統合）。
  // **実データは全判定 clean なので、検出力は変異でしか示せない** —— 一時ツリーで当てる。
  {
    const { spawnSync: spawnAdrNum } = require('child_process');
    const pathAdrNum = require('path');
    const adrNumScript = pathAdrNum.join(__dirname, 'check-adr-numbering.js');
    const runAdrNum = (args) =>
      spawnAdrNum(process.execPath, [adrNumScript, ...args], { encoding: 'utf8' });

    ok('check-adr-numbering --self-test が通る（M1〜M6 を対で固定）', () => {
      const r = runAdrNum(['--self-test']);
      assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
    });

    ok('check-adr-numbering が実データ（.ai-context/adr）で違反 0 件', () => {
      const r = runAdrNum([]);
      assert.strictEqual(r.status, 0, `IADR 採番に違反がある:\n${r.stdout}\n${r.stderr}`);
    });

    // **実バイナリ経路での検出力**。自己試験は関数を直接叩くので、CLI が exit 1 を返すかは別に見る。
    ok('check-adr-numbering: 欠番のあるツリーで exit 1', () => {
      const fsAdrNum = require('fs');
      const osAdrNum = require('os');
      const dir = fsAdrNum.mkdtempSync(pathAdrNum.join(osAdrNum.tmpdir(), 'adrnum-repo-'));
      try {
        fsAdrNum.writeFileSync(pathAdrNum.join(dir, 'IADR-0000_a.md'), '# IADR-0000: a\n');
        fsAdrNum.writeFileSync(pathAdrNum.join(dir, 'IADR-0002_c.md'), '# IADR-0002: c\n');
        fsAdrNum.writeFileSync(
          pathAdrNum.join(dir, 'README.md'),
          '| [IADR-0000](./IADR-0000_a.md) | a | Accepted |\n' +
            '| [IADR-0002](./IADR-0002_c.md) | c | Accepted |\n'
        );
        const r = runAdrNum(['--dir', dir]);
        assert.strictEqual(r.status, 1, `欠番で exit 1 にならない:\n${r.stdout}\n${r.stderr}`);
        assert.match(String(r.stderr), /missing-number/);
      } finally {
        fsAdrNum.rmSync(dir, { recursive: true, force: true });
      }
    });
  }

  //
  // NFR / #649: 本文を変えたのに frontmatter の `updated:` が古いままの文書を止める検査器。
  // **同型の事故が 2 回起きたので入れた**（PR #648 のレビュー 2 巡目で 9 件・4 巡目で 1 件）。
  //
  // **ここが check-doc-updated.js の CI 呼び出し口である。** `.github/workflows/` は GitHub App
  // 権限で編集できないため、新しい検査器を足しても新ジョブからは呼べない。ci.yml の scripts-tests
  // ジョブ（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）が本 companion を読み込むので、
  // ここから呼ぶ（check-cross-repo-refs / check-plan-id-qualification と同じ相乗り。IADR-0140 決定 2）。
  {
    const { findViolations: findUpdatedViolations, stripFrontmatter, readUpdated } =
      require('./check-doc-updated.js');

    const doc = (updated, body) => `---\ntitle: t\nupdated: ${updated}\n---\n\n${body}\n`;
    const run = (files, first) => findUpdatedViolations(files, first).violations;

    ok('check-doc-updated: 本文を変えて updated: 据え置きは stale', () => {
      const v = run(
        [{ path: 'docs/a.md', baseText: doc('2026-07-08', '旧'), headText: doc('2026-07-08', '新') }],
        '2026-08-09',
      );
      assert.strictEqual(v.length, 1);
      assert.strictEqual(v[0].kind, 'stale');
    });

    ok('check-doc-updated: 本文を変えて updated: を進めれば通る', () => {
      const v = run(
        [{ path: 'docs/a.md', baseText: doc('2026-07-08', '旧'), headText: doc('2026-08-09', '新') }],
        '2026-08-09',
      );
      assert.deepStrictEqual(v, []);
    });

    // ★ 案 A（base から updated: が変わったか）の誤検知を固定する回帰。
    // base 側が既に同じ日付なら、同日中の再編集で据え置きが**正しい**。
    // 案 A を入れていたら PR #648 の docs/api/BFF_bff-surface.md がここで落ちていた。
    ok('check-doc-updated: 同日中の再編集（base と同じ日付）は通る', () => {
      const v = run(
        [{ path: 'docs/a.md', baseText: doc('2026-08-09', '旧'), headText: doc('2026-08-09', '新') }],
        '2026-08-09',
      );
      assert.deepStrictEqual(v, []);
    });

    ok('check-doc-updated: frontmatter だけの変更は対象外', () => {
      const v = run(
        [{ path: 'docs/a.md', baseText: doc('2026-07-08', '同じ'), headText: doc('2026-07-09', '同じ') }],
        '2026-08-09',
      );
      assert.deepStrictEqual(v, []);
    });

    ok('check-doc-updated: updated: を持たない文書は violation にせず notice へ回す', () => {
      const r = findUpdatedViolations(
        [{ path: 'docs/README.md', baseText: '# a\n旧\n', headText: '# a\n新\n' }],
        '2026-08-09',
      );
      assert.deepStrictEqual(r.violations, []);
      assert.deepStrictEqual(r.skippedNoUpdated, ['docs/README.md']);
    });

    ok('check-doc-updated: 日付形式でない updated: は invalid-date', () => {
      const v = run(
        [{ path: 'docs/a.md', baseText: doc('2026-07-08', '旧'), headText: doc('未定', '新') }],
        '2026-08-09',
      );
      assert.strictEqual(v.length, 1);
      assert.strictEqual(v[0].kind, 'invalid-date');
    });

    ok('check-doc-updated: 新規追加も検査する（base 版が無い＝本文は全部が変更）', () => {
      const v = run(
        [{ path: 'docs/new.md', baseText: null, headText: doc('2026-07-08', '新規') }],
        '2026-08-09',
      );
      assert.strictEqual(v.length, 1);
      assert.strictEqual(v[0].kind, 'stale');
    });

    // ★ PR #652 レビュー 1 巡目の誤検知を固定する回帰。
    // テンプレートの `updated:` は雛形の穴（`<YYYY-MM-DD>`）であり、**埋まっていないのが正しい**。
    // 本文を編集するたびに invalid-date で落ちると、**検査器が邪魔者になって外される**。
    ok('check-doc-updated: docs/templates/ は対象外（雛形の穴で落ちない）', () => {
      const tpl = '---\ntitle: t\nupdated: <YYYY-MM-DD>\n---\n\n';
      const v = run(
        [{ path: 'docs/templates/spec_template.md', baseText: tpl + '旧\n', headText: tpl + '新\n' }],
        '2026-08-09',
      );
      assert.deepStrictEqual(v, []);
      // 同じ穴つき frontmatter でも、テンプレート**以外**なら invalid-date で落ちること
      // （除外がパスに閉じていて、日付検査そのものを緩めていないことの確認）。
      const outside = run(
        [{ path: '.ai-context/specs/x.md', baseText: tpl + '旧\n', headText: tpl + '新\n' }],
        '2026-08-09',
      );
      assert.strictEqual(outside.length, 1);
      assert.strictEqual(outside[0].kind, 'invalid-date');
    });

    // 実データ: 追跡下のテンプレートが本当に穴のままであること（前提が崩れたら気づく）。
    ok('check-doc-updated: 実データのテンプレートは updated: が穴のまま', () => {
      const fsTpl = require('fs');
      const pathTpl = require('path');
      const dir = pathTpl.join(__dirname, '..', 'docs', 'templates');
      const files = fsTpl.readdirSync(dir).filter((f) => f.endsWith('.md'));
      assert.ok(files.length > 0, 'テンプレートが 1 件も無い');
      const withPlaceholder = files.filter((f) =>
        /^updated:\s*<YYYY-MM-DD>/m.test(fsTpl.readFileSync(pathTpl.join(dir, f), 'utf8')),
      );
      assert.ok(
        withPlaceholder.length > 0,
        '穴つきテンプレートが 1 件も無い（除外の前提が変わったなら除外を見直すこと）',
      );
    });

    ok('check-doc-updated: 削除された文書は対象外', () => {
      const v = run([{ path: 'docs/gone.md', baseText: doc('2026-07-08', '旧'), headText: null }], '2026-08-09');
      assert.deepStrictEqual(v, []);
    });

    ok('check-doc-updated: frontmatter の抽出（無い・閉じていない場合は全文が本文）', () => {
      assert.strictEqual(stripFrontmatter('# a\nb\n'), '# a\nb\n');
      assert.strictEqual(stripFrontmatter('---\nx: 1\n'), '---\nx: 1\n');
      assert.strictEqual(stripFrontmatter('---\nx: 1\n---\n本文\n'), '本文\n');
      assert.strictEqual(readUpdated('---\nupdated: 2026-08-09\n---\nb\n'), '2026-08-09');
      assert.strictEqual(readUpdated('---\nupdated: "2026-08-09"\n---\nb\n'), '2026-08-09');
      assert.strictEqual(readUpdated('# a\n'), null);
    });

    // 実データ。本ブランチの docs/ 変更に据え置きが無いこと（＝自分自身に適用する）。
    ok('check-doc-updated が実データで違反 0 件', () => {
      const { spawnSync: spawnUpd } = require('child_process');
      const pathUpd = require('path');
      const r = spawnUpd(process.execPath, [pathUpd.join(__dirname, 'check-doc-updated.js')], {
        encoding: 'utf8',
      });
      assert.strictEqual(r.status, 0, `updated: の据え置きがある:\n${r.stdout}\n${r.stderr}`);
    });
  }

  //
  // NFR / #647: openapi.yaml の宣言ロール（x-roles）と BFF 実装の実効ロールを突き合わせる検査器。
  // **認可を狭めたのに契約が追随しない事故が 2 回起きた**ので入れた（#629 → #640 で偶然発見）。
  // ここが CI 呼び出し口である（IADR-0140 決定 2 の相乗り。check-doc-updated と同じ）。
  {
    const authz = require('./check-bff-authz-docs.js');

    const AUTH_SRC = `
      public static class PlatformAuthPolicies {
        public const string AdminOnly = "AdminOnly";
        public const string AdminRole = "platform-admin";
        public const string OperatorRole = "platform-operator";
      }
      services.AddAuthorization(options => {
        options.AddPolicy(PlatformAuthPolicies.AdminOnly, policy =>
          policy.RequireRole(PlatformAuthPolicies.AdminRole));
      });
    `;
    const { consts, policies } = authz.loadPolicies(AUTH_SRC);

    ok('check-bff-authz-docs: ポリシー名 → ロールをソースから解決する', () => {
      assert.deepStrictEqual([...policies.AdminOnly], ['platform-admin']);
      assert.strictEqual(consts.OperatorRole, 'platform-operator');
    });

    // ★ 実測 4 点（作業仕様書 #647 §母集合）を 1 つずつ固定する。
    // どれも「素朴な実装なら壊れる」形であり、壊れたら実効ロールを誤って報告する。
    ok('check-bff-authz-docs: 認可は AND 合成される（群 admin+operator × 端点 AdminOnly = admin）', () => {
      const stmt = 'g.MapPost("/x", h).WithName("N").RequireAuthorization(PlatformAuthPolicies.AdminOnly)';
      assert.deepStrictEqual([...authz.rolesFromStatement(stmt, consts, policies)], ['platform-admin']);
    });

    ok('check-bff-authz-docs: RequireAuthorization() だけならロール制約なし', () => {
      assert.strictEqual(
        authz.rolesFromStatement('g.MapGet("/x", h).RequireAuthorization()', consts, policies),
        null,
      );
    });

    ok('check-bff-authz-docs: RequireRole ラムダを解ける', () => {
      const stmt =
        '.RequireAuthorization(p => p.RequireRole(PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))';
      assert.deepStrictEqual(
        [...authz.rolesFromStatement(stmt, consts, policies)].sort(),
        ['platform-admin', 'platform-operator'],
      );
    });

    // 実測 2・3: 認可が MapGroup / WithName と別行でも、`;` までを 1 文として読めること。
    ok('check-bff-authz-docs: 文は深さ 0 の ; まで（ラムダ本体の ; で切れない）', () => {
      const src = 'g.MapGet("/x", async () => { var a = 1; return a; }).RequireAuthorization();';
      const stmt = authz.statementFrom(src, 0);
      assert.ok(stmt.includes('RequireAuthorization'), '端点の認可まで届いていない');
    });

    // コメント内の ; や " で壊れないこと（stripComments）。
    ok('check-bff-authz-docs: コメントを落としても文字列は守る', () => {
      const out = authz.stripComments('var a = "x;y"; // c;omment "q"\nvar b = 2;');
      assert.ok(out.includes('"x;y"'), '文字列が壊れた');
      assert.ok(!out.includes('omment'), 'コメントが残っている');
    });

    // 実装の経路制約を openapi の表記へ揃える。
    ok('check-bff-authz-docs: {id:guid} を {id} へ正規化する', () => {
      assert.strictEqual(authz.normalizePath('/bff/documents/{id:guid}/x'), '/bff/documents/{id}/x');
    });

    ok('check-bff-authz-docs: x-roles を読む（インライン形・列挙形とも）', () => {
      const yaml = [
        '  /bff/a:',
        '    get:',
        '      x-roles: [platform-admin, platform-operator]',
        '    post:',
        '      x-roles:',
        '        - platform-admin',
      ].join('\n');
      const ops = authz.collectContract(yaml);
      assert.deepStrictEqual(ops.get('get /bff/a').roles, ['platform-admin', 'platform-operator']);
      assert.deepStrictEqual(ops.get('post /bff/a').roles, ['platform-admin']);
    });

    // ★ 判定の両方向。片側だけ見るテストでは「誰でも通る」状態を検出できない。
    ok('check-bff-authz-docs: 実装が狭く文書が広いと fail（#629 の事故そのもの）', () => {
      const eps = [{ file: 'f', path: '/bff/documents', method: 'post', roles: new Set(['platform-admin']) }];
      const ops = new Map([['post /bff/documents', { roles: ['platform-admin', 'platform-operator'], line: 1 }]]);
      const v = authz.findViolations(eps, ops);
      assert.strictEqual(v.length, 1);
      assert.strictEqual(v[0].kind, 'mismatch');
    });

    ok('check-bff-authz-docs: 実装が広く文書が狭くても fail（逆向き）', () => {
      const eps = [{
        file: 'f', path: '/bff/documents', method: 'post',
        roles: new Set(['platform-admin', 'platform-operator']),
      }];
      const ops = new Map([['post /bff/documents', { roles: ['platform-admin'], line: 1 }]]);
      assert.strictEqual(authz.findViolations(eps, ops).length, 1);
    });

    ok('check-bff-authz-docs: 一致していれば通る', () => {
      const eps = [{ file: 'f', path: '/bff/x', method: 'get', roles: new Set(['platform-admin']) }];
      const ops = new Map([['get /bff/x', { roles: ['platform-admin'], line: 1 }]]);
      assert.deepStrictEqual(authz.findViolations(eps, ops), []);
    });

    // 新しい端点が x-roles を書かずに素通りしないこと（allowlist を持たない代わりの担保）。
    ok('check-bff-authz-docs: x-roles が無い端点は fail（書き忘れを素通りさせない）', () => {
      const eps = [{ file: 'f', path: '/bff/x', method: 'get', roles: new Set(['platform-admin']) }];
      const ops = new Map([['get /bff/x', { roles: null, line: 9 }]]);
      const v = authz.findViolations(eps, ops);
      assert.strictEqual(v[0].kind, 'missing-x-roles');
    });

    // ★ #656: **無認証は、契約と一致していても違反である。**
    // `rolesFromStatement` は「認可属性なし」と「RequireAuthorization()」をどちらも null へ畳むため、
    // ロールの一致だけを見ていると**無認証の端点が素通りする**（#521 が 1 例目・#656 が 2 例目）。
    ok('check-bff-authz-docs: 無認証の端点は x-roles: [] と一致していても fail（#656）', () => {
      const eps = [{ file: 'f', path: '/bff/search', method: 'post', roles: null, requiresAuth: false }];
      const ops = new Map([['post /bff/search', { roles: [], line: 1 }]]);
      const v = authz.findViolations(eps, ops);
      assert.strictEqual(v.length, 1);
      assert.strictEqual(v[0].kind, 'anonymous');
    });

    // ★ #656: **検査器自身の盲点。** 群を辿って認可を合成する設計なので、
    // `app.MapVerb("/bff/...")` を群外に書かれると `requiresAuth` を判定できない。
    // 黙って読み飛ばすと「無認証の /bff/ 端点は存在しない」という不変条件がすり抜ける。
    // **`collectImplementation` に実際に読ませる。** 手組みの端点オブジェクトを `findViolations` へ
    // 渡すだけでは、**検出そのもの（正規表現で `app.MapVerb("/bff/...")` を拾う経路）が 1 度も走らない**。
    // 実データには群外の `/bff/` 端点が無いため、そこからも到達しない。
    ok('check-bff-authz-docs: 群に属さない /bff/ 端点を違反として報告する（#656）', () => {
      const fsG = require('fs');
      const osG = require('os');
      const pathG = require('path');
      const dir = fsG.mkdtempSync(pathG.join(osG.tmpdir(), 'bff-authz-'));
      const file = pathG.join(dir, 'RogueBffEndpoints.cs');
      try {
        fsG.writeFileSync(file, [
          'public static class RogueBffEndpoints {',
          '  public static IEndpointRouteBuilder Map(this IEndpointRouteBuilder app) {',
          '    var g = app.MapGroup("/bff/ok").RequireAuthorization();',
          '    g.MapGet("/", h);',
          '    app.MapPost("/bff/rogue", h);          // 群外の /bff/ → 検出される',
          '    app.MapPost("/internal/thing", h);     // 群外だが /bff/ ではない → 検出されない',
          '    return app;',
          '  }',
          '}',
        ].join('\n'));

        const eps = authz.collectImplementation([file], consts, policies);
        const rogue = eps.find((e) => e.path === '/bff/rogue');
        assert.ok(rogue, '群外の /bff/ 端点を拾えていない');
        assert.strictEqual(rogue.ungrouped, true);
        assert.ok(!eps.some((e) => e.path.startsWith('/internal/')), '/internal/ を拾っている');
        // 群内の端点は通常どおり（ungrouped ではない）。
        assert.strictEqual(eps.find((e) => e.path === '/bff/ok').ungrouped, undefined);

        const v = authz.findViolations(eps, new Map([['get /bff/ok', { roles: [], line: 1 }]]));
        assert.strictEqual(v.length, 1);
        assert.strictEqual(v[0].kind, 'ungrouped');
        assert.strictEqual(v[0].key, 'post /bff/rogue');
      } finally {
        fsG.rmSync(dir, { recursive: true, force: true });
      }
    });

    ok('check-bff-authz-docs: 群外でも /bff/ 以外（/internal/ 等）は対象外（#656）', () => {
      const fsU = require('fs');
      const pathU = require('path');
      const real = authz.loadPolicies(fsU.readFileSync(
        pathU.join(__dirname, '..',
          'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs'),
        'utf8'));
      // 実データ: ConfigBffEndpoints は `/internal/config/drift-run` を群外に持つ（意図的・メッシュ内部限定）。
      const eps = authz.collectImplementation(
        [pathU.join(__dirname, '..',
          'src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/ConfigBffEndpoints.cs')],
        real.consts, real.policies);
      assert.ok(!eps.some((e) => e.ungrouped), '/internal/ を違反として拾っている');
      assert.ok(!eps.some((e) => e.path.startsWith('/internal/')), '/internal/ を端点として数えている');
    });

    ok('check-bff-authz-docs: 認証のみ（RequireAuthorization()）は通る（#656）', () => {
      const eps = [{ file: 'f', path: '/bff/search', method: 'post', roles: null, requiresAuth: true }];
      const ops = new Map([['post /bff/search', { roles: [], line: 1 }]]);
      assert.deepStrictEqual(authz.findViolations(eps, ops), []);
    });

    // ★ 誤検出の側。`ConfigBffEndpoints` は **RequireAuthorization を意図的に付けず**、
    // ハンドラ内 `AuthorizeAsync(ConfigViewer)` ＋ 404 で存在を秘匿する（IADR-0009）。
    // **ミドルウェアの有無で判定すると、この 3 本を「無認証」と誤って報告する。**
    ok('check-bff-authz-docs: ハンドラ内認可の端点を無認証と誤判定しない（#656 / 実データ）', () => {
      const fsC = require('fs');
      const pathC = require('path');
      const real = authz.loadPolicies(fsC.readFileSync(
        pathC.join(__dirname, '..',
          'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs'),
        'utf8'));
      const eps = authz.collectImplementation(
        [pathC.join(__dirname, '..',
          'src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/ConfigBffEndpoints.cs')],
        real.consts, real.policies);
      assert.ok(eps.length > 0, '端点を 1 つも抽出できていない');
      for (const ep of eps) {
        assert.strictEqual(ep.requiresAuth, true, `${ep.path} を無認証と誤判定している`);
      }
    });

    // 実データ: 無認証の端点が 1 つも無いこと（#656 の受け入れ基準）。
    ok('check-bff-authz-docs: /bff/* に無認証の端点が無い（#656 / 実データ）', () => {
      const fsD = require('fs');
      const pathD = require('path');
      const real = authz.loadPolicies(fsD.readFileSync(
        pathD.join(__dirname, '..',
          'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs'),
        'utf8'));
      const bffFiles = [];
      const walkBff = (d) => {
        if (!fsD.existsSync(d)) return;
        for (const e of fsD.readdirSync(d, { withFileTypes: true })) {
          const p = pathD.join(d, e.name);
          if (e.isDirectory()) walkBff(p);
          else if (e.name.endsWith('BffEndpoints.cs')) bffFiles.push(p);
        }
      };
      for (const r of ['src/platform/backend/Bff', 'src/knowledge/backend/Bff']) {
        walkBff(pathD.join(__dirname, '..', r));
      }
      const eps = authz.collectImplementation(bffFiles.sort(), real.consts, real.policies);
      const anon = eps.filter((e) => !e.requiresAuth).map((e) => `${e.method} ${e.path}`);
      assert.deepStrictEqual(anon, [], '無認証で到達できる端点が在る（NFR-09 暫定運用）');
    });

    ok('check-bff-authz-docs: ロール制約なしと x-roles: [] は一致', () => {
      const eps = [{ file: 'f', path: '/bff/search', method: 'post', roles: null, requiresAuth: true }];
      const ops = new Map([['post /bff/search', { roles: [], line: 1 }]]);
      assert.deepStrictEqual(authz.findViolations(eps, ops), []);
    });

    // ★ PR #653 レビュー 1 巡目の 🔴 を固定する回帰。
    // `ConfigBffEndpoints` は **RequireAuthorization を意図的に付けず**、ハンドラ内で
    // AuthorizeAsync(ConfigViewer) を呼んで 404 で存在を秘匿する（IADR-0009）。
    // **ミドルウェアを使っていないだけでロール制約は在る。** 当初これを「制約なし」と
    // 誤って記録しかけた——コメントだけ読んで DenyAsync の本体を開かなかったためである。
    ok('check-bff-authz-docs: ハンドラ内の AuthorizeAsync も実効ロールに数える', () => {
      const pol = { ConfigViewer: new Set(['platform-admin', 'platform-operator']) };
      const src = `
        private static async Task<IResult?> DenyAsync(HttpContext http, IAuthorizationService authz) {
          var authorized = (await authz.AuthorizeAsync(http.User, PlatformAuthPolicies.ConfigViewer)).Succeeded;
          if (!authorized) { return Results.NotFound(); }
          return null;
        }
      `;
      const helpers = authz.collectAuthHelpers(src, consts, pol);
      assert.ok(helpers.DenyAsync, 'ヘルパを認可の担い手として拾えていない');
      assert.deepStrictEqual([...helpers.DenyAsync].sort(), ['platform-admin', 'platform-operator']);
    });

    ok('check-bff-authz-docs: 直接の AuthorizeAsync も拾う', () => {
      const pol = { ConfigViewer: new Set(['platform-admin', 'platform-operator']) };
      const roles = authz.rolesFromAuthorizeAsync(
        'await authz.AuthorizeAsync(http.User, PlatformAuthPolicies.ConfigViewer)', consts, pol);
      assert.deepStrictEqual([...roles].sort(), ['platform-admin', 'platform-operator']);
    });

    // 実データ: /bff/admin/config は「制約なし」ではないこと（🔴 の再発防止）。
    ok('check-bff-authz-docs: /bff/admin/config の実効ロールは admin+operator', () => {
      const fsC = require('fs');
      const pathC = require('path');
      const authSrc = fsC.readFileSync(
        pathC.join(__dirname, '..',
          'src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs'),
        'utf8');
      const real = authz.loadPolicies(authSrc);
      const eps = authz.collectImplementation(
        [pathC.join(__dirname, '..',
          'src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/ConfigBffEndpoints.cs')],
        real.consts, real.policies);
      assert.ok(eps.length > 0, '端点を 1 つも抽出できていない');
      for (const ep of eps) {
        assert.notStrictEqual(ep.roles, null, `${ep.path} が「制約なし」になっている`);
        assert.deepStrictEqual([...ep.roles].sort(), ['platform-admin', 'platform-operator']);
      }
    });

    // 実データ。実装と openapi.yaml が一致していること。
    ok('check-bff-authz-docs が実データで違反 0 件', () => {
      const { spawnSync: spawnAuthz } = require('child_process');
      const pathAuthz = require('path');
      const r = spawnAuthz(process.execPath, [pathAuthz.join(__dirname, 'check-bff-authz-docs.js')], {
        encoding: 'utf8',
      });
      assert.strictEqual(r.status, 0, `宣言ロールと実効ロールが食い違っている:\n${r.stdout}\n${r.stderr}`);
    });

    // allowlist を持たないこと（#647 受け入れ基準。持つと事故を隠す）。
    ok('check-bff-authz-docs: allowlist ファイルを持たない', () => {
      const fsA = require('fs');
      const pathA = require('path');
      const src = fsA.readFileSync(pathA.join(__dirname, 'check-bff-authz-docs.js'), 'utf8');
      assert.ok(!/allowlist/i.test(src.replace(/allowlist を持たない[^\n]*/g, '')
        .replace(/allowlist は事故を隠[^\n]*/g, '')
        .replace(/allowlist ファイルを持たない[^\n]*/g, '')
        .replace(/allowlist を持たない（#647[^\n]*/g, '')),
        'allowlist を参照している');
    });
  }

  //
  // NFR / #525: openapi.yaml の components.schemas と C# 契約 record を突き合わせる検査器。
  // **契約が実装と食い違ったまま残る事故が 4 回起きた**ので入れた
  // （#118 パス誤り 3 件 / #506 型誤り / #520 required 欠落 / #525 フィールド欠落）。
  // `.github/workflows/` は編集不可なので、ここが CI 呼び出し口である（IADR-0140 決定 2 の相乗り）。
  {
    const { spawnSync: spawnDrift } = require('child_process');
    const pathDrift = require('path');
    const driftScript = pathDrift.join(__dirname, 'check-openapi-dto-drift.js');
    const runDrift = (args) => spawnDrift(process.execPath, [driftScript, ...args], { encoding: 'utf8' });

    ok('check-openapi-dto-drift --self-test が通る', () => {
      const r = runDrift(['--self-test']);
      assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
    });

    ok('check-openapi-dto-drift が実データで違反 0 件', () => {
      const r = runDrift([]);
      assert.strictEqual(r.status, 0, `契約と C# DTO が食い違っている:\n${r.stdout}\n${r.stderr}`);
    });

    // ★ #525 そのものの回帰。`granted` を落とすと deny-by-default と全件許可が
    // 契約の上で同一になる。**検査器がそれを検出できることを実データで固定する。**
    ok('check-openapi-dto-drift: AccessScopeResponse.granted の欠落を検出する', () => {
      const drift = require('./check-openapi-dto-drift.js');
      const fsD = require('fs');
      const schemas = drift.collectSchemas(
        fsD.readFileSync(pathDrift.join(__dirname, '..', 'docs/api/openapi.yaml'), 'utf8'));
      assert.ok(schemas.AccessScopeResponse, 'AccessScopeResponse を読めていない');
      // 実データ: いまは載っている。
      assert.ok(schemas.AccessScopeResponse.props.includes('granted'));
      assert.ok(schemas.AccessScopeResponse.required.includes('granted'));
      // 落とすと検出される（properties から消した場合・required から外した場合の両方）。
      const cs = { AccessScopeResponse: [{ name: 'Granted', nonNullable: true }] };
      assert.strictEqual(
        drift.findDrift({ AccessScopeResponse: { props: [], required: [] } }, cs, { entries: [] })[0].kind,
        'missing-in-openapi');
      assert.strictEqual(
        drift.findDrift({ AccessScopeResponse: { props: ['granted'], required: [] } }, cs, { entries: [] })[0].kind,
        'missing-in-required');
    });

    // #658 / IADR-0162: ラチェット（requiredMismatchBaseline・是正待ち 10 件）は**撤去した**。
    // 10 件のうち是正すべき乖離は 3 件だけで、残り 7 件は偽陽性か意図的な差だった。
    // **空配列でも残さない** —— 「また据え置いてよい」と読めるからである。
    ok('check-openapi-dto-drift: requiredMismatchBaseline が復活していない', () => {
      const fsB = require('fs');
      const list = JSON.parse(
        fsB.readFileSync(pathDrift.join(__dirname, 'openapi-dto-drift-allowlist.json'), 'utf8'));
      assert.ok(!('requiredMismatchBaseline' in list),
        'ラチェットは #658 で撤去した。据え置きたい差は理由を書いて requiredExceptions へ入れること');
    });

    // `requiredExceptions` は**理由つきの宣言**である。理由の無い据え置きを機械で止める。
    ok('check-openapi-dto-drift: requiredExceptions は理由を持ち、2 件を超えない', () => {
      const fsB = require('fs');
      const list = JSON.parse(
        fsB.readFileSync(pathDrift.join(__dirname, 'openapi-dto-drift-allowlist.json'), 'utf8'));
      const ex = list.requiredExceptions || [];
      assert.ok(ex.length <= 2, `意図的な差が増えている（${ex.length} 件）。増やす前に契約と C# のどちらが正しいかを決めること`);
      for (const e of ex) {
        assert.ok(e.reason && e.reason.trim(), `${e.schema}.${e.property} に reason が無い`);
      }
    });

    // ★ **規則を緩めすぎていないことの側**（#658 の変異試験 M3）。
    // 要求側にのみ到達するスキーマでも、**既定値を持たない**非 null メンバーは required でなければならない。
    // これが抜けると、要求スキーマの必須漏れを丸ごと見逃す判定器になる。
    ok('check-openapi-dto-drift: 要求側でも既定値なしの非 null は required を要求する', () => {
      const drift = require('./check-openapi-dto-drift.js');
      const schemas = { Req: { props: ['a', 'b'], required: [] } };
      const records = {
        Req: [
          { name: 'A', nonNullable: true, hasDefault: true },
          { name: 'B', nonNullable: true, hasDefault: false },
        ],
      };
      const found = drift.findDrift(schemas, records, { entries: [] }, new Set(['Req']));
      assert.deepStrictEqual(found.map((d) => d.property), ['b'],
        '既定値つき a は見逃してよいが、既定値なし b は required を要求しなければならない');
    });

    // 到達性そのもの。実データで狙った 5 件に当たっていること（規則が「たまたま」効いていない側）。
    ok('check-openapi-dto-drift: 実データの要求側スキーマを取れる', () => {
      const fsB = require('fs');
      const drift = require('./check-openapi-dto-drift.js');
      const yaml = fsB.readFileSync(
        pathDrift.join(__dirname, '..', 'docs/api/openapi.yaml'), 'utf8');
      const { requestOnly } = drift.collectReachability(yaml);
      for (const n of ['SearchRequest', 'AnalysisTaskRequest', 'AnalysisDataRange',
        'CompletionApiRequest', 'EmbedApiRequest']) {
        assert.ok(requestOnly.has(n), `${n} が要求側として取れていない`);
      }
      assert.ok(!requestOnly.has('ConversionJobDto'), 'ConversionJobDto は応答側である');
    });
  }

  // ── #546 / IADR-0164: Grafana の LLM ダッシュボードが実装のメトリクスから乖離しないこと ──
  //
  // 月次の手動確認（`docs/operations/llm-cost-monthly-review-runbook.md`）はこのダッシュボードを
  // 見に行く。**式のメトリクス名・属性名が実装とずれると、運用者は空のグラフを見て「異常なし」と記録する。**
  // 名前の一致だけは機械で固定する（描画そのものは Grafana を起動できないため検証していない）。
  {
    const fsG = require('fs');
    const pathG = require('path');
    const root = pathG.join(__dirname, '..');
    const dashFile = 'deploy/grafana/provisioning/dashboards/llm-usage.json';

    ok('llm-usage ダッシュボード: JSON として妥当で uid が既存と重複しない', () => {
      const d = JSON.parse(fsG.readFileSync(pathG.join(root, dashFile), 'utf8'));
      const other = JSON.parse(fsG.readFileSync(
        pathG.join(root, 'deploy/grafana/provisioning/dashboards/microservices-platform-overview.json'), 'utf8'));
      assert.ok(d.uid && d.uid !== other.uid, `uid が空か重複している: ${d.uid}`);
      assert.ok(Array.isArray(d.panels) && d.panels.length > 0, 'panels が空');
    });

    // ★ **母集合を 1 ファイルに絞ると誤判定する。** 属性値（max_tokens / refusal）の実体は
    // CompletionStopReasons（CompletionDto.cs）にあり、メトリクス側は定数を参照しているだけである。
    ok('llm-usage ダッシュボード: 式の名前がすべて実装に実在する', () => {
      const d = JSON.parse(fsG.readFileSync(pathG.join(root, dashFile), 'utf8'));
      const exprs = d.panels.flatMap((p) => (p.targets || []).map((t) => t.expr)).join(' ');
      const src = fsG.readFileSync(pathG.join(root,
        'src/platform/backend/Services/LlmGateway/src/LlmGateway.Api/Foundation/Observability/LlmCompletionMetrics.cs'), 'utf8');
      const dto = fsG.readFileSync(pathG.join(root,
        'src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/CompletionDto.cs'), 'utf8');
      const impl = src + dto;

      // メトリクス名（OTel の `.` は Prometheus で `_` になる）。
      const metrics = [...new Set(exprs.match(/\bllm_[a-z_]+_total\b/g) || [])];
      assert.deepStrictEqual(metrics, ['llm_completion_total'], '式のメトリクス名が想定と違う');
      assert.ok(/"llm\.completion\.total"/.test(impl), '実装に llm.completion.total が無い');

      // 属性名。
      for (const attr of new Set(exprs.match(/\bllm_(?:result|purpose|model|provider|stop_reason|confidentiality)\b/g) || [])) {
        const otel = attr.replace(/^llm_/, 'llm.');
        assert.ok(impl.includes(`"${otel}"`), `式の属性 ${attr} が実装に無い`);
      }

      // 属性値（リテラルで絞り込んでいるもの）。
      const vals = new Set();
      for (const m of exprs.matchAll(/llm_(?:stop_reason|result)="([a-z_]+)"/g)) vals.add(m[1]);
      assert.ok(vals.size > 0, '属性値による絞り込みが 1 つも無い（式が空か形が変わった）');
      for (const v of vals) assert.ok(impl.includes(`"${v}"`), `式の属性値 ${v} が実装に無い`);
    });

    // Runbook が指す先が実在すること（#592 / IADR-0163 と同じ趣旨を、この 1 対について持つ）。
    ok('月次確認 Runbook が指すダッシュボードが実在する', () => {
      const rb = fsG.readFileSync(pathG.join(root, 'docs/operations/llm-cost-monthly-review-runbook.md'), 'utf8');
      assert.ok(rb.includes(dashFile), 'Runbook がダッシュボードのパスを指していない');
      assert.ok(fsG.existsSync(pathG.join(root, dashFile)), 'ダッシュボードが存在しない');
      // 決定 41: 絶対額のしきい値を書かない。
      assert.ok(!/[0-9][0-9,]*\s*(円|ドル|USD|JPY|\$)/.test(rb),
        'Runbook に絶対額のしきい値が書かれている（計画 決定 41 に反する）');
    });
  }

  //
  // **ここが check-cross-repo-refs.js の CI 呼び出し口である。**`.github/workflows/` は
  // GitHub App 権限で編集できないため、新しい検査器を足しても新ジョブからは呼べない。
  // ci.yml の scripts-tests ジョブ（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）が
  // 本 companion を読み込むので、そこから子プロセスで検査器を起動する。
  // 検査器を消す・壊す・実データに違反を混ぜる、のいずれでもこのテストが落ちる。
  // 他プロジェクト（AST）の**計画 ID / ADR ID** の修飾（#576）。`check-cross-repo-refs.js` とは
  // 対象が違う（あちらは issue / PR 番号、こちらは計画 ID）。`.github/workflows/` は編集不可なので
  // 同じ呼び出し口（ci.yml の scripts-tests）へ相乗りする（IADR-0140 決定 2）。
  {
    const { spawnSync: spawnPlanId } = require('child_process');
    const pathPlanId = require('path');
    const planIdScript = pathPlanId.join(__dirname, 'check-plan-id-qualification.js');
    const runPlanId = (args) =>
      spawnPlanId(process.execPath, [planIdScript, ...args], { encoding: 'utf8' });

    ok('check-plan-id-qualification --self-test が通る（正例・負例を対で固定）', () => {
      const r = runPlanId(['--self-test']);
      assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
    });

    ok('check-plan-id-qualification が実データで違反 0 件', () => {
      const r = runPlanId([]);
      assert.strictEqual(r.status, 0, `他プロジェクト ID の修飾違反がある:\n${r.stdout}\n${r.stderr}`);
    });

    // 検出力の実地確認。**規約どおりの形（AST/FR-17）で落ちないこと**も対で見る
    // ——偽陽性を出す検査は外されるため、正例だけでは不十分である。
    ok('check-plan-id-qualification: 空白形で exit 1・規約どおりの形で exit 0', () => {
      const fsPlanId = require('fs');
      const osPlanId = require('os');
      const dir = fsPlanId.mkdtempSync(pathPlanId.join(osPlanId.tmpdir(), 'planid-repo-test-'));
      try {
        const ng = pathPlanId.join(dir, 'ng.md');
        // **違反文字列はリテラルで書かない。** 本検査は `.md` に限らず追跡下の全ファイルを走査する
        // ので、フィクスチャをそのまま書くと**このファイル自身が違反として上がる**（実測した）。
        // 連結で組み立てる（`check-cross-repo-refs` の repo テストが採っている定石と同じ）。
        fsPlanId.writeFileSync(ng, '# x\n\n（AST' + ' IADR-0048 決定3）と AST' + ' FR-17。\n');
        const r = runPlanId([ng]);
        assert.strictEqual(r.status, 1, `違反ファイルで exit 1 にならない:\n${r.stdout}\n${r.stderr}`);
        assert.match(String(r.stderr), /空白区切りの ID 修飾/);

        const okFile = pathPlanId.join(dir, 'ok.md');
        fsPlanId.writeFileSync(okFile, '# y\n\nAST/IADR-0048 と AST/FR-17 と MSP の FR-14。\n');
        assert.strictEqual(runPlanId([okFile]).status, 0, '規約どおりの形で落ちている（偽陽性）');
      } finally {
        fsPlanId.rmSync(dir, { recursive: true, force: true });
      }
    });

    // --- NFR / #756: キット版へ差し替えたことで増えた検出力と、その退行の門 -----------------
    //
    // #756 で HOWTO（`planning/tools/impl-handoff-kit/HOWTO.md` §B-5）の手順どおり実走して
    // 突合した結果、**この 1 本だけはキット版が優った**（`NFR` の検出・置換点・submodule 導出）ため
    // キット版へ差し替えた。以下は「差し替えで増えた側」と「差し替えで失いやすい側」を対で固定する。

    ok('check-plan-id-qualification: NFR も検出する（#756 の差し替えで増えた検出力）', () => {
      const fsNfr = require('fs');
      const osNfr = require('os');
      const dir = fsNfr.mkdtempSync(pathPlanId.join(osNfr.tmpdir(), 'planid-nfr-'));
      try {
        // 違反文字列はリテラルで書かない（本検査は追跡下の全ファイルを走査するため）。
        const ng = pathPlanId.join(dir, 'ng.md');
        fsNfr.writeFileSync(ng, '# x\n\nAST' + ' NFR-01 の目標値。\n');
        const r = runPlanId([ng]);
        assert.strictEqual(r.status, 1, `NFR の空白区切りを検出できていない:\n${r.stdout}\n${r.stderr}`);
        assert.match(String(r.stderr), /NFR-01/);
        // 対の負例: 規約どおりの形は落とさない。
        const okFile = pathPlanId.join(dir, 'ok.md');
        fsNfr.writeFileSync(okFile, '# y\n\nAST/NFR-01 は規約どおり。\n');
        assert.strictEqual(runPlanId([okFile]).status, 0, '規約どおりの NFR 形で落ちている（偽陽性）');
      } finally {
        fsNfr.rmSync(dir, { recursive: true, force: true });
      }
    });

    // **置換点が空へ戻る退行の門**（変異試験）。キット版は `PROJECT_PREFIXES` が空だと
    // **skip して exit 0 を返す** —— 設定し忘れが「緑」で固定される、いちばん気付けない壊れ方である。
    // よって (1) 実ファイルの既定が非空であることと、(2) 空にすると本当に skip へ落ちること（＝
    // 門が守っている対象が実在すること）を対で主張する。**片方だけでは vacuous になる。**
    ok('check-plan-id-qualification: 置換点 PROJECT_PREFIXES が空へ戻ると skip する（変異試験）', () => {
      const fsMut = require('fs');
      const osMut = require('os');
      const planId = require('./check-plan-id-qualification.js');
      assert.ok(
        Array.isArray(planId.PROJECT_PREFIXES) && planId.PROJECT_PREFIXES.length > 0,
        '置換点 PROJECT_PREFIXES が空である（この検査は skip して緑を返し続ける）',
      );

      const dir = fsMut.mkdtempSync(pathPlanId.join(osMut.tmpdir(), 'planid-mutate-'));
      try {
        // `check-cross-repo-refs.js`（maskCode）と `lib/worktree-state.js` を要するので scripts/ ごと写す。
        const mutScripts = pathPlanId.join(dir, 'scripts');
        fsMut.cpSync(__dirname, mutScripts, { recursive: true });
        const target = pathPlanId.join(mutScripts, 'check-plan-id-qualification.js');
        const src = fsMut.readFileSync(target, 'utf8');
        const mutated = src.replace(/(splitList\(process\.env\.PLAN_ID_PREFIXES,\s*)\[[^\]]*\]/, '$1[]');
        assert.notStrictEqual(mutated, src, '置換点の記述を見つけられない（変異を当てられていない）');
        fsMut.writeFileSync(target, mutated);

        const ng = pathPlanId.join(dir, 'ng.md');
        fsMut.writeFileSync(ng, '# x\n\nAST' + ' FR-17。\n');
        const r = spawnPlanId(process.execPath, [target, ng], { encoding: 'utf8' });
        assert.strictEqual(r.status, 0, `空の置換点で exit 0（skip）にならない:\n${r.stdout}\n${r.stderr}`);
        assert.match(String(r.stdout), /skip/, '空の置換点であることを述べていない');
        // 同じ入力を実ファイルへ当てると落ちる（＝差は置換点だけである）。
        assert.strictEqual(runPlanId([ng]).status, 1, '実ファイルが同じ入力を検出できていない');
      } finally {
        fsMut.rmSync(dir, { recursive: true, force: true });
      }
    });

    // 差し替えで失いやすいもう一方: 除外の維持。#576 版の `EXCLUDED_PATH_RE` が持っていた
    // `.ai-context/superpowers/`（旧 docs/superpowers/。外部由来の教材の写し）を、
    // キット版の置換点 `EXTRA_EXCLUDES` で保つ。planning submodule は ADR-0048 決定 2 で撤去済みのため
    // 除外リストから外れ、submodule 導出は残る src/ai-stock-trading のみで確認する。
    ok('check-plan-id-qualification: 除外は submodule 導出 ＋ .ai-context/superpowers/ を保つ', () => {
      const planId = require('./check-plan-id-qualification.js');
      const excluded = planId.createExcluder(['.ai-context/superpowers/']);
      assert.strictEqual(excluded('.ai-context/superpowers/x.md'), true, '.ai-context/superpowers/ の除外が落ちている');
      assert.strictEqual(excluded('planning/x.md'), false, 'planning は撤去済みの submodule なので除外されてはならない');
      assert.strictEqual(
        excluded('src/ai-stock-trading/x.md'),
        true,
        'submodule（src/ai-stock-trading）が .gitmodules から導出されていない',
      );
      assert.strictEqual(excluded('.ai-context/adr/IADR-0000_x.md'), false, '除外が広すぎる');
    });
  }

  {
    const { spawnSync } = require('child_process');
    const pathXrepo = require('path');
    const script = pathXrepo.join(__dirname, 'check-cross-repo-refs.js');
    const run = (args) => spawnSync(process.execPath, [script, ...args], { encoding: 'utf8' });

    ok('check-cross-repo-refs --self-test が通る（正例・負例を対で固定）', () => {
      const r = run(['--self-test']);
      assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
      assert.match(String(r.stdout), /all passed/);
    });

    ok('check-cross-repo-refs: 本リポの *.md が green（実データ）', () => {
      const r = run([]);
      assert.strictEqual(r.status, 0, `実データで違反が出ている:\n${r.stdout}\n${r.stderr}`);
    });

    // 検出力の実地確認（変異試験の常設化）。フィクスチャを 1 枚置いて exit 1 になることを
    // 確かめる。「実データが green」だけでは、検査器が何も見ていない状態と区別できない。
    ok('check-cross-repo-refs: 違反を含む .md を渡すと exit 1（素通りの検出）', () => {
      const fsX = require('fs');
      const osX = require('os');
      const dir = fsX.mkdtempSync(pathXrepo.join(osX.tmpdir(), 'crossrepo-repo-test-'));
      try {
        // 各型を 1 枚に入れる。型 3（空白区切り）は #507 のクロス監査が実測した「第 4 の表記」で、
        // 着手時の母集合から丸ごと欠落していた——**検出されることを常設で確かめる**。
        // 型 4（owner 誤り）は #590。**.md で唯一「死んだリンク」になる型**なので、ここで
        // CI ゲートに載せる（自己試験だけだと実バイナリ経路の回帰を見ない）。
        const ng = pathXrepo.join(dir, 'ng.md');
        fsX.writeFileSync(
          ng,
          '# x\n\n環流は project-planning#50 と planning#206 / #207。追跡は AST' +
            ' #24。実装は endodazon/ai-stock-trading#106。\n'
        );
        const r = run([ng]);
        assert.strictEqual(r.status, 1, `違反ファイルで exit 1 にならない:\n${r.stdout}\n${r.stderr}`);
        assert.match(String(r.stderr), /長い表記/);
        assert.match(String(r.stderr), /列挙形の修飾漏れ/);
        assert.match(String(r.stderr), /空白区切りの修飾/);
        assert.match(String(r.stderr), /フルパス形式の owner 誤り/);

        // 正しい表記へ直すと 0 に戻る（偽陽性を出していないことの対）。
        // **自リポジトリを指す修飾語（MSP）の直後の裸番号は正しい**ので、ここで落ちてはならない。
        const okFile = pathXrepo.join(dir, 'ok.md');
        fsX.writeFileSync(
          okFile,
          '# x\n\n環流は planning#50 と planning#206 / planning#207。追跡は AST#24。\n' +
            '親は #454。MSP' + ' #283 と #450（FR-17/18）・#451（FR-19/20）は本リポジトリの参照。\n' +
            // 型 4 の対（#590）: 規約が許すフルパス形式と、owner が endazon でないのが**正しい**
            // 第三者リポジトリ参照。型 4 を足したせいでこれらが落ちては本末転倒である。
            'フルパスは endazon/ai-stock-trading#106、第三者は anthropics/claude-code-action#723。\n'
        );
        assert.strictEqual(run([okFile]).status, 0, '正しい表記で落ちている（偽陽性）');

        // 閉じないフェンスは「以降のファイル全体が黙って検査対象外」になる経路。fail-loud を固定する。
        const fence = pathXrepo.join(dir, 'fence.md');
        fsX.writeFileSync(fence, '# x\n\n```console\n$ echo unterminated\n');
        const rf = run([fence]);
        assert.strictEqual(rf.status, 1, '閉じないフェンスで exit 1 にならない（黙って盲目化する）');
        assert.match(String(rf.stderr), /閉じないコードフェンス/);
      } finally {
        fsX.rmSync(dir, { recursive: true, force: true });
      }
    });

    // **findViolations が返し得る全 kind に、CI ログ用のラベルが在ること**（#590）。
    // ラベルの追随は 2 度漏れた（#507 で 1 度、型 4 を足した #590 でもう 1 度）。漏れても
    // 検査は落ちず「未知の違反種別 owner」と出るだけなので、テキストの追随では止まらない。
    // **検査器のソースから `kind:` リテラルを静的に集めて突き合わせる**ことで、
    // 将来 kind を足したときも自動的に対象へ入る（フィクスチャ列挙だと新 kind を取りこぼす）。
    ok('crossRepoRefReasons のラベルが findViolations の全 kind を覆う', () => {
      const src = require('fs').readFileSync(script, 'utf8');
      const kinds = [...src.matchAll(/kind:\s*'([a-z]+)'/g)].map((m) => m[1]);
      assert.ok(kinds.length >= 4, `kind リテラルを集められていない（${kinds.length} 件）`);
      const { CROSS_REPO_REF_LABELS } = require(
        pathXrepo.join(__dirname, 'check-commit-messages.js')
      );
      const missing = [...new Set(kinds)].filter((k) => !(k in CROSS_REPO_REF_LABELS));
      assert.deepStrictEqual(
        missing,
        [],
        `check-commit-messages.js の CROSS_REPO_REF_LABELS に無い kind: ${missing.join(', ')}`
      );
    });

    // 規約（.claude/rules/traceability.md）と検査器の対応。規約だけ書いても再発するので
    // 「検査器がある」ことを規約側から辿れる状態を固定する（#507 の受け入れ基準）。
    ok('traceability.md が短縮形の統一と検査器への導線を持つ', () => {
      const fsX = require('fs');
      // ★ #755: 入口はキット配布物 ＋ companion（本リポ固有）の 2 ファイル。連結で見る。
      const rules =
        fsX.readFileSync(pathXrepo.join(__dirname, '..', '.claude', 'rules', 'traceability.md'), 'utf8') +
        '\n' +
        fsX.readFileSync(pathXrepo.join(__dirname, '..', '.claude', 'rules', 'traceability.repo.md'), 'utf8');
      assert.match(rules, /check-cross-repo-refs\.js/, '規約から検査器へ辿れない');
      assert.match(rules, /列挙形でも各番号を修飾する/, '列挙形の規約が消えている');
      // 型 3（空白区切り）の規約。#507 クロス監査 R1 で追加した。
      assert.match(rules, /修飾語と番号の間に空白を入れない/, '型 3 の規約が消えている');
    });
  }

  // --- NFR / #579 / IADR-0145: check-commit-messages のレンジモードを実バイナリで通す ---
  //
  // **なぜ必要か（#612 レビュー 🔴 で実測）**: FR/UC/SC 実在性検査を足したとき、
  // `checkSingleTitle`（`--title`）へは `planIds` を渡したのに、**`main()` のレンジモード
  // （`ci.yml` の `commit-messages` ジョブが実際に実行する経路）へ渡し忘れていた。**
  // `--title` の変異試験だけが通ったので「検査が効いている」と誤って結論した。
  // **同じ型（呼び出し口を 1 つだけ配線する）はこのリポジトリで 3 度目である**
  // （`crossRepoRefReasons` のラベル欠落が 2 度）。**経路ごとに実バイナリで当てる。**
  {
    const { spawnSync: spawnCcm } = require('child_process');
    const pathCcm = require('path');
    const fsCcm = require('fs');
    const osCcm = require('os');
    const ccmScript = pathCcm.join(__dirname, 'check-commit-messages.js');

    /** 使い捨ての git リポジトリに件名 1 件のコミットを作り、レンジモードで検査する。 */
    const runRangeOn = (subject) => {
      const dir = fsCcm.mkdtempSync(pathCcm.join(osCcm.tmpdir(), 'ccm-range-'));
      const g = (args) =>
        spawnCcm('git', args, { cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
      try {
        g(['init', '-q', '-b', 'main']);
        g(['config', 'user.email', 'test@example.com']);
        g(['config', 'user.name', 'tester']);
        fsCcm.writeFileSync(pathCcm.join(dir, 'a.txt'), 'base\n');
        g(['add', '-A']);
        g(['commit', '-q', '-m', 'chore(NFR): 基点']);
        fsCcm.writeFileSync(pathCcm.join(dir, 'a.txt'), 'head\n');
        g(['add', '-A']);
        g(['commit', '-q', '-m', subject]);
        // 検査器は本リポジトリの .ai-context/adr と .claude/rules を見るので、cwd は一時リポでよい
        // （--range で範囲だけを与える）。
        return spawnCcm(process.execPath, [ccmScript, '--range', 'HEAD~1..HEAD'], {
          cwd: dir,
          encoding: 'utf8',
        });
      } finally {
        fsCcm.rmSync(dir, { recursive: true, force: true });
      }
    };

    ok('check-commit-messages レンジモード: 正当な件名は通る（正例）', () => {
      const r = runRangeOn('feat(FR-12,SC-07): 正当な起点 ID');
      assert.strictEqual(r.status, 0, `正当な件名で落ちた:\n${r.stdout}\n${r.stderr}`);
    });

    ok('check-commit-messages レンジモード: 実在しない画面 ID で exit 1（#612 レビュー 🔴 の回帰）', () => {
      const r = runRangeOn('feat(SC-99): 存在しない画面 ID');
      assert.strictEqual(
        r.status,
        1,
        `レンジモードで実在性検査が効いていない（planIds の配線漏れ）:\n${r.stdout}\n${r.stderr}`
      );
      assert.match(String(r.stdout) + String(r.stderr), /計画レンジに実在しない/);
    });

    ok('check-commit-messages レンジモード: 実在しない要求 ID / UC でも exit 1', () => {
      for (const subject of ['feat(FR-77): 存在しない要求 ID', 'feat(UC-88): 存在しない UC']) {
        const r = runRangeOn(subject);
        assert.strictEqual(r.status, 1, `${subject} で落ちない:\n${r.stdout}\n${r.stderr}`);
      }
    });
  }

  // --- NFR / #579 / IADR-0145: スカッシュ着地件名の検査 -----------------------------
  // `pr-title.yml`（PR タイトル）と `commit-messages`（base..HEAD）のどちらでもない
  // **第 3 の文字列** ——実際に develop へ載る件名——を規約へ照合する。
  // **#568 で実際に起きた「マージ時に ID が落ちる」型は本検査では検出できない**
  // （落ちた後の件名はそれ自体が規約適合であり、判定には PR タイトルとの突合が要る）。
  // 検出できるのは書式違反と実在しない起点 ID の 2 型で、どちらも事後検知である。
  {
    const { spawnSync: spawnLanded } = require('child_process');
    const pathLanded = require('path');
    const landedScript = pathLanded.join(__dirname, 'check-landed-subjects.js');
    const runLanded = (args) =>
      spawnLanded(process.execPath, [landedScript, ...args], { encoding: 'utf8' });

    ok('check-landed-subjects --self-test が通る（M1〜M6 ＋ ラチェット 3 件を対で固定）', () => {
      const r = runLanded(['--self-test']);
      assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}\n${r.stderr}`);
    });

    ok('check-landed-subjects が実データで baseline 外の違反 0 件', () => {
      const r = runLanded([]);
      // 浅いクローンでは skip される（notice が出て exit 0）。その場合も緑でよい——
      // **skip したことは notice で可視化されている**（黙って 0 件にはしない）。
      assert.strictEqual(r.status, 0, `着地件名に baseline 外の違反がある:\n${r.stdout}\n${r.stderr}`);
    });

    // **実バイナリ経路での検出力**。自己試験は純関数を直接叩くので、CLI が exit 1 を返すかは別に見る。
    // ラチェットを 1 段緩める（baseline から 1 件外す）と、その件が「新規違反」として落ちること。
    ok('check-landed-subjects: baseline を 1 件緩めると exit 1（ラチェットが効いている）', () => {
      const fsLanded = require('fs');
      const blPath = pathLanded.join(__dirname, 'landed-subject-baseline.json');
      const original = fsLanded.readFileSync(blPath, 'utf8');
      const json = JSON.parse(original);
      if (json.known.length === 0) return; // baseline が空なら緩める先が無い（将来 0 件になったとき）
      try {
        fsLanded.writeFileSync(
          blPath,
          JSON.stringify({ ...json, known: json.known.slice(1) }, null, 2) + '\n'
        );
        const r = runLanded([]);
        // 浅いクローンでは skip されるため exit 0 になり得る。その場合は検出力を主張しない。
        if (/浅いクローン/.test(String(r.stdout) + String(r.stderr))) return;
        assert.strictEqual(r.status, 1, `baseline を緩めても落ちない:\n${r.stdout}\n${r.stderr}`);
        assert.match(String(r.stderr), /baseline 外の着地件名の違反/);
      } finally {
        fsLanded.writeFileSync(blPath, original);
      }
    });
  }

  // --- NFR / #580: ADR 索引の行の「形」を固定する検査は #581 側へ統合した ---------------
  //
  // ここに `inspectAdrIndex`（`not-linked` / `id-file-mismatch` / `no-trailing-pipe` と、
  // 索引行数 >= ADR 本体数の fail-open 下限）を置いていたが、**#580 の申し送りどおり**
  // （`.ai-context/specs/20260807_issue-580_adr-records-drift.md`「#581 への申し送り」）
  // `scripts/check-adr-numbering.js` の**判定 6** へ統合し、ここからは削除した。
  // **同じ不変条件の検査を 2 本残さない。** 下限は判定 4（`index-missing`）と
  // `no-adr-files` が同じことをより直接に見るため引き継いでいない。
  // 実行経路は変わらない —— 上の `check-adr-numbering` ブロックが `--self-test` と実データ走査を
  // 呼び、同じ `scripts-tests` ジョブ（`REQUIRE_REPO_TESTS=1`）でゲートになる。

  // [[IADR-0144]] 決定 5: 索引行の抽出式は **1 つに畳む**。定義は `check-adr-numbering.js` に在り、
  // ここでは **import して借りる**（リテラルを複製すると、片方だけ直したとき挙動が黙って割れる
  // ——決定 5 が防ごうとしたのはまさにこれで、統合時は複製のままだった。2026-08-08 監査 Y-3）。
  const { INDEX_LINE_RE: INDEX_LINE_RE_SHARED } = require('./check-adr-numbering.js');

  // --- NFR / #580: 索引タイトルセルのラチェット ---------------------------------------
  //
  // **なぜ必要か（#580 の再監査 🟡-2 で実測）**: 索引と本体の突合から「タイトル列の字義一致」を
  // 外した結果、タイトル列が**完全に無検査**になった。識別可能な変異で確認したところ、
  // `IADR-0005` のタイトルセルを**まったく別の決定の話**へ置換しても全ゲートが緑だった
  // （`doc-links` / `scripts.test` / `cross-repo` / `repo.test` すべて exit 0）。
  //
  // **字義一致を課さない**のは、実測で 141 行中 96 行が本体 `title:` と一致せず、そのうち
  // **87 行は索引の方が長い**（＝索引に決定文がまるごと貼られている）ためである。つまり必要な
  // 是正は「本体へ揃える」ではなく「**索引タイトルセルを要約へ縮める**」であり、字義一致検査は
  // その作業の前に 96 行を一斉に赤にするだけで方向が合わない（`.ai-context/adr/README.md` §運用ルール）。
  //
  // よって**縮める方向へ効く不変条件だけ**をラチェットで固定する。現在の違反を
  // `scripts/adr-index-title-baseline.json` に baseline 化し、**新規混入のみ落とす**
  // （`scripts/backend-library-baseline.json` やカバレッジ床と同じ作法）。直したのに baseline に
  // 残っていれば fail させ、baseline が必ず縮む向きにする。
  //
  //   - 見る:   タイトルセルの空・状態語（`Superseded by IADR-XXXX`）・`［YYYY-MM-DD 追記］`・長さ上限
  //             ＋ **本体 `title:` との文字の共有量**（`title-drift`。下記）
  //   - 見ない: 本体 `title:` との**字義一致**（上記のとおり方向が合わない。#581 も対象外）
  //
  // **`title-drift`（本体と全く別の内容への差し替えを落とす）**。字義一致は課さないが、
  // 「別の決定の話へ丸ごと書き換える」型は**文字の共有量**で落ちる。索引タイトルセルと本体 `title:`
  // （先頭の ID を除いた部分）の**文字単位 LCS（最長共通部分列）**を測り、下限を割ったら違反とする。
  //
  // 実測（本ブロックで再計測・2026-08-07・索引 141 行）:
  //   - LCS の分布は 最小 11 / p10 33 / 中央値 49 / 最大 188。`LCS < 12` に該当するのは **1 行だけ**で、
  //     それは `IADR-0000`（索引と本体が**字義一致**しており、共有できる上限が本体の 11 字しかない）。
  //     つまり**素の下限値は最も正しい行を最初に赤にする**。
  //   - よって下限は**短い側の長さで頭打ちにする**: `LCS < min(minTitleOverlap, 本体長, タイトル長)`。
  //     「12 字以上共有せよ。ただし短い側が 12 字未満なら**短い側の全長**を共有せよ（＝要約は本体の
  //     部分列であれ）」の意味になる。この形なら現状の違反は **0 行**（baseline へ足す行が無い）。
  //   - 閾値を上げると**是正の目標形（短い要約）から先に赤くなる**: 20 にすると `IADR-0010`（19 字）と
  //     `IADR-0011`（18 字）——どちらも既に要約済みの行——が違反になる。ラチェットの向きと衝突する。
  //
  // **検出できること / できないこと**（変異試験で実測・2026-08-07）:
  //   - 検出する: baseline 外の行への `［追記］` 混入 / 他の決定の**全文**をタイトルへ貼る
  //     （監査が使った変異はこの型で、`title-too-long` が捕まえる）/ baseline を縮め忘れた stale
  //     / **200 字以内のきれいな文で別の決定の話へ書き換える**変異（`title-drift`。監査の M4 型。
  //     サンプル 5 行で LCS 1〜7 に対し下限 11〜12 ＝全件落ちる）
  //   - 取りこぼす: 「**別の決定の本体 `title:` をそのまま貼る**」型は総当たり（141 × 140 = 19,740 通り）で
  //     **69% しか落ちない**。残り 31% は日本語の助詞・語尾だけで 12 字を偶然共有して通る。
  //     字義一致まで課せば 100% になるが、それは上記のとおり方向が合わない。**索引を要約へ縮め切った
  //     後なら #581 が字義一致を掛けられる**（そのとき本ブロックは #581 側へ統合する）。
  {
    const TITLE_BASELINE_PATH = path.join(__dirname, 'adr-index-title-baseline.json');
    const titleBaseline = JSON.parse(fs.readFileSync(TITLE_BASELINE_PATH, 'utf8'));

    // 文字単位の最長共通部分列（LCS）の長さ。索引タイトルセルが本体 `title:` と
    // 「どれだけ同じ内容を共有しているか」の下限を測るために使う（字義一致は課さない）。
    const lcsLength = (a, b) => {
      const A = [...a];
      const B = [...b];
      let prev = new Uint16Array(B.length + 1);
      for (let i = 1; i <= A.length; i++) {
        const cur = new Uint16Array(B.length + 1);
        for (let j = 1; j <= B.length; j++) {
          cur[j] = A[i - 1] === B[j - 1] ? prev[j - 1] + 1 : Math.max(prev[j], cur[j - 1]);
        }
        prev = cur;
      }
      return prev[B.length];
    };

    // 索引 1 ファイル分の Markdown を受け取り、タイトルセルの違反を返す純関数。
    // `bodyTitles`（ID → 本体 frontmatter の `title:` から先頭 ID を除いた文字列）を渡したときだけ
    // `title-drift` を見る（渡さない負例テストでは長さ・追記・状態語だけを見る）。
    const inspectAdrIndexTitles = (md, opts = {}) => {
      const maxChars = opts.maxChars ?? titleBaseline.maxTitleChars;
      const minOverlap = opts.minOverlap ?? titleBaseline.minTitleOverlap;
      const bodyTitles = opts.bodyTitles ?? {};
      const violations = [];
      md.split('\n').forEach((line, i) => {
        if (!INDEX_LINE_RE_SHARED.test(line)) return; // 索引の行だけを見る
        const id = line.match(/IADR-\d{4}/)[0];
        const t = line.trim();
        const cells = t.slice(1, t.endsWith('|') ? -1 : undefined).split('|').map((c) => c.trim());
        const title = cells.length >= 3 ? cells[1] : null;
        const push = (kind) => violations.push({ line: i + 1, id, kind });
        if (title === null || title === '') {
          push('title-missing'); // 空セル。索引から決定の内容が読めない
          return;
        }
        // 状態は状態列の役割。タイトルへ書くと 2 箇所に分かれて片方が腐る。
        if (/(Superseded|Deprecated)\s+by\s+I?ADR-\d{4}/.test(title)) push('title-status-word');
        // 追記ブロックは本体本文が持つ。索引へ複製すると同じ内容が 2 箇所にあり索引側が腐る。
        if (/［\d{4}-\d{2}-\d{2}\s*追記/.test(title)) push('title-addendum');
        // 決定文の貼り付けを止める上限。要約に収まる長さを超えたら縮める。
        if ([...title].length > maxChars) push('title-too-long');
        // 本体と全く別の内容への差し替えを止める下限（字義一致は課さない。上のコメント参照）。
        // 下限は短い側の長さで頭打ちにする——短い要約（＝是正の目標形）を赤にしないため。
        const body = bodyTitles[id];
        if (body) {
          const need = Math.min(minOverlap, [...body].length, [...title].length);
          if (lcsLength(title, body) < need) push('title-drift');
        }
      });
      return violations;
    };

    const TITLE_GOOD = [
      '| IADR | タイトル | 状態 |',
      '| --- | --- | --- |',
      '| [IADR-0000](./IADR-0000_a.md) | 実装意思決定の記録方針 | Accepted |',
      '| [IADR-0001](./IADR-0001_b.md) | カタログの正本所有 | Superseded by IADR-0002 |',
    ].join('\n');

    ok('索引タイトル: 正常な索引は違反 0（正例）', () => {
      assert.deepStrictEqual(inspectAdrIndexTitles(TITLE_GOOD), []);
      // 索引行以外（見出し・ブロック引用・本文）を誤検出しない。
      assert.deepStrictEqual(
        inspectAdrIndexTitles('## 一覧\n> 採番に関する注記: IADR-0139 を採った\nただの本文 IADR-0001'),
        [],
      );
    });

    ok('索引タイトル: 空セルを検出する（変異試験）', () => {
      const mutated = TITLE_GOOD.replace('| 実装意思決定の記録方針 |', '|  |');
      assert.deepStrictEqual(inspectAdrIndexTitles(mutated), [
        { line: 3, id: 'IADR-0000', kind: 'title-missing' },
      ]);
    });

    ok('索引タイトル: 状態語の混入を検出する（状態列と二重持ちになる型・変異試験）', () => {
      const mutated = TITLE_GOOD.replace(
        '| カタログの正本所有 |',
        '| カタログの正本所有（Superseded by IADR-0002） |',
      );
      assert.deepStrictEqual(inspectAdrIndexTitles(mutated), [
        { line: 4, id: 'IADR-0001', kind: 'title-status-word' },
      ]);
    });

    ok('索引タイトル: ［YYYY-MM-DD 追記］ の混入を検出する（変異試験）', () => {
      const mutated = TITLE_GOOD.replace(
        '| 実装意思決定の記録方針 |',
        '| 実装意思決定の記録方針。［2026-08-07 追記 / #580］本体にも同じ追記がある |',
      );
      assert.deepStrictEqual(inspectAdrIndexTitles(mutated), [
        { line: 3, id: 'IADR-0000', kind: 'title-addendum' },
      ]);
    });

    // 本体 frontmatter の `title:`（先頭 ID を除いた部分）に相当する固定入力。
    const TITLE_BODIES = {
      'IADR-0000': '実装意思決定の記録方針',
      'IADR-0001': 'カタログの正本所有と DocumentNormalized の購読責務',
    };

    ok('索引タイトル: 本体と共有する文字が下限以上なら通す（要約は本体より短くてよい・正例）', () => {
      // IADR-0001 の索引セルは本体 title: の要約（9 字）で、本体の部分列になっている。
      // IADR-0000 は字義一致だが本体が 11 字しかない＝下限 12 に届かない。どちらも通ること。
      assert.deepStrictEqual(
        inspectAdrIndexTitles(TITLE_GOOD, { bodyTitles: TITLE_BODIES }),
        [],
      );
      // 本体を持たない ID（索引にあるが本体を読めない）は drift を見ない（#581 の担当範囲）。
      assert.deepStrictEqual(inspectAdrIndexTitles(TITLE_GOOD, { bodyTitles: {} }), []);
    });

    ok('索引タイトル: 200 字以内・清潔な文で別の決定へ書き換えると title-drift で落ちる（変異試験）', () => {
      // 監査（#580 再監査 M4）が使ったのと同じ型の変異。長さ上限にも追記にも状態語にも掛からない。
      const mutated = TITLE_GOOD.replace(
        '| 実装意思決定の記録方針 |',
        '| 既定モデルの選定は運用ポリシーで行い、コードには焼き込まない |',
      );
      assert.deepStrictEqual(inspectAdrIndexTitles(mutated, { bodyTitles: TITLE_BODIES }), [
        { line: 3, id: 'IADR-0000', kind: 'title-drift' },
      ]);
      // 下限（`minTitleOverlap`）を緩めればこの変異は通る＝閾値が効いていることの確認。
      assert.deepStrictEqual(
        inspectAdrIndexTitles(mutated, { bodyTitles: TITLE_BODIES, minOverlap: 0 }),
        [],
      );
    });

    ok('索引タイトル: 短くしても本体の部分列なら通り、無関係な短文は落ちる（下限の頭打ちの境界）', () => {
      // 下限は min(12, 本体長, タイトル長)。IADR-0001 の索引セルは 9 字（下限 12 未満）だが本体
      // title: の部分列なので通る＝要約へ縮める作業を妨げない（正例側は上のテストで固定済み）。
      // 同じ短さでも本体と文字を共有しない文字列なら落ちる（「短くすれば何でも通る」抜け道が無いこと）。
      const garbled = TITLE_GOOD.replace('| カタログの正本所有 |', '| 監視基盤の刷新 |');
      assert.deepStrictEqual(inspectAdrIndexTitles(garbled, { bodyTitles: TITLE_BODIES }), [
        { line: 4, id: 'IADR-0001', kind: 'title-drift' },
      ]);
    });

    ok('索引タイトル: 閾値は baseline に据え置く（緩める変更は必ず diff に載る）', () => {
      // 閾値を JSON 側でこっそり緩める（上限を上げる / 下限を下げる）と検査が骨抜きになるため、
      // 値そのものを固定する。変えるなら本テストも同じ PR で直すことになり、レビューの目に入る。
      assert.strictEqual(titleBaseline.maxTitleChars, 200, 'maxTitleChars を緩めるなら根拠を PR に書くこと');
      assert.strictEqual(titleBaseline.minTitleOverlap, 12, 'minTitleOverlap を緩めるなら根拠を PR に書くこと');
      // 緩めた場合に何が素通りするかを固定する（上限 400 なら 201 字の貼り付けが通る）。
      const pasted = TITLE_GOOD.replace('| 実装意思決定の記録方針 |', `| ${'あ'.repeat(201)} |`);
      assert.deepStrictEqual(inspectAdrIndexTitles(pasted, { maxChars: 400 }), []);
    });

    ok('索引タイトル: 長さ上限の超過を検出する（決定文の貼り付け・変異試験）', () => {
      const mutated = TITLE_GOOD.replace('| 実装意思決定の記録方針 |', `| ${'あ'.repeat(201)} |`);
      assert.deepStrictEqual(inspectAdrIndexTitles(mutated), [
        { line: 3, id: 'IADR-0000', kind: 'title-too-long' },
      ]);
      // 上限ちょうどは通す（境界の取り違えを固定する）。
      assert.deepStrictEqual(
        inspectAdrIndexTitles(TITLE_GOOD.replace('| 実装意思決定の記録方針 |', `| ${'あ'.repeat(200)} |`)),
        [],
      );
    });

    ok('索引タイトル: 本リポの .ai-context/adr/README.md が baseline を超えていない（実データ・ラチェット）', () => {
      const adrDir = path.join(__dirname, '..', '.ai-context', 'adr');
      const md = fs.readFileSync(path.join(adrDir, 'README.md'), 'utf8');
      // 本体 frontmatter の `title:` を ID → 文字列で集める（先頭の ID 接頭辞は落とす）。
      const bodyFiles = fs.readdirSync(adrDir).filter((f) => /^IADR-\d{4}_.*\.md$/.test(f));
      const bodyTitles = {};
      for (const f of bodyFiles) {
        const id = f.slice(0, 9);
        const raw = (fs.readFileSync(path.join(adrDir, f), 'utf8').match(/^title:\s*(.+)$/m) || [])[1];
        if (raw) bodyTitles[id] = raw.trim().replace(new RegExp(`^${id}[:：]?\\s*`), '');
      }
      const actual = inspectAdrIndexTitles(md, { bodyTitles });

      // 走査 0 件で緑になる fail-open を塞ぐ（baseline に行がある限り違反も出るはず）。
      assert.ok(
        Object.keys(titleBaseline.rows).length === 0 || actual.length > 0,
        'baseline に残件があるのに違反 0（走査が壊れている）',
      );
      // **行を索引から隠す**変異（先頭に空白を入れて `^|` アンカーから外す）は違反数が減るだけで
      // 上のガードを通り抜ける。行数の下限を本ブロック自身に持たせて塞ぐ。
      //
      // **［2026-08-08 追記 / フェーズ末クロス監査 G-2］隣の「全行リンク形式」ブロックは #581（PR #606）で
      // `check-adr-numbering.js` の判定 6 へ統合され、既に消えている。** よってこの下限は
      // 「二重に持つ」ものではなく**本ラチェット唯一の fail-open 塞ぎ**である。
      // [[IADR-0144]] 決定 6 の「fail-open 下限も同時に消える」は**この行を見落としており不正確**だった
      // （あちらが消したのは削除済みブロック側の下限であって、本ブロックのものではない）。
      //
      // 索引行の抽出は `check-adr-numbering.js` の `INDEX_LINE_RE` を**借りる**（同 決定 5）。
      // 同じ式をリテラルで複製すると、片方だけ直したとき挙動が黙って割れる。
      const indexRows = md.split('\n').filter((l) => INDEX_LINE_RE_SHARED.test(l));
      assert.ok(bodyFiles.length > 0, '.ai-context/adr/ に ADR 本体が 1 件も見つからない（走査が壊れている）');
      assert.ok(
        indexRows.length >= bodyFiles.length,
        `索引行 ${indexRows.length} 件に対し ADR 本体は ${bodyFiles.length} 件（索引行が隠されているか走査が壊れている）`,
      );
      assert.ok(
        Object.keys(bodyTitles).length >= bodyFiles.length,
        `本体 title: を読めた ADR は ${Object.keys(bodyTitles).length} 件（本体 ${bodyFiles.length} 件。frontmatter の欠落か走査の破損）`,
      );

      const seen = new Map(); // id -> Set(kind)
      for (const v of actual) {
        if (!seen.has(v.id)) seen.set(v.id, new Set());
        seen.get(v.id).add(v.kind);
      }
      const added = actual.filter((v) => !(titleBaseline.rows[v.id] || []).includes(v.kind));
      assert.deepStrictEqual(
        added,
        [],
        '索引タイトルセルに新しい違反が入った（baseline へ足さず、タイトルを本体 title: の要約へ縮めること）:\n' +
          added.map((x) => `  README.md:${x.line} ${x.id} ${x.kind}`).join('\n'),
      );

      const stale = [];
      for (const [id, kinds] of Object.entries(titleBaseline.rows)) {
        for (const kind of kinds) if (!(seen.get(id) || new Set()).has(kind)) stale.push(`${id} ${kind}`);
      }
      assert.deepStrictEqual(
        stale,
        [],
        `直っているのに baseline に残っている（scripts/adr-index-title-baseline.json から削除する）:\n  ${stale.join('\n  ')}`,
      );
    });
  }

  // --- #664: 0 件走査で緑を返さない（fail-closed の門） -------------------------
  //
  // #592 の初版は `walk()` へ絶対パスを渡し、`catch` が黙って空配列を返していた。
  // 必須仕様書を 1 件も読まないまま「違反 0 件」と報告し、**変異試験 M1 が落ちるべきなのに
  // 通ってしまった**。門だけでは「門をすり抜ける経路」が残るため、**実データでの下限**
  // （走査 1 件以上）を自己試験として常設する。
  {
    const { spawnSync } = require('child_process');
    const path = require('path');
    const SCRIPTS = __dirname;

    /** 検査器を実データで走らせ、stdout ＋ stderr を返す。 */
    const run = (name, args = []) => {
      const r = spawnSync(process.execPath, [path.join(SCRIPTS, name), ...args], {
        encoding: 'utf8',
        cwd: path.join(SCRIPTS, '..'),
      });
      return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
    };

    // 走査件数が OK メッセージに出ることを、**数字を書かずに**確かめる。
    // 件数をリテラルで書くと契約が増えるたびに落ちる（#558 の教訓）。
    const scannedAtLeastOne = [
      ['check-doc-links.js', /OK: (\d+) 件の Markdown/],
      // ★ #583 で走査対象が `.md` 外へ広がり、OK メッセージの文言が変わった
      //   （「N 件の Markdown」→「N 件に」）。**兄弟の取り残しを作らない。**
      ['check-cross-repo-refs.js', /OK: (\d+) 件に/],
      ['check-plan-id-qualification.js', /OK: (\d+) 件に/],
      ['check-unit-dependencies.js', /OK: csproj (\d+) 件/],
    ];
    for (const [name, re] of scannedAtLeastOne) {
      ok(`0 件走査の門: ${name} は実データで 1 件以上を走査する（下限）`, () => {
        const { code, out } = run(name);
        assert.strictEqual(code, 0, `${name} が実データで落ちた:\n${out}`);
        const m = out.match(re);
        assert.ok(m, `${name} の OK メッセージから走査件数を読めない（門の下限を検査できない）:\n${out}`);
        assert.ok(
          Number(m[1]) > 0,
          `${name} の走査件数が 0 だった。0 件検査は「検査しているつもりで何も見ていない」状態である`,
        );
      });
    }

    ok('0 件走査の門: check-openapi-dto-drift は C# record を 1 件以上拾う（下限）', () => {
      const { code, out } = run('check-openapi-dto-drift.js');
      assert.strictEqual(code, 0, `実データで落ちた:\n${out}`);
      const m = out.match(/C# record (\d+)/);
      assert.ok(m, `OK メッセージから C# record 数を読めない:\n${out}`);
      assert.ok(Number(m[1]) > 0, 'C# record が 0 件だった（DTO_ROOTS のパスずれを疑う）');
    });

    // **門そのものが効いていることの側**（変異試験）。走査結果を空にすると fail する。
    // ★ **門が効いていることの側**（変異試験）を、手元の 1 回で終わらせず自動回帰にする。
    //
    // 上の下限テストは**実データ（走査件数 > 0）**に対して走るため、**門の分岐を 1 度も通らない**。
    // 門を消しても下限テストは緑のままであり、**「検査しているつもりで何も見ていない」を防ぐ
    // 本 PR が、自分の追加した試験で同じ穴を作る**ことになる（#672 のレビュー指摘）。
    //
    // 装置: **走査ルートが存在しない一時リポジトリ**へ `scripts/` を写し、そこで走らせる。
    // 検査器は `__dirname/..` を REPO_ROOT にするため、**cwd を変えるだけでは足りない**。
    {
      const fs = require('fs');
      const os = require('os');

      /** scripts/ だけを持つ一時リポジトリを作る（走査ルートは存在しない）。 */
      const makeEmptyRepo = () => {
        const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'empty-scan-'));
        fs.cpSync(SCRIPTS, path.join(dir, 'scripts'), { recursive: true });
        const pkg = path.join(SCRIPTS, '..', 'package.json');
        if (fs.existsSync(pkg)) fs.copyFileSync(pkg, path.join(dir, 'package.json'));
        spawnSync('git', ['init', '-q'], { cwd: dir });
        spawnSync('git', ['commit', '-q', '--allow-empty', '-m', 'init'], { cwd: dir });
        return dir;
      };

      const runIn = (dir, name) => {
        const r = spawnSync(process.execPath, [path.join(dir, 'scripts', name)], {
          encoding: 'utf8',
          cwd: dir,
        });
        return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
      };

      const gated = [
        'check-doc-links.js',
        'check-cross-repo-refs.js',
        'check-plan-id-qualification.js',
        'check-unit-dependencies.js',
      ];
      for (const name of gated) {
        ok(`0 件走査の門: ${name} は走査ルートが無いと fail する（変異試験）`, () => {
          const dir = makeEmptyRepo();
          try {
            const { code, out } = runIn(dir, name);
            assert.strictEqual(
              code,
              1,
              `${name} が 0 件走査で緑を返した。門が消えている:\n${out}`,
            );
            assert.match(out, /0 件検査/, `${name} が 0 件検査であることを述べていない:\n${out}`);
          } finally {
            fs.rmSync(dir, { recursive: true, force: true });
          }
        });
      }

      // `check-openapi-dto-drift` は空リポジトリでは OPENAPI が読めず例外で落ちるため、
      // **上の装置では門を通らない**。契約だけを置いて DTO を 1 件も置かない状態を作る。
      ok('0 件走査の門: check-openapi-dto-drift は DTO を拾えないと fail する（変異試験）', () => {
        const dir = makeEmptyRepo();
        try {
          fs.mkdirSync(path.join(dir, 'docs/api'), { recursive: true });
          fs.writeFileSync(
            path.join(dir, 'docs/api/openapi.yaml'),
            'openapi: 3.0.3\ncomponents:\n  schemas:\n    FooDto:\n      type: object\n      properties:\n        a:\n          type: string\n',
          );
          const { code, out } = runIn(dir, 'check-openapi-dto-drift.js');
          assert.strictEqual(code, 1, `DTO 0 件で緑を返した。門が消えている:\n${out}`);
          assert.match(out, /0 件検査/, `0 件検査であることを述べていない:\n${out}`);
        } finally {
          fs.rmSync(dir, { recursive: true, force: true });
        }
      });
    }

    ok('0 件走査の門: findDrift は records が空でも [] を返す（門が main 側に要る根拠）', () => {
      const d = require('./check-openapi-dto-drift.js');
      const r = d.findDrift({ Foo: { props: ['a'], required: [] } }, {}, { entries: [] }, new Set());
      assert.deepStrictEqual(
        r,
        [],
        'findDrift は 0 件走査を違反 0 件として返す。**だから main() 側に門が要る**',
      );
    });
  }

  // --- #665: Grafana 内蔵アラート（暫定の一次検知）の provisioning ------------------
  //
  // **この検査器は「Grafana が受理するか」を見ていない**（実装環境で Grafana を起動できない。
  // #665 の作業仕様書 §判断 0）。見ているのは provisioning YAML の内部整合だけである。
  // **だからこそ、その狭い検出力が本当に働いていることを自動回帰にする。**
  {
    const { spawnSync } = require('child_process');
    const path = require('path');
    const fs = require('fs');
    const SCRIPTS = __dirname;
    const REPO = path.join(SCRIPTS, '..');
    const script = path.join(SCRIPTS, 'check-grafana-alerting.js');

    const runGrafana = (args = [], cwd = REPO) => {
      const r = spawnSync(process.execPath, [script, ...args], { encoding: 'utf8', cwd });
      return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
    };

    ok('check-grafana-alerting --self-test が通る', () => {
      const { code, out } = runGrafana(['--self-test']);
      assert.strictEqual(code, 0, out);
    });

    // ★ **self-test の件数だけを見ない。** 件数は変異ケースを消しても通りつづける
    //   （#657 で実際にやった誤り）。**個々の変異ケースが走っていることを名指しで確かめる。**
    ok('check-grafana-alerting: self-test が 5 種の変異ケースを実際に走らせている', () => {
      const { out } = runGrafana(['--self-test']);
      for (const name of [
        'Prometheus にだけあるルールを検出する',
        'Grafana にだけあるルールを検出する',
        '宣言されていない datasourceUid を検出する',
        'compose と k8s の乖離を検出する',
        '必須キーの欠落を検出する',
      ]) {
        assert.ok(out.includes(name), `self-test から変異ケース「${name}」が消えている:\n${out}`);
      }
    });

    ok('check-grafana-alerting が実データで違反 0 件', () => {
      const { code, out } = runGrafana();
      assert.strictEqual(code, 0, out);
    });

    // #664 / IADR-0130 の下限。**件数リテラルは書かない**（ルールが増えれば動く。#558 の教訓）。
    ok('0 件走査の門: check-grafana-alerting は実データでルールを 1 件以上拾う（下限）', () => {
      const { code, out } = runGrafana();
      assert.strictEqual(code, 0, out);
      const m = out.match(/Prometheus (\d+) 件 \/ Grafana (\d+) 件/);
      assert.ok(m, `OK メッセージから走査件数を読めない:\n${out}`);
      assert.ok(Number(m[1]) > 0 && Number(m[2]) > 0, `走査件数が 0 だった:\n${out}`);
    });

    // ★ **変異試験は実データに対しても当てる。** フィクスチャだけだと、
    //   「実ファイルの書式が正規表現に合っていない」型の空振り（#664 の枝番行・
    //   #665 の `^  - alert:` 決め打ち）を捕まえられない。
    ok('check-grafana-alerting: 実データのルールを 1 件消すと違反を出す（変異試験）', () => {
      const g = require('./check-grafana-alerting.js');
      const read = (p) => fs.readFileSync(path.join(REPO, p), 'utf8');
      const prom = read('deploy/prometheus/alerts.yml');
      const grafana = read('deploy/grafana/provisioning/alerting/slo-alerts.yaml');
      const datasources = read('deploy/grafana/provisioning/datasources/datasources.yaml');
      const k8sInline = g.extractK8sInline(read('deploy/local/observability/grafana.yaml'));

      const clean = g.findIssues({ prom, grafana, datasources, k8sInline });
      assert.deepStrictEqual(clean.issues, [], `実データが既に違反を持っている:\n${clean.issues.join('\n')}`);

      // 実データの Grafana 側から**先頭のルール名だけ**を書き換える（Prometheus 側と食い違わせる）。
      const first = g.promAlertNames(prom)[0];
      assert.ok(first, 'Prometheus のルール名を 1 件も拾えなかった（正規表現が実書式に合っていない）');
      const mutated = grafana.replace(`title: ${first}`, `title: ${first}Renamed`);
      assert.notStrictEqual(mutated, grafana, `変異が当たっていない（title: ${first} が実ファイルに無い）`);
      const r = g.findIssues({ prom, grafana: mutated, datasources, k8sInline });
      assert.ok(
        r.issues.some((x) => x.includes(`Grafana に無いルール: ${first}`)),
        `実データの変異を検出できなかった:\n${JSON.stringify(r.issues)}`,
      );
    });

    ok('check-grafana-alerting: 実データの datasource 宣言を消すと違反を出す（変異試験）', () => {
      const g = require('./check-grafana-alerting.js');
      const read = (p) => fs.readFileSync(path.join(REPO, p), 'utf8');
      const grafana = read('deploy/grafana/provisioning/alerting/slo-alerts.yaml');
      const r = g.findIssues({
        prom: read('deploy/prometheus/alerts.yml'),
        grafana,
        datasources: 'apiVersion: 1\ndatasources: []\n', // uid 宣言を全部落とす
        k8sInline: g.extractK8sInline(read('deploy/local/observability/grafana.yaml')),
      });
      assert.ok(
        r.issues.some((x) => x.includes('datasourceUid') && x.includes('宣言されていない')),
        `datasource 宣言の欠落を検出できなかった（軸 3 の再発防止が効いていない）:\n${JSON.stringify(r.issues)}`,
      );
    });

    // ★ 門が効いていることの側（#672 のレビュー指摘。手元の 1 回で終わらせない）。
    //
    // **門は 2 つある。1 つの変異で両方を確かめたつもりにならない**（メタ変異試験で実測した）:
    //   門 A: 4 つの対象ファイルのいずれかが読めない
    //   門 B: ファイルは読めるが**ルールを 1 件も拾えない**（正規表現が実書式に合っていない型）
    // **門 B を消しても門 A の試験は緑のまま通る。** だから 2 本に分ける。
    const makeRepoWith = (files) => {
      const os = require('os');
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'grafana-alerting-'));
      fs.cpSync(SCRIPTS, path.join(dir, 'scripts'), { recursive: true });
      for (const [rel, body] of Object.entries(files)) {
        const abs = path.join(dir, rel);
        fs.mkdirSync(path.dirname(abs), { recursive: true });
        fs.writeFileSync(abs, body);
      }
      return dir;
    };
    const runInRepo = (dir) => {
      const r = spawnSync(process.execPath, [path.join(dir, 'scripts', 'check-grafana-alerting.js')], {
        encoding: 'utf8',
        cwd: dir,
      });
      return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
    };

    ok('0 件走査の門 A: check-grafana-alerting は対象ファイルが無いと fail する（変異試験）', () => {
      const dir = makeRepoWith({});
      try {
        const { code, out } = runInRepo(dir);
        assert.strictEqual(code, 1, `対象ファイルが無いのに緑を返した。門が消えている:\n${out}`);
        assert.match(out, /0 件検査/, `0 件検査であることを述べていない:\n${out}`);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    ok('0 件走査の門 B: 4 ファイルが揃っていてもルール 0 件なら fail する（変異試験）', () => {
      // **ファイルはすべて読める**（門 A は通過する）。中身にルールが 1 件も無い状態を作る。
      // これは「`^  - alert:` の決め打ちで実書式を拾えなかった」型の事故そのものである
      // （#665 の作業仕様書 §軸 2 で実際にやった誤り）。
      // ★ フィクスチャは**ルール 0 件以外はすべて健全**にする。他の違反を踏ませると、
      //   門 B を消しても「別の理由で exit 1」になり、**門 B の試験が空振りする**
      //   （最初に書いた `groups: []` がまさにこれで、必須キー検査に引っかかっていた）。
      const dir = makeRepoWith({
        'deploy/prometheus/alerts.yml': 'groups:\n',
        'deploy/grafana/provisioning/alerting/slo-alerts.yaml': 'apiVersion: 1\ngroups:\n',
        'deploy/grafana/provisioning/datasources/datasources.yaml': 'apiVersion: 1\ndatasources:\n',
        'deploy/local/observability/grafana.yaml':
          'data:\n  slo-alerts.yaml: |\n    apiVersion: 1\n    groups:\n',
      });
      try {
        const { code, out } = runInRepo(dir);
        assert.strictEqual(code, 1, `ルール 0 件で緑を返した。門 B が消えている:\n${out}`);
        assert.match(out, /0 件検査/, `0 件検査であることを述べていない:\n${out}`);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });
  }

  // --- #667: 仕様書 status の値域 ------------------------------------------------
  //
  // 規約（docs/README.md 運用ルール 6）は最初から 5 値を定義していたが、**誰も追随していなかった**。
  // 実測で 37 件が語彙外で、うち 16 件は**計画リポの語彙（`fixed`）を持ち込んだ自分の仕様書**だった。
  // **規約を書き足すのではなく、既にある規約を機械で閉じる。**
  {
    const { spawnSync } = require('child_process');
    const path = require('path');
    const fs = require('fs');
    const SCRIPTS = __dirname;
    const REPO = path.join(SCRIPTS, '..');
    const vocab = require('./check-doc-status-vocabulary.js');

    const runVocab = (args = []) => {
      const r = spawnSync(process.execPath, [path.join(SCRIPTS, 'check-doc-status-vocabulary.js'), ...args], {
        encoding: 'utf8',
        cwd: REPO,
      });
      return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
    };

    ok('check-doc-status-vocabulary --self-test が通る', () => {
      const { code, out } = runVocab(['--self-test']);
      assert.strictEqual(code, 0, out);
    });

    // 件数だけを見ると変異ケースを消しても通る（#657 の誤り）。**ケース名で確かめる。**
    ok('check-doc-status-vocabulary: self-test が主要な変異ケースを実際に走らせている', () => {
      const { out } = runVocab(['--self-test']);
      for (const name of [
        '値域外の語を検出する',
        'ADR に仕様書の語を書くと検出する',
        '仕様書に ADR の語を書くと検出する',
        '大小文字だけ違う語を素通ししない',
        '本文中の status: を frontmatter と取り違えない',
        '据え置きが上限を超えると落ちる',
      ]) {
        assert.ok(out.includes(name), `self-test から変異ケース「${name}」が消えている:\n${out}`);
      }
    });

    ok('check-doc-status-vocabulary が実データで違反 0 件', () => {
      const { code, out } = runVocab();
      assert.strictEqual(code, 0, out);
    });

    // #664 / IADR-0130 の下限。**件数リテラルは書かない**（文書が増えれば動く）。
    ok('0 件走査の門: check-doc-status-vocabulary は実データで 1 件以上を走査する（下限）', () => {
      const { code, out } = runVocab();
      assert.strictEqual(code, 0, out);
      const m = out.match(/OK: (\d+) 件の仕様書/);
      assert.ok(m, `OK メッセージから走査件数を読めない:\n${out}`);
      assert.ok(Number(m[1]) > 0, `走査件数が 0 だった:\n${out}`);
    });

    // ★ 変異試験は**実データにも当てる**。フィクスチャだけだと「実ファイルの frontmatter が
    //   想定の形でない」型の空振り（#665 で実際にやった）を捕まえられない。
    ok('check-doc-status-vocabulary: 実データの 1 件を語彙外へ変えると検出する（変異試験）', () => {
      const docs = [];
      const walk = (dir) => {
        for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
          const p = path.join(dir, e.name);
          if (e.isDirectory()) walk(p);
          else if (e.name.endsWith('.md'))
            docs.push({
              relPath: path.relative(REPO, p).split(path.sep).join('/'),
              text: fs.readFileSync(p, 'utf8'),
            });
        }
      };
      walk(path.join(REPO, 'docs'));
      walk(path.join(REPO, '.ai-context'));

      const clean = vocab.findIssues(docs);
      assert.deepStrictEqual(clean.violations, [], `実データが既に違反を持っている:\n${JSON.stringify(clean.violations)}`);
      assert.ok(clean.scanned > 0, 'frontmatter を 1 件も拾えていない（形が想定と違う）');

      // 実ファイルのうち .ai-context/specs の 1 件だけを語彙外へ変異させる。
      const target = docs.find((d) => d.relPath.startsWith('.ai-context/specs/') && /^status: done$/m.test(vocab.frontMatter(d.text) || ''));
      assert.ok(target, '実データに status: done の作業仕様書が 1 件も無い（前提が崩れている）');
      const mutated = docs.map((d) =>
        d === target ? { ...d, text: d.text.replace(/^status: done$/m, 'status: bogus-value') } : d,
      );
      const r = vocab.findIssues(mutated);
      assert.ok(
        r.violations.some((v) => v.file === target.relPath && v.status === 'bogus-value'),
        `実データの変異を検出できなかった:\n${JSON.stringify(r.violations)}`,
      );
    });

    // ★ 門は 2 つある（#665 の教訓。1 つの変異で両方を確かめたつもりにならない）。
    //   門 A: 走査 0 件 ／ 門 B: 据え置きのラチェット
    ok('0 件走査の門 A: docs/ が無いと fail する（変異試験）', () => {
      const os = require('os');
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'status-vocab-'));
      try {
        // ★ **写した側のスクリプトを叩く。** 検査器は `__dirname/..` を REPO_ROOT にするため、
        //   cwd を変えるだけでは実リポジトリの docs/ を読んでしまい、変異が当たらない
        //   （最初にそう書いて、門 A を消していないのに「緑を返した」と落ちた）。
        fs.cpSync(SCRIPTS, path.join(dir, 'scripts'), { recursive: true });
        const r = spawnSync(process.execPath, [path.join(dir, 'scripts', 'check-doc-status-vocabulary.js')], {
          encoding: 'utf8',
          cwd: dir,
        });
        const code = r.status;
        const out = `${r.stdout || ''}${r.stderr || ''}`;
        assert.strictEqual(code, 1, `docs/ が無いのに緑を返した。門 A が消えている:\n${out}`);
        assert.match(out, /0 件検査/, `0 件検査であることを述べていない:\n${out}`);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    });

    ok('門 B: 据え置きが 1 件でも増えると fail する（ラチェット・変異試験）', () => {
      // **門 A は通る**（走査件数は 0 でない）状態で、据え置きだけを超過させる。
      // 門 A と同じ変異で確かめると、門 B が消えても気づけない。
      assert.deepStrictEqual(vocab.ratchetViolations({ ...vocab.BASELINE }), []);
      for (const key of Object.keys(vocab.BASELINE)) {
        const over = vocab.ratchetViolations({ ...vocab.BASELINE, [key]: vocab.BASELINE[key] + 1 });
        assert.strictEqual(over.length, 1, `据え置き "${key}" の超過を検出できない: ${JSON.stringify(over)}`);
      }
    });

    // 据え置きの件数は**実データと一致していること**。ずれたまま放置すると
    // 「据え置きを許しているつもりで、実は上限に余裕がある」状態になる。
    ok('門 B: 据え置きの上限が実データの件数と一致している（余裕を残さない）', () => {
      const { out } = runVocab();
      const m = out.match(/据え置き: review (\d+) \/ \.ai-context\/specs の completed (\d+)/);
      assert.ok(m, `OK メッセージから据え置き件数を読めない:\n${out}`);
      assert.strictEqual(Number(m[1]), vocab.BASELINE.review, 'review の据え置きが実データとずれている');
      assert.strictEqual(
        Number(m[2]),
        vocab.BASELINE['specs-completed'],
        '.ai-context/specs の completed の据え置きが実データとずれている',
      );
    });
  }

  // --- #675: 仕様書 type の値域（テンプレートが正本） -------------------------------
  //
  // ★ 再発防止の軸を 1 度取り違えた（IADR-0167 決定 6）。初版は「2 枚のテンプレートが同じ type を
  //   書いていないか」を見ていたが、欠陥は「1 枚を 2 種別が共用している」形であり、
  //   是正前の状態に当てても素通りした。**直したはずのものを捕まえるかを確かめる。**
  {
    const { spawnSync } = require('child_process');
    const path = require('path');
    const fs = require('fs');
    const SCRIPTS = __dirname;
    const REPO = path.join(SCRIPTS, '..');
    const tv = require('./check-doc-type-vocabulary.js');

    const runType = (args = []) => {
      const r = spawnSync(process.execPath, [path.join(SCRIPTS, 'check-doc-type-vocabulary.js'), ...args], {
        encoding: 'utf8',
        cwd: REPO,
      });
      return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
    };

    ok('check-doc-type-vocabulary --self-test が通る', () => {
      const { code, out } = runType(['--self-test']);
      assert.strictEqual(code, 0, out);
    });

    ok('check-doc-type-vocabulary: self-test が主要な変異ケースを実際に走らせている', () => {
      const { out } = runType(['--self-test']);
      for (const name of [
        'テンプレートが書かない type を検出する',
        '1 枚のテンプレートを 2 種別が共用していたら衝突として検出する',
        '別テンプレートへ分ければ衝突しない',
        '本文中の type: を frontmatter と取り違えない',
        '据え置きが上限を超えると落ちる',
      ]) {
        assert.ok(out.includes(name), `self-test から変異ケース「${name}」が消えている:\n${out}`);
      }
    });

    ok('check-doc-type-vocabulary が実データで違反 0 件', () => {
      const { code, out } = runType();
      assert.strictEqual(code, 0, out);
    });

    ok('0 件走査の門: check-doc-type-vocabulary は実データで 1 件以上を走査する（下限）', () => {
      const { code, out } = runType();
      assert.strictEqual(code, 0, out);
      const m = out.match(/OK: (\d+) 件の文書/);
      assert.ok(m, `OK メッセージから走査件数を読めない:\n${out}`);
      assert.ok(Number(m[1]) > 0, `走査件数が 0 だった:\n${out}`);
    });

    // ★★ #675 の欠陥そのものを実データで再現して当てる。
    //   フィクスチャだけだと「実ファイルの表の書式が正規表現に合っていない」型の空振りを捕まえられない。
    ok('check-doc-type-vocabulary: 実データの種別表を是正前へ戻すと衝突を検出する（変異試験）', () => {
      const cmd = fs.readFileSync(path.join(REPO, '.claude/commands/new-spec.md'), 'utf8');
      const kinds = tv.parseKindTable(cmd);
      assert.ok(kinds.length > 0, '実データの種別表を 1 行も読めない（表の書式が変わった）');

      const templates = fs
        .readdirSync(path.join(REPO, 'docs/templates'))
        .filter((n) => n.endsWith('.md'))
        .map((n) => ({ name: n, text: fs.readFileSync(path.join(REPO, 'docs/templates', n), 'utf8') }));
      const { typeOfTemplate } = tv.buildVocabulary(templates);

      assert.deepStrictEqual(tv.kindCollisions(kinds, typeOfTemplate), [], '実データが既に衝突している');

      // 是正前の形: runbook / how-to を共用テンプレへ差し戻す。
      const before = kinds.map((k) =>
        k.kind === 'runbook'
          ? { ...k, template: 'operations_spec_template.md' }
          : k.kind === 'how-to'
            ? { ...k, template: 'spec_template.md' }
            : k,
      );
      const c = tv.kindCollisions(before, typeOfTemplate);
      const types = c.map((x) => x.type).sort();
      assert.deepStrictEqual(
        types,
        ['operations-spec', 'spec'],
        `是正前の衝突 2 件を検出できなかった: ${JSON.stringify(c)}`,
      );
    });

    // ★ 門は 3 つある。別々に確かめる（IADR-0167 §結果）。
    const makeRepo = (files) => {
      const os = require('os');
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'type-vocab-'));
      fs.cpSync(SCRIPTS, path.join(dir, 'scripts'), { recursive: true });
      for (const [rel, body] of Object.entries(files)) {
        const abs = path.join(dir, rel);
        fs.mkdirSync(path.dirname(abs), { recursive: true });
        fs.writeFileSync(abs, body);
      }
      // ★ **写した側のスクリプトを叩く。** cwd を変えるだけでは REPO_ROOT が実リポジトリのままになる。
      const r = spawnSync(process.execPath, [path.join(dir, 'scripts', 'check-doc-type-vocabulary.js')], {
        encoding: 'utf8',
        cwd: dir,
      });
      const out = `${r.stdout || ''}${r.stderr || ''}`;
      fs.rmSync(dir, { recursive: true, force: true });
      return { code: r.status, out };
    };
    const KIND_TABLE = '| `work` | 作業仕様書 | `spec_template.md` | `.ai-context/specs/` | 単位 |\n';
    const TPL = '---\ntype: spec\n---\n';

    ok('門 A: docs/templates から type を読めないと fail する（変異試験）', () => {
      const { code, out } = makeRepo({
        '.ai-context/specs/a.md': '---\ntype: spec\n---\n',
        '.claude/commands/new-spec.md': KIND_TABLE,
      });
      assert.strictEqual(code, 1, `テンプレが無いのに緑を返した。門 A が消えている:\n${out}`);
      assert.match(out, /0 件検査/, out);
    });

    ok('門 B: テンプレはあるが type を持つ文書が 0 件なら fail する（変異試験）', () => {
      const { code, out } = makeRepo({
        'docs/templates/spec_template.md': TPL,
        '.claude/commands/new-spec.md': KIND_TABLE,
      });
      assert.strictEqual(code, 1, `走査 0 件で緑を返した。門 B が消えている:\n${out}`);
      assert.match(out, /0 件検査/, out);
    });

    ok('門 C: new-spec.md の種別表を 1 行も読めないと fail する（変異試験）', () => {
      const { code, out } = makeRepo({
        'docs/templates/spec_template.md': TPL,
        '.ai-context/specs/a.md': '---\ntype: spec\n---\n',
        '.claude/commands/new-spec.md': '表の書式が変わった\n',
      });
      assert.strictEqual(code, 1, `種別表 0 行で緑を返した。門 C が消えている:\n${out}`);
      assert.match(out, /0 件検査/, out);
    });
  }

  // --- #674: Grafana provisioning の経路間パリティ ---------------------------------
  //
  // ★ #665 の check-grafana-alerting.js は `alerting/` だけを突合していた。だから
  //   `dashboards/` が k8s に丸ごと無いことを誰も見ていなかった —— その中の llm-usage.json は
  //   #546（IADR-0164）の月次確認 Runbook が指す行き先である。射程が狭かった。
  {
    const { spawnSync, execFileSync } = require('child_process');
    const path = require('path');
    const SCRIPTS = __dirname;
    const REPO = path.join(SCRIPTS, '..');
    const parity = require('./check-grafana-provisioning-parity.js');

    const runParity = (args = []) => {
      const r = spawnSync(
        process.execPath,
        [path.join(SCRIPTS, 'check-grafana-provisioning-parity.js'), ...args],
        { encoding: 'utf8', cwd: REPO },
      );
      return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
    };

    ok('check-grafana-provisioning-parity --self-test が通る', () => {
      const { code, out } = runParity(['--self-test']);
      assert.strictEqual(code, 0, out);
    });

    ok('check-grafana-provisioning-parity: self-test が主要な変異ケースを実際に走らせている', () => {
      const { out } = runParity(['--self-test']);
      for (const name of [
        'k8s に無いファイルを検出する',
        'ConfigMap はあるがマウントされていない形を検出する',
        '内容の乖離を検出する',
        'JSON の中身が違えば検出する',
        'k8s にだけある inline を検出する',
        'k8s の ConfigMap に同名がある形を検出する',
        'compose 側に同名がある形も検出する',
        '同名があるときは「同内容でない」と断定しない',
      ]) {
        assert.ok(out.includes(name), `self-test から変異ケース「${name}」が消えている:\n${out}`);
      }
    });

    ok('check-grafana-provisioning-parity が実データで違反 0 件', () => {
      const { code, out } = runParity();
      assert.strictEqual(code, 0, out);
    });

    ok('0 件走査の門: check-grafana-provisioning-parity は実データで 1 件以上を突合する（下限）', () => {
      const { code, out } = runParity();
      assert.strictEqual(code, 0, out);
      const m = out.match(/compose (\d+) 件 \/ k8s inline (\d+) 件/);
      assert.ok(m, `OK メッセージから件数を読めない:\n${out}`);
      assert.ok(Number(m[1]) > 0 && Number(m[2]) > 0, `突合件数が 0 だった:\n${out}`);
    });

    // ★★ 「直したものを捕まえるか」を実データで確かめる（IADR-0167 決定 6 の教訓）。
    //   develop 時点の k8s マニフェストを取り出して当て、4 件の乖離が出ることを見る。
    //   ここが空振りだと、#674 が直した欠陥の再発を止められない。
    ok('check-grafana-provisioning-parity: develop 時点の k8s へ当てると乖離を検出する（変異試験）', () => {
      let before;
      try {
        before = execFileSync('git', ['show', 'origin/develop:deploy/local/observability/grafana.yaml'], {
          encoding: 'utf8',
          cwd: REPO,
        });
      } catch {
        return; // origin/develop が無い環境（浅いクローン等）ではこの試験を飛ばす
      }
      // develop に既に是正が入っている（＝本 PR がマージ済み）なら、この試験の前提が消える。
      const inlineBefore = parity.extractInlineFiles(before);
      if (inlineBefore.has('llm-usage.json')) return;

      const compose = parity.collectCompose(path.join(REPO, 'deploy/grafana/provisioning'));
      const r = parity.findIssues({
        compose,
        inline: inlineBefore,
        mounts: parity.mountedPaths(before),
      });
      assert.ok(
        r.issues.some((x) => x.includes('llm-usage.json') && x.includes('経路 B に存在しない')),
        `develop の欠落を検出できなかった: ${JSON.stringify(r.issues)}`,
      );
      assert.ok(
        r.issues.some((x) => x.includes('datasources.yaml') && x.includes('同内容でない')),
        `develop の datasource 乖離を検出できなかった: ${JSON.stringify(r.issues)}`,
      );
    });

    // 門は 2 つある（compose 側 0 件 / k8s 側 0 件）。別々に確かめる。
    const fsP = require('fs');
    const makeRepo = (files) => {
      const os = require('os');
      const dir = fsP.mkdtempSync(path.join(os.tmpdir(), 'grafana-parity-'));
      fsP.cpSync(SCRIPTS, path.join(dir, 'scripts'), { recursive: true });
      for (const [rel, body] of Object.entries(files)) {
        const abs = path.join(dir, rel);
        fsP.mkdirSync(path.dirname(abs), { recursive: true });
        fsP.writeFileSync(abs, body);
      }
      const r = spawnSync(
        process.execPath,
        [path.join(dir, 'scripts', 'check-grafana-provisioning-parity.js')],
        { encoding: 'utf8', cwd: dir },
      );
      const out = `${r.stdout || ''}${r.stderr || ''}`;
      fsP.rmSync(dir, { recursive: true, force: true });
      return { code: r.status, out };
    };

    ok('門 A: compose の provisioning が 1 件も無いと fail する（変異試験）', () => {
      const { code, out } = makeRepo({
        'deploy/local/observability/grafana.yaml': 'data:\n  x.yaml: |\n    a: 1\n',
      });
      assert.strictEqual(code, 1, `compose 0 件で緑を返した。門 A が消えている:\n${out}`);
      assert.match(out, /0 件検査/, out);
    });

    ok('門 B: k8s の inline を 1 件も取り出せないと fail する（変異試験）', () => {
      const { code, out } = makeRepo({
        'deploy/grafana/provisioning/datasources/datasources.yaml': 'apiVersion: 1\n',
        'deploy/local/observability/grafana.yaml': 'kind: ConfigMap\n',
      });
      assert.strictEqual(code, 1, `inline 0 件で緑を返した。門 B が消えている:\n${out}`);
      assert.match(out, /0 件検査/, out);
    });
  }

  // --- #583: 他リポジトリ参照の走査を .md 外へ広げる ------------------------------
  //
  // ★ #507 は対象を *.md に限っていた（決定 4）。そのため
  //   `.github/workflows/doc-links-planning.yml` の空白区切り参照を誰も見ていなかった。
  //   人が読む散文は docs/（.md）だけでなく .github/ と deploy/ にもある。
  {
    const { spawnSync, execFileSync } = require('child_process');
    const path = require('path');
    const SCRIPTS = __dirname;
    const REPO = path.join(SCRIPTS, '..');
    const xrepo = require('./check-cross-repo-refs.js');

    const runXrepo = (args = []) => {
      const r = spawnSync(process.execPath, [path.join(SCRIPTS, 'check-cross-repo-refs.js'), ...args], {
        encoding: 'utf8',
        cwd: REPO,
      });
      return { code: r.status, out: `${r.stdout || ''}${r.stderr || ''}` };
    };

    ok('check-cross-repo-refs が実データで違反 0 件（.md 外を含む）', () => {
      const { code, out } = runXrepo();
      assert.strictEqual(code, 0, out);
    });

    ok('check-cross-repo-refs: 走査が .md だけに戻っていない（下限）', () => {
      const { out } = runXrepo();
      const m = out.match(/OK: (\d+) 件に/);
      assert.ok(m, `OK メッセージから走査件数を読めない:\n${out}`);
      // .md だけなら 619 件前後。広げた後は 1400 件を超える。**件数リテラルで固定しない**が、
      // 「.md だけへ戻った」ことは検出できる下限を置く。
      assert.ok(
        Number(m[1]) > 1000,
        `走査が ${m[1]} 件しかない。対象が *.md だけへ戻っていないか:\n${out}`,
      );
    });

    ok('check-cross-repo-refs: 除外した件数をログに出す（黙って飛ばさない）', () => {
      const { out } = runXrepo();
      // 文言そのものではなく「除外した対象と件数を名指ししている」ことを見る
      // （文言を固定すると、説明を足すたびに兄弟が取り残される。本 PR で 2 度踏んだ）。
      // ★ #790: キット版へ差し替えた際に文言が「除外 N 件（scripts/ の非 Markdown）」へ変わった。
      //   **意図（対象と件数を名指しする）は同じ**なので、旧文言と新文言の両方を受ける形へ広げた
      //   —— ここで新文言だけへ書き換えると、キットが元の言い回しへ戻ったときに黙って外れる。
      assert.match(
        out,
        /除外 \d+ 件（scripts\/[^）]*）|scripts\/[^\d]*\d+ 件は検査していません/,
        out,
      );
    });

    // ★★ 直したものを捕まえるか、実データで確かめる（#675 の教訓）。
    ok('check-cross-repo-refs: develop 時点の workflow へ当てると空白区切りを検出する（変異試験）', () => {
      let before;
      try {
        before = execFileSync('git', ['show', 'origin/develop:.github/workflows/doc-links-planning.yml'], {
          encoding: 'utf8',
          cwd: REPO,
        });
      } catch {
        return; // origin/develop が無い環境（tarball 展開等）では飛ばす
      }
      const v = xrepo.findViolations(before, { markdown: false });
      // develop 側に既に是正が入っている（＝本 PR がマージ済み）なら前提が消える。
      if (v.length === 0) return;
      assert.ok(
        v.some((x) => x.kind === 'spaced' && x.matched.includes('ai-stock-trading')),
        `develop の空白区切りを検出できなかった: ${JSON.stringify(v)}`,
      );
    });

    // 除外が効いていることの側（設計の根拠を固定する）。
    ok('check-cross-repo-refs: scripts/ を除外しないと検査器自身が大量に落ちる（除外の根拠）', () => {
      const self = require('fs').readFileSync(path.join(SCRIPTS, 'check-cross-repo-refs.js'), 'utf8');
      const v = xrepo.findViolations(self, { markdown: false });
      assert.ok(
        v.length > 10,
        `検査器自身のフィクスチャが ${v.length} 件しか当たらない。除外の根拠が消えていないか確認すること`,
      );
    });

    ok('check-cross-repo-refs: 除外は scripts/ の 1 本（名指しリストへ戻っていない）', () => {
      // ★ #790: キット版は EXCLUDED_DIRS を export しないため、**振る舞い**で同じ性質を固定する
      //   （export を 1 行足す固有デルタより、振る舞いで見るほうがキット追随の摩擦が小さい）。
      //   まだ存在しないファイル名が除外されることが「名指しリストではない」ことの証拠である。
      assert.ok(xrepo.isExcluded('scripts/zz-not-yet-written-checker.js'), 'scripts/ 配下が除外されない');
      assert.ok(!xrepo.isExcluded('scripts/README.md'), 'scripts/ の .md まで除外している');
      assert.ok(!xrepo.isExcluded('docs/how-to/anything.yml'), 'scripts/ 以外まで除外している');
    });

    // ★★ 走査範囲を「広げた」はずが、以前見ていたものを落としていないか。
    //
    // PR #679 の初版は `scripts/` を丸ごと除外し、**`scripts/README.md` を走査対象から
    // 落としていた**（レビュー指摘）。是正前は対象が `*.md` だったため見えていたファイルであり、
    // **「広げる」変更が同時に狭めていた**ことになる。**方向の違う退行は下限テストでは捕まらない**
    // —— 走査件数は増えているからである。**集合の包含として固定する。**
    ok('check-cross-repo-refs: 追跡下の .md は 1 件残らず走査対象に入る（狭める退行の門）', () => {
      const md = execFileSync(
        'git',
        ['-C', REPO, 'ls-files', '--', '*.md', ':!src/ai-stock-trading'],
        { encoding: 'utf8', maxBuffer: 1 << 26 },
      )
        .split('\n')
        .map((s) => s.trim())
        .filter(Boolean);
      assert.ok(md.length > 100, `追跡 .md が ${md.length} 件しかない（母集合の取得が壊れている）`);
      const dropped = md.filter((f) => xrepo.isExcluded(f));
      assert.deepStrictEqual(
        dropped,
        [],
        `以前は走査していた .md が除外されている: ${dropped.join(', ')}`,
      );
    });

    ok('check-cross-repo-refs: scripts/ の非 Markdown は除外され、.md は除外されない', () => {
      assert.strictEqual(xrepo.isExcluded('scripts/check-cross-repo-refs.js'), true);
      assert.strictEqual(xrepo.isExcluded('scripts/changelog-overrides.json'), true);
      assert.strictEqual(xrepo.isExcluded('scripts/README.md'), false);
      assert.strictEqual(xrepo.isExcluded('scripts/lib/anything.md'), false);
      assert.strictEqual(xrepo.isExcluded('docs/README.md'), false);
      assert.strictEqual(xrepo.isExcluded('.github/workflows/ci.yml'), false);
    });

    // 廃止した関数が戻っていないか（「後方互換」と書きながら誰も呼んでいなかった）。
    //
    // ★ #790: キット版へ差し替えたことで `trackedMarkdown` が戻った（キット側は内部で
    //   一度も呼んでいない＝未使用の export である）。**本リポの都合で消すと固有デルタが
    //   1 つ増える**ため、ここでは「戻っていない」ではなく **「主経路が使っていない」**
    //   —— つまり走査が `*.md` だけへ縮んでいないこと —— を固定する形へ変えた。
    //   未使用 export そのものはキットへ環流する（本 PR の作業仕様書「環流の起票案」）。
    ok('check-cross-repo-refs: 走査の主経路が trackedMarkdown（.md だけ）へ戻っていない', () => {
      const src = require('fs').readFileSync(path.join(SCRIPTS, 'check-cross-repo-refs.js'), 'utf8');
      const main = src.slice(src.indexOf('function main('));
      assert.ok(
        !/trackedMarkdown\(/.test(main),
        'main() が trackedMarkdown を呼んでいる（走査が *.md だけへ戻っている）',
      );
      assert.ok(/trackedFiles\(/.test(main), 'main() が追跡下の全ファイル走査を使っていない');
    });

    // --- NFR / #757: キット版の createChecker 構造を載せたことの門 -------------------
    //
    // キット版 scripts.test.js は「自リポ名を CROSS_REPOS へ入れたら設定エラーで止まる」
    // だけを見る。ここでは**本リポの既定設定が実際にその不変条件を満たしていること**と、
    // **載せ替えで検出力（型 1〜4・〔〕区切り）が 1 つも落ちていないこと**を固定する。
    // 従前この不変条件はコメントでしか守られておらず、**SELF_NAMES を CROSS_REPOS へ
    // 入れると正当な自リポ参照を 22 件止める**（#507 の実測）。

    ok('check-cross-repo-refs #757: 既定設定が自リポ名を他リポ側へ混ぜていない', () => {
      // ★ #790: キット版は CROSS_REPOS / SELF_NAMES を直接 export せず、
      //   `LONG_NAMES`（= 既定の crossRepos）と `DEFAULT_CHECKER.selfNames` から取る。
      const crossRepos = xrepo.LONG_NAMES;
      const selfNames = xrepo.DEFAULT_CHECKER.selfNames;
      const names = [...Object.keys(crossRepos), ...Object.values(crossRepos)];
      for (const self of selfNames) {
        assert.ok(!names.includes(self), `自リポ名 ${self} が CROSS_REPOS に混ざっている`);
      }
      // 自リポ名は空でない（空にすると検証が素通りする＝門が無効化される）。
      assert.ok(selfNames.length > 0, 'SELF_NAMES が空（設定の妥当性検査が効かない）');
      assert.ok(selfNames.includes('MSP'), '規約が定める自リポ短縮形 MSP が抜けている');
    });

    // ★ 変異試験。**「検証を入れた」と言えるのは、壊した設定を実際に拒むときだけである。**
    ok('check-cross-repo-refs #757: 自リポ名を混ぜた設定は例外で止まる', () => {
      assert.throws(
        () => xrepo.createChecker({ crossRepos: { 'my-repo': 'MINE' }, selfNames: ['MINE'] }),
        /SELF_NAMES/,
      );
      // 長い表記の側で衝突しても止まること（短縮形だけ見て素通りしない）。
      assert.throws(
        () => xrepo.createChecker({ crossRepos: { MSP: 'x' }, selfNames: ['MSP'] }),
        /SELF_NAMES/,
      );
      // 空設定は「検査した」と誤認しない（0 件検査で緑を返さない）。
      assert.throws(() => xrepo.createChecker({ crossRepos: {} }), /CROSS_REPOS が空/);
    });

    // ★ 非退行の門。設定から組み立てる形へ載せ替えたときに、**本リポにしか無い検出力**
    //   （型 4 owner 誤り #590 / 〔〕区切りの列挙 #586 / 型 1・2・3）が落ちていないこと。
    //   #756 の判定が「本リポ版が優る」根拠にした当の 2 型である。
    ok('check-cross-repo-refs #757: 載せ替えで型 1〜4 と〔〕区切りの検出が落ちていない', () => {
      const kinds = (t) => xrepo.findViolations(t).map((v) => v.kind).sort();
      // 型 1（長い表記）。長い名前を先に当てるので `project-planning#1` 全体を拾う。
      assert.deepStrictEqual(kinds('project-planning#1'), ['long']);
      // 型 2（列挙の裸）。
      assert.deepStrictEqual(kinds('planning#1・#2'), ['enum']);
      // 型 2（〔〕で添える形の中を裸にしたもの。#586）。
      assert.deepStrictEqual(kinds('PR planning#244〔裁定依頼 #237〕'), ['enum']);
      // 型 3（空白区切り）。
      assert.deepStrictEqual(kinds('planning #123'), ['spaced']);
      // 型 4（owner 誤り。#590）。
      assert.deepStrictEqual(kinds('acme/project-planning#50'), ['owner']);
      // 偽陽性の側（規約どおりの形で鳴らない）。**片側だけ見ると検査器が外される。**
      assert.deepStrictEqual(kinds('planning#1 と AST#2 と endazon/project-planning#50'), []);
      assert.deepStrictEqual(kinds('PR planning#244〔裁定依頼 planning#237〕'), []);
      assert.deepStrictEqual(kinds('本リポの #454 と #455'), []);
    });
  }

  // --- #587: ピン留めモデルの版数移行 Runbook（IADR-0112 決定 3） --------------------
  //
  // ★ **文書は消えても CI が赤くならない。** 受け入れ基準の 3 点が Runbook から落ちたら
  //   気づけるようにする（#546 / #665 と同じ型）。**文言の丸写しではなく、
  //   「その節が果たす役割」を固定する。**
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const RUNBOOK = 'docs/operations/llm-model-pin-runbook.md';

    ok('ピン Runbook: 版数移行に Stage 0 再検証が前提だと読み取れる', () => {
      const t = fs.readFileSync(path.join(REPO, RUNBOOK), 'utf8');
      assert.match(t, /Stage 0/, 'Stage 0 再検証への言及が無い');
      assert.match(t, /AST\/ADR-0011/, 'ピン留めを定めた計画 ADR への言及が無い');
    });

    // ★★ `ADR-0011` は **両プロジェクトに実在し、意味が違う**（実測）:
    //      AST/ADR-0011 = LLM モデルのピン留め   ← 本 Runbook が指したいもの
    //      MSP/ADR-0011 = wiki エンジンの選定     ← 裸で書くとこちらへ解決される
    //    `.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合の ID 修飾」の対象だが、
    //    **同規約の適用箇所に frontmatter は挙がっておらず**、`check-plan-id-qualification.js` も
    //    「AST 文脈で裸の ID」（型 B）は**偽陽性を避けるため意図的に検出しない**。つまり
    //    **機械はこの取り違えを止めない。**
    //
    //    ［2026-08-21 追記 / specs キー是正］ 旧版は「`specs:` へ実体名 `ADR-0011_llm-model-pinning`
    //    を書く」を代替手段としていたが、この文字列は `.ai-context/specs/` に実在せず
    //    `check-trace-blocks.js`（specs キーの実在性検査）の違反そのものだった。正しい代替は
    //    ADR-0048 決定 9 の一般修飾子（`<英数の短縮名>:<ID>`）を `adrs:` へ書くことである
    //    （AST 側の同型是正 commit e5dbf4f が確立した方式と同じ）。**qualifier 付きトークンは
    //    external として実在検査を skip する**ため、機械はこちらも取り違えを止めないが、
    //    `specs:` の実在性検査とは矛盾しない。
    //    ★ frontmatter の `related_ids:` は、docs/ trace-ification（ADR-0048 決定 4。
    //    `migrate-remaining.js` の Class T）により本文直後の `<!-- trace: ... -->`
    //    コメントブロックへ移設済み（レンダリングされない・5 キー固定）。本テストも
    //    移設後の形で読む。
    ok('ピン Runbook: AST の計画 ADR を裸の ID で adrs/iadrs へ入れず、AST: 修飾で adrs へ入れている（trace ブロック）', () => {
      const t = fs.readFileSync(path.join(REPO, RUNBOOK), 'utf8').replace(/\r\n/g, '\n');
      const traceMatch = /<!--\s*trace:\s*\n([\s\S]*?)-->/m.exec(t);
      assert.ok(traceMatch, 'trace ブロックが無い');
      const trace = traceMatch[1];
      const adrsLine = /^adrs:\s*\[([^\]]*)\]/m.exec(trace);
      const iadrsLine = /^iadrs:\s*\[([^\]]*)\]/m.exec(trace);
      const specsLine = /^specs:\s*\[([^\]]*)\]/m.exec(trace);
      assert.ok(adrsLine, 'trace ブロックに adrs: が無い');
      assert.ok(iadrsLine, 'trace ブロックに iadrs: が無い');
      assert.ok(specsLine, 'trace ブロックに specs: が無い');
      const adrIds = [
        ...adrsLine[1].split(',').map((s) => s.trim()).filter(Boolean),
        ...iadrsLine[1].split(',').map((s) => s.trim()).filter(Boolean),
      ];
      const specIds = specsLine[1].split(',').map((s) => s.trim()).filter(Boolean);
      assert.ok(
        !adrIds.includes('ADR-0011'),
        '裸の ADR-0011 は MSP の wiki エンジン ADR へ解決される。AST の計画 ADR は adrs/iadrs へ入れない',
      );
      assert.ok(
        adrIds.includes('AST:ADR-0011'),
        'AST/ADR-0011 への参照が trace ブロック（adrs: の AST: 修飾）から読み取れない',
      );
      assert.ok(
        !specIds.some((s) => s.startsWith('ADR-0011')),
        '.ai-context/specs/ に実在しない実体名 ADR-0011_llm-model-pinning が specs: に残っている（check-trace-blocks.js 違反）',
      );
    });

    ok('ピン Runbook: 利用不能時は実行せず発注せず、かつ「障害ではない」と書いてある', () => {
      const t = fs.readFileSync(path.join(REPO, RUNBOOK), 'utf8');
      assert.match(t, /発注もしない/, '「発注しない」が書かれていない');
      // ★ 禁止だけでは足りない。**なぜ落とさないのか**が無いと善意で破られる（#382 の懸念）。
      assert.match(t, /障害ではない/, '「障害ではない」（設計上の正常な結果）が書かれていない');
      // レート制限と利用不能の区別（確定事項 3）。
      assert.match(t, /429/, 'レート制限（429）と利用不能の区別が書かれていない');
    });

    ok('ピン Runbook: 監視対象を単一情報源で示し、値を複写していない', () => {
      const t = fs.readFileSync(path.join(REPO, RUNBOOK), 'utf8');
      assert.match(t, /PurposeModels/, 'ピンの単一情報源（PurposeModels）を指していない');
      assert.match(t, /appsettings\.json/, '単一情報源のファイルを指していない');
      // ★★ 値そのものを書き写していないこと。#440 が analysis を変える予定であり、
      //    複写すると本 PR の時点で既に古くなることが分かっている（[[IADR-0141]]）。
      assert.doesNotMatch(
        t,
        /claude-fable-5/,
        'ピンの値を Runbook へ複写している（単一情報源を指すだけにする。#440 で変わる値である）',
      );
    });

    // ★ **手順書のコマンドは、本リポの道具立て（Node.js / .NET）だけで動くこと。**
    //   `python3` は `scripts/setup.sh` でコメントアウトされた opt-in であり**利用保証が無い**。
    //   手順書は運用者が実行するものなので、手元に無い処理系へ依存させない。
    //   **実測**: `docs/operations/` `docs/how-to/` の手順書 8 本のコードブロックが使う実行ファイルを
    //   全数で引くと、`python3` はここ 1 件だけであった（他は git / node / pnpm / dotnet と、
    //   kubectl / argocd / docker / psql / kcadm.sh のようにその手順が本来必要とする道具）。
    //   **見るのはコードブロック内の実行行だけである。** 地の文は対象外 —— 本 Runbook は
    //   「なぜ python を使わないか」を**説明するために `python3` の語を含む**。素の全文検索で
    //   落とすと、説明を書いたことで落ちる（**禁止の理由を書けなくなる**）。
    ok('ピン Runbook: 列挙コマンドが本リポの道具立て（node）で書かれている', () => {
      const t = fs.readFileSync(path.join(REPO, RUNBOOK), 'utf8').replace(/\r\n/g, '\n');
      const cmds = [...t.matchAll(/```(?:console|bash|sh)\n([\s\S]*?)```/g)]
        .flatMap((m) => m[1].split('\n'))
        .map((l) => l.trim().replace(/^\$\s*/, ''))
        .filter((l) => l && !l.startsWith('#'));
      assert.ok(cmds.length > 0, 'コードブロックの実行行を 1 行も拾えていない（検査が空振りしている）');
      assert.ok(
        !cmds.some((l) => /^python3?\b/.test(l)),
        '手順書のコマンドが python へ依存している（scripts/setup.sh で opt-in＝利用保証が無い）',
      );
      assert.ok(
        cmds.some((l) => /^node\b/.test(l)),
        '列挙コマンドが node で書かれていない',
      );
    });

    ok('ピン Runbook: 運用仕様書から辿れる（孤立していない）', () => {
      const ops = fs.readFileSync(path.join(REPO, 'docs/operations/operations.md'), 'utf8');
      assert.match(ops, /llm-model-pin-runbook/, 'operations.md から Runbook へ辿れない');
    });
  }

  // --- #701: blocked 判定の再検証（IADR-0180） ---------------------------------
  //
  // ★ #617 の再発防止。#554 / #556 / #562 は「AI だけでは完結しない」として保留され
  //   フェーズ B 打ち切りの根拠にされたが、3 件とも別環境で同日中に着地した。
  // ★ 機械検査は置かない（環境固有か恒久制約かは意味の理解が要る）。
  //   固定するのは「規範が消えないこと」だけである。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const HANDOFF = 'docs/how-to/session-handoff.md';
    const ADR = '.ai-context/adr/IADR-0180_blocked-judgments-expire.md';

    ok('#701: 棚卸しのたびに波 5（旧 行 H）を測り直す規範が引継資料にある', () => {
      const t = fs.readFileSync(path.join(REPO, HANDOFF), 'utf8');
      assert.match(
        t,
        /一度「できない」と書いた判定は、棚卸しのたびに測り直す/,
        '再検証の規範が引継資料から消えた',
      );
      assert.match(t, /前回できなかった.*据え置かない/s, '「据え置かない」が消えた');
    });

    ok('#701: 判定に「最後に測った時点」を添える規範がある', () => {
      const t = fs.readFileSync(path.join(REPO, HANDOFF), 'utf8');
      assert.match(t, /最後に測った時点/, '「最後に測った時点」が消えた');
    });

    // ★ 恒久制約（§3）と環境依存（§4.5）の書き分けが崩れると #617 が再発する。
    ok('#701: 恒久制約と環境固有の観測の書き分けが残っている', () => {
      const t = fs.readFileSync(path.join(REPO, HANDOFF), 'utf8');
      assert.match(t, /環境に依らない制約は §3 に書いてある/, '§3 と §4.5 の対比が消えた');
      assert.match(t, /本節は\*\*測れば変わりうる\*\*/, '「測れば変わりうる」が消えた');
    });

    ok('#701: 判定の賞味期限が ADR に記録されている', () => {
      const t = fs.readFileSync(path.join(REPO, ADR), 'utf8');
      assert.match(t, /賞味期限/, '「判定には賞味期限がある」が消えた');
      assert.match(t, /#617/, '根拠（#617 の実測）が消えた');
      assert.match(t, /機械検査は置かない/, '機械検査を置かない旨が消えた');
    });
  }

  // --- #703: キット e0bc81c への追随（IADR-0181） -------------------------------
  //
  // ★ #623 の受け入れ基準 1・2 は「作る」ではなく「追随する」課題だった。
  //   キット e0bc81c が repo-template へ加えた 5 ファイルのうち 3 つは #622 で着地し、
  //   2 つ（pr-size.yml / ai-implementation.yml）が置き去りにされていた。
  // ★ repo-template 全体のバイト一致検査は置かない（IADR-0181 決定 5）——
  //   planning が未 populate の環境で**静かに緑を返す検査器**になるためである
  //   （#664 / PR #672 で 5 本是正した型）。固定するのは本リポ側の到達状態だけとする。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const TEMPLATE = '.github/ISSUE_TEMPLATE/ai-implementation.yml';
    const PRSIZE = '.github/workflows/pr-size.yml';

    ok('#703: issue テンプレートが Given-When-Then とファイル領域の 2 欄を必須で持つ', () => {
      const t = fs.readFileSync(path.join(REPO, TEMPLATE), 'utf8');
      assert.match(t, /受け入れ基準（Given-When-Then）/, 'Given-When-Then の欄名が消えた');
      assert.match(t, /Given <前提> \/ When <操作> \/ Then <期待結果>/, 'GWT の雛形が消えた');
      assert.match(t, /id: file_scope/, 'ファイル領域の欄（file_scope）が消えた');
      assert.match(t, /ファイル領域（並列判定に使う）/, 'ファイル領域の欄名が消えた');
      // ★ 欄が在っても **任意**なら宣言は集まらない。必須であることまで見る。
      //   `id:` から次の `- type:` までを 1 欄として切り、その中の validations を見る。
      const blocks = t.split(/\n  - type: /).slice(1);
      for (const id of ['acceptance', 'file_scope']) {
        const b = blocks.find((x) => new RegExp(`^\\s*\\w+\\n\\s*id: ${id}\\b`, 'm').test(x));
        assert.ok(b, `欄 ${id} を切り出せない（テンプレートの構造が変わった）`);
        assert.match(b, /validations:\s*\n\s*required: true/, `欄 ${id} が必須ではない`);
      }
    });

    ok('#703: キットが正本 —— issue テンプレートはキットとバイト一致（分類 A）', () => {
      const kit = path.join(
        REPO,
        'planning/tools/impl-handoff-kit/repo-template',
        TEMPLATE,
      );
      // planning が未 populate の環境では比較できない。**静かに緑を返さない**ため、
      // populate 済みかどうかを先に確かめ、未 populate のときだけ明示して抜ける。
      if (!fs.existsSync(path.join(REPO, 'planning/tools/impl-handoff-kit'))) {
        console.log('    (planning 未 populate のためバイト一致比較を省略)');
        return;
      }
      assert.ok(fs.existsSync(kit), `キット側に ${TEMPLATE} が無い（キットの改名を疑う）`);
      const a = fs.readFileSync(path.join(REPO, TEMPLATE));
      const b = fs.readFileSync(kit);
      assert.ok(
        a.equals(b),
        `${TEMPLATE} がキットとバイト一致しない（IADR-0115 決定 1 分類 A）: ` +
          `本リポ ${a.length}B / キット ${b.length}B`,
      );
    });

    ok('#703: PR サイズ検査は warn 方式（マージを止めない）', () => {
      const t = fs.readFileSync(path.join(REPO, PRSIZE), 'utf8');
      assert.match(t, /name: PR Size/, 'ワークフロー名が変わった');
      assert.doesNotMatch(t, /^\s*exit 1\s*$/m, 'PR サイズ検査が fail する形になっている');
      assert.doesNotMatch(t, /continue-on-error/, 'warn 方式なら continue-on-error は要らない');
      assert.match(t, /GITHUB_STEP_SUMMARY/, '警告の出力先（ジョブサマリ）が消えた');
      // ★ `/> 400/` だと `> 4000` へ緩めた変異を素通りする（変異試験で実測）。
      //   **部分一致で数値を見ない。** 末尾を \b で閉じる。
      assert.match(t, /> 400\b/, 'しきい値 400 が消えた（キットの数値は動かさない）');
      assert.match(t, /目安の 400 行/, '警告文のしきい値が本体の条件と食い違っている');
    });

    // ★ 除外は「効くこと」を実測して置く。キット既定の `**/orval/**` は本リポで 0 件しか
    //   当たらない（直近 60 PR）。**実パスが消えると検査は静かに無意味になる。**
    ok('#703: PR サイズ検査の除外が本リポの生成物の実パスを指している', () => {
      const t = fs.readFileSync(path.join(REPO, PRSIZE), 'utf8');
      const required = [
        'src/platform/frontend/src/foundation/api/generated/**',
        'src/platform/frontend/src/foundation/i18n/locales/**',
        'docs/**',
        '.ai-context/**',
      ];
      const missing = required.filter((p) => !t.includes(`:(exclude)${p}`));
      assert.deepStrictEqual(missing, [], `除外から落ちた実パス: ${missing.join(', ')}`);
      // ★ 除外先が実在することまで見る（パスの改名で静かに空振りしないため）。
      for (const p of required) {
        const dir = path.join(REPO, p.replace(/\/\*\*$/, ''));
        assert.ok(fs.existsSync(dir), `除外先 ${p} が実在しない（改名・移動を疑う）`);
      }
    });

    ok('#703: 較正の根拠（実測値）と環流が IADR に残っている', () => {
      const t = fs.readFileSync(
        path.join(REPO, '.ai-context/adr/IADR-0181_pr-size-check-calibration.md'),
        'utf8',
      );
      // ★ `> 400` が `> 4000` を素通りしたのと同型。**数値は前後を \b で閉じる。**
      assert.match(t, /\b23 \/ 30\b/, 'キット既定の警告率（実測）が消えた');
      assert.match(t, /\b7 \/ 30\b/, '本設定の警告率（実測）が消えた');
      assert.match(t, /両立しない/, '正本の 2 規範が両立しない旨が消えた');
    });
  }

  // --- #705: required status check の設定手順（IADR-0182） -----------------------
  //
  // ★ 「必須にすると永久 pending になる」型の事故は 2 件目である。
  //     1 件目 = paths: フィルタ（docs/ai-workflow.md が文書で警告していた）
  //     2 件目 = types: に reopened が無い（本 issue の実測。claude-code-review.yml のみ）
  //   CLAUDE.md「検査器・規約の追加は同型の事故が 2 回起きたら」の条件を満たすため検査器を足す。
  //
  // ★ paths: の側は検査器にしない —— frontend.yml 等は**意図して** paths: を持ち、
  //   必須にしないことで正しく運用されている。機械的に禁じると正当な設定を壊す。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const WF = path.join(REPO, '.github/workflows');
    const DOC = 'docs/ai-workflow.md';

    ok('#705: pull_request で起動する全ワークフローが reopened を含む', () => {
      const files = fs.readdirSync(WF).filter((f) => /\.ya?ml$/.test(f));
      assert.ok(files.length > 0, 'ワークフローを 1 件も読めない（走査が壊れている）');
      const missing = [];
      let scanned = 0;
      for (const f of files) {
        const text = fs.readFileSync(path.join(WF, f), 'utf8');
        // `on:` ブロック内の `pull_request:` に続く `types:` を見る。
        // pull_request_target / issue_comment 等の別イベントの types は対象外。
        const m = text.match(/^\s{2}pull_request:\s*$\n((?:\s{4}.*\n|\s*\n)*)/m);
        if (!m) continue;
        const block = m[1];
        const types = block.match(/^\s{4}types:\s*\[([^\]]*)\]/m);
        if (!types) continue; // types 省略時は既定に reopened が含まれる
        scanned += 1;
        if (!/\breopened\b/.test(types[1])) missing.push(`${f}: [${types[1].trim()}]`);
      }
      // 走査 0 件で静かに緑を返す形を塞ぐ（#664 / PR #672 の型）。
      assert.ok(scanned >= 5, `types: を持つ pull_request ワークフローが ${scanned} 件（走査が壊れている）`);
      assert.deepStrictEqual(
        missing,
        [],
        '再オープンで起動しないワークフローがある（required にすると永久 pending になる）:\n  ' +
          missing.join('\n  '),
      );
    });

    ok('#705: 手順書が check 名とワークフロー名の取り違えを止めている', () => {
      const t = fs.readFileSync(path.join(REPO, DOC), 'utf8');
      assert.match(t, /check の名前.*ワークフローの名前」?ではない/s, 'context の取り違えへの警告が消えた');
      // 実在する check 名が**表の行として**挙がっていること（#623 基準 3 が名指しした 2 つを含む）。
      // ★ 単なる包含（`t.includes('`claude-review`')`）だと、表から行を消しても
      //   本文の別の言及（見出し「`claude-review` を必須にする場合の注意」など）が残るため
      //   素通りする —— 変異試験で実測した。**行そのものを見る。**
      for (const name of ['build-and-test', 'lint', 'commit-messages', 'pr-title', 'image-build', 'claude-review']) {
        assert.match(
          t,
          new RegExp(`^\\| \`${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\` \\|`, 'm'),
          `必須にする check 名 ${name} の表の行が手順書から消えた`,
        );
      }
      // CodeQL は #719 で pull_request に paths: が付いたため必須対象から除外した。
      // 行そのものは取り消し線付きで残し、「必須にしない」の根拠が読める状態を固定する
      // （行ごと消すと除外の経緯が追えなくなり、再び必須に足す退行を止められない）。
      assert.match(t, /^\| ~~`CodeQL`~~ \|/m, 'CodeQL の除外行（取り消し線付き）が手順書から消えた');
      assert.match(t, /~~`CodeQL`~~.*必須にしない/s, 'CodeQL を必須にしない旨の注記が消えた');
      // 存在しない context を「指定してはならない」と明示していること。
      assert.match(t, /`CI` \/ `Security`.*指定してはならない/s, 'ワークフロー名を禁じる記述が消えた');
    });

    ok('#705: claude-review の必須化が「完了」であって「指摘なし」でないと書いてある', () => {
      const t = fs.readFileSync(path.join(REPO, DOC), 'utf8');
      assert.match(t, /レビューが完走したこと/, '「完了を担保する」旨が消えた');
      assert.match(t, /🔴 のままのマージは止まらない/, '必須化しても 🔴 を止めない旨が消えた');
    });

    ok('#705: blocked 判定が能力と規則を書き分け、最後に測った時点を持つ', () => {
      const t = fs.readFileSync(path.join(REPO, DOC), 'utf8');
      assert.match(t, /能力の不在/, '「能力の不在」の区分が消えた');
      assert.match(t, /規則による禁止/, '「規則による禁止」の区分が消えた');
      assert.match(t, /最後に測った時点: 2026-08-11/, '最後に測った時点が消えた');
      assert.match(t, /棚卸しのたびに測り直す/, '再測定の規範が消えた');
      assert.match(t, /ToolSearch/, '再測定の手順（MCP ツールの確認）が消えた');
    });

    ok('#705: 手順書が「推奨であって現状ではない」と読み分けさせている', () => {
      const t = fs.readFileSync(path.join(REPO, DOC), 'utf8');
      assert.match(t, /推奨設定」であって「現在そうなっている」ではない/, '推奨と現状の書き分けが消えた');
      assert.match(t, /配備されるまでの暫定手段/, '暫定手段の併記が消えた');
      // API 経路（UI だけにしない）。
      assert.match(t, /required_status_checks\.contexts/, 'API 経路の記述が消えた');
    });
  }

  // --- #683: 差分ベースの検査器が返す「偽の緑」（IADR-0183） --------------------
  //
  // ★ 検査器に欠陥は無い。**走らせた順序**の問題である。
  //     クラス A（`git show HEAD:` / `git diff …HEAD` を読む）= **未コミットが見えない**
  //       … PR #682 の事故（#683 が挙げた 1 本）
  //     クラス B（`git ls-files` を読む）= **untracked が見えない**
  //       … PR #708 の事故（**本セッションが実際に踏んだ**。#683 は挙げていなかった）
  //   クラス C（作業ツリーを読む 29 本）には**何も足さない**。順序で結果が変わらないので嘘になる。
  //
  // ★ 到達可能性を静的に見積もらない。初回実装では、当時の 6 本中 **3 本**の呼び出しが
  //   `--self-test` ブロックの内側（`return;` の手前）にあり dead code だったが、
  //   静的ヒューリスティックはその 6 本すべて到達可能と誤答した（IADR-0183 決定 7）。
  //   **本検査は untracked のファイルを実際に作り、実挙動を 1 本ずつ観測する。**
  {
    const fs = require('fs');
    const path = require('path');
    const { execFileSync, spawnSync } = require('child_process');
    const REPO = path.join(__dirname, '..');
    const HEAD_CHECKERS = ['check-doc-updated.js', 'check-landed-subjects.js'];
    const TRACKED_CHECKERS = ['check-cross-repo-refs.js', 'check-plan-id-qualification.js'];
    const GUARDED = [...HEAD_CHECKERS, ...TRACKED_CHECKERS];
    const readScript = (f) => fs.readFileSync(path.join(REPO, 'scripts', f), 'utf8');

    ok('#683: 偽の緑を返しうる検査器が A=2 / B=2 で宣言されている', () => {
      const all = fs
        .readdirSync(path.join(REPO, 'scripts'))
        .filter((f) => /\.js$/.test(f) && !/\.test\.js$/.test(f))
        .sort();
      // 走査 0 件・少数件で静かに緑を返す形を塞ぐ（#664 / PR #672 の型）。
      assert.ok(all.length >= 30, `scripts/ の走査が壊れている（${all.length} 件）`);

      const head = [];
      const tracked = [];
      for (const f of all) {
        for (const m of readScript(f).matchAll(
          /warnIfResultMayDifferFromCi\(\s*'[^']*'\s*,\s*MODE\.(HEAD|TRACKED)/g,
        )) {
          (m[1] === 'HEAD' ? head : tracked).push(f);
        }
      }
      assert.deepStrictEqual(head, HEAD_CHECKERS, 'クラス A（HEAD を読む）の該当が変わった');
      assert.deepStrictEqual(tracked, TRACKED_CHECKERS, 'クラス B（git ls-files）の該当が変わった');

      // クラス C には足さない（順序に依存しないので、足せば嘘の警告になる）。
      const strays = all.filter((f) => !GUARDED.includes(f) && /worktree-state/.test(readScript(f)));
      assert.deepStrictEqual(strays, [], 'クラス C の検査器に順序警告が足されている');
    });

    ok('#683: 該当 4 本すべてが実際に警告を出し、終了コードを変えない', () => {
      const probe = path.join(REPO, '.tmp-worktree-state-probe-683');
      const run = (f) =>
        spawnSync(process.execPath, [path.join(REPO, 'scripts', f)], {
          cwd: REPO,
          encoding: 'utf8',
          maxBuffer: 64 * 1024 * 1024,
        });
      // 警告が出ていない状態の終了コードを先に採る（比較の基準）。
      const baseline = new Map(GUARDED.map((f) => [f, run(f).status]));

      fs.writeFileSync(probe, '#683 の到達可能性を実測する一時ファイル。テストが必ず消す。\n');
      try {
        // 前提: probe が本当に untracked として見えていること。
        // 見えないまま「警告が出た」を測ると、何を測ったのか分からない検査になる。
        const porcelain = execFileSync(
          'git',
          ['status', '--porcelain', '--untracked-files=normal'],
          { cwd: REPO, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 },
        );
        assert.match(
          porcelain,
          /^\?\? \.tmp-worktree-state-probe-683$/m,
          'probe が untracked として見えない（.gitignore 等に隠れている）',
        );

        for (const f of GUARDED) {
          const r = run(f);
          // warn は **stdout** へ書く（ci-annotate.js の注記）。取りこぼさないよう両方を見る。
          const out = `${r.stdout || ''}${r.stderr || ''}`;
          assert.match(
            out,
            /#683 \/ IADR-0183/,
            `${f} が警告を出さない（呼び出しが到達不能になっている疑い）`,
          );
          assert.strictEqual(
            r.status,
            baseline.get(f),
            `${f} の終了コードが警告で変わった（警告は失敗させない: IADR-0183 決定 1）`,
          );
        }
      } finally {
        fs.rmSync(probe, { force: true });
      }
    });

    ok('#683: クラスごとに促す行動を書き分けている', () => {
      const t = fs.readFileSync(path.join(REPO, 'scripts/lib/worktree-state.js'), 'utf8');
      assert.match(t, /コミットしてから再実行/, 'クラス A の促し（コミット）が消えた');
      assert.match(t, /`git add` してから再実行/, 'クラス B の促し（git add）が消えた');
      // CI 用の環境分岐を書かない（決定 3）。条件と分岐の二重管理を避ける。
      assert.doesNotMatch(t, /GITHUB_ACTIONS/, 'CI 用の環境分岐が足された（決定 3 に反する）');
    });

    ok('#683: worktree-state.js の自己試験が緑', () => {
      const r = spawnSync(
        process.execPath,
        [path.join(REPO, 'scripts/lib/worktree-state.js'), '--self-test'],
        { encoding: 'utf8' },
      );
      assert.strictEqual(r.status, 0, `自己試験が落ちた:\n${r.stdout}${r.stderr}`);
      assert.match(r.stdout, /self-test OK/, '自己試験の結果表示が消えた');
    });

    // ★★ 分類そのものを実挙動で突き合わせる（IADR-0183 決定 8・9）。
    //
    //   初回の分類は**文字列 grep** で行い、`check-test-spec-coverage.js` を誤ってクラス B に入れた。
    //   当たったのは**エラーメッセージ中の `git ls-files`** で、この検査器は git を一切呼ばない。
    //   AI レビューの指摘は 1 本だったが、**測り直したら過大は 2 本**だった（`check-action-versions.js` も）。
    //
    //   「静的な見積もりが誤答した」型はこれで 2 回目（1 回目は注入の到達可能性）なので、
    //   `CLAUDE.md`「同型の事故が 2 回起きたら」に従い検査器にする。
    //
    // ★ 突合は 2 方向とも掛ける。**宣言 → 実挙動だけでは、宣言し忘れた新設の検査器を素通りする。**
    ok('#683: クラス分類が実挙動の git 使用と一致する（両方向）', () => {
      const os = require('os');
      const shimDir = fs.mkdtempSync(path.join(os.tmpdir(), 'git-shim-'));
      // ★★ PATH へ偽の `git` を置く方式はやめた（#851）。**Windows では原理的に効かない。**
      //
      //   実測（2026-08-17）:
      //     execFileSync('git', args) / spawnSync('git', args)（shell:false） … **素通り**
      //     execSync('git …')（shell 経由）                                   … 経由する
      //     execFileSync(<絶対パス>/git.cmd, args)                            … **EINVAL**
      //
      //   `options.env.PATH` でも親の `process.env.PATH` でも素通りする。**Node は shell:false での
      //   `.cmd` / `.bat` 実行を拒否する**（CVE-2024-27980 対策）ため、PATH 解決がシムを飛ばして
      //   実体の `git.exe` に当たる。**本テストの母集合（下の 38 検査器）で数えると
      //   `execFileSync('git'` が 5 ファイル 7 件・`spawnSync('git'` が 0 件・
      //   `execSync(\`git` が 2 ファイル 2 件**であり、**`TRACKED_CHECKERS` の 2 本は
      //   どちらも `execFileSync`** である（`check-cross-repo-refs.js` 1 件 /
      //   `check-plan-id-qualification.js` 2 件）。したがって `.cmd` ラッパーを足しても
      //   永久に捕まらない。
      //
      //   したがって **`child_process` を JS レベルでフックする**。検査器の起動は
      //   `spawnSync(process.execPath, …)` なので `--require` を 1 つ足すだけでよい
      //   （`NODE_OPTIONS` の引用符問題も避けられる）。**プラットフォーム分岐は作らない**
      //   —— CI とローカルが同じものを測る状態を保つ。
      //
      //   **検査器は入れ子でプロセスを起動しない**（実測: `bash` / `node` / `process.execPath` の
      //   起動は 0 件）ため、直接の子プロセスだけをフックすれば旧シムと同じ範囲を覆う。
      const probe = path.join(shimDir, 'git-probe.js');
      fs.writeFileSync(
        probe,
        [
          "'use strict';",
          "const fs = require('fs');",
          "const cp = require('child_process');",
          'const LOG = process.env.GIT_SHIM_LOG;',
          // 旧シムの `echo "$@"` と同じ書式（引数をスペース連結して 1 行）で追記する。
          'const write = (parts) => { try { fs.appendFileSync(LOG, parts.join(" ") + "\\n"); } catch (_) {} };',
          // `git` / `git.exe` / 絶対パスのいずれでも拾う。
          'const isGit = (f) => /(^|[\\\\/])git(\\.exe)?$/i.test(String(f));',
          "for (const name of ['execFileSync', 'spawnSync', 'execFile', 'spawn']) {",
          '  const orig = cp[name];',
          '  cp[name] = function (file, args) {',
          '    if (isGit(file) && Array.isArray(args)) write(args);',
          '    return orig.apply(this, arguments);',
          '  };',
          '}',
          // shell 経由（`execSync('git …')`）は文字列で来る。先頭の `git` を剥がして同じ書式にする。
          //
          // **検出しないこと（明示する）**: コマンド文字列の**先頭トークンが git** である形だけを見る。
          // `cd x && git …` のような複合コマンド中の git は捕まえない（旧 PATH シムは PATH 解決
          // 経由で捕まえた）。**現行の 2 件はいずれも `` `git ${args}` `` の単純形であり実害は無い**が、
          // 複合形が増えたら本フックを広げること（#852 の AI レビュー 🟢）。
          "for (const name of ['execSync', 'exec']) {",
          '  const orig = cp[name];',
          '  cp[name] = function (cmd) {',
          '    const m = /^\\s*(?:"[^"]*git(?:\\.exe)?"|\\S*git(?:\\.exe)?)\\s+([\\s\\S]+)$/.exec(String(cmd));',
          '    if (m) write([m[1]]);',
          '    return orig.apply(this, arguments);',
          '  };',
          '}',
        ].join('\n'),
      );
      try {
        // 母集合は**検査器**である。生成器・投入スクリプトは対象外であり、**実行もしない**
        // （`gen-changelog.js` は履歴を読むのが本来の仕事で、走らせれば成果物を書き得る）。
        const all = fs
          .readdirSync(path.join(REPO, 'scripts'))
          .filter((f) => /\.js$/.test(f) && !/\.test\.js$/.test(f))
          .sort();
        const NOT_CHECKERS = [
          'gen-changelog.js',
          'gen-openapi-skeleton.js',
          'gen-knowledge-graph.js',
          'measure-abac-combinations.js',
          'seed-abac-policies.js',
        ];
        const scripts = all.filter((f) => !NOT_CHECKERS.includes(f));
        // 母集合の件数を固定する。**新しい検査器が増えたら、まずここが落ちて宣言を促す。**
        // ★ #713 で `check-kit-sync.js` を足したため 33 → 34（ラチェットが設計どおり発火した）。
        // ★ #737 で `check-feedback-status-sync.js` を足したため 34 → 35（同上）。
        // ★ 計画 pin を ce9abd2 へ進めた際、キットが新規配布した `check-review-verdict.js` を
        //    採用したため 35 → 36（同上。planning#333 / AI レビューが判定を投稿しない形を止める）。
        // ★ #755 で `check-reading-budget.js`（必読規約の総量予算。IADR-0200）を足したため 36 → 37（同上）。
        // ★ #493 で `check-knip.js`（未使用コード・依存のラチェット。IADR-0211）を足したため 37 → 38（同上）。
        //    git を一切呼ばない検査器なので TRACKED_CHECKERS / HEAD_CHECKERS のどちらにも載らない。
        // ★ ADR-0048 決定 2・決定 6（planning 依存の全撤去・kit 同期検査の退役）で
        //    `check-planning-pin-freshness.js` / `check-kit-sync.js` / `check-feedback-dispatched.js` /
        //    `check-feedback-status-sync.js` の 4 本を退役させたため 38 → 34（同上）。
        // ★ ADR-0048 決定 4（trace ブロックの文法・値域検査）で `check-trace-blocks.js` を新設した
        //    ため 34 → 35（同上。git を一切呼ばず fs のみで走査するため TRACKED_CHECKERS /
        //    HEAD_CHECKERS のどちらにも載らない）。同じ PR で新設した `gen-knowledge-graph.js` は
        //    `gen-changelog.js` / `gen-openapi-skeleton.js` と同じ生成器であり（既定 `--json` は
        //    stdoutへ書くだけで副作用は無いが、役割は「検査」ではなく「生成」）、NOT_CHECKERS へ
        //    加えて母集合に数えない。
        // ★ #783（#442 子 5）で `check-deploy-manifests.js`（deploy/ の chart / overlay が
        //    レンダリングできるかを検査）を新設したため 35 → 36（同上）。git を一切呼ばず
        //    fs と外部コマンド（helm / kubectl）で走るため、TRACKED_CHECKERS / HEAD_CHECKERS の
        //    どちらにも載らない（`check-trace-blocks.js` と同じ扱い）。
        // ★ #455 子 C で `check-event-topology.js`（イベント型 → 発行元 / 購読先の対応表を
        //    baseline と突合）を新設したため 36 → 37（同上）。git を一切呼ばず fs のみで走査するため、
        //    TRACKED_CHECKERS / HEAD_CHECKERS のどちらにも載らない。
        // ★ IADR-0232 決定 8 で `check-ci-latency.js`（CI の「逆転」——build-and-test が
        //    claude-review の下限を追い越したことの検知）を新設したため 37 → 38（同上）。
        //    GitHub API を叩くが git は一切呼ばないため、TRACKED_CHECKERS / HEAD_CHECKERS の
        //    どちらにも載らない（`check-trace-blocks.js` と同じ扱い）。
        assert.strictEqual(scripts.length, 38, `検査器の母集合が 38 本から変わった（${scripts.length} 件）`);
        assert.deepStrictEqual(
          NOT_CHECKERS.filter((f) => !all.includes(f)),
          [],
          '検査器でないとして除外した名前が scripts/ に存在しない（リストが腐っている）',
        );

        // 本リポを対象とする呼び出しだけを数える（`-C planning` の呼び出しは軸が違う）。
        const HISTORY = new Set(['show', 'diff', 'log', 'merge-base', 'rev-list']);
        // 規則 2 の例外。コミット前に件名は存在しないので、見落とす既存状態が無い（決定 5）。
        const HISTORY_EXEMPT = ['check-commit-messages.js'];
        assert.strictEqual(
          HISTORY_EXEMPT.length,
          1,
          '名指しの例外が増えた（黙って伸びる除外リストは腐る: IADR-0169 決定 2）',
        );

        const usesScanLsFiles = [];
        const usesHistory = [];
        for (const f of scripts) {
          const log = path.join(shimDir, `${f}.log`);
          fs.writeFileSync(log, '');
          spawnSync(process.execPath, ['--require', probe, path.join(REPO, 'scripts', f)], {
            cwd: REPO,
            encoding: 'utf8',
            maxBuffer: 64 * 1024 * 1024,
            env: { ...process.env, GIT_SHIM_LOG: log },
          });
          for (const raw of fs.readFileSync(log, 'utf8').split('\n').filter(Boolean)) {
            // `-C <path>` を剥がす。剥がした先が本リポ以外なら数えない。
            let line = raw;
            const m = /^-C (\S+) (.*)$/.exec(line);
            if (m) {
              if (path.resolve(m[1]) !== path.resolve(REPO)) continue;
              line = m[2];
            }
            const [sub, ...rest] = line.split(/\s+/);
            // `status` は本モジュール（worktree-state.js）自身の呼び出しなので数えない。
            if (sub === 'status') continue;
            if (sub === 'ls-files' && !rest.includes('--error-unmatch')) usesScanLsFiles.push(f);
            if (HISTORY.has(sub)) usesHistory.push(f);
          }
        }

        // 実挙動 → 宣言。
        assert.deepStrictEqual(
          [...new Set(usesScanLsFiles)].sort(),
          TRACKED_CHECKERS,
          '走査母集合を git ls-files から引く検査器と MODE.TRACKED の宣言が食い違う',
        );
        assert.deepStrictEqual(
          [...new Set(usesHistory)].filter((f) => !HISTORY_EXEMPT.includes(f)).sort(),
          HEAD_CHECKERS,
          'コミット済みの履歴を読む検査器と MODE.HEAD の宣言が食い違う',
        );
        // 宣言 → 実挙動（宣言だけあって実体が無い＝今回の誤分類を止める）。
        for (const f of TRACKED_CHECKERS) {
          assert.ok(usesScanLsFiles.includes(f), `${f} は MODE.TRACKED を宣言するが git ls-files を呼ばない`);
        }
        for (const f of HEAD_CHECKERS) {
          assert.ok(usesHistory.includes(f), `${f} は MODE.HEAD を宣言するが履歴を読まない`);
        }
      } finally {
        fs.rmSync(shimDir, { recursive: true, force: true });
      }
    });

    ok('#683: 検証の順序が DoD にだけ書かれている', () => {
      const dod = fs.readFileSync(path.join(REPO, 'docs/DEFINITION_OF_DONE.md'), 'utf8');
      assert.match(dod, /^### 検証の順序/m, 'DoD から「検証の順序」の節が消えた');
      assert.match(dod, /`git add -A` → 検査器 → コミット/, '順序そのものが消えた');
      assert.match(dod, /\*\*4 本\*\*/, '該当本数（4 本）が消えた');
      // 正本は DoD 1 箇所（IADR-0141 単一情報源 / IADR-0183 決定 6）。
      const wf = fs.readFileSync(path.join(REPO, 'docs/ai-workflow.md'), 'utf8');
      assert.doesNotMatch(wf, /検証の順序/, 'ai-workflow.md へ順序が重複した（正本は DoD）');
    });
  }


  // --- #688: メタ作業の NFR は無採番のまま（IADR-0179） -------------------------
  //
  // ★ 規範を 1 つ足しながら必読を 208B 減らした（IADR-0178 決定 4 の実行）。
  //   経緯を別紙へ出し、機構の説明を圧縮した分で相殺している。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const ENTRY = '.claude/rules/traceability.md';
    // ★ #755 / IADR-0201: 入口はキット配布物（分類 A・バイト一致）と companion の 2 ファイルになった。
    //   本リポ固有の規範は companion に在るため、「入口に残っている」は 2 ファイルの連結で見る。
    //   「入口から出した」の否定は本リポが書く companion 側で見る（キット配布物の文言は本リポの管理外）。
    const ENTRY_REPO = '.claude/rules/traceability.repo.md';
    const readEntry = () => fs.readFileSync(path.join(REPO, ENTRY), 'utf8') + '\n' + fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const readRepoEntry = () => fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const ANNEX = 'docs/how-to/plan-id-range-history-annex.md';

    // ★ #724 / IADR-0188 が射程を「メタ作業」限定から一般化したので、固定する文字列も追随させた。
    //   規範そのものは #688 のものである（覆っていない。広がっただけ）。
    ok('#688: 当たる番号が無いなら無採番 NFR という規範が入口にある', () => {
      const t = readEntry();
      assert.match(
        t,
        // ★ #755: 規範はキット配布物 traceability.md の文言（planning#311 の場合 2）で固定する。
        /ID 列はあるが、その作業に当たる番号が無い場合/,
        '無採番の規範が入口から消えた',
      );
    });

    // ★ 既存 3 規範を巻き込んでいないこと。加筆のついでに落とすのが最も危ない。
    ok('#688: NFR 採番の既存 3 規範が残っている', () => {
      const t = readEntry();
      for (const n of [
        // ★ #755: companion へ畳んだ際に短くした（規範は同じ）。
        '実在性は検査されない',
        '既存の無採番 `NFR` は遡及書き換えしない',
        'NFR-01`〜`NFR-27',
      ]) {
        assert.ok(t.includes(n), `既存の規範「${n}」が入口から消えた`);
      }
    });

    ok('#688: 採番の経緯が別紙へ移り、入口から消えている', () => {
      const t = readEntry();
      const a = fs.readFileSync(path.join(REPO, ANNEX), 'utf8');
      assert.ok(!t.includes('直近 100 コミット中 50 件'), '採番導入の経緯が入口に残っている');
      assert.ok(a.includes('直近 100 コミット中 50 件'), '経緯が別紙に無い（移動でなく削除になっている）');
      assert.match(a, /稼働する製品の要件/, '無採番の根拠（27 件が製品の要件）が別紙に無い');
    });
  }

  // --- #724: 無採番 NFR の射程の一般化と、必読予算（IADR-0188） -----------------
  //
  // ★ 予算は CLAUDE.md が既に定めている規範であり（IADR-0178 決定 4）、本 PR が足すのは
  //   規範ではなく「守れたかを機械が読める形にした表明」である。#718 以来 302B 超過したまま
  //   6 コミット・3 日間、誰にも気づかれなかった（人手だけでは守れていない）。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const ENTRY = '.claude/rules/traceability.md';
    // ★ #755 / IADR-0201: 入口はキット配布物（分類 A・バイト一致）と companion の 2 ファイルになった。
    //   本リポ固有の規範は companion に在るため、「入口に残っている」は 2 ファイルの連結で見る。
    //   「入口から出した」の否定は本リポが書く companion 側で見る（キット配布物の文言は本リポの管理外）。
    const ENTRY_REPO = '.claude/rules/traceability.repo.md';
    const readEntry = () => fs.readFileSync(path.join(REPO, ENTRY), 'utf8') + '\n' + fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const readRepoEntry = () => fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const ANNEX = 'docs/how-to/plan-id-range-history-annex.md';
    // ★ #755 / IADR-0200: 母集合と予算値の単一情報源は scripts/check-reading-budget.js に移した
    //   （Claude Code の集合 = CLAUDE.md ＋ .claude/rules/*.md を走査。予算 51,200 の正本は計画リポ運用ガイド §8）。
    //   ここでリテラルの一覧・値を持つと、companion を足したときに黙って母集合から落ちる（本 PR がまさに
    //   traceability.repo.md を足した）。テストは検査器の measure() を呼ぶ。
    const rbudget = require('./check-reading-budget.js');
    const CLAUDE_SET = rbudget.AGENT_SETS.find((x) => x.name === 'Claude Code');
    const BUDGET = rbudget.BUDGET_BYTES;
    const measureClaude = () => rbudget.measure(CLAUDE_SET, REPO);

    ok('#724: 毎セッション必読（Claude Code の集合）が 51,200B 予算に収まっている', () => {
      const m = measureClaude();
      assert.deepStrictEqual(m.missing, [], `母集合に欠落がある: ${m.missing.join(' / ')}`);
      assert.ok(m.entries.some((e) => e.file === ENTRY), 'キット配布物 traceability.md が母集合に無い');
      assert.ok(m.entries.some((e) => e.file === ENTRY_REPO), 'companion traceability.repo.md が母集合に無い（走査で拾えていない）');
      assert.ok(
        m.total <= BUDGET,
        `必読合計が予算を超えた（${m.total}B / 上限 ${BUDGET}B・超過 ${m.total - BUDGET}B）。` +
          `内訳: ${m.entries.map((e) => `${e.file}=${e.bytes}`).join(' / ')}。` +
          '加筆するなら同量以上を削るか、重複を正本・別紙へ畳むこと（IADR-0178 決定 4 / IADR-0188 決定 4 / IADR-0200）',
      );
    });

    ok('#724: 一般化の 3 要素が入口にある（代表例・製品も対象・実装側で作らない）', () => {
      const t = readEntry();
      for (const n of [
        'メタ作業（規約・検査器・文書統制）は代表例',
        '製品の作業にも当たる番号が無いことはある',
        '無いことは「実装側で作ってよい」ではない',
      ]) {
        assert.ok(t.includes(n), `一般化の要素「${n}」が入口から消えた`);
      }
    });

    // ★ #718 が別紙へ 1 世代足して入口の「4 世代分」を取り残した型（母集合の規則 8）。
    //   当初は「入口の数 == 別紙の数」を突き合わせていたが、**それでも腐った** ——
    //   #795 が同じ括弧の中で pin だけ前進させ、「5 世代」を据え置いた。実体は 8 世代あり、
    //   2 箇所が**揃って古い**ので一致検査は緑のままだった（波末クロス監査 🟡 / #793）。
    // ★★ よって固定する性質を変える: **導出値（世代数）をどちらも持たないこと**。
    //   数が無ければ「揃って古くなる」こともできない（[[IADR-0141]] 決定 1 の規則 10）。
    //   導線（入口 → 別紙）と、別紙が実体を持つことは引き続き見る。
    ok('#724 / #793: 世代数という導出値を入口も別紙も持たない（導線と実体は残る）', () => {
      const t = readEntry();
      const a = fs.readFileSync(path.join(REPO, ANNEX), 'utf8');
      // 1. 導出値が復活していないこと（入口・別紙とも）
      for (const [label, text] of [['入口', t], ['別紙', a]]) {
        const m = text.match(/(\d+)\s*世代(分|で引き直)/);
        assert.strictEqual(
          m,
          null,
          `${label}に世代数「${m && m[0]}」が戻っている。母数（別紙の記録）が増えるたびに腐る導出値であり、` +
            '数を書かずに参照だけ残すこと（#793）',
        );
      }
      // 2. 導線が残っていること（数を消したついでに参照ごと落とす事故を防ぐ）
      assert.match(t, /引き直しの記録は別紙/, '入口から別紙への導線（引き直しの記録）が消えた');
      assert.match(t, /plan-id-range-history-annex\.md/, '入口が別紙を指していない');
      // 3. 別紙が実体を持っていること。**実体は「pin の遷移」の件数で測る**（宣言ではなく走査）。
      const generations = new Set(
        [...a.matchAll(/`[0-9a-f]{7}` → `[0-9a-f]{7}`/g)].map((m2) => m2[0]),
      );
      assert.ok(
        generations.size >= 5,
        `別紙の引き直し記録が ${generations.size} 世代しか無い（記録ごと消えている可能性）`,
      );
    });

    // --- #728: planning#311 の裁定（無採番 NFR の 2 場合）への追随（IADR-0189） ---
    //
    // ★ 本リポの入口は分類 B/C であり、キットとのバイト一致では追随漏れを検出できない
    //   （#721 は分類 A だったので回帰テストが落ちた）。ここで固定できるのは
    //   「入った規範が後で消えること」までで、「上流に新しい規範が入ったこと」は捕まえられない。
    ok('#728: 無採番 NFR の 2 場合（環流の要否）が入口にある', () => {
      const t = readEntry();
      for (const n of [
        // ★ #755: この規範はキット配布物 traceability.md（分類 A）が持つ形になった。文言はキットの
        //   ものに揃える（規範の内容は同じ。planning#311 の 2 場合）。
        '無採番の `NFR` を許すのは次の 2 つの場合に限る',
        '計画へ ID の付与を環流する',
        '環流しない',
        '「面倒だから無採番」は 2 に当たらない',
        '作業を始める前に計画の ID 列を見て',
      ]) {
        assert.ok(t.includes(n), `planning#311 の規範「${n}」が入口から消えた`);
      }
    });

    // --- #730: 必読の恒久的な余白（IADR-0190） -----------------------------------
    //
    // ★ 予算テスト（#724）は上限しか見ない。「余白が薄すぎて次の規範を足せない」状態は
    //   上限内でも起こるので、下限（＝確保した余白）の側もラチェットとして固定する。
    // ★ 上限（BUDGET）とファイル一覧（REQUIRED）は #724 の定義を再利用する。
    //   ここでリテラルを持つと、上限を変えたとき下限側だけ追随を忘れて静かにずれる
    //   —— 本 PR が「同じ数値を 2 箇所に持つと必ずずれる」と書いている当のことである（#731 レビュー 🟡）。
    ok('#730: 必読の余白が確保した水準を割っていない', () => {
      // ★ #853: 下限は check-reading-budget.js が持つ（#790/#793 も同じ値を読むため）。
      //   ここでリテラルを持つと、2 箇所で同じ数値を持つことになり必ずずれる。
      const FLOOR = rbudget.MARGIN_FLOOR_BYTES;
      const total = measureClaude().total;
      const margin = BUDGET - total;
      assert.ok(
        margin >= FLOOR,
        `必読の余白が ${margin}B まで減った（下限 ${FLOOR}B）。` +
          '#730 が節の別紙化で作った余白を食い潰している。' +
          '規範でない部分を別紙へ出してから加筆すること（IADR-0173 / IADR-0190 決定 2）',
      );
    });

    // --- #735: キット追随漏れの取り込み（IADR-0194） ------------------------------
    //
    // ★ 未使用権限は「宣言だけ残る」形で静かに増える。キットが外した判断へ追随した以上、
    //   戻っていないことを固定する（出力先は GITHUB_STEP_SUMMARY だけである）。
    ok('#735: pr-size.yml が未使用権限 pull-requests: write を持たない', () => {
      const src = fs.readFileSync(path.join(REPO, '.github/workflows/pr-size.yml'), 'utf8');
      // コメント行を除いた実効の permissions だけを見る（散文の引用に引っかからないこと）
      const lines = src.split('\n').filter((l) => !/^\s*#/.test(l));
      const i = lines.findIndex((l) => /^permissions:/.test(l));
      assert.ok(i >= 0, 'pr-size.yml から permissions ブロックが消えた');
      const block = [];
      for (let j = i; j < lines.length; j++) {
        if (j > i && !/^\s+/.test(lines[j])) break;
        block.push(lines[j]);
      }
      assert.ok(
        !block.some((l) => /pull-requests/.test(l)),
        `pr-size.yml に未使用権限が戻っている:\n${block.join('\n')}`,
      );
    });

    // ★ 折りたたみスカラー（`>-`）の中では `#` はコメントではなく値の一部になる。
    //   固有デルタの説明を EXCLUDES の中へ書くと、pathspec が壊れて黙って除外が効かなくなる。
    ok('#735: pr-size.yml の EXCLUDES にコメントが混入していない', () => {
      const src = fs.readFileSync(path.join(REPO, '.github/workflows/pr-size.yml'), 'utf8');
      const m = src.match(/EXCLUDES: >-\n([\s\S]*?)\n {8}run:/);
      assert.ok(m, 'pr-size.yml から EXCLUDES の折りたたみスカラーを読めない');
      const value = m[1]
        .split('\n')
        .map((s) => s.trim())
        .join(' ');
      assert.ok(!value.includes('#'), `EXCLUDES にコメントが混入している（pathspec が壊れる）:\n${value}`);
      // 本リポの実パス（IADR-0181 の固有デルタ）が残っていること
      for (const p of [
        'src/platform/frontend/src/foundation/api/generated/**',
        'src/platform/frontend/src/foundation/i18n/locales/**',
      ]) {
        assert.ok(value.includes(p), `固有デルタの実パス「${p}」が EXCLUDES から消えた（IADR-0181 決定 1）`);
      }
    });

    // ★ 上流裁定へ追随した 4 ファイルから、撤廃した数値上限が戻っていないこと（IADR-0194 決定 1）。
    ok('#735: 監査の起動口から巡数の上限が消え、打ち切り条件は残っている', () => {
      for (const f of [
        '.claude/agents/adr-guardian.md',
        '.claude/agents/traceability-auditor.md',
        '.claude/commands/adr-check.md',
        '.claude/commands/trace-check.md',
      ]) {
        const t = fs.readFileSync(path.join(REPO, f), 'utf8');
        assert.ok(
          !t.includes('全面巡回は 1 回まで'),
          `${f} に巡数の上限が戻っている（IADR-0194 決定 1 が撤廃した）`,
        );
        // 打ち切り条件の明言は「本体」なので残っていること（決定 2）
        assert.ok(
          t.includes('これ以上の巡回'),
          `${f} から打ち切り条件の明言が消えた（IADR-0194 決定 2。上限より強く効く）`,
        );
      }
    });

    // ★★ 上の 4 ファイルだけを見ていたのが #739 レビュー 🔴 の原因である。
    //   `docs/DEFINITION_OF_DONE.md` に上限が残り、/verify が受け入れ基準として読む状態だった。
    //   **撤廃した語で追跡下を全数走査し、「記録・引用」として残ってよいものだけを名指しで許す**
    //   （母集合の規則 7 の機械化。規則 1「誤りの側から引く」＋ 規則 3「拡張子で絞らない」）。
    ok('#735: 撤廃した巡数上限が、記録・引用以外のどこにも残っていない', () => {
      const ABOLISHED = '全面巡回は 1 回まで';
      // 残ってよい場所と、その理由。**live な規範文書は 1 つも入れない。**
      // feedback/ は ADR-0048 決定 5 で撤去済み（環流は planning の issue へ一本化）のため、
      // 旧 feedback/ 由来の ALLOWED エントリは持たない（対象ファイル自体が存在しない）。
      const ALLOWED = new Map([
        ['.ai-context/adr/IADR-0141_audit-rounds-and-population-drawing.md', '撤廃された当の決定。日付つき追記で Superseded を明記済み'],
        ['.ai-context/adr/IADR-0116_reimplementation-branching-and-pr-policy.md', '同上（追記で撤廃を明記）'],
        ['.ai-context/adr/IADR-0194_audit-rounds-follow-upstream-no-numeric-cap.md', '新旧の対照表として引用している'],
        ['.ai-context/specs/20260814_issue-735_kit-catchup.md', '同上（本 PR の作業仕様書）'],
        ['scripts/scripts.repo.test.js', '本テスト自身'],
      ]);
      // 拡張子で絞らない（規則 3）。パスの除外だけで取る（規則 4）。
      const { execFileSync } = require('child_process');
      const tracked = execFileSync(
        'git',
        ['-C', REPO, 'ls-files', '--', ':!src/ai-stock-trading', ':!CHANGELOG.md'],
        { encoding: 'utf8', maxBuffer: 1 << 26 },
      )
        .split('\n')
        .map((s) => s.trim())
        .filter(Boolean);
      const offenders = [];
      let scanned = 0;
      for (const rel of tracked) {
        const abs = path.join(REPO, rel);
        let text;
        try {
          text = fs.readFileSync(abs, 'utf8');
        } catch {
          continue; // バイナリ・削除済み
        }
        scanned += 1;
        if (text.includes(ABOLISHED) && !ALLOWED.has(rel)) offenders.push(rel);
      }
      // ★ 0 件走査で静かに緑にしない（#664 の門）
      assert.ok(scanned > 50, `走査対象が ${scanned} 件しかない。走査が空振りしている`);
      assert.deepStrictEqual(
        offenders,
        [],
        `撤廃した巡数上限（「${ABOLISHED}」）が live な文書に残っている。` +
          'IADR-0194 決定 1 が撤廃したので、規範として書いてはならない（記録・引用なら ALLOWED へ理由つきで足すこと）',
      );
    });

    // --- #574: 「現在の床」を書いた文書が coverage-floor.json と食い違わない（IADR-0195） ---
    //
    // ★ 床の値は **3 度**置き直された（#571 で line 34 → 33、#574 で line 33 → 39 / branch 17 → 27、
    //   #899 で line 39 → 38）。**この行は #899 の時点で古くなっており、#900 の作業で母集合を
    //   引き直して初めて見つかった** —— 下の正規表現は「バッククォート付きの並記形 ＋ 同一行の
    //   『未満』」しか拾わないため、**この平文コメント自身を検査対象にできない**。
    //   機械検査の母集合を自分の母集合として採用すると同じ見落としを繰り返す（#902 の自己批判）。
    //   そのたびに追随が要る文書が複数あり、**追随漏れは「文書が古い床を現在値として述べる」**
    //   という形で現れる。旧値の文字列を全数走査する形は使えない —— 置き直しの**経緯**を書いた
    //   文書（IADR-0118 / IADR-0138 / IADR-0195 / TEST_STRATEGY・確定済みの作業仕様書）が
    //   正当に旧値を含むため、ALLOWED が母集合とほぼ一致して検出力が消える。
    //   そこで**「いま CI が判定に使う床」を述べている書き方だけ**を機械が拾い、JSON と突き合わせる。
    //   拾うのは **同じ行に「未満」を含む**もの＝ゲートの言明に限る。
    //   「据え置き」「置き直した」のような**経緯の記述は「未満」を伴わない**ので巻き込まない
    //   （矢印形 `line 33` → `39` も並記形に当たらない）。
    //   **限界を明記する**: 新しい文書がゲートの床を別の言い回しで書けば、この検査は素通りする。
    //   下の 0 件走査の門は「既存の言い回しが消えたこと」しか見ない。
    ok('#574: ゲートの床を述べた文書が coverage-floor.json と一致する', () => {
      const floor = JSON.parse(fs.readFileSync(path.join(REPO, 'src', 'coverage-floor.json'), 'utf8')).backend;
      // 「床」の近傍で `line N` / `branch M` が / で並記され、同じ行に「未満」がある形＝ゲートの言明。
      const STATED = /床[^\n]{0,12}?`line (\d+)`\s*\/\s*`branch (\d+)`[^\n]*未満/g;
      const { execFileSync } = require('child_process');
      const tracked = execFileSync(
        'git',
        ['-C', REPO, 'ls-files', '--', ':!src/ai-stock-trading', ':!CHANGELOG.md'],
        { encoding: 'utf8', maxBuffer: 1 << 26 },
      )
        .split('\n')
        .map((s) => s.trim())
        .filter(Boolean);
      const offenders = [];
      let stated = 0;
      for (const rel of tracked) {
        if (rel === 'scripts/scripts.repo.test.js') continue; // 本テスト自身（正規表現の literal）
        let text;
        try {
          text = fs.readFileSync(path.join(REPO, rel), 'utf8');
        } catch {
          continue; // バイナリ・削除済み
        }
        for (const m of text.matchAll(STATED)) {
          stated += 1;
          if (Number(m[1]) !== floor.line || Number(m[2]) !== floor.branch) {
            offenders.push(`${rel}: 「${m[0]}」（JSON は line ${floor.line} / branch ${floor.branch}）`);
          }
        }
      }
      // ★ 0 件走査で静かに緑にしない（#664 の門）。書き方を変えて検出対象が消えたら気付く。
      assert.ok(stated >= 2, `「現在の床」を述べた箇所が ${stated} 件しか見つからない。走査が空振りしている`);
      assert.deepStrictEqual(
        offenders,
        [],
        '床の現在値を述べた文書が src/coverage-floor.json と食い違っている（値の正は同 JSON。IADR-0118 決定 2 / IADR-0195 決定 3）',
      );
    });


    // --- #717: 記録を書き換えてよい境界（IADR-0191） ------------------------------
    //
    // ★ 「書き換えない」が本文と frontmatter の両方に掛かると読めていた（#715 レビュー 🟡）。
    //   キットが status の更新主体を定めている以上、一般禁止（読み B）は採れない。
    ok('#717: 書き換え境界（本文は不可 / 状態欄は対象外）が入口にある', () => {
      const t = readEntry();
      for (const n of [
        '「書き換えない」の対象は本文への後付け注記である',
        '日付つき追記ブロック',
        'frontmatter の状態欄',
        'は対象外',
      ]) {
        assert.ok(t.includes(n), `書き換え境界の規範「${n}」が入口から消えた`);
      }
    });

    // ★ 根拠がキット側に在ること（本リポの理屈だけで組み直されないように固定する）。
    ok('#717: 状態欄の更新主体をキットが定めている', () => {
      const kit = path.join(REPO, 'planning/tools/impl-handoff-kit/repo-template/feedback/README.md');
      if (!fs.existsSync(kit)) {
        console.log('notice: planning が未 populate のため、#717 のキット根拠は検査していない。');
        return;
      }
      const k = fs.readFileSync(kit, 'utf8');
      assert.match(k, /誰が書き換えるか/, 'キットから status の更新主体の表が消えた（IADR-0191 決定 1 の根拠）');
    });

    // ★ 母集合の規則は 8 つとも入口に残り、実例だけが出ていること。
    ok('#730: 母集合の規則（キット 1〜8 ＋ 本リポ 9・10）が入口に残り、実例は入口に無い', () => {
      // ★ #755: 規則 1〜8 はキット配布物 traceability.md、旧 7・8（現 9・10）は companion に在る。
      const t = readEntry();
      for (const n of [
        '誤りの側から引く',
        'あり得る形をすべて列挙してから引く',
        '拡張子で絞らない',
        '行フィルタで絞らない。パスから引く',
        '軸を 1 本で終わらせない',
        '引いた結果と、除外したものとその理由を作業仕様書に書く',
        '「追随する文書」を記憶で挙げない',
        '是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す',
        // 規則 8 のセルへ畳んだ 2 つの規範（実例と一緒に出してはならない）
        '是正前の語で引いても捕まらない',
        '走査ではなく計算し直す',
      ]) {
        assert.ok(t.includes(n), `母集合の規範「${n}」が入口から消えた`);
      }
      // 実例の issue 番号は 1 つも入口（本リポが書く companion 側）に残っていない
      const rt = readRepoEntry();
      const sec = rt.slice(rt.indexOf('## 是正・追随の母集合の取り方'));
      const end = sec.indexOf('\n## ', 3);
      const body = end < 0 ? sec : sec.slice(0, end);
      for (const n of ['#541', '#507', '#583', '#570', '#593', '#642', '#645', '#687', '#690']) {
        assert.ok(!body.includes(n), `実例「${n}」が入口に残っている（別紙・IADR-0141 と二重持ち）`);
      }
    });

    // ★ 上流が先に裁定していたことを IADR-0188 から辿れること（引用の欠落の是正）。
    ok('#728: IADR-0188 が planning#311 を引用している', () => {
      const a = fs.readFileSync(
        path.join(REPO, '.ai-context/adr/IADR-0188_unnumbered-nfr-applies-to-all-work.md'),
        'utf8',
      );
      assert.match(a, /planning#311/, 'IADR-0188 に planning#311 への参照が無い');
    });

    // ★ 入口から重複を消した分が、別紙に在ること（削除ではなく重複解消であることの確認）。
    ok('#724: ADR-0023 の遷移の記述が入口から消え、別紙に在る', () => {
      const t = readEntry();
      const a = fs.readFileSync(path.join(REPO, ANNEX), 'utf8');
      assert.ok(
        !t.includes('cert-manager'),
        'ADR-0023 の遷移の記述が入口に残っている（別紙 §2 と重複する）',
      );
      assert.ok(a.includes('cert-manager'), '遷移の記述が別紙に無い（重複解消でなく削除になっている）');
    });
  }

  // --- #697: CLAUDE.md の減量と 50KB 到達（IADR-0178） --------------------------
  //
  // ★ 本件は「別紙化」ではなく「正本へ畳む」。種別表は docs/README.md と完全に重複していた。
  //   3 箇所目（別紙）を作ると IADR-0141 に反するため、導線に置き換えた。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const CLAUDE = 'CLAUDE.md';
    const README = 'docs/README.md';
    const ADR = '.ai-context/adr/IADR-0178_claude-md-defers-to-docs-readme.md';

    ok('#697: 種別表が CLAUDE.md から消え、docs/README.md に在る', () => {
      const c = fs.readFileSync(path.join(REPO, CLAUDE), 'utf8');
      const d = fs.readFileSync(path.join(REPO, README), 'utf8');
      const rows = (s) => (s.match(/^\| `[\w-]+` \|/gm) || []).length;
      assert.strictEqual(rows(c), 0, `CLAUDE.md に種別表の行が残っている（${rows(c)} 行）`);
      assert.ok(rows(d) >= 19, `docs/README.md の種別表が痩せた（${rows(d)} 行）。正本を壊している`);
      assert.match(c, /docs\/README\.md/, 'CLAUDE.md に正本への導線が無い');
    });

    // ★ 表は出しても規範は出さない（IADR-0173 決定 2）
    ok('#697: 仕様書まわりの規範が CLAUDE.md に残っている', () => {
      const c = fs.readFileSync(path.join(REPO, CLAUDE), 'utf8');
      for (const n of ['.ai-context/specs/', '実装ADR', 'plan-feedback', '起点 ID']) {
        assert.ok(c.includes(n), `規範「${n}」が CLAUDE.md から消えた`);
      }
    });

    ok('#697: SPA 移行の進捗が CLAUDE.md から消え、規範が残っている', () => {
      const c = fs.readFileSync(path.join(REPO, CLAUDE), 'utf8');
      assert.ok(!c.includes('第 2 段の項目まで消化済み'), '進捗ナラティブが残っている');
      assert.match(c, /IADR-0121/, '進捗の正本（IADR-0121）への導線が無い');
      for (const n of ['React 19', 'Vite 6', 'TanStack Router']) {
        assert.ok(c.includes(n), `規範「${n}」が CLAUDE.md から消えた`);
      }
    });

    // ★★ IADR-0169 が実測で否定した記述。落ちると誤りが必読ファイルへ戻る。
    ok('#697: .github/workflows/ の記述が実測に合っている', () => {
      const c = fs.readFileSync(path.join(REPO, CLAUDE), 'utf8');
      assert.ok(
        !/GitHub App 権限では編集不可。ワークフロー変更はローカル/.test(c),
        'IADR-0169 が否定した「編集不可」の記述が残っている',
      );
      assert.match(c, /IADR-0169/, '是正の根拠（IADR-0169）を指していない');
    });

    // ★ レビュー 🟡 で判明した「記録から漏れた 2 件」。回帰テストが無かったため
    //   書き戻されても検出できない状態だった（IADR-0178 決定 6）。
    ok('#697: 認証の詳細が CLAUDE.md へ戻っていない（AI_SETUP.md が正本）', () => {
      const c = fs.readFileSync(path.join(REPO, CLAUDE), 'utf8');
      const setup = fs.readFileSync(path.join(REPO, 'AI_SETUP.md'), 'utf8');
      assert.ok(
        !c.includes('CLAUDE_CODE_OAUTH_TOKEN'),
        'CLAUDE.md に認証シークレットの詳細が戻っている（AI_SETUP.md と重複する）',
      );
      assert.ok(
        setup.includes('CLAUDE_CODE_OAUTH_TOKEN'),
        'AI_SETUP.md から認証シークレットが消えた。正本を壊している',
      );
    });

    ok('#697: 冒頭の規範（技術スタック別ルールへ追記）が残っている', () => {
      const c = fs.readFileSync(path.join(REPO, CLAUDE), 'utf8');
      assert.match(
        c,
        /技術スタックに依存する規約とフォルダ構成は、末尾の「技術スタック別ルール」へ追記する/,
        '冒頭を圧縮した際に規範まで落ちている',
      );
    });

    ok('#697: 到達と予算維持が ADR に記録されている', () => {
      const t = fs.readFileSync(path.join(REPO, ADR), 'utf8');
      assert.match(t, /正本へ畳む/, '「正本へ畳む」方針が書かれていない');
      assert.match(t, /誤りは「削る」のではなく「直す」/, '誤りの扱いが書かれていない');
      assert.match(t, /予算内に保つ/, '到達後の維持責任が書かれていない');
      assert.match(t, /余白を食う/, '予算記述の自己参照（決定 5）が書かれていない');
    });
  }

  // --- #695: 必読規約の減量 段 5（IADR-0177） ------------------------------------
  //
  // ★ 本段の対象節は**確定済み記録が節名で引く件数が全節中 最多（20 件）**であり、
  //   `check-commit-messages.js` / `check-test-traceability.js` も節名で引いている。
  // ★ 塊の内側に埋まっていた規範（着手条件は FR 単位 / CI は守っていない）は
  //   1 行に畳んで入口へ残した。**これが消えると、経緯だけ別紙にあって規範が消える。**
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const ENTRY = '.claude/rules/traceability.md';
    // ★ #755 / IADR-0201: 入口はキット配布物（分類 A・バイト一致）と companion の 2 ファイルになった。
    //   本リポ固有の規範は companion に在るため、「入口に残っている」は 2 ファイルの連結で見る。
    //   「入口から出した」の否定は本リポが書く companion 側で見る（キット配布物の文言は本リポの管理外）。
    const ENTRY_REPO = '.claude/rules/traceability.repo.md';
    const readEntry = () => fs.readFileSync(path.join(REPO, ENTRY), 'utf8') + '\n' + fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const readRepoEntry = () => fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const ANNEX = 'docs/how-to/plan-id-range-history-annex.md';
    const CMSG_ANNEX = 'docs/how-to/commit-message-rules-annex.md';
    const RECAP_ADR = '.ai-context/adr/IADR-0177_entry-exhausted-claude-md-quota.md';
    const HEADS = ['起点 ID の種別', 'コミットメッセージの機械チェック'];
    const NORMS = [
      'FR-01..22',                    // 現行レンジ（pin は資料再編 IADR-0228 で撤去）
      'NFR-01`〜`NFR-27',             // NFR の採番
      '`Proposed` でも ID としては実在する',
      '着手条件は FR 単位で読む',      // 塊の内側から畳んだ規範
      // 同上。旧規範「CI は計画 ADR の実在性を守っていない」は資料再編（IADR-0228）で
      // 宣言レンジ検査へ置き換わった（経緯は別紙 §3 の 2026-08-21 追記）。
      '計画 ADR の実在性は本節の宣言レンジで検査する',
    ];
    // 別紙へ出した塊（入口に残っていてはならない＝移動していない）
    const POP_ANNEX = 'docs/how-to/population-drawing-annex.md';
    const MOVED_OUT = [
      ['以下は前回の追随記録である', ANNEX],
      ['5 件とも `Accepted` へ移った', ANNEX],
      ['doc-links-planning.yml', ANNEX],
      ['恒久履歴へ載れる状態だった', CMSG_ANNEX],
      ['夜間の planning 系', CMSG_ANNEX],
      // ★ #730: 母集合の「破れた実例」列。規則 7・8 の分だけが別紙の担当で、
      //   規則 1〜6 の実例は IADR-0141 決定 1 が正本である（重複を作らない）。
      ['是正後も `admin / operator`〔空白あり〕の 1 件が残った', POP_ANNEX],
      ['索引の要約 1 行', POP_ANNEX],
    ];

    ok('#695 段 5: 対象節の見出しが 2 つともスタブとして入口に残っている', () => {
      const t = readEntry().replace(/\r\n/g, '\n');
      const headings = t
        .split('\n')
        .filter((l) => /^#{2,3}\s/.test(l))
        .map((l) => l.replace(/^#{2,3}\s*/, '').trim());
      for (const h of HEADS) {
        assert.ok(
          headings.some((x) => x.startsWith(h)),
          `見出し「${h}」が入口から消えた。確定済み引用（本節は 20 件で最多）とスクリプトの参照を壊す`,
        );
      }
    });

    ok('#695 段 5: 規範が入口に残っている（塊の内側から畳んだ 2 行を含む）', () => {
      const t = readEntry();
      for (const n of NORMS) {
        assert.ok(t.includes(n), `規範「${n}」が入口から消えた（IADR-0173 決定 2 に反する）`);
      }
    });

    ok('#695 段 5: 5 塊が入口から別紙へ移っている', () => {
      // ★ #755: 否定（入口に残っていない）は本リポが書く companion で見る。キット配布物は
      //   「夜間の planning 系」を一般規約として持つが、それは本リポの塊ではない。
      const t = readRepoEntry();
      for (const [needle, dest] of MOVED_OUT) {
        assert.ok(!t.includes(needle), `「${needle}」が入口に残っている（別紙へ出ていない）`);
        const a = fs.readFileSync(path.join(REPO, dest), 'utf8');
        assert.ok(a.includes(needle), `「${needle}」が ${dest} に無い（移動ではなく削除になっている）`);
      }
      assert.match(t, /plan-id-range-history-annex\.md/, '新規別紙への導線が無い');
      const a = fs.readFileSync(path.join(REPO, ANNEX), 'utf8');
      assert.match(a, /参照時にだけ読む別紙/, '別紙に「いつ読むか」が無い');
    });

    // ★★ 総括の結論。落ちると「CLAUDE.md の必要量は未確定」へ戻り、
    //    減量が必要量不明のまま進むか、止まったままになる。
    ok('#695 段 5: 入口を尽くした総括が ADR に記録されている', () => {
      const t = fs.readFileSync(path.join(REPO, RECAP_ADR), 'utf8');
      assert.match(t, /入口は尽きた/, '「入口が尽きた」が書かれていない');
      assert.match(t, /全節を塊単位で測る/, '塊単位の走査を全節へ広げたことが書かれていない');
      assert.match(t, /CLAUDE\.md/, 'CLAUDE.md への言及が無い');
      assert.match(t, /着手を解禁/, 'CLAUDE.md 減量の解禁が書かれていない');
      assert.match(t, /曲げない/, '届かない場合も統制を弱めない旨が書かれていない');
    });

    // ★★ IADR-0177 決定 5。**同型 2 回目**なので検査器を置いた。
    //   段 3 は行番号で切って文の途中を分断し、段 5 は文字列で切ったが行頭のインデントを残した
    //   （`- **ADR / IADR の実在性**` が前の箇条の子項目として描画された）。
    //   どちらも「切り出しの境界が行頭に揃っていない」型である。
    //   ★ 捕まえるのは残骸だけで、インデントのずれ自体は捕まらない（正しい値は文脈依存）。
    ok('#695 段 5: 入口と別紙に切り出しの残骸（空白のみの行・行末空白）が無い', () => {
      const targets = [
        '.claude/rules/traceability.md',
        '.claude/rules/traceability.repo.md', // ★ #755 で追加。companion も必読
        'CLAUDE.md', // ★ #697 で追加。必読 2 ファイルは同じ扱いにする
        'docs/how-to/plan-id-range-history-annex.md',
        'docs/how-to/commit-message-rules-annex.md',
        'docs/how-to/adr-supersede-citation-annex.md',
        'docs/how-to/cross-project-id-refs-annex.md',
        'docs/how-to/changelog-overrides-annex.md',
      ];
      for (const rel of targets) {
        const lines = fs.readFileSync(path.join(REPO, rel), 'utf8').replace(/\r\n/g, '\n').split('\n');
        const blank = [];
        const trailing = [];
        lines.forEach((l, i) => {
          if (/^[ \t]+$/.test(l)) blank.push(i + 1);
          else if (/[ \t]+$/.test(l)) trailing.push(i + 1);
        });
        assert.strictEqual(
          blank.length,
          0,
          `${rel}: 空白のみの行が残っている（行 ${blank.join(',')}）。切り出しが行頭に揃っていない`,
        );
        assert.strictEqual(
          trailing.length,
          0,
          `${rel}: 行末に空白が残っている（行 ${trailing.join(',')}）`,
        );
        // ★ 連続空行も同じ型の残骸である。**上の 2 つでは捕まらない**
        //   （`/^[ \t]+$/` は空文字列の行に当たらない）——本 PR のレビュー 🟢 が
        //   まさにこの穴を突いた。develop 側は 6 ファイルとも 0 件で、床は clean である。
        const doubles = [];
        for (let i = 1; i < lines.length; i += 1) {
          if (lines[i] === '' && lines[i - 1] === '') doubles.push(i + 1);
        }
        assert.strictEqual(
          doubles.length,
          0,
          `${rel}: 空行が 2 行以上連続している（行 ${doubles.join(',')}）。切り出しの残骸`,
        );
      }
    });

    ok('#695 段 5: 総括 ADR が可変の数値ではなく測り方を書いている', () => {
      const t = fs.readFileSync(path.join(REPO, RECAP_ADR), 'utf8');
      assert.match(t, /可変の数値を断定しない/, '数値を断定しない旨が書かれていない');
      assert.match(t, /statSync/, '測り方（コマンド）が書かれていない');
    });
  }

  // --- #693: 必読規約の減量 段 4（IADR-0176） ------------------------------------
  //
  // ★ 本段の対象節は `##` と `###` の**両方**を確定済み記録が節名で引いている。
  //   どちらを消しても引用先が消え、IADR-0166 により引用側は直せない。
  // ★ 出した 3 塊は見出しを持たないため、**入口から消えたこと**を負の表明で固定する
  //   （見出しスタブと違い「残っているか」だけでは移動を確かめられない）。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const ENTRY = '.claude/rules/traceability.md';
    // ★ #755 / IADR-0201: 入口はキット配布物（分類 A・バイト一致）と companion の 2 ファイルになった。
    //   本リポ固有の規範は companion に在るため、「入口に残っている」は 2 ファイルの連結で見る。
    //   「入口から出した」の否定は本リポが書く companion 側で見る（キット配布物の文言は本リポの管理外）。
    const ENTRY_REPO = '.claude/rules/traceability.repo.md';
    const readEntry = () => fs.readFileSync(path.join(REPO, ENTRY), 'utf8') + '\n' + fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const readRepoEntry = () => fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const ANNEX = 'docs/how-to/adr-supersede-citation-annex.md';
    const RECAP_ADR = '.ai-context/adr/IADR-0176_entry-slimming-recap-block-level-classification.md';
    const HEADS = ['残す箇所と書式', 'Superseded / Deprecated な ADR を引用するときの書式'];
    // 入口に残す規範（別紙へ出すと「別紙を読まなかっただけで成果物が壊れる」）
    const NORMS = [
      'ID を後継へ付け替えてはならない',
      '後継 ID は旧 ID の隣に置く',
      '注記そのものへ起票 ID を書き',
      'live な権威文書とコード',
      '機械検査は置いていない',
    ];
    // 別紙へ出した塊（入口に残っていてはならない＝移動していない）
    const MOVED_OUT = ['例外は 2 本あるが', 'コードを対象外にしない理由', 'submodule を populate しない'];

    ok('#693 段 4: 対象節の見出しが 2 つともスタブとして入口に残っている', () => {
      const t = readEntry().replace(/\r\n/g, '\n');
      const headings = t
        .split('\n')
        .filter((l) => /^#{2,3}\s/.test(l))
        .map((l) => l.replace(/^#{2,3}\s*/, '').trim());
      for (const h of HEADS) {
        assert.ok(
          headings.some((x) => x.startsWith(h)),
          `見出し「${h}」が入口から消えた。確定済み引用が指す先を壊す（IADR-0173 決定 1）`,
        );
      }
    });

    ok('#693 段 4: 規範が入口に残っている', () => {
      const t = readEntry();
      for (const n of NORMS) {
        assert.ok(t.includes(n), `規範「${n}」が入口から消えた（IADR-0173 決定 2 に反する）`);
      }
    });

    ok('#693 段 4: 説明・経緯が入口から別紙へ移っている', () => {
      const t = readEntry();
      const a = fs.readFileSync(path.join(REPO, ANNEX), 'utf8');
      for (const m of MOVED_OUT) {
        assert.ok(!t.includes(m), `「${m}」が入口に残っている（別紙へ出ていない）`);
        assert.ok(a.includes(m), `「${m}」が別紙に無い（移動ではなく削除になっている）`);
      }
      assert.match(t, /adr-supersede-citation-annex\.md/, '別紙への導線が無い');
      assert.match(a, /参照時にだけ読む別紙/, '別紙に「いつ読むか」が無い');
    });

    // ★★ 総括の結論。落ちると「段 4 で入口は尽きた」という誤りへ戻り、
    //    段 5 が計画から抜けたまま CLAUDE.md を必要量不明のまま削ることになる。
    ok('#693 段 4: 入口の総括が ADR に記録されている', () => {
      const t = fs.readFileSync(path.join(REPO, RECAP_ADR), 'utf8');
      assert.match(t, /段 4 は最終段ではなかった/, '「段 4 が最終段ではない」が書かれていない');
      assert.match(t, /段 5 を追加する/, '段 5 の追加が書かれていない');
      assert.match(t, /塊単位/, '分類を塊単位へ改めたことが書かれていない');
      assert.match(t, /50,000 を下回らない/, '上限側の実測（入口だけでは届かない）が書かれていない');
    });

    // ★ IADR-0175 決定 0 の継承。ADR が可変の数値を断定すると自分の編集で古くなる。
    ok('#693 段 4: 総括 ADR が可変の数値ではなく測り方を書いている', () => {
      const t = fs.readFileSync(path.join(REPO, RECAP_ADR), 'utf8');
      assert.match(t, /可変の数値を断定しない/, '数値を断定しない旨が書かれていない');
      assert.match(t, /statSync/, '測り方（コマンド）が書かれていない');
    });
  }

  // --- #691: 必読規約の減量 段 3（IADR-0175） ------------------------------------
  //
  // ★ 段 3 の対象節は **確定済み記録が節名で引く件数が全節中 最多（5 件）** である。
  //   見出しを消すと 5 件の引用が指す先が消え、IADR-0166 により引用側は直せない。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const ENTRY = '.claude/rules/traceability.md';
    // ★ #755 / IADR-0201: 入口はキット配布物（分類 A・バイト一致）と companion の 2 ファイルになった。
    //   本リポ固有の規範は companion に在るため、「入口に残っている」は 2 ファイルの連結で見る。
    //   「入口から出した」の否定は本リポが書く companion 側で見る（キット配布物の文言は本リポの管理外）。
    const ENTRY_REPO = '.claude/rules/traceability.repo.md';
    const readEntry = () => fs.readFileSync(path.join(REPO, ENTRY), 'utf8') + '\n' + fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const readRepoEntry = () => fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const ANNEX = 'docs/how-to/cross-project-id-refs-annex.md';
    const PLAN_ADR = '.ai-context/adr/IADR-0175_slimming-requires-claude-md-reduction.md';
    const MOVED = '複数プロジェクトを跨ぐ場合の ID 修飾';
    // 入口に残す規範（別紙へ出すと「別紙を読まなかっただけで CI に落ちる」）
    const NORMS = ['AST/FR-17', '短縮形に寄せる', '空白を入れない', 'endazon'];

    ok('#691 段 3: 対象節の見出しがスタブとして入口に残っている', () => {
      const t = readEntry().replace(/\r\n/g, '\n');
      const headings = t
        .split('\n')
        .filter((l) => /^#{2,3}\s/.test(l))
        .map((l) => l.replace(/^#{2,3}\s*/, '').trim());
      assert.ok(
        headings.some((x) => x.startsWith(MOVED)),
        `見出し「${MOVED}」が入口から消えた。確定済み引用 5 件が指す先を壊す`,
      );
    });

    ok('#691 段 3: 規範が入口に残っている', () => {
      const t = readEntry();
      for (const n of NORMS) {
        assert.ok(t.includes(n), `規範「${n}」が入口から消えた（IADR-0173 決定 2 に反する）`);
      }
    });

    ok('#691 段 3: スタブが別紙を指し、別紙に経緯が移っている', () => {
      const t = readEntry();
      assert.match(t, /cross-project-id-refs-annex\.md/, '別紙への導線が無い');
      const a = fs.readFileSync(path.join(REPO, ANNEX), 'utf8');
      assert.match(a, /いつ読むか/, '別紙に「いつ読むか」が無い');
      assert.match(a, /計画大改定/, '別紙に経緯（計画大改定の重なり方）が無い');
      assert.match(a, /自動リンク/, '別紙に実測（自動リンクの効く面）が無い');
    });

    // ★★ 「入口だけでは 50KB に届かない」という結論。落ちると
    //    「段 4 まででよい」という誤りへ戻り、CLAUDE.md の減量が計画から抜ける。
    ok('#691 段 3: CLAUDE.md の減量が必須だと ADR に書いてある', () => {
      const t = fs.readFileSync(path.join(REPO, PLAN_ADR), 'utf8');
      assert.match(t, /CLAUDE\.md/, 'CLAUDE.md への言及が無い');
      assert.match(t, /確定事項/, '「想定 → 確定事項」への格上げが書かれていない');
      assert.match(t, /段 4 の総括/, '「段 4 の総括まで CLAUDE.md に手を付けない」が無い');
    });

    // ★ #690 の反省。ADR が可変の数値を断定すると、その ADR 自身の編集で古くなる。
    ok('#691 段 3: ADR が可変の数値ではなく測り方を書いている', () => {
      const t = fs.readFileSync(path.join(REPO, PLAN_ADR), 'utf8');
      assert.match(t, /現在値の出し方/, '測り方（コマンド）が書かれていない');
      assert.match(t, /基準コミット/, '数値を書くときの基準明記が書かれていない');
    });
  }

  // --- #689: 必読規約の減量 段 2（IADR-0174） ------------------------------------
  //
  // ★ 段 1（#686）と同じ型。**確定済み引用が 0 件でも見出しは残す**（IADR-0173 決定 1 に
  //   例外を作らない）。**「今は引かれていない」は「今後も引かれない」ではない。**
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const ENTRY = '.claude/rules/traceability.md';
    // ★ #755 / IADR-0201: 入口はキット配布物（分類 A・バイト一致）と companion の 2 ファイルになった。
    //   本リポ固有の規範は companion に在るため、「入口に残っている」は 2 ファイルの連結で見る。
    //   「入口から出した」の否定は本リポが書く companion 側で見る（キット配布物の文言は本リポの管理外）。
    const ENTRY_REPO = '.claude/rules/traceability.repo.md';
    const readEntry = () => fs.readFileSync(path.join(REPO, ENTRY), 'utf8') + '\n' + fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const readRepoEntry = () => fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const ANNEX = 'docs/how-to/changelog-overrides-annex.md';
    const PLAN_ADR = '.ai-context/adr/IADR-0174_slimming-projection-requires-stage4.md';
    const MOVED = 'CHANGELOG 生成時の誤記補正・除外規定';

    // ★ 見出し行だけを見る（#686 の M1 の教訓。スタブ本文が同じ文字列を含むため全文検索は空振る）。
    ok('#689 段 2: CHANGELOG 節の見出しがスタブとして入口に残っている', () => {
      const t = readEntry().replace(/\r\n/g, '\n');
      const headings = t
        .split('\n')
        .filter((l) => /^#{2,3}\s/.test(l))
        .map((l) => l.replace(/^#{2,3}\s*/, '').trim());
      assert.ok(
        headings.some((x) => x.startsWith(MOVED)),
        `見出し「${MOVED}」が入口から消えた。確定済み引用が 0 件でも残す（IADR-0173 決定 1）`,
      );
    });

    // ★★ 規範は入口に残す。**一般則（CLAUDE.md の「破壊的な git 操作は行わない」）とは別物**で、
    //    こちらは「誤記があっても直さない」という**例外の否定**である。
    //    これが消えると「誤記の是正は正当な理由だ」と読まれうる。
    ok('#689 段 2: 「履歴は書き換えない」の規範が入口に残っている', () => {
      const t = readEntry();
      assert.match(t, /既存の git 履歴を書き換えないこと/, '規範（履歴不変）が入口から消えた');
      assert.match(t, /force push で修正してはならない/, '「誤記があっても直さない」が入口から消えた');
    });

    ok('#689 段 2: スタブが別紙を指し、別紙に本文が移っている', () => {
      const t = readEntry();
      assert.match(t, /changelog-overrides-annex\.md/, '別紙への導線が無い');
      const a = fs.readFileSync(path.join(REPO, ANNEX), 'utf8');
      assert.match(a, /いつ読むか/, '別紙に「いつ読むか」が無い');
      assert.match(a, /action.*remap/s, '別紙に仕組みの本文（remap）が無い');
      assert.match(a, /exclude/, '別紙に仕組みの本文（exclude）が無い');
    });

    // ★ 計画の見込みを引き直した結論。落ちると「段 3 まででよい」という誤りへ戻る。
    ok('#689 段 2: 段 3 まででは 50KB に届かないことが ADR に書いてある', () => {
      const t = fs.readFileSync(path.join(REPO, PLAN_ADR), 'utf8');
      assert.match(t, /段 4/, '段 4 への言及が無い');
      assert.match(t, /届かない|あと約/, '「段 3 まででは届かない」という結論が無い');
    });
  }

  // --- #686: 必読規約の減量 段 1（IADR-0173） ------------------------------------
  //
  // ★★ **機械はこの型を検出しない。** 節名ごと消してもリンクは切れず、
  //   `check-doc-links.js` は緑のまま通る。**確定済み記録は「ファイル ＋ 節名」で規約を引いており**
  //   （実測 43 件・うち確定済み 18 件）、**節名が消えると引用先が消える**が、
  //   `IADR-0166` により**引用側は書き換えられない**。だからスタブを固定する。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const ENTRY = '.claude/rules/traceability.md';
    // ★ #755 / IADR-0201: 入口はキット配布物（分類 A・バイト一致）と companion の 2 ファイルになった。
    //   本リポ固有の規範は companion に在るため、「入口に残っている」は 2 ファイルの連結で見る。
    //   「入口から出した」の否定は本リポが書く companion 側で見る（キット配布物の文言は本リポの管理外）。
    const ENTRY_REPO = '.claude/rules/traceability.repo.md';
    const readEntry = () => fs.readFileSync(path.join(REPO, ENTRY), 'utf8') + '\n' + fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const readRepoEntry = () => fs.readFileSync(path.join(REPO, ENTRY_REPO), 'utf8');
    const ANNEX = 'docs/how-to/commit-message-rules-annex.md';
    // 別紙へ出した節（見出しはスタブとして入口に残す）
    const MOVED = ['PR タイトル（スカッシュ後件名）の検査', '検査対象から除外する自動コミット'];
    // 入口に残す規範（別紙へ出すと「読まなかっただけで CI に落ちる」。IADR-0173 決定 2）
    const KEPT = ['採番衝突時の改番手順', 'クロスリポジトリの issue / PR 番号の修飾'];

    ok('#686 段 1: 入口のパスが変わっていない', () => {
      assert.ok(fs.existsSync(path.join(REPO, ENTRY)), `${ENTRY} が無い（IADR-0172 決定 2 に反する）`);
    });

    // ★ 確定済み記録（IADR-0145:26 等）が節名で引いているため、見出しは消せない。
    //
    // ★★ **見出し行そのものを見る。全文検索では駄目である。**
    //    スタブの本文に「§PR タイトル（スカッシュ後件名）の検査（再発防止）へ移した」と
    //    **同じ文字列が入っている**ため、`t.includes(見出し)` だと**見出し行を消しても通る**
    //    （変異試験 M1 が実際に緑のまま通り、この穴を捕まえた）。
    ok('#686 段 1: 出した節の見出しがスタブとして入口に残っている', () => {
      const t = readEntry().replace(/\r\n/g, '\n');
      const headings = t
        .split('\n')
        .filter((l) => /^#{2,3}\s/.test(l))
        .map((l) => l.replace(/^#{2,3}\s*/, '').trim());
      for (const h of MOVED) {
        assert.ok(
          headings.some((x) => x.startsWith(h)),
          `見出し「${h}」が入口から消えた（確定済み記録の引用が指す先を壊す）。` +
            `現在の見出し: ${JSON.stringify(headings)}`,
        );
      }
    });

    // ★ 導線が無いと統制が消える（IADR-0172 決定 4）。スタブが別紙を指していること。
    ok('#686 段 1: スタブが別紙を指している（導線が残っている）', () => {
      const t = readEntry();
      const n = t.split('commit-message-rules-annex.md').length - 1;
      assert.ok(n >= MOVED.length, `別紙への導線が ${n} 件しかない（出した節は ${MOVED.length} 件）`);
      assert.ok(fs.existsSync(path.join(REPO, ANNEX)), `別紙 ${ANNEX} が無い`);
    });

    // ★ 規範を別紙へ出していないこと。出すと「別紙を読まなかった」だけで CI に落ちる。
    ok('#686 段 1: 規範は入口に残っている（本文ごと）', () => {
      const t = readEntry();
      for (const h of KEPT) {
        assert.ok(t.includes(h), `規範「${h}」が入口から消えた`);
      }
      // 前文の規範（起点 ID の書式）が本文として残っていること。
      assert.match(t, /許可する種別/, '節の前文（許可する種別）が入口から消えた');
    });

    // ★ 別紙の本文が実際に移っていること（スタブだけ作って中身が無い、を防ぐ）。
    ok('#686 段 1: 別紙に本文が移っている', () => {
      const a = fs.readFileSync(path.join(REPO, ANNEX), 'utf8');
      for (const h of MOVED) {
        assert.ok(a.includes(h), `別紙に「${h}」が無い`);
      }
      assert.match(a, /いつ読むか/, '別紙に「いつ読むか」が無い（参照時に読む別紙の要件）');
      assert.ok(a.length > 5000, `別紙が小さすぎる（${a.length}）。本文が移っていない可能性`);
    });
  }

  // --- #684: 必読規約の減量計画（IADR-0172） ------------------------------------
  //
  // ★ **計画は文書にしか無い。消えても CI は赤くならない**（#546 / #665 / #587 と同じ型）。
  //   固定するのは**計画の要 3 点**であって、文言の丸写しではない。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const PLAN_ADR = '.ai-context/adr/IADR-0172_required-rules-slimming-plan.md';
    const RULES = '.claude/rules/traceability.md';

    // ★★ 最重要。**入口のパスを変えられない**という制約が計画の土台である。
    //   確定済みの記録（.ai-context/specs/ 62 件・.ai-context/adr/ 19 件）からリンクが張られており、
    //   それらは IADR-0166 と .claude/rules/traceability.md 自身が書き換えを禁じている。
    //   **この制約が落ちると「分割すればよい」という誤った計画へ戻る。**
    ok('#684 計画: 入口ファイルのパスを変えないことが書いてある', () => {
      const t = fs.readFileSync(path.join(REPO, PLAN_ADR), 'utf8');
      assert.match(t, /パスは変えない/, '「パスは変えない」という決定が無い');
      assert.match(t, /確定済み/, 'パスを変えられない理由（確定済み記録からのリンク）が無い');
    });

    // ★ 「機械が強制しているから読まなくてよい」は、**機械が止める範囲でしか成り立たない**。
    //   採番衝突時の改番手順は機械が無い（check-adr-numbering.js は欠番・重複しか見ない）。
    ok('#684 計画: 機械が強制していない規範を入口へ残すと書いてある', () => {
      const t = fs.readFileSync(path.join(REPO, PLAN_ADR), 'utf8');
      assert.match(t, /採番衝突時の改番手順/, '機械が無い規範（採番衝突時の改番手順）の特定が無い');
      assert.match(t, /導線/, '別紙化する場合の導線への言及が無い');
    });

    // ★★ **区分を粗く取ると後段が索引更新まで避ける。**
    //   `.ai-context/adr/` には**確定済みの IADR 本体**と**都度更新する索引（README.md）**が混在する。
    //   初版は `.ai-context/adr/` を一律「不可」とし、確定済みリンクを 22 件（.ai-context/specs/ の分だけ）と
    //   過小に数えていた。**正しくは 22 ＋ IADR 本体 6 ＝ 28 件**であり、
    //   **索引の 1 件は可変なので数に入れない**（レビュー指摘の「29 件」は索引を含めた数である）。
    ok('#684 計画: 索引（.ai-context/adr/README.md）が可変だと区別されている', () => {
      const t = fs.readFileSync(path.join(REPO, PLAN_ADR), 'utf8');
      assert.match(t, /README\.md/, '索引（.ai-context/adr/README.md）への言及が無い');
      assert.match(
        t,
        /索引[^\n]*可|可[^\n]*索引/,
        '索引が「可変」であることが区別されていない。後段が索引更新まで避ける',
      );
      assert.match(t, /28 件/, '書き換えられないリンク数（28 件）が書かれていない');
    });

    // ★★ **索引の 1 行要約に件数を書かない。** 本体と索引の 2 箇所へ同じ数を書くと
    //   **片方が黙って古くなる**（[[IADR-0141]]「参照点を 1 つに畳む」）。実際に起きた ——
    //   本体と作業仕様書の「22 件」を「28 件」へ直したとき、**索引だけ 22 件のまま残った**。
    //   `check-doc-links.js` はリンク切れしか見ないので機械では捕まらない。
    //   **同期し直すのではなく、重複そのものを消した**（#679 で 3 回踏んだ末に採った手）。
    ok('#684 計画: ADR 索引の要約が件数を持たない（本体と二重に持たない）', () => {
      const idx = fs.readFileSync(path.join(REPO, '.ai-context/adr/README.md'), 'utf8');
      const row = idx.split('\n').find((l) => l.includes('IADR-0172_required-rules-slimming-plan.md'));
      assert.ok(row, 'IADR-0172 の索引行が無い');
      assert.doesNotMatch(
        row,
        /\d+\s*件/,
        '索引の要約が件数を持っている。本体と二重に持つと片方が古くなる（IADR-0141）',
      );
    });

    // ★ 事実と違うことを書かないための門。別紙化は「読む量」を減らすのであって
    //   「総量」は減らさない。**後続の PR がここを踏み外しやすい。**
    ok('#684 計画: 「総量は減らない」という限界が明記されている', () => {
      const t = fs.readFileSync(path.join(REPO, PLAN_ADR), 'utf8');
      assert.match(t, /総量は減らない/, '別紙化の限界（総量は減らない）が明記されていない');
    });

    // ★ 入口が「常時適用」でなくなると、計画の前提（毎セッション必読の集合）が崩れる。
    //   **本 PR は 1 行も移動しないので、ここは現状の固定である。**
    ok('#684 計画: 入口ファイルが現状どおり常時適用である', () => {
      const t = fs.readFileSync(path.join(REPO, RULES), 'utf8').replace(/\r\n/g, '\n');
      assert.ok(t.startsWith('---\n'), 'frontmatter が無い');
      const end = t.indexOf('\n---', 3);
      assert.ok(end !== -1, 'frontmatter が閉じていない');
      assert.match(
        t.slice(0, end),
        /paths:\s*\n\s*-\s*["']\*\*\/\*["']/,
        '入口の paths が "**/*"（常時適用）でない。計画の前提が変わっている',
      );
    });
  }

  // --- #626: 逆リンク義務の向き（IADR-0171） ------------------------------------
  //
  // ★ 裁定（2026-08-11・案 A）: 「相互リンク」の義務は**仕様書側の一方向**であり、
  //   ADR 側に逆リンクを張る義務は無い。実測 283 対（広い軸で 606 対）は**欠陥ではない**。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const read = (rel) => fs.readFileSync(path.join(REPO, rel), 'utf8');

    ok('逆リンク義務: 向きが docs/README.md（正本）に書かれている', () => {
      const t = read('docs/README.md');
      assert.match(t, /リンクの義務は仕様書側の一方向/, 'docs/README.md に向きが書かれていない');
      assert.match(t, /IADR-0171/, '裁定の所在（IADR-0171）が示されていない');
    });

    ok('逆リンク義務: CLAUDE.md は正本を指すだけで、理由を複写していない', () => {
      const t = read('CLAUDE.md');
      assert.match(t, /一方向/, 'CLAUDE.md が向きに触れていない');
      assert.match(t, /docs\/README\.md/, 'CLAUDE.md が正本を指していない');
      // ★★ 理由（ADR が更新履歴の索引になる）は**正本にだけ**置く。
      //   2 箇所へ同じ説明を書くと片方が黙って古くなる（[[IADR-0141]]。#583 で 3 回踏んだ）。
      assert.doesNotMatch(
        t,
        /更新履歴の索引/,
        'CLAUDE.md に理由が複写されている（説明の正本は docs/README.md 運用ルール 4 の 1 箇所に畳む）',
      );
    });
  }

  // --- #716: 脆弱な推移依存のピン（IADR-0186） ---------------------------------
  //
  // ★ 同型 2 回目なので検査にした（CLAUDE.md「検査器の追加は同型の事故が 2 回起きたら」）。
  //   1 回目: Microsoft.OpenApi（#61 / #80。NU1903 GHSA-v5pm-xwqc-g5wc）
  //   2 回目: SSH.NET（#716。GHSA-q939-rpr3-3284。Testcontainers の推移依存）
  // ★ ピンが黙って消えると脆弱性が再混入する。**しかも再混入に気づけるかは advisory feed 次第**で、
  //   `dotnet list package --vulnerable` が鳴るまで分からない。ピンの存在をここで固定する。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const PROPS = path.join(REPO, 'src/Directory.Packages.props');

    /** `<PackageVersion Include="X" Version="Y" />` から Y を返す。無ければ null。 */
    const pinnedVersion = (xml, id) => {
      const re = new RegExp(
        `<PackageVersion\\s+Include="${id.replace(/\./g, '\\.')}"\\s+Version="([^"]+)"`,
      );
      const m = re.exec(xml);
      return m ? m[1] : null;
    };

    /** "2026.0.0" → [2026,0,0]。数値比較用（文字列比較だと 2026.0.0 < 2025.1.0 になり得る）。 */
    const parts = (v) => v.split('.').map((n) => Number.parseInt(n, 10) || 0);
    const gte = (a, b) => {
      const [x, y] = [parts(a), parts(b)];
      for (let i = 0; i < Math.max(x.length, y.length); i++) {
        const d = (x[i] || 0) - (y[i] || 0);
        if (d !== 0) return d > 0;
      }
      return true;
    };

    ok('#716: 推移ピンの前提（CentralPackageTransitivePinningEnabled）が有効である', () => {
      const xml = fs.readFileSync(PROPS, 'utf8');
      // ★ これが false になると、SSH.NET / Microsoft.OpenApi のピンが**黙って効かなくなる**
      //   （PackageVersion は残るのに推移依存へ適用されない）。前提ごと固定する。
      assert.match(
        xml,
        /<CentralPackageTransitivePinningEnabled>true<\/CentralPackageTransitivePinningEnabled>/,
        '推移ピンが無効化された（脆弱な推移依存をパッチ版へ固定できなくなる）',
      );
    });

    ok('#716: 脆弱な推移依存が修正版以上へピンされている', () => {
      const xml = fs.readFileSync(PROPS, 'utf8');
      // [パッケージ, 修正版の下限, 出所]
      const PINS = [
        ['SSH.NET', '2026.0.0', '#716 / GHSA-q939-rpr3-3284 (High)'],
        ['Microsoft.OpenApi', '2.7.5', '#61 / #80 / GHSA-v5pm-xwqc-g5wc (High)'],
      ];
      const bad = [];
      for (const [id, floor, src] of PINS) {
        const v = pinnedVersion(xml, id);
        if (v === null) {
          bad.push(`${id}: ピンが消えた（${src}）`);
        } else if (!gte(v, floor)) {
          // ★ 下限は数値比較する。文字列比較だと "2026.0.0" < "2025.1.0" のような取り違えが起きる。
          bad.push(`${id}: ${v} は修正版 ${floor} 未満（${src}）`);
        }
      }
      assert.deepStrictEqual(
        bad,
        [],
        `脆弱な推移依存のピンが失われた:\n  ${bad.join('\n  ')}`,
      );
    });

    ok('#716: SSH.NET を直接参照するプロジェクトは無い（推移ピンである前提）', () => {
      // ★ 直接参照が生まれたら、ピンではなく通常の依存として管理する話になる（前提が変わる）。
      //   `Directory.Packages.props` 自身は版定義を持つので対象外。
      const { execFileSync } = require('child_process');
      let out = '';
      try {
        out = execFileSync('git', ['grep', '-l', 'SSH.NET', '--', '*.csproj'], {
          cwd: REPO,
          encoding: 'utf8',
        });
      } catch {
        out = ''; // git grep はヒット 0 件で exit 1
      }
      assert.strictEqual(
        out.trim(),
        '',
        `SSH.NET を直接参照する csproj ができた（推移ピンの前提が変わる）:\n${out}`,
      );
    });

  }

  // --- #755: 計画 pin 4d6a7d6 の追随（IADR-0200 / IADR-0201。#751 を束ねた） ---------
  //
  // ★ 必読規約の母集合の検査器（合算しない・出典つきの予算値）を固定する。
  //   ★ ADR-0048 決定 2・決定 6（planning 依存の全撤去・kit 同期検査の退役）で、同じ #755 コミットが
  //   足した他の型（キット版 check-kit-sync.js のパス正規化・companion 分離の分類 A 判定・
  //   doc-links-planning.yml 配線）は検査器ごと退役した。
  {
    const fs = require('fs');
    const path = require('path');
    const { spawnSync } = require('child_process');
    const REPO = path.join(__dirname, '..');
    ok('#755: check-reading-budget は集合ごとに判定し、予算値を出典つきで持つ', () => {
      const rb = require('./check-reading-budget.js');
      assert.strictEqual(rb.BUDGET_BYTES, 51200, '予算値が計画リポ運用ガイド §8 の 51,200 と違う（複製は正本と同じ値を持つ）');
      // ★ #853: 下限を緩めると #730 と #790/#793 の両方が同時に黙るため、値をここでも固定する。
      assert.strictEqual(rb.MARGIN_FLOOR_BYTES, 1000, '余白の下限が #730 / [[IADR-0190]] の 1,000 と違う');
      const src = fs.readFileSync(path.join(REPO, 'scripts/check-reading-budget.js'), 'utf8');
      assert.match(src, /ai-implementation-workflow-guide\.md/, '予算値の出典（計画リポ運用ガイド）がソースに無い');
      const claude = rb.AGENT_SETS.find((x) => x.name === 'Claude Code');
      assert.ok(claude && claude.globDirs.includes('.claude/rules'), 'Claude Code の集合が .claude/rules を走査していない');
      assert.ok(!claude.files.includes('AGENTS.md'), 'AGENTS.md が Claude Code の集合に合算されている');
      const r = spawnSync(process.execPath, [path.join(REPO, 'scripts/check-reading-budget.js'), '--self-test'], { encoding: 'utf8' });
      assert.strictEqual(r.status, 0, `--self-test が失敗した:\n${r.stdout}\n${r.stderr}`);
      // 実データ: warn 帯（90% 以上）でも exit 0（warn は失敗にしない）
      const real = spawnSync(process.execPath, [path.join(REPO, 'scripts/check-reading-budget.js')], { encoding: 'utf8' });
      assert.strictEqual(real.status, 0, `実データで落ちた:\n${real.stdout}\n${real.stderr}`);
      assert.match(real.stdout, /Claude Code: [\d,]+ バイト/, '集合ごとの実測が出力に無い');
    });
  }

  // --- NFR / #747: submodule の bump でフロント CI が起動すること -----------------
  //
  // ★ 起点 ID は**無採番の `NFR`** である（`.claude/rules/traceability.md` の例外 2）。
  //   本件は CI の起動条件という**工程の統制**であり、計画側の非機能要件表（`NFR-01`〜`NFR-27`）に
  //   当たる番号が無い。近い番号を無理に当てない。ワークフロー側のコメントと同じ ID を使う。
  //
  // ★ 「paths: の取りこぼしで検査が静かに素通りする」型は **3 件目**である。
  //     1 件目 = #558（frontend-tests.yml に契約と生成の設定が無く、契約だけの PR で
  //              カバレッジ床の検査が起動しなかった）
  //     2 件目 = #562（整形ゲートの設定 .prettierrc.json / .prettierignore が paths: に無く、
  //              単独変更で CI が走らなかった）
  //     3 件目 = #747（AST submodule の bump が src/*/frontend/** に一致せず、3 回素通りして
  //              初期ロードが +35.51 kB 増えた）
  //   CLAUDE.md「検査器・規約の追加は同型の事故が 2 回起きたら」の条件を満たす。
  //
  // ★ 期待値は .gitmodules から**導出**する（列挙を書き写さない）。src/ 配下へ submodule を
  //   足したときも自動で赤くなる。paths: は glob で gitlink を表現できないため、checkout 側の
  //   汎用形（.gitmodules の総なめ）と違い手で足す必要があるからである。
  //
  // ★ 上の #705（`:4300`）の「paths: の側は検査器にしない」とは**射程が違う**。あちらは
  //   `paths:` を**持つこと自体を禁じない**（frontend.yml は意図して持ち、required にしない運用で
  //   正しい）という宣言であり、本検査は `paths:` を持つ前提で**その列挙に src/ 配下の gitlink が
  //   入っているか**だけを見る。存在の禁止 ≠ 列挙の要求であり、`paths:` の有無・required 化の
  //   可否には一切触れない。
  {
    const fs = require('fs');
    const path = require('path');
    const REPO = path.join(__dirname, '..');
    const FRONTEND_WORKFLOWS = ['frontend.yml', 'frontend-tests.yml'];

    // `on:` 直下の push / pull_request それぞれの paths: ブロックの値を返す。
    const pathsOf = (yml, event) => {
      const m = yml.match(new RegExp(`^\\s{2}${event}:\\s*$\\n((?:\\s{4}.*\\n|\\s*\\n)*)`, 'm'));
      if (!m) return null;
      const block = m[1].match(/^\s{4}paths:\s*$\n((?:\s{6}.*\n|\s*\n)*)/m);
      if (!block) return null;
      return block[1]
        .split('\n')
        .map((l) => l.match(/^\s{6}-\s*"?([^"#]+?)"?\s*$/))
        .filter(Boolean)
        .map((m2) => m2[1]);
    };

    ok('NFR / #747: .gitmodules の src/ 配下 submodule がフロント CI の paths: に全て挙がっている', () => {
      const gitmodules = fs.readFileSync(path.join(REPO, '.gitmodules'), 'utf8');
      const submodules = [...gitmodules.matchAll(/^\s*path\s*=\s*(src\/\S+)\s*$/gm)].map((m) => m[1]);
      // 走査 0 件で静かに緑を返す形を塞ぐ（#664 / PR #672 の型）。
      assert.ok(submodules.length >= 1, `.gitmodules から src/ 配下の submodule を読めない（走査が壊れている）`);

      const missing = [];
      let checked = 0;
      for (const f of FRONTEND_WORKFLOWS) {
        const yml = fs.readFileSync(path.join(REPO, '.github/workflows', f), 'utf8');
        for (const event of ['push', 'pull_request']) {
          const paths = pathsOf(yml, event);
          assert.ok(paths && paths.length > 0, `${f}: ${event}.paths を読めない（パーサが壊れている）`);
          checked += 1;
          for (const sub of submodules) {
            // 末尾に /** を付けた形は gitlink 1 エントリに一致しない（bump を取りこぼす）。
            if (!paths.includes(sub)) missing.push(`${f}: ${event}.paths に "${sub}" が無い`);
            if (paths.includes(`${sub}/**`)) {
              missing.push(`${f}: ${event}.paths の "${sub}/**" は gitlink に一致しない（/** を外す）`);
            }
          }
        }
      }
      assert.strictEqual(checked, 4, `paths: を持つトリガが ${checked} 箇所（push / pull_request の 4 箇所のはず）`);
      assert.deepStrictEqual(
        missing,
        [],
        'submodule の bump でフロント CI が起動しない（初期ロードの ratchet が素通りする。#747）:\n  ' +
          missing.join('\n  '),
      );
    });

    // --- NFR / #801: vitest の test.include ⊆ frontend-tests.yml の paths: -----------
    //
    // ★ 起点 ID は**無採番の `NFR`**（上の #747 節と同じ理由。CI の起動条件という工程の統制であり、
    //   計画側の非機能要件表に当たる番号が無い）。雛形そのものの根拠は `FR-14` / `IADR-0060`。
    //
    // ★ 「paths: の取りこぼしで検査が静かに素通りする」型は**着地日順に 4 件目**である
    //     1 件目 = #562（`ce96eb81` / 2026-08-08。整形ゲートの設定が paths: に無く、単独変更で走らなかった）
    //     2 件目 = #558（`4dbd5010` / 2026-08-10。契約と生成の設定が frontend-tests.yml に無かった）
    //     3 件目 = #747（`3cf2437a` / 2026-08-15。AST submodule の gitlink が一致せず 3 回素通りした）
    //     4 件目 = #801（本節。test.include が雛形のテストを収集するのに paths: が拾わない）
    //   上の #747 節のコメントは同じ 3 件を **issue 番号順**（1=#558 / 2=#562）で並べている。
    //   **集合は同一で、違うのは先頭 2 件の並びだけ**である（ここは是正が着地した日付順）。
    //   CLAUDE.md「検査器・規約の追加は同型の事故が 2 回起きたら」の条件を満たす。
    //   **#747 の検査器は .gitmodules の gitlink しか見ておらず、本件は素通りする**ので別の不変条件を置く。
    //
    // ★★ **不変条件は「test.include ⊆ frontend-tests.yml の paths:」であって、
    //     2 本のワークフローの paths: の「対称性」ではない。**
    //   `.ai-context/specs/20260810_issue-558_carried-debt.md` が非対称を全数で測り、
    //   **`src/.prettierrc.json` / `src/.prettierignore` / `src/lingui.config.ts` の 3 件を
    //   理由つきで意図的に残している** —— `frontend.yml` は lint / format / build / e2e を担い、
    //   `frontend-tests.yml` は **`pnpm run test:coverage` しか走らせない**。整形設定も
    //   `lingui.config.ts` も `test:coverage` の結果を変えないので、足すと
    //   **何も新しく確かめられないジョブが起動して CI 時間だけが伸びる**。
    //   `src/eslint.templates.config.js` も同じ理由で `frontend-tests.yml` には無い。
    //   **対称性を検査にすると、この 4 件を誤検出する。** だから包含だけを見る。
    //
    // ★ 方式は「**代表パス合成**」であり、実ファイル突合ではない。
    //   実ファイル（`git ls-files`）に依存すると、**submodule の `src/ai-stock-trading` は 0 件しか
    //   出ないため AST の include が空走査で静かに緑になる**（#664 / PR #672 が扱った fail-open の
    //   新設に当たる）。代表パスなら populate の有無に関わらず同じ判定になる。
    //
    // ★ **fail-closed の門**を 3 つ置く（IADR-0183「偽の緑」。IADR-0209 決定 4 が正本）。
    //   ① test.include 節を読めない ② include の抽出が 0 件 ③ paths: が読めない／0 件 ——
    //   いずれも throw する。正規表現が壊れたときに「0 件検査して緑」を返さない。
    //   ［2026-08-16 訂正 / 波 7 末クロス監査］本コメントは「2 つ」と書いていたが実装も
    //   IADR-0209 決定 4 も **3 つ**である（門 ① を数え落としていた）。コメントだけが古かった。
    //
    // ★ glob → RegExp は素の Node で自作する。本リポには `package.json` も `node_modules` も無く
    //   `minimatch` 等を使えない。`**`（`/` を跨ぐ）/ `*`（跨がない）/ `{a,b}` を扱えれば足りる。

    /** glob を RegExp へ変換する。`**` は `/` を跨ぎ、`*` は跨がない。`{a,b}` は選択。 */
    const globToRegExp = (glob) => {
      let re = '';
      for (let i = 0; i < glob.length; i += 1) {
        const c = glob[i];
        if (c === '*') {
          if (glob[i + 1] === '*') {
            i += 1;
            if (glob[i + 1] === '/') {
              i += 1;
              re += '(?:.*/)?'; // `**/` は 0 階層にも一致する
            } else {
              re += '.*';
            }
          } else {
            re += '[^/]*';
          }
        } else if (c === '{') {
          const end = glob.indexOf('}', i);
          assert.ok(end > i, `glob の { が閉じていない: ${glob}`);
          const alts = glob.slice(i + 1, end).split(',');
          re += `(?:${alts.map((a) => a.replace(/[\\^$+?.()|[\]{}*]/g, '\\$&')).join('|')})`;
          i = end;
        } else if ('\\^$+?.()|[]'.includes(c)) {
          re += `\\${c}`;
        } else {
          re += c;
        }
      }
      return new RegExp(`^${re}$`);
    };

    /** glob から代表パスを機械合成する（`**` → `a/b`、`*` → `a`、`{x,y}` → `x`）。 */
    const representativePath = (glob) =>
      glob
        .replace(/\{([^}]*)\}/g, (_m, alts) => alts.split(',')[0])
        .replace(/\*\*/g, 'a/b')
        .replace(/\*/g, 'a');

    ok('NFR / #801: vitest の test.include が拾うパスは frontend-tests.yml の paths: にも載る', () => {
      const cfg = fs.readFileSync(path.join(REPO, 'src/vitest.config.ts'), 'utf8');
      // `  test: {` を起点に、**4 スペース**の `include: [` を非貪欲で取る。
      // `coverage.include` は 6 スペース（1 段深い）なので取り違えない。
      const block = cfg.match(/\n {2}test: \{\n[\s\S]*?\n {4}include: \[\n([\s\S]*?)\n {4}\],/);
      // fail-closed の門 ①: 節が読めない＝正規表現が腐った。0 件検査で緑を返さない。
      if (!block) {
        throw new Error(
          'src/vitest.config.ts の test.include 節を読めない（抽出の正規表現が壊れている）。' +
            '0 件検査で緑を返さないため fail させる',
        );
      }
      const includes = block[1]
        .split('\n')
        .map((l) => l.match(/^\s*'([^']+)',?\s*$/))
        .filter(Boolean)
        .map((m) => m[1]);
      // fail-closed の門 ②: 1 件も取れないのは行の書式が変わった証拠である。
      if (includes.length === 0) {
        throw new Error('src/vitest.config.ts の test.include から glob を 1 件も取れない（走査が壊れている）');
      }

      // vite root は `src/` なので、リポジトリルート相対へ正規化する
      // （`../templates/...` → `templates/...`、それ以外は `src/` を前置）。
      const toRepoRelative = (glob) => {
        const p = path.posix.normalize(path.posix.join('src', glob));
        assert.ok(!p.startsWith('../'), `test.include がリポジトリ外を指している: ${glob}`);
        return p;
      };

      const yml = fs.readFileSync(path.join(REPO, '.github/workflows/frontend-tests.yml'), 'utf8');
      const missing = [];
      let checked = 0;
      // **push と pull_request を別々に見る**（片方だけ足す事故を止める）。
      for (const event of ['push', 'pull_request']) {
        const paths = pathsOf(yml, event);
        // fail-closed の門 ③: paths: が読めない／0 件なら throw（#747 節と同じ扱い）。
        if (!paths || paths.length === 0) {
          throw new Error(`frontend-tests.yml: ${event}.paths を読めない（パーサが壊れている）`);
        }
        const matchers = paths.map(globToRegExp);
        checked += 1;
        for (const glob of includes) {
          const rel = toRepoRelative(glob);
          const sample = representativePath(rel);
          if (!matchers.some((re) => re.test(sample))) {
            missing.push(
              `frontend-tests.yml: ${event}.paths が test.include "${glob}" を拾わない` +
                `（代表パス "${sample}"）`,
            );
          }
        }
      }
      assert.strictEqual(checked, 2, `paths: を持つトリガが ${checked} 箇所（push / pull_request の 2 箇所のはず）`);
      assert.deepStrictEqual(
        missing,
        [],
        'vitest が収集するのにテストを走らせる CI が起動しない（#801）。' +
          'frontend-tests.yml の push / pull_request の**両方**の paths: へ足すこと:\n  ' +
          missing.join('\n  '),
      );
    });

    // --- NFR / IADR-0214: ゲートが読むファイル ⊆ そのゲートを走らせる workflow の paths: -----
    //
    // ★ 起点 ID は**無採番の `NFR`**（上の #747 / #801 節と同じ理由。CI の起動条件という工程の
    //   統制であり、計画側の非機能要件表〔`NFR-01`〜`NFR-27`〕に当たる番号が無い。IADR-0179 決定 1）。
    //
    // ★ 「paths: の取りこぼしで検査が静かに素通りする」型は**着地日順に 5 件目**である。
    //     1 件目 = #562（`ce96eb81` / 2026-08-08。整形ゲートの設定が paths: に無かった）
    //     2 件目 = #558（`4dbd5010` / 2026-08-10。契約と生成の設定が frontend-tests.yml に無かった）
    //     3 件目 = #747（`3cf2437a` / 2026-08-15。AST submodule の gitlink が一致せず 3 回素通りした）
    //     4 件目 = #801（`49ec8e32` / 2026-08-16。test.include が拾う雛形を paths: が拾わなかった）
    //     5 件目 = 本節（`f423ca4e` / 2026-08-16。Knip ゲートの**入力**——床 knip-baseline.json と
    //              検査器本体 check-knip.js——が frontend.yml の paths: に無い。**4 件目と同じ波で
    //              作り込んだ**。床だけを 18 → 60 に緩める PR では、ゲートが 1 度も起動しない）。
    //   **上の 2 つの検査器は本件を素通りする** —— #747 は .gitmodules の gitlink しか見ず、
    //   #801 は vitest の test.include しか見ない。よって同じ場所へ**3 本目の不変条件**を置く。
    //
    // ★★ **不変条件は「ゲートが読むファイル ⊆ そのゲートを走らせるワークフローの paths:」である。**
    //   #801（IADR-0209）が「**走らせる対象**（テストファイル）」を見るのに対し、ここは
    //   「**検査器が読む入力**（床・設定・検査器本体）」を見る。族は同じで対象が違う。
    //
    // ★ **対象ゲートも入力ファイルもハードコードしない。**
    //   - ゲートの一覧は**ワークフローの run: から導く**（`node scripts/<name>.js`）。
    //   - 入力ファイルは**検査器のソースから静的に導く**（`path.join` / `path.resolve` の
    //     リテラルと既知の定数だけで組まれた式を解決し、実在するファイルだけを残す）。
    //     検査器自身のパスも常に含める（本体を書き換える PR でゲートが起動しないのは同じ穴）。
    //
    // ★ **検出しないこと（意図的な穴。網羅ではない）**
    //   - **`require()` の依存グラフは辿らない。** 辿ると `scripts/lib/ci-annotate.js` のような共有
    //     ライブラリを引き込むが、それらは壊れれば**例外で落ちる**ので「静かに素通りする」型ではない
    //     （回帰は ci.yml の scripts-tests が各検査器の --self-test で見ている）。
    //   - **実行時引数で決まる入力は見えない**（`check-static-egress.js --require <dist>` の走査先等）。
    //   - **変数・テンプレートリテラルで組まれたパスは解決できず、黙って落ちる。**
    //     だから下の fail-closed の門 ② で「式を 1 件も切り出せない」形を止める。
    //
    // ★ **fail-closed の門を 3 つ置く**（IADR-0183「偽の緑」）。
    //   ① ワークフローからゲートを 1 件も取れない ② 本文に path.join( が在るのに式を 1 件も
    //   切り出せない ③ paths: が読めない／0 件 —— いずれも **throw** する。

    /** YAML の行コメント（`#` 以降）を落とす。ゲート抽出がコメント中の例文を拾わないため。 */
    const withoutYamlComments = (yml) =>
      yml
        .split('\n')
        .map((l) => l.replace(/(^|\s)#.*$/, '$1'))
        .join('\n');

    /** ワークフローの `run:` に現れる `node scripts/<name>.js` を全部拾う。 */
    const gateScriptsOf = (yml) => {
      const found = new Set();
      for (const m of withoutYamlComments(yml).matchAll(/\bnode\s+(scripts\/[\w.-]+\.js)\b/g)) {
        found.add(m[1]);
      }
      return [...found].sort();
    };

    /** 引数リストを**トップレベルのカンマ**で割る（入れ子の括弧・文字列の中は割らない）。 */
    const splitTopLevelArgs = (s) => {
      const out = [];
      let depth = 0;
      let cur = '';
      let quote = null;
      for (let i = 0; i < s.length; i += 1) {
        const c = s[i];
        if (quote) {
          cur += c;
          if (c === '\\') {
            cur += s[i + 1] ?? '';
            i += 1;
          } else if (c === quote) quote = null;
          continue;
        }
        if (c === "'" || c === '"' || c === '`') {
          quote = c;
          cur += c;
          continue;
        }
        if ('([{'.includes(c)) depth += 1;
        else if (')]}'.includes(c)) depth -= 1;
        else if (c === ',' && depth === 0) {
          out.push(cur.trim());
          cur = '';
          continue;
        }
        cur += c;
      }
      if (cur.trim() !== '') out.push(cur.trim());
      return out;
    };

    /**
     * 検査器のソースから「**リポジトリ内の実ファイル**を指す path 定数」を静的に導く。
     * 基点は `__dirname`（= `scripts`）。`const NAME = path.join(...)` は解決結果を記号表へ入れ、
     * 後続の式から参照できるようにする（REPO_ROOT / SRC_DIR / BASELINE_PATH … の連鎖を辿るため）。
     * 戻り値の `expressions` は**切り出せた式の数**で、fail-closed の門 ② が使う。
     */
    const repoFilesReadBy = (relScript) => {
      const src = fs.readFileSync(path.join(REPO, relScript), 'utf8');
      const symbols = new Map([['__dirname', path.posix.dirname(relScript)]]);
      const candidates = new Set();
      let expressions = 0;
      const CALL = /(?:const\s+([A-Za-z_$][\w$]*)\s*=\s*)?path\.(?:join|resolve)\(/g;
      let m;
      while ((m = CALL.exec(src)) !== null) {
        const name = m[1];
        const open = CALL.lastIndex - 1;
        // 対応する `)` を探す（文字列の中の括弧は数えない）。
        let depth = 0;
        let end = -1;
        let quote = null;
        for (let i = open; i < src.length; i += 1) {
          const c = src[i];
          if (quote) {
            if (c === '\\') i += 1;
            else if (c === quote) quote = null;
            continue;
          }
          if (c === "'" || c === '"' || c === '`') quote = c;
          else if (c === '(') depth += 1;
          else if (c === ')') {
            depth -= 1;
            if (depth === 0) {
              end = i;
              break;
            }
          }
        }
        if (end < 0) continue;
        expressions += 1;
        const args = splitTopLevelArgs(src.slice(open + 1, end));
        const parts = [];
        let resolvable = args.length > 0;
        for (const arg of args) {
          const lit = arg.match(/^'([^'\\]*)'$/) || arg.match(/^"([^"\\]*)"$/);
          if (lit) {
            parts.push(lit[1]);
            continue;
          }
          if (symbols.has(arg)) {
            parts.push(symbols.get(arg));
            continue;
          }
          resolvable = false; // 変数・関数呼び出し等。**黙って落とす**（門 ② が全滅を止める）。
          break;
        }
        if (!resolvable) continue;
        const rel = path.posix.normalize(path.posix.join(...parts));
        if (rel.startsWith('..')) continue; // リポジトリの外
        if (name) symbols.set(name, rel);
        candidates.add(rel);
      }
      // **検査器自身**も入力である（本体を書き換える PR でゲートが起動しないのは同じ穴）。
      const files = new Set([relScript]);
      for (const rel of candidates) {
        if (rel.split('/').includes('node_modules')) continue; // 生成物。追跡下に無い
        const abs = path.join(REPO, rel);
        if (!fs.existsSync(abs) || !fs.statSync(abs).isFile()) continue; // ディレクトリ・実行時生成物
        files.add(rel);
      }
      return { files: [...files].sort(), expressions, hasPathCall: /path\.(?:join|resolve)\(/.test(src) };
    };

    ok('NFR / IADR-0214: フロント CI のゲートが読むファイルが frontend.yml の paths: に全て載っている', () => {
      const WORKFLOW = 'frontend.yml';
      const yml = fs.readFileSync(path.join(REPO, '.github/workflows', WORKFLOW), 'utf8');

      const gates = gateScriptsOf(yml);
      // fail-closed の門 ①: ゲートを 1 件も取れない＝抽出が腐った。0 件検査で緑を返さない。
      if (gates.length === 0) {
        throw new Error(
          `${WORKFLOW} から "node scripts/*.js" のゲートを 1 件も取れない（抽出が壊れている）。` +
            '0 件検査で緑を返さないため fail させる',
        );
      }

      const inputs = new Map();
      for (const gate of gates) {
        const r = repoFilesReadBy(gate);
        // fail-closed の門 ②: 本文に path.join( が在るのに式を 1 件も切り出せない＝括弧の
        // 対応取り・正規表現が腐った。ここで止めないと「入力が検査器自身だけ」で緑になる。
        if (r.hasPathCall && r.expressions === 0) {
          throw new Error(
            `${gate}: path.join(/path.resolve( が在るのに式を 1 件も切り出せない（抽出が壊れている）`,
          );
        }
        inputs.set(gate, r.files);
      }

      const missing = [];
      let checked = 0;
      // **push と pull_request を別々に見る**（片方だけ足す事故を止める）。
      for (const event of ['push', 'pull_request']) {
        const paths = pathsOf(yml, event);
        // fail-closed の門 ③: paths: が読めない／0 件なら throw（#747 / #801 節と同じ扱い）。
        if (!paths || paths.length === 0) {
          throw new Error(`${WORKFLOW}: ${event}.paths を読めない（パーサが壊れている）`);
        }
        const matchers = paths.map(globToRegExp);
        checked += 1;
        for (const [gate, files] of inputs) {
          for (const file of files) {
            if (!matchers.some((re) => re.test(file))) {
              missing.push(`${WORKFLOW}: ${event}.paths に "${file}" が無い（${gate} の入力）`);
            }
          }
        }
      }
      assert.strictEqual(checked, 2, `paths: を持つトリガが ${checked} 箇所（push / pull_request の 2 箇所のはず）`);
      assert.deepStrictEqual(
        missing,
        [],
        'ゲートの入力だけを変える PR でゲートが起動しない（床を緩める変更が静かに素通りする）。' +
          `${WORKFLOW} の push / pull_request の**両方**の paths: へ足すこと:\n  ` +
          missing.join('\n  '),
      );
    });
  }

  // --- #836: check-cross-repo-refs の遅延 require が lib 側の例外を握り潰さない -------
  //
  // 固定する退行は **初版の「エラーメッセージで見分ける」形**である:
  //     catch (e) { if (e.code !== 'MODULE_NOT_FOUND' || !/worktree-state/.test(e.message)) throw e; }
  // MODULE_NOT_FOUND の message は `Require stack:` を含み、**lib が別モジュールを
  // 見失った場合にも lib 自身のパスが載る**。よって上の判別は真になり、**結線が黙って
  // 切れたまま検査器が走り続ける**（#840 の実装中に実測した）。現行は解決
  // （require.resolve）と読み込み（require）を分け、lib 内部の例外を try の外で起こす。
  //
  // **判定は終了コードではない。** 握り潰した場合も 0 件走査の門が exit 1 を返すため、
  // 状態コードだけでは両者を区別できない。**門のメッセージが出ていないこと**が
  // 「握らずに伝播した」ことの証拠である。
  //
  // 置き場所が companion である理由: `scripts/scripts.test.js` は分類 A（キットとバイト
  // 一致）へ戻したところであり 1 バイトも足せない。そして `lib/worktree-state.js` への
  // 結線は分類 B 種 3（本リポ固有）で、キット版の検査器には存在しない。
  {
    const osX836 = require('os');
    const fsX836 = require('fs');
    const pathX836 = require('path');
    const { execFileSync: execX836, spawnSync: spawnX836 } = require('child_process');

    // 一時ディレクトリへ**検査器 1 本だけ**を写し、`scripts/lib/worktree-state.js` を
    // libSource で作って CLI として走らせる。既存の「scripts/ を丸ごと cpSync する」
    // ヘルパは使えない —— 本物の lib が入ってしまい、遅延 require の分岐を試験できない。
    // 一時ディレクトリの `scripts/lib/` を `populateLib(libDir)` に作らせてから検査器を走らせる。
    // lib の中身を差し替えられる形にしてあるのは、**壊れた lib（#836）と本物の lib（#842）の
    // 両方**を同じ経路で走らせるためである。
    const runCheckerWithLibX836 = (populateLib) => {
      const dir = fsX836.mkdtempSync(pathX836.join(osX836.tmpdir(), 'xrepo-lib-'));
      try {
        fsX836.mkdirSync(pathX836.join(dir, 'scripts'));
        fsX836.copyFileSync(
          require.resolve('./check-cross-repo-refs.js'),
          pathX836.join(dir, 'scripts', 'check-cross-repo-refs.js')
        );
        const libDir = pathX836.join(dir, 'scripts', 'lib');
        fsX836.mkdirSync(libDir);
        populateLib(libDir, dir);
        // git init は要る。無いと fail-open の分岐（git ls-files 不可）へ落ち、
        // 握り潰しの有無を見る前に終わってしまう。
        execX836('git', ['-C', dir, 'init', '-q'], { stdio: 'ignore' });
        const r = spawnX836(
          process.execPath,
          [pathX836.join(dir, 'scripts', 'check-cross-repo-refs.js')],
          { encoding: 'utf8' }
        );
        return { status: r.status, out: `${r.stdout || ''}${r.stderr || ''}`, dir };
      } finally {
        fsX836.rmSync(dir, { recursive: true, force: true });
      }
    };

    const runWithLibX836 = (libSource) =>
      runCheckerWithLibX836((libDir) =>
        fsX836.writeFileSync(pathX836.join(libDir, 'worktree-state.js'), libSource)
      );

    const GATE_X836 = /1 件も見つけられませんでした/;

    ok('#836: lib が別モジュールを見失ったら握り潰さない（メッセージ判別への退行を止める）', () => {
      const { status, out } = runWithLibX836(
        "'use strict';\nrequire('./nonexistent-xyz.js');\nmodule.exports = {};\n"
      );
      assert.notStrictEqual(status, 0, `結線が切れているのに exit ${status} で続行した:\n${out}`);
      assert.match(
        out,
        /nonexistent-xyz/,
        `lib 側の MODULE_NOT_FOUND を握り潰している（伝播していない）:\n${out}`
      );
      assert.doesNotMatch(
        out,
        GATE_X836,
        `0 件走査の門まで到達している＝例外を握って走り続けた（結線が黙って切れる）:\n${out}`
      );
    });

    // **保険であって本命ではない。** 構文エラーの `SyntaxError` は `.code` を持たないため、
    // 上の旧実装（`e.code === 'MODULE_NOT_FOUND' && ...`）でも条件が偽になって throw する。
    // つまり**この試験は当のバグを検出しない**。効くのは `catch (e) {}` のように catch を
    // 広げすぎる将来の退行に対してである。**「2 本あるから握り潰しは固定されている」と
    // 読んではならない** —— 固定しているのは上の 1 本だけである。
    ok('#836: lib が構文エラーでも握り潰さない（catch を広げすぎる退行への保険）', () => {
      const { status, out } = runWithLibX836(
        "'use strict';\nmodule.exports = { warnIfResultMayDifferFromCi: (\n"
      );
      assert.notStrictEqual(status, 0, `構文エラーの lib で exit ${status} で続行した:\n${out}`);
      assert.match(out, /SyntaxError/, `SyntaxError が伝播していない:\n${out}`);
      assert.doesNotMatch(
        out,
        GATE_X836,
        `0 件走査の門まで到達している＝例外を握って走り続けた:\n${out}`
      );
    });

    // --- #842: 結線が「生きている」ことを実挙動で固定する（#836 の対の欠け） ---------------
    //
    // **上の 2 本が固定しているのは「壊れた lib を握り潰さない」ことだけ**で、
    // **「正しい lib を置いたときに結線が実際に働く」ことは 1 本も見ていなかった。**
    // `check-cross-repo-refs.js` の遅延 require は、`require.resolve` が MODULE_NOT_FOUND を
    // 返すと `MODE = {}` ＋ no-op のまま**警告を 1 行も出さずに**走り続ける（意図された
    // fail-open。キット版の門試験が `lib/` の無い一時ディレクトリで走るため）。
    // その fail-open は「正常な一時 dir」と「本リポで結線が壊れた」を区別しない ——
    // 例えば結線のブロックを丸ごと消しても、上の 2 本は**どちらも緑のまま**になる。
    //
    // **他 3 本（check-doc-updated / check-landed-subjects / check-plan-id-qualification）には
    // 遅延 require が無いので、この穴は本ファイル固有である。** それらの実挙動は上の
    // 「#683: 該当 4 本すべてが実際に警告を出し…」が本リポジトリのツリー上で見ているが、
    // **本リポジトリのツリーには当然 `lib/` が在る**ため、遅延 require の分岐は通らない。
    //
    // **判定は終了コードではない**（#836 の 2 本と同じ理由。lib が正しくても 0 件走査の門が
    // exit 1 を返すため、状態コードでは両者を区別できない）。**判定行の有無で見る** ——
    // 文言は #683 群（4884 / 4909 行あたり）と同じ `#683 / IADR-0183` を使う。
    ok('#842: 正しい lib を置くと結線が実際に働く（警告行が出る。黙って no-op へ落ちない）', () => {
      const { out } = runCheckerWithLibX836((libDir, dir) => {
        // **本物を 2 ファイル写す。** `lib/worktree-state.js` は `./ci-annotate` を require するので、
        // 1 ファイルだけでは MODULE_NOT_FOUND になり #836 の「握り潰さない」側の試験になってしまう。
        for (const f of ['worktree-state.js', 'ci-annotate.js']) {
          fsX836.copyFileSync(
            require.resolve(`./lib/${f}`),
            pathX836.join(libDir, f)
          );
        }
        // untracked を 1 件は確実に作る（MODE.TRACKED の警告条件）。
        // `scripts/` 自体も untracked だが、条件を偶然に頼らず明示で満たす。
        fsX836.writeFileSync(pathX836.join(dir, '.tmp-untracked-probe-842'), 'probe\n');
      });
      assert.match(
        out,
        /#683 \/ IADR-0183/,
        '本物の lib を置いても警告が出ない＝遅延 require の結線が死んでいる（no-op のまま exit する）:\n' + out
      );
      assert.match(
        out,
        /untracked のファイルが \d+ 件ある/,
        'MODE.TRACKED が渡っていない（MODE = {} のまま呼ばれると mode が undefined になる）:\n' + out
      );
    });
  }

  // --- #830: 雛形（templates/*/backend）を CI が実際にコンパイル・テストしていること ----------
  //
  // 事故: 雛形のテストプロジェクトに GlobalUsings.cs が無く（ImplicitUsings は Xunit を含まない）、
  // **配布中の雛形が一度もコンパイルできない状態のまま出荷されていた**。誰も気付けなかったのは
  // 雛形 backend をコンパイルするジョブが 1 つも無かったためである（lint / build-and-test /
  // codeql.yml は src/*/backend/backend.slnx を glob し、security.yml と copilot-setup-steps.yml は
  // -not -path './templates/*' で明示除外する）。
  //
  // **本群が固定するのは「配線が在ること」であって「雛形がビルドできること」ではない。**
  // 実際のコンパイルは ci.yml の template-backend-build ジョブが行う（node からは dotnet を
  // 呼べないし、呼べても scripts-tests ジョブは setup-dotnet を持たない）。ここで固定するのは、
  // **その配線が黙って外れないこと**と、**外れると空回りする各要素**である:
  //   - .sample を複製先から外す（外し忘れると src/ の単一情報源を上書きする。IADR-0060 決定 4）
  //   - --artifacts-path（無いと ProjectReference 先の src/platform/**/{bin,obj} が作業ツリーへ生える）
  //   - 実行件数の下限判定（無いと「0 件実行の Test Run Successful」を緑と読む）
  //   - 後片付けの実測ステップ（if: always()。ビルドが落ちた回にこそ残骸が出る）
  // 併せて、**実際に出荷された欠陥そのもの**（GlobalUsings.cs の欠落）を静的にも固定する。
  {
    const fs830 = require('fs');
    const path830 = require('path');
    const REPO830 = path830.resolve(__dirname, '..');
    const CI830 = fs830.readFileSync(path830.join(REPO830, '.github/workflows/ci.yml'), 'utf8');
    // 行頭 # のコメントを落とす（配線の実体だけを見る。コメントに書いただけで緑にしない）。
    const bare830 = CI830.split('\n')
      .filter((l) => !/^\s*#/.test(l))
      .join('\n');

    ok('#830: ci.yml に雛形ビルドのジョブ ID template-backend-build が在る', () => {
      assert.match(
        bare830,
        /^ {2}template-backend-build:$/m,
        'ジョブ ID template-backend-build が無い（雛形をコンパイルする経路が消えている）'
      );
    });

    ok('#830: 雛形を src/ 配下へ複製してから build / test している', () => {
      // 配置後の位置を模した複製先。templates/ 位置のままでは platform Shared への相対参照が
      // 解決できず restore が MSB4181 で落ちる（IADR-0060 決定 3）。
      assert.match(
        bare830,
        /stage="src\/\.template-buildcheck-\$\{name\}"/,
        '複製先が src/ 配下でない（テンプレート位置のままではビルドできない）'
      );
      assert.match(
        bare830,
        /for slnx in templates\/\*\/backend\/backend\.slnx; do/,
        '雛形の自動発見 glob が無い'
      );
      assert.match(
        bare830,
        /dotnet build "\$\{stage\}\/backend\/backend\.slnx"/,
        '複製した雛形の dotnet build が無い'
      );
      assert.match(
        bare830,
        /dotnet test "\$\{stage\}\/backend\/backend\.slnx"/,
        '複製した雛形の dotnet test が無い'
      );
      // glob が空振りしたまま緑になる形（雛形が消えた・移動した）を止めていること。
      assert.match(bare830, /if \[ "\$found" -eq 0 \]/, 'glob 空振りの検出が無い');
    });

    ok('#830: 複製先から .sample を外している（src/ の単一情報源を上書きしない）', () => {
      assert.match(
        bare830,
        /find "\$stage" -name '\*\.sample' -type f -delete/,
        '.sample の除去が無い。置いたままだと src/Directory.Build.props より近い階層で発見され' +
          '上書きする（IADR-0060 決定 4）'
      );
    });

    ok('#830: --artifacts-path で obj/bin を作業ツリーの外へ逃がしている', () => {
      const hits = bare830.match(/--artifacts-path "\$artifacts"/g) || [];
      assert.ok(
        hits.length >= 2,
        `build と test の双方に --artifacts-path が要る（実測 ${hits.length} 件）。` +
          '片方でも欠けると src/platform/backend/Shared/*/{bin,obj} が作業ツリーへ生える'
      );
    });

    ok('#830: 実行されたテスト名を出し、件数の下限を [Fact]/[Theory] で押さえている', () => {
      // 名前が出ないと「何が走ったか」を誰も確かめられない（#830 受け入れ基準）。
      assert.match(bare830, /--verbosity normal/, 'dotnet test の --verbosity normal が無い');
      assert.match(
        bare830,
        /expected=\$\(grep -rhE '\^\[\[:space:\]\]\*\\\[\(Fact\|Theory\)'/,
        '[Fact]/[Theory] の数え上げが無い（0 件実行を緑と読む）'
      );
      assert.match(
        bare830,
        /if \[ "\$executed" -lt "\$expected" \]/,
        '実行件数の下限判定が無い（1 件だけ動いて 1 件が拾われない、を見逃す）'
      );
    });

    ok('#830: 後片付けの取りこぼしを if: always() で実測している', () => {
      assert.match(bare830, /trap cleanup EXIT/, '複製の後片付け（trap）が無い');
      assert.match(
        bare830,
        /git status --short --ignored -- src\//,
        '残骸ゼロの実測が無い（#830 受け入れ基準）'
      );
      // ビルドが落ちた回にこそ残骸が出る。if: always() が無いとその回に検査されない。
      const verify = bare830.slice(bare830.indexOf('Verify the staged copies left nothing behind'));
      assert.match(verify.slice(0, 200), /if: always\(\)/, '後片付け検査に if: always() が無い');
    });

    // ── 起動条件と必須チェックの context を変えていないこと（CLAUDE.md「変更したら確かめる」）。
    ok('#830: ci.yml の起動条件と既存の必須チェック名は不変', () => {
      // ci.yml は paths: フィルタを持たない。持たせると「雛形だけを触る PR で起動しない」に戻り、
      // かつ必須チェックが恒久 pending になる（docs/ai-workflow.md）。
      assert.doesNotMatch(bare830, /^\s*paths:/m, 'ci.yml に paths: フィルタが入っている');
      assert.match(bare830, /types: \[opened, synchronize, reopened\]/, 'pull_request の types が変わっている');
      for (const job of ['build-and-test', 'lint', 'commit-messages']) {
        assert.match(
          bare830,
          new RegExp(`^ {2}${job}:$`, 'm'),
          `必須チェックの context であるジョブ ID ${job} が変わっている（docs/ai-workflow.md の表）`
        );
      }
    });

    // ── 実際に出荷された欠陥そのもの。テスト属性を持つ雛形のテストプロジェクトは
    //    global using Xunit; を持たねばならない（ImplicitUsings は Xunit を含まない）。
    ok('#830: 雛形のテストプロジェクトが global using Xunit; を持つ', () => {
      const walk = (dir, acc) => {
        for (const e of fs830.readdirSync(dir, { withFileTypes: true })) {
          const full = path830.join(dir, e.name);
          if (e.isDirectory()) {
            if (e.name === 'bin' || e.name === 'obj' || e.name === 'node_modules') continue;
            walk(full, acc);
          } else if (e.name.endsWith('.cs')) {
            acc.push(full);
          }
        }
        return acc;
      };
      const roots = fs830
        .readdirSync(path830.join(REPO830, 'templates'), { withFileTypes: true })
        .filter((e) => e.isDirectory())
        .map((e) => path830.join(REPO830, 'templates', e.name, 'backend'))
        .filter((d) => fs830.existsSync(d));
      assert.ok(roots.length > 0, 'templates/*/backend が 1 件も無い（走査が空回りしている）');

      let checked = 0;
      for (const root of roots) {
        const files = walk(root, []);
        // テスト属性を持つファイルが属するプロジェクト（.csproj のあるディレクトリ）を集める。
        const projects = new Set();
        for (const f of files) {
          if (!/^[ \t]*\[(Fact|Theory)/m.test(fs830.readFileSync(f, 'utf8'))) continue;
          let dir = path830.dirname(f);
          while (dir.startsWith(root)) {
            if (fs830.readdirSync(dir).some((n) => n.endsWith('.csproj'))) {
              projects.add(dir);
              break;
            }
            dir = path830.dirname(dir);
          }
        }
        assert.ok(
          projects.size > 0,
          `${root} に [Fact]/[Theory] を持つテストプロジェクトが無い（雛形のテストが消えている）`
        );
        for (const proj of projects) {
          const hasGlobalUsing = walk(proj, []).some((f) =>
            /^\s*global\s+using\s+Xunit\s*;/m.test(fs830.readFileSync(f, 'utf8'))
          );
          assert.ok(
            hasGlobalUsing,
            `${path830.relative(REPO830, proj)} に global using Xunit; が無い。` +
              'ImplicitUsings は Xunit を含まないため [Fact] が CS0246 で落ちる（#830 の実害）'
          );
          checked += 1;
        }
      }
      assert.ok(checked > 0, '検査したテストプロジェクトが 0 件（試験が空回りしている）');
    });
  }

  // --- 計画 ADR-0048 決定 4: check-trace-blocks.js / gen-knowledge-graph.js -----------
  //
  // trace ブロック（`<!-- trace: ... -->`）・trace-table ブロックの文法・値域検査（新設）と、
  // それを元にしたナレッジグラフ生成（新設）。純関数の網羅は各スクリプト自身の --self-test が
  // 持つ（lib/trace-blocks.js 58 件・check-trace-blocks.js 28 件・gen-knowledge-graph.js 26 件）。
  // ここでは他の検査器と同じ流儀で「自己試験が子プロセスで実際に緑であること」と
  // 「実データに対して例外を投げずに完走すること」を固定する。**実データの違反件数は固定しない**
  // ——docs/ は本 PR と並行して trace ブロックへの移行が進行中であり、件数は移行の進捗で
  // 変わり続ける（違反 0 件を固定すると移行完了までこのテストが赤いままになる）。
  {
    const fs = require('fs');
    const path = require('path');
    const { spawnSync } = require('child_process');
    const runSelf = (f) =>
      spawnSync(process.execPath, [path.join(__dirname, f), '--self-test'], { encoding: 'utf8' });

    ok('lib/trace-blocks.js --self-test は exit 0', () => {
      const r = runSelf('lib/trace-blocks.js');
      assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}${r.stderr}`);
      assert.match(String(r.stdout), /自己試験 \d+ 件 OK/);
    });

    ok('check-trace-blocks.js --self-test は exit 0（実データからの値域読み出しを含む）', () => {
      const r = runSelf('check-trace-blocks.js');
      assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}${r.stderr}`);
      assert.match(String(r.stdout), /自己試験 \d+ 件 OK/);
    });

    ok('gen-knowledge-graph.js --self-test は exit 0（実データでのグラフ構築を含む）', () => {
      const r = runSelf('gen-knowledge-graph.js');
      assert.strictEqual(r.status, 0, `自己試験が失敗した:\n${r.stdout}${r.stderr}`);
      assert.match(String(r.stdout), /自己試験 \d+ 件 OK/);
    });

    ok('check-trace-blocks.js: docs/ の実データ走査が例外を投げずに終了コード 0/1 で終わる', () => {
      const r = spawnSync(process.execPath, [path.join(__dirname, 'check-trace-blocks.js')], { encoding: 'utf8' });
      assert.ok([0, 1].includes(r.status), `想定外の終了コード ${r.status}:\n${r.stdout}${r.stderr}`);
      const out = `${r.stdout}${r.stderr}`;
      assert.match(out, r.status === 0 ? /OK: \d+ 件の Markdown/ : /違反 \d+ 件を検出しました/, out);
    });

    ok('gen-knowledge-graph.js --json: 実データからノード・エッジを構築する（形の検査）', () => {
      const r = spawnSync(process.execPath, [path.join(__dirname, 'gen-knowledge-graph.js'), '--json'], {
        encoding: 'utf8',
        maxBuffer: 32 * 1024 * 1024,
      });
      assert.strictEqual(r.status, 0, `--json が失敗した:\n${r.stdout}${r.stderr}`);
      const graph = JSON.parse(r.stdout);
      assert.ok(Array.isArray(graph.nodes) && graph.nodes.length > 0, 'nodes が空');
      assert.ok(Array.isArray(graph.edges) && graph.edges.length > 0, 'edges が空');
      const byKind = { doc: 0, iadr: 0, spec: 0 };
      for (const n of graph.nodes) byKind[n.kind] = (byKind[n.kind] || 0) + 1;
      assert.ok(byKind.doc > 0 && byKind.iadr > 0 && byKind.spec > 0, `ノード種別が揃っていない: ${JSON.stringify(byKind)}`);
      const ids = graph.nodes.map((n) => n.id);
      assert.strictEqual(new Set(ids).size, ids.length, 'ノード ID が重複している（doc/iadr/spec の正規化衝突の疑い）');
    });

    ok('gen-knowledge-graph.js --mermaid --scope: スコープ配下だけの flowchart を出す', () => {
      const r = spawnSync(
        process.execPath,
        [path.join(__dirname, 'gen-knowledge-graph.js'), '--mermaid', '--scope', 'docs/tech'],
        { encoding: 'utf8' },
      );
      assert.strictEqual(r.status, 0, `--mermaid が失敗した:\n${r.stdout}${r.stderr}`);
      assert.match(r.stdout, /^flowchart LR/, 'Mermaid の先頭行が flowchart LR でない');
      assert.doesNotMatch(r.stdout, /docs_screens_/, '--scope docs/tech なのに docs/screens 配下のノードが混入している');
    });

    ok('gen-knowledge-graph.js --check: 終了コード 0/1 でノード・エッジ件数を報告する', () => {
      const r = spawnSync(process.execPath, [path.join(__dirname, 'gen-knowledge-graph.js'), '--check'], {
        encoding: 'utf8',
      });
      assert.ok([0, 1].includes(r.status), `想定外の終了コード ${r.status}:\n${r.stdout}${r.stderr}`);
      assert.match(r.stdout, /ノード \d+ 件、エッジ \d+ 件/);
    });

    // ci.yml への配線（宣言 → 実挙動）。scripts/README.md の記述と対で、
    // 「新設した検査器が CI から呼ばれていない」事故（他の check-*.js と同型）を防ぐ。
    // ［2026-08-21 追記 / IADR-0232 決定 6］旧 doc-links ジョブは static-checks へ統合された。
    // 見るのは「どのジョブに居るか」ではなく「CI から呼ばれているか」なので、
    // ジョブ名を追随させるだけでよい（検査器の本数と対象は統合前と同一）。
    //
    // ★ ジョブの切り出しに `\n {2}\S` を使わないこと。統合後のジョブは 2 空白のコメント行
    //   （移設した各検査の由来コメント）を含むため、最初のコメントで切れて中身を見落とす。
    //   次の**ジョブキー**（2 空白 ＋ 英小文字 ＋ `:`）までを取る。
    ok('ci.yml の static-checks ジョブが check-trace-blocks.js / gen-knowledge-graph.js --check を呼ぶ', () => {
      const ciText = fs.readFileSync(path.join(__dirname, '..', '.github', 'workflows', 'ci.yml'), 'utf8');
      const m = /\n {2}static-checks:\n([\s\S]*?)(?=\n {2}[a-z][a-z0-9-]*:\n)/.exec(`${ciText}\n  zzz:\n`);
      assert.ok(m, 'static-checks ジョブが見つからない');
      const job = m[1];
      assert.match(job, /node scripts\/check-trace-blocks\.js --self-test/);
      assert.match(job, /node scripts\/check-trace-blocks\.js\s*$/m);
      assert.match(job, /node scripts\/gen-knowledge-graph\.js --self-test/);
      assert.match(job, /node scripts\/gen-knowledge-graph\.js --check/);
    });

    ok('scripts/README.md に check-trace-blocks.js / gen-knowledge-graph.js が載っている', () => {
      const readme = fs.readFileSync(path.join(__dirname, 'README.md'), 'utf8');
      assert.match(readme, /\| `check-trace-blocks\.js` \|/);
      assert.match(readme, /\| `gen-knowledge-graph\.js` \|/);
    });

    // NFR / #783（#442 子 5）: chart / overlay の検証ジョブが CI に在り、かつ fail-open の
    // 抜け道を使っていないことを固定する。
    //
    // **なぜ「ジョブが在る」だけでは足りないか。** check-deploy-manifests.js は helm / kubectl が
    // 無いとき `DEPLOY_MANIFESTS_ALLOW_MISSING_TOOLS=1` で notice を出して skip する経路を持つ。
    // CI がこれを立てると、ツール導入が失敗しても検査が緑を返し、**壊れた overlay がマージされる**。
    // 「検査がある」と「検査が働いている」を読み分けられない状態であり、本リポジトリが
    // 繰り返し踏んできた型である（#558 / #562 / #747 / #801 / IADR-0209）。
    ok('NFR / #783: ci.yml に deploy-manifests ジョブが在り、fail-open の抜け道を立てていない', () => {
      const REPO = path.join(__dirname, '..');
      const ciPath = path.join(REPO, '.github/workflows/ci.yml');
      const ci = fs.readFileSync(ciPath, 'utf8');
      const { ALLOW_MISSING_TOOLS_ENV } = require('./check-deploy-manifests.js');

      // ［2026-08-21 追記 / IADR-0232 決定 6］旧 deploy-manifests ジョブは static-checks-units へ
      // 統合された（submodule 取得と helm / kubectl を要する検査をまとめたジョブ）。
      // 本試験の主眼は「検査が CI で走っているか」と「fail-open の抜け道を立てていないか」で
      // あって「専用ジョブが在るか」ではないため、ジョブ名だけ追随させる。
      assert.ok(
        /\n {2}static-checks-units:\n/.test(ci),
        'ci.yml に static-checks-units ジョブが無い（#783 の検査が CI で走らない）',
      );
      assert.ok(
        ci.includes('node scripts/check-deploy-manifests.js --self-test'),
        'static-checks-units ジョブが検査器の --self-test を呼んでいない',
      );
      assert.ok(
        ci.includes('node scripts/check-deploy-manifests.js\n'),
        'static-checks-units ジョブが本走査を呼んでいない',
      );
      // helm / kubectl の導入が同じジョブに在ること（別ジョブへ離れると検査が動かない）。
      const unitsJob = /\n {2}static-checks-units:\n([\s\S]*?)(?=\n {2}[a-z][a-z0-9-]*:\n)/
        .exec(`${ci}\n  zzz:\n`);
      assert.ok(unitsJob, 'static-checks-units ジョブを切り出せない');
      assert.match(unitsJob[1], /azure\/setup-helm/, 'static-checks-units に helm の導入が無い');
      assert.match(unitsJob[1], /azure\/setup-kubectl/, 'static-checks-units に kubectl の導入が無い');

      // 抜け道を「立てている」行だけを違反にする。注意書きとして名前に言及するのは許す
      // （コメント行は行頭が `#`）。
      const armed = ci
        .split('\n')
        .filter((line) => line.includes(ALLOW_MISSING_TOOLS_ENV))
        .filter((line) => !/^\s*#/.test(line));
      assert.deepStrictEqual(
        armed,
        [],
        `ci.yml が ${ALLOW_MISSING_TOOLS_ENV} を立てている。立てると helm / kubectl の導入が` +
          '失敗しても検査が素通りする:\n' + armed.join('\n'),
      );
    });

    ok('NFR / #783: overlay の列挙をワークフローへ書いていない（走査で発見する）', () => {
      const REPO = path.join(__dirname, '..');
      const ci = fs.readFileSync(path.join(REPO, '.github/workflows/ci.yml'), 'utf8');
      const { discoverOverlays } = require('./check-deploy-manifests.js');
      const overlays = discoverOverlays(REPO);
      // fail-closed の門: 走査が 0 件なら、この試験は何も見ていない。
      assert.ok(overlays.length > 0, 'overlay が 0 件（走査が壊れている）');

      // ［2026-08-21 追記 / IADR-0232 決定 6］旧 deploy-manifests ジョブは static-checks-units へ統合。
      // 検査の主眼（overlay パスをワークフローへ直書きしていないこと）は変わらない。
      const job = ci.match(/\n {2}static-checks-units:\n([\s\S]*?)(?=\n {2}[a-z][a-z0-9-]*:\n|$)/);
      assert.ok(job, 'static-checks-units ジョブの本文を読めない');
      const hardcoded = overlays.filter((o) => job[1].includes(o));
      assert.deepStrictEqual(
        hardcoded,
        [],
        'deploy-manifests ジョブに overlay のパスが直書きされている。書くと次に overlay が' +
          '増えたとき静かに検査対象から外れる:\n' + hardcoded.join('\n'),
      );
    });
  }
};
