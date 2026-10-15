# 截图工具：必须先把当前线程设成 DPI 感知，否则 CopyFromScreen 拿到的是被系统
# 虚拟化（÷1.5）的坐标，截出来的"全屏"其实是左上角的一块。
param(
    [int]$X = 0,
    [int]$Y = 0,
    [int]$W = 2560,
    [int]$H = 1600,
    [string]$Out = "shot.png"
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DpiAware {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
}
"@
[DpiAware]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("saved {0} ({1}x{2} at {3},{4})" -f $Out, $W, $H, $X, $Y)
