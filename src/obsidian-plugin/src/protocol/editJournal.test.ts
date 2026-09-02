import {
  MAX_PENDING_EDITS,
  clearPath,
  emptyJournal,
  localRenames,
  readJournal,
  recordDelete,
  recordRename,
  recordSave,
} from './editJournal.ts';

const P = '個人資料/a.md';
const at = (sec: number) => new Date(Date.UTC(2026, 8, 3, 0, 0, sec));

describe('editJournal — 「1 編集」の刻み（ADR-0037 決定 8 / フォローアップ 5, IADR-0352 決定 1）', () => {
  // 静穏窓（30 秒）の中の連続保存は 1 編集に畳み込み、窓を超えたら次の編集になる
  it('30 秒未満の連続保存は 1 編集に畳み込み、30 秒以上空いた保存は次の編集になる', () => {
    const j = emptyJournal();
    recordSave(j, P, 'v1', at(0));
    recordSave(j, P, 'v1 typing', at(5));
    recordSave(j, P, 'v1 done', at(29));
    expect(j.edits[P]).toEqual([{ at: at(29).toISOString(), content: 'v1 done' }]);

    recordSave(j, P, 'v2', at(60));
    expect(j.edits[P]!.map((e) => e.content)).toEqual(['v1 done', 'v2']);
  });

  // 受け入れ基準: オフラインで 10 回保存（各 30 秒以上空く）→ 10 編集（＝ push で 10 版）
  it('30 秒以上空けた 10 回の保存は 10 編集として積まれる', () => {
    const j = emptyJournal();
    for (let i = 0; i < 10; i += 1) recordSave(j, P, `save ${i}`, at(i * 30));
    expect(j.edits[P]).toHaveLength(10);
    expect(j.edits[P]!.at(-1)!.content).toBe('save 9');
  });

  // 上限（50 件）を超えたら古いものから落とす（サーバも直近 50 版しか残さない。決定 16）
  it(`${MAX_PENDING_EDITS} 件を超える未送信の編集は古いものから落とす`, () => {
    const j = emptyJournal();
    for (let i = 0; i < MAX_PENDING_EDITS + 5; i += 1) recordSave(j, P, `s${i}`, at(i * 60));
    expect(j.edits[P]).toHaveLength(MAX_PENDING_EDITS);
    expect(j.edits[P]![0]!.content).toBe('s5');
    expect(j.edits[P]!.at(-1)!.content).toBe(`s${MAX_PENDING_EDITS + 4}`);
  });

  // ADR-0037 決定 4・5: 削除は「削除」、フォルダ外への移動は「同期停止」として別々に記録する（対）
  it('削除は deleted に、フォルダ外への移動は movedOut に記録し、未送信の編集は捨てる', () => {
    const del = recordSave(emptyJournal(), P, 'x', at(0));
    recordDelete(del, P);
    expect(del).toEqual({ edits: {}, deleted: { [P]: true }, movedOut: {}, renamed: {} });

    const out = recordSave(emptyJournal(), P, 'x', at(0));
    recordRename(out, P, 'archive/a.md', { fromInFolder: true, toInFolder: false });
    expect(out).toEqual({ edits: {}, deleted: {}, movedOut: { [P]: true }, renamed: {} });

    // 削除のあと同じパスに保存すれば削除は取り消される
    recordSave(del, P, 'again', at(60));
    expect(del.deleted).toEqual({});
    expect(del.edits[P]).toHaveLength(1);
  });

  // IADR-0352 決定 5: フォルダ内のリネームは元の追跡パスへ紐付け、編集列を引き継ぐ。連鎖しても元を指す
  it('フォルダ内のリネームは renamed[new]=元パス を記録し、連鎖しても元の追跡パスを指す。元へ戻せば記録が消える', () => {
    const j = recordSave(emptyJournal(), P, 'x', at(0));
    recordRename(j, P, '個人資料/b.md', { fromInFolder: true, toInFolder: true });
    recordRename(j, '個人資料/b.md', '個人資料/c.md', { fromInFolder: true, toInFolder: true });
    expect(j.renamed).toEqual({ '個人資料/c.md': P });
    expect(j.edits).toEqual({ '個人資料/c.md': [{ at: at(0).toISOString(), content: 'x' }] });
    expect(localRenames(j)).toEqual(new Map([[P, '個人資料/c.md']]));

    // リネーム後に削除 → 元の追跡パスに削除の印
    recordDelete(j, '個人資料/c.md');
    expect(j.deleted).toEqual({ [P]: true });
    expect(j.renamed).toEqual({});

    const back = recordRename(emptyJournal(), P, '個人資料/tmp.md', {
      fromInFolder: true,
      toInFolder: true,
    });
    recordRename(back, '個人資料/tmp.md', P, { fromInFolder: true, toInFolder: true });
    expect(back.renamed).toEqual({});

    // フォルダ外 → フォルダ内は記録しない（次の push が新規として拾う）
    const inbound = recordRename(emptyJournal(), 'outside.md', P, {
      fromInFolder: false,
      toInFolder: true,
    });
    expect(inbound).toEqual(emptyJournal());
  });

  it('clearPath は編集・削除・移動・リネーム（値側も）を消し、readJournal は壊れた値を捨てる', () => {
    const j = recordSave(emptyJournal(), P, 'x', at(0));
    recordRename(j, P, '個人資料/b.md', { fromInFolder: true, toInFolder: true });
    clearPath(j, P);
    expect(j.renamed).toEqual({});
    expect(Object.keys(j.edits)).toEqual(['個人資料/b.md']);
    clearPath(j, '個人資料/b.md');
    expect(j).toEqual(emptyJournal());

    expect(
      readJournal({
        edits: { [P]: [{ at: 't', content: 'c' }, { at: 1 }], bad: 'x' },
        deleted: { [P]: true, other: 'yes' },
        movedOut: 5,
        renamed: { new: 'old', empty: '' },
      }),
    ).toEqual({
      edits: { [P]: [{ at: 't', content: 'c' }] },
      deleted: { [P]: true },
      movedOut: {},
      renamed: { new: 'old' },
    });
    expect(readJournal(undefined)).toEqual(emptyJournal());
  });
});
