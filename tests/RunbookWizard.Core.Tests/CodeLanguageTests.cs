using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;
using Xunit;

namespace RunbookWizard.Core.Tests;

public class CodeLanguageTests
{
    [Theory]
    [InlineData("powershell", "powershell")]
    [InlineData("PWSH", "powershell")]
    [InlineData("winps", "powershell")]
    [InlineData("bash", "bash")]
    [InlineData("sh", "bash")]
    [InlineData("cmd", "batch")]
    [InlineData("bat", "batch")]
    [InlineData("JSON", "json")]
    [InlineData("csharp", "csharp")]
    [InlineData("  yaml  ", "yaml")]
    public void Canonical_maps_aliases_and_lowercases_the_rest(string tag, string expected) =>
        Assert.Equal(expected, CodeLanguage.Canonical(tag));

    [Theory]
    [InlineData("powershell {name=install}", "powershell")]
    [InlineData("bash {\"name\":\"x\"}", "bash")]
    [InlineData("cmd  destructive=true", "batch")]
    public void Canonical_ignores_attributes_after_the_language(string info, string expected) =>
        Assert.Equal(expected, CodeLanguage.Canonical(info));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Canonical_is_null_without_a_tag(string? tag) => Assert.Null(CodeLanguage.Canonical(tag));

    [Theory]
    [InlineData(ShellKind.Powershell, "powershell")]
    [InlineData(ShellKind.Pwsh, "powershell")]
    [InlineData(ShellKind.Winps, "powershell")]
    [InlineData(ShellKind.Bash, "bash")]
    [InlineData(ShellKind.Sh, "bash")]
    [InlineData(ShellKind.Cmd, "batch")]
    public void ForShell_names_the_language_of_the_command(ShellKind shell, string expected) =>
        Assert.Equal(expected, CodeLanguage.ForShell(shell));
}
