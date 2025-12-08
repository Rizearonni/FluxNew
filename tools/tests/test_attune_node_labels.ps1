param(
    [string]$AddonPath = "D:\Program Files (x86)\World of Warcraft\_classic_era_\Interface\AddOns\Attune",
    [string]$ProjectPath = "e:\FluxParser\flux-new\FluxNew\FluxNew.csproj",
    [switch]$VerboseRun
)

Write-Host "Running headless loader for addon path: $AddonPath"

$dotnetArgs = @("run", "--project", $ProjectPath, "--configuration", "Debug", "--", "--load-addon", $AddonPath)
if ($VerboseRun) { Write-Host "dotnet $($dotnetArgs -join ' ')" }

# Run dotnet and capture all output
$procOutput = & dotnet @dotnetArgs 2>&1
$outStr = $procOutput -join "`n"
 # Prefer reading the JSON file written by the serializer when available
 $projectDir = Split-Path $ProjectPath -Parent
 $jsonFile = Join-Path $projectDir 'debug_frames.json'
 if (Test-Path $jsonFile) {
    try {
        $jsonText = Get-Content -Raw -Path $jsonFile -ErrorAction Stop
    } catch {
        Write-Error ("Failed to read debug JSON file at {0}: {1}" -f $jsonFile, $_)
        exit 2
    }
} else {
    # Try to locate the JSON produced on stdout. The program may print a marker like:
    # "Addon 'Attune' frames (json): <json>" or "Serializer raw (len=...): <json>"
    $marker = "Addon 'Attune' frames (json):"
    $markerIndex = $outStr.LastIndexOf($marker)
    if ($markerIndex -lt 0) {
        Write-Host "JSON file not found; attempting to extract JSON from stdout markers"
        $jsonCandidate = $outStr
    } else {
        $jsonStart = $markerIndex + $marker.Length
        $jsonCandidate = $outStr.Substring($jsonStart).Trim()
    }

    # Prefer extracting the JSON that follows the serializer's "Serializer raw (len=...)" marker
    $serMarker = 'Serializer raw (len='
    $serIndex = $outStr.LastIndexOf($serMarker)
    if ($serIndex -ge 0) {
        $start = $outStr.IndexOf('[', $serIndex)
        $end = $outStr.LastIndexOf(']')
        if ($start -ge 0 -and $end -gt $start) {
            $jsonText = $outStr.Substring($start, $end - $start + 1)
        } else {
            # fallback to using the candidate
            $firstBracket = $jsonCandidate.IndexOf('[')
            $lastBracket = $jsonCandidate.LastIndexOf(']')
            if ($firstBracket -ge 0 -and $lastBracket -gt $firstBracket) {
                $jsonText = $jsonCandidate.Substring($firstBracket, $lastBracket - $firstBracket + 1)
            } else {
                $jsonText = $jsonCandidate
            }
        }
    } else {
        $firstBracket = $jsonCandidate.IndexOf('[')
        $lastBracket = $jsonCandidate.LastIndexOf(']')
        if ($firstBracket -ge 0 -and $lastBracket -gt $firstBracket) {
            $jsonText = $jsonCandidate.Substring($firstBracket, $lastBracket - $firstBracket + 1)
        } else {
            $jsonText = $jsonCandidate
        }
    }
}

try {
    $items = $jsonText | ConvertFrom-Json -ErrorAction Stop
} catch {
    Write-Error "Failed to parse JSON from serializer output: $_"; Write-Host "Raw JSON candidate:`n$jsonText"; exit 3
}

# Assert: at least one object with type 'FontString' and non-empty text
$font = $items | Where-Object { $_.type -eq 'FontString' -and $_.text -and ($_.text.Trim() -ne '') } | Select-Object -First 1
if (-not $font) {
    Write-Error "Assertion failed: No FontString with non-empty 'text' found in serialized frames."
    exit 4
}

# Assert: at least one object (typically a frame) with non-empty nodePath
$frameWithNodePath = $items | Where-Object { $_.PSObject.Properties.Match('nodePath') -and ($_.nodePath -ne $null) -and ($_.nodePath.Trim() -ne '') } | Select-Object -First 1
if (-not $frameWithNodePath) {
    Write-Error "Assertion failed: No frame with non-empty 'nodePath' found in serialized frames."
    exit 5
}

Write-Host "Integration test passed: found FontString text and frame with nodePath."
Write-Host "Example FontString text: '$($font.text)'"
Write-Host "Example nodePath: '$($frameWithNodePath.nodePath)'"
exit 0
