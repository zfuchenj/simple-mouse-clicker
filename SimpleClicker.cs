using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class SimpleClicker : Form
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    private readonly NumericUpDown interval = new NumericUpDown();
    private readonly NumericUpDown limit = new NumericUpDown();
    private readonly ComboBox button = new ComboBox();
    private readonly Label status = new Label();
    private readonly Label countLabel = new Label();
    private readonly Label shortcuts = new Label();
    private readonly Button toggle = new Button();
    private readonly Timer timer = new Timer();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private enum RunState { Idle, Countdown, Running, Paused }
    private RunState state = RunState.Idle;
    private long deadline;
    private long count;
    private bool f6WasDown;
    private Func<Keys, bool> keyDown = delegate(Keys key) { return GetAsyncKeyState((int)key) < 0; };
    private Func<Point> cursorPosition = delegate { return Cursor.Position; };
    private Func<long> elapsed;
    private Action<uint> click = delegate(uint flags) { mouse_event(flags, 0, 0, 0, UIntPtr.Zero); };

    [STAThread]
    private static void Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Construction smoke check: never shows the window or starts clicking.
        if (args.Length == 2 && args[0] == "--check")
        {
            using (SimpleClicker form = new SimpleClicker())
            {
                form.CheckBehavior();
                System.IO.File.WriteAllText(args[1], "PASS: defaults; F6 edge detection; F7 priority stop; Escape stop; countdown stop; corner stop; right-click flags; click limit; pause/resume; interval scheduling. Mouse output replaced by a counter: no mouse events sent.");
            }
            return;
        }
        bool created;
        using (System.Threading.Mutex instance = new System.Threading.Mutex(true, "Local\\Codex.SimpleClicker.V2", out created))
        {
            if (!created)
            {
                MessageBox.Show("连点器已经打开，请使用已有窗口。", "简单鼠标连点器");
                return;
            }
            try { Application.Run(new SimpleClicker()); }
            finally { instance.ReleaseMutex(); }
        }
    }

    private SimpleClicker()
    {
        elapsed = delegate { return clock.ElapsedMilliseconds; };
        Text = "简单鼠标连点器 · 修复版";
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 395);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 248, 251);

        AddLabel("鼠标连点器", 24, 18, 430, 32).Font = new Font(Font.FontFamily, 16F, FontStyle.Bold);
        AddLabel("点击鼠标当前位置，启动前预留 3 秒移动鼠标。", 24, 58, 440, 26);
        AddLabel("点击间隔（毫秒）", 24, 105, 180, 26);
        interval.SetBounds(230, 103, 210, 30);
        interval.Minimum = 20;
        interval.Maximum = 60000;
        interval.Value = 100;
        interval.Increment = 10;
        Controls.Add(interval);

        AddLabel("鼠标按键", 24, 149, 180, 26);
        button.SetBounds(230, 147, 210, 30);
        button.DropDownStyle = ComboBoxStyle.DropDownList;
        button.Items.AddRange(new object[] { "左键", "右键" });
        button.SelectedIndex = 0;
        Controls.Add(button);

        AddLabel("点击次数（0 为无限）", 24, 193, 200, 26);
        limit.SetBounds(230, 191, 210, 30);
        limit.Minimum = 0;
        limit.Maximum = 1000000;
        Controls.Add(limit);

        toggle.Text = "开始（F6）";
        toggle.SetBounds(24, 239, 200, 40);
        toggle.Click += delegate { Toggle(); };
        Controls.Add(toggle);
        Button stop = new Button();
        stop.Text = "停止（F7）";
        stop.SetBounds(240, 239, 200, 40);
        stop.Click += delegate { Stop("已停止"); };
        Controls.Add(stop);

        status.SetBounds(24, 291, 290, 25);
        status.Text = "就绪";
        status.ForeColor = Color.FromArgb(35, 93, 160);
        Controls.Add(status);
        countLabel.SetBounds(310, 291, 145, 25);
        countLabel.Text = "已点击：0";
        Controls.Add(countLabel);
        shortcuts.SetBounds(24, 326, 435, 56);
        shortcuts.Font = new Font(Font.FontFamily, 9F);
        shortcuts.Text = "F6 开始 / 暂停 / 继续    ·    F7 或 Esc 停止\r\n紧急停止：把鼠标移到任一屏幕左上角。";
        Controls.Add(shortcuts);
        timer.Interval = 10;
        timer.Tick += Tick;
        FormClosing += delegate { timer.Stop(); state = RunState.Idle; };
    }

    private Label AddLabel(string text, int x, int y, int width, int height)
    {
        Label label = new Label();
        label.Text = text;
        label.SetBounds(x, y, width, height);
        Controls.Add(label);
        return label;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        f6WasDown = keyDown(Keys.F6);
        // Poll continuously, including while idle or paused. No exclusive hotkey registration.
        timer.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        timer.Stop();
        base.OnHandleDestroyed(e);
    }

    private static bool IsStopCorner(Point position)
    {
        foreach (Screen screen in Screen.AllScreens)
            if (new Rectangle(screen.Bounds.Left, screen.Bounds.Top, 5, 5).Contains(position)) return true;
        return false;
    }

    private void Toggle()
    {
        if (state == RunState.Running || state == RunState.Countdown)
        {
            state = RunState.Paused;
            status.Text = "已暂停";
            toggle.Text = "继续（F6）";
            return;
        }
        if (state == RunState.Idle)
        {
            count = 0;
            countLabel.Text = "已点击：0";
        }
        interval.Enabled = button.Enabled = limit.Enabled = false;
        state = RunState.Countdown;
        deadline = elapsed() + 3000;
        status.Text = "3 秒后开始，请移动鼠标…";
        toggle.Text = "暂停（F6）";
    }

    private void Tick(object sender, EventArgs e)
    {
        bool f6 = keyDown(Keys.F6);
        bool stopDown = keyDown(Keys.F7) || keyDown(Keys.Escape);
        bool f6Pressed = f6 && !f6WasDown;
        f6WasDown = f6;
        // Evaluate every stop condition before countdown, start, or mouse output.
        if (stopDown)
        {
            if (state != RunState.Idle) Stop("已停止");
            return;
        }
        if (state != RunState.Idle && IsStopCorner(cursorPosition()))
        {
            Stop("已紧急停止（鼠标移至左上角）");
            return;
        }
        if (f6Pressed) Toggle();
        long now = elapsed();
        if (state == RunState.Countdown)
        {
            if (now < deadline)
            {
                status.Text = ((deadline - now + 999) / 1000) + " 秒后开始，请移动鼠标…";
                return;
            }
            state = RunState.Running;
            status.Text = "正在连点 · F7 停止";
        }
        if (state != RunState.Running || now < deadline) return;
        uint flags = button.SelectedIndex == 0 ? 0x0002u | 0x0004u : 0x0008u | 0x0010u;
        click(flags);
        count++;
        countLabel.Text = "已点击：" + count;
        // Schedule from the actual click: never queue a burst of overdue clicks.
        deadline = now + (long)interval.Value;
        if (limit.Value > 0 && count >= (long)limit.Value) Stop("已完成设定次数");
    }

    private void Stop(string message)
    {
        state = RunState.Idle;
        interval.Enabled = button.Enabled = limit.Enabled = true;
        toggle.Text = "开始（F6）";
        status.Text = message;
    }

    private void CheckBehavior()
    {
        Action<bool, string> require = delegate(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); };
        require(state == RunState.Idle && !timer.Enabled && interval.Value == 100 && limit.Value == 0 && button.SelectedIndex == 0, "Defaults");
        Keys held = Keys.None;
        bool both = false;
        int sent = 0;
        uint lastFlags = 0;
        long now = 0;
        Point cursor = new Point(Screen.PrimaryScreen.Bounds.Left + 100, Screen.PrimaryScreen.Bounds.Top + 100);
        keyDown = delegate(Keys key) { return key == held || (both && key == Keys.F6); };
        cursorPosition = delegate { return cursor; };
        elapsed = delegate { return now; };
        click = delegate(uint flags) { sent++; lastFlags = flags; };
        held = Keys.F6; Tick(null, EventArgs.Empty);
        require(state == RunState.Countdown, "F6 start");
        Tick(null, EventArgs.Empty);
        require(state == RunState.Countdown, "F6 held must not retrigger");
        held = Keys.F7; Tick(null, EventArgs.Empty);
        require(state == RunState.Idle && sent == 0, "F7 countdown stop");
        state = RunState.Running; deadline = 0; both = true;
        Tick(null, EventArgs.Empty);
        require(state == RunState.Idle && sent == 0, "F7 wins over F6 and click");
        both = false; held = Keys.Escape; state = RunState.Running;
        Tick(null, EventArgs.Empty);
        require(state == RunState.Idle && sent == 0, "Escape wins over click");
        held = Keys.None; state = RunState.Running;
        cursor = Screen.PrimaryScreen.Bounds.Location;
        Tick(null, EventArgs.Empty);
        require(state == RunState.Idle && sent == 0, "Corner wins over click");
        cursor.Offset(100, 100);
        button.SelectedIndex = 1; limit.Value = 2; Toggle();
        now = 2999; Tick(null, EventArgs.Empty);
        require(sent == 0, "Countdown delay");
        now = 3000; Tick(null, EventArgs.Empty);
        require(sent == 1 && lastFlags == 0x18 && state == RunState.Running, "Right click");
        now = 3099; Tick(null, EventArgs.Empty);
        require(sent == 1, "Interval gate");
        now = 3100; Tick(null, EventArgs.Empty);
        require(sent == 2 && state == RunState.Idle && count == 2, "Finite count");
        Toggle(); held = Keys.F6; Tick(null, EventArgs.Empty);
        require(state == RunState.Paused, "Pause");
        held = Keys.None; Tick(null, EventArgs.Empty);
        held = Keys.F6; Tick(null, EventArgs.Empty);
        require(state == RunState.Countdown, "Resume countdown");
        held = Keys.F7; Tick(null, EventArgs.Empty);
        require(state == RunState.Idle && sent == 2, "No extra clicks after stop");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
