namespace Knowledge.IntegrationTests.Fixtures;

// NFR / #1292: コンテナ起動が失敗したときに **skip へ倒すか、原因を添えて落とすか**を決める。
//
// ■ 🔴 なぜ 1 か所に置くのか
//   判定は `PostgresFixture` と `RabbitMqFixture` の 2 つが要る。同じ規則を 2 か所へ写すと、
//   **片方だけ直した状態**が作れてしまう —— それは本リポジトリが繰り返し踏んでいる形である。
//   規則をここに 1 つ持てば、**片側だけ直せる形そのものが無くなる。**
//
// ■ 従前の欠陥（実測。run 33962157954）
//   両 fixture は `catch { IsAvailable = false; }` で**例外を握り潰し、理由を 1 行も残さなかった**。
//   その結果:
//     1. 試験クラスが `if (!postgres.IsAvailable) return;` で早期 return し `_client` は `null!` のまま
//     2. 試験本体のガード `RequiredServices.SkipUnlessObtainable(...)` は **CI では無条件に真**を返す
//     3. ⇒ skip されず、null の `_client` に触って
//        `ArgumentNullException: Value cannot be null. (Parameter 'client')` で落ちる
//     4. しかも**なぜ起動に失敗したかはログに 1 行も無い**
//   5 件がこの形で落ち、原因は特定できなかった（`ryuk` / `testcontainers.org` / `Cannot connect to
//   the Docker` はいずれもログに 0 件）。
//
// ■ 🔴 `DockerRequired.IsAvailable()` が CI で無条件に真を返すのは、ここでは前提であって欠陥ではない
//   CI は Docker がある前提で回っている（同 run で 84 件中 78 件が実際に走っている）。
//   **そこでコンテナが起きないのは skip すべき事情ではなく、報告すべき失敗である。**
//   [[IADR-0231]] 決定 3 が撲滅した「走っていないのに Passed」と同じ向きである。
internal static class ContainerStartupFailure
{
    /// <summary>
    /// 起動失敗を**失敗として投げる**なら例外を、**skip へ倒す**なら <c>null</c> を返す。
    /// </summary>
    /// <param name="fixtureName">どの fixture か（メッセージに出す）。</param>
    /// <param name="what">起こそうとしたもの（例: <c>PostgreSQL</c>）。</param>
    /// <param name="externalEndpointVariable">外部供給へ切り替える環境変数の名前。</param>
    /// <param name="cause">握り潰さずに内包する元の例外。</param>
    /// <param name="dockerAvailable">Docker が使えるか（`DockerRequired.IsAvailable()`）。</param>
    internal static InvalidOperationException? ToThrow(
        string fixtureName, string what, string externalEndpointVariable,
        Exception cause, bool dockerAvailable)
    {
        // Docker が無い環境では従来どおり skip へ倒す（**挙動を変えない**）。
        if (!dockerAvailable) return null;

        return new InvalidOperationException(
            $"{fixtureName}: Docker は使えるのに {what} コンテナを起動できなかった。"
            + $" 外部の {what} を使うなら {externalEndpointVariable} を設定すること。",
            cause);
    }
}
