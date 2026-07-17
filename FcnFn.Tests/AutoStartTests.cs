using FcnFn;
using Xunit;

public class AutoStartTests
{
    [Fact]
    public void CreateArgString_registers_onlogon_limited_and_force()
    {
        string exe = @"C:\Tools\FcnFn.exe";
        string s = AutoStart.CreateArgString(exe);

        Assert.Contains("/Create", s);
        Assert.Contains($"/TN {AutoStart.TaskName}", s);
        Assert.Contains("/SC ONLOGON", s);
        Assert.Contains("/RL LIMITED", s);   // no elevation of the task itself
        Assert.Contains("/F", s);

        // /TR value must carry literal inner quotes around the exe path so a spaced
        // path survives Task Scheduler's program/args split.
        Assert.Contains($"/TR \"\\\"{exe}\\\"\"", s);
    }

    [Fact]
    public void CreateArgString_inner_quotes_survive_spaced_path()
    {
        string exe = @"C:\Program Files\FcnFn.exe";
        string s = AutoStart.CreateArgString(exe);
        Assert.Contains($"\"\\\"{exe}\\\"\"", s);
    }

    [Fact]
    public void DeleteArgString_targets_the_task_with_force()
    {
        string s = AutoStart.DeleteArgString();
        Assert.Contains("/Delete", s);
        Assert.Contains($"/TN {AutoStart.TaskName}", s);
        Assert.Contains("/F", s);
    }

    [Fact]
    public void QueryArgs_targets_the_task()
    {
        var args = AutoStart.QueryArgs();
        Assert.Equal("/Query", args[0]);
        int tn = System.Array.IndexOf(args, "/TN");
        Assert.Equal("FcnFn", args[tn + 1]);
    }
}
