param([ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$Rebuild,[switch]$Clean)
. $PSScriptRoot\common.ps1
Initialize-JpvEnvironment -RuntimeOnly
Push-Location $JpvRoot
try {
    if(-not $Clean){Invoke-JpvChecked $JpvCmake @('--preset','runtime')}
    $arguments=@('--build','--preset',"runtime-$($Configuration.ToLowerInvariant())",'--parallel','4')
    if($Rebuild){$arguments+='--clean-first'}
    if($Clean){$arguments+=@('--target','clean')}
    Invoke-JpvChecked $JpvCmake $arguments
} finally { Pop-Location }
