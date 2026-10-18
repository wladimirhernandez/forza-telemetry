using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App.Tests;

public class RelaunchPlanTests
{
    [Fact]
    public void The_app_executable_is_started_again_with_the_original_arguments()
    {
        var plan = RelaunchPlan.Create(
            @"C:\Program Files\Forza Telemetry\ForzaTelemetry.App.exe",
            @"C:\Program Files\Forza Telemetry\ForzaTelemetry.App.dll",
            ["--log-level", "debug"]);

        Assert.NotNull(plan);
        Assert.Equal(@"C:\Program Files\Forza Telemetry\ForzaTelemetry.App.exe", plan.FileName);
        Assert.Equal(["--log-level", "debug"], plan.Arguments);
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE")]
    public void Under_the_dotnet_host_the_assembly_is_the_first_argument_and_the_user_arguments_follow(string host)
    {
        var plan = RelaunchPlan.Create(host, @"C:\src\bin\ForzaTelemetry.App.dll", ["--flag"]);

        Assert.NotNull(plan);
        Assert.Equal(host, plan.FileName);
        Assert.Equal([@"C:\src\bin\ForzaTelemetry.App.dll", "--flag"], plan.Arguments);
    }

    [Fact]
    public void An_executable_that_merely_has_dotnet_in_its_name_is_not_the_host()
    {
        var plan = RelaunchPlan.Create(@"C:\apps\dotnet-tool.exe", @"C:\apps\x.dll", []);

        Assert.NotNull(plan);
        Assert.Empty(plan.Arguments);
    }

    [Fact]
    public void Arguments_with_spaces_stay_one_element_each()
    {
        var plan = RelaunchPlan.Create(@"C:\app.exe", null, [@"C:\My Folder\file.json"]);

        Assert.Equal([@"C:\My Folder\file.json"], plan!.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_process_path_there_is_nothing_to_relaunch(string? processPath) =>
        Assert.Null(RelaunchPlan.Create(processPath, @"C:\x.dll", []));

    [Fact]
    public void Under_the_dotnet_host_without_an_assembly_path_there_is_nothing_to_relaunch() =>
        Assert.Null(RelaunchPlan.Create(@"C:\dotnet\dotnet.exe", null, []));
}
