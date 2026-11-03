using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using WheelWizard.Models.Enums;
using WheelWizard.Recomp.Domain;

namespace WheelWizard.Recomp;

/// <summary>The macOS package contains a setup.command, not the Linux subcommand host.</summary>
public sealed class RecompMacInstallService(
    IRecompEnvironment environment,
    IRecompProcessRunner runner,
    IRecompSetupDownloader downloader,
    RecompSetupHostAcquirer releases,
    IFileSystem fileSystem
) : IRecompInstallService
{
    private readonly SemaphoreSlim gate = new(1);
    private bool reconciled;
    private string Backend => fileSystem.Path.GetDirectoryName(environment.BackendStateFilePath)!;
    private string Products => fileSystem.Path.Combine(Backend, "Products");
    private string Expanded => fileSystem.Path.Combine(environment.InstallFolderPath, "Package");
    private string Script => FindScript(Expanded);
    public bool OperationInFlight => gate.CurrentCount == 0;
    public bool IsInstalled => RecompVersion.TryParse(ReadState()?.Version, out _) && fileSystem.File.Exists(Executable("WiiCompiled"));

    private string Executable(string name) => fileSystem.Path.Combine(Products, name + ".app", "Contents", "MacOS", name);

    private MacState? ReadState()
    {
        try
        {
            return JsonSerializer.Deserialize<MacState>(fileSystem.File.ReadAllText(environment.InstallStateFilePath));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal string FindScript(string directory) =>
        fileSystem.Directory.Exists(directory)
            ? fileSystem
                .Directory.EnumerateFiles(directory, "setup.command", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.EndsWith("WiiCompiled Setup.app/Contents/Resources/setup.command", StringComparison.Ordinal))
                ?? string.Empty
            : string.Empty;

    public Task<string?> GetInstalledVersionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(IsInstalled ? ReadState()?.Version : null);

    private string? CodeHash()
    {
        var path = fileSystem.Path.Combine(environment.RetroRewindFolderPath ?? "", "Binaries", "Code.pul");
        if (!fileSystem.File.Exists(path))
            return null;
        using var stream = fileSystem.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public Task<OperationResult<RecompProductsEvent>> CheckProductsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = ReadState();
        RecompProductStatus Presence(string name) =>
            new(fileSystem.File.Exists(Executable(name)) ? RecompProductState.Current : RecompProductState.Broken, name);
        var retro = Presence("RetroRewind");
        if (environment.RetroRewindFolderPath is not null && (state?.CodeHash is null || state.CodeHash != CodeHash()))
            retro = new(RecompProductState.CodePulChanged, "Retro Rewind changed since the last build.");
        return Task.FromResult<OperationResult<RecompProductsEvent>>(
            Ok(new RecompProductsEvent(state?.Version, environment.InstallFolderPath, false, Presence("WiiCompiled"), retro))
        );
    }

    public async Task<WheelWizardStatus> GetCurrentStatusAsync(CancellationToken cancellationToken = default)
    {
        var release = await releases.TryGetLatestReleaseAsync(cancellationToken);
        var products = await CheckProductsAsync(cancellationToken);
        return RecompStatusResolver.Resolve(
            fileSystem.File.Exists(environment.GameFilePath),
            IsInstalled ? ReadState()?.Version : null,
            release?.TagName,
            products.IsSuccess ? products.Value : null,
            OperationInFlight
        );
    }

    public async Task<OperationResult> InstallAsync(
        IProgress<RecompInstallProgress>? progress = null,
        Func<Task<bool>>? confirmOfflineInstall = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            return Fail("Another WiiCompiled operation is already running.");
        try
        {
            reconciled = false;
            if (!fileSystem.File.Exists(environment.GameFilePath))
                return Fail("Configure a Mario Kart Wii disc image before installing WiiCompiled.");
            var release = await releases.TryGetLatestReleaseAsync(cancellationToken);
            if (release is null)
                return Fail("Could not find a WiiCompiled macOS setup package.");
            fileSystem.Directory.CreateDirectory(environment.CacheFolderPath);
            var package = fileSystem.Path.Combine(environment.CacheFolderPath, RecompPlatform.CachedSetupFileName(release.TagName));
            progress?.Report(new("Downloading WiiCompiled setup", 5));
            var download = await downloader.DownloadAsync(release.SetupDownloadUrl, package, null, cancellationToken);
            if (download.IsFailure)
                return download.Error;
            fileSystem.Directory.CreateDirectory(environment.InstallFolderPath);
            fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(environment.InstallStateFilePath)!);
            var staged = fileSystem.Path.Combine(environment.InstallFolderPath, "Package-" + Guid.NewGuid().ToString("N"));
            try
            {
                var expand = await runner.RunAsync("/usr/sbin/pkgutil", ["--expand-full", package, staged], null, null, cancellationToken);
                if (expand.IsFailure)
                    return expand.Error;
                if (expand.Value != 0 || string.IsNullOrEmpty(FindScript(staged)))
                    return Fail("The WiiCompiled package could not be extracted.");
                var script = FindScript(staged);
                var versionFile = fileSystem.Path.Combine(fileSystem.Path.GetDirectoryName(script)!, "workspace", ".bundle-version");
                if (
                    !fileSystem.File.Exists(versionFile)
                    || !RecompSetupHostAcquirer.VersionsMatch(fileSystem.File.ReadAllText(versionFile).Trim(), release.TagName)
                )
                    return Fail("The macOS package version does not match the selected release.");
                // Replacing the toolkit invalidates any previously recorded product authorization.
                fileSystem.File.Delete(environment.InstallStateFilePath);
                if (fileSystem.Directory.Exists(Expanded))
                    fileSystem.Directory.Delete(Expanded, true);
                fileSystem.Directory.Move(staged, Expanded);
            }
            finally
            {
                if (fileSystem.Directory.Exists(staged))
                    fileSystem.Directory.Delete(staged, true);
            }
            return await BuildAsync(release.TagName, progress, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Fail(e);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<OperationResult> BuildAsync(string version, IProgress<RecompInstallProgress>? progress, CancellationToken token)
    {
        if (!fileSystem.File.Exists(Script))
            return Fail("The installed macOS setup is missing. Reinstall WiiCompiled.");
        List<string> arguments = [Script, "--game", environment.GameFilePath, "--install-location", "user"];
        if (environment.RetroRewindFolderPath is { } retro)
            arguments.AddRange(["--retro-dir", retro]);
        var hash = CodeHash();
        progress?.Report(new("Building WiiCompiled", 35));
        var result = await runner.RunAsync("/bin/bash", arguments, null, line => progress?.Report(new(line, 50)), token);
        if (result.IsFailure)
            return result.Error;
        if (result.Value != 0)
            return Fail($"The macOS WiiCompiled setup exited with code {result.Value}. See the application log for details.");
        if (
            !fileSystem.File.Exists(Executable("WiiCompiled"))
            || (environment.RetroRewindFolderPath is not null && !fileSystem.File.Exists(Executable("RetroRewind")))
        )
            return Fail("The macOS setup did not produce the expected apps.");
        fileSystem.File.WriteAllText(environment.InstallStateFilePath, JsonSerializer.Serialize(new MacState(version, hash)));
        progress?.Report(new("WiiCompiled installation complete", 100));
        return Ok();
    }

    public async Task<OperationResult> ReconcileForLaunchAsync(
        IProgress<RecompInstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            return Fail("Another WiiCompiled operation is already running.");
        try
        {
            reconciled = false;
            var state = ReadState();
            if (state is null)
                return Fail("Install WiiCompiled before launching.");
            var products = await CheckProductsAsync(cancellationToken);
            if (products.IsFailure)
                return products.Error;
            if (products.Value.ActionRequired)
            {
                var build = await BuildAsync(state.Version, progress, cancellationToken);
                if (build.IsFailure)
                    return build;
            }
            reconciled = true;
            return Ok();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationResult> LaunchAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            return Fail("Another WiiCompiled operation is already running.");
        try
        {
            if (!reconciled)
                return Fail("Check WiiCompiled before launching.");
            reconciled = false;
            var result = await runner.RunAsync(Executable("RetroRewind"), [], Backend, null, cancellationToken);
            return result.IsFailure ? result.Error
                : result.Value == 0 ? Ok()
                : Fail($"WiiCompiled exited with code {result.Value}.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            return Fail("Another WiiCompiled operation is already running.");
        try
        {
            reconciled = false;
            foreach (var name in new[] { "WiiCompiled", "RetroRewind" })
            {
                var app = fileSystem.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Applications",
                    name + ".app"
                );
                if (fileSystem.Directory.Exists(app))
                    fileSystem.Directory.Delete(app, true);
            }
            foreach (var path in new[] { Backend, environment.InstallFolderPath, environment.CacheFolderPath })
                if (fileSystem.Directory.Exists(path))
                    fileSystem.Directory.Delete(path, true);
            return Ok();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Fail(e);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() { }

    public sealed record MacState(string Version, string? CodeHash);
}
