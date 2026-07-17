namespace FcnFn;

internal readonly record struct InjectOp(ushort Vk, bool Up);

internal readonly record struct HookOutcome(bool Swallow);

/// <summary>
/// Pure remap decision logic. No Win32 calls: Process() records the key events
/// that should be injected (see <see cref="Injects"/>) and whether the incoming
/// event should be swallowed. This makes the tricky chord / Start-menu
/// suppression state machine unit-testable.
/// </summary>
internal sealed class RemapEngine
{
    // ---- Media-key map (OS-injected VKs from HID consumer usages) ----
    private static readonly Dictionary<uint, ushort> MediaMap = new()
    {
        { 0xAD /* mute       */, 0x70 /* F1 */ },
        { 0xAE /* vol down   */, 0x71 /* F2 */ },
        { 0xAF /* vol up     */, 0x72 /* F3 */ },
        { 0xB1 /* prev track */, 0x73 /* F4 */ },
        { 0xB3 /* play/pause */, 0x74 /* F5 */ },
        { 0xB0 /* next track */, 0x75 /* F6 */ },
    };

    private readonly record struct Chord(uint Vk, bool Win, bool Shift, bool Ctrl);
    private static readonly Dictionary<Chord, ushort> ChordMap = new()
    {
        { new Chord(0x84 /*F21*/, Win: true, Shift: true,  Ctrl: false), 0x76 /* F7  - verify */ },
        { new Chord(0x84 /*F21*/, Win: true, Shift: false, Ctrl: true ), 0x77 /* F8  - verify */ },
        { new Chord(0x09 /*Tab*/, Win: true, Shift: false, Ctrl: false), 0x79 /* F10 */ },
        { new Chord(0x84 /*F21*/, Win: true, Shift: false, Ctrl: false), 0x7B /* F12 */ },
    };

    private const uint VK_SCROLL = 0x91;

    // Injected-op scratch buffer, reused every Process() call (no per-key alloc).
    // Worst case is a first-fire triple-modifier chord: 0xFF down/up (2) +
    // release Win/Shift/Ctrl (3) + F-key down (1) = 6.
    private readonly InjectOp[] _buffer = new InjectOp[8];
    private int _count;

    private bool _win, _shift, _ctrl, _alt;
    private readonly HashSet<uint> _suppressedModUps = new();
    private readonly Dictionary<uint, ushort> _activeChords = new();

    public bool Enabled { get; set; } = true;
    public event Action? EnabledChanged;

    public ReadOnlySpan<InjectOp> Injects => _buffer.AsSpan(0, _count);

    private HookOutcome Emit(ushort vk, bool up)
    {
        _buffer[_count++] = new InjectOp(vk, up);
        return default; // convenience; callers set Swallow explicitly
    }

    private void ReleaseMod(ushort vk)
    {
        _buffer[_count++] = new InjectOp(vk, true);
        _suppressedModUps.Add(vk);            // left variant
        _suppressedModUps.Add((uint)vk + 1);  // right variant (0x5C, 0xA1, 0xA3)
    }

    public HookOutcome Process(uint vk, bool isDown, bool isMarker)
    {
        _count = 0;

        // Skip ONLY our own injections. OS-injected media keys must be processed.
        if (isMarker)
            return new HookOutcome(false);

        // ---- modifier bookkeeping (physical state) ----
        switch (vk)
        {
            case 0x5B:
            case 0x5C: // L/R Win
                _win = isDown;
                if (!isDown && _suppressedModUps.Remove(vk)) return new HookOutcome(true);
                return new HookOutcome(false);
            case 0xA0:
            case 0xA1: // L/R Shift
                _shift = isDown;
                if (!isDown && _suppressedModUps.Remove(vk)) return new HookOutcome(true);
                return new HookOutcome(false);
            case 0xA2:
            case 0xA3: // L/R Ctrl
                _ctrl = isDown;
                if (!isDown && _suppressedModUps.Remove(vk)) return new HookOutcome(true);
                return new HookOutcome(false);
            case 0xA4:
            case 0xA5: // L/R Alt
                _alt = isDown;
                return new HookOutcome(false);
        }

        // ---- toggle: Scroll Lock (swallow both down and up) ----
        if (vk == VK_SCROLL)
        {
            if (isDown)
            {
                Enabled = !Enabled;
                EnabledChanged?.Invoke();
            }
            return new HookOutcome(true);
        }

        if (!Enabled)
            return new HookOutcome(false);

        // ---- media keys (OS-injected consumer usages) ----
        if (MediaMap.TryGetValue(vk, out ushort mediaFk))
        {
            Emit(mediaFk, up: !isDown);
            return new HookOutcome(true);
        }

        // ---- chord terminal keys ----
        if (isDown)
        {
            if (ChordMap.TryGetValue(new Chord(vk, _win, _shift, _ctrl), out ushort fk))
            {
                bool firstFire = !_activeChords.ContainsKey(vk);
                _activeChords[vk] = fk;

                if (firstFire)
                {
                    Emit(0xFF, up: false); // dirty the Win press
                    Emit(0xFF, up: true);
                    if (_win) ReleaseMod(0x5B);
                    if (_shift) ReleaseMod(0xA0);
                    if (_ctrl) ReleaseMod(0xA2);
                }
                Emit(fk, up: false);
                return new HookOutcome(true);
            }
        }
        else if (_activeChords.Remove(vk, out ushort heldFk))
        {
            Emit(heldFk, up: true);
            return new HookOutcome(true);
        }

        return new HookOutcome(false);
    }
}
