var target = Argument("target", "RunClient");
var configuration = Argument("configuration", "Release");
var benchmarkFilter = Argument("benchmarkFilter", "*");

Task("InstallSdk")
.Does(() =>
    {
        IEnumerable<FilePath> sdkFiles = GetFiles("./sdk/*.json").Distinct();

        foreach (FilePath sdkFile in sdkFiles)
        {
            if (IsRunningOnWindows())
            {
                StartProcess("pwsh", $"-ExecutionPolicy Bypass -File ./script/dotnet-install.ps1 --jsonfile {sdkFile}");
            }
            else
            {
                StartProcess("bash", $"./script/dotnet-install.sh --jsonfile {sdkFile}");
            }
        }
    });

Task("RunClient")
    .IsDependentOn("InstallSdk")
    .Does(() =>
    {
        DotNetRun("./src/Tui/Tui.csproj", new DotNetRunSettings
        {
            Configuration = configuration,
        });
    });

Task("RunServer")
    .IsDependentOn("InstallSdk")
    .Does(() =>
    {
        DotNetRun("./src/Api/Api.csproj", new DotNetRunSettings
        {
            Configuration = configuration,
        });
    });

Task("Test")
    .IsDependentOn("InstallSdk")
    .Does(() =>
    {
        var projects = GetFiles("test/**/*.csproj");

        foreach (var proj in projects)
        {
            DotNetTest(proj.FullPath, new DotNetTestSettings
            {
                ArgumentCustomization = args => args.Append("--collect:\"XPlat Code Coverage\""),
            });
        }
    });

Task("GenerateCoverage")
    .IsDependentOn("Test")
    .Does(() =>
    {
        ReportGenerator(new GlobPattern("**/coverage.cobertura.xml"), Directory("./coveragereport"), new ReportGeneratorSettings
        {
            ReportTypes = [ReportGeneratorReportType.Html],
        });
    });

Task("Benchmark")
    .IsDependentOn("InstallSdk")
    .Does(() =>
    {
        const string benchmarkProject = "./bench/Api.Benchmarks/Api.Benchmarks.csproj";
        string benchmarkDll = $"./bench/Api.Benchmarks/bin/{configuration}/net10.0/Api.Benchmarks.dll";

        DotNetBuild(benchmarkProject, new DotNetBuildSettings
        {
            Configuration = configuration,
        });

        StartProcess("dotnet", new ProcessSettings
        {
            Arguments = $"{benchmarkDll} --filter \"{benchmarkFilter}\"",
        });
    });

RunTarget(target);
