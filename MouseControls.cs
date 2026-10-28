// マウスのサイドボタンを、前面アプリごとに別のキーへ割り当てる常駐アプリ。
// 設定は exe と同じフォルダの config.txt。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    const int WH_MOUSE_LL = 14;
    const int WM_XBUTTONDOWN = 0x020B;
    const int WM_XBUTTONUP = 0x020C;
    const uint LLMHF_INJECTED = 0x1;
    const uint KEYEVENTF_KEYUP = 0x2;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "MouseControls";

    const int WH_KEYBOARD_LL = 13;

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    static HookProc hookProc = HookCallback; // GCで消えないよう保持
    static HookProc keyHookProc = KeyHookCallback;
    static IntPtr hookHandle, keyHookHandle;

    // ログはフックを遅くしないよう別スレッドで書き込む
    static BlockingCollection<string> logQueue = new BlockingCollection<string>();

    static string LogPath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "log.txt"); }
    }

    static void Log(string text)
    {
        logQueue.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text);
    }

    static void StartLogWriter()
    {
        // 起動ごとに空にし、1MB を超えたら log.old.txt に回して書き直す（最大でも約2MB）
        var t = new Thread(delegate ()
        {
            string oldPath = Path.Combine(Path.GetDirectoryName(LogPath), "log.old.txt");
            var w = new StreamWriter(LogPath, false, Encoding.UTF8) { AutoFlush = true };
            foreach (var line in logQueue.GetConsumingEnumerable())
            {
                w.WriteLine(line);
                if (w.BaseStream.Length > 1024 * 1024)
                {
                    w.Dispose();
                    try { File.Copy(LogPath, oldPath, true); } catch { }
                    w = new StreamWriter(LogPath, false, Encoding.UTF8) { AutoFlush = true };
                }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // プロセス名 -> [サイドボタン1のキー, サイドボタン2のキー]（0 = デフォルト動作）
    static Dictionary<string, Keys[]> mappings = new Dictionary<string, Keys[]>();
    // 押下中に送ったキー（離したときに同じキーを離すため）
    static Keys[] pressed = new Keys[2];

    static string ConfigPath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "config.txt"); }
    }

    [STAThread]
    static void Main()
    {
        bool created;
        using (new Mutex(true, "MouseControls_SingleInstance", out created))
        {
            if (!created) return;

            SetProcessDPIAware(); // 高DPIでもアイコンやメニューがぼやけないように
            StartLogWriter();
            Log("起動: " + Application.ExecutablePath);
            LoadConfig();

            var startup = new ToolStripMenuItem("Windows起動時に実行") { Checked = IsStartupEnabled() };
            startup.Click += delegate { SetStartup(!startup.Checked); startup.Checked = IsStartupEnabled(); };

            var addApp = new ToolStripMenuItem("開いているアプリを追加");
            addApp.DropDownItems.Add("(読み込み中)"); // サブメニューの矢印を出すためのダミー
            addApp.DropDownOpening += delegate { FillAppList(addApp); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(addApp);
            menu.Items.Add("設定ファイルを開く", null, delegate { Process.Start("notepad.exe", "\"" + ConfigPath + "\""); });
            menu.Items.Add("設定を再読み込み", null, delegate { LoadConfig(); });
            menu.Items.Add("ログを開く", null, delegate { Process.Start("notepad.exe", "\"" + LogPath + "\""); });
            menu.Items.Add(startup);
            menu.Items.Add("終了", null, delegate { Application.Exit(); });

            Icon icon;
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("app.ico"))
                icon = new Icon(s, SystemInformation.SmallIconSize);
            var tray = new NotifyIcon { Icon = icon, Text = AppName, ContextMenuStrip = menu, Visible = true };

            hookHandle = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
            Log("マウスフック: " + (hookHandle != IntPtr.Zero ? "OK" : "失敗 error=" + Marshal.GetLastWin32Error()));
            keyHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, keyHookProc, GetModuleHandle(null), 0);
            Log("キーボードフック: " + (keyHookHandle != IntPtr.Zero ? "OK" : "失敗 error=" + Marshal.GetLastWin32Error()));
            Application.Run();

            UnhookWindowsHookEx(hookHandle);
            UnhookWindowsHookEx(keyHookHandle);
            tray.Visible = false;
        }
    }

    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        // 移動(0x200)以外のボタン操作をログに出す。ホイールはShift押下中だけ
        bool shiftDown = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        if (nCode >= 0 && msg >= 0x201 && msg <= 0x20E && (msg != 0x20A && msg != 0x20E || shiftDown))
        {
            var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
            bool injected = (info.flags & LLMHF_INJECTED) != 0;

            if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
            {
                int index = (int)(info.mouseData >> 16) - 1; // XBUTTON1 = 0, XBUTTON2 = 1
                string proc = ForegroundProcessName();
                string head = (msg == WM_XBUTTONDOWN ? "サイドボタン押下" : "サイドボタン離す") +
                    " button=" + (index + 1) + " app=" + proc + (injected ? " (合成イベント)" : "");

                string reason = "そのまま通す";
                if (!injected && (index == 0 || index == 1))
                {
                    if (msg == WM_XBUTTONDOWN)
                    {
                        Keys[] keys;
                        if (!mappings.TryGetValue(proc, out keys)) { reason = "設定なし、デフォルト動作"; }
                        else if (keys[index] == Keys.None) { reason = "\"-\" なのでデフォルト動作"; }
                        else
                        {
                            pressed[index] = keys[index];
                            SendKey(keys[index], false);
                            Log(head + " → " + keys[index] + " を押下");
                            return (IntPtr)1; // 元のクリックは握りつぶす
                        }
                    }
                    else if (pressed[index] != Keys.None)
                    {
                        Keys k = pressed[index];
                        SendKey(k, true);
                        pressed[index] = Keys.None;
                        Log(head + " → " + k + " を離す");
                        return (IntPtr)1;
                    }
                }
                Log(head + " → " + reason);
            }
            else
            {
                Log("マウス " + MouseMessageName(msg) + " app=" + ForegroundProcessName() +
                    (shiftDown ? " [Shift押下中]" : "") + (injected ? " (合成イベント)" : ""));
            }
        }
        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    // マウス側のソフトがサイドボタンを「ブラウザの戻る/進む」キーに変換していないか確認する
    static IntPtr KeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        if (nCode >= 0)
        {
            var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            var k = (Keys)info.vkCode;
            bool down = msg == 0x100 || msg == 0x104; // WM_KEYDOWN / WM_SYSKEYDOWN
            // 送ったShift/Escが届いているかと、マウス側のソフトによるブラウザキー変換を確認する
            if (k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.ShiftKey || k == Keys.Escape ||
                k == Keys.BrowserBack || k == Keys.BrowserForward)
                Log("キー " + k + (down ? " 押下" : " 離す") + " app=" + ForegroundProcessName() +
                    ((info.flags & 0x10) != 0 ? " (合成イベント)" : ""));
        }
        return CallNextHookEx(keyHookHandle, nCode, wParam, lParam);
    }

    static string MouseMessageName(int msg)
    {
        switch (msg)
        {
            case 0x201: return "左押下";
            case 0x202: return "左離す";
            case 0x204: return "右押下";
            case 0x205: return "右離す";
            case 0x207: return "中押下";
            case 0x208: return "中離す";
            case 0x20A: return "ホイール";
            case 0x20E: return "横ホイール";
            default: return "msg=0x" + msg.ToString("X");
        }
    }

    // ウィンドウを持つプロセスを一覧にする。設定済みのものはチェック付きで選べない
    static void FillAppList(ToolStripMenuItem parent)
    {
        var apps = new SortedDictionary<string, string>(); // プロセス名 -> ウィンドウタイトル
        int self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != self && p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0)
                {
                    string name = p.ProcessName.ToLowerInvariant();
                    if (!apps.ContainsKey(name)) apps[name] = p.MainWindowTitle;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        parent.DropDownItems.Clear();
        foreach (var pair in apps)
        {
            string name = pair.Key;
            string title = pair.Value.Length > 40 ? pair.Value.Substring(0, 40) + "…" : pair.Value;
            var item = new ToolStripMenuItem(name + "    " + title);
            if (mappings.ContainsKey(name)) { item.Checked = true; item.Enabled = false; }
            else item.Click += delegate { AddApp(name); };
            parent.DropDownItems.Add(item);
        }
    }

    // 設定ファイルに「プロセス名 - -」の行を足し、キーを書いてもらうためにメモ帳で開く
    static void AddApp(string name)
    {
        string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
        if (text.Length > 0 && !text.EndsWith("\n")) text += "\r\n";
        File.WriteAllText(ConfigPath, text + name + "  -  -\r\n", Encoding.UTF8);
        Log("アプリを追加: " + name);
        LoadConfig();
        Process.Start("notepad.exe", "\"" + ConfigPath + "\"");
    }

    static void SendKey(Keys key, bool up)
    {
        byte vk = (byte)key;
        keybd_event(vk, (byte)MapVirtualKey(vk, 0), up ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
    }

    static string ForegroundProcessName()
    {
        try
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            using (var p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant();
        }
        catch { return ""; }
    }

    static void LoadConfig()
    {
        // config.txt がなければ config.example.txt をコピーし、それもなければ既定の内容で作る
        string examplePath = Path.Combine(Path.GetDirectoryName(ConfigPath), "config.example.txt");
        if (!File.Exists(ConfigPath) && File.Exists(examplePath))
        {
            File.Copy(examplePath, ConfigPath);
        }
        else if (!File.Exists(ConfigPath))
        {
            File.WriteAllText(ConfigPath,
                "# 書式: プロセス名  サイドボタン1(戻る)  サイドボタン2(進む)\r\n" +
                "# キー名: Shift / Ctrl / Alt / Esc / Enter / Tab / A / F5 など（.NET の Keys 名）\r\n" +
                "# \"-\" はデフォルト動作のまま。ここに書いていないアプリもデフォルト動作。\r\n" +
                "# 編集後はトレイアイコンの右クリック→「設定を再読み込み」\r\n" +
                "chrome  Esc  Shift\r\n", Encoding.UTF8);
        }

        var result = new Dictionary<string, Keys[]>();
        var errors = new List<string>();
        foreach (var raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) { errors.Add(line); continue; }

            string name = parts[0].ToLowerInvariant();
            if (name.EndsWith(".exe")) name = name.Substring(0, name.Length - 4);
            Keys k1, k2;
            if (!ParseKey(parts[1], out k1) || !ParseKey(parts[2], out k2)) { errors.Add(line); continue; }
            result[name] = new[] { k1, k2 };
        }
        mappings = result;

        Log("設定読み込み: " + ConfigPath);
        foreach (var pair in result)
            Log("  " + pair.Key + "  ボタン1=" + pair.Value[0] + "  ボタン2=" + pair.Value[1]);
        foreach (var e in errors) Log("  読み込めなかった行: " + e);

        if (errors.Count > 0)
            MessageBox.Show("読み込めなかった行:\n" + string.Join("\n", errors), AppName);
    }

    static bool ParseKey(string s, out Keys key)
    {
        switch (s.ToLowerInvariant())
        {
            case "-": key = Keys.None; return true;
            case "shift": key = Keys.LShiftKey; return true;
            case "ctrl": case "control": key = Keys.LControlKey; return true;
            case "alt": key = Keys.LMenu; return true;
            case "esc": key = Keys.Escape; return true;
            case "win": key = Keys.LWin; return true;
        }
        return Enum.TryParse(s, true, out key) && key != Keys.None && (int)key <= 0xFF;
    }

    static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null;
    }

    static void SetStartup(bool enable)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enable) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(AppName, false);
        }
    }
}
