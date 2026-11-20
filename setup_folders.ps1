<#
.SYNOPSIS
    Non-destructively imports current Windows Desktop shortcuts into LumeDesk's big-folder structure.
.DESCRIPTION
    Scans the current user's Desktop and Public Desktop, classifies .lnk/.url shortcuts by keywords
    into curated smartphone-style folders under .\data\, and selects up to 4 Dock shortcuts.
    Original Windows Desktop files are NEVER moved or deleted.
.PARAMETER NoCopy
    Only create empty starter folders and default config.json without copying Desktop shortcuts.
.PARAMETER Force
    Overwrite existing shortcuts in .\data\ if files with the same name already exist.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\setup_folders.ps1
    powershell -ExecutionPolicy Bypass -File .\setup_folders.ps1 -NoCopy
#>
param(
    [switch]$NoCopy,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dataRoot = Join-Path $root "data"
$userDesktop = [Environment]::GetFolderPath("Desktop")
$pubDesktop = [Environment]::GetFolderPath("CommonDesktopDirectory")

$categories = @(
    "01 · AI 助手",
    "02 · 社媒沟通",
    "03 · 知识办公",
    "04 · 影音娱乐",
    "05 · 开发工具",
    "06 · 系统网络"
)

foreach ($sub in ($categories + @("_Dock", "_Icons"))) {
    $p = Join-Path $dataRoot $sub
    if (-not (Test-Path $p)) {
        New-Item -ItemType Directory -Path $p -Force | Out-Null
    }
}

# Keyword-based auto-categorization rules (evaluated in priority order)
$rules = @(
    @{ Folder = "01 · AI 助手";   Pattern = "gemini|chatgpt|openai|claude|grok|deepseek|copilot|notebooklm|ai studio|antigravity|perplexity|cursor|poe|kimi|doubao|豆包|元宝|通义|文心" },
    @{ Folder = "02 · 社媒沟通"; Pattern = "微信|wechat|weixin|qq|tim|telegram|discord|slack|feishu|飞书|dingtalk|钉钉|whatsapp|teams|zoom|腾讯会议|mail|gmail|outlook|foxmail|thunderbird" },
    @{ Folder = "03 · 知识办公"; Pattern = "obsidian|notion|zotero|wps|word|excel|powerpoint|onenote|office|google docs|google sheets|google slides|google drive|google calendar|typora|xmind|acrobat|pdf|caj|endnote|logseq|anki" },
    @{ Folder = "04 · 影音娱乐"; Pattern = "youtube|spotify|music|网易云|qq音乐|酷狗|bilibili|b站|potplayer|vlc|mpv|steam|epic|wegame|xbox|qbittorrent|utorrent|netflix|爱奇艺|腾讯视频|优酷|photos|相册|obs" },
    @{ Folder = "05 · 开发工具"; Pattern = "visual studio|code|vscode|pycharm|idea|clion|goland|rider|webstorm|git|github|docker|postman|terminal|powershell|anaconda|miniconda|cursor|windsurf|sublime|notepad\+\+|xshell|mobaxterm|termius|dbeaver|navicat|figma|blender" },
    @{ Folder = "06 · 系统网络"; Pattern = "clash|v2ray|mihomo|flclash|nekoray|tailscale|zerotier|wireguard|everything|wiztree|spacesniffer|7-zip|winrar|bandizip|chrome|edge|firefox|brave|arc|opera|夸克|浏览器|snipaste|pixpin|powertoys|geforce|nvidia|amd|msi|hwinfo|ccleaner|geek|uTools" }
)

# High-priority candidates for bottom 4-icon Dock
$dockPriority = @(
    "chrome|edge|firefox|brave",
    "微信|wechat|weixin|telegram|qq",
    "obsidian|notion|wps|zotero",
    "antigravity|cursor|visual studio code|vscode|terminal"
)

if (-not $NoCopy) {
    $allShortcuts = @()
    $seenNames = @{}

    foreach ($srcRoot in @($userDesktop, $pubDesktop)) {
        if (-not (Test-Path $srcRoot)) { continue }
        $items = Get-ChildItem -Path $srcRoot -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @(".lnk", ".url") }
        foreach ($it in $items) {
            if ($it.BaseName -match "^(LumeDesk|VertiHome)") { continue }
            $key = $it.Name.ToLowerInvariant()
            if (-not $seenNames.ContainsKey($key)) {
                $seenNames[$key] = $true
                $allShortcuts += $it
            }
        }
    }

    Write-Host "Scanning Desktop shortcuts non-destructively (found $($allShortcuts.Count) items)..."
    $copiedCount = 0

    foreach ($it in $allShortcuts) {
        $targetFolder = "06 · 系统网络"
        foreach ($r in $rules) {
            if ($it.BaseName -imatch $r.Pattern) {
                $targetFolder = $r.Folder
                break
            }
        }
        $dst = Join-Path (Join-Path $dataRoot $targetFolder) $it.Name
        if ($Force -or -not (Test-Path $dst)) {
            Copy-Item -Path $it.FullName -Destination $dst -Force
            Write-Host "  + [$targetFolder] $($it.Name)"
            $copiedCount++
        }
    }

    # Populate _Dock with up to 4 frequently used apps if _Dock is currently empty
    $dockDir = Join-Path $dataRoot "_Dock"
    $existingDock = @(Get-ChildItem -Path $dockDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @(".lnk", ".url") })
    if ($existingDock.Count -eq 0 -and $allShortcuts.Count -gt 0) {
        $picked = @()
        foreach ($pat in $dockPriority) {
            $match = $allShortcuts | Where-Object { $_.BaseName -imatch $pat -and ($picked -notcontains $_.FullName) } | Select-Object -First 1
            if ($match) {
                $picked += $match.FullName
                Copy-Item -Path $match.FullName -Destination (Join-Path $dockDir $match.Name) -Force
                Write-Host "  + [_Dock] $($match.Name)"
            }
            if ($picked.Count -ge 4) { break }
        }
        # Fill remaining Dock slots if fewer than 4 matched
        foreach ($it in $allShortcuts) {
            if ($picked.Count -ge 4) { break }
            if ($picked -notcontains $it.FullName) {
                $picked += $it.FullName
                Copy-Item -Path $it.FullName -Destination (Join-Path $dockDir $it.Name) -Force
                Write-Host "  + [_Dock] $($it.Name)"
            }
        }
    }

    Write-Host "Imported $copiedCount shortcuts into $dataRoot."
} else {
    Write-Host "Created starter folder structure in $dataRoot (-NoCopy mode)."
}

# Create clean default config.json from config.example.json if missing
$cfgPath = Join-Path $root "config.json"
$examplePath = Join-Path $root "config.example.json"
if (-not (Test-Path $cfgPath)) {
    if (Test-Path $examplePath) {
        Copy-Item -Path $examplePath -Destination $cfgPath -Force
        Write-Host "Created default config.json from config.example.json"
    }
}

Write-Host "Done! Original Windows Desktop was NOT modified."
