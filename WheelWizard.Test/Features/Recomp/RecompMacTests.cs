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
    public async Task FirstInstallCreatesFrontendStateDirectorySeparateFromBackend()
    {
        var fs = new Testably.Abstractions.Testing.MockFileSystem();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ww-mac-first-install"));
        var backend = Path.Combine(root, "WiiCompiled");
        var statePath = Path.Combine(root, "CT-MKWII", "Recomp", "Install", "install-state.json");
        fs.Directory.CreateDirectory(root);
        var game = Path.Combine(root, "game.iso");
        fs.File.WriteAllText(game, "disc");
        var environment = Substitute.For<IRecompEnvironment>();
        environment.GameFilePath.Returns(game);
        environment.RetroRewindFolderPath.Returns((string?)null);
        environment.InstallFolderPath.Returns(backend);
        environment.BackendStateFilePath.Returns(Path.Combine(backend, "install-state.json"));
        environment.InstallStateFilePath.Returns(statePath);
        environment.CacheFolderPath.Returns(Path.Combine(root, "Cache"));
        var runner = Substitute.For<IRecompProcessRunner>();
        runner
            .RunAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string?>(),
                Arg.Any<Action<string>?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                var args = call.ArgAt<IReadOnlyList<string>>(1);
                if (call.ArgAt<string>(0) == "/usr/bin/xcrun")
                {
                    call.ArgAt<Action<string>?>(3)?.Invoke("/Applications/Xcode.app/SDKs/MacOSX.sdk");
                    return Ok(0);
                }
                if (call.ArgAt<string>(0) == "/usr/bin/env")
                    Assert.Equal("SDKROOT=/Applications/Xcode.app/SDKs/MacOSX.sdk", args[0]);
                if (call.ArgAt<string>(0) == "/usr/sbin/pkgutil")
                {
                    var resources = Path.Combine(args[2], "Payload", "Applications", "WiiCompiled Setup.app", "Contents", "Resources");
                    fs.Directory.CreateDirectory(Path.Combine(resources, "workspace"));
                    fs.File.WriteAllText(Path.Combine(resources, "setup.command"), "setup");
                    fs.File.WriteAllText(Path.Combine(resources, "workspace", ".bundle-version"), "0.9.3");
                }
                else
                {
                    var executable = Path.Combine(backend, "Products", "WiiCompiled.app", "Contents", "MacOS", "WiiCompiled");
                    fs.Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                    fs.File.WriteAllText(executable, "binary");
                }
                return Ok(0);
            });
        var downloader = Substitute.For<IRecompSetupDownloader>();
        downloader
            .DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>())
            .Returns(Ok());
        var github = Substitute.For<WheelWizard.GitHub.IGitHubSingletonService>();
        github
            .GetReleasesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(
                Ok(
                    new List<GithubRelease>
                    {
                        new()
                        {
                            TagName = "v0.9.3",
                            Assets =
                            [
                                new GithubAsset
                                {
                                    Name = RecompPlatform.ReleaseAssetName,
                                    BrowserDownloadUrl = "https://example.com/setup.pkg",
                                },
                            ],
                        },
                    }
                )
            );
        var hosts = new RecompSetupHostAcquirer(
            runner,
            downloader,
            github,
            fs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RecompSetupHostAcquirer>.Instance
        );
        using var service = new RecompMacInstallService(environment, runner, downloader, hosts, fs);
        var result = await service.InstallAsync();
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");
        Assert.True(fs.File.Exists(statePath));
        Assert.True(service.IsInstalled);
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
