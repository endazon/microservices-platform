using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmGateway.Domain.Ports;

namespace LlmGateway.Infrastructure.Persistence;

// NFR-18, NFR-21, ADR-0044, IADR-0698: 当日の費用の枠を、ボリューム（クラウドでは PVC）の上の 1 つのファイルに置く。
//
// - 置き換えは「一時ファイルへ書いて fsync → 名前を替える」（途中で落ちても、前の版か新しい版のどちらかが残る）。
// - 🔴 **書き手は 1 つだけ。** 同じ場所の `.lock` を排他で開いたまま持つ（.NET は Unix で flock(LOCK_EX|LOCK_NB) を取る）。
//   別のプロセス（新旧の Pod が重なったとき）は取れず、読み書きとも例外になる ＝ 判定器は段 2 で送らない。
// - 🔴 **ディレクトリは作らない。** 無ければ例外（ボリュームが付いていない構成で、消える場所へ黙って書かない）。
// - 読めない・形が違う（版・日付・負の値・知らない鍵）なら例外。黙って空にしない（空は「消費 0」と区別できない）。
public sealed class FileLlmSpendStore : ILlmSpendStore, IDisposable
{
    public const string PathConfigKey = "Llm:CallLimits:SpendStore:Path";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly string _tmpPath;
    private readonly string _lockPath;
    private readonly object _gate = new();
    private FileStream? _lock;
    private bool _disposed;

    public FileLlmSpendStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("当日の費用の置き場のパスが空である。", nameof(path));
        _path = Path.GetFullPath(path);
        _tmpPath = _path + ".tmp";
        _lockPath = _path + ".lock";
    }

    public bool IsDurable => true;

    public string FilePath => _path;

    public LlmSpendSnapshot? Load()
    {
        lock (_gate)
        {
            AcquireLock();
            if (!File.Exists(_path)) return null;
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllBytes(_path), Json)
                ?? throw new InvalidDataException("当日の費用のファイルが空である。");
            if (model.Version != FormatVersion)
                throw new InvalidDataException($"当日の費用のファイルの版が違う（{model.Version}）。");
            if (!DateOnly.TryParseExact(model.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new InvalidDataException("当日の費用のファイルの日付が読めない。");
            var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var (key, value) in model.Totals ?? [])
            {
                if (!(key.StartsWith("c|", StringComparison.Ordinal) || key.StartsWith("p|", StringComparison.Ordinal)))
                    throw new InvalidDataException("当日の費用のファイルに知らない形の鍵がある。");
                if (value < 0m)
                    throw new InvalidDataException("当日の費用のファイルに負の値がある。");
                totals[key] = value;
            }
            return new LlmSpendSnapshot(day, totals);
        }
    }

    public void Save(LlmSpendSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            AcquireLock();
            var model = new FileModel
            {
                Version = FormatVersion,
                Day = snapshot.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Totals = new Dictionary<string, decimal>(snapshot.Totals, StringComparer.Ordinal),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(model, Json);
            using (var fs = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(_tmpPath, _path, overwrite: true);
        }
    }

    private void AcquireLock()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lock is not null) return;
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException("当日の費用の置き場のディレクトリが無い（ボリュームが付いていない）。");
        // 取れなければ IOException（別のプロセスが持っている）。取れるまで、呼ばれるたびに取り直す。
        _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _lock?.Dispose();
            _lock = null;
        }
    }

    private sealed class FileModel
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("day")]
        public string? Day { get; set; }

        [JsonPropertyName("totals")]
        public Dictionary<string, decimal>? Totals { get; set; }
    }
}
