using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachForge.AI;
using ReachForge.AI.Evals;
using ReachForge.AI.Prompts;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;

// プロンプト・モデルの評価（RF-DES-001 4.6）。プロンプトやモデル設定を変えたときに CI で実行する。
//   dotnet run --project src/ReachForge.AI.Evals -- [--cases 50] [--prompt-file copy.sbn] [--out report.md] [--export-dataset path]
// モデルは AI:Providers / AI:Routes（環境変数 AI__Providers__... でも可）。未設定ならスタブで、評価の仕組みだけを確認する。
var arguments = Args.Parse(args);
if (arguments.ExportDataset is { } exportPath)
{
    await File.WriteAllTextAsync(exportPath, EvalDataset.Serialize(EvalDataset.Build()));
    Console.WriteLine($"Exported {EvalDataset.Build().Count} cases to {exportPath}");
    return 0;
}

var builder = Host.CreateApplicationBuilder();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["AI:Providers:local:Type"] = "Stub",
    ["AI:Routes:Default:0"] = "local",
});
builder.Configuration.AddEnvironmentVariables();
if (arguments.Config is { } configPath) builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITenantContext>(_ => new MutableTenantContext { IsSystem = true, UserName = "evals" });
builder.Services.AddReachForgeAi(builder.Configuration);
builder.Services.AddScoped<EvalRunner>();
using var host = builder.Build();

await using var scope = host.Services.CreateAsyncScope();
var dataset = EvalDataset.Load(arguments.Dataset);
var report = await scope.ServiceProvider.GetRequiredService<EvalRunner>().RunAsync(dataset, new EvalOptions
{
    MaxCases = arguments.Cases,
    // --prompt-file：未登録の copy.generate テンプレート（版数は 0 と表示）
    Overrides = arguments.PromptFile is { } file
        ? new Dictionary<string, StoredPrompt> { [PromptKeys.Copy] = new(PromptKeys.Copy, 0, await File.ReadAllTextAsync(file)) }
        : new Dictionary<string, StoredPrompt>(),
}, CancellationToken.None);

var markdown = report.ToMarkdown();
Console.WriteLine(markdown);
if (arguments.Out is { } outPath) await File.WriteAllTextAsync(outPath, markdown);
return report.Passed ? 0 : 1;

internal sealed record Args(int? Cases, string? PromptFile, string? Out, string? Dataset, string? Config, string? ExportDataset)
{
    public static Args Parse(string[] args)
    {
        string? Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return new Args(int.TryParse(Value("--cases"), out var n) ? n : null, Value("--prompt-file"), Value("--out"),
            Value("--dataset"), Value("--config"), Value("--export-dataset"));
    }
}
