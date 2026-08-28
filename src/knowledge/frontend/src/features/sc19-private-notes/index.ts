// ADR-0031 / IADR-0262 決定 4: feature の公開面。
// feature の外から触ってよいのはこのファイルが再輸出したものだけである。
export {
  createSc19PrivateNotesRoute,
  sc19PrivateNotesNav,
  sc19PrivateNotesBreadcrumb,
} from './routes/sc19PrivateNotesRoute';
