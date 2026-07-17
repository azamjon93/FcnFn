using FcnFn;
using Xunit;

public class RemapEngineTests
{
    private static InjectOp[] Ops(RemapEngine e) => e.Injects.ToArray();

    [Fact]
    public void Marker_events_pass_through_untouched()
    {
        var e = new RemapEngine();
        var r = e.Process(0xAD /*mute*/, isDown: true, isMarker: true);
        Assert.False(r.Swallow);
        Assert.Empty(Ops(e));
    }

    [Fact]
    public void Media_key_down_remaps_to_fkey_down_and_swallows()
    {
        var e = new RemapEngine();
        var r = e.Process(0xAD /*mute*/, isDown: true, isMarker: false);
        Assert.True(r.Swallow);
        Assert.Equal(new[] { new InjectOp(0x70 /*F1*/, false) }, Ops(e));
    }

    [Fact]
    public void Media_key_up_remaps_to_fkey_up()
    {
        var e = new RemapEngine();
        var r = e.Process(0xAF /*vol up*/, isDown: false, isMarker: false);
        Assert.True(r.Swallow);
        Assert.Equal(new[] { new InjectOp(0x72 /*F3*/, true) }, Ops(e));
    }

    [Fact]
    public void Disabled_engine_passes_media_through()
    {
        var e = new RemapEngine { Enabled = false };
        var r = e.Process(0xAD, isDown: true, isMarker: false);
        Assert.False(r.Swallow);
        Assert.Empty(Ops(e));
    }

    [Fact]
    public void ScrollLock_down_toggles_enabled_and_swallows()
    {
        var e = new RemapEngine();
        bool fired = false;
        e.EnabledChanged += () => fired = true;

        var r = e.Process(0x91 /*VK_SCROLL*/, isDown: true, isMarker: false);

        Assert.True(r.Swallow);
        Assert.False(e.Enabled);
        Assert.True(fired);
        Assert.Empty(Ops(e));
    }

    [Fact]
    public void ScrollLock_up_is_swallowed_without_toggle()
    {
        var e = new RemapEngine();
        e.Process(0x91, isDown: true, isMarker: false);  // now disabled
        var r = e.Process(0x91, isDown: false, isMarker: false);
        Assert.True(r.Swallow);
        Assert.False(e.Enabled); // unchanged by the up event
    }

    [Fact]
    public void Win_chord_first_fire_dirties_win_releases_modifiers_then_emits_fkey()
    {
        var e = new RemapEngine();
        // Physically: Win down, then the terminal F21 (0x84) down.
        e.Process(0x5B /*LWin*/, isDown: true, isMarker: false);
        var r = e.Process(0x84 /*F21*/, isDown: true, isMarker: false);

        Assert.True(r.Swallow);
        Assert.Equal(new[]
        {
            new InjectOp(0xFF, false),  // dirty Win press
            new InjectOp(0xFF, true),
            new InjectOp(0x5B, true),   // logical Win up
            new InjectOp(0x7B /*F12*/, false),
        }, Ops(e));
    }

    [Fact]
    public void Win_chord_release_emits_fkey_up()
    {
        var e = new RemapEngine();
        e.Process(0x5B, isDown: true, isMarker: false);
        e.Process(0x84, isDown: true, isMarker: false);   // fires F12 down
        var r = e.Process(0x84, isDown: false, isMarker: false);

        Assert.True(r.Swallow);
        Assert.Equal(new[] { new InjectOp(0x7B /*F12*/, true) }, Ops(e));
    }

    [Fact]
    public void Physical_win_up_after_chord_is_suppressed_once()
    {
        var e = new RemapEngine();
        e.Process(0x5B, isDown: true, isMarker: false);
        e.Process(0x84, isDown: true, isMarker: false);   // ReleaseMod(0x5B) marks LWin up for suppression

        var up = e.Process(0x5B, isDown: false, isMarker: false);
        Assert.True(up.Swallow);   // first physical LWin up swallowed

        // A later, unrelated LWin up passes through normally.
        e.Process(0x5B, isDown: true, isMarker: false);
        var up2 = e.Process(0x5B, isDown: false, isMarker: false);
        Assert.False(up2.Swallow);
    }

    [Fact]
    public void Shift_win_chord_emits_f7()
    {
        var e = new RemapEngine();
        e.Process(0x5B, isDown: true, isMarker: false);
        e.Process(0xA0 /*LShift*/, isDown: true, isMarker: false);
        var r = e.Process(0x84 /*F21*/, isDown: true, isMarker: false);

        Assert.True(r.Swallow);
        // 0xFF x2, release Win (0x5B), release Shift (0xA0), then F7 (0x76).
        Assert.Equal(new[]
        {
            new InjectOp(0xFF, false),
            new InjectOp(0xFF, true),
            new InjectOp(0x5B, true),
            new InjectOp(0xA0, true),
            new InjectOp(0x76 /*F7*/, false),
        }, Ops(e));
    }

    [Fact]
    public void Unmapped_key_passes_through()
    {
        var e = new RemapEngine();
        var r = e.Process(0x41 /*A*/, isDown: true, isMarker: false);
        Assert.False(r.Swallow);
        Assert.Empty(Ops(e));
    }

    [Fact]
    public void Ctrl_win_chord_emits_f8_releasing_win_then_ctrl()
    {
        var e = new RemapEngine();
        e.Process(0x5B /*LWin*/, isDown: true, isMarker: false);
        e.Process(0xA2 /*LCtrl*/, isDown: true, isMarker: false);
        var r = e.Process(0x84 /*F21*/, isDown: true, isMarker: false);

        Assert.True(r.Swallow);
        // 0xFF x2, release Win (0x5B), release Ctrl (0xA2), then F8 (0x77).
        Assert.Equal(new[]
        {
            new InjectOp(0xFF, false),
            new InjectOp(0xFF, true),
            new InjectOp(0x5B, true),
            new InjectOp(0xA2, true),
            new InjectOp(0x77 /*F8*/, false),
        }, Ops(e));
    }

    [Fact]
    public void Win_tab_chord_emits_f10()
    {
        var e = new RemapEngine();
        e.Process(0x5B /*LWin*/, isDown: true, isMarker: false);
        var r = e.Process(0x09 /*Tab*/, isDown: true, isMarker: false);

        Assert.True(r.Swallow);
        Assert.Equal(new[]
        {
            new InjectOp(0xFF, false),
            new InjectOp(0xFF, true),
            new InjectOp(0x5B, true),
            new InjectOp(0x79 /*F10*/, false),
        }, Ops(e));
    }

    [Fact]
    public void Right_win_up_after_chord_is_suppressed()
    {
        var e = new RemapEngine();
        e.Process(0x5C /*RWin*/, isDown: true, isMarker: false);
        e.Process(0x84 /*F21*/, isDown: true, isMarker: false); // fires; ReleaseMod adds 0x5B and 0x5C

        var up = e.Process(0x5C /*RWin*/, isDown: false, isMarker: false);
        Assert.True(up.Swallow); // right-variant up suppressed via the +1 arithmetic
    }

    [Fact]
    public void Right_ctrl_up_after_chord_is_suppressed()
    {
        var e = new RemapEngine();
        e.Process(0x5B /*LWin*/, isDown: true, isMarker: false);
        e.Process(0xA3 /*RCtrl*/, isDown: true, isMarker: false);
        e.Process(0x84 /*F21*/, isDown: true, isMarker: false); // Ctrl+Win chord; ReleaseMod(0xA2) adds 0xA2 and 0xA3

        var up = e.Process(0xA3 /*RCtrl*/, isDown: false, isMarker: false);
        Assert.True(up.Swallow); // right Ctrl up suppressed
    }

    [Fact]
    public void Opposite_variant_modifier_up_is_not_suppressed_after_chord()
    {
        var e = new RemapEngine();
        e.Process(0x5B /*LWin*/, isDown: true, isMarker: false);
        e.Process(0x84 /*F21*/, isDown: true, isMarker: false); // chord fires; suppresses only 0x5B
        e.Process(0x5B, isDown: false, isMarker: false);          // physical LWin up consumed

        // A later, unrelated RIGHT Win press/release must pass through untouched.
        e.Process(0x5C /*RWin*/, isDown: true, isMarker: false);
        var up = e.Process(0x5C, isDown: false, isMarker: false);
        Assert.False(up.Swallow);
    }
}
