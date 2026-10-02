param(
 [Parameter(Mandatory=$true)][string]$ValidationRoot,
 [Parameter(Mandatory=$true)][string]$Ymm4Dir,
 [Parameter(Mandatory=$true)][string]$OutputDir,
 [Parameter(Mandatory=$true)][string]$DotnetPath,
 [Parameter(Mandatory=$true)][string]$SyntheticSeedProject,
 [int]$TimeoutSeconds=240
)
$ErrorActionPreference='Stop'
function Assert-ChildPath([string]$Target,[string]$Parent) {
 $resolved=[IO.Path]::GetFullPath($Target);$boundary=[IO.Path]::GetFullPath($Parent).TrimEnd('\')+'\'
 if(-not $resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw "Outside validation boundary: $resolved"}
 return $resolved
}
$Ymm4Dir=Assert-ChildPath $Ymm4Dir (Join-Path $ValidationRoot 'ymm4')
$OutputDir=Assert-ChildPath $OutputDir (Join-Path $ValidationRoot 'runs')
$SyntheticSeedProject=Assert-ChildPath $SyntheticSeedProject (Join-Path $ValidationRoot 'runs')
$exe=Join-Path $Ymm4Dir 'YukkuriMovieMaker.exe'
if(-not(Test-Path -LiteralPath $exe)){throw 'Independent host missing.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne '4.56.1.0'){throw 'Proof pinned to YMM4 4.56.1.0.'}
if(Get-Process -Name YukkuriMovieMaker -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}){throw 'Owned target already running.'}
if(Test-Path -LiteralPath $OutputDir){throw 'Use a fresh output directory; preserve existing evidence.'}
New-Item -ItemType Directory $OutputDir | Out-Null
$pluginRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceCheckout=[IO.Path]::GetFullPath($pluginRoot)
$sourceHead=& git -c "safe.directory=$sourceCheckout" -C $sourceCheckout rev-parse HEAD
if($LASTEXITCODE -ne 0){throw 'Cannot record source HEAD.'}
$sourceStatus=@(& git -c "safe.directory=$sourceCheckout" -C $sourceCheckout status --short)
if($sourceStatus.Count -ne 0){throw 'Commit the reviewable source before host execution.'}
$p=$null
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PsdNormalExportWindows {
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback,IntPtr param);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint process);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder text,int length);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
}
'@
try {
 "BUILD_START $([DateTime]::UtcNow.ToString('O'))" | Set-Content -LiteralPath (Join-Path $OutputDir 'batch-stages.log') -Encoding utf8
 $proofProject=Join-Path $pluginRoot 'tests/PsdTachieNext.HostProof/PsdTachieNext.HostProof.csproj'
 & $DotnetPath build $proofProject -c Release --no-restore "-p:YMM4DirPath=$Ymm4Dir" 2>&1 | Tee-Object -FilePath (Join-Path $OutputDir 'build.log')
 if($LASTEXITCODE -ne 0){throw 'Build failed; do not launch host.'}
 $destination=Join-Path $Ymm4Dir 'user/plugin/PsdTachieNext';New-Item -ItemType Directory -Force $destination | Out-Null
 $bin=Join-Path $pluginRoot 'tests/PsdTachieNext.HostProof/bin/Release/net10.0-windows10.0.19041.0'
 $backup=Join-Path $OutputDir 'preinstalled-dlls';New-Item -ItemType Directory $backup | Out-Null
 foreach($name in @('PsdTachieNext.Core.dll','PsdTachieNext.Compiler.dll','PsdTachieNext.Parser.dll','PsdTachieNext.Ymm4.dll','PsdTachieNext.HostProof.dll')) {
  if(Test-Path -LiteralPath (Join-Path $destination $name)){Copy-Item -LiteralPath (Join-Path $destination $name) -Destination (Join-Path $backup $name)}
  Copy-Item -LiteralPath (Join-Path $bin $name) -Destination (Join-Path $destination $name)
 }
 $seed=Get-Content -LiteralPath $SyntheticSeedProject -Encoding utf8 -Raw | ConvertFrom-Json
 if(@($seed.Characters).Count -ne 1 -or $seed.Characters[0].Name -ne 'PR-A live synthetic' -or @($seed.Timelines).Count -ne 1 -or @($seed.Timelines[0].Items).Count -ne 1){throw 'Known synthetic fixture required.'}
 $source=Assert-ChildPath $seed.Timelines[0].Items[0].TachieItemParameter.Source.Path (Join-Path $ValidationRoot 'runs')
 if([IO.Path]::GetFileName($source) -ne 'synthetic.psd'){throw 'Synthetic PSD name mismatch.'}
 $synthetic=Join-Path $OutputDir 'synthetic.psd';Copy-Item -LiteralPath $source -Destination $synthetic
 $project=Join-Path $OutputDir 'synthetic-normal-export.ymmp';$identity=[Guid]::NewGuid().ToString('N')
 $seed.Characters[0].Name='PR-A H-A2 synthetic'
 $seed.Characters[0].TachieDefaultItemParameter.Source.Path=$synthetic
 $seed.Characters[0].TachieDefaultItemParameter.Source.AssetIdentity=$identity
 $item=$seed.Timelines[0].Items[0];$item.CharacterName=$seed.Characters[0].Name
 $item.TachieItemParameter.Source.Path=$synthetic;$item.TachieItemParameter.Source.AssetIdentity=$identity
 $item.Frame=0;$item.Length=5
 $seed.Timelines[0].Name='PR-A H-A2 normal writer';$seed.Timelines[0].CurrentFrame=0
 $seed.Timelines[0].VideoInfo.Width=64;$seed.Timelines[0].VideoInfo.Height=32;$seed.Timelines[0].VideoInfo.FPS=30
 $seed.FilePath=$project;$seed | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $project -Encoding utf8
 Get-ChildItem -LiteralPath $destination -Filter '*.dll' | Get-FileHash | Select-Object Hash,Path | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'installed-dll-hashes.json') -Encoding utf8
 $env:SOURCE_HEAD=$sourceHead;$env:PSD_NEXT_LIVE_PROOF_OUTPUT=$OutputDir;$env:PSD_NEXT_LIVE_PROOF_PROJECT=$project;$env:PSD_NEXT_LIVE_PROOF_SCENARIO='NormalExport'
 "HOST_LAUNCH $([DateTime]::UtcNow.ToString('O'))" | Add-Content -LiteralPath (Join-Path $OutputDir 'batch-stages.log')
 $p=Start-Process -FilePath $exe -ArgumentList ('"'+$project+'"') -WorkingDirectory $Ymm4Dir -WindowStyle Hidden -PassThru
 [ordered]@{sourceHead=$sourceHead;sourceStatus=$sourceStatus;hostVersion='4.56.1.0';hostSha256=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant();sdk=(& $DotnetPath --version);ownedPid=$p.Id;host=$exe;syntheticProject=$project;scope='one native host, official normal writer, five-frame cold/failure/cancel/reference-clear cases';scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant();pushed=$false;actionsStarted=$false;newDependencies=$false} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDir 'batch-provenance.json') -Encoding utf8
 $marker=Join-Path $OutputDir 'export-complete.txt'
 for($i=0;$i -lt $TimeoutSeconds;$i++) {
  if(Test-Path -LiteralPath $marker){break}
  if($p.HasExited){throw 'Owned host exited before proof completion.'}
  $script:exportProofPid=$p.Id;$script:exportProofOutput=$OutputDir
  $callback=[PsdNormalExportWindows+EnumProc]{param([IntPtr]$h,[IntPtr]$v)
   [uint32]$owner=0;[void][PsdNormalExportWindows]::GetWindowThreadProcessId($h,[ref]$owner)
   if($owner -eq $script:exportProofPid){
    $title=New-Object Text.StringBuilder 1024;[void][PsdNormalExportWindows]::GetWindowText($h,$title,1024)
    $text=$title.ToString();if($text){Add-Content -LiteralPath (Join-Path $script:exportProofOutput 'window-titles.log') -Value $text}
    if($text -like '*Check for updates*' -or $text -like '*About YukkuriMovieMaker*'){[void][PsdNormalExportWindows]::PostMessage($h,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)}
   };return $true
  }
  [void][PsdNormalExportWindows]::EnumWindows($callback,[IntPtr]::Zero)
  Start-Sleep -Seconds 1
 }
 if(-not(Test-Path -LiteralPath $marker)){throw 'Native batch timed out; preserve evidence without a PASS claim.'}
 $result=Get-Content -LiteralPath (Join-Path $OutputDir 'export-results.json') -Raw | ConvertFrom-Json
 $actual=(Get-FileHash -LiteralPath (Join-Path $destination 'PsdTachieNext.Ymm4.dll')).Hash.ToLowerInvariant()
 if($result.productAssemblySha256 -ne $actual){throw 'Executed product differs from installed DLL hash.'}
 if($result.status -notin @('PASS_NORMAL_WRITER_WITH_NATIVE_CANCEL','PARTIAL_NORMAL_WRITER_NATIVE_CANCEL_BOUNDARY_OPEN')){throw "Native batch failed: $($result.error)"}
 Write-Host "NORMAL WRITER SUMMARY: $($result.assertions) assertions; $($result.status); host launches=1."
} catch {
 [ordered]@{status='FAIL_OR_BLOCKED';error=$_.Exception.Message;sourceHead=$sourceHead;completedNativeProof=(Test-Path -LiteralPath (Join-Path $OutputDir 'export-complete.txt'))} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'batch-results.json') -Encoding utf8
 throw
} finally {
 if($null -ne $p -and -not $p.HasExited){$p.Kill($true);$p.WaitForExit(10000) | Out-Null}
 "OWNED_HOST_STOPPED $([DateTime]::UtcNow.ToString('O'))" | Add-Content -LiteralPath (Join-Path $OutputDir 'batch-stages.log')
 foreach($name in @('SOURCE_HEAD','PSD_NEXT_LIVE_PROOF_OUTPUT','PSD_NEXT_LIVE_PROOF_PROJECT','PSD_NEXT_LIVE_PROOF_SCENARIO')){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}
}
