import { describe, it, expect, beforeEach } from 'vitest';
import { useAiChatStore, selectHistory } from './aiChatStore';

// 05_screens §共通シェル（画面別履歴・全消去）/ ADR-0031 §採用技術一覧（クライアント状態 = Zustand）/
// IADR-0121 決定 1 の第 4 段（#788）。**ストアは純ロジックとして固定する**——描画を通すと
// 「画面ごとに分かれているか」の判定がレイアウトの都合に紛れる。

const turn = (id: string) => ({ id, question: `q-${id}`, answer: `a-${id}`, answerId: null });

beforeEach(() => {
  useAiChatStore.setState({ open: false, historyByScreen: {} });
});

describe('aiChatStore', () => {
  // 05_screens §共通シェル: 右レールは既定で閉じている（主領域を最初から狭めない）。
  it('starts closed and toggles', () => {
    expect(useAiChatStore.getState().open).toBe(false);
    useAiChatStore.getState().togglePanel();
    expect(useAiChatStore.getState().open).toBe(true);
    useAiChatStore.getState().closePanel();
    expect(useAiChatStore.getState().open).toBe(false);
    useAiChatStore.getState().openPanel();
    expect(useAiChatStore.getState().open).toBe(true);
  });

  // 05_screens §共通シェル「画面別履歴」: 別の画面の会話が混ざらないこと。
  it('keeps history separate per screen', () => {
    const { appendTurn } = useAiChatStore.getState();
    appendTurn('/ask', turn('1'));
    appendTurn('/analyze', turn('2'));
    appendTurn('/ask', turn('3'));

    const state = useAiChatStore.getState();
    expect(selectHistory(state, '/ask').map((t) => t.id)).toEqual(['1', '3']);
    expect(selectHistory(state, '/analyze').map((t) => t.id)).toEqual(['2']);
    // 未登録の画面は空（`undefined` を呼び出し側で分岐させない）。
    expect(selectHistory(state, '/unknown')).toEqual([]);
  });

  // 05_screens §共通シェル「画面ごとの保持」: 1 画面ぶんの消去が他画面を巻き添えにしないこと。
  it('clears only the named screen', () => {
    const { appendTurn, clearScreen } = useAiChatStore.getState();
    appendTurn('/ask', turn('1'));
    appendTurn('/analyze', turn('2'));
    clearScreen('/ask');

    const state = useAiChatStore.getState();
    expect(selectHistory(state, '/ask')).toEqual([]);
    expect(selectHistory(state, '/analyze')).toHaveLength(1);
    // キーごと落とす（空配列を残すと「消したのに履歴がある」と読める状態が残る）。
    expect(Object.keys(state.historyByScreen)).toEqual(['/analyze']);
  });

  // 05_screens §共通シェル「全消去」。
  it('clears every screen', () => {
    const { appendTurn, clearAll } = useAiChatStore.getState();
    appendTurn('/ask', turn('1'));
    appendTurn('/analyze', turn('2'));
    clearAll();
    expect(useAiChatStore.getState().historyByScreen).toEqual({});
  });

  // #788: 空配列の同一性。呼び出しごとに新しい配列を返すと購読側が毎回再描画される。
  it('returns a stable empty history', () => {
    const state = useAiChatStore.getState();
    expect(selectHistory(state, '/none')).toBe(selectHistory(state, '/other'));
  });
});
