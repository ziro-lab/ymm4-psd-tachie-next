param([Parameter(Mandatory=$true)][string]$Ymm4Dir,[Parameter(Mandatory=$true)][string]$OutputDir,[switch]$LiveRefresh,[string]$ProjectPath,[int]$TimeoutSeconds=120)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$marker=Join-Path $OutputDir $(if($LiveRefresh){'live-complete.txt'}else{'host-complete.txt'})
Remove-Item $marker -Force -ErrorAction SilentlyContinue
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PsdProofWindows {
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback,IntPtr param);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint process);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder text,int length);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
}
'@
if($LiveRefresh){$env:PSD_NEXT_LIVE_PROOF_OUTPUT=$OutputDir}else{$env:PSD_NEXT_HOST_PROOF_OUTPUT=$OutputDir}
if($LiveRefresh -and $ProjectPath){
 if(-not(Test-Path -LiteralPath $ProjectPath)){throw 'Synthetic project missing.'}
 $env:PSD_NEXT_LIVE_PROOF_PROJECT=$ProjectPath
 $p=Start-Process -FilePath (Join-Path $Ymm4Dir 'YukkuriMovieMaker.exe') -ArgumentList $ProjectPath -WorkingDirectory $Ymm4Dir -WindowStyle Hidden -PassThru
}else{$p=Start-Process -FilePath (Join-Path $Ymm4Dir 'YukkuriMovieMaker.exe') -WorkingDirectory $Ymm4Dir -WindowStyle Hidden -PassThru}
try {
 for($i=0;$i -lt $TimeoutSeconds;$i++) {
  if(Test-Path $marker){break}
  if($p.HasExited){throw 'Isolated host exited before completing the checkpoint.'}
  $script:proofPid=$p.Id
  $callback=[PsdProofWindows+EnumProc]{param([IntPtr]$h,[IntPtr]$v)
   [uint32]$owner=0;[void][PsdProofWindows]::GetWindowThreadProcessId($h,[ref]$owner)
   if($owner -eq $script:proofPid){
    $title=New-Object Text.StringBuilder 1024;[void][PsdProofWindows]::GetWindowText($h,$title,1024)
    $text=$title.ToString();if($text){Add-Content -LiteralPath (Join-Path $OutputDir 'window-titles.log') -Value $text}
    if($title.ToString() -like '*Check for updates*' -or $title.ToString() -like '*About YukkuriMovieMaker*'){
     [void][PsdProofWindows]::PostMessage($h,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    }
   };return $true
  }
  [void][PsdProofWindows]::EnumWindows($callback,[IntPtr]::Zero)
  Start-Sleep -Seconds 1
 }
 if(-not(Test-Path $marker)){throw 'No completed native callback evidence; do not interpret as a product PASS.'}
 if($LiveRefresh){
  $result=Get-Content -Raw (Join-Path $OutputDir 'live-results.json') | ConvertFrom-Json
  $actual=(Get-FileHash (Join-Path $Ymm4Dir 'user/plugin/PsdTachieNext/PsdTachieNext.Ymm4.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
  if($result.productAssemblySha256 -ne $actual){throw 'Executed live product assembly does not match installed DLL.'}
  if($result.status -ne 'PASS_REAL_PLAYER_ASYNC_REFRESH'){throw "Live refresh failed: $($result.error)"}
  Write-Host "LIVE REFRESH SUMMARY: $($result.assertions) assertions; $($result.status); previewPixelsVerified=$($result.details.previewPixelsVerified); audio/dirty flag NOT verified."
  return
 }
 $result=Get-Content -Raw (Join-Path $OutputDir 'host-results.json') | ConvertFrom-Json
 $result.results | ForEach-Object {Write-Host "$($_.status) $($_.name)";if($_.error){Write-Host $_.error}}
 $actual=(Get-FileHash (Join-Path $Ymm4Dir 'user/plugin/PsdTachieNext/PsdTachieNext.Ymm4.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
 if($result.productAssemblySha256 -ne $actual){throw 'Executed product assembly does not match installed DLL.'}
 if($result.status -ne 'PASS'){throw "Native checkpoint failed: $($result.failures) failures"}
 Write-Host "HOST CALLBACK SUMMARY: $($result.total) cases; $($result.assertions) assertions; 0 failures; timeline UI NOT verified."
} finally {
 if(-not $p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}
 Remove-Item Env:PSD_NEXT_HOST_PROOF_OUTPUT -ErrorAction SilentlyContinue
 Remove-Item Env:PSD_NEXT_LIVE_PROOF_OUTPUT -ErrorAction SilentlyContinue
 Remove-Item Env:PSD_NEXT_LIVE_PROOF_PROJECT -ErrorAction SilentlyContinue
}
