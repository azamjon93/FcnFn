using FcnFn;
using Xunit;

public class AutoStartTests
{
    [Fact]
    public void CreateArgs_registers_onlogon_limited_and_force()
    {
        var args = AutoStart.CreateArgs(@"C:\Tools\FcnFn.exe");

        Assert.Equal("/Create", args[0]);
        Assert.Contains("/SC", args);
        Assert.Contains("ONLOGON", args);
        Assert.Contains("/RL", args);
        Assert.Contains("LIMITED", args);   // no elevation
        Assert.Contains("/F", args);        // overwrite without prompt

        int tn = Array.IndexOf(args, "/TN");
        Assert.Equal("FcnFn", args[tn + 1]);

        int tr = Array.IndexOf(args, "/TR");
        Assert.Equal(@"C:\Tools\FcnFn.exe", args[tr + 1]);
    }

    [Fact]
    public void DeleteArgs_targets_the_task_with_force()
    {
        var args = AutoStart.DeleteArgs();
        Assert.Equal("/Delete", args[0]);
        int tn = Array.IndexOf(args, "/TN");
        Assert.Equal("FcnFn", args[tn + 1]);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void QueryArgs_targets_the_task()
    {
        var args = AutoStart.QueryArgs();
        Assert.Equal("/Query", args[0]);
        int tn = Array.IndexOf(args, "/TN");
        Assert.Equal("FcnFn", args[tn + 1]);
    }
}
