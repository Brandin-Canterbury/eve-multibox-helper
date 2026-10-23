using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EveMultibox.Clients;
using EveMultibox.Config;
using EveMultibox.Esi;
using EveMultibox.Interop;
using EveMultibox.StaticData;
using EveMultibox.Toolbox;
using EveMultibox.Trade;

namespace EveMultibox.Widget;

/// <summary>
/// The on-screen action widget: walks through a plan one step at a time. Each button does exactly
/// one thing (set a destination, press one EVE shortcut in the EVE window you're already in, or
/// copy a value); steps that ESI can observe, like arriving, advance on their own.
/// It never takes focus, so the EVE window you clicked stays in front.
/// </summary>
public partial class ActionWidgetWindow : Window
{
    private readonly App _app;
    private readonly ActionPlan _plan;
    private readonly TradeService _trade;
    private readonly ClientTracker _tracker;
    private readonly KeySender _sender = new(new Win32InputBackend());
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly JumpGraph? _graph;
    private int _index;
    private int _ticks;
    private bool _finished;

    public ActionWidgetWindow(App app, ActionPlan plan, TradeService trade, ClientTracker tracker)
    {
        InitializeComponent();
        Shell.WindowLook.Apply(this);
        _app = app;
        _plan = plan;
        _trade = trade;
        _tracker = tracker;
        _graph = trade.Sde.Data is { } sde ? new JumpGraph(sde.Systems) : null;

        if (app.Config.Widget.Position is WindowSpot spot)
        {
            Left = spot.X;
            Top = spot.Y;
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 80;
            Top = work.Top + 120;
        }

        SourceInitialized += (_, _) =>
            Win32.AddExStyle(new WindowInteropHelper(this).Handle, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        _timer.Tick += async (_, _) => await TickAsync();
        Loaded += (_, _) =>
        {
            ShowStep();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private ActionStep Step => _plan.Steps[_index];

    private CharacterState? Character => _trade.Characters.FirstOrDefault(c => c.Id == _plan.CharacterId);

    private void ShowStep()
    {
        PlanText.Text = $"{_plan.Title} · {_plan.CharacterName} · step {_index + 1} of {_plan.Steps.Count}";
        StepTitle.Text = Step.Title;
        InstructionText.Text = Step.Instruction;
        DoneButton.Content = Step.DoneLabel + "  ▶";
        BackButton.IsEnabled = _index > 0;
        StatusText.Text = "";
        BuildButtons();
        UpdateInfo();
    }

    private void BuildButtons()
    {
        ActionButtons.Children.Clear();

        if (Step.DestinationId is long dest)
        {
            var b = BigButton($"📍  Set destination: {Step.DestinationLabel}");
            b.Click += async (_, _) => await SetDestinationAsync(dest);
            ActionButtons.Children.Add(b);
        }

        foreach (var key in Step.Keys)
        {
            string? keyText = key == GameKey.JumpOrDock ? _app.Config.Widget.JumpKey : _app.Config.Widget.UndockKey;
            string label = key == GameKey.JumpOrDock ? "Jump / Dock" : "Undock";
            bool valid = KeyChord.TryParse(keyText, out var chord, out _);
            var b = BigButton(valid ? $"⏵  {label}  ({chord})" : $"⏵  {label}  (set its key in ☰ → Settings)");
            b.IsEnabled = valid;
            if (valid) b.Click += (_, _) => PressKey(key, chord!);
            ActionButtons.Children.Add(b);
        }

        foreach (var copy in Step.Copies)
        {
            var b = BigButton($"⧉  {copy.Label}: {copy.Value}");
            b.Click += (_, _) =>
            {
                Clipboard.SetText(copy.Value);
                StatusText.Text = $"Copied {copy.Value}. Paste it in EVE with Ctrl+V.";
            };
            ActionButtons.Children.Add(b);
        }
    }

    private Button BigButton(string text) => new()
    {
        Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
        Style = (Style)Resources["Big"],
    };

    private void UpdateInfo()
    {
        var c = Character;
        if (Step.TargetSystemId is int target && c?.SystemId is int here && _graph is not null)
        {
            int? left = _graph.Jumps(here, target);
            InfoText.Text = left switch
            {
                null => "",
                0 when c.StationId == Step.DestinationId => "Docked here.",
                0 => "In the destination system: select the station and press Jump / Dock.",
                1 => "1 jump left",
                int n => $"{n} jumps left",
            };
        }
        else
        {
            InfoText.Text = "";
        }
    }

    private async Task TickAsync()
    {
        if (_finished || Character is not { } c) return;

        // While waiting for something ESI can see (arriving, undocking), check the location often.
        if (Step.CompleteWhen is not null && ++_ticks % 3 == 0) await _trade.RefreshCharacterAsync(c);

        UpdateInfo();
        if (Step.CompleteWhen?.Invoke(new PlanContext(c.SystemId, c.StationId, c.StructureId)) == true) Advance();
    }

    /// <summary>One keypress into the plan character's EVE window, only if it's already the active window.</summary>
    private void PressKey(GameKey key, KeyChord chord)
    {
        var client = _tracker.FindByCharacter(_plan.CharacterName);
        var result = _sender.TrySend(client?.Hwnd ?? IntPtr.Zero, "widget:" + key, chord);
        StatusText.Text = result switch
        {
            SendResult.Sent => $"Pressed {chord} in {_plan.CharacterName}'s window.",
            SendResult.CoolingDown => StatusText.Text,
            SendResult.TargetNotForeground or SendResult.NoTarget or SendResult.TargetGone =>
                $"Nothing sent. Click into {_plan.CharacterName}'s EVE window first, then press the button.",
            _ => "Windows blocked the keypress (is EVE running as administrator?).",
        };
    }

    private async Task SetDestinationAsync(long destinationId)
    {
        if (Character is not { } c)
        {
            StatusText.Text = $"{_plan.CharacterName} isn't signed in any more.";
            return;
        }
        try
        {
            await _trade.SetDestinationAsync(c, destinationId, addAsWaypoint: false);
            StatusText.Text = "Destination set. The next gate is highlighted yellow in your overview.";
        }
        catch (Exception ex) when (ex is EsiException or SignInRequiredException or System.Net.Http.HttpRequestException)
        {
            StatusText.Text = "Couldn't set the destination: " + ex.Message;
        }
    }

    private void Advance()
    {
        if (_index < _plan.Steps.Count - 1)
        {
            _index++;
            _ticks = 0;
            ShowStep();
            return;
        }
        _finished = true;
        _timer.Stop();
        StepTitle.Text = "All done ✓";
        InstructionText.Text = "Plan complete. Close this widget or start another plan from Trade.";
        InfoText.Text = "";
        ActionButtons.Children.Clear();
        DoneButton.Content = "Close";
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_finished) Close();
        else Advance();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_index == 0) return;
        _index--;
        _finished = false;
        _timer.Start();
        ShowStep();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Win32.IsAltDown()) return;
        DragMove();
        _app.Config.Widget.Position = new WindowSpot { X = Left, Y = Top };
        _app.SaveQuietly();
    }
}
