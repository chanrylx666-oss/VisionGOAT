param(
    [ValidateSet('Debug','Release','Profile','RelWithDebInfo')][string]$Configuration='Release',
    [ValidateSet('Debug','Release','RelWithDebInfo')][string]$JpvNativeConfiguration,
    [switch]$Rebuild,
    [switch]$Clean
)
. $PSScriptRoot\common.ps1
$nativeConfiguration = if ($JpvNativeConfiguration) { $JpvNativeConfiguration } elseif ($Configuration -eq 'Profile') { 'RelWithDebInfo' } else { $Configuration }
Initialize-JpvEnvironment -RuntimeOnly
Push-Location $JpvRoot
try {
    if(-not $Clean){Invoke-JpvChecked $JpvCmake @('--preset','runtime')}
    $arguments=@('--build','--preset',"runtime-$($nativeConfiguration.ToLowerInvariant())",'--parallel','4')
    if($Rebuild){$arguments+='--clean-first'}
    if($Clean){$arguments+=@('--target','clean')}
    Invoke-JpvChecked $JpvCmake $arguments
} finally { Pop-Location }
