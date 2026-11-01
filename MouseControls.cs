// マウスのサイドボタンを、前面アプリごとに別のキーへ割り当てる常駐アプリ。
// 設定は exe と同じフォルダの appsettings.json。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
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

    // 前面ウィンドウの切り替え通知
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    const uint EVENT_SYSTEM_FOREGROUND = 3;
    static WinEventProc foregroundProc = OnForegroundChanged; // GCで消えないよう保持

    // プロセスの権限（管理者かどうか）を調べる
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int len, out int retLen);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint TOKEN_QUERY = 0x8;
    const int TokenElevation = 20;

    static readonly bool IsSelfElevated =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    const string TaskName = "MouseControls";

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
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "appsettings.json"); }
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "MouseControls_SingleInstance", out created))
        {
            // 「管理者として再起動」で起動されたときは、前のインスタンスが終わるのを待つ
            if (!created && Array.IndexOf(args, "--restart") >= 0)
            {
                try { created = mutex.WaitOne(10000); }
                catch (AbandonedMutexException) { created = true; }
            }
            if (!created) return;

            SetProcessDPIAware(); // 高DPIでもアイコンやメニューがぼやけないように
            StartLogWriter();
            Log("起動: " + Application.ExecutablePath + "  権限: " + (IsSelfElevated ? "管理者" : "通常"));
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
            if (!IsSelfElevated)
                menu.Items.Add("管理者として再起動", null, delegate { RestartAsAdmin(); });
            menu.Items.Add("終了", null, delegate { Application.Exit(); });

            Icon icon;
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("app.ico"))
                icon = new Icon(s, SystemInformation.SmallIconSize);
            var tray = new NotifyIcon { Icon = icon, Text = AppName, ContextMenuStrip = menu, Visible = true };

            hookHandle = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
            Log("マウスフック: " + (hookHandle != IntPtr.Zero ? "OK" : "失敗 error=" + Marshal.GetLastWin32Error()));
            keyHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, keyHookProc, GetModuleHandle(null), 0);
            Log("キーボードフック: " + (keyHookHandle != IntPtr.Zero ? "OK" : "失敗 error=" + Marshal.GetLastWin32Error()));
            IntPtr fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundProc, 0, 0, 0);
            Application.Run();

            UnhookWinEvent(fgHook);
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

    // 設定ファイルに両ボタン "Default" のアプリを足し、キーを書いてもらうためにメモ帳で開く
    static void AddApp(string name)
    {
        try
        {
            var apps = ReadConfigFile();
            apps[name] = new[] { "Default", "Default" };
            WriteConfigFile(apps);
        }
        catch (Exception ex)
        {
            MessageBox.Show("設定ファイルを読み込めないため追加できませんでした:\n" + ex.Message, AppName);
            return;
        }
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

    const string ConfigHeader =
        "// サイドボタンの割り当て（XButton1 = 戻る / XButton2 = 進む）\r\n" +
        "// アプリ名はプロセス名（.exe は省略可）。ここにないアプリは通常の動作のまま\r\n" +
        "// キー名: Shift / Ctrl / Alt / Esc / Enter / Tab / A / F5 など（.NET の Keys 名）。\"Default\" は通常の動作\r\n" +
        "// 編集後はトレイアイコンの右クリック→「設定を再読み込み」\r\n";

    // "Apps" を { アプリ名: [XButton1, XButton2] } の形で読む。// で始まる行はコメントとして無視する
    static Dictionary<string, string[]> ReadConfigFile()
    {
        var json = new StringBuilder();
        foreach (var line in File.ReadAllLines(ConfigPath, Encoding.UTF8))
            if (!line.TrimStart().StartsWith("//")) json.AppendLine(line);

        var root = new JavaScriptSerializer().DeserializeObject(json.ToString()) as Dictionary<string, object>;
        object appsObj;
        if (root == null || !root.TryGetValue("Apps", out appsObj) || !(appsObj is Dictionary<string, object>))
            throw new FormatException("\"Apps\": { ... } が見つかりません");

        var result = new Dictionary<string, string[]>();
        foreach (var pair in (Dictionary<string, object>)appsObj)
        {
            var obj = pair.Value as Dictionary<string, object>;
            if (obj == null) throw new FormatException("\"" + pair.Key + "\" の値が { ... } になっていません");
            var buttons = new Dictionary<string, object>(obj, StringComparer.OrdinalIgnoreCase);
            result[pair.Key] = new[] { GetString(buttons, "XButton1"), GetString(buttons, "XButton2") };
        }
        return result;
    }

    static string GetString(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) && v != null ? v.ToString() : "Default";
    }

    static void WriteConfigFile(Dictionary<string, string[]> apps)
    {
        var js = new JavaScriptSerializer();
        var sb = new StringBuilder(ConfigHeader);
        sb.Append("{\r\n  \"Apps\": {\r\n");
        int i = 0;
        foreach (var pair in apps)
        {
            sb.Append("    " + js.Serialize(pair.Key) + ": { \"XButton1\": " + js.Serialize(pair.Value[0]) +
                ", \"XButton2\": " + js.Serialize(pair.Value[1]) + " }");
            sb.Append(++i < apps.Count ? ",\r\n" : "\r\n");
        }
        sb.Append("  }\r\n}\r\n");
        File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
    }

    static void LoadConfig()
    {
        // appsettings.json がなければ appsettings.example.json をコピーし、それもなければ既定の内容で作る
        string examplePath = Path.Combine(Path.GetDirectoryName(ConfigPath), "appsettings.example.json");
        if (!File.Exists(ConfigPath) && File.Exists(examplePath))
            File.Copy(examplePath, ConfigPath);
        else if (!File.Exists(ConfigPath))
            WriteConfigFile(new Dictionary<string, string[]> { { "chrome", new[] { "Esc", "Shift" } } });

        Dictionary<string, string[]> apps;
        try { apps = ReadConfigFile(); }
        catch (Exception ex)
        {
            Log("設定の読み込みに失敗: " + ex.Message);
            MessageBox.Show("設定ファイルを読み込めませんでした（前の設定のまま動きます）:\n" + ex.Message, AppName);
            return;
        }

        var result = new Dictionary<string, Keys[]>();
        var errors = new List<string>();
        foreach (var pair in apps)
        {
            string name = pair.Key.ToLowerInvariant();
            if (name.EndsWith(".exe")) name = name.Substring(0, name.Length - 4);
            Keys k1, k2;
            if (!ParseKey(pair.Value[0], out k1) || !ParseKey(pair.Value[1], out k2))
            {
                errors.Add(pair.Key + ": XButton1=" + pair.Value[0] + ", XButton2=" + pair.Value[1]);
                continue;
            }
            result[name] = new[] { k1, k2 };
        }
        mappings = result;

        Log("設定読み込み: " + ConfigPath);
        foreach (var pair in result)
            Log("  " + pair.Key + "  XButton1=" + pair.Value[0] + "  XButton2=" + pair.Value[1]);
        foreach (var e in errors) Log("  キー名が不正: " + e);

        if (errors.Count > 0)
            MessageBox.Show("キー名が不正なアプリ:\n" + string.Join("\n", errors), AppName);
    }

    static bool ParseKey(string s, out Keys key)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "default": case "-": case "": key = Keys.None; return true;
            case "shift": key = Keys.LShiftKey; return true;
            case "ctrl": case "control": key = Keys.LControlKey; return true;
            case "alt": key = Keys.LMenu; return true;
            case "esc": key = Keys.Escape; return true;
            case "win": key = Keys.LWin; return true;
        }
        return Enum.TryParse(s, true, out key) && key != Keys.None && (int)key <= 0xFF;
    }

    // 前面のアプリが変わるたびに、名前と権限をログに出す
    static void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        string name;
        try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName.ToLowerInvariant(); }
        catch { name = "?"; }
        bool elevated = IsProcessElevated(pid);
        Log("前面のアプリ: " + name + (elevated ? " (管理者)" : "") +
            (elevated && !IsSelfElevated ? " ← このアプリが通常権限のため、ここではマウス入力を受け取れません" : ""));
    }

    static bool IsProcessElevated(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            IntPtr token;
            if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return false;
            try { int value, len; return GetTokenInformation(token, TokenElevation, out value, 4, out len) && value != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    static void RestartAsAdmin()
    {
        try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--restart") { Verb = "runas", UseShellExecute = true }); }
        catch (Win32Exception) { return; } // UAC でキャンセルされた
        Application.Exit();
    }

    // 自動起動はタスクスケジューラで「最高の特権」で登録する（管理者権限のアプリでも効くように）。
    // レジストリの Run は管理者権限で起動できないため使わない
    static bool IsStartupEnabled()
    {
        var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN " + TaskName)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode == 0; }
    }

    static void SetStartup(bool enable)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), "MouseControls_task.xml");
        string args = "/Delete /TN " + TaskName + " /F";
        if (enable)
        {
            File.WriteAllText(xmlPath, TaskXml(), Encoding.Unicode);
            args = "/Create /TN " + TaskName + " /XML \"" + xmlPath + "\" /F";
        }
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                Log("自動起動の" + (enable ? "登録" : "解除") + ": schtasks 終了コード " + p.ExitCode);
            }
        }
        catch (Win32Exception) { Log("自動起動の変更をキャンセル"); }
        finally { if (File.Exists(xmlPath)) File.Delete(xmlPath); }

        // 以前のレジストリ方式の登録が残っていれば消す（通常権限で二重に起動してしまうため）
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (k != null) k.DeleteValue(AppName, false);
    }

    // ログオン時に最高の特権で起動。電源や実行時間の制限は外し、優先度は通常(4)にする
    static string TaskXml()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(Application.ExecutablePath);
        string dir = SecurityElement.Escape(Path.GetDirectoryName(Application.ExecutablePath));
        return
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\r\n" +
            "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType>" +
            "<RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            "  <Settings>\r\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
            "    <Priority>4</Priority>\r\n" +
            "  </Settings>\r\n" +
            "  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><WorkingDirectory>" + dir + "</WorkingDirectory></Exec></Actions>\r\n" +
            "</Task>\r\n";
    }
}
