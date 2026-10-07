using ReachForge.Web.Hosting;

// 組み立ては ReachForgeApp（Windows 版も同じものをプロセス内で使う）
var app = await ReachForgeApp.CreateAsync(WebApplication.CreateBuilder(args));
app.Run();

public partial class Program;
