using CuteDB.Cli;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(CliApp.Configure);
return await app.RunAsync(args);

/// <summary>Entry point marker, so the assembly version can be found from top-level statements.</summary>
public partial class Program;
