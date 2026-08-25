# Increments the revision field of Identity/@Version in Package.appxmanifest.
#
#   1.1.0.0  ->  1.1.0.1  ->  1.1.0.2 ...
#
# Major/minor/build stay under manual control; only the last field moves. This
# exists because Windows refuses to install a sideloaded package whose version is
# not higher than the one already on the device - an unchanged version looks
# exactly like "my fix did nothing" while the old build quietly stays put.
#
# AppxAutoIncrementPackageRevision in the csproj is supposed to do this, but only
# fires in the Store packaging flow, not on an ordinary build or F5 deploy.
#
# Run automatically by the BumpPackageVersion target. Disable for a build with:
#   msbuild ... /p:BumpPackageVersion=false
param(
    [Parameter(Mandatory = $true)][string]$Manifest
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Manifest)) { throw "manifest not found: $Manifest" }

$text = [System.IO.File]::ReadAllText($Manifest)

$pattern = '(?<head><Identity[^>]*?Version=")(?<major>\d+)\.(?<minor>\d+)\.(?<build>\d+)\.(?<rev>\d+)(?<tail>")'
$match = [System.Text.RegularExpressions.Regex]::Match($text, $pattern)
if (-not $match.Success) { throw "Identity/@Version not found in $Manifest" }

$major = [int]$match.Groups['major'].Value
$minor = [int]$match.Groups['minor'].Value
$build = [int]$match.Groups['build'].Value
$rev   = [int]$match.Groups['rev'].Value

# Appx version fields are 16-bit. Roll into the build field rather than
# overflowing, which would silently produce an invalid manifest.
$rev++
if ($rev -gt 65535) { $rev = 0; $build++ }
if ($build -gt 65535) { throw "build field exhausted; raise major/minor by hand" }

$old = "$major.$minor.$($match.Groups['build'].Value).$($match.Groups['rev'].Value)"
$new = "$major.$minor.$build.$rev"

$replacement = '${head}' + $new + '${tail}'
$text = [System.Text.RegularExpressions.Regex]::Replace($text, $pattern, $replacement)

# Preserve the BOM: the manifest is UTF-8 with one, and dropping it makes the
# appx packaging step reject the file.
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($Manifest, $text, $utf8Bom)

Write-Host "Package version $old -> $new"
