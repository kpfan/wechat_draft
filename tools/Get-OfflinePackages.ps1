param(
    [string]$Package = 'Microsoft.WindowsAppSDK.WinUI',
    [string]$Version = '1.8.260803003'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
$taskFeed = Join-Path $taskRoot '.tools\feed'
$taskCache = Join-Path $taskRoot '.nuget\packages'
New-Item -ItemType Directory -Path $taskFeed -Force | Out-Null
$taskQueue = [System.Collections.Generic.Queue[object]]::new()
$taskQueue.Enqueue(@{ Id=$Package.ToLowerInvariant(); Version=$Version })
$taskSeen = [System.Collections.Generic.HashSet[string]]::new()
while ($taskQueue.Count -gt 0) {
    $taskItem = $taskQueue.Dequeue()
    $taskId = $taskItem.Id.ToLowerInvariant()
    $taskVersion = $taskItem.Version.ToLowerInvariant()
    if ($taskId -notmatch '^[a-z0-9._-]+$' -or $taskVersion -notmatch '^[a-z0-9.+-]+$') { throw 'Invalid package identity' }
    if (-not $taskSeen.Add("$taskId/$taskVersion")) { continue }
    $taskFile = Join-Path $taskFeed "$taskId.$taskVersion.nupkg"
    $taskCached = Join-Path $taskCache "$taskId\$taskVersion\$taskId.$taskVersion.nupkg"
    if ((-not (Test-Path -LiteralPath $taskFile)) -and (Test-Path -LiteralPath $taskCached)) { Copy-Item -LiteralPath $taskCached -Destination $taskFile }
    if (-not (Test-Path -LiteralPath $taskFile)) {
        $taskUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/flat2/$taskId/$taskVersion/$taskId.$taskVersion.nupkg"
        Write-Output "Downloading $taskId $taskVersion"
        & curl.exe --silent --show-error --fail --location --http1.1 --tls-max 1.2 --connect-timeout 15 --max-time 600 --retry 2 --retry-all-errors --retry-delay 1 $taskUrl --output "$taskFile.partial"
        if ($LASTEXITCODE -ne 0) { throw "Package download failed: $taskId $taskVersion" }
        $taskResolved = [IO.Path]::GetFullPath($taskFile)
        if (-not $taskResolved.StartsWith([IO.Path]::GetFullPath($taskFeed) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escaped workspace feed' }
        Move-Item -LiteralPath "$taskFile.partial" -Destination $taskFile -Force
    }
    $taskZip = [System.IO.Compression.ZipFile]::OpenRead($taskFile)
    try {
        $taskEntry = $taskZip.Entries | Where-Object FullName -Like '*.nuspec' | Select-Object -First 1
        $taskReader = [System.IO.StreamReader]::new($taskEntry.Open())
        try { [xml]$taskSpec = $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
        foreach ($taskDependency in $taskSpec.SelectNodes('//*[local-name()="dependency"]')) {
            $taskDependencyVersion = $taskDependency.version.Trim('[',']','(',')').Split(',')[0].Trim()
            if ($taskDependencyVersion -and $taskDependencyVersion -notlike '*`**') {
                $taskQueue.Enqueue(@{ Id=$taskDependency.id; Version=$taskDependencyVersion })
            }
        }
    } finally { $taskZip.Dispose() }
}
Write-Output "Offline package collection complete: $($taskSeen.Count) packages"
