using ModernUninstaller.Models;
using ModernUninstaller.Services;
using Xunit;

namespace ModernUninstaller.Tests;

public class LeftoverCandidatesTests
{
    [Fact]
    public void Strips_version_and_architecture_from_display_name()
    {
        var c = LeftoverService.Candidates(new ProgramItem { Name = "7-Zip 26.05 (arm64)", Id = "7-Zip" });
        Assert.Contains("7-Zip", c);
        Assert.DoesNotContain(c, x => x.Contains("26.05") || x.Contains("arm64"));
    }

    [Fact]
    public void Adds_install_folder_name_when_it_differs_from_display_name()
    {
        var c = LeftoverService.Candidates(new ProgramItem { Name = "Zq9 Tool 3.2", InstallLocation = @"C:\Program Files\ZedTool" });
        Assert.Contains("ZedTool", c);
        Assert.Contains("Zq9 Tool", c);
    }

    [Fact]
    public void Generic_names_are_never_candidates()
    {
        var c = LeftoverService.Candidates(new ProgramItem { Name = "Microsoft", InstallLocation = @"C:\Program Files\Common Files" });
        Assert.Empty(c);
    }

    [Fact]
    public void Too_short_names_are_never_candidates()
    {
        Assert.Empty(LeftoverService.Candidates(new ProgramItem { Name = "Go" }));
    }

    [Fact]
    public void Forced_placeholder_id_is_not_used_as_name()
    {
        var c = LeftoverService.Candidates(new ProgramItem { Id = "forced", Name = "SomeOldTool", InstallLocation = @"D:\Apps\SomeOldTool" });
        Assert.DoesNotContain("forced", c);
        Assert.Contains("SomeOldTool", c);
    }

    [Fact]
    public void System_install_folders_are_not_used_as_names()
    {
        var c = LeftoverService.Candidates(new ProgramItem { Name = "Windows", InstallLocation = @"C:\Windows\System32" });
        Assert.Empty(c);
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5,0 MB")]
    public void FormatSize_is_human_readable(long bytes, string expectedDe)
    {
        var expected = expectedDe.Replace(',', System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0]);
        Assert.Equal(expected, ProgramItem.FormatSize(bytes));
    }
}
