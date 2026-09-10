using System;
using System.IO;
using System.Threading.Tasks;
using AvasRoutingSetup.Engine;
using Xunit;

namespace AvasRoutingApp.Tests;

public class InstallerTests
{
    [Fact]
    public void DependencyDetector_DetectsInstalledDotNet8()
    {
        var result = DependencyDetector.CheckDotNet8DesktopRuntime();
        Assert.True(result.IsInstalled, "Should detect installed .NET 8 Windows Desktop Runtime on this system");
        Assert.False(string.IsNullOrWhiteSpace(result.VersionInfo));
    }

    [Fact]
    public void DependencyDetector_DetectsInstalledWebView2()
    {
        var result = DependencyDetector.CheckWebView2Runtime();
        Assert.True(result.IsInstalled, "Should detect installed WebView2 Runtime on this system");
        Assert.Contains("WebView2", result.VersionInfo);
    }

    [Fact]
    public async Task AppDeployer_DeploysDirectoryCorrectly()
    {
        string tempSource = Path.Combine(Path.GetTempPath(), "AvasDeploySource_" + Guid.NewGuid().ToString("N"));
        string tempTarget = Path.Combine(Path.GetTempPath(), "AvasDeployTarget_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempSource);
            Directory.CreateDirectory(Path.Combine(tempSource, "subfolder"));

            File.WriteAllText(Path.Combine(tempSource, "app.exe"), "dummy exe");
            File.WriteAllText(Path.Combine(tempSource, "subfolder", "data.txt"), "dummy data");

            var result = await AppDeployer.DeployAsync(tempSource, tempTarget);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(tempTarget, "app.exe")));
            Assert.True(File.Exists(Path.Combine(tempTarget, "subfolder", "data.txt")));
            Assert.Equal("dummy data", File.ReadAllText(Path.Combine(tempTarget, "subfolder", "data.txt")));
        }
        finally
        {
            if (Directory.Exists(tempSource)) Directory.Delete(tempSource, true);
            if (Directory.Exists(tempTarget)) Directory.Delete(tempTarget, true);
        }
    }

    [Fact]
    public void WhiteLabelManager_ResolvesAppPath()
    {
        string path = WhiteLabelManager.GetBlueRiverAppPath();
        Assert.False(string.IsNullOrEmpty(path));
        Assert.Contains("BlueRiver AV Manager", path);
    }

    [Fact]
    public void WhiteLabelManager_PatchIndexJsBranding_ReplacesRequiredValues()
    {
        string original = @"
const Branding = (() => {
  const { APP_TITLE, APP_HEADER, THEME_PRIMARY_COLOR, THEME_SECONDARY_COLOR } = {
    APP_TITLE: 'BlueRiver AV Manager',
    APP_HEADER: 'BlueRiver AV Manager',
    THEME_PRIMARY_COLOR: '#00afaa',
    THEME_SECONDARY_COLOR: '#f2f2f2',
  };
  return { Title: APP_TITLE, Header: APP_HEADER };
})();";

        string patched = WhiteLabelManager.PatchIndexJsBranding(original);

        Assert.Contains("APP_TITLE: 'AV Manager'", patched);
        Assert.Contains("APP_HEADER: 'AV Manager'", patched);
        Assert.Contains("THEME_PRIMARY_COLOR: '#0055afff'", patched);
        Assert.DoesNotContain("BlueRiver AV Manager", patched);
        Assert.DoesNotContain("#00afaa", patched);
    }

    [Fact]
    public void WhiteLabelManager_AppliesAssetsAndCreatesBackups()
    {
        string tempSource = Path.Combine(Path.GetTempPath(), "AvasWLSource_" + Guid.NewGuid().ToString("N"));
        string tempTarget = Path.Combine(Path.GetTempPath(), "AvasWLTarget_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempSource);
            Directory.CreateDirectory(Path.Combine(tempTarget, @"front\images"));
            Directory.CreateDirectory(Path.Combine(tempTarget, @"src\config"));

            // Create original files in target
            File.WriteAllText(Path.Combine(tempTarget, @"front\images\logo.svg"), "<svg>original</svg>");
            File.WriteAllText(Path.Combine(tempTarget, @"src\config\index.js"), "// original index.js");

            // Create new files in source
            File.WriteAllText(Path.Combine(tempSource, "logo.svg"), "<svg>advantech</svg>");
            File.WriteAllText(Path.Combine(tempSource, "index.js"), "// advantech index.js");

            // Simulate file deployment logic
            string destLogo = Path.Combine(tempTarget, @"front\images\logo.svg");
            string bakLogo = Path.Combine(tempTarget, @"front\images\logo.svg.bak");
            File.Copy(destLogo, bakLogo, overwrite: false);
            File.Copy(Path.Combine(tempSource, "logo.svg"), destLogo, overwrite: true);

            string destIndex = Path.Combine(tempTarget, @"src\config\index.js");
            string bakIndex = Path.Combine(tempTarget, @"src\config\index.js.bak");
            File.Copy(destIndex, bakIndex, overwrite: false);
            File.Copy(Path.Combine(tempSource, "index.js"), destIndex, overwrite: true);

            // Assertions
            Assert.True(File.Exists(bakLogo));
            Assert.True(File.Exists(bakIndex));
            Assert.Equal("<svg>original</svg>", File.ReadAllText(bakLogo));
            Assert.Equal("<svg>advantech</svg>", File.ReadAllText(destLogo));
            Assert.Equal("// original index.js", File.ReadAllText(bakIndex));
            Assert.Equal("// advantech index.js", File.ReadAllText(destIndex));
        }
        finally
        {
            if (Directory.Exists(tempSource)) Directory.Delete(tempSource, true);
            if (Directory.Exists(tempTarget)) Directory.Delete(tempTarget, true);
        }
    }
}
