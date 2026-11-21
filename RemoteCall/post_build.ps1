param(
	[Parameter(Mandatory=$true)]
	[string]$TargetPath,

	[Parameter(Mandatory=$true)]
	[string]$OutputPath,

	[Parameter(Mandatory=$true)]
	[string]$VCToolsInstallDir
)

$ErrorActionPreference = 'Stop'

$dumpbin = Join-Path -Path $VCToolsInstallDir -ChildPath bin\Hostx64\x64\dumpbin

$dump = & $dumpbin /section:.text /rawdata "$TargetPath"
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$startIndex = -1
$endIndex = $dump.Count
for ($i = 0; $i -lt $dump.Count; $i++) {
	$line = $dump[$i]
	if ($startIndex -eq -1) {
		if ($line -match '^RAW DATA') {
			$startIndex = $i + 1
		}
	} elseif ($line -eq '') {
		$endIndex = $i
		break
	}
}
if ($startIndex -eq -1) {
	Write-Error "Raw data not found in dumpbin output"
	exit 1
}

$lines = [string[]]::new($endIndex - $startIndex)
for ($i = $startIndex; $i -lt $endIndex; $i++) {
	$line = $dump[$i]
	if (-not ($line -match '^  [0-9A-F]+:((?: [0-9A-F]{2}){1,16})')) {
		Write-Error "Failed to parse dumpbin output: $line"
		exit 1
	}

	$bytes = ($Matches[1] -replace ' ([0-9A-F]{2})', '0x$1, ').TrimEnd().ToLower()
	$lines[$i - $startIndex] = $bytes
}
Set-Content -Path $OutputPath -Value $lines