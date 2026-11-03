using System.Security.Cryptography;
using System.Text.Json;
using NSubstitute;
using WheelWizard.GitHub.Domain;
using WheelWizard.Recomp;
using WheelWizard.Recomp.Domain;

namespace WheelWizard.Test.Features.Recomp;

public class RecompMacTests
{
    [Fact]
    public void SelectsPublishedMacPackage()
    {
        var release = new GithubRelease
        {
            TagName = "v0.9.3",
            Assets = [new GithubAsset { Name = "WiiCompiled-Setup.pkg", BrowserDownloadUrl = "https://example.com/setup.pkg" }],
        };
        Assert.Equal(
            "https://example.com/setup.pkg",
            RecompReleaseResolver.FindLatest([release], RecompPlatform.MacReleaseAssetName)?.SetupDownloadUrl
        );
    }

    [Fact]
    public async Task ChangedCodePulRequiresRebuildAndMissingAppIsBroken()
    {
        var root = Path.Combine(Path.GetTempPath(), "ww-mac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var environment = Substitute.For<IRecompEnvironment>();
            environment.BackendStateFilePath.Returns(Path.Combine(root, "install-state.json"));
            environment.InstallStateFilePath.Returns(Path.Combine(root, "frontend.json"));
            environment.InstallFolderPath.Returns(root);
            environment.RetroRewindFolderPath.Returns(Path.Combine(root, "RetroRewind6"));
            var code = Path.Combine(environment.RetroRewindFolderPath!, "Binaries", "Code.pul");
            Directory.CreateDirectory(Path.GetDirectoryName(code)!);
            File.WriteAllText(code, "original");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(code)));
            File.WriteAllText(
                environment.InstallStateFilePath,
                JsonSerializer.Serialize(new RecompMacInstallService.MacState("0.9.3", hash))
            );
            foreach (var name in new[] { "WiiCompiled", "RetroRewind" })
            {
                var exe = Path.Combine(root, "Products", name + ".app", "Contents", "MacOS", name);
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                File.WriteAllText(exe, "executable");
            }
            using var service = new RecompMacInstallService(
                environment,
                Substitute.For<IRecompProcessRunner>(),
                Substitute.For<IRecompSetupDownloader>(),
                null!,
                new Testably.Abstractions.RealFileSystem()
            );
            Assert.True((await service.CheckProductsAsync()).Value.RetroRewind.IsCurrent);
            File.WriteAllText(code, "updated");
            Assert.Equal(RecompProductState.CodePulChanged, (await service.CheckProductsAsync()).Value.RetroRewind.State);
            File.Delete(Path.Combine(root, "Products", "WiiCompiled.app", "Contents", "MacOS", "WiiCompiled"));
            Assert.False(service.IsInstalled);
            Assert.Equal(RecompProductState.Broken, (await service.CheckProductsAsync()).Value.Base.State);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
