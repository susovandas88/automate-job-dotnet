# Deploys the same image as one Container Apps replica.
# Requires: az login, and compose.env with Auth__JwtKey, Auth__PasswordHash, Auth__Username.
# Optional in compose.env: GEMINI_API_KEY, LANGSMITH_API_KEY.
# Usage: ./deploy/azure-containerapp.ps1 -ResourceGroup automate-job -Location centralindia -Name automatejob

param(
    [string]$ResourceGroup = "automate-job",
    [string]$Location = "centralindia",
    [string]$Name = "automatejob",
    [string]$EnvFile = "compose.env"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $EnvFile)) {
    throw "Create $EnvFile from compose.env.example before deploying."
}

$envValues = @{}
Get-Content $EnvFile | ForEach-Object {
    $line = $_.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith("#") -or -not $line.Contains("=")) { return }
    $split = $line.IndexOf("=")
    $key = $line.Substring(0, $split).Trim()
    $value = $line.Substring($split + 1).Trim().Trim('"').Trim("'")
    if ($key.Length -gt 0) { $envValues[$key] = $value }
}

foreach ($required in @("Auth__JwtKey", "Auth__PasswordHash", "Auth__Username")) {
    if (-not $envValues.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($envValues[$required])) {
        throw "$EnvFile is missing $required."
    }
}

az group create --name $ResourceGroup --location $Location
az acr create --resource-group $ResourceGroup --name $Name --sku Basic --admin-enabled true
az acr build --registry $Name --image "${Name}:latest" .
$acrServer = az acr show --name $Name --query loginServer -o tsv
$acrUser = az acr credential show --name $Name --query username -o tsv
$acrPassword = az acr credential show --name $Name --query "passwords[0].value" -o tsv

$storage = "${Name}data"
az storage account create --name $storage --resource-group $ResourceGroup --location $Location --sku Standard_LRS --kind StorageV2
$storageKey = az storage account keys list --account-name $storage --query "[0].value" -o tsv
az storage share create --name appdata --account-name $storage --account-key $storageKey

az containerapp env create --name "${Name}-env" --resource-group $ResourceGroup --location $Location
az containerapp env storage set `
    --name "${Name}-env" `
    --resource-group $ResourceGroup `
    --storage-name appdata `
    --azure-file-account-name $storage `
    --azure-file-account-key $storageKey `
    --azure-file-share-name appdata `
    --access-mode ReadWrite

function Quote-Yaml([string]$value) {
    return '"' + ($value -replace '"', '') + '"'
}

$yaml = Join-Path $env:TEMP "automate-job-containerapp.yaml"
$secretLines = @(
    "      - name: acr-password",
    "        value: $(Quote-Yaml $acrPassword)",
    "      - name: jwt-key",
    "        value: $(Quote-Yaml $envValues['Auth__JwtKey'])",
    "      - name: password-hash",
    "        value: $(Quote-Yaml $envValues['Auth__PasswordHash'])",
    "      - name: username",
    "        value: $(Quote-Yaml $envValues['Auth__Username'])"
)
$envLines = @(
    "        - name: DATA_DIR",
    "          value: /data",
    "        - name: HEADLESS",
    "          value: ""true""",
    "        - name: PYTHON",
    "          value: /opt/agent-venv/bin/python",
    "        - name: CHROME_PATH",
    "          value: /usr/local/bin/headless-chromium",
    "        - name: OPENAI_ENABLED",
    "          value: ""true""",
    "        - name: Auth__JwtKey",
    "          secretRef: jwt-key",
    "        - name: Auth__PasswordHash",
    "          secretRef: password-hash",
    "        - name: Auth__Username",
    "          secretRef: username"
)
if ($envValues.ContainsKey("GEMINI_API_KEY") -and -not [string]::IsNullOrWhiteSpace($envValues["GEMINI_API_KEY"])) {
    $secretLines += @("      - name: gemini-key", "        value: $(Quote-Yaml $envValues['GEMINI_API_KEY'])")
    $envLines += @("        - name: GEMINI_API_KEY", "          secretRef: gemini-key")
}
if ($envValues.ContainsKey("LANGSMITH_API_KEY") -and -not [string]::IsNullOrWhiteSpace($envValues["LANGSMITH_API_KEY"])) {
    $secretLines += @("      - name: langsmith-key", "        value: $(Quote-Yaml $envValues['LANGSMITH_API_KEY'])")
    $envLines += @("        - name: LANGSMITH_API_KEY", "          secretRef: langsmith-key")
}

@(
    "location: $Location",
    "identity:",
    "  type: None",
    "properties:",
    "  environmentId: $(az containerapp env show --name "${Name}-env" --resource-group $ResourceGroup --query id -o tsv)",
    "  configuration:",
    "    activeRevisionsMode: single",
    "    ingress:",
    "      external: true",
    "      targetPort: 8080",
    "      transport: http",
    "    registries:",
    "      - server: $acrServer",
    "        username: $acrUser",
    "        passwordSecretRef: acr-password",
    "    secrets:",
    ($secretLines -join "`n"),
    "  template:",
    "    containers:",
    "      - name: app",
    "        image: ${acrServer}/${Name}:latest",
    "        resources:",
    "          cpu: 2",
    "          memory: 4Gi",
    "        env:",
    ($envLines -join "`n"),
    "        volumeMounts:",
    "          - volumeName: appdata",
    "            mountPath: /data",
    "    scale:",
    "      minReplicas: 1",
    "      maxReplicas: 1",
    "    volumes:",
    "      - name: appdata",
    "        storageName: appdata",
    "        storageType: AzureFile"
) | Set-Content -Encoding utf8 $yaml

az containerapp create --name $Name --resource-group $ResourceGroup --yaml $yaml
Remove-Item $yaml
$fqdn = az containerapp show --name $Name --resource-group $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv
Write-Output "Container app is up at https://$fqdn"
