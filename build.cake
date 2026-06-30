var target = Argument("target", "Run");
var configuration = Argument("configuration", "Release");

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

Task("Run")
    .IsDependentOn("InstallSdk")
    .Does(() =>
    {
        DotNetRun("./src/SampleApp.UI/SampleApp.UI.csproj", new DotNetRunSettings
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

RunTarget(target);
