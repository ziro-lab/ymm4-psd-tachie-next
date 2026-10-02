param(
 [Parameter(Mandatory=$true)][string]$ValidationRoot,
 [Parameter(Mandatory=$true)][string]$Ymm4Dir,
 [Parameter(Mandatory=$true)][string]$OutputDir,
 [Parameter(Mandatory=$true)][string]$DotnetPath,
 [Parameter(Mandatory=$true)][string]$SyntheticSeedProject,
 [Parameter(Mandatory=$true)][string]$BudgetPreflightProject,
 [int]$TimeoutSeconds=180
)
$ErrorActionPreference='Stop'
function Assert-ChildPath([string]$Target,[string]$Parent) {
 $resolved=[IO.Path]::GetFullPath($Target); $boundary=[IO.Path]::GetFullPath($Parent).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw "Target outside authorized validation directory: $resolved"}
 return $resolved
}
$Ymm4Dir=Assert-ChildPath $Ymm4Dir (Join-Path $ValidationRoot 'ymm4')
$OutputDir=Assert-ChildPath $OutputDir (Join-Path $ValidationRoot 'runs')
$SyntheticSeedProject=Assert-ChildPath $SyntheticSeedProject (Join-Path $ValidationRoot 'runs')
$BudgetPreflightProject=Assert-ChildPath $BudgetPreflightProject (Join-Path $ValidationRoot 'runs')
if([IO.Path]::GetFileName($BudgetPreflightProject) -ne 'selected-budget-preflight.csproj'){throw 'Only the prepared synthetic budget driver is accepted.'}
$exe=Join-Path $Ymm4Dir 'YukkuriMovieMaker.exe'
if(-not(Test-Path -LiteralPath $exe)){throw 'Independent host missing.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne '4.56.1.0'){throw 'This proof is pinned to YMM4 4.56.1.0.'}
if(Get-Process -Name YukkuriMovieMaker -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}){throw 'Target host is already running; do not duplicate or replace its DLLs.'}
if(Test-Path -LiteralPath (Join-Path $OutputDir 'selected-complete.txt')){throw 'Use a new output directory; preserve existing proof.'}
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$pluginRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$proofProject=Join-Path $pluginRoot 'tests/PsdTachieNext.HostProof/PsdTachieNext.HostProof.csproj'
$sourceCheckout=[IO.Path]::GetFullPath($pluginRoot)
$sourceHead=& git -c "safe.directory=$sourceCheckout" -C $sourceCheckout rev-parse HEAD
if($LASTEXITCODE -ne 0){throw 'Source HEAD could not be recorded.'}
$sourceStatus=@(& git -c "safe.directory=$sourceCheckout" -C $sourceCheckout status --short)
$p=$null
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PsdSelectedProofWindows {
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback,IntPtr param);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint process);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder text,int length);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
}
'@
try {
 try {
  Get-CimInstance Win32_OperatingSystem | Select-Object TotalVisibleMemorySize,FreePhysicalMemory |
   ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'system-memory-before.json') -Encoding utf8
 } catch { $_.Exception.Message | Set-Content -LiteralPath (Join-Path $OutputDir 'system-memory-read-error.txt') -Encoding utf8 }
 "BUDGET_PREFLIGHT_START $([DateTime]::UtcNow.ToString('O'))" | Set-Content -LiteralPath (Join-Path $OutputDir 'batch-stages.log') -Encoding utf8
 $budgetOutput=Join-Path $OutputDir 'budget-preflight'
 & $DotnetPath run --project $BudgetPreflightProject -c Release -- $budgetOutput 2>&1 | Out-File -LiteralPath (Join-Path $OutputDir 'budget-preflight.log') -Encoding utf8
 if($LASTEXITCODE -ne 0){throw 'Synthetic budget preflight failed; do not launch host.'}
 "BUILD_START $([DateTime]::UtcNow.ToString('O'))" | Add-Content -LiteralPath (Join-Path $OutputDir 'batch-stages.log') -Encoding utf8
 & $DotnetPath build $proofProject -c Release --no-restore "-p:YMM4DirPath=$Ymm4Dir" 2>&1 | Tee-Object -FilePath (Join-Path $OutputDir 'build.log')
 if($LASTEXITCODE -ne 0){throw 'Build failed; do not launch host.'}
 $destination=Join-Path $Ymm4Dir 'user/plugin/PsdTachieNext'; New-Item -ItemType Directory -Force $destination | Out-Null
 $bin=Join-Path $pluginRoot 'tests/PsdTachieNext.HostProof/bin/Release/net10.0-windows10.0.19041.0'
 $backup=Join-Path $OutputDir 'preinstalled-dlls'; New-Item -ItemType Directory -Force $backup | Out-Null
 foreach($name in @('PsdTachieNext.Core.dll','PsdTachieNext.Compiler.dll','PsdTachieNext.Parser.dll','PsdTachieNext.Ymm4.dll','PsdTachieNext.HostProof.dll')) {
  if(Test-Path -LiteralPath (Join-Path $destination $name)){Copy-Item -LiteralPath (Join-Path $destination $name) -Destination (Join-Path $backup $name)}
  Copy-Item -LiteralPath (Join-Path $bin $name) -Destination (Join-Path $destination $name)
 }
 $seed=Get-Content -LiteralPath $SyntheticSeedProject -Encoding utf8 -Raw | ConvertFrom-Json
 if(@($seed.Characters).Count -ne 1 -or $seed.Characters[0].Name -ne 'PR-A live synthetic' -or @($seed.Timelines).Count -ne 1 -or @($seed.Timelines[0].Items).Count -ne 1){throw 'Only the known single-character synthetic proof fixture is accepted.'}
 $seedSource=Assert-ChildPath $seed.Timelines[0].Items[0].TachieItemParameter.Source.Path (Join-Path $ValidationRoot 'runs')
 if([IO.Path]::GetFileName($seedSource) -ne 'synthetic.psd'){throw 'Synthetic PSD name mismatch.'}
 $synthetic=Join-Path $OutputDir 'synthetic.psd';Copy-Item -LiteralPath $seedSource -Destination $synthetic
 $project=Join-Path $OutputDir 'synthetic-selected.ymmp';$characters=@()
 $none=$seed.Characters[0] | ConvertTo-Json -Depth 100 | ConvertFrom-Json
 $none.Name='PR-A selected synthetic none';$none.TachieType=$null
 $none.TachieCharacterParameter=$null;$none.TachieDefaultItemParameter=$null;$none.TachieDefaultFaceParameter=$null
 $characters+=$none
 foreach($label in @('A','B','unused')) {
  $character=$seed.Characters[0] | ConvertTo-Json -Depth 100 | ConvertFrom-Json
  $character.Name='PR-A selected synthetic '+$label
  $sourcePath=if($label -eq 'A'){$synthetic}elseif($label -eq 'B'){Join-Path $OutputDir 'synthetic-b.psd'}else{Join-Path $OutputDir 'synthetic-unused.psd'}
  $character.TachieDefaultItemParameter.Source.Path=$sourcePath
  $character.TachieDefaultItemParameter.Source.AssetIdentity=[Guid]::NewGuid().ToString('N')
  $characters+=$character
 }
 $seed.Characters=$characters;$seed.Timelines[0].Items=@();$seed.FilePath=$project
 $seed.Timelines[0].MaxLayer=1;$seed.Timelines[0].CurrentFrame=0
 $seed | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $project -Encoding utf8
 Get-ChildItem -LiteralPath $destination -Filter '*.dll' | Get-FileHash | Select-Object Hash,Path | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'installed-dll-hashes.json') -Encoding utf8
 $env:SOURCE_HEAD=$sourceHead;$env:PSD_NEXT_LIVE_PROOF_OUTPUT=$OutputDir;$env:PSD_NEXT_LIVE_PROOF_PROJECT=$project;$env:PSD_NEXT_LIVE_PROOF_SCENARIO='SelectedPrefetch'
 "HOST_LAUNCH $([DateTime]::UtcNow.ToString('O'))" | Add-Content -LiteralPath (Join-Path $OutputDir 'batch-stages.log')
 $p=Start-Process -FilePath $exe -ArgumentList ('"'+$project+'"') -WorkingDirectory $Ymm4Dir -WindowStyle Hidden -PassThru
 [ordered]@{sourceHead=$sourceHead;sourceStatus=$sourceStatus;hostVersion='4.56.1.0';sdk=(& $DotnetPath --version);ownedPid=$p.Id;host=$exe;syntheticProject=$project;scope='selected character prefetch, first native placement, rapid switching, actual priority, real-time expiry and synthetic decoded-budget controls; one host launch';pushed=$false;actionsStarted=$false} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDir 'batch-provenance.json') -Encoding utf8
 $marker=Join-Path $OutputDir 'selected-complete.txt'
 for($i=0;$i -lt $TimeoutSeconds;$i++) {
  if(Test-Path -LiteralPath $marker){break}
  if($p.HasExited){throw 'Owned host exited before proof completion.'}
  $script:selectedProofPid=$p.Id;$script:selectedProofOutput=$OutputDir
  $callback=[PsdSelectedProofWindows+EnumProc]{param([IntPtr]$h,[IntPtr]$v)
   [uint32]$owner=0;[void][PsdSelectedProofWindows]::GetWindowThreadProcessId($h,[ref]$owner)
   if($owner -eq $script:selectedProofPid){
    $title=New-Object Text.StringBuilder 1024;[void][PsdSelectedProofWindows]::GetWindowText($h,$title,1024)
    $text=$title.ToString();if($text){Add-Content -LiteralPath (Join-Path $script:selectedProofOutput 'window-titles.log') -Value $text}
    # Same narrowly scoped startup notices as RunHostCheckpoint; no generic confirmation dismissal.
    if($text -like '*Check for updates*' -or $text -like '*About YukkuriMovieMaker*'){
     [void][PsdSelectedProofWindows]::PostMessage($h,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    }
   };return $true
  }
  [void][PsdSelectedProofWindows]::EnumWindows($callback,[IntPtr]::Zero)
  Start-Sleep -Seconds 1
 }
 if(-not(Test-Path -LiteralPath $marker)){throw 'Proof timed out; no PASS claim.'}
 $result=Get-Content -LiteralPath (Join-Path $OutputDir 'selected-results.json') -Raw | ConvertFrom-Json
 $actual=(Get-FileHash -LiteralPath (Join-Path $destination 'PsdTachieNext.Ymm4.dll')).Hash.ToLowerInvariant()
 if($result.productAssemblySha256 -ne $actual){throw 'Executed product hash differs from installed DLL.'}
 if($result.status -ne 'PASS_REAL_HOST_SELECTED_PREFETCH'){throw "Selected prefetch failed: $($result.error)"}
 Write-Host "SELECTED PREFETCH SUMMARY: $($result.assertions) assertions; $($result.status); compilerCount=$($result.compilerCount); host launches=1."
} catch {
 [ordered]@{status='FAIL_OR_BLOCKED';error=$_.Exception.Message;sourceHead=$sourceHead;
  completedNativeProof=(Test-Path -LiteralPath (Join-Path $OutputDir 'selected-complete.txt'));
  firstPlacementEvidence=(Test-Path -LiteralPath (Join-Path $OutputDir 'first-placement.json'));
  realExpiryEvidence=(Test-Path -LiteralPath (Join-Path $OutputDir 'real-expiry.json'))} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'batch-results.json') -Encoding utf8
 throw
} finally {
 if($null -ne $p){$p.Refresh();[ordered]@{ownedPid=$p.Id;peakWorkingSetBytes=$p.PeakWorkingSet64} |
  ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'owned-host-memory.json') -Encoding utf8}
 if($null -ne $p -and -not $p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}
 "OWNED_HOST_STOPPED $([DateTime]::UtcNow.ToString('O'))" | Add-Content -LiteralPath (Join-Path $OutputDir 'batch-stages.log')
 foreach($name in @('SOURCE_HEAD','PSD_NEXT_LIVE_PROOF_OUTPUT','PSD_NEXT_LIVE_PROOF_PROJECT','PSD_NEXT_LIVE_PROOF_SCENARIO')){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}
}
