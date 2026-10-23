using Microsoft.Extensions.Logging;
using NSubstitute;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Infrastructure.Esl;
using SkyrimModPacker.Infrastructure.FileSystem;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class PackingLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PackingLifecycleTests", Guid.NewGuid().ToString("N"));
    private readonly List<PackingContext> _contexts = [];
    private readonly List<string> _foreignStages = [];
    private string Data => Path.Combine(_root, "Game", "Data");
    private string Output => Path.Combine(_root, "Output", "Sword-1.0.zip");
    private ModProfile Profile => new() { Name = "Sword", Version = "1.0", PluginFileName = "Sword.esp", Keywords = ["Sword"] };
    private AppSettings Settings(bool useTemp = true) => new() { SkyrimDataPath = Data, OutputDirectory = Path.GetDirectoryName(Output)!, UseTempDirectory = useTemp, OverwriteExistingArchive = true };

    public PackingLifecycleTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "meshes", "Sword"));
        File.WriteAllText(Path.Combine(Data, "Sword.esp"), "plugin fixture");
        File.WriteAllText(Path.Combine(Data, "meshes", "Sword", "model.nif"), "model fixture");
    }

    public void Dispose()
    {
        foreach (var context in _contexts) Assert.True(context.TryCleanupStaging(), string.Join("; ", context.Warnings));
        foreach (var stage in _foreignStages) PackingPathGuard.DeleteStagingDirectory(stage, Data);
        var prefix = Path.Combine(Path.GetTempPath(), "PackingLifecycleTests") + Path.DirectorySeparatorChar;
        Assert.StartsWith(prefix, Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(_root, recursive: true);
    }

    private PackingContext Track(PackingContext context)
    {
        _contexts.Add(context);
        return context;
    }

    private PackingPipeline Pipeline(params IPackStep[] tail) => new([
        new Step("Capture", 0, (context, _) => { Track(context); return Task.FromResult(true); }),
        new ScanStep(new ModScanner()), new FilterStep(), new ValidateStep(new ZipArchiver()), new CopyStep(), .. tail
    ], new CallbackLogger<PackingPipeline>());

    private void CreateOldArchive()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        File.WriteAllText(Output, "previous archive");
    }

    private async Task<PackingContext> PreparedContext(bool esl = false)
    {
        var profile = Profile;
        profile.EnableEsl = esl;
        var context = Track(new PackingContext
        {
            Profile = profile, Settings = Settings(), OutputArchivePath = Output,
            SelectedFiles = [new ScannedFile { AbsolutePath = Path.Combine(Data, "meshes", "Sword", "model.nif"), RelativePath = "meshes/Sword/model.nif", Category = FileCategory.Mesh }]
        });
        Assert.True(await new CopyStep().ExecuteAsync(context));
        Assert.NotNull(context.OwnedStagingDirectory);
        return context;
    }

    private static void AssertCleaned(PackingContext context)
    {
        Assert.Null(context.OwnedStagingDirectory);
        if (context.StagingDirectory != null) Assert.False(Directory.Exists(context.StagingDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_FailureAfterCopy_CleansStagingAndDoesNotReuseOldArchive(bool throws)
    {
        CreateOldArchive();
        var original = await File.ReadAllBytesAsync(Output);
        var injected = new IOException("Injected step failure");
        var laterCalled = false;
        var pipeline = Pipeline(
            new Step("Fail after copy", 425, (_, _) => throws ? Task.FromException<bool>(injected) : Task.FromResult(false)),
            new Step("Must not run", 450, (_, _) => { laterCalled = true; return Task.FromResult(true); }));

        if (throws) Assert.Same(injected, await Assert.ThrowsAsync<IOException>(() => pipeline.ExecuteAsync(Profile, Settings())));
        else Assert.False((await pipeline.ExecuteAsync(Profile, Settings())).IsSuccess);

        var context = Assert.Single(_contexts);
        Assert.Equal(PackingStatus.Failed, context.Status);
        Assert.False(context.IsSuccess);
        Assert.False(context.ArchiveCreated);
        Assert.Contains(context.Errors, error => error.Contains("Fail after copy"));
        Assert.False(laterCalled);
        AssertCleaned(context);
        Assert.Equal(original, await File.ReadAllBytesAsync(Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_CancelBetweenCopyAndNextStep_CleansWithCancelledToken(bool esl)
    {
        CreateOldArchive();
        var profile = Profile;
        profile.EnableEsl = esl;
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(_ =>
        {
            if (_contexts.SingleOrDefault()?.OwnedStagingDirectory != null) cancellation.Cancel();
        });
        var laterCalled = false;
        var pipeline = Pipeline(new Step("Must not run", 450, (_, _) => { laterCalled = true; return Task.FromResult(true); }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(profile, Settings(useTemp: !esl), progress, cancellation.Token));

        var context = Assert.Single(_contexts);
        Assert.Equal(PackingStatus.Cancelled, context.Status);
        Assert.False(context.IsSuccess);
        Assert.False(laterCalled);
        AssertCleaned(context);
        Assert.Equal("previous archive", await File.ReadAllTextAsync(Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyStep_CancelInsideCopy_CleansWithoutRequiringEsl(bool esl)
    {
        using var cancellation = new CancellationTokenSource();
        var profile = Profile;
        profile.EnableEsl = esl;
        var context = Track(new PackingContext
        {
            Profile = profile, Settings = Settings(),
            SelectedFiles = [new ScannedFile { AbsolutePath = Path.Combine(Data, "Sword.esp"), RelativePath = "Sword.esp", Category = FileCategory.Plugin }]
        });
        var logger = new CallbackLogger<CopyStep>((_, message) => { if (message.StartsWith("Copying ")) cancellation.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CopyStep(logger).ExecuteAsync(context, cancellation.Token));

        Assert.NotNull(context.StagingDirectory);
        AssertCleaned(context);
        Assert.Empty(context.Warnings);
        Assert.Equal("plugin fixture", await File.ReadAllTextAsync(Path.Combine(Data, "Sword.esp")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneFinalSteps_PreCancelled_CleanOwnedStaging(bool eslStep)
    {
        var context = await PreparedContext(eslStep);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IPackStep step = eslStep ? new EslFlagStep(new EslService()) : new ArchiveStep(new ZipArchiver());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.ExecuteAsync(context, cancellation.Token));

        AssertCleaned(context);
        Assert.False(context.ArchiveCreated);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_CancelDuringZip_RemovesPartialArchiveAndStaging(bool useTemp)
    {
        using var cancellation = new CancellationTokenSource();
        var archiving = false;
        var progress = new CallbackProgress(_ => { if (archiving) cancellation.Cancel(); });
        var pipeline = Pipeline(
            new Step("Before ZIP", 490, (_, _) => { archiving = true; return Task.FromResult(true); }),
            new ArchiveStep(new ZipArchiver()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(Profile, Settings(useTemp), progress, cancellation.Token));

        var context = Assert.Single(_contexts);
        Assert.Equal(PackingStatus.Cancelled, context.Status);
        Assert.False(context.IsSuccess);
        Assert.False(File.Exists(Output));
        AssertCleaned(context);
    }

    [Fact]
    public async Task Pipeline_ErrorReportedBySuccessfulStep_StillStopsAndCleans()
    {
        var laterCalled = false;
        var result = await Pipeline(
            new Step("Reports error", 425, (context, _) => { context.Errors.Add("Reported error"); return Task.FromResult(true); }),
            new Step("Must not run", 450, (_, _) => { laterCalled = true; return Task.FromResult(true); })).ExecuteAsync(Profile, Settings());

        Assert.Equal(PackingStatus.Failed, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal("Reported error", Assert.Single(result.Errors));
        Assert.False(laterCalled);
        AssertCleaned(result);
    }

    [Fact]
    public async Task Pipeline_WithoutArchiveStep_DoesNotTreatOldArchiveAsSuccess()
    {
        CreateOldArchive();

        var result = await Pipeline().ExecuteAsync(Profile, Settings());

        Assert.Equal(PackingStatus.Failed, result.Status);
        Assert.False(result.ArchiveCreated);
        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        AssertCleaned(result);
        Assert.Equal("previous archive", await File.ReadAllTextAsync(Output));
    }

    [Fact]
    public async Task Pipeline_EmptyStepList_FailsWithoutAnArchive()
    {
        var result = await new PackingPipeline([], new CallbackLogger<PackingPipeline>()).ExecuteAsync(Profile, Settings());

        Assert.Equal(PackingStatus.Failed, result.Status);
        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Context_ExistingArchiveWithoutCurrentCompletion_IsNotSuccess()
    {
        CreateOldArchive();
        var context = new PackingContext { Profile = Profile, Settings = Settings(), OutputArchivePath = Output };

        Assert.Equal(PackingStatus.NotStarted, context.Status);
        Assert.False(context.IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_Success_RequiresCurrentArchiveAndReleasesStaging(bool useTemp)
    {
        var result = await Pipeline(new ArchiveStep(new ZipArchiver())).ExecuteAsync(Profile, Settings(useTemp));

        Assert.Equal(PackingStatus.Succeeded, result.Status);
        Assert.True(result.ArchiveCreated);
        Assert.True(result.IsSuccess);
        Assert.True(result.ArchiveSizeBytes > 0);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        AssertCleaned(result);
    }

    [Fact]
    public async Task Pipeline_FailureAfterArchive_IsNotSuccessEvenWhenNewArchiveExists()
    {
        var result = await Pipeline(new ArchiveStep(new ZipArchiver()),
            new Step("Post archive failure", 600, (_, _) => Task.FromResult(false))).ExecuteAsync(Profile, Settings());

        Assert.True(File.Exists(Output));
        Assert.True(result.ArchiveCreated);
        Assert.Equal(PackingStatus.Failed, result.Status);
        Assert.False(result.IsSuccess);
        AssertCleaned(result);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task CleanupFailure_IsReportedWithoutHidingOriginalOutcome(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        FileStream? heldFile = null;
        var original = new IOException("Original failure");
        var pipeline = Pipeline(new Step("Fail with locked staging", 425, (context, _) =>
        {
            heldFile = new FileStream(Path.Combine(context.StagingDirectory!, "Sword.esp"), FileMode.Open, FileAccess.Read, FileShare.None);
            if (outcome == "exception") return Task.FromException<bool>(original);
            if (outcome == "cancel") cancellation.Cancel();
            return Task.FromResult(false);
        }));
        try
        {
            if (outcome == "exception") Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => pipeline.ExecuteAsync(Profile, Settings())));
            else if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(Profile, Settings(), cancellationToken: cancellation.Token));
            else Assert.False((await pipeline.ExecuteAsync(Profile, Settings())).IsSuccess);

            var context = Assert.Single(_contexts);
            Assert.Equal(outcome == "cancel" ? PackingStatus.Cancelled : PackingStatus.Failed, context.Status);
            Assert.False(context.IsSuccess);
            Assert.NotNull(context.OwnedStagingDirectory);
            Assert.Contains(context.StagingDirectory!, Assert.Single(context.Warnings));
            Assert.Contains(context.LogMessages, message => message.Contains("Could not clean"));
            if (outcome == "exception") Assert.Contains(context.Errors, error => error.Contains(original.Message));
        }
        finally { heldFile?.Dispose(); }

        var remaining = Assert.Single(_contexts);
        Assert.True(remaining.TryCleanupStaging());
        AssertCleaned(remaining);
    }

    [Fact]
    public async Task Pipeline_CancelAtFinalProgress_DoesNotReportSuccessForCompletedArchive()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(_ => { if (_contexts.SingleOrDefault()?.ArchiveCreated == true) cancellation.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline(new ArchiveStep(new ZipArchiver()))
            .ExecuteAsync(Profile, Settings(), progress, cancellation.Token));

        var context = Assert.Single(_contexts);
        Assert.Equal(PackingStatus.Cancelled, context.Status);
        Assert.True(context.ArchiveCreated);
        Assert.True(File.Exists(Output));
        Assert.False(context.IsSuccess);
        AssertCleaned(context);
    }

    [Fact]
    public async Task BuildCommand_CleanupWarning_RemainsVisibleWhenArchiveSucceeded()
    {
        FileStream? heldFile = null;
        var history = Substitute.For<IBuildHistoryRepository>();
        history.AddAsync(Arg.Any<BuildRecord>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            heldFile = new FileStream(Path.Combine(_contexts.Single().StagingDirectory!, "Sword.esp"), FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.CompletedTask;
        });
        var profiles = Substitute.For<IProfileRepository>();
        profiles.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ModProfile> { Profile });
        var settings = Substitute.For<ISettingsRepository>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(Settings());
        var vm = new BuildViewModel(profiles, settings, new ModScanner(), Pipeline(new ArchiveStep(new ZipArchiver(), history)));
        try
        {
            await vm.ScanCommand.ExecuteAsync(null);
            await vm.PackCommand.ExecuteAsync(null);

            var context = Assert.Single(_contexts);
            Assert.Equal(PackingStatus.Succeeded, context.Status);
            Assert.True(context.IsSuccess);
            Assert.Single(context.Warnings);
            Assert.True(vm.HasSuccessNotification);
            Assert.Null(vm.ErrorMessage);
            Assert.Contains(context.Warnings[0], vm.StatusMessage);
            Assert.True(File.Exists(vm.LastArchiveCreatedPath));
        }
        finally { heldFile?.Dispose(); }
        Assert.True(_contexts.Single().TryCleanupStaging());
        AssertCleaned(_contexts.Single());
    }

    [Fact]
    public async Task ArchiveStep_UnregisteredStagingWithValidName_IsPreserved()
    {
        var foreign = Path.Combine(Path.GetTempPath(), "SkyrimModPacker", "staging", Guid.NewGuid().ToString("N"));
        PackingPathGuard.EnsureStagingDirectory(foreign, Data);
        Directory.CreateDirectory(foreign);
        _foreignStages.Add(foreign);
        var sentinel = Path.Combine(foreign, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unregistered workspace");
        var context = Track(new PackingContext { Profile = Profile, Settings = Settings(), OutputArchivePath = Output, StagingDirectory = foreign });

        Assert.False(await new ArchiveStep(new ZipArchiver()).ExecuteAsync(context));

        Assert.Contains("does not belong", Assert.Single(context.Errors));
        Assert.Equal("unregistered workspace", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task Pipeline_ChangedStagingPointer_CleansOwnedDirectoryAndPreservesForeignOne()
    {
        var foreign = Path.Combine(Path.GetTempPath(), "SkyrimModPacker", "staging", Guid.NewGuid().ToString("N"));
        PackingPathGuard.EnsureStagingDirectory(foreign, Data);
        Directory.CreateDirectory(foreign);
        _foreignStages.Add(foreign);
        var sentinel = Path.Combine(foreign, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "foreign workspace");
        string? owned = null;
        var result = await Pipeline(new Step("Change pointer", 425, (context, _) =>
        {
            owned = context.OwnedStagingDirectory;
            context.StagingDirectory = foreign;
            return Task.FromResult(false);
        })).ExecuteAsync(Profile, Settings());

        Assert.Equal(PackingStatus.Failed, result.Status);
        Assert.NotNull(owned);
        Assert.False(Directory.Exists(owned));
        Assert.Null(result.OwnedStagingDirectory);
        Assert.Equal("foreign workspace", await File.ReadAllTextAsync(sentinel));
    }

    private sealed class Step(string name, int order, Func<PackingContext, CancellationToken, Task<bool>> execute) : IPackStep
    {
        public string Name => name;
        public int Order => order;
        public Task<bool> ExecuteAsync(PackingContext context, CancellationToken cancellationToken = default) => execute(context, cancellationToken);
    }

    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class CallbackLogger<T>(Action<LogLevel, string>? callback = null) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            callback?.Invoke(logLevel, formatter(state, exception));
    }
}
