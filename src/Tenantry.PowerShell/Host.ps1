# Tenantry's PowerShell console host. Tenantry starts pwsh with this script and talks to it one
# JSON line at a time: requests on stdin, replies on stdout marked with a leading \u0001 (anything
# else on stdout is a command writing straight to the console). Commands run in the global scope,
# so their variables last until the session restarts. Its own variables start $__tny_.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$ConfirmPreference = 'None'
$PSStyle.OutputRendering = 'PlainText'
$ErrorView = 'ConciseView'
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$__tny_out = [Console]::Out
$__tny_modules = [ordered]@{
    graph      = 'Microsoft.Graph.Authentication'
    exchange   = 'ExchangeOnlineManagement'
    purview    = 'ExchangeOnlineManagement'
    sharepoint = 'PnP.PowerShell'
}

function __TnySend([System.Collections.IDictionary]$message) {
    $__tny_out.WriteLine("$([char]1)" + (ConvertTo-Json -InputObject $message -Compress -Depth 4))
    $__tny_out.Flush()
}

function __TnyLine([string]$stream, [string]$text) {
    __TnySend @{ kind = 'line'; stream = $stream; text = $text }
}

# An error as "Command: message". PowerShell's own view would point at this script's line,
# which means nothing to whoever typed the command.
function __TnyError($record) {
    $message = if ($record.ErrorDetails.Message) { $record.ErrorDetails.Message } else { $record.Exception.Message }
    $command = $record.InvocationInfo.MyCommand.Name
    if ($command -and $command -notlike '*.ps1') { "${command}: $message" } else { $message }
}

function __TnyFound {
    $found = @{}
    foreach ($name in ($__tny_modules.Values | Select-Object -Unique)) {
        $module = Get-Module -ListAvailable -Name $name | Sort-Object Version -Descending | Select-Object -First 1
        $found[$name] = if ($module) { "$($module.Version)" } else { '' }
    }
    $found
}

function __TnyConnection([string]$service, [string]$status, [string]$detail) {
    __TnySend @{ kind = 'connection'; service = $service; status = $status; detail = $detail }
}

# Connects one service, quietly; says how it went.
function __TnyConnectOne([string]$service, $found, [scriptblock]$connect) {
    $module = $__tny_modules[$service]
    if (-not $found[$module]) {
        __TnyConnection $service 'ModuleMissing' "The $module module isn't installed."
        return
    }
    __TnyConnection $service 'Connecting' ''
    try {
        $ErrorActionPreference = 'Stop'
        & $connect *> $null
        __TnyConnection $service 'Connected' "$module $($found[$module])"
    }
    catch {
        __TnyConnection $service 'Failed' (($_.Exception.Message -split "`n")[0])
    }
}

# Graph first, then Exchange and Purview, then PnP: the order seen working with all four
# modules' sign-in libraries loaded in one process.
function __TnyConnect($request) {
    $found = __TnyFound
    $services = @($request.services)
    if ($services -contains 'graph') {
        # One real request straight away: the Graph module loads its Microsoft.Graph.Core only on
        # its first request, and once Exchange or PnP has loaded their older copy of that
        # assembly first, every Graph request fails ("The pipeline has been stopped").
        __TnyConnectOne 'graph' $found {
            Connect-MgGraph -AccessToken (ConvertTo-SecureString $request.graph -AsPlainText -Force) -NoWelcome
            $null = Invoke-MgGraphRequest -Method GET -Uri 'v1.0/organization?$select=id'
        }
    }
    if ($services -contains 'exchange') {
        __TnyConnectOne 'exchange' $found { Connect-ExchangeOnline -AccessToken $request.exchange -Organization $request.organization -UserPrincipalName $request.account -ShowBanner:$false }
    }
    if ($services -contains 'purview') {
        if ($request.purview) {
            __TnyConnectOne 'purview' $found { Connect-IPPSSession -AccessToken $request.purview -Organization $request.organization -UserPrincipalName $request.account -ShowBanner:$false }
        }
        else {
            __TnyConnection 'purview' 'NeedsSignIn' 'Purview needs its own sign-in first.'
        }
    }
    if ($services -contains 'sharepoint') {
        if ($request.sharepoint) {
            __TnyConnectOne 'sharepoint' $found { Connect-PnPOnline -Url $request.sharePointAdmin -AccessToken $request.sharepoint }
        }
        else {
            __TnyConnection 'sharepoint' 'Failed' "This tenant's SharePoint address couldn't be found."
        }
    }
    __TnySend @{ kind = 'connected' }
}

function __TnyInstall {
    $found = __TnyFound
    foreach ($name in ($__tny_modules.Values | Select-Object -Unique)) {
        if ($found[$name]) {
            continue
        }
        __TnyLine 'Information' "Installing $name for this Windows/Mac/Linux user from the PowerShell Gallery…"
        try {
            if (Get-Command Install-PSResource -ErrorAction SilentlyContinue) {
                Install-PSResource -Name $name -Scope CurrentUser -TrustRepository -ErrorAction Stop *> $null
            }
            else {
                Install-Module -Name $name -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop *> $null
            }
            __TnyLine 'Information' "Installed $name."
        }
        catch {
            __TnyLine 'Error' "Couldn't install ${name}: $(($_.Exception.Message -split "`n")[0])"
        }
    }
    __TnySend @{ kind = 'installed' }
}

__TnySend @{ kind = 'hello'; version = "$($PSVersionTable.PSVersion)" }

while ($null -ne ($__tny_request = [Console]::In.ReadLine())) {
    $__tny_request = $__tny_request | ConvertFrom-Json
    if ($__tny_request.op -eq 'connect') {
        __TnyConnect $__tny_request
        $__tny_request = $null
    }
    elseif ($__tny_request.op -eq 'install') {
        __TnyInstall
    }
    elseif ($__tny_request.op -eq 'complete') {
        $__tny_matches = @()
        $__tny_start = $__tny_request.cursor
        $__tny_length = 0
        try {
            $__tny_result = TabExpansion2 -inputScript $__tny_request.input -cursorColumn $__tny_request.cursor
            $__tny_start = $__tny_result.ReplacementIndex
            $__tny_length = $__tny_result.ReplacementLength
            $__tny_matches = @($__tny_result.CompletionMatches | Select-Object -First 200 | ForEach-Object CompletionText)
        }
        catch {
        }
        __TnySend @{ kind = 'completion'; start = $__tny_start; length = $__tny_length; matches = [string[]]$__tny_matches }
    }
    elseif ($__tny_request.op -eq 'run') {
        # Everything a command writes comes through one pipeline: errors, warnings and Write-Host
        # by kind, objects through one Out-String so a table keeps one header however long it runs.
        $__tny_errors = 0
        $__tny_format = { Out-String -Stream -Width 240 }.GetSteppablePipeline()
        $__tny_format.Begin($true)
        try {
            $__tny_command = [scriptblock]::Create($__tny_request.script)
            . $__tny_command *>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) {
                    $__tny_errors++
                    __TnyLine 'Error' (__TnyError $_)
                }
                elseif ($_ -is [System.Management.Automation.WarningRecord]) {
                    __TnyLine 'Warning' "WARNING: $($_.Message)"
                }
                elseif ($_ -is [System.Management.Automation.VerboseRecord]) {
                    __TnyLine 'Verbose' "VERBOSE: $($_.Message)"
                }
                elseif ($_ -is [System.Management.Automation.DebugRecord]) {
                    __TnyLine 'Verbose' "DEBUG: $($_.Message)"
                }
                elseif ($_ -is [System.Management.Automation.InformationRecord]) {
                    __TnyLine 'Information' "$($_.MessageData)"
                }
                else {
                    foreach ($__tny_text in $__tny_format.Process($_)) {
                        __TnyLine 'Output' $__tny_text
                    }
                }
            }
        }
        catch {
            $__tny_errors++
            __TnyLine 'Error' (__TnyError $_)
        }
        try {
            foreach ($__tny_text in $__tny_format.End()) {
                __TnyLine 'Output' $__tny_text
            }
        }
        catch {
        }
        __TnySend @{ kind = 'done'; errors = $__tny_errors }
    }
}
