param(
    [Parameter(Mandatory=$true)][string]$Ymm4Dir,
    [Parameter(Mandatory=$true)][string]$DriverDir,
    [Parameter(Mandatory=$true)][string]$EvidenceDir
)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP -or -not $env:GITHUB_RUN_ID){
    throw 'Runner-only synthetic validation; local GUI launch is not supported by this script.'
}
$hostRoot=[IO.Path]::GetFullPath($Ymm4Dir).TrimEnd('\')+'\'
$tempRoot=[IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')+'\'
if(-not $hostRoot.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Host must remain in the temporary runner directory.'}
if(Get-Process -Name YukkuriMovieMaker -ErrorAction SilentlyContinue){throw 'Unexpected existing YMM4 process.'}
$exe=Join-Path $hostRoot 'YukkuriMovieMaker.exe'
if(-not(Test-Path -LiteralPath $exe) -or (Get-Item $exe).VersionInfo.FileVersion -ne $env:HOST_VERSION){throw 'Verified host is unavailable.'}
$names=@('PsdTachieNext.Ymm4.dll','PsdTachieNext.Core.dll','PsdTachieNext.Compiler.dll','PsdTachieNext.Parser.dll','PsdTachieNext.HostProof.dll')
$dest=Join-Path $hostRoot 'user\plugin\PsdTachieNext'
New-Item -ItemType Directory -Path $dest | Out-Null
foreach($name in $names){
    $source=Join-Path $DriverDir $name
    if(-not(Test-Path -LiteralPath $source)){throw 'Allowlisted assembly missing.'}
    Copy-Item -LiteralPath $source -Destination (Join-Path $dest $name)
}
New-Item -ItemType Directory -Force $EvidenceDir | Out-Null
$scratch=Join-Path $env:RUNNER_TEMP 'psd-palette-synthetic-input-and-private-diagnostics'
New-Item -ItemType Directory -Path $scratch | Out-Null
$env:PSD_NEXT_PALETTE_NATIVE_OUTPUT=$scratch
$process=$null
try{
    $process=Start-Process -FilePath $exe -WorkingDirectory $hostRoot -WindowStyle Hidden -PassThru
    $complete=Join-Path $scratch 'palette-native-complete.txt'
    $deadline=[DateTime]::UtcNow.AddSeconds(300)
    while([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited -and -not(Test-Path -LiteralPath $complete)){
        Start-Sleep -Milliseconds 500
    }
    $result=Join-Path $scratch 'palette-results.json'
    if(-not(Test-Path -LiteralPath $result)){
        [ordered]@{schema='psd-next.palette-native-ui.v1';status='BLOCKED_NO_RESULT';actualHost=$true;actualHostRealizedProductView=$false;physicalClickKeyboardVerified=$false;reason='Owned host exited or bounded 300-second deadline elapsed.'} | ConvertTo-Json | Set-Content (Join-Path $EvidenceDir 'palette-results.json')
        throw 'Native palette validation blocked: no sanitized result.'
    }
    $data=Get-Content -Raw -LiteralPath $result | ConvertFrom-Json
    Copy-Item -LiteralPath $result -Destination (Join-Path $EvidenceDir 'palette-results.json')
    foreach($name in @('target.png','eyes.png','mouth.png','reopened.png')){
        $image=Join-Path $scratch $name
        if(Test-Path -LiteralPath $image){Copy-Item -LiteralPath $image -Destination (Join-Path $EvidenceDir $name)}
    }
    Write-Host ("palette_status="+$data.status+";assertions="+$data.assertions+";realized_view="+$data.actualHostRealizedProductView+";physical_input=false")
    if($data.status -ne 'PASS'){throw 'Native palette validation failed or blocked; inspect allowlisted JSON.'}
    foreach($name in @('target.png','eyes.png','mouth.png','reopened.png')){
        if(-not(Test-Path -LiteralPath (Join-Path $EvidenceDir $name))){throw 'Expected synthetic WPF evidence missing.'}
    }
}finally{
    Remove-Item Env:PSD_NEXT_PALETTE_NATIVE_OUTPUT -ErrorAction SilentlyContinue
    if($process -and -not $process.HasExited){
        # Runner-only owned test process cleanup, not product history rollback.
        Stop-Process -Id $process.Id -Force
    }
}
