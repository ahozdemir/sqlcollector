<#
  Regenerates the screenshots of the Guide (src/SqlDataCollector/wwwroot/guide/*.png) from the running local test
  application (scripts/dev.ps1 run, http://localhost:5080). Headless Chrome signs in with Windows (Negotiate),
  opens each page at 1440 px and the region of interest is cropped – never the top bar (user name), the menu footer
  (machine name) or server names, because the images are part of the repository.

  Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/guide-screenshots.ps1 [-BaseUrl http://localhost:5080] [-Only transfer,runs]
  The regions are CSS pixels at 1440 px width (measured with the browser's getBoundingClientRect); adjust them when a page layout changes.
#>
param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [string[]]$Only = @()   # names of the screenshots to refresh (default: all)
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "src\SqlDataCollector\wwwroot\guide"
$work = Join-Path ([IO.Path]::GetTempPath()) "sdc-guide-shots"
New-Item -ItemType Directory -Force -Path $outDir, $work | Out-Null

# name, page, window height, crop x, y, width, height, signed in
$shots = @(
    @{ Name = "login";            Page = "/login";          H = 700;  X = 460; Y = 100; W = 520;  Ht = 300; Auth = $false },
    @{ Name = "status-overview";  Page = "/";               H = 1000; X = 256; Y = 64;  W = 1168; Ht = 210; Auth = $true },
    @{ Name = "status-jobs";      Page = "/";               H = 1100; X = 256; Y = 440; W = 1168; Ht = 650; Auth = $true },
    @{ Name = "extract-source";   Page = "/extract/3";      H = 2400; X = 262; Y = 141; W = 568;  Ht = 700; Auth = $true },
    @{ Name = "extract-target";   Page = "/extract/3";      H = 2400; X = 834; Y = 141; W = 568;  Ht = 715; Auth = $true },
    @{ Name = "extract-trigger";  Page = "/extract/3";      H = 2400; X = 834; Y = 860; W = 568;  Ht = 368; Auth = $true },
    @{ Name = "extract-quality";  Page = "/extract/3";      H = 2400; X = 834; Y = 1234; W = 568; Ht = 572; Auth = $true },
    @{ Name = "merge-load";       Page = "/extract/9";      H = 2400; X = 834; Y = 586; W = 568;  Ht = 489; Auth = $true },
    @{ Name = "export-format";    Page = "/export/1";       H = 1600; X = 834; Y = 339; W = 568;  Ht = 351; Auth = $true },
    @{ Name = "export-trigger";   Page = "/export/1";       H = 1600; X = 834; Y = 694; W = 568;  Ht = 614; Auth = $true },
    @{ Name = "privacy";          Page = "/admin/privacy";  H = 1000; X = 256; Y = 64;  W = 1168; Ht = 500; Auth = $true },
    @{ Name = "quality-page";     Page = "/quality";        H = 1000; X = 256; Y = 64;  W = 1168; Ht = 480; Auth = $true },
    @{ Name = "runs";             Page = "/runs";           H = 1000; X = 256; Y = 64;  W = 1168; Ht = 560; Auth = $true },
    @{ Name = "alerts";           Page = "/admin/alerts";   H = 1000; X = 256; Y = 64;  W = 1168; Ht = 560; Auth = $true },
    @{ Name = "destinations";     Page = "/admin/destinations"; H = 1000; X = 256; Y = 64; W = 1168; Ht = 400; Auth = $true },
    @{ Name = "transfer";         Page = "/transfer";       H = 1000; X = 256; Y = 64;  W = 1168; Ht = 900; Auth = $true },
    @{ Name = "settings-gcs";     Page = "/admin/settings"; H = 1400; X = 262; Y = 728; W = 1141; Ht = 261; Auth = $true }
)

$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })   # "-Only a,b" arrives as one string with -File
if ($Only.Count -gt 0) { $shots = @($shots | Where-Object { $Only -contains $_.Name }) }
foreach ($s in $shots) {
    $raw = Join-Path $work "$($s.Name)-raw.png"
    Remove-Item $raw -ErrorAction SilentlyContinue
    $url = if ($s.Auth) { "$BaseUrl/auth/windows?returnUrl=" + [Uri]::EscapeDataString($s.Page) } else { "$BaseUrl$($s.Page)" }
    $profile = Join-Path $work ($(if ($s.Auth) { "profile-auth" } else { "profile-anon" }))
    $args = @("--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
              "--auth-server-allowlist=localhost", "--window-size=1440,$($s.H)", "--virtual-time-budget=10000",
              "--user-data-dir=$profile", "--screenshot=$raw", $url)
    $p = Start-Process -FilePath $Chrome -ArgumentList $args -PassThru -Wait -WindowStyle Hidden
    if (-not (Test-Path $raw)) { throw "Chrome produced no screenshot for $($s.Name) (exit $($p.ExitCode))." }

    $src = [System.Drawing.Image]::FromFile($raw)
    try {
        $rect = New-Object System.Drawing.Rectangle $s.X, $s.Y, ([Math]::Min($s.W, $src.Width - $s.X)), ([Math]::Min($s.Ht, $src.Height - $s.Y))
        $bmp = New-Object System.Drawing.Bitmap $rect.Width, $rect.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $rect.Width, $rect.Height), $rect, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        $out = Join-Path $outDir "$($s.Name).png"
        $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host ("{0,-18} {1,5}x{2,-5} {3,8:N0} bytes" -f $s.Name, $rect.Width, $rect.Height, (Get-Item $out).Length)
    }
    finally { $src.Dispose() }
}
Write-Host "Screenshots written to $outDir"
