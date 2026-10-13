using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using RunbookWizard.Core.Parsing;
using Xunit;

namespace RunbookWizard.Core.Tests;

/// <summary>Elevation handling (ADR 0013): the player never elevates itself; it gates elevated steps and relaunches on request.</summary>
public class ElevationTests
{
    [Fact]
    public void An_elevated_step_is_blocked_when_the_player_is_not_elevated()
    {
        var check = ElevationGate.Check(true, new FixedElevationProbe(false));
        Assert.True(check.Blocked);
        Assert.Contains("administrator", check.Reason);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Everything_else_is_allowed(bool stepNeedsElevation, bool playerElevated)
    {
        var check = ElevationGate.Check(stepNeedsElevation, new FixedElevationProbe(playerElevated));
        Assert.False(check.Blocked);
        Assert.Empty(check.Reason);
    }

    [Fact]
    public void The_real_probe_answers_without_throwing()
        => _ = new ProcessElevationProbe().IsElevated;

    [Fact]
    public void Counts_only_script_steps_that_need_elevation()
    {
        var result = RunbookParser.Parse("""
            ---
            runbook: 1
            title: T
            ---
            ## A
            ```step
            elevated: true
            ```
            ```powershell
            echo a
            ```
            ## B
            ```powershell
            echo b
            ```
            ## C
            ```step
            elevated: true
            type: manual
            ```
            Do it by hand.
            """);
        Assert.True(result.IsRunnable, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, ElevationGate.CountElevated(result.Runbook!.Steps));
        Assert.Equal("1 of 3 steps need administrator rights.", ElevationGate.Summary(1, 3));
        Assert.Empty(ElevationGate.Summary(0, 3));
    }

    [Fact]
    public void Relaunch_arguments_carry_the_guide_and_the_data_folder_only()
    {
        var args = ElevatedRelaunch.BuildArguments(@"C:\app\RunbookWizard.dll", @"C:\app\RunbookWizard.exe", @"C:\My Guides\iis site.runbook.md", @"C:\data");
        Assert.Equal(new[] { "--data-dir", @"C:\data", @"C:\My Guides\iis site.runbook.md" }, args);
    }

    [Fact]
    public void Relaunch_through_dotnet_passes_the_library_again()
    {
        var args = ElevatedRelaunch.BuildArguments(@"C:\app\RunbookWizard.dll", @"C:\Program Files\dotnet\dotnet.exe", @"C:\g.runbook.md", null);
        Assert.Equal(new[] { @"C:\app\RunbookWizard.dll", @"C:\g.runbook.md" }, args);
    }

    [Fact]
    public void Relaunch_start_info_asks_for_the_UAC_prompt_and_quotes_paths()
    {
        var info = ElevatedRelaunch.BuildStartInfo(null, @"C:\app\RunbookWizard.exe", @"C:\My Guides\iis site.runbook.md", null);
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
        Assert.Equal("\"C:\\My Guides\\iis site.runbook.md\"", info.Arguments);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData(@"a b", "\"a b\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void Quote_follows_the_windows_command_line_rules(string argument, string expected)
        => Assert.Equal(expected, ElevatedRelaunch.Quote(argument));
}
