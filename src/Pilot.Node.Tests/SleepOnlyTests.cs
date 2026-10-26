using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A job that only sleeps or polls is refused (pilot #288); real work with a sleep among it is not.</summary>
public sealed class SleepOnlyTests
{
    [Theory]
    [InlineData("python -c 'import os,time\nwhile not os.path.exists(\"/tmp/done\"): time.sleep(5)'")]
    [InlineData("python3 -c \"import time, os; [time.sleep(1) for _ in range(3)]\"")]
    [InlineData("while [ ! -f /tmp/done ]; do sleep 5; done")]
    [InlineData("until [ -f /tmp/done ]; do sleep 5; done")]
    [InlineData("while true; do [ -f /tmp/done ] && break; sleep 5; done")]
    [InlineData("while ! test -f /tmp/done; do sleep 5; done")]
    [InlineData("while (-not (Test-Path 'C:\\x\\done')) { Start-Sleep -Seconds 5 }")]
    [InlineData("while (-not (Test-Path C:\\x\\done)) { Start-Sleep 5 }")]
    [InlineData("Start-Sleep -Seconds 600")]
    [InlineData("sleep 600")]
    [InlineData("timeout /t 600 /nobreak")]
    [InlineData("bash -c 'while [ ! -f /tmp/done ]; do sleep 5; done'")]
    [InlineData("pwsh -NoProfile -Command \"while (-not (Test-Path x)) { Start-Sleep 5 }\"")]
    [InlineData("cmd /c timeout /t 30")]
    public void ASleepOrPollLoopIsRefusedWithAHintNamingLegWait(string line)
    {
        var refusal = SleepOnly.Refusal(line);

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("pilot leg wait --for-file");
    }

    [Theory]
    [InlineData("dotnet test src/Pilot.Node.Tests; sleep 5")]
    [InlineData("sleep 5; dotnet build -c Release")]
    [InlineData("while [ ! -f /tmp/done ]; do sleep 5; dotnet test; done")]
    [InlineData("while (-not (Test-Path x)) { Start-Sleep 5; Invoke-Thing }")]
    [InlineData("python -c 'import time; time.sleep(1); print(open(\"f\").read())'")]
    [InlineData("python -c 'print(1)'")]
    [InlineData("python -c 'import subprocess, time\nwhile subprocess.run([\"x\"]).returncode: time.sleep(5)'")]
    [InlineData("while [ \"$(curl -s http://host/ready)\" != ok ]; do sleep 5; done")]
    [InlineData("echo hello")]
    [InlineData("dotnet test")]
    [InlineData("")]
    public void RealWorkWithASleepAmongItIsNotRefused(string line) => SleepOnly.Refusal(line).ShouldBeNull();
}
