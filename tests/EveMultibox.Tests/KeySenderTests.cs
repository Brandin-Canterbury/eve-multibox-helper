using System.Windows.Input;
using EveMultibox.Toolbox;

namespace EveMultibox.Tests;

public class KeySenderTests
{
    private sealed class FakeBackend : IInputBackend
    {
        public IntPtr Foreground { get; set; }
        public HashSet<IntPtr> Windows { get; } = new();
        public List<KeyChord> Sent { get; } = new();
        public bool Fail { get; set; }

        public IntPtr GetForegroundWindow() => Foreground;
        public bool IsWindow(IntPtr hwnd) => Windows.Contains(hwnd);

        public bool SendChord(KeyChord chord)
        {
            if (Fail) return false;
            Sent.Add(chord);
            return true;
        }
    }

    private static readonly IntPtr Miner1 = new(100);
    private static readonly IntPtr Miner2 = new(200);
    private static readonly KeyChord F1 = new(Key.F1);

    private readonly FakeBackend _backend = new();
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly KeySender _sender;

    public KeySenderTests()
    {
        _backend.Windows.Add(Miner1);
        _backend.Windows.Add(Miner2);
        _sender = new KeySender(_backend, () => _now);
    }

    [Fact]
    public void SendsOneChordToForegroundTarget()
    {
        _backend.Foreground = Miner1;
        Assert.Equal(SendResult.Sent, _sender.TrySend(Miner1, "fav", F1));
        Assert.Equal(new[] { F1 }, _backend.Sent);
    }

    [Fact]
    public void RefusesWhenTargetIsNotForeground()
    {
        _backend.Foreground = Miner2;
        Assert.Equal(SendResult.TargetNotForeground, _sender.TrySend(Miner1, "fav", F1));
        Assert.Empty(_backend.Sent);
    }

    [Fact]
    public void RefusesWhenSomethingElseEntirelyHasFocus()
    {
        _backend.Foreground = new IntPtr(999);
        Assert.Equal(SendResult.TargetNotForeground, _sender.TrySend(Miner1, "fav", F1));
        Assert.Empty(_backend.Sent);
    }

    [Fact]
    public void RefusesWithoutTarget()
    {
        _backend.Foreground = Miner1;
        Assert.Equal(SendResult.NoTarget, _sender.TrySend(IntPtr.Zero, "fav", F1));
        Assert.Empty(_backend.Sent);
    }

    [Fact]
    public void RefusesClosedWindow()
    {
        var gone = new IntPtr(300);
        _backend.Foreground = gone;
        Assert.Equal(SendResult.TargetGone, _sender.TrySend(gone, "fav", F1));
        Assert.Empty(_backend.Sent);
    }

    [Fact]
    public void SwallowsDoubleClickWithinCooldown()
    {
        _backend.Foreground = Miner1;
        Assert.Equal(SendResult.Sent, _sender.TrySend(Miner1, "fav", F1));
        _now += TimeSpan.FromMilliseconds(120);
        Assert.Equal(SendResult.CoolingDown, _sender.TrySend(Miner1, "fav", F1));
        _now += KeySender.DefaultCooldown;
        Assert.Equal(SendResult.Sent, _sender.TrySend(Miner1, "fav", F1));
        Assert.Equal(2, _backend.Sent.Count);
    }

    [Fact]
    public void CooldownIsPerButton()
    {
        _backend.Foreground = Miner1;
        Assert.Equal(SendResult.Sent, _sender.TrySend(Miner1, "a", F1));
        Assert.Equal(SendResult.Sent, _sender.TrySend(Miner1, "b", new KeyChord(Key.F2)));
    }

    [Fact]
    public void FailedSendDoesNotStartCooldown()
    {
        _backend.Foreground = Miner1;
        _backend.Fail = true;
        Assert.Equal(SendResult.SendFailed, _sender.TrySend(Miner1, "fav", F1));
        _backend.Fail = false;
        Assert.Equal(SendResult.Sent, _sender.TrySend(Miner1, "fav", F1));
    }
}
