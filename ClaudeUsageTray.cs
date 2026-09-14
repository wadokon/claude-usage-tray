// ClaudeUsageTray - Claude の使用量をデスクトップに常駐表示するウィジェット (Windows)
// Claude Code のログイン情報（~/.claude/.credentials.json）を読み、
// 使用量エンドポイントから 5時間枠・週間枠の残量を取得してトレイとウィジェットに表示する。
//
// 注意: 本ツールは非公式です。利用している使用量 API は公開ドキュメントに記載が無く、
//       予告なく変更・停止される可能性があります。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClaudeUsageTray
{
    static class Program
    {
        public static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        // 環境変数 CLAUDEUSAGETRAY_DEBUG=1 のときだけ poll-log.txt に通信ログを残す
        public static bool DebugLog
        {
            get { return Environment.GetEnvironmentVariable("CLAUDEUSAGETRAY_DEBUG") == "1"; }
        }

        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, "Global\\ClaudeUsageTrayMutex", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // API は TLS1.2 必須
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try
                {
                    Application.Run(new TrayContext());
                }
                catch (Exception ex)
                {
                    Log(ex.ToString());
                    throw;
                }
            }
        }

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDir, "tray-error.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + "\r\n");
            }
            catch { }
        }
    }

    enum RowKind { Bar, Header, Message }

    // ウィジェットに表示する1行分（5時間枠 / 週間枠 / モデル別枠、ChatGPT 併用時は見出しとメッセージも）
    class UsageRow
    {
        public RowKind Kind = RowKind.Bar;
        public string Label;  // Bar=枠名 / Header=見出し / Message=本文
        public int Remain;    // 残量 %
        public string Reset;  // リセット時刻の表示用文字列
        public string Note;   // Header の右端に出す補足（取得失敗中の警告など）
    }

    static class UiUtil
    {
        // 残量に応じた色。50%以上=緑、20%以上=橙、それ未満=赤
        public static Color RemainColor(int remain)
        {
            if (remain >= 50) return Color.FromArgb(34, 197, 94);
            if (remain >= 20) return Color.FromArgb(245, 158, 11);
            return Color.FromArgb(239, 68, 68);
        }
    }

    class WidgetForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GestureInfo info);
        [DllImport("user32.dll")] static extern bool CloseGestureInfoHandle(IntPtr hGestureInfo);

        [StructLayout(LayoutKind.Sequential)]
        struct GestureInfo
        {
            public int cbSize;
            public int dwFlags;
            public int dwID;
            public IntPtr hwndTarget;
            public short x, y;
            public int dwInstanceID;
            public int dwSequenceID;
            public long ullArguments;  // GID_ZOOM では2本の指の間隔
            public int cbExtraArgs;
        }

        const int WM_MOUSEWHEEL = 0x020A;
        const int WM_GESTURE = 0x0119;
        const int MK_CONTROL = 0x0008;
        const int GID_ZOOM = 3;
        const int GF_BEGIN = 1;

        // 以下は 100% のときの大きさ。描画時に表示倍率を掛ける
        const int RowH = 27;
        const int HeaderH = 20;
        const int PadTop = 8;
        const int WidgetW = 240;

        // 表示倍率（%）。リモートデスクトップでスマホから見るときなどに大きくする
        public static readonly int[] ZoomPresets = { 100, 125, 150, 200, 250, 300, 400 };
        const int MinZoom = 100;
        const int MaxZoom = 400;
        const int ZoomStep = 25;  // Ctrl+ホイール1段あたり

        int zoomPercent = 100;
        int logicalH = 62;       // 100% のときの高さ（初期値は2行分）
        int wheelDelta;          // 高精度タッチパッドは 120 未満の刻みで来るので貯めてから1段動かす
        long pinchDistance;
        int pinchZoom;

        public bool IsError;
        public string ErrorMessage = "";
        public bool IsStale;          // 取得失敗中だが前回値を表示している状態
        public string StaleSince = "";
        public bool AlwaysOnTop = true;
        public List<UsageRow> Rows = new List<UsageRow>();

        readonly string posPath = Path.Combine(Program.AppDir, "widget-position.json");

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x8000000;  // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        public WidgetForm()
        {
            Text = "Claude Usage";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 28, 34);
            Opacity = 0.92;
            DoubleBuffered = true;

            LoadPosition();
            TopMost = AlwaysOnTop;

            // 余白のドラッグで移動
            MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
            };
            ResizeEnd += delegate { SavePosition(); };
        }

        public int ZoomPercent
        {
            get { return zoomPercent; }
        }

        float Zoom
        {
            get { return zoomPercent / 100f; }
        }

        int Px(int logical)
        {
            return (int)Math.Round(logical * Zoom);
        }

        void LoadPosition()
        {
            Point? saved = null;
            try
            {
                if (File.Exists(posPath))
                {
                    var ser = new JavaScriptSerializer();
                    var d = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(posPath));
                    saved = new Point(Convert.ToInt32(d["X"]), Convert.ToInt32(d["Y"]));
                    if (d.ContainsKey("TopMost")) AlwaysOnTop = Convert.ToBoolean(d["TopMost"]);
                    if (d.ContainsKey("Zoom"))
                        zoomPercent = Math.Max(MinZoom, Math.Min(MaxZoom, Convert.ToInt32(d["Zoom"])));
                    // 前回の高さで始めないと、初回の FitToRows で上に伸びた分だけ保存位置より上にずれる
                    if (d.ContainsKey("Height"))
                        logicalH = Math.Max(PadTop + RowH, Math.Min(1000, Convert.ToInt32(d["Height"])));
                }
            }
            catch { }

            // 倍率が決まってから大きさと既定位置（プライマリ画面の右下）を決める
            ClientSize = new Size(Px(WidgetW), Px(logicalH));
            var wa = Screen.PrimaryScreen.WorkingArea;
            var loc = new Point(wa.Right - Width, wa.Bottom - Height);
            // 画面構成が変わって画面外になっていた場合は既定位置に戻す
            if (saved.HasValue)
            {
                foreach (var sc in Screen.AllScreens)
                {
                    if (sc.WorkingArea.Contains(saved.Value)) { loc = saved.Value; break; }
                }
            }
            Location = loc;
        }

        public void SavePosition()
        {
            try
            {
                File.WriteAllText(posPath, string.Format(
                    "{{\"X\":{0},\"Y\":{1},\"TopMost\":{2},\"Zoom\":{3},\"Height\":{4}}}",
                    Location.X, Location.Y, AlwaysOnTop ? "true" : "false", zoomPercent, logicalH));
            }
            catch { }
        }

        // 行に合わせて高さを変える。下端を固定したいので上に伸ばす
        public void FitToRows()
        {
            int h = PadTop;
            foreach (var row in Rows) h += row.Kind == RowKind.Header ? HeaderH : RowH;
            if (Rows.Count == 0) h += RowH;
            if (logicalH == h) return;
            logicalH = h;
            int diff = Px(h) - ClientSize.Height;
            ClientSize = new Size(Px(WidgetW), Px(h));
            Top -= diff;
        }

        // 表示倍率を変える。作業領域の中での相対位置を保って伸び縮みさせる
        public void SetZoom(int percent)
        {
            var old = Bounds;
            var wa = Screen.FromRectangle(old).WorkingArea;
            // スマホを画面にしているときなど、作業領域に収まらない倍率にはしない
            int fit = (int)Math.Min(wa.Width * 100L / WidgetW, wa.Height * 100L / logicalH);
            percent = Math.Max(MinZoom, Math.Min(Math.Min(MaxZoom, fit), percent));
            if (percent == zoomPercent) return;
            zoomPercent = percent;

            int w = Px(WidgetW), h = Px(logicalH);
            Bounds = new Rectangle(
                KeepRelative(old.X, old.Width, w, wa.X, wa.Width),
                KeepRelative(old.Y, old.Height, h, wa.Y, wa.Height), w, h);
            Invalidate();
            SavePosition();
        }

        // 作業領域の中での相対位置（左端/上端=0、右端/下端=1）を保ったまま大きさを変えたときの位置。
        // 端に寄せてあれば端に付いたまま、倍率を戻せば元の位置に戻る
        static int KeepRelative(int pos, int size, int newSize, int areaStart, int areaSize)
        {
            int room = areaSize - size;
            double t = room > 0 ? Math.Max(0.0, Math.Min(1.0, (pos - areaStart) / (double)room)) : 0.0;
            return areaStart + (int)Math.Round(Math.Max(0, areaSize - newSize) * t);
        }

        protected override void WndProc(ref Message m)
        {
            // Ctrl+ホイール（高精度タッチパッドのピンチもこれで届く）で拡大縮小
            if (m.Msg == WM_MOUSEWHEEL && (m.WParam.ToInt64() & MK_CONTROL) != 0)
            {
                wheelDelta += (short)(m.WParam.ToInt64() >> 16);
                int steps = wheelDelta / 120;
                if (steps != 0)
                {
                    wheelDelta -= steps * 120;
                    SetZoom(zoomPercent + steps * ZoomStep);
                }
                return;
            }

            // タッチ画面のピンチ。開始時の指の間隔との比で倍率を決める（5% 刻み）
            if (m.Msg == WM_GESTURE)
            {
                var gi = new GestureInfo();
                gi.cbSize = Marshal.SizeOf(typeof(GestureInfo));
                if (GetGestureInfo(m.LParam, ref gi) && gi.dwID == GID_ZOOM)
                {
                    if ((gi.dwFlags & GF_BEGIN) != 0)
                    {
                        pinchDistance = gi.ullArguments;
                        pinchZoom = zoomPercent;
                    }
                    else if (pinchDistance > 0)
                    {
                        SetZoom((int)Math.Round(pinchZoom * gi.ullArguments / (double)pinchDistance / 5) * 5);
                    }
                    CloseGestureInfoHandle(m.LParam);
                    return;
                }
            }

            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            // 100% のときの座標で描いて、表示倍率はまとめて掛ける
            float z = Zoom;
            g.ScaleTransform(z, z);

            using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var fontSmall = new Font("Segoe UI", 7.5f))
            using (var fg = new SolidBrush(Color.FromArgb(235, 235, 240)))
            using (var dim = new SolidBrush(Color.FromArgb(150, 150, 158)))
            using (var track = new SolidBrush(Color.FromArgb(60, 60, 70)))
            using (var border = new Pen(Color.FromArgb(70, 70, 82)))
            {
                // 拡大しても枠線が外周の内側にぴったり収まるようにずらす（100% のときは従来と同じ位置）
                float inset = (z - 1) / (2 * z);
                g.DrawRectangle(border, inset, inset, ClientSize.Width / z - 1, ClientSize.Height / z - 1);

                if (IsError)
                {
                    g.DrawString("取得エラー", font, dim, 12f, 10f);
                    g.DrawString(ErrorMessage, fontSmall, dim,
                        new RectangleF(12f, 30f, WidgetW - 24, logicalH - 34));
                    return;
                }

                // 前回値を表示中は右上に警告マークと最終取得時刻を出す
                if (IsStale)
                {
                    using (var warn = new SolidBrush(Color.FromArgb(245, 158, 11)))
                    {
                        g.DrawString("!", font, warn, WidgetW - 16, 1f);
                        g.DrawString(StaleSince, fontSmall, warn, WidgetW - 52, 3f);
                    }
                }

                int y = PadTop;
                foreach (var row in Rows)
                {
                    if (row.Kind == RowKind.Header)
                    {
                        // 2つ目以降の見出しの上に区切り線を引く
                        if (y > PadTop) g.DrawLine(border, 8, y + 1, WidgetW - 9, y + 1);
                        g.DrawString(row.Label, fontSmall, dim, 8f, y + 4);
                        if (!string.IsNullOrEmpty(row.Note))
                        {
                            using (var warn = new SolidBrush(Color.FromArgb(245, 158, 11)))
                            using (var right = new StringFormat { Alignment = StringAlignment.Far })
                                g.DrawString(row.Note, fontSmall, warn,
                                    new RectangleF(0, y + 4, WidgetW - 8, HeaderH), right);
                        }
                        y += HeaderH;
                        continue;
                    }
                    if (row.Kind == RowKind.Message)
                    {
                        g.DrawString(row.Label, fontSmall, dim, new RectangleF(8f, y + 2, WidgetW - 16, RowH - 4));
                        y += RowH;
                        continue;
                    }

                    g.DrawString(row.Label, font, fg, 8f, y);
                    g.FillRectangle(track, 52, y + 3, 80, 11);
                    int w = Math.Max(1, (int)Math.Round(80.0 * row.Remain / 100.0));
                    using (var bar = new SolidBrush(UiUtil.RemainColor(row.Remain)))
                        g.FillRectangle(bar, 52, y + 3, w, 11);
                    g.DrawString("残" + row.Remain + "%", font, fg, 136f, y);
                    g.DrawString(row.Reset, fontSmall, dim, 182f, y + 2);
                    y += RowH;
                }
            }
        }
    }

    class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        const int PollMs = 120000;      // 通常のポーリング間隔（2分）
        const int MaxBackoffMs = 300000; // エラー時の最大間隔（5分）
        const int MaxRateLimitWaitMs = 3900000; // 429 のときに待つ上限（65分）
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

        // アクセストークンの自動更新（Claude Code と同じ OAuth クライアント）
        const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        const long RefreshMarginMs = 300000; // 期限の5分前には更新しておく

        // ChatGPT は Codex CLI のログイン情報（~/.codex/auth.json）で ChatGPT プランの枠を取得する
        const string GptUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
        const string GptTokenUrl = "https://auth.openai.com/oauth/token";
        const string GptClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

        const string ClaudeLoginMenu = "Claude にログイン (Claude Code を起動)";
        const string GptLoginMenu = "ChatGPT にログイン (codex login)";

        readonly NotifyIcon notify;
        readonly WidgetForm widget;
        readonly System.Windows.Forms.Timer timer;
        readonly ToolTip widgetTip = new ToolTip();
        readonly string credPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude\\.credentials.json");
        readonly string codexAuthPath = Path.Combine(CodexHome(), "auth.json");
        readonly string pollLogPath = Path.Combine(Program.AppDir, "poll-log.txt");

        // Claude 側の状態
        readonly RetryGate claudeGate = new RetryGate();
        List<UsageRow> claudeRows = new List<UsageRow>();
        string claudeGoodDetail = "";
        string claudeError;       // 直近の取得に失敗していればその理由（成功中は null）
        string claudeErrorBrief;
        string claudePlan;
        int claudeFive, claudeWeekWorst;
        string claudeFiveReset = "";
        DateTime lastSuccess = DateTime.MinValue;

        // ChatGPT 側の状態（auth.json に ChatGPT ログインがあるときだけ有効）
        readonly RetryGate gptGate = new RetryGate();
        bool gptEnabled;
        List<UsageRow> gptRows = new List<UsageRow>();
        string gptGoodDetail = "";
        string gptError;
        string gptErrorBrief;
        string gptPlan;
        string gptRawBody = "";
        DateTime gptLastSuccess = DateTime.MinValue;

        string lastDetail = "未取得";
        string lastRawBody = "";
        IntPtr prevHicon = IntPtr.Zero;

        // トークン更新自体が拒否された（リフレッシュトークンが無効）＝再ログインが必要
        class RefreshFailedException : Exception
        {
            public RefreshFailedException(string message, Exception inner) : base(message, inner) { }
        }

        // 取得失敗時の待ち時間をサービスごとに管理する（失敗のたびに倍、429 は Retry-After に従う）
        class RetryGate
        {
            int backoffMs;
            DateTime nextTry = DateTime.MinValue;

            // 手動更新のときは待ち時間中でも再試行する
            public bool CanTry(bool manual)
            {
                return manual || DateTime.Now >= nextTry;
            }

            public void Succeeded()
            {
                backoffMs = 0;
                nextTry = DateTime.MinValue;
            }

            public void Failed(Exception ex)
            {
                int wait;
                if (HttpStatus(ex) == 429)
                    wait = Math.Max(PollMs, Math.Min(RetryAfterMs(ex as WebException), MaxRateLimitWaitMs));
                else
                    wait = backoffMs = backoffMs == 0 ? PollMs : Math.Min(backoffMs * 2, MaxBackoffMs);
                // タイマーの刻みとずれて1回余計に飛ばさないよう、少し早めに解禁する
                nextTry = DateTime.Now.AddMilliseconds(wait - 10000);
            }
        }

        public TrayContext()
        {
            widget = new WidgetForm();

            var menu = new ContextMenuStrip();
            menu.Items.Add("ウィジェットを表示/隠す", null, delegate
            {
                if (widget.Visible) widget.Hide(); else widget.Show();
            });

            var miTopMost = new ToolStripMenuItem("常に前面に表示");
            miTopMost.Checked = widget.AlwaysOnTop;
            miTopMost.CheckOnClick = true;
            miTopMost.CheckedChanged += delegate
            {
                widget.AlwaysOnTop = miTopMost.Checked;
                widget.TopMost = miTopMost.Checked;
                widget.SavePosition();
            };
            menu.Items.Add(miTopMost);

            var miZoom = new ToolStripMenuItem("表示サイズ");
            foreach (var p in WidgetForm.ZoomPresets)
            {
                int percent = p;
                var mi = new ToolStripMenuItem(percent + "%", null, delegate { widget.SetZoom(percent); });
                mi.Tag = percent;
                miZoom.DropDownItems.Add(mi);
            }
            menu.Items.Add(miZoom);

            menu.Items.Add("詳細を表示", null, delegate
            {
                notify.ShowBalloonTip(8000, BalloonTitle, lastDetail, ToolTipIcon.Info);
            });
            menu.Items.Add("今すぐ更新", null, delegate { UpdateStatus(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Claude の使用状況ページを開く", null, delegate
            {
                try { Process.Start("https://claude.ai/settings/usage"); } catch { }
            });
            menu.Items.Add(ClaudeLoginMenu, null, delegate
            {
                try { Process.Start("cmd.exe", "/k claude"); } catch { }
            });
            var miGptPage = new ToolStripMenuItem("ChatGPT の使用状況ページを開く", null, delegate
            {
                try { Process.Start("https://chatgpt.com/codex/settings/usage"); } catch { }
            });
            var miGptLogin = new ToolStripMenuItem(GptLoginMenu, null, delegate
            {
                try { Process.Start("cmd.exe", "/k codex login"); } catch { }
            });
            menu.Items.Add(miGptPage);
            menu.Items.Add(miGptLogin);
            // ChatGPT の項目は Codex で ChatGPT ログインしているときだけ出す
            menu.Opening += delegate
            {
                miGptPage.Visible = gptEnabled;
                miGptLogin.Visible = gptEnabled;
                // Ctrl+ホイールやピンチで半端な倍率になっても分かるよう、今の倍率を項目名に出す
                miZoom.Text = "表示サイズ (" + widget.ZoomPercent + "%)";
                foreach (ToolStripMenuItem mi in miZoom.DropDownItems)
                    mi.Checked = (int)mi.Tag == widget.ZoomPercent;
            };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了", null, delegate
            {
                widget.SavePosition();
                notify.Visible = false;
                notify.Dispose();
                ExitThread();
            });

            notify = new NotifyIcon();
            notify.Visible = true;
            notify.ContextMenuStrip = menu;
            notify.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                UpdateStatus(true);
                notify.ShowBalloonTip(8000, BalloonTitle, lastDetail, ToolTipIcon.Info);
            };

            widget.ContextMenuStrip = menu;
            widget.MouseDoubleClick += delegate { UpdateStatus(true); };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = PollMs;
            timer.Tick += delegate { UpdateStatus(false); };

            UpdateStatus(false);
            widget.Show();
            timer.Start();
        }

        void PollLog(string line)
        {
            if (!Program.DebugLog) return;
            try
            {
                var fi = new FileInfo(pollLogPath);
                if (fi.Exists && fi.Length > 204800) fi.Delete();  // 200KB を超えたら作り直す
                File.AppendAllText(pollLogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + "\r\n");
            }
            catch { }
        }

        // 429 のときサーバーが返す Retry-After（秒）を待ち時間に変換する
        static int RetryAfterMs(WebException web)
        {
            try
            {
                var res = web != null ? web.Response as HttpWebResponse : null;
                if (res != null)
                {
                    var v = res.Headers["Retry-After"];
                    int sec;
                    if (!string.IsNullOrEmpty(v) && int.TryParse(v.Trim(), out sec) && sec > 0)
                        return Math.Min(sec, 86400) * 1000 + 5000; // 少し余裕を足す
                }
            }
            catch { }
            return MaxBackoffMs;
        }

        // ISO8601 を「今日ならHH:mm、それ以外はM/d HH:mm」に整形
        static string FormatReset(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "—";
            try
            {
                var t = DateTimeOffset.Parse(iso).ToLocalTime();
                return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("M/d HH:mm");
            }
            catch { return "—"; }
        }

        static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            return (d != null && d.TryGetValue(key, out v)) ? v as string : null;
        }

        static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        static int HttpStatus(Exception ex)
        {
            var web = ex as WebException;
            var res = web != null ? web.Response as HttpWebResponse : null;
            return res != null ? (int)res.StatusCode : 0;
        }

        // Unix 秒を FormatReset と同じ書式に整形
        static string FormatResetUnix(object sec)
        {
            try
            {
                var t = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Convert.ToDouble(sec)).ToLocalTime();
                return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("M/d HH:mm");
            }
            catch { return "—"; }
        }

        // "Claude" + "max" → "Claude Max"
        static string WithPlan(string service, string plan)
        {
            if (string.IsNullOrEmpty(plan)) return service;
            return service + " " + char.ToUpperInvariant(plan[0]) + plan.Substring(1);
        }

        string BalloonTitle
        {
            get { return gptEnabled ? "Claude / ChatGPT 使用状況" : "Claude 使用状況"; }
        }

        // 失敗理由をツールチップ用（長い）とウィジェット用（短い）の2通りで返す
        static void DescribeError(Exception ex, string loginMenu, out string full, out string brief)
        {
            int status = HttpStatus(ex);
            if (status == 429)
            {
                full = "APIレート制限中。間隔を空けて自動再試行します";
                brief = "レート制限中（自動で再試行）";
            }
            else if (status == 401 || status == 403 || ex is RefreshFailedException)
            {
                full = "認証エラー。右クリック →「" + loginMenu + "」で再ログインしてください";
                brief = "認証エラー（右クリック→ログイン）";
            }
            else
            {
                full = "取得エラー: " + ex.Message;
                brief = "取得エラー";
            }
        }

        // トークン更新エンドポイントに JSON を POST して応答を返す。
        // 4xx（429 以外）はリフレッシュトークン自体が無効とみなし、再ログインを促すエラーにする
        static Dictionary<string, object> PostTokenRequest(string url, Dictionary<string, object> payload, string anthropicBeta)
        {
            var ser = new JavaScriptSerializer();
            var body = Encoding.UTF8.GetBytes(ser.Serialize(payload));
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = 15000;
            req.ContentType = "application/json";
            req.ContentLength = body.Length;
            req.UserAgent = "ClaudeUsageTray/1.0";
            if (anthropicBeta != null) req.Headers["anthropic-beta"] = anthropicBeta;
            try
            {
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream()))
                    return (Dictionary<string, object>)ser.DeserializeObject(r.ReadToEnd());
            }
            catch (WebException ex)
            {
                int code = HttpStatus(ex);
                if (code >= 400 && code < 500 && code != 429)
                    throw new RefreshFailedException("トークンの更新に失敗しました (HTTP " + code + ")", ex);
                throw;
            }
        }

        // 本家 CLI が読んでいる最中に壊れないよう、書いてから差し替える
        static void WriteJsonAtomic(string path, object obj)
        {
            var tmp = path + ".tray.tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(obj), new UTF8Encoding(false));
            try { File.Replace(tmp, path, null); }
            catch (FileNotFoundException) { File.Move(tmp, path); }
            finally { if (File.Exists(tmp)) try { File.Delete(tmp); } catch { } }
        }

        // 有効なアクセストークンを返す。期限切れ（間近）なら refreshToken で更新する。
        // force=true のときは期限に関係なく必ず更新する（401 を受けた直後など）。
        string GetAccessToken(bool force)
        {
            var ser = new JavaScriptSerializer();
            var cred = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(credPath));
            if (cred == null || !cred.ContainsKey("claudeAiOauth"))
                throw new Exception("credentials.json に claudeAiOauth がありません");
            var oauth = (Dictionary<string, object>)cred["claudeAiOauth"];
            var token = oauth.ContainsKey("accessToken") ? oauth["accessToken"] as string : null;
            claudePlan = GetStr(oauth, "subscriptionType");

            long expiresAt = 0;
            object rawExpires;
            if (oauth.TryGetValue("expiresAt", out rawExpires) && rawExpires != null)
                try { expiresAt = Convert.ToInt64(rawExpires); } catch { }

            if (!force && !string.IsNullOrEmpty(token) && expiresAt > NowMs() + RefreshMarginMs)
                return token;

            if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(oauth.ContainsKey("refreshToken") ? oauth["refreshToken"] as string : null))
                throw new Exception("credentials.json に accessToken がありません");

            return RefreshAccessToken(cred, oauth);
        }

        // refreshToken でアクセストークンを更新し、credentials.json に書き戻す
        string RefreshAccessToken(Dictionary<string, object> cred, Dictionary<string, object> oauth)
        {
            var refresh = oauth.ContainsKey("refreshToken") ? oauth["refreshToken"] as string : null;
            if (string.IsNullOrEmpty(refresh))
                throw new Exception("credentials.json に refreshToken がありません");

            var payload = new Dictionary<string, object>();
            payload["grant_type"] = "refresh_token";
            payload["refresh_token"] = refresh;
            payload["client_id"] = ClientId;
            var json = PostTokenRequest(TokenUrl, payload, "oauth-2025-04-20");
            var access = json.ContainsKey("access_token") ? json["access_token"] as string : null;
            if (string.IsNullOrEmpty(access))
                throw new Exception("トークン更新のレスポンスに access_token がありません");

            long expiresIn = 0;
            object rawIn;
            if (json.TryGetValue("expires_in", out rawIn) && rawIn != null)
                try { expiresIn = Convert.ToInt64(rawIn); } catch { }
            if (expiresIn <= 0) expiresIn = 28800; // 応答に無ければ8時間とみなす

            oauth["accessToken"] = access;
            oauth["expiresAt"] = NowMs() + expiresIn * 1000;
            var newRefresh = json.ContainsKey("refresh_token") ? json["refresh_token"] as string : null;
            if (!string.IsNullOrEmpty(newRefresh)) oauth["refreshToken"] = newRefresh;

            // リフレッシュトークン自体の期限も返ってきたら合わせて更新する
            long refreshIn = 0;
            object rawRefreshIn;
            if (json.TryGetValue("refresh_token_expires_in", out rawRefreshIn) && rawRefreshIn != null)
                try { refreshIn = Convert.ToInt64(rawRefreshIn); } catch { }
            if (refreshIn > 0) oauth["refreshTokenExpiresAt"] = NowMs() + refreshIn * 1000;

            WriteJsonAtomic(credPath, cred);
            PollLog("REFRESH ok expiresIn=" + expiresIn + "s rotated=" + (!string.IsNullOrEmpty(newRefresh)));
            return access;
        }

        // Claude Code のログイン情報を使って使用量を取得する
        Dictionary<string, object> FetchUsage()
        {
            try
            {
                return RequestUsage(GetAccessToken(false));
            }
            catch (WebException ex)
            {
                // トークンが先に失効していた場合は一度だけ更新してやり直す
                var res = ex.Response as HttpWebResponse;
                if (res == null || (int)res.StatusCode != 401) throw;
                PollLog("401 → トークンを更新して再試行");
                return RequestUsage(GetAccessToken(true));
            }
        }

        Dictionary<string, object> RequestUsage(string token)
        {
            var ser = new JavaScriptSerializer();
            var req = (HttpWebRequest)WebRequest.Create(UsageUrl);
            req.Method = "GET";
            req.Timeout = 15000;
            req.UserAgent = "ClaudeUsageTray/1.0";
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(res.GetResponseStream()))
            {
                lastRawBody = r.ReadToEnd();
                return (Dictionary<string, object>)ser.DeserializeObject(lastRawBody);
            }
        }

        static string CodexHome()
        {
            var env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        // JWT の exp をミリ秒で返す。読めなければ 0
        static long JwtExpMs(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return 0;
                var p = parts[1].Replace('-', '+').Replace('_', '/');
                p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
                var claims = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
                    Encoding.UTF8.GetString(Convert.FromBase64String(p)));
                object exp;
                return claims.TryGetValue("exp", out exp) ? Convert.ToInt64(exp) * 1000 : 0;
            }
            catch { return 0; }
        }

        // Codex に ChatGPT アカウントでログインしていれば auth.json の tokens を返す（未ログインなら null）
        Dictionary<string, object> ReadCodexTokens(out Dictionary<string, object> auth)
        {
            auth = null;
            if (!File.Exists(codexAuthPath)) return null;
            auth = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(codexAuthPath)) as Dictionary<string, object>;
            var tokens = auth != null && auth.ContainsKey("tokens") ? auth["tokens"] as Dictionary<string, object> : null;
            if (tokens == null || string.IsNullOrEmpty(GetStr(tokens, "access_token"))) return null;
            return tokens;
        }

        bool CodexLoggedIn()
        {
            try
            {
                Dictionary<string, object> auth;
                return ReadCodexTokens(out auth) != null;
            }
            catch { return gptEnabled; }  // Codex が書き込み中などで読めないときは前回の判定を保つ
        }

        // 有効な ChatGPT のアクセストークンを返す。
        // Codex 本体は期限（10日）より前に自分で更新するので、こちらは切れる直前か 401 のときだけ更新する
        string GetGptAccessToken(bool force, out string accountId)
        {
            Dictionary<string, object> auth;
            var tokens = ReadCodexTokens(out auth);
            if (tokens == null) throw new Exception("Codex に ChatGPT でログインしていません");
            accountId = GetStr(tokens, "account_id");
            var token = GetStr(tokens, "access_token");
            long exp = JwtExpMs(token);
            if (!force && (exp == 0 || exp > NowMs() + RefreshMarginMs)) return token;
            return RefreshGptToken(auth, tokens);
        }

        // refresh_token で ChatGPT のトークンを更新し、auth.json に Codex と同じ形で書き戻す
        string RefreshGptToken(Dictionary<string, object> auth, Dictionary<string, object> tokens)
        {
            var refresh = GetStr(tokens, "refresh_token");
            if (string.IsNullOrEmpty(refresh))
                throw new RefreshFailedException("auth.json に refresh_token がありません", null);

            var payload = new Dictionary<string, object>();
            payload["client_id"] = GptClientId;
            payload["grant_type"] = "refresh_token";
            payload["refresh_token"] = refresh;
            payload["scope"] = "openid profile email";
            var json = PostTokenRequest(GptTokenUrl, payload, null);
            var access = GetStr(json, "access_token");
            if (string.IsNullOrEmpty(access))
                throw new Exception("トークン更新のレスポンスに access_token がありません");

            tokens["access_token"] = access;
            var idToken = GetStr(json, "id_token");
            if (!string.IsNullOrEmpty(idToken)) tokens["id_token"] = idToken;
            var newRefresh = GetStr(json, "refresh_token");
            if (!string.IsNullOrEmpty(newRefresh)) tokens["refresh_token"] = newRefresh;
            auth["last_refresh"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");

            WriteJsonAtomic(codexAuthPath, auth);
            PollLog("GPT REFRESH ok rotated=" + (!string.IsNullOrEmpty(newRefresh)));
            return access;
        }

        Dictionary<string, object> FetchGptUsage()
        {
            string accountId;
            try
            {
                return RequestGptUsage(GetGptAccessToken(false, out accountId), accountId);
            }
            catch (WebException ex)
            {
                if (HttpStatus(ex) != 401) throw;
                PollLog("GPT 401 → トークンを更新して再試行");
                return RequestGptUsage(GetGptAccessToken(true, out accountId), accountId);
            }
        }

        Dictionary<string, object> RequestGptUsage(string token, string accountId)
        {
            var req = (HttpWebRequest)WebRequest.Create(GptUsageUrl);
            req.Method = "GET";
            req.Timeout = 15000;
            req.UserAgent = "ClaudeUsageTray/1.0";
            req.Headers["Authorization"] = "Bearer " + token;
            if (!string.IsNullOrEmpty(accountId)) req.Headers["ChatGPT-Account-Id"] = accountId;
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(res.GetResponseStream()))
            {
                gptRawBody = r.ReadToEnd();
                return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(gptRawBody);
            }
        }

        // rate_limit の primary_window / secondary_window を行にする（Plus なら 5時間枠 / 週間枠）
        static List<UsageRow> ParseGptRows(Dictionary<string, object> json)
        {
            var rows = new List<UsageRow>();
            var rl = json.ContainsKey("rate_limit") ? json["rate_limit"] as Dictionary<string, object> : null;
            if (rl == null) return rows;
            foreach (var key in new[] { "primary_window", "secondary_window" })
            {
                var w = rl.ContainsKey(key) ? rl[key] as Dictionary<string, object> : null;
                if (w == null) continue;
                object sec, reset;
                w.TryGetValue("limit_window_seconds", out sec);
                w.TryGetValue("reset_at", out reset);
                rows.Add(new UsageRow
                {
                    Label = WindowLabel(sec == null ? 0 : Convert.ToInt64(sec)),
                    Remain = Math.Max(0, 100 - (int)Math.Round(Convert.ToDouble(w["used_percent"]))),
                    Reset = reset == null ? "—" : FormatResetUnix(reset)
                });
            }
            return rows;
        }

        // 枠の長さ（秒）を表示名にする。18000 → 5h、604800 → 週
        static string WindowLabel(long sec)
        {
            if (sec == 604800) return "週";
            if (sec > 0 && sec % 86400 == 0) return (sec / 86400) + "日";
            if (sec > 0 && sec % 3600 == 0) return (sec / 3600) + "h";
            if (sec > 0) return (sec / 60) + "分";
            return "枠";
        }

        // manual=true はユーザー操作による更新（失敗後の待ち時間中でも再試行する）
        void UpdateStatus(bool manual)
        {
            if (claudeGate.CanTry(manual)) UpdateClaude();
            gptEnabled = CodexLoggedIn();
            if (gptEnabled && gptGate.CanTry(manual)) UpdateChatGpt();
            Render();
        }

        void UpdateClaude()
        {
            try
            {
                var json = FetchUsage();

                // 5時間枠
                var five = (Dictionary<string, object>)json["five_hour"];
                int fiveRemain = Math.Max(0, 100 - (int)Math.Round(Convert.ToDouble(five["utilization"])));
                string fiveReset = FormatReset(GetStr(five, "resets_at"));

                // 週間枠は limits 配列に入る。scope.model があればモデル別、無ければ全体
                var overall = new List<Dictionary<string, object>>();
                var perModel = new List<Dictionary<string, object>>();
                var limits = json.ContainsKey("limits") ? json["limits"] as object[] : null;
                if (limits != null)
                {
                    foreach (var o in limits)
                    {
                        var lim = o as Dictionary<string, object>;
                        if (lim == null || (string)lim["group"] != "weekly") continue;
                        var scope = lim.ContainsKey("scope") ? lim["scope"] as Dictionary<string, object> : null;
                        var model = (scope != null && scope.ContainsKey("model"))
                            ? scope["model"] as Dictionary<string, object> : null;
                        if (model != null) perModel.Add(lim); else overall.Add(lim);
                    }
                }

                var rows = new List<UsageRow>();
                rows.Add(new UsageRow { Label = "5h", Remain = fiveRemain, Reset = fiveReset });

                int weekWorst = 100;
                if (overall.Count > 0 || perModel.Count > 0)
                {
                    foreach (var lim in overall)
                    {
                        int remain = Math.Max(0, 100 - Convert.ToInt32(lim["percent"]));
                        rows.Add(new UsageRow { Label = "週", Remain = remain, Reset = FormatReset(GetStr(lim, "resets_at")) });
                        weekWorst = Math.Min(weekWorst, remain);
                    }
                    foreach (var lim in perModel)
                    {
                        var scope = (Dictionary<string, object>)lim["scope"];
                        var model = (Dictionary<string, object>)scope["model"];
                        string label = GetStr(model, "display_name") ?? "モデル別";
                        int remain = Math.Max(0, 100 - Convert.ToInt32(lim["percent"]));
                        rows.Add(new UsageRow { Label = label, Remain = remain, Reset = FormatReset(GetStr(lim, "resets_at")) });
                        weekWorst = Math.Min(weekWorst, remain);
                    }
                }
                else
                {
                    // limits が無い旧レスポンス向けのフォールバック
                    var seven = (Dictionary<string, object>)json["seven_day"];
                    int remain = Math.Max(0, 100 - (int)Math.Round(Convert.ToDouble(seven["utilization"])));
                    rows.Add(new UsageRow { Label = "週", Remain = remain, Reset = FormatReset(GetStr(seven, "resets_at")) });
                    weekWorst = remain;
                }

                PollLog("OK 5h=" + fiveRemain + "% weekWorst=" + weekWorst + "%");

                var lines = new List<string>();
                foreach (var row in rows)
                {
                    string name = row.Label == "5h" ? "5時間枠"
                        : row.Label == "週" ? "週間 (全体)"
                        : "週間 [" + row.Label + "]";
                    lines.Add(name + " : 残り " + row.Remain + "% (リセット " + row.Reset + ")");
                }
                lines.Add("更新: " + DateTime.Now.ToString("HH:mm:ss"));

                claudeRows = rows;
                claudeFive = fiveRemain;
                claudeWeekWorst = weekWorst;
                claudeFiveReset = fiveReset;
                claudeGoodDetail = string.Join("\n", lines.ToArray());
                claudeError = null;
                lastSuccess = DateTime.Now;
                claudeGate.Succeeded();
            }
            catch (Exception ex)
            {
                DescribeError(ex, ClaudeLoginMenu, out claudeError, out claudeErrorBrief);
                claudeGate.Failed(ex);
                int status = HttpStatus(ex);
                PollLog("ERR status=" + status + " " + ex.Message.Replace("\r", " ").Replace("\n", " "));
                if (status == 0 && !(ex is WebException) && !(ex is RefreshFailedException) && lastRawBody.Length > 0)
                    PollLog("BODY " + (lastRawBody.Length > 2000 ? lastRawBody.Substring(0, 2000) + "..." : lastRawBody));
            }
        }

        void UpdateChatGpt()
        {
            try
            {
                var json = FetchGptUsage();
                var rows = ParseGptRows(json);
                if (rows.Count == 0) throw new Exception("使用量の情報がありません");

                var lines = new List<string>();
                foreach (var row in rows)
                {
                    string name = row.Label == "5h" ? "5時間枠" : row.Label == "週" ? "週間" : row.Label + "枠";
                    lines.Add(name + " : 残り " + row.Remain + "% (リセット " + row.Reset + ")");
                }
                lines.Add("更新: " + DateTime.Now.ToString("HH:mm:ss"));

                gptRows = rows;
                gptPlan = GetStr(json, "plan_type");
                gptGoodDetail = string.Join("\n", lines.ToArray());
                gptError = null;
                gptLastSuccess = DateTime.Now;
                gptGate.Succeeded();
                PollLog("GPT OK " + string.Join(" ", lines.ToArray(), 0, rows.Count));
            }
            catch (Exception ex)
            {
                DescribeError(ex, GptLoginMenu, out gptError, out gptErrorBrief);
                gptGate.Failed(ex);
                int status = HttpStatus(ex);
                PollLog("GPT ERR status=" + status + " " + ex.Message.Replace("\r", " ").Replace("\n", " "));
                if (status == 0 && !(ex is WebException) && !(ex is RefreshFailedException) && gptRawBody.Length > 0)
                    PollLog("GPT BODY " + (gptRawBody.Length > 2000 ? gptRawBody.Substring(0, 2000) + "..." : gptRawBody));
            }
        }

        // 取得結果をウィジェット・トレイ・ツールチップに反映する
        void Render()
        {
            bool claudeOk = claudeError == null;
            bool claudeHas = claudeRows.Count > 0;
            string claudeDetail = claudeOk ? claudeGoodDetail
                : claudeHas ? "[" + claudeError + "]\n最終取得 " + lastSuccess.ToString("HH:mm:ss") + "\n" + claudeGoodDetail
                : claudeError;
            string claudeTray = "Claude 5h: 残" + claudeFive + "% (" + claudeFiveReset + ")\n週: 残" + claudeWeekWorst + "%";

            // トレイアイコンは Claude の値で描く
            if (claudeHas) SetTrayIcon(claudeFive, claudeWeekWorst, false);
            else SetTrayIcon(0, 0, true);

            if (!gptEnabled)
            {
                // Claude だけのときは従来どおりの表示（一度でも取得できていれば前回値を残して警告表示）
                widget.IsError = !claudeHas;
                widget.ErrorMessage = claudeError ?? "";
                widget.IsStale = !claudeOk && claudeHas;
                widget.StaleSince = lastSuccess.ToString("HH:mm");
                if (claudeHas)
                {
                    widget.Rows = claudeRows;
                    widget.FitToRows();
                }
                if (claudeOk) SetTrayText(claudeTray);
                else if (claudeHas) SetTrayText("Claude: " + claudeError);
                else notify.Text = "Claude 使用量: 取得エラー";
                lastDetail = claudeDetail;
            }
            else
            {
                bool gptOk = gptError == null;
                bool gptHas = gptRows.Count > 0;
                string gptDetail = gptOk ? gptGoodDetail
                    : gptHas ? "[" + gptError + "]\n最終取得 " + gptLastSuccess.ToString("HH:mm:ss") + "\n" + gptGoodDetail
                    : gptError;

                // 見出しで区切って Claude → ChatGPT の順に並べる。失敗中は見出しの右に最終取得時刻を出す
                var rows = new List<UsageRow>();
                rows.Add(new UsageRow
                {
                    Kind = RowKind.Header,
                    Label = WithPlan("Claude", claudePlan),
                    Note = !claudeOk && claudeHas ? "! " + lastSuccess.ToString("HH:mm") : null
                });
                if (claudeHas) rows.AddRange(claudeRows);
                else rows.Add(new UsageRow { Kind = RowKind.Message, Label = claudeErrorBrief ?? "取得中…" });
                rows.Add(new UsageRow
                {
                    Kind = RowKind.Header,
                    Label = WithPlan("ChatGPT", gptPlan),
                    Note = !gptOk && gptHas ? "! " + gptLastSuccess.ToString("HH:mm") : null
                });
                if (gptHas) rows.AddRange(gptRows);
                else rows.Add(new UsageRow { Kind = RowKind.Message, Label = gptErrorBrief ?? "取得中…" });

                widget.IsError = false;
                widget.IsStale = false;
                widget.Rows = rows;
                widget.FitToRows();

                var gptParts = new List<string>();
                foreach (var row in gptRows) gptParts.Add(row.Label + ": 残" + row.Remain + "%");
                SetTrayText((claudeOk ? claudeTray : "Claude: " + claudeErrorBrief) + "\n"
                    + (gptOk && gptHas ? "GPT " + string.Join(" ", gptParts.ToArray()) : "GPT: " + (gptErrorBrief ?? "取得中…")));
                lastDetail = "【" + WithPlan("Claude", claudePlan) + "】\n" + claudeDetail
                    + "\n\n【" + WithPlan("ChatGPT", gptPlan) + "】\n" + (gptDetail ?? "取得中…");
            }

            widget.Invalidate();
            widgetTip.SetToolTip(widget, lastDetail);
            if (widget.Visible && widget.AlwaysOnTop) widget.TopMost = true;
        }

        // NotifyIcon.Text は 63 文字までしか設定できない
        void SetTrayText(string text)
        {
            notify.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // 5時間枠の残量を数字で、週間枠の残量を下部のバーで描いたアイコンを作る
        void SetTrayIcon(int fiveRemain, int weekRemain, bool isError)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var center = new StringFormat())
                    {
                        center.Alignment = StringAlignment.Center;
                        center.LineAlignment = StringAlignment.Center;

                        if (isError)
                        {
                            using (var f = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var b = new SolidBrush(Color.Gray))
                                g.DrawString("!", f, b, new RectangleF(0, 0, 32, 32), center);
                        }
                        else
                        {
                            string s = fiveRemain.ToString();
                            using (var f = new Font("Segoe UI", s.Length >= 3 ? 13 : 19, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (var b = new SolidBrush(UiUtil.RemainColor(fiveRemain)))
                                g.DrawString(s, f, b, new RectangleF(0, -2, 32, 26), center);

                            using (var track = new SolidBrush(Color.FromArgb(80, 128, 128, 128)))
                                g.FillRectangle(track, 1, 25, 30, 6);
                            int w = Math.Max(1, (int)Math.Round(30.0 * weekRemain / 100.0));
                            using (var bar = new SolidBrush(UiUtil.RemainColor(weekRemain)))
                                g.FillRectangle(bar, 1, 25, w, 6);
                        }
                    }
                }
                IntPtr h = bmp.GetHicon();
                notify.Icon = Icon.FromHandle(h);
                if (prevHicon != IntPtr.Zero) DestroyIcon(prevHicon);
                prevHicon = h;
            }
        }
    }
}
