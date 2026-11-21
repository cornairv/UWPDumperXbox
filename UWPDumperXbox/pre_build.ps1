param(
	[Parameter(Mandatory=$true)]
	[string]$ProjectDir,

	[Parameter(Mandatory=$true)]
	[string]$CodeBytesPath,
	
	[Parameter(Mandatory=$true)]
	[string]$CodeOutputPath,

	[Parameter(Mandatory=$true)]
	[string]$XmlOutputPath
)

$ErrorActionPreference = 'Stop'

$xmlTemplateFile = Join-Path -Path $ProjectDir -ChildPath UWPDumperXbox.template.xml
$codeTemplateFile = Join-Path -Path $ProjectDir -ChildPath UWPDumperXbox.template.cs

$xmlTemplate = Get-Content $xmlTemplateFile -Raw
$codeTemplate = Get-Content $codeTemplateFile -Raw
$codeBytes = Get-Content $CodeBytesPath -Raw
$codeBytes = $codeBytes.Replace("`n", "`n" + " " * 8).TrimEnd()
$code = $codeTemplate.Replace("// {{code_bytes}}", $codeBytes)
$xml = $xmlTemplate.Replace("// {{code}}", $code)

Set-Content -NoNewline -Path $CodeOutputPath -Value $code
Set-Content -NoNewline -Path $XmlOutputPath -Value $xml