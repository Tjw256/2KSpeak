# End-to-end dictation check. Start 2KSpeak with TWOKSPEAK_TEST_AUDIO=<16 kHz WAV> first, then run this.
# It opens its own text box, takes focus, holds Ctrl+Win for $HoldSeconds via injected keys, and saves what
# 2KSpeak typed into the box. It refuses to press anything unless its own box has focus, because 2KSpeak
# types into whatever window is focused.
param([int]$HoldSeconds = 12, [int]$AfterSeconds = 5, [string]$Out = "$env:TEMP\e2e-typed.txt", [string]$Order = "CtrlWin")
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K {
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Explicit, Size=40)] public struct IN { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int size);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr p);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  // Windows refuses focus to background processes; sharing the foreground thread's input state lifts that.
  public static bool Focus(IntPtr h) {
    var fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero); var me = GetCurrentThreadId();
    AttachThreadInput(fg, me, true); BringWindowToTop(h); SetForegroundWindow(h); AttachThreadInput(fg, me, false);
    return GetForegroundWindow() == h;
  }
  public static void Key(ushort vk, bool down) {
    var i = new IN[1]; i[0].type = 1; i[0].ki.vk = vk; i[0].ki.flags = (down ? 0u : 2u) | ((vk==0x5B||vk==0x5C||vk==0xA3) ? 1u : 0u);
    SendInput(1, i, Marshal.SizeOf(typeof(IN)));
  }
}
"@
$form = New-Object System.Windows.Forms.Form -Property @{ Text = "2KSpeak E2E"; Width = 900; Height = 300; TopMost = $true; StartPosition = "CenterScreen" }
$box = New-Object System.Windows.Forms.TextBox -Property @{ Multiline = $true; Dock = "Fill"; Font = New-Object System.Drawing.Font("Segoe UI", 12) }
$form.Controls.Add($box)
$first = if ($Order -eq "CtrlWin") { 0xA2 } else { 0x5B }; $second = if ($Order -eq "CtrlWin") { 0x5B } else { 0xA2 }
$script:step = 0; $script:status = "ok"
$timer = New-Object System.Windows.Forms.Timer -Property @{ Interval = 700 }
$timer.Add_Tick({
  switch ($script:step) {
    0 { $ok = [K]::Focus($form.Handle); $box.Focus() | Out-Null
        if (-not $ok -or -not $box.Focused) { $script:status = "ABORTED: test window not focused"; $form.Close(); return }
        [K]::Key($first, $true); Start-Sleep -Milliseconds 60; [K]::Key($second, $true); $timer.Interval = $HoldSeconds * 1000 }
    1 { if (-not [K]::Focus($form.Handle)) { $script:status = "WARNING: focus moved during hold" }
        [K]::Key($second, $false); Start-Sleep -Milliseconds 60; [K]::Key($first, $false); $timer.Interval = $AfterSeconds * 1000 }
    2 { $timer.Stop(); [IO.File]::WriteAllText($Out, $box.Text, [Text.Encoding]::UTF8); $form.Close() }
  }
  $script:step++
})
$form.Add_Shown({ $timer.Start() })
[System.Windows.Forms.Application]::Run($form)
"status: $script:status; order $Order; held ${HoldSeconds}s"
