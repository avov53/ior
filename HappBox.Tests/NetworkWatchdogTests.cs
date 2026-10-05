using System.Diagnostics;
using System.Text.Json;
using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class NetworkWatchdogTests
{
    [Fact]
    public async Task ParentCrashKillsOnlyOwnedChildAndClearsMatchingEmptyJournal()
    {
        var binary = Environment.GetEnvironmentVariable("IOR_NATIVE_TEST_BINARY");
        if (string.IsNullOrWhiteSpace(binary)) return;
        var directory = Path.Combine(Path.GetTempPath(), "ior-watchdog-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var runId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(XrayTunRouteLease.JournalPath(directory), JsonSerializer.Serialize(new
        { InterfaceName = "reference-interface", Address = "192.0.2.2", Gateway = "192.0.2.1", Routes = Array.Empty<object>(), RunId = runId }));
        Process Dummy()
        {
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("control-stdin"); start.ArgumentList.Add("2080");
            return Process.Start(start)!; // waits on private stdin before opening any sockets
        }
        using var parent = Dummy(); using var child = Dummy(); using var unrelated = Dummy();
        var watchdogStart = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        watchdogStart.ArgumentList.Add(typeof(AppState).Assembly.Location);
        watchdogStart.ArgumentList.Add("--network-watchdog");
        using var watchdog = Process.Start(watchdogStart)!;
        try
        {
            await watchdog.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Kind = "start", ParentPid = parent.Id,
                ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks, AppDirectory = directory, RunId = runId }));
            await watchdog.StandardInput.FlushAsync();
            Assert.Equal("READY", await watchdog.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await watchdog.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Kind = "child", ChildPid = child.Id,
                ChildStartTicks = child.StartTime.ToUniversalTime().Ticks }));
            await watchdog.StandardInput.FlushAsync();
            Assert.Equal("OWNED", await watchdog.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            parent.Kill(); await parent.WaitForExitAsync();
            await watchdog.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(0, watchdog.ExitCode);
            Assert.True(child.HasExited);
            Assert.False(unrelated.HasExited);
            Assert.False(File.Exists(XrayTunRouteLease.JournalPath(directory)));
        }
        finally
        {
            foreach (var process in new[] { parent, child, unrelated, watchdog })
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }
}
