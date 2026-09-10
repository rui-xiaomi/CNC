[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Invoke-HitlStep {
    param([Parameter(Mandatory)][string]$Instruction)
    Write-Host "`n>>> $Instruction"
    Read-Host '    完成后按 Enter' | Out-Null
}

function Read-HitlValue {
    param([Parameter(Mandatory)][string]$Question)
    Write-Host "`n>>> $Question"
    return Read-Host '    >'
}

# 在下方按复现路径修改。不要写入密钥或生产数据。
Invoke-HitlStep '启动应用并打开待诊断的 Surface。'
$errored = Read-HitlValue '执行目标操作后是否出现错误？(y/n)'
$errorMessage = Read-HitlValue "粘贴脱敏后的错误信息（没有则输入 'none'）"

Write-Host "`n--- 已捕获 ---"
Write-Output "ERRORED=$errored"
Write-Output "ERROR_MSG=$errorMessage"

