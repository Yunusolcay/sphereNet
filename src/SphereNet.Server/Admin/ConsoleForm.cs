using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace SphereNet.Server.Admin;

/// <summary>
/// WinForms console window with RichTextBox output and TextBox input.
/// Auto-scroll controlled by a checkbox: checked = follow new output, unchecked = free scroll.
/// </summary>
public sealed class ConsoleForm : Form, ILogEventSink
{
    private readonly RichTextBox _output;
    private readonly TextBox _input;
    private readonly CheckBox _followCheck;
    private readonly Label _titleLabel;
    private readonly Label _statusLabel;
    private readonly Label _uptimeLabel;
    private readonly Label _activePlayersLabel;
    private readonly Label _totalAccountsLabel;
    private readonly Label _totalCharsLabel;
    private readonly Label _totalItemsLabel;
    private readonly Label _totalScriptsLabel;
    private readonly Label _cpuBar;
    private readonly Label _ramBar;
    private readonly Button _debugButton;
    private readonly System.Windows.Forms.Timer _statsTimer;
    private readonly MessageTemplateTextFormatter _formatter;
    private readonly ConcurrentQueue<string> _commandQueue = new();
    private readonly ConcurrentQueue<(string Text, Color Color)> _pendingLines = new();
    private readonly System.Windows.Forms.Timer _flushTimer;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private int _activePlayers;
    private int _lineCount;
    private bool _debugActive;
    private Func<int>? _activePlayersProvider;
    private Func<int>? _totalAccountsProvider;
    private Func<int>? _totalCharsProvider;
    private Func<int>? _totalItemsProvider;
    private Func<int>? _totalScriptsProvider;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleUtc;
    private DateTime _serverStartUtc = DateTime.UtcNow;

    private const int MaxLines = 5000;
    private const int MaxHistory = 100;
    private const int FlushIntervalMs = 16;   // ~60fps — near real-time for debugging
    private const int MaxLinesPerFlush = 200;
    private const string EmptyBar = "..........";
    private const string FillBar = "██████████";

    // Color palette
    private static readonly Color BgDark = Color.FromArgb(13, 17, 26);
    private static readonly Color BgPanel = Color.FromArgb(19, 24, 38);
    private static readonly Color BgCard = Color.FromArgb(24, 31, 46);
    private static readonly Color BgInput = Color.FromArgb(18, 26, 40);
    private static readonly Color BorderSubtle = Color.FromArgb(42, 58, 86);
    private static readonly Color BorderAccent = Color.FromArgb(55, 85, 130);
    private static readonly Color TextPrimary = Color.FromArgb(230, 238, 255);
    private static readonly Color TextSecondary = Color.FromArgb(160, 175, 200);
    private static readonly Color AccentBlue = Color.FromArgb(70, 140, 220);
    private static readonly Color AccentGreen = Color.FromArgb(72, 200, 130);
    private static readonly Color AccentAmber = Color.FromArgb(230, 180, 80);
    private static readonly Color AccentRed = Color.FromArgb(220, 80, 80);

    public ConcurrentQueue<string> CommandQueue => _commandQueue;

    public event Action? ShutdownRequested;

    public ConsoleForm()
    {
        _formatter = new MessageTemplateTextFormatter(
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        Text = "sphereNet";
        Size = new Size(1200, 760);
        MinimumSize = new Size(900, 560);
        BackColor = BgDark;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        _output = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(12, 16, 26),
            ForeColor = Color.FromArgb(209, 219, 235),
            Font = new Font("Consolas", 11f),
            BorderStyle = BorderStyle.None,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };

        // ── Header ──
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(20, 0, 20, 0),
            BackColor = BgPanel,
        };
        headerPanel.Paint += PaintBottomBorder;

        _titleLabel = new Label
        {
            Dock = DockStyle.Left,
            AutoSize = false,
            Width = 600,
            Text = "sphereNet",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI Semibold", 14f),
        };
        _statusLabel = new Label
        {
            Dock = DockStyle.Right,
            AutoSize = false,
            Width = 300,
            Text = "RUNNING",
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = AccentGreen,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        };
        headerPanel.Controls.Add(_statusLabel);
        headerPanel.Controls.Add(_titleLabel);

        // ── Body ──
        var bodyPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = BgDark,
        };

        // ── Side Panel ──
        var sidePanel = new Panel
        {
            Dock = DockStyle.Right,
            Width = 280,
            Padding = new Padding(12, 0, 0, 0),
            BackColor = Color.Transparent,
        };

        // ── Output Frame ──
        var outputFrame = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = BgPanel,
        };
        outputFrame.Controls.Add(_output);
        outputFrame.Paint += (_, e) =>
        {
            using var p = new Pen(BorderSubtle, 1f);
            var r = outputFrame.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(p, r);
        };

        // ── Stats Card ──
        // Visual order (top → bottom): CPU, RAM, world (Items/Chars/Scripts),
        // accounts (Accounts/Players), Uptime. With DockStyle.Top the last-added
        // control sits topmost, so the Add() sequence below walks the desired
        // visual layout in reverse.
        // Plain card, no title strip: 8 rows (2×40 metric + 6×38 stat) +
        // 3 separators(3) + padding(20) ≈ 345px.
        var statsCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 345,
            BackColor = BgCard,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 0, 10),
        };
        statsCard.Paint += (_, e) =>
        {
            using var p = new Pen(BorderSubtle, 1f);
            var r = statsCard.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(p, r);
        };
        _cpuBar = CreateBarLabel();
        _ramBar = CreateBarLabel();
        _uptimeLabel = CreateStatLabel("Uptime", "0s");
        _activePlayersLabel = CreateStatLabel("Players Online", "0");
        _totalAccountsLabel = CreateStatLabel("Accounts", "0");
        _totalCharsLabel = CreateStatLabel("Characters", "0");
        _totalItemsLabel = CreateStatLabel("Items", "0");
        _totalScriptsLabel = CreateStatLabel("Scripts", "0");

        statsCard.Controls.Add(_uptimeLabel);
        statsCard.Controls.Add(CreateSeparator());
        statsCard.Controls.Add(_totalAccountsLabel);
        statsCard.Controls.Add(_activePlayersLabel);
        statsCard.Controls.Add(CreateSeparator());
        statsCard.Controls.Add(_totalScriptsLabel);
        statsCard.Controls.Add(_totalCharsLabel);
        statsCard.Controls.Add(_totalItemsLabel);
        statsCard.Controls.Add(CreateSeparator());
        statsCard.Controls.Add(CreateMetricRow("RAM", _ramBar, AccentAmber));
        statsCard.Controls.Add(CreateMetricRow("CPU", _cpuBar, AccentBlue));

        // ── Quick Actions (no title) ──
        // Plain card: title strip dropped per user request. Height sized for
        // the 3 buttons + their top margins + card padding; the SAVE button
        // was previously clipped at the bottom because the old 180px panel
        // didn't fit title(26)+sep(1)+spacer(8)+3×(36+6)+padding(20)=181px.
        var quickActionsPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 156,
            BackColor = BgCard,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 0, 10),
        };
        quickActionsPanel.Paint += (_, e) =>
        {
            using var p = new Pen(BorderSubtle, 1f);
            var r = quickActionsPanel.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(p, r);
        };

        _debugButton = CreateActionButton(
            "DEBUG: OFF", "debug",
            backColor: Color.FromArgb(55, 45, 30), borderColor: Color.FromArgb(140, 110, 50));
        var resyncButton = CreateActionButton(
            "RESYNC", "resync",
            backColor: Color.FromArgb(35, 55, 90), borderColor: Color.FromArgb(70, 120, 190));
        var saveButton = CreateActionButton(
            "SAVE", "save",
            backColor: Color.FromArgb(30, 65, 45), borderColor: Color.FromArgb(60, 140, 90));

        quickActionsPanel.Controls.Add(_debugButton);
        quickActionsPanel.Controls.Add(resyncButton);
        quickActionsPanel.Controls.Add(saveButton);

        sidePanel.Controls.Add(statsCard);
        sidePanel.Controls.Add(quickActionsPanel);

        bodyPanel.Controls.Add(outputFrame);
        bodyPanel.Controls.Add(sidePanel);

        // ── Bottom Input Bar ──
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            Padding = new Padding(16, 8, 16, 10),
            BackColor = BgPanel,
        };
        bottomPanel.Paint += PaintTopBorder;

        var inputContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgInput,
            Padding = new Padding(12, 8, 12, 8),
        };
        inputContainer.Paint += (_, e) =>
        {
            using var p = new Pen(BorderSubtle, 1f);
            var r = inputContainer.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(p, r);
        };

        _followCheck = new CheckBox
        {
            Text = "AUTO",
            Checked = true,
            Dock = DockStyle.Right,
            Width = 60,
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            BackColor = AccentBlue,
            TextAlign = ContentAlignment.MiddleCenter,
            TabStop = false,
            Margin = new Padding(8, 0, 0, 0),
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
        };
        _followCheck.FlatAppearance.BorderSize = 1;
        _followCheck.FlatAppearance.BorderColor = AccentBlue;
        _followCheck.FlatAppearance.CheckedBackColor = AccentBlue;
        _followCheck.FlatAppearance.MouseOverBackColor = Color.FromArgb(85, 155, 230);
        _followCheck.FlatAppearance.MouseDownBackColor = Color.FromArgb(55, 120, 200);

        _input = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = BgInput,
            ForeColor = TextPrimary,
            Font = new Font("Consolas", 11f),
            BorderStyle = BorderStyle.None,
        };
        _input.KeyDown += Input_KeyDown;
        inputContainer.Controls.Add(_input);

        _followCheck.CheckedChanged += (_, _) =>
        {
            if (_followCheck.Checked)
            {
                _followCheck.BackColor = AccentBlue;
                _followCheck.FlatAppearance.BorderColor = AccentBlue;
                _output.SelectionStart = _output.TextLength;
                _output.ScrollToCaret();
            }
            else
            {
                _followCheck.BackColor = Color.FromArgb(40, 50, 70);
                _followCheck.FlatAppearance.BorderColor = BorderSubtle;
            }
            _input.Focus();
        };

        bottomPanel.Controls.Add(inputContainer);
        bottomPanel.Controls.Add(_followCheck);

        Controls.Add(bodyPanel);
        Controls.Add(headerPanel);
        Controls.Add(bottomPanel);
        UpdateStatsBars(20, 30, 0);

        _statsTimer = new System.Windows.Forms.Timer
        {
            Interval = 1_000 // every second
        };
        _statsTimer.Tick += (_, _) => RefreshRuntimeStats();

        _flushTimer = new System.Windows.Forms.Timer
        {
            Interval = FlushIntervalMs
        };
        _flushTimer.Tick += (_, _) => FlushPendingLines();
        _flushTimer.Start();
    }

    private static void PaintBottomBorder(object? sender, PaintEventArgs e)
    {
        if (sender is not Control c) return;
        using var p = new Pen(BorderSubtle, 1f);
        e.Graphics.DrawLine(p, 0, c.Height - 1, c.Width, c.Height - 1);
    }

    private static void PaintTopBorder(object? sender, PaintEventArgs e)
    {
        if (sender is not Control c) return;
        using var p = new Pen(BorderSubtle, 1f);
        e.Graphics.DrawLine(p, 0, 0, c.Width, 0);
    }

    private static Panel CreateSeparator()
    {
        var sep = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = BorderSubtle,
            Margin = new Padding(0, 2, 0, 2),
        };
        return sep;
    }

    private static Panel CreateCard(string title)
    {
        var card = new Panel
        {
            Dock = DockStyle.Top,
            Height = 190,
            BackColor = BgCard,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 0, 10),
        };
        card.Paint += (_, e) =>
        {
            using var p = new Pen(BorderSubtle, 1f);
            var r = card.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(p, r);
        };

        var titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = title.ToUpperInvariant(),
            ForeColor = TextSecondary,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 0, 4),
        };
        var titleSep = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = BorderAccent,
        };
        // Breathing room between header divider and content rows.
        var titleSpacer = new Panel
        {
            Dock = DockStyle.Top,
            Height = 8,
            BackColor = Color.Transparent,
        };
        card.Controls.Add(titleSpacer);
        card.Controls.Add(titleSep);
        card.Controls.Add(titleLabel);
        return card;
    }

    private static Label CreateBarLabel()
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Consolas", 9f, FontStyle.Regular),
            Text = EmptyBar,
        };
    }

    private static Panel CreateMetricRow(string label, Label bar, Color barColor)
    {
        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(0, 2, 0, 2)
        };
        var lbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 16,
            Text = label,
            ForeColor = TextSecondary,
            Font = new Font("Segoe UI", 8.5f),
        };
        bar.ForeColor = barColor;
        row.Controls.Add(bar);
        row.Controls.Add(lbl);
        return row;
    }

    private static Label CreateStatLabel(string title, string value)
    {
        return new Label
        {
            Dock = DockStyle.Top,
            Height = 38,
            Text = $"  {title}:  {value}",
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
        };
    }

    private static Label CreateErrorLabel(string text)
    {
        return new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = text,
            ForeColor = Color.FromArgb(214, 196, 196),
            Font = new Font("Segoe UI", 9f),
        };
    }

    public void SetServerName(string serverName)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetServerName(serverName));
            return;
        }

        string displayName = string.IsNullOrWhiteSpace(serverName) ? "Unknown" : serverName.Trim();
        Text = $"sphereNet - {displayName}";
        _titleLabel.Text = $"sphereNet  |  {displayName}";
    }

    /// <summary>Anchor the uptime counter to a specific UTC instant (typically
    /// when the server finished initializing, not when the process launched).</summary>
    public void SetServerStartTime(DateTime startUtc)
    {
        _serverStartUtc = startUtc.Kind == DateTimeKind.Utc ? startUtc : startUtc.ToUniversalTime();
    }

    public void SetStatsProviders(
        Func<int> activePlayersProvider,
        Func<int> totalAccountsProvider,
        Func<int> totalCharsProvider,
        Func<int> totalItemsProvider,
        Func<int> totalScriptsProvider)
    {
        _activePlayersProvider = activePlayersProvider;
        _totalAccountsProvider = totalAccountsProvider;
        _totalCharsProvider = totalCharsProvider;
        _totalItemsProvider = totalItemsProvider;
        _totalScriptsProvider = totalScriptsProvider;
    }

    public void SetDebugState(bool active)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetDebugState(active));
            return;
        }

        _debugActive = active;
        _debugButton.Text = active ? "DEBUG: ON" : "DEBUG: OFF";
        if (active)
        {
            _debugButton.BackColor = Color.FromArgb(90, 55, 25);
            _debugButton.FlatAppearance.BorderColor = Color.FromArgb(200, 150, 50);
            _debugButton.ForeColor = AccentAmber;
        }
        else
        {
            _debugButton.BackColor = Color.FromArgb(55, 45, 30);
            _debugButton.FlatAppearance.BorderColor = Color.FromArgb(140, 110, 50);
            _debugButton.ForeColor = TextPrimary;
        }
    }

    private Button CreateActionButton(
        string text,
        string command,
        bool confirm = false,
        Color? backColor = null,
        Color? borderColor = null)
    {
        Color buttonBack = backColor ?? Color.FromArgb(40, 48, 65);
        Color buttonBorder = borderColor ?? BorderSubtle;

        var btn = new Button
        {
            Dock = DockStyle.Top,
            Height = 36,
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = buttonBack,
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI Semibold", 10f),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 6),
        };
        btn.FlatAppearance.BorderColor = buttonBorder;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(
            Math.Min(buttonBack.R + 15, 255),
            Math.Min(buttonBack.G + 15, 255),
            Math.Min(buttonBack.B + 15, 255));
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(
            Math.Min(buttonBack.R + 8, 255),
            Math.Min(buttonBack.G + 8, 255),
            Math.Min(buttonBack.B + 8, 255));
        btn.Click += (_, _) =>
        {
            if (confirm)
            {
                var confirmResult = MessageBox.Show(
                    "Server kapatılacak. Emin misiniz?",
                    "Kapatma Onayı",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirmResult != DialogResult.Yes)
                {
                    _input.Focus();
                    return;
                }
            }

            _commandQueue.Enqueue(command);
            AppendLine($"[UI] {text} requested.", Color.FromArgb(150, 197, 255));
            _input.Focus();
        };
        return btn;
    }

    private void UpdateStatsBars(double cpuPercent, double ramPercent, long ramUsedBytes)
    {
        cpuPercent = Math.Clamp(cpuPercent, 0, 100);
        ramPercent = Math.Clamp(ramPercent, 0, 100);
        int cpuLevel = (int)Math.Round(cpuPercent / 10.0, MidpointRounding.AwayFromZero);
        int ramLevel = (int)Math.Round(ramPercent / 10.0, MidpointRounding.AwayFromZero);
        cpuLevel = Math.Clamp(cpuLevel, 0, 10);
        ramLevel = Math.Clamp(ramLevel, 0, 10);
        _cpuBar.Text = $"{FillBar[..cpuLevel]}{EmptyBar[cpuLevel..]}  {cpuPercent:0.0}%";
        _ramBar.Text = $"{FillBar[..ramLevel]}{EmptyBar[ramLevel..]}  {FormatBytes(ramUsedBytes)}";
    }

    private void RefreshRuntimeStats()
    {
        int players = _activePlayersProvider?.Invoke() ?? _activePlayers;
        _activePlayers = Math.Max(0, players);
        _activePlayersLabel.Text = $"  Players Online:  {_activePlayers}";
        int totalAccounts = Math.Max(0, _totalAccountsProvider?.Invoke() ?? 0);
        int totalChars = Math.Max(0, _totalCharsProvider?.Invoke() ?? 0);
        int totalItems = Math.Max(0, _totalItemsProvider?.Invoke() ?? 0);
        int totalScripts = Math.Max(0, _totalScriptsProvider?.Invoke() ?? 0);
        _totalAccountsLabel.Text = $"  Accounts:  {totalAccounts}";
        _totalCharsLabel.Text = $"  Characters:  {totalChars}";
        _totalItemsLabel.Text = $"  Items:  {totalItems}";
        _totalScriptsLabel.Text = $"  Scripts:  {totalScripts}";
        _uptimeLabel.Text = $"  Uptime:  {FormatUptime(DateTime.UtcNow - _serverStartUtc)}";

        double cpuPercent = 0;
        double ramPercent = 0;
        DateTime now = DateTime.UtcNow;

        long ramUsedBytes;
        using (var proc = Process.GetCurrentProcess())
        {
            TimeSpan cpuNow = proc.TotalProcessorTime;
            if (_lastCpuSampleUtc != default)
            {
                double wallSeconds = (now - _lastCpuSampleUtc).TotalSeconds;
                if (wallSeconds > 0.001)
                {
                    double cpuSeconds = (cpuNow - _lastCpuTime).TotalSeconds;
                    cpuPercent = (cpuSeconds / (wallSeconds * Environment.ProcessorCount)) * 100.0;
                }
            }
            _lastCpuTime = cpuNow;
            _lastCpuSampleUtc = now;

            ramUsedBytes = proc.WorkingSet64;
            long totalAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (totalAvailable > 0)
                ramPercent = (ramUsedBytes / (double)totalAvailable) * 100.0;
        }

        UpdateStatsBars(cpuPercent, ramPercent, ramUsedBytes);
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.Ticks < 0) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        if (t.TotalHours < 24) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    private static string FormatBytes(long bytes)
    {
        const double Kb = 1024.0;
        const double Mb = Kb * 1024.0;
        const double Gb = Mb * 1024.0;

        if (bytes >= Gb) return $"{bytes / Gb:0.00} GB";
        if (bytes >= Mb) return $"{bytes / Mb:0.0} MB";
        if (bytes >= Kb) return $"{bytes / Kb:0.0} KB";
        return $"{bytes} B";
    }

    private void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                e.SuppressKeyPress = true;
                string cmd = _input.Text.Trim();
                if (!string.IsNullOrEmpty(cmd))
                {
                    _history.Add(cmd);
                    if (_history.Count > MaxHistory)
                        _history.RemoveAt(0);
                    _historyIndex = -1;

                    AppendLine(cmd, Color.FromArgb(239, 247, 255));
                    _commandQueue.Enqueue(cmd);
                }
                _input.Text = "";
                break;

            case Keys.Up:
                e.SuppressKeyPress = true;
                if (_history.Count > 0)
                {
                    if (_historyIndex < 0)
                        _historyIndex = _history.Count - 1;
                    else if (_historyIndex > 0)
                        _historyIndex--;
                    _input.Text = _history[_historyIndex];
                    _input.SelectionStart = _input.Text.Length;
                }
                break;

            case Keys.Down:
                e.SuppressKeyPress = true;
                if (_historyIndex >= 0)
                {
                    _historyIndex++;
                    if (_historyIndex >= _history.Count)
                    {
                        _historyIndex = -1;
                        _input.Text = "";
                    }
                    else
                    {
                        _input.Text = _history[_historyIndex];
                        _input.SelectionStart = _input.Text.Length;
                    }
                }
                break;

            case Keys.Escape:
                e.SuppressKeyPress = true;
                _input.Text = "";
                _historyIndex = -1;
                break;
        }
    }

    public void AppendLine(string text, Color color)
    {
        if (IsDisposed) return;
        _pendingLines.Enqueue((text, color));
    }

    /// <summary>
    /// Flush buffered log lines to the RichTextBox in a single batch.
    /// Called by _flushTimer on the UI thread — avoids flooding the message pump
    /// with per-line BeginInvoke calls that can cause the window to lose focus.
    /// </summary>
    private void FlushPendingLines()
    {
        if (_pendingLines.IsEmpty || IsDisposed) return;

        bool follow = _followCheck.Checked;

        _output.SuspendLayout();
        try
        {
            int processed = 0;
            while (processed < MaxLinesPerFlush && _pendingLines.TryDequeue(out var entry))
            {
                _output.SelectionStart = _output.TextLength;
                _output.SelectionLength = 0;
                _output.SelectionColor = entry.Color;
                _output.AppendText(entry.Text + Environment.NewLine);
                _lineCount++;
                UpdateUiStats(entry.Text, entry.Color);
                processed++;
            }

            // Trim excess lines
            if (_output.Lines.Length > MaxLines)
            {
                int removeCount = _output.Lines.Length - MaxLines;
                int removeEnd = _output.GetFirstCharIndexFromLine(removeCount);
                _output.SelectionStart = 0;
                _output.SelectionLength = removeEnd;
                _output.SelectedText = "";
            }

            if (follow)
            {
                _output.SelectionStart = _output.TextLength;
                _output.ScrollToCaret();
            }
        }
        finally
        {
            _output.ResumeLayout();
        }
    }

    public void Emit(LogEvent logEvent)
    {
        if (IsDisposed) return;

        using var sw = new StringWriter();
        _formatter.Format(logEvent, sw);
        string text = sw.ToString().TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(text)) return;

        var color = logEvent.Level switch
        {
            LogEventLevel.Error or LogEventLevel.Fatal => AccentRed,
            LogEventLevel.Warning => Color.FromArgb(255, 72, 72),
            LogEventLevel.Debug or LogEventLevel.Verbose => Color.FromArgb(195, 154, 102),
            _ => Color.FromArgb(204, 225, 255),
        };

        _pendingLines.Enqueue((text, color));
    }

    private void UpdateUiStats(string text, Color color)
    {
        _ = text;
        _ = color;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _flushTimer.Stop();
        _flushTimer.Dispose();
        _statsTimer.Stop();
        _statsTimer.Dispose();
        ShutdownRequested?.Invoke();
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshRuntimeStats();
        _statsTimer.Start();
        if (_followCheck.Checked)
        {
            _output.SelectionStart = _output.TextLength;
            _output.ScrollToCaret();
        }
        _input.Focus();
    }
}
