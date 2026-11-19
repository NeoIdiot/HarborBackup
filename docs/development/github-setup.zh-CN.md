# 首次推送 GitHub、运行 CI 与设置保护

以下命令由你在自己的 PowerShell 中执行，目标为已创建的空公共仓库 `NeoIdiot/HarborBackup`。不需要连接聊天插件，也不要把登录验证码、令牌或授权文件粘贴到聊天或 issue。命令不会创建远端仓库，不会自动提交本地修改，不会强推。

启用保护后，后续 `main` 修改需要通过分支和 PR，CI 通过后可以自行合并；批准人数为 0，不需要第二个人批准。这里使用经典分支保护，一个 PUT 即可创建或更新，没有另外叠加规则集。若仓库已有其他保护规则，先检查原规则再调整，不要直接覆盖既有配置。

## 1. 安装和登录

已安装 `gh` 可跳过安装。否则执行：

```powershell
winget install --id GitHub.cli --exact --source winget
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI 安装未成功，请先处理安装错误。' }
```

安装后重新打开 PowerShell，让 PATH 生效。把下面的 `<仓库目录>` 替换为本地仓库目录；后续各段在同一个 PowerShell 窗口依次执行。

```powershell
Set-Location '<仓库目录>'
$ErrorActionPreference = 'Stop'
$repo = 'NeoIdiot/HarborBackup'
$origin = "https://github.com/$repo.git"

gh --version
if ($LASTEXITCODE -ne 0) { throw '请先安装 GitHub CLI，并重新打开 PowerShell。' }

# 现有活动账户的凭据有效就直接使用，不重复登录。
gh auth status --hostname github.com --active
if ($LASTEXITCODE -ne 0) {
    # 只有未登录或凭据无效时，才在你自己的浏览器中登录。
    # workflow 是附加授权，用于推送 .github/workflows 文件。
    gh auth login --hostname github.com --git-protocol https --web --scopes workflow
    if ($LASTEXITCODE -ne 0) { throw 'GitHub 登录未完成。' }
    gh auth status --hostname github.com --active
    if ($LASTEXITCODE -ne 0) { throw 'GitHub 登录状态无效。' }
}

# 为 github.com 的 Git HTTPS 操作配置 gh 凭据助手。
gh auth setup-git --hostname github.com
if ($LASTEXITCODE -ne 0) { throw 'Git 凭据助手配置失败。' }

function Invoke-GitChecked {
    $result = & git @args
    if ($LASTEXITCODE -ne 0) { throw "git 命令失败，退出码 $LASTEXITCODE。" }
    $result
}

function Invoke-GhChecked {
    $result = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh 命令失败，退出码 $LASTEXITCODE。" }
    $result
}

# API 错误立即停止；不把错误响应当作成功或空数据。
function Invoke-GitHubApi {
    param([string]$Path, [string]$Method = 'GET', [string]$Json)
    $options = @('api', $Path, '--method', $Method,
        '--header', 'Accept: application/vnd.github+json',
        '--header', 'X-GitHub-Api-Version: 2026-03-10')
    if ($PSBoundParameters.ContainsKey('Json')) {
        $response = $Json | & gh @options --input -
    } else {
        $response = & gh @options
    }
    if ($LASTEXITCODE -ne 0) { throw "GitHub API 调用失败：$Method $Path。" }
    if ($response) { ($response -join "`n") | ConvertFrom-Json }
}

$repository = Invoke-GitHubApi "repos/$repo"
if ($repository.full_name -ne $repo -or $repository.visibility -ne 'public' -or
    -not $repository.permissions.admin) {
    throw '目标必须是指定的公共仓库，且当前账户必须具有管理权限。'
}
```

现有活动账户凭据有效时，上面的检查会跳过登录。若该账户缺少 `workflow` scope，由你执行 `gh auth refresh --hostname github.com --scopes workflow`，确认其退出码为 0，再继续。

## 2. 检查并推送 main

有未提交文件时先自行检查并按任务提交，再重跑本段。不要为了让检查通过而直接提交所有本机文件。

```powershell
$branch = (Invoke-GitChecked branch --show-current | Out-String).Trim()
if ($branch -ne 'main') { throw '请先切换到准备首次推送的 main 分支。' }

$changes = @(Invoke-GitChecked status --porcelain=v1 --untracked-files=normal)
if ($changes.Count -ne 0) {
    $changes
    throw '工作区尚未干净；请先审查并提交需要公开的文件。'
}
$sha = (Invoke-GitChecked rev-parse --verify refs/heads/main | Out-String).Trim()

$remotes = @(Invoke-GitChecked remote)
if ($remotes -notcontains 'origin') {
    Invoke-GitChecked remote add origin $origin
} else {
    $existing = (Invoke-GitChecked remote get-url origin | Out-String).Trim()
    if ($existing -ne $origin) { throw 'origin 已指向其他 URL；请先人工核对，不自动覆盖。' }
}

$remoteRefs = @(Invoke-GitChecked ls-remote --heads --tags origin)
if ($remoteRefs.Count -gt 0) {
    # 允许首次推送成功后重复执行；其他已有历史需要单独处理。
    if ($remoteRefs.Count -ne 1 -or
        $remoteRefs[0] -notmatch ('^' + [regex]::Escape($sha) + '\s+refs/heads/main$')) {
        throw '远端不是空库，也不是同一提交的 main；停止，不强推或覆盖远端历史。'
    }
}

Invoke-GitChecked push --set-upstream origin main
Invoke-GhChecked repo edit $repo --default-branch main

$repository = Invoke-GitHubApi "repos/$repo"
$remoteMain = Invoke-GitHubApi "repos/$repo/branches/main"
if ($repository.full_name -ne $repo -or $repository.default_branch -ne 'main' -or
    $remoteMain.name -ne 'main' -or $remoteMain.commit.sha -ne $sha) {
    throw '远端仓库、默认分支或 main 提交与本地不一致。'
}
```

首次 push 会自动触发 CI。下一段另行派发一轮，便于明确选择本次手动运行。

## 3. 派发并等待 CI

```powershell
$workflow = Invoke-GitHubApi "repos/$repo/actions/workflows/ci.yml"
if ($workflow.path -ne '.github/workflows/ci.yml' -or $workflow.state -ne 'active') {
    throw 'ci.yml 未注册或未启用；先在 GitHub Actions 页面检查。'
}

Invoke-GhChecked workflow run ci.yml --repo $repo --ref main
Invoke-GhChecked run list --repo $repo --workflow ci.yml --branch main --commit $sha --event workflow_dispatch --limit 10

$runId = Read-Host '输入刚派发的 workflow_dispatch 运行的 ID（列表中的 ID 列）'
if ($runId -notmatch '^\d+$') { throw '运行 ID 必须是数字。' }

$run = Invoke-GitHubApi "repos/$repo/actions/runs/$runId"
if ($run.repository.full_name -ne $repo -or $run.head_branch -ne 'main' -or
    $run.head_sha -ne $sha -or $run.workflow_id -ne $workflow.id -or
    $run.event -ne 'workflow_dispatch') {
    throw '选择的运行不属于目标仓库、main、ci.yml 和本地提交。'
}

gh run watch $runId --repo $repo --exit-status
if ($LASTEXITCODE -ne 0) {
    throw 'CI 未成功；先查看失败日志并修复，不继续添加必需检查。'
}

$run = Invoke-GitHubApi "repos/$repo/actions/runs/$runId"
if ($run.status -ne 'completed' -or $run.conclusion -ne 'success' -or
    $run.head_sha -ne $sha -or $run.workflow_id -ne $workflow.id) {
    throw 'API 回读未确认这次 CI 成功。'
}
```

若派发后列表暂未显示新运行，重新执行 `run list`，再输入 ID。若失败，可执行 `gh run view $runId --repo $repo --log-failed` 查看日志，检查命令退出码后再处理；不要把失败或取消当成成功继续。

SDK 安装与 NuGet 还原需要网络。此 CI 是无真实云端依赖的合成测试，不是实际授权传输或安装/升级验收。

## 4. 从该次运行取得真实检查名称和 App ID

不手填 workflow 名，也不猜 GitHub Actions 的 App ID。下面沿本次运行的作业读取 `check_run_url`，再确认对应的 check-run。

```powershell
$jobs = Invoke-GitHubApi "repos/$repo/actions/runs/$runId/jobs?filter=latest&per_page=100"
$ciJobs = @($jobs.jobs | Where-Object {
    $_.name -eq 'Windows / .NET 10 / synthetic tests' -and
    $_.run_id -eq $runId -and $_.head_sha -eq $sha -and
    $_.conclusion -eq 'success'
})
if ($ciJobs.Count -ne 1) { throw '未找到唯一成功的目标 CI 作业。' }

$checkUri = [uri]$ciJobs[0].check_run_url
if ($checkUri.Scheme -ne 'https' -or $checkUri.Host -ne 'api.github.com' -or
    -not $checkUri.AbsolutePath.StartsWith("/repos/$repo/check-runs/", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'API 返回的 check-run 地址不属于目标仓库。'
}
$check = Invoke-GitHubApi $checkUri.AbsoluteUri
if ($check.name -ne $ciJobs[0].name -or $check.head_sha -ne $sha -or
    $check.status -ne 'completed' -or $check.conclusion -ne 'success' -or
    $check.check_suite.id -ne $run.check_suite_id -or
    $check.app.slug -ne 'github-actions' -or [long]$check.app.id -le 0) {
    throw '检查名称、提交、check suite、结果或提供方未通过核对。'
}

$check | Select-Object name, conclusion, head_sha,
    @{Name='appId'; Expression={$_.app.id}}
```

## 5. 启用私密漏洞报告和 main 保护

下面先再次核对本地与远端 `main` 仍是刚通过 CI 的提交，再配置 PVR 和经典分支保护。批准人数为 0；CI 严格要求分支基于最新 main，管理员也遵守保护，禁止强推和删除。

```powershell
$branch = (Invoke-GitChecked branch --show-current | Out-String).Trim()
$currentSha = (Invoke-GitChecked rev-parse refs/heads/main | Out-String).Trim()
$repository = Invoke-GitHubApi "repos/$repo"
$remoteMain = Invoke-GitHubApi "repos/$repo/branches/main"
if ($branch -ne 'main' -or $currentSha -ne $sha -or
    $repository.full_name -ne $repo -or $repository.visibility -ne 'public' -or
    $repository.default_branch -ne 'main' -or -not $repository.permissions.admin -or
    $remoteMain.commit.sha -ne $sha -or $check.head_sha -ne $sha) {
    throw 'main 或仓库状态已经改变；重新核对并运行 CI 后再配置保护。'
}

Invoke-GitHubApi "repos/$repo/private-vulnerability-reporting" -Method PUT
$pvr = Invoke-GitHubApi "repos/$repo/private-vulnerability-reporting"
if ($pvr.enabled -ne $true) { throw '私密漏洞报告未确认启用。' }

$protectionBody = @{
    required_status_checks = @{
        strict = $true
        contexts = @()
        checks = @(@{ context = [string]$check.name; app_id = [long]$check.app.id })
    }
    enforce_admins = $true
    required_pull_request_reviews = @{
        required_approving_review_count = 0
        dismiss_stale_reviews = $false
        require_code_owner_reviews = $false
        require_last_push_approval = $false
    }
    restrictions = $null
    allow_force_pushes = $false
    allow_deletions = $false
    required_conversation_resolution = $true
}
$json = $protectionBody | ConvertTo-Json -Depth 8
$null = Invoke-GitHubApi "repos/$repo/branches/main/protection" -Method PUT -Json $json

$protection = Invoke-GitHubApi "repos/$repo/branches/main/protection"
$required = @($protection.required_status_checks.checks)
if ($protection.required_status_checks.strict -ne $true -or
    $protection.enforce_admins.enabled -ne $true -or
    $required.Count -ne 1 -or $required[0].context -ne $check.name -or
    [long]$required[0].app_id -ne [long]$check.app.id -or
    $null -eq $protection.required_pull_request_reviews -or
    $protection.required_pull_request_reviews.required_approving_review_count -ne 0 -or
    $protection.required_pull_request_reviews.require_code_owner_reviews -ne $false -or
    $protection.required_pull_request_reviews.require_last_push_approval -ne $false -or
    $protection.allow_force_pushes.enabled -ne $false -or
    $protection.allow_deletions.enabled -ne $false -or
    $protection.required_conversation_resolution.enabled -ne $true) {
    throw '保护规则回读与预期不一致，请检查 GitHub 仓库设置。'
}

[pscustomobject]@{
    Repository = $repo
    Commit = $sha
    SuccessfulRun = $runId
    RequiredCheck = $required[0].context
    CheckAppId = $required[0].app_id
    StrictChecks = $protection.required_status_checks.strict
    RequiredApprovals = $protection.required_pull_request_reviews.required_approving_review_count
    ForcePushAllowed = $protection.allow_force_pushes.enabled
    DeletionAllowed = $protection.allow_deletions.enabled
    PrivateVulnerabilityReporting = $pvr.enabled
}
```

GitHub Free 的公共仓库支持这些保护和 PVR。若 API 返回权限错误或产品限制，脚本会停止；先处理账户权限或仓库可见性，不要以忽略退出码或填入未知 status 绕过。完成后可在仓库的 Settings → Branches、Security → Advisories 再查看设置。

## 官方参考

- [GitHub CLI 安装](https://github.com/cli/cli#installation)、[网页登录与 scopes](https://cli.github.com/manual/gh_auth_login)、[Git 凭据助手](https://cli.github.com/manual/gh_auth_setup-git)。
- [派发 workflow](https://cli.github.com/manual/gh_workflow_run)、[按提交列出运行](https://cli.github.com/manual/gh_run_list)、[等待运行并返回失败码](https://cli.github.com/manual/gh_run_watch)。
- [gh api 的 JSON 输入与退出行为](https://cli.github.com/manual/gh_api)、[运行 API](https://docs.github.com/en/rest/actions/workflow-runs)、[作业 API](https://docs.github.com/en/rest/actions/workflow-jobs)、[check-run API](https://docs.github.com/en/rest/checks/runs)。
- [经典分支保护 API](https://docs.github.com/en/rest/branches/branch-protection#update-branch-protection)、[必需检查规则](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks)。
- [PVR API](https://docs.github.com/en/rest/repos/repos#enable-private-vulnerability-reporting-for-a-repository)、[PVR 设置说明](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)。
