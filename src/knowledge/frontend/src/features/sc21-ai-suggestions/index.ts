// ADR-0031 / IADR-0262 決定 4: feature の公開面。
// feature の外から触ってよいのはこのファイルが再輸出したものだけである。
export {
  createSc21AiSuggestionsRoute,
  sc21AiSuggestionsNav,
  sc21AiSuggestionsBreadcrumb,
} from './routes/sc21AiSuggestionsRoute';
