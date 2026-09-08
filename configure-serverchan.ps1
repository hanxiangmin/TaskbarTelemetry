[CmdletBinding()]
param(
    [switch]$Clear
)

$ErrorActionPreference = 'Stop'
$variableName = 'SERVERCHAN_SENDKEY'

if ($Clear) {
    [Environment]::SetEnvironmentVariable($variableName, $null, 'User')
    [Environment]::SetEnvironmentVariable($variableName, $null, 'Process')
    Write-Host "已删除当前 Windows 用户的 $variableName。"
    return
}

$secureKey = Read-Host '请粘贴 Server酱³ 或 Turbo SendKey（输入内容会隐藏）' -AsSecureString
$keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
try {
    $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
    if ($null -ne $plainKey) { $plainKey = $plainKey.Trim() }
    if ([string]::IsNullOrWhiteSpace($plainKey) -or
        $plainKey -cnotmatch '\A(?:sctp[0-9]{1,20}t[A-Za-z0-9_-]+|SCT[A-Za-z0-9_-]+)\z' -or
        $plainKey.Length -lt 12 -or $plainKey.Length -gt 512) {
        throw 'SendKey 无效：Server酱³ 为 sctp数字t密钥，Turbo 为 SCT 开头。'
    }

    [Environment]::SetEnvironmentVariable($variableName, $plainKey.Trim(), 'User')
    [Environment]::SetEnvironmentVariable($variableName, $plainKey.Trim(), 'Process')
    Write-Host "已为当前 Windows 用户配置 $variableName。"
    Write-Host '密钥没有写入 TaskbarTelemetry.ini，也不会回显到控制台。'
    Write-Host '请右键 TaskbarTelemetry，选择“发送通知测试消息”。'
}
finally {
    if ($keyPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)
    }
    Remove-Variable plainKey -ErrorAction SilentlyContinue
    Remove-Variable secureKey -ErrorAction SilentlyContinue
}
