using LlmGateway.Domain.Ports;

namespace LlmGateway.Domain.Limits;

// NFR-18, IADR-0698: 置き場を設定しない構成（ローカル・試験）の既定。何も残さない（再起動で当日の枠は 0 に戻る）。
// クラウドは `Llm:CallLimits:SpendStore:Path` を与えて FileLlmSpendStore を使う（scripts/deploy-cloud.test.js が固定する）。
public sealed class ProcessLocalLlmSpendStore : ILlmSpendStore
{
    public bool IsDurable => false;

    public LlmSpendSnapshot? Load() => null;

    public void Save(LlmSpendSnapshot snapshot)
    {
    }
}
