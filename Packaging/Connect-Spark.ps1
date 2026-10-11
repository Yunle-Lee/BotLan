# Keep the laptop connected to a DGX Spark running the BotLan gateway (BotLan-CLI scripts/08_botlan.sh).
#
#   .\Packaging\Connect-Spark.ps1 -Spark user1@192.168.1.50
#   .\Packaging\Connect-Spark.ps1 -Spark user1@spark.example.lan -SshPort 222
#
# Opens a local-only tunnel 127.0.0.1:8091 -> Spark 127.0.0.1:8091 and reopens it if it drops.
# The gateway key stays on the Spark; add the Bot once with the key printed by 08_botlan.sh.
# Use SSH keys (ssh-copy-id) so reconnects do not stop at a password prompt.
param(
    [Parameter(Mandatory)][string]$Spark,
    [int]$SshPort = 22,
    [int]$LocalPort = 8091,
    [int]$RemotePort = 8091
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) { throw 'OpenSSH client (ssh.exe) is required.' }
Write-Host "BotLan -> $Spark : 127.0.0.1:$LocalPort -> 127.0.0.1:$RemotePort (Ctrl+C to stop)"
while ($true) {
    & ssh -N -p $SshPort `
        -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 `
        -L "127.0.0.1:${LocalPort}:127.0.0.1:${RemotePort}" $Spark
    Write-Host "$(Get-Date -Format HH:mm:ss) tunnel closed (exit $LASTEXITCODE); reconnecting in 3 s..."
    Start-Sleep -Seconds 3
}
